using System;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Represents a decoded slice header containing all information 
    /// needed to decode the slice data.
    /// </summary>
    public sealed class SliceHeader
    {
        /// <summary>First macroblock address in this slice.</summary>
        public uint FirstMacroblockAddress { get; set; }

        /// <summary>Slice type (I, P, B, SI, SP).</summary>
        public SliceType Type { get; set; }

        /// <summary>Picture parameter set ID referenced by this slice.</summary>
        public byte PictureParameterSetId { get; set; }

        /// <summary>Color plane ID for separate color plane coding.</summary>
        public byte ColorPlaneId { get; set; }

        /// <summary>Frame number for reference picture management.</summary>
        public ushort FrameNumber { get; set; }

        /// <summary>True if this slice is from a field (interlaced).</summary>
        public bool FieldPicture { get; set; }

        /// <summary>True if bottom field (when FieldPicture is true).</summary>
        public bool BottomField { get; set; }

        /// <summary>IDR picture ID for IDR slices.</summary>
        public ushort IdrPictureId { get; set; }

        /// <summary>Picture order count LSB (for POC type 0).</summary>
        public uint PictureOrderCountLsb { get; set; }

        /// <summary>Delta POC bottom (for POC type 0 with field pictures).</summary>
        public int DeltaPicOrderCountBottom { get; set; }

        /// <summary>Delta POC values (for POC type 1).</summary>
        public int DeltaPictureOrderCount0 { get; set; }
        public int DeltaPictureOrderCount1 { get; set; }

        /// <summary>Redundant picture count.</summary>
        public uint RedundantPictureCount { get; set; }

        /// <summary>Direct spatial MV prediction flag for B slices.</summary>
        public bool DirectSpatialMvPrediction { get; set; }

        /// <summary>Number of active L0 references override flag.</summary>
        public bool NumRefIdxActiveOverride { get; set; }

        /// <summary>Number of active L0 reference pictures.</summary>
        public int NumRefIdxL0ActiveMinus1 { get; set; }

        /// <summary>Number of active L1 reference pictures.</summary>
        public int NumRefIdxL1ActiveMinus1 { get; set; }

        /// <summary>Reference picture list modification for L0.</summary>
        public bool RefPicListModificationL0 { get; set; }

        /// <summary>Reference picture list modification for L1.</summary>
        public bool RefPicListModificationL1 { get; set; }

        // RPLM (Reference Picture List Modification) commands per list
        public int RplmL0Count { get; set; }
        public int RplmL1Count { get; set; }
        public readonly uint[] RplmL0Idc = new uint[32];
        public readonly uint[] RplmL0Value = new uint[32];
        public readonly uint[] RplmL1Idc = new uint[32];
        public readonly uint[] RplmL1Value = new uint[32];

        /// <summary>Luma log2 weight denom for weighted prediction.</summary>
        public uint LumaLog2WeightDenom { get; set; }

        /// <summary>Chroma log2 weight denom for weighted prediction.</summary>
        public uint ChromaLog2WeightDenom { get; set; }

        /// <summary>Whether weighted prediction is active for this slice.</summary>
        public bool HasWeightedPred { get; set; }

        // Weighted prediction tables (indexed by ref_idx, max 16 refs)
        public int[] LumaWeightL0 { get; } = new int[16];
        public int[] LumaOffsetL0 { get; } = new int[16];
        public int[] LumaWeightL1 { get; } = new int[16];
        public int[] LumaOffsetL1 { get; } = new int[16];
        public int[,] ChromaWeightL0 { get; } = new int[16, 2];
        public int[,] ChromaOffsetL0 { get; } = new int[16, 2];
        public int[,] ChromaWeightL1 { get; } = new int[16, 2];
        public int[,] ChromaOffsetL1 { get; } = new int[16, 2];

        /// <summary>No output of prior pictures flag (IDR only).</summary>
        public bool NoOutputOfPriorPics { get; set; }

        /// <summary>Long term reference flag (IDR only).</summary>
        public bool LongTermReference { get; set; }

        /// <summary>Adaptive reference picture marking mode.</summary>
        public bool AdaptiveRefPicMarkingMode { get; set; }

        /// <summary>MMCO commands for adaptive reference picture marking (spec 8.2.5.4).</summary>
        public MmcoCommand[] MmcoCommands { get; set; } = Array.Empty<MmcoCommand>();
        public int MmcoCommandCount { get; set; }

        /// <summary>CABAC initialization IDC (0-2).</summary>
        public uint CabacInitIdc { get; set; }

        /// <summary>Slice QP delta.</summary>
        public int SliceQpDelta { get; set; }

        /// <summary>SP for switch flag.</summary>
        public bool SpForSwitch { get; set; }

        /// <summary>Slice QS delta (for SI/SP slices).</summary>
        public int SliceQsDelta { get; set; }

        /// <summary>Disable deblocking filter IDC (0=enabled, 1=disabled, 2=disabled at slice boundary).</summary>
        public uint DisableDeblockingFilterIdc { get; set; }

        /// <summary>Slice alpha C0 offset (deblocking strength).</summary>
        public int SliceAlphaC0OffsetDiv2 { get; set; }

        /// <summary>Slice beta offset (deblocking strength).</summary>
        public int SliceBetaOffsetDiv2 { get; set; }

        /// <summary>Slice group change cycle.</summary>
        public uint SliceGroupChangeCycle { get; set; }

        /// <summary>NAL reference IDC (from NAL header, indicates if used for reference).</summary>
        public byte NalReferenceIdc { get; set; }
        
        /// <summary>Bit position in RBSP where slice data begins (after header).</summary>
        public int SliceDataBitOffset { get; set; }

        /// <summary>Gets the effective slice QP value (H.264 spec SliceQPY, without bit-depth offset).</summary>
        /// <param name="pps">The PPS containing initial QP.</param>

        /// <summary>Returns this header to the state a freshly constructed one would be in.</summary>
        /// <remarks>
        /// Headers are reused between slices, and the parser assigns many of these fields only when
        /// the bitstream carries them, relying otherwise on a new object's defaults - so reuse has to
        /// restore exactly that state or a stale value leaks into the next slice. This body is
        /// generated from the declarations above rather than written by hand; with fifty-one members
        /// one missed by eye would surface only on streams that omit that syntax element.
        /// The reference-list, weight and MMCO buffers are cleared or left in place rather than
        /// reallocated - that reuse is the point of the exercise. MmcoCommands is bounded by
        /// MmcoCommandCount, so leaving its contents stale past the count is safe.
        /// </remarks>
        public void Reset()
        {
            FirstMacroblockAddress = default;
            Type = default;
            PictureParameterSetId = default;
            ColorPlaneId = default;
            FrameNumber = default;
            FieldPicture = default;
            BottomField = default;
            IdrPictureId = default;
            PictureOrderCountLsb = default;
            DeltaPicOrderCountBottom = default;
            DeltaPictureOrderCount0 = default;
            DeltaPictureOrderCount1 = default;
            RedundantPictureCount = default;
            DirectSpatialMvPrediction = default;
            NumRefIdxActiveOverride = default;
            NumRefIdxL0ActiveMinus1 = default;
            NumRefIdxL1ActiveMinus1 = default;
            RefPicListModificationL0 = default;
            RefPicListModificationL1 = default;
            RplmL0Count = default;
            RplmL1Count = default;
            LumaLog2WeightDenom = default;
            ChromaLog2WeightDenom = default;
            HasWeightedPred = default;
            Array.Clear(LumaWeightL0);
            Array.Clear(LumaOffsetL0);
            Array.Clear(LumaWeightL1);
            Array.Clear(LumaOffsetL1);
            Array.Clear(ChromaWeightL0);
            Array.Clear(ChromaOffsetL0);
            Array.Clear(ChromaWeightL1);
            Array.Clear(ChromaOffsetL1);
            NoOutputOfPriorPics = default;
            LongTermReference = default;
            AdaptiveRefPicMarkingMode = default;
            MmcoCommandCount = default;
            CabacInitIdc = default;
            SliceQpDelta = default;
            SpForSwitch = default;
            SliceQsDelta = default;
            DisableDeblockingFilterIdc = default;
            SliceAlphaC0OffsetDiv2 = default;
            SliceBetaOffsetDiv2 = default;
            SliceGroupChangeCycle = default;
            NalReferenceIdc = default;
            SliceDataBitOffset = default;
            Array.Clear(RplmL0Idc);
            Array.Clear(RplmL0Value);
            Array.Clear(RplmL1Idc);
            Array.Clear(RplmL1Value);
            MmcoCommandCount = 0;
        }
        /// <summary>SliceQPY (7.4.3), bounded to the range the standard allows it.</summary>
        /// <param name="qpBdOffset">
        /// QpBdOffsetY, which is how far below zero SliceQPY may legally sit. Left at zero the
        /// result is bounded to 0..51, which is also what CABAC context initialisation wants.
        /// </param>
        /// <remarks>
        /// Both inputs come from exp-Golomb codes and neither is checked against the standard's
        /// range, so a malformed stream can produce any value at all. Everything downstream
        /// indexes a table with this: the dequantisation matrices by <c>qp / 6</c> and
        /// <c>qp % 6</c>, and the deblocking filter's thresholds by the quantiser plus a slice
        /// offset. Bounding it once here is what lets those lookups be plain reads rather than
        /// each carrying its own clamp, and it closes the same hole for dequantisation, which
        /// never had one.
        /// </remarks>
        public int GetSliceQp(PictureParameterSet pps, int qpBdOffset = 0)
        {
            return Math.Clamp(26 + pps.PictureInitQpMinus26 + SliceQpDelta, -qpBdOffset, 51);
        }

        /// <summary>Returns true if this is an intra slice (I or SI).</summary>
        public bool IsIntraSlice => 
            Type == SliceType.ISlice || Type == SliceType.ISliceAll ||
            Type == SliceType.SiSlice || Type == SliceType.SiSliceAll;

        /// <summary>Returns true if this is a P slice.</summary>
        public bool IsPSlice =>
            Type == SliceType.PSlice || Type == SliceType.PSliceAll ||
            Type == SliceType.SpSlice || Type == SliceType.SpSliceAll;

        /// <summary>Returns true if this is a B slice.</summary>
        public bool IsBSlice =>
            Type == SliceType.BSlice || Type == SliceType.BSliceAll;
    }

    /// <summary>
    /// Parser for H.264 slice headers.
    /// </summary>
    public static class SliceHeaderParser
    {
        /// <summary>
        /// Parses a slice header from NAL unit data.
        /// </summary>
        /// <param name="nalPayload">NAL unit payload (after removing type byte and emulation bytes).</param>
        /// <param name="nalType">NAL unit type.</param>
        /// <param name="sps">Active SPS (from PPS reference).</param>
        /// <param name="pps">Active PPS (from slice header reference).</param>
        /// <returns>Parsed slice header, or null if parsing fails.</returns>
        public static SliceHeader? ParseSliceHeader(
            ReadOnlySpan<byte> nalPayload,
            NalUnitType nalType,
            byte nalRefIdc,
            SequenceParameterSet sps,
            PictureParameterSet pps)
        {
            if (nalPayload.Length == 0 || sps == null || pps == null)
                return null;

            return ParseSliceHeaderRbsp(
                NalUnitParser.RemoveEmulationPreventionBytes(nalPayload), nalType, nalRefIdc, sps, pps);
        }

        /// <summary>
        /// Parses a slice header from data that has already had its emulation prevention bytes
        /// removed, so the decoder can unescape a slice once and use the result for both the header
        /// and the slice data.
        /// </summary>
        public static SliceHeader? ParseSliceHeaderRbsp(
            ReadOnlySpan<byte> rbsp,
            NalUnitType nalType,
            byte nalRefIdc,
            SequenceParameterSet sps,
            PictureParameterSet pps,
            SliceHeader? reuse = null)
        {
            if (rbsp.Length == 0 || sps == null || pps == null)
                return null;

            var reader = new BitstreamReader(rbsp);

            try
            {
                SliceHeader header;
                if (reuse != null)
                {
                    reuse.Reset();
                    header = reuse;
                }
                else
                {
                    header = new SliceHeader();
                }

                // First macroblock in slice
                header.FirstMacroblockAddress = reader.ReadExpGolombUnsigned();

                // Slice type
                uint sliceTypeCode = reader.ReadExpGolombUnsigned();
                header.Type = (SliceType)(sliceTypeCode % 5);

                // PPS ID
                header.PictureParameterSetId = (byte)reader.ReadExpGolombUnsigned();

                // Separate color plane
                if (sps.SeparateColorPlane)
                {
                    header.ColorPlaneId = (byte)reader.ReadBits(2);
                }

                // Frame number
                int frameNumBits = sps.Log2MaxFrameNumber;
                header.FrameNumber = (ushort)reader.ReadBits(frameNumBits);

                // Field/frame coding
                if (!sps.FrameMacroblockOnly)
                {
                    header.FieldPicture = reader.ReadBit() == 1;
                    if (header.FieldPicture)
                    {
                        header.BottomField = reader.ReadBit() == 1;
                    }
                }

                // IDR picture ID
                bool isIdr = nalType == NalUnitType.CodedSliceIdr;
                if (isIdr)
                {
                    header.IdrPictureId = (ushort)reader.ReadExpGolombUnsigned();
                }

                // Picture order count
                if (sps.PictureOrderCountType == 0)
                {
                    int pocLsbBits = sps.Log2MaxPictureOrderCountLsb;
                    header.PictureOrderCountLsb = reader.ReadBits(pocLsbBits);

                    if (pps.BottomFieldPicOrderPresent && !header.FieldPicture)
                    {
                        header.DeltaPicOrderCountBottom = reader.ReadExpGolombSigned();
                    }
                }
                else if (sps.PictureOrderCountType == 1 && !sps.DeltaPocAlwaysZero)
                {
                    header.DeltaPictureOrderCount0 = reader.ReadExpGolombSigned();
                    if (pps.BottomFieldPicOrderPresent && !header.FieldPicture)
                    {
                        header.DeltaPictureOrderCount1 = reader.ReadExpGolombSigned();
                    }
                }

                // Redundant pic count
                if (pps.RedundantPictureCountPresent)
                {
                    header.RedundantPictureCount = reader.ReadExpGolombUnsigned();
                }

                // Direct prediction (B slices)
                if (header.IsBSlice)
                {
                    header.DirectSpatialMvPrediction = reader.ReadBit() == 1;
                }

                // Reference picture count override
                if (header.IsPSlice || header.IsBSlice)
                {
                    header.NumRefIdxActiveOverride = reader.ReadBit() == 1;
                    if (header.NumRefIdxActiveOverride)
                    {
                        header.NumRefIdxL0ActiveMinus1 = (int)reader.ReadExpGolombUnsigned();
                        if (header.IsBSlice)
                        {
                            header.NumRefIdxL1ActiveMinus1 = (int)reader.ReadExpGolombUnsigned();
                        }
                    }
                    else
                    {
                        header.NumRefIdxL0ActiveMinus1 = pps.NumRefIdxL0DefaultActiveMinus1;
                        header.NumRefIdxL1ActiveMinus1 = pps.NumRefIdxL1DefaultActiveMinus1;
                    }
                }

                // Reference picture list reordering
                if (!header.IsIntraSlice)
                {
                    header.RefPicListModificationL0 = reader.ReadBit() == 1;
                    if (header.RefPicListModificationL0)
                    {
                        header.RplmL0Count = ParseRefPicListModification(ref reader, header.RplmL0Idc, header.RplmL0Value);
                    }
                }
                if (header.IsBSlice)
                {
                    header.RefPicListModificationL1 = reader.ReadBit() == 1;
                    if (header.RefPicListModificationL1)
                    {
                        header.RplmL1Count = ParseRefPicListModification(ref reader, header.RplmL1Idc, header.RplmL1Value);
                    }
                }

                // Weighted prediction
                if ((pps.WeightedPrediction && header.IsPSlice) ||
                    (pps.WeightedBiprediction == 1 && header.IsBSlice))
                {
                    ParseWeightedPredTable(ref reader, header, sps.ChromaFormat);
                }

                // Reference picture marking (only for reference pictures)
                if (nalRefIdc != 0)
                {
                    if (isIdr)
                    {
                        header.NoOutputOfPriorPics = reader.ReadBit() == 1;
                        header.LongTermReference = reader.ReadBit() == 1;
                    }
                    else
                    {
                        header.AdaptiveRefPicMarkingMode = reader.ReadBit() == 1;
                        if (header.AdaptiveRefPicMarkingMode)
                        {
                            ParseDecRefPicMarking(ref reader, header);
                        }
                    }
                }

                // CABAC init
                if (pps.EntropyCodingMode && !header.IsIntraSlice)
                {
                    header.CabacInitIdc = reader.ReadExpGolombUnsigned();
                }

                // Slice QP delta
                header.SliceQpDelta = reader.ReadExpGolombSigned();

                // SP/SI slices
                if (header.Type == SliceType.SpSlice || header.Type == SliceType.SpSliceAll)
                {
                    if (header.Type == SliceType.SpSlice)
                    {
                        header.SpForSwitch = reader.ReadBit() == 1;
                    }
                    header.SliceQsDelta = reader.ReadExpGolombSigned();
                }
                if (header.Type == SliceType.SiSlice || header.Type == SliceType.SiSliceAll)
                {
                    header.SliceQsDelta = reader.ReadExpGolombSigned();
                }

                // Deblocking filter control
                if (pps.DeblockingFilterControlPresent)
                {
                    header.DisableDeblockingFilterIdc = reader.ReadExpGolombUnsigned();
                    if (header.DisableDeblockingFilterIdc != 1)
                    {
                        // Both are constrained to -6..6 by the standard (7.4.3), and the
                        // deblocking filter relies on it: its threshold tables are padded just
                        // far enough that a legal quantiser plus a legal offset cannot leave
                        // them, which is what lets every lookup there drop its clamp. Bounding
                        // the value here keeps that an invariant of parsing rather than
                        // something each of the fifty-odd lookups has to re-establish.
                        //
                        // FFmpeg rejects the whole slice instead (h264_slice.c:1895). Clamping
                        // decodes a malformed stream where they would drop it, which is the
                        // right trade for a player; no conforming stream can tell the
                        // difference, since for those the value is already in range.
                        header.SliceAlphaC0OffsetDiv2 = Math.Clamp(reader.ReadExpGolombSigned(), -6, 6);
                        header.SliceBetaOffsetDiv2 = Math.Clamp(reader.ReadExpGolombSigned(), -6, 6);
                    }
                }

                // Slice group map (rarely used)
                if (pps.NumSliceGroupsMinus1 > 0)
                {
                    // Complex slice group handling - skip for now
                }

                // Store bit position where slice data begins
                header.SliceDataBitOffset = reader.BitPosition;

                return header;
            }
            catch
            {
                return null;
            }
        }

        private static int ParseRefPicListModification(ref BitstreamReader reader, uint[] idcArray, uint[] valueArray)
        {
            int count = 0;
            uint idc;
            do
            {
                idc = reader.ReadExpGolombUnsigned();
                if (idc == 0 || idc == 1 || idc == 2)
                {
                    uint val = reader.ReadExpGolombUnsigned();
                    if (count < idcArray.Length)
                    {
                        idcArray[count] = idc;
                        valueArray[count] = val;
                        count++;
                    }
                }
            } while (idc != 3);
            return count;
        }

        private static void ParseWeightedPredTable(ref BitstreamReader reader, SliceHeader header, ChromaFormat chromaFormat)
        {
            header.LumaLog2WeightDenom = reader.ReadExpGolombUnsigned();
            int defaultLumaWeight = 1 << (int)header.LumaLog2WeightDenom;
            
            int defaultChromaWeight = 0;
            if (chromaFormat != ChromaFormat.Monochrome)
            {
                header.ChromaLog2WeightDenom = reader.ReadExpGolombUnsigned();
                defaultChromaWeight = 1 << (int)header.ChromaLog2WeightDenom;
            }

            // The presence of a weight table does not mean any weighting happens. x264 emits one
            // for every P slice when weightp is on, and most of its entries are the identity:
            // weight = 1 << denom with offset 0, which computes ((p << d) + (1 << d-1)) >> d = p.
            // FFmpeg tracks this as use_weight (h264_parse.c:72) and takes the plain mc_part_std
            // path unless a weight is genuinely non-default. We were taking the weighted path for
            // the whole stream: a multiply, a shift, an add and two clamps per sample to arrive
            // back at the sample, and it also blocked predicting straight into the frame.
            bool anyWeightApplies = false;

            // L0 weights
            for (int i = 0; i <= header.NumRefIdxL0ActiveMinus1; i++)
            {
                bool lumaWeightFlag = reader.ReadBit() == 1;
                if (lumaWeightFlag)
                {
                    header.LumaWeightL0[i] = reader.ReadExpGolombSigned();
                    header.LumaOffsetL0[i] = reader.ReadExpGolombSigned();
                    anyWeightApplies |= header.LumaWeightL0[i] != defaultLumaWeight || header.LumaOffsetL0[i] != 0;
                }
                else
                {
                    header.LumaWeightL0[i] = defaultLumaWeight;
                    header.LumaOffsetL0[i] = 0;
                }

                if (chromaFormat != ChromaFormat.Monochrome)
                {
                    bool chromaWeightFlag = reader.ReadBit() == 1;
                    if (chromaWeightFlag)
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            header.ChromaWeightL0[i, j] = reader.ReadExpGolombSigned();
                            header.ChromaOffsetL0[i, j] = reader.ReadExpGolombSigned();
                            anyWeightApplies |= header.ChromaWeightL0[i, j] != defaultChromaWeight || header.ChromaOffsetL0[i, j] != 0;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < 2; j++)
                        {
                            header.ChromaWeightL0[i, j] = defaultChromaWeight;
                            header.ChromaOffsetL0[i, j] = 0;
                        }
                    }
                }
            }

            // L1 weights for B slices
            if (header.IsBSlice)
            {
                for (int i = 0; i <= header.NumRefIdxL1ActiveMinus1; i++)
                {
                    bool lumaWeightFlag = reader.ReadBit() == 1;
                    if (lumaWeightFlag)
                    {
                        header.LumaWeightL1[i] = reader.ReadExpGolombSigned();
                        header.LumaOffsetL1[i] = reader.ReadExpGolombSigned();
                        anyWeightApplies |= header.LumaWeightL1[i] != defaultLumaWeight || header.LumaOffsetL1[i] != 0;
                    }
                    else
                    {
                        header.LumaWeightL1[i] = defaultLumaWeight;
                        header.LumaOffsetL1[i] = 0;
                    }

                    if (chromaFormat != ChromaFormat.Monochrome)
                    {
                        bool chromaWeightFlag = reader.ReadBit() == 1;
                        if (chromaWeightFlag)
                        {
                            for (int j = 0; j < 2; j++)
                            {
                                header.ChromaWeightL1[i, j] = reader.ReadExpGolombSigned();
                                header.ChromaOffsetL1[i, j] = reader.ReadExpGolombSigned();
                                anyWeightApplies |= header.ChromaWeightL1[i, j] != defaultChromaWeight || header.ChromaOffsetL1[i, j] != 0;
                            }
                        }
                        else
                        {
                            for (int j = 0; j < 2; j++)
                            {
                                header.ChromaWeightL1[i, j] = defaultChromaWeight;
                                header.ChromaOffsetL1[i, j] = 0;
                            }
                        }
                    }
                }
            }

            header.HasWeightedPred = anyWeightApplies;
        }

        private static void ParseDecRefPicMarking(ref BitstreamReader reader, SliceHeader header)
        {
            Scratch.Of16<MmcoCommand> commandsStorage = default;
            Span<MmcoCommand> commands = commandsStorage;
            int count = 0;

            uint operation;
            do
            {
                operation = reader.ReadExpGolombUnsigned();
                if (operation == 0)
                    break;

                var cmd = new MmcoCommand { Operation = (byte)operation };

                if (operation == 1 || operation == 3)
                    cmd.DifferenceOfPicNumsMinus1 = reader.ReadExpGolombUnsigned();
                if (operation == 2)
                    cmd.LongTermPicNum = reader.ReadExpGolombUnsigned();
                if (operation == 3 || operation == 6)
                    cmd.LongTermFrameIdx = reader.ReadExpGolombUnsigned();
                if (operation == 4)
                    cmd.MaxLongTermFrameIdxPlus1 = reader.ReadExpGolombUnsigned();

                if (count < commands.Length)
                    commands[count++] = cmd;
            } while (true);

            // Grown in place: readers bound by MmcoCommandCount, so a longer buffer is fine.
            if (header.MmcoCommands.Length < count)
                header.MmcoCommands = new MmcoCommand[count];
            commands.Slice(0, count).CopyTo(header.MmcoCommands);
            header.MmcoCommandCount = count;
        }
    }

    /// <summary>Memory management control operation (H.264 spec 8.2.5.4).</summary>
    public struct MmcoCommand
    {
        /// <summary>MMCO operation type (1-6).</summary>
        public byte Operation;
        /// <summary>difference_of_pic_nums_minus1 for ops 1, 3.</summary>
        public uint DifferenceOfPicNumsMinus1;
        /// <summary>long_term_pic_num for op 2.</summary>
        public uint LongTermPicNum;
        /// <summary>long_term_frame_idx for ops 3, 6.</summary>
        public uint LongTermFrameIdx;
        /// <summary>max_long_term_frame_idx_plus1 for op 4.</summary>
        public uint MaxLongTermFrameIdxPlus1;
    }
}

using System;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Video Usability Information (VUI) parameters parsed from SPS.
    /// Contains timing, color space, and display information.
    /// </summary>
    public sealed class VideoUsabilityInfo
    {
        /// <summary>Aspect ratio information is present.</summary>
        public bool AspectRatioInfoPresent { get; set; }

        /// <summary>Aspect ratio IDC (see Table E-1).</summary>
        public byte AspectRatioIdc { get; set; }

        /// <summary>Sample aspect ratio width (when AspectRatioIdc == 255).</summary>
        public ushort SampleAspectRatioWidth { get; set; }

        /// <summary>Sample aspect ratio height (when AspectRatioIdc == 255).</summary>
        public ushort SampleAspectRatioHeight { get; set; }

        /// <summary>Overscan information is present.</summary>
        public bool OverscanInfoPresent { get; set; }

        /// <summary>Overscan is appropriate for display.</summary>
        public bool OverscanAppropriate { get; set; }

        /// <summary>Video signal type information is present.</summary>
        public bool VideoSignalTypePresent { get; set; }

        /// <summary>Video format (0-7, see Table E-2).</summary>
        public byte VideoFormat { get; set; }

        /// <summary>Video uses full range (0-255 vs 16-235).</summary>
        public bool VideoFullRange { get; set; }

        /// <summary>Color description is present.</summary>
        public bool ColorDescriptionPresent { get; set; }

        /// <summary>Color primaries (see Table E-3).</summary>
        public byte ColorPrimaries { get; set; }

        /// <summary>Transfer characteristics (see Table E-4).</summary>
        public byte TransferCharacteristics { get; set; }

        /// <summary>Matrix coefficients for YCbCr (see Table E-5).</summary>
        public byte MatrixCoefficients { get; set; }

        /// <summary>Chroma sample location information is present.</summary>
        public bool ChromaLocationInfoPresent { get; set; }

        /// <summary>Chroma sample location for top field.</summary>
        public uint ChromaSampleLocationTop { get; set; }

        /// <summary>Chroma sample location for bottom field.</summary>
        public uint ChromaSampleLocationBottom { get; set; }

        /// <summary>Timing information is present.</summary>
        public bool TimingInfoPresent { get; set; }

        /// <summary>Number of time units in one second tick.</summary>
        public uint NumUnitsInTick { get; set; }

        /// <summary>Number of time scale units per second.</summary>
        public uint TimeScale { get; set; }

        /// <summary>Each picture has fixed frame duration.</summary>
        public bool FixedFrameRate { get; set; }

        /// <summary>NAL HRD parameters are present.</summary>
        public bool NalHrdParametersPresent { get; set; }

        /// <summary>VCL HRD parameters are present.</summary>
        public bool VclHrdParametersPresent { get; set; }

        /// <summary>Low delay HRD.</summary>
        public bool LowDelayHrd { get; set; }

        /// <summary>Picture structure is present in SEI.</summary>
        public bool PictureStructPresent { get; set; }

        /// <summary>Bitstream restriction info is present.</summary>
        public bool BitstreamRestrictionPresent { get; set; }

        /// <summary>Motion vectors over picture boundaries.</summary>
        public bool MotionVectorsOverPicBoundaries { get; set; }

        /// <summary>Maximum bytes per picture.</summary>
        public uint MaxBytesPerPicDenom { get; set; }

        /// <summary>Maximum bits per macroblock.</summary>
        public uint MaxBitsPerMacroblockDenom { get; set; }

        /// <summary>Log2 of max horizontal motion vector length.</summary>
        public uint Log2MaxMvLengthHorizontal { get; set; }

        /// <summary>Log2 of max vertical motion vector length.</summary>
        public uint Log2MaxMvLengthVertical { get; set; }

        /// <summary>Maximum number of reordered frames.</summary>
        public uint MaxNumReorderFrames { get; set; }

        /// <summary>Maximum decoded picture buffer size in frames.</summary>
        public uint MaxDecFrameBuffering { get; set; }

        /// <summary>Calculates framerate from timing info.</summary>
        public double CalculateFrameRate()
        {
            if (!TimingInfoPresent || NumUnitsInTick == 0)
                return 0;

            double fps = (double)TimeScale / (2.0 * NumUnitsInTick);
            return fps;
        }
    }

    /// <summary>
    /// Default scaling list matrices from the H.264 specification (Table 7-2).
    /// Stored in raster scan order for direct use in dequantization.
    /// </summary>
    internal static class ScalingListDefaults
    {
        // H.264 spec Table 7-2: default 4x4 intra
        public static readonly byte[] Default4x4Intra =
        {
             6, 13, 20, 28, 13, 20, 28, 32,
            20, 28, 32, 37, 28, 32, 37, 42
        };

        // H.264 spec Table 7-2: default 4x4 inter
        public static readonly byte[] Default4x4Inter =
        {
            10, 14, 20, 24, 14, 20, 24, 27,
            20, 24, 27, 30, 24, 27, 30, 34
        };

        // H.264 spec Table 7-2: default 8x8 intra
        public static readonly byte[] Default8x8Intra =
        {
             6, 10, 13, 16, 18, 23, 25, 27,
            10, 11, 16, 18, 23, 25, 27, 29,
            13, 16, 18, 23, 25, 27, 29, 31,
            16, 18, 23, 25, 27, 29, 31, 33,
            18, 23, 25, 27, 29, 31, 33, 36,
            23, 25, 27, 29, 31, 33, 36, 38,
            25, 27, 29, 31, 33, 36, 38, 40,
            27, 29, 31, 33, 36, 38, 40, 42
        };

        // H.264 spec Table 7-2: default 8x8 inter
        public static readonly byte[] Default8x8Inter =
        {
             9, 13, 15, 17, 19, 21, 22, 24,
            13, 13, 17, 19, 21, 22, 24, 25,
            15, 17, 19, 21, 22, 24, 25, 27,
            17, 19, 21, 22, 24, 25, 27, 28,
            19, 21, 22, 24, 25, 27, 28, 30,
            21, 22, 24, 25, 27, 28, 30, 32,
            22, 24, 25, 27, 28, 30, 32, 33,
            24, 25, 27, 28, 30, 32, 33, 35
        };

        // 4x4 zigzag scan order (raster position at each scan index)
        public static readonly byte[] ZigzagScan4x4 =
        {
            0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15
        };

        // 8x8 zigzag scan order (raster position at each scan index)
        public static readonly byte[] ZigzagScan8x8 =
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };
    }

    /// <summary>
    /// Sequence Parameter Set (SPS) for H.264.
    /// Contains video dimensions, profile/level, and decoding parameters.
    /// </summary>
    public sealed class SequenceParameterSet
    {
        /// <summary>SPS ID (0-31).</summary>
        public byte SequenceParameterSetId { get; set; }

        /// <summary>Profile IDC (identifies codec profile).</summary>
        public H264Profile Profile { get; set; }

        /// <summary>Constraint set flags.</summary>
        public byte ConstraintSetFlags { get; set; }

        /// <summary>Level IDC (e.g., 40 = level 4.0).</summary>
        public byte Level { get; set; }

        /// <summary>Chroma format (4:2:0, 4:2:2, 4:4:4, or monochrome).</summary>
        public ChromaFormat ChromaFormat { get; set; } = ChromaFormat.Chroma420;

        /// <summary>Separate color plane flag (for 4:4:4).</summary>
        public bool SeparateColorPlane { get; set; }

        /// <summary>Bit depth for luma samples (8-14).</summary>
        public int BitDepthLuma { get; set; } = 8;

        /// <summary>Bit depth for chroma samples (8-14).</summary>
        public int BitDepthChroma { get; set; } = 8;

        /// <summary>Transform bypass for lossless coding.</summary>
        public bool TransformBypass { get; set; }

        /// <summary>Scaling matrices are present.</summary>
        public bool ScalingMatrixPresent { get; set; }

        /// <summary>Bitmask tracking which scaling lists were explicitly signaled.</summary>
        public ushort ScalingMatrixPresentMask { get; set; }

        /// <summary>6x 4x4 scaling matrices in raster order. [0-2]=Intra Y/Cb/Cr, [3-5]=Inter Y/Cb/Cr.</summary>
        public byte[][] ScalingMatrix4 { get; set; } = CreateDefaultScaling4();

        /// <summary>6x 8x8 scaling matrices in raster order. [0-2]=Intra Y/Cb/Cr, [3-5]=Inter Y/Cb/Cr.</summary>
        public byte[][] ScalingMatrix8 { get; set; } = CreateDefaultScaling8();

        /// <summary>
        /// Creates flat-16 4x4 scaling matrices (the correct default when no scaling
        /// matrices are signaled in SPS/PPS). Table 7-3 non-flat defaults are only
        /// used through explicit signaling via Fallback Rule Set A in DecodeScalingList.
        /// </summary>
        internal static byte[][] CreateDefaultScaling4()
        {
            var m = new byte[6][];
            for (int i = 0; i < 6; i++)
            {
                m[i] = new byte[16];
                Array.Fill(m[i], (byte)16);
            }
            return m;
        }

        /// <summary>
        /// Creates flat-16 8x8 scaling matrices (the correct default when no scaling
        /// matrices are signaled in SPS/PPS).
        /// </summary>
        internal static byte[][] CreateDefaultScaling8()
        {
            var m = new byte[6][];
            for (int i = 0; i < 6; i++)
            {
                m[i] = new byte[64];
                Array.Fill(m[i], (byte)16);
            }
            return m;
        }

        /// <summary>Log2 of max frame number (for POC type 0).</summary>
        public int Log2MaxFrameNumber { get; set; }

        /// <summary>Maximum frame number (2^Log2MaxFrameNumber).</summary>
        public int MaxFrameNumber => 1 << Log2MaxFrameNumber;

        /// <summary>Picture order count type (0, 1, or 2).</summary>
        public int PictureOrderCountType { get; set; }

        /// <summary>Log2 of max POC LSB (for POC type 0).</summary>
        public int Log2MaxPictureOrderCountLsb { get; set; }

        /// <summary>Delta POC always zero (for POC type 1).</summary>
        public bool DeltaPocAlwaysZero { get; set; }

        /// <summary>Offset for non-reference POC (for POC type 1).</summary>
        public int OffsetForNonRefPic { get; set; }

        /// <summary>Offset for top to bottom field (for POC type 1).</summary>
        public int OffsetForTopToBottomField { get; set; }

        /// <summary>Number of reference frames in POC cycle (for POC type 1).</summary>
        public int NumRefFramesInPocCycle { get; set; }

        /// <summary>Offsets for reference frame POC (for POC type 1).</summary>
        public int[]? OffsetForRefFrame { get; set; }

        /// <summary>Maximum number of reference frames.</summary>
        public int MaxNumRefFrames { get; set; }

        /// <summary>Gaps in frame numbers are allowed.</summary>
        public bool GapsInFrameNumAllowed { get; set; }

        /// <summary>Picture width in macroblocks minus 1.</summary>
        public int PictureWidthInMacroblocksMinus1 { get; set; }

        /// <summary>Picture height in map units minus 1.</summary>
        public int PictureHeightInMapUnitsMinus1 { get; set; }

        /// <summary>Frame macroblocks only (not field).</summary>
        public bool FrameMacroblockOnly { get; set; } = true;

        /// <summary>Macroblock adaptive frame-field coding.</summary>
        public bool MbAdaptiveFrameField { get; set; }

        /// <summary>8x8 transform is direct spatial.</summary>
        public bool Direct8x8Inference { get; set; }

        /// <summary>Frame cropping is applied.</summary>
        public bool FrameCroppingFlag { get; set; }

        /// <summary>Left crop offset in pixels.</summary>
        public int FrameCropLeftOffset { get; set; }

        /// <summary>Right crop offset in pixels.</summary>
        public int FrameCropRightOffset { get; set; }

        /// <summary>Top crop offset in pixels.</summary>
        public int FrameCropTopOffset { get; set; }

        /// <summary>Bottom crop offset in pixels.</summary>
        public int FrameCropBottomOffset { get; set; }

        /// <summary>VUI parameters are present.</summary>
        public bool VuiParametersPresent { get; set; }

        /// <summary>Video Usability Information.</summary>
        public VideoUsabilityInfo? Vui { get; set; }

        /// <summary>Raw NAL unit data.</summary>
        public byte[]? RawData { get; set; }

        /// <summary>
        /// Calculates the coded picture width in pixels (before cropping).
        /// </summary>
        public int CodedWidth => (PictureWidthInMacroblocksMinus1 + 1) * 16;

        /// <summary>
        /// Calculates the coded picture height in pixels (before cropping).
        /// </summary>
        public int CodedHeight
        {
            get
            {
                int mapUnits = PictureHeightInMapUnitsMinus1 + 1;
                int heightMultiplier = FrameMacroblockOnly ? 1 : 2;
                return mapUnits * 16 * heightMultiplier;
            }
        }

        /// <summary>
        /// Calculates the display width after cropping.
        /// </summary>
        public int DisplayWidth
        {
            get
            {
                if (!FrameCroppingFlag)
                    return CodedWidth;

                int chromaArrayType = SeparateColorPlane ? 0 : (int)ChromaFormat;
                int subWidthC = (chromaArrayType == 1 || chromaArrayType == 2) ? 2 : 1;
                int cropUnitX = chromaArrayType == 0 ? 1 : subWidthC;

                return CodedWidth - (FrameCropLeftOffset + FrameCropRightOffset) * cropUnitX;
            }
        }

        /// <summary>
        /// Calculates the display height after cropping.
        /// </summary>
        public int DisplayHeight
        {
            get
            {
                if (!FrameCroppingFlag)
                    return CodedHeight;

                int chromaArrayType = SeparateColorPlane ? 0 : (int)ChromaFormat;
                int subHeightC = (chromaArrayType == 1) ? 2 : 1;
                int cropUnitY = (FrameMacroblockOnly ? 1 : 2) * (chromaArrayType == 0 ? 1 : subHeightC);

                return CodedHeight - (FrameCropTopOffset + FrameCropBottomOffset) * cropUnitY;
            }
        }

        /// <summary>
        /// Gets the framerate from VUI timing info.
        /// </summary>
        public double FrameRate => Vui?.CalculateFrameRate() ?? 0;
    }

    /// <summary>
    /// Picture Parameter Set (PPS) for H.264.
    /// Contains slice-level decoding parameters.
    /// </summary>
    public sealed class PictureParameterSet
    {
        /// <summary>PPS ID (0-255).</summary>
        public byte PictureParameterSetId { get; set; }

        /// <summary>Referenced SPS ID.</summary>
        public byte SequenceParameterSetId { get; set; }

        /// <summary>Entropy coding mode (0 = CAVLC, 1 = CABAC).</summary>
        public bool EntropyCodingMode { get; set; }

        /// <summary>Bottom field POC present in slice header.</summary>
        public bool BottomFieldPicOrderPresent { get; set; }

        /// <summary>Number of slice groups minus 1.</summary>
        public int NumSliceGroupsMinus1 { get; set; }

        /// <summary>Slice group map type.</summary>
        public int SliceGroupMapType { get; set; }

        /// <summary>Number of ref idx L0 active minus 1.</summary>
        public int NumRefIdxL0DefaultActiveMinus1 { get; set; }

        /// <summary>Number of ref idx L1 active minus 1.</summary>
        public int NumRefIdxL1DefaultActiveMinus1 { get; set; }

        /// <summary>Weighted prediction for P slices.</summary>
        public bool WeightedPrediction { get; set; }

        /// <summary>Weighted prediction mode for B slices (0-2).</summary>
        public int WeightedBiprediction { get; set; }

        /// <summary>Initial QP minus 26.</summary>
        public int PictureInitQpMinus26 { get; set; }

        /// <summary>Initial QP for SP/SI minus 26.</summary>
        public int PictureInitQsMinus26 { get; set; }

        /// <summary>Chroma QP index offset (-12 to 12).</summary>
        public int ChromaQpIndexOffset { get; set; }

        /// <summary>Deblocking filter control present in slice header.</summary>
        public bool DeblockingFilterControlPresent { get; set; }

        /// <summary>Constrained intra prediction.</summary>
        public bool ConstrainedIntraPrediction { get; set; }

        /// <summary>Redundant picture count present.</summary>
        public bool RedundantPictureCountPresent { get; set; }

        /// <summary>8x8 transform mode flag (High profile).</summary>
        public bool Transform8x8Mode { get; set; }

        /// <summary>Picture scaling matrix present.</summary>
        public bool PictureScalingMatrixPresent { get; set; }

        /// <summary>Bitmask tracking which PPS scaling lists were explicitly signaled.</summary>
        public ushort PictureScalingMatrixPresentMask { get; set; }

        /// <summary>6x 4x4 PPS scaling matrices in raster order.</summary>
        public byte[][] ScalingMatrix4 { get; set; } = SequenceParameterSet.CreateDefaultScaling4();

        /// <summary>6x 8x8 PPS scaling matrices in raster order.</summary>
        public byte[][] ScalingMatrix8 { get; set; } = SequenceParameterSet.CreateDefaultScaling8();

        /// <summary>Second chroma QP index offset.</summary>
        public int SecondChromaQpIndexOffset { get; set; }

        /// <summary>Raw NAL unit data.</summary>
        public byte[]? RawData { get; set; }

        /// <summary>
        /// The sequence parameter set this one was parsed against, or null if none was known yet.
        /// </summary>
        /// <remarks>
        /// The scaling matrices can fall back to the sequence set (7.4.2.2), so an identical
        /// picture set payload only yields an identical picture set when the sequence set behind
        /// it is the same one. <see cref="H264DecoderContext"/> needs both to recognise a repeated
        /// payload and keep the set it already has.
        /// </remarks>
        internal SequenceParameterSet? ParsedAgainstSps { get; set; }
    }

    /// <summary>
    /// Parser for SPS and PPS NAL units.
    /// </summary>
    public static class ParameterSetParser
    {
        /// <summary>
        /// Profiles that use extended SPS syntax.
        /// </summary>
        private static readonly H264Profile[] ExtendedProfiles = new[]
        {
            H264Profile.High,
            H264Profile.High10,
            H264Profile.High422,
            H264Profile.High444Predictive,
            H264Profile.ScalableBaseline,
            H264Profile.ScalableHigh,
            H264Profile.MultiviewHigh,
            H264Profile.StereoHigh,
            H264Profile.MultiviewDepthHigh,
        };

        /// <summary>
        /// Parses a Sequence Parameter Set from NAL unit payload.
        /// </summary>
        /// <param name="payload">NAL unit payload (RBSP data after type byte).</param>
        /// <returns>Parsed SPS, or null if parsing fails.</returns>
        public static SequenceParameterSet? ParseSps(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 4)
                return null;

            // Remove emulation prevention bytes
            byte[] rbsp = NalUnitParser.RemoveEmulationPreventionBytes(payload);
            var reader = new BitstreamReader(rbsp);

            try
            {
                var sps = new SequenceParameterSet();
                sps.RawData = payload.ToArray();

                // Profile, constraint flags, level
                sps.Profile = (H264Profile)reader.ReadBits(8);
                sps.ConstraintSetFlags = (byte)reader.ReadBits(8);
                sps.Level = (byte)reader.ReadBits(8);

                sps.SequenceParameterSetId = (byte)reader.ReadExpGolombUnsigned();

                // Extended profile syntax
                if (IsExtendedProfile(sps.Profile))
                {
                    sps.ChromaFormat = (ChromaFormat)reader.ReadExpGolombUnsigned();

                    if (sps.ChromaFormat == ChromaFormat.Chroma444)
                    {
                        sps.SeparateColorPlane = reader.ReadBit() == 1;
                    }

                    sps.BitDepthLuma = (int)reader.ReadExpGolombUnsigned() + 8;
                    sps.BitDepthChroma = (int)reader.ReadExpGolombUnsigned() + 8;
                    sps.TransformBypass = reader.ReadBit() == 1;

                    sps.ScalingMatrixPresent = reader.ReadBit() == 1;
                    if (sps.ScalingMatrixPresent)
                    {
                        ushort mask = 0;
                        DecodeScalingMatrices(ref reader, null, null, true,
                            sps.ScalingMatrixPresent, ref mask,
                            sps.ScalingMatrix4, sps.ScalingMatrix8);
                        sps.ScalingMatrixPresentMask = mask;
                    }
                }

                // Frame number and POC
                sps.Log2MaxFrameNumber = (int)reader.ReadExpGolombUnsigned() + 4;
                sps.PictureOrderCountType = (int)reader.ReadExpGolombUnsigned();

                if (sps.PictureOrderCountType == 0)
                {
                    sps.Log2MaxPictureOrderCountLsb = (int)reader.ReadExpGolombUnsigned() + 4;
                }
                else if (sps.PictureOrderCountType == 1)
                {
                    sps.DeltaPocAlwaysZero = reader.ReadBit() == 1;
                    sps.OffsetForNonRefPic = reader.ReadExpGolombSigned();
                    sps.OffsetForTopToBottomField = reader.ReadExpGolombSigned();
                    sps.NumRefFramesInPocCycle = (int)reader.ReadExpGolombUnsigned();

                    sps.OffsetForRefFrame = new int[sps.NumRefFramesInPocCycle];
                    for (int i = 0; i < sps.NumRefFramesInPocCycle; i++)
                    {
                        sps.OffsetForRefFrame[i] = reader.ReadExpGolombSigned();
                    }
                }

                sps.MaxNumRefFrames = (int)reader.ReadExpGolombUnsigned();
                sps.GapsInFrameNumAllowed = reader.ReadBit() == 1;

                // Picture dimensions
                sps.PictureWidthInMacroblocksMinus1 = (int)reader.ReadExpGolombUnsigned();
                sps.PictureHeightInMapUnitsMinus1 = (int)reader.ReadExpGolombUnsigned();

                sps.FrameMacroblockOnly = reader.ReadBit() == 1;
                if (!sps.FrameMacroblockOnly)
                {
                    sps.MbAdaptiveFrameField = reader.ReadBit() == 1;
                }

                sps.Direct8x8Inference = reader.ReadBit() == 1;

                // Frame cropping
                sps.FrameCroppingFlag = reader.ReadBit() == 1;
                if (sps.FrameCroppingFlag)
                {
                    sps.FrameCropLeftOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.FrameCropRightOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.FrameCropTopOffset = (int)reader.ReadExpGolombUnsigned();
                    sps.FrameCropBottomOffset = (int)reader.ReadExpGolombUnsigned();
                }

                // VUI parameters
                sps.VuiParametersPresent = reader.ReadBit() == 1;
                if (sps.VuiParametersPresent)
                {
                    sps.Vui = ParseVui(ref reader);
                }

                return sps;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parses a Picture Parameter Set from NAL unit payload.
        /// </summary>
        /// <param name="payload">NAL unit payload (RBSP data after type byte).</param>
        /// <param name="sps">The SPS referenced by this PPS (for extended syntax check).</param>
        /// <returns>Parsed PPS, or null if parsing fails.</returns>
        public static PictureParameterSet? ParsePps(ReadOnlySpan<byte> payload, SequenceParameterSet? sps = null)
        {
            if (payload.Length < 1)
                return null;

            byte[] rbsp = NalUnitParser.RemoveEmulationPreventionBytes(payload);
            var reader = new BitstreamReader(rbsp);

            try
            {
                var pps = new PictureParameterSet();
                pps.RawData = payload.ToArray();
                pps.PictureParameterSetId= (byte)reader.ReadExpGolombUnsigned();
                pps.SequenceParameterSetId = (byte)reader.ReadExpGolombUnsigned();

                pps.EntropyCodingMode = reader.ReadBit() == 1;
                pps.BottomFieldPicOrderPresent = reader.ReadBit() == 1;

                pps.NumSliceGroupsMinus1 = (int)reader.ReadExpGolombUnsigned();
                if (pps.NumSliceGroupsMinus1 > 0)
                {
                    pps.SliceGroupMapType = (int)reader.ReadExpGolombUnsigned();
                    // Skip slice group parameters - rarely used
                    SkipSliceGroupParameters(ref reader, pps);
                }

                pps.NumRefIdxL0DefaultActiveMinus1 = (int)reader.ReadExpGolombUnsigned();
                pps.NumRefIdxL1DefaultActiveMinus1 = (int)reader.ReadExpGolombUnsigned();

                pps.WeightedPrediction = reader.ReadBit() == 1;
                pps.WeightedBiprediction = (int)reader.ReadBits(2);

                pps.PictureInitQpMinus26 = reader.ReadExpGolombSigned();
                pps.PictureInitQsMinus26 = reader.ReadExpGolombSigned();
                pps.ChromaQpIndexOffset = reader.ReadExpGolombSigned();

                pps.DeblockingFilterControlPresent = reader.ReadBit() == 1;
                pps.ConstrainedIntraPrediction = reader.ReadBit() == 1;
                pps.RedundantPictureCountPresent = reader.ReadBit() == 1;

                // Extended PPS syntax (High profile)
                bool hasMore = reader.HasMoreRbspData();
                if (hasMore)
                {
                    pps.Transform8x8Mode = reader.ReadBit() == 1;
                    pps.PictureScalingMatrixPresent = reader.ReadBit() == 1;

                    if (pps.PictureScalingMatrixPresent)
                    {
                        ushort mask = 0;
                        DecodeScalingMatrices(ref reader, sps, pps, false,
                            pps.PictureScalingMatrixPresent, ref mask,
                            pps.ScalingMatrix4, pps.ScalingMatrix8);
                        pps.PictureScalingMatrixPresentMask = mask;
                    }
                    else if (sps != null && sps.ScalingMatrixPresent)
                    {
                        // No PPS scaling — inherit from SPS (only if SPS signaled matrices)
                        for (int i = 0; i < 6; i++)
                        {
                            Array.Copy(sps.ScalingMatrix4[i], pps.ScalingMatrix4[i], 16);
                            Array.Copy(sps.ScalingMatrix8[i], pps.ScalingMatrix8[i], 64);
                        }
                    }
                    // else: PPS keeps flat-16 defaults (no scaling matrices signaled anywhere)

                    pps.SecondChromaQpIndexOffset = reader.ReadExpGolombSigned();
                }
                else
                {
                    pps.SecondChromaQpIndexOffset = pps.ChromaQpIndexOffset;
                }

                return pps;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private static bool IsExtendedProfile(H264Profile profile)
        {
            foreach (var p in ExtendedProfiles)
            {
                if (p == profile)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Decodes a single scaling list from the bitstream (port of FFmpeg decode_scaling_list).
        /// Values are stored in raster order via zigzag scan reordering.
        /// </summary>
        private static bool DecodeScalingList(ref BitstreamReader reader, byte[] factors, int size,
            byte[] jvtDefaultList, byte[] fallbackList, ref ushort mask, int position)
        {
            ReadOnlySpan<byte> scan = size == 16
                ? ScalingListDefaults.ZigzagScan4x4
                : ScalingListDefaults.ZigzagScan8x8;

            bool present = reader.ReadBit() == 1;
            if (present)
                mask |= (ushort)(1 << position);

            if (!present)
            {
                // Not written in bitstream — use fallback (previous list or SPS list)
                Array.Copy(fallbackList, factors, size);
                return true;
            }

            int last = 8, next = 8;
            for (int i = 0; i < size; i++)
            {
                if (next != 0)
                {
                    int delta = reader.ReadExpGolombSigned();
                    next = (last + delta + 256) & 0xff;
                }

                if (i == 0 && next == 0)
                {
                    // Matrix not written — use JVT default
                    Array.Copy(jvtDefaultList, factors, size);
                    return true;
                }

                byte value = (byte)(next != 0 ? next : last);
                factors[scan[i]] = value;
                last = value;
            }

            return true;
        }

        /// <summary>
        /// Decodes all scaling matrices for SPS or PPS (port of FFmpeg decode_scaling_matrices).
        /// </summary>
        private static void DecodeScalingMatrices(ref BitstreamReader reader,
            SequenceParameterSet? sps, PictureParameterSet? pps, bool isSps,
            bool presentFlag, ref ushort mask,
            byte[][] scalingMatrix4, byte[][] scalingMatrix8)
        {
            bool fallbackToSps = !isSps && sps != null && sps.ScalingMatrixPresent;
            byte[] fallback0 = fallbackToSps ? sps!.ScalingMatrix4[0] : ScalingListDefaults.Default4x4Intra;
            byte[] fallback1 = fallbackToSps ? sps!.ScalingMatrix4[3] : ScalingListDefaults.Default4x4Inter;
            byte[] fallback2 = fallbackToSps ? sps!.ScalingMatrix8[0] : ScalingListDefaults.Default8x8Intra;
            byte[] fallback3 = fallbackToSps ? sps!.ScalingMatrix8[3] : ScalingListDefaults.Default8x8Inter;

            mask = 0;
            if (!presentFlag)
                return;

            // 6x 4x4 matrices: [0]=IntraY, [1]=IntraCb, [2]=IntraCr, [3]=InterY, [4]=InterCb, [5]=InterCr
            DecodeScalingList(ref reader, scalingMatrix4[0], 16, ScalingListDefaults.Default4x4Intra, fallback0, ref mask, 0);
            DecodeScalingList(ref reader, scalingMatrix4[1], 16, ScalingListDefaults.Default4x4Intra, scalingMatrix4[0], ref mask, 1);
            DecodeScalingList(ref reader, scalingMatrix4[2], 16, ScalingListDefaults.Default4x4Intra, scalingMatrix4[1], ref mask, 2);
            DecodeScalingList(ref reader, scalingMatrix4[3], 16, ScalingListDefaults.Default4x4Inter, fallback1, ref mask, 3);
            DecodeScalingList(ref reader, scalingMatrix4[4], 16, ScalingListDefaults.Default4x4Inter, scalingMatrix4[3], ref mask, 4);
            DecodeScalingList(ref reader, scalingMatrix4[5], 16, ScalingListDefaults.Default4x4Inter, scalingMatrix4[4], ref mask, 5);

            // 8x8 matrices: only if SPS-level or PPS has transform_8x8_mode
            if (isSps || (pps != null && pps.Transform8x8Mode))
            {
                DecodeScalingList(ref reader, scalingMatrix8[0], 64, ScalingListDefaults.Default8x8Intra, fallback2, ref mask, 6);
                DecodeScalingList(ref reader, scalingMatrix8[3], 64, ScalingListDefaults.Default8x8Inter, fallback3, ref mask, 7);

                if (sps != null && sps.ChromaFormat == ChromaFormat.Chroma444)
                {
                    DecodeScalingList(ref reader, scalingMatrix8[1], 64, ScalingListDefaults.Default8x8Intra, scalingMatrix8[0], ref mask, 8);
                    DecodeScalingList(ref reader, scalingMatrix8[4], 64, ScalingListDefaults.Default8x8Inter, scalingMatrix8[3], ref mask, 9);
                    DecodeScalingList(ref reader, scalingMatrix8[2], 64, ScalingListDefaults.Default8x8Intra, scalingMatrix8[1], ref mask, 10);
                    DecodeScalingList(ref reader, scalingMatrix8[5], 64, ScalingListDefaults.Default8x8Inter, scalingMatrix8[4], ref mask, 11);
                }
            }
        }

        private static void SkipSliceGroupParameters(ref BitstreamReader reader, PictureParameterSet pps)
        {
            // This is complex and rarely used - skip for now
            // Full implementation would handle all slice group map types
        }

        private static VideoUsabilityInfo ParseVui(ref BitstreamReader reader)
        {
            var vui = new VideoUsabilityInfo();

            vui.AspectRatioInfoPresent = reader.ReadBit() == 1;
            if (vui.AspectRatioInfoPresent)
            {
                vui.AspectRatioIdc = (byte)reader.ReadBits(8);
                if (vui.AspectRatioIdc == 255)  // Extended_SAR
                {
                    vui.SampleAspectRatioWidth = (ushort)reader.ReadBits(16);
                    vui.SampleAspectRatioHeight = (ushort)reader.ReadBits(16);
                }
            }

            vui.OverscanInfoPresent = reader.ReadBit() == 1;
            if (vui.OverscanInfoPresent)
            {
                vui.OverscanAppropriate = reader.ReadBit() == 1;
            }

            vui.VideoSignalTypePresent = reader.ReadBit() == 1;
            if (vui.VideoSignalTypePresent)
            {
                vui.VideoFormat = (byte)reader.ReadBits(3);
                vui.VideoFullRange = reader.ReadBit() == 1;
                vui.ColorDescriptionPresent = reader.ReadBit() == 1;

                if (vui.ColorDescriptionPresent)
                {
                    vui.ColorPrimaries = (byte)reader.ReadBits(8);
                    vui.TransferCharacteristics = (byte)reader.ReadBits(8);
                    vui.MatrixCoefficients = (byte)reader.ReadBits(8);
                }
            }

            vui.ChromaLocationInfoPresent = reader.ReadBit() == 1;
            if (vui.ChromaLocationInfoPresent)
            {
                vui.ChromaSampleLocationTop = reader.ReadExpGolombUnsigned();
                vui.ChromaSampleLocationBottom = reader.ReadExpGolombUnsigned();
            }

            vui.TimingInfoPresent = reader.ReadBit() == 1;
            if (vui.TimingInfoPresent)
            {
                vui.NumUnitsInTick = reader.ReadBits(32);
                vui.TimeScale = reader.ReadBits(32);
                vui.FixedFrameRate = reader.ReadBit() == 1;
            }

            vui.NalHrdParametersPresent = reader.ReadBit() == 1;
            if (vui.NalHrdParametersPresent)
            {
                SkipHrdParameters(ref reader);
            }

            vui.VclHrdParametersPresent = reader.ReadBit() == 1;
            if (vui.VclHrdParametersPresent)
            {
                SkipHrdParameters(ref reader);
            }

            if (vui.NalHrdParametersPresent || vui.VclHrdParametersPresent)
            {
                vui.LowDelayHrd = reader.ReadBit() == 1;
            }

            vui.PictureStructPresent = reader.ReadBit() == 1;

            vui.BitstreamRestrictionPresent = reader.ReadBit() == 1;
            if (vui.BitstreamRestrictionPresent)
            {
                vui.MotionVectorsOverPicBoundaries = reader.ReadBit() == 1;
                vui.MaxBytesPerPicDenom = reader.ReadExpGolombUnsigned();
                vui.MaxBitsPerMacroblockDenom = reader.ReadExpGolombUnsigned();
                vui.Log2MaxMvLengthHorizontal = reader.ReadExpGolombUnsigned();
                vui.Log2MaxMvLengthVertical = reader.ReadExpGolombUnsigned();
                vui.MaxNumReorderFrames = reader.ReadExpGolombUnsigned();
                vui.MaxDecFrameBuffering = reader.ReadExpGolombUnsigned();
            }

            return vui;
        }

        private static void SkipHrdParameters(ref BitstreamReader reader)
        {
            uint cpbCount = reader.ReadExpGolombUnsigned() + 1;
            reader.Skip(4);  // bit_rate_scale
            reader.Skip(4);  // cpb_size_scale

            for (int i = 0; i < cpbCount; i++)
            {
                reader.ReadExpGolombUnsigned();  // bit_rate_value_minus1
                reader.ReadExpGolombUnsigned();  // cpb_size_value_minus1
                reader.Skip(1);  // cbr_flag
            }

            reader.Skip(5);   // initial_cpb_removal_delay_length_minus1
            reader.Skip(5);   // cpb_removal_delay_length_minus1
            reader.Skip(5);   // dpb_output_delay_length_minus1
            reader.Skip(5);   // time_offset_length
        }
    }
}

using System;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Macroblock types for I-slices (Table 7-11 in H.264 spec).
    /// </summary>
    public enum IntraMacroblockType
    {
        /// <summary>Intra prediction with 4x4 luma blocks.</summary>
        Intra4x4 = 0,

        /// <summary>Intra prediction with 16x16 luma block (mode in name).</summary>
        Intra16x16_0_0_0 = 1,
        Intra16x16_1_0_0 = 2,
        Intra16x16_2_0_0 = 3,
        Intra16x16_3_0_0 = 4,
        Intra16x16_0_1_0 = 5,
        Intra16x16_1_1_0 = 6,
        Intra16x16_2_1_0 = 7,
        Intra16x16_3_1_0 = 8,
        Intra16x16_0_2_0 = 9,
        Intra16x16_1_2_0 = 10,
        Intra16x16_2_2_0 = 11,
        Intra16x16_3_2_0 = 12,
        Intra16x16_0_0_1 = 13,
        Intra16x16_1_0_1 = 14,
        Intra16x16_2_0_1 = 15,
        Intra16x16_3_0_1 = 16,
        Intra16x16_0_1_1 = 17,
        Intra16x16_1_1_1 = 18,
        Intra16x16_2_1_1 = 19,
        Intra16x16_3_1_1 = 20,
        Intra16x16_0_2_1 = 21,
        Intra16x16_1_2_1 = 22,
        Intra16x16_2_2_1 = 23,
        Intra16x16_3_2_1 = 24,

        /// <summary>Raw PCM samples, no prediction.</summary>
        IntraPcm = 25
    }

    /// <summary>
    /// Macroblock types for P-slices (Table 7-13 in H.264 spec).
    /// </summary>
    public enum InterPMacroblockType
    {
        /// <summary>16x16 partition with L0 prediction.</summary>
        P_L0_16x16 = 0,

        /// <summary>Two 16x8 partitions with L0 prediction.</summary>
        P_L0_L0_16x8 = 1,

        /// <summary>Two 8x16 partitions with L0 prediction.</summary>
        P_L0_L0_8x16 = 2,

        /// <summary>Four 8x8 sub-macroblock partitions.</summary>
        P_8x8 = 3,

        /// <summary>8x8 partitions with reference index reuse.</summary>
        P_8x8ref0 = 4,

        /// <summary>Intra-coded in P-slice.</summary>
        P_Intra = 5
    }

    /// <summary>
    /// Macroblock types for B-slices (Table 7-14 in H.264 spec).
    /// </summary>
    public enum InterBMacroblockType
    {
        /// <summary>Direct mode prediction.</summary>
        B_Direct_16x16 = 0,

        /// <summary>16x16 with L0 prediction.</summary>
        B_L0_16x16 = 1,

        /// <summary>16x16 with L1 prediction.</summary>
        B_L1_16x16 = 2,

        /// <summary>16x16 with bidirectional prediction.</summary>
        B_Bi_16x16 = 3,

        /// <summary>16x8 with L0_L0.</summary>
        B_L0_L0_16x8 = 4,
        B_L0_L0_8x16 = 5,
        B_L1_L1_16x8 = 6,
        B_L1_L1_8x16 = 7,
        B_L0_L1_16x8 = 8,
        B_L0_L1_8x16 = 9,
        B_L1_L0_16x8 = 10,
        B_L1_L0_8x16 = 11,
        B_L0_Bi_16x8 = 12,
        B_L0_Bi_8x16 = 13,
        B_L1_Bi_16x8 = 14,
        B_L1_Bi_8x16 = 15,
        B_Bi_L0_16x8 = 16,
        B_Bi_L0_8x16 = 17,
        B_Bi_L1_16x8 = 18,
        B_Bi_L1_8x16 = 19,
        B_Bi_Bi_16x8 = 20,
        B_Bi_Bi_8x16 = 21,

        /// <summary>8x8 sub-macroblock partitions in B-slice.</summary>
        B_8x8 = 22,

        /// <summary>Intra-coded in B-slice.</summary>
        B_Intra = 23
    }

    /// <summary>
    /// Sub-macroblock types for P-slices (Table 7-17).
    /// </summary>
    public enum SubMacroblockTypeP
    {
        P_L0_8x8 = 0,
        P_L0_8x4 = 1,
        P_L0_4x8 = 2,
        P_L0_4x4 = 3
    }

    /// <summary>
    /// Sub-macroblock types for B-slices (Table 7-18).
    /// </summary>
    public enum SubMacroblockTypeB
    {
        B_Direct_8x8 = 0,
        B_L0_8x8 = 1,
        B_L1_8x8 = 2,
        B_Bi_8x8 = 3,
        B_L0_8x4 = 4,
        B_L0_4x8 = 5,
        B_L1_8x4 = 6,
        B_L1_4x8 = 7,
        B_Bi_8x4 = 8,
        B_Bi_4x8 = 9,
        B_L0_4x4 = 10,
        B_L1_4x4 = 11,
        B_Bi_4x4 = 12
    }

    /// <summary>
    /// Intra 4x4 prediction modes.
    /// </summary>
    public enum Intra4x4PredictionMode
    {
        Vertical = 0,
        Horizontal = 1,
        Dc = 2,
        DiagonalDownLeft = 3,
        DiagonalDownRight = 4,
        VerticalRight = 5,
        HorizontalDown = 6,
        VerticalLeft = 7,
        HorizontalUp = 8
    }

    /// <summary>
    /// Intra 16x16 prediction modes.
    /// </summary>
    public enum Intra16x16PredictionMode
    {
        Vertical = 0,
        Horizontal = 1,
        Dc = 2,
        Plane = 3
    }

    /// <summary>
    /// Intra chroma prediction modes (8x8 for 4:2:0).
    /// </summary>
    public enum IntraChromaPredictionMode
    {
        Dc = 0,
        Horizontal = 1,
        Vertical = 2,
        Plane = 3
    }

    /// <summary>
    /// Represents a decoded macroblock.
    /// </summary>
    public struct Macroblock
    {
        /// <summary>Macroblock address (raster scan order).</summary>
        public int Address;

        /// <summary>X position in macroblocks.</summary>
        public int PositionX;

        /// <summary>Y position in macroblocks.</summary>
        public int PositionY;

        /// <summary>True if this is an intra-coded macroblock.</summary>
        public bool IsIntra;

        /// <summary>True if this is a skipped macroblock.</summary>
        public bool IsSkipped;

        /// <summary>Intra MB type (if IsIntra is true).</summary>
        public IntraMacroblockType IntraType;

        /// <summary>Quantization parameter for this macroblock.</summary>
        public int QuantizationParameter;

        /// <summary>Coded block pattern for luma (4 bits, one per 8x8 block).</summary>
        public int CodedBlockPatternLuma;

        /// <summary>Coded block pattern for chroma (0, 1, or 2).</summary>
        public int CodedBlockPatternChroma;

        /// <summary>Transform size flag (true = 8x8, false = 4x4).</summary>
        public bool Transform8x8;

        /// <summary>Number of non-zero luma coefficients per 4x4 block (16 values).</summary>
        public unsafe fixed byte NonZeroCountLuma[16];

        /// <summary>Number of non-zero chroma coefficients per 4x4 block (16 values for 4:2:2, first 8 used for 4:2:0).</summary>
        public unsafe fixed byte NonZeroCountChroma[16];

        /// <summary>Intra 4x4 prediction modes (16 values for 16 4x4 blocks).</summary>
        public unsafe fixed byte Intra4x4PredModes[16];

        /// <summary>Intra 16x16 prediction mode.</summary>
        public Intra16x16PredictionMode Intra16x16PredMode;

        /// <summary>Intra chroma prediction mode.</summary>
        public IntraChromaPredictionMode IntraChromaPredMode;

        // --- Inter prediction data (populated by CAVLC path for H264Decoder to process) ---

        /// <summary>
        /// Inter partition mode: -1=not set, 0=P_L0_16x16, 1=P_L0_L0_16x8, 2=P_L0_L0_8x16, 3=P_8x8, 4=P_8x8ref0.
        /// </summary>
        public int InterPartitionMode;

        /// <summary>Reference indices per partition (up to 4 for P_8x8).</summary>
        public unsafe fixed sbyte InterRefIdxL0[4];

        /// <summary>MVD X values per sub-partition (up to 16 for P_8x8 with 4x4 sub-MBs).</summary>
        public unsafe fixed short InterMvdL0X[16];

        /// <summary>MVD Y values per sub-partition (up to 16 for P_8x8 with 4x4 sub-MBs).</summary>
        public unsafe fixed short InterMvdL0Y[16];

        /// <summary>Sub-MB partition types for P_8x8 mode (0=8x8, 1=8x4, 2=4x8, 3=4x4).</summary>
        public unsafe fixed byte InterSubMbTypes[4];

        // --- B-slice inter prediction data ---

        /// <summary>
        /// Raw B-slice mb_type (H.264 Table 7-14). -1 = not a B-slice inter MB.
        /// 0=B_Direct_16x16, 1=B_L0_16x16, 2=B_L1_16x16, 3=B_Bi_16x16, 4-21=mixed, 22=B_8x8.
        /// </summary>
        public int BSliceMbType;

        /// <summary>L1 reference indices per partition (up to 4 for B_8x8).</summary>
        public unsafe fixed sbyte InterRefIdxL1[4];

        /// <summary>L1 MVD X values per sub-partition.</summary>
        public unsafe fixed short InterMvdL1X[16];

        /// <summary>L1 MVD Y values per sub-partition.</summary>
        public unsafe fixed short InterMvdL1Y[16];

        /// <summary>Sub-MB partition types for B_8x8 mode (H.264 Table 7-18).</summary>
        public unsafe fixed byte BSliceSubMbTypes[4];

        /// <summary>
        /// Gets the number of 4x4 luma blocks that have residual data.
        /// </summary>
        public readonly int CountLumaBlocks()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((CodedBlockPatternLuma & (1 << i)) != 0)
                    count += 4;  // Each 8x8 block contains 4 4x4 blocks
            }
            return count;
        }

        /// <summary>
        /// Checks if a specific 4x4 luma block has coded residual.
        /// </summary>
        /// <param name="blockIndex">Block index 0-15.</param>
        public readonly bool HasLumaResidual(int blockIndex)
        {
            // Map 4x4 block to 8x8 quadrant
            int quadrant = (blockIndex >> 2);
            return (CodedBlockPatternLuma & (1 << quadrant)) != 0;
        }

        /// <summary>
        /// Checks if a 4x4 block at (x4, y4) coordinates has coded residual.
        /// x4 and y4 are in 4x4-block units (0-3 each) within the macroblock.
        /// Uses per-4x4 non-zero counts for accurate deblocking bS computation.
        /// </summary>
        public readonly bool HasNonZeroCoeffs4x4(int x4, int y4)
        {
            // Convert raster (x4, y4) to scan-order block index
            int blkIdx = (y4 >> 1) * 8 + (x4 >> 1) * 4 + (y4 & 1) * 2 + (x4 & 1);
            unsafe { return NonZeroCountLuma[blkIdx] > 0; }
        }

        /// <summary>
        /// Checks if chroma DC has coded residual.
        /// </summary>
        public readonly bool HasChromaDcResidual => CodedBlockPatternChroma >= 1;

        /// <summary>
        /// Checks if chroma AC has coded residual.
        /// </summary>
        public readonly bool HasChromaAcResidual => CodedBlockPatternChroma == 2;
    }

    /// <summary>
    /// Motion vector for inter prediction.
    /// </summary>
    public struct MotionVector
    {
        /// <summary>Horizontal component in quarter-pixel units.</summary>
        public short X;

        /// <summary>Vertical component in quarter-pixel units.</summary>
        public short Y;

        /// <summary>Reference frame index.</summary>
        public sbyte ReferenceIndex;

        /// <summary>True if this partition uses L0 prediction.</summary>
        public bool UsesL0;

        /// <summary>True if this partition uses L1 prediction.</summary>
        public bool UsesL1;

        /// <summary>
        /// Creates a zero motion vector.
        /// </summary>
        public static MotionVector Zero => new() { X = 0, Y = 0, ReferenceIndex = 0 };

        /// <summary>
        /// Adds two motion vectors.
        /// </summary>
        public static MotionVector operator +(MotionVector a, MotionVector b)
        {
            return new MotionVector
            {
                X = (short)(a.X + b.X),
                Y = (short)(a.Y + b.Y),
                ReferenceIndex = a.ReferenceIndex
            };
        }

        /// <summary>
        /// Computes median prediction from three motion vectors.
        /// </summary>
        public static MotionVector Median(MotionVector a, MotionVector b, MotionVector c)
        {
            return new MotionVector
            {
                X = Median(a.X, b.X, c.X),
                Y = Median(a.Y, b.Y, c.Y),
                ReferenceIndex = a.ReferenceIndex
            };
        }

        private static short Median(short x, short y, short z)
        {
            // Sort and return middle value
            if (x > y)
                (x, y) = (y, x);
            if (y > z)
                (y, z) = (z, y);
            if (x > y)
                (x, y) = (y, x);
            return y;
        }
    }

    /// <summary>
    /// Stores motion vectors for an inter-coded macroblock.
    /// Each macroblock can have up to 16 4x4 partitions with independent MVs.
    /// </summary>
    public struct MacroblockMotionVectors
    {
        /// <summary>L0 motion vectors for 16 4x4 blocks.</summary>
        public MotionVector MvL0_0, MvL0_1, MvL0_2, MvL0_3;
        public MotionVector MvL0_4, MvL0_5, MvL0_6, MvL0_7;
        public MotionVector MvL0_8, MvL0_9, MvL0_10, MvL0_11;
        public MotionVector MvL0_12, MvL0_13, MvL0_14, MvL0_15;

        /// <summary>L1 motion vectors for 16 4x4 blocks (B-slices only).</summary>
        public MotionVector MvL1_0, MvL1_1, MvL1_2, MvL1_3;
        public MotionVector MvL1_4, MvL1_5, MvL1_6, MvL1_7;
        public MotionVector MvL1_8, MvL1_9, MvL1_10, MvL1_11;
        public MotionVector MvL1_12, MvL1_13, MvL1_14, MvL1_15;

        /// <summary>
        /// Gets L0 motion vector for a specific 4x4 block.
        /// </summary>
        public readonly MotionVector GetL0(int blockIndex)
        {
            return blockIndex switch
            {
                0 => MvL0_0, 1 => MvL0_1, 2 => MvL0_2, 3 => MvL0_3,
                4 => MvL0_4, 5 => MvL0_5, 6 => MvL0_6, 7 => MvL0_7,
                8 => MvL0_8, 9 => MvL0_9, 10 => MvL0_10, 11 => MvL0_11,
                12 => MvL0_12, 13 => MvL0_13, 14 => MvL0_14, 15 => MvL0_15,
                _ => MotionVector.Zero
            };
        }

        /// <summary>
        /// Gets L1 motion vector for a specific 4x4 block.
        /// </summary>
        public readonly MotionVector GetL1(int blockIndex)
        {
            return blockIndex switch
            {
                0 => MvL1_0, 1 => MvL1_1, 2 => MvL1_2, 3 => MvL1_3,
                4 => MvL1_4, 5 => MvL1_5, 6 => MvL1_6, 7 => MvL1_7,
                8 => MvL1_8, 9 => MvL1_9, 10 => MvL1_10, 11 => MvL1_11,
                12 => MvL1_12, 13 => MvL1_13, 14 => MvL1_14, 15 => MvL1_15,
                _ => MotionVector.Zero
            };
        }
    }

    /// <summary>
    /// Scan order tables for coefficients.
    /// </summary>
    public static class ScanOrder
    {
        /// <summary>
        /// Zig-zag scan order for 4x4 blocks (Table 8-13).
        /// Maps coefficient index 0-15 to (row, col) position.
        /// </summary>
        public static ReadOnlySpan<byte> ZigZag4x4 => new byte[]
        {
            0, 1, 4, 8,
            5, 2, 3, 6,
            9, 12, 13, 10,
            7, 11, 14, 15
        };

        /// <summary>
        /// Field scan order for 4x4 blocks (interlaced).
        /// </summary>
        public static ReadOnlySpan<byte> Field4x4 => new byte[]
        {
            0, 4, 1, 8,
            12, 5, 9, 13,
            2, 6, 10, 14,
            3, 7, 11, 15
        };

        /// <summary>
        /// Zig-zag scan order for 8x8 blocks (Table 8-14), standard raster positions.
        /// Used for scaling list parsing and other non-transform contexts.
        /// </summary>
        public static ReadOnlySpan<byte> ZigZag8x8 => new byte[]
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

        /// <summary>
        /// Transposed 8x8 zigzag scan for CABAC/CAVLC coefficient decode.
        /// Each entry is TRANSPOSE(ZigZag8x8[i]) where TRANSPOSE(x) = (x >> 3) | ((x &amp; 7) &lt;&lt; 3).
        /// This matches FFmpeg's h->zigzag_scan8x8 (h264_slice.c line 763) which ensures the
        /// IDCT column/row processing order produces bit-exact results with FFmpeg's implementation.
        /// The transposition swaps row↔col so the column-first IDCT effectively processes
        /// rows first on the original coefficient matrix, matching FFmpeg's rounding behavior.
        /// </summary>
        public static ReadOnlySpan<byte> ZigZag8x8Transposed => new byte[]
        {
             0,  8,  1,  2,  9, 16, 24, 17,
            10,  3,  4, 11, 18, 25, 32, 40,
            33, 26, 19, 12,  5,  6, 13, 20,
            27, 34, 41, 48, 56, 49, 42, 35,
            28, 21, 14,  7, 15, 22, 29, 36,
            43, 50, 57, 58, 51, 44, 37, 30,
            23, 31, 38, 45, 52, 59, 60, 53,
            46, 39, 47, 54, 61, 62, 55, 63
        };

        /// <summary>
        /// Field scan order for 8x8 blocks (H.264 spec Table 8-14).
        /// Used instead of ZigZag8x8 when field_pic_flag=1 or mb_field_decoding_flag=1.
        /// Prioritizes vertical traversal (column-first) for interlaced content.
        /// Non-transposed version matching our ZigZag8x8 convention.
        /// Ported from FFmpeg h264_slice.c field_scan8x8[64].
        /// </summary>
        public static ReadOnlySpan<byte> FieldScan8x8 => new byte[]
        {
             0,  8, 16,  1,  9, 24, 32, 17,
             2, 25, 40, 48, 56, 33, 10,  3,
            18, 41, 49, 57, 26, 11,  4, 19,
            34, 42, 50, 58, 27, 12,  5, 20,
            35, 43, 51, 59, 28, 13,  6, 21,
            36, 44, 52, 60, 29, 14, 22, 37,
            45, 53, 61, 30,  7, 15, 38, 46,
            54, 62, 23, 31, 39, 47, 55, 63
        };

        /// <summary>
        /// Transposed field scan for 8x8 CABAC blocks.
        /// Each entry is TRANSPOSE(FieldScan8x8[i]) matching FFmpeg h->field_scan8x8.
        /// </summary>
        public static ReadOnlySpan<byte> FieldScan8x8Transposed => new byte[]
        {
             0,  1,  2,  8,  9,  3,  4, 10,
            16, 11,  5,  6,  7, 12, 17, 24,
            18, 13, 14, 15, 19, 25, 32, 26,
            20, 21, 22, 23, 27, 33, 40, 34,
            28, 29, 30, 31, 35, 41, 48, 42,
            36, 37, 38, 39, 43, 49, 50, 44,
            45, 46, 47, 51, 56, 57, 52, 53,
            54, 55, 58, 59, 60, 61, 62, 63
        };

        /// <summary>
        /// CAVLC scan order for 8x8 blocks — maps sub-block coefficient positions to raster positions.
        /// Each 8x8 block is decoded as 4 sub-blocks of 16 coefficients each.
        /// Entry [s*16 + k] gives the raster position for sub-block s, coefficient k.
        /// Non-transposed raw values from FFmpeg h264_slice.c zigzag_scan8x8_cavlc[64].
        /// </summary>
        public static ReadOnlySpan<byte> ZigZagScan8x8Cavlc => new byte[]
        {
            // Sub-block 0 (entries 0-15)
             0,  9, 17, 18, 12, 40, 27,  7,
            35, 57, 29, 30, 58, 38, 53, 47,
            // Sub-block 1 (entries 16-31)
             1,  2, 24, 11, 19, 48, 20, 14,
            42, 50, 22, 37, 59, 31, 60, 55,
            // Sub-block 2 (entries 32-47)
             8,  3, 32,  4, 26, 41, 13, 21,
            49, 43, 15, 44, 52, 39, 61, 62,
            // Sub-block 3 (entries 48-63)
            16, 10, 25,  5, 33, 34,  6, 28,
            56, 36, 23, 51, 45, 46, 54, 63,
        };

        /// <summary>
        /// Transposed CAVLC scan for 8x8 blocks — matches FFmpeg h->zigzag_scan8x8_cavlc.
        /// Each entry is TRANSPOSE(ZigZagScan8x8Cavlc[i]).
        /// </summary>
        public static ReadOnlySpan<byte> ZigZagScan8x8CavlcTransposed => new byte[]
        {
            // Sub-block 0 (entries 0-15)
             0,  9, 10, 18, 33,  5, 27, 56,
            28, 15, 43, 51, 23, 52, 46, 61,
            // Sub-block 1 (entries 16-31)
             8, 16,  3, 25, 26,  6, 34, 49,
            21, 22, 50, 44, 31, 59, 39, 62,
            // Sub-block 2 (entries 32-47)
             1, 24,  4, 32, 19, 13, 41, 42,
            14, 29, 57, 37, 38, 60, 47, 55,
            // Sub-block 3 (entries 48-63)
             2, 17, 11, 40, 12, 20, 48, 35,
             7, 36, 58, 30, 45, 53, 54, 63,
        };

        /// <summary>
        /// Field CAVLC scan order for 8x8 blocks — field variant of ZigZagScan8x8Cavlc.
        /// Non-transposed raw values from FFmpeg h264_slice.c field_scan8x8_cavlc[64].
        /// </summary>
        public static ReadOnlySpan<byte> FieldScan8x8Cavlc => new byte[]
        {
            // Sub-block 0 (entries 0-15)
             0,  9,  2, 56, 18, 26, 34, 27,
            35, 28, 36, 29, 45,  7, 54, 39,
            // Sub-block 1 (entries 16-31)
             8, 24, 25, 33, 41, 11, 42, 12,
            43, 13, 44, 14, 53, 15, 62, 47,
            // Sub-block 2 (entries 32-47)
            16, 32, 40, 10, 49,  4, 50,  5,
            51,  6, 52, 22, 61, 38, 23, 55,
            // Sub-block 3 (entries 48-63)
             1, 17, 48,  3, 57, 19, 58, 20,
            59, 21, 60, 37, 30, 46, 31, 63,
        };

        /// <summary>
        /// Transposed field CAVLC scan for 8x8 blocks — matches FFmpeg h->field_scan8x8_cavlc.
        /// Each entry is TRANSPOSE(FieldScan8x8Cavlc[i]).
        /// </summary>
        public static ReadOnlySpan<byte> FieldScan8x8CavlcTransposed => new byte[]
        {
            // Sub-block 0 (entries 0-15)
             0,  9, 16,  7, 18, 19, 20, 27,
            28, 35, 36, 43, 45, 56, 54, 60,
            // Sub-block 1 (entries 16-31)
             1,  3, 11, 12, 13, 25, 21, 33,
            29, 41, 37, 49, 46, 57, 55, 61,
            // Sub-block 2 (entries 32-47)
             2,  4,  5, 17, 14, 32, 22, 40,
            30, 48, 38, 50, 47, 52, 58, 62,
            // Sub-block 3 (entries 48-63)
             8, 10,  6, 24, 15, 26, 23, 34,
            31, 42, 39, 44, 51, 53, 59, 63,
        };

        /// <summary>
        /// Block position to raster index for 4x4 blocks in a macroblock.
        /// Maps 0-15 to the position in luma plane.
        /// </summary>
        public static ReadOnlySpan<byte> Block4x4ToRaster => new byte[]
        {
            0, 1, 4, 5,
            2, 3, 6, 7,
            8, 9, 12, 13,
            10, 11, 14, 15
        };

        /// <summary>
        /// Maps 4x4 block index to X offset within macroblock (in pixels).
        /// </summary>
        public static int BlockIndexToX(int blockIndex)
        {
            return (blockIndex & 3) * 4;
        }

        /// <summary>
        /// Maps 4x4 block index to Y offset within macroblock (in pixels).
        /// </summary>
        public static int BlockIndexToY(int blockIndex)
        {
            return (blockIndex >> 2) * 4;
        }
    }
}

/// <summary>
/// The shape of the picture being decoded, carried as a type argument so the JIT folds it.
///
/// MBAFF cannot change inside a picture, but the decoder asks whether it is on over a hundred
/// times per macroblock - in neighbour addressing, in the CABAC context derivations, in motion
/// vector prediction and in reconstruction. Each test is cheap and perfectly predicted; what
/// costs is the code behind it, which inflates every method it sits in and crowds the registers
/// and the instruction cache of the path that is actually running. Measured by making the field
/// a `const` and letting the JIT delete the branches: 1.9% of decode on a progressive stream.
///
/// This is what FFmpeg's `h264_mb_template.c` buys by compiling the macroblock layer once per
/// shape. A C# generic over a struct is instantiated separately for each type argument, so
/// `TShape.Mbaff` folds to a literal and the dead side is deleted exactly the same way.
///
/// The dispatch happens once per macroblock, not once per call: the macroblock loop picks the
/// shape and everything below it is called with that type argument already fixed.
/// </summary>
internal interface IPictureShape
{
    /// <summary>True when the picture is MBAFF - macroblock-adaptive frame/field coded.</summary>
    static abstract bool Mbaff { get; }
}

/// <summary>A picture with no macroblock-adaptive frame/field coding. The common case.</summary>
internal readonly struct ProgressiveShape : IPictureShape
{
    public static bool Mbaff => false;
}

/// <summary>An MBAFF picture, where each macroblock pair chooses frame or field coding.</summary>
internal readonly struct MbaffShape : IPictureShape
{
    public static bool Mbaff => true;
}

/// <summary>
/// The sample width of the picture, carried as a type argument for the same reason as
/// <see cref="IPictureShape"/>. Eight-bit and ten-or-more-bit reconstruction differ in the
/// element type of every coefficient buffer and in how a plane offset becomes a byte offset,
/// so the reconstruction methods carry both paths and choose between them 44 times. Making the
/// field a `const` and letting the JIT delete the wide half measured 1.4% of decode.
///
/// FFmpeg compiles h264_template.c once per bit depth to the same end. The dispatch here is one
/// test per macroblock, in <c>ReconstructMacroblock</c>; only the reconstruction call graph is
/// instantiated twice, because that is where the probe found the cost.
/// </summary>
internal interface IPixelDepth
{
    /// <summary>0 when a sample is one byte, 1 when it is two.</summary>
    static abstract int PixelShift { get; }
}

/// <summary>Eight-bit samples - one byte each. The common case.</summary>
internal readonly struct EightBitDepth : IPixelDepth
{
    public static int PixelShift => 0;
}

/// <summary>Ten-bit or wider samples - two bytes each.</summary>
internal readonly struct WideBitDepth : IPixelDepth
{
    public static int PixelShift => 1;
}

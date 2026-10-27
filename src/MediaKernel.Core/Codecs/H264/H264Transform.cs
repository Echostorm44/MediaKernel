using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using MediaKernel.Core.Dsp;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Integer transforms for H.264/AVC video decoding.
    /// Implements the H.264-specific integer approximation of the DCT.
    /// </summary>
    /// <remarks>
    /// H.264 uses a modified DCT that:
    /// - Uses only integer arithmetic (no floating point)
    /// - Provides bit-exact results across all implementations
    /// - Separates scaling from transform (scaling done during dequantization)
    /// </remarks>
    public static class H264Transform
    {
        /// <summary>
        /// Performs 4x4 inverse transform on a block of coefficients.
        /// Based on H.264 section 8.5.12.
        /// </summary>
        /// <param name="coefficients">Input coefficients in raster order (16 values).</param>
        /// <param name="output">Output residuals in raster order (16 values).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InverseTransform4x4(ReadOnlySpan<short> coefficients, Span<short> output)
        {
            if (Sse2.IsSupported)
            {
                InverseTransform4x4Sse2(coefficients, output);
            }
            else
            {
                InverseTransform4x4Scalar(coefficients, output);
            }
        }

        /// <summary>
        /// Performs 4x4 inverse transform using scalar implementation.
        /// </summary>
        public static void InverseTransform4x4Scalar(ReadOnlySpan<short> d, Span<short> output)
        {
            // First pass: horizontal transform
            Scratch.Of16<int> tempStorage = default;
            Span<int> temp = tempStorage;

            for (int i = 0; i < 4; i++)
            {
                int offset = i * 4;

                int d0 = d[offset];
                int d1 = d[offset + 1];
                int d2 = d[offset + 2];
                int d3 = d[offset + 3];

                // 1D horizontal IDCT
                int e0 = d0 + d2;
                int e1 = d0 - d2;
                int e2 = (d1 >> 1) - d3;
                int e3 = d1 + (d3 >> 1);

                temp[offset] = e0 + e3;
                temp[offset + 1] = e1 + e2;
                temp[offset + 2] = e1 - e2;
                temp[offset + 3] = e0 - e3;
            }

            // Second pass: vertical transform
            for (int i = 0; i < 4; i++)
            {
                int f0 = temp[i];
                int f1 = temp[i + 4];
                int f2 = temp[i + 8];
                int f3 = temp[i + 12];

                // 1D vertical IDCT
                int g0 = f0 + f2;
                int g1 = f0 - f2;
                int g2 = (f1 >> 1) - f3;
                int g3 = f1 + (f3 >> 1);

                // Round and shift (add 32, shift by 6)
                output[i] = (short)((g0 + g3 + 32) >> 6);
                output[i + 4] = (short)((g1 + g2 + 32) >> 6);
                output[i + 8] = (short)((g1 - g2 + 32) >> 6);
                output[i + 12] = (short)((g0 - g3 + 32) >> 6);
            }
        }

        /// <summary>
        /// Performs 4x4 inverse transform using SSE2 intrinsics via shared DSP library.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InverseTransform4x4Sse2(ReadOnlySpan<short> coefficients, Span<short> output)
        {
            H264TransformDsp.InverseTransform4x4(coefficients, output);
        }

        /// <summary>
        /// Performs 8x8 inverse transform for High profile blocks.
        /// Based on H.264 section 8.5.12.
        /// </summary>
        /// <param name="coefficients">Input coefficients in raster order (64 values).</param>
        /// <param name="output">Output residuals in raster order (64 values).</param>
        public static void InverseTransform8x8(ReadOnlySpan<short> coefficients, Span<short> output)
        {
            H264TransformDsp.InverseTransform8x8(coefficients, output);
        }

        /// <summary>
        /// Scalar 8x8 inverse transform matching FFmpeg's h264idct_template.c.
        /// Row pass first, then column pass with >>6 normalization.
        /// FFmpeg transposes coefficients via zigzag_scan8x8 TRANSPOSE macro, so its
        /// column-first loop structure processes original rows first — identical to our
        /// row-first approach. Intermediate results truncated to int16 between passes
        /// (matching FFmpeg's dctcoef=int16_t storage for 8-bit).
        /// </summary>
        public static void InverseTransform8x8Scalar(ReadOnlySpan<short> d, Span<short> output)
        {
            H264TransformDsp.InverseTransform8x8Scalar(d, output);
        }

        /// <summary>
        /// AVX2/SSE2 implementation of 8x8 inverse transform via shared DSP library.
        /// </summary>
        public static void InverseTransform8x8Avx2(ReadOnlySpan<short> coefficients, Span<short> output)
        {
            H264TransformDsp.InverseTransform8x8(coefficients, output);
        }

        /// <summary>
        /// Performs 4x4 Hadamard transform for luma DC coefficients (Intra16x16).
        /// </summary>
        /// <param name="dc">16 DC coefficients.</param>
        /// <param name="output">16 transformed coefficients.</param>
        public static void HadamardInverse4x4(ReadOnlySpan<short> dc, Span<short> output)
        {
            // Horizontal Hadamard
            Scratch.Of16<int> tempStorage = default;
            Span<int> temp = tempStorage;

            for (int i = 0; i < 4; i++)
            {
                int offset = i * 4;

                int h0 = dc[offset] + dc[offset + 2];
                int h1 = dc[offset] - dc[offset + 2];
                int h2 = dc[offset + 1] - dc[offset + 3];
                int h3 = dc[offset + 1] + dc[offset + 3];

                temp[offset] = h0 + h3;
                temp[offset + 1] = h1 + h2;
                temp[offset + 2] = h1 - h2;
                temp[offset + 3] = h0 - h3;
            }

            // Vertical Hadamard — raw (no normalization), per H.264 spec 8.5.11.1.
            // Scaling is handled separately in the DC dequantization step (spec 8.5.12.2).
            for (int i = 0; i < 4; i++)
            {
                int t0 = temp[i] + temp[i + 8];
                int t1 = temp[i] - temp[i + 8];
                int t2 = temp[i + 4] - temp[i + 12];
                int t3 = temp[i + 4] + temp[i + 12];

                output[i] = (short)(t0 + t3);
                output[i + 4] = (short)(t1 + t2);
                output[i + 8] = (short)(t1 - t2);
                output[i + 12] = (short)(t0 - t3);
            }
        }

        /// <summary>4x4 Hadamard with int32 output for 10-bit decode.</summary>
        public static void HadamardInverse4x4(ReadOnlySpan<short> dc, Span<int> output)
        {
            Scratch.Of16<int> tempStorage = default;
            Span<int> temp = tempStorage;

            for (int i = 0; i < 4; i++)
            {
                int offset = i * 4;
                int h0 = dc[offset] + dc[offset + 2];
                int h1 = dc[offset] - dc[offset + 2];
                int h2 = dc[offset + 1] - dc[offset + 3];
                int h3 = dc[offset + 1] + dc[offset + 3];
                temp[offset] = h0 + h3;
                temp[offset + 1] = h1 + h2;
                temp[offset + 2] = h1 - h2;
                temp[offset + 3] = h0 - h3;
            }

            for (int i = 0; i < 4; i++)
            {
                int t0 = temp[i] + temp[i + 8];
                int t1 = temp[i] - temp[i + 8];
                int t2 = temp[i + 4] - temp[i + 12];
                int t3 = temp[i + 4] + temp[i + 12];
                output[i] = t0 + t3;
                output[i + 4] = t1 + t2;
                output[i + 8] = t1 - t2;
                output[i + 12] = t0 - t3;
            }
        }

        /// <summary>
        /// Performs 2x2 Hadamard transform for chroma DC coefficients.
        /// </summary>
        /// <param name="dc">4 DC coefficients.</param>
        /// <param name="output">4 transformed coefficients.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HadamardInverse2x2(ReadOnlySpan<short> dc, Span<short> output)
        {
            // 2x2 Hadamard butterfly: dc layout is [0,0],[0,1],[1,0],[1,1] (row-major)
            // Output order must be [TL, TR, BL, BR] to match sub-block index mapping
            int a = dc[0] + dc[1];
            int b = dc[0] - dc[1];
            int c = dc[2] + dc[3];
            int d = dc[2] - dc[3];

            output[0] = (short)(a + c);  // TL: (dc[0]+dc[1])+(dc[2]+dc[3])
            output[1] = (short)(b + d);  // TR: (dc[0]-dc[1])+(dc[2]-dc[3])
            output[2] = (short)(a - c);  // BL: (dc[0]+dc[1])-(dc[2]+dc[3])
            output[3] = (short)(b - d);  // BR: (dc[0]-dc[1])-(dc[2]-dc[3])
        }

        /// <summary>2x2 Hadamard with int32 output for 10-bit decode.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HadamardInverse2x2(ReadOnlySpan<short> dc, Span<int> output)
        {
            int a = dc[0] + dc[1];
            int b = dc[0] - dc[1];
            int c = dc[2] + dc[3];
            int d = dc[2] - dc[3];
            output[0] = a + c;  // TL
            output[1] = b + d;  // TR
            output[2] = a - c;  // BL
            output[3] = b - d;  // BR
        }

        /// <summary>
        /// 2×4 inverse Hadamard for 4:2:2 chroma DC (8 coefficients).
        /// Input: 8 DC values in scan order (Chroma422DcScan).
        /// Output (in-place): 8 transformed DC values in row-major 2×4 grid order
        /// matching blkIdx layout: [r0c0, r0c1, r1c0, r1c1, r2c0, r2c1, r3c0, r3c1].
        /// Port of ff_h264_chroma422_dc_dequant_idct (transform only, no dequant).
        /// </summary>
        public static void HadamardInverse2x4(Span<short> dc)
        {
            // Descan from 4:2:2 chroma DC scan order to row-major 2×4 grid
            Scratch.Of8<short> gridStorage = default;
            Span<short> grid = gridStorage;
            grid[0] = dc[0];   // scan[0] → (r0,c0)
            grid[1] = dc[2];   // scan[2] → (r0,c1)
            grid[2] = dc[1];   // scan[1] → (r1,c0)
            grid[3] = dc[5];   // scan[5] → (r1,c1)
            grid[4] = dc[3];   // scan[3] → (r2,c0)
            grid[5] = dc[6];   // scan[6] → (r2,c1)
            grid[6] = dc[4];   // scan[4] → (r3,c0)
            grid[7] = dc[7];   // scan[7] → (r3,c1)

            // Horizontal pass: sum/diff of 2 columns per row
            Scratch.Of8<int> tempStorage = default;
            Span<int> temp = tempStorage;
            for (int row = 0; row < 4; row++)
            {
                temp[row * 2 + 0] = grid[row * 2] + grid[row * 2 + 1];
                temp[row * 2 + 1] = grid[row * 2] - grid[row * 2 + 1];
            }

            // Vertical pass: 4-point butterfly per column
            for (int col = 0; col < 2; col++)
            {
                int z0 = temp[0 + col] + temp[4 + col];   // row0 + row2
                int z1 = temp[0 + col] - temp[4 + col];   // row0 - row2
                int z2 = temp[2 + col] - temp[6 + col];   // row1 - row3
                int z3 = temp[2 + col] + temp[6 + col];   // row1 + row3

                dc[0 + col] = (short)(z0 + z3);
                dc[2 + col] = (short)(z1 + z2);
                dc[4 + col] = (short)(z1 - z2);
                dc[6 + col] = (short)(z0 - z3);
            }
        }

        /// <summary>2×4 inverse Hadamard with int32 output for 10-bit 4:2:2 decode.</summary>
        public static void HadamardInverse2x4(ReadOnlySpan<short> dc, Span<int> output)
        {
            // Descan from 4:2:2 chroma DC scan order to row-major 2×4 grid
            Scratch.Of8<int> gridStorage = default;
            Span<int> grid = gridStorage;
            grid[0] = dc[0];   // scan[0] → (r0,c0)
            grid[1] = dc[2];   // scan[2] → (r0,c1)
            grid[2] = dc[1];   // scan[1] → (r1,c0)
            grid[3] = dc[5];   // scan[5] → (r1,c1)
            grid[4] = dc[3];   // scan[3] → (r2,c0)
            grid[5] = dc[6];   // scan[6] → (r2,c1)
            grid[6] = dc[4];   // scan[4] → (r3,c0)
            grid[7] = dc[7];   // scan[7] → (r3,c1)

            // Horizontal pass: sum/diff of 2 columns per row
            Scratch.Of8<int> tempStorage = default;
            Span<int> temp = tempStorage;
            for (int row = 0; row < 4; row++)
            {
                temp[row * 2 + 0] = grid[row * 2] + grid[row * 2 + 1];
                temp[row * 2 + 1] = grid[row * 2] - grid[row * 2 + 1];
            }

            // Vertical pass: 4-point butterfly per column
            for (int col = 0; col < 2; col++)
            {
                int z0 = temp[0 + col] + temp[4 + col];
                int z1 = temp[0 + col] - temp[4 + col];
                int z2 = temp[2 + col] - temp[6 + col];
                int z3 = temp[2 + col] + temp[6 + col];

                output[0 + col] = z0 + z3;
                output[2 + col] = z1 + z2;
                output[4 + col] = z1 - z2;
                output[6 + col] = z0 - z3;
            }
        }

        // H.264 spec Table 8-15: Position-dependent level scale values
        // LevelScaleTable[qpMod6, positionClass] = normAdjust values
        // Per H.264 spec eq 8-313: v[m][i][j] = normAdjust(m,i,j) * weightScale(i,j)
        // The weightScale (default=16) is NOT included here because the spec formula
        // d = c * v * 2^(qp/6 - 4) is equivalent to d = c * normAdjust * 2^(qp/6)
        // since 16 * 2^(qp/6-4) = 2^4 * 2^(qp/6-4) = 2^(qp/6).
        // posClass 0: (even row, even col)  posClass 1: (odd row, odd col)  posClass 2: mixed
        private static readonly int[,] LevelScaleTable =
        {
            { 10, 16, 13 }, // qpMod6 = 0
            { 11, 18, 14 }, // qpMod6 = 1
            { 13, 20, 16 }, // qpMod6 = 2
            { 14, 23, 18 }, // qpMod6 = 3
            { 16, 25, 20 }, // qpMod6 = 4
            { 18, 29, 23 }, // qpMod6 = 5
        };

        // Position class for each raster position in a 4x4 block
        private static readonly int[] PositionClass4x4 =
        {
            0, 2, 0, 2,
            2, 1, 2, 1,
            0, 2, 0, 2,
            2, 1, 2, 1
        };

        /// <summary>
        /// Dequantizes a 4x4 block per H.264 spec section 8.5.12.1.
        /// Uses position-dependent level scaling with the combined normalization
        /// convention: scale = V[qpMod6][posClass] &lt;&lt; qpDiv6, matching the 
        /// inverse transform's built-in &gt;&gt;6 normalization.
        /// </summary>
        /// <param name="coefficients">Quantized coefficients in raster order (modified in place).</param>
        /// <param name="qp">Quantization parameter.</param>
        /// <param name="isIntra">True for intra blocks (unused — scaling is identical for intra/inter).</param>
        /// <param name="rasterOffset">Starting raster position (0 for full block, 1 for AC-only).</param>
        public static void Dequantize4x4(Span<short> coefficients, int qp, bool isIntra, int rasterOffset = 0)
        {
            // Clamp QP to valid range: 0-51 for 8-bit, up to 63 for 10-bit (51+6*(bitDepth-8))
            if (qp < 0) qp = 0;
            else if (qp > 63) qp = 63;

            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < coefficients.Length; i++)
            {
                int posClass = PositionClass4x4[rasterOffset + i];
                int scale = LevelScaleTable[qpMod6, posClass] << qpDiv6;
                coefficients[i] = (short)((int)coefficients[i] * scale);
            }
        }

        /// <summary>
        /// The dequantisation multiplier for one raster position of a 4x4 block - the entry
        /// FFmpeg tabulates as <c>dequant4_coeff[qp][position]</c>, computed instead of stored.
        /// </summary>
        /// <remarks>
        /// <para>
        /// FFmpeg builds the whole table once per picture parameter set and reads it per
        /// coefficient. Ported that way it measured -0.48%: the table is 4 KB for a 4x4 matrix
        /// and 16 KB for an 8x8 one, and the live row changes whenever the quantiser does, which
        /// at 1080p is per macroblock - so it competes with the picture for L1 against the 72
        /// bytes of level scales and 16 bytes of position classes it replaces, which never leave
        /// it. The multiplier is three loads, a multiply and a shift; the table lookup that
        /// replaces it is one load that misses.
        /// </para>
        /// <para>
        /// What FFmpeg actually gains from the table is not cheaper arithmetic but the ability to
        /// apply it from inside <c>decode_cabac_residual</c>, at the positions the significance
        /// map has just reported non-zero. That is what this overload is for: the parsers call it
        /// at their store, and the reconstruct side then has no dequantisation step at all.
        /// </para>
        /// </remarks>
        /// <param name="scalingMatrix">4x4 scaling matrix values (16 bytes in raster order).</param>
        /// <param name="qpDiv6"><c>qp / 6</c>, hoisted because it is fixed for a whole block.</param>
        /// <param name="qpMod6"><c>qp % 6</c>, likewise.</param>
        /// <param name="rasterPosition">Position within the 4x4 block, 0..15 in raster order.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Multiplier4x4(ReadOnlySpan<byte> scalingMatrix, int qpDiv6, int qpMod6, int rasterPosition)
            => (long)(LevelScaleTable[qpMod6, PositionClass4x4[rasterPosition]] * scalingMatrix[rasterPosition])
               << (qpDiv6 + 2);

        /// <summary>
        /// Applies <see cref="Multiplier4x4"/> to one parsed level, rounding as the spec's
        /// fixed point requires. The six bits come from the multiplier being scaled by
        /// <c>&lt;&lt; 2</c> above and the inverse transform's own <c>&gt;&gt; 6</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short DequantizeLevel(int level, long multiplier)
            => (short)((level * multiplier + 32) >> 6);

        /// <summary>
        /// Dequantizes a 4x4 block using custom scaling matrix.
        /// Per FFmpeg h264_ps.c init_dequant4_coeff_table:
        ///   qmul[pos] = (LevelScale[qpMod6][posClass] * scalingMatrix[pos]) &lt;&lt; (qpDiv6 + 2)
        /// Then during decode: coeff = (level * qmul[pos] + 32) >> 6
        /// </summary>
        /// <param name="coefficients">Quantized coefficients (modified in place).</param>
        /// <param name="scalingMatrix">4x4 scaling matrix values (16 bytes in raster order).</param>
        /// <param name="qp">Quantization parameter.</param>
        /// <param name="rasterOffset">Starting raster position (0 for full block, 1 for AC-only).</param>
        public static void Dequantize4x4WithMatrix(
            Span<short> coefficients,
            ReadOnlySpan<byte> scalingMatrix,
            int qp,
            int rasterOffset = 0)
        {
            if (qp < 0) qp = 0;
            else if (qp > 63) qp = 63;

            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < coefficients.Length; i++)
            {
                if (coefficients[i] == 0)
                    continue;
                int posClass = PositionClass4x4[rasterOffset + i];
                long qmul = (long)(LevelScaleTable[qpMod6, posClass] * scalingMatrix[rasterOffset + i]) << (qpDiv6 + 2);
                coefficients[i] = (short)(((long)coefficients[i] * qmul + 32) >> 6);
            }
        }

        /// <summary>
        /// Reconstructs a 4x4 block by adding residual to prediction.
        /// </summary>
        /// <param name="prediction">Predicted samples (16 values).</param>
        /// <param name="residual">Residual after inverse transform (16 values).</param>
        /// <param name="output">Reconstructed samples (16 values).</param>
        /// <param name="bitDepth">Bit depth (8 for standard, 10 for high10).</param>
        // H.264 spec 8.5.12.1: 8x8 position-dependent level scale values.
        // 6 classes derived from (row%4, col%4) per ffmpeg dequant8_coeff_init_scan.
        // The classes map to normAdjust values indexed by qpMod6.
        private static readonly int[,] LevelScale8x8Table =
        {
            { 20, 18, 32, 19, 25, 24 }, // qpMod6 = 0
            { 22, 19, 35, 21, 28, 26 }, // qpMod6 = 1
            { 26, 23, 42, 24, 33, 31 }, // qpMod6 = 2
            { 28, 25, 45, 26, 35, 33 }, // qpMod6 = 3
            { 32, 28, 51, 30, 40, 38 }, // qpMod6 = 4
            { 36, 32, 58, 34, 46, 43 }, // qpMod6 = 5
        };

        // Position class for each raster position in an 8x8 block.
        // Derived from ffmpeg: dequant8_coeff_init_scan[((x>>1)&12)|(x&3)].
        private static readonly int[] PositionClass8x8 =
        {
            0, 3, 4, 3, 0, 3, 4, 3, // Row 0
            3, 1, 5, 1, 3, 1, 5, 1, // Row 1
            4, 5, 2, 5, 4, 5, 2, 5, // Row 2
            3, 1, 5, 1, 3, 1, 5, 1, // Row 3
            0, 3, 4, 3, 0, 3, 4, 3, // Row 4
            3, 1, 5, 1, 3, 1, 5, 1, // Row 5
            4, 5, 2, 5, 4, 5, 2, 5, // Row 6
            3, 1, 5, 1, 3, 1, 5, 1, // Row 7
        };

        /// <summary>
        /// Dequantizes an 8x8 block per H.264 spec 8.5.12.1 with flat default scaling (weightScale=16).
        /// Uses the same formula as ffmpeg's inline dequant: d = (c * V * 16 * 2^(qP/6) + 32) >> 6.
        /// This produces correctly scaled coefficients for the 8x8 IDCT which normalizes with >> 6.
        /// </summary>
        public static void Dequantize8x8(Span<short> coefficients, int qp)
        {
            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < 64; i++)
            {
                if (coefficients[i] == 0)
                    continue;
                int posClass = PositionClass8x8[i];
                int qmul = LevelScale8x8Table[qpMod6, posClass] << (qpDiv6 + 4);
                coefficients[i] = (short)(((int)coefficients[i] * qmul + 32) >> 6);
            }
        }

        /// <summary>
        /// The dequantisation multiplier for one raster position of an 8x8 block, for the parsers
        /// to apply as they store - see <see cref="Multiplier4x4"/> for why they rather than the
        /// reconstruct side. The 8x8 shift carries no <c>+ 2</c>: its scaling matrix entries are
        /// already four bits larger than the 4x4 ones.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Multiplier8x8(ReadOnlySpan<byte> scalingMatrix, int qpDiv6, int qpMod6, int rasterPosition)
            => (long)(LevelScale8x8Table[qpMod6, PositionClass8x8[rasterPosition]] * scalingMatrix[rasterPosition])
               << qpDiv6;

        /// <summary>
        /// Dequantizes an 8x8 block using custom scaling matrix from SPS/PPS.
        /// Per FFmpeg h264_ps.c init_dequant8_coeff_table:
        ///   qmul[pos] = (normAdjust[qpMod6][posClass] * scalingMatrix[pos]) &lt;&lt; qpDiv6
        /// Then during decode: coeff = (level * qmul[pos] + 32) >> 6
        /// </summary>
        public static void Dequantize8x8WithMatrix(
            Span<short> coefficients,
            ReadOnlySpan<byte> scalingMatrix,
            int qp)
        {
            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < 64; i++)
            {
                if (coefficients[i] == 0)
                    continue;
                int posClass = PositionClass8x8[i];
                long qmul = (long)(LevelScale8x8Table[qpMod6, posClass] * scalingMatrix[i]) << qpDiv6;
                coefficients[i] = (short)(((long)coefficients[i] * qmul + 32) >> 6);
            }
        }

        // ──────────────────────────────────────────────
        //  Wide (int32) overloads for 10-bit decode
        //  FFmpeg uses dctcoef=int32_t for bitDepth > 8
        // ──────────────────────────────────────────────

        /// <summary>
        /// Dequantizes a 4x4 block using custom scaling matrix with int32 output.
        /// Prevents overflow that occurs with short at high QPs (> 36 with 10-bit).
        /// </summary>
        public static void Dequantize4x4WithMatrix(
            Span<int> coefficients,
            ReadOnlySpan<byte> scalingMatrix,
            int qp,
            int rasterOffset = 0)
        {
            if (qp < 0) qp = 0;
            else if (qp > 63) qp = 63;

            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < coefficients.Length; i++)
            {
                if (coefficients[i] == 0)
                    continue;
                int posClass = PositionClass4x4[rasterOffset + i];
                long qmul = (long)(LevelScaleTable[qpMod6, posClass] * scalingMatrix[rasterOffset + i]) << (qpDiv6 + 2);
                coefficients[i] = (int)(((long)coefficients[i] * qmul + 32) >> 6);
            }
        }

        /// <summary>
        /// Dequantizes an 8x8 block using custom scaling matrix with int32 output.
        /// </summary>
        public static void Dequantize8x8WithMatrix(
            Span<int> coefficients,
            ReadOnlySpan<byte> scalingMatrix,
            int qp)
        {
            int qpDiv6 = qp / 6;
            int qpMod6 = qp % 6;

            for (int i = 0; i < 64; i++)
            {
                if (coefficients[i] == 0)
                    continue;
                int posClass = PositionClass8x8[i];
                long qmul = (long)(LevelScale8x8Table[qpMod6, posClass] * scalingMatrix[i]) << qpDiv6;
                coefficients[i] = (int)(((long)coefficients[i] * qmul + 32) >> 6);
            }
        }

        /// <summary>4x4 inverse transform with int32 coefficients for 10-bit decode.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InverseTransform4x4(ReadOnlySpan<int> coefficients, Span<int> output)
        {
            H264TransformDsp.InverseTransform4x4(coefficients, output);
        }

        /// <summary>8x8 inverse transform with int32 coefficients for 10-bit decode.</summary>
        public static void InverseTransform8x8(ReadOnlySpan<int> coefficients, Span<int> output)
        {
            H264TransformDsp.InverseTransform8x8Wide(coefficients, output);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reconstruct4x4(
            ReadOnlySpan<byte> prediction,
            ReadOnlySpan<short> residual,
            Span<byte> output,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;

            if (Sse2.IsSupported)
            {
                Reconstruct4x4Sse2(prediction, residual, output, maxValue);
            }
            else
            {
                for (int i = 0; i < 16; i++)
                {
                    int value = prediction[i] + residual[i];
                    output[i] = (byte)Math.Clamp(value, 0, maxValue);
                }
            }
        }

        /// <summary>
        /// SSE2-optimized reconstruction.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Reconstruct4x4Sse2(
            ReadOnlySpan<byte> prediction,
            ReadOnlySpan<short> residual,
            Span<byte> output,
            int maxValue)
        {
            fixed (byte* pPred = prediction)
            fixed (short* pRes = residual)
            fixed (byte* pOut = output)
            {
                // Load prediction bytes and zero-extend to shorts
                var pred = Sse2.LoadVector128(pPred);
                var predLo = Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16();
                var predHi = Sse2.UnpackHigh(pred, Vector128<byte>.Zero).AsInt16();

                // Load residuals
                var resLo = Sse2.LoadVector128(pRes);
                var resHi = Sse2.LoadVector128(pRes + 8);

                // Add
                var sumLo = Sse2.Add(predLo, resLo);
                var sumHi = Sse2.Add(predHi, resHi);

                // Pack with unsigned saturation to bytes
                var result = Sse2.PackUnsignedSaturate(sumLo, sumHi);

                // Store result
                Sse2.Store(pOut, result);
            }
        }
    }
}

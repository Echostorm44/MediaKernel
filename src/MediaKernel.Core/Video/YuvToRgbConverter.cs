using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video
{
    /// <summary>
    /// Color space conversion coefficients for different standards.
    /// </summary>
    public static class ColorCoefficients
    {
        // BT.601 (SD video) - values multiplied by 256 for fixed-point math
        public const int Bt601Kr = 66;   // 0.257 * 256
        public const int Bt601Kg = 129;  // 0.504 * 256
        public const int Bt601Kb = 25;   // 0.098 * 256
        
        // BT.709 (HD video) - standard for 1080p/4K
        public const int Bt709Kr = 47;   // 0.183 * 256
        public const int Bt709Kg = 157;  // 0.614 * 256
        public const int Bt709Kb = 16;   // 0.062 * 256

        // BT.2020 (UHD/HDR video)
        public const int Bt2020Kr = 53;  // 0.2126 * 256
        public const int Bt2020Kg = 183; // 0.7152 * 256
        public const int Bt2020Kb = 19;  // 0.0722 * 256
    }

    /// <summary>
    /// Color matrix for YUV to RGB conversion.
    /// Pre-calculated coefficients for efficient conversion.
    /// </summary>
    public readonly struct ColorMatrix
    {
        // Floating-point coefficients for accurate conversion
        public readonly float CrToR;
        public readonly float CrToG;
        public readonly float CbToG;
        public readonly float CbToB;

        // Fixed-point coefficients (scaled by 1024) for integer math
        public readonly int CrToRFixed;
        public readonly int CrToGFixed;
        public readonly int CbToGFixed;
        public readonly int CbToBFixed;
        
        // SIMD coefficients scaled for (coeff * 255 / 224) * 256
        // Used with MultiplyLow followed by >>8
        public readonly short CrToRSimd;
        public readonly short CrToGSimd;
        public readonly short CbToGSimd;
        public readonly short CbToBSimd;

        private ColorMatrix(float crToR, float crToG, float cbToG, float cbToB)
        {
            CrToR = crToR;
            CrToG = crToG;
            CbToG = cbToG;
            CbToB = cbToB;

            // Scale by 1024 for fixed-point (10-bit precision)
            CrToRFixed = (int)(crToR * 1024);
            CrToGFixed = (int)(crToG * 1024);
            CbToGFixed = (int)(cbToG * 1024);
            CbToBFixed = (int)(cbToB * 1024);
            
            // SIMD: coeff * 255 / 224 * 256 = coeff * 291.07
            const float simdScale = 255.0f / 224.0f * 256.0f;
            CrToRSimd = (short)(crToR * simdScale);
            CrToGSimd = (short)(crToG * simdScale);
            CbToGSimd = (short)(cbToG * simdScale);
            CbToBSimd = (short)(cbToB * simdScale);
        }

        /// <summary>
        /// BT.601 color matrix (SD video - 480i/576i).
        /// </summary>
        public static ColorMatrix Bt601 => new(
            crToR: 1.402f,
            crToG: -0.714136f,
            cbToG: -0.344136f,
            cbToB: 1.772f
        );

        /// <summary>
        /// BT.709 color matrix (HD video - 720p/1080p/4K).
        /// </summary>
        public static ColorMatrix Bt709 => new(
            crToR: 1.5748f,
            crToG: -0.468124f,
            cbToG: -0.187324f,
            cbToB: 1.8556f
        );

        /// <summary>
        /// BT.2020 color matrix (UHD/HDR video).
        /// </summary>
        public static ColorMatrix Bt2020 => new(
            crToR: 1.4746f,
            crToG: -0.571353f,
            cbToG: -0.164553f,
            cbToB: 1.8814f
        );
    }

    /// <summary>
    /// High-performance YUV to RGB converter with SIMD optimization.
    /// Supports NV12, P010, and I420 input formats.
    /// </summary>
    public static class YuvToRgbConverter
    {
        /// <summary>
        /// Converts NV12 (8-bit 4:2:0) to BGRA using AVX2.
        /// </summary>
        /// <param name="yPlane">Y plane (width * height bytes).</param>
        /// <param name="uvPlane">Interleaved UV plane (width * height/2 bytes).</param>
        /// <param name="width">Image width (must be multiple of 16).</param>
        /// <param name="height">Image height (must be even).</param>
        /// <param name="yStride">Stride of Y plane.</param>
        /// <param name="uvStride">Stride of UV plane.</param>
        /// <param name="output">Output BGRA buffer (width * height * 4 bytes).</param>
        /// <param name="outputStride">Stride of output buffer.</param>
        /// <param name="matrix">Color conversion matrix.</param>
        public static void ConvertNv12ToBgra(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            if (Avx2.IsSupported && width >= 16)
            {
                ConvertNv12ToBgraAvx2(yPlane, uvPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
            }
            else if (Sse41.IsSupported && width >= 8)
            {
                ConvertNv12ToBgraSse2(yPlane, uvPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
            }
            else
            {
                ConvertNv12ToBgraScalar(yPlane, uvPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
            }
        }

        /// <summary>
        /// AVX2 implementation - processes 16 pixels at a time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertNv12ToBgraAvx2(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            // Pre-calculate vectors with SIMD-specific scaling
            var y16 = Vector256.Create((short)16);
            var y128 = Vector256.Create((short)128);
            var yMult = Vector256.Create((short)298);  // 255/219 * 256 = 298
            var crToR = Vector256.Create(matrix.CrToRSimd);
            var crToG = Vector256.Create(matrix.CrToGSimd);
            var cbToG = Vector256.Create(matrix.CbToGSimd);
            var cbToB = Vector256.Create(matrix.CbToBSimd);
            var alpha = Vector256.Create((byte)255);
            var zero = Vector256<byte>.Zero;

            fixed (byte* yPtr = yPlane)
            fixed (byte* uvPtr = uvPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uvRow = uvPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    int col = 0;
                    int alignedWidth = width & ~15;

                    for (; col < alignedWidth; col += 16)
                    {
                        // Load 16 Y values
                        var yVals = Avx.LoadVector128(yRow + col);
                        
                        // Expand Y to 16-bit
                        var yLo = Avx2.UnpackLow(yVals.ToVector256Unsafe(), zero).GetLower();
                        var yHi = Avx2.UnpackHigh(yVals.ToVector256Unsafe(), zero).GetLower();
                        var y16Lo = Avx2.SubtractSaturate(yLo.AsInt16(), y16.GetLower());
                        var y16Hi = Avx2.SubtractSaturate(yHi.AsInt16(), y16.GetLower());

                        // Load 8 UV pairs (16 bytes = 8 U + 8 V interleaved)
                        var uvVals = Avx.LoadVector128(uvRow + col);

                        // Deinterleave U and V
                        var deinterleaved = DeinterleaveUvSse2(uvVals.ToVector256Unsafe().GetLower());
                        var uVals = deinterleaved.U;
                        var vVals = deinterleaved.V;

                        // Expand U and V to 16-bit and duplicate for 2 pixels each
                        var u16 = Avx2.UnpackLow(uVals.ToVector256Unsafe(), zero).GetLower().AsInt16();
                        var v16 = Avx2.UnpackLow(vVals.ToVector256Unsafe(), zero).GetLower().AsInt16();

                        // Subtract 128 (center)
                        u16 = Sse2.Subtract(u16, y128.GetLower());
                        v16 = Sse2.Subtract(v16, y128.GetLower());

                        // Duplicate for chroma upsampling
                        var uLoLo = Sse2.UnpackLow(u16, u16);
                        var uLoHi = Sse2.UnpackHigh(u16, u16);
                        var vLoLo = Sse2.UnpackLow(v16, v16);
                        var vLoHi = Sse2.UnpackHigh(v16, v16);

                        // Calculate RGB for first 8 pixels
                        var rLo = CalculateRgbComponentSse2(y16Lo.AsInt16(), vLoLo, yMult.GetLower(), crToR.GetLower());
                        var gLo = CalculateGreenSse2(y16Lo.AsInt16(), uLoLo, vLoLo, yMult.GetLower(), crToG.GetLower(), cbToG.GetLower());
                        var bLo = CalculateRgbComponentSse2(y16Lo.AsInt16(), uLoLo, yMult.GetLower(), cbToB.GetLower());

                        // Calculate RGB for second 8 pixels
                        var rHi = CalculateRgbComponentSse2(y16Hi.AsInt16(), vLoHi, yMult.GetLower(), crToR.GetLower());
                        var gHi = CalculateGreenSse2(y16Hi.AsInt16(), uLoHi, vLoHi, yMult.GetLower(), crToG.GetLower(), cbToG.GetLower());
                        var bHi = CalculateRgbComponentSse2(y16Hi.AsInt16(), uLoHi, yMult.GetLower(), cbToB.GetLower());

                        // Pack to bytes
                        var r8 = Sse2.PackUnsignedSaturate(rLo, rHi);
                        var g8 = Sse2.PackUnsignedSaturate(gLo, gHi);
                        var b8 = Sse2.PackUnsignedSaturate(bLo, bHi);

                        // Interleave to BGRA
                        var bg0 = Sse2.UnpackLow(b8, g8);
                        var bg1 = Sse2.UnpackHigh(b8, g8);
                        var ra0 = Sse2.UnpackLow(r8, alpha.GetLower());
                        var ra1 = Sse2.UnpackHigh(r8, alpha.GetLower());

                        var bgra0 = Sse2.UnpackLow(bg0.AsInt16(), ra0.AsInt16());
                        var bgra1 = Sse2.UnpackHigh(bg0.AsInt16(), ra0.AsInt16());
                        var bgra2 = Sse2.UnpackLow(bg1.AsInt16(), ra1.AsInt16());
                        var bgra3 = Sse2.UnpackHigh(bg1.AsInt16(), ra1.AsInt16());

                        // Store
                        Sse2.Store(outRow + col * 4, bgra0.AsByte());
                        Sse2.Store(outRow + col * 4 + 16, bgra1.AsByte());
                        Sse2.Store(outRow + col * 4 + 32, bgra2.AsByte());
                        Sse2.Store(outRow + col * 4 + 48, bgra3.AsByte());
                    }

                    // Handle remaining pixels with scalar
                    for (; col < width; col++)
                    {
                        int uvIndex = col / 2 * 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uvRow[uvIndex],
                            uvRow[uvIndex + 1],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }

        private readonly struct UvPair
        {
            public readonly Vector128<byte> U;
            public readonly Vector128<byte> V;
            public UvPair(Vector128<byte> u, Vector128<byte> v) { U = u; V = v; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UvPair DeinterleaveUvSse2(Vector128<byte> uv)
        {
            // UV is interleaved: U0 V0 U1 V1 U2 V2 ...
            // We need to extract U and V separately
            var mask = Vector128.Create(
                (byte)0, 2, 4, 6, 8, 10, 12, 14,
                1, 3, 5, 7, 9, 11, 13, 15);

            if (Ssse3.IsSupported)
            {
                var shuffled = Ssse3.Shuffle(uv, mask);
                var u = Sse2.UnpackLow(shuffled.AsUInt64(), shuffled.AsUInt64()).AsByte();
                var v = Sse2.UnpackHigh(shuffled.AsUInt64(), shuffled.AsUInt64()).AsByte();
                return new UvPair(u, v);
            }
            else
            {
                // SSE2 fallback - manual deinterleave
                Span<byte> uvBytes = stackalloc byte[16];
                Span<byte> uBytes = stackalloc byte[8];
                Span<byte> vBytes = stackalloc byte[8];

                uv.CopyTo(uvBytes);
                for (int i = 0; i < 8; i++)
                {
                    uBytes[i] = uvBytes[i * 2];
                    vBytes[i] = uvBytes[i * 2 + 1];
                }

                return new UvPair(
                    Vector128.Create(uBytes[0], uBytes[1], uBytes[2], uBytes[3],
                                   uBytes[4], uBytes[5], uBytes[6], uBytes[7],
                                   (byte)0, 0, 0, 0, 0, 0, 0, 0),
                    Vector128.Create(vBytes[0], vBytes[1], vBytes[2], vBytes[3],
                                   vBytes[4], vBytes[5], vBytes[6], vBytes[7],
                                   (byte)0, 0, 0, 0, 0, 0, 0, 0));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> CalculateRgbComponentSse2(
            Vector128<short> y,
            Vector128<short> chroma,
            Vector128<short> yMult,
            Vector128<short> chromaMult)
        {
            // Accurate fixed-point: ((Y-16) * yMult + (chroma-128) * chromaMult + 128) >> 8
            // Use 32-bit intermediates to avoid overflow
            // For SSE2, process in two halves
            
            var yLo32 = Sse2.UnpackLow(y, Sse2.ShiftRightArithmetic(y, 15)).AsInt32();
            var yHi32 = Sse2.UnpackHigh(y, Sse2.ShiftRightArithmetic(y, 15)).AsInt32();
            var cLo32 = Sse2.UnpackLow(chroma, Sse2.ShiftRightArithmetic(chroma, 15)).AsInt32();
            var cHi32 = Sse2.UnpackHigh(chroma, Sse2.ShiftRightArithmetic(chroma, 15)).AsInt32();
            
            var yMultLo = Sse2.UnpackLow(yMult, Vector128<short>.Zero).AsInt32();
            var cMultLo = Sse2.UnpackLow(chromaMult, Sse2.ShiftRightArithmetic(chromaMult, 15)).AsInt32();
            
            // Multiply and accumulate
            var yScaledLo = Sse41.MultiplyLow(yLo32, yMultLo);
            var yScaledHi = Sse41.MultiplyLow(yHi32, yMultLo);
            var cScaledLo = Sse41.MultiplyLow(cLo32, cMultLo);
            var cScaledHi = Sse41.MultiplyLow(cHi32, cMultLo);
            
            var sumLo = Sse2.Add(yScaledLo, cScaledLo);
            var sumHi = Sse2.Add(yScaledHi, cScaledHi);
            
            // Shift right by 8 and pack back to 16-bit
            sumLo = Sse2.ShiftRightArithmetic(sumLo, 8);
            sumHi = Sse2.ShiftRightArithmetic(sumHi, 8);
            
            return Sse2.PackSignedSaturate(sumLo, sumHi);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> CalculateGreenSse2(
            Vector128<short> y,
            Vector128<short> u,
            Vector128<short> v,
            Vector128<short> yMult,
            Vector128<short> crToG,
            Vector128<short> cbToG)
        {
            // Same as above but with three terms
            var yLo32 = Sse2.UnpackLow(y, Sse2.ShiftRightArithmetic(y, 15)).AsInt32();
            var yHi32 = Sse2.UnpackHigh(y, Sse2.ShiftRightArithmetic(y, 15)).AsInt32();
            var uLo32 = Sse2.UnpackLow(u, Sse2.ShiftRightArithmetic(u, 15)).AsInt32();
            var uHi32 = Sse2.UnpackHigh(u, Sse2.ShiftRightArithmetic(u, 15)).AsInt32();
            var vLo32 = Sse2.UnpackLow(v, Sse2.ShiftRightArithmetic(v, 15)).AsInt32();
            var vHi32 = Sse2.UnpackHigh(v, Sse2.ShiftRightArithmetic(v, 15)).AsInt32();
            
            var yMultLo = Sse2.UnpackLow(yMult, Vector128<short>.Zero).AsInt32();
            var vMultLo = Sse2.UnpackLow(crToG, Sse2.ShiftRightArithmetic(crToG, 15)).AsInt32();
            var uMultLo = Sse2.UnpackLow(cbToG, Sse2.ShiftRightArithmetic(cbToG, 15)).AsInt32();
            
            var yScaledLo = Sse41.MultiplyLow(yLo32, yMultLo);
            var yScaledHi = Sse41.MultiplyLow(yHi32, yMultLo);
            var vScaledLo = Sse41.MultiplyLow(vLo32, vMultLo);
            var vScaledHi = Sse41.MultiplyLow(vHi32, vMultLo);
            var uScaledLo = Sse41.MultiplyLow(uLo32, uMultLo);
            var uScaledHi = Sse41.MultiplyLow(uHi32, uMultLo);
            
            var sumLo = Sse2.Add(Sse2.Add(yScaledLo, vScaledLo), uScaledLo);
            var sumHi = Sse2.Add(Sse2.Add(yScaledHi, vScaledHi), uScaledHi);
            
            sumLo = Sse2.ShiftRightArithmetic(sumLo, 8);
            sumHi = Sse2.ShiftRightArithmetic(sumHi, 8);
            
            return Sse2.PackSignedSaturate(sumLo, sumHi);
        }

        /// <summary>
        /// SSE2 implementation - processes 8 pixels at a time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertNv12ToBgraSse2(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            // Pre-calculate vectors for SSE2 with SIMD-specific scaling
            var y16 = Vector128.Create((short)16);
            var y128 = Vector128.Create((short)128);
            var yMult = Vector128.Create((short)298);  // 255/219 * 256 = 298
            var crToR = Vector128.Create(matrix.CrToRSimd);
            var crToG = Vector128.Create(matrix.CrToGSimd);
            var cbToG = Vector128.Create(matrix.CbToGSimd);
            var cbToB = Vector128.Create(matrix.CbToBSimd);
            var alpha = Vector128.Create((byte)255);
            var zero = Vector128<byte>.Zero;

            fixed (byte* yPtr = yPlane)
            fixed (byte* uvPtr = uvPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uvRow = uvPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    int col = 0;
                    int alignedWidth = width & ~7; // Process 8 pixels at a time

                    for (; col < alignedWidth; col += 8)
                    {
                        // Load 8 Y values
                        var yVals = Sse2.LoadScalarVector128((long*)(yRow + col)).AsByte();
                        
                        // Expand Y to 16-bit (lower 8 bytes only)
                        var y16Vals = Sse2.UnpackLow(yVals, zero).AsInt16();
                        y16Vals = Sse2.SubtractSaturate(y16Vals.AsUInt16(), y16.AsUInt16()).AsInt16();

                        // Load 8 bytes of UV (4 pairs): U0 V0 U1 V1 U2 V2 U3 V3
                        var uvVals = Sse2.LoadScalarVector128((long*)(uvRow + col)).AsByte();

                        // Deinterleave U and V
                        var deinterleaved = DeinterleaveUvSse2(uvVals);
                        var uVals = deinterleaved.U;
                        var vVals = deinterleaved.V;

                        // Expand U and V to 16-bit (only 4 values each, need to duplicate)
                        var u16 = Sse2.UnpackLow(uVals, zero).AsInt16();
                        var v16 = Sse2.UnpackLow(vVals, zero).AsInt16();

                        // Subtract 128 (center)
                        u16 = Sse2.Subtract(u16, y128);
                        v16 = Sse2.Subtract(v16, y128);

                        // Duplicate for chroma upsampling (each U/V value applies to 2 horizontal pixels)
                        var uDup = Sse2.UnpackLow(u16, u16);
                        var vDup = Sse2.UnpackLow(v16, v16);

                        // Calculate RGB components
                        var r = CalculateRgbComponentSse2(y16Vals, vDup, yMult, crToR);
                        var g = CalculateGreenSse2(y16Vals, uDup, vDup, yMult, crToG, cbToG);
                        var b = CalculateRgbComponentSse2(y16Vals, uDup, yMult, cbToB);

                        // Pack to bytes with saturation
                        var r8 = Sse2.PackUnsignedSaturate(r, r);
                        var g8 = Sse2.PackUnsignedSaturate(g, g);
                        var b8 = Sse2.PackUnsignedSaturate(b, b);

                        // Interleave to BGRA format
                        var bg = Sse2.UnpackLow(b8, g8);
                        var ra = Sse2.UnpackLow(r8, alpha);

                        var bgra0 = Sse2.UnpackLow(bg.AsInt16(), ra.AsInt16());
                        var bgra1 = Sse2.UnpackHigh(bg.AsInt16(), ra.AsInt16());

                        // Store 8 pixels (32 bytes)
                        Sse2.Store(outRow + col * 4, bgra0.AsByte());
                        Sse2.Store(outRow + col * 4 + 16, bgra1.AsByte());
                    }

                    // Handle remaining pixels with scalar
                    for (; col < width; col++)
                    {
                        int uvIndex = col / 2 * 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uvRow[uvIndex],
                            uvRow[uvIndex + 1],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }

        /// <summary>
        /// Scalar fallback implementation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertNv12ToBgraScalar(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            fixed (byte* yPtr = yPlane)
            fixed (byte* uvPtr = uvPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uvRow = uvPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    for (int col = 0; col < width; col++)
                    {
                        int uvIndex = col / 2 * 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uvRow[uvIndex],
                            uvRow[uvIndex + 1],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ConvertPixelScalar(
            byte y,
            byte u,
            byte v,
            byte* bgra,
            in ColorMatrix matrix)
        {
            // Limited range to full range conversion
            // Y is 16-235 range, Cb/Cr is 16-240 range centered at 128
            float yNorm = (y - 16) * (1.0f / 219.0f);  // Normalize Y to 0-1
            float cb = (u - 128) * (1.0f / 224.0f);    // Normalize Cb to -0.5 to 0.5
            float cr = (v - 128) * (1.0f / 224.0f);    // Normalize Cr to -0.5 to 0.5

            // Apply color matrix
            float r = yNorm + matrix.CrToR * cr;
            float g = yNorm + matrix.CrToG * cr + matrix.CbToG * cb;
            float b = yNorm + matrix.CbToB * cb;

            // Scale to 0-255 and clamp
            bgra[0] = ClampToByte((int)(b * 255.0f + 0.5f));
            bgra[1] = ClampToByte((int)(g * 255.0f + 0.5f));
            bgra[2] = ClampToByte((int)(r * 255.0f + 0.5f));
            bgra[3] = 255;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ClampToByte(int value)
        {
            return (byte)Math.Clamp(value, 0, 255);
        }

        /// <summary>
        /// Converts I420 (YUV420P) to BGRA.
        /// </summary>
        public static void ConvertI420ToBgra(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            if (Sse41.IsSupported && width >= 8)
            {
                ConvertI420ToBgraSse2(yPlane, uPlane, vPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
            }
            else
            {
                ConvertI420ToBgraScalar(yPlane, uPlane, vPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
            }
        }
        
        /// <summary>
        /// Converts I420 to BGRA using only the scalar path (no SIMD). Used for testing.
        /// </summary>
        public static void ConvertI420ToBgraScalarOnly(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            ConvertI420ToBgraScalar(yPlane, uPlane, vPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertI420ToBgraSse2(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            var y16 = Vector128.Create((short)16);
            var y128 = Vector128.Create((short)128);
            var yMult = Vector128.Create((short)298);
            var crToR = Vector128.Create(matrix.CrToRSimd);
            var crToG = Vector128.Create(matrix.CrToGSimd);
            var cbToG = Vector128.Create(matrix.CbToGSimd);
            var cbToB = Vector128.Create(matrix.CbToBSimd);
            var alpha = Vector128.Create((byte)255);
            var zero = Vector128<byte>.Zero;

            fixed (byte* yPtr = yPlane)
            fixed (byte* uPtr = uPlane)
            fixed (byte* vPtr = vPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uRow = uPtr + (row / 2) * uvStride;
                    byte* vRow = vPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    int col = 0;
                    int alignedWidth = width & ~7;

                    for (; col < alignedWidth; col += 8)
                    {
                        // Load 8 Y values
                        var yVals = Sse2.LoadScalarVector128((long*)(yRow + col)).AsByte();
                        var y16Vals = Sse2.UnpackLow(yVals, zero).AsInt16();
                        y16Vals = Sse2.SubtractSaturate(y16Vals.AsUInt16(), y16.AsUInt16()).AsInt16();

                        // Load 4 U and 4 V values (planar, not interleaved)
                        int uvCol = col / 2;
                        var uVals = Sse2.LoadScalarVector128((int*)(uRow + uvCol)).AsByte();
                        var vVals = Sse2.LoadScalarVector128((int*)(vRow + uvCol)).AsByte();

                        // Expand to 16-bit
                        var u16 = Sse2.UnpackLow(uVals, zero).AsInt16();
                        var v16 = Sse2.UnpackLow(vVals, zero).AsInt16();

                        // Center at 128
                        u16 = Sse2.Subtract(u16, y128);
                        v16 = Sse2.Subtract(v16, y128);

                        // Duplicate for horizontal chroma upsampling
                        var uDup = Sse2.UnpackLow(u16, u16);
                        var vDup = Sse2.UnpackLow(v16, v16);

                        // Calculate RGB
                        var r = CalculateRgbComponentSse2(y16Vals, vDup, yMult, crToR);
                        var g = CalculateGreenSse2(y16Vals, uDup, vDup, yMult, crToG, cbToG);
                        var b = CalculateRgbComponentSse2(y16Vals, uDup, yMult, cbToB);

                        // Pack and interleave to BGRA
                        var r8 = Sse2.PackUnsignedSaturate(r, r);
                        var g8 = Sse2.PackUnsignedSaturate(g, g);
                        var b8 = Sse2.PackUnsignedSaturate(b, b);

                        var bg = Sse2.UnpackLow(b8, g8);
                        var ra = Sse2.UnpackLow(r8, alpha);

                        var bgra0 = Sse2.UnpackLow(bg.AsInt16(), ra.AsInt16());
                        var bgra1 = Sse2.UnpackHigh(bg.AsInt16(), ra.AsInt16());

                        Sse2.Store(outRow + col * 4, bgra0.AsByte());
                        Sse2.Store(outRow + col * 4 + 16, bgra1.AsByte());
                    }

                    // Scalar fallback for remaining pixels
                    for (; col < width; col++)
                    {
                        int uvIndex = col / 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uRow[uvIndex],
                            vRow[uvIndex],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }

        private static unsafe void ConvertI420ToBgraScalar(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            fixed (byte* yPtr = yPlane)
            fixed (byte* uPtr = uPlane)
            fixed (byte* vPtr = vPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uRow = uPtr + (row / 2) * uvStride;
                    byte* vRow = vPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    for (int col = 0; col < width; col++)
                    {
                        int uvIndex = col / 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uRow[uvIndex],
                            vRow[uvIndex],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }

        /// <summary>
        /// Converts P010 (10-bit 4:2:0) to BGRA.
        /// </summary>
        public static void ConvertP010ToBgra(
            ReadOnlySpan<ushort> yPlane,
            ReadOnlySpan<ushort> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            ConvertP010ToBgraScalar(yPlane, uvPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
        }

        private static unsafe void ConvertP010ToBgraScalar(
            ReadOnlySpan<ushort> yPlane,
            ReadOnlySpan<ushort> uvPlane,
            int width,
            int height,
            int yStride,
            int uvStride,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            fixed (ushort* yPtr = yPlane)
            fixed (ushort* uvPtr = uvPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    ushort* yRow = yPtr + row * yStride;
                    ushort* uvRow = uvPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    for (int col = 0; col < width; col++)
                    {
                        int uvIndex = col / 2 * 2;
                        
                        // P010 uses upper 10 bits of 16-bit value, shift to 8-bit
                        int y10 = yRow[col] >> 6;
                        int u10 = uvRow[uvIndex] >> 6;
                        int v10 = uvRow[uvIndex + 1] >> 6;

                        // Scale from 10-bit (0-1023) to 8-bit range for calculation
                        int yVal = (y10 * 255 / 1023) - 16;
                        int uVal = (u10 * 255 / 1023) - 128;
                        int vVal = (v10 * 255 / 1023) - 128;

                        int r = (298 * yVal + matrix.CrToRFixed * vVal + 512) >> 10;
                        int g = (298 * yVal + matrix.CrToGFixed * vVal + matrix.CbToGFixed * uVal + 512) >> 10;
                        int b = (298 * yVal + matrix.CbToBFixed * uVal + 512) >> 10;

                        outRow[col * 4 + 0] = ClampToByte(b);
                        outRow[col * 4 + 1] = ClampToByte(g);
                        outRow[col * 4 + 2] = ClampToByte(r);
                        outRow[col * 4 + 3] = 255;
                    }
                }
            }
        }
        
        /// <summary>
        /// Converts YUY2 (packed YUV 4:2:2) to BGRA.
        /// YUY2 format: [Y0 U Y1 V] repeating for each pair of pixels.
        /// </summary>
        public static void ConvertYuy2ToBgra(
            ReadOnlySpan<byte> yuy2Data,
            int width, int height, int stride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            if (Ssse3.IsSupported && width >= 8)
            {
                ConvertYuy2ToBgraSsse3(yuy2Data, width, height, stride, output, outputStride, in matrix);
            }
            else
            {
                ConvertPackedYuvToBgraScalar(yuy2Data, width, height, stride, output, outputStride, in matrix, isYuy2: true);
            }
        }
        
        /// <summary>
        /// Converts UYVY (packed YUV 4:2:2) to BGRA.
        /// UYVY format: [U Y0 V Y1] repeating for each pair of pixels.
        /// </summary>
        public static void ConvertUyvyToBgra(
            ReadOnlySpan<byte> uyvyData,
            int width, int height, int stride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            if (Ssse3.IsSupported && width >= 8)
            {
                ConvertUyvyToBgraSsse3(uyvyData, width, height, stride, output, outputStride, in matrix);
            }
            else
            {
                ConvertPackedYuvToBgraScalar(uyvyData, width, height, stride, output, outputStride, in matrix, isYuy2: false);
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertYuy2ToBgraSsse3(
            ReadOnlySpan<byte> packedData,
            int width, int height, int stride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            // YUY2: Y0 U Y1 V Y2 U Y3 V ... (4 bytes = 2 pixels)
            // Shuffle mask to extract Y values: positions 0,2,4,6,8,10,12,14
            var yMask = Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            // Shuffle mask to extract U values: positions 1,1,5,5,9,9,13,13 (duplicate for 2 pixels)
            var uMask = Vector128.Create((byte)1, 1, 5, 5, 9, 9, 13, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            // Shuffle mask to extract V values: positions 3,3,7,7,11,11,15,15
            var vMask = Vector128.Create((byte)3, 3, 7, 7, 11, 11, 15, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            
            var y16 = Vector128.Create((short)16);
            var y128 = Vector128.Create((short)128);
            var yMult = Vector128.Create((short)298);
            var crToR = Vector128.Create(matrix.CrToRSimd);
            var crToG = Vector128.Create(matrix.CrToGSimd);
            var cbToG = Vector128.Create(matrix.CbToGSimd);
            var cbToB = Vector128.Create(matrix.CbToBSimd);
            var alpha = Vector128.Create((byte)255);
            var zero = Vector128<byte>.Zero;

            fixed (byte* inputPtr = packedData)
            fixed (byte* outputPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* inRow = inputPtr + row * stride;
                    byte* outRow = outputPtr + row * outputStride;
                    
                    int col = 0;
                    int alignedWidth = width & ~7;
                    
                    for (; col < alignedWidth; col += 8)
                    {
                        // Load 16 bytes = 8 pixels worth of YUY2
                        var packed = Sse2.LoadVector128(inRow + col * 2);
                        
                        // Extract Y, U, V using shuffles
                        var yVals = Ssse3.Shuffle(packed, yMask);
                        var uVals = Ssse3.Shuffle(packed, uMask);
                        var vVals = Ssse3.Shuffle(packed, vMask);
                        
                        // Expand to 16-bit
                        var y16Vals = Sse2.UnpackLow(yVals, zero).AsInt16();
                        y16Vals = Sse2.SubtractSaturate(y16Vals.AsUInt16(), y16.AsUInt16()).AsInt16();
                        
                        var u16Vals = Sse2.UnpackLow(uVals, zero).AsInt16();
                        var v16Vals = Sse2.UnpackLow(vVals, zero).AsInt16();
                        u16Vals = Sse2.Subtract(u16Vals, y128);
                        v16Vals = Sse2.Subtract(v16Vals, y128);
                        
                        // Calculate RGB
                        var r = CalculateRgbComponentSse2(y16Vals, v16Vals, yMult, crToR);
                        var g = CalculateGreenSse2(y16Vals, u16Vals, v16Vals, yMult, crToG, cbToG);
                        var b = CalculateRgbComponentSse2(y16Vals, u16Vals, yMult, cbToB);
                        
                        // Pack and interleave
                        var r8 = Sse2.PackUnsignedSaturate(r, r);
                        var g8 = Sse2.PackUnsignedSaturate(g, g);
                        var b8 = Sse2.PackUnsignedSaturate(b, b);
                        
                        var bg = Sse2.UnpackLow(b8, g8);
                        var ra = Sse2.UnpackLow(r8, alpha);
                        
                        var bgra0 = Sse2.UnpackLow(bg.AsInt16(), ra.AsInt16());
                        var bgra1 = Sse2.UnpackHigh(bg.AsInt16(), ra.AsInt16());
                        
                        Sse2.Store(outRow + col * 4, bgra0.AsByte());
                        Sse2.Store(outRow + col * 4 + 16, bgra1.AsByte());
                    }
                    
                    // Scalar fallback
                    for (; col < width; col += 2)
                    {
                        int macroPixel = col * 2;
                        byte y0 = inRow[macroPixel];
                        byte u = inRow[macroPixel + 1];
                        byte y1 = inRow[macroPixel + 2];
                        byte v = inRow[macroPixel + 3];
                        
                        ConvertPixelScalar(y0, u, v, outRow + col * 4, in matrix);
                        if (col + 1 < width)
                            ConvertPixelScalar(y1, u, v, outRow + (col + 1) * 4, in matrix);
                    }
                }
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertUyvyToBgraSsse3(
            ReadOnlySpan<byte> packedData,
            int width, int height, int stride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            // UYVY: U Y0 V Y1 U Y2 V Y3 ... (4 bytes = 2 pixels)
            // Shuffle mask to extract Y values: positions 1,3,5,7,9,11,13,15
            var yMask = Vector128.Create((byte)1, 3, 5, 7, 9, 11, 13, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            // Shuffle mask to extract U values: positions 0,0,4,4,8,8,12,12 (duplicate for 2 pixels)
            var uMask = Vector128.Create((byte)0, 0, 4, 4, 8, 8, 12, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            // Shuffle mask to extract V values: positions 2,2,6,6,10,10,14,14
            var vMask = Vector128.Create((byte)2, 2, 6, 6, 10, 10, 14, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            
            var y16 = Vector128.Create((short)16);
            var y128 = Vector128.Create((short)128);
            var yMult = Vector128.Create((short)298);
            var crToR = Vector128.Create(matrix.CrToRSimd);
            var crToG = Vector128.Create(matrix.CrToGSimd);
            var cbToG = Vector128.Create(matrix.CbToGSimd);
            var cbToB = Vector128.Create(matrix.CbToBSimd);
            var alpha = Vector128.Create((byte)255);
            var zero = Vector128<byte>.Zero;

            fixed (byte* inputPtr = packedData)
            fixed (byte* outputPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* inRow = inputPtr + row * stride;
                    byte* outRow = outputPtr + row * outputStride;
                    
                    int col = 0;
                    int alignedWidth = width & ~7;
                    
                    for (; col < alignedWidth; col += 8)
                    {
                        var packed = Sse2.LoadVector128(inRow + col * 2);
                        
                        var yVals = Ssse3.Shuffle(packed, yMask);
                        var uVals = Ssse3.Shuffle(packed, uMask);
                        var vVals = Ssse3.Shuffle(packed, vMask);
                        
                        var y16Vals = Sse2.UnpackLow(yVals, zero).AsInt16();
                        y16Vals = Sse2.SubtractSaturate(y16Vals.AsUInt16(), y16.AsUInt16()).AsInt16();
                        
                        var u16Vals = Sse2.UnpackLow(uVals, zero).AsInt16();
                        var v16Vals = Sse2.UnpackLow(vVals, zero).AsInt16();
                        u16Vals = Sse2.Subtract(u16Vals, y128);
                        v16Vals = Sse2.Subtract(v16Vals, y128);
                        
                        var r = CalculateRgbComponentSse2(y16Vals, v16Vals, yMult, crToR);
                        var g = CalculateGreenSse2(y16Vals, u16Vals, v16Vals, yMult, crToG, cbToG);
                        var b = CalculateRgbComponentSse2(y16Vals, u16Vals, yMult, cbToB);
                        
                        var r8 = Sse2.PackUnsignedSaturate(r, r);
                        var g8 = Sse2.PackUnsignedSaturate(g, g);
                        var b8 = Sse2.PackUnsignedSaturate(b, b);
                        
                        var bg = Sse2.UnpackLow(b8, g8);
                        var ra = Sse2.UnpackLow(r8, alpha);
                        
                        var bgra0 = Sse2.UnpackLow(bg.AsInt16(), ra.AsInt16());
                        var bgra1 = Sse2.UnpackHigh(bg.AsInt16(), ra.AsInt16());
                        
                        Sse2.Store(outRow + col * 4, bgra0.AsByte());
                        Sse2.Store(outRow + col * 4 + 16, bgra1.AsByte());
                    }
                    
                    for (; col < width; col += 2)
                    {
                        int macroPixel = col * 2;
                        byte u = inRow[macroPixel];
                        byte y0 = inRow[macroPixel + 1];
                        byte v = inRow[macroPixel + 2];
                        byte y1 = inRow[macroPixel + 3];
                        
                        ConvertPixelScalar(y0, u, v, outRow + col * 4, in matrix);
                        if (col + 1 < width)
                            ConvertPixelScalar(y1, u, v, outRow + (col + 1) * 4, in matrix);
                    }
                }
            }
        }
        
        private static unsafe void ConvertPackedYuvToBgraScalar(
            ReadOnlySpan<byte> packedData,
            int width, int height, int stride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix, bool isYuy2)
        {
            // YUY2: Y0 U Y1 V (Y at offsets 0, 2; U at 1, V at 3)
            // UYVY: U Y0 V Y1 (Y at offsets 1, 3; U at 0, V at 2)
            int yOffset0 = isYuy2 ? 0 : 1;
            int yOffset1 = isYuy2 ? 2 : 3;
            int uOffset = isYuy2 ? 1 : 0;
            int vOffset = isYuy2 ? 3 : 2;
            
            fixed (byte* inputPtr = packedData)
            fixed (byte* outputPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* inRow = inputPtr + row * stride;
                    byte* outRow = outputPtr + row * outputStride;
                    
                    for (int col = 0; col < width; col += 2)
                    {
                        int macroPixel = col * 2;
                        
                        byte y0 = inRow[macroPixel + yOffset0];
                        byte y1 = inRow[macroPixel + yOffset1];
                        byte u = inRow[macroPixel + uOffset];
                        byte v = inRow[macroPixel + vOffset];
                        
                        // First pixel
                        ConvertPixelScalar(y0, u, v, outRow + col * 4, in matrix);
                        
                        // Second pixel (if within bounds)
                        if (col + 1 < width)
                        {
                            ConvertPixelScalar(y1, u, v, outRow + (col + 1) * 4, in matrix);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Converts I422 (planar YUV 4:2:2) to BGRA.
        /// Similar to I420 but U/V planes are full height.
        /// </summary>
        public static void ConvertI422ToBgra(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width, int height, int yStride, int uvStride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            ConvertI422ToBgraScalar(yPlane, uPlane, vPlane, width, height, yStride, uvStride, output, outputStride, in matrix);
        }
        
        private static unsafe void ConvertI422ToBgraScalar(
            ReadOnlySpan<byte> yPlane,
            ReadOnlySpan<byte> uPlane,
            ReadOnlySpan<byte> vPlane,
            int width, int height, int yStride, int uvStride,
            Span<byte> output, int outputStride,
            in ColorMatrix matrix)
        {
            fixed (byte* yPtr = yPlane)
            fixed (byte* uPtr = uPlane)
            fixed (byte* vPtr = vPlane)
            fixed (byte* outputPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * yStride;
                    byte* uRow = uPtr + row * uvStride;  // 4:2:2 has U/V at full height
                    byte* vRow = vPtr + row * uvStride;
                    byte* outRow = outputPtr + row * outputStride;
                    
                    for (int col = 0; col < width; col++)
                    {
                        int uvIndex = col / 2;
                        ConvertPixelScalar(
                            yRow[col],
                            uRow[uvIndex],
                            vRow[uvIndex],
                            outRow + col * 4,
                            in matrix);
                    }
                }
            }
        }
        
        /// <summary>
        /// Converts Y only (grayscale) to BGRA.
        /// </summary>
        public static void ConvertYToBgra(
            ReadOnlySpan<byte> yPlane,
            int width, int height, int stride,
            Span<byte> output, int outputStride)
        {
            ConvertYToBgraScalar(yPlane, width, height, stride, output, outputStride);
        }
        
        private static unsafe void ConvertYToBgraScalar(
            ReadOnlySpan<byte> yPlane,
            int width, int height, int stride,
            Span<byte> output, int outputStride)
        {
            fixed (byte* yPtr = yPlane)
            fixed (byte* outputPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = yPtr + row * stride;
                    byte* outRow = outputPtr + row * outputStride;
                    
                    for (int col = 0; col < width; col++)
                    {
                        // Convert limited range Y (16-235) to full range
                        int y = yRow[col];
                        int gray = (y - 16) * 255 / 219;
                        gray = gray < 0 ? 0 : gray > 255 ? 255 : gray;
                        
                        byte g = (byte)gray;
                        outRow[col * 4 + 0] = g;
                        outRow[col * 4 + 1] = g;
                        outRow[col * 4 + 2] = g;
                        outRow[col * 4 + 3] = 255;
                    }
                }
            }
        }
        /// <summary>
        /// Converts planar 10-bit YUV 4:2:0 (I420P10) to BGRA.
        /// 10-bit limited range: Y=[64,940], Cb/Cr=[64,960], mid-chroma=512.
        /// </summary>
        public static void ConvertI420P10ToBgra(
            ReadOnlySpan<byte> yPlaneBytes,
            ReadOnlySpan<byte> uPlaneBytes,
            ReadOnlySpan<byte> vPlaneBytes,
            int width, int height,
            int yStrideByte, int uvStrideByte,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            if (Avx2.IsSupported && width >= 8)
            {
                ConvertI420P10ToBgraAvx2(yPlaneBytes, uPlaneBytes, vPlaneBytes,
                    width, height, yStrideByte, uvStrideByte, output, outputStride, in matrix);
            }
            else
            {
                ConvertI420P10ToBgraScalar(yPlaneBytes, uPlaneBytes, vPlaneBytes,
                    width, height, yStrideByte, uvStrideByte, output, outputStride, in matrix);
            }
        }

        /// <summary>
        /// AVX2 10-bit I420 → BGRA. Processes 8 pixels per iteration using 32-bit intermediates.
        /// Each pixel is assembled as a packed BGRA int32 via shift+OR, then stored directly.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ConvertI420P10ToBgraAvx2(
            ReadOnlySpan<byte> yPlaneBytes,
            ReadOnlySpan<byte> uPlaneBytes,
            ReadOnlySpan<byte> vPlaneBytes,
            int width, int height,
            int yStrideByte, int uvStrideByte,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            int yStride = yStrideByte / 2;
            int uvStride = uvStrideByte / 2;

            // 10-bit coefficients: R = (298*(Y-64) + crToR*(V-512) + 512) >> 10
            var vYFactor = Vector256.Create(298);
            var vCrToR = Vector256.Create(matrix.CrToRFixed >> 2);
            var vCrToG = Vector256.Create(matrix.CrToGFixed >> 2);
            var vCbToG = Vector256.Create(matrix.CbToGFixed >> 2);
            var vCbToB = Vector256.Create(matrix.CbToBFixed >> 2);
            var vRound = Vector256.Create(512);
            var vZero = Vector256<int>.Zero;
            var v255 = Vector256.Create(255);
            var vAlphaMask = Vector256.Create(unchecked((int)0xFF000000));
            var vYOffset = Vector256.Create(64);
            var vUvOffset = Vector256.Create(512);
            var vChromaDup = Vector256.Create(0, 0, 1, 1, 2, 2, 3, 3);

            fixed (byte* yBase = yPlaneBytes)
            fixed (byte* uBase = uPlaneBytes)
            fixed (byte* vBase = vPlaneBytes)
            fixed (byte* outBase = output)
            {
                ushort* yPtr = (ushort*)yBase;
                ushort* uPtr = (ushort*)uBase;
                ushort* vPtr = (ushort*)vBase;

                for (int row = 0; row < height; row++)
                {
                    ushort* yRow = yPtr + row * yStride;
                    ushort* uRow = uPtr + (row / 2) * uvStride;
                    ushort* vRow = vPtr + (row / 2) * uvStride;
                    byte* outRow = outBase + row * outputStride;

                    int col = 0;
                    int alignedWidth = width & ~7;

                    for (; col < alignedWidth; col += 8)
                    {
                        // Load 8 Y values (16-bit) → sign-extend to 32-bit
                        var y = Avx2.ConvertToVector256Int32(
                            Sse2.LoadVector128((short*)(yRow + col)));
                        y = Avx2.Max(Avx2.Subtract(y, vYOffset), vZero);
                        var yScaled = Avx2.MultiplyLow(vYFactor, y);

                        // Load 4 U and 4 V values, widen to 32-bit, duplicate for chroma upsampling
                        int uvCol = col / 2;
                        var u4 = Sse41.ConvertToVector128Int32(
                            Vector128.CreateScalarUnsafe(*(long*)(uRow + uvCol)).AsInt16());
                        var v4 = Sse41.ConvertToVector128Int32(
                            Vector128.CreateScalarUnsafe(*(long*)(vRow + uvCol)).AsInt16());

                        // [u0,u1,u2,u3] → [u0,u0,u1,u1,u2,u2,u3,u3]
                        var u = Avx2.Subtract(
                            Avx2.PermuteVar8x32(Vector256.Create(u4, u4), vChromaDup),
                            vUvOffset);
                        var v = Avx2.Subtract(
                            Avx2.PermuteVar8x32(Vector256.Create(v4, v4), vChromaDup),
                            vUvOffset);

                        // Color conversion in int32
                        var r = Avx2.ShiftRightArithmetic(
                            Avx2.Add(Avx2.Add(yScaled, Avx2.MultiplyLow(vCrToR, v)), vRound), 10);
                        var g = Avx2.ShiftRightArithmetic(
                            Avx2.Add(Avx2.Add(Avx2.Add(yScaled,
                                Avx2.MultiplyLow(vCrToG, v)),
                                Avx2.MultiplyLow(vCbToG, u)), vRound), 10);
                        var b = Avx2.ShiftRightArithmetic(
                            Avx2.Add(Avx2.Add(yScaled, Avx2.MultiplyLow(vCbToB, u)), vRound), 10);

                        // Clamp to [0, 255]
                        r = Avx2.Min(Avx2.Max(r, vZero), v255);
                        g = Avx2.Min(Avx2.Max(g, vZero), v255);
                        b = Avx2.Min(Avx2.Max(b, vZero), v255);

                        // Pack as BGRA pixels: B | (G<<8) | (R<<16) | (0xFF<<24)
                        var bgra = Avx2.Or(Avx2.Or(Avx2.Or(
                            b,
                            Avx2.ShiftLeftLogical(g, 8)),
                            Avx2.ShiftLeftLogical(r, 16)),
                            vAlphaMask);

                        Avx.Store((int*)(outRow + col * 4), bgra);
                    }

                    // Scalar fallback for remaining pixels
                    for (; col < width; col++)
                    {
                        int uvCol = col / 2;
                        int yVal = yRow[col] - 64;
                        if (yVal < 0) yVal = 0;
                        int uVal = uRow[uvCol] - 512;
                        int vVal = vRow[uvCol] - 512;
                        int crToR = matrix.CrToRFixed >> 2;
                        int crToG = matrix.CrToGFixed >> 2;
                        int cbToG = matrix.CbToGFixed >> 2;
                        int cbToB = matrix.CbToBFixed >> 2;
                        int ri = (298 * yVal + crToR * vVal + 512) >> 10;
                        int gi = (298 * yVal + crToG * vVal + cbToG * uVal + 512) >> 10;
                        int bi = (298 * yVal + cbToB * uVal + 512) >> 10;
                        outRow[col * 4 + 0] = ClampToByte(bi);
                        outRow[col * 4 + 1] = ClampToByte(gi);
                        outRow[col * 4 + 2] = ClampToByte(ri);
                        outRow[col * 4 + 3] = 255;
                    }
                }
            }
        }

        /// <summary>
        /// Scalar fallback for 10-bit I420 → BGRA conversion.
        /// </summary>
        private static unsafe void ConvertI420P10ToBgraScalar(
            ReadOnlySpan<byte> yPlaneBytes,
            ReadOnlySpan<byte> uPlaneBytes,
            ReadOnlySpan<byte> vPlaneBytes,
            int width, int height,
            int yStrideByte, int uvStrideByte,
            Span<byte> output,
            int outputStride,
            in ColorMatrix matrix)
        {
            int yStride = yStrideByte / 2;
            int uvStride = uvStrideByte / 2;
            var yPlane = MemoryMarshal.Cast<byte, ushort>(yPlaneBytes);
            var uPlane = MemoryMarshal.Cast<byte, ushort>(uPlaneBytes);
            var vPlane = MemoryMarshal.Cast<byte, ushort>(vPlaneBytes);

            int crToR = matrix.CrToRFixed >> 2;
            int crToG = matrix.CrToGFixed >> 2;
            int cbToG = matrix.CbToGFixed >> 2;
            int cbToB = matrix.CbToBFixed >> 2;

            fixed (ushort* yPtr = yPlane)
            fixed (ushort* uPtr = uPlane)
            fixed (ushort* vPtr = vPlane)
            fixed (byte* outPtr = output)
            {
                for (int row = 0; row < height; row++)
                {
                    ushort* yRow = yPtr + row * yStride;
                    ushort* uRow = uPtr + (row / 2) * uvStride;
                    ushort* vRow = vPtr + (row / 2) * uvStride;
                    byte* outRow = outPtr + row * outputStride;

                    for (int col = 0; col < width; col++)
                    {
                        int uvCol = col / 2;
                        int yVal = yRow[col] - 64;
                        if (yVal < 0) yVal = 0;
                        int uVal = uRow[uvCol] - 512;
                        int vVal = vRow[uvCol] - 512;
                        int r = (298 * yVal + crToR * vVal + 512) >> 10;
                        int g = (298 * yVal + crToG * vVal + cbToG * uVal + 512) >> 10;
                        int b = (298 * yVal + cbToB * uVal + 512) >> 10;
                        outRow[col * 4 + 0] = ClampToByte(b);
                        outRow[col * 4 + 1] = ClampToByte(g);
                        outRow[col * 4 + 2] = ClampToByte(r);
                        outRow[col * 4 + 3] = 255;
                    }
                }
            }
        }
    }
}

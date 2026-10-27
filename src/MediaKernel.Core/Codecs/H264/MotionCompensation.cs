using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Motion compensation for H.264 inter-prediction.
    /// Implements quarter-pixel interpolation for luma and eighth-pixel for chroma.
    /// </summary>
    public static class MotionCompensation
    {
        /// <summary>
        /// 6-tap interpolation filter coefficients for half-pixel positions.
        /// H.264 uses {1, -5, 20, 20, -5, 1} / 32.
        /// </summary>
        private static ReadOnlySpan<short> HalfPixelFilter => new short[] { 1, -5, 20, 20, -5, 1 };

        // Temporary diagnostic: dump MC computation for a specific block
        public static System.IO.StringWriter? McDiagnostic;
        public static (int bx, int by, int mvx, int mvy)? McDiagTarget;
        
        /// <summary>
        /// Performs motion compensation for a 4x4 luma block.
        /// </summary>
        /// <param name="reference">Reference frame luma plane.</param>
        /// <param name="refStride">Stride of reference frame in bytes.</param>
        /// <param name="output">Output prediction block (16 bytes).</param>
        /// <param name="mvX">Horizontal motion vector in quarter-pixel units.</param>
        /// <param name="mvY">Vertical motion vector in quarter-pixel units.</param>
        /// <param name="blockX">Block X position in pixels.</param>
        /// <param name="blockY">Block Y position in pixels.</param>
        /// <param name="frameWidth">Reference frame width.</param>
        /// <param name="frameHeight">Reference frame height.</param>
        public static void Compensate4x4Luma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight,
            int outStride = 4)
        {
            // Calculate full-pixel and fractional parts
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            // Edge extension is handled per-pixel in each interpolation path
            // via Math.Clamp(pos, 0, dim-1). Do NOT clamp fullX/fullY here,
            // as that shifts the interpolation window and produces wrong filter
            // tap positions when MVs point past the frame boundary.

            if (fracX == 0 && fracY == 0)
            {
                // Integer position - direct copy
                CopyBlock4x4(reference, refStride, output, fullX, fullY, frameWidth, frameHeight, outStride);
            }
            else if (fracX == 0)
            {
                // Vertical half-pixel only
                InterpolateVertical4x4(reference, refStride, output, fullX, fullY, fracY, frameWidth, frameHeight, outStride);
            }
            else if (fracY == 0)
            {
                // Horizontal half-pixel only
                InterpolateHorizontal4x4(reference, refStride, output, fullX, fullY, fracX, frameWidth, frameHeight, outStride);
            }
            else
            {
                // Both horizontal and vertical interpolation needed
                // One implementation for every size: a 4x4 block is just the narrowest partition.
                InterpolateDiagonalBlock(reference, refStride, output, fullX, fullY, fracX, fracY,
                    frameWidth, frameHeight, 4, 4, outStride);
            }
        }

        /// <summary>
        /// Motion compensation for a whole luma partition, in one pass rather than as separate
        /// 4x4 blocks.
        /// </summary>
        /// <param name="outStride">Row stride of <paramref name="output"/>.</param>
        /// <remarks>
        /// This is the shape FFmpeg uses: motion compensation runs once per partition, and a
        /// skipped macroblock is a single 16x16 partition. Doing it as sixteen 4x4 operations
        /// pays sixteen lots of block setup and reference lookup, and - more expensively - forces
        /// the prediction to be blended and written out in sixteen pieces too.
        ///
        /// The interpolation itself is still the proven per-4x4 code, called with a stride so it
        /// writes straight into the partition buffer. Widening the filter windows themselves is a
        /// separate change with its own measurement.
        /// </remarks>
        public static void CompensateLuma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int width,
            int height,
            int frameWidth,
            int frameHeight)
        {
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            // By far the most common case, and it was taking the slowest path. Measured on a 1080p
            // stream: 2.4 million pixels a frame land here - more than a whole luma plane, because
            // bi-prediction compensates twice - against 98 thousand that need the two-dimensional
            // filter. Tiling it as sixteen 4x4 copies meant sixty-four four-byte copies for a 16x16
            // partition; whole rows are one copy each.
            if (fracX == 0 && fracY == 0
                && fullX >= 0 && fullY >= 0
                && fullX + width <= frameWidth && fullY + height <= frameHeight)
            {
                Dsp.ReconstructDsp.CopyRows(
                    reference, fullY * refStride + fullX, refStride,
                    output, 0, outStride, width, height);

                return;
            }

            // An integer position that hangs off the edge of the frame: the row-copy above
            // could not take it, and there is no filter to run - every sample is just clamped
            // back into the picture. Reaching the horizontal filter here would apply a six-tap
            // filter to a whole-pixel position, which is what the removed 4x4 tiling loop used
            // to prevent by dispatching on the fractions again for every block.
            if (fracX == 0 && fracY == 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1) * refStride;
                    for (int col = 0; col < width; col++)
                        output[row * outStride + col] =
                            reference[srcY + Math.Clamp(fullX + col, 0, frameWidth - 1)];
                }

                return;
            }

            // Every sub-pel position is now filtered once for the whole partition. The cost of
            // all three falls with width: the bounds test is paid once, and the horizontal
            // kernel needs eight lanes before it engages at all, which a 4x4 block cannot give.
            if (fracX != 0 && fracY != 0)
            {
                InterpolateDiagonalBlock(
                    reference, refStride, output, fullX, fullY, fracX, fracY,
                    frameWidth, frameHeight, width, height, outStride);
                return;
            }

            if (fracY == 0)
            {
                InterpolateHorizontalBlock(
                    reference, refStride, output, fullX, fullY, fracX,
                    frameWidth, frameHeight, width, height, outStride);
                return;
            }

            InterpolateVerticalBlock(
                reference, refStride, output, fullX, fullY, fracY,
                frameWidth, frameHeight, width, height, outStride);


        }

        /// <summary>
        /// Performs motion compensation for chroma (Cb or Cr) 4x4 block.
        /// Chroma uses bilinear interpolation (eighth-pixel precision).
        /// </summary>
        /// <summary>
        /// Chroma motion compensation for a whole partition, in one pass.
        /// </summary>
        /// <remarks>
        /// A skipped macroblock's chroma is 8x8 per plane. Running it as sixteen 2x2 operations
        /// meant sixteen lots of setup - fractional position, bounds test, weight products - to
        /// produce four pixels each. FFmpeg does one call per partition per plane; so does this.
        ///
        /// Bilinear, so the window is one sample wider and taller than the block. Inside the plane
        /// the sample to the right is the next output's left sample, and the row below is one
        /// stride on, so each row reads width+1 samples rather than four per output pixel.
        /// </remarks>
        public static void CompensateChroma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int width,
            int height,
            int planeWidth,
            int planeHeight)
        {
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            // Both fractions zero means the bilinear weights collapse to "take the sample", so the
            // general path below spends four multiplies and a shift per pixel computing a copy.
            // Chroma motion vectors are the luma ones, so this happens whenever the luma vector is
            // a multiple of eight - which on this stream is a large share of 1.4 million chroma
            // pixels a frame.
            if (fracX == 0 && fracY == 0
                && fullX >= 0 && fullY >= 0
                && fullX + width <= planeWidth && fullY + height <= planeHeight)
            {
                Dsp.ReconstructDsp.CopyRows(
                    reference, fullY * refStride + fullX, refStride,
                    output, 0, outStride, width, height);

                return;
            }

            int ax = 8 - fracX, ay = 8 - fracY;
            int bx = fracX, by = fracY;
            int axay = ax * ay, bxay = bx * ay, axby = ax * by, bxby = bx * by;

            if (fullX >= 0 && fullY >= 0
                && fullX + width <= planeWidth - 1 && fullY + height <= planeHeight - 1)
            {
                // A whole partition is eight or four wide, and every one of its samples is
                // computed - there is no early exit for the vector path to lose against. The
                // four-wide case is a third of chroma motion compensation and used to fall to the
                // scalar loop below for want of a kernel its size.
                if (Dsp.H264InterpolationDsp.IsChromaBilinearSupported)
                {
                    if (width == 8)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            int upper = (fullY + y) * refStride + fullX;
                            Dsp.H264InterpolationDsp.BilinearChromaRow8(
                                reference, upper, upper + refStride,
                                output, y * outStride, axay, bxay, axby, bxby);
                        }

                        return;
                    }

                    if (width == 4)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            int upper = (fullY + y) * refStride + fullX;
                            Dsp.H264InterpolationDsp.BilinearChromaRow4(
                                reference, upper, upper + refStride,
                                output, y * outStride, axay, bxay, axby, bxby);
                        }

                        return;
                    }
                }

                for (int y = 0; y < height; y++)
                {
                    int upper = (fullY + y) * refStride + fullX;
                    int lower = upper + refStride;

                    int a = reference[upper];
                    int c = reference[lower];

                    for (int x = 0; x < width; x++)
                    {
                        int b = reference[upper + x + 1];
                        int d = reference[lower + x + 1];

                        output[y * outStride + x] =
                            (byte)((axay * a + bxay * b + axby * c + bxby * d + 32) >> 6);

                        a = b;
                        c = d;
                    }
                }

                return;
            }

            // Crosses the edge of the plane, so every sample is clamped.
            for (int y = 0; y < height; y++)
            {
                int srcY = Math.Clamp(fullY + y, 0, planeHeight - 1);
                int srcYNext = Math.Clamp(fullY + y + 1, 0, planeHeight - 1);

                for (int x = 0; x < width; x++)
                {
                    int srcX = Math.Clamp(fullX + x, 0, planeWidth - 1);
                    int srcXNext = Math.Clamp(fullX + x + 1, 0, planeWidth - 1);

                    int a = reference[srcY * refStride + srcX];
                    int b = reference[srcY * refStride + srcXNext];
                    int c = reference[srcYNext * refStride + srcX];
                    int d = reference[srcYNext * refStride + srcXNext];

                    output[y * outStride + x] =
                        (byte)((axay * a + bxay * b + axby * c + bxby * d + 32) >> 6);
                }
            }
        }

        public static void Compensate4x4Chroma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int planeWidth,
            int planeHeight)
        {
            // Luma quarter-pixel MV maps directly to chroma eighth-pixel precision (4:2:0)
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            // Bilinear interpolation weights
            int ax = 8 - fracX;
            int ay = 8 - fracY;
            int bx = fracX;
            int by = fracY;

            // As in the 2x2 case: inside the plane no clamp can bite, and the row below is simply
            // one stride further on. Each row then reads its five samples once instead of eight
            // times, and the sample shared with the next output column is carried across.
            if (fullX >= 0 && fullY >= 0 && fullX + 4 <= planeWidth - 1 && fullY + 4 <= planeHeight - 1)
            {
                int axay = ax * ay, bxay = bx * ay, axby = ax * by, bxby = bx * by;

                for (int y = 0; y < 4; y++)
                {
                    int upper = (fullY + y) * refStride + fullX;
                    int lower = upper + refStride;

                    int a = reference[upper];
                    int c = reference[lower];

                    for (int x = 0; x < 4; x++)
                    {
                        int b = reference[upper + x + 1];
                        int d = reference[lower + x + 1];

                        output[y * 4 + x] = (byte)((axay * a + bxay * b + axby * c + bxby * d + 32) >> 6);

                        // The right-hand sample of this column is the left-hand one of the next.
                        a = b;
                        c = d;
                    }
                }

                return;
            }

            for (int y = 0; y < 4; y++)
            {
                int srcY = Math.Clamp(fullY + y, 0, planeHeight - 1);
                int srcYNext = Math.Clamp(fullY + y + 1, 0, planeHeight - 1);

                for (int x = 0; x < 4; x++)
                {
                    int srcX = Math.Clamp(fullX + x, 0, planeWidth - 1);
                    int srcXNext = Math.Clamp(fullX + x + 1, 0, planeWidth - 1);

                    // Bilinear interpolation: (ax*ay*A + bx*ay*B + ax*by*C + bx*by*D + 32) >> 6
                    int a = reference[srcY * refStride + srcX];
                    int b = reference[srcY * refStride + srcXNext];
                    int c = reference[srcYNext * refStride + srcX];
                    int d = reference[srcYNext * refStride + srcXNext];

                    int result = (ax * ay * a + bx * ay * b + ax * by * c + bx * by * d + 32) >> 6;
                    output[y * 4 + x] = (byte)result;
                }
            }
        }

        /// <summary>
        /// Performs motion compensation for a 2x2 chroma sub-block.
        /// Used when sub-8x8 luma partitions have different MVs per 4x4 block,
        /// requiring independent chroma MC for each 2x2 region within the 4x4 chroma block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Compensate2x2Chroma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int planeWidth,
            int planeHeight)
        {
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            int ax = 8 - fracX;
            int ay = 8 - fracY;
            int bx = fracX;
            int by = fracY;

            // The bilinear window for a 2x2 block is the 3x3 samples at fullX/fullY. When all of
            // it is inside the plane no clamp can bite, and the nine samples can be read once
            // rather than sixteen times - each interior sample is shared by two or four of the
            // four outputs, and the general path below re-reads and re-clamps it every time.
            if (fullX >= 0 && fullY >= 0 && fullX + 2 <= planeWidth - 1 && fullY + 2 <= planeHeight - 1)
            {
                int row0 = fullY * refStride + fullX;
                int row1 = row0 + refStride;
                int row2 = row1 + refStride;

                int s00 = reference[row0], s01 = reference[row0 + 1], s02 = reference[row0 + 2];
                int s10 = reference[row1], s11 = reference[row1 + 1], s12 = reference[row1 + 2];
                int s20 = reference[row2], s21 = reference[row2 + 1], s22 = reference[row2 + 2];

                int axay = ax * ay, bxay = bx * ay, axby = ax * by, bxby = bx * by;

                output[0] = (byte)((axay * s00 + bxay * s01 + axby * s10 + bxby * s11 + 32) >> 6);
                output[1] = (byte)((axay * s01 + bxay * s02 + axby * s11 + bxby * s12 + 32) >> 6);
                output[2] = (byte)((axay * s10 + bxay * s11 + axby * s20 + bxby * s21 + 32) >> 6);
                output[3] = (byte)((axay * s11 + bxay * s12 + axby * s21 + bxby * s22 + 32) >> 6);
                return;
            }

            for (int y = 0; y < 2; y++)
            {
                int srcY = Math.Clamp(fullY + y, 0, planeHeight - 1);
                int srcYNext = Math.Clamp(fullY + y + 1, 0, planeHeight - 1);

                for (int x = 0; x < 2; x++)
                {
                    int srcX = Math.Clamp(fullX + x, 0, planeWidth - 1);
                    int srcXNext = Math.Clamp(fullX + x + 1, 0, planeWidth - 1);

                    int a = reference[srcY * refStride + srcX];
                    int b = reference[srcY * refStride + srcXNext];
                    int c = reference[srcYNext * refStride + srcX];
                    int d = reference[srcYNext * refStride + srcXNext];

                    int result = (ax * ay * a + bx * ay * b + ax * by * c + bx * by * d + 32) >> 6;
                    output[y * 2 + x] = (byte)result;
                }
            }
        }

        /// <summary>
        /// Performs bi-directional prediction for B-slices.
        /// </summary>
        /// <param name="predL0">Prediction from L0 reference (16 bytes for 4x4).</param>
        /// <param name="predL1">Prediction from L1 reference (16 bytes for 4x4).</param>
        /// <param name="output">Output bi-predicted block (16 bytes).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>Averages two predictions straight into the frame.</summary>
        /// <remarks>
        /// The pair was averaged into a third scratch buffer and that buffer then copied to the
        /// picture: a write and a read of 256 bytes per partition that nothing needs. Writing the
        /// average where it belongs costs the frame exactly what the copy did.
        /// <para>
        /// Note this is not FFmpeg's shape, which is to interpolate and average in one pass with
        /// no scratch at all. Going halfway to that - compensating list zero directly into the
        /// frame and averaging list one over it - measured *worse*, because it triples the frame
        /// traffic while still materialising a temporary. Their design only pays whole.
        /// </para>
        /// </remarks>
        public static void BiPredictionToFrame(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> destination,
            int destinationOffset,
            int destinationStride,
            int size)
        {
            for (int row = 0; row < size; row++)
            {
                Span<byte> target = destination.Slice(destinationOffset + row * destinationStride, size);
                ReadOnlySpan<byte> a = predL0.Slice(row * size, size);
                ReadOnlySpan<byte> b = predL1.Slice(row * size, size);
                int at = 0;

                if (Sse2.IsSupported)
                {
                    for (; at + 16 <= size; at += 16)
                    {
                        Sse2.Average(Vector128.Create(a.Slice(at, 16)),
                                     Vector128.Create(b.Slice(at, 16)))
                            .CopyTo(target.Slice(at, 16));
                    }
                }

                for (; at < size; at++)
                    target[at] = (byte)((a[at] + b[at] + 1) >> 1);
            }
        }

        public static void BiPrediction4x4(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output)
        {
            // Length-generic: a whole partition is blended in one call, so this is no longer
            // always sixteen samples. The averaging is (a + b + 1) >> 1, which is what PAVGB
            // computes, so a full vector is one instruction.
            int count = Math.Min(predL0.Length, Math.Min(predL1.Length, output.Length));
            int at = 0;

            if (Avx2.IsSupported)
            {
                for (; at + 32 <= count; at += 32)
                {
                    Avx2.Average(Vector256.Create(predL0.Slice(at, 32)),
                                 Vector256.Create(predL1.Slice(at, 32)))
                        .CopyTo(output.Slice(at, 32));
                }
            }

            if (Sse2.IsSupported)
            {
                for (; at + 16 <= count; at += 16)
                {
                    Sse2.Average(Vector128.Create(predL0.Slice(at, 16)),
                                 Vector128.Create(predL1.Slice(at, 16)))
                        .CopyTo(output.Slice(at, 16));
                }
            }

            for (; at < count; at++)
                output[at] = (byte)((predL0[at] + predL1[at] + 1) >> 1);
        }

        /// <summary>
        /// SSE2-optimized bi-prediction.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void BiPrediction4x4Sse2(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output)
        {
            fixed (byte* pL0 = predL0)
            fixed (byte* pL1 = predL1)
            fixed (byte* pOut = output)
            {
                var l0 = Sse2.LoadVector128(pL0);
                var l1 = Sse2.LoadVector128(pL1);

                // Average with rounding using Sse2.Average
                var avg = Sse2.Average(l0, l1);

                Sse2.Store(pOut, avg);
            }
        }

        /// <summary>
        /// Copies a 4x4 block at integer position.
        /// </summary>
        private static void CopyBlock4x4(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int frameWidth,
            int frameHeight,
            int outStride = 4)
        {
            // Inside the frame the four samples of a row are contiguous, so each row is one copy
            // rather than four clamped indexings.
            if (x >= 0 && y >= 0 && x + 4 <= frameWidth && y + 4 <= frameHeight)
            {
                for (int row = 0; row < 4; row++)
                {
                    reference.Slice((y + row) * refStride + x, 4)
                             .CopyTo(output.Slice(row * outStride, 4));
                }

                return;
            }

            for (int row = 0; row < 4; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                for (int col = 0; col < 4; col++)
                {
                    int srcX = Math.Clamp(x + col, 0, frameWidth - 1);
                    output[row * outStride + col] = reference[srcY * refStride + srcX];
                }
            }
        }

        /// <summary>
        /// Horizontal half-pixel interpolation for 4x4 block.
        /// H.264 Section 8.4.2.2.1: Uses 6-tap filter for half-pixel,
        /// then averages with integer pixel for quarter-pixel positions.
        /// </summary>
        /// <summary>
        /// Horizontal-only interpolation for a whole partition.
        /// </summary>
        /// <remarks>
        /// A sixteen-wide partition at a horizontal-only sub-pel position used to be filtered as
        /// sixteen separate 4x4 blocks, each redoing the bounds test and each too narrow for the
        /// vector kernel, which needs eight lanes before it engages. A whole row is one call now,
        /// through the same vectorised six-tap filter the diagonal path uses.
        /// </remarks>
        private static void InterpolateHorizontalBlock(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int fracX,
            int frameWidth,
            int frameHeight,
            int width,
            int height,
            int outStride)
        {
            // The window is x-2 through x+width+2 across - six taps per output plus the
            // quarter-pixel sample one to the right - and y through y+height-1 down.
            bool interior = x >= 2 && x + width + 2 <= frameWidth - 1
                         && y >= 0 && y + height - 1 <= frameHeight - 1;

            // The kernel below is AVX2; without it the clamped scalar path further down
            // produces the same samples. Three other call sites already ask this;
            // these two did not, and threw PlatformNotSupportedException instead.
            if (interior && Dsp.H264InterpolationDsp.IsSixTapRowSupported)
            {
                // The same six-tap kernel as the vertical case, with its taps one sample apart
                // instead of a row apart. It rounds, clips and averages inside the vector, so
                // the intermediate sums never reach memory.
                for (int row = 0; row < height; row++)
                {
                    int rowBase = (y + row) * refStride;
                    int integerBase = fracX == 2
                        ? -1
                        : rowBase + x + (fracX == 3 ? 1 : 0);

                    Dsp.H264InterpolationDsp.SixTapRow(
                        reference, rowBase + x - 2, 1, integerBase,
                        output, row * outStride, width);
                }
                return;
            }

            // Off the edge of the frame: every tap is clamped back into it.
            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1) * refStride;

                for (int col = 0; col < width; col++)
                {
                    int halfSum = 0;
                    for (int k = 0; k < 6; k++)
                        halfSum += HalfPixelFilter[k]
                                 * reference[srcY + Math.Clamp(x + col - 2 + k, 0, frameWidth - 1)];

                    int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);
                    if (fracX == 2)
                    {
                        output[row * outStride + col] = (byte)halfPixel;
                    }
                    else
                    {
                        int integerX = Math.Clamp(fracX == 1 ? x + col : x + col + 1, 0, frameWidth - 1);
                        output[row * outStride + col] =
                            (byte)((reference[srcY + integerX] + halfPixel + 1) >> 1);
                    }
                }
            }
        }

        /// <summary>
        /// Vertical-only interpolation for a whole partition.
        /// </summary>
        /// <remarks>
        /// The 4x4 version walks columns outermost, so every step of its inner loop jumps a whole
        /// row of the reference frame. Going row by row instead reads the six source rows once per
        /// output row, and consecutive output rows share five of them.
        /// </remarks>
        private static void InterpolateVerticalBlock(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int fracY,
            int frameWidth,
            int frameHeight,
            int width,
            int height,
            int outStride)
        {
            // The window is x through x+width-1 across, and y-2 through y+height+2 down.
            bool interior = x >= 0 && x + width - 1 <= frameWidth - 1
                         && y >= 2 && y + height + 2 <= frameHeight - 1;

            // The kernel below is AVX2; without it the clamped scalar path further down
            // produces the same samples. Three other call sites already ask this;
            // these two did not, and threw PlatformNotSupportedException instead.
            if (interior && Dsp.H264InterpolationDsp.IsSixTapRowSupported)
            {
                // Six taps of a vertical filter are six whole rows at the same columns, so each
                // is a contiguous run of bytes and a whole output row goes through one vector.
                for (int row = 0; row < height; row++)
                {
                    int tapBase = (y + row - 2) * refStride + x;
                    int integerBase = fracY == 2
                        ? -1
                        : (fracY == 1 ? y + row : y + row + 1) * refStride + x;

                    Dsp.H264InterpolationDsp.SixTapRow(
                        reference, tapBase, refStride, integerBase,
                        output, row * outStride, width);
                }
                return;
            }

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(x + col, 0, frameWidth - 1);

                    int halfSum = 0;
                    for (int k = 0; k < 6; k++)
                        halfSum += HalfPixelFilter[k]
                                 * reference[Math.Clamp(y + row - 2 + k, 0, frameHeight - 1) * refStride + srcX];

                    int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);
                    if (fracY == 2)
                    {
                        output[row * outStride + col] = (byte)halfPixel;
                    }
                    else
                    {
                        int integerY = Math.Clamp(fracY == 1 ? y + row : y + row + 1, 0, frameHeight - 1);
                        output[row * outStride + col] =
                            (byte)((reference[integerY * refStride + srcX] + halfPixel + 1) >> 1);
                    }
                }
            }
        }

        private static void InterpolateHorizontal4x4(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int fracX,
            int frameWidth,
            int frameHeight,
            int outStride = 4)
        {
            // The clamps below exist for blocks that hang off the edge of the frame, but they run
            // for every tap of every pixel - six per output sample - and the overwhelming majority
            // of blocks are nowhere near an edge. The same shape in the H.265 interpolation cost
            // 43% once an interior fast path was added.
            //
            // The source window for a 4x4 block with the 6-tap filter is x-2 through x+6
            // horizontally (col 0..3, k 0..5, plus the quarter-pixel integer sample at x+4) and
            // y through y+3 vertically. When all of that is inside the frame, no clamp can bite.
            bool interior = x >= 2 && x + 6 <= frameWidth - 1
                         && y >= 0 && y + 3 <= frameHeight - 1;

            if (interior)
            {
                for (int row = 0; row < 4; row++)
                {
                    int rowBase = (y + row) * refStride;

                    for (int col = 0; col < 4; col++)
                    {
                        int tapBase = rowBase + x + col - 2;
                        int halfSum = HalfPixelFilter[0] * reference[tapBase]
                                    + HalfPixelFilter[1] * reference[tapBase + 1]
                                    + HalfPixelFilter[2] * reference[tapBase + 2]
                                    + HalfPixelFilter[3] * reference[tapBase + 3]
                                    + HalfPixelFilter[4] * reference[tapBase + 4]
                                    + HalfPixelFilter[5] * reference[tapBase + 5];
                        int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);

                        if (fracX == 2)
                        {
                            output[row * outStride + col] = (byte)halfPixel;
                        }
                        else
                        {
                            int integerVal = reference[rowBase + (fracX == 1 ? x + col : x + col + 1)];
                            output[row * outStride + col] = (byte)((integerVal + halfPixel + 1) >> 1);
                        }
                    }
                }

                return;
            }

            for (int row = 0; row < 4; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1);

                for (int col = 0; col < 4; col++)
                {
                    // Always compute half-pixel (b) using 6-tap filter
                    int halfSum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        int srcX = Math.Clamp(x + col - 2 + k, 0, frameWidth - 1);
                        halfSum += HalfPixelFilter[k] * reference[srcY * refStride + srcX];
                    }
                    int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);

                    if (fracX == 2)
                    {
                        output[row * outStride + col] = (byte)halfPixel;
                    }
                    else
                    {
                        // Quarter-pixel: average of integer and half-pixel
                        int integerX = fracX == 1 ? x + col : x + col + 1;
                        integerX = Math.Clamp(integerX, 0, frameWidth - 1);
                        int integerVal = reference[srcY * refStride + integerX];
                        output[row * outStride + col] = (byte)((integerVal + halfPixel + 1) >> 1);
                    }
                }
            }
        }

        /// <summary>
        /// Vertical half-pixel interpolation for 4x4 block.
        /// H.264 Section 8.4.2.2.1: Uses 6-tap filter for half-pixel,
        /// then averages with integer pixel for quarter-pixel positions.
        /// </summary>
        private static void InterpolateVertical4x4(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int fracY,
            int frameWidth,
            int frameHeight,
            int outStride = 4)
        {
            // Interior fast path - see InterpolateHorizontal4x4. The window here is x through x+3
            // horizontally and y-2 through y+6 vertically (row 0..3, k 0..5, plus the
            // quarter-pixel integer sample at y+4).
            bool interior = x >= 0 && x + 3 <= frameWidth - 1
                         && y >= 2 && y + 6 <= frameHeight - 1;

            if (interior)
            {
                for (int col = 0; col < 4; col++)
                {
                    int colBase = x + col;

                    for (int row = 0; row < 4; row++)
                    {
                        int tapBase = (y + row - 2) * refStride + colBase;
                        int halfSum = HalfPixelFilter[0] * reference[tapBase]
                                    + HalfPixelFilter[1] * reference[tapBase + refStride]
                                    + HalfPixelFilter[2] * reference[tapBase + 2 * refStride]
                                    + HalfPixelFilter[3] * reference[tapBase + 3 * refStride]
                                    + HalfPixelFilter[4] * reference[tapBase + 4 * refStride]
                                    + HalfPixelFilter[5] * reference[tapBase + 5 * refStride];
                        int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);

                        if (fracY == 2)
                        {
                            output[row * outStride + col] = (byte)halfPixel;
                        }
                        else
                        {
                            int integerY = fracY == 1 ? y + row : y + row + 1;
                            output[row * outStride + col] = (byte)((reference[integerY * refStride + colBase] + halfPixel + 1) >> 1);
                        }
                    }
                }

                return;
            }

            for (int col = 0; col < 4; col++)
            {
                int srcX = Math.Clamp(x + col, 0, frameWidth - 1);

                for (int row = 0; row < 4; row++)
                {
                    // Always compute half-pixel (h) using 6-tap filter
                    int halfSum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        int srcY = Math.Clamp(y + row - 2 + k, 0, frameHeight - 1);
                        halfSum += HalfPixelFilter[k] * reference[srcY * refStride + srcX];
                    }
                    int halfPixel = Math.Clamp((halfSum + 16) >> 5, 0, 255);

                    if (fracY == 2)
                    {
                        output[row * outStride + col] = (byte)halfPixel;
                    }
                    else
                    {
                        // Quarter-pixel: average of integer and half-pixel
                        int integerY = fracY == 1 ? y + row : y + row + 1;
                        integerY = Math.Clamp(integerY, 0, frameHeight - 1);
                        int integerVal = reference[integerY * refStride + srcX];
                        output[row * outStride + col] = (byte)((integerVal + halfPixel + 1) >> 1);
                    }
                }
            }
        }

        /// <summary>
        /// Diagonal (both H and V) interpolation for 4x4 block.
        /// H.264 Section 8.4.2.2.1: Computes half-pixel values (b, h, j)
        /// and averages the appropriate pair for each sub-pixel position.
        /// </summary>

        /// <summary>Six-tap horizontal accumulation at (x+col), clamped only when it has to be.</summary>
        /// <remarks>
        /// The clamp guards blocks that hang off the edge of the picture, but it ran once per tap -
        /// six times per output sample - and almost no block is near an edge. The diagonal filter is
        /// the two-dimensional case and evaluates this nine times per output row, so it pays most.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int HorizontalTap6(
            ReadOnlySpan<byte> reference, int rowBase, int x, int col, int frameWidth, bool interiorX)
        {
            if (interiorX)
            {
                int b = rowBase + x + col - 2;
                return HalfPixelFilter[0] * reference[b]
                     + HalfPixelFilter[1] * reference[b + 1]
                     + HalfPixelFilter[2] * reference[b + 2]
                     + HalfPixelFilter[3] * reference[b + 3]
                     + HalfPixelFilter[4] * reference[b + 4]
                     + HalfPixelFilter[5] * reference[b + 5];
            }

            int sum = 0;
            for (int k = 0; k < 6; k++)
                sum += HalfPixelFilter[k] * reference[rowBase + Math.Clamp(x + col - 2 + k, 0, frameWidth - 1)];

            return sum;
        }


        /// <summary>
        /// The two-dimensional filter over a whole partition rather than a 4x4 block.
        /// </summary>
        /// <remarks>
        /// Identical arithmetic to the 4x4 form, with the loop bounds and scratch strides taken
        /// from the partition. Two things follow from the extra width. The horizontal pass runs
        /// once per row of the partition instead of once per row of each 4x4 block, so a 16x16
        /// partition costs 21 rows of taps rather than sixteen blocks of nine - 336 against 576.
        /// And sixteen samples is wide enough for the vector paths, which need eight before their
        /// loop can run at all and so never engaged from a 4x4 caller.
        /// </remarks>
        private static void InterpolateDiagonalBlock(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int x,
            int y,
            int fracX,
            int fracY,
            int frameWidth,
            int frameHeight,
            int width,
            int height,
            int outStride)
        {
            // Whether the 6-tap horizontal window - x-2 through x+6 - lies entirely inside the
            // frame. When it does, no clamp can bite and the taps are indexed directly.
            bool interiorX = x >= 2 && x + width + 2 <= frameWidth - 1;

            // The four-wide paths read the row directly, so they need the same interior
            // guarantee the scalar taps rely on - outside it, samples are clamped per tap and
            // the window is no longer six consecutive bytes.
            bool fourWide = interiorX && Dsp.H264InterpolationDsp.CanFilter4Wide;

            if (fracX == 2 && fracY == 2)
            {
                // Position j: two-pass 6-tap (horizontal then vertical)
                // Written in full by the horizontal pass below before anything reads it, so
                // it does not need clearing - and clearing it is not cheap: the buffer is
                // sized for the largest partition, 16 * (16 + 5) ints, while a 4x4 block uses
                // 36 of them. Zeroing all 1344 bytes for every diagonal interpolation was
                // measured at 45% of the entire decode thread.
                Unsafe.SkipInit(out Scratch.Of336<int> tempHStorage);
                Span<int> tempH = ((Span<int>)tempHStorage)[..(width * (height + 5))];  // 4 columns, 9 rows
                for (int row = -2; row < height + 3; row++)
                {
                    int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                    int tempRow = row + 2;
                    if (fourWide)
                    {
                        // Four consecutive outputs are four lanes; the window slides one sample
                        // per output, so this is six overlapping loads of the same row.
                        Dsp.H264InterpolationDsp.HorizontalTap6(
                            reference, srcY * refStride + x, tempH.Slice(tempRow * width, width), width);
                    }
                    else
                    {
                        for (int col = 0; col < width; col++)
                        {
                            int sum = HorizontalTap6(reference, srcY * refStride, x, col, frameWidth, interiorX);
                            tempH[tempRow * width + col] = sum;
                        }
                    }
                }
                // The vertical pass writes its bytes directly. It used to fill an int buffer -
                // stackalloc'd inside this loop, so once per row - and then round, clip and
                // store one column at a time.
                for (int row = 0; row < height; row++)
                {
                    Dsp.H264InterpolationDsp.VerticalTap6ToBytes(
                        tempH, width, row, output, row * outStride, width);
                }
                return;
            }

            if (fracX == 2)
            {
                // fracX=2, fracY=1 or 3: avg(j, horizontal_half)
                // fracY=1 → avg(j, b) where b = horizontal half at row y
                // fracY=3 → avg(j, s) where s = horizontal half at row y+1
                // Written in full by the horizontal pass below before anything reads it, so
                // it does not need clearing - and clearing it is not cheap: the buffer is
                // sized for the largest partition, 16 * (16 + 5) ints, while a 4x4 block uses
                // 36 of them. Zeroing all 1344 bytes for every diagonal interpolation was
                // measured at 45% of the entire decode thread.
                Unsafe.SkipInit(out Scratch.Of336<int> tempHStorage);
                Span<int> tempH = ((Span<int>)tempHStorage)[..(width * (height + 5))];
                // Likewise written in full by the vertical pass before it is read.
                Unsafe.SkipInit(out Scratch.Of256<byte> jValuesStorage);
                Span<byte> jValues = ((Span<byte>)jValuesStorage)[..(width * height)];
                for (int row = -2; row < height + 3; row++)
                {
                    int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                    int tempRow = row + 2;
                    if (fourWide)
                    {
                        // Four consecutive outputs are four lanes; the window slides one sample
                        // per output, so this is six overlapping loads of the same row.
                        Dsp.H264InterpolationDsp.HorizontalTap6(
                            reference, srcY * refStride + x, tempH.Slice(tempRow * width, width), width);
                    }
                    else
                    {
                        for (int col = 0; col < width; col++)
                        {
                            int sum = HorizontalTap6(reference, srcY * refStride, x, col, frameWidth, interiorX);
                            tempH[tempRow * width + col] = sum;
                        }
                    }
                }
                for (int row = 0; row < height; row++)
                {
                    Dsp.H264InterpolationDsp.VerticalTap6ToBytes(
                        tempH, width, row, jValues, row * width, width);
                }

                // Horizontal half-pixel at row y (fracY=1) or y+1 (fracY=3)
                int bRowOffset = fracY == 1 ? 0 : 1;

                // The second half-pel here is a plain horizontal filter, and combining it with
                // j is the rounding average. Both have kernels already; this branch was doing
                // the filter a column at a time and the average a pixel at a time.
                bool bRowsInside = interiorX
                    && y + bRowOffset >= 0 && y + height - 1 + bRowOffset <= frameHeight - 1;

                if (bRowsInside && Dsp.H264InterpolationDsp.IsSixTapRowSupported)
                {
                    Scratch.Of16<byte> bValuesStorage = default;
                    Span<byte> bValues = bValuesStorage;
                    for (int row = 0; row < height; row++)
                    {
                        Dsp.H264InterpolationDsp.SixTapRow(
                            reference, (y + row + bRowOffset) * refStride + x - 2, 1, -1,
                            bValues, 0, width);

                        BiPrediction4x4(
                            jValues.Slice(row * width, width), bValues[..width],
                            output.Slice(row * outStride, width));
                    }

                    return;
                }

                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(y + row + bRowOffset, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int halfSum = HorizontalTap6(reference, srcY * refStride, x, col, frameWidth, interiorX);
                        int bVal = Math.Clamp((halfSum + 16) >> 5, 0, 255);
                        output[row * outStride + col] = (byte)((jValues[row * width + col] + bVal + 1) >> 1);
                    }
                }
                return;
            }

            if (fracY == 2)
            {
                // fracX=1 or 3, fracY=2: avg(j, vertical_half)
                // fracX=1 → avg(j, h) where h = vertical half at col x
                // fracX=3 → avg(j, m) where m = vertical half at col x+1
                // Written in full by the horizontal pass below before anything reads it, so
                // it does not need clearing - and clearing it is not cheap: the buffer is
                // sized for the largest partition, 16 * (16 + 5) ints, while a 4x4 block uses
                // 36 of them. Zeroing all 1344 bytes for every diagonal interpolation was
                // measured at 45% of the entire decode thread.
                Unsafe.SkipInit(out Scratch.Of336<int> tempHStorage);
                Span<int> tempH = ((Span<int>)tempHStorage)[..(width * (height + 5))];
                // Likewise written in full by the vertical pass before it is read.
                Unsafe.SkipInit(out Scratch.Of256<byte> jValuesStorage);
                Span<byte> jValues = ((Span<byte>)jValuesStorage)[..(width * height)];
                for (int row = -2; row < height + 3; row++)
                {
                    int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                    int tempRow = row + 2;
                    if (fourWide)
                    {
                        // Four consecutive outputs are four lanes; the window slides one sample
                        // per output, so this is six overlapping loads of the same row.
                        Dsp.H264InterpolationDsp.HorizontalTap6(
                            reference, srcY * refStride + x, tempH.Slice(tempRow * width, width), width);
                    }
                    else
                    {
                        for (int col = 0; col < width; col++)
                        {
                            int sum = HorizontalTap6(reference, srcY * refStride, x, col, frameWidth, interiorX);
                            tempH[tempRow * width + col] = sum;
                        }
                    }
                }
                for (int row = 0; row < height; row++)
                {
                    Dsp.H264InterpolationDsp.VerticalTap6ToBytes(
                        tempH, width, row, jValues, row * width, width);
                }

                // Vertical half-pixel at col x (fracX=1) or col x+1 (fracX=3)
                int hColOffset = fracX == 1 ? 0 : 1;

                // The mirror of the branch above: a plain vertical filter averaged with j. The
                // scalar version walked columns outermost, so every step of its inner loop
                // jumped a whole row of the reference frame.
                bool hColsInside = x + hColOffset >= 0
                    && x + width - 1 + hColOffset <= frameWidth - 1
                    && y >= 2 && y + height + 2 <= frameHeight - 1;

                if (hColsInside && Dsp.H264InterpolationDsp.IsSixTapRowSupported)
                {
                    Scratch.Of16<byte> hValuesStorage = default;
                    Span<byte> hValues = hValuesStorage;
                    for (int row = 0; row < height; row++)
                    {
                        Dsp.H264InterpolationDsp.SixTapRow(
                            reference, (y + row - 2) * refStride + x + hColOffset, refStride, -1,
                            hValues, 0, width);

                        BiPrediction4x4(
                            jValues.Slice(row * width, width), hValues[..width],
                            output.Slice(row * outStride, width));
                    }

                    return;
                }

                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(x + col + hColOffset, 0, frameWidth - 1);
                    for (int row = 0; row < height; row++)
                    {
                        int halfSum = 0;
                        for (int k = 0; k < 6; k++)
                        {
                            int srcY = Math.Clamp(y + row - 2 + k, 0, frameHeight - 1);
                            halfSum += HalfPixelFilter[k] * reference[srcY * refStride + srcX];
                        }
                        int hVal = Math.Clamp((halfSum + 16) >> 5, 0, 255);
                        output[row * outStride + col] = (byte)((jValues[row * width + col] + hVal + 1) >> 1);
                    }
                }
                return;
            }

            // Both fracX and fracY are quarter-pixel (1 or 3)
            // (1,1)=avg(b,h), (3,1)=avg(b,m), (1,3)=avg(s,h), (3,3)=avg(s,m)
            // b = horizontal half at row y, s = horizontal half at row y+1
            // h = vertical half at col x, m = vertical half at col x+1
            int bRowOff = fracY == 1 ? 0 : 1;   // shift b down for fracY=3
            int hColOff = fracX == 1 ? 0 : 1;    // shift h right for fracX=3
            
            bool diag = McDiagTarget.HasValue && McDiagnostic != null
                && McDiagTarget.Value.bx == x - (x - (McDiagTarget.Value.bx + (McDiagTarget.Value.mvx >> 2)))
                && McDiagTarget.Value.by == y - (y - (McDiagTarget.Value.by + (McDiagTarget.Value.mvy >> 2)));
            // Simpler: check if this is the target block's MC position
            if (McDiagnostic != null && McDiagTarget.HasValue)
            {
                int tFullX = McDiagTarget.Value.bx + (McDiagTarget.Value.mvx >> 2);
                int tFullY = McDiagTarget.Value.by + (McDiagTarget.Value.mvy >> 2);
                diag = (x == tFullX && y == tFullY);
            }
            
            if (diag)
            {
                McDiagnostic!.WriteLine($"MC-DIAG: x={x} y={y} fracX={fracX} fracY={fracY} bRowOff={bRowOff} hColOff={hColOff} refStride={refStride} fW={frameWidth} fH={frameHeight}");
                for (int dy = -3; dy <= 5; dy++)
                {
                    int ry = Math.Clamp(y + dy, 0, frameHeight - 1);
                    McDiagnostic.Write($"  ref row {ry} (dy={dy}): ");
                    for (int dx = -3; dx <= 5; dx++)
                    {
                        int rx = Math.Clamp(x + dx, 0, frameWidth - 1);
                        McDiagnostic.Write($"{reference[ry * refStride + rx],4}");
                    }
                    McDiagnostic.WriteLine();
                }
            }
            
            // Both halves are plain six-tap filters - one along the row, one down the column -
            // and the result is their rounding average. The loop below computes each of them a
            // pixel at a time, and the vertical one re-reads six rows for every column.
            bool bothInside = interiorX
                && y + bRowOff >= 0 && y + height - 1 + bRowOff <= frameHeight - 1
                && x + hColOff >= 0 && x + width - 1 + hColOff <= frameWidth - 1
                && y >= 2 && y + height + 2 <= frameHeight - 1;

            // The scalar path stays for the diagnostic, which reports per pixel.
            if (bothInside && !diag && Dsp.H264InterpolationDsp.IsSixTapRowSupported)
            {
                Scratch.Of16<byte> bValuesStorage = default;
                Span<byte> bValues = bValuesStorage;
                Scratch.Of16<byte> hValuesStorage = default;
                Span<byte> hValues = hValuesStorage;

                for (int row = 0; row < height; row++)
                {
                    Dsp.H264InterpolationDsp.SixTapRow(
                        reference, (y + row + bRowOff) * refStride + x - 2, 1, -1,
                        bValues, 0, width);

                    Dsp.H264InterpolationDsp.SixTapRow(
                        reference, (y + row - 2) * refStride + x + hColOff, refStride, -1,
                        hValues, 0, width);

                    BiPrediction4x4(
                        bValues[..width], hValues[..width], output.Slice(row * outStride, width));
                }

                return;
            }

            for (int row = 0; row < height; row++)
            {
                int bSrcY = Math.Clamp(y + row + bRowOff, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    // Compute horizontal half-pixel (b or s)
                    int bSum = HorizontalTap6(reference, bSrcY * refStride, x, col, frameWidth, interiorX);
                    int bVal = Math.Clamp((bSum + 16) >> 5, 0, 255);

                    // Compute vertical half-pixel (h or m)
                    int hSrcX = Math.Clamp(x + col + hColOff, 0, frameWidth - 1);
                    int hSum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        int srcY = Math.Clamp(y + row - 2 + k, 0, frameHeight - 1);
                        hSum += HalfPixelFilter[k] * reference[srcY * refStride + hSrcX];
                    }
                    int hVal = Math.Clamp((hSum + 16) >> 5, 0, 255);

                    if (diag && row == 0)
                    {
                        McDiagnostic!.Write($"  [{row},{col}] bSrcY={bSrcY} hSrcX={hSrcX}");
                        McDiagnostic.Write($" bPix=[");
                        for (int k = 0; k < 6; k++)
                        {
                            int sx = Math.Clamp(x + col - 2 + k, 0, frameWidth - 1);
                            McDiagnostic.Write($"{reference[bSrcY * refStride + sx]}{(k<5?",":"")}");
                        }
                        McDiagnostic.Write($"] bSum={bSum} bVal={bVal}");
                        McDiagnostic.Write($" hPix=[");
                        for (int k = 0; k < 6; k++)
                        {
                            int sy = Math.Clamp(y + row - 2 + k, 0, frameHeight - 1);
                            McDiagnostic.Write($"{reference[sy * refStride + hSrcX]}{(k<5?",":"")}");
                        }
                        McDiagnostic.WriteLine($"] hSum={hSum} hVal={hVal} mc={((bVal + hVal + 1) >> 1)}");
                    }

                    output[row * outStride + col] = (byte)((bVal + hVal + 1) >> 1);
                }
            }
        }

        /// <summary>
        /// Weighted prediction for B-slice advanced mode.
        /// </summary>
        /// <param name="pred">Prediction samples.</param>
        /// <param name="output">Output samples.</param>
        /// <param name="weight">Weight factor.</param>
        /// <param name="offset">DC offset.</param>
        /// <param name="logWeightDenom">Log2 of weight denominator.</param>
        public static void WeightedPrediction(
            ReadOnlySpan<byte> pred,
            Span<byte> output,
            int weight,
            int offset,
            int logWeightDenom)
        {
            int shift = logWeightDenom;
            int round = shift > 0 ? 1 << (shift - 1) : 0;

            if (shift == 0)
            {
                for (int i = 0; i < pred.Length; i++)
                {
                    int result = pred[i] * weight + offset;
                    output[i] = (byte)Math.Clamp(result, 0, 255);
                }
            }
            else
            {
                for (int i = 0; i < pred.Length; i++)
                {
                    int result = ((pred[i] * weight + round) >> shift) + offset;
                    output[i] = (byte)Math.Clamp(result, 0, 255);
                }
            }
        }

        /// <summary>
        /// Bi-prediction with weights.
        /// </summary>
        public static void WeightedBiPrediction(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int weightL0,
            int weightL1,
            int offsetL0,
            int offsetL1,
            int logWeightDenom)
        {
            int shift = logWeightDenom + 1;
            int round = 1 << logWeightDenom;
            int offset = ((offsetL0 + offsetL1 + 1) >> 1);

            int count = Math.Min(predL0.Length, Math.Min(predL1.Length, output.Length));
            int at = 0;

            // This is where most bi-predicted pixels go, not the plain average: x264 turns on
            // weighted bi-prediction by default, so B slices reach this rather than PAVGB. About
            // 1.9 million pixels a frame at 1080p, one multiply-add-shift-clamp each.
            //
            // Thirty-two bit lanes because the two weighted products can sum past what a short
            // holds - 255 times 127, twice over.
            if (Avx2.IsSupported)
            {
                var w0 = Vector256.Create(weightL0);
                var w1 = Vector256.Create(weightL1);
                var rounds = Vector256.Create(round);
                var offsets = Vector256.Create(offset);
                var lowest = Vector256<int>.Zero;
                var highest = Vector256.Create(255);

                for (; at + 8 <= count; at += 8)
                {
                    Vector256<int> a = Avx2.ConvertToVector256Int32(
                        Vector64.Create(predL0.Slice(at, 8)).ToVector128());
                    Vector256<int> b = Avx2.ConvertToVector256Int32(
                        Vector64.Create(predL1.Slice(at, 8)).ToVector128());

                    Vector256<int> result = (((a * w0 + b * w1 + rounds) >> shift) + offsets);
                    result = Vector256.Min(Vector256.Max(result, lowest), highest);

                    Vector128<ushort> halves = Vector128.Narrow(
                        result.GetLower().AsUInt32(), result.GetUpper().AsUInt32());

                    BinaryPrimitives.WriteUInt64LittleEndian(
                        output.Slice(at, 8),
                        Vector128.Narrow(halves, halves).AsUInt64().ToScalar());
                }
            }

            for (; at < count; at++)
            {
                int result = ((predL0[at] * weightL0 + predL1[at] * weightL1 + round) >> shift) + offset;
                output[at] = (byte)Math.Clamp(result, 0, 255);
            }
        }
    }
}

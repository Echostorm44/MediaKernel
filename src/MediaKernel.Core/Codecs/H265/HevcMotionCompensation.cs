using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Codecs.H265
{
    /// <summary>
    /// Motion compensation for HEVC inter-prediction.
    /// Implements quarter-pixel interpolation with 8-tap luma and 4-tap chroma filters.
    /// </summary>
    /// <remarks>
    /// HEVC motion compensation differences from H.264:
    /// - 8-tap interpolation filter for luma (vs 6-tap in H.264)
    /// - 4-tap filter for chroma (vs bilinear in H.264)
    /// - Variable block sizes up to 64x64 (vs 16x16 in H.264)
    /// - Same quarter-pixel precision for both luma and chroma
    /// </remarks>
    public static class HevcMotionCompensation
    {
        #region Filter Coefficients

        /// <summary>
        /// HEVC luma interpolation filter coefficients (8-tap, DCT-based).
        /// Each row corresponds to a fractional position (0-3).
        /// Position 0 is integer (delta impulse), positions 1-3 are fractional.
        /// </summary>
        private static ReadOnlySpan<short> LumaFilter => new short[]
        {
            // Position 0 (integer): passthrough
              0,   0,   0,  64,   0,   0,   0,   0,
            // Position 1 (1/4 pixel)
             -1,   4, -10,  58,  17,  -5,   1,   0,
            // Position 2 (1/2 pixel)
             -1,   4, -11,  40,  40, -11,   4,  -1,
            // Position 3 (3/4 pixel)
              0,   1,  -5,  17,  58, -10,   4,  -1
        };

        /// <summary>
        /// HEVC chroma interpolation filter coefficients (4-tap).
        /// Each row corresponds to a fractional position (0-7).
        /// </summary>
        private static ReadOnlySpan<short> ChromaFilter => new short[]
        {
            // Position 0 (integer)
              0,  64,   0,   0,
            // Position 1 (1/8 pixel)
             -2,  58,  10,  -2,
            // Position 2 (2/8 pixel)
             -4,  54,  16,  -2,
            // Position 3 (3/8 pixel)
             -6,  46,  28,  -4,
            // Position 4 (4/8 pixel = 1/2)
             -4,  36,  36,  -4,
            // Position 5 (5/8 pixel)
             -4,  28,  46,  -6,
            // Position 6 (6/8 pixel)
             -2,  16,  54,  -4,
            // Position 7 (7/8 pixel)
             -2,  10,  58,  -2
        };

        #endregion

        #region Luma Motion Compensation

        /// <summary>
        /// Performs motion compensation for a variable-size luma block.
        /// </summary>
        /// <param name="reference">Reference frame luma plane.</param>
        /// <param name="refStride">Stride of reference frame.</param>
        /// <param name="output">Output prediction block.</param>
        /// <param name="outStride">Stride of output buffer.</param>
        /// <param name="width">Block width (4, 8, 16, 32, or 64).</param>
        /// <param name="height">Block height.</param>
        /// <param name="mvX">Horizontal motion vector in quarter-pixel units.</param>
        /// <param name="mvY">Vertical motion vector in quarter-pixel units.</param>
        /// <param name="blockX">Block X position in pixels.</param>
        /// <param name="blockY">Block Y position in pixels.</param>
        /// <param name="frameWidth">Reference frame width.</param>
        /// <param name="frameHeight">Reference frame height.</param>
        public static void CompensateLuma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            // Calculate full-pixel and fractional parts
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                // Integer position - direct copy
                CopyBlock(reference, refStride, output, outStride, width, height, fullX, fullY, frameWidth, frameHeight);
                return;
            }

            // One two-pass form for all three fractional cases, which is what the high bit depth
            // path does and what makes the vectorised kernels worth having. Filter position 0 is
            // a delta impulse (0,0,0,64,0,0,0,0), so a horizontal-only block runs the general
            // path with a vertical filter that multiplies by 64 and shifts back by 6 - the same
            // arithmetic as the specialised loop this replaces, to the bit, and the conformance
            // suite says so on all 166 streams.
            Span<int> temp = stackalloc int[width * (height + 7)];

            HorizontalLuma8Bit(reference, refStride, temp, width, height + 7,
                               fullX, fullY, frameWidth, frameHeight, fracX * 8);

            ReadOnlySpan<short> taps = LumaFilter.Slice(fracY * 8, 8);

            for (int row = 0; row < height; row++)
            {
                VerticalLumaToBytes(temp.Slice(row * width, width * 8), width,
                                    output.Slice(row * outStride, width), taps);
            }
        }

        /// <summary>
        /// The horizontal half of the eight-tap luma filter over an eight-bit plane, shared by
        /// the uni- and bi-prediction paths.
        /// </summary>
        /// <remarks>
        /// The ten-bit twin of this method carries a note about the two things that made it slow:
        /// the source index clamped to the frame inside the innermost tap loop, and the taps
        /// re-read from a span on every tap. The eight-bit path still had both, because the
        /// vectorisation work was measured on a Main 10 file and never ran these methods at all.
        /// A 1080p eight-bit stream decoded at 16.7 fps against 54 for a 1080p ten-bit one.
        /// </remarks>
        private static void HorizontalLuma8Bit(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> temp,
            int width,
            int rows,
            int fullX,
            int fullY,
            int frameWidth,
            int frameHeight,
            int filterOffset)
        {
            ReadOnlySpan<short> taps = LumaFilter.Slice(filterOffset, 8);
            int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
            int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];

            // The filter reaches three pixels left and four right of each output column.
            bool interior = fullX - 3 >= 0 && fullX + width + 4 <= frameWidth;

            for (int row = 0; row < rows; row++)
            {
                int srcY = Math.Clamp(fullY + row - 3, 0, frameHeight - 1);
                int rowBase = srcY * refStride;
                Span<int> destination = temp.Slice(row * width, width);

                if (!interior)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 3 + k, 0, frameWidth - 1);
                            sum += taps[k] * reference[rowBase + srcX];
                        }
                        destination[col] = sum;
                    }
                    continue;
                }

                ReadOnlySpan<byte> source = reference.Slice(rowBase + fullX - 3, width + 7);

                if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
                {
                    Dsp.HevcInterpolationDsp.HorizontalLuma8TapBytes(source, destination, taps, width);
                    continue;
                }

                ref byte src = ref MemoryMarshal.GetReference(source);
                ref int dst = ref MemoryMarshal.GetReference(destination);

                for (int col = 0; col < width; col++)
                {
                    ref byte at = ref Unsafe.Add(ref src, col);
                    Unsafe.Add(ref dst, col) =
                          t0 * at
                        + t1 * Unsafe.Add(ref at, 1)
                        + t2 * Unsafe.Add(ref at, 2)
                        + t3 * Unsafe.Add(ref at, 3)
                        + t4 * Unsafe.Add(ref at, 4)
                        + t5 * Unsafe.Add(ref at, 5)
                        + t6 * Unsafe.Add(ref at, 6)
                        + t7 * Unsafe.Add(ref at, 7);
                }
            }
        }

        /// <summary>
        /// The vertical half for eight-bit uni-prediction: filter, normalise and clip to a byte.
        /// </summary>
        /// <remarks>
        /// shift is 14 - 8 and offset is 1 &lt;&lt; (shift - 1), as <c>put_hevc_qpel_uni_hv</c>
        /// has them for eight-bit content.
        /// </remarks>
        private static void VerticalLumaToBytes(
            ReadOnlySpan<int> window, int width, Span<byte> destination, ReadOnlySpan<short> taps)
        {
            const int shift = 6;
            const int offset = 1 << (shift - 1);

            if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
            {
                Dsp.HevcInterpolationDsp.VerticalLuma8TapToBytes(
                    window, destination, taps, width, offset, shift);
                return;
            }

            Dsp.HevcInterpolationDsp.VerticalLuma8TapToBytesScalar(
                window, destination, taps, width, offset, shift, 0);
        }

        #endregion

        #region Chroma Motion Compensation

        /// <summary>
        /// Performs motion compensation for chroma (Cb or Cr).
        /// Uses 4-tap filter with 1/8 pixel precision.
        /// </summary>
        public static void CompensateChroma(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int planeWidth,
            int planeHeight)
        {
            // Chroma MVs in 4:2:0 are same precision as luma (quarter-pel for 4:2:0)
            // but applied to half-resolution plane, giving eighth-pel effective
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            if (fracX == 0 && fracY == 0)
            {
                CopyBlock(reference, refStride, output, outStride, width, height,
                    fullX, fullY, planeWidth, planeHeight);
                return;
            }

            Span<int> temp = stackalloc int[width * (height + 3)];

            HorizontalChroma8Bit(reference, refStride, temp, width, height + 3,
                                 fullX, fullY, planeWidth, planeHeight, fracX * 4);

            ReadOnlySpan<short> taps = ChromaFilter.Slice(fracY * 4, 4);

            for (int row = 0; row < height; row++)
            {
                VerticalChromaToBytes(temp.Slice(row * width, width * 4), width,
                                      output.Slice(row * outStride, width), taps);
            }
        }

        /// <summary>
        /// The horizontal half of the four-tap chroma filter over an eight-bit plane.
        /// </summary>
        private static void HorizontalChroma8Bit(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> temp,
            int width,
            int rows,
            int fullX,
            int fullY,
            int planeWidth,
            int planeHeight,
            int filterOffset)
        {
            ReadOnlySpan<short> taps = ChromaFilter.Slice(filterOffset, 4);
            int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];

            // The filter reaches one sample left and two right of each output column.
            bool interior = fullX - 1 >= 0 && fullX + width + 2 <= planeWidth;

            for (int row = 0; row < rows; row++)
            {
                int srcY = Math.Clamp(fullY + row - 1, 0, planeHeight - 1);
                int rowBase = srcY * refStride;
                Span<int> destination = temp.Slice(row * width, width);

                if (!interior)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 1 + k, 0, planeWidth - 1);
                            sum += taps[k] * reference[rowBase + srcX];
                        }
                        destination[col] = sum;
                    }
                    continue;
                }

                ReadOnlySpan<byte> source = reference.Slice(rowBase + fullX - 1, width + 3);

                if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
                {
                    Dsp.HevcInterpolationDsp.HorizontalChroma4TapBytes(source, destination, taps, width);
                    continue;
                }

                ref byte src = ref MemoryMarshal.GetReference(source);
                ref int dst = ref MemoryMarshal.GetReference(destination);

                for (int col = 0; col < width; col++)
                {
                    ref byte at = ref Unsafe.Add(ref src, col);
                    Unsafe.Add(ref dst, col) = t0 * at
                                             + t1 * Unsafe.Add(ref at, 1)
                                             + t2 * Unsafe.Add(ref at, 2)
                                             + t3 * Unsafe.Add(ref at, 3);
                }
            }
        }

        /// <summary>
        /// The vertical half for eight-bit chroma uni-prediction.
        /// </summary>
        private static void VerticalChromaToBytes(
            ReadOnlySpan<int> window, int width, Span<byte> destination, ReadOnlySpan<short> taps)
        {
            const int shift = 6;
            const int offset = 1 << (shift - 1);

            if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
            {
                Dsp.HevcInterpolationDsp.VerticalChroma4TapToBytes(
                    window, destination, taps, width, offset, shift);
                return;
            }

            Dsp.HevcInterpolationDsp.VerticalChroma4TapToBytesScalar(
                window, destination, taps, width, offset, shift, 0);
        }

        #endregion

        #region Bi-Prediction

        /// <summary>
        /// Bi-directional prediction (average of L0 and L1 predictions).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BiPrediction(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int stride,
            int width,
            int height)
        {
            int count = width * height;

            if (Sse2.IsSupported && count >= 16)
            {
                BiPredictionSse2(predL0, predL1, output, count);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    output[i] = (byte)((predL0[i] + predL1[i] + 1) >> 1);
                }
            }
        }

        private static unsafe void BiPredictionSse2(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int count)
        {
            fixed (byte* pL0 = predL0)
            fixed (byte* pL1 = predL1)
            fixed (byte* pOut = output)
            {
                int i = 0;
                for (; i + 16 <= count; i += 16)
                {
                    var l0 = Sse2.LoadVector128(pL0 + i);
                    var l1 = Sse2.LoadVector128(pL1 + i);
                    var avg = Sse2.Average(l0, l1);
                    Sse2.Store(pOut + i, avg);
                }

                // Handle remaining
                for (; i < count; i++)
                {
                    pOut[i] = (byte)((pL0[i] + pL1[i] + 1) >> 1);
                }
            }
        }

        #endregion

        #region Weighted Prediction

        /// <summary>
        /// Weighted uni-prediction.
        /// </summary>
        public static void WeightedPrediction(
            ReadOnlySpan<byte> pred,
            Span<byte> output,
            int width,
            int height,
            int weight,
            int offset,
            int logWeightDenom,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;
            int shift = logWeightDenom;
            int round = shift > 0 ? 1 << (shift - 1) : 0;

            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                int result = ((pred[i] * weight + round) >> shift) + offset;
                output[i] = (byte)Math.Clamp(result, 0, maxValue);
            }
        }

        /// <summary>
        /// Weighted bi-prediction.
        /// </summary>
        public static void WeightedBiPrediction(
            ReadOnlySpan<byte> predL0,
            ReadOnlySpan<byte> predL1,
            Span<byte> output,
            int width,
            int height,
            int weightL0,
            int weightL1,
            int offsetL0,
            int offsetL1,
            int logWeightDenom,
            int bitDepth = 8)
        {
            int maxValue = (1 << bitDepth) - 1;
            int shift = logWeightDenom + 1;
            int round = 1 << logWeightDenom;
            int offsetSum = (offsetL0 + offsetL1 + 1) >> 1;

            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                int result = ((predL0[i] * weightL0 + predL1[i] * weightL1 + round) >> shift) + offsetSum;
                output[i] = (byte)Math.Clamp(result, 0, maxValue);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Copies a block at integer position with boundary handling.
        /// </summary>
        private static void CopyBlock(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<byte> output,
            int outStride,
            int width,
            int height,
            int x,
            int y,
            int frameWidth,
            int frameHeight)
        {
            for (int row = 0; row < height; row++)
            {
                int srcY = Math.Clamp(y + row, 0, frameHeight - 1);
                for (int col = 0; col < width; col++)
                {
                    int srcX = Math.Clamp(x + col, 0, frameWidth - 1);
                    output[row * outStride + col] = reference[srcY * refStride + srcX];
                }
            }
        }

        /// <summary>
        /// Performs motion compensation for high bit-depth (10-12 bit) luma.
        /// </summary>
        public static void CompensateLumaHighBitDepth(
            ReadOnlySpan<ushort> reference,
            int refStride,
            Span<ushort> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                // Integer copy
                bool interiorX = fullX >= 0 && fullX + width <= frameWidth;

                // Rows that are wholly inside the frame are copied at a width settled before
                // the loop. Span.CopyTo would route each one through Buffer.Memmove, which
                // costs about four times the copy itself at these sizes - worth 14.3% of decode
                // when the same change was made to H.264.
                if (interiorX && fullY >= 0 && fullY + height <= frameHeight)
                {
                    Dsp.ReconstructDsp.CopyRows(
                        reference, fullY * refStride + fullX, refStride,
                        output, 0, outStride, width, height);
                    return;
                }

                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    int rowBase = srcY * refStride;
                    int outBase = row * outStride;

                    if (interiorX)
                    {
                        Dsp.ReconstructDsp.CopyRows(
                            reference, rowBase + fullX, refStride,
                            output, outBase, outStride, width, 1);
                        continue;
                    }

                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[outBase + col] = reference[rowBase + srcX];
                    }
                }
            }
            else
            {
                // Fractional - horizontal then vertical.
                // Matches ffmpeg put_hevc_qpel_uni_hv for high bit depth.
                int shift = 14 - bitDepth;
                int offset = 1 << (shift - 1);

                Span<int> temp = stackalloc int[width * (height + 7)];
                HorizontalLumaHighBitDepth(reference, refStride, temp, width, height + 7,
                                           fullX, fullY, frameWidth, frameHeight, fracX * 8, bitDepth - 8);

                // Vertical pass - combined normalization:
                // dst = av_clip_pixel(((QPEL_FILTER(tmp, stride) >> 6) + offset) >> shift)
                ReadOnlySpan<short> taps = LumaFilter.Slice(fracY * 8, 8);
                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<int> window = temp.Slice(row * width, width * 8);
                    Span<ushort> destination = output.Slice(row * outStride, width);
                    VerticalLumaToPixels(window, width, destination, taps, shift, offset, maxValue);
                }
            }
        }

        /// <summary>
        /// Luma MC producing intermediate ~14-bit precision output for bi-prediction.
        /// Output values are NOT clipped to pixel range; they must be combined with the
        /// other reference's intermediate values before final clipping.
        /// </summary>
        public static void CompensateLumaHighBitDepthIntermediate(
            ReadOnlySpan<ushort> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight,
            int bitDepth)
        {
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;
            int shift14 = 14 - bitDepth;

            if (fracX == 0 && fracY == 0)
            {
                // Integer: pixel << (14 - bitDepth) to reach ~14-bit precision
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    int rowBase = srcY * refStride;
                    int outBase = row * outStride;
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[outBase + col] = reference[rowBase + srcX] << shift14;
                    }
                }
            }
            else
            {
                Span<int> temp = stackalloc int[width * (height + 7)];
                HorizontalLumaHighBitDepth(reference, refStride, temp, width, height + 7,
                                           fullX, fullY, frameWidth, frameHeight, fracX * 8, bitDepth - 8);

                // Vertical pass: output intermediate (sum >> 6) without clip
                ReadOnlySpan<short> taps = LumaFilter.Slice(fracY * 8, 8);
                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<int> window = temp.Slice(row * width, width * 8);
                    Span<int> destination = output.Slice(row * outStride, width);
                    VerticalLumaToIntermediate(window, width, destination, taps);
                }
            }
        }

        /// <summary>
        /// The horizontal half of the 8-tap luma interpolation, shared by the uni- and
        /// bi-prediction paths, which differ only in what the vertical half does with the result.
        /// </summary>
        /// <remarks>
        /// This is the hottest loop in HEVC decoding: motion compensation is around 60% of decode
        /// time on a 1080p Main-10 stream, and this is most of it. Two things made it slow. The
        /// source index was clamped to the frame inside the innermost 8-tap loop, so every output
        /// pixel paid eight clamps for a condition that only applies at the frame edge; and the
        /// filter taps were re-read from a span on every tap. Almost every block sits far enough
        /// inside the frame that no clamping applies, so that case now runs straight-line over
        /// contiguous memory and only the edge case keeps the clamped loop.
        /// </remarks>

        private static void HorizontalLumaHighBitDepth(
            ReadOnlySpan<ushort> reference,
            int refStride,
            Span<int> temp,
            int width,
            int rows,
            int fullX,
            int fullY,
            int frameWidth,
            int frameHeight,
            int filterOffset,
            int shift)
        {
            ReadOnlySpan<short> taps = LumaFilter.Slice(filterOffset, 8);
            int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
            int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];

            // The filter reaches three pixels left and four right of each output column.
            bool interior = fullX - 3 >= 0 && fullX + width + 4 <= frameWidth;

            for (int row = 0; row < rows; row++)
            {
                int srcY = Math.Clamp(fullY + row - 3, 0, frameHeight - 1);
                int rowBase = srcY * refStride;
                Span<int> destination = temp.Slice(row * width, width);

                if (!interior)
                {
                    for (int col = 0; col < width; col++)
                    {
                        int sum = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int srcX = Math.Clamp(fullX + col - 3 + k, 0, frameWidth - 1);
                            sum += taps[k] * reference[rowBase + srcX];
                        }
                        destination[col] = sum >> shift;
                    }
                    continue;
                }

                ReadOnlySpan<ushort> source = reference.Slice(rowBase + fullX - 3, width + 7);

                // Eight columns at a time where the width allows it. Every load and store in
                // there is bounds-checked; the unchecked equivalent is only a few percent
                // faster and this stays verifiably memory-safe.
                if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
                {
                    Dsp.HevcInterpolationDsp.HorizontalLuma8Tap(source, destination, taps, shift, width);
                    continue;
                }

                ref ushort src = ref MemoryMarshal.GetReference(source);
                ref int dst = ref MemoryMarshal.GetReference(destination);

                for (int col = 0; col < width; col++)
                {
                    ref ushort at = ref Unsafe.Add(ref src, col);
                    int sum = t0 * at
                            + t1 * Unsafe.Add(ref at, 1)
                            + t2 * Unsafe.Add(ref at, 2)
                            + t3 * Unsafe.Add(ref at, 3)
                            + t4 * Unsafe.Add(ref at, 4)
                            + t5 * Unsafe.Add(ref at, 5)
                            + t6 * Unsafe.Add(ref at, 6)
                            + t7 * Unsafe.Add(ref at, 7);
                    Unsafe.Add(ref dst, col) = sum >> shift;
                }
            }
        }

        /// <summary>
        /// The vertical half for uni-prediction: filter, normalise and clip to pixel range.
        /// </summary>
        /// <param name="window">
        /// Eight rows of horizontally filtered samples, row stride <paramref name="width"/>.
        /// </param>
        private static void VerticalLumaToPixels(
            ReadOnlySpan<int> window,
            int width,
            Span<ushort> destination,
            ReadOnlySpan<short> taps,
            int shift,
            int offset,
            int maxValue)
        {
            int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
            int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];
            ref int top = ref MemoryMarshal.GetReference(window);
            ref ushort dst = ref MemoryMarshal.GetReference(destination);

            for (int col = 0; col < width; col++)
            {
                ref int at = ref Unsafe.Add(ref top, col);
                int sum = t0 * at
                        + t1 * Unsafe.Add(ref at, width)
                        + t2 * Unsafe.Add(ref at, width * 2)
                        + t3 * Unsafe.Add(ref at, width * 3)
                        + t4 * Unsafe.Add(ref at, width * 4)
                        + t5 * Unsafe.Add(ref at, width * 5)
                        + t6 * Unsafe.Add(ref at, width * 6)
                        + t7 * Unsafe.Add(ref at, width * 7);
                Unsafe.Add(ref dst, col) = (ushort)Math.Clamp(((sum >> 6) + offset) >> shift, 0, maxValue);
            }
        }

        /// <summary>
        /// The vertical half for bi-prediction: filter and normalise, leaving the result unclipped
        /// so it can be averaged with the other reference.
        /// </summary>
        private static void VerticalLumaToIntermediate(
            ReadOnlySpan<int> window,
            int width,
            Span<int> destination,
            ReadOnlySpan<short> taps)
        {
            int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
            int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];
            ref int top = ref MemoryMarshal.GetReference(window);
            ref int dst = ref MemoryMarshal.GetReference(destination);

            for (int col = 0; col < width; col++)
            {
                ref int at = ref Unsafe.Add(ref top, col);
                int sum = t0 * at
                        + t1 * Unsafe.Add(ref at, width)
                        + t2 * Unsafe.Add(ref at, width * 2)
                        + t3 * Unsafe.Add(ref at, width * 3)
                        + t4 * Unsafe.Add(ref at, width * 4)
                        + t5 * Unsafe.Add(ref at, width * 5)
                        + t6 * Unsafe.Add(ref at, width * 6)
                        + t7 * Unsafe.Add(ref at, width * 7);
                Unsafe.Add(ref dst, col) = sum >> 6;
            }
        }

        public static void CompensateLumaIntermediate(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            int fullX = blockX + (mvX >> 2);
            int fullY = blockY + (mvY >> 2);
            int fracX = mvX & 3;
            int fracY = mvY & 3;

            if (fracX == 0 && fracY == 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX] << 6;
                    }
                }
            }
            else
            {
                Span<int> temp = stackalloc int[width * (height + 7)];

                HorizontalLuma8Bit(reference, refStride, temp, width, height + 7,
                                   fullX, fullY, frameWidth, frameHeight, fracX * 8);

                // Vertical pass: intermediate precision, left unclipped for the average.
                ReadOnlySpan<short> taps = LumaFilter.Slice(fracY * 8, 8);

                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<int> window = temp.Slice(row * width, width * 8);
                    Span<int> destination = output.Slice(row * outStride, width);

                    if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
                        Dsp.HevcInterpolationDsp.VerticalLuma8TapToIntermediate(window, destination, taps, width);
                    else
                        Dsp.HevcInterpolationDsp.VerticalLuma8TapToIntermediateScalar(window, destination, taps, width, 0);
                }
            }
        }

        /// <summary>
        /// 8-bit chroma intermediate MC for bi-prediction. Outputs ~14-bit precision values.
        /// Uses 4-tap chroma filter with fractional positions in 1/8-pel units.
        /// </summary>
        public static void CompensateChromaIntermediate(
            ReadOnlySpan<byte> reference,
            int refStride,
            Span<int> output,
            int outStride,
            int width,
            int height,
            int mvX,
            int mvY,
            int blockX,
            int blockY,
            int frameWidth,
            int frameHeight)
        {
            int fullX = blockX + (mvX >> 3);
            int fullY = blockY + (mvY >> 3);
            int fracX = mvX & 7;
            int fracY = mvY & 7;

            if (fracX == 0 && fracY == 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int srcY = Math.Clamp(fullY + row, 0, frameHeight - 1);
                    for (int col = 0; col < width; col++)
                    {
                        int srcX = Math.Clamp(fullX + col, 0, frameWidth - 1);
                        output[row * outStride + col] = reference[srcY * refStride + srcX] << 6;
                    }
                }
            }
            else
            {
                Span<int> temp = stackalloc int[width * (height + 3)];

                HorizontalChroma8Bit(reference, refStride, temp, width, height + 3,
                                     fullX, fullY, frameWidth, frameHeight, fracX * 4);

                ReadOnlySpan<short> taps = ChromaFilter.Slice(fracY * 4, 4);

                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<int> window = temp.Slice(row * width, width * 4);
                    Span<int> destination = output.Slice(row * outStride, width);

                    if (Dsp.HevcInterpolationDsp.CanFilterHorizontal(width))
                        Dsp.HevcInterpolationDsp.VerticalChroma4TapToIntermediate(window, destination, taps, width);
                    else
                        Dsp.HevcInterpolationDsp.VerticalChroma4TapToIntermediateScalar(window, destination, taps, width, 0);
                }
            }
        }

        #endregion
    }
}

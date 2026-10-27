// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Distortion metrics used by encoders for mode decision and motion estimation.
/// SAD (Sum of Absolute Differences), SATD (Sum of Absolute Transformed Differences),
/// SSD (Sum of Squared Differences).
/// </summary>
public static class MetricDsp
{
    // ──────────────────────────────────────────────
    // SAD — Sum of Absolute Differences
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute SAD between two blocks of 8-bit samples.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sad(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        if (Sse2.IsSupported && width >= 16)
            return SadSse2(source, reference, width, height, sourceStride, referenceStride);

        return SadScalar(source, reference, width, height, sourceStride, referenceStride);
    }

    /// <summary>Scalar SAD reference implementation.</summary>
    public static int SadScalar(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        int sad = 0;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * sourceStride;
            int refRow = y * referenceStride;
            for (int x = 0; x < width; x++)
            {
                sad += Math.Abs(source[srcRow + x] - reference[refRow + x]);
            }
        }
        return sad;
    }

    /// <summary>SSE2 SAD using psadbw (packed sum of absolute byte differences).</summary>
    public static unsafe int SadSse2(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pRef = reference)
        {
            var accumulator = Vector128<long>.Zero;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = pSrc + y * sourceStride;
                byte* refRow = pRef + y * referenceStride;
                int x = 0;

                for (; x + 16 <= width; x += 16)
                {
                    var s = Sse2.LoadVector128(srcRow + x);
                    var r = Sse2.LoadVector128(refRow + x);
                    // SumAbsoluteDifferences returns two u64 sums in a Vector128<ushort>
                    var sad = Sse2.SumAbsoluteDifferences(s, r).AsInt64();
                    accumulator = Sse2.Add(accumulator, sad);
                }

                // Scalar tail for rows not multiple of 16
                if (x < width)
                {
                    long tail = 0;
                    for (; x < width; x++)
                        tail += Math.Abs(srcRow[x] - refRow[x]);
                    accumulator = Sse2.Add(accumulator,
                        Vector128.Create(tail, 0L));
                }
            }

            // Sum the two 64-bit lanes
            return (int)(accumulator.GetElement(0) + accumulator.GetElement(1));
        }
    }

    // ──────────────────────────────────────────────
    // SSD — Sum of Squared Differences
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute SSD between two blocks of 8-bit samples.
    /// Also known as SSE (Sum of Squared Errors) in encoder literature.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Ssd(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        if (Sse2.IsSupported && width >= 8)
            return SsdSse2(source, reference, width, height, sourceStride, referenceStride);

        return SsdScalar(source, reference, width, height, sourceStride, referenceStride);
    }

    /// <summary>Scalar SSD reference implementation.</summary>
    public static long SsdScalar(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        long ssd = 0;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * sourceStride;
            int refRow = y * referenceStride;
            for (int x = 0; x < width; x++)
            {
                int diff = source[srcRow + x] - reference[refRow + x];
                ssd += diff * diff;
            }
        }
        return ssd;
    }

    /// <summary>SSE2 SSD: widen bytes to shorts, subtract, multiply-add for squared sum.</summary>
    public static unsafe long SsdSse2(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int width,
        int height,
        int sourceStride,
        int referenceStride)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pRef = reference)
        {
            var accumulator = Vector128<int>.Zero;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = pSrc + y * sourceStride;
                byte* refRow = pRef + y * referenceStride;
                int x = 0;

                for (; x + 8 <= width; x += 8)
                {
                    // Load 8 bytes, unpack to shorts
                    var s = Sse2.UnpackLow(
                        Sse2.LoadScalarVector128((long*)(srcRow + x)).AsByte(),
                        Vector128<byte>.Zero).AsInt16();
                    var r = Sse2.UnpackLow(
                        Sse2.LoadScalarVector128((long*)(refRow + x)).AsByte(),
                        Vector128<byte>.Zero).AsInt16();

                    var diff = Sse2.Subtract(s, r);
                    // MultiplyAddAdjacent: (a0*b0 + a1*b1) as int32 — perfect for squaring
                    var squared = Sse2.MultiplyAddAdjacent(diff, diff);
                    accumulator = Sse2.Add(accumulator, squared);
                }

                // Scalar tail
                for (; x < width; x++)
                {
                    int diff = srcRow[x] - refRow[x];
                    accumulator = Sse2.Add(accumulator,
                        Vector128.Create(diff * diff, 0, 0, 0));
                }
            }

            // Sum the four 32-bit lanes
            long sum = 0;
            for (int lane = 0; lane < 4; lane++)
                sum += accumulator.GetElement(lane);
            return sum;
        }
    }

    // ──────────────────────────────────────────────
    // SATD — Sum of Absolute Transformed Differences (4x4 Hadamard)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute 4x4 SATD between two blocks. Uses Hadamard transform for better
    /// correlation with actual coding cost than SAD.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Satd4x4(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int sourceStride,
        int referenceStride)
    {
        return Satd4x4Scalar(source, reference, sourceStride, referenceStride);
    }

    /// <summary>Scalar 4x4 SATD: difference → 4x4 Hadamard → sum of absolute values.</summary>
    public static int Satd4x4Scalar(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int sourceStride,
        int referenceStride)
    {
        // Step 1: Compute 4x4 difference block
        Span<int> diff = stackalloc int[16];
        for (int y = 0; y < 4; y++)
        {
            int srcRow = y * sourceStride;
            int refRow = y * referenceStride;
            for (int x = 0; x < 4; x++)
                diff[y * 4 + x] = source[srcRow + x] - reference[refRow + x];
        }

        // Step 2: Horizontal Hadamard transform
        Span<int> tmp = stackalloc int[16];
        for (int y = 0; y < 4; y++)
        {
            int s0 = diff[y * 4 + 0];
            int s1 = diff[y * 4 + 1];
            int s2 = diff[y * 4 + 2];
            int s3 = diff[y * 4 + 3];

            int a0 = s0 + s3;
            int a1 = s1 + s2;
            int a2 = s1 - s2;
            int a3 = s0 - s3;

            tmp[y * 4 + 0] = a0 + a1;
            tmp[y * 4 + 1] = a3 + a2;
            tmp[y * 4 + 2] = a0 - a1;
            tmp[y * 4 + 3] = a3 - a2;
        }

        // Step 3: Vertical Hadamard transform + accumulate absolute values
        int satd = 0;
        for (int x = 0; x < 4; x++)
        {
            int s0 = tmp[0 * 4 + x];
            int s1 = tmp[1 * 4 + x];
            int s2 = tmp[2 * 4 + x];
            int s3 = tmp[3 * 4 + x];

            int a0 = s0 + s3;
            int a1 = s1 + s2;
            int a2 = s1 - s2;
            int a3 = s0 - s3;

            satd += Math.Abs(a0 + a1);
            satd += Math.Abs(a3 + a2);
            satd += Math.Abs(a0 - a1);
            satd += Math.Abs(a3 - a2);
        }

        return (satd + 1) >> 1;
    }

    /// <summary>
    /// Compute 8x8 SATD by summing four 4x4 SATD blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Satd8x8(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> reference,
        int sourceStride,
        int referenceStride)
    {
        int satd = 0;
        satd += Satd4x4(source, reference, sourceStride, referenceStride);
        satd += Satd4x4(source[4..], reference[4..], sourceStride, referenceStride);
        satd += Satd4x4(source[(4 * sourceStride)..], reference[(4 * referenceStride)..], sourceStride, referenceStride);
        satd += Satd4x4(source[(4 * sourceStride + 4)..], reference[(4 * referenceStride + 4)..], sourceStride, referenceStride);
        return satd;
    }

    // ──────────────────────────────────────────────
    // Block comparison helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute SAD for a contiguous block (no stride — flat array).
    /// Useful for coefficient comparison, residual analysis, etc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SadFlat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int count)
    {
        return Sad(a, b, count, 1, count, count);
    }

    /// <summary>
    /// Compute SSD for a contiguous block (no stride — flat array).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SsdFlat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int count)
    {
        return Ssd(a, b, count, 1, count, count);
    }

    /// <summary>
    /// Compute PSNR in dB from SSD, pixel count, and bit depth.
    /// Returns double.PositiveInfinity if SSD is 0 (identical blocks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double PsnrFromSsd(long ssd, int pixelCount, int bitDepth = 8)
    {
        if (ssd == 0) return double.PositiveInfinity;
        double maxValue = (1 << bitDepth) - 1;
        double mse = (double)ssd / pixelCount;
        return 10.0 * Math.Log10(maxValue * maxValue / mse);
    }
}

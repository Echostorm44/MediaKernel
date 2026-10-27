// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// HEVC/H.265 inverse transform DSP with SIMD acceleration.
/// Implements DCT-II (4/8/16/32) and DST-VII (4x4 intra) as 2-pass separable transforms.
/// Pass 2 inner dot products use SSE2 pmaddwd for speedup on sizes >= 8.
/// </summary>
public static class HevcTransformDsp
{
    // ──────────────────────────────────────────────
    //  Coefficient Tables (ITU-T H.265 Tables 8-6, 8-7)
    // ──────────────────────────────────────────────

    private static ReadOnlySpan<short> Dct4x4 => new short[]
    {
        64,  64,  64,  64,
        83,  36, -36, -83,
        64, -64, -64,  64,
        36, -83,  83, -36
    };

    private static ReadOnlySpan<short> Dst4x4 => new short[]
    {
        29,  55,  74,  84,
        74,  74,   0, -74,
        84, -29, -74,  55,
        55, -84,  74, -29
    };

    private static ReadOnlySpan<short> Dct8x8 => new short[]
    {
        64,  64,  64,  64,  64,  64,  64,  64,
        89,  75,  50,  18, -18, -50, -75, -89,
        83,  36, -36, -83, -83, -36,  36,  83,
        75, -18, -89, -50,  50,  89,  18, -75,
        64, -64, -64,  64,  64, -64, -64,  64,
        50, -89,  18,  75, -75, -18,  89, -50,
        36, -83,  83, -36, -36,  83, -83,  36,
        18, -50,  75, -89,  89, -75,  50, -18
    };

    // 16x16 and 32x32 tables accessed via delegation to HevcTransform (too large to duplicate).

    // ──────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────

    /// <summary>
    /// 4x4 inverse DCT-II (inter/skip blocks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDct4x4(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransformScalar(coefficients, output, Dct4x4, 4, shift2, add2);
    }

    /// <summary>
    /// 4x4 inverse DST-VII (intra blocks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDst4x4(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransformScalar(coefficients, output, Dst4x4, 4, shift2, add2);
    }

    /// <summary>
    /// 8x8 inverse DCT-II with SSE2-accelerated pass 2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransformDct8x8(
        ReadOnlySpan<short> coefficients, Span<short> output, int shift2 = 12, int add2 = 2048)
    {
        InverseTransform(coefficients, output, Dct8x8, 8, shift2, add2);
    }

    /// <summary>
    /// Generic NxN inverse transform — dispatches SIMD for pass 2 on sizes >= 8.
    /// Used for 16x16 and 32x32 where coefficient tables are passed from the codec.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        if (Sse2.IsSupported && size >= 8)
            InverseTransformSse2(coefficients, output, matrix, size, shift2, add2);
        else
            InverseTransformScalar(coefficients, output, matrix, size, shift2, add2);
    }

    // ──────────────────────────────────────────────
    //  Scalar Reference
    // ──────────────────────────────────────────────

    /// <summary>
    /// Scalar NxN inverse transform. Two-pass separable: column then row.
    /// </summary>
    public static void InverseTransformScalar(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        int n2 = size * size;
        Span<int> temp = n2 <= 256 ? stackalloc int[n2] : new int[n2];

        // Pass 1: column transform
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int sum = 0;
                for (int k = 0; k < size; k++)
                    sum += matrix[k * size + i] * coefficients[k * size + j];
                temp[i * size + j] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Pass 2: row transform
        for (int i = 0; i < size; i++)
        {
            int rowOffset = i * size;
            for (int j = 0; j < size; j++)
            {
                int sum = 0;
                for (int k = 0; k < size; k++)
                    sum += matrix[k * size + j] * temp[rowOffset + k];
                output[rowOffset + j] = (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    // ──────────────────────────────────────────────
    //  SSE2 Accelerated (sizes >= 8)
    // ──────────────────────────────────────────────

    /// <summary>
    /// SSE2-accelerated NxN inverse transform.
    /// Pass 1 (column) is scalar (strided access pattern).
    /// Pass 2 (row) uses pmaddwd for 8 multiply-adds per instruction.
    /// </summary>
    public static void InverseTransformSse2(
        ReadOnlySpan<short> coefficients, Span<short> output,
        ReadOnlySpan<short> matrix, int size, int shift2, int add2)
    {
        // Both passes are dot products, and both want one operand read down a column. Gathering
        // that column inside the inner loop - which is what this used to do - costs O(size^3) of
        // pure copying on top of the arithmetic, and it does not depend on the inner index at all.
        // Transposing the matrix and the coefficients once, into zero-padded rows, makes every dot
        // product a straight run over contiguous memory and lets the tail disappear: the padding
        // contributes zero, so there is no scalar remainder to handle.
        //
        // The arithmetic is unchanged. Sums are exact in int regardless of the order the products
        // are added in, so pairwise SIMD accumulation gives the same result as the scalar loop.
        int lanes = Avx2.IsSupported ? 16 : 8;
        int padSize = (size + lanes - 1) & ~(lanes - 1);
        int padded = size * padSize;

        Span<short> matrixT = padded <= 1024 ? stackalloc short[padded] : new short[padded];
        Span<short> coefficientsT = padded <= 1024 ? stackalloc short[padded] : new short[padded];
        Span<short> temp = padded <= 1024 ? stackalloc short[padded] : new short[padded];
        matrixT.Clear();
        coefficientsT.Clear();
        temp.Clear();

        for (int column = 0; column < size; column++)
        {
            int rowBase = column * padSize;
            for (int k = 0; k < size; k++)
            {
                matrixT[rowBase + k] = matrix[k * size + column];
                coefficientsT[rowBase + k] = coefficients[k * size + column];
            }
        }

        ref short matrixTRef = ref MemoryMarshal.GetReference(matrixT);
        ref short coefficientsTRef = ref MemoryMarshal.GetReference(coefficientsT);
        ref short tempRef = ref MemoryMarshal.GetReference(temp);
        ref short outputRef = ref MemoryMarshal.GetReference(output);

        // Pass 1 (column transform): temp[i][j] = sum_k matrix[k][i] * coefficients[k][j]
        for (int i = 0; i < size; i++)
        {
            ref short matrixColumn = ref Unsafe.Add(ref matrixTRef, i * padSize);
            ref short row = ref Unsafe.Add(ref tempRef, i * padSize);

            for (int j = 0; j < size; j++)
            {
                int sum = Dot(ref matrixColumn, ref Unsafe.Add(ref coefficientsTRef, j * padSize), padSize);
                Unsafe.Add(ref row, j) = (short)Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Pass 2 (row transform): output[i][j] = sum_k temp[i][k] * matrix[k][j]
        for (int i = 0; i < size; i++)
        {
            ref short row = ref Unsafe.Add(ref tempRef, i * padSize);
            ref short destination = ref Unsafe.Add(ref outputRef, i * size);

            for (int j = 0; j < size; j++)
            {
                int sum = Dot(ref row, ref Unsafe.Add(ref matrixTRef, j * padSize), padSize);
                Unsafe.Add(ref destination, j) =
                    (short)Math.Clamp((sum + add2) >> shift2, short.MinValue, short.MaxValue);
            }
        }
    }

    /// <summary>
    /// Dot product of two zero-padded runs of 16-bit values. <paramref name="length"/> is a multiple
    /// of the vector width, so there is no scalar tail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Dot(ref short left, ref short right, int length)
    {
        if (Avx2.IsSupported && (length & 15) == 0)
        {
            var wide = Vector256<int>.Zero;
            for (int k = 0; k < length; k += 16)
            {
                wide = Avx2.Add(wide, Avx2.MultiplyAddAdjacent(
                    Vector256.LoadUnsafe(ref left, (nuint)k),
                    Vector256.LoadUnsafe(ref right, (nuint)k)));
            }
            return Vector256.Sum(wide);
        }

        var accumulator = Vector128<int>.Zero;
        for (int k = 0; k < length; k += 8)
        {
            accumulator = Sse2.Add(accumulator, Sse2.MultiplyAddAdjacent(
                Vector128.LoadUnsafe(ref left, (nuint)k),
                Vector128.LoadUnsafe(ref right, (nuint)k)));
        }
        return Vector128.Sum(accumulator);
    }

    /// <summary>Horizontal sum of 4 ints in a Vector128.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HorizontalSum(Vector128<int> v)
    {
        var hi64 = Sse2.Shuffle(v, 0x0E); // [c, d, a, b]
        var sum64 = Sse2.Add(v, hi64);    // [a+c, b+d, ...]
        var hi32 = Sse2.Shuffle(sum64, 0x01); // [b+d, ...]
        return Sse2.Add(sum64, hi32).ToScalar();
    }
}

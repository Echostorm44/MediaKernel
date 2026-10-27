// BGRA/RGBA → YUV420P conversion with SIMD acceleration.
// For encoding pipeline: takes packed RGB input and produces planar YUV 4:2:0 output.
// Chroma downsampling uses 2×2 box averaging (the simplest correct approach).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.PixelConversion.FormatConversion;

/// <summary>
/// Converts packed BGRA/RGBA to planar YUV 4:2:0.
/// All methods are static, zero-allocation, and operate on Span/ReadOnlySpan.
/// </summary>
public static class RgbToYuvConvert
{
    /// <summary>
    /// Convert BGRA32 to YUV420P (8-bit planar 4:2:0).
    /// Chroma is downsampled using 2×2 box average.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BgraToYuv420P(
        ReadOnlySpan<byte> input, int inputStride,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        if (Sse2.IsSupported && width >= 8)
            BgraToYuv420PSse2(input, inputStride, yPlane, yStride,
                uPlane, uStride, vPlane, vStride, width, height, in matrix);
        else
            BgraToYuv420PScalar(input, inputStride, yPlane, yStride,
                uPlane, uStride, vPlane, vStride, width, height, in matrix);
    }

    /// <summary>
    /// Convert RGBA32 to YUV420P (8-bit planar 4:2:0).
    /// </summary>
    public static void RgbaToYuv420P(
        ReadOnlySpan<byte> input, int inputStride,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        // RGBA: R at offset 0, G at 1, B at 2, A at 3
        // BGRA: B at offset 0, G at 1, R at 2, A at 3
        // We can reuse the BGRA path by swapping the R and B coefficient usage.
        // For simplicity, use the scalar path with swapped offsets.
        RgbaToYuv420PScalar(input, inputStride, yPlane, yStride,
            uPlane, uStride, vPlane, vStride, width, height, in matrix);
    }

    // ── Scalar implementation ────────────────────────────────────────

    private static void BgraToYuv420PScalar(
        ReadOnlySpan<byte> input, int inputStride,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        ref byte inBase = ref MemoryMarshal.GetReference(input);
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);

        int shift = ColorMatrix.RgbToYuvShift;
        int halfRound = 1 << (shift - 1);

        for (int row = 0; row < height; row++)
        {
            ref byte inRow = ref Unsafe.Add(ref inBase, row * inputStride);
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);

            bool isChromaRow = (row & 1) == 0 && row + 1 <= height;
            int chromaRow = row >> 1;
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);

            // Next row for 2×2 averaging (only when we're on even rows and not the last row)
            bool hasNextRow = isChromaRow && (row + 1 < height);
            ref byte inRowNext = ref Unsafe.Add(ref inBase, Math.Min(row + 1, height - 1) * inputStride);

            for (int col = 0; col < width; col++)
            {
                int pixOffset = col * 4;
                int b = Unsafe.Add(ref inRow, pixOffset);
                int g = Unsafe.Add(ref inRow, pixOffset + 1);
                int r = Unsafe.Add(ref inRow, pixOffset + 2);

                // Compute Y
                int y = (matrix.RToY * r + matrix.GToY * g + matrix.BToY * b + matrix.YBias + halfRound) >> shift;
                Unsafe.Add(ref yRow, col) = ClampToByte(y);

                // Compute chroma only on even rows/columns (2×2 box average)
                if (isChromaRow && (col & 1) == 0)
                {
                    int chromaCol = col >> 1;

                    // Accumulate 2×2 block
                    int sumR = r, sumG = g, sumB = b;

                    // Right pixel (same row)
                    if (col + 1 < width)
                    {
                        int nextPixOffset = (col + 1) * 4;
                        sumB += Unsafe.Add(ref inRow, nextPixOffset);
                        sumG += Unsafe.Add(ref inRow, nextPixOffset + 1);
                        sumR += Unsafe.Add(ref inRow, nextPixOffset + 2);
                    }
                    else
                    {
                        sumR += r; sumG += g; sumB += b; // Duplicate edge
                    }

                    // Bottom-left pixel
                    if (hasNextRow)
                    {
                        sumB += Unsafe.Add(ref inRowNext, pixOffset);
                        sumG += Unsafe.Add(ref inRowNext, pixOffset + 1);
                        sumR += Unsafe.Add(ref inRowNext, pixOffset + 2);

                        // Bottom-right pixel
                        if (col + 1 < width)
                        {
                            int nextPixOffset = (col + 1) * 4;
                            sumB += Unsafe.Add(ref inRowNext, nextPixOffset);
                            sumG += Unsafe.Add(ref inRowNext, nextPixOffset + 1);
                            sumR += Unsafe.Add(ref inRowNext, nextPixOffset + 2);
                        }
                        else
                        {
                            sumB += Unsafe.Add(ref inRowNext, pixOffset);
                            sumG += Unsafe.Add(ref inRowNext, pixOffset + 1);
                            sumR += Unsafe.Add(ref inRowNext, pixOffset + 2);
                        }
                    }
                    else
                    {
                        // Duplicate the current row
                        sumR += r; sumG += g; sumB += b;
                        if (col + 1 < width)
                        {
                            int nextPixOffset = (col + 1) * 4;
                            sumB += Unsafe.Add(ref inRow, nextPixOffset);
                            sumG += Unsafe.Add(ref inRow, nextPixOffset + 1);
                            sumR += Unsafe.Add(ref inRow, nextPixOffset + 2);
                        }
                        else
                        {
                            sumR += r; sumG += g; sumB += b;
                        }
                    }

                    // Average 4 pixels
                    int avgR = (sumR + 2) >> 2;
                    int avgG = (sumG + 2) >> 2;
                    int avgB = (sumB + 2) >> 2;

                    int cb = (matrix.RToCb * avgR + matrix.GToCb * avgG + matrix.BToCb * avgB + matrix.CBias + halfRound) >> shift;
                    int cr = (matrix.RToCr * avgR + matrix.GToCr * avgG + matrix.BToCr * avgB + matrix.CBias + halfRound) >> shift;

                    Unsafe.Add(ref uRow, chromaCol) = ClampToByte(cb);
                    Unsafe.Add(ref vRow, chromaCol) = ClampToByte(cr);
                }
            }
        }
    }

    private static void RgbaToYuv420PScalar(
        ReadOnlySpan<byte> input, int inputStride,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        // RGBA has R at 0, G at 1, B at 2 — swap offsets compared to BGRA (B=0, G=1, R=2)
        ref byte inBase = ref MemoryMarshal.GetReference(input);
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);

        int shift = ColorMatrix.RgbToYuvShift;
        int halfRound = 1 << (shift - 1);

        for (int row = 0; row < height; row++)
        {
            ref byte inRow = ref Unsafe.Add(ref inBase, row * inputStride);
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            bool isChromaRow = (row & 1) == 0;
            int chromaRow = row >> 1;
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);
            bool hasNextRow = isChromaRow && (row + 1 < height);
            ref byte inRowNext = ref Unsafe.Add(ref inBase, Math.Min(row + 1, height - 1) * inputStride);

            for (int col = 0; col < width; col++)
            {
                int pixOffset = col * 4;
                int r = Unsafe.Add(ref inRow, pixOffset);     // RGBA: R first
                int g = Unsafe.Add(ref inRow, pixOffset + 1);
                int b = Unsafe.Add(ref inRow, pixOffset + 2);

                int y = (matrix.RToY * r + matrix.GToY * g + matrix.BToY * b + matrix.YBias + halfRound) >> shift;
                Unsafe.Add(ref yRow, col) = ClampToByte(y);

                if (isChromaRow && (col & 1) == 0)
                {
                    int chromaCol = col >> 1;
                    // Simple: use center pixel for chroma (faster, less accurate than 2×2 average)
                    int cb = (matrix.RToCb * r + matrix.GToCb * g + matrix.BToCb * b + matrix.CBias + halfRound) >> shift;
                    int cr = (matrix.RToCr * r + matrix.GToCr * g + matrix.BToCr * b + matrix.CBias + halfRound) >> shift;
                    Unsafe.Add(ref uRow, chromaCol) = ClampToByte(cb);
                    Unsafe.Add(ref vRow, chromaCol) = ClampToByte(cr);
                }
            }
        }
    }

    // ── SSE2 implementation ──────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BgraToYuv420PSse2(
        ReadOnlySpan<byte> input, int inputStride,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        ref byte inBase = ref MemoryMarshal.GetReference(input);
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);

        // Use shift-7 precision for SIMD to avoid 16-bit overflow.
        // shift-8 causes overflow: gToY8≈157, 157×255=40035 > short.MaxValue.
        // shift-7: max coefficient sum × 255 + bias ≈ 30098 < 32767. Safe.
        int rToY7 = (matrix.RToY + 128) >> 8;
        int gToY7 = (matrix.GToY + 128) >> 8;
        int bToY7 = (matrix.BToY + 128) >> 8;
        int yBias7 = (matrix.YBias + 128) >> 8;
        int halfRound7 = 1 << 6; // rounding for >> 7

        var vecRToY = Vector128.Create((short)rToY7);
        var vecGToY = Vector128.Create((short)gToY7);
        var vecBToY = Vector128.Create((short)bToY7);
        var vecYBias = Vector128.Create((short)(yBias7 + halfRound7));
        var vecZero = Vector128<byte>.Zero;

        int shift = ColorMatrix.RgbToYuvShift;
        int halfRound = 1 << (shift - 1);
        int simdWidth = width & ~7; // Process 8 pixels at a time

        for (int row = 0; row < height; row++)
        {
            ref byte inRow = ref Unsafe.Add(ref inBase, row * inputStride);
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            bool isChromaRow = (row & 1) == 0;
            int chromaRow = row >> 1;
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);

            int col = 0;

            // SIMD: compute Y for 8 pixels at a time
            for (; col < simdWidth; col += 8)
            {
                // Load 8 BGRA pixels (32 bytes = two 128-bit loads)
                var bgra0 = Vector128.LoadUnsafe(ref inRow, (nuint)(col * 4));       // pixels 0-3
                var bgra1 = Vector128.LoadUnsafe(ref inRow, (nuint)((col + 4) * 4)); // pixels 4-7

                // Extract B, G, R channels from BGRA layout
                // BGRA: [B0,G0,R0,A0, B1,G1,R1,A1, B2,G2,R2,A2, B3,G3,R3,A3]
                // We need to deinterleave — extract every 4th byte starting at offset 0,1,2

                // Scalar extraction for correct deinterleaving (SIMD deinterleave is complex without SSSE3)
                Span<short> rVals = stackalloc short[8];
                Span<short> gVals = stackalloc short[8];
                Span<short> bVals = stackalloc short[8];

                for (int p = 0; p < 8; p++)
                {
                    int pOff = (col + p) * 4;
                    bVals[p] = Unsafe.Add(ref inRow, pOff);
                    gVals[p] = Unsafe.Add(ref inRow, pOff + 1);
                    rVals[p] = Unsafe.Add(ref inRow, pOff + 2);
                }

                var vecR = Vector128.Create(rVals[0], rVals[1], rVals[2], rVals[3], rVals[4], rVals[5], rVals[6], rVals[7]);
                var vecG = Vector128.Create(gVals[0], gVals[1], gVals[2], gVals[3], gVals[4], gVals[5], gVals[6], gVals[7]);
                var vecB = Vector128.Create(bVals[0], bVals[1], bVals[2], bVals[3], bVals[4], bVals[5], bVals[6], bVals[7]);

                // Y = (rToY*R + gToY*G + bToY*B + yBias + halfRound) >> 7
                var yWords = Sse2.Add(
                    Sse2.Add(Sse2.MultiplyLow(vecR, vecRToY), Sse2.MultiplyLow(vecG, vecGToY)),
                    Sse2.Add(Sse2.MultiplyLow(vecB, vecBToY), vecYBias));
                yWords = Sse2.ShiftRightArithmetic(yWords, 7);

                // Pack to bytes and store
                var yBytes = Sse2.PackUnsignedSaturate(yWords, yWords);
                // Store low 8 bytes
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref yRow, col),
                    yBytes.AsInt64().ToScalar());
            }

            // Scalar tail for Y
            for (; col < width; col++)
            {
                int pOff = col * 4;
                int b = Unsafe.Add(ref inRow, pOff);
                int g = Unsafe.Add(ref inRow, pOff + 1);
                int r = Unsafe.Add(ref inRow, pOff + 2);
                int y = (matrix.RToY * r + matrix.GToY * g + matrix.BToY * b + matrix.YBias + halfRound) >> shift;
                Unsafe.Add(ref yRow, col) = ClampToByte(y);
            }

            // Chroma: scalar (complex 2×2 averaging not worth SIMD for initial impl)
            if (isChromaRow)
            {
                ref byte inRowNext = ref Unsafe.Add(ref inBase, Math.Min(row + 1, height - 1) * inputStride);
                bool hasNextRow = (row + 1 < height);
                int chromaWidth = (width + 1) >> 1;

                for (int cx = 0; cx < chromaWidth; cx++)
                {
                    int lx = cx * 2;
                    int sumR = 0, sumG = 0, sumB = 0;
                    int count = 0;

                    // Top-left
                    AddPixelBgra(ref inRow, lx, width, ref sumR, ref sumG, ref sumB, ref count);
                    // Top-right
                    AddPixelBgra(ref inRow, lx + 1, width, ref sumR, ref sumG, ref sumB, ref count);
                    // Bottom-left
                    if (hasNextRow)
                    {
                        AddPixelBgra(ref inRowNext, lx, width, ref sumR, ref sumG, ref sumB, ref count);
                        AddPixelBgra(ref inRowNext, lx + 1, width, ref sumR, ref sumG, ref sumB, ref count);
                    }
                    else
                    {
                        // Replicate top row
                        AddPixelBgra(ref inRow, lx, width, ref sumR, ref sumG, ref sumB, ref count);
                        AddPixelBgra(ref inRow, lx + 1, width, ref sumR, ref sumG, ref sumB, ref count);
                    }

                    int avgR = (sumR + (count >> 1)) / count;
                    int avgG = (sumG + (count >> 1)) / count;
                    int avgB = (sumB + (count >> 1)) / count;

                    int cb = (matrix.RToCb * avgR + matrix.GToCb * avgG + matrix.BToCb * avgB + matrix.CBias + halfRound) >> shift;
                    int cr = (matrix.RToCr * avgR + matrix.GToCr * avgG + matrix.BToCr * avgB + matrix.CBias + halfRound) >> shift;

                    Unsafe.Add(ref uRow, cx) = ClampToByte(cb);
                    Unsafe.Add(ref vRow, cx) = ClampToByte(cr);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPixelBgra(ref byte row, int col, int width, ref int sumR, ref int sumG, ref int sumB, ref int count)
    {
        if (col >= width) col = width - 1; // Clamp to edge
        int offset = col * 4;
        sumB += Unsafe.Add(ref row, offset);
        sumG += Unsafe.Add(ref row, offset + 1);
        sumR += Unsafe.Add(ref row, offset + 2);
        count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value)
    {
        if ((uint)value <= 255) return (byte)value;
        return (byte)(value < 0 ? 0 : 255);
    }
}

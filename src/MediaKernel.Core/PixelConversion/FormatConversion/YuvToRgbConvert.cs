// YUV420P → BGRA/RGBA conversion with SIMD acceleration.
// Handles planar 4:2:0 input (I420) to packed 32-bit RGB output.
// Uses the existing YuvToRgbConverter SIMD kernels where available,
// plus new scalar/SIMD paths for additional format combinations.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.PixelConversion.FormatConversion;

/// <summary>
/// Converts planar YUV 4:2:0 (8-bit and 10-bit) to packed BGRA/RGBA.
/// All methods are static, zero-allocation, and operate on Span/ReadOnlySpan.
/// Uses SSE4.1 (pmulld) for 32-bit SIMD multiply — virtually all x86 CPUs since 2008.
/// </summary>
public static class YuvToRgbConvert
{
    /// <summary>
    /// Convert YUV420P (8-bit planar) to BGRA32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Yuv420PToBgra(
        ReadOnlySpan<byte> yPlane, int yStride,
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> output, int outputStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        if (Sse41.IsSupported && width >= 4)
            Yuv420PToBgraSse2(yPlane, yStride, uPlane, uStride, vPlane, vStride,
                output, outputStride, width, height, in matrix);
        else
            Yuv420PToBgraScalar(yPlane, yStride, uPlane, uStride, vPlane, vStride,
                output, outputStride, width, height, in matrix);
    }

    /// <summary>
    /// Convert YUV420P10 (10-bit planar) to BGRA32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Yuv420P10ToBgra(
        ReadOnlySpan<byte> yPlane, int yStride,
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> output, int outputStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        // 10-bit values stored as 16-bit little-endian
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);
        ref byte outBase = ref MemoryMarshal.GetReference(output);

        int chromaHeight = (height + 1) >> 1;

        for (int row = 0; row < height; row++)
        {
            int chromaRow = row >> 1;
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);
            ref byte outRow = ref Unsafe.Add(ref outBase, row * outputStride);

            for (int col = 0; col < width; col++)
            {
                int chromaCol = col >> 1;

                // Read 10-bit values (stored in low 10 bits of 16-bit words)
                int y10 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref yRow, col * 2));
                int u10 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref uRow, chromaCol * 2));
                int v10 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vRow, chromaCol * 2));

                // Scale from 10-bit to 8-bit equivalent for matrix multiplication
                // 10-bit limited range: Y=[64,940], C=[64,960], center=512
                int y = y10 - (matrix.YOffset << 2); // offset scaled to 10-bit
                int cb = u10 - 512;
                int cr = v10 - 512;

                // Use fixed-point matrix (shift 16), but input is 10-bit so adjust shift
                // yMul already has shift 16 baked in for 8-bit input
                // For 10-bit: extra 2 bits → need shift 18 total
                int r = (matrix.YMultiplier * y + matrix.CrToR * cr + (1 << 17)) >> 18;
                int g = (matrix.YMultiplier * y + matrix.CrToG * cr + matrix.CbToG * cb + (1 << 17)) >> 18;
                int b = (matrix.YMultiplier * y + matrix.CbToB * cb + (1 << 17)) >> 18;

                int outOffset = col * 4;
                Unsafe.Add(ref outRow, outOffset) = ClampToByte(b);
                Unsafe.Add(ref outRow, outOffset + 1) = ClampToByte(g);
                Unsafe.Add(ref outRow, outOffset + 2) = ClampToByte(r);
                Unsafe.Add(ref outRow, outOffset + 3) = 255;
            }
        }
    }

    /// <summary>
    /// Convert grayscale (Y-only) to BGRA32.
    /// </summary>
    public static void GrayToBgra(
        ReadOnlySpan<byte> yPlane, int yStride,
        Span<byte> output, int outputStride,
        int width, int height)
    {
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte outBase = ref MemoryMarshal.GetReference(output);

        for (int row = 0; row < height; row++)
        {
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            ref byte outRow = ref Unsafe.Add(ref outBase, row * outputStride);

            for (int col = 0; col < width; col++)
            {
                byte luma = Unsafe.Add(ref yRow, col);
                int outOffset = col * 4;
                Unsafe.Add(ref outRow, outOffset) = luma;     // B
                Unsafe.Add(ref outRow, outOffset + 1) = luma; // G
                Unsafe.Add(ref outRow, outOffset + 2) = luma; // R
                Unsafe.Add(ref outRow, outOffset + 3) = 255;  // A
            }
        }
    }

    // ── SIMD implementation ─────────────────────────────────────────
    // Uses 32-bit integer arithmetic to avoid 16-bit overflow.
    // SSE4.1 provides _mm_mullo_epi32 for 32×32→32 multiply.
    // Falls back to scalar if only SSE2 is available.

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Yuv420PToBgraSse2(
        ReadOnlySpan<byte> yPlane, int yStride,
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> output, int outputStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        if (!Sse41.IsSupported)
        {
            // Without SSE4.1 (pmulld), fall back to scalar to avoid 16-bit overflow
            Yuv420PToBgraScalar(yPlane, yStride, uPlane, uStride, vPlane, vStride,
                output, outputStride, width, height, in matrix);
            return;
        }

        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);
        ref byte outBase = ref MemoryMarshal.GetReference(output);

        // 32-bit coefficient vectors (full shift-16 precision, no overflow)
        var vecYMul = Vector128.Create(matrix.YMultiplier);
        var vecCrToR = Vector128.Create(matrix.CrToR);
        var vecCrToG = Vector128.Create(matrix.CrToG);
        var vecCbToG = Vector128.Create(matrix.CbToG);
        var vecCbToB = Vector128.Create(matrix.CbToB);
        var vecRound = Vector128.Create(1 << 15);
        var vecZero32 = Vector128<int>.Zero;
        var vec255 = Vector128.Create(255);
        int yOffset = matrix.YOffset;

        for (int row = 0; row < height; row++)
        {
            int chromaRow = row >> 1;
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);
            ref byte outRow = ref Unsafe.Add(ref outBase, row * outputStride);

            int col = 0;
            int simdWidth = width & ~3; // Process 4 pixels at a time (32-bit lanes)

            for (; col < simdWidth; col += 4)
            {
                // Load 4 Y values → zero-extend to 32-bit → subtract offset
                var y0 = (int)Unsafe.Add(ref yRow, col) - yOffset;
                var y1 = (int)Unsafe.Add(ref yRow, col + 1) - yOffset;
                var y2 = (int)Unsafe.Add(ref yRow, col + 2) - yOffset;
                var y3 = (int)Unsafe.Add(ref yRow, col + 3) - yOffset;
                var yVec = Vector128.Create(y0, y1, y2, y3);

                // Load 2 chroma pairs → duplicate for 4 luma pixels
                int chromaCol = col >> 1;
                int u0 = (int)Unsafe.Add(ref uRow, chromaCol) - 128;
                int u1 = (int)Unsafe.Add(ref uRow, chromaCol + 1) - 128;
                int v0 = (int)Unsafe.Add(ref vRow, chromaCol) - 128;
                int v1 = (int)Unsafe.Add(ref vRow, chromaCol + 1) - 128;
                var cbVec = Vector128.Create(u0, u0, u1, u1);
                var crVec = Vector128.Create(v0, v0, v1, v1);

                // 32-bit matrix multiply: R = (ymul*y + crToR*cr + round) >> 16
                var yScaled = Sse41.MultiplyLow(yVec, vecYMul);
                var r32 = Sse2.ShiftRightArithmetic(
                    Sse2.Add(Sse2.Add(yScaled, Sse41.MultiplyLow(crVec, vecCrToR)), vecRound), 16);
                var g32 = Sse2.ShiftRightArithmetic(
                    Sse2.Add(Sse2.Add(Sse2.Add(yScaled,
                        Sse41.MultiplyLow(crVec, vecCrToG)),
                        Sse41.MultiplyLow(cbVec, vecCbToG)), vecRound), 16);
                var b32 = Sse2.ShiftRightArithmetic(
                    Sse2.Add(Sse2.Add(yScaled, Sse41.MultiplyLow(cbVec, vecCbToB)), vecRound), 16);

                // Clamp to [0, 255]
                r32 = Sse41.Min(Sse41.Max(r32, vecZero32), vec255);
                g32 = Sse41.Min(Sse41.Max(g32, vecZero32), vec255);
                b32 = Sse41.Min(Sse41.Max(b32, vecZero32), vec255);

                // Pack BGRA: b | (g<<8) | (r<<16) | (0xFF<<24)
                var bgra = Sse2.Or(Sse2.Or(Sse2.Or(
                    b32,
                    Sse2.ShiftLeftLogical(g32, 8)),
                    Sse2.ShiftLeftLogical(r32, 16)),
                    Vector128.Create(unchecked((int)0xFF000000)));

                bgra.StoreUnsafe(ref Unsafe.As<byte, int>(ref outRow), (nuint)col);
            }

            // Scalar tail
            for (; col < width; col++)
                ConvertOnePixelScalar(ref yRow, ref uRow, ref vRow, ref outRow, col, in matrix);
        }
    }

    // ── Scalar implementation ────────────────────────────────────────

    private static void Yuv420PToBgraScalar(
        ReadOnlySpan<byte> yPlane, int yStride,
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> output, int outputStride,
        int width, int height,
        in ColorMatrix matrix)
    {
        ref byte yBase = ref MemoryMarshal.GetReference(yPlane);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);
        ref byte outBase = ref MemoryMarshal.GetReference(output);

        for (int row = 0; row < height; row++)
        {
            ref byte yRow = ref Unsafe.Add(ref yBase, row * yStride);
            int chromaRow = row >> 1;
            ref byte uRow = ref Unsafe.Add(ref uBase, chromaRow * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, chromaRow * vStride);
            ref byte outRow = ref Unsafe.Add(ref outBase, row * outputStride);

            for (int col = 0; col < width; col++)
                ConvertOnePixelScalar(ref yRow, ref uRow, ref vRow, ref outRow, col, in matrix);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertOnePixelScalar(
        ref byte yRow, ref byte uRow, ref byte vRow, ref byte outRow,
        int col, in ColorMatrix matrix)
    {
        int chromaCol = col >> 1;
        int y = Unsafe.Add(ref yRow, col) - matrix.YOffset;
        int cb = Unsafe.Add(ref uRow, chromaCol) - 128;
        int cr = Unsafe.Add(ref vRow, chromaCol) - 128;

        int r = (matrix.YMultiplier * y + matrix.CrToR * cr + (1 << 15)) >> ColorMatrix.YuvToRgbShift;
        int g = (matrix.YMultiplier * y + matrix.CrToG * cr + matrix.CbToG * cb + (1 << 15)) >> ColorMatrix.YuvToRgbShift;
        int b = (matrix.YMultiplier * y + matrix.CbToB * cb + (1 << 15)) >> ColorMatrix.YuvToRgbShift;

        int outOffset = col * 4;
        Unsafe.Add(ref outRow, outOffset) = ClampToByte(b);
        Unsafe.Add(ref outRow, outOffset + 1) = ClampToByte(g);
        Unsafe.Add(ref outRow, outOffset + 2) = ClampToByte(r);
        Unsafe.Add(ref outRow, outOffset + 3) = 255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(int value)
    {
        if ((uint)value <= 255) return (byte)value;
        return (byte)(value < 0 ? 0 : 255);
    }
}

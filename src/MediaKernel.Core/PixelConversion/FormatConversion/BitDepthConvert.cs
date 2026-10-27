// Bit depth conversion for video pixel data.
// Converts between 8-bit, 10-bit, 12-bit, and 16-bit sample depths.
// All operations are plane-at-a-time for maximum cache efficiency.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.PixelConversion.FormatConversion;

/// <summary>
/// Converts between bit depths for planar video data.
/// 10/12/16-bit samples are stored as 16-bit little-endian (ushort).
/// 8-bit samples are stored as bytes.
/// </summary>
public static class BitDepthConvert
{
    /// <summary>
    /// Convert a plane from 10-bit (ushort, low 10 bits) to 8-bit (byte).
    /// Uses right-shift by 2 with rounding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Convert10To8(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertHighToLow(src, srcStride, dst, dstStride, width, height, rightShift: 2);
    }

    /// <summary>
    /// Convert a plane from 12-bit (ushort, low 12 bits) to 8-bit (byte).
    /// Uses right-shift by 4 with rounding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Convert12To8(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertHighToLow(src, srcStride, dst, dstStride, width, height, rightShift: 4);
    }

    /// <summary>
    /// Convert a plane from 16-bit to 8-bit.
    /// </summary>
    public static void Convert16To8(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertHighToLow(src, srcStride, dst, dstStride, width, height, rightShift: 8);
    }

    /// <summary>
    /// Convert a plane from 8-bit (byte) to 10-bit (ushort, low 10 bits).
    /// Uses left-shift by 2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Convert8To10(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertLowToHigh(src, srcStride, dst, dstStride, width, height, leftShift: 2);
    }

    /// <summary>
    /// Convert a plane from 8-bit to 12-bit.
    /// </summary>
    public static void Convert8To12(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertLowToHigh(src, srcStride, dst, dstStride, width, height, leftShift: 4);
    }

    /// <summary>
    /// Convert a plane from 10-bit to 12-bit.
    /// </summary>
    public static void Convert10To12(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertHighBitDepths(src, srcStride, dst, dstStride, width, height, leftShift: 2);
    }

    /// <summary>
    /// Convert a plane from 12-bit to 10-bit.
    /// </summary>
    public static void Convert12To10(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        ConvertHighBitDepths(src, srcStride, dst, dstStride, width, height, leftShift: -2);
    }

    // ── High-to-low (16-bit → 8-bit) ────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertHighToLow(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height, int rightShift)
    {
        ref byte srcBase = ref MemoryMarshal.GetReference(src);
        ref byte dstBase = ref MemoryMarshal.GetReference(dst);
        int roundAdd = 1 << (rightShift - 1);

        for (int row = 0; row < height; row++)
        {
            ref byte srcRow = ref Unsafe.Add(ref srcBase, row * srcStride);
            ref byte dstRow = ref Unsafe.Add(ref dstBase, row * dstStride);

            int col = 0;

            if (Sse2.IsSupported)
            {
                var vecRound = Vector128.Create((ushort)roundAdd);
                int simdWidth = width & ~7; // Process 8 samples at a time

                for (; col < simdWidth; col += 8)
                {
                    var samples = Vector128.LoadUnsafe(ref srcRow, (nuint)(col * 2)).AsUInt16();

                    // Add rounding and shift
                    samples = Sse2.Add(samples, vecRound);
                    samples = Sse2.ShiftRightLogical(samples, (byte)rightShift);

                    // Pack 16-bit to 8-bit with unsigned saturation
                    var packed = Sse2.PackUnsignedSaturate(samples.AsInt16(), samples.AsInt16());

                    // Store low 8 bytes
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow, col), packed.AsInt64().ToScalar());
                }
            }

            // Scalar tail
            for (; col < width; col++)
            {
                int val = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref srcRow, col * 2));
                val = (val + roundAdd) >> rightShift;
                Unsafe.Add(ref dstRow, col) = (byte)Math.Min(val, 255);
            }
        }
    }

    // ── Low-to-high (8-bit → 16-bit) ────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertLowToHigh(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height, int leftShift)
    {
        ref byte srcBase = ref MemoryMarshal.GetReference(src);
        ref byte dstBase = ref MemoryMarshal.GetReference(dst);

        for (int row = 0; row < height; row++)
        {
            ref byte srcRow = ref Unsafe.Add(ref srcBase, row * srcStride);
            ref byte dstRow = ref Unsafe.Add(ref dstBase, row * dstStride);

            int col = 0;

            if (Sse2.IsSupported)
            {
                var vecZero = Vector128<byte>.Zero;
                int simdWidth = width & ~15; // Process 16 samples at a time

                for (; col < simdWidth; col += 16)
                {
                    var bytes = Vector128.LoadUnsafe(ref srcRow, (nuint)col);

                    // Unpack bytes to 16-bit words
                    var lo = Sse2.UnpackLow(bytes, vecZero).AsUInt16();
                    var hi = Sse2.UnpackHigh(bytes, vecZero).AsUInt16();

                    // Left-shift to target bit depth
                    lo = Sse2.ShiftLeftLogical(lo, (byte)leftShift);
                    hi = Sse2.ShiftLeftLogical(hi, (byte)leftShift);

                    // Store as 16-bit words
                    lo.AsByte().StoreUnsafe(ref dstRow, (nuint)(col * 2));
                    hi.AsByte().StoreUnsafe(ref dstRow, (nuint)((col + 8) * 2));
                }
            }

            // Scalar tail
            for (; col < width; col++)
            {
                ushort val = (ushort)(Unsafe.Add(ref srcRow, col) << leftShift);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow, col * 2), val);
            }
        }
    }

    // ── Between high bit depths (16-bit ↔ 16-bit) ───────────────────

    private static void ConvertHighBitDepths(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height, int leftShift)
    {
        ref byte srcBase = ref MemoryMarshal.GetReference(src);
        ref byte dstBase = ref MemoryMarshal.GetReference(dst);

        for (int row = 0; row < height; row++)
        {
            ref byte srcRow = ref Unsafe.Add(ref srcBase, row * srcStride);
            ref byte dstRow = ref Unsafe.Add(ref dstBase, row * dstStride);

            for (int col = 0; col < width; col++)
            {
                ushort val = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref srcRow, col * 2));
                if (leftShift > 0)
                    val = (ushort)(val << leftShift);
                else
                    val = (ushort)(val >> (-leftShift));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow, col * 2), val);
            }
        }
    }
}

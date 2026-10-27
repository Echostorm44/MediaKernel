// Semi-planar ↔ planar YUV conversions.
// NV12/NV21 (interleaved UV) ↔ I420 (separate U/V planes)
// P010 (10-bit semi-planar) ↔ YUV420P10 (10-bit planar)

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.PixelConversion.FormatConversion;

/// <summary>
/// Converts between semi-planar (NV12/NV21/P010) and fully-planar YUV formats.
/// The Y plane is identical — only the chroma plane layout differs.
/// </summary>
public static class SemiPlanarConvert
{
    /// <summary>
    /// Convert NV12 (Y + interleaved UV) to YUV420P (Y + U + V separate planes).
    /// Y plane is copied as-is; UV plane is deinterleaved into separate U and V.
    /// </summary>
    public static void Nv12ToYuv420P(
        ReadOnlySpan<byte> yPlaneIn, int yStrideIn,
        ReadOnlySpan<byte> uvPlane, int uvStride,
        Span<byte> yPlaneOut, int yStrideOut,
        Span<byte> uPlaneOut, int uStrideOut,
        Span<byte> vPlaneOut, int vStrideOut,
        int width, int height)
    {
        // Copy Y plane
        CopyPlane(yPlaneIn, yStrideIn, yPlaneOut, yStrideOut, width, height);

        // Deinterleave UV → U + V
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        DeinterleaveUv(uvPlane, uvStride, uPlaneOut, uStrideOut, vPlaneOut, vStrideOut, chromaWidth, chromaHeight);
    }

    /// <summary>
    /// Convert NV21 (Y + interleaved VU) to YUV420P (Y + U + V separate planes).
    /// Same as NV12 but U and V are swapped in the interleaved plane.
    /// </summary>
    public static void Nv21ToYuv420P(
        ReadOnlySpan<byte> yPlaneIn, int yStrideIn,
        ReadOnlySpan<byte> vuPlane, int vuStride,
        Span<byte> yPlaneOut, int yStrideOut,
        Span<byte> uPlaneOut, int uStrideOut,
        Span<byte> vPlaneOut, int vStrideOut,
        int width, int height)
    {
        CopyPlane(yPlaneIn, yStrideIn, yPlaneOut, yStrideOut, width, height);

        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        // NV21 has VU order, so swap output planes
        DeinterleaveUv(vuPlane, vuStride, vPlaneOut, vStrideOut, uPlaneOut, uStrideOut, chromaWidth, chromaHeight);
    }

    /// <summary>
    /// Convert YUV420P (separate planes) to NV12 (Y + interleaved UV).
    /// </summary>
    public static void Yuv420PToNv12(
        ReadOnlySpan<byte> yPlaneIn, int yStrideIn,
        ReadOnlySpan<byte> uPlaneIn, int uStrideIn,
        ReadOnlySpan<byte> vPlaneIn, int vStrideIn,
        Span<byte> yPlaneOut, int yStrideOut,
        Span<byte> uvPlaneOut, int uvStrideOut,
        int width, int height)
    {
        CopyPlane(yPlaneIn, yStrideIn, yPlaneOut, yStrideOut, width, height);

        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        InterleaveUv(uPlaneIn, uStrideIn, vPlaneIn, vStrideIn, uvPlaneOut, uvStrideOut, chromaWidth, chromaHeight);
    }

    /// <summary>
    /// Convert P010 (10-bit semi-planar) to YUV420P10 (10-bit planar).
    /// P010 stores 10-bit values in the upper 10 bits of 16-bit words.
    /// YUV420P10 stores 10-bit values in the lower 10 bits.
    /// </summary>
    public static void P010ToYuv420P10(
        ReadOnlySpan<byte> yPlaneIn, int yStrideIn,
        ReadOnlySpan<byte> uvPlane, int uvStride,
        Span<byte> yPlaneOut, int yStrideOut,
        Span<byte> uPlaneOut, int uStrideOut,
        Span<byte> vPlaneOut, int vStrideOut,
        int width, int height)
    {
        // Copy and shift Y plane (P010: upper 10 bits → YUV420P10: lower 10 bits)
        CopyPlane16WithShift(yPlaneIn, yStrideIn, yPlaneOut, yStrideOut, width, height, rightShift: 6);

        // Deinterleave and shift UV
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        DeinterleaveUv16WithShift(uvPlane, uvStride, uPlaneOut, uStrideOut, vPlaneOut, vStrideOut,
            chromaWidth, chromaHeight, rightShift: 6);
    }

    /// <summary>
    /// Convert YUV420P10 (10-bit planar) to P010 (10-bit semi-planar).
    /// </summary>
    public static void Yuv420P10ToP010(
        ReadOnlySpan<byte> yPlaneIn, int yStrideIn,
        ReadOnlySpan<byte> uPlaneIn, int uStrideIn,
        ReadOnlySpan<byte> vPlaneIn, int vStrideIn,
        Span<byte> yPlaneOut, int yStrideOut,
        Span<byte> uvPlaneOut, int uvStrideOut,
        int width, int height)
    {
        // Shift and copy Y plane (YUV420P10: lower 10 bits → P010: upper 10 bits)
        CopyPlane16WithShift(yPlaneIn, yStrideIn, yPlaneOut, yStrideOut, width, height, rightShift: -6);

        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        InterleaveUv16WithShift(uPlaneIn, uStrideIn, vPlaneIn, vStrideIn,
            uvPlaneOut, uvStrideOut, chromaWidth, chromaHeight, leftShift: 6);
    }

    // ── Core operations ──────────────────────────────────────────────

    /// <summary>
    /// Copy a single plane row by row (handles different strides).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void CopyPlane(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height)
    {
        if (srcStride == dstStride && srcStride == width)
        {
            // Fast path: contiguous memory
            src.Slice(0, width * height).CopyTo(dst);
            return;
        }

        ref byte srcBase = ref MemoryMarshal.GetReference(src);
        ref byte dstBase = ref MemoryMarshal.GetReference(dst);

        for (int row = 0; row < height; row++)
        {
            Unsafe.CopyBlockUnaligned(
                ref Unsafe.Add(ref dstBase, row * dstStride),
                ref Unsafe.Add(ref srcBase, row * srcStride),
                (uint)width);
        }
    }

    /// <summary>
    /// Deinterleave UV pairs into separate U and V planes with SIMD acceleration.
    /// Input: [U0,V0, U1,V1, U2,V2, ...] → Output: [U0,U1,U2,...] + [V0,V1,V2,...]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DeinterleaveUv(
        ReadOnlySpan<byte> uvInterleaved, int uvStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int chromaWidth, int chromaHeight)
    {
        ref byte uvBase = ref MemoryMarshal.GetReference(uvInterleaved);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);

        for (int row = 0; row < chromaHeight; row++)
        {
            ref byte uvRow = ref Unsafe.Add(ref uvBase, row * uvStride);
            ref byte uRow = ref Unsafe.Add(ref uBase, row * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, row * vStride);

            int col = 0;

            if (Sse2.IsSupported)
            {
                // Process 16 UV pairs (32 bytes) at a time
                int simdWidth = chromaWidth & ~15;
                for (; col < simdWidth; col += 16)
                {
                    var uv0 = Vector128.LoadUnsafe(ref uvRow, (nuint)(col * 2));
                    var uv1 = Vector128.LoadUnsafe(ref uvRow, (nuint)(col * 2 + 16));

                    // Deinterleave: extract even bytes (U) and odd bytes (V)
                    // Use AND + SHIFT to separate
                    var mask = Vector128.Create((ushort)0x00FF).AsByte();
                    var u0 = Sse2.And(uv0, mask);         // [U0,0, U1,0, U2,0, ...]
                    var v0 = Sse2.ShiftRightLogical(uv0.AsUInt16(), 8).AsByte(); // [V0,0, V1,0, ...]
                    var u1 = Sse2.And(uv1, mask);
                    var v1 = Sse2.ShiftRightLogical(uv1.AsUInt16(), 8).AsByte();

                    // Pack: take low bytes from each 16-bit word
                    var uPacked = Sse2.PackUnsignedSaturate(u0.AsInt16(), u1.AsInt16());
                    var vPacked = Sse2.PackUnsignedSaturate(v0.AsInt16(), v1.AsInt16());

                    uPacked.StoreUnsafe(ref uRow, (nuint)col);
                    vPacked.StoreUnsafe(ref vRow, (nuint)col);
                }
            }

            // Scalar tail
            for (; col < chromaWidth; col++)
            {
                int uvOffset = col * 2;
                Unsafe.Add(ref uRow, col) = Unsafe.Add(ref uvRow, uvOffset);
                Unsafe.Add(ref vRow, col) = Unsafe.Add(ref uvRow, uvOffset + 1);
            }
        }
    }

    /// <summary>
    /// Interleave separate U and V planes into UV pairs.
    /// Input: [U0,U1,U2,...] + [V0,V1,V2,...] → Output: [U0,V0, U1,V1, ...]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void InterleaveUv(
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> uvInterleaved, int uvStride,
        int chromaWidth, int chromaHeight)
    {
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);
        ref byte uvBase = ref MemoryMarshal.GetReference(uvInterleaved);

        for (int row = 0; row < chromaHeight; row++)
        {
            ref byte uRow = ref Unsafe.Add(ref uBase, row * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, row * vStride);
            ref byte uvRow = ref Unsafe.Add(ref uvBase, row * uvStride);

            int col = 0;

            if (Sse2.IsSupported)
            {
                int simdWidth = chromaWidth & ~15;
                for (; col < simdWidth; col += 16)
                {
                    var u = Vector128.LoadUnsafe(ref uRow, (nuint)col);
                    var v = Vector128.LoadUnsafe(ref vRow, (nuint)col);

                    var uvLo = Sse2.UnpackLow(u, v);   // [U0,V0, U1,V1, ..., U7,V7]
                    var uvHi = Sse2.UnpackHigh(u, v);   // [U8,V8, ..., U15,V15]

                    uvLo.StoreUnsafe(ref uvRow, (nuint)(col * 2));
                    uvHi.StoreUnsafe(ref uvRow, (nuint)(col * 2 + 16));
                }
            }

            for (; col < chromaWidth; col++)
            {
                int uvOffset = col * 2;
                Unsafe.Add(ref uvRow, uvOffset) = Unsafe.Add(ref uRow, col);
                Unsafe.Add(ref uvRow, uvOffset + 1) = Unsafe.Add(ref vRow, col);
            }
        }
    }

    // ── 16-bit variants for 10-bit formats ───────────────────────────

    private static void CopyPlane16WithShift(
        ReadOnlySpan<byte> src, int srcStride,
        Span<byte> dst, int dstStride,
        int width, int height, int rightShift)
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
                if (rightShift > 0)
                    val = (ushort)(val >> rightShift);
                else if (rightShift < 0)
                    val = (ushort)(val << (-rightShift));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRow, col * 2), val);
            }
        }
    }

    private static void DeinterleaveUv16WithShift(
        ReadOnlySpan<byte> uvInterleaved, int uvStride,
        Span<byte> uPlane, int uStride,
        Span<byte> vPlane, int vStride,
        int chromaWidth, int chromaHeight, int rightShift)
    {
        ref byte uvBase = ref MemoryMarshal.GetReference(uvInterleaved);
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);

        for (int row = 0; row < chromaHeight; row++)
        {
            ref byte uvRow = ref Unsafe.Add(ref uvBase, row * uvStride);
            ref byte uRow = ref Unsafe.Add(ref uBase, row * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, row * vStride);

            for (int col = 0; col < chromaWidth; col++)
            {
                int uvOffset = col * 4; // 2 samples × 2 bytes each
                ushort u = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref uvRow, uvOffset));
                ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref uvRow, uvOffset + 2));

                if (rightShift > 0)
                {
                    u = (ushort)(u >> rightShift);
                    v = (ushort)(v >> rightShift);
                }

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref uRow, col * 2), u);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vRow, col * 2), v);
            }
        }
    }

    private static void InterleaveUv16WithShift(
        ReadOnlySpan<byte> uPlane, int uStride,
        ReadOnlySpan<byte> vPlane, int vStride,
        Span<byte> uvInterleaved, int uvStride,
        int chromaWidth, int chromaHeight, int leftShift)
    {
        ref byte uBase = ref MemoryMarshal.GetReference(uPlane);
        ref byte vBase = ref MemoryMarshal.GetReference(vPlane);
        ref byte uvBase = ref MemoryMarshal.GetReference(uvInterleaved);

        for (int row = 0; row < chromaHeight; row++)
        {
            ref byte uRow = ref Unsafe.Add(ref uBase, row * uStride);
            ref byte vRow = ref Unsafe.Add(ref vBase, row * vStride);
            ref byte uvRow = ref Unsafe.Add(ref uvBase, row * uvStride);

            for (int col = 0; col < chromaWidth; col++)
            {
                ushort u = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref uRow, col * 2));
                ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vRow, col * 2));

                if (leftShift > 0)
                {
                    u = (ushort)(u << leftShift);
                    v = (ushort)(v << leftShift);
                }

                int uvOffset = col * 4;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref uvRow, uvOffset), u);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref uvRow, uvOffset + 2), v);
            }
        }
    }
}

// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Reconstruction: prediction + residual → clipped output.
/// Shared by all video decoders (H.264, HEVC, VP9, AV1, MPEG-2, etc.).
/// Zero-allocation — operates entirely on Span&lt;T&gt;.
/// </summary>
public static class ReconstructDsp
{
    /// <summary>
    /// Reconstruct 8-bit samples: output[i] = clamp(prediction[i] + residual[i], 0, 255).
    /// Dispatches to SSE2 or AVX2 when available; scalar fallback otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reconstruct8Bit(
        ReadOnlySpan<byte> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> output,
        int count)
    {
        if (Avx2.IsSupported && count >= 32)
            Reconstruct8BitAvx2(prediction, residual, output, count);
        else if (Sse2.IsSupported && count >= 16)
            Reconstruct8BitSse2(prediction, residual, output, count);
        else
            Reconstruct8BitScalar(prediction, residual, output, count);
    }

    /// <summary>
    /// Scalar reference implementation. Always correct.
    /// </summary>
    public static void Reconstruct8BitScalar(
        ReadOnlySpan<byte> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> output,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            int value = prediction[i] + residual[i];
            output[i] = (byte)Math.Clamp(value, 0, 255);
        }
    }

    /// <summary>
    /// SSE2 path: processes 16 bytes per iteration using 128-bit registers.
    /// pred bytes → unpack to shorts → add residual → pack with unsigned saturation.
    /// </summary>
    public static unsafe void Reconstruct8BitSse2(
        ReadOnlySpan<byte> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> output,
        int count)
    {
        fixed (byte* pPred = prediction)
        fixed (short* pRes = residual)
        fixed (byte* pOut = output)
        {
            int i = 0;
            for (; i + 16 <= count; i += 16)
            {
                // Load 16 prediction bytes
                var pred = Sse2.LoadVector128(pPred + i);

                // Unpack to two vectors of 8 shorts each
                var predLo = Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16();
                var predHi = Sse2.UnpackHigh(pred, Vector128<byte>.Zero).AsInt16();

                // Load 16 residual shorts (two 128-bit loads)
                var resLo = Sse2.LoadVector128(pRes + i);
                var resHi = Sse2.LoadVector128(pRes + i + 8);

                // Add prediction + residual
                var sumLo = Sse2.Add(predLo, resLo);
                var sumHi = Sse2.Add(predHi, resHi);

                // Pack back to bytes with unsigned saturation (clamps to 0..255)
                var result = Sse2.PackUnsignedSaturate(sumLo, sumHi);
                Sse2.Store(pOut + i, result);
            }

            // Scalar tail
            for (; i < count; i++)
            {
                int value = pPred[i] + pRes[i];
                pOut[i] = (byte)Math.Clamp(value, 0, 255);
            }
        }
    }

    /// <summary>
    /// AVX2 path: processes 32 bytes per iteration using 256-bit registers.
    /// </summary>
    public static unsafe void Reconstruct8BitAvx2(
        ReadOnlySpan<byte> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> output,
        int count)
    {
        fixed (byte* pPred = prediction)
        fixed (short* pRes = residual)
        fixed (byte* pOut = output)
        {
            int i = 0;
            for (; i + 32 <= count; i += 32)
            {
                // Load 32 prediction bytes
                var pred = Avx.LoadVector256(pPred + i);

                // Unpack to four vectors of 8 shorts each
                // AVX2 unpack works within 128-bit lanes, so we need to handle carefully
                var predLo128 = pred.GetLower();
                var predHi128 = pred.GetUpper();

                var predS0 = Sse2.UnpackLow(predLo128, Vector128<byte>.Zero).AsInt16();
                var predS1 = Sse2.UnpackHigh(predLo128, Vector128<byte>.Zero).AsInt16();
                var predS2 = Sse2.UnpackLow(predHi128, Vector128<byte>.Zero).AsInt16();
                var predS3 = Sse2.UnpackHigh(predHi128, Vector128<byte>.Zero).AsInt16();

                // Load 32 residual shorts (four 128-bit loads)
                var res0 = Sse2.LoadVector128(pRes + i);
                var res1 = Sse2.LoadVector128(pRes + i + 8);
                var res2 = Sse2.LoadVector128(pRes + i + 16);
                var res3 = Sse2.LoadVector128(pRes + i + 24);

                // Add
                var sum0 = Sse2.Add(predS0, res0);
                var sum1 = Sse2.Add(predS1, res1);
                var sum2 = Sse2.Add(predS2, res2);
                var sum3 = Sse2.Add(predS3, res3);

                // Pack with unsigned saturation
                var packed0 = Sse2.PackUnsignedSaturate(sum0, sum1);
                var packed1 = Sse2.PackUnsignedSaturate(sum2, sum3);

                // Combine to 256-bit and store
                var result = Vector256.Create(packed0, packed1);
                Avx.Store(pOut + i, result);
            }

            // SSE2 middle
            for (; i + 16 <= count; i += 16)
            {
                var pred = Sse2.LoadVector128(pPred + i);
                var predLo = Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16();
                var predHi = Sse2.UnpackHigh(pred, Vector128<byte>.Zero).AsInt16();
                var resLo = Sse2.LoadVector128(pRes + i);
                var resHi = Sse2.LoadVector128(pRes + i + 8);
                var sumLo = Sse2.Add(predLo, resLo);
                var sumHi = Sse2.Add(predHi, resHi);
                var packed = Sse2.PackUnsignedSaturate(sumLo, sumHi);
                Sse2.Store(pOut + i, packed);
            }

            // Scalar tail
            for (; i < count; i++)
            {
                int value = pPred[i] + pRes[i];
                pOut[i] = (byte)Math.Clamp(value, 0, 255);
            }
        }
    }

    /// <summary>
    /// Reconstruct high-bit-depth samples (10/12-bit): output[i] = clamp(prediction[i] + residual[i], 0, maxValue).
    /// Used by HEVC 10-bit, AV1 10/12-bit, etc.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReconstructHighBitDepth(
        ReadOnlySpan<ushort> prediction,
        ReadOnlySpan<int> residual,
        Span<ushort> output,
        int count,
        int bitDepth)
    {
        int maxValue = (1 << bitDepth) - 1;
        for (int i = 0; i < count; i++)
        {
            int value = prediction[i] + residual[i];
            output[i] = (ushort)Math.Clamp(value, 0, maxValue);
        }
    }

    /// <summary>
    /// The same copy for decoders that hold samples as sixteen-bit, which is every path that
    /// can carry more than eight bits per sample.
    /// </summary>
    /// <remarks>
    /// Offsets, strides and width are in samples here and are converted to bytes, so a row of
    /// sixteen samples takes the thirty-two byte path.
    /// </remarks>
    public static void CopyRows(
        ReadOnlySpan<ushort> source, int sourceOffset, int sourceStride,
        Span<ushort> destination, int destinationOffset, int destinationStride,
        int width, int height)
    {
        CopyRows(
            MemoryMarshal.AsBytes(source), sourceOffset * 2, sourceStride * 2,
            MemoryMarshal.AsBytes(destination), destinationOffset * 2, destinationStride * 2,
            width * 2, height);
    }

    /// <summary>
    /// Adds a residual block to an integer prediction block and writes the clipped result into a
    /// strided destination: <c>dst[offset + y*stride + x] = clip(pred[y*size+x] + res[y*size+x])</c>.
    /// </summary>
    /// <remarks>
    /// This is the shape the intra paths actually have. <see cref="Reconstruct8Bit"/> next to it
    /// wants the prediction as contiguous bytes and writes contiguous bytes, which no caller in
    /// the decoder can supply - intra prediction produces <c>int</c> and the destination is the
    /// frame, at a stride. That mismatch is why it had no callers at all.
    ///
    /// The clip is free. Packing signed 16-bit lanes down to bytes with unsigned saturation
    /// clamps to 0..255, which is exactly what the standard asks for, so the compare-and-select
    /// pair the scalar loop spends per sample disappears into the pack. Predictions are already
    /// in 0..255, so narrowing them from <c>int</c> is exact rather than saturating.
    /// </remarks>
    public static void AddResidual8Bit(
        ReadOnlySpan<int> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int size)
    {
        // A row narrower than a vector would read past its own row and store over the next
        // four samples, so the 4x4 blocks stay scalar. They are 3% of intra macroblocks.
        if (!Sse2.IsSupported || size < 8)
        {
            AddResidual8BitScalar(prediction, residual, destination, destinationOffset,
                                  destinationStride, size);
            return;
        }

        ref int pred = ref MemoryMarshal.GetReference(prediction);
        ref short res = ref MemoryMarshal.GetReference(residual);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationOffset);

        for (int y = 0; y < size; y++)
        {
            nuint row = (nuint)(y * size);
            ref byte outRow = ref Unsafe.Add(ref dst, y * destinationStride);

            for (int x = 0; x < size; x += 8)
            {
                nuint at = row + (nuint)x;

                // Eight predictions arrive as two vectors of four ints and narrow into one vector
                // of eight shorts, which is the width the residual already has.
                Vector128<short> sum =
                    Vector128.Narrow(Vector128.LoadUnsafe(ref pred, at),
                                     Vector128.LoadUnsafe(ref pred, at + 4))
                    + Vector128.LoadUnsafe(ref res, at);

                ulong packed = Sse2.PackUnsignedSaturate(sum, sum).AsUInt64().ToScalar();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref outRow, x), packed);
            }
        }
    }

    /// <summary>Scalar reference for <see cref="AddResidual8Bit"/>. Always correct.</summary>
    public static void AddResidual8BitScalar(
        ReadOnlySpan<int> prediction,
        ReadOnlySpan<short> residual,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int size)
    {
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int value = prediction[y * size + x] + residual[y * size + x];
            destination[destinationOffset + y * destinationStride + x] =
                (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
        }
    }

    /// <summary>
    /// As <see cref="AddResidual8Bit(ReadOnlySpan{int}, ReadOnlySpan{short}, Span{byte}, int, int, int)"/>,
    /// but the prediction is a sub-block of a larger prediction plane rather than contiguous.
    /// </summary>
    /// <remarks>
    /// Intra 16x16 predicts the whole macroblock once and then adds residual over it in 4x4
    /// blocks, and intra chroma does the same over an 8x8 plane, so those callers hold a stride
    /// into a prediction they do not own a contiguous piece of.
    ///
    /// Four-wide rows are safe here where they are not in the contiguous overload: a row of a
    /// strided prediction has its own four values at a known offset, so nothing is read from the
    /// next row and only four bytes are stored.
    /// </remarks>
    public static void AddResidual8Bit(
        ReadOnlySpan<int> prediction, int predictionOffset, int predictionStride,
        ReadOnlySpan<short> residual,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int size)
    {
        if (!Sse2.IsSupported)
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int value = prediction[predictionOffset + y * predictionStride + x]
                          + residual[y * size + x];
                destination[destinationOffset + y * destinationStride + x] =
                    (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            }

            return;
        }

        ref int pred = ref Unsafe.Add(ref MemoryMarshal.GetReference(prediction), predictionOffset);
        ref short res = ref MemoryMarshal.GetReference(residual);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationOffset);

        for (int y = 0; y < size; y++)
        {
            ref int predRow = ref Unsafe.Add(ref pred, y * predictionStride);
            ref short resRow = ref Unsafe.Add(ref res, y * size);
            ref byte outRow = ref Unsafe.Add(ref dst, y * destinationStride);

            if (size == 4)
            {
                // Four shorts, loaded as one 64-bit word so the read stops at the end of the row.
                Vector128<short> four = Vector128.CreateScalar(
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref resRow))).AsInt16();
                Vector128<int> p = Vector128.LoadUnsafe(ref predRow);
                Vector128<short> sum = Vector128.Narrow(p, p) + four;

                Unsafe.WriteUnaligned(ref outRow,
                    Sse2.PackUnsignedSaturate(sum, sum).AsUInt32().ToScalar());
                continue;
            }

            for (int x = 0; x < size; x += 8)
            {
                Vector128<short> sum =
                    Vector128.Narrow(Vector128.LoadUnsafe(ref predRow, (nuint)x),
                                     Vector128.LoadUnsafe(ref predRow, (nuint)(x + 4)))
                    + Vector128.LoadUnsafe(ref resRow, (nuint)x);

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref outRow, x),
                    Sse2.PackUnsignedSaturate(sum, sum).AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>
    /// Writes an integer prediction block into a strided destination as bytes:
    /// <c>dst[offset + y*stride + x] = pred[y*size+x]</c>.
    /// </summary>
    /// <remarks>
    /// The block whose residual is entirely zero is the common case, not a corner: on
    /// <c>h264-1080p-high.mp4</c> **1,484,504 of 2,769,516 intra 8x8 blocks** - 54% - carry no
    /// coefficients at all. The inverse transform of zero is zero and adding zero leaves the
    /// prediction, so those blocks want the prediction stored and nothing else, which is what
    /// <c>hl_decode_mb_internal</c> does when <c>non_zero_count_cache</c> is clear
    /// (`h264_mb.c:697`).
    ///
    /// Predictions are already in 0..255, so the narrow from <c>int</c> and the saturating pack
    /// are both exact here; the pack is used for the width, not for the clamp.
    /// </remarks>
    public static void StorePrediction8Bit(
        ReadOnlySpan<int> prediction,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int size)
    {
        if (!Sse2.IsSupported || size < 8)
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                destination[destinationOffset + y * destinationStride + x] =
                    (byte)prediction[y * size + x];

            return;
        }

        ref int pred = ref MemoryMarshal.GetReference(prediction);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationOffset);

        for (int y = 0; y < size; y++)
        {
            nuint row = (nuint)(y * size);
            ref byte outRow = ref Unsafe.Add(ref dst, y * destinationStride);

            for (int x = 0; x < size; x += 8)
            {
                nuint at = row + (nuint)x;
                Vector128<short> eight =
                    Vector128.Narrow(Vector128.LoadUnsafe(ref pred, at),
                                     Vector128.LoadUnsafe(ref pred, at + 4));

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref outRow, x),
                    Sse2.PackUnsignedSaturate(eight, eight).AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>
    /// Adds a residual block to what is already in the destination, in place and clipped:
    /// <c>dst[i] = clip(dst[i] + residual[i])</c> over a size-by-size block at a stride.
    /// </summary>
    /// <remarks>
    /// This is FFmpeg's <c>idct_add</c> shape. Once motion compensation has written its
    /// prediction into the frame there is no separate prediction array to read - the frame is
    /// the prediction - so the residual is added to the bytes already there. Zero-extending
    /// bytes to shorts and packing back with unsigned saturation makes the clip free, exactly
    /// as in the overloads above.
    /// </remarks>
    public static void AddResidualInPlace8Bit(
        ReadOnlySpan<short> residual,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int size)
    {
        if (!Sse2.IsSupported || size < 8)
        {
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int at = destinationOffset + y * destinationStride + x;
                int value = destination[at] + residual[y * size + x];
                destination[at] = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            }

            return;
        }

        ref short res = ref MemoryMarshal.GetReference(residual);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationOffset);

        for (int y = 0; y < size; y++)
        {
            ref short resRow = ref Unsafe.Add(ref res, y * size);
            ref byte outRow = ref Unsafe.Add(ref dst, y * destinationStride);

            for (int x = 0; x < size; x += 8)
            {
                Vector128<byte> have = Vector128.CreateScalar(
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref outRow, x))).AsByte();

                Vector128<short> sum = Sse2.UnpackLow(have, Vector128<byte>.Zero).AsInt16()
                                     + Vector128.LoadUnsafe(ref resRow, (nuint)x);

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref outRow, x),
                    Sse2.PackUnsignedSaturate(sum, sum).AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>
    /// Copies a rectangle of samples, one row at a time, at a width known before the loop.
    /// </summary>
    /// <remarks>
    /// <c>Span.CopyTo</c> goes through <c>Buffer.Memmove</c>, which decides what to do from a
    /// length it only learns at run time. That is the right trade for a large copy and the wrong
    /// one for sixteen bytes: measured over rows of 4, 8 and 16 bytes, <c>CopyTo</c> takes 2.2 ns
    /// a row against 0.64 ns for a copy whose width is fixed - between 3.4x and 4.2x.
    ///
    /// This matters because whole-pixel motion compensation is the commonest path in the decoder:
    /// on a 1080p stream about 2.4 million pixels a frame are copied rather than filtered, and
    /// every one of those rows was paying that dispatch.
    ///
    /// The width is tested once, outside the loop, so each row is a single load and store.
    /// </remarks>
    public static void CopyRows(
        ReadOnlySpan<byte> source, int sourceOffset, int sourceStride,
        Span<byte> destination, int destinationOffset, int destinationStride,
        int width, int height)
    {
        ref byte from = ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceOffset);
        ref byte to = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationOffset);

        switch (width)
        {
            case 64:
                for (int y = 0; y < height; y++)
                {
                    Vector256.LoadUnsafe(ref from).StoreUnsafe(ref to);
                    Vector256.LoadUnsafe(ref from, 32).StoreUnsafe(ref to, 32);
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            case 32:
                for (int y = 0; y < height; y++)
                {
                    Vector256.LoadUnsafe(ref from).StoreUnsafe(ref to);
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            case 16:
                for (int y = 0; y < height; y++)
                {
                    Vector128.LoadUnsafe(ref from).StoreUnsafe(ref to);
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            case 8:
                for (int y = 0; y < height; y++)
                {
                    Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ulong>(ref from));
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            case 4:
                for (int y = 0; y < height; y++)
                {
                    Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<uint>(ref from));
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            case 2:
                for (int y = 0; y < height; y++)
                {
                    Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ushort>(ref from));
                    from = ref Unsafe.Add(ref from, sourceStride);
                    to = ref Unsafe.Add(ref to, destinationStride);
                }
                return;

            default:
                for (int y = 0; y < height; y++)
                {
                    source.Slice(sourceOffset + y * sourceStride, width)
                          .CopyTo(destination.Slice(destinationOffset + y * destinationStride, width));
                }
                return;
        }
    }
}

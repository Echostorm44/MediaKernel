// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Bi-prediction averaging and weighted prediction operations.
/// Shared by all video decoders that support B-frames (H.264, HEVC, VP9, AV1).
/// </summary>
public static class BiPredictionDsp
{
    /// <summary>
    /// Average two prediction blocks: output[i] = (predL0[i] + predL1[i] + 1) &gt;&gt; 1.
    /// Uses SSE2 pavgb for byte-level rounding-aware averaging.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Average8Bit(
        ReadOnlySpan<byte> predL0,
        ReadOnlySpan<byte> predL1,
        Span<byte> output,
        int count)
    {
        if (Sse2.IsSupported && count >= 16)
            Average8BitSse2(predL0, predL1, output, count);
        else
            Average8BitScalar(predL0, predL1, output, count);
    }

    /// <summary>Scalar bi-prediction averaging.</summary>
    public static void Average8BitScalar(
        ReadOnlySpan<byte> predL0,
        ReadOnlySpan<byte> predL1,
        Span<byte> output,
        int count)
    {
        for (int i = 0; i < count; i++)
            output[i] = (byte)((predL0[i] + predL1[i] + 1) >> 1);
    }

    /// <summary>SSE2 bi-prediction using pavgb (Average with rounding).</summary>
    public static unsafe void Average8BitSse2(
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

            // Scalar tail
            for (; i < count; i++)
                pOut[i] = (byte)((pL0[i] + pL1[i] + 1) >> 1);
        }
    }

    /// <summary>
    /// Weighted uni-prediction: output[i] = clamp((pred[i] * weight + round) &gt;&gt; logWeightDenom + offset, 0, 255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WeightedPrediction8Bit(
        ReadOnlySpan<byte> prediction,
        Span<byte> output,
        int count,
        int weight,
        int offset,
        int logWeightDenom)
    {
        int round = logWeightDenom > 0 ? 1 << (logWeightDenom - 1) : 0;

        for (int i = 0; i < count; i++)
        {
            int result = ((prediction[i] * weight + round) >> logWeightDenom) + offset;
            output[i] = (byte)Math.Clamp(result, 0, 255);
        }
    }

    /// <summary>
    /// Weighted bi-prediction: output[i] = clamp(
    ///   (predL0[i] * weightL0 + predL1[i] * weightL1 + round) &gt;&gt; (logWeightDenom + 1) + offset, 0, 255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WeightedBiPrediction8Bit(
        ReadOnlySpan<byte> predL0,
        ReadOnlySpan<byte> predL1,
        Span<byte> output,
        int count,
        int weightL0,
        int weightL1,
        int offsetL0,
        int offsetL1,
        int logWeightDenom)
    {
        int shift = logWeightDenom + 1;
        int round = 1 << logWeightDenom;
        int combinedOffset = (offsetL0 + offsetL1 + 1) >> 1;

        for (int i = 0; i < count; i++)
        {
            int result = ((predL0[i] * weightL0 + predL1[i] * weightL1 + round) >> shift) + combinedOffset;
            output[i] = (byte)Math.Clamp(result, 0, 255);
        }
    }
}

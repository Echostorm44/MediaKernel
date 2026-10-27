using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Vectorised HEVC luma interpolation, written without pointers or unsafe loads.
/// </summary>
/// <remarks>
/// <para>
/// Every load and store here is bounds-checked - <see cref="Vector128.Create(ReadOnlySpan{ushort})"/>
/// and <see cref="Vector256.CopyTo(Span{int})"/> rather than LoadUnsafe against a raw ref. That is a
/// deliberate choice and it was measured rather than assumed: against the scalar code this replaces,
/// the safe form runs 2.2x faster at width 16, 2.2x at 32 and 2.9x at 64. An equivalent written with
/// unchecked loads is a further 1.05x to 1.28x on top, so the checks cost a slice of the improvement
/// and not the improvement itself. Keeping the decoder verifiably memory-safe is worth that.
/// </para>
/// <para>
/// Below eight samples the vector path cannot run a single iteration and the caller's scalar loop is
/// better than any tail loop here: a naive bounds-checked fallback measured fourteen times slower
/// than the tuned scalar code at width 4, which HEVC does use. <see cref="CanFilterHorizontal"/>
/// says whether the vector path is worth taking, and the caller keeps its own scalar path for when
/// it is not.
/// </para>
/// </remarks>
internal static class HevcInterpolationDsp
{
    /// <summary>Whether the vectorised horizontal filter will beat the caller's scalar loop.</summary>
    public static bool CanFilterHorizontal(int width) => Avx2.IsSupported && width >= 8;

    /// <summary>
    /// Applies the eight-tap luma filter across <paramref name="width"/> columns, shifting each sum
    /// right by <paramref name="shift"/>.
    /// </summary>
    /// <param name="source">
    /// At least <paramref name="width"/> + 7 samples: output column <c>c</c> reads <c>c</c> through
    /// <c>c + 7</c>. The caller's interior check guarantees that.
    /// </param>
    /// <param name="destination">At least <paramref name="width"/> entries.</param>
    /// <param name="taps">The eight filter taps.</param>
    public static void HorizontalLuma8Tap(
        ReadOnlySpan<ushort> source, Span<int> destination, ReadOnlySpan<short> taps, int shift, int width)
    {
        // Eight outputs per iteration: VPMOVZXWD widens eight 16-bit samples to eight 32-bit lanes,
        // and the tap window slides one sample per output, so the eight taps are eight overlapping
        // loads of the same row.
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 8; k++)
            {
                Vector128<ushort> samples = Vector128.Create(source.Slice(col + k, 8));
                sum += Avx2.ConvertToVector256Int32(samples) * taps[k];
            }

            (sum >> shift).CopyTo(destination.Slice(col, 8));
        }

        // Remainder, unrolled with the taps in locals so it does not become the naive loop that
        // measured far slower than the caller's scalar path.
        if (col < width)
            HorizontalLuma8TapScalar(source, destination, taps, shift, col, width);
    }

    /// <summary>
    /// Applies the eight-tap luma filter down columns, leaving the sums unclipped for bi-prediction.
    /// </summary>
    /// <param name="window">Eight rows of horizontally filtered samples, row stride <paramref name="width"/>.</param>
    /// <remarks>
    /// A better vector shape than the horizontal filter: the taps read rows apart while adjacent
    /// columns sit next to each other and are independent, so there is no widening and no shuffling -
    /// eight whole columns per iteration, loaded and stored as they lie in memory.
    /// </remarks>
    public static void VerticalLuma8TapToIntermediate(
        ReadOnlySpan<int> window, Span<int> destination, ReadOnlySpan<short> taps, int width)
    {
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 8; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            (sum >> 6).CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            VerticalLuma8TapToIntermediateScalar(window, destination, taps, width, col);
    }

    /// <summary>The definition for <see cref="VerticalLuma8TapToIntermediate"/>.</summary>
    public static void VerticalLuma8TapToIntermediateScalar(
        ReadOnlySpan<int> window, Span<int> destination, ReadOnlySpan<short> taps, int width, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 8; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = sum >> 6;
        }
    }

    /// <summary>
    /// Applies the eight-tap luma filter down columns, then normalises and clips to pixel range.
    /// </summary>
    public static void VerticalLuma8TapToPixels(
        ReadOnlySpan<int> window, Span<ushort> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int maxValue)
    {
        int col = 0;
        var offsets = Vector256.Create(offset);
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 8; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            Vector256<int> scaled = ((sum >> 6) + offsets) >> shift;
            Vector256<int> clipped = Vector256.Min(Vector256.Max(scaled, lowest), highest);

            // Narrow to 16 bits by hand: the lanes are already inside [0, maxValue], so this cannot
            // saturate, but Narrow keeps it to one instruction rather than eight stores.
            Vector128<ushort> narrowed = Vector128.Narrow(
                clipped.GetLower().AsUInt32(), clipped.GetUpper().AsUInt32());
            narrowed.CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            VerticalLuma8TapToPixelsScalar(window, destination, taps, width, offset, shift, maxValue, col);
    }

    /// <summary>The definition for <see cref="VerticalLuma8TapToPixels"/>.</summary>
    public static void VerticalLuma8TapToPixelsScalar(
        ReadOnlySpan<int> window, Span<ushort> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int maxValue, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 8; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = (ushort)Math.Clamp(((sum >> 6) + offset) >> shift, 0, maxValue);
        }
    }

    /// <summary>
    /// Averages two intermediate-precision predictions into pixels: <c>(a + b + offset) &gt;&gt; shift</c>,
    /// clipped to <paramref name="maxValue"/>.
    /// </summary>
    /// <remarks>
    /// The simplest shape of the three - two contiguous reads, one contiguous write, no taps and no
    /// window. The caller passes one row at a time, having already worked out how much of the row
    /// lies inside the picture, so there is no edge test in here.
    /// </remarks>
    public static void AverageBiPrediction(
        ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<ushort> destination,
        int count, int offset, int shift, int maxValue)
    {
        int col = 0;
        if (Avx2.IsSupported)
        {
            var offsets = Vector256.Create(offset);
            var lowest = Vector256<int>.Zero;
            var highest = Vector256.Create(maxValue);

            for (; col + 8 <= count; col += 8)
            {
                Vector256<int> sum = Vector256.Create(first.Slice(col, 8))
                                   + Vector256.Create(second.Slice(col, 8))
                                   + offsets;
                Vector256<int> clipped = Vector256.Min(Vector256.Max(sum >> shift, lowest), highest);
                Vector128.Narrow(clipped.GetLower().AsUInt32(), clipped.GetUpper().AsUInt32())
                    .CopyTo(destination.Slice(col, 8));
            }
        }

        AverageBiPredictionScalar(first, second, destination, count, offset, shift, maxValue, col);
    }

    /// <summary>
    /// Averages two intermediate-precision predictions into eight-bit pixels.
    /// </summary>
    /// <remarks>
    /// The same arithmetic as <see cref="AverageBiPrediction"/> with one more narrowing step,
    /// since the destination is bytes. Eight lanes at a time rather than sixteen: chroma planes
    /// are half width, so blocks of eight are common and blocks of sixteen are not.
    /// </remarks>
    public static void AverageBiPredictionToBytes(
        ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<byte> destination,
        int count, int offset, int shift)
    {
        int col = 0;
        if (Avx2.IsSupported)
        {
            var offsets = Vector256.Create(offset);
            var lowest = Vector256<int>.Zero;
            var highest = Vector256.Create(255);

            for (; col + 8 <= count; col += 8)
            {
                Vector256<int> sum = Vector256.Create(first.Slice(col, 8))
                                   + Vector256.Create(second.Slice(col, 8))
                                   + offsets;
                Vector256<int> clipped = Vector256.Min(Vector256.Max(sum >> shift, lowest), highest);

                Vector128<ushort> halves = Vector128.Narrow(
                    clipped.GetLower().AsUInt32(), clipped.GetUpper().AsUInt32());

                // Eight bytes as one 64-bit store. Narrow needs sixteen lanes to fill a
                // Vector128<byte>, so the upper half is a duplicate and only the lower half kept.
                BinaryPrimitives.WriteUInt64LittleEndian(
                    destination.Slice(col, 8),
                    Vector128.Narrow(halves, halves).AsUInt64().ToScalar());
            }
        }

        AverageBiPredictionToBytesScalar(first, second, destination, count, offset, shift, col);
    }

    /// <summary>The definition for <see cref="AverageBiPredictionToBytes"/>.</summary>
    public static void AverageBiPredictionToBytesScalar(
        ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<byte> destination,
        int count, int offset, int shift, int from)
    {
        for (int col = from; col < count; col++)
            destination[col] = (byte)Math.Clamp((first[col] + second[col] + offset) >> shift, 0, 255);
    }

    /// <summary>The definition for <see cref="AverageBiPrediction"/>.</summary>
    public static void AverageBiPredictionScalar(
        ReadOnlySpan<int> first, ReadOnlySpan<int> second, Span<ushort> destination,
        int count, int offset, int shift, int maxValue, int from)
    {
        for (int col = from; col < count; col++)
            destination[col] = (ushort)Math.Clamp((first[col] + second[col] + offset) >> shift, 0, maxValue);
    }

    // ── Eight-bit source and destination ────────────────────────────────────────────────
    //
    // The same filters over a byte plane. They exist because HEVC Main - eight bit - is a
    // different set of methods from Main 10 all the way down, and only the ten-bit ones had ever
    // been vectorised: a 1080p eight-bit stream decoded at 16.7 fps where a 1080p ten-bit one
    // managed 54, on the cheaper format.
    //
    // The only difference from the ushort versions is the widening instruction - VPMOVZXBD takes
    // eight bytes to eight ints where VPMOVZXWD takes eight shorts - and the store, which packs
    // back down to bytes.

    /// <summary>
    /// Applies the eight-tap luma filter across <paramref name="width"/> columns of an eight-bit
    /// plane. No shift: for eight-bit content the horizontal pass keeps full precision.
    /// </summary>
    /// <param name="source">
    /// At least <paramref name="width"/> + 7 bytes: output column <c>c</c> reads <c>c</c> through
    /// <c>c + 7</c>. The caller's interior check guarantees that.
    /// </param>
    public static void HorizontalLuma8TapBytes(
        ReadOnlySpan<byte> source, Span<int> destination, ReadOnlySpan<short> taps, int width)
    {
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 8; k++)
                sum += Widen8(source.Slice(col + k, 8)) * taps[k];

            sum.CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            HorizontalLuma8TapBytesScalar(source, destination, taps, col, width);
    }

    /// <summary>The definition for <see cref="HorizontalLuma8TapBytes"/>.</summary>
    public static void HorizontalLuma8TapBytesScalar(
        ReadOnlySpan<byte> source, Span<int> destination, ReadOnlySpan<short> taps, int from, int width)
    {
        int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
        int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];

        for (int col = from; col < width; col++)
        {
            ReadOnlySpan<byte> at = source.Slice(col, 8);
            destination[col] = t0 * at[0] + t1 * at[1] + t2 * at[2] + t3 * at[3]
                             + t4 * at[4] + t5 * at[5] + t6 * at[6] + t7 * at[7];
        }
    }

    /// <summary>
    /// Applies the eight-tap luma filter down columns, then normalises and clips to eight bits.
    /// </summary>
    public static void VerticalLuma8TapToBytes(
        ReadOnlySpan<int> window, Span<byte> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift)
    {
        int col = 0;
        var offsets = Vector256.Create(offset);
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(255);

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 8; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            Vector256<int> scaled = ((sum >> 6) + offsets) >> shift;
            StoreEightBytes(Vector256.Min(Vector256.Max(scaled, lowest), highest),
                            destination.Slice(col, 8));
        }

        if (col < width)
            VerticalLuma8TapToBytesScalar(window, destination, taps, width, offset, shift, col);
    }

    /// <summary>The definition for <see cref="VerticalLuma8TapToBytes"/>.</summary>
    public static void VerticalLuma8TapToBytesScalar(
        ReadOnlySpan<int> window, Span<byte> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 8; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = (byte)Math.Clamp(((sum >> 6) + offset) >> shift, 0, 255);
        }
    }

    /// <summary>
    /// Applies the four-tap chroma filter across <paramref name="width"/> columns of an eight-bit
    /// plane.
    /// </summary>
    public static void HorizontalChroma4TapBytes(
        ReadOnlySpan<byte> source, Span<int> destination, ReadOnlySpan<short> taps, int width)
    {
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 4; k++)
                sum += Widen8(source.Slice(col + k, 8)) * taps[k];

            sum.CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            HorizontalChroma4TapBytesScalar(source, destination, taps, col, width);
    }

    /// <summary>The definition for <see cref="HorizontalChroma4TapBytes"/>.</summary>
    public static void HorizontalChroma4TapBytesScalar(
        ReadOnlySpan<byte> source, Span<int> destination, ReadOnlySpan<short> taps, int from, int width)
    {
        int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];

        for (int col = from; col < width; col++)
        {
            ReadOnlySpan<byte> at = source.Slice(col, 4);
            destination[col] = t0 * at[0] + t1 * at[1] + t2 * at[2] + t3 * at[3];
        }
    }

    /// <summary>
    /// Applies the four-tap chroma filter down columns, then normalises and clips to eight bits.
    /// </summary>
    public static void VerticalChroma4TapToBytes(
        ReadOnlySpan<int> window, Span<byte> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift)
    {
        int col = 0;
        var offsets = Vector256.Create(offset);
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(255);

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 4; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            Vector256<int> scaled = ((sum >> 6) + offsets) >> shift;
            StoreEightBytes(Vector256.Min(Vector256.Max(scaled, lowest), highest),
                            destination.Slice(col, 8));
        }

        if (col < width)
            VerticalChroma4TapToBytesScalar(window, destination, taps, width, offset, shift, col);
    }

    /// <summary>The definition for <see cref="VerticalChroma4TapToBytes"/>.</summary>
    public static void VerticalChroma4TapToBytesScalar(
        ReadOnlySpan<int> window, Span<byte> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 4; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = (byte)Math.Clamp(((sum >> 6) + offset) >> shift, 0, 255);
        }
    }

    /// <summary>
    /// Eight bytes widened to eight 32-bit lanes (VPMOVZXBD). Read as one 64-bit load so the
    /// span only has to hold the eight bytes that are actually used - a Vector128&lt;byte&gt;
    /// load would demand sixteen and read past the end of the last row of a block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Widen8(ReadOnlySpan<byte> source)
    {
        ulong eight = BinaryPrimitives.ReadUInt64LittleEndian(source);
        return Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(eight).AsByte());
    }

    /// <summary>
    /// Eight lanes, already clipped to 0-255, stored as eight bytes in one 64-bit write.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreEightBytes(Vector256<int> clipped, Span<byte> destination)
    {
        Vector128<ushort> halves = Vector128.Narrow(
            clipped.GetLower().AsUInt32(), clipped.GetUpper().AsUInt32());

        BinaryPrimitives.WriteUInt64LittleEndian(
            destination, Vector128.Narrow(halves, halves).AsUInt64().ToScalar());
    }

    /// <summary>
    /// Applies the four-tap chroma filter across <paramref name="width"/> columns.
    /// </summary>
    /// <param name="source">
    /// At least <paramref name="width"/> + 3 samples, starting one column left of the first output:
    /// output <c>c</c> reads <c>c</c> through <c>c + 3</c>.
    /// </param>
    public static void HorizontalChroma4Tap(
        ReadOnlySpan<ushort> source, Span<int> destination, ReadOnlySpan<short> taps, int shift, int width)
    {
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 4; k++)
            {
                Vector128<ushort> samples = Vector128.Create(source.Slice(col + k, 8));
                sum += Avx2.ConvertToVector256Int32(samples) * taps[k];
            }

            (sum >> shift).CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            HorizontalChroma4TapScalar(source, destination, taps, shift, col, width);
    }

    /// <summary>The definition for <see cref="HorizontalChroma4Tap"/>.</summary>
    public static void HorizontalChroma4TapScalar(
        ReadOnlySpan<ushort> source, Span<int> destination, ReadOnlySpan<short> taps,
        int shift, int from, int width)
    {
        int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];

        for (int col = from; col < width; col++)
        {
            var window = source.Slice(col, 4);
            destination[col] = (t0 * window[0] + t1 * window[1]
                              + t2 * window[2] + t3 * window[3]) >> shift;
        }
    }

    /// <summary>
    /// Applies the four-tap chroma filter down columns, leaving the sums unclipped for bi-prediction.
    /// </summary>
    /// <param name="window">Four rows of horizontally filtered samples, row stride <paramref name="width"/>.</param>
    public static void VerticalChroma4TapToIntermediate(
        ReadOnlySpan<int> window, Span<int> destination, ReadOnlySpan<short> taps, int width)
    {
        int col = 0;
        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 4; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            (sum >> 6).CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            VerticalChroma4TapToIntermediateScalar(window, destination, taps, width, col);
    }

    /// <summary>The definition for <see cref="VerticalChroma4TapToIntermediate"/>.</summary>
    public static void VerticalChroma4TapToIntermediateScalar(
        ReadOnlySpan<int> window, Span<int> destination, ReadOnlySpan<short> taps, int width, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 4; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = sum >> 6;
        }
    }

    /// <summary>
    /// Applies the four-tap chroma filter down columns, then normalises and clips to pixel range.
    /// </summary>
    public static void VerticalChroma4TapToPixels(
        ReadOnlySpan<int> window, Span<ushort> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int maxValue)
    {
        int col = 0;
        var offsets = Vector256.Create(offset);
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (int k = 0; k < 4; k++)
                sum += Vector256.Create(window.Slice(col + k * width, 8)) * taps[k];

            Vector256<int> scaled = ((sum >> 6) + offsets) >> shift;
            Vector256<int> clipped = Vector256.Min(Vector256.Max(scaled, lowest), highest);

            Vector128.Narrow(clipped.GetLower().AsUInt32(), clipped.GetUpper().AsUInt32())
                .CopyTo(destination.Slice(col, 8));
        }

        if (col < width)
            VerticalChroma4TapToPixelsScalar(window, destination, taps, width, offset, shift, maxValue, col);
    }

    /// <summary>The definition for <see cref="VerticalChroma4TapToPixels"/>.</summary>
    public static void VerticalChroma4TapToPixelsScalar(
        ReadOnlySpan<int> window, Span<ushort> destination, ReadOnlySpan<short> taps,
        int width, int offset, int shift, int maxValue, int from)
    {
        for (int col = from; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 4; k++)
                sum += taps[k] * window[col + k * width];

            destination[col] = (ushort)Math.Clamp(((sum >> 6) + offset) >> shift, 0, maxValue);
        }
    }

    /// <summary>The definition, and the tail the vector loop leaves behind.</summary>
    public static void HorizontalLuma8TapScalar(
        ReadOnlySpan<ushort> source, Span<int> destination, ReadOnlySpan<short> taps,
        int shift, int from, int width)
    {
        int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3];
        int t4 = taps[4], t5 = taps[5], t6 = taps[6], t7 = taps[7];

        for (int col = from; col < width; col++)
        {
            var window = source.Slice(col, 8);
            destination[col] = (t0 * window[0] + t1 * window[1] + t2 * window[2] + t3 * window[3]
                              + t4 * window[4] + t5 * window[5] + t6 * window[6] + t7 * window[7]) >> shift;
        }
    }
}

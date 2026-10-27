using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Vectorised HEVC sample adaptive offset, written without pointers or unsafe loads.
/// </summary>
/// <remarks>
/// <para>
/// SAO is a per-pixel table lookup and add, which sounds like the wrong shape for SIMD until the
/// tables are looked at closely. Neither of them needs a gather:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Edge offset indexes five categories by comparing a pixel with two neighbours. Five entries fit
/// inside one vector, so the lookup is a single cross-lane permute rather than eight loads.
/// </description></item>
/// <item><description>
/// Band offset indexes thirty-two bands, but only four of them are ever non-zero and the rest
/// contribute nothing. So it is four compares and four selects against the bands that matter, and
/// the thirty-two entry table never has to exist.
/// </description></item>
/// </list>
/// <para>
/// The scalar code skips the write when the offset is zero. The vector paths write unconditionally,
/// which is only correct because the destination starts as an exact copy of the source it reads
/// from - <c>buffer.CopyTo(preSaoCopy)</c> - so storing the unchanged pixel writes back what was
/// already there. That is what removes the branch.
/// </para>
/// </remarks>
internal static class HevcSaoDsp
{
    /// <summary>Whether the vector paths are available.</summary>
    public static bool IsSupported => Avx2.IsSupported;

    /// <summary>Eight samples at <paramref name="offset"/>, widened to 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8(ReadOnlySpan<ushort> samples, int offset)
        => Avx2.ConvertToVector256Int32(Vector128.Create(samples.Slice(offset, 8)));

    /// <summary>Narrows eight lanes back to samples at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(Span<ushort> samples, int offset, Vector256<int> value)
        => Vector128.Narrow(value.GetLower().AsUInt32(), value.GetUpper().AsUInt32())
                    .CopyTo(samples.Slice(offset, 8));

    /// <summary>
    /// The sign of <c>a - b</c> per lane, as -1, 0 or +1.
    /// </summary>
    /// <remarks>
    /// Comparisons yield all-ones, which is -1 read as an integer, so subtracting the two masks
    /// gives the sign directly: +1 when a &gt; b, -1 when a &lt; b, zero when equal. No branches and
    /// no multiply.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> SignOfDifference(Vector256<int> a, Vector256<int> b)
        => Vector256.LessThan(a, b) - Vector256.GreaterThan(a, b);

    // ------------------------------------------------------------------ edge offset

    /// <summary>
    /// Applies edge-offset SAO to one row, from <paramref name="startX"/> up to <paramref name="endX"/>.
    /// </summary>
    /// <param name="rowStart">Index of column zero of this row.</param>
    /// <param name="neighbour0">Index delta to the first neighbour, from the current pixel.</param>
    /// <param name="neighbour1">Index delta to the second neighbour.</param>
    /// <param name="categoryOffsets">The five category offsets in lanes 0 to 4.</param>
    public static void EdgeOffsetRow(
        ReadOnlySpan<ushort> readPlane, Span<ushort> plane, int rowStart,
        int neighbour0, int neighbour1, int startX, int endX,
        Vector256<int> categoryOffsets, int maxValue)
    {
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);
        var two = Vector256.Create(2);

        int x = startX;
        for (; x + 8 <= endX; x += 8)
        {
            int at = rowStart + x;
            Vector256<int> current = Load8(readPlane, at);

            Vector256<int> category = SignOfDifference(current, Load8(readPlane, at + neighbour0))
                                    + SignOfDifference(current, Load8(readPlane, at + neighbour1))
                                    + two;

            // Five entries live inside one vector, so the whole table lookup is one permute.
            Vector256<int> offset = Vector256.Shuffle(categoryOffsets, category);

            Store8(plane, at, Vector256.Min(Vector256.Max(current + offset, lowest), highest));
        }

        if (x < endX)
            EdgeOffsetRowScalar(readPlane, plane, rowStart, neighbour0, neighbour1, x, endX,
                categoryOffsets, maxValue);
    }

    /// <summary>The definition for <see cref="EdgeOffsetRow"/>.</summary>
    public static void EdgeOffsetRowScalar(
        ReadOnlySpan<ushort> readPlane, Span<ushort> plane, int rowStart,
        int neighbour0, int neighbour1, int startX, int endX,
        Vector256<int> categoryOffsets, int maxValue)
    {
        for (int x = startX; x < endX; x++)
        {
            int at = rowStart + x;
            int current = readPlane[at];

            int category = Math.Sign(current - readPlane[at + neighbour0])
                         + Math.Sign(current - readPlane[at + neighbour1])
                         + 2;

            int offset = categoryOffsets[category];
            if (offset != 0)
                plane[at] = (ushort)Math.Clamp(current + offset, 0, maxValue);
        }
    }

    // ------------------------------------------------------------------ eight-bit edge offset

    /// <summary>Eight samples at <paramref name="offset"/>, widened to 32-bit lanes.</summary>
    /// <remarks>VPMOVZXBD takes eight bytes to eight 32-bit lanes in one instruction, so the
    /// eight-bit path runs the same cores as the high bit-depth one with no extra step.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8(ReadOnlySpan<byte> samples, int offset)
        => Avx2.ConvertToVector256Int32(Vector64.Create(samples.Slice(offset, 8)).ToVector128());

    /// <summary>Narrows eight lanes back to bytes at <paramref name="offset"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(Span<byte> samples, int offset, Vector256<int> value)
    {
        Vector128<ushort> halves = Vector128.Narrow(
            value.GetLower().AsUInt32(), value.GetUpper().AsUInt32());

        // Narrow needs sixteen lanes to fill a vector, so the upper half is a duplicate and only
        // the lower eight bytes are kept.
        BinaryPrimitives.WriteUInt64LittleEndian(
            samples.Slice(offset, 8),
            Vector128.Narrow(halves, halves).AsUInt64().ToScalar());
    }

    /// <summary>Applies edge-offset SAO to one row of an eight-bit plane.</summary>
    public static void EdgeOffsetRow8(
        ReadOnlySpan<byte> readPlane, Span<byte> plane, int rowStart,
        int neighbour0, int neighbour1, int startX, int endX,
        Vector256<int> categoryOffsets, int maxValue)
    {
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);
        var two = Vector256.Create(2);

        int x = startX;
        for (; x + 8 <= endX; x += 8)
        {
            int at = rowStart + x;
            Vector256<int> current = Load8(readPlane, at);

            Vector256<int> category = SignOfDifference(current, Load8(readPlane, at + neighbour0))
                                    + SignOfDifference(current, Load8(readPlane, at + neighbour1))
                                    + two;

            Vector256<int> offset = Vector256.Shuffle(categoryOffsets, category);

            Store8(plane, at, Vector256.Min(Vector256.Max(current + offset, lowest), highest));
        }

        if (x < endX)
            EdgeOffsetRow8Scalar(readPlane, plane, rowStart, neighbour0, neighbour1, x, endX,
                categoryOffsets, maxValue);
    }

    /// <summary>The definition for <see cref="EdgeOffsetRow8"/>.</summary>
    public static void EdgeOffsetRow8Scalar(
        ReadOnlySpan<byte> readPlane, Span<byte> plane, int rowStart,
        int neighbour0, int neighbour1, int startX, int endX,
        Vector256<int> categoryOffsets, int maxValue)
    {
        for (int x = startX; x < endX; x++)
        {
            int at = rowStart + x;
            int current = readPlane[at];

            int category = Math.Sign(current - readPlane[at + neighbour0])
                         + Math.Sign(current - readPlane[at + neighbour1])
                         + 2;

            int offset = categoryOffsets[category];
            if (offset != 0)
                plane[at] = (byte)Math.Clamp(current + offset, 0, maxValue);
        }
    }

    /// <summary>Applies band-offset SAO to one row of an eight-bit plane.</summary>
    public static void BandOffsetRow8(
        ReadOnlySpan<byte> readPlane, Span<byte> plane, int rowStart, int width,
        int bandShift, ReadOnlySpan<int> bands, ReadOnlySpan<int> bandOffsets, int maxValue)
    {
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);

        var band0 = Vector256.Create(bands[0]);
        var band1 = Vector256.Create(bands[1]);
        var band2 = Vector256.Create(bands[2]);
        var band3 = Vector256.Create(bands[3]);
        var offset0 = Vector256.Create(bandOffsets[0]);
        var offset1 = Vector256.Create(bandOffsets[1]);
        var offset2 = Vector256.Create(bandOffsets[2]);
        var offset3 = Vector256.Create(bandOffsets[3]);

        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            int at = rowStart + x;
            Vector256<int> current = Load8(readPlane, at);
            Vector256<int> band = current >> bandShift;

            Vector256<int> offset = Vector256.ConditionalSelect(Vector256.Equals(band, band0), offset0, Vector256<int>.Zero);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band1), offset1, offset);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band2), offset2, offset);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band3), offset3, offset);

            Store8(plane, at, Vector256.Min(Vector256.Max(current + offset, lowest), highest));
        }

        if (x < width)
            BandOffsetRow8Scalar(readPlane, plane, rowStart, x, width, bandShift, bands, bandOffsets, maxValue);
    }

    /// <summary>The definition for <see cref="BandOffsetRow8"/>.</summary>
    public static void BandOffsetRow8Scalar(
        ReadOnlySpan<byte> readPlane, Span<byte> plane, int rowStart, int startX, int width,
        int bandShift, ReadOnlySpan<int> bands, ReadOnlySpan<int> bandOffsets, int maxValue)
    {
        for (int x = startX; x < width; x++)
        {
            int at = rowStart + x;
            int current = readPlane[at];
            int band = current >> bandShift;

            int offset = 0;
            for (int i = 0; i < 4; i++)
                if (band == bands[i]) offset = bandOffsets[i];

            if (offset != 0)
                plane[at] = (byte)Math.Clamp(current + offset, 0, maxValue);
        }
    }

    // ------------------------------------------------------------------ band offset

    /// <summary>
    /// Applies band-offset SAO to one row.
    /// </summary>
    /// <param name="bands">The four band indices that carry an offset.</param>
    /// <param name="bandOffsets">Their offsets, in the same order.</param>
    public static void BandOffsetRow(
        ReadOnlySpan<ushort> readPlane, Span<ushort> plane, int rowStart, int width,
        int bandShift, ReadOnlySpan<int> bands, ReadOnlySpan<int> bandOffsets, int maxValue)
    {
        var lowest = Vector256<int>.Zero;
        var highest = Vector256.Create(maxValue);

        // Only four of the thirty-two bands carry an offset, so the table never has to be built.
        var band0 = Vector256.Create(bands[0]);
        var band1 = Vector256.Create(bands[1]);
        var band2 = Vector256.Create(bands[2]);
        var band3 = Vector256.Create(bands[3]);
        var offset0 = Vector256.Create(bandOffsets[0]);
        var offset1 = Vector256.Create(bandOffsets[1]);
        var offset2 = Vector256.Create(bandOffsets[2]);
        var offset3 = Vector256.Create(bandOffsets[3]);

        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            int at = rowStart + x;
            Vector256<int> current = Load8(readPlane, at);
            Vector256<int> band = current >> bandShift;

            Vector256<int> offset = Vector256.ConditionalSelect(Vector256.Equals(band, band0), offset0, Vector256<int>.Zero);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band1), offset1, offset);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band2), offset2, offset);
            offset = Vector256.ConditionalSelect(Vector256.Equals(band, band3), offset3, offset);

            Store8(plane, at, Vector256.Min(Vector256.Max(current + offset, lowest), highest));
        }

        if (x < width)
            BandOffsetRowScalar(readPlane, plane, rowStart, x, width, bandShift, bands, bandOffsets, maxValue);
    }

    /// <summary>The definition for <see cref="BandOffsetRow"/>.</summary>
    public static void BandOffsetRowScalar(
        ReadOnlySpan<ushort> readPlane, Span<ushort> plane, int rowStart, int startX, int width,
        int bandShift, ReadOnlySpan<int> bands, ReadOnlySpan<int> bandOffsets, int maxValue)
    {
        for (int x = startX; x < width; x++)
        {
            int at = rowStart + x;
            int current = readPlane[at];
            int band = current >> bandShift;

            int offset = 0;
            for (int i = 0; i < 4; i++)
                if (band == bands[i]) offset = bandOffsets[i];

            if (offset != 0)
                plane[at] = (ushort)Math.Clamp(current + offset, 0, maxValue);
        }
    }
}

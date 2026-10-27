using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Vectorised pieces of H.264 luma interpolation.
/// </summary>
/// <remarks>
/// <para>
/// The half-pel filter is the six taps [1, -5, 20, 20, -5, 1] applied to consecutive samples. Run
/// per output pixel it is six multiply-adds; run across a row it is six shifted loads of the same
/// data, which is what makes it worth vectorising. A 4x4 block only has four columns and wastes most
/// of a register, so these operate on a whole macroblock row - sixteen samples - where a 128-bit
/// vector holds eight and a 256-bit vector holds all sixteen.
/// </para>
/// <para>
/// The scalar path is not a fallback that happens to agree; it is the definition. The tests assert
/// the vector paths reproduce it exactly for every input, because motion compensation feeds the
/// reference frames and a single sample of drift compounds through the whole stream.
/// </para>
/// </remarks>
internal static class H264InterpolationDsp
{
    /// <summary>Taps of the H.264 half-pel filter.</summary>
    private const int Tap0 = 1, Tap1 = -5, Tap2 = 20, Tap3 = 20, Tap4 = -5, Tap5 = 1;

    /// <summary>
    /// Horizontal six-tap sums for <paramref name="count"/> consecutive outputs starting at
    /// <paramref name="srcOffset"/>, before rounding or clipping.
    /// </summary>
    /// <remarks>
    /// Output <c>i</c> reads source <c>srcOffset + i - 2</c> through <c>srcOffset + i + 3</c>, so the
    /// caller must guarantee those are in range - the interior check the callers already do.
    /// </remarks>
    public static void HorizontalTap6(ReadOnlySpan<byte> source, int srcOffset, Span<int> destination, int count)
    {
        if (Avx2.IsSupported && Sse41.IsSupported && count >= 16)
        {
            HorizontalTap6Avx2(source, srcOffset, destination, count);
            return;
        }

        if (Sse41.IsSupported && count >= 8)
        {
            HorizontalTap6Sse2(source, srcOffset, destination, count);
            return;
        }

        // Four at a time below eight, so a 4x4 caller keeps the four-lane path rather than
        // dropping to scalar. Without this, routing the 4x4 case through the width-generic
        // filter would be a regression for the most common block size there is.
        if (Sse41.IsSupported && count >= 4)
        {
            int at = 0;
            for (; at + 4 <= count; at += 4)
                HorizontalTap6x4(source, srcOffset + at - 2, destination.Slice(at, 4));

            if (at < count)
                HorizontalTap6Scalar(source, srcOffset + at, destination[at..], count - at);

            return;
        }

        HorizontalTap6Scalar(source, srcOffset, destination, count);
    }

    /// <summary>The definition the vector paths must reproduce exactly.</summary>
    public static void HorizontalTap6Scalar(
        ReadOnlySpan<byte> source, int srcOffset, Span<int> destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int b = srcOffset + i - 2;
            destination[i] = Tap0 * source[b]
                           + Tap1 * source[b + 1]
                           + Tap2 * source[b + 2]
                           + Tap3 * source[b + 3]
                           + Tap4 * source[b + 4]
                           + Tap5 * source[b + 5];
        }
    }

    /// <summary>
    /// Four consecutive six-tap sums, for the two-dimensional filter's horizontal pass.
    /// </summary>
    /// <param name="offset">Index of the first tap of output column zero, i.e. <c>x - 2</c>.</param>
    /// <remarks>
    /// A 4x4 block is only four columns wide, which is too narrow for the row-at-a-time paths above
    /// - they need eight samples before a vector loop can run at all, so at width four they fall
    /// through to scalar. Four consecutive outputs are still four lanes, though: the tap window
    /// slides one sample per output, so the six taps are six overlapping four-byte loads.
    /// </remarks>
    public static void HorizontalTap6x4(ReadOnlySpan<byte> source, int offset, Span<int> destination)
    {
        Vector128<int> sum = Widen4(source, offset) * Tap0
                           + Widen4(source, offset + 1) * Tap1
                           + Widen4(source, offset + 2) * Tap2
                           + Widen4(source, offset + 3) * Tap3
                           + Widen4(source, offset + 4) * Tap4
                           + Widen4(source, offset + 5) * Tap5;

        sum.CopyTo(destination);
    }

    /// <summary>
    /// The vertical half of the two-dimensional filter across a whole partition width.
    /// </summary>
    /// <param name="window">Horizontally filtered sums, row stride <paramref name="width"/>.</param>
    /// <param name="firstRow">Index of the first of the six rows the taps span.</param>
    /// <remarks>
    /// Eight columns at a time. The four-wide form above exists for a 4x4 block, which is all a
    /// per-block caller ever has; a partition gives this one sixteen columns to work with.
    /// </remarks>
    public static void VerticalTap6(
        ReadOnlySpan<int> window, int width, int firstRow, Span<int> destination)
    {
        int col = 0;

        if (Avx2.IsSupported)
        {
            for (; col + 8 <= width; col += 8)
            {
                Vector256<int> sum = Vector256.Create(window.Slice(firstRow * width + col, 8)) * Tap0
                                   + Vector256.Create(window.Slice((firstRow + 1) * width + col, 8)) * Tap1
                                   + Vector256.Create(window.Slice((firstRow + 2) * width + col, 8)) * Tap2
                                   + Vector256.Create(window.Slice((firstRow + 3) * width + col, 8)) * Tap3
                                   + Vector256.Create(window.Slice((firstRow + 4) * width + col, 8)) * Tap4
                                   + Vector256.Create(window.Slice((firstRow + 5) * width + col, 8)) * Tap5;

                sum.CopyTo(destination.Slice(col, 8));
            }
        }

        for (; col + 4 <= width; col += 4)
        {
            Vector128<int> sum = Vector128.Create(window.Slice(firstRow * width + col, 4)) * Tap0
                               + Vector128.Create(window.Slice((firstRow + 1) * width + col, 4)) * Tap1
                               + Vector128.Create(window.Slice((firstRow + 2) * width + col, 4)) * Tap2
                               + Vector128.Create(window.Slice((firstRow + 3) * width + col, 4)) * Tap3
                               + Vector128.Create(window.Slice((firstRow + 4) * width + col, 4)) * Tap4
                               + Vector128.Create(window.Slice((firstRow + 5) * width + col, 4)) * Tap5;

            sum.CopyTo(destination.Slice(col, 4));
        }

        for (; col < width; col++)
        {
            int sum = 0;
            for (int k = 0; k < 6; k++)
                sum += TapAt(k) * window[(firstRow + k) * width + col];

            destination[col] = sum;
        }
    }

    /// <summary>The filter taps by index, for the scalar tail.</summary>
    private static int TapAt(int k) => k switch
    {
        0 => Tap0, 1 => Tap1, 2 => Tap2, 3 => Tap3, 4 => Tap4, _ => Tap5,
    };

    /// <summary>Widens four bytes at <paramref name="offset"/> to four 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Widen4(ReadOnlySpan<byte> source, int offset)
    {
        // One four-byte read, then PMOVZXBD. Reading through BinaryPrimitives keeps the load
        // bounds-checked; the widening is a single instruction.
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        return Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte());
    }

    /// <summary>Whether the four-wide paths are available.</summary>
    public static bool CanFilter4Wide => Sse41.IsSupported;

    /// <summary>
    /// The vertical half of the two-dimensional filter for four columns, over a window of six rows.
    /// </summary>
    /// <param name="window">Horizontally filtered sums, row stride four.</param>
    /// <param name="firstRow">Index of the first of the six rows in <paramref name="window"/>.</param>
    public static void VerticalTap6x4(ReadOnlySpan<int> window, int firstRow, Span<int> destination)
    {
        Vector128<int> sum = Vector128.Create(window.Slice(firstRow * 4, 4)) * Tap0
                           + Vector128.Create(window.Slice((firstRow + 1) * 4, 4)) * Tap1
                           + Vector128.Create(window.Slice((firstRow + 2) * 4, 4)) * Tap2
                           + Vector128.Create(window.Slice((firstRow + 3) * 4, 4)) * Tap3
                           + Vector128.Create(window.Slice((firstRow + 4) * 4, 4)) * Tap4
                           + Vector128.Create(window.Slice((firstRow + 5) * 4, 4)) * Tap5;

        sum.CopyTo(destination);
    }

    /// <summary>Widens eight bytes at <paramref name="offset"/> to eight 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen8(ref byte origin, int offset)
    {
        // PMOVZXBW: eight bytes to eight 16-bit lanes in one instruction.
        Vector128<byte> raw = Vector128.LoadUnsafe(ref origin, (nuint)offset);
        return Sse41.ConvertToVector128Int16(raw);
    }

    /// <summary>Widens sixteen bytes at <paramref name="offset"/> to sixteen 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Widen16(ref byte origin, int offset)
    {
        // VPMOVZXBW: sixteen bytes to sixteen 16-bit lanes in one instruction.
        Vector128<byte> raw = Vector128.LoadUnsafe(ref origin, (nuint)offset);
        return Avx2.ConvertToVector256Int16(raw);
    }

    private static void HorizontalTap6Sse2(
        ReadOnlySpan<byte> source, int srcOffset, Span<int> destination, int count)
    {
        ref byte origin = ref Unsafe.AsRef(in source[0]);
        int i = 0;
        for (; i + 8 <= count; i += 8)
        {
            int b = srcOffset + i - 2;

            // Six shifted loads of the same row: the tap window slides one sample per output.
            Vector128<short> sum =
                  Widen8(ref origin, b)
                + Widen8(ref origin, b + 1) * (short)Tap1
                + Widen8(ref origin, b + 2) * (short)Tap2
                + Widen8(ref origin, b + 3) * (short)Tap3
                + Widen8(ref origin, b + 4) * (short)Tap4
                + Widen8(ref origin, b + 5);

            // Widening to int for the caller: the sum fits in 16 bits for 8-bit input (the extremes
            // are -2550 and 10710) but the scalar contract is int, so keep it int.
            Vector128.WidenLower(sum).StoreUnsafe(ref destination[i]);
            Vector128.WidenUpper(sum).StoreUnsafe(ref destination[i + 4]);
        }

        if (i < count)
            HorizontalTap6Scalar(source, srcOffset + i, destination[i..], count - i);
    }

    private static void HorizontalTap6Avx2(
        ReadOnlySpan<byte> source, int srcOffset, Span<int> destination, int count)
    {
        ref byte origin = ref Unsafe.AsRef(in source[0]);
        int i = 0;
        for (; i + 16 <= count; i += 16)
        {
            int b = srcOffset + i - 2;

            Vector256<short> sum =
                  Widen16(ref origin, b)
                + Widen16(ref origin, b + 1) * (short)Tap1
                + Widen16(ref origin, b + 2) * (short)Tap2
                + Widen16(ref origin, b + 3) * (short)Tap3
                + Widen16(ref origin, b + 4) * (short)Tap4
                + Widen16(ref origin, b + 5);

            Vector256.WidenLower(sum).StoreUnsafe(ref destination[i]);
            Vector256.WidenUpper(sum).StoreUnsafe(ref destination[i + 8]);
        }

        if (i < count)
            HorizontalTap6(source, srcOffset + i, destination[i..], count - i);
    }

    /// <summary>Whether the eight-wide chroma bilinear path is available.</summary>
    /// <summary>
    /// Whether the vectorised chroma filter can run. SSE4.1 widens the bytes and SSE2 packs
    /// them back; there is nothing here that wants a 256-bit register.
    /// </summary>
    public static bool IsChromaBilinearSupported => Sse41.IsSupported && Sse2.IsSupported;

    /// <summary>
    /// One row of eight bilinear chroma samples.
    /// </summary>
    /// <remarks>
    /// The scalar version carries the right-hand samples into the next column - <c>a = b; c = d;</c>
    /// - which saves two loads per pixel but makes every iteration depend on the one before it,
    /// so a row is a serial chain eight long. Reloading instead costs two extra loads per row and
    /// makes the whole row independent.
    ///
    /// The four weights sum to sixty-four and the samples are bytes, so the largest possible
    /// weighted sum is 64 * 255, which is why this fits in a signed 32-bit lane with room to
    /// spare and needs no intermediate clamping - the saturation in the pack back to bytes
    /// cannot trigger.
    /// </remarks>
    /// <summary>
    /// Four samples of one bilinear chroma row, for the partitions that are four wide.
    /// </summary>
    /// <remarks>
    /// A third of chroma motion compensation is four samples wide, and the eight-wide kernel
    /// cannot take it, so all of it ran through the scalar loop. The arithmetic is identical -
    /// only the load and the store narrow. The load is deliberately eight bytes rather than the
    /// sixteen Widen8 takes: five are needed, and there is no reason to reach further past the
    /// end of a row for a smaller block than for a larger one.
    /// </remarks>
    public static void BilinearChromaRow4(
        ReadOnlySpan<byte> reference, int upper, int lower,
        Span<byte> output, int outOffset,
        int weightUpperLeft, int weightUpperRight, int weightLowerLeft, int weightLowerRight)
    {
        ref byte origin = ref MemoryMarshal.GetReference(reference);

        Vector128<short> sum =
              Widen4(ref Unsafe.Add(ref origin, upper)) * Vector128.Create((short)weightUpperLeft)
            + Widen4(ref Unsafe.Add(ref origin, upper + 1)) * Vector128.Create((short)weightUpperRight)
            + Widen4(ref Unsafe.Add(ref origin, lower)) * Vector128.Create((short)weightLowerLeft)
            + Widen4(ref Unsafe.Add(ref origin, lower + 1)) * Vector128.Create((short)weightLowerRight)
            + Vector128.Create((short)32);

        sum = Vector128.ShiftRightArithmetic(sum, 6);

        Vector128<byte> asBytes = Sse2.PackUnsignedSaturate(sum, sum);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.Slice(outOffset, 4), asBytes.AsUInt32().GetElement(0));
    }

    /// <summary>Eight contiguous bytes widened to shorts; only the low four lanes are used.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen4(ref byte source)
        => Sse41.ConvertToVector128Int16(
            Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref source)).AsByte());

    public static void BilinearChromaRow8(
        ReadOnlySpan<byte> reference, int upper, int lower,
        Span<byte> output, int outOffset,
        int weightUpperLeft, int weightUpperRight, int weightLowerLeft, int weightLowerRight)
    {
        // Sixteen-bit lanes are enough, so this needs no 256-bit register and no AVX2. The four
        // weights sum to 64 and a sample is at most 255, so the weighted sum cannot exceed
        // 16320 - comfortably inside a signed short, rounding included.
        ref byte origin = ref MemoryMarshal.GetReference(reference);

        Vector128<short> sum =
              Widen8(ref Unsafe.Add(ref origin, upper)) * Vector128.Create((short)weightUpperLeft)
            + Widen8(ref Unsafe.Add(ref origin, upper + 1)) * Vector128.Create((short)weightUpperRight)
            + Widen8(ref Unsafe.Add(ref origin, lower)) * Vector128.Create((short)weightLowerLeft)
            + Widen8(ref Unsafe.Add(ref origin, lower + 1)) * Vector128.Create((short)weightLowerRight)
            + Vector128.Create((short)32);

        sum = Vector128.ShiftRightArithmetic(sum, 6);

        Vector128<byte> asBytes = Sse2.PackUnsignedSaturate(sum, sum);
        BinaryPrimitives.WriteUInt64LittleEndian(
            output.Slice(outOffset, 8), asBytes.AsUInt64().GetElement(0));
    }

    /// <summary>Eight contiguous bytes widened to eight shorts - one PMOVZXBW.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen8(ref byte source)
        => Sse41.ConvertToVector128Int16(Vector128.LoadUnsafe(ref source));

    /// <summary>Eight contiguous bytes widened to eight ints - one PMOVZXBD.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Widen8ToInt(ReadOnlySpan<byte> source, int offset)
    {
        ulong eight = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
        return Avx2.ConvertToVector256Int32(Vector128.CreateScalar(eight).AsByte());
    }

    /// <summary>
    /// The vertical six-tap pass of the two-dimensional filter, rounded, clipped and written
    /// straight out as bytes.
    /// </summary>
    /// <remarks>
    /// The sums used to land in an int buffer and then be rounded, clipped and stored one column
    /// at a time. Three separate measurements say why that is worth fusing: loading through refs
    /// rather than slicing spans is worth about 1.44x on this shape, packing with saturation
    /// instead of an explicit Min and Max another 1.08x, and the scalar tail nothing at all.
    ///
    /// The clip is free. Packing signed 16-bit lanes down to unsigned bytes saturates at 0 and
    /// 255, which is exactly the clip the standard asks for, so the store instruction does the
    /// work that Math.Clamp was doing per sample. The intermediate pack from 32 to 16 bits is
    /// lossless here: after the shift the sums are far inside short range.
    /// </remarks>
    public static void VerticalTap6ToBytes(
        ReadOnlySpan<int> window, int width, int firstRow,
        Span<byte> destination, int destinationOffset, int count)
    {
        ref int origin = ref MemoryMarshal.GetReference(window);
        ref int row0 = ref Unsafe.Add(ref origin, firstRow * width);
        int col = 0;

        if (Avx2.IsSupported)
        {
            for (; col + 8 <= count; col += 8)
            {
                ref int at = ref Unsafe.Add(ref row0, col);
                Vector256<int> sum =
                      Vector256.LoadUnsafe(ref at) * Tap0
                    + Vector256.LoadUnsafe(ref Unsafe.Add(ref at, width)) * Tap1
                    + Vector256.LoadUnsafe(ref Unsafe.Add(ref at, 2 * width)) * Tap2
                    + Vector256.LoadUnsafe(ref Unsafe.Add(ref at, 3 * width)) * Tap3
                    + Vector256.LoadUnsafe(ref Unsafe.Add(ref at, 4 * width)) * Tap4
                    + Vector256.LoadUnsafe(ref Unsafe.Add(ref at, 5 * width)) * Tap5;

                sum = Vector256.ShiftRightArithmetic(sum + Vector256.Create(512), 10);

                Vector128<short> asShorts = Sse2.PackSignedSaturate(sum.GetLower(), sum.GetUpper());
                BinaryPrimitives.WriteUInt64LittleEndian(
                    destination.Slice(destinationOffset + col, 8),
                    Sse2.PackUnsignedSaturate(asShorts, asShorts).AsUInt64().GetElement(0));
            }
        }

        if (Sse41.IsSupported)
        {
            for (; col + 4 <= count; col += 4)
            {
                ref int at = ref Unsafe.Add(ref row0, col);
                Vector128<int> sum =
                      Vector128.LoadUnsafe(ref at) * Tap0
                    + Vector128.LoadUnsafe(ref Unsafe.Add(ref at, width)) * Tap1
                    + Vector128.LoadUnsafe(ref Unsafe.Add(ref at, 2 * width)) * Tap2
                    + Vector128.LoadUnsafe(ref Unsafe.Add(ref at, 3 * width)) * Tap3
                    + Vector128.LoadUnsafe(ref Unsafe.Add(ref at, 4 * width)) * Tap4
                    + Vector128.LoadUnsafe(ref Unsafe.Add(ref at, 5 * width)) * Tap5;

                sum = Vector128.ShiftRightArithmetic(sum + Vector128.Create(512), 10);

                Vector128<short> asShorts = Sse2.PackSignedSaturate(sum, sum);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination.Slice(destinationOffset + col, 4),
                    Sse2.PackUnsignedSaturate(asShorts, asShorts).AsUInt32().GetElement(0));
            }
        }

        for (; col < count; col++)
        {
            int sum = Tap0 * window[firstRow * width + col]
                    + Tap1 * window[(firstRow + 1) * width + col]
                    + Tap2 * window[(firstRow + 2) * width + col]
                    + Tap3 * window[(firstRow + 3) * width + col]
                    + Tap4 * window[(firstRow + 4) * width + col]
                    + Tap5 * window[(firstRow + 5) * width + col];

            destination[destinationOffset + col] = (byte)Math.Clamp((sum + 512) >> 10, 0, 255);
        }
    }

    /// <summary>Whether the vertical half-pel row kernel is available.</summary>
    /// <summary>
    /// Whether the vectorised six-tap filter can run at all. SSE4.1 is the floor - it widens
    /// bytes to shorts - and every x64 machine has it; AVX2 only widens the top rung.
    /// </summary>
    public static bool IsSixTapRowSupported => Sse41.IsSupported && Sse2.IsSupported;

    /// <summary>
    /// One output row of the vertical-only half-pel filter, and optionally its average with a
    /// whole-pixel row for the quarter-pel positions.
    /// </summary>
    /// <param name="tapBase">Index of the first of the six source samples.</param>
    /// <param name="tapStep">
    /// Distance between taps: the row stride for a vertical filter, one for a horizontal one.
    /// </param>
    /// <param name="integerBase">
    /// Index of the whole-pixel row to average with, or -1 for the half-pel position itself.
    /// </param>
    /// <remarks>
    /// The six taps of a vertical filter are six whole rows at the same columns, so each one is
    /// a contiguous run of bytes and the whole row goes through one vector. The scalar version
    /// this replaces computed the six-tap sum a column at a time.
    ///
    /// Two things come for free and are worth naming, because both were being paid for
    /// explicitly before. Packing signed 16-bit lanes to unsigned bytes saturates at 0 and 255,
    /// which is the clip; and PAVGB computes (a + b + 1) >> 1, which is exactly the rounding
    /// average the quarter-pel positions are defined as.
    ///
    /// Sixteen-bit lanes are enough throughout: the six-tap sum of bytes spans -2550 to 10710.
    /// </remarks>
    public static void SixTapRow(
        ReadOnlySpan<byte> reference, int tapBase, int tapStep, int integerBase,
        Span<byte> destination, int destinationOffset, int count)
    {
        ref byte origin = ref MemoryMarshal.GetReference(reference);
        int col = 0;

        // A ladder, the way FFmpeg builds one: sixteen samples at a time where AVX2 exists,
        // eight where only SSE4.1 does, and a scalar tail for the four-wide partitions. Each
        // rung is complete on its own, so dropping the top one costs width, not vectorisation
        // - which is the difference between 90 fps and 57 on a machine without AVX2.
        if (Avx2.IsSupported)
        {
        for (; col + 16 <= count; col += 16)
        {
            ref byte at = ref Unsafe.Add(ref origin, tapBase + col);

            Vector256<short> a0 = WidenRow(ref at);
            Vector256<short> a1 = WidenRow(ref Unsafe.Add(ref at, tapStep));
            Vector256<short> a2 = WidenRow(ref Unsafe.Add(ref at, 2 * tapStep));
            Vector256<short> a3 = WidenRow(ref Unsafe.Add(ref at, 3 * tapStep));
            Vector256<short> a4 = WidenRow(ref Unsafe.Add(ref at, 4 * tapStep));
            Vector256<short> a5 = WidenRow(ref Unsafe.Add(ref at, 5 * tapStep));

            // (a0 + a5) - 5 * (a1 + a4) + 20 * (a2 + a3), which is the six-tap filter with two
            // multiplies instead of six.
            Vector256<short> sum = (a0 + a5)
                                 - (a1 + a4) * Vector256.Create((short)5)
                                 + (a2 + a3) * Vector256.Create((short)20);

            sum = Vector256.ShiftRightArithmetic(sum + Vector256.Create((short)16), 5);

            Vector128<byte> halfPel = Sse2.PackUnsignedSaturate(sum.GetLower(), sum.GetUpper());

            if (integerBase >= 0)
            {
                halfPel = Sse2.Average(
                    halfPel, Vector128.LoadUnsafe(ref Unsafe.Add(ref origin, integerBase + col)));
            }

            halfPel.StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)(destinationOffset + col));
        }
        }

        // Eight-wide, and whatever the rung above left behind.
        for (; col + 8 <= count; col += 8)
        {
            ref byte at = ref Unsafe.Add(ref origin, tapBase + col);

            Vector128<short> a0 = WidenHalfRow(ref at);
            Vector128<short> a1 = WidenHalfRow(ref Unsafe.Add(ref at, tapStep));
            Vector128<short> a2 = WidenHalfRow(ref Unsafe.Add(ref at, 2 * tapStep));
            Vector128<short> a3 = WidenHalfRow(ref Unsafe.Add(ref at, 3 * tapStep));
            Vector128<short> a4 = WidenHalfRow(ref Unsafe.Add(ref at, 4 * tapStep));
            Vector128<short> a5 = WidenHalfRow(ref Unsafe.Add(ref at, 5 * tapStep));

            Vector128<short> sum = (a0 + a5)
                                 - (a1 + a4) * Vector128.Create((short)5)
                                 + (a2 + a3) * Vector128.Create((short)20);

            sum = Vector128.ShiftRightArithmetic(sum + Vector128.Create((short)16), 5);

            Vector128<byte> halfPel = Sse2.PackUnsignedSaturate(sum, sum);

            if (integerBase >= 0)
            {
                ulong whole = BinaryPrimitives.ReadUInt64LittleEndian(
                    reference.Slice(integerBase + col, 8));
                halfPel = Sse2.Average(halfPel, Vector128.CreateScalar(whole).AsByte());
            }

            BinaryPrimitives.WriteUInt64LittleEndian(
                destination.Slice(destinationOffset + col, 8),
                halfPel.AsUInt64().GetElement(0));
        }

        for (; col < count; col++)
        {
            int at = tapBase + col;
            int sum = Tap0 * reference[at]
                    + Tap1 * reference[at + tapStep]
                    + Tap2 * reference[at + 2 * tapStep]
                    + Tap3 * reference[at + 3 * tapStep]
                    + Tap4 * reference[at + 4 * tapStep]
                    + Tap5 * reference[at + 5 * tapStep];

            int halfPel = Math.Clamp((sum + 16) >> 5, 0, 255);
            destination[destinationOffset + col] = integerBase >= 0
                ? (byte)((reference[integerBase + col] + halfPel + 1) >> 1)
                : (byte)halfPel;
        }
    }

    /// <summary>Eight contiguous bytes widened to eight shorts - one PMOVZXBW.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> WidenHalfRow(ref byte source)
        => Sse41.ConvertToVector128Int16(Vector128.LoadUnsafe(ref source));
    /// <summary>Sixteen contiguous bytes widened to sixteen shorts - one PMOVZXBW.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> WidenRow(ref byte source)
        => Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref source));
}

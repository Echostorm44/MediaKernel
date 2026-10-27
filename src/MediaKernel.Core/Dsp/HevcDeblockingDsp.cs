using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Vectorised HEVC luma deblocking, written without pointers or unsafe loads.
/// </summary>
/// <remarks>
/// <para>
/// The filter works on four lines of an edge at a time, and every line is independent: the same
/// arithmetic, different pixels. That is four lanes of a 128-bit vector with no cross-lane traffic
/// at all - the shape SIMD is for. The decisions above it (strong or weak, whether to extend to
/// p1/q1, whether either side is filtered) are taken once for all four lines and stay scalar.
/// </para>
/// <para>
/// What direction the edge runs changes only how the lines are addressed. For an edge that runs
/// horizontally the four lines are four adjacent columns, so a lane is one column and the loads are
/// contiguous. For an edge that runs vertically the four lines are four adjacent rows, so a lane is
/// one row and the eight pixels of a line lie contiguous instead - which needs a transpose. The
/// arithmetic below is shared by both; only the load and store differ.
/// </para>
/// <para>
/// The one place a per-lane decision survives is the weak filter's <c>|delta| &lt; 10 * tc</c> test,
/// which is per line. Rather than branch, both outcomes are computed and selected by mask, which is
/// what leaves the whole thing branch-free.
/// </para>
/// </remarks>
internal static class HevcDeblockingDsp
{
    /// <summary>Whether the vector path is available.</summary>
    public static bool IsSupported => Sse41.IsSupported;

    /// <summary>Widens four samples at <paramref name="offset"/> to four 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ReadOnlySpan<ushort> samples, int offset)
        => Vector128.WidenLower(Vector64.Create(samples.Slice(offset, 4)).ToVector128()).AsInt32();

    /// <summary>Narrows four lanes back to samples at <paramref name="offset"/>.</summary>
    /// <remarks>
    /// The lanes are already inside [0, maxValue], so the narrow cannot lose anything; only the
    /// lower half of the result is wanted, since Narrow needs eight lanes to fill a whole vector.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(Span<ushort> samples, int offset, Vector128<int> value)
        => Vector128.Narrow(value.AsUInt32(), value.AsUInt32())
                    .GetLower()
                    .CopyTo(samples.Slice(offset, 4));

    /// <summary>Clips <paramref name="value"/> to +/- <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClipTo(Vector128<int> value, Vector128<int> limit)
        => Vector128.Min(Vector128.Max(value, -limit), limit);

    /// <summary>Clips to pixel range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClipToPixel(Vector128<int> value, Vector128<int> maxValue)
        => Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), maxValue);

    // ------------------------------------------------------------------ strong filter

    /// <summary>
    /// The strong filter for four lines, spec 8.7.2.5.7. Every position is clipped to +/- 2*tC.
    /// </summary>
    /// <remarks>
    /// The six outputs are written before any of them is read again, so a caller filtering in place
    /// is safe: all eight inputs are loaded up front.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrongFilterCore(
        Vector128<int> p3, Vector128<int> p2, Vector128<int> p1, Vector128<int> p0,
        Vector128<int> q0, Vector128<int> q1, Vector128<int> q2, Vector128<int> q3,
        int tc2, int maxValue,
        out Vector128<int> np2, out Vector128<int> np1, out Vector128<int> np0,
        out Vector128<int> nq0, out Vector128<int> nq1, out Vector128<int> nq2)
    {
        var limit = Vector128.Create(tc2);
        var top = Vector128.Create(maxValue);
        var two = Vector128.Create(2);
        var three = Vector128.Create(3);
        var four = Vector128.Create(4);

        np0 = ClipToPixel(p0 + ClipTo((((p2 + two * p1 + two * p0 + two * q0 + q1 + four) >> 3) - p0), limit), top);
        np1 = ClipToPixel(p1 + ClipTo((((p2 + p1 + p0 + q0 + two) >> 2) - p1), limit), top);
        np2 = ClipToPixel(p2 + ClipTo((((two * p3 + three * p2 + p1 + p0 + q0 + four) >> 3) - p2), limit), top);

        nq0 = ClipToPixel(q0 + ClipTo((((p1 + two * p0 + two * q0 + two * q1 + q2 + four) >> 3) - q0), limit), top);
        nq1 = ClipToPixel(q1 + ClipTo((((p0 + q0 + q1 + q2 + two) >> 2) - q1), limit), top);
        nq2 = ClipToPixel(q2 + ClipTo((((p0 + q0 + q1 + three * q2 + two * q3 + four) >> 3) - q2), limit), top);
    }

    // ------------------------------------------------------------------ weak filter

    /// <summary>
    /// The weak filter for four lines, spec 8.7.2.5.7.
    /// </summary>
    /// <remarks>
    /// A line whose delta fails the <c>|delta| &lt; 10 * tc</c> test is left completely alone -
    /// including its p1 and q1. That is expressed as a mask rather than a branch, so the outputs
    /// carry the original samples through on those lanes and the caller can store unconditionally.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WeakFilterCore(
        Vector128<int> p2, Vector128<int> p1, Vector128<int> p0,
        Vector128<int> q0, Vector128<int> q1, Vector128<int> q2,
        int tc, bool extendP, bool extendQ, int maxValue,
        out Vector128<int> np1, out Vector128<int> np0,
        out Vector128<int> nq0, out Vector128<int> nq1)
    {
        var top = Vector128.Create(maxValue);
        var one = Vector128.Create(1);
        var tcLimit = Vector128.Create(tc);
        var halfLimit = Vector128.Create(tc >> 1);

        Vector128<int> delta = (Vector128.Create(9) * (q0 - p0) - Vector128.Create(3) * (q1 - p1)
                                + Vector128.Create(8)) >> 4;

        // Per line, and the only per-lane decision left in the filter.
        Vector128<int> filtered = Vector128.LessThan(Vector128.Abs(delta), Vector128.Create(10 * tc));

        Vector128<int> clipped = ClipTo(delta, tcLimit);

        np0 = Vector128.ConditionalSelect(filtered, ClipToPixel(p0 + clipped, top), p0);
        nq0 = Vector128.ConditionalSelect(filtered, ClipToPixel(q0 - clipped, top), q0);

        if (extendP)
        {
            Vector128<int> deltaP1 = ClipTo(((((p2 + p0 + one) >> 1) - p1 + clipped) >> 1), halfLimit);
            np1 = Vector128.ConditionalSelect(filtered, ClipToPixel(p1 + deltaP1, top), p1);
        }
        else
        {
            np1 = p1;
        }

        if (extendQ)
        {
            Vector128<int> deltaQ1 = ClipTo(((((q2 + q0 + one) >> 1) - q1 - clipped) >> 1), halfLimit);
            nq1 = Vector128.ConditionalSelect(filtered, ClipToPixel(q1 + deltaQ1, top), q1);
        }
        else
        {
            nq1 = q1;
        }
    }

    // ------------------------------------------------------------------ horizontal edge

    /// <summary>
    /// The strong filter across an edge that runs horizontally, for four adjacent columns.
    /// </summary>
    /// <param name="origin">Index of q0 in the leftmost of the four columns.</param>
    /// <param name="stride">Distance between the lines the filter reaches across.</param>
    public static void StrongFilterHorizontal4(
        Span<ushort> samples, int origin, int stride, int tc2, int maxValue, bool noP, bool noQ)
    {
        Vector128<int> p3 = Load4(samples, origin - 4 * stride);
        Vector128<int> p2 = Load4(samples, origin - 3 * stride);
        Vector128<int> p1 = Load4(samples, origin - 2 * stride);
        Vector128<int> p0 = Load4(samples, origin - stride);
        Vector128<int> q0 = Load4(samples, origin);
        Vector128<int> q1 = Load4(samples, origin + stride);
        Vector128<int> q2 = Load4(samples, origin + 2 * stride);
        Vector128<int> q3 = Load4(samples, origin + 3 * stride);

        StrongFilterCore(p3, p2, p1, p0, q0, q1, q2, q3, tc2, maxValue,
            out var np2, out var np1, out var np0, out var nq0, out var nq1, out var nq2);

        if (!noP)
        {
            Store4(samples, origin - stride, np0);
            Store4(samples, origin - 2 * stride, np1);
            Store4(samples, origin - 3 * stride, np2);
        }

        if (!noQ)
        {
            Store4(samples, origin, nq0);
            Store4(samples, origin + stride, nq1);
            Store4(samples, origin + 2 * stride, nq2);
        }
    }

    /// <summary>
    /// The weak filter across an edge that runs horizontally, for four adjacent columns.
    /// </summary>
    public static void WeakFilterHorizontal4(
        Span<ushort> samples, int origin, int stride, int tc, int maxValue,
        bool extendP, bool extendQ, bool noP, bool noQ)
    {
        Vector128<int> p2 = Load4(samples, origin - 3 * stride);
        Vector128<int> p1 = Load4(samples, origin - 2 * stride);
        Vector128<int> p0 = Load4(samples, origin - stride);
        Vector128<int> q0 = Load4(samples, origin);
        Vector128<int> q1 = Load4(samples, origin + stride);
        Vector128<int> q2 = Load4(samples, origin + 2 * stride);

        WeakFilterCore(p2, p1, p0, q0, q1, q2, tc, extendP && !noP, extendQ && !noQ, maxValue,
            out var np1, out var np0, out var nq0, out var nq1);

        if (!noP)
        {
            Store4(samples, origin - stride, np0);
            if (extendP) Store4(samples, origin - 2 * stride, np1);
        }

        if (!noQ)
        {
            Store4(samples, origin, nq0);
            if (extendQ) Store4(samples, origin + stride, nq1);
        }
    }

    // ------------------------------------------------------------------ vertical edge

    /// <summary>
    /// Transposes a 4x4 block held in the low four lanes of each vector.
    /// </summary>
    /// <remarks>
    /// This is the whole trick for vertical edges. A transpose is its own inverse, so the same
    /// helper serves both the load and the store - there is no second, mirror-image routine to get
    /// subtly wrong. The eight pixels of a line split into two 4x4 blocks (p3..p0 and q0..q3),
    /// which is why a 4x4 helper is enough for an 8-wide window.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4(
        Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> c, Vector128<ushort> d,
        out Vector128<ushort> o0, out Vector128<ushort> o1,
        out Vector128<ushort> o2, out Vector128<ushort> o3)
    {
        Vector128<ushort> ab = Sse2.UnpackLow(a, b);   // a0 b0 a1 b1 a2 b2 a3 b3
        Vector128<ushort> cd = Sse2.UnpackLow(c, d);   // c0 d0 c1 d1 c2 d2 c3 d3

        Vector128<ushort> lo = Sse2.UnpackLow(ab.AsUInt32(), cd.AsUInt32()).AsUInt16();
        Vector128<ushort> hi = Sse2.UnpackHigh(ab.AsUInt32(), cd.AsUInt32()).AsUInt16();

        o0 = lo;                       // low four lanes: column 0
        o1 = Upper64ToLower(lo);       // column 1
        o2 = hi;                       // column 2
        o3 = Upper64ToLower(hi);       // column 3
    }

    /// <summary>Moves the upper 64 bits into the lower 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Upper64ToLower(Vector128<ushort> value)
        => Sse2.Shuffle(value.AsUInt32(), 0b_11_10_11_10).AsUInt16();

    /// <summary>Widens the low four lanes to four 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenLow4(Vector128<ushort> value)
        => Vector128.WidenLower(value).AsInt32();

    /// <summary>Narrows four 32-bit lanes into the low four lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> NarrowToLow4(Vector128<int> value)
        => Vector128.Narrow(value.AsUInt32(), value.AsUInt32());

    /// <summary>Loads four lines of an edge that runs vertically, transposed into lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadVertical(
        ReadOnlySpan<ushort> samples, int origin, int stride,
        out Vector128<int> p3, out Vector128<int> p2, out Vector128<int> p1, out Vector128<int> p0,
        out Vector128<int> q0, out Vector128<int> q1, out Vector128<int> q2, out Vector128<int> q3)
    {
        // Each row holds its whole window contiguously: p3 p2 p1 p0 q0 q1 q2 q3.
        Vector128<ushort> r0 = Vector128.Create(samples.Slice(origin - 4, 8));
        Vector128<ushort> r1 = Vector128.Create(samples.Slice(origin + stride - 4, 8));
        Vector128<ushort> r2 = Vector128.Create(samples.Slice(origin + 2 * stride - 4, 8));
        Vector128<ushort> r3 = Vector128.Create(samples.Slice(origin + 3 * stride - 4, 8));

        Transpose4(r0, r1, r2, r3, out var c0, out var c1, out var c2, out var c3);
        p3 = WidenLow4(c0); p2 = WidenLow4(c1); p1 = WidenLow4(c2); p0 = WidenLow4(c3);

        Transpose4(Upper64ToLower(r0), Upper64ToLower(r1), Upper64ToLower(r2), Upper64ToLower(r3),
            out var c4, out var c5, out var c6, out var c7);
        q0 = WidenLow4(c4); q1 = WidenLow4(c5); q2 = WidenLow4(c6); q3 = WidenLow4(c7);
    }

    /// <summary>Stores four lines of an edge that runs vertically, transposing back.</summary>
    /// <remarks>
    /// All eight positions are written, including p3 and q3 which the filter never changes: the
    /// caller passes their original values straight through, so writing them back costs one store
    /// and saves masking the ones that did change.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreVertical(
        Span<ushort> samples, int origin, int stride,
        Vector128<int> p3, Vector128<int> p2, Vector128<int> p1, Vector128<int> p0,
        Vector128<int> q0, Vector128<int> q1, Vector128<int> q2, Vector128<int> q3)
    {
        Transpose4(NarrowToLow4(p3), NarrowToLow4(p2), NarrowToLow4(p1), NarrowToLow4(p0),
            out var pr0, out var pr1, out var pr2, out var pr3);
        Transpose4(NarrowToLow4(q0), NarrowToLow4(q1), NarrowToLow4(q2), NarrowToLow4(q3),
            out var qr0, out var qr1, out var qr2, out var qr3);

        // Each row's two halves rejoin into the eight contiguous samples it started as.
        Sse2.UnpackLow(pr0.AsUInt64(), qr0.AsUInt64()).AsUInt16().CopyTo(samples.Slice(origin - 4, 8));
        Sse2.UnpackLow(pr1.AsUInt64(), qr1.AsUInt64()).AsUInt16().CopyTo(samples.Slice(origin + stride - 4, 8));
        Sse2.UnpackLow(pr2.AsUInt64(), qr2.AsUInt64()).AsUInt16().CopyTo(samples.Slice(origin + 2 * stride - 4, 8));
        Sse2.UnpackLow(pr3.AsUInt64(), qr3.AsUInt64()).AsUInt16().CopyTo(samples.Slice(origin + 3 * stride - 4, 8));
    }

    /// <summary>
    /// The strong filter across an edge that runs vertically, for four adjacent rows.
    /// </summary>
    /// <param name="origin">Index of q0 in the topmost of the four rows.</param>
    /// <param name="stride">Distance between rows.</param>
    public static void StrongFilterVertical4(
        Span<ushort> samples, int origin, int stride, int tc2, int maxValue, bool noP, bool noQ)
    {
        LoadVertical(samples, origin, stride,
            out var p3, out var p2, out var p1, out var p0,
            out var q0, out var q1, out var q2, out var q3);

        StrongFilterCore(p3, p2, p1, p0, q0, q1, q2, q3, tc2, maxValue,
            out var np2, out var np1, out var np0, out var nq0, out var nq1, out var nq2);

        if (noP) { np0 = p0; np1 = p1; np2 = p2; }
        if (noQ) { nq0 = q0; nq1 = q1; nq2 = q2; }

        StoreVertical(samples, origin, stride, p3, np2, np1, np0, nq0, nq1, nq2, q3);
    }

    /// <summary>
    /// The weak filter across an edge that runs vertically, for four adjacent rows.
    /// </summary>
    public static void WeakFilterVertical4(
        Span<ushort> samples, int origin, int stride, int tc, int maxValue,
        bool extendP, bool extendQ, bool noP, bool noQ)
    {
        LoadVertical(samples, origin, stride,
            out var p3, out var p2, out var p1, out var p0,
            out var q0, out var q1, out var q2, out var q3);

        WeakFilterCore(p2, p1, p0, q0, q1, q2, tc, extendP && !noP, extendQ && !noQ, maxValue,
            out var np1, out var np0, out var nq0, out var nq1);

        if (noP) { np0 = p0; np1 = p1; }
        if (noQ) { nq0 = q0; nq1 = q1; }

        StoreVertical(samples, origin, stride, p3, p2, np1, np0, nq0, nq1, q2, q3);
    }

    /// <summary>The definition for <see cref="StrongFilterVertical4"/>.</summary>
    public static void StrongFilterVertical4Scalar(
        Span<ushort> samples, int origin, int stride, int tc2, int maxValue, bool noP, bool noQ)
    {
        for (int d = 0; d < 4; d++)
        {
            int at = origin + d * stride;
            int p3 = samples[at - 4];
            int p2 = samples[at - 3];
            int p1 = samples[at - 2];
            int p0 = samples[at - 1];
            int q0 = samples[at];
            int q1 = samples[at + 1];
            int q2 = samples[at + 2];
            int q3 = samples[at + 3];

            if (!noP)
            {
                samples[at - 1] = (ushort)Math.Clamp(
                    p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, maxValue);
                samples[at - 2] = (ushort)Math.Clamp(
                    p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, maxValue);
                samples[at - 3] = (ushort)Math.Clamp(
                    p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, maxValue);
            }

            if (!noQ)
            {
                samples[at] = (ushort)Math.Clamp(
                    q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, maxValue);
                samples[at + 1] = (ushort)Math.Clamp(
                    q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, maxValue);
                samples[at + 2] = (ushort)Math.Clamp(
                    q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, maxValue);
            }
        }
    }

    /// <summary>The definition for <see cref="WeakFilterVertical4"/>.</summary>
    public static void WeakFilterVertical4Scalar(
        Span<ushort> samples, int origin, int stride, int tc, int maxValue,
        bool extendP, bool extendQ, bool noP, bool noQ)
    {
        int tcDiv2 = tc >> 1;

        for (int d = 0; d < 4; d++)
        {
            int at = origin + d * stride;
            int p2 = samples[at - 3];
            int p1 = samples[at - 2];
            int p0 = samples[at - 1];
            int q0 = samples[at];
            int q1 = samples[at + 1];
            int q2 = samples[at + 2];

            int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
            if (Math.Abs(delta0) >= 10 * tc) continue;

            delta0 = Math.Clamp(delta0, -tc, tc);

            if (!noP) samples[at - 1] = (ushort)Math.Clamp(p0 + delta0, 0, maxValue);
            if (!noQ) samples[at] = (ushort)Math.Clamp(q0 - delta0, 0, maxValue);

            if (extendP && !noP)
            {
                int deltaP1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                samples[at - 2] = (ushort)Math.Clamp(p1 + deltaP1, 0, maxValue);
            }

            if (extendQ && !noQ)
            {
                int deltaQ1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                samples[at + 1] = (ushort)Math.Clamp(q1 + deltaQ1, 0, maxValue);
            }
        }
    }

    // ------------------------------------------------------------------ definitions

    /// <summary>The definition for <see cref="StrongFilterHorizontal4"/>.</summary>
    public static void StrongFilterHorizontal4Scalar(
        Span<ushort> samples, int origin, int stride, int tc2, int maxValue, bool noP, bool noQ)
    {
        for (int d = 0; d < 4; d++)
        {
            int at = origin + d;
            int p3 = samples[at - 4 * stride];
            int p2 = samples[at - 3 * stride];
            int p1 = samples[at - 2 * stride];
            int p0 = samples[at - stride];
            int q0 = samples[at];
            int q1 = samples[at + stride];
            int q2 = samples[at + 2 * stride];
            int q3 = samples[at + 3 * stride];

            if (!noP)
            {
                samples[at - stride] = (ushort)Math.Clamp(
                    p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, maxValue);
                samples[at - 2 * stride] = (ushort)Math.Clamp(
                    p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, maxValue);
                samples[at - 3 * stride] = (ushort)Math.Clamp(
                    p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, maxValue);
            }

            if (!noQ)
            {
                samples[at] = (ushort)Math.Clamp(
                    q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, maxValue);
                samples[at + stride] = (ushort)Math.Clamp(
                    q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, maxValue);
                samples[at + 2 * stride] = (ushort)Math.Clamp(
                    q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, maxValue);
            }
        }
    }

    /// <summary>The definition for <see cref="WeakFilterHorizontal4"/>.</summary>
    public static void WeakFilterHorizontal4Scalar(
        Span<ushort> samples, int origin, int stride, int tc, int maxValue,
        bool extendP, bool extendQ, bool noP, bool noQ)
    {
        int tcDiv2 = tc >> 1;

        for (int d = 0; d < 4; d++)
        {
            int at = origin + d;
            int p2 = samples[at - 3 * stride];
            int p1 = samples[at - 2 * stride];
            int p0 = samples[at - stride];
            int q0 = samples[at];
            int q1 = samples[at + stride];
            int q2 = samples[at + 2 * stride];

            int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
            if (Math.Abs(delta0) >= 10 * tc) continue;

            delta0 = Math.Clamp(delta0, -tc, tc);

            if (!noP) samples[at - stride] = (ushort)Math.Clamp(p0 + delta0, 0, maxValue);
            if (!noQ) samples[at] = (ushort)Math.Clamp(q0 - delta0, 0, maxValue);

            if (extendP && !noP)
            {
                int deltaP1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                samples[at - 2 * stride] = (ushort)Math.Clamp(p1 + deltaP1, 0, maxValue);
            }

            if (extendQ && !noQ)
            {
                int deltaQ1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                samples[at + stride] = (ushort)Math.Clamp(q1 + deltaQ1, 0, maxValue);
            }
        }
    }
}

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Vectorised eight-bit H.264 luma deblocking, written without pointers or unsafe loads.
/// </summary>
/// <remarks>
/// <para>
/// The filter runs over an edge in segments of four lines, and boundary strength, alpha and beta are
/// decided once per segment - so the four lines share every scalar decision and differ only in their
/// pixels. That is four lanes with no cross-lane traffic, the same shape as the HEVC filter in
/// <see cref="HevcDeblockingDsp"/>.
/// </para>
/// <para>
/// What is per line here is the set of gates: the three threshold tests that decide whether a line
/// is filtered at all, and then <c>ap</c> and <c>aq</c>, which decide whether p1 and q1 move and, in
/// the normal filter, also widen the clipping limit. Every one of them is computed as a mask and
/// applied with a select, so a line that fails a gate carries its original sample through and the
/// caller can store unconditionally. There is no branch anywhere in the filtering itself.
/// </para>
/// <para>
/// Only four lanes fit, since these are the four lines of one segment and the next segment has its
/// own boundary strength. That is narrower than the eight the interpolation filters get, but the
/// arithmetic per lane is far heavier - fifteen-odd operations against a handful - so the trade is
/// still worth taking.
/// </para>
/// </remarks>
internal static class H264DeblockingDsp
{
    /// <summary>Whether the vector path is available.</summary>
    public static bool IsSupported => Sse41.IsSupported;

    /// <summary>Four bytes taken one per line, <paramref name="step"/> apart.</summary>
    /// <remarks>
    /// A horizontal edge filters four adjacent columns, so its four lines are one byte apart and
    /// the whole gather is a single 32-bit load widened in one instruction. Only a vertical edge
    /// needs the four separate reads, because there its lines really are a row apart.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Gather4(ReadOnlySpan<byte> samples, int offset, int step)
    {
        if (step == 1)
        {
            uint four = BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(offset, 4));
            return Sse41.ConvertToVector128Int32(Vector128.CreateScalar(four).AsByte());
        }

        return Vector128.Create((int)samples[offset], samples[offset + step],
                                samples[offset + 2 * step], samples[offset + 3 * step]);
    }

    /// <summary>Writes four lanes back one per line, <paramref name="step"/> apart.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Scatter4(Span<byte> samples, int offset, int step, Vector128<int> value)
    {
        if (step == 1)
        {
            // The lanes hold 0..255 already, so packing twice is exact rather than saturating.
            Vector128<short> asShorts = Sse2.PackSignedSaturate(value, value);
            Vector128<byte> asBytes = Sse2.PackUnsignedSaturate(asShorts, asShorts);
            BinaryPrimitives.WriteUInt32LittleEndian(
                samples.Slice(offset, 4), asBytes.AsUInt32().ToScalar());
            return;
        }

        samples[offset] = (byte)value[0];
        samples[offset + step] = (byte)value[1];
        samples[offset + 2 * step] = (byte)value[2];
        samples[offset + 3 * step] = (byte)value[3];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClipToPixel(Vector128<int> value)
        => Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), Vector128.Create(255));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ClipTo(Vector128<int> value, Vector128<int> limit)
        => Vector128.Min(Vector128.Max(value, -limit), limit);

    /// <summary>
    /// The three threshold tests that decide whether a line is filtered at all, as a lane mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> FilterMask(
        Vector128<int> p1, Vector128<int> p0, Vector128<int> q0, Vector128<int> q1,
        int alpha, int beta)
    {
        var alphas = Vector128.Create(alpha);
        var betas = Vector128.Create(beta);

        return Vector128.LessThan(Vector128.Abs(p0 - q0), alphas)
             & Vector128.LessThan(Vector128.Abs(p1 - p0), betas)
             & Vector128.LessThan(Vector128.Abs(q1 - q0), betas);
    }

    // ------------------------------------------------------------------ normal filter, bS 1..3

    /// <summary>
    /// The normal filter for four lines, H.264 section 8.7.2.3.
    /// </summary>
    /// <remarks>
    /// <paramref name="tc0"/> is the table value for this segment's boundary strength; the limit
    /// actually used widens by one for each of p and q whose neighbouring sample is close enough,
    /// which is a per-line decision and so becomes a masked add.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormalFilterCore(
        Vector128<int> p2, Vector128<int> p1, Vector128<int> p0,
        Vector128<int> q0, Vector128<int> q1, Vector128<int> q2,
        int alpha, int beta, int tc0,
        out Vector128<int> np1, out Vector128<int> np0,
        out Vector128<int> nq0, out Vector128<int> nq1)
    {
        var one = Vector128.Create(1);
        var betas = Vector128.Create(beta);

        Vector128<int> filtered = FilterMask(p1, p0, q0, q1, alpha, beta);

        Vector128<int> ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betas);
        Vector128<int> aq = Vector128.LessThan(Vector128.Abs(q2 - q0), betas);

        // A mask is all-ones, which is -1 as an integer, so subtracting it adds one.
        Vector128<int> tc = Vector128.Create(tc0) - ap - aq;

        Vector128<int> delta = ClipTo(
            ((((q0 - p0) << 2) + (p1 - q1) + Vector128.Create(4)) >> 3), tc);

        np0 = Vector128.ConditionalSelect(filtered, ClipToPixel(p0 + delta), p0);
        nq0 = Vector128.ConditionalSelect(filtered, ClipToPixel(q0 - delta), q0);

        // p1 and q1 move only where the line is filtered and its own side is close enough; the
        // limit for them is the unwidened tc0.
        var limit0 = Vector128.Create(tc0);
        Vector128<int> average = (p0 + q0 + one) >> 1;

        Vector128<int> deltaP1 = ClipTo(((p2 + average - (p1 << 1)) >> 1), limit0);
        Vector128<int> deltaQ1 = ClipTo(((q2 + average - (q1 << 1)) >> 1), limit0);

        np1 = Vector128.ConditionalSelect(filtered & ap, ClipToPixel(p1 + deltaP1), p1);
        nq1 = Vector128.ConditionalSelect(filtered & aq, ClipToPixel(q1 + deltaQ1), q1);
    }

    // ------------------------------------------------------------------ strong filter, bS 4

    /// <summary>
    /// The strong filter for four lines, H.264 section 8.7.2.4.
    /// </summary>
    /// <remarks>
    /// Three-way per line: where the gap across the edge is small and the side is flat, three
    /// samples move; where the gap is small but the side is not flat, only the sample next to the
    /// edge moves, by a different formula; where the gap is not small, the same narrow formula
    /// applies to both sides. All three are computed and selected between.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StrongFilterCore(
        Vector128<int> p3, Vector128<int> p2, Vector128<int> p1, Vector128<int> p0,
        Vector128<int> q0, Vector128<int> q1, Vector128<int> q2, Vector128<int> q3,
        int alpha, int beta,
        out Vector128<int> np2, out Vector128<int> np1, out Vector128<int> np0,
        out Vector128<int> nq0, out Vector128<int> nq1, out Vector128<int> nq2)
    {
        var two = Vector128.Create(2);
        var three = Vector128.Create(3);
        var four = Vector128.Create(4);
        var betas = Vector128.Create(beta);

        Vector128<int> filtered = FilterMask(p1, p0, q0, q1, alpha, beta);

        Vector128<int> smallGap = Vector128.LessThan(
            Vector128.Abs(p0 - q0), Vector128.Create((alpha >> 2) + 2));
        Vector128<int> ap = Vector128.LessThan(Vector128.Abs(p2 - p0), betas);
        Vector128<int> aq = Vector128.LessThan(Vector128.Abs(q2 - q0), betas);

        // The narrow form, used when the gap is wide or the side is not flat.
        Vector128<int> narrowP0 = ((p1 << 1) + p0 + q1 + two) >> 2;
        Vector128<int> narrowQ0 = ((q1 << 1) + q0 + p1 + two) >> 2;

        Vector128<int> wideP0 = (p2 + (p1 << 1) + (p0 << 1) + (q0 << 1) + q1 + four) >> 3;
        Vector128<int> wideP1 = (p2 + p1 + p0 + q0 + two) >> 2;
        Vector128<int> wideP2 = ((p3 << 1) + three * p2 + p1 + p0 + q0 + four) >> 3;

        Vector128<int> wideQ0 = (p1 + (p0 << 1) + (q0 << 1) + (q1 << 1) + q2 + four) >> 3;
        Vector128<int> wideQ1 = (p0 + q0 + q1 + q2 + two) >> 2;
        Vector128<int> wideQ2 = (p0 + q0 + q1 + three * q2 + (q3 << 1) + four) >> 3;

        Vector128<int> widenP = smallGap & ap;
        Vector128<int> widenQ = smallGap & aq;

        np0 = Vector128.ConditionalSelect(widenP, wideP0, narrowP0);
        nq0 = Vector128.ConditionalSelect(widenQ, wideQ0, narrowQ0);

        np1 = Vector128.ConditionalSelect(widenP, wideP1, p1);
        np2 = Vector128.ConditionalSelect(widenP, wideP2, p2);
        nq1 = Vector128.ConditionalSelect(widenQ, wideQ1, q1);
        nq2 = Vector128.ConditionalSelect(widenQ, wideQ2, q2);

        // Lines that failed the threshold tests keep everything they had.
        np0 = Vector128.ConditionalSelect(filtered, np0, p0);
        np1 = Vector128.ConditionalSelect(filtered, np1, p1);
        np2 = Vector128.ConditionalSelect(filtered, np2, p2);
        nq0 = Vector128.ConditionalSelect(filtered, nq0, q0);
        nq1 = Vector128.ConditionalSelect(filtered, nq1, q1);
        nq2 = Vector128.ConditionalSelect(filtered, nq2, q2);
    }

    // ------------------------------------------------------------------ edge entry points

    /// <summary>
    /// Filters four lines of an edge, where the samples of one line are <paramref name="step"/>
    /// apart and consecutive lines are <paramref name="lineStep"/> apart.
    /// </summary>
    /// <param name="offset">Index of q0 on the first of the four lines.</param>
    /// <param name="boundaryStrength">4 selects the strong filter; 1 to 3 the normal one.</param>
    /// <param name="tc0">The table value for this segment, already scaled.</param>
    public static void FilterLuma4(
        Span<byte> samples, int offset, int step, int lineStep,
        int alpha, int beta, int boundaryStrength, int tc0)
    {
        Vector128<int> p1 = Gather4(samples, offset - 2 * step, lineStep);
        Vector128<int> p0 = Gather4(samples, offset - step, lineStep);
        Vector128<int> q0 = Gather4(samples, offset, lineStep);
        Vector128<int> q1 = Gather4(samples, offset + step, lineStep);
        Vector128<int> p2 = Gather4(samples, offset - 3 * step, lineStep);
        Vector128<int> q2 = Gather4(samples, offset + 2 * step, lineStep);

        if (boundaryStrength == 4)
        {
            Vector128<int> p3 = Gather4(samples, offset - 4 * step, lineStep);
            Vector128<int> q3 = Gather4(samples, offset + 3 * step, lineStep);

            StrongFilterCore(p3, p2, p1, p0, q0, q1, q2, q3, alpha, beta,
                out var np2, out var np1, out var np0, out var nq0, out var nq1, out var nq2);

            Scatter4(samples, offset - 3 * step, lineStep, np2);
            Scatter4(samples, offset - 2 * step, lineStep, np1);
            Scatter4(samples, offset - step, lineStep, np0);
            Scatter4(samples, offset, lineStep, nq0);
            Scatter4(samples, offset + step, lineStep, nq1);
            Scatter4(samples, offset + 2 * step, lineStep, nq2);
        }
        else
        {
            NormalFilterCore(p2, p1, p0, q0, q1, q2, alpha, beta, tc0,
                out var np1, out var np0, out var nq0, out var nq1);

            Scatter4(samples, offset - 2 * step, lineStep, np1);
            Scatter4(samples, offset - step, lineStep, np0);
            Scatter4(samples, offset, lineStep, nq0);
            Scatter4(samples, offset + step, lineStep, nq1);
        }
    }
    // ---------------------------------------------------------------- chroma edges

    /// <summary>Whether the chroma edge filter's vector path is available.</summary>
    /// <remarks>
    /// <c>Ssse3</c> is named because the vertical path's transpose uses <c>pshufb</c>, even
    /// though <c>Sse41.IsSupported</c> implies it: a kernel in this decoder once ran on a check
    /// one instruction set short of what it used, and no gate was running without AVX2 to catch
    /// it.
    /// </remarks>
    public static bool IsChromaEdgeSupported
        => Sse41.IsSupported && Ssse3.IsSupported && Sse2.IsSupported;

    /// <summary>One sample position along a whole chroma edge, widened to eight shorts.</summary>
    /// <remarks>
    /// A horizontal edge's eight lanes are eight adjacent columns, so they arrive in one 64-bit
    /// load. A vertical edge's are eight separate lines and have to be picked up one at a time,
    /// but the filter arithmetic that follows is the same either way, and that is the bulk of it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadChroma8(ReadOnlySpan<byte> samples, int at, int lineStep)
    {
        if (lineStep == 1)
        {
            ulong eight = BinaryPrimitives.ReadUInt64LittleEndian(samples.Slice(at, 8));
            return Sse41.ConvertToVector128Int16(Vector128.CreateScalar(eight).AsByte());
        }

        return Vector128.Create(
            (short)samples[at],
            (short)samples[at + lineStep],
            (short)samples[at + 2 * lineStep],
            (short)samples[at + 3 * lineStep],
            (short)samples[at + 4 * lineStep],
            (short)samples[at + 5 * lineStep],
            (short)samples[at + 6 * lineStep],
            (short)samples[at + 7 * lineStep]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreChroma8(Span<byte> samples, int at, int lineStep, Vector128<short> value)
    {
        ulong packed = Sse2.PackUnsignedSaturate(value, value).AsUInt64().ToScalar();

        if (lineStep == 1)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(samples.Slice(at, 8), packed);
            return;
        }

        for (int lane = 0; lane < 8; lane++)
            samples[at + lane * lineStep] = (byte)(packed >> (8 * lane));
    }

    /// <summary>
    /// Loads a vertical chroma edge's four sample positions with one 32-bit read per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a vertical edge p1, p0, q0 and q1 of one line are four <em>adjacent</em> bytes, so a
    /// line is a single load and the whole edge is eight - where picking up each sample position
    /// separately costs thirty-two, and thirty-two vector inserts to go with them.
    /// </para>
    /// <para>
    /// What that buys has to be paid back in a transpose, and the transpose is cheap: one
    /// <c>pshufb</c> per half lifts one sample position out of four lines at once, and joining
    /// the halves is a single 32-bit unpack. Sixteen shuffles and four unpacks against
    /// twenty-four loads and twenty-four inserts.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadChromaEdgeVertical(
        ReadOnlySpan<byte> samples, int offset, int lineStep,
        out Vector128<short> p1, out Vector128<short> p0,
        out Vector128<short> q0, out Vector128<short> q1)
    {
        // The highest byte touched is offset + 1 + 7 * lineStep, which is exactly what reading
        // the four positions separately touched - the window has not widened.
        int at = offset - 2;
        Vector128<byte> lines0To3 = Vector128.Create(
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + lineStep, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 2 * lineStep, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 3 * lineStep, 4))).AsByte();
        Vector128<byte> lines4To7 = Vector128.Create(
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 4 * lineStep, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 5 * lineStep, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 6 * lineStep, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(samples.Slice(at + 7 * lineStep, 4))).AsByte();

        p1 = SamplePositionOfEightLines(lines0To3, lines4To7, 0);
        p0 = SamplePositionOfEightLines(lines0To3, lines4To7, 1);
        q0 = SamplePositionOfEightLines(lines0To3, lines4To7, 2);
        q1 = SamplePositionOfEightLines(lines0To3, lines4To7, 3);
    }

    /// <summary>
    /// One sample position across eight lines, out of two vectors holding four packed lines each.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SamplePositionOfEightLines(
        Vector128<byte> lines0To3, Vector128<byte> lines4To7, byte position)
    {
        // A line occupies four bytes, so one sample position sits four bytes apart within each
        // half. Gather those four to the bottom of each, join, widen. The control vector is
        // built from a constant and folds away.
        Vector128<byte> control = Vector128.Create(
            position, (byte)(position + 4), (byte)(position + 8), (byte)(position + 12),
            (byte)0x80, (byte)0x80, (byte)0x80, (byte)0x80,
            (byte)0x80, (byte)0x80, (byte)0x80, (byte)0x80,
            (byte)0x80, (byte)0x80, (byte)0x80, (byte)0x80);

        Vector128<uint> low = Ssse3.Shuffle(lines0To3, control).AsUInt32();
        Vector128<uint> high = Ssse3.Shuffle(lines4To7, control).AsUInt32();
        return Sse41.ConvertToVector128Int16(Sse2.UnpackLow(low, high).AsByte());
    }

    /// <summary>
    /// Writes a vertical chroma edge's two filtered positions with one 16-bit store per line.
    /// </summary>
    /// <remarks>
    /// The chroma filter moves p0 and q0 only, and on a vertical edge those two are adjacent, so
    /// a line is one 16-bit store rather than two byte stores. Interleaving them is one shuffle.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreChromaEdgeVertical(
        Span<byte> samples, int offset, int lineStep,
        Vector128<short> p0, Vector128<short> q0)
    {
        // Pack to [p0 x8][q0 x8], then interleave to [p0,q0] per line.
        Vector128<byte> packed = Sse2.PackUnsignedSaturate(p0, q0);
        Vector128<ushort> pairs = Ssse3.Shuffle(packed, Vector128.Create(
            (byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15)).AsUInt16();

        int at = offset - 1;
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at, 2), pairs.GetElement(0));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + lineStep, 2), pairs.GetElement(1));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 2 * lineStep, 2), pairs.GetElement(2));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 3 * lineStep, 2), pairs.GetElement(3));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 4 * lineStep, 2), pairs.GetElement(4));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 5 * lineStep, 2), pairs.GetElement(5));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 6 * lineStep, 2), pairs.GetElement(6));
        BinaryPrimitives.WriteUInt16LittleEndian(samples.Slice(at + 7 * lineStep, 2), pairs.GetElement(7));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ClampPixel8(Vector128<short> value)
        => Vector128.Min(Vector128.Max(value, Vector128<short>.Zero), Vector128.Create((short)255));

    /// <summary>
    /// Filters a whole 4:2:0 chroma edge - eight lines - in one pass.
    /// </summary>
    /// <remarks>
    /// The scalar filter this replaces ran once per line per plane, so a macroblock's four chroma
    /// edges cost thirty-two calls, each re-reading its thresholds and re-deciding whether to
    /// filter. The edge is only eight lines wide, which is exactly one vector of shorts, and the
    /// boundary strength is constant over each two-line segment - so the whole edge is one pass
    /// with the strength differences carried in <paramref name="tc"/> and
    /// <paramref name="active"/> rather than in control flow.
    /// </remarks>
    /// <param name="tc">Per-lane clipping limit, already scaled and offset by one.</param>
    /// <param name="active">All-ones in lanes whose segment has a non-zero strength.</param>
    /// <param name="strong">Whether the edge takes the bS == 4 filter.</param>
    public static void FilterChromaEdge8(
        Span<byte> samples, int offset, int step, int lineStep,
        int alpha, int beta, Vector128<short> tc, Vector128<short> active, bool strong)
    {
        // A horizontal edge's eight lanes are eight adjacent columns and each sample position
        // arrives in one 64-bit load, so there is nothing to transpose. A vertical edge's lanes
        // are eight lines, and there the four positions of a line are what is contiguous.
        bool verticalEdge = step == 1 && lineStep != 1;

        Vector128<short> p1, p0, q0, q1;
        if (verticalEdge)
        {
            LoadChromaEdgeVertical(samples, offset, lineStep, out p1, out p0, out q0, out q1);
        }
        else
        {
            p1 = LoadChroma8(samples, offset - 2 * step, lineStep);
            p0 = LoadChroma8(samples, offset - step, lineStep);
            q0 = LoadChroma8(samples, offset, lineStep);
            q1 = LoadChroma8(samples, offset + step, lineStep);
        }

        // The same three threshold tests the scalar filter makes, as a lane mask.
        Vector128<short> mask =
            Vector128.LessThan(Vector128.Abs(p0 - q0), Vector128.Create((short)alpha))
            & Vector128.LessThan(Vector128.Abs(p1 - p0), Vector128.Create((short)beta))
            & Vector128.LessThan(Vector128.Abs(q1 - q0), Vector128.Create((short)beta))
            & active;

        Vector128<short> np0, nq0;
        if (strong)
        {
            Vector128<short> two = Vector128.Create((short)2);
            np0 = Vector128.ShiftRightArithmetic(p1 + p1 + p0 + q1 + two, 2);
            nq0 = Vector128.ShiftRightArithmetic(q1 + q1 + q0 + p1 + two, 2);
        }
        else
        {
            Vector128<short> delta = Vector128.ShiftRightArithmetic(
                Vector128.ShiftLeft(q0 - p0, 2) + (p1 - q1) + Vector128.Create((short)4), 3);
            delta = Vector128.Min(Vector128.Max(delta, -tc), tc);
            np0 = ClampPixel8(p0 + delta);
            nq0 = ClampPixel8(q0 - delta);
        }

        Vector128<short> filteredP0 = Vector128.ConditionalSelect(mask, np0, p0);
        Vector128<short> filteredQ0 = Vector128.ConditionalSelect(mask, nq0, q0);

        if (verticalEdge)
        {
            StoreChromaEdgeVertical(samples, offset, lineStep, filteredP0, filteredQ0);
        }
        else
        {
            StoreChroma8(samples, offset - step, lineStep, filteredP0);
            StoreChroma8(samples, offset, lineStep, filteredQ0);
        }
    }
}

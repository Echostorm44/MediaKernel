// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// H.264/AVC integer transform DSP operations with real SIMD implementations.
/// The H.264 4x4 IDCT uses a simplified integer butterfly that differs from standard DCT.
/// The 8x8 IDCT (High Profile) uses a more complex 8-point butterfly.
///
/// The 16-bit wrapping arithmetic property of the H.264 butterfly allows all SIMD operations
/// to use 16-bit vectors (psraw, paddw, psubw) without widening to 32-bit intermediates.
/// The final >>6 shift brings results back into valid range even if intermediates wrapped.
/// </summary>
public static class H264TransformDsp
{
    // ──────────────────────────────────────────────
    //  4x4 Inverse Transform (H.264 spec 8.5.12)
    // ──────────────────────────────────────────────

    /// <summary>
    /// 4x4 inverse integer transform. Dispatches to SSE2 when available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform4x4(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        if (Sse2.IsSupported)
            InverseTransform4x4Sse2(coefficients, output);
        else
            InverseTransform4x4Scalar(coefficients, output);
    }

    /// <summary>
    /// Scalar 4x4 IDCT reference. Two-pass separable: horizontal then vertical.
    /// Rounding: +32 bias in vertical pass, >>6 normalization.
    /// </summary>
    public static void InverseTransform4x4Scalar(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];

        // Horizontal pass (rows)
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4;
            int d0 = coefficients[offset];
            int d1 = coefficients[offset + 1];
            int d2 = coefficients[offset + 2];
            int d3 = coefficients[offset + 3];

            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);

            temp[offset] = e0 + e3;
            temp[offset + 1] = e1 + e2;
            temp[offset + 2] = e1 - e2;
            temp[offset + 3] = e0 - e3;
        }

        // Vertical pass (columns) with rounding and >>6
        for (int i = 0; i < 4; i++)
        {
            int f0 = temp[i];
            int f1 = temp[i + 4];
            int f2 = temp[i + 8];
            int f3 = temp[i + 12];

            int g0 = f0 + f2;
            int g1 = f0 - f2;
            int g2 = (f1 >> 1) - f3;
            int g3 = f1 + (f3 >> 1);

            output[i] = (short)((g0 + g3 + 32) >> 6);
            output[i + 4] = (short)((g1 + g2 + 32) >> 6);
            output[i + 8] = (short)((g1 - g2 + 32) >> 6);
            output[i + 12] = (short)((g0 - g3 + 32) >> 6);
        }
    }

    /// <summary>
    /// Real SSE2 4x4 IDCT using 32-bit int intermediates for overflow safety.
    /// Algorithm matches scalar exactly: horizontal pass (rows) first, then vertical pass (columns).
    /// Uses ref-based vector loads to avoid fixed/pinning overhead in hot loops.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform4x4Sse2(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        ref short inRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(coefficients);
        ref short outRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output);

        // Load 16 shorts as two 128-bit vectors, then widen to int32
        var v01 = Vector128.LoadUnsafe(ref inRef);           // shorts 0-7 (rows 0,1)
        var v23 = Vector128.LoadUnsafe(ref inRef, (nuint)8); // shorts 8-15 (rows 2,3)

        var r0 = WidenLowToInt32(v01);   // row 0
        var r1 = WidenHighToInt32(v01);  // row 1
        var r2 = WidenLowToInt32(v23);   // row 2
        var r3 = WidenHighToInt32(v23);  // row 3

        // Transpose → horizontal butterfly → transpose → vertical butterfly → rounding → >>6
        Transpose4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Butterfly4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Transpose4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Butterfly4x4Int32(ref r0, ref r1, ref r2, ref r3);

        var rounding = Vector128.Create(32);
        r0 = Sse2.ShiftRightArithmetic(Sse2.Add(r0, rounding), 6);
        r1 = Sse2.ShiftRightArithmetic(Sse2.Add(r1, rounding), 6);
        r2 = Sse2.ShiftRightArithmetic(Sse2.Add(r2, rounding), 6);
        r3 = Sse2.ShiftRightArithmetic(Sse2.Add(r3, rounding), 6);

        // Pack int32→int16 and store
        var packed01 = Sse2.PackSignedSaturate(r0, r1);
        var packed23 = Sse2.PackSignedSaturate(r2, r3);
        packed01.StoreUnsafe(ref outRef);
        packed23.StoreUnsafe(ref outRef, (nuint)8);
    }

    /// <summary>Sign-extend the low 4 shorts of a Vector128 to 4 ints.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenLowToInt32(Vector128<short> v)
    {
        var sign = Sse2.ShiftRightArithmetic(v, 15);
        return Sse2.UnpackLow(v, sign).AsInt32();
    }

    /// <summary>
    /// H.264 4-point butterfly for 4x4 IDCT in 32-bit int precision (in-place).
    /// Operates on 4 vectors of 4 ints each.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly4x4Int32(
        ref Vector128<int> d0,
        ref Vector128<int> d1,
        ref Vector128<int> d2,
        ref Vector128<int> d3)
    {
        var e0 = Sse2.Add(d0, d2);
        var e1 = Sse2.Subtract(d0, d2);
        var e2 = Sse2.Subtract(Sse2.ShiftRightArithmetic(d1, 1), d3);
        var e3 = Sse2.Add(d1, Sse2.ShiftRightArithmetic(d3, 1));

        d0 = Sse2.Add(e0, e3);
        d1 = Sse2.Add(e1, e2);
        d2 = Sse2.Subtract(e1, e2);
        d3 = Sse2.Subtract(e0, e3);
    }

    /// <summary>
    /// Transpose a 4x4 matrix of 32-bit ints in 4 SSE2 registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4Int32(
        ref Vector128<int> r0,
        ref Vector128<int> r1,
        ref Vector128<int> r2,
        ref Vector128<int> r3)
    {
        // Level 1: interleave 32-bit pairs
        var t0 = Sse2.UnpackLow(r0, r1);   // [a0 b0 a1 b1]
        var t1 = Sse2.UnpackHigh(r0, r1);  // [a2 b2 a3 b3]
        var t2 = Sse2.UnpackLow(r2, r3);   // [c0 d0 c1 d1]
        var t3 = Sse2.UnpackHigh(r2, r3);  // [c2 d2 c3 d3]

        // Level 2: interleave 64-bit pairs
        r0 = Sse2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32();   // [a0 b0 c0 d0]
        r1 = Sse2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();  // [a1 b1 c1 d1]
        r2 = Sse2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32();   // [a2 b2 c2 d2]
        r3 = Sse2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();  // [a3 b3 c3 d3]
    }

    // ──────────────────────────────────────────────
    //  8x8 Inverse Transform (H.264 spec 8.5.12.2, High Profile)
    // ──────────────────────────────────────────────

    /// <summary>
    /// 8x8 inverse integer transform for H.264 High Profile.
    /// Dispatches to SSE2 when available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform8x8(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        if (Sse2.IsSupported)
            InverseTransform8x8Sse2(coefficients, output);
        else
            InverseTransform8x8Scalar(coefficients, output);
    }

    /// <summary>
    /// Scalar 8x8 IDCT reference. Row pass first (with DC+32 bias), then column pass with >>6.
    /// FFmpeg transposes coefficients via zigzag_scan8x8 TRANSPOSE macro, so its column-first
    /// loop structure processes original rows first — identical to our row-first approach.
    /// Int16 truncation between passes matches FFmpeg's dctcoef=int16_t for 8-bit.
    /// </summary>
    public static void InverseTransform8x8Scalar(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        Span<short> block = stackalloc short[64];
        coefficients.Slice(0, 64).CopyTo(block);

        block[0] += 32;

        // Row pass (i = row index)
        for (int i = 0; i < 8; i++)
        {
            int s0 = block[0 + i * 8];
            int s1 = block[1 + i * 8];
            int s2 = block[2 + i * 8];
            int s3 = block[3 + i * 8];
            int s4 = block[4 + i * 8];
            int s5 = block[5 + i * 8];
            int s6 = block[6 + i * 8];
            int s7 = block[7 + i * 8];

            int a0 = s0 + s4;
            int a2 = s0 - s4;
            int a4 = (s2 >> 1) - s6;
            int a6 = (s6 >> 1) + s2;

            int b0 = a0 + a6;
            int b2 = a2 + a4;
            int b4 = a2 - a4;
            int b6 = a0 - a6;

            int a1 = -s3 + s5 - s7 - (s7 >> 1);
            int a3 = s1 + s7 - s3 - (s3 >> 1);
            int a5 = -s1 + s7 + s5 + (s5 >> 1);
            int a7 = s3 + s5 + s1 + (s1 >> 1);

            int b1 = (a7 >> 2) + a1;
            int b3 = a3 + (a5 >> 2);
            int b5 = (a3 >> 2) - a5;
            int b7 = a7 - (a1 >> 2);

            // Store with int16 truncation (matches FFmpeg's dctcoef=int16_t)
            block[0 + i * 8] = (short)(b0 + b7);
            block[7 + i * 8] = (short)(b0 - b7);
            block[1 + i * 8] = (short)(b2 + b5);
            block[6 + i * 8] = (short)(b2 - b5);
            block[2 + i * 8] = (short)(b4 + b3);
            block[5 + i * 8] = (short)(b4 - b3);
            block[3 + i * 8] = (short)(b6 + b1);
            block[4 + i * 8] = (short)(b6 - b1);
        }

        // Column pass with >>6 normalization (i = column index)
        for (int i = 0; i < 8; i++)
        {
            int s0 = block[i + 0 * 8];
            int s1 = block[i + 1 * 8];
            int s2 = block[i + 2 * 8];
            int s3 = block[i + 3 * 8];
            int s4 = block[i + 4 * 8];
            int s5 = block[i + 5 * 8];
            int s6 = block[i + 6 * 8];
            int s7 = block[i + 7 * 8];

            int a0 = s0 + s4;
            int a2 = s0 - s4;
            int a4 = (s2 >> 1) - s6;
            int a6 = (s6 >> 1) + s2;

            int b0 = a0 + a6;
            int b2 = a2 + a4;
            int b4 = a2 - a4;
            int b6 = a0 - a6;

            int a1 = -s3 + s5 - s7 - (s7 >> 1);
            int a3 = s1 + s7 - s3 - (s3 >> 1);
            int a5 = -s1 + s7 + s5 + (s5 >> 1);
            int a7 = s3 + s5 + s1 + (s1 >> 1);

            int b1 = (a7 >> 2) + a1;
            int b3 = a3 + (a5 >> 2);
            int b5 = (a3 >> 2) - a5;
            int b7 = a7 - (a1 >> 2);

            output[i + 0 * 8] = (short)((b0 + b7) >> 6);
            output[i + 1 * 8] = (short)((b2 + b5) >> 6);
            output[i + 2 * 8] = (short)((b4 + b3) >> 6);
            output[i + 3 * 8] = (short)((b6 + b1) >> 6);
            output[i + 4 * 8] = (short)((b6 - b1) >> 6);
            output[i + 5 * 8] = (short)((b4 - b3) >> 6);
            output[i + 6 * 8] = (short)((b2 - b5) >> 6);
            output[i + 7 * 8] = (short)((b0 - b7) >> 6);
        }
    }

    /// <summary>
    /// SSE2 8x8 IDCT matching scalar bit-exactly. Row pass first with int16 truncation
    /// between passes (matching FFmpeg's dctcoef=int16_t), then column pass with >>6.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform8x8Sse2(ReadOnlySpan<short> coefficients, Span<short> output)
    {
        ref short inRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(coefficients);
        ref short outRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output);

        // Load 8 rows as 128-bit vectors (8 x int16 per row)
        var v0 = Vector128.LoadUnsafe(ref inRef, (nuint)(0 * 8));
        var v1 = Vector128.LoadUnsafe(ref inRef, (nuint)(1 * 8));
        var v2 = Vector128.LoadUnsafe(ref inRef, (nuint)(2 * 8));
        var v3 = Vector128.LoadUnsafe(ref inRef, (nuint)(3 * 8));
        var v4 = Vector128.LoadUnsafe(ref inRef, (nuint)(4 * 8));
        var v5 = Vector128.LoadUnsafe(ref inRef, (nuint)(5 * 8));
        var v6 = Vector128.LoadUnsafe(ref inRef, (nuint)(6 * 8));
        var v7 = Vector128.LoadUnsafe(ref inRef, (nuint)(7 * 8));

        // DC rounding bias: v0 element 0 += 32
        v0 = Sse2.Add(v0, Vector128.Create((short)32, 0, 0, 0, 0, 0, 0, 0));

        // ── ROW PASS ── Process each row independently, store as int16 (truncation)
        RowButterfly8x8Row(ref v0);
        RowButterfly8x8Row(ref v1);
        RowButterfly8x8Row(ref v2);
        RowButterfly8x8Row(ref v3);
        RowButterfly8x8Row(ref v4);
        RowButterfly8x8Row(ref v5);
        RowButterfly8x8Row(ref v6);
        RowButterfly8x8Row(ref v7);

        // v0-v7 are now int16 rows after row pass (truncated, matching scalar)

        // ── COLUMN PASS ── Widen to int32, butterfly across rows, >>6, pack back
        // Left half (columns 0-3)
        var l0 = WidenLowToInt32(v0);
        var l1 = WidenLowToInt32(v1);
        var l2 = WidenLowToInt32(v2);
        var l3 = WidenLowToInt32(v3);
        var l4 = WidenLowToInt32(v4);
        var l5 = WidenLowToInt32(v5);
        var l6 = WidenLowToInt32(v6);
        var l7 = WidenLowToInt32(v7);

        Butterfly8x8Int32(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);

        // Right half (columns 4-7)
        var r0 = WidenHighToInt32(v0);
        var r1 = WidenHighToInt32(v1);
        var r2 = WidenHighToInt32(v2);
        var r3 = WidenHighToInt32(v3);
        var r4 = WidenHighToInt32(v4);
        var r5 = WidenHighToInt32(v5);
        var r6 = WidenHighToInt32(v6);
        var r7 = WidenHighToInt32(v7);

        Butterfly8x8Int32(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // >>6 normalization (use byte immediate, not vector — vector form reads low 64-bit as shift count)
        l0 = Sse2.ShiftRightArithmetic(l0, 6); r0 = Sse2.ShiftRightArithmetic(r0, 6);
        l1 = Sse2.ShiftRightArithmetic(l1, 6); r1 = Sse2.ShiftRightArithmetic(r1, 6);
        l2 = Sse2.ShiftRightArithmetic(l2, 6); r2 = Sse2.ShiftRightArithmetic(r2, 6);
        l3 = Sse2.ShiftRightArithmetic(l3, 6); r3 = Sse2.ShiftRightArithmetic(r3, 6);
        l4 = Sse2.ShiftRightArithmetic(l4, 6); r4 = Sse2.ShiftRightArithmetic(r4, 6);
        l5 = Sse2.ShiftRightArithmetic(l5, 6); r5 = Sse2.ShiftRightArithmetic(r5, 6);
        l6 = Sse2.ShiftRightArithmetic(l6, 6); r6 = Sse2.ShiftRightArithmetic(r6, 6);
        l7 = Sse2.ShiftRightArithmetic(l7, 6); r7 = Sse2.ShiftRightArithmetic(r7, 6);

        // Pack int32→int16 and store
        Sse2.PackSignedSaturate(l0, r0).StoreUnsafe(ref outRef, (nuint)(0 * 8));
        Sse2.PackSignedSaturate(l1, r1).StoreUnsafe(ref outRef, (nuint)(1 * 8));
        Sse2.PackSignedSaturate(l2, r2).StoreUnsafe(ref outRef, (nuint)(2 * 8));
        Sse2.PackSignedSaturate(l3, r3).StoreUnsafe(ref outRef, (nuint)(3 * 8));
        Sse2.PackSignedSaturate(l4, r4).StoreUnsafe(ref outRef, (nuint)(4 * 8));
        Sse2.PackSignedSaturate(l5, r5).StoreUnsafe(ref outRef, (nuint)(5 * 8));
        Sse2.PackSignedSaturate(l6, r6).StoreUnsafe(ref outRef, (nuint)(6 * 8));
        Sse2.PackSignedSaturate(l7, r7).StoreUnsafe(ref outRef, (nuint)(7 * 8));
    }

    /// <summary>
    /// 8-point row butterfly on a single row stored as Vector128&lt;short&gt;.
    /// Extracts elements to int32, computes butterfly, packs back to int16.
    /// The int16 pack provides the truncation that matches FFmpeg's dctcoef=int16_t.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RowButterfly8x8Row(ref Vector128<short> row)
    {
        int s0 = row.GetElement(0), s1 = row.GetElement(1);
        int s2 = row.GetElement(2), s3 = row.GetElement(3);
        int s4 = row.GetElement(4), s5 = row.GetElement(5);
        int s6 = row.GetElement(6), s7 = row.GetElement(7);

        int a0 = s0 + s4;
        int a2 = s0 - s4;
        int a4 = (s2 >> 1) - s6;
        int a6 = (s6 >> 1) + s2;

        int b0 = a0 + a6;
        int b2 = a2 + a4;
        int b4 = a2 - a4;
        int b6 = a0 - a6;

        int a1 = -s3 + s5 - s7 - (s7 >> 1);
        int a3 = s1 + s7 - s3 - (s3 >> 1);
        int a5 = -s1 + s7 + s5 + (s5 >> 1);
        int a7 = s3 + s5 + s1 + (s1 >> 1);

        int b1 = (a7 >> 2) + a1;
        int b3 = a3 + (a5 >> 2);
        int b5 = (a3 >> 2) - a5;
        int b7 = a7 - (a1 >> 2);

        // Pack to int16 — truncation matches scalar's (short) cast
        row = Vector128.Create(
            (short)(b0 + b7), (short)(b2 + b5), (short)(b4 + b3), (short)(b6 + b1),
            (short)(b6 - b1), (short)(b4 - b3), (short)(b2 - b5), (short)(b0 - b7));
    }

    /// <summary>Sign-extend the high 4 shorts of a Vector128 to 4 ints.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenHighToInt32(Vector128<short> v)
    {
        var sign = Sse2.ShiftRightArithmetic(v, 15);
        return Sse2.UnpackHigh(v, sign).AsInt32();
    }

    /// <summary>
    /// H.264 8-point column butterfly in 32-bit int precision.
    /// Operates across 8 registers (rows) at each of the 4 column positions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly8x8Int32(
        ref Vector128<int> s0, ref Vector128<int> s1,
        ref Vector128<int> s2, ref Vector128<int> s3,
        ref Vector128<int> s4, ref Vector128<int> s5,
        ref Vector128<int> s6, ref Vector128<int> s7)
    {
        // Even part
        var a0 = Sse2.Add(s0, s4);
        var a2 = Sse2.Subtract(s0, s4);
        var a4 = Sse2.Subtract(Sse2.ShiftRightArithmetic(s2, 1), s6);
        var a6 = Sse2.Add(Sse2.ShiftRightArithmetic(s6, 1), s2);

        var b0 = Sse2.Add(a0, a6);
        var b2 = Sse2.Add(a2, a4);
        var b4 = Sse2.Subtract(a2, a4);
        var b6 = Sse2.Subtract(a0, a6);

        // Odd part
        var a1 = Sse2.Subtract(
            Sse2.Subtract(s5, s3),
            Sse2.Add(s7, Sse2.ShiftRightArithmetic(s7, 1)));

        var a3 = Sse2.Subtract(
            Sse2.Add(s1, s7),
            Sse2.Add(s3, Sse2.ShiftRightArithmetic(s3, 1)));

        var a5 = Sse2.Add(
            Sse2.Subtract(s7, s1),
            Sse2.Add(s5, Sse2.ShiftRightArithmetic(s5, 1)));

        var a7 = Sse2.Add(
            Sse2.Add(s3, s5),
            Sse2.Add(s1, Sse2.ShiftRightArithmetic(s1, 1)));

        var b1 = Sse2.Add(Sse2.ShiftRightArithmetic(a7, 2), a1);
        var b3 = Sse2.Add(a3, Sse2.ShiftRightArithmetic(a5, 2));
        var b5 = Sse2.Subtract(Sse2.ShiftRightArithmetic(a3, 2), a5);
        var b7 = Sse2.Subtract(a7, Sse2.ShiftRightArithmetic(a1, 2));

        s0 = Sse2.Add(b0, b7);
        s1 = Sse2.Add(b2, b5);
        s2 = Sse2.Add(b4, b3);
        s3 = Sse2.Add(b6, b1);
        s4 = Sse2.Subtract(b6, b1);
        s5 = Sse2.Subtract(b4, b3);
        s6 = Sse2.Subtract(b2, b5);
        s7 = Sse2.Subtract(b0, b7);
    }

    // ──────────────────────────────────────────────
    //  Hadamard Transforms
    // ──────────────────────────────────────────────

    /// <summary>
    /// 4x4 Hadamard inverse transform for luma DC coefficients (Intra16x16).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HadamardInverse4x4(ReadOnlySpan<short> dc, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];

        // Horizontal
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4;
            int h0 = dc[offset] + dc[offset + 2];
            int h1 = dc[offset] - dc[offset + 2];
            int h2 = dc[offset + 1] - dc[offset + 3];
            int h3 = dc[offset + 1] + dc[offset + 3];

            temp[offset] = h0 + h3;
            temp[offset + 1] = h1 + h2;
            temp[offset + 2] = h1 - h2;
            temp[offset + 3] = h0 - h3;
        }

        // Vertical (raw, no normalization — scaling done in DC dequantization)
        for (int i = 0; i < 4; i++)
        {
            int t0 = temp[i] + temp[i + 8];
            int t1 = temp[i] - temp[i + 8];
            int t2 = temp[i + 4] - temp[i + 12];
            int t3 = temp[i + 4] + temp[i + 12];

            output[i] = (short)(t0 + t3);
            output[i + 4] = (short)(t1 + t2);
            output[i + 8] = (short)(t1 - t2);
            output[i + 12] = (short)(t0 - t3);
        }
    }

    /// <summary>
    /// 2x2 Hadamard inverse for chroma DC coefficients.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HadamardInverse2x2(ReadOnlySpan<short> dc, Span<short> output)
    {
        int a = dc[0] + dc[1];
        int b = dc[0] - dc[1];
        int c = dc[2] + dc[3];
        int d = dc[2] - dc[3];

        output[0] = (short)(a + c);
        output[1] = (short)(a - c);
        output[2] = (short)(b + d);
        output[3] = (short)(b - d);
    }

    // ──────────────────────────────────────────────
    //  Wide (int32) overloads for 10-bit decode
    //  FFmpeg uses dctcoef=int32_t for bitDepth > 8
    // ──────────────────────────────────────────────

    /// <summary>
    /// 4x4 inverse transform with int input (for 10-bit dequantized coefficients).
    /// Uses SSE2 int32 path since inputs are already int32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform4x4(ReadOnlySpan<int> coefficients, Span<int> output)
    {
        if (Sse2.IsSupported)
            InverseTransform4x4Sse2Wide(coefficients, output);
        else
            InverseTransform4x4ScalarWide(coefficients, output);
    }

    /// <summary>Scalar 4x4 IDCT with int32 coefficients. Same butterfly as short version.</summary>
    public static void InverseTransform4x4ScalarWide(ReadOnlySpan<int> d, Span<int> output)
    {
        Span<int> temp = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int offset = i * 4;
            int d0 = d[offset];
            int d1 = d[offset + 1];
            int d2 = d[offset + 2];
            int d3 = d[offset + 3];

            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);

            temp[offset] = e0 + e3;
            temp[offset + 1] = e1 + e2;
            temp[offset + 2] = e1 - e2;
            temp[offset + 3] = e0 - e3;
        }

        for (int i = 0; i < 4; i++)
        {
            int f0 = temp[i];
            int f1 = temp[i + 4];
            int f2 = temp[i + 8];
            int f3 = temp[i + 12];

            int g0 = f0 + f2;
            int g1 = f0 - f2;
            int g2 = (f1 >> 1) - f3;
            int g3 = f1 + (f3 >> 1);

            output[i] = (g0 + g3 + 32) >> 6;
            output[i + 4] = (g1 + g2 + 32) >> 6;
            output[i + 8] = (g1 - g2 + 32) >> 6;
            output[i + 12] = (g0 - g3 + 32) >> 6;
        }
    }

    /// <summary>SSE2 4x4 IDCT with int32 input/output.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InverseTransform4x4Sse2Wide(ReadOnlySpan<int> coefficients, Span<int> output)
    {
        ref int inRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(coefficients);
        ref int outRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output);

        var r0 = Vector128.LoadUnsafe(ref inRef, (nuint)0);
        var r1 = Vector128.LoadUnsafe(ref inRef, (nuint)4);
        var r2 = Vector128.LoadUnsafe(ref inRef, (nuint)8);
        var r3 = Vector128.LoadUnsafe(ref inRef, (nuint)12);

        Transpose4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Butterfly4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Transpose4x4Int32(ref r0, ref r1, ref r2, ref r3);
        Butterfly4x4Int32(ref r0, ref r1, ref r2, ref r3);

        var rounding = Vector128.Create(32);
        r0 = Sse2.ShiftRightArithmetic(Sse2.Add(r0, rounding), 6);
        r1 = Sse2.ShiftRightArithmetic(Sse2.Add(r1, rounding), 6);
        r2 = Sse2.ShiftRightArithmetic(Sse2.Add(r2, rounding), 6);
        r3 = Sse2.ShiftRightArithmetic(Sse2.Add(r3, rounding), 6);

        r0.StoreUnsafe(ref outRef, (nuint)0);
        r1.StoreUnsafe(ref outRef, (nuint)4);
        r2.StoreUnsafe(ref outRef, (nuint)8);
        r3.StoreUnsafe(ref outRef, (nuint)12);
    }

    /// <summary>
    /// 8x8 inverse transform with int32 coefficients for 10-bit.
    /// Unlike the 8-bit version, NO int16 truncation between row and column passes
    /// (FFmpeg uses int32_t dctcoef for bitDepth > 8).
    /// </summary>
    public static void InverseTransform8x8Wide(ReadOnlySpan<int> coefficients, Span<int> output)
    {
        Span<int> block = stackalloc int[64];
        coefficients.Slice(0, 64).CopyTo(block);

        block[0] += 32;

        // Row pass — NO truncation (int32 throughout)
        for (int i = 0; i < 8; i++)
        {
            int s0 = block[0 + i * 8];
            int s1 = block[1 + i * 8];
            int s2 = block[2 + i * 8];
            int s3 = block[3 + i * 8];
            int s4 = block[4 + i * 8];
            int s5 = block[5 + i * 8];
            int s6 = block[6 + i * 8];
            int s7 = block[7 + i * 8];

            int a0 = s0 + s4;
            int a2 = s0 - s4;
            int a4 = (s2 >> 1) - s6;
            int a6 = (s6 >> 1) + s2;

            int b0 = a0 + a6;
            int b2 = a2 + a4;
            int b4 = a2 - a4;
            int b6 = a0 - a6;

            int a1 = -s3 + s5 - s7 - (s7 >> 1);
            int a3 = s1 + s7 - s3 - (s3 >> 1);
            int a5 = -s1 + s7 + s5 + (s5 >> 1);
            int a7 = s3 + s5 + s1 + (s1 >> 1);

            int b1 = (a7 >> 2) + a1;
            int b3 = a3 + (a5 >> 2);
            int b5 = (a3 >> 2) - a5;
            int b7 = a7 - (a1 >> 2);

            block[0 + i * 8] = b0 + b7;
            block[7 + i * 8] = b0 - b7;
            block[1 + i * 8] = b2 + b5;
            block[6 + i * 8] = b2 - b5;
            block[2 + i * 8] = b4 + b3;
            block[5 + i * 8] = b4 - b3;
            block[3 + i * 8] = b6 + b1;
            block[4 + i * 8] = b6 - b1;
        }

        // Column pass with >>6 normalization
        for (int i = 0; i < 8; i++)
        {
            int s0 = block[i + 0 * 8];
            int s1 = block[i + 1 * 8];
            int s2 = block[i + 2 * 8];
            int s3 = block[i + 3 * 8];
            int s4 = block[i + 4 * 8];
            int s5 = block[i + 5 * 8];
            int s6 = block[i + 6 * 8];
            int s7 = block[i + 7 * 8];

            int a0 = s0 + s4;
            int a2 = s0 - s4;
            int a4 = (s2 >> 1) - s6;
            int a6 = (s6 >> 1) + s2;

            int b0 = a0 + a6;
            int b2 = a2 + a4;
            int b4 = a2 - a4;
            int b6 = a0 - a6;

            int a1 = -s3 + s5 - s7 - (s7 >> 1);
            int a3 = s1 + s7 - s3 - (s3 >> 1);
            int a5 = -s1 + s7 + s5 + (s5 >> 1);
            int a7 = s3 + s5 + s1 + (s1 >> 1);

            int b1 = (a7 >> 2) + a1;
            int b3 = a3 + (a5 >> 2);
            int b5 = (a3 >> 2) - a5;
            int b7 = a7 - (a1 >> 2);

            output[i + 0 * 8] = (b0 + b7) >> 6;
            output[i + 1 * 8] = (b2 + b5) >> 6;
            output[i + 2 * 8] = (b4 + b3) >> 6;
            output[i + 3 * 8] = (b6 + b1) >> 6;
            output[i + 4 * 8] = (b6 - b1) >> 6;
            output[i + 5 * 8] = (b4 - b3) >> 6;
            output[i + 6 * 8] = (b2 - b5) >> 6;
            output[i + 7 * 8] = (b0 - b7) >> 6;
        }
    }
}

// VP9 inverse transforms (IDCT/IADST/IWHT)
// Ported from FFmpeg libavcodec/vp9dsp_template.c (8bpp)
// For 8-bit: dctcoef = short (int16_t), dctint = int

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>
/// VP9 inverse transform operations: IDCT, IADST, and IWHT at 4×4, 8×8, 16×16, and 32×32 sizes.
/// All transforms follow the 2-pass row/column decomposition from FFmpeg's vp9dsp_template.c.
/// </summary>
public static class Vp9Transform
{
    // ================================================================
    // 1D Transform Kernels
    // ================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct4_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int t0 = ((inp[0 * stride] + inp[2 * stride]) * 11585 + (1 << 13)) >> 14;
        int t1 = ((inp[0 * stride] - inp[2 * stride]) * 11585 + (1 << 13)) >> 14;
        int t2 = (inp[1 * stride] * 6270 - inp[3 * stride] * 15137 + (1 << 13)) >> 14;
        int t3 = (inp[1 * stride] * 15137 + inp[3 * stride] * 6270 + (1 << 13)) >> 14;

        output[0] = t0 + t3;
        output[1] = t1 + t2;
        output[2] = t1 - t2;
        output[3] = t0 - t3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Iadst4_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int i0 = inp[0 * stride], i1 = inp[1 * stride], i2 = inp[2 * stride], i3 = inp[3 * stride];

        int t0 = 5283 * i0 + 15212 * i2 + 9929 * i3;
        int t1 = 9929 * i0 - 5283 * i2 - 15212 * i3;
        int t2 = 13377 * (i0 - i2 + i3);
        int t3 = 13377 * i1;

        output[0] = (t0 + t3 + (1 << 13)) >> 14;
        output[1] = (t1 + t3 + (1 << 13)) >> 14;
        output[2] = (t2 + (1 << 13)) >> 14;
        output[3] = (t0 + t1 - t3 + (1 << 13)) >> 14;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct8_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int t0a = ((inp[0 * stride] + inp[4 * stride]) * 11585 + (1 << 13)) >> 14;
        int t1a = ((inp[0 * stride] - inp[4 * stride]) * 11585 + (1 << 13)) >> 14;
        int t2a = (inp[2 * stride] * 6270 - inp[6 * stride] * 15137 + (1 << 13)) >> 14;
        int t3a = (inp[2 * stride] * 15137 + inp[6 * stride] * 6270 + (1 << 13)) >> 14;
        int t4a = (inp[1 * stride] * 3196 - inp[7 * stride] * 16069 + (1 << 13)) >> 14;
        int t5a = (inp[5 * stride] * 13623 - inp[3 * stride] * 9102 + (1 << 13)) >> 14;
        int t6a = (inp[5 * stride] * 9102 + inp[3 * stride] * 13623 + (1 << 13)) >> 14;
        int t7a = (inp[1 * stride] * 16069 + inp[7 * stride] * 3196 + (1 << 13)) >> 14;

        int t0 = t0a + t3a;
        int t1 = t1a + t2a;
        int t2 = t1a - t2a;
        int t3 = t0a - t3a;
        int t4 = t4a + t5a;
        int t5a2 = t4a - t5a;
        int t7 = t7a + t6a;
        int t6a2 = t7a - t6a;

        int t5 = ((t6a2 - t5a2) * 11585 + (1 << 13)) >> 14;
        int t6 = ((t6a2 + t5a2) * 11585 + (1 << 13)) >> 14;

        output[0] = t0 + t7;
        output[1] = t1 + t6;
        output[2] = t2 + t5;
        output[3] = t3 + t4;
        output[4] = t3 - t4;
        output[5] = t2 - t5;
        output[6] = t1 - t6;
        output[7] = t0 - t7;
    }

    private static void Iadst8_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int i0 = inp[0 * stride], i1 = inp[1 * stride], i2 = inp[2 * stride], i3 = inp[3 * stride];
        int i4 = inp[4 * stride], i5 = inp[5 * stride], i6 = inp[6 * stride], i7 = inp[7 * stride];

        int t0a = 16305 * i7 + 1606 * i0;
        int t1a = 1606 * i7 - 16305 * i0;
        int t2a = 14449 * i5 + 7723 * i2;
        int t3a = 7723 * i5 - 14449 * i2;
        int t4a = 10394 * i3 + 12665 * i4;
        int t5a = 12665 * i3 - 10394 * i4;
        int t6a = 4756 * i1 + 15679 * i6;
        int t7a = 15679 * i1 - 4756 * i6;

        int t0 = (t0a + t4a + (1 << 13)) >> 14;
        int t1 = (t1a + t5a + (1 << 13)) >> 14;
        int t2 = (t2a + t6a + (1 << 13)) >> 14;
        int t3 = (t3a + t7a + (1 << 13)) >> 14;
        int t4 = (t0a - t4a + (1 << 13)) >> 14;
        int t5 = (t1a - t5a + (1 << 13)) >> 14;
        int t6 = (t2a - t6a + (1 << 13)) >> 14;
        int t7 = (t3a - t7a + (1 << 13)) >> 14;

        int t4a2 = (int)(15137U * t4 + 6270U * t5);
        int t5a2 = (int)(6270U * t4 - 15137U * t5);
        int t6a2 = (int)(15137U * t7 - 6270U * t6);
        int t7a2 = (int)(6270U * t7 + 15137U * t6);

        output[0] = t0 + t2;
        output[7] = -(t1 + t3);
        int t2b = t0 - t2;
        int t3b = t1 - t3;

        output[1] = -((int)((1U << 13) + (uint)t4a2 + (uint)t6a2) >> 14);
        output[6] = (int)((1U << 13) + (uint)t5a2 + (uint)t7a2) >> 14;
        int t6b = (int)((1U << 13) + (uint)t4a2 - (uint)t6a2) >> 14;
        int t7b = (int)((1U << 13) + (uint)t5a2 - (uint)t7a2) >> 14;

        output[3] = -((int)((uint)(t2b + t3b) * 11585U + (1 << 13)) >> 14);
        output[4] = (int)((uint)(t2b - t3b) * 11585U + (1 << 13)) >> 14;
        output[2] = (int)((uint)(t6b + t7b) * 11585U + (1 << 13)) >> 14;
        output[5] = -((int)((uint)(t6b - t7b) * 11585U + (1 << 13)) >> 14);
    }

    internal static void Idct16_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int t0a = (int)((uint)(inp[0 * stride] + inp[8 * stride]) * 11585U + (1 << 13)) >> 14;
        int t1a = (int)((uint)(inp[0 * stride] - inp[8 * stride]) * 11585U + (1 << 13)) >> 14;
        int t2a = (int)((uint)inp[4 * stride] * 6270U - (uint)inp[12 * stride] * 15137U + (1 << 13)) >> 14;
        int t3a = (int)((uint)inp[4 * stride] * 15137U + (uint)inp[12 * stride] * 6270U + (1 << 13)) >> 14;
        int t4a = (int)((uint)inp[2 * stride] * 3196U - (uint)inp[14 * stride] * 16069U + (1 << 13)) >> 14;
        int t7a = (int)((uint)inp[2 * stride] * 16069U + (uint)inp[14 * stride] * 3196U + (1 << 13)) >> 14;
        int t5a = (int)((uint)inp[10 * stride] * 13623U - (uint)inp[6 * stride] * 9102U + (1 << 13)) >> 14;
        int t6a = (int)((uint)inp[10 * stride] * 9102U + (uint)inp[6 * stride] * 13623U + (1 << 13)) >> 14;
        int t8a = (int)((uint)inp[1 * stride] * 1606U - (uint)inp[15 * stride] * 16305U + (1 << 13)) >> 14;
        int t15a = (int)((uint)inp[1 * stride] * 16305U + (uint)inp[15 * stride] * 1606U + (1 << 13)) >> 14;
        int t9a = (int)((uint)inp[9 * stride] * 12665U - (uint)inp[7 * stride] * 10394U + (1 << 13)) >> 14;
        int t14a = (int)((uint)inp[9 * stride] * 10394U + (uint)inp[7 * stride] * 12665U + (1 << 13)) >> 14;
        int t10a = (int)((uint)inp[5 * stride] * 7723U - (uint)inp[11 * stride] * 14449U + (1 << 13)) >> 14;
        int t13a = (int)((uint)inp[5 * stride] * 14449U + (uint)inp[11 * stride] * 7723U + (1 << 13)) >> 14;
        int t11a = (int)((uint)inp[13 * stride] * 15679U - (uint)inp[3 * stride] * 4756U + (1 << 13)) >> 14;
        int t12a = (int)((uint)inp[13 * stride] * 4756U + (uint)inp[3 * stride] * 15679U + (1 << 13)) >> 14;

        int t0 = t0a + t3a;
        int t1 = t1a + t2a;
        int t2 = t1a - t2a;
        int t3 = t0a - t3a;
        int t4 = t4a + t5a;
        int t5 = t4a - t5a;
        int t6 = t7a - t6a;
        int t7 = t7a + t6a;
        int t8 = t8a + t9a;
        int t9 = t8a - t9a;
        int t10 = t11a - t10a;
        int t11 = t11a + t10a;
        int t12 = t12a + t13a;
        int t13 = t12a - t13a;
        int t14 = t15a - t14a;
        int t15 = t15a + t14a;

        int t5b = (int)((uint)(t6 - t5) * 11585U + (1 << 13)) >> 14;
        int t6b = (int)((uint)(t6 + t5) * 11585U + (1 << 13)) >> 14;
        int t9b = (int)((uint)t14 * 6270U - (uint)t9 * 15137U + (1 << 13)) >> 14;
        int t14b = (int)((uint)t14 * 15137U + (uint)t9 * 6270U + (1 << 13)) >> 14;
        int t10b = (int)(-(uint)(t13 * 15137 + t10 * 6270) + (1 << 13)) >> 14;
        int t13b = (int)((uint)t13 * 6270U - (uint)t10 * 15137U + (1 << 13)) >> 14;

        int t0b = t0 + t7;
        int t1b = t1 + t6b;
        int t2b = t2 + t5b;
        int t3b = t3 + t4;
        t4 = t3 - t4;
        t5 = t2 - t5b;
        t6 = t1 - t6b;
        t7 = t0 - t7;
        int t8b = t8 + t11;
        t9 = t9b + t10b;
        t10 = t9b - t10b;
        int t11b = t8 - t11;
        int t12b = t15 - t12;
        t13 = t14b - t13b;
        t14 = t14b + t13b;
        int t15b = t15 + t12;

        int t10c = (int)((uint)(t13 - t10) * 11585U + (1 << 13)) >> 14;
        int t13c = (int)((uint)(t13 + t10) * 11585U + (1 << 13)) >> 14;
        t11 = (int)((uint)(t12b - t11b) * 11585U + (1 << 13)) >> 14;
        t12 = (int)((uint)(t12b + t11b) * 11585U + (1 << 13)) >> 14;

        output[0] = t0b + t15b;
        output[1] = t1b + t14;
        output[2] = t2b + t13c;
        output[3] = t3b + t12;
        output[4] = t4 + t11;
        output[5] = t5 + t10c;
        output[6] = t6 + t9;
        output[7] = t7 + t8b;
        output[8] = t7 - t8b;
        output[9] = t6 - t9;
        output[10] = t5 - t10c;
        output[11] = t4 - t11;
        output[12] = t3b - t12;
        output[13] = t2b - t13c;
        output[14] = t1b - t14;
        output[15] = t0b - t15b;
    }

    private static void Iadst16_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int i0 = inp[0 * stride], i1 = inp[1 * stride], i2 = inp[2 * stride], i3 = inp[3 * stride];
        int i4 = inp[4 * stride], i5 = inp[5 * stride], i6 = inp[6 * stride], i7 = inp[7 * stride];
        int i8 = inp[8 * stride], i9 = inp[9 * stride], i10 = inp[10 * stride], i11 = inp[11 * stride];
        int i12 = inp[12 * stride], i13 = inp[13 * stride], i14 = inp[14 * stride], i15 = inp[15 * stride];

        int t0 = i15 * 16364 + i0 * 804;
        int t1 = i15 * 804 - i0 * 16364;
        int t2 = i13 * 15893 + i2 * 3981;
        int t3 = i13 * 3981 - i2 * 15893;
        int t4 = i11 * 14811 + i4 * 7005;
        int t5 = i11 * 7005 - i4 * 14811;
        int t6 = i9 * 13160 + i6 * 9760;
        int t7 = i9 * 9760 - i6 * 13160;
        int t8 = i7 * 11003 + i8 * 12140;
        int t9 = i7 * 12140 - i8 * 11003;
        int t10 = i5 * 8423 + i10 * 14053;
        int t11 = i5 * 14053 - i10 * 8423;
        int t12 = i3 * 5520 + i12 * 15426;
        int t13 = i3 * 15426 - i12 * 5520;
        int t14 = i1 * 2404 + i14 * 16207;
        int t15 = i1 * 16207 - i14 * 2404;

        int t0a = (int)((1U << 13) + (uint)(t0 + t8)) >> 14;
        int t1a = (int)((1U << 13) + (uint)(t1 + t9)) >> 14;
        int t2a = (int)((1U << 13) + (uint)(t2 + t10)) >> 14;
        int t3a = (int)((1U << 13) + (uint)(t3 + t11)) >> 14;
        int t4a = (int)((1U << 13) + (uint)(t4 + t12)) >> 14;
        int t5a = (int)((1U << 13) + (uint)(t5 + t13)) >> 14;
        int t6a = (int)((1U << 13) + (uint)(t6 + t14)) >> 14;
        int t7a = (int)((1U << 13) + (uint)(t7 + t15)) >> 14;
        int t8a = (int)((1U << 13) + (uint)(t0 - t8)) >> 14;
        int t9a = (int)((1U << 13) + (uint)(t1 - t9)) >> 14;
        int t10a = (int)((1U << 13) + (uint)(t2 - t10)) >> 14;
        int t11a = (int)((1U << 13) + (uint)(t3 - t11)) >> 14;
        int t12a = (int)((1U << 13) + (uint)(t4 - t12)) >> 14;
        int t13a = (int)((1U << 13) + (uint)(t5 - t13)) >> 14;
        int t14a = (int)((1U << 13) + (uint)(t6 - t14)) >> 14;
        int t15a = (int)((1U << 13) + (uint)(t7 - t15)) >> 14;

        t8 = (int)(t8a * 16069U + t9a * 3196U);
        t9 = (int)(t8a * 3196U - t9a * 16069U);
        t10 = (int)(t10a * 9102U + t11a * 13623U);
        t11 = (int)(t10a * 13623U - t11a * 9102U);
        t12 = (int)(t13a * 16069U - t12a * 3196U);
        t13 = (int)(t13a * 3196U + t12a * 16069U);
        t14 = (int)(t15a * 9102U - t14a * 13623U);
        t15 = (int)(t15a * 13623U + t14a * 9102U);

        t0 = t0a + t4a;
        t1 = t1a + t5a;
        t2 = t2a + t6a;
        t3 = t3a + t7a;
        t4 = (int)((uint)t0a - (uint)t4a);
        t5 = (int)((uint)t1a - (uint)t5a);
        t6 = (int)((uint)t2a - (uint)t6a);
        t7 = (int)((uint)t3a - (uint)t7a);
        t8a = (int)((1U << 13) + (uint)(t8 + t12)) >> 14;
        t9a = (int)((1U << 13) + (uint)(t9 + t13)) >> 14;
        t10a = (int)((1U << 13) + (uint)(t10 + t14)) >> 14;
        t11a = (int)((1U << 13) + (uint)(t11 + t15)) >> 14;
        t12a = (int)((1U << 13) + (uint)(t8 - t12)) >> 14;
        t13a = (int)((1U << 13) + (uint)(t9 - t13)) >> 14;
        t14a = (int)((1U << 13) + (uint)(t10 - t14)) >> 14;
        t15a = (int)((1U << 13) + (uint)(t11 - t15)) >> 14;

        int t4a2 = (int)(t4 * 15137U + t5 * 6270U);
        int t5a2 = (int)(t4 * 6270U - t5 * 15137U);
        int t6a2 = (int)(t7 * 15137U - t6 * 6270U);
        int t7a2 = (int)(t7 * 6270U + t6 * 15137U);
        t12 = (int)(t12a * 15137U + t13a * 6270U);
        t13 = (int)(t12a * 6270U - t13a * 15137U);
        t14 = (int)(t15a * 15137U - t14a * 6270U);
        t15 = (int)(t15a * 6270U + t14a * 15137U);

        output[0] = t0 + t2;
        output[15] = -(t1 + t3);
        int t2b = t0 - t2;
        int t3b = t1 - t3;
        output[3] = -((int)((1U << 13) + (uint)t4a2 + (uint)t6a2) >> 14);
        output[12] = (int)((1U << 13) + (uint)t5a2 + (uint)t7a2) >> 14;
        t6 = (int)((1U << 13) + (uint)t4a2 - (uint)t6a2) >> 14;
        t7 = (int)((1U << 13) + (uint)t5a2 - (uint)t7a2) >> 14;
        output[1] = -(t8a + t10a);
        output[14] = t9a + t11a;
        t10 = t8a - t10a;
        t11 = t9a - t11a;
        output[2] = (int)((1U << 13) + (uint)(t12 + t14)) >> 14;
        output[13] = -((int)((1U << 13) + (uint)(t13 + t15)) >> 14);
        int t14b = (int)((1U << 13) + (uint)(t12 - t14)) >> 14;
        int t15b = (int)((1U << 13) + (uint)(t13 - t15)) >> 14;

        output[7] = (int)((uint)(-(t2b + t3b)) * 11585U + (1 << 13)) >> 14;
        output[8] = (int)((uint)(t2b - t3b) * 11585U + (1 << 13)) >> 14;
        output[4] = (int)((uint)(t7 + t6) * 11585U + (1 << 13)) >> 14;
        output[11] = (int)((uint)(t7 - t6) * 11585U + (1 << 13)) >> 14;
        output[6] = (int)((uint)(t11 + t10) * 11585U + (1 << 13)) >> 14;
        output[9] = (int)((uint)(t11 - t10) * 11585U + (1 << 13)) >> 14;
        output[5] = (int)((uint)(-(t14b + t15b)) * 11585U + (1 << 13)) >> 14;
        output[10] = (int)((uint)(t14b - t15b) * 11585U + (1 << 13)) >> 14;
    }

    internal static void Idct32_1d(ReadOnlySpan<short> inp, int stride, Span<int> output)
    {
        int t0a = (int)((uint)(inp[0 * stride] + inp[16 * stride]) * 11585U + (1 << 13)) >> 14;
        int t1a = (int)((uint)(inp[0 * stride] - inp[16 * stride]) * 11585U + (1 << 13)) >> 14;
        int t2a = (int)((uint)inp[8 * stride] * 6270U - (uint)inp[24 * stride] * 15137U + (1 << 13)) >> 14;
        int t3a = (int)((uint)inp[8 * stride] * 15137U + (uint)inp[24 * stride] * 6270U + (1 << 13)) >> 14;
        int t4a = (int)((uint)inp[4 * stride] * 3196U - (uint)inp[28 * stride] * 16069U + (1 << 13)) >> 14;
        int t7a = (int)((uint)inp[4 * stride] * 16069U + (uint)inp[28 * stride] * 3196U + (1 << 13)) >> 14;
        int t5a = (int)((uint)inp[20 * stride] * 13623U - (uint)inp[12 * stride] * 9102U + (1 << 13)) >> 14;
        int t6a = (int)((uint)inp[20 * stride] * 9102U + (uint)inp[12 * stride] * 13623U + (1 << 13)) >> 14;
        int t8a = (int)((uint)inp[2 * stride] * 1606U - (uint)inp[30 * stride] * 16305U + (1 << 13)) >> 14;
        int t15a = (int)((uint)inp[2 * stride] * 16305U + (uint)inp[30 * stride] * 1606U + (1 << 13)) >> 14;
        int t9a = (int)((uint)inp[18 * stride] * 12665U - (uint)inp[14 * stride] * 10394U + (1 << 13)) >> 14;
        int t14a = (int)((uint)inp[18 * stride] * 10394U + (uint)inp[14 * stride] * 12665U + (1 << 13)) >> 14;
        int t10a = (int)((uint)inp[10 * stride] * 7723U - (uint)inp[22 * stride] * 14449U + (1 << 13)) >> 14;
        int t13a = (int)((uint)inp[10 * stride] * 14449U + (uint)inp[22 * stride] * 7723U + (1 << 13)) >> 14;
        int t11a = (int)((uint)inp[26 * stride] * 15679U - (uint)inp[6 * stride] * 4756U + (1 << 13)) >> 14;
        int t12a = (int)((uint)inp[26 * stride] * 4756U + (uint)inp[6 * stride] * 15679U + (1 << 13)) >> 14;
        int t16a = (int)((uint)inp[1 * stride] * 804U - (uint)inp[31 * stride] * 16364U + (1 << 13)) >> 14;
        int t31a = (int)((uint)inp[1 * stride] * 16364U + (uint)inp[31 * stride] * 804U + (1 << 13)) >> 14;
        int t17a = (int)((uint)inp[17 * stride] * 12140U - (uint)inp[15 * stride] * 11003U + (1 << 13)) >> 14;
        int t30a = (int)((uint)inp[17 * stride] * 11003U + (uint)inp[15 * stride] * 12140U + (1 << 13)) >> 14;
        int t18a = (int)((uint)inp[9 * stride] * 7005U - (uint)inp[23 * stride] * 14811U + (1 << 13)) >> 14;
        int t29a = (int)((uint)inp[9 * stride] * 14811U + (uint)inp[23 * stride] * 7005U + (1 << 13)) >> 14;
        int t19a = (int)((uint)inp[25 * stride] * 15426U - (uint)inp[7 * stride] * 5520U + (1 << 13)) >> 14;
        int t28a = (int)((uint)inp[25 * stride] * 5520U + (uint)inp[7 * stride] * 15426U + (1 << 13)) >> 14;
        int t20a = (int)((uint)inp[5 * stride] * 3981U - (uint)inp[27 * stride] * 15893U + (1 << 13)) >> 14;
        int t27a = (int)((uint)inp[5 * stride] * 15893U + (uint)inp[27 * stride] * 3981U + (1 << 13)) >> 14;
        int t21a = (int)((uint)inp[21 * stride] * 14053U - (uint)inp[11 * stride] * 8423U + (1 << 13)) >> 14;
        int t26a = (int)((uint)inp[21 * stride] * 8423U + (uint)inp[11 * stride] * 14053U + (1 << 13)) >> 14;
        int t22a = (int)((uint)inp[13 * stride] * 9760U - (uint)inp[19 * stride] * 13160U + (1 << 13)) >> 14;
        int t25a = (int)((uint)inp[13 * stride] * 13160U + (uint)inp[19 * stride] * 9760U + (1 << 13)) >> 14;
        int t23a = (int)((uint)inp[29 * stride] * 16207U - (uint)inp[3 * stride] * 2404U + (1 << 13)) >> 14;
        int t24a = (int)((uint)inp[29 * stride] * 2404U + (uint)inp[3 * stride] * 16207U + (1 << 13)) >> 14;

        // Stage 2
        int t0 = t0a + t3a, t1 = t1a + t2a, t2 = t1a - t2a, t3 = t0a - t3a;
        int t4 = t4a + t5a, t5 = t4a - t5a, t6 = t7a - t6a, t7 = t7a + t6a;
        int t8 = t8a + t9a, t9 = t8a - t9a, t10 = t11a - t10a, t11 = t11a + t10a;
        int t12 = t12a + t13a, t13 = t12a - t13a, t14 = t15a - t14a, t15 = t15a + t14a;
        int t16 = t16a + t17a, t17 = t16a - t17a, t18 = t19a - t18a, t19 = t19a + t18a;
        int t20 = t20a + t21a, t21 = t20a - t21a, t22 = t23a - t22a, t23 = t23a + t22a;
        int t24 = t24a + t25a, t25 = t24a - t25a, t26 = t27a - t26a, t27 = t27a + t26a;
        int t28 = t28a + t29a, t29 = t28a - t29a, t30 = t31a - t30a, t31 = t31a + t30a;

        // Stage 3
        int t5b = (int)((uint)(t6 - t5) * 11585U + (1 << 13)) >> 14;
        int t6b = (int)((uint)(t6 + t5) * 11585U + (1 << 13)) >> 14;
        int t9b = (int)((uint)t14 * 6270U - (uint)t9 * 15137U + (1 << 13)) >> 14;
        int t14b = (int)((uint)t14 * 15137U + (uint)t9 * 6270U + (1 << 13)) >> 14;
        int t10b = (int)(-(uint)(t13 * 15137 + t10 * 6270) + (1 << 13)) >> 14;
        int t13b = (int)((uint)t13 * 6270U - (uint)t10 * 15137U + (1 << 13)) >> 14;
        int t17b = (int)((uint)t30 * 3196U - (uint)t17 * 16069U + (1 << 13)) >> 14;
        int t30b = (int)((uint)t30 * 16069U + (uint)t17 * 3196U + (1 << 13)) >> 14;
        int t18b = (int)(-(uint)(t29 * 16069 + t18 * 3196) + (1 << 13)) >> 14;
        int t29b = (int)((uint)t29 * 3196U - (uint)t18 * 16069U + (1 << 13)) >> 14;
        int t21b = (int)((uint)t26 * 13623U - (uint)t21 * 9102U + (1 << 13)) >> 14;
        int t26b = (int)((uint)t26 * 9102U + (uint)t21 * 13623U + (1 << 13)) >> 14;
        int t22b = (int)(-(uint)(t25 * 9102 + t22 * 13623) + (1 << 13)) >> 14;
        int t25b = (int)((uint)t25 * 13623U - (uint)t22 * 9102U + (1 << 13)) >> 14;

        // Stage 4
        int t0c = t0 + t7, t1c = t1 + t6b, t2c = t2 + t5b, t3c = t3 + t4;
        int t4c = t3 - t4;
        t5 = t2 - t5b;
        t6 = t1 - t6b;
        int t7c = t0 - t7;
        int t8c = t8 + t11, t9c = t9b + t10b, t10c = t9b - t10b, t11c = t8 - t11;
        int t12c = t15 - t12, t13c = t14b - t13b, t14c = t14b + t13b, t15c = t15 + t12;
        int t16c = t16 + t19, t17c = t17b + t18b, t18c = t17b - t18b, t19c = t16 - t19;
        int t20c = t23 - t20, t21c = t22b - t21b, t22c = t22b + t21b, t23c = t23 + t20;
        int t24c = t24 + t27, t25c = t25b + t26b, t26c = t25b - t26b, t27c = t24 - t27;
        int t28c = t31 - t28, t29c = t30b - t29b, t30c = t30b + t29b, t31c = t31 + t28;

        // Stage 5
        int t10d = (int)((uint)(t13c - t10c) * 11585U + (1 << 13)) >> 14;
        int t13d = (int)((uint)(t13c + t10c) * 11585U + (1 << 13)) >> 14;
        t11 = (int)((uint)(t12c - t11c) * 11585U + (1 << 13)) >> 14;
        t12 = (int)((uint)(t12c + t11c) * 11585U + (1 << 13)) >> 14;
        int t18d = (int)((uint)t29c * 6270U - (uint)t18c * 15137U + (1 << 13)) >> 14;
        int t29d = (int)((uint)t29c * 15137U + (uint)t18c * 6270U + (1 << 13)) >> 14;
        t19 = (int)((uint)t28c * 6270U - (uint)t19c * 15137U + (1 << 13)) >> 14;
        t28 = (int)((uint)t28c * 15137U + (uint)t19c * 6270U + (1 << 13)) >> 14;
        t20 = (int)(-(uint)(t27c * 15137 + t20c * 6270) + (1 << 13)) >> 14;
        t27 = (int)((uint)t27c * 6270U - (uint)t20c * 15137U + (1 << 13)) >> 14;
        int t21d = (int)(-(uint)(t26c * 15137 + t21c * 6270) + (1 << 13)) >> 14;
        int t26d = (int)((uint)t26c * 6270U - (uint)t21c * 15137U + (1 << 13)) >> 14;

        // Stage 6
        t0 = t0c + t15c; t1 = t1c + t14c; t2 = t2c + t13d; t3 = t3c + t12;
        t4 = t4c + t11; t5a = t5 + t10d; t6a = t6 + t9c; t7 = t7c + t8c;
        t8 = t7c - t8c; int t9e = t6 - t9c; t10 = t5 - t10d; t11a = t4c - t11;
        t12a = t3c - t12; t13 = t2c - t13d; t14a = t1c - t14c; t15 = t0c - t15c;
        t16 = t16c + t23c; t17a = t17c + t22c; t18 = t18d + t21d; t19a = t19 + t20;
        int t20b = t19 - t20; t21 = t18d - t21d; int t22d = t17c - t22c; t23 = t16c - t23c;
        t24 = t31c - t24c; t25a = t30c - t25c; t26 = t29d - t26d; t27a = t28 - t27;
        int t28b = t28 + t27; t29 = t29d + t26d; t30a = t30c + t25c; t31 = t31c + t24c;

        // Stage 7
        t20 = (int)((uint)(t27a - t20b) * 11585U + (1 << 13)) >> 14;
        t27 = (int)((uint)(t27a + t20b) * 11585U + (1 << 13)) >> 14;
        t21a = (int)((uint)(t26 - t21) * 11585U + (1 << 13)) >> 14;
        t26a = (int)((uint)(t26 + t21) * 11585U + (1 << 13)) >> 14;
        t22 = (int)((uint)(t25a - t22d) * 11585U + (1 << 13)) >> 14;
        t25 = (int)((uint)(t25a + t22d) * 11585U + (1 << 13)) >> 14;
        t23a = (int)((uint)(t24 - t23) * 11585U + (1 << 13)) >> 14;
        t24a = (int)((uint)(t24 + t23) * 11585U + (1 << 13)) >> 14;

        // Output
        output[0] = t0 + t31;
        output[1] = t1 + t30a;
        output[2] = t2 + t29;
        output[3] = t3 + t28b;
        output[4] = t4 + t27;
        output[5] = t5a + t26a;
        output[6] = t6a + t25;
        output[7] = t7 + t24a;
        output[8] = t8 + t23a;
        output[9] = t9e + t22;
        output[10] = t10 + t21a;
        output[11] = t11a + t20;
        output[12] = t12a + t19a;
        output[13] = t13 + t18;
        output[14] = t14a + t17a;
        output[15] = t15 + t16;
        output[16] = t15 - t16;
        output[17] = t14a - t17a;
        output[18] = t13 - t18;
        output[19] = t12a - t19a;
        output[20] = t11a - t20;
        output[21] = t10 - t21a;
        output[22] = t9e - t22;
        output[23] = t8 - t23a;
        output[24] = t7 - t24a;
        output[25] = t6a - t25;
        output[26] = t5a - t26a;
        output[27] = t4 - t27;
        output[28] = t3 - t28b;
        output[29] = t2 - t29;
        output[30] = t1 - t30a;
        output[31] = t0 - t31;
    }

    private static void Iwht4_1d(ReadOnlySpan<short> inp, int stride, Span<int> output, int pass)
    {
        int t0, t1, t2, t3;

        if (pass == 0)
        {
            t0 = inp[0 * stride] >> 2;
            t1 = inp[3 * stride] >> 2;
            t2 = inp[1 * stride] >> 2;
            t3 = inp[2 * stride] >> 2;
        }
        else
        {
            t0 = inp[0 * stride];
            t1 = inp[3 * stride];
            t2 = inp[1 * stride];
            t3 = inp[2 * stride];
        }

        t0 += t2;
        t3 -= t1;
        int t4 = (t0 - t3) >> 1;
        t1 = t4 - t1;
        t2 = t4 - t2;
        t0 -= t1;
        t3 += t2;

        output[0] = t0;
        output[1] = t1;
        output[2] = t2;
        output[3] = t3;
    }

    // ================================================================
    // 2D Transform Wrappers (itxfm_wrapper macro equivalent)
    // ================================================================

    /// <summary>Delegate type for 1D transform functions.</summary>
    private delegate void Transform1d(ReadOnlySpan<short> inp, int stride, Span<int> output);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int val)
    {
        return (byte)Math.Clamp(val, 0, 255);
    }

    /// <summary>
    /// Generic 2D inverse transform: row pass (type_a) then column pass (type_b), add to destination.
    /// Port of itxfm_wrapper macro from vp9dsp_template.c.
    /// </summary>
    private static void InverseTransform2d(
        Span<byte> dst, int stride, Span<short> block, int eob, int sz, int bits,
        bool hasDcOnly, Transform1d rowTransform, Transform1d colTransform)
    {
        if (hasDcOnly && eob == 1)
        {
            int t = ((block[0] * 11585 + (1 << 13)) >> 14) * 11585;
            t = (t + (1 << 13)) >> 14;
            block[0] = 0;
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++)
                {
                    int val = bits != 0 ? (int)(t + (1U << (bits - 1))) >> bits : t;
                    dst[j * stride] = ClipPixel(dst[j * stride] + val);
                }
                dst = dst[1..];
            }
            return;
        }

        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];

        // Row pass: transform each row
        for (int i = 0; i < sz; i++)
        {
            rowTransform(block[(i)..], sz, tmp[(i * sz)..]);
        }

        // Zero out the input block
        block[..(sz * sz)].Clear();

        // Column pass: transform each column and add to destination
        for (int i = 0; i < sz; i++)
        {
            // Create a temporary span for column input: tmp[i], tmp[i+sz], tmp[i+2*sz], ...
            // We need to convert int[] column to short[] for the transform, but our 1D transforms
            // take ReadOnlySpan<short>. For the second pass, we need int->short conversion.
            // Actually, the FFmpeg code uses the same 1d function for both passes with dctcoef arrays.
            // For the second pass, tmp is dctcoef (short), but our tmp is int.
            // We need a different approach for the second pass.
            colTransform(ConvertColumn(tmp, i, sz, output), sz, output);

            for (int j = 0; j < sz; j++)
            {
                int val = bits != 0 ? (int)(output[j] + (1U << (bits - 1))) >> bits : output[j];
                dst[j * stride] = ClipPixel(dst[j * stride] + val);
            }
            dst = dst[1..];
        }
    }

    /// <summary>
    /// Helper to convert an int column to short span for the second transform pass.
    /// FFmpeg's code uses dctcoef (short) for both passes; we use int for intermediate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<short> ConvertColumn(Span<int> tmp, int col, int sz, Span<int> scratch)
    {
        // This won't work as-is because our 1D transforms take ReadOnlySpan<short> but intermediate is int.
        // We need to rethink the architecture.
        // For the second pass in FFmpeg, the intermediate values are stored as dctcoef (short for 8bpp).
        // So after the first pass, values are already back in short range.
        // Let's just do the column extraction and cast.
        Span<short> col_data = stackalloc short[sz];
        for (int j = 0; j < sz; j++)
            col_data[j] = (short)tmp[j * sz + col];
        // Can't return stackalloc... We need to restructure.
        // Actually let's just inline the 2D transforms instead of using delegates.
        throw new NotImplementedException("Use specific typed methods instead");
    }

    // ================================================================
    // Direct 2D Transform Methods (avoiding delegate/stackalloc issues)
    // ================================================================

    /// <summary>4×4 IDCT-IDCT inverse transform, add to destination. bits=4.</summary>
    public static void IdctIdct4x4Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 4;
        const int bits = 4;

        if (eob == 1) // DC-only fast path
        {
            int t = ((block[0] * 11585 + (1 << 13)) >> 14);
            t = (t * 11585 + (1 << 13)) >> 14;
            block[0] = 0;
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++)
                    dst[j * stride] = ClipPixel(dst[j * stride] + ((t + (1 << (bits - 1))) >> bits));
                dst = dst[1..];
            }
            return;
        }

        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];

        // Row pass
        for (int i = 0; i < sz; i++)
            Idct4_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();

        // Column pass — truncate output to short (FFmpeg uses dctcoef/int16_t for out[])
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct4_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    /// <summary>4×4 IADST-IDCT inverse transform, add to destination.</summary>
    public static void IadstIdct4x4Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 4, bits = 4;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];

        for (int i = 0; i < sz; i++) Iadst4_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct4_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    /// <summary>4×4 IDCT-IADST inverse transform, add to destination.</summary>
    public static void IdctIadst4x4Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 4, bits = 4;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];

        for (int i = 0; i < sz; i++) Idct4_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst4_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    /// <summary>4×4 IADST-IADST inverse transform, add to destination.</summary>
    public static void IadstIadst4x4Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 4, bits = 4;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];

        for (int i = 0; i < sz; i++) Iadst4_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst4_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    // 8×8 transforms

    /// <summary>8×8 IDCT-IDCT inverse transform, add to destination. bits=5.</summary>
    public static void IdctIdct8x8Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 8, bits = 5;
        if (eob == 1)
        {
            int t = ((block[0] * 11585 + (1 << 13)) >> 14);
            t = (t * 11585 + (1 << 13)) >> 14;
            block[0] = 0;
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++)
                    dst[j * stride] = ClipPixel(dst[j * stride] + ((t + (1 << (bits - 1))) >> bits));
                dst = dst[1..];
            }
            return;
        }
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Idct8_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct8_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IadstIdct8x8Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 8, bits = 5;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Iadst8_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct8_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IdctIadst8x8Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 8, bits = 5;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Idct8_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst8_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IadstIadst8x8Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 8, bits = 5;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Iadst8_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst8_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    // 16×16 transforms

    public static void IdctIdct16x16Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 16, bits = 6;
        if (eob == 1)
        {
            int t = ((block[0] * 11585 + (1 << 13)) >> 14);
            t = (t * 11585 + (1 << 13)) >> 14;
            block[0] = 0;
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++)
                    dst[j * stride] = ClipPixel(dst[j * stride] + ((t + (1 << (bits - 1))) >> bits));
                dst = dst[1..];
            }
            return;
        }
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Idct16_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct16_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IadstIdct16x16Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 16, bits = 6;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Iadst16_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Idct16_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IdctIadst16x16Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 16, bits = 6;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Idct16_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst16_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    public static void IadstIadst16x16Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 16, bits = 6;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];
        for (int i = 0; i < sz; i++) Iadst16_1d(block[(i)..], sz, tmp[(i * sz)..]);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iadst16_1d(colBuf, 1, output);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
            dst = dst[1..];
        }
    }

    // 32×32 transform (only IDCT-IDCT, no ADST for 32×32)

    /// <summary>Diagnostic: when set, the next IdctIdct32x32Add call captures its input coefficients here.</summary>
    public static short[]? DiagCapturedCoefs;
    /// <summary>Diagnostic: when set, captures the dst prediction pixels before IDCT add.</summary>
    public static byte[]? DiagCapturedPrediction;
    /// <summary>Diagnostic: captured stride value.</summary>
    public static int DiagCapturedStride;
    /// <summary>Set to true to trigger capture on next call, automatically resets to false.</summary>
    public static bool DiagCaptureNext;
    /// <summary>When true, dumps column 31 details for the next 32x32 IDCT call.</summary>
    public static bool DiagCaptureNext32x32;

    public static void IdctIdct32x32Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 32, bits = 6;

        // Diagnostic coefficient capture
        if (DiagCaptureNext)
        {
            DiagCaptureNext = false;
            DiagCapturedCoefs = block[..(sz * sz)].ToArray();
            DiagCapturedStride = stride;
            DiagCapturedPrediction = new byte[sz * sz];
            for (int r = 0; r < sz; r++)
                for (int c = 0; c < sz; c++)
                    DiagCapturedPrediction[r * sz + c] = dst[r * stride + c];
        }
        if (eob == 1)
        {
            int t = ((block[0] * 11585 + (1 << 13)) >> 14);
            t = (t * 11585 + (1 << 13)) >> 14;
            block[0] = 0;
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++)
                    dst[j * stride] = ClipPixel(dst[j * stride] + ((t + (1 << (bits - 1))) >> bits));
                dst = dst[1..];
            }
            return;
        }

        // 32×32 = 1024 ints for tmp — too large for stackalloc, use array pool
        int[] tmpArr = System.Buffers.ArrayPool<int>.Shared.Rent(sz * sz);
        try
        {
            Span<int> tmp = tmpArr.AsSpan(0, sz * sz);
            Span<int> output = stackalloc int[sz];
            Span<short> colBuf = stackalloc short[sz];

            for (int i = 0; i < sz; i++) Idct32_1d(block[(i)..], sz, tmp[(i * sz)..]);
            block[..(sz * sz)].Clear();
            for (int i = 0; i < sz; i++)
            {
                for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
                Idct32_1d(colBuf, 1, output);
                if (DiagCaptureNext32x32 && i == 31)
                {
                    DiagCaptureNext32x32 = false;
                    Console.Write($"  IDCT32 col31 tmp_int_FULL: ");
                    for (int k = 0; k < sz; k++) Console.Write($"{tmp[k * sz + i]} ");
                    Console.WriteLine();
                    // Check for int16 overflow
                    for (int k = 0; k < sz; k++)
                    {
                        int v = tmp[k * sz + i];
                        if (v < -32768 || v > 32767)
                            Console.WriteLine($"  *** OVERFLOW at tmp[{k}*32+31] = {v} (short={(short)v})");
                    }
                    Console.WriteLine($"  IDCT32 col31: output[0]={output[0]} short={(short)output[0]} residual={((short)output[0] + 32) >> 6}");
                    Console.WriteLine($"  IDCT32 col31: dst[0]={dst[0]} final={ClipPixel(dst[0] + (((short)output[0] + (1 << (bits - 1))) >> bits))}");
                }
                for (int j = 0; j < sz; j++)
                    dst[j * stride] = ClipPixel(dst[j * stride] + (((short)output[j] + (1 << (bits - 1))) >> bits));
                dst = dst[1..];
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(tmpArr);
        }
    }

    // 4×4 IWHT

    public static void IwhtIwht4x4Add(Span<byte> dst, int stride, Span<short> block, int eob)
    {
        const int sz = 4, bits = 0;
        Span<int> tmp = stackalloc int[sz * sz];
        Span<int> output = stackalloc int[sz];
        Span<short> colBuf = stackalloc short[sz];

        for (int i = 0; i < sz; i++) Iwht4_1d(block[(i)..], sz, tmp[(i * sz)..], 0);
        block[..(sz * sz)].Clear();
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++) colBuf[j] = (short)tmp[j * sz + i];
            Iwht4_1d(colBuf, 1, output, 1);
            for (int j = 0; j < sz; j++)
                dst[j * stride] = ClipPixel(dst[j * stride] + (short)output[j]);
            dst = dst[1..];
        }
    }

    // ================================================================
    // Dispatch table indexed by [TxSize][TxType]
    // TxType: 0=DCT_DCT, 1=ADST_DCT, 2=DCT_ADST, 3=ADST_ADST (matches FFmpeg TxfmType enum)
    // ================================================================

    /// <summary>
    /// Delegate for inverse transform + add operation.
    /// </summary>
    public delegate void ItxfmAddFunc(Span<byte> dst, int stride, Span<short> block, int eob);

    /// <summary>
    /// Dispatch table: ItxfmAdd[txSize][txType].
    /// txSize: 0=4×4, 1=8×8, 2=16×16, 3=32×32, 4=lossless.
    /// txType: 0=DCT_DCT, 1=DCT_ADST(→iadst_idct), 2=ADST_DCT(→idct_iadst), 3=ADST_ADST.
    /// Note: indices 1/2 are swapped vs FFmpeg's enum, matching our IntraTxfmType convention.
    /// </summary>
    public static readonly ItxfmAddFunc[][] ItxfmAdd = new ItxfmAddFunc[][]
    {
        // TX_4X4  — [0]=DCT_DCT, [1]=DCT_ADST→iadst_idct, [2]=ADST_DCT→idct_iadst, [3]=ADST_ADST
        new ItxfmAddFunc[] { IdctIdct4x4Add, IadstIdct4x4Add, IdctIadst4x4Add, IadstIadst4x4Add },
        // TX_8X8
        new ItxfmAddFunc[] { IdctIdct8x8Add, IadstIdct8x8Add, IdctIadst8x8Add, IadstIadst8x8Add },
        // TX_16X16
        new ItxfmAddFunc[] { IdctIdct16x16Add, IadstIdct16x16Add, IdctIadst16x16Add, IadstIadst16x16Add },
        // TX_32X32 (only DCT_DCT)
        new ItxfmAddFunc[] { IdctIdct32x32Add, IdctIdct32x32Add, IdctIdct32x32Add, IdctIdct32x32Add },
        // Lossless (IWHT 4x4)
        new ItxfmAddFunc[] { IwhtIwht4x4Add, IwhtIwht4x4Add, IwhtIwht4x4Add, IwhtIwht4x4Add },
    };

    /// <summary>
    /// IWHT 4×4 transform (used for lossless mode).
    /// </summary>
    public static readonly ItxfmAddFunc Iwht4x4Add = IwhtIwht4x4Add;
}

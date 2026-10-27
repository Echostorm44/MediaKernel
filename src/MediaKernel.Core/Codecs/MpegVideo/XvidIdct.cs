// FFmpeg's xvididct.c - the Walken IDCT, which Xvid uses and which a stream Xvid encoded has
// to be decoded with to match ffmpeg.
//
// This is not a better or worse IDCT than the simple one; it is a DIFFERENT one, and the
// difference is visible in the output. An IDCT is specified only to a tolerance, so two
// conforming implementations disagree by a count here and there, and those counts accumulate
// through a chain of predicted pictures. FFmpeg switches to this one whenever a stream says it
// was written by Xvid, which is why a decoder that only has the simple IDCT produces a
// near-miss on such a stream rather than a clean failure.
//
// The C file's own note is worth repeating: this C is not Xvid's original IDCT either. It is a
// modified one that reproduces the ERROR PROFILE of the MMX/SSE2 versions, which is why
// idct_col_8 multiplies by SQRT2 and doubles rather than multiplying by 2*SQRT2 - the
// intermediate truncation is the point, and "simplifying" it changes the output.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>The Walken IDCT (<c>xvididct.c</c>).</summary>
public static class XvidIdct
{
    private const int RowShift = 11;
    private const int ColShift = 6;

    /// <summary><c>RND0</c> - <c>1 &lt;&lt; (COL_SHIFT + ROW_SHIFT - 1)</c>.</summary>
    private const int Rnd0 = 65536;

    private const int Rnd1 = 3597;      // FIX(1.75683487303)
    private const int Rnd2 = 2260;      // FIX(1.10355339059)
    private const int Rnd3 = 1203;      // FIX(0.587788325588)
    private const int Rnd4 = 0;
    private const int Rnd5 = 120;       // FIX(0.058658283817)
    private const int Rnd6 = 512;       // FIX(0.25)
    private const int Rnd7 = 512;       // FIX(0.25)

    private static ReadOnlySpan<int> Tab04 => [22725, 21407, 19266, 16384, 12873, 8867, 4520];
    private static ReadOnlySpan<int> Tab17 => [31521, 29692, 26722, 22725, 17855, 12299, 6270];
    private static ReadOnlySpan<int> Tab26 => [29692, 27969, 25172, 21407, 16819, 11585, 5906];
    private static ReadOnlySpan<int> Tab35 => [26722, 25172, 22654, 19266, 15137, 10426, 5315];

    private const int Tan1 = 0x32EC;
    private const int Tan2 = 0x6A0A;
    private const int Tan3 = 0xAB0E;
    private const int Sqrt2 = 0x5A82;

    /// <summary>
    /// <c>MULT(c, x, n)</c> - <c>(unsigned)((int)((c) * (unsigned)(x)) &gt;&gt; (n))</c>.
    /// </summary>
    /// <remarks>
    /// The C multiplies in unsigned, reinterprets as signed and shifts arithmetically. Signed
    /// and unsigned 32-bit multiplication produce the same bits, so plain <c>int</c> in an
    /// unchecked context is the same operation - the cast chain exists in the C to keep the
    /// multiply itself free of signed overflow, which is undefined there and defined here.
    /// </remarks>
    private static int Mult(int c, int x, int n) => unchecked(c * x) >> n;

    /// <summary><c>idct_row</c>.</summary>
    /// <returns>0 when the row is entirely zero after the transform, 1 otherwise.</returns>
    /// <remarks>
    /// Three arms, picked on which coefficients are non-zero: only the DC and the first three
    /// AC terms; only the DC and the fourth; or the general case. They are not an optimisation
    /// bolted on - the narrow arms compute different expressions, and the C's own zero test
    /// (<c>right | in[4]</c>) is what decides which.
    /// </remarks>
    private static int IdctRow(Span<short> input, ReadOnlySpan<int> tab, int rnd)
    {
        int c1 = tab[0], c2 = tab[1], c3 = tab[2], c4 = tab[3];
        int c5 = tab[4], c6 = tab[5], c7 = tab[6];

        int right = input[5] | input[6] | input[7];
        int left = input[1] | input[2] | input[3];

        unchecked
        {
            if ((right | input[4]) == 0)
            {
                int k = c4 * input[0] + rnd;
                if (left != 0)
                {
                    int a0 = k + c2 * input[2];
                    int a1 = k + c6 * input[2];
                    int a2 = k - c6 * input[2];
                    int a3 = k - c2 * input[2];

                    int b0 = c1 * input[1] + c3 * input[3];
                    int b1 = c3 * input[1] - c7 * input[3];
                    int b2 = c5 * input[1] - c1 * input[3];
                    int b3 = c7 * input[1] - c5 * input[3];

                    input[0] = (short)((a0 + b0) >> RowShift);
                    input[1] = (short)((a1 + b1) >> RowShift);
                    input[2] = (short)((a2 + b2) >> RowShift);
                    input[3] = (short)((a3 + b3) >> RowShift);
                    input[4] = (short)((a3 - b3) >> RowShift);
                    input[5] = (short)((a2 - b2) >> RowShift);
                    input[6] = (short)((a1 - b1) >> RowShift);
                    input[7] = (short)((a0 - b0) >> RowShift);
                }
                else
                {
                    int a0 = k >> RowShift;
                    if (a0 == 0)
                        return 0;

                    input[0] = (short)a0;
                    input[1] = (short)a0;
                    input[2] = (short)a0;
                    input[3] = (short)a0;
                    input[4] = (short)a0;
                    input[5] = (short)a0;
                    input[6] = (short)a0;
                    input[7] = (short)a0;
                }
            }
            else if ((left | right) == 0)
            {
                int a0 = (rnd + c4 * (input[0] + input[4])) >> RowShift;
                int a1 = (rnd + c4 * (input[0] - input[4])) >> RowShift;

                input[0] = (short)a0;
                input[3] = (short)a0;
                input[4] = (short)a0;
                input[7] = (short)a0;
                input[1] = (short)a1;
                input[2] = (short)a1;
                input[5] = (short)a1;
                input[6] = (short)a1;
            }
            else
            {
                int k = c4 * input[0] + rnd;
                int a0 = k + c2 * input[2] + c4 * input[4] + c6 * input[6];
                int a1 = k + c6 * input[2] - c4 * input[4] - c2 * input[6];
                int a2 = k - c6 * input[2] - c4 * input[4] + c2 * input[6];
                int a3 = k - c2 * input[2] + c4 * input[4] - c6 * input[6];

                int b0 = c1 * input[1] + c3 * input[3] + c5 * input[5] + c7 * input[7];
                int b1 = c3 * input[1] - c7 * input[3] - c1 * input[5] - c5 * input[7];
                int b2 = c5 * input[1] - c1 * input[3] + c7 * input[5] + c3 * input[7];
                int b3 = c7 * input[1] - c5 * input[3] + c3 * input[5] - c1 * input[7];

                input[0] = (short)((a0 + b0) >> RowShift);
                input[1] = (short)((a1 + b1) >> RowShift);
                input[2] = (short)((a2 + b2) >> RowShift);
                input[3] = (short)((a3 + b3) >> RowShift);
                input[4] = (short)((a3 - b3) >> RowShift);
                input[5] = (short)((a2 - b2) >> RowShift);
                input[6] = (short)((a1 - b1) >> RowShift);
                input[7] = (short)((a0 - b0) >> RowShift);
            }
        }

        return 1;
    }

    /// <summary><c>idct_col_8</c> - one column, with all eight inputs possibly non-zero.</summary>
    private static void IdctCol8(Span<short> input, int at)
    {
        unchecked
        {
            // odd
            int mm4 = input[at + 7 * 8];
            int mm5 = input[at + 5 * 8];
            int mm6 = input[at + 3 * 8];
            int mm7 = input[at + 1 * 8];

            int mm0 = Mult(Tan1, mm4, 16) + mm7;
            int mm1 = Mult(Tan1, mm7, 16) - mm4;
            int mm2 = Mult(Tan3, mm5, 16) + mm6;
            int mm3 = Mult(Tan3, mm6, 16) - mm5;

            mm7 = mm0 + mm2;
            mm4 = mm1 - mm3;
            mm0 = mm0 - mm2;
            mm1 = mm1 + mm3;
            mm6 = mm0 + mm1;
            mm5 = mm0 - mm1;

            // Watch out: 2 * MULT(SQRT2, x, 16) is not MULT(2 * SQRT2, x, 16). The truncation
            // happens before the doubling, which is what the pmulhw in the MMX/SSE2 versions
            // does, and matching their error profile is the whole purpose of this file.
            mm5 = 2 * Mult(Sqrt2, mm5, 16);
            mm6 = 2 * Mult(Sqrt2, mm6, 16);

            // even
            mm1 = input[at + 2 * 8];
            mm2 = input[at + 6 * 8];
            mm3 = Mult(Tan2, mm2, 16) + mm1;
            mm2 = Mult(Tan2, mm1, 16) - mm2;

            // LOAD_BUTTERFLY(mm0, mm1, 0 * 8, 4 * 8, spill, in)
            int s0 = input[at + 0 * 8], s4 = input[at + 4 * 8];
            mm0 = s0 + s4;
            mm1 = s0 - s4;

            Store(input, at, ref mm0, ref mm1, ref mm2, ref mm3, ref mm4, ref mm5, ref mm6, ref mm7);
        }
    }

    /// <summary>
    /// <c>idct_col_4</c> - one column where only the first four inputs can be non-zero.
    /// </summary>
    private static void IdctCol4(Span<short> input, int at)
    {
        unchecked
        {
            // odd
            int mm0 = input[at + 1 * 8];
            int mm2 = input[at + 3 * 8];

            int mm1 = Mult(Tan1, mm0, 16);
            int mm3 = Mult(Tan3, mm2, 16);

            int mm7 = mm0 + mm2;
            int mm4 = mm1 - mm3;
            mm0 = mm0 - mm2;
            mm1 = mm1 + mm3;
            int mm6 = mm0 + mm1;
            int mm5 = mm0 - mm1;
            mm6 = 2 * Mult(Sqrt2, mm6, 16);
            mm5 = 2 * Mult(Sqrt2, mm5, 16);

            // even
            mm0 = mm1 = input[at + 0 * 8];
            mm3 = input[at + 2 * 8];
            mm2 = Mult(Tan2, mm3, 16);

            Store(input, at, ref mm0, ref mm1, ref mm2, ref mm3, ref mm4, ref mm5, ref mm6, ref mm7);
        }
    }

    /// <summary>
    /// <c>idct_col_3</c> - one column where only the first three inputs can be non-zero.
    /// </summary>
    private static void IdctCol3(Span<short> input, int at)
    {
        unchecked
        {
            // odd
            int mm7 = input[at + 1 * 8];
            int mm4 = Mult(Tan1, mm7, 16);

            int mm6 = mm7 + mm4;
            int mm5 = mm7 - mm4;
            mm6 = 2 * Mult(Sqrt2, mm6, 16);
            mm5 = 2 * Mult(Sqrt2, mm5, 16);

            // even
            int mm0 = input[at + 0 * 8];
            int mm1 = mm0;
            int mm3 = input[at + 2 * 8];
            int mm2 = Mult(Tan2, mm3, 16);

            Store(input, at, ref mm0, ref mm1, ref mm2, ref mm3, ref mm4, ref mm5, ref mm6, ref mm7);
        }
    }

    /// <summary>
    /// The butterfly-and-store tail the three column kernels share verbatim.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: in the C this is the last twenty lines of each of
    /// <c>idct_col_8</c>, <c>idct_col_4</c> and <c>idct_col_3</c>, written out three times and
    /// identical in all three. It is one method here because three copies of arithmetic this
    /// exact is three places for a typo to hide, and the only thing that differs between the
    /// callers is which registers they arrive in - which the parameter order preserves.
    /// </remarks>
    private static void Store(Span<short> input, int at,
                              ref int mm0, ref int mm1, ref int mm2, ref int mm3,
                              ref int mm4, ref int mm5, ref int mm6, ref int mm7)
    {
        unchecked
        {
            Butterfly(ref mm0, ref mm3);
            Butterfly(ref mm0, ref mm7);
            input[at + 8 * 0] = (short)(mm0 >> ColShift);
            input[at + 8 * 7] = (short)(mm7 >> ColShift);
            Butterfly(ref mm3, ref mm4);
            input[at + 8 * 3] = (short)(mm3 >> ColShift);
            input[at + 8 * 4] = (short)(mm4 >> ColShift);

            Butterfly(ref mm1, ref mm2);
            Butterfly(ref mm1, ref mm6);
            input[at + 8 * 1] = (short)(mm1 >> ColShift);
            input[at + 8 * 6] = (short)(mm6 >> ColShift);
            Butterfly(ref mm2, ref mm5);
            input[at + 8 * 2] = (short)(mm2 >> ColShift);
            input[at + 8 * 5] = (short)(mm5 >> ColShift);
        }
    }

    /// <summary><c>BUTTERFLY(a, b, tmp)</c>.</summary>
    /// <remarks>
    /// The C's third argument names a caller variable to use as scratch, and two of the call
    /// sites pass <c>mm0</c> rather than <c>spill</c> - but only after <c>mm0</c> has been
    /// stored and is dead, so the choice is a register hint and not an output. A local
    /// temporary is the same operation.
    /// </remarks>
    private static void Butterfly(ref int a, ref int b)
    {
        unchecked
        {
            int tmp = a + b;
            b = a - b;
            a = tmp;
        }
    }

    /// <summary><c>ff_xvid_idct</c> - the transform, in place.</summary>
    /// <remarks>
    /// Which column kernel runs is decided by which rows produced anything: a row that came out
    /// all-zero cannot contribute, so if rows 4 to 7 are all zero the four-input kernel is
    /// enough, and if row 3 is zero too the three-input one is. Rows 0 to 2 are assumed
    /// non-zero (<c>rows</c> starts at 0x07) without being tested, because the narrowest kernel
    /// handles them anyway.
    /// </remarks>
    public static void XvidIdctTransform(Span<short> input)
    {
        int rows = 0x07;

        IdctRow(input.Slice(0 * 8, 8), Tab04, Rnd0);
        IdctRow(input.Slice(1 * 8, 8), Tab17, Rnd1);
        IdctRow(input.Slice(2 * 8, 8), Tab26, Rnd2);
        if (IdctRow(input.Slice(3 * 8, 8), Tab35, Rnd3) != 0) rows |= 0x08;
        if (IdctRow(input.Slice(4 * 8, 8), Tab04, Rnd4) != 0) rows |= 0x10;
        if (IdctRow(input.Slice(5 * 8, 8), Tab35, Rnd5) != 0) rows |= 0x20;
        if (IdctRow(input.Slice(6 * 8, 8), Tab26, Rnd6) != 0) rows |= 0x40;
        if (IdctRow(input.Slice(7 * 8, 8), Tab17, Rnd7) != 0) rows |= 0x80;

        if ((rows & 0xF0) != 0)
        {
            for (int i = 0; i < 8; i++)
                IdctCol8(input, i);
        }
        else if ((rows & 0x08) != 0)
        {
            for (int i = 0; i < 8; i++)
                IdctCol4(input, i);
        }
        else
        {
            for (int i = 0; i < 8; i++)
                IdctCol3(input, i);
        }
    }

    /// <summary><c>xvid_idct_put</c>.</summary>
    public static void XvidIdctPut(Span<byte> dest, int offset, int lineSize, Span<short> block)
    {
        XvidIdctTransform(block);
        IdctDsp.PutPixelsClamped(block, dest, offset, lineSize);
    }

    /// <summary><c>xvid_idct_add</c>.</summary>
    public static void XvidIdctAdd(Span<byte> dest, int offset, int lineSize, Span<short> block)
    {
        XvidIdctTransform(block);
        IdctDsp.AddPixelsClamped(block, dest, offset, lineSize);
    }
}

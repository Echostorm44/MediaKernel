// libavutil/tx_template.c's odd-factor codelets and the prime-factor index maps that drive them.
//
// A transform whose length is not a power of two is computed by the prime-factor algorithm: for
// a length n*m with gcd(n, m) == 1, m transforms of length n, then n transforms of length m, with
// the input gathered and the output scattered through two index maps. AAC's 960-sample framing
// wants an inverse MDCT at 960, whose sub-transform is 480 = 15 * 32; ER AAC ELD's 480-sample
// framing lands on the same factorisation.
//
// The structure here is not inferred from the factor lists in the codelet definitions - it is
// what av_tx's own tree printer says it resolved to, obtained by building libavutil and running
// av_tx_init at AV_LOG_DEBUG:
//
//     fft 15  -> fft_pfa over fft3_ns  (n=3)  and fft5_ns  (m=5)
//     fft 480 -> fft_pfa over fft15_ns (n=15) and fft32_ns (m=32)
//     imdct 960 -> mdct_pfa_15xM_inv over fft32_ns
//     imdct 480 -> mdct_pfa_15xM_inv over fft16_ns
//     imdct 120 -> mdct_pfa_15xM_inv over fft4_ns
//
// The MDCT at a compound length is a codelet of its own, not the generic MDCT over a compound
// FFT. Guessing otherwise produces a transform that looks complete and computes the wrong thing.

using System;

namespace MediaKernel.Core.Dsp.Tx;

/// <summary>The 3-point and 5-point codelets, and the 15-point one built from them.</summary>
internal static class TxPfa
{
    /// <summary>
    /// <c>ff_tx_tab_53</c>. The 5-point entries are doubled in the C to avoid AVX lane
    /// shuffles; the duplicates are kept so the indices match the C's exactly.
    /// </summary>
    private static readonly float[] Tab53 = BuildTab53();

    private static float[] BuildTab53()
    {
        var t = new float[12];

        // 5-point, doubled.
        t[0] = (float)Math.Cos(2 * Math.PI / 5);
        t[1] = (float)Math.Cos(2 * Math.PI / 5);
        t[2] = (float)Math.Cos(2 * Math.PI / 10);
        t[3] = (float)Math.Cos(2 * Math.PI / 10);
        t[4] = (float)Math.Sin(2 * Math.PI / 5);
        t[5] = (float)Math.Sin(2 * Math.PI / 5);
        t[6] = (float)Math.Sin(2 * Math.PI / 10);
        t[7] = (float)Math.Sin(2 * Math.PI / 10);

        // 3-point.
        t[8] = (float)Math.Cos(2 * Math.PI / 12);
        t[9] = (float)Math.Cos(2 * Math.PI / 12);
        t[10] = (float)Math.Cos(2 * Math.PI / 6);
        t[11] = (float)Math.Cos(8 * Math.PI / 6);

        return t;
    }

    /// <summary><c>fft3</c>. Reads three consecutive inputs, writes with a stride.</summary>
    public static void Fft3(Span<TxComplex> output, int outAt, ReadOnlySpan<TxComplex> input,
                            int inAt, int stride)
    {
        float[] tab = Tab53;
        Span<TxComplex> tmp = stackalloc TxComplex[3];

        tmp[0] = input[inAt];

        // BF(x, y, a, b): x = a - b; y = a + b.
        TxComplex t1 = default, t2 = default;
        t1.Re = input[inAt + 1].Im - input[inAt + 2].Im;
        t2.Im = input[inAt + 1].Im + input[inAt + 2].Im;
        t1.Im = input[inAt + 1].Re - input[inAt + 2].Re;
        t2.Re = input[inAt + 1].Re + input[inAt + 2].Re;
        tmp[1] = t1;
        tmp[2] = t2;

        output[outAt].Re = tmp[0].Re + tmp[2].Re;
        output[outAt].Im = tmp[0].Im + tmp[2].Im;

        t1.Re = tab[8] * tmp[1].Re;
        t1.Im = tab[9] * tmp[1].Im;
        t2.Re = tab[10] * tmp[2].Re;
        t2.Im = tab[10] * tmp[2].Im;

        output[outAt + stride].Re = tmp[0].Re - t2.Re + t1.Re;
        output[outAt + stride].Im = tmp[0].Im - t2.Im - t1.Im;
        output[outAt + 2 * stride].Re = tmp[0].Re - t2.Re - t1.Re;
        output[outAt + 2 * stride].Im = tmp[0].Im - t2.Im + t1.Im;
    }

    /// <summary>
    /// <c>DECL_FFT5</c>. The four instantiations differ only in where they write, which is
    /// what <paramref name="d0"/>..<paramref name="d4"/> carry: <c>fft5</c> is 0,1,2,3,4 and the
    /// three used inside <see cref="Fft15"/> are permuted. Writing those permutations by hand is
    /// how a 15-point transform comes out complete and wrong.
    /// </summary>
    public static void Fft5(Span<TxComplex> output, int outAt, ReadOnlySpan<TxComplex> input,
                            int inAt, int stride,
                            int d0, int d1, int d2, int d3, int d4)
    {
        float[] tab = Tab53;

        TxComplex dc = input[inAt];
        Span<TxComplex> t = stackalloc TxComplex[6];
        Span<TxComplex> z0 = stackalloc TxComplex[4];

        // BF(t[1].im, t[0].re, in[1].re, in[4].re) and the three like it.
        t[1].Im = input[inAt + 1].Re - input[inAt + 4].Re;
        t[0].Re = input[inAt + 1].Re + input[inAt + 4].Re;
        t[1].Re = input[inAt + 1].Im - input[inAt + 4].Im;
        t[0].Im = input[inAt + 1].Im + input[inAt + 4].Im;
        t[3].Im = input[inAt + 2].Re - input[inAt + 3].Re;
        t[2].Re = input[inAt + 2].Re + input[inAt + 3].Re;
        t[3].Re = input[inAt + 2].Im - input[inAt + 3].Im;
        t[2].Im = input[inAt + 2].Im + input[inAt + 3].Im;

        output[outAt + d0 * stride].Re = dc.Re + t[0].Re + t[2].Re;
        output[outAt + d0 * stride].Im = dc.Im + t[0].Im + t[2].Im;

        // SMUL(dre, dim, are, aim, bre, bim): dre = are*bre - aim*bim;
        //                                     dim = are*bim - aim*bre.
        float t4re = tab[0] * t[2].Re - tab[2] * t[0].Re;
        float t0re = tab[0] * t[0].Re - tab[2] * t[2].Re;
        float t4im = tab[0] * t[2].Im - tab[2] * t[0].Im;
        float t0im = tab[0] * t[0].Im - tab[2] * t[2].Im;

        // CMUL(dre, dim, are, aim, bre, bim): dre = are*bre - aim*bim;
        //                                     dim = are*bim + aim*bre.
        float t5re = tab[4] * t[3].Re - tab[6] * t[1].Re;
        float t1re = tab[4] * t[1].Re + tab[6] * t[3].Re;
        float t5im = tab[4] * t[3].Im - tab[6] * t[1].Im;
        float t1im = tab[4] * t[1].Im + tab[6] * t[3].Im;

        t[4].Re = t4re; t[0].Re = t0re;
        t[4].Im = t4im; t[0].Im = t0im;
        t[5].Re = t5re; t[1].Re = t1re;
        t[5].Im = t5im; t[1].Im = t1im;

        z0[0].Re = t[0].Re - t[1].Re;
        z0[3].Re = t[0].Re + t[1].Re;
        z0[0].Im = t[0].Im - t[1].Im;
        z0[3].Im = t[0].Im + t[1].Im;
        z0[2].Re = t[4].Re - t[5].Re;
        z0[1].Re = t[4].Re + t[5].Re;
        z0[2].Im = t[4].Im - t[5].Im;
        z0[1].Im = t[4].Im + t[5].Im;

        output[outAt + d1 * stride].Re = dc.Re + z0[3].Re;
        output[outAt + d1 * stride].Im = dc.Im + z0[0].Im;
        output[outAt + d2 * stride].Re = dc.Re + z0[2].Re;
        output[outAt + d2 * stride].Im = dc.Im + z0[1].Im;
        output[outAt + d3 * stride].Re = dc.Re + z0[1].Re;
        output[outAt + d3 * stride].Im = dc.Im + z0[2].Im;
        output[outAt + d4 * stride].Re = dc.Re + z0[0].Re;
        output[outAt + d4 * stride].Im = dc.Im + z0[3].Im;
    }

    /// <summary>
    /// <c>fft15</c>: five 3-point transforms into a scratch, then three permuted 5-point
    /// transforms out of it.
    /// </summary>
    public static void Fft15(Span<TxComplex> output, int outAt, ReadOnlySpan<TxComplex> input,
                             int inAt, int stride)
    {
        Span<TxComplex> tmp = stackalloc TxComplex[15];

        for (int i = 0; i < 5; i++)
            Fft3(tmp, i, input, inAt + i * 3, 5);

        Fft5(output, outAt, tmp, 0, stride, 0, 6, 12, 3, 9);    // fft5_m1
        Fft5(output, outAt, tmp, 5, stride, 10, 1, 7, 13, 4);   // fft5_m2
        Fft5(output, outAt, tmp, 10, stride, 5, 11, 2, 8, 14);  // fft5_m3
    }

    /// <summary>
    /// <c>ff_tx_gen_compound_mapping</c> - the Ruritanian input map and the CRT output map for
    /// a length <c>n * m</c> with <c>n</c> and <c>m</c> coprime.
    /// </summary>
    /// <remarks>
    /// Returns one array of <c>2 * n * m</c> entries, the input map followed by the output map,
    /// exactly as the C's single allocation does - the MDCT codelet reads the second half by
    /// offsetting past the first.
    /// </remarks>
    public static int[] GenCompoundMapping(bool inverse, bool scatter, int n, int m)
    {
        int len = n * m;

        if (Gcd(n, m) != 1)
            throw new ArgumentException(
                $"tx: the prime-factor algorithm needs coprime factors; {n} and {m} are not.");

        int mInv = MulInv(m, n);
        int nInv = MulInv(n, m);

        var map = new int[2 * len];
        Span<int> inMap = map.AsSpan(0, len);
        Span<int> outMap = map.AsSpan(len, len);

        if (scatter)
        {
            for (int j = 0; j < m; j++)
                for (int i = 0; i < n; i++)
                {
                    inMap[(i * m + j * n) % len] = j * n + i;
                    outMap[(i * m * mInv + j * n * nInv) % len] = i * m + j;
                }
        }
        else
        {
            for (int j = 0; j < m; j++)
                for (int i = 0; i < n; i++)
                {
                    inMap[j * n + i] = (i * m + j * n) % len;
                    outMap[(i * m * mInv + j * n * nInv) % len] = i * m + j;
                }
        }

        if (inverse)
        {
            // Reverse each group of n after its DC. This is what makes the map an inverse
            // transform's rather than a forward one's.
            for (int i = 0; i < m; i++)
            {
                int at = i * n + 1;
                for (int j = 0; j < (n - 1) >> 1; j++)
                    (map[at + j], map[at + n - j - 2]) = (map[at + n - j - 2], map[at + j]);
            }
        }

        return map;
    }

    /// <summary>
    /// <c>TX_EMBED_INPUT_PFA_MAP</c> - fold a 15-point sub-transform's own prime-factor input
    /// map into an already-built map, in place.
    /// </summary>
    /// <remarks>
    /// The 15-point codelet is itself compound, so its input wants permuting too. Composing the
    /// two maps here means the hot loop performs one indirection rather than two.
    /// </remarks>
    public static void EmbedInputPfaMap(Span<int> map, int totalLength, int d1, int d2)
    {
        int sl = d1 * d2;
        Span<int> scratch = stackalloc int[sl];

        for (int k = 0; k < totalLength; k += sl)
        {
            map.Slice(k, sl).CopyTo(scratch);
            for (int m = 0; m < d2; m++)
                for (int n = 0; n < d1; n++)
                    map[k + m * d1 + n] = scratch[(m * d1 + n * d2) % sl];
        }
    }

    /// <summary>
    /// <c>ff_tx_gen_pfa_input_map</c> - the map a standalone 15-point codelet expects when it is
    /// used as a sub-transform.
    /// </summary>
    public static int[] GenPfaInputMap(int length, bool inverse, bool scatter, int d1, int d2)
    {
        int sl = d1 * d2;
        var map = new int[length];

        for (int k = 0; k < length; k += sl)
        {
            if (inverse || scatter)
            {
                for (int m = 0; m < d2; m++)
                    for (int n = 0; n < d1; n++)
                        map[k + ((m * d1 + n * d2) % sl)] = m * d1 + n;
            }
            else
            {
                for (int m = 0; m < d2; m++)
                    for (int n = 0; n < d1; n++)
                        map[k + m * d1 + n] = (m * d1 + n * d2) % sl;
            }

            if (inverse)
                for (int w = 1; w <= sl >> 1; w++)
                    (map[k + w], map[k + sl - w]) = (map[k + sl - w], map[k + w]);
        }

        return map;
    }

    /// <summary><c>av_gcd</c>.</summary>
    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    /// <summary>
    /// <c>mulinv</c> from tx.c - the multiplicative inverse of <paramref name="n"/> modulo
    /// <paramref name="m"/>, found by trial because m is small.
    /// </summary>
    private static int MulInv(int n, int m)
    {
        n = n % m;
        for (int x = 1; x < m; x++)
            if ((n * x) % m == 1)
                return x;
        return 0;
    }
}

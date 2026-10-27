// libavutil/tx_template.c's MDCT codelets: the half-length inverse, the full inverse and the
// forward transform, over the split-radix FFT in TxFft.
//
// The MDCT is a pre-rotation, a complex FFT of half the length, and a post-rotation. FFmpeg folds
// the FFT's input permutation into the pre-rotation's index map, and folds a multiply by two into
// that map as well, which is why the map is doubled at the end of init and the pre-rotation
// indexes the input with it directly.
//
// Everything here is ordered as the C orders it. The exponent table is generated in double and
// rounded once, as ff_tx_mdct_gen_exp does, including its factor of sqrt(|scale|) and the 1/8
// offset in the angle.

using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Dsp.Tx;

/// <summary>
/// One initialised MDCT, the equivalent of an <c>AVTXContext</c> for
/// <c>AV_TX_FLOAT_MDCT</c> or <c>AV_TX_FULL_IMDCT</c>.
/// </summary>
internal sealed class TxMdct
{
    private readonly int len;
    private readonly bool inverse;
    private readonly bool full;
    private readonly int[] map;
    private readonly TxComplex[] exp;

    // The prime-factor path, used when the length is 15 times a power of two. subLen is the
    // power-of-two sub-transform's length, subMap its preshuffle map, and tmp the scratch the
    // fifteen interleaved sub-transforms are computed in.
    private readonly int subLen;
    private readonly int[]? subMap;
    private readonly TxComplex[]? tmp;

    /// <summary>The odd factor of a prime-factor length; 0 for a power-of-two transform.</summary>
    private readonly int oddFactor;

    /// <summary>Coefficients in for the inverse, samples in for the forward direction.</summary>
    public int Length => len;

    /// <summary>
    /// <c>av_tx_init</c> for the three MDCT shapes this project needs. Lengths are powers of
    /// two; <paramref name="full"/> selects <c>AV_TX_FULL_IMDCT</c>, which emits all
    /// <c>2 * length</c> samples rather than the middle <c>length</c>.
    /// </summary>
    public static TxMdct Create(int length, bool inverse, bool full, float scale)
    {
        bool powerOfTwo = length >= 8 && (length & (length - 1)) == 0;

        // 15 times a power of two resolves to mdct_pfa_15xM in av_tx: AAC's 960 and 120, and
        // ER AAC ELD's 480. A power of two is checked first, because 240 is both 15*16 and
        // not a power of two while 16 is, and the resolution has to match av_tx's.
        bool pfa15 = !powerOfTwo
                  && length % 15 == 0
                  && IsPowerOfTwo((length / 15) >> 1)
                  && length >= 30;

        if (!powerOfTwo && !pfa15)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"tx: no MDCT for length {length}. This port covers powers of two of at least 8 "
              + "and 15 times a power of two, which is what av_tx resolves to mdct_pfa_15xM.");
        if (full && !inverse)
            throw new ArgumentException(
                "tx: AV_TX_FULL_IMDCT exists only in the inverse direction.", nameof(full));
        if (full && pfa15)
            throw new ArgumentException(
                "tx: AV_TX_FULL_IMDCT at a prime-factor length is not translated; nothing asks "
              + "for one.", nameof(full));

        return new TxMdct(length, inverse, full, scale, pfa15 ? 15 : 0);
    }

    private TxMdct(int length, bool inverse, bool full, float scale, int oddFactor)
    {
        len = length;
        this.inverse = inverse;
        this.full = full;
        this.oddFactor = oddFactor;

        if (oddFactor != 0)
        {
            // ff_tx_mdct_pfa_init. Note the two differences from the power-of-two path: the
            // sub-transform's map direction is SCATTER regardless of direction, and the
            // compound map is built for the whole half-length rather than taken from the
            // sub-transform.
            int half = len >> 1;
            subLen = half / oddFactor;
            subMap = TxFft.GeneratePowerOfTwoRevTab(subLen, inverse, scatter: true);

            map = TxPfa.GenCompoundMapping(inverse, scatter: false, n: oddFactor, m: subLen);

            // The 15-point transform is itself compound, so its own input map is folded in.
            if (oddFactor == 15)
                TxPfa.EmbedInputPfaMap(map.AsSpan(0, half), half, 3, 5);

            exp = GenerateExponents(scale, inverse ? map : null);

            // "Saves multiplies in loops." Unlike the power-of-two path this happens in both
            // directions, and the forward transform undoes it with a shift when it indexes exp.
            for (int i = 0; i < half; i++)
                map[i] <<= 1;

            tmp = new TxComplex[half];
            return;
        }

        // ff_tx_mdct_init: the subtransform is an in-place preshuffled FFT of half the length,
        // and its index map becomes this context's.
        map = TxFft.GeneratePowerOfTwoRevTab(len >> 1, inverse, scatter: !inverse);

        exp = GenerateExponents(scale, inverse ? map : null);

        // "Saves a multiply in a hot path."
        if (inverse)
            for (int i = 0; i < (len >> 1); i++)
                map[i] <<= 1;
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary><c>ff_tx_mdct_gen_exp</c>.</summary>
    private TxComplex[] GenerateExponents(float scale, int[]? preTab)
    {
        int len4 = len >> 1;                       // the C's name; it is half the length
        double scaled = scale;
        double theta = (scaled < 0 ? len4 : 0) + 1.0 / 8.0;
        int alloc = preTab != null ? 2 * len4 : len4;
        var table = new TxComplex[alloc];

        scaled = Math.Sqrt(Math.Abs(scaled));
        int off = preTab != null ? len4 : 0;

        for (int i = 0; i < len4; i++)
        {
            double alpha = Math.PI / 2 * (i + theta) / len4;
            table[off + i].Re = (float)(Math.Cos(alpha) * scaled);
            table[off + i].Im = (float)(Math.Sin(alpha) * scaled);
        }

        if (preTab != null)
            for (int i = 0; i < len4; i++)
                table[i] = table[len4 + preTab[i]];

        return table;
    }

    /// <summary>
    /// Run the transform. For the inverse, <paramref name="src"/> holds <c>Length</c>
    /// coefficients and <paramref name="dst"/> receives <c>Length</c> samples, or
    /// <c>2 * Length</c> for a full IMDCT. For the forward direction, <paramref name="src"/>
    /// holds <c>2 * Length</c> samples and <paramref name="dst"/> receives <c>Length</c>
    /// coefficients.
    /// </summary>
    public void Transform(Span<float> dst, ReadOnlySpan<float> src)
    {
        if (oddFactor != 0)
        {
            if (inverse) InversePfa(dst, src);
            else ForwardPfa(dst, src);
            return;
        }

        if (!inverse) { Forward(dst, src); return; }
        if (full) { InverseFull(dst, src); return; }
        Inverse(dst, src);
    }

    /// <summary><c>ff_tx_mdct_inv</c>, with a stride of one sample.</summary>
    private void Inverse(Span<float> dst, ReadOnlySpan<float> src)
    {
        int len2 = len >> 1;
        int len4 = len >> 2;

        if (dst.Length < len || src.Length < len)
            throw new ArgumentException(
                $"tx: an inverse MDCT of length {len} needs {len} coefficients in and {len} "
              + $"samples out; it was given {src.Length} and {dst.Length}.");

        Span<TxComplex> z = MemoryMarshal.Cast<float, TxComplex>(dst)[..len2];
        int in2 = len2 * 2 - 1;

        for (int i = 0; i < len2; i++)
        {
            int k = map[i];
            TxComplex tmp;
            tmp.Re = src[in2 - k];
            tmp.Im = src[k];
            // CMUL3(z[i], tmp, exp[i])
            z[i].Re = tmp.Re * exp[i].Re - tmp.Im * exp[i].Im;
            z[i].Im = tmp.Re * exp[i].Im + tmp.Im * exp[i].Re;
        }

        TxFft.FftNs(z, z, len2);

        int e = len2;      // the C's exp += len2
        for (int i = 0; i < len4; i++)
        {
            int i0 = len4 + i, i1 = len4 - i - 1;
            float src1Re = z[i1].Im, src1Im = z[i1].Re;
            float src0Re = z[i0].Im, src0Im = z[i0].Re;

            // CMUL(z[i1].re, z[i0].im, src1.re, src1.im, exp[i1].im, exp[i1].re)
            z[i1].Re = src1Re * exp[e + i1].Im - src1Im * exp[e + i1].Re;
            z[i0].Im = src1Re * exp[e + i1].Re + src1Im * exp[e + i1].Im;
            // CMUL(z[i0].re, z[i1].im, src0.re, src0.im, exp[i0].im, exp[i0].re)
            z[i0].Re = src0Re * exp[e + i0].Im - src0Im * exp[e + i0].Re;
            z[i1].Im = src0Re * exp[e + i0].Re + src0Im * exp[e + i0].Im;
        }
    }

    /// <summary>
    /// <c>ff_tx_mdct_pfa_15xM_inv</c> - the inverse MDCT at 15 times a power of two.
    /// </summary>
    /// <remarks>
    /// Same three stages as the power-of-two inverse, but the middle one is a prime-factor FFT:
    /// the pre-rotation feeds fifteen inputs at a time into a 15-point codelet whose outputs are
    /// scattered <c>m</c> apart, then <c>m</c>-strided runs of fifteen are transformed by the
    /// power-of-two sub-transform, and the post-rotation gathers through the output map.
    /// </remarks>
    private void InversePfa(Span<float> dst, ReadOnlySpan<float> src)
    {
        int len2 = len >> 1;
        int len4 = len >> 2;
        int n = oddFactor;
        int m = subLen;

        if (dst.Length < len || src.Length < len)
            throw new ArgumentException(
                $"tx: an inverse MDCT of length {len} needs {len} coefficients in and {len} "
              + $"samples out; it was given {src.Length} and {dst.Length}.");

        Span<TxComplex> z = MemoryMarshal.Cast<float, TxComplex>(dst)[..len2];
        Span<TxComplex> scratch = tmp!;
        Span<TxComplex> fftIn = stackalloc TxComplex[n];

        int in2 = (n * m * 2) - 1;
        int mapAt = 0, expAt = 0, subAt = 0;

        for (int i = 0; i < len2; i += n)
        {
            for (int j = 0; j < n; j++)
            {
                int k = map[mapAt + j];
                TxComplex t;
                t.Re = src[in2 - k];
                t.Im = src[k];
                // CMUL3(fftIn[j], t, exp[expAt + j])
                fftIn[j].Re = t.Re * exp[expAt + j].Re - t.Im * exp[expAt + j].Im;
                fftIn[j].Im = t.Re * exp[expAt + j].Im + t.Im * exp[expAt + j].Re;
            }

            TxPfa.Fft15(scratch, subMap![subAt++], fftIn, 0, m);
            expAt += n;
            mapAt += n;
        }

        for (int i = 0; i < n; i++)
            TxFft.FftNs(scratch.Slice(m * i, m), scratch.Slice(m * i, m), m);

        int outMapAt = n * m;
        int e = len2;
        for (int i = 0; i < len4; i++)
        {
            int i0 = len4 + i, i1 = len4 - i - 1;
            int s0 = map[outMapAt + i0], s1 = map[outMapAt + i1];
            float src1Re = scratch[s1].Im, src1Im = scratch[s1].Re;
            float src0Re = scratch[s0].Im, src0Im = scratch[s0].Re;

            z[i1].Re = src1Re * exp[e + i1].Im - src1Im * exp[e + i1].Re;
            z[i0].Im = src1Re * exp[e + i1].Re + src1Im * exp[e + i1].Im;
            z[i0].Re = src0Re * exp[e + i0].Im - src0Im * exp[e + i0].Re;
            z[i1].Im = src0Re * exp[e + i0].Re + src0Im * exp[e + i0].Im;
        }
    }

    /// <summary><c>ff_tx_mdct_pfa_15xM_fwd</c>.</summary>
    private void ForwardPfa(Span<float> dst, ReadOnlySpan<float> src)
    {
        int n = oddFactor;
        int m = subLen;
        int len4 = n * m;
        int len3 = len4 * 3;
        int len8 = len >> 2;

        if (dst.Length < len || src.Length < 2 * len)
            throw new ArgumentException(
                $"tx: a forward MDCT of length {len} needs {2 * len} samples in and {len} "
              + $"coefficients out; it was given {src.Length} and {dst.Length}.");

        Span<TxComplex> scratch = tmp!;
        Span<TxComplex> fftIn = stackalloc TxComplex[n];

        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++)
            {
                int k = map[i * n + j];
                TxComplex t;
                if (k < len4)
                {
                    t.Re = -src[len4 + k] + src[len4 - 1 - k];
                    t.Im = -src[len3 + k] - src[len3 - 1 - k];
                }
                else
                {
                    t.Re = -src[len4 + k] - src[5 * len4 - 1 - k];
                    t.Im = src[k - len4] - src[len3 - 1 - k];
                }

                // CMUL(fftIn[j].im, fftIn[j].re, t.re, t.im, exp[k >> 1].re, exp[k >> 1].im)
                int e = k >> 1;
                fftIn[j].Im = t.Re * exp[e].Re - t.Im * exp[e].Im;
                fftIn[j].Re = t.Re * exp[e].Im + t.Im * exp[e].Re;
            }

            TxPfa.Fft15(scratch, subMap![i], fftIn, 0, m);
        }

        for (int i = 0; i < n; i++)
            TxFft.FftNs(scratch.Slice(m * i, m), scratch.Slice(m * i, m), m);

        int outMapAt = n * m;
        for (int i = 0; i < len8; i++)
        {
            int i0 = len8 + i, i1 = len8 - i - 1;
            int s0 = map[outMapAt + i0], s1 = map[outMapAt + i1];
            float src1Re = scratch[s1].Re, src1Im = scratch[s1].Im;
            float src0Re = scratch[s0].Re, src0Im = scratch[s0].Im;

            dst[2 * i1 + 1] = src0Re * exp[i0].Im - src0Im * exp[i0].Re;
            dst[2 * i0] = src0Re * exp[i0].Re + src0Im * exp[i0].Im;
            dst[2 * i0 + 1] = src1Re * exp[i1].Im - src1Im * exp[i1].Re;
            dst[2 * i1] = src1Re * exp[i1].Re + src1Im * exp[i1].Im;
        }
    }

    /// <summary><c>ff_tx_mdct_inv_full</c>: the half transform, then the mirrored halves.</summary>
    private void InverseFull(Span<float> dst, ReadOnlySpan<float> src)
    {
        int full2 = len << 1;       // the C's len = s->len << 1
        int len2 = full2 >> 1;
        int len4 = full2 >> 2;

        if (dst.Length < full2)
            throw new ArgumentException(
                $"tx: a full inverse MDCT of length {len} writes {full2} samples; it was given "
              + $"{dst.Length}.");

        Inverse(dst[len4..], src);

        for (int i = 0; i < len4; i++)
        {
            dst[i] = -dst[len2 - i - 1];
            dst[full2 - i - 1] = dst[len2 + i];
        }
    }

    /// <summary><c>ff_tx_mdct_fwd</c>, with a stride of one sample.</summary>
    private void Forward(Span<float> dst, ReadOnlySpan<float> src)
    {
        int len2 = len >> 1;
        int len4 = len >> 2;
        int len3 = len2 * 3;

        if (dst.Length < len || src.Length < 2 * len)
            throw new ArgumentException(
                $"tx: a forward MDCT of length {len} needs {2 * len} samples in and {len} "
              + $"coefficients out; it was given {src.Length} and {dst.Length}.");

        Span<TxComplex> z = MemoryMarshal.Cast<float, TxComplex>(dst)[..len2];

        for (int i = 0; i < len2; i++)
        {
            int k = 2 * i;
            int idx = map[i];
            TxComplex tmp;

            if (k < len2)
            {
                tmp.Re = -src[len2 + k] + src[1 * len2 - 1 - k];
                tmp.Im = -src[len3 + k] + -src[1 * len3 - 1 - k];
            }
            else
            {
                tmp.Re = -src[len2 + k] + -src[5 * len2 - 1 - k];
                tmp.Im = src[-len2 + k] + -src[1 * len3 - 1 - k];
            }

            // CMUL(z[idx].im, z[idx].re, tmp.re, tmp.im, exp[i].re, exp[i].im)
            z[idx].Im = tmp.Re * exp[i].Re - tmp.Im * exp[i].Im;
            z[idx].Re = tmp.Re * exp[i].Im + tmp.Im * exp[i].Re;
        }

        TxFft.FftNs(z, z, len2);

        for (int i = 0; i < len4; i++)
        {
            int i0 = len4 + i, i1 = len4 - i - 1;
            float src1Re = z[i1].Re, src1Im = z[i1].Im;
            float src0Re = z[i0].Re, src0Im = z[i0].Im;

            // CMUL(dst[2*i1 + 1], dst[2*i0], src0.re, src0.im, exp[i0].im, exp[i0].re)
            dst[2 * i1 + 1] = src0Re * exp[i0].Im - src0Im * exp[i0].Re;
            dst[2 * i0] = src0Re * exp[i0].Re + src0Im * exp[i0].Im;
            // CMUL(dst[2*i0 + 1], dst[2*i1], src1.re, src1.im, exp[i1].im, exp[i1].re)
            dst[2 * i0 + 1] = src1Re * exp[i1].Im - src1Im * exp[i1].Re;
            dst[2 * i1] = src1Re * exp[i1].Re + src1Im * exp[i1].Im;
        }
    }
}

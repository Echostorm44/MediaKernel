// MediaKernel - the AAC encoder's rate/distortion core
//
// A translation of the band cost family in libavcodec/aaccoder.c:
// quantize_and_encode_band_cost_template, the nine functions the
// QUANTIZE_AND_ENCODE_BAND_COST_FUNC macro instantiates from it, the dispatch table they sit
// in, and quantize_and_encode_band.
//
// This is where the encoder decides things. For every band and every candidate codebook it
// quantises, measures the squared error, counts the bits, and returns cost = error * lambda +
// bits; the searches above it pick the cheapest. A difference of one in the last place of any
// of this moves a band to another codebook, which is why ffmpeg's own SIMD build and its own C
// build produce different AAC files from the same input.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary>
/// The nine shapes <c>QUANTIZE_AND_ENCODE_BAND_COST_FUNC</c> instantiates the template in.
/// </summary>
/// <remarks>
/// The C passes seven ints and a float to an <c>av_always_inline</c> template and relies on the
/// compiler to fold them; C# gets the same effect from a generic over a struct with static
/// abstract members, which the JIT specialises per type argument so the branches disappear
/// exactly as the C's do. This is the project's form for a <c>#define</c> template, and
/// it keeps one copy of the algorithm rather than nine.
/// </remarks>
internal interface IAacBandCostShape
{
    /// <summary>Zero band: no spectral data at all.</summary>
    static abstract bool Zero { get; }

    /// <summary>The codebook codes magnitudes, with the signs written separately.</summary>
    static abstract bool Unsigned { get; }

    /// <summary>Two values per codeword rather than four.</summary>
    static abstract bool Pair { get; }

    /// <summary>The codebook has an escape for magnitudes it cannot carry.</summary>
    static abstract bool Escape { get; }

    /// <summary>Noise substitution: the band is coded as a level, not as coefficients.</summary>
    static abstract bool Noise { get; }

    /// <summary>Intensity stereo: the band comes from the other channel.</summary>
    static abstract bool Stereo { get; }

    /// <summary><c>ROUND_STANDARD</c> or <c>ROUND_TO_ZERO</c>.</summary>
    static abstract float Rounding { get; }
}

#pragma warning disable CS1591
internal readonly struct BandCostZero : IAacBandCostShape
{
    public static bool Zero => true;
    public static bool Unsigned => false;
    public static bool Pair => false;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostSquad : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => false;
    public static bool Pair => false;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostUquad : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => true;
    public static bool Pair => false;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostSpair : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => false;
    public static bool Pair => true;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostUpair : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => true;
    public static bool Pair => true;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostEsc : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => true;
    public static bool Pair => true;
    public static bool Escape => true;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostEscRtz : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => true;
    public static bool Pair => true;
    public static bool Escape => true;
    public static bool Noise => false;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundToZero;
}

internal readonly struct BandCostNoise : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => false;
    public static bool Pair => false;
    public static bool Escape => false;
    public static bool Noise => true;
    public static bool Stereo => false;
    public static float Rounding => AacEncUtils.RoundStandard;
}

internal readonly struct BandCostStereo : IAacBandCostShape
{
    public static bool Zero => false;
    public static bool Unsigned => false;
    public static bool Pair => false;
    public static bool Escape => false;
    public static bool Noise => false;
    public static bool Stereo => true;
    public static float Rounding => AacEncUtils.RoundStandard;
}
#pragma warning restore CS1591

/// <summary>
/// <c>aaccoder.c</c>: quantising a band, costing it, and writing it.
/// </summary>
internal static class AacCoder
{
    /// <summary>
    /// The scratch the band cost needs: <c>s->scoefs</c> and <c>s->qcoefs</c> in the C, which
    /// live on AACEncContext and are reused for every band of every frame.
    /// </summary>
    /// <remarks>
    /// Passed in rather than allocated here, for the same reason the C keeps them on the
    /// context: this runs for every candidate codebook of every band of every frame, and a
    /// per-call array would be a per-frame allocation several hundred times over.
    /// </remarks>
    internal sealed class BandCostScratch
    {
        public readonly float[] Scoefs = new float[1024];
        public readonly int[] Qcoefs = new int[1024];
    }

    /// <summary>
    /// <c>quantize_and_encode_band_cost_template</c>: the rate/distortion cost of coding one
    /// band with one codebook, and optionally the quantised values and the bitstream.
    /// </summary>
    /// <param name="scratch">The encoder's reusable buffers.</param>
    /// <param name="pb">Where to write the band, or null to only cost it.</param>
    /// <param name="input"><c>in</c>: the spectral coefficients.</param>
    /// <param name="output"><c>out</c>: the dequantised values, or empty for none.</param>
    /// <param name="scaled">
    /// <c>scaled</c>: |in|^(3/4), or empty to have it computed into the scratch.
    /// </param>
    /// <param name="size">Coefficients in the band.</param>
    /// <param name="scaleIdx">The band's scalefactor.</param>
    /// <param name="cb">The codebook, 1 to 11.</param>
    /// <param name="lambda">The rate/distortion trade-off.</param>
    /// <param name="uplim">Give up and return this once the cost exceeds it.</param>
    /// <param name="bits">Out: the bits the band costs, if wanted.</param>
    /// <param name="energy">Out: the energy of the quantised band, if wanted.</param>
    /// <returns>The quantisation distortion, weighted by lambda, plus the bits.</returns>
    public static float QuantizeAndEncodeBandCostTemplate<TShape>(
        BandCostScratch scratch, PutBitContext? pb, ReadOnlySpan<float> input,
        Span<float> output, ReadOnlySpan<float> scaled, int size, int scaleIdx, int cb,
        float lambda, float uplim, out int bits, out float energy)
        where TShape : IAacBandCostShape
    {
        int qIdx = AacEncUtils.PowSf2Zero - scaleIdx + AacEncUtils.ScaleOnePos - AacEncUtils.ScaleDiv512;
        float q = AacUsacTables.Pow2SfTab[qIdx];
        float q34 = AacEncTables.Pow34SfTab[qIdx];
        float iq = AacUsacTables.Pow2SfTab[AacEncUtils.PowSf2Zero + scaleIdx
            - AacEncUtils.ScaleOnePos + AacEncUtils.ScaleDiv512];
        float clippedEscape = 165140.0f * iq;

        float cost = 0;
        float qenergy = 0;
        int dim = TShape.Pair ? 2 : 4;
        int resbits = 0;

        if (TShape.Zero || TShape.Noise || TShape.Stereo)
        {
            for (int i = 0; i < size; i++)
                cost += input[i] * input[i];
            bits = 0;
            energy = qenergy;
            if (!output.IsEmpty)
            {
                for (int i = 0; i < size; i += dim)
                    for (int j = 0; j < dim; j++)
                        output[i + j] = 0.0f;
            }
            return cost * lambda;
        }

        if (scaled.IsEmpty)
        {
            AacEncDsp.AbsPow34V(scratch.Scoefs, input, size);
            scaled = scratch.Scoefs;
        }

        // aac_cb_maxval, the largest magnitude this CODEBOOK can carry - not aac_maxval_cb,
        // which is the other direction, the cheapest codebook for a given magnitude, and which
        // find_min_book uses. The two sit next to each other in aacenctab.h with names one
        // transposition apart, and using the wrong one here indexes the codebook table out of
        // bounds, which is how this was caught on the first run against ffmpeg.
        AacEncDsp.QuantizeBands(scratch.Qcoefs, input, scaled, size, !TShape.Unsigned,
                                AacEncTables.CbMaxval[cb], q34, TShape.Rounding);

        int off = TShape.Unsigned ? 0 : AacEncTables.CbMaxval[cb];
        ReadOnlySpan<float> vectors = AacEncTables.CodebookVectors(cb);
        ReadOnlySpan<byte> spectralBits = AacSpectralTables.Bits(cb);
        ReadOnlySpan<ushort> spectralCodes = AacSpectralTables.Codes(cb);
        int range = AacEncTables.CbRange[cb];

        for (int i = 0; i < size; i += dim)
        {
            Span<int> quants = scratch.Qcoefs.AsSpan(i);
            int curidx = 0;
            float rd = 0.0f;

            for (int j = 0; j < dim; j++)
            {
                curidx *= range;
                curidx += quants[j] + off;
            }

            int curbits = spectralBits[curidx];
            ReadOnlySpan<float> vec = vectors.Slice(curidx * dim, dim);

            if (TShape.Unsigned)
            {
                for (int j = 0; j < dim; j++)
                {
                    float t = MathF.Abs(input[i + j]);
                    float quantized;
                    if (TShape.Escape && vec[j] == AacEncTables.EscapeValue)
                    {
                        if (t >= clippedEscape)
                        {
                            quantized = clippedEscape;
                            curbits += 21;
                        }
                        else
                        {
                            int c = ClipUintp2(AacEncUtils.Quant(t, q, TShape.Rounding), 13);
                            quantized = c * MathF.Cbrt(c) * iq;
                            curbits += Log2(c) * 2 - 4 + 1;
                        }
                    }
                    else
                    {
                        quantized = vec[j] * iq;
                    }

                    float di = t - quantized;
                    if (!output.IsEmpty)
                        output[i + j] = input[i + j] >= 0 ? quantized : -quantized;
                    if (vec[j] != 0.0f)
                        curbits++;
                    qenergy += quantized * quantized;
                    rd += di * di;
                }
            }
            else
            {
                for (int j = 0; j < dim; j++)
                {
                    float quantized = vec[j] * iq;
                    qenergy += quantized * quantized;
                    if (!output.IsEmpty)
                        output[i + j] = quantized;
                    rd += (input[i + j] - quantized) * (input[i + j] - quantized);
                }
            }

            cost += rd * lambda + curbits;
            resbits += curbits;

            if (cost >= uplim)
            {
                // The C returns uplim here WITHOUT writing *bits or *energy, so a caller that
                // reads them past a give-up sees whatever it passed in. C# has to write them,
                // so they carry what was accumulated - the caller is expected to look at the
                // return value first, as every caller in aaccoder.c does.
                bits = resbits;
                energy = qenergy;
                return uplim;
            }

            if (pb != null)
            {
                pb.PutBits(spectralBits[curidx], spectralCodes[curidx]);
                if (TShape.Unsigned)
                {
                    for (int j = 0; j < dim; j++)
                    {
                        if (vectors[curidx * dim + j] != 0.0f)
                            pb.PutBits(1, input[i + j] < 0.0f ? 1u : 0u);
                    }
                }
                if (TShape.Escape)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        if (vectors[curidx * 2 + j] == AacEncTables.EscapeValue)
                        {
                            int coef = Math.Clamp(
                                AacEncUtils.Quant(MathF.Abs(input[i + j]), q, TShape.Rounding),
                                16, (1 << 13) - 1);
                            int len = Log2(coef);

                            pb.PutBits(len - 4 + 1, (uint)((1 << (len - 4 + 1)) - 2));
                            pb.PutSBits(len, coef);
                        }
                    }
                }
            }
        }

        bits = resbits;
        energy = qenergy;
        return cost;
    }

    /// <summary>
    /// <c>ff_quantize_and_encode_band_cost</c>: the dispatch the searches call, which picks the
    /// template instantiation from the codebook.
    /// </summary>
    /// <remarks>
    /// The C indexes a table of nine function pointers with the codebook number. The switch
    /// here is the same mapping written out, and it is what lets the JIT see a concrete type
    /// argument and fold the shape's branches away.
    /// </remarks>
    public static float QuantizeAndEncodeBandCost(
        BandCostScratch scratch, PutBitContext? pb, ReadOnlySpan<float> input,
        Span<float> output, ReadOnlySpan<float> scaled, int size, int scaleIdx, int cb,
        float lambda, float uplim, out int bits, out float energy,
        bool roundToZero = false)
    {
        return cb switch
        {
            0 => QuantizeAndEncodeBandCostTemplate<BandCostZero>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            1 or 2 => QuantizeAndEncodeBandCostTemplate<BandCostSquad>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            3 or 4 => QuantizeAndEncodeBandCostTemplate<BandCostUquad>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            5 or 6 => QuantizeAndEncodeBandCostTemplate<BandCostSpair>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            7 or 8 or 9 or 10 => QuantizeAndEncodeBandCostTemplate<BandCostUpair>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            // The C keeps two dispatch tables that differ in one entry: the escape book,
            // rounded to zero rather than normally. Every other codebook is the same
            // function in both, so the flag is carried here rather than duplicating fifteen
            // rows.
            11 when roundToZero => QuantizeAndEncodeBandCostTemplate<BandCostEscRtz>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            11 => QuantizeAndEncodeBandCostTemplate<BandCostEsc>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            // 12 is RESERVED_BT, which no band may use; 13 is noise and 14-15 intensity
            // stereo, all of which cost as "no spectral data".
            12 => throw new ArgumentOutOfRangeException(nameof(cb), cb,
                      "Codebook 12 is RESERVED_BT and cannot code a band."),
            13 => QuantizeAndEncodeBandCostTemplate<BandCostNoise>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            14 or 15 => QuantizeAndEncodeBandCostTemplate<BandCostStereo>(
                     scratch, pb, input, output, scaled, size, scaleIdx, cb, lambda, uplim,
                     out bits, out energy),
            _ => throw new ArgumentOutOfRangeException(nameof(cb), cb,
                     "AAC band types run from 0 to 15."),
        };
    }

    /// <summary>
    /// <c>quantize_and_encode_band</c>: write the band, discarding the cost.
    /// </summary>
    public static void QuantizeAndEncodeBand(
        BandCostScratch scratch, PutBitContext pb, ReadOnlySpan<float> input,
        Span<float> output, int size, int scaleIdx, int cb, float lambda, bool roundToZero) =>
        QuantizeAndEncodeBandCost(scratch, pb, input, output, [], size, scaleIdx, cb,
                                  lambda, float.PositiveInfinity, out _, out _, roundToZero);

    /// <summary>
    /// <c>quantize_band_cost</c> from <c>aacenc_quantization.h</c>: the cost alone.
    /// </summary>
    public static float QuantizeBandCost(
        BandCostScratch scratch, ReadOnlySpan<float> input, ReadOnlySpan<float> scaled,
        int size, int scaleIdx, int cb, float lambda, float uplim, out int bits,
        out float energy) =>
        QuantizeAndEncodeBandCost(scratch, null, input, [], scaled, size, scaleIdx, cb,
                                  lambda, uplim, out bits, out energy);

    /// <summary>
    /// <c>quantize_band_cost_bits</c>: the bits alone, which is the cost with lambda zeroed.
    /// </summary>
    public static int QuantizeBandCostBits(
        BandCostScratch scratch, ReadOnlySpan<float> input, ReadOnlySpan<float> scaled,
        int size, int scaleIdx, int cb, float lambda, float uplim)
    {
        QuantizeAndEncodeBandCost(scratch, null, input, [], scaled, size, scaleIdx, cb,
                                  0.0f, uplim, out int bits, out _);
        return bits;
    }

    /// <summary>
    /// <c>av_clip_uintp2</c>: clamp to an unsigned field of <paramref name="p"/> bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipUintp2(int a, int p)
    {
        if ((a & ~((1 << p) - 1)) != 0) return -a >> 31 & ((1 << p) - 1);
        return a;
    }

    /// <summary>
    /// <c>av_log2</c>: the index of the highest set bit, and zero for zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(int v) => v <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);
    /// <summary>
    /// C's <c>FLT_MIN</c>: the smallest NORMAL float. Not .NET's <c>float.Epsilon</c>, which
    /// is the smallest subnormal - about 10^30 times smaller - and which would make the
    /// guarded divisions below overflow where the C's does not.
    /// </summary>
    private const float FltMin = 1.17549435E-38f;

    /// <summary>
    /// <c>set_special_band_scalefactors</c>: the scalefactors of the bands that do not carry
    /// spectral data - intensity stereo and noise substitution - where the "scalefactor" is
    /// really the level the decoder reconstructs from.
    /// </summary>
    public static void SetSpecialBandScalefactors(AacSingleChannelElement sce)
    {
        int prevscalerN = -255, prevscalerI = 0;
        int bands = 0;

        for (int w = 0; w < sce.Ics.NumWindows; w += sce.Ics.GroupLen[w])
        {
            for (int g = 0; g < sce.Ics.NumSwb; g++)
            {
                if (sce.Zeroes[w * 16 + g] != 0)
                    continue;

                if (sce.BandType[w * 16 + g] == (int)AacBandType.Intensity
                    || sce.BandType[w * 16 + g] == (int)AacBandType.Intensity2)
                {
                    sce.SfIdx[w * 16 + g] = Math.Clamp(
                        (int)MathF.Round(MathF.Log2(sce.IsEner[w * 16 + g]) * 2), -155, 100);
                    bands++;
                }
                else if (sce.BandType[w * 16 + g] == (int)AacBandType.Noise)
                {
                    sce.SfIdx[w * 16 + g] = Math.Clamp(
                        3 + (int)MathF.Ceiling(MathF.Log2(sce.PnsEner[w * 16 + g]) * 2), -100, 155);
                    if (prevscalerN == -255)
                        prevscalerN = sce.SfIdx[w * 16 + g];
                    bands++;
                }
            }
        }

        if (bands == 0)
            return;

        // The deltas between consecutive special bands have to be codeable too.
        for (int w = 0; w < sce.Ics.NumWindows; w += sce.Ics.GroupLen[w])
        {
            for (int g = 0; g < sce.Ics.NumSwb; g++)
            {
                if (sce.Zeroes[w * 16 + g] != 0)
                    continue;

                if (sce.BandType[w * 16 + g] == (int)AacBandType.Intensity
                    || sce.BandType[w * 16 + g] == (int)AacBandType.Intensity2)
                {
                    sce.SfIdx[w * 16 + g] = prevscalerI = Math.Clamp(sce.SfIdx[w * 16 + g],
                        prevscalerI - AacEncUtils.ScaleMaxDiff,
                        prevscalerI + AacEncUtils.ScaleMaxDiff);
                }
                else if (sce.BandType[w * 16 + g] == (int)AacBandType.Noise)
                {
                    sce.SfIdx[w * 16 + g] = prevscalerN = Math.Clamp(sce.SfIdx[w * 16 + g],
                        prevscalerN - AacEncUtils.ScaleMaxDiff,
                        prevscalerN + AacEncUtils.ScaleMaxDiff);
                }
            }
        }
    }

    /// <summary>
    /// <c>search_for_ms</c>: which bands are cheaper coded as mid and side than as left and
    /// right.
    /// </summary>
    /// <remarks>
    /// Unlike intensity stereo, which discards one channel, mid/side keeps both and only
    /// changes their basis - so the search can afford to try four different scalefactors for
    /// the side channel and keep the cheapest, which is the <c>sid_sf_boost</c> loop.
    /// </remarks>
    public static void SearchForMs(BandCostScratch scratch, AacChannelElement cpe,
                                   ReadOnlySpan<AacPsyBand> psy0, ReadOnlySpan<AacPsyBand> psy1,
                                   float lambda)
    {
        AacSingleChannelElement sce0 = cpe.Ch[0];
        AacSingleChannelElement sce1 = cpe.Ch[1];

        // The C carves its one scratch buffer into six 128-float windows.
        Span<float> m = scratch.Scoefs.AsSpan(128 * 0, 128);
        Span<float> s = scratch.Scoefs.AsSpan(128 * 1, 128);
        Span<float> l34 = scratch.Scoefs.AsSpan(128 * 2, 128);
        Span<float> r34 = scratch.Scoefs.AsSpan(128 * 3, 128);
        Span<float> m34 = scratch.Scoefs.AsSpan(128 * 4, 128);
        Span<float> s34 = scratch.Scoefs.AsSpan(128 * 5, 128);

        float mslambda = MathF.Min(1.0f, lambda / 120.0f);
        Span<byte> nextband0 = stackalloc byte[128];
        Span<byte> nextband1 = stackalloc byte[128];

        if (cpe.CommonWindow == 0)
            return;

        AacEncUtils.InitNextbandMap(sce0.Ics.NumWindows, sce0.Ics.NumSwb, sce0.Ics.GroupLen,
                                    sce0.Zeroes, sce0.BandType, nextband0);
        AacEncUtils.InitNextbandMap(sce1.Ics.NumWindows, sce1.Ics.NumSwb, sce1.Ics.GroupLen,
                                    sce1.Zeroes, sce1.BandType, nextband1);

        int prevMid = sce0.SfIdx[0];
        int prevSide = sce1.SfIdx[0];

        for (int w = 0; w < sce0.Ics.NumWindows; w += sce0.Ics.GroupLen[w])
        {
            int start = 0;
            for (int g = 0; g < sce0.Ics.NumSwb; g++)
            {
                float bmax = AacEncUtils.Bval2Bmax(g * 17.0f / sce0.Ics.NumSwb) / 0.0045f;

                if (cpe.IsMask[w * 16 + g] == 0)
                    cpe.MsMask[w * 16 + g] = 0;

                if (sce0.Zeroes[w * 16 + g] == 0 && sce1.Zeroes[w * 16 + g] == 0
                    && cpe.IsMask[w * 16 + g] == 0)
                {
                    float mmax = 0.0f, smax = 0.0f;
                    int swbSize = sce0.Ics.SwbSizes[g];

                    // The mid/side scalefactor and codebook are chosen for the whole window
                    // group, so the maxima are taken across it before anything is costed.
                    for (int w2 = 0; w2 < sce0.Ics.GroupLen[w]; w2++)
                    {
                        for (int i = 0; i < swbSize; i++)
                        {
                            m[i] = (sce0.Coeffs[start + (w + w2) * 128 + i]
                                  + sce1.Coeffs[start + (w + w2) * 128 + i]) * 0.5f;
                            s[i] = m[i] - sce1.Coeffs[start + (w + w2) * 128 + i];
                        }
                        AacEncDsp.AbsPow34V(m34, m, swbSize);
                        AacEncDsp.AbsPow34V(s34, s, swbSize);
                        for (int i = 0; i < swbSize; i++)
                        {
                            mmax = MathF.Max(mmax, m34[i]);
                            smax = MathF.Max(smax, s34[i]);
                        }
                    }

                    for (int sidSfBoost = 0; sidSfBoost < 4; sidSfBoost++)
                    {
                        float dist1 = 0.0f, dist2 = 0.0f;
                        int b0 = 0, b1 = 0;

                        int minidx = Math.Min(sce0.SfIdx[w * 16 + g], sce1.SfIdx[w * 16 + g]);
                        int mididx = Math.Clamp(minidx, 0,
                            AacEncUtils.ScaleMaxPos - AacEncUtils.ScaleDiv512);
                        int sididx = Math.Clamp(minidx - sidSfBoost * 3, 0,
                            AacEncUtils.ScaleMaxPos - AacEncUtils.ScaleDiv512);

                        if (sce0.BandType[w * 16 + g] != (int)AacBandType.Noise
                            && sce1.BandType[w * 16 + g] != (int)AacBandType.Noise
                            && (!AacEncUtils.SfdeltaCanReplace(sce0.SfIdx, nextband0, prevMid,
                                                               mididx, w * 16 + g)
                                || !AacEncUtils.SfdeltaCanReplace(sce1.SfIdx, nextband1, prevSide,
                                                                  sididx, w * 16 + g)))
                        {
                            // A scalefactor the delta coding cannot express, which would cost
                            // more quality than mid/side could save.
                            continue;
                        }

                        int midcb = AacEncUtils.FindMinBook(mmax, mididx);
                        int sidcb = AacEncUtils.FindMinBook(smax, sididx);

                        // No codebook may be zero.
                        midcb = Math.Max(1, midcb);
                        sidcb = Math.Max(1, sidcb);

                        for (int w2 = 0; w2 < sce0.Ics.GroupLen[w]; w2++)
                        {
                            AacPsyBand band0 = psy0[(w + w2) * 16 + g];
                            AacPsyBand band1 = psy1[(w + w2) * 16 + g];
                            float minthr = MathF.Min(band0.Threshold, band1.Threshold);

                            for (int i = 0; i < swbSize; i++)
                            {
                                m[i] = (sce0.Coeffs[start + (w + w2) * 128 + i]
                                      + sce1.Coeffs[start + (w + w2) * 128 + i]) * 0.5f;
                                s[i] = m[i] - sce1.Coeffs[start + (w + w2) * 128 + i];
                            }

                            AacEncDsp.AbsPow34V(l34, sce0.Coeffs.AsSpan(start + (w + w2) * 128), swbSize);
                            AacEncDsp.AbsPow34V(r34, sce1.Coeffs.AsSpan(start + (w + w2) * 128), swbSize);
                            AacEncDsp.AbsPow34V(m34, m, swbSize);
                            AacEncDsp.AbsPow34V(s34, s, swbSize);

                            dist1 += QuantizeBandCost(scratch,
                                sce0.Coeffs.AsSpan(start + (w + w2) * 128), l34, swbSize,
                                sce0.SfIdx[w * 16 + g], sce0.BandType[w * 16 + g],
                                lambda / (band0.Threshold + FltMin),
                                float.PositiveInfinity, out int bits1, out _);
                            dist1 += QuantizeBandCost(scratch,
                                sce1.Coeffs.AsSpan(start + (w + w2) * 128), r34, swbSize,
                                sce1.SfIdx[w * 16 + g], sce1.BandType[w * 16 + g],
                                lambda / (band1.Threshold + FltMin),
                                float.PositiveInfinity, out int bits2, out _);
                            dist2 += QuantizeBandCost(scratch, m, m34, swbSize, mididx, midcb,
                                lambda / (minthr + FltMin), float.PositiveInfinity,
                                out int bits3, out _);
                            dist2 += QuantizeBandCost(scratch, s, s34, swbSize, sididx, sidcb,
                                mslambda / (minthr * bmax + FltMin),
                                float.PositiveInfinity, out int bits4, out _);

                            b0 += bits1 + bits2;
                            b1 += bits3 + bits4;
                            dist1 -= bits1 + bits2;
                            dist2 -= bits3 + bits4;
                        }

                        cpe.MsMask[w * 16 + g] = (byte)(dist2 <= dist1 && b1 < b0 ? 1 : 0);

                        if (cpe.MsMask[w * 16 + g] != 0)
                        {
                            if (sce0.BandType[w * 16 + g] != (int)AacBandType.Noise
                                && sce1.BandType[w * 16 + g] != (int)AacBandType.Noise)
                            {
                                sce0.SfIdx[w * 16 + g] = mididx;
                                sce1.SfIdx[w * 16 + g] = sididx;
                                sce0.BandType[w * 16 + g] = (byte)midcb;
                                sce1.BandType[w * 16 + g] = (byte)sidcb;
                            }
                            else if ((sce0.BandType[w * 16 + g] != (int)AacBandType.Noise)
                                     ^ (sce1.BandType[w * 16 + g] != (int)AacBandType.Noise))
                            {
                                // The mask would be unneeded here and confuses some decoders.
                                cpe.MsMask[w * 16 + g] = 0;
                            }
                            break;
                        }
                        else if (b1 > b0)
                        {
                            // More boost will not fix this band.
                            break;
                        }
                    }
                }

                if (sce0.Zeroes[w * 16 + g] == 0
                    && sce0.BandType[w * 16 + g] < (int)AacBandType.Reserved)
                    prevMid = sce0.SfIdx[w * 16 + g];
                if (sce1.Zeroes[w * 16 + g] == 0 && cpe.IsMask[w * 16 + g] == 0
                    && sce1.BandType[w * 16 + g] < (int)AacBandType.Reserved)
                    prevSide = sce1.SfIdx[w * 16 + g];

                start += sce0.Ics.SwbSizes[g];
            }
        }
    }
}

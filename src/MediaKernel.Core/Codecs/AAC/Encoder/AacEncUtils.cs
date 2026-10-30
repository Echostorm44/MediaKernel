// MediaKernel - AAC encoder utilities
//
// A translation of libavcodec/aacenc_utils.h. Small functions, and every one of them sits in a
// decision the encoder makes about which codebook a band is coded with, so they are translated
// literally: the same expression in the same order, the same float widths, and the same
// truncating conversions C does on the way to an int.
//
// Where C writes a float into an int variable it truncates toward zero, which is also what a
// C# cast does - but C does it implicitly and C# makes you write it, so every one of those
// casts here is in the C too. A Math.Round anywhere in this file would be a different encoder.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary>
/// <c>aacenc_utils.h</c>: the small shared helpers of FFmpeg's AAC encoder.
/// </summary>
internal static class AacEncUtils
{
    /// <summary><c>ROUND_STANDARD</c>.</summary>
    public const float RoundStandard = 0.4054f;

    /// <summary><c>ROUND_TO_ZERO</c>.</summary>
    public const float RoundToZero = 0.1054f;

    /// <summary><c>C_QUANT</c>.</summary>
    public const float CQuant = 0.4054f;

    /// <summary><c>SCALE_DIV_512</c>: the scalefactor difference worth a factor of 512.</summary>
    public const int ScaleDiv512 = 36;

    /// <summary><c>SCALE_ONE_POS</c>: the scalefactor index that means a gain of 1.0.</summary>
    public const int ScaleOnePos = 140;

    /// <summary><c>SCALE_MAX_POS</c>.</summary>
    public const int ScaleMaxPos = 255;

    /// <summary><c>SCALE_MAX_DIFF</c>: the largest difference the format can code.</summary>
    public const int ScaleMaxDiff = 60;

    /// <summary>
    /// <c>SCALE_DIFF_ZERO</c>: the codebook index a scalefactor difference of zero codes as.
    /// </summary>
    public const int ScaleDiffZero = 60;

    /// <summary><c>POW_SF2_ZERO</c>.</summary>
    public const int PowSf2Zero = 200;

    /// <summary>
    /// <c>pos_pow34</c>: a^(3/4) for a &gt;= 0, as two square roots rather than a call to
    /// <c>powf</c>.
    /// </summary>
    /// <remarks>
    /// Square root is the one transcendental IEEE-754 requires to be correctly rounded, so this
    /// is identical here and in the C - which is why the quantiser, built on this, can be held
    /// to byte equality at all.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float PosPow34(float a) => MathF.Sqrt(a * MathF.Sqrt(a));

    /// <summary>
    /// <c>quant</c>: one coefficient, quantised. 3GPP TS26.403 5.6.2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Quant(float coef, float q, float rounding)
    {
        float a = coef * q;
        return (int)(MathF.Sqrt(a * MathF.Sqrt(a)) + rounding);
    }

    /// <summary><c>find_max_val</c>: the largest scaled coefficient in a band.</summary>
    public static float FindMaxVal(int groupLen, int swbSize, ReadOnlySpan<float> scaled)
    {
        float maxval = 0.0f;
        for (int w2 = 0; w2 < groupLen; w2++)
        {
            for (int i = 0; i < swbSize; i++)
                maxval = MathF.Max(maxval, scaled[w2 * 128 + i]);
        }
        return maxval;
    }

    /// <summary>
    /// <c>find_min_book</c>: the cheapest codebook that can carry a band's largest value.
    /// </summary>
    public static int FindMinBook(float maxval, int sf)
    {
        float q34 = AacEncTables.Pow34SfTab[PowSf2Zero - sf + ScaleOnePos - ScaleDiv512];
        int qmaxval = (int)(maxval * q34 + CQuant);
        return qmaxval >= AacEncTables.MaxvalCb.Length ? 11 : AacEncTables.MaxvalCb[qmaxval];
    }

    /// <summary>
    /// <c>find_form_factor</c>: how peaky a band is, which decides how much of the bit budget
    /// the two-loop search is willing to spend on it.
    /// </summary>
    public static float FindFormFactor(int groupLen, int swbSize, float thresh,
                                       ReadOnlySpan<float> scaled, float nzslope)
    {
        float iswbSize = 1.0f / swbSize;
        float iswbSizem1 = 1.0f / (swbSize - 1);
        float ethresh = thresh;
        float form = 0.0f, weight = 0.0f;

        for (int w2 = 0; w2 < groupLen; w2++)
        {
            float e = 0.0f, e2 = 0.0f, var = 0.0f, maxval = 0.0f;
            float nzl = 0;

            for (int i = 0; i < swbSize; i++)
            {
                float s = MathF.Abs(scaled[w2 * 128 + i]);
                maxval = MathF.Max(maxval, s);
                e += s;
                e2 += s *= s;
                // Not a hard non-zero-line count: lines below the threshold still add to the
                // band's power, so this falls steeply towards zero but smoothly.
                if (s >= ethresh)
                {
                    nzl += 1.0f;
                }
                else
                {
                    if (nzslope == 2.0f)
                        nzl += (s / ethresh) * (s / ethresh);
                    else
                        nzl += FastPowF(s / ethresh, nzslope);
                }
            }

            if (e2 > thresh)
            {
                e *= iswbSize;

                // the variance
                for (int i = 0; i < swbSize; i++)
                {
                    float d = MathF.Abs(scaled[w2 * 128 + i]) - e;
                    var += d * d;
                }
                var = MathF.Sqrt(var * iswbSizem1);

                e2 *= iswbSize;
                float frm = e / MathF.Min(e + 4 * var, maxval);
                form += e2 * MathF.Sqrt(frm) / MathF.Max(0.5f, nzl);
                weight += e2;
            }
        }

        return weight > 0 ? form / weight : 1.0f;
    }

    /// <summary>
    /// <c>ff_fast_powf</c> from <c>libavutil/ffmath.h</c>: x^y as <c>expf(logf(x) * y)</c>.
    /// </summary>
    /// <remarks>
    /// FFmpeg's own comment says this handles no edge cases and is less accurate than a libm
    /// <c>powf</c>; both of those are the point, because the encoder's decisions depend on the
    /// value it actually computes and not on the value it was approximating.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastPowF(float x, float y) => MathF.Exp(MathF.Log(x) * y);

    /// <summary>
    /// <c>coef2minsf</c>: the smallest scalefactor at which a coefficient does not clip.
    /// </summary>
    public static byte Coef2MinSf(float coef) =>
        ClipUint8((int)(MathF.Log2(coef) * 4 - 69 + ScaleOnePos - ScaleDiv512));

    /// <summary>
    /// <c>coef2maxsf</c>: the largest scalefactor at which a coefficient is still non-zero.
    /// </summary>
    public static byte Coef2MaxSf(float coef) =>
        ClipUint8((int)(MathF.Log2(coef) * 4 + 6 + ScaleOnePos - ScaleDiv512));

    /// <summary>
    /// <c>av_clip_uint8</c>, as the bit trick FFmpeg writes rather than as a conditional - the
    /// two agree, and keeping the C's form keeps the translation checkable line by line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClipUint8(int a)
    {
        if ((a & ~0xFF) != 0) return (byte)((~a) >> 31);
        return (byte)a;
    }

    /// <summary>
    /// <c>quant_array_idx</c>: the index of the closest value in an array.
    /// </summary>
    public static int QuantArrayIdx(float val, ReadOnlySpan<float> arr, int num)
    {
        int index = 0;
        float quantMinErr = float.PositiveInfinity;
        for (int i = 0; i < num; i++)
        {
            float error = (val - arr[i]) * (val - arr[i]);
            if (error < quantMinErr)
            {
                quantMinErr = error;
                index = i;
            }
        }
        return index;
    }

    /// <summary>
    /// <c>bval2bmax</c>: approximates
    /// <c>exp10f(-3(0.5 + 0.5 cosf(min(b, 15.5) / 15.5)))</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Bval2Bmax(float b) => 0.001f + 0.0035f * (b * b * b) / (15.5f * 15.5f * 15.5f);

    /// <summary>
    /// <c>ff_init_nextband_map</c>: for each band, the next non-zero band after it, which the
    /// scalefactor delta constraint walks. 128 entries; positions that are not the first window
    /// of a group are left as they fall.
    /// </summary>
    public static void InitNextbandMap(int numWindows, int numSwb, ReadOnlySpan<byte> groupLen,
                                       ReadOnlySpan<byte> zeroes, ReadOnlySpan<byte> bandType,
                                       Span<byte> nextband)
    {
        byte prevband = 0;

        // Just a safe default.
        for (int g = 0; g < 128; g++)
            nextband[g] = (byte)g;

        // Now really navigate the non-zero band chain.
        for (int w = 0; w < numWindows; w += groupLen[w])
        {
            for (int g = 0; g < numSwb; g++)
            {
                if (zeroes[w * 16 + g] == 0 && bandType[w * 16 + g] < (int)AacBandType.Reserved)
                    prevband = nextband[prevband] = (byte)(w * 16 + g);
            }
        }

        nextband[prevband] = prevband;      // terminate
    }

    /// <summary>
    /// <c>ff_nextband_remove</c>: the map after a band has been zeroed, without rebuilding it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NextbandRemove(Span<byte> nextband, int prevband, int band) =>
        nextband[prevband] = nextband[band];

    /// <summary>
    /// <c>ff_sfdelta_can_remove_band</c>: whether zeroing a band would leave a scalefactor
    /// delta the format cannot code. <paramref name="prevSf"/> is negative if there is no
    /// previous non-zero, non-special band.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SfdeltaCanRemoveBand(ReadOnlySpan<int> sfIdx, ReadOnlySpan<byte> nextband,
                                            int prevSf, int band) =>
        prevSf >= 0
        && sfIdx[nextband[band]] >= prevSf - ScaleMaxDiff
        && sfIdx[nextband[band]] <= prevSf + ScaleMaxDiff;

    /// <summary>
    /// <c>ff_sfdelta_can_replace</c>: the same question for a changed scalefactor rather than
    /// a removed band.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SfdeltaCanReplace(ReadOnlySpan<int> sfIdx, ReadOnlySpan<byte> nextband,
                                         int prevSf, int newSf, int band) =>
        newSf >= prevSf - ScaleMaxDiff
        && newSf <= prevSf + ScaleMaxDiff
        && sfIdx[nextband[band]] >= newSf - ScaleMaxDiff
        && sfIdx[nextband[band]] <= newSf + ScaleMaxDiff;

    /// <summary>
    /// <c>lcg_random</c>: the linear congruential generator the noise substitution uses. The
    /// multiply is meant to wrap, and the result is reinterpreted as signed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LcgRandom(uint previousVal) => (int)(previousVal * 1664525u + 1013904223u);
}

/// <summary>
/// <c>enum BandType</c> from <c>aac.h</c>, as the encoder uses it.
/// </summary>
internal enum AacBandType
{
    Zero = 0,
    Escape = 11,
    Reserved = 12,
    Noise = 13,
    Intensity2 = 14,
    Intensity = 15,
}

// aacpsdsp_template.c - the seven kernels Parametric Stereo spends its time in.
//
// Scalar, in FFmpeg's order of operations. The float build's AAC_MUL/AAC_MADD macros are plain
// multiplies and multiply-adds, so what matters here is the order of the sums: several of these
// accumulate six or eight products into one float, and reassociating them moves the last bit.
//
// Where the C passes a pointer into the middle of an array - delay[k] + PS_MAX_DELAY - 2, out + 27
// - this takes the array and an offset, because that is the nearest thing C# has and hiding it in
// a slice would copy.

using System;

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary>The PS DSP kernels, as <c>PSDSPContext</c> wires them up in the float build.</summary>
internal static class PsDsp
{
    // Inline arrays rather than stackalloc: a method containing localloc never reaches Tier1 or
    // Dynamic PGO, and these two run 91 and 50 times per frame.
    [System.Runtime.CompilerServices.InlineArray(6)]
    private struct Float6 { private float element0; }

    [System.Runtime.CompilerServices.InlineArray(PsLimits.ApLinks)]
    private struct Float3 { private float element0; }

    /// <summary><c>ps_add_squares_c</c>: accumulate the magnitude squared of a complex row.</summary>
    public static void AddSquares(float[] dst, float[][] src, int n)
    {
        for (int i = 0; i < n; i++)
            dst[i] += src[i][0] * src[i][0] + src[i][1] * src[i][1];
    }

    /// <summary><c>ps_mul_pair_single_c</c>: scale a complex row by a real one.</summary>
    public static void MulPairSingle(float[][] dst, float[][] src0, int src0Offset,
                                     float[] src1, int n)
    {
        for (int i = 0; i < n; i++)
        {
            dst[i][0] = src0[src0Offset + i][0] * src1[i];
            dst[i][1] = src0[src0Offset + i][1] * src1[i];
        }
    }

    /// <summary>
    /// <c>ps_hybrid_analysis_c</c>: one 13-tap complex filter bank evaluated at <paramref name="n"/>
    /// band centres, producing <paramref name="n"/> complex outputs.
    /// </summary>
    /// <remarks>
    /// The C scatters its results through a pointer stride, which both callers use differently -
    /// stride 1 into an eight-entry scratch, stride 32 into successive sub-subbands. Here the
    /// results land in two spans and the caller scatters them, which keeps the arithmetic in one
    /// place; <paramref name="n"/> is at most 12.
    /// </remarks>
    public static void HybridAnalysis(float[][] input, int inputOffset, float[][][] filter,
                                      int n, Span<float> outRe, Span<float> outIm)
    {
        Float6 inre0Store = default, inre1Store = default;
        Float6 inim0Store = default, inim1Store = default;
        Span<float> inre0 = inre0Store;
        Span<float> inre1 = inre1Store;
        Span<float> inim0 = inim0Store;
        Span<float> inim1 = inim1Store;

        for (int j = 0; j < 6; j++)
        {
            float[] a = input[inputOffset + j];
            float[] b = input[inputOffset + 12 - j];
            inre0[j] = a[0] + b[0];
            inre1[j] = a[1] - b[1];
            inim0[j] = a[1] + b[1];
            inim1[j] = a[0] - b[0];
        }

        float[] centre = input[inputOffset + 6];

        for (int i = 0; i < n; i++)
        {
            float[][] f = filter[i];
            float sumRe = f[6][0] * centre[0];
            float sumIm = f[6][0] * centre[1];

            for (int j = 0; j < 6; j++)
            {
                sumRe += f[j][0] * inre0[j] - f[j][1] * inre1[j];
                sumIm += f[j][0] * inim0[j] + f[j][1] * inim1[j];
            }

            outRe[i] = sumRe;
            outIm[i] = sumIm;
        }
    }

    /// <summary>
    /// <c>ps_hybrid_analysis_ileave_c</c>: the bands above the hybrid split pass through, turned
    /// from the QMF layout <c>[re/im][slot][band]</c> into the PS layout <c>[band][slot][re/im]</c>.
    /// </summary>
    public static void HybridAnalysisIleave(float[][][] outBands, int bandOffset,
                                            float[][][] left, int i, int len)
    {
        for (; i < 64; i++)
        {
            float[][] band = outBands[bandOffset + i];
            for (int j = 0; j < len; j++)
            {
                band[j][0] = left[0][j][i];
                band[j][1] = left[1][j][i];
            }
        }
    }

    /// <summary><c>ps_hybrid_synthesis_deint_c</c>: the same pass-through, back again.</summary>
    public static void HybridSynthesisDeint(float[][][] output, float[][][] inBands,
                                            int bandOffset, int i, int len)
    {
        for (; i < 64; i++)
        {
            float[][] band = inBands[bandOffset + i];
            for (int n = 0; n < len; n++)
            {
                output[0][n][i] = band[n][0];
                output[1][n][i] = band[n][1];
            }
        }
    }

    /// <summary>All-pass link gains, <c>a[]</c> in <c>ps_decorrelate_c</c>.</summary>
    private static readonly float[] LinkGain =
        [0.65143905753106f, 0.56471812200776f, 0.48954165955695f];

    /// <summary>
    /// <c>ps_decorrelate_c</c>: three cascaded fractional-delay all-pass sections, then the
    /// transient gain.
    /// </summary>
    public static void Decorrelate(float[][] output, float[][] delay, int delayOffset,
                                   float[][][] apDelay, float[] phiFract, float[][] qFract,
                                   float[] transientGain, float gDecaySlope, int len)
    {
        Float3 agStore = default;
        Span<float> ag = agStore;
        for (int m = 0; m < PsLimits.ApLinks; m++)
            ag[m] = LinkGain[m] * gDecaySlope;

        for (int n = 0; n < len; n++)
        {
            float[] d = delay[delayOffset + n];
            float inRe = d[0] * phiFract[0] - d[1] * phiFract[1];
            float inIm = d[0] * phiFract[1] + d[1] * phiFract[0];

            for (int m = 0; m < PsLimits.ApLinks; m++)
            {
                float aRe = ag[m] * inRe;
                float aIm = ag[m] * inIm;
                float[] link = apDelay[m][n + 2 - m];
                float linkDelayRe = link[0];
                float linkDelayIm = link[1];
                float fractionalDelayRe = qFract[m][0];
                float fractionalDelayIm = qFract[m][1];
                float apdRe = inRe;
                float apdIm = inIm;
                inRe = linkDelayRe * fractionalDelayRe - linkDelayIm * fractionalDelayIm;
                inRe -= aRe;
                inIm = linkDelayRe * fractionalDelayIm + linkDelayIm * fractionalDelayRe;
                inIm -= aIm;
                float[] store = apDelay[m][n + 5];
                store[0] = apdRe + ag[m] * inRe;
                store[1] = apdIm + ag[m] * inIm;
            }

            output[n][0] = transientGain[n] * inRe;
            output[n][1] = transientGain[n] * inIm;
        }
    }

    /// <summary>
    /// <c>ps_stereo_interpolate_c</c>: apply the 2x2 mixing matrix, stepping it once per slot so
    /// it ramps linearly to the next envelope's value.
    /// </summary>
    public static void StereoInterpolate(float[][] l, int lOffset, float[][] r, int rOffset,
                                         float[][] h, float[][] hStep, int len)
    {
        float h0 = h[0][0], h1 = h[0][1], h2 = h[0][2], h3 = h[0][3];
        float hs0 = hStep[0][0], hs1 = hStep[0][1], hs2 = hStep[0][2], hs3 = hStep[0][3];

        for (int n = 0; n < len; n++)
        {
            float[] lv = l[lOffset + n];
            float[] rv = r[rOffset + n];
            float lRe = lv[0], lIm = lv[1], rRe = rv[0], rIm = rv[1];
            h0 += hs0;
            h1 += hs1;
            h2 += hs2;
            h3 += hs3;
            lv[0] = h0 * lRe + h2 * rRe;
            lv[1] = h0 * lIm + h2 * rIm;
            rv[0] = h1 * lRe + h3 * rRe;
            rv[1] = h1 * lIm + h3 * rIm;
        }
    }

    /// <summary><c>ps_stereo_interpolate_ipdopd_c</c>: the same with the imaginary half live.</summary>
    public static void StereoInterpolateIpdOpd(float[][] l, int lOffset, float[][] r, int rOffset,
                                               float[][] h, float[][] hStep, int len)
    {
        float h00 = h[0][0], h10 = h[1][0];
        float h01 = h[0][1], h11 = h[1][1];
        float h02 = h[0][2], h12 = h[1][2];
        float h03 = h[0][3], h13 = h[1][3];
        float hs00 = hStep[0][0], hs10 = hStep[1][0];
        float hs01 = hStep[0][1], hs11 = hStep[1][1];
        float hs02 = hStep[0][2], hs12 = hStep[1][2];
        float hs03 = hStep[0][3], hs13 = hStep[1][3];

        for (int n = 0; n < len; n++)
        {
            float[] lv = l[lOffset + n];
            float[] rv = r[rOffset + n];
            float lRe = lv[0], lIm = lv[1], rRe = rv[0], rIm = rv[1];
            h00 += hs00;
            h01 += hs01;
            h02 += hs02;
            h03 += hs03;
            h10 += hs10;
            h11 += hs11;
            h12 += hs12;
            h13 += hs13;

            // AAC_MSUB30_V8(x,y,a,b,c,d,e,f) is x*y + a*b - c*d - e*f; the V8 add form is
            // x*y + a*b + c*d + e*f. Written out because the macro names read backwards.
            lv[0] = h00 * lRe + h02 * rRe - h10 * lIm - h12 * rIm;
            lv[1] = h00 * lIm + h02 * rIm + h10 * lRe + h12 * rRe;
            rv[0] = h01 * lRe + h03 * rRe - h11 * lIm - h13 * rIm;
            rv[1] = h01 * lIm + h03 * rIm + h11 * lRe + h13 * rRe;
        }
    }
}

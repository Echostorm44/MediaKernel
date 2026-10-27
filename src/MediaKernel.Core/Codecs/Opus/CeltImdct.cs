// Licensed under the MIT License.
// IMDCT implementation for CELT decoder — supports sizes 120, 240, 480, 960.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Inverse Modified Discrete Cosine Transform for CELT.
/// Supports non-power-of-2 sizes: 120, 240, 480, 960 (= 15 × 2^n).
/// Matches FFmpeg's av_tx half-length IMDCT exactly (ff_tx_mdct_naive_inv).
/// N spectral → N time-domain samples. Scale = -1/32768.
/// </summary>
public sealed class CeltImdct
{
    private const float ImdctScale = -1.0f / 32768.0f;

    private readonly int halfLen;     // N/2  (FFmpeg's "len")
    private readonly int fullLen;     // N    (FFmpeg's "len2")
    private readonly float[] twiddleD; // cosines for lower half [halfLen * fullLen]
    private readonly float[] twiddleU; // cosines for upper half [halfLen * fullLen]

    /// <summary>Spectral coefficient count and output sample count (N).</summary>
    public int Size => fullLen;

    /// <summary>
    /// Creates a half-length IMDCT instance for a given spectral size.
    /// </summary>
    /// <param name="spectralSize">Number of spectral coefficients (120, 240, 480, or 960).</param>
    public CeltImdct(int spectralSize)
    {
        fullLen = spectralSize;
        halfLen = spectralSize >> 1;

        // FFmpeg's formula from ff_tx_mdct_naive_inv:
        // phase = PI / (4 * len2) where len2 = spectralSize
        // lower half: cos((2j+1) * phase * (4*halfLen - 2*i - 1))
        // upper half: cos((2j+1) * phase * (3*fullLen + 2*i + 1))
        double phase = Math.PI / (4.0 * fullLen);

        twiddleD = new float[halfLen * fullLen];
        twiddleU = new float[halfLen * fullLen];

        for (int i = 0; i < halfLen; i++)
        {
            double factorD = phase * (4 * halfLen - 2 * i - 1);
            double factorU = phase * (3 * fullLen + 2 * i + 1);

            for (int j = 0; j < fullLen; j++)
            {
                double a = 2.0 * j + 1.0;
                twiddleD[i * fullLen + j] = (float)Math.Cos(a * factorD);
                twiddleU[i * fullLen + j] = (float)Math.Cos(a * factorU);
            }
        }
    }

    /// <summary>
    /// Computes the half-length IMDCT: N spectral coefficients → N time-domain samples.
    /// This matches FFmpeg's av_tx IMDCT convention (ff_tx_mdct_naive_inv).
    /// </summary>
    /// <param name="input">N spectral coefficients (may be strided).</param>
    /// <param name="output">N time-domain output samples.</param>
    /// <param name="inputStride">Stride between input elements (1 for contiguous).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Compute(ReadOnlySpan<float> input, Span<float> output, int inputStride = 1)
    {
        for (int i = 0; i < halfLen; i++)
        {
            float sumD = 0, sumU = 0;
            int twBase = i * fullLen;

            if (inputStride == 1)
            {
                for (int j = 0; j < fullLen; j++)
                {
                    float val = input[j];
                    sumD += twiddleD[twBase + j] * val;
                    sumU += twiddleU[twBase + j] * val;
                }
            }
            else
            {
                for (int j = 0; j < fullLen; j++)
                {
                    float val = input[j * inputStride];
                    sumD += twiddleD[twBase + j] * val;
                    sumU += twiddleU[twBase + j] * val;
                }
            }

            output[i] = sumD * ImdctScale;
            output[i + halfLen] = -sumU * ImdctScale;
        }
    }
}

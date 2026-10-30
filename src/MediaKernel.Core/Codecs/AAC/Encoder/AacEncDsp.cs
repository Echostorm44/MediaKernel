// MediaKernel - the AAC encoder's two inner kernels
//
// A translation of libavcodec/aacencdsp.h. FFmpeg dispatches these to SSE2, NEON or RVV at run
// time; this tree translates the C, because the C is what the bitstream has to agree with.
//
// That is not a preference. ffmpeg's own assembly and its own C produce DIFFERENT AAC files
// from the same input - 32,951 bytes against 32,944 on two seconds of a sine - because these
// two functions accumulate in a different order and a last-place difference moves a band to
// another codebook. Any SIMD written here later has to reproduce the C's exact arithmetic and
// be held to it by the same gate, not merely be fast.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary><c>aacencdsp.h</c>: absolute value to the 3/4, and band quantisation.</summary>
internal static class AacEncDsp
{
    /// <summary>
    /// <c>abs_pow34_v</c>: |in|^(3/4), the magnitude the quantiser and the cost estimator both
    /// work in.
    /// </summary>
    public static void AbsPow34V(Span<float> output, ReadOnlySpan<float> input, int size)
    {
        for (int i = 0; i < size; i++)
        {
            float a = MathF.Abs(input[i]);
            output[i] = MathF.Sqrt(a * MathF.Sqrt(a));
        }
    }

    /// <summary>
    /// <c>quantize_bands</c>: scaled magnitudes to quantised integers, clamped at the
    /// codebook's largest value and signed from the original coefficients.
    /// </summary>
    /// <remarks>
    /// The clamp happens in float and the truncation after it, which is the C's
    /// <c>(int)FFMIN((float)(qc + rounding), (float)maxval)</c>. Clamping the integer instead
    /// cannot be told apart here by any input: truncation commutes with an integer clamp in
    /// range, and out of range .NET's float-to-int conversion saturates where C's is
    /// undefined. It is written this way because that is what the C says, and no test can
    /// hold it to that - the perturbation sweep says so rather than leaving it to be assumed.
    /// </remarks>
    public static void QuantizeBands(Span<int> output, ReadOnlySpan<float> input,
                                     ReadOnlySpan<float> scaled, int size, bool isSigned,
                                     int maxval, float q34, float rounding)
    {
        for (int i = 0; i < size; i++)
        {
            float qc = scaled[i] * q34;
            int tmp = (int)MathF.Min(qc + rounding, maxval);
            if (isSigned && input[i] < 0.0f)
                tmp = -tmp;
            output[i] = tmp;
        }
    }
}

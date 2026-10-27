// Licensed under the MIT License.
// Vorbis IMDCT (Inverse Modified Discrete Cosine Transform) implementation.
// Matches FFmpeg's imdct_half: produces N/2 unwindowed values from N/2 spectral coefficients.
// Window is applied during overlap-add by VorbisDecoder, not inside the transform.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>
/// Inverse MDCT for Vorbis decoding.
/// Produces N/2 unwindowed time-domain samples from N/2 spectral coefficients,
/// matching FFmpeg's imdct_half convention.
/// </summary>
public class VorbisMdct
{
    private readonly int blockSize;   // N (full block size)
    private readonly int halfSize;    // N/2
    private readonly int quarterSize; // N/4

    // Precomputed Vorbis window (N/2 values, matching FFmpeg's ff_vorbis_vwin)
    // win[i] = sin(π/2 * sin²(π * (i + 0.5) / (N/2)))  for i = 0..N/2-1
    private readonly float[] window;

    /// <summary>
    /// Creates an MDCT transformer for the specified block size.
    /// </summary>
    public VorbisMdct(int blockSize)
    {
        if ((blockSize & (blockSize - 1)) != 0 || blockSize < 64)
            throw new ArgumentException("Block size must be power of 2 >= 64", nameof(blockSize));

        this.blockSize = blockSize;
        halfSize = blockSize / 2;
        quarterSize = blockSize / 4;

        window = new float[halfSize];
        ComputeWindow();
    }

    /// <summary>
    /// Computes the Vorbis window function (N/2 values).
    /// Matches FFmpeg's ff_vorbis_vwin: sin(π/2 * sin²(π * (i + 0.5) / N))
    /// where N is the full block size.
    /// </summary>
    private void ComputeWindow()
    {
        for (int i = 0; i < halfSize; i++)
        {
            double x = (i + 0.5) / blockSize * Math.PI;
            double sinVal = Math.Sin(x);
            sinVal *= sinVal;
            window[i] = (float)Math.Sin(Math.PI * 0.5 * sinVal);
        }
    }

    /// <summary>
    /// Performs inverse MDCT half-transform (matching FFmpeg's imdct_half with scale=-1).
    /// Produces N/2 unwindowed time-domain values from N/2 spectral coefficients.
    /// </summary>
    /// <param name="input">Spectral coefficients (N/2 elements).</param>
    /// <param name="output">Time-domain output (N/2 elements, unwindowed).</param>
    public void InverseMdctHalf(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length < halfSize)
            throw new ArgumentException($"Input must have at least {halfSize} elements", nameof(input));
        if (output.Length < halfSize)
            throw new ArgumentException($"Output must have at least {halfSize} elements", nameof(output));

        // imdct_half matching FFmpeg's output convention for vector_fmul_window overlap-add.
        //
        // Derived from the standard IMDCT: x[n] = Σ X[k]*cos(π*(2n+1+N/2)*(2k+1)/(2N))
        // FFmpeg's imdct_half reorders the N/2 output as:
        //   First quarter:  half[i] = -x[N/4-1-i]     for i = 0..N/4-1
        //   Second quarter: half[N/4+i] = x[N/2+i]    for i = 0..N/4-1
        // Which gives the cosine phases:
        //   First:  π*(N-2i-1)*(2k+1)/(2N)     (negated sum)
        //   Second: π*(3N/2+2i+1)*(2k+1)/(2N)  (positive sum)

        double piOver2N = Math.PI / (2.0 * blockSize);

        for (int i = 0; i < quarterSize; i++)
        {
            double phase = piOver2N * (blockSize - 2 * i - 1);
            double sum = 0;
            for (int k = 0; k < halfSize; k++)
                sum += input[k] * Math.Cos((2 * k + 1) * phase);
            output[i] = (float)(-sum);
        }

        for (int i = 0; i < quarterSize; i++)
        {
            double phase = piOver2N * (3 * halfSize + 2 * i + 1);
            double sum = 0;
            for (int k = 0; k < halfSize; k++)
                sum += input[k] * Math.Cos((2 * k + 1) * phase);
            output[quarterSize + i] = (float)sum;
        }
    }

    /// <summary>
    /// Applies windowed overlap-add matching FFmpeg's vector_fmul_window.
    /// Combines saved (previous frame) with buf (current IMDCT output) using the window.
    /// </summary>
    /// <param name="dst">Output buffer (2*len values).</param>
    /// <param name="saved">Previous frame's saved overlap values (len values).</param>
    /// <param name="buf">Current frame's IMDCT half output (len values).</param>
    /// <param name="win">Full window function (2*len values, matching FFmpeg's vwin).</param>
    /// <param name="len">Overlap length (N/4 for equal blocks, bs0/4 for transitions).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VectorFmulWindow(Span<float> dst, ReadOnlySpan<float> saved, 
                                         ReadOnlySpan<float> buf, ReadOnlySpan<float> win, int len)
    {
        // Matches FFmpeg's vector_fmul_window_c exactly:
        // After pointer offsets: dst+=len, win+=len, src0+=len
        // Loop i from -len to -1, j from len-1 to 0:
        //   dst[i] = src0[i]*win[j] - src1[j]*win[i]  (uses win[0..len-1] and win[len..2*len-1])
        //   dst[j] = src0[i]*win[i] + src1[j]*win[j]
        //
        // In zero-based (k=0..len-1): j=len-1-k
        //   dst[k]         = saved[k]*win[2*len-1-k] - buf[len-1-k]*win[k]
        //   dst[2*len-1-k] = saved[k]*win[k]         + buf[len-1-k]*win[2*len-1-k]
        for (int i = 0; i < len; i++)
        {
            float s0 = saved[i];
            float s1 = buf[len - 1 - i];
            float wi = win[i];
            float wj = win[2 * len - 1 - i];
            dst[i] = s0 * wj - s1 * wi;
            dst[2 * len - 1 - i] = s0 * wi + s1 * wj;
        }
    }

    /// <summary>
    /// Gets the Vorbis window for this block size (N/2 values).
    /// </summary>
    public ReadOnlySpan<float> Window => window;

    /// <summary>
    /// Gets the block size (N).
    /// </summary>
    public int BlockSize => blockSize;

    /// <summary>
    /// Gets the half block size (N/2).
    /// </summary>
    public int HalfSize => halfSize;

    /// <summary>
    /// Gets the quarter block size (N/4).
    /// </summary>
    public int QuarterSize => quarterSize;
}

using System;

namespace MediaKernel.Core.Codecs.Audio.Flac;

/// <summary>
/// FFmpeg's <c>flacencdsp.c</c> and <c>flacdsp_lpc_template.c</c>: the LPC residual kernel the
/// FLAC encoder runs over every candidate predictor.
/// </summary>
/// <remarks>
/// The C builds the template twice, for <c>SAMPLE_SIZE</c> 16 and 32, which differ only in the
/// accumulator width and whether the shifted sum is clipped to int32. That is two C# methods, as
/// it is two C functions. The hand-unrolled two-samples-per-iteration shape is kept: a plain
/// loop would give different results at the last sample of an odd-length block, because the C
/// writes <c>res[i+1]</c> unconditionally and the caller sizes the residual array to allow it.
/// </remarks>
public static class FlacEncDsp
{
    /// <summary>
    /// flac_lpc_encode_c_16: the 16-bit path, whose products fit a 32-bit accumulator so no
    /// clipping is needed.
    /// </summary>
    public static void FlacLpcEncodeC16(Span<int> res, ReadOnlySpan<int> smp, int len, int order,
                                        ReadOnlySpan<int> coefs, int shift)
    {
        for (int i = 0; i < order; i++)
            res[i] = smp[i];

        for (int i = order; i < len; i += 2)
        {
            int s = smp[i - order];
            int p0 = 0, p1 = 0;

            // LPC1(x), counting down from `order` exactly as the C's switch fallthrough does.
            for (int x = order; x >= 1; x--)
            {
                int c = coefs[x - 1];
                p0 += c * s;
                s = smp[i - x + 1];
                p1 += c * s;
            }

            res[i] = smp[i] - (p0 >> shift);
            res[i + 1] = smp[i + 1] - (p1 >> shift);
        }
    }

    /// <summary>
    /// flac_lpc_encode_c_32: the 32-bit path, with a 64-bit accumulator and the shifted sum
    /// clipped to int32 (<c>av_clipl_int32</c>).
    /// </summary>
    public static void FlacLpcEncodeC32(Span<int> res, ReadOnlySpan<int> smp, int len, int order,
                                        ReadOnlySpan<int> coefs, int shift)
    {
        for (int i = 0; i < order; i++)
            res[i] = smp[i];

        for (int i = order; i < len; i += 2)
        {
            int s = smp[i - order];
            long p0 = 0, p1 = 0;

            for (int x = order; x >= 1; x--)
            {
                long c = coefs[x - 1];
                p0 += c * s;
                s = smp[i - x + 1];
                p1 += c * s;
            }

            res[i] = smp[i] - ClipInt32(p0 >> shift);
            res[i + 1] = smp[i + 1] - ClipInt32(p1 >> shift);
        }
    }

    /// <summary>av_clipl_int32</summary>
    private static int ClipInt32(long a) =>
        (a + 0x80000000L) > uint.MaxValue ? (int)((a >> 63) ^ 0x7FFFFFFF) : (int)a;
}

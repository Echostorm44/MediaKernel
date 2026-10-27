using System;
using System.Numerics;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// FFmpeg's <c>mathops.h</c> - the scalar helpers its codecs share. Only the ones a ported
/// codec reaches for are here; the header's other entries are per-architecture overrides of
/// the same C fallbacks.
/// </summary>
public static class MathOps
{
    /// <summary>
    /// <c>av_log2</c> (libavutil/common.h) - the index of the highest set bit, and 0 for 0.
    /// </summary>
    /// <remarks>
    /// Not from mathops.h, but it is the same kind of thing and several codecs here have
    /// re-declared it privately. This is the copy the MPEG video family uses.
    /// </remarks>
    public static int AvLog2(uint v) => v == 0 ? 0 : 31 - BitOperations.LeadingZeroCount(v);

    /// <summary>
    /// <c>median3_c</c>, which <c>mid_pred</c> is a macro for on every architecture this port
    /// targets. Written as the C writes it rather than as a sort, because the C is what the
    /// motion-vector predictor was translated from.
    /// </summary>
    public static int MidPred(int a, int b, int c)
    {
        int max2, min2;

        if (a >= b)
        {
            max2 = a;
            min2 = b;
        }
        else
        {
            max2 = b;
            min2 = a;
        }

        int m = c >= max2 ? max2 : c;

        return m >= min2 ? m : min2;
    }

    /// <summary>
    /// <c>median4</c> (mathops.h) - the mean of the middle two of four values, which VC-1's
    /// four-vector luma and chroma predictors use. Written as the C writes it: the division is
    /// a C division of a sum that can be negative, so it truncates towards zero.
    /// </summary>
    public static int Median4(int a, int b, int c, int d)
    {
        if (a < b)
        {
            if (c < d) return (Math.Min(b, d) + Math.Max(a, c)) / 2;
            return (Math.Min(b, c) + Math.Max(a, d)) / 2;
        }
        if (c < d) return (Math.Min(a, d) + Math.Max(b, c)) / 2;
        return (Math.Min(a, c) + Math.Max(b, d)) / 2;
    }
}

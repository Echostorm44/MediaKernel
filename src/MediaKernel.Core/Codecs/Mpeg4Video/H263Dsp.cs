using System;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// FFmpeg's <c>h263dsp.c</c>: the two H.263 loop filters, which run on the reconstructed
/// picture at every 8x8 edge an unskipped macroblock touches.
/// </summary>
public static class H263Dsp
{
    /// <summary><c>ff_h263_loop_filter_strength[32]</c> - qscale to filter strength.</summary>
    public static ReadOnlySpan<byte> LoopFilterStrength =>
    [
        0, 1, 1, 2, 2, 3, 3,  4,  4,  4,  5,  5,  6,  6,  7, 7,
        7, 8, 8, 8, 9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12
    ];

    /// <summary>
    /// <c>h263_h_loop_filter_c</c> - the filter across a vertical edge, which runs down eight
    /// rows and reads two pixels either side of <paramref name="srcOff"/>.
    /// </summary>
    public static void H263HLoopFilterC(Span<byte> src, int srcOff, int stride, int qscale)
    {
        int strength = LoopFilterStrength[qscale];

        for (int y = 0; y < 8; y++)
        {
            int p0 = src[srcOff + y * stride - 2];
            int p1 = src[srcOff + y * stride - 1];
            int p2 = src[srcOff + y * stride + 0];
            int p3 = src[srcOff + y * stride + 1];
            int d = (p0 - p3 + 4 * (p2 - p1)) / 8;

            int d1 = FilterDelta(d, strength);

            p1 += d1;
            p2 -= d1;
            // The C's clip: bit 8 set means the value left 0..255, and ~(v >> 31) is 0 for a
            // negative one and -1 - which stores as 255 - for one above the range.
            if ((p1 & 256) != 0)
                p1 = ~(p1 >> 31);
            if ((p2 & 256) != 0)
                p2 = ~(p2 >> 31);

            src[srcOff + y * stride - 1] = (byte)p1;
            src[srcOff + y * stride + 0] = (byte)p2;

            int ad1 = FfAbs(d1) >> 1;

            int d2 = Math.Clamp((p0 - p3) / 4, -ad1, ad1);

            src[srcOff + y * stride - 2] = (byte)(p0 - d2);
            src[srcOff + y * stride + 1] = (byte)(p3 + d2);
        }
    }

    /// <summary>
    /// <c>h263_v_loop_filter_c</c> - the filter across a horizontal edge, which runs along eight
    /// columns and reads two rows either side of <paramref name="srcOff"/>.
    /// </summary>
    public static void H263VLoopFilterC(Span<byte> src, int srcOff, int stride, int qscale)
    {
        int strength = LoopFilterStrength[qscale];

        for (int x = 0; x < 8; x++)
        {
            int p0 = src[srcOff + x - 2 * stride];
            int p1 = src[srcOff + x - 1 * stride];
            int p2 = src[srcOff + x + 0 * stride];
            int p3 = src[srcOff + x + 1 * stride];
            int d = (p0 - p3 + 4 * (p2 - p1)) / 8;

            int d1 = FilterDelta(d, strength);

            p1 += d1;
            p2 -= d1;
            if ((p1 & 256) != 0)
                p1 = ~(p1 >> 31);
            if ((p2 & 256) != 0)
                p2 = ~(p2 >> 31);

            src[srcOff + x - 1 * stride] = (byte)p1;
            src[srcOff + x + 0 * stride] = (byte)p2;

            int ad1 = FfAbs(d1) >> 1;

            int d2 = Math.Clamp((p0 - p3) / 4, -ad1, ad1);

            src[srcOff + x - 2 * stride] = (byte)(p0 - d2);
            src[srcOff + x + stride] = (byte)(p3 + d2);
        }
    }

    /// <summary>
    /// The five-way ladder both filters apply to <c>d</c>. It is written out twice in the C,
    /// identically; the only difference between the two functions is how they index.
    /// </summary>
    private static int FilterDelta(int d, int strength)
    {
        if (d < -2 * strength)
            return 0;
        if (d < -strength)
            return -2 * strength - d;
        if (d < strength)
            return d;
        if (d < 2 * strength)
            return 2 * strength - d;
        return 0;
    }

    /// <summary><c>FFABS</c> - wraps on int.MinValue where Math.Abs throws. d1 never reaches
    /// it, but the macro's behaviour is what is being translated.</summary>
    private static int FfAbs(int a) => a < 0 ? -a : a;
}

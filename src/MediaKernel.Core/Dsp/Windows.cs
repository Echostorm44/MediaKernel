// libavcodec/kbdwin.c, libavcodec/sinewin_tablegen.h and libavutil/mathematics.c's Bessel
// function: the window tables every MDCT codec in FFmpeg windows with.
//
// These were reconstructed here from the formulas rather than translated, and the two forms are
// not numerically the same window. FFmpeg's KBD sums i*(n-i)*alpha2 under the Bessel function and
// normalises with a symmetric count; the textbook form evaluates alpha*pi*sqrt(1-x^2) and divides
// by I0(alpha*pi). Algebraically equal, different in the last bits - and the last bits are what
// decides whether a decoder matches the reference decoder, because the window multiplies every
// output sample.
//
// Its Bessel function matters as much: FFmpeg uses minimax rational approximations from Blair and
// Edwards (Chalk River AECL-4928, 1974), not the ascending series, and the two disagree well above
// the last bit.

using System;

namespace MediaKernel.Core.Dsp;

/// <summary>FFmpeg's window generators, translated.</summary>
public static class Windows
{
    /// <summary>
    /// <c>ff_sine_window_init</c>: <c>window[i] = sinf((i + 0.5) * (M_PI / (2.0 * n)))</c> for a
    /// half window of <paramref name="n"/> entries.
    /// </summary>
    /// <remarks>
    /// The argument is computed in double and then passed to <c>sinf</c>, so it is rounded to
    /// float before the sine is taken - which is not the same as taking the sine in double and
    /// rounding afterwards.
    /// </remarks>
    public static float[] SineHalfWindow(int n)
    {
        var window = new float[n];
        for (int i = 0; i < n; i++)
            window[i] = MathF.Sin((float)((i + 0.5) * (Math.PI / (2.0 * n))));
        return window;
    }

    /// <summary>
    /// <c>ff_kbd_window_init</c>: the Kaiser-Bessel derived window, <paramref name="n"/> entries
    /// for a transform of that length.
    /// </summary>
    public static float[] KbdWindow(float alpha, int n)
    {
        var window = new float[n];
        var temp = new double[n / 2 + 1];
        double sum = 0.0;
        double scale = 0.0;
        double alpha2 = 4 * (alpha * Math.PI / n) * (alpha * Math.PI / n);

        for (int i = 0; i <= n / 2; i++)
        {
            double tmp = i * (n - i) * alpha2;
            temp[i] = BesselI0(Math.Sqrt(tmp));
            scale += temp[i] * (1 + ((i != 0 && i < n / 2) ? 1 : 0));
        }

        scale = 1.0 / (scale + 1);

        int k = 0;
        for (; k <= n / 2; k++)
        {
            sum += temp[k];
            window[k] = (float)Math.Sqrt(sum * scale);
        }

        for (; k < n; k++)
        {
            sum += temp[n - k];
            window[k] = (float)Math.Sqrt(sum * scale);
        }

        return window;
    }

    /// <summary><c>eval_poly</c>: Horner's method over the coefficients in FFmpeg's order.</summary>
    private static double EvalPoly(ReadOnlySpan<double> coeff, double x)
    {
        double sum = coeff[^1];
        for (int i = coeff.Length - 2; i >= 0; --i)
        {
            sum *= x;
            sum += coeff[i];
        }
        return sum;
    }

    private static ReadOnlySpan<double> P1 =>
    [
        -2.2335582639474375249e+15,
        -5.5050369673018427753e+14,
        -3.2940087627407749166e+13,
        -8.4925101247114157499e+11,
        -1.1912746104985237192e+10,
        -1.0313066708737980747e+08,
        -5.9545626019847898221e+05,
        -2.4125195876041896775e+03,
        -7.0935347449210549190e+00,
        -1.5453977791786851041e-02,
        -2.5172644670688975051e-05,
        -3.0517226450451067446e-08,
        -2.6843448573468483278e-11,
        -1.5982226675653184646e-14,
        -5.2487866627945699800e-18,
    ];

    private static ReadOnlySpan<double> Q1 =>
    [
        -2.2335582639474375245e+15,
         7.8858692566751002988e+12,
        -1.2207067397808979846e+10,
         1.0377081058062166144e+07,
        -4.8527560179962773045e+03,
         1.0,
    ];

    private static ReadOnlySpan<double> P2 =>
    [
        -2.2210262233306573296e-04,
         1.3067392038106924055e-02,
        -4.4700805721174453923e-01,
         5.5674518371240761397e+00,
        -2.3517945679239481621e+01,
         3.1611322818701131207e+01,
        -9.6090021968656180000e+00,
    ];

    private static ReadOnlySpan<double> Q2 =>
    [
        -5.5194330231005480228e-04,
         3.2547697594819615062e-02,
        -1.1151759188741312645e+00,
         1.3982595353892851542e+01,
        -6.0228002066743340583e+01,
         8.5539563258012929600e+01,
        -3.1446690275135491500e+01,
         1.0,
    ];

    /// <summary>
    /// <c>av_bessel_i0</c>: the modified Bessel function of the first kind of order zero, by the
    /// minimax rational approximations FFmpeg uses rather than by its series.
    /// </summary>
    public static double BesselI0(double x)
    {
        if (x == 0) return 1.0;
        x = Math.Abs(x);

        if (x <= 15)
        {
            double y = x * x;
            return EvalPoly(P1, y) / EvalPoly(Q1, y);
        }

        double z = 1 / x - 1.0 / 15;
        double r = EvalPoly(P2, z) / EvalPoly(Q2, z);
        double factor = Math.Exp(x) / Math.Sqrt(x);
        return factor * r;
    }
}

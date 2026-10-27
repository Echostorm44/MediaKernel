// Translated from lame-3.100/libmp3lame/newmdct.c - Takehiro Tominaga's MDCT.
//
//
// POINTERS WITH NEGATIVE INDICES. window_subband takes `const sample_t *x1` aimed at the middle
// of the caller's buffer, derives `x2 = &x1[238 - 14 - 286]` - that is `&x1[-62]` - and then
// reads x2[-224] and x1[-256]. Both walk backwards as the loop runs. A Span anchored at x1
// cannot express that, so x1 and x2 are carried as integer indices into a span covering the
// whole buffer, which is also how mdct_sub48's `wk` already behaves. mdct_sub48 starts wk at
// w0 + 286 and the furthest reach behind it is w0 + 16, so every index stays in range.
//
// PRECISION. The scaling constants here are plain double literals - 2.069978111953089e-11,
// 1.907525191737280e-11, 0.86602540378443870761 - and the C's operands are FLOAT. So every
// expression mixing them promotes to double, evaluates in double, and rounds once on assignment
// back to a FLOAT. Each such statement is therefore written as (float)(...) around the whole
// expression, not with f-suffixed constants, which would evaluate in float and differ. Statements
// that mix only FLOATs stay in float, because that is what the C does.
//
// cx and tantab_l are not tables of their own: the C defines them as views into win[SHORT_TYPE],
//     #define tantab_l (win[SHORT_TYPE]+3)
//     #define cx       (win[SHORT_TYPE]+12)
// so win[2] carries the short window's coefficients, tantab_l and cx packed end to end. They are
// exposed below as spans at the same offsets, keeping the C's names at the point of use.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>newmdct.c</c>.</summary>
internal static class LameMdct
{
    /// <summary>
    /// <c>SQRT2</c> - <c>M_SQRT2</c> (<c>util.h:77</c>), and a double. Every expression below
    /// that touches it therefore evaluates in double and rounds once on store into a FLOAT.
    /// </summary>
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary><c>tantab_l</c> - <c>win[SHORT_TYPE] + 3</c>.</summary>
    private static ReadOnlySpan<float> TantabL =>
        LameMdctTables.win.AsSpan(LameBlockType.Short * LameMdctTables.Nl + 3);

    /// <summary><c>ca</c> - <c>win[SHORT_TYPE] + 20</c>, the aliasing butterfly's cosines.</summary>
    private static ReadOnlySpan<float> Ca =>
        LameMdctTables.win.AsSpan(LameBlockType.Short * LameMdctTables.Nl + 20);

    /// <summary><c>cs</c> - <c>win[SHORT_TYPE] + 28</c>.</summary>
    private static ReadOnlySpan<float> Cs =>
        LameMdctTables.win.AsSpan(LameBlockType.Short * LameMdctTables.Nl + 28);

    /// <summary><c>cx</c> - <c>win[SHORT_TYPE] + 12</c>.</summary>
    private static ReadOnlySpan<float> Cx =>
        LameMdctTables.win.AsSpan(LameBlockType.Short * LameMdctTables.Nl + 12);

    /// <summary><c>win[SHORT_TYPE]</c> - the row mdct_short's three coefficients come from.</summary>
    private static ReadOnlySpan<float> WinShort =>
        LameMdctTables.win.AsSpan(LameBlockType.Short * LameMdctTables.Nl, LameMdctTables.Nl);

    /// <summary>
    /// <c>mdct_short</c> - the three interleaved 12-point transforms of a short granule, in place.
    /// </summary>
    /// <remarks>
    /// The C advances <c>inout</c> by one on each of the three passes and indexes it in strides
    /// of three, which is how the three short blocks sit interleaved in one 18-coefficient
    /// window. Here <paramref name="inout"/> is re-sliced by <c>l</c> for the same effect.
    ///
    /// The comments in the C name the constants <c>tritab_s[0..2]</c>, although it writes them
    /// as literals and two of them differ in the last digit (…280e-11 against …281e-11). They
    /// are reproduced exactly as written, differing digit included, because an encoder is
    /// compared byte for byte and tidying them would be a change.
    /// </remarks>
    public static void MdctShort(Span<float> inout)
    {
        ReadOnlySpan<float> win = WinShort;

        for (int l = 0; l < 3; l++)
        {
            Span<float> x = inout[l..];
            float tc0, tc1, tc2, ts0, ts1, ts2;

            ts0 = x[2 * 3] * win[0] - x[5 * 3];
            tc0 = x[0 * 3] * win[2] - x[3 * 3];
            tc1 = ts0 + tc0;
            tc2 = ts0 - tc0;

            ts0 = x[5 * 3] * win[0] + x[2 * 3];
            tc0 = x[3 * 3] * win[2] + x[0 * 3];
            ts1 = ts0 + tc0;
            ts2 = -ts0 + tc0;

            tc0 = (float)((x[1 * 3] * win[1] - x[4 * 3]) * 2.069978111953089e-11); // tritab_s[1]
            ts0 = (float)((x[4 * 3] * win[1] + x[1 * 3]) * 2.069978111953089e-11); // tritab_s[1]

            x[3 * 0] = (float)(tc1 * 1.907525191737280e-11 + tc0);  // tritab_s[2]
            x[3 * 5] = (float)(-ts1 * 1.907525191737280e-11 + ts0); // tritab_s[0]

            tc2 = (float)(tc2 * 0.86602540378443870761 * 1.907525191737281e-11); // tritab_s[2]
            ts1 = (float)(ts1 * 0.5 * 1.907525191737281e-11 + ts0);
            x[3 * 1] = tc2 - ts1;
            x[3 * 2] = tc2 + ts1;

            tc1 = (float)(tc1 * 0.5 * 1.907525191737281e-11 - tc0);
            ts2 = (float)(ts2 * 0.86602540378443870761 * 1.907525191737281e-11); // tritab_s[0]
            x[3 * 3] = tc1 + ts2;
            x[3 * 4] = tc1 - ts2;
        }
    }

    /// <summary>
    /// <c>mdct_long</c> - the 18-point transform of a long granule.
    /// </summary>
    /// <remarks>
    /// Every expression here mixes only FLOATs and <c>cx[]</c>, which is a FLOAT table, so all of
    /// it stays in float. The C's two brace-delimited blocks are kept as scopes so that the two
    /// sets of temporaries stay separate, as they do in the C.
    /// </remarks>
    public static void MdctLong(Span<float> @out, ReadOnlySpan<float> @in)
    {
        ReadOnlySpan<float> cx = Cx;
        float ct, st;

        {
            float tc1, tc2, tc3, tc4, ts5, ts6, ts7, ts8;

            // 1,2, 5,6, 9,10, 13,14, 17
            tc1 = @in[17] - @in[9];
            tc3 = @in[15] - @in[11];
            tc4 = @in[14] - @in[12];
            ts5 = @in[0] + @in[8];
            ts6 = @in[1] + @in[7];
            ts7 = @in[2] + @in[6];
            ts8 = @in[3] + @in[5];

            @out[17] = (ts5 + ts7 - ts8) - (ts6 - @in[4]);
            st = (ts5 + ts7 - ts8) * cx[7] + (ts6 - @in[4]);
            ct = (tc1 - tc3 - tc4) * cx[6];
            @out[5] = ct + st;
            @out[6] = ct - st;

            tc2 = (@in[16] - @in[10]) * cx[6];
            ts6 = ts6 * cx[7] + @in[4];
            ct = tc1 * cx[0] + tc2 + tc3 * cx[1] + tc4 * cx[2];
            st = -ts5 * cx[4] + ts6 - ts7 * cx[5] + ts8 * cx[3];
            @out[1] = ct + st;
            @out[2] = ct - st;

            ct = tc1 * cx[1] - tc2 - tc3 * cx[2] + tc4 * cx[0];
            st = -ts5 * cx[5] + ts6 - ts7 * cx[3] + ts8 * cx[4];
            @out[9] = ct + st;
            @out[10] = ct - st;

            ct = tc1 * cx[2] - tc2 + tc3 * cx[0] - tc4 * cx[1];
            st = ts5 * cx[3] - ts6 + ts7 * cx[4] - ts8 * cx[5];
            @out[13] = ct + st;
            @out[14] = ct - st;
        }
        {
            float ts1, ts2, ts3, ts4, tc5, tc6, tc7, tc8;

            ts1 = @in[8] - @in[0];
            ts3 = @in[6] - @in[2];
            ts4 = @in[5] - @in[3];
            tc5 = @in[17] + @in[9];
            tc6 = @in[16] + @in[10];
            tc7 = @in[15] + @in[11];
            tc8 = @in[14] + @in[12];

            @out[0] = (tc5 + tc7 + tc8) + (tc6 + @in[13]);
            ct = (tc5 + tc7 + tc8) * cx[7] - (tc6 + @in[13]);
            st = (ts1 - ts3 + ts4) * cx[6];
            @out[11] = ct + st;
            @out[12] = ct - st;

            ts2 = (@in[7] - @in[1]) * cx[6];
            tc6 = @in[13] - tc6 * cx[7];
            ct = tc5 * cx[3] - tc6 + tc7 * cx[4] + tc8 * cx[5];
            st = ts1 * cx[2] + ts2 + ts3 * cx[0] + ts4 * cx[1];
            @out[3] = ct + st;
            @out[4] = ct - st;

            ct = -tc5 * cx[5] + tc6 - tc7 * cx[3] - tc8 * cx[4];
            st = ts1 * cx[1] + ts2 - ts3 * cx[2] - ts4 * cx[0];
            @out[7] = ct + st;
            @out[8] = ct - st;

            ct = -tc5 * cx[4] + tc6 - tc7 * cx[5] - tc8 * cx[3];
            st = ts1 * cx[0] - ts2 + ts3 * cx[1] - ts4 * cx[2];
            @out[15] = ct + st;
            @out[16] = ct - st;
        }
    }

    /// <summary>
    /// <c>window_subband</c> - the polyphase analysis filterbank, returning
    /// <c>sum(j = 0..31) a[j] * cos(PI * j * (k + 1/2) / 32)</c> for <c>0 &lt;= k &lt; 32</c>.
    /// </summary>
    /// <param name="x">The buffer the C's <c>x1</c> points into.</param>
    /// <param name="x1">The C's <c>x1</c>, as an index into <paramref name="x"/>.</param>
    /// <param name="a">The C's <c>a[SBLIMIT]</c> - 32 subband samples, written in place.</param>
    /// <remarks>
    /// Everything here is FLOAT - <c>sample_t</c> is a FLOAT too (<c>machine.h:156</c>) - except
    /// the <see cref="Sqrt2"/> products in the final butterfly block, which are double.
    /// </remarks>
    public static void WindowSubband(ReadOnlySpan<float> x, int x1, Span<float> a)
    {
        ReadOnlySpan<float> enwindow = LameMdctTables.enwindow;
        int wp = 10;
        int x2 = x1 + (238 - 14 - 286);

        for (int i = -15; i < 0; i++)
        {
            float w, s, t;

            w = enwindow[wp - 10];
            s = x[x2 - 224] * w;
            t = x[x1 + 224] * w;
            w = enwindow[wp - 9];
            s += x[x2 - 160] * w;
            t += x[x1 + 160] * w;
            w = enwindow[wp - 8];
            s += x[x2 - 96] * w;
            t += x[x1 + 96] * w;
            w = enwindow[wp - 7];
            s += x[x2 - 32] * w;
            t += x[x1 + 32] * w;
            w = enwindow[wp - 6];
            s += x[x2 + 32] * w;
            t += x[x1 - 32] * w;
            w = enwindow[wp - 5];
            s += x[x2 + 96] * w;
            t += x[x1 - 96] * w;
            w = enwindow[wp - 4];
            s += x[x2 + 160] * w;
            t += x[x1 - 160] * w;
            w = enwindow[wp - 3];
            s += x[x2 + 224] * w;
            t += x[x1 - 224] * w;

            w = enwindow[wp - 2];
            s += x[x1 - 256] * w;
            t -= x[x2 + 256] * w;
            w = enwindow[wp - 1];
            s += x[x1 - 192] * w;
            t -= x[x2 + 192] * w;
            w = enwindow[wp + 0];
            s += x[x1 - 128] * w;
            t -= x[x2 + 128] * w;
            w = enwindow[wp + 1];
            s += x[x1 - 64] * w;
            t -= x[x2 + 64] * w;
            w = enwindow[wp + 2];
            s += x[x1 + 0] * w;
            t -= x[x2 + 0] * w;
            w = enwindow[wp + 3];
            s += x[x1 + 64] * w;
            t -= x[x2 - 64] * w;
            w = enwindow[wp + 4];
            s += x[x1 + 128] * w;
            t -= x[x2 - 128] * w;
            w = enwindow[wp + 5];
            s += x[x1 + 192] * w;
            t -= x[x2 - 192] * w;

            // The C notes that this multiplier could be folded away at the cost of 256 more
            // FLOATs, and keeps it for the data cache's sake.
            s *= enwindow[wp + 6];
            w = t - s;
            a[30 + i * 2] = t + s;
            a[31 + i * 2] = enwindow[wp + 7] * w;
            wp += 18;
            x1--;
            x2++;
        }
        {
            float s, t, u, v;
            t = x[x1 - 16] * enwindow[wp - 10];
            s = x[x1 - 32] * enwindow[wp - 2];
            t += (x[x1 - 48] - x[x1 + 16]) * enwindow[wp - 9];
            s += x[x1 - 96] * enwindow[wp - 1];
            t += (x[x1 - 80] + x[x1 + 48]) * enwindow[wp - 8];
            s += x[x1 - 160] * enwindow[wp + 0];
            t += (x[x1 - 112] - x[x1 + 80]) * enwindow[wp - 7];
            s += x[x1 - 224] * enwindow[wp + 1];
            t += (x[x1 - 144] + x[x1 + 112]) * enwindow[wp - 6];
            s -= x[x1 + 32] * enwindow[wp + 2];
            t += (x[x1 - 176] - x[x1 + 144]) * enwindow[wp - 5];
            s -= x[x1 + 96] * enwindow[wp + 3];
            t += (x[x1 - 208] + x[x1 + 176]) * enwindow[wp - 4];
            s -= x[x1 + 160] * enwindow[wp + 4];
            t += (x[x1 - 240] - x[x1 + 208]) * enwindow[wp - 3];
            s -= x[x1 + 224];

            u = s - t;
            v = s + t;

            t = a[14];
            s = a[15] - t;

            a[31] = v + t;  // A0
            a[30] = u + s;  // A1
            a[15] = u - s;  // A2
            a[14] = v - t;  // A3
        }
        {
            float xr;
            xr = a[28] - a[0];
            a[0] += a[28];
            a[28] = xr * enwindow[wp - 2 * 18 + 7];
            xr = a[29] - a[1];
            a[1] += a[29];
            a[29] = xr * enwindow[wp - 2 * 18 + 7];

            xr = a[26] - a[2];
            a[2] += a[26];
            a[26] = xr * enwindow[wp - 4 * 18 + 7];
            xr = a[27] - a[3];
            a[3] += a[27];
            a[27] = xr * enwindow[wp - 4 * 18 + 7];

            xr = a[24] - a[4];
            a[4] += a[24];
            a[24] = xr * enwindow[wp - 6 * 18 + 7];
            xr = a[25] - a[5];
            a[5] += a[25];
            a[25] = xr * enwindow[wp - 6 * 18 + 7];

            xr = a[22] - a[6];
            a[6] += a[22];
            a[22] = (float)(xr * Sqrt2);
            xr = a[23] - a[7];
            a[7] += a[23];
            a[23] = (float)(xr * Sqrt2 - a[7]);
            a[7] -= a[6];
            a[22] -= a[7];
            a[23] -= a[22];

            xr = a[6];
            a[6] = a[31] - xr;
            a[31] = a[31] + xr;
            xr = a[7];
            a[7] = a[30] - xr;
            a[30] = a[30] + xr;
            xr = a[22];
            a[22] = a[15] - xr;
            a[15] = a[15] + xr;
            xr = a[23];
            a[23] = a[14] - xr;
            a[14] = a[14] + xr;

            xr = a[20] - a[8];
            a[8] += a[20];
            a[20] = xr * enwindow[wp - 10 * 18 + 7];
            xr = a[21] - a[9];
            a[9] += a[21];
            a[21] = xr * enwindow[wp - 10 * 18 + 7];

            xr = a[18] - a[10];
            a[10] += a[18];
            a[18] = xr * enwindow[wp - 12 * 18 + 7];
            xr = a[19] - a[11];
            a[11] += a[19];
            a[19] = xr * enwindow[wp - 12 * 18 + 7];

            xr = a[16] - a[12];
            a[12] += a[16];
            a[16] = xr * enwindow[wp - 14 * 18 + 7];
            xr = a[17] - a[13];
            a[13] += a[17];
            a[17] = xr * enwindow[wp - 14 * 18 + 7];

            xr = -a[20] + a[24];
            a[20] += a[24];
            a[24] = xr * enwindow[wp - 12 * 18 + 7];
            xr = -a[21] + a[25];
            a[21] += a[25];
            a[25] = xr * enwindow[wp - 12 * 18 + 7];

            xr = a[4] - a[8];
            a[4] += a[8];
            a[8] = xr * enwindow[wp - 12 * 18 + 7];
            xr = a[5] - a[9];
            a[5] += a[9];
            a[9] = xr * enwindow[wp - 12 * 18 + 7];

            xr = a[0] - a[12];
            a[0] += a[12];
            a[12] = xr * enwindow[wp - 4 * 18 + 7];
            xr = a[1] - a[13];
            a[1] += a[13];
            a[13] = xr * enwindow[wp - 4 * 18 + 7];
            xr = a[16] - a[28];
            a[16] += a[28];
            a[28] = xr * enwindow[wp - 4 * 18 + 7];
            xr = -a[17] + a[29];
            a[17] += a[29];
            a[29] = xr * enwindow[wp - 4 * 18 + 7];

            xr = (float)(Sqrt2 * (a[2] - a[10]));
            a[2] += a[10];
            a[10] = xr;
            xr = (float)(Sqrt2 * (a[3] - a[11]));
            a[3] += a[11];
            a[11] = xr;
            xr = (float)(Sqrt2 * (-a[18] + a[26]));
            a[18] += a[26];
            a[26] = xr - a[18];
            xr = (float)(Sqrt2 * (-a[19] + a[27]));
            a[19] += a[27];
            a[27] = xr - a[19];

            xr = a[2];
            a[19] -= a[3];
            a[3] -= xr;
            a[2] = a[31] - xr;
            a[31] += xr;
            xr = a[3];
            a[11] -= a[19];
            a[18] -= xr;
            a[3] = a[30] - xr;
            a[30] += xr;
            xr = a[18];
            a[27] -= a[11];
            a[19] -= xr;
            a[18] = a[15] - xr;
            a[15] += xr;

            xr = a[19];
            a[10] -= xr;
            a[19] = a[14] - xr;
            a[14] += xr;
            xr = a[10];
            a[11] -= xr;
            a[10] = a[23] - xr;
            a[23] += xr;
            xr = a[11];
            a[26] -= xr;
            a[11] = a[22] - xr;
            a[22] += xr;
            xr = a[26];
            a[27] -= xr;
            a[26] = a[7] - xr;
            a[7] += xr;

            xr = a[27];
            a[27] = a[6] - xr;
            a[6] += xr;

            xr = (float)(Sqrt2 * (a[0] - a[4]));
            a[0] += a[4];
            a[4] = xr;
            xr = (float)(Sqrt2 * (a[1] - a[5]));
            a[1] += a[5];
            a[5] = xr;
            xr = (float)(Sqrt2 * (a[16] - a[20]));
            a[16] += a[20];
            a[20] = xr;
            xr = (float)(Sqrt2 * (a[17] - a[21]));
            a[17] += a[21];
            a[21] = xr;

            xr = (float)(-Sqrt2 * (a[8] - a[12]));
            a[8] += a[12];
            a[12] = xr - a[8];
            xr = (float)(-Sqrt2 * (a[9] - a[13]));
            a[9] += a[13];
            a[13] = xr - a[9];
            xr = (float)(-Sqrt2 * (a[25] - a[29]));
            a[25] += a[29];
            a[29] = xr - a[25];
            xr = (float)(-Sqrt2 * (a[24] + a[28]));
            a[24] -= a[28];
            a[28] = xr - a[24];

            xr = a[24] - a[16];
            a[24] = xr;
            xr = a[20] - xr;
            a[20] = xr;
            xr = a[28] - xr;
            a[28] = xr;

            xr = a[25] - a[17];
            a[25] = xr;
            xr = a[21] - xr;
            a[21] = xr;
            xr = a[29] - xr;
            a[29] = xr;

            xr = a[17] - a[1];
            a[17] = xr;
            xr = a[9] - xr;
            a[9] = xr;
            xr = a[25] - xr;
            a[25] = xr;
            xr = a[5] - xr;
            a[5] = xr;
            xr = a[21] - xr;
            a[21] = xr;
            xr = a[13] - xr;
            a[13] = xr;
            xr = a[29] - xr;
            a[29] = xr;

            xr = a[1] - a[0];
            a[1] = xr;
            xr = a[16] - xr;
            a[16] = xr;
            xr = a[17] - xr;
            a[17] = xr;
            xr = a[8] - xr;
            a[8] = xr;
            xr = a[9] - xr;
            a[9] = xr;
            xr = a[24] - xr;
            a[24] = xr;
            xr = a[25] - xr;
            a[25] = xr;
            xr = a[4] - xr;
            a[4] = xr;
            xr = a[5] - xr;
            a[5] = xr;
            xr = a[20] - xr;
            a[20] = xr;
            xr = a[21] - xr;
            a[21] = xr;
            xr = a[12] - xr;
            a[12] = xr;
            xr = a[13] - xr;
            a[13] = xr;
            xr = a[28] - xr;
            a[28] = xr;
            xr = a[29] - xr;
            a[29] = xr;

            xr = a[0];
            a[0] += a[31];
            a[31] -= xr;
            xr = a[1];
            a[1] += a[30];
            a[30] -= xr;
            xr = a[16];
            a[16] += a[15];
            a[15] -= xr;
            xr = a[17];
            a[17] += a[14];
            a[14] -= xr;
            xr = a[8];
            a[8] += a[23];
            a[23] -= xr;
            xr = a[9];
            a[9] += a[22];
            a[22] -= xr;
            xr = a[24];
            a[24] += a[7];
            a[7] -= xr;
            xr = a[25];
            a[25] += a[6];
            a[6] -= xr;
            xr = a[4];
            a[4] += a[27];
            a[27] -= xr;
            xr = a[5];
            a[5] += a[26];
            a[26] -= xr;
            xr = a[20];
            a[20] += a[11];
            a[11] -= xr;
            xr = a[21];
            a[21] += a[10];
            a[10] -= xr;
            xr = a[12];
            a[12] += a[19];
            a[19] -= xr;
            xr = a[13];
            a[13] += a[18];
            a[18] -= xr;
            xr = a[28];
            a[28] += a[3];
            a[3] -= xr;
            xr = a[29];
            a[29] += a[2];
            a[2] -= xr;
        }
    }

    /// <summary>
    /// <c>mdct_sub48</c> - the whole filterbank-plus-MDCT stage for one frame.
    /// </summary>
    /// <param name="gfc">The encoder state.</param>
    /// <param name="w0">The C's <c>w0</c> - channel 0's <c>mfbuf</c> row.</param>
    /// <param name="w1">The C's <c>w1</c> - channel 1's row. Unread when mono.</param>
    /// <remarks>
    /// The C notes that looping channel-then-granule beats the other order for the data cache,
    /// and that order is kept. <c>wk</c> starts 286 samples into the channel's buffer and
    /// advances 64 per half-granule, which is what leaves room for window_subband's backward
    /// reach.
    /// </remarks>
    public static void MdctSub48(LameInternalFlags gfc, ReadOnlySpan<float> w0, ReadOnlySpan<float> w1)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        float[] sbSample = gfc.SvEnc.SbSample!;
        float[] ampFilter = gfc.SvEnc.AmpFilter!;

        ReadOnlySpan<float> w = w0;
        int wk = 286;

        for (int ch = 0; ch < cfg.ChannelsOut; ch++)
        {
            for (int gr = 0; gr < cfg.ModeGr; gr++)
            {
                int band;
                ref GrInfo gi = ref gfc.L3Side.Tt[gr * 2 + ch];
                Span<float> xr = gi.Xr;
                int mdctEnc = 0;
                int samp = (ch * 2 + (1 - gr)) * 576;

                for (int k = 0; k < 18 / 2; k++)
                {
                    WindowSubband(w, wk, sbSample.AsSpan(samp, LameConstants.SbLimit));
                    WindowSubband(w, wk + 32, sbSample.AsSpan(samp + 32, LameConstants.SbLimit));
                    samp += 64;
                    wk += 64;

                    // Compensate for inversion in the analysis filter.
                    for (band = 1; band < 32; band += 2)
                        sbSample[samp + band - 32] *= -1;
                }

                // Perform imdct of 18 previous subband samples + 18 current subband samples.
                for (band = 0; band < 32; band++, mdctEnc += 18)
                {
                    int type = gi.BlockType;
                    int band0 = (ch * 2 + gr) * 576 + LameMdctTables.order[band];
                    int band1 = (ch * 2 + (1 - gr)) * 576 + LameMdctTables.order[band];
                    if (gi.MixedBlockFlag != 0 && band < 2)
                        type = LameBlockType.Norm;
                    if (ampFilter[band] < 1e-12)
                    {
                        xr.Slice(mdctEnc, 18).Clear();
                    }
                    else
                    {
                        if (ampFilter[band] < 1.0)
                        {
                            for (int k = 0; k < 18; k++)
                                sbSample[band1 + k * 32] *= ampFilter[band];
                        }
                        if (type == LameBlockType.Short)
                        {
                            ReadOnlySpan<float> winS = WinShort;
                            for (int k = -LameMdctTables.Ns / 4; k < 0; k++)
                            {
                                float wk3 = winS[k + 3];
                                xr[mdctEnc + k * 3 + 9] =
                                    sbSample[band0 + (9 + k) * 32] * wk3 - sbSample[band0 + (8 - k) * 32];
                                xr[mdctEnc + k * 3 + 18] =
                                    sbSample[band0 + (14 - k) * 32] * wk3 + sbSample[band0 + (15 + k) * 32];
                                xr[mdctEnc + k * 3 + 10] =
                                    sbSample[band0 + (15 + k) * 32] * wk3 - sbSample[band0 + (14 - k) * 32];
                                xr[mdctEnc + k * 3 + 19] =
                                    sbSample[band1 + (2 - k) * 32] * wk3 + sbSample[band1 + (3 + k) * 32];
                                xr[mdctEnc + k * 3 + 11] =
                                    sbSample[band1 + (3 + k) * 32] * wk3 - sbSample[band1 + (2 - k) * 32];
                                xr[mdctEnc + k * 3 + 20] =
                                    sbSample[band1 + (8 - k) * 32] * wk3 + sbSample[band1 + (9 + k) * 32];
                            }
                            MdctShort(xr.Slice(mdctEnc));
                        }
                        else
                        {
                            Float18 workStorage = default;
                            Span<float> work = workStorage;
                            ReadOnlySpan<float> winT =
                                LameMdctTables.win.AsSpan(type * LameMdctTables.Nl, LameMdctTables.Nl);
                            ReadOnlySpan<float> tantabL = TantabL;

                            for (int k = -LameMdctTables.Nl / 4; k < 0; k++)
                            {
                                float a, b;
                                a = winT[k + 27] * sbSample[band1 + (k + 9) * 32]
                                  + winT[k + 36] * sbSample[band1 + (8 - k) * 32];
                                b = winT[k + 9] * sbSample[band0 + (k + 9) * 32]
                                  - winT[k + 18] * sbSample[band0 + (8 - k) * 32];
                                work[k + 9] = a - b * tantabL[k + 9];
                                work[k + 18] = a * tantabL[k + 9] + b;
                            }

                            MdctLong(xr.Slice(mdctEnc, 18), work);
                        }
                    }

                    // Perform aliasing reduction butterfly.
                    if (type != LameBlockType.Short && band != 0)
                    {
                        ReadOnlySpan<float> ca = Ca;
                        ReadOnlySpan<float> cs = Cs;
                        for (int k = 7; k >= 0; --k)
                        {
                            float bu, bd;
                            bu = xr[mdctEnc + k] * ca[k] + xr[mdctEnc - 1 - k] * cs[k];
                            bd = xr[mdctEnc + k] * cs[k] - xr[mdctEnc - 1 - k] * ca[k];

                            xr[mdctEnc - 1 - k] = bu;
                            xr[mdctEnc + k] = bd;
                        }
                    }
                }
            }
            w = w1;
            wk = 286;
            if (cfg.ModeGr == 1)
                Array.Copy(sbSample, (ch * 2 + 1) * 576, sbSample, (ch * 2 + 0) * 576, 576);
        }
    }

    /// <summary><c>FLOAT work[18]</c> - mdct_sub48's long-block scratch.</summary>
    [InlineArray(18)]
    private struct Float18 { private float element0; }
}

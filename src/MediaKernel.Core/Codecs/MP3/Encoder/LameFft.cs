// Translated from lame-3.100/libmp3lame/fft.c - the Hartley transform the psychoacoustic model
// runs on, and the windowed radix-4 front end that feeds it.
//
// PRECISION, and the two places it is load-bearing here:
//
//   SQRT2 is M_SQRT2, a double (util.h:77). In `f3 = SQRT2 * gi[k3]` the C promotes the float to
//   double, multiplies in double, and rounds once on assignment back to FLOAT. Writing that as a
//   float multiply by 1.41421356f gives a different answer. Hence the (float)(Sqrt2 * ...) casts.
//
//   init_fft builds both windows with cos() in double and rounds each to float on store, so the
//   window arithmetic stays in double here too.
//
// Everything else in fht is genuinely float: the C's int literals promote to float, and on
// x86-64 FLT_EVAL_METHOD is 0, so float expressions evaluate in float exactly as C# does.
//
// The function pointer gfc->fft_fht, which the C uses to swap in 3DNow or SSE kernels, becomes a
// direct static call per the project's decision on DSP contexts.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary>
/// <c>const sample_t *const buffer[2]</c> - the two input channels as the C passes them, each a
/// pointer already offset into <c>mfbuf</c>.
/// </summary>
/// <remarks>
/// A ref struct of two spans rather than an array of arrays, so the caller's offset survives the
/// call without copying and without allocating. Indexing it reads like the C's
/// <c>buffer[chn][i]</c>.
/// </remarks>
internal readonly ref struct SampleBuffers
{
    private readonly ReadOnlySpan<float> channel0;
    private readonly ReadOnlySpan<float> channel1;

    public SampleBuffers(ReadOnlySpan<float> channel0, ReadOnlySpan<float> channel1)
    {
        this.channel0 = channel0;
        this.channel1 = channel1;
    }

    /// <summary><c>buffer[chn]</c>.</summary>
    public ReadOnlySpan<float> this[int chn]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => chn == 0 ? channel0 : channel1;
    }
}

/// <summary><c>fft.c</c>.</summary>
internal static class LameFft
{
    /// <summary><c>SQRT2</c> - <c>M_SQRT2</c>, and a double, which the multiplies below rely on.</summary>
    private const double Sqrt2 = 1.41421356237309504880;

    /// <summary><c>TRI_SIZE</c> - <c>5 - 1</c>, because 1024 is 4 to the 5th.</summary>
    private const int TriSize = 5 - 1;

    /// <summary><c>costab[TRI_SIZE * 2]</c> - Buneman's trig generator seeds.</summary>
    private static readonly float[] costab =
    [
        9.238795325112867e-01f, 3.826834323650898e-01f,
        9.951847266721969e-01f, 9.801714032956060e-02f,
        9.996988186962042e-01f, 2.454122852291229e-02f,
        9.999811752826011e-01f, 6.135884649154475e-03f,
    ];

    /// <summary><c>rv_tbl[]</c> - the bit-reversal table the radix-4 front end scatters through.</summary>
    private static readonly byte[] rv_tbl =
    [
        0x00, 0x80, 0x40, 0xc0, 0x20, 0xa0, 0x60, 0xe0,
        0x10, 0x90, 0x50, 0xd0, 0x30, 0xb0, 0x70, 0xf0,
        0x08, 0x88, 0x48, 0xc8, 0x28, 0xa8, 0x68, 0xe8,
        0x18, 0x98, 0x58, 0xd8, 0x38, 0xb8, 0x78, 0xf8,
        0x04, 0x84, 0x44, 0xc4, 0x24, 0xa4, 0x64, 0xe4,
        0x14, 0x94, 0x54, 0xd4, 0x34, 0xb4, 0x74, 0xf4,
        0x0c, 0x8c, 0x4c, 0xcc, 0x2c, 0xac, 0x6c, 0xec,
        0x1c, 0x9c, 0x5c, 0xdc, 0x3c, 0xbc, 0x7c, 0xfc,
        0x02, 0x82, 0x42, 0xc2, 0x22, 0xa2, 0x62, 0xe2,
        0x12, 0x92, 0x52, 0xd2, 0x32, 0xb2, 0x72, 0xf2,
        0x0a, 0x8a, 0x4a, 0xca, 0x2a, 0xaa, 0x6a, 0xea,
        0x1a, 0x9a, 0x5a, 0xda, 0x3a, 0xba, 0x7a, 0xfa,
        0x06, 0x86, 0x46, 0xc6, 0x26, 0xa6, 0x66, 0xe6,
        0x16, 0x96, 0x56, 0xd6, 0x36, 0xb6, 0x76, 0xf6,
        0x0e, 0x8e, 0x4e, 0xce, 0x2e, 0xae, 0x6e, 0xee,
        0x1e, 0x9e, 0x5e, 0xde, 0x3e, 0xbe, 0x7e, 0xfe,
    ];

    /// <summary>
    /// <c>fht</c> - a Hartley transform of <c>n</c> points, in place.
    /// </summary>
    /// <remarks>
    /// The C doubles <c>n</c> on entry, which is why both callers pass half the block size: the
    /// comment there says "because of 3DNow! ASM routine". Kept, so the call sites read as they
    /// do in the C.
    ///
    /// The C walks <c>fi</c> and <c>gi</c> as pointers and compares them against the end pointer
    /// <c>fn</c>. Here they are indices into <paramref name="fz"/> and the loop condition is the
    /// same comparison on those indices.
    /// </remarks>
    public static void Fht(Span<float> fz, int n)
    {
        int tri = 0;
        int k4;

        n <<= 1;                // to get BLKSIZE, because of the 3DNow! ASM routine
        int fn = n;
        k4 = 4;
        do
        {
            float s1, c1;
            int i, k1, k2, k3, kx;
            kx = k4 >> 1;
            k1 = k4;
            k2 = k4 << 1;
            k3 = k2 + k1;
            k4 = k2 << 1;
            int fi = 0;
            int gi = fi + kx;
            do
            {
                float f0, f1, f2, f3;
                f1 = fz[fi] - fz[fi + k1];
                f0 = fz[fi] + fz[fi + k1];
                f3 = fz[fi + k2] - fz[fi + k3];
                f2 = fz[fi + k2] + fz[fi + k3];
                fz[fi + k2] = f0 - f2;
                fz[fi] = f0 + f2;
                fz[fi + k3] = f1 - f3;
                fz[fi + k1] = f1 + f3;
                f1 = fz[gi] - fz[gi + k1];
                f0 = fz[gi] + fz[gi + k1];

                // SQRT2 is a double, so these two multiply in double and round once.
                f3 = (float)(Sqrt2 * fz[gi + k3]);
                f2 = (float)(Sqrt2 * fz[gi + k2]);

                fz[gi + k2] = f0 - f2;
                fz[gi] = f0 + f2;
                fz[gi + k3] = f1 - f3;
                fz[gi + k1] = f1 + f3;
                gi += k4;
                fi += k4;
            } while (fi < fn);

            c1 = costab[tri + 0];
            s1 = costab[tri + 1];
            for (i = 1; i < kx; i++)
            {
                float c2, s2;
                c2 = 1 - (2 * s1) * s1;
                s2 = (2 * s1) * c1;
                fi = i;
                gi = k1 - i;
                do
                {
                    float a, b, g0, f0, f1, g1, f2, g2, f3, g3;
                    b = s2 * fz[fi + k1] - c2 * fz[gi + k1];
                    a = c2 * fz[fi + k1] + s2 * fz[gi + k1];
                    f1 = fz[fi] - a;
                    f0 = fz[fi] + a;
                    g1 = fz[gi] - b;
                    g0 = fz[gi] + b;
                    b = s2 * fz[fi + k3] - c2 * fz[gi + k3];
                    a = c2 * fz[fi + k3] + s2 * fz[gi + k3];
                    f3 = fz[fi + k2] - a;
                    f2 = fz[fi + k2] + a;
                    g3 = fz[gi + k2] - b;
                    g2 = fz[gi + k2] + b;
                    b = s1 * f2 - c1 * g3;
                    a = c1 * f2 + s1 * g3;
                    fz[fi + k2] = f0 - a;
                    fz[fi] = f0 + a;
                    fz[gi + k3] = g1 - b;
                    fz[gi + k1] = g1 + b;
                    b = c1 * g2 - s1 * f3;
                    a = s1 * g2 + c1 * f3;
                    fz[gi + k2] = g0 - a;
                    fz[gi] = g0 + a;
                    fz[fi + k3] = f1 - b;
                    fz[fi + k1] = f1 + b;
                    gi += k4;
                    fi += k4;
                } while (fi < fn);

                c2 = c1;
                c1 = c2 * costab[tri + 0] - s1 * costab[tri + 1];
                s1 = c2 * costab[tri + 1] + s1 * costab[tri + 0];
            }
            tri += 2;
        } while (k4 < n);
    }

    /// <summary>
    /// <c>fft_short</c> - the three short-block transforms of one channel.
    /// </summary>
    /// <remarks>
    /// <paramref name="xReal"/> is the C's <c>FLOAT x_real[3][BLKSIZE_s]</c>, flattened; block
    /// <c>b</c> starts at <c>b * BLKSIZE_s</c>.
    ///
    /// The C's ms00..ms31 macros fold the window into the fetch, and two of the four taps read
    /// the window backwards (<c>window_s[0x7f - i]</c>, <c>window_s[0x3f - i]</c>) because the
    /// short window is only BLKSIZE_s/2 long and is mirrored. Written out here rather than kept
    /// as helpers, so the asymmetry is visible.
    /// </remarks>
    public static void FftShort(LameInternalFlags gfc, Span<float> xReal, int chn, SampleBuffers buffer)
    {
        const int blkSizeS = LameConstants.BlkSizeS;
        float[] windowS = gfc.CdPsy.WindowS!;
        ReadOnlySpan<float> ch = buffer[chn];

        for (int b = 0; b < 3; b++)
        {
            Span<float> block = xReal.Slice(b * blkSizeS, blkSizeS);
            int x = blkSizeS / 2;
            int k = (576 / 3) * (b + 1);
            int j = blkSizeS / 8 - 1;
            do
            {
                float f0, f1, f2, f3, w;

                int i = rv_tbl[j << 2];

                f0 = windowS[i] * ch[i + k];
                w = windowS[0x7f - i] * ch[i + k + 0x80];
                f1 = f0 - w;
                f0 = f0 + w;
                f2 = windowS[i + 0x40] * ch[i + k + 0x40];
                w = windowS[0x3f - i] * ch[i + k + 0xc0];
                f3 = f2 - w;
                f2 = f2 + w;

                x -= 4;
                block[x + 0] = f0 + f2;
                block[x + 2] = f0 - f2;
                block[x + 1] = f1 + f3;
                block[x + 3] = f1 - f3;

                f0 = windowS[i + 0x01] * ch[i + k + 0x01];
                w = windowS[0x7e - i] * ch[i + k + 0x81];
                f1 = f0 - w;
                f0 = f0 + w;
                f2 = windowS[i + 0x41] * ch[i + k + 0x41];
                w = windowS[0x3e - i] * ch[i + k + 0xc1];
                f3 = f2 - w;
                f2 = f2 + w;

                block[x + blkSizeS / 2 + 0] = f0 + f2;
                block[x + blkSizeS / 2 + 2] = f0 - f2;
                block[x + blkSizeS / 2 + 1] = f1 + f3;
                block[x + blkSizeS / 2 + 3] = f1 - f3;
            } while (--j >= 0);

            // BLKSIZE_s/2 because of the 3DNow! ASM routine.
            Fht(block[x..], blkSizeS / 2);
        }
    }

    /// <summary>
    /// <c>fft_long</c> - the long-block transform of one channel.
    /// </summary>
    public static void FftLong(LameInternalFlags gfc, Span<float> x, int chn, SampleBuffers buffer)
    {
        const int blkSize = LameConstants.BlkSize;
        float[] window = gfc.CdPsy.Window!;
        ReadOnlySpan<float> ch = buffer[chn];

        int jj = blkSize / 8 - 1;
        int p = blkSize / 2;
        do
        {
            float f0, f1, f2, f3, w;

            int i = rv_tbl[jj];
            f0 = window[i] * ch[i];
            w = window[i + 0x200] * ch[i + 0x200];
            f1 = f0 - w;
            f0 = f0 + w;
            f2 = window[i + 0x100] * ch[i + 0x100];
            w = window[i + 0x300] * ch[i + 0x300];
            f3 = f2 - w;
            f2 = f2 + w;

            p -= 4;
            x[p + 0] = f0 + f2;
            x[p + 2] = f0 - f2;
            x[p + 1] = f1 + f3;
            x[p + 3] = f1 - f3;

            f0 = window[i + 0x001] * ch[i + 0x001];
            w = window[i + 0x201] * ch[i + 0x201];
            f1 = f0 - w;
            f0 = f0 + w;
            f2 = window[i + 0x101] * ch[i + 0x101];
            w = window[i + 0x301] * ch[i + 0x301];
            f3 = f2 - w;
            f2 = f2 + w;

            x[p + blkSize / 2 + 0] = f0 + f2;
            x[p + blkSize / 2 + 2] = f0 - f2;
            x[p + blkSize / 2 + 1] = f1 + f3;
            x[p + blkSize / 2 + 3] = f1 - f3;
        } while (--jj >= 0);

        // BLKSIZE/2 because of the 3DNow! ASM routine.
        Fht(x[p..], blkSize / 2);
    }

    /// <summary>
    /// <c>init_fft</c> - builds both analysis windows.
    /// </summary>
    /// <remarks>
    /// The C's comment says the window type makes no real difference and that this is a Blackman
    /// window for the sake of merging nspsytune. Both are computed in double from <c>cos</c> and
    /// rounded to float on store, which is what the casts preserve.
    ///
    /// The C ends by assigning <c>gfc-&gt;fft_fht</c>, choosing a 3DNow or SSE kernel where one
    /// is built. There is no pointer here: <see cref="Fht"/> is called directly.
    /// </remarks>
    public static void InitFft(LameInternalFlags gfc)
    {
        // psymodel_init allocates cd_psy before it calls init_fft, so this is never null on the
        // C's path either; a NullReferenceException here means a caller skipped that.
        float[] window = gfc.CdPsy!.Window!;
        float[] windowS = gfc.CdPsy.WindowS!;

        for (int i = 0; i < LameConstants.BlkSize; i++)
        {
            // Blackman window.
            window[i] = (float)(0.42
                              - 0.5 * Math.Cos(2 * Math.PI * (i + .5) / LameConstants.BlkSize)
                              + 0.08 * Math.Cos(4 * Math.PI * (i + .5) / LameConstants.BlkSize));
        }

        for (int i = 0; i < LameConstants.BlkSizeS / 2; i++)
        {
            windowS[i] = (float)(0.5
                               * (1.0 - Math.Cos(2.0 * Math.PI * (i + 0.5) / LameConstants.BlkSizeS)));
        }
    }
}

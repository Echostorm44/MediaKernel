// aacps_tablegen.h - the tables PS computes at startup rather than ships.
//
// FFmpeg builds these at init unless CONFIG_HARDCODED_TABLES is set, from the same code that
// generates the hardcoded header, so computing them here is the faithful translation and not a
// shortcut. The arithmetic is kept exactly as the C has it - which intermediates are double and
// which are float decides the last bit of several hundred table entries, and those entries are
// multiplied into every output sample.

using System;

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary><c>ps_tableinit</c> and the prototype filters it builds from.</summary>
internal static class PsTables
{
    public const int NrAllpassBands20 = 30;
    public const int NrAllpassBands34 = 50;

    /// <summary>Phase smoothing, indexed <c>pd0*64 + pd1*8 + pd2</c>.</summary>
    public static readonly float[] PdReSmooth = new float[8 * 8 * 8];
    public static readonly float[] PdImSmooth = new float[8 * 8 * 8];

    /// <summary>Mixing matrix for ICC modes 0-2 (and all of baseline PS).</summary>
    public static readonly float[][][] HA = Cube(46, 8, 4);

    /// <summary>Mixing matrix for ICC modes 3-5.</summary>
    public static readonly float[][][] HB = Cube(46, 8, 4);

    public static readonly float[][][] F20_0_8 = Cube(8, 8, 2);
    public static readonly float[][][] F34_0_12 = Cube(12, 8, 2);
    public static readonly float[][][] F34_1_8 = Cube(8, 8, 2);
    public static readonly float[][][] F34_2_4 = Cube(4, 8, 2);

    /// <summary><c>Q_fract_allpass[2][50][3][2]</c>.</summary>
    public static readonly float[][][][] QFractAllpass = new float[2][][][]
    {
        Cube(50, 3, 2),
        Cube(50, 3, 2),
    };

    /// <summary><c>phi_fract[2][50][2]</c>.</summary>
    public static readonly float[][][] PhiFract = new float[2][][]
    {
        Plane(50, 2),
        Plane(50, 2),
    };

    private static readonly float[] G0Q8 =
    [
        0.00746082949812f, 0.02270420949825f, 0.04546865930473f, 0.07266113929591f,
        0.09885108575264f, 0.11793710567217f, 0.125f,
    ];

    private static readonly float[] G0Q12 =
    [
        0.04081179924692f, 0.03812810994926f, 0.05144908135699f, 0.06399831151592f,
        0.07428313801106f, 0.08100347892914f, 0.08333333333333f,
    ];

    private static readonly float[] G1Q8 =
    [
        0.01565675600122f, 0.03752716391991f, 0.05417891378782f, 0.08417044116767f,
        0.10307344158036f, 0.12222452249753f, 0.125f,
    ];

    private static readonly float[] G2Q4 =
    [
        -0.05908211155639f, -0.04871498374946f, 0.0f, 0.07778723915851f,
         0.16486303567403f,  0.23279856662996f, 0.25f,
    ];

    /// <summary><c>g1_Q2</c> from <c>aacps.c</c>: the real two-band splitter's prototype.</summary>
    public static readonly float[] G1Q2 =
    [
        0.0f, 0.01899487526049f, 0.0f, -0.07293139167538f,
        0.0f, 0.30596630545168f, 0.5f,
    ];

    private static bool initialised;
    private static readonly object initLock = new();

    /// <summary><c>ff_ps_init</c>.</summary>
    public static void Initialise()
    {
        if (initialised) return;
        lock (initLock)
        {
            if (initialised) return;
            TableInit();
            initialised = true;
        }
    }

    private static void TableInit()
    {
        float[] ipdopdSin =
        [
            0f, (float)(1 / Math.Sqrt(2)), 1f, (float)(1 / Math.Sqrt(2)),
            0f, (float)(-1 / Math.Sqrt(2)), -1f, (float)(-1 / Math.Sqrt(2)),
        ];
        float[] ipdopdCos =
        [
            1f, (float)(1 / Math.Sqrt(2)), 0f, (float)(-1 / Math.Sqrt(2)),
            -1f, (float)(-1 / Math.Sqrt(2)), 0f, (float)(1 / Math.Sqrt(2)),
        ];

        // iid_par_dequant_default then iid_par_dequant_fine, one table as the C has it.
        float[] iidParDequant =
        [
            0.05623413251903f, 0.12589254117942f, 0.19952623149689f, 0.31622776601684f,
            0.44668359215096f, 0.63095734448019f, 0.79432823472428f, 1f,
            1.25892541179417f, 1.58489319246111f, 2.23872113856834f, 3.16227766016838f,
            5.01187233627272f, 7.94328234724282f, 17.7827941003892f,

            0.00316227766017f, 0.00562341325190f, 0.01f,             0.01778279410039f,
            0.03162277660168f, 0.05623413251903f, 0.07943282347243f, 0.11220184543020f,
            0.15848931924611f, 0.22387211385683f, 0.31622776601684f, 0.39810717055350f,
            0.50118723362727f, 0.63095734448019f, 0.79432823472428f, 1f,
            1.25892541179417f, 1.58489319246111f, 1.99526231496888f, 2.51188643150958f,
            3.16227766016838f, 4.46683592150963f, 6.30957344480193f, 8.91250938133745f,
            12.5892541179417f, 17.7827941003892f, 31.6227766016838f, 56.2341325190349f,
            100f,              177.827941003892f, 316.227766016837f,
        ];

        float[] iccInvq = [1f, 0.937f, 0.84118f, 0.60092f, 0.36764f, 0f, -0.589f, -1f];
        float[] acosIccInvq =
        [
            0f, 0.35685527f, 0.57133466f, 0.92614472f, 1.1943263f,
            (float)(Math.PI / 2), 2.2006171f, (float)Math.PI,
        ];

        for (int pd0 = 0; pd0 < 8; pd0++)
        {
            float pd0Re = ipdopdCos[pd0];
            float pd0Im = ipdopdSin[pd0];
            for (int pd1 = 0; pd1 < 8; pd1++)
            {
                float pd1Re = ipdopdCos[pd1];
                float pd1Im = ipdopdSin[pd1];
                for (int pd2 = 0; pd2 < 8; pd2++)
                {
                    float pd2Re = ipdopdCos[pd2];
                    float pd2Im = ipdopdSin[pd2];
                    float reSmooth = 0.25f * pd0Re + 0.5f * pd1Re + pd2Re;
                    float imSmooth = 0.25f * pd0Im + 0.5f * pd1Im + pd2Im;
                    // hypot() in double, as the C's libm call is.
                    float pdMag = (float)(1 / Hypot(imSmooth, reSmooth));
                    PdReSmooth[pd0 * 64 + pd1 * 8 + pd2] = reSmooth * pdMag;
                    PdImSmooth[pd0 * 64 + pd1 * 8 + pd2] = imSmooth * pdMag;
                }
            }
        }

        for (int iid = 0; iid < 46; iid++)
        {
            float c = iidParDequant[iid];               // linear intensity difference
            float c1 = (float)Math.Sqrt(2) / MathF.Sqrt(1.0f + c * c);
            float c2 = c * c1;
            for (int icc = 0; icc < 8; icc++)
            {
                float alpha = 0.5f * acosIccInvq[icc];
                float beta = alpha * (c1 - c2) * (float)(1 / Math.Sqrt(2));
                HA[iid][icc][0] = c2 * MathF.Cos(beta + alpha);
                HA[iid][icc][1] = c1 * MathF.Cos(beta - alpha);
                HA[iid][icc][2] = c2 * MathF.Sin(beta + alpha);
                HA[iid][icc][3] = c1 * MathF.Sin(beta - alpha);

                float rho = MathF.Max(iccInvq[icc], 0.05f);
                float alphaB = 0.5f * MathF.Atan2(2.0f * c * rho, c * c - 1.0f);
                float mu = c + 1.0f / c;
                mu = MathF.Sqrt(1 + (4 * rho * rho - 4) / (mu * mu));
                float gamma = MathF.Atan(MathF.Sqrt((1.0f - mu) / (1.0f + mu)));
                if (alphaB < 0) alphaB += (float)(Math.PI / 2);
                float alphaC = MathF.Cos(alphaB);
                float alphaS = MathF.Sin(alphaB);
                float gammaC = MathF.Cos(gamma);
                float gammaS = MathF.Sin(gamma);
                HB[iid][icc][0] = (float)(Math.Sqrt(2) * alphaC * gammaC);
                HB[iid][icc][1] = (float)(Math.Sqrt(2) * alphaS * gammaC);
                HB[iid][icc][2] = (float)(-Math.Sqrt(2) * alphaS * gammaS);
                HB[iid][icc][3] = (float)(Math.Sqrt(2) * alphaC * gammaS);
            }
        }

        sbyte[] fCenter20 = [-3, -1, 1, 3, 5, 7, 10, 14, 18, 22];
        sbyte[] fCenter34 =
        [
             2,  6, 10, 14, 18, 22, 26, 30,
            34,-10, -6, -2, 51, 57, 15, 21,
            27, 33, 39, 45, 54, 66, 78, 42,
           102, 66, 78, 90,102,114,126, 90,
        ];
        float[] fractionalDelayLinks = [0.43f, 0.75f, 0.347f];
        const float fractionalDelayGain = 0.39f;

        for (int k = 0; k < NrAllpassBands20; k++)
        {
            double fCenter = k < fCenter20.Length ? fCenter20[k] * 0.125 : k - 6.5f;
            for (int m = 0; m < PsLimits.ApLinks; m++)
            {
                double theta = -Math.PI * fractionalDelayLinks[m] * fCenter;
                QFractAllpass[0][k][m][0] = (float)Math.Cos(theta);
                QFractAllpass[0][k][m][1] = (float)Math.Sin(theta);
            }
            double thetaGain = -Math.PI * fractionalDelayGain * fCenter;
            PhiFract[0][k][0] = (float)Math.Cos(thetaGain);
            PhiFract[0][k][1] = (float)Math.Sin(thetaGain);
        }

        for (int k = 0; k < NrAllpassBands34; k++)
        {
            double fCenter = k < fCenter34.Length ? fCenter34[k] / 24.0 : k - 26.5f;
            for (int m = 0; m < PsLimits.ApLinks; m++)
            {
                double theta = -Math.PI * fractionalDelayLinks[m] * fCenter;
                QFractAllpass[1][k][m][0] = (float)Math.Cos(theta);
                QFractAllpass[1][k][m][1] = (float)Math.Sin(theta);
            }
            double thetaGain = -Math.PI * fractionalDelayGain * fCenter;
            PhiFract[1][k][0] = (float)Math.Cos(thetaGain);
            PhiFract[1][k][1] = (float)Math.Sin(thetaGain);
        }

        MakeFiltersFromProto(F20_0_8, G0Q8, 8);
        MakeFiltersFromProto(F34_0_12, G0Q12, 12);
        MakeFiltersFromProto(F34_1_8, G1Q8, 8);
        MakeFiltersFromProto(F34_2_4, G2Q4, 4);
    }

    /// <summary><c>make_filters_from_proto</c>. Taps 7..8 of each band stay zero.</summary>
    private static void MakeFiltersFromProto(float[][][] filter, float[] proto, int bands)
    {
        for (int q = 0; q < bands; q++)
            for (int n = 0; n < 7; n++)
            {
                double theta = 2 * Math.PI * (q + 0.5) * (n - 6) / bands;
                filter[q][n][0] = (float)(proto[n] * Math.Cos(theta));
                filter[q][n][1] = (float)(proto[n] * -Math.Sin(theta));
            }
    }

    /// <summary>
    /// C's <c>hypot</c>. <see cref="Math"/> has no two-argument hypot, and the naive
    /// <c>sqrt(x*x+y*y)</c> differs from it on the last bit for some inputs, so this is the
    /// scaled form the libm implementations use.
    /// </summary>
    private static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        if (x < y) (x, y) = (y, x);
        if (x == 0) return 0;
        double r = y / x;
        return x * Math.Sqrt(1 + r * r);
    }

    private static float[][][] Cube(int a, int b, int c)
    {
        var result = new float[a][][];
        for (int i = 0; i < a; i++) result[i] = Plane(b, c);
        return result;
    }

    private static float[][] Plane(int rows, int columns)
    {
        var result = new float[rows][];
        for (int i = 0; i < rows; i++) result[i] = new float[columns];
        return result;
    }
}

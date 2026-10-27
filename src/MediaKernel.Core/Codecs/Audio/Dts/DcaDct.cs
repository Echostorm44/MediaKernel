// DTS Coherent Acoustics - the fixed point inverse MDCT.
//
// Port of ffmpeg-master/libavcodec/dcadct.c. This is the integer half of the core's synthesis:
// filter_frame_fixed uses it where filter_frame_float uses TxMdct, and FFmpeg picks the fixed
// path whenever the packet carries an XLL asset or AV_CODEC_FLAG_BITEXACT is set - which is why
// the lossless extension needs this before it needs anything of its own.
//
// It is not an integer approximation of the float transform and must not be read as one. The
// structure is a hand-factored 32- or 64-point IMDCT: four butterfly summations, then a pair of
// 8-point DCTs, then three stages of twiddle multiplication, with every stage clipped to 24 bits
// signed. The clipping is part of the result, not a safety net: the reference decoder saturates
// at exactly these points and a port that clipped elsewhere, or not at all, would differ on loud
// input and agree on quiet input.

using System;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary>
/// <c>dcadct.c</c> - the fixed point inverse MDCT, at 32 and 64 points.
/// </summary>
internal static class DcaDct
{
    /// <summary><c>sum_a</c>.</summary>
    private static void SumA(ReadOnlySpan<int> input, Span<int> output, int len)
    {
        for (int i = 0; i < len; i++)
            output[i] = input[2 * i] + input[2 * i + 1];
    }

    /// <summary><c>sum_b</c>.</summary>
    private static void SumB(ReadOnlySpan<int> input, Span<int> output, int len)
    {
        output[0] = input[0];
        for (int i = 1; i < len; i++)
            output[i] = input[2 * i] + input[2 * i - 1];
    }

    /// <summary><c>sum_c</c>.</summary>
    private static void SumC(ReadOnlySpan<int> input, Span<int> output, int len)
    {
        for (int i = 0; i < len; i++)
            output[i] = input[2 * i];
    }

    /// <summary><c>sum_d</c>.</summary>
    private static void SumD(ReadOnlySpan<int> input, Span<int> output, int len)
    {
        output[0] = input[1];
        for (int i = 1; i < len; i++)
            output[i] = input[2 * i - 1] + input[2 * i + 1];
    }

    private static ReadOnlySpan<int> DctACosMod =>
    [
        8348215,  8027397,  7398092,  6484482,  5321677,  3954362,  2435084,   822227,
        8027397,  5321677,   822227, -3954362, -7398092, -8348215, -6484482, -2435084,
        7398092,   822227, -6484482, -8027397, -2435084,  5321677,  8348215,  3954362,
        6484482, -3954362, -8027397,   822227,  8348215,  2435084, -7398092, -5321677,
        5321677, -7398092, -2435084,  8348215,  -822227, -8027397,  3954362,  6484482,
        3954362, -8348215,  5321677,  2435084, -8027397,  6484482,   822227, -7398092,
        2435084, -6484482,  8348215, -7398092,  3954362,   822227, -5321677,  8027397,
         822227, -2435084,  3954362, -5321677,  6484482, -7398092,  8027397, -8348215,
    ];

    /// <summary><c>dct_a</c> - an 8 by 8 matrix product, accumulated in 64 bits.</summary>
    private static void DctA(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = DctACosMod;

        for (int i = 0; i < 8; i++)
        {
            long res = 0;
            for (int j = 0; j < 8; j++)
                res += (long)cosMod[i * 8 + j] * input[j];
            output[i] = DcaMath.Norm23(res);
        }
    }

    private static ReadOnlySpan<int> DctBCosMod =>
    [
         8227423,  7750063,  6974873,  5931642,  4660461,  3210181,  1636536,
         6974873,  3210181, -1636536, -5931642, -8227423, -7750063, -4660461,
         4660461, -3210181, -8227423, -5931642,  1636536,  7750063,  6974873,
         1636536, -7750063, -4660461,  5931642,  6974873, -3210181, -8227423,
        -1636536, -7750063,  4660461,  5931642, -6974873, -3210181,  8227423,
        -4660461, -3210181,  8227423, -5931642, -1636536,  7750063, -6974873,
        -6974873,  3210181,  1636536, -5931642,  8227423, -7750063,  4660461,
        -8227423,  7750063, -6974873,  5931642, -4660461,  3210181, -1636536,
    ];

    /// <summary><c>dct_b</c> - 8 by 7, with input[0] carried straight through at unit weight.</summary>
    /// <remarks>
    /// The C writes that carry as <c>input[0] * (INT64_C(1) &lt;&lt; 23)</c>, so the product is
    /// formed in 64 bits before <c>norm23</c> takes it back down. An <c>int</c> shift would
    /// overflow for an input above 2^8, which is every real sample.
    /// </remarks>
    private static void DctB(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = DctBCosMod;

        for (int i = 0; i < 8; i++)
        {
            long res = (long)input[0] * (1L << 23);
            for (int j = 0; j < 7; j++)
                res += (long)cosMod[i * 7 + j] * input[1 + j];
            output[i] = DcaMath.Norm23(res);
        }
    }

    private static ReadOnlySpan<int> ModACosMod =>
    [
          4199362,   4240198,   4323885,   4454708,
          4639772,   4890013,   5221943,   5660703,
         -6245623,  -7040975,  -8158494,  -9809974,
        -12450076, -17261920, -28585092, -85479984,
    ];

    /// <summary><c>mod_a</c>.</summary>
    private static void ModA(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = ModACosMod;

        for (int i = 0; i < 8; i++)
            output[i] = DcaMath.Mul23(cosMod[i], input[i] + input[8 + i]);

        for (int i = 8, k = 7; i < 16; i++, k--)
            output[i] = DcaMath.Mul23(cosMod[i], input[k] - input[8 + k]);
    }

    private static ReadOnlySpan<int> ModBCosMod =>
    [
        4214598,  4383036,  4755871,  5425934,
        6611520,  8897610, 14448934, 42791536,
    ];

    /// <summary><c>mod_b</c> - scales the second half of its input IN PLACE before summing.</summary>
    private static void ModB(Span<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = ModBCosMod;

        for (int i = 0; i < 8; i++)
            input[8 + i] = DcaMath.Mul23(cosMod[i], input[8 + i]);

        for (int i = 0; i < 8; i++)
            output[i] = input[i] + input[8 + i];

        for (int i = 8, k = 7; i < 16; i++, k--)
            output[i] = input[k] - input[8 + k];
    }

    private static ReadOnlySpan<int> ModCCosMod =>
    [
         1048892,  1051425,   1056522,   1064244,
         1074689,  1087987,   1104313,   1123884,
         1146975,  1173922,   1205139,   1241133,
         1282529,  1330095,   1384791,   1447815,
        -1520688, -1605358,  -1704360,  -1821051,
        -1959964, -2127368,  -2332183,  -2587535,
        -2913561, -3342802,  -3931480,  -4785806,
        -6133390, -8566050, -14253820, -42727120,
    ];

    /// <summary><c>mod_c</c>.</summary>
    private static void ModC(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = ModCCosMod;

        for (int i = 0; i < 16; i++)
            output[i] = DcaMath.Mul23(cosMod[i], input[i] + input[16 + i]);

        for (int i = 16, k = 15; i < 32; i++, k--)
            output[i] = DcaMath.Mul23(cosMod[i], input[k] - input[16 + k]);
    }

    /// <summary><c>clp_v</c> - saturate a run of values to 24 bits signed.</summary>
    private static void ClpV(Span<int> input, int len)
    {
        for (int i = 0; i < len; i++)
            input[i] = DcaMath.Clip23(input[i]);
    }

    private static ReadOnlySpan<int> Mod64ACosMod =>
    [
          4195568,   4205700,   4226086,    4256977,
          4298755,   4351949,   4417251,    4495537,
          4587901,   4695690,   4820557,    4964534,
          5130115,   5320382,   5539164,    5791261,
         -6082752,  -6421430,  -6817439,   -7284203,
         -7839855,  -8509474,  -9328732,  -10350140,
        -11654242, -13371208, -15725922,  -19143224,
        -24533560, -34264200, -57015280, -170908480,
    ];

    /// <summary><c>mod64_a</c>.</summary>
    private static void Mod64A(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = Mod64ACosMod;

        for (int i = 0; i < 16; i++)
            output[i] = DcaMath.Mul23(cosMod[i], input[i] + input[16 + i]);

        for (int i = 16, k = 15; i < 32; i++, k--)
            output[i] = DcaMath.Mul23(cosMod[i], input[k] - input[16 + k]);
    }

    private static ReadOnlySpan<int> Mod64BCosMod =>
    [
         4199362,  4240198,  4323885,  4454708,
         4639772,  4890013,  5221943,  5660703,
         6245623,  7040975,  8158494,  9809974,
        12450076, 17261920, 28585092, 85479984,
    ];

    /// <summary><c>mod64_b</c> - scales the second half in place, as <c>mod_b</c> does.</summary>
    private static void Mod64B(Span<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = Mod64BCosMod;

        for (int i = 0; i < 16; i++)
            input[16 + i] = DcaMath.Mul23(cosMod[i], input[16 + i]);

        for (int i = 0; i < 16; i++)
            output[i] = input[i] + input[16 + i];

        for (int i = 16, k = 15; i < 32; i++, k--)
            output[i] = input[k] - input[16 + k];
    }

    private static ReadOnlySpan<int> Mod64CCosMod =>
    [
          741511,    741958,    742853,    744199,
          746001,    748262,    750992,    754197,
          757888,    762077,    766777,    772003,
          777772,    784105,    791021,    798546,
          806707,    815532,    825054,    835311,
          846342,    858193,    870912,    884554,
          899181,    914860,    931667,    949686,
          969011,    989747,   1012012,   1035941,
        -1061684,  -1089412,  -1119320,  -1151629,
        -1186595,  -1224511,  -1265719,  -1310613,
        -1359657,  -1413400,  -1472490,  -1537703,
        -1609974,  -1690442,  -1780506,  -1881904,
        -1996824,  -2128058,  -2279225,  -2455101,
        -2662128,  -2909200,  -3208956,  -3579983,
        -4050785,  -4667404,  -5509372,  -6726913,
        -8641940, -12091426, -20144284, -60420720,
    ];

    /// <summary><c>mod64_c</c>.</summary>
    private static void Mod64C(ReadOnlySpan<int> input, Span<int> output)
    {
        ReadOnlySpan<int> cosMod = Mod64CCosMod;

        for (int i = 0; i < 32; i++)
            output[i] = DcaMath.Mul23(cosMod[i], input[i] + input[32 + i]);

        for (int i = 32, k = 31; i < 64; i++, k--)
            output[i] = DcaMath.Mul23(cosMod[i], input[k] - input[32 + k]);
    }

    /// <summary>
    /// <c>imdct_half_32</c> - 32 coefficients in, 32 time samples out.
    /// </summary>
    /// <remarks>
    /// The leading magnitude test is not an optimisation and cannot be dropped. The sum of
    /// absolute values decides a two-bit prescale, every stage then runs on the scaled input, and
    /// the scale is undone at the end under <c>clip23</c>. Without it a loud frame saturates in
    /// the middle stages; with a different threshold it saturates differently, which is a
    /// difference no quiet test signal would show.
    /// </remarks>
    public static void ImdctHalf32(Span<int> output, ReadOnlySpan<int> input)
    {
        if (output.Length < 32 || input.Length < 32)
            throw new ArgumentException(
                $"dca: the 32-point fixed IMDCT needs 32 coefficients in and 32 samples out; it "
              + $"was given {input.Length} and {output.Length}.");

        Span<int> bufA = stackalloc int[32];
        Span<int> bufB = stackalloc int[32];

        int mag = 0;
        for (int i = 0; i < 32; i++)
            mag += Math.Abs(input[i]);

        int shift = mag > 0x400000 ? 2 : 0;
        int round = shift > 0 ? 1 << (shift - 1) : 0;

        for (int i = 0; i < 32; i++)
            bufA[i] = (input[i] + round) >> shift;

        SumA(bufA, bufB, 16);
        SumB(bufA, bufB[16..], 16);
        ClpV(bufB, 32);

        SumA(bufB, bufA, 8);
        SumB(bufB, bufA[8..], 8);
        SumC(bufB[16..], bufA[16..], 8);
        SumD(bufB[16..], bufA[24..], 8);
        ClpV(bufA, 32);

        DctA(bufA, bufB);
        DctB(bufA[8..], bufB[8..]);
        DctB(bufA[16..], bufB[16..]);
        DctB(bufA[24..], bufB[24..]);
        ClpV(bufB, 32);

        ModA(bufB, bufA);
        ModB(bufB[16..], bufA[16..]);
        ClpV(bufA, 32);

        ModC(bufA, bufB);

        for (int i = 0; i < 32; i++)
            bufB[i] = DcaMath.Clip23(bufB[i] * (1 << shift));

        for (int i = 0, k = 31; i < 16; i++, k--)
        {
            output[i] = DcaMath.Clip23(bufB[i] - bufB[k]);
            output[16 + i] = DcaMath.Clip23(bufB[i] + bufB[k]);
        }
    }

    /// <summary><c>imdct_half_64</c> - the same shape at twice the length, for X96.</summary>
    public static void ImdctHalf64(Span<int> output, ReadOnlySpan<int> input)
    {
        if (output.Length < 64 || input.Length < 64)
            throw new ArgumentException(
                $"dca: the 64-point fixed IMDCT needs 64 coefficients in and 64 samples out; it "
              + $"was given {input.Length} and {output.Length}.");

        Span<int> bufA = stackalloc int[64];
        Span<int> bufB = stackalloc int[64];

        int mag = 0;
        for (int i = 0; i < 64; i++)
            mag += Math.Abs(input[i]);

        int shift = mag > 0x400000 ? 2 : 0;
        int round = shift > 0 ? 1 << (shift - 1) : 0;

        for (int i = 0; i < 64; i++)
            bufA[i] = (input[i] + round) >> shift;

        SumA(bufA, bufB, 32);
        SumB(bufA, bufB[32..], 32);
        ClpV(bufB, 64);

        SumA(bufB, bufA, 16);
        SumB(bufB, bufA[16..], 16);
        SumC(bufB[32..], bufA[32..], 16);
        SumD(bufB[32..], bufA[48..], 16);
        ClpV(bufA, 64);

        SumA(bufA, bufB, 8);
        SumB(bufA, bufB[8..], 8);
        SumC(bufA[16..], bufB[16..], 8);
        SumD(bufA[16..], bufB[24..], 8);
        SumC(bufA[32..], bufB[32..], 8);
        SumD(bufA[32..], bufB[40..], 8);
        SumC(bufA[48..], bufB[48..], 8);
        SumD(bufA[48..], bufB[56..], 8);
        ClpV(bufB, 64);

        DctA(bufB, bufA);
        DctB(bufB[8..], bufA[8..]);
        DctB(bufB[16..], bufA[16..]);
        DctB(bufB[24..], bufA[24..]);
        DctB(bufB[32..], bufA[32..]);
        DctB(bufB[40..], bufA[40..]);
        DctB(bufB[48..], bufA[48..]);
        DctB(bufB[56..], bufA[56..]);
        ClpV(bufA, 64);

        ModA(bufA, bufB);
        ModB(bufA[16..], bufB[16..]);
        ModB(bufA[32..], bufB[32..]);
        ModB(bufA[48..], bufB[48..]);
        ClpV(bufB, 64);

        Mod64A(bufB, bufA);
        Mod64B(bufB[32..], bufA[32..]);
        ClpV(bufA, 64);

        Mod64C(bufA, bufB);

        for (int i = 0; i < 64; i++)
            bufB[i] = DcaMath.Clip23(bufB[i] * (1 << shift));

        for (int i = 0, k = 63; i < 32; i++, k--)
        {
            output[i] = DcaMath.Clip23(bufB[i] - bufB[k]);
            output[32 + i] = DcaMath.Clip23(bufB[i] + bufB[k]);
        }
    }
}

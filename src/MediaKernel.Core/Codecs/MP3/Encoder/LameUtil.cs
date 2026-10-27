// Translated from lame-3.100/libmp3lame/util.c and util.h - the helpers the rest of the encoder
// reaches for. Functions are added here as their callers are translated, not all at once.
//
// USE_FAST_LOG IS ON IN THE REFERENCE BUILD, AND THAT IS NOT A DETAIL.
//
// util.h defines FAST_LOG10 two ways:
//
//     #ifdef USE_FAST_LOG
//     #define FAST_LOG10(x)     (fast_log2(x)*(LOG2/LOG10))
//     #else
//     #define FAST_LOG10(x)     log10(x)
//
// and configure.in turns USE_FAST_LOG on for host_cpu x86_64, amd64, *86 and powerpc, which is
// every machine anybody builds libmp3lame on; configMS.h defines it too. So the reference
// libmp3lame does not call log10 at all in the psychoacoustic model - it interpolates a
// 513-entry table of log2 values, with a documented absolute error around 1e-6. A port that
// called Math.Log10 would be more accurate than LAME and would never match it byte for byte.
//
// Both paths are here, selected by UseFastLog, which defaults to the x86-64 build's answer.
// The same configure branch also defines TAKEHIRO_IEEE754_HACK, which takehiro.c will need.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>util.h</c>'s constants and <c>util.c</c>'s helpers.</summary>
internal static class LameUtil
{
    /// <summary><c>FLT_EPSILON</c> from <c>float.h</c> - the gap above 1.0f.</summary>
    public const double FltEpsilon = 1.19209290e-07;

    /// <summary><c>LOG2</c> - <c>M_LN2</c>.</summary>
    public const double Log2Constant = 0.69314718055994530942;

    /// <summary><c>LOG10</c> - <c>M_LN10</c>.</summary>
    public const double Log10Constant = 2.30258509299404568402;

    /// <summary><c>SQRT2</c> - <c>M_SQRT2</c>.</summary>
    public const double Sqrt2 = 1.41421356237309504880;

    /// <summary>
    /// <c>USE_FAST_LOG</c> - on for x86-64, which is what the reference build is.
    /// </summary>
    public const bool UseFastLog = true;

    /// <summary><c>LOG2_SIZE</c>.</summary>
    private const int Log2Size = 512;

    /// <summary><c>LOG2_SIZE_L2</c>.</summary>
    private const int Log2SizeL2 = 9;

    /// <summary><c>log_table[LOG2_SIZE + 1]</c>.</summary>
    private static readonly float[] logTable = BuildLogTable();

    /// <summary>
    /// <c>init_log_table</c> - the C fills the table on first use behind a static flag; a static
    /// initialiser is the same thing and cannot race.
    /// </summary>
    /// <remarks>
    /// <c>1.0f + j / (ieee754_float32_t) LOG2_SIZE</c> is a float expression, which is then
    /// widened for <c>log()</c> and the quotient rounded once on store. Computing the whole
    /// thing in double would give different entries.
    /// </remarks>
    private static float[] BuildLogTable()
    {
        var table = new float[Log2Size + 1];
        for (int j = 0; j < Log2Size + 1; j++)
            table[j] = (float)(Math.Log(1.0f + j / (float)Log2Size) / Math.Log(2.0f));
        return table;
    }

    /// <summary>
    /// <c>fast_log2</c> - log2 by reading the exponent and interpolating the mantissa.
    /// </summary>
    /// <remarks>
    /// The C reads the float's bits through a union; <see cref="BitConverter.SingleToInt32Bits"/>
    /// is the same reinterpretation. <c>partial</c> is an <c>int</c> assigned to a FLOAT, so the
    /// conversion is explicit here.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastLog2(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        int mantisse = bits & 0x7fffff;
        float log2val = ((bits >> 23) & 0xFF) - 0x7f;
        float partial = mantisse & ((1 << (23 - Log2SizeL2)) - 1);
        partial *= 1.0f / (1 << (23 - Log2SizeL2));

        mantisse >>= 23 - Log2SizeL2;

        log2val += logTable[mantisse] * (1.0f - partial) + logTable[mantisse + 1] * partial;
        return log2val;
    }

    /// <summary><c>FAST_LOG10(x)</c>.</summary>
    /// <remarks>
    /// Returns double, not float, and that is the macro's own type: <c>fast_log2</c> returns a
    /// FLOAT which is immediately multiplied by the double <c>LOG2/LOG10</c>, so the result is
    /// a double and the caller's surrounding expression evaluates in double and rounds once.
    /// The non-fast path is <c>log10(x)</c>, a double as well. Narrowing here would round
    /// twice and diverge.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastLog10(float x) =>
        UseFastLog
            ? FastLog2(x) * (Log2Constant / Log10Constant)
            : Math.Log10(x);

    /// <summary><c>FAST_LOG10_X(x, y)</c> - a double, for the reason above.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastLog10X(float x, float y) =>
        UseFastLog
            ? FastLog2(x) * (Log2Constant / Log10Constant * y)
            : Math.Log10(x) * y;

    /// <summary><c>freq2bark</c> - hertz to barks.</summary>
    public static float Freq2Bark(float freq)
    {
        if (freq < 0) freq = 0;
        freq = (float)(freq * 0.001);
        return (float)(13.0 * Math.Atan(.76 * freq) + 3.5 * Math.Atan(freq * freq / (7.5 * 7.5)));
    }

    /// <summary>
    /// <c>ATHformula_GB</c> - Painter and Spanias as refitted by Gabriel Bouvigne.
    /// </summary>
    /// <remarks>
    /// Every term is a double expression in the C - the literals are doubles and <c>pow</c> and
    /// <c>exp</c> return doubles - so the whole sum evaluates in double and rounds once into the
    /// returned FLOAT.
    /// </remarks>
    public static float AthFormulaGb(float f, float value, float fMin, float fMax)
    {
        // The Hack that lets a caller ask for the lowest value.
        if (f < -.3) f = 3410;

        f = (float)(f / 1000);  // convert to khz
        f = Math.Max(fMin, f);
        f = Math.Min(fMax, f);

        return (float)(3.640 * Math.Pow(f, -0.8)
                     - 6.800 * Math.Exp(-0.6 * Math.Pow(f - 3.4, 2.0))
                     + 6.000 * Math.Exp(-0.15 * Math.Pow(f - 8.7, 2.0))
                     + (0.6 + 0.04 * value) * 0.001 * Math.Pow(f, 4.0));
    }

    /// <summary><c>SmpFrqIndex</c> - the sample rate's table index, and its MPEG version.</summary>
    /// <returns>0, 1 or 2, or -1 if the rate is not one MPEG audio defines.</returns>
    /// <remarks>
    /// Both 22050 and 11025 return index 0 with version 0: the version distinguishes MPEG-2
    /// from MPEG-2.5 nowhere in this function, and the caller separates them by whether the
    /// rate is below 16000.
    /// </remarks>
    public static int SmpFrqIndex(int sampleFreq, out int version)
    {
        switch (sampleFreq)
        {
            case 44100: version = 1; return 0;
            case 48000: version = 1; return 1;
            case 32000: version = 1; return 2;
            case 22050: version = 0; return 0;
            case 24000: version = 0; return 1;
            case 16000: version = 0; return 2;
            case 11025: version = 0; return 0;
            case 12000: version = 0; return 1;
            case 8000: version = 0; return 2;
            default: version = 0; return -1;
        }
    }

    /// <summary><c>FindNearestBitrate</c> - the legal rate closest to the one asked for.</summary>
    public static int FindNearestBitrate(int bRate, int version, int samplerate)
    {
        if (samplerate < 16000) version = 2;

        int bitrate = LameTables.bitrate_table[version][1];

        for (int i = 2; i <= 14; i++)
        {
            if (LameTables.bitrate_table[version][i] > 0)
            {
                if (Math.Abs(LameTables.bitrate_table[version][i] - bRate)
                    < Math.Abs(bitrate - bRate))
                    bitrate = LameTables.bitrate_table[version][i];
            }
        }
        return bitrate;
    }

    /// <summary><c>nearestBitrateFullIndex</c> - borrowed from the DM ABR presets.</summary>
    public static int NearestBitrateFullIndex(int bitrate)
    {
        int[] fullBitrateTable =
            [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

        int lowerRange, lowerRangeKbps, upperRange, upperRangeKbps;

        // Assume the rate asked for is 320 kbps, and correct that below.
        upperRangeKbps = fullBitrateTable[16];
        upperRange = 16;
        lowerRangeKbps = fullBitrateTable[16];
        lowerRange = 16;

        for (int b = 0; b < 16; b++)
        {
            if (Math.Max(bitrate, fullBitrateTable[b + 1]) != bitrate)
            {
                upperRangeKbps = fullBitrateTable[b + 1];
                upperRange = b + 1;
                lowerRangeKbps = fullBitrateTable[b];
                lowerRange = b;
                break;
            }
        }

        return upperRangeKbps - bitrate > bitrate - lowerRangeKbps ? lowerRange : upperRange;
    }

    /// <summary><c>map2MP3Frequency</c>.</summary>
    public static int Map2Mp3Frequency(int freq)
    {
        if (freq <= 8000) return 8000;
        if (freq <= 11025) return 11025;
        if (freq <= 12000) return 12000;
        if (freq <= 16000) return 16000;
        if (freq <= 22050) return 22050;
        if (freq <= 24000) return 24000;
        if (freq <= 32000) return 32000;
        if (freq <= 44100) return 44100;
        return 48000;
    }

    /// <summary><c>BitrateIndex</c> - the table index of a bitrate, or -1.</summary>
    public static int BitrateIndex(int bRate, int version, int samplerate)
    {
        if (samplerate < 16000) version = 2;
        for (int i = 0; i <= 14; i++)
            if (LameTables.bitrate_table[version][i] > 0
                && LameTables.bitrate_table[version][i] == bRate)
                return i;
        return -1;
    }

    /// <summary><c>isResamplingNecessary</c> - within half a per mille counts as equal.</summary>
    public static bool IsResamplingNecessary(ref SessionConfig cfg)
    {
        int l = (int)(cfg.SamplerateOut * 0.9995f);
        int h = (int)(cfg.SamplerateOut * 1.0005f);
        return cfg.SamplerateIn < l || h < cfg.SamplerateIn;
    }

    /// <summary><c>ATHformula</c> - the absolute threshold of hearing, in dB, at <paramref name="f"/> Hz.</summary>
    public static float AthFormula(ref SessionConfig cfg, float f) => cfg.AthType switch
    {
        0 => AthFormulaGb(f, 9, 0.1f, 24.0f),
        1 => AthFormulaGb(f, -1, 0.1f, 24.0f), // over sensitive, should probably be removed
        2 => AthFormulaGb(f, 0, 0.1f, 24.0f),
        3 => AthFormulaGb(f, 1, 0.1f, 24.0f) + 6, // modification of GB formula by Roel
        4 => AthFormulaGb(f, cfg.AthCurve, 0.1f, 24.0f),
        5 => AthFormulaGb(f, cfg.AthCurve, 3.41f, 16.1f),
        _ => AthFormulaGb(f, 0, 0.1f, 24.0f),
    };

    /// <summary><c>gcd</c> - Euclid, written recursively as in the C.</summary>
    /// <remarks>
    /// Only <c>fill_buffer_resample</c> uses it, to reduce the input and output sample rates
    /// to the stride of the resampler's filter. That function is a recorded divergence,
    /// because libmp3lame.c always sets the two rates equal; this is here so that the file has
    /// no untranslated function left, and so that the divergence is the resampler alone.
    /// </remarks>
    public static int Gcd(int i, int j) => j != 0 ? Gcd(j, i % j) : i;

    /// <summary>
    /// <c>blackman</c> - one tap of the resampler's blackman-windowed FIR, from Stearns and
    /// David, <i>Signal Processing Algorithms in Fortran and C</i> (Prentice-Hall, 1992).
    /// </summary>
    /// <remarks>
    /// The narrowing points are the C's, not tidied. <c>FLOAT</c> is 32-bit, so <c>x2</c>,
    /// <c>bkwn</c> and <c>wcn</c> each round to float at their store even though the
    /// expression that produced them was evaluated in double, and the return value rounds
    /// again. Collapsing any of those into one double expression changes taps in the last
    /// bit, and the taps are normalised by their own sum, so the error spreads across the
    /// whole window rather than staying in one coefficient.
    /// </remarks>
    public static float Blackman(float x, float fcn, int l)
    {
        float wcn = (float)(Math.PI * fcn);

        x /= l;
        if (x < 0) x = 0;
        if (x > 1) x = 1;
        float x2 = (float)(x - .5);

        float bkwn = (float)(0.42 - 0.5 * Math.Cos(2 * x * Math.PI)
                                  + 0.08 * Math.Cos(4 * x * Math.PI));
        if (Math.Abs(x2) < 1e-9)
            return (float)(wcn / Math.PI);

        // l * wcn * x2 is a float product in the C, widened only by the call to sin.
        return (float)(bkwn * Math.Sin(l * wcn * x2) / (Math.PI * l * x2));
    }

    /// <summary>
    /// <c>fill_buffer_resample</c> - resamples one channel into <paramref name="outbuf"/> with
    /// a blackman-windowed FIR, and reports how much input it consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reached only when the input and output sample rates differ by more than half a per
    /// mille. ffmpeg's libmp3lame.c sets them equal, so nothing in the ffmpeg path comes here;
    /// <c>Mp3Encoder</c> in <c>Ripping/</c> can, because a CD rip is 44.1 kHz and a caller may
    /// ask for a different output rate.
    /// </para>
    /// <para>
    /// The state is allocated on first use, exactly where the C allocates it, rather than in
    /// <c>EncStateVar.Allocate</c>: <c>BLACKSIZE</c> depends on the ratio, and the 641 window
    /// buffers would otherwise be allocated for every encode that never resamples. It happens
    /// once per encoder, not per frame.
    /// </para>
    /// <para>
    /// <c>PRECOMPUTE</c> is defined at the top of <c>util.c</c>, so the inner loop reads the
    /// precomputed window rather than calling <see cref="Blackman"/> per tap - the C's own
    /// comment on the other branch is "very slow!". The two branches are not equivalent: the
    /// precomputed taps are normalised to sum to one and quantised to the nearest of
    /// <c>2 * bpc + 1</c> offsets, so this is the arm the reference runs and the only one
    /// translated.
    /// </para>
    /// </remarks>
    public static int FillBufferResample(
        LameInternalFlags gfc,
        float[] outbuf, int outbufOffset, int desiredLen,
        float[] inbuf, int inbufOffset, int len,
        out int numUsed, int ch)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        double resampleRatio = (double)cfg.SamplerateIn / cfg.SamplerateOut;
        int i, j = 0, k;

        int bpc = cfg.SamplerateOut / Gcd(cfg.SamplerateOut, cfg.SamplerateIn);
        if (bpc > LameEncStateConstants.Bpc) bpc = LameEncStateConstants.Bpc;

        // A FLOAT holding 0 or 1 in the C, then added to an int - so an integral ratio makes
        // the filter even and everything else leaves it odd.
        //
        // FltEpsilon is the C's constant: FLT_EPSILON is the gap above 1.0, 1.19e-7, where
        // .NET's float.Epsilon is the smallest denormal, 1.4e-45. Measured rather than
        // assumed, the two cannot be told apart here - for integer sample rates the quotient
        // is either exactly integral, where the difference is zero and zero is below both
        // constants, or at least about 1/samplerate_out away from an integer, which is four
        // orders of magnitude above the larger one. The C's constant is used anyway, because
        // the next person to read this should see FLT_EPSILON, not a value that happens to
        // work; mp3enc-resample confirms the filter length itself is covered, and fails at
        // output sample 0 if this line stops adding to filter_l at all.
        float intratio =
            Math.Abs(resampleRatio - Math.Floor(.5 + resampleRatio)) < FltEpsilon ? 1f : 0f;
        float fcn = (float)(1.00 / resampleRatio);
        if (fcn > 1.00) fcn = 1.00f;
        int filterL = 31;                 // must be odd
        filterL += (int)intratio;         // unless resample_ratio is an integer, then even

        int blacksize = filterL + 1;      // size of data needed for FIR

        if (gfc.FillBufferResampleInit == 0)
        {
            esv.InbufOld![0] = new float[blacksize];
            esv.InbufOld[1] = new float[blacksize];
            for (i = 0; i <= 2 * bpc; ++i)
                esv.Blackfilt![i] = new float[blacksize];

            esv.Itime[0] = 0;
            esv.Itime[1] = 0;

            // precompute blackman filter coefficients
            for (j = 0; j <= 2 * bpc; j++)
            {
                float sum = 0f;
                float offs = (float)((j - bpc) / (2.0 * bpc));
                float[] filt = esv.Blackfilt![j]!;
                for (i = 0; i <= filterL; i++)
                    sum += filt[i] = Blackman(i - offs, fcn, filterL);
                for (i = 0; i <= filterL; i++)
                    filt[i] /= sum;
            }
            gfc.FillBufferResampleInit = 1;
        }

        float[] inbufOld = esv.InbufOld![ch]!;

        // time of j'th element in inbuf = itime + j/ifreq;
        // time of k'th element in outbuf = j/ofreq
        for (k = 0; k < desiredLen; k++)
        {
            double time0 = k * resampleRatio;   // time of k'th output sample

            j = (int)Math.Floor(time0 - esv.Itime[ch]);

            // check if we need more input data
            if (filterL + j - filterL / 2 >= len) break;

            // blackman filter: by default the window is centred at j + .5 * (filter_l % 2),
            // but it is wanted centred at time0
            float offset = (float)(time0 - esv.Itime[ch] - (j + .5 * (filterL % 2)));
            Debug.Assert(Math.Abs(offset) <= .501);

            // find the closest precomputed window for this offset
            int joff = (int)Math.Floor(offset * 2 * bpc + bpc + .5);

            float xvalue = 0f;
            float[] filt = esv.Blackfilt![joff]!;
            for (i = 0; i <= filterL; ++i)
            {
                int j2 = i + j - filterL / 2;
                Debug.Assert(j2 < len);
                Debug.Assert(j2 + blacksize >= 0);
                float y = j2 < 0 ? inbufOld[blacksize + j2] : inbuf[inbufOffset + j2];
                xvalue += y * filt[i];
            }
            outbuf[outbufOffset + k] = xvalue;
        }

        // k is the number of samples added to outbuf, and the last one used input
        // [j - filter_l/2, j + filter_l - filter_l/2]. How many inputs that was:
        numUsed = Math.Min(len, filterL + j - filterL / 2);

        // Advance the input time counter by the samples used, then normalise so that the next
        // output sample is at time 0 and the next input buffer is at time itime[ch].
        esv.Itime[ch] += numUsed - k * resampleRatio;

        // save the last BLACKSIZE samples into inbuf_old
        if (numUsed >= blacksize)
        {
            for (i = 0; i < blacksize; i++)
                inbufOld[i] = inbuf[inbufOffset + numUsed + i - blacksize];
        }
        else
        {
            int nShift = blacksize - numUsed;   // number of samples to shift

            // shift n_shift samples down by num_used, making room for the new ones
            for (i = 0; i < nShift; ++i)
                inbufOld[i] = inbufOld[i + numUsed];

            // shift in the num_used new samples
            for (j = 0; i < blacksize; ++i, ++j)
                inbufOld[i] = inbuf[inbufOffset + j];

            Debug.Assert(j == numUsed);
        }
        return k;   // the number of samples created at the new sample rate
    }
}

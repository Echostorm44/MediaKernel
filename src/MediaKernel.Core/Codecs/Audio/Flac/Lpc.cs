using System;

namespace MediaKernel.Core.Codecs.Audio.Flac;

/// <summary>LPC analysis type — FFmpeg's <c>enum FFLPCType</c>.</summary>
public enum LpcType
{
    /// <summary>FF_LPC_TYPE_DEFAULT: use the codec's own default.</summary>
    Default = -1,
    /// <summary>FF_LPC_TYPE_NONE: no LPC prediction.</summary>
    None = 0,
    /// <summary>FF_LPC_TYPE_FIXED: the fixed predictors.</summary>
    Fixed = 1,
    /// <summary>FF_LPC_TYPE_LEVINSON: Levinson-Durbin recursion.</summary>
    Levinson = 2,
    /// <summary>FF_LPC_TYPE_CHOLESKY: Cholesky factorisation.</summary>
    Cholesky = 3,
}

/// <summary>
/// FFmpeg's <c>lpc.c</c> and <c>lpc_functions.h</c>: linear-prediction analysis shared by the
/// lossless encoders.
/// </summary>
/// <remarks>
/// <c>LPC_USE_DOUBLE</c> is defined before <c>lpc_functions.h</c> is included by <c>lpc.c</c>,
/// so <c>LPC_TYPE</c> is <c>double</c> throughout and <c>LPC_SRA_R</c>/<c>LPC_MUL26</c> are
/// identities. The coefficient arrays are two-dimensional in the C and flat here, with the
/// stride the C passes as <c>lpc_stride</c> kept explicit.
/// </remarks>
public sealed class Lpc
{
    /// <summary>MIN_LPC_ORDER</summary>
    public const int MinLpcOrder = 1;
    /// <summary>MAX_LPC_ORDER</summary>
    public const int MaxLpcOrder = 32;

    /// <summary>ORDER_METHOD_EST</summary>
    public const int OrderMethodEst = 0;
    /// <summary>ORDER_METHOD_2LEVEL</summary>
    public const int OrderMethod2Level = 1;
    /// <summary>ORDER_METHOD_4LEVEL</summary>
    public const int OrderMethod4Level = 2;
    /// <summary>ORDER_METHOD_8LEVEL</summary>
    public const int OrderMethod8Level = 3;
    /// <summary>ORDER_METHOD_SEARCH</summary>
    public const int OrderMethodSearch = 4;
    /// <summary>ORDER_METHOD_LOG</summary>
    public const int OrderMethodLog = 5;

    private int blocksize;
    private int maxOrder;
    private LpcType lpcType;

    // windowed_buffer, and the offset at which windowed_samples starts inside it. The C sets
    // windowed_samples = windowed_buffer + FFALIGN(max_order, 4) and zero-fills the whole
    // allocation, because lpc_compute_autocorr reads up to `lag` doubles *before* the first
    // sample. Those zeros are load-bearing, not slack.
    private double[] windowedBuffer = [];
    private int windowedOffset;

    // double lpc[MAX_LPC_ORDER][MAX_LPC_ORDER], a local in the C. It is a field here because a
    // 8 KB allocation per frame per channel is not something this tree does.
    private readonly double[] lpc = new double[MaxLpcOrder * MaxLpcOrder];

    /// <summary>ff_lpc_init</summary>
    public void Init(int blocksize, int maxOrder, LpcType lpcType)
    {
        this.blocksize = blocksize;
        this.maxOrder = maxOrder;
        this.lpcType = lpcType;

        windowedOffset = (maxOrder + 3) & ~3;                      // FFALIGN(max_order, 4)
        windowedBuffer = new double[blocksize + 2 + windowedOffset];
    }

    /// <summary>ff_lpc_end</summary>
    public void End()
    {
        windowedBuffer = [];
        windowedOffset = 0;
    }

    private Span<double> WindowedSamples => windowedBuffer.AsSpan(windowedOffset);

    /// <summary>
    /// compute_lpc_coefs: Levinson-Durbin recursion, producing LPC coefficients from
    /// autocorrelation data.
    /// </summary>
    /// <remarks>
    /// The C advances <c>autoc</c> by one when it takes the error from <c>autoc[0]</c>, so every
    /// later <c>autoc[i]</c> is one element further along; <paramref name="autocOffset"/> carries
    /// that. Returns -1 where the C does, which only happens when <paramref name="fail"/> is set.
    /// </remarks>
    public static int ComputeLpcCoefs(ReadOnlySpan<double> autoc, int autocOffset, int i,
                                      int maxOrder, Span<double> lpc, int lpcOffset,
                                      int lpcStride, bool fail, bool normalize,
                                      ref double errRef, bool hasErrPtr)
    {
        double err = 0;
        int lpcLast = lpcOffset;

        if (normalize)
        {
            if (i == 0)
            {
                err = autoc[autocOffset];
                autocOffset++;
            }
            else
            {
                err = errRef;
            }
        }

        if (fail && (autoc[autocOffset + maxOrder - 1] == 0 || err <= 0))
            return -1;

        for (; i < maxOrder; i++)
        {
            double r = -autoc[autocOffset + i];

            if (normalize)
            {
                for (int j = 0; j < i; j++)
                    r -= lpc[lpcLast + j] * autoc[autocOffset + i - j - 1];

                if (err != 0)
                    r /= err;
                err *= 1.0f - (r * r);
            }

            lpc[lpcOffset + i] = r;

            for (int j = 0; j < (i + 1) >> 1; j++)
            {
                double f = lpc[lpcLast + j];
                double b = lpc[lpcLast + i - 1 - j];
                lpc[lpcOffset + j] = f + r * b;
                lpc[lpcOffset + i - 1 - j] = b + r * f;
            }

            if (fail && err < 0)
                return -1;

            lpcLast = lpcOffset;
            lpcOffset += lpcStride;
        }

        if (hasErrPtr)
            errRef = err;

        return 0;
    }

    /// <summary>compute_ref_coefs: Schur recursion, reflection coefficients from autocorrelation.</summary>
    public static void ComputeRefCoefs(ReadOnlySpan<double> autoc, int maxOrder,
                                       Span<double> reflection, Span<double> error)
    {
        Span<double> gen0 = stackalloc double[MaxLpcOrder];
        Span<double> gen1 = stackalloc double[MaxLpcOrder];

        for (int i = 0; i < maxOrder; i++)
            gen0[i] = gen1[i] = autoc[i + 1];

        double err = autoc[0];
        reflection[0] = -gen1[0] / (err != 0 ? err : 1);
        err += gen1[0] * reflection[0];
        if (!error.IsEmpty)
            error[0] = err;

        for (int i = 1; i < maxOrder; i++)
        {
            for (int j = 0; j < maxOrder - i; j++)
            {
                gen1[j] = gen1[j + 1] + reflection[i - 1] * gen0[j];
                gen0[j] = gen1[j + 1] * reflection[i - 1] + gen0[j];
            }
            reflection[i] = -gen1[0] / (err != 0 ? err : 1);
            err += gen1[0] * reflection[i];
            if (!error.IsEmpty)
                error[i] = err;
        }
    }

    /// <summary>lpc_apply_welch_window_c</summary>
    public static void LpcApplyWelchWindowC(ReadOnlySpan<int> data, int len, Span<double> wData)
    {
        if (len == 1)
        {
            wData[0] = 0.0;
            return;
        }

        int n2 = len >> 1;
        double c = 2.0 / (len - 1.0);

        if ((len & 1) != 0)
        {
            for (int i = 0; i < n2; i++)
            {
                double w = c - i - 1.0;
                w = 1.0 - (w * w);
                wData[i] = data[i] * w;
                wData[len - 1 - i] = data[len - 1 - i] * w;
            }
            wData[n2] = 0.0;
            return;
        }

        // The C advances both pointers to the midpoint and indexes either side of it.
        for (int i = 0; i < n2; i++)
        {
            double w = c - n2 + i;
            w = 1.0 - (w * w);
            wData[n2 - i - 1] = data[n2 - i - 1] * w;
            wData[n2 + i] = data[n2 + i] * w;
        }
    }

    /// <summary>
    /// lpc_compute_autocorr_c. <paramref name="dataOffset"/> is where the samples begin inside
    /// <paramref name="buffer"/>; the C relies on reading up to <c>lag</c> zeroed doubles before
    /// that point.
    /// </summary>
    public static void LpcComputeAutocorrC(ReadOnlySpan<double> buffer, int dataOffset, int len,
                                           int lag, Span<double> autoc)
    {
        int j;
        for (j = 0; j < lag; j += 2)
        {
            double sum0 = 1.0, sum1 = 1.0;
            for (int i = j; i < len; i++)
            {
                sum0 += buffer[dataOffset + i] * buffer[dataOffset + i - j];
                sum1 += buffer[dataOffset + i] * buffer[dataOffset + i - j - 1];
            }
            autoc[j] = sum0;
            autoc[j + 1] = sum1;
        }

        if (j == lag)
        {
            double sum = 1.0;
            for (int i = j - 1; i < len; i++)
                sum += buffer[dataOffset + i] * buffer[dataOffset + i - j];
            autoc[j] = sum;
        }
    }

    /// <summary>quantize_lpc_coefs</summary>
    /// <remarks>
    /// <c>lrintf</c> takes a <c>float</c>, so the C narrows the running <c>double</c> error to
    /// single precision before rounding it to nearest-even. That narrowing changes results and is
    /// reproduced here rather than tidied away.
    /// </remarks>
    public static void QuantizeLpcCoefs(Span<double> lpcIn, int order, int precision,
                                        Span<int> lpcOut, out int shift, int minShift,
                                        int maxShift, int zeroShift)
    {
        int qmax = (1 << (precision - 1)) - 1;

        double cmax = 0.0;
        for (int i = 0; i < order; i++)
            cmax = Math.Max(cmax, Math.Abs(lpcIn[i]));

        if (cmax * (1 << maxShift) < 1.0)
        {
            shift = zeroShift;
            lpcOut[..order].Clear();
            return;
        }

        int sh = maxShift;
        while (cmax * (1 << sh) > qmax && sh > minShift)
            sh--;

        // Negative shifts are unsupported by the decoder, so scale the coefficients instead.
        if (sh == 0 && cmax > qmax)
        {
            double scale = qmax / cmax;
            for (int i = 0; i < order; i++)
                lpcIn[i] *= scale;
        }

        double error = 0;
        for (int i = 0; i < order; i++)
        {
            error -= lpcIn[i] * (1 << sh);
            long rounded = (long)MathF.Round((float)error, MidpointRounding.ToEven);
            lpcOut[i] = (int)Math.Clamp(rounded, -qmax, qmax);
            error -= lpcOut[i];
        }
        shift = sh;
    }

    /// <summary>estimate_best_order</summary>
    public static int EstimateBestOrder(ReadOnlySpan<double> reflection, int minOrder, int maxOrder)
    {
        int est = minOrder;
        for (int i = maxOrder - 1; i >= minOrder - 1; i--)
        {
            if (reflection[i] > 0.10)
            {
                est = i + 1;
                break;
            }
        }
        return est;
    }

    /// <summary>ff_lpc_calc_ref_coefs</summary>
    public int CalcRefCoefs(ReadOnlySpan<int> samples, int order, Span<double> reflection)
    {
        Span<double> autoc = stackalloc double[MaxLpcOrder + 1];

        LpcApplyWelchWindowC(samples, blocksize, WindowedSamples);
        LpcComputeAutocorrC(windowedBuffer, windowedOffset, blocksize, order, autoc);
        ComputeRefCoefs(autoc, order, reflection, Span<double>.Empty);

        return order;
    }

    /// <summary>ff_lpc_calc_ref_coefs_f</summary>
    public double CalcRefCoefsF(ReadOnlySpan<float> samples, int len, int order,
                                Span<double> reflection)
    {
        Span<double> autoc = stackalloc double[MaxLpcOrder + 1];
        Span<double> error = stackalloc double[MaxLpcOrder + 1];
        autoc.Clear();
        error.Clear();

        const double a = 0.5f;
        const double b = 1.0f - a;

        Span<double> windowed = WindowedSamples;
        for (int i = 0; i <= len / 2; i++)
        {
            double weight = a - b * Math.Cos((2 * Math.PI * i) / (len - 1));
            windowed[i] = weight * samples[i];
            windowed[len - 1 - i] = weight * samples[len - 1 - i];
        }

        LpcComputeAutocorrC(windowedBuffer, windowedOffset, len, order, autoc);
        double signal = autoc[0];
        ComputeRefCoefs(autoc, order, reflection, error);

        double avgErr = 0.0f;
        for (int i = 0; i < order; i++)
            avgErr = (avgErr + error[i]) / 2.0f;

        return avgErr != 0 ? signal / avgErr : double.NaN;
    }

    /// <summary>
    /// ff_lpc_calc_coefs: LPC coefficients for every order from
    /// <paramref name="minOrder"/> to <paramref name="maxOrder"/>.
    /// </summary>
    /// <param name="coefs">
    /// Flat stand-in for <c>int32_t coefs[][MAX_LPC_ORDER]</c>: row <c>i</c> at
    /// <c>i * MAX_LPC_ORDER</c>.
    /// </param>
    public int CalcCoefs(ReadOnlySpan<int> samples, int blocksize, int minOrder, int maxOrder,
                         int precision, Span<int> coefs, Span<int> shift, LpcType lpcType,
                         int lpcPasses, int omethod, int minShift, int maxShift, int zeroShift)
    {
        if (maxOrder < MinLpcOrder || maxOrder > MaxLpcOrder)
            throw new ArgumentOutOfRangeException(nameof(maxOrder));
        if (lpcType != LpcType.Levinson)
        {
            // The Cholesky path needs libavutil's LLS solver, which this tree does not have.
            // It is reachable only through an explicit -lpc_type cholesky, never from a default
            // encode, and failing loudly is better than quietly substituting Levinson.
            throw new NotSupportedException(
                $"lpc_type {lpcType} is not ported; only Levinson-Durbin is.");
        }

        Span<double> autoc = stackalloc double[MaxLpcOrder + 1];
        Span<double> reflection = stackalloc double[MaxLpcOrder];
        reflection.Clear();

        if (blocksize != this.blocksize || maxOrder != this.maxOrder || lpcType != this.lpcType)
        {
            End();
            Init(blocksize, maxOrder, lpcType);
        }

        if (lpcPasses <= 0)
            lpcPasses = 2;

        LpcApplyWelchWindowC(samples, blocksize, WindowedSamples);
        LpcComputeAutocorrC(windowedBuffer, windowedOffset, blocksize, maxOrder, autoc);

        double unusedErr = 0;
        ComputeLpcCoefs(autoc, 0, 0, maxOrder, lpc, 0, MaxLpcOrder, false, true,
                        ref unusedErr, false);

        for (int i = 0; i < maxOrder; i++)
            reflection[i] = Math.Abs(lpc[i * MaxLpcOrder + i]);

        int optOrder = maxOrder;

        if (omethod == OrderMethodEst)
        {
            optOrder = EstimateBestOrder(reflection, minOrder, maxOrder);
            int i = optOrder - 1;
            QuantizeLpcCoefs(lpc.AsSpan(i * MaxLpcOrder), i + 1, precision,
                             coefs.Slice(i * MaxLpcOrder, MaxLpcOrder), out int sh,
                             minShift, maxShift, zeroShift);
            shift[i] = sh;
        }
        else
        {
            for (int i = minOrder - 1; i < maxOrder; i++)
            {
                QuantizeLpcCoefs(lpc.AsSpan(i * MaxLpcOrder), i + 1, precision,
                                 coefs.Slice(i * MaxLpcOrder, MaxLpcOrder), out int sh,
                                 minShift, maxShift, zeroShift);
                shift[i] = sh;
            }
        }

        return optOrder;
    }
}

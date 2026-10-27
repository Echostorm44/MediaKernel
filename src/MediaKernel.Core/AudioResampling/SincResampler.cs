// Kaiser-windowed sinc resampler with polyphase filter bank.
// This is the core resampling algorithm, equivalent to FFmpeg's resample.c.
// Supports arbitrary rational rate conversion with configurable quality.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.AudioResampling;

/// <summary>
/// Quality presets controlling filter length and Kaiser window parameter.
/// Higher quality = more taps = better frequency response but more CPU.
/// </summary>
public enum ResampleQuality
{
    /// <summary>8 taps, fast. Acceptable for speech.</summary>
    Fast,
    /// <summary>16 taps, good. Suitable for music preview.</summary>
    Medium,
    /// <summary>32 taps, high quality. Good for music production.</summary>
    High,
    /// <summary>64 taps, best quality. Broadcast/mastering.</summary>
    Best
}

/// <summary>
/// Polyphase sinc resampler with Kaiser window. Processes one channel at a time
/// (planar float samples). Create one instance per channel, or reuse with Reset().
/// </summary>
public sealed class SincResampler : IDisposable
{
    private int filterLength;                // Taps per phase (mutable for downsample adjustment)
    private readonly int phaseCount;         // Number of filter phases
    private readonly float[] filterBank;     // filterLength × phaseCount coefficients
    private float[] historyBuffer;           // Buffer for input history
    private int historyLength;               // Valid samples in history
    private long phase;                      // Current fractional phase [0, phaseCount)
    private readonly int srcStep;            // Integer input samples to advance per output sample
    private readonly int srcPhaseStep;       // Fractional phase advance per output sample [0, phaseCount)
    private bool disposed;

    /// <summary>Input sample rate.</summary>
    public int SourceRate { get; }

    /// <summary>Output sample rate.</summary>
    public int DestinationRate { get; }

    /// <summary>Number of taps in the FIR filter per phase.</summary>
    public int FilterTaps => filterLength;

    /// <summary>Latency in output samples introduced by the filter.</summary>
    public int LatencySamples => (int)((long)filterLength / 2 * DestinationRate / SourceRate);

    public SincResampler(int sourceRate, int destinationRate, ResampleQuality quality = ResampleQuality.High)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (destinationRate <= 0) throw new ArgumentOutOfRangeException(nameof(destinationRate));

        SourceRate = sourceRate;
        DestinationRate = destinationRate;

        // Quality determines base filter length and Kaiser window parameter
        (int baseTaps, double kaiserBeta, int minPhases) = quality switch
        {
            ResampleQuality.Fast => (8, 5.0, 128),
            ResampleQuality.Medium => (16, 7.0, 256),
            ResampleQuality.High => (32, 9.0, 512),
            ResampleQuality.Best => (64, 10.0, 1024),
            _ => (32, 9.0, 512)
        };

        // Use GCD-based exact ratio to eliminate fixed-point quantization drift.
        // srcRate/dstRate = srcSimplified/dstSimplified (irreducible fraction).
        // phaseCount = dstSimplified × M, where M ensures at least minPhases.
        int gcd = Gcd(sourceRate, destinationRate);
        int srcSimplified = sourceRate / gcd;      // e.g., 44100/300 = 147
        int dstSimplified = destinationRate / gcd;  // e.g., 48000/300 = 160

        // Choose phaseCount as a multiple of dstSimplified ≥ minPhases
        int phaseMultiplier = Math.Max(1, (minPhases + dstSimplified - 1) / dstSimplified);
        phaseCount = dstSimplified * phaseMultiplier;
        // dstRate field removed — phase now tracked in [0, phaseCount)

        // Exact integer arithmetic: per output sample advance srcSimplified×M phases.
        // Decompose into integer input samples (srcStep) and fractional phase (srcPhaseStep).
        int totalPhaseStep = srcSimplified * phaseMultiplier;
        srcStep = totalPhaseStep / phaseCount;
        srcPhaseStep = totalPhaseStep % phaseCount;

        // If downsampling, widen the filter to prevent aliasing
        double cutoff = Math.Min(1.0, (double)destinationRate / sourceRate);
        filterLength = baseTaps;
        if (cutoff < 1.0)
            filterLength = (int)(baseTaps / cutoff) | 1;

        filterBank = new float[filterLength * phaseCount];
        BuildFilterBank(filterLength, phaseCount, cutoff, kaiserBeta);

        historyBuffer = new float[filterLength + 4096];
        historyLength = 0;
        phase = 0;
    }

    /// <summary>
    /// Resample a block of input samples, producing output samples.
    /// Call repeatedly with sequential input blocks (streaming).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Resample(ReadOnlySpan<float> input, Span<float> output)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SincResampler));

        // If same rate, just copy
        if (SourceRate == DestinationRate)
        {
            int toCopy = Math.Min(input.Length, output.Length);
            input.Slice(0, toCopy).CopyTo(output);
            return toCopy;
        }

        // Append input to history buffer
        EnsureHistoryCapacity(historyLength + input.Length);
        input.CopyTo(historyBuffer.AsSpan(historyLength));
        int totalAvailable = historyLength + input.Length;

        ref float histRef = ref MemoryMarshal.GetArrayDataReference(historyBuffer);
        ref float filterRef = ref MemoryMarshal.GetArrayDataReference(filterBank);
        int outputCount = 0;
        int maxOutput = output.Length;

        // Track position as: intPos (integer sample) + phase (fractional, [0, phaseCount))
        int intPos = 0;
        long curPhase = phase;

        while (outputCount < maxOutput)
        {
            // Check if we have enough history for this convolution
            if (intPos + filterLength > totalAvailable)
                break;

            // Phase directly indexes the filter bank — no division needed
            int filterOffset = (int)curPhase * filterLength;

            // Convolve: output = sum(filter[phase][tap] * history[intPos + tap])
            float sample = ConvolveBlock(
                ref Unsafe.Add(ref histRef, intPos),
                ref Unsafe.Add(ref filterRef, filterOffset),
                filterLength);

            output[outputCount++] = sample;

            // Advance: add srcStep integer samples + srcPhaseStep fractional
            intPos += srcStep;
            curPhase += srcPhaseStep;
            if (curPhase >= phaseCount)
            {
                curPhase -= phaseCount;
                intPos++;
            }
        }

        // Remove consumed input from history — keep the tail needed for next call
        if (intPos > 0 && intPos <= totalAvailable)
        {
            int remaining = totalAvailable - intPos;
            if (remaining > 0)
                Array.Copy(historyBuffer, intPos, historyBuffer, 0, remaining);
            historyLength = remaining;
        }
        else
        {
            historyLength = totalAvailable;
        }
        phase = curPhase;

        return outputCount;
    }

    /// <summary>
    /// Flush remaining samples from the filter delay line.
    /// Call once at end-of-stream. Feeds zeros to push out the tail.
    /// </summary>
    /// <param name="output">Output buffer for flushed samples.</param>
    /// <returns>Number of output samples written.</returns>
    public int Flush(Span<float> output)
    {
        // Feed filterLength zeros to push out remaining samples
        float[] zeros = ArrayPool<float>.Shared.Rent(filterLength);
        Array.Clear(zeros, 0, filterLength);
        int flushed = Resample(zeros.AsSpan(0, filterLength), output);
        ArrayPool<float>.Shared.Return(zeros);
        return flushed;
    }

    /// <summary>Reset internal state for reuse with a new stream.</summary>
    public void Reset()
    {
        historyLength = 0;
        phase = 0;
    }

    /// <summary>
    /// Estimate how many output samples will be produced from a given input count.
    /// </summary>
    public int EstimateOutputSamples(int inputSamples)
    {
        // output ≈ input × dstRate / srcRate + some extra for filter tail
        return (int)((long)(inputSamples + historyLength) * DestinationRate / SourceRate) + 1;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            historyBuffer = Array.Empty<float>();
        }
    }

    // ── Filter bank construction ─────────────────────────────────

    private void BuildFilterBank(int taps, int phases, double cutoff, double kaiserBeta)
    {
        // Build a polyphase filter bank:
        // For each phase p in [0, phases), compute taps coefficients
        // using a Kaiser-windowed sinc function.

        double besselDenom = BesselI0(kaiserBeta);
        int center = taps / 2;

        for (int phase = 0; phase < phases; phase++)
        {
            double phaseOffset = (double)phase / phases;
            double sum = 0;

            for (int tap = 0; tap < taps; tap++)
            {
                // FFmpeg convention: subtract phase offset so increasing phase
                // moves the interpolation point forward in time
                double t = tap - center - phaseOffset;

                // Sinc function with cutoff
                double sincVal;
                if (Math.Abs(t) < 1e-10)
                    sincVal = cutoff;
                else
                    sincVal = Math.Sin(Math.PI * cutoff * t) / (Math.PI * t);

                // Kaiser window
                double windowArg = 2.0 * (tap - center) / (taps - 1); // [-1, 1]
                double windowVal = BesselI0(kaiserBeta * Math.Sqrt(Math.Max(0, 1.0 - windowArg * windowArg))) / besselDenom;

                double coeff = sincVal * windowVal;
                filterBank[phase * taps + tap] = (float)coeff;
                sum += coeff;
            }

            // Normalize so each phase sums to 1.0 (preserves DC gain)
            if (Math.Abs(sum) > 1e-10)
            {
                float normFactor = (float)(1.0 / sum);
                for (int tap = 0; tap < taps; tap++)
                    filterBank[phase * taps + tap] *= normFactor;
            }
        }
    }

    /// <summary>Modified Bessel function of the first kind, order 0.</summary>
    private static double BesselI0(double x)
    {
        // Polynomial approximation (Abramowitz & Stegun 9.8.1 / 9.8.2)
        double ax = Math.Abs(x);
        if (ax < 3.75)
        {
            double t = x / 3.75;
            t *= t;
            return 1.0 + t * (3.5156229 + t * (3.0899424 + t * (1.2067492
                + t * (0.2659732 + t * (0.0360768 + t * 0.0045813)))));
        }
        else
        {
            double t = 3.75 / ax;
            return (Math.Exp(ax) / Math.Sqrt(ax)) * (0.39894228 + t * (0.01328592
                + t * (0.00225319 + t * (-0.00157565 + t * (0.00916281
                + t * (-0.02057706 + t * (0.02635537 + t * (-0.01647633
                + t * 0.00392377))))))));
        }
    }

    // ── Convolution inner loop ───────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ConvolveBlock(ref float input, ref float filter, int length)
    {
        if (Sse.IsSupported && length >= 8)
            return ConvolveSse(ref input, ref filter, length);
        return ConvolveScalar(ref input, ref filter, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float ConvolveSse(ref float input, ref float filter, int length)
    {
        var acc0 = Vector128<float>.Zero;
        var acc1 = Vector128<float>.Zero;
        int i = 0;
        int simdEnd = length & ~7;

        for (; i < simdEnd; i += 8)
        {
            var s0 = Vector128.LoadUnsafe(ref input, (nuint)i);
            var f0 = Vector128.LoadUnsafe(ref filter, (nuint)i);
            var s1 = Vector128.LoadUnsafe(ref input, (nuint)(i + 4));
            var f1 = Vector128.LoadUnsafe(ref filter, (nuint)(i + 4));
            acc0 = Sse.Add(acc0, Sse.Multiply(s0, f0));
            acc1 = Sse.Add(acc1, Sse.Multiply(s1, f1));
        }

        acc0 = Sse.Add(acc0, acc1);

        // Horizontal sum of 4 floats
        var hi = Sse.MoveHighToLow(acc0, acc0);
        acc0 = Sse.Add(acc0, hi);
        acc0 = Sse.AddScalar(acc0, Sse.Shuffle(acc0, acc0, 0x01));
        float sum = acc0.ToScalar();

        // Scalar tail
        for (; i < length; i++)
            sum += Unsafe.Add(ref input, i) * Unsafe.Add(ref filter, i);

        return sum;
    }

    private static float ConvolveScalar(ref float input, ref float filter, int length)
    {
        float sum = 0;
        for (int i = 0; i < length; i++)
            sum += Unsafe.Add(ref input, i) * Unsafe.Add(ref filter, i);
        return sum;
    }

    private void EnsureHistoryCapacity(int needed)
    {
        if (needed <= historyBuffer.Length)
            return;

        int newSize = Math.Max(needed, historyBuffer.Length * 2);
        float[] newBuffer = new float[newSize];
        Array.Copy(historyBuffer, 0, newBuffer, 0, historyLength);
        historyBuffer = newBuffer;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int t = b;
            b = a % b;
            a = t;
        }
        return a;
    }
}

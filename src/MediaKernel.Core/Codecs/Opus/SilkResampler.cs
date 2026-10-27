// Licensed under the MIT License.
// Polyphase resampler matching FFmpeg's swresample for Opus SILK upsampling.
// Supports integer-ratio upsampling (8→48, 12→48, 16→48 kHz).
// Faithfully ports FFmpeg's invert_initial_buffer, resample_common,
// and FIFO management for bit-exact output.

using System;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Polyphase FIR resampler for upsampling SILK output to 48kHz.
/// Produces output matching FFmpeg's swresample with default settings:
/// Kaiser window (beta=9), filter_size=32, cutoff=0.97, exact_rational=1.
///
/// For integer upsampling by ratio R:
///   phase_count = R, filter_length = 32, dst_incr_div = 1, dst_incr_mod = 0.
///   Phase advances by 1 per output sample, sample_index advances when phase wraps.
///
/// Initial state: index = -R * center (negative), triggers mirror inversion
/// on first data to match FFmpeg's zero-phase filter initialization.
/// </summary>
internal class SilkResampler
{
    // FFmpeg's Opus decoder uses filter_size=16 (set in dec.c:734).
    // This differs from swresample's default of 32.
    private const int FilterSize = 16;
    private const double CutoffDefault = 0.97;
    private const double KaiserBeta = 9.0;

    private int ratio;
    private int filterLength;
    private float[][] filterBank = null!;

    // FIFO input buffer — accumulates input samples across calls.
    // After initial inversion, contains [mirrored_prefix | original_data].
    // Between frames, retains filterLength-1 unconsumed tail samples.
    private float[] fifo = Array.Empty<float>();
    private int fifoStart;  // index of first valid sample
    private int fifoCount;  // number of valid samples

    // Phase tracking (matches FFmpeg's ResampleContext.index).
    // Starts negative (-ratio * center), becomes 0..ratio-1 after inversion.
    private int index;

    private bool needsInversion;
    private int inSampleRate;
    private bool initialized;

    /// <summary>
    /// Initialize the resampler for a given input sample rate → 48kHz.
    /// </summary>
    public void Init(int inputSampleRate)
    {
        if (inputSampleRate == inSampleRate && initialized)
            return;

        inSampleRate = inputSampleRate;

        int gcd = Gcd(48000, inputSampleRate);
        ratio = 48000 / gcd;
        int srcFactor = inputSampleRate / gcd;
        if (srcFactor != 1)
            throw new ArgumentException($"Non-integer upsampling ratio: 48000/{inputSampleRate}");

        double factor = Math.Min(48000.0 * CutoffDefault / inputSampleRate, 1.0);

        filterLength = Math.Max((int)Math.Ceiling(FilterSize / factor), 1);
        if (filterLength > 1 && filterLength % 2 != 0)
            filterLength++;

        BuildFilter(factor);

        int center = (filterLength - 1) / 2;
        index = -ratio * center;
        needsInversion = true;

        // Pre-allocate FIFO for mirror prefix + typical frame + headroom
        int initialSize = 2 * filterLength + 1 + 1024;
        fifo = new float[initialSize];
        fifoStart = 0;
        fifoCount = 0;
        initialized = true;
    }

    /// <summary>
    /// Feed silence samples to prime the resampler (matches FFmpeg's initial delay).
    /// Places silence at position filterLength in the FIFO, preparing for
    /// mirror inversion when the first real data arrives.
    /// </summary>
    public void FeedSilence(int silenceSamples)
    {
        // FFmpeg's invert_initial_buffer expects data at positions
        // [filterLength .. filterLength + count - 1] in the in_buffer.
        // Silence = zeros, which are already there after EnsureCapacity.
        EnsureCapacity(filterLength + silenceSamples);
        Array.Clear(fifo, 0, filterLength + silenceSamples);
        fifoStart = filterLength;
        fifoCount = silenceSamples;
    }

    /// <summary>
    /// Resample input samples to output at 48kHz.
    /// Returns actual number of output samples produced (may be less than maxOutput
    /// for the first frame due to filter startup delay).
    /// </summary>
    public int Resample(ReadOnlySpan<float> input, int inputCount, Span<float> output, int maxOutput)
    {
        if (needsInversion)
            return HandleInversion(input, inputCount, output, maxOutput);

        AppendToFifo(input, inputCount);
        return RunConvolution(output, maxOutput);
    }

    /// <summary>
    /// Flush remaining delayed samples from the resampler.
    /// Called at end-of-stream or mode switch.
    /// </summary>
    public int Flush(Span<float> output, int maxOutput)
    {
        if (maxOutput <= 0)
            return 0;

        // Mirror reflection of trailing samples, matching FFmpeg's resample_flush.
        // FFmpeg reflects the last (min(count, filterLength)+1)/2 samples after
        // the end of the buffer. This produces better filter ring-down than
        // zero-padding, especially at mode transitions.
        int reflection = (Math.Min(fifoCount, filterLength) + 1) / 2;
        EnsureCapacity(fifoStart + fifoCount + reflection);
        int bufEnd = fifoStart + fifoCount;
        for (int j = 0; j < reflection; j++)
            fifo[bufEnd + j] = fifo[bufEnd - j - 1];
        fifoCount += reflection;

        return RunConvolution(output, maxOutput);
    }

    /// <summary>
    /// Reset the resampler state.
    /// </summary>
    public void Reset()
    {
        if (fifo.Length > 0)
            Array.Clear(fifo);
        fifoStart = 0;
        fifoCount = 0;
        inSampleRate = 0;
        initialized = false;
        needsInversion = true;
    }

    /// <summary>
    /// Handles the initial mirror inversion (ports FFmpeg's invert_initial_buffer).
    /// On first data arrival, creates a time-reversed mirror of the signal at the
    /// boundary between silence and data. This provides zero-phase filter initialization.
    /// </summary>
    private int HandleInversion(ReadOnlySpan<float> input, int inputCount, Span<float> output, int maxOutput)
    {
        // Append input after existing silence/data in the FIFO.
        // Data is stored starting at position filterLength (set by FeedSilence).
        int existingEnd = fifoStart + fifoCount;
        EnsureCapacity(existingEnd + inputCount);
        input.Slice(0, inputCount).CopyTo(fifo.AsSpan(existingEnd, inputCount));
        int totalAvailable = fifoCount + inputCount;

        if (totalAvailable < filterLength + 1)
        {
            // Not enough data for inversion yet — accumulate and wait
            fifoCount = totalAvailable;
            return 0;
        }

        // --- Mirror inversion (matching FFmpeg's invert_initial_buffer) ---
        // Buffer layout before inversion:
        //   [0..filterLength-1]: unused (will be filled by mirror)
        //   [filterLength..filterLength+totalAvailable-1]: silence + input data
        //
        // After mirror: position[filterLength - n] = position[filterLength + n]
        // This creates a time-reversed reflection around the silence/data boundary.
        for (int n = 1; n <= filterLength; n++)
            fifo[filterLength - n] = fifo[filterLength + n];

        // Adjust index from negative to non-negative by advancing outIdx
        int outIdx = filterLength;
        while (index < 0)
        {
            outIdx--;
            index += ratio;
        }

        // Update FIFO: valid data starts at outIdx
        fifoStart = outIdx;
        fifoCount = filterLength + totalAvailable - outIdx;
        needsInversion = false;

        return RunConvolution(output, maxOutput);
    }

    /// <summary>
    /// Core convolution loop — faithful port of FFmpeg's resample_common.
    /// For integer-ratio upsampling: phase advances by 1 per output,
    /// sample_index advances by 1 when phase wraps around ratio.
    /// </summary>
    private int RunConvolution(Span<float> output, int maxOutput)
    {
        // Compute maximum possible output samples given available input.
        // Matches FFmpeg: end_index = (1 + src_size - filter_length) * phase_count
        long endIndex = (1L + fifoCount - filterLength) * ratio;
        long possible = endIndex - index;
        int outputCount = (int)Math.Max(Math.Min(maxOutput, possible), 0);

        if (outputCount <= 0)
            return 0;

        int sampleIndex = 0;
        int localIndex = index;
        while (localIndex >= ratio)
        {
            sampleIndex++;
            localIndex -= ratio;
        }

        // Convolution with 2x unroll matching FFmpeg's resample_common
        for (int i = 0; i < outputCount; i++)
        {
            float[] filter = filterBank[localIndex];
            float val = 0;
            float val2 = 0;
            int srcOff = fifoStart + sampleIndex;

            int j;
            for (j = 0; j + 1 < filterLength; j += 2)
            {
                val += fifo[srcOff + j] * filter[j];
                val2 += fifo[srcOff + j + 1] * filter[j + 1];
            }
            if (j < filterLength)
                val += fifo[srcOff + j] * filter[j];

            output[i] = val + val2;

            if (++localIndex >= ratio)
            {
                localIndex -= ratio;
                sampleIndex++;
            }
        }

        // Update persistent state
        index = localIndex;
        fifoStart += sampleIndex;
        fifoCount -= sampleIndex;

        // Compact FIFO: shift remaining data to start of array
        if (fifoCount > 0 && fifoStart > 0)
        {
            Array.Copy(fifo, fifoStart, fifo, 0, fifoCount);
            fifoStart = 0;
        }
        else if (fifoCount == 0)
        {
            fifoStart = 0;
        }

        return outputCount;
    }

    private void AppendToFifo(ReadOnlySpan<float> data, int count)
    {
        int end = fifoStart + fifoCount;
        EnsureCapacity(end + count);
        data.Slice(0, count).CopyTo(fifo.AsSpan(end, count));
        fifoCount += count;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed > fifo.Length)
        {
            int newSize = Math.Max(fifo.Length * 2, needed + 512);
            float[] newFifo = new float[newSize];
            if (fifoCount > 0)
                Array.Copy(fifo, 0, newFifo, 0, fifoStart + fifoCount);
            fifo = newFifo;
        }
    }

    /// <summary>
    /// Build polyphase filter bank matching FFmpeg's build_filter.
    /// Kaiser window, sinc interpolation, normalized to unit DC gain.
    /// </summary>
    private void BuildFilter(double factor)
    {
        filterBank = new float[ratio][];
        int center = (filterLength - 1) / 2;
        double[] tab = new double[filterLength];

        int phNb = (ratio % 2 != 0) ? ratio : ratio / 2 + 1;
        double[] sinLut = new double[phNb];
        int signMul = ((center & 1) != 0) ? 1 : -1;
        for (int ph = 0; ph < phNb; ph++)
            sinLut[ph] = Math.Sin(Math.PI * ph / ratio) * signMul;

        double norm = 0;

        for (int ph = 0; ph < phNb; ph++)
        {
            double s = sinLut[ph];

            for (int i = 0; i < filterLength; i++)
            {
                double x = Math.PI * ((double)(i - center) - (double)ph / ratio) * factor;
                double y;

                if (x == 0)
                    y = 1.0;
                else if (factor == 1.0)
                    y = s / x;
                else
                    y = Math.Sin(x) / x;

                double w = 2.0 * x / (factor * filterLength * Math.PI);
                y *= BesselI0(KaiserBeta * Math.Sqrt(Math.Max(1 - w * w, 0)));

                tab[i] = y;
                s = -s;

                if (ph == 0)
                    norm += y;
            }

            filterBank[ph] = new float[filterLength];
            for (int i = 0; i < filterLength; i++)
                filterBank[ph][i] = (float)(tab[i] / norm);

            // Mirror: if phase_count is even, phase (phase_count - ph) = reversed phase ph
            // Matches FFmpeg: filter[(phase_count-ph) * alloc + tap_count-1-i] = filter[ph * alloc + i]
            // Skip when mirrorPh == ph (center phase is already symmetric)
            if (ratio % 2 == 0 && ph > 0)
            {
                int mirrorPh = ratio - ph;
                if (mirrorPh != ph)
                {
                    filterBank[mirrorPh] = new float[filterLength];
                    for (int i = 0; i < filterLength; i++)
                        filterBank[mirrorPh][i] = filterBank[ph][filterLength - 1 - i];
                }
            }
        }
    }

    /// <summary>
    /// Modified Bessel function of the first kind, order 0.
    /// Matches FFmpeg's av_bessel_i0.
    /// </summary>
    private static double BesselI0(double x)
    {
        double v = 1;
        double lastV = 0;
        double t = 1;

        x = x * x / 4;
        for (int i = 1; v != lastV; i++)
        {
            lastV = v;
            t *= x / ((double)i * i);
            v += t;
        }
        return v;
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

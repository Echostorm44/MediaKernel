// MediaKernel - Audio Visualization
// Spectrum analyzer, waveform display, VU meter, and oscilloscope

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Type of audio visualization.
/// </summary>
public enum VisualizationType
{
    /// <summary>Spectrum analyzer bars.</summary>
    Spectrum,
    /// <summary>Waveform display.</summary>
    Waveform,
    /// <summary>VU meter.</summary>
    VuMeter,
    /// <summary>Oscilloscope.</summary>
    Oscilloscope,
    /// <summary>Spectrogram (time vs frequency heatmap).</summary>
    Spectrogram
}

/// <summary>
/// Spectrum analyzer data for a single frame.
/// </summary>
public readonly struct SpectrumData
{
    /// <summary>Magnitude values for each frequency band (0.0 to 1.0).</summary>
    public float[] Bands { get; init; }
    
    /// <summary>Peak values for each band (for peak hold display).</summary>
    public float[] Peaks { get; init; }
    
    /// <summary>Number of bands.</summary>
    public int BandCount => Bands?.Length ?? 0;
    
    /// <summary>Left channel RMS level (0.0 to 1.0).</summary>
    public float LeftLevel { get; init; }
    
    /// <summary>Right channel RMS level (0.0 to 1.0).</summary>
    public float RightLevel { get; init; }
    
    /// <summary>Left channel peak (0.0 to 1.0).</summary>
    public float LeftPeak { get; init; }
    
    /// <summary>Right channel peak (0.0 to 1.0).</summary>
    public float RightPeak { get; init; }
}

/// <summary>
/// Waveform data for display.
/// </summary>
public readonly struct WaveformData
{
    /// <summary>Left channel samples (normalized -1.0 to 1.0).</summary>
    public float[] LeftChannel { get; init; }
    
    /// <summary>Right channel samples (normalized -1.0 to 1.0).</summary>
    public float[] RightChannel { get; init; }
    
    /// <summary>Number of samples.</summary>
    public int SampleCount => LeftChannel?.Length ?? 0;
}

/// <summary>
/// Fast Fourier Transform for audio spectrum analysis.
/// Uses Cooley-Tukey radix-2 algorithm.
/// </summary>
public sealed class FastFourierTransform
{
    private readonly int fftSize;
    private readonly int log2Size;
    private readonly float[] cosTable;
    private readonly float[] sinTable;
    private readonly int[] bitReversalTable;
    
    /// <summary>
    /// Creates an FFT processor.
    /// </summary>
    /// <param name="size">FFT size (must be power of 2).</param>
    public FastFourierTransform(int size = 1024)
    {
        if ((size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of 2", nameof(size));
        
        fftSize = size;
        log2Size = (int)Math.Log2(size);
        
        // Precompute twiddle factors
        cosTable = new float[size / 2];
        sinTable = new float[size / 2];
        
        for (int i = 0; i < size / 2; i++)
        {
            double angle = -2 * Math.PI * i / size;
            cosTable[i] = (float)Math.Cos(angle);
            sinTable[i] = (float)Math.Sin(angle);
        }
        
        // Precompute bit reversal table
        bitReversalTable = new int[size];
        for (int i = 0; i < size; i++)
        {
            bitReversalTable[i] = ReverseBits(i, log2Size);
        }
    }
    
    /// <summary>FFT size.</summary>
    public int Size => fftSize;
    
    /// <summary>
    /// Compute forward FFT in-place.
    /// </summary>
    /// <param name="real">Real part (modified in place).</param>
    /// <param name="imaginary">Imaginary part (modified in place).</param>
    public void Forward(Span<float> real, Span<float> imaginary)
    {
        if (real.Length < fftSize || imaginary.Length < fftSize)
            throw new ArgumentException($"Buffers must be at least {fftSize} elements");
        
        // Bit reversal permutation
        for (int i = 0; i < fftSize; i++)
        {
            int j = bitReversalTable[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        
        // Cooley-Tukey butterfly
        for (int stage = 1; stage <= log2Size; stage++)
        {
            int m = 1 << stage;
            int halfM = m / 2;
            int tableStep = fftSize / m;
            
            for (int k = 0; k < fftSize; k += m)
            {
                for (int j = 0; j < halfM; j++)
                {
                    int tableIdx = j * tableStep;
                    float cos = cosTable[tableIdx];
                    float sin = sinTable[tableIdx];
                    
                    int evenIdx = k + j;
                    int oddIdx = k + j + halfM;
                    
                    float tReal = cos * real[oddIdx] - sin * imaginary[oddIdx];
                    float tImag = sin * real[oddIdx] + cos * imaginary[oddIdx];
                    
                    real[oddIdx] = real[evenIdx] - tReal;
                    imaginary[oddIdx] = imaginary[evenIdx] - tImag;
                    real[evenIdx] = real[evenIdx] + tReal;
                    imaginary[evenIdx] = imaginary[evenIdx] + tImag;
                }
            }
        }
    }
    
    /// <summary>
    /// Compute magnitude spectrum from FFT result.
    /// </summary>
    /// <param name="real">Real part from FFT.</param>
    /// <param name="imaginary">Imaginary part from FFT.</param>
    /// <param name="magnitude">Output magnitude spectrum (half of FFT size).</param>
    public void GetMagnitude(ReadOnlySpan<float> real, ReadOnlySpan<float> imaginary, Span<float> magnitude)
    {
        int halfSize = fftSize / 2;
        if (magnitude.Length < halfSize)
            throw new ArgumentException($"Magnitude buffer must be at least {halfSize} elements");
        
        for (int i = 0; i < halfSize; i++)
        {
            float r = real[i];
            float im = imaginary[i];
            magnitude[i] = MathF.Sqrt(r * r + im * im);
        }
    }
    
    private static int ReverseBits(int value, int numBits)
    {
        int result = 0;
        for (int i = 0; i < numBits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}

/// <summary>
/// Window functions for FFT preprocessing.
/// </summary>
public static class WindowFunctions
{
    /// <summary>Apply Hann (Hanning) window.</summary>
    public static void ApplyHann(Span<float> samples)
    {
        int n = samples.Length;
        float factor = 2 * MathF.PI / (n - 1);
        
        for (int i = 0; i < n; i++)
        {
            float window = 0.5f * (1 - MathF.Cos(factor * i));
            samples[i] *= window;
        }
    }
    
    /// <summary>Apply Hamming window.</summary>
    public static void ApplyHamming(Span<float> samples)
    {
        int n = samples.Length;
        float factor = 2 * MathF.PI / (n - 1);
        
        for (int i = 0; i < n; i++)
        {
            float window = 0.54f - 0.46f * MathF.Cos(factor * i);
            samples[i] *= window;
        }
    }
    
    /// <summary>Apply Blackman window.</summary>
    public static void ApplyBlackman(Span<float> samples)
    {
        int n = samples.Length;
        float factor = 2 * MathF.PI / (n - 1);
        
        for (int i = 0; i < n; i++)
        {
            float window = 0.42f - 0.5f * MathF.Cos(factor * i) + 0.08f * MathF.Cos(2 * factor * i);
            samples[i] *= window;
        }
    }
    
    /// <summary>Apply flat-top window (best for amplitude accuracy).</summary>
    public static void ApplyFlatTop(Span<float> samples)
    {
        int n = samples.Length;
        float factor = 2 * MathF.PI / (n - 1);
        
        for (int i = 0; i < n; i++)
        {
            float window = 0.21557895f 
                - 0.41663158f * MathF.Cos(factor * i)
                + 0.277263158f * MathF.Cos(2 * factor * i)
                - 0.083578947f * MathF.Cos(3 * factor * i)
                + 0.006947368f * MathF.Cos(4 * factor * i);
            samples[i] *= window;
        }
    }
}

/// <summary>
/// Audio spectrum analyzer for visualization.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private readonly FastFourierTransform fft;
    private readonly int fftSize;
    private readonly int bandCount;
    private readonly float[] realBuffer;
    private readonly float[] imagBuffer;
    private readonly float[] magnitudeBuffer;
    private readonly float[] bands;
    private readonly float[] peaks;
    private readonly float[] previousBands;
    private readonly int[] bandBoundaries;
    
    private float peakDecayRate = 0.95f;
    private float bandSmoothingFactor = 0.3f;
    private float minDecibels = -60f;
    private float maxDecibels = 0f;
    
    /// <summary>
    /// Creates a spectrum analyzer.
    /// </summary>
    /// <param name="fftSize">FFT size (power of 2, default 2048).</param>
    /// <param name="bandCount">Number of frequency bands (default 32).</param>
    public SpectrumAnalyzer(int fftSize = 2048, int bandCount = 32)
    {
        this.fftSize = fftSize;
        this.bandCount = bandCount;
        
        fft = new FastFourierTransform(fftSize);
        realBuffer = new float[fftSize];
        imagBuffer = new float[fftSize];
        magnitudeBuffer = new float[fftSize / 2];
        bands = new float[bandCount];
        peaks = new float[bandCount];
        previousBands = new float[bandCount];
        
        // Calculate logarithmic band boundaries
        bandBoundaries = CalculateBandBoundaries(bandCount, fftSize / 2);
    }
    
    /// <summary>Number of frequency bands.</summary>
    public int BandCount => bandCount;
    
    /// <summary>
    /// Peak decay rate per frame (0.0 to 1.0). Higher = slower decay.
    /// </summary>
    public float PeakDecayRate
    {
        get => peakDecayRate;
        set => peakDecayRate = Math.Clamp(value, 0f, 0.999f);
    }
    
    /// <summary>
    /// Smoothing factor for band values (0.0 to 1.0). Higher = more smoothing.
    /// </summary>
    public float BandSmoothingFactor
    {
        get => bandSmoothingFactor;
        set => bandSmoothingFactor = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Minimum decibel level (default -60 dB).
    /// </summary>
    public float MinDecibels
    {
        get => minDecibels;
        set => minDecibels = Math.Clamp(value, -100f, 0f);
    }
    
    /// <summary>
    /// Maximum decibel level (default 0 dB).
    /// </summary>
    public float MaxDecibels
    {
        get => maxDecibels;
        set => maxDecibels = Math.Clamp(value, minDecibels + 1, 20f);
    }
    
    /// <summary>
    /// Analyze audio samples and compute spectrum data.
    /// </summary>
    /// <param name="samples">Audio samples (mono or interleaved stereo).</param>
    /// <param name="isStereo">Whether samples are interleaved stereo.</param>
    /// <returns>Spectrum analysis result.</returns>
    public SpectrumData Analyze(ReadOnlySpan<float> samples, bool isStereo = false)
    {
        float leftRms = 0f, rightRms = 0f;
        float leftPeak = 0f, rightPeak = 0f;
        
        // Fill FFT buffer and calculate levels
        int sampleCount = Math.Min(samples.Length, fftSize * (isStereo ? 2 : 1));
        
        if (isStereo)
        {
            int monoCount = sampleCount / 2;
            for (int i = 0; i < monoCount && i < fftSize; i++)
            {
                float left = samples[i * 2];
                float right = samples[i * 2 + 1];
                
                // Mix to mono for FFT
                realBuffer[i] = (left + right) * 0.5f;
                imagBuffer[i] = 0f;
                
                // Calculate levels
                leftRms += left * left;
                rightRms += right * right;
                leftPeak = Math.Max(leftPeak, Math.Abs(left));
                rightPeak = Math.Max(rightPeak, Math.Abs(right));
            }
            
            // Zero-pad if needed
            for (int i = monoCount; i < fftSize; i++)
            {
                realBuffer[i] = 0f;
                imagBuffer[i] = 0f;
            }
            
            if (monoCount > 0)
            {
                leftRms = MathF.Sqrt(leftRms / monoCount);
                rightRms = MathF.Sqrt(rightRms / monoCount);
            }
        }
        else
        {
            for (int i = 0; i < sampleCount && i < fftSize; i++)
            {
                realBuffer[i] = samples[i];
                imagBuffer[i] = 0f;
                
                float absVal = Math.Abs(samples[i]);
                leftRms += samples[i] * samples[i];
                leftPeak = Math.Max(leftPeak, absVal);
            }
            
            for (int i = sampleCount; i < fftSize; i++)
            {
                realBuffer[i] = 0f;
                imagBuffer[i] = 0f;
            }
            
            if (sampleCount > 0)
            {
                leftRms = MathF.Sqrt(leftRms / sampleCount);
            }
            
            rightRms = leftRms;
            rightPeak = leftPeak;
        }
        
        // Apply window function
        WindowFunctions.ApplyHann(realBuffer);
        
        // Compute FFT
        fft.Forward(realBuffer, imagBuffer);
        
        // Get magnitude spectrum
        fft.GetMagnitude(realBuffer, imagBuffer, magnitudeBuffer);
        
        // Map to frequency bands
        ComputeBands();
        
        // Update peaks
        UpdatePeaks();
        
        // Return result
        return new SpectrumData
        {
            Bands = (float[])bands.Clone(),
            Peaks = (float[])peaks.Clone(),
            LeftLevel = leftRms,
            RightLevel = rightRms,
            LeftPeak = leftPeak,
            RightPeak = rightPeak
        };
    }
    
    private void ComputeBands()
    {
        float decibelRange = maxDecibels - minDecibels;
        
        for (int band = 0; band < bandCount; band++)
        {
            int startBin = bandBoundaries[band];
            int endBin = bandBoundaries[band + 1];
            
            // Find max magnitude in this band
            float maxMag = 0f;
            for (int bin = startBin; bin < endBin; bin++)
            {
                maxMag = Math.Max(maxMag, magnitudeBuffer[bin]);
            }
            
            // Convert to decibels and normalize
            float db = maxMag > 0 ? 20f * MathF.Log10(maxMag / fftSize) : minDecibels;
            float normalized = (db - minDecibels) / decibelRange;
            normalized = Math.Clamp(normalized, 0f, 1f);
            
            // Apply smoothing
            float smoothed = previousBands[band] * bandSmoothingFactor + normalized * (1 - bandSmoothingFactor);
            bands[band] = smoothed;
            previousBands[band] = smoothed;
        }
    }
    
    private void UpdatePeaks()
    {
        for (int band = 0; band < bandCount; band++)
        {
            if (bands[band] >= peaks[band])
            {
                peaks[band] = bands[band];
            }
            else
            {
                peaks[band] *= peakDecayRate;
            }
        }
    }
    
    private static int[] CalculateBandBoundaries(int bandCount, int binCount)
    {
        // Logarithmic frequency distribution
        int[] boundaries = new int[bandCount + 1];
        boundaries[0] = 1; // Skip DC component
        
        float logMin = MathF.Log10(1);
        float logMax = MathF.Log10(binCount);
        float logRange = logMax - logMin;
        
        for (int i = 1; i <= bandCount; i++)
        {
            float logFreq = logMin + (logRange * i / bandCount);
            int bin = (int)MathF.Pow(10, logFreq);
            boundaries[i] = Math.Min(bin, binCount);
        }
        
        return boundaries;
    }
    
    /// <summary>
    /// Reset analyzer state (clear peaks and smoothing).
    /// </summary>
    public void Reset()
    {
        Array.Clear(bands);
        Array.Clear(peaks);
        Array.Clear(previousBands);
    }
}

/// <summary>
/// VU Meter for audio level monitoring.
/// </summary>
public sealed class VuMeter
{
    private float leftLevel;
    private float rightLevel;
    private float leftPeak;
    private float rightPeak;
    private float attackTime = 0.01f;  // 10ms attack
    private float releaseTime = 0.3f;  // 300ms release
    private float peakHoldTime = 1.0f; // 1 second peak hold
    private float leftPeakAge;
    private float rightPeakAge;
    private int sampleRate = 44100;
    
    /// <summary>
    /// Attack time in seconds.
    /// </summary>
    public float AttackTime
    {
        get => attackTime;
        set => attackTime = Math.Clamp(value, 0.001f, 0.1f);
    }
    
    /// <summary>
    /// Release time in seconds.
    /// </summary>
    public float ReleaseTime
    {
        get => releaseTime;
        set => releaseTime = Math.Clamp(value, 0.05f, 2f);
    }
    
    /// <summary>
    /// Peak hold time in seconds.
    /// </summary>
    public float PeakHoldTime
    {
        get => peakHoldTime;
        set => peakHoldTime = Math.Clamp(value, 0f, 5f);
    }
    
    /// <summary>
    /// Sample rate for timing calculations.
    /// </summary>
    public int SampleRate
    {
        get => sampleRate;
        set => sampleRate = Math.Max(8000, value);
    }
    
    /// <summary>Current left channel level (0 to 1).</summary>
    public float LeftLevel => leftLevel;
    
    /// <summary>Current right channel level (0 to 1).</summary>
    public float RightLevel => rightLevel;
    
    /// <summary>Left channel peak (0 to 1).</summary>
    public float LeftPeak => leftPeak;
    
    /// <summary>Right channel peak (0 to 1).</summary>
    public float RightPeak => rightPeak;
    
    /// <summary>Left level in decibels.</summary>
    public float LeftLevelDb => leftLevel > 0 ? 20f * MathF.Log10(leftLevel) : -100f;
    
    /// <summary>Right level in decibels.</summary>
    public float RightLevelDb => rightLevel > 0 ? 20f * MathF.Log10(rightLevel) : -100f;
    
    /// <summary>
    /// Process stereo audio samples.
    /// </summary>
    /// <param name="samples">Interleaved stereo samples.</param>
    public void Process(ReadOnlySpan<float> samples)
    {
        if (samples.Length < 2)
            return;
        
        int samplePairs = samples.Length / 2;
        float blockDuration = (float)samplePairs / sampleRate;
        
        // Calculate attack/release coefficients
        float attackCoeff = 1f - MathF.Exp(-blockDuration / attackTime);
        float releaseCoeff = 1f - MathF.Exp(-blockDuration / releaseTime);
        
        // Find peak levels in this block
        float leftBlockPeak = 0f, rightBlockPeak = 0f;
        
        for (int i = 0; i < samplePairs; i++)
        {
            float left = Math.Abs(samples[i * 2]);
            float right = Math.Abs(samples[i * 2 + 1]);
            
            leftBlockPeak = Math.Max(leftBlockPeak, left);
            rightBlockPeak = Math.Max(rightBlockPeak, right);
        }
        
        // Update levels with attack/release
        if (leftBlockPeak > leftLevel)
            leftLevel = leftLevel + attackCoeff * (leftBlockPeak - leftLevel);
        else
            leftLevel = leftLevel - releaseCoeff * (leftLevel - leftBlockPeak);
        
        if (rightBlockPeak > rightLevel)
            rightLevel = rightLevel + attackCoeff * (rightBlockPeak - rightLevel);
        else
            rightLevel = rightLevel - releaseCoeff * (rightLevel - rightBlockPeak);
        
        // Update peaks with hold
        leftPeakAge += blockDuration;
        rightPeakAge += blockDuration;
        
        if (leftBlockPeak >= leftPeak || leftPeakAge > peakHoldTime)
        {
            leftPeak = leftBlockPeak;
            leftPeakAge = 0;
        }
        
        if (rightBlockPeak >= rightPeak || rightPeakAge > peakHoldTime)
        {
            rightPeak = rightBlockPeak;
            rightPeakAge = 0;
        }
    }
    
    /// <summary>
    /// Process mono audio samples.
    /// </summary>
    public void ProcessMono(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return;
        
        float blockDuration = (float)samples.Length / sampleRate;
        float attackCoeff = 1f - MathF.Exp(-blockDuration / attackTime);
        float releaseCoeff = 1f - MathF.Exp(-blockDuration / releaseTime);
        
        float blockPeak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            blockPeak = Math.Max(blockPeak, Math.Abs(samples[i]));
        }
        
        if (blockPeak > leftLevel)
            leftLevel = leftLevel + attackCoeff * (blockPeak - leftLevel);
        else
            leftLevel = leftLevel - releaseCoeff * (leftLevel - blockPeak);
        
        rightLevel = leftLevel;
        
        leftPeakAge += blockDuration;
        if (blockPeak >= leftPeak || leftPeakAge > peakHoldTime)
        {
            leftPeak = blockPeak;
            leftPeakAge = 0;
        }
        rightPeak = leftPeak;
        rightPeakAge = leftPeakAge;
    }
    
    /// <summary>
    /// Reset meter state.
    /// </summary>
    public void Reset()
    {
        leftLevel = 0;
        rightLevel = 0;
        leftPeak = 0;
        rightPeak = 0;
        leftPeakAge = 0;
        rightPeakAge = 0;
    }
}

/// <summary>
/// Waveform generator for audio visualization.
/// </summary>
public sealed class WaveformGenerator
{
    private readonly int displaySamples;
    private readonly float[] leftBuffer;
    private readonly float[] rightBuffer;
    
    /// <summary>
    /// Creates a waveform generator.
    /// </summary>
    /// <param name="displaySamples">Number of samples to display.</param>
    public WaveformGenerator(int displaySamples = 512)
    {
        this.displaySamples = displaySamples;
        leftBuffer = new float[displaySamples];
        rightBuffer = new float[displaySamples];
    }
    
    /// <summary>Number of display samples.</summary>
    public int DisplaySamples => displaySamples;
    
    /// <summary>
    /// Generate waveform data from stereo audio.
    /// </summary>
    public WaveformData Generate(ReadOnlySpan<float> samples, bool isStereo = true)
    {
        if (isStereo)
        {
            int samplePairs = samples.Length / 2;
            float ratio = (float)samplePairs / displaySamples;
            
            for (int i = 0; i < displaySamples; i++)
            {
                int srcStart = (int)(i * ratio);
                int srcEnd = (int)((i + 1) * ratio);
                srcEnd = Math.Min(srcEnd, samplePairs);
                
                // Find min/max in range (for envelope display)
                float leftMin = float.MaxValue, leftMax = float.MinValue;
                float rightMin = float.MaxValue, rightMax = float.MinValue;
                
                for (int j = srcStart; j < srcEnd; j++)
                {
                    float left = samples[j * 2];
                    float right = samples[j * 2 + 1];
                    
                    leftMin = Math.Min(leftMin, left);
                    leftMax = Math.Max(leftMax, left);
                    rightMin = Math.Min(rightMin, right);
                    rightMax = Math.Max(rightMax, right);
                }
                
                // Use average of min/max for display (or use both for filled display)
                leftBuffer[i] = (leftMin + leftMax) * 0.5f;
                rightBuffer[i] = (rightMin + rightMax) * 0.5f;
            }
        }
        else
        {
            float ratio = (float)samples.Length / displaySamples;
            
            for (int i = 0; i < displaySamples; i++)
            {
                int srcStart = (int)(i * ratio);
                int srcEnd = (int)((i + 1) * ratio);
                srcEnd = Math.Min(srcEnd, samples.Length);
                
                float min = float.MaxValue, max = float.MinValue;
                for (int j = srcStart; j < srcEnd; j++)
                {
                    min = Math.Min(min, samples[j]);
                    max = Math.Max(max, samples[j]);
                }
                
                leftBuffer[i] = (min + max) * 0.5f;
                rightBuffer[i] = leftBuffer[i];
            }
        }
        
        return new WaveformData
        {
            LeftChannel = (float[])leftBuffer.Clone(),
            RightChannel = (float[])rightBuffer.Clone()
        };
    }
    
    /// <summary>
    /// Generate waveform with min/max envelope for filled display.
    /// </summary>
    public (float[] minLeft, float[] maxLeft, float[] minRight, float[] maxRight) GenerateEnvelope(
        ReadOnlySpan<float> samples, bool isStereo = true)
    {
        float[] minLeft = new float[displaySamples];
        float[] maxLeft = new float[displaySamples];
        float[] minRight = new float[displaySamples];
        float[] maxRight = new float[displaySamples];
        
        if (isStereo)
        {
            int samplePairs = samples.Length / 2;
            float ratio = (float)samplePairs / displaySamples;
            
            for (int i = 0; i < displaySamples; i++)
            {
                int srcStart = (int)(i * ratio);
                int srcEnd = (int)((i + 1) * ratio);
                srcEnd = Math.Min(srcEnd, samplePairs);
                
                minLeft[i] = float.MaxValue;
                maxLeft[i] = float.MinValue;
                minRight[i] = float.MaxValue;
                maxRight[i] = float.MinValue;
                
                for (int j = srcStart; j < srcEnd; j++)
                {
                    float left = samples[j * 2];
                    float right = samples[j * 2 + 1];
                    
                    minLeft[i] = Math.Min(minLeft[i], left);
                    maxLeft[i] = Math.Max(maxLeft[i], left);
                    minRight[i] = Math.Min(minRight[i], right);
                    maxRight[i] = Math.Max(maxRight[i], right);
                }
                
                if (minLeft[i] == float.MaxValue) minLeft[i] = 0;
                if (maxLeft[i] == float.MinValue) maxLeft[i] = 0;
                if (minRight[i] == float.MaxValue) minRight[i] = 0;
                if (maxRight[i] == float.MinValue) maxRight[i] = 0;
            }
        }
        else
        {
            float ratio = (float)samples.Length / displaySamples;
            
            for (int i = 0; i < displaySamples; i++)
            {
                int srcStart = (int)(i * ratio);
                int srcEnd = (int)((i + 1) * ratio);
                srcEnd = Math.Min(srcEnd, samples.Length);
                
                minLeft[i] = float.MaxValue;
                maxLeft[i] = float.MinValue;
                
                for (int j = srcStart; j < srcEnd; j++)
                {
                    minLeft[i] = Math.Min(minLeft[i], samples[j]);
                    maxLeft[i] = Math.Max(maxLeft[i], samples[j]);
                }
                
                if (minLeft[i] == float.MaxValue) minLeft[i] = 0;
                if (maxLeft[i] == float.MinValue) maxLeft[i] = 0;
                
                minRight[i] = minLeft[i];
                maxRight[i] = maxLeft[i];
            }
        }
        
        return (minLeft, maxLeft, minRight, maxRight);
    }
}

/// <summary>
/// Spectrogram generator for time-frequency visualization.
/// </summary>
public sealed class SpectrogramGenerator
{
    private readonly SpectrumAnalyzer analyzer;
    private readonly int historyLength;
    private readonly float[,] spectrumHistory;
    private int historyPosition;
    
    /// <summary>
    /// Creates a spectrogram generator.
    /// </summary>
    /// <param name="fftSize">FFT size.</param>
    /// <param name="bandCount">Number of frequency bands.</param>
    /// <param name="historyLength">Number of time slices to keep.</param>
    public SpectrogramGenerator(int fftSize = 1024, int bandCount = 64, int historyLength = 256)
    {
        analyzer = new SpectrumAnalyzer(fftSize, bandCount);
        this.historyLength = historyLength;
        spectrumHistory = new float[historyLength, bandCount];
        historyPosition = 0;
    }
    
    /// <summary>Number of frequency bands.</summary>
    public int BandCount => analyzer.BandCount;
    
    /// <summary>Number of time slices.</summary>
    public int HistoryLength => historyLength;
    
    /// <summary>
    /// Add audio samples to the spectrogram.
    /// </summary>
    public void AddSamples(ReadOnlySpan<float> samples, bool isStereo = false)
    {
        var spectrum = analyzer.Analyze(samples, isStereo);
        
        // Store in history
        for (int i = 0; i < spectrum.BandCount; i++)
        {
            spectrumHistory[historyPosition, i] = spectrum.Bands[i];
        }
        
        historyPosition = (historyPosition + 1) % historyLength;
    }
    
    /// <summary>
    /// Get the spectrogram data as a 2D array [time, frequency].
    /// Time index 0 is the oldest, historyLength-1 is newest.
    /// </summary>
    public float[,] GetSpectrogram()
    {
        float[,] result = new float[historyLength, analyzer.BandCount];
        
        for (int t = 0; t < historyLength; t++)
        {
            int srcIdx = (historyPosition + t) % historyLength;
            for (int f = 0; f < analyzer.BandCount; f++)
            {
                result[t, f] = spectrumHistory[srcIdx, f];
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Reset the spectrogram history.
    /// </summary>
    public void Reset()
    {
        Array.Clear(spectrumHistory);
        historyPosition = 0;
        analyzer.Reset();
    }
}

/// <summary>
/// Beat/onset detection result.
/// </summary>
public readonly struct BeatInfo
{
    /// <summary>Whether a beat was detected.</summary>
    public bool IsBeat { get; init; }
    
    /// <summary>Confidence level (0.0 to 1.0).</summary>
    public float Confidence { get; init; }
    
    /// <summary>Estimated BPM (beats per minute), or 0 if unknown.</summary>
    public float EstimatedBpm { get; init; }
    
    /// <summary>Time since last beat in seconds.</summary>
    public float TimeSinceLastBeat { get; init; }
    
    /// <summary>Current energy level (0.0 to 1.0).</summary>
    public float CurrentEnergy { get; init; }
    
    /// <summary>Average energy level (0.0 to 1.0).</summary>
    public float AverageEnergy { get; init; }
}

/// <summary>
/// Simple beat/onset detector using energy-based detection.
/// </summary>
/// <remarks>
/// Detects beats by comparing instantaneous energy to a moving average.
/// Works well for music with clear rhythmic content.
/// </remarks>
public sealed class BeatDetector
{
    private readonly int sampleRate;
    private readonly int windowSize;
    private readonly float[] energyHistory;
    private readonly float[] beatTimes;
    private int historyPosition;
    private int beatTimePosition;
    private float energySum;
    private float lastBeatTime;
    private float currentTime;
    private float threshold;
    private float sensitivity;
    
    /// <summary>Detection threshold multiplier (higher = fewer detections).</summary>
    public float Threshold
    {
        get => threshold;
        set => threshold = Math.Clamp(value, 1.0f, 3.0f);
    }
    
    /// <summary>Detection sensitivity (0.0 to 1.0).</summary>
    public float Sensitivity
    {
        get => sensitivity;
        set => sensitivity = Math.Clamp(value, 0.0f, 1.0f);
    }
    
    /// <summary>Last detected beat time in seconds.</summary>
    public float LastBeatTime => lastBeatTime;
    
    /// <summary>Current estimated BPM.</summary>
    public float EstimatedBpm { get; private set; }
    
    /// <summary>Number of beats detected since last reset.</summary>
    public int BeatCount { get; private set; }
    
    public BeatDetector(int sampleRate, int historyWindowMs = 500)
    {
        this.sampleRate = sampleRate;
        
        // Calculate how many energy samples we need to track
        // We process in ~10ms chunks
        int chunksPerSecond = 100;
        int historySize = (historyWindowMs * chunksPerSecond) / 1000;
        windowSize = historySize;
        
        energyHistory = new float[historySize];
        beatTimes = new float[32]; // Keep last 32 beat times for BPM calculation
        
        threshold = 1.5f;
        sensitivity = 0.7f;
    }
    
    /// <summary>
    /// Processes audio samples and detects beats.
    /// </summary>
    /// <param name="samples">Audio samples (mono or interleaved stereo).</param>
    /// <param name="isStereo">Whether samples are stereo.</param>
    /// <returns>Beat detection result.</returns>
    public BeatInfo Process(ReadOnlySpan<float> samples, bool isStereo = true)
    {
        // Calculate instantaneous energy
        float energy = CalculateEnergy(samples, isStereo);
        
        // Update time
        int sampleCount = isStereo ? samples.Length / 2 : samples.Length;
        float blockDuration = (float)sampleCount / sampleRate;
        currentTime += blockDuration;
        
        // Get average energy
        float avgEnergy = energySum / windowSize;
        
        // Update history
        energySum -= energyHistory[historyPosition];
        energyHistory[historyPosition] = energy;
        energySum += energy;
        historyPosition = (historyPosition + 1) % windowSize;
        
        // Detect beat: energy exceeds average by threshold
        float adjustedThreshold = threshold * (1.0f - sensitivity * 0.3f);
        float beatThreshold = avgEnergy * adjustedThreshold;
        
        // Also require minimum time since last beat (100ms = 600 BPM max)
        float minBeatInterval = 0.1f;
        float timeSinceLastBeat = currentTime - lastBeatTime;
        
        bool isBeat = energy > beatThreshold && 
                      energy > 0.001f && 
                      timeSinceLastBeat > minBeatInterval;
        
        float confidence = 0;
        if (isBeat)
        {
            BeatCount++;
            lastBeatTime = currentTime;
            
            // Store beat time for BPM calculation
            beatTimes[beatTimePosition] = currentTime;
            beatTimePosition = (beatTimePosition + 1) % beatTimes.Length;
            
            // Calculate confidence based on how much energy exceeded threshold
            confidence = Math.Min(1.0f, (energy - beatThreshold) / beatThreshold);
            
            // Update BPM estimate
            UpdateBpmEstimate();
        }
        
        return new BeatInfo
        {
            IsBeat = isBeat,
            Confidence = confidence,
            EstimatedBpm = EstimatedBpm,
            TimeSinceLastBeat = timeSinceLastBeat,
            CurrentEnergy = energy,
            AverageEnergy = avgEnergy
        };
    }
    
    /// <summary>
    /// Resets detector state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(energyHistory);
        Array.Clear(beatTimes);
        historyPosition = 0;
        beatTimePosition = 0;
        energySum = 0;
        lastBeatTime = 0;
        currentTime = 0;
        BeatCount = 0;
        EstimatedBpm = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateEnergy(ReadOnlySpan<float> samples, bool isStereo)
    {
        float sum = 0;
        
        if (isStereo)
        {
            for (int i = 0; i < samples.Length; i += 2)
            {
                float left = samples[i];
                float right = samples[i + 1];
                float mono = (left + right) * 0.5f;
                sum += mono * mono;
            }
            return sum / (samples.Length / 2);
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return sum / samples.Length;
        }
    }
    
    private void UpdateBpmEstimate()
    {
        if (BeatCount < 4)
            return;
        
        // Calculate average interval between recent beats
        float totalInterval = 0;
        int intervalCount = 0;
        
        for (int i = 0; i < Math.Min(BeatCount - 1, beatTimes.Length - 1); i++)
        {
            int idx1 = (beatTimePosition - i - 1 + beatTimes.Length) % beatTimes.Length;
            int idx2 = (beatTimePosition - i - 2 + beatTimes.Length) % beatTimes.Length;
            
            float interval = beatTimes[idx1] - beatTimes[idx2];
            
            // Only count reasonable intervals (50-200 BPM range)
            if (interval > 0.3f && interval < 1.2f)
            {
                totalInterval += interval;
                intervalCount++;
            }
        }
        
        if (intervalCount > 0)
        {
            float avgInterval = totalInterval / intervalCount;
            EstimatedBpm = 60.0f / avgInterval;
            
            // Clamp to reasonable range
            EstimatedBpm = Math.Clamp(EstimatedBpm, 40, 220);
        }
    }
}

/// <summary>
/// Sub-band beat detector that analyzes multiple frequency bands.
/// More accurate than simple energy-based detection.
/// </summary>
public sealed class SubBandBeatDetector
{
    private readonly int sampleRate;
    private readonly SpectrumAnalyzer analyzer;
    private readonly float[][] bandHistory;
    private readonly float[] bandEnergySums;
    private readonly int historySize;
    private int historyPosition;
    private float lastBeatTime;
    private float currentTime;
    
    /// <summary>Number of frequency bands analyzed.</summary>
    public int BandCount { get; }
    
    /// <summary>Detection threshold multiplier.</summary>
    public float Threshold { get; set; } = 1.4f;
    
    /// <summary>Minimum number of bands that must trigger for a beat.</summary>
    public int MinTriggeredBands { get; set; } = 2;
    
    public SubBandBeatDetector(int sampleRate, int bandCount = 8, int fftSize = 1024)
    {
        this.sampleRate = sampleRate;
        BandCount = bandCount;
        analyzer = new SpectrumAnalyzer(fftSize, bandCount);
        
        // Track ~500ms of history per band
        historySize = 50;
        bandHistory = new float[bandCount][];
        bandEnergySums = new float[bandCount];
        
        for (int i = 0; i < bandCount; i++)
        {
            bandHistory[i] = new float[historySize];
        }
    }
    
    /// <summary>
    /// Processes audio samples and detects beats using sub-band analysis.
    /// </summary>
    public BeatInfo Process(ReadOnlySpan<float> samples, bool isStereo = true)
    {
        // Get spectrum data
        var spectrum = analyzer.Analyze(samples, isStereo);
        
        // Update time
        int sampleCount = isStereo ? samples.Length / 2 : samples.Length;
        float blockDuration = (float)sampleCount / sampleRate;
        currentTime += blockDuration;
        
        int triggeredBands = 0;
        float maxExcess = 0;
        
        // Check each band for beats
        for (int band = 0; band < BandCount; band++)
        {
            float energy = spectrum.Bands[band];
            float avgEnergy = bandEnergySums[band] / historySize;
            
            // Update history
            bandEnergySums[band] -= bandHistory[band][historyPosition];
            bandHistory[band][historyPosition] = energy;
            bandEnergySums[band] += energy;
            
            // Check if this band triggered
            if (energy > avgEnergy * Threshold && avgEnergy > 0.001f)
            {
                triggeredBands++;
                float excess = (energy - avgEnergy * Threshold) / (avgEnergy * Threshold);
                maxExcess = Math.Max(maxExcess, excess);
            }
        }
        
        historyPosition = (historyPosition + 1) % historySize;
        
        // Minimum time between beats
        float minBeatInterval = 0.1f;
        float timeSinceLastBeat = currentTime - lastBeatTime;
        
        bool isBeat = triggeredBands >= MinTriggeredBands && timeSinceLastBeat > minBeatInterval;
        
        if (isBeat)
        {
            lastBeatTime = currentTime;
        }
        
        return new BeatInfo
        {
            IsBeat = isBeat,
            Confidence = isBeat ? Math.Min(1.0f, maxExcess) : 0,
            TimeSinceLastBeat = timeSinceLastBeat,
            CurrentEnergy = spectrum.LeftLevel,
            AverageEnergy = spectrum.LeftLevel // Simplified
        };
    }
    
    /// <summary>
    /// Resets detector state.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
        {
            Array.Clear(bandHistory[i]);
            bandEnergySums[i] = 0;
        }
        historyPosition = 0;
        lastBeatTime = 0;
        currentTime = 0;
        analyzer.Reset();
    }
}

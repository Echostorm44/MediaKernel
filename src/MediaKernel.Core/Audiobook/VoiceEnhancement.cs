// MediaKernel - Voice Enhancement Filters
// Voice boost, silence skipping, volume normalization for podcasts/audiobooks

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audiobook;

/// <summary>
/// Voice boost equalizer preset for speech content.
/// Enhances clarity and presence of human voice frequencies.
/// </summary>
public sealed class VoiceBoostFilter
{
    private readonly int sampleRate;
    private readonly int channels;
    
    // Biquad filter states per channel
    private double[] lowShelfState;  // 2 states per channel
    private double[] presenceState;  // 2 states per channel
    private double[] highShelfState; // 2 states per channel
    
    // Filter coefficients (computed for sample rate)
    private double[] lowShelfB = new double[3];
    private double[] lowShelfA = new double[3];
    private double[] presenceB = new double[3];
    private double[] presenceA = new double[3];
    private double[] highShelfB = new double[3];
    private double[] highShelfA = new double[3];
    
    private float boostAmount = 1.0f; // 0.0 to 2.0
    
    public VoiceBoostFilter(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        
        lowShelfState = new double[channels * 2];
        presenceState = new double[channels * 2];
        highShelfState = new double[channels * 2];
        
        ComputeCoefficients();
    }
    
    /// <summary>
    /// Amount of voice boost (0.0 = none, 1.0 = normal, 2.0 = maximum).
    /// </summary>
    public float BoostAmount
    {
        get => boostAmount;
        set
        {
            boostAmount = Math.Clamp(value, 0.0f, 2.0f);
            ComputeCoefficients();
        }
    }
    
    private void ComputeCoefficients()
    {
        // Voice enhancement EQ:
        // 1. Low shelf cut at 100 Hz (-3dB) - reduce rumble
        // 2. Presence boost at 3 kHz (+4dB) - enhance clarity
        // 3. High shelf at 8 kHz (+2dB) - add air
        
        float gain = boostAmount;
        
        // Low shelf (cut)
        ComputeLowShelf(100.0, -3.0 * gain, lowShelfB, lowShelfA);
        
        // Presence peak
        ComputePeakingEq(3000.0, 4.0 * gain, 1.5, presenceB, presenceA);
        
        // High shelf (boost)
        ComputeHighShelf(8000.0, 2.0 * gain, highShelfB, highShelfA);
    }
    
    private void ComputeLowShelf(double freq, double gainDb, double[] b, double[] a)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double A = Math.Pow(10.0, gainDb / 40.0);
        double alpha = Math.Sin(w0) / 2.0 * Math.Sqrt(2.0);
        
        double cosw0 = Math.Cos(w0);
        double sqrtA = Math.Sqrt(A);
        
        double a0 = (A + 1) + (A - 1) * cosw0 + 2 * sqrtA * alpha;
        
        b[0] = A * ((A + 1) - (A - 1) * cosw0 + 2 * sqrtA * alpha) / a0;
        b[1] = 2 * A * ((A - 1) - (A + 1) * cosw0) / a0;
        b[2] = A * ((A + 1) - (A - 1) * cosw0 - 2 * sqrtA * alpha) / a0;
        a[0] = 1.0;
        a[1] = -2 * ((A - 1) + (A + 1) * cosw0) / a0;
        a[2] = ((A + 1) + (A - 1) * cosw0 - 2 * sqrtA * alpha) / a0;
    }
    
    private void ComputeHighShelf(double freq, double gainDb, double[] b, double[] a)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double A = Math.Pow(10.0, gainDb / 40.0);
        double alpha = Math.Sin(w0) / 2.0 * Math.Sqrt(2.0);
        
        double cosw0 = Math.Cos(w0);
        double sqrtA = Math.Sqrt(A);
        
        double a0 = (A + 1) - (A - 1) * cosw0 + 2 * sqrtA * alpha;
        
        b[0] = A * ((A + 1) + (A - 1) * cosw0 + 2 * sqrtA * alpha) / a0;
        b[1] = -2 * A * ((A - 1) + (A + 1) * cosw0) / a0;
        b[2] = A * ((A + 1) + (A - 1) * cosw0 - 2 * sqrtA * alpha) / a0;
        a[0] = 1.0;
        a[1] = 2 * ((A - 1) - (A + 1) * cosw0) / a0;
        a[2] = ((A + 1) - (A - 1) * cosw0 - 2 * sqrtA * alpha) / a0;
    }
    
    private void ComputePeakingEq(double freq, double gainDb, double q, double[] b, double[] a)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double A = Math.Pow(10.0, gainDb / 40.0);
        double alpha = Math.Sin(w0) / (2.0 * q);
        
        double cosw0 = Math.Cos(w0);
        
        double a0 = 1 + alpha / A;
        
        b[0] = (1 + alpha * A) / a0;
        b[1] = -2 * cosw0 / a0;
        b[2] = (1 - alpha * A) / a0;
        a[0] = 1.0;
        a[1] = -2 * cosw0 / a0;
        a[2] = (1 - alpha / A) / a0;
    }
    
    /// <summary>
    /// Process audio samples with voice boost.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples)
    {
        if (boostAmount < 0.01f) return;
        
        int samplesPerChannel = samples.Length / channels;
        
        for (int s = 0; s < samplesPerChannel; s++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int idx = s * channels + ch;
                int stateBase = ch * 2;
                
                double input = samples[idx];
                
                // Low shelf
                double y1 = lowShelfB[0] * input + lowShelfState[stateBase];
                lowShelfState[stateBase] = lowShelfB[1] * input - lowShelfA[1] * y1 + lowShelfState[stateBase + 1];
                lowShelfState[stateBase + 1] = lowShelfB[2] * input - lowShelfA[2] * y1;
                
                // Presence
                double y2 = presenceB[0] * y1 + presenceState[stateBase];
                presenceState[stateBase] = presenceB[1] * y1 - presenceA[1] * y2 + presenceState[stateBase + 1];
                presenceState[stateBase + 1] = presenceB[2] * y1 - presenceA[2] * y2;
                
                // High shelf
                double y3 = highShelfB[0] * y2 + highShelfState[stateBase];
                highShelfState[stateBase] = highShelfB[1] * y2 - highShelfA[1] * y3 + highShelfState[stateBase + 1];
                highShelfState[stateBase + 1] = highShelfB[2] * y2 - highShelfA[2] * y3;
                
                samples[idx] = (float)y3;
            }
        }
    }
    
    /// <summary>
    /// Reset filter state (call when seeking).
    /// </summary>
    public void Reset()
    {
        Array.Clear(lowShelfState);
        Array.Clear(presenceState);
        Array.Clear(highShelfState);
    }
}

/// <summary>
/// Silence skipper for podcasts and audiobooks.
/// Automatically speeds through or skips silent sections.
/// </summary>
public sealed class SilenceSkipper
{
    private readonly int sampleRate;
    private readonly int channels;
    
    // Detection parameters
    private float silenceThreshold = 0.01f;  // -40 dB
    private int minSilenceDurationMs = 500;  // Minimum silence to detect
    private int leadingSilenceMs = 100;      // Silence to keep at start
    private int trailingSilenceMs = 100;     // Silence to keep at end
    
    // State
    private int consecutiveSilentSamples;
    private int silenceStartSample;
    private bool inSilence;
    private float[]? silenceBuffer;
    private int silenceBufferPos;
    
    public SilenceSkipper(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        
        int bufferSize = (minSilenceDurationMs * sampleRate / 1000) * channels;
        silenceBuffer = new float[bufferSize];
    }
    
    /// <summary>
    /// Silence detection threshold (0.0 to 1.0).
    /// </summary>
    public float SilenceThreshold
    {
        get => silenceThreshold;
        set => silenceThreshold = Math.Clamp(value, 0.0001f, 0.1f);
    }
    
    /// <summary>
    /// Minimum silence duration to skip (milliseconds).
    /// </summary>
    public int MinSilenceDurationMs
    {
        get => minSilenceDurationMs;
        set
        {
            minSilenceDurationMs = Math.Clamp(value, 100, 5000);
            ResizeBuffer();
        }
    }
    
    /// <summary>
    /// Whether silence skipping is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Event raised when silence is detected (for seeking).
    /// </summary>
    public event EventHandler<SilenceDetectedEventArgs>? SilenceDetected;
    
    private void ResizeBuffer()
    {
        int bufferSize = (minSilenceDurationMs * sampleRate / 1000) * channels;
        silenceBuffer = new float[bufferSize];
        silenceBufferPos = 0;
    }
    
    /// <summary>
    /// Process samples and detect/skip silence.
    /// Returns the number of output samples (may be less than input if silence was skipped).
    /// </summary>
    public int Process(Span<float> input, Span<float> output)
    {
        if (!Enabled || silenceBuffer == null)
        {
            input.CopyTo(output);
            return input.Length;
        }
        
        int outputPos = 0;
        int samplesPerFrame = channels;
        int minSilenceSamples = (minSilenceDurationMs * sampleRate / 1000) * channels;
        int leadingSamples = (leadingSilenceMs * sampleRate / 1000) * channels;
        int trailingSamples = (trailingSilenceMs * sampleRate / 1000) * channels;
        
        for (int i = 0; i < input.Length; i += samplesPerFrame)
        {
            // Calculate frame RMS
            float rms = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = input[i + ch];
                rms += sample * sample;
            }
            rms = MathF.Sqrt(rms / channels);
            
            bool isSilent = rms < silenceThreshold;
            
            if (isSilent)
            {
                if (!inSilence)
                {
                    inSilence = true;
                    silenceStartSample = i;
                    silenceBufferPos = 0;
                }
                
                // Buffer silent samples
                if (silenceBufferPos + samplesPerFrame <= silenceBuffer.Length)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        silenceBuffer[silenceBufferPos + ch] = input[i + ch];
                    }
                    silenceBufferPos += samplesPerFrame;
                }
                
                consecutiveSilentSamples += samplesPerFrame;
            }
            else
            {
                if (inSilence)
                {
                    // End of silence detected
                    int silenceDuration = consecutiveSilentSamples;
                    
                    if (silenceDuration >= minSilenceSamples)
                    {
                        // Silence was long enough to skip
                        // Output leading silence from buffer
                        int leadingToOutput = Math.Min(leadingSamples, silenceBufferPos);
                        for (int j = 0; j < leadingToOutput && outputPos < output.Length; j++)
                        {
                            output[outputPos++] = silenceBuffer[j];
                        }
                        
                        // Output trailing silence from buffer
                        int trailingStart = Math.Max(0, silenceBufferPos - trailingSamples);
                        for (int j = trailingStart; j < silenceBufferPos && outputPos < output.Length; j++)
                        {
                            output[outputPos++] = silenceBuffer[j];
                        }
                        
                        // Raise event
                        SilenceDetected?.Invoke(this, new SilenceDetectedEventArgs
                        {
                            StartSample = silenceStartSample,
                            EndSample = i,
                            DurationMs = silenceDuration * 1000 / (sampleRate * channels)
                        });
                    }
                    else
                    {
                        // Silence was too short - output all buffered samples
                        for (int j = 0; j < silenceBufferPos && outputPos < output.Length; j++)
                        {
                            output[outputPos++] = silenceBuffer[j];
                        }
                    }
                    
                    inSilence = false;
                    consecutiveSilentSamples = 0;
                }
                
                // Output non-silent sample
                for (int ch = 0; ch < channels && outputPos < output.Length; ch++)
                {
                    output[outputPos++] = input[i + ch];
                }
            }
        }
        
        return outputPos;
    }
    
    /// <summary>
    /// Analyze samples and return silence regions (for UI display).
    /// </summary>
    public SilenceRegion[] AnalyzeSilence(ReadOnlySpan<float> samples)
    {
        var regions = new System.Collections.Generic.List<SilenceRegion>();
        int minSilenceSamples = (minSilenceDurationMs * sampleRate / 1000) * channels;
        
        int silenceStart = -1;
        int consecutiveSilent = 0;
        
        for (int i = 0; i < samples.Length; i += channels)
        {
            float rms = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = samples[i + ch];
                rms += sample * sample;
            }
            rms = MathF.Sqrt(rms / channels);
            
            if (rms < silenceThreshold)
            {
                if (silenceStart < 0)
                {
                    silenceStart = i;
                }
                consecutiveSilent += channels;
            }
            else
            {
                if (silenceStart >= 0 && consecutiveSilent >= minSilenceSamples)
                {
                    regions.Add(new SilenceRegion
                    {
                        StartSample = silenceStart,
                        EndSample = i,
                        StartTime = TimeSpan.FromSeconds((double)silenceStart / (sampleRate * channels)),
                        EndTime = TimeSpan.FromSeconds((double)i / (sampleRate * channels))
                    });
                }
                silenceStart = -1;
                consecutiveSilent = 0;
            }
        }
        
        return regions.ToArray();
    }
    
    /// <summary>
    /// Reset state (call when seeking).
    /// </summary>
    public void Reset()
    {
        inSilence = false;
        consecutiveSilentSamples = 0;
        silenceBufferPos = 0;
    }
}

/// <summary>
/// Silence detection event arguments.
/// </summary>
public sealed class SilenceDetectedEventArgs : EventArgs
{
    public int StartSample { get; init; }
    public int EndSample { get; init; }
    public int DurationMs { get; init; }
}

/// <summary>
/// Silence region for visualization.
/// </summary>
public readonly struct SilenceRegion
{
    public int StartSample { get; init; }
    public int EndSample { get; init; }
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
}

/// <summary>
/// Per-podcast/audiobook volume normalization.
/// Uses loudness analysis to maintain consistent volume across different content.
/// </summary>
public sealed class ContentNormalizer
{
    private readonly int sampleRate;
    private readonly int channels;
    
    // Target loudness (LUFS)
    private double targetLoudness = -18.0;
    
    // Current gain state
    private float currentGain = 1.0f;
    private float targetGain = 1.0f;
    private float gainSmoothingFactor = 0.001f;
    
    // Loudness measurement
    private double[] momentaryPower;
    private int momentaryIndex;
    private const int MomentaryWindowMs = 400;
    
    // Limiter
    private float limiterThreshold = 0.95f;
    private float limiterRelease = 0.9995f;
    private float limiterGain = 1.0f;
    
    public ContentNormalizer(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        
        int windowSamples = MomentaryWindowMs * sampleRate / 1000;
        momentaryPower = new double[windowSamples];
    }
    
    /// <summary>
    /// Target loudness in LUFS (-23 to -14 typical).
    /// </summary>
    public double TargetLoudness
    {
        get => targetLoudness;
        set => targetLoudness = Math.Clamp(value, -30.0, -10.0);
    }
    
    /// <summary>
    /// Set the measured loudness of the content.
    /// </summary>
    public void SetContentLoudness(double measuredLoudness)
    {
        // Calculate gain to reach target
        double gainDb = targetLoudness - measuredLoudness;
        targetGain = (float)Math.Pow(10.0, gainDb / 20.0);
        
        // Limit gain to reasonable range
        targetGain = Math.Clamp(targetGain, 0.1f, 10.0f);
    }
    
    /// <summary>
    /// Process samples with normalization and limiting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples)
    {
        int samplesPerFrame = channels;
        
        for (int i = 0; i < samples.Length; i += samplesPerFrame)
        {
            // Smooth gain transition
            currentGain += (targetGain - currentGain) * gainSmoothingFactor;
            
            // Apply gain
            float maxSample = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                samples[i + ch] *= currentGain;
                float abs = MathF.Abs(samples[i + ch]);
                if (abs > maxSample) maxSample = abs;
            }
            
            // Limiter
            if (maxSample > limiterThreshold)
            {
                float reduction = limiterThreshold / maxSample;
                if (reduction < limiterGain)
                {
                    limiterGain = reduction;
                }
            }
            else
            {
                limiterGain = MathF.Min(1.0f, limiterGain / limiterRelease);
            }
            
            if (limiterGain < 1.0f)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    samples[i + ch] *= limiterGain;
                }
            }
            
            // Update momentary loudness measurement
            double framePower = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                double sample = samples[i + ch];
                framePower += sample * sample;
            }
            framePower /= channels;
            
            momentaryPower[momentaryIndex] = framePower;
            momentaryIndex = (momentaryIndex + 1) % momentaryPower.Length;
        }
    }
    
    /// <summary>
    /// Get current momentary loudness in LUFS.
    /// </summary>
    public double GetMomentaryLoudness()
    {
        double sum = 0;
        foreach (double p in momentaryPower)
        {
            sum += p;
        }
        
        double meanPower = sum / momentaryPower.Length;
        if (meanPower <= 0) return -70.0;
        
        return -0.691 + 10.0 * Math.Log10(meanPower);
    }
    
    /// <summary>
    /// Reset state (call when seeking or changing content).
    /// </summary>
    public void Reset()
    {
        Array.Clear(momentaryPower);
        momentaryIndex = 0;
        limiterGain = 1.0f;
        currentGain = targetGain;
    }
}

// Additional audio utility filters: Gain control, Volume normalizer, Center isolation, Mid-Side
// Ported from VLC's audio_filter modules

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Simple gain control filter for adjusting audio volume.
/// Applies a linear multiplier to all samples.
/// </summary>
public sealed class GainFilter
{
    private float gain = 1.0f;
    
    /// <summary>
    /// Gain multiplier. 1.0 = unity gain, 0.5 = -6dB, 2.0 = +6dB.
    /// Range: 0.0 to 10.0.
    /// </summary>
    public float Gain
    {
        get => gain;
        set => gain = Math.Clamp(value, 0f, 10f);
    }
    
    /// <summary>
    /// Gain in decibels. 0dB = unity gain.
    /// </summary>
    public float GainDb
    {
        get => 20f * MathF.Log10(Math.Max(gain, 0.0001f));
        set => gain = Math.Clamp(MathF.Pow(10f, value / 20f), 0f, 10f);
    }
    
    /// <summary>
    /// Enable or disable gain processing.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Process audio samples with gain adjustment.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (!Enabled || Math.Abs(gain - 1f) < 0.0001f)
            return;
        
        if (Avx.IsSupported && samples.Length >= 8)
        {
            ProcessAvx(samples);
        }
        else if (Sse.IsSupported && samples.Length >= 4)
        {
            ProcessSse(samples);
        }
        else
        {
            ProcessScalar(samples);
        }
    }
    
    private void ProcessScalar(Span<float> samples)
    {
        float g = gain;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= g;
        }
    }
    
    private void ProcessSse(Span<float> samples)
    {
        var gainVec = Vector128.Create(gain);
        int i = 0;
        
        for (; i <= samples.Length - 4; i += 4)
        {
            var vec = Vector128.Create(samples[i], samples[i + 1], samples[i + 2], samples[i + 3]);
            var result = Sse.Multiply(vec, gainVec);
            samples[i] = result.GetElement(0);
            samples[i + 1] = result.GetElement(1);
            samples[i + 2] = result.GetElement(2);
            samples[i + 3] = result.GetElement(3);
        }
        
        for (; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }
    
    private void ProcessAvx(Span<float> samples)
    {
        var gainVec = Vector256.Create(gain);
        int i = 0;
        
        for (; i <= samples.Length - 8; i += 8)
        {
            var vec = Vector256.Create(
                samples[i], samples[i + 1], samples[i + 2], samples[i + 3],
                samples[i + 4], samples[i + 5], samples[i + 6], samples[i + 7]);
            var result = Avx.Multiply(vec, gainVec);
            
            for (int j = 0; j < 8; j++)
                samples[i + j] = result.GetElement(j);
        }
        
        // Handle remaining with SSE or scalar
        for (; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }
}

/// <summary>
/// Dynamic volume normalizer that reduces loud passages and boosts quiet ones.
/// Uses a sliding window to measure average power and applies adaptive gain.
/// </summary>
public sealed class VolumeNormalizer
{
    private readonly float[] powerHistory;
    private int historyIndex;
    private int historyFilled;
    private readonly int bufferCount;
    private float maxLevel;
    
    /// <summary>
    /// Creates a volume normalizer.
    /// </summary>
    /// <param name="bufferCount">Number of audio buffers to average over. Higher = smoother but slower response.</param>
    /// <param name="maxLevel">Target maximum level. Audio exceeding this will be attenuated.</param>
    public VolumeNormalizer(int bufferCount = 20, float maxLevel = 2.0f)
    {
        this.bufferCount = Math.Max(1, bufferCount);
        this.maxLevel = Math.Max(0.01f, maxLevel);
        this.powerHistory = new float[this.bufferCount];
        Reset();
    }
    
    /// <summary>
    /// Enable or disable normalization.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Target maximum level. Audio with average power above this will be reduced.
    /// </summary>
    public float MaxLevel
    {
        get => maxLevel;
        set => maxLevel = Math.Max(0.01f, value);
    }
    
    /// <summary>
    /// Current gain being applied (read-only).
    /// </summary>
    public float CurrentGain { get; private set; } = 1.0f;
    
    /// <summary>
    /// Reset the normalizer state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(powerHistory);
        historyIndex = 0;
        historyFilled = 0;
        CurrentGain = 1.0f;
    }
    
    /// <summary>
    /// Process interleaved audio samples.
    /// </summary>
    /// <param name="samples">Audio samples to normalize in-place.</param>
    /// <param name="channelCount">Number of audio channels.</param>
    public void Process(Span<float> samples, int channelCount = 2)
    {
        if (!Enabled || samples.Length == 0 || channelCount < 1)
            return;
        
        int sampleCount = samples.Length / channelCount;
        if (sampleCount == 0)
            return;
        
        // Calculate RMS power for this buffer
        float sumSquared = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquared += samples[i] * samples[i];
        }
        
        float rms = MathF.Sqrt(sumSquared / samples.Length);
        
        // Store in history
        powerHistory[historyIndex] = rms;
        historyIndex = (historyIndex + 1) % bufferCount;
        if (historyFilled < bufferCount)
            historyFilled++;
        
        // Calculate average power over history
        float avgPower = 0f;
        for (int i = 0; i < historyFilled; i++)
        {
            avgPower += powerHistory[i];
        }
        avgPower /= historyFilled;
        
        // Calculate gain to apply
        float targetGain = 1.0f;
        if (avgPower > maxLevel)
        {
            targetGain = maxLevel / avgPower;
        }
        
        // Smooth gain transition to avoid clicks
        CurrentGain = CurrentGain * 0.9f + targetGain * 0.1f;
        
        // Apply gain if not unity
        if (Math.Abs(CurrentGain - 1f) > 0.001f)
        {
            ApplyGain(samples, CurrentGain);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGain(Span<float> samples, float gain)
    {
        if (Avx.IsSupported && samples.Length >= 8)
        {
            var gainVec = Vector256.Create(gain);
            int i = 0;
            
            for (; i <= samples.Length - 8; i += 8)
            {
                var vec = Vector256.Create(
                    samples[i], samples[i + 1], samples[i + 2], samples[i + 3],
                    samples[i + 4], samples[i + 5], samples[i + 6], samples[i + 7]);
                var result = Avx.Multiply(vec, gainVec);
                
                for (int j = 0; j < 8; j++)
                    samples[i + j] = result.GetElement(j);
            }
            
            for (; i < samples.Length; i++)
                samples[i] *= gain;
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= gain;
        }
    }
}

/// <summary>
/// Audio center isolation filter - opposite of karaoke.
/// Extracts center-panned content (vocals) while removing sides.
/// </summary>
public sealed class CenterIsolationFilter
{
    private const float NormalizationFactor = 0.70710678f;
    
    /// <summary>
    /// Enable or disable the center isolation effect.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Effect strength from 0 (no effect) to 1 (full side removal).
    /// </summary>
    public float Strength { get; set; } = 1.0f;
    
    /// <summary>
    /// Processes interleaved stereo samples to isolate center content.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (!Enabled || samples.Length < 2)
            return;
        
        float strength = Math.Clamp(Strength, 0f, 1f);
        if (strength < 0.001f)
            return;
        
        float factor = NormalizationFactor * strength;
        float dryMix = 1f - strength;
        
        int pairs = samples.Length / 2;
        
        for (int i = 0; i < pairs; i++)
        {
            int idx = i * 2;
            float left = samples[idx];
            float right = samples[idx + 1];
            
            // Add to get center content
            float center = (left + right) * factor;
            
            // Mix with dry signal
            samples[idx] = left * dryMix + center;
            samples[idx + 1] = right * dryMix + center;
        }
    }
}

/// <summary>
/// Mid-Side processor for stereo manipulation.
/// Allows independent control of mid (center) and side (stereo) content.
/// </summary>
public sealed class MidSideProcessor
{
    private const float Sqrt2 = 1.41421356f;
    private const float InvSqrt2 = 0.70710678f;
    
    /// <summary>
    /// Mid (center) level. 1.0 = unity.
    /// </summary>
    public float MidLevel { get; set; } = 1.0f;
    
    /// <summary>
    /// Side (stereo) level. 1.0 = unity, 0 = mono, >1 = wider stereo.
    /// </summary>
    public float SideLevel { get; set; } = 1.0f;
    
    /// <summary>
    /// Enable or disable processing.
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Processes interleaved stereo samples with mid-side adjustment.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (!Enabled || samples.Length < 2)
            return;
        
        // Skip if no change
        if (Math.Abs(MidLevel - 1f) < 0.001f && Math.Abs(SideLevel - 1f) < 0.001f)
            return;
        
        float midGain = MidLevel * InvSqrt2;
        float sideGain = SideLevel * InvSqrt2;
        
        int pairs = samples.Length / 2;
        
        for (int i = 0; i < pairs; i++)
        {
            int idx = i * 2;
            float left = samples[idx];
            float right = samples[idx + 1];
            
            // Convert to mid-side
            float mid = (left + right) * midGain;
            float side = (left - right) * sideGain;
            
            // Convert back to left-right
            samples[idx] = mid + side;
            samples[idx + 1] = mid - side;
        }
    }
}

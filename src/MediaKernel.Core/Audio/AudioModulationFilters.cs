// MediaKernel - Noise Gate Audio Filter
// Based on VLC's modules/audio_filter/gate.c

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Noise gate audio filter that mutes signals below a threshold.
/// Reference: VLC modules/audio_filter/gate.c (Anders Johansson approach)
/// </summary>
public sealed class NoiseGate
{
    private readonly int channelCount;
    private readonly int sampleRate;
    private readonly float[] powerEstimate;
    private readonly float[] currentGain;
    
    private float thresholdDb;
    private float thresholdLinearSquared;
    private float attackTimeMs;
    private float releaseTimeMs;
    private float smoothingTimeMs;
    private float alpha;
    private float attackFactor;
    private float releaseFactor;
    
    /// <summary>Threshold level in dB (negative values). Default: -30 dB.</summary>
    public float ThresholdDb
    {
        get => thresholdDb;
        set
        {
            thresholdDb = Math.Clamp(value, -60f, 0f);
            float linear = DbToLinear(thresholdDb);
            thresholdLinearSquared = linear * linear;
        }
    }
    
    /// <summary>Attack time in milliseconds (how fast gate opens). Default: 50 ms.</summary>
    public float AttackTimeMs
    {
        get => attackTimeMs;
        set
        {
            attackTimeMs = Math.Clamp(value, 10f, 500f);
            float attackSamples = sampleRate * attackTimeMs / 1000f;
            attackFactor = attackSamples > 0 ? 1f / attackSamples : 1f;
        }
    }
    
    /// <summary>Release time in milliseconds (how fast gate closes). Default: 300 ms.</summary>
    public float ReleaseTimeMs
    {
        get => releaseTimeMs;
        set
        {
            releaseTimeMs = Math.Clamp(value, 50f, 1000f);
            float releaseSamples = sampleRate * releaseTimeMs / 1000f;
            releaseFactor = releaseSamples > 0 ? 1f / releaseSamples : 1f;
        }
    }
    
    /// <summary>Power estimation smoothing time in milliseconds. Default: 10 ms.</summary>
    public float SmoothingTimeMs
    {
        get => smoothingTimeMs;
        set
        {
            smoothingTimeMs = Math.Clamp(value, 1f, 30f);
            alpha = CalculateAlpha(sampleRate, smoothingTimeMs);
        }
    }
    
    /// <summary>
    /// Creates a noise gate filter.
    /// </summary>
    /// <param name="channelCount">Number of audio channels.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    public NoiseGate(int channelCount, int sampleRate)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        
        this.channelCount = channelCount;
        this.sampleRate = sampleRate;
        powerEstimate = new float[channelCount];
        currentGain = new float[channelCount];
        
        // Initialize with full gain
        for (int i = 0; i < channelCount; i++)
            currentGain[i] = 1f;
        
        // Set defaults
        ThresholdDb = -30f;
        AttackTimeMs = 50f;
        ReleaseTimeMs = 300f;
        SmoothingTimeMs = 10f;
    }
    
    /// <summary>
    /// Process audio samples in-place with noise gate.
    /// </summary>
    /// <param name="samples">Interleaved audio samples.</param>
    public void Process(Span<float> samples)
    {
        int sampleCount = samples.Length / channelCount;
        
        for (int ch = 0; ch < channelCount; ch++)
        {
            for (int i = ch; i < samples.Length; i += channelCount)
            {
                float sample = samples[i];
                float power = sample * sample;
                
                // Update power estimate with exponential smoothing
                powerEstimate[ch] = (1f - alpha) * powerEstimate[ch] + power * alpha;
                
                // Determine target gain
                float targetGain = powerEstimate[ch] < thresholdLinearSquared ? 0f : 1f;
                
                // Calculate gain delta
                float gainDelta = targetGain - currentGain[ch];
                
                // Apply smoothing - attack or release depending on direction
                float gainChangeFactor = gainDelta > 0f ? attackFactor : releaseFactor;
                
                // Smoothly adjust gain
                currentGain[ch] += gainChangeFactor * gainDelta;
                
                // Apply gain
                samples[i] = sample * currentGain[ch];
            }
        }
    }
    
    /// <summary>
    /// Reset the gate state.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < channelCount; i++)
        {
            powerEstimate[i] = 0f;
            currentGain[i] = 1f;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
    
    private static float CalculateAlpha(int sampleRate, float smoothingTimeMs)
    {
        // Reference: https://en.wikipedia.org/wiki/Exponential_smoothing#Time_constant
        float timeConstantSec = smoothingTimeMs / 1000f;
        float deltaT = 1f / sampleRate;
        return 1f - MathF.Exp(-deltaT / timeConstantSec);
    }
}

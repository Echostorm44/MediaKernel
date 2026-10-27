// MediaKernel - Audio Reverb (Freeverb algorithm)
// Based on Jezar's Freeverb which is public domain
// Simulates room acoustics with comb and allpass filters

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Comb filter for reverb processing.
/// Delay line with lowpass feedback.
/// </summary>
internal sealed class CombFilter
{
    private readonly float[] buffer;
    private int bufferIndex;
    private float filterStore;
    private float damp1;
    private float damp2;
    private float feedback;
    
    public CombFilter(int bufferSize)
    {
        buffer = new float[bufferSize];
        bufferIndex = 0;
        filterStore = 0f;
        feedback = 0.5f;
        SetDamp(0.5f);
    }
    
    public void SetDamp(float value)
    {
        damp1 = value;
        damp2 = 1f - value;
    }
    
    public void SetFeedback(float value)
    {
        feedback = value;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input)
    {
        float output = buffer[bufferIndex];
        
        // Prevent denormals
        if (MathF.Abs(output) < 1e-20f)
            output = 0f;
        
        // Lowpass filter (matches VLC's simplified version)
        filterStore = output * damp2;
        
        // Prevent denormals in filter store
        if (MathF.Abs(filterStore) < 1e-20f)
            filterStore = 0f;
        
        buffer[bufferIndex] = input + filterStore * feedback;
        
        if (++bufferIndex >= buffer.Length)
            bufferIndex = 0;
        
        return output;
    }
    
    public void Mute()
    {
        Array.Clear(buffer);
        filterStore = 0f;
    }
}

/// <summary>
/// Allpass filter for reverb diffusion.
/// </summary>
internal sealed class AllpassFilter
{
    private readonly float[] buffer;
    private int bufferIndex;
    private float feedback;
    
    public AllpassFilter(int bufferSize)
    {
        buffer = new float[bufferSize];
        bufferIndex = 0;
        feedback = 0.5f;
    }
    
    public void SetFeedback(float value)
    {
        feedback = value;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float input)
    {
        float bufferOut = buffer[bufferIndex];
        
        // Prevent denormals
        if (MathF.Abs(bufferOut) < 1e-20f)
            bufferOut = 0f;
        
        float output = -input + bufferOut;
        buffer[bufferIndex] = input + bufferOut * feedback;
        
        if (++bufferIndex >= buffer.Length)
            bufferIndex = 0;
        
        return output;
    }
    
    public void Mute()
    {
        Array.Clear(buffer);
    }
}

/// <summary>
/// Freeverb stereo reverb effect.
/// Simulates room acoustics using parallel comb filters and series allpass filters.
/// </summary>
public sealed class AudioReverb
{
    // Freeverb tuning constants (based on 44.1kHz, work well for 48kHz too)
    private const int NumCombs = 8;
    private const int NumAllpasses = 4;
    private const int StereoSpread = 23;
    
    // Comb filter tunings (samples at 44.1kHz)
    private static readonly int[] CombTuningsLeft = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] CombTuningsRight = { 1139, 1211, 1300, 1379, 1445, 1514, 1580, 1640 };
    
    // Allpass filter tunings
    private static readonly int[] AllpassTuningsLeft = { 556, 441, 341, 225 };
    private static readonly int[] AllpassTuningsRight = { 579, 464, 364, 248 };
    
    // Default constants
    private const float FixedGain = 0.005f;  // Matches VLC's fixedgain
    private const float ScaleWet = 3f;
    private const float ScaleDry = 2f;
    private const float ScaleDamp = 0.4f;
    private const float ScaleRoom = 0.28f;
    private const float OffsetRoom = 0.7f;
    
    // Filter banks
    private readonly CombFilter[] combsLeft = new CombFilter[NumCombs];
    private readonly CombFilter[] combsRight = new CombFilter[NumCombs];
    private readonly AllpassFilter[] allpassesLeft = new AllpassFilter[NumAllpasses];
    private readonly AllpassFilter[] allpassesRight = new AllpassFilter[NumAllpasses];
    
    // Parameters
    private float roomSize;
    private float damping;
    private float wet;
    private float wet1;
    private float wet2;
    private float dry;
    private float width;
    private int sampleRate;
    
    /// <summary>
    /// Creates a new reverb effect with default settings.
    /// </summary>
    public AudioReverb(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        float sampleRateRatio = sampleRate / 44100f;
        
        // Initialize comb filters with scaled buffer sizes
        for (int i = 0; i < NumCombs; i++)
        {
            int sizeLeft = (int)(CombTuningsLeft[i] * sampleRateRatio);
            int sizeRight = (int)(CombTuningsRight[i] * sampleRateRatio);
            combsLeft[i] = new CombFilter(sizeLeft);
            combsRight[i] = new CombFilter(sizeRight);
        }
        
        // Initialize allpass filters
        for (int i = 0; i < NumAllpasses; i++)
        {
            int sizeLeft = (int)(AllpassTuningsLeft[i] * sampleRateRatio);
            int sizeRight = (int)(AllpassTuningsRight[i] * sampleRateRatio);
            allpassesLeft[i] = new AllpassFilter(sizeLeft);
            allpassesRight[i] = new AllpassFilter(sizeRight);
            
            // Allpass feedback is fixed at 0.5
            allpassesLeft[i].SetFeedback(0.5f);
            allpassesRight[i].SetFeedback(0.5f);
        }
        
        // Set default values
        SetWet(0.33f);
        SetDry(0.4f);
        SetRoomSize(0.5f);
        SetDamping(0.5f);
        SetWidth(1f);
    }
    
    /// <summary>
    /// Room size from 0.0 to 1.0. Larger values = longer reverb tail.
    /// </summary>
    public float RoomSize
    {
        get => roomSize;
        set => SetRoomSize(Math.Clamp(value, 0f, 1f));
    }
    
    /// <summary>
    /// Damping from 0.0 to 1.0. Higher values = more high-frequency absorption.
    /// </summary>
    public float Damping
    {
        get => damping;
        set => SetDamping(Math.Clamp(value, 0f, 1f));
    }
    
    /// <summary>
    /// Wet mix from 0.0 to 1.0. Amount of reverb signal.
    /// </summary>
    public float Wet
    {
        get => wet;
        set => SetWet(Math.Clamp(value, 0f, 1f));
    }
    
    /// <summary>
    /// Dry mix from 0.0 to 1.0. Amount of original signal.
    /// </summary>
    public float Dry
    {
        get => dry;
        set => SetDry(Math.Clamp(value, 0f, 1f));
    }
    
    /// <summary>
    /// Stereo width from 0.0 to 1.0. 0 = mono, 1 = full stereo.
    /// </summary>
    public float Width
    {
        get => width;
        set => SetWidth(Math.Clamp(value, 0f, 1f));
    }
    
    /// <summary>
    /// Sample rate for the effect.
    /// </summary>
    public int SampleRate
    {
        get => sampleRate;
        set
        {
            if (value != sampleRate)
            {
                sampleRate = value;
                // Reinitialize with new sample rate
                Mute();
            }
        }
    }
    
    private void SetRoomSize(float value)
    {
        roomSize = value;
        float feedback = value * ScaleRoom + OffsetRoom;
        
        for (int i = 0; i < NumCombs; i++)
        {
            combsLeft[i].SetFeedback(feedback);
            combsRight[i].SetFeedback(feedback);
        }
    }
    
    private void SetDamping(float value)
    {
        damping = value;
        float dampValue = value * ScaleDamp;
        
        for (int i = 0; i < NumCombs; i++)
        {
            combsLeft[i].SetDamp(dampValue);
            combsRight[i].SetDamp(dampValue);
        }
    }
    
    private void SetWet(float value)
    {
        wet = value;
        float scaledWet = value * ScaleWet;
        wet1 = scaledWet * (width / 2f + 0.5f);
        wet2 = scaledWet * (1f - width) / 2f;
    }
    
    private void SetDry(float value)
    {
        dry = value;
    }
    
    // Computed dry level for processing
    private float DryLevel => dry * ScaleDry;
    
    private void SetWidth(float value)
    {
        width = value;
        // Recalculate wet gains
        float scaledWet = wet * ScaleWet;
        wet1 = scaledWet * (width / 2f + 0.5f);
        wet2 = scaledWet * (1f - width) / 2f;
    }
    
    /// <summary>
    /// Process stereo audio samples in-place.
    /// </summary>
    /// <param name="samples">Interleaved stereo samples (L0, R0, L1, R1, ...)</param>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2)
            return;
        
        int samplePairs = samples.Length / 2;
        
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            float inputLeft = samples[offset];
            float inputRight = samples[offset + 1];
            
            // Mix input to mono for reverb processing
            float input = (inputLeft + inputRight) * FixedGain;
            
            // Process through comb filters (in parallel)
            float outLeft = 0f;
            float outRight = 0f;
            
            for (int c = 0; c < NumCombs; c++)
            {
                outLeft += combsLeft[c].Process(input);
                outRight += combsRight[c].Process(input);
            }
            
            // Process through allpass filters (in series)
            for (int a = 0; a < NumAllpasses; a++)
            {
                outLeft = allpassesLeft[a].Process(outLeft);
                outRight = allpassesRight[a].Process(outRight);
            }
            
            // Mix with stereo width
            float mixLeft = outLeft * wet1 + outRight * wet2;
            float mixRight = outRight * wet1 + outLeft * wet2;
            
            // Output = dry + wet
            samples[offset] = inputLeft * DryLevel + mixLeft;
            samples[offset + 1] = inputRight * DryLevel + mixRight;
        }
    }
    
    /// <summary>
    /// Process mono audio samples in-place (applies reverb and outputs stereo-like mono).
    /// </summary>
    /// <param name="samples">Mono audio samples</param>
    public void ProcessMono(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i] * FixedGain;
            
            float outLeft = 0f;
            float outRight = 0f;
            
            for (int c = 0; c < NumCombs; c++)
            {
                outLeft += combsLeft[c].Process(input);
                outRight += combsRight[c].Process(input);
            }
            
            for (int a = 0; a < NumAllpasses; a++)
            {
                outLeft = allpassesLeft[a].Process(outLeft);
                outRight = allpassesRight[a].Process(outRight);
            }
            
            // Sum L+R for mono output
            float wetOut = (outLeft + outRight) * wet * 0.5f;
            samples[i] = samples[i] * DryLevel + wetOut;
        }
    }
    
    /// <summary>
    /// Clear all reverb delay lines (silence the reverb tail).
    /// </summary>
    public void Mute()
    {
        for (int i = 0; i < NumCombs; i++)
        {
            combsLeft[i].Mute();
            combsRight[i].Mute();
        }
        
        for (int i = 0; i < NumAllpasses; i++)
        {
            allpassesLeft[i].Mute();
            allpassesRight[i].Mute();
        }
    }
    
    /// <summary>
    /// Reset all parameters to defaults.
    /// </summary>
    public void Reset()
    {
        SetWet(0.33f);
        SetDry(0.4f);
        SetRoomSize(0.5f);
        SetDamping(0.5f);
        SetWidth(1f);
        Mute();
    }
    
    /// <summary>
    /// Apply a preset.
    /// </summary>
    public void ApplyPreset(ReverbPreset preset)
    {
        switch (preset)
        {
            case ReverbPreset.SmallRoom:
                SetRoomSize(0.3f);
                SetDamping(0.7f);
                SetWet(0.25f);
                SetDry(0.75f);
                SetWidth(0.8f);
                break;
                
            case ReverbPreset.MediumRoom:
                SetRoomSize(0.5f);
                SetDamping(0.5f);
                SetWet(0.33f);
                SetDry(0.67f);
                SetWidth(1f);
                break;
                
            case ReverbPreset.LargeHall:
                SetRoomSize(0.8f);
                SetDamping(0.3f);
                SetWet(0.4f);
                SetDry(0.6f);
                SetWidth(1f);
                break;
                
            case ReverbPreset.Cathedral:
                SetRoomSize(0.95f);
                SetDamping(0.2f);
                SetWet(0.5f);
                SetDry(0.5f);
                SetWidth(1f);
                break;
                
            case ReverbPreset.Plate:
                SetRoomSize(0.6f);
                SetDamping(0.8f);
                SetWet(0.35f);
                SetDry(0.65f);
                SetWidth(1f);
                break;
                
            case ReverbPreset.Ambient:
                SetRoomSize(0.7f);
                SetDamping(0.6f);
                SetWet(0.2f);
                SetDry(0.8f);
                SetWidth(1f);
                break;
        }
    }
}

/// <summary>
/// Reverb presets.
/// </summary>
public enum ReverbPreset
{
    /// <summary>Small room reverb - short tail.</summary>
    SmallRoom,
    
    /// <summary>Medium room reverb - balanced.</summary>
    MediumRoom,
    
    /// <summary>Large concert hall - long tail.</summary>
    LargeHall,
    
    /// <summary>Cathedral/church - very long tail.</summary>
    Cathedral,
    
    /// <summary>Plate reverb emulation - bright sound.</summary>
    Plate,
    
    /// <summary>Ambient/atmospheric - subtle background reverb.</summary>
    Ambient
}

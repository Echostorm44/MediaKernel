// MediaKernel - Audio Effects Filters
// Chorus, flanger, delay, reverb, pitch shift, and tremolo effects

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Delay effect with feedback for echo effects.
/// </summary>
public sealed class DelayFilter
{
    private float[] delayBuffer;
    private int writePosition;
    private int sampleRate;
    private int delayBufferLength;
    
    private float delayTimeMs = 250f;
    private float feedbackGain = 0.4f;
    private float wetLevel = 0.5f;
    private float dryLevel = 0.8f;
    
    /// <summary>
    /// Creates a delay filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="maxDelayMs">Maximum delay time in milliseconds.</param>
    public DelayFilter(int sampleRate = 44100, float maxDelayMs = 1000f)
    {
        this.sampleRate = sampleRate;
        delayBufferLength = (int)(sampleRate * maxDelayMs / 1000f) + 1;
        delayBuffer = new float[delayBufferLength];
        writePosition = 0;
    }
    
    /// <summary>
    /// Delay time in milliseconds.
    /// </summary>
    public float DelayTimeMs
    {
        get => delayTimeMs;
        set => delayTimeMs = Math.Clamp(value, 0f, delayBufferLength * 1000f / sampleRate);
    }
    
    /// <summary>
    /// Feedback gain from -0.9 to 0.9. Controls how much delayed signal feeds back.
    /// </summary>
    public float FeedbackGain
    {
        get => feedbackGain;
        set => feedbackGain = Math.Clamp(value, -0.9f, 0.9f);
    }
    
    /// <summary>
    /// Wet (delayed) signal level from 0 to 1.
    /// </summary>
    public float WetLevel
    {
        get => wetLevel;
        set => wetLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Dry (original) signal level from 0 to 1.
    /// </summary>
    public float DryLevel
    {
        get => dryLevel;
        set => dryLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Process mono audio samples with delay effect.
    /// </summary>
    public void ProcessMono(Span<float> samples)
    {
        int delaySamples = (int)(sampleRate * delayTimeMs / 1000f);
        
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            
            // Read from delay buffer
            int readPosition = writePosition - delaySamples;
            if (readPosition < 0)
                readPosition += delayBufferLength;
            
            float delayed = delayBuffer[readPosition];
            
            // Write to delay buffer (input + feedback)
            delayBuffer[writePosition] = input + delayed * feedbackGain;
            
            // Mix dry and wet signals
            samples[i] = input * dryLevel + delayed * wetLevel;
            
            // Advance write position
            writePosition = (writePosition + 1) % delayBufferLength;
        }
    }
    
    /// <summary>
    /// Process stereo audio samples with delay effect.
    /// </summary>
    public void ProcessStereo(Span<float> samples)
    {
        if (samples.Length < 2)
            return;
            
        int delaySamples = (int)(sampleRate * delayTimeMs / 1000f);
        
        // Simple stereo processing - same delay for both channels
        for (int i = 0; i < samples.Length; i += 2)
        {
            for (int ch = 0; ch < 2 && i + ch < samples.Length; ch++)
            {
                float input = samples[i + ch];
                
                int readPosition = writePosition - delaySamples;
                if (readPosition < 0)
                    readPosition += delayBufferLength;
                
                float delayed = delayBuffer[readPosition];
                delayBuffer[writePosition] = input + delayed * feedbackGain;
                samples[i + ch] = input * dryLevel + delayed * wetLevel;
                
                writePosition = (writePosition + 1) % delayBufferLength;
            }
        }
    }
    
    /// <summary>
    /// Clear the delay buffer.
    /// </summary>
    public void Reset()
    {
        Array.Clear(delayBuffer);
        writePosition = 0;
    }
}

/// <summary>
/// Chorus effect using modulated delay.
/// Creates a richer, fuller sound by mixing slightly pitch-shifted copies.
/// </summary>
public sealed class ChorusFilter
{
    private float[] delayBuffer;
    private int writePosition;
    private int sampleRate;
    private int delayBufferLength;
    private double phase;
    
    private float delayTimeMs = 20f;
    private float sweepDepthMs = 6f;
    private float sweepRateHz = 0.5f;
    private float wetLevel = 0.5f;
    private float dryLevel = 0.8f;
    
    /// <summary>
    /// Creates a chorus filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    public ChorusFilter(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        // Buffer for max delay + sweep depth
        delayBufferLength = (int)(sampleRate * 0.1f); // 100ms max
        delayBuffer = new float[delayBufferLength];
        writePosition = 0;
        phase = 0;
    }
    
    /// <summary>
    /// Base delay time in milliseconds.
    /// </summary>
    public float DelayTimeMs
    {
        get => delayTimeMs;
        set => delayTimeMs = Math.Clamp(value, 1f, 50f);
    }
    
    /// <summary>
    /// Sweep depth in milliseconds (how much the delay varies).
    /// </summary>
    public float SweepDepthMs
    {
        get => sweepDepthMs;
        set => sweepDepthMs = Math.Clamp(value, 0f, 20f);
    }
    
    /// <summary>
    /// Sweep rate in Hz (how fast the delay modulates).
    /// </summary>
    public float SweepRateHz
    {
        get => sweepRateHz;
        set => sweepRateHz = Math.Clamp(value, 0.1f, 10f);
    }
    
    /// <summary>
    /// Wet (chorused) signal level from 0 to 1.
    /// </summary>
    public float WetLevel
    {
        get => wetLevel;
        set => wetLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Dry (original) signal level from 0 to 1.
    /// </summary>
    public float DryLevel
    {
        get => dryLevel;
        set => dryLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Process mono audio samples with chorus effect.
    /// </summary>
    public void ProcessMono(Span<float> samples)
    {
        double phaseIncrement = 2 * Math.PI * sweepRateHz / sampleRate;
        int baseDelaySamples = (int)(sampleRate * delayTimeMs / 1000f);
        int sweepSamples = (int)(sampleRate * sweepDepthMs / 1000f);
        
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            
            // Calculate modulated delay
            float sweep = (float)Math.Sin(phase) * sweepSamples;
            float delayFractional = baseDelaySamples + sweep;
            
            // Ensure positive delay
            if (delayFractional < 1)
                delayFractional = 1;
            
            // Linear interpolation for fractional delay
            int delayInt = (int)delayFractional;
            float fraction = delayFractional - delayInt;
            
            int readPos1 = writePosition - delayInt;
            if (readPos1 < 0) readPos1 += delayBufferLength;
            
            int readPos2 = readPos1 - 1;
            if (readPos2 < 0) readPos2 += delayBufferLength;
            
            float delayed = delayBuffer[readPos1] * (1 - fraction) + delayBuffer[readPos2] * fraction;
            
            // Write to delay buffer
            delayBuffer[writePosition] = input;
            
            // Mix dry and wet signals
            samples[i] = input * dryLevel + delayed * wetLevel;
            
            // Advance
            writePosition = (writePosition + 1) % delayBufferLength;
            phase += phaseIncrement;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
        }
    }
    
    /// <summary>
    /// Process stereo audio samples with chorus effect.
    /// </summary>
    public void ProcessStereo(Span<float> samples)
    {
        // Convert to mono, process, then back to stereo would lose stereo image
        // Instead, process each channel independently
        int halfLength = samples.Length / 2;
        Span<float> leftChannel = stackalloc float[halfLength];
        Span<float> rightChannel = stackalloc float[halfLength];
        
        // Deinterleave
        for (int i = 0; i < halfLength; i++)
        {
            leftChannel[i] = samples[i * 2];
            rightChannel[i] = samples[i * 2 + 1];
        }
        
        // Save state
        double savedPhase = phase;
        int savedWritePos = writePosition;
        
        // Process left
        ProcessMono(leftChannel);
        
        // Restore and process right (with slight phase offset for stereo spread)
        phase = savedPhase + Math.PI / 4; // Offset for stereo effect
        writePosition = savedWritePos;
        ProcessMono(rightChannel);
        
        // Interleave back
        for (int i = 0; i < halfLength; i++)
        {
            samples[i * 2] = leftChannel[i];
            samples[i * 2 + 1] = rightChannel[i];
        }
    }
    
    /// <summary>
    /// Reset the filter state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(delayBuffer);
        writePosition = 0;
        phase = 0;
    }
}

/// <summary>
/// Simple reverb effect using multiple comb and allpass filters.
/// Based on Schroeder reverb design.
/// </summary>
public sealed class ReverbFilter
{
    private readonly CombFilter[] combFilters;
    private readonly AllpassFilter[] allpassFilters;
    private float wetLevel = 0.3f;
    private float dryLevel = 0.7f;
    private float roomSize = 0.5f;
    private float damping = 0.5f;
    
    // Prime numbers for comb filter delays (in samples at 44.1kHz)
    private static readonly int[] CombDelays = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassDelays = { 225, 556, 441, 341 };
    
    /// <summary>
    /// Creates a reverb filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    public ReverbFilter(int sampleRate = 44100)
    {
        float sampleRateRatio = sampleRate / 44100f;
        
        combFilters = new CombFilter[8];
        for (int i = 0; i < 8; i++)
        {
            int delay = (int)(CombDelays[i] * sampleRateRatio);
            combFilters[i] = new CombFilter(delay);
        }
        
        allpassFilters = new AllpassFilter[4];
        for (int i = 0; i < 4; i++)
        {
            int delay = (int)(AllpassDelays[i] * sampleRateRatio);
            allpassFilters[i] = new AllpassFilter(delay);
        }
        
        UpdateParameters();
    }
    
    /// <summary>
    /// Room size from 0 (small) to 1 (large).
    /// </summary>
    public float RoomSize
    {
        get => roomSize;
        set
        {
            roomSize = Math.Clamp(value, 0f, 1f);
            UpdateParameters();
        }
    }
    
    /// <summary>
    /// High frequency damping from 0 (bright) to 1 (dark).
    /// </summary>
    public float Damping
    {
        get => damping;
        set
        {
            damping = Math.Clamp(value, 0f, 1f);
            UpdateParameters();
        }
    }
    
    /// <summary>
    /// Wet (reverb) signal level from 0 to 1.
    /// </summary>
    public float WetLevel
    {
        get => wetLevel;
        set => wetLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Dry (original) signal level from 0 to 1.
    /// </summary>
    public float DryLevel
    {
        get => dryLevel;
        set => dryLevel = Math.Clamp(value, 0f, 1f);
    }
    
    private void UpdateParameters()
    {
        float feedback = 0.7f + roomSize * 0.28f; // 0.7 to 0.98
        float damp = damping * 0.4f; // 0 to 0.4
        
        foreach (var comb in combFilters)
        {
            comb.Feedback = feedback;
            comb.Damping = damp;
        }
    }
    
    /// <summary>
    /// Process mono audio samples with reverb effect.
    /// </summary>
    public void ProcessMono(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            
            // Sum of all comb filter outputs
            float combSum = 0f;
            foreach (var comb in combFilters)
            {
                combSum += comb.Process(input);
            }
            
            // Pass through allpass filters in series
            float output = combSum;
            foreach (var allpass in allpassFilters)
            {
                output = allpass.Process(output);
            }
            
            // Mix dry and wet
            samples[i] = input * dryLevel + output * wetLevel * 0.125f; // Scale down comb sum
        }
    }
    
    /// <summary>
    /// Process stereo audio samples with reverb effect.
    /// </summary>
    public void ProcessStereo(Span<float> samples)
    {
        // Convert to mono, add reverb, then mix back
        for (int i = 0; i < samples.Length; i += 2)
        {
            float left = samples[i];
            float right = samples[i + 1];
            float mono = (left + right) * 0.5f;
            
            // Sum of all comb filter outputs
            float combSum = 0f;
            foreach (var comb in combFilters)
            {
                combSum += comb.Process(mono);
            }
            
            // Pass through allpass filters
            float reverb = combSum;
            foreach (var allpass in allpassFilters)
            {
                reverb = allpass.Process(reverb);
            }
            reverb *= wetLevel * 0.125f;
            
            // Mix
            samples[i] = left * dryLevel + reverb;
            samples[i + 1] = right * dryLevel + reverb;
        }
    }
    
    /// <summary>
    /// Reset all filter states.
    /// </summary>
    public void Reset()
    {
        foreach (var comb in combFilters)
            comb.Reset();
        foreach (var allpass in allpassFilters)
            allpass.Reset();
    }
    
    /// <summary>
    /// Comb filter for reverb.
    /// </summary>
    private sealed class CombFilter
    {
        private readonly float[] buffer;
        private int index;
        private float filterStore;
        
        public float Feedback { get; set; } = 0.7f;
        public float Damping { get; set; } = 0.2f;
        
        public CombFilter(int delay)
        {
            buffer = new float[delay];
            index = 0;
            filterStore = 0;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float input)
        {
            float output = buffer[index];
            
            // Low-pass filter in feedback path
            filterStore = output * (1 - Damping) + filterStore * Damping;
            
            buffer[index] = input + filterStore * Feedback;
            
            index = (index + 1) % buffer.Length;
            
            return output;
        }
        
        public void Reset()
        {
            Array.Clear(buffer);
            index = 0;
            filterStore = 0;
        }
    }
    
    /// <summary>
    /// Allpass filter for reverb diffusion.
    /// </summary>
    private sealed class AllpassFilter
    {
        private readonly float[] buffer;
        private int index;
        private const float Feedback = 0.5f;
        
        public AllpassFilter(int delay)
        {
            buffer = new float[delay];
            index = 0;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float input)
        {
            float buffered = buffer[index];
            float output = -input + buffered;
            
            buffer[index] = input + buffered * Feedback;
            
            index = (index + 1) % buffer.Length;
            
            return output;
        }
        
        public void Reset()
        {
            Array.Clear(buffer);
            index = 0;
        }
    }
}

/// <summary>
/// Flanger effect using very short modulated delay.
/// Similar to chorus but with shorter delays and feedback.
/// </summary>
public sealed class FlangerFilter
{
    private float[] delayBuffer;
    private int writePosition;
    private int sampleRate;
    private int delayBufferLength;
    private double phase;
    
    private float delayTimeMs = 5f;
    private float sweepDepthMs = 3f;
    private float sweepRateHz = 0.3f;
    private float feedbackGain = 0.5f;
    private float wetLevel = 0.5f;
    private float dryLevel = 0.8f;
    
    /// <summary>
    /// Creates a flanger filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    public FlangerFilter(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        delayBufferLength = (int)(sampleRate * 0.05f); // 50ms max
        delayBuffer = new float[delayBufferLength];
        writePosition = 0;
        phase = 0;
    }
    
    /// <summary>
    /// Base delay time in milliseconds (typically 1-10ms for flanging).
    /// </summary>
    public float DelayTimeMs
    {
        get => delayTimeMs;
        set => delayTimeMs = Math.Clamp(value, 0.5f, 20f);
    }
    
    /// <summary>
    /// Sweep depth in milliseconds.
    /// </summary>
    public float SweepDepthMs
    {
        get => sweepDepthMs;
        set => sweepDepthMs = Math.Clamp(value, 0f, 10f);
    }
    
    /// <summary>
    /// Sweep rate in Hz.
    /// </summary>
    public float SweepRateHz
    {
        get => sweepRateHz;
        set => sweepRateHz = Math.Clamp(value, 0.01f, 5f);
    }
    
    /// <summary>
    /// Feedback gain from -0.95 to 0.95.
    /// </summary>
    public float FeedbackGain
    {
        get => feedbackGain;
        set => feedbackGain = Math.Clamp(value, -0.95f, 0.95f);
    }
    
    /// <summary>
    /// Wet signal level from 0 to 1.
    /// </summary>
    public float WetLevel
    {
        get => wetLevel;
        set => wetLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Dry signal level from 0 to 1.
    /// </summary>
    public float DryLevel
    {
        get => dryLevel;
        set => dryLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Process mono audio samples with flanger effect.
    /// </summary>
    public void ProcessMono(Span<float> samples)
    {
        double phaseIncrement = 2 * Math.PI * sweepRateHz / sampleRate;
        int baseDelaySamples = (int)(sampleRate * delayTimeMs / 1000f);
        int sweepSamples = (int)(sampleRate * sweepDepthMs / 1000f);
        
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            
            // Calculate modulated delay
            float sweep = (float)Math.Sin(phase) * sweepSamples;
            float delayFractional = baseDelaySamples + sweep;
            
            if (delayFractional < 1)
                delayFractional = 1;
            
            // Linear interpolation
            int delayInt = (int)delayFractional;
            float fraction = delayFractional - delayInt;
            
            int readPos1 = writePosition - delayInt;
            if (readPos1 < 0) readPos1 += delayBufferLength;
            
            int readPos2 = readPos1 - 1;
            if (readPos2 < 0) readPos2 += delayBufferLength;
            
            float delayed = delayBuffer[readPos1] * (1 - fraction) + delayBuffer[readPos2] * fraction;
            
            // Write to delay buffer (with feedback)
            delayBuffer[writePosition] = input + delayed * feedbackGain;
            
            // Mix
            samples[i] = input * dryLevel + delayed * wetLevel;
            
            writePosition = (writePosition + 1) % delayBufferLength;
            phase += phaseIncrement;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
        }
    }
    
    /// <summary>
    /// Reset the filter state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(delayBuffer);
        writePosition = 0;
        phase = 0;
    }
}

/// <summary>
/// Audio tremolo effect (amplitude modulation).
/// </summary>
public sealed class TremoloFilter
{
    private int sampleRate;
    private double phase;
    
    private float rate = 5f;
    private float depth = 0.5f;
    
    /// <summary>
    /// Creates a tremolo filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    public TremoloFilter(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        phase = 0;
    }
    
    /// <summary>
    /// Tremolo rate in Hz (how fast the volume oscillates).
    /// </summary>
    public float Rate
    {
        get => rate;
        set => rate = Math.Clamp(value, 0.1f, 20f);
    }
    
    /// <summary>
    /// Tremolo depth from 0 (no effect) to 1 (full effect).
    /// </summary>
    public float Depth
    {
        get => depth;
        set => depth = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Process audio samples with tremolo effect.
    /// </summary>
    public void Process(Span<float> samples)
    {
        double phaseIncrement = 2 * Math.PI * rate / sampleRate;
        
        for (int i = 0; i < samples.Length; i++)
        {
            // Calculate modulation (0.5 to 1.0 range adjusted by depth)
            float modulation = 1f - depth * 0.5f * (1f - (float)Math.Sin(phase));
            
            samples[i] *= modulation;
            
            phase += phaseIncrement;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
        }
    }
    
    /// <summary>
    /// Reset the filter state.
    /// </summary>
    public void Reset()
    {
        phase = 0;
    }
}

/// <summary>
/// Audio pitch shifter using simple resampling.
/// Note: This is a basic implementation - high-quality pitch shifting requires more complex algorithms.
/// </summary>
public sealed class PitchShiftFilter
{
    private float[] inputBuffer;
    private float[] outputBuffer;
    private int inputWritePos;
    private double readPos;
    private int bufferLength;
    
    private float pitchRatio = 1.0f;
    
    /// <summary>
    /// Creates a pitch shift filter.
    /// </summary>
    /// <param name="bufferSize">Internal buffer size.</param>
    public PitchShiftFilter(int bufferSize = 4096)
    {
        bufferLength = bufferSize;
        inputBuffer = new float[bufferLength];
        outputBuffer = new float[bufferLength];
        inputWritePos = 0;
        readPos = 0;
    }
    
    /// <summary>
    /// Pitch ratio. 1.0 = no change, 2.0 = one octave up, 0.5 = one octave down.
    /// </summary>
    public float PitchRatio
    {
        get => pitchRatio;
        set => pitchRatio = Math.Clamp(value, 0.25f, 4f);
    }
    
    /// <summary>
    /// Pitch shift in semitones (-24 to +24).
    /// </summary>
    public float Semitones
    {
        get => 12f * MathF.Log2(pitchRatio);
        set => pitchRatio = MathF.Pow(2f, value / 12f);
    }
    
    /// <summary>
    /// Process audio samples with pitch shift.
    /// </summary>
    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Write input to buffer
            inputBuffer[inputWritePos] = samples[i];
            inputWritePos = (inputWritePos + 1) % bufferLength;
            
            // Read from buffer at shifted rate
            int readPosInt = (int)readPos;
            float fraction = (float)(readPos - readPosInt);
            
            int pos1 = readPosInt % bufferLength;
            int pos2 = (readPosInt + 1) % bufferLength;
            
            // Linear interpolation
            samples[i] = inputBuffer[pos1] * (1 - fraction) + inputBuffer[pos2] * fraction;
            
            readPos += pitchRatio;
            while (readPos >= bufferLength) readPos -= bufferLength;
        }
    }
    
    /// <summary>
    /// Reset the filter state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(inputBuffer);
        Array.Clear(outputBuffer);
        inputWritePos = 0;
        readPos = 0;
    }
}

// MediaKernel - Audio Delay/Echo Effect
// Provides delay line with feedback for echo effects

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Delay line for audio effects - circular buffer with interpolated read.
/// </summary>
internal sealed class DelayLine
{
    private readonly float[] buffer;
    private int writeIndex;
    
    public DelayLine(int maxSamples)
    {
        buffer = new float[maxSamples];
        writeIndex = 0;
    }
    
    public int Length => buffer.Length;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float sample)
    {
        buffer[writeIndex] = sample;
        writeIndex++;
        if (writeIndex >= buffer.Length)
            writeIndex = 0;
    }
    
    /// <summary>
    /// Read a sample from the delay line at the specified delay.
    /// </summary>
    /// <param name="delaySamples">Delay in samples (can be fractional for interpolation).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(float delaySamples)
    {
        float readPos = writeIndex - delaySamples;
        while (readPos < 0)
            readPos += buffer.Length;
        
        int index0 = (int)readPos;
        int index1 = index0 + 1;
        if (index1 >= buffer.Length)
            index1 = 0;
        
        float fraction = readPos - index0;
        
        // Linear interpolation
        return buffer[index0] + (buffer[index1] - buffer[index0]) * fraction;
    }
    
    /// <summary>
    /// Read without interpolation (integer delay).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int delaySamples)
    {
        int readPos = writeIndex - delaySamples;
        while (readPos < 0)
            readPos += buffer.Length;
        
        return buffer[readPos];
    }
    
    public void Clear()
    {
        Array.Clear(buffer);
        writeIndex = 0;
    }
}

/// <summary>
/// Audio delay/echo effect with multiple taps and feedback.
/// </summary>
public sealed class AudioDelay
{
    private readonly DelayLine delayLeft;
    private readonly DelayLine delayRight;
    
    private int sampleRate;
    private float delayTimeMs;
    private int delaySamples;
    private float feedback;
    private float wet;
    private float dry;
    private bool stereoOffset;
    
    /// <summary>
    /// Maximum delay time in milliseconds.
    /// </summary>
    public const float MaxDelayTimeMs = 2000f;
    
    /// <summary>
    /// Creates a new delay effect.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    public AudioDelay(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        int maxSamples = (int)(sampleRate * MaxDelayTimeMs / 1000f) + 1;
        
        delayLeft = new DelayLine(maxSamples);
        delayRight = new DelayLine(maxSamples);
        
        // Defaults
        SetDelayTime(250f);  // 250ms delay
        Feedback = 0.4f;
        Wet = 0.5f;
        Dry = 1f;
        StereoOffset = true;
    }
    
    /// <summary>
    /// Delay time in milliseconds (0 to 2000ms).
    /// </summary>
    public float DelayTimeMs
    {
        get => delayTimeMs;
        set => SetDelayTime(Math.Clamp(value, 0f, MaxDelayTimeMs));
    }
    
    /// <summary>
    /// Feedback amount (0 = no echo repeat, 0.9 = many echoes).
    /// Values above 0.9 can cause infinite feedback. Clamped to -0.95 to 0.95.
    /// </summary>
    public float Feedback
    {
        get => feedback;
        set => feedback = Math.Clamp(value, -0.95f, 0.95f);
    }
    
    /// <summary>
    /// Wet (delayed) signal level (0 to 1).
    /// </summary>
    public float Wet
    {
        get => wet;
        set => wet = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Dry (original) signal level (0 to 1).
    /// </summary>
    public float Dry
    {
        get => dry;
        set => dry = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// If true, adds slight offset between L/R channels for stereo width.
    /// </summary>
    public bool StereoOffset
    {
        get => stereoOffset;
        set => stereoOffset = value;
    }
    
    /// <summary>
    /// Sample rate for the effect.
    /// </summary>
    public int SampleRate
    {
        get => sampleRate;
        set
        {
            if (value != sampleRate && value > 0)
            {
                sampleRate = value;
                SetDelayTime(delayTimeMs);
            }
        }
    }
    
    private void SetDelayTime(float ms)
    {
        delayTimeMs = ms;
        delaySamples = (int)(ms * sampleRate / 1000f);
        
        // Ensure we don't exceed buffer
        if (delaySamples >= delayLeft.Length)
            delaySamples = delayLeft.Length - 1;
    }
    
    /// <summary>
    /// Process stereo audio samples in-place.
    /// </summary>
    /// <param name="samples">Interleaved stereo samples (L0, R0, L1, R1, ...).</param>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2 || delaySamples <= 0)
            return;
        
        int samplePairs = samples.Length / 2;
        
        // Slight offset for stereo (about 3% difference)
        int delaySamplesRight = stereoOffset 
            ? (int)(delaySamples * 1.03f) 
            : delaySamples;
        
        if (delaySamplesRight >= delayRight.Length)
            delaySamplesRight = delayRight.Length - 1;
        
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            float inputLeft = samples[offset];
            float inputRight = samples[offset + 1];
            
            // Read delayed samples
            float delayedLeft = delayLeft.Read(delaySamples);
            float delayedRight = delayRight.Read(delaySamplesRight);
            
            // Write input + feedback to delay lines
            delayLeft.Write(inputLeft + delayedLeft * feedback);
            delayRight.Write(inputRight + delayedRight * feedback);
            
            // Mix dry + wet
            samples[offset] = inputLeft * dry + delayedLeft * wet;
            samples[offset + 1] = inputRight * dry + delayedRight * wet;
        }
    }
    
    /// <summary>
    /// Process mono audio samples in-place.
    /// </summary>
    /// <param name="samples">Mono audio samples.</param>
    public void ProcessMono(Span<float> samples)
    {
        if (delaySamples <= 0)
            return;
        
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            
            float delayed = delayLeft.Read(delaySamples);
            delayLeft.Write(input + delayed * feedback);
            
            samples[i] = input * dry + delayed * wet;
        }
    }
    
    /// <summary>
    /// Clear all delay buffers.
    /// </summary>
    public void Clear()
    {
        delayLeft.Clear();
        delayRight.Clear();
    }
    
    /// <summary>
    /// Reset to default parameters and clear buffers.
    /// </summary>
    public void Reset()
    {
        SetDelayTime(250f);
        Feedback = 0.4f;
        Wet = 0.5f;
        Dry = 1f;
        StereoOffset = true;
        Clear();
    }
    
    /// <summary>
    /// Apply a delay preset.
    /// </summary>
    public void ApplyPreset(DelayPreset preset)
    {
        switch (preset)
        {
            case DelayPreset.Slapback:
                SetDelayTime(80f);
                Feedback = 0.1f;
                Wet = 0.4f;
                Dry = 1f;
                break;
                
            case DelayPreset.ShortEcho:
                SetDelayTime(200f);
                Feedback = 0.35f;
                Wet = 0.45f;
                Dry = 1f;
                break;
                
            case DelayPreset.QuarterNote:
                SetDelayTime(375f);  // ~160 BPM quarter note
                Feedback = 0.4f;
                Wet = 0.5f;
                Dry = 1f;
                break;
                
            case DelayPreset.LongEcho:
                SetDelayTime(500f);
                Feedback = 0.45f;
                Wet = 0.4f;
                Dry = 1f;
                break;
                
            case DelayPreset.Ambient:
                SetDelayTime(750f);
                Feedback = 0.6f;
                Wet = 0.35f;
                Dry = 1f;
                break;
                
            case DelayPreset.DubDelay:
                SetDelayTime(400f);
                Feedback = 0.7f;
                Wet = 0.5f;
                Dry = 0.8f;
                break;
        }
    }
    
    /// <summary>
    /// Set delay time synced to a tempo in BPM.
    /// </summary>
    /// <param name="bpm">Beats per minute.</param>
    /// <param name="division">Note division (1 = whole, 2 = half, 4 = quarter, 8 = eighth, etc.)</param>
    public void SyncToTempo(float bpm, int division = 4)
    {
        if (bpm <= 0 || division <= 0)
            return;
        
        // ms per beat = 60000 / bpm
        // divided by note division
        float msPerWholeNote = 60000f / bpm * 4f;
        float ms = msPerWholeNote / division;
        
        SetDelayTime(Math.Min(ms, MaxDelayTimeMs));
    }
}

/// <summary>
/// Delay effect presets.
/// </summary>
public enum DelayPreset
{
    /// <summary>Very short slapback echo (rockabilly style).</summary>
    Slapback,
    
    /// <summary>Short echo - subtle doubling effect.</summary>
    ShortEcho,
    
    /// <summary>Quarter note delay at ~160 BPM.</summary>
    QuarterNote,
    
    /// <summary>Long echo with moderate feedback.</summary>
    LongEcho,
    
    /// <summary>Atmospheric ambient delay.</summary>
    Ambient,
    
    /// <summary>Dub-style delay with high feedback.</summary>
    DubDelay
}

/// <summary>
/// Multi-tap delay with configurable tap positions.
/// </summary>
public sealed class MultiTapDelay
{
    private readonly DelayLine delayLine;
    private readonly List<DelayTap> taps;
    
    private int sampleRate;
    private float feedback;
    private float dry;
    
    /// <summary>
    /// Maximum delay time for the longest tap.
    /// </summary>
    public const float MaxDelayTimeMs = 2000f;
    
    /// <summary>
    /// Creates a multi-tap delay.
    /// </summary>
    public MultiTapDelay(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        int maxSamples = (int)(sampleRate * MaxDelayTimeMs / 1000f) + 1;
        
        delayLine = new DelayLine(maxSamples);
        taps = new List<DelayTap>();
        
        feedback = 0f;
        dry = 1f;
    }
    
    /// <summary>
    /// Feedback from output back to input (0 to 0.95).
    /// </summary>
    public float Feedback
    {
        get => feedback;
        set => feedback = Math.Clamp(value, 0f, 0.95f);
    }
    
    /// <summary>
    /// Dry signal level.
    /// </summary>
    public float Dry
    {
        get => dry;
        set => dry = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Current number of taps.
    /// </summary>
    public int TapCount => taps.Count;
    
    /// <summary>
    /// Add a delay tap.
    /// </summary>
    /// <param name="delayMs">Delay time in milliseconds.</param>
    /// <param name="level">Tap level (0 to 1).</param>
    /// <param name="pan">Stereo pan (-1 = left, 0 = center, 1 = right).</param>
    public void AddTap(float delayMs, float level, float pan = 0f)
    {
        int samples = (int)(Math.Min(delayMs, MaxDelayTimeMs) * sampleRate / 1000f);
        taps.Add(new DelayTap(samples, Math.Clamp(level, 0f, 1f), Math.Clamp(pan, -1f, 1f)));
    }
    
    /// <summary>
    /// Remove all taps.
    /// </summary>
    public void ClearTaps()
    {
        taps.Clear();
    }
    
    /// <summary>
    /// Process stereo samples in-place.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2 || taps.Count == 0)
            return;
        
        int samplePairs = samples.Length / 2;
        
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            float inputLeft = samples[offset];
            float inputRight = samples[offset + 1];
            
            // Mix input to mono for delay line
            float inputMono = (inputLeft + inputRight) * 0.5f;
            
            // Sum all taps
            float wetLeft = 0f;
            float wetRight = 0f;
            
            foreach (var tap in taps)
            {
                float delayed = delayLine.Read(tap.DelaySamples);
                
                // Pan law: constant power
                float leftGain = MathF.Cos((tap.Pan + 1f) * 0.25f * MathF.PI);
                float rightGain = MathF.Sin((tap.Pan + 1f) * 0.25f * MathF.PI);
                
                wetLeft += delayed * tap.Level * leftGain;
                wetRight += delayed * tap.Level * rightGain;
            }
            
            // Write to delay line with feedback
            delayLine.Write(inputMono + wetLeft * feedback);
            
            samples[offset] = inputLeft * dry + wetLeft;
            samples[offset + 1] = inputRight * dry + wetRight;
        }
    }
    
    /// <summary>
    /// Clear the delay buffer.
    /// </summary>
    public void Clear()
    {
        delayLine.Clear();
    }
    
    private readonly record struct DelayTap(int DelaySamples, float Level, float Pan);
}

// MediaKernel - Stereo Audio Filters
// Includes pan control, stereo widening, and noise gate

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Stereo pan control filter. Adjusts left/right balance.
/// </summary>
public sealed class StereoPanFilter
{
    private float panPosition = 0.5f;
    
    /// <summary>
    /// Pan position from 0.0 (full left) to 1.0 (full right). Default is 0.5 (center).
    /// </summary>
    public float PanPosition
    {
        get => panPosition;
        set => panPosition = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Pan position in dB range (-100 to +100). 0 = center.
    /// </summary>
    public int PanPositionPercent
    {
        get => (int)((panPosition - 0.5f) * 200);
        set => panPosition = Math.Clamp(value / 200f + 0.5f, 0f, 1f);
    }
    
    /// <summary>
    /// Process stereo audio samples in-place.
    /// </summary>
    /// <param name="samples">Interleaved stereo samples (L0, R0, L1, R1, ...)</param>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2)
            return;
        
        float leftGain = 1f - panPosition;
        float rightGain = panPosition;
        
        // Process in pairs (left, right)
        int samplePairs = samples.Length / 2;
        
        if (Avx.IsSupported && samplePairs >= 8)
        {
            ProcessAvx(samples, leftGain, rightGain, samplePairs);
        }
        else
        {
            ProcessScalar(samples, leftGain, rightGain, samplePairs);
        }
    }
    
    private static void ProcessScalar(Span<float> samples, float leftGain, float rightGain, int samplePairs)
    {
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            samples[offset] *= leftGain;
            samples[offset + 1] *= rightGain;
        }
    }
    
    private static unsafe void ProcessAvx(Span<float> samples, float leftGain, float rightGain, int samplePairs)
    {
        fixed (float* ptr = samples)
        {
            // Interleaved gain pattern: L, R, L, R, L, R, L, R
            var gainVector = Vector256.Create(leftGain, rightGain, leftGain, rightGain, 
                                               leftGain, rightGain, leftGain, rightGain);
            
            int vectorCount = samplePairs / 4; // 8 floats = 4 stereo pairs
            int i = 0;
            
            for (; i < vectorCount; i++)
            {
                var input = Avx.LoadVector256(ptr + i * 8);
                var output = Avx.Multiply(input, gainVector);
                Avx.Store(ptr + i * 8, output);
            }
            
            // Process remaining samples
            int processed = vectorCount * 8;
            for (int j = processed; j < samplePairs * 2; j += 2)
            {
                ptr[j] *= leftGain;
                ptr[j + 1] *= rightGain;
            }
        }
    }
    
    /// <summary>
    /// Reset pan to center.
    /// </summary>
    public void Reset()
    {
        panPosition = 0.5f;
    }
}

/// <summary>
/// Stereo widening filter. Enhances or reduces stereo separation.
/// </summary>
public sealed class StereoWidenFilter
{
    private float widthFactor = 1.0f;
    
    /// <summary>
    /// Width factor. 0.0 = mono, 1.0 = normal stereo, 2.0 = extra wide.
    /// Values above 2.0 create exaggerated stereo with phase issues.
    /// </summary>
    public float WidthFactor
    {
        get => widthFactor;
        set => widthFactor = Math.Clamp(value, 0f, 3f);
    }
    
    /// <summary>
    /// Width as percentage (0-200). 100 = normal stereo.
    /// </summary>
    public int WidthPercent
    {
        get => (int)(widthFactor * 100);
        set => widthFactor = Math.Clamp(value / 100f, 0f, 3f);
    }
    
    /// <summary>
    /// Process stereo audio samples in-place.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2)
            return;
        
        int samplePairs = samples.Length / 2;
        
        // Mid-side processing:
        // M = (L + R) / 2 (mono/center content)
        // S = (L - R) / 2 (stereo/side content)
        // L' = M + S * width
        // R' = M - S * width
        
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            float left = samples[offset];
            float right = samples[offset + 1];
            
            float mid = (left + right) * 0.5f;
            float side = (left - right) * 0.5f;
            
            samples[offset] = mid + side * widthFactor;
            samples[offset + 1] = mid - side * widthFactor;
        }
    }
    
    /// <summary>
    /// Reset width to normal.
    /// </summary>
    public void Reset()
    {
        widthFactor = 1.0f;
    }
}

/// <summary>
/// Noise gate filter. Attenuates audio below a threshold.
/// </summary>
public sealed class NoiseGateFilter
{
    private float thresholdDb = -40f;
    private float thresholdLinear;
    private float attackTimeMs = 10f;
    private float releaseTimeMs = 100f;
    private float estimationTimeMs = 20f;
    private int sampleRate = 48000;
    
    private float attackFactor;
    private float releaseFactor;
    private float alpha;
    private float[] estimatedPower = new float[8];
    private float[] currentGain = new float[8];
    
    public NoiseGateFilter()
    {
        UpdateCoefficients();
        ResetState();
    }
    
    /// <summary>
    /// Threshold level in dB. Audio below this is attenuated.
    /// Typical range: -60 dB to -20 dB.
    /// </summary>
    public float ThresholdDb
    {
        get => thresholdDb;
        set
        {
            thresholdDb = Math.Clamp(value, -80f, 0f);
            thresholdLinear = DbToLinear(thresholdDb);
        }
    }
    
    /// <summary>
    /// Attack time in milliseconds. How fast the gate opens.
    /// </summary>
    public float AttackTimeMs
    {
        get => attackTimeMs;
        set
        {
            attackTimeMs = Math.Clamp(value, 0.1f, 1000f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Release time in milliseconds. How fast the gate closes.
    /// </summary>
    public float ReleaseTimeMs
    {
        get => releaseTimeMs;
        set
        {
            releaseTimeMs = Math.Clamp(value, 1f, 5000f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Power estimation time in milliseconds. Smoothing for level detection.
    /// </summary>
    public float EstimationTimeMs
    {
        get => estimationTimeMs;
        set
        {
            estimationTimeMs = Math.Clamp(value, 1f, 500f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Sample rate for timing calculations.
    /// </summary>
    public int SampleRate
    {
        get => sampleRate;
        set
        {
            sampleRate = Math.Max(8000, value);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Process audio samples in-place.
    /// </summary>
    /// <param name="samples">Audio samples (interleaved if multi-channel)</param>
    /// <param name="channelCount">Number of audio channels</param>
    public void Process(Span<float> samples, int channelCount)
    {
        if (samples.Length == 0 || channelCount < 1)
            return;
        
        channelCount = Math.Min(channelCount, 8);
        int sampleFrames = samples.Length / channelCount;
        
        for (int frame = 0; frame < sampleFrames; frame++)
        {
            int baseOffset = frame * channelCount;
            
            for (int ch = 0; ch < channelCount; ch++)
            {
                float sample = samples[baseOffset + ch];
                float samplePower = sample * sample;
                
                // Exponential moving average for power estimation
                estimatedPower[ch] = alpha * samplePower + (1f - alpha) * estimatedPower[ch];
                float rms = MathF.Sqrt(estimatedPower[ch]);
                
                // Determine target gain
                float targetGain = rms >= thresholdLinear ? 1f : 0f;
                
                // Smooth gain transitions (attack/release envelope)
                if (targetGain > currentGain[ch])
                {
                    // Gate opening (attack)
                    currentGain[ch] = attackFactor * targetGain + (1f - attackFactor) * currentGain[ch];
                }
                else
                {
                    // Gate closing (release)
                    currentGain[ch] = releaseFactor * targetGain + (1f - releaseFactor) * currentGain[ch];
                }
                
                samples[baseOffset + ch] = sample * currentGain[ch];
            }
        }
    }
    
    /// <summary>
    /// Process stereo samples (convenience method).
    /// </summary>
    public void ProcessStereo(Span<float> samples)
    {
        Process(samples, 2);
    }
    
    /// <summary>
    /// Reset filter state.
    /// </summary>
    public void ResetState()
    {
        Array.Clear(estimatedPower);
        for (int i = 0; i < currentGain.Length; i++)
            currentGain[i] = 1f;
    }
    
    /// <summary>
    /// Reset all parameters to defaults.
    /// </summary>
    public void Reset()
    {
        thresholdDb = -40f;
        attackTimeMs = 10f;
        releaseTimeMs = 100f;
        estimationTimeMs = 20f;
        UpdateCoefficients();
        ResetState();
    }
    
    private void UpdateCoefficients()
    {
        thresholdLinear = DbToLinear(thresholdDb);
        alpha = CalculateSmoothingFactor(estimationTimeMs);
        attackFactor = CalculateSmoothingFactor(attackTimeMs);
        releaseFactor = CalculateSmoothingFactor(releaseTimeMs);
    }
    
    private float CalculateSmoothingFactor(float timeMs)
    {
        // Time constant based smoothing: α = 1 - e^(-ΔT/τ)
        float timeConstantSec = timeMs / 1000f;
        float deltaT = 1f / sampleRate;
        return 1f - MathF.Exp(-deltaT / timeConstantSec);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DbToLinear(float db)
    {
        return MathF.Pow(10f, db / 20f);
    }
}

/// <summary>
/// Simple vocal/karaoke filter. Removes center-panned audio (typically vocals).
/// </summary>
public sealed class KaraokeFilter
{
    private float effectStrength = 1.0f;
    private float lowCutoffHz = 100f;
    private float highCutoffHz = 8000f;
    private int sampleRate = 48000;
    
    // High-pass and low-pass state for frequency-selective processing
    private float[] hpState = new float[2];
    private float[] lpState = new float[2];
    private float hpCoeff;
    private float lpCoeff;
    
    public KaraokeFilter()
    {
        UpdateCoefficients();
    }
    
    /// <summary>
    /// Effect strength from 0.0 (no effect) to 1.0 (full vocal removal).
    /// </summary>
    public float EffectStrength
    {
        get => effectStrength;
        set => effectStrength = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Low frequency cutoff in Hz. Frequencies below this are not affected.
    /// </summary>
    public float LowCutoffHz
    {
        get => lowCutoffHz;
        set
        {
            lowCutoffHz = Math.Clamp(value, 20f, 500f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// High frequency cutoff in Hz. Frequencies above this are not affected.
    /// </summary>
    public float HighCutoffHz
    {
        get => highCutoffHz;
        set
        {
            highCutoffHz = Math.Clamp(value, 2000f, 20000f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Sample rate for filter coefficient calculation.
    /// </summary>
    public int SampleRate
    {
        get => sampleRate;
        set
        {
            sampleRate = Math.Max(8000, value);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Process stereo audio samples in-place.
    /// Basic approach: L' = L - R, R' = R - L (removes center-panned content)
    /// With frequency limiting to preserve bass and some highs.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (samples.Length < 2)
            return;
        
        int samplePairs = samples.Length / 2;
        
        for (int i = 0; i < samplePairs; i++)
        {
            int offset = i * 2;
            float left = samples[offset];
            float right = samples[offset + 1];
            
            // Calculate center (mono) content
            float center = (left + right) * 0.5f;
            
            // Apply frequency-selective filtering to center
            // High-pass: remove low frequencies from cancellation
            hpState[0] = hpCoeff * (hpState[0] + center - hpState[1]);
            hpState[1] = center;
            float hpOut = hpState[0];
            
            // Low-pass: remove high frequencies from cancellation  
            lpState[0] = lpState[0] + lpCoeff * (hpOut - lpState[0]);
            float filteredCenter = lpState[0];
            
            // Remove filtered center content
            float cancelAmount = filteredCenter * effectStrength;
            samples[offset] = left - cancelAmount;
            samples[offset + 1] = right - cancelAmount;
        }
    }
    
    /// <summary>
    /// Reset filter state.
    /// </summary>
    public void Reset()
    {
        effectStrength = 1.0f;
        Array.Clear(hpState);
        Array.Clear(lpState);
    }
    
    private void UpdateCoefficients()
    {
        // Simple RC filter coefficients
        float hpRc = 1f / (2f * MathF.PI * lowCutoffHz);
        float hpDt = 1f / sampleRate;
        hpCoeff = hpRc / (hpRc + hpDt);
        
        float lpRc = 1f / (2f * MathF.PI * highCutoffHz);
        float lpDt = 1f / sampleRate;
        lpCoeff = lpDt / (lpRc + lpDt);
    }
}

/// <summary>
/// Headphone surround virtualizer - creates spatial audio from surround channels.
/// Based on VLC's headphone.c - simulates speakers in a virtual room for headphone listening.
/// 
/// Uses interaural time difference (ITD) and interaural level difference (ILD) to create
/// the perception of sound sources at different positions around the listener.
/// </summary>
public sealed class HeadphoneVirtualizer
{
    // Sound speed in m/s (at 20°C)
    private const float SoundSpeed = 343.0f;
    
    // Head radius approximation (ear to ear through head center)
    private const float HeadRadius = 0.085f;
    
    // Maximum ITD in samples (head diameter / sound speed * sample rate)
    // At 48kHz: 2 * 0.085 / 343 * 48000 ≈ 24 samples
    private const int MaxItdSamples = 48;
    
    // Configuration
    private int sampleRate;
    private float roomSizeMeters;
    private bool delayCompensation;
    
    // Pre-computed speaker operations
    private readonly SpeakerVirtualization[] speakerOps;
    
    /// <summary>
    /// Gets or sets the virtual room size in meters.
    /// Larger values create more spacious sound with longer delays.
    /// Default is 3 meters (small room).
    /// </summary>
    public float RoomSizeMeters
    {
        get => roomSizeMeters;
        set
        {
            roomSizeMeters = Math.Clamp(value, 1.0f, 20.0f);
            RecalculateSpeakerPositions();
        }
    }
    
    /// <summary>
    /// Gets or sets whether to compensate for algorithm delay.
    /// When enabled, reduces latency but may affect spatialization quality.
    /// </summary>
    public bool DelayCompensation
    {
        get => delayCompensation;
        set
        {
            delayCompensation = value;
            RecalculateSpeakerPositions();
        }
    }
    
    /// <summary>
    /// Creates a headphone virtualizer for surround to stereo conversion.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate (default 48000 Hz).</param>
    /// <param name="roomSizeMeters">Virtual room size in meters (default 3m).</param>
    public HeadphoneVirtualizer(int sampleRate = 48000, float roomSizeMeters = 3.0f)
    {
        this.sampleRate = sampleRate;
        this.roomSizeMeters = Math.Clamp(roomSizeMeters, 1.0f, 20.0f);
        this.delayCompensation = false;
        
        // Initialize speaker virtualizations for up to 8 channels
        speakerOps = new SpeakerVirtualization[8];
        for (int i = 0; i < speakerOps.Length; i++)
        {
            speakerOps[i] = new SpeakerVirtualization(MaxItdSamples);
        }
        
        RecalculateSpeakerPositions();
    }
    
    /// <summary>
    /// Process 5.1 surround audio (6 channels) to stereo headphone output.
    /// </summary>
    /// <param name="input">6-channel interleaved input (FL, FR, C, LFE, BL, BR).</param>
    /// <param name="output">2-channel stereo output (L, R).</param>
    public void Process51(ReadOnlySpan<float> input, Span<float> output)
    {
        int inputFrames = input.Length / 6;
        int outputSamples = inputFrames * 2;
        
        if (output.Length < outputSamples)
            return;
        
        // Clear output
        output.Slice(0, outputSamples).Clear();
        
        // Mix each channel through its virtual speaker position
        for (int frame = 0; frame < inputFrames; frame++)
        {
            int inOffset = frame * 6;
            int outOffset = frame * 2;
            
            float frontLeft = input[inOffset + 0];
            float frontRight = input[inOffset + 1];
            float center = input[inOffset + 2];
            float lfe = input[inOffset + 3];
            float rearLeft = input[inOffset + 4];
            float rearRight = input[inOffset + 5];
            
            // Process each speaker through its virtualization
            ProcessSpeaker(speakerOps[0], frontLeft, output, outOffset);   // Front Left
            ProcessSpeaker(speakerOps[1], frontRight, output, outOffset);  // Front Right
            ProcessSpeaker(speakerOps[2], center, output, outOffset);      // Center
            ProcessSpeaker(speakerOps[3], lfe, output, outOffset);         // LFE (centered)
            ProcessSpeaker(speakerOps[4], rearLeft, output, outOffset);    // Rear Left
            ProcessSpeaker(speakerOps[5], rearRight, output, outOffset);   // Rear Right
        }
    }
    
    /// <summary>
    /// Process 7.1 surround audio (8 channels) to stereo headphone output.
    /// </summary>
    /// <param name="input">8-channel interleaved input (FL, FR, C, LFE, BL, BR, SL, SR).</param>
    /// <param name="output">2-channel stereo output (L, R).</param>
    public void Process71(ReadOnlySpan<float> input, Span<float> output)
    {
        int inputFrames = input.Length / 8;
        int outputSamples = inputFrames * 2;
        
        if (output.Length < outputSamples)
            return;
        
        output.Slice(0, outputSamples).Clear();
        
        for (int frame = 0; frame < inputFrames; frame++)
        {
            int inOffset = frame * 8;
            int outOffset = frame * 2;
            
            float frontLeft = input[inOffset + 0];
            float frontRight = input[inOffset + 1];
            float center = input[inOffset + 2];
            float lfe = input[inOffset + 3];
            float rearLeft = input[inOffset + 4];
            float rearRight = input[inOffset + 5];
            float sideLeft = input[inOffset + 6];
            float sideRight = input[inOffset + 7];
            
            ProcessSpeaker(speakerOps[0], frontLeft, output, outOffset);
            ProcessSpeaker(speakerOps[1], frontRight, output, outOffset);
            ProcessSpeaker(speakerOps[2], center, output, outOffset);
            ProcessSpeaker(speakerOps[3], lfe, output, outOffset);
            ProcessSpeaker(speakerOps[4], rearLeft, output, outOffset);
            ProcessSpeaker(speakerOps[5], rearRight, output, outOffset);
            ProcessSpeaker(speakerOps[6], sideLeft, output, outOffset);
            ProcessSpeaker(speakerOps[7], sideRight, output, outOffset);
        }
    }
    
    /// <summary>
    /// Process stereo input with enhanced spatialization for headphones.
    /// </summary>
    /// <param name="input">2-channel stereo input (L, R).</param>
    /// <param name="output">2-channel stereo output with spatialization (L, R).</param>
    public void ProcessStereo(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / 2;
        
        if (output.Length < frames * 2)
            return;
        
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * 2;
            float left = input[offset];
            float right = input[offset + 1];
            
            // Clear and mix through virtual front speakers
            output[offset] = 0;
            output[offset + 1] = 0;
            
            ProcessSpeaker(speakerOps[0], left, output, offset);
            ProcessSpeaker(speakerOps[1], right, output, offset);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessSpeaker(SpeakerVirtualization speaker, float sample, Span<float> output, int offset)
    {
        // First push sample into delay buffer
        speaker.PushSample(sample);
        
        // Get delayed samples for each ear
        float leftSample = speaker.GetDelayedSample(speaker.LeftDelaySamples);
        float rightSample = speaker.GetDelayedSample(speaker.RightDelaySamples);
        
        // Apply level differences
        output[offset] += leftSample * speaker.LeftGain;
        output[offset + 1] += rightSample * speaker.RightGain;
    }
    
    /// <summary>
    /// Reset all delay buffers (call when seeking or track changes).
    /// </summary>
    public void Reset()
    {
        foreach (var op in speakerOps)
        {
            op.Reset();
        }
    }
    
    private void RecalculateSpeakerPositions()
    {
        // Speaker positions using normalized coordinates
        // X: left (-1) to right (+1)
        // Z: front (+1) to back (-1)
        
        // Front Left at -30 degrees
        SetSpeakerPosition(speakerOps[0], -0.5f, 0.866f, 1.0f);
        
        // Front Right at +30 degrees  
        SetSpeakerPosition(speakerOps[1], 0.5f, 0.866f, 1.0f);
        
        // Center at 0 degrees
        SetSpeakerPosition(speakerOps[2], 0f, 1.0f, 1.0f);
        
        // LFE - same as center (bass is non-directional)
        SetSpeakerPosition(speakerOps[3], 0f, 1.0f, 0.7f);
        
        // Rear Left at -110 degrees
        SetSpeakerPosition(speakerOps[4], -0.94f, -0.34f, 0.7f);
        
        // Rear Right at +110 degrees
        SetSpeakerPosition(speakerOps[5], 0.94f, -0.34f, 0.7f);
        
        // Side Left at -90 degrees
        SetSpeakerPosition(speakerOps[6], -1.0f, 0f, 0.8f);
        
        // Side Right at +90 degrees
        SetSpeakerPosition(speakerOps[7], 1.0f, 0f, 0.8f);
    }
    
    private void SetSpeakerPosition(SpeakerVirtualization speaker, float normalizedX, float normalizedZ, float channelGain)
    {
        // Calculate ITD (interaural time difference)
        // Woodworth formula approximation for ITD:
        // ITD = (HeadRadius / SoundSpeed) * (azimuth + sin(azimuth))
        float azimuthRadians = MathF.Atan2(normalizedX, Math.Max(normalizedZ, 0.001f));
        float itdSeconds = (HeadRadius / SoundSpeed) * (MathF.Abs(azimuthRadians) + MathF.Sin(MathF.Abs(azimuthRadians)));
        int itdSamples = (int)(itdSeconds * sampleRate);
        itdSamples = Math.Clamp(itdSamples, 0, MaxItdSamples - 2);
        
        // Left ear gets signal earlier for left sources
        // Right ear gets signal earlier for right sources
        if (normalizedX < 0)
        {
            // Source on left - left ear earlier
            speaker.LeftDelaySamples = 1;  // Minimal delay
            speaker.RightDelaySamples = 1 + itdSamples;
        }
        else if (normalizedX > 0)
        {
            // Source on right - right ear earlier
            speaker.LeftDelaySamples = 1 + itdSamples;
            speaker.RightDelaySamples = 1;
        }
        else
        {
            // Center source - both ears equal
            speaker.LeftDelaySamples = 1;
            speaker.RightDelaySamples = 1;
        }
        
        // Calculate ILD (interaural level difference)
        // Sources to the left are louder in left ear
        float ild = MathF.Sin(azimuthRadians) * 0.2f;  // ±20% level difference
        
        speaker.LeftGain = channelGain * (0.5f - ild);
        speaker.RightGain = channelGain * (0.5f + ild);
    }
    
    /// <summary>
    /// Internal class for per-speaker virtualization state.
    /// </summary>
    private sealed class SpeakerVirtualization
    {
        private readonly float[] delayBuffer;
        private int writePosition;
        
        public int LeftDelaySamples;
        public int RightDelaySamples;
        public float LeftGain;
        public float RightGain;
        
        public SpeakerVirtualization(int maxDelay)
        {
            delayBuffer = new float[maxDelay];
            writePosition = 0;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetDelayedSample(int delaySamples)
        {
            int readPos = writePosition - delaySamples;
            if (readPos < 0)
                readPos += delayBuffer.Length;

            return delayBuffer[readPos];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushSample(float sample)
        {
            delayBuffer[writePosition] = sample;
            writePosition = (writePosition + 1) % delayBuffer.Length;
        }

        public void Reset()
        {
            Array.Clear(delayBuffer);
            writePosition = 0;
        }
    }
}

/// <summary>
/// Center channel extraction filter - creates a phantom center channel from stereo.
/// Based on VLC's center.c. Outputs 3 channels: Left, Right, Center.
/// </summary>
public sealed class CenterChannelExtractor
{
    // Factor for center extraction (sqrt(2)/2 for equal power)
    private const float CenterFactor = 0.70710678f;
    
    /// <summary>
    /// Whether to output only the center channel (mono).
    /// </summary>
    public bool CenterOnly { get; set; }
    
    /// <summary>
    /// Extract center channel from stereo and output 3 channels (L, R, C).
    /// </summary>
    /// <param name="input">Interleaved stereo samples (L, R, L, R, ...)</param>
    /// <param name="output">Output buffer for 3-channel samples (L, R, C, L, R, C, ...)</param>
    public void ExtractToThreeChannel(ReadOnlySpan<float> input, Span<float> output)
    {
        int sampleCount = input.Length / 2;
        
        for (int i = 0; i < sampleCount; i++)
        {
            float left = input[i * 2];
            float right = input[i * 2 + 1];
            float center = (left + right) * CenterFactor * 0.5f;
            
            output[i * 3] = left;
            output[i * 3 + 1] = right;
            output[i * 3 + 2] = center;
        }
    }
    
    /// <summary>
    /// Extract only the center channel from stereo (mono output).
    /// </summary>
    /// <param name="input">Interleaved stereo samples (L, R, L, R, ...)</param>
    /// <param name="output">Output buffer for center channel (mono)</param>
    public void ExtractCenterOnly(ReadOnlySpan<float> input, Span<float> output)
    {
        int sampleCount = input.Length / 2;
        
        if (Avx.IsSupported && sampleCount >= 8)
        {
            ExtractCenterOnlyAvx(input, output, sampleCount);
        }
        else if (Sse.IsSupported && sampleCount >= 4)
        {
            ExtractCenterOnlySse(input, output, sampleCount);
        }
        else
        {
            ExtractCenterOnlyScalar(input, output, sampleCount);
        }
    }
    
    /// <summary>
    /// Extract "vocals" by removing what's different between L and R.
    /// This is the opposite of karaoke - it isolates the center.
    /// </summary>
    /// <param name="samples">Interleaved stereo samples (modified in place)</param>
    public void IsolateCenterInPlace(Span<float> samples)
    {
        int sampleCount = samples.Length / 2;
        
        for (int i = 0; i < sampleCount; i++)
        {
            float left = samples[i * 2];
            float right = samples[i * 2 + 1];
            float center = (left + right) * 0.5f;
            
            // Output mono center to both channels
            samples[i * 2] = center;
            samples[i * 2 + 1] = center;
        }
    }
    
    private void ExtractCenterOnlyScalar(ReadOnlySpan<float> input, Span<float> output, int sampleCount)
    {
        for (int i = 0; i < sampleCount; i++)
        {
            float left = input[i * 2];
            float right = input[i * 2 + 1];
            output[i] = (left + right) * CenterFactor * 0.5f;
        }
    }
    
    private void ExtractCenterOnlySse(ReadOnlySpan<float> input, Span<float> output, int sampleCount)
    {
        int i = 0;
        Vector128<float> factor = Vector128.Create(CenterFactor * 0.5f);
        
        for (; i + 4 <= sampleCount; i += 4)
        {
            // Load 8 samples (4 pairs)
            var pair0 = Vector128.Create(input[i * 2], input[i * 2 + 1], input[i * 2 + 2], input[i * 2 + 3]);
            var pair1 = Vector128.Create(input[i * 2 + 4], input[i * 2 + 5], input[i * 2 + 6], input[i * 2 + 7]);
            
            // Shuffle to get L and R separated
            var left = Sse.Shuffle(pair0, pair1, 0b10_00_10_00);  // L0, L1, L2, L3
            var right = Sse.Shuffle(pair0, pair1, 0b11_01_11_01); // R0, R1, R2, R3
            
            // Sum and scale
            var sum = Sse.Add(left, right);
            var center = Sse.Multiply(sum, factor);
            
            // Store
            center.CopyTo(output.Slice(i, 4));
        }
        
        // Handle remaining samples
        for (; i < sampleCount; i++)
        {
            output[i] = (input[i * 2] + input[i * 2 + 1]) * CenterFactor * 0.5f;
        }
    }
    
    private void ExtractCenterOnlyAvx(ReadOnlySpan<float> input, Span<float> output, int sampleCount)
    {
        int i = 0;
        Vector256<float> factor = Vector256.Create(CenterFactor * 0.5f);
        
        for (; i + 8 <= sampleCount; i += 8)
        {
            // Load 16 samples (8 pairs)
            var v0 = Vector256.Create(
                input[i * 2], input[i * 2 + 2], input[i * 2 + 4], input[i * 2 + 6],
                input[i * 2 + 8], input[i * 2 + 10], input[i * 2 + 12], input[i * 2 + 14]);
            var v1 = Vector256.Create(
                input[i * 2 + 1], input[i * 2 + 3], input[i * 2 + 5], input[i * 2 + 7],
                input[i * 2 + 9], input[i * 2 + 11], input[i * 2 + 13], input[i * 2 + 15]);
            
            var sum = Avx.Add(v0, v1);
            var center = Avx.Multiply(sum, factor);
            
            center.CopyTo(output.Slice(i, 8));
        }
        
        // Handle remaining with SSE or scalar
        for (; i < sampleCount; i++)
        {
            output[i] = (input[i * 2] + input[i * 2 + 1]) * CenterFactor * 0.5f;
        }
    }
}

/// <summary>
/// Bass management filter - redirects low frequencies to subwoofer or mixes to main channels.
/// </summary>
public sealed class BassManagementFilter
{
    private float crossoverFrequency = 80f;
    private float[] lowPassState;
    private float[] highPassState;
    private float lowPassCoeff;
    private float highPassCoeff;
    private int sampleRate;
    private int channelCount;
    
    /// <summary>
    /// Crossover frequency in Hz (default 80 Hz).
    /// </summary>
    public float CrossoverFrequency
    {
        get => crossoverFrequency;
        set
        {
            crossoverFrequency = Math.Clamp(value, 20f, 200f);
            UpdateCoefficients();
        }
    }
    
    /// <summary>
    /// Initialize the bass management filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="channelCount">Number of input channels.</param>
    public void Initialize(int sampleRate, int channelCount)
    {
        this.sampleRate = sampleRate;
        this.channelCount = channelCount;
        lowPassState = new float[channelCount];
        highPassState = new float[channelCount];
        UpdateCoefficients();
    }
    
    private void UpdateCoefficients()
    {
        if (sampleRate == 0) return;
        
        // Simple one-pole filter coefficients
        float omega = 2.0f * MathF.PI * crossoverFrequency / sampleRate;
        lowPassCoeff = 1.0f - MathF.Exp(-omega);
        highPassCoeff = 1.0f - lowPassCoeff;
    }
    
    /// <summary>
    /// Extract bass frequencies from all channels and output to LFE channel.
    /// </summary>
    /// <param name="input">Multi-channel input samples.</param>
    /// <param name="output">Multi-channel output with LFE added.</param>
    /// <param name="lfeChannelIndex">Index of the LFE channel in output.</param>
    public void ExtractBass(ReadOnlySpan<float> input, Span<float> output, int lfeChannelIndex)
    {
        int sampleCount = input.Length / channelCount;
        float lfeSum = 0;
        
        for (int i = 0; i < sampleCount; i++)
        {
            lfeSum = 0;
            
            for (int ch = 0; ch < channelCount; ch++)
            {
                int idx = i * channelCount + ch;
                float sample = input[idx];
                
                // Low-pass filter for bass
                lowPassState[ch] += lowPassCoeff * (sample - lowPassState[ch]);
                float bass = lowPassState[ch];
                
                // Sum bass to LFE
                lfeSum += bass * 0.5f; // Reduce level to prevent clipping
                
                // High-pass filter removes bass from main channel
                output[idx] = sample - bass;
            }
            
            // Write LFE (if we have enough output channels)
            int lfeIdx = i * (channelCount + 1) + lfeChannelIndex;
            if (lfeIdx < output.Length)
            {
                output[lfeIdx] = lfeSum;
            }
        }
    }
    
    /// <summary>
    /// Redirect LFE to main speakers (for systems without subwoofer).
    /// </summary>
    /// <param name="samples">Multi-channel samples including LFE.</param>
    /// <param name="lfeChannelIndex">Index of the LFE channel.</param>
    public void RedirectLfeToMains(Span<float> samples, int lfeChannelIndex)
    {
        int sampleCount = samples.Length / channelCount;
        
        for (int i = 0; i < sampleCount; i++)
        {
            int lfeIdx = i * channelCount + lfeChannelIndex;
            float lfe = samples[lfeIdx];
            
            // Distribute LFE equally to front channels (0, 1)
            samples[i * channelCount] += lfe * 0.5f;
            samples[i * channelCount + 1] += lfe * 0.5f;
            
            // Zero out LFE channel
            samples[lfeIdx] = 0;
        }
    }
    
    /// <summary>
    /// Reset filter state.
    /// </summary>
    public void Reset()
    {
        if (lowPassState != null) Array.Clear(lowPassState);
        if (highPassState != null) Array.Clear(highPassState);
    }
}

/// <summary>
/// Dynamic range compressor for audio.
/// Based on VLC's compressor.c (ported from LADSPA SWH plugins).
/// Reduces the dynamic range of audio by attenuating loud sounds and boosting quiet ones.
/// </summary>
public sealed class DynamicRangeCompressor
{
    // RMS calculation ring buffer
    private const int RmsBufferSize = 960;
    private const int LookaheadSize = RmsBufferSize * 2;
    private const int AttackTableSize = 256;
    
    private readonly float[] rmsBuffer = new float[RmsBufferSize];
    private readonly float[] lookaheadBuffer;
    private readonly float[] attackTable = new float[AttackTableSize];
    private readonly float[] dbTable = new float[1024];
    private readonly float[] linearTable = new float[1024];
    
    private int rmsPosition;
    private int rmsCount;
    private float rmsSum;
    
    private int lookaheadPosition;
    private float envelope;
    private float envelopePeak;
    private float envelopeRms;
    private float currentGain;
    private float amplitudeRms;
    private float runningSum;
    private int sampleCounter;
    
    private int sampleRate;
    private int channelCount;
    
    // Compressor parameters
    private float rmsPeakMix = 0.0f; // 0 = pure RMS, 1 = pure peak
    private float attackTime = 1.5f; // ms
    private float releaseTime = 400f; // ms
    private float thresholdDb = -10f; // dB
    private float ratio = 4f; // ratio:1
    private float kneeRadius = 3f; // dB
    private float makeupGain = 6f; // dB
    
    /// <summary>
    /// RMS/Peak mix (0 = pure RMS detection, 1 = pure peak detection). Default 0.
    /// </summary>
    public float RmsPeakMix
    {
        get => rmsPeakMix;
        set => rmsPeakMix = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Attack time in milliseconds (0.01-500). Default 1.5 ms.
    /// </summary>
    public float AttackTimeMs
    {
        get => attackTime;
        set => attackTime = Math.Clamp(value, 0.01f, 500f);
    }
    
    /// <summary>
    /// Release time in milliseconds (0.01-2000). Default 400 ms.
    /// </summary>
    public float ReleaseTimeMs
    {
        get => releaseTime;
        set => releaseTime = Math.Clamp(value, 0.01f, 2000f);
    }
    
    /// <summary>
    /// Threshold in dB (-60 to 0). Signals above this are compressed. Default -10 dB.
    /// </summary>
    public float ThresholdDb
    {
        get => thresholdDb;
        set => thresholdDb = Math.Clamp(value, -60f, 0f);
    }
    
    /// <summary>
    /// Compression ratio (1-20). 4:1 means 4dB above threshold becomes 1dB. Default 4.
    /// </summary>
    public float Ratio
    {
        get => ratio;
        set => ratio = Math.Clamp(value, 1f, 20f);
    }
    
    /// <summary>
    /// Knee radius in dB (0-10). Softens the transition at threshold. Default 3 dB.
    /// </summary>
    public float KneeRadiusDb
    {
        get => kneeRadius;
        set => kneeRadius = Math.Clamp(value, 0f, 10f);
    }
    
    /// <summary>
    /// Makeup gain in dB (-20 to 20). Applied after compression. Default 6 dB.
    /// </summary>
    public float MakeupGainDb
    {
        get => makeupGain;
        set => makeupGain = Math.Clamp(value, -20f, 20f);
    }
    
    /// <summary>
    /// Current gain reduction in dB (negative value indicates compression).
    /// </summary>
    public float GainReductionDb { get; private set; }
    
    /// <summary>
    /// Creates a new dynamic range compressor.
    /// </summary>
    public DynamicRangeCompressor()
    {
        lookaheadBuffer = new float[LookaheadSize];
        BuildTables();
    }
    
    private void BuildTables()
    {
        // Build attack coefficient table
        for (int i = 0; i < AttackTableSize; i++)
        {
            attackTable[i] = (float)Math.Exp(-1.0 / (1.0 + (double)i / AttackTableSize * 1.5));
        }
        
        // Build dB lookup table (-60 to +24 dB)
        const float dbMin = -60f;
        const float dbMax = 24f;
        for (int i = 0; i < 1024; i++)
        {
            float db = dbMin + (dbMax - dbMin) * i / 1023f;
            dbTable[i] = (float)Math.Pow(10.0, db / 20.0);
        }
        
        // Build linear to dB lookup table
        const float linMin = 0.0000000002f;
        const float linMax = 9f;
        for (int i = 0; i < 1024; i++)
        {
            float lin = linMin + (linMax - linMin) * i / 1023f;
            linearTable[i] = 20f * (float)Math.Log10(Math.Max(lin, linMin));
        }
    }
    
    /// <summary>
    /// Initialize the compressor.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="channelCount">Number of audio channels.</param>
    public void Initialize(int sampleRate, int channelCount)
    {
        this.sampleRate = sampleRate;
        this.channelCount = channelCount;
        Reset();
    }
    
    /// <summary>
    /// Process audio samples through the compressor.
    /// </summary>
    /// <param name="samples">Interleaved audio samples (modified in place).</param>
    public void Process(Span<float> samples)
    {
        if (sampleRate == 0 || channelCount == 0) return;
        
        int sampleCount = samples.Length / channelCount;
        
        // Calculate coefficients
        float attackCoeff = attackTime < 2f ? 0f : 
            attackTable[(int)(attackTime * 0.001f * (AttackTableSize - 1))];
        float releaseCoeff = attackTable[(int)Math.Min(AttackTableSize - 1, releaseTime * 0.001f * (AttackTableSize - 1))];
        float ratioScale = (ratio - 1f) / ratio;
        float makeupLinear = DbToLinear(makeupGain);
        float kneeMinLinear = DbToLinear(thresholdDb - kneeRadius);
        float kneeMaxLinear = DbToLinear(thresholdDb + kneeRadius);
        float thresholdLinear = DbToLinear(thresholdDb);
        
        float envelopeFastAttack = attackCoeff * 0.25f;
        float envelopeFastAttackInv = 1f - envelopeFastAttack;
        
        for (int i = 0; i < sampleCount; i++)
        {
            int baseIdx = i * channelCount;
            
            // Find peak level of this sample across all channels
            float levelIn = 0f;
            for (int ch = 0; ch < channelCount; ch++)
            {
                levelIn = Math.Max(levelIn, Math.Abs(samples[baseIdx + ch]));
            }
            
            // Store in lookahead and get old value
            float levelOld = lookaheadBuffer[lookaheadPosition];
            lookaheadBuffer[lookaheadPosition] = levelIn;
            lookaheadPosition = (lookaheadPosition + 1) % LookaheadSize;
            
            // Update running sum for RMS calculation
            runningSum += levelIn * levelIn;
            
            // Update RMS envelope
            if (amplitudeRms > envelopeRms)
            {
                envelopeRms = envelopeRms * attackCoeff + amplitudeRms * (1f - attackCoeff);
            }
            else
            {
                envelopeRms = envelopeRms * releaseCoeff + amplitudeRms * (1f - releaseCoeff);
            }
            RoundToZero(ref envelopeRms);
            
            // Update peak envelope
            if (levelOld > envelopePeak)
            {
                envelopePeak = envelopePeak * attackCoeff + levelOld * (1f - attackCoeff);
            }
            else
            {
                envelopePeak = envelopePeak * releaseCoeff + levelOld * (1f - releaseCoeff);
            }
            RoundToZero(ref envelopePeak);
            
            // Process RMS and calculate gain every 4 samples
            if ((sampleCounter++ & 3) == 3)
            {
                // Calculate RMS amplitude
                amplitudeRms = ProcessRms(runningSum * 0.25f);
                runningSum = 0f;
                
                if (float.IsNaN(envelopeRms))
                    envelopeRms = 0f;
                
                // Mix RMS and peak envelopes
                envelope = LinearInterp(rmsPeakMix, envelopeRms, envelopePeak);
                
                // Calculate gain reduction
                if (envelope <= kneeMinLinear)
                {
                    // Below knee - no compression
                    currentGain = 1f;
                }
                else if (envelope < kneeMaxLinear)
                {
                    // In knee region - soft transition
                    float kneeRange = kneeMaxLinear - kneeMinLinear;
                    float position = (envelope - kneeMinLinear) / kneeRange;
                    float gainReduction = position * position * kneeRadius * ratioScale;
                    currentGain = DbToLinear(-gainReduction);
                }
                else
                {
                    // Above knee - full compression
                    float overshootDb = LinearToDb(envelope) - thresholdDb;
                    currentGain = DbToLinear(-overshootDb * ratioScale);
                }
                
                currentGain *= makeupLinear;
            }
            
            // Apply gain smoothly
            float gainSmoothed = envelope * envelopeFastAttack + 
                                (envelope > envelopePeak ? currentGain : currentGain * envelopeFastAttackInv);
            
            // Apply gain to all channels
            for (int ch = 0; ch < channelCount; ch++)
            {
                samples[baseIdx + ch] *= currentGain;
            }
            
            // Update gain reduction display
            GainReductionDb = LinearToDb(currentGain / makeupLinear);
        }
    }
    
    private float ProcessRms(float squaredInput)
    {
        // Simple moving average RMS
        rmsSum -= rmsBuffer[rmsPosition];
        rmsBuffer[rmsPosition] = squaredInput;
        rmsSum += squaredInput;
        rmsPosition = (rmsPosition + 1) % RmsBufferSize;
        
        if (rmsCount < RmsBufferSize)
            rmsCount++;
        
        return (float)Math.Sqrt(rmsSum / rmsCount);
    }
    
    private static float LinearInterp(float t, float a, float b)
    {
        return a + t * (b - a);
    }
    
    private static float DbToLinear(float db)
    {
        return (float)Math.Pow(10.0, db / 20.0);
    }
    
    private static float LinearToDb(float linear)
    {
        return 20f * (float)Math.Log10(Math.Max(linear, 0.0000001f));
    }
    
    private static void RoundToZero(ref float value)
    {
        // Prevent denormals
        if (Math.Abs(value) < 1e-15f)
            value = 0f;
    }
    
    /// <summary>
    /// Reset compressor state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(rmsBuffer);
        Array.Clear(lookaheadBuffer);
        rmsPosition = 0;
        rmsCount = 0;
        rmsSum = 0;
        lookaheadPosition = 0;
        envelope = 0;
        envelopePeak = 0;
        envelopeRms = 0;
        currentGain = 1f;
        amplitudeRms = 0;
        runningSum = 0;
        sampleCounter = 0;
        GainReductionDb = 0;
    }
}

/// <summary>
/// Limiter (a special case of compressor with high ratio and fast attack).
/// Prevents audio from exceeding a maximum level.
/// </summary>
public sealed class AudioLimiter
{
    private readonly DynamicRangeCompressor compressor = new();
    
    /// <summary>
    /// Ceiling level in dB (-20 to 0). Audio will not exceed this level. Default -0.1 dB.
    /// </summary>
    public float CeilingDb
    {
        get => compressor.ThresholdDb;
        set => compressor.ThresholdDb = Math.Clamp(value, -20f, 0f);
    }
    
    /// <summary>
    /// Release time in milliseconds. Default 50 ms for limiting.
    /// </summary>
    public float ReleaseTimeMs
    {
        get => compressor.ReleaseTimeMs;
        set => compressor.ReleaseTimeMs = value;
    }
    
    /// <summary>
    /// Creates a new audio limiter.
    /// </summary>
    public AudioLimiter()
    {
        // Configure compressor as limiter
        compressor.Ratio = 20f; // Very high ratio (essentially infinity:1)
        compressor.AttackTimeMs = 0.01f; // Very fast attack (essentially instant)
        compressor.ReleaseTimeMs = 50f; // Moderate release
        compressor.KneeRadiusDb = 0f; // Hard knee
        compressor.ThresholdDb = -0.1f; // Just below 0 dBFS
        compressor.MakeupGainDb = 0f; // No makeup gain for limiting
        compressor.RmsPeakMix = 1f; // Peak detection only
    }
    
    /// <summary>
    /// Initialize the limiter.
    /// </summary>
    public void Initialize(int sampleRate, int channelCount)
    {
        compressor.Initialize(sampleRate, channelCount);
    }
    
    /// <summary>
    /// Process audio through the limiter.
    /// </summary>
    public void Process(Span<float> samples)
    {
        compressor.Process(samples);
    }
    
    /// <summary>
    /// Current gain reduction in dB.
    /// </summary>
    public float GainReductionDb => compressor.GainReductionDb;
    
    /// <summary>
    /// Reset limiter state.
    /// </summary>
    public void Reset()
    {
        compressor.Reset();
    }
}

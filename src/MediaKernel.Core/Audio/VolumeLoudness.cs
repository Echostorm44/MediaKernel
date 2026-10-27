using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio
{
    /// <summary>
    /// Volume and loudness processing for audio output.
    /// 
    /// Features:
    /// - Logarithmic volume curve for natural perception
    /// - EBU R128 inspired loudness measurement
    /// - Peak limiting with soft clipping
    /// </summary>
    public sealed class VolumeLoudness
    {
        // Volume state
        private float linearVolume = 1.0f;
        private float targetLinearVolume = 1.0f;
        private bool isMuted;
        
        // Loudness measurement (simplified EBU R128)
        private double momentaryLoudnessSquaredSum;
        private int momentaryLoudnessSampleCount;
        private const int MomentaryWindowSamples = 400 * 48; // 400ms at 48kHz
        
        // Peak limiter state
        private float peakCeiling = 0.95f;
        private float attackCoeff;
        private float releaseCoeff;
        private float envelope;
        
        // Ramp for click-free volume changes
        private const int RampSamples = 64;

        /// <summary>
        /// Creates a new volume/loudness processor.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz.</param>
        public VolumeLoudness(int sampleRate = 48000)
        {
            // Attack: 5ms, Release: 100ms
            float attackTime = 0.005f;
            float releaseTime = 0.100f;
            attackCoeff = MathF.Exp(-1.0f / (sampleRate * attackTime));
            releaseCoeff = MathF.Exp(-1.0f / (sampleRate * releaseTime));
        }

        /// <summary>
        /// Gets or sets volume using a slider value (0.0 to 1.0).
        /// Uses logarithmic curve: linear = slider^4 for natural perception.
        /// </summary>
        public float SliderVolume
        {
            get
            {
                // Inverse of slider^4
                return MathF.Pow(targetLinearVolume, 0.25f);
            }
            set
            {
                float clamped = Math.Clamp(value, 0.0f, 1.0f);
                targetLinearVolume = MathF.Pow(clamped, 4.0f);
            }
        }

        /// <summary>
        /// Gets or sets linear volume directly (0.0 to 1.0).
        /// </summary>
        public float LinearVolume
        {
            get => targetLinearVolume;
            set => targetLinearVolume = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Gets or sets mute state.
        /// </summary>
        public bool IsMuted
        {
            get => isMuted;
            set => isMuted = value;
        }

        /// <summary>
        /// Gets or sets the peak limiter ceiling (0.0 to 1.0).
        /// Default is 0.95 to prevent clipping.
        /// </summary>
        public float PeakCeiling
        {
            get => peakCeiling;
            set => peakCeiling = Math.Clamp(value, 0.1f, 1.0f);
        }

        /// <summary>
        /// Gets the current momentary loudness in LUFS (Loudness Units Full Scale).
        /// </summary>
        public float MomentaryLoudnessLufs
        {
            get
            {
                if (momentaryLoudnessSampleCount == 0)
                    return -100.0f; // Silence
                
                double meanSquare = momentaryLoudnessSquaredSum / momentaryLoudnessSampleCount;
                if (meanSquare < 1e-10)
                    return -100.0f;
                
                // LUFS = -0.691 + 10 * log10(mean_square)
                return (float)(-0.691 + 10.0 * Math.Log10(meanSquare));
            }
        }

        /// <summary>
        /// Applies volume, loudness measurement, and peak limiting to audio.
        /// </summary>
        /// <param name="buffer">Audio buffer (modified in place).</param>
        /// <param name="channelCount">Number of channels (1 or 2).</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Process(Span<float> buffer, int channelCount)
        {
            if (isMuted)
            {
                buffer.Clear();
                return;
            }
            
            int frameCount = buffer.Length / channelCount;
            
            // Calculate volume ramp if target changed
            float volumeStep = (targetLinearVolume - linearVolume) / Math.Min(RampSamples, frameCount);
            
            if (channelCount == 1)
            {
                ProcessMono(buffer, frameCount, volumeStep);
            }
            else if (channelCount == 2)
            {
                if (Sse.IsSupported && frameCount >= 4)
                {
                    ProcessStereoSse(buffer, frameCount, volumeStep);
                }
                else
                {
                    ProcessStereo(buffer, frameCount, volumeStep);
                }
            }
            
            // Finalize volume ramp
            linearVolume = targetLinearVolume;
        }

        /// <summary>
        /// Processes mono audio.
        /// </summary>
        private void ProcessMono(Span<float> buffer, int frameCount, float volumeStep)
        {
            float volume = linearVolume;
            double loudnessSum = 0;
            
            for (int i = 0; i < frameCount; i++)
            {
                // Apply volume with ramp
                if (i < RampSamples)
                    volume += volumeStep;
                
                float sample = buffer[i] * volume;
                
                // Measure loudness
                loudnessSum += sample * sample;
                
                // Apply peak limiter
                sample = SoftClip(sample);
                
                buffer[i] = sample;
            }
            
            UpdateLoudness(loudnessSum, frameCount);
        }

        /// <summary>
        /// Processes stereo audio (scalar).
        /// </summary>
        private void ProcessStereo(Span<float> buffer, int frameCount, float volumeStep)
        {
            float volume = linearVolume;
            double loudnessSum = 0;
            
            for (int i = 0; i < frameCount; i++)
            {
                if (i < RampSamples)
                    volume += volumeStep;
                
                int idx = i * 2;
                float left = buffer[idx] * volume;
                float right = buffer[idx + 1] * volume;
                
                // Measure loudness (sum of both channels)
                loudnessSum += left * left + right * right;
                
                // Apply peak limiter
                buffer[idx] = SoftClip(left);
                buffer[idx + 1] = SoftClip(right);
            }
            
            UpdateLoudness(loudnessSum, frameCount * 2);
        }

        /// <summary>
        /// SIMD-optimized stereo processing.
        /// </summary>
        private unsafe void ProcessStereoSse(Span<float> buffer, int frameCount, float volumeStep)
        {
            float volume = linearVolume;
            double loudnessSum = 0;
            int i = 0;
            
            fixed (float* bufPtr = buffer)
            {
                // Process 4 stereo frames at a time (8 samples)
                var ceilingVec = Vector128.Create(peakCeiling);
                var negCeilingVec = Vector128.Create(-peakCeiling);
                
                for (; i <= frameCount - 4; i += 4)
                {
                    // Volume ramp
                    if (i < RampSamples)
                        volume += volumeStep * 4;
                    
                    var volumeVec = Vector128.Create(volume);
                    
                    // Load 8 samples (4 stereo frames)
                    var samples = Sse.LoadVector128(bufPtr + i * 2);
                    var samples2 = Sse.LoadVector128(bufPtr + i * 2 + 4);
                    
                    // Apply volume
                    samples = Sse.Multiply(samples, volumeVec);
                    samples2 = Sse.Multiply(samples2, volumeVec);
                    
                    // Measure loudness (accumulate squared values)
                    var sq1 = Sse.Multiply(samples, samples);
                    var sq2 = Sse.Multiply(samples2, samples2);
                    
                    // Horizontal sum for loudness
                    var sum = Sse.Add(sq1, sq2);
                    if (Sse3.IsSupported)
                    {
                        sum = Sse3.HorizontalAdd(sum, sum);
                        sum = Sse3.HorizontalAdd(sum, sum);
                    }
                    else
                    {
                        sum = Sse.Add(sum, Sse.Shuffle(sum, sum, 0b10_11_00_01));
                        sum = Sse.Add(sum, Sse.Shuffle(sum, sum, 0b00_00_10_10));
                    }
                    loudnessSum += sum.GetElement(0);
                    
                    // Soft clipping (clamp to ceiling)
                    samples = Sse.Max(Sse.Min(samples, ceilingVec), negCeilingVec);
                    samples2 = Sse.Max(Sse.Min(samples2, ceilingVec), negCeilingVec);
                    
                    // Store
                    Sse.Store(bufPtr + i * 2, samples);
                    Sse.Store(bufPtr + i * 2 + 4, samples2);
                }
            }
            
            // Handle remaining frames
            for (; i < frameCount; i++)
            {
                if (i < RampSamples)
                    volume += volumeStep;
                
                int idx = i * 2;
                float left = buffer[idx] * volume;
                float right = buffer[idx + 1] * volume;
                
                loudnessSum += left * left + right * right;
                
                buffer[idx] = SoftClip(left);
                buffer[idx + 1] = SoftClip(right);
            }
            
            UpdateLoudness(loudnessSum, frameCount * 2);
        }

        /// <summary>
        /// Soft clipping using tanh-style curve near ceiling.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float SoftClip(float sample)
        {
            float abs = MathF.Abs(sample);
            
            if (abs <= peakCeiling * 0.8f)
            {
                // Below threshold - pass through
                return sample;
            }
            else if (abs >= peakCeiling)
            {
                // At or above ceiling - hard limit
                return sample >= 0 ? peakCeiling : -peakCeiling;
            }
            else
            {
                // Soft knee region
                float sign = sample >= 0 ? 1.0f : -1.0f;
                float knee = peakCeiling * 0.8f;
                float range = peakCeiling - knee;
                float excess = abs - knee;
                float ratio = excess / (peakCeiling * 0.2f);
                
                // Soft curve: approaches ceiling asymptotically
                float compressed = knee + range * (1.0f - MathF.Exp(-ratio * 2.0f));
                return sign * compressed;
            }
        }

        /// <summary>
        /// Updates the running loudness measurement.
        /// </summary>
        private void UpdateLoudness(double squaredSum, int sampleCount)
        {
            momentaryLoudnessSquaredSum += squaredSum;
            momentaryLoudnessSampleCount += sampleCount;
            
            // Keep window at approximately 400ms
            if (momentaryLoudnessSampleCount > MomentaryWindowSamples)
            {
                // Decay old samples
                double ratio = (double)MomentaryWindowSamples / momentaryLoudnessSampleCount;
                momentaryLoudnessSquaredSum *= ratio;
                momentaryLoudnessSampleCount = MomentaryWindowSamples;
            }
        }

        /// <summary>
        /// Resets the loudness measurement.
        /// </summary>
        public void ResetLoudness()
        {
            momentaryLoudnessSquaredSum = 0;
            momentaryLoudnessSampleCount = 0;
        }

        /// <summary>
        /// Converts a slider position (0-100) to decibels.
        /// </summary>
        public static float SliderToDecibels(float sliderPercent)
        {
            if (sliderPercent <= 0)
                return float.NegativeInfinity;
            
            float linear = MathF.Pow(sliderPercent / 100.0f, 4.0f);
            return 20.0f * MathF.Log10(linear);
        }

        /// <summary>
        /// Converts decibels to linear volume.
        /// </summary>
        public static float DecibelsToLinear(float dB)
        {
            if (float.IsNegativeInfinity(dB))
                return 0.0f;
            return MathF.Pow(10.0f, dB / 20.0f);
        }

        /// <summary>
        /// Converts linear volume to decibels.
        /// </summary>
        public static float LinearToDecibels(float linear)
        {
            if (linear <= 0)
                return float.NegativeInfinity;
            return 20.0f * MathF.Log10(linear);
        }
    }
}

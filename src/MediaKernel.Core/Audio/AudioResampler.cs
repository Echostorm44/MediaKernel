using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio
{
    /// <summary>
    /// Resampling quality levels.
    /// </summary>
    public enum ResamplerQuality
    {
        /// <summary>Fastest - linear interpolation.</summary>
        Fast = 0,
        
        /// <summary>Medium quality - 8-point sinc.</summary>
        Medium = 1,
        
        /// <summary>High quality - 16-point sinc.</summary>
        High = 2,
        
        /// <summary>Best quality - 32-point sinc.</summary>
        Best = 3
    }

    /// <summary>
    /// High-performance audio resampler supporting linear and sinc interpolation.
    /// 
    /// Supports mono and stereo float32 audio.
    /// Uses SIMD acceleration where available.
    /// </summary>
    public sealed class AudioResampler : IDisposable
    {
        // Quality-based sinc filter sizes (taps per side)
        private static readonly int[] FilterSizes = [2, 4, 8, 16];
        
        // Pre-calculated sinc coefficients for different phases
        // Organized as [phase][tap] with 256 phases for sub-sample precision
        private const int PhaseCount = 256;
        
        private readonly float[] sincCoefficients;
        private readonly int filterSize;
        private readonly int channelCount;
        private readonly ResamplerQuality quality;
        
        // Sample rate ratio (output/input)
        private double ratio;
        private int inputRate;
        private int outputRate;
        
        // Fractional sample position (0.0 to 1.0)
        private double fractionalPosition;
        
        // History buffer for sinc interpolation
        private float[] historyBuffer;
        private int historyLength;
        private bool disposed;

        /// <summary>
        /// Creates a new audio resampler.
        /// </summary>
        /// <param name="inputSampleRate">Source sample rate in Hz.</param>
        /// <param name="outputSampleRate">Target sample rate in Hz.</param>
        /// <param name="channels">Number of audio channels (1 or 2).</param>
        /// <param name="quality">Resampling quality.</param>
        public AudioResampler(
            int inputSampleRate,
            int outputSampleRate,
            int channels,
            ResamplerQuality quality = ResamplerQuality.Medium)
        {
            if (inputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
            if (outputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            if (channels < 1 || channels > 2)
                throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo supported");

            inputRate = inputSampleRate;
            outputRate = outputSampleRate;
            ratio = (double)outputSampleRate / inputSampleRate;
            channelCount = channels;
            this.quality = quality;
            
            filterSize = FilterSizes[(int)quality];
            historyLength = filterSize * 2 * channelCount;
            historyBuffer = new float[historyLength];
            
            // Pre-calculate sinc coefficients
            sincCoefficients = GenerateSincCoefficients(filterSize);
        }

        /// <summary>
        /// Gets the latency introduced by resampling in samples.
        /// </summary>
        public int LatencySamples => filterSize;

        /// <summary>
        /// Gets the current sample rate ratio.
        /// </summary>
        public double Ratio => ratio;

        /// <summary>
        /// Sets new sample rates (for variable-rate resampling).
        /// </summary>
        public void SetRates(int inputSampleRate, int outputSampleRate)
        {
            inputRate = inputSampleRate;
            outputRate = outputSampleRate;
            ratio = (double)outputSampleRate / inputSampleRate;
        }

        /// <summary>
        /// Calculates the required output buffer size.
        /// </summary>
        public int CalculateOutputFrames(int inputFrames)
        {
            return (int)Math.Ceiling(inputFrames * ratio) + filterSize;
        }

        /// <summary>
        /// Resamples audio data.
        /// </summary>
        /// <param name="input">Input samples (interleaved if stereo).</param>
        /// <param name="output">Output buffer (must be large enough).</param>
        /// <returns>Number of output frames produced.</returns>
        public int Resample(ReadOnlySpan<float> input, Span<float> output)
        {
            if (quality == ResamplerQuality.Fast)
            {
                return ResampleLinear(input, output);
            }
            else
            {
                return ResampleSinc(input, output);
            }
        }

        /// <summary>
        /// Fast linear interpolation resampling.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int ResampleLinear(ReadOnlySpan<float> input, Span<float> output)
        {
            int inputFrames = input.Length / channelCount;
            int outputFrames = 0;
            int maxOutputFrames = output.Length / channelCount;
            
            double position = fractionalPosition;
            double step = 1.0 / ratio;
            
            if (channelCount == 1)
            {
                // Mono path
                while (position < inputFrames - 1 && outputFrames < maxOutputFrames)
                {
                    int index0 = (int)position;
                    float frac = (float)(position - index0);
                    
                    output[outputFrames] = input[index0] + (input[index0 + 1] - input[index0]) * frac;
                    
                    outputFrames++;
                    position += step;
                }
            }
            else
            {
                // Stereo path
                while (position < inputFrames - 1 && outputFrames < maxOutputFrames)
                {
                    int index0 = (int)position;
                    float frac = (float)(position - index0);
                    int i0 = index0 * 2;
                    int i1 = i0 + 2;
                    int outIdx = outputFrames * 2;
                    
                    // Left channel
                    output[outIdx] = input[i0] + (input[i1] - input[i0]) * frac;
                    // Right channel
                    output[outIdx + 1] = input[i0 + 1] + (input[i1 + 1] - input[i0 + 1]) * frac;
                    
                    outputFrames++;
                    position += step;
                }
            }
            
            // Store fractional position for next call
            fractionalPosition = position - (int)position;
            if (position >= inputFrames)
                fractionalPosition = 0;
            
            return outputFrames;
        }

        /// <summary>
        /// High-quality sinc interpolation resampling.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int ResampleSinc(ReadOnlySpan<float> input, Span<float> output)
        {
            int inputFrames = input.Length / channelCount;
            int outputFrames = 0;
            int maxOutputFrames = output.Length / channelCount;
            
            double position = fractionalPosition;
            double step = 1.0 / ratio;
            int taps = filterSize * 2;
            
            if (channelCount == 1)
            {
                while (position < inputFrames - filterSize && outputFrames < maxOutputFrames)
                {
                    int centerIndex = (int)position;
                    int phase = (int)((position - centerIndex) * PhaseCount);
                    int coeffOffset = phase * taps;
                    
                    float sample = 0;
                    
                    if (Avx.IsSupported && taps >= 8)
                    {
                        sample = ConvolveSincAvx(input, centerIndex - filterSize + 1, sincCoefficients.AsSpan(coeffOffset, taps));
                    }
                    else if (Sse.IsSupported && taps >= 4)
                    {
                        sample = ConvolveSincSse(input, centerIndex - filterSize + 1, sincCoefficients.AsSpan(coeffOffset, taps));
                    }
                    else
                    {
                        sample = ConvolveSincScalar(input, centerIndex - filterSize + 1, sincCoefficients.AsSpan(coeffOffset, taps));
                    }
                    
                    output[outputFrames] = sample;
                    outputFrames++;
                    position += step;
                }
            }
            else
            {
                // Stereo sinc resampling
                while (position < inputFrames - filterSize && outputFrames < maxOutputFrames)
                {
                    int centerIndex = (int)position;
                    int phase = (int)((position - centerIndex) * PhaseCount);
                    int coeffOffset = phase * taps;
                    var coeffs = sincCoefficients.AsSpan(coeffOffset, taps);
                    
                    float leftSample = 0;
                    float rightSample = 0;
                    int startIdx = (centerIndex - filterSize + 1) * 2;
                    
                    for (int t = 0; t < taps; t++)
                    {
                        int idx = startIdx + t * 2;
                        if (idx >= 0 && idx + 1 < input.Length)
                        {
                            leftSample += input[idx] * coeffs[t];
                            rightSample += input[idx + 1] * coeffs[t];
                        }
                    }
                    
                    int outIdx = outputFrames * 2;
                    output[outIdx] = leftSample;
                    output[outIdx + 1] = rightSample;
                    outputFrames++;
                    position += step;
                }
            }
            
            fractionalPosition = position - (int)position;
            if (position >= inputFrames)
                fractionalPosition = 0;
            
            return outputFrames;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ConvolveSincScalar(ReadOnlySpan<float> input, int startIndex, ReadOnlySpan<float> coeffs)
        {
            float sum = 0;
            for (int i = 0; i < coeffs.Length; i++)
            {
                int idx = startIndex + i;
                if (idx >= 0 && idx < input.Length)
                {
                    sum += input[idx] * coeffs[i];
                }
            }
            return sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe float ConvolveSincSse(ReadOnlySpan<float> input, int startIndex, ReadOnlySpan<float> coeffs)
        {
            var sum = Vector128<float>.Zero;
            int i = 0;
            
            fixed (float* inputPtr = input)
            fixed (float* coeffPtr = coeffs)
            {
                int safeStart = Math.Max(0, -startIndex);
                int safeEnd = Math.Min(coeffs.Length, input.Length - startIndex);
                
                // Process 4 samples at a time
                for (i = safeStart; i <= safeEnd - 4; i += 4)
                {
                    var samples = Sse.LoadVector128(inputPtr + startIndex + i);
                    var coeff = Sse.LoadVector128(coeffPtr + i);
                    sum = Sse.Add(sum, Sse.Multiply(samples, coeff));
                }
                
                // Horizontal sum
                sum = Sse3.IsSupported 
                    ? Sse3.HorizontalAdd(sum, sum) 
                    : Sse.Add(sum, Sse.Shuffle(sum, sum, 0b10_11_00_01));
                sum = Sse.Add(sum, Sse.Shuffle(sum, sum, 0b00_00_10_10));
                float result = sum.GetElement(0);
                
                // Handle remaining samples
                for (; i < safeEnd; i++)
                {
                    result += inputPtr[startIndex + i] * coeffPtr[i];
                }
                
                return result;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe float ConvolveSincAvx(ReadOnlySpan<float> input, int startIndex, ReadOnlySpan<float> coeffs)
        {
            var sum = Vector256<float>.Zero;
            int i = 0;
            
            fixed (float* inputPtr = input)
            fixed (float* coeffPtr = coeffs)
            {
                int safeStart = Math.Max(0, -startIndex);
                int safeEnd = Math.Min(coeffs.Length, input.Length - startIndex);
                
                // Process 8 samples at a time
                for (i = safeStart; i <= safeEnd - 8; i += 8)
                {
                    var samples = Avx.LoadVector256(inputPtr + startIndex + i);
                    var coeff = Avx.LoadVector256(coeffPtr + i);
                    sum = Avx.Add(sum, Avx.Multiply(samples, coeff));
                }
                
                // Horizontal sum (256 -> 128 -> scalar)
                var sum128 = Sse.Add(sum.GetLower(), sum.GetUpper());
                sum128 = Sse3.IsSupported 
                    ? Sse3.HorizontalAdd(sum128, sum128) 
                    : Sse.Add(sum128, Sse.Shuffle(sum128, sum128, 0b10_11_00_01));
                sum128 = Sse.Add(sum128, Sse.Shuffle(sum128, sum128, 0b00_00_10_10));
                float result = sum128.GetElement(0);
                
                // Handle remaining samples
                for (; i < safeEnd; i++)
                {
                    result += inputPtr[startIndex + i] * coeffPtr[i];
                }
                
                return result;
            }
        }

        /// <summary>
        /// Generates windowed sinc coefficients for all phases.
        /// </summary>
        private static float[] GenerateSincCoefficients(int filterSize)
        {
            int taps = filterSize * 2;
            float[] coefficients = new float[PhaseCount * taps];
            
            for (int phase = 0; phase < PhaseCount; phase++)
            {
                double phaseOffset = (double)phase / PhaseCount;
                int coeffOffset = phase * taps;
                
                double sum = 0;
                for (int t = 0; t < taps; t++)
                {
                    double x = t - filterSize + 1 - phaseOffset;
                    double sinc = Sinc(x);
                    double window = BlackmanWindow(t, taps);
                    double coeff = sinc * window;
                    coefficients[coeffOffset + t] = (float)coeff;
                    sum += coeff;
                }
                
                // Normalize coefficients
                if (sum > 0.0001)
                {
                    float normFactor = (float)(1.0 / sum);
                    for (int t = 0; t < taps; t++)
                    {
                        coefficients[coeffOffset + t] *= normFactor;
                    }
                }
            }
            
            return coefficients;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 0.0001)
                return 1.0;
            double px = Math.PI * x;
            return Math.Sin(px) / px;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double BlackmanWindow(int n, int length)
        {
            double a0 = 0.42;
            double a1 = 0.5;
            double a2 = 0.08;
            double phase = 2.0 * Math.PI * n / (length - 1);
            return a0 - a1 * Math.Cos(phase) + a2 * Math.Cos(2.0 * phase);
        }

        /// <summary>
        /// Resets the resampler state.
        /// </summary>
        public void Reset()
        {
            fractionalPosition = 0;
            Array.Clear(historyBuffer);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
        }
    }
}

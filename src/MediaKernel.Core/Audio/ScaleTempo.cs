// ScaleTempo audio filter - Variable speed playback with pitch preservation
// Reference: VLC modules/audio_filter/scaletempo.c
// Uses WSOLA (Waveform Similarity Overlap-Add) algorithm

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Configuration for ScaleTempo filter.
/// </summary>
public readonly struct ScaleTempoConfig
{
    /// <summary>Length in milliseconds to output each stride (default: 30ms).</summary>
    public readonly int StrideMilliseconds;

    /// <summary>Percentage of stride to overlap (0.0-1.0, default: 0.20).</summary>
    public readonly float OverlapPercent;

    /// <summary>Length in milliseconds to search for best overlap position (default: 14ms).</summary>
    public readonly int SearchMilliseconds;

    /// <summary>Default configuration matching VLC defaults.</summary>
    public static readonly ScaleTempoConfig Default = new(30, 0.20f, 14);

    public ScaleTempoConfig(int strideMilliseconds, float overlapPercent, int searchMilliseconds)
    {
        StrideMilliseconds = Math.Clamp(strideMilliseconds, 1, 2000);
        OverlapPercent = Math.Clamp(overlapPercent, 0.0f, 1.0f);
        SearchMilliseconds = Math.Clamp(searchMilliseconds, 0, 200);
    }
}

/// <summary>
/// Audio time-stretching filter that changes playback speed without affecting pitch.
/// Uses WSOLA (Waveform Similarity Overlap-Add) algorithm.
/// </summary>
/// <remarks>
/// ScaleTempo works by:
/// 1. Producing audio in constant-sized chunks (strides)
/// 2. Consuming chunks proportional to the playback rate
/// 3. Blending the end of one stride with the next (overlap)
/// 4. Searching for the best overlap position using cross-correlation
/// 
/// This allows variable speed playback (0.5x to 2.0x typically) without
/// the "chipmunk" or "slowed down" voice effect.
/// </remarks>
public sealed class ScaleTempo : IDisposable
{
    private readonly ScaleTempoConfig config;
    private readonly int channels;
    private readonly int sampleRate;
    private readonly int bytesPerSample;
    private readonly int bytesPerFrame;

    private float scale;

    // Stride parameters
    private int framesStride;
    private int bytesStride;
    private double framesStrideScaled;
    private double framesStrideError;

    // Overlap parameters
    private int framesOverlap;
    private int samplesOverlap;
    private int bytesOverlap;
    private int bytesStanding;
    private float[] overlapBuffer;
    private float[] blendTable;

    // Search/correlation parameters
    private int framesSearch;
    private float[] preCorrBuffer;
    private float[] windowTable;

    // Queue buffer
    private float[] queueBuffer;
    private int bytesQueueMax;
    private int samplesQueued;
    private int samplesToSlide;

    private bool isInitialized;
    private bool isDisposed;

    /// <summary>Gets the current playback scale (1.0 = normal speed).</summary>
    public float Scale => scale;

    /// <summary>Gets the number of audio channels.</summary>
    public int Channels => channels;

    /// <summary>Gets the sample rate in Hz.</summary>
    public int SampleRate => sampleRate;

    /// <summary>
    /// Creates a new ScaleTempo filter.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="config">Optional configuration (uses defaults if null).</param>
    public ScaleTempo(int sampleRate, int channels, ScaleTempoConfig? config = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0 || channels > 8)
            throw new ArgumentOutOfRangeException(nameof(channels));

        this.sampleRate = sampleRate;
        this.channels = channels;
        this.config = config ?? ScaleTempoConfig.Default;
        this.bytesPerSample = sizeof(float);
        this.bytesPerFrame = channels * bytesPerSample;
        this.scale = 1.0f;

        overlapBuffer = [];
        blendTable = [];
        preCorrBuffer = [];
        windowTable = [];
        queueBuffer = [];

        InitializeBuffers();
    }

    /// <summary>
    /// Sets the playback scale (tempo multiplier).
    /// </summary>
    /// <param name="newScale">Scale factor (e.g., 1.0 = normal, 2.0 = 2x speed, 0.5 = half speed).</param>
    public void SetScale(float newScale)
    {
        if (newScale <= 0.0f || newScale > 10.0f)
            throw new ArgumentOutOfRangeException(nameof(newScale), "Scale must be between 0.0 (exclusive) and 10.0");

        scale = newScale;
        framesStrideScaled = framesStride * scale;
        framesStrideError = 0.0;
    }

    /// <summary>
    /// Processes input audio and produces time-stretched output.
    /// </summary>
    /// <param name="input">Input audio samples (interleaved float, -1.0 to 1.0).</param>
    /// <param name="output">Output buffer for processed audio.</param>
    /// <returns>Number of samples written to output (per channel).</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (!isInitialized)
            return 0;

        int inputFrames = input.Length / channels;
        int inputOffset = 0;
        int outputOffset = 0;
        int maxOutputFrames = output.Length / channels;

        while (inputOffset < inputFrames || samplesQueued >= (bytesQueueMax / bytesPerFrame))
        {
            // Fill the queue buffer
            int framesConsumed = FillQueue(input.Slice(inputOffset * channels), inputOffset);
            inputOffset += framesConsumed;

            // Check if we have enough to produce output
            if (samplesQueued >= (bytesQueueMax / bytesPerFrame))
            {
                // Check output space
                int framesPerStride = bytesStride / bytesPerFrame;
                if (outputOffset + framesPerStride > maxOutputFrames)
                    break;

                // Transform and output one stride
                int framesOut = TransformBuffer(output.Slice(outputOffset * channels));
                outputOffset += framesOut;
            }
            else
            {
                break;
            }
        }

        return outputOffset;
    }

    /// <summary>
    /// Resets the filter state (call after seeking).
    /// </summary>
    public void Reset()
    {
        Array.Clear(queueBuffer);
        Array.Clear(overlapBuffer);
        samplesQueued = 0;
        samplesToSlide = 0;
        framesStrideError = 0.0;
    }

    /// <summary>
    /// Calculates expected output size for given input size and current scale.
    /// </summary>
    public int CalculateOutputSize(int inputSamples)
    {
        if (scale == 1.0f)
            return inputSamples;

        // Output size is approximately input / scale
        return (int)Math.Ceiling(inputSamples / scale) + (framesStride * channels * 2);
    }

    private void InitializeBuffers()
    {
        // Stride parameters
        framesStride = (int)(config.StrideMilliseconds * sampleRate / 1000.0);
        bytesStride = framesStride * bytesPerFrame;
        framesStrideScaled = framesStride * scale;

        // Overlap parameters
        framesOverlap = (int)(framesStride * config.OverlapPercent);

        if (framesOverlap < 1)
        {
            // No overlap
            bytesOverlap = 0;
            bytesStanding = bytesStride;
            samplesOverlap = 0;
            blendTable = [];
            overlapBuffer = [];
        }
        else
        {
            bytesOverlap = framesOverlap * bytesPerFrame;
            samplesOverlap = framesOverlap * channels;
            bytesStanding = bytesStride - bytesOverlap;

            overlapBuffer = new float[samplesOverlap];
            blendTable = new float[samplesOverlap];

            // Create blend table (linear blend)
            float t = framesOverlap;
            for (int i = 0; i < framesOverlap; i++)
            {
                float v = i / t;
                for (int j = 0; j < channels; j++)
                {
                    blendTable[i * channels + j] = v;
                }
            }
        }

        // Search/correlation parameters
        framesSearch = framesOverlap <= 1 ? 0 : (int)(config.SearchMilliseconds * sampleRate / 1000.0);

        if (framesSearch < 1)
        {
            preCorrBuffer = [];
            windowTable = [];
        }
        else
        {
            int preCorrSamples = samplesOverlap - channels;
            preCorrBuffer = new float[preCorrSamples];
            windowTable = new float[preCorrSamples];

            // Create window table (Hann-like window for correlation)
            for (int i = 1; i < framesOverlap; i++)
            {
                float v = i * (framesOverlap - i);
                for (int j = 0; j < channels; j++)
                {
                    windowTable[(i - 1) * channels + j] = v;
                }
            }
        }

        // Queue buffer (holds input samples waiting to be processed)
        // Need enough for stride + search + overlap
        int queueFrames = framesStride + framesSearch + framesOverlap;
        bytesQueueMax = queueFrames * bytesPerFrame;
        queueBuffer = new float[queueFrames * channels];

        isInitialized = true;
    }

    private int FillQueue(ReadOnlySpan<float> input, int inputOffset)
    {
        int inputSamples = input.Length;
        int framesAvailable = inputSamples / channels;
        int framesConsumed = 0;

        // Handle sliding (skip samples when speeding up)
        if (samplesToSlide > 0)
        {
            if (samplesToSlide < samplesQueued)
            {
                // Slide within queue
                int samplesToMove = samplesQueued - samplesToSlide;
                Array.Copy(queueBuffer, samplesToSlide, queueBuffer, 0, samplesToMove);
                samplesToSlide = 0;
                samplesQueued = samplesToMove;
            }
            else
            {
                // Skip from input
                int framesToSkip = Math.Min(samplesToSlide / channels, framesAvailable);
                samplesToSlide -= framesToSkip * channels;
                samplesQueued = 0;
                framesConsumed = framesToSkip;
                framesAvailable -= framesToSkip;
            }
        }

        // Copy remaining input to queue
        if (framesAvailable > 0)
        {
            int queueCapacity = (bytesQueueMax / bytesPerFrame) - (samplesQueued / channels);
            int framesToCopy = Math.Min(queueCapacity, framesAvailable);

            if (framesToCopy > 0)
            {
                input.Slice(framesConsumed * channels, framesToCopy * channels)
                     .CopyTo(queueBuffer.AsSpan(samplesQueued));
                samplesQueued += framesToCopy * channels;
                framesConsumed += framesToCopy;
            }
        }

        return framesConsumed;
    }

    private int TransformBuffer(Span<float> output)
    {
        int bestOffset = 0;

        // Find best overlap position using cross-correlation
        if (framesSearch > 0 && samplesOverlap > channels)
        {
            bestOffset = FindBestOverlapOffset();
        }

        // Output with overlap blending
        if (samplesOverlap > 0)
        {
            OutputOverlap(output, bestOffset);
        }

        // Copy standing portion (non-overlapped part)
        int standingSamples = bytesStanding / bytesPerSample;
        int sourceOffset = bestOffset + samplesOverlap;

        queueBuffer.AsSpan(sourceOffset, standingSamples)
                   .CopyTo(output.Slice(samplesOverlap));

        // Prepare for next stride: copy overlap region for next iteration
        int nextOverlapOffset = bestOffset + (bytesStride / bytesPerSample);
        queueBuffer.AsSpan(nextOverlapOffset, samplesOverlap)
                   .CopyTo(overlapBuffer);

        // Calculate how much to slide for next stride
        double framesToSlide = framesStrideScaled + framesStrideError;
        int framesToSlideWhole = (int)framesToSlide;
        samplesToSlide = framesToSlideWhole * channels;
        framesStrideError = framesToSlide - framesToSlideWhole;

        return bytesStride / bytesPerFrame;
    }

    private int FindBestOverlapOffset()
    {
        // Pre-compute windowed overlap buffer for correlation
        int preCorrLength = samplesOverlap - channels;

        for (int i = 0; i < preCorrLength; i++)
        {
            preCorrBuffer[i] = windowTable[i] * overlapBuffer[i + channels];
        }

        // Search for best correlation
        float bestCorr = float.MinValue;
        int bestOffset = 0;

        for (int off = 0; off < framesSearch; off++)
        {
            float corr = 0;
            int searchStart = (off + 1) * channels; // Skip first frame

            for (int i = 0; i < preCorrLength; i++)
            {
                corr += preCorrBuffer[i] * queueBuffer[searchStart + i];
            }

            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestOffset = off;
            }
        }

        return bestOffset * channels;
    }

    private void OutputOverlap(Span<float> output, int queueOffset)
    {
        // Blend previous overlap buffer with current queue position
        for (int i = 0; i < samplesOverlap; i++)
        {
            float blend = blendTable[i];
            float prev = overlapBuffer[i];
            float curr = queueBuffer[queueOffset + i];
            output[i] = prev + blend * (curr - prev);
        }
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            queueBuffer = [];
            overlapBuffer = [];
            blendTable = [];
            preCorrBuffer = [];
            windowTable = [];
        }
    }
}

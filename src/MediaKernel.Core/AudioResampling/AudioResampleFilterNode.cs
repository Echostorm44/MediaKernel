// Audio resample filter node for the pipeline filter graph.
// Wraps AudioResampler to convert sample rate, format, and channel layout
// between pipeline stages.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using MediaKernel.Core.Audio;
using MediaKernel.Core.Filters;

namespace MediaKernel.Core.AudioResampling;

/// <summary>
/// Pipeline filter node that converts audio between formats, sample rates,
/// and channel layouts. Lazily creates the resampler on first frame.
/// </summary>
public sealed class AudioResampleFilterNode : IAudioFilterNode
{
    private readonly int targetSampleRate;
    private readonly int targetChannels;
    private readonly AudioChannelLayout targetLayout;
    private readonly ResampleQuality quality;

    private AudioResampler? resampler;
    private float[]? outputBuffer;

    public string Name { get; }

    /// <summary>
    /// Create an audio resample filter node.
    /// </summary>
    /// <param name="targetSampleRate">Desired output sample rate (Hz).</param>
    /// <param name="targetLayout">Desired output channel layout.</param>
    /// <param name="quality">Resampling quality (affects filter length and CPU cost).</param>
    public AudioResampleFilterNode(
        int targetSampleRate,
        AudioChannelLayout targetLayout = AudioChannelLayout.Stereo,
        ResampleQuality quality = ResampleQuality.High)
    {
        this.targetSampleRate = targetSampleRate;
        this.targetChannels = (int)targetLayout;
        this.targetLayout = targetLayout;
        this.quality = quality;
        Name = $"aresample={targetSampleRate}";
    }

    public FilterMediaFormat[] GetSupportedInputFormats()
    {
        // Accept any audio format — the resampler handles conversion internally
        return
        [
            FilterMediaFormat.Audio(44100, 1),
            FilterMediaFormat.Audio(44100, 2),
            FilterMediaFormat.Audio(48000, 1),
            FilterMediaFormat.Audio(48000, 2),
            FilterMediaFormat.Audio(96000, 2),
        ];
    }

    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat)
    {
        return FilterMediaFormat.Audio(targetSampleRate, targetChannels);
    }

    public AudioFrame Process(AudioFrame frame)
    {
        bool rateMatch = frame.SampleRate == targetSampleRate;
        bool channelMatch = frame.Channels == targetChannels;

        // Pass-through when no conversion needed
        if (rateMatch && channelMatch)
            return frame;

        // Lazy-init resampler on first frame (we don't know source format until then)
        if (resampler == null)
        {
            var srcLayout = frame.Channels switch
            {
                1 => AudioChannelLayout.Mono,
                2 => AudioChannelLayout.Stereo,
                6 => AudioChannelLayout.Surround51,
                8 => AudioChannelLayout.Surround71,
                _ => AudioChannelLayout.Stereo,
            };

            resampler = new AudioResampler(
                frame.SampleRate, AudioSampleFormat.Float32, srcLayout,
                targetSampleRate, AudioSampleFormat.Float32, targetLayout,
                quality);
        }

        // Estimate output size
        int estimatedOutputFrames = resampler.EstimateOutputFrames(frame.SampleCount);
        int totalOutputSamples = estimatedOutputFrames * targetChannels;

        // Ensure output buffer is large enough
        if (outputBuffer == null || outputBuffer.Length < totalOutputSamples)
        {
            if (outputBuffer != null) ArrayPool<float>.Shared.Return(outputBuffer);
            outputBuffer = ArrayPool<float>.Shared.Rent(totalOutputSamples);
        }

        // Convert
        var inputSpan = frame.Samples.Span;
        int outputFrames = resampler.Convert(
            inputSpan, frame.SampleCount,
            outputBuffer.AsSpan(0, totalOutputSamples), estimatedOutputFrames);

        if (outputFrames <= 0)
        {
            // Resampler buffered but didn't produce output yet
            return new AudioFrame
            {
                Samples = Memory<float>.Empty,
                SampleCount = 0,
                Channels = targetChannels,
                SampleRate = targetSampleRate,
                Pts = frame.Pts,
            };
        }

        // Create output frame (copies into pooled memory — the node's outputBuffer is reused next call)
        return AudioFrame.Create(
            outputBuffer.AsSpan(0, outputFrames * targetChannels),
            outputFrames,
            targetChannels,
            targetSampleRate,
            frame.Pts);
    }

    public void Dispose()
    {
        resampler?.Dispose();
        resampler = null;
        if (outputBuffer != null)
        {
            ArrayPool<float>.Shared.Return(outputBuffer);
            outputBuffer = null;
        }
    }
}

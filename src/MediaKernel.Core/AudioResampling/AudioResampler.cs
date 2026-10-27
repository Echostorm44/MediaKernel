// Unified audio resampling context — SwrContext equivalent.
// Combines sample format conversion, sample rate conversion, and channel remapping
// into a single Convert() call. Internal pipeline:
//
//   Input → [Format Convert] → [Rematrix/Channel Map] → [Resample] → [Format Convert] → Output
//
// Internal processing format is always Float32 Planar.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MediaKernel.Core.Audio;

namespace MediaKernel.Core.AudioResampling;

/// <summary>
/// Unified audio conversion context. Handles sample format conversion,
/// sample rate conversion (sinc resampling), and channel layout remapping.
/// Thread-safe for separate instances; NOT thread-safe for concurrent use of one instance.
/// </summary>
public sealed class AudioResampler : IDisposable
{
    private readonly AudioFormat sourceFormat;
    private readonly AudioFormat destinationFormat;
    private readonly bool needsFormatConversion;
    private readonly bool needsResample;
    private readonly bool needsChannelMap;

    private readonly SincResampler[]? perChannelResamplers;
    private readonly ChannelMapper? channelMapper;

    // Pooled intermediate buffers (reused across Convert calls)
    private float[]? inputFloatBuffer;
    private float[]? resampledBuffer;
    private float[]? outputFloatBuffer;

    // Working channel count (after input conversion, before output conversion)
    private readonly int internalChannels;

    private bool disposed;

    /// <summary>Source audio format.</summary>
    public AudioFormat SourceFormat => sourceFormat;

    /// <summary>Destination audio format.</summary>
    public AudioFormat DestinationFormat => destinationFormat;

    /// <summary>Resampling quality setting.</summary>
    public ResampleQuality Quality { get; }

    /// <summary>
    /// Create a resampler context for the specified format pair.
    /// </summary>
    public AudioResampler(
        int srcRate, AudioSampleFormat srcSampleFormat, AudioChannelLayout srcLayout,
        int dstRate, AudioSampleFormat dstSampleFormat, AudioChannelLayout dstLayout,
        ResampleQuality quality = ResampleQuality.High)
    {
        int srcChannels = (int)srcLayout;
        int dstChannels = (int)dstLayout;
        sourceFormat = new AudioFormat(srcRate, srcChannels, srcSampleFormat, srcLayout);
        destinationFormat = new AudioFormat(dstRate, dstChannels, dstSampleFormat, dstLayout);
        Quality = quality;

        needsFormatConversion = srcSampleFormat != dstSampleFormat
            || srcSampleFormat != AudioSampleFormat.Float32;
        needsResample = srcRate != dstRate;
        needsChannelMap = srcLayout != dstLayout;

        // Pipeline order: format → channel map → resample → format
        // Channel mapping happens before resampling so we resample fewer channels if downmixing.
        // (Matches FFmpeg's resample_first=0 path for channel reduction.)
        if (needsChannelMap)
        {
            channelMapper = new ChannelMapper(srcLayout, dstLayout);
            internalChannels = dstChannels;
        }
        else
        {
            internalChannels = srcChannels;
        }

        // Create one sinc resampler per channel
        if (needsResample)
        {
            perChannelResamplers = new SincResampler[internalChannels];
            for (int ch = 0; ch < internalChannels; ch++)
                perChannelResamplers[ch] = new SincResampler(srcRate, dstRate, quality);
        }
    }

    /// <summary>
    /// Convenience constructor using AudioFormat structs.
    /// </summary>
    public AudioResampler(AudioFormat src, AudioFormat dst, ResampleQuality quality = ResampleQuality.High)
        : this(src.SampleRate, src.SampleFormat, src.ChannelLayout,
               dst.SampleRate, dst.SampleFormat, dst.ChannelLayout, quality)
    {
    }

    /// <summary>
    /// Convert input audio to the destination format.
    /// Input is interleaved samples in the source format.
    /// Output is interleaved samples in the destination format.
    /// Returns the number of output frames (samples per channel) written.
    /// </summary>
    /// <param name="input">Input audio bytes (interleaved in source format).</param>
    /// <param name="inputFrameCount">Number of input frames (samples per channel).</param>
    /// <param name="output">Output audio bytes (interleaved in destination format).</param>
    /// <param name="maxOutputFrames">Maximum output frames the buffer can hold.</param>
    /// <returns>Number of output frames written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Convert(ReadOnlySpan<byte> input, int inputFrameCount,
                       Span<byte> output, int maxOutputFrames)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AudioResampler));

        int srcChannels = sourceFormat.ChannelCount;
        int dstChannels = destinationFormat.ChannelCount;
        int inputSampleCount = inputFrameCount * srcChannels;

        // ── Step 1: Convert input to Float32 ─────────────────────
        EnsureBuffer(ref inputFloatBuffer, inputSampleCount);
        var inputFloat = inputFloatBuffer.AsSpan(0, inputSampleCount);

        if (sourceFormat.SampleFormat is AudioSampleFormat.Float32)
            MemoryMarshal.Cast<byte, float>(input).Slice(0, inputSampleCount).CopyTo(inputFloat);
        else
            SampleConverter.ConvertToFloat32(input, inputFloat, sourceFormat.SampleFormat, inputSampleCount);

        // ── Step 2: Channel remapping (if needed) ────────────────
        Span<float> remappedFloat;
        int remappedFrameCount = inputFrameCount;

        if (needsChannelMap && channelMapper != null)
        {
            int remappedSampleCount = inputFrameCount * dstChannels;
            float[] remappedBuffer = ArrayPool<float>.Shared.Rent(remappedSampleCount);
            var remappedSpan = remappedBuffer.AsSpan(0, remappedSampleCount);

            channelMapper.RemapInterleaved(inputFloat, remappedSpan, inputFrameCount);
            remappedFloat = remappedSpan;
            // Note: remappedBuffer returned after use below
            EnsureBuffer(ref this.resampledBuffer, remappedSampleCount);
            remappedFloat.CopyTo(this.resampledBuffer.AsSpan(0, remappedSampleCount));
            ArrayPool<float>.Shared.Return(remappedBuffer);
            remappedFloat = this.resampledBuffer.AsSpan(0, remappedSampleCount);
        }
        else
        {
            remappedFloat = inputFloat;
        }

        // ── Step 3: Resample (if needed) ─────────────────────────
        Span<float> finalFloat;
        int outputFrameCount;

        if (needsResample && perChannelResamplers != null)
        {
            int estimatedOutputFrames = perChannelResamplers[0].EstimateOutputSamples(remappedFrameCount);
            estimatedOutputFrames = Math.Min(estimatedOutputFrames, maxOutputFrames);

            int totalOutputSamples = estimatedOutputFrames * internalChannels;
            EnsureBuffer(ref outputFloatBuffer, totalOutputSamples);

            // Deinterleave → resample per channel → interleave
            outputFrameCount = ResampleAllChannels(
                remappedFloat, remappedFrameCount,
                outputFloatBuffer.AsSpan(0, totalOutputSamples),
                estimatedOutputFrames, internalChannels);

            finalFloat = outputFloatBuffer.AsSpan(0, outputFrameCount * internalChannels);
        }
        else
        {
            finalFloat = remappedFloat;
            outputFrameCount = remappedFrameCount;
        }

        outputFrameCount = Math.Min(outputFrameCount, maxOutputFrames);

        // ── Step 4: Convert to destination format ────────────────
        int outputSampleCount = outputFrameCount * dstChannels;

        if (destinationFormat.SampleFormat is AudioSampleFormat.Float32)
        {
            finalFloat.Slice(0, outputSampleCount).CopyTo(MemoryMarshal.Cast<byte, float>(output));
        }
        else
        {
            SampleConverter.ConvertFromFloat32(
                finalFloat.Slice(0, outputSampleCount), output,
                destinationFormat.SampleFormat, outputSampleCount);
        }

        return outputFrameCount;
    }

    /// <summary>
    /// Convenience overload: Convert Float32 interleaved to Float32 interleaved.
    /// </summary>
    public int Convert(ReadOnlySpan<float> input, int inputFrameCount,
                       Span<float> output, int maxOutputFrames)
    {
        var inBytes = MemoryMarshal.AsBytes(input);
        var outBytes = MemoryMarshal.AsBytes(output);
        return Convert(inBytes, inputFrameCount, outBytes, maxOutputFrames);
    }

    /// <summary>
    /// Flush remaining samples from the resampler delay line.
    /// Call once at end-of-stream.
    /// </summary>
    public int Flush(Span<byte> output, int maxOutputFrames)
    {
        if (disposed || !needsResample || perChannelResamplers == null)
            return 0;

        int totalOutputSamples = maxOutputFrames * internalChannels;
        EnsureBuffer(ref outputFloatBuffer, totalOutputSamples);

        int outputFrameCount = FlushAllChannels(
            outputFloatBuffer.AsSpan(0, totalOutputSamples),
            maxOutputFrames, internalChannels);

        if (outputFrameCount == 0) return 0;

        int dstChannels = destinationFormat.ChannelCount;
        int outputSampleCount = outputFrameCount * dstChannels;

        if (destinationFormat.SampleFormat is AudioSampleFormat.Float32)
        {
            outputFloatBuffer.AsSpan(0, outputSampleCount)
                .CopyTo(MemoryMarshal.Cast<byte, float>(output));
        }
        else
        {
            SampleConverter.ConvertFromFloat32(
                outputFloatBuffer.AsSpan(0, outputSampleCount), output,
                destinationFormat.SampleFormat, outputSampleCount);
        }

        return outputFrameCount;
    }

    /// <summary>Reset internal state for reuse with a new stream.</summary>
    public void Reset()
    {
        if (perChannelResamplers != null)
        {
            foreach (var r in perChannelResamplers)
                r.Reset();
        }
    }

    /// <summary>
    /// Estimate output frame count for a given input frame count.
    /// </summary>
    public int EstimateOutputFrames(int inputFrames)
    {
        if (!needsResample || perChannelResamplers == null)
            return inputFrames;
        return perChannelResamplers[0].EstimateOutputSamples(inputFrames);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (perChannelResamplers != null)
            {
                foreach (var r in perChannelResamplers)
                    r.Dispose();
            }
            ReturnBuffer(ref inputFloatBuffer);
            ReturnBuffer(ref resampledBuffer);
            ReturnBuffer(ref outputFloatBuffer);
        }
    }

    // ── Internal helpers ─────────────────────────────────────────

    private int ResampleAllChannels(
        ReadOnlySpan<float> interleavedInput, int inputFrames,
        Span<float> interleavedOutput, int maxOutputFrames,
        int channels)
    {
        // Deinterleave into temporary per-channel buffers
        float[][] inChBuffers = new float[channels][];
        float[][] outChBuffers = new float[channels][];

        for (int ch = 0; ch < channels; ch++)
        {
            inChBuffers[ch] = ArrayPool<float>.Shared.Rent(inputFrames);
            outChBuffers[ch] = ArrayPool<float>.Shared.Rent(maxOutputFrames);
        }

        SampleConverter.Deinterleave(interleavedInput, inChBuffers, inputFrames);

        // Resample each channel independently
        int outputFrames = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int produced = perChannelResamplers![ch].Resample(
                inChBuffers[ch].AsSpan(0, inputFrames),
                outChBuffers[ch].AsSpan(0, maxOutputFrames));
            if (ch == 0) outputFrames = produced;
            else outputFrames = Math.Min(outputFrames, produced);
        }

        // Interleave output
        if (outputFrames > 0)
            SampleConverter.Interleave(outChBuffers, interleavedOutput, outputFrames);

        // Return pooled buffers
        for (int ch = 0; ch < channels; ch++)
        {
            ArrayPool<float>.Shared.Return(inChBuffers[ch]);
            ArrayPool<float>.Shared.Return(outChBuffers[ch]);
        }

        return outputFrames;
    }

    private int FlushAllChannels(Span<float> interleavedOutput, int maxOutputFrames, int channels)
    {
        float[][] outChBuffers = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
            outChBuffers[ch] = ArrayPool<float>.Shared.Rent(maxOutputFrames);

        int outputFrames = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int produced = perChannelResamplers![ch].Flush(outChBuffers[ch].AsSpan(0, maxOutputFrames));
            if (ch == 0) outputFrames = produced;
            else outputFrames = Math.Min(outputFrames, produced);
        }

        if (outputFrames > 0)
            SampleConverter.Interleave(outChBuffers, interleavedOutput, outputFrames);

        for (int ch = 0; ch < channels; ch++)
            ArrayPool<float>.Shared.Return(outChBuffers[ch]);

        return outputFrames;
    }

    private static void EnsureBuffer(ref float[]? buffer, int needed)
    {
        if (buffer != null && buffer.Length >= needed) return;
        if (buffer != null) ArrayPool<float>.Shared.Return(buffer);
        buffer = ArrayPool<float>.Shared.Rent(needed);
    }

    private static void ReturnBuffer(ref float[]? buffer)
    {
        if (buffer != null)
        {
            ArrayPool<float>.Shared.Return(buffer);
            buffer = null;
        }
    }
}

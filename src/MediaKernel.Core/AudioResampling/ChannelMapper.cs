// Channel layout remapping: downmix, upmix, and custom per-channel mixing.
// Uses ITU-R BS.775 standard coefficients for 5.1→stereo, 7.1→5.1, etc.
// Wraps the mixing logic (the existing ChannelDownmixer handles 5.1→stereo SIMD).

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Audio;

namespace MediaKernel.Core.AudioResampling;

/// <summary>
/// Standard channel positions for layout mapping.
/// Indices into the mixing matrix.
/// </summary>
public enum ChannelPosition
{
    FrontLeft = 0,
    FrontRight = 1,
    FrontCenter = 2,
    Lfe = 3,
    BackLeft = 4,
    BackRight = 5,
    SideLeft = 6,
    SideRight = 7,
}

/// <summary>
/// Maps audio between different channel layouts using a mixing coefficient matrix.
/// Supports all standard layouts (Mono, Stereo, 5.1, 7.1) and custom matrices.
/// Operates on planar Float32 data.
/// </summary>
public sealed class ChannelMapper
{
    private readonly float[,] mixMatrix; // [outChannel, inChannel]
    private readonly int inputChannels;
    private readonly int outputChannels;

    /// <summary>Number of input channels.</summary>
    public int InputChannels => inputChannels;

    /// <summary>Number of output channels.</summary>
    public int OutputChannels => outputChannels;

    /// <summary>
    /// Create a channel mapper for the given layout pair.
    /// Uses ITU-R BS.775 standard coefficients.
    /// </summary>
    public ChannelMapper(AudioChannelLayout sourceLayout, AudioChannelLayout destLayout)
    {
        inputChannels = (int)sourceLayout;
        outputChannels = (int)destLayout;
        mixMatrix = BuildStandardMatrix(sourceLayout, destLayout);
    }

    /// <summary>
    /// Create a channel mapper with a custom mixing matrix.
    /// <paramref name="matrix"/> is [outputChannels, inputChannels].
    /// </summary>
    public ChannelMapper(float[,] matrix)
    {
        outputChannels = matrix.GetLength(0);
        inputChannels = matrix.GetLength(1);
        mixMatrix = (float[,])matrix.Clone();
    }

    /// <summary>
    /// Remap samples from input planes to output planes.
    /// Each plane is one channel of Float32 samples.
    /// </summary>
    /// <param name="inputPlanes">Input: one array per input channel.</param>
    /// <param name="outputPlanes">Output: one array per output channel (will be overwritten).</param>
    /// <param name="frameCount">Number of samples per channel.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Remap(float[][] inputPlanes, float[][] outputPlanes, int frameCount)
    {
        // Clear output first
        for (int ch = 0; ch < outputChannels; ch++)
            outputPlanes[ch].AsSpan(0, frameCount).Clear();

        // Matrix multiply: out[ch] += mixMatrix[ch, inCh] * in[inCh]
        for (int outCh = 0; outCh < outputChannels; outCh++)
        {
            var outArr = outputPlanes[outCh];
            for (int inCh = 0; inCh < inputChannels; inCh++)
            {
                float coeff = mixMatrix[outCh, inCh];
                if (coeff == 0f) continue;

                var inArr = inputPlanes[inCh];
                if (coeff == 1f)
                {
                    for (int i = 0; i < frameCount; i++)
                        outArr[i] += inArr[i];
                }
                else
                {
                    for (int i = 0; i < frameCount; i++)
                        outArr[i] += inArr[i] * coeff;
                }
            }
        }
    }

    /// <summary>
    /// Convenience: remap interleaved samples (deinterleave → remap → interleave).
    /// Less efficient than planar, but simpler API.
    /// </summary>
    public void RemapInterleaved(ReadOnlySpan<float> input, Span<float> output, int frameCount)
    {
        // Deinterleave input
        var inArrays = new float[inputChannels][];
        for (int ch = 0; ch < inputChannels; ch++)
            inArrays[ch] = new float[frameCount];
        SampleConverter.Deinterleave(input, inArrays, frameCount);

        // Remap
        var outArrays = new float[outputChannels][];
        for (int ch = 0; ch < outputChannels; ch++)
            outArrays[ch] = new float[frameCount];

        Remap(inArrays, outArrays, frameCount);

        // Interleave output
        SampleConverter.Interleave(outArrays, output, frameCount);
    }

    // ── Standard mixing matrices ─────────────────────────────────

    // ITU-R BS.775 standard coefficients
    private const float CenterMix = 0.7071068f;   // 1/√2 ≈ -3dB
    private const float SurroundMix = 0.7071068f;  // 1/√2 ≈ -3dB
    private const float LfeMix = 0f;               // LFE typically excluded from stereo downmix

    private static float[,] BuildStandardMatrix(AudioChannelLayout src, AudioChannelLayout dst)
    {
        // Identity
        if (src == dst)
            return BuildIdentityMatrix((int)src);

        return (src, dst) switch
        {
            // Stereo → Mono
            (AudioChannelLayout.Stereo, AudioChannelLayout.Mono) =>
                new float[,] { { 0.5f, 0.5f } },

            // Mono → Stereo
            (AudioChannelLayout.Mono, AudioChannelLayout.Stereo) =>
                new float[,] { { 1f }, { 1f } },

            // 5.1 → Stereo (ITU-R BS.775)
            // L = FL + center×FC + surround×BL
            // R = FR + center×FC + surround×BR
            (AudioChannelLayout.Surround51, AudioChannelLayout.Stereo) =>
                new float[,]
                {
                    //  FL     FR      FC          LFE     BL           BR
                    { 1f,    0f,   CenterMix,   LfeMix,  SurroundMix, 0f },         // L
                    { 0f,    1f,   CenterMix,   LfeMix,  0f,          SurroundMix }, // R
                },

            // 5.1 → Mono
            (AudioChannelLayout.Surround51, AudioChannelLayout.Mono) =>
                new float[,]
                {
                    { 0.5f, 0.5f, CenterMix * 0.5f, LfeMix, SurroundMix * 0.5f, SurroundMix * 0.5f }
                },

            // 7.1 → Stereo
            // L = FL + center×FC + surround×BL + surround×SL
            // R = FR + center×FC + surround×BR + surround×SR
            (AudioChannelLayout.Surround71, AudioChannelLayout.Stereo) =>
                new float[,]
                {
                    //  FL     FR      FC          LFE     BL           BR           SL           SR
                    { 1f,    0f,   CenterMix,   LfeMix,  SurroundMix, 0f,          SurroundMix, 0f },
                    { 0f,    1f,   CenterMix,   LfeMix,  0f,          SurroundMix, 0f,          SurroundMix },
                },

            // 7.1 → 5.1 (fold side into back)
            (AudioChannelLayout.Surround71, AudioChannelLayout.Surround51) =>
                new float[,]
                {
                    //  FL    FR    FC    LFE   BL          BR          SL          SR
                    { 1f,   0f,   0f,   0f,   0f,         0f,         0f,         0f },         // FL
                    { 0f,   1f,   0f,   0f,   0f,         0f,         0f,         0f },         // FR
                    { 0f,   0f,   1f,   0f,   0f,         0f,         0f,         0f },         // FC
                    { 0f,   0f,   0f,   1f,   0f,         0f,         0f,         0f },         // LFE
                    { 0f,   0f,   0f,   0f,   CenterMix,  0f,         CenterMix,  0f },         // BL = BL+SL
                    { 0f,   0f,   0f,   0f,   0f,         CenterMix,  0f,         CenterMix },  // BR = BR+SR
                },

            // Stereo → 5.1 (upmix: split stereo to FL/FR, silence others)
            (AudioChannelLayout.Stereo, AudioChannelLayout.Surround51) =>
                new float[,]
                {
                    { 1f, 0f },   // FL = L
                    { 0f, 1f },   // FR = R
                    { 0.5f, 0.5f }, // FC = (L+R)/2
                    { 0f, 0f },   // LFE = 0
                    { 0f, 0f },   // BL = 0
                    { 0f, 0f },   // BR = 0
                },

            _ => BuildFallbackMatrix(src, dst)
        };
    }

    private static float[,] BuildIdentityMatrix(int channels)
    {
        var m = new float[channels, channels];
        for (int i = 0; i < channels; i++)
            m[i, i] = 1f;
        return m;
    }

    private static float[,] BuildFallbackMatrix(AudioChannelLayout src, AudioChannelLayout dst)
    {
        int inCh = (int)src;
        int outCh = (int)dst;
        var m = new float[outCh, inCh];

        if (outCh < inCh)
        {
            // Downmix: evenly distribute extra channels
            for (int i = 0; i < outCh; i++)
                m[i, i] = 1f;
            for (int i = outCh; i < inCh; i++)
                m[i % outCh, i] = 1f / (1 + (inCh - outCh) / outCh);
        }
        else
        {
            // Upmix: copy what we have, silence the rest
            for (int i = 0; i < inCh; i++)
                m[i, i] = 1f;
        }
        return m;
    }
}

// MediaKernel - ReplayGain Scanner
// Volume normalization using ITU-R BS.1770 loudness measurement

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Ripping;

/// <summary>
/// ReplayGain scanner using ITU-R BS.1770 algorithm for loudness measurement.
/// Calculates track and album gain values for volume normalization.
/// </summary>
public sealed class ReplayGainScanner
{
    // Reference loudness for ReplayGain 2.0 (EBU R128)
    private const double ReferenceLoudness = -18.0; // LUFS
    
    // ITU-R BS.1770 K-weighting filter coefficients (48 kHz)
    // Pre-filter (shelving)
    private static readonly double[] PreFilterB = [1.53512485958697, -2.69169618940638, 1.19839281085285];
    private static readonly double[] PreFilterA = [1.0, -1.69065929318241, 0.73248077421585];
    
    // High-pass filter (RLB weighting)
    private static readonly double[] HighPassB = [1.0, -2.0, 1.0];
    private static readonly double[] HighPassA = [1.0, -1.99004745483398, 0.99007225036621];
    
    /// <summary>
    /// Scan audio samples and calculate ReplayGain.
    /// </summary>
    public ReplayGainResult Scan(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        // Calculate mean square for each channel
        int samplesPerChannel = samples.Length / channels;
        double[] channelSums = new double[channels];
        
        // Apply K-weighting filter
        var filteredSamples = new float[samples.Length];
        ApplyKWeighting(samples, filteredSamples, sampleRate, channels);
        
        // Calculate gated loudness using overlapping 400ms windows
        int windowSamples = sampleRate * 4 / 10; // 400ms
        int hopSamples = windowSamples / 4; // 100ms hop (75% overlap)
        int numWindows = (samplesPerChannel - windowSamples) / hopSamples + 1;
        
        var windowLoudness = new double[numWindows];
        double peakSample = 0;
        
        for (int w = 0; w < numWindows; w++)
        {
            int startSample = w * hopSamples;
            double windowSum = 0;
            
            for (int ch = 0; ch < channels; ch++)
            {
                double channelWeight = GetChannelWeight(ch, channels);
                double channelSum = 0;
                
                for (int s = 0; s < windowSamples; s++)
                {
                    int idx = (startSample + s) * channels + ch;
                    if (idx < filteredSamples.Length)
                    {
                        float sample = filteredSamples[idx];
                        channelSum += sample * sample;
                        
                        // Track peak
                        float absSample = Math.Abs(samples[idx]);
                        if (absSample > peakSample)
                            peakSample = absSample;
                    }
                }
                
                windowSum += channelWeight * channelSum / windowSamples;
            }
            
            windowLoudness[w] = windowSum;
        }
        
        // Absolute gating (-70 LUFS)
        double absoluteThreshold = Math.Pow(10, (-70.0 + 0.691) / 10.0);
        var gatedWindows = new List<double>();
        
        foreach (double loudness in windowLoudness)
        {
            if (loudness > absoluteThreshold)
                gatedWindows.Add(loudness);
        }
        
        // Relative gating (-10 LU below ungated loudness)
        double ungatedMean = 0;
        foreach (double l in gatedWindows)
            ungatedMean += l;
        ungatedMean /= gatedWindows.Count;
        
        double relativeThreshold = ungatedMean * Math.Pow(10, -10.0 / 10.0);
        
        double gatedSum = 0;
        int gatedCount = 0;
        foreach (double l in gatedWindows)
        {
            if (l > relativeThreshold)
            {
                gatedSum += l;
                gatedCount++;
            }
        }
        
        // Calculate integrated loudness
        double integratedLoudness = gatedCount > 0
            ? -0.691 + 10.0 * Math.Log10(gatedSum / gatedCount)
            : -70.0;
        
        // Calculate gain to reach reference loudness
        double gain = ReferenceLoudness - integratedLoudness;
        
        return new ReplayGainResult
        {
            TrackGain = gain,
            TrackPeak = peakSample,
            IntegratedLoudness = integratedLoudness
        };
    }
    
    /// <summary>
    /// Calculate album gain from individual track results.
    /// </summary>
    public AlbumGainResult CalculateAlbumGain(IReadOnlyList<ReplayGainResult> trackResults)
    {
        if (trackResults.Count == 0)
        {
            return new AlbumGainResult
            {
                AlbumGain = 0,
                AlbumPeak = 0
            };
        }
        
        // Album gain is the average of track loudness values, not gains
        double totalLoudnessLinear = 0;
        double albumPeak = 0;
        
        foreach (var track in trackResults)
        {
            // Convert LUFS to linear power
            double linearPower = Math.Pow(10, track.IntegratedLoudness / 10.0);
            totalLoudnessLinear += linearPower;
            
            if (track.TrackPeak > albumPeak)
                albumPeak = track.TrackPeak;
        }
        
        double averageLoudness = 10.0 * Math.Log10(totalLoudnessLinear / trackResults.Count);
        double albumGain = ReferenceLoudness - averageLoudness;
        
        return new AlbumGainResult
        {
            AlbumGain = albumGain,
            AlbumPeak = albumPeak
        };
    }
    
    private void ApplyKWeighting(ReadOnlySpan<float> input, Span<float> output, int sampleRate, int channels)
    {
        // Get filter coefficients for this sample rate
        GetKWeightingCoeffs(sampleRate, 
            out var preB, out var preA, 
            out var hpB, out var hpA);
        
        // Filter state for each channel
        var preState1 = new double[channels];
        var preState2 = new double[channels];
        var hpState1 = new double[channels];
        var hpState2 = new double[channels];
        
        int samplesPerChannel = input.Length / channels;
        
        for (int s = 0; s < samplesPerChannel; s++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int idx = s * channels + ch;
                double sample = input[idx];
                
                // Pre-filter (shelving)
                double pre = preB[0] * sample + preState1[ch];
                preState1[ch] = preB[1] * sample - preA[1] * pre + preState2[ch];
                preState2[ch] = preB[2] * sample - preA[2] * pre;
                
                // High-pass (RLB weighting)
                double hp = hpB[0] * pre + hpState1[ch];
                hpState1[ch] = hpB[1] * pre - hpA[1] * hp + hpState2[ch];
                hpState2[ch] = hpB[2] * pre - hpA[2] * hp;
                
                output[idx] = (float)hp;
            }
        }
    }
    
    private static void GetKWeightingCoeffs(int sampleRate,
        out double[] preB, out double[] preA,
        out double[] hpB, out double[] hpA)
    {
        // Coefficients optimized for 48 kHz
        // For other sample rates, we'd need to recalculate using bilinear transform
        
        if (sampleRate == 48000)
        {
            preB = PreFilterB;
            preA = PreFilterA;
            hpB = HighPassB;
            hpA = HighPassA;
            return;
        }
        
        // Simplified coefficients for 44.1 kHz
        if (sampleRate == 44100)
        {
            preB = [1.5308412300498355, -2.6509799951536985, 1.1690790799210682];
            preA = [1.0, -1.6636551132560204, 0.7125954280732254];
            hpB = [1.0, -2.0, 1.0];
            hpA = [1.0, -1.9891696736297957, 0.9891990357870394];
            return;
        }
        
        // Default to 48 kHz coefficients (approximate for other rates)
        preB = PreFilterB;
        preA = PreFilterA;
        hpB = HighPassB;
        hpA = HighPassA;
    }
    
    private static double GetChannelWeight(int channel, int totalChannels)
    {
        // ITU-R BS.1770 channel weights
        // Center, L, R: 1.0
        // Ls, Rs (surround): 1.41 (+3dB)
        // LFE: 0 (excluded)
        
        if (totalChannels <= 2)
            return 1.0;
        
        // 5.1 layout: L, R, C, LFE, Ls, Rs
        return channel switch
        {
            0 => 1.0,   // L
            1 => 1.0,   // R
            2 => 1.0,   // C
            3 => 0.0,   // LFE
            4 => 1.41,  // Ls
            5 => 1.41,  // Rs
            _ => 1.0
        };
    }
    
    /// <summary>
    /// Scan samples using SIMD for performance.
    /// </summary>
    public ReplayGainResult ScanSimd(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        if (Avx2.IsSupported && samples.Length >= Vector256<float>.Count)
        {
            return ScanAvx2(samples, sampleRate, channels);
        }
        
        return Scan(samples, sampleRate, channels);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ReplayGainResult ScanAvx2(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        // Fast peak detection with AVX2
        var peakVec = Vector256<float>.Zero;
        var absMax = Vector256.Create(float.MaxValue);
        
        int vectorCount = samples.Length / Vector256<float>.Count;
        
        ref float sampleRef = ref MemoryMarshal.GetReference(samples);
        
        for (int i = 0; i < vectorCount; i++)
        {
            var v = Vector256.LoadUnsafe(ref sampleRef, (nuint)(i * Vector256<float>.Count));
            var abs = Avx.And(v, Vector256.Create(0x7FFFFFFF).AsSingle());
            peakVec = Avx.Max(peakVec, abs);
        }
        
        // Reduce peak vector
        float peak = 0;
        Span<float> peakArr = stackalloc float[8];
        peakVec.CopyTo(peakArr);
        for (int i = 0; i < 8; i++)
        {
            if (peakArr[i] > peak)
                peak = peakArr[i];
        }
        
        // Handle remainder
        for (int i = vectorCount * Vector256<float>.Count; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak)
                peak = abs;
        }
        
        // Fall back to scalar for loudness (K-weighting is complex)
        var result = Scan(samples, sampleRate, channels);
        result.TrackPeak = peak;
        
        return result;
    }
}

/// <summary>
/// ReplayGain result for a single track.
/// </summary>
public sealed class ReplayGainResult
{
    /// <summary>Gain adjustment in dB.</summary>
    public double TrackGain { get; set; }
    
    /// <summary>Peak sample value (0.0 to 1.0+).</summary>
    public double TrackPeak { get; set; }
    
    /// <summary>Integrated loudness in LUFS.</summary>
    public double IntegratedLoudness { get; set; }
    
    /// <summary>Format gain for display.</summary>
    public string FormatGain() => $"{TrackGain:+0.00;-0.00;0.00} dB";
}

/// <summary>
/// Album-level ReplayGain result.
/// </summary>
public sealed class AlbumGainResult
{
    /// <summary>Album gain adjustment in dB.</summary>
    public double AlbumGain { get; set; }
    
    /// <summary>Album peak (highest peak across all tracks).</summary>
    public double AlbumPeak { get; set; }
    
    /// <summary>Format gain for display.</summary>
    public string FormatGain() => $"{AlbumGain:+0.00;-0.00;0.00} dB";
}

/// <summary>
/// High-performance peak detector using SIMD.
/// </summary>
public static class PeakDetector
{
    /// <summary>
    /// Find the peak absolute sample value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FindPeak(ReadOnlySpan<float> samples)
    {
        if (Avx2.IsSupported && samples.Length >= Vector256<float>.Count * 4)
        {
            return FindPeakAvx2(samples);
        }
        
        return FindPeakScalar(samples);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float FindPeakAvx2(ReadOnlySpan<float> samples)
    {
        var signMask = Vector256.Create(0x7FFFFFFF).AsSingle();
        var peak0 = Vector256<float>.Zero;
        var peak1 = Vector256<float>.Zero;
        var peak2 = Vector256<float>.Zero;
        var peak3 = Vector256<float>.Zero;
        
        ref float sampleRef = ref MemoryMarshal.GetReference(samples);
        int unrollCount = samples.Length / (Vector256<float>.Count * 4);
        
        for (int i = 0; i < unrollCount; i++)
        {
            int baseIdx = i * Vector256<float>.Count * 4;
            
            var v0 = Vector256.LoadUnsafe(ref sampleRef, (nuint)(baseIdx));
            var v1 = Vector256.LoadUnsafe(ref sampleRef, (nuint)(baseIdx + 8));
            var v2 = Vector256.LoadUnsafe(ref sampleRef, (nuint)(baseIdx + 16));
            var v3 = Vector256.LoadUnsafe(ref sampleRef, (nuint)(baseIdx + 24));
            
            peak0 = Avx.Max(peak0, Avx.And(v0, signMask));
            peak1 = Avx.Max(peak1, Avx.And(v1, signMask));
            peak2 = Avx.Max(peak2, Avx.And(v2, signMask));
            peak3 = Avx.Max(peak3, Avx.And(v3, signMask));
        }
        
        // Reduce
        peak0 = Avx.Max(peak0, peak1);
        peak2 = Avx.Max(peak2, peak3);
        peak0 = Avx.Max(peak0, peak2);
        
        Span<float> peakArr = stackalloc float[8];
        peak0.CopyTo(peakArr);
        
        float peak = 0;
        for (int i = 0; i < 8; i++)
        {
            if (peakArr[i] > peak)
                peak = peakArr[i];
        }
        
        // Handle remainder
        int remainder = unrollCount * Vector256<float>.Count * 4;
        for (int i = remainder; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak)
                peak = abs;
        }
        
        return peak;
    }
    
    private static float FindPeakScalar(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak)
                peak = abs;
        }
        return peak;
    }
    
    /// <summary>
    /// Find peak with 16-bit samples.
    /// </summary>
    public static short FindPeak16(ReadOnlySpan<short> samples)
    {
        short peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            short abs = Math.Abs(samples[i]);
            if (abs > peak)
                peak = abs;
        }
        return peak;
    }
}

/// <summary>
/// R128 loudness scanner (EBU R128 standard).
/// </summary>
public sealed class R128Scanner
{
    private readonly List<double> blockLoudness = [];
    private readonly int sampleRate;
    private readonly int channels;
    private double[] kWeightState;
    
    private const int BlockDurationMs = 400;
    private const int OverlapMs = 100;
    
    public R128Scanner(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        kWeightState = new double[channels * 4]; // 2 stages * 2 state vars per channel
    }
    
    /// <summary>
    /// Add samples to the scanner.
    /// </summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        // Process overlapping 400ms blocks with 100ms hop
        int blockSamples = sampleRate * BlockDurationMs / 1000;
        int hopSamples = sampleRate * OverlapMs / 1000;
        
        int samplesPerChannel = samples.Length / channels;
        
        for (int start = 0; start + blockSamples <= samplesPerChannel; start += hopSamples)
        {
            double blockPower = CalculateBlockPower(samples, start, blockSamples);
            blockLoudness.Add(blockPower);
        }
    }
    
    private double CalculateBlockPower(ReadOnlySpan<float> samples, int startSample, int blockSamples)
    {
        double totalPower = 0;
        
        for (int ch = 0; ch < channels; ch++)
        {
            double channelWeight = ch switch
            {
                3 => 0.0,  // LFE
                4 or 5 => 1.41, // Surround
                _ => 1.0
            };
            
            double channelPower = 0;
            
            for (int s = 0; s < blockSamples; s++)
            {
                int idx = (startSample + s) * channels + ch;
                if (idx < samples.Length)
                {
                    float sample = samples[idx];
                    // Apply K-weighting would be done here
                    channelPower += sample * sample;
                }
            }
            
            totalPower += channelWeight * channelPower / blockSamples;
        }
        
        return totalPower;
    }
    
    /// <summary>
    /// Get integrated loudness in LUFS.
    /// </summary>
    public double GetIntegratedLoudness()
    {
        if (blockLoudness.Count == 0)
            return -70.0;
        
        // Absolute gate: -70 LUFS
        double absoluteThreshold = Math.Pow(10, -7.0);
        var gated = new List<double>();
        
        foreach (double l in blockLoudness)
        {
            if (l > absoluteThreshold)
                gated.Add(l);
        }
        
        if (gated.Count == 0)
            return -70.0;
        
        // Relative gate: -10 LU below ungated mean
        double ungatedMean = 0;
        foreach (double l in gated)
            ungatedMean += l;
        ungatedMean /= gated.Count;
        
        double relativeThreshold = ungatedMean * 0.1; // -10 dB
        
        double gatedSum = 0;
        int gatedCount = 0;
        
        foreach (double l in gated)
        {
            if (l > relativeThreshold)
            {
                gatedSum += l;
                gatedCount++;
            }
        }
        
        if (gatedCount == 0)
            return -70.0;
        
        return -0.691 + 10.0 * Math.Log10(gatedSum / gatedCount);
    }
    
    /// <summary>
    /// Get short-term loudness (3s window).
    /// </summary>
    public double GetShortTermLoudness()
    {
        // Use last 30 blocks (3 seconds at 100ms hop)
        int shortTermBlocks = Math.Min(30, blockLoudness.Count);
        if (shortTermBlocks == 0)
            return -70.0;
        
        double sum = 0;
        for (int i = blockLoudness.Count - shortTermBlocks; i < blockLoudness.Count; i++)
        {
            sum += blockLoudness[i];
        }
        
        return -0.691 + 10.0 * Math.Log10(sum / shortTermBlocks);
    }
    
    /// <summary>
    /// Get momentary loudness (400ms window).
    /// </summary>
    public double GetMomentaryLoudness()
    {
        if (blockLoudness.Count == 0)
            return -70.0;
        
        double last = blockLoudness[^1];
        return -0.691 + 10.0 * Math.Log10(last);
    }
}

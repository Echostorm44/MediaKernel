using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// MP3 IMDCT and polyphase synthesis filterbank.
/// Ported from FFmpeg's mpegaudiodsp_template.c, mpegaudiodsp.c, and dct32_template.c.
/// Converts frequency-domain spectral coefficients to time-domain PCM samples.
/// </summary>
public sealed class Mp3Filterbank
{
    /// <summary>Number of subbands.</summary>
    public const int SubbandCount = 32;
    
    /// <summary>Number of samples per subband per granule.</summary>
    public const int SamplesPerSubband = 18;
    
    /// <summary>Total spectral lines per granule.</summary>
    public const int SpectralLines = 576;
    
    // IMDCT overlap buffer (32 subbands × 18 samples)
    private readonly float[] overlapBuffer;
    
    // Synthesis circular buffer: 512 × 2 = 1024 entries.
    // [0..511] is the active circular buffer, [512..1023] mirrors [0..511] for wrap-around.
    // Matches FFmpeg's synth_buf[512*2] in mpegaudiodec_template.c.
    private readonly float[] synthBuffer;
    private int synthBufferOffset;
    
    public Mp3Filterbank()
    {
        overlapBuffer = new float[SubbandCount * SamplesPerSubband];
        synthBuffer = new float[512 * 2];
        synthBufferOffset = 0;
    }
    
    /// <summary>
    /// Resets the filterbank state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(overlapBuffer);
        Array.Clear(synthBuffer);
        synthBufferOffset = 0;
    }
    
    /// <summary>
    /// Performs IMDCT, frequency inversion, and polyphase synthesis for one granule.
    /// Uses an external overlap buffer (managed by the decoder) and internal synthesis state.
    /// </summary>
    public void ProcessGranuleWithSynthesis(
        ReadOnlySpan<float> spectral,
        in Mp3GranuleInfo granuleInfo,
        Span<float> overlapState,
        Span<float> output)
    {
        Mp3BlockType blockType = granuleInfo.BlockType switch
        {
            0 => Mp3BlockType.Normal,
            1 => Mp3BlockType.Start,
            2 => Mp3BlockType.Short,
            3 => Mp3BlockType.End,
            _ => Mp3BlockType.Normal
        };

        // Step 1: IMDCT + window + overlap-add → 32×18 subband samples
        Span<float> subbandSamples = stackalloc float[SubbandCount * SamplesPerSubband];
        ProcessGranuleImdct(spectral, blockType, granuleInfo.MixedBlockFlag, overlapState, subbandSamples);
        
        // Step 2: Polyphase synthesisfor each of 18 time slots
        // (Frequency inversion is built into the merged IMDCT windows)
        Span<float> subbandValues = stackalloc float[SubbandCount];
        
        for (int sample = 0; sample < SamplesPerSubband; sample++)
        {
            // Extract 32 subband values for this time slot (time-major layout from IMDCT)
            for (int sb = 0; sb < SubbandCount; sb++)
                subbandValues[sb] = subbandSamples[sample * SubbandCount + sb];
            
            // Synthesize 32 PCM samples via DCT-32 + windowed accumulation
            SynthesizePolyphase(subbandValues, output.Slice(sample * SubbandCount, SubbandCount));
        }
    }

    /// <summary>
    /// Performs IMDCT and polyphase synthesis using the instance's own overlap buffer.
    /// Used by unit tests and standalone filterbank testing.
    /// </summary>
    public void ProcessGranule(
        ReadOnlySpan<float> spectral,
        Mp3BlockType blockType,
        bool mixedBlock,
        Span<float> output)
    {
        Span<float> subbandSamples = stackalloc float[SubbandCount * SamplesPerSubband];
        ProcessGranuleImdct(spectral, blockType, mixedBlock, overlapBuffer, subbandSamples);
        
        Span<float> subbandValues = stackalloc float[SubbandCount];
        for (int sample = 0; sample < SamplesPerSubband; sample++)
        {
            for (int sb = 0; sb < SubbandCount; sb++)
                subbandValues[sb] = subbandSamples[sample * SubbandCount + sb];
            SynthesizePolyphase(subbandValues, output.Slice(sample * SubbandCount, SubbandCount));
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IMDCT — FFmpeg-matching fast implementation
    //  Uses Lee decomposition + hand-coded 9-point DCT for imdct36, and
    //  prefix-sum + butterfly for imdct12. Merged window coefficients
    //  include IMDCT_SCALAR compensation and frequency inversion.
    //  Ported from FFmpeg's imdct36() in mpegaudiodsp_template.c and
    //  imdct12() in mpegaudiodec_template.c.
    // ──────────────────────────────────────────────────────────────────────

    private const double ImdctScalar = 1.759;
    
    // MDCT_BUF_SIZE= FFALIGN(36, 8) = 40.
    // Window layout: indices 0..17 = first half, 20..37 = second half (gap at 18-19).
    private const int MdctBufSize = 40;
    
    // Merged MDCT windows: 8 variants (4 block types × 2 freq inversion states).
    // [0]=Normal, [1]=Start, [2]=Short (12 entries), [3]=End
    // [4..7]=freq-inverted versions of [0..3]
    // Computed identically to FFmpeg's mpadsp_init_tabs() in mpegaudiodsp.c.
    private static readonly float[][] MergedMdctWin = InitializeMergedMdctWindows();
    
    private static float[][] InitializeMergedMdctWindows()
    {
        var win = new float[8][];
        for (int j = 0; j < 8; j++)
            win[j] = new float[MdctBufSize];
        
        for (int i = 0; i < 36; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                if (j == 2 && i % 3 != 1)
                    continue;
                
                double d = Math.Sin(Math.PI * (i + 0.5) / 36.0);
                
                if (j == 1) // Start window
                {
                    if (i >= 30) d = 0;
                    else if (i >= 24) d = Math.Sin(Math.PI * (i - 18 + 0.5) / 12.0);
                    else if (i >= 18) d = 1;
                }
                else if (j == 3) // End window
                {
                    if (i < 6) d = 0;
                    else if (i < 12) d = Math.Sin(Math.PI * (i - 6 + 0.5) / 12.0);
                    else if (i < 18) d = 1;
                }
                
                // Merge last stage of IMDCT into window coefficients (FFmpeg's mpadsp_init_tabs)
                d *= 0.5 * ImdctScalar / Math.Cos(Math.PI * (2 * i + 19) / 72.0);
                
                // FFmpeg's code divides by 32 here (1 << 5), but that's compensated by FFmpeg's
                // much larger spectral values (the +400 exponent offset in exponents_from_scale_factors
                // means FFmpeg's spectral values are ~2^28 larger). Since our requant produces
                // natural-scale floats, we omit the /32 to maintain correct output magnitude.
                if (j == 2) // Short window — 12 entries at i/3
                    win[j][i / 3] = (float)d;
                else
                {
                    int idx = i < 18 ? i : i + (MdctBufSize / 2 - 18);
                    win[j][idx] = (float)d;
                }
            }
        }
        
        // Frequency inversion: windows 4-7 negate odd indices
        for (int j = 0; j < 4; j++)
        {
            for (int i = 0; i < MdctBufSize; i += 2)
            {
                win[j + 4][i]     =  win[j][i];
                win[j + 4][i + 1] = -win[j][i + 1];
            }
        }
        
        return win;
    }
    
    // cos(π*i/18) constants for 9-point DCT (FIXHR = float cast of value/2)
    private const float ImdctC1 = (float)(0.98480775301220805936 / 2);
    private const float ImdctC2 = (float)(0.93969262078590838405 / 2);
    private const float ImdctC3 = (float)(0.86602540378443864676 / 2);
    private const float ImdctC4 = (float)(0.76604444311897803520 / 2);
    private const float ImdctC5 = (float)(0.64278760968653932632 / 2);
    private const float ImdctC6 = (float)(0.5 / 2);
    private const float ImdctC7 = (float)(0.34202014332566873304 / 2);
    private const float ImdctC8 = (float)(0.17364817766693034885 / 2);
    
    // 0.5 / cos(π*(2i+1)/36)
    private static readonly float[] Icos36 =
    {
        (float)0.50190991877167369479,
        (float)0.51763809020504152469,
        (float)0.55168895948124587824,
        (float)0.61038729438072803416,
        (float)0.70710678118654752439,
        (float)0.87172339781054900991,
        (float)1.18310079157624925896,
        (float)1.93185165257813657349,
        (float)5.73685662283492756461,
    };
    
    // 0.5 / cos(π*(2i+1)/36) with extra halving for icos36h[6..7]
    private static readonly float[] Icos36h =
    {
        (float)(0.50190991877167369479 / 2),
        (float)(0.51763809020504152469 / 2),
        (float)(0.55168895948124587824 / 2),
        (float)(0.61038729438072803416 / 2),
        (float)(0.70710678118654752439 / 2),
        (float)(0.87172339781054900991 / 2),
        (float)(1.18310079157624925896 / 4),
        (float)(1.93185165257813657349 / 4),
    };
    
    /// <summary>
    /// IMDCT + window + overlap-add for all 32 subbands.
    /// Uses brute-force imdct36/imdct12 with standard windows.
    /// Output is in time-major layout: subbandSamples[sample * 32 + subband].
    /// </summary>
    private static void ProcessGranuleImdct(
        ReadOnlySpan<float> spectral,
        Mp3BlockType blockType,
        bool mixedBlock,
        Span<float> overlapState,
        Span<float> subbandSamples)
    {
        int longEnd = blockType == Mp3BlockType.Short ? (mixedBlock ? 2 : 0) : SubbandCount;
        
        // Long blocks (and mixed-block long subbands) — brute-force approach
        ReadOnlySpan<float> cos36 = Mp3HuffmanTables.ImdctCos36;
        ReadOnlySpan<float> longWindow = blockType switch
        {
            Mp3BlockType.Start => Mp3HuffmanTables.ImdctWindowStart,
            Mp3BlockType.End   => Mp3HuffmanTables.ImdctWindowEnd,
            _                  => Mp3HuffmanTables.ImdctWindowLong
        };
        
        for (int j = 0; j < longEnd; j++)
        {
            ReadOnlySpan<float> win = (mixedBlock && j < 2)
                ? Mp3HuffmanTables.ImdctWindowLong
                : longWindow;
            Span<float> overlap = overlapState.Slice(j * SamplesPerSubband, SamplesPerSubband);
            ReadOnlySpan<float> input = spectral.Slice(j * SamplesPerSubband, SamplesPerSubband);
            
            ProcessLongBlockBrute(input, overlap, subbandSamples, j, cos36, win);
        }
        
        // Short blocks — brute-force imdct12 with standard short window
        ReadOnlySpan<float> cos12 = Mp3HuffmanTables.ImdctCos12;
        ReadOnlySpan<float> shortWindow = Mp3HuffmanTables.ImdctWindowShort;
        for (int j = longEnd; j < SubbandCount; j++)
        {
            Span<float> overlap = overlapState.Slice(j * SamplesPerSubband, SamplesPerSubband);
            ReadOnlySpan<float> input = spectral.Slice(j * SamplesPerSubband, SamplesPerSubband);
            
            ProcessShortBlockBrute(input, overlap, subbandSamples, j, cos12, shortWindow);
        }
        
        // Frequency inversion for ALL odd subbands (brute-force doesn't have it built in)
        for (int sample = 1; sample < SamplesPerSubband; sample += 2)
        {
            for (int sb = 1; sb < SubbandCount; sb += 2)
            {
                subbandSamples[sample * SubbandCount + sb] = -subbandSamples[sample * SubbandCount + sb];
            }
        }
    }
    
    /// <summary>
    /// Fast 36-point IMDCT using Lee decomposition + hand-coded 9-point DCT.
    /// Port of FFmpeg's imdct36() from mpegaudiodsp_template.c (float mode).
    /// Input is modified in-place (prefix sums). Overlap uses stride 1.
    /// Output written at stride 32 into subband sample buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Imdct36Fast(
        Span<float> input,
        Span<float> overlap,
        float[] win,
        Span<float> output,
        int subbandIdx)
    {
        // Prefix sums
        for (int i = 17; i >= 1; i--)
            input[i] += input[i - 1];
        for (int i = 17; i >= 3; i -= 2)
            input[i] += input[i - 2];
        
        Span<float> tmp = stackalloc float[18];
        
        // Two-pass 9-point DCT (even and odd indexed elements)
        for (int j = 0; j < 2; j++)
        {
            // in1[2*k] = input[j + 2*k], tmp1[2*k] = tmp[j + 2*k]
            float t2 = input[j + 8] + input[j + 16] - input[j + 4];
            float t3 = input[j] + input[j + 12] * 0.5f;
            float t1 = input[j] - input[j + 12];
            tmp[j + 6] = t1 - t2 * 0.5f;
            tmp[j + 16] = t1 + t2;
            
            float t0 = 2 * ImdctC2 * (input[j + 4] + input[j + 8]);
            t1 = 1 * (-2 * ImdctC8) * (input[j + 8] - input[j + 16]);
            t2 = 2 * (-ImdctC4) * (input[j + 4] + input[j + 16]);
            
            tmp[j + 10] = t3 - t0 - t2;
            tmp[j + 2] = t3 + t0 + t1;
            tmp[j + 14] = t3 + t2 - t1;
            
            tmp[j + 4] = 2 * (-ImdctC3) * (input[j + 10] + input[j + 14] - input[j + 2]);
            t2 = 2 * ImdctC1 * (input[j + 2] + input[j + 10]);
            t3 = 1 * (-2 * ImdctC7) * (input[j + 10] - input[j + 14]);
            t0 = 2 * ImdctC3 * input[j + 6];
            t1 = 2 * (-ImdctC5) * (input[j + 2] + input[j + 14]);
            
            tmp[j + 0] = t2 + t3 + t0;
            tmp[j + 12] = t2 + t1 - t0;
            tmp[j + 8] = t3 - t1 - t0;
        }
        
        // Final combine: butterfly + icos36 multiply + window + overlap-add
        int idx = 0;
        for (int j = 0; j < 4; j++)
        {
            float t0 = tmp[idx];
            float t1 = tmp[idx + 2];
            float s0 = t1 + t0;
            float s2 = t1 - t0;
            
            float t2 = tmp[idx + 1];
            float t3 = tmp[idx + 3];
            float s1 = 2 * Icos36h[j] * (t3 + t2);
            float s3 = Icos36[8 - j] * (t3 - t2);
            
            t0 = s0 + s1;
            t1 = s0 - s1;
            output[(9 + j) * SubbandCount + subbandIdx] = win[9 + j] * t1 + overlap[9 + j];
            output[(8 - j) * SubbandCount + subbandIdx] = win[8 - j] * t1 + overlap[8 - j];
            overlap[9 + j] = win[MdctBufSize / 2 + 9 + j] * t0;
            overlap[8 - j] = win[MdctBufSize / 2 + 8 - j] * t0;
            
            t0 = s2 + s3;
            t1 = s2 - s3;
            output[(9 + 8 - j) * SubbandCount + subbandIdx] = win[9 + 8 - j] * t1 + overlap[9 + 8 - j];
            output[j * SubbandCount + subbandIdx] = win[j] * t1 + overlap[j];
            overlap[9 + 8 - j] = win[MdctBufSize / 2 + 9 + 8 - j] * t0;
            overlap[j] = win[MdctBufSize / 2 + j] * t0;
            
            idx += 4;
        }
        
        // Last pair (j=4)
        float s0f = tmp[16];
        float s1f = 2 * Icos36h[4] * tmp[17];
        float t0f = s0f + s1f;
        float t1f = s0f - s1f;
        output[(9 + 4) * SubbandCount + subbandIdx] = win[9 + 4] * t1f + overlap[9 + 4];
        output[(8 - 4) * SubbandCount + subbandIdx] = win[8 - 4] * t1f + overlap[8 - 4];
        overlap[9 + 4] = win[MdctBufSize / 2 + 9 + 4] * t0f;
        overlap[8 - 4] = win[MdctBufSize / 2 + 8 - 4] * t0f;
    }
    
    /// <summary>
    /// Short block processing using FFmpeg's fast imdct12 and merged windows.
    /// Port of the short block loop in mpegaudiodec_template.c:1168-1196.
    /// </summary>
    private static void ProcessShortBlockFast(
        ReadOnlySpan<float> spectral,
        Span<float> overlap,
        float[] win,
        Span<float> outBuf, int subbandIdx)
    {
        Span<float> out2 = stackalloc float[12];
        
        // Input spectral data already divided by IMDCT_SCALAR in requantization,
        // so no additional scaling needed here.
        
        // First 6 output positions: copy from overlap buffer
        for (int i = 0; i < 6; i++)
            outBuf[i * SubbandCount + subbandIdx] = overlap[i];
        
        // Window 0: imdct12(offset=0)
        Imdct12Fast(out2, spectral, 0);
        for (int i = 0; i < 6; i++)
        {
            outBuf[(6 + i) * SubbandCount + subbandIdx] = win[i] * out2[i] + overlap[6 + i];
            overlap[12 + i] = win[i + 6] * out2[i + 6];
        }
        
        // Window 1: imdct12(offset=1)
        Imdct12Fast(out2, spectral, 1);
        for (int i = 0; i < 6; i++)
        {
            outBuf[(12 + i) * SubbandCount + subbandIdx] = win[i] * out2[i] + overlap[12 + i];
            overlap[i] = win[i + 6] * out2[i + 6];
        }
        
        // Window 2: imdct12(offset=2) — updates overlap only
        Imdct12Fast(out2, spectral, 2);
        for (int i = 0; i < 6; i++)
        {
            overlap[i]      = win[i] * out2[i] + overlap[i];
            overlap[6 + i]  = win[i + 6] * out2[i + 6];
            overlap[12 + i] = 0;
        }
    }
    
    /// <summary>
    /// Fast 12-point IMDCT using prefix sums + butterfly decomposition.
    /// Port of FFmpeg's imdct12() from mpegaudiodec_template.c (float mode).
    /// Reads 6 values at stride 3 from input[offset + k*3].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Imdct12Fast(
        Span<float> output,
        ReadOnlySpan<float> input,
        int offset)
    {
        float in0 = input[offset + 0 * 3];
        float in1 = input[offset + 1 * 3] + input[offset + 0 * 3];
        float in2 = input[offset + 2 * 3] + input[offset + 1 * 3];
        float in3 = input[offset + 3 * 3] + input[offset + 2 * 3];
        float in4 = input[offset + 4 * 3] + input[offset + 3 * 3];
        float in5 = input[offset + 5 * 3] + input[offset + 4 * 3];
        in5 += in3;
        in3 += in1;
        
        in2 = 2 * ImdctC3 * in2;
        in3 = 4 * ImdctC3 * in3;
        
        float t1 = in0 - in4;
        float t2 = 2 * ImdctC4 * (in1 - in5);
        
        output[7] = output[10] = t1 + t2;
        output[1] = output[4]  = t1 - t2;
        
        in0 += in4 * 0.5f;
        in4 = in0 + in2;
        in5 += 2 * in1;
        in1 = 1 * ImdctC5 * (in5 + in3);
        output[8] = output[9] = in4 + in1;
        output[2] = output[3] = in4 - in1;
        
        in0 -= in2;
        in5 = 2 * ImdctC6 * (in5 - in3);
        output[0] = output[5]  = in0 - in5;
        output[6] = output[11] = in0 + in5;
    }
    
    /// <summary>
    /// Brute-force 36-point IMDCT with window and overlap-add for long blocks.
    /// Produces BIT-EXACT output matching FFmpeg.
    /// </summary>
    private static void ProcessLongBlockBrute(
        ReadOnlySpan<float> spectral,
        Span<float> overlap,
        Span<float> outBuf, int subbandIdx,
        ReadOnlySpan<float> cos36,
        ReadOnlySpan<float> win)
    {
        Span<float> imdctOutput = stackalloc float[36];
        
        for (int n = 0; n < 36; n++)
        {
            float sum = 0;
            int cosBase = n * 18;
            for (int k = 0; k < 18; k++)
                sum += spectral[k] * cos36[cosBase + k];
            imdctOutput[n] = sum;
        }
        
        for (int i = 0; i < SamplesPerSubband; i++)
        {
            outBuf[i * SubbandCount + subbandIdx] = imdctOutput[i] * win[i] + overlap[i];
            overlap[i] = imdctOutput[i + SamplesPerSubband] * win[i + SamplesPerSubband];
        }
    }
    
    /// <summary>
    /// Short block processing: 3 × brute-force imdct12 with sine window and overlap-add.
    /// This brute-force approach achieves BIT-EXACT output for si_block.
    /// </summary>
    private static void ProcessShortBlockBrute(
        ReadOnlySpan<float> spectral,
        Span<float> overlap,
        Span<float> outBuf, int subbandIdx,
        ReadOnlySpan<float> cos12,
        ReadOnlySpan<float> shortWindow)
    {
        Span<float> out2 = stackalloc float[12];
        
        // First 6 output positions: copy from overlap buffer
        for (int i = 0; i < 6; i++)
            outBuf[i * SubbandCount + subbandIdx] = overlap[i];
        
        // Window 0: imdct12(offset=0)
        Imdct12Brute(out2, spectral, 0, cos12);
        for (int i = 0; i < 6; i++)
        {
            outBuf[(6 + i) * SubbandCount + subbandIdx] = out2[i] * shortWindow[i] + overlap[6 + i];
            overlap[12 + i] = out2[i + 6] * shortWindow[i + 6];
        }
        
        // Window 1: imdct12(offset=1)
        Imdct12Brute(out2, spectral, 1, cos12);
        for (int i = 0; i < 6; i++)
        {
            outBuf[(12 + i) * SubbandCount + subbandIdx] = out2[i] * shortWindow[i] + overlap[12 + i];
            overlap[i] = out2[i + 6] * shortWindow[i + 6];
        }
        
        // Window 2: imdct12(offset=2) — updates overlap only
        Imdct12Brute(out2, spectral, 2, cos12);
        for (int i = 0; i < 6; i++)
        {
            overlap[i]      = out2[i] * shortWindow[i] + overlap[i];
            overlap[6 + i]  = out2[i + 6] * shortWindow[i + 6];
            overlap[12 + i] = 0;
        }
    }
    
    /// <summary>
    /// Brute-force 12-point IMDCT: 6 inputs (stride 3) → 12 outputs.
    /// output[n] = Σ_{k=0}^{5} input[offset + k*3] * cos(π/24 * (2n+7) * (2k+1))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Imdct12Brute(
        Span<float> output,
        ReadOnlySpan<float> input,
        int offset,
        ReadOnlySpan<float> cos12)
    {
        for (int n = 0; n < 12; n++)
        {
            float sum = 0;
            int cosBase = n * 6;
            for (int k = 0; k < 6; k++)
                sum += input[offset + k * 3] * cos12[cosBase + k];
            output[n] = sum;
        }
    }
    
    // ──────────────────────────────────────────────────────────────────────
    //  Polyphase Synthesis Filter
    //  Ported from FFmpeg's ff_mpa_synth_filter_float + apply_window_float
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One pass of the polyphase synthesis filter: 32 subband samples in, 32 PCM samples out.
    /// </summary>
    /// <remarks>
    /// Layers I, II and III share this filter - FFmpeg calls the same
    /// <c>ff_mpa_synth_filter_float</c> for all three - so the layer II decoder drives it
    /// directly rather than carrying a second copy. The state is per channel, so a stereo
    /// decoder needs one filterbank per channel.
    /// </remarks>
    public void SynthesizeSubbands(ReadOnlySpan<float> subbandSamples, Span<float> output)
        => SynthesizePolyphase(subbandSamples, output);

    /// <summary>
    /// 32-subband synthesis filter: DCT-32 → circular buffer → windowed accumulation → 32 PCM samples.
    /// Matches FFmpeg's ff_mpa_synth_filter_float in mpegaudiodsp_template.c.
    /// </summary>
    private void SynthesizePolyphase(ReadOnlySpan<float> subbandSamples, Span<float> output)
    {
        // DCT-32 into circular synthesis buffer
        Dct32(synthBuffer.AsSpan(synthBufferOffset), subbandSamples);
        
        // Windowed accumulation → 32 output samples
        ApplyWindow(synthBuffer, synthBufferOffset, Mp3HuffmanTables.SynthesisWindowArray, output);
        
        // Advance circular buffer
        synthBufferOffset = (synthBufferOffset - 32) & 511;
    }
    
    /// <summary>
    /// Apply synthesis window to produce 32 PCM samples from the circular synth buffer.
    /// Ported from FFmpeg's ff_mpadsp_apply_window_float in mpegaudiodsp_template.c.
    /// </summary>
    private static void ApplyWindow(
        float[] synthBuf,
        int offset,
        float[] window,
        Span<float> samples)
    {
        // Copy current 32 entries to mirror region for wrap-around access.
        // FFmpeg: memcpy(synth_buf + 512, synth_buf, 32 * sizeof(float))
        // Note: synth_buf in FFmpeg is already offset-adjusted, so this copies
        // the current DCT slot's data to offset+512 for stride-64 wrap-around.
        Array.Copy(synthBuf, offset, synthBuf, offset + 512, 32);
        
        // Sample 0: sum = Σ w[t*64]*buf[16+t*64] - Σ w[32+t*64]*buf[48+t*64]
        float sum = 0;
        for (int t = 0; t < 8; t++)
            sum += window[t * 64] * synthBuf[offset + 16 + t * 64];
        for (int t = 0; t < 8; t++)
            sum -= window[32 + t * 64] * synthBuf[offset + 48 + t * 64];
        samples[0] = sum;
        
        // Samples 1..15 (forward) and 31..17 (backward), two at a time
        for (int j = 1; j < 16; j++)
        {
            sum = 0;
            float sum2 = 0;
            
            // First group: p = synth_buf + 16 + j
            for (int t = 0; t < 8; t++)
            {
                float tmp = synthBuf[offset + 16 + j + t * 64];
                sum  += window[j + t * 64] * tmp;           // w[j + t*64]
                sum2 -= window[32 - j + t * 64] * tmp;      // w2[t*64] where w2 = window + 32 - j
            }
            
            // Second group: p = synth_buf + 48 - j
            for (int t = 0; t < 8; t++)
            {
                float tmp = synthBuf[offset + 48 - j + t * 64];
                sum  -= window[32 + j + t * 64] * tmp;      // (w+32)[t*64]
                sum2 -= window[64 - j + t * 64] * tmp;      // (w2+32)[t*64]
            }
            
            samples[j] = sum;
            samples[32 - j] = sum2;
        }
        
        // Sample 16: sum = -Σ w[48+t*64]*buf[32+t*64]
        sum = 0;
        for (int t = 0; t < 8; t++)
            sum -= window[48 + t * 64] * synthBuf[offset + 32 + t * 64];
        samples[16] = sum;
    }
    
    /// <summary>
    /// Static helper to process a granule without synthesis (for tests).
    /// </summary>
    public static void ProcessGranule(
        ReadOnlySpan<float> spectral,
        in Mp3GranuleInfo granuleInfo,
        Span<float> overlapState,
        Span<float> output)
    {
        Mp3BlockType blockType = granuleInfo.BlockType switch
        {
            0 => Mp3BlockType.Normal,
            1 => Mp3BlockType.Start,
            2 => Mp3BlockType.Short,
            3 => Mp3BlockType.End,
            _ => Mp3BlockType.Normal
        };
        ProcessGranuleImdct(spectral, blockType, granuleInfo.MixedBlockFlag, overlapState, output);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DCT-32 (ported from FFmpeg's dct32_template.c / ff_dct32_float)
    // ──────────────────────────────────────────────────────────────────────

    // Cosine constants for DCT-32 butterfly stages.
    // FFmpeg defines these as FIXHR(value/divisor) and then applies (1<<s) in the BF macro,
    // where divisor == (1<<s), so the net multiplier is just the original value.
    // For float: we use the original values directly (no division, no separate shift).
    private const float COS0_0  = 0.50060299823519630134f;
    private const float COS0_1  = 0.50547095989754365998f;
    private const float COS0_2  = 0.51544730992262454697f;
    private const float COS0_3  = 0.53104259108978417447f;
    private const float COS0_4  = 0.55310389603444452782f;
    private const float COS0_5  = 0.58293496820613387367f;
    private const float COS0_6  = 0.62250412303566481615f;
    private const float COS0_7  = 0.67480834145500574602f;
    private const float COS0_8  = 0.74453627100229844977f;
    private const float COS0_9  = 0.83934964541552703873f;
    private const float COS0_10 = 0.97256823786196069369f;
    private const float COS0_11 = 1.16943993343288495515f;
    private const float COS0_12 = 1.48416461631416627724f;
    private const float COS0_13 = 2.05778100995341155085f;
    private const float COS0_14 = 3.40760841846871878570f;
    private const float COS0_15 = 10.19000812354805681150f;

    private const float COS1_0 = 0.50241928618815570551f;
    private const float COS1_1 = 0.52249861493968888062f;
    private const float COS1_2 = 0.56694403481635770368f;
    private const float COS1_3 = 0.64682178335999012954f;
    private const float COS1_4 = 0.78815462345125022473f;
    private const float COS1_5 = 1.06067768599034747134f;
    private const float COS1_6 = 1.72244709823833392782f;
    private const float COS1_7 = 5.10114861868916385802f;

    private const float COS2_0 = 0.50979557910415916894f;
    private const float COS2_1 = 0.60134488693504528054f;
    private const float COS2_2 = 0.89997622313641570463f;
    private const float COS2_3 = 2.56291544774150617881f;

    private const float COS3_0 = 0.54119610014619698439f;
    private const float COS3_1 = 1.30656296487637652785f;

    private const float COS4_0 = 0.70710678118654752439f;

    /// <summary>
    /// Fast 32-point DCT. Ported from FFmpeg's ff_dct32_float (dct32_template.c).
    /// Writes 32 values into out[0..31] in bit-reversed order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Dct32(Span<float> @out, ReadOnlySpan<float> tab)
    {
        float tmp0, tmp1;
        float val0, val1, val2, val3, val4, val5, val6, val7,
              val8, val9, val10, val11, val12, val13, val14, val15,
              val16, val17, val18, val19, val20, val21, val22, val23,
              val24, val25, val26, val27, val28, val29, val30, val31;

        // Pass 1
        tmp0 = tab[0] + tab[31]; tmp1 = tab[0] - tab[31]; val0 = tmp0; val31 = tmp1 * COS0_0;
        tmp0 = tab[15] + tab[16]; tmp1 = tab[15] - tab[16]; val15 = tmp0; val16 = tmp1 * COS0_15;
        // Pass 2
        tmp0 = val0 + val15; tmp1 = val0 - val15; val0 = tmp0; val15 = tmp1 * COS1_0;
        tmp0 = val16 + val31; tmp1 = val16 - val31; val16 = tmp0; val31 = tmp1 * (-COS1_0);
        // Pass 1
        tmp0 = tab[7] + tab[24]; tmp1 = tab[7] - tab[24]; val7 = tmp0; val24 = tmp1 * COS0_7;
        tmp0 = tab[8] + tab[23]; tmp1 = tab[8] - tab[23]; val8 = tmp0; val23 = tmp1 * COS0_8;
        // Pass 2
        tmp0 = val7 + val8; tmp1 = val7 - val8; val7 = tmp0; val8 = tmp1 * COS1_7;
        tmp0 = val23 + val24; tmp1 = val23 - val24; val23 = tmp0; val24 = tmp1 * (-COS1_7);
        // Pass 3
        tmp0 = val0 + val7; tmp1 = val0 - val7; val0 = tmp0; val7 = tmp1 * COS2_0;
        tmp0 = val8 + val15; tmp1 = val8 - val15; val8 = tmp0; val15 = tmp1 * (-COS2_0);
        tmp0 = val16 + val23; tmp1 = val16 - val23; val16 = tmp0; val23 = tmp1 * COS2_0;
        tmp0 = val24 + val31; tmp1 = val24 - val31; val24 = tmp0; val31 = tmp1 * (-COS2_0);
        // Pass 1
        tmp0 = tab[3] + tab[28]; tmp1 = tab[3] - tab[28]; val3 = tmp0; val28 = tmp1 * COS0_3;
        tmp0 = tab[12] + tab[19]; tmp1 = tab[12] - tab[19]; val12 = tmp0; val19 = tmp1 * COS0_12;
        // Pass 2
        tmp0 = val3 + val12; tmp1 = val3 - val12; val3 = tmp0; val12 = tmp1 * COS1_3;
        tmp0 = val19 + val28; tmp1 = val19 - val28; val19 = tmp0; val28 = tmp1 * (-COS1_3);
        // Pass 1
        tmp0 = tab[4] + tab[27]; tmp1 = tab[4] - tab[27]; val4 = tmp0; val27 = tmp1 * COS0_4;
        tmp0 = tab[11] + tab[20]; tmp1 = tab[11] - tab[20]; val11 = tmp0; val20 = tmp1 * COS0_11;
        // Pass 2
        tmp0 = val4 + val11; tmp1 = val4 - val11; val4 = tmp0; val11 = tmp1 * COS1_4;
        tmp0 = val20 + val27; tmp1 = val20 - val27; val20 = tmp0; val27 = tmp1 * (-COS1_4);
        // Pass 3
        tmp0 = val3 + val4; tmp1 = val3 - val4; val3 = tmp0; val4 = tmp1 * COS2_3;
        tmp0 = val11 + val12; tmp1 = val11 - val12; val11 = tmp0; val12 = tmp1 * (-COS2_3);
        tmp0 = val19 + val20; tmp1 = val19 - val20; val19 = tmp0; val20 = tmp1 * COS2_3;
        tmp0 = val27 + val28; tmp1 = val27 - val28; val27 = tmp0; val28 = tmp1 * (-COS2_3);
        // Pass 4
        tmp0 = val0 + val3; tmp1 = val0 - val3; val0 = tmp0; val3 = tmp1 * COS3_0;
        tmp0 = val4 + val7; tmp1 = val4 - val7; val4 = tmp0; val7 = tmp1 * (-COS3_0);
        tmp0 = val8 + val11; tmp1 = val8 - val11; val8 = tmp0; val11 = tmp1 * COS3_0;
        tmp0 = val12 + val15; tmp1 = val12 - val15; val12 = tmp0; val15 = tmp1 * (-COS3_0);
        tmp0 = val16 + val19; tmp1 = val16 - val19; val16 = tmp0; val19 = tmp1 * COS3_0;
        tmp0 = val20 + val23; tmp1 = val20 - val23; val20 = tmp0; val23 = tmp1 * (-COS3_0);
        tmp0 = val24 + val27; tmp1 = val24 - val27; val24 = tmp0; val27 = tmp1 * COS3_0;
        tmp0 = val28 + val31; tmp1 = val28 - val31; val28 = tmp0; val31 = tmp1 * (-COS3_0);

        // --- Second half: odd-indexed inputs ---
        // Pass 1
        tmp0 = tab[1] + tab[30]; tmp1 = tab[1] - tab[30]; val1 = tmp0; val30 = tmp1 * COS0_1;
        tmp0 = tab[14] + tab[17]; tmp1 = tab[14] - tab[17]; val14 = tmp0; val17 = tmp1 * COS0_14;
        // Pass 2
        tmp0 = val1 + val14; tmp1 = val1 - val14; val1 = tmp0; val14 = tmp1 * COS1_1;
        tmp0 = val17 + val30; tmp1 = val17 - val30; val17 = tmp0; val30 = tmp1 * (-COS1_1);
        // Pass 1
        tmp0 = tab[6] + tab[25]; tmp1 = tab[6] - tab[25]; val6 = tmp0; val25 = tmp1 * COS0_6;
        tmp0 = tab[9] + tab[22]; tmp1 = tab[9] - tab[22]; val9 = tmp0; val22 = tmp1 * COS0_9;
        // Pass 2
        tmp0 = val6 + val9; tmp1 = val6 - val9; val6 = tmp0; val9 = tmp1 * COS1_6;
        tmp0 = val22 + val25; tmp1 = val22 - val25; val22 = tmp0; val25 = tmp1 * (-COS1_6);
        // Pass 3
        tmp0 = val1 + val6; tmp1 = val1 - val6; val1 = tmp0; val6 = tmp1 * COS2_1;
        tmp0 = val9 + val14; tmp1 = val9 - val14; val9 = tmp0; val14 = tmp1 * (-COS2_1);
        tmp0 = val17 + val22; tmp1 = val17 - val22; val17 = tmp0; val22 = tmp1 * COS2_1;
        tmp0 = val25 + val30; tmp1 = val25 - val30; val25 = tmp0; val30 = tmp1 * (-COS2_1);

        // Pass 1
        tmp0 = tab[2] + tab[29]; tmp1 = tab[2] - tab[29]; val2 = tmp0; val29 = tmp1 * COS0_2;
        tmp0 = tab[13] + tab[18]; tmp1 = tab[13] - tab[18]; val13 = tmp0; val18 = tmp1 * COS0_13;
        // Pass 2
        tmp0 = val2 + val13; tmp1 = val2 - val13; val2 = tmp0; val13 = tmp1 * COS1_2;
        tmp0 = val18 + val29; tmp1 = val18 - val29; val18 = tmp0; val29 = tmp1 * (-COS1_2);
        // Pass 1
        tmp0 = tab[5] + tab[26]; tmp1 = tab[5] - tab[26]; val5 = tmp0; val26 = tmp1 * COS0_5;
        tmp0 = tab[10] + tab[21]; tmp1 = tab[10] - tab[21]; val10 = tmp0; val21 = tmp1 * COS0_10;
        // Pass 2
        tmp0 = val5 + val10; tmp1 = val5 - val10; val5 = tmp0; val10 = tmp1 * COS1_5;
        tmp0 = val21 + val26; tmp1 = val21 - val26; val21 = tmp0; val26 = tmp1 * (-COS1_5);
        // Pass 3
        tmp0 = val2 + val5; tmp1 = val2 - val5; val2 = tmp0; val5 = tmp1 * COS2_2;
        tmp0 = val10 + val13; tmp1 = val10 - val13; val10 = tmp0; val13 = tmp1 * (-COS2_2);
        tmp0 = val18 + val21; tmp1 = val18 - val21; val18 = tmp0; val21 = tmp1 * COS2_2;
        tmp0 = val26 + val29; tmp1 = val26 - val29; val26 = tmp0; val29 = tmp1 * (-COS2_2);
        // Pass 4
        tmp0 = val1 + val2; tmp1 = val1 - val2; val1 = tmp0; val2 = tmp1 * COS3_1;
        tmp0 = val5 + val6; tmp1 = val5 - val6; val5 = tmp0; val6 = tmp1 * (-COS3_1);
        tmp0 = val9 + val10; tmp1 = val9 - val10; val9 = tmp0; val10 = tmp1 * COS3_1;
        tmp0 = val13 + val14; tmp1 = val13 - val14; val13 = tmp0; val14 = tmp1 * (-COS3_1);
        tmp0 = val17 + val18; tmp1 = val17 - val18; val17 = tmp0; val18 = tmp1 * COS3_1;
        tmp0 = val21 + val22; tmp1 = val21 - val22; val21 = tmp0; val22 = tmp1 * (-COS3_1);
        tmp0 = val25 + val26; tmp1 = val25 - val26; val25 = tmp0; val26 = tmp1 * COS3_1;
        tmp0 = val29 + val30; tmp1 = val29 - val30; val29 = tmp0; val30 = tmp1 * (-COS3_1);

        // Pass 5: BF1/BF2 operations
        // BF1(0,1,2,3)
        tmp0 = val0 + val1; tmp1 = val0 - val1; val0 = tmp0; val1 = tmp1 * COS4_0;
        tmp0 = val2 + val3; tmp1 = val2 - val3; val2 = tmp0; val3 = tmp1 * (-COS4_0);
        val2 += val3;
        // BF2(4,5,6,7)
        tmp0 = val4 + val5; tmp1 = val4 - val5; val4 = tmp0; val5 = tmp1 * COS4_0;
        tmp0 = val6 + val7; tmp1 = val6 - val7; val6 = tmp0; val7 = tmp1 * (-COS4_0);
        val6 += val7; val4 += val6; val6 += val5; val5 += val7;
        // BF1(8,9,10,11)
        tmp0 = val8 + val9; tmp1 = val8 - val9; val8 = tmp0; val9 = tmp1 * COS4_0;
        tmp0 = val10 + val11; tmp1 = val10 - val11; val10 = tmp0; val11 = tmp1 * (-COS4_0);
        val10 += val11;
        // BF2(12,13,14,15)
        tmp0 = val12 + val13; tmp1 = val12 - val13; val12 = tmp0; val13 = tmp1 * COS4_0;
        tmp0 = val14 + val15; tmp1 = val14 - val15; val14 = tmp0; val15 = tmp1 * (-COS4_0);
        val14 += val15; val12 += val14; val14 += val13; val13 += val15;
        // BF1(16,17,18,19)
        tmp0 = val16 + val17; tmp1 = val16 - val17; val16 = tmp0; val17 = tmp1 * COS4_0;
        tmp0 = val18 + val19; tmp1 = val18 - val19; val18 = tmp0; val19 = tmp1 * (-COS4_0);
        val18 += val19;
        // BF2(20,21,22,23)
        tmp0 = val20 + val21; tmp1 = val20 - val21; val20 = tmp0; val21 = tmp1 * COS4_0;
        tmp0 = val22 + val23; tmp1 = val22 - val23; val22 = tmp0; val23 = tmp1 * (-COS4_0);
        val22 += val23; val20 += val22; val22 += val21; val21 += val23;
        // BF1(24,25,26,27)
        tmp0 = val24 + val25; tmp1 = val24 - val25; val24 = tmp0; val25 = tmp1 * COS4_0;
        tmp0 = val26 + val27; tmp1 = val26 - val27; val26 = tmp0; val27 = tmp1 * (-COS4_0);
        val26 += val27;
        // BF2(28,29,30,31)
        tmp0 = val28 + val29; tmp1 = val28 - val29; val28 = tmp0; val29 = tmp1 * COS4_0;
        tmp0 = val30 + val31; tmp1 = val30 - val31; val30 = tmp0; val31 = tmp1 * (-COS4_0);
        val30 += val31; val28 += val30; val30 += val29; val29 += val31;

        // Pass 6: ADD chain for first 16 outputs
        val8 += val12;
        val12 += val10;
        val10 += val14;
        val14 += val9;
        val9 += val13;
        val13 += val11;
        val11 += val15;

        @out[0]  = val0;
        @out[16] = val1;
        @out[8]  = val2;
        @out[24] = val3;
        @out[4]  = val4;
        @out[20] = val5;
        @out[12] = val6;
        @out[28] = val7;
        @out[2]  = val8;
        @out[18] = val9;
        @out[10] = val10;
        @out[26] = val11;
        @out[6]  = val12;
        @out[22] = val13;
        @out[14] = val14;
        @out[30] = val15;

        // Pass 6: ADD chain for last 16 outputs
        val24 += val28;
        val28 += val26;
        val26 += val30;
        val30 += val25;
        val25 += val29;
        val29 += val27;
        val27 += val31;

        @out[1]  = val16 + val24;
        @out[17] = val17 + val25;
        @out[9]  = val18 + val26;
        @out[25] = val19 + val27;
        @out[5]  = val20 + val28;
        @out[21] = val21 + val29;
        @out[13] = val22 + val30;
        @out[29] = val23 + val31;
        @out[3]  = val24 + val20;
        @out[19] = val25 + val21;
        @out[11] = val26 + val22;
        @out[27] = val27 + val23;
        @out[7]  = val28 + val18;
        @out[23] = val29 + val19;
        @out[15] = val30 + val17;
        @out[31] = val31;
    }
}

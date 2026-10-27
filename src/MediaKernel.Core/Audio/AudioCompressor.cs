// Audio dynamic range compressor
// Based on VLC's compressor.c (ported from LADSPA SWH SC4)

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Dynamic range compressor with soft knee, RMS/peak detection, and lookahead.
/// Reduces dynamic range by attenuating signals above a threshold.
/// </summary>
public sealed class AudioCompressor
{
    // Lookup table sizes
    private const int DbToLinearTableSize = 1024;
    private const int AttackTableSize = 256;
    private const float DbRange = 60.0f;
    
    // Lookahead and RMS buffer sizes
    private const int RmsBufferSize = 960;
    private const int LookaheadSize = 1920;
    
    // Pre-computed lookup tables
    private static readonly float[] DbToLinearTable = GenerateDbToLinearTable();
    private static readonly float[] LinearToDbTable = GenerateLinearToDbTable();

    // Parameters
    private float attackMilliseconds;
    private float releaseMilliseconds;
    private float thresholdDb;
    private float ratio;
    private float kneeDb;
    private float makeupGainDb;
    private float rmsPeakBlend;  // 0.0 = pure RMS, 1.0 = pure peak

    // Derived values
    private float attackCoefficient;
    private float releaseCoefficient;
    private float thresholdLinear;
    private float makeupGainLinear;

    // State per channel
    private readonly CompressorChannelState[] channelStates;
    private int sampleRate;
    private int channelCount;
    private readonly object syncLock = new();

    /// <summary>
    /// Attack time in milliseconds (1.5-400).
    /// </summary>
    public float AttackMilliseconds
    {
        get => attackMilliseconds;
        set
        {
            lock (syncLock)
            {
                attackMilliseconds = Math.Clamp(value, 1.5f, 400.0f);
                UpdateCoefficients();
            }
        }
    }

    /// <summary>
    /// Release time in milliseconds (2-800).
    /// </summary>
    public float ReleaseMilliseconds
    {
        get => releaseMilliseconds;
        set
        {
            lock (syncLock)
            {
                releaseMilliseconds = Math.Clamp(value, 2.0f, 800.0f);
                UpdateCoefficients();
            }
        }
    }

    /// <summary>
    /// Threshold in dB (-30 to 0).
    /// </summary>
    public float ThresholdDb
    {
        get => thresholdDb;
        set
        {
            lock (syncLock)
            {
                thresholdDb = Math.Clamp(value, -30.0f, 0.0f);
                thresholdLinear = DbToLinear(thresholdDb);
            }
        }
    }

    /// <summary>
    /// Compression ratio (1-20). Higher = more compression.
    /// </summary>
    public float Ratio
    {
        get => ratio;
        set
        {
            lock (syncLock)
            {
                ratio = Math.Clamp(value, 1.0f, 20.0f);
            }
        }
    }

    /// <summary>
    /// Knee width in dB (1-10). Larger = softer compression onset.
    /// </summary>
    public float KneeDb
    {
        get => kneeDb;
        set
        {
            lock (syncLock)
            {
                kneeDb = Math.Clamp(value, 1.0f, 10.0f);
            }
        }
    }

    /// <summary>
    /// Makeup gain in dB (0-24). Compensates for gain reduction.
    /// </summary>
    public float MakeupGainDb
    {
        get => makeupGainDb;
        set
        {
            lock (syncLock)
            {
                makeupGainDb = Math.Clamp(value, 0.0f, 24.0f);
                makeupGainLinear = DbToLinear(makeupGainDb);
            }
        }
    }

    /// <summary>
    /// RMS/Peak blend (0-1). 0 = pure RMS, 1 = pure peak.
    /// </summary>
    public float RmsPeakBlend
    {
        get => rmsPeakBlend;
        set
        {
            lock (syncLock)
            {
                rmsPeakBlend = Math.Clamp(value, 0.0f, 1.0f);
            }
        }
    }

    /// <summary>
    /// Current gain reduction in dB (for metering).
    /// </summary>
    public float CurrentGainReductionDb { get; private set; }

    /// <summary>
    /// Creates an audio compressor with default settings.
    /// </summary>
    public AudioCompressor(int sampleRate, int channelCount)
    {
        this.sampleRate = sampleRate;
        this.channelCount = channelCount;

        // Initialize channel states
        channelStates = new CompressorChannelState[channelCount];
        for (int ch = 0; ch < channelCount; ch++)
            channelStates[ch] = new CompressorChannelState();

        // Set defaults (matching VLC)
        attackMilliseconds = 25.0f;
        releaseMilliseconds = 100.0f;
        thresholdDb = -11.0f;
        ratio = 4.0f;
        kneeDb = 5.0f;
        makeupGainDb = 7.0f;
        rmsPeakBlend = 0.2f;

        UpdateCoefficients();
    }

    /// <summary>
    /// Applies one of the built-in presets.
    /// </summary>
    public void ApplyPreset(CompressorPreset preset)
    {
        lock (syncLock)
        {
            switch (preset)
            {
                case CompressorPreset.Gentle:
                    attackMilliseconds = 30.0f;
                    releaseMilliseconds = 200.0f;
                    thresholdDb = -10.0f;
                    ratio = 2.0f;
                    kneeDb = 6.0f;
                    makeupGainDb = 3.0f;
                    rmsPeakBlend = 0.2f;
                    break;

                case CompressorPreset.Voice:
                    attackMilliseconds = 10.0f;
                    releaseMilliseconds = 100.0f;
                    thresholdDb = -15.0f;
                    ratio = 4.0f;
                    kneeDb = 4.0f;
                    makeupGainDb = 8.0f;
                    rmsPeakBlend = 0.3f;
                    break;

                case CompressorPreset.Music:
                    attackMilliseconds = 25.0f;
                    releaseMilliseconds = 150.0f;
                    thresholdDb = -12.0f;
                    ratio = 3.0f;
                    kneeDb = 5.0f;
                    makeupGainDb = 6.0f;
                    rmsPeakBlend = 0.2f;
                    break;

                case CompressorPreset.Aggressive:
                    attackMilliseconds = 5.0f;
                    releaseMilliseconds = 50.0f;
                    thresholdDb = -20.0f;
                    ratio = 8.0f;
                    kneeDb = 3.0f;
                    makeupGainDb = 12.0f;
                    rmsPeakBlend = 0.5f;
                    break;

                case CompressorPreset.Limiter:
                    attackMilliseconds = 1.5f;
                    releaseMilliseconds = 30.0f;
                    thresholdDb = -1.0f;
                    ratio = 20.0f;
                    kneeDb = 1.0f;
                    makeupGainDb = 0.0f;
                    rmsPeakBlend = 1.0f;
                    break;
            }

            UpdateCoefficients();
        }
    }

    /// <summary>
    /// Processes audio samples in-place.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (samples.Length == 0)
            return;

        lock (syncLock)
        {
            int frameCount = samples.Length / channelCount;
            float maxGainReduction = 0.0f;

            for (int frame = 0; frame < frameCount; frame++)
            {
                // Calculate input level (sum across channels)
                float sumSquares = 0.0f;
                float peak = 0.0f;

                for (int ch = 0; ch < channelCount; ch++)
                {
                    float sample = samples[frame * channelCount + ch];
                    sumSquares += sample * sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                }

                float rms = MathF.Sqrt(sumSquares / channelCount);

                // Blend RMS and peak
                float inputLevel = rms + rmsPeakBlend * (peak - rms);

                // Apply envelope follower
                float envelopeLevel = UpdateEnvelope(inputLevel);

                // Calculate gain reduction
                float gainDb = CalculateGain(envelopeLevel);
                float gainLinear = DbToLinearFast(gainDb) * makeupGainLinear;

                maxGainReduction = Math.Min(maxGainReduction, gainDb);

                // Apply gain to all channels
                for (int ch = 0; ch < channelCount; ch++)
                {
                    samples[frame * channelCount + ch] *= gainLinear;
                }
            }

            CurrentGainReductionDb = -maxGainReduction;
        }
    }

    /// <summary>
    /// Processes audio from input to output buffer.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        input.CopyTo(output);
        Process(output);
    }

    /// <summary>
    /// Resets the compressor state.
    /// </summary>
    public void Reset()
    {
        lock (syncLock)
        {
            foreach (var state in channelStates)
                state.Reset();
            CurrentGainReductionDb = 0.0f;
        }
    }

    private void UpdateCoefficients()
    {
        // Attack and release coefficients (first-order low-pass)
        float attackSeconds = attackMilliseconds / 1000.0f;
        float releaseSeconds = releaseMilliseconds / 1000.0f;

        attackCoefficient = MathF.Exp(-1.0f / (attackSeconds * sampleRate));
        releaseCoefficient = MathF.Exp(-1.0f / (releaseSeconds * sampleRate));

        thresholdLinear = DbToLinear(thresholdDb);
        makeupGainLinear = DbToLinear(makeupGainDb);
    }

    // Shared envelope state (for linked stereo/multichannel)
    private float envelopeLevel;

    private float UpdateEnvelope(float inputLevel)
    {
        // Attack or release
        float coefficient = inputLevel > envelopeLevel ? attackCoefficient : releaseCoefficient;

        // First-order low-pass filter
        envelopeLevel = coefficient * envelopeLevel + (1.0f - coefficient) * inputLevel;

        // Prevent denormals
        if (envelopeLevel < 1e-10f)
            envelopeLevel = 0.0f;

        return envelopeLevel;
    }

    private float CalculateGain(float inputLevel)
    {
        if (inputLevel < 1e-10f)
            return 0.0f;

        float inputDb = LinearToDbFast(inputLevel);
        float outputDb = inputDb;

        // Soft knee implementation
        float kneeStart = thresholdDb - kneeDb;
        float kneeEnd = thresholdDb + kneeDb;

        if (inputDb < kneeStart)
        {
            // Below knee - no compression
            outputDb = inputDb;
        }
        else if (inputDb > kneeEnd)
        {
            // Above knee - full compression
            outputDb = thresholdDb + (inputDb - thresholdDb) / ratio;
        }
        else
        {
            // In knee region - soft transition (parabolic)
            float x = (inputDb - kneeStart) / (2.0f * kneeDb);
            float compressionAmount = (ratio - 1.0f) / ratio;
            outputDb = inputDb - compressionAmount * kneeDb * x * x;
        }

        return outputDb - inputDb; // Gain reduction in dB
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DbToLinear(float db)
    {
        return MathF.Pow(10.0f, db / 20.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToDb(float linear)
    {
        if (linear < 1e-10f)
            return -120.0f;
        return 20.0f * MathF.Log10(linear);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DbToLinearFast(float db)
    {
        // Use lookup table for fast conversion
        float index = (db + DbRange) * (DbToLinearTableSize - 1) / (2.0f * DbRange);
        int i = (int)index;
        if (i < 0) return DbToLinearTable[0];
        if (i >= DbToLinearTableSize - 1) return DbToLinearTable[DbToLinearTableSize - 1];
        float frac = index - i;
        return DbToLinearTable[i] + frac * (DbToLinearTable[i + 1] - DbToLinearTable[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToDbFast(float linear)
    {
        if (linear < 1e-10f)
            return -DbRange;
        // Use lookup table with linear interpolation
        float index = linear * (DbToLinearTableSize - 1);
        if (index >= DbToLinearTableSize - 1)
            return 20.0f * MathF.Log10(linear);
        int i = (int)index;
        float frac = index - i;
        return LinearToDbTable[i] + frac * (LinearToDbTable[i + 1] - LinearToDbTable[i]);
    }

    private static float[] GenerateDbToLinearTable()
    {
        var table = new float[DbToLinearTableSize];
        for (int i = 0; i < DbToLinearTableSize; i++)
        {
            float db = -DbRange + (2.0f * DbRange * i) / (DbToLinearTableSize - 1);
            table[i] = MathF.Pow(10.0f, db / 20.0f);
        }
        return table;
    }

    private static float[] GenerateLinearToDbTable()
    {
        var table = new float[DbToLinearTableSize];
        for (int i = 0; i < DbToLinearTableSize; i++)
        {
            float linear = (float)i / (DbToLinearTableSize - 1);
            table[i] = linear < 1e-10f ? -DbRange : 20.0f * MathF.Log10(linear);
        }
        return table;
    }

    /// <summary>
    /// Per-channel compressor state.
    /// </summary>
    private sealed class CompressorChannelState
    {
        public float EnvelopeRms;
        public float EnvelopePeak;
        public float[] LookaheadBuffer = new float[LookaheadSize];
        public int LookaheadPosition;
        public float[] RmsBuffer = new float[RmsBufferSize];
        public int RmsPosition;
        public float RmsSum;

        public void Reset()
        {
            EnvelopeRms = 0.0f;
            EnvelopePeak = 0.0f;
            LookaheadPosition = 0;
            RmsPosition = 0;
            RmsSum = 0.0f;
            Array.Clear(LookaheadBuffer);
            Array.Clear(RmsBuffer);
        }
    }
}

/// <summary>
/// Built-in compressor presets.
/// </summary>
public enum CompressorPreset
{
    /// <summary>Gentle compression for subtle leveling.</summary>
    Gentle,
    
    /// <summary>Voice/podcast compression.</summary>
    Voice,
    
    /// <summary>Music mastering compression.</summary>
    Music,
    
    /// <summary>Heavy compression for loud/punchy sound.</summary>
    Aggressive,
    
    /// <summary>Brick-wall limiter to prevent clipping.</summary>
    Limiter
}

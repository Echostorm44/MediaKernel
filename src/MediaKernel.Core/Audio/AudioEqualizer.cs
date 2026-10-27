// AudioEqualizer - 10-band parametric equalizer with IIR biquad filters
// Ported from VLC's equalizer.c with C# idioms and SIMD optimization

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// 10-band audio equalizer using second-order IIR (biquad) peaking filters.
/// Supports VLC and ISO standard frequency bands, presets, and real-time gain adjustment.
/// </summary>
public sealed class AudioEqualizer
{
    // VLC standard frequencies (Hz)
    private static readonly float[] VlcFrequencies = 
    { 
        60f, 170f, 310f, 600f, 1000f, 3000f, 6000f, 12000f, 14000f, 16000f 
    };
    
    // ISO standard frequencies (Hz)
    private static readonly float[] IsoFrequencies = 
    { 
        31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f 
    };
    
    private const int BandCount = 10;
    private const int MaxChannels = 8;
    private const float EqzInFactor = 0.25f;
    private const float MinGainDb = -20f;
    private const float MaxGainDb = 20f;
    
    private readonly EqualizerBand[] bands = new EqualizerBand[BandCount];
    private readonly float[] bandGains = new float[BandCount]; // Linear gains
    
    // State variables: [channel, band, history_index]
    private readonly float[,,] xHistory = new float[MaxChannels, BandCount, 2];
    private readonly float[,,] yHistory = new float[MaxChannels, BandCount, 2];
    
    private float preampGain = 1.0f;
    private int sampleRate;
    private int channelCount = 2;
    private bool twoPassMode;
    private bool isEnabled = true;
    
    private readonly object lockObject = new();
    
    /// <summary>Gets or sets the preamp gain in dB (-20 to +20).</summary>
    public float PreampDb
    {
        get => LinearToDb(preampGain);
        set => preampGain = DbToLinear(Math.Clamp(value, MinGainDb, MaxGainDb));
    }
    
    /// <summary>Gets or sets whether the equalizer is enabled.</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set => isEnabled = value;
    }
    
    /// <summary>Gets or sets two-pass filtering mode for increased effect intensity.</summary>
    public bool TwoPassMode
    {
        get => twoPassMode;
        set => twoPassMode = value;
    }
    
    /// <summary>Gets the center frequencies for all bands.</summary>
    public ReadOnlySpan<float> BandFrequencies
    {
        get
        {
            var frequencies = new float[BandCount];
            for (int i = 0; i < BandCount; i++)
                frequencies[i] = bands[i].Frequency;
            return frequencies;
        }
    }
    
    /// <summary>
    /// Creates a new audio equalizer with the specified sample rate.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="useIsoFrequencies">Use ISO standard frequencies instead of VLC frequencies.</param>
    public AudioEqualizer(int sampleRate, int channels = 2, bool useIsoFrequencies = false)
    {
        this.sampleRate = sampleRate;
        this.channelCount = Math.Min(channels, MaxChannels);
        
        var frequencies = useIsoFrequencies ? IsoFrequencies : VlcFrequencies;
        
        for (int i = 0; i < BandCount; i++)
        {
            bands[i] = new EqualizerBand { Frequency = frequencies[i] };
            ComputeCoefficients(i);
            bandGains[i] = 0f; // Flat by default
        }
        
        Reset();
    }
    
    /// <summary>
    /// Sets the gain for a specific band.
    /// </summary>
    /// <param name="bandIndex">Band index (0-9).</param>
    /// <param name="gainDb">Gain in decibels (-20 to +20).</param>
    public void SetBandGain(int bandIndex, float gainDb)
    {
        if (bandIndex < 0 || bandIndex >= BandCount)
            return;
        
        lock (lockObject)
        {
            float clampedDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
            // Convert dB to linear amplitude factor
            // VLC formula: EQZ_IN_FACTOR * (10^(dB/20) - 1)
            bandGains[bandIndex] = EqzInFactor * (MathF.Pow(10f, clampedDb / 20f) - 1f);
        }
    }
    
    /// <summary>
    /// Gets the gain for a specific band in dB.
    /// </summary>
    public float GetBandGain(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= BandCount)
            return 0f;
        
        // Reverse the VLC formula
        float amp = bandGains[bandIndex] / EqzInFactor + 1f;
        return 20f * MathF.Log10(amp);
    }
    
    /// <summary>
    /// Sets gains for all bands at once.
    /// </summary>
    /// <param name="gains">Array of 10 gain values in dB.</param>
    public void SetAllBandGains(ReadOnlySpan<float> gains)
    {
        lock (lockObject)
        {
            int count = Math.Min(gains.Length, BandCount);
            for (int i = 0; i < count; i++)
            {
                float clampedDb = Math.Clamp(gains[i], MinGainDb, MaxGainDb);
                bandGains[i] = EqzInFactor * (MathF.Pow(10f, clampedDb / 20f) - 1f);
            }
        }
    }
    
    /// <summary>
    /// Applies a named preset.
    /// </summary>
    /// <param name="preset">Preset name.</param>
    /// <returns>True if preset was found and applied.</returns>
    public bool ApplyPreset(EqualizerPreset preset)
    {
        var gains = GetPresetGains(preset);
        if (gains == null)
            return false;
        
        SetAllBandGains(gains);
        PreampDb = GetPresetPreamp(preset);
        return true;
    }
    
    /// <summary>
    /// Resets the equalizer to flat response.
    /// </summary>
    public void SetFlat()
    {
        lock (lockObject)
        {
            for (int i = 0; i < BandCount; i++)
                bandGains[i] = 0f;
            preampGain = 1f;
        }
    }
    
    /// <summary>
    /// Resets all filter state (call after seek or sample rate change).
    /// </summary>
    public void Reset()
    {
        lock (lockObject)
        {
            Array.Clear(xHistory);
            Array.Clear(yHistory);
        }
    }
    
    /// <summary>
    /// Updates the sample rate and recomputes filter coefficients.
    /// </summary>
    public void SetSampleRate(int newSampleRate)
    {
        if (newSampleRate == sampleRate)
            return;
        
        lock (lockObject)
        {
            sampleRate = newSampleRate;
            for (int i = 0; i < BandCount; i++)
                ComputeCoefficients(i);
            Reset();
        }
    }
    
    /// <summary>
    /// Processes audio samples in-place.
    /// </summary>
    /// <param name="samples">Interleaved audio samples.</param>
    public void Process(Span<float> samples)
    {
        if (!isEnabled || samples.Length == 0)
            return;
        
        lock (lockObject)
        {
            int sampleCount = samples.Length / channelCount;
            
            for (int s = 0; s < sampleCount; s++)
            {
                int baseIndex = s * channelCount;
                
                for (int ch = 0; ch < channelCount; ch++)
                {
                    float input = samples[baseIndex + ch];
                    float output = ProcessSample(input, ch);
                    
                    if (twoPassMode)
                    {
                        // Second pass for increased effect
                        output = ProcessSample(output, ch);
                    }
                    
                    samples[baseIndex + ch] = output;
                }
            }
        }
    }
    
    /// <summary>
    /// Processes a single sample for one channel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessSample(float input, int channel)
    {
        float filtered = 0f;
        
        for (int i = 0; i < BandCount; i++)
        {
            ref var band = ref bands[i];
            
            // Get history references
            float x0 = input;
            float x1 = xHistory[channel, i, 0];
            float x2 = xHistory[channel, i, 1];
            float y1 = yHistory[channel, i, 0];
            float y2 = yHistory[channel, i, 1];
            
            // IIR filter: y = alpha*(x - x[n-2]) + gamma*y[n-1] - beta*y[n-2]
            float y0 = band.Alpha * (x0 - x2) + band.Gamma * y1 - band.Beta * y2;
            
            // Update history
            xHistory[channel, i, 1] = x1;
            xHistory[channel, i, 0] = x0;
            yHistory[channel, i, 1] = y1;
            yHistory[channel, i, 0] = y0;
            
            // Accumulate filtered output with band gain
            filtered += y0 * bandGains[i];
        }
        
        // Mix: output = preamp * (0.25*input + filtered)
        return preampGain * (EqzInFactor * input + filtered);
    }
    
    /// <summary>
    /// Computes biquad filter coefficients for a band.
    /// </summary>
    private void ComputeCoefficients(int bandIndex)
    {
        ref var band = ref bands[bandIndex];
        float frequency = band.Frequency;
        
        if (sampleRate <= 0 || frequency <= 0)
            return;
        
        // Ensure frequency is below Nyquist
        frequency = Math.Min(frequency, sampleRate / 2f - 1f);
        
        // Angular frequency
        float omega = 2f * MathF.PI * frequency / sampleRate;
        
        // Q factor (bandwidth = 1 octave)
        // For peaking filter: Q determines the bandwidth
        float q = 1f / (2f * MathF.Sinh(MathF.Log(2f) / 2f));
        
        // Compute intermediate values
        float sinOmega = MathF.Sin(omega);
        float cosOmega = MathF.Cos(omega);
        float alpha = sinOmega / (2f * q);
        
        // Biquad coefficients for peaking filter at unity gain
        // These are transformed to the VLC-style alpha/beta/gamma
        float a0 = 1f + alpha;
        float a1 = -2f * cosOmega;
        float a2 = 1f - alpha;
        float b0 = alpha;
        float b2 = -alpha;
        
        // Normalize and convert to VLC format
        band.Alpha = b0 / a0;
        band.Beta = a2 / a0;
        band.Gamma = -a1 / a0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToDb(float linear) => 20f * MathF.Log10(linear);
    
    /// <summary>
    /// Gets the gain values for a preset.
    /// </summary>
    private static float[]? GetPresetGains(EqualizerPreset preset) => preset switch
    {
        EqualizerPreset.Flat => new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        EqualizerPreset.Classical => new float[] { 0, 0, 0, 0, 0, 0, -7.2f, -7.2f, -7.2f, -9.6f },
        EqualizerPreset.Club => new float[] { 0, 0, 8, 5.6f, 5.6f, 5.6f, 3.2f, 0, 0, 0 },
        EqualizerPreset.Dance => new float[] { 9.6f, 7.2f, 2.4f, 0, 0, -5.6f, -7.2f, -7.2f, 0, 0 },
        EqualizerPreset.FullBass => new float[] { -8, 9.6f, 9.6f, 5.6f, 1.6f, -4, -8, -10.4f, -11.2f, -11.2f },
        EqualizerPreset.FullBassTreble => new float[] { 7.2f, 5.6f, 0, -7.2f, -4.8f, 1.6f, 8, 11.2f, 12, 12 },
        EqualizerPreset.FullTreble => new float[] { -9.6f, -9.6f, -9.6f, -4, 2.4f, 11.2f, 16, 16, 16, 16.8f },
        EqualizerPreset.Headphones => new float[] { 4.8f, 11.2f, 5.6f, -3.2f, -2.4f, 1.6f, 4.8f, 9.6f, 12.8f, 14.4f },
        EqualizerPreset.LargeHall => new float[] { 10.4f, 10.4f, 5.6f, 5.6f, 0, -4.8f, -4.8f, -4.8f, 0, 0 },
        EqualizerPreset.Live => new float[] { -4.8f, 0, 4, 5.6f, 5.6f, 5.6f, 4, 2.4f, 2.4f, 2.4f },
        EqualizerPreset.Party => new float[] { 7.2f, 7.2f, 0, 0, 0, 0, 0, 0, 7.2f, 7.2f },
        EqualizerPreset.Pop => new float[] { -1.6f, 4.8f, 7.2f, 8, 5.6f, 0, -2.4f, -2.4f, -1.6f, -1.6f },
        EqualizerPreset.Reggae => new float[] { 0, 0, 0, -5.6f, 0, 6.4f, 6.4f, 0, 0, 0 },
        EqualizerPreset.Rock => new float[] { 8, 4.8f, -5.6f, -8, -3.2f, 4, 8.8f, 11.2f, 11.2f, 11.2f },
        EqualizerPreset.Ska => new float[] { -2.4f, -4.8f, -4, 0, 4, 5.6f, 8.8f, 9.6f, 11.2f, 9.6f },
        EqualizerPreset.Soft => new float[] { 4.8f, 1.6f, 0, -2.4f, 0, 4, 8, 9.6f, 11.2f, 12 },
        EqualizerPreset.SoftRock => new float[] { 4, 4, 2.4f, 0, -4, -5.6f, -3.2f, 0, 2.4f, 8.8f },
        EqualizerPreset.Techno => new float[] { 8, 5.6f, 0, -5.6f, -4.8f, 0, 8, 9.6f, 9.6f, 8.8f },
        _ => null
    };
    
    /// <summary>
    /// Gets the preamp value for a preset.
    /// </summary>
    private static float GetPresetPreamp(EqualizerPreset preset) => preset switch
    {
        EqualizerPreset.Classical => 0f,
        EqualizerPreset.Club => 0f,
        EqualizerPreset.Dance => 0f,
        EqualizerPreset.FullBass => 0f,
        EqualizerPreset.FullBassTreble => 0f,
        EqualizerPreset.FullTreble => 0f,
        EqualizerPreset.Headphones => 0f,
        EqualizerPreset.LargeHall => 0f,
        EqualizerPreset.Live => 0f,
        EqualizerPreset.Party => 0f,
        EqualizerPreset.Pop => 0f,
        EqualizerPreset.Reggae => 0f,
        EqualizerPreset.Rock => 0f,
        EqualizerPreset.Ska => 0f,
        EqualizerPreset.Soft => 0f,
        EqualizerPreset.SoftRock => 0f,
        EqualizerPreset.Techno => 0f,
        _ => 0f
    };
}

/// <summary>
/// Internal band state and coefficients.
/// </summary>
internal struct EqualizerBand
{
    /// <summary>Center frequency in Hz.</summary>
    public float Frequency;
    
    /// <summary>Alpha coefficient for IIR filter.</summary>
    public float Alpha;
    
    /// <summary>Beta coefficient for IIR filter.</summary>
    public float Beta;
    
    /// <summary>Gamma coefficient for IIR filter.</summary>
    public float Gamma;
}

/// <summary>
/// Built-in equalizer presets matching VLC.
/// </summary>
public enum EqualizerPreset
{
    Flat,
    Classical,
    Club,
    Dance,
    FullBass,
    FullBassTreble,
    FullTreble,
    Headphones,
    LargeHall,
    Live,
    Party,
    Pop,
    Reggae,
    Rock,
    Ska,
    Soft,
    SoftRock,
    Techno
}

/// <summary>
/// Parametric equalizer with shelving filters and adjustable peak bands.
/// Based on VLC's param_eq.c - provides low/high shelving plus 3 parametric peak bands.
/// </summary>
public sealed class ParametricEqualizer
{
    private const int MaxChannels = 8;
    
    private float lowFrequency = 100f;
    private float lowGainDb = 0f;
    private float highFrequency = 10000f;
    private float highGainDb = 0f;
    
    private readonly float[] peakFrequencies = { 300f, 1000f, 3000f };
    private readonly float[] peakGainsDb = { 0f, 0f, 0f };
    private readonly float[] peakQs = { 3f, 3f, 3f };
    
    // Filter coefficients: a0, a1, a2, b0, b1, b2
    private readonly float[] lowShelfCoeffs = new float[6];
    private readonly float[] highShelfCoeffs = new float[6];
    private readonly float[][] peakCoeffs = new float[3][];
    
    // Filter states: x[n-1], x[n-2], y[n-1], y[n-2] per channel
    private readonly float[,] lowShelfState = new float[MaxChannels, 4];
    private readonly float[,] highShelfState = new float[MaxChannels, 4];
    private readonly float[,,] peakState = new float[3, MaxChannels, 4];
    
    private int sampleRate = 48000;
    private bool coeffsNeedUpdate = true;
    
    public ParametricEqualizer()
    {
        for (int i = 0; i < 3; i++)
            peakCoeffs[i] = new float[6];
    }
    
    /// <summary>Low shelf frequency in Hz.</summary>
    public float LowFrequency
    {
        get => lowFrequency;
        set { lowFrequency = Math.Clamp(value, 20f, 500f); coeffsNeedUpdate = true; }
    }
    
    /// <summary>Low shelf gain in dB (-20 to +20).</summary>
    public float LowGainDb
    {
        get => lowGainDb;
        set { lowGainDb = Math.Clamp(value, -20f, 20f); coeffsNeedUpdate = true; }
    }
    
    /// <summary>High shelf frequency in Hz.</summary>
    public float HighFrequency
    {
        get => highFrequency;
        set { highFrequency = Math.Clamp(value, 1000f, 20000f); coeffsNeedUpdate = true; }
    }
    
    /// <summary>High shelf gain in dB (-20 to +20).</summary>
    public float HighGainDb
    {
        get => highGainDb;
        set { highGainDb = Math.Clamp(value, -20f, 20f); coeffsNeedUpdate = true; }
    }
    
    /// <summary>Set peak band parameters.</summary>
    /// <param name="band">Band index (0-2).</param>
    /// <param name="frequency">Center frequency in Hz.</param>
    /// <param name="gainDb">Gain in dB (-20 to +20).</param>
    /// <param name="q">Q factor (0.1 to 100).</param>
    public void SetPeakBand(int band, float frequency, float gainDb, float q)
    {
        if (band < 0 || band >= 3) return;
        peakFrequencies[band] = Math.Clamp(frequency, 20f, 20000f);
        peakGainsDb[band] = Math.Clamp(gainDb, -20f, 20f);
        peakQs[band] = Math.Clamp(q, 0.1f, 100f);
        coeffsNeedUpdate = true;
    }
    
    /// <summary>Get peak band frequency.</summary>
    public float GetPeakFrequency(int band) => band >= 0 && band < 3 ? peakFrequencies[band] : 0f;
    
    /// <summary>Get peak band gain.</summary>
    public float GetPeakGainDb(int band) => band >= 0 && band < 3 ? peakGainsDb[band] : 0f;
    
    /// <summary>Get peak band Q.</summary>
    public float GetPeakQ(int band) => band >= 0 && band < 3 ? peakQs[band] : 0f;
    
    /// <summary>Initialize with sample rate.</summary>
    public void Initialize(int sampleRate)
    {
        this.sampleRate = sampleRate;
        coeffsNeedUpdate = true;
        Reset();
    }
    
    /// <summary>Reset all filter states.</summary>
    public void Reset()
    {
        Array.Clear(lowShelfState);
        Array.Clear(highShelfState);
        Array.Clear(peakState);
    }
    
    /// <summary>Process audio samples in place.</summary>
    /// <param name="samples">Interleaved audio samples.</param>
    /// <param name="channelCount">Number of channels.</param>
    public void Process(Span<float> samples, int channelCount)
    {
        if (coeffsNeedUpdate)
        {
            UpdateCoefficients();
            coeffsNeedUpdate = false;
        }
        
        int frameCount = samples.Length / channelCount;
        
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < channelCount && ch < MaxChannels; ch++)
            {
                int idx = frame * channelCount + ch;
                float sample = samples[idx];
                
                // Apply low shelf
                sample = ProcessBiquad(sample, lowShelfCoeffs, lowShelfState, ch);
                
                // Apply peak bands
                for (int band = 0; band < 3; band++)
                {
                    sample = ProcessBiquadPeak(sample, peakCoeffs[band], peakState, band, ch);
                }
                
                // Apply high shelf
                sample = ProcessBiquad(sample, highShelfCoeffs, highShelfState, ch);
                
                samples[idx] = sample;
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessBiquad(float input, float[] coeffs, float[,] state, int channel)
    {
        // coeffs: a0, a1, a2, b0, b1, b2 (normalized so a0=1)
        float x0 = input;
        float x1 = state[channel, 0];
        float x2 = state[channel, 1];
        float y1 = state[channel, 2];
        float y2 = state[channel, 3];
        
        float output = coeffs[3] * x0 + coeffs[4] * x1 + coeffs[5] * x2 
                      - coeffs[1] * y1 - coeffs[2] * y2;
        
        state[channel, 1] = x1;
        state[channel, 0] = x0;
        state[channel, 3] = y1;
        state[channel, 2] = output;
        
        return output;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessBiquadPeak(float input, float[] coeffs, float[,,] state, int band, int channel)
    {
        float x0 = input;
        float x1 = state[band, channel, 0];
        float x2 = state[band, channel, 1];
        float y1 = state[band, channel, 2];
        float y2 = state[band, channel, 3];
        
        float output = coeffs[3] * x0 + coeffs[4] * x1 + coeffs[5] * x2 
                      - coeffs[1] * y1 - coeffs[2] * y2;
        
        state[band, channel, 1] = x1;
        state[band, channel, 0] = x0;
        state[band, channel, 3] = y1;
        state[band, channel, 2] = output;
        
        return output;
    }
    
    private void UpdateCoefficients()
    {
        CalculateShelfCoeffs(lowFrequency, lowGainDb, true, lowShelfCoeffs);
        CalculateShelfCoeffs(highFrequency, highGainDb, false, highShelfCoeffs);
        
        for (int i = 0; i < 3; i++)
        {
            CalculatePeakCoeffs(peakFrequencies[i], peakGainsDb[i], peakQs[i], peakCoeffs[i]);
        }
    }
    
    /// <summary>
    /// Calculate low/high shelf filter coefficients.
    /// </summary>
    private void CalculateShelfCoeffs(float frequency, float gainDb, bool isLowShelf, float[] coeffs)
    {
        float w0 = 2.0f * MathF.PI * frequency / sampleRate;
        float A = MathF.Pow(10.0f, gainDb / 40.0f);
        float cosW0 = MathF.Cos(w0);
        float sinW0 = MathF.Sin(w0);
        float alpha = sinW0 / 2.0f * MathF.Sqrt((A + 1.0f / A) * 2.0f);
        float sqrtA = MathF.Sqrt(A);
        
        float a0, a1, a2, b0, b1, b2;
        
        if (isLowShelf)
        {
            b0 = A * ((A + 1) - (A - 1) * cosW0 + 2 * sqrtA * alpha);
            b1 = 2 * A * ((A - 1) - (A + 1) * cosW0);
            b2 = A * ((A + 1) - (A - 1) * cosW0 - 2 * sqrtA * alpha);
            a0 = (A + 1) + (A - 1) * cosW0 + 2 * sqrtA * alpha;
            a1 = -2 * ((A - 1) + (A + 1) * cosW0);
            a2 = (A + 1) + (A - 1) * cosW0 - 2 * sqrtA * alpha;
        }
        else
        {
            b0 = A * ((A + 1) + (A - 1) * cosW0 + 2 * sqrtA * alpha);
            b1 = -2 * A * ((A - 1) + (A + 1) * cosW0);
            b2 = A * ((A + 1) + (A - 1) * cosW0 - 2 * sqrtA * alpha);
            a0 = (A + 1) - (A - 1) * cosW0 + 2 * sqrtA * alpha;
            a1 = 2 * ((A - 1) - (A + 1) * cosW0);
            a2 = (A + 1) - (A - 1) * cosW0 - 2 * sqrtA * alpha;
        }
        
        // Normalize
        coeffs[0] = 1.0f;
        coeffs[1] = a1 / a0;
        coeffs[2] = a2 / a0;
        coeffs[3] = b0 / a0;
        coeffs[4] = b1 / a0;
        coeffs[5] = b2 / a0;
    }
    
    /// <summary>
    /// Calculate peak/notch filter coefficients.
    /// </summary>
    private void CalculatePeakCoeffs(float frequency, float gainDb, float q, float[] coeffs)
    {
        float w0 = 2.0f * MathF.PI * frequency / sampleRate;
        float A = MathF.Pow(10.0f, gainDb / 40.0f);
        float cosW0 = MathF.Cos(w0);
        float sinW0 = MathF.Sin(w0);
        float alpha = sinW0 / (2.0f * q);
        
        float b0 = 1 + alpha * A;
        float b1 = -2 * cosW0;
        float b2 = 1 - alpha * A;
        float a0 = 1 + alpha / A;
        float a1 = -2 * cosW0;
        float a2 = 1 - alpha / A;
        
        // Normalize
        coeffs[0] = 1.0f;
        coeffs[1] = a1 / a0;
        coeffs[2] = a2 / a0;
        coeffs[3] = b0 / a0;
        coeffs[4] = b1 / a0;
        coeffs[5] = b2 / a0;
    }
}

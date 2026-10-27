// ReplayGain Normalizer - Applies ReplayGain metadata for volume normalization
// Based on VLC's replay_gain.c - calculates multiplier from track/album gain with peak protection

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// ReplayGain metadata for audio normalization.
/// Parsed from ID3v2, Vorbis Comments, or APE tags.
/// </summary>
public sealed class ReplayGainInfo
{
    /// <summary>
    /// Track gain in decibels (loudness adjustment for single track).
    /// </summary>
    public float? TrackGainDb { get; set; }
    
    /// <summary>
    /// Track peak (0.0 to 1.0+, maximum sample value).
    /// </summary>
    public float? TrackPeak { get; set; }
    
    /// <summary>
    /// Album gain in decibels (loudness adjustment for entire album).
    /// </summary>
    public float? AlbumGainDb { get; set; }
    
    /// <summary>
    /// Album peak (0.0 to 1.0+, maximum sample value).
    /// </summary>
    public float? AlbumPeak { get; set; }
    
    /// <summary>
    /// Reference loudness in LUFS (standard is -18 LUFS for ReplayGain).
    /// </summary>
    public float? ReferenceLoudnessLufs { get; set; }
    
    /// <summary>
    /// Returns true if any gain information is available.
    /// </summary>
    public bool HasGain => TrackGainDb.HasValue || AlbumGainDb.HasValue;
    
    /// <summary>
    /// Returns true if any peak information is available.
    /// </summary>
    public bool HasPeak => TrackPeak.HasValue || AlbumPeak.HasValue;
}

/// <summary>
/// Mode for selecting which ReplayGain value to use.
/// </summary>
public enum ReplayGainMode
{
    /// <summary>
    /// Disable ReplayGain - no normalization applied.
    /// </summary>
    None,
    
    /// <summary>
    /// Use track gain for consistent per-track loudness.
    /// Best for shuffle playback or mixed playlists.
    /// </summary>
    Track,
    
    /// <summary>
    /// Use album gain to preserve relative loudness within albums.
    /// Best for listening to complete albums.
    /// </summary>
    Album
}

/// <summary>
/// Applies ReplayGain normalization to audio based on metadata.
/// 
/// Features:
/// - Track and album gain modes
/// - Peak protection to prevent clipping
/// - Pre-amp adjustment for louder/quieter output
/// - Default gain for files without ReplayGain
/// - SIMD-optimized processing
/// </summary>
public sealed class ReplayGainNormalizer
{
    // Standard ReplayGain reference loudness
    private const float StandardReferenceLufs = -18.0f;
    
    // Default peak value when not specified
    private const float DefaultPeak = 1.0f;
    
    // Current configuration
    private ReplayGainMode mode = ReplayGainMode.Track;
    private float preampGainDb = 0.0f;
    private float defaultGainDb = 0.0f;
    private bool enablePeakProtection = true;
    
    // Cached gain multiplier
    private float currentMultiplier = 1.0f;
    private bool hasReplayGainData;
    
    /// <summary>
    /// Gets or sets the ReplayGain mode.
    /// </summary>
    public ReplayGainMode Mode
    {
        get => mode;
        set
        {
            mode = value;
            InvalidateCache();
        }
    }
    
    /// <summary>
    /// Gets or sets the pre-amp gain in decibels (-20 to +20 dB).
    /// Added to the calculated ReplayGain value.
    /// Positive values make output louder, negative values quieter.
    /// </summary>
    public float PreampGainDb
    {
        get => preampGainDb;
        set
        {
            preampGainDb = Math.Clamp(value, -20.0f, 20.0f);
            InvalidateCache();
        }
    }
    
    /// <summary>
    /// Gets or sets the default gain in decibels (-20 to +20 dB).
    /// Applied when no ReplayGain metadata is available.
    /// </summary>
    public float DefaultGainDb
    {
        get => defaultGainDb;
        set
        {
            defaultGainDb = Math.Clamp(value, -20.0f, 20.0f);
            InvalidateCache();
        }
    }
    
    /// <summary>
    /// Gets or sets whether peak protection is enabled.
    /// When enabled, gain is limited to prevent clipping based on peak values.
    /// </summary>
    public bool EnablePeakProtection
    {
        get => enablePeakProtection;
        set
        {
            enablePeakProtection = value;
            InvalidateCache();
        }
    }
    
    /// <summary>
    /// Gets the current ReplayGain info being used.
    /// </summary>
    public ReplayGainInfo? CurrentInfo { get; private set; }
    
    /// <summary>
    /// Gets the calculated gain multiplier being applied.
    /// </summary>
    public float CurrentMultiplier => currentMultiplier;
    
    /// <summary>
    /// Gets the effective gain in decibels being applied.
    /// </summary>
    public float EffectiveGainDb => LinearToDecibels(currentMultiplier);
    
    /// <summary>
    /// Gets whether the current track has ReplayGain data.
    /// </summary>
    public bool HasReplayGainData => hasReplayGainData;

    /// <summary>
    /// Sets the ReplayGain info for the current track.
    /// </summary>
    /// <param name="info">ReplayGain metadata, or null if not available.</param>
    public void SetReplayGainInfo(ReplayGainInfo? info)
    {
        CurrentInfo = info;
        CalculateMultiplier();
    }
    
    /// <summary>
    /// Clears the current ReplayGain info (for track changes).
    /// </summary>
    public void ClearReplayGainInfo()
    {
        CurrentInfo = null;
        currentMultiplier = DecibelsToLinear(defaultGainDb);
        hasReplayGainData = false;
    }
    
    /// <summary>
    /// Processes audio samples with ReplayGain normalization.
    /// </summary>
    /// <param name="samples">Audio samples (modified in place).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples)
    {
        if (mode == ReplayGainMode.None || Math.Abs(currentMultiplier - 1.0f) < 0.0001f)
            return;
        
        if (Avx.IsSupported && samples.Length >= 8)
        {
            ProcessAvx(samples);
        }
        else if (Sse.IsSupported && samples.Length >= 4)
        {
            ProcessSse(samples);
        }
        else
        {
            ProcessScalar(samples);
        }
    }
    
    /// <summary>
    /// Scalar processing fallback.
    /// </summary>
    private void ProcessScalar(Span<float> samples)
    {
        float multiplier = currentMultiplier;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= multiplier;
        }
    }
    
    /// <summary>
    /// SSE-optimized processing (4 samples at a time).
    /// </summary>
    private unsafe void ProcessSse(Span<float> samples)
    {
        float multiplier = currentMultiplier;
        var gainVector = Vector128.Create(multiplier);
        
        int vectorEnd = samples.Length - (samples.Length % 4);
        
        fixed (float* samplesPtr = samples)
        {
            for (int i = 0; i < vectorEnd; i += 4)
            {
                var values = Sse.LoadVector128(samplesPtr + i);
                values = Sse.Multiply(values, gainVector);
                Sse.Store(samplesPtr + i, values);
            }
        }
        
        // Process remaining samples
        for (int i = vectorEnd; i < samples.Length; i++)
        {
            samples[i] *= multiplier;
        }
    }
    
    /// <summary>
    /// AVX-optimized processing (8 samples at a time).
    /// </summary>
    private unsafe void ProcessAvx(Span<float> samples)
    {
        float multiplier = currentMultiplier;
        var gainVector = Vector256.Create(multiplier);
        
        int vectorEnd = samples.Length - (samples.Length % 8);
        
        fixed (float* samplesPtr = samples)
        {
            for (int i = 0; i < vectorEnd; i += 8)
            {
                var values = Avx.LoadVector256(samplesPtr + i);
                values = Avx.Multiply(values, gainVector);
                Avx.Store(samplesPtr + i, values);
            }
        }
        
        // Process remaining samples
        for (int i = vectorEnd; i < samples.Length; i++)
        {
            samples[i] *= multiplier;
        }
    }
    
    /// <summary>
    /// Calculates the gain multiplier based on current settings and metadata.
    /// </summary>
    private void CalculateMultiplier()
    {
        if (mode == ReplayGainMode.None)
        {
            currentMultiplier = 1.0f;
            hasReplayGainData = false;
            return;
        }
        
        var info = CurrentInfo;
        if (info == null || !info.HasGain)
        {
            // No ReplayGain data - use default gain
            currentMultiplier = DecibelsToLinear(defaultGainDb);
            hasReplayGainData = false;
            return;
        }
        
        hasReplayGainData = true;
        
        // Select gain value based on mode
        float? selectedGain;
        float? selectedPeak;
        
        if (mode == ReplayGainMode.Album)
        {
            // Prefer album gain, fall back to track
            selectedGain = info.AlbumGainDb ?? info.TrackGainDb;
            selectedPeak = info.AlbumPeak ?? info.TrackPeak;
        }
        else // Track mode
        {
            // Prefer track gain, fall back to album
            selectedGain = info.TrackGainDb ?? info.AlbumGainDb;
            selectedPeak = info.TrackPeak ?? info.AlbumPeak;
        }
        
        if (!selectedGain.HasValue)
        {
            currentMultiplier = DecibelsToLinear(defaultGainDb);
            hasReplayGainData = false;
            return;
        }
        
        // Calculate gain with reference loudness adjustment
        float gain = selectedGain.Value;
        
        // If the file has a non-standard reference loudness, adjust
        if (info.ReferenceLoudnessLufs.HasValue)
        {
            float referenceDelta = StandardReferenceLufs - info.ReferenceLoudnessLufs.Value;
            gain += referenceDelta;
        }
        
        // Add pre-amp
        gain += preampGainDb;
        
        // Convert to linear multiplier
        float multiplier = DecibelsToLinear(gain);
        
        // Apply peak protection if enabled
        if (enablePeakProtection && hasReplayGainData)
        {
            float peak = selectedPeak ?? DefaultPeak;
            
            if (peak > 0.0f)
            {
                // Maximum multiplier to avoid clipping: 1.0 / peak
                // Examples:
                // - peak = 0.5  -> max gain = 2.0 (+6 dB)
                // - peak = 1.0  -> max gain = 1.0 (0 dB)  
                // - peak = 1.5  -> max gain = 0.67 (-3.5 dB)
                float peakLimit = 1.0f / peak;
                
                if (multiplier > peakLimit)
                {
                    multiplier = peakLimit;
                }
            }
        }
        
        currentMultiplier = multiplier;
    }
    
    /// <summary>
    /// Invalidates the cached multiplier and recalculates.
    /// </summary>
    private void InvalidateCache()
    {
        CalculateMultiplier();
    }
    
    /// <summary>
    /// Converts decibels to linear gain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DecibelsToLinear(float db)
    {
        return MathF.Pow(10.0f, db / 20.0f);
    }
    
    /// <summary>
    /// Converts linear gain to decibels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToDecibels(float linear)
    {
        if (linear <= 0.0f)
            return float.NegativeInfinity;
        return 20.0f * MathF.Log10(linear);
    }
    
    /// <summary>
    /// Creates ReplayGainInfo from parsed metadata values.
    /// </summary>
    /// <param name="trackGainDb">Track gain from metadata (e.g., "-6.5 dB").</param>
    /// <param name="albumGainDb">Album gain from metadata.</param>
    /// <param name="trackPeak">Track peak value (0.0 to 1.0+).</param>
    /// <param name="albumPeak">Album peak value.</param>
    /// <returns>ReplayGainInfo populated with the values.</returns>
    public static ReplayGainInfo CreateInfo(
        double? trackGainDb = null,
        double? albumGainDb = null,
        float? trackPeak = null,
        float? albumPeak = null)
    {
        return new ReplayGainInfo
        {
            TrackGainDb = trackGainDb.HasValue ? (float)trackGainDb.Value : null,
            AlbumGainDb = albumGainDb.HasValue ? (float)albumGainDb.Value : null,
            TrackPeak = trackPeak,
            AlbumPeak = albumPeak
        };
    }
}

/// <summary>
/// Audio loudness normalization using EBU R128 target levels.
/// Alternative to ReplayGain for consistent loudness.
/// </summary>
public sealed class LoudnessNormalizer
{
    // EBU R128 standard targets
    private const float DefaultTargetLufs = -23.0f;
    private const float DefaultTruePeakLimit = -1.0f;
    
    private float targetLoudness = DefaultTargetLufs;
    private float truePeakLimit = DefaultTruePeakLimit;
    private float currentGainDb = 0.0f;
    private float currentMultiplier = 1.0f;
    
    /// <summary>
    /// Gets or sets the target loudness in LUFS.
    /// EBU R128 recommends -23 LUFS for broadcast.
    /// Spotify uses -14 LUFS, YouTube uses -14 LUFS.
    /// </summary>
    public float TargetLoudnessLufs
    {
        get => targetLoudness;
        set => targetLoudness = Math.Clamp(value, -40.0f, -5.0f);
    }
    
    /// <summary>
    /// Gets or sets the true peak limit in dBFS.
    /// Default is -1.0 dBFS per EBU R128.
    /// </summary>
    public float TruePeakLimitDbfs
    {
        get => truePeakLimit;
        set => truePeakLimit = Math.Clamp(value, -6.0f, 0.0f);
    }
    
    /// <summary>
    /// Gets the current gain being applied in decibels.
    /// </summary>
    public float CurrentGainDb => currentGainDb;
    
    /// <summary>
    /// Sets the measured loudness of the current track.
    /// </summary>
    /// <param name="measuredLoudnessLufs">Integrated loudness measurement.</param>
    /// <param name="truePeakDbfs">True peak measurement (optional).</param>
    public void SetTrackLoudness(float measuredLoudnessLufs, float? truePeakDbfs = null)
    {
        // Calculate gain to reach target
        currentGainDb = targetLoudness - measuredLoudnessLufs;
        
        // Apply true peak limiting if we have peak data
        if (truePeakDbfs.HasValue)
        {
            // Maximum gain before exceeding true peak limit
            float maxGainForPeak = truePeakLimit - truePeakDbfs.Value;
            
            if (currentGainDb > maxGainForPeak)
            {
                currentGainDb = maxGainForPeak;
            }
        }
        
        // Convert to linear
        currentMultiplier = MathF.Pow(10.0f, currentGainDb / 20.0f);
    }
    
    /// <summary>
    /// Clears the loudness data (for track changes).
    /// </summary>
    public void Clear()
    {
        currentGainDb = 0.0f;
        currentMultiplier = 1.0f;
    }
    
    /// <summary>
    /// Processes audio samples with loudness normalization.
    /// </summary>
    /// <param name="samples">Audio samples (modified in place).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples)
    {
        if (Math.Abs(currentMultiplier - 1.0f) < 0.0001f)
            return;
        
        float multiplier = currentMultiplier;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= multiplier;
        }
    }
}

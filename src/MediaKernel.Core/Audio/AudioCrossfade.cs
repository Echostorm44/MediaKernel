// Audio crossfade and gapless playback utilities
// For smooth transitions between audio tracks

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Crossfade curve type.
/// </summary>
public enum CrossfadeCurve
{
    /// <summary>Linear fade (equal amplitude).</summary>
    Linear,
    /// <summary>Equal power (constant loudness during crossfade).</summary>
    EqualPower,
    /// <summary>S-curve (smooth acceleration/deceleration).</summary>
    SCurve,
    /// <summary>Logarithmic (fast at start, slow at end).</summary>
    Logarithmic,
    /// <summary>Exponential (slow at start, fast at end).</summary>
    Exponential
}

/// <summary>
/// Audio crossfade processor for smooth transitions between tracks.
/// </summary>
public sealed class AudioCrossfader
{
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly CrossfadeCurve _curve;
    
    private int _crossfadeSamples;
    private int _currentSample;
    private bool _isActive;
    
    /// <summary>
    /// Duration of crossfade in seconds.
    /// </summary>
    public double Duration { get; private set; }
    
    /// <summary>
    /// Whether a crossfade is currently in progress.
    /// </summary>
    public bool IsActive => _isActive;
    
    /// <summary>
    /// Progress of current crossfade (0.0 to 1.0).
    /// </summary>
    public double Progress => _crossfadeSamples > 0 
        ? (double)_currentSample / _crossfadeSamples 
        : 0.0;
    
    public AudioCrossfader(int sampleRate, int channelCount, 
        double durationSeconds = 3.0, CrossfadeCurve curve = CrossfadeCurve.EqualPower)
    {
        _sampleRate = sampleRate;
        _channelCount = channelCount;
        _curve = curve;
        Duration = durationSeconds;
        _crossfadeSamples = (int)(sampleRate * durationSeconds);
    }
    
    /// <summary>
    /// Sets the crossfade duration.
    /// </summary>
    public void SetDuration(double seconds)
    {
        Duration = Math.Max(0.001, seconds);
        _crossfadeSamples = (int)(_sampleRate * Duration);
    }
    
    /// <summary>
    /// Starts a new crossfade transition.
    /// </summary>
    public void Start()
    {
        _currentSample = 0;
        _isActive = true;
    }
    
    /// <summary>
    /// Stops the current crossfade.
    /// </summary>
    public void Stop()
    {
        _isActive = false;
        _currentSample = 0;
    }
    
    /// <summary>
    /// Processes crossfade between outgoing and incoming audio.
    /// </summary>
    /// <param name="outgoing">Audio fading out (source A).</param>
    /// <param name="incoming">Audio fading in (source B).</param>
    /// <param name="output">Mixed output buffer.</param>
    /// <returns>True if crossfade is still active.</returns>
    public bool Process(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming, Span<float> output)
    {
        if (!_isActive)
        {
            incoming.CopyTo(output);
            return false;
        }
        
        int sampleFrames = Math.Min(outgoing.Length, incoming.Length) / _channelCount;
        sampleFrames = Math.Min(sampleFrames, output.Length / _channelCount);
        
        for (int i = 0; i < sampleFrames; i++)
        {
            if (_currentSample >= _crossfadeSamples)
            {
                // Crossfade complete, just use incoming
                for (int ch = 0; ch < _channelCount; ch++)
                {
                    output[i * _channelCount + ch] = incoming[i * _channelCount + ch];
                }
            }
            else
            {
                double t = (double)_currentSample / _crossfadeSamples;
                (double fadeOut, double fadeIn) = CalculateFades(t);
                
                for (int ch = 0; ch < _channelCount; ch++)
                {
                    int idx = i * _channelCount + ch;
                    output[idx] = (float)(outgoing[idx] * fadeOut + incoming[idx] * fadeIn);
                }
                
                _currentSample++;
            }
        }
        
        if (_currentSample >= _crossfadeSamples)
        {
            _isActive = false;
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Calculates fade gains for fade-out only (ending track).
    /// </summary>
    public float GetFadeOutGain(int sampleOffset)
    {
        if (!_isActive || _crossfadeSamples == 0)
            return 0.0f;
        
        double t = Math.Clamp((double)(_currentSample + sampleOffset) / _crossfadeSamples, 0.0, 1.0);
        (double fadeOut, _) = CalculateFades(t);
        return (float)fadeOut;
    }
    
    /// <summary>
    /// Calculates fade gains for fade-in only (starting track).
    /// </summary>
    public float GetFadeInGain(int sampleOffset)
    {
        if (!_isActive || _crossfadeSamples == 0)
            return 1.0f;
        
        double t = Math.Clamp((double)(_currentSample + sampleOffset) / _crossfadeSamples, 0.0, 1.0);
        (_, double fadeIn) = CalculateFades(t);
        return (float)fadeIn;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (double fadeOut, double fadeIn) CalculateFades(double t)
    {
        return _curve switch
        {
            CrossfadeCurve.Linear => (1.0 - t, t),
            CrossfadeCurve.EqualPower => CalculateEqualPower(t),
            CrossfadeCurve.SCurve => CalculateSCurve(t),
            CrossfadeCurve.Logarithmic => CalculateLogarithmic(t),
            CrossfadeCurve.Exponential => CalculateExponential(t),
            _ => (1.0 - t, t)
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double fadeOut, double fadeIn) CalculateEqualPower(double t)
    {
        // Equal power: sin/cos relationship maintains constant power
        double angle = t * Math.PI * 0.5;
        return (Math.Cos(angle), Math.Sin(angle));
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double fadeOut, double fadeIn) CalculateSCurve(double t)
    {
        // Smooth S-curve using cubic interpolation
        double s = t * t * (3.0 - 2.0 * t);
        return (1.0 - s, s);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double fadeOut, double fadeIn) CalculateLogarithmic(double t)
    {
        // Logarithmic: fast start, slow end
        const double logBase = 10.0;
        double fadeIn = Math.Log(1 + t * (logBase - 1)) / Math.Log(logBase);
        return (1.0 - fadeIn, fadeIn);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double fadeOut, double fadeIn) CalculateExponential(double t)
    {
        // Exponential: slow start, fast end
        const double expFactor = 3.0;
        double fadeIn = (Math.Exp(t * expFactor) - 1) / (Math.Exp(expFactor) - 1);
        return (1.0 - fadeIn, fadeIn);
    }
}

/// <summary>
/// Simple fade-in/fade-out processor for individual tracks.
/// </summary>
public sealed class AudioFader
{
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly CrossfadeCurve _curve;
    
    private int _fadeSamples;
    private int _currentSample;
    private FadeDirection _direction;
    private bool _isActive;
    
    private enum FadeDirection { In, Out }
    
    /// <summary>Whether a fade is currently active.</summary>
    public bool IsActive => _isActive;
    
    /// <summary>Fade duration in seconds.</summary>
    public double Duration { get; private set; }
    
    public AudioFader(int sampleRate, int channelCount, 
        double durationSeconds = 0.5, CrossfadeCurve curve = CrossfadeCurve.EqualPower)
    {
        _sampleRate = sampleRate;
        _channelCount = channelCount;
        _curve = curve;
        Duration = durationSeconds;
        _fadeSamples = (int)(sampleRate * durationSeconds);
    }
    
    /// <summary>
    /// Sets fade duration.
    /// </summary>
    public void SetDuration(double seconds)
    {
        Duration = Math.Max(0.001, seconds);
        _fadeSamples = (int)(_sampleRate * Duration);
    }
    
    /// <summary>
    /// Starts a fade-in.
    /// </summary>
    public void StartFadeIn()
    {
        _direction = FadeDirection.In;
        _currentSample = 0;
        _isActive = true;
    }
    
    /// <summary>
    /// Starts a fade-out.
    /// </summary>
    public void StartFadeOut()
    {
        _direction = FadeDirection.Out;
        _currentSample = 0;
        _isActive = true;
    }
    
    /// <summary>
    /// Processes audio with fade applied.
    /// </summary>
    /// <returns>True if fade is still active.</returns>
    public bool Process(Span<float> samples)
    {
        if (!_isActive)
            return false;
        
        int sampleFrames = samples.Length / _channelCount;
        
        for (int i = 0; i < sampleFrames && _currentSample < _fadeSamples; i++)
        {
            double t = (double)_currentSample / _fadeSamples;
            double gain = _direction == FadeDirection.In 
                ? CalculateFadeIn(t) 
                : CalculateFadeOut(t);
            
            for (int ch = 0; ch < _channelCount; ch++)
            {
                samples[i * _channelCount + ch] *= (float)gain;
            }
            
            _currentSample++;
        }
        
        if (_currentSample >= _fadeSamples)
        {
            _isActive = false;
            return false;
        }
        
        return true;
    }
    
    private double CalculateFadeIn(double t)
    {
        return _curve switch
        {
            CrossfadeCurve.Linear => t,
            CrossfadeCurve.EqualPower => Math.Sin(t * Math.PI * 0.5),
            CrossfadeCurve.SCurve => t * t * (3.0 - 2.0 * t),
            CrossfadeCurve.Logarithmic => Math.Log(1 + t * 9) / Math.Log(10),
            CrossfadeCurve.Exponential => (Math.Exp(t * 3) - 1) / (Math.Exp(3) - 1),
            _ => t
        };
    }
    
    private double CalculateFadeOut(double t)
    {
        return 1.0 - CalculateFadeIn(t);
    }
}

/// <summary>
/// Gapless playback buffer manager for seamless track transitions.
/// </summary>
public sealed class GaplessPlaybackBuffer
{
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly float[] _overlapBuffer;
    private readonly int _overlapSamples;
    
    private int _overlapWritePos;
    private int _overlapReadPos;
    private bool _hasOverlapData;
    
    /// <summary>
    /// Amount of overlap in seconds.
    /// </summary>
    public double OverlapDuration { get; }
    
    /// <summary>
    /// Whether there is buffered overlap data available.
    /// </summary>
    public bool HasOverlapData => _hasOverlapData;
    
    public GaplessPlaybackBuffer(int sampleRate, int channelCount, double overlapSeconds = 0.1)
    {
        _sampleRate = sampleRate;
        _channelCount = channelCount;
        OverlapDuration = overlapSeconds;
        _overlapSamples = (int)(sampleRate * overlapSeconds) * channelCount;
        _overlapBuffer = new float[_overlapSamples];
    }
    
    /// <summary>
    /// Stores the end of the outgoing track for overlap.
    /// </summary>
    public void StoreTrackEnd(ReadOnlySpan<float> samples)
    {
        int toCopy = Math.Min(samples.Length, _overlapSamples);
        int sourceOffset = samples.Length - toCopy;
        
        samples.Slice(sourceOffset, toCopy).CopyTo(_overlapBuffer.AsSpan(0, toCopy));
        _overlapWritePos = toCopy;
        _overlapReadPos = 0;
        _hasOverlapData = toCopy > 0;
    }
    
    /// <summary>
    /// Mixes stored overlap with incoming track start.
    /// </summary>
    public void MixWithTrackStart(Span<float> samples)
    {
        if (!_hasOverlapData)
            return;
        
        int overlapAvailable = _overlapWritePos - _overlapReadPos;
        int toMix = Math.Min(samples.Length, overlapAvailable);
        
        // Simple linear crossfade over the overlap region
        for (int i = 0; i < toMix; i++)
        {
            float t = (float)i / toMix;
            float outGain = 1.0f - t;
            float inGain = t;
            
            samples[i] = _overlapBuffer[_overlapReadPos + i] * outGain + samples[i] * inGain;
        }
        
        _overlapReadPos += toMix;
        if (_overlapReadPos >= _overlapWritePos)
        {
            _hasOverlapData = false;
        }
    }
    
    /// <summary>
    /// Clears the overlap buffer.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_overlapBuffer);
        _overlapWritePos = 0;
        _overlapReadPos = 0;
        _hasOverlapData = false;
    }
}

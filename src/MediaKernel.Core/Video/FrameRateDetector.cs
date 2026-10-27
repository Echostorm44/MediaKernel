// Frame rate detection from timestamp analysis
// Determines actual frame rate from stream timestamps

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Video;

/// <summary>
/// Common video frame rates with their exact rational representation.
/// </summary>
public readonly struct FrameRate : IEquatable<FrameRate>
{
    /// <summary>Numerator of the frame rate fraction.</summary>
    public int Numerator { get; }
    
    /// <summary>Denominator of the frame rate fraction.</summary>
    public int Denominator { get; }
    
    /// <summary>Frame rate as a floating point value.</summary>
    public double Value => Denominator > 0 ? (double)Numerator / Denominator : 0;
    
    /// <summary>Frame duration in microseconds.</summary>
    public long FrameDurationUs => Denominator > 0 ? (long)(1_000_000.0 * Denominator / Numerator) : 0;
    
    /// <summary>Frame duration in milliseconds.</summary>
    public double FrameDurationMs => Denominator > 0 ? 1000.0 * Denominator / Numerator : 0;
    
    public FrameRate(int numerator, int denominator)
    {
        Numerator = numerator;
        Denominator = denominator > 0 ? denominator : 1;
    }
    
    public FrameRate(double fps)
    {
        // Try to find a close rational approximation
        var (num, den) = ApproximateRational(fps);
        Numerator = num;
        Denominator = den;
    }
    
    private static (int num, int den) ApproximateRational(double value)
    {
        // Common frame rates to check first
        var common = new (int num, int den, double fps)[]
        {
            (24000, 1001, 23.976),
            (24, 1, 24.0),
            (25, 1, 25.0),
            (30000, 1001, 29.97),
            (30, 1, 30.0),
            (50, 1, 50.0),
            (60000, 1001, 59.94),
            (60, 1, 60.0),
            (120000, 1001, 119.88),
            (120, 1, 120.0),
        };
        
        foreach (var (num, den, fps) in common)
        {
            if (Math.Abs(value - fps) < 0.01)
                return (num, den);
        }
        
        // Default to simple approximation
        if (value < 1) return (1, 1);
        return ((int)Math.Round(value * 1000), 1000);
    }
    
    // Common frame rates
    public static FrameRate Fps23_976 => new(24000, 1001);
    public static FrameRate Fps24 => new(24, 1);
    public static FrameRate Fps25 => new(25, 1);
    public static FrameRate Fps29_97 => new(30000, 1001);
    public static FrameRate Fps30 => new(30, 1);
    public static FrameRate Fps50 => new(50, 1);
    public static FrameRate Fps59_94 => new(60000, 1001);
    public static FrameRate Fps60 => new(60, 1);
    public static FrameRate Fps120 => new(120, 1);
    
    public bool Equals(FrameRate other)
        => Numerator == other.Numerator && Denominator == other.Denominator;
    
    public override bool Equals(object? obj)
        => obj is FrameRate other && Equals(other);
    
    public override int GetHashCode()
        => HashCode.Combine(Numerator, Denominator);
    
    public static bool operator ==(FrameRate left, FrameRate right) => left.Equals(right);
    public static bool operator !=(FrameRate left, FrameRate right) => !left.Equals(right);
    
    public override string ToString()
    {
        double fps = Value;
        // Check for NTSC drop-frame rates
        if (Denominator == 1001)
            return $"{fps:F3} fps ({Numerator}/{Denominator})";
        return $"{fps:F2} fps";
    }
    
    /// <summary>
    /// Check if this is a variable frame rate marker (0/0 or similar).
    /// </summary>
    public bool IsVariable => Numerator <= 0 || Denominator <= 0;
    
    /// <summary>
    /// Check if this matches a common NTSC drop-frame rate.
    /// </summary>
    public bool IsDropFrame => Denominator == 1001;
}

/// <summary>
/// Detects frame rate from a sequence of presentation timestamps.
/// Useful when container metadata is missing or incorrect.
/// </summary>
public sealed class FrameRateDetector
{
    private readonly List<long> timestamps = new();
    private readonly List<long> deltas = new();
    private readonly int maxSamples;
    private bool isAnalyzed;
    private FrameRate detectedRate;
    private double confidence;
    
    /// <summary>
    /// Creates a frame rate detector.
    /// </summary>
    /// <param name="maxSamples">Maximum number of timestamps to analyze.</param>
    public FrameRateDetector(int maxSamples = 100)
    {
        this.maxSamples = Math.Max(10, maxSamples);
    }
    
    /// <summary>
    /// Add a presentation timestamp (in microseconds).
    /// </summary>
    public void AddTimestamp(long ptsUs)
    {
        if (timestamps.Count > 0)
        {
            long delta = ptsUs - timestamps[^1];
            if (delta > 0 && delta < 1_000_000) // Reasonable delta (< 1 second)
            {
                deltas.Add(delta);
            }
        }
        
        timestamps.Add(ptsUs);
        
        if (timestamps.Count > maxSamples)
        {
            timestamps.RemoveAt(0);
        }
        if (deltas.Count > maxSamples)
        {
            deltas.RemoveAt(0);
        }
        
        isAnalyzed = false;
    }
    
    /// <summary>
    /// Add a presentation timestamp as TimeSpan.
    /// </summary>
    public void AddTimestamp(TimeSpan pts)
    {
        AddTimestamp((long)(pts.TotalMilliseconds * 1000));
    }
    
    /// <summary>
    /// Analyze collected timestamps and detect frame rate.
    /// </summary>
    public FrameRate Analyze()
    {
        if (isAnalyzed)
            return detectedRate;
        
        if (deltas.Count < 5)
        {
            detectedRate = new FrameRate(0, 1);
            confidence = 0;
            isAnalyzed = true;
            return detectedRate;
        }
        
        // Calculate median delta
        var sortedDeltas = new List<long>(deltas);
        sortedDeltas.Sort();
        long medianDelta = sortedDeltas[sortedDeltas.Count / 2];
        
        // Calculate average delta (excluding outliers)
        long sumDelta = 0;
        int count = 0;
        long lowerBound = medianDelta / 2;
        long upperBound = medianDelta * 2;
        
        foreach (long delta in deltas)
        {
            if (delta >= lowerBound && delta <= upperBound)
            {
                sumDelta += delta;
                count++;
            }
        }
        
        if (count == 0)
        {
            detectedRate = new FrameRate(0, 1);
            confidence = 0;
            isAnalyzed = true;
            return detectedRate;
        }
        
        double avgDeltaUs = (double)sumDelta / count;
        double detectedFps = 1_000_000.0 / avgDeltaUs;
        
        // Calculate confidence based on variance
        double variance = 0;
        foreach (long delta in deltas)
        {
            if (delta >= lowerBound && delta <= upperBound)
            {
                double diff = delta - avgDeltaUs;
                variance += diff * diff;
            }
        }
        variance /= count;
        double stdDev = Math.Sqrt(variance);
        
        // Confidence is inverse of relative standard deviation
        double relativeStdDev = stdDev / avgDeltaUs;
        confidence = Math.Max(0, Math.Min(1, 1 - relativeStdDev * 10));
        
        detectedRate = MatchToStandardRate(detectedFps);
        isAnalyzed = true;
        
        return detectedRate;
    }
    
    /// <summary>
    /// Match detected FPS to nearest standard frame rate.
    /// </summary>
    private static FrameRate MatchToStandardRate(double fps)
    {
        var standards = new (FrameRate rate, double tolerance)[]
        {
            (FrameRate.Fps23_976, 0.05),
            (FrameRate.Fps24, 0.5),
            (FrameRate.Fps25, 0.5),
            (FrameRate.Fps29_97, 0.05),
            (FrameRate.Fps30, 0.5),
            (FrameRate.Fps50, 1.0),
            (FrameRate.Fps59_94, 0.1),
            (FrameRate.Fps60, 1.0),
            (FrameRate.Fps120, 2.0),
        };
        
        foreach (var (rate, tolerance) in standards)
        {
            if (Math.Abs(fps - rate.Value) < tolerance)
                return rate;
        }
        
        // No match, return as-is
        return new FrameRate(fps);
    }
    
    /// <summary>
    /// Gets the detected frame rate (calls Analyze if needed).
    /// </summary>
    public FrameRate DetectedFrameRate
    {
        get
        {
            if (!isAnalyzed) Analyze();
            return detectedRate;
        }
    }
    
    /// <summary>
    /// Confidence level of the detection (0-1).
    /// Higher values indicate more consistent frame timing.
    /// </summary>
    public double Confidence
    {
        get
        {
            if (!isAnalyzed) Analyze();
            return confidence;
        }
    }
    
    /// <summary>
    /// Number of timestamps collected.
    /// </summary>
    public int SampleCount => timestamps.Count;
    
    /// <summary>
    /// Whether variable frame rate is detected (low confidence or high variance).
    /// </summary>
    public bool IsVariableFrameRate => Confidence < 0.7;
    
    /// <summary>
    /// Reset the detector state.
    /// </summary>
    public void Reset()
    {
        timestamps.Clear();
        deltas.Clear();
        isAnalyzed = false;
        detectedRate = new FrameRate(0, 1);
        confidence = 0;
    }
}

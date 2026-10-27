// MediaKernel - Adaptive Bitrate Selection Logic
// Implements multiple algorithms for quality selection: rate-based, predictive, BOLA

using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Base class for adaptation logic implementations.
/// Selects the optimal quality variant based on network conditions and buffer state.
/// </summary>
public abstract class AdaptationLogic
{
    protected readonly AdaptiveStreamingConfiguration configuration;
    protected readonly AdaptiveStreamingStatistics statistics;

    protected AdaptationLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
    {
        this.configuration = configuration;
        this.statistics = statistics;
    }

    /// <summary>
    /// Select the best variant index for current conditions.
    /// </summary>
    /// <param name="variants">Available quality variants, sorted by bandwidth ascending</param>
    /// <param name="currentVariantIndex">Currently selected variant index</param>
    /// <param name="bufferLevel">Current buffer state</param>
    /// <param name="measuredBandwidth">Recently measured bandwidth in bits per second</param>
    /// <returns>Index of selected variant</returns>
    public abstract int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth);

    /// <summary>
    /// Filter variants by configuration constraints.
    /// </summary>
    protected List<int> GetEligibleVariantIndices(ReadOnlySpan<StreamVariant> variants)
    {
        var eligible = new List<int>();

        for (int i = 0; i < variants.Length; i++)
        {
            var variant = variants[i];

            // Check resolution constraints
            if (configuration.MaxVideoWidth > 0 && variant.Width > configuration.MaxVideoWidth)
                continue;
            if (configuration.MaxVideoHeight > 0 && variant.Height > configuration.MaxVideoHeight)
                continue;

            // Check bandwidth constraint
            if (configuration.MaxBandwidth > 0 && variant.Bandwidth > configuration.MaxBandwidth)
                continue;

            eligible.Add(i);
        }

        // Always include at least the lowest quality
        if (eligible.Count == 0 && variants.Length > 0)
        {
            eligible.Add(0);
        }

        return eligible;
    }

    /// <summary>
    /// Create the appropriate logic based on strategy.
    /// </summary>
    public static AdaptationLogic Create(
        AdaptationStrategy strategy,
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
    {
        return strategy switch
        {
            AdaptationStrategy.AlwaysBest => new AlwaysBestLogic(configuration, statistics),
            AdaptationStrategy.AlwaysLowest => new AlwaysLowestLogic(configuration, statistics),
            AdaptationStrategy.RateBased => new RateBasedLogic(configuration, statistics),
            AdaptationStrategy.Predictive => new PredictiveLogic(configuration, statistics),
            AdaptationStrategy.NearOptimal => new NearOptimalLogic(configuration, statistics),
            _ => new RateBasedLogic(configuration, statistics)
        };
    }
}

/// <summary>
/// Always selects the highest available quality (ignores bandwidth).
/// Useful for testing or when bandwidth is not a concern.
/// </summary>
public sealed class AlwaysBestLogic : AdaptationLogic
{
    public AlwaysBestLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
        : base(configuration, statistics)
    {
    }

    public override int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth)
    {
        var eligible = GetEligibleVariantIndices(variants);
        return eligible.Count > 0 ? eligible[^1] : 0;
    }
}

/// <summary>
/// Always selects the lowest available quality (data saver mode).
/// </summary>
public sealed class AlwaysLowestLogic : AdaptationLogic
{
    public AlwaysLowestLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
        : base(configuration, statistics)
    {
    }

    public override int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth)
    {
        var eligible = GetEligibleVariantIndices(variants);
        return eligible.Count > 0 ? eligible[0] : 0;
    }
}

/// <summary>
/// Selects quality based on measured download rate.
/// Classic approach used by most adaptive streaming implementations.
/// </summary>
public sealed class RateBasedLogic : AdaptationLogic
{
    // Exponential moving average for bandwidth smoothing
    private double smoothedBandwidth;
    private const double SmoothingFactor = 0.8;

    public RateBasedLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
        : base(configuration, statistics)
    {
    }

    public override int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth)
    {
        if (variants.Length == 0) return 0;

        // Update smoothed bandwidth estimate
        if (measuredBandwidth > 0)
        {
            smoothedBandwidth = smoothedBandwidth > 0
                ? SmoothingFactor * smoothedBandwidth + (1 - SmoothingFactor) * measuredBandwidth
                : measuredBandwidth;
        }

        // Apply safety factor to prevent oscillation
        double safeBandwidth = smoothedBandwidth * configuration.BandwidthSafetyFactor;

        var eligible = GetEligibleVariantIndices(variants);
        if (eligible.Count == 0) return 0;

        // Find highest quality that fits within bandwidth
        int selectedIndex = eligible[0];

        for (int i = eligible.Count - 1; i >= 0; i--)
        {
            int variantIndex = eligible[i];
            if (variants[variantIndex].Bandwidth <= safeBandwidth)
            {
                selectedIndex = variantIndex;
                break;
            }
        }

        // Hysteresis: require higher bandwidth to upgrade (prevent oscillation)
        if (selectedIndex > currentVariantIndex && currentVariantIndex >= 0)
        {
            double upgradeBandwidth = variants[selectedIndex].Bandwidth * configuration.UpgradeBandwidthThreshold;
            if (smoothedBandwidth < upgradeBandwidth)
            {
                selectedIndex = currentVariantIndex;
            }
        }

        // Emergency downgrade if buffer is critical
        if (bufferLevel.IsCritical && selectedIndex > 0)
        {
            // Drop to lowest quality immediately
            selectedIndex = eligible[0];
        }
        else if (bufferLevel.IsLow && selectedIndex > currentVariantIndex)
        {
            // Don't upgrade while buffer is low
            selectedIndex = currentVariantIndex >= 0 ? currentVariantIndex : selectedIndex;
        }

        return selectedIndex;
    }
}

/// <summary>
/// Predicts future bandwidth based on historical trends.
/// Uses linear regression on recent samples for proactive quality selection.
/// </summary>
public sealed class PredictiveLogic : AdaptationLogic
{
    private readonly Queue<(long timestamp, double bandwidth)> bandwidthSamples = new();
    private const int MaxSamples = 20;
    private const long SampleWindowMicroseconds = 30_000_000; // 30 seconds

    public PredictiveLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
        : base(configuration, statistics)
    {
    }

    public override int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth)
    {
        if (variants.Length == 0) return 0;

        long now = Environment.TickCount64 * 1000; // microseconds

        // Add new sample
        if (measuredBandwidth > 0)
        {
            bandwidthSamples.Enqueue((now, measuredBandwidth));

            // Remove old samples
            while (bandwidthSamples.Count > MaxSamples ||
                   (bandwidthSamples.Count > 0 && now - bandwidthSamples.Peek().timestamp > SampleWindowMicroseconds))
            {
                bandwidthSamples.Dequeue();
            }
        }

        // Predict future bandwidth
        double predictedBandwidth = PredictBandwidth(now);

        // Apply safety factor
        double safeBandwidth = predictedBandwidth * configuration.BandwidthSafetyFactor;

        var eligible = GetEligibleVariantIndices(variants);
        if (eligible.Count == 0) return 0;

        // Find highest quality that fits
        int selectedIndex = eligible[0];
        for (int i = eligible.Count - 1; i >= 0; i--)
        {
            int variantIndex = eligible[i];
            if (variants[variantIndex].Bandwidth <= safeBandwidth)
            {
                selectedIndex = variantIndex;
                break;
            }
        }

        // Buffer-based adjustments
        if (bufferLevel.IsCritical)
        {
            selectedIndex = eligible[0];
        }
        else if (bufferLevel.IsLow && selectedIndex > currentVariantIndex)
        {
            selectedIndex = Math.Max(0, currentVariantIndex);
        }

        return selectedIndex;
    }

    private double PredictBandwidth(long targetTime)
    {
        if (bandwidthSamples.Count < 2)
        {
            return bandwidthSamples.Count > 0 ? bandwidthSamples.Peek().bandwidth : 0;
        }

        // Simple linear regression
        var samples = bandwidthSamples.ToArray();
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        int n = samples.Length;

        for (int i = 0; i < n; i++)
        {
            double x = samples[i].timestamp / 1_000_000.0; // seconds
            double y = samples[i].bandwidth;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        double denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < 0.0001)
        {
            // Fallback to average
            return sumY / n;
        }

        double slope = (n * sumXY - sumX * sumY) / denominator;
        double intercept = (sumY - slope * sumX) / n;

        // Predict 5 seconds ahead
        double predictX = targetTime / 1_000_000.0 + 5.0;
        double predicted = slope * predictX + intercept;

        // Clamp to reasonable bounds
        double lastBandwidth = samples[^1].bandwidth;
        predicted = Math.Clamp(predicted, lastBandwidth * 0.5, lastBandwidth * 2.0);

        return Math.Max(0, predicted);
    }
}

/// <summary>
/// BOLA-inspired buffer-based adaptation.
/// Uses buffer level as primary signal rather than bandwidth.
/// Reference: "BOLA: Near-Optimal Bitrate Adaptation for Online Videos" (INFOCOM 2016)
/// </summary>
public sealed class NearOptimalLogic : AdaptationLogic
{
    public NearOptimalLogic(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics statistics)
        : base(configuration, statistics)
    {
    }

    public override int SelectVariant(
        ReadOnlySpan<StreamVariant> variants,
        int currentVariantIndex,
        BufferLevel bufferLevel,
        double measuredBandwidth)
    {
        if (variants.Length == 0) return 0;

        var eligible = GetEligibleVariantIndices(variants);
        if (eligible.Count == 0) return 0;
        if (eligible.Count == 1) return eligible[0];

        // BOLA parameters
        double bufferTarget = configuration.TargetBufferSeconds;
        double bufferCurrent = bufferLevel.CurrentMicroseconds / 1_000_000.0;

        // Calculate utility for each quality level
        // Utility is logarithm of bitrate (diminishing returns for higher quality)
        double maxUtility = double.MinValue;
        int selectedIndex = eligible[0];

        for (int i = 0; i < eligible.Count; i++)
        {
            int variantIndex = eligible[i];
            var variant = variants[variantIndex];

            // Utility: logarithm of bitrate
            double utility = Math.Log(variant.Bandwidth / 1000.0 + 1);

            // Buffer-based adjustment: higher buffer allows higher quality
            double bufferFactor = bufferCurrent / bufferTarget;

            // Penalize high bitrates when buffer is low
            double adjustedUtility = utility * bufferFactor;

            // If bandwidth is known, also consider download feasibility
            if (measuredBandwidth > 0)
            {
                double downloadRatio = variant.Bandwidth / measuredBandwidth;
                if (downloadRatio > 1.0)
                {
                    // Penalty for exceeding bandwidth
                    adjustedUtility *= 0.5 / downloadRatio;
                }
            }

            if (adjustedUtility > maxUtility)
            {
                maxUtility = adjustedUtility;
                selectedIndex = variantIndex;
            }
        }

        // Safety overrides
        if (bufferLevel.IsCritical)
        {
            selectedIndex = eligible[0];
        }

        return selectedIndex;
    }
}

/// <summary>
/// Bandwidth estimator using exponential weighted moving average.
/// </summary>
public sealed class BandwidthEstimator
{
    private double estimatedBandwidth;
    private double estimatedVariance;
    private int sampleCount;
    private readonly object lockObject = new();

    // EMA smoothing factor (higher = more responsive, lower = more stable)
    private const double Alpha = 0.3;
    private const double VarianceAlpha = 0.1;

    public double EstimatedBandwidth
    {
        get { lock (lockObject) return estimatedBandwidth; }
    }

    public double EstimatedVariance
    {
        get { lock (lockObject) return estimatedVariance; }
    }

    public int SampleCount
    {
        get { lock (lockObject) return sampleCount; }
    }

    /// <summary>
    /// Gets a conservative bandwidth estimate (mean - 1 std dev).
    /// Safer for quality selection to prevent rebuffering.
    /// </summary>
    public double ConservativeEstimate
    {
        get
        {
            lock (lockObject)
            {
                double stdDev = Math.Sqrt(estimatedVariance);
                return Math.Max(0, estimatedBandwidth - stdDev);
            }
        }
    }

    public void AddSample(long bytesDownloaded, long downloadTimeMilliseconds)
    {
        if (downloadTimeMilliseconds <= 0 || bytesDownloaded <= 0) return;

        double bitsPerSecond = (bytesDownloaded * 8.0 * 1000.0) / downloadTimeMilliseconds;

        lock (lockObject)
        {
            if (sampleCount == 0)
            {
                estimatedBandwidth = bitsPerSecond;
                estimatedVariance = 0;
            }
            else
            {
                // EMA update
                double delta = bitsPerSecond - estimatedBandwidth;
                estimatedBandwidth += Alpha * delta;
                estimatedVariance = (1 - VarianceAlpha) * estimatedVariance + VarianceAlpha * delta * delta;
            }
            sampleCount++;
        }
    }

    public void Reset()
    {
        lock (lockObject)
        {
            estimatedBandwidth = 0;
            estimatedVariance = 0;
            sampleCount = 0;
        }
    }
}

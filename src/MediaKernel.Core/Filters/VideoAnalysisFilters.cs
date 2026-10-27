// MediaKernel - Video Analysis Filters
// Scene detection, histogram analysis, and motion estimation
// Reference: VLC modules/video_filter/motiondetect.c, scene.c

using System;
using System.Buffers;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Result of scene change detection.
/// </summary>
public readonly struct SceneChangeResult
{
    /// <summary>Frame number that was analyzed.</summary>
    public long FrameNumber { get; init; }
    
    /// <summary>Timestamp of the frame.</summary>
    public TimeSpan Timestamp { get; init; }
    
    /// <summary>Whether a scene change was detected.</summary>
    public bool IsSceneChange { get; init; }
    
    /// <summary>Difference score from previous frame (0-1).</summary>
    public double DifferenceScore { get; init; }
    
    /// <summary>Threshold used for detection.</summary>
    public double Threshold { get; init; }
}

/// <summary>
/// Luminance histogram for video frame analysis.
/// </summary>
public sealed class LuminanceHistogram
{
    private readonly int[] bins = new int[256];
    private int totalPixels;
    
    /// <summary>Gets the histogram bins (256 values).</summary>
    public ReadOnlySpan<int> Bins => bins;
    
    /// <summary>Total pixels counted.</summary>
    public int TotalPixels => totalPixels;
    
    /// <summary>
    /// Build histogram from Y plane data.
    /// </summary>
    public void BuildFromYPlane(ReadOnlySpan<byte> yPlaneData)
    {
        Array.Clear(bins);
        totalPixels = yPlaneData.Length;
        
        foreach (byte y in yPlaneData)
        {
            bins[y]++;
        }
    }
    
    /// <summary>
    /// Build histogram from RGBA data (converts to luminance).
    /// </summary>
    public void BuildFromRgba(ReadOnlySpan<byte> rgbaData)
    {
        Array.Clear(bins);
        totalPixels = rgbaData.Length / 4;
        
        for (int i = 0; i < rgbaData.Length; i += 4)
        {
            // BT.601 luminance: Y = 0.299R + 0.587G + 0.114B
            int r = rgbaData[i];
            int g = rgbaData[i + 1];
            int b = rgbaData[i + 2];
            int y = (77 * r + 150 * g + 29 * b) >> 8;
            bins[Math.Clamp(y, 0, 255)]++;
        }
    }
    
    /// <summary>
    /// Calculate mean luminance.
    /// </summary>
    public double CalculateMean()
    {
        if (totalPixels == 0) return 0;
        
        long sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += (long)bins[i] * i;
        }
        return (double)sum / totalPixels;
    }
    
    /// <summary>
    /// Calculate histogram difference using chi-squared metric.
    /// </summary>
    /// <returns>Difference score from 0 (identical) to 1 (completely different).</returns>
    public double CalculateDifference(LuminanceHistogram other)
    {
        if (totalPixels == 0 || other.TotalPixels == 0)
            return 1.0;
        
        double chiSquared = 0;
        var otherBins = other.Bins;
        
        for (int i = 0; i < 256; i++)
        {
            double a = (double)bins[i] / totalPixels;
            double b = (double)otherBins[i] / other.TotalPixels;
            double sum = a + b;
            
            if (sum > 0)
            {
                double diff = a - b;
                chiSquared += (diff * diff) / sum;
            }
        }
        
        // Normalize to 0-1 range (max chi-squared is 2)
        return Math.Min(chiSquared / 2.0, 1.0);
    }
    
    /// <summary>
    /// Calculate histogram intersection (Bhattacharyya coefficient).
    /// </summary>
    /// <returns>Similarity score from 0 (different) to 1 (identical).</returns>
    public double CalculateSimilarity(LuminanceHistogram other)
    {
        if (totalPixels == 0 || other.TotalPixels == 0)
            return 0.0;
        
        double sum = 0;
        var otherBins = other.Bins;
        
        for (int i = 0; i < 256; i++)
        {
            double a = (double)bins[i] / totalPixels;
            double b = (double)otherBins[i] / other.TotalPixels;
            sum += Math.Sqrt(a * b);
        }
        
        return sum;
    }
    
    /// <summary>
    /// Copy histogram data to another instance.
    /// </summary>
    public void CopyTo(LuminanceHistogram destination)
    {
        bins.AsSpan().CopyTo(destination.bins);
        destination.totalPixels = totalPixels;
    }
}

/// <summary>
/// Detects scene changes between video frames using histogram comparison.
/// Enhanced version with configurable options and detailed results.
/// </summary>
public sealed class AdvancedSceneChangeDetector
{
    private readonly LuminanceHistogram previousHistogram = new();
    private readonly LuminanceHistogram currentHistogram = new();
    private bool hasPreviousFrame;
    private long frameNumber;
    
    /// <summary>Threshold for scene change detection (0-1). Default is 0.3.</summary>
    public double Threshold { get; set; } = 0.3;
    
    /// <summary>Minimum frames between detected scene changes.</summary>
    public int MinimumFramesBetweenChanges { get; set; } = 10;
    
    /// <summary>Frame rate for timestamp calculation.</summary>
    public double FrameRate { get; set; } = 25.0;
    
    private long lastSceneChangeFrame;
    
    /// <summary>
    /// Analyze a frame for scene change.
    /// </summary>
    /// <param name="yPlaneData">Luminance (Y) plane data.</param>
    /// <returns>Result indicating whether a scene change was detected.</returns>
    public SceneChangeResult AnalyzeFrame(ReadOnlySpan<byte> yPlaneData)
    {
        currentHistogram.BuildFromYPlane(yPlaneData);
        return AnalyzeCurrentHistogram();
    }
    
    /// <summary>
    /// Analyze a frame for scene change using RGBA data.
    /// </summary>
    public SceneChangeResult AnalyzeFrameRgba(ReadOnlySpan<byte> rgbaData)
    {
        currentHistogram.BuildFromRgba(rgbaData);
        return AnalyzeCurrentHistogram();
    }
    
    private SceneChangeResult AnalyzeCurrentHistogram()
    {
        frameNumber++;
        
        var timestamp = TimeSpan.FromSeconds(frameNumber / FrameRate);
        
        if (!hasPreviousFrame)
        {
            currentHistogram.CopyTo(previousHistogram);
            hasPreviousFrame = true;
            
            return new SceneChangeResult
            {
                FrameNumber = frameNumber,
                Timestamp = timestamp,
                IsSceneChange = true, // First frame is always a "scene"
                DifferenceScore = 1.0,
                Threshold = Threshold
            };
        }
        
        double difference = previousHistogram.CalculateDifference(currentHistogram);
        bool isSceneChange = difference >= Threshold &&
                            (frameNumber - lastSceneChangeFrame) >= MinimumFramesBetweenChanges;
        
        if (isSceneChange)
        {
            lastSceneChangeFrame = frameNumber;
        }
        
        // Swap histograms
        currentHistogram.CopyTo(previousHistogram);
        
        return new SceneChangeResult
        {
            FrameNumber = frameNumber,
            Timestamp = timestamp,
            IsSceneChange = isSceneChange,
            DifferenceScore = difference,
            Threshold = Threshold
        };
    }
    
    /// <summary>
    /// Reset the detector state.
    /// </summary>
    public void Reset()
    {
        hasPreviousFrame = false;
        frameNumber = 0;
        lastSceneChangeFrame = 0;
    }
}

/// <summary>
/// Motion estimation between frames using block matching.
/// </summary>
public readonly struct MotionVector
{
    public int BlockX { get; init; }
    public int BlockY { get; init; }
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
    public int MatchError { get; init; }
    
    /// <summary>Magnitude of the motion vector.</summary>
    public double Magnitude => Math.Sqrt(DeltaX * DeltaX + DeltaY * DeltaY);
    
    /// <summary>Direction of the motion in radians.</summary>
    public double Direction => Math.Atan2(DeltaY, DeltaX);
}

/// <summary>
/// Result of motion estimation analysis.
/// </summary>
public sealed class MotionEstimationResult
{
    public long FrameNumber { get; init; }
    public TimeSpan Timestamp { get; init; }
    public MotionVector[] Vectors { get; init; } = Array.Empty<MotionVector>();
    public double AverageMotion { get; init; }
    public double MaxMotion { get; init; }
    public bool HasSignificantMotion { get; init; }
}

/// <summary>
/// Estimates motion between video frames using block matching.
/// Reference: Diamond search algorithm (simplified)
/// </summary>
public sealed class MotionEstimator
{
    /// <summary>Block size for motion estimation.</summary>
    public int BlockSize { get; set; } = 16;
    
    /// <summary>Search range in pixels.</summary>
    public int SearchRange { get; set; } = 16;
    
    /// <summary>Motion threshold for "significant" motion detection.</summary>
    public double MotionThreshold { get; set; } = 2.0;
    
    /// <summary>Frame rate for timestamp calculation.</summary>
    public double FrameRate { get; set; } = 25.0;
    
    private byte[]? previousFrame;
    private int previousWidth;
    private int previousHeight;
    private long frameNumber;
    
    /// <summary>
    /// Estimate motion between current and previous frame.
    /// </summary>
    /// <param name="yPlaneData">Current frame Y plane.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <returns>Motion estimation result.</returns>
    public MotionEstimationResult EstimateMotion(ReadOnlySpan<byte> yPlaneData, int width, int height)
    {
        frameNumber++;
        var timestamp = TimeSpan.FromSeconds(frameNumber / FrameRate);
        
        if (previousFrame == null || previousWidth != width || previousHeight != height)
        {
            // First frame or resolution change
            previousFrame = yPlaneData.ToArray();
            previousWidth = width;
            previousHeight = height;
            
            return new MotionEstimationResult
            {
                FrameNumber = frameNumber,
                Timestamp = timestamp,
                AverageMotion = 0,
                MaxMotion = 0,
                HasSignificantMotion = false
            };
        }
        
        int blocksX = width / BlockSize;
        int blocksY = height / BlockSize;
        var vectors = new MotionVector[blocksX * blocksY];
        
        double totalMotion = 0;
        double maxMotion = 0;
        int vectorIndex = 0;
        
        ReadOnlySpan<byte> prevSpan = previousFrame;
        
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                var vector = SearchBlock(
                    yPlaneData, prevSpan,
                    width, height,
                    bx * BlockSize, by * BlockSize);
                
                vectors[vectorIndex++] = vector;
                totalMotion += vector.Magnitude;
                maxMotion = Math.Max(maxMotion, vector.Magnitude);
            }
        }
        
        double avgMotion = vectorIndex > 0 ? totalMotion / vectorIndex : 0;
        
        // Store current frame as previous
        yPlaneData.CopyTo(previousFrame);
        
        return new MotionEstimationResult
        {
            FrameNumber = frameNumber,
            Timestamp = timestamp,
            Vectors = vectors,
            AverageMotion = avgMotion,
            MaxMotion = maxMotion,
            HasSignificantMotion = avgMotion >= MotionThreshold
        };
    }
    
    private MotionVector SearchBlock(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous,
        int width, int height,
        int blockX, int blockY)
    {
        int bestDx = 0;
        int bestDy = 0;
        int bestError = int.MaxValue;
        
        // Diamond search pattern
        int[][] diamondPattern = new[]
        {
            new[] { 0, -1 },
            new[] { -1, 0 },
            new[] { 1, 0 },
            new[] { 0, 1 }
        };
        
        int centerDx = 0;
        int centerDy = 0;
        
        // Initial center check
        bestError = CalculateBlockError(current, previous, width, height, blockX, blockY, 0, 0);
        
        // Diamond search
        for (int step = SearchRange; step >= 1; step /= 2)
        {
            bool improved = true;
            while (improved)
            {
                improved = false;
                
                foreach (var pattern in diamondPattern)
                {
                    int dx = centerDx + pattern[0] * step;
                    int dy = centerDy + pattern[1] * step;
                    
                    if (Math.Abs(dx) > SearchRange || Math.Abs(dy) > SearchRange)
                        continue;
                    
                    int error = CalculateBlockError(current, previous, width, height, blockX, blockY, dx, dy);
                    
                    if (error < bestError)
                    {
                        bestError = error;
                        bestDx = dx;
                        bestDy = dy;
                        improved = true;
                    }
                }
                
                if (improved)
                {
                    centerDx = bestDx;
                    centerDy = bestDy;
                }
            }
        }
        
        return new MotionVector
        {
            BlockX = blockX / BlockSize,
            BlockY = blockY / BlockSize,
            DeltaX = bestDx,
            DeltaY = bestDy,
            MatchError = bestError
        };
    }
    
    private int CalculateBlockError(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous,
        int width, int height,
        int blockX, int blockY,
        int dx, int dy)
    {
        int refX = blockX + dx;
        int refY = blockY + dy;
        
        // Check bounds
        if (refX < 0 || refY < 0 || 
            refX + BlockSize > width || refY + BlockSize > height)
        {
            return int.MaxValue;
        }
        
        int sad = 0; // Sum of Absolute Differences
        
        for (int y = 0; y < BlockSize; y++)
        {
            int curRow = (blockY + y) * width + blockX;
            int refRow = (refY + y) * width + refX;
            
            for (int x = 0; x < BlockSize; x++)
            {
                sad += Math.Abs(current[curRow + x] - previous[refRow + x]);
            }
        }
        
        return sad;
    }
    
    /// <summary>
    /// Reset the estimator state.
    /// </summary>
    public void Reset()
    {
        previousFrame = null;
        frameNumber = 0;
    }
}

/// <summary>
/// Detects flash/strobe frames that might cause seizures.
/// </summary>
public sealed class FlashDetector
{
    private readonly Queue<double> luminanceHistory = new();
    private const int HistorySize = 5;
    
    /// <summary>Luminance change threshold for flash detection.</summary>
    public double FlashThreshold { get; set; } = 0.2;
    
    /// <summary>Maximum flashes per second considered safe.</summary>
    public int MaxFlashesPerSecond { get; set; } = 3;
    
    /// <summary>Frame rate for flash frequency calculation.</summary>
    public double FrameRate { get; set; } = 25.0;
    
    private int flashCount;
    private long frameNumber;
    private long windowStartFrame;
    
    /// <summary>
    /// Analyze a frame for flash content.
    /// </summary>
    /// <returns>True if flash rate is potentially dangerous.</returns>
    public bool AnalyzeFrame(ReadOnlySpan<byte> yPlaneData)
    {
        frameNumber++;
        
        // Calculate average luminance
        long sum = 0;
        foreach (byte y in yPlaneData)
            sum += y;
        double avgLuminance = (double)sum / yPlaneData.Length / 255.0;
        
        bool isFlash = false;
        
        if (luminanceHistory.Count > 0)
        {
            double prevLuminance = luminanceHistory.Peek();
            double change = Math.Abs(avgLuminance - prevLuminance);
            
            if (change >= FlashThreshold)
            {
                flashCount++;
                isFlash = true;
            }
        }
        
        luminanceHistory.Enqueue(avgLuminance);
        if (luminanceHistory.Count > HistorySize)
            luminanceHistory.Dequeue();
        
        // Check flash rate over 1 second window
        long framesPerSecond = (long)FrameRate;
        if (frameNumber - windowStartFrame >= framesPerSecond)
        {
            bool dangerous = flashCount > MaxFlashesPerSecond;
            flashCount = 0;
            windowStartFrame = frameNumber;
            return dangerous && isFlash;
        }
        
        return false;
    }
    
    /// <summary>
    /// Reset the detector state.
    /// </summary>
    public void Reset()
    {
        luminanceHistory.Clear();
        flashCount = 0;
        frameNumber = 0;
        windowStartFrame = 0;
    }
}

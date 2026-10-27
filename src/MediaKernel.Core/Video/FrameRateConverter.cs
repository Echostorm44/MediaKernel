// Frame Rate Converter - Convert video between different frame rates
// Reference: VLC modules/video_filter/fps.c

using System;

namespace MediaKernel.Core.Video;

/// <summary>
/// Frame rate conversion mode.
/// </summary>
public enum FrameRateMode
{
    /// <summary>Drop or duplicate frames to match target rate.</summary>
    DropDuplicate,
    
    /// <summary>Blend adjacent frames for smoother output.</summary>
    Blend,
    
    /// <summary>Use motion interpolation (most CPU intensive).</summary>
    MotionInterpolation
}

/// <summary>
/// Result of processing a frame through the frame rate converter.
/// </summary>
public readonly struct FrameRateResult
{
    /// <summary>Number of output frames produced.</summary>
    public int OutputFrameCount { get; init; }
    
    /// <summary>Presentation timestamps for each output frame (in microseconds).</summary>
    public long[] OutputTimestamps { get; init; }
    
    /// <summary>Whether frames need to be blended with next input.</summary>
    public bool NeedsBlending { get; init; }
    
    /// <summary>Blend factor for current frame (0-1).</summary>
    public float BlendFactor { get; init; }
}

/// <summary>
/// Converts video frame rate from source to target.
/// Based on VLC's fps.c filter.
/// </summary>
public sealed class FrameRateConverter
{
    private readonly double sourceFrameRate;
    private readonly double targetFrameRate;
    private readonly long sourceFrameInterval; // microseconds
    private readonly long targetFrameInterval; // microseconds
    private readonly FrameRateMode mode;
    
    private long nextOutputPts;
    private long lastInputPts;
    private bool isInitialized;
    
    /// <summary>Source frame rate in frames per second.</summary>
    public double SourceFrameRate => sourceFrameRate;
    
    /// <summary>Target frame rate in frames per second.</summary>
    public double TargetFrameRate => targetFrameRate;
    
    /// <summary>Conversion mode being used.</summary>
    public FrameRateMode Mode => mode;
    
    /// <summary>Whether we're upsampling (increasing frame rate).</summary>
    public bool IsUpsampling => targetFrameRate > sourceFrameRate;
    
    /// <summary>Whether we're downsampling (decreasing frame rate).</summary>
    public bool IsDownsampling => targetFrameRate < sourceFrameRate;
    
    /// <summary>
    /// Creates a new frame rate converter.
    /// </summary>
    /// <param name="sourceFrameRate">Source frame rate (e.g., 23.976, 24, 25, 29.97, 30, 50, 59.94, 60)</param>
    /// <param name="targetFrameRate">Target frame rate</param>
    /// <param name="mode">Conversion mode</param>
    public FrameRateConverter(double sourceFrameRate, double targetFrameRate, 
        FrameRateMode mode = FrameRateMode.DropDuplicate)
    {
        if (sourceFrameRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrameRate), "Frame rate must be positive");
        if (targetFrameRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFrameRate), "Frame rate must be positive");
        
        this.sourceFrameRate = sourceFrameRate;
        this.targetFrameRate = targetFrameRate;
        this.mode = mode;
        
        // Calculate frame intervals in microseconds
        sourceFrameInterval = (long)(1_000_000.0 / sourceFrameRate);
        targetFrameInterval = (long)(1_000_000.0 / targetFrameRate);
        
        nextOutputPts = -1;
        lastInputPts = -1;
    }
    
    /// <summary>
    /// Process an input frame and determine output timing.
    /// </summary>
    /// <param name="inputPts">Input frame presentation timestamp (microseconds)</param>
    /// <returns>Result indicating how many frames to output and when</returns>
    public FrameRateResult ProcessFrame(long inputPts)
    {
        // Handle invalid timestamp
        if (inputPts < 0)
        {
            return new FrameRateResult { OutputFrameCount = 0 };
        }
        
        // First frame or discontinuity - reset timing
        // Use source frame interval for discontinuity detection (VLC uses output interval but only 1x)
        if (!isInitialized || inputPts > lastInputPts + sourceFrameInterval * 3)
        {
            nextOutputPts = inputPts;
            lastInputPts = inputPts;
            isInitialized = true;
            
            // Advance past first frame
            nextOutputPts += targetFrameInterval;
            
            return new FrameRateResult
            {
                OutputFrameCount = 1,
                OutputTimestamps = new[] { inputPts }
            };
        }
        
        // Check if this frame should be skipped (for downsampling)
        // VLC: if src_date < (next_output_pts - output_frame_interval)
        if (inputPts < nextOutputPts - targetFrameInterval)
        {
            lastInputPts = inputPts;
            return new FrameRateResult { OutputFrameCount = 0 };
        }
        
        // Calculate how many output frames we need
        var timestamps = new System.Collections.Generic.List<long>();
        
        if (mode == FrameRateMode.Blend && IsUpsampling)
        {
            // For blending mode, calculate blend factor
            float blendFactor = (float)(inputPts - lastInputPts) / sourceFrameInterval;
            blendFactor = Math.Clamp(blendFactor, 0f, 1f);
            
            while (nextOutputPts <= inputPts)
            {
                timestamps.Add(nextOutputPts);
                nextOutputPts += targetFrameInterval;
            }
            
            lastInputPts = inputPts;
            
            return new FrameRateResult
            {
                OutputFrameCount = timestamps.Count,
                OutputTimestamps = timestamps.ToArray(),
                NeedsBlending = true,
                BlendFactor = blendFactor
            };
        }
        else
        {
            // Drop/duplicate mode
            // For upsampling: generate frames as long as next_output_pts + interval is before src_date
            // This matches VLC's fps.c logic on line 121
            while (nextOutputPts + targetFrameInterval < inputPts)
            {
                timestamps.Add(nextOutputPts);
                nextOutputPts += targetFrameInterval;
            }
            
            // Always output at least one frame (the "current" frame)
            timestamps.Add(nextOutputPts);
            nextOutputPts += targetFrameInterval;
            
            lastInputPts = inputPts;
            
            return new FrameRateResult
            {
                OutputFrameCount = timestamps.Count,
                OutputTimestamps = timestamps.ToArray()
            };
        }
    }
    
    /// <summary>
    /// Blend two frames together for smooth frame rate conversion.
    /// </summary>
    /// <param name="frame1">First frame (BGRA)</param>
    /// <param name="frame2">Second frame (BGRA)</param>
    /// <param name="output">Output buffer</param>
    /// <param name="blendFactor">Blend factor (0 = frame1, 1 = frame2)</param>
    public static void BlendFrames(ReadOnlySpan<byte> frame1, ReadOnlySpan<byte> frame2, 
        Span<byte> output, float blendFactor)
    {
        if (frame1.Length != frame2.Length || frame1.Length != output.Length)
            throw new ArgumentException("All buffers must be the same size");
        
        blendFactor = Math.Clamp(blendFactor, 0f, 1f);
        float invBlend = 1f - blendFactor;
        
        int length = frame1.Length;
        
        // Process 4 bytes at a time (one BGRA pixel)
        for (int i = 0; i < length; i++)
        {
            output[i] = (byte)(frame1[i] * invBlend + frame2[i] * blendFactor);
        }
    }
    
    /// <summary>
    /// Reset the converter state (call when seeking or changing source).
    /// </summary>
    public void Reset()
    {
        nextOutputPts = -1;
        lastInputPts = -1;
        isInitialized = false;
    }
    
    /// <summary>
    /// Get common frame rate conversions info.
    /// </summary>
    public static string GetConversionDescription(double source, double target)
    {
        // Common conversions
        bool is24to30 = Math.Abs(source - 23.976) < 0.1 && Math.Abs(target - 29.97) < 0.1;
        bool is24to60 = Math.Abs(source - 23.976) < 0.1 && Math.Abs(target - 59.94) < 0.1;
        bool is25to30 = Math.Abs(source - 25) < 0.1 && Math.Abs(target - 30) < 0.1;
        bool is30to24 = Math.Abs(source - 29.97) < 0.1 && Math.Abs(target - 23.976) < 0.1;
        bool is60to30 = Math.Abs(source - 59.94) < 0.1 && Math.Abs(target - 29.97) < 0.1;
        
        if (is24to30) return "3:2 Pulldown (Film to NTSC)";
        if (is24to60) return "Film to 60Hz (2.5x duplication)";
        if (is25to30) return "PAL to NTSC conversion";
        if (is30to24) return "Inverse Telecine (NTSC to Film)";
        if (is60to30) return "2:1 Decimation";
        
        double ratio = target / source;
        if (ratio > 1)
            return $"{ratio:F2}x Upsampling";
        else
            return $"{1/ratio:F2}x Downsampling";
    }
}

/// <summary>
/// Common film and video frame rates.
/// </summary>
public static class CommonFrameRates
{
    /// <summary>Film (NTSC compatible) - 23.976 fps</summary>
    public const double Film = 24000.0 / 1001.0; // 23.976...
    
    /// <summary>Film (exact) - 24 fps</summary>
    public const double Film24 = 24.0;
    
    /// <summary>PAL - 25 fps</summary>
    public const double Pal = 25.0;
    
    /// <summary>NTSC - 29.97 fps</summary>
    public const double Ntsc = 30000.0 / 1001.0; // 29.97...
    
    /// <summary>30 fps (exact)</summary>
    public const double Fps30 = 30.0;
    
    /// <summary>PAL high - 50 fps</summary>
    public const double Pal50 = 50.0;
    
    /// <summary>NTSC high - 59.94 fps</summary>
    public const double Ntsc60 = 60000.0 / 1001.0; // 59.94...
    
    /// <summary>60 fps (exact)</summary>
    public const double Fps60 = 60.0;
    
    /// <summary>High frame rate cinema - 48 fps</summary>
    public const double Hfr48 = 48.0;
    
    /// <summary>Gaming/high refresh - 120 fps</summary>
    public const double Fps120 = 120.0;
    
    /// <summary>Gaming/high refresh - 144 fps</summary>
    public const double Fps144 = 144.0;
}

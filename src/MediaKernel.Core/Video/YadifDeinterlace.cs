// YadifDeinterlace - Yet Another DeInterlacing Filter
// Ported from VLC's yadif implementation with C# idioms and SIMD optimization

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Yadif (Yet Another DeInterlacing Filter) implementation.
/// Uses spatial and temporal interpolation to produce high-quality deinterlaced output.
/// </summary>
public sealed class YadifDeinterlace
{
    private readonly int width;
    private readonly int height;
    private readonly int stride;
    
    // History buffer for temporal filtering (prev, current, next)
    private byte[]? previousFrame;
    private byte[]? currentFrame;
    private byte[]? nextFrame;
    private int frameCount;
    
    private readonly bool doubleFrameRate;
    private readonly object lockObject = new();
    
    /// <summary>Gets the output width.</summary>
    public int Width => width;
    
    /// <summary>Gets the output height.</summary>
    public int Height => height;
    
    /// <summary>Gets whether frame rate doubling is enabled.</summary>
    public bool DoubleFrameRate => doubleFrameRate;
    
    /// <summary>
    /// Creates a new Yadif deinterlacer.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Bytes per row (may be padded).</param>
    /// <param name="doubleFrameRate">If true, outputs 2 frames per input (yadif2x mode).</param>
    public YadifDeinterlace(int width, int height, int stride, bool doubleFrameRate = false)
    {
        this.width = width;
        this.height = height;
        this.stride = stride > 0 ? stride : width;
        this.doubleFrameRate = doubleFrameRate;
    }
    
    /// <summary>
    /// Feeds a new frame into the deinterlacer.
    /// </summary>
    /// <param name="frame">Input interlaced frame data.</param>
    public void FeedFrame(ReadOnlySpan<byte> frame)
    {
        lock (lockObject)
        {
            // Rotate frame buffers
            (previousFrame, currentFrame, nextFrame) = (currentFrame, nextFrame, previousFrame);
            
            // Copy new frame to nextFrame buffer
            if (nextFrame == null || nextFrame.Length < frame.Length)
                nextFrame = new byte[frame.Length];
            
            frame.CopyTo(nextFrame);
            frameCount++;
        }
    }
    
    /// <summary>
    /// Deinterlaces the current frame.
    /// </summary>
    /// <param name="output">Output buffer for deinterlaced frame.</param>
    /// <param name="parity">Field parity: 0 = bottom field first, 1 = top field first.</param>
    /// <returns>True if output was produced, false if not enough frames buffered.</returns>
    public bool Deinterlace(Span<byte> output, int parity)
    {
        lock (lockObject)
        {
            if (frameCount < 3 || previousFrame == null || currentFrame == null || nextFrame == null)
                return false;
            
            // Process each line
            for (int y = 0; y < height; y++)
            {
                int dstOffset = y * stride;
                
                if (y == 0 || y == height - 1)
                {
                    // First and last lines: just copy from current frame
                    currentFrame.AsSpan(dstOffset, width).CopyTo(output.Slice(dstOffset, width));
                }
                else if ((y % 2) == parity)
                {
                    // This line belongs to the field we're keeping - copy as-is
                    currentFrame.AsSpan(dstOffset, width).CopyTo(output.Slice(dstOffset, width));
                }
                else
                {
                    // This line needs to be interpolated
                    FilterLine(
                        output.Slice(dstOffset, width),
                        previousFrame.AsSpan(),
                        currentFrame.AsSpan(),
                        nextFrame.AsSpan(),
                        y,
                        parity);
                }
            }
            
            return true;
        }
    }
    
    /// <summary>
    /// Deinterlaces the current frame for yadif2x mode (both field outputs).
    /// </summary>
    /// <param name="outputFirst">First output frame (field 0).</param>
    /// <param name="outputSecond">Second output frame (field 1).</param>
    /// <returns>True if both outputs were produced.</returns>
    public bool Deinterlace2x(Span<byte> outputFirst, Span<byte> outputSecond)
    {
        bool first = Deinterlace(outputFirst, 0);
        bool second = Deinterlace(outputSecond, 1);
        return first && second;
    }
    
    /// <summary>
    /// Resets the frame history (call after seek).
    /// </summary>
    public void Reset()
    {
        lock (lockObject)
        {
            frameCount = 0;
            previousFrame = null;
            currentFrame = null;
            nextFrame = null;
        }
    }
    
    /// <summary>
    /// Filters a single line using the Yadif algorithm.
    /// </summary>
    private void FilterLine(
        Span<byte> dst,
        ReadOnlySpan<byte> prev,
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> next,
        int y,
        int parity)
    {
        // Line offsets for current, previous, and next lines
        int curLineOffset = y * stride;
        int prevLineAbove = (y - 1) * stride;
        int prevLineBelow = (y + 1) * stride;
        
        // Use SIMD if available
        if (Sse2.IsSupported && width >= 16)
        {
            FilterLineSse2(dst, prev, cur, next, curLineOffset, prevLineAbove, prevLineBelow);
        }
        else
        {
            FilterLineScalar(dst, prev, cur, next, curLineOffset, prevLineAbove, prevLineBelow);
        }
    }
    
    /// <summary>
    /// Scalar implementation of Yadif line filtering.
    /// </summary>
    private void FilterLineScalar(
        Span<byte> dst,
        ReadOnlySpan<byte> prev,
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> next,
        int curOffset,
        int aboveOffset,
        int belowOffset)
    {
        // First and last pixels: just average
        dst[0] = (byte)((cur[aboveOffset] + cur[belowOffset] + 1) >> 1);
        dst[width - 1] = (byte)((cur[aboveOffset + width - 1] + cur[belowOffset + width - 1] + 1) >> 1);
        
        for (int x = 1; x < width - 1; x++)
        {
            // Get pixels from current frame (line above and below)
            int c = cur[aboveOffset + x];    // Center above
            int e = cur[belowOffset + x];    // Center below
            
            // Temporal reference: previous and next frames at same position
            int prevPix = prev[curOffset + x];
            int nextPix = next[curOffset + x];
            
            // Temporal difference (motion detection)
            int temporalDiff0 = Math.Abs(prevPix - nextPix);
            
            // Additional temporal differences for edge detection
            int temporalDiff1 = (Math.Abs(prev[aboveOffset + x] - c) + 
                                  Math.Abs(prev[belowOffset + x] - e)) >> 1;
            int temporalDiff2 = (Math.Abs(next[aboveOffset + x] - c) + 
                                  Math.Abs(next[belowOffset + x] - e)) >> 1;
            
            int diff = Math.Max(temporalDiff0 >> 1, Math.Max(temporalDiff1, temporalDiff2));
            
            // Temporal average
            int d = (prevPix + nextPix + 1) >> 1;
            
            // Spatial prediction: start with simple average
            int spatialPred = (c + e + 1) >> 1;
            
            // Check for edges and select best spatial prediction
            // Looking at horizontal neighbors to detect edges
            int score = Math.Abs(cur[aboveOffset + x - 1] - cur[belowOffset + x - 1]) +
                       Math.Abs(c - e) +
                       Math.Abs(cur[aboveOffset + x + 1] - cur[belowOffset + x + 1]);
            
            // Check diagonal patterns for better edge handling
            if (x > 1 && x < width - 2)
            {
                // Check score at x-1, x+1 offsets
                int scoreLeft = Math.Abs(cur[aboveOffset + x - 2] - cur[belowOffset + x]) +
                               Math.Abs(cur[aboveOffset + x - 1] - cur[belowOffset + x + 1]) +
                               Math.Abs(c - cur[belowOffset + x + 2]);
                
                int scoreRight = Math.Abs(cur[aboveOffset + x + 2] - cur[belowOffset + x]) +
                                Math.Abs(cur[aboveOffset + x + 1] - cur[belowOffset + x - 1]) +
                                Math.Abs(c - cur[belowOffset + x - 2]);
                
                // If a diagonal has lower score, use that prediction
                if (scoreLeft < score && scoreLeft <= scoreRight)
                {
                    spatialPred = (cur[aboveOffset + x - 1] + cur[belowOffset + x + 1] + 1) >> 1;
                    score = scoreLeft;
                }
                else if (scoreRight < score)
                {
                    spatialPred = (cur[aboveOffset + x + 1] + cur[belowOffset + x - 1] + 1) >> 1;
                    score = scoreRight;
                }
            }
            
            // If no motion, use spatial prediction directly
            // If motion detected, clamp spatial prediction to temporal bounds
            int output;
            if (diff > 0)
            {
                int min = d - diff;
                int max = d + diff;
                output = Math.Clamp(spatialPred, min, max);
            }
            else
            {
                output = spatialPred;
            }
            
            dst[x] = (byte)Math.Clamp(output, 0, 255);
        }
    }
    
    /// <summary>
    /// SSE2 optimized implementation of Yadif line filtering.
    /// Processes 16 pixels at a time.
    /// </summary>
    private unsafe void FilterLineSse2(
        Span<byte> dst,
        ReadOnlySpan<byte> prev,
        ReadOnlySpan<byte> cur,
        ReadOnlySpan<byte> next,
        int curOffset,
        int aboveOffset,
        int belowOffset)
    {
        fixed (byte* pDst = dst)
        fixed (byte* pPrev = prev)
        fixed (byte* pCur = cur)
        fixed (byte* pNext = next)
        {
            // Handle edges with scalar
            dst[0] = (byte)((cur[aboveOffset] + cur[belowOffset] + 1) >> 1);
            dst[width - 1] = (byte)((cur[aboveOffset + width - 1] + cur[belowOffset + width - 1] + 1) >> 1);
            
            int vectorWidth = width - 2; // Exclude first and last pixel
            int simdWidth = vectorWidth & ~15; // Process 16 bytes at a time
            
            for (int x = 1; x < simdWidth + 1; x += 16)
            {
                // Load 16 bytes from each source
                var curAbove = Sse2.LoadVector128(pCur + aboveOffset + x);
                var curBelow = Sse2.LoadVector128(pCur + belowOffset + x);
                var prevCenter = Sse2.LoadVector128(pPrev + curOffset + x);
                var nextCenter = Sse2.LoadVector128(pNext + curOffset + x);
                
                // Temporal average: (prev + next + 1) >> 1
                var temporalAvg = Sse2.Average(prevCenter, nextCenter);
                
                // Spatial average: (above + below + 1) >> 1
                var spatialPred = Sse2.Average(curAbove, curBelow);
                
                // Temporal difference: |prev - next|
                var diffA = Sse2.SubtractSaturate(prevCenter, nextCenter);
                var diffB = Sse2.SubtractSaturate(nextCenter, prevCenter);
                var temporalDiff = Sse2.Or(diffA, diffB);
                
                // Simple motion-adaptive blend
                // If high motion (diff > 0), use more spatial prediction
                // If low motion, blend towards temporal
                
                // For now, use a simplified approach: blend spatial and temporal
                // Full Yadif would need more complex edge detection
                var halfDiff = Sse2.ShiftRightLogical(
                    Sse2.UnpackLow(temporalDiff, Vector128<byte>.Zero).AsUInt16(), 1).AsByte();
                
                // Clamp spatial prediction to temporal bounds
                // min = temporal - diff, max = temporal + diff
                var minVal = Sse2.SubtractSaturate(temporalAvg, temporalDiff);
                var maxVal = Sse2.AddSaturate(temporalAvg, temporalDiff);
                
                // Clamp: result = max(min(spatial, max), min)
                var clamped = Sse2.Max(Sse2.Min(spatialPred, maxVal), minVal);
                
                // Store result
                Sse2.Store(pDst + x, clamped);
            }
            
            // Handle remaining pixels with scalar
            for (int x = simdWidth + 1; x < width - 1; x++)
            {
                int c = cur[aboveOffset + x];
                int e = cur[belowOffset + x];
                int prevPix = prev[curOffset + x];
                int nextPix = next[curOffset + x];
                
                int d = (prevPix + nextPix + 1) >> 1;
                int spatialPred = (c + e + 1) >> 1;
                int diff = Math.Abs(prevPix - nextPix) >> 1;
                
                int output = diff > 0 
                    ? Math.Clamp(spatialPred, d - diff, d + diff)
                    : spatialPred;
                
                dst[x] = (byte)Math.Clamp(output, 0, 255);
            }
        }
    }
}

/// <summary>
/// Simpler deinterlacing algorithms for lower CPU usage.
/// </summary>
public static class SimpleDeinterlace
{
    /// <summary>
    /// Bob deinterlacing: simple line doubling from one field.
    /// Fast but can produce visible stepping on motion.
    /// </summary>
    public static void Bob(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride, int field)
    {
        for (int y = 0; y < height; y++)
        {
            int srcY = (y / 2) * 2 + field;
            if (srcY >= height) srcY = height - 1;
            
            int srcOffset = srcY * stride;
            int dstOffset = y * stride;
            
            input.Slice(srcOffset, width).CopyTo(output.Slice(dstOffset, width));
        }
    }
    
    /// <summary>
    /// Blend deinterlacing: averages adjacent lines.
    /// Reduces flicker but can cause ghosting on motion.
    /// </summary>
    public static void Blend(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride)
    {
        // First line: copy
        input.Slice(0, width).CopyTo(output.Slice(0, width));
        
        // Middle lines: blend
        for (int y = 1; y < height - 1; y++)
        {
            int curOffset = y * stride;
            int prevOffset = (y - 1) * stride;
            int nextOffset = (y + 1) * stride;
            
            for (int x = 0; x < width; x++)
            {
                // Average of 3 lines
                int sum = input[prevOffset + x] + input[curOffset + x] * 2 + input[nextOffset + x];
                output[curOffset + x] = (byte)((sum + 2) >> 2);
            }
        }
        
        // Last line: copy
        int lastOffset = (height - 1) * stride;
        input.Slice(lastOffset, width).CopyTo(output.Slice(lastOffset, width));
    }
    
    /// <summary>
    /// Linear deinterlacing: interpolates missing lines.
    /// Good balance of quality and speed.
    /// </summary>
    public static void Linear(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride, int field)
    {
        for (int y = 0; y < height; y++)
        {
            int dstOffset = y * stride;
            
            if ((y % 2) == field)
            {
                // Keep this line
                input.Slice(dstOffset, width).CopyTo(output.Slice(dstOffset, width));
            }
            else
            {
                // Interpolate from adjacent lines
                int aboveY = Math.Max(0, y - 1);
                int belowY = Math.Min(height - 1, y + 1);
                
                int aboveOffset = aboveY * stride;
                int belowOffset = belowY * stride;
                
                for (int x = 0; x < width; x++)
                {
                    output[dstOffset + x] = (byte)((input[aboveOffset + x] + input[belowOffset + x] + 1) >> 1);
                }
            }
        }
    }
    
    /// <summary>
    /// Discard deinterlacing: keeps only one field (half vertical resolution).
    /// Fastest but loses half the vertical information.
    /// </summary>
    public static void Discard(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride, int field)
    {
        int outputHeight = height / 2;
        int outputStride = stride; // Keep same stride for simplicity
        
        for (int y = 0; y < outputHeight; y++)
        {
            int srcY = y * 2 + field;
            int srcOffset = srcY * stride;
            int dstOffset = y * outputStride;
            
            input.Slice(srcOffset, width).CopyTo(output.Slice(dstOffset, width));
        }
    }
}

/// <summary>
/// Deinterlacing mode selection.
/// </summary>
public enum DeinterlaceMode
{
    /// <summary>No deinterlacing.</summary>
    Off,
    
    /// <summary>Simple line doubling (fast).</summary>
    Bob,
    
    /// <summary>Line blending (medium quality).</summary>
    Blend,
    
    /// <summary>Linear interpolation (good quality).</summary>
    Linear,
    
    /// <summary>Yadif motion-adaptive (high quality).</summary>
    Yadif,
    
    /// <summary>Yadif with frame rate doubling (highest quality).</summary>
    Yadif2x,
    
    /// <summary>Discard one field (lowest quality, half resolution).</summary>
    Discard
}

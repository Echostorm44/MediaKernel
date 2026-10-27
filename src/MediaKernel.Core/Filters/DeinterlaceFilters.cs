// MediaKernel - Deinterlace Filters
// Linear blend, bob, and weave deinterlacing algorithms

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Field order for interlaced video.
/// </summary>
public enum FieldOrder
{
    /// <summary>Top field first (TFF).</summary>
    TopFieldFirst,
    /// <summary>Bottom field first (BFF).</summary>
    BottomFieldFirst
}

/// <summary>
/// Deinterlacing algorithm selection.
/// </summary>
public enum DeinterlaceAlgorithm
{
    /// <summary>No deinterlacing (passthrough).</summary>
    None,
    /// <summary>Simple line doubling - fast but lowest quality.</summary>
    Bob,
    /// <summary>Linear blend between fields - medium quality.</summary>
    LinearBlend,
    /// <summary>Weave both fields together - good for still content.</summary>
    Weave,
    /// <summary>Discard one field and interpolate - medium quality.</summary>
    Discard,
    /// <summary>Adaptive blend based on motion detection.</summary>
    Adaptive,
    /// <summary>YADIF-style edge-directed interpolation.</summary>
    Yadif
}

/// <summary>
/// Deinterlace filter for converting interlaced video to progressive.
/// </summary>
public sealed class DeinterlaceFilter
{
    private DeinterlaceAlgorithm algorithm = DeinterlaceAlgorithm.LinearBlend;
    private FieldOrder fieldOrder = FieldOrder.TopFieldFirst;
    private bool currentFieldIsTop = true;
    private byte[] previousFrame;
    
    /// <summary>
    /// Deinterlacing algorithm to use.
    /// </summary>
    public DeinterlaceAlgorithm Algorithm
    {
        get => algorithm;
        set => algorithm = value;
    }
    
    /// <summary>
    /// Field order of the source video.
    /// </summary>
    public FieldOrder FieldOrder
    {
        get => fieldOrder;
        set => fieldOrder = value;
    }
    
    /// <summary>
    /// Process a frame using the selected deinterlacing algorithm.
    /// </summary>
    /// <param name="frameData">Frame data (modified in place).</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="bytesPerPixel">Bytes per pixel (default 4 for RGBA).</param>
    public void Process(Span<byte> frameData, int width, int height, int bytesPerPixel = 4)
    {
        switch (algorithm)
        {
            case DeinterlaceAlgorithm.None:
                break;
            case DeinterlaceAlgorithm.Bob:
                ProcessBob(frameData, width, height, bytesPerPixel);
                break;
            case DeinterlaceAlgorithm.LinearBlend:
                ProcessLinearBlend(frameData, width, height, bytesPerPixel);
                break;
            case DeinterlaceAlgorithm.Weave:
                ProcessWeave(frameData, width, height, bytesPerPixel);
                break;
            case DeinterlaceAlgorithm.Discard:
                ProcessDiscard(frameData, width, height, bytesPerPixel);
                break;
            case DeinterlaceAlgorithm.Adaptive:
                ProcessAdaptive(frameData, width, height, bytesPerPixel);
                break;
            case DeinterlaceAlgorithm.Yadif:
                ProcessYadif(frameData, width, height, bytesPerPixel);
                break;
        }
        
        // Alternate fields for bob/weave algorithms
        currentFieldIsTop = !currentFieldIsTop;
    }
    
    /// <summary>
    /// Bob deinterlacing - doubles lines from one field.
    /// Fast but can cause vertical bobbing.
    /// </summary>
    private void ProcessBob(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        bool processTopField = currentFieldIsTop == (fieldOrder == FieldOrder.TopFieldFirst);
        
        if (processTopField)
        {
            // Fill odd lines by copying from even lines above
            for (int y = 1; y < height; y += 2)
            {
                int srcOffset = (y - 1) * stride;
                int dstOffset = y * stride;
                frameData.Slice(srcOffset, stride).CopyTo(frameData.Slice(dstOffset, stride));
            }
        }
        else
        {
            // Fill even lines by copying from odd lines below
            for (int y = 0; y < height - 1; y += 2)
            {
                int srcOffset = (y + 1) * stride;
                int dstOffset = y * stride;
                frameData.Slice(srcOffset, stride).CopyTo(frameData.Slice(dstOffset, stride));
            }
        }
    }
    
    /// <summary>
    /// Linear blend deinterlacing - averages adjacent lines.
    /// Good balance of quality and speed.
    /// </summary>
    private void ProcessLinearBlend(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        
        // SIMD path
        if (Avx2.IsSupported && stride >= 32)
        {
            ProcessLinearBlendAvx2(frameData, width, height, bytesPerPixel);
            return;
        }
        
        // Scalar fallback
        for (int y = 1; y < height - 1; y++)
        {
            int prevOffset = (y - 1) * stride;
            int currOffset = y * stride;
            int nextOffset = (y + 1) * stride;
            
            for (int x = 0; x < stride; x++)
            {
                // Average of line above, current, and line below (weighted)
                int avg = (frameData[prevOffset + x] + frameData[currOffset + x] * 2 + frameData[nextOffset + x]) / 4;
                frameData[currOffset + x] = (byte)avg;
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessLinearBlendAvx2(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        
        for (int y = 1; y < height - 1; y++)
        {
            int prevOffset = (y - 1) * stride;
            int currOffset = y * stride;
            int nextOffset = (y + 1) * stride;
            
            int x = 0;
            int vectorEnd = stride - 31;
            
            for (; x < vectorEnd; x += 32)
            {
                // Load 32 bytes from each line
                var prev = Vector256.Create(frameData.Slice(prevOffset + x, 32));
                var curr = Vector256.Create(frameData.Slice(currOffset + x, 32));
                var next = Vector256.Create(frameData.Slice(nextOffset + x, 32));
                
                // Average: (prev + curr*2 + next) / 4
                // Using shifts to avoid overflow: avg of (prev/2 + curr + next/2) / 2
                var prevHalf = Avx2.ShiftRightLogical(prev.AsUInt16(), 1).AsByte();
                var nextHalf = Avx2.ShiftRightLogical(next.AsUInt16(), 1).AsByte();
                
                // Simplified averaging using unpacks for proper byte averaging
                var sum1 = Avx2.Average(prev, next);
                var result = Avx2.Average(sum1, curr);
                
                result.CopyTo(frameData.Slice(currOffset + x, 32));
            }
            
            // Handle remaining bytes
            for (; x < stride; x++)
            {
                int avg = (frameData[prevOffset + x] + frameData[currOffset + x] * 2 + frameData[nextOffset + x]) / 4;
                frameData[currOffset + x] = (byte)avg;
            }
        }
    }
    
    /// <summary>
    /// Weave deinterlacing - combines fields from consecutive frames.
    /// Good for static content but causes combing on motion.
    /// </summary>
    private void ProcessWeave(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        int frameSize = stride * height;
        
        // Initialize previous frame storage if needed
        if (previousFrame == null || previousFrame.Length != frameSize)
        {
            previousFrame = new byte[frameSize];
            frameData.CopyTo(previousFrame);
            return;
        }
        
        // Weave: take one field from current, one from previous
        bool useCurrentForEven = currentFieldIsTop == (fieldOrder == FieldOrder.TopFieldFirst);
        
        for (int y = 0; y < height; y++)
        {
            bool isEvenLine = (y % 2) == 0;
            int offset = y * stride;
            
            if (isEvenLine == useCurrentForEven)
            {
                // Keep current frame's line
                // Copy current to previous for next iteration
                frameData.Slice(offset, stride).CopyTo(previousFrame.AsSpan(offset, stride));
            }
            else
            {
                // Use previous frame's line
                previousFrame.AsSpan(offset, stride).CopyTo(frameData.Slice(offset, stride));
            }
        }
    }
    
    /// <summary>
    /// Discard one field and interpolate missing lines.
    /// </summary>
    private void ProcessDiscard(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        bool discardOdd = currentFieldIsTop == (fieldOrder == FieldOrder.TopFieldFirst);
        
        if (discardOdd)
        {
            // Interpolate odd lines from even lines
            for (int y = 1; y < height - 1; y += 2)
            {
                int prevOffset = (y - 1) * stride;
                int currOffset = y * stride;
                int nextOffset = (y + 1) * stride;
                
                for (int x = 0; x < stride; x++)
                {
                    frameData[currOffset + x] = (byte)((frameData[prevOffset + x] + frameData[nextOffset + x]) / 2);
                }
            }
            
            // Last odd line copies from previous
            if (height > 1 && (height - 1) % 2 == 1)
            {
                int srcOffset = (height - 2) * stride;
                int dstOffset = (height - 1) * stride;
                frameData.Slice(srcOffset, stride).CopyTo(frameData.Slice(dstOffset, stride));
            }
        }
        else
        {
            // Interpolate even lines from odd lines
            for (int y = 2; y < height; y += 2)
            {
                int prevOffset = (y - 1) * stride;
                int currOffset = y * stride;
                int nextOffset = Math.Min(y + 1, height - 1) * stride;
                
                for (int x = 0; x < stride; x++)
                {
                    frameData[currOffset + x] = (byte)((frameData[prevOffset + x] + frameData[nextOffset + x]) / 2);
                }
            }
            
            // First even line copies from first odd line
            if (height > 1)
            {
                frameData.Slice(stride, stride).CopyTo(frameData.Slice(0, stride));
            }
        }
    }
    
    /// <summary>
    /// Adaptive deinterlacing - blends based on detected motion.
    /// </summary>
    private void ProcessAdaptive(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        int frameSize = stride * height;
        
        // Initialize previous frame storage if needed
        if (previousFrame == null || previousFrame.Length != frameSize)
        {
            previousFrame = new byte[frameSize];
            frameData.CopyTo(previousFrame);
            ProcessLinearBlend(frameData, width, height, bytesPerPixel);
            return;
        }
        
        // Motion threshold
        const int MotionThreshold = 20;
        
        for (int y = 1; y < height - 1; y++)
        {
            int prevOffset = (y - 1) * stride;
            int currOffset = y * stride;
            int nextOffset = (y + 1) * stride;
            
            for (int x = 0; x < stride; x++)
            {
                byte current = frameData[currOffset + x];
                byte previous = previousFrame[currOffset + x];
                
                // Detect motion by comparing with previous frame
                int motion = Math.Abs(current - previous);
                
                if (motion > MotionThreshold)
                {
                    // Motion detected - use linear blend
                    int blended = (frameData[prevOffset + x] + current * 2 + frameData[nextOffset + x]) / 4;
                    frameData[currOffset + x] = (byte)blended;
                }
                // else: no motion - keep weaved result (current pixel)
                
                // Update previous frame
                previousFrame[currOffset + x] = current;
            }
        }
    }
    
    /// <summary>
    /// YADIF-style edge-directed interpolation.
    /// High quality but computationally expensive.
    /// </summary>
    private void ProcessYadif(Span<byte> frameData, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        int frameSize = stride * height;
        
        // Initialize previous frame storage if needed
        if (previousFrame == null || previousFrame.Length != frameSize)
        {
            previousFrame = new byte[frameSize];
            frameData.CopyTo(previousFrame);
            ProcessLinearBlend(frameData, width, height, bytesPerPixel);
            return;
        }
        
        // Process lines to interpolate
        bool interpolateOdd = currentFieldIsTop == (fieldOrder == FieldOrder.TopFieldFirst);
        int startLine = interpolateOdd ? 1 : 2;
        int step = 2;
        
        for (int y = startLine; y < height - 1; y += step)
        {
            int prevLineOffset = (y - 1) * stride;
            int currLineOffset = y * stride;
            int nextLineOffset = (y + 1) * stride;
            int prevPrevLineOffset = Math.Max(0, y - 2) * stride;
            int nextNextLineOffset = Math.Min(height - 1, y + 2) * stride;
            
            for (int x = bytesPerPixel; x < stride - bytesPerPixel; x++)
            {
                // Get pixels from current and previous frames
                byte prevLineVal = frameData[prevLineOffset + x];
                byte nextLineVal = frameData[nextLineOffset + x];
                byte prevPrevLineVal = frameData[prevPrevLineOffset + x];
                byte nextNextLineVal = frameData[nextNextLineOffset + x];
                
                // Previous frame values
                byte prevFramePrevLine = previousFrame[prevLineOffset + x];
                byte prevFrameCurr = previousFrame[currLineOffset + x];
                byte prevFrameNextLine = previousFrame[nextLineOffset + x];
                
                // Calculate spatial prediction
                int spatialPred = (prevLineVal + nextLineVal) / 2;
                
                // Calculate temporal prediction
                int temporalPred = (prevFramePrevLine + prevFrameNextLine + prevLineVal + nextLineVal) / 4;
                
                // Calculate spatial score (edge detection)
                int spatialScore = Math.Abs(prevLineVal - nextLineVal);
                
                // Calculate diagonal scores
                int diagScore1 = Math.Abs(frameData[prevLineOffset + x - bytesPerPixel] - frameData[nextLineOffset + x + bytesPerPixel]);
                int diagScore2 = Math.Abs(frameData[prevLineOffset + x + bytesPerPixel] - frameData[nextLineOffset + x - bytesPerPixel]);
                
                int interpolated;
                
                // Choose interpolation method based on edge direction
                if (diagScore1 < spatialScore && diagScore1 < diagScore2)
                {
                    // Diagonal edge (top-left to bottom-right)
                    interpolated = (frameData[prevLineOffset + x - bytesPerPixel] + frameData[nextLineOffset + x + bytesPerPixel]) / 2;
                }
                else if (diagScore2 < spatialScore)
                {
                    // Diagonal edge (top-right to bottom-left)
                    interpolated = (frameData[prevLineOffset + x + bytesPerPixel] + frameData[nextLineOffset + x - bytesPerPixel]) / 2;
                }
                else
                {
                    // Vertical or no clear edge - use temporal-spatial blend
                    interpolated = (spatialPred + temporalPred) / 2;
                }
                
                // Apply motion-adaptive filter
                int motion = Math.Abs(prevFrameCurr - frameData[currLineOffset + x]);
                if (motion < 10)
                {
                    // Low motion - weave from previous frame
                    interpolated = (interpolated + prevFrameCurr) / 2;
                }
                
                // Clamp based on neighboring pixels to avoid artifacts
                int minVal = Math.Min(prevLineVal, nextLineVal) - 5;
                int maxVal = Math.Max(prevLineVal, nextLineVal) + 5;
                interpolated = Math.Clamp(interpolated, Math.Max(0, minVal), Math.Min(255, maxVal));
                
                frameData[currLineOffset + x] = (byte)interpolated;
            }
        }
        
        // Update previous frame
        frameData.CopyTo(previousFrame);
    }
    
    /// <summary>
    /// Reset filter state (clear previous frame buffer).
    /// </summary>
    public void Reset()
    {
        previousFrame = null;
        currentFieldIsTop = true;
    }
}

/// <summary>
/// Utility for detecting interlaced content in video frames.
/// </summary>
public static class InterlaceDetector
{
    /// <summary>
    /// Analyzes a frame to estimate if it's interlaced.
    /// Returns a score from 0 (progressive) to 1 (definitely interlaced).
    /// </summary>
    public static float AnalyzeFrame(ReadOnlySpan<byte> frameData, int width, int height, int bytesPerPixel = 4)
    {
        if (height < 4)
            return 0f;
        
        int stride = width * bytesPerPixel;
        long combingScore = 0;
        long totalScore = 0;
        
        // Sample pixels to detect combing artifacts
        int sampleCount = 0;
        
        for (int y = 2; y < height - 2; y += 4)
        {
            for (int x = bytesPerPixel; x < stride - bytesPerPixel; x += bytesPerPixel * 8)
            {
                // Get luminance from surrounding lines
                int prevPrevLine = (y - 2) * stride + x;
                int prevLine = (y - 1) * stride + x;
                int currLine = y * stride + x;
                int nextLine = (y + 1) * stride + x;
                int nextNextLine = (y + 2) * stride + x;
                
                // Use green channel as luminance approximation
                int lumPrevPrev = frameData[prevPrevLine + 1];
                int lumPrev = frameData[prevLine + 1];
                int lumCurr = frameData[currLine + 1];
                int lumNext = frameData[nextLine + 1];
                int lumNextNext = frameData[nextNextLine + 1];
                
                // Combing detection: check if adjacent field lines differ significantly
                // while same-field lines are similar
                int interFieldDiff = Math.Abs(lumPrev - lumCurr) + Math.Abs(lumCurr - lumNext);
                int intraFieldDiff = Math.Abs(lumPrevPrev - lumCurr) + Math.Abs(lumCurr - lumNextNext);
                
                // High inter-field difference with low intra-field difference = combing
                if (interFieldDiff > 20 && intraFieldDiff < interFieldDiff / 2)
                {
                    combingScore += interFieldDiff - intraFieldDiff;
                }
                
                totalScore += Math.Max(interFieldDiff, 1);
                sampleCount++;
            }
        }
        
        if (sampleCount == 0 || totalScore == 0)
            return 0f;
        
        return Math.Clamp((float)combingScore / totalScore, 0f, 1f);
    }
    
    /// <summary>
    /// Detects the field order of an interlaced frame by analyzing motion between fields.
    /// </summary>
    public static FieldOrder DetectFieldOrder(ReadOnlySpan<byte> frame1, ReadOnlySpan<byte> frame2, 
        int width, int height, int bytesPerPixel = 4)
    {
        int stride = width * bytesPerPixel;
        
        long tffScore = 0; // Top field first score
        long bffScore = 0; // Bottom field first score
        
        for (int y = 2; y < height - 2; y += 2)
        {
            for (int x = bytesPerPixel; x < stride - bytesPerPixel; x += bytesPerPixel * 4)
            {
                int offset = y * stride + x;
                int prevOffset = (y - 1) * stride + x;
                int nextOffset = (y + 1) * stride + x;
                
                // Check temporal continuity for each field order assumption
                // TFF: even lines (0,2,4...) come first, odd lines from next temporal position
                // BFF: odd lines (1,3,5...) come first
                
                int frame1Even = frame1[offset + 1]; // Green channel
                int frame1Odd = frame1[nextOffset + 1];
                int frame2Even = frame2[offset + 1];
                int frame2Odd = frame2[nextOffset + 1];
                
                // For TFF, even lines should be more similar across frames (they're from same temporal instant)
                int evenDiff = Math.Abs(frame1Even - frame2Even);
                int oddDiff = Math.Abs(frame1Odd - frame2Odd);
                
                if (evenDiff < oddDiff)
                    tffScore += oddDiff - evenDiff;
                else
                    bffScore += evenDiff - oddDiff;
            }
        }
        
        return tffScore >= bffScore ? FieldOrder.TopFieldFirst : FieldOrder.BottomFieldFirst;
    }
}

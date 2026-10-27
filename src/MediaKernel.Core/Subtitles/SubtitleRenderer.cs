// SubtitleRenderer - Core subtitle timing and blending logic
// Handles both text and bitmap subtitle rendering with SIMD acceleration

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Core subtitle rendering engine with timing synchronization and alpha blending.
/// </summary>
public sealed class SubtitleRenderer : IDisposable
{
    private readonly SubtitleTrack track;
    private readonly List<SubtitleCue> activeCues = new();
    private long lastUpdateTimeMicroseconds = long.MinValue;
    private int currentCueIndex;
    private bool isDisposed;
    
    /// <summary>Gets the subtitle track being rendered.</summary>
    public SubtitleTrack Track => track;
    
    /// <summary>Gets currently active subtitle cues.</summary>
    public IReadOnlyList<SubtitleCue> ActiveCues => activeCues;
    
    /// <summary>Gets the default text style for rendering.</summary>
    public SubtitleRenderStyle TextStyle { get; set; } = SubtitleRenderStyle.Default;
    
    /// <summary>
    /// Creates a new subtitle renderer for the specified track.
    /// </summary>
    public SubtitleRenderer(SubtitleTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        this.track = track;
    }
    
    /// <summary>
    /// Updates active cues based on the current playback time.
    /// Returns true if the active cue list changed.
    /// </summary>
    /// <param name="currentTimeMicroseconds">Current playback time in microseconds.</param>
    public bool Update(long currentTimeMicroseconds)
    {
        if (currentTimeMicroseconds == lastUpdateTimeMicroseconds)
            return false;
        
        bool changed = false;
        
        // Handle seek backward - reset state
        if (currentTimeMicroseconds < lastUpdateTimeMicroseconds)
        {
            activeCues.Clear();
            currentCueIndex = 0;
            changed = true;
        }
        
        lastUpdateTimeMicroseconds = currentTimeMicroseconds;
        
        // Remove expired cues
        for (int i = activeCues.Count - 1; i >= 0; i--)
        {
            if (activeCues[i].EndTimeMicroseconds <= currentTimeMicroseconds)
            {
                activeCues.RemoveAt(i);
                changed = true;
            }
        }
        
        // Add newly active cues
        var cues = track.Cues;
        while (currentCueIndex < cues.Count)
        {
            var cue = cues[currentCueIndex];
            
            if (cue.StartTimeMicroseconds > currentTimeMicroseconds)
            {
                // Haven't reached this cue yet
                break;
            }
            
            if (cue.EndTimeMicroseconds > currentTimeMicroseconds)
            {
                // Cue is currently active
                activeCues.Add(cue);
                changed = true;
            }
            
            currentCueIndex++;
        }
        
        return changed;
    }
    
    /// <summary>
    /// Gets the time until the next subtitle event (start or end).
    /// Returns long.MaxValue if no more events.
    /// </summary>
    public long GetTimeToNextEvent(long currentTimeMicroseconds)
    {
        long nextTime = long.MaxValue;
        
        // Check when current cues expire
        foreach (var cue in activeCues)
        {
            if (cue.EndTimeMicroseconds > currentTimeMicroseconds)
            {
                nextTime = Math.Min(nextTime, cue.EndTimeMicroseconds);
            }
        }
        
        // Check when next cue starts
        if (currentCueIndex < track.Cues.Count)
        {
            nextTime = Math.Min(nextTime, track.Cues[currentCueIndex].StartTimeMicroseconds);
        }
        
        return nextTime == long.MaxValue ? nextTime : nextTime - currentTimeMicroseconds;
    }
    
    /// <summary>
    /// Seeks to a new time position, resetting cue tracking state.
    /// </summary>
    public void Seek(long timeMicroseconds)
    {
        activeCues.Clear();
        lastUpdateTimeMicroseconds = long.MinValue;
        
        // Binary search for starting cue index
        var cues = track.Cues;
        int low = 0;
        int high = cues.Count - 1;
        currentCueIndex = cues.Count;
        
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (cues[mid].StartTimeMicroseconds <= timeMicroseconds)
            {
                currentCueIndex = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        
        // Back up to find any cues that are still active
        while (currentCueIndex > 0)
        {
            var prevCue = cues[currentCueIndex - 1];
            if (prevCue.EndTimeMicroseconds > timeMicroseconds)
            {
                currentCueIndex--;
            }
            else
            {
                break;
            }
        }
        
        // Update with new time
        Update(timeMicroseconds);
    }
    
    /// <summary>
    /// Resets the renderer state to the beginning of the track.
    /// </summary>
    public void Reset()
    {
        activeCues.Clear();
        currentCueIndex = 0;
        lastUpdateTimeMicroseconds = long.MinValue;
    }
    
    /// <summary>
    /// Alpha-blends a bitmap subtitle onto a destination frame buffer.
    /// Uses SIMD acceleration when available.
    /// </summary>
    /// <param name="bitmap">Source bitmap (RGBA8888 format).</param>
    /// <param name="destination">Destination frame buffer (RGBA8888 format).</param>
    /// <param name="destinationWidth">Width of destination in pixels.</param>
    /// <param name="destinationHeight">Height of destination in pixels.</param>
    /// <param name="destinationStride">Stride of destination in bytes.</param>
    public static void BlendBitmap(
        SubtitleBitmap bitmap,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        
        int srcX = Math.Max(0, bitmap.PositionX);
        int srcY = Math.Max(0, bitmap.PositionY);
        int copyWidth = Math.Min(bitmap.Width, destinationWidth - srcX);
        int copyHeight = Math.Min(bitmap.Height, destinationHeight - srcY);
        
        if (copyWidth <= 0 || copyHeight <= 0)
            return;
        
        int srcOffsetX = bitmap.PositionX < 0 ? -bitmap.PositionX : 0;
        int srcOffsetY = bitmap.PositionY < 0 ? -bitmap.PositionY : 0;
        
        // Adjust copy dimensions for source offset
        copyWidth = Math.Min(copyWidth, bitmap.Width - srcOffsetX);
        copyHeight = Math.Min(copyHeight, bitmap.Height - srcOffsetY);
        
        if (Avx2.IsSupported && copyWidth >= 8)
        {
            BlendBitmapAvx2(bitmap, destination, destinationStride, 
                srcX, srcY, srcOffsetX, srcOffsetY, copyWidth, copyHeight);
        }
        else if (Sse2.IsSupported && copyWidth >= 4)
        {
            BlendBitmapSse2(bitmap, destination, destinationStride,
                srcX, srcY, srcOffsetX, srcOffsetY, copyWidth, copyHeight);
        }
        else
        {
            BlendBitmapScalar(bitmap, destination, destinationStride,
                srcX, srcY, srcOffsetX, srcOffsetY, copyWidth, copyHeight);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendBitmapScalar(
        SubtitleBitmap bitmap,
        Span<byte> destination,
        int destinationStride,
        int dstStartX,
        int dstStartY,
        int srcOffsetX,
        int srcOffsetY,
        int copyWidth,
        int copyHeight)
    {
        var srcData = bitmap.PixelData;
        int srcStride = bitmap.Stride;
        
        for (int y = 0; y < copyHeight; y++)
        {
            int srcRowStart = (srcOffsetY + y) * srcStride + srcOffsetX * 4;
            int dstRowStart = (dstStartY + y) * destinationStride + dstStartX * 4;
            
            for (int x = 0; x < copyWidth; x++)
            {
                int srcIdx = srcRowStart + x * 4;
                int dstIdx = dstRowStart + x * 4;
                
                byte srcR = srcData[srcIdx];
                byte srcG = srcData[srcIdx + 1];
                byte srcB = srcData[srcIdx + 2];
                byte srcA = srcData[srcIdx + 3];
                
                if (srcA == 0)
                    continue;
                    
                if (srcA == 255)
                {
                    destination[dstIdx] = srcR;
                    destination[dstIdx + 1] = srcG;
                    destination[dstIdx + 2] = srcB;
                    destination[dstIdx + 3] = 255;
                    continue;
                }
                
                // Alpha blend: out = src * srcA + dst * (1 - srcA)
                byte dstR = destination[dstIdx];
                byte dstG = destination[dstIdx + 1];
                byte dstB = destination[dstIdx + 2];
                byte dstA = destination[dstIdx + 3];
                
                int invAlpha = 255 - srcA;
                
                destination[dstIdx] = (byte)((srcR * srcA + dstR * invAlpha + 127) / 255);
                destination[dstIdx + 1] = (byte)((srcG * srcA + dstG * invAlpha + 127) / 255);
                destination[dstIdx + 2] = (byte)((srcB * srcA + dstB * invAlpha + 127) / 255);
                destination[dstIdx + 3] = (byte)Math.Min(255, srcA + dstA - srcA * dstA / 255);
            }
        }
    }
    
    private static void BlendBitmapSse2(
        SubtitleBitmap bitmap,
        Span<byte> destination,
        int destinationStride,
        int dstStartX,
        int dstStartY,
        int srcOffsetX,
        int srcOffsetY,
        int copyWidth,
        int copyHeight)
    {
        var srcData = bitmap.PixelData;
        int srcStride = bitmap.Stride;
        
        // Process 4 pixels at a time with SSE2
        int vectorWidth = 4;
        int vectorCount = copyWidth / vectorWidth;
        int remainder = copyWidth % vectorWidth;
        
        var zero = Vector128<short>.Zero;
        var alpha255 = Vector128.Create((short)255);
        var round128 = Vector128.Create((short)128);
        
        for (int y = 0; y < copyHeight; y++)
        {
            int srcRowStart = (srcOffsetY + y) * srcStride + srcOffsetX * 4;
            int dstRowStart = (dstStartY + y) * destinationStride + dstStartX * 4;
            
            for (int v = 0; v < vectorCount; v++)
            {
                int srcIdx = srcRowStart + v * 16;
                int dstIdx = dstRowStart + v * 16;
                
                // Load 4 source pixels (16 bytes)
                var srcBytes = Vector128.Create(
                    srcData[srcIdx], srcData[srcIdx + 1], srcData[srcIdx + 2], srcData[srcIdx + 3],
                    srcData[srcIdx + 4], srcData[srcIdx + 5], srcData[srcIdx + 6], srcData[srcIdx + 7],
                    srcData[srcIdx + 8], srcData[srcIdx + 9], srcData[srcIdx + 10], srcData[srcIdx + 11],
                    srcData[srcIdx + 12], srcData[srcIdx + 13], srcData[srcIdx + 14], srcData[srcIdx + 15]);
                
                // Load 4 destination pixels
                var dstSpan = destination.Slice(dstIdx, 16);
                var dstBytes = Vector128.Create(
                    dstSpan[0], dstSpan[1], dstSpan[2], dstSpan[3],
                    dstSpan[4], dstSpan[5], dstSpan[6], dstSpan[7],
                    dstSpan[8], dstSpan[9], dstSpan[10], dstSpan[11],
                    dstSpan[12], dstSpan[13], dstSpan[14], dstSpan[15]);
                
                // Unpack to 16-bit for blending math
                var srcLow = Sse2.UnpackLow(srcBytes.AsByte(), zero.AsByte()).AsInt16();
                var srcHigh = Sse2.UnpackHigh(srcBytes.AsByte(), zero.AsByte()).AsInt16();
                var dstLow = Sse2.UnpackLow(dstBytes.AsByte(), zero.AsByte()).AsInt16();
                var dstHigh = Sse2.UnpackHigh(dstBytes.AsByte(), zero.AsByte()).AsInt16();
                
                // Extract alpha values and broadcast (every 4th short)
                // Pixel layout: RGBA RGBA RGBA RGBA
                var alphaLow = Sse2.ShuffleHigh(Sse2.ShuffleLow(srcLow, 0xFF), 0xFF); // A A A A for first 2 pixels
                var alphaHigh = Sse2.ShuffleHigh(Sse2.ShuffleLow(srcHigh, 0xFF), 0xFF); // A A A A for last 2 pixels
                
                // invAlpha = 255 - alpha
                var invAlphaLow = Sse2.Subtract(alpha255, alphaLow);
                var invAlphaHigh = Sse2.Subtract(alpha255, alphaHigh);
                
                // result = (src * alpha + dst * invAlpha + 128) / 255
                // Approximate /255 with >>8 after adding 128
                var blendLow = Sse2.Add(
                    Sse2.MultiplyLow(srcLow, alphaLow),
                    Sse2.MultiplyLow(dstLow, invAlphaLow));
                blendLow = Sse2.Add(blendLow, round128);
                blendLow = Sse2.ShiftRightLogical(blendLow, 8);
                
                var blendHigh = Sse2.Add(
                    Sse2.MultiplyLow(srcHigh, alphaHigh),
                    Sse2.MultiplyLow(dstHigh, invAlphaHigh));
                blendHigh = Sse2.Add(blendHigh, round128);
                blendHigh = Sse2.ShiftRightLogical(blendHigh, 8);
                
                // Pack back to bytes
                var result = Sse2.PackUnsignedSaturate(blendLow, blendHigh);
                
                // Store result
                Unsafe.WriteUnaligned(ref destination[dstIdx], result);
            }
            
            // Handle remaining pixels with scalar
            for (int x = vectorCount * vectorWidth; x < copyWidth; x++)
            {
                int srcIdx = srcRowStart + x * 4;
                int dstIdx = dstRowStart + x * 4;
                
                byte srcA = srcData[srcIdx + 3];
                if (srcA == 0) continue;
                
                byte srcR = srcData[srcIdx];
                byte srcG = srcData[srcIdx + 1];
                byte srcB = srcData[srcIdx + 2];
                
                if (srcA == 255)
                {
                    destination[dstIdx] = srcR;
                    destination[dstIdx + 1] = srcG;
                    destination[dstIdx + 2] = srcB;
                    destination[dstIdx + 3] = 255;
                    continue;
                }
                
                int invAlpha = 255 - srcA;
                destination[dstIdx] = (byte)((srcR * srcA + destination[dstIdx] * invAlpha + 127) / 255);
                destination[dstIdx + 1] = (byte)((srcG * srcA + destination[dstIdx + 1] * invAlpha + 127) / 255);
                destination[dstIdx + 2] = (byte)((srcB * srcA + destination[dstIdx + 2] * invAlpha + 127) / 255);
                destination[dstIdx + 3] = (byte)Math.Min(255, srcA + destination[dstIdx + 3] - srcA * destination[dstIdx + 3] / 255);
            }
        }
    }
    
    private static void BlendBitmapAvx2(
        SubtitleBitmap bitmap,
        Span<byte> destination,
        int destinationStride,
        int dstStartX,
        int dstStartY,
        int srcOffsetX,
        int srcOffsetY,
        int copyWidth,
        int copyHeight)
    {
        var srcData = bitmap.PixelData;
        int srcStride = bitmap.Stride;
        
        // Process 8 pixels at a time with AVX2
        int vectorWidth = 8;
        int vectorCount = copyWidth / vectorWidth;
        int remainder = copyWidth % vectorWidth;
        
        var zero = Vector256<short>.Zero;
        var alpha255 = Vector256.Create((short)255);
        var round128 = Vector256.Create((short)128);
        
        for (int y = 0; y < copyHeight; y++)
        {
            int srcRowStart = (srcOffsetY + y) * srcStride + srcOffsetX * 4;
            int dstRowStart = (dstStartY + y) * destinationStride + dstStartX * 4;
            
            for (int v = 0; v < vectorCount; v++)
            {
                int srcIdx = srcRowStart + v * 32;
                int dstIdx = dstRowStart + v * 32;
                
                // Load 8 source pixels (32 bytes)
                ref byte srcRef = ref srcData[srcIdx];
                var srcBytes = Unsafe.ReadUnaligned<Vector256<byte>>(ref srcRef);
                
                // Load 8 destination pixels
                var dstBytes = Unsafe.ReadUnaligned<Vector256<byte>>(ref destination[dstIdx]);
                
                // Unpack to 16-bit for blending math (low and high halves)
                var srcLow = Avx2.UnpackLow(srcBytes, zero.AsByte()).AsInt16();
                var srcHigh = Avx2.UnpackHigh(srcBytes, zero.AsByte()).AsInt16();
                var dstLow = Avx2.UnpackLow(dstBytes, zero.AsByte()).AsInt16();
                var dstHigh = Avx2.UnpackHigh(dstBytes, zero.AsByte()).AsInt16();
                
                // Shuffle to broadcast alpha to all channels of each pixel
                // Pattern: each pixel is RGBA, we want AAAA for each
                var alphaShuffle = Vector256.Create(
                    (byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15,
                    3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
                var srcAlphaBytes = Avx2.Shuffle(srcBytes, alphaShuffle);
                
                var srcAlphaLow = Avx2.UnpackLow(srcAlphaBytes, zero.AsByte()).AsInt16();
                var srcAlphaHigh = Avx2.UnpackHigh(srcAlphaBytes, zero.AsByte()).AsInt16();
                
                // invAlpha = 255 - alpha
                var invAlphaLow = Avx2.Subtract(alpha255, srcAlphaLow);
                var invAlphaHigh = Avx2.Subtract(alpha255, srcAlphaHigh);
                
                // result = (src * alpha + dst * invAlpha + 128) >> 8
                var blendLow = Avx2.Add(
                    Avx2.MultiplyLow(srcLow, srcAlphaLow),
                    Avx2.MultiplyLow(dstLow, invAlphaLow));
                blendLow = Avx2.Add(blendLow, round128);
                blendLow = Avx2.ShiftRightLogical(blendLow, 8);
                
                var blendHigh = Avx2.Add(
                    Avx2.MultiplyLow(srcHigh, srcAlphaHigh),
                    Avx2.MultiplyLow(dstHigh, invAlphaHigh));
                blendHigh = Avx2.Add(blendHigh, round128);
                blendHigh = Avx2.ShiftRightLogical(blendHigh, 8);
                
                // Pack back to bytes
                var result = Avx2.PackUnsignedSaturate(blendLow, blendHigh);
                
                // Store result
                Unsafe.WriteUnaligned(ref destination[dstIdx], result);
            }
            
            // Handle remaining pixels with SSE2 or scalar
            int remainStart = vectorCount * vectorWidth;
            if (Sse2.IsSupported && remainder >= 4)
            {
                // Process 4 more pixels with SSE2
                int srcIdx = srcRowStart + remainStart * 4;
                int dstIdx = dstRowStart + remainStart * 4;
                
                // Simplified SSE2 blend for remainder
                for (int x = remainStart; x < copyWidth; x++)
                {
                    srcIdx = srcRowStart + x * 4;
                    dstIdx = dstRowStart + x * 4;
                    
                    byte srcA = srcData[srcIdx + 3];
                    if (srcA == 0) continue;
                    
                    byte srcR = srcData[srcIdx];
                    byte srcG = srcData[srcIdx + 1];
                    byte srcB = srcData[srcIdx + 2];
                    
                    if (srcA == 255)
                    {
                        destination[dstIdx] = srcR;
                        destination[dstIdx + 1] = srcG;
                        destination[dstIdx + 2] = srcB;
                        destination[dstIdx + 3] = 255;
                        continue;
                    }
                    
                    int invAlpha = 255 - srcA;
                    destination[dstIdx] = (byte)((srcR * srcA + destination[dstIdx] * invAlpha + 127) / 255);
                    destination[dstIdx + 1] = (byte)((srcG * srcA + destination[dstIdx + 1] * invAlpha + 127) / 255);
                    destination[dstIdx + 2] = (byte)((srcB * srcA + destination[dstIdx + 2] * invAlpha + 127) / 255);
                    destination[dstIdx + 3] = (byte)Math.Min(255, srcA + destination[dstIdx + 3] - srcA * destination[dstIdx + 3] / 255);
                }
            }
            else
            {
                // Scalar remainder
                for (int x = remainStart; x < copyWidth; x++)
                {
                    int srcIdx = srcRowStart + x * 4;
                    int dstIdx = dstRowStart + x * 4;
                    
                    byte srcA = srcData[srcIdx + 3];
                    if (srcA == 0) continue;
                    
                    byte srcR = srcData[srcIdx];
                    byte srcG = srcData[srcIdx + 1];
                    byte srcB = srcData[srcIdx + 2];
                    
                    if (srcA == 255)
                    {
                        destination[dstIdx] = srcR;
                        destination[dstIdx + 1] = srcG;
                        destination[dstIdx + 2] = srcB;
                        destination[dstIdx + 3] = 255;
                        continue;
                    }
                    
                    int invAlpha = 255 - srcA;
                    destination[dstIdx] = (byte)((srcR * srcA + destination[dstIdx] * invAlpha + 127) / 255);
                    destination[dstIdx + 1] = (byte)((srcG * srcA + destination[dstIdx + 1] * invAlpha + 127) / 255);
                    destination[dstIdx + 2] = (byte)((srcB * srcA + destination[dstIdx + 2] * invAlpha + 127) / 255);
                    destination[dstIdx + 3] = (byte)Math.Min(255, srcA + destination[dstIdx + 3] - srcA * destination[dstIdx + 3] / 255);
                }
            }
        }
    }
    
    public void Dispose()
    {
        if (!isDisposed)
        {
            activeCues.Clear();
            isDisposed = true;
        }
    }
}

/// <summary>
/// Rendering style settings for text subtitles.
/// </summary>
public sealed class SubtitleRenderStyle
{
    /// <summary>Font family name.</summary>
    public string FontFamily { get; init; } = "Arial";
    
    /// <summary>Font size in pixels (at 1080p, scaled for other resolutions).</summary>
    public float FontSize { get; init; } = 48f;
    
    /// <summary>Primary text color (ARGB).</summary>
    public uint TextColor { get; init; } = 0xFFFFFFFF;
    
    /// <summary>Outline color (ARGB).</summary>
    public uint OutlineColor { get; init; } = 0xFF000000;
    
    /// <summary>Outline width in pixels.</summary>
    public float OutlineWidth { get; init; } = 2f;
    
    /// <summary>Shadow color (ARGB).</summary>
    public uint ShadowColor { get; init; } = 0x80000000;
    
    /// <summary>Shadow offset in pixels.</summary>
    public float ShadowOffset { get; init; } = 2f;
    
    /// <summary>Bold text.</summary>
    public bool Bold { get; init; }
    
    /// <summary>Italic text.</summary>
    public bool Italic { get; init; }
    
    /// <summary>Bottom margin (percentage of screen height).</summary>
    public float BottomMarginPercent { get; init; } = 5f;
    
    /// <summary>Side margin (percentage of screen width).</summary>
    public float SideMarginPercent { get; init; } = 5f;
    
    /// <summary>Default render style.</summary>
    public static SubtitleRenderStyle Default { get; } = new();
    
    /// <summary>Creates a style from an ASS/SSA style definition.</summary>
    public static SubtitleRenderStyle FromAssStyle(SubtitleStyle assStyle)
    {
        return new SubtitleRenderStyle
        {
            FontFamily = assStyle.FontName,
            FontSize = assStyle.FontSize,
            TextColor = assStyle.PrimaryColor,
            OutlineColor = assStyle.OutlineColor,
            OutlineWidth = assStyle.OutlineWidth,
            ShadowColor = assStyle.BackColor,
            ShadowOffset = assStyle.ShadowDepth,
            Bold = assStyle.Bold,
            Italic = assStyle.Italic
        };
    }
}

/// <summary>
/// Priority queue for subtitle timing synchronization.
/// Efficiently tracks upcoming subtitle events.
/// </summary>
public sealed class SubtitleEventQueue
{
    private readonly SortedSet<SubtitleEvent> events = new(SubtitleEventComparer.Instance);
    
    /// <summary>Number of pending events.</summary>
    public int Count => events.Count;
    
    /// <summary>
    /// Adds events for a subtitle cue (start and end).
    /// </summary>
    public void EnqueueCue(SubtitleCue cue, int cueIndex)
    {
        events.Add(new SubtitleEvent(cue.StartTimeMicroseconds, SubtitleEventType.CueStart, cueIndex));
        events.Add(new SubtitleEvent(cue.EndTimeMicroseconds, SubtitleEventType.CueEnd, cueIndex));
    }
    
    /// <summary>
    /// Gets the next event without removing it.
    /// </summary>
    public SubtitleEvent? PeekNext()
    {
        return events.Count > 0 ? events.Min : null;
    }
    
    /// <summary>
    /// Gets and removes the next event.
    /// </summary>
    public SubtitleEvent? DequeueNext()
    {
        if (events.Count == 0)
            return null;
        
        var next = events.Min;
        events.Remove(next);
        return next;
    }
    
    /// <summary>
    /// Removes all events before the specified time.
    /// </summary>
    public void RemoveEventsBefore(long timeMicroseconds)
    {
        events.RemoveWhere(e => e.TimeMicroseconds < timeMicroseconds);
    }
    
    /// <summary>
    /// Clears all events.
    /// </summary>
    public void Clear() => events.Clear();
    
    private sealed class SubtitleEventComparer : IComparer<SubtitleEvent>
    {
        public static readonly SubtitleEventComparer Instance = new();
        
        public int Compare(SubtitleEvent x, SubtitleEvent y)
        {
            int timeCompare = x.TimeMicroseconds.CompareTo(y.TimeMicroseconds);
            if (timeCompare != 0)
                return timeCompare;
            
            // Same time: ends before starts
            int typeCompare = x.Type.CompareTo(y.Type);
            if (typeCompare != 0)
                return typeCompare;
            
            return x.CueIndex.CompareTo(y.CueIndex);
        }
    }
}

/// <summary>
/// A subtitle timing event (cue start or end).
/// </summary>
public readonly record struct SubtitleEvent(
    long TimeMicroseconds,
    SubtitleEventType Type,
    int CueIndex);

/// <summary>
/// Type of subtitle event.
/// </summary>
public enum SubtitleEventType
{
    /// <summary>Cue end (processed before starts at same time).</summary>
    CueEnd = 0,
    /// <summary>Cue start.</summary>
    CueStart = 1
}

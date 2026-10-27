// MediaKernel - Video Overlay System
// Text rendering, logo/image overlay, and OSD (On-Screen Display) support

using System;
using System.Buffers;
using System.Collections.Generic;

namespace MediaKernel.Core.Video;

/// <summary>
/// Position anchor for overlays on screen.
/// </summary>
public enum OverlayPosition
{
    /// <summary>Custom X,Y coordinates from top-left.</summary>
    Absolute,
    /// <summary>Top-left corner with margin.</summary>
    TopLeft,
    /// <summary>Top-center with margin.</summary>
    TopCenter,
    /// <summary>Top-right corner with margin.</summary>
    TopRight,
    /// <summary>Middle-left with margin.</summary>
    MiddleLeft,
    /// <summary>Center of screen.</summary>
    Center,
    /// <summary>Middle-right with margin.</summary>
    MiddleRight,
    /// <summary>Bottom-left corner with margin.</summary>
    BottomLeft,
    /// <summary>Bottom-center with margin.</summary>
    BottomCenter,
    /// <summary>Bottom-right corner with margin.</summary>
    BottomRight
}

/// <summary>
/// Text alignment within a text overlay region.
/// </summary>
public enum TextAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// RGBA color for overlays.
/// </summary>
public readonly struct OverlayColor
{
    public byte Red { get; init; }
    public byte Green { get; init; }
    public byte Blue { get; init; }
    public byte Alpha { get; init; }
    
    public OverlayColor(byte red, byte green, byte blue, byte alpha = 255)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }
    
    public static OverlayColor White => new(255, 255, 255, 255);
    public static OverlayColor Black => new(0, 0, 0, 255);
    public static OverlayColor PureRed => new(255, 0, 0, 255);
    public static OverlayColor PureGreen => new(0, 255, 0, 255);
    public static OverlayColor PureBlue => new(0, 0, 255, 255);
    public static OverlayColor Yellow => new(255, 255, 0, 255);
    public static OverlayColor Cyan => new(0, 255, 255, 255);
    public static OverlayColor Magenta => new(255, 0, 255, 255);
    public static OverlayColor Transparent => new(0, 0, 0, 0);
    
    public static OverlayColor FromArgb(uint argb)
    {
        return new OverlayColor(
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF),
            (byte)((argb >> 24) & 0xFF));
    }
    
    public uint ToArgb() => ((uint)Alpha << 24) | ((uint)Red << 16) | ((uint)Green << 8) | Blue;
}

/// <summary>
/// Base class for all overlay items.
/// </summary>
public abstract class OverlayItem
{
    /// <summary>Unique identifier for this overlay.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>Position anchor on screen.</summary>
    public OverlayPosition Position { get; set; } = OverlayPosition.TopLeft;
    
    /// <summary>X offset from anchor (or absolute X if Position is Absolute).</summary>
    public int OffsetX { get; set; }
    
    /// <summary>Y offset from anchor (or absolute Y if Position is Absolute).</summary>
    public int OffsetY { get; set; }
    
    /// <summary>Overall opacity (0-255).</summary>
    public byte Opacity { get; set; } = 255;
    
    /// <summary>Z-order for layering (higher = on top).</summary>
    public int ZOrder { get; set; }
    
    /// <summary>Whether overlay is currently visible.</summary>
    public bool IsVisible { get; set; } = true;
    
    /// <summary>When overlay should start showing (frame number or timestamp).</summary>
    public long StartTime { get; set; }
    
    /// <summary>Duration to show overlay (0 = indefinite).</summary>
    public long Duration { get; set; }
    
    /// <summary>Calculate actual screen position based on anchor and video dimensions.</summary>
    public (int X, int Y) CalculatePosition(int videoWidth, int videoHeight, int overlayWidth, int overlayHeight)
    {
        int x = OffsetX;
        int y = OffsetY;
        
        switch (Position)
        {
            case OverlayPosition.Absolute:
                break;
            case OverlayPosition.TopLeft:
                break;
            case OverlayPosition.TopCenter:
                x = (videoWidth - overlayWidth) / 2 + OffsetX;
                break;
            case OverlayPosition.TopRight:
                x = videoWidth - overlayWidth - OffsetX;
                break;
            case OverlayPosition.MiddleLeft:
                y = (videoHeight - overlayHeight) / 2 + OffsetY;
                break;
            case OverlayPosition.Center:
                x = (videoWidth - overlayWidth) / 2 + OffsetX;
                y = (videoHeight - overlayHeight) / 2 + OffsetY;
                break;
            case OverlayPosition.MiddleRight:
                x = videoWidth - overlayWidth - OffsetX;
                y = (videoHeight - overlayHeight) / 2 + OffsetY;
                break;
            case OverlayPosition.BottomLeft:
                y = videoHeight - overlayHeight - OffsetY;
                break;
            case OverlayPosition.BottomCenter:
                x = (videoWidth - overlayWidth) / 2 + OffsetX;
                y = videoHeight - overlayHeight - OffsetY;
                break;
            case OverlayPosition.BottomRight:
                x = videoWidth - overlayWidth - OffsetX;
                y = videoHeight - overlayHeight - OffsetY;
                break;
        }
        
        return (x, y);
    }
}

/// <summary>
/// Text overlay item with font and style settings.
/// </summary>
public sealed class TextOverlay : OverlayItem
{
    /// <summary>Text to display (supports format strings like %H:%M:%S for time).</summary>
    public string Text { get; set; } = string.Empty;
    
    /// <summary>Font family name.</summary>
    public string FontFamily { get; set; } = "Arial";
    
    /// <summary>Font size in pixels.</summary>
    public int FontSize { get; set; } = 24;
    
    /// <summary>Whether text is bold.</summary>
    public bool IsBold { get; set; }
    
    /// <summary>Whether text is italic.</summary>
    public bool IsItalic { get; set; }
    
    /// <summary>Text color.</summary>
    public OverlayColor TextColor { get; set; } = OverlayColor.White;
    
    /// <summary>Background color (transparent for no background).</summary>
    public OverlayColor BackgroundColor { get; set; } = OverlayColor.Transparent;
    
    /// <summary>Outline/shadow color.</summary>
    public OverlayColor OutlineColor { get; set; } = OverlayColor.Black;
    
    /// <summary>Outline thickness in pixels (0 = no outline).</summary>
    public int OutlineThickness { get; set; } = 1;
    
    /// <summary>Shadow offset X (0 = no shadow).</summary>
    public int ShadowOffsetX { get; set; }
    
    /// <summary>Shadow offset Y (0 = no shadow).</summary>
    public int ShadowOffsetY { get; set; }
    
    /// <summary>Text alignment within bounding box.</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    
    /// <summary>Maximum width before wrapping (0 = no wrap).</summary>
    public int MaxWidth { get; set; }
    
    /// <summary>Whether to scroll text horizontally (marquee effect).</summary>
    public bool EnableMarquee { get; set; }
    
    /// <summary>Marquee scroll speed in pixels per second.</summary>
    public int MarqueeSpeed { get; set; } = 50;
    
    /// <summary>Current marquee offset (updated during rendering).</summary>
    internal int MarqueeOffset { get; set; }
    
    /// <summary>
    /// Format the text with dynamic values.
    /// </summary>
    public string GetFormattedText(DateTime? currentTime = null)
    {
        if (string.IsNullOrEmpty(Text))
            return string.Empty;
        
        var time = currentTime ?? DateTime.Now;
        string result = Text;
        
        // Replace common format strings (like VLC's marquee)
        result = result.Replace("%Y", time.Year.ToString("D4"));
        result = result.Replace("%m", time.Month.ToString("D2"));
        result = result.Replace("%d", time.Day.ToString("D2"));
        result = result.Replace("%H", time.Hour.ToString("D2"));
        result = result.Replace("%M", time.Minute.ToString("D2"));
        result = result.Replace("%S", time.Second.ToString("D2"));
        result = result.Replace("%%", "%");
        
        return result;
    }
}

/// <summary>
/// Image/logo overlay item.
/// </summary>
public sealed class ImageOverlay : OverlayItem
{
    /// <summary>Image data in RGBA format.</summary>
    public byte[]? ImageData { get; set; }
    
    /// <summary>Image width.</summary>
    public int Width { get; set; }
    
    /// <summary>Image height.</summary>
    public int Height { get; set; }
    
    /// <summary>Optional scaling factor (1.0 = original size).</summary>
    public float Scale { get; set; } = 1.0f;
    
    /// <summary>Animation frames for animated overlays.</summary>
    public List<ImageFrame>? AnimationFrames { get; set; }
    
    /// <summary>Current animation frame index.</summary>
    internal int CurrentFrame { get; set; }
    
    /// <summary>Time when current frame started.</summary>
    internal long FrameStartTime { get; set; }
    
    /// <summary>Number of animation loops (-1 = infinite).</summary>
    public int AnimationLoops { get; set; } = -1;
    
    /// <summary>Current loop count.</summary>
    internal int CurrentLoop { get; set; }
    
    /// <summary>Scaled width.</summary>
    public int ScaledWidth => (int)(Width * Scale);
    
    /// <summary>Scaled height.</summary>
    public int ScaledHeight => (int)(Height * Scale);
}

/// <summary>
/// Single frame in an animated image overlay.
/// </summary>
public sealed class ImageFrame
{
    /// <summary>Frame image data in RGBA format.</summary>
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    
    /// <summary>Frame width.</summary>
    public int Width { get; set; }
    
    /// <summary>Frame height.</summary>
    public int Height { get; set; }
    
    /// <summary>Duration to show this frame in milliseconds.</summary>
    public int DurationMilliseconds { get; set; } = 100;
    
    /// <summary>Per-frame opacity override (null = use parent opacity).</summary>
    public byte? Opacity { get; set; }
}

/// <summary>
/// Progress bar overlay (for OSD).
/// </summary>
public sealed class ProgressBarOverlay : OverlayItem
{
    /// <summary>Current progress value (0.0 to 1.0).</summary>
    public float Progress { get; set; }
    
    /// <summary>Bar width in pixels.</summary>
    public int BarWidth { get; set; } = 300;
    
    /// <summary>Bar height in pixels.</summary>
    public int BarHeight { get; set; } = 8;
    
    /// <summary>Background color of the bar.</summary>
    public OverlayColor BackgroundColor { get; set; } = new(64, 64, 64, 200);
    
    /// <summary>Fill color for progress.</summary>
    public OverlayColor FillColor { get; set; } = new(0, 120, 215, 255);
    
    /// <summary>Border color (transparent for no border).</summary>
    public OverlayColor BorderColor { get; set; } = new(128, 128, 128, 200);
    
    /// <summary>Border thickness.</summary>
    public int BorderThickness { get; set; } = 1;
    
    /// <summary>Corner radius for rounded bars.</summary>
    public int CornerRadius { get; set; } = 4;
}

/// <summary>
/// Volume indicator overlay (OSD).
/// </summary>
public sealed class VolumeIndicatorOverlay : OverlayItem
{
    /// <summary>Current volume (0.0 to 1.0, can exceed 1.0 for boost).</summary>
    public float Volume { get; set; } = 1.0f;
    
    /// <summary>Whether audio is muted.</summary>
    public bool IsMuted { get; set; }
    
    /// <summary>Indicator width.</summary>
    public int Width { get; set; } = 200;
    
    /// <summary>Indicator height.</summary>
    public int Height { get; set; } = 40;
    
    /// <summary>Number of bars in the indicator.</summary>
    public int BarCount { get; set; } = 16;
    
    /// <summary>Low volume color.</summary>
    public OverlayColor LowVolumeColor { get; set; } = new(0, 200, 0, 255);
    
    /// <summary>Medium volume color.</summary>
    public OverlayColor MediumVolumeColor { get; set; } = new(200, 200, 0, 255);
    
    /// <summary>High volume color.</summary>
    public OverlayColor HighVolumeColor { get; set; } = new(200, 0, 0, 255);
    
    /// <summary>Background color.</summary>
    public OverlayColor BackgroundColor { get; set; } = new(32, 32, 32, 200);
    
    /// <summary>Muted indicator color.</summary>
    public OverlayColor MutedColor { get; set; } = new(128, 128, 128, 255);
}

/// <summary>
/// On-Screen Display manager for video playback.
/// </summary>
public sealed class OnScreenDisplay
{
    private readonly List<OverlayItem> overlays = new();
    private readonly object lockObject = new();
    
    /// <summary>Whether OSD is enabled.</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>Default margin from screen edges.</summary>
    public int DefaultMargin { get; set; } = 20;
    
    /// <summary>Default fade-out duration in milliseconds.</summary>
    public int FadeOutDuration { get; set; } = 500;
    
    /// <summary>
    /// Add an overlay item.
    /// </summary>
    public void AddOverlay(OverlayItem overlay)
    {
        lock (lockObject)
        {
            overlays.Add(overlay);
            overlays.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
        }
    }
    
    /// <summary>
    /// Remove an overlay by ID.
    /// </summary>
    public bool RemoveOverlay(string id)
    {
        lock (lockObject)
        {
            int index = overlays.FindIndex(o => o.Id == id);
            if (index >= 0)
            {
                overlays.RemoveAt(index);
                return true;
            }
            return false;
        }
    }
    
    /// <summary>
    /// Get an overlay by ID.
    /// </summary>
    public OverlayItem? GetOverlay(string id)
    {
        lock (lockObject)
        {
            return overlays.Find(o => o.Id == id);
        }
    }
    
    /// <summary>
    /// Clear all overlays.
    /// </summary>
    public void ClearOverlays()
    {
        lock (lockObject)
        {
            overlays.Clear();
        }
    }
    
    /// <summary>
    /// Get all visible overlays for rendering.
    /// </summary>
    public List<OverlayItem> GetVisibleOverlays(long currentTime)
    {
        if (!IsEnabled)
            return new List<OverlayItem>();
        
        lock (lockObject)
        {
            var visible = new List<OverlayItem>();
            
            foreach (var overlay in overlays)
            {
                if (!overlay.IsVisible)
                    continue;
                
                // Check time-based visibility
                if (overlay.StartTime > 0 && currentTime < overlay.StartTime)
                    continue;
                
                if (overlay.Duration > 0 && currentTime > overlay.StartTime + overlay.Duration)
                {
                    overlay.IsVisible = false;
                    continue;
                }
                
                visible.Add(overlay);
            }
            
            return visible;
        }
    }
    
    /// <summary>
    /// Create and show a temporary text message.
    /// </summary>
    public string ShowMessage(string text, int durationMilliseconds = 3000, OverlayPosition position = OverlayPosition.BottomCenter)
    {
        var overlay = new TextOverlay
        {
            Text = text,
            Position = position,
            OffsetY = DefaultMargin,
            Duration = durationMilliseconds,
            FontSize = 28,
            OutlineThickness = 2,
            ZOrder = 100
        };
        
        AddOverlay(overlay);
        return overlay.Id;
    }
    
    /// <summary>
    /// Create and show a progress bar (e.g., for seeking).
    /// </summary>
    public string ShowProgress(float progress, int durationMilliseconds = 2000)
    {
        var overlay = new ProgressBarOverlay
        {
            Progress = Math.Clamp(progress, 0f, 1f),
            Position = OverlayPosition.BottomCenter,
            OffsetY = DefaultMargin + 40,
            Duration = durationMilliseconds,
            ZOrder = 99
        };
        
        AddOverlay(overlay);
        return overlay.Id;
    }
    
    /// <summary>
    /// Create and show a volume indicator.
    /// </summary>
    public string ShowVolume(float volume, bool isMuted = false, int durationMilliseconds = 2000)
    {
        var overlay = new VolumeIndicatorOverlay
        {
            Volume = volume,
            IsMuted = isMuted,
            Position = OverlayPosition.TopRight,
            OffsetX = DefaultMargin,
            OffsetY = DefaultMargin,
            Duration = durationMilliseconds,
            ZOrder = 98
        };
        
        AddOverlay(overlay);
        return overlay.Id;
    }
    
    /// <summary>
    /// Update progress of an existing progress bar.
    /// </summary>
    public void UpdateProgress(string id, float progress)
    {
        if (GetOverlay(id) is ProgressBarOverlay progressBar)
        {
            progressBar.Progress = Math.Clamp(progress, 0f, 1f);
        }
    }
    
    /// <summary>
    /// Update volume of an existing volume indicator.
    /// </summary>
    public void UpdateVolume(string id, float volume, bool isMuted = false)
    {
        if (GetOverlay(id) is VolumeIndicatorOverlay volumeIndicator)
        {
            volumeIndicator.Volume = volume;
            volumeIndicator.IsMuted = isMuted;
        }
    }
}

/// <summary>
/// Renders overlays onto video frames.
/// </summary>
public sealed class OverlayRenderer
{
    private readonly OnScreenDisplay osd;
    
    public OverlayRenderer(OnScreenDisplay onScreenDisplay)
    {
        osd = onScreenDisplay;
    }
    
    /// <summary>
    /// Render all visible overlays onto an RGBA frame.
    /// </summary>
    public void RenderOverlays(Span<byte> frameRgba, int frameWidth, int frameHeight, long currentTime)
    {
        var overlays = osd.GetVisibleOverlays(currentTime);
        
        foreach (var overlay in overlays)
        {
            switch (overlay)
            {
                case ImageOverlay imageOverlay:
                    RenderImageOverlay(frameRgba, frameWidth, frameHeight, imageOverlay, currentTime);
                    break;
                case ProgressBarOverlay progressBar:
                    RenderProgressBar(frameRgba, frameWidth, frameHeight, progressBar);
                    break;
                case VolumeIndicatorOverlay volumeIndicator:
                    RenderVolumeIndicator(frameRgba, frameWidth, frameHeight, volumeIndicator);
                    break;
                // TextOverlay requires platform-specific text rendering (fonts)
                // Would integrate with platform text services
            }
        }
    }
    
    private void RenderImageOverlay(Span<byte> frame, int frameWidth, int frameHeight, ImageOverlay overlay, long currentTime)
    {
        byte[]? imageData = overlay.ImageData;
        int imgWidth = overlay.Width;
        int imgHeight = overlay.Height;
        
        // Handle animation
        if (overlay.AnimationFrames is { Count: > 0 })
        {
            var frames = overlay.AnimationFrames;
            
            // Check if we need to advance frame
            if (currentTime > overlay.FrameStartTime + frames[overlay.CurrentFrame].DurationMilliseconds)
            {
                overlay.CurrentFrame++;
                if (overlay.CurrentFrame >= frames.Count)
                {
                    if (overlay.AnimationLoops != 0)
                    {
                        overlay.CurrentFrame = 0;
                        if (overlay.AnimationLoops > 0)
                        {
                            overlay.CurrentLoop++;
                            if (overlay.CurrentLoop >= overlay.AnimationLoops)
                            {
                                overlay.IsVisible = false;
                                return;
                            }
                        }
                    }
                    else
                    {
                        overlay.CurrentFrame = frames.Count - 1;
                    }
                }
                overlay.FrameStartTime = currentTime;
            }
            
            var currentFrameData = frames[overlay.CurrentFrame];
            imageData = currentFrameData.ImageData;
            imgWidth = currentFrameData.Width;
            imgHeight = currentFrameData.Height;
        }
        
        if (imageData == null || imageData.Length == 0)
            return;
        
        int scaledWidth = (int)(imgWidth * overlay.Scale);
        int scaledHeight = (int)(imgHeight * overlay.Scale);
        
        var (posX, posY) = overlay.CalculatePosition(frameWidth, frameHeight, scaledWidth, scaledHeight);
        
        // Simple alpha blending without scaling (scaling would require bilinear interpolation)
        byte opacity = overlay.Opacity;
        
        for (int y = 0; y < imgHeight && posY + y < frameHeight; y++)
        {
            if (posY + y < 0) continue;
            
            for (int x = 0; x < imgWidth && posX + x < frameWidth; x++)
            {
                if (posX + x < 0) continue;
                
                int srcIdx = (y * imgWidth + x) * 4;
                int dstIdx = ((posY + y) * frameWidth + (posX + x)) * 4;
                
                byte srcR = imageData[srcIdx];
                byte srcG = imageData[srcIdx + 1];
                byte srcB = imageData[srcIdx + 2];
                byte srcA = (byte)((imageData[srcIdx + 3] * opacity) / 255);
                
                if (srcA == 0) continue;
                
                if (srcA == 255)
                {
                    frame[dstIdx] = srcR;
                    frame[dstIdx + 1] = srcG;
                    frame[dstIdx + 2] = srcB;
                    frame[dstIdx + 3] = 255;
                }
                else
                {
                    // Alpha blending: dst = src * srcA + dst * (1 - srcA)
                    int invAlpha = 255 - srcA;
                    frame[dstIdx] = (byte)((srcR * srcA + frame[dstIdx] * invAlpha) / 255);
                    frame[dstIdx + 1] = (byte)((srcG * srcA + frame[dstIdx + 1] * invAlpha) / 255);
                    frame[dstIdx + 2] = (byte)((srcB * srcA + frame[dstIdx + 2] * invAlpha) / 255);
                    frame[dstIdx + 3] = (byte)Math.Min(255, frame[dstIdx + 3] + srcA);
                }
            }
        }
    }
    
    private void RenderProgressBar(Span<byte> frame, int frameWidth, int frameHeight, ProgressBarOverlay bar)
    {
        var (posX, posY) = bar.CalculatePosition(frameWidth, frameHeight, bar.BarWidth, bar.BarHeight);
        
        int fillWidth = (int)(bar.BarWidth * bar.Progress);
        byte opacity = bar.Opacity;
        
        for (int y = 0; y < bar.BarHeight; y++)
        {
            int screenY = posY + y;
            if (screenY < 0 || screenY >= frameHeight) continue;
            
            for (int x = 0; x < bar.BarWidth; x++)
            {
                int screenX = posX + x;
                if (screenX < 0 || screenX >= frameWidth) continue;
                
                int idx = (screenY * frameWidth + screenX) * 4;
                
                // Determine if this pixel is border, fill, or background
                bool isBorder = bar.BorderThickness > 0 && 
                               (y < bar.BorderThickness || y >= bar.BarHeight - bar.BorderThickness ||
                                x < bar.BorderThickness || x >= bar.BarWidth - bar.BorderThickness);
                
                OverlayColor color;
                if (isBorder)
                    color = bar.BorderColor;
                else if (x < fillWidth)
                    color = bar.FillColor;
                else
                    color = bar.BackgroundColor;
                
                byte alpha = (byte)((color.Alpha * opacity) / 255);
                if (alpha == 0) continue;
                
                int invAlpha = 255 - alpha;
                frame[idx] = (byte)((color.Red * alpha + frame[idx] * invAlpha) / 255);
                frame[idx + 1] = (byte)((color.Green * alpha + frame[idx + 1] * invAlpha) / 255);
                frame[idx + 2] = (byte)((color.Blue * alpha + frame[idx + 2] * invAlpha) / 255);
                frame[idx + 3] = (byte)Math.Min(255, frame[idx + 3] + alpha);
            }
        }
    }
    
    private void RenderVolumeIndicator(Span<byte> frame, int frameWidth, int frameHeight, VolumeIndicatorOverlay vol)
    {
        var (posX, posY) = vol.CalculatePosition(frameWidth, frameHeight, vol.Width, vol.Height);
        
        byte opacity = vol.Opacity;
        int barCount = vol.BarCount;
        int barWidth = (vol.Width - (barCount + 1) * 2) / barCount; // 2px gap between bars
        int barMaxHeight = vol.Height - 8; // 4px padding top/bottom
        
        // Draw background
        for (int y = 0; y < vol.Height; y++)
        {
            int screenY = posY + y;
            if (screenY < 0 || screenY >= frameHeight) continue;
            
            for (int x = 0; x < vol.Width; x++)
            {
                int screenX = posX + x;
                if (screenX < 0 || screenX >= frameWidth) continue;
                
                int idx = (screenY * frameWidth + screenX) * 4;
                var bg = vol.BackgroundColor;
                byte alpha = (byte)((bg.Alpha * opacity) / 255);
                int invAlpha = 255 - alpha;
                
                frame[idx] = (byte)((bg.Red * alpha + frame[idx] * invAlpha) / 255);
                frame[idx + 1] = (byte)((bg.Green * alpha + frame[idx + 1] * invAlpha) / 255);
                frame[idx + 2] = (byte)((bg.Blue * alpha + frame[idx + 2] * invAlpha) / 255);
            }
        }
        
        // Draw volume bars
        int filledBars = vol.IsMuted ? 0 : (int)(barCount * Math.Min(vol.Volume, 1.5f) / 1.5f);
        
        for (int i = 0; i < barCount; i++)
        {
            int barX = posX + 2 + i * (barWidth + 2);
            int barHeight = 4 + (barMaxHeight - 4) * (i + 1) / barCount; // Increasing height
            int barY = posY + vol.Height - 4 - barHeight;
            
            OverlayColor barColor;
            if (vol.IsMuted || i >= filledBars)
            {
                barColor = vol.MutedColor;
            }
            else
            {
                // Color based on position
                float ratio = (float)i / barCount;
                if (ratio < 0.5f)
                    barColor = vol.LowVolumeColor;
                else if (ratio < 0.75f)
                    barColor = vol.MediumVolumeColor;
                else
                    barColor = vol.HighVolumeColor;
            }
            
            for (int y = 0; y < barHeight; y++)
            {
                int screenY = barY + y;
                if (screenY < 0 || screenY >= frameHeight) continue;
                
                for (int x = 0; x < barWidth; x++)
                {
                    int screenX = barX + x;
                    if (screenX < 0 || screenX >= frameWidth) continue;
                    
                    int idx = (screenY * frameWidth + screenX) * 4;
                    byte alpha = (byte)((barColor.Alpha * opacity) / 255);
                    int invAlpha = 255 - alpha;
                    
                    frame[idx] = (byte)((barColor.Red * alpha + frame[idx] * invAlpha) / 255);
                    frame[idx + 1] = (byte)((barColor.Green * alpha + frame[idx + 1] * invAlpha) / 255);
                    frame[idx + 2] = (byte)((barColor.Blue * alpha + frame[idx + 2] * invAlpha) / 255);
                }
            }
        }
    }
}

/// <summary>
/// Subtitle overlay for displaying subtitles on video.
/// </summary>
public sealed class SubtitleOverlay : OverlayItem
{
    /// <summary>Primary subtitle text.</summary>
    public string PrimaryText { get; set; } = string.Empty;
    
    /// <summary>Secondary subtitle text (e.g., translation).</summary>
    public string? SecondaryText { get; set; }
    
    /// <summary>Font family for primary text.</summary>
    public string FontFamily { get; set; } = "Arial";
    
    /// <summary>Font size for primary text.</summary>
    public int FontSize { get; set; } = 32;
    
    /// <summary>Font size for secondary text.</summary>
    public int SecondaryFontSize { get; set; } = 24;
    
    /// <summary>Primary text color.</summary>
    public OverlayColor TextColor { get; set; } = OverlayColor.White;
    
    /// <summary>Secondary text color.</summary>
    public OverlayColor SecondaryTextColor { get; set; } = new(200, 200, 200, 255);
    
    /// <summary>Outline color.</summary>
    public OverlayColor OutlineColor { get; set; } = OverlayColor.Black;
    
    /// <summary>Outline thickness.</summary>
    public int OutlineThickness { get; set; } = 2;
    
    /// <summary>Background box color (transparent for no box).</summary>
    public OverlayColor BackgroundColor { get; set; } = new(0, 0, 0, 128);
    
    /// <summary>Margin from screen bottom.</summary>
    public int BottomMargin { get; set; } = 50;
    
    /// <summary>Alignment (typically center for subtitles).</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Center;
    
    /// <summary>Whether to use karaoke-style highlighting.</summary>
    public bool EnableKaraoke { get; set; }
    
    /// <summary>Karaoke highlight progress (0.0 to 1.0).</summary>
    public float KaraokeProgress { get; set; }
    
    /// <summary>Karaoke highlight color.</summary>
    public OverlayColor KaraokeColor { get; set; } = new(255, 220, 0, 255);
}

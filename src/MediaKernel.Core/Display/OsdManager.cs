// MediaKernel - On-Screen Display (OSD)
// Text and graphics overlay for video playback

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace MediaKernel.Core.Display;

/// <summary>
/// Horizontal alignment for OSD elements.
/// </summary>
public enum OsdAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Vertical alignment for OSD elements.
/// </summary>
public enum OsdVerticalAlignment
{
    Top,
    Middle,
    Bottom
}

/// <summary>
/// OSD element position preset.
/// </summary>
public enum OsdPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// Type of OSD message.
/// </summary>
public enum OsdMessageType
{
    /// <summary>Temporary notification (e.g., volume change).</summary>
    Notification,
    
    /// <summary>Persistent status display (e.g., time remaining).</summary>
    Status,
    
    /// <summary>Progress bar (e.g., volume, seek position).</summary>
    Progress,
    
    /// <summary>Static text overlay.</summary>
    Text,
    
    /// <summary>Image/logo overlay.</summary>
    Image
}

/// <summary>
/// Represents an OSD message to display.
/// </summary>
public sealed class OsdMessage
{
    /// <summary>Unique identifier.</summary>
    public required string Id { get; init; }
    
    /// <summary>Message type.</summary>
    public OsdMessageType Type { get; init; }
    
    /// <summary>Text content.</summary>
    public string? Text { get; set; }
    
    /// <summary>Progress value (0-1) for progress bars.</summary>
    public float Progress { get; set; }
    
    /// <summary>Position on screen.</summary>
    public OsdPosition Position { get; init; } = OsdPosition.TopCenter;
    
    /// <summary>Display duration. Zero = persistent until removed.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);
    
    /// <summary>When the message was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>When the message should be removed.</summary>
    public DateTime ExpiresAt => Duration > TimeSpan.Zero ? CreatedAt + Duration : DateTime.MaxValue;
    
    /// <summary>Opacity (0-1).</summary>
    public float Opacity { get; init; } = 1.0f;
    
    /// <summary>Font size multiplier.</summary>
    public float FontScale { get; init; } = 1.0f;
    
    /// <summary>Text color (RGBA).</summary>
    public uint Color { get; init; } = 0xFFFFFFFF; // White
    
    /// <summary>Background color (RGBA).</summary>
    public uint BackgroundColor { get; init; } = 0x80000000; // Semi-transparent black
    
    /// <summary>Whether message has expired.</summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    
    /// <summary>
    /// Calculate fade-in/out opacity based on age.
    /// </summary>
    public float CalculateOpacity(TimeSpan fadeIn, TimeSpan fadeOut)
    {
        var age = DateTime.UtcNow - CreatedAt;
        var remaining = ExpiresAt - DateTime.UtcNow;
        
        float opacity = Opacity;
        
        // Fade in
        if (age < fadeIn && fadeIn > TimeSpan.Zero)
        {
            opacity *= (float)(age.TotalMilliseconds / fadeIn.TotalMilliseconds);
        }
        
        // Fade out
        if (Duration > TimeSpan.Zero && remaining < fadeOut && fadeOut > TimeSpan.Zero)
        {
            opacity *= (float)(remaining.TotalMilliseconds / fadeOut.TotalMilliseconds);
        }
        
        return Math.Clamp(opacity, 0f, 1f);
    }
}

/// <summary>
/// Icon type for common OSD graphics.
/// </summary>
public enum OsdIcon
{
    None,
    Play,
    Pause,
    Stop,
    FastForward,
    Rewind,
    SkipNext,
    SkipPrevious,
    Volume,
    VolumeMute,
    VolumeHigh,
    VolumeLow,
    Subtitles,
    SubtitlesOff,
    Fullscreen,
    ExitFullscreen,
    Settings,
    Loop,
    Shuffle
}

/// <summary>
/// Progress bar configuration.
/// </summary>
public readonly struct OsdProgressBar
{
    /// <summary>Current value (0-1).</summary>
    public float Value { get; init; }
    
    /// <summary>Fill color (RGBA).</summary>
    public uint FillColor { get; init; }
    
    /// <summary>Background color (RGBA).</summary>
    public uint BackgroundColor { get; init; }
    
    /// <summary>Width in pixels (0 = full width minus margin).</summary>
    public int Width { get; init; }
    
    /// <summary>Height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Border radius in pixels.</summary>
    public int BorderRadius { get; init; }
    
    public OsdProgressBar()
    {
        Value = 0;
        FillColor = 0xFF4CAF50; // Green
        BackgroundColor = 0x80000000; // Semi-transparent black
        Width = 0;
        Height = 8;
        BorderRadius = 4;
    }
}

/// <summary>
/// Manages on-screen display elements.
/// </summary>
public sealed class OsdManager : IDisposable
{
    private readonly ConcurrentDictionary<string, OsdMessage> _messages = new();
    private readonly Timer _cleanupTimer;
    private readonly object _lock = new();
    
    /// <summary>
    /// Event fired when OSD content changes.
    /// </summary>
    public event EventHandler? ContentChanged;
    
    /// <summary>
    /// Whether OSD is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Default fade-in duration.
    /// </summary>
    public TimeSpan FadeInDuration { get; set; } = TimeSpan.FromMilliseconds(200);
    
    /// <summary>
    /// Default fade-out duration.
    /// </summary>
    public TimeSpan FadeOutDuration { get; set; } = TimeSpan.FromMilliseconds(500);
    
    /// <summary>
    /// Screen margin in pixels.
    /// </summary>
    public int Margin { get; set; } = 20;
    
    /// <summary>
    /// Base font size in pixels.
    /// </summary>
    public int BaseFontSize { get; set; } = 32;
    
    public OsdManager()
    {
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }
    
    /// <summary>
    /// Show a notification message.
    /// </summary>
    public void ShowNotification(string text, TimeSpan? duration = null, OsdPosition position = OsdPosition.TopCenter)
    {
        var message = new OsdMessage
        {
            Id = $"notification_{Guid.NewGuid():N}",
            Type = OsdMessageType.Notification,
            Text = text,
            Duration = duration ?? TimeSpan.FromSeconds(2),
            Position = position
        };
        
        AddMessage(message);
    }
    
    /// <summary>
    /// Show a progress notification (e.g., volume change).
    /// </summary>
    public void ShowProgress(string id, string label, float value, TimeSpan? duration = null)
    {
        var message = new OsdMessage
        {
            Id = id,
            Type = OsdMessageType.Progress,
            Text = label,
            Progress = Math.Clamp(value, 0f, 1f),
            Duration = duration ?? TimeSpan.FromSeconds(1.5),
            Position = OsdPosition.TopCenter
        };
        
        AddMessage(message);
    }
    
    /// <summary>
    /// Show volume OSD.
    /// </summary>
    public void ShowVolume(int volumePercent, bool isMuted = false)
    {
        string text = isMuted ? "Muted" : $"Volume: {volumePercent}%";
        float progress = volumePercent / 100f;
        
        ShowProgress("volume", text, progress);
    }
    
    /// <summary>
    /// Show seek position OSD.
    /// </summary>
    public void ShowSeekPosition(TimeSpan position, TimeSpan duration)
    {
        string posText = FormatTime(position);
        string durText = FormatTime(duration);
        string text = $"{posText} / {durText}";
        float progress = duration > TimeSpan.Zero ? (float)(position / duration) : 0;
        
        ShowProgress("seek", text, progress);
    }
    
    /// <summary>
    /// Show playback speed OSD.
    /// </summary>
    public void ShowPlaybackSpeed(double speed)
    {
        string text = speed == 1.0 ? "Normal Speed" : $"Speed: {speed:F2}x";
        ShowNotification(text, TimeSpan.FromSeconds(1.5));
    }
    
    /// <summary>
    /// Show play/pause state.
    /// </summary>
    public void ShowPlayPause(bool isPlaying)
    {
        ShowNotification(isPlaying ? "▶ Play" : "❚❚ Pause", TimeSpan.FromSeconds(1));
    }
    
    /// <summary>
    /// Show subtitle track change.
    /// </summary>
    public void ShowSubtitleTrack(string trackName, bool enabled)
    {
        string text = enabled ? $"Subtitles: {trackName}" : "Subtitles: Off";
        ShowNotification(text);
    }
    
    /// <summary>
    /// Show audio track change.
    /// </summary>
    public void ShowAudioTrack(string trackName)
    {
        ShowNotification($"Audio: {trackName}");
    }
    
    /// <summary>
    /// Show/update a persistent status message.
    /// </summary>
    public void SetStatus(string id, string text, OsdPosition position = OsdPosition.BottomLeft)
    {
        var message = new OsdMessage
        {
            Id = $"status_{id}",
            Type = OsdMessageType.Status,
            Text = text,
            Duration = TimeSpan.Zero, // Persistent
            Position = position,
            Opacity = 0.8f
        };
        
        AddMessage(message);
    }
    
    /// <summary>
    /// Remove a status message.
    /// </summary>
    public void ClearStatus(string id)
    {
        RemoveMessage($"status_{id}");
    }
    
    /// <summary>
    /// Add or update a message.
    /// </summary>
    public void AddMessage(OsdMessage message)
    {
        if (!IsEnabled)
            return;
        
        _messages[message.Id] = message;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Remove a message by ID.
    /// </summary>
    public void RemoveMessage(string id)
    {
        if (_messages.TryRemove(id, out _))
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    
    /// <summary>
    /// Get all active messages.
    /// </summary>
    public IReadOnlyCollection<OsdMessage> GetActiveMessages()
    {
        return _messages.Values
            .Where(m => !m.IsExpired)
            .ToList();
    }
    
    /// <summary>
    /// Get messages at a specific position.
    /// </summary>
    public IReadOnlyCollection<OsdMessage> GetMessagesAt(OsdPosition position)
    {
        return _messages.Values
            .Where(m => !m.IsExpired && m.Position == position)
            .OrderBy(m => m.CreatedAt)
            .ToList();
    }
    
    /// <summary>
    /// Clear all messages.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Calculate screen position for OSD element.
    /// </summary>
    public (int X, int Y) CalculatePosition(OsdPosition position, int screenWidth, int screenHeight, int elementWidth, int elementHeight)
    {
        int x = position switch
        {
            OsdPosition.TopLeft or OsdPosition.MiddleLeft or OsdPosition.BottomLeft => Margin,
            OsdPosition.TopCenter or OsdPosition.MiddleCenter or OsdPosition.BottomCenter => (screenWidth - elementWidth) / 2,
            OsdPosition.TopRight or OsdPosition.MiddleRight or OsdPosition.BottomRight => screenWidth - elementWidth - Margin,
            _ => Margin
        };
        
        int y = position switch
        {
            OsdPosition.TopLeft or OsdPosition.TopCenter or OsdPosition.TopRight => Margin,
            OsdPosition.MiddleLeft or OsdPosition.MiddleCenter or OsdPosition.MiddleRight => (screenHeight - elementHeight) / 2,
            OsdPosition.BottomLeft or OsdPosition.BottomCenter or OsdPosition.BottomRight => screenHeight - elementHeight - Margin,
            _ => Margin
        };
        
        return (x, y);
    }
    
    private void CleanupExpired(object? state)
    {
        bool removed = false;
        
        foreach (var kvp in _messages)
        {
            if (kvp.Value.IsExpired && _messages.TryRemove(kvp.Key, out _))
            {
                removed = true;
            }
        }
        
        if (removed)
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    
    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes}:{time.Seconds:D2}";
    }
    
    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}

/// <summary>
/// Color utilities for OSD rendering.
/// </summary>
public static class OsdColors
{
    // Standard colors (ARGB)
    public const uint White = 0xFFFFFFFF;
    public const uint Black = 0xFF000000;
    public const uint Red = 0xFFFF0000;
    public const uint Green = 0xFF00FF00;
    public const uint Blue = 0xFF0000FF;
    public const uint Yellow = 0xFFFFFF00;
    public const uint Cyan = 0xFF00FFFF;
    public const uint Magenta = 0xFFFF00FF;
    
    // Semi-transparent
    public const uint TransparentBlack50 = 0x80000000;
    public const uint TransparentBlack75 = 0xC0000000;
    public const uint TransparentWhite50 = 0x80FFFFFF;
    
    // UI colors
    public const uint VolumeGreen = 0xFF4CAF50;
    public const uint ProgressBlue = 0xFF2196F3;
    public const uint ErrorRed = 0xFFF44336;
    public const uint WarningOrange = 0xFFFF9800;
    
    /// <summary>
    /// Create ARGB color from components.
    /// </summary>
    public static uint FromArgb(byte a, byte r, byte g, byte b) =>
        (uint)(a << 24 | r << 16 | g << 8 | b);
    
    /// <summary>
    /// Create ARGB color with specified opacity.
    /// </summary>
    public static uint WithAlpha(uint color, byte alpha) =>
        (color & 0x00FFFFFF) | ((uint)alpha << 24);
    
    /// <summary>
    /// Extract alpha component.
    /// </summary>
    public static byte GetAlpha(uint color) => (byte)(color >> 24);
    
    /// <summary>
    /// Extract red component.
    /// </summary>
    public static byte GetRed(uint color) => (byte)(color >> 16);
    
    /// <summary>
    /// Extract green component.
    /// </summary>
    public static byte GetGreen(uint color) => (byte)(color >> 8);
    
    /// <summary>
    /// Extract blue component.
    /// </summary>
    public static byte GetBlue(uint color) => (byte)color;
}

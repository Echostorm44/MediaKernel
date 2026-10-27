// Player settings and preferences
// Stores user configuration for audio, video, subtitles, and playback

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaKernel.Core.Settings;

/// <summary>
/// Audio output settings.
/// </summary>
public sealed class AudioSettings
{
    /// <summary>Volume level (0.0 to 1.0).</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Whether audio is muted.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Preferred audio output device ID (null = default).</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Equalizer preset name (null = disabled).</summary>
    public string? EqualizerPreset { get; set; }

    /// <summary>Custom equalizer band gains in dB (10 bands).</summary>
    public float[]? EqualizerBands { get; set; }

    /// <summary>Whether to enable audio normalization.</summary>
    public bool EnableNormalization { get; set; }

    /// <summary>Audio delay in milliseconds (positive = delay audio).</summary>
    public int AudioDelayMilliseconds { get; set; }

    /// <summary>Preferred audio track language (ISO 639-1 code, e.g. "en").</summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>Enable downmixing to stereo for headphones.</summary>
    public bool DownmixToStereo { get; set; }

    public AudioSettings Clone() => new()
    {
        Volume = Volume,
        IsMuted = IsMuted,
        OutputDeviceId = OutputDeviceId,
        EqualizerPreset = EqualizerPreset,
        EqualizerBands = EqualizerBands?.ToArray(),
        EnableNormalization = EnableNormalization,
        AudioDelayMilliseconds = AudioDelayMilliseconds,
        PreferredLanguage = PreferredLanguage,
        DownmixToStereo = DownmixToStereo
    };
}

/// <summary>
/// Video output settings.
/// </summary>
public sealed class VideoSettings
{
    /// <summary>Preferred video scaling mode.</summary>
    public VideoScaleMode ScaleMode { get; set; } = VideoScaleMode.Fit;

    /// <summary>Enable hardware acceleration.</summary>
    public bool EnableHardwareAcceleration { get; set; } = true;

    /// <summary>Deinterlace mode.</summary>
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Auto;

    /// <summary>Brightness adjustment (-1.0 to 1.0).</summary>
    public float Brightness { get; set; }

    /// <summary>Contrast adjustment (0.0 to 2.0, 1.0 = normal).</summary>
    public float Contrast { get; set; } = 1.0f;

    /// <summary>Saturation adjustment (0.0 to 2.0, 1.0 = normal).</summary>
    public float Saturation { get; set; } = 1.0f;

    /// <summary>Hue adjustment in degrees (-180 to 180).</summary>
    public float Hue { get; set; }

    /// <summary>Gamma adjustment (0.1 to 3.0, 1.0 = normal).</summary>
    public float Gamma { get; set; } = 1.0f;

    /// <summary>Enable video sharpening filter.</summary>
    public bool EnableSharpening { get; set; }

    /// <summary>Sharpening strength (0.0 to 2.0).</summary>
    public float SharpenStrength { get; set; } = 0.5f;

    /// <summary>Aspect ratio override (null = use source, e.g. "16:9", "4:3").</summary>
    public string? AspectRatioOverride { get; set; }

    /// <summary>Crop setting (null = none, e.g. "16:9", "1.85:1").</summary>
    public string? CropMode { get; set; }

    public VideoSettings Clone() => new()
    {
        ScaleMode = ScaleMode,
        EnableHardwareAcceleration = EnableHardwareAcceleration,
        Deinterlace = Deinterlace,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Hue = Hue,
        Gamma = Gamma,
        EnableSharpening = EnableSharpening,
        SharpenStrength = SharpenStrength,
        AspectRatioOverride = AspectRatioOverride,
        CropMode = CropMode
    };
}

/// <summary>
/// Subtitle display settings.
/// </summary>
public sealed class SubtitleSettings
{
    /// <summary>Whether subtitles are enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Font family name.</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Font size in points.</summary>
    public int FontSize { get; set; } = 24;

    /// <summary>Font color as ARGB hex (e.g. "FFFFFFFF" for white).</summary>
    public string FontColor { get; set; } = "FFFFFFFF";

    /// <summary>Outline/border color as ARGB hex.</summary>
    public string OutlineColor { get; set; } = "FF000000";

    /// <summary>Outline width in pixels.</summary>
    public int OutlineWidth { get; set; } = 2;

    /// <summary>Background/shadow opacity (0.0 to 1.0).</summary>
    public float BackgroundOpacity { get; set; } = 0.5f;

    /// <summary>Vertical position from bottom (0.0 = bottom, 1.0 = top).</summary>
    public float VerticalPosition { get; set; } = 0.05f;

    /// <summary>Subtitle delay in milliseconds (positive = delay subtitles).</summary>
    public int SubtitleDelayMilliseconds { get; set; }

    /// <summary>Preferred subtitle track language (ISO 639-1 code).</summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>Enable subtitle auto-selection.</summary>
    public bool AutoSelectSubtitles { get; set; } = true;

    /// <summary>Use bold font.</summary>
    public bool Bold { get; set; }

    public SubtitleSettings Clone() => new()
    {
        Enabled = Enabled,
        FontFamily = FontFamily,
        FontSize = FontSize,
        FontColor = FontColor,
        OutlineColor = OutlineColor,
        OutlineWidth = OutlineWidth,
        BackgroundOpacity = BackgroundOpacity,
        VerticalPosition = VerticalPosition,
        SubtitleDelayMilliseconds = SubtitleDelayMilliseconds,
        PreferredLanguage = PreferredLanguage,
        AutoSelectSubtitles = AutoSelectSubtitles,
        Bold = Bold
    };
}

/// <summary>
/// Playback behavior settings.
/// </summary>
public sealed class PlaybackSettings
{
    /// <summary>Default playback speed (1.0 = normal).</summary>
    public float DefaultSpeed { get; set; } = 1.0f;

    /// <summary>Resume playback from last position.</summary>
    public bool ResumePlayback { get; set; } = true;

    /// <summary>Minimum position to save for resume (seconds).</summary>
    public int ResumeMinimumSeconds { get; set; } = 30;

    /// <summary>Auto-play when media is opened.</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>Default repeat mode for playlists.</summary>
    public RepeatModeSetting DefaultRepeatMode { get; set; } = RepeatModeSetting.None;

    /// <summary>Enable shuffle by default.</summary>
    public bool DefaultShuffle { get; set; }

    /// <summary>Skip forward duration in seconds.</summary>
    public int SkipForwardSeconds { get; set; } = 10;

    /// <summary>Skip backward duration in seconds.</summary>
    public int SkipBackwardSeconds { get; set; } = 10;

    /// <summary>Long skip duration in seconds (Ctrl+Arrow).</summary>
    public int LongSkipSeconds { get; set; } = 60;

    /// <summary>Enable media key support.</summary>
    public bool EnableMediaKeys { get; set; } = true;

    public PlaybackSettings Clone() => new()
    {
        DefaultSpeed = DefaultSpeed,
        ResumePlayback = ResumePlayback,
        ResumeMinimumSeconds = ResumeMinimumSeconds,
        AutoPlay = AutoPlay,
        DefaultRepeatMode = DefaultRepeatMode,
        DefaultShuffle = DefaultShuffle,
        SkipForwardSeconds = SkipForwardSeconds,
        SkipBackwardSeconds = SkipBackwardSeconds,
        LongSkipSeconds = LongSkipSeconds,
        EnableMediaKeys = EnableMediaKeys
    };
}

/// <summary>
/// Interface/window settings.
/// </summary>
public sealed class InterfaceSettings
{
    /// <summary>Remember window size and position.</summary>
    public bool RememberWindowState { get; set; } = true;

    /// <summary>Last window width.</summary>
    public int WindowWidth { get; set; } = 1280;

    /// <summary>Last window height.</summary>
    public int WindowHeight { get; set; } = 720;

    /// <summary>Last window X position.</summary>
    public int? WindowX { get; set; }

    /// <summary>Last window Y position.</summary>
    public int? WindowY { get; set; }

    /// <summary>Whether window was maximized.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>Show playlist panel by default.</summary>
    public bool ShowPlaylist { get; set; }

    /// <summary>Show on-screen display (volume, seek, etc).</summary>
    public bool ShowOsd { get; set; } = true;

    /// <summary>Hide cursor and controls after inactivity (seconds, 0 = never).</summary>
    public int HideControlsAfterSeconds { get; set; } = 3;

    /// <summary>UI theme (System, Light, Dark).</summary>
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;

    /// <summary>UI accent color as ARGB hex.</summary>
    public string? AccentColor { get; set; }

    public InterfaceSettings Clone() => new()
    {
        RememberWindowState = RememberWindowState,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowX = WindowX,
        WindowY = WindowY,
        IsMaximized = IsMaximized,
        ShowPlaylist = ShowPlaylist,
        ShowOsd = ShowOsd,
        HideControlsAfterSeconds = HideControlsAfterSeconds,
        Theme = Theme,
        AccentColor = AccentColor
    };
}

/// <summary>
/// Video scaling mode options.
/// </summary>
public enum VideoScaleMode
{
    /// <summary>Fit video within window, preserve aspect ratio.</summary>
    Fit,

    /// <summary>Fill window, may crop edges.</summary>
    Fill,

    /// <summary>Stretch to fill window (distorts aspect ratio).</summary>
    Stretch,

    /// <summary>Original size (1:1 pixels, may be larger/smaller than window).</summary>
    Original
}

/// <summary>
/// Deinterlace mode options.
/// </summary>
public enum DeinterlaceMode
{
    /// <summary>Disable deinterlacing.</summary>
    Off,

    /// <summary>Auto-detect interlaced content.</summary>
    Auto,

    /// <summary>Always deinterlace.</summary>
    On
}

/// <summary>
/// Repeat mode setting for persistence.
/// </summary>
public enum RepeatModeSetting
{
    None,
    One,
    All
}

/// <summary>
/// UI theme setting.
/// </summary>
public enum ThemeSetting
{
    /// <summary>Follow system theme.</summary>
    System,

    /// <summary>Light theme.</summary>
    Light,

    /// <summary>Dark theme.</summary>
    Dark
}

/// <summary>
/// Root settings object containing all player preferences.
/// </summary>
public sealed class PlayerSettings
{
    /// <summary>Settings schema version for migration.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Audio settings.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Video settings.</summary>
    public VideoSettings Video { get; set; } = new();

    /// <summary>Subtitle settings.</summary>
    public SubtitleSettings Subtitles { get; set; } = new();

    /// <summary>Playback settings.</summary>
    public PlaybackSettings Playback { get; set; } = new();

    /// <summary>Interface settings.</summary>
    public InterfaceSettings Interface { get; set; } = new();

    /// <summary>Recent files list (up to RecentFilesLimit).</summary>
    public List<RecentFile> RecentFiles { get; set; } = [];

    /// <summary>Maximum number of recent files to remember.</summary>
    public int RecentFilesLimit { get; set; } = 20;

    /// <summary>Creates a deep clone of settings.</summary>
    public PlayerSettings Clone() => new()
    {
        Version = Version,
        Audio = Audio.Clone(),
        Video = Video.Clone(),
        Subtitles = Subtitles.Clone(),
        Playback = Playback.Clone(),
        Interface = Interface.Clone(),
        RecentFiles = RecentFiles.Select(r => r.Clone()).ToList(),
        RecentFilesLimit = RecentFilesLimit
    };

    /// <summary>Adds a file to the recent files list.</summary>
    public void AddRecentFile(string uri, string? title = null, long? positionMs = null)
    {
        // Remove existing entry for this URI
        RecentFiles.RemoveAll(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase));

        // Add to front
        RecentFiles.Insert(0, new RecentFile
        {
            Uri = uri,
            Title = title ?? Path.GetFileName(uri),
            LastPlayedUtc = DateTime.UtcNow,
            LastPositionMilliseconds = positionMs
        });

        // Trim to limit
        while (RecentFiles.Count > RecentFilesLimit)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
    }

    /// <summary>Gets the resume position for a file.</summary>
    public long? GetResumePosition(string uri)
    {
        var recent = RecentFiles.FirstOrDefault(r =>
            string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase));
        return recent?.LastPositionMilliseconds;
    }

    /// <summary>Clears the recent files list.</summary>
    public void ClearRecentFiles() => RecentFiles.Clear();
}

/// <summary>
/// A recently played file entry.
/// </summary>
public sealed class RecentFile
{
    /// <summary>File path or URL.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>Display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>When the file was last played.</summary>
    public DateTime LastPlayedUtc { get; set; }

    /// <summary>Last playback position in milliseconds.</summary>
    public long? LastPositionMilliseconds { get; set; }

    public RecentFile Clone() => new()
    {
        Uri = Uri,
        Title = Title,
        LastPlayedUtc = LastPlayedUtc,
        LastPositionMilliseconds = LastPositionMilliseconds
    };
}

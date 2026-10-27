// Settings persistence manager
// Loads and saves PlayerSettings to JSON file

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaKernel.Core.Settings;

/// <summary>
/// The settings file's shape, generated at compile time rather than discovered by reflection.
/// </summary>
/// <remarks>
/// <c>JsonSerializer.Serialize&lt;T&gt;(T, JsonSerializerOptions)</c> carries
/// <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>: it walks the type with
/// reflection, so a trimmer cannot know which properties to keep and AOT cannot generate the
/// accessors. A source-generated context states the shape in IL the compiler can see, which is
/// why it is the only form that works in a native build. The options move onto the attribute
/// because the generator has to know them at compile time too - a <c>JsonSerializerOptions</c>
/// built at run time would put the reflection back.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PlayerSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Manages loading, saving, and accessing player settings.
/// </summary>
public sealed class SettingsManager
{

    private readonly string settingsFilePath;
    private readonly object syncLock = new();
    private PlayerSettings settings;
    private bool isDirty;

    /// <summary>Event raised when settings change.</summary>
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>Gets the current settings (read-only view).</summary>
    public PlayerSettings Settings
    {
        get { lock (syncLock) return settings; }
    }

    /// <summary>Gets the settings file path.</summary>
    public string FilePath => settingsFilePath;

    /// <summary>Gets whether there are unsaved changes.</summary>
    public bool HasUnsavedChanges
    {
        get { lock (syncLock) return isDirty; }
    }

    /// <summary>
    /// Creates a new SettingsManager with the specified settings file path.
    /// </summary>
    /// <param name="settingsFilePath">Path to the settings JSON file.</param>
    public SettingsManager(string settingsFilePath)
    {
        this.settingsFilePath = settingsFilePath ?? throw new ArgumentNullException(nameof(settingsFilePath));
        this.settings = new PlayerSettings();
    }

    /// <summary>
    /// Creates a SettingsManager using the default settings location.
    /// </summary>
    public static SettingsManager CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDir = Path.Combine(appData, "MediaKernel");
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        return new SettingsManager(settingsPath);
    }

    /// <summary>
    /// Loads settings from disk. If the file doesn't exist, uses defaults.
    /// </summary>
    public void Load()
    {
        lock (syncLock)
        {
            if (File.Exists(settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(settingsFilePath);
                    var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PlayerSettings);
                    settings = loaded ?? new PlayerSettings();
                    MigrateIfNeeded();
                }
                catch (JsonException)
                {
                    // Invalid JSON - use defaults
                    settings = new PlayerSettings();
                }
            }
            else
            {
                settings = new PlayerSettings();
            }

            isDirty = false;
        }
    }

    /// <summary>
    /// Saves settings to disk.
    /// </summary>
    public void Save()
    {
        lock (syncLock)
        {
            var directory = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.PlayerSettings);
            File.WriteAllText(settingsFilePath, json);
            isDirty = false;
        }
    }

    /// <summary>
    /// Saves settings if there are unsaved changes.
    /// </summary>
    public void SaveIfDirty()
    {
        if (HasUnsavedChanges)
        {
            Save();
        }
    }

    /// <summary>
    /// Updates settings and raises the SettingsChanged event.
    /// </summary>
    /// <param name="updateAction">Action to modify settings.</param>
    /// <param name="section">Optional section name for the event.</param>
    public void Update(Action<PlayerSettings> updateAction, string? section = null)
    {
        lock (syncLock)
        {
            updateAction(settings);
            isDirty = true;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section));
    }

    /// <summary>
    /// Updates audio settings.
    /// </summary>
    public void UpdateAudio(Action<AudioSettings> updateAction)
    {
        Update(s => updateAction(s.Audio), "Audio");
    }

    /// <summary>
    /// Updates video settings.
    /// </summary>
    public void UpdateVideo(Action<VideoSettings> updateAction)
    {
        Update(s => updateAction(s.Video), "Video");
    }

    /// <summary>
    /// Updates subtitle settings.
    /// </summary>
    public void UpdateSubtitles(Action<SubtitleSettings> updateAction)
    {
        Update(s => updateAction(s.Subtitles), "Subtitles");
    }

    /// <summary>
    /// Updates playback settings.
    /// </summary>
    public void UpdatePlayback(Action<PlaybackSettings> updateAction)
    {
        Update(s => updateAction(s.Playback), "Playback");
    }

    /// <summary>
    /// Updates interface settings.
    /// </summary>
    public void UpdateInterface(Action<InterfaceSettings> updateAction)
    {
        Update(s => updateAction(s.Interface), "Interface");
    }

    /// <summary>
    /// Adds a file to recent files and optionally saves.
    /// </summary>
    public void AddRecentFile(string uri, string? title = null, long? positionMs = null, bool autoSave = true)
    {
        Update(s => s.AddRecentFile(uri, title, positionMs), "RecentFiles");

        if (autoSave)
        {
            Save();
        }
    }

    /// <summary>
    /// Resets all settings to defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (syncLock)
        {
            settings = new PlayerSettings();
            isDirty = true;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(null));
    }

    /// <summary>
    /// Resets a specific section to defaults.
    /// </summary>
    public void ResetSection(string section)
    {
        lock (syncLock)
        {
            switch (section.ToLowerInvariant())
            {
                case "audio":
                    settings.Audio = new AudioSettings();
                    break;
                case "video":
                    settings.Video = new VideoSettings();
                    break;
                case "subtitles":
                    settings.Subtitles = new SubtitleSettings();
                    break;
                case "playback":
                    settings.Playback = new PlaybackSettings();
                    break;
                case "interface":
                    settings.Interface = new InterfaceSettings();
                    break;
                default:
                    return;
            }
            isDirty = true;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section));
    }

    /// <summary>
    /// Exports settings to a file.
    /// </summary>
    public void ExportTo(string filePath)
    {
        lock (syncLock)
        {
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.PlayerSettings);
            File.WriteAllText(filePath, json);
        }
    }

    /// <summary>
    /// Imports settings from a file.
    /// </summary>
    public void ImportFrom(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var imported = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PlayerSettings)
            ?? throw new InvalidOperationException("Failed to deserialize settings");

        lock (syncLock)
        {
            settings = imported;
            isDirty = true;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(null));
    }

    private void MigrateIfNeeded()
    {
        // Future: handle settings version migrations here
        // if (settings.Version < CurrentVersion) { ... migrate ... }
    }
}

/// <summary>
/// Event args for settings changes.
/// </summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>The section that changed (null = all/reset).</summary>
    public string? Section { get; }

    public SettingsChangedEventArgs(string? section)
    {
        Section = section;
    }
}

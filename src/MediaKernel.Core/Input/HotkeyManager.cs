// MediaKernel - Hotkey System
// Keyboard shortcut handling for media player actions

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MediaKernel.Core.Input;

/// <summary>
/// Keyboard modifier keys.
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>
/// Standard actions that can be triggered by hotkeys.
/// </summary>
public enum MediaAction
{
    // Playback control
    PlayPause,
    Stop,
    NextTrack,
    PreviousTrack,
    
    // Seeking
    SeekForwardShort,
    SeekBackwardShort,
    SeekForwardMedium,
    SeekBackwardMedium,
    SeekForwardLong,
    SeekBackwardLong,
    SeekToStart,
    SeekToEnd,
    
    // Volume
    VolumeUp,
    VolumeDown,
    Mute,
    
    // Speed
    SpeedUp,
    SpeedDown,
    SpeedNormal,
    
    // Video
    ToggleFullscreen,
    CycleAspectRatio,
    CycleCropRatio,
    CycleZoom,
    SnapshotVideo,
    
    // Audio
    CycleAudioTrack,
    AudioDelayIncrease,
    AudioDelayDecrease,
    
    // Subtitles
    CycleSubtitleTrack,
    ToggleSubtitles,
    SubtitleDelayIncrease,
    SubtitleDelayDecrease,
    SubtitleSizeIncrease,
    SubtitleSizeDecrease,
    
    // Navigation
    ChapterNext,
    ChapterPrevious,
    TitleNext,
    TitlePrevious,
    
    // Interface
    ToggleOsd,
    TogglePlaylist,
    ToggleStatistics,
    Quit,
    
    // Bookmarks
    SetBookmark,
    JumpToBookmark,
    
    // Frame stepping
    FrameNext,
    FramePrevious,
    
    // A/B loop
    LoopSetA,
    LoopSetB,
    LoopClear
}

/// <summary>
/// Represents a keyboard shortcut.
/// </summary>
public readonly struct KeyBinding : IEquatable<KeyBinding>
{
    /// <summary>Key code (platform-specific or virtual key).</summary>
    public int KeyCode { get; }
    
    /// <summary>Modifier keys required.</summary>
    public KeyModifiers Modifiers { get; }
    
    public KeyBinding(int keyCode, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyCode = keyCode;
        Modifiers = modifiers;
    }
    
    public bool Equals(KeyBinding other) =>
        KeyCode == other.KeyCode && Modifiers == other.Modifiers;
    
    public override bool Equals(object? obj) =>
        obj is KeyBinding other && Equals(other);
    
    public override int GetHashCode() =>
        HashCode.Combine(KeyCode, Modifiers);
    
    public static bool operator ==(KeyBinding left, KeyBinding right) => left.Equals(right);
    public static bool operator !=(KeyBinding left, KeyBinding right) => !left.Equals(right);
    
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        parts.Add(((VirtualKey)KeyCode).ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// Common virtual key codes.
/// </summary>
public enum VirtualKey
{
    None = 0,
    
    // Letters
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    
    // Numbers
    D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    
    // Function keys
    F1 = 112, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    
    // Navigation
    Left = 37, Up, Right, Down,
    Home = 36, End = 35,
    PageUp = 33, PageDown = 34,
    
    // Special
    Space = 32,
    Enter = 13,
    Escape = 27,
    Tab = 9,
    Backspace = 8,
    Delete = 46,
    Insert = 45,
    
    // Numpad
    NumPad0 = 96, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply = 106,
    Add = 107,
    Subtract = 109,
    Decimal = 110,
    Divide = 111,
    
    // Symbols
    OemPlus = 187,
    OemMinus = 189,
    OemComma = 188,
    OemPeriod = 190,
    OemOpenBrackets = 219,
    OemCloseBrackets = 221
}

/// <summary>
/// Event arguments for action triggered.
/// </summary>
public sealed class ActionTriggeredEventArgs : EventArgs
{
    public MediaAction Action { get; }
    public KeyBinding Binding { get; }
    public DateTime Timestamp { get; }
    
    public ActionTriggeredEventArgs(MediaAction action, KeyBinding binding)
    {
        Action = action;
        Binding = binding;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
/// Manages keyboard shortcuts and action bindings.
/// </summary>
public sealed class HotkeyManager
{
    private readonly Dictionary<KeyBinding, MediaAction> _bindings = new();
    private readonly Dictionary<MediaAction, List<KeyBinding>> _actionBindings = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Event fired when an action is triggered.
    /// </summary>
    public event EventHandler<ActionTriggeredEventArgs>? ActionTriggered;
    
    /// <summary>
    /// Whether hotkeys are enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    public HotkeyManager()
    {
        LoadDefaultBindings();
    }
    
    /// <summary>
    /// Bind a key combination to an action.
    /// </summary>
    public void Bind(KeyBinding binding, MediaAction action)
    {
        lock (_lock)
        {
            // Remove old binding if exists
            if (_bindings.TryGetValue(binding, out var oldAction))
            {
                if (_actionBindings.TryGetValue(oldAction, out var list))
                    list.Remove(binding);
            }
            
            _bindings[binding] = action;
            
            if (!_actionBindings.TryGetValue(action, out var bindings))
            {
                bindings = new List<KeyBinding>();
                _actionBindings[action] = bindings;
            }
            if (!bindings.Contains(binding))
                bindings.Add(binding);
        }
    }
    
    /// <summary>
    /// Bind a key code with modifiers to an action.
    /// </summary>
    public void Bind(int keyCode, KeyModifiers modifiers, MediaAction action) =>
        Bind(new KeyBinding(keyCode, modifiers), action);
    
    /// <summary>
    /// Bind a virtual key with modifiers to an action.
    /// </summary>
    public void Bind(VirtualKey key, KeyModifiers modifiers, MediaAction action) =>
        Bind(new KeyBinding((int)key, modifiers), action);
    
    /// <summary>
    /// Bind a virtual key to an action.
    /// </summary>
    public void Bind(VirtualKey key, MediaAction action) =>
        Bind(new KeyBinding((int)key, KeyModifiers.None), action);
    
    /// <summary>
    /// Remove a key binding.
    /// </summary>
    public void Unbind(KeyBinding binding)
    {
        lock (_lock)
        {
            if (_bindings.TryGetValue(binding, out var action))
            {
                _bindings.Remove(binding);
                if (_actionBindings.TryGetValue(action, out var list))
                    list.Remove(binding);
            }
        }
    }
    
    /// <summary>
    /// Remove all bindings for an action.
    /// </summary>
    public void UnbindAction(MediaAction action)
    {
        lock (_lock)
        {
            if (_actionBindings.TryGetValue(action, out var bindings))
            {
                foreach (var binding in bindings.ToList())
                    _bindings.Remove(binding);
                _actionBindings.Remove(action);
            }
        }
    }
    
    /// <summary>
    /// Get all bindings for an action.
    /// </summary>
    public IReadOnlyList<KeyBinding> GetBindings(MediaAction action)
    {
        lock (_lock)
        {
            if (_actionBindings.TryGetValue(action, out var bindings))
                return bindings.ToList();
            return Array.Empty<KeyBinding>();
        }
    }
    
    /// <summary>
    /// Get all registered bindings.
    /// </summary>
    public IReadOnlyDictionary<KeyBinding, MediaAction> GetAllBindings()
    {
        lock (_lock)
        {
            return new Dictionary<KeyBinding, MediaAction>(_bindings);
        }
    }
    
    /// <summary>
    /// Handle a key press event.
    /// </summary>
    /// <returns>True if the key triggered an action.</returns>
    public bool HandleKeyPress(int keyCode, KeyModifiers modifiers)
    {
        if (!IsEnabled)
            return false;
        
        var binding = new KeyBinding(keyCode, modifiers);
        
        lock (_lock)
        {
            if (_bindings.TryGetValue(binding, out var action))
            {
                ActionTriggered?.Invoke(this, new ActionTriggeredEventArgs(action, binding));
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Handle a key press event.
    /// </summary>
    public bool HandleKeyPress(VirtualKey key, KeyModifiers modifiers) =>
        HandleKeyPress((int)key, modifiers);
    
    /// <summary>
    /// Clear all bindings.
    /// </summary>
    public void ClearBindings()
    {
        lock (_lock)
        {
            _bindings.Clear();
            _actionBindings.Clear();
        }
    }
    
    /// <summary>
    /// Load default VLC-like key bindings.
    /// </summary>
    public void LoadDefaultBindings()
    {
        lock (_lock)
        {
            _bindings.Clear();
            _actionBindings.Clear();
            
            // Playback
            Bind(VirtualKey.Space, MediaAction.PlayPause);
            Bind(VirtualKey.S, MediaAction.Stop);
            Bind(VirtualKey.N, MediaAction.NextTrack);
            Bind(VirtualKey.P, MediaAction.PreviousTrack);
            
            // Seeking
            Bind(VirtualKey.Right, MediaAction.SeekForwardShort);
            Bind(VirtualKey.Left, MediaAction.SeekBackwardShort);
            Bind(VirtualKey.Right, KeyModifiers.Shift, MediaAction.SeekForwardMedium);
            Bind(VirtualKey.Left, KeyModifiers.Shift, MediaAction.SeekBackwardMedium);
            Bind(VirtualKey.Right, KeyModifiers.Control, MediaAction.SeekForwardLong);
            Bind(VirtualKey.Left, KeyModifiers.Control, MediaAction.SeekBackwardLong);
            Bind(VirtualKey.Home, MediaAction.SeekToStart);
            Bind(VirtualKey.End, MediaAction.SeekToEnd);
            
            // Volume
            Bind(VirtualKey.Up, MediaAction.VolumeUp);
            Bind(VirtualKey.Down, MediaAction.VolumeDown);
            Bind(VirtualKey.M, MediaAction.Mute);
            
            // Speed
            Bind(VirtualKey.OemCloseBrackets, MediaAction.SpeedUp);   // ]
            Bind(VirtualKey.OemOpenBrackets, MediaAction.SpeedDown);  // [
            Bind(VirtualKey.OemCloseBrackets, KeyModifiers.Shift, MediaAction.SpeedNormal);
            
            // Video
            Bind(VirtualKey.F, MediaAction.ToggleFullscreen);
            Bind(VirtualKey.A, MediaAction.CycleAspectRatio);
            Bind(VirtualKey.C, MediaAction.CycleCropRatio);
            Bind(VirtualKey.Z, MediaAction.CycleZoom);
            Bind(VirtualKey.S, KeyModifiers.Shift, MediaAction.SnapshotVideo);
            
            // Audio
            Bind(VirtualKey.B, MediaAction.CycleAudioTrack);
            Bind(VirtualKey.J, MediaAction.AudioDelayIncrease);
            Bind(VirtualKey.K, MediaAction.AudioDelayDecrease);
            
            // Subtitles
            Bind(VirtualKey.V, MediaAction.CycleSubtitleTrack);
            Bind(VirtualKey.V, KeyModifiers.Shift, MediaAction.ToggleSubtitles);
            Bind(VirtualKey.H, MediaAction.SubtitleDelayIncrease);
            Bind(VirtualKey.G, MediaAction.SubtitleDelayDecrease);
            Bind(VirtualKey.OemPlus, KeyModifiers.Control, MediaAction.SubtitleSizeIncrease);
            Bind(VirtualKey.OemMinus, KeyModifiers.Control, MediaAction.SubtitleSizeDecrease);
            
            // Navigation
            Bind(VirtualKey.PageUp, MediaAction.ChapterNext);
            Bind(VirtualKey.PageDown, MediaAction.ChapterPrevious);
            
            // Interface
            Bind(VirtualKey.O, MediaAction.ToggleOsd);
            Bind(VirtualKey.L, KeyModifiers.Control, MediaAction.TogglePlaylist);
            Bind(VirtualKey.I, KeyModifiers.Control | KeyModifiers.Shift, MediaAction.ToggleStatistics);
            Bind(VirtualKey.Q, KeyModifiers.Control, MediaAction.Quit);
            
            // Frame stepping
            Bind(VirtualKey.E, MediaAction.FrameNext);
            Bind(VirtualKey.E, KeyModifiers.Shift, MediaAction.FramePrevious);
            
            // A/B loop
            Bind(VirtualKey.L, MediaAction.LoopSetA);
            Bind(VirtualKey.L, KeyModifiers.Shift, MediaAction.LoopSetB);
            Bind(VirtualKey.L, KeyModifiers.Control, MediaAction.LoopClear);
        }
    }
    
    /// <summary>
    /// Get display string for an action's bindings.
    /// </summary>
    public string GetBindingString(MediaAction action)
    {
        var bindings = GetBindings(action);
        if (bindings.Count == 0)
            return "Not bound";
        return string.Join(" or ", bindings.Select(b => b.ToString()));
    }
}

/// <summary>
/// Default seek amounts.
/// </summary>
public static class SeekAmounts
{
    public static TimeSpan Short { get; set; } = TimeSpan.FromSeconds(5);
    public static TimeSpan Medium { get; set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan Long { get; set; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Default volume amounts.
/// </summary>
public static class VolumeAmounts
{
    public static int Step { get; set; } = 5; // Percent
}

/// <summary>
/// Default playback speed ratios.
/// </summary>
public static class SpeedRatios
{
    public static readonly double[] Presets = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
    public static double StepUp { get; set; } = 0.1;
    public static double StepDown { get; set; } = 0.1;
}

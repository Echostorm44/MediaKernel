// MediaKernel - Mouse Gesture Recognition System
// Implements pattern recognition for mouse movements (similar to VLC gestures.c)

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Input;

/// <summary>
/// Cardinal direction for gesture movements.
/// </summary>
public enum GestureDirection : byte
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4
}

/// <summary>
/// Predefined gesture patterns that trigger specific actions.
/// </summary>
public enum GesturePattern : ushort
{
    None = 0,
    
    // Single direction gestures
    Left = (ushort)GestureDirection.Left,
    Right = (ushort)GestureDirection.Right,
    Up = (ushort)GestureDirection.Up,
    Down = (ushort)GestureDirection.Down,
    
    // Two-direction gestures (direction1 | (direction2 << 4))
    LeftRight = (ushort)GestureDirection.Left | ((ushort)GestureDirection.Right << 4),
    RightLeft = (ushort)GestureDirection.Right | ((ushort)GestureDirection.Left << 4),
    UpDown = (ushort)GestureDirection.Up | ((ushort)GestureDirection.Down << 4),
    DownUp = (ushort)GestureDirection.Down | ((ushort)GestureDirection.Up << 4),
    LeftUp = (ushort)GestureDirection.Left | ((ushort)GestureDirection.Up << 4),
    LeftDown = (ushort)GestureDirection.Left | ((ushort)GestureDirection.Down << 4),
    RightUp = (ushort)GestureDirection.Right | ((ushort)GestureDirection.Up << 4),
    RightDown = (ushort)GestureDirection.Right | ((ushort)GestureDirection.Down << 4),
    UpLeft = (ushort)GestureDirection.Up | ((ushort)GestureDirection.Left << 4),
    UpRight = (ushort)GestureDirection.Up | ((ushort)GestureDirection.Right << 4),
    DownLeft = (ushort)GestureDirection.Down | ((ushort)GestureDirection.Left << 4),
    DownRight = (ushort)GestureDirection.Down | ((ushort)GestureDirection.Right << 4),
    
    // Three-direction gestures
    LeftUpRight = (ushort)GestureDirection.Left | ((ushort)GestureDirection.Up << 4) | ((ushort)GestureDirection.Right << 8),
    RightUpLeft = (ushort)GestureDirection.Right | ((ushort)GestureDirection.Up << 4) | ((ushort)GestureDirection.Left << 8),
    LeftDownRight = (ushort)GestureDirection.Left | ((ushort)GestureDirection.Down << 4) | ((ushort)GestureDirection.Right << 8),
    RightDownLeft = (ushort)GestureDirection.Right | ((ushort)GestureDirection.Down << 4) | ((ushort)GestureDirection.Left << 8),
    
    // Four-direction square gestures
    DownLeftUpRight = (ushort)GestureDirection.Down | ((ushort)GestureDirection.Left << 4) | ((ushort)GestureDirection.Up << 8) | ((ushort)GestureDirection.Right << 12),
    UpRightDownLeft = (ushort)GestureDirection.Up | ((ushort)GestureDirection.Right << 4) | ((ushort)GestureDirection.Down << 8) | ((ushort)GestureDirection.Left << 12)
}

/// <summary>
/// Actions triggered by gestures.
/// </summary>
public enum GestureAction
{
    None,
    
    // Playback control
    PlayPause,
    Stop,
    SeekBackward,
    SeekForward,
    PlaybackSlower,
    PlaybackFaster,
    
    // Playlist
    PreviousTrack,
    NextTrack,
    
    // Volume
    VolumeUp,
    VolumeDown,
    ToggleMute,
    
    // Video
    ToggleFullscreen,
    NextAudioTrack,
    NextSubtitleTrack,
    
    // Window
    Close
}

/// <summary>
/// Event args for recognized gestures.
/// </summary>
public sealed class GestureRecognizedEventArgs : EventArgs
{
    public GesturePattern Pattern { get; }
    public GestureAction Action { get; }
    public int DirectionCount { get; }
    
    public GestureRecognizedEventArgs(GesturePattern pattern, GestureAction action, int directionCount)
    {
        Pattern = pattern;
        Action = action;
        DirectionCount = directionCount;
    }
}

/// <summary>
/// Mouse gesture recognizer that detects directional patterns.
/// </summary>
public sealed class GestureRecognizer
{
    private const int MaxGestureDirections = 4;
    
    private int threshold = 30;
    private bool isTracking;
    private int lastX;
    private int lastY;
    private ushort pattern;
    private int directionCount;
    
    // Default gesture-to-action mappings
    private readonly Dictionary<GesturePattern, GestureAction> actionMappings = new()
    {
        // Single directions
        { GesturePattern.Left, GestureAction.SeekBackward },
        { GesturePattern.Right, GestureAction.SeekForward },
        { GesturePattern.Up, GestureAction.VolumeUp },
        { GesturePattern.Down, GestureAction.VolumeDown },
        
        // Two-direction combinations
        { GesturePattern.LeftRight, GestureAction.PlayPause },
        { GesturePattern.RightLeft, GestureAction.PlayPause },
        { GesturePattern.UpDown, GestureAction.ToggleMute },
        { GesturePattern.DownUp, GestureAction.ToggleMute },
        { GesturePattern.LeftUp, GestureAction.PlaybackSlower },
        { GesturePattern.RightUp, GestureAction.PlaybackFaster },
        { GesturePattern.LeftDown, GestureAction.PreviousTrack },
        { GesturePattern.RightDown, GestureAction.NextTrack },
        { GesturePattern.UpLeft, GestureAction.ToggleFullscreen },
        { GesturePattern.UpRight, GestureAction.NextAudioTrack },
        { GesturePattern.DownRight, GestureAction.NextSubtitleTrack },
        { GesturePattern.DownLeft, GestureAction.Close }
    };
    
    /// <summary>
    /// Event fired when a gesture is recognized.
    /// </summary>
    public event EventHandler<GestureRecognizedEventArgs>? GestureRecognized;
    
    /// <summary>
    /// Movement threshold in pixels before a direction is recognized.
    /// </summary>
    public int Threshold
    {
        get => threshold;
        set => threshold = Math.Max(10, Math.Min(100, value));
    }
    
    /// <summary>
    /// Whether gesture tracking is currently active.
    /// </summary>
    public bool IsTracking => isTracking;
    
    /// <summary>
    /// Current pattern being built (for debugging/visualization).
    /// </summary>
    public ushort CurrentPattern => pattern;
    
    /// <summary>
    /// Number of directions in current pattern.
    /// </summary>
    public int CurrentDirectionCount => directionCount;
    
    /// <summary>
    /// Gets the action mapping for a pattern.
    /// </summary>
    public GestureAction GetMappedAction(GesturePattern gesturePattern)
    {
        return actionMappings.TryGetValue(gesturePattern, out var action) ? action : GestureAction.None;
    }
    
    /// <summary>
    /// Sets a custom action mapping for a pattern.
    /// </summary>
    public void SetActionMapping(GesturePattern gesturePattern, GestureAction action)
    {
        actionMappings[gesturePattern] = action;
    }
    
    /// <summary>
    /// Removes an action mapping.
    /// </summary>
    public void RemoveActionMapping(GesturePattern gesturePattern)
    {
        actionMappings.Remove(gesturePattern);
    }
    
    /// <summary>
    /// Called when mouse button is pressed to start tracking.
    /// </summary>
    /// <param name="x">Mouse X coordinate</param>
    /// <param name="y">Mouse Y coordinate</param>
    public void StartTracking(int x, int y)
    {
        isTracking = true;
        lastX = x;
        lastY = y;
        pattern = 0;
        directionCount = 0;
    }
    
    /// <summary>
    /// Called when mouse moves while tracking.
    /// </summary>
    /// <param name="x">Current mouse X coordinate</param>
    /// <param name="y">Current mouse Y coordinate</param>
    public void UpdatePosition(int x, int y)
    {
        if (!isTracking || directionCount >= MaxGestureDirections)
            return;
        
        int deltaX = x - lastX;
        int deltaY = y - lastY;
        
        // Normalize by threshold
        int normalizedDeltaX = deltaX / threshold;
        int normalizedDeltaY = deltaY / threshold;
        
        GestureDirection direction = GestureDirection.None;
        
        // Determine primary direction (larger delta wins)
        if (Math.Abs(normalizedDeltaX) > Math.Abs(normalizedDeltaY))
        {
            if (normalizedDeltaX > 0)
                direction = GestureDirection.Right;
            else if (normalizedDeltaX < 0)
                direction = GestureDirection.Left;
        }
        else
        {
            if (normalizedDeltaY > 0)
                direction = GestureDirection.Down;
            else if (normalizedDeltaY < 0)
                direction = GestureDirection.Up;
        }
        
        if (direction != GestureDirection.None)
        {
            // Check if this is a new direction (different from last)
            GestureDirection lastDirection = GetDirectionAt(directionCount - 1);
            
            if (direction != lastDirection)
            {
                // Add direction to pattern
                pattern = (ushort)(pattern | ((ushort)direction << (directionCount * 4)));
                directionCount++;
                
                // Update last position
                lastX = x;
                lastY = y;
            }
        }
    }
    
    /// <summary>
    /// Called when mouse button is released to finish tracking and recognize gesture.
    /// </summary>
    /// <returns>The recognized gesture pattern, or None if no gesture.</returns>
    public GesturePattern StopTracking()
    {
        if (!isTracking)
            return GesturePattern.None;
        
        isTracking = false;
        
        if (directionCount == 0)
            return GesturePattern.None;
        
        var recognizedPattern = (GesturePattern)pattern;
        var action = GetMappedAction(recognizedPattern);
        
        // Fire event
        GestureRecognized?.Invoke(this, new GestureRecognizedEventArgs(
            recognizedPattern, action, directionCount));
        
        return recognizedPattern;
    }
    
    /// <summary>
    /// Cancels current tracking without recognizing a gesture.
    /// </summary>
    public void CancelTracking()
    {
        isTracking = false;
        pattern = 0;
        directionCount = 0;
    }
    
    /// <summary>
    /// Gets the direction at a specific index in the current pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GestureDirection GetDirectionAt(int index)
    {
        if (index < 0 || index >= directionCount)
            return GestureDirection.None;
        
        return (GestureDirection)((pattern >> (index * 4)) & 0xF);
    }
    
    /// <summary>
    /// Builds a pattern from directions.
    /// </summary>
    public static GesturePattern BuildPattern(params GestureDirection[] directions)
    {
        if (directions.Length == 0 || directions.Length > MaxGestureDirections)
            return GesturePattern.None;
        
        ushort result = 0;
        for (int i = 0; i < directions.Length; i++)
        {
            result |= (ushort)((ushort)directions[i] << (i * 4));
        }
        return (GesturePattern)result;
    }
    
    /// <summary>
    /// Parses a pattern into individual directions.
    /// </summary>
    public static GestureDirection[] ParsePattern(GesturePattern gesturePattern)
    {
        var directions = new List<GestureDirection>(MaxGestureDirections);
        ushort value = (ushort)gesturePattern;
        
        for (int i = 0; i < MaxGestureDirections; i++)
        {
            var direction = (GestureDirection)((value >> (i * 4)) & 0xF);
            if (direction == GestureDirection.None)
                break;
            directions.Add(direction);
        }
        
        return directions.ToArray();
    }
    
    /// <summary>
    /// Gets a human-readable description of a pattern.
    /// </summary>
    public static string DescribePattern(GesturePattern gesturePattern)
    {
        var directions = ParsePattern(gesturePattern);
        if (directions.Length == 0)
            return "None";
        
        return string.Join(" → ", directions.Select(d => d.ToString()));
    }
}

/// <summary>
/// Touch gesture recognizer for touchscreens and touchpads.
/// </summary>
public sealed class TouchGestureRecognizer
{
    private const float SwipeThreshold = 50f;
    private const float PinchThreshold = 0.1f;
    
    private float startX;
    private float startY;
    private float startDistance;
    private bool isTracking;
    private int touchCount;
    
    // Two-finger touch points
    private float touch1X, touch1Y;
    private float touch2X, touch2Y;
    
    /// <summary>
    /// Event fired when a swipe gesture is detected.
    /// </summary>
    public event Action<GestureDirection, float>? SwipeDetected;
    
    /// <summary>
    /// Event fired when a pinch gesture is detected (zoom in/out).
    /// </summary>
    public event Action<float>? PinchDetected;
    
    /// <summary>
    /// Event fired when a double-tap is detected.
    /// </summary>
    public event Action? DoubleTapDetected;
    
    /// <summary>
    /// Minimum swipe distance in pixels.
    /// </summary>
    public float SwipeMinimumDistance { get; set; } = SwipeThreshold;
    
    /// <summary>
    /// Whether tracking is currently active.
    /// </summary>
    public bool IsTracking => isTracking;
    
    /// <summary>
    /// Current number of touch points.
    /// </summary>
    public int TouchCount => touchCount;
    
    /// <summary>
    /// Called when first touch begins.
    /// </summary>
    public void TouchStart(float x, float y)
    {
        startX = x;
        startY = y;
        touchCount = 1;
        isTracking = true;
    }
    
    /// <summary>
    /// Called when second touch begins (for pinch gestures).
    /// </summary>
    public void SecondTouchStart(float x1, float y1, float x2, float y2)
    {
        touch1X = x1;
        touch1Y = y1;
        touch2X = x2;
        touch2Y = y2;
        startDistance = CalculateDistance(x1, y1, x2, y2);
        touchCount = 2;
    }
    
    /// <summary>
    /// Called when touch moves.
    /// </summary>
    public void TouchMove(float x, float y)
    {
        if (!isTracking || touchCount != 1)
            return;
        
        // Check for swipe
        float deltaX = x - startX;
        float deltaY = y - startY;
        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        
        if (distance >= SwipeMinimumDistance)
        {
            GestureDirection direction;
            if (MathF.Abs(deltaX) > MathF.Abs(deltaY))
            {
                direction = deltaX > 0 ? GestureDirection.Right : GestureDirection.Left;
            }
            else
            {
                direction = deltaY > 0 ? GestureDirection.Down : GestureDirection.Up;
            }
            
            SwipeDetected?.Invoke(direction, distance);
            
            // Reset start position after swipe
            startX = x;
            startY = y;
        }
    }
    
    /// <summary>
    /// Called when two-finger touch moves (for pinch).
    /// </summary>
    public void TwoFingerMove(float x1, float y1, float x2, float y2)
    {
        if (!isTracking || touchCount != 2 || startDistance < 0.001f)
            return;
        
        float currentDistance = CalculateDistance(x1, y1, x2, y2);
        float scale = currentDistance / startDistance;
        
        if (MathF.Abs(scale - 1.0f) >= PinchThreshold)
        {
            PinchDetected?.Invoke(scale);
            
            // Update reference
            touch1X = x1;
            touch1Y = y1;
            touch2X = x2;
            touch2Y = y2;
            startDistance = currentDistance;
        }
    }
    
    /// <summary>
    /// Called when touch ends.
    /// </summary>
    public void TouchEnd()
    {
        touchCount = Math.Max(0, touchCount - 1);
        if (touchCount == 0)
        {
            isTracking = false;
        }
    }
    
    /// <summary>
    /// Called when a double-tap is detected (by the input system).
    /// </summary>
    public void OnDoubleTap()
    {
        DoubleTapDetected?.Invoke();
    }
    
    /// <summary>
    /// Resets the recognizer state.
    /// </summary>
    public void Reset()
    {
        isTracking = false;
        touchCount = 0;
        startDistance = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateDistance(float x1, float y1, float x2, float y2)
    {
        float deltaX = x2 - x1;
        float deltaY = y2 - y1;
        return MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }
}

/// <summary>
/// Combined gesture handler that integrates mouse and touch gestures with media player controls.
/// </summary>
public sealed class MediaGestureHandler
{
    private readonly GestureRecognizer mouseRecognizer = new();
    private readonly TouchGestureRecognizer touchRecognizer = new();
    
    // Event callbacks for media player integration
    public Action? OnPlayPause { get; set; }
    public Action? OnStop { get; set; }
    public Action<TimeSpan>? OnSeek { get; set; }
    public Action<float>? OnPlaybackRateChange { get; set; }
    public Action? OnPreviousTrack { get; set; }
    public Action? OnNextTrack { get; set; }
    public Action<float>? OnVolumeChange { get; set; }
    public Action? OnToggleMute { get; set; }
    public Action? OnToggleFullscreen { get; set; }
    public Action? OnNextAudioTrack { get; set; }
    public Action? OnNextSubtitleTrack { get; set; }
    public Action? OnClose { get; set; }
    
    // Configurable values
    public TimeSpan SeekAmount { get; set; } = TimeSpan.FromSeconds(10);
    public float VolumeStep { get; set; } = 0.05f;
    public float PlaybackRateStep { get; set; } = 0.25f;
    
    public MediaGestureHandler()
    {
        mouseRecognizer.GestureRecognized += OnMouseGestureRecognized;
        touchRecognizer.SwipeDetected += OnSwipeDetected;
        touchRecognizer.PinchDetected += OnPinchDetected;
        touchRecognizer.DoubleTapDetected += OnDoubleTap;
    }
    
    /// <summary>
    /// Gets the mouse gesture recognizer for direct access.
    /// </summary>
    public GestureRecognizer MouseRecognizer => mouseRecognizer;
    
    /// <summary>
    /// Gets the touch gesture recognizer for direct access.
    /// </summary>
    public TouchGestureRecognizer TouchRecognizer => touchRecognizer;
    
    private void OnMouseGestureRecognized(object? sender, GestureRecognizedEventArgs e)
    {
        ExecuteAction(e.Action);
    }
    
    private void OnSwipeDetected(GestureDirection direction, float distance)
    {
        switch (direction)
        {
            case GestureDirection.Left:
                OnSeek?.Invoke(-SeekAmount);
                break;
            case GestureDirection.Right:
                OnSeek?.Invoke(SeekAmount);
                break;
            case GestureDirection.Up:
                OnVolumeChange?.Invoke(VolumeStep);
                break;
            case GestureDirection.Down:
                OnVolumeChange?.Invoke(-VolumeStep);
                break;
        }
    }
    
    private void OnPinchDetected(float scale)
    {
        // Pinch-to-zoom could control playback rate or zoom
        if (scale > 1.0f)
        {
            OnPlaybackRateChange?.Invoke(PlaybackRateStep);
        }
        else if (scale < 1.0f)
        {
            OnPlaybackRateChange?.Invoke(-PlaybackRateStep);
        }
    }
    
    private void OnDoubleTap()
    {
        OnToggleFullscreen?.Invoke();
    }
    
    private void ExecuteAction(GestureAction action)
    {
        switch (action)
        {
            case GestureAction.PlayPause:
                OnPlayPause?.Invoke();
                break;
            case GestureAction.Stop:
                OnStop?.Invoke();
                break;
            case GestureAction.SeekBackward:
                OnSeek?.Invoke(-SeekAmount);
                break;
            case GestureAction.SeekForward:
                OnSeek?.Invoke(SeekAmount);
                break;
            case GestureAction.PlaybackSlower:
                OnPlaybackRateChange?.Invoke(-PlaybackRateStep);
                break;
            case GestureAction.PlaybackFaster:
                OnPlaybackRateChange?.Invoke(PlaybackRateStep);
                break;
            case GestureAction.PreviousTrack:
                OnPreviousTrack?.Invoke();
                break;
            case GestureAction.NextTrack:
                OnNextTrack?.Invoke();
                break;
            case GestureAction.VolumeUp:
                OnVolumeChange?.Invoke(VolumeStep);
                break;
            case GestureAction.VolumeDown:
                OnVolumeChange?.Invoke(-VolumeStep);
                break;
            case GestureAction.ToggleMute:
                OnToggleMute?.Invoke();
                break;
            case GestureAction.ToggleFullscreen:
                OnToggleFullscreen?.Invoke();
                break;
            case GestureAction.NextAudioTrack:
                OnNextAudioTrack?.Invoke();
                break;
            case GestureAction.NextSubtitleTrack:
                OnNextSubtitleTrack?.Invoke();
                break;
            case GestureAction.Close:
                OnClose?.Invoke();
                break;
        }
    }
}

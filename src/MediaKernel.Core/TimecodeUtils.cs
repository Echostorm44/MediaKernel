// MediaKernel - SMPTE Timecode Utilities
// Frame-accurate timecodes for professional video applications

using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace MediaKernel.Core;

/// <summary>
/// Standard frame rates for SMPTE timecodes.
/// </summary>
public enum TimecodeFrameRate
{
    /// <summary>24 fps (film).</summary>
    Film24 = 24,
    
    /// <summary>23.976 fps (NTSC film pulldown).</summary>
    Film23976 = 23,
    
    /// <summary>25 fps (PAL).</summary>
    Pal25 = 25,
    
    /// <summary>29.97 fps drop-frame (NTSC).</summary>
    NtscDropFrame = 30,
    
    /// <summary>29.97 fps non-drop-frame (NTSC).</summary>
    NtscNonDropFrame = 29,
    
    /// <summary>30 fps.</summary>
    Fps30 = 31,
    
    /// <summary>50 fps.</summary>
    Fps50 = 50,
    
    /// <summary>59.94 fps drop-frame.</summary>
    Fps5994DropFrame = 60,
    
    /// <summary>59.94 fps non-drop-frame.</summary>
    Fps5994NonDropFrame = 59,
    
    /// <summary>60 fps.</summary>
    Fps60 = 61
}

/// <summary>
/// SMPTE timecode representation (HH:MM:SS:FF or HH:MM:SS;FF for drop-frame).
/// </summary>
public readonly struct Timecode : IEquatable<Timecode>, IComparable<Timecode>
{
    /// <summary>Hours (0-23).</summary>
    public int Hours { get; init; }
    
    /// <summary>Minutes (0-59).</summary>
    public int Minutes { get; init; }
    
    /// <summary>Seconds (0-59).</summary>
    public int Seconds { get; init; }
    
    /// <summary>Frames (0 to frame rate - 1).</summary>
    public int Frames { get; init; }
    
    /// <summary>Frame rate of this timecode.</summary>
    public TimecodeFrameRate FrameRate { get; init; }
    
    /// <summary>Whether this is a drop-frame timecode.</summary>
    public bool IsDropFrame => FrameRate is TimecodeFrameRate.NtscDropFrame or TimecodeFrameRate.Fps5994DropFrame;
    
    /// <summary>Total frame count from start.</summary>
    public long TotalFrames => TimecodeConverter.ToFrameCount(this);
    
    /// <summary>Zero timecode at 24 fps.</summary>
    public static readonly Timecode Zero = new() { FrameRate = TimecodeFrameRate.Film24 };
    
    public override string ToString()
    {
        char separator = IsDropFrame ? ';' : ':';
        return $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{separator}{Frames:D2}";
    }
    
    public bool Equals(Timecode other)
    {
        return Hours == other.Hours && Minutes == other.Minutes &&
               Seconds == other.Seconds && Frames == other.Frames &&
               FrameRate == other.FrameRate;
    }
    
    public override bool Equals(object? obj) => obj is Timecode other && Equals(other);
    
    public override int GetHashCode() => HashCode.Combine(Hours, Minutes, Seconds, Frames, FrameRate);
    
    public int CompareTo(Timecode other)
    {
        // Convert both to frame count for comparison
        return TotalFrames.CompareTo(other.TotalFrames);
    }
    
    public static bool operator ==(Timecode left, Timecode right) => left.Equals(right);
    public static bool operator !=(Timecode left, Timecode right) => !left.Equals(right);
    public static bool operator <(Timecode left, Timecode right) => left.CompareTo(right) < 0;
    public static bool operator >(Timecode left, Timecode right) => left.CompareTo(right) > 0;
    public static bool operator <=(Timecode left, Timecode right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Timecode left, Timecode right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Converts between timecodes, frame counts, and time values.
/// </summary>
public static class TimecodeConverter
{
    /// <summary>
    /// Gets the actual frames per second for a frame rate.
    /// </summary>
    public static double GetFrameRateValue(TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Film24 => 24.0,
        TimecodeFrameRate.Film23976 => 24000.0 / 1001.0,
        TimecodeFrameRate.Pal25 => 25.0,
        TimecodeFrameRate.NtscDropFrame => 30000.0 / 1001.0,
        TimecodeFrameRate.NtscNonDropFrame => 30000.0 / 1001.0,
        TimecodeFrameRate.Fps30 => 30.0,
        TimecodeFrameRate.Fps50 => 50.0,
        TimecodeFrameRate.Fps5994DropFrame => 60000.0 / 1001.0,
        TimecodeFrameRate.Fps5994NonDropFrame => 60000.0 / 1001.0,
        TimecodeFrameRate.Fps60 => 60.0,
        _ => 24.0
    };
    
    /// <summary>
    /// Gets the nominal frame rate (for timecode calculation).
    /// </summary>
    public static int GetNominalFrameRate(TimecodeFrameRate rate) => rate switch
    {
        TimecodeFrameRate.Film24 => 24,
        TimecodeFrameRate.Film23976 => 24,
        TimecodeFrameRate.Pal25 => 25,
        TimecodeFrameRate.NtscDropFrame => 30,
        TimecodeFrameRate.NtscNonDropFrame => 30,
        TimecodeFrameRate.Fps30 => 30,
        TimecodeFrameRate.Fps50 => 50,
        TimecodeFrameRate.Fps5994DropFrame => 60,
        TimecodeFrameRate.Fps5994NonDropFrame => 60,
        TimecodeFrameRate.Fps60 => 60,
        _ => 24
    };
    
    /// <summary>
    /// Determines if a frame rate uses drop-frame.
    /// </summary>
    public static bool IsDropFrame(TimecodeFrameRate rate) =>
        rate is TimecodeFrameRate.NtscDropFrame or TimecodeFrameRate.Fps5994DropFrame;
    
    /// <summary>
    /// Converts a timecode to total frame count.
    /// </summary>
    public static long ToFrameCount(Timecode timecode)
    {
        int fps = GetNominalFrameRate(timecode.FrameRate);
        
        if (IsDropFrame(timecode.FrameRate))
        {
            // Drop-frame: skip 2 frame numbers at the start of each minute, except every 10th minute
            // For 29.97: drop 2 frames, for 59.94: drop 4 frames
            int dropFrames = fps == 30 ? 2 : 4;
            
            long totalMinutes = timecode.Hours * 60L + timecode.Minutes;
            long dropFramesTotal = totalMinutes * dropFrames - (totalMinutes / 10) * dropFrames;
            
            return timecode.Hours * 3600L * fps +
                   timecode.Minutes * 60L * fps +
                   timecode.Seconds * fps +
                   timecode.Frames -
                   dropFramesTotal;
        }
        else
        {
            return timecode.Hours * 3600L * fps +
                   timecode.Minutes * 60L * fps +
                   timecode.Seconds * fps +
                   timecode.Frames;
        }
    }
    
    /// <summary>
    /// Converts a frame count to timecode.
    /// </summary>
    public static Timecode FromFrameCount(long frameCount, TimecodeFrameRate frameRate)
    {
        int fps = GetNominalFrameRate(frameRate);
        
        if (frameCount < 0)
            frameCount = 0;
        
        if (IsDropFrame(frameRate))
        {
            // Reverse drop-frame calculation
            int dropFrames = fps == 30 ? 2 : 4;
            int framesPerTenMinutes = fps * 60 * 10 - dropFrames * 9;
            int framesPerMinute = fps * 60 - dropFrames;
            
            long tenMinuteBlocks = frameCount / framesPerTenMinutes;
            long remainder = frameCount % framesPerTenMinutes;
            
            long minutes;
            if (remainder < fps * 60) // First minute of 10-minute block (no drop)
            {
                minutes = tenMinuteBlocks * 10;
            }
            else
            {
                // Remaining minutes with drops
                minutes = tenMinuteBlocks * 10 + 1 + (remainder - fps * 60) / framesPerMinute;
            }
            
            // Recalculate actual frame count for the minute
            long framesIntoMinute;
            if (minutes % 10 == 0)
            {
                long framesBeforeThisMinute = (minutes / 10) * framesPerTenMinutes;
                if (minutes % 10 != 0)
                    framesBeforeThisMinute += (minutes % 10) * framesPerMinute;
                framesIntoMinute = frameCount - framesBeforeThisMinute;
            }
            else
            {
                long fullTenMinBlocks = minutes / 10;
                long extraMinutes = minutes % 10;
                long framesBeforeThisMinute = fullTenMinBlocks * framesPerTenMinutes + fps * 60 + (extraMinutes - 1) * framesPerMinute;
                framesIntoMinute = frameCount - framesBeforeThisMinute;
            }
            
            int hours = (int)(minutes / 60);
            int mins = (int)(minutes % 60);
            int secs = (int)(framesIntoMinute / fps);
            int frames = (int)(framesIntoMinute % fps);
            
            // Apply drop frame offset for display (add back dropped frames for display)
            if (secs >= 60)
            {
                secs -= 60;
                mins++;
                if (mins >= 60)
                {
                    mins -= 60;
                    hours++;
                }
            }
            
            return new Timecode
            {
                Hours = hours % 24,
                Minutes = mins,
                Seconds = secs,
                Frames = frames,
                FrameRate = frameRate
            };
        }
        else
        {
            long totalSeconds = frameCount / fps;
            int frames = (int)(frameCount % fps);
            int seconds = (int)(totalSeconds % 60);
            int minutes = (int)((totalSeconds / 60) % 60);
            int hours = (int)(totalSeconds / 3600);
            
            return new Timecode
            {
                Hours = hours % 24,
                Minutes = minutes,
                Seconds = seconds,
                Frames = frames,
                FrameRate = frameRate
            };
        }
    }
    
    /// <summary>
    /// Converts a TimeSpan to timecode.
    /// </summary>
    public static Timecode FromTimeSpan(TimeSpan time, TimecodeFrameRate frameRate)
    {
        double fps = GetFrameRateValue(frameRate);
        long frameCount = (long)(time.TotalSeconds * fps + 0.5);
        return FromFrameCount(frameCount, frameRate);
    }
    
    /// <summary>
    /// Converts a timecode to TimeSpan.
    /// </summary>
    public static TimeSpan ToTimeSpan(Timecode timecode)
    {
        double fps = GetFrameRateValue(timecode.FrameRate);
        long frames = ToFrameCount(timecode);
        return TimeSpan.FromSeconds(frames / fps);
    }
    
    /// <summary>
    /// Parses a timecode string (HH:MM:SS:FF or HH:MM:SS;FF).
    /// </summary>
    public static bool TryParse(string text, TimecodeFrameRate frameRate, out Timecode timecode)
    {
        timecode = default;
        
        if (string.IsNullOrWhiteSpace(text))
            return false;
        
        // Normalize separators
        text = text.Trim();
        bool isDropFrame = text.Contains(';');
        
        // Replace semicolons with colons for parsing
        string normalized = text.Replace(';', ':');
        var parts = normalized.Split(':');
        
        if (parts.Length != 4)
            return false;
        
        if (!int.TryParse(parts[0], out int hours) ||
            !int.TryParse(parts[1], out int minutes) ||
            !int.TryParse(parts[2], out int seconds) ||
            !int.TryParse(parts[3], out int frames))
            return false;
        
        if (hours < 0 || hours > 23 ||
            minutes < 0 || minutes > 59 ||
            seconds < 0 || seconds > 59 ||
            frames < 0 || frames >= GetNominalFrameRate(frameRate))
            return false;
        
        timecode = new Timecode
        {
            Hours = hours,
            Minutes = minutes,
            Seconds = seconds,
            Frames = frames,
            FrameRate = frameRate
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses a timecode string (HH:MM:SS:FF or HH:MM:SS;FF).
    /// </summary>
    public static Timecode Parse(string text, TimecodeFrameRate frameRate)
    {
        if (!TryParse(text, frameRate, out var timecode))
            throw new FormatException($"Invalid timecode format: {text}");
        return timecode;
    }
    
    /// <summary>
    /// Adds two timecodes (uses the frame rate of the first timecode).
    /// </summary>
    public static Timecode Add(Timecode a, Timecode b)
    {
        long totalFrames = ToFrameCount(a) + ToFrameCount(b);
        return FromFrameCount(totalFrames, a.FrameRate);
    }
    
    /// <summary>
    /// Subtracts timecode b from a (uses the frame rate of the first timecode).
    /// </summary>
    public static Timecode Subtract(Timecode a, Timecode b)
    {
        long totalFrames = ToFrameCount(a) - ToFrameCount(b);
        if (totalFrames < 0) totalFrames = 0;
        return FromFrameCount(totalFrames, a.FrameRate);
    }
    
    /// <summary>
    /// Adds frames to a timecode.
    /// </summary>
    public static Timecode AddFrames(Timecode timecode, long frames)
    {
        long total = ToFrameCount(timecode) + frames;
        if (total < 0) total = 0;
        return FromFrameCount(total, timecode.FrameRate);
    }
}

/// <summary>
/// Generator for sequential timecodes at a fixed frame rate.
/// </summary>
public sealed class TimecodeGenerator
{
    private long currentFrame;
    private readonly TimecodeFrameRate frameRate;
    private readonly Timecode startTimecode;
    
    /// <summary>Current frame number.</summary>
    public long CurrentFrame => currentFrame;
    
    /// <summary>Current timecode.</summary>
    public Timecode Current => TimecodeConverter.FromFrameCount(currentFrame, frameRate);
    
    /// <summary>Frame rate.</summary>
    public TimecodeFrameRate FrameRate => frameRate;
    
    public TimecodeGenerator(TimecodeFrameRate frameRate, Timecode? startTimecode = null)
    {
        this.frameRate = frameRate;
        this.startTimecode = startTimecode ?? Timecode.Zero;
        this.currentFrame = TimecodeConverter.ToFrameCount(this.startTimecode);
    }
    
    /// <summary>
    /// Advances to the next frame.
    /// </summary>
    public Timecode Next()
    {
        currentFrame++;
        return Current;
    }
    
    /// <summary>
    /// Resets to the start timecode.
    /// </summary>
    public void Reset()
    {
        currentFrame = TimecodeConverter.ToFrameCount(startTimecode);
    }
    
    /// <summary>
    /// Sets the current frame.
    /// </summary>
    public void SetFrame(long frame)
    {
        currentFrame = Math.Max(0, frame);
    }
    
    /// <summary>
    /// Sets the current timecode.
    /// </summary>
    public void SetTimecode(Timecode timecode)
    {
        currentFrame = TimecodeConverter.ToFrameCount(timecode);
    }
}

/// <summary>
/// Frame rate converter for timecodes.
/// </summary>
public static class TimecodeFrameRateConverter
{
    /// <summary>
    /// Converts a timecode to a different frame rate.
    /// </summary>
    public static Timecode ConvertFrameRate(Timecode source, TimecodeFrameRate targetRate)
    {
        // Convert to time, then to target frame rate
        var time = TimecodeConverter.ToTimeSpan(source);
        return TimecodeConverter.FromTimeSpan(time, targetRate);
    }
    
    /// <summary>
    /// Gets the closest standard frame rate for a given value.
    /// </summary>
    public static TimecodeFrameRate GetClosestFrameRate(double fps)
    {
        // Use tolerance-based matching for precise rates
        if (Math.Abs(fps - 23.976) < 0.01) return TimecodeFrameRate.Film23976;
        if (Math.Abs(fps - 24.0) < 0.01) return TimecodeFrameRate.Film24;
        if (Math.Abs(fps - 25.0) < 0.01) return TimecodeFrameRate.Pal25;
        if (Math.Abs(fps - 29.97) < 0.01) return TimecodeFrameRate.NtscNonDropFrame;
        if (Math.Abs(fps - 30.0) < 0.01) return TimecodeFrameRate.Fps30;
        if (Math.Abs(fps - 50.0) < 0.01) return TimecodeFrameRate.Fps50;
        if (Math.Abs(fps - 59.94) < 0.01) return TimecodeFrameRate.Fps5994NonDropFrame;
        if (Math.Abs(fps - 60.0) < 0.01) return TimecodeFrameRate.Fps60;
        
        // Fallback range matching
        if (fps <= 23.5) return TimecodeFrameRate.Film23976;
        if (fps <= 24.5) return TimecodeFrameRate.Film24;
        if (fps <= 27.5) return TimecodeFrameRate.Pal25;
        if (fps <= 29.5) return TimecodeFrameRate.NtscNonDropFrame;
        if (fps <= 32) return TimecodeFrameRate.Fps30;
        if (fps <= 55) return TimecodeFrameRate.Fps50;
        if (fps <= 59.5) return TimecodeFrameRate.Fps5994NonDropFrame;
        return TimecodeFrameRate.Fps60;
    }
}

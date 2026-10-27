// Static metadata and helpers for AudioSampleFormat.
// Analogous to FFmpeg's av_get_bytes_per_sample / av_sample_fmt_is_planar.

using MediaKernel.Core.Audio;

namespace MediaKernel.Core.AudioResampling;

/// <summary>
/// Static helpers for <see cref="AudioSampleFormat"/> — bytes per sample,
/// packed↔planar mapping, and interleave/deinterleave support.
/// </summary>
public static class SampleFormatInfo
{
    /// <summary>Bytes per sample (one channel, one sample).</summary>
    public static int BytesPerSample(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.UInt8 or AudioSampleFormat.UInt8Planar => 1,
        AudioSampleFormat.Int16 or AudioSampleFormat.Int16Planar => 2,
        AudioSampleFormat.Int24 => 3,
        AudioSampleFormat.Int24In32 => 4,
        AudioSampleFormat.Int32 or AudioSampleFormat.Int32Planar => 4,
        AudioSampleFormat.Float32 or AudioSampleFormat.Float32Planar => 4,
        AudioSampleFormat.Float64 or AudioSampleFormat.Float64Planar => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>True if the format stores each channel in a separate plane.</summary>
    public static bool IsPlanar(AudioSampleFormat format) => format is
        AudioSampleFormat.UInt8Planar or AudioSampleFormat.Int16Planar or
        AudioSampleFormat.Int32Planar or AudioSampleFormat.Float32Planar or
        AudioSampleFormat.Float64Planar;

    /// <summary>True if sample values are floating-point.</summary>
    public static bool IsFloat(AudioSampleFormat format) => format is
        AudioSampleFormat.Float32 or AudioSampleFormat.Float64 or
        AudioSampleFormat.Float32Planar or AudioSampleFormat.Float64Planar;

    /// <summary>Returns the packed (interleaved) equivalent of a planar format, or the format itself.</summary>
    public static AudioSampleFormat ToPacked(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.UInt8Planar => AudioSampleFormat.UInt8,
        AudioSampleFormat.Int16Planar => AudioSampleFormat.Int16,
        AudioSampleFormat.Int32Planar => AudioSampleFormat.Int32,
        AudioSampleFormat.Float32Planar => AudioSampleFormat.Float32,
        AudioSampleFormat.Float64Planar => AudioSampleFormat.Float64,
        _ => format
    };

    /// <summary>Returns the planar equivalent of a packed format, or the format itself.</summary>
    public static AudioSampleFormat ToPlanar(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.UInt8 => AudioSampleFormat.UInt8Planar,
        AudioSampleFormat.Int16 => AudioSampleFormat.Int16Planar,
        AudioSampleFormat.Int32 => AudioSampleFormat.Int32Planar,
        AudioSampleFormat.Float32 => AudioSampleFormat.Float32Planar,
        AudioSampleFormat.Float64 => AudioSampleFormat.Float64Planar,
        _ => format
    };
}

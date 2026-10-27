using System;

namespace MediaKernel.Core.Audio
{
    /// <summary>
    /// Supported audio sample formats.
    /// Packed (interleaved) formats store all channels in one buffer: [L0 R0 L1 R1 ...].
    /// Planar formats store each channel separately: [L0 L1 L2 ...], [R0 R1 R2 ...].
    /// </summary>
    public enum AudioSampleFormat
    {
        /// <summary>8-bit unsigned PCM (interleaved).</summary>
        UInt8,
        
        /// <summary>16-bit signed PCM (interleaved, most common).</summary>
        Int16,
        
        /// <summary>24-bit signed PCM packed in 3 bytes (interleaved).</summary>
        Int24,
        
        /// <summary>24-bit signed PCM stored in 32-bit container (interleaved).</summary>
        Int24In32,
        
        /// <summary>32-bit signed PCM (interleaved).</summary>
        Int32,
        
        /// <summary>32-bit floating-point (interleaved).</summary>
        Float32,
        
        /// <summary>64-bit floating-point (interleaved).</summary>
        Float64,

        // ── Planar formats (separate buffer per channel) ─────────────

        /// <summary>8-bit unsigned PCM (planar).</summary>
        UInt8Planar,

        /// <summary>16-bit signed PCM (planar).</summary>
        Int16Planar,

        /// <summary>32-bit signed PCM (planar).</summary>
        Int32Planar,

        /// <summary>32-bit floating-point (planar). Most codecs decode to this.</summary>
        Float32Planar,

        /// <summary>64-bit floating-point (planar).</summary>
        Float64Planar
    }

    /// <summary>
    /// Standard audio channel layouts.
    /// </summary>
    public enum AudioChannelLayout
    {
        /// <summary>Single channel.</summary>
        Mono = 1,
        
        /// <summary>Left and right channels.</summary>
        Stereo = 2,
        
        /// <summary>2.1 (stereo + LFE).</summary>
        Stereo21 = 3,
        
        /// <summary>Quadraphonic (FL, FR, BL, BR).</summary>
        Quad = 4,
        
        /// <summary>5.0 (FL, FR, FC, BL, BR).</summary>
        Surround50 = 5,
        
        /// <summary>5.1 (FL, FR, FC, LFE, BL, BR).</summary>
        Surround51 = 6,
        
        /// <summary>7.1 (FL, FR, FC, LFE, BL, BR, SL, SR).</summary>
        Surround71 = 8
    }

    /// <summary>
    /// Audio format specification.
    /// </summary>
    public readonly struct AudioFormat
    {
        /// <summary>Sample rate in Hz (e.g., 44100, 48000, 96000).</summary>
        public readonly int SampleRate;
        
        /// <summary>Number of audio channels.</summary>
        public readonly int ChannelCount;
        
        /// <summary>Sample format.</summary>
        public readonly AudioSampleFormat SampleFormat;
        
        /// <summary>Channel layout for multi-channel audio.</summary>
        public readonly AudioChannelLayout ChannelLayout;

        public AudioFormat(
            int sampleRate,
            int channelCount,
            AudioSampleFormat sampleFormat,
            AudioChannelLayout channelLayout = AudioChannelLayout.Stereo)
        {
            SampleRate = sampleRate;
            ChannelCount = channelCount;
            SampleFormat = sampleFormat;
            ChannelLayout = channelLayout;
        }

        /// <summary>Bytes per sample (per channel).</summary>
        public int BytesPerSample => SampleFormat switch
        {
            AudioSampleFormat.UInt8 or AudioSampleFormat.UInt8Planar => 1,
            AudioSampleFormat.Int16 or AudioSampleFormat.Int16Planar => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int24In32 => 4,
            AudioSampleFormat.Int32 or AudioSampleFormat.Int32Planar => 4,
            AudioSampleFormat.Float32 or AudioSampleFormat.Float32Planar => 4,
            AudioSampleFormat.Float64 or AudioSampleFormat.Float64Planar => 8,
            _ => 2
        };

        /// <summary>True if this format stores channels in separate planes.</summary>
        public bool IsPlanar => SampleFormat is AudioSampleFormat.UInt8Planar
            or AudioSampleFormat.Int16Planar or AudioSampleFormat.Int32Planar
            or AudioSampleFormat.Float32Planar or AudioSampleFormat.Float64Planar;

        /// <summary>True if sample values are floating-point.</summary>
        public bool IsFloat => SampleFormat is AudioSampleFormat.Float32 or AudioSampleFormat.Float64
            or AudioSampleFormat.Float32Planar or AudioSampleFormat.Float64Planar;

        /// <summary>Bytes per frame (all channels).</summary>
        public int BytesPerFrame => BytesPerSample * ChannelCount;

        /// <summary>Bytes per second of audio.</summary>
        public int BytesPerSecond => BytesPerFrame * SampleRate;

        /// <summary>Common CD-quality format (44.1kHz stereo 16-bit).</summary>
        public static AudioFormat CdQuality => new(44100, 2, AudioSampleFormat.Int16, AudioChannelLayout.Stereo);

        /// <summary>Common DVD/streaming format (48kHz stereo 16-bit).</summary>
        public static AudioFormat DvdQuality => new(48000, 2, AudioSampleFormat.Int16, AudioChannelLayout.Stereo);

        /// <summary>High-definition format (48kHz stereo float).</summary>
        public static AudioFormat HighDefinition => new(48000, 2, AudioSampleFormat.Float32, AudioChannelLayout.Stereo);
    }

    /// <summary>
    /// Audio buffer containing decoded samples.
    /// </summary>
    public readonly struct AudioBuffer
    {
        /// <summary>Pointer to sample data.</summary>
        public readonly nint DataPointer;
        
        /// <summary>Number of frames (samples per channel).</summary>
        public readonly int FrameCount;
        
        /// <summary>Presentation timestamp in microseconds.</summary>
        public readonly long PresentationTimestampMicroseconds;
        
        /// <summary>Audio format.</summary>
        public readonly AudioFormat Format;

        public AudioBuffer(
            nint dataPointer,
            int frameCount,
            long presentationTimestampMicroseconds,
            AudioFormat format)
        {
            DataPointer = dataPointer;
            FrameCount = frameCount;
            PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
            Format = format;
        }

        /// <summary>Total size in bytes.</summary>
        public int SizeBytes => FrameCount * Format.BytesPerFrame;

        /// <summary>Duration in microseconds.</summary>
        public long DurationMicroseconds => (long)(FrameCount * 1_000_000.0 / Format.SampleRate);
    }

    /// <summary>
    /// Audio output device information.
    /// </summary>
    public sealed class AudioOutputDevice
    {
        /// <summary>Device identifier (platform-specific).</summary>
        public string DeviceId { get; init; } = string.Empty;
        
        /// <summary>Human-readable device name.</summary>
        public string DisplayName { get; init; } = string.Empty;
        
        /// <summary>Whether this is the system default device.</summary>
        public bool IsDefault { get; init; }
        
        /// <summary>Maximum supported sample rate.</summary>
        public int MaxSampleRate { get; init; }
        
        /// <summary>Maximum supported channel count.</summary>
        public int MaxChannels { get; init; }
    }

    /// <summary>
    /// Audio output statistics.
    /// </summary>
    public struct AudioOutputStatistics
    {
        /// <summary>Frames written to output.</summary>
        public long FramesWritten;
        
        /// <summary>Frames dropped due to buffer underrun.</summary>
        public long FramesDropped;
        
        /// <summary>Current latency in microseconds.</summary>
        public long LatencyMicroseconds;
        
        /// <summary>Buffer fill level (0.0 to 1.0).</summary>
        public float BufferFillLevel;
    }

    /// <summary>
    /// Common interface for audio output implementations.
    /// </summary>
    public interface IAudioOutput : IDisposable
    {
        /// <summary>Initializes the audio output with the specified format.</summary>
        bool Initialize(AudioFormat format);
        
        /// <summary>Starts audio playback.</summary>
        void Start();
        
        /// <summary>Stops audio playback.</summary>
        void Stop();
        
        /// <summary>Pauses audio playback.</summary>
        void Pause();
        
        /// <summary>Resumes audio playback.</summary>
        void Resume();
        
        /// <summary>Writes audio data to the output buffer.</summary>
        /// <param name="buffer">Audio buffer to write.</param>
        /// <returns>Number of frames actually written.</returns>
        int Write(in AudioBuffer buffer);
        
        /// <summary>Flushes any buffered audio.</summary>
        void Flush();
        
        /// <summary>Gets current playback position in microseconds.</summary>
        long GetPositionMicroseconds();
        
        /// <summary>Gets current latency in microseconds.</summary>
        long GetLatencyMicroseconds();
        
        /// <summary>Gets current statistics.</summary>
        AudioOutputStatistics GetStatistics();
        
        /// <summary>Gets available output devices.</summary>
        ReadOnlySpan<AudioOutputDevice> GetDevices();
        
        /// <summary>Sets the output device.</summary>
        bool SetDevice(string deviceId);
        
        /// <summary>Gets or sets master volume (0.0 to 1.0).</summary>
        float Volume { get; set; }
        
        /// <summary>Gets or sets mute state.</summary>
        bool IsMuted { get; set; }
    }
}

namespace MediaKernel.Codecs;

/// <summary>
/// Marks a class as a codec module for automatic registration by the source generator.
/// Decorated classes will be included in the static CodecRegistry at compile time.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CodecAttribute : Attribute
{
    /// <summary>
    /// Optional priority for codec selection when multiple codecs can handle the same format.
    /// Higher values indicate higher priority.
    /// </summary>
    public int Priority { get; init; } = 0;
}

/// <summary>
/// Base interface for all media processing modules (decoders, demuxers, filters).
/// </summary>
public interface IMediaModule
{
    /// <summary>
    /// Human-readable name of this module.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Initializes the module. Called once before processing begins.
    /// </summary>
    /// <returns>True if initialization succeeded, false otherwise.</returns>
    bool Initialize();

    /// <summary>
    /// Releases any resources held by this module.
    /// </summary>
    void Shutdown();
}

/// <summary>
/// Interface for video and audio decoder modules.
/// </summary>
public interface IDecoderModule : IMediaModule
{
    /// <summary>
    /// Checks if this decoder can handle the specified codec.
    /// </summary>
    /// <param name="codecFourCC">The codec identifier (e.g., "h264", "hevc", "aac").</param>
    /// <returns>True if this decoder supports the codec.</returns>
    bool CanHandle(string codecFourCC);

    /// <summary>
    /// Resets the decoder state. Called during seek operations.
    /// Clears reference frames and any buffered data.
    /// </summary>
    void ResetState();
}

/// <summary>
/// Interface for video decoders specifically.
/// </summary>
public interface IVideoDecoder : IDecoderModule
{
    /// <summary>
    /// Decodes a compressed video packet into raw pixel data.
    /// </summary>
    /// <param name="input">The compressed video data (NAL units, etc.).</param>
    /// <param name="output">Buffer to receive decoded pixels.</param>
    /// <returns>True if a frame was decoded, false if more data is needed.</returns>
    bool DecodeFrame(ReadOnlySpan<byte> input, Span<byte> output);

    /// <summary>
    /// Gets the output frame dimensions after initialization.
    /// </summary>
    (int Width, int Height) FrameDimensions { get; }

    /// <summary>
    /// Gets the output pixel format.
    /// </summary>
    MediaKernel.Core.PixelFormat OutputFormat { get; }
}

/// <summary>
/// Interface for audio decoders specifically.
/// </summary>
public interface IAudioDecoder : IDecoderModule
{
    /// <summary>
    /// Decodes compressed audio into raw samples.
    /// </summary>
    /// <param name="input">The compressed audio data.</param>
    /// <param name="output">Buffer to receive decoded float samples.</param>
    /// <returns>Number of samples decoded per channel.</returns>
    int DecodeAudio(ReadOnlySpan<byte> input, Span<float> output);

    /// <summary>
    /// Gets the number of audio channels.
    /// </summary>
    int ChannelCount { get; }

    /// <summary>
    /// Gets the sample rate in Hz.
    /// </summary>
    int SampleRate { get; }
}

/// <summary>
/// Interface for hardware-accelerated decoders.
/// </summary>
public interface IHardwareDecoder : IVideoDecoder
{
    /// <summary>
    /// Maximum supported video width.
    /// </summary>
    int MaxSupportedWidth { get; }

    /// <summary>
    /// Maximum supported video height.
    /// </summary>
    int MaxSupportedHeight { get; }

    /// <summary>
    /// Whether this hardware decoder supports 10-bit or higher color depth.
    /// </summary>
    bool SupportsHighBitDepth { get; }

    /// <summary>
    /// Decodes to a GPU surface, returning a native handle.
    /// </summary>
    /// <param name="input">The compressed video data.</param>
    /// <returns>Native handle to the GPU surface, or IntPtr.Zero on failure.</returns>
    nint DecodeToSurface(ReadOnlySpan<byte> input);
}

/// <summary>
/// Scoring system for codec selection.
/// </summary>
public enum DecoderScore
{
    /// <summary>Decoder cannot handle this format.</summary>
    Unsupported = 0,
    
    /// <summary>Software decoder available.</summary>
    Software = 10,
    
    /// <summary>Hardware-accelerated decoder available.</summary>
    Hardware = 100
}

/// <summary>
/// Request parameters for codec selection.
/// </summary>
public readonly record struct CodecRequest(
    string CodecName,
    int Width,
    int Height,
    int BitDepth,
    bool IsBatteryPowered);

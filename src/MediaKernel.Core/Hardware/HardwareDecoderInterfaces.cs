using System;

namespace MediaKernel.Core.Hardware
{
    /// <summary>
    /// Represents a decoded video frame from hardware acceleration.
    /// Contains platform-specific handles to GPU surfaces.
    /// </summary>
    public readonly struct HardwareDecodedFrame : IDisposable
    {
        /// <summary>Platform-specific surface handle (D3D11 texture, VA-API surface, etc.).</summary>
        public readonly nint SurfaceHandle;

        /// <summary>For D3D11, the array slice index within the texture array.</summary>
        public readonly int ArraySliceIndex;

        /// <summary>Width of the decoded frame in pixels.</summary>
        public readonly int Width;

        /// <summary>Height of the decoded frame in pixels.</summary>
        public readonly int Height;

        /// <summary>Pixel format of the surface (NV12, P010, etc.).</summary>
        public readonly HardwarePixelFormat Format;

        /// <summary>Presentation timestamp in ticks.</summary>
        public readonly long PresentationTimestamp;

        /// <summary>True if this is a keyframe/IDR.</summary>
        public readonly bool IsKeyframe;

        /// <summary>Reference to the surface pool for returning the surface.</summary>
        private readonly IHardwareSurfacePool? surfacePool;

        /// <summary>Surface index within the pool.</summary>
        private readonly int surfaceIndex;

        public HardwareDecodedFrame(
            nint surfaceHandle,
            int arraySliceIndex,
            int width,
            int height,
            HardwarePixelFormat format,
            long presentationTimestamp,
            bool isKeyframe,
            IHardwareSurfacePool? surfacePool = null,
            int surfaceIndex = -1)
        {
            SurfaceHandle = surfaceHandle;
            ArraySliceIndex = arraySliceIndex;
            Width = width;
            Height = height;
            Format = format;
            PresentationTimestamp = presentationTimestamp;
            IsKeyframe = isKeyframe;
            this.surfacePool = surfacePool;
            this.surfaceIndex = surfaceIndex;
        }

        /// <summary>
        /// Returns the surface to the pool for reuse.
        /// </summary>
        public void Dispose()
        {
            surfacePool?.ReleaseSurface(surfaceIndex);
        }
    }

    /// <summary>
    /// Hardware codec types.
    /// </summary>
    public enum HardwareCodecType
    {
        Unknown = 0,
        H264 = 1,
        Hevc = 2,
        Vp9 = 3,
        Av1 = 4,
        Mpeg2 = 5,
        Vc1 = 6,
    }

    /// <summary>
    /// Hardware surface pixel formats.
    /// </summary>
    public enum HardwarePixelFormat
    {
        /// <summary>Unknown or unsupported format.</summary>
        Unknown = 0,

        /// <summary>NV12: 8-bit Y plane followed by interleaved U/V plane (4:2:0).</summary>
        Nv12 = 1,

        /// <summary>P010: 10-bit Y plane followed by interleaved U/V plane (4:2:0).</summary>
        P010 = 2,

        /// <summary>P016: 16-bit Y plane followed by interleaved U/V plane (4:2:0).</summary>
        P016 = 3,

        /// <summary>AYUV: 8-bit packed AYUV (4:4:4).</summary>
        Ayuv = 4,

        /// <summary>Y410: 10-bit packed AVYU (4:4:4).</summary>
        Y410 = 5,

        /// <summary>D3D11 opaque format - GPU internal.</summary>
        D3D11Opaque = 100,
    }

    /// <summary>
    /// Interface for hardware surface pools that manage decoder output surfaces.
    /// </summary>
    public interface IHardwareSurfacePool
    {
        /// <summary>
        /// Acquires an available surface for decoding.
        /// Blocks if no surfaces are available.
        /// </summary>
        /// <param name="surfaceHandle">Output: platform-specific surface handle.</param>
        /// <param name="arraySliceIndex">Output: array slice index for texture arrays.</param>
        /// <returns>Surface index for later release, or -1 if failed.</returns>
        int AcquireSurface(out nint surfaceHandle, out int arraySliceIndex);

        /// <summary>
        /// Returns a surface to the pool for reuse.
        /// </summary>
        /// <param name="surfaceIndex">The surface index returned by AcquireSurface.</param>
        void ReleaseSurface(int surfaceIndex);

        /// <summary>
        /// Gets the total number of surfaces in the pool.
        /// </summary>
        int SurfaceCount { get; }

        /// <summary>
        /// Gets the number of currently available surfaces.
        /// </summary>
        int AvailableSurfaceCount { get; }
    }

    /// <summary>
    /// Interface for hardware video decoders.
    /// </summary>
    public interface IHardwareVideoDecoder : IDisposable
    {
        /// <summary>Gets the codec ID this decoder handles (e.g., "h264", "hevc").</summary>
        string CodecId { get; }

        /// <summary>Gets whether the decoder is successfully initialized.</summary>
        bool IsInitialized { get; }

        /// <summary>Gets the current video width.</summary>
        int Width { get; }

        /// <summary>Gets the current video height.</summary>
        int Height { get; }

        /// <summary>Gets the output pixel format.</summary>
        HardwarePixelFormat OutputFormat { get; }

        /// <summary>
        /// Initializes the decoder with stream parameters.
        /// </summary>
        /// <param name="width">Video width in pixels.</param>
        /// <param name="height">Video height in pixels.</param>
        /// <param name="codecPrivateData">Codec-specific initialization data (e.g., avcC for H.264).</param>
        /// <returns>True if initialization succeeded.</returns>
        bool Initialize(int width, int height, ReadOnlySpan<byte> codecPrivateData);

        /// <summary>
        /// Submits a compressed frame for decoding.
        /// </summary>
        /// <param name="data">Compressed frame data.</param>
        /// <param name="presentationTimestamp">Presentation timestamp in ticks.</param>
        /// <param name="isKeyframe">True if this is a keyframe.</param>
        /// <returns>True if the frame was accepted for decoding.</returns>
        bool SubmitFrame(ReadOnlySpan<byte> data, long presentationTimestamp, bool isKeyframe);

        /// <summary>
        /// Retrieves a decoded frame if available.
        /// </summary>
        /// <param name="frame">Output: the decoded frame.</param>
        /// <returns>True if a frame was retrieved.</returns>
        bool TryGetDecodedFrame(out HardwareDecodedFrame frame);

        /// <summary>
        /// Flushes the decoder, outputting all pending frames.
        /// </summary>
        void Flush();

        /// <summary>
        /// Resets the decoder state (e.g., after a seek).
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Factory for creating hardware decoders.
    /// </summary>
    public interface IHardwareDecoderFactory
    {
        /// <summary>Gets the platform name (e.g., "D3D11VA", "VA-API", "VideoToolbox").</summary>
        string PlatformName { get; }

        /// <summary>Gets whether this factory is available on the current system.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Creates a hardware decoder for the specified codec.
        /// </summary>
        /// <param name="codecId">Codec ID (e.g., "h264", "hevc").</param>
        /// <returns>Hardware decoder instance, or null if not supported.</returns>
        IHardwareVideoDecoder? CreateDecoder(string codecId);

        /// <summary>
        /// Checks if a specific codec is supported.
        /// </summary>
        /// <param name="codecId">Codec ID to check.</param>
        /// <returns>True if the codec is supported by this hardware decoder.</returns>
        bool IsCodecSupported(string codecId);
    }

    /// <summary>
    /// Capabilities of a hardware decoder.
    /// </summary>
    public sealed class HardwareDecoderCapabilities
    {
        /// <summary>Supported codec type.</summary>
        public HardwareCodecType CodecType { get; init; }

        /// <summary>Profile name (e.g., "H.264 VLD No FGT", "HEVC Main 10").</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>Maximum supported width.</summary>
        public uint MaxWidth { get; init; }

        /// <summary>Maximum supported height.</summary>
        public uint MaxHeight { get; init; }

        /// <summary>Maximum bit depth supported (8, 10, 12).</summary>
        public int MaxBitDepth { get; init; } = 8;

        /// <summary>Supported output pixel formats.</summary>
        public HardwarePixelFormat[] SupportedFormats { get; init; } = [];
    }
}

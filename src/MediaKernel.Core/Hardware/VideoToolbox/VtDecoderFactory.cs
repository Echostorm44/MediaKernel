// VideoToolbox Decoder Factory
// Factory for creating VideoToolbox hardware decoders

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// Factory for creating VideoToolbox video decoders.
/// Handles capability enumeration on macOS/iOS.
/// </summary>
public sealed class VtDecoderFactory : IHardwareDecoderFactory, IDisposable
{
    private bool isInitialized;
    private bool isDisposed;
    private readonly List<HardwareDecoderCapabilities> cachedCapabilities = new();

    /// <summary>
    /// Creates a new VideoToolbox decoder factory.
    /// </summary>
    public VtDecoderFactory()
    {
    }

    /// <summary>
    /// Gets the platform name.
    /// </summary>
    public string PlatformName => "VideoToolbox";

    /// <summary>
    /// Gets whether the factory is available on this system.
    /// </summary>
    public bool IsAvailable => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();

    /// <summary>
    /// Gets whether the factory has been initialized.
    /// </summary>
    public bool IsInitialized => isInitialized;

    /// <summary>
    /// Gets the cached decoder capabilities.
    /// </summary>
    public IReadOnlyList<HardwareDecoderCapabilities> Capabilities => cachedCapabilities;

    /// <summary>
    /// Initializes the factory.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized) return true;
        if (!IsAvailable) return false;

        // Enumerate capabilities
        EnumerateCapabilities();

        isInitialized = true;
        return true;
    }

    /// <summary>
    /// Enumerates decoder capabilities.
    /// </summary>
    private void EnumerateCapabilities()
    {
        cachedCapabilities.Clear();

        // Check each codec type
        var codecsToCheck = new[]
        {
            (CmVideoCodecType.H264, "H.264", 8, 4096u, 2160u),
            (CmVideoCodecType.Hevc, "HEVC Main", 8, 8192u, 4320u),
            (CmVideoCodecType.Vp9, "VP9", 8, 8192u, 4320u),
            (CmVideoCodecType.Av1, "AV1", 10, 8192u, 4320u),
            (CmVideoCodecType.ProRes422, "ProRes 422", 10, 8192u, 4320u),
            (CmVideoCodecType.Mpeg2Video, "MPEG-2", 8, 1920u, 1080u),
        };

        foreach (var (codecType, name, bitDepth, maxW, maxH) in codecsToCheck)
        {
            if (IsHardwareDecodeSupported(codecType))
            {
                var hwCodecType = VtInterop.VideoCodecTypeToHardwareCodecType(codecType);

                cachedCapabilities.Add(new HardwareDecoderCapabilities
                {
                    CodecType = hwCodecType,
                    ProfileName = name,
                    MaxWidth = maxW,
                    MaxHeight = maxH,
                    MaxBitDepth = bitDepth,
                    SupportedFormats = bitDepth > 8
                        ? [HardwarePixelFormat.P010]
                        : [HardwarePixelFormat.Nv12],
                });
            }
        }
    }

    /// <summary>
    /// Checks if hardware decode is supported for a codec.
    /// </summary>
    private static bool IsHardwareDecodeSupported(CmVideoCodecType codecType)
    {
        try
        {
            return VtInterop.VTIsHardwareDecodeSupported(codecType);
        }
        catch
        {
            // P/Invoke failed - likely not on Apple platform
            return false;
        }
    }

    /// <summary>
    /// Creates a hardware decoder for the specified codec.
    /// </summary>
    public IHardwareVideoDecoder? CreateDecoder(string codecId)
    {
        if (!isInitialized)
            return null;

        if (!IsCodecSupported(codecId))
            return null;

        return new VtVideoDecoder(codecId);
    }

    /// <summary>
    /// Checks if a codec is supported.
    /// </summary>
    public bool IsCodecSupported(string codecId)
    {
        var codecType = VtInterop.CodecIdToVideoCodecType(codecId);
        if (codecType == 0)
            return false;

        var hwCodecType = VtInterop.VideoCodecTypeToHardwareCodecType(codecType);
        return cachedCapabilities.Exists(c => c.CodecType == hwCodecType);
    }

    /// <summary>
    /// Gets a summary of capabilities.
    /// </summary>
    public string GetCapabilitySummary()
    {
        if (!isInitialized)
            return "Not initialized";

        var codecs = cachedCapabilities
            .Select(c => c.CodecType)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        return $"VideoToolbox (Apple)\n" +
               $"Codecs: {string.Join(", ", codecs)}\n" +
               $"Profiles: {cachedCapabilities.Count}";
    }

    /// <summary>
    /// Disposes the factory.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        cachedCapabilities.Clear();
    }
}

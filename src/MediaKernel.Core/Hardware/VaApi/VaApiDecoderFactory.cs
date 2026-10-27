// VA-API Decoder Factory
// Factory for creating VA-API hardware decoders

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// Factory for creating VA-API video decoders.
/// Handles display initialization and capability enumeration.
/// </summary>
public sealed unsafe class VaApiDecoderFactory : IHardwareDecoderFactory, IDisposable
{
    private nint display;
    private int drmFd = -1;
    private bool ownsDisplay;
    private bool isInitialized;
    private bool isDisposed;
    private readonly List<HardwareDecoderCapabilities> cachedCapabilities = new();

    private int majorVersion;
    private int minorVersion;
    private string vendorString = "";

    /// <summary>
    /// Creates a new VA-API decoder factory.
    /// </summary>
    public VaApiDecoderFactory()
    {
    }

    /// <summary>
    /// Creates a VA-API decoder factory with an existing display.
    /// </summary>
    /// <param name="existingDisplay">Existing VA display handle.</param>
    public VaApiDecoderFactory(nint existingDisplay)
    {
        display = existingDisplay;
        ownsDisplay = false;
    }

    /// <summary>
    /// Gets the platform name.
    /// </summary>
    public string PlatformName => "VA-API";

    /// <summary>
    /// Gets whether the factory is available on this system.
    /// </summary>
    public bool IsAvailable => OperatingSystem.IsLinux();

    /// <summary>
    /// Gets whether the factory has been initialized.
    /// </summary>
    public bool IsInitialized => isInitialized;

    /// <summary>
    /// Gets the VA-API version.
    /// </summary>
    public Version Version => new(majorVersion, minorVersion);

    /// <summary>
    /// Gets the vendor string.
    /// </summary>
    public string VendorString => vendorString;

    /// <summary>
    /// Gets the VA display handle.
    /// </summary>
    public nint Display => display;

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

        // Try to get display
        if (display == 0)
        {
            if (!TryOpenDrmDisplay())
            {
                return false;
            }
            ownsDisplay = true;
        }

        // Initialize VA
        VaStatus status = VaApiInterop.vaInitialize(display, out majorVersion, out minorVersion);
        if (status != VaStatus.Success)
        {
            CloseDrmDisplay();
            return false;
        }

        vendorString = VaApiInterop.GetVendorString(display);

        // Enumerate capabilities
        EnumerateCapabilities();

        isInitialized = true;
        return true;
    }

    /// <summary>
    /// Tries to open a DRM render node for VA-API.
    /// </summary>
    private bool TryOpenDrmDisplay()
    {
        // Try common DRM render node paths
        string[] renderNodes =
        [
            "/dev/dri/renderD128",
            "/dev/dri/renderD129",
            "/dev/dri/renderD130",
            "/dev/dri/renderD131",
        ];

        foreach (var path in renderNodes)
        {
            drmFd = OpenDrmNode(path);
            if (drmFd >= 0)
            {
                display = VaApiInterop.vaGetDisplayDRM(drmFd);
                if (display != 0)
                    return true;

                CloseDrmFd();
            }
        }

        return false;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int LibcClose(int fd);

    private static int OpenDrmNode(string path)
    {
        const int O_RDWR = 0x0002;
        const int O_CLOEXEC = 0x80000;
        return LibcOpen(path, O_RDWR | O_CLOEXEC);
    }

    private void CloseDrmFd()
    {
        if (drmFd >= 0)
        {
            LibcClose(drmFd);
            drmFd = -1;
        }
    }

    private void CloseDrmDisplay()
    {
        display = 0;
        CloseDrmFd();
    }

    /// <summary>
    /// Enumerates decoder capabilities.
    /// </summary>
    private void EnumerateCapabilities()
    {
        cachedCapabilities.Clear();

        int maxProfiles = VaApiInterop.vaMaxNumProfiles(display);
        if (maxProfiles <= 0) return;

        var profiles = stackalloc VaProfile[maxProfiles];
        int numProfiles;

        VaStatus status = VaApiInterop.vaQueryConfigProfiles(display, profiles, out numProfiles);
        if (status != VaStatus.Success) return;

        int maxEntrypoints = VaApiInterop.vaMaxNumEntrypoints(display);
        var entrypoints = stackalloc VaEntrypoint[maxEntrypoints];

        for (int i = 0; i < numProfiles; i++)
        {
            VaProfile profile = profiles[i];
            HardwareCodecType codecType = VaApiInterop.ProfileToCodecType(profile);
            if (codecType == HardwareCodecType.Unknown)
                continue;

            int numEntrypoints;
            status = VaApiInterop.vaQueryConfigEntrypoints(display, profile, entrypoints, out numEntrypoints);
            if (status != VaStatus.Success)
                continue;

            bool hasVld = false;
            for (int j = 0; j < numEntrypoints; j++)
            {
                if (entrypoints[j] == VaEntrypoint.Vld)
                {
                    hasVld = true;
                    break;
                }
            }

            if (!hasVld)
                continue;

            // Query max resolution
            VaConfigAttrib attrib;
            attrib.Type = VaConfigAttribType.MaxPictureWidth;
            attrib.Value = 0;

            uint maxWidth = 4096;  // Default fallback
            uint maxHeight = 2160;

            status = VaApiInterop.vaGetConfigAttributes(display, profile, VaEntrypoint.Vld, &attrib, 1);
            if (status == VaStatus.Success && attrib.Value != VaConfigAttribValues.Undefined)
            {
                maxWidth = attrib.Value;
            }

            attrib.Type = VaConfigAttribType.MaxPictureHeight;
            status = VaApiInterop.vaGetConfigAttributes(display, profile, VaEntrypoint.Vld, &attrib, 1);
            if (status == VaStatus.Success && attrib.Value != VaConfigAttribValues.Undefined)
            {
                maxHeight = attrib.Value;
            }

            int bitDepth = VaApiInterop.GetProfileBitDepth(profile);

            cachedCapabilities.Add(new HardwareDecoderCapabilities
            {
                CodecType = codecType,
                ProfileName = profile.ToString(),
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                MaxBitDepth = bitDepth,
                SupportedFormats = bitDepth > 8
                    ? [HardwarePixelFormat.P010]
                    : [HardwarePixelFormat.Nv12],
            });
        }
    }

    /// <summary>
    /// Creates a hardware decoder for the specified codec.
    /// </summary>
    public IHardwareVideoDecoder? CreateDecoder(string codecId)
    {
        if (!isInitialized || display == 0)
            return null;

        if (!IsCodecSupported(codecId))
            return null;

        return new VaApiVideoDecoder(display, codecId);
    }

    /// <summary>
    /// Checks if a codec is supported.
    /// </summary>
    public bool IsCodecSupported(string codecId)
    {
        VaProfile profile = VaApiInterop.CodecIdToProfile(codecId);
        if (profile == VaProfile.None)
            return false;

        HardwareCodecType codecType = VaApiInterop.ProfileToCodecType(profile);
        return cachedCapabilities.Exists(c => c.CodecType == codecType);
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

        return $"VA-API {majorVersion}.{minorVersion} - {vendorString}\n" +
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

        if (ownsDisplay && display != 0)
        {
            VaApiInterop.vaTerminate(display);
            CloseDrmDisplay();
        }
    }
}

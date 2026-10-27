// VA-API P/Invoke Interop
// Linux Video Acceleration API native bindings

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// P/Invoke bindings for libva (VA-API).
/// </summary>
public static unsafe partial class VaApiInterop
{
    private const string LibVa = "libva.so.2";
    private const string LibVaDrm = "libva-drm.so.2";
    private const string LibVaX11 = "libva-x11.so.2";
    private const string LibVaWayland = "libva-wayland.so.2";

    #region Display Functions

    /// <summary>
    /// Gets the VA display from a DRM file descriptor.
    /// </summary>
    [DllImport(LibVaDrm, EntryPoint = "vaGetDisplayDRM")]
    public static extern nint vaGetDisplayDRM(int fd);

    /// <summary>
    /// Gets the VA display from an X11 display.
    /// </summary>
    [DllImport(LibVaX11, EntryPoint = "vaGetDisplay")]
    public static extern nint vaGetDisplay(nint x11Display);

    /// <summary>
    /// Gets the VA display from a Wayland display.
    /// </summary>
    [DllImport(LibVaWayland, EntryPoint = "vaGetDisplayWl")]
    public static extern nint vaGetDisplayWl(nint wlDisplay);

    /// <summary>
    /// Initializes the VA display.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaInitialize")]
    public static extern VaStatus vaInitialize(nint dpy, out int majorVersion, out int minorVersion);

    /// <summary>
    /// Terminates the VA display.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaTerminate")]
    public static extern VaStatus vaTerminate(nint dpy);

    /// <summary>
    /// Gets error string for a status code.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaErrorStr")]
    public static extern nint vaErrorStr(VaStatus status);

    /// <summary>
    /// Gets the VA vendor string.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQueryVendorString")]
    public static extern nint vaQueryVendorString(nint dpy);

    #endregion

    #region Configuration Functions

    /// <summary>
    /// Queries the number of supported profiles.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaMaxNumProfiles")]
    public static extern int vaMaxNumProfiles(nint dpy);

    /// <summary>
    /// Queries the number of supported entrypoints for a profile.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaMaxNumEntrypoints")]
    public static extern int vaMaxNumEntrypoints(nint dpy);

    /// <summary>
    /// Queries the number of supported config attributes.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaMaxNumConfigAttributes")]
    public static extern int vaMaxNumConfigAttributes(nint dpy);

    /// <summary>
    /// Queries the supported profiles.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQueryConfigProfiles")]
    public static extern VaStatus vaQueryConfigProfiles(nint dpy, VaProfile* profiles, out int numProfiles);

    /// <summary>
    /// Queries the supported entrypoints for a profile.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQueryConfigEntrypoints")]
    public static extern VaStatus vaQueryConfigEntrypoints(
        nint dpy, VaProfile profile, VaEntrypoint* entrypoints, out int numEntrypoints);

    /// <summary>
    /// Gets config attributes.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaGetConfigAttributes")]
    public static extern VaStatus vaGetConfigAttributes(
        nint dpy, VaProfile profile, VaEntrypoint entrypoint,
        VaConfigAttrib* attribList, int numAttribs);

    /// <summary>
    /// Creates a configuration.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateConfig")]
    public static extern VaStatus vaCreateConfig(
        nint dpy, VaProfile profile, VaEntrypoint entrypoint,
        VaConfigAttrib* attribList, int numAttribs, out uint configId);

    /// <summary>
    /// Destroys a configuration.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDestroyConfig")]
    public static extern VaStatus vaDestroyConfig(nint dpy, uint configId);

    /// <summary>
    /// Queries config attributes.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQueryConfigAttributes")]
    public static extern VaStatus vaQueryConfigAttributes(
        nint dpy, uint configId, out VaProfile profile, out VaEntrypoint entrypoint,
        VaConfigAttrib* attribList, out int numAttribs);

    #endregion

    #region Surface Functions

    /// <summary>
    /// Creates surfaces.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateSurfaces")]
    public static extern VaStatus vaCreateSurfaces(
        nint dpy, uint format, uint width, uint height,
        uint* surfaces, uint numSurfaces,
        VaSurfaceAttrib* attribList, uint numAttribs);

    /// <summary>
    /// Destroys surfaces.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDestroySurfaces")]
    public static extern VaStatus vaDestroySurfaces(nint dpy, uint* surfaces, int numSurfaces);

    /// <summary>
    /// Queries surface status.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQuerySurfaceStatus")]
    public static extern VaStatus vaQuerySurfaceStatus(nint dpy, uint surface, out VaSurfaceStatus status);

    /// <summary>
    /// Queries surface attributes.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQuerySurfaceAttributes")]
    public static extern VaStatus vaQuerySurfaceAttributes(
        nint dpy, uint configId, VaSurfaceAttrib* attribList, out uint numAttribs);

    /// <summary>
    /// Syncs surface (waits for rendering to complete).
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaSyncSurface")]
    public static extern VaStatus vaSyncSurface(nint dpy, uint surface);

    #endregion

    #region Context Functions

    /// <summary>
    /// Creates a context.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateContext")]
    public static extern VaStatus vaCreateContext(
        nint dpy, uint configId, int pictureWidth, int pictureHeight,
        int flag, uint* renderTargets, int numRenderTargets, out uint contextId);

    /// <summary>
    /// Destroys a context.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDestroyContext")]
    public static extern VaStatus vaDestroyContext(nint dpy, uint contextId);

    #endregion

    #region Buffer Functions

    /// <summary>
    /// Creates a buffer.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateBuffer")]
    public static extern VaStatus vaCreateBuffer(
        nint dpy, uint contextId, VaBufferType type,
        uint size, uint numElements, void* data, out uint bufferId);

    /// <summary>
    /// Creates a buffer with type2.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateBuffer2")]
    public static extern VaStatus vaCreateBuffer2(
        nint dpy, uint contextId, VaBufferType type,
        uint width, uint height, out uint unitSize, out uint pitch, out uint bufferId);

    /// <summary>
    /// Maps a buffer.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaMapBuffer")]
    public static extern VaStatus vaMapBuffer(nint dpy, uint bufferId, out nint buffer);

    /// <summary>
    /// Unmaps a buffer.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaUnmapBuffer")]
    public static extern VaStatus vaUnmapBuffer(nint dpy, uint bufferId);

    /// <summary>
    /// Destroys a buffer.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDestroyBuffer")]
    public static extern VaStatus vaDestroyBuffer(nint dpy, uint bufferId);

    #endregion

    #region Rendering Functions

    /// <summary>
    /// Begins rendering to a surface.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaBeginPicture")]
    public static extern VaStatus vaBeginPicture(nint dpy, uint contextId, uint surface);

    /// <summary>
    /// Renders buffers to the current surface.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaRenderPicture")]
    public static extern VaStatus vaRenderPicture(nint dpy, uint contextId, uint* buffers, int numBuffers);

    /// <summary>
    /// Ends rendering to the current surface.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaEndPicture")]
    public static extern VaStatus vaEndPicture(nint dpy, uint contextId);

    #endregion

    #region Image Functions

    /// <summary>
    /// Queries the number of supported image formats.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaMaxNumImageFormats")]
    public static extern int vaMaxNumImageFormats(nint dpy);

    /// <summary>
    /// Queries supported image formats.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaQueryImageFormats")]
    public static extern VaStatus vaQueryImageFormats(nint dpy, VaImageFormat* formats, out int numFormats);

    /// <summary>
    /// Creates an image.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCreateImage")]
    public static extern VaStatus vaCreateImage(
        nint dpy, VaImageFormat* format, int width, int height, out VaImage image);

    /// <summary>
    /// Derives an image from a surface.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDeriveImage")]
    public static extern VaStatus vaDeriveImage(nint dpy, uint surface, out VaImage image);

    /// <summary>
    /// Destroys an image.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaDestroyImage")]
    public static extern VaStatus vaDestroyImage(nint dpy, uint imageId);

    /// <summary>
    /// Gets image data.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaGetImage")]
    public static extern VaStatus vaGetImage(
        nint dpy, uint surface, int x, int y, uint width, uint height, uint imageId);

    /// <summary>
    /// Puts image data to a surface.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaPutImage")]
    public static extern VaStatus vaPutImage(
        nint dpy, uint surface, uint imageId,
        int srcX, int srcY, uint srcWidth, uint srcHeight,
        int destX, int destY, uint destWidth, uint destHeight);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the error string for a status code.
    /// </summary>
    public static string GetErrorString(VaStatus status)
    {
        nint ptr = vaErrorStr(status);
        return ptr != 0 ? Marshal.PtrToStringAnsi(ptr) ?? status.ToString() : status.ToString();
    }

    /// <summary>
    /// Gets the vendor string for a display.
    /// </summary>
    public static string GetVendorString(nint dpy)
    {
        nint ptr = vaQueryVendorString(dpy);
        return ptr != 0 ? Marshal.PtrToStringAnsi(ptr) ?? "" : "";
    }

    /// <summary>
    /// Converts VaProfile to HardwareCodecType.
    /// </summary>
    public static HardwareCodecType ProfileToCodecType(VaProfile profile)
    {
        return profile switch
        {
            VaProfile.H264Baseline or VaProfile.H264Main or VaProfile.H264High or
            VaProfile.H264ConstrainedBaseline or VaProfile.H264MultiviewHigh or
            VaProfile.H264StereoHigh => HardwareCodecType.H264,

            VaProfile.HevcMain or VaProfile.HevcMain10 or VaProfile.HevcMain12 or
            VaProfile.HevcMain422_10 or VaProfile.HevcMain422_12 or
            VaProfile.HevcMain444 or VaProfile.HevcMain444_10 or VaProfile.HevcMain444_12 or
            VaProfile.HevcSccMain or VaProfile.HevcSccMain10 or
            VaProfile.HevcSccMain444 or VaProfile.HevcSccMain444_10 => HardwareCodecType.Hevc,

            VaProfile.Vp9Profile0 or VaProfile.Vp9Profile1 or
            VaProfile.Vp9Profile2 or VaProfile.Vp9Profile3 => HardwareCodecType.Vp9,

            VaProfile.Av1Profile0 or VaProfile.Av1Profile1 => HardwareCodecType.Av1,

            VaProfile.Mpeg2Simple or VaProfile.Mpeg2Main => HardwareCodecType.Mpeg2,

            VaProfile.Vc1Simple or VaProfile.Vc1Main or VaProfile.Vc1Advanced => HardwareCodecType.Vc1,

            _ => HardwareCodecType.Unknown,
        };
    }

    /// <summary>
    /// Maps codec ID string to VaProfile.
    /// </summary>
    public static VaProfile CodecIdToProfile(string codecId, int profile = 0, int bitDepth = 8)
    {
        return codecId.ToLowerInvariant() switch
        {
            "h264" or "avc" => profile switch
            {
                66 => VaProfile.H264Baseline,
                77 => VaProfile.H264Main,
                100 => VaProfile.H264High,
                _ => VaProfile.H264Main,
            },

            "h265" or "hevc" => bitDepth switch
            {
                10 => VaProfile.HevcMain10,
                12 => VaProfile.HevcMain12,
                _ => VaProfile.HevcMain,
            },

            "vp9" => bitDepth switch
            {
                10 or 12 => VaProfile.Vp9Profile2,
                _ => VaProfile.Vp9Profile0,
            },

            "av1" => VaProfile.Av1Profile0,

            "mpeg2" or "mpeg2video" => VaProfile.Mpeg2Main,

            "vc1" => VaProfile.Vc1Advanced,

            _ => VaProfile.None,
        };
    }

    /// <summary>
    /// Gets the bit depth for a profile.
    /// </summary>
    public static int GetProfileBitDepth(VaProfile profile)
    {
        return profile switch
        {
            VaProfile.HevcMain10 or VaProfile.HevcMain422_10 or
            VaProfile.HevcMain444_10 or VaProfile.HevcSccMain10 or
            VaProfile.HevcSccMain444_10 or VaProfile.Vp9Profile2 or
            VaProfile.Vp9Profile3 => 10,

            VaProfile.HevcMain12 or VaProfile.HevcMain422_12 or
            VaProfile.HevcMain444_12 => 12,

            _ => 8,
        };
    }

    #endregion
}

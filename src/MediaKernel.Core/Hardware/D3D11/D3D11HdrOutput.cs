// D3D11 HDR output management for Windows HDR displays
// Reference: VLC's modules/video_output/win32/d3d11_swapchain.c, DXGI HDR documentation

using System;
using System.Runtime.InteropServices;
using MediaKernel.Core.Video;

namespace MediaKernel.Core.Hardware.D3D11;

/// <summary>
/// Manages HDR output configuration for D3D11 swap chains.
/// Handles HDR10 metadata passthrough, color space configuration, and display capability detection.
/// </summary>
public sealed class D3D11HdrOutput : IDisposable
{
    private nint swapChain;
    private bool hdrEnabled;
    private DxgiColorSpaceType activeColorSpace;
    private HdrMetadata currentMetadata;
    private bool disposed;
    
    /// <summary>Current HDR output state.</summary>
    public bool IsHdrEnabled => hdrEnabled;
    
    /// <summary>Active color space for the swap chain.</summary>
    public DxgiColorSpaceType ColorSpace => activeColorSpace;
    
    /// <summary>Detected display HDR capabilities.</summary>
    public HdrDisplayCapabilities DisplayCapabilities { get; private set; }

    /// <summary>
    /// Creates an HDR output manager for the specified swap chain.
    /// </summary>
    /// <param name="swapChainHandle">IDXGISwapChain4 pointer.</param>
    public D3D11HdrOutput(nint swapChainHandle)
    {
        swapChain = swapChainHandle;
        activeColorSpace = DxgiColorSpaceType.RgbFullG22NoneP709;
        DetectDisplayCapabilities();
    }

    /// <summary>
    /// Detects the HDR capabilities of the connected display.
    /// </summary>
    public void DetectDisplayCapabilities()
    {
        if (swapChain == nint.Zero)
        {
            DisplayCapabilities = default;
            return;
        }

        var caps = new HdrDisplayCapabilities();

        // Query output containing the swap chain
        var hr = GetContainingOutput(swapChain, out nint output);
        if (hr < 0 || output == nint.Zero)
        {
            DisplayCapabilities = caps;
            return;
        }

        try
        {
            // Try to get IDXGIOutput6 for HDR capabilities
            var iidOutput6 = new Guid("068346e8-aaec-4b84-add7-137f513f77a1");
            hr = QueryInterface(output, ref iidOutput6, out nint output6);
            
            if (hr >= 0 && output6 != nint.Zero)
            {
                try
                {
                    // Get output description including HDR support
                    hr = GetDesc1(output6, out var desc);
                    if (hr >= 0)
                    {
                        caps.SupportsHdr = desc.ColorSpace == DxgiColorSpaceType.RgbFullG2084NoneP2020;
                        caps.IsHdrEnabled = caps.SupportsHdr;
                        caps.MaxLuminance = desc.MaxLuminance;
                        caps.MaxFullFrameLuminance = desc.MaxFullFrameLuminance;
                        caps.MinLuminance = desc.MinLuminance;
                        caps.BitsPerColor = (int)desc.BitsPerColor;
                        caps.ColorSpace = desc.ColorSpace;
                        
                        // Map to our color primaries enum
                        caps.Primaries = desc.ColorSpace switch
                        {
                            DxgiColorSpaceType.RgbFullG2084NoneP2020 => ColorPrimaries.Bt2020,
                            DxgiColorSpaceType.RgbStudioG2084NoneP2020 => ColorPrimaries.Bt2020,
                            _ => ColorPrimaries.Bt709
                        };
                    }
                }
                finally
                {
                    Release(output6);
                }
            }
        }
        finally
        {
            Release(output);
        }

        DisplayCapabilities = caps;
    }

    /// <summary>
    /// Enables HDR output mode with the specified color space.
    /// </summary>
    /// <param name="colorSpace">DXGI color space type (use RgbFullG2084NoneP2020 for HDR10).</param>
    /// <returns>True if HDR was successfully enabled.</returns>
    public bool EnableHdr(DxgiColorSpaceType colorSpace = DxgiColorSpaceType.RgbFullG2084NoneP2020)
    {
        if (swapChain == nint.Zero)
            return false;

        // Check if display supports HDR
        if (!DisplayCapabilities.SupportsHdr)
            return false;

        // Set the color space on the swap chain
        var hr = SetColorSpace1(swapChain, colorSpace);
        if (hr < 0)
            return false;

        activeColorSpace = colorSpace;
        hdrEnabled = colorSpace == DxgiColorSpaceType.RgbFullG2084NoneP2020 ||
                     colorSpace == DxgiColorSpaceType.RgbStudioG2084NoneP2020;
        
        return true;
    }

    /// <summary>
    /// Disables HDR mode and returns to SDR (sRGB).
    /// </summary>
    public void DisableHdr()
    {
        if (swapChain == nint.Zero)
            return;

        SetColorSpace1(swapChain, DxgiColorSpaceType.RgbFullG22NoneP709);
        activeColorSpace = DxgiColorSpaceType.RgbFullG22NoneP709;
        hdrEnabled = false;
    }

    /// <summary>
    /// Sets HDR10 static metadata for the content being displayed.
    /// This should be called when starting HDR content playback or when metadata changes.
    /// </summary>
    /// <param name="metadata">HDR metadata from the video stream.</param>
    /// <returns>True if metadata was successfully set.</returns>
    public bool SetHdrMetadata(HdrMetadata metadata)
    {
        if (swapChain == nint.Zero || !hdrEnabled)
            return false;

        currentMetadata = metadata;
        
        // Convert to DXGI HDR10 metadata format
        var dxgiMetadata = DxgiHdrMetadataHdr10.FromHdrMetadata(metadata);
        
        var hr = SetHDRMetaData(swapChain, DxgiHdrMetadataType.Hdr10, ref dxgiMetadata);
        return hr >= 0;
    }

    /// <summary>
    /// Clears HDR metadata (use when switching to SDR content).
    /// </summary>
    public void ClearHdrMetadata()
    {
        if (swapChain == nint.Zero)
            return;

        currentMetadata = default;
        ClearHDRMetaData(swapChain);
    }

    /// <summary>
    /// Gets the optimal DXGI format for the current HDR state.
    /// </summary>
    /// <returns>
    /// R10G10B10A2 for HDR10, R16G16B16A16F for scRGB, or R8G8B8A8 for SDR.
    /// </returns>
    public DxgiFormat GetOptimalFormat()
    {
        if (!hdrEnabled)
            return DxgiFormat.R8G8B8A8Unorm;

        return activeColorSpace switch
        {
            DxgiColorSpaceType.RgbFullG2084NoneP2020 => DxgiFormat.R10G10B10A2Unorm,
            DxgiColorSpaceType.RgbStudioG2084NoneP2020 => DxgiFormat.R10G10B10A2Unorm,
            DxgiColorSpaceType.RgbFullG10NoneP709 => DxgiFormat.R16G16B16A16Float,
            _ => DxgiFormat.R8G8B8A8Unorm,
        };
    }

    /// <summary>
    /// Determines whether tone mapping is required based on content and display capabilities.
    /// </summary>
    /// <param name="contentMetadata">HDR metadata from the content.</param>
    /// <returns>True if tone mapping should be applied.</returns>
    public bool RequiresToneMapping(HdrMetadata contentMetadata)
    {
        // If display doesn't support HDR, always tone map
        if (!DisplayCapabilities.SupportsHdr || !DisplayCapabilities.IsHdrEnabled)
            return true;

        // If content peak exceeds display capability, tone mapping helps
        var contentPeak = contentMetadata.ContentLightLevel.MaxContentLightLevel;
        if (contentPeak > 0 && contentPeak > DisplayCapabilities.MaxLuminance)
            return true;

        // HDR passthrough - no tone mapping needed
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        if (swapChain != nint.Zero && hdrEnabled)
        {
            DisableHdr();
            ClearHdrMetadata();
        }

        swapChain = nint.Zero;
        disposed = true;
    }

    #region DXGI Interop

    // IDXGISwapChain3::SetColorSpace1
    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int SetColorSpace1(nint swapChain, DxgiColorSpaceType colorSpace);

    // IDXGISwapChain4::SetHDRMetaData
    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int SetHDRMetaData(
        nint swapChain, 
        DxgiHdrMetadataType type, 
        ref DxgiHdrMetadataHdr10 metadata);

    // Clear HDR metadata
    private static void ClearHDRMetaData(nint swapChain)
    {
        // Setting type to None clears metadata
        var empty = default(DxgiHdrMetadataHdr10);
        SetHDRMetaData(swapChain, DxgiHdrMetadataType.None, ref empty);
    }

    // IDXGISwapChain::GetContainingOutput
    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int GetContainingOutput(nint swapChain, out nint output);

    // IUnknown::QueryInterface
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int QueryInterface(nint obj, ref Guid iid, out nint ppv);

    // IUnknown::Release
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int Release(nint obj);

    // IDXGIOutput6::GetDesc1 - returns DXGI_OUTPUT_DESC1
    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int GetDesc1(nint output6, out DxgiOutputDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        
        public int DesktopCoordinatesLeft;
        public int DesktopCoordinatesTop;
        public int DesktopCoordinatesRight;
        public int DesktopCoordinatesBottom;
        
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
        
        public uint BitsPerColor;
        public DxgiColorSpaceType ColorSpace;
        public float RedPrimaryX;
        public float RedPrimaryY;
        public float GreenPrimaryX;
        public float GreenPrimaryY;
        public float BluePrimaryX;
        public float BluePrimaryY;
        public float WhitePointX;
        public float WhitePointY;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    #endregion
}

/// <summary>
/// HDR output mode for the video renderer.
/// </summary>
public enum HdrOutputMode
{
    /// <summary>Always use SDR output with tone mapping.</summary>
    ForceSdr,
    
    /// <summary>Use HDR output if display supports it, otherwise SDR with tone mapping.</summary>
    Auto,
    
    /// <summary>Force HDR output (may cause clipping on SDR displays).</summary>
    ForceHdr,
}

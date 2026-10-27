// HDR metadata types and color space definitions
// Reference: VLC's libplacebo integration, ITU-R BT.2390, BT.2020

namespace MediaKernel.Core.Video;

/// <summary>
/// Transfer function (EOTF/OETF) used for encoding luminance values.
/// </summary>
public enum TransferFunction
{
    /// <summary>Unknown or unspecified transfer function.</summary>
    Unknown = 0,
    
    /// <summary>SDR: BT.1886 (CRT emulation, ~gamma 2.4).</summary>
    Bt1886,
    
    /// <summary>SDR: sRGB (~gamma 2.2 with linear segment).</summary>
    Srgb,
    
    /// <summary>HDR: Perceptual Quantizer (SMPTE ST 2084, HDR10).</summary>
    Pq,
    
    /// <summary>HDR: Hybrid Log-Gamma (ARIB STD-B67, broadcast HDR).</summary>
    Hlg,
    
    /// <summary>Linear (no transfer function applied).</summary>
    Linear
}

/// <summary>
/// Color primaries defining the RGB color space.
/// </summary>
public enum ColorPrimaries
{
    /// <summary>Unknown or unspecified primaries.</summary>
    Unknown = 0,
    
    /// <summary>BT.709 (HD video, sRGB).</summary>
    Bt709,
    
    /// <summary>BT.601 (SD video, NTSC/PAL).</summary>
    Bt601,
    
    /// <summary>BT.2020 (UHD/HDR wide color gamut).</summary>
    Bt2020,
    
    /// <summary>DCI-P3 (digital cinema, wide color gamut).</summary>
    DciP3,
    
    /// <summary>Display P3 (Apple devices, D65 white point).</summary>
    DisplayP3
}

/// <summary>
/// Content light level information (CLL) for HDR content.
/// Used for scene-referred HDR metadata (MaxCLL/MaxFALL from HEVC SEI).
/// </summary>
public readonly struct ContentLightLevel
{
    /// <summary>Maximum Content Light Level in cd/m² (nits).</summary>
    public readonly ushort MaxContentLightLevel;
    
    /// <summary>Maximum Frame-Average Light Level in cd/m² (nits).</summary>
    public readonly ushort MaxFrameAverageLightLevel;
    
    public ContentLightLevel(ushort maxCll, ushort maxFall)
    {
        MaxContentLightLevel = maxCll;
        MaxFrameAverageLightLevel = maxFall;
    }
    
    /// <summary>Returns true if content light level information is present.</summary>
    public bool IsValid => MaxContentLightLevel > 0 || MaxFrameAverageLightLevel > 0;
    
    public static ContentLightLevel None => default;
}

/// <summary>
/// Mastering display color volume metadata (SMPTE ST 2086).
/// Describes the display used to master HDR content.
/// </summary>
public readonly struct MasteringDisplayInfo
{
    /// <summary>Red primary X coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float RedPrimaryX;
    
    /// <summary>Red primary Y coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float RedPrimaryY;
    
    /// <summary>Green primary X coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float GreenPrimaryX;
    
    /// <summary>Green primary Y coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float GreenPrimaryY;
    
    /// <summary>Blue primary X coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float BluePrimaryX;
    
    /// <summary>Blue primary Y coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float BluePrimaryY;
    
    /// <summary>White point X coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float WhitePointX;
    
    /// <summary>White point Y coordinate (0.0-1.0 in CIE 1931).</summary>
    public readonly float WhitePointY;
    
    /// <summary>Maximum luminance of the mastering display in cd/m² (nits).</summary>
    public readonly float MaxLuminance;
    
    /// <summary>Minimum luminance of the mastering display in cd/m² (nits).</summary>
    public readonly float MinLuminance;
    
    public MasteringDisplayInfo(
        float redX, float redY,
        float greenX, float greenY,
        float blueX, float blueY,
        float whiteX, float whiteY,
        float maxLuminance, float minLuminance)
    {
        RedPrimaryX = redX;
        RedPrimaryY = redY;
        GreenPrimaryX = greenX;
        GreenPrimaryY = greenY;
        BluePrimaryX = blueX;
        BluePrimaryY = blueY;
        WhitePointX = whiteX;
        WhitePointY = whiteY;
        MaxLuminance = maxLuminance;
        MinLuminance = minLuminance;
    }
    
    /// <summary>Returns true if mastering display info is present.</summary>
    public bool IsValid => MaxLuminance > 0;
    
    public static MasteringDisplayInfo None => default;
    
    /// <summary>Standard BT.2020 primaries with 1000 nit peak.</summary>
    public static MasteringDisplayInfo Bt2020_1000Nits => new(
        redX: 0.708f, redY: 0.292f,
        greenX: 0.170f, greenY: 0.797f,
        blueX: 0.131f, blueY: 0.046f,
        whiteX: 0.3127f, whiteY: 0.3290f,  // D65
        maxLuminance: 1000f, minLuminance: 0.0001f);
    
    /// <summary>Standard DCI-P3 primaries with 1000 nit peak.</summary>
    public static MasteringDisplayInfo DciP3_1000Nits => new(
        redX: 0.680f, redY: 0.320f,
        greenX: 0.265f, greenY: 0.690f,
        blueX: 0.150f, blueY: 0.060f,
        whiteX: 0.3127f, whiteY: 0.3290f,  // D65
        maxLuminance: 1000f, minLuminance: 0.0001f);
}

/// <summary>
/// Complete HDR metadata for a video stream or frame.
/// </summary>
public readonly struct HdrMetadata
{
    /// <summary>Transfer function (PQ, HLG, etc.).</summary>
    public readonly TransferFunction Transfer;
    
    /// <summary>Color primaries (BT.2020, DCI-P3, etc.).</summary>
    public readonly ColorPrimaries Primaries;
    
    /// <summary>Content light level information.</summary>
    public readonly ContentLightLevel ContentLightLevel;
    
    /// <summary>Mastering display information.</summary>
    public readonly MasteringDisplayInfo MasteringDisplay;
    
    /// <summary>Peak luminance in nits (derived from metadata or estimated).</summary>
    public readonly float PeakLuminance;
    
    /// <summary>Whether Dolby Vision metadata is present.</summary>
    public readonly bool HasDolbyVision;
    
    /// <summary>Dolby Vision profile if present.</summary>
    public readonly DolbyVisionProfile DolbyVisionProfile;
    
    public HdrMetadata(
        TransferFunction transfer,
        ColorPrimaries primaries,
        ContentLightLevel contentLightLevel,
        MasteringDisplayInfo masteringDisplay,
        DolbyVisionProfile dolbyVisionProfile = DolbyVisionProfile.Unknown)
    {
        Transfer = transfer;
        Primaries = primaries;
        ContentLightLevel = contentLightLevel;
        MasteringDisplay = masteringDisplay;
        HasDolbyVision = dolbyVisionProfile != DolbyVisionProfile.Unknown;
        DolbyVisionProfile = dolbyVisionProfile;
        
        // Derive peak luminance from available metadata
        if (contentLightLevel.MaxContentLightLevel > 0)
            PeakLuminance = contentLightLevel.MaxContentLightLevel;
        else if (masteringDisplay.MaxLuminance > 0)
            PeakLuminance = masteringDisplay.MaxLuminance;
        else if (transfer == TransferFunction.Pq)
            PeakLuminance = 1000f;  // Default HDR10 assumption
        else if (transfer == TransferFunction.Hlg)
            PeakLuminance = 1000f;  // HLG nominal
        else
            PeakLuminance = 100f;   // SDR
    }
    
    /// <summary>Returns true if this is HDR content.</summary>
    public bool IsHdr => Transfer == TransferFunction.Pq || Transfer == TransferFunction.Hlg || HasDolbyVision;
    
    /// <summary>Returns true if any HDR metadata is present.</summary>
    public bool HasMetadata => ContentLightLevel.IsValid || MasteringDisplay.IsValid || HasDolbyVision;
    
    public static HdrMetadata Sdr => new(
        TransferFunction.Bt1886,
        ColorPrimaries.Bt709,
        ContentLightLevel.None,
        MasteringDisplayInfo.None);
    
    public static HdrMetadata Hdr10Default => new(
        TransferFunction.Pq,
        ColorPrimaries.Bt2020,
        new ContentLightLevel(1000, 400),
        MasteringDisplayInfo.Bt2020_1000Nits);
    
    /// <summary>
    /// Creates HDR metadata with Dolby Vision profile.
    /// </summary>
    public static HdrMetadata DolbyVision(DolbyVisionProfile profile) => new(
        TransferFunction.Pq,
        ColorPrimaries.Bt2020,
        new ContentLightLevel(1000, 400),
        MasteringDisplayInfo.Bt2020_1000Nits,
        profile);
}

/// <summary>
/// HDR10+ dynamic metadata for a single frame.
/// Contains scene-by-scene brightness information.
/// </summary>
public readonly struct Hdr10PlusMetadata
{
    /// <summary>Maximum scene luminance per RGB channel (nits).</summary>
    public readonly float MaxSceneR;
    public readonly float MaxSceneG;
    public readonly float MaxSceneB;
    
    /// <summary>Average scene luminance (nits).</summary>
    public readonly float AverageMaxRgb;
    
    /// <summary>Target display luminance (nits).</summary>
    public readonly float TargetLuminance;
    
    /// <summary>Knee point coordinates for tone curve.</summary>
    public readonly float KneePointX;
    public readonly float KneePointY;
    
    /// <summary>Bezier curve anchors (up to 9).</summary>
    public readonly float[] BezierAnchors;
    
    /// <summary>Number of valid Bezier anchors.</summary>
    public readonly int BezierAnchorCount;
    
    public Hdr10PlusMetadata(
        float maxR, float maxG, float maxB,
        float average,
        float targetLuminance,
        float kneeX, float kneeY,
        float[]? bezierAnchors = null)
    {
        MaxSceneR = maxR;
        MaxSceneG = maxG;
        MaxSceneB = maxB;
        AverageMaxRgb = average;
        TargetLuminance = targetLuminance;
        KneePointX = kneeX;
        KneePointY = kneeY;
        BezierAnchors = bezierAnchors ?? [];
        BezierAnchorCount = BezierAnchors.Length;
    }
    
    public bool IsValid => TargetLuminance > 0;
    
    public static Hdr10PlusMetadata None => default;
}

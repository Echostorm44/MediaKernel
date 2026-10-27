// Dolby Vision metadata types and structures
// Reference: ETSI GS DVB 094 Part 2, SMPTE ST 2094-10, VLC libplacebo integration

namespace MediaKernel.Core.Video;

/// <summary>
/// Dolby Vision profile identifier.
/// Profiles define the encoding format and compatibility with other HDR standards.
/// </summary>
public enum DolbyVisionProfile
{
    /// <summary>Unknown or invalid profile.</summary>
    Unknown = 0,
    
    /// <summary>Profile 4: Dual-layer, 12-bit, HDR10 base layer compatible.</summary>
    Profile4 = 4,
    
    /// <summary>Profile 5: Single-layer, 10-bit IPT-PQ.</summary>
    Profile5 = 5,
    
    /// <summary>Profile 7: Dual-layer, reference display target.</summary>
    Profile7 = 7,
    
    /// <summary>Profile 8: Single-layer, HDR10 compatible. Most common for streaming.</summary>
    Profile8 = 8,
    
    /// <summary>Profile 10: Single-layer, HLG compatible for broadcast.</summary>
    Profile10 = 10
}

/// <summary>
/// Dolby Vision level indicating resolution and frame rate limits.
/// </summary>
public enum DolbyVisionLevel
{
    /// <summary>Unknown level.</summary>
    Unknown = 0,
    
    /// <summary>Level 1: HD 24fps.</summary>
    Level1 = 1,
    
    /// <summary>Level 2: HD 30fps.</summary>
    Level2 = 2,
    
    /// <summary>Level 3: HD 48fps.</summary>
    Level3 = 3,
    
    /// <summary>Level 4: HD 60fps.</summary>
    Level4 = 4,
    
    /// <summary>Level 5: UHD 24fps.</summary>
    Level5 = 5,
    
    /// <summary>Level 6: UHD 30fps.</summary>
    Level6 = 6,
    
    /// <summary>Level 7: UHD 48fps.</summary>
    Level7 = 7,
    
    /// <summary>Level 8: UHD 60fps.</summary>
    Level8 = 8,
    
    /// <summary>Level 9: UHD 120fps.</summary>
    Level9 = 9
}

/// <summary>
/// Method used for reshaping curve mapping in Dolby Vision.
/// </summary>
public enum ReshapeMethod
{
    /// <summary>Polynomial mapping (up to cubic).</summary>
    Polynomial = 0,
    
    /// <summary>Multi-Model Regression (MMR) mapping for complex curves.</summary>
    Mmr = 1
}

/// <summary>
/// Non-Linear Quantization method for Dolby Vision.
/// </summary>
public enum NlqMethod
{
    /// <summary>No NLQ applied.</summary>
    None = -1,
    
    /// <summary>Linear dead-zone NLQ.</summary>
    LinearDeadZone = 0
}

/// <summary>
/// Reshaping curve for a single color component in Dolby Vision.
/// Used to map base layer values to output values.
/// </summary>
public struct DolbyVisionReshapeCurve
{
    /// <summary>Number of pivot points (1-9).</summary>
    public byte NumPivots;
    
    /// <summary>Pivot point values (up to 9 points).</summary>
    public ushort[] Pivots;
    
    /// <summary>Mapping method for each segment between pivots (up to 8 segments).</summary>
    public ReshapeMethod[] MappingMethod;
    
    /// <summary>Polynomial order for each segment (0=constant, 1=linear, 2=quadratic).</summary>
    public byte[] PolynomialOrder;
    
    /// <summary>Polynomial coefficients [segment][order] - up to 8 segments, 3 coefficients each.</summary>
    public long[][] PolynomialCoefficients;
    
    /// <summary>MMR order for each segment (1-3).</summary>
    public byte[] MmrOrder;
    
    /// <summary>MMR constant term for each segment.</summary>
    public long[] MmrConstant;
    
    /// <summary>MMR coefficients [segment][component][order] - up to 8 segments, 3 components, 7 coefficients.</summary>
    public long[][][] MmrCoefficients;
    
    /// <summary>
    /// Creates a new reshaping curve with default empty arrays.
    /// </summary>
    public static DolbyVisionReshapeCurve Create()
    {
        return new DolbyVisionReshapeCurve
        {
            NumPivots = 0,
            Pivots = new ushort[9],
            MappingMethod = new ReshapeMethod[8],
            PolynomialOrder = new byte[8],
            PolynomialCoefficients = CreateJaggedArray<long>(8, 3),
            MmrOrder = new byte[8],
            MmrConstant = new long[8],
            MmrCoefficients = CreateJaggedArray<long>(8, 3, 7)
        };
    }
    
    private static T[][] CreateJaggedArray<T>(int dim1, int dim2)
    {
        var array = new T[dim1][];
        for (int i = 0; i < dim1; i++)
            array[i] = new T[dim2];
        return array;
    }
    
    private static T[][][] CreateJaggedArray<T>(int dim1, int dim2, int dim3)
    {
        var array = new T[dim1][][];
        for (int i = 0; i < dim1; i++)
        {
            array[i] = new T[dim2][];
            for (int j = 0; j < dim2; j++)
                array[i][j] = new T[dim3];
        }
        return array;
    }
}

/// <summary>
/// Non-Linear Quantization parameters for a single color component.
/// Used when enhancement layer is present.
/// </summary>
public struct DolbyVisionNlqParams
{
    /// <summary>Bit depth of the offset value.</summary>
    public byte OffsetDepth;
    
    /// <summary>DC offset for NLQ.</summary>
    public ushort Offset;
    
    /// <summary>Maximum HDR input value.</summary>
    public ulong HdrInMax;
    
    /// <summary>Dead-zone slope.</summary>
    public ulong DeadZoneSlope;
    
    /// <summary>Dead-zone threshold.</summary>
    public ulong DeadZoneThreshold;
}

/// <summary>
/// Color metadata for Dolby Vision including conversion matrices.
/// </summary>
public struct DolbyVisionColorMetadata
{
    /// <summary>YCbCr to RGB offset (3 components).</summary>
    public float[] YccToRgbOffset;
    
    /// <summary>YCbCr to RGB 3x3 matrix (row-major, 9 elements).</summary>
    public float[] YccToRgbMatrix;
    
    /// <summary>RGB to LMS 3x3 matrix (row-major, 9 elements).</summary>
    public float[] RgbToLmsMatrix;
    
    /// <summary>Minimum source PQ value (12-bit, 0-4095).</summary>
    public ushort SourceMinPq;
    
    /// <summary>Maximum source PQ value (12-bit, 0-4095).</summary>
    public ushort SourceMaxPq;
    
    /// <summary>
    /// Creates color metadata with default arrays.
    /// </summary>
    public static DolbyVisionColorMetadata Create()
    {
        return new DolbyVisionColorMetadata
        {
            YccToRgbOffset = new float[3],
            YccToRgbMatrix = new float[9],
            RgbToLmsMatrix = new float[9],
            SourceMinPq = 0,
            SourceMaxPq = 4095
        };
    }
    
    /// <summary>
    /// Gets the source minimum luminance in cd/m² (nits) from PQ value.
    /// </summary>
    public float SourceMinLuminance => PqToNits(SourceMinPq / 4095f);
    
    /// <summary>
    /// Gets the source maximum luminance in cd/m² (nits) from PQ value.
    /// </summary>
    public float SourceMaxLuminance => PqToNits(SourceMaxPq / 4095f);
    
    /// <summary>
    /// Convert PQ (ST 2084) signal to luminance in nits.
    /// </summary>
    private static float PqToNits(float pq)
    {
        const float m1 = 0.1593017578125f;
        const float m2 = 78.84375f;
        const float c1 = 0.8359375f;
        const float c2 = 18.8515625f;
        const float c3 = 18.6875f;
        const float peakLuminance = 10000f;
        
        float p = MathF.Pow(pq, 1f / m2);
        float numerator = MathF.Max(p - c1, 0f);
        float denominator = c2 - c3 * p;
        float linear = MathF.Pow(numerator / denominator, 1f / m1);
        
        return linear * peakLuminance;
    }
}

/// <summary>
/// RPU (Reference Processing Unit) header information.
/// </summary>
public struct DolbyVisionRpuHeader
{
    /// <summary>RPU type (0 = none, 2 = 8.1 RPU).</summary>
    public byte RpuType;
    
    /// <summary>RPU format (for error checking).</summary>
    public ushort RpuFormat;
    
    /// <summary>VDR RPU profile (maps to DolbyVisionProfile).</summary>
    public byte VdrRpuProfile;
    
    /// <summary>VDR RPU level.</summary>
    public byte VdrRpuLevel;
    
    /// <summary>Base layer codec type (0 = AVC, 1 = HEVC).</summary>
    public bool BlVideoIdcPresent;
    
    /// <summary>Enhancement layer present flag.</summary>
    public bool ElPresent;
    
    /// <summary>VDR bit depth minus 8 (typically 2 for 10-bit, 4 for 12-bit).</summary>
    public byte VdrBitDepthMinus8;
    
    /// <summary>Base layer bit depth minus 8.</summary>
    public byte BlBitDepthMinus8;
    
    /// <summary>Enhancement layer bit depth minus 8.</summary>
    public byte ElBitDepthMinus8;
    
    /// <summary>Log2 of coefficient denominator for reshaping.</summary>
    public byte CoefLog2Denom;
    
    /// <summary>Gets the Dolby Vision profile.</summary>
    public DolbyVisionProfile Profile => (DolbyVisionProfile)VdrRpuProfile;
    
    /// <summary>Gets the Dolby Vision level.</summary>
    public DolbyVisionLevel Level => (DolbyVisionLevel)VdrRpuLevel;
    
    /// <summary>Gets base layer bit depth.</summary>
    public int BlBitDepth => BlBitDepthMinus8 + 8;
    
    /// <summary>Gets enhancement layer bit depth.</summary>
    public int ElBitDepth => ElBitDepthMinus8 + 8;
}

/// <summary>
/// Complete Dolby Vision metadata for a single frame.
/// Contains RPU header, reshaping curves, NLQ parameters, and color metadata.
/// </summary>
public struct DolbyVisionMetadata
{
    /// <summary>Whether this metadata is valid/present.</summary>
    public bool IsValid;
    
    /// <summary>RPU header with profile/level information.</summary>
    public DolbyVisionRpuHeader Header;
    
    /// <summary>Coefficient log2 denominator for reshaping curves.</summary>
    public byte CoefLog2Denom;
    
    /// <summary>Base layer bit depth.</summary>
    public byte BlBitDepth;
    
    /// <summary>Enhancement layer bit depth.</summary>
    public byte ElBitDepth;
    
    /// <summary>NLQ method indicator.</summary>
    public NlqMethod NlqMethodIdc;
    
    /// <summary>Color conversion metadata (matrices, PQ range).</summary>
    public DolbyVisionColorMetadata ColorMetadata;
    
    /// <summary>Reshaping curves for Y, Cb, Cr components.</summary>
    public DolbyVisionReshapeCurve[] ReshapeCurves;
    
    /// <summary>NLQ parameters for Y, Cb, Cr components.</summary>
    public DolbyVisionNlqParams[] NlqParams;
    
    /// <summary>
    /// Creates empty metadata structure with default arrays.
    /// </summary>
    public static DolbyVisionMetadata Create()
    {
        return new DolbyVisionMetadata
        {
            IsValid = false,
            Header = default,
            CoefLog2Denom = 0,
            BlBitDepth = 10,
            ElBitDepth = 0,
            NlqMethodIdc = NlqMethod.None,
            ColorMetadata = DolbyVisionColorMetadata.Create(),
            ReshapeCurves = new[]
            {
                DolbyVisionReshapeCurve.Create(),
                DolbyVisionReshapeCurve.Create(),
                DolbyVisionReshapeCurve.Create()
            },
            NlqParams = new DolbyVisionNlqParams[3]
        };
    }
    
    /// <summary>None/empty Dolby Vision metadata.</summary>
    public static DolbyVisionMetadata None => default;
}

/// <summary>
/// Dolby Vision configuration record parsed from MP4/MKV container.
/// </summary>
public readonly struct DolbyVisionConfigurationRecord
{
    /// <summary>Major version of the Dolby Vision specification.</summary>
    public readonly byte MajorVersion;
    
    /// <summary>Minor version of the Dolby Vision specification.</summary>
    public readonly byte MinorVersion;
    
    /// <summary>Dolby Vision profile.</summary>
    public readonly DolbyVisionProfile Profile;
    
    /// <summary>Dolby Vision level.</summary>
    public readonly DolbyVisionLevel Level;
    
    /// <summary>Whether RPU data is present in the video stream.</summary>
    public readonly bool RpuPresent;
    
    /// <summary>Whether enhancement layer is present.</summary>
    public readonly bool ElPresent;
    
    /// <summary>Whether base layer is present (should always be true).</summary>
    public readonly bool BlPresent;
    
    /// <summary>Base layer compatibility ID (HDR10, SDR, etc.).</summary>
    public readonly byte BlSignalCompatibilityId;
    
    public DolbyVisionConfigurationRecord(
        byte majorVersion,
        byte minorVersion,
        DolbyVisionProfile profile,
        DolbyVisionLevel level,
        bool rpuPresent,
        bool elPresent,
        bool blPresent,
        byte blSignalCompatibilityId)
    {
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        Profile = profile;
        Level = level;
        RpuPresent = rpuPresent;
        ElPresent = elPresent;
        BlPresent = blPresent;
        BlSignalCompatibilityId = blSignalCompatibilityId;
    }
    
    /// <summary>
    /// Parse Dolby Vision configuration from MP4/MKV dvcC or dvvC box data.
    /// </summary>
    /// <param name="data">Raw configuration data (at least 4 bytes).</param>
    /// <returns>Parsed configuration record.</returns>
    public static DolbyVisionConfigurationRecord Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return default;
        
        byte majorVersion = data[0];
        byte minorVersion = data[1];
        
        // Byte 2: profile (7 bits) + level high bit
        byte profileAndLevel = data[2];
        var profile = (DolbyVisionProfile)((profileAndLevel >> 1) & 0x7F);
        
        // Byte 3: level low bits (5 bits) + flags
        byte levelAndFlags = data[3];
        var level = (DolbyVisionLevel)(((profileAndLevel & 0x01) << 5) | ((levelAndFlags >> 3) & 0x1F));
        
        bool rpuPresent = (levelAndFlags & 0x04) != 0;
        bool elPresent = (levelAndFlags & 0x02) != 0;
        bool blPresent = (levelAndFlags & 0x01) != 0;
        
        byte blSignalCompatibilityId = 0;
        if (data.Length >= 5)
            blSignalCompatibilityId = (byte)((data[4] >> 4) & 0x0F);
        
        return new DolbyVisionConfigurationRecord(
            majorVersion, minorVersion, profile, level,
            rpuPresent, elPresent, blPresent, blSignalCompatibilityId);
    }
    
    /// <summary>
    /// Whether this is a valid Dolby Vision configuration.
    /// </summary>
    public bool IsValid => Profile != DolbyVisionProfile.Unknown;
    
    /// <summary>
    /// Whether this stream is HDR10 compatible (can be played without DoVi decoder).
    /// </summary>
    public bool IsHdr10Compatible => BlSignalCompatibilityId == 1;
    
    /// <summary>
    /// Whether this stream is SDR compatible.
    /// </summary>
    public bool IsSdrCompatible => BlSignalCompatibilityId == 2;
    
    /// <summary>
    /// Whether this stream is HLG compatible.
    /// </summary>
    public bool IsHlgCompatible => BlSignalCompatibilityId == 4;
}

using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// D3D11 GUID constants for video decoder profiles.
    /// </summary>
    public static class D3D11VideoDecoderProfiles
    {
        // H.264 profiles
        public static readonly Guid H264VldNoFgt = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
        public static readonly Guid H264VldFgt = new("1b81be69-a0c7-11d3-b984-00c04f2e73c5");
        public static readonly Guid H264VldWithFmoxNoFgt = new("1b81be6a-a0c7-11d3-b984-00c04f2e73c5");
        public static readonly Guid H264VldStereoProgressive = new("d79be8da-0cf1-4c81-b82a-69a4e236f43d");
        public static readonly Guid H264VldStereo = new("f9aaccbb-c2b6-4cfc-8779-5707b1760552");
        public static readonly Guid H264VldMultiviewNoFgt = new("705b9d82-76cf-49d6-b7e6-ac8872db013c");

        // HEVC profiles
        public static readonly Guid HevcVldMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
        public static readonly Guid HevcVldMain10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
        public static readonly Guid HevcVldMain12 = new("36E32A52-8DFD-44D0-ADDB-E15A02693E4B");
        public static readonly Guid HevcVldMain422_10 = new("B4F71B74-8148-4E92-9B52-45D66A4CD4E6");
        public static readonly Guid HevcVldMain422_12 = new("3BD6B20C-7E8E-4A98-9E88-C0B8B0929CED");
        public static readonly Guid HevcVldMain444 = new("E54E6EBB-E1C8-44C1-8E96-4F3B8EB02F67");
        public static readonly Guid HevcVldMain444_10 = new("0E1BA93C-0F3F-4A47-B103-F35C6FBFC66A");
        public static readonly Guid HevcVldMain444_12 = new("A4917C28-F448-4A93-8259-5F8D4E11BA79");

        // VP9 profiles
        public static readonly Guid Vp9VldProfile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
        public static readonly Guid Vp9VldProfile210bit = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");

        // AV1 profiles
        public static readonly Guid Av1VldProfile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");
        public static readonly Guid Av1VldProfile1 = new("6936ff0f-45b1-4163-9cc1-646ef6946108");
        public static readonly Guid Av1VldProfile210bit = new("0c5f2aa1-e541-4089-bb7b-98110a19d7c8");

        /// <summary>
        /// Gets the decoder profile GUID for an H.264 profile.
        /// </summary>
        public static Guid GetH264Profile(int profileIdc, int bitDepth)
        {
            // For 8-bit H.264, use the standard VLD profile
            if (bitDepth == 8)
            {
                return H264VldNoFgt;
            }

            // 10/12-bit H.264 not commonly hardware accelerated
            return Guid.Empty;
        }

        /// <summary>
        /// Gets the decoder profile GUID for an HEVC profile.
        /// </summary>
        public static Guid GetHevcProfile(int profileIdc, int bitDepth, int chromaFormat)
        {
            // Main profile, 8-bit
            if (bitDepth == 8 && chromaFormat == 1)
                return HevcVldMain;

            // Main 10 profile
            if (bitDepth == 10 && chromaFormat == 1)
                return HevcVldMain10;

            // Main 12 profile
            if (bitDepth == 12 && chromaFormat == 1)
                return HevcVldMain12;

            // 4:2:2 profiles
            if (chromaFormat == 2)
            {
                if (bitDepth == 10) return HevcVldMain422_10;
                if (bitDepth == 12) return HevcVldMain422_12;
            }

            // 4:4:4 profiles
            if (chromaFormat == 3)
            {
                if (bitDepth == 8) return HevcVldMain444;
                if (bitDepth == 10) return HevcVldMain444_10;
                if (bitDepth == 12) return HevcVldMain444_12;
            }

            return Guid.Empty;
        }
    }

    /// <summary>
    /// DXGI format constants for video surfaces.
    /// </summary>
    public enum DxgiFormat : uint
    {
        Unknown = 0,
        R10G10B10A2Typeless = 23,
        R10G10B10A2Unorm = 24,
        R10G10B10A2Uint = 25,
        R8G8B8A8Unorm = 28,
        R16G16B16A16Float = 113,
        B8G8R8A8Unorm = 87,
        Nv12 = 103,
        P010 = 104,
        P016 = 105,
        Opaque420 = 106,
        Yuy2 = 107,
        Y210 = 108,
        Y216 = 109,
        Nv11 = 110,
        Ai44 = 111,
        Ia44 = 112,
        P8 = 113,
        A8P8 = 114,
        B4G4R4A4Unorm = 115,
        Ayuv = 100,
        Y410 = 101,
        Y416 = 102,
    }

    /// <summary>
    /// D3D11 bind flags.
    /// </summary>
    [Flags]
    public enum D3D11BindFlags : uint
    {
        VertexBuffer = 0x1,
        IndexBuffer = 0x2,
        ConstantBuffer = 0x4,
        ShaderResource = 0x8,
        StreamOutput = 0x10,
        RenderTarget = 0x20,
        DepthStencil = 0x40,
        UnorderedAccess = 0x80,
        Decoder = 0x200,
        VideoEncoder = 0x400,
    }

    /// <summary>
    /// D3D11 usage flags.
    /// </summary>
    public enum D3D11Usage : uint
    {
        Default = 0,
        Immutable = 1,
        Dynamic = 2,
        Staging = 3,
    }

    /// <summary>
    /// D3D11 CPU access flags.
    /// </summary>
    [Flags]
    public enum D3D11CpuAccessFlags : uint
    {
        None = 0,
        Write = 0x10000,
        Read = 0x20000,
    }

    /// <summary>
    /// D3D11 resource misc flags.
    /// </summary>
    [Flags]
    public enum D3D11ResourceMiscFlags : uint
    {
        None = 0,
        GenerateMips = 0x1,
        Shared = 0x2,
        TextureCube = 0x4,
        DrawIndirectArgs = 0x10,
        BufferAllowRawViews = 0x20,
        BufferStructured = 0x40,
        ResourceClamp = 0x80,
        SharedKeyedMutex = 0x100,
        GdiCompatible = 0x200,
        SharedNtHandle = 0x800,
        RestrictedContent = 0x1000,
        RestrictSharedResource = 0x2000,
        RestrictSharedResourceDriver = 0x4000,
        Guarded = 0x8000,
        TilePool = 0x20000,
        Tiled = 0x40000,
        HwProtected = 0x80000,
    }

    /// <summary>
    /// D3D11_TEXTURE2D_DESC structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public DxgiFormat Format;
        public DxgiSampleDesc SampleDesc;
        public D3D11Usage Usage;
        public D3D11BindFlags BindFlags;
        public D3D11CpuAccessFlags CpuAccessFlags;
        public D3D11ResourceMiscFlags MiscFlags;
    }

    /// <summary>
    /// DXGI_SAMPLE_DESC structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiSampleDesc
    {
        public uint Count;
        public uint Quality;

        public static DxgiSampleDesc Default => new() { Count = 1, Quality = 0 };
    }

    /// <summary>
    /// D3D11_VIDEO_DECODER_DESC structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public DxgiFormat OutputFormat;
    }

    /// <summary>
    /// D3D11_VIDEO_DECODER_CONFIG structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11VideoDecoderConfig
    {
        public Guid GuidConfigBitstreamEncryption;
        public Guid GuidConfigMBcontrolEncryption;
        public Guid GuidConfigResidDiffEncryption;
        public uint ConfigBitstreamRaw;
        public uint ConfigMBcontrolRasterOrder;
        public uint ConfigResidDiffHost;
        public uint ConfigSpatialResid8;
        public uint ConfigResid8Subtraction;
        public uint ConfigSpatialHost8or9Clipping;
        public uint ConfigSpatialResidInterleaved;
        public uint ConfigIntraResidUnsigned;
        public uint ConfigResidDiffAccelerator;
        public uint ConfigHostInverseScan;
        public uint ConfigSpecificIDCT;
        public uint Config4GroupedCoefs;
        public ushort ConfigMinRenderTargetBuffCount;
        public ushort ConfigDecoderSpecific;
    }

    /// <summary>
    /// D3D11_VIDEO_DECODER_BUFFER_DESC structure for H.264.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11VideoDecoderBufferDesc
    {
        public D3D11VideoDecoderBufferType BufferType;
        public uint BufferIndex;
        public uint DataOffset;
        public uint DataSize;
        public uint FirstMBAddress;
        public uint NumMBsInBuffer;
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint ReservedBits;
        public nint IV;
        public uint IVSize;
        public int PartialEncryption;
        public D3D11EncryptedBlockInfo EncryptedBlockInfo;
    }

    /// <summary>
    /// D3D11_VIDEO_DECODER_BUFFER_TYPE enumeration.
    /// </summary>
    public enum D3D11VideoDecoderBufferType : uint
    {
        PictureParameters = 0,
        MacroblockControl = 1,
        ResidualDifference = 2,
        DeblockingControl = 3,
        InverseQuantizationMatrix = 4,
        SliceControl = 5,
        Bitstream = 6,
        MotionVector = 7,
        FilmGrain = 8,
    }

    /// <summary>
    /// D3D11_ENCRYPTED_BLOCK_INFO structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11EncryptedBlockInfo
    {
        public uint NumEncryptedBytesAtBeginning;
        public uint NumBytesInSkipPattern;
        public uint NumBytesInEncryptPattern;
    }

    /// <summary>
    /// Converts between MediaKernel and DXGI formats.
    /// </summary>
    public static class FormatConverter
    {
        public static HardwarePixelFormat ToHardwareFormat(DxgiFormat dxgiFormat)
        {
            return dxgiFormat switch
            {
                DxgiFormat.Nv12 => HardwarePixelFormat.Nv12,
                DxgiFormat.P010 => HardwarePixelFormat.P010,
                DxgiFormat.P016 => HardwarePixelFormat.P016,
                DxgiFormat.Ayuv => HardwarePixelFormat.Ayuv,
                DxgiFormat.Y410 => HardwarePixelFormat.Y410,
                DxgiFormat.Opaque420 => HardwarePixelFormat.D3D11Opaque,
                _ => HardwarePixelFormat.Unknown,
            };
        }

        public static DxgiFormat ToDxgiFormat(HardwarePixelFormat format)
        {
            return format switch
            {
                HardwarePixelFormat.Nv12 => DxgiFormat.Nv12,
                HardwarePixelFormat.P010 => DxgiFormat.P010,
                HardwarePixelFormat.P016 => DxgiFormat.P016,
                HardwarePixelFormat.Ayuv => DxgiFormat.Ayuv,
                HardwarePixelFormat.Y410 => DxgiFormat.Y410,
                HardwarePixelFormat.D3D11Opaque => DxgiFormat.Opaque420,
                _ => DxgiFormat.Unknown,
            };
        }

        /// <summary>
        /// Gets the preferred output format for a bit depth.
        /// </summary>
        public static DxgiFormat GetPreferredFormat(int bitDepth)
        {
            return bitDepth switch
            {
                8 => DxgiFormat.Nv12,
                10 => DxgiFormat.P010,
                12 or 16 => DxgiFormat.P016,
                _ => DxgiFormat.Nv12,
            };
        }
    }

    /// <summary>
    /// DXGI color space types for HDR output configuration.
    /// Reference: DXGI_COLOR_SPACE_TYPE enumeration.
    /// </summary>
    public enum DxgiColorSpaceType : uint
    {
        /// <summary>Standard RGB (sRGB), full range, BT.709 primaries.</summary>
        RgbFullG22NoneP709 = 0,
        
        /// <summary>Standard RGB (sRGB), limited range, BT.709 primaries.</summary>
        RgbStudioG22NoneP709 = 1,
        
        /// <summary>BT.2020 RGB, limited range, gamma 2.2.</summary>
        RgbStudioG22NoneP2020 = 2,
        
        /// <summary>HDR10: BT.2020 RGB, full range, PQ (ST 2084) transfer.</summary>
        RgbFullG2084NoneP2020 = 12,
        
        /// <summary>HDR10: BT.2020 RGB, limited range, PQ (ST 2084) transfer.</summary>
        RgbStudioG2084NoneP2020 = 13,
        
        /// <summary>scRGB: Linear RGB, full range, BT.709 primaries.</summary>
        RgbFullG10NoneP709 = 14,
        
        /// <summary>YCbCr BT.601, limited range.</summary>
        YcbcrFullG22NoneP709X601 = 3,
        
        /// <summary>YCbCr BT.709, limited range.</summary>
        YcbcrStudioG22LeftP709 = 6,
        
        /// <summary>YCbCr BT.2020, limited range.</summary>
        YcbcrStudioG22LeftP2020 = 9,
        
        /// <summary>YCbCr BT.2020, full range.</summary>
        YcbcrFullG22LeftP2020 = 10,
        
        /// <summary>HDR10 YCbCr: BT.2020, PQ transfer, limited range.</summary>
        YcbcrStudioG2084LeftP2020 = 11,
        
        /// <summary>HLG YCbCr: BT.2020, HLG transfer.</summary>
        YcbcrStudioGHlgTopLeftP2020 = 16,
        
        /// <summary>HLG YCbCr: BT.2020, HLG transfer, full range.</summary>
        YcbcrFullGHlgTopLeftP2020 = 17,
    }

    /// <summary>
    /// DXGI HDR metadata types.
    /// </summary>
    public enum DxgiHdrMetadataType : uint
    {
        /// <summary>No HDR metadata.</summary>
        None = 0,
        
        /// <summary>HDR10 static metadata (SMPTE ST 2086 + MaxCLL/MaxFALL).</summary>
        Hdr10 = 1,
        
        /// <summary>HDR10+ dynamic metadata (reserved, not widely supported).</summary>
        Hdr10Plus = 2,
    }

    /// <summary>
    /// DXGI_HDR_METADATA_HDR10 structure for HDR10 static metadata.
    /// Used with IDXGISwapChain4::SetHDRMetaData.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiHdrMetadataHdr10
    {
        /// <summary>Red primary X coordinate (0.00002 units, CIE 1931).</summary>
        public ushort RedPrimaryX;
        
        /// <summary>Red primary Y coordinate (0.00002 units, CIE 1931).</summary>
        public ushort RedPrimaryY;
        
        /// <summary>Green primary X coordinate (0.00002 units, CIE 1931).</summary>
        public ushort GreenPrimaryX;
        
        /// <summary>Green primary Y coordinate (0.00002 units, CIE 1931).</summary>
        public ushort GreenPrimaryY;
        
        /// <summary>Blue primary X coordinate (0.00002 units, CIE 1931).</summary>
        public ushort BluePrimaryX;
        
        /// <summary>Blue primary Y coordinate (0.00002 units, CIE 1931).</summary>
        public ushort BluePrimaryY;
        
        /// <summary>White point X coordinate (0.00002 units, CIE 1931).</summary>
        public ushort WhitePointX;
        
        /// <summary>White point Y coordinate (0.00002 units, CIE 1931).</summary>
        public ushort WhitePointY;
        
        /// <summary>Maximum mastering display luminance in 1 nit units.</summary>
        public uint MaxMasteringLuminance;
        
        /// <summary>Minimum mastering display luminance in 0.0001 nit units.</summary>
        public uint MinMasteringLuminance;
        
        /// <summary>Maximum content light level in nits.</summary>
        public ushort MaxContentLightLevel;
        
        /// <summary>Maximum frame-average light level in nits.</summary>
        public ushort MaxFrameAverageLightLevel;

        /// <summary>
        /// Creates HDR10 metadata from MediaKernel HDR metadata.
        /// </summary>
        public static DxgiHdrMetadataHdr10 FromHdrMetadata(Video.HdrMetadata metadata)
        {
            var mastering = metadata.MasteringDisplay;
            var cll = metadata.ContentLightLevel;
            
            return new DxgiHdrMetadataHdr10
            {
                // Convert from 0.0-1.0 to 0.00002 units (multiply by 50000)
                RedPrimaryX = (ushort)(mastering.RedPrimaryX * 50000f),
                RedPrimaryY = (ushort)(mastering.RedPrimaryY * 50000f),
                GreenPrimaryX = (ushort)(mastering.GreenPrimaryX * 50000f),
                GreenPrimaryY = (ushort)(mastering.GreenPrimaryY * 50000f),
                BluePrimaryX = (ushort)(mastering.BluePrimaryX * 50000f),
                BluePrimaryY = (ushort)(mastering.BluePrimaryY * 50000f),
                WhitePointX = (ushort)(mastering.WhitePointX * 50000f),
                WhitePointY = (ushort)(mastering.WhitePointY * 50000f),
                
                // Luminance: max in nits, min in 0.0001 nit units
                MaxMasteringLuminance = (uint)mastering.MaxLuminance,
                MinMasteringLuminance = (uint)(mastering.MinLuminance * 10000f),
                
                // Content light levels
                MaxContentLightLevel = cll.MaxContentLightLevel,
                MaxFrameAverageLightLevel = cll.MaxFrameAverageLightLevel,
            };
        }
        
        /// <summary>
        /// Creates default HDR10 metadata for BT.2020 with 1000 nit peak.
        /// </summary>
        public static DxgiHdrMetadataHdr10 DefaultHdr10 => new()
        {
            // BT.2020 primaries
            RedPrimaryX = 34000,   // 0.680
            RedPrimaryY = 16000,   // 0.320
            GreenPrimaryX = 13250, // 0.265
            GreenPrimaryY = 34500, // 0.690
            BluePrimaryX = 7500,   // 0.150
            BluePrimaryY = 3000,   // 0.060
            WhitePointX = 15635,   // 0.3127 (D65)
            WhitePointY = 16450,   // 0.3290 (D65)
            MaxMasteringLuminance = 1000,     // 1000 nits
            MinMasteringLuminance = 1,        // 0.0001 nits
            MaxContentLightLevel = 1000,
            MaxFrameAverageLightLevel = 400,
        };
    }

    /// <summary>
    /// HDR display capabilities reported by DXGI.
    /// </summary>
    public struct HdrDisplayCapabilities
    {
        /// <summary>Whether HDR output is supported.</summary>
        public bool SupportsHdr;
        
        /// <summary>Whether advanced color (HDR) is enabled in Windows.</summary>
        public bool IsHdrEnabled;
        
        /// <summary>Maximum luminance in nits.</summary>
        public float MaxLuminance;
        
        /// <summary>Maximum full-frame luminance in nits.</summary>
        public float MaxFullFrameLuminance;
        
        /// <summary>Minimum luminance in nits.</summary>
        public float MinLuminance;
        
        /// <summary>Current color space of the output.</summary>
        public DxgiColorSpaceType ColorSpace;
        
        /// <summary>Bits per color channel.</summary>
        public int BitsPerColor;
        
        /// <summary>Color primaries of the display.</summary>
        public Video.ColorPrimaries Primaries;
    }
}

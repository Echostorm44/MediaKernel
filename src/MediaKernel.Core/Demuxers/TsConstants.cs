namespace MediaKernel.Core.Demuxers;

/// <summary>
/// MPEG-TS stream type codes as defined in ISO/IEC 13818-1.
/// Used in PMT to identify elementary stream formats.
/// </summary>
public static class TsStreamType
{
    // Video
    public const byte Mpeg1Video = 0x01;
    public const byte Mpeg2Video = 0x02;
    public const byte Mpeg4Video = 0x10;
    public const byte H264 = 0x1B;
    public const byte H265 = 0x24;
    public const byte Vp9 = 0xC0;  // VP9 (non-standard)

    // Audio
    public const byte Mpeg1Audio = 0x03;
    public const byte Mpeg2Audio = 0x04;
    public const byte AacAdts = 0x0F;
    public const byte AacLatm = 0x11;
    public const byte Lpcm = 0x80;      // Private stream - LPCM
    public const byte Ac3 = 0x81;       // Private stream - AC-3
    public const byte Dts = 0x82;       // Private stream - DTS
    public const byte TrueHd = 0x83;    // Private stream - TrueHD
    public const byte Eac3 = 0x84;      // Private stream - E-AC3
    public const byte DtsHd = 0x85;     // Private stream - DTS-HD

    // Private/subtitles
    public const byte PrivateData = 0x06;  // PES private data
    public const byte DvbSubtitle = 0x59;
    public const byte Teletext = 0x56;
    public const byte Scte27 = 0x82;

    // Other
    public const byte Metadata = 0x15;
    public const byte Dcii = 0x80;
}

/// <summary>
/// Well-known PID values in MPEG-TS.
/// </summary>
public static class TsReservedPid
{
    public const ushort Pat = 0x0000;      // Program Association Table
    public const ushort Cat = 0x0001;      // Conditional Access Table
    public const ushort Tsdt = 0x0002;     // Transport Stream Description Table
    public const ushort NullPacket = 0x1FFF; // Null packet (padding)
    
    // DVB-specific
    public const ushort Nit = 0x0010;      // Network Information Table
    public const ushort Sdt = 0x0011;      // Service Description Table
    public const ushort Eit = 0x0012;      // Event Information Table
    public const ushort Rst = 0x0013;      // Running Status Table
    public const ushort Tdt = 0x0014;      // Time and Date Table
}

/// <summary>
/// TS packet adaptation field flags.
/// </summary>
[Flags]
public enum TsAdaptationFlags : byte
{
    None = 0,
    DiscontinuityIndicator = 0x80,
    RandomAccessIndicator = 0x40,
    ElementaryStreamPriority = 0x20,
    PcrFlag = 0x10,
    OpcrFlag = 0x08,
    SplicingPointFlag = 0x04,
    TransportPrivateData = 0x02,
    AdaptationFieldExtension = 0x01
}

/// <summary>
/// Track type for MPEG-TS streams.
/// </summary>
public enum TsTrackType
{
    Unknown,
    Video,
    Audio,
    Subtitle,
    PrivateData
}

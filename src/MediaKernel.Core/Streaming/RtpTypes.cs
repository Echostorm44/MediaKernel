// RTP (Real-time Transport Protocol) Types
// Based on RFC 3550: RTP - A Transport Protocol for Real-Time Applications

namespace MediaKernel.Core.Streaming;

/// <summary>
/// RTP header structure (RFC 3550 Section 5.1).
/// Fixed 12-byte header plus optional CSRC and extension.
/// </summary>
public readonly struct RtpHeader
{
    /// <summary>RTP version (always 2).</summary>
    public int Version { get; init; }
    
    /// <summary>Padding flag - indicates padding octets at end of packet.</summary>
    public bool HasPadding { get; init; }
    
    /// <summary>Extension flag - indicates header extension follows fixed header.</summary>
    public bool HasExtension { get; init; }
    
    /// <summary>CSRC count (0-15) - number of CSRC identifiers following fixed header.</summary>
    public int CsrcCount { get; init; }
    
    /// <summary>Marker bit - interpretation defined by payload type.</summary>
    public bool Marker { get; init; }
    
    /// <summary>Payload type (0-127) - identifies payload format.</summary>
    public int PayloadType { get; init; }
    
    /// <summary>Sequence number (16-bit) - increments by one for each packet.</summary>
    public ushort SequenceNumber { get; init; }
    
    /// <summary>Timestamp (32-bit) - sampling instant of first octet.</summary>
    public uint Timestamp { get; init; }
    
    /// <summary>SSRC - synchronization source identifier.</summary>
    public uint Ssrc { get; init; }
    
    /// <summary>Total header length in bytes (12 + 4*CsrcCount + extension if present).</summary>
    public int HeaderLength { get; init; }
    
    /// <summary>Extension header ID (if HasExtension is true).</summary>
    public ushort ExtensionId { get; init; }
    
    /// <summary>Extension length in 32-bit words.</summary>
    public int ExtensionLength { get; init; }
}

/// <summary>
/// Parsed RTP packet.
/// </summary>
public readonly struct RtpPacket
{
    /// <summary>RTP header.</summary>
    public RtpHeader Header { get; init; }
    
    /// <summary>CSRC identifiers (Contributing Source).</summary>
    public uint[] CsrcList { get; init; }
    
    /// <summary>Payload data (excludes header and padding).</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
    
    /// <summary>Whether packet was parsed successfully.</summary>
    public bool IsValid { get; init; }
}

/// <summary>
/// Standard RTP payload types (RFC 3551).
/// </summary>
public enum RtpPayloadType
{
    PCMU = 0,        // G.711 μ-law
    Reserved1 = 1,
    Reserved2 = 2,
    GSM = 3,
    G723 = 4,
    DVI4_8K = 5,
    DVI4_16K = 6,
    LPC = 7,
    PCMA = 8,        // G.711 A-law
    G722 = 9,
    L16Stereo = 10,
    L16Mono = 11,
    QCELP = 12,
    CN = 13,         // Comfort noise
    MPA = 14,        // MPEG Audio
    G728 = 15,
    DVI4_11K = 16,
    DVI4_22K = 17,
    G729 = 18,
    Reserved19 = 19,
    Unassigned20 = 20,
    Unassigned21 = 21,
    Unassigned22 = 22,
    Unassigned23 = 23,
    Unassigned24 = 24,
    CelB = 25,
    JPEG = 26,
    Unassigned27 = 27,
    NV = 28,
    Unassigned29 = 29,
    Unassigned30 = 30,
    H261 = 31,
    MPV = 32,        // MPEG Video
    MP2T = 33,       // MPEG-2 TS
    H263 = 34,
    // 35-71: Unassigned
    // 72-76: Reserved
    // 77-95: Unassigned
    // 96-127: Dynamic
    DynamicMin = 96,
    DynamicMax = 127
}

/// <summary>
/// RTCP packet types (RFC 3550 Section 6).
/// </summary>
public enum RtcpPacketType
{
    /// <summary>Sender Report.</summary>
    SR = 200,
    
    /// <summary>Receiver Report.</summary>
    RR = 201,
    
    /// <summary>Source Description.</summary>
    SDES = 202,
    
    /// <summary>Goodbye.</summary>
    BYE = 203,
    
    /// <summary>Application-Defined.</summary>
    APP = 204
}

/// <summary>
/// RTCP Sender Report.
/// </summary>
public readonly struct RtcpSenderReport
{
    /// <summary>SSRC of sender.</summary>
    public uint Ssrc { get; init; }
    
    /// <summary>NTP timestamp (seconds since 1900).</summary>
    public ulong NtpTimestamp { get; init; }
    
    /// <summary>RTP timestamp corresponding to NTP timestamp.</summary>
    public uint RtpTimestamp { get; init; }
    
    /// <summary>Sender's packet count.</summary>
    public uint SenderPacketCount { get; init; }
    
    /// <summary>Sender's octet count.</summary>
    public uint SenderOctetCount { get; init; }
    
    /// <summary>Report blocks from other sources.</summary>
    public RtcpReportBlock[] ReportBlocks { get; init; }
}

/// <summary>
/// RTCP Receiver Report.
/// </summary>
public readonly struct RtcpReceiverReport
{
    /// <summary>SSRC of packet sender.</summary>
    public uint Ssrc { get; init; }
    
    /// <summary>Report blocks.</summary>
    public RtcpReportBlock[] ReportBlocks { get; init; }
}

/// <summary>
/// RTCP Report Block.
/// </summary>
public readonly struct RtcpReportBlock
{
    /// <summary>SSRC of source being reported.</summary>
    public uint Ssrc { get; init; }
    
    /// <summary>Fraction of packets lost (0-255 = 0%-100%).</summary>
    public byte FractionLost { get; init; }
    
    /// <summary>Cumulative number of packets lost.</summary>
    public int CumulativeLost { get; init; }
    
    /// <summary>Extended highest sequence number received.</summary>
    public uint ExtendedHighestSeq { get; init; }
    
    /// <summary>Interarrival jitter estimate.</summary>
    public uint Jitter { get; init; }
    
    /// <summary>Last SR timestamp (middle 32 bits of NTP).</summary>
    public uint LastSR { get; init; }
    
    /// <summary>Delay since last SR in 1/65536 seconds.</summary>
    public uint DelaySinceLastSR { get; init; }
}

/// <summary>
/// RTP source statistics for jitter buffer management.
/// </summary>
public sealed class RtpSourceStatistics
{
    /// <summary>SSRC identifier.</summary>
    public uint Ssrc { get; set; }
    
    /// <summary>Number of packets received.</summary>
    public long PacketsReceived { get; set; }
    
    /// <summary>Number of packets lost.</summary>
    public long PacketsLost { get; set; }
    
    /// <summary>Number of packets out of order.</summary>
    public long PacketsOutOfOrder { get; set; }
    
    /// <summary>Number of duplicate packets.</summary>
    public long PacketsDuplicate { get; set; }
    
    /// <summary>Total bytes received.</summary>
    public long BytesReceived { get; set; }
    
    /// <summary>Highest sequence number received.</summary>
    public uint HighestSequence { get; set; }
    
    /// <summary>Estimated jitter in RTP timestamp units.</summary>
    public double Jitter { get; set; }
    
    /// <summary>Last RTP timestamp received.</summary>
    public uint LastTimestamp { get; set; }
    
    /// <summary>Clock rate for this source.</summary>
    public int ClockRate { get; set; }
}

/// <summary>
/// Dynamic payload type mapping from SDP.
/// </summary>
public sealed class RtpPayloadMapping
{
    /// <summary>Payload type number (96-127 for dynamic).</summary>
    public int PayloadType { get; init; }
    
    /// <summary>Encoding name (e.g., "H264", "opus", "mpeg4-generic").</summary>
    public string EncodingName { get; init; } = string.Empty;
    
    /// <summary>Clock rate in Hz.</summary>
    public int ClockRate { get; init; }
    
    /// <summary>Number of channels (for audio).</summary>
    public int Channels { get; init; } = 1;
    
    /// <summary>Format-specific parameters from SDP a=fmtp line.</summary>
    public string? FormatParameters { get; init; }
    
    /// <summary>
    /// Gets whether this is a dynamic payload type.
    /// </summary>
    public bool IsDynamic => PayloadType >= 96 && PayloadType <= 127;
}

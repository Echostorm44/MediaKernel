// RTP Depacketizers for video/audio payloads
// H.264: RFC 6184, H.265: RFC 7798

using System.Buffers.Binary;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Base interface for RTP depacketizers.
/// </summary>
public interface IRtpDepacketizer
{
    /// <summary>
    /// Processes an RTP packet and returns any complete NAL units or frames.
    /// </summary>
    /// <param name="packet">The RTP packet to process.</param>
    /// <returns>Complete output units, empty if fragmentation in progress.</returns>
    IReadOnlyList<RtpDepacketizedUnit> Process(RtpPacket packet);
    
    /// <summary>
    /// Resets the depacketizer state (e.g., after seek or error).
    /// </summary>
    void Reset();
}

/// <summary>
/// Output from depacketizer - a complete NAL unit or access unit.
/// </summary>
public readonly struct RtpDepacketizedUnit
{
    /// <summary>NAL unit data with Annex B start code (0x00 0x00 0x00 0x01).</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>RTP timestamp.</summary>
    public uint Timestamp { get; init; }
    
    /// <summary>Whether this is the last unit in an access unit (marker bit).</summary>
    public bool IsAccessUnitEnd { get; init; }
}

/// <summary>
/// H.264 RTP depacketizer (RFC 6184).
/// Supports Single NAL Unit, STAP-A, and FU-A modes.
/// </summary>
public sealed class H264RtpDepacketizer : IRtpDepacketizer
{
    private static readonly byte[] AnnexBStartCode = [0, 0, 0, 1];
    
    // FU-A fragmentation state
    private MemoryStream? fragmentBuffer;
    private byte fragmentNalHeader;
    private uint fragmentTimestamp;
    private bool inFragment;
    
    /// <summary>
    /// Processes an H.264 RTP packet.
    /// </summary>
    public IReadOnlyList<RtpDepacketizedUnit> Process(RtpPacket packet)
    {
        if (!packet.IsValid || packet.Payload.Length == 0)
            return [];
        
        var payload = packet.Payload.Span;
        byte nalHeader = payload[0];
        int nalType = nalHeader & 0x1F;
        
        return nalType switch
        {
            // NAL unit types 1-23: Single NAL unit mode
            >= 1 and <= 23 => ProcessSingleNalUnit(payload, packet),
            
            // 24: STAP-A (Single-time aggregation packet type A)
            24 => ProcessStapA(payload, packet),
            
            // 28: FU-A (Fragmentation unit A)
            28 => ProcessFuA(payload, packet),
            
            // 25-27, 29: STAP-B, MTAP16, MTAP24, FU-B (not commonly used)
            _ => []
        };
    }
    
    private List<RtpDepacketizedUnit> ProcessSingleNalUnit(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        // Abort any incomplete fragment
        AbortFragment();
        
        // Prepend Annex B start code
        byte[] output = new byte[4 + payload.Length];
        AnnexBStartCode.CopyTo(output, 0);
        payload.CopyTo(output.AsSpan(4));
        
        return [new RtpDepacketizedUnit
        {
            Data = output,
            Timestamp = packet.Header.Timestamp,
            IsAccessUnitEnd = packet.Header.Marker
        }];
    }
    
    private List<RtpDepacketizedUnit> ProcessStapA(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        // Abort any incomplete fragment
        AbortFragment();
        
        var units = new List<RtpDepacketizedUnit>();
        int offset = 1; // Skip STAP-A header
        
        while (offset + 2 <= payload.Length)
        {
            // 2-byte NAL unit size (big endian)
            int nalSize = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            
            if (offset + nalSize > payload.Length)
                break;
            
            byte[] output = new byte[4 + nalSize];
            AnnexBStartCode.CopyTo(output, 0);
            payload.Slice(offset, nalSize).CopyTo(output.AsSpan(4));
            
            units.Add(new RtpDepacketizedUnit
            {
                Data = output,
                Timestamp = packet.Header.Timestamp,
                IsAccessUnitEnd = false
            });
            
            offset += nalSize;
        }
        
        // Mark last unit as end if marker bit set
        if (units.Count > 0 && packet.Header.Marker)
        {
            var last = units[^1];
            units[^1] = new RtpDepacketizedUnit
            {
                Data = last.Data,
                Timestamp = last.Timestamp,
                IsAccessUnitEnd = true
            };
        }
        
        return units;
    }
    
    private List<RtpDepacketizedUnit> ProcessFuA(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        if (payload.Length < 2)
            return [];
        
        byte fuIndicator = payload[0];
        byte fuHeader = payload[1];
        
        bool startBit = (fuHeader & 0x80) != 0;
        bool endBit = (fuHeader & 0x40) != 0;
        int nalType = fuHeader & 0x1F;
        
        // Reconstruct original NAL header: NRI from FU indicator, type from FU header
        byte reconstructedNalHeader = (byte)((fuIndicator & 0xE0) | nalType);
        
        if (startBit)
        {
            // Start of new fragment
            AbortFragment();
            
            fragmentBuffer = new MemoryStream();
            fragmentNalHeader = reconstructedNalHeader;
            fragmentTimestamp = packet.Header.Timestamp;
            inFragment = true;
            
            // Write NAL header
            fragmentBuffer.WriteByte(reconstructedNalHeader);
        }
        
        if (!inFragment || fragmentBuffer == null)
        {
            // Received middle/end without start - discard
            return [];
        }
        
        // Check timestamp continuity
        if (packet.Header.Timestamp != fragmentTimestamp)
        {
            AbortFragment();
            return [];
        }
        
        // Append fragment payload (skip FU indicator and header)
        fragmentBuffer.Write(payload.Slice(2));
        
        if (endBit)
        {
            // Fragment complete - output with Annex B start code
            byte[] nalData = fragmentBuffer.ToArray();
            byte[] output = new byte[4 + nalData.Length];
            AnnexBStartCode.CopyTo(output, 0);
            nalData.CopyTo(output, 4);
            
            AbortFragment();
            
            return [new RtpDepacketizedUnit
            {
                Data = output,
                Timestamp = packet.Header.Timestamp,
                IsAccessUnitEnd = packet.Header.Marker
            }];
        }
        
        return [];
    }
    
    private void AbortFragment()
    {
        fragmentBuffer?.Dispose();
        fragmentBuffer = null;
        inFragment = false;
    }
    
    /// <summary>
    /// Resets the depacketizer state.
    /// </summary>
    public void Reset()
    {
        AbortFragment();
    }
}

/// <summary>
/// H.265/HEVC RTP depacketizer (RFC 7798).
/// Supports Single NAL Unit, AP (Aggregation Packet), and FU (Fragmentation Unit) modes.
/// </summary>
public sealed class H265RtpDepacketizer : IRtpDepacketizer
{
    private static readonly byte[] AnnexBStartCode = [0, 0, 0, 1];
    
    // FU fragmentation state
    private MemoryStream? fragmentBuffer;
    private byte[] fragmentNalHeader = new byte[2];
    private uint fragmentTimestamp;
    private bool inFragment;
    
    /// <summary>
    /// Processes an H.265 RTP packet.
    /// </summary>
    public IReadOnlyList<RtpDepacketizedUnit> Process(RtpPacket packet)
    {
        if (!packet.IsValid || packet.Payload.Length < 2)
            return [];
        
        var payload = packet.Payload.Span;
        
        // HEVC uses 2-byte NAL header
        // PayloadHdr (16 bits): F(1) + Type(6) + LayerId(6) + TID(3)
        int nalType = (payload[0] >> 1) & 0x3F;
        
        return nalType switch
        {
            // NAL types 0-47: Single NAL unit mode
            >= 0 and <= 47 => ProcessSingleNalUnit(payload, packet),
            
            // 48: Aggregation Packet (AP)
            48 => ProcessAggregationPacket(payload, packet),
            
            // 49: Fragmentation Unit (FU)
            49 => ProcessFragmentationUnit(payload, packet),
            
            // 50: PACI (not commonly used)
            _ => []
        };
    }
    
    private List<RtpDepacketizedUnit> ProcessSingleNalUnit(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        AbortFragment();
        
        byte[] output = new byte[4 + payload.Length];
        AnnexBStartCode.CopyTo(output, 0);
        payload.CopyTo(output.AsSpan(4));
        
        return [new RtpDepacketizedUnit
        {
            Data = output,
            Timestamp = packet.Header.Timestamp,
            IsAccessUnitEnd = packet.Header.Marker
        }];
    }
    
    private List<RtpDepacketizedUnit> ProcessAggregationPacket(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        AbortFragment();
        
        var units = new List<RtpDepacketizedUnit>();
        int offset = 2; // Skip PayloadHdr
        
        while (offset + 2 <= payload.Length)
        {
            // Optional DONL/DOND skipped for simplicity (interleaving mode not supported)
            
            int nalSize = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
            
            if (offset + nalSize > payload.Length)
                break;
            
            byte[] output = new byte[4 + nalSize];
            AnnexBStartCode.CopyTo(output, 0);
            payload.Slice(offset, nalSize).CopyTo(output.AsSpan(4));
            
            units.Add(new RtpDepacketizedUnit
            {
                Data = output,
                Timestamp = packet.Header.Timestamp,
                IsAccessUnitEnd = false
            });
            
            offset += nalSize;
        }
        
        if (units.Count > 0 && packet.Header.Marker)
        {
            var last = units[^1];
            units[^1] = new RtpDepacketizedUnit
            {
                Data = last.Data,
                Timestamp = last.Timestamp,
                IsAccessUnitEnd = true
            };
        }
        
        return units;
    }
    
    private List<RtpDepacketizedUnit> ProcessFragmentationUnit(ReadOnlySpan<byte> payload, RtpPacket packet)
    {
        if (payload.Length < 3)
            return [];
        
        // PayloadHdr (2 bytes) + FU header (1 byte)
        byte fuHeader = payload[2];
        
        bool startBit = (fuHeader & 0x80) != 0;
        bool endBit = (fuHeader & 0x40) != 0;
        int nalType = fuHeader & 0x3F;
        
        if (startBit)
        {
            AbortFragment();
            
            fragmentBuffer = new MemoryStream();
            fragmentTimestamp = packet.Header.Timestamp;
            inFragment = true;
            
            // Reconstruct NAL header: 
            // First byte: F bit from PayloadHdr[0], NAL type, LayerId MSBs
            // Second byte: LayerId LSBs + TID from PayloadHdr[1]
            fragmentNalHeader[0] = (byte)((payload[0] & 0x81) | (nalType << 1));
            fragmentNalHeader[1] = payload[1];
            
            fragmentBuffer.Write(fragmentNalHeader);
        }
        
        if (!inFragment || fragmentBuffer == null)
            return [];
        
        if (packet.Header.Timestamp != fragmentTimestamp)
        {
            AbortFragment();
            return [];
        }
        
        // Append fragment payload (skip PayloadHdr + FU header)
        fragmentBuffer.Write(payload.Slice(3));
        
        if (endBit)
        {
            byte[] nalData = fragmentBuffer.ToArray();
            byte[] output = new byte[4 + nalData.Length];
            AnnexBStartCode.CopyTo(output, 0);
            nalData.CopyTo(output, 4);
            
            AbortFragment();
            
            return [new RtpDepacketizedUnit
            {
                Data = output,
                Timestamp = packet.Header.Timestamp,
                IsAccessUnitEnd = packet.Header.Marker
            }];
        }
        
        return [];
    }
    
    private void AbortFragment()
    {
        fragmentBuffer?.Dispose();
        fragmentBuffer = null;
        inFragment = false;
    }
    
    /// <summary>
    /// Resets the depacketizer state.
    /// </summary>
    public void Reset()
    {
        AbortFragment();
    }
}

/// <summary>
/// MPEG-4 Generic RTP depacketizer (RFC 3640).
/// Used for AAC audio in RTP.
/// </summary>
public sealed class Mpeg4GenericRtpDepacketizer : IRtpDepacketizer
{
    private readonly int sizeLength;
    private readonly int indexLength;
    private readonly int indexDeltaLength;
    
    /// <summary>
    /// Creates an MPEG-4 Generic depacketizer.
    /// </summary>
    /// <param name="sizeLength">AU-size field length in bits.</param>
    /// <param name="indexLength">AU-index field length in bits.</param>
    /// <param name="indexDeltaLength">AU-index-delta field length in bits.</param>
    public Mpeg4GenericRtpDepacketizer(int sizeLength = 13, int indexLength = 3, int indexDeltaLength = 3)
    {
        this.sizeLength = sizeLength;
        this.indexLength = indexLength;
        this.indexDeltaLength = indexDeltaLength;
    }
    
    /// <summary>
    /// Processes an MPEG-4 Generic RTP packet.
    /// </summary>
    public IReadOnlyList<RtpDepacketizedUnit> Process(RtpPacket packet)
    {
        if (!packet.IsValid || packet.Payload.Length < 2)
            return [];
        
        var payload = packet.Payload.Span;
        
        // AU-headers-length (16 bits) - in bits
        int auHeadersLengthBits = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(0, 2));
        int auHeadersLengthBytes = (auHeadersLengthBits + 7) / 8;
        
        if (payload.Length < 2 + auHeadersLengthBytes)
            return [];
        
        // Parse AU headers
        var auHeaders = ParseAuHeaders(payload.Slice(2, auHeadersLengthBytes), auHeadersLengthBits);
        
        if (auHeaders.Count == 0)
            return [];
        
        // Extract AU data
        var units = new List<RtpDepacketizedUnit>();
        int dataOffset = 2 + auHeadersLengthBytes;
        
        for (int i = 0; i < auHeaders.Count; i++)
        {
            int auSize = auHeaders[i];
            
            if (dataOffset + auSize > payload.Length)
                break;
            
            byte[] output = payload.Slice(dataOffset, auSize).ToArray();
            
            units.Add(new RtpDepacketizedUnit
            {
                Data = output,
                Timestamp = packet.Header.Timestamp,
                IsAccessUnitEnd = i == auHeaders.Count - 1 && packet.Header.Marker
            });
            
            dataOffset += auSize;
        }
        
        return units;
    }
    
    private List<int> ParseAuHeaders(ReadOnlySpan<byte> headerData, int totalBits)
    {
        var sizes = new List<int>();
        int bitOffset = 0;
        bool isFirst = true;
        
        while (bitOffset + sizeLength + (isFirst ? indexLength : indexDeltaLength) <= totalBits)
        {
            int size = ReadBits(headerData, bitOffset, sizeLength);
            bitOffset += sizeLength;
            
            // Skip index/index-delta (we don't support interleaving)
            bitOffset += isFirst ? indexLength : indexDeltaLength;
            isFirst = false;
            
            if (size > 0)
                sizes.Add(size);
        }
        
        return sizes;
    }
    
    private static int ReadBits(ReadOnlySpan<byte> data, int bitOffset, int numBits)
    {
        int result = 0;
        
        for (int i = 0; i < numBits; i++)
        {
            int byteIndex = (bitOffset + i) / 8;
            int bitIndex = 7 - ((bitOffset + i) % 8);
            
            if (byteIndex < data.Length)
            {
                if ((data[byteIndex] & (1 << bitIndex)) != 0)
                    result |= 1 << (numBits - 1 - i);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Resets the depacketizer state.
    /// </summary>
    public void Reset()
    {
        // No state to reset for AAC
    }
}

/// <summary>
/// Factory for creating RTP depacketizers based on payload mapping.
/// </summary>
public static class RtpDepacketizerFactory
{
    /// <summary>
    /// Creates an RTP depacketizer for the given payload mapping.
    /// </summary>
    public static IRtpDepacketizer? Create(RtpPayloadMapping mapping)
    {
        return mapping.EncodingName.ToUpperInvariant() switch
        {
            "H264" => new H264RtpDepacketizer(),
            "H265" or "HEVC" => new H265RtpDepacketizer(),
            "MPEG4-GENERIC" => CreateMpeg4Generic(mapping),
            _ => null
        };
    }
    
    /// <summary>
    /// Creates a depacketizer for a static payload type.
    /// </summary>
    public static IRtpDepacketizer? Create(RtpPayloadType payloadType)
    {
        return payloadType switch
        {
            // Static payload types don't need depacketization (raw PCM, etc.)
            _ => null
        };
    }
    
    private static IRtpDepacketizer CreateMpeg4Generic(RtpPayloadMapping mapping)
    {
        // Parse fmtp parameters for size-length, index-length, etc.
        int sizeLength = 13;
        int indexLength = 3;
        int indexDeltaLength = 3;
        
        if (!string.IsNullOrEmpty(mapping.FormatParameters))
        {
            var fmtp = mapping.FormatParameters;
            
            if (TryParseFmtpInt(fmtp, "sizelength", out int sl))
                sizeLength = sl;
            if (TryParseFmtpInt(fmtp, "indexlength", out int il))
                indexLength = il;
            if (TryParseFmtpInt(fmtp, "indexdeltalength", out int idl))
                indexDeltaLength = idl;
        }
        
        return new Mpeg4GenericRtpDepacketizer(sizeLength, indexLength, indexDeltaLength);
    }
    
    private static bool TryParseFmtpInt(string fmtp, string param, out int value)
    {
        value = 0;
        
        int idx = fmtp.IndexOf(param + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;
        
        idx += param.Length + 1;
        int endIdx = fmtp.IndexOfAny([';', ' ', ','], idx);
        if (endIdx < 0)
            endIdx = fmtp.Length;
        
        return int.TryParse(fmtp.AsSpan(idx, endIdx - idx), out value);
    }
}

// RTP (Real-time Transport Protocol) Parser
// Based on RFC 3550: RTP - A Transport Protocol for Real-Time Applications

using System.Buffers.Binary;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parser for RTP packets (RFC 3550).
/// </summary>
public static class RtpParser
{
    /// <summary>Minimum RTP header size (12 bytes).</summary>
    public const int MinHeaderSize = 12;
    
    /// <summary>RTP version (always 2).</summary>
    public const int RtpVersion = 2;
    
    /// <summary>
    /// Parses an RTP packet from raw data.
    /// </summary>
    /// <param name="data">Raw packet data.</param>
    /// <returns>Parsed RTP packet.</returns>
    public static RtpPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinHeaderSize)
        {
            return new RtpPacket { IsValid = false };
        }
        
        // First byte: V(2), P(1), X(1), CC(4)
        byte byte0 = data[0];
        int version = (byte0 >> 6) & 0x03;
        bool hasPadding = ((byte0 >> 5) & 0x01) == 1;
        bool hasExtension = ((byte0 >> 4) & 0x01) == 1;
        int csrcCount = byte0 & 0x0F;
        
        // Verify version
        if (version != RtpVersion)
        {
            return new RtpPacket { IsValid = false };
        }
        
        // Second byte: M(1), PT(7)
        byte byte1 = data[1];
        bool marker = ((byte1 >> 7) & 0x01) == 1;
        int payloadType = byte1 & 0x7F;
        
        // Sequence number (16 bits)
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        
        // Timestamp (32 bits)
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        
        // SSRC (32 bits)
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        
        int headerOffset = MinHeaderSize;
        
        // Check we have enough data for CSRC list
        int csrcSize = csrcCount * 4;
        if (data.Length < headerOffset + csrcSize)
        {
            return new RtpPacket { IsValid = false };
        }
        
        // Parse CSRC list
        uint[] csrcList = new uint[csrcCount];
        for (int i = 0; i < csrcCount; i++)
        {
            csrcList[i] = BinaryPrimitives.ReadUInt32BigEndian(
                data.Slice(headerOffset + (i * 4), 4));
        }
        headerOffset += csrcSize;
        
        // Parse extension header if present
        ushort extensionId = 0;
        int extensionLength = 0;
        
        if (hasExtension)
        {
            if (data.Length < headerOffset + 4)
            {
                return new RtpPacket { IsValid = false };
            }
            
            extensionId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(headerOffset, 2));
            extensionLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(headerOffset + 2, 2));
            
            // Extension length is in 32-bit words
            int extensionBytes = 4 + (extensionLength * 4);
            
            if (data.Length < headerOffset + extensionBytes)
            {
                return new RtpPacket { IsValid = false };
            }
            
            headerOffset += extensionBytes;
        }
        
        // Calculate payload size (excluding padding)
        int payloadSize = data.Length - headerOffset;
        
        if (hasPadding && payloadSize > 0)
        {
            // Last byte indicates padding length
            int paddingLength = data[data.Length - 1];
            if (paddingLength > payloadSize)
            {
                return new RtpPacket { IsValid = false };
            }
            payloadSize -= paddingLength;
        }
        
        if (payloadSize < 0)
        {
            return new RtpPacket { IsValid = false };
        }
        
        var header = new RtpHeader
        {
            Version = version,
            HasPadding = hasPadding,
            HasExtension = hasExtension,
            CsrcCount = csrcCount,
            Marker = marker,
            PayloadType = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
            HeaderLength = headerOffset,
            ExtensionId = extensionId,
            ExtensionLength = extensionLength
        };
        
        return new RtpPacket
        {
            Header = header,
            CsrcList = csrcList,
            Payload = data.Slice(headerOffset, payloadSize).ToArray(),
            IsValid = true
        };
    }
    
    /// <summary>
    /// Checks if data appears to be a valid RTP packet.
    /// </summary>
    public static bool IsRtpPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinHeaderSize)
            return false;
        
        int version = (data[0] >> 6) & 0x03;
        return version == RtpVersion;
    }
    
    /// <summary>
    /// Parses an RTCP Sender Report.
    /// </summary>
    public static RtcpSenderReport? ParseSenderReport(ReadOnlySpan<byte> data)
    {
        // Minimum SR size: 4 (header) + 4 (SSRC) + 20 (sender info) = 28 bytes
        if (data.Length < 28)
            return null;
        
        byte byte0 = data[0];
        int version = (byte0 >> 6) & 0x03;
        int reportCount = byte0 & 0x1F;
        int packetType = data[1];
        
        if (version != 2 || packetType != (int)RtcpPacketType.SR)
            return null;
        
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        
        // NTP timestamp (64 bits)
        uint ntpSec = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        uint ntpFrac = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4));
        ulong ntpTimestamp = ((ulong)ntpSec << 32) | ntpFrac;
        
        uint rtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        uint packetCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        uint octetCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24, 4));
        
        // Parse report blocks
        var reportBlocks = new RtcpReportBlock[reportCount];
        int offset = 28;
        
        for (int i = 0; i < reportCount && offset + 24 <= data.Length; i++)
        {
            reportBlocks[i] = ParseReportBlock(data.Slice(offset, 24));
            offset += 24;
        }
        
        return new RtcpSenderReport
        {
            Ssrc = ssrc,
            NtpTimestamp = ntpTimestamp,
            RtpTimestamp = rtpTimestamp,
            SenderPacketCount = packetCount,
            SenderOctetCount = octetCount,
            ReportBlocks = reportBlocks
        };
    }
    
    /// <summary>
    /// Parses an RTCP Receiver Report.
    /// </summary>
    public static RtcpReceiverReport? ParseReceiverReport(ReadOnlySpan<byte> data)
    {
        // Minimum RR size: 4 (header) + 4 (SSRC) = 8 bytes
        if (data.Length < 8)
            return null;
        
        byte byte0 = data[0];
        int version = (byte0 >> 6) & 0x03;
        int reportCount = byte0 & 0x1F;
        int packetType = data[1];
        
        if (version != 2 || packetType != (int)RtcpPacketType.RR)
            return null;
        
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        
        var reportBlocks = new RtcpReportBlock[reportCount];
        int offset = 8;
        
        for (int i = 0; i < reportCount && offset + 24 <= data.Length; i++)
        {
            reportBlocks[i] = ParseReportBlock(data.Slice(offset, 24));
            offset += 24;
        }
        
        return new RtcpReceiverReport
        {
            Ssrc = ssrc,
            ReportBlocks = reportBlocks
        };
    }
    
    private static RtcpReportBlock ParseReportBlock(ReadOnlySpan<byte> data)
    {
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
        byte fractionLost = data[4];
        
        // Cumulative lost is 24-bit signed integer
        int cumulativeLost = (data[5] << 16) | (data[6] << 8) | data[7];
        if ((data[5] & 0x80) != 0) // Sign extend
            cumulativeLost |= unchecked((int)0xFF000000);
        
        uint extHighestSeq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        uint jitter = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4));
        uint lastSr = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        uint dlsr = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        
        return new RtcpReportBlock
        {
            Ssrc = ssrc,
            FractionLost = fractionLost,
            CumulativeLost = cumulativeLost,
            ExtendedHighestSeq = extHighestSeq,
            Jitter = jitter,
            LastSR = lastSr,
            DelaySinceLastSR = dlsr
        };
    }
    
    /// <summary>
    /// Gets the RTCP packet type from raw data.
    /// </summary>
    public static RtcpPacketType? GetRtcpPacketType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return null;
        
        int packetType = data[1];
        
        return packetType switch
        {
            200 => RtcpPacketType.SR,
            201 => RtcpPacketType.RR,
            202 => RtcpPacketType.SDES,
            203 => RtcpPacketType.BYE,
            204 => RtcpPacketType.APP,
            _ => null
        };
    }
    
    /// <summary>
    /// Computes sequence number difference handling wrap-around.
    /// </summary>
    /// <param name="a">First sequence number.</param>
    /// <param name="b">Second sequence number.</param>
    /// <returns>Signed difference (a - b) in range [-32768, 32767].</returns>
    public static short SequenceDifference(ushort a, ushort b)
    {
        return (short)(a - b);
    }
    
    /// <summary>
    /// Checks if sequence number a is after b (handles wrap-around).
    /// </summary>
    public static bool IsSequenceAfter(ushort a, ushort b)
    {
        return SequenceDifference(a, b) > 0;
    }
}

/// <summary>
/// RTP sequence tracker for reordering and loss detection.
/// </summary>
public sealed class RtpSequenceTracker
{
    private const int MaxMisorder = 100;
    private const int MaxDropout = 3000;
    
    private ushort maxSeq;
    private uint cycles;
    private uint baseSeq;
    private uint badSeq;
    private uint probation;
    private uint received;
    private uint expectedPrior;
    private uint receivedPrior;
    private bool initialized;
    
    /// <summary>
    /// Processes a received sequence number.
    /// </summary>
    /// <param name="seq">Received sequence number.</param>
    /// <returns>True if packet should be processed, false if it's a duplicate or too old.</returns>
    public bool UpdateSequence(ushort seq)
    {
        if (!initialized)
        {
            InitSequence(seq);
            received++;
            return true;
        }
        
        ushort udelta = (ushort)(seq - maxSeq);
        
        if (udelta < MaxDropout)
        {
            // In order, with permissible gap
            if (seq < maxSeq)
            {
                // Sequence number wrapped
                cycles += 0x10000;
            }
            maxSeq = seq;
        }
        else if (udelta <= 0xFFFF - MaxMisorder)
        {
            // The sequence number made a very large jump
            if (seq == badSeq)
            {
                // Two sequential packets, assume new cycle
                InitSequence(seq);
            }
            else
            {
                badSeq = (uint)((seq + 1) & 0xFFFF);
                return false;
            }
        }
        else
        {
            // Duplicate or reordered packet
        }
        
        received++;
        return true;
    }
    
    private void InitSequence(ushort seq)
    {
        baseSeq = seq;
        maxSeq = seq;
        badSeq = 0x10000 + 1; // Invalid
        cycles = 0;
        received = 0;
        receivedPrior = 0;
        expectedPrior = 0;
        initialized = true;
    }
    
    /// <summary>
    /// Gets the extended highest sequence number (includes cycles).
    /// </summary>
    public uint ExtendedHighestSequence => cycles + maxSeq;
    
    /// <summary>
    /// Gets the total packets expected.
    /// </summary>
    public uint ExpectedPackets => ExtendedHighestSequence - baseSeq + 1;
    
    /// <summary>
    /// Gets the total packets received.
    /// </summary>
    public uint ReceivedPackets => received;
    
    /// <summary>
    /// Gets the number of lost packets.
    /// </summary>
    public int LostPackets => (int)(ExpectedPackets - received);
    
    /// <summary>
    /// Computes fraction of packets lost for RTCP report (0-255).
    /// </summary>
    public byte ComputeFractionLost()
    {
        uint expected = ExpectedPackets - expectedPrior;
        expectedPrior = ExpectedPackets;
        
        uint receivedInterval = received - receivedPrior;
        receivedPrior = received;
        
        int lost = (int)(expected - receivedInterval);
        
        if (expected == 0 || lost <= 0)
            return 0;
        
        return (byte)((lost << 8) / expected);
    }
}

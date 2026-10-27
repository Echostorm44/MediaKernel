// MediaKernel - AV1 Packetizer
// Parses AV1 Open Bitstream Units (OBUs) for frame assembly and codec configuration.
// Based on AV1 specification and VLC's av1_obu.h/av1.c

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// AV1 Open Bitstream Unit types per AV1 specification.
/// </summary>
public enum Av1ObuType : byte
{
    Reserved0 = 0,
    SequenceHeader = 1,
    TemporalDelimiter = 2,
    FrameHeader = 3,
    TileGroup = 4,
    Metadata = 5,
    Frame = 6,
    RedundantFrameHeader = 7,
    TileList = 8,
    Reserved9 = 9,
    Reserved10 = 10,
    Reserved11 = 11,
    Reserved12 = 12,
    Reserved13 = 13,
    Reserved14 = 14,
    Padding = 15
}

/// <summary>
/// AV1 metadata types for OBU_METADATA.
/// </summary>
public enum Av1MetadataType : uint
{
    HdrContentLightLevel = 1,
    HdrMasteringDisplayColorVolume = 2,
    Scalability = 3,
    ItutT35 = 4,
    Timecode = 5
}

/// <summary>
/// Parsed AV1 OBU header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Av1ObuHeader
{
    /// <summary>OBU type.</summary>
    public readonly Av1ObuType Type;
    
    /// <summary>Whether the OBU has a size field.</summary>
    public readonly bool HasSizeField;
    
    /// <summary>Whether the OBU has an extension field.</summary>
    public readonly bool HasExtension;
    
    /// <summary>Total header size in bytes (1 or 2).</summary>
    public readonly byte HeaderSize;
    
    /// <summary>Temporal ID from extension (0-7).</summary>
    public readonly byte TemporalId;
    
    /// <summary>Spatial ID from extension (0-3).</summary>
    public readonly byte SpatialId;
    
    /// <summary>OBU payload size if HasSizeField is true.</summary>
    public readonly ulong PayloadSize;
    
    /// <summary>Size of the LEB128 encoded size field (0 if not present).</summary>
    public readonly byte SizeFieldLength;
    
    public Av1ObuHeader(Av1ObuType type, bool hasSizeField, bool hasExtension,
        byte headerSize, byte temporalId, byte spatialId, ulong payloadSize, byte sizeFieldLength)
    {
        Type = type;
        HasSizeField = hasSizeField;
        HasExtension = hasExtension;
        HeaderSize = headerSize;
        TemporalId = temporalId;
        SpatialId = spatialId;
        PayloadSize = payloadSize;
        SizeFieldLength = sizeFieldLength;
    }
    
    /// <summary>Total OBU size including header and size field.</summary>
    public int TotalObuSize => HeaderSize + SizeFieldLength + (int)PayloadSize;
    
    /// <summary>Whether this OBU is from the base layer.</summary>
    public bool IsBaseLayer => !HasExtension || (TemporalId == 0 && SpatialId == 0);
}

/// <summary>
/// AV1 Sequence Header parameters extracted from OBU_SEQUENCE_HEADER.
/// </summary>
public struct Av1SequenceHeader
{
    public byte SeqProfile;
    public bool StillPicture;
    public bool ReducedStillPictureHeader;
    public bool TimingInfoPresent;
    public bool DecoderModelInfoPresent;
    public bool InitialDisplayDelayPresent;
    public int OperatingPointsCntMinus1;
    
    // Frame dimensions
    public int MaxFrameWidthMinus1;
    public int MaxFrameHeightMinus1;
    public bool FrameIdNumbersPresent;
    
    // Video characteristics
    public bool Use128x128Superblock;
    public bool EnableFilterIntra;
    public bool EnableIntraEdgeFilter;
    public bool EnableInterintraCompound;
    public bool EnableMaskedCompound;
    public bool EnableWarpedMotion;
    public bool EnableDualFilter;
    public bool EnableOrderHint;
    public bool EnableJntComp;
    public bool EnableRefFrameMvs;
    public bool SeqChooseScreenContentTools;
    public bool SeqForceScreenContentTools;
    public bool SeqChooseIntegerMv;
    public bool SeqForceIntegerMv;
    public byte OrderHintBits;
    public bool EnableSuperRes;
    public bool EnableCdef;
    public bool EnableRestoration;
    
    // Color config
    public bool HighBitDepth;
    public bool TwelveBit;
    public bool MonoChrome;
    public bool ColorDescriptionPresent;
    public byte ColorPrimaries;
    public byte TransferCharacteristics;
    public byte MatrixCoefficients;
    public bool ColorRange;
    public byte SubsamplingX;
    public byte SubsamplingY;
    public byte ChromaSamplePosition;
    public bool SeparateUvDeltaQ;
    
    public int MaxFrameWidth => MaxFrameWidthMinus1 + 1;
    public int MaxFrameHeight => MaxFrameHeightMinus1 + 1;
    public int BitDepth => HighBitDepth ? (TwelveBit ? 12 : 10) : 8;
}

/// <summary>
/// Result of parsing an AV1 temporal unit (access unit).
/// </summary>
public readonly struct Av1TemporalUnit
{
    /// <summary>Raw OBU data for this temporal unit.</summary>
    public readonly ReadOnlyMemory<byte> Data;
    
    /// <summary>Whether a sequence header is present.</summary>
    public readonly bool HasSequenceHeader;
    
    /// <summary>Whether a visible frame is present.</summary>
    public readonly bool HasVisibleFrame;
    
    /// <summary>Whether this is a keyframe.</summary>
    public readonly bool IsKeyframe;
    
    public Av1TemporalUnit(ReadOnlyMemory<byte> data, bool hasSequenceHeader, bool hasVisibleFrame, bool isKeyframe)
    {
        Data = data;
        HasSequenceHeader = hasSequenceHeader;
        HasVisibleFrame = hasVisibleFrame;
        IsKeyframe = isKeyframe;
    }
}

/// <summary>
/// AV1 packetizer for parsing OBU streams and assembling temporal units.
/// Handles OBU demarcation, sequence header extraction, and frame detection.
/// </summary>
public sealed class Av1Packetizer
{
    private readonly List<byte> pendingData = new();
    private Av1SequenceHeader? sequenceHeader;
    private bool sequenceHeaderChanged;
    
    /// <summary>Gets the current sequence header if parsed.</summary>
    public Av1SequenceHeader? SequenceHeader => sequenceHeader;
    
    /// <summary>Whether the sequence header changed since last read.</summary>
    public bool SequenceHeaderChanged
    {
        get
        {
            bool changed = sequenceHeaderChanged;
            sequenceHeaderChanged = false;
            return changed;
        }
    }
    
    /// <summary>
    /// Parse LEB128 (Little Endian Base 128) encoded value.
    /// Used for OBU size fields.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ParseLeb128(ReadOnlySpan<byte> data, out byte bytesRead)
    {
        ulong value = 0;
        bytesRead = 0;
        
        for (int i = 0; i < 8 && i < data.Length; i++)
        {
            byte b = data[i];
            value |= (ulong)(b & 0x7F) << (i * 7);
            
            if ((b & 0x80) == 0)
            {
                bytesRead = (byte)(i + 1);
                return value;
            }
        }
        
        return 0; // Invalid LEB128
    }
    
    /// <summary>
    /// Parse an OBU header from the given data.
    /// </summary>
    public static bool TryParseObuHeader(ReadOnlySpan<byte> data, out Av1ObuHeader header)
    {
        header = default;
        
        if (data.Length < 1)
            return false;
        
        byte firstByte = data[0];
        
        // obu_forbidden_bit must be 0
        if ((firstByte & 0x80) != 0)
            return false;
        
        var type = (Av1ObuType)((firstByte >> 3) & 0x0F);
        bool hasExtension = (firstByte & 0x04) != 0;
        bool hasSizeField = (firstByte & 0x02) != 0;
        
        // obu_reserved_1bit must be 0
        if ((firstByte & 0x01) != 0)
            return false;
        
        byte headerSize = 1;
        byte temporalId = 0;
        byte spatialId = 0;
        
        if (hasExtension)
        {
            if (data.Length < 2)
                return false;
            
            byte extByte = data[1];
            temporalId = (byte)((extByte >> 5) & 0x07);
            spatialId = (byte)((extByte >> 3) & 0x03);
            // extension_header_reserved_3bits must be 0
            if ((extByte & 0x07) != 0)
                return false;
            
            headerSize = 2;
        }
        
        ulong payloadSize = 0;
        byte sizeFieldLength = 0;
        
        if (hasSizeField)
        {
            if (data.Length < headerSize + 1)
                return false;
            
            payloadSize = ParseLeb128(data.Slice(headerSize), out sizeFieldLength);
            if (sizeFieldLength == 0)
                return false;
        }
        
        header = new Av1ObuHeader(type, hasSizeField, hasExtension, headerSize,
            temporalId, spatialId, payloadSize, sizeFieldLength);
        return true;
    }
    
    /// <summary>
    /// Check if OBU data is valid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidObu(ReadOnlySpan<byte> data)
    {
        return data.Length > 0 && (data[0] & 0x81) == 0;
    }
    
    /// <summary>
    /// Get the OBU type from the first byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Av1ObuType GetObuType(byte firstByte)
    {
        return (Av1ObuType)((firstByte >> 3) & 0x0F);
    }
    
    /// <summary>
    /// Feed raw AV1 data to the packetizer.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        pendingData.AddRange(data.ToArray());
    }
    
    /// <summary>
    /// Try to read the next complete temporal unit (access unit).
    /// </summary>
    public bool TryReadTemporalUnit(out Av1TemporalUnit temporalUnit)
    {
        temporalUnit = default;
        
        if (pendingData.Count == 0)
            return false;
        
        var data = pendingData.ToArray().AsSpan();
        int offset = 0;
        bool hasSequenceHeader = false;
        bool hasVisibleFrame = false;
        bool isKeyframe = false;
        bool foundTemporalDelimiter = false;
        int tuEndOffset = 0;
        
        // Parse OBUs until we find a complete temporal unit
        while (offset < data.Length)
        {
            if (!TryParseObuHeader(data.Slice(offset), out var header))
                break;
            
            int obuTotalSize = header.TotalObuSize;
            if (offset + obuTotalSize > data.Length)
                break; // Incomplete OBU
            
            // Temporal delimiter marks the start of a new TU
            if (header.Type == Av1ObuType.TemporalDelimiter)
            {
                if (foundTemporalDelimiter)
                {
                    // Found second TD - previous TU is complete
                    tuEndOffset = offset;
                    break;
                }
                foundTemporalDelimiter = true;
            }
            
            // Process based on OBU type
            switch (header.Type)
            {
                case Av1ObuType.SequenceHeader:
                    hasSequenceHeader = true;
                    int payloadOffset = offset + header.HeaderSize + header.SizeFieldLength;
                    TryParseSequenceHeader(data.Slice(payloadOffset, (int)header.PayloadSize));
                    break;
                
                case Av1ObuType.Frame:
                case Av1ObuType.FrameHeader:
                    hasVisibleFrame = true;
                    // Check for keyframe (frame_type in frame header)
                    // Simplified: if sequence header is present, assume keyframe
                    if (hasSequenceHeader)
                        isKeyframe = true;
                    break;
            }
            
            offset += obuTotalSize;
        }
        
        // If we parsed at least one complete OBU with visible frame
        if (hasVisibleFrame && offset > 0)
        {
            int endOffset = tuEndOffset > 0 ? tuEndOffset : offset;
            var tuData = new byte[endOffset];
            pendingData.CopyTo(0, tuData, 0, endOffset);
            pendingData.RemoveRange(0, endOffset);
            
            temporalUnit = new Av1TemporalUnit(tuData, hasSequenceHeader, hasVisibleFrame, isKeyframe);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Parse OBUs from a complete buffer (no temporal unit assembly needed).
    /// </summary>
    public List<Av1ObuHeader> ParseObus(ReadOnlySpan<byte> data)
    {
        var result = new List<Av1ObuHeader>();
        int offset = 0;
        
        while (offset < data.Length)
        {
            if (!TryParseObuHeader(data.Slice(offset), out var header))
                break;
            
            result.Add(header);
            offset += header.TotalObuSize;
        }
        
        return result;
    }
    
    /// <summary>
    /// Extract codec configuration from AV1 data (first sequence header OBU).
    /// Returns the av1C box configuration data for MP4/MKV containers.
    /// </summary>
    public static byte[]? ExtractCodecConfiguration(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        
        while (offset < data.Length)
        {
            if (!TryParseObuHeader(data.Slice(offset), out var header))
                break;
            
            if (header.Type == Av1ObuType.SequenceHeader)
            {
                // av1C format: 4 bytes header + sequence header OBU
                int obuSize = header.TotalObuSize;
                var config = new byte[4 + obuSize];
                
                // TODO: Parse sequence header to populate av1C fields properly
                // For now, just include the raw sequence header OBU
                config[0] = 0x81; // marker + version
                config[1] = 0x00; // seq_profile + seq_level_idx
                config[2] = 0x00; // seq_tier + high_bitdepth + twelve_bit + monochrome + chroma_subsampling
                config[3] = 0x00; // chroma_sample_position + initial_presentation_delay
                
                data.Slice(offset, obuSize).CopyTo(config.AsSpan(4));
                return config;
            }
            
            offset += header.TotalObuSize;
        }
        
        return null;
    }
    
    /// <summary>
    /// Parse sequence header OBU payload.
    /// </summary>
    private void TryParseSequenceHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            return;
        
        // Simple parsing of key fields using bit reader
        int bitOffset = 0;
        
        // seq_profile (3 bits)
        byte seqProfile = (byte)((payload[0] >> 5) & 0x07);
        
        // still_picture (1 bit)
        bool stillPicture = ((payload[0] >> 4) & 0x01) != 0;
        
        // reduced_still_picture_header (1 bit)
        bool reducedStillPictureHeader = ((payload[0] >> 3) & 0x01) != 0;
        
        var newHeader = new Av1SequenceHeader
        {
            SeqProfile = seqProfile,
            StillPicture = stillPicture,
            ReducedStillPictureHeader = reducedStillPictureHeader
        };
        
        // Check if sequence header changed
        if (sequenceHeader.HasValue)
        {
            var old = sequenceHeader.Value;
            if (old.SeqProfile != newHeader.SeqProfile ||
                old.StillPicture != newHeader.StillPicture)
            {
                sequenceHeaderChanged = true;
            }
        }
        else
        {
            sequenceHeaderChanged = true;
        }
        
        sequenceHeader = newHeader;
    }
    
    /// <summary>
    /// Reset packetizer state.
    /// </summary>
    public void Reset()
    {
        pendingData.Clear();
        sequenceHeader = null;
        sequenceHeaderChanged = false;
    }
}

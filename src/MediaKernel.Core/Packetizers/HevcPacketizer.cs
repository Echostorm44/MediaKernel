// HevcPacketizer - Converts demuxer output into complete HEVC access units
// Handles hvcC/Annex-B conversion, VPS/SPS/PPS management, and AU boundary detection

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.H264; // BitstreamReader
using MediaKernel.Core.Codecs.H265;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// H.265/HEVC packetizer that prepares demuxer output for decoder consumption.
/// </summary>
public sealed class HevcPacketizer : IDisposable
{
    private readonly List<byte[]> pendingNalUnits = new();
    private readonly List<AccessUnit> outputQueue = new();
    
    // Carries incomplete NAL data across chunk boundaries in Annex B mode.
    private byte[]? annexBPartialNal;
    
    // Global "latest" PS — updated whenever a PS NAL is encountered
    private byte[]? currentVps;
    private byte[]? currentSps;
    private byte[]? currentPps;
    
    // Latest PS NAL per parameter set ID — needed when a stream contains
    // multiple parameter sets with different IDs (e.g., SPS ID 0 and SPS ID 1).
    // A single currentSps/currentPps would lose earlier entries.
    // Using ID-based maps (not content-unique lists) ensures that when PS content
    // cycles for the same ID (e.g., DBLK_B with 30 PPSes all ID=0), only the
    // latest version is injected.
    private readonly Dictionary<int, byte[]> vpsByPsId = new();
    private readonly Dictionary<int, byte[]> spsByPsId = new();
    private readonly Dictionary<int, byte[]> ppsByPsId = new();
    
    // Snapshotted PS for the current AU being built — captured when first VCL arrives
    private byte[]? auVps;
    private byte[]? auSps;
    private byte[]? auPps;
    private Dictionary<int, byte[]>? auAllVps;
    private Dictionary<int, byte[]>? auAllSps;
    private Dictionary<int, byte[]>? auAllPps;
    
    // Last PS that was injected into an AU — used to detect PS changes via reference equality
    private byte[]? lastInjectedVps;
    private byte[]? lastInjectedSps;
    private byte[]? lastInjectedPps;
    
    private int hvccLengthSize = 4;
    private bool isHvccFormat;
    
    // Timestamp interpolation
    private long lastDts = long.MinValue;
    private long lastPts = long.MinValue;
    private long frameDurationTicks = TimeSpan.TicksPerSecond / 30; // Default 30 fps
    private double frameRate = 30.0;
    
    /// <summary>Gets the current VPS data.</summary>
    public ReadOnlySpan<byte> CurrentVps => currentVps;
    
    /// <summary>Gets the current SPS data.</summary>
    public ReadOnlySpan<byte> CurrentSps => currentSps;
    
    /// <summary>Gets the current PPS data.</summary>
    public ReadOnlySpan<byte> CurrentPps => currentPps;
    
    /// <summary>Gets or sets whether to inject VPS/SPS/PPS before keyframes.</summary>
    public bool InjectParameterSets { get; set; } = true;
    
    /// <summary>Gets or sets the frame rate for timestamp interpolation.</summary>
    public double DefaultFrameRate
    {
        get => frameRate;
        set
        {
            frameRate = value > 0 ? value : 30.0;
            frameDurationTicks = (long)(TimeSpan.TicksPerSecond / frameRate);
        }
    }
    
    /// <summary>
    /// Configures the packetizer for hvcC format (MP4/MOV containers).
    /// </summary>
    /// <param name="hvccData">HEVCDecoderConfigurationRecord from hvcC box.</param>
    public void ConfigureHvcc(ReadOnlySpan<byte> hvccData)
    {
        if (hvccData.Length < 23)
            return;
        
        // HEVCDecoderConfigurationRecord:
        // configurationVersion (1)
        // general_profile_space/tier/profile (12 bytes)
        // general_constraint_indicator_flags (6 bytes)
        // general_level_idc (1)
        // ... more fields ...
        // lengthSizeMinusOne (1)
        // numOfArrays (1)
        // arrays[]
        
        isHvccFormat = true;
        hvccLengthSize = (hvccData[21] & 0x03) + 1;
        
        int numArrays = hvccData[22];
        int offset = 23;
        
        for (int i = 0; i < numArrays && offset < hvccData.Length; i++)
        {
            if (offset + 3 > hvccData.Length)
                break;
            
            int nalType = hvccData[offset] & 0x3F;
            int numNalus = BinaryPrimitives.ReadUInt16BigEndian(hvccData.Slice(offset + 1, 2));
            offset += 3;
            
            for (int j = 0; j < numNalus && offset + 2 <= hvccData.Length; j++)
            {
                int nalLength = BinaryPrimitives.ReadUInt16BigEndian(hvccData.Slice(offset, 2));
                offset += 2;
                
                if (offset + nalLength > hvccData.Length)
                    break;
                
                var nalData = hvccData.Slice(offset, nalLength).ToArray();
                offset += nalLength;
                
                // Store based on NAL type
                var hevcNalType = (HevcNalUnitType)nalType;
                switch (hevcNalType)
                {
                    case HevcNalUnitType.VideoParameterSet:
                        currentVps = nalData;
                        vpsByPsId[ParseVpsId(nalData)] = nalData;
                        break;
                    case HevcNalUnitType.SequenceParameterSet:
                        currentSps = nalData;
                        spsByPsId[ParseSpsId(nalData)] = nalData;
                        break;
                    case HevcNalUnitType.PictureParameterSet:
                        currentPps = nalData;
                        ppsByPsId[ParsePpsId(nalData)] = nalData;
                        break;
                }
            }
        }
        
        frameDurationTicks = (long)(TimeSpan.TicksPerSecond / frameRate);
    }
    
    /// <summary>
    /// Configures the packetizer for Annex-B format.
    /// </summary>
    public void ConfigureAnnexB()
    {
        isHvccFormat = false;
        frameDurationTicks = (long)(TimeSpan.TicksPerSecond / frameRate);
    }
    
    /// <summary>
    /// Feeds raw data from the demuxer into the packetizer.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, long pts, long dts, bool isKeyframe)
    {
        if (isHvccFormat)
        {
            ParseHvccData(data, pts, dts);
        }
        else
        {
            ParseAnnexBData(data, pts, dts);
        }
    }
    
    /// <summary>
    /// Retrieves the next complete access unit.
    /// </summary>
    public bool TryGetAccessUnit(out AccessUnit accessUnit)
    {
        if (outputQueue.Count > 0)
        {
            accessUnit = outputQueue[0];
            outputQueue.RemoveAt(0);
            return true;
        }
        
        accessUnit = default;
        return false;
    }
    
    /// <summary>
    /// Flushes pending NAL units.
    /// </summary>
    public void Flush()
    {
        if (annexBPartialNal != null && annexBPartialNal.Length > 0)
        {
            ProcessNalUnit(annexBPartialNal, lastPts, lastDts);
            annexBPartialNal = null;
        }
        
        if (pendingNalUnits.Count > 0)
        {
            EmitAccessUnit(lastPts, lastDts, CheckIfKeyframe());
        }
    }
    
    /// <summary>
    /// Resets packetizer state.
    /// </summary>
    public void Reset()
    {
        pendingNalUnits.Clear();
        outputQueue.Clear();
        annexBPartialNal = null;
        lastDts = long.MinValue;
        lastPts = long.MinValue;
        lastInjectedVps = null;
        lastInjectedSps = null;
        lastInjectedPps = null;
        // Don't clear vpsByPsId/spsByPsId/ppsByPsId — they accumulate across the stream
        // and are needed for correct injection after seek
    }
    
    private void ParseHvccData(ReadOnlySpan<byte> data, long pts, long dts)
    {
        int offset = 0;
        
        while (offset + hvccLengthSize <= data.Length)
        {
            int nalLength = hvccLengthSize switch
            {
                1 => data[offset],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)),
                4 => (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)),
                _ => 0
            };
            
            offset += hvccLengthSize;
            
            if (nalLength <= 0 || offset + nalLength > data.Length)
                break;
            
            var nalData = data.Slice(offset, nalLength).ToArray();
            ProcessNalUnit(nalData, pts, dts);
            offset += nalLength;
        }
    }
    
    private void ParseAnnexBData(ReadOnlySpan<byte> data, long pts, long dts)
    {
        // Combine any leftover partial NAL from the previous chunk with new data.
        ReadOnlySpan<byte> workData;
        byte[]? combinedBuffer = null;
        // When a start code falls exactly at a chunk boundary, nalStart == workData.Length
        // (the NAL header begins in the next chunk). We must still save this as a partial NAL
        // so the next chunk combines correctly with nalStart = 0.
        bool hadPartialNal = annexBPartialNal != null;
        
        if (hadPartialNal)
        {
            if (annexBPartialNal!.Length > 0)
            {
                combinedBuffer = new byte[annexBPartialNal.Length + data.Length];
                annexBPartialNal.AsSpan().CopyTo(combinedBuffer);
                data.CopyTo(combinedBuffer.AsSpan(annexBPartialNal.Length));
                workData = combinedBuffer;
            }
            else
            {
                // Empty partial: start code ended exactly at previous chunk boundary.
                // New data starts the NAL from byte 0.
                workData = data;
            }
            annexBPartialNal = null;
        }
        else
        {
            workData = data;
        }
        
        int offset = 0;
        int nalStart = hadPartialNal ? 0 : -1;
        
        while (offset <= workData.Length - 3)
        {
            bool isStartCode3 = workData[offset] == 0 && workData[offset + 1] == 0 && workData[offset + 2] == 1;
            bool isStartCode4 = !isStartCode3 && offset <= workData.Length - 4 &&
                                workData[offset] == 0 && workData[offset + 1] == 0 &&
                                workData[offset + 2] == 0 && workData[offset + 3] == 1;
            
            if (isStartCode3 || isStartCode4)
            {
                if (nalStart >= 0)
                {
                    int nalEnd = offset;
                    while (nalEnd > nalStart && workData[nalEnd - 1] == 0)
                        nalEnd--;
                    
                    if (nalEnd > nalStart)
                        ProcessNalUnit(workData.Slice(nalStart, nalEnd - nalStart).ToArray(), pts, dts);
                }
                
                offset += isStartCode3 ? 3 : 4;
                nalStart = offset;
            }
            else
            {
                offset++;
            }
        }
        
        if (nalStart >= 0)
        {
            annexBPartialNal = workData.Slice(nalStart, workData.Length - nalStart).ToArray();
        }
        else if (nalStart < 0 && hadPartialNal)
        {
            annexBPartialNal = workData.ToArray();
        }
    }
    
    private void ProcessNalUnit(byte[] nalData, long pts, long dts)
    {
        if (nalData.Length < 2)
            return;
        
        // HEVC NAL header is 2 bytes: forbidden_zero_bit(1) + nal_unit_type(6) + layer_id(6) + temporal_id(3)
        var nalType = (HevcNalUnitType)((nalData[0] >> 1) & 0x3F);
        
        // Extract nuh_layer_id for MV-HEVC: distinguishes base layer (0) from enhancement (1+)
        int nuhLayerId = ((nalData[0] & 1) << 5) | ((nalData[1] >> 3) & 0x1F);
        
        switch (nalType)
        {
            case HevcNalUnitType.VideoParameterSet:
                currentVps = nalData;
                var vpsId = ParseVpsId(nalData);
                vpsByPsId[vpsId] = nalData;
                // MV-HEVC: enhancement layer PS arriving after the base VCL but within the
                // same AU must update the snapshot so the emitted AU includes them.
                // Base layer PS (nuhLayerId=0) after the snapshot belong to the NEXT AU.
                if (nuhLayerId > 0 && auAllVps != null) auAllVps[vpsId] = nalData;
                break;
            
            case HevcNalUnitType.SequenceParameterSet:
                currentSps = nalData;
                var spsId = ParseSpsId(nalData);
                spsByPsId[spsId] = nalData;
                if (nuhLayerId > 0 && auAllSps != null) auAllSps[spsId] = nalData;
                break;
            
            case HevcNalUnitType.PictureParameterSet:
                currentPps = nalData;
                var ppsId = ParsePpsId(nalData);
                ppsByPsId[ppsId] = nalData;
                if (nuhLayerId > 0 && auAllPps != null) auAllPps[ppsId] = nalData;
                break;
            
            case HevcNalUnitType.AccessUnitDelimiter:
                // Access unit delimiter marks new AU
                if (pendingNalUnits.Count > 0)
                {
                    EmitAccessUnit(lastPts, lastDts, CheckIfKeyframe());
                }
                pendingNalUnits.Add(nalData);
                // Only update timestamps if new values are valid
                if (pts >= 0)
                    lastPts = pts;
                if (dts >= 0)
                    lastDts = dts;
                break;
            
            case HevcNalUnitType.PrefixSei:
            case HevcNalUnitType.SuffixSei:
                pendingNalUnits.Add(nalData);
                break;
            
            default:
                // VCL NAL units (0-31 are coded slice segments)
                if ((int)nalType <= 31)
                {
                    if (IsFirstVclNal(nalData))
                    {
                        if (pendingNalUnits.Count > 0)
                        {
                            EmitAccessUnit(lastPts, lastDts, CheckIfKeyframe());
                        }
                        // Snapshot current PS state for this new AU — captures the PS
                        // that was active when this picture's first VCL NAL arrived.
                        // This prevents later PS updates (for the NEXT picture) from
                        // overwriting the PS needed by THIS picture's AU emission.
                        auVps = currentVps;
                        auSps = currentSps;
                        auPps = currentPps;
                        auAllVps = new Dictionary<int, byte[]>(vpsByPsId);
                        auAllSps = new Dictionary<int, byte[]>(spsByPsId);
                        auAllPps = new Dictionary<int, byte[]>(ppsByPsId);
                        // Only update timestamps if new values are valid
                        if (pts >= 0)
                            lastPts = pts;
                        if (dts >= 0)
                            lastDts = dts;
                    }
                    pendingNalUnits.Add(nalData);
                }
                else
                {
                    pendingNalUnits.Add(nalData);
                }
                break;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsFirstVclNal(byte[] nalData)
    {
        // Check first_slice_segment_in_pic_flag
        // This is the first bit after the NAL header (2 bytes)
        if (nalData.Length < 3)
            return false;
        
        // In MV-HEVC, an access unit contains NALs from ALL layers.
        // Only layer 0 (nuh_layer_id == 0) first slices mark AU boundaries.
        // Layer 1+ first slices are part of the same access unit.
        // HEVC NAL header: byte0 bits[0]=forbidden, bits[6:1]=nal_type, bits[7]=layer_id_msb
        //                  byte1 bits[7:3]=layer_id_lsb, bits[2:0]=temporal_id_plus1
        int nuhLayerId = ((nalData[0] & 1) << 5) | ((nalData[1] >> 3) & 0x1F);
        if (nuhLayerId > 0)
            return false;
        
        // First bit of slice header is first_slice_segment_in_pic_flag
        bool firstSlice = (nalData[2] & 0x80) != 0;
        return firstSlice;
    }
    
    private bool CheckIfKeyframe()
    {
        foreach (var nal in pendingNalUnits)
        {
            if (nal.Length >= 2)
            {
                var nalType = (HevcNalUnitType)((nal[0] >> 1) & 0x3F);
                // IDR or CRA are keyframes
                if (nalType == HevcNalUnitType.IdrWithRadl || 
                    nalType == HevcNalUnitType.IdrNoLeadingPictures ||
                    nalType == HevcNalUnitType.CleanRandomAccess)
                {
                    return true;
                }
            }
        }
        return false;
    }
    
    private void EmitAccessUnit(long pts, long dts, bool isKeyframe)
    {
        if (pendingNalUnits.Count == 0)
            return;
        
        InterpolateTimestamps(ref pts, ref dts);
        
        int totalSize = 0;
        bool hasVcl = false;
        
        // Use AU-snapshotted PS for injection (avoids using a later PPS that overwrote the global)
        var injectVps = auVps ?? currentVps;
        var injectSps = auSps ?? currentSps;
        var injectPps = auPps ?? currentPps;
        
        // Use snapshotted maps of ALL PS by ID, falling back to current global maps
        var injectAllVps = auAllVps ?? vpsByPsId;
        var injectAllSps = auAllSps ?? spsByPsId;
        var injectAllPps = auAllPps ?? ppsByPsId;
        
        // Inject PS when any of them differ from what was last injected (reference equality).
        // This handles: first AU (lastInjected* == null), per-frame PPS changes (TILES_A/B),
        // and mid-stream PS updates — without requiring a boolean flag.
        bool shouldInjectPs = InjectParameterSets &&
            (injectVps != lastInjectedVps || injectSps != lastInjectedSps || injectPps != lastInjectedPps);
        
        if (shouldInjectPs)
        {
            foreach (var vps in injectAllVps.Values)
                totalSize += 4 + vps.Length;
            foreach (var sps in injectAllSps.Values)
                totalSize += 4 + sps.Length;
            foreach (var pps in injectAllPps.Values)
                totalSize += 4 + pps.Length;
        }
        
        foreach (var nal in pendingNalUnits)
        {
            totalSize += 4 + nal.Length;
            if (nal.Length >= 2)
            {
                var nalType = (HevcNalUnitType)((nal[0] >> 1) & 0x3F);
                if ((int)nalType <= 31)
                    hasVcl = true;
            }
        }
        
        if (!hasVcl)
        {
            pendingNalUnits.Clear();
            return;
        }
        
        var data = new byte[totalSize];
        int offset = 0;
        
        // Inject latest parameter sets per ID (VPS, SPS, PPS)
        if (shouldInjectPs)
        {
            foreach (var vps in injectAllVps.Values)
            {
                WriteStartCode(data, ref offset);
                vps.CopyTo(data.AsSpan(offset));
                offset += vps.Length;
            }
            foreach (var sps in injectAllSps.Values)
            {
                WriteStartCode(data, ref offset);
                sps.CopyTo(data.AsSpan(offset));
                offset += sps.Length;
            }
            foreach (var pps in injectAllPps.Values)
            {
                WriteStartCode(data, ref offset);
                pps.CopyTo(data.AsSpan(offset));
                offset += pps.Length;
            }
            lastInjectedVps = injectVps;
            lastInjectedSps = injectSps;
            lastInjectedPps = injectPps;
        }
        
        foreach (var nal in pendingNalUnits)
        {
            WriteStartCode(data, ref offset);
            nal.CopyTo(data.AsSpan(offset));
            offset += nal.Length;
        }
        
        outputQueue.Add(new AccessUnit
        {
            Data = data,
            PresentationTimestamp = pts,
            DecodeTimestamp = dts,
            IsKeyframe = isKeyframe,
            DurationTicks = frameDurationTicks
        });
        
        pendingNalUnits.Clear();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteStartCode(byte[] data, ref int offset)
    {
        data[offset] = 0;
        data[offset + 1] = 0;
        data[offset + 2] = 0;
        data[offset + 3] = 1;
        offset += 4;
    }
    
    private void InterpolateTimestamps(ref long pts, ref long dts)
    {
        if (dts < 0)
        {
            dts = lastDts >= 0 ? lastDts + frameDurationTicks : 0;
        }
        
        if (pts < 0)
        {
            pts = dts;
        }
        
        lastPts = pts;
        lastDts = dts;
    }
    
    /// <summary>
    /// Extracts vps_video_parameter_set_id (4 bits after 2-byte NAL header).
    /// </summary>
    private static int ParseVpsId(byte[] nalData)
    {
        if (nalData.Length < 3) return 0;
        return (nalData[2] >> 4) & 0x0F;
    }
    
    /// <summary>
    /// Extracts pps_pic_parameter_set_id (first UE(v) after 2-byte NAL header).
    /// EPB is impossible in these first bytes since the NAL header never contains 0x00.
    /// </summary>
    private static int ParsePpsId(byte[] nalData)
    {
        if (nalData.Length < 3) return 0;
        return ReadUeV(nalData, byteOffset: 2, bitOffset: 0);
    }
    
    /// <summary>
    /// Extracts sps_seq_parameter_set_id from an SPS NAL.
    /// This requires skipping profile_tier_level, so we use the full BitstreamReader.
    /// SPS NALs are rare (1-2 per stream), so the cost is negligible.
    /// </summary>
    private static int ParseSpsId(byte[] nalData)
    {
        if (nalData.Length < 3) return 0;
        
        // Extract nuh_layer_id from NAL header (bytes 0-1)
        int nuhLayerId = ((nalData[0] & 1) << 5) | ((nalData[1] >> 3) & 0x1F);
        
        // Remove emulation prevention bytes for safe bitstream reading
        byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(nalData.AsSpan(2));
        var reader = new BitstreamReader(rbsp);
        
        // sps_video_parameter_set_id: u(4)
        reader.ReadBits(4);
        // sps_max_sub_layers_minus1 (or sps_ext_or_max_sub_layers_minus1): u(3)
        int maxSubLayers = (int)reader.ReadBits(3);
        
        // Multi-layer extension SPS (nuh_layer_id > 0 and marker value 7) has NO
        // sps_temporal_id_nesting_flag and NO profile_tier_level
        bool multiLayerExt = nuhLayerId > 0 && maxSubLayers == 7;
        if (!multiLayerExt)
        {
            // sps_temporal_id_nesting_flag: u(1) — only present in normal SPS
            reader.ReadBits(1);
            // profile_tier_level(1, maxSubLayers)
            SkipProfileTierLevel(ref reader, maxSubLayers);
        }
        
        // sps_seq_parameter_set_id: ue(v)
        return (int)reader.ReadExpGolombUnsigned();
    }
    
    /// <summary>
    /// Skips profile_tier_level in the bitstream (needed to reach SPS ID).
    /// </summary>
    private static void SkipProfileTierLevel(ref BitstreamReader reader, int maxSubLayersMinus1)
    {
        // general_profile_space(2) + general_tier_flag(1) + general_profile_idc(5)
        reader.ReadBits(8);
        // general_profile_compatibility_flags(32)
        reader.ReadBits(32);
        // constraint flags + reserved (48 bits = 32 + 16)
        reader.ReadBits(32);
        reader.ReadBits(16);
        // general_level_idc(8)
        reader.ReadBits(8);
        // Total: 96 bits for the general tier
        
        // Sub-layer flags
        Span<bool> subLayerProfilePresent = stackalloc bool[maxSubLayersMinus1];
        Span<bool> subLayerLevelPresent = stackalloc bool[maxSubLayersMinus1];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            subLayerProfilePresent[i] = reader.ReadBits(1) != 0;
            subLayerLevelPresent[i] = reader.ReadBits(1) != 0;
        }
        if (maxSubLayersMinus1 > 0)
        {
            for (int i = maxSubLayersMinus1; i < 8; i++)
                reader.ReadBits(2); // reserved_zero_2bits
        }
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (subLayerProfilePresent[i])
            {
                // sub_layer profile data: 88 bits = 32 + 32 + 24
                reader.ReadBits(32);
                reader.ReadBits(32);
                reader.ReadBits(24);
            }
            if (subLayerLevelPresent[i])
                reader.ReadBits(8); // sub_layer_level_idc
        }
    }
    
    /// <summary>
    /// Reads an unsigned Exp-Golomb coded value starting at the given byte/bit offset.
    /// </summary>
    private static int ReadUeV(byte[] data, int byteOffset, int bitOffset)
    {
        int pos = byteOffset * 8 + bitOffset;
        int zeros = 0;
        
        while (pos / 8 < data.Length)
        {
            int byteIdx = pos / 8;
            int bitIdx = 7 - (pos % 8);
            if (((data[byteIdx] >> bitIdx) & 1) == 0)
            {
                zeros++;
                pos++;
            }
            else
            {
                pos++; // skip the '1' bit
                int value = 0;
                for (int i = 0; i < zeros; i++)
                {
                    if (pos / 8 >= data.Length) return 0;
                    int bi = pos / 8;
                    int bti = 7 - (pos % 8);
                    value = (value << 1) | ((data[bi] >> bti) & 1);
                    pos++;
                }
                return (1 << zeros) - 1 + value;
            }
        }
        return 0;
    }
    
    public void Dispose()
    {
        pendingNalUnits.Clear();
        outputQueue.Clear();
    }
}

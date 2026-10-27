// H264Packetizer - Converts demuxer output into complete H.264 access units
// Handles Annex-B/AVCC conversion, SPS/PPS management, AU boundary detection, and timestamp interpolation

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.H264;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// H.264/AVC packetizer that prepares demuxer output for decoder consumption.
/// Handles format conversion, parameter set injection, and frame boundary detection.
/// </summary>
public sealed class H264Packetizer : IDisposable
{
    private readonly List<byte[]> pendingNalUnits = new();
    private readonly List<AccessUnit> outputQueue = new();
    
    // Carries incomplete NAL data across chunk boundaries in Annex B mode.
    // When a NAL unit spans two read chunks, the first portion is saved here
    // and combined with the continuation in the next chunk.
    private byte[]? annexBPartialNal;
    
    private byte[]? currentSps;
    private byte[]? currentPps;
    private readonly Dictionary<int, byte[]> ppsMap = new();
    private int avccLengthSize = 4; // Default for AVCC format (1, 2, or 4)
    private bool isAvccFormat;
    private bool needParameterSetInjection = true;
    
    // Timestamp interpolation state
    private long lastDts = long.MinValue;
    private long lastPts = long.MinValue;
    private long frameDurationTicks = TimeSpan.TicksPerSecond / 30; // Default 30 fps
    private int pocLsb;
    private int prevPocLsb;
    private int pocMsb;
    private int prevPocMsb;
    
    // SPS-derived info for timestamp interpolation
    private int maxPocLsb; // 2^(log2_max_poc_lsb)
    private double frameRate = 30.0;
    private bool isInterlaced;
    
    // AU boundary detection state (H.264 §7.4.1.2.4)
    private int log2MaxFrameNum = 4; // Default: log2_max_frame_num_minus4=0 → 2^4=16
    private int pendingVclFrameNum = -1; // frame_num of current pending AU's VCL NALs
    private bool pendingVclIsIdr; // Whether current pending AU is IDR
    
    /// <summary>Gets the current SPS data.</summary>
    public ReadOnlySpan<byte> CurrentSps => currentSps;
    
    /// <summary>Gets the current PPS data.</summary>
    public ReadOnlySpan<byte> CurrentPps => currentPps;
    
    /// <summary>Gets or sets whether to inject SPS/PPS before keyframes.</summary>
    public bool InjectParameterSets { get; set; } = true;
    
    /// <summary>Gets or sets the frame rate for timestamp interpolation when not available from SPS.</summary>
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
    /// Configures the packetizer for AVCC format (MP4/MOV containers).
    /// </summary>
    /// <param name="avccData">AVCDecoderConfigurationRecord from esds/avcC box.</param>
    public void ConfigureAvcc(ReadOnlySpan<byte> avccData)
    {
        if (avccData.Length < 7)
            return;
        
        // AVCDecoderConfigurationRecord structure:
        // configurationVersion (1)
        // AVCProfileIndication (1)
        // profile_compatibility (1)
        // AVCLevelIndication (1)
        // reserved (6 bits) + lengthSizeMinusOne (2 bits)
        // reserved (3 bits) + numOfSPS (5 bits)
        // spsLength (2) + spsData[]
        // numOfPPS (1)
        // ppsLength (2) + ppsData[]
        
        isAvccFormat = true;
        avccLengthSize = (avccData[4] & 0x03) + 1;
        
        int offset = 5;
        int numSps = avccData[offset] & 0x1F;
        offset++;
        
        if (numSps > 0 && offset + 2 <= avccData.Length)
        {
            int spsLength = BinaryPrimitives.ReadUInt16BigEndian(avccData.Slice(offset, 2));
            offset += 2;
            
            if (offset + spsLength <= avccData.Length)
            {
                currentSps = avccData.Slice(offset, spsLength).ToArray();
                offset += spsLength;
                
                // Parse SPS for timing info
                ParseSpsForTiming(currentSps);
            }
        }
        
        if (offset < avccData.Length)
        {
            int numPps = avccData[offset];
            offset++;
            
            if (numPps > 0 && offset + 2 <= avccData.Length)
            {
                int ppsLength = BinaryPrimitives.ReadUInt16BigEndian(avccData.Slice(offset, 2));
                offset += 2;
                
                if (offset + ppsLength <= avccData.Length)
                {
                    currentPps = avccData.Slice(offset, ppsLength).ToArray();
                }
            }
        }
    }
    
    /// <summary>
    /// Configures the packetizer for Annex-B format (raw streams, TS, MKV).
    /// </summary>
    public void ConfigureAnnexB()
    {
        isAvccFormat = false;
    }
    
    /// <summary>
    /// Feeds raw data from the demuxer into the packetizer.
    /// </summary>
    /// <param name="data">Raw H.264 data (Annex-B or AVCC format).</param>
    /// <param name="pts">Presentation timestamp in ticks (-1 if unknown).</param>
    /// <param name="dts">Decode timestamp in ticks (-1 if unknown).</param>
    /// <param name="isKeyframe">Hint from demuxer if this is a keyframe.</param>
    public void Feed(ReadOnlySpan<byte> data, long pts, long dts, bool isKeyframe)
    {
        // Track latest timestamps so Flush() can use them for buffered partial NALs
        if (pts >= 0) lastPts = pts;
        if (dts >= 0) lastDts = dts;

        if (isAvccFormat)
        {
            ParseAvccData(data, pts, dts, isKeyframe);
        }
        else
        {
            ParseAnnexBData(data, pts, dts, isKeyframe);
        }
    }
    
    /// <summary>
    /// Retrieves the next complete access unit (frame).
    /// </summary>
    /// <param name="accessUnit">The output access unit.</param>
    /// <returns>True if an access unit was available.</returns>
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
    /// Flushes any pending NAL units as a final access unit.
    /// </summary>
    public void Flush()
    {
        // Emit any incomplete NAL that was waiting for the next start code
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
    /// Resets the packetizer state (for seeking).
    /// </summary>
    public void Reset()
    {
        pendingNalUnits.Clear();
        outputQueue.Clear();
        annexBPartialNal = null;
        lastDts = long.MinValue;
        lastPts = long.MinValue;
        pocLsb = 0;
        prevPocLsb = 0;
        pocMsb = 0;
        prevPocMsb = 0;
        needParameterSetInjection = true;
        pendingVclFrameNum = -1;
        pendingVclIsIdr = false;
    }
    
    private void ParseAvccData(ReadOnlySpan<byte> data, long pts, long dts, bool isKeyframe)
    {
        int offset = 0;
        
        while (offset + avccLengthSize <= data.Length)
        {
            int nalLength = avccLengthSize switch
            {
                1 => data[offset],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)),
                4 => (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)),
                _ => 0
            };
            
            offset += avccLengthSize;
            
            if (nalLength <= 0 || offset + nalLength > data.Length)
                break;
            
            var nalData = data.Slice(offset, nalLength).ToArray();
            ProcessNalUnit(nalData, pts, dts);
            offset += nalLength;
        }
    }
    
    private void ParseAnnexBData(ReadOnlySpan<byte> data, long pts, long dts, bool isKeyframe)
    {
        // Combine any leftover partial NAL from the previous chunk with new data.
        // This handles NAL units that span read buffer boundaries.
        ReadOnlySpan<byte> workData;
        byte[]? combinedBuffer = null;
        bool hadPartialNal = annexBPartialNal != null;
        
        if (hadPartialNal)
        {
            combinedBuffer = new byte[annexBPartialNal!.Length + data.Length];
            annexBPartialNal.AsSpan().CopyTo(combinedBuffer);
            data.CopyTo(combinedBuffer.AsSpan(annexBPartialNal.Length));
            workData = combinedBuffer;
            annexBPartialNal = null;
        }
        else
        {
            workData = data;
        }
        
        // Find NAL units by scanning for start codes (0x000001 or 0x00000001).
        // If we had a partial NAL, its data starts at offset 0 of workData.
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
                    // Strip trailing zeros that belong to the start code prefix
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
        
        // Save any trailing incomplete NAL for the next chunk.
        // Do NOT emit it now — we don't know if more data follows.
        // Use <= to handle the edge case where a start code occupies the very last
        // bytes of a chunk: nalStart == workData.Length means the NAL body starts
        // in the next chunk. We save an empty array so hadPartialNal is true.
        if (nalStart >= 0 && nalStart <= workData.Length)
        {
            annexBPartialNal = nalStart < workData.Length
                ? workData.Slice(nalStart, workData.Length - nalStart).ToArray()
                : Array.Empty<byte>();
        }
        else if (nalStart < 0 && hadPartialNal)
        {
            // No start code found at all — entire chunk is continuation of partial NAL
            annexBPartialNal = workData.ToArray();
        }
    }
    
    private void ProcessNalUnit(byte[] nalData, long pts, long dts)
    {
        if (nalData.Length == 0)
            return;
        
        var nalType = (NalUnitType)(nalData[0] & 0x1F);
        
        switch (nalType)
        {
            case NalUnitType.SequenceParameterSet:
                UpdateSps(nalData);
                pendingNalUnits.Add(nalData);
                break;
            
            case NalUnitType.PictureParameterSet:
                UpdatePps(nalData);
                pendingNalUnits.Add(nalData);
                break;
            
            case NalUnitType.SupplementalEnhancementInfo:
                pendingNalUnits.Add(nalData);
                break;
            
            case NalUnitType.AccessUnitDelimiter:
                if (pendingNalUnits.Count > 0)
                {
                    EmitAccessUnit(lastPts, lastDts, CheckIfKeyframe());
                }
                pendingNalUnits.Add(nalData);
                if (pts >= 0)
                    lastPts = pts;
                if (dts >= 0)
                    lastDts = dts;
                break;
            
            case NalUnitType.CodedSliceIdr:
            case NalUnitType.CodedSliceNonIdr:
            case NalUnitType.CodedSliceDataPartitionA:
            case NalUnitType.CodedSliceDataPartitionB:
            case NalUnitType.CodedSliceDataPartitionC:
                if (IsFirstVclNal(nalData))
                {
                    // Only emit if the pending NALs contain at least one VCL NAL
                    // (i.e., they form a complete access unit from the previous picture).
                    // Non-VCL NALs (SPS, PPS, SEI) preceding this VCL NAL belong to
                    // THIS access unit per H.264 spec 7.4.1.2.3, so keep them.
                    if (pendingNalUnits.Count > 0 && PendingHasVclNal())
                    {
                        EmitAccessUnit(lastPts, lastDts, CheckIfKeyframe());
                    }
                    if (pts >= 0)
                        lastPts = pts;
                    if (dts >= 0)
                        lastDts = dts;
                }
                pendingNalUnits.Add(nalData);
                break;
            
            default:
                pendingNalUnits.Add(nalData);
                break;
        }
    }
    
    private void UpdateSps(byte[] spsData)
    {
        currentSps = spsData;
        ParseSpsForTiming(spsData);
        needParameterSetInjection = true;
    }
    
    private void UpdatePps(byte[] ppsData)
    {
        currentPps = ppsData;
        // Store by PPS ID for multi-PPS injection
        if (ppsData.Length >= 2)
        {
            int ppsId = ParseExpGolombFromNalPayload(ppsData, 1);
            if (ppsId >= 0)
                ppsMap[ppsId] = ppsData;
        }
        needParameterSetInjection = true;
    }
    
    /// <summary>
    /// Reads the first exp-golomb value starting at the given byte offset (bit 0 of that byte).
    /// Returns -1 on failure.
    /// </summary>
    private static int ParseExpGolombFromNalPayload(byte[] data, int byteOffset)
    {
        int bitOffset = byteOffset * 8;
        return ReadExpGolomb(data, ref bitOffset);
    }
    
    /// <summary>Reads n bits from data starting at bitPos (MSB first), advancing bitPos.</summary>
    private static int ReadBits(byte[] data, ref int bitPos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            if (bitPos >= data.Length * 8) return value;
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);
            value = (value << 1) | ((data[byteIdx] >> bitIdx) & 1);
            bitPos++;
        }
        return value;
    }
    
    /// <summary>Reads an unsigned exp-golomb coded value from data at bitPos.</summary>
    private static int ReadExpGolomb(byte[] data, ref int bitPos)
    {
        int leadingZeros = 0;
        while (bitPos < data.Length * 8)
        {
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);
            bool bit = ((data[byteIdx] >> bitIdx) & 1) != 0;
            bitPos++;
            if (bit) break;
            leadingZeros++;
            if (leadingZeros > 32) return -1;
        }
        int value = 0;
        for (int i = 0; i < leadingZeros; i++)
        {
            if (bitPos >= data.Length * 8) return -1;
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);
            value = (value << 1) | ((data[byteIdx] >> bitIdx) & 1);
            bitPos++;
        }
        return (1 << leadingZeros) - 1 + value;
    }
    
    /// <summary>Reads a signed exp-golomb coded value from data at bitPos.</summary>
    private static int ReadSignedExpGolomb(byte[] data, ref int bitPos)
    {
        int codeNum = ReadExpGolomb(data, ref bitPos);
        if (codeNum <= 0) return 0;
        int val = (codeNum + 1) / 2;
        return (codeNum % 2 == 0) ? -val : val;
    }
    
    private void ParseSpsForTiming(byte[] spsData)
    {
        // Skip NAL header byte
        if (spsData.Length < 5)
            return;
        
        // Parse SPS to extract log2_max_frame_num for AU boundary detection.
        // SPS layout: profile_idc(8), constraint_set_flags(8), level_idc(8), 
        //             sps_id(ue), [chroma/scaling for High+], log2_max_frame_num_minus4(ue)
        try
        {
            int bitPos = 8; // skip NAL header byte
            int profileIdc = ReadBits(spsData, ref bitPos, 8);
            ReadBits(spsData, ref bitPos, 8); // constraint_set flags
            ReadBits(spsData, ref bitPos, 8); // level_idc
            ReadExpGolomb(spsData, ref bitPos); // seq_parameter_set_id
            
            // High profile and above have chroma_format_idc etc.
            if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 ||
                profileIdc == 244 || profileIdc == 44 || profileIdc == 83 ||
                profileIdc == 86 || profileIdc == 118 || profileIdc == 128 ||
                profileIdc == 138 || profileIdc == 139 || profileIdc == 134 ||
                profileIdc == 135)
            {
                int chromaFormatIdc = ReadExpGolomb(spsData, ref bitPos);
                if (chromaFormatIdc == 3)
                    ReadBits(spsData, ref bitPos, 1); // separate_colour_plane_flag
                ReadExpGolomb(spsData, ref bitPos); // bit_depth_luma_minus8
                ReadExpGolomb(spsData, ref bitPos); // bit_depth_chroma_minus8
                ReadBits(spsData, ref bitPos, 1); // qpprime_y_zero_transform_bypass_flag
                int seqScalingMatrixPresent = ReadBits(spsData, ref bitPos, 1);
                if (seqScalingMatrixPresent != 0)
                {
                    int count = (chromaFormatIdc != 3) ? 8 : 12;
                    for (int i = 0; i < count; i++)
                    {
                        int listPresent = ReadBits(spsData, ref bitPos, 1);
                        if (listPresent != 0)
                        {
                            int size = (i < 6) ? 16 : 64;
                            int lastScale = 8, nextScale = 8;
                            for (int j = 0; j < size; j++)
                            {
                                if (nextScale != 0)
                                {
                                    int delta = ReadSignedExpGolomb(spsData, ref bitPos);
                                    nextScale = (lastScale + delta + 256) % 256;
                                }
                                lastScale = (nextScale == 0) ? lastScale : nextScale;
                            }
                        }
                    }
                }
            }
            
            int log2MaxFrameNumMinus4 = ReadExpGolomb(spsData, ref bitPos);
            log2MaxFrameNum = log2MaxFrameNumMinus4 + 4;
        }
        catch
        {
            log2MaxFrameNum = 4; // fallback
        }
        
        // Defaults for other fields - full SPS parsing is in SequenceParameterSet.cs
        maxPocLsb = 256; // 2^8 default
        isInterlaced = false;
        frameDurationTicks = (long)(TimeSpan.TicksPerSecond / frameRate);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsFirstVclNal(byte[] nalData)
    {
        // Simple AU boundary detection: a VCL NAL with first_mb_in_slice == 0
        // starts a new coded picture. This works for single-slice-per-frame streams
        // (the vast majority). Full spec-compliant detection (§7.4.1.2.4) requires
        // comparing frame_num, nal_ref_idc, POC, field_pic_flag, etc. — needed for
        // redundant pictures and field pairs. TODO: implement when adding interlaced support.
        if (nalData.Length < 2)
            return false;
        
        int bitPos = 8; // skip NAL header byte
        int firstMbInSlice = ReadExpGolomb(nalData, ref bitPos);
        return firstMbInSlice == 0;
    }
    
    private bool CheckIfKeyframe()
    {
        foreach (var nal in pendingNalUnits)
        {
            if (nal.Length > 0)
            {
                var nalType = (NalUnitType)(nal[0] & 0x1F);
                if (nalType == NalUnitType.CodedSliceIdr)
                    return true;
            }
        }
        return false;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PendingHasVclNal()
    {
        foreach (var nal in pendingNalUnits)
        {
            if (nal.Length > 0)
            {
                var nalType = (NalUnitType)(nal[0] & 0x1F);
                if (nalType >= NalUnitType.CodedSliceNonIdr && nalType <= NalUnitType.CodedSliceIdr)
                    return true;
            }
        }
        return false;
    }
    
    private void EmitAccessUnit(long pts, long dts, bool isKeyframe)
    {
        if (pendingNalUnits.Count == 0)
            return;
        
        // Interpolate timestamps if needed
        InterpolateTimestamps(ref pts, ref dts);
        
        // Build access unit with Annex-B format (start codes)
        int totalSize = 0;
        bool hasVcl = false;
        
        // Calculate size with SPS/PPS injection if needed
        if (InjectParameterSets && needParameterSetInjection && isKeyframe)
        {
            if (currentSps != null)
                totalSize += 4 + currentSps.Length; // Start code + SPS
            foreach (var pps in ppsMap.Values)
                totalSize += 4 + pps.Length; // Start code + each PPS
        }
        
        foreach (var nal in pendingNalUnits)
        {
            totalSize += 4 + nal.Length; // Start code + NAL
            var nalType = (NalUnitType)(nal[0] & 0x1F);
            if (nalType >= NalUnitType.CodedSliceNonIdr && nalType <= NalUnitType.CodedSliceIdr)
                hasVcl = true;
        }
        
        if (!hasVcl)
        {
            pendingNalUnits.Clear();
            return;
        }
        
        var data = new byte[totalSize];
        int offset = 0;
        
        // Inject SPS/PPS before keyframe
        if (InjectParameterSets && needParameterSetInjection && isKeyframe)
        {
            if (currentSps != null)
            {
                WriteStartCode(data, ref offset);
                currentSps.CopyTo(data.AsSpan(offset));
                offset += currentSps.Length;
            }
            foreach (var pps in ppsMap.Values)
            {
                WriteStartCode(data, ref offset);
                pps.CopyTo(data.AsSpan(offset));
                offset += pps.Length;
            }
            needParameterSetInjection = false;
        }
        
        // Write NAL units
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
            IsKeyframe = isKeyframe
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
        // DTS interpolation: should monotonically increase
        if (dts < 0)
        {
            if (lastDts >= 0)
            {
                dts = lastDts + frameDurationTicks;
            }
            else
            {
                dts = 0;
            }
        }
        
        // PTS interpolation: use DTS if not available
        if (pts < 0)
        {
            pts = dts;
        }
        
        lastPts = pts;
        lastDts = dts;
    }
    
    public void Dispose()
    {
        pendingNalUnits.Clear();
        outputQueue.Clear();
    }
}

/// <summary>
/// A complete H.264 access unit (video frame) ready for decoding.
/// </summary>
public struct AccessUnit
{
    /// <summary>Frame data in Annex-B format with start codes.</summary>
    public byte[] Data;
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp;
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long DecodeTimestamp;
    
    /// <summary>Whether this is a keyframe (IDR).</summary>
    public bool IsKeyframe;
    
    /// <summary>Duration of this frame in ticks.</summary>
    public long DurationTicks;
}

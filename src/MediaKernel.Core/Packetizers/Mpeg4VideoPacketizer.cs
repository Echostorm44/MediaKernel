// MediaKernel - MPEG-4 Part 2 (Visual) Packetizer
// Parses MPEG-4 ASP video elementary streams (DivX, XviD, etc.) into VOP units.
// Based on ISO/IEC 14496-2

using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// MPEG-4 Part 2 Visual Object Type.
/// </summary>
public enum Mpeg4VisualObjectType : byte
{
    /// <summary>Unknown type.</summary>
    Unknown = 0,
    /// <summary>Video object (most common).</summary>
    Video = 1,
    /// <summary>Still texture object.</summary>
    StillTexture = 2,
    /// <summary>Mesh object.</summary>
    Mesh = 3,
    /// <summary>Face object.</summary>
    Face = 4,
    /// <summary>BIFS object (scene description).</summary>
    Bifs = 5
}

/// <summary>
/// MPEG-4 Part 2 video shape type.
/// </summary>
public enum Mpeg4VideoShape : byte
{
    /// <summary>Rectangular frame (normal video).</summary>
    Rectangular = 0,
    /// <summary>Binary shape (alpha mask).</summary>
    Binary = 1,
    /// <summary>Binary only (no texture).</summary>
    BinaryOnly = 2,
    /// <summary>Grayscale shape.</summary>
    Grayscale = 3
}

/// <summary>
/// MPEG-4 Part 2 VOP (Video Object Plane) coding type.
/// </summary>
public enum Mpeg4VopType : byte
{
    /// <summary>Intra-coded (keyframe).</summary>
    Intra = 0,
    /// <summary>Predicted (P-frame).</summary>
    Predicted = 1,
    /// <summary>Bidirectional (B-frame).</summary>
    Bidirectional = 2,
    /// <summary>Sprite (S-VOP).</summary>
    Sprite = 3
}

/// <summary>
/// Parsed MPEG-4 Part 2 Video Object Layer header.
/// </summary>
public readonly struct Mpeg4VideoObjectLayer
{
    /// <summary>Video object ID (0-31).</summary>
    public byte VideoObjectId { get; init; }
    
    /// <summary>Video object layer ID (0-15).</summary>
    public byte VideoObjectLayerId { get; init; }
    
    /// <summary>Video object version identifier (1-15).</summary>
    public byte VersionId { get; init; }
    
    /// <summary>Shape type.</summary>
    public Mpeg4VideoShape Shape { get; init; }
    
    /// <summary>VOP time increment resolution (ticks per second for time_increment).</summary>
    public int TimeIncrementResolution { get; init; }
    
    /// <summary>Fixed VOP rate flag.</summary>
    public bool FixedVopRate { get; init; }
    
    /// <summary>Fixed VOP time increment (if FixedVopRate is true).</summary>
    public int FixedVopTimeIncrement { get; init; }
    
    /// <summary>Video width in pixels (for rectangular shape).</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels (for rectangular shape).</summary>
    public int Height { get; init; }
    
    /// <summary>Sample aspect ratio numerator.</summary>
    public int SarNumerator { get; init; }
    
    /// <summary>Sample aspect ratio denominator.</summary>
    public int SarDenominator { get; init; }
    
    /// <summary>Interlaced content flag.</summary>
    public bool Interlaced { get; init; }
    
    /// <summary>Low delay mode (no B-frames).</summary>
    public bool LowDelay { get; init; }
    
    /// <summary>Calculated frame rate (if available).</summary>
    public double FrameRate => FixedVopRate && FixedVopTimeIncrement > 0
        ? (double)TimeIncrementResolution / FixedVopTimeIncrement
        : TimeIncrementResolution > 0 ? TimeIncrementResolution : 0;
}

/// <summary>
/// Parsed MPEG-4 Part 2 VOP (Video Object Plane).
/// </summary>
public readonly struct Mpeg4Vop
{
    /// <summary>Complete VOP data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long DecodeTimestamp { get; init; }
    
    /// <summary>VOP coding type.</summary>
    public Mpeg4VopType VopType { get; init; }
    
    /// <summary>Whether this is a keyframe (I-VOP).</summary>
    public bool IsKeyframe => VopType == Mpeg4VopType.Intra;
    
    /// <summary>Modulo time base (seconds since last ref VOP).</summary>
    public int ModuloTimeBase { get; init; }
    
    /// <summary>VOP time increment within the current second.</summary>
    public int TimeIncrement { get; init; }
    
    /// <summary>Whether VOP is coded (not skipped).</summary>
    public bool IsCoded { get; init; }
}

/// <summary>
/// MPEG-4 Part 2 (Visual) packetizer for DivX/XviD and other ASP content.
/// </summary>
/// <remarks>
/// Converts raw MPEG-4 Part 2 elementary stream data into complete VOP units.
/// Handles VOL detection, VOP boundary detection, and timestamp interpolation.
/// Start codes: 00 00 01 xx where xx defines the start code type.
/// </remarks>
public sealed class Mpeg4VideoPacketizer : IDisposable
{
    // Start code prefixes (00 00 01 xx)
    private const int VideoObjectStartCode = 0x100;          // 0x100-0x11F (video_object_id = low 5 bits)
    private const int VideoObjectLayerStartCode = 0x120;     // 0x120-0x12F (vol_id = low 4 bits)
    private const int ReservedStartCode = 0x130;             // 0x130-0x1AF reserved
    private const int VisualObjectSequenceStartCode = 0x1B0;
    private const int VisualObjectSequenceEndCode = 0x1B1;
    private const int UserDataStartCode = 0x1B2;
    private const int GroupOfVopStartCode = 0x1B3;           // GOP
    private const int VideoSessionErrorCode = 0x1B4;
    private const int VisualObjectStartCode = 0x1B5;
    private const int VopStartCode = 0x1B6;                  // VOP (frame)
    private const int FaceObjectStartCode = 0x1BA;
    private const int FaceObjectPlaneStartCode = 0x1BB;
    private const int MeshObjectStartCode = 0x1BC;
    private const int MeshObjectPlaneStartCode = 0x1BD;
    private const int StillTextureObjectStartCode = 0x1BE;
    private const int TextureSpatialLayerStartCode = 0x1BF;
    private const int TextureSnrLayerStartCode = 0x1C0;

    private const int MaxBufferSize = 16 * 1024 * 1024; // 16MB
    private const int DefaultBufferSize = 512 * 1024;   // 512KB

    private byte[] buffer = new byte[DefaultBufferSize];
    private int bufferLength;
    private readonly List<Mpeg4Vop> outputQueue = new();

    // Current VOL state
    private Mpeg4VideoObjectLayer currentVol;
    private byte[]? volData;
    private bool hasVol;

    // VOP assembly state
    private int vopStartOffset = -1;
    private int pendingStartCodeOffset = -1;
    private long currentPts = -1;
    private long currentDts = -1;
    private long pendingPts = -1;
    private long pendingDts = -1;
    private Mpeg4VopType currentVopType = Mpeg4VopType.Intra;
    private int currentModuloTimeBase;
    private int currentTimeIncrement;
    private bool currentIsCoded = true;
    
    // B-frame reordering state
    private bool hasSeenBFrame;
    private long lastRefPts = long.MinValue;
    private long lastRefDts = long.MinValue;

    // Timestamp interpolation
    private long lastOutputPts = long.MinValue;
    private long lastOutputDts = long.MinValue;
    private long frameDurationTicks;
    
    // Time reference tracking for interpolation
    private long timeRef;
    private long lastTimeRef;
    private long lastTime;
    private long lastTimeIncr;

    /// <summary>Gets the current Video Object Layer (if parsed).</summary>
    public Mpeg4VideoObjectLayer? VideoObjectLayer => hasVol ? currentVol : null;

    /// <summary>Gets the frame width from VOL.</summary>
    public int Width => currentVol.Width;

    /// <summary>Gets the frame height from VOL.</summary>
    public int Height => currentVol.Height;

    /// <summary>Gets the frame rate from VOL (if available).</summary>
    public double FrameRate => currentVol.FrameRate;

    /// <summary>Gets whether the stream is interlaced.</summary>
    public bool IsInterlaced => currentVol.Interlaced;

    /// <summary>Gets whether VOL header has been parsed.</summary>
    public bool HasVideoObjectLayer => hasVol;

    /// <summary>Gets the raw VOL data for codec initialization.</summary>
    public ReadOnlySpan<byte> VideoObjectLayerData => volData;

    /// <summary>
    /// Feeds raw MPEG-4 Part 2 elementary stream data.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, long pts = -1, long dts = -1)
    {
        if (data.Length == 0)
            return;

        if (pts >= 0)
            pendingPts = pts;
        if (dts >= 0)
            pendingDts = dts;

        EnsureCapacity(bufferLength + data.Length);

        data.CopyTo(buffer.AsSpan(bufferLength));
        bufferLength += data.Length;

        ParseBuffer();
    }

    /// <summary>
    /// Retrieves the next complete VOP.
    /// </summary>
    public bool TryGetVop(out Mpeg4Vop vop)
    {
        if (outputQueue.Count > 0)
        {
            vop = outputQueue[0];
            outputQueue.RemoveAt(0);
            return true;
        }

        vop = default;
        return false;
    }

    /// <summary>
    /// Flushes any pending data as a final VOP.
    /// </summary>
    public void Flush()
    {
        if (vopStartOffset >= 0 && bufferLength > vopStartOffset)
        {
            EmitVop(vopStartOffset, bufferLength);
        }

        vopStartOffset = -1;
        bufferLength = 0;
    }

    /// <summary>
    /// Resets the packetizer state (for seeking).
    /// </summary>
    public void Reset()
    {
        bufferLength = 0;
        outputQueue.Clear();
        vopStartOffset = -1;
        pendingStartCodeOffset = -1;
        currentPts = -1;
        currentDts = -1;
        pendingPts = -1;
        pendingDts = -1;
        lastOutputPts = long.MinValue;
        lastOutputDts = long.MinValue;
        hasSeenBFrame = false;
        lastRefPts = long.MinValue;
        lastRefDts = long.MinValue;
        timeRef = 0;
        lastTimeRef = 0;
        lastTime = 0;
        lastTimeIncr = 0;
        // Keep VOL - only reset on new stream
    }

    /// <summary>
    /// Clears all state including VOL (for new stream).
    /// </summary>
    public void Clear()
    {
        Reset();
        hasVol = false;
        currentVol = default;
        volData = null;
    }

    private void ParseBuffer()
    {
        int offset = pendingStartCodeOffset >= 0 ? pendingStartCodeOffset : 0;
        var span = buffer.AsSpan(0, bufferLength);

        while (offset + 4 <= bufferLength)
        {
            int scPos = FindStartCode(span.Slice(offset), bufferLength - offset);
            if (scPos < 0)
            {
                pendingStartCodeOffset = -1;
                break;
            }

            int absolutePos = offset + scPos;
            int startCodeValue = span[absolutePos + 3];

            // Need at least a few bytes after start code for any parsing
            if (absolutePos + 5 > bufferLength)
            {
                pendingStartCodeOffset = absolutePos;
                break;
            }

            int consumed = HandleStartCode(absolutePos, startCodeValue);
            if (consumed < 0)
            {
                // Need more data
                pendingStartCodeOffset = absolutePos;
                break;
            }

            pendingStartCodeOffset = -1;
            offset = absolutePos + 4;
        }

        TrimBuffer();
    }

    private int HandleStartCode(int offset, int startCode)
    {
        // Video Object (0x00-0x1F within 0x100-0x11F range, so startCode 0x00-0x1F)
        if (startCode <= 0x1F)
        {
            // Video Object header - just skip, we need VOL
            return 0;
        }
        
        // Video Object Layer (0x20-0x2F within 0x120-0x12F range, so startCode 0x20-0x2F)
        if (startCode >= 0x20 && startCode <= 0x2F)
        {
            return HandleVolHeader(offset, startCode);
        }
        
        // Visual Object Sequence Start
        if (startCode == 0xB0)
        {
            // VOS - profile_and_level_indication follows
            return 0;
        }
        
        // Visual Object Start
        if (startCode == 0xB5)
        {
            return HandleVisualObject(offset);
        }
        
        // Group of VOP (GOP)
        if (startCode == 0xB3)
        {
            return HandleGop(offset);
        }
        
        // VOP (Video Object Plane)
        if (startCode == 0xB6)
        {
            return HandleVopHeader(offset);
        }

        // Other start codes (user data, etc.) - skip
        return 0;
    }

    private int HandleVolHeader(int offset, int startCode)
    {
        var span = buffer.AsSpan(offset, Math.Min(bufferLength - offset, 64));
        if (span.Length < 10)
            return -1; // Need more data (minimum VOL is about 9 bytes after start code)

        // Parse VOL header
        int bitOffset = 32; // Skip start code
        
        // random_accessible_vol (1 bit)
        bool randomAccessible = ReadBit(span, ref bitOffset);
        
        // video_object_type_indication (8 bits)
        int voTypeIndication = ReadBits(span, ref bitOffset, 8);
        
        // is_object_layer_identifier (1 bit)
        int versionId = 1;
        if (ReadBit(span, ref bitOffset))
        {
            // video_object_layer_verid (4 bits)
            versionId = ReadBits(span, ref bitOffset, 4);
            // video_object_layer_priority (3 bits)
            ReadBits(span, ref bitOffset, 3);
        }

        // aspect_ratio_info (4 bits)
        int aspectRatioInfo = ReadBits(span, ref bitOffset, 4);
        int sarNum = 1, sarDen = 1;
        if (aspectRatioInfo == 0xF) // Extended_PAR
        {
            sarNum = ReadBits(span, ref bitOffset, 8);
            sarDen = ReadBits(span, ref bitOffset, 8);
        }
        else
        {
            (sarNum, sarDen) = GetParFromAspectRatioInfo(aspectRatioInfo);
        }

        // vol_control_parameters (1 bit)
        bool lowDelay = false;
        if (ReadBit(span, ref bitOffset))
        {
            // chroma_format (2 bits)
            ReadBits(span, ref bitOffset, 2);
            // low_delay (1 bit)
            lowDelay = ReadBit(span, ref bitOffset);
            
            // vbv_parameters (1 bit)
            if (ReadBit(span, ref bitOffset))
            {
                // Skip VBV parameters: first_half_bit_rate (15), marker, latter...
                ReadBits(span, ref bitOffset, 79);
            }
        }

        // video_object_layer_shape (2 bits)
        var shape = (Mpeg4VideoShape)ReadBits(span, ref bitOffset, 2);
        
        if (shape == Mpeg4VideoShape.Grayscale && versionId != 1)
        {
            // video_object_layer_shape_extension (4 bits)
            ReadBits(span, ref bitOffset, 4);
        }

        // marker_bit (1 bit)
        ReadBit(span, ref bitOffset);

        // vop_time_increment_resolution (16 bits)
        int timeIncrementResolution = ReadBits(span, ref bitOffset, 16);
        if (timeIncrementResolution == 0)
            timeIncrementResolution = 1;

        // marker_bit (1 bit)
        ReadBit(span, ref bitOffset);

        // fixed_vop_rate (1 bit)
        bool fixedVopRate = ReadBit(span, ref bitOffset);
        int fixedVopTimeIncrement = 0;
        
        if (fixedVopRate)
        {
            int timeIncrementBits = Log2(timeIncrementResolution - 1) + 1;
            if (timeIncrementBits < 1)
                timeIncrementBits = 1;
            fixedVopTimeIncrement = ReadBits(span, ref bitOffset, timeIncrementBits);
        }

        int width = 0, height = 0;
        bool interlaced = false;
        
        if (shape == Mpeg4VideoShape.Rectangular)
        {
            // marker_bit (1 bit)
            ReadBit(span, ref bitOffset);
            // video_object_layer_width (13 bits)
            width = ReadBits(span, ref bitOffset, 13);
            // marker_bit (1 bit)
            ReadBit(span, ref bitOffset);
            // video_object_layer_height (13 bits)
            height = ReadBits(span, ref bitOffset, 13);
            // marker_bit (1 bit)
            ReadBit(span, ref bitOffset);
            
            // interlaced (1 bit)
            interlaced = ReadBit(span, ref bitOffset);
        }

        currentVol = new Mpeg4VideoObjectLayer
        {
            VideoObjectId = (byte)(startCode & 0x1F),
            VideoObjectLayerId = (byte)(startCode & 0x0F),
            VersionId = (byte)versionId,
            Shape = shape,
            TimeIncrementResolution = timeIncrementResolution,
            FixedVopRate = fixedVopRate,
            FixedVopTimeIncrement = fixedVopTimeIncrement,
            Width = width,
            Height = height,
            SarNumerator = sarNum,
            SarDenominator = sarDen,
            Interlaced = interlaced,
            LowDelay = lowDelay
        };

        // Calculate frame duration for timestamp interpolation
        if (fixedVopRate && fixedVopTimeIncrement > 0)
        {
            frameDurationTicks = TimeSpan.TicksPerSecond * fixedVopTimeIncrement / timeIncrementResolution;
        }
        else if (timeIncrementResolution > 0)
        {
            frameDurationTicks = TimeSpan.TicksPerSecond / timeIncrementResolution;
        }

        hasVol = true;

        // Find the extent of this VOL (up to next start code)
        int volEnd = FindNextStartCode(offset + 4);
        if (volEnd < 0)
            volEnd = bufferLength;

        volData = buffer.AsSpan(offset, volEnd - offset).ToArray();

        return 0;
    }

    private int HandleVisualObject(int offset)
    {
        // Visual Object header - optional color info
        // Just skip for now, VOL has what we need
        return 0;
    }

    private int HandleGop(int offset)
    {
        // Group of VOP - time code information
        // We can extract time code if needed, but VOP has timing info
        return 0;
    }

    private int HandleVopHeader(int offset)
    {
        // If we're already in a VOP, emit the previous one
        if (vopStartOffset >= 0 && offset > vopStartOffset)
        {
            EmitVop(vopStartOffset, offset);
        }

        var span = buffer.AsSpan(offset, Math.Min(bufferLength - offset, 16));
        if (span.Length < 8)
            return -1;

        int bitOffset = 32; // Skip start code

        // vop_coding_type (2 bits)
        int vopType = ReadBits(span, ref bitOffset, 2);
        currentVopType = (Mpeg4VopType)vopType;

        // modulo_time_base (variable: count of 1s until 0)
        int moduloTimeBase = 0;
        while (ReadBit(span, ref bitOffset))
        {
            moduloTimeBase++;
        }
        currentModuloTimeBase = moduloTimeBase;

        // marker_bit (1 bit)
        ReadBit(span, ref bitOffset);

        // vop_time_increment (variable bits based on time_increment_resolution)
        int timeIncrementBits = currentVol.TimeIncrementResolution > 1
            ? Log2(currentVol.TimeIncrementResolution - 1) + 1
            : 1;
        if (timeIncrementBits < 1)
            timeIncrementBits = 1;
        currentTimeIncrement = ReadBits(span, ref bitOffset, timeIncrementBits);

        // marker_bit (1 bit)
        ReadBit(span, ref bitOffset);

        // vop_coded (1 bit)
        currentIsCoded = ReadBit(span, ref bitOffset);

        // Start new VOP
        vopStartOffset = offset;
        
        // Consume pending timestamps for this VOP
        if (pendingPts >= 0 && currentPts < 0)
        {
            currentPts = pendingPts;
            pendingPts = -1;
        }
        if (pendingDts >= 0 && currentDts < 0)
        {
            currentDts = pendingDts;
            pendingDts = -1;
        }

        if (vopType == 2) // B-frame
            hasSeenBFrame = true;

        return 0;
    }

    private void EmitVop(int startOffset, int endOffset)
    {
        if (!hasVol)
        {
            // Don't emit VOP without VOL
            vopStartOffset = -1;
            return;
        }

        int length = endOffset - startOffset;
        if (length <= 4)
        {
            vopStartOffset = -1;
            return;
        }

        // Determine timestamps
        long pts = currentPts >= 0 ? currentPts : InterpolatePts();
        long dts = currentDts >= 0 ? currentDts : InterpolateDts(pts);

        // Update time reference for interpolation
        UpdateTimeReference();

        // Store for next frame interpolation
        if (currentVopType != Mpeg4VopType.Bidirectional)
        {
            lastRefPts = pts;
            lastRefDts = dts;
        }

        lastOutputPts = pts;
        lastOutputDts = dts;

        // Copy VOP data (optionally prepend VOL for keyframes)
        byte[] vopData;
        if (currentVopType == Mpeg4VopType.Intra && volData != null)
        {
            // Prepend VOL to I-VOP for decoder init
            vopData = new byte[volData.Length + length];
            volData.CopyTo(vopData, 0);
            buffer.AsSpan(startOffset, length).CopyTo(vopData.AsSpan(volData.Length));
        }
        else
        {
            vopData = buffer.AsSpan(startOffset, length).ToArray();
        }

        var vop = new Mpeg4Vop
        {
            Data = vopData,
            PresentationTimestamp = pts,
            DecodeTimestamp = dts,
            VopType = currentVopType,
            ModuloTimeBase = currentModuloTimeBase,
            TimeIncrement = currentTimeIncrement,
            IsCoded = currentIsCoded
        };

        outputQueue.Add(vop);
        vopStartOffset = -1;
        currentPts = -1;
        currentDts = -1;
    }

    private void UpdateTimeReference()
    {
        if (currentVopType != Mpeg4VopType.Bidirectional)
        {
            lastTimeRef = timeRef;
            timeRef += currentModuloTimeBase * currentVol.TimeIncrementResolution;
        }
        
        lastTime = (currentVopType == Mpeg4VopType.Bidirectional ? lastTimeRef : timeRef);
        lastTimeIncr = currentTimeIncrement;
    }

    private long InterpolatePts()
    {
        if (lastOutputPts == long.MinValue)
            return 0;

        // Calculate time difference in VOL time units
        if (currentVol.TimeIncrementResolution > 0)
        {
            long currentTime = (currentVopType == Mpeg4VopType.Bidirectional)
                ? lastTimeRef + currentModuloTimeBase * currentVol.TimeIncrementResolution
                : timeRef + currentModuloTimeBase * currentVol.TimeIncrementResolution;
            
            long timeDiff = (currentTime + currentTimeIncrement) - (lastTime + lastTimeIncr);
            return lastOutputPts + (timeDiff * TimeSpan.TicksPerSecond / currentVol.TimeIncrementResolution);
        }

        return lastOutputPts + frameDurationTicks;
    }

    private long InterpolateDts(long pts)
    {
        // For B-frames or streams without B-frames, DTS == PTS
        if (currentVopType == Mpeg4VopType.Bidirectional || !hasSeenBFrame)
        {
            return pts;
        }

        // For reference frames when B-frames exist, DTS is before PTS
        if (lastRefDts != long.MinValue)
        {
            return lastRefDts;
        }

        return pts;
    }

    private int FindNextStartCode(int startOffset)
    {
        var span = buffer.AsSpan(startOffset, bufferLength - startOffset);
        int pos = FindStartCode(span, span.Length);
        return pos >= 0 ? startOffset + pos : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindStartCode(ReadOnlySpan<byte> data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01)
            {
                return i;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReadBit(ReadOnlySpan<byte> data, ref int bitOffset)
    {
        int byteOffset = bitOffset >> 3;
        int bitInByte = 7 - (bitOffset & 7);
        bitOffset++;
        
        if (byteOffset >= data.Length)
            return false;
            
        return ((data[byteOffset] >> bitInByte) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitOffset, int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (ReadBit(data, ref bitOffset) ? 1 : 0);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(int value)
    {
        if (value <= 0)
            return 0;
        return BitOperations.Log2((uint)value);
    }

    private static (int num, int den) GetParFromAspectRatioInfo(int aspectRatioInfo)
    {
        return aspectRatioInfo switch
        {
            1 => (1, 1),   // 1:1 (square)
            2 => (12, 11), // 12:11 (4:3 PAL)
            3 => (10, 11), // 10:11 (4:3 NTSC)
            4 => (16, 11), // 16:11 (16:9 PAL)
            5 => (40, 33), // 40:33 (16:9 NTSC)
            _ => (1, 1)
        };
    }

    private void TrimBuffer()
    {
        // Only trim if we have a reference point we're preserving
        // If we have an active VOP, keep from its start
        // If we have a pending start code, keep from there
        // Otherwise, keep the last 3 bytes to handle split start codes
        
        int trimPoint;
        
        if (vopStartOffset >= 0)
        {
            // Keep data from VOP start
            trimPoint = vopStartOffset;
        }
        else if (pendingStartCodeOffset >= 0)
        {
            // Keep data from pending start code
            trimPoint = pendingStartCodeOffset;
        }
        else
        {
            // No active parsing state - keep last 3 bytes for potential split start codes
            trimPoint = Math.Max(0, bufferLength - 3);
        }

        if (trimPoint > 0)
        {
            int remaining = bufferLength - trimPoint;
            Buffer.BlockCopy(buffer, trimPoint, buffer, 0, remaining);
            bufferLength = remaining;

            if (vopStartOffset >= 0)
                vopStartOffset -= trimPoint;
            if (pendingStartCodeOffset >= 0)
                pendingStartCodeOffset -= trimPoint;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
            return;

        int newSize = buffer.Length;
        while (newSize < required && newSize < MaxBufferSize)
            newSize *= 2;

        if (newSize > MaxBufferSize)
            throw new InvalidOperationException($"MPEG-4 packetizer buffer exceeded maximum size of {MaxBufferSize} bytes");

        Array.Resize(ref buffer, newSize);
    }

    public void Dispose()
    {
        outputQueue.Clear();
    }
}

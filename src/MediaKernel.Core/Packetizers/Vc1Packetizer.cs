// MediaKernel - VC-1 (WMV9) Video Packetizer
// Parses VC-1 elementary streams into frame units.
// Based on SMPTE 421M (VC-1) and supports Simple, Main, and Advanced profiles.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// VC-1 profile.
/// </summary>
public enum Vc1Profile : byte
{
    /// <summary>Simple profile.</summary>
    Simple = 0,
    /// <summary>Main profile.</summary>
    Main = 1,
    /// <summary>Reserved.</summary>
    Reserved = 2,
    /// <summary>Advanced profile (uses start codes).</summary>
    Advanced = 3
}

/// <summary>
/// VC-1 IDU (Independent Decoding Unit) type.
/// </summary>
public enum Vc1IduType : byte
{
    /// <summary>End of sequence.</summary>
    EndOfSequence = 0x0A,
    /// <summary>Slice level data.</summary>
    Slice = 0x0B,
    /// <summary>Field (interlaced content).</summary>
    Field = 0x0C,
    /// <summary>Frame data.</summary>
    Frame = 0x0D,
    /// <summary>Entry point header.</summary>
    EntryPoint = 0x0E,
    /// <summary>Sequence header.</summary>
    SequenceHeader = 0x0F,
    /// <summary>Slice user data.</summary>
    SliceUserData = 0x1B,
    /// <summary>Field user data.</summary>
    FieldUserData = 0x1C,
    /// <summary>Frame user data.</summary>
    FrameUserData = 0x1D,
    /// <summary>Entry point user data.</summary>
    EntryPointUserData = 0x1E,
    /// <summary>Sequence level user data.</summary>
    SequenceLevelUserData = 0x1F
}

/// <summary>
/// VC-1 frame type (picture coding type).
/// </summary>
public enum Vc1FrameType : byte
{
    /// <summary>Intra-coded (I-frame/keyframe).</summary>
    Intra = 0,
    /// <summary>Predicted (P-frame).</summary>
    Predicted = 1,
    /// <summary>Bidirectional (B-frame).</summary>
    Bidirectional = 2,
    /// <summary>Bidirectional-Intra (BI-frame).</summary>
    BidirectionalIntra = 3,
    /// <summary>Skipped (no data).</summary>
    Skipped = 4
}

/// <summary>
/// Parsed VC-1 sequence header information.
/// </summary>
public readonly struct Vc1SequenceHeader
{
    /// <summary>Profile (Simple, Main, Advanced).</summary>
    public Vc1Profile Profile { get; init; }
    
    /// <summary>Level (0-4 for Advanced profile).</summary>
    public int Level { get; init; }
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Whether content is interlaced.</summary>
    public bool IsInterlaced { get; init; }
    
    /// <summary>Whether stream contains B-frames.</summary>
    public bool HasBFrames { get; init; }
    
    /// <summary>Whether frame interpolation is used.</summary>
    public bool FrameInterpolation { get; init; }
    
    /// <summary>Whether range reduction is enabled (Simple/Main only).</summary>
    public bool RangeReduction { get; init; }
    
    /// <summary>Frame rate numerator.</summary>
    public int FrameRateNum { get; init; }
    
    /// <summary>Frame rate denominator.</summary>
    public int FrameRateDen { get; init; }
    
    /// <summary>Calculated frame rate.</summary>
    public double FrameRate => FrameRateDen > 0 ? (double)FrameRateNum / FrameRateDen : 0;
    
    /// <summary>Sample aspect ratio numerator.</summary>
    public int SarNumerator { get; init; }
    
    /// <summary>Sample aspect ratio denominator.</summary>
    public int SarDenominator { get; init; }
}

/// <summary>
/// Parsed VC-1 frame.
/// </summary>
public readonly struct Vc1Frame
{
    /// <summary>Complete frame data (includes sequence header + entry point for keyframes).</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long DecodeTimestamp { get; init; }
    
    /// <summary>Frame type (I, P, B, BI, Skipped).</summary>
    public Vc1FrameType FrameType { get; init; }
    
    /// <summary>Whether this is a keyframe (I-frame or BI-frame).</summary>
    public bool IsKeyframe => FrameType == Vc1FrameType.Intra || FrameType == Vc1FrameType.BidirectionalIntra;
}

/// <summary>
/// VC-1 video packetizer for WMV9/WMVA content.
/// </summary>
/// <remarks>
/// Converts raw VC-1 elementary stream data into complete frame units.
/// Handles sequence header detection, entry point detection, and frame boundary detection.
/// VC-1 Advanced profile uses Annex-B style start codes (00 00 01 xx).
/// Simple/Main profiles may not have start codes (ASF container provides framing).
/// </remarks>
public sealed class Vc1Packetizer : IDisposable
{
    private const int MaxBufferSize = 16 * 1024 * 1024; // 16MB
    private const int DefaultBufferSize = 512 * 1024;   // 512KB

    private byte[] buffer = new byte[DefaultBufferSize];
    private int bufferLength;
    private readonly List<Vc1Frame> outputQueue = new();

    // Sequence header state
    private Vc1SequenceHeader sequenceHeader;
    private byte[]? sequenceHeaderData;
    private bool hasSequenceHeader;

    // Entry point state
    private byte[]? entryPointData;
    private bool hasEntryPoint;

    // Frame assembly state
    private int frameStartOffset = -1;
    private int pendingStartCodeOffset = -1;
    private long currentPts = -1;
    private long currentDts = -1;
    private long pendingPts = -1;
    private long pendingDts = -1;
    private Vc1FrameType currentFrameType = Vc1FrameType.Intra;
    private bool inFrame;

    // Timestamp interpolation
    private long lastOutputPts = long.MinValue;
    private long lastOutputDts = long.MinValue;
    private long frameDurationTicks;

    /// <summary>Gets the current sequence header (if parsed).</summary>
    public Vc1SequenceHeader? SequenceHeader => hasSequenceHeader ? sequenceHeader : null;

    /// <summary>Gets the frame width from sequence header.</summary>
    public int Width => sequenceHeader.Width;

    /// <summary>Gets the frame height from sequence header.</summary>
    public int Height => sequenceHeader.Height;

    /// <summary>Gets the frame rate from sequence header.</summary>
    public double FrameRate => sequenceHeader.FrameRate;

    /// <summary>Gets whether the stream is interlaced.</summary>
    public bool IsInterlaced => sequenceHeader.IsInterlaced;

    /// <summary>Gets whether sequence header has been parsed.</summary>
    public bool HasSequenceHeader => hasSequenceHeader;

    /// <summary>Gets whether entry point has been parsed.</summary>
    public bool HasEntryPoint => hasEntryPoint;

    /// <summary>Gets the raw sequence header data for codec initialization.</summary>
    public ReadOnlySpan<byte> SequenceHeaderData => sequenceHeaderData;

    /// <summary>
    /// Feeds raw VC-1 elementary stream data.
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
    /// Retrieves the next complete frame.
    /// </summary>
    public bool TryGetFrame(out Vc1Frame frame)
    {
        if (outputQueue.Count > 0)
        {
            frame = outputQueue[0];
            outputQueue.RemoveAt(0);
            return true;
        }

        frame = default;
        return false;
    }

    /// <summary>
    /// Flushes any pending data as a final frame.
    /// </summary>
    public void Flush()
    {
        if (frameStartOffset >= 0 && bufferLength > frameStartOffset)
        {
            EmitFrame(frameStartOffset, bufferLength);
        }

        frameStartOffset = -1;
        bufferLength = 0;
    }

    /// <summary>
    /// Resets the packetizer state (for seeking).
    /// </summary>
    public void Reset()
    {
        bufferLength = 0;
        outputQueue.Clear();
        frameStartOffset = -1;
        pendingStartCodeOffset = -1;
        currentPts = -1;
        currentDts = -1;
        pendingPts = -1;
        pendingDts = -1;
        lastOutputPts = long.MinValue;
        lastOutputDts = long.MinValue;
        inFrame = false;
        // Keep sequence header and entry point - only reset on new stream
    }

    /// <summary>
    /// Clears all state including headers (for new stream).
    /// </summary>
    public void Clear()
    {
        Reset();
        hasSequenceHeader = false;
        hasEntryPoint = false;
        sequenceHeader = default;
        sequenceHeaderData = null;
        entryPointData = null;
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
            var iduType = (Vc1IduType)span[absolutePos + 3];

            // Need at least a few bytes after start code for any parsing
            if (absolutePos + 5 > bufferLength)
            {
                pendingStartCodeOffset = absolutePos;
                break;
            }

            int consumed = HandleIdu(absolutePos, iduType);
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

    private int HandleIdu(int offset, Vc1IduType iduType)
    {
        switch (iduType)
        {
            case Vc1IduType.SequenceHeader:
                return HandleSequenceHeader(offset);

            case Vc1IduType.EntryPoint:
                return HandleEntryPoint(offset);

            case Vc1IduType.Frame:
            case Vc1IduType.Field:
                return HandleFrame(offset, iduType);

            case Vc1IduType.Slice:
                // Slice data belongs to current frame - continue accumulating
                return 0;

            case Vc1IduType.EndOfSequence:
                // End of sequence - emit any pending frame
                if (inFrame && frameStartOffset >= 0 && offset > frameStartOffset)
                {
                    EmitFrame(frameStartOffset, offset);
                }
                return 0;

            case Vc1IduType.FrameUserData:
            case Vc1IduType.FieldUserData:
            case Vc1IduType.SliceUserData:
                // User data belongs to current frame - continue accumulating
                return 0;

            default:
                // Unknown or user data - skip
                return 0;
        }
    }

    private int HandleSequenceHeader(int offset)
    {
        var span = buffer.AsSpan(offset, Math.Min(bufferLength - offset, 64));
        if (span.Length < 8)
            return -1;

        // Parse sequence header
        int bitOffset = 32; // Skip start code

        int profile = ReadBits(span, ref bitOffset, 2);

        if (profile == 3) // Advanced profile
        {
            int level = ReadBits(span, ref bitOffset, 3);

            // Chromat format (2) + frmrtq_postproc (3) + bitrtq_postproc (5) + postprocflag (1)
            ReadBits(span, ref bitOffset, 11);

            int width = (ReadBits(span, ref bitOffset, 12) * 2) + 2;
            int height = (ReadBits(span, ref bitOffset, 12) * 2) + 2;

            // pulldown (1)
            ReadBit(span, ref bitOffset);
            bool interlaced = ReadBit(span, ref bitOffset);
            // tfcntrflag (1)
            ReadBit(span, ref bitOffset);
            bool frameInterpolation = ReadBit(span, ref bitOffset);
            // reserved (1)
            ReadBit(span, ref bitOffset);
            // psf (1)
            ReadBit(span, ref bitOffset);

            int fpsNum = 0, fpsDen = 1;
            int sarNum = 1, sarDen = 1;

            // Display extension
            if (ReadBit(span, ref bitOffset))
            {
                int displayWidth = ReadBits(span, ref bitOffset, 14) + 1;
                int displayHeight = ReadBits(span, ref bitOffset, 14) + 1;

                // Calculate SAR from display vs coded dimensions
                sarNum = displayWidth * height;
                sarDen = displayHeight * width;

                // Aspect ratio info
                if (ReadBit(span, ref bitOffset))
                {
                    int arIndex = ReadBits(span, ref bitOffset, 4);
                    if (arIndex == 15)
                    {
                        sarNum = ReadBits(span, ref bitOffset, 8);
                        sarDen = ReadBits(span, ref bitOffset, 8);
                    }
                    else
                    {
                        (sarNum, sarDen) = GetAspectRatio(arIndex);
                    }
                }
            }

            // Frame rate
            if (ReadBit(span, ref bitOffset))
            {
                if (ReadBit(span, ref bitOffset))
                {
                    fpsNum = ReadBits(span, ref bitOffset, 16) + 1;
                    fpsDen = 32;
                }
                else
                {
                    int nr = ReadBits(span, ref bitOffset, 8);
                    int dn = ReadBits(span, ref bitOffset, 4);

                    fpsNum = nr switch
                    {
                        1 => 24000,
                        2 => 25000,
                        3 => 30000,
                        4 => 50000,
                        5 => 60000,
                        6 => 48000,
                        7 => 72000,
                        _ => 30000
                    };

                    fpsDen = dn switch
                    {
                        1 => 1000,
                        2 => 1001,
                        _ => 1000
                    };
                }
            }

            sequenceHeader = new Vc1SequenceHeader
            {
                Profile = Vc1Profile.Advanced,
                Level = level,
                Width = width,
                Height = height,
                IsInterlaced = interlaced,
                HasBFrames = true, // Advanced profile always supports B-frames
                FrameInterpolation = frameInterpolation,
                RangeReduction = false,
                FrameRateNum = fpsNum,
                FrameRateDen = fpsDen,
                SarNumerator = sarNum,
                SarDenominator = sarDen
            };

            if (fpsNum > 0 && fpsDen > 0)
            {
                frameDurationTicks = (long)(TimeSpan.TicksPerSecond * fpsDen / (double)fpsNum);
            }
            else
            {
                // Default to 30fps if no frame rate is specified
                frameDurationTicks = TimeSpan.TicksPerSecond / 30;
            }
        }
        else
        {
            // Simple/Main profile
            // These don't have dimensions in the sequence header - they come from container
            // Skip: res_sm (2) + frmrtq_postproc (3) + bitrtq_postproc (5) + loop_filter (1)
            //       + res_x8 (1) + multires (1) + res_fasttx (1) + fastuvmc (1) + extended_mv (1)
            //       + dquant (2) + vstransform (1) + res_transtab (1) + overlap (1) + syncmarker (1)
            ReadBits(span, ref bitOffset, 22);
            bool rangeReduction = ReadBit(span, ref bitOffset);
            int maxBFrames = ReadBits(span, ref bitOffset, 3);
            ReadBits(span, ref bitOffset, 2); // quantizer
            bool frameInterpolation = ReadBit(span, ref bitOffset);

            sequenceHeader = new Vc1SequenceHeader
            {
                Profile = (Vc1Profile)profile,
                Level = 0,
                Width = 0, // Unknown - comes from container
                Height = 0,
                IsInterlaced = false,
                HasBFrames = maxBFrames > 0,
                FrameInterpolation = frameInterpolation,
                RangeReduction = rangeReduction,
                FrameRateNum = 30000,
                FrameRateDen = 1001,
                SarNumerator = 1,
                SarDenominator = 1
            };

            frameDurationTicks = TimeSpan.TicksPerSecond / 30;
        }

        hasSequenceHeader = true;

        // Find extent of sequence header (up to next start code)
        int shEnd = FindNextStartCode(offset + 4);
        if (shEnd < 0)
            shEnd = bufferLength;

        sequenceHeaderData = buffer.AsSpan(offset, shEnd - offset).ToArray();

        return 0;
    }

    private int HandleEntryPoint(int offset)
    {
        if (!hasSequenceHeader)
            return 0; // Wait for sequence header first

        // Find extent of entry point (up to next start code)
        int epEnd = FindNextStartCode(offset + 4);
        if (epEnd < 0)
            epEnd = bufferLength;

        entryPointData = buffer.AsSpan(offset, epEnd - offset).ToArray();
        hasEntryPoint = true;

        return 0;
    }

    private int HandleFrame(int offset, Vc1IduType iduType)
    {
        // If we're already in a frame, emit the previous one
        if (inFrame && frameStartOffset >= 0 && offset > frameStartOffset)
        {
            EmitFrame(frameStartOffset, offset);
        }

        if (!hasSequenceHeader)
        {
            // Can't decode frames without sequence header
            return 0;
        }

        // Simple frame type detection - first few bits after start code
        var span = buffer.AsSpan(offset, Math.Min(bufferLength - offset, 8));
        if (span.Length < 5)
            return -1;

        // For Advanced profile, detect frame type from first bits
        if (sequenceHeader.Profile == Vc1Profile.Advanced)
        {
            int bitOffset = 32;

            // Frame coding mode depends on interlaced flag
            if (sequenceHeader.IsInterlaced)
            {
                // FCM field (2 bits if interlaced)
                ReadBits(span, ref bitOffset, 2);
            }

            // Picture type (variable length)
            if (ReadBit(span, ref bitOffset))
            {
                // 1x = P or B
                if (ReadBit(span, ref bitOffset))
                {
                    // 11x = B or BI
                    if (ReadBit(span, ref bitOffset))
                    {
                        currentFrameType = Vc1FrameType.BidirectionalIntra;
                    }
                    else
                    {
                        currentFrameType = Vc1FrameType.Bidirectional;
                    }
                }
                else
                {
                    currentFrameType = Vc1FrameType.Predicted;
                }
            }
            else
            {
                currentFrameType = Vc1FrameType.Intra;
            }
        }
        else
        {
            // Simple/Main profile - assume P-frame, keyframes marked by container
            currentFrameType = Vc1FrameType.Predicted;
        }

        // Start new frame
        frameStartOffset = offset;
        inFrame = true;

        // Consume pending timestamps
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

        return 0;
    }

    private void EmitFrame(int startOffset, int endOffset)
    {
        if (!hasSequenceHeader)
        {
            frameStartOffset = -1;
            inFrame = false;
            return;
        }

        int length = endOffset - startOffset;
        if (length <= 4)
        {
            frameStartOffset = -1;
            inFrame = false;
            return;
        }

        // Determine timestamps
        long pts = currentPts >= 0 ? currentPts : InterpolatePts();
        long dts = currentDts >= 0 ? currentDts : InterpolateDts(pts);

        lastOutputPts = pts;
        lastOutputDts = dts;

        // Build frame data - prepend sequence header + entry point for keyframes
        byte[] frameData;
        bool isKeyframe = currentFrameType == Vc1FrameType.Intra || currentFrameType == Vc1FrameType.BidirectionalIntra;

        if (isKeyframe && sequenceHeaderData != null && entryPointData != null)
        {
            frameData = new byte[sequenceHeaderData.Length + entryPointData.Length + length];
            sequenceHeaderData.CopyTo(frameData, 0);
            entryPointData.CopyTo(frameData, sequenceHeaderData.Length);
            buffer.AsSpan(startOffset, length).CopyTo(frameData.AsSpan(sequenceHeaderData.Length + entryPointData.Length));
        }
        else if (isKeyframe && sequenceHeaderData != null)
        {
            frameData = new byte[sequenceHeaderData.Length + length];
            sequenceHeaderData.CopyTo(frameData, 0);
            buffer.AsSpan(startOffset, length).CopyTo(frameData.AsSpan(sequenceHeaderData.Length));
        }
        else
        {
            frameData = buffer.AsSpan(startOffset, length).ToArray();
        }

        var frame = new Vc1Frame
        {
            Data = frameData,
            PresentationTimestamp = pts,
            DecodeTimestamp = dts,
            FrameType = currentFrameType
        };

        outputQueue.Add(frame);
        frameStartOffset = -1;
        inFrame = false;
        currentPts = -1;
        currentDts = -1;
    }

    private long InterpolatePts()
    {
        if (lastOutputPts == long.MinValue)
            return 0;

        return lastOutputPts + frameDurationTicks;
    }

    private long InterpolateDts(long pts)
    {
        // For B-frames or streams without B-frames, DTS == PTS
        if (currentFrameType == Vc1FrameType.Bidirectional ||
            currentFrameType == Vc1FrameType.BidirectionalIntra ||
            !sequenceHeader.HasBFrames)
        {
            return pts;
        }

        if (lastOutputDts != long.MinValue)
        {
            return lastOutputDts + frameDurationTicks;
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

    private static (int num, int den) GetAspectRatio(int index)
    {
        return index switch
        {
            1 => (1, 1),
            2 => (12, 11),
            3 => (10, 11),
            4 => (16, 11),
            5 => (40, 33),
            6 => (24, 11),
            7 => (20, 11),
            8 => (32, 11),
            9 => (80, 33),
            10 => (18, 11),
            11 => (15, 11),
            12 => (64, 33),
            13 => (160, 99),
            _ => (1, 1)
        };
    }

    private void TrimBuffer()
    {
        int trimPoint;

        if (frameStartOffset >= 0)
        {
            trimPoint = frameStartOffset;
        }
        else if (pendingStartCodeOffset >= 0)
        {
            trimPoint = pendingStartCodeOffset;
        }
        else
        {
            trimPoint = Math.Max(0, bufferLength - 3);
        }

        if (trimPoint > 0)
        {
            int remaining = bufferLength - trimPoint;
            Buffer.BlockCopy(buffer, trimPoint, buffer, 0, remaining);
            bufferLength = remaining;

            if (frameStartOffset >= 0)
                frameStartOffset -= trimPoint;
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
            throw new InvalidOperationException($"VC-1 packetizer buffer exceeded maximum size of {MaxBufferSize} bytes");

        Array.Resize(ref buffer, newSize);
    }

    public void Dispose()
    {
        outputQueue.Clear();
    }
}

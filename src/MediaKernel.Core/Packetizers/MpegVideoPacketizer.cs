// MediaKernel - MPEG-1/2 Video Packetizer
// Parses MPEG video elementary streams into picture units for decoding.
// Based on ISO/IEC 11172-2 (MPEG-1) and ISO/IEC 13818-2 (MPEG-2)

using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.Mpeg2;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// MPEG video version.
/// </summary>
public enum MpegVideoVersion : byte
{
    /// <summary>Unknown version.</summary>
    Unknown = 0,
    /// <summary>MPEG-1 (ISO/IEC 11172-2).</summary>
    Mpeg1 = 1,
    /// <summary>MPEG-2 (ISO/IEC 13818-2).</summary>
    Mpeg2 = 2
}

/// <summary>
/// Parsed MPEG video picture information.
/// </summary>
public readonly struct MpegVideoPicture
{
    /// <summary>Picture data including all slices.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long DecodeTimestamp { get; init; }
    
    /// <summary>Picture type (I, P, B).</summary>
    public Mpeg2PictureType PictureType { get; init; }
    
    /// <summary>Whether this is a keyframe (I-frame).</summary>
    public bool IsKeyframe => PictureType == Mpeg2PictureType.Intra;
    
    /// <summary>Temporal reference (0-1023).</summary>
    public int TemporalReference { get; init; }
    
    /// <summary>Picture structure (frame or field).</summary>
    public Mpeg2PictureStructure PictureStructure { get; init; }
    
    /// <summary>Whether the frame is progressive.</summary>
    public bool IsProgressive { get; init; }
}

/// <summary>
/// MPEG-1/2 video stream packetizer.
/// </summary>
/// <remarks>
/// Converts raw MPEG video elementary stream data into complete picture units.
/// Handles sequence header detection, picture boundary detection, and GOP markers.
/// </remarks>
public sealed class MpegVideoPacketizer : IDisposable
{
    private const int MaxBufferSize = 16 * 1024 * 1024; // 16MB max
    private const int DefaultBufferSize = 512 * 1024; // 512KB default
    
    private byte[] buffer = new byte[DefaultBufferSize];
    private int bufferLength;
    private readonly List<MpegVideoPicture> outputQueue = new();
    
    // Current sequence state
    private Mpeg2SequenceHeader? currentSequenceHeader;
    private byte[]? sequenceHeaderData;
    private Mpeg2GopHeader? currentGopHeader;
    
    // Current picture assembly state
    private int pictureStartOffset = -1;
    private int pendingStartCodeOffset = -1; // Offset of start code we need more data for
    private long currentPts = -1;
    private long currentDts = -1;
    private long pendingPts = -1;
    private long pendingDts = -1;
    private Mpeg2PictureHeader? currentPictureHeader;
    private bool foundSequenceHeader;
    private bool injectSequenceHeader = true;
    
    // Timestamp interpolation
    private long lastOutputPts = long.MinValue;
    private long lastOutputDts = long.MinValue;
    private long frameDurationTicks;
    
    /// <summary>Gets the detected video version.</summary>
    public MpegVideoVersion Version { get; private set; } = MpegVideoVersion.Unknown;
    
    /// <summary>Gets the current sequence header (if parsed).</summary>
    public Mpeg2SequenceHeader? SequenceHeader => currentSequenceHeader;
    
    /// <summary>Gets the frame width from the sequence header.</summary>
    public int Width => currentSequenceHeader?.Width ?? 0;
    
    /// <summary>Gets the frame height from the sequence header.</summary>
    public int Height => currentSequenceHeader?.Height ?? 0;
    
    /// <summary>Gets the frame rate from the sequence header.</summary>
    public double FrameRate => currentSequenceHeader?.FrameRate ?? 0;
    
    /// <summary>Gets whether the stream is progressive.</summary>
    public bool IsProgressive => currentSequenceHeader?.IsProgressive ?? true;
    
    /// <summary>Gets or sets whether to inject sequence header before keyframes.</summary>
    public bool InjectSequenceHeader
    {
        get => injectSequenceHeader;
        set => injectSequenceHeader = value;
    }
    
    /// <summary>
    /// Gets the raw sequence header data for codec initialization.
    /// </summary>
    public ReadOnlySpan<byte> SequenceHeaderData => sequenceHeaderData;
    
    /// <summary>
    /// Feeds raw MPEG video elementary stream data.
    /// </summary>
    /// <param name="data">Raw ES data.</param>
    /// <param name="pts">Presentation timestamp in ticks (-1 if unknown).</param>
    /// <param name="dts">Decode timestamp in ticks (-1 if unknown).</param>
    public void Feed(ReadOnlySpan<byte> data, long pts = -1, long dts = -1)
    {
        if (data.Length == 0)
            return;
        
        // Store timestamp for the picture that will be discovered in this data.
        // If we're currently in a picture, this timestamp is for the NEXT picture.
        if (pts >= 0)
            pendingPts = pts;
        if (dts >= 0)
            pendingDts = dts;
        
        // Ensure buffer capacity
        EnsureCapacity(bufferLength + data.Length);
        
        // Append data to buffer
        data.CopyTo(buffer.AsSpan(bufferLength));
        bufferLength += data.Length;
        
        // Parse start codes
        ParseBuffer();
    }
    
    /// <summary>
    /// Retrieves the next complete picture.
    /// </summary>
    public bool TryGetPicture(out MpegVideoPicture picture)
    {
        if (outputQueue.Count > 0)
        {
            picture = outputQueue[0];
            outputQueue.RemoveAt(0);
            return true;
        }
        
        picture = default;
        return false;
    }
    
    /// <summary>
    /// Flushes any pending data as a final picture.
    /// </summary>
    public void Flush()
    {
        if (pictureStartOffset >= 0 && bufferLength > pictureStartOffset)
        {
            EmitPicture(pictureStartOffset, bufferLength);
        }
        
        pictureStartOffset = -1;
        bufferLength = 0;
    }
    
    /// <summary>
    /// Resets the packetizer state (for seeking).
    /// </summary>
    public void Reset()
    {
        bufferLength = 0;
        pictureStartOffset = -1;
        pendingStartCodeOffset = -1;
        currentPts = -1;
        currentDts = -1;
        pendingPts = -1;
        pendingDts = -1;
        currentPictureHeader = null;
        lastOutputPts = long.MinValue;
        lastOutputDts = long.MinValue;
        outputQueue.Clear();
    }
    
    /// <summary>
    /// Clears all state including sequence header.
    /// </summary>
    public void Clear()
    {
        Reset();
        currentSequenceHeader = null;
        sequenceHeaderData = null;
        currentGopHeader = null;
        foundSequenceHeader = false;
        Version = MpegVideoVersion.Unknown;
    }
    
    private void ParseBuffer()
    {
        int searchOffset = 0;
        pendingStartCodeOffset = -1;
        
        while (searchOffset + 4 <= bufferLength)
        {
            // Find next start code
            int startCodeOffset = FindStartCode(buffer.AsSpan(searchOffset, bufferLength - searchOffset));
            if (startCodeOffset < 0)
                break;
            
            int absoluteOffset = searchOffset + startCodeOffset;
            byte startCode = buffer[absoluteOffset + 3];
            
            // Process start code
            bool handled = HandleStartCode(absoluteOffset, startCode);
            if (!handled)
            {
                // Need more data for this start code - don't trim past it
                pendingStartCodeOffset = absoluteOffset;
                break;
            }
            
            searchOffset = absoluteOffset + 4;
        }
        
        // Keep unprocessed data (from picture start or pending start code, otherwise minimal overlap)
        int keepFrom = -1;
        
        if (pictureStartOffset >= 0)
        {
            keepFrom = pictureStartOffset;
        }
        else if (pendingStartCodeOffset >= 0)
        {
            keepFrom = pendingStartCodeOffset;
        }
        
        if (keepFrom >= 0 && keepFrom < bufferLength)
        {
            if (keepFrom > 0)
            {
                int remaining = bufferLength - keepFrom;
                Buffer.BlockCopy(buffer, keepFrom, buffer, 0, remaining);
                bufferLength = remaining;
            }
            
            // Adjust offsets
            if (pictureStartOffset >= keepFrom)
                pictureStartOffset -= keepFrom;
            if (pendingStartCodeOffset >= keepFrom)
                pendingStartCodeOffset -= keepFrom;
        }
        else if (bufferLength > 3)
        {
            // Keep last 3 bytes for partial start code detection
            Buffer.BlockCopy(buffer, bufferLength - 3, buffer, 0, 3);
            bufferLength = 3;
        }
    }
    
    /// <summary>
    /// Returns true if the start code was fully handled, false if more data is needed.
    /// </summary>
    private bool HandleStartCode(int offset, byte startCode)
    {
        var data = buffer.AsSpan(offset, bufferLength - offset);
        
        switch (startCode)
        {
            case Mpeg2StartCodes.Sequence:
                return HandleSequenceHeader(offset, data);
                
            case Mpeg2StartCodes.GroupOfPictures:
                return HandleGopHeader(data);
                
            case Mpeg2StartCodes.Picture:
                return HandlePictureHeader(offset, data);
                
            case Mpeg2StartCodes.Extension:
                HandleExtension(data);
                return true;
                
            case Mpeg2StartCodes.SequenceEnd:
                // Flush current picture and mark end
                if (pictureStartOffset >= 0)
                {
                    EmitPicture(pictureStartOffset, offset + 4);
                    pictureStartOffset = -1;
                }
                return true;
                
            default:
                // Slice start codes (0x01-0xAF) are part of picture data
                return true;
        }
    }
    
    private bool HandleSequenceHeader(int offset, ReadOnlySpan<byte> data)
    {
        // Need at least 12 bytes for sequence header
        if (data.Length < 12)
            return false;
        
        // If we're in a picture, emit it first
        if (pictureStartOffset >= 0 && pictureStartOffset < offset)
        {
            EmitPicture(pictureStartOffset, offset);
            pictureStartOffset = -1;
        }
        
        // Parse sequence header
        if (Mpeg2HeaderParser.TryParseSequenceHeader(data, out var seqHeader, out int bytesConsumed))
        {
            currentSequenceHeader = seqHeader;
            foundSequenceHeader = true;
            
            // Update frame duration for timestamp interpolation
            if (seqHeader.FrameRateDenominator > 0 && seqHeader.FrameRateNumerator > 0)
            {
                frameDurationTicks = (long)(TimeSpan.TicksPerSecond * seqHeader.FrameRateDenominator / seqHeader.FrameRateNumerator);
            }
            
            // Detect MPEG version (MPEG-2 has sequence extension)
            Version = seqHeader.HasExtension ? MpegVideoVersion.Mpeg2 : MpegVideoVersion.Mpeg1;
            
            // Store raw sequence header data for codec init
            if (bytesConsumed > 0 && offset + bytesConsumed <= bufferLength)
            {
                sequenceHeaderData = data.Slice(0, bytesConsumed).ToArray();
            }
        }
        
        return true;
    }
    
    private bool HandleGopHeader(ReadOnlySpan<byte> data)
    {
        // Need at least 8 bytes for GOP header
        if (data.Length < 8)
            return false;
        
        if (Mpeg2HeaderParser.TryParseGopHeader(data, out var gopHeader))
        {
            currentGopHeader = gopHeader;
        }
        
        return true;
    }
    
    private bool HandlePictureHeader(int offset, ReadOnlySpan<byte> data)
    {
        // Need at least 9 bytes for picture header
        if (data.Length < 9)
            return false;
        
        // Skip if this is the same picture start code we're already tracking
        // (happens after buffer trimming when picture start moves to offset 0)
        if (pictureStartOffset == offset)
            return true;
        
        // If we're already in a picture, emit the previous one
        if (pictureStartOffset >= 0 && pictureStartOffset < offset)
        {
            EmitPicture(pictureStartOffset, offset);
        }
        
        // Start new picture with pending timestamps
        pictureStartOffset = offset;
        currentPts = pendingPts;
        currentDts = pendingDts;
        pendingPts = -1;
        pendingDts = -1;
        
        // For keyframes, include sequence header if we should inject it
        if (injectSequenceHeader && sequenceHeaderData != null && foundSequenceHeader)
        {
            // Check if this picture is preceded by a sequence header
            // If not, we need to remember we should prepend it when emitting
        }
        
        // Parse picture header
        if (Mpeg2HeaderParser.TryParsePictureHeader(data, out var picHeader, out _))
        {
            currentPictureHeader = picHeader;
        }
        
        return true;
    }
    
    private void HandleExtension(ReadOnlySpan<byte> data)
    {
        // Extension start codes are parsed within sequence/picture header parsing
        // This is here for completeness if we need to handle user data etc.
    }
    
    private void EmitPicture(int startOffset, int endOffset)
    {
        if (!currentPictureHeader.HasValue || endOffset <= startOffset)
        {
            currentPictureHeader = null;
            currentPts = -1;
            currentDts = -1;
            return;
        }
        
        var picHeader = currentPictureHeader.Value;
        
        // Calculate timestamps
        long pts = currentPts;
        long dts = currentDts;
        
        // Interpolate timestamps if missing
        if (pts < 0 && lastOutputPts >= 0 && frameDurationTicks > 0)
        {
            // For display order, account for B-frames
            pts = lastOutputPts + frameDurationTicks;
        }
        
        if (dts < 0 && lastOutputDts >= 0 && frameDurationTicks > 0)
        {
            // DTS is decode order
            dts = lastOutputDts + frameDurationTicks;
        }
        
        // Build picture data
        int dataLength = endOffset - startOffset;
        byte[] pictureData;
        
        // Prepend sequence header for keyframes if configured
        if (injectSequenceHeader && picHeader.PictureType == Mpeg2PictureType.Intra && 
            sequenceHeaderData != null)
        {
            // Check if sequence header already precedes this picture in buffer
            bool hasSeqHeader = false;
            if (startOffset >= sequenceHeaderData.Length)
            {
                var preceding = buffer.AsSpan(startOffset - sequenceHeaderData.Length, sequenceHeaderData.Length);
                hasSeqHeader = preceding.SequenceEqual(sequenceHeaderData);
            }
            
            if (!hasSeqHeader)
            {
                pictureData = new byte[sequenceHeaderData.Length + dataLength];
                sequenceHeaderData.CopyTo(pictureData, 0);
                buffer.AsSpan(startOffset, dataLength).CopyTo(pictureData.AsSpan(sequenceHeaderData.Length));
            }
            else
            {
                pictureData = buffer.AsSpan(startOffset, dataLength).ToArray();
            }
        }
        else
        {
            pictureData = buffer.AsSpan(startOffset, dataLength).ToArray();
        }
        
        var picture = new MpegVideoPicture
        {
            Data = pictureData,
            PresentationTimestamp = pts,
            DecodeTimestamp = dts >= 0 ? dts : pts,
            PictureType = picHeader.PictureType,
            TemporalReference = picHeader.TemporalReference,
            PictureStructure = picHeader.PictureStructure,
            IsProgressive = picHeader.ProgressiveFrame
        };
        
        outputQueue.Add(picture);
        
        // Update last timestamps
        if (pts >= 0)
            lastOutputPts = pts;
        if (dts >= 0)
            lastOutputDts = dts;
        
        // Reset for next picture
        currentPictureHeader = null;
        currentPts = -1;
        currentDts = -1;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindStartCode(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i;
            }
        }
        return -1;
    }
    
    private void EnsureCapacity(int required)
    {
        if (buffer.Length >= required)
            return;
        
        int newSize = Math.Min(Math.Max(buffer.Length * 2, required), MaxBufferSize);
        if (newSize < required)
            throw new InvalidOperationException($"Buffer size limit exceeded: {required} bytes required");
        
        Array.Resize(ref buffer, newSize);
    }
    
    public void Dispose()
    {
        outputQueue.Clear();
        buffer = Array.Empty<byte>();
        bufferLength = 0;
    }
}

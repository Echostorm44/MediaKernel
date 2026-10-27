// MjpegPacketizer - Parses MJPEG streams into complete JPEG frames
// Handles SOI/EOI markers, SOF parsing for dimensions, and timestamp interpolation

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// JPEG marker types used in MJPEG streams.
/// </summary>
public enum JpegMarker : byte
{
    /// <summary>Start of Image.</summary>
    SOI = 0xD8,

    /// <summary>End of Image.</summary>
    EOI = 0xD9,

    /// <summary>Start of Frame (Baseline DCT).</summary>
    SOF0 = 0xC0,

    /// <summary>Start of Frame (Extended Sequential DCT).</summary>
    SOF1 = 0xC1,

    /// <summary>Start of Frame (Progressive DCT).</summary>
    SOF2 = 0xC2,

    /// <summary>Start of Frame (Lossless).</summary>
    SOF3 = 0xC3,

    /// <summary>Define Huffman Table.</summary>
    DHT = 0xC4,

    /// <summary>Start of Frame (Differential Sequential DCT).</summary>
    SOF5 = 0xC5,

    /// <summary>Start of Frame (Differential Progressive DCT).</summary>
    SOF6 = 0xC6,

    /// <summary>Start of Frame (Differential Lossless).</summary>
    SOF7 = 0xC7,

    /// <summary>Start of Frame (Extended Sequential DCT, Arithmetic).</summary>
    SOF9 = 0xC9,

    /// <summary>Start of Frame (Progressive DCT, Arithmetic).</summary>
    SOF10 = 0xCA,

    /// <summary>Start of Frame (Lossless, Arithmetic).</summary>
    SOF11 = 0xCB,

    /// <summary>Define Arithmetic Coding.</summary>
    DAC = 0xCC,

    /// <summary>Start of Frame (Differential Sequential DCT, Arithmetic).</summary>
    SOF13 = 0xCD,

    /// <summary>Start of Frame (Differential Progressive DCT, Arithmetic).</summary>
    SOF14 = 0xCE,

    /// <summary>Start of Frame (Differential Lossless, Arithmetic).</summary>
    SOF15 = 0xCF,

    /// <summary>Restart with modulo 8 count 0.</summary>
    RST0 = 0xD0,

    /// <summary>Define Quantization Table.</summary>
    DQT = 0xDB,

    /// <summary>Define Restart Interval.</summary>
    DRI = 0xDD,

    /// <summary>Start of Scan.</summary>
    SOS = 0xDA,

    /// <summary>Application segment 0 (JFIF).</summary>
    APP0 = 0xE0,

    /// <summary>Application segment 1 (EXIF).</summary>
    APP1 = 0xE1,

    /// <summary>Comment.</summary>
    COM = 0xFE
}

/// <summary>
/// Represents a complete MJPEG frame.
/// </summary>
public readonly struct MjpegFrame
{
    /// <summary>Complete JPEG data from SOI to EOI.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp { get; init; }

    /// <summary>Decode timestamp in ticks (same as PTS for MJPEG).</summary>
    public long DecodeTimestamp { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Number of color components (1=grayscale, 3=YCbCr, 4=CMYK).</summary>
    public int ComponentCount { get; init; }

    /// <summary>Sample precision in bits (typically 8).</summary>
    public int BitDepth { get; init; }

    /// <summary>Whether this frame is a keyframe (always true for MJPEG).</summary>
    public bool IsKeyframe => true;
}

/// <summary>
/// MJPEG packetizer that parses JPEG streams from video containers.
/// Handles SOI/EOI boundary detection, SOF parsing for dimensions, and timestamp management.
/// </summary>
public sealed class MjpegPacketizer
{
    private const int DefaultBufferSize = 65536;
    private const int MaxFrameSize = 16 * 1024 * 1024; // 16 MB max frame

    private byte[] buffer = new byte[DefaultBufferSize];
    private int bufferLength;

    private readonly List<MjpegFrame> outputQueue = new();

    // Frame parsing state
    private int frameStartOffset = -1;
    private bool inFrame;

    // Timestamp state
    private long pendingPts = -1;
    private long pendingDts = -1;
    private long currentPts = -1;
    private long currentDts = -1;
    private long lastOutputPts = long.MinValue;
    private long lastOutputDts = long.MinValue;
    private long frameDurationTicks;

    // Frame info from SOF
    private int currentWidth;
    private int currentHeight;
    private int currentComponentCount;
    private int currentBitDepth;

    // Stream info
    private int width;
    private int height;
    private int frameRateNum = 30000;
    private int frameRateDen = 1001;

    /// <summary>Creates a new MJPEG packetizer.</summary>
    public MjpegPacketizer()
    {
        frameDurationTicks = TimeSpan.TicksPerSecond * frameRateDen / frameRateNum;
    }

    /// <summary>Creates a new MJPEG packetizer with specified frame rate.</summary>
    /// <param name="fps">Frame rate in frames per second.</param>
    public MjpegPacketizer(double fps)
    {
        if (fps > 0)
        {
            frameDurationTicks = (long)(TimeSpan.TicksPerSecond / fps);
            frameRateNum = (int)(fps * 1000);
            frameRateDen = 1000;
        }
        else
        {
            frameDurationTicks = TimeSpan.TicksPerSecond / 30;
        }
    }

    /// <summary>Gets the detected frame width in pixels.</summary>
    public int Width => width;

    /// <summary>Gets the detected frame height in pixels.</summary>
    public int Height => height;

    /// <summary>Gets the frame rate numerator.</summary>
    public int FrameRateNum => frameRateNum;

    /// <summary>Gets the frame rate denominator.</summary>
    public int FrameRateDen => frameRateDen;

    /// <summary>Gets the calculated frame rate.</summary>
    public double FrameRate => frameRateDen > 0 ? (double)frameRateNum / frameRateDen : 30.0;

    /// <summary>Gets the number of pending output frames.</summary>
    public int PendingFrameCount => outputQueue.Count;

    /// <summary>
    /// Sets the frame rate for timestamp interpolation.
    /// </summary>
    /// <param name="num">Frame rate numerator.</param>
    /// <param name="den">Frame rate denominator.</param>
    public void SetFrameRate(int num, int den)
    {
        if (num > 0 && den > 0)
        {
            frameRateNum = num;
            frameRateDen = den;
            frameDurationTicks = TimeSpan.TicksPerSecond * den / num;
        }
    }

    /// <summary>
    /// Feeds data to the packetizer.
    /// </summary>
    /// <param name="data">Raw JPEG data.</param>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        EnsureCapacity(bufferLength + data.Length);

        data.CopyTo(buffer.AsSpan(bufferLength));
        bufferLength += data.Length;

        ParseBuffer();
    }

    /// <summary>
    /// Feeds data with timestamps to the packetizer.
    /// </summary>
    /// <param name="data">Raw JPEG data.</param>
    /// <param name="pts">Presentation timestamp in ticks.</param>
    /// <param name="dts">Decode timestamp in ticks.</param>
    public void Feed(ReadOnlySpan<byte> data, long pts, long dts)
    {
        pendingPts = pts;
        pendingDts = dts;
        Feed(data);
    }

    /// <summary>
    /// Retrieves the next complete frame.
    /// </summary>
    public bool TryGetFrame(out MjpegFrame frame)
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
        if (inFrame && frameStartOffset >= 0 && bufferLength > frameStartOffset + 2)
        {
            // Try to find EOI in remaining buffer
            int eoiPos = FindEOI(frameStartOffset + 2, bufferLength);
            if (eoiPos >= 0)
            {
                EmitFrame(frameStartOffset, eoiPos + 2);
            }
            else
            {
                // Force emit without proper EOI (truncated stream)
                EmitFrame(frameStartOffset, bufferLength);
            }
        }

        frameStartOffset = -1;
        bufferLength = 0;
        inFrame = false;
    }

    /// <summary>
    /// Resets the packetizer state (for seeking).
    /// </summary>
    public void Reset()
    {
        bufferLength = 0;
        outputQueue.Clear();
        frameStartOffset = -1;
        inFrame = false;
        currentPts = -1;
        currentDts = -1;
        pendingPts = -1;
        pendingDts = -1;
        lastOutputPts = long.MinValue;
        lastOutputDts = long.MinValue;
    }

    /// <summary>
    /// Clears all state including detected dimensions.
    /// </summary>
    public void Clear()
    {
        Reset();
        width = 0;
        height = 0;
    }

    private void ParseBuffer()
    {
        int offset = 0;

        while (offset < bufferLength - 1)
        {
            if (!inFrame)
            {
                // Look for SOI marker (FF D8)
                int soiPos = FindSOI(offset);
                if (soiPos < 0)
                {
                    // No SOI found, discard up to last byte (might be start of FF)
                    if (bufferLength > 1)
                    {
                        buffer[0] = buffer[bufferLength - 1];
                        bufferLength = 1;
                    }
                    return;
                }

                frameStartOffset = soiPos;
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

                // Reset frame info
                currentWidth = 0;
                currentHeight = 0;
                currentComponentCount = 0;
                currentBitDepth = 0;

                offset = soiPos + 2;
            }
            else
            {
                // Look for EOI marker (FF D9) or parse markers
                int eoiPos = FindEOI(offset, bufferLength);
                if (eoiPos >= 0)
                {
                    // Parse markers for frame info before emitting
                    ParseMarkers(frameStartOffset + 2, eoiPos);
                    EmitFrame(frameStartOffset, eoiPos + 2);
                    offset = eoiPos + 2;
                }
                else
                {
                    // No EOI yet, need more data
                    // But parse any markers we can see
                    ParseMarkers(offset, bufferLength);
                    break;
                }
            }
        }

        TrimBuffer();
    }

    private void ParseMarkers(int startOffset, int endOffset)
    {
        int offset = startOffset;

        while (offset < endOffset - 3)
        {
            // Look for marker
            if (buffer[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = buffer[offset + 1];

            // Skip stuffed bytes and RST markers
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
            {
                offset += 2;
                continue;
            }

            // SOI/EOI have no length
            if (marker == (byte)JpegMarker.SOI || marker == (byte)JpegMarker.EOI)
            {
                offset += 2;
                continue;
            }

            // Check we have length bytes
            if (offset + 4 > endOffset)
                break;

            int length = (buffer[offset + 2] << 8) | buffer[offset + 3];
            if (length < 2 || offset + 2 + length > endOffset)
                break;

            // Parse SOF markers for dimensions
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (length >= 7)
                {
                    currentBitDepth = buffer[offset + 4];
                    currentHeight = (buffer[offset + 5] << 8) | buffer[offset + 6];
                    currentWidth = (buffer[offset + 7] << 8) | buffer[offset + 8];

                    if (length >= 9)
                    {
                        currentComponentCount = buffer[offset + 9];
                    }

                    // Update stream dimensions if this is first frame
                    if (width == 0 && currentWidth > 0)
                    {
                        width = currentWidth;
                        height = currentHeight;
                    }
                }
            }

            offset += 2 + length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSOI(int startOffset)
    {
        var span = buffer.AsSpan(startOffset, bufferLength - startOffset);

        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == 0xFF && span[i + 1] == (byte)JpegMarker.SOI)
                return startOffset + i;
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindEOI(int startOffset, int endOffset)
    {
        var span = buffer.AsSpan(startOffset, endOffset - startOffset);

        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == 0xFF && span[i + 1] == (byte)JpegMarker.EOI)
                return startOffset + i;
        }

        return -1;
    }

    private void EmitFrame(int startOffset, int endOffset)
    {
        int length = endOffset - startOffset;
        if (length <= 4)
        {
            frameStartOffset = -1;
            inFrame = false;
            currentPts = -1;
            currentDts = -1;
            return;
        }

        // Determine timestamps
        long pts = currentPts >= 0 ? currentPts : InterpolatePts();
        long dts = pts; // DTS == PTS for MJPEG (no B-frames)

        lastOutputPts = pts;
        lastOutputDts = dts;

        var frameData = buffer.AsSpan(startOffset, length).ToArray();

        var frame = new MjpegFrame
        {
            Data = frameData,
            PresentationTimestamp = pts,
            DecodeTimestamp = dts,
            Width = currentWidth > 0 ? currentWidth : width,
            Height = currentHeight > 0 ? currentHeight : height,
            ComponentCount = currentComponentCount,
            BitDepth = currentBitDepth
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

    private void TrimBuffer()
    {
        if (!inFrame && frameStartOffset < 0)
        {
            // No frame in progress - keep only last byte for potential FF marker
            if (bufferLength > 1)
            {
                buffer[0] = buffer[bufferLength - 1];
                bufferLength = 1;
            }
            return;
        }

        // Keep data from frame start
        int keepFrom = frameStartOffset >= 0 ? frameStartOffset : 0;
        if (keepFrom > 0)
        {
            Buffer.BlockCopy(buffer, keepFrom, buffer, 0, bufferLength - keepFrom);
            bufferLength -= keepFrom;
            if (frameStartOffset >= 0)
                frameStartOffset -= keepFrom;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (buffer.Length >= required)
            return;

        if (required > MaxFrameSize)
            throw new InvalidOperationException($"MJPEG frame exceeds maximum size of {MaxFrameSize} bytes");

        int newSize = Math.Max(buffer.Length * 2, required);
        Array.Resize(ref buffer, newSize);
    }
}

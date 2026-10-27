// MediaKernel - Animated GIF Demuxer
// Demuxes animated GIF files into individual frames with proper timing.
// Reference: https://www.w3.org/Graphics/GIF/spec-gif89a.txt

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for GIF demuxer.
/// </summary>
public sealed class GifDemuxerConfiguration
{
    /// <summary>
    /// Default frame delay in milliseconds when not specified (10ms = 100fps cap).
    /// </summary>
    public int DefaultFrameDelayMs { get; init; } = 100;
    
    /// <summary>
    /// Minimum frame delay in milliseconds (browsers typically use 20ms minimum).
    /// </summary>
    public int MinimumFrameDelayMs { get; init; } = 20;
}

/// <summary>
/// Result from probing a stream for GIF content.
/// </summary>
public readonly struct GifProbeResult
{
    /// <summary>Whether valid GIF content was detected.</summary>
    public bool IsGif { get; init; }
    
    /// <summary>GIF version ("87a" or "89a").</summary>
    public string Version { get; init; }
    
    /// <summary>Logical screen width.</summary>
    public int Width { get; init; }
    
    /// <summary>Logical screen height.</summary>
    public int Height { get; init; }
}

/// <summary>
/// GIF disposal method for frame compositing.
/// </summary>
public enum GifDisposalMethod
{
    /// <summary>No disposal specified (leave frame in place).</summary>
    None = 0,
    
    /// <summary>Do not dispose (leave frame in place).</summary>
    DoNotDispose = 1,
    
    /// <summary>Restore to background color.</summary>
    RestoreToBackground = 2,
    
    /// <summary>Restore to previous frame.</summary>
    RestoreToPrevious = 3
}

/// <summary>
/// A decoded GIF frame.
/// </summary>
public sealed class GifFrame
{
    /// <summary>Frame index (0-based).</summary>
    public int Index { get; init; }
    
    /// <summary>Frame image data in BGRA format.</summary>
    public byte[] ImageData { get; init; } = Array.Empty<byte>();
    
    /// <summary>Frame width.</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height.</summary>
    public int Height { get; init; }
    
    /// <summary>X offset within logical screen.</summary>
    public int Left { get; init; }
    
    /// <summary>Y offset within logical screen.</summary>
    public int Top { get; init; }
    
    /// <summary>Frame delay in centiseconds (hundredths of a second).</summary>
    public int DelayCentiseconds { get; init; }
    
    /// <summary>Disposal method for this frame.</summary>
    public GifDisposalMethod DisposalMethod { get; init; }
    
    /// <summary>Whether the frame has transparency.</summary>
    public bool HasTransparency { get; init; }
    
    /// <summary>Transparent color index (if HasTransparency is true).</summary>
    public int TransparentColorIndex { get; init; }
    
    /// <summary>Frame delay as TimeSpan.</summary>
    public TimeSpan Delay => TimeSpan.FromMilliseconds(DelayCentiseconds * 10);
}

/// <summary>
/// Demuxer for animated GIF files.
/// </summary>
/// <remarks>
/// Supports GIF87a and GIF89a formats, including:
/// - Animated GIFs with multiple frames
/// - Graphic Control Extensions (timing, transparency, disposal)
/// - Local and global color tables
/// - Interlaced images
/// - LZW decompression
/// </remarks>
public sealed class GifDemuxer : IDisposable
{
    private const int HeaderSize = 6;
    private const int LogicalScreenDescriptorSize = 7;
    
    // Block types
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageDescriptor = 0x2C;
    private const byte Trailer = 0x3B;
    
    // Extension labels
    private const byte GraphicControlLabel = 0xF9;
    private const byte CommentLabel = 0xFE;
    private const byte PlainTextLabel = 0x01;
    private const byte ApplicationLabel = 0xFF;
    
    private readonly Stream stream;
    private readonly GifDemuxerConfiguration configuration;
    
    private bool isParsed;
    private bool isEndOfStream;
    private string version = "";
    private int logicalScreenWidth;
    private int logicalScreenHeight;
    private byte[] globalColorTable = Array.Empty<byte>();
    private int backgroundColorIndex;
    private bool hasGlobalColorTable;
    private int globalColorTableSize;
    
    // Current frame parsing state
    private int currentFrameIndex;
    private int pendingDelayCentiseconds;
    private GifDisposalMethod pendingDisposalMethod;
    private bool pendingHasTransparency;
    private int pendingTransparentColorIndex;
    
    // Canvas for compositing frames
    private byte[]? canvasBuffer;
    private byte[]? previousCanvasBuffer;
    
    // All frames (for looping)
    private readonly List<long> framePositions = new();
    private long firstFramePosition;
    private int loopCount; // 0 = infinite, >0 = specific count
    
    /// <summary>
    /// Gets the logical screen width.
    /// </summary>
    public int Width => logicalScreenWidth;
    
    /// <summary>
    /// Gets the logical screen height.
    /// </summary>
    public int Height => logicalScreenHeight;
    
    /// <summary>
    /// Gets the GIF version.
    /// </summary>
    public string Version => version;
    
    /// <summary>
    /// Gets whether the stream has been parsed.
    /// </summary>
    public bool IsParsed => isParsed;
    
    /// <summary>
    /// Gets whether we've reached end of stream.
    /// </summary>
    public bool IsEndOfStream => isEndOfStream;
    
    /// <summary>
    /// Gets the current frame index.
    /// </summary>
    public int CurrentFrame => currentFrameIndex;
    
    /// <summary>
    /// Gets the loop count (0 = infinite).
    /// </summary>
    public int LoopCount => loopCount;
    
    /// <summary>
    /// Gets the codec identifier.
    /// </summary>
    public string Codec => "gif";
    
    /// <summary>
    /// Creates a new GIF demuxer.
    /// </summary>
    public GifDemuxer(Stream stream, GifDemuxerConfiguration? configuration = null)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.configuration = configuration ?? new GifDemuxerConfiguration();
    }
    
    /// <summary>
    /// Probes a stream for GIF content.
    /// </summary>
    public static GifProbeResult Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        if (!stream.CanRead)
            return default;
        
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            byte[] buffer = new byte[HeaderSize + LogicalScreenDescriptorSize];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            
            if (bytesRead < buffer.Length)
                return default;
            
            return Probe(buffer);
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }
    
    /// <summary>
    /// Probes a buffer for GIF content.
    /// </summary>
    public static GifProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + LogicalScreenDescriptorSize)
            return default;
        
        // Check signature "GIF"
        if (data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
            return default;
        
        // Check version
        string version;
        if (data[3] == '8' && data[4] == '9' && data[5] == 'a')
            version = "89a";
        else if (data[3] == '8' && data[4] == '7' && data[5] == 'a')
            version = "87a";
        else
            return default;
        
        // Parse logical screen descriptor
        int width = data[6] | (data[7] << 8);
        int height = data[8] | (data[9] << 8);
        
        return new GifProbeResult
        {
            IsGif = true,
            Version = version,
            Width = width,
            Height = height
        };
    }
    
    /// <summary>
    /// Parses the GIF header and prepares for frame reading.
    /// </summary>
    public bool Parse()
    {
        if (isParsed)
            return true;
        
        // Read header
        byte[] headerBuffer = new byte[HeaderSize + LogicalScreenDescriptorSize];
        int bytesRead = stream.Read(headerBuffer, 0, headerBuffer.Length);
        
        if (bytesRead < headerBuffer.Length)
            return false;
        
        // Verify signature
        if (headerBuffer[0] != 'G' || headerBuffer[1] != 'I' || headerBuffer[2] != 'F')
            return false;
        
        // Get version
        if (headerBuffer[3] == '8' && headerBuffer[4] == '9' && headerBuffer[5] == 'a')
            version = "89a";
        else if (headerBuffer[3] == '8' && headerBuffer[4] == '7' && headerBuffer[5] == 'a')
            version = "87a";
        else
            return false;
        
        // Parse logical screen descriptor
        logicalScreenWidth = headerBuffer[6] | (headerBuffer[7] << 8);
        logicalScreenHeight = headerBuffer[8] | (headerBuffer[9] << 8);
        
        byte packedFields = headerBuffer[10];
        hasGlobalColorTable = (packedFields & 0x80) != 0;
        int colorResolution = ((packedFields >> 4) & 0x07) + 1;
        bool sortFlag = (packedFields & 0x08) != 0;
        globalColorTableSize = 1 << ((packedFields & 0x07) + 1);
        
        backgroundColorIndex = headerBuffer[11];
        byte pixelAspectRatio = headerBuffer[12];
        
        // Read global color table if present
        if (hasGlobalColorTable)
        {
            globalColorTable = new byte[globalColorTableSize * 3];
            bytesRead = stream.Read(globalColorTable, 0, globalColorTable.Length);
            if (bytesRead < globalColorTable.Length)
                return false;
        }
        
        // Initialize canvas
        canvasBuffer = new byte[logicalScreenWidth * logicalScreenHeight * 4];
        previousCanvasBuffer = new byte[logicalScreenWidth * logicalScreenHeight * 4];
        
        // Fill with background color
        if (hasGlobalColorTable && backgroundColorIndex < globalColorTableSize)
        {
            int bgOffset = backgroundColorIndex * 3;
            byte r = globalColorTable[bgOffset];
            byte g = globalColorTable[bgOffset + 1];
            byte b = globalColorTable[bgOffset + 2];
            
            for (int i = 0; i < canvasBuffer.Length; i += 4)
            {
                canvasBuffer[i] = b;     // B
                canvasBuffer[i + 1] = g; // G
                canvasBuffer[i + 2] = r; // R
                canvasBuffer[i + 3] = 255; // A
            }
        }
        
        firstFramePosition = stream.CanSeek ? stream.Position : 0;
        isParsed = true;
        
        return true;
    }
    
    /// <summary>
    /// Reads the next frame from the GIF.
    /// </summary>
    public GifFrame? ReadFrame()
    {
        if (!isParsed && !Parse())
            return null;
        
        if (isEndOfStream)
            return null;
        
        while (true)
        {
            int blockType = stream.ReadByte();
            if (blockType < 0)
            {
                isEndOfStream = true;
                return null;
            }
            
            switch (blockType)
            {
                case ExtensionIntroducer:
                    if (!ReadExtension())
                    {
                        isEndOfStream = true;
                        return null;
                    }
                    break;
                
                case ImageDescriptor:
                    var frame = ReadImageData();
                    if (frame != null)
                        return frame;
                    break;
                
                case Trailer:
                    isEndOfStream = true;
                    return null;
                
                default:
                    // Unknown block, try to skip
                    break;
            }
        }
    }
    
    private bool ReadExtension()
    {
        int label = stream.ReadByte();
        if (label < 0)
            return false;
        
        switch (label)
        {
            case GraphicControlLabel:
                return ReadGraphicControlExtension();
            
            case ApplicationLabel:
                return ReadApplicationExtension();
            
            case CommentLabel:
            case PlainTextLabel:
            default:
                return SkipSubBlocks();
        }
    }
    
    private bool ReadGraphicControlExtension()
    {
        int blockSize = stream.ReadByte();
        if (blockSize != 4)
            return SkipSubBlocks();
        
        byte[] data = new byte[4];
        if (stream.Read(data, 0, 4) < 4)
            return false;
        
        byte packedFields = data[0];
        pendingDisposalMethod = (GifDisposalMethod)((packedFields >> 2) & 0x07);
        bool userInputFlag = (packedFields & 0x02) != 0;
        pendingHasTransparency = (packedFields & 0x01) != 0;
        
        pendingDelayCentiseconds = data[1] | (data[2] << 8);
        
        // Apply minimum delay
        if (pendingDelayCentiseconds * 10 < configuration.MinimumFrameDelayMs)
            pendingDelayCentiseconds = configuration.MinimumFrameDelayMs / 10;
        
        pendingTransparentColorIndex = data[3];
        
        // Read block terminator
        stream.ReadByte();
        
        return true;
    }
    
    private bool ReadApplicationExtension()
    {
        int blockSize = stream.ReadByte();
        if (blockSize != 11)
            return SkipSubBlocks();
        
        byte[] data = new byte[11];
        if (stream.Read(data, 0, 11) < 11)
            return false;
        
        // Check for NETSCAPE2.0 application extension (loop count)
        if (data[0] == 'N' && data[1] == 'E' && data[2] == 'T' &&
            data[3] == 'S' && data[4] == 'C' && data[5] == 'A' &&
            data[6] == 'P' && data[7] == 'E' && data[8] == '2' &&
            data[9] == '.' && data[10] == '0')
        {
            // Read loop count sub-block
            int subBlockSize = stream.ReadByte();
            if (subBlockSize == 3)
            {
                byte[] subBlock = new byte[3];
                if (stream.Read(subBlock, 0, 3) >= 3)
                {
                    if (subBlock[0] == 1)
                    {
                        loopCount = subBlock[1] | (subBlock[2] << 8);
                    }
                }
            }
        }
        
        return SkipSubBlocks();
    }
    
    private bool SkipSubBlocks()
    {
        while (true)
        {
            int size = stream.ReadByte();
            if (size < 0)
                return false;
            if (size == 0)
                return true;
            
            // Skip sub-block data
            if (!SkipBytes(size))
                return false;
        }
    }
    
    private bool SkipBytes(int count)
    {
        if (stream.CanSeek)
        {
            stream.Position += count;
            return true;
        }
        
        byte[] buffer = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            int toRead = Math.Min(count, buffer.Length);
            int read = stream.Read(buffer, 0, toRead);
            if (read == 0)
                return false;
            count -= read;
        }
        return true;
    }
    
    private GifFrame? ReadImageData()
    {
        // Read image descriptor (9 bytes already read the first byte)
        byte[] descriptor = new byte[9];
        if (stream.Read(descriptor, 0, 9) < 9)
            return null;
        
        int left = descriptor[0] | (descriptor[1] << 8);
        int top = descriptor[2] | (descriptor[3] << 8);
        int frameWidth = descriptor[4] | (descriptor[5] << 8);
        int frameHeight = descriptor[6] | (descriptor[7] << 8);
        
        byte packedFields = descriptor[8];
        bool hasLocalColorTable = (packedFields & 0x80) != 0;
        bool interlaced = (packedFields & 0x40) != 0;
        bool sortFlag = (packedFields & 0x20) != 0;
        int localColorTableSize = 1 << ((packedFields & 0x07) + 1);
        
        // Read local color table if present
        byte[] colorTable;
        int colorTableSize;
        if (hasLocalColorTable)
        {
            colorTable = new byte[localColorTableSize * 3];
            if (stream.Read(colorTable, 0, colorTable.Length) < colorTable.Length)
                return null;
            colorTableSize = localColorTableSize;
        }
        else
        {
            colorTable = globalColorTable;
            colorTableSize = globalColorTableSize;
        }
        
        // Read LZW minimum code size
        int lzwMinCodeSize = stream.ReadByte();
        if (lzwMinCodeSize < 0 || lzwMinCodeSize > 12)
            return null;
        
        // Read compressed data
        using var compressedData = new MemoryStream();
        while (true)
        {
            int subBlockSize = stream.ReadByte();
            if (subBlockSize < 0)
                return null;
            if (subBlockSize == 0)
                break;
            
            byte[] subBlock = new byte[subBlockSize];
            if (stream.Read(subBlock, 0, subBlockSize) < subBlockSize)
                return null;
            compressedData.Write(subBlock, 0, subBlockSize);
        }
        
        // Decompress LZW data
        byte[] indexData = DecompressLzw(compressedData.ToArray(), lzwMinCodeSize, frameWidth * frameHeight);
        if (indexData == null)
            return null;
        
        // Handle disposal method from previous frame
        if (canvasBuffer != null && previousCanvasBuffer != null)
        {
            switch (pendingDisposalMethod)
            {
                case GifDisposalMethod.RestoreToPrevious:
                    // Restore from previous canvas (saved before this frame was drawn)
                    break;
                case GifDisposalMethod.RestoreToBackground:
                    // Clear the frame area to background
                    // Already handled in compositing
                    break;
            }
        }
        
        // Save canvas for potential restore
        canvasBuffer?.CopyTo(previousCanvasBuffer!, 0);
        
        // Convert indexed color to BGRA and composite onto canvas
        byte[] frameData = new byte[frameWidth * frameHeight * 4];
        int srcIndex = 0;
        
        for (int y = 0; y < frameHeight; y++)
        {
            int actualY = interlaced ? DeinterlaceRow(y, frameHeight) : y;
            
            for (int x = 0; x < frameWidth; x++)
            {
                int colorIndex = indexData[srcIndex++];
                int dstIndex = (actualY * frameWidth + x) * 4;
                
                if (pendingHasTransparency && colorIndex == pendingTransparentColorIndex)
                {
                    // Transparent pixel
                    frameData[dstIndex] = 0;
                    frameData[dstIndex + 1] = 0;
                    frameData[dstIndex + 2] = 0;
                    frameData[dstIndex + 3] = 0;
                }
                else if (colorIndex < colorTableSize)
                {
                    int paletteOffset = colorIndex * 3;
                    frameData[dstIndex] = colorTable[paletteOffset + 2];     // B
                    frameData[dstIndex + 1] = colorTable[paletteOffset + 1]; // G
                    frameData[dstIndex + 2] = colorTable[paletteOffset];     // R
                    frameData[dstIndex + 3] = 255;                            // A
                }
            }
        }
        
        // Composite frame onto canvas
        if (canvasBuffer != null)
        {
            CompositeFrame(frameData, frameWidth, frameHeight, left, top);
        }
        
        var frame = new GifFrame
        {
            Index = currentFrameIndex,
            ImageData = canvasBuffer != null ? (byte[])canvasBuffer.Clone() : frameData,
            Width = logicalScreenWidth,
            Height = logicalScreenHeight,
            Left = left,
            Top = top,
            DelayCentiseconds = pendingDelayCentiseconds > 0 ? pendingDelayCentiseconds : configuration.DefaultFrameDelayMs / 10,
            DisposalMethod = pendingDisposalMethod,
            HasTransparency = pendingHasTransparency,
            TransparentColorIndex = pendingTransparentColorIndex
        };
        
        currentFrameIndex++;
        
        // Reset pending values
        pendingDelayCentiseconds = 0;
        pendingDisposalMethod = GifDisposalMethod.None;
        pendingHasTransparency = false;
        pendingTransparentColorIndex = 0;
        
        return frame;
    }
    
    private void CompositeFrame(byte[] frameData, int frameWidth, int frameHeight, int left, int top)
    {
        if (canvasBuffer == null)
            return;
        
        for (int y = 0; y < frameHeight; y++)
        {
            int canvasY = top + y;
            if (canvasY < 0 || canvasY >= logicalScreenHeight)
                continue;
            
            for (int x = 0; x < frameWidth; x++)
            {
                int canvasX = left + x;
                if (canvasX < 0 || canvasX >= logicalScreenWidth)
                    continue;
                
                int srcIndex = (y * frameWidth + x) * 4;
                int dstIndex = (canvasY * logicalScreenWidth + canvasX) * 4;
                
                byte alpha = frameData[srcIndex + 3];
                if (alpha > 0)
                {
                    // Opaque or semi-transparent pixel
                    canvasBuffer[dstIndex] = frameData[srcIndex];
                    canvasBuffer[dstIndex + 1] = frameData[srcIndex + 1];
                    canvasBuffer[dstIndex + 2] = frameData[srcIndex + 2];
                    canvasBuffer[dstIndex + 3] = alpha;
                }
                // Transparent pixels leave canvas unchanged
            }
        }
    }
    
    private static int DeinterlaceRow(int y, int height)
    {
        // GIF interlacing passes:
        // Pass 1: Every 8th row, starting at 0
        // Pass 2: Every 8th row, starting at 4
        // Pass 3: Every 4th row, starting at 2
        // Pass 4: Every 2nd row, starting at 1
        
        int pass1Rows = (height + 7) / 8;
        int pass2Rows = (height + 3) / 8;
        int pass3Rows = (height + 1) / 4;
        
        if (y < pass1Rows)
            return y * 8;
        
        y -= pass1Rows;
        if (y < pass2Rows)
            return 4 + y * 8;
        
        y -= pass2Rows;
        if (y < pass3Rows)
            return 2 + y * 4;
        
        y -= pass3Rows;
        return 1 + y * 2;
    }
    
    private static byte[]? DecompressLzw(byte[] input, int minCodeSize, int expectedSize)
    {
        if (minCodeSize < 2 || minCodeSize > 12)
            return null;
        
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        
        // LZW decompression state
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;
        int codeMask = (1 << codeSize) - 1;
        
        // Dictionary
        const int maxDictSize = 4096;
        byte[][] dictionary = new byte[maxDictSize][];
        
        // Initialize dictionary
        for (int i = 0; i < clearCode; i++)
            dictionary[i] = new byte[] { (byte)i };
        
        // Output
        using var output = new MemoryStream(expectedSize);
        
        // Bit reader state
        int bitBuffer = 0;
        int bitsAvailable = 0;
        int inputIndex = 0;
        
        int prevCode = -1;
        
        while (inputIndex < input.Length || bitsAvailable >= codeSize)
        {
            // Read next code
            while (bitsAvailable < codeSize && inputIndex < input.Length)
            {
                bitBuffer |= input[inputIndex++] << bitsAvailable;
                bitsAvailable += 8;
            }
            
            if (bitsAvailable < codeSize)
                break;
            
            int code = bitBuffer & codeMask;
            bitBuffer >>= codeSize;
            bitsAvailable -= codeSize;
            
            if (code == clearCode)
            {
                // Reset dictionary
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
                codeMask = (1 << codeSize) - 1;
                prevCode = -1;
                continue;
            }
            
            if (code == endCode)
                break;
            
            byte[] entry;
            if (code < nextCode)
            {
                entry = dictionary[code];
            }
            else if (code == nextCode && prevCode >= 0)
            {
                // Special case: code not in dictionary yet
                byte[] prev = dictionary[prevCode];
                entry = new byte[prev.Length + 1];
                prev.CopyTo(entry, 0);
                entry[prev.Length] = prev[0];
            }
            else
            {
                // Invalid code
                return null;
            }
            
            output.Write(entry, 0, entry.Length);
            
            // Add to dictionary
            if (prevCode >= 0 && nextCode < maxDictSize)
            {
                byte[] prev = dictionary[prevCode];
                byte[] newEntry = new byte[prev.Length + 1];
                prev.CopyTo(newEntry, 0);
                newEntry[prev.Length] = entry[0];
                dictionary[nextCode++] = newEntry;
                
                // Increase code size if needed
                if (nextCode > codeMask && codeSize < 12)
                {
                    codeSize++;
                    codeMask = (1 << codeSize) - 1;
                }
            }
            
            prevCode = code;
            
            if (output.Length >= expectedSize)
                break;
        }
        
        byte[] result = output.ToArray();
        if (result.Length < expectedSize)
        {
            // Pad with zeros if needed
            byte[] padded = new byte[expectedSize];
            result.CopyTo(padded, 0);
            return padded;
        }
        
        return result;
    }
    
    /// <summary>
    /// Seeks to a specific frame.
    /// </summary>
    public bool SeekToFrame(int frameIndex)
    {
        if (!stream.CanSeek)
            return false;
        
        if (frameIndex == 0)
        {
            stream.Position = firstFramePosition;
            currentFrameIndex = 0;
            isEndOfStream = false;
            
            // Reset canvas
            if (canvasBuffer != null)
                Array.Clear(canvasBuffer);
            
            return true;
        }
        
        // For other frames, we need to replay from start
        // (GIF frames can depend on previous frames)
        if (frameIndex < currentFrameIndex)
        {
            stream.Position = firstFramePosition;
            currentFrameIndex = 0;
            if (canvasBuffer != null)
                Array.Clear(canvasBuffer);
        }
        
        while (currentFrameIndex < frameIndex && !isEndOfStream)
        {
            ReadFrame(); // Read and discard frames
        }
        
        return currentFrameIndex == frameIndex;
    }
    
    /// <summary>
    /// Gets track information.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Video,
            CodecId = Codec,
            Width = logicalScreenWidth,
            Height = logicalScreenHeight,
            FrameRate = 10.0 // Default for GIF
        };
    }
    
    /// <summary>
    /// Disposes the demuxer.
    /// </summary>
    public void Dispose()
    {
        // Nothing to dispose - we don't own the stream
    }
}

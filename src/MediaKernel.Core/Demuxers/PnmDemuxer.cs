// MediaKernel - PNM Image Demuxer
// Demuxes PNM (Portable Anymap) still images.
// Reference: Netpbm format specification
//
// Supports:
// - PBM (Portable Bitmap): P1 (ASCII), P4 (binary)
// - PGM (Portable Graymap): P2 (ASCII), P5 (binary)
// - PPM (Portable Pixmap): P3 (ASCII), P6 (binary)
// - PAM (Portable Arbitrary Map): P7

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// PNM format type (magic number).
/// </summary>
public enum PnmFormatType
{
    /// <summary>Unknown format.</summary>
    Unknown = 0,
    
    /// <summary>PBM ASCII (bitmap).</summary>
    PbmAscii = 1,
    
    /// <summary>PGM ASCII (graymap).</summary>
    PgmAscii = 2,
    
    /// <summary>PPM ASCII (pixmap).</summary>
    PpmAscii = 3,
    
    /// <summary>PBM binary (bitmap).</summary>
    PbmBinary = 4,
    
    /// <summary>PGM binary (graymap).</summary>
    PgmBinary = 5,
    
    /// <summary>PPM binary (pixmap).</summary>
    PpmBinary = 6,
    
    /// <summary>PAM (Portable Arbitrary Map).</summary>
    Pam = 7
}

/// <summary>
/// PNM probe result.
/// </summary>
public readonly struct PnmProbeResult
{
    public bool IsPnm { get; init; }
    public int Confidence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MaxValue { get; init; }
    public PnmFormatType FormatType { get; init; }
    public bool IsBinary { get; init; }
    public bool IsColor { get; init; }
    public int ChannelCount { get; init; }
    public int BitsPerSample { get; init; }
}

/// <summary>
/// PNM image frame.
/// </summary>
public sealed class PnmFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int MaxValue { get; init; }
    public PnmFormatType FormatType { get; init; }
    public bool IsBinary { get; init; }
    public bool IsColor { get; init; }
    public int ChannelCount { get; init; }
    public int BitsPerSample { get; init; }
    public byte[] RawData { get; init; } = [];
    public int HeaderSize { get; init; }
}

/// <summary>
/// Demuxes PNM (Portable Anymap) still images (PBM, PGM, PPM).
/// </summary>
public sealed class PnmDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long startPosition;
    private bool disposed;
    
    private byte[]? fileData;
    private int headerSize;
    private bool frameRead;
    
    public PnmDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        startPosition = stream.CanSeek ? stream.Position : 0;
    }
    
    public bool IsParsed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int MaxValue { get; private set; } = 1;
    public PnmFormatType FormatType { get; private set; }
    public bool IsBinary => FormatType >= PnmFormatType.PbmBinary;
    public bool IsColor => FormatType is PnmFormatType.PpmAscii or PnmFormatType.PpmBinary;
    public int ChannelCount => IsColor ? 3 : 1;
    public int BitsPerSample => MaxValue > 255 ? 16 : MaxValue > 1 ? 8 : 1;
    public bool IsEndOfStream => frameRead;
    
    /// <summary>
    /// Probes a stream to determine if it contains a valid PNM file.
    /// </summary>
    public static PnmProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[256];
        int read = stream.Read(header);
        
        if (read < 3)
            return new PnmProbeResult { IsPnm = false };
        
        return ProbeInternal(header[..read]);
    }
    
    /// <summary>
    /// Probes data to determine if it contains a valid PNM file.
    /// </summary>
    public static PnmProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return new PnmProbeResult { IsPnm = false };
        
        return ProbeInternal(data);
    }
    
    private static PnmProbeResult ProbeInternal(ReadOnlySpan<byte> data)
    {
        // Check magic number: P followed by 1-7 followed by whitespace
        if (data[0] != 'P')
            return new PnmProbeResult { IsPnm = false };
        
        if (data[1] < '1' || data[1] > '7')
            return new PnmProbeResult { IsPnm = false };
        
        if (!IsWhitespace(data[2]))
            return new PnmProbeResult { IsPnm = false };
        
        var formatType = (PnmFormatType)(data[1] - '0');
        
        // Parse width and height from header
        int pos = 3;
        int width = 0, height = 0, maxValue = 1;
        
        // Skip initial whitespace and comments
        pos = SkipWhitespaceAndComments(data, pos);
        
        // Parse width
        (width, pos) = ParseNumber(data, pos);
        if (width <= 0) return new PnmProbeResult { IsPnm = false };
        
        pos = SkipWhitespaceAndComments(data, pos);
        
        // Parse height
        (height, pos) = ParseNumber(data, pos);
        if (height <= 0) return new PnmProbeResult { IsPnm = false };
        
        // Parse max value (not for PBM)
        if (formatType != PnmFormatType.PbmAscii && formatType != PnmFormatType.PbmBinary)
        {
            pos = SkipWhitespaceAndComments(data, pos);
            (maxValue, pos) = ParseNumber(data, pos);
            if (maxValue <= 0) maxValue = 255;
        }
        
        bool isBinary = formatType >= PnmFormatType.PbmBinary && formatType <= PnmFormatType.PpmBinary;
        bool isColor = formatType is PnmFormatType.PpmAscii or PnmFormatType.PpmBinary;
        int channelCount = isColor ? 3 : 1;
        int bitsPerSample = maxValue > 255 ? 16 : maxValue > 1 ? 8 : 1;
        
        return new PnmProbeResult
        {
            IsPnm = true,
            Confidence = 95,
            Width = width,
            Height = height,
            MaxValue = maxValue,
            FormatType = formatType,
            IsBinary = isBinary,
            IsColor = isColor,
            ChannelCount = channelCount,
            BitsPerSample = bitsPerSample
        };
    }
    
    private static bool IsWhitespace(byte b) => b == ' ' || b == '\t' || b == '\r' || b == '\n';
    
    private static int SkipWhitespaceAndComments(ReadOnlySpan<byte> data, int pos)
    {
        while (pos < data.Length)
        {
            if (data[pos] == '#')
            {
                // Skip comment until end of line
                while (pos < data.Length && data[pos] != '\n')
                    pos++;
                if (pos < data.Length) pos++; // Skip the newline
            }
            else if (IsWhitespace(data[pos]))
            {
                pos++;
            }
            else
            {
                break;
            }
        }
        return pos;
    }
    
    private static (int value, int newPos) ParseNumber(ReadOnlySpan<byte> data, int pos)
    {
        int value = 0;
        bool foundDigit = false;
        
        while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9')
        {
            value = value * 10 + (data[pos] - '0');
            foundDigit = true;
            pos++;
        }
        
        return foundDigit ? (value, pos) : (0, pos);
    }
    
    /// <summary>
    /// Parses the PNM structure.
    /// </summary>
    public bool Parse()
    {
        if (IsParsed) return true;
        
        stream.Position = startPosition;
        
        // Read entire file
        fileData = new byte[stream.Length - startPosition];
        int read = stream.Read(fileData);
        
        if (read < 3)
            return false;
        
        var probeResult = Probe(fileData);
        if (!probeResult.IsPnm)
            return false;
        
        FormatType = probeResult.FormatType;
        Width = probeResult.Width;
        Height = probeResult.Height;
        MaxValue = probeResult.MaxValue;
        
        // Calculate header size (find where pixel data starts)
        headerSize = CalculateHeaderSize(fileData);
        
        IsParsed = true;
        return true;
    }
    
    private int CalculateHeaderSize(ReadOnlySpan<byte> data)
    {
        int pos = 3; // After "Px "
        
        // Skip whitespace/comments + width
        pos = SkipWhitespaceAndComments(data, pos);
        while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9') pos++;
        
        // Skip whitespace/comments + height
        pos = SkipWhitespaceAndComments(data, pos);
        while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9') pos++;
        
        // For non-PBM, skip whitespace/comments + maxval
        if (FormatType != PnmFormatType.PbmAscii && FormatType != PnmFormatType.PbmBinary)
        {
            pos = SkipWhitespaceAndComments(data, pos);
            while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9') pos++;
        }
        
        // For binary formats, skip exactly one whitespace character
        if (IsBinary && pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }
        else
        {
            // For ASCII, skip all whitespace
            pos = SkipWhitespaceAndComments(data, pos);
        }
        
        return pos;
    }
    
    /// <summary>
    /// Reads the image as a frame.
    /// </summary>
    public PnmFrame? ReadFrame()
    {
        if (!IsParsed || fileData == null) return null;
        if (frameRead) return null;
        
        frameRead = true;
        
        return new PnmFrame
        {
            Width = Width,
            Height = Height,
            MaxValue = MaxValue,
            FormatType = FormatType,
            IsBinary = IsBinary,
            IsColor = IsColor,
            ChannelCount = ChannelCount,
            BitsPerSample = BitsPerSample,
            RawData = fileData,
            HeaderSize = headerSize
        };
    }
    
    /// <summary>
    /// Resets to allow reading the frame again.
    /// </summary>
    public void Reset()
    {
        frameRead = false;
    }
    
    /// <summary>
    /// Gets track information.
    /// </summary>
    public DemuxerTrackInfo? GetTrackInfo()
    {
        if (!IsParsed) return null;
        
        string codecId = FormatType switch
        {
            PnmFormatType.PbmAscii or PnmFormatType.PbmBinary => "pbm",
            PnmFormatType.PgmAscii or PnmFormatType.PgmBinary => "pgm",
            PnmFormatType.PpmAscii or PnmFormatType.PpmBinary => "ppm",
            PnmFormatType.Pam => "pam",
            _ => "pnm"
        };
        
        return new DemuxerTrackInfo
        {
            TrackType = DemuxerTrackType.Video,
            TrackId = 0,
            CodecId = codecId,
            Width = Width,
            Height = Height,
            BitsPerSample = BitsPerSample,
            ChannelCount = ChannelCount
        };
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        
        fileData = null;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

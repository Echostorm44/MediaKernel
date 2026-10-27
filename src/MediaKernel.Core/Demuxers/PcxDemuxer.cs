// MediaKernel - PCX Image Demuxer
// Demuxes PCX (ZSoft Paintbrush) still images.
// Reference: ZSoft PCX File Format Technical Reference Manual
//
// Supports:
// - Versions 0, 2, 3, and 5
// - Uncompressed and RLE-compressed data
// - Bit depths: 1, 2, 4, 8 bits per plane
// - Plane counts: 1, 2, 3, 4 (RGB, RGBA)
// - VGA palette (256-color mode)

using System.Buffers.Binary;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// PCX version numbers.
/// </summary>
public enum PcxVersion : byte
{
    /// <summary>Version 2.5 with fixed EGA palette.</summary>
    Version25 = 0,
    
    /// <summary>Version 2.8 with modifiable palette.</summary>
    Version28Palette = 2,
    
    /// <summary>Version 2.8 without palette.</summary>
    Version28NoPalette = 3,
    
    /// <summary>Version 3.0 (Windows).</summary>
    Version30 = 5
}

/// <summary>
/// PCX encoding method.
/// </summary>
public enum PcxEncoding : byte
{
    /// <summary>Uncompressed data.</summary>
    Uncompressed = 0,
    
    /// <summary>RLE (Run-Length Encoding) compressed.</summary>
    Rle = 1
}

/// <summary>
/// PCX probe result.
/// </summary>
public readonly struct PcxProbeResult
{
    public bool IsPcx { get; init; }
    public int Confidence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerPixel { get; init; }
    public int PlaneCount { get; init; }
    public int TotalBitsPerPixel { get; init; }
    public PcxVersion Version { get; init; }
    public PcxEncoding Encoding { get; init; }
    public bool IsRleCompressed { get; init; }
}

/// <summary>
/// PCX image frame.
/// </summary>
public sealed class PcxFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerPixel { get; init; }
    public int PlaneCount { get; init; }
    public int TotalBitsPerPixel { get; init; }
    public PcxVersion Version { get; init; }
    public PcxEncoding Encoding { get; init; }
    public int BytesPerLine { get; init; }
    public int HorizontalDpi { get; init; }
    public int VerticalDpi { get; init; }
    public bool HasVgaPalette { get; init; }
    public byte[] RawData { get; init; } = [];
}

/// <summary>
/// Demuxes PCX (ZSoft Paintbrush) still images.
/// </summary>
public sealed class PcxDemuxer : IDisposable
{
    private const byte PcxMarker = 0x0A;
    private const byte VgaPaletteMarker = 0x0C;
    private const int HeaderSize = 128;
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long startPosition;
    private bool disposed;
    
    private byte[]? fileData;
    private bool frameRead;
    
    public PcxDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        startPosition = stream.CanSeek ? stream.Position : 0;
    }
    
    public bool IsParsed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BitsPerPixel { get; private set; }
    public int PlaneCount { get; private set; }
    public int TotalBitsPerPixel => BitsPerPixel * PlaneCount;
    public PcxVersion Version { get; private set; }
    public PcxEncoding Encoding { get; private set; }
    public int BytesPerLine { get; private set; }
    public int HorizontalDpi { get; private set; }
    public int VerticalDpi { get; private set; }
    public bool HasVgaPalette { get; private set; }
    public int XMin { get; private set; }
    public int YMin { get; private set; }
    public int XMax { get; private set; }
    public int YMax { get; private set; }
    public bool IsRleCompressed => Encoding == PcxEncoding.Rle;
    public bool IsEndOfStream => frameRead;
    public int ChannelCount => PlaneCount >= 3 ? PlaneCount : 1;
    
    /// <summary>
    /// Probes a stream to determine if it contains a valid PCX file.
    /// </summary>
    public static PcxProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        int read = stream.Read(header);
        
        if (read < 66)
            return new PcxProbeResult { IsPcx = false };
        
        return ProbeInternal(header[..read]);
    }
    
    /// <summary>
    /// Probes data to determine if it contains a valid PCX file.
    /// </summary>
    public static PcxProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 66)
            return new PcxProbeResult { IsPcx = false };
        
        return ProbeInternal(data);
    }
    
    private static PcxProbeResult ProbeInternal(ReadOnlySpan<byte> header)
    {
        // Byte 0: marker (must be 0x0A)
        if (header[0] != PcxMarker)
            return new PcxProbeResult { IsPcx = false };
        
        // Byte 1: version (0, 2, 3, or 5)
        byte version = header[1];
        if (version != 0 && version != 2 && version != 3 && version != 5)
            return new PcxProbeResult { IsPcx = false };
        
        // Byte 2: encoding (0 = uncompressed, 1 = RLE)
        byte encoding = header[2];
        if (encoding > 1)
            return new PcxProbeResult { IsPcx = false };
        
        // Byte 3: bits per pixel per plane (1, 2, 4, or 8)
        byte bitsPerPixel = header[3];
        if (bitsPerPixel != 1 && bitsPerPixel != 2 && bitsPerPixel != 4 && bitsPerPixel != 8)
            return new PcxProbeResult { IsPcx = false };
        
        // Byte 64: reserved (must be 0)
        if (header[64] != 0)
            return new PcxProbeResult { IsPcx = false };
        
        // Byte 65: number of planes (1-4)
        byte planeCount = header[65];
        if (planeCount == 0 || planeCount > 4)
            return new PcxProbeResult { IsPcx = false };
        
        // Image window
        int xmin = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        int ymin = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        int xmax = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        int ymax = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        
        // xmin should be <= xmax, ymin should be <= ymax
        if (xmin > xmax || ymin > ymax)
            return new PcxProbeResult { IsPcx = false };
        
        int width = xmax - xmin + 1;
        int height = ymax - ymin + 1;
        
        return new PcxProbeResult
        {
            IsPcx = true,
            Confidence = 90,
            Width = width,
            Height = height,
            BitsPerPixel = bitsPerPixel,
            PlaneCount = planeCount,
            TotalBitsPerPixel = bitsPerPixel * planeCount,
            Version = (PcxVersion)version,
            Encoding = (PcxEncoding)encoding,
            IsRleCompressed = encoding == 1
        };
    }
    
    /// <summary>
    /// Parses the PCX structure.
    /// </summary>
    public bool Parse()
    {
        if (IsParsed) return true;
        
        stream.Position = startPosition;
        
        // Read entire file
        fileData = new byte[stream.Length - startPosition];
        int read = stream.Read(fileData);
        
        if (read < HeaderSize)
            return false;
        
        var probeResult = Probe(fileData);
        if (!probeResult.IsPcx)
            return false;
        
        // Parse header
        Version = (PcxVersion)fileData[1];
        Encoding = (PcxEncoding)fileData[2];
        BitsPerPixel = fileData[3];
        
        XMin = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(4));
        YMin = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(6));
        XMax = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(8));
        YMax = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(10));
        
        Width = XMax - XMin + 1;
        Height = YMax - YMin + 1;
        
        HorizontalDpi = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(12));
        VerticalDpi = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(14));
        
        PlaneCount = fileData[65];
        BytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(66));
        
        // Check for VGA palette (256-color mode)
        // VGA palette is at end of file, preceded by 0x0C marker
        if (BitsPerPixel == 8 && PlaneCount == 1 && fileData.Length > 769)
        {
            int paletteOffset = fileData.Length - 769;
            if (fileData[paletteOffset] == VgaPaletteMarker)
            {
                HasVgaPalette = true;
            }
        }
        
        IsParsed = true;
        return true;
    }
    
    /// <summary>
    /// Reads the image as a frame.
    /// </summary>
    public PcxFrame? ReadFrame()
    {
        if (!IsParsed || fileData == null) return null;
        if (frameRead) return null;
        
        frameRead = true;
        
        return new PcxFrame
        {
            Width = Width,
            Height = Height,
            BitsPerPixel = BitsPerPixel,
            PlaneCount = PlaneCount,
            TotalBitsPerPixel = TotalBitsPerPixel,
            Version = Version,
            Encoding = Encoding,
            BytesPerLine = BytesPerLine,
            HorizontalDpi = HorizontalDpi,
            VerticalDpi = VerticalDpi,
            HasVgaPalette = HasVgaPalette,
            RawData = fileData
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
        
        return new DemuxerTrackInfo
        {
            TrackType = DemuxerTrackType.Video,
            TrackId = 0,
            CodecId = "pcx",
            Width = Width,
            Height = Height,
            BitsPerSample = TotalBitsPerPixel,
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

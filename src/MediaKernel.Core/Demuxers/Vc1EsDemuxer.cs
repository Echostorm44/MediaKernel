// VC-1 raw elementary stream demuxer
// Demuxes raw VC-1 bitstreams (Advanced profile with start codes)
// Reference: SMPTE 421M, VLC's modules/demux/mpeg/h26x.c

using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for VC-1 elementary stream demuxer.
/// </summary>
public sealed class Vc1EsConfiguration
{
    /// <summary>
    /// Frame rate to use when not available from sequence header.
    /// Default is 25.0 fps.
    /// </summary>
    public double DefaultFrameRate { get; init; } = 25.0;
}

/// <summary>
/// Result from probing a stream for VC-1 content.
/// </summary>
public readonly struct Vc1EsProbeResult
{
    /// <summary>Whether valid VC-1 Advanced profile content was detected.</summary>
    public bool IsVc1 { get; init; }
    
    /// <summary>Whether a sequence header was found.</summary>
    public bool HasSequenceHeader { get; init; }
    
    /// <summary>Whether an entry point was found.</summary>
    public bool HasEntryPoint { get; init; }
    
    /// <summary>Video width from sequence header (0 if not available).</summary>
    public int Width { get; init; }
    
    /// <summary>Video height from sequence header (0 if not available).</summary>
    public int Height { get; init; }
    
    /// <summary>Detected profile.</summary>
    public Vc1Profile Profile { get; init; }
}

/// <summary>
/// Demuxer for raw VC-1 elementary streams (Advanced profile with Annex-B style start codes).
/// </summary>
/// <remarks>
/// VC-1 Advanced profile uses start codes similar to MPEG (0x000001xx).
/// Only Advanced profile streams have start codes; Simple/Main profiles rely on
/// container framing (e.g., ASF). This demuxer handles Advanced profile raw streams.
/// </remarks>
public sealed class Vc1EsDemuxer : IDisposable
{
    private const int ReadChunkSize = 4096;
    private const int MaxProbeSize = 32768;
    
    private readonly Stream _stream;
    private readonly Vc1EsConfiguration _config;
    private readonly Vc1Packetizer _packetizer;
    private readonly byte[] _readBuffer;
    
    private long _currentDts;
    private long _frameDurationTicks;
    private int _width;
    private int _height;
    private double _frameRate;
    private bool _disposed;
    private bool _endOfStream;
    
    /// <summary>Video width in pixels (available after Parse).</summary>
    public int Width => _width;
    
    /// <summary>Video height in pixels (available after Parse).</summary>
    public int Height => _height;
    
    /// <summary>Detected frame rate (may be from sequence header or default).</summary>
    public double FrameRate => _frameRate;
    
    /// <summary>Codec identifier.</summary>
    public string Codec => "vc1";
    
    /// <summary>Creates a new VC-1 ES demuxer.</summary>
    /// <param name="stream">Source stream containing raw VC-1 data.</param>
    /// <param name="config">Optional configuration.</param>
    public Vc1EsDemuxer(Stream stream, Vc1EsConfiguration? config = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _config = config ?? new Vc1EsConfiguration();
        _packetizer = new Vc1Packetizer();
        _readBuffer = new byte[ReadChunkSize];
        
        _frameRate = _config.DefaultFrameRate;
        _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _frameRate);
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid VC-1 elementary stream data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static Vc1EsProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new Vc1EsProbeResult { IsVc1 = false };
        
        long startPosition = stream.Position;
        
        try
        {
            int probeSize = (int)Math.Min(MaxProbeSize, stream.Length - stream.Position);
            if (probeSize < 8)
                return new Vc1EsProbeResult { IsVc1 = false };
            
            byte[] data = new byte[probeSize];
            int bytesRead = stream.Read(data, 0, probeSize);
            if (bytesRead < 8)
                return new Vc1EsProbeResult { IsVc1 = false };
            
            return ProbeData(data.AsSpan(0, bytesRead));
        }
        finally
        {
            stream.Position = startPosition;
        }
    }
    
    /// <summary>
    /// Probes raw data for VC-1 content.
    /// </summary>
    public static Vc1EsProbeResult ProbeData(ReadOnlySpan<byte> data)
    {
        bool foundStartCode = false;
        bool hasSequenceHeader = false;
        bool hasEntryPoint = false;
        int width = 0;
        int height = 0;
        int iduCount = 0;
        const int maxIduCheck = 8;
        
        int offset = 0;
        
        while (offset < data.Length - 4 && iduCount < maxIduCheck)
        {
            // Look for start code (VC-1 uses 0x00 0x00 0x01 xx)
            if (data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
            {
                foundStartCode = true;
                byte iduType = data[offset + 3];
                
                iduCount++;
                
                // Valid VC-1 IDU types
                switch (iduType)
                {
                    case 0x0F: // Sequence header
                        hasSequenceHeader = true;
                        (width, height) = TryParseSequenceHeader(data.Slice(offset));
                        break;
                    
                    case 0x0E: // Entry point
                        hasEntryPoint = true;
                        break;
                    
                    case 0x0D: // Frame
                    case 0x0C: // Field
                    case 0x0B: // Slice
                    case 0x0A: // End of sequence
                        // Valid frame-related IDUs
                        break;
                    
                    case 0x1B: // Slice user data
                    case 0x1C: // Field user data
                    case 0x1D: // Frame user data
                    case 0x1E: // Entry point user data
                    case 0x1F: // Sequence level user data
                        // Valid user data IDUs
                        break;
                    
                    default:
                        // Unknown type - might not be VC-1
                        if (iduType > 0x1F)
                        {
                            offset++;
                            continue;
                        }
                        break;
                }
                
                offset += 4;
            }
            else
            {
                offset++;
            }
        }
        
        // Valid VC-1 ES should have start codes and ideally a sequence header
        bool isVc1 = foundStartCode && iduCount > 0 && (hasSequenceHeader || iduCount >= 2);
        
        return new Vc1EsProbeResult
        {
            IsVc1 = isVc1,
            HasSequenceHeader = hasSequenceHeader,
            HasEntryPoint = hasEntryPoint,
            Width = width,
            Height = height,
            Profile = isVc1 ? Vc1Profile.Advanced : Vc1Profile.Simple
        };
    }
    
    /// <summary>
    /// Parses the stream header to extract format information.
    /// </summary>
    /// <returns>True if parsing succeeded.</returns>
    public bool Parse()
    {
        if (_disposed)
            return false;
        
        var probe = Probe(_stream);
        
        if (!probe.IsVc1)
            return false;
        
        _width = probe.Width;
        _height = probe.Height;
        
        return true;
    }
    
    /// <summary>
    /// Reads the next video frame from the stream.
    /// </summary>
    /// <param name="packet">Output packet containing frame data.</param>
    /// <returns>True if a frame was read, false at end of stream.</returns>
    public bool ReadPacket(out Vc1EsPacket packet)
    {
        packet = default;
        
        if (_disposed)
            return false;
        
        // Try to get a complete frame from the packetizer
        Vc1Frame frame;
        while (!_packetizer.TryGetFrame(out frame))
        {
            if (_endOfStream)
            {
                _packetizer.Flush();
                
                if (_packetizer.TryGetFrame(out frame))
                {
                    packet = CreatePacket(frame);
                    return true;
                }
                
                return false;
            }
            
            // Read more data from stream
            int bytesRead = _stream.Read(_readBuffer, 0, ReadChunkSize);
            
            if (bytesRead == 0)
            {
                _endOfStream = true;
                continue;
            }
            
            // Feed data to packetizer
            _packetizer.Feed(_readBuffer.AsSpan(0, bytesRead), -1, -1);
        }
        
        // Update dimensions from sequence header if available
        if (_packetizer.HasSequenceHeader && _packetizer.SequenceHeader.HasValue)
        {
            var seqHeader = _packetizer.SequenceHeader.Value;
            if (seqHeader.Width > 0 && seqHeader.Height > 0)
            {
                _width = seqHeader.Width;
                _height = seqHeader.Height;
            }
            
            if (seqHeader.FrameRate > 0)
            {
                _frameRate = seqHeader.FrameRate;
                _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _frameRate);
            }
        }
        
        packet = CreatePacket(frame);
        return true;
    }
    
    /// <summary>
    /// Seeks to a position in the stream.
    /// </summary>
    public bool SeekToPosition(long position)
    {
        if (_disposed || !_stream.CanSeek)
            return false;
        
        _stream.Position = Math.Clamp(position, 0, _stream.Length);
        _packetizer.Reset();
        _endOfStream = false;
        
        return ScanForStartCode();
    }
    
    /// <summary>
    /// Seeks to approximate time position.
    /// </summary>
    public bool SeekToTime(TimeSpan time)
    {
        if (_disposed || !_stream.CanSeek || _stream.Length == 0)
            return false;
        
        // Estimate position based on linear distribution
        double fraction = time.TotalSeconds * _frameRate / EstimateTotalFrames();
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        
        long position = (long)(_stream.Length * fraction);
        
        _currentDts = time.Ticks;
        
        return SeekToPosition(position);
    }
    
    /// <summary>
    /// Resets the demuxer state.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
            return;
        
        if (_stream.CanSeek)
            _stream.Position = 0;
        
        _packetizer.Reset();
        _currentDts = 0;
        _endOfStream = false;
    }
    
    private Vc1EsPacket CreatePacket(Vc1Frame frame)
    {
        var packet = new Vc1EsPacket
        {
            Data = frame.Data.ToArray(),
            Pts = frame.PresentationTimestamp >= 0 ? frame.PresentationTimestamp : _currentDts,
            Dts = frame.DecodeTimestamp >= 0 ? frame.DecodeTimestamp : _currentDts,
            IsKeyframe = frame.IsKeyframe,
            FrameType = frame.FrameType
        };
        
        _currentDts += _frameDurationTicks;
        
        return packet;
    }
    
    private bool ScanForStartCode()
    {
        byte[] buffer = new byte[256];
        
        while (true)
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            if (bytesRead < 4)
                return false;
            
            for (int i = 0; i < bytesRead - 3; i++)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 1)
                {
                    byte iduType = buffer[i + 3];
                    
                    // Check for valid VC-1 IDU types
                    if (iduType >= 0x0A && iduType <= 0x1F)
                    {
                        _stream.Position -= (bytesRead - i);
                        return true;
                    }
                }
            }
            
            if (_stream.CanSeek)
                _stream.Position -= 3;
            else
                break;
        }
        
        return false;
    }
    
    private double EstimateTotalFrames()
    {
        // Rough estimate for VC-1 (typically ~4 Mbit/s for HD content)
        const double assumedBitrateKbps = 4000;
        double estimatedDuration = (_stream.Length * 8.0) / (assumedBitrateKbps * 1000);
        return estimatedDuration * _frameRate;
    }
    
    private static (int width, int height) TryParseSequenceHeader(ReadOnlySpan<byte> data)
    {
        // VC-1 sequence header parsing (Advanced profile)
        // Start code is 0x00 0x00 0x01 0x0F
        
        if (data.Length < 10)
            return (0, 0);
        
        // Skip start code (4 bytes)
        int bitOffset = 32;
        
        // profile (2 bits) - must be 3 for Advanced
        int profile = ReadBits(data, ref bitOffset, 2);
        if (profile != 3)
            return (0, 0); // Not Advanced profile
        
        // level (3 bits)
        ReadBits(data, ref bitOffset, 3);
        
        // chromat_format (2 bits), frmrtq_postproc (3 bits), bitrtq_postproc (5 bits), postprocflag (1 bit)
        ReadBits(data, ref bitOffset, 11);
        
        // coded_width (12 bits) -> actual = (value + 1) * 2
        int codedWidth = ReadBits(data, ref bitOffset, 12);
        int width = (codedWidth + 1) * 2;
        
        // coded_height (12 bits) -> actual = (value + 1) * 2
        int codedHeight = ReadBits(data, ref bitOffset, 12);
        int height = (codedHeight + 1) * 2;
        
        return (width, height);
    }
    
    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitOffset, int count)
    {
        int value = 0;
        
        for (int i = 0; i < count; i++)
        {
            int byteIndex = bitOffset / 8;
            int bitIndex = 7 - (bitOffset % 8);
            
            if (byteIndex >= data.Length)
                break;
            
            int bit = (data[byteIndex] >> bitIndex) & 1;
            value = (value << 1) | bit;
            bitOffset++;
        }
        
        return value;
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        _packetizer.Dispose();
    }
}

/// <summary>
/// Video packet from VC-1 ES demuxer.
/// </summary>
public readonly struct Vc1EsPacket
{
    /// <summary>Frame data.</summary>
    public byte[] Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long Pts { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long Dts { get; init; }
    
    /// <summary>True if this is a keyframe (I-frame).</summary>
    public bool IsKeyframe { get; init; }
    
    /// <summary>Frame type (I, P, B, BI, Skipped).</summary>
    public Vc1FrameType FrameType { get; init; }
}

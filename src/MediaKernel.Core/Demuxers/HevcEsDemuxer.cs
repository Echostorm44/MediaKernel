// HEVC/H.265 raw elementary stream demuxer
// Demuxes raw Annex-B HEVC bitstreams (.hevc, .h265, .265 files)
// Reference: VLC's modules/demux/mpeg/h26x.c

using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for HEVC elementary stream demuxer.
/// </summary>
public sealed class HevcEsConfiguration
{
    /// <summary>
    /// Frame rate to use when not available from SPS.
    /// Default is 25.0 fps.
    /// </summary>
    public double DefaultFrameRate { get; init; } = 25.0;
}

/// <summary>
/// Result from probing a stream for HEVC content.
/// </summary>
public readonly struct HevcEsProbeResult
{
    /// <summary>Whether valid HEVC content was detected.</summary>
    public bool IsHevc { get; init; }
    
    /// <summary>Whether a Video Parameter Set was found.</summary>
    public bool HasVps { get; init; }
    
    /// <summary>Whether a Sequence Parameter Set was found.</summary>
    public bool HasSps { get; init; }
    
    /// <summary>Whether a Picture Parameter Set was found.</summary>
    public bool HasPps { get; init; }
    
    /// <summary>Video width from SPS (0 if not available).</summary>
    public int Width { get; init; }
    
    /// <summary>Video height from SPS (0 if not available).</summary>
    public int Height { get; init; }
}

/// <summary>
/// Demuxer for raw HEVC/H.265 elementary streams (Annex-B format).
/// </summary>
/// <remarks>
/// Raw elementary streams contain NAL units with start code prefixes (0x000001 or 0x00000001).
/// This demuxer reads chunks, detects frame boundaries via the packetizer, and outputs
/// complete access units with interpolated timestamps.
/// </remarks>
public sealed class HevcEsDemuxer : IDisposable
{
    private const int ReadChunkSize = 4096;
    private const int MaxProbeSize = 32768;
    
    private readonly Stream _stream;
    private readonly HevcEsConfiguration _config;
    private readonly HevcPacketizer _packetizer;
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
    
    /// <summary>Detected frame rate (may be from SPS or default).</summary>
    public double FrameRate => _frameRate;
    
    /// <summary>Codec identifier.</summary>
    public string Codec => "hevc";
    
    /// <summary>Creates a new HEVC ES demuxer.</summary>
    /// <param name="stream">Source stream containing raw HEVC data.</param>
    /// <param name="config">Optional configuration.</param>
    public HevcEsDemuxer(Stream stream, HevcEsConfiguration? config = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _config = config ?? new HevcEsConfiguration();
        _packetizer = new HevcPacketizer();
        _readBuffer = new byte[ReadChunkSize];
        
        _frameRate = _config.DefaultFrameRate;
        _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _frameRate);
        _packetizer.DefaultFrameRate = _frameRate;
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid HEVC elementary stream data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static HevcEsProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new HevcEsProbeResult { IsHevc = false };
        
        long startPosition = stream.Position;
        
        try
        {
            // Read probe data
            int probeSize = (int)Math.Min(MaxProbeSize, stream.Length - stream.Position);
            if (probeSize < 8)
                return new HevcEsProbeResult { IsHevc = false };
            
            byte[] data = new byte[probeSize];
            int bytesRead = stream.Read(data, 0, probeSize);
            if (bytesRead < 8)
                return new HevcEsProbeResult { IsHevc = false };
            
            return ProbeData(data.AsSpan(0, bytesRead));
        }
        finally
        {
            stream.Position = startPosition;
        }
    }
    
    /// <summary>
    /// Probes raw data for HEVC content.
    /// </summary>
    public static HevcEsProbeResult ProbeData(ReadOnlySpan<byte> data)
    {
        bool foundStartCode = false;
        bool hasVps = false;
        bool hasSps = false;
        bool hasPps = false;
        int width = 0;
        int height = 0;
        int nalCount = 0;
        const int maxNalCheck = 8;
        
        int offset = 0;
        
        while (offset < data.Length - 4 && nalCount < maxNalCheck)
        {
            // Look for start code
            int startCodeLength = 0;
            
            if (offset + 3 < data.Length &&
                data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
            {
                startCodeLength = 3;
            }
            else if (offset + 4 < data.Length &&
                     data[offset] == 0 && data[offset + 1] == 0 &&
                     data[offset + 2] == 0 && data[offset + 3] == 1)
            {
                startCodeLength = 4;
            }
            
            if (startCodeLength > 0)
            {
                foundStartCode = true;
                int nalStart = offset + startCodeLength;
                
                if (nalStart + 1 < data.Length)
                {
                    byte nalHeader0 = data[nalStart];
                    byte nalHeader1 = data[nalStart + 1];
                    
                    // Check forbidden_zero_bit (must be 0)
                    if ((nalHeader0 & 0x80) != 0)
                    {
                        offset++;
                        continue;
                    }
                    
                    // HEVC NAL type is bits 1-6 of first byte
                    int nalType = (nalHeader0 >> 1) & 0x3F;
                    
                    // Valid HEVC NAL types are 0-63
                    // Check for reasonable types
                    nalCount++;
                    
                    // VPS (NAL type 32)
                    if (nalType == 32)
                    {
                        hasVps = true;
                    }
                    // SPS (NAL type 33)
                    else if (nalType == 33)
                    {
                        hasSps = true;
                        // Try to parse width/height from SPS
                        int nalEnd = FindNextStartCode(data, nalStart + 2);
                        if (nalEnd > nalStart + 2)
                        {
                            (width, height) = TryParseSpsSize(data.Slice(nalStart, nalEnd - nalStart));
                        }
                    }
                    // PPS (NAL type 34)
                    else if (nalType == 34)
                    {
                        hasPps = true;
                    }
                }
                
                offset = nalStart + 2;
            }
            else
            {
                offset++;
            }
        }
        
        // Valid HEVC ES should have start codes
        // Having VPS/SPS/PPS increases confidence
        bool isHevc = foundStartCode && nalCount > 0;
        
        return new HevcEsProbeResult
        {
            IsHevc = isHevc,
            HasVps = hasVps,
            HasSps = hasSps,
            HasPps = hasPps,
            Width = width,
            Height = height
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
        
        // Probe to get initial info
        var probe = Probe(_stream);
        
        if (!probe.IsHevc)
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
    public bool ReadPacket(out HevcEsPacket packet)
    {
        packet = default;
        
        if (_disposed)
            return false;
        
        // Try to get a complete access unit from the packetizer
        AccessUnit accessUnit;
        while (!_packetizer.TryGetAccessUnit(out accessUnit))
        {
            if (_endOfStream)
            {
                // Flush any remaining data
                _packetizer.Flush();
                
                if (_packetizer.TryGetAccessUnit(out accessUnit))
                {
                    packet = CreatePacket(accessUnit);
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
            
            // Feed data to packetizer (let it handle timestamp interpolation)
            _packetizer.Feed(_readBuffer.AsSpan(0, bytesRead), -1, -1, false);
        }
        
        packet = CreatePacket(accessUnit);
        return true;
    }
    
    /// <summary>
    /// Seeks to a position in the stream.
    /// </summary>
    /// <remarks>
    /// Seeking in raw elementary streams is approximate. The demuxer will seek to
    /// the requested byte position and search for the next valid NAL start code.
    /// </remarks>
    public bool SeekToPosition(long position)
    {
        if (_disposed || !_stream.CanSeek)
            return false;
        
        _stream.Position = Math.Clamp(position, 0, _stream.Length);
        _packetizer.Reset();
        _endOfStream = false;
        
        // Scan for next start code
        return ScanForStartCode();
    }
    
    /// <summary>
    /// Seeks to approximate time position.
    /// </summary>
    /// <remarks>
    /// Since raw ES has no index, this estimates position based on average bitrate.
    /// </remarks>
    public bool SeekToTime(TimeSpan time)
    {
        if (_disposed || !_stream.CanSeek || _stream.Length == 0)
            return false;
        
        // Estimate position based on linear distribution (crude but best we can do)
        double fraction = time.TotalSeconds * _frameRate / EstimateTotalFrames();
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        
        long position = (long)(_stream.Length * fraction);
        
        // Update DTS estimate
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
    
    private HevcEsPacket CreatePacket(AccessUnit au)
    {
        // Update dimensions from SPS if available
        if (_packetizer.CurrentSps.Length > 0)
        {
            var (w, h) = TryParseSpsSize(_packetizer.CurrentSps);
            if (w > 0 && h > 0)
            {
                _width = w;
                _height = h;
            }
        }
        
        var packet = new HevcEsPacket
        {
            Data = au.Data,
            Pts = au.PresentationTimestamp >= 0 ? au.PresentationTimestamp : _currentDts,
            Dts = au.DecodeTimestamp >= 0 ? au.DecodeTimestamp : _currentDts,
            IsKeyframe = au.IsKeyframe
        };
        
        // Advance timestamp for next frame
        _currentDts += _frameDurationTicks;
        
        return packet;
    }
    
    private bool ScanForStartCode()
    {
        // Scan for next 0x00 0x00 0x01 or 0x00 0x00 0x00 0x01
        byte[] buffer = new byte[256];
        
        while (true)
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            if (bytesRead < 4)
                return false;
            
            for (int i = 0; i < bytesRead - 3; i++)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0)
                {
                    if (buffer[i + 2] == 1 ||
                        (i + 3 < bytesRead && buffer[i + 2] == 0 && buffer[i + 3] == 1))
                    {
                        // Found start code - rewind to this position
                        _stream.Position -= (bytesRead - i);
                        return true;
                    }
                }
            }
            
            // Keep last 3 bytes to handle start codes spanning reads
            if (_stream.CanSeek)
                _stream.Position -= 3;
            else
                break;
        }
        
        return false;
    }
    
    private double EstimateTotalFrames()
    {
        // Rough estimate: assume ~2 Mbit/s for typical HEVC (more efficient than H.264)
        const double assumedBitrateKbps = 2000;
        double estimatedDuration = (_stream.Length * 8.0) / (assumedBitrateKbps * 1000);
        return estimatedDuration * _frameRate;
    }
    
    private static int FindNextStartCode(ReadOnlySpan<byte> data, int startOffset)
    {
        for (int i = startOffset; i < data.Length - 3; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1 ||
                    (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1))
                {
                    return i;
                }
            }
        }
        return data.Length;
    }
    
    private static (int width, int height) TryParseSpsSize(ReadOnlySpan<byte> spsNal)
    {
        // Minimal HEVC SPS parsing for dimensions
        // Full parsing is done by HevcPacketizer
        
        if (spsNal.Length < 15)
            return (0, 0);
        
        // HEVC SPS structure (simplified):
        // 2-byte NAL header
        // 4 bits: sps_video_parameter_set_id
        // 3 bits: sps_max_sub_layers_minus1
        // 1 bit: sps_temporal_id_nesting_flag
        // profile_tier_level (variable)
        // ue(v): sps_seq_parameter_set_id
        // ue(v): chroma_format_idc
        // if chroma_format_idc == 3: 1 bit separate_colour_plane_flag
        // ue(v): pic_width_in_luma_samples
        // ue(v): pic_height_in_luma_samples
        
        // Skip NAL header (2 bytes)
        int bitPos = 16;
        
        // Skip sps_video_parameter_set_id (4 bits)
        bitPos += 4;
        
        // Read sps_max_sub_layers_minus1 (3 bits)
        int maxSubLayersMinus1 = ReadBits(spsNal, ref bitPos, 3);
        
        // Skip sps_temporal_id_nesting_flag (1 bit)
        bitPos += 1;
        
        // Skip profile_tier_level (12 bytes base + variable for sub-layers)
        // Simplified: skip 96 bits (12 bytes) for base profile_tier_level
        bitPos += 96;
        
        // Skip sub_layer info if present (2 bits per sub-layer for flags)
        if (maxSubLayersMinus1 > 0)
        {
            // sub_layer_profile_present_flag and sub_layer_level_present_flag
            bitPos += maxSubLayersMinus1 * 2;
            
            // Reserved bits if maxSubLayersMinus1 < 8
            if (maxSubLayersMinus1 < 8)
                bitPos += (8 - maxSubLayersMinus1) * 2;
        }
        
        // sps_seq_parameter_set_id (ue(v))
        if (!SkipExpGolomb(spsNal, ref bitPos)) return (0, 0);
        
        // chroma_format_idc (ue(v))
        uint chromaFormatIdc = ReadExpGolomb(spsNal, ref bitPos);
        
        // separate_colour_plane_flag if chroma == 3
        if (chromaFormatIdc == 3)
            bitPos++;
        
        // pic_width_in_luma_samples (ue(v))
        uint width = ReadExpGolomb(spsNal, ref bitPos);
        
        // pic_height_in_luma_samples (ue(v))
        uint height = ReadExpGolomb(spsNal, ref bitPos);
        
        return ((int)width, (int)height);
    }
    
    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitPos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            if (bitPos / 8 >= data.Length)
                break;
            
            int byteIndex = bitPos / 8;
            int bitIndex = 7 - (bitPos % 8);
            
            value = (value << 1) | ((data[byteIndex] >> bitIndex) & 1);
            bitPos++;
        }
        return value;
    }
    
    private static bool SkipExpGolomb(ReadOnlySpan<byte> data, ref int bitPos)
    {
        int leadingZeros = 0;
        
        while (bitPos / 8 < data.Length)
        {
            int byteIndex = bitPos / 8;
            int bitIndex = 7 - (bitPos % 8);
            
            if (((data[byteIndex] >> bitIndex) & 1) == 0)
            {
                leadingZeros++;
                bitPos++;
                
                if (leadingZeros > 32)
                    return false;
            }
            else
            {
                break;
            }
        }
        
        // Skip the 1 bit and the value bits
        bitPos += 1 + leadingZeros;
        
        return bitPos / 8 < data.Length + 4;
    }
    
    private static uint ReadExpGolomb(ReadOnlySpan<byte> data, ref int bitPos)
    {
        int leadingZeros = 0;
        
        while (bitPos / 8 < data.Length)
        {
            int byteIndex = bitPos / 8;
            int bitIndex = 7 - (bitPos % 8);
            
            if (((data[byteIndex] >> bitIndex) & 1) == 0)
            {
                leadingZeros++;
                bitPos++;
                
                if (leadingZeros > 32)
                    return 0;
            }
            else
            {
                break;
            }
        }
        
        // Skip the 1 bit
        bitPos++;
        
        if (leadingZeros == 0)
            return 0;
        
        // Read value bits
        uint value = 0;
        for (int i = 0; i < leadingZeros; i++)
        {
            if (bitPos / 8 >= data.Length)
                break;
            
            int byteIndex = bitPos / 8;
            int bitIndex = 7 - (bitPos % 8);
            
            value = (value << 1) | (uint)((data[byteIndex] >> bitIndex) & 1);
            bitPos++;
        }
        
        return (1u << leadingZeros) - 1 + value;
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
/// Video packet from HEVC ES demuxer.
/// </summary>
public readonly struct HevcEsPacket
{
    /// <summary>Frame data (complete access unit).</summary>
    public byte[] Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long Pts { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long Dts { get; init; }
    
    /// <summary>True if this is a keyframe (IDR/CRA/BLA).</summary>
    public bool IsKeyframe { get; init; }
}

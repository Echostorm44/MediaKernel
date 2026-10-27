// H.264/AVC raw elementary stream demuxer
// Demuxes raw Annex-B H.264 bitstreams (.h264, .264, .avc files)
// Reference: VLC's modules/demux/mpeg/h26x.c

using System.Buffers.Binary;
using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for H.264 elementary stream demuxer.
/// </summary>
public sealed class H264EsConfiguration
{
    /// <summary>
    /// Frame rate to use when not available from SPS.
    /// Default is 25.0 fps.
    /// </summary>
    public double DefaultFrameRate { get; init; } = 25.0;
}

/// <summary>
/// Result from probing a stream for H.264 content.
/// </summary>
public readonly struct H264EsProbeResult
{
    /// <summary>Whether valid H.264 content was detected.</summary>
    public bool IsH264 { get; init; }
    
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
/// Demuxer for raw H.264/AVC elementary streams (Annex-B format).
/// </summary>
/// <remarks>
/// Raw elementary streams contain NAL units with start code prefixes (0x000001 or 0x00000001).
/// This demuxer reads chunks, detects frame boundaries via the packetizer, and outputs
/// complete access units with interpolated timestamps.
/// </remarks>
public sealed class H264EsDemuxer : IDisposable
{
    private const int ReadChunkSize = 4096;
    private const int MaxProbeSize = 32768;
    
    private readonly Stream _stream;
    private readonly H264EsConfiguration _config;
    private readonly H264Packetizer _packetizer;
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
    public string Codec => "h264";
    
    /// <summary>Creates a new H.264 ES demuxer.</summary>
    /// <param name="stream">Source stream containing raw H.264 data.</param>
    /// <param name="config">Optional configuration.</param>
    public H264EsDemuxer(Stream stream, H264EsConfiguration? config = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _config = config ?? new H264EsConfiguration();
        _packetizer = new H264Packetizer();
        _readBuffer = new byte[ReadChunkSize];
        
        _frameRate = _config.DefaultFrameRate;
        _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _frameRate);
        _packetizer.DefaultFrameRate = _frameRate;
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid H.264 elementary stream data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static H264EsProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new H264EsProbeResult { IsH264 = false };
        
        long startPosition = stream.Position;
        
        try
        {
            // Read probe data
            int probeSize = (int)Math.Min(MaxProbeSize, stream.Length - stream.Position);
            if (probeSize < 8)
                return new H264EsProbeResult { IsH264 = false };
            
            byte[] data = new byte[probeSize];
            int bytesRead = stream.Read(data, 0, probeSize);
            if (bytesRead < 8)
                return new H264EsProbeResult { IsH264 = false };
            
            return ProbeData(data.AsSpan(0, bytesRead));
        }
        finally
        {
            stream.Position = startPosition;
        }
    }
    
    /// <summary>
    /// Probes raw data for H.264 content.
    /// </summary>
    public static H264EsProbeResult ProbeData(ReadOnlySpan<byte> data)
    {
        bool foundStartCode = false;
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
                
                if (nalStart < data.Length)
                {
                    byte nalHeader = data[nalStart];
                    
                    // Check forbidden_zero_bit (must be 0)
                    if ((nalHeader & 0x80) != 0)
                    {
                        offset++;
                        continue;
                    }
                    
                    int nalType = nalHeader & 0x1F;
                    
                    // Valid H.264 NAL types are 0-23 (and some extensions up to 31)
                    if (nalType == 0 || nalType > 23)
                    {
                        offset++;
                        continue;
                    }
                    
                    nalCount++;
                    
                    // Check for SPS (NAL type 7)
                    if (nalType == 7)
                    {
                        hasSps = true;
                        // Try to parse width/height from SPS
                        (width, height) = TryParseSpsSize(data, nalStart);
                    }
                    // Check for PPS (NAL type 8)
                    else if (nalType == 8)
                    {
                        hasPps = true;
                    }
                }
                
                offset = nalStart + 1;
            }
            else
            {
                offset++;
            }
        }
        
        // Valid H.264 ES should have start codes
        bool isH264 = foundStartCode && nalCount > 0;
        
        return new H264EsProbeResult
        {
            IsH264 = isH264,
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
        
        if (!probe.IsH264)
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
    public bool ReadPacket(out H264EsPacket packet)
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
        // This is very approximate for raw ES
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
    
    private H264EsPacket CreatePacket(AccessUnit au)
    {
        // Update dimensions from SPS if available
        if (_packetizer.CurrentSps.Length > 0)
        {
            var (w, h) = TryParseSpsSize(_packetizer.CurrentSps, 0);
            if (w > 0 && h > 0)
            {
                _width = w;
                _height = h;
            }
        }
        
        var packet = new H264EsPacket
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
        // Rough estimate: assume ~1 Mbit/s for typical H.264
        // This is very crude but we have no index
        const double assumedBitrateKbps = 1000;
        double estimatedDuration = (_stream.Length * 8.0) / (assumedBitrateKbps * 1000);
        return estimatedDuration * _frameRate;
    }
    
    private static (int width, int height) TryParseSpsSize(ReadOnlySpan<byte> data, int offset)
    {
        // Simple SPS parsing for width/height
        // This is a minimal parser - the full H264Packetizer handles complete parsing
        
        if (offset + 10 >= data.Length)
            return (0, 0);
        
        // Skip NAL header
        int pos = offset + 1;
        
        // profile_idc
        if (pos >= data.Length) return (0, 0);
        byte profileIdc = data[pos++];
        
        // constraint flags + level
        if (pos + 2 >= data.Length) return (0, 0);
        pos += 2;
        
        // Skip seq_parameter_set_id (exp-golomb)
        if (!SkipExpGolomb(data, ref pos)) return (0, 0);
        
        // For high profiles, skip chroma/bit depth info
        if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 ||
            profileIdc == 244 || profileIdc == 44 || profileIdc == 83 ||
            profileIdc == 86 || profileIdc == 118 || profileIdc == 128)
        {
            // chroma_format_idc
            uint chromaFormatIdc = ReadExpGolomb(data, ref pos);
            if (chromaFormatIdc == 3)
            {
                // separate_colour_plane_flag
                if (pos / 8 >= data.Length) return (0, 0);
                pos++;
            }
            
            // bit_depth_luma_minus8
            if (!SkipExpGolomb(data, ref pos)) return (0, 0);
            // bit_depth_chroma_minus8
            if (!SkipExpGolomb(data, ref pos)) return (0, 0);
            // qpprime_y_zero_transform_bypass_flag
            pos++;
            
            // seq_scaling_matrix_present_flag
            if (pos / 8 >= data.Length) return (0, 0);
            // Skip scaling matrices if present (complex - just return 0,0 for now)
        }
        
        // log2_max_frame_num_minus4
        if (!SkipExpGolomb(data, ref pos)) return (0, 0);
        
        // pic_order_cnt_type
        uint pocType = ReadExpGolomb(data, ref pos);
        
        if (pocType == 0)
        {
            // log2_max_pic_order_cnt_lsb_minus4
            if (!SkipExpGolomb(data, ref pos)) return (0, 0);
        }
        else if (pocType == 1)
        {
            // Skip delta_pic_order_always_zero_flag (1 bit)
            pos++;
            // offset_for_non_ref_pic
            if (!SkipExpGolomb(data, ref pos)) return (0, 0);
            // offset_for_top_to_bottom_field
            if (!SkipExpGolomb(data, ref pos)) return (0, 0);
            // num_ref_frames_in_pic_order_cnt_cycle
            uint numRefFrames = ReadExpGolomb(data, ref pos);
            for (uint i = 0; i < numRefFrames; i++)
            {
                if (!SkipExpGolomb(data, ref pos)) return (0, 0);
            }
        }
        
        // max_num_ref_frames
        if (!SkipExpGolomb(data, ref pos)) return (0, 0);
        
        // gaps_in_frame_num_value_allowed_flag
        pos++;
        
        // pic_width_in_mbs_minus1
        uint widthMbs = ReadExpGolomb(data, ref pos) + 1;
        
        // pic_height_in_map_units_minus1
        uint heightMapUnits = ReadExpGolomb(data, ref pos) + 1;
        
        int width = (int)(widthMbs * 16);
        int height = (int)(heightMapUnits * 16);
        
        // frame_mbs_only_flag affects height
        // For simplicity, assume frame-based (most common)
        
        return (width, height);
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
                    return false; // Invalid
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
/// Video packet from H.264 ES demuxer.
/// </summary>
public readonly struct H264EsPacket
{
    /// <summary>Frame data (complete access unit).</summary>
    public byte[] Data { get; init; }
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long Pts { get; init; }
    
    /// <summary>Decode timestamp in ticks.</summary>
    public long Dts { get; init; }
    
    /// <summary>True if this is a keyframe (IDR).</summary>
    public bool IsKeyframe { get; init; }
}

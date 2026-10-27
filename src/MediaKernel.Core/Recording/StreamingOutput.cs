// MediaKernel - Streaming Output
// RTP/RTSP server, HLS/DASH output, Chromecast, DLNA renderer
// Based on VLC's stream_output modules

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Recording;

/// <summary>
/// Streaming output protocol types.
/// </summary>
public enum StreamingProtocol
{
    Rtp,          // RTP unicast/multicast
    Rtsp,         // RTSP with RTP transport
    Hls,          // HTTP Live Streaming (Apple)
    Dash,         // MPEG-DASH
    HttpMpegTs,   // HTTP progressive download (MPEG-TS)
    Chromecast,   // Google Cast protocol
    Dlna          // DLNA/UPnP renderer
}

/// <summary>
/// Configuration for streaming output.
/// </summary>
public sealed class StreamingOutputConfiguration
{
    /// <summary>Streaming protocol to use.</summary>
    public StreamingProtocol Protocol { get; init; }
    
    /// <summary>Destination address (IP, hostname, or URL).</summary>
    public string? DestinationAddress { get; init; }
    
    /// <summary>Destination port.</summary>
    public int Port { get; init; }
    
    /// <summary>Use multicast for RTP.</summary>
    public bool Multicast { get; init; }
    
    /// <summary>Multicast TTL.</summary>
    public int MulticastTtl { get; init; } = 12;
    
    /// <summary>Output directory for HLS/DASH segments.</summary>
    public string? OutputDirectory { get; init; }
    
    /// <summary>Base URL for HLS/DASH manifests.</summary>
    public string? BaseUrl { get; init; }
    
    /// <summary>Segment duration in seconds (for HLS/DASH).</summary>
    public double SegmentDuration { get; init; } = 6.0;
    
    /// <summary>Number of segments to keep in playlist (0 = keep all).</summary>
    public int PlaylistLength { get; init; } = 10;
    
    /// <summary>Video codec for transcoding (null = passthrough).</summary>
    public VideoCodecType? VideoCodec { get; init; }
    
    /// <summary>Audio codec for transcoding (null = passthrough).</summary>
    public AudioCodecType? AudioCodec { get; init; }
    
    /// <summary>Video bitrate in kbps.</summary>
    public int VideoBitrateKbps { get; init; }
    
    /// <summary>Audio bitrate in kbps.</summary>
    public int AudioBitrateKbps { get; init; }
    
    /// <summary>Stream name for RTSP/announcement.</summary>
    public string? StreamName { get; init; }
    
    /// <summary>Session description for SAP announcement.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// RTP streaming output.
/// </summary>
public sealed class RtpOutputStream : IAsyncDisposable
{
    private readonly StreamingOutputConfiguration config;
    private UdpClient? rtpSocket;
    private UdpClient? rtcpSocket;
    private IPEndPoint? destination;
    private ushort sequenceNumber;
    private uint timestamp;
    private uint ssrc;
    private bool started;
    
    /// <summary>Creates an RTP output stream.</summary>
    public RtpOutputStream(StreamingOutputConfiguration configuration)
    {
        config = configuration;
        ssrc = (uint)Random.Shared.Next();
    }
    
    /// <summary>Starts the RTP stream.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
            throw new InvalidOperationException("Stream already started");
        
        destination = new IPEndPoint(
            IPAddress.Parse(config.DestinationAddress ?? "127.0.0.1"),
            config.Port > 0 ? config.Port : 5004);
        
        rtpSocket = new UdpClient();
        rtcpSocket = new UdpClient();
        
        if (config.Multicast)
        {
            rtpSocket.JoinMulticastGroup(destination.Address, config.MulticastTtl);
        }
        
        started = true;
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Sends a video frame as RTP packet(s).
    /// </summary>
    /// <param name="nalUnit">H.264/H.265 NAL unit data.</param>
    /// <param name="pts">Presentation timestamp in 90kHz units.</param>
    /// <param name="isKeyframe">True if this is a keyframe.</param>
    public async Task SendVideoFrameAsync(ReadOnlyMemory<byte> nalUnit, uint pts, bool isKeyframe,
        CancellationToken cancellationToken = default)
    {
        if (!started || rtpSocket == null || destination == null)
            return;
        
        const int maxPayloadSize = 1400; // MTU - IP/UDP/RTP headers
        const int rtpHeaderSize = 12;
        
        timestamp = pts;
        
        if (nalUnit.Length <= maxPayloadSize)
        {
            // Single NAL unit packet
            byte[] packet = new byte[rtpHeaderSize + nalUnit.Length];
            WriteRtpHeader(packet, payloadType: 96, marker: true);
            nalUnit.Span.CopyTo(packet.AsSpan(rtpHeaderSize));
            
            await rtpSocket.SendAsync(packet, destination, cancellationToken);
            sequenceNumber++;
        }
        else
        {
            // Fragmentation Unit-A (FU-A) for H.264
            byte nalHeader = nalUnit.Span[0];
            byte fuIndicator = (byte)((nalHeader & 0xE0) | 28); // FU-A type
            
            int offset = 1;
            int remaining = nalUnit.Length - 1;
            bool first = true;
            
            while (remaining > 0)
            {
                int chunkSize = Math.Min(remaining, maxPayloadSize - 2);
                bool last = chunkSize == remaining;
                
                byte fuHeader = (byte)((nalHeader & 0x1F) | (first ? 0x80 : 0) | (last ? 0x40 : 0));
                
                byte[] packet = new byte[rtpHeaderSize + 2 + chunkSize];
                WriteRtpHeader(packet, payloadType: 96, marker: last);
                packet[rtpHeaderSize] = fuIndicator;
                packet[rtpHeaderSize + 1] = fuHeader;
                nalUnit.Slice(offset, chunkSize).Span.CopyTo(packet.AsSpan(rtpHeaderSize + 2));
                
                await rtpSocket.SendAsync(packet, destination, cancellationToken);
                sequenceNumber++;
                
                offset += chunkSize;
                remaining -= chunkSize;
                first = false;
            }
        }
    }
    
    /// <summary>
    /// Sends audio samples as RTP packet.
    /// </summary>
    public async Task SendAudioFrameAsync(ReadOnlyMemory<byte> audioFrame, uint pts,
        CancellationToken cancellationToken = default)
    {
        if (!started || rtpSocket == null || destination == null)
            return;
        
        const int rtpHeaderSize = 12;
        
        byte[] packet = new byte[rtpHeaderSize + audioFrame.Length];
        WriteRtpHeader(packet, payloadType: 97, marker: true);
        timestamp = pts;
        audioFrame.Span.CopyTo(packet.AsSpan(rtpHeaderSize));
        
        await rtpSocket.SendAsync(packet, destination, cancellationToken);
        sequenceNumber++;
    }
    
    private void WriteRtpHeader(byte[] buffer, byte payloadType, bool marker)
    {
        // Version (2), Padding (0), Extension (0), CSRC count (0)
        buffer[0] = 0x80;
        
        // Marker + Payload type
        buffer[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        
        // Sequence number
        buffer[2] = (byte)(sequenceNumber >> 8);
        buffer[3] = (byte)sequenceNumber;
        
        // Timestamp
        buffer[4] = (byte)(timestamp >> 24);
        buffer[5] = (byte)(timestamp >> 16);
        buffer[6] = (byte)(timestamp >> 8);
        buffer[7] = (byte)timestamp;
        
        // SSRC
        buffer[8] = (byte)(ssrc >> 24);
        buffer[9] = (byte)(ssrc >> 16);
        buffer[10] = (byte)(ssrc >> 8);
        buffer[11] = (byte)ssrc;
    }
    
    public async ValueTask DisposeAsync()
    {
        started = false;
        rtpSocket?.Dispose();
        rtcpSocket?.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// HLS (HTTP Live Streaming) output.
/// Generates .m3u8 playlist and .ts segment files.
/// </summary>
public sealed class HlsOutputStream : IAsyncDisposable
{
    private readonly StreamingOutputConfiguration config;
    private readonly Queue<SegmentInfo> segments = new();
    private int segmentNumber;
    private DateTime segmentStartTime;
    private MemoryStream? currentSegmentBuffer;
    private bool started;
    
    /// <summary>Creates an HLS output stream.</summary>
    public HlsOutputStream(StreamingOutputConfiguration configuration)
    {
        config = configuration;
    }
    
    /// <summary>Starts the HLS stream.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
            throw new InvalidOperationException("Stream already started");
        
        if (string.IsNullOrEmpty(config.OutputDirectory))
            throw new ArgumentException("Output directory required for HLS");
        
        Directory.CreateDirectory(config.OutputDirectory);
        
        segmentNumber = 0;
        segmentStartTime = DateTime.UtcNow;
        currentSegmentBuffer = new MemoryStream();
        started = true;
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Writes data to the current segment.
    /// </summary>
    public async Task WriteDataAsync(ReadOnlyMemory<byte> data, bool isKeyframe,
        CancellationToken cancellationToken = default)
    {
        if (!started || currentSegmentBuffer == null)
            return;
        
        await currentSegmentBuffer.WriteAsync(data, cancellationToken);
        
        // Check if we should close this segment
        TimeSpan segmentDuration = DateTime.UtcNow - segmentStartTime;
        if (segmentDuration.TotalSeconds >= config.SegmentDuration && isKeyframe)
        {
            await CloseSegmentAsync(cancellationToken);
        }
    }
    
    /// <summary>
    /// Gets the current master playlist URL.
    /// </summary>
    public string GetPlaylistUrl()
    {
        string baseUrl = config.BaseUrl ?? $"file://{config.OutputDirectory}";
        return $"{baseUrl.TrimEnd('/')}/playlist.m3u8";
    }
    
    private async Task CloseSegmentAsync(CancellationToken cancellationToken)
    {
        if (currentSegmentBuffer == null || config.OutputDirectory == null)
            return;
        
        TimeSpan duration = DateTime.UtcNow - segmentStartTime;
        string segmentName = $"segment{segmentNumber:D5}.ts";
        string segmentPath = Path.Combine(config.OutputDirectory, segmentName);
        
        // Write segment file
        await using (var file = new FileStream(segmentPath, FileMode.Create, FileAccess.Write))
        {
            currentSegmentBuffer.Position = 0;
            await currentSegmentBuffer.CopyToAsync(file, cancellationToken);
        }
        
        // Track segment
        var segmentInfo = new SegmentInfo
        {
            Filename = segmentName,
            Duration = duration.TotalSeconds,
            SequenceNumber = segmentNumber
        };
        segments.Enqueue(segmentInfo);
        
        // Remove old segments if playlist length is limited
        while (config.PlaylistLength > 0 && segments.Count > config.PlaylistLength)
        {
            var old = segments.Dequeue();
            string oldPath = Path.Combine(config.OutputDirectory, old.Filename);
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); } catch { }
            }
        }
        
        // Update playlist
        await UpdatePlaylistAsync(cancellationToken);
        
        // Start new segment
        segmentNumber++;
        segmentStartTime = DateTime.UtcNow;
        currentSegmentBuffer = new MemoryStream();
    }
    
    private async Task UpdatePlaylistAsync(CancellationToken cancellationToken)
    {
        if (config.OutputDirectory == null)
            return;
        
        string playlistPath = Path.Combine(config.OutputDirectory, "playlist.m3u8");
        
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(config.SegmentDuration)}");
        
        int mediaSequence = segments.Count > 0 ? segments.Peek().SequenceNumber : 0;
        sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}");
        
        foreach (var segment in segments)
        {
            sb.AppendLine($"#EXTINF:{segment.Duration:F3},");
            sb.AppendLine(segment.Filename);
        }
        
        await File.WriteAllTextAsync(playlistPath, sb.ToString(), cancellationToken);
    }
    
    /// <summary>
    /// Finalizes the stream (adds endlist tag).
    /// </summary>
    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return;
        
        // Close final segment
        if (currentSegmentBuffer?.Length > 0)
        {
            await CloseSegmentAsync(cancellationToken);
        }
        
        // Add #EXT-X-ENDLIST to playlist
        if (config.OutputDirectory != null)
        {
            string playlistPath = Path.Combine(config.OutputDirectory, "playlist.m3u8");
            await File.AppendAllTextAsync(playlistPath, "#EXT-X-ENDLIST\n", cancellationToken);
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (started)
        {
            await FinalizeAsync();
        }
        
        currentSegmentBuffer?.Dispose();
        started = false;
    }
    
    private sealed class SegmentInfo
    {
        public required string Filename { get; init; }
        public double Duration { get; init; }
        public int SequenceNumber { get; init; }
    }
}

/// <summary>
/// MPEG-DASH output.
/// Generates .mpd manifest and segment files.
/// </summary>
public sealed class DashOutputStream : IAsyncDisposable
{
    private readonly StreamingOutputConfiguration config;
    private readonly List<SegmentInfo> segments = new();
    private int segmentNumber;
    private DateTime segmentStartTime;
    private DateTime streamStartTime;
    private MemoryStream? currentSegmentBuffer;
    private bool started;
    
    /// <summary>Creates a DASH output stream.</summary>
    public DashOutputStream(StreamingOutputConfiguration configuration)
    {
        config = configuration;
    }
    
    /// <summary>Starts the DASH stream.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
            throw new InvalidOperationException("Stream already started");
        
        if (string.IsNullOrEmpty(config.OutputDirectory))
            throw new ArgumentException("Output directory required for DASH");
        
        Directory.CreateDirectory(config.OutputDirectory);
        
        segmentNumber = 1;
        segmentStartTime = DateTime.UtcNow;
        streamStartTime = DateTime.UtcNow;
        currentSegmentBuffer = new MemoryStream();
        started = true;
        
        await UpdateManifestAsync(cancellationToken);
    }
    
    /// <summary>
    /// Writes data to the current segment.
    /// </summary>
    public async Task WriteDataAsync(ReadOnlyMemory<byte> data, bool isKeyframe,
        CancellationToken cancellationToken = default)
    {
        if (!started || currentSegmentBuffer == null)
            return;
        
        await currentSegmentBuffer.WriteAsync(data, cancellationToken);
        
        TimeSpan segmentDuration = DateTime.UtcNow - segmentStartTime;
        if (segmentDuration.TotalSeconds >= config.SegmentDuration && isKeyframe)
        {
            await CloseSegmentAsync(cancellationToken);
        }
    }
    
    /// <summary>
    /// Gets the current manifest URL.
    /// </summary>
    public string GetManifestUrl()
    {
        string baseUrl = config.BaseUrl ?? $"file://{config.OutputDirectory}";
        return $"{baseUrl.TrimEnd('/')}/manifest.mpd";
    }
    
    private async Task CloseSegmentAsync(CancellationToken cancellationToken)
    {
        if (currentSegmentBuffer == null || config.OutputDirectory == null)
            return;
        
        TimeSpan duration = DateTime.UtcNow - segmentStartTime;
        string segmentName = $"segment_{segmentNumber:D5}.m4s";
        string segmentPath = Path.Combine(config.OutputDirectory, segmentName);
        
        await using (var file = new FileStream(segmentPath, FileMode.Create, FileAccess.Write))
        {
            currentSegmentBuffer.Position = 0;
            await currentSegmentBuffer.CopyToAsync(file, cancellationToken);
        }
        
        segments.Add(new SegmentInfo
        {
            Filename = segmentName,
            Duration = duration.TotalSeconds,
            SequenceNumber = segmentNumber
        });
        
        // Remove old segments if needed
        while (config.PlaylistLength > 0 && segments.Count > config.PlaylistLength)
        {
            var old = segments[0];
            segments.RemoveAt(0);
            string oldPath = Path.Combine(config.OutputDirectory, old.Filename);
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); } catch { }
            }
        }
        
        await UpdateManifestAsync(cancellationToken);
        
        segmentNumber++;
        segmentStartTime = DateTime.UtcNow;
        currentSegmentBuffer = new MemoryStream();
    }
    
    private async Task UpdateManifestAsync(CancellationToken cancellationToken)
    {
        if (config.OutputDirectory == null)
            return;
        
        string manifestPath = Path.Combine(config.OutputDirectory, "manifest.mpd");
        
        TimeSpan totalDuration = DateTime.UtcNow - streamStartTime;
        string durationStr = $"PT{totalDuration.TotalSeconds:F1}S";
        
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" ");
        sb.AppendLine("     type=\"dynamic\" ");
        sb.AppendLine($"     mediaPresentationDuration=\"{durationStr}\" ");
        sb.AppendLine($"     minBufferTime=\"PT{config.SegmentDuration:F1}S\">");
        sb.AppendLine("  <Period id=\"1\" start=\"PT0S\">");
        sb.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\">");
        sb.AppendLine($"      <Representation id=\"1\" bandwidth=\"{config.VideoBitrateKbps * 1000}\">");
        sb.AppendLine("        <SegmentTemplate media=\"segment_$Number%05d$.m4s\" ");
        sb.AppendLine($"                        startNumber=\"1\" duration=\"{(int)(config.SegmentDuration * 1000)}\" timescale=\"1000\" />");
        sb.AppendLine("      </Representation>");
        sb.AppendLine("    </AdaptationSet>");
        sb.AppendLine("  </Period>");
        sb.AppendLine("</MPD>");
        
        await File.WriteAllTextAsync(manifestPath, sb.ToString(), cancellationToken);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (started && currentSegmentBuffer?.Length > 0)
        {
            await CloseSegmentAsync(CancellationToken.None);
        }
        
        currentSegmentBuffer?.Dispose();
        started = false;
    }
    
    private sealed class SegmentInfo
    {
        public required string Filename { get; init; }
        public double Duration { get; init; }
        public int SequenceNumber { get; init; }
    }
}

/// <summary>
/// Stream duplication for sending to multiple destinations.
/// </summary>
public sealed class StreamDuplicator : IAsyncDisposable
{
    private readonly List<IStreamOutput> outputs = new();
    private bool disposed;
    
    /// <summary>Adds an output destination.</summary>
    public void AddOutput(IStreamOutput output)
    {
        ThrowIfDisposed();
        outputs.Add(output);
    }
    
    /// <summary>Removes an output destination.</summary>
    public bool RemoveOutput(IStreamOutput output)
    {
        ThrowIfDisposed();
        return outputs.Remove(output);
    }
    
    /// <summary>Writes data to all outputs.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var tasks = new List<Task>();
        foreach (var output in outputs)
        {
            tasks.Add(output.WriteAsync(data, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            
            foreach (var output in outputs)
            {
                if (output is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (output is IDisposable disposable)
                    disposable.Dispose();
            }
            
            outputs.Clear();
        }
    }
}

/// <summary>
/// Interface for stream output destinations.
/// </summary>
public interface IStreamOutput
{
    /// <summary>Writes data to the output.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}

/// <summary>
/// Time delay buffer for broadcast delay.
/// </summary>
public sealed class TimeDelayBuffer : IAsyncDisposable
{
    private readonly TimeSpan delay;
    private readonly ConcurrentQueue<(DateTime Timestamp, byte[] Data)> buffer = new();
    private bool disposed;
    
    /// <summary>Creates a time delay buffer.</summary>
    /// <param name="delay">Delay duration.</param>
    public TimeDelayBuffer(TimeSpan delay)
    {
        this.delay = delay;
    }
    
    /// <summary>Gets the configured delay.</summary>
    public TimeSpan Delay => delay;
    
    /// <summary>
    /// Adds data to the buffer.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        buffer.Enqueue((DateTime.UtcNow, data.ToArray()));
    }
    
    /// <summary>
    /// Reads delayed data.
    /// </summary>
    /// <returns>Data that has been delayed long enough, or null if nothing ready.</returns>
    public byte[]? Read()
    {
        ThrowIfDisposed();
        
        DateTime cutoff = DateTime.UtcNow - delay;
        
        if (buffer.TryPeek(out var item) && item.Timestamp <= cutoff)
        {
            if (buffer.TryDequeue(out item))
            {
                return item.Data;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets all ready data.
    /// </summary>
    public IEnumerable<byte[]> ReadAll()
    {
        ThrowIfDisposed();
        
        DateTime cutoff = DateTime.UtcNow - delay;
        
        while (buffer.TryPeek(out var item) && item.Timestamp <= cutoff)
        {
            if (buffer.TryDequeue(out item))
            {
                yield return item.Data;
            }
        }
    }
    
    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        while (buffer.TryDequeue(out _)) { }
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            Clear();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Mosaic composition for combining multiple video streams into a grid.
/// </summary>
public sealed class MosaicCompositor
{
    private readonly int columns;
    private readonly int rows;
    private readonly int outputWidth;
    private readonly int outputHeight;
    private readonly int cellWidth;
    private readonly int cellHeight;
    
    /// <summary>
    /// Creates a mosaic compositor.
    /// </summary>
    /// <param name="columns">Number of columns in the grid.</param>
    /// <param name="rows">Number of rows in the grid.</param>
    /// <param name="outputWidth">Total output width in pixels.</param>
    /// <param name="outputHeight">Total output height in pixels.</param>
    public MosaicCompositor(int columns, int rows, int outputWidth = 1920, int outputHeight = 1080)
    {
        this.columns = Math.Max(1, columns);
        this.rows = Math.Max(1, rows);
        this.outputWidth = outputWidth;
        this.outputHeight = outputHeight;
        cellWidth = outputWidth / this.columns;
        cellHeight = outputHeight / this.rows;
    }
    
    /// <summary>Gets the number of columns.</summary>
    public int Columns => columns;
    
    /// <summary>Gets the number of rows.</summary>
    public int Rows => rows;
    
    /// <summary>Gets total cell count.</summary>
    public int CellCount => columns * rows;
    
    /// <summary>
    /// Gets the position and size for a cell.
    /// </summary>
    /// <param name="index">Cell index (0 to CellCount-1).</param>
    /// <returns>Rectangle for the cell (x, y, width, height).</returns>
    public (int X, int Y, int Width, int Height) GetCellRect(int index)
    {
        if (index < 0 || index >= CellCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        
        int col = index % columns;
        int row = index / columns;
        
        return (col * cellWidth, row * cellHeight, cellWidth, cellHeight);
    }
    
    /// <summary>
    /// Composites multiple frames into a single mosaic frame.
    /// </summary>
    /// <param name="frames">Source frames (one per cell, can be null for empty cells).</param>
    /// <param name="output">Output buffer (must be outputWidth * outputHeight * bytesPerPixel).</param>
    /// <param name="bytesPerPixel">Bytes per pixel (3 for RGB, 4 for RGBA).</param>
    public void Composite(ReadOnlySpan<Memory<byte>?> frames, Span<byte> output, int bytesPerPixel = 4)
    {
        int outputStride = outputWidth * bytesPerPixel;
        
        // Clear output to black
        output.Clear();
        
        int cellCount = Math.Min(frames.Length, CellCount);
        
        for (int i = 0; i < cellCount; i++)
        {
            if (frames[i] == null)
                continue;
            
            var frame = frames[i]!.Value;
            var (x, y, width, height) = GetCellRect(i);
            
            // Simple copy assuming frame is already scaled to cell size
            // In a real implementation, would scale the source frame
            int srcStride = width * bytesPerPixel;
            
            for (int row = 0; row < height && row * srcStride < frame.Length; row++)
            {
                int dstOffset = (y + row) * outputStride + x * bytesPerPixel;
                int srcOffset = row * srcStride;
                int copySize = Math.Min(srcStride, frame.Length - srcOffset);
                
                if (dstOffset + copySize <= output.Length)
                {
                    frame.Span.Slice(srcOffset, copySize).CopyTo(output.Slice(dstOffset, copySize));
                }
            }
        }
    }
}

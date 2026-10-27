// RTSP Client Implementation
// RFC 2326 / RFC 7826 compliant

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// RTSP client for streaming media sessions.
/// Implements RFC 2326 RTSP protocol with RTP/UDP and RTP/TCP interleaved transport.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private const int DefaultRtspPort = 554;
    private const int DefaultBufferSize = 8192;
    private const int ResponseTimeoutMs = 10000;
    
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private int _cseq;
    private string? _sessionId;
    private int? _sessionTimeout;
    private RtspSessionState _state = RtspSessionState.Init;
    private readonly List<RtspMediaTrack> _setupTracks = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;
    
    /// <summary>
    /// Gets the current RTSP session state.
    /// </summary>
    public RtspSessionState State => _state;
    
    /// <summary>
    /// Gets the session ID from the server.
    /// </summary>
    public string? SessionId => _sessionId;
    
    /// <summary>
    /// Gets the session timeout in seconds.
    /// </summary>
    public int? SessionTimeout => _sessionTimeout;
    
    /// <summary>
    /// Gets whether the connection is established.
    /// </summary>
    public bool IsConnected => _tcpClient?.Connected == true;
    
    /// <summary>
    /// Gets the User-Agent header value.
    /// </summary>
    public string UserAgent { get; set; } = "MediaKernel RTSP Client/1.0";
    
    /// <summary>
    /// Event raised when interleaved RTP data is received on TCP.
    /// </summary>
    public event Action<int, ReadOnlyMemory<byte>>? InterleavedDataReceived;
    
    /// <summary>
    /// Connects to an RTSP server.
    /// </summary>
    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (_tcpClient != null)
            throw new InvalidOperationException("Already connected");
        
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : DefaultRtspPort;
        
        _tcpClient = new TcpClient();
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
        
        _networkStream = _tcpClient.GetStream();
        _cseq = 0;
    }
    
    /// <summary>
    /// Sends OPTIONS request to query server capabilities.
    /// </summary>
    public async Task<RtspResponse> OptionsAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Options,
            Uri = uri
        };
        request.Headers["User-Agent"] = UserAgent;
        
        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Sends DESCRIBE request to get session description (SDP).
    /// </summary>
    public async Task<RtspSessionDescriptor?> DescribeAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Describe,
            Uri = uri
        };
        request.Headers["Accept"] = "application/sdp";
        request.Headers["User-Agent"] = UserAgent;
        
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (!response.IsSuccess || response.Body == null)
            return null;
        
        // Parse SDP body
        var sdp = SdpParser.Parse(response.Body);
        if (sdp == null)
            return null;
        
        var descriptor = new RtspSessionDescriptor
        {
            SdpSession = sdp,
            BaseUri = uri
        };
        
        // Extract duration from a=range attribute
        foreach (var attr in sdp.Attributes)
        {
            if (attr.Name.Equals("range", StringComparison.OrdinalIgnoreCase) && attr.Value != null)
            {
                var range = RtspRange.Parse(attr.Value);
                descriptor.Duration = range.End;
            }
        }
        
        // Build media tracks
        foreach (var media in sdp.MediaDescriptions)
        {
            var payloadMappings = media.GetRtpPayloadMappings();
            if (payloadMappings.Count == 0)
                continue;
            
            // Get control URL
            string controlUrl = uri.AbsoluteUri;
            foreach (var attr in media.Attributes)
            {
                if (attr.Name.Equals("control", StringComparison.OrdinalIgnoreCase) && attr.Value != null)
                {
                    controlUrl = ResolveControlUrl(uri, attr.Value);
                    break;
                }
            }
            
            foreach (var mapping in payloadMappings)
            {
                descriptor.MediaTracks.Add(new RtspMediaTrack
                {
                    ControlUrl = controlUrl,
                    MediaType = media.MediaType,
                    PayloadType = mapping.PayloadType,
                    PayloadMapping = mapping
                });
            }
        }
        
        return descriptor;
    }
    
    /// <summary>
    /// Sends SETUP request to configure a media track.
    /// </summary>
    public async Task<RtspResponse> SetupAsync(
        string controlUrl, 
        RtspTransport transport,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Setup,
            Uri = new Uri(controlUrl),
            SessionId = _sessionId
        };
        request.Headers["Transport"] = transport.Serialize();
        request.Headers["User-Agent"] = UserAgent;
        
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (response.IsSuccess)
        {
            // Extract session ID
            if (response.Headers.TryGetValue("Session", out var sessionHeader))
            {
                ParseSessionHeader(sessionHeader);
            }
            
            _state = RtspSessionState.Ready;
        }
        
        return response;
    }
    
    /// <summary>
    /// Sends SETUP request for interleaved TCP transport.
    /// </summary>
    public async Task<(RtspResponse Response, RtspTransport? ServerTransport)> SetupInterleavedAsync(
        RtspMediaTrack track,
        int rtpChannel,
        int rtcpChannel,
        CancellationToken cancellationToken = default)
    {
        var transport = new RtspTransport
        {
            Protocol = "RTP",
            Profile = "AVP",
            LowerTransport = "TCP",
            IsMulticast = false,
            InterleavedChannelStart = rtpChannel,
            InterleavedChannelEnd = rtcpChannel
        };
        
        var response = await SetupAsync(track.ControlUrl, transport, cancellationToken).ConfigureAwait(false);
        
        RtspTransport? serverTransport = null;
        if (response.IsSuccess && response.Headers.TryGetValue("Transport", out var transportHeader))
        {
            serverTransport = RtspTransport.Parse(transportHeader);
            track.Transport = serverTransport;
            _setupTracks.Add(track);
        }
        
        return (response, serverTransport);
    }
    
    /// <summary>
    /// Sends SETUP request for UDP transport.
    /// </summary>
    public async Task<(RtspResponse Response, RtspTransport? ServerTransport)> SetupUdpAsync(
        RtspMediaTrack track,
        int clientRtpPort,
        int clientRtcpPort,
        CancellationToken cancellationToken = default)
    {
        var transport = new RtspTransport
        {
            Protocol = "RTP",
            Profile = "AVP",
            LowerTransport = "UDP",
            IsMulticast = false,
            ClientPortStart = clientRtpPort,
            ClientPortEnd = clientRtcpPort
        };
        
        var response = await SetupAsync(track.ControlUrl, transport, cancellationToken).ConfigureAwait(false);
        
        RtspTransport? serverTransport = null;
        if (response.IsSuccess && response.Headers.TryGetValue("Transport", out var transportHeader))
        {
            serverTransport = RtspTransport.Parse(transportHeader);
            track.Transport = serverTransport;
            _setupTracks.Add(track);
        }
        
        return (response, serverTransport);
    }
    
    /// <summary>
    /// Sends PLAY request to start streaming.
    /// </summary>
    public async Task<RtspResponse> PlayAsync(
        Uri uri, 
        RtspRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        if (_state != RtspSessionState.Ready && _state != RtspSessionState.Playing)
            throw new InvalidOperationException($"Cannot PLAY in state {_state}");
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Play,
            Uri = uri,
            SessionId = _sessionId
        };
        request.Headers["User-Agent"] = UserAgent;
        
        if (range.HasValue)
        {
            request.Headers["Range"] = range.Value.Serialize();
        }
        
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (response.IsSuccess)
        {
            _state = RtspSessionState.Playing;
            
            // Parse RTP-Info header
            if (response.Headers.TryGetValue("RTP-Info", out var rtpInfoHeader))
            {
                var rtpInfos = RtpInfo.ParseAll(rtpInfoHeader);
                foreach (var info in rtpInfos)
                {
                    var track = _setupTracks.Find(t => t.ControlUrl.Equals(info.Url, StringComparison.OrdinalIgnoreCase));
                    if (track != null)
                    {
                        track.RtpInfo = info;
                    }
                }
            }
        }
        
        return response;
    }
    
    /// <summary>
    /// Sends PAUSE request to pause streaming.
    /// </summary>
    public async Task<RtspResponse> PauseAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        if (_state != RtspSessionState.Playing)
            throw new InvalidOperationException($"Cannot PAUSE in state {_state}");
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Pause,
            Uri = uri,
            SessionId = _sessionId
        };
        request.Headers["User-Agent"] = UserAgent;
        
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (response.IsSuccess)
        {
            _state = RtspSessionState.Ready;
        }
        
        return response;
    }
    
    /// <summary>
    /// Sends TEARDOWN request to end the session.
    /// </summary>
    public async Task<RtspResponse> TeardownAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var request = new RtspRequest
        {
            Method = RtspMethod.Teardown,
            Uri = uri,
            SessionId = _sessionId
        };
        request.Headers["User-Agent"] = UserAgent;
        
        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        
        _state = RtspSessionState.Init;
        _sessionId = null;
        _sessionTimeout = null;
        _setupTracks.Clear();
        
        return response;
    }
    
    /// <summary>
    /// Sends GET_PARAMETER request (can be used as keepalive).
    /// </summary>
    public async Task<RtspResponse> GetParameterAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var request = new RtspRequest
        {
            Method = RtspMethod.GetParameter,
            Uri = uri,
            SessionId = _sessionId
        };
        request.Headers["User-Agent"] = UserAgent;
        
        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Reads interleaved data from TCP connection (for RTP/TCP mode).
    /// Call this in a loop during playback to receive RTP packets.
    /// </summary>
    public async Task<(int Channel, Memory<byte> Data)?> ReadInterleavedDataAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        
        var buffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            // Read 4-byte interleaved header: $ + channel (1 byte) + length (2 bytes big-endian)
            var bytesRead = await ReadExactAsync(_networkStream!, buffer.AsMemory(0, 4), cancellationToken)
                .ConfigureAwait(false);
            
            if (bytesRead < 4)
                return null;
            
            if (buffer[0] != '$')
            {
                // Not interleaved data - might be an RTSP response
                // Put it back by reading as response
                throw new InvalidOperationException("Expected interleaved data but received RTSP message");
            }
            
            int channel = buffer[1];
            int length = (buffer[2] << 8) | buffer[3];
            
            var data = new byte[length];
            bytesRead = await ReadExactAsync(_networkStream!, data.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            
            if (bytesRead < length)
                return null;
            
            InterleavedDataReceived?.Invoke(channel, data);
            return (channel, data);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private async Task<RtspResponse> SendRequestAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        request.CSeq = Interlocked.Increment(ref _cseq);
        
        var requestText = request.Serialize();
        var requestBytes = Encoding.UTF8.GetBytes(requestText);
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        linkedCts.CancelAfter(ResponseTimeoutMs);
        
        await _networkStream!.WriteAsync(requestBytes, linkedCts.Token).ConfigureAwait(false);
        
        if (request.Body != null && request.Body.Length > 0)
        {
            await _networkStream.WriteAsync(request.Body, linkedCts.Token).ConfigureAwait(false);
        }
        
        await _networkStream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
        
        return await ReadResponseAsync(linkedCts.Token).ConfigureAwait(false);
    }
    
    private async Task<RtspResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        try
        {
            var responseBuilder = new StringBuilder();
            var totalRead = 0;
            var headerEndIndex = -1;
            
            // Read until we have the full headers
            while (headerEndIndex < 0)
            {
                var bytesRead = await _networkStream!.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead), 
                    cancellationToken).ConfigureAwait(false);
                
                if (bytesRead == 0)
                    throw new EndOfStreamException("Connection closed by server");
                
                totalRead += bytesRead;
                var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
                headerEndIndex = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                
                if (headerEndIndex < 0 && totalRead >= buffer.Length)
                {
                    // Resize buffer
                    var newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = newBuffer;
                }
            }
            
            var headerText = Encoding.UTF8.GetString(buffer, 0, headerEndIndex);
            var response = ParseResponse(headerText);
            
            // Calculate how much body we already read
            var headerLength = headerEndIndex + 4; // Include \r\n\r\n
            var bodyBytesRead = totalRead - headerLength;
            
            // Read remaining body if Content-Length specified
            if (response.Headers.TryGetValue("Content-Length", out var contentLengthStr) &&
                int.TryParse(contentLengthStr, out var contentLength) &&
                contentLength > 0)
            {
                var body = new byte[contentLength];
                
                // Copy what we already have
                if (bodyBytesRead > 0)
                {
                    Buffer.BlockCopy(buffer, headerLength, body, 0, Math.Min(bodyBytesRead, contentLength));
                }
                
                // Read the rest
                if (bodyBytesRead < contentLength)
                {
                    await ReadExactAsync(
                        _networkStream!,
                        body.AsMemory(bodyBytesRead, contentLength - bodyBytesRead),
                        cancellationToken).ConfigureAwait(false);
                }
                
                response.Body = body;
            }
            
            return response;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private static RtspResponse ParseResponse(string headerText)
    {
        var response = new RtspResponse();
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        
        if (lines.Length == 0)
            throw new FormatException("Empty RTSP response");
        
        // Parse status line: RTSP/1.0 200 OK
        var statusLine = lines[0];
        var firstSpace = statusLine.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid status line: {statusLine}");
        
        response.Version = statusLine[..firstSpace];
        
        var secondSpace = statusLine.IndexOf(' ', firstSpace + 1);
        if (secondSpace < 0)
            throw new FormatException($"Invalid status line: {statusLine}");
        
        if (!int.TryParse(statusLine.AsSpan(firstSpace + 1, secondSpace - firstSpace - 1), out var statusCode))
            throw new FormatException($"Invalid status code in: {statusLine}");
        
        response.StatusCode = (RtspStatusCode)statusCode;
        response.ReasonPhrase = statusLine[(secondSpace + 1)..];
        
        // Parse headers
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;
            
            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0) continue;
            
            var name = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            
            response.Headers[name] = value;
            
            if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var cseq))
            {
                response.CSeq = cseq;
            }
        }
        
        return response;
    }
    
    private void ParseSessionHeader(string sessionHeader)
    {
        // Format: session-id[;timeout=seconds]
        var semicolonIndex = sessionHeader.IndexOf(';');
        if (semicolonIndex < 0)
        {
            _sessionId = sessionHeader.Trim();
        }
        else
        {
            _sessionId = sessionHeader[..semicolonIndex].Trim();
            var rest = sessionHeader[(semicolonIndex + 1)..];
            
            if (rest.StartsWith("timeout=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(rest.AsSpan(8), out var timeout))
            {
                _sessionTimeout = timeout;
            }
        }
    }
    
    private static string ResolveControlUrl(Uri baseUri, string control)
    {
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            control.StartsWith("rtspu://", StringComparison.OrdinalIgnoreCase))
        {
            return control;
        }
        
        if (control == "*")
        {
            return baseUri.AbsoluteUri;
        }
        
        // Relative URL
        var baseUrl = baseUri.AbsoluteUri;
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        
        return baseUrl + control;
    }
    
    private static async Task<int> ReadExactAsync(
        NetworkStream stream, 
        Memory<byte> buffer, 
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }
        return totalRead;
    }
    
    private void EnsureConnected()
    {
        if (_tcpClient == null || !_tcpClient.Connected)
            throw new InvalidOperationException("Not connected to RTSP server");
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        
        if (_networkStream != null)
        {
            await _networkStream.DisposeAsync().ConfigureAwait(false);
        }
        
        _tcpClient?.Dispose();
    }
}

/// <summary>
/// Parses RTSP response from raw bytes.
/// </summary>
public static class RtspResponseParser
{
    /// <summary>
    /// Tries to parse a complete RTSP response from a buffer.
    /// Returns the number of bytes consumed, or 0 if incomplete.
    /// </summary>
    public static int TryParse(ReadOnlySpan<byte> buffer, out RtspResponse? response)
    {
        response = null;
        
        // Find end of headers
        var text = Encoding.UTF8.GetString(buffer);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return 0;
        
        var headerText = text[..headerEnd];
        response = ParseHeaders(headerText);
        
        var headerLength = headerEnd + 4;
        
        // Check for body
        if (response.Headers.TryGetValue("Content-Length", out var clStr) &&
            int.TryParse(clStr, out var contentLength) &&
            contentLength > 0)
        {
            var totalNeeded = headerLength + contentLength;
            if (buffer.Length < totalNeeded)
            {
                response = null;
                return 0;
            }
            
            response.Body = buffer.Slice(headerLength, contentLength).ToArray();
            return totalNeeded;
        }
        
        return headerLength;
    }
    
    private static RtspResponse ParseHeaders(string headerText)
    {
        var response = new RtspResponse();
        var lines = headerText.Split("\r\n");
        
        if (lines.Length == 0)
            throw new FormatException("Empty response");
        
        // Parse status line
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 3)
            throw new FormatException($"Invalid status line: {lines[0]}");
        
        response.Version = parts[0];
        response.StatusCode = (RtspStatusCode)int.Parse(parts[1]);
        response.ReasonPhrase = parts[2];
        
        // Parse headers
        for (int i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                var name = lines[i][..colon].Trim();
                var value = lines[i][(colon + 1)..].Trim();
                response.Headers[name] = value;
            }
        }
        
        return response;
    }
}

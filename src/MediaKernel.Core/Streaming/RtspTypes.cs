// RTSP (Real Time Streaming Protocol) Type Definitions
// RFC 2326 / RFC 7826 compliant structures

using System.Net;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// RTSP request methods (RFC 2326 Section 10).
/// </summary>
public enum RtspMethod
{
    Options,
    Describe,
    Announce,
    Setup,
    Play,
    Pause,
    Teardown,
    GetParameter,
    SetParameter,
    Redirect,
    Record
}

/// <summary>
/// RTSP transport protocol modes.
/// </summary>
public enum RtspTransportMode
{
    /// <summary>RTP over UDP (default).</summary>
    RtpUdp,
    
    /// <summary>RTP interleaved over RTSP TCP connection.</summary>
    RtpTcpInterleaved,
    
    /// <summary>RTP over UDP multicast.</summary>
    RtpUdpMulticast
}

/// <summary>
/// RTSP session state machine states (RFC 2326 Appendix A).
/// </summary>
public enum RtspSessionState
{
    Init,
    Ready,
    Playing,
    Recording
}

/// <summary>
/// RTSP response status codes (RFC 2326 Section 11).
/// </summary>
public enum RtspStatusCode
{
    Continue = 100,
    
    // Success 2xx
    Ok = 200,
    Created = 201,
    LowOnStorageSpace = 250,
    
    // Redirection 3xx
    MultipleChoices = 300,
    MovedPermanently = 301,
    MovedTemporarily = 302,
    SeeOther = 303,
    NotModified = 304,
    UseProxy = 305,
    
    // Client Error 4xx
    BadRequest = 400,
    Unauthorized = 401,
    PaymentRequired = 402,
    Forbidden = 403,
    NotFound = 404,
    MethodNotAllowed = 405,
    NotAcceptable = 406,
    ProxyAuthenticationRequired = 407,
    RequestTimeout = 408,
    Gone = 410,
    LengthRequired = 411,
    PreconditionFailed = 412,
    RequestEntityTooLarge = 413,
    RequestUriTooLarge = 414,
    UnsupportedMediaType = 415,
    ParameterNotUnderstood = 451,
    ConferenceNotFound = 452,
    NotEnoughBandwidth = 453,
    SessionNotFound = 454,
    MethodNotValidInThisState = 455,
    HeaderFieldNotValidForResource = 456,
    InvalidRange = 457,
    ParameterIsReadOnly = 458,
    AggregateOperationNotAllowed = 459,
    OnlyAggregateOperationAllowed = 460,
    UnsupportedTransport = 461,
    DestinationUnreachable = 462,
    
    // Server Error 5xx
    InternalServerError = 500,
    NotImplemented = 501,
    BadGateway = 502,
    ServiceUnavailable = 503,
    GatewayTimeout = 504,
    RtspVersionNotSupported = 505,
    OptionNotSupported = 551
}

/// <summary>
/// RTSP request message.
/// </summary>
public sealed class RtspRequest
{
    public required RtspMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public string Version { get; init; } = "RTSP/1.0";
    public int CSeq { get; set; }
    public string? SessionId { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }
    
    /// <summary>
    /// Serializes the request to wire format.
    /// </summary>
    public string Serialize()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Method.ToString().ToUpperInvariant());
        sb.Append(' ');
        sb.Append(Uri.AbsoluteUri);
        sb.Append(' ');
        sb.Append(Version);
        sb.Append("\r\n");
        
        sb.Append("CSeq: ");
        sb.Append(CSeq);
        sb.Append("\r\n");
        
        if (!string.IsNullOrEmpty(SessionId))
        {
            sb.Append("Session: ");
            sb.Append(SessionId);
            sb.Append("\r\n");
        }
        
        foreach (var header in Headers)
        {
            sb.Append(header.Key);
            sb.Append(": ");
            sb.Append(header.Value);
            sb.Append("\r\n");
        }
        
        if (Body != null && Body.Length > 0)
        {
            sb.Append("Content-Length: ");
            sb.Append(Body.Length);
            sb.Append("\r\n");
        }
        
        sb.Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>
/// RTSP response message.
/// </summary>
public sealed class RtspResponse
{
    public string Version { get; set; } = "RTSP/1.0";
    public RtspStatusCode StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public int CSeq { get; set; }
    public string? SessionId { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }
    
    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;
    
    /// <summary>
    /// Gets the Content-Type header value.
    /// </summary>
    public string? ContentType => Headers.GetValueOrDefault("Content-Type");
    
    /// <summary>
    /// Gets the body as a UTF-8 string.
    /// </summary>
    public string? BodyAsString => Body != null ? System.Text.Encoding.UTF8.GetString(Body) : null;
}

/// <summary>
/// Transport header parameters (RFC 2326 Section 12.39).
/// </summary>
public sealed class RtspTransport
{
    public string Protocol { get; set; } = "RTP";
    public string Profile { get; set; } = "AVP";
    public string LowerTransport { get; set; } = "UDP";
    public bool IsMulticast { get; set; }
    public string? Destination { get; set; }
    public int? Ttl { get; set; }
    public int? ClientPortStart { get; set; }
    public int? ClientPortEnd { get; set; }
    public int? ServerPortStart { get; set; }
    public int? ServerPortEnd { get; set; }
    public int? InterleavedChannelStart { get; set; }
    public int? InterleavedChannelEnd { get; set; }
    public string? Mode { get; set; }
    public uint? Ssrc { get; set; }
    
    /// <summary>
    /// Parses a Transport header value.
    /// </summary>
    public static RtspTransport Parse(ReadOnlySpan<char> value)
    {
        var transport = new RtspTransport();
        
        var parts = value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return transport;
        
        // Parse transport specifier: RTP/AVP[/UDP|/TCP]
        var spec = parts[0].Split('/');
        if (spec.Length >= 1) transport.Protocol = spec[0];
        if (spec.Length >= 2) transport.Profile = spec[1];
        if (spec.Length >= 3) transport.LowerTransport = spec[2];
        
        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            
            if (part.Equals("unicast", StringComparison.OrdinalIgnoreCase))
            {
                transport.IsMulticast = false;
            }
            else if (part.Equals("multicast", StringComparison.OrdinalIgnoreCase))
            {
                transport.IsMulticast = true;
            }
            else if (part.StartsWith("destination=", StringComparison.OrdinalIgnoreCase))
            {
                transport.Destination = part[12..];
            }
            else if (part.StartsWith("ttl=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(part.AsSpan(4), out var ttl))
            {
                transport.Ttl = ttl;
            }
            else if (part.StartsWith("client_port=", StringComparison.OrdinalIgnoreCase))
            {
                ParsePortRange(part.AsSpan(12), out var clientStart, out var clientEnd);
                transport.ClientPortStart = clientStart;
                transport.ClientPortEnd = clientEnd;
            }
            else if (part.StartsWith("server_port=", StringComparison.OrdinalIgnoreCase))
            {
                ParsePortRange(part.AsSpan(12), out var serverStart, out var serverEnd);
                transport.ServerPortStart = serverStart;
                transport.ServerPortEnd = serverEnd;
            }
            else if (part.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase))
            {
                ParsePortRange(part.AsSpan(12), out var interleavedStart, out var interleavedEnd);
                transport.InterleavedChannelStart = interleavedStart;
                transport.InterleavedChannelEnd = interleavedEnd;
            }
            else if (part.StartsWith("mode=", StringComparison.OrdinalIgnoreCase))
            {
                transport.Mode = part[5..].Trim('"');
            }
            else if (part.StartsWith("ssrc=", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(part.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out var ssrc))
                {
                    transport.Ssrc = ssrc;
                }
            }
        }
        
        return transport;
    }
    
    private static void ParsePortRange(ReadOnlySpan<char> value, out int? start, out int? end)
    {
        start = null;
        end = null;
        
        var dashIndex = value.IndexOf('-');
        if (dashIndex < 0)
        {
            if (int.TryParse(value, out var port))
            {
                start = port;
                end = port;
            }
        }
        else
        {
            if (int.TryParse(value[..dashIndex], out var s))
                start = s;
            if (int.TryParse(value[(dashIndex + 1)..], out var e))
                end = e;
        }
    }
    
    /// <summary>
    /// Serializes transport parameters for request.
    /// </summary>
    public string Serialize()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Protocol);
        sb.Append('/');
        sb.Append(Profile);
        
        if (!string.Equals(LowerTransport, "UDP", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append('/');
            sb.Append(LowerTransport);
        }
        
        sb.Append(IsMulticast ? ";multicast" : ";unicast");
        
        if (Destination != null)
        {
            sb.Append(";destination=");
            sb.Append(Destination);
        }
        
        if (Ttl.HasValue)
        {
            sb.Append(";ttl=");
            sb.Append(Ttl.Value);
        }
        
        if (ClientPortStart.HasValue)
        {
            sb.Append(";client_port=");
            sb.Append(ClientPortStart.Value);
            if (ClientPortEnd.HasValue && ClientPortEnd.Value != ClientPortStart.Value)
            {
                sb.Append('-');
                sb.Append(ClientPortEnd.Value);
            }
        }
        
        if (InterleavedChannelStart.HasValue)
        {
            sb.Append(";interleaved=");
            sb.Append(InterleavedChannelStart.Value);
            if (InterleavedChannelEnd.HasValue && InterleavedChannelEnd.Value != InterleavedChannelStart.Value)
            {
                sb.Append('-');
                sb.Append(InterleavedChannelEnd.Value);
            }
        }
        
        if (Mode != null)
        {
            sb.Append(";mode=\"");
            sb.Append(Mode);
            sb.Append('"');
        }
        
        return sb.ToString();
    }
}

/// <summary>
/// RTP-Info header data (RFC 2326 Section 12.33).
/// </summary>
public readonly struct RtpInfo
{
    public required string Url { get; init; }
    public uint? Seq { get; init; }
    public uint? RtpTime { get; init; }
    
    /// <summary>
    /// Parses RTP-Info header value.
    /// </summary>
    public static List<RtpInfo> ParseAll(ReadOnlySpan<char> value)
    {
        var results = new List<RtpInfo>();
        var entries = value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var entry in entries)
        {
            string? url = null;
            uint? seq = null;
            uint? rtpTime = null;
            
            var parts = entry.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                {
                    url = trimmed[4..];
                }
                else if (trimmed.StartsWith("seq=", StringComparison.OrdinalIgnoreCase) &&
                         uint.TryParse(trimmed.AsSpan(4), out var s))
                {
                    seq = s;
                }
                else if (trimmed.StartsWith("rtptime=", StringComparison.OrdinalIgnoreCase) &&
                         uint.TryParse(trimmed.AsSpan(8), out var t))
                {
                    rtpTime = t;
                }
            }
            
            if (url != null)
            {
                results.Add(new RtpInfo { Url = url, Seq = seq, RtpTime = rtpTime });
            }
        }
        
        return results;
    }
}

/// <summary>
/// Range header value (RFC 2326 Section 12.29).
/// </summary>
public readonly struct RtspRange
{
    public TimeSpan? Start { get; init; }
    public TimeSpan? End { get; init; }
    public bool IsLive { get; init; }
    
    /// <summary>
    /// Creates a range from start to end of media.
    /// </summary>
    public static RtspRange FromBeginning() => new() { Start = TimeSpan.Zero, End = null };
    
    /// <summary>
    /// Creates a range from a specific time.
    /// </summary>
    public static RtspRange FromTime(TimeSpan start, TimeSpan? end = null) => new() { Start = start, End = end };
    
    /// <summary>
    /// Parses an NPT (Normal Play Time) range.
    /// </summary>
    public static RtspRange Parse(ReadOnlySpan<char> value)
    {
        // Format: npt=start-end or npt=now-
        value = value.Trim();
        
        if (value.StartsWith("npt=", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }
        
        if (value.Equals("now-", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspRange { IsLive = true };
        }
        
        var dashIndex = value.IndexOf('-');
        if (dashIndex < 0)
        {
            return new RtspRange { Start = ParseNptTime(value) };
        }
        
        var startStr = value[..dashIndex];
        var endStr = value[(dashIndex + 1)..];
        
        return new RtspRange
        {
            Start = ParseNptTime(startStr),
            End = endStr.IsEmpty ? null : ParseNptTime(endStr)
        };
    }
    
    private static TimeSpan? ParseNptTime(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.IsEmpty) return null;
        
        // Could be seconds (123.456) or time (1:23:45.678)
        var colonIndex = value.IndexOf(':');
        if (colonIndex < 0)
        {
            // Simple seconds format
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        else
        {
            // HH:MM:SS.mmm format
            if (TimeSpan.TryParse(value, out var time))
            {
                return time;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Serializes to NPT format.
    /// </summary>
    public string Serialize()
    {
        if (IsLive) return "npt=now-";
        
        var sb = new System.Text.StringBuilder("npt=");
        
        if (Start.HasValue)
        {
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F3}", Start.Value.TotalSeconds);
        }
        
        sb.Append('-');
        
        if (End.HasValue)
        {
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F3}", End.Value.TotalSeconds);
        }
        
        return sb.ToString();
    }
}

/// <summary>
/// Media track from RTSP DESCRIBE response.
/// </summary>
public sealed class RtspMediaTrack
{
    public required string ControlUrl { get; init; }
    public required string MediaType { get; init; }
    public required int PayloadType { get; init; }
    public required RtpPayloadMapping PayloadMapping { get; init; }
    public RtspTransport? Transport { get; set; }
    public RtpInfo? RtpInfo { get; set; }
}

/// <summary>
/// RTSP session descriptor from DESCRIBE response.
/// </summary>
public sealed class RtspSessionDescriptor
{
    public required SdpSession SdpSession { get; init; }
    public required Uri BaseUri { get; init; }
    public List<RtspMediaTrack> MediaTracks { get; } = [];
    public TimeSpan? Duration { get; set; }
    
    /// <summary>
    /// Gets the aggregate control URL if available.
    /// </summary>
    public string? AggregateControlUrl
    {
        get
        {
            foreach (var attr in SdpSession.Attributes)
            {
                if (attr.Name.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    return attr.Value;
                }
            }
            return null;
        }
    }
}

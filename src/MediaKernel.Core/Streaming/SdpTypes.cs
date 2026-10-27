// SDP (Session Description Protocol) Types
// Based on RFC 4566: SDP - Session Description Protocol

using System.Net;
using System.Net.Sockets;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parsed SDP session descriptor (RFC 4566).
/// </summary>
public sealed class SdpSession
{
    /// <summary>Protocol version (always 0).</summary>
    public int Version { get; init; }
    
    /// <summary>Origin/session owner information.</summary>
    public SdpOrigin Origin { get; init; } = SdpOrigin.Empty;
    
    /// <summary>Session name (s= line).</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>Session information (i= line, optional).</summary>
    public string? Information { get; init; }
    
    /// <summary>URI of session description (u= line, optional).</summary>
    public string? Uri { get; init; }
    
    /// <summary>Email addresses (e= lines).</summary>
    public IReadOnlyList<string> Emails { get; init; } = [];
    
    /// <summary>Phone numbers (p= lines).</summary>
    public IReadOnlyList<string> Phones { get; init; } = [];
    
    /// <summary>Session-level connection information (c= line, optional).</summary>
    public SdpConnection? Connection { get; init; }
    
    /// <summary>Session-level bandwidth specifications (b= lines).</summary>
    public IReadOnlyList<SdpBandwidth> Bandwidths { get; init; } = [];
    
    /// <summary>Time descriptions (t= lines).</summary>
    public IReadOnlyList<SdpTime> Times { get; init; } = [];
    
    /// <summary>Session-level attributes (a= lines).</summary>
    public IReadOnlyList<SdpAttribute> Attributes { get; init; } = [];
    
    /// <summary>Media descriptions (m= lines).</summary>
    public IReadOnlyList<SdpMedia> MediaDescriptions { get; init; } = [];
    
    /// <summary>Whether this session descriptor is valid.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>
    /// Gets a session attribute value by name.
    /// </summary>
    public string? GetAttribute(string name)
    {
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return attr.Value;
        }
        return null;
    }
    
    /// <summary>
    /// Checks if a session attribute is present.
    /// </summary>
    public bool HasAttribute(string name)
    {
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Origin field (o= line): session originator.
/// Format: username sess-id sess-version nettype addrtype unicast-address
/// </summary>
public readonly struct SdpOrigin
{
    /// <summary>User name (or "-" if not available).</summary>
    public string Username { get; init; }
    
    /// <summary>Session ID (NTP timestamp or random).</summary>
    public string SessionId { get; init; }
    
    /// <summary>Session version (incremented for updates).</summary>
    public string SessionVersion { get; init; }
    
    /// <summary>Network type (usually "IN" for Internet).</summary>
    public string NetworkType { get; init; }
    
    /// <summary>Address type ("IP4" or "IP6").</summary>
    public string AddressType { get; init; }
    
    /// <summary>Address of the machine.</summary>
    public string Address { get; init; }
    
    public static SdpOrigin Empty => new()
    {
        Username = "-",
        SessionId = "0",
        SessionVersion = "0",
        NetworkType = "IN",
        AddressType = "IP4",
        Address = "0.0.0.0"
    };
}

/// <summary>
/// Connection information (c= line).
/// Format: nettype addrtype connection-address
/// </summary>
public readonly struct SdpConnection
{
    /// <summary>Network type (usually "IN" for Internet).</summary>
    public string NetworkType { get; init; }
    
    /// <summary>Address type ("IP4" or "IP6").</summary>
    public string AddressType { get; init; }
    
    /// <summary>Connection address (IP or hostname).</summary>
    public string Address { get; init; }
    
    /// <summary>TTL for multicast (0 = not specified).</summary>
    public int Ttl { get; init; }
    
    /// <summary>Number of addresses for multicast (1 = single).</summary>
    public int AddressCount { get; init; }
    
    /// <summary>
    /// Gets the address family.
    /// </summary>
    public AddressFamily AddressFamily => AddressType switch
    {
        "IP4" => AddressFamily.InterNetwork,
        "IP6" => AddressFamily.InterNetworkV6,
        _ => AddressFamily.Unknown
    };
}

/// <summary>
/// Bandwidth specification (b= line).
/// Format: bwtype:bandwidth
/// </summary>
public readonly struct SdpBandwidth
{
    /// <summary>Bandwidth type (CT = Conference Total, AS = Application-Specific).</summary>
    public string Type { get; init; }
    
    /// <summary>Bandwidth in kilobits per second.</summary>
    public int Kbps { get; init; }
}

/// <summary>
/// Time description (t= line).
/// Format: start-time stop-time
/// </summary>
public readonly struct SdpTime
{
    /// <summary>Start time (NTP seconds since 1900, 0 = permanent).</summary>
    public long StartTime { get; init; }
    
    /// <summary>Stop time (NTP seconds since 1900, 0 = unbounded).</summary>
    public long StopTime { get; init; }
    
    /// <summary>Repeat intervals (r= lines).</summary>
    public IReadOnlyList<SdpRepeat> Repeats { get; init; }
}

/// <summary>
/// Repeat time (r= line).
/// Format: repeat-interval active-duration offsets
/// </summary>
public readonly struct SdpRepeat
{
    /// <summary>Repeat interval in seconds.</summary>
    public long RepeatInterval { get; init; }
    
    /// <summary>Active duration in seconds.</summary>
    public long ActiveDuration { get; init; }
    
    /// <summary>Offsets from start time in seconds.</summary>
    public IReadOnlyList<long> Offsets { get; init; }
}

/// <summary>
/// Session or media attribute (a= line).
/// Format: attribute or attribute:value
/// </summary>
public readonly struct SdpAttribute
{
    /// <summary>Attribute name.</summary>
    public string Name { get; init; }
    
    /// <summary>Attribute value (null for flag attributes).</summary>
    public string? Value { get; init; }
    
    /// <summary>Whether this is a flag attribute (no value).</summary>
    public bool IsFlag => Value is null;
}

/// <summary>
/// Media description (m= line with associated fields).
/// Format: media port proto fmt ...
/// </summary>
public sealed class SdpMedia
{
    /// <summary>Media type (audio, video, text, application, message).</summary>
    public string MediaType { get; init; } = string.Empty;
    
    /// <summary>Transport port.</summary>
    public int Port { get; init; }
    
    /// <summary>Number of ports (for multicast).</summary>
    public int PortCount { get; init; } = 1;
    
    /// <summary>Transport protocol (RTP/AVP, RTP/SAVP, UDP, etc.).</summary>
    public string Protocol { get; init; } = string.Empty;
    
    /// <summary>Format list (payload types for RTP, or format strings).</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];
    
    /// <summary>Media title/information (i= line, optional).</summary>
    public string? Information { get; init; }
    
    /// <summary>Media-level connection information (c= lines).</summary>
    public IReadOnlyList<SdpConnection> Connections { get; init; } = [];
    
    /// <summary>Media-level bandwidth specifications (b= lines).</summary>
    public IReadOnlyList<SdpBandwidth> Bandwidths { get; init; } = [];
    
    /// <summary>Media-level attributes (a= lines).</summary>
    public IReadOnlyList<SdpAttribute> Attributes { get; init; } = [];
    
    /// <summary>
    /// Gets a media attribute value by name.
    /// </summary>
    public string? GetAttribute(string name)
    {
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return attr.Value;
        }
        return null;
    }
    
    /// <summary>
    /// Checks if a media attribute is present.
    /// </summary>
    public bool HasAttribute(string name)
    {
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    
    /// <summary>
    /// Gets all rtpmap attributes parsed as RTP payload mappings.
    /// </summary>
    public IReadOnlyList<RtpPayloadMapping> GetRtpPayloadMappings()
    {
        var mappings = new List<RtpPayloadMapping>();
        
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals("rtpmap", StringComparison.OrdinalIgnoreCase) && attr.Value != null)
            {
                var mapping = ParseRtpMap(attr.Value);
                if (mapping != null)
                {
                    // Look for corresponding fmtp
                    var fmtp = GetFmtp(mapping.PayloadType);
                    if (fmtp != null)
                    {
                        mapping = new RtpPayloadMapping
                        {
                            PayloadType = mapping.PayloadType,
                            EncodingName = mapping.EncodingName,
                            ClockRate = mapping.ClockRate,
                            Channels = mapping.Channels,
                            FormatParameters = fmtp
                        };
                    }
                    mappings.Add(mapping);
                }
            }
        }
        
        return mappings;
    }
    
    private string? GetFmtp(int payloadType)
    {
        string prefix = $"{payloadType} ";
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals("fmtp", StringComparison.OrdinalIgnoreCase) && 
                attr.Value?.StartsWith(prefix) == true)
            {
                return attr.Value[prefix.Length..];
            }
        }
        return null;
    }
    
    private static RtpPayloadMapping? ParseRtpMap(string value)
    {
        // Format: <payload type> <encoding name>/<clock rate>[/<encoding parameters>]
        int spaceIdx = value.IndexOf(' ');
        if (spaceIdx < 0)
            return null;
        
        if (!int.TryParse(value.AsSpan(0, spaceIdx), out int payloadType))
            return null;
        
        var remainder = value.AsSpan(spaceIdx + 1);
        int slashIdx = remainder.IndexOf('/');
        if (slashIdx < 0)
            return null;
        
        string encodingName = remainder[..slashIdx].ToString();
        remainder = remainder[(slashIdx + 1)..];
        
        int clockRate;
        int channels = 1;
        
        int secondSlash = remainder.IndexOf('/');
        if (secondSlash >= 0)
        {
            if (!int.TryParse(remainder[..secondSlash], out clockRate))
                return null;
            if (!int.TryParse(remainder[(secondSlash + 1)..], out channels))
                channels = 1;
        }
        else
        {
            if (!int.TryParse(remainder, out clockRate))
                return null;
        }
        
        return new RtpPayloadMapping
        {
            PayloadType = payloadType,
            EncodingName = encodingName,
            ClockRate = clockRate,
            Channels = channels
        };
    }
    
    /// <summary>
    /// Gets whether this media is RTP-based.
    /// </summary>
    public bool IsRtp => Protocol.StartsWith("RTP/", StringComparison.OrdinalIgnoreCase);
    
    /// <summary>
    /// Gets whether this media uses SRTP (secure).
    /// </summary>
    public bool IsSecure => Protocol.Contains("SAVP", StringComparison.OrdinalIgnoreCase);
    
    /// <summary>
    /// Gets the direction attribute (sendrecv, sendonly, recvonly, inactive).
    /// </summary>
    public string Direction
    {
        get
        {
            if (HasAttribute("sendrecv")) return "sendrecv";
            if (HasAttribute("sendonly")) return "sendonly";
            if (HasAttribute("recvonly")) return "recvonly";
            if (HasAttribute("inactive")) return "inactive";
            return "sendrecv"; // default
        }
    }
}

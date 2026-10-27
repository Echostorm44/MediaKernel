// SDP (Session Description Protocol) Parser
// Based on RFC 4566: SDP - Session Description Protocol

using System.Text;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parser for SDP session descriptors (RFC 4566).
/// </summary>
public static class SdpParser
{
    /// <summary>
    /// Parses an SDP session descriptor from a string.
    /// </summary>
    /// <param name="sdpText">The SDP text to parse.</param>
    /// <returns>Parsed SDP session.</returns>
    public static SdpSession Parse(string sdpText)
    {
        return Parse(sdpText.AsSpan());
    }
    
    /// <summary>
    /// Parses an SDP session descriptor from a span.
    /// </summary>
    public static SdpSession Parse(ReadOnlySpan<char> sdpText)
    {
        var lines = new List<SdpLine>();
        
        // Split into lines and parse type=value
        int start = 0;
        for (int i = 0; i < sdpText.Length; i++)
        {
            if (sdpText[i] == '\n')
            {
                var line = sdpText[start..i];
                
                // Handle CRLF
                if (line.Length > 0 && line[^1] == '\r')
                    line = line[..^1];
                
                if (line.Length >= 2 && line[1] == '=')
                {
                    lines.Add(new SdpLine
                    {
                        Type = line[0],
                        Value = line[2..].ToString()
                    });
                }
                
                start = i + 1;
            }
        }
        
        // Handle last line without newline
        if (start < sdpText.Length)
        {
            var line = sdpText[start..];
            if (line.Length >= 2 && line[1] == '=')
            {
                lines.Add(new SdpLine
                {
                    Type = line[0],
                    Value = line[2..].ToString()
                });
            }
        }
        
        return ParseLines(lines);
    }
    
    /// <summary>
    /// Parses an SDP session descriptor from bytes (UTF-8).
    /// </summary>
    public static SdpSession Parse(ReadOnlySpan<byte> data)
    {
        return Parse(Encoding.UTF8.GetString(data));
    }
    
    private static SdpSession ParseLines(List<SdpLine> lines)
    {
        if (lines.Count == 0)
        {
            return new SdpSession { IsValid = false };
        }
        
        int index = 0;
        
        // v= (version, must be first and must be 0)
        if (!TryGetLine(lines, ref index, 'v', out string? versionStr) || versionStr != "0")
        {
            return new SdpSession { IsValid = false };
        }
        
        // o= (origin, required)
        if (!TryGetLine(lines, ref index, 'o', out string? originStr))
        {
            return new SdpSession { IsValid = false };
        }
        var origin = ParseOrigin(originStr);
        
        // s= (session name, required)
        if (!TryGetLine(lines, ref index, 's', out string? sessionName))
        {
            return new SdpSession { IsValid = false };
        }
        
        // i= (session info, optional)
        TryGetLine(lines, ref index, 'i', out string? sessionInfo);
        
        // u= (URI, optional)
        TryGetLine(lines, ref index, 'u', out string? uri);
        
        // e= (email, zero or more)
        var emails = new List<string>();
        while (TryGetLine(lines, ref index, 'e', out string? email))
        {
            emails.Add(email);
        }
        
        // p= (phone, zero or more)
        var phones = new List<string>();
        while (TryGetLine(lines, ref index, 'p', out string? phone))
        {
            phones.Add(phone);
        }
        
        // c= (connection, optional at session level)
        SdpConnection? sessionConnection = null;
        if (TryGetLine(lines, ref index, 'c', out string? connStr))
        {
            sessionConnection = ParseConnection(connStr);
        }
        
        // b= (bandwidth, zero or more)
        var sessionBandwidths = new List<SdpBandwidth>();
        while (TryGetLine(lines, ref index, 'b', out string? bwStr))
        {
            var bw = ParseBandwidth(bwStr);
            if (bw.HasValue)
                sessionBandwidths.Add(bw.Value);
        }
        
        // t= (timing, one or more)
        var times = new List<SdpTime>();
        while (TryGetLine(lines, ref index, 't', out string? timeStr))
        {
            var time = ParseTime(timeStr);
            
            // r= (repeat, zero or more per time)
            var repeats = new List<SdpRepeat>();
            while (TryGetLine(lines, ref index, 'r', out string? repeatStr))
            {
                var repeat = ParseRepeat(repeatStr);
                if (repeat.HasValue)
                    repeats.Add(repeat.Value);
            }
            
            times.Add(new SdpTime
            {
                StartTime = time.StartTime,
                StopTime = time.StopTime,
                Repeats = repeats
            });
        }
        
        // z= (time zone adjustments, optional, skip)
        while (TryGetLine(lines, ref index, 'z', out _)) { }
        
        // k= (encryption key, optional, skip)
        TryGetLine(lines, ref index, 'k', out _);
        
        // a= (session attributes, zero or more)
        var sessionAttributes = new List<SdpAttribute>();
        while (TryGetLine(lines, ref index, 'a', out string? attrStr))
        {
            sessionAttributes.Add(ParseAttribute(attrStr));
        }
        
        // m= (media descriptions, zero or more)
        var mediaDescriptions = new List<SdpMedia>();
        while (index < lines.Count && lines[index].Type == 'm')
        {
            var media = ParseMediaDescription(lines, ref index);
            if (media != null)
                mediaDescriptions.Add(media);
        }
        
        return new SdpSession
        {
            Version = 0,
            Origin = origin,
            Name = sessionName,
            Information = sessionInfo,
            Uri = uri,
            Emails = emails,
            Phones = phones,
            Connection = sessionConnection,
            Bandwidths = sessionBandwidths,
            Times = times,
            Attributes = sessionAttributes,
            MediaDescriptions = mediaDescriptions,
            IsValid = true
        };
    }
    
    private static SdpMedia? ParseMediaDescription(List<SdpLine> lines, ref int index)
    {
        if (!TryGetLine(lines, ref index, 'm', out string? mediaLine))
            return null;
        
        // Format: media port[/count] proto fmt ...
        var parts = mediaLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;
        
        string mediaType = parts[0];
        
        // Parse port and optional port count
        int port = 0;
        int portCount = 1;
        var portParts = parts[1].Split('/');
        if (portParts.Length >= 1)
            int.TryParse(portParts[0], out port);
        if (portParts.Length >= 2)
            int.TryParse(portParts[1], out portCount);
        
        string protocol = parts[2];
        
        // Remaining parts are format list
        var formats = new List<string>();
        for (int i = 3; i < parts.Length; i++)
        {
            formats.Add(parts[i]);
        }
        
        // i= (media info, optional)
        TryGetLine(lines, ref index, 'i', out string? mediaInfo);
        
        // c= (connections, zero or more at media level)
        var connections = new List<SdpConnection>();
        while (TryGetLine(lines, ref index, 'c', out string? connStr))
        {
            var conn = ParseConnection(connStr);
            if (conn.HasValue)
                connections.Add(conn.Value);
        }
        
        // b= (bandwidth, zero or more)
        var bandwidths = new List<SdpBandwidth>();
        while (TryGetLine(lines, ref index, 'b', out string? bwStr))
        {
            var bw = ParseBandwidth(bwStr);
            if (bw.HasValue)
                bandwidths.Add(bw.Value);
        }
        
        // k= (encryption key, optional, skip)
        TryGetLine(lines, ref index, 'k', out _);
        
        // a= (media attributes, zero or more)
        var attributes = new List<SdpAttribute>();
        while (TryGetLine(lines, ref index, 'a', out string? attrStr))
        {
            attributes.Add(ParseAttribute(attrStr));
        }
        
        return new SdpMedia
        {
            MediaType = mediaType,
            Port = port,
            PortCount = portCount,
            Protocol = protocol,
            Formats = formats,
            Information = mediaInfo,
            Connections = connections,
            Bandwidths = bandwidths,
            Attributes = attributes
        };
    }
    
    private static bool TryGetLine(List<SdpLine> lines, ref int index, char type, out string? value)
    {
        if (index < lines.Count && lines[index].Type == type)
        {
            value = lines[index].Value;
            index++;
            return true;
        }
        value = null;
        return false;
    }
    
    private static SdpOrigin ParseOrigin(string value)
    {
        // Format: username sess-id sess-version nettype addrtype unicast-address
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
            return SdpOrigin.Empty;
        
        return new SdpOrigin
        {
            Username = parts[0],
            SessionId = parts[1],
            SessionVersion = parts[2],
            NetworkType = parts[3],
            AddressType = parts[4],
            Address = parts[5]
        };
    }
    
    private static SdpConnection? ParseConnection(string value)
    {
        // Format: nettype addrtype connection-address
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;
        
        string address = parts[2];
        int ttl = 0;
        int addrCount = 1;
        
        // Parse address with optional TTL and count: addr/ttl/count
        int slashIdx = address.IndexOf('/');
        if (slashIdx >= 0)
        {
            var addrParts = address.Split('/');
            address = addrParts[0];
            
            if (addrParts.Length >= 2)
                int.TryParse(addrParts[1], out ttl);
            if (addrParts.Length >= 3)
                int.TryParse(addrParts[2], out addrCount);
        }
        
        return new SdpConnection
        {
            NetworkType = parts[0],
            AddressType = parts[1],
            Address = address,
            Ttl = ttl,
            AddressCount = addrCount
        };
    }
    
    private static SdpBandwidth? ParseBandwidth(string value)
    {
        // Format: bwtype:bandwidth
        int colonIdx = value.IndexOf(':');
        if (colonIdx < 0)
            return null;
        
        string type = value[..colonIdx];
        if (!int.TryParse(value.AsSpan(colonIdx + 1), out int kbps))
            return null;
        
        return new SdpBandwidth
        {
            Type = type,
            Kbps = kbps
        };
    }
    
    private static SdpTime ParseTime(string value)
    {
        // Format: start-time stop-time
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        long startTime = 0;
        long stopTime = 0;
        
        if (parts.Length >= 1)
            long.TryParse(parts[0], out startTime);
        if (parts.Length >= 2)
            long.TryParse(parts[1], out stopTime);
        
        return new SdpTime
        {
            StartTime = startTime,
            StopTime = stopTime,
            Repeats = []
        };
    }
    
    private static SdpRepeat? ParseRepeat(string value)
    {
        // Format: repeat-interval active-duration offsets...
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        
        long repeatInterval = ParseTimeValue(parts[0]);
        long activeDuration = ParseTimeValue(parts[1]);
        
        var offsets = new List<long>();
        for (int i = 2; i < parts.Length; i++)
        {
            offsets.Add(ParseTimeValue(parts[i]));
        }
        
        return new SdpRepeat
        {
            RepeatInterval = repeatInterval,
            ActiveDuration = activeDuration,
            Offsets = offsets
        };
    }
    
    private static long ParseTimeValue(string value)
    {
        // Time values can have suffixes: d (days), h (hours), m (minutes), s (seconds)
        if (value.Length == 0)
            return 0;
        
        char lastChar = value[^1];
        long multiplier = 1;
        string numPart = value;
        
        switch (lastChar)
        {
            case 'd':
                multiplier = 86400;
                numPart = value[..^1];
                break;
            case 'h':
                multiplier = 3600;
                numPart = value[..^1];
                break;
            case 'm':
                multiplier = 60;
                numPart = value[..^1];
                break;
            case 's':
                multiplier = 1;
                numPart = value[..^1];
                break;
        }
        
        if (long.TryParse(numPart, out long num))
            return num * multiplier;
        
        return 0;
    }
    
    private static SdpAttribute ParseAttribute(string value)
    {
        // Format: attribute or attribute:value
        int colonIdx = value.IndexOf(':');
        
        if (colonIdx < 0)
        {
            return new SdpAttribute
            {
                Name = value,
                Value = null
            };
        }
        
        return new SdpAttribute
        {
            Name = value[..colonIdx],
            Value = value[(colonIdx + 1)..]
        };
    }
    
    private readonly struct SdpLine
    {
        public char Type { get; init; }
        public string Value { get; init; }
    }
}

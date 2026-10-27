// SAP (Session Announcement Protocol) Type Definitions
// RFC 2974 compliant structures

using System.Net;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// SAP packet flags (RFC 2974 Section 4).
/// </summary>
[Flags]
public enum SapFlags : byte
{
    None = 0,
    
    /// <summary>Payload is compressed with zlib.</summary>
    Compressed = 0x01,
    
    /// <summary>Payload is encrypted.</summary>
    Encrypted = 0x02,
    
    /// <summary>This is a deletion announcement.</summary>
    Delete = 0x04,
    
    /// <summary>Source address is IPv6 (otherwise IPv4).</summary>
    IPv6 = 0x10
}

/// <summary>
/// SAP announcement state.
/// </summary>
public enum SapAnnouncementState
{
    /// <summary>New announcement received.</summary>
    New,
    
    /// <summary>Announcement refreshed.</summary>
    Updated,
    
    /// <summary>Announcement deleted by source.</summary>
    Deleted,
    
    /// <summary>Announcement expired (timeout).</summary>
    Expired
}

/// <summary>
/// SAP packet header (RFC 2974 Section 4).
/// </summary>
public readonly struct SapHeader
{
    /// <summary>Protocol version (must be 1).</summary>
    public byte Version { get; init; }
    
    /// <summary>Packet flags.</summary>
    public SapFlags Flags { get; init; }
    
    /// <summary>Authentication data length in 32-bit words.</summary>
    public byte AuthenticationLength { get; init; }
    
    /// <summary>Message identifier hash (unique with source).</summary>
    public ushort MessageIdHash { get; init; }
    
    /// <summary>Originating source IP address.</summary>
    public IPAddress OriginatingSource { get; init; }
    
    /// <summary>Gets whether this is an IPv6 source.</summary>
    public bool IsIPv6 => Flags.HasFlag(SapFlags.IPv6);
    
    /// <summary>Gets whether this is a deletion announcement.</summary>
    public bool IsDelete => Flags.HasFlag(SapFlags.Delete);
    
    /// <summary>Gets whether the payload is compressed.</summary>
    public bool IsCompressed => Flags.HasFlag(SapFlags.Compressed);
    
    /// <summary>Gets whether the payload is encrypted.</summary>
    public bool IsEncrypted => Flags.HasFlag(SapFlags.Encrypted);
    
    /// <summary>Gets header size in bytes.</summary>
    public int HeaderSize => 4 + (IsIPv6 ? 16 : 4) + (AuthenticationLength * 4);
}

/// <summary>
/// SAP announcement representing a discovered multicast stream.
/// </summary>
public sealed class SapAnnouncement
{
    /// <summary>
    /// Unique identifier combining source address and message hash.
    /// </summary>
    public required string Id { get; init; }
    
    /// <summary>
    /// Originating source IP address.
    /// </summary>
    public required IPAddress Source { get; init; }
    
    /// <summary>
    /// Message identifier hash.
    /// </summary>
    public required ushort MessageHash { get; init; }
    
    /// <summary>
    /// Session Description Protocol data.
    /// </summary>
    public required SdpSession Session { get; init; }
    
    /// <summary>
    /// Stream name (from SDP s= field).
    /// </summary>
    public string Name => Session.Name;
    
    /// <summary>
    /// Stream description (from SDP i= field).
    /// </summary>
    public string? Description => Session.Information;
    
    /// <summary>
    /// Connection info for multicast (from SDP c= field).
    /// </summary>
    public SdpConnection? Connection => Session.Connection;
    
    /// <summary>
    /// Media descriptions from SDP.
    /// </summary>
    public IReadOnlyList<SdpMedia> MediaDescriptions => Session.MediaDescriptions;
    
    /// <summary>
    /// First received timestamp.
    /// </summary>
    public DateTimeOffset FirstReceived { get; init; }
    
    /// <summary>
    /// Last received timestamp.
    /// </summary>
    public DateTimeOffset LastReceived { get; set; }
    
    /// <summary>
    /// Announcement refresh period (estimated from reception intervals).
    /// </summary>
    public TimeSpan? AnnouncePeriod { get; set; }
    
    /// <summary>
    /// Current state of this announcement.
    /// </summary>
    public SapAnnouncementState State { get; set; } = SapAnnouncementState.New;
    
    /// <summary>
    /// Gets the multicast address for streaming.
    /// </summary>
    public IPAddress? MulticastAddress
    {
        get
        {
            SdpConnection? conn = Connection;
            if (conn == null)
            {
                var firstMedia = Session.MediaDescriptions.FirstOrDefault();
                if (firstMedia != null && firstMedia.Connections.Count > 0)
                {
                    conn = firstMedia.Connections[0];
                }
            }
            
            if (conn.HasValue && !string.IsNullOrEmpty(conn.Value.Address) && 
                IPAddress.TryParse(conn.Value.Address, out var addr))
            {
                return addr;
            }
            return null;
        }
    }
    
    /// <summary>
    /// Gets the first media port.
    /// </summary>
    public int? Port => Session.MediaDescriptions.FirstOrDefault()?.Port;
    
    /// <summary>
    /// Gets category from SDP a=cat attribute.
    /// </summary>
    public string? Category
    {
        get
        {
            foreach (var attr in Session.Attributes)
            {
                if (attr.Name.Equals("cat", StringComparison.OrdinalIgnoreCase))
                    return attr.Value;
            }
            return null;
        }
    }
    
    /// <summary>
    /// Gets tool name from SDP a=tool attribute.
    /// </summary>
    public string? Tool
    {
        get
        {
            foreach (var attr in Session.Attributes)
            {
                if (attr.Name.Equals("tool", StringComparison.OrdinalIgnoreCase))
                    return attr.Value;
            }
            return null;
        }
    }
}

/// <summary>
/// SAP multicast group addresses (RFC 2974 Section 3).
/// </summary>
public static class SapAddresses
{
    /// <summary>Standard SAP port.</summary>
    public const int Port = 9875;
    
    /// <summary>Global scope SAP address (224.2.127.254).</summary>
    public static readonly IPAddress GlobalV4 = IPAddress.Parse("224.2.127.254");
    
    /// <summary>Organization-local SAP address (239.195.255.255).</summary>
    public static readonly IPAddress OrgLocalV4 = IPAddress.Parse("239.195.255.255");
    
    /// <summary>Local scope SAP address (239.255.255.255).</summary>
    public static readonly IPAddress LocalV4 = IPAddress.Parse("239.255.255.255");
    
    /// <summary>Link-local SAP address (224.0.0.255).</summary>
    public static readonly IPAddress LinkLocalV4 = IPAddress.Parse("224.0.0.255");
    
    /// <summary>IPv6 link-local SAP address (FF02::2:7FFE).</summary>
    public static readonly IPAddress LinkLocalV6 = IPAddress.Parse("FF02::2:7FFE");
    
    /// <summary>IPv6 site-local SAP address (FF05::2:7FFE).</summary>
    public static readonly IPAddress SiteLocalV6 = IPAddress.Parse("FF05::2:7FFE");
    
    /// <summary>
    /// Gets the default SAP addresses to listen on.
    /// </summary>
    public static IReadOnlyList<IPAddress> DefaultAddresses { get; } =
        [GlobalV4, OrgLocalV4, LocalV4, LinkLocalV4];
}

/// <summary>
/// Event args for SAP announcement events.
/// </summary>
public sealed class SapAnnouncementEventArgs : EventArgs
{
    public required SapAnnouncement Announcement { get; init; }
    public required SapAnnouncementState State { get; init; }
}

// MediaKernel - UPnP/DLNA Media Server Discovery
// Discovers UPnP/DLNA media servers on the local network using SSDP

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MediaKernel.Core.Discovery;

/// <summary>
/// Represents a discovered UPnP/DLNA device.
/// </summary>
public sealed class UpnpDevice
{
    /// <summary>Unique device identifier (UUID).</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Friendly name for display.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Device manufacturer.</summary>
    public string Manufacturer { get; init; } = string.Empty;

    /// <summary>Device model name.</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>Device description URL.</summary>
    public string DescriptionUrl { get; init; } = string.Empty;

    /// <summary>Base URL for the device.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Content directory service control URL.</summary>
    public string? ContentDirectoryUrl { get; init; }

    /// <summary>Device type URN.</summary>
    public string DeviceType { get; init; } = string.Empty;

    /// <summary>IP address of the device.</summary>
    public IPAddress? IpAddress { get; init; }

    /// <summary>When the device was discovered.</summary>
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    /// <summary>True if this is a media server.</summary>
    public bool IsMediaServer => DeviceType.Contains("MediaServer", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this is a media renderer.</summary>
    public bool IsMediaRenderer => DeviceType.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{FriendlyName} ({ModelName})";
}

/// <summary>
/// Represents a content item from a UPnP/DLNA media server.
/// </summary>
public sealed class UpnpContentItem
{
    /// <summary>Object ID for browsing.</summary>
    public string ObjectId { get; init; } = string.Empty;

    /// <summary>Parent container ID.</summary>
    public string ParentId { get; init; } = string.Empty;

    /// <summary>Title of the item.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>UPnP class (e.g., "object.container", "object.item.audioItem").</summary>
    public string UpnpClass { get; init; } = string.Empty;

    /// <summary>True if this is a container (folder).</summary>
    public bool IsContainer => UpnpClass.StartsWith("object.container", StringComparison.OrdinalIgnoreCase);

    /// <summary>Media URL for playable items.</summary>
    public string? ResourceUrl { get; init; }

    /// <summary>MIME type of the resource.</summary>
    public string? MimeType { get; init; }

    /// <summary>Duration in seconds (for audio/video).</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Album art URL.</summary>
    public string? AlbumArtUrl { get; init; }

    /// <summary>Artist name.</summary>
    public string? Artist { get; init; }

    /// <summary>Album name.</summary>
    public string? Album { get; init; }

    /// <summary>Child count for containers.</summary>
    public int ChildCount { get; init; }
}

/// <summary>
/// Discovers UPnP/DLNA devices on the local network using SSDP.
/// </summary>
public sealed class UpnpDiscovery : IDisposable
{
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    private readonly Dictionary<string, UpnpDevice> discoveredDevices = new();
    private readonly HttpClient httpClient;
    private readonly object lockObj = new();
    private UdpClient? udpClient;
    private CancellationTokenSource? listeningCts;
    private bool disposed;

    /// <summary>
    /// Raised when a new device is discovered.
    /// </summary>
    public event Action<UpnpDevice>? DeviceDiscovered;

    /// <summary>
    /// Raised when a device is no longer available.
    /// </summary>
    public event Action<string>? DeviceLost;

    public UpnpDiscovery()
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Gets all discovered devices.
    /// </summary>
    public IReadOnlyList<UpnpDevice> Devices
    {
        get
        {
            lock (lockObj)
            {
                return new List<UpnpDevice>(discoveredDevices.Values);
            }
        }
    }

    /// <summary>
    /// Performs a one-time search for UPnP devices.
    /// </summary>
    /// <param name="searchTarget">SSDP search target (default: all UPnP devices).</param>
    /// <param name="timeout">Search timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered devices.</returns>
    public async Task<List<UpnpDevice>> SearchAsync(
        string searchTarget = "ssdp:all",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(5);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        // Send M-SEARCH request
        string searchRequest = $"""
            M-SEARCH * HTTP/1.1
            HOST: {SsdpMulticastAddress}:{SsdpPort}
            MAN: "ssdp:discover"
            MX: 3
            ST: {searchTarget}

            """.Replace("\n", "\r\n");

        byte[] requestBytes = Encoding.ASCII.GetBytes(searchRequest);
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);

        await udp.SendAsync(requestBytes, requestBytes.Length, multicastEndpoint);

        var devices = new List<UpnpDevice>();
        var endTime = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
        {
            udp.Client.ReceiveTimeout = (int)(endTime - DateTime.UtcNow).TotalMilliseconds;

            try
            {
                var result = await udp.ReceiveAsync(cancellationToken).AsTask()
                    .WaitAsync(endTime - DateTime.UtcNow, cancellationToken);

                string response = Encoding.ASCII.GetString(result.Buffer);
                var device = await ParseSsdpResponseAsync(response, result.RemoteEndPoint, cancellationToken);

                if (device != null)
                {
                    lock (lockObj)
                    {
                        if (!discoveredDevices.ContainsKey(device.DeviceId))
                        {
                            discoveredDevices[device.DeviceId] = device;
                            devices.Add(device);
                            DeviceDiscovered?.Invoke(device);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Timeout or error, continue
            }
        }

        return devices;
    }

    /// <summary>
    /// Starts listening for SSDP announcements continuously.
    /// </summary>
    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (listeningCts != null)
            return;

        listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
        udpClient.JoinMulticastGroup(IPAddress.Parse(SsdpMulticastAddress));

        _ = ListenLoopAsync(listeningCts.Token);

        // Also do initial search
        await SearchAsync("upnp:rootdevice", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stops listening for SSDP announcements.
    /// </summary>
    public void StopListening()
    {
        listeningCts?.Cancel();
        listeningCts?.Dispose();
        listeningCts = null;

        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && udpClient != null)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(cancellationToken);
                string message = Encoding.ASCII.GetString(result.Buffer);

                // Handle NOTIFY announcements
                if (message.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleNotifyAsync(message, result.RemoteEndPoint, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue listening
            }
        }
    }

    private async Task HandleNotifyAsync(string message, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
    {
        var headers = ParseHttpHeaders(message);

        if (headers.TryGetValue("NTS", out string? nts))
        {
            if (nts.Contains("ssdp:byebye", StringComparison.OrdinalIgnoreCase))
            {
                // Device leaving
                if (headers.TryGetValue("USN", out string? usn))
                {
                    string deviceId = ExtractDeviceId(usn);
                    lock (lockObj)
                    {
                        if (discoveredDevices.Remove(deviceId))
                        {
                            DeviceLost?.Invoke(deviceId);
                        }
                    }
                }
            }
            else if (nts.Contains("ssdp:alive", StringComparison.OrdinalIgnoreCase))
            {
                // Device announcing
                var device = await ParseSsdpResponseAsync(message, remoteEndpoint, cancellationToken);
                if (device != null)
                {
                    lock (lockObj)
                    {
                        if (!discoveredDevices.ContainsKey(device.DeviceId))
                        {
                            discoveredDevices[device.DeviceId] = device;
                            DeviceDiscovered?.Invoke(device);
                        }
                    }
                }
            }
        }
    }

    private async Task<UpnpDevice?> ParseSsdpResponseAsync(string response, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
    {
        var headers = ParseHttpHeaders(response);

        if (!headers.TryGetValue("LOCATION", out string? locationUrl))
            return null;

        string deviceId = headers.TryGetValue("USN", out string? usn) ? ExtractDeviceId(usn) : Guid.NewGuid().ToString();

        try
        {
            // Fetch device description XML
            string descriptionXml = await httpClient.GetStringAsync(locationUrl, cancellationToken);
            return ParseDeviceDescription(descriptionXml, locationUrl, deviceId, remoteEndpoint.Address);
        }
        catch (Exception)
        {
            // Return basic device info if description fetch fails
            return new UpnpDevice
            {
                DeviceId = deviceId,
                DescriptionUrl = locationUrl,
                IpAddress = remoteEndpoint.Address,
                FriendlyName = remoteEndpoint.Address.ToString()
            };
        }
    }

    private UpnpDevice ParseDeviceDescription(string xml, string descriptionUrl, string deviceId, IPAddress ipAddress)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device == null)
                return new UpnpDevice { DeviceId = deviceId, DescriptionUrl = descriptionUrl, IpAddress = ipAddress };

            // Extract base URL
            var uri = new Uri(descriptionUrl);
            string baseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

            // Find ContentDirectory service URL
            string? contentDirectoryUrl = null;
            var services = device.Descendants(ns + "service");
            foreach (var service in services)
            {
                string? serviceType = service.Element(ns + "serviceType")?.Value;
                if (serviceType?.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string? controlUrl = service.Element(ns + "controlURL")?.Value;
                    if (!string.IsNullOrEmpty(controlUrl))
                    {
                        contentDirectoryUrl = controlUrl.StartsWith("/") ? baseUrl + controlUrl : controlUrl;
                    }
                    break;
                }
            }

            return new UpnpDevice
            {
                DeviceId = deviceId,
                FriendlyName = device.Element(ns + "friendlyName")?.Value ?? "Unknown",
                Manufacturer = device.Element(ns + "manufacturer")?.Value ?? string.Empty,
                ModelName = device.Element(ns + "modelName")?.Value ?? string.Empty,
                DeviceType = device.Element(ns + "deviceType")?.Value ?? string.Empty,
                DescriptionUrl = descriptionUrl,
                BaseUrl = baseUrl,
                ContentDirectoryUrl = contentDirectoryUrl,
                IpAddress = ipAddress
            };
        }
        catch (Exception)
        {
            return new UpnpDevice { DeviceId = deviceId, DescriptionUrl = descriptionUrl, IpAddress = ipAddress };
        }
    }

    private static Dictionary<string, string> ParseHttpHeaders(string message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1)) // Skip status/request line
        {
            int colonPos = line.IndexOf(':');
            if (colonPos > 0)
            {
                string key = line.Substring(0, colonPos).Trim();
                string value = line.Substring(colonPos + 1).Trim();
                headers[key] = value;
            }
        }

        return headers;
    }

    private static string ExtractDeviceId(string usn)
    {
        // USN format: uuid:device-UUID::urn:schemas...
        if (usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            int endPos = usn.IndexOf("::", 5);
            return endPos > 5 ? usn.Substring(5, endPos - 5) : usn.Substring(5);
        }
        return usn;
    }

    /// <summary>
    /// Browse content from a UPnP media server.
    /// </summary>
    /// <param name="device">The media server device.</param>
    /// <param name="objectId">Object ID to browse (0 for root).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of content items.</returns>
    public async Task<List<UpnpContentItem>> BrowseAsync(
        UpnpDevice device,
        string objectId = "0",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(device.ContentDirectoryUrl))
            return new List<UpnpContentItem>();

        string soapRequest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:Browse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <ObjectID>{objectId}</ObjectID>
                  <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                  <Filter>*</Filter>
                  <StartingIndex>0</StartingIndex>
                  <RequestedCount>1000</RequestedCount>
                  <SortCriteria></SortCriteria>
                </u:Browse>
              </s:Body>
            </s:Envelope>
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, device.ContentDirectoryUrl)
        {
            Content = new StringContent(soapRequest, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");

        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            string responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseBrowseResponse(responseXml, device.BaseUrl);
        }
        catch (Exception)
        {
            return new List<UpnpContentItem>();
        }
    }

    private List<UpnpContentItem> ParseBrowseResponse(string xml, string baseUrl)
    {
        var items = new List<UpnpContentItem>();

        try
        {
            var doc = XDocument.Parse(xml);

            // Find the Result element which contains DIDL-Lite XML
            var resultElement = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result");
            if (resultElement == null)
                return items;

            string didlXml = resultElement.Value;
            if (string.IsNullOrEmpty(didlXml))
                return items;

            var didlDoc = XDocument.Parse(didlXml);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
            XNamespace didl = "urn:schemas-dlna-org:metadata-1-0/";

            foreach (var element in didlDoc.Root!.Elements())
            {
                bool isContainer = element.Name.LocalName == "container";
                string? resourceUrl = null;
                string? mimeType = null;
                TimeSpan? duration = null;

                if (!isContainer)
                {
                    var res = element.Element(element.Name.Namespace + "res");
                    if (res != null)
                    {
                        resourceUrl = res.Value;
                        mimeType = res.Attribute("protocolInfo")?.Value?.Split(':').Skip(2).FirstOrDefault();

                        string? durationStr = res.Attribute("duration")?.Value;
                        if (!string.IsNullOrEmpty(durationStr))
                        {
                            duration = ParseDuration(durationStr);
                        }
                    }
                }

                items.Add(new UpnpContentItem
                {
                    ObjectId = element.Attribute("id")?.Value ?? string.Empty,
                    ParentId = element.Attribute("parentID")?.Value ?? string.Empty,
                    Title = element.Element(dc + "title")?.Value ?? string.Empty,
                    UpnpClass = element.Element(upnp + "class")?.Value ?? string.Empty,
                    ResourceUrl = resourceUrl,
                    MimeType = mimeType,
                    Duration = duration,
                    AlbumArtUrl = element.Element(upnp + "albumArtURI")?.Value,
                    Artist = element.Element(upnp + "artist")?.Value ?? element.Element(dc + "creator")?.Value,
                    Album = element.Element(upnp + "album")?.Value,
                    ChildCount = int.TryParse(element.Attribute("childCount")?.Value, out int count) ? count : 0
                });
            }
        }
        catch (Exception)
        {
            // Return empty list on parse error
        }

        return items;
    }

    private static TimeSpan? ParseDuration(string duration)
    {
        // Format: H:MM:SS.mmm or H:MM:SS
        var parts = duration.Split(':');
        if (parts.Length >= 3)
        {
            if (int.TryParse(parts[0], out int hours) &&
                int.TryParse(parts[1], out int minutes) &&
                double.TryParse(parts[2], out double seconds))
            {
                return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopListening();
        httpClient.Dispose();
    }
}

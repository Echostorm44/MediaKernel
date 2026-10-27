// MediaKernel - Online Content Discovery
// S.3 Podcast directories, radio browser, IPTV

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Discovery;

/// <summary>
/// The shapes this file reads off the network, generated at compile time rather than
/// discovered by reflection.
/// </summary>
/// <remarks>
/// <c>GetFromJsonAsync&lt;T&gt;</c> and <c>ReadFromJsonAsync&lt;T&gt;</c> carry
/// <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>, because they walk the
/// type with reflection: a trimmer cannot tell which properties to keep and AOT cannot
/// generate the accessors. The overloads that take a <c>JsonTypeInfo&lt;T&gt;</c> are the
/// AOT-safe ones, and this context is where those come from.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ItunesSearchResponse))]
[JsonSerializable(typeof(PodcastIndexSearchResponse))]
[JsonSerializable(typeof(List<RadioBrowserServer>))]
[JsonSerializable(typeof(List<RadioTagResponse>))]
[JsonSerializable(typeof(List<RadioCountryResponse>))]
[JsonSerializable(typeof(List<RadioStationResponse>))]
internal sealed partial class OnlineContentJsonContext : JsonSerializerContext;

/// <summary>
/// Podcast directory integration (iTunes, Podcastindex).
/// </summary>
public sealed class PodcastDirectory
{
    private readonly HttpClient httpClient;
    
    private const string ItunesSearchUrl = "https://itunes.apple.com/search";
    private const string PodcastIndexUrl = "https://api.podcastindex.org/api/1.0";
    
    private string? podcastIndexApiKey;
    private string? podcastIndexApiSecret;
    
    public PodcastDirectory(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaKernel/1.0");
    }
    
    /// <summary>
    /// Configure Podcastindex API credentials.
    /// </summary>
    public void ConfigurePodcastIndex(string apiKey, string apiSecret)
    {
        podcastIndexApiKey = apiKey;
        podcastIndexApiSecret = apiSecret;
    }
    
    /// <summary>
    /// Search podcasts via iTunes.
    /// </summary>
    public async Task<List<PodcastSearchResult>> SearchItunesAsync(string query, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        string url = $"{ItunesSearchUrl}?term={Uri.EscapeDataString(query)}&media=podcast&limit={limit}";
        
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadFromJsonAsync(OnlineContentJsonContext.Default.ItunesSearchResponse, 
            cancellationToken: cancellationToken);
        
        if (json?.Results == null)
            return [];
        
        return json.Results.Select(r => new PodcastSearchResult
        {
            Title = r.CollectionName ?? r.TrackName ?? "",
            Author = r.ArtistName ?? "",
            FeedUrl = r.FeedUrl ?? "",
            ImageUrl = r.ArtworkUrl600 ?? r.ArtworkUrl100,
            Description = r.Description ?? "",
            Genre = r.PrimaryGenreName,
            TrackCount = r.TrackCount
        }).Where(p => !string.IsNullOrEmpty(p.FeedUrl)).ToList();
    }
    
    /// <summary>
    /// Search podcasts via Podcastindex.
    /// </summary>
    public async Task<List<PodcastSearchResult>> SearchPodcastIndexAsync(string query, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(podcastIndexApiKey))
            return [];
        
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string authHash = ComputePodcastIndexAuth(timestamp);
        
        string url = $"{PodcastIndexUrl}/search/byterm?q={Uri.EscapeDataString(query)}&max={limit}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Auth-Date", timestamp.ToString());
        request.Headers.Add("X-Auth-Key", podcastIndexApiKey);
        request.Headers.Add("Authorization", authHash);
        
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadFromJsonAsync(OnlineContentJsonContext.Default.PodcastIndexSearchResponse, 
            cancellationToken: cancellationToken);
        
        if (json?.Feeds == null)
            return [];
        
        return json.Feeds.Select(f => new PodcastSearchResult
        {
            Title = f.Title ?? "",
            Author = f.Author ?? "",
            FeedUrl = f.Url ?? "",
            ImageUrl = f.Image ?? f.Artwork,
            Description = f.Description ?? "",
            Language = f.Language,
            Categories = f.Categories?.Values.ToList() ?? []
        }).ToList();
    }
    
    /// <summary>
    /// Get trending podcasts from Podcastindex.
    /// </summary>
    public async Task<List<PodcastSearchResult>> GetTrendingAsync(int limit = 25,
        string? language = null, string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(podcastIndexApiKey))
            return [];
        
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string authHash = ComputePodcastIndexAuth(timestamp);
        
        string url = $"{PodcastIndexUrl}/podcasts/trending?max={limit}";
        if (!string.IsNullOrEmpty(language))
            url += $"&lang={language}";
        if (!string.IsNullOrEmpty(category))
            url += $"&cat={category}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Auth-Date", timestamp.ToString());
        request.Headers.Add("X-Auth-Key", podcastIndexApiKey);
        request.Headers.Add("Authorization", authHash);
        
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadFromJsonAsync(OnlineContentJsonContext.Default.PodcastIndexSearchResponse, 
            cancellationToken: cancellationToken);
        
        if (json?.Feeds == null)
            return [];
        
        return json.Feeds.Select(f => new PodcastSearchResult
        {
            Title = f.Title ?? "",
            Author = f.Author ?? "",
            FeedUrl = f.Url ?? "",
            ImageUrl = f.Image ?? f.Artwork,
            Description = f.Description ?? "",
            TrendScore = f.TrendScore
        }).ToList();
    }
    
    private string ComputePodcastIndexAuth(long timestamp)
    {
        string input = $"{podcastIndexApiKey}{podcastIndexApiSecret}{timestamp}";
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Radio browser integration (radio-browser.info).
/// </summary>
public sealed class RadioBrowser
{
    private readonly HttpClient httpClient;
    private readonly List<string> serverUrls = [];
    private int currentServerIndex;
    
    private const string DnsLookupUrl = "https://all.api.radio-browser.info/json/servers";
    
    public RadioBrowser(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaKernel/1.0");
    }
    
    /// <summary>
    /// Initialize by discovering available servers.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var servers = await httpClient.GetFromJsonAsync( 
                DnsLookupUrl, OnlineContentJsonContext.Default.ListRadioBrowserServer,
                cancellationToken);
            
            if (servers != null)
            {
                serverUrls.Clear();
                serverUrls.AddRange(servers.Select(s => $"https://{s.Name}"));
            }
        }
        catch
        {
            // Use fallback
            serverUrls.Add("https://de1.api.radio-browser.info");
        }
        
        if (serverUrls.Count == 0)
        {
            serverUrls.Add("https://de1.api.radio-browser.info");
        }
    }
    
    private string CurrentServer => serverUrls.Count > 0 
        ? serverUrls[currentServerIndex % serverUrls.Count] 
        : "https://de1.api.radio-browser.info";
    
    /// <summary>
    /// Search radio stations by name.
    /// </summary>
    public async Task<List<RadioStation>> SearchAsync(string query, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/stations/byname/{Uri.EscapeDataString(query)}?limit={limit}";
        
        return await FetchStationsAsync(url, cancellationToken);
    }
    
    /// <summary>
    /// Get stations by country.
    /// </summary>
    public async Task<List<RadioStation>> GetByCountryAsync(string countryCode, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/stations/bycountrycodeexact/{countryCode}?limit={limit}&order=clickcount&reverse=true";
        
        return await FetchStationsAsync(url, cancellationToken);
    }
    
    /// <summary>
    /// Get stations by tag/genre.
    /// </summary>
    public async Task<List<RadioStation>> GetByTagAsync(string tag, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/stations/bytag/{Uri.EscapeDataString(tag)}?limit={limit}&order=clickcount&reverse=true";
        
        return await FetchStationsAsync(url, cancellationToken);
    }
    
    /// <summary>
    /// Get top clicked stations.
    /// </summary>
    public async Task<List<RadioStation>> GetTopClickedAsync(int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/stations/topclick/{limit}";
        
        return await FetchStationsAsync(url, cancellationToken);
    }
    
    /// <summary>
    /// Get trending stations (by votes in last 24h).
    /// </summary>
    public async Task<List<RadioStation>> GetTrendingAsync(int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/stations/topvote/{limit}";
        
        return await FetchStationsAsync(url, cancellationToken);
    }
    
    /// <summary>
    /// Get all available tags/genres.
    /// </summary>
    public async Task<List<RadioTag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/tags?order=stationcount&reverse=true&limit=100";
        
        var response = await httpClient.GetFromJsonAsync(url, OnlineContentJsonContext.Default.ListRadioTagResponse, cancellationToken);
        
        return response?.Select(t => new RadioTag
        {
            Name = t.Name ?? "",
            StationCount = t.StationCount
        }).ToList() ?? [];
    }
    
    /// <summary>
    /// Get all countries with station counts.
    /// </summary>
    public async Task<List<RadioCountry>> GetCountriesAsync(CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/countries?order=stationcount&reverse=true";
        
        var response = await httpClient.GetFromJsonAsync(url, OnlineContentJsonContext.Default.ListRadioCountryResponse, cancellationToken);
        
        return response?.Select(c => new RadioCountry
        {
            Name = c.Name ?? "",
            Code = c.Iso3166_1 ?? "",
            StationCount = c.StationCount
        }).ToList() ?? [];
    }
    
    /// <summary>
    /// Record a station click (for statistics).
    /// </summary>
    public async Task RecordClickAsync(string stationUuid, CancellationToken cancellationToken = default)
    {
        string url = $"{CurrentServer}/json/url/{stationUuid}";
        
        try
        {
            await httpClient.GetAsync(url, cancellationToken);
        }
        catch
        {
            // Ignore click tracking errors
        }
    }
    
    private async Task<List<RadioStation>> FetchStationsAsync(string url,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync(url, OnlineContentJsonContext.Default.ListRadioStationResponse, cancellationToken);
        
        return response?.Select(s => new RadioStation
        {
            Uuid = s.Stationuuid ?? "",
            Name = s.Name ?? "",
            Url = s.UrlResolved ?? s.Url ?? "",
            Homepage = s.Homepage,
            Favicon = s.Favicon,
            Country = s.Country,
            CountryCode = s.Countrycode,
            Language = s.Language,
            Tags = s.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList() ?? [],
            Codec = s.Codec,
            Bitrate = s.Bitrate,
            Votes = s.Votes,
            ClickCount = s.Clickcount
        }).ToList() ?? [];
    }
}

/// <summary>
/// IPTV playlist manager (M3U with group tags).
/// </summary>
public sealed class IptvPlaylistManager
{
    private readonly HttpClient httpClient;
    
    public IptvPlaylistManager(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }
    
    /// <summary>
    /// Parse an M3U playlist with group tags.
    /// </summary>
    public async Task<IptvPlaylist> ParsePlaylistAsync(string urlOrPath,
        CancellationToken cancellationToken = default)
    {
        string content;
        
        if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            content = await httpClient.GetStringAsync(urlOrPath, cancellationToken);
        }
        else
        {
            content = await System.IO.File.ReadAllTextAsync(urlOrPath, cancellationToken);
        }
        
        return ParseM3U(content);
    }
    
    private static IptvPlaylist ParseM3U(string content)
    {
        var playlist = new IptvPlaylist();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        IptvChannel? currentChannel = null;
        
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            
            if (line.StartsWith("#EXTM3U"))
            {
                // Parse playlist attributes
                playlist.Name = ExtractAttribute(line, "x-tvg-url") ?? "IPTV Playlist";
                continue;
            }
            
            if (line.StartsWith("#EXTINF:"))
            {
                currentChannel = new IptvChannel();
                
                // Parse duration
                int commaIndex = line.IndexOf(',');
                if (commaIndex > 8)
                {
                    string durationPart = line[8..commaIndex];
                    int spaceIndex = durationPart.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        if (int.TryParse(durationPart[..spaceIndex], out int duration))
                            currentChannel.Duration = duration;
                    }
                    else if (int.TryParse(durationPart, out int duration))
                    {
                        currentChannel.Duration = duration;
                    }
                    
                    // Parse name
                    if (commaIndex + 1 < line.Length)
                        currentChannel.Name = line[(commaIndex + 1)..].Trim();
                }
                
                // Parse attributes
                currentChannel.TvgId = ExtractAttribute(line, "tvg-id");
                currentChannel.TvgName = ExtractAttribute(line, "tvg-name");
                currentChannel.TvgLogo = ExtractAttribute(line, "tvg-logo");
                currentChannel.GroupTitle = ExtractAttribute(line, "group-title");
                
                continue;
            }
            
            if (!line.StartsWith("#") && currentChannel != null)
            {
                currentChannel.Url = line;
                playlist.Channels.Add(currentChannel);
                
                // Add to group
                string group = currentChannel.GroupTitle ?? "Uncategorized";
                if (!playlist.Groups.ContainsKey(group))
                    playlist.Groups[group] = [];
                playlist.Groups[group].Add(currentChannel);
                
                currentChannel = null;
            }
        }
        
        return playlist;
    }
    
    private static string? ExtractAttribute(string line, string attribute)
    {
        string search = $"{attribute}=\"";
        int start = line.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        
        start += search.Length;
        int end = line.IndexOf('"', start);
        if (end < 0) return null;
        
        return line[start..end];
    }
}

// Data types

public sealed class PodcastSearchResult
{
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string FeedUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? Description { get; init; }
    public string? Genre { get; init; }
    public string? Language { get; init; }
    public List<string> Categories { get; init; } = [];
    public int? TrackCount { get; init; }
    public int? TrendScore { get; init; }
}

public sealed class RadioStation
{
    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Homepage { get; init; }
    public string? Favicon { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? Language { get; init; }
    public List<string> Tags { get; init; } = [];
    public string? Codec { get; init; }
    public int? Bitrate { get; init; }
    public int Votes { get; init; }
    public int ClickCount { get; init; }
}

public sealed class RadioTag
{
    public required string Name { get; init; }
    public int StationCount { get; init; }
}

public sealed class RadioCountry
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public int StationCount { get; init; }
}

public sealed class IptvPlaylist
{
    public string Name { get; set; } = "IPTV Playlist";
    public List<IptvChannel> Channels { get; } = [];
    public Dictionary<string, List<IptvChannel>> Groups { get; } = new();
}

public sealed class IptvChannel
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? TvgLogo { get; set; }
    public string? GroupTitle { get; set; }
    public int Duration { get; set; } = -1;
}

// JSON response types

internal sealed class ItunesSearchResponse
{
    [JsonPropertyName("results")]
    public List<ItunesResult>? Results { get; set; }
}

internal sealed class ItunesResult
{
    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; set; }
    
    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }
    
    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }
    
    [JsonPropertyName("feedUrl")]
    public string? FeedUrl { get; set; }
    
    [JsonPropertyName("artworkUrl100")]
    public string? ArtworkUrl100 { get; set; }
    
    [JsonPropertyName("artworkUrl600")]
    public string? ArtworkUrl600 { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("primaryGenreName")]
    public string? PrimaryGenreName { get; set; }
    
    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }
}

internal sealed class PodcastIndexSearchResponse
{
    [JsonPropertyName("feeds")]
    public List<PodcastIndexFeed>? Feeds { get; set; }
}

internal sealed class PodcastIndexFeed
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("artwork")]
    public string? Artwork { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    
    [JsonPropertyName("categories")]
    public Dictionary<string, string>? Categories { get; set; }
    
    [JsonPropertyName("trendScore")]
    public int TrendScore { get; set; }
}

internal sealed class RadioBrowserServer
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class RadioStationResponse
{
    [JsonPropertyName("stationuuid")]
    public string? Stationuuid { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("url_resolved")]
    public string? UrlResolved { get; set; }
    
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
    
    [JsonPropertyName("favicon")]
    public string? Favicon { get; set; }
    
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    
    [JsonPropertyName("countrycode")]
    public string? Countrycode { get; set; }
    
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }
    
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }
    
    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }
    
    [JsonPropertyName("votes")]
    public int Votes { get; set; }
    
    [JsonPropertyName("clickcount")]
    public int Clickcount { get; set; }
}

internal sealed class RadioTagResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("stationcount")]
    public int StationCount { get; set; }
}

internal sealed class RadioCountryResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("iso_3166_1")]
    public string? Iso3166_1 { get; set; }
    
    [JsonPropertyName("stationcount")]
    public int StationCount { get; set; }
}

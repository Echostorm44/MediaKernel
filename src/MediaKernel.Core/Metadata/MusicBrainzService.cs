// MediaKernel - MusicBrainz Metadata Service
// Lookup track, artist, and album metadata from MusicBrainz database
// Reference: VLC modules/misc/webservices/musicbrainz.c

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// Track information from MusicBrainz.
/// </summary>
public sealed class MusicBrainzTrack
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; } = 1;
    public TimeSpan Duration { get; init; }
    public string MusicBrainzId { get; init; } = string.Empty;
}

/// <summary>
/// Release (album) information from MusicBrainz.
/// </summary>
public sealed record MusicBrainzRelease
{
    public string Id { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string? CoverArtUrl { get; init; }
    public int TrackCount { get; init; }
    public List<MusicBrainzTrack> Tracks { get; init; } = new();
}

/// <summary>
/// Recording search result from MusicBrainz.
/// </summary>
public sealed class MusicBrainzRecording
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int Score { get; init; }
    public List<MusicBrainzRelease> Releases { get; init; } = new();
}

/// <summary>
/// Artist information from MusicBrainz.
/// </summary>
public sealed class MusicBrainzArtist
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SortName { get; init; } = string.Empty;
    public string? Country { get; init; }
    public string? Disambiguation { get; init; }
    public string? Type { get; init; }
    public int Score { get; init; }
}

/// <summary>
/// Configuration for MusicBrainz service.
/// </summary>
public sealed class MusicBrainzConfig
{
    /// <summary>MusicBrainz API server.</summary>
    public string Server { get; set; } = "musicbrainz.org";
    
    /// <summary>Cover Art Archive server.</summary>
    public string CoverArtServer { get; set; } = "coverartarchive.org";
    
    /// <summary>Application name for User-Agent (required by API).</summary>
    public string ApplicationName { get; set; } = "MediaKernel";
    
    /// <summary>Application version for User-Agent.</summary>
    public string ApplicationVersion { get; set; } = "1.0.0";
    
    /// <summary>Contact email for User-Agent (recommended by API).</summary>
    public string? ContactEmail { get; set; }
    
    /// <summary>Request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    
    /// <summary>Minimum delay between requests (API rate limiting).</summary>
    public TimeSpan RateLimitDelay { get; set; } = TimeSpan.FromMilliseconds(1100);
}

/// <summary>
/// Service for looking up music metadata from MusicBrainz.
/// Implements rate limiting as required by MusicBrainz API.
/// </summary>
public sealed class MusicBrainzService : IDisposable
{
    private readonly MusicBrainzConfig config;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim rateLimitSemaphore = new(1, 1);
    private DateTime lastRequestTime = DateTime.MinValue;
    private bool disposed;
    
    public MusicBrainzService() : this(new MusicBrainzConfig())
    {
    }
    
    public MusicBrainzService(MusicBrainzConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        
        httpClient = new HttpClient
        {
            Timeout = config.Timeout
        };
        
        // MusicBrainz requires a descriptive User-Agent
        string userAgent = $"{config.ApplicationName}/{config.ApplicationVersion}";
        if (!string.IsNullOrEmpty(config.ContactEmail))
            userAgent += $" ( {config.ContactEmail} )";
        
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }
    
    /// <summary>
    /// Search for recordings (tracks) by title and optionally artist.
    /// </summary>
    public async Task<List<MusicBrainzRecording>> SearchRecordingsAsync(
        string title, 
        string? artist = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<MusicBrainzRecording>();
        
        string query = $"recording:\"{EscapeQuery(title)}\"";
        if (!string.IsNullOrWhiteSpace(artist))
            query += $" AND artist:\"{EscapeQuery(artist)}\"";
        
        string url = $"https://{config.Server}/ws/2/recording?query={Uri.EscapeDataString(query)}&limit={limit}&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return new List<MusicBrainzRecording>();
        
        return ParseRecordingSearchResults(json.Value);
    }
    
    /// <summary>
    /// Search for artists by name.
    /// </summary>
    public async Task<List<MusicBrainzArtist>> SearchArtistsAsync(
        string name,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new List<MusicBrainzArtist>();
        
        string query = $"artist:\"{EscapeQuery(name)}\"";
        string url = $"https://{config.Server}/ws/2/artist?query={Uri.EscapeDataString(query)}&limit={limit}&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return new List<MusicBrainzArtist>();
        
        return ParseArtistSearchResults(json.Value);
    }
    
    /// <summary>
    /// Search for releases (albums) by title and optionally artist.
    /// </summary>
    public async Task<List<MusicBrainzRelease>> SearchReleasesAsync(
        string title,
        string? artist = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<MusicBrainzRelease>();
        
        string query = $"release:\"{EscapeQuery(title)}\"";
        if (!string.IsNullOrWhiteSpace(artist))
            query += $" AND artist:\"{EscapeQuery(artist)}\"";
        
        string url = $"https://{config.Server}/ws/2/release?query={Uri.EscapeDataString(query)}&limit={limit}&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return new List<MusicBrainzRelease>();
        
        return ParseReleaseSearchResults(json.Value);
    }
    
    /// <summary>
    /// Get recording details by MusicBrainz ID.
    /// </summary>
    public async Task<MusicBrainzRecording?> GetRecordingAsync(
        string recordingId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            return null;
        
        string url = $"https://{config.Server}/ws/2/recording/{recordingId}?inc=artist-credits+releases+release-groups&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return null;
        
        return ParseRecordingDetail(json.Value);
    }
    
    /// <summary>
    /// Get release details by MusicBrainz ID.
    /// </summary>
    public async Task<MusicBrainzRelease?> GetReleaseAsync(
        string releaseId,
        bool includeTracks = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
            return null;
        
        string inc = "artist-credits+release-groups";
        if (includeTracks)
            inc += "+recordings";
        
        string url = $"https://{config.Server}/ws/2/release/{releaseId}?inc={inc}&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return null;
        
        return ParseReleaseDetail(json.Value);
    }
    
    /// <summary>
    /// Lookup release by CD disc ID.
    /// </summary>
    public async Task<List<MusicBrainzRelease>> LookupByDiscIdAsync(
        string discId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discId))
            return new List<MusicBrainzRelease>();
        
        string url = $"https://{config.Server}/ws/2/discid/{discId}?inc=artist-credits+recordings+release-groups&fmt=json";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return new List<MusicBrainzRelease>();
        
        return ParseDiscIdResults(json.Value);
    }
    
    /// <summary>
    /// Get cover art URL for a release or release group.
    /// </summary>
    public string GetCoverArtUrl(string releaseGroupId)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
            return string.Empty;
        
        return $"https://{config.CoverArtServer}/release-group/{releaseGroupId}/front";
    }
    
    /// <summary>
    /// Check if cover art exists and get direct image URL.
    /// </summary>
    public async Task<string?> GetCoverArtImageUrlAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
            return null;
        
        string url = $"https://{config.CoverArtServer}/release/{releaseId}";
        
        var json = await FetchJsonAsync(url, cancellationToken);
        if (json == null)
            return null;
        
        if (json.Value.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                if (image.TryGetProperty("front", out var front) && front.GetBoolean())
                {
                    if (image.TryGetProperty("image", out var imageUrl))
                        return imageUrl.GetString();
                }
            }
            
            // Fall back to first image
            if (images.GetArrayLength() > 0)
            {
                var first = images[0];
                if (first.TryGetProperty("image", out var imageUrl))
                    return imageUrl.GetString();
            }
        }
        
        return null;
    }
    
    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await rateLimitSemaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        
        try
        {
            // Rate limiting - MusicBrainz requires 1 request per second
            var timeSinceLastRequest = DateTime.UtcNow - lastRequestTime;
            if (timeSinceLastRequest < config.RateLimitDelay)
            {
                await Task.Delay(config.RateLimitDelay - timeSinceLastRequest, cancellationToken);
            }
            
            lastRequestTime = DateTime.UtcNow;
            
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(content).RootElement;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            rateLimitSemaphore.Release();
        }
    }
    
    private static string EscapeQuery(string query)
    {
        // Escape special Lucene characters
        return query
            .Replace("\\", "\\\\")
            .Replace("+", "\\+")
            .Replace("-", "\\-")
            .Replace("&&", "\\&&")
            .Replace("||", "\\||")
            .Replace("!", "\\!")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("^", "\\^")
            .Replace("\"", "\\\"")
            .Replace("~", "\\~")
            .Replace("*", "\\*")
            .Replace("?", "\\?")
            .Replace(":", "\\:");
    }
    
    private static List<MusicBrainzRecording> ParseRecordingSearchResults(JsonElement json)
    {
        var results = new List<MusicBrainzRecording>();
        
        if (!json.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return results;
        
        foreach (var recording in recordings.EnumerateArray())
        {
            var rec = new MusicBrainzRecording
            {
                Id = GetString(recording, "id"),
                Title = GetString(recording, "title"),
                Artist = GetArtistCredit(recording),
                Duration = TimeSpan.FromMilliseconds(GetInt(recording, "length")),
                Score = GetInt(recording, "score"),
                Releases = ParseReleasesFromRecording(recording)
            };
            
            results.Add(rec);
        }
        
        return results;
    }
    
    private static List<MusicBrainzRelease> ParseReleasesFromRecording(JsonElement recording)
    {
        var releases = new List<MusicBrainzRelease>();
        
        if (!recording.TryGetProperty("releases", out var releasesArray) || releasesArray.ValueKind != JsonValueKind.Array)
            return releases;
        
        foreach (var release in releasesArray.EnumerateArray())
        {
            releases.Add(ParseRelease(release));
        }
        
        return releases;
    }
    
    private static MusicBrainzRelease ParseRelease(JsonElement release)
    {
        string groupId = string.Empty;
        string date = string.Empty;
        
        if (release.TryGetProperty("release-group", out var rg))
        {
            groupId = GetString(rg, "id");
            date = GetString(rg, "first-release-date");
        }
        
        if (string.IsNullOrEmpty(date) && release.TryGetProperty("date", out var dateElem))
        {
            date = dateElem.GetString() ?? string.Empty;
        }
        
        int trackCount = 0;
        if (release.TryGetProperty("track-count", out var tc))
            trackCount = tc.GetInt32();
        
        return new MusicBrainzRelease
        {
            Id = GetString(release, "id"),
            GroupId = groupId,
            Title = GetString(release, "title"),
            Artist = GetArtistCredit(release),
            ReleaseDate = date,
            TrackCount = trackCount
        };
    }
    
    private static List<MusicBrainzArtist> ParseArtistSearchResults(JsonElement json)
    {
        var results = new List<MusicBrainzArtist>();
        
        if (!json.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            return results;
        
        foreach (var artist in artists.EnumerateArray())
        {
            results.Add(new MusicBrainzArtist
            {
                Id = GetString(artist, "id"),
                Name = GetString(artist, "name"),
                SortName = GetString(artist, "sort-name"),
                Country = GetStringOrNull(artist, "country"),
                Disambiguation = GetStringOrNull(artist, "disambiguation"),
                Type = GetStringOrNull(artist, "type"),
                Score = GetInt(artist, "score")
            });
        }
        
        return results;
    }
    
    private static List<MusicBrainzRelease> ParseReleaseSearchResults(JsonElement json)
    {
        var results = new List<MusicBrainzRelease>();
        
        if (!json.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return results;
        
        foreach (var release in releases.EnumerateArray())
        {
            results.Add(ParseRelease(release));
        }
        
        return results;
    }
    
    private static MusicBrainzRecording? ParseRecordingDetail(JsonElement json)
    {
        return new MusicBrainzRecording
        {
            Id = GetString(json, "id"),
            Title = GetString(json, "title"),
            Artist = GetArtistCredit(json),
            Duration = TimeSpan.FromMilliseconds(GetInt(json, "length")),
            Releases = ParseReleasesFromRecording(json)
        };
    }
    
    private static MusicBrainzRelease? ParseReleaseDetail(JsonElement json)
    {
        var release = ParseRelease(json);
        
        // Parse full track list from media
        var tracks = new List<MusicBrainzTrack>();
        if (json.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            int discNumber = 0;
            foreach (var medium in media.EnumerateArray())
            {
                discNumber++;
                if (medium.TryGetProperty("tracks", out var tracksArray) && tracksArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var track in tracksArray.EnumerateArray())
                    {
                        tracks.Add(new MusicBrainzTrack
                        {
                            MusicBrainzId = GetString(track, "id"),
                            Title = GetString(track, "title"),
                            Artist = GetArtistCredit(track),
                            TrackNumber = GetInt(track, "position"),
                            DiscNumber = discNumber,
                            Duration = TimeSpan.FromMilliseconds(GetInt(track, "length"))
                        });
                    }
                }
            }
        }
        
        return release with { Tracks = tracks };
    }
    
    private static List<MusicBrainzRelease> ParseDiscIdResults(JsonElement json)
    {
        var results = new List<MusicBrainzRelease>();
        
        if (!json.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return results;
        
        foreach (var release in releases.EnumerateArray())
        {
            var parsed = ParseRelease(release);
            
            // Parse tracks from media
            var tracks = new List<MusicBrainzTrack>();
            if (release.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                int discNumber = 0;
                foreach (var medium in media.EnumerateArray())
                {
                    discNumber++;
                    if (medium.TryGetProperty("tracks", out var tracksArray) && tracksArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var track in tracksArray.EnumerateArray())
                        {
                            string trackTitle = GetString(track, "title");
                            string trackArtist = GetArtistCredit(track);
                            
                            // Also check recording for more info
                            if (track.TryGetProperty("recording", out var recording))
                            {
                                if (string.IsNullOrEmpty(trackTitle))
                                    trackTitle = GetString(recording, "title");
                                if (string.IsNullOrEmpty(trackArtist))
                                    trackArtist = GetArtistCredit(recording);
                            }
                            
                            tracks.Add(new MusicBrainzTrack
                            {
                                MusicBrainzId = GetString(track, "id"),
                                Title = trackTitle,
                                Artist = trackArtist,
                                TrackNumber = GetInt(track, "position"),
                                DiscNumber = discNumber,
                                Duration = TimeSpan.FromMilliseconds(GetInt(track, "length"))
                            });
                        }
                    }
                }
            }
            
            results.Add(parsed with { Tracks = tracks });
        }
        
        return results;
    }
    
    private static string GetArtistCredit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return string.Empty;
        
        var artists = new List<string>();
        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.TryGetProperty("name", out var name))
            {
                artists.Add(name.GetString() ?? string.Empty);
            }
            else if (credit.TryGetProperty("artist", out var artist))
            {
                artists.Add(GetString(artist, "name"));
            }
        }
        
        return string.Join(", ", artists);
    }
    
    private static string GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return string.Empty;
    }
    
    private static string? GetStringOrNull(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }
    
    private static int GetInt(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetInt32();
        }
        return 0;
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        
        httpClient.Dispose();
        rateLimitSemaphore.Dispose();
    }
}

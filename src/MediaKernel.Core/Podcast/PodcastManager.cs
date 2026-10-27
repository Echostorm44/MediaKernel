// MediaKernel - Podcast Manager
// RSS feed subscription, auto-download, episode management

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MediaKernel.Core.Podcast;

/// <summary>
/// The shapes this file persists, generated at compile time rather than discovered by
/// reflection - see the note on <c>SettingsJsonContext</c> for why that is the only form
/// that survives trimming and AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<PodcastSubscription>))]
[JsonSerializable(typeof(List<EpisodePlaybackState>))]
internal sealed partial class PodcastJsonContext : JsonSerializerContext;

/// <summary>
/// Podcast subscription and episode management.
/// </summary>
public sealed class PodcastManager : IAsyncDisposable
{
    private readonly string libraryPath;
    private readonly string downloadPath;
    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, PodcastSubscription> subscriptions = new();
    private readonly ConcurrentDictionary<string, EpisodePlaybackState> playbackStates = new();
    private readonly Timer? refreshTimer;
    private readonly SemaphoreSlim downloadSemaphore = new(3); // Max 3 concurrent downloads
    private bool disposed;
    
    public PodcastManager(string libraryPath, string downloadPath)
    {
        this.libraryPath = libraryPath;
        this.downloadPath = downloadPath;
        
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaKernel/1.0");
        
        // Auto-refresh every hour
        refreshTimer = new Timer(async _ => await RefreshAllFeedsAsync(), null, 
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        
        Directory.CreateDirectory(libraryPath);
        Directory.CreateDirectory(downloadPath);
    }
    
    /// <summary>
    /// Subscribe to a podcast feed.
    /// </summary>
    public async Task<PodcastSubscription> SubscribeAsync(string feedUrl, 
        CancellationToken cancellationToken = default)
    {
        string feedContent = await httpClient.GetStringAsync(feedUrl, cancellationToken);
        var subscription = ParseFeed(feedUrl, feedContent);
        
        subscriptions[subscription.Id] = subscription;
        await SaveSubscriptionsAsync(cancellationToken);
        
        return subscription;
    }
    
    /// <summary>
    /// Unsubscribe from a podcast.
    /// </summary>
    public async Task UnsubscribeAsync(string podcastId, bool deleteDownloads = false,
        CancellationToken cancellationToken = default)
    {
        if (subscriptions.TryRemove(podcastId, out var subscription))
        {
            if (deleteDownloads)
            {
                string podcastDir = Path.Combine(downloadPath, SanitizeFileName(subscription.Title));
                if (Directory.Exists(podcastDir))
                {
                    Directory.Delete(podcastDir, true);
                }
            }
            
            await SaveSubscriptionsAsync(cancellationToken);
        }
    }
    
    /// <summary>
    /// Refresh a specific podcast feed.
    /// </summary>
    public async Task<int> RefreshFeedAsync(string podcastId, 
        CancellationToken cancellationToken = default)
    {
        if (!subscriptions.TryGetValue(podcastId, out var subscription))
            return 0;
        
        try
        {
            string feedContent = await httpClient.GetStringAsync(subscription.FeedUrl, cancellationToken);
            var updated = ParseFeed(subscription.FeedUrl, feedContent);
            
            // Find new episodes
            var existingGuids = subscription.Episodes.Select(e => e.Guid).ToHashSet();
            var newEpisodes = updated.Episodes.Where(e => !existingGuids.Contains(e.Guid)).ToList();
            
            // Add new episodes
            foreach (var episode in newEpisodes)
            {
                subscription.Episodes.Insert(0, episode);
            }
            
            subscription.LastRefresh = DateTimeOffset.UtcNow;
            await SaveSubscriptionsAsync(cancellationToken);
            
            // Auto-download if enabled
            if (subscription.AutoDownload && newEpisodes.Count > 0)
            {
                foreach (var episode in newEpisodes.Take(subscription.MaxAutoDownload))
                {
                    _ = DownloadEpisodeAsync(podcastId, episode.Guid, null, cancellationToken);
                }
            }
            
            return newEpisodes.Count;
        }
        catch (Exception ex)
        {
            subscription.LastError = ex.Message;
            return 0;
        }
    }
    
    /// <summary>
    /// Refresh all podcast feeds.
    /// </summary>
    public async Task<Dictionary<string, int>> RefreshAllFeedsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, int>();
        
        foreach (var subscription in subscriptions.Values)
        {
            int newCount = await RefreshFeedAsync(subscription.Id, cancellationToken);
            results[subscription.Id] = newCount;
        }
        
        return results;
    }
    
    /// <summary>
    /// Download an episode.
    /// </summary>
    public async Task<string?> DownloadEpisodeAsync(string podcastId, string episodeGuid,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!subscriptions.TryGetValue(podcastId, out var subscription))
            return null;
        
        var episode = subscription.Episodes.FirstOrDefault(e => e.Guid == episodeGuid);
        if (episode == null || string.IsNullOrEmpty(episode.MediaUrl))
            return null;
        
        await downloadSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            string podcastDir = Path.Combine(downloadPath, SanitizeFileName(subscription.Title));
            Directory.CreateDirectory(podcastDir);
            
            string extension = Path.GetExtension(new Uri(episode.MediaUrl).AbsolutePath);
            if (string.IsNullOrEmpty(extension)) extension = ".mp3";
            
            string fileName = SanitizeFileName(episode.Title) + extension;
            string filePath = Path.Combine(podcastDir, fileName);
            
            using var response = await httpClient.GetAsync(episode.MediaUrl, 
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            
            response.EnsureSuccessStatusCode();
            
            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            long downloadedBytes = 0;
            
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(filePath);
            
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            
            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;
                    
                    progress?.Report(new DownloadProgress
                    {
                        TotalBytes = totalBytes,
                        DownloadedBytes = downloadedBytes,
                        PercentComplete = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0,
                        EpisodeTitle = episode.Title
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            
            episode.LocalPath = filePath;
            episode.DownloadDate = DateTimeOffset.UtcNow;
            await SaveSubscriptionsAsync(cancellationToken);
            
            return filePath;
        }
        finally
        {
            downloadSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Apply retention policy to a podcast.
    /// </summary>
    public async Task ApplyRetentionPolicyAsync(string podcastId,
        CancellationToken cancellationToken = default)
    {
        if (!subscriptions.TryGetValue(podcastId, out var subscription))
            return;
        
        var policy = subscription.RetentionPolicy;
        var toRemove = new List<PodcastEpisode>();
        
        foreach (var episode in subscription.Episodes)
        {
            if (string.IsNullOrEmpty(episode.LocalPath)) continue;
            if (!File.Exists(episode.LocalPath)) continue;
            
            bool shouldRemove = false;
            
            // Check age
            if (policy.MaxAgeDays > 0 && episode.DownloadDate.HasValue)
            {
                var age = DateTimeOffset.UtcNow - episode.DownloadDate.Value;
                if (age.TotalDays > policy.MaxAgeDays)
                    shouldRemove = true;
            }
            
            // Check if played
            if (policy.DeleteAfterPlayed && episode.IsPlayed)
                shouldRemove = true;
            
            // Keep pinned
            if (episode.IsPinned)
                shouldRemove = false;
            
            if (shouldRemove)
                toRemove.Add(episode);
        }
        
        // Keep minimum count
        int downloadedCount = subscription.Episodes.Count(e => !string.IsNullOrEmpty(e.LocalPath));
        int canRemove = downloadedCount - policy.KeepMinimum;
        
        foreach (var episode in toRemove.Take(Math.Max(0, canRemove)))
        {
            if (File.Exists(episode.LocalPath!))
            {
                File.Delete(episode.LocalPath!);
            }
            episode.LocalPath = null;
        }
        
        await SaveSubscriptionsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Get or update playback position for an episode.
    /// </summary>
    public TimeSpan GetPlaybackPosition(string episodeGuid)
    {
        return playbackStates.TryGetValue(episodeGuid, out var state) 
            ? state.Position 
            : TimeSpan.Zero;
    }
    
    /// <summary>
    /// Save playback position for an episode.
    /// </summary>
    public async Task SavePlaybackPositionAsync(string podcastId, string episodeGuid, 
        TimeSpan position, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        playbackStates[episodeGuid] = new EpisodePlaybackState
        {
            EpisodeGuid = episodeGuid,
            Position = position,
            Duration = duration,
            LastPlayed = DateTimeOffset.UtcNow
        };
        
        // Mark as played if near end (within 30 seconds or 95%)
        if (subscriptions.TryGetValue(podcastId, out var subscription))
        {
            var episode = subscription.Episodes.FirstOrDefault(e => e.Guid == episodeGuid);
            if (episode != null)
            {
                bool nearEnd = (duration - position).TotalSeconds < 30 || 
                    (duration.TotalSeconds > 0 && position.TotalSeconds / duration.TotalSeconds > 0.95);
                
                if (nearEnd)
                    episode.IsPlayed = true;
            }
        }
        
        await SavePlaybackStatesAsync(cancellationToken);
    }
    
    /// <summary>
    /// Get all subscriptions.
    /// </summary>
    public IReadOnlyList<PodcastSubscription> GetSubscriptions()
    {
        return subscriptions.Values.OrderBy(s => s.Title).ToList();
    }
    
    /// <summary>
    /// Load subscriptions from disk.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string subscriptionsFile = Path.Combine(libraryPath, "podcasts.json");
        if (File.Exists(subscriptionsFile))
        {
            string json = await File.ReadAllTextAsync(subscriptionsFile, cancellationToken);
            var subs = JsonSerializer.Deserialize(json, PodcastJsonContext.Default.ListPodcastSubscription);
            
            if (subs != null)
            {
                foreach (var sub in subs)
                {
                    subscriptions[sub.Id] = sub;
                }
            }
        }
        
        string statesFile = Path.Combine(libraryPath, "playback-states.json");
        if (File.Exists(statesFile))
        {
            string json = await File.ReadAllTextAsync(statesFile, cancellationToken);
            var states = JsonSerializer.Deserialize(json, PodcastJsonContext.Default.ListEpisodePlaybackState);
            
            if (states != null)
            {
                foreach (var state in states)
                {
                    playbackStates[state.EpisodeGuid] = state;
                }
            }
        }
    }
    
    private async Task SaveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(subscriptions.Values.ToList(),
            PodcastJsonContext.Default.ListPodcastSubscription);
        
        await File.WriteAllTextAsync(
            Path.Combine(libraryPath, "podcasts.json"), json, cancellationToken);
    }
    
    private async Task SavePlaybackStatesAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(playbackStates.Values.ToList(),
            PodcastJsonContext.Default.ListEpisodePlaybackState);
        
        await File.WriteAllTextAsync(
            Path.Combine(libraryPath, "playback-states.json"), json, cancellationToken);
    }
    
    private PodcastSubscription ParseFeed(string feedUrl, string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var channel = doc.Root?.Element("channel");
        
        if (channel == null)
            throw new InvalidDataException("Invalid RSS feed - no channel element");
        
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        
        string title = channel.Element("title")?.Value ?? "Unknown Podcast";
        
        var subscription = new PodcastSubscription
        {
            Id = GeneratePodcastId(feedUrl),
            FeedUrl = feedUrl,
            Title = title,
            Description = channel.Element("description")?.Value ?? 
                channel.Element(itunes + "summary")?.Value ?? "",
            Author = channel.Element(itunes + "author")?.Value ?? 
                channel.Element("managingEditor")?.Value ?? "",
            ImageUrl = channel.Element(itunes + "image")?.Attribute("href")?.Value ??
                channel.Element("image")?.Element("url")?.Value,
            Link = channel.Element("link")?.Value,
            Language = channel.Element("language")?.Value ?? "en",
            Categories = channel.Elements(itunes + "category")
                .Select(c => c.Attribute("text")?.Value)
                .Where(c => c != null)
                .Cast<string>()
                .ToList(),
            LastRefresh = DateTimeOffset.UtcNow
        };
        
        foreach (var item in channel.Elements("item"))
        {
            var enclosure = item.Element("enclosure");
            if (enclosure == null) continue;
            
            string guid = item.Element("guid")?.Value ?? 
                enclosure.Attribute("url")?.Value ?? 
                Guid.NewGuid().ToString();
            
            DateTimeOffset pubDate = DateTimeOffset.MinValue;
            string? pubDateStr = item.Element("pubDate")?.Value;
            if (!string.IsNullOrEmpty(pubDateStr))
            {
                DateTimeOffset.TryParse(pubDateStr, out pubDate);
            }
            
            long.TryParse(enclosure.Attribute("length")?.Value, out long length);
            
            string? durationStr = item.Element(itunes + "duration")?.Value;
            TimeSpan duration = ParseDuration(durationStr);
            
            var episode = new PodcastEpisode
            {
                Guid = guid,
                Title = item.Element("title")?.Value ?? "Untitled Episode",
                Description = item.Element("description")?.Value ??
                    item.Element(itunes + "summary")?.Value ??
                    item.Element(content + "encoded")?.Value ?? "",
                MediaUrl = enclosure.Attribute("url")?.Value,
                MediaType = enclosure.Attribute("type")?.Value ?? "audio/mpeg",
                MediaLength = length,
                Duration = duration,
                PublishDate = pubDate,
                ImageUrl = item.Element(itunes + "image")?.Attribute("href")?.Value,
                Season = int.TryParse(item.Element(itunes + "season")?.Value, out int s) ? s : null,
                EpisodeNumber = int.TryParse(item.Element(itunes + "episode")?.Value, out int e) ? e : null
            };
            
            // Parse chapter markers if present
            var chapters = item.Element(PscNamespace + "chapters");
            if (chapters != null)
            {
                foreach (var ch in chapters.Elements(PscNamespace + "chapter"))
                {
                    var startAttr = ch.Attribute("start");
                    var titleAttr = ch.Attribute("title");
                    
                    if (startAttr != null && titleAttr != null)
                    {
                        episode.Chapters.Add(new ChapterMarker
                        {
                            StartTime = ParseDuration(startAttr.Value),
                            Title = titleAttr.Value,
                            ImageUrl = ch.Attribute("image")?.Value,
                            Url = ch.Attribute("href")?.Value
                        });
                    }
                }
            }
            
            subscription.Episodes.Add(episode);
        }
        
        // Sort by publish date (newest first)
        subscription.Episodes = subscription.Episodes
            .OrderByDescending(e => e.PublishDate)
            .ToList();
        
        return subscription;
    }
    
    private static readonly XNamespace PscNamespace = "http://podlove.org/simple-chapters";
    
    private static TimeSpan ParseDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return TimeSpan.Zero;
        
        // Handle HH:MM:SS or MM:SS or seconds
        if (duration.Contains(':'))
        {
            var parts = duration.Split(':');
            
            if (parts.Length == 3)
            {
                int.TryParse(parts[0], out int h);
                int.TryParse(parts[1], out int m);
                int.TryParse(parts[2], out int s);
                return new TimeSpan(h, m, s);
            }
            else if (parts.Length == 2)
            {
                int.TryParse(parts[0], out int m);
                int.TryParse(parts[1], out int s);
                return new TimeSpan(0, m, s);
            }
        }
        
        if (int.TryParse(duration, out int totalSeconds))
        {
            return TimeSpan.FromSeconds(totalSeconds);
        }
        
        return TimeSpan.Zero;
    }
    
    private static string GeneratePodcastId(string feedUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(feedUrl));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
    
    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        
        string result = sb.ToString().Trim();
        if (result.Length > 100)
            result = result[..100];
        
        return result;
    }
    
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        
        if (refreshTimer != null)
        {
            await refreshTimer.DisposeAsync();
        }
        
        downloadSemaphore.Dispose();
        httpClient.Dispose();
    }
}

/// <summary>
/// Podcast subscription.
/// </summary>
public sealed class PodcastSubscription
{
    public required string Id { get; init; }
    public required string FeedUrl { get; init; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string? Link { get; set; }
    public string Language { get; set; } = "en";
    public List<string> Categories { get; set; } = [];
    public DateTimeOffset LastRefresh { get; set; }
    public string? LastError { get; set; }
    
    public List<PodcastEpisode> Episodes { get; set; } = [];
    
    // Settings
    public bool AutoDownload { get; set; }
    public int MaxAutoDownload { get; set; } = 3;
    public RetentionPolicy RetentionPolicy { get; set; } = new();
}

/// <summary>
/// Podcast episode.
/// </summary>
public sealed class PodcastEpisode
{
    public required string Guid { get; init; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public string? MediaUrl { get; set; }
    public string MediaType { get; set; } = "audio/mpeg";
    public long MediaLength { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset PublishDate { get; set; }
    public string? ImageUrl { get; set; }
    public int? Season { get; set; }
    public int? EpisodeNumber { get; set; }
    
    // Local state
    public string? LocalPath { get; set; }
    public DateTimeOffset? DownloadDate { get; set; }
    public bool IsPlayed { get; set; }
    public bool IsPinned { get; set; }
    
    // Chapters
    public List<ChapterMarker> Chapters { get; set; } = [];
}

/// <summary>
/// Chapter marker for podcasts/audiobooks.
/// </summary>
public sealed class ChapterMarker
{
    public TimeSpan StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public required string Title { get; set; }
    public string? ImageUrl { get; set; }
    public string? Url { get; set; }
}

/// <summary>
/// Episode retention policy.
/// </summary>
public sealed class RetentionPolicy
{
    public int MaxAgeDays { get; set; } = 30;
    public bool DeleteAfterPlayed { get; set; }
    public int KeepMinimum { get; set; } = 3;
}

/// <summary>
/// Episode playback state for position sync.
/// </summary>
public sealed class EpisodePlaybackState
{
    public required string EpisodeGuid { get; init; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset LastPlayed { get; set; }
}

/// <summary>
/// Download progress.
/// </summary>
public readonly struct DownloadProgress
{
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public double PercentComplete { get; init; }
    public string EpisodeTitle { get; init; }
}

/// <summary>
/// OPML import/export for podcast subscriptions.
/// </summary>
public static class OpmlHandler
{
    /// <summary>
    /// Export subscriptions to OPML format.
    /// </summary>
    public static string ExportToOpml(IEnumerable<PodcastSubscription> subscriptions)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "MediaKernel Podcast Subscriptions"),
                    new XElement("dateCreated", DateTimeOffset.UtcNow.ToString("R"))
                ),
                new XElement("body",
                    subscriptions.Select(s => new XElement("outline",
                        new XAttribute("type", "rss"),
                        new XAttribute("text", s.Title),
                        new XAttribute("title", s.Title),
                        new XAttribute("xmlUrl", s.FeedUrl),
                        s.Link != null ? new XAttribute("htmlUrl", s.Link) : null
                    ))
                )
            )
        );
        
        return doc.ToString();
    }
    
    /// <summary>
    /// Import subscriptions from OPML format.
    /// </summary>
    public static IEnumerable<string> ImportFromOpml(string opmlContent)
    {
        var doc = XDocument.Parse(opmlContent);
        
        foreach (var outline in doc.Descendants("outline"))
        {
            string? xmlUrl = outline.Attribute("xmlUrl")?.Value;
            if (!string.IsNullOrEmpty(xmlUrl))
            {
                yield return xmlUrl;
            }
        }
    }
}

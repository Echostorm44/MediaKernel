// MediaKernel - AcoustID Audio Fingerprint Service
// Lookup track metadata from AcoustID database using audio fingerprints
// Reference: VLC modules/misc/webservices/acoustid.c

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// A single recording match from AcoustID.
/// </summary>
public sealed class AcoustIdRecording
{
    public string MusicBrainzId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
}

/// <summary>
/// An AcoustID lookup result with score and matched recordings.
/// </summary>
public sealed class AcoustIdResult
{
    /// <summary>Confidence score from 0 to 1.</summary>
    public double Score { get; init; }
    
    /// <summary>AcoustID fingerprint ID.</summary>
    public string Id { get; init; } = string.Empty;
    
    /// <summary>Matched recordings from MusicBrainz.</summary>
    public List<AcoustIdRecording> Recordings { get; init; } = new();
}

/// <summary>
/// Response from AcoustID lookup.
/// </summary>
public sealed record AcoustIdResponse
{
    public string Status { get; init; } = string.Empty;
    public List<AcoustIdResult> Results { get; init; } = new();
}

/// <summary>
/// Configuration for AcoustID service.
/// </summary>
public sealed class AcoustIdConfig
{
    /// <summary>
    /// AcoustID API server. Default uses VideoLAN anonymized proxy.
    /// Set to "api.acoustid.org" for direct access (requires API key).
    /// </summary>
    public string Server { get; set; } = "fingerprint.videolan.org";
    
    /// <summary>
    /// API path. Different for direct vs proxy access.
    /// </summary>
    public string ApiPath { get; set; } = "/acoustid.php";
    
    /// <summary>
    /// API key for direct AcoustID access. Not needed for VideoLAN proxy.
    /// </summary>
    public string? ApiKey { get; set; }
    
    /// <summary>Application name for User-Agent.</summary>
    public string ApplicationName { get; set; } = "MediaKernel";
    
    /// <summary>Application version for User-Agent.</summary>
    public string ApplicationVersion { get; set; } = "1.0.0";
    
    /// <summary>Request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    
    /// <summary>
    /// Metadata to request. Combine with '+'.
    /// Options: recordings, recordingids, releases, releaseids, releasegroups,
    /// releasegroupids, tracks, compress, usermeta, sources.
    /// </summary>
    public string Metadata { get; set; } = "recordings+tracks+usermeta+releases";
}

/// <summary>
/// Service for looking up audio fingerprints via AcoustID.
/// Requires Chromaprint-generated fingerprints.
/// </summary>
public sealed class AcoustIdService : IDisposable
{
    private readonly AcoustIdConfig config;
    private readonly HttpClient httpClient;
    private bool disposed;
    
    public AcoustIdService() : this(new AcoustIdConfig())
    {
    }
    
    public AcoustIdService(AcoustIdConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        
        httpClient = new HttpClient
        {
            Timeout = config.Timeout
        };
        
        string userAgent = $"{config.ApplicationName}/{config.ApplicationVersion}";
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }
    
    /// <summary>
    /// Lookup a Chromaprint fingerprint to identify a track.
    /// </summary>
    /// <param name="fingerprint">Chromaprint fingerprint string (base64 encoded).</param>
    /// <param name="durationSeconds">Duration of the audio in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>AcoustID response with matching tracks.</returns>
    public async Task<AcoustIdResponse> LookupFingerprintAsync(
        string fingerprint,
        int durationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return new AcoustIdResponse { Status = "error" };
        
        if (durationSeconds <= 0)
            return new AcoustIdResponse { Status = "error" };
        
        string url = BuildLookupUrl(fingerprint, durationSeconds);
        
        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new AcoustIdResponse { Status = "error" };
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(content);
        }
        catch (OperationCanceledException)
        {
            return new AcoustIdResponse { Status = "cancelled" };
        }
        catch (Exception)
        {
            return new AcoustIdResponse { Status = "error" };
        }
    }
    
    private string BuildLookupUrl(string fingerprint, int durationSeconds)
    {
        string baseUrl;
        
        if (config.Server == "fingerprint.videolan.org")
        {
            // VideoLAN anonymized proxy - no API key needed
            baseUrl = $"https://{config.Server}{config.ApiPath}" +
                     $"?meta={Uri.EscapeDataString(config.Metadata)}" +
                     $"&duration={durationSeconds}" +
                     $"&fingerprint={Uri.EscapeDataString(fingerprint)}";
        }
        else
        {
            // Direct AcoustID access - API key required
            baseUrl = $"https://{config.Server}/v2/lookup" +
                     $"?client={Uri.EscapeDataString(config.ApiKey ?? "")}" +
                     $"&meta={Uri.EscapeDataString(config.Metadata)}" +
                     $"&duration={durationSeconds}" +
                     $"&fingerprint={Uri.EscapeDataString(fingerprint)}";
        }
        
        return baseUrl;
    }
    
    private static AcoustIdResponse ParseResponse(string json)
    {
        var response = new AcoustIdResponse();
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("status", out var status))
                response = response with { Status = status.GetString() ?? "unknown" };
            
            if (response.Status != "ok")
                return response;
            
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return response;
            
            var resultList = new List<AcoustIdResult>();
            
            foreach (var result in results.EnumerateArray())
            {
                var acoustResult = new AcoustIdResult
                {
                    Score = result.TryGetProperty("score", out var score) ? score.GetDouble() : 0,
                    Id = result.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Recordings = ParseRecordings(result)
                };
                
                resultList.Add(acoustResult);
            }
            
            return response with { Results = resultList };
        }
        catch
        {
            return new AcoustIdResponse { Status = "parse_error" };
        }
    }
    
    private static List<AcoustIdRecording> ParseRecordings(JsonElement result)
    {
        var recordings = new List<AcoustIdRecording>();
        
        if (!result.TryGetProperty("recordings", out var recordingsArray) || 
            recordingsArray.ValueKind != JsonValueKind.Array)
            return recordings;
        
        foreach (var recording in recordingsArray.EnumerateArray())
        {
            string title = recording.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string mbId = recording.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            string artist = "";
            
            // Parse artist from artists array
            if (recording.TryGetProperty("artists", out var artists) && 
                artists.ValueKind == JsonValueKind.Array && 
                artists.GetArrayLength() > 0)
            {
                var firstArtist = artists[0];
                if (firstArtist.TryGetProperty("name", out var name))
                    artist = name.GetString() ?? "";
            }
            
            recordings.Add(new AcoustIdRecording
            {
                MusicBrainzId = mbId,
                Title = title,
                Artist = artist
            });
        }
        
        return recordings;
    }
    
    /// <summary>
    /// Gets the best matching recording from the results.
    /// </summary>
    public static AcoustIdRecording? GetBestMatch(AcoustIdResponse response)
    {
        if (response.Status != "ok" || response.Results.Count == 0)
            return null;
        
        // Sort by score descending
        var bestResult = response.Results
            .OrderByDescending(r => r.Score)
            .FirstOrDefault();
        
        return bestResult?.Recordings.FirstOrDefault();
    }
    
    /// <summary>
    /// Gets all recordings with score above threshold.
    /// </summary>
    public static List<AcoustIdRecording> GetMatchesAboveThreshold(
        AcoustIdResponse response, 
        double minimumScore = 0.5)
    {
        var matches = new List<AcoustIdRecording>();
        
        if (response.Status != "ok")
            return matches;
        
        foreach (var result in response.Results.Where(r => r.Score >= minimumScore))
        {
            matches.AddRange(result.Recordings);
        }
        
        return matches;
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        httpClient.Dispose();
    }
}

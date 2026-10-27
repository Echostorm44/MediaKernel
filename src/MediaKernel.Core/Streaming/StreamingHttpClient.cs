// MediaKernel - HTTP Streaming Client
// Optimized for segment downloads with connection pooling and retry logic

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Result of a segment download operation.
/// </summary>
public readonly struct SegmentDownloadResult
{
    public readonly bool Success;
    public readonly byte[] Data;
    public readonly long DownloadTimeMilliseconds;
    public readonly int StatusCode;
    public readonly string? ErrorMessage;
    public readonly long BytesDownloaded;

    private SegmentDownloadResult(
        bool success,
        byte[] data,
        long downloadTimeMilliseconds,
        int statusCode,
        string? errorMessage)
    {
        Success = success;
        Data = data;
        DownloadTimeMilliseconds = downloadTimeMilliseconds;
        StatusCode = statusCode;
        ErrorMessage = errorMessage;
        BytesDownloaded = data.Length;
    }

    public static SegmentDownloadResult Succeeded(byte[] data, long downloadTimeMs, int statusCode = 200)
        => new(true, data, downloadTimeMs, statusCode, null);

    public static SegmentDownloadResult Failed(string error, int statusCode = 0, long downloadTimeMs = 0)
        => new(false, Array.Empty<byte>(), downloadTimeMs, statusCode, error);

    public double DownloadBitsPerSecond =>
        DownloadTimeMilliseconds > 0
            ? (BytesDownloaded * 8.0 * 1000.0) / DownloadTimeMilliseconds
            : 0;
}

/// <summary>
/// HTTP client optimized for streaming segment downloads.
/// Uses HTTP/2 when available, connection pooling, and retry logic.
/// </summary>
public sealed class StreamingHttpClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly AdaptiveStreamingConfiguration configuration;
    private readonly AdaptiveStreamingStatistics? statistics;
    private readonly SemaphoreSlim downloadSemaphore;
    private bool isDisposed;

    /// <summary>
    /// Maximum concurrent downloads (prevents connection exhaustion).
    /// </summary>
    public int MaxConcurrentDownloads { get; }

    public StreamingHttpClient(
        AdaptiveStreamingConfiguration? configuration = null,
        AdaptiveStreamingStatistics? statistics = null,
        int maxConcurrentDownloads = 4)
    {
        this.configuration = configuration ?? new AdaptiveStreamingConfiguration();
        this.statistics = statistics;
        MaxConcurrentDownloads = maxConcurrentDownloads;
        downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);

        // Configure HttpClient for streaming
        var handler = new SocketsHttpHandler
        {
            // Enable HTTP/2 for multiplexed connections
            EnableMultipleHttp2Connections = true,

            // Connection pooling
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = maxConcurrentDownloads * 2,

            // Keep-alive to reduce connection overhead
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),

            // Compression support
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,

            // Reasonable timeouts
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(this.configuration.DownloadTimeoutMilliseconds),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // Standard streaming headers
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MediaKernel/1.0");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>
    /// Download a media segment.
    /// </summary>
    public async Task<SegmentDownloadResult> DownloadSegmentAsync(
        Uri uri,
        string? byteRange = null,
        CancellationToken cancellation = default)
    {
        await downloadSemaphore.WaitAsync(cancellation);

        var stopwatch = Stopwatch.StartNew();
        int lastStatusCode = 0;

        try
        {
            int attempts = 0;
            Exception? lastException = null;

            while (attempts < configuration.MaxRetryAttempts)
            {
                attempts++;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                    if (!string.IsNullOrEmpty(byteRange))
                    {
                        request.Headers.Range = System.Net.Http.Headers.RangeHeaderValue.Parse($"bytes={byteRange}");
                    }

                    using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellation);

                    lastStatusCode = (int)response.StatusCode;

                    if (response.IsSuccessStatusCode)
                    {
                        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellation);
                        stopwatch.Stop();

                        statistics?.RecordSegmentDownload(data.Length, stopwatch.ElapsedMilliseconds);

                        return SegmentDownloadResult.Succeeded(
                            data,
                            stopwatch.ElapsedMilliseconds,
                            lastStatusCode);
                    }

                    // Non-retryable status codes
                    if (lastStatusCode == 404 || lastStatusCode == 403 || lastStatusCode == 410)
                    {
                        return SegmentDownloadResult.Failed(
                            $"HTTP {lastStatusCode}: {response.ReasonPhrase}",
                            lastStatusCode,
                            stopwatch.ElapsedMilliseconds);
                    }

                    // Retryable - wait and try again
                    lastException = new HttpRequestException($"HTTP {lastStatusCode}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                // Exponential backoff before retry
                if (attempts < configuration.MaxRetryAttempts)
                {
                    int delay = configuration.RetryDelayMilliseconds * (1 << (attempts - 1));
                    await Task.Delay(Math.Min(delay, 5000), cancellation);
                }
            }

            stopwatch.Stop();
            statistics?.RecordDownloadError();

            return SegmentDownloadResult.Failed(
                lastException?.Message ?? "Unknown error",
                lastStatusCode,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Download a manifest/playlist file.
    /// </summary>
    public async Task<SegmentDownloadResult> DownloadManifestAsync(
        Uri uri,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.GetAsync(uri, cancellation);

            int statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                byte[] data = await response.Content.ReadAsByteArrayAsync(cancellation);
                stopwatch.Stop();

                return SegmentDownloadResult.Succeeded(data, stopwatch.ElapsedMilliseconds, statusCode);
            }

            return SegmentDownloadResult.Failed(
                $"HTTP {statusCode}: {response.ReasonPhrase}",
                statusCode,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return SegmentDownloadResult.Failed(ex.Message, 0, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Download a stream with progress callback (for large files).
    /// </summary>
    public async Task<SegmentDownloadResult> DownloadWithProgressAsync(
        Uri uri,
        IProgress<long>? progress = null,
        CancellationToken cancellation = default)
    {
        await downloadSemaphore.WaitAsync(cancellation);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation);

            if (!response.IsSuccessStatusCode)
            {
                return SegmentDownloadResult.Failed(
                    $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }

            long contentLength = response.Content.Headers.ContentLength ?? 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellation);
            using var memoryStream = new MemoryStream(contentLength > 0 ? (int)contentLength : 65536);

            byte[] buffer = new byte[81920]; // 80KB chunks
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellation)) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;
                progress?.Report(totalBytesRead);
            }

            stopwatch.Stop();
            byte[] data = memoryStream.ToArray();

            statistics?.RecordSegmentDownload(data.Length, stopwatch.ElapsedMilliseconds);

            return SegmentDownloadResult.Succeeded(
                data,
                stopwatch.ElapsedMilliseconds,
                (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            statistics?.RecordDownloadError();
            return SegmentDownloadResult.Failed(ex.Message, 0, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            downloadSemaphore.Release();
        }
    }

    /// <summary>
    /// Check if a URL is accessible without downloading full content.
    /// </summary>
    public async Task<bool> CheckAvailabilityAsync(Uri uri, CancellationToken cancellation = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get content length without downloading.
    /// </summary>
    public async Task<long?> GetContentLengthAsync(Uri uri, CancellationToken cancellation = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation);

            if (response.IsSuccessStatusCode)
            {
                return response.Content.Headers.ContentLength;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        httpClient.Dispose();
        downloadSemaphore.Dispose();
    }
}

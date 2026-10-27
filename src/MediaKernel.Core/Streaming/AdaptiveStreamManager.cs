// MediaKernel - Adaptive Stream Manager
// Top-level coordinator for HLS/DASH streaming playback

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// State of the adaptive stream manager.
/// </summary>
public enum StreamManagerState
{
    Stopped,
    Loading,
    Buffering,
    Playing,
    Seeking,
    Error,
    EndOfStream
}

/// <summary>
/// Top-level manager for adaptive streaming playback.
/// Coordinates segment downloading, quality switching, and buffer management.
/// </summary>
public sealed class AdaptiveStreamManager : IDisposable, ISegmentTrackerListener
{
    private readonly StreamingHttpClient httpClient;
    private readonly SegmentTracker segmentTracker;
    private readonly AdaptiveStreamingConfiguration configuration;
    private readonly AdaptiveStreamingStatistics statistics;
    private readonly Channel<DownloadedSegment> segmentChannel;

    private CancellationTokenSource? downloadCancellation;
    private Task? downloadTask;
    private StreamManagerState state = StreamManagerState.Stopped;
    private AdaptiveStream? currentStream;
    private List<MediaSegment> currentSegments = new();
    private readonly object stateLock = new();

    /// <summary>
    /// Raised when a new segment is available for demuxing.
    /// </summary>
    public event Action<DownloadedSegment>? SegmentReady;

    /// <summary>
    /// Raised when state changes.
    /// </summary>
    public event Action<StreamManagerState>? StateChanged;

    /// <summary>
    /// Raised when quality switches.
    /// </summary>
    public event Action<int, int>? QualityChanged;

    /// <summary>
    /// Raised on download errors.
    /// </summary>
    public event Action<string>? DownloadError;

    public AdaptiveStreamManager(
        AdaptiveStreamingConfiguration? configuration = null,
        AdaptiveStreamingStatistics? statistics = null)
    {
        this.configuration = configuration ?? new AdaptiveStreamingConfiguration();
        this.statistics = statistics ?? new AdaptiveStreamingStatistics();

        httpClient = new StreamingHttpClient(this.configuration, this.statistics);
        segmentTracker = new SegmentTracker(this.configuration, this.statistics);
        segmentTracker.AddListener(this);

        // Bounded channel prevents unbounded memory growth
        segmentChannel = Channel.CreateBounded<DownloadedSegment>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Current state of the stream manager.
    /// </summary>
    public StreamManagerState State
    {
        get { lock (stateLock) return state; }
        private set
        {
            bool changed;
            lock (stateLock)
            {
                changed = state != value;
                state = value;
            }
            if (changed)
            {
                StateChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Current buffer level information.
    /// </summary>
    public BufferLevel BufferLevel => segmentTracker.BufferLevel;

    /// <summary>
    /// Current playback position in microseconds.
    /// </summary>
    public long PositionMicroseconds => segmentTracker.PositionMicroseconds;

    /// <summary>
    /// Currently selected quality variant index.
    /// </summary>
    public int CurrentVariantIndex => segmentTracker.CurrentVariantIndex;

    /// <summary>
    /// Available quality variants.
    /// </summary>
    public IReadOnlyList<StreamVariant>? Variants => currentStream?.Variants;

    /// <summary>
    /// Estimated bandwidth in bits per second.
    /// </summary>
    public double EstimatedBandwidth => segmentTracker.EstimatedBandwidth;

    /// <summary>
    /// Streaming statistics.
    /// </summary>
    public AdaptiveStreamingStatistics Statistics => statistics;

    /// <summary>
    /// Start streaming from the given stream definition.
    /// </summary>
    public Task StartAsync(AdaptiveStream stream, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (stateLock)
        {
            if (state != StreamManagerState.Stopped)
            {
                throw new InvalidOperationException("Stream manager is already running");
            }
        }

        currentStream = stream;
        segmentTracker.Initialize(stream);
        State = StreamManagerState.Loading;

        downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        downloadTask = RunDownloadLoopAsync(downloadCancellation.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop streaming.
    /// </summary>
    public async Task StopAsync()
    {
        if (downloadCancellation == null) return;

        downloadCancellation.Cancel();

        if (downloadTask != null)
        {
            try
            {
                await downloadTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        segmentTracker.Reset();
        currentStream = null;
        currentSegments.Clear();
        State = StreamManagerState.Stopped;

        downloadCancellation.Dispose();
        downloadCancellation = null;
        downloadTask = null;
    }

    /// <summary>
    /// Seek to a specific position.
    /// </summary>
    public bool Seek(long positionMicroseconds)
    {
        if (currentSegments.Count == 0) return false;

        var segmentSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(currentSegments);
        bool result = segmentTracker.SeekToPosition(positionMicroseconds, segmentSpan);

        if (result)
        {
            State = StreamManagerState.Seeking;
            // Clear pending segments
            while (segmentChannel.Reader.TryRead(out _)) { }
        }

        return result;
    }

    /// <summary>
    /// Force a specific quality variant.
    /// </summary>
    public void SetQuality(int variantIndex)
    {
        if (currentStream == null) return;
        if (variantIndex < 0 || variantIndex >= currentStream.Variants.Count) return;

        currentStream.CurrentVariantIndex = variantIndex;
    }

    /// <summary>
    /// Report that playback consumed buffer time.
    /// </summary>
    public void ReportPlaybackProgress(long consumedMicroseconds)
    {
        segmentTracker.ReportPlaybackProgress(consumedMicroseconds);

        // Update state based on buffer level
        var level = BufferLevel;
        if (level.IsCritical && State == StreamManagerState.Playing)
        {
            State = StreamManagerState.Buffering;
            statistics.RecordBufferStall();
        }
        else if (level.IsHigh && State == StreamManagerState.Buffering)
        {
            State = StreamManagerState.Playing;
        }
    }

    /// <summary>
    /// Update available segments (from playlist refresh).
    /// </summary>
    public void UpdateSegments(IEnumerable<MediaSegment> segments)
    {
        currentSegments = new List<MediaSegment>(segments);
    }

    /// <summary>
    /// Get the channel reader for consuming downloaded segments.
    /// </summary>
    public ChannelReader<DownloadedSegment> GetSegmentReader() => segmentChannel.Reader;

    /// <summary>
    /// Try to read the next downloaded segment.
    /// </summary>
    public bool TryReadSegment(out DownloadedSegment segment)
    {
        return segmentChannel.Reader.TryRead(out segment);
    }

    /// <summary>
    /// Asynchronously read the next downloaded segment.
    /// </summary>
    public async ValueTask<DownloadedSegment> ReadSegmentAsync(CancellationToken cancellation = default)
    {
        return await segmentChannel.Reader.ReadAsync(cancellation);
    }

    void ISegmentTrackerListener.OnTrackerEvent(TrackerEvent trackerEvent)
    {
        switch (trackerEvent.Type)
        {
            case TrackerEventType.VariantSwitch:
                QualityChanged?.Invoke(
                    trackerEvent.PreviousVariantIndex,
                    trackerEvent.CurrentVariantIndex);
                break;

            case TrackerEventType.EndOfStream:
                State = StreamManagerState.EndOfStream;
                segmentChannel.Writer.TryComplete();
                break;

            case TrackerEventType.BufferingStateChanged:
                if (trackerEvent.Message?.Contains("critical", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (State == StreamManagerState.Playing)
                    {
                        State = StreamManagerState.Buffering;
                    }
                }
                break;
        }
    }

    private async Task RunDownloadLoopAsync(CancellationToken cancellation)
    {
        try
        {
            long initialBufferTarget = (long)(configuration.InitialBufferSeconds * 1_000_000);

            while (!cancellation.IsCancellationRequested)
            {
                // Get next segment to download
                var segmentSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(currentSegments);
                var nextSegment = segmentTracker.GetNextSegment(segmentSpan);

                if (nextSegment == null)
                {
                    // No segment available - wait and retry (for live streams)
                    if (currentStream?.IsLive == true)
                    {
                        await Task.Delay(configuration.LivePlaylistRefreshMilliseconds, cancellation);
                        continue;
                    }

                    // VOD with no more segments - end of stream
                    break;
                }

                var segment = nextSegment.Value;

                // Check if we should pause downloading (buffer full)
                var level = BufferLevel;
                if (level.CurrentMicroseconds > level.MaximumMicroseconds)
                {
                    await Task.Delay(1000, cancellation);
                    continue;
                }

                // Download the segment
                var result = await httpClient.DownloadSegmentAsync(
                    segment.Uri,
                    segment.ByteRange,
                    cancellation);

                if (result.Success)
                {
                    var downloaded = new DownloadedSegment(
                        segment,
                        result.Data,
                        result.DownloadTimeMilliseconds,
                        result.BytesDownloaded);

                    segmentTracker.ReportSegmentDownloaded(downloaded);

                    // Write to channel for demuxer consumption
                    await segmentChannel.Writer.WriteAsync(downloaded, cancellation);

                    SegmentReady?.Invoke(downloaded);

                    // Update state after initial buffering
                    if (State == StreamManagerState.Loading || State == StreamManagerState.Buffering)
                    {
                        if (BufferLevel.CurrentMicroseconds >= initialBufferTarget ||
                            BufferLevel.IsHigh)
                        {
                            State = StreamManagerState.Playing;
                        }
                        else if (State == StreamManagerState.Loading)
                        {
                            State = StreamManagerState.Buffering;
                        }
                    }
                }
                else
                {
                    segmentTracker.ReportSegmentFailed(segment);
                    DownloadError?.Invoke(result.ErrorMessage ?? "Download failed");

                    // For non-retryable errors, skip segment
                    if (result.StatusCode == 404 || result.StatusCode == 410)
                    {
                        // Skip to next segment
                        segmentTracker.ReportPlaybackProgress(segment.DurationMicroseconds);
                    }
                }
            }

            segmentChannel.Writer.TryComplete();
            State = StreamManagerState.EndOfStream;
        }
        catch (OperationCanceledException)
        {
            segmentChannel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            DownloadError?.Invoke(ex.Message);
            State = StreamManagerState.Error;
            segmentChannel.Writer.TryComplete(ex);
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        httpClient.Dispose();
        segmentTracker.Dispose();
    }
}

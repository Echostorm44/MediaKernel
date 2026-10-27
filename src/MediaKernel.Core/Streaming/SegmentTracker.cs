// MediaKernel - Segment Tracker for Adaptive Streaming
// Manages segment queue, position tracking, and quality switching

using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Listener interface for segment tracker events.
/// </summary>
public interface ISegmentTrackerListener
{
    void OnTrackerEvent(TrackerEvent trackerEvent);
}

/// <summary>
/// Tracks segment position and manages quality switching for adaptive streams.
/// Core component that connects adaptation logic with segment downloading.
/// </summary>
public sealed class SegmentTracker : IDisposable
{
    private readonly AdaptationLogic adaptationLogic;
    private readonly BandwidthEstimator bandwidthEstimator;
    private readonly AdaptiveStreamingConfiguration configuration;
    private readonly AdaptiveStreamingStatistics statistics;
    private readonly List<ISegmentTrackerListener> listeners = new();
    private readonly object lockObject = new();

    private AdaptiveStream? currentStream;
    private long currentSequenceNumber;
    private long currentPositionMicroseconds;
    private int currentVariantIndex = -1;
    private bool isInitialized;
    private bool isDisposed;

    // Buffer tracking
    private long bufferedDurationMicroseconds;
    private readonly long targetBufferMicroseconds;
    private readonly long minimumBufferMicroseconds;
    private readonly long maximumBufferMicroseconds;

    // Segment history for discontinuity detection
    private readonly Queue<(long sequence, int variant)> segmentHistory = new();
    private const int MaxHistorySize = 50;

    public SegmentTracker(
        AdaptiveStreamingConfiguration configuration,
        AdaptiveStreamingStatistics? statistics = null)
    {
        this.configuration = configuration;
        this.statistics = statistics ?? new AdaptiveStreamingStatistics();

        bandwidthEstimator = new BandwidthEstimator();
        adaptationLogic = AdaptationLogic.Create(
            configuration.Strategy,
            configuration,
            this.statistics);

        targetBufferMicroseconds = (long)(configuration.TargetBufferSeconds * 1_000_000);
        minimumBufferMicroseconds = (long)(configuration.MinimumBufferSeconds * 1_000_000);
        maximumBufferMicroseconds = (long)(configuration.MaximumBufferSeconds * 1_000_000);
    }

    /// <summary>
    /// Current playback position in microseconds.
    /// </summary>
    public long PositionMicroseconds
    {
        get { lock (lockObject) return currentPositionMicroseconds; }
    }

    /// <summary>
    /// Current segment sequence number.
    /// </summary>
    public long SequenceNumber
    {
        get { lock (lockObject) return currentSequenceNumber; }
    }

    /// <summary>
    /// Currently selected variant index.
    /// </summary>
    public int CurrentVariantIndex
    {
        get { lock (lockObject) return currentVariantIndex; }
    }

    /// <summary>
    /// Current buffer level.
    /// </summary>
    public BufferLevel BufferLevel
    {
        get
        {
            lock (lockObject)
            {
                return new BufferLevel(
                    bufferedDurationMicroseconds,
                    targetBufferMicroseconds,
                    minimumBufferMicroseconds,
                    maximumBufferMicroseconds);
            }
        }
    }

    /// <summary>
    /// Whether the tracker has been initialized with a stream.
    /// </summary>
    public bool IsInitialized
    {
        get { lock (lockObject) return isInitialized; }
    }

    /// <summary>
    /// Estimated bandwidth in bits per second.
    /// </summary>
    public double EstimatedBandwidth => bandwidthEstimator.EstimatedBandwidth;

    /// <summary>
    /// Initialize tracker with an adaptive stream.
    /// </summary>
    public void Initialize(AdaptiveStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (lockObject)
        {
            currentStream = stream;
            currentSequenceNumber = 0;
            currentPositionMicroseconds = 0;
            bufferedDurationMicroseconds = 0;
            segmentHistory.Clear();

            // Select initial variant (start with middle quality for faster startup)
            if (stream.Variants.Count > 0)
            {
                currentVariantIndex = stream.Variants.Count / 2;
                stream.CurrentVariantIndex = currentVariantIndex;
            }

            isInitialized = true;
        }

        NotifyListeners(new TrackerEvent(
            TrackerEventType.BufferingStateChanged,
            message: "Initialized"));
    }

    /// <summary>
    /// Get the next segment to download.
    /// </summary>
    /// <param name="segments">Available segments from current playlist</param>
    /// <param name="allowQualitySwitch">Whether to allow quality switching for this segment</param>
    /// <returns>Next segment to download, or null if no segment available</returns>
    public MediaSegment? GetNextSegment(
        ReadOnlySpan<MediaSegment> segments,
        bool allowQualitySwitch = true)
    {
        lock (lockObject)
        {
            if (!isInitialized || currentStream == null)
                return null;

            if (segments.Length == 0)
                return null;

            // Find segment matching current sequence
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].SequenceNumber >= currentSequenceNumber)
                {
                    var segment = segments[i];

                    // Check for discontinuity
                    if (segment.SequenceNumber != currentSequenceNumber && currentSequenceNumber > 0)
                    {
                        NotifyListeners(new TrackerEvent(
                            TrackerEventType.SegmentGap,
                            sequenceNumber: segment.SequenceNumber,
                            message: $"Gap from {currentSequenceNumber} to {segment.SequenceNumber}"));
                    }

                    // Quality switching decision
                    if (allowQualitySwitch && currentStream.Variants.Count > 1)
                    {
                        SelectBestVariant();
                    }

                    return segment;
                }
            }

            // Check for end of stream
            if (segments.Length > 0 && segments[^1].IsEndOfStream)
            {
                NotifyListeners(new TrackerEvent(TrackerEventType.EndOfStream));
                return segments[^1];
            }

            return null;
        }
    }

    /// <summary>
    /// Report that a segment was successfully downloaded.
    /// </summary>
    public void ReportSegmentDownloaded(DownloadedSegment downloaded)
    {
        lock (lockObject)
        {
            // Update bandwidth estimate
            bandwidthEstimator.AddSample(downloaded.BytesDownloaded, downloaded.DownloadTimeMilliseconds);

            // Update statistics
            statistics.RecordSegmentDownload(downloaded.BytesDownloaded, downloaded.DownloadTimeMilliseconds);

            // Update position
            currentSequenceNumber = downloaded.Segment.SequenceNumber + 1;
            currentPositionMicroseconds += downloaded.Segment.DurationMicroseconds;
            bufferedDurationMicroseconds += downloaded.Segment.DurationMicroseconds;

            // Track history
            segmentHistory.Enqueue((downloaded.Segment.SequenceNumber, downloaded.Segment.VariantIndex));
            while (segmentHistory.Count > MaxHistorySize)
            {
                segmentHistory.Dequeue();
            }
        }

        // Notify buffer level change
        NotifyListeners(new TrackerEvent(
            TrackerEventType.BufferLevelChanged,
            timestampMicroseconds: currentPositionMicroseconds,
            sequenceNumber: downloaded.Segment.SequenceNumber));
    }

    /// <summary>
    /// Report that segment download failed.
    /// </summary>
    public void ReportSegmentFailed(MediaSegment segment, Exception? error = null)
    {
        statistics.RecordDownloadError();

        NotifyListeners(new TrackerEvent(
            TrackerEventType.PlaylistUpdateFailed,
            sequenceNumber: segment.SequenceNumber,
            message: error?.Message));
    }

    /// <summary>
    /// Report that playback consumed buffered content.
    /// </summary>
    public void ReportPlaybackProgress(long consumedMicroseconds)
    {
        lock (lockObject)
        {
            bufferedDurationMicroseconds = Math.Max(0, bufferedDurationMicroseconds - consumedMicroseconds);
        }

        var level = BufferLevel;
        if (level.IsLow)
        {
            NotifyListeners(new TrackerEvent(
                TrackerEventType.BufferingStateChanged,
                timestampMicroseconds: currentPositionMicroseconds,
                message: level.IsCritical ? "Buffer critical" : "Buffer low"));
        }
    }

    /// <summary>
    /// Seek to a specific position.
    /// </summary>
    /// <param name="positionMicroseconds">Target position in microseconds</param>
    /// <param name="segments">Available segments to search</param>
    /// <returns>True if seek was successful</returns>
    public bool SeekToPosition(long positionMicroseconds, ReadOnlySpan<MediaSegment> segments)
    {
        lock (lockObject)
        {
            if (!isInitialized || segments.Length == 0)
                return false;

            // Find segment containing target position
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                long segmentEnd = segment.StartTimeMicroseconds + segment.DurationMicroseconds;

                if (positionMicroseconds >= segment.StartTimeMicroseconds &&
                    positionMicroseconds < segmentEnd)
                {
                    currentSequenceNumber = segment.SequenceNumber;
                    currentPositionMicroseconds = segment.StartTimeMicroseconds;
                    bufferedDurationMicroseconds = 0;
                    segmentHistory.Clear();

                    NotifyListeners(new TrackerEvent(
                        TrackerEventType.PositionChanged,
                        timestampMicroseconds: positionMicroseconds));

                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Set position to start of stream.
    /// </summary>
    public void SeekToStart(ReadOnlySpan<MediaSegment> segments)
    {
        if (segments.Length > 0)
        {
            SeekToPosition(segments[0].StartTimeMicroseconds, segments);
        }
    }

    /// <summary>
    /// Register a listener for tracker events.
    /// </summary>
    public void AddListener(ISegmentTrackerListener listener)
    {
        lock (listeners)
        {
            if (!listeners.Contains(listener))
            {
                listeners.Add(listener);
            }
        }
    }

    /// <summary>
    /// Remove a listener.
    /// </summary>
    public void RemoveListener(ISegmentTrackerListener listener)
    {
        lock (listeners)
        {
            listeners.Remove(listener);
        }
    }

    /// <summary>
    /// Reset tracker state.
    /// </summary>
    public void Reset()
    {
        lock (lockObject)
        {
            currentSequenceNumber = 0;
            currentPositionMicroseconds = 0;
            bufferedDurationMicroseconds = 0;
            currentVariantIndex = -1;
            isInitialized = false;
            segmentHistory.Clear();
            bandwidthEstimator.Reset();
        }
    }

    private void SelectBestVariant()
    {
        if (currentStream == null || currentStream.Variants.Count <= 1)
            return;

        var variants = currentStream.Variants;
        var variantSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(variants);

        int newVariantIndex = adaptationLogic.SelectVariant(
            variantSpan,
            currentVariantIndex,
            BufferLevel,
            bandwidthEstimator.EstimatedBandwidth);

        if (newVariantIndex != currentVariantIndex)
        {
            int previousIndex = currentVariantIndex;
            currentVariantIndex = newVariantIndex;
            currentStream.CurrentVariantIndex = newVariantIndex;

            statistics.RecordVariantSwitch();

            NotifyListeners(new TrackerEvent(
                TrackerEventType.VariantSwitch,
                timestampMicroseconds: currentPositionMicroseconds,
                previousVariantIndex: previousIndex,
                currentVariantIndex: newVariantIndex));
        }
    }

    private void NotifyListeners(TrackerEvent trackerEvent)
    {
        ISegmentTrackerListener[] listenersCopy;
        lock (listeners)
        {
            listenersCopy = listeners.ToArray();
        }

        foreach (var listener in listenersCopy)
        {
            try
            {
                listener.OnTrackerEvent(trackerEvent);
            }
            catch
            {
                // Don't let listener errors break the tracker
            }
        }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        Reset();
        lock (listeners)
        {
            listeners.Clear();
        }
    }
}

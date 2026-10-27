// MediaKernel - Transcoding Pipeline
// Video/audio encoding, resolution/framerate conversion, batch processing
// Based on VLC's transcode module concepts

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Recording;

/// <summary>
/// Transcoding job state.
/// </summary>
public enum TranscodeJobState
{
    Pending,
    Preparing,
    Transcoding,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Video encoding preset levels.
/// </summary>
public enum VideoEncodingPreset
{
    Ultrafast,  // Fastest, lowest quality
    Superfast,
    Veryfast,
    Faster,
    Fast,
    Medium,     // Default balance
    Slow,
    Slower,
    Veryslow,
    Placebo     // Slowest, highest quality
}

/// <summary>
/// Video codec options.
/// </summary>
public enum VideoCodecType
{
    H264,
    H265,
    VP9,
    AV1,
    MJPEG,
    ProRes,
    DNxHD,
    Theora
}

/// <summary>
/// Audio codec options.
/// </summary>
public enum AudioCodecType
{
    AAC,
    MP3,
    Opus,
    Vorbis,
    FLAC,
    AC3,
    EAC3,
    PCM
}

/// <summary>
/// Container format for a transcode's output.
/// </summary>
/// <remarks>
/// This and <see cref="RecordingMetadata"/> are all that survived
/// <c>RecordingEngine.cs</c>, deleted on 2026-10-26. That file held a recording engine,
/// a scheduler and their configuration, and nothing outside its own tests ever
/// constructed any of it; what it wrote was decoded samples with no container header, so
/// the file it produced was one nothing could open. It read as the recorder this project
/// has, which is what made it worth deleting rather than leaving: the recorder is
/// <see cref="StreamTitleRecorder" />, and the muxers a general one would need do not
/// exist yet.
/// </remarks>
public enum RecordingFormat
{
    Mp4,      // Most compatible, fast-start supported
    Mkv,      // Matroska, supports almost any codec
    WebM,     // Web-friendly, VP9/Opus
    Ts,       // MPEG Transport Stream, robust for broadcast
    Avi,      // Legacy format
    Flv,      // Flash Video (for RTMP compatibility)
    Mp3,      // Audio-only MP3
    Wav,      // Audio-only uncompressed
    Flac,     // Audio-only lossless
    Ogg       // Audio-only Vorbis/Opus
}

/// <summary>
/// Tags to write onto a transcode's output.
/// </summary>
public sealed class RecordingMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Comment { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
}

/// <summary>
/// Configuration for a transcoding job.
/// </summary>
public sealed class TranscodeConfiguration
{
    /// <summary>Input file path or URL.</summary>
    public required string InputPath { get; init; }
    
    /// <summary>Output file path.</summary>
    public required string OutputPath { get; init; }
    
    /// <summary>Container format for output.</summary>
    public RecordingFormat OutputFormat { get; init; } = RecordingFormat.Mp4;
    
    // Video settings
    /// <summary>Video codec to use (null = copy/passthrough).</summary>
    public VideoCodecType? VideoCodec { get; init; }
    
    /// <summary>Video encoding preset.</summary>
    public VideoEncodingPreset VideoPreset { get; init; } = VideoEncodingPreset.Medium;
    
    /// <summary>Target video bitrate in kbps (0 = auto based on resolution).</summary>
    public int VideoBitrateKbps { get; init; }
    
    /// <summary>Constant Rate Factor for quality-based encoding (0-51, lower = better).</summary>
    public int? VideoCrf { get; init; }
    
    /// <summary>Target video width (0 = original, -1 = scale proportionally).</summary>
    public int VideoWidth { get; init; }
    
    /// <summary>Target video height (0 = original, -1 = scale proportionally).</summary>
    public int VideoHeight { get; init; }
    
    /// <summary>Target frame rate (0 = original).</summary>
    public double FrameRate { get; init; }
    
    /// <summary>Deinterlace video.</summary>
    public bool Deinterlace { get; init; }
    
    /// <summary>Video rotation in degrees (0, 90, 180, 270).</summary>
    public int Rotation { get; init; }
    
    /// <summary>Crop rectangle (left, top, width, height) or null for no crop.</summary>
    public (int Left, int Top, int Width, int Height)? Crop { get; init; }
    
    // Audio settings
    /// <summary>Audio codec to use (null = copy/passthrough).</summary>
    public AudioCodecType? AudioCodec { get; init; }
    
    /// <summary>Target audio bitrate in kbps (0 = auto).</summary>
    public int AudioBitrateKbps { get; init; }
    
    /// <summary>Target audio sample rate (0 = original).</summary>
    public int AudioSampleRate { get; init; }
    
    /// <summary>Target audio channels (0 = original).</summary>
    public int AudioChannels { get; init; }
    
    /// <summary>Audio volume adjustment (1.0 = no change).</summary>
    public float AudioVolume { get; init; } = 1.0f;
    
    /// <summary>Normalize audio loudness.</summary>
    public bool AudioNormalize { get; init; }
    
    // Timing
    /// <summary>Start position in source (null = from beginning).</summary>
    public TimeSpan? StartPosition { get; init; }
    
    /// <summary>Duration to transcode (null = to end).</summary>
    public TimeSpan? Duration { get; init; }
    
    // Processing
    /// <summary>Number of threads to use (0 = auto).</summary>
    public int Threads { get; init; }
    
    /// <summary>Enable hardware acceleration if available.</summary>
    public bool UseHardwareAcceleration { get; init; } = true;
    
    /// <summary>Two-pass encoding for better quality.</summary>
    public bool TwoPass { get; init; }
    
    /// <summary>Metadata to embed in output.</summary>
    public RecordingMetadata? Metadata { get; init; }
}

/// <summary>
/// Progress information for a transcoding job.
/// </summary>
public readonly struct TranscodeProgress
{
    public TimeSpan ElapsedTime { get; init; }
    public TimeSpan ProcessedDuration { get; init; }
    public TimeSpan? TotalDuration { get; init; }
    public double PercentComplete { get; init; }
    public int FramesProcessed { get; init; }
    public double CurrentFps { get; init; }
    public double AverageFps { get; init; }
    public long BytesWritten { get; init; }
    public double OutputBitrateKbps { get; init; }
    public double SpeedFactor { get; init; } // 1.0 = real-time, 2.0 = 2x speed
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public int CurrentPass { get; init; } // For two-pass encoding
    public int TotalPasses { get; init; }
}

/// <summary>
/// Represents a single transcoding job.
/// </summary>
public sealed class TranscodeJob : IDisposable
{
    private TranscodeJobState state = TranscodeJobState.Pending;
    private readonly TranscodeConfiguration config;
    private CancellationTokenSource? cancellation;
    private DateTime startTime;
    private int framesProcessed;
    private long bytesWritten;
    private TimeSpan processedDuration;
    private TimeSpan? totalDuration;
    private bool disposed;
    
    /// <summary>Unique job identifier.</summary>
    public string Id { get; }
    
    /// <summary>Current job state.</summary>
    public TranscodeJobState State => state;
    
    /// <summary>Job configuration.</summary>
    public TranscodeConfiguration Configuration => config;
    
    /// <summary>Event raised when state changes.</summary>
    public event EventHandler<TranscodeJobState>? StateChanged;
    
    /// <summary>Event raised with progress updates.</summary>
    public event EventHandler<TranscodeProgress>? ProgressChanged;
    
    /// <summary>Event raised on error.</summary>
    public event EventHandler<Exception>? ErrorOccurred;
    
    public TranscodeJob(TranscodeConfiguration configuration)
    {
        Id = Guid.NewGuid().ToString("N")[..8];
        config = configuration;
    }
    
    /// <summary>
    /// Starts the transcoding job.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (state != TranscodeJobState.Pending)
            throw new InvalidOperationException($"Cannot start job in state {state}");
        
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        try
        {
            SetState(TranscodeJobState.Preparing);
            
            // Validate input exists
            if (!System.IO.File.Exists(config.InputPath))
                throw new System.IO.FileNotFoundException("Input file not found", config.InputPath);
            
            // Get input duration (placeholder - would use actual demuxer)
            totalDuration = await GetInputDurationAsync();
            
            SetState(TranscodeJobState.Transcoding);
            startTime = DateTime.UtcNow;
            
            // Main transcoding loop (placeholder - would use actual encoders)
            await TranscodeAsync(cancellation.Token);
            
            SetState(TranscodeJobState.Completed);
        }
        catch (OperationCanceledException)
        {
            SetState(TranscodeJobState.Cancelled);
        }
        catch (Exception ex)
        {
            SetState(TranscodeJobState.Failed);
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }
    
    /// <summary>
    /// Pauses the transcoding job.
    /// </summary>
    public void Pause()
    {
        ThrowIfDisposed();
        
        if (state == TranscodeJobState.Transcoding)
        {
            SetState(TranscodeJobState.Paused);
        }
    }
    
    /// <summary>
    /// Resumes a paused job.
    /// </summary>
    public void Resume()
    {
        ThrowIfDisposed();
        
        if (state == TranscodeJobState.Paused)
        {
            SetState(TranscodeJobState.Transcoding);
        }
    }
    
    /// <summary>
    /// Cancels the transcoding job.
    /// </summary>
    public void Cancel()
    {
        ThrowIfDisposed();
        cancellation?.Cancel();
    }
    
    /// <summary>
    /// Gets current progress.
    /// </summary>
    public TranscodeProgress GetProgress()
    {
        TimeSpan elapsed = state == TranscodeJobState.Transcoding || state == TranscodeJobState.Paused
            ? DateTime.UtcNow - startTime
            : TimeSpan.Zero;
        
        double percent = totalDuration.HasValue && totalDuration.Value.TotalSeconds > 0
            ? processedDuration.TotalSeconds / totalDuration.Value.TotalSeconds * 100
            : 0;
        
        double avgFps = elapsed.TotalSeconds > 0 ? framesProcessed / elapsed.TotalSeconds : 0;
        double speedFactor = elapsed.TotalSeconds > 0 
            ? processedDuration.TotalSeconds / elapsed.TotalSeconds 
            : 0;
        
        TimeSpan? remaining = null;
        if (speedFactor > 0 && totalDuration.HasValue)
        {
            var remainingDuration = totalDuration.Value - processedDuration;
            remaining = TimeSpan.FromSeconds(remainingDuration.TotalSeconds / speedFactor);
        }
        
        double bitrate = elapsed.TotalSeconds > 0 
            ? (bytesWritten * 8.0 / 1000.0) / elapsed.TotalSeconds 
            : 0;
        
        return new TranscodeProgress
        {
            ElapsedTime = elapsed,
            ProcessedDuration = processedDuration,
            TotalDuration = totalDuration,
            PercentComplete = percent,
            FramesProcessed = framesProcessed,
            CurrentFps = avgFps,
            AverageFps = avgFps,
            BytesWritten = bytesWritten,
            OutputBitrateKbps = bitrate,
            SpeedFactor = speedFactor,
            EstimatedTimeRemaining = remaining,
            CurrentPass = 1,
            TotalPasses = config.TwoPass ? 2 : 1
        };
    }
    
    private async Task<TimeSpan?> GetInputDurationAsync()
    {
        // Placeholder - would use actual demuxer to get duration
        await Task.Delay(10);
        return TimeSpan.FromMinutes(5); // Example
    }
    
    private async Task TranscodeAsync(CancellationToken cancellationToken)
    {
        // Placeholder implementation
        // In real implementation, this would:
        // 1. Open input with demuxer
        // 2. Initialize video/audio encoders
        // 3. Process frames through decode -> filter -> encode pipeline
        // 4. Mux to output container
        
        int totalFrames = totalDuration.HasValue 
            ? (int)(totalDuration.Value.TotalSeconds * 30) // Assume 30fps
            : 10000;
        
        for (int i = 0; i < totalFrames && !cancellationToken.IsCancellationRequested; i++)
        {
            // Simulate frame processing
            while (state == TranscodeJobState.Paused)
            {
                await Task.Delay(100, cancellationToken);
            }
            
            await Task.Delay(1, cancellationToken); // Simulate work
            
            framesProcessed = i + 1;
            bytesWritten += 5000; // Simulate ~5KB per frame
            processedDuration = TimeSpan.FromSeconds((double)(i + 1) / 30);
            
            // Report progress every 30 frames
            if (i % 30 == 0)
            {
                ProgressChanged?.Invoke(this, GetProgress());
            }
        }
    }
    
    private void SetState(TranscodeJobState newState)
    {
        if (state != newState)
        {
            state = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }
    }
}

/// <summary>
/// Batch transcoding queue for processing multiple files.
/// </summary>
public sealed class TranscodingQueue : IDisposable
{
    private readonly ConcurrentQueue<TranscodeJob> pendingJobs = new();
    private readonly ConcurrentDictionary<string, TranscodeJob> activeJobs = new();
    private readonly ConcurrentDictionary<string, TranscodeJob> completedJobs = new();
    private int maxConcurrentJobs = 1;
    private CancellationTokenSource? cancellation;
    private Task? processingTask;
    private bool disposed;
    
    /// <summary>Maximum number of concurrent transcoding jobs.</summary>
    public int MaxConcurrentJobs
    {
        get => maxConcurrentJobs;
        set => maxConcurrentJobs = Math.Clamp(value, 1, Environment.ProcessorCount);
    }
    
    /// <summary>Number of pending jobs.</summary>
    public int PendingCount => pendingJobs.Count;
    
    /// <summary>Number of active jobs.</summary>
    public int ActiveCount => activeJobs.Count;
    
    /// <summary>Number of completed jobs.</summary>
    public int CompletedCount => completedJobs.Count;
    
    /// <summary>Event raised when a job completes.</summary>
    public event EventHandler<TranscodeJob>? JobCompleted;
    
    /// <summary>Event raised when a job fails.</summary>
    public event EventHandler<(TranscodeJob Job, Exception Error)>? JobFailed;
    
    /// <summary>
    /// Adds a job to the queue.
    /// </summary>
    /// <param name="configuration">Job configuration.</param>
    /// <returns>The created job.</returns>
    public TranscodeJob Enqueue(TranscodeConfiguration configuration)
    {
        ThrowIfDisposed();
        
        var job = new TranscodeJob(configuration);
        pendingJobs.Enqueue(job);
        return job;
    }
    
    /// <summary>
    /// Starts processing the queue.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        
        if (processingTask != null && !processingTask.IsCompleted)
            return;
        
        cancellation = new CancellationTokenSource();
        processingTask = ProcessQueueAsync(cancellation.Token);
    }
    
    /// <summary>
    /// Stops processing the queue (completes current jobs but doesn't start new ones).
    /// </summary>
    public async Task StopAsync()
    {
        ThrowIfDisposed();
        
        cancellation?.Cancel();
        
        if (processingTask != null)
        {
            try
            {
                await processingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        
        processingTask = null;
    }
    
    /// <summary>
    /// Cancels all jobs and clears the queue.
    /// </summary>
    public void CancelAll()
    {
        ThrowIfDisposed();
        
        cancellation?.Cancel();
        
        foreach (var job in activeJobs.Values)
        {
            job.Cancel();
        }
        
        while (pendingJobs.TryDequeue(out var job))
        {
            job.Dispose();
        }
        
        activeJobs.Clear();
    }
    
    /// <summary>
    /// Gets all jobs in all states.
    /// </summary>
    public IReadOnlyCollection<TranscodeJob> GetAllJobs()
    {
        var all = new List<TranscodeJob>();
        all.AddRange(pendingJobs);
        all.AddRange(activeJobs.Values);
        all.AddRange(completedJobs.Values);
        return all;
    }
    
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Start new jobs up to max concurrent
            while (activeJobs.Count < maxConcurrentJobs && pendingJobs.TryDequeue(out var job))
            {
                activeJobs.TryAdd(job.Id, job);
                _ = ProcessJobAsync(job, cancellationToken);
            }
            
            await Task.Delay(100, cancellationToken);
        }
    }
    
    private async Task ProcessJobAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        try
        {
            await job.StartAsync(cancellationToken);
            
            activeJobs.TryRemove(job.Id, out _);
            completedJobs.TryAdd(job.Id, job);
            
            JobCompleted?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            activeJobs.TryRemove(job.Id, out _);
            completedJobs.TryAdd(job.Id, job);
            
            JobFailed?.Invoke(this, (job, ex));
        }
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            
            cancellation?.Cancel();
            cancellation?.Dispose();
            
            foreach (var job in activeJobs.Values)
            {
                job.Dispose();
            }
            
            while (pendingJobs.TryDequeue(out var job))
            {
                job.Dispose();
            }
            
            foreach (var job in completedJobs.Values)
            {
                job.Dispose();
            }
        }
    }
}

/// <summary>
/// Preset configurations for common transcoding scenarios.
/// </summary>
public static class TranscodePresets
{
    /// <summary>Web-optimized MP4 with H.264 and AAC.</summary>
    public static TranscodeConfiguration WebOptimized(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Mp4,
        VideoCodec = VideoCodecType.H264,
        VideoPreset = VideoEncodingPreset.Medium,
        VideoCrf = 23,
        AudioCodec = AudioCodecType.AAC,
        AudioBitrateKbps = 128,
        UseHardwareAcceleration = true
    };
    
    /// <summary>High quality archive with H.265.</summary>
    public static TranscodeConfiguration ArchiveQuality(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Mkv,
        VideoCodec = VideoCodecType.H265,
        VideoPreset = VideoEncodingPreset.Slow,
        VideoCrf = 18,
        AudioCodec = AudioCodecType.FLAC,
        UseHardwareAcceleration = true
    };
    
    /// <summary>Mobile-optimized small file.</summary>
    public static TranscodeConfiguration Mobile(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Mp4,
        VideoCodec = VideoCodecType.H264,
        VideoPreset = VideoEncodingPreset.Fast,
        VideoBitrateKbps = 1000,
        VideoWidth = 720,
        VideoHeight = -1, // Proportional
        AudioCodec = AudioCodecType.AAC,
        AudioBitrateKbps = 96,
        AudioChannels = 2,
        UseHardwareAcceleration = true
    };
    
    /// <summary>Audio extraction to MP3.</summary>
    public static TranscodeConfiguration AudioOnly(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Mp3,
        VideoCodec = null, // No video
        AudioCodec = AudioCodecType.MP3,
        AudioBitrateKbps = 320
    };
    
    /// <summary>Lossless audio extraction to FLAC.</summary>
    public static TranscodeConfiguration LosslessAudio(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Flac,
        VideoCodec = null,
        AudioCodec = AudioCodecType.FLAC
    };
    
    /// <summary>YouTube upload optimized.</summary>
    public static TranscodeConfiguration YouTube(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Mp4,
        VideoCodec = VideoCodecType.H264,
        VideoPreset = VideoEncodingPreset.Slow,
        VideoBitrateKbps = 8000, // 1080p target
        FrameRate = 30,
        AudioCodec = AudioCodecType.AAC,
        AudioBitrateKbps = 384,
        AudioSampleRate = 48000,
        TwoPass = true,
        UseHardwareAcceleration = true
    };
    
    /// <summary>Streaming-ready HLS segments.</summary>
    public static TranscodeConfiguration StreamingHls(string inputPath, string outputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        OutputFormat = RecordingFormat.Ts,
        VideoCodec = VideoCodecType.H264,
        VideoPreset = VideoEncodingPreset.Veryfast,
        VideoBitrateKbps = 4000,
        AudioCodec = AudioCodecType.AAC,
        AudioBitrateKbps = 128,
        UseHardwareAcceleration = true
    };
}

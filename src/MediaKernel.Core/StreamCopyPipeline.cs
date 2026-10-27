// MediaKernel — Stream Copy Pipeline
// Zero-decode pass-through from demuxer to muxer with timestamp remapping.
// Used for remuxing (container conversion without re-encoding).

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaKernel.Core.Demuxers;
using MediaKernel.Core.Muxers;

namespace MediaKernel.Core;

/// <summary>
/// Configuration for stream copy operations.
/// </summary>
public sealed class StreamCopyOptions
{
    /// <summary>Read buffer size in bytes.</summary>
    public int ReadBufferSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>Track indices to copy. Null = copy all tracks.</summary>
    public int[]? SelectedTrackIndices { get; init; }

    /// <summary>Start time to seek to before copying (null = from beginning).</summary>
    public TimeSpan? StartTime { get; init; }

    /// <summary>Duration to copy (null = until end).</summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Remuxes packets directly from a demuxer to a muxer without decoding.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// await StreamCopyPipeline.CopyAsync("input.mp4", "output.mkv", MuxerFormat.Matroska, cancellationToken: ct);
/// </code>
/// </remarks>
public static class StreamCopyPipeline
{
    /// <summary>
    /// Copies all selected tracks from input to output without decoding.
    /// </summary>
    public static async Task<StreamCopyResult> CopyAsync(
        string inputPath,
        string outputPath,
        MuxerFormat outputFormat,
        StreamCopyOptions? options = null,
        IProgress<StreamCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StreamCopyOptions();

        using var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

        return await CopyAsync(inputStream, outputStream, outputFormat, options, progress, cancellationToken);
    }

    /// <summary>
    /// Copies tracks from input stream to output stream.
    /// </summary>
    public static async Task<StreamCopyResult> CopyAsync(
        Stream inputStream,
        Stream outputStream,
        MuxerFormat outputFormat,
        StreamCopyOptions? options = null,
        IProgress<StreamCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StreamCopyOptions();

        // Probe input format
        Span<byte> header = stackalloc byte[16];
        long startPos = inputStream.Position;
        int headerRead = inputStream.Read(header);
        inputStream.Position = startPos;

        var probe = DemuxerFactory.ProbeByMagic(header[..headerRead]);
        if (!probe.Success)
            throw new NotSupportedException("Could not detect input media format");

        // Create demuxer
        using var demuxerState = CreateDemuxerState(inputStream, probe.Type);

        // Create muxer
        using var muxer = MuxerFactory.Create(outputStream, new MuxerConfiguration { Format = outputFormat });
        using var remux = new RemuxPipeline(muxer);

        // Discover and map tracks
        var trackInfos = demuxerState.GetTrackInfos();
        long totalDurationTicks = (long)(demuxerState.DurationMilliseconds * TimeSpan.TicksPerMillisecond);

        var selectedSet = options.SelectedTrackIndices != null
            ? new System.Collections.Generic.HashSet<int>(options.SelectedTrackIndices)
            : null;

        foreach (var track in trackInfos)
        {
            if (selectedSet != null && !selectedSet.Contains(track.TrackId))
                continue;

            remux.MapTrack(track.TrackId, new MuxerTrackInfo
            {
                TrackId = track.TrackId,
                TrackType = track.TrackType switch
                {
                    DemuxerTrackType.Video => MuxerTrackType.Video,
                    DemuxerTrackType.Audio => MuxerTrackType.Audio,
                    DemuxerTrackType.Subtitle => MuxerTrackType.Subtitle,
                    _ => MuxerTrackType.Video,
                },
                Codec = track.CodecId,
                Width = track.Width,
                Height = track.Height,
                SampleRate = track.SampleRate,
                ChannelCount = track.ChannelCount,
                CodecPrivateData = track.CodecPrivateData,
            });
        }

        // Seek if needed
        if (options.StartTime.HasValue)
            demuxerState.Seek(options.StartTime.Value.TotalMilliseconds);

        long endTimeTicks = options.Duration.HasValue && options.StartTime.HasValue
            ? options.StartTime.Value.Ticks + options.Duration.Value.Ticks
            : options.Duration.HasValue
                ? options.Duration.Value.Ticks
                : long.MaxValue;

        // Start muxer
        await remux.StartAsync(cancellationToken);

        // Copy loop
        var readBuffer = ArrayPool<byte>.Shared.Rent(options.ReadBufferSize);
        long packetsWritten = 0;
        long bytesWritten = 0;
        long currentPositionTicks = 0;
        int progressCounter = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!demuxerState.ReadNextPacket(
                        out int trackIndex, out long pts, out long dts,
                        out bool isKeyframe, out long payloadPosition, out int payloadSize))
                    break;

                // Check track selection
                if (selectedSet != null && !selectedSet.Contains(trackIndex))
                    continue;

                // Check end time
                if (pts > endTimeTicks) break;

                // Read payload
                if (payloadSize > readBuffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(readBuffer);
                    readBuffer = ArrayPool<byte>.Shared.Rent(payloadSize);
                }

                inputStream.Position = payloadPosition;
                int totalRead = 0;
                while (totalRead < payloadSize)
                {
                    int bytesRead = inputStream.Read(readBuffer, totalRead, payloadSize - totalRead);
                    if (bytesRead == 0)
                        throw new EndOfStreamException($"Unexpected end of stream at {payloadPosition + totalRead}");
                    totalRead += bytesRead;
                }

                // Write to muxer
                await remux.WritePacketAsync(
                    trackIndex,
                    new ReadOnlyMemory<byte>(readBuffer, 0, payloadSize),
                    pts, dts, isKeyframe, cancellationToken);

                packetsWritten++;
                bytesWritten += payloadSize;
                currentPositionTicks = pts;

                // Report progress
                if (++progressCounter >= 100)
                {
                    progressCounter = 0;
                    progress?.Report(new StreamCopyProgress
                    {
                        PacketsWritten = packetsWritten,
                        BytesWritten = bytesWritten,
                        CurrentPositionTicks = currentPositionTicks,
                        TotalDurationTicks = totalDurationTicks,
                    });
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        // Finalize
        await remux.FinishAsync(cancellationToken);

        return new StreamCopyResult
        {
            PacketsWritten = packetsWritten,
            BytesWritten = bytesWritten,
            DurationTicks = currentPositionTicks,
            Success = true,
        };
    }

    // ── Demuxer state wrapper ───────────────────────────────────────────

    private static DemuxerState CreateDemuxerState(Stream stream, DemuxerType type)
    {
        return type switch
        {
            DemuxerType.Mp4 => new Mp4DemuxerState(stream),
            DemuxerType.Matroska => new MkvDemuxerState(stream),
            DemuxerType.Avi => new AviDemuxerState(stream),
            _ => throw new NotSupportedException($"Demuxer type {type} not supported for stream copy"),
        };
    }

    private abstract class DemuxerState : IDisposable
    {
        public abstract System.Collections.Generic.List<DemuxerTrackInfo> GetTrackInfos();
        public abstract double DurationMilliseconds { get; }
        public abstract bool Seek(double timestampMs);
        public abstract bool ReadNextPacket(
            out int trackIndex, out long pts, out long dts,
            out bool isKeyframe, out long payloadPosition, out int payloadSize);
        public abstract void Dispose();
    }

    private sealed class Mp4DemuxerState : DemuxerState
    {
        private readonly Mp4Demuxer demuxer;

        public Mp4DemuxerState(Stream stream)
        {
            demuxer = new Mp4Demuxer(stream);
            if (!demuxer.Initialize())
                throw new InvalidDataException("Failed to initialize MP4 demuxer");
        }

        public override double DurationMilliseconds => demuxer.DurationMilliseconds;

        public override System.Collections.Generic.List<DemuxerTrackInfo> GetTrackInfos()
        {
            var infos = new System.Collections.Generic.List<DemuxerTrackInfo>();
            for (int i = 0; i < demuxer.Tracks.Count; i++)
            {
                var track = demuxer.Tracks[i];
                infos.Add(new DemuxerTrackInfo
                {
                    TrackId = i,
                    TrackType = track.TrackType switch
                    {
                        Mp4TrackType.Video => DemuxerTrackType.Video,
                        Mp4TrackType.Audio => DemuxerTrackType.Audio,
                        Mp4TrackType.Subtitle => DemuxerTrackType.Subtitle,
                        _ => DemuxerTrackType.Unknown,
                    },
                    CodecId = FourCCToString(track.CodecFourCC),
                    CodecPrivateData = track.CodecPrivate,
                    Width = track.Width,
                    Height = track.Height,
                    SampleRate = (int)track.AudioSampleRate,
                    ChannelCount = track.AudioChannels,
                    BitsPerSample = track.AudioBitsPerSample,
                });
            }
            return infos;
        }

        public override bool Seek(double timestampMs) => demuxer.Seek(timestampMs);

        public override bool ReadNextPacket(
            out int trackIndex, out long pts, out long dts,
            out bool isKeyframe, out long payloadPosition, out int payloadSize)
        {
            if (!demuxer.ReadNextPacket(out var pkt))
            {
                trackIndex = -1; pts = 0; dts = 0;
                isKeyframe = false; payloadPosition = 0; payloadSize = 0;
                return false;
            }
            trackIndex = pkt.TrackIndex;
            pts = pkt.PresentationTimeTicks;
            dts = pkt.DecodeTimeTicks;
            isKeyframe = pkt.IsKeyframe;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = pkt.PayloadSize;
            return true;
        }

        public override void Dispose() => demuxer.Dispose();
    }

    private sealed class MkvDemuxerState : DemuxerState
    {
        private readonly MatroskaDemuxer demuxer;

        public MkvDemuxerState(Stream stream)
        {
            demuxer = new MatroskaDemuxer(stream);
            if (!demuxer.Initialize())
                throw new InvalidDataException("Failed to initialize Matroska demuxer");
        }

        public override double DurationMilliseconds => demuxer.DurationMilliseconds;

        public override System.Collections.Generic.List<DemuxerTrackInfo> GetTrackInfos()
        {
            var infos = new System.Collections.Generic.List<DemuxerTrackInfo>();
            for (int i = 0; i < demuxer.Tracks.Count; i++)
            {
                var track = demuxer.Tracks[i];
                infos.Add(new DemuxerTrackInfo
                {
                    TrackId = track.TrackNumber,
                    TrackType = track.TrackType switch
                    {
                        MatroskaTrackType.Video => DemuxerTrackType.Video,
                        MatroskaTrackType.Audio => DemuxerTrackType.Audio,
                        MatroskaTrackType.Subtitle => DemuxerTrackType.Subtitle,
                        _ => DemuxerTrackType.Unknown,
                    },
                    CodecId = track.CodecId ?? "unknown",
                    Name = track.Name,
                    Language = track.Language,
                    IsDefault = track.IsDefault,
                    CodecPrivateData = track.CodecPrivate,
                    Width = track.VideoWidth,
                    Height = track.VideoHeight,
                    FrameRate = track.DefaultDurationNs > 0 ? 1_000_000_000.0 / track.DefaultDurationNs : 0,
                    SampleRate = (int)track.AudioSampleRate,
                    ChannelCount = track.AudioChannels,
                    BitsPerSample = track.AudioBitDepth,
                });
            }
            return infos;
        }

        public override bool Seek(double timestampMs) => demuxer.Seek(timestampMs);

        public override bool ReadNextPacket(
            out int trackIndex, out long pts, out long dts,
            out bool isKeyframe, out long payloadPosition, out int payloadSize)
        {
            if (!demuxer.ReadNextPacket(out var pkt))
            {
                trackIndex = -1; pts = 0; dts = 0;
                isKeyframe = false; payloadPosition = 0; payloadSize = 0;
                return false;
            }
            trackIndex = pkt.TrackNumber;
            pts = pkt.TimestampTicks;
            dts = pkt.TimestampTicks;
            isKeyframe = pkt.IsKeyframe;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = (int)pkt.PayloadSize;
            return true;
        }

        public override void Dispose() => demuxer.Dispose();
    }

    private sealed class AviDemuxerState : DemuxerState
    {
        private readonly AviDemuxer demuxer;

        public AviDemuxerState(Stream stream)
        {
            demuxer = new AviDemuxer(stream);
            demuxer.Initialize();
        }

        public override double DurationMilliseconds => demuxer.DurationMilliseconds;

        public override System.Collections.Generic.List<DemuxerTrackInfo> GetTrackInfos()
        {
            var infos = new System.Collections.Generic.List<DemuxerTrackInfo>();
            for (int i = 0; i < demuxer.Tracks.Count; i++)
            {
                var track = demuxer.Tracks[i];
                infos.Add(new DemuxerTrackInfo
                {
                    TrackId = i,
                    TrackType = track.TrackType switch
                    {
                        AviTrackType.Video => DemuxerTrackType.Video,
                        AviTrackType.Audio => DemuxerTrackType.Audio,
                        AviTrackType.Subtitle => DemuxerTrackType.Subtitle,
                        _ => DemuxerTrackType.Unknown,
                    },
                    CodecId = FourCCToString(track.FourCC),
                    Width = track.Width,
                    Height = track.Height,
                    SampleRate = (int)track.AudioSampleRate,
                    ChannelCount = track.AudioChannels,
                    BitsPerSample = track.AudioBitsPerSample,
                    CodecPrivateData = track.CodecPrivate,
                });
            }
            return infos;
        }

        public override bool Seek(double timestampMs) => demuxer.Seek(timestampMs);

        public override bool ReadNextPacket(
            out int trackIndex, out long pts, out long dts,
            out bool isKeyframe, out long payloadPosition, out int payloadSize)
        {
            if (!demuxer.ReadNextPacket(out var pkt))
            {
                trackIndex = -1; pts = 0; dts = 0;
                isKeyframe = false; payloadPosition = 0; payloadSize = 0;
                return false;
            }
            trackIndex = pkt.TrackIndex;
            pts = 0;
            dts = 0;
            isKeyframe = pkt.IsKeyframe;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = pkt.PayloadSize;
            return true;
        }

        public override void Dispose() => demuxer.Dispose();
    }

    private static string FourCCToString(uint fourcc)
    {
        if (fourcc == 0) return "unknown";
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)((fourcc >> 24) & 0xFF);
        chars[1] = (char)((fourcc >> 16) & 0xFF);
        chars[2] = (char)((fourcc >> 8) & 0xFF);
        chars[3] = (char)(fourcc & 0xFF);
        return new string(chars).Trim('\0').ToLowerInvariant();
    }
}

/// <summary>
/// Progress report for stream copy operations.
/// </summary>
public readonly struct StreamCopyProgress
{
    public long PacketsWritten { get; init; }
    public long BytesWritten { get; init; }
    public long CurrentPositionTicks { get; init; }
    public long TotalDurationTicks { get; init; }

    public double ProgressRatio =>
        TotalDurationTicks > 0 ? Math.Clamp((double)CurrentPositionTicks / TotalDurationTicks, 0, 1) : -1;
}

/// <summary>
/// Result of a stream copy operation.
/// </summary>
public readonly struct StreamCopyResult
{
    public bool Success { get; init; }
    public long PacketsWritten { get; init; }
    public long BytesWritten { get; init; }
    public long DurationTicks { get; init; }
}

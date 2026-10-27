// MediaKernel — Filter Graph
// Directed acyclic graph of video/audio filters with format negotiation.
// Linear chains are the common case; multi-input/output supported for future expansion.

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Filters;

// ── Filter Interfaces ──────────────────────────────────────────────────

/// <summary>
/// Describes the format of data flowing through a filter graph link.
/// </summary>
public readonly struct FilterMediaFormat
{
    // Video properties
    public int Width { get; init; }
    public int Height { get; init; }
    public PixelFormat PixelFormat { get; init; }
    public (int Num, int Den) FrameRate { get; init; }

    // Audio properties
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    /// <summary>Whether this is a video format.</summary>
    public bool IsVideo => Width > 0 && Height > 0;

    /// <summary>Whether this is an audio format.</summary>
    public bool IsAudio => SampleRate > 0 && Channels > 0;

    public static FilterMediaFormat Video(int width, int height, PixelFormat format, (int, int) frameRate = default)
        => new() { Width = width, Height = height, PixelFormat = format, FrameRate = frameRate };

    public static FilterMediaFormat Audio(int sampleRate, int channels)
        => new() { SampleRate = sampleRate, Channels = channels };
}

/// <summary>
/// Base interface for all pipeline-aware filters.
/// Filters process frames in-place or produce new frames.
/// </summary>
public interface IFilterNode : IDisposable
{
    /// <summary>Filter name for display and graph serialization.</summary>
    string Name { get; }

    /// <summary>
    /// Queries supported input formats. Called during graph configuration.
    /// </summary>
    FilterMediaFormat[] GetSupportedInputFormats();

    /// <summary>
    /// Given the input format, returns the output format this filter will produce.
    /// Allows format negotiation through the chain.
    /// </summary>
    FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat);
}

/// <summary>
/// Video filter that processes frames in the pipeline.
/// Operates on VideoFrame data (YUV planes or packed formats).
/// </summary>
public interface IVideoFilterNode : IFilterNode
{
    /// <summary>
    /// Processes a video frame in-place or returns a new frame.
    /// If the filter operates in-place, it modifies the frame's planes directly and returns it.
    /// If the filter changes resolution, it allocates a new frame.
    /// </summary>
    /// <param name="frame">Input frame. Caller retains ownership unless filter replaces it.</param>
    /// <returns>Processed frame. May be the same instance (in-place) or a new allocation.</returns>
    VideoFrame Process(VideoFrame frame);
}

/// <summary>
/// Audio filter that processes audio frames.
/// </summary>
public interface IAudioFilterNode : IFilterNode
{
    /// <summary>
    /// Processes an audio frame.
    /// </summary>
    AudioFrame Process(AudioFrame frame);
}

// ── Filter Graph ───────────────────────────────────────────────────────

/// <summary>
/// A node in the filter graph with wiring metadata.
/// </summary>
internal sealed class FilterGraphNode
{
    public required IFilterNode Filter { get; init; }
    public int Index { get; init; }
    public FilterMediaFormat InputFormat { get; set; }
    public FilterMediaFormat OutputFormat { get; set; }
}

/// <summary>
/// A linear chain of video filters applied in sequence.
/// Handles format negotiation and in-place processing where possible.
/// </summary>
/// <remarks>
/// For most use cases, a simple linear chain suffices. The full DAG-based
/// FilterGraph (for overlays, splits, merges) will be built on top of this.
/// </remarks>
public sealed class VideoFilterChain : IDisposable
{
    private readonly List<IVideoFilterNode> filters = new();
    private FilterMediaFormat inputFormat;
    private FilterMediaFormat outputFormat;
    private bool isConfigured;

    /// <summary>Number of filters in the chain.</summary>
    public int Count => filters.Count;

    /// <summary>Input format after configuration.</summary>
    public FilterMediaFormat InputFormat => inputFormat;

    /// <summary>Output format after configuration (reflects any resolution/format changes).</summary>
    public FilterMediaFormat OutputFormat => outputFormat;

    /// <summary>
    /// Appends a filter to the chain. Must be called before Configure().
    /// </summary>
    public VideoFilterChain Add(IVideoFilterNode filter)
    {
        if (isConfigured)
            throw new InvalidOperationException("Cannot add filters after the chain is configured");
        filters.Add(filter);
        return this;
    }

    /// <summary>
    /// Configures the chain with the source format. Negotiates formats through the chain.
    /// </summary>
    public void Configure(FilterMediaFormat sourceFormat)
    {
        inputFormat = sourceFormat;
        var current = sourceFormat;

        foreach (var filter in filters)
        {
            current = filter.GetOutputFormat(current);
        }

        outputFormat = current;
        isConfigured = true;
    }

    /// <summary>
    /// Processes a video frame through all filters in sequence.
    /// </summary>
    public VideoFrame Process(VideoFrame frame)
    {
        if (!isConfigured)
            throw new InvalidOperationException("Chain must be configured before processing");

        var current = frame;
        foreach (var filter in filters)
        {
            current = filter.Process(current);
        }
        return current;
    }

    public void Dispose()
    {
        foreach (var filter in filters)
            filter.Dispose();
        filters.Clear();
    }
}

/// <summary>
/// A linear chain of audio filters applied in sequence.
/// </summary>
public sealed class AudioFilterChain : IDisposable
{
    private readonly List<IAudioFilterNode> filters = new();
    private FilterMediaFormat inputFormat;
    private FilterMediaFormat outputFormat;
    private bool isConfigured;

    public int Count => filters.Count;
    public FilterMediaFormat InputFormat => inputFormat;
    public FilterMediaFormat OutputFormat => outputFormat;

    public AudioFilterChain Add(IAudioFilterNode filter)
    {
        if (isConfigured)
            throw new InvalidOperationException("Cannot add filters after the chain is configured");
        filters.Add(filter);
        return this;
    }

    public void Configure(FilterMediaFormat sourceFormat)
    {
        inputFormat = sourceFormat;
        var current = sourceFormat;

        foreach (var filter in filters)
        {
            current = filter.GetOutputFormat(current);
        }

        outputFormat = current;
        isConfigured = true;
    }

    public AudioFrame Process(AudioFrame frame)
    {
        if (!isConfigured)
            throw new InvalidOperationException("Chain must be configured before processing");

        var current = frame;
        foreach (var filter in filters)
        {
            current = filter.Process(current);
        }
        return current;
    }

    public void Dispose()
    {
        foreach (var filter in filters)
            filter.Dispose();
        filters.Clear();
    }
}

// ── Adapter for existing filters ───────────────────────────────────────

/// <summary>
/// Wraps a legacy filter (one that operates on Span&lt;byte&gt;) as an IVideoFilterNode.
/// Bridges existing BrightnessContrastFilter, SepiaFilter, etc. into the pipeline.
/// </summary>
public sealed class LegacyYuvFilterAdapter : IVideoFilterNode
{
    private readonly string name;
    private readonly Action<Span<byte>> processYPlane;
    private readonly Action<Span<byte>, Span<byte>>? processUvPlanes;

    /// <summary>
    /// Creates an adapter for a filter that operates on Y plane only.
    /// </summary>
    public LegacyYuvFilterAdapter(string name, Action<Span<byte>> processYPlane)
    {
        this.name = name;
        this.processYPlane = processYPlane;
    }

    /// <summary>
    /// Creates an adapter for a filter that operates on Y, U, and V planes.
    /// </summary>
    public LegacyYuvFilterAdapter(
        string name,
        Action<Span<byte>> processYPlane,
        Action<Span<byte>, Span<byte>> processUvPlanes)
    {
        this.name = name;
        this.processYPlane = processYPlane;
        this.processUvPlanes = processUvPlanes;
    }

    public string Name => name;

    public FilterMediaFormat[] GetSupportedInputFormats()
        => [FilterMediaFormat.Video(0, 0, PixelFormat.Yuv420P)];

    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat)
        => inputFormat; // passthrough — same resolution and format

    public VideoFrame Process(VideoFrame frame)
    {
        processYPlane(frame.YPlane.Span);
        if (processUvPlanes != null)
            processUvPlanes(frame.UPlane.Span, frame.VPlane.Span);
        return frame;
    }

    public void Dispose() { }
}

/// <summary>
/// Wraps a legacy RGBA filter as an IVideoFilterNode.
/// </summary>
public sealed class LegacyRgbaFilterAdapter : IVideoFilterNode
{
    private readonly string name;
    private readonly Action<Span<byte>> processRgba;

    public LegacyRgbaFilterAdapter(string name, Action<Span<byte>> processRgba)
    {
        this.name = name;
        this.processRgba = processRgba;
    }

    public string Name => name;

    public FilterMediaFormat[] GetSupportedInputFormats()
        => [FilterMediaFormat.Video(0, 0, PixelFormat.Rgba)];

    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat) => inputFormat;

    public VideoFrame Process(VideoFrame frame)
    {
        // RGBA filters operate on a single packed plane stored in YPlane
        processRgba(frame.YPlane.Span);
        return frame;
    }

    public void Dispose() { }
}

// ── Built-in Filter Nodes ──────────────────────────────────────────────

/// <summary>
/// Scale filter — resizes video frames to a target resolution.
/// Uses bilinear interpolation. Placeholder for more advanced scaling algorithms.
/// </summary>
public sealed class ScaleFilterNode : IVideoFilterNode
{
    private readonly int targetWidth;
    private readonly int targetHeight;

    public ScaleFilterNode(int width, int height)
    {
        targetWidth = width;
        targetHeight = height;
    }

    public string Name => $"scale={targetWidth}x{targetHeight}";

    public FilterMediaFormat[] GetSupportedInputFormats()
        => [FilterMediaFormat.Video(0, 0, PixelFormat.Yuv420P)];

    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat)
        => inputFormat with { Width = targetWidth, Height = targetHeight };

    public VideoFrame Process(VideoFrame frame)
    {
        if (frame.Width == targetWidth && frame.Height == targetHeight)
            return frame;

        // Scale using bilinear interpolation
        var scaled = VideoFrame.AllocatePlanar420(targetWidth, targetHeight, frame.Pts);
        BilinearScaleY(frame.YPlane.Span, frame.Width, frame.Height, frame.YStride,
                       scaled.YPlane.Span, targetWidth, targetHeight, scaled.YStride);

        int srcChromaW = frame.Width / 2;
        int srcChromaH = frame.Height / 2;
        int dstChromaW = targetWidth / 2;
        int dstChromaH = targetHeight / 2;

        BilinearScaleY(frame.UPlane.Span, srcChromaW, srcChromaH, frame.UStride,
                       scaled.UPlane.Span, dstChromaW, dstChromaH, scaled.UStride);
        BilinearScaleY(frame.VPlane.Span, srcChromaW, srcChromaH, frame.VStride,
                       scaled.VPlane.Span, dstChromaW, dstChromaH, scaled.VStride);

        frame.Dispose();
        return scaled;
    }

    private static void BilinearScaleY(
        ReadOnlySpan<byte> src, int srcW, int srcH, int srcStride,
        Span<byte> dst, int dstW, int dstH, int dstStride)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float srcY = y * yRatio;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float yFrac = srcY - y0;

            var srcRow0 = src.Slice(y0 * srcStride, srcW);
            var srcRow1 = src.Slice(y1 * srcStride, srcW);
            var dstRow = dst.Slice(y * dstStride, dstW);

            for (int x = 0; x < dstW; x++)
            {
                float srcX = x * xRatio;
                int x0 = (int)srcX;
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float xFrac = srcX - x0;

                float top = srcRow0[x0] * (1 - xFrac) + srcRow0[x1] * xFrac;
                float bot = srcRow1[x0] * (1 - xFrac) + srcRow1[x1] * xFrac;
                dstRow[x] = (byte)Math.Clamp(top * (1 - yFrac) + bot * yFrac, 0, 255);
            }
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Null filter — passes frames through unchanged. Useful for testing.
/// </summary>
public sealed class NullFilterNode : IVideoFilterNode
{
    public string Name => "null";
    public FilterMediaFormat[] GetSupportedInputFormats() => [];
    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat) => inputFormat;
    public VideoFrame Process(VideoFrame frame) => frame;
    public void Dispose() { }
}

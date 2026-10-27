// FormatConvertFilterNode — pipeline video filter that converts pixel formats.
// Uses ScalingContext for the actual conversion work. Inserts into FilterGraph
// chains when format negotiation requires a format change between two filters.

using System;
using System.Buffers;
using MediaKernel.Core.Filters;
using MediaKernel.Core.PixelConversion;

namespace MediaKernel.Core.PixelConversion;

/// <summary>
/// Video filter node that converts pixel format and/or color space.
/// Wraps a ScalingContext for zero-allocation steady-state conversion.
/// </summary>
public sealed class FormatConvertFilterNode : IVideoFilterNode
{
    private readonly PixelFormat targetFormat;
    private readonly ColorSpace colorSpace;
    private readonly ColorRange colorRange;

    private ScalingContext? context;
    private byte[]? outputBuffer;
    private int lastWidth;
    private int lastHeight;

    public FormatConvertFilterNode(
        PixelFormat targetFormat,
        ColorSpace colorSpace = ColorSpace.Bt709,
        ColorRange colorRange = ColorRange.Limited)
    {
        this.targetFormat = targetFormat;
        this.colorSpace = colorSpace;
        this.colorRange = colorRange;
    }

    public string Name => $"format={targetFormat}";

    public FilterMediaFormat[] GetSupportedInputFormats()
    {
        // Accept any video format — we'll convert from whatever comes in
        return [
            FilterMediaFormat.Video(0, 0, PixelFormat.Yuv420P),
            FilterMediaFormat.Video(0, 0, PixelFormat.Yuv420P10),
            FilterMediaFormat.Video(0, 0, PixelFormat.Nv12),
            FilterMediaFormat.Video(0, 0, PixelFormat.Bgra),
        ];
    }

    public FilterMediaFormat GetOutputFormat(FilterMediaFormat inputFormat)
        => inputFormat with { PixelFormat = targetFormat };

    public VideoFrame Process(VideoFrame frame)
    {
        if (frame.Format == targetFormat)
            return frame; // Already in target format

        EnsureContext(frame.Width, frame.Height, frame.Format);

        // Allocate output frame
        var dstDesc = PixelFormatDescriptor.Get(targetFormat);
        int totalSize = dstDesc.GetTotalBufferSize(frame.Width, frame.Height);

        // Reuse or allocate output buffer
        if (outputBuffer == null || outputBuffer.Length < totalSize)
        {
            if (outputBuffer != null)
                ArrayPool<byte>.Shared.Return(outputBuffer);
            outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        }

        // Build source FrameBuffer from VideoFrame planes
        var srcBuffer = new FrameBuffer(
            frame.YPlane.Span, frame.YStride,
            frame.UPlane.Span, frame.UStride,
            frame.VPlane.Span, frame.VStride);

        // Build destination FrameBuffer
        FrameBuffer dstBuffer;
        if (dstDesc.Layout == PixelLayout.Packed)
        {
            int stride = dstDesc.GetMinStride(frame.Width, 0);
            dstBuffer = new FrameBuffer(outputBuffer.AsSpan(0, stride * frame.Height), stride);
        }
        else if (dstDesc.Layout == PixelLayout.SemiPlanar)
        {
            int ySize = frame.Width * dstDesc.BytesPerSample * frame.Height;
            int chromaHeight = dstDesc.GetPlaneHeight(frame.Height, 1);
            int uvSize = frame.Width * dstDesc.BytesPerSample * chromaHeight;
            dstBuffer = new FrameBuffer(
                outputBuffer.AsSpan(0, ySize), frame.Width * dstDesc.BytesPerSample,
                outputBuffer.AsSpan(ySize, uvSize), frame.Width * dstDesc.BytesPerSample);
        }
        else
        {
            // Planar
            int yStride = frame.Width * dstDesc.BytesPerSample;
            int ySize = yStride * frame.Height;
            int chromaWidth = dstDesc.GetPlaneWidth(frame.Width, 1);
            int chromaHeight = dstDesc.GetPlaneHeight(frame.Height, 1);
            int chromaStride = chromaWidth * dstDesc.BytesPerSample;
            int chromaSize = chromaStride * chromaHeight;

            dstBuffer = new FrameBuffer(
                outputBuffer.AsSpan(0, ySize), yStride,
                outputBuffer.AsSpan(ySize, chromaSize), chromaStride,
                outputBuffer.AsSpan(ySize + chromaSize, chromaSize), chromaStride);
        }

        context!.Convert(in srcBuffer, in dstBuffer);

        // Create output VideoFrame
        // For BGRA output, store in YPlane (packed format convention)
        VideoFrame result;
        if (dstDesc.Layout == PixelLayout.Packed)
        {
            int stride = dstDesc.GetMinStride(frame.Width, 0);
            result = new VideoFrame
            {
                Width = frame.Width,
                Height = frame.Height,
                Format = targetFormat,
                Pts = frame.Pts,
                YPlane = outputBuffer.AsMemory(0, stride * frame.Height),
                YStride = stride,
            };
        }
        else
        {
            int yStride = frame.Width * dstDesc.BytesPerSample;
            int ySize = yStride * frame.Height;
            int chromaWidth = dstDesc.GetPlaneWidth(frame.Width, 1);
            int chromaHeight = dstDesc.GetPlaneHeight(frame.Height, 1);
            int chromaStride = chromaWidth * dstDesc.BytesPerSample;
            int chromaSize = chromaStride * chromaHeight;

            result = new VideoFrame
            {
                Width = frame.Width,
                Height = frame.Height,
                Format = targetFormat,
                Pts = frame.Pts,
                YPlane = outputBuffer.AsMemory(0, ySize),
                YStride = yStride,
                UPlane = outputBuffer.AsMemory(ySize, chromaSize),
                UStride = chromaStride,
                VPlane = outputBuffer.AsMemory(ySize + chromaSize, chromaSize),
                VStride = chromaStride,
            };
        }

        frame.Dispose();
        return result;
    }

    private void EnsureContext(int width, int height, PixelFormat srcFormat)
    {
        if (context != null && lastWidth == width && lastHeight == height)
            return;

        context?.Dispose();
        context = new ScalingContext(
            width, height, srcFormat,
            width, height, targetFormat,
            ScalingAlgorithm.Point,
            colorSpace, colorRange);
        lastWidth = width;
        lastHeight = height;
    }

    public void Dispose()
    {
        context?.Dispose();
        if (outputBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
            outputBuffer = null;
        }
    }
}

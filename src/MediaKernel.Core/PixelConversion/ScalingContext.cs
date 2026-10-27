// ScalingContext — the unified pixel format conversion and scaling API.
// Equivalent to FFmpeg's SwsContext: create once, convert many frames, dispose.
// Pre-allocates intermediate buffers on construction for zero-allocation Convert() calls.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using MediaKernel.Core.PixelConversion.FormatConversion;

namespace MediaKernel.Core.PixelConversion;

/// <summary>
/// Scaling algorithm for image resizing.
/// </summary>
public enum ScalingAlgorithm
{
    /// <summary>No scaling — format conversion only (fastest).</summary>
    Point,

    /// <summary>Nearest neighbor — fast, no interpolation, good for pixel art.</summary>
    Nearest,

    /// <summary>Bilinear interpolation — fast, acceptable quality.</summary>
    Bilinear,

    /// <summary>Bicubic interpolation — good quality, reasonable speed.</summary>
    Bicubic,

    /// <summary>Lanczos 3-lobe — high quality, best for downscaling.</summary>
    Lanczos3,

    /// <summary>Area averaging — best for significant downscaling.</summary>
    Area,
}

/// <summary>
/// Reusable context for pixel format conversion and/or image scaling.
/// Create once, call <see cref="Convert"/> for each frame, dispose when done.
/// Thread-safety: each thread must have its own ScalingContext instance.
/// </summary>
public sealed class ScalingContext : IDisposable
{
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public PixelFormat SourceFormat { get; }

    public int DestinationWidth { get; }
    public int DestinationHeight { get; }
    public PixelFormat DestinationFormat { get; }

    public ScalingAlgorithm Algorithm { get; }
    public ColorSpace ColorSpace { get; }
    public ColorRange ColorRange { get; }

    private readonly ColorMatrix colorMatrix;
    private readonly PixelFormatDescriptor srcDesc;
    private readonly PixelFormatDescriptor dstDesc;
    private readonly bool needsScaling;
    private readonly ConversionPath conversionPath;

    // Pre-allocated intermediate buffers
    private byte[]? intermediateBuffer;

    /// <summary>
    /// Create a new scaling/conversion context.
    /// </summary>
    public ScalingContext(
        int srcWidth, int srcHeight, PixelFormat srcFormat,
        int dstWidth, int dstHeight, PixelFormat dstFormat,
        ScalingAlgorithm algorithm = ScalingAlgorithm.Bilinear,
        ColorSpace colorSpace = ColorSpace.Bt709,
        ColorRange colorRange = ColorRange.Limited)
    {
        SourceWidth = srcWidth;
        SourceHeight = srcHeight;
        SourceFormat = srcFormat;
        DestinationWidth = dstWidth;
        DestinationHeight = dstHeight;
        DestinationFormat = dstFormat;
        Algorithm = algorithm;
        ColorSpace = colorSpace;
        ColorRange = colorRange;

        srcDesc = PixelFormatDescriptor.Get(srcFormat);
        dstDesc = PixelFormatDescriptor.Get(dstFormat);
        colorMatrix = ColorMatrix.Get(colorSpace, colorRange);
        needsScaling = (srcWidth != dstWidth || srcHeight != dstHeight);
        conversionPath = DetermineConversionPath();

        // Pre-allocate intermediate buffers if needed
        AllocateIntermediateBuffers();
    }

    /// <summary>
    /// Convert pixel data from source format/size to destination format/size.
    /// Zero-allocation after context creation.
    /// </summary>
    /// <param name="source">Source frame data containing all planes.</param>
    /// <param name="destination">Destination buffer for converted data.</param>
    public void Convert(in FrameBuffer source, in FrameBuffer destination)
    {
        if (needsScaling)
            throw new NotSupportedException(
                $"Scaling from {SourceWidth}x{SourceHeight} to {DestinationWidth}x{DestinationHeight} " +
                "is not yet implemented. Use Point algorithm for format-only conversion.");

        switch (conversionPath)
        {
            case ConversionPath.DirectCopy:
                CopyDirect(in source, in destination);
                break;

            case ConversionPath.Yuv420PToBgra:
                YuvToRgbConvert.Yuv420PToBgra(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    source.Plane2, source.Stride2,
                    destination.Plane0, destination.Stride0,
                    SourceWidth, SourceHeight, in colorMatrix);
                break;

            case ConversionPath.Yuv420P10ToBgra:
                YuvToRgbConvert.Yuv420P10ToBgra(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    source.Plane2, source.Stride2,
                    destination.Plane0, destination.Stride0,
                    SourceWidth, SourceHeight, in colorMatrix);
                break;

            case ConversionPath.BgraToYuv420P:
                RgbToYuvConvert.BgraToYuv420P(
                    source.Plane0, source.Stride0,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    destination.Plane2, destination.Stride2,
                    SourceWidth, SourceHeight, in colorMatrix);
                break;

            case ConversionPath.RgbaToYuv420P:
                RgbToYuvConvert.RgbaToYuv420P(
                    source.Plane0, source.Stride0,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    destination.Plane2, destination.Stride2,
                    SourceWidth, SourceHeight, in colorMatrix);
                break;

            case ConversionPath.Nv12ToYuv420P:
                SemiPlanarConvert.Nv12ToYuv420P(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    destination.Plane2, destination.Stride2,
                    SourceWidth, SourceHeight);
                break;

            case ConversionPath.Yuv420PToNv12:
                SemiPlanarConvert.Yuv420PToNv12(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    source.Plane2, source.Stride2,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    SourceWidth, SourceHeight);
                break;

            case ConversionPath.P010ToYuv420P10:
                SemiPlanarConvert.P010ToYuv420P10(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    destination.Plane2, destination.Stride2,
                    SourceWidth, SourceHeight);
                break;

            case ConversionPath.Yuv420P10ToP010:
                SemiPlanarConvert.Yuv420P10ToP010(
                    source.Plane0, source.Stride0,
                    source.Plane1, source.Stride1,
                    source.Plane2, source.Stride2,
                    destination.Plane0, destination.Stride0,
                    destination.Plane1, destination.Stride1,
                    SourceWidth, SourceHeight);
                break;

            case ConversionPath.Nv12ToBgra:
                ConvertNv12ToBgraViaIntermediate(in source, in destination);
                break;

            case ConversionPath.GrayToBgra:
                YuvToRgbConvert.GrayToBgra(
                    source.Plane0, source.Stride0,
                    destination.Plane0, destination.Stride0,
                    SourceWidth, SourceHeight);
                break;

            case ConversionPath.Yuv420P10ToYuv420P:
                ConvertYuv420P10ToYuv420P(in source, in destination);
                break;

            case ConversionPath.Yuv420PToYuv420P10:
                ConvertYuv420PToYuv420P10(in source, in destination);
                break;

            default:
                throw new NotSupportedException(
                    $"Conversion from {SourceFormat} to {DestinationFormat} is not supported.");
        }
    }

    public void Dispose()
    {
        if (intermediateBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(intermediateBuffer);
            intermediateBuffer = null;
        }
    }

    // ── Conversion path determination ────────────────────────────────

    private enum ConversionPath
    {
        Unsupported,
        DirectCopy,
        Yuv420PToBgra,
        Yuv420P10ToBgra,
        BgraToYuv420P,
        RgbaToYuv420P,
        Nv12ToYuv420P,
        Yuv420PToNv12,
        P010ToYuv420P10,
        Yuv420P10ToP010,
        Nv12ToBgra,
        GrayToBgra,
        Yuv420P10ToYuv420P,
        Yuv420PToYuv420P10,
    }

    private ConversionPath DetermineConversionPath()
    {
        if (SourceFormat == DestinationFormat && !needsScaling)
            return ConversionPath.DirectCopy;

        return (SourceFormat, DestinationFormat) switch
        {
            (PixelFormat.Yuv420P, PixelFormat.Bgra) => ConversionPath.Yuv420PToBgra,
            (PixelFormat.Yuv420P, PixelFormat.Rgba) => ConversionPath.Yuv420PToBgra, // Same math, different pack order (TODO: separate)
            (PixelFormat.Yuv420P10, PixelFormat.Bgra) => ConversionPath.Yuv420P10ToBgra,
            (PixelFormat.Bgra, PixelFormat.Yuv420P) => ConversionPath.BgraToYuv420P,
            (PixelFormat.Rgba, PixelFormat.Yuv420P) => ConversionPath.RgbaToYuv420P,
            (PixelFormat.Nv12, PixelFormat.Yuv420P) => ConversionPath.Nv12ToYuv420P,
            (PixelFormat.Nv21, PixelFormat.Yuv420P) => ConversionPath.Nv12ToYuv420P, // NV21 handled by swapped planes
            (PixelFormat.Yuv420P, PixelFormat.Nv12) => ConversionPath.Yuv420PToNv12,
            (PixelFormat.P010, PixelFormat.Yuv420P10) => ConversionPath.P010ToYuv420P10,
            (PixelFormat.Yuv420P10, PixelFormat.P010) => ConversionPath.Yuv420P10ToP010,
            (PixelFormat.Nv12, PixelFormat.Bgra) => ConversionPath.Nv12ToBgra,
            (PixelFormat.Gray8, PixelFormat.Bgra) => ConversionPath.GrayToBgra,
            (PixelFormat.Yuv420P10, PixelFormat.Yuv420P) => ConversionPath.Yuv420P10ToYuv420P,
            (PixelFormat.Yuv420P, PixelFormat.Yuv420P10) => ConversionPath.Yuv420PToYuv420P10,
            _ => ConversionPath.Unsupported,
        };
    }

    private void AllocateIntermediateBuffers()
    {
        // NV12→BGRA needs intermediate YUV420P buffer
        if (conversionPath == ConversionPath.Nv12ToBgra)
        {
            int ySize = SourceWidth * SourceHeight;
            int chromaSize = ((SourceWidth + 1) >> 1) * ((SourceHeight + 1) >> 1);
            intermediateBuffer = ArrayPool<byte>.Shared.Rent(ySize + 2 * chromaSize);
        }
    }

    // ── Multi-step conversion helpers ────────────────────────────────

    private void ConvertNv12ToBgraViaIntermediate(in FrameBuffer source, in FrameBuffer destination)
    {
        if (intermediateBuffer == null)
            throw new InvalidOperationException("Intermediate buffer not allocated");

        int ySize = SourceWidth * SourceHeight;
        int chromaWidth = (SourceWidth + 1) >> 1;
        int chromaHeight = (SourceHeight + 1) >> 1;
        int chromaSize = chromaWidth * chromaHeight;

        var yInter = intermediateBuffer.AsSpan(0, ySize);
        var uInter = intermediateBuffer.AsSpan(ySize, chromaSize);
        var vInter = intermediateBuffer.AsSpan(ySize + chromaSize, chromaSize);

        // Step 1: NV12 → YUV420P (deinterleave)
        SemiPlanarConvert.Nv12ToYuv420P(
            source.Plane0, source.Stride0,
            source.Plane1, source.Stride1,
            yInter, SourceWidth,
            uInter, chromaWidth,
            vInter, chromaWidth,
            SourceWidth, SourceHeight);

        // Step 2: YUV420P → BGRA
        YuvToRgbConvert.Yuv420PToBgra(
            yInter, SourceWidth,
            uInter, chromaWidth,
            vInter, chromaWidth,
            destination.Plane0, destination.Stride0,
            SourceWidth, SourceHeight, in colorMatrix);
    }

    private void ConvertYuv420P10ToYuv420P(in FrameBuffer source, in FrameBuffer destination)
    {
        int chromaWidth = (SourceWidth + 1) >> 1;
        int chromaHeight = (SourceHeight + 1) >> 1;

        // Convert each plane: 10-bit → 8-bit
        BitDepthConvert.Convert10To8(source.Plane0, source.Stride0, destination.Plane0, destination.Stride0, SourceWidth, SourceHeight);
        BitDepthConvert.Convert10To8(source.Plane1, source.Stride1, destination.Plane1, destination.Stride1, chromaWidth, chromaHeight);
        BitDepthConvert.Convert10To8(source.Plane2, source.Stride2, destination.Plane2, destination.Stride2, chromaWidth, chromaHeight);
    }

    private void ConvertYuv420PToYuv420P10(in FrameBuffer source, in FrameBuffer destination)
    {
        int chromaWidth = (SourceWidth + 1) >> 1;
        int chromaHeight = (SourceHeight + 1) >> 1;

        BitDepthConvert.Convert8To10(source.Plane0, source.Stride0, destination.Plane0, destination.Stride0, SourceWidth, SourceHeight);
        BitDepthConvert.Convert8To10(source.Plane1, source.Stride1, destination.Plane1, destination.Stride1, chromaWidth, chromaHeight);
        BitDepthConvert.Convert8To10(source.Plane2, source.Stride2, destination.Plane2, destination.Stride2, chromaWidth, chromaHeight);
    }

    private void CopyDirect(in FrameBuffer source, in FrameBuffer destination)
    {
        for (int plane = 0; plane < srcDesc.PlaneCount; plane++)
        {
            int planeWidth = srcDesc.GetPlaneWidth(SourceWidth, plane) * srcDesc.BytesPerSample;
            int planeHeight = srcDesc.GetPlaneHeight(SourceHeight, plane);

            // Handle semi-planar chroma plane (2 components interleaved)
            if (srcDesc.Layout == PixelLayout.SemiPlanar && plane == 1)
                planeWidth *= 2;

            ReadOnlySpan<byte> srcPlane = plane switch
            {
                0 => source.Plane0,
                1 => source.Plane1,
                2 => source.Plane2,
                _ => ReadOnlySpan<byte>.Empty
            };
            Span<byte> dstPlane = plane switch
            {
                0 => destination.Plane0,
                1 => destination.Plane1,
                2 => destination.Plane2,
                _ => Span<byte>.Empty
            };
            int srcStride = plane switch { 0 => source.Stride0, 1 => source.Stride1, 2 => source.Stride2, _ => 0 };
            int dstStride = plane switch { 0 => destination.Stride0, 1 => destination.Stride1, 2 => destination.Stride2, _ => 0 };

            SemiPlanarConvert.CopyPlane(srcPlane, srcStride, dstPlane, dstStride,
                planeWidth, planeHeight);
        }
    }
}

/// <summary>
/// Describes the memory layout of a frame for conversion.
/// Wraps up to 3 planes with their strides. For packed formats, only Plane0 is used.
/// </summary>
public readonly ref struct FrameBuffer
{
    public readonly Span<byte> Plane0;
    public readonly Span<byte> Plane1;
    public readonly Span<byte> Plane2;
    public readonly int Stride0;
    public readonly int Stride1;
    public readonly int Stride2;

    /// <summary>
    /// Create a frame buffer for packed formats (single plane).
    /// </summary>
    public FrameBuffer(Span<byte> plane0, int stride0)
    {
        Plane0 = plane0;
        Stride0 = stride0;
        Plane1 = Span<byte>.Empty;
        Plane2 = Span<byte>.Empty;
        Stride1 = 0;
        Stride2 = 0;
    }

    /// <summary>
    /// Create a frame buffer for semi-planar formats (two planes).
    /// </summary>
    public FrameBuffer(Span<byte> plane0, int stride0, Span<byte> plane1, int stride1)
    {
        Plane0 = plane0;
        Stride0 = stride0;
        Plane1 = plane1;
        Stride1 = stride1;
        Plane2 = Span<byte>.Empty;
        Stride2 = 0;
    }

    /// <summary>
    /// Create a frame buffer for planar formats (three planes).
    /// </summary>
    public FrameBuffer(Span<byte> plane0, int stride0, Span<byte> plane1, int stride1, Span<byte> plane2, int stride2)
    {
        Plane0 = plane0;
        Stride0 = stride0;
        Plane1 = plane1;
        Stride1 = stride1;
        Plane2 = plane2;
        Stride2 = stride2;
    }

    /// <summary>
    /// Create a read-only frame buffer view from ReadOnlySpan data.
    /// </summary>
    public static FrameBuffer FromReadOnly(ReadOnlySpan<byte> plane0, int stride0)
    {
        return new FrameBuffer(
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plane0), plane0.Length),
            stride0);
    }

    public static FrameBuffer FromReadOnly(
        ReadOnlySpan<byte> plane0, int stride0,
        ReadOnlySpan<byte> plane1, int stride1,
        ReadOnlySpan<byte> plane2, int stride2)
    {
        return new FrameBuffer(
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plane0), plane0.Length),
            stride0,
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plane1), plane1.Length),
            stride1,
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plane2), plane2.Length),
            stride2);
    }
}

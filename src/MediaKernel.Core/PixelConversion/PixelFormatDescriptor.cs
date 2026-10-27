// Pixel format metadata system — describes memory layout, bit depth, and subsampling
// for every supported pixel format. Used by ScalingContext to determine conversion paths.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.PixelConversion;

/// <summary>
/// How pixel components are stored in memory.
/// </summary>
public enum PixelLayout
{
    /// <summary>Each component in its own contiguous plane (e.g., YUV420P: Y[], U[], V[]).</summary>
    Planar,

    /// <summary>Luma in one plane, chroma interleaved in a second plane (e.g., NV12: Y[], UV[]).</summary>
    SemiPlanar,

    /// <summary>All components packed together per-pixel (e.g., BGRA: B,G,R,A,B,G,R,A,...).</summary>
    Packed,
}

/// <summary>
/// Chroma subsampling ratio relative to luma.
/// </summary>
public enum ChromaSubsampling
{
    /// <summary>No chroma (grayscale or RGB).</summary>
    None,

    /// <summary>4:4:4 — no subsampling, chroma same resolution as luma.</summary>
    Yuv444,

    /// <summary>4:2:2 — chroma half width, full height.</summary>
    Yuv422,

    /// <summary>4:2:0 — chroma half width, half height.</summary>
    Yuv420,

    /// <summary>4:1:1 — chroma quarter width, full height.</summary>
    Yuv411,
}

/// <summary>
/// Color model of the pixel data.
/// </summary>
public enum ColorModel
{
    /// <summary>Luma + chroma difference (Y, Cb/U, Cr/V).</summary>
    Yuv,

    /// <summary>Red, Green, Blue (and optionally Alpha).</summary>
    Rgb,

    /// <summary>Grayscale (single luminance component).</summary>
    Gray,
}

/// <summary>
/// Immutable descriptor for a pixel format. Provides all metadata needed
/// to compute buffer sizes, stride requirements, and conversion paths.
/// </summary>
public readonly struct PixelFormatDescriptor
{
    /// <summary>The pixel format this describes.</summary>
    public readonly PixelFormat Format;

    /// <summary>Short FFmpeg-style name (e.g., "yuv420p", "bgra").</summary>
    public readonly string Name;

    /// <summary>Bits per component (8, 10, 12, 16).</summary>
    public readonly int BitsPerComponent;

    /// <summary>Number of color components (1=gray, 3=YUV/RGB, 4=RGBA/BGRA).</summary>
    public readonly int ComponentCount;

    /// <summary>How components are arranged in memory.</summary>
    public readonly PixelLayout Layout;

    /// <summary>Chroma subsampling ratio.</summary>
    public readonly ChromaSubsampling Subsampling;

    /// <summary>Color model (YUV, RGB, Gray).</summary>
    public readonly ColorModel ColorModel;

    /// <summary>Number of memory planes (1 for packed, 2 for semi-planar, 3 for YUV planar).</summary>
    public readonly int PlaneCount;

    /// <summary>log2 of horizontal chroma subsampling (0=none, 1=half).</summary>
    public readonly int ChromaWidthShift;

    /// <summary>log2 of vertical chroma subsampling (0=none, 1=half).</summary>
    public readonly int ChromaHeightShift;

    /// <summary>Whether the format has an alpha channel.</summary>
    public readonly bool HasAlpha;

    /// <summary>Bytes per pixel for packed formats, or bytes per sample for planar formats.</summary>
    public readonly int BytesPerSample;

    public PixelFormatDescriptor(
        PixelFormat format, string name,
        int bitsPerComponent, int componentCount,
        PixelLayout layout, ChromaSubsampling subsampling, ColorModel colorModel,
        int planeCount, int chromaWidthShift, int chromaHeightShift,
        bool hasAlpha, int bytesPerSample)
    {
        Format = format;
        Name = name;
        BitsPerComponent = bitsPerComponent;
        ComponentCount = componentCount;
        Layout = layout;
        Subsampling = subsampling;
        ColorModel = colorModel;
        PlaneCount = planeCount;
        ChromaWidthShift = chromaWidthShift;
        ChromaHeightShift = chromaHeightShift;
        HasAlpha = hasAlpha;
        BytesPerSample = bytesPerSample;
    }

    /// <summary>
    /// Computes the required buffer size for a single plane of this format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPlaneSize(int width, int height, int planeIndex)
    {
        int planeWidth = GetPlaneWidth(width, planeIndex);
        int planeHeight = GetPlaneHeight(height, planeIndex);
        return planeWidth * BytesPerSample * planeHeight;
    }

    /// <summary>
    /// Width of a specific plane, accounting for chroma subsampling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPlaneWidth(int lumaWidth, int planeIndex)
    {
        if (Layout == PixelLayout.Packed)
            return lumaWidth;

        // Plane 0 is always luma (full resolution)
        if (planeIndex == 0)
            return lumaWidth;

        // Chroma planes are subsampled horizontally
        // For semi-planar, plane 1 has width = lumaWidth (UV pairs occupy lumaWidth bytes at 8-bit)
        if (Layout == PixelLayout.SemiPlanar)
            return lumaWidth; // UV interleaved: each UV pair = 2 bytes per luma column pair

        return (lumaWidth + (1 << ChromaWidthShift) - 1) >> ChromaWidthShift;
    }

    /// <summary>
    /// Height of a specific plane, accounting for chroma subsampling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPlaneHeight(int lumaHeight, int planeIndex)
    {
        if (Layout == PixelLayout.Packed)
            return lumaHeight;

        if (planeIndex == 0)
            return lumaHeight;

        return (lumaHeight + (1 << ChromaHeightShift) - 1) >> ChromaHeightShift;
    }

    /// <summary>
    /// Total buffer size needed for all planes of an image at the given resolution.
    /// </summary>
    public int GetTotalBufferSize(int width, int height)
    {
        int total = 0;
        for (int i = 0; i < PlaneCount; i++)
            total += GetPlaneSize(width, height, i);
        return total;
    }

    /// <summary>
    /// Minimum stride (bytes per row) for a given plane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetMinStride(int lumaWidth, int planeIndex)
    {
        if (Layout == PixelLayout.Packed)
            return lumaWidth * ComponentCount * BytesPerSample;

        int planeWidth = GetPlaneWidth(lumaWidth, planeIndex);

        // Semi-planar chroma plane has 2 components interleaved
        if (Layout == PixelLayout.SemiPlanar && planeIndex == 1)
            return planeWidth * BytesPerSample;

        return planeWidth * BytesPerSample;
    }

    public override string ToString() => Name;

    // ─── Static Lookup Table ─────────────────────────────────────────

    private static readonly PixelFormatDescriptor[] descriptors = BuildDescriptorTable();

    /// <summary>
    /// Get the descriptor for a pixel format. Returns a descriptor with Format=Unknown for unsupported formats.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly PixelFormatDescriptor Get(PixelFormat format)
    {
        int index = (int)format;
        if ((uint)index < (uint)descriptors.Length)
            return ref descriptors[index];
        return ref descriptors[0]; // Unknown
    }

    private static PixelFormatDescriptor[] BuildDescriptorTable()
    {
        // Size to accommodate all enum values
        int maxValue = 0;
        foreach (PixelFormat pf in Enum.GetValues<PixelFormat>())
        {
            int val = (int)pf;
            if (val > maxValue) maxValue = val;
        }

        var table = new PixelFormatDescriptor[maxValue + 1];

        // Default all entries to Unknown
        table[0] = new(PixelFormat.Unknown, "unknown", 0, 0, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Gray, 0, 0, 0, false, 0);

        // ── Planar YUV 8-bit ──
        table[(int)PixelFormat.Yuv420P] = new(PixelFormat.Yuv420P, "yuv420p", 8, 3, PixelLayout.Planar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 3, 1, 1, false, 1);
        table[(int)PixelFormat.Yuv422P] = new(PixelFormat.Yuv422P, "yuv422p", 8, 3, PixelLayout.Planar, ChromaSubsampling.Yuv422, ColorModel.Yuv, 3, 1, 0, false, 1);
        table[(int)PixelFormat.Yuv444P] = new(PixelFormat.Yuv444P, "yuv444p", 8, 3, PixelLayout.Planar, ChromaSubsampling.Yuv444, ColorModel.Yuv, 3, 0, 0, false, 1);

        // ── Planar YUV 10-bit ──
        table[(int)PixelFormat.Yuv420P10] = new(PixelFormat.Yuv420P10, "yuv420p10le", 10, 3, PixelLayout.Planar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 3, 1, 1, false, 2);
        table[(int)PixelFormat.Yuv422P10] = new(PixelFormat.Yuv422P10, "yuv422p10le", 10, 3, PixelLayout.Planar, ChromaSubsampling.Yuv422, ColorModel.Yuv, 3, 1, 0, false, 2);
        table[(int)PixelFormat.Yuv444P10] = new(PixelFormat.Yuv444P10, "yuv444p10le", 10, 3, PixelLayout.Planar, ChromaSubsampling.Yuv444, ColorModel.Yuv, 3, 0, 0, false, 2);

        // ── Planar YUV 12-bit ──
        table[(int)PixelFormat.Yuv420P12] = new(PixelFormat.Yuv420P12, "yuv420p12le", 12, 3, PixelLayout.Planar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 3, 1, 1, false, 2);
        table[(int)PixelFormat.Yuv422P12] = new(PixelFormat.Yuv422P12, "yuv422p12le", 12, 3, PixelLayout.Planar, ChromaSubsampling.Yuv422, ColorModel.Yuv, 3, 1, 0, false, 2);
        table[(int)PixelFormat.Yuv444P12] = new(PixelFormat.Yuv444P12, "yuv444p12le", 12, 3, PixelLayout.Planar, ChromaSubsampling.Yuv444, ColorModel.Yuv, 3, 0, 0, false, 2);

        // ── Semi-planar ──
        table[(int)PixelFormat.Nv12] = new(PixelFormat.Nv12, "nv12", 8, 3, PixelLayout.SemiPlanar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 2, 1, 1, false, 1);
        table[(int)PixelFormat.Nv21] = new(PixelFormat.Nv21, "nv21", 8, 3, PixelLayout.SemiPlanar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 2, 1, 1, false, 1);
        table[(int)PixelFormat.P010] = new(PixelFormat.P010, "p010le", 10, 3, PixelLayout.SemiPlanar, ChromaSubsampling.Yuv420, ColorModel.Yuv, 2, 1, 1, false, 2);

        // ── Packed RGB ──
        table[(int)PixelFormat.Rgb24] = new(PixelFormat.Rgb24, "rgb24", 8, 3, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Rgb, 1, 0, 0, false, 1);
        table[(int)PixelFormat.Bgr24] = new(PixelFormat.Bgr24, "bgr24", 8, 3, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Rgb, 1, 0, 0, false, 1);
        table[(int)PixelFormat.Rgba] = new(PixelFormat.Rgba, "rgba", 8, 4, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Rgb, 1, 0, 0, true, 1);
        table[(int)PixelFormat.Bgra] = new(PixelFormat.Bgra, "bgra", 8, 4, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Rgb, 1, 0, 0, true, 1);

        // ── Packed YUV ──
        table[(int)PixelFormat.Yuyv422] = new(PixelFormat.Yuyv422, "yuyv422", 8, 3, PixelLayout.Packed, ChromaSubsampling.Yuv422, ColorModel.Yuv, 1, 1, 0, false, 1);
        table[(int)PixelFormat.Uyvy422] = new(PixelFormat.Uyvy422, "uyvy422", 8, 3, PixelLayout.Packed, ChromaSubsampling.Yuv422, ColorModel.Yuv, 1, 1, 0, false, 1);

        // ── Grayscale ──
        table[(int)PixelFormat.Gray8] = new(PixelFormat.Gray8, "gray", 8, 1, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Gray, 1, 0, 0, false, 1);
        table[(int)PixelFormat.Gray16] = new(PixelFormat.Gray16, "gray16le", 16, 1, PixelLayout.Packed, ChromaSubsampling.None, ColorModel.Gray, 1, 0, 0, false, 2);

        // ── Planar RGB ──
        table[(int)PixelFormat.Gbrp] = new(PixelFormat.Gbrp, "gbrp", 8, 3, PixelLayout.Planar, ChromaSubsampling.None, ColorModel.Rgb, 3, 0, 0, false, 1);
        table[(int)PixelFormat.Gbrp10] = new(PixelFormat.Gbrp10, "gbrp10le", 10, 3, PixelLayout.Planar, ChromaSubsampling.None, ColorModel.Rgb, 3, 0, 0, false, 2);

        return table;
    }
}

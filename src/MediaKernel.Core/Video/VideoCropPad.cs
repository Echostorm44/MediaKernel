// Video crop and pad filter
// Reference: VLC's modules/video_filter/croppadd.c

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Crop/pad region specification.
/// </summary>
public readonly struct CropRegion
{
    /// <summary>Pixels to crop/pad from the left edge.</summary>
    public int Left { get; init; }
    
    /// <summary>Pixels to crop/pad from the top edge.</summary>
    public int Top { get; init; }
    
    /// <summary>Pixels to crop/pad from the right edge.</summary>
    public int Right { get; init; }
    
    /// <summary>Pixels to crop/pad from the bottom edge.</summary>
    public int Bottom { get; init; }
    
    /// <summary>
    /// Creates a crop region with uniform values on all sides.
    /// </summary>
    public static CropRegion Uniform(int value) => new() { Left = value, Top = value, Right = value, Bottom = value };
    
    /// <summary>
    /// Creates a crop region with horizontal and vertical values.
    /// </summary>
    public static CropRegion Symmetric(int horizontal, int vertical) => 
        new() { Left = horizontal, Right = horizontal, Top = vertical, Bottom = vertical };
    
    /// <summary>
    /// Gets whether this region has any non-zero values.
    /// </summary>
    public bool IsActive => Left != 0 || Top != 0 || Right != 0 || Bottom != 0;
    
    /// <summary>
    /// Total horizontal change (left + right).
    /// </summary>
    public int TotalHorizontal => Left + Right;
    
    /// <summary>
    /// Total vertical change (top + bottom).
    /// </summary>
    public int TotalVertical => Top + Bottom;
}

/// <summary>
/// Pad color specification.
/// </summary>
public readonly struct PadColor
{
    /// <summary>Blue component (0-255).</summary>
    public byte Blue { get; init; }
    
    /// <summary>Green component (0-255).</summary>
    public byte Green { get; init; }
    
    /// <summary>Red component (0-255).</summary>
    public byte Red { get; init; }
    
    /// <summary>Alpha component (0-255, 255 = opaque).</summary>
    public byte Alpha { get; init; }
    
    /// <summary>Black color (opaque).</summary>
    public static PadColor Black => new() { Blue = 0, Green = 0, Red = 0, Alpha = 255 };
    
    /// <summary>White color (opaque).</summary>
    public static PadColor White => new() { Blue = 255, Green = 255, Red = 255, Alpha = 255 };
    
    /// <summary>Transparent black.</summary>
    public static PadColor Transparent => new() { Blue = 0, Green = 0, Red = 0, Alpha = 0 };
    
    /// <summary>
    /// Creates a grayscale color.
    /// </summary>
    public static PadColor Gray(byte value) => new() { Blue = value, Green = value, Red = value, Alpha = 255 };
    
    /// <summary>
    /// Creates an RGB color (opaque).
    /// </summary>
    public static PadColor Rgb(byte r, byte g, byte b) => new() { Blue = b, Green = g, Red = r, Alpha = 255 };
    
    /// <summary>
    /// Gets the BGRA value as a 32-bit integer.
    /// </summary>
    public uint ToBgra() => (uint)(Blue | (Green << 8) | (Red << 16) | (Alpha << 24));
}

/// <summary>
/// Video crop and pad filter for removing black bars or adding borders.
/// </summary>
/// <remarks>
/// Supports:
/// - Cropping from any edge (removes pixels)
/// - Padding to any edge (adds pixels with specified color)
/// - Combined crop+pad operations
/// - BGRA and planar YUV (I420) formats
/// - SIMD-optimized fill operations
/// </remarks>
public static class VideoCropPad
{
    /// <summary>
    /// Crops a BGRA image by removing pixels from the edges.
    /// </summary>
    /// <param name="source">Source pixel data (BGRA format, sourceWidth × sourceHeight × 4 bytes).</param>
    /// <param name="sourceWidth">Source image width.</param>
    /// <param name="sourceHeight">Source image height.</param>
    /// <param name="destination">Destination buffer (must be large enough for output).</param>
    /// <param name="crop">Crop region specifying pixels to remove from each edge.</param>
    /// <param name="outputWidth">Output width (sourceWidth - left - right).</param>
    /// <param name="outputHeight">Output height (sourceHeight - top - bottom).</param>
    public static void CropBgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, CropRegion crop,
        out int outputWidth, out int outputHeight)
    {
        outputWidth = sourceWidth - crop.Left - crop.Right;
        outputHeight = sourceHeight - crop.Top - crop.Bottom;
        
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            outputWidth = 0;
            outputHeight = 0;
            return;
        }
        
        int sourceStride = sourceWidth * 4;
        int destStride = outputWidth * 4;
        
        // Copy each row, skipping cropped regions
        int sourceOffset = (crop.Top * sourceWidth + crop.Left) * 4;
        int destOffset = 0;
        
        for (int y = 0; y < outputHeight; y++)
        {
            source.Slice(sourceOffset, destStride).CopyTo(destination.Slice(destOffset, destStride));
            sourceOffset += sourceStride;
            destOffset += destStride;
        }
    }
    
    /// <summary>
    /// Pads a BGRA image by adding pixels to the edges.
    /// </summary>
    /// <param name="source">Source pixel data (BGRA format).</param>
    /// <param name="sourceWidth">Source image width.</param>
    /// <param name="sourceHeight">Source image height.</param>
    /// <param name="destination">Destination buffer (must be outputWidth × outputHeight × 4 bytes).</param>
    /// <param name="pad">Pad region specifying pixels to add to each edge.</param>
    /// <param name="padColor">Color for the padded regions.</param>
    /// <param name="outputWidth">Output width (sourceWidth + left + right).</param>
    /// <param name="outputHeight">Output height (sourceHeight + top + bottom).</param>
    public static void PadBgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, CropRegion pad, PadColor padColor,
        out int outputWidth, out int outputHeight)
    {
        outputWidth = sourceWidth + pad.Left + pad.Right;
        outputHeight = sourceHeight + pad.Top + pad.Bottom;
        
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            outputWidth = 0;
            outputHeight = 0;
            return;
        }
        
        uint fillColor = padColor.ToBgra();
        int destStride = outputWidth * 4;
        int sourceStride = sourceWidth * 4;
        
        // Fill top padding rows
        FillBgra(destination[..(pad.Top * destStride)], fillColor);
        
        // Fill middle rows (left padding + source + right padding)
        int destOffset = pad.Top * destStride;
        int sourceOffset = 0;
        
        for (int y = 0; y < sourceHeight; y++)
        {
            // Left padding
            if (pad.Left > 0)
            {
                FillBgra(destination.Slice(destOffset, pad.Left * 4), fillColor);
            }
            
            // Source row
            source.Slice(sourceOffset, sourceStride).CopyTo(
                destination.Slice(destOffset + pad.Left * 4, sourceStride));
            
            // Right padding
            if (pad.Right > 0)
            {
                FillBgra(destination.Slice(destOffset + (pad.Left + sourceWidth) * 4, pad.Right * 4), fillColor);
            }
            
            destOffset += destStride;
            sourceOffset += sourceStride;
        }
        
        // Fill bottom padding rows
        if (pad.Bottom > 0)
        {
            FillBgra(destination.Slice(destOffset, pad.Bottom * destStride), fillColor);
        }
    }
    
    /// <summary>
    /// Crops a planar YUV (I420) image.
    /// </summary>
    /// <param name="sourceY">Source Y plane.</param>
    /// <param name="sourceU">Source U plane.</param>
    /// <param name="sourceV">Source V plane.</param>
    /// <param name="sourceWidth">Source width (must be even).</param>
    /// <param name="sourceHeight">Source height (must be even).</param>
    /// <param name="destY">Destination Y plane.</param>
    /// <param name="destU">Destination U plane.</param>
    /// <param name="destV">Destination V plane.</param>
    /// <param name="crop">Crop region (values should be even for chroma alignment).</param>
    /// <param name="outputWidth">Output width.</param>
    /// <param name="outputHeight">Output height.</param>
    public static void CropI420(
        ReadOnlySpan<byte> sourceY, ReadOnlySpan<byte> sourceU, ReadOnlySpan<byte> sourceV,
        int sourceWidth, int sourceHeight,
        Span<byte> destY, Span<byte> destU, Span<byte> destV,
        CropRegion crop, out int outputWidth, out int outputHeight)
    {
        // Ensure crop values are even for chroma alignment
        int cropLeft = crop.Left & ~1;
        int cropRight = crop.Right & ~1;
        int cropTop = crop.Top & ~1;
        int cropBottom = crop.Bottom & ~1;
        
        outputWidth = sourceWidth - cropLeft - cropRight;
        outputHeight = sourceHeight - cropTop - cropBottom;
        
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            outputWidth = 0;
            outputHeight = 0;
            return;
        }
        
        // Crop Y plane
        int ySourceOffset = cropTop * sourceWidth + cropLeft;
        int yDestOffset = 0;
        
        for (int y = 0; y < outputHeight; y++)
        {
            sourceY.Slice(ySourceOffset, outputWidth).CopyTo(destY.Slice(yDestOffset, outputWidth));
            ySourceOffset += sourceWidth;
            yDestOffset += outputWidth;
        }
        
        // Crop U and V planes (half resolution)
        int chromaSourceWidth = sourceWidth / 2;
        int chromaOutputWidth = outputWidth / 2;
        int chromaOutputHeight = outputHeight / 2;
        int chromaCropLeft = cropLeft / 2;
        int chromaCropTop = cropTop / 2;
        
        int uvSourceOffset = chromaCropTop * chromaSourceWidth + chromaCropLeft;
        int uvDestOffset = 0;
        
        for (int y = 0; y < chromaOutputHeight; y++)
        {
            sourceU.Slice(uvSourceOffset, chromaOutputWidth).CopyTo(destU.Slice(uvDestOffset, chromaOutputWidth));
            sourceV.Slice(uvSourceOffset, chromaOutputWidth).CopyTo(destV.Slice(uvDestOffset, chromaOutputWidth));
            uvSourceOffset += chromaSourceWidth;
            uvDestOffset += chromaOutputWidth;
        }
    }
    
    /// <summary>
    /// Pads a planar YUV (I420) image.
    /// </summary>
    /// <param name="sourceY">Source Y plane.</param>
    /// <param name="sourceU">Source U plane.</param>
    /// <param name="sourceV">Source V plane.</param>
    /// <param name="sourceWidth">Source width (must be even).</param>
    /// <param name="sourceHeight">Source height (must be even).</param>
    /// <param name="destY">Destination Y plane.</param>
    /// <param name="destU">Destination U plane.</param>
    /// <param name="destV">Destination V plane.</param>
    /// <param name="pad">Pad region (values should be even for chroma alignment).</param>
    /// <param name="yFill">Y component for fill color (16 = black, 235 = white for TV range).</param>
    /// <param name="uFill">U component for fill color (128 = neutral).</param>
    /// <param name="vFill">V component for fill color (128 = neutral).</param>
    /// <param name="outputWidth">Output width.</param>
    /// <param name="outputHeight">Output height.</param>
    public static void PadI420(
        ReadOnlySpan<byte> sourceY, ReadOnlySpan<byte> sourceU, ReadOnlySpan<byte> sourceV,
        int sourceWidth, int sourceHeight,
        Span<byte> destY, Span<byte> destU, Span<byte> destV,
        CropRegion pad, byte yFill, byte uFill, byte vFill,
        out int outputWidth, out int outputHeight)
    {
        // Ensure pad values are even for chroma alignment
        int padLeft = (pad.Left + 1) & ~1;
        int padRight = (pad.Right + 1) & ~1;
        int padTop = (pad.Top + 1) & ~1;
        int padBottom = (pad.Bottom + 1) & ~1;
        
        outputWidth = sourceWidth + padLeft + padRight;
        outputHeight = sourceHeight + padTop + padBottom;
        
        // Fill Y plane with padding
        destY.Fill(yFill);
        
        // Copy source Y into center
        int ySourceOffset = 0;
        int yDestOffset = padTop * outputWidth + padLeft;
        
        for (int y = 0; y < sourceHeight; y++)
        {
            sourceY.Slice(ySourceOffset, sourceWidth).CopyTo(destY.Slice(yDestOffset, sourceWidth));
            ySourceOffset += sourceWidth;
            yDestOffset += outputWidth;
        }
        
        // Fill U and V planes
        destU.Fill(uFill);
        destV.Fill(vFill);
        
        // Copy source U and V into center
        int chromaSourceWidth = sourceWidth / 2;
        int chromaSourceHeight = sourceHeight / 2;
        int chromaOutputWidth = outputWidth / 2;
        int chromaPadLeft = padLeft / 2;
        int chromaPadTop = padTop / 2;
        
        int uvSourceOffset = 0;
        int uvDestOffset = chromaPadTop * chromaOutputWidth + chromaPadLeft;
        
        for (int y = 0; y < chromaSourceHeight; y++)
        {
            sourceU.Slice(uvSourceOffset, chromaSourceWidth).CopyTo(destU.Slice(uvDestOffset, chromaSourceWidth));
            sourceV.Slice(uvSourceOffset, chromaSourceWidth).CopyTo(destV.Slice(uvDestOffset, chromaSourceWidth));
            uvSourceOffset += chromaSourceWidth;
            uvDestOffset += chromaOutputWidth;
        }
    }
    
    /// <summary>
    /// Calculates output dimensions after applying crop/pad.
    /// </summary>
    /// <param name="sourceWidth">Source width.</param>
    /// <param name="sourceHeight">Source height.</param>
    /// <param name="crop">Crop region (negative values pad instead).</param>
    /// <param name="outputWidth">Resulting width.</param>
    /// <param name="outputHeight">Resulting height.</param>
    /// <returns>True if output dimensions are valid (positive).</returns>
    public static bool CalculateOutputDimensions(
        int sourceWidth, int sourceHeight, CropRegion crop,
        out int outputWidth, out int outputHeight)
    {
        outputWidth = sourceWidth - crop.Left - crop.Right;
        outputHeight = sourceHeight - crop.Top - crop.Bottom;
        return outputWidth > 0 && outputHeight > 0;
    }
    
    /// <summary>
    /// Creates a crop region to remove letterbox/pillarbox black bars.
    /// </summary>
    /// <param name="sourceWidth">Source width.</param>
    /// <param name="sourceHeight">Source height.</param>
    /// <param name="sourceAspectRatio">Source display aspect ratio (e.g., 16/9).</param>
    /// <param name="targetAspectRatio">Target display aspect ratio (e.g., 4/3).</param>
    /// <returns>Crop region to achieve target aspect ratio.</returns>
    public static CropRegion CalculateAspectRatioCrop(
        int sourceWidth, int sourceHeight,
        double sourceAspectRatio, double targetAspectRatio)
    {
        if (Math.Abs(sourceAspectRatio - targetAspectRatio) < 0.001)
        {
            return new CropRegion(); // No crop needed
        }
        
        if (sourceAspectRatio > targetAspectRatio)
        {
            // Source is wider - crop sides (pillarbox removal)
            int targetWidth = (int)(sourceHeight * targetAspectRatio);
            int cropTotal = sourceWidth - targetWidth;
            int cropSide = cropTotal / 2;
            return new CropRegion { Left = cropSide, Right = cropTotal - cropSide };
        }
        else
        {
            // Source is taller - crop top/bottom (letterbox removal)
            int targetHeight = (int)(sourceWidth / targetAspectRatio);
            int cropTotal = sourceHeight - targetHeight;
            int cropVertical = cropTotal / 2;
            return new CropRegion { Top = cropVertical, Bottom = cropTotal - cropVertical };
        }
    }
    
    /// <summary>
    /// Creates a pad region to achieve a target aspect ratio.
    /// </summary>
    /// <param name="sourceWidth">Source width.</param>
    /// <param name="sourceHeight">Source height.</param>
    /// <param name="sourceAspectRatio">Source display aspect ratio.</param>
    /// <param name="targetAspectRatio">Target display aspect ratio.</param>
    /// <returns>Pad region to achieve target aspect ratio.</returns>
    public static CropRegion CalculateAspectRatioPad(
        int sourceWidth, int sourceHeight,
        double sourceAspectRatio, double targetAspectRatio)
    {
        if (Math.Abs(sourceAspectRatio - targetAspectRatio) < 0.001)
        {
            return new CropRegion(); // No padding needed
        }
        
        if (sourceAspectRatio < targetAspectRatio)
        {
            // Source is narrower - add pillarbox (sides)
            int targetWidth = (int)(sourceHeight * targetAspectRatio);
            int padTotal = targetWidth - sourceWidth;
            int padSide = padTotal / 2;
            return new CropRegion { Left = padSide, Right = padTotal - padSide };
        }
        else
        {
            // Source is shorter - add letterbox (top/bottom)
            int targetHeight = (int)(sourceWidth / targetAspectRatio);
            int padTotal = targetHeight - sourceHeight;
            int padVertical = padTotal / 2;
            return new CropRegion { Top = padVertical, Bottom = padTotal - padVertical };
        }
    }
    
    /// <summary>
    /// Fills a BGRA buffer with a solid color using SIMD when available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillBgra(Span<byte> destination, uint color)
    {
        if (destination.IsEmpty)
            return;
        
        int pixelCount = destination.Length / 4;
        
        if (Avx2.IsSupported && pixelCount >= 8)
        {
            FillBgraAvx2(destination, color);
        }
        else if (Sse2.IsSupported && pixelCount >= 4)
        {
            FillBgraSse2(destination, color);
        }
        else
        {
            FillBgraScalar(destination, color);
        }
    }
    
    /// <summary>
    /// AVX2 optimized BGRA fill.
    /// </summary>
    private static unsafe void FillBgraAvx2(Span<byte> destination, uint color)
    {
        var fillVector = Vector256.Create(color);
        int offset = 0;
        
        fixed (byte* destPtr = destination)
        {
            // Process 8 pixels at a time
            while (offset + 32 <= destination.Length)
            {
                Avx.Store((uint*)(destPtr + offset), fillVector);
                offset += 32;
            }
        }
        
        // Handle remaining pixels
        while (offset + 4 <= destination.Length)
        {
            destination[offset] = (byte)color;
            destination[offset + 1] = (byte)(color >> 8);
            destination[offset + 2] = (byte)(color >> 16);
            destination[offset + 3] = (byte)(color >> 24);
            offset += 4;
        }
    }
    
    /// <summary>
    /// SSE2 optimized BGRA fill.
    /// </summary>
    private static unsafe void FillBgraSse2(Span<byte> destination, uint color)
    {
        var fillVector = Vector128.Create(color);
        int offset = 0;
        
        fixed (byte* destPtr = destination)
        {
            // Process 4 pixels at a time
            while (offset + 16 <= destination.Length)
            {
                Sse2.Store((uint*)(destPtr + offset), fillVector);
                offset += 16;
            }
        }
        
        // Handle remaining pixels
        while (offset + 4 <= destination.Length)
        {
            destination[offset] = (byte)color;
            destination[offset + 1] = (byte)(color >> 8);
            destination[offset + 2] = (byte)(color >> 16);
            destination[offset + 3] = (byte)(color >> 24);
            offset += 4;
        }
    }
    
    /// <summary>
    /// Scalar BGRA fill.
    /// </summary>
    private static void FillBgraScalar(Span<byte> destination, uint color)
    {
        byte b = (byte)color;
        byte g = (byte)(color >> 8);
        byte r = (byte)(color >> 16);
        byte a = (byte)(color >> 24);
        
        for (int i = 0; i + 4 <= destination.Length; i += 4)
        {
            destination[i] = b;
            destination[i + 1] = g;
            destination[i + 2] = r;
            destination[i + 3] = a;
        }
    }
}

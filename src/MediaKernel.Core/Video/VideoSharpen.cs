// Video sharpen filter using unsharp masking
// Reference: VLC's modules/video_filter/sharpen.c

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Sharpening algorithm selection.
/// </summary>
public enum SharpenAlgorithm
{
    /// <summary>Simple 3x3 unsharp mask - fast and effective.</summary>
    UnsharpMask3x3,
    
    /// <summary>5x5 unsharp mask - higher quality, slower.</summary>
    UnsharpMask5x5,
    
    /// <summary>Laplacian sharpening - edge detection based.</summary>
    Laplacian
}

/// <summary>
/// Video sharpen filter for edge enhancement.
/// </summary>
/// <remarks>
/// Uses unsharp masking technique:
/// 1. Blur the image slightly
/// 2. Subtract blurred from original to get high-frequency details
/// 3. Add scaled details back to original for enhancement
/// 
/// Higher strength values produce more aggressive sharpening.
/// Very high values can introduce artifacts (halos).
/// </remarks>
public static class VideoSharpen
{
    /// <summary>
    /// Sharpens a BGRA image using the specified algorithm.
    /// </summary>
    /// <param name="source">Source pixel data (BGRA format).</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="destination">Destination buffer (same size as source).</param>
    /// <param name="strength">Sharpening strength (0.0 = no change, 1.0 = normal, 2.0+ = aggressive).</param>
    /// <param name="algorithm">Sharpening algorithm to use.</param>
    public static void SharpenBgra(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength = 1.0f,
        SharpenAlgorithm algorithm = SharpenAlgorithm.UnsharpMask3x3)
    {
        if (source.IsEmpty || width <= 0 || height <= 0 || strength <= 0)
        {
            source.CopyTo(destination);
            return;
        }
        
        switch (algorithm)
        {
            case SharpenAlgorithm.UnsharpMask3x3:
                SharpenUnsharp3x3Bgra(source, width, height, destination, strength);
                break;
            case SharpenAlgorithm.UnsharpMask5x5:
                SharpenUnsharp5x5Bgra(source, width, height, destination, strength);
                break;
            case SharpenAlgorithm.Laplacian:
                SharpenLaplacianBgra(source, width, height, destination, strength);
                break;
        }
    }
    
    /// <summary>
    /// Sharpens a single-plane image (Y plane of YUV).
    /// </summary>
    /// <param name="source">Source plane data.</param>
    /// <param name="width">Plane width.</param>
    /// <param name="height">Plane height.</param>
    /// <param name="destination">Destination buffer (same size as source).</param>
    /// <param name="strength">Sharpening strength (0.0-2.0, 1.0 = normal).</param>
    public static void SharpenPlane(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength = 1.0f)
    {
        if (source.IsEmpty || width <= 0 || height <= 0 || strength <= 0)
        {
            source.CopyTo(destination);
            return;
        }
        
        SharpenUnsharp3x3Plane(source, width, height, destination, strength);
    }
    
    /// <summary>
    /// 3x3 unsharp mask for BGRA images.
    /// Kernel: Original + strength * (Original - Blur)
    /// </summary>
    private static void SharpenUnsharp3x3Bgra(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength)
    {
        int stride = width * 4;
        
        // Process interior pixels
        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 1; x < width - 1; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                
                // For each color channel (B, G, R), skip A
                for (int c = 0; c < 3; c++)
                {
                    // Get center pixel value
                    int center = source[pixelOffset + c];
                    
                    // Calculate 3x3 box blur average of neighbors
                    int blur = (
                        source[pixelOffset - stride - 4 + c] +
                        source[pixelOffset - stride + c] +
                        source[pixelOffset - stride + 4 + c] +
                        source[pixelOffset - 4 + c] +
                        center +
                        source[pixelOffset + 4 + c] +
                        source[pixelOffset + stride - 4 + c] +
                        source[pixelOffset + stride + c] +
                        source[pixelOffset + stride + 4 + c]
                    ) / 9;
                    
                    // Unsharp mask: original + strength * (original - blurred)
                    int highFreq = center - blur;
                    int result = center + (int)(highFreq * strength);
                    
                    // Clamp to valid range
                    destination[pixelOffset + c] = (byte)Math.Clamp(result, 0, 255);
                }
                
                // Copy alpha unchanged
                destination[pixelOffset + 3] = source[pixelOffset + 3];
            }
        }
        
        // Copy edge pixels unchanged (1 pixel border)
        CopyBorderPixelsBgra(source, destination, width, height, 1);
    }
    
    /// <summary>
    /// 5x5 unsharp mask for higher quality sharpening.
    /// Uses Gaussian-weighted blur for smoother results.
    /// </summary>
    private static void SharpenUnsharp5x5Bgra(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength)
    {
        int stride = width * 4;
        
        // 5x5 Gaussian-like weights (sum = 64 for bit shift)
        // 1 2 4 2 1
        // 2 4 8 4 2
        // 4 8 16 8 4
        // 2 4 8 4 2
        // 1 2 4 2 1
        
        for (int y = 2; y < height - 2; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 2; x < width - 2; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                
                for (int c = 0; c < 3; c++)
                {
                    int center = source[pixelOffset + c];
                    
                    // Weighted 5x5 blur
                    int blur = 0;
                    
                    // Row -2
                    int row = pixelOffset - stride * 2;
                    blur += source[row - 8 + c] * 1;
                    blur += source[row - 4 + c] * 2;
                    blur += source[row + c] * 4;
                    blur += source[row + 4 + c] * 2;
                    blur += source[row + 8 + c] * 1;
                    
                    // Row -1
                    row = pixelOffset - stride;
                    blur += source[row - 8 + c] * 2;
                    blur += source[row - 4 + c] * 4;
                    blur += source[row + c] * 8;
                    blur += source[row + 4 + c] * 4;
                    blur += source[row + 8 + c] * 2;
                    
                    // Row 0
                    row = pixelOffset;
                    blur += source[row - 8 + c] * 4;
                    blur += source[row - 4 + c] * 8;
                    blur += source[row + c] * 16;
                    blur += source[row + 4 + c] * 8;
                    blur += source[row + 8 + c] * 4;
                    
                    // Row +1
                    row = pixelOffset + stride;
                    blur += source[row - 8 + c] * 2;
                    blur += source[row - 4 + c] * 4;
                    blur += source[row + c] * 8;
                    blur += source[row + 4 + c] * 4;
                    blur += source[row + 8 + c] * 2;
                    
                    // Row +2
                    row = pixelOffset + stride * 2;
                    blur += source[row - 8 + c] * 1;
                    blur += source[row - 4 + c] * 2;
                    blur += source[row + c] * 4;
                    blur += source[row + 4 + c] * 2;
                    blur += source[row + 8 + c] * 1;
                    
                    // Divide by 100 (approximate sum of weights)
                    blur /= 100;
                    
                    int highFreq = center - blur;
                    int result = center + (int)(highFreq * strength);
                    
                    destination[pixelOffset + c] = (byte)Math.Clamp(result, 0, 255);
                }
                
                destination[pixelOffset + 3] = source[pixelOffset + 3];
            }
        }
        
        CopyBorderPixelsBgra(source, destination, width, height, 2);
    }
    
    /// <summary>
    /// Laplacian edge-detection based sharpening.
    /// Uses discrete Laplacian kernel: [0 -1 0; -1 4 -1; 0 -1 0]
    /// </summary>
    private static void SharpenLaplacianBgra(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength)
    {
        int stride = width * 4;
        
        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 1; x < width - 1; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                
                for (int c = 0; c < 3; c++)
                {
                    int center = source[pixelOffset + c];
                    
                    // Laplacian: 4*center - north - south - east - west
                    int laplacian = 4 * center
                        - source[pixelOffset - stride + c]  // north
                        - source[pixelOffset + stride + c]  // south
                        - source[pixelOffset - 4 + c]       // west
                        - source[pixelOffset + 4 + c];      // east
                    
                    // Add scaled edge information to original
                    int result = center + (int)(laplacian * strength * 0.25f);
                    
                    destination[pixelOffset + c] = (byte)Math.Clamp(result, 0, 255);
                }
                
                destination[pixelOffset + 3] = source[pixelOffset + 3];
            }
        }
        
        CopyBorderPixelsBgra(source, destination, width, height, 1);
    }
    
    /// <summary>
    /// 3x3 unsharp mask for single-plane (Y) images.
    /// </summary>
    private static void SharpenUnsharp3x3Plane(
        ReadOnlySpan<byte> source, int width, int height,
        Span<byte> destination, float strength)
    {
        // Process interior pixels
        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * width;
            
            for (int x = 1; x < width - 1; x++)
            {
                int pixelOffset = rowOffset + x;
                
                int center = source[pixelOffset];
                
                // 3x3 box blur average
                int blur = (
                    source[pixelOffset - width - 1] +
                    source[pixelOffset - width] +
                    source[pixelOffset - width + 1] +
                    source[pixelOffset - 1] +
                    center +
                    source[pixelOffset + 1] +
                    source[pixelOffset + width - 1] +
                    source[pixelOffset + width] +
                    source[pixelOffset + width + 1]
                ) / 9;
                
                int highFreq = center - blur;
                int result = center + (int)(highFreq * strength);
                
                destination[pixelOffset] = (byte)Math.Clamp(result, 0, 255);
            }
        }
        
        // Copy border pixels
        CopyBorderPixelsPlane(source, destination, width, height, 1);
    }
    
    /// <summary>
    /// Copies border pixels unchanged for BGRA images.
    /// </summary>
    private static void CopyBorderPixelsBgra(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int borderSize)
    {
        int stride = width * 4;
        
        // Top rows
        for (int y = 0; y < borderSize; y++)
        {
            int rowStart = y * stride;
            source.Slice(rowStart, stride).CopyTo(destination.Slice(rowStart, stride));
        }
        
        // Bottom rows
        for (int y = height - borderSize; y < height; y++)
        {
            int rowStart = y * stride;
            source.Slice(rowStart, stride).CopyTo(destination.Slice(rowStart, stride));
        }
        
        // Left and right columns for middle rows
        for (int y = borderSize; y < height - borderSize; y++)
        {
            int rowStart = y * stride;
            
            // Left border
            source.Slice(rowStart, borderSize * 4).CopyTo(destination.Slice(rowStart, borderSize * 4));
            
            // Right border
            int rightStart = rowStart + (width - borderSize) * 4;
            source.Slice(rightStart, borderSize * 4).CopyTo(destination.Slice(rightStart, borderSize * 4));
        }
    }
    
    /// <summary>
    /// Copies border pixels unchanged for single-plane images.
    /// </summary>
    private static void CopyBorderPixelsPlane(
        ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int borderSize)
    {
        // Top rows
        for (int y = 0; y < borderSize; y++)
        {
            int rowStart = y * width;
            source.Slice(rowStart, width).CopyTo(destination.Slice(rowStart, width));
        }
        
        // Bottom rows
        for (int y = height - borderSize; y < height; y++)
        {
            int rowStart = y * width;
            source.Slice(rowStart, width).CopyTo(destination.Slice(rowStart, width));
        }
        
        // Left and right columns for middle rows
        for (int y = borderSize; y < height - borderSize; y++)
        {
            int rowStart = y * width;
            
            // Left border
            for (int x = 0; x < borderSize; x++)
            {
                destination[rowStart + x] = source[rowStart + x];
            }
            
            // Right border  
            for (int x = width - borderSize; x < width; x++)
            {
                destination[rowStart + x] = source[rowStart + x];
            }
        }
    }
}

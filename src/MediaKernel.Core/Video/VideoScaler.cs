// Video scaler with multiple scaling algorithms
// Reference: VLC's modules/video_filter/scale.c

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Scaling algorithm for image resizing.
/// </summary>
public enum ScaleAlgorithm
{
    /// <summary>Nearest neighbor - fastest, pixelated.</summary>
    NearestNeighbor,
    
    /// <summary>Bilinear interpolation - good quality, fast.</summary>
    Bilinear,
    
    /// <summary>Bicubic interpolation - better quality, slower.</summary>
    Bicubic,
    
    /// <summary>Lanczos resampling - best quality, slowest.</summary>
    Lanczos
}

/// <summary>
/// Video scaler supporting multiple algorithms and pixel formats.
/// </summary>
public static class VideoScaler
{
    // Lanczos kernel size (number of lobes)
    private const int LanczosLobes = 3;

    /// <summary>
    /// Scales an image using the specified algorithm.
    /// </summary>
    /// <param name="source">Source pixel data (BGRA format)</param>
    /// <param name="sourceWidth">Source image width</param>
    /// <param name="sourceHeight">Source image height</param>
    /// <param name="destination">Destination buffer (must be destWidth × destHeight × 4 bytes)</param>
    /// <param name="destWidth">Destination width</param>
    /// <param name="destHeight">Destination height</param>
    /// <param name="algorithm">Scaling algorithm to use</param>
    public static void ScaleBgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destWidth, int destHeight,
        ScaleAlgorithm algorithm = ScaleAlgorithm.Bilinear)
    {
        switch (algorithm)
        {
            case ScaleAlgorithm.NearestNeighbor:
                ScaleNearestNeighbor(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
            case ScaleAlgorithm.Bilinear:
                ScaleBilinear(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
            case ScaleAlgorithm.Bicubic:
                ScaleBicubic(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
            case ScaleAlgorithm.Lanczos:
                ScaleLanczos(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
        }
    }

    /// <summary>
    /// Scales Y plane (single channel) using the specified algorithm.
    /// </summary>
    public static void ScaleY(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destWidth, int destHeight,
        ScaleAlgorithm algorithm = ScaleAlgorithm.Bilinear)
    {
        switch (algorithm)
        {
            case ScaleAlgorithm.NearestNeighbor:
                ScaleNearestNeighborY(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
            case ScaleAlgorithm.Bilinear:
                ScaleBilinearY(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
            default:
                // Fall back to bilinear for other algorithms on Y plane
                ScaleBilinearY(source, sourceWidth, sourceHeight, destination, destWidth, destHeight);
                break;
        }
    }

    /// <summary>
    /// Calculates the output buffer size for the given dimensions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateBufferSize(int width, int height, int bytesPerPixel = 4)
    {
        return width * height * bytesPerPixel;
    }

    // Nearest neighbor scaling (fastest)
    private static void ScaleNearestNeighbor(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            int srcY = (int)(y * yRatio);
            srcY = Math.Min(srcY, srcH - 1);

            for (int x = 0; x < dstW; x++)
            {
                int srcX = (int)(x * xRatio);
                srcX = Math.Min(srcX, srcW - 1);

                int srcIdx = (srcY * srcW + srcX) * 4;
                int dstIdx = (y * dstW + x) * 4;

                dest[dstIdx] = source[srcIdx];
                dest[dstIdx + 1] = source[srcIdx + 1];
                dest[dstIdx + 2] = source[srcIdx + 2];
                dest[dstIdx + 3] = source[srcIdx + 3];
            }
        }
    }

    // Bilinear interpolation
    private static void ScaleBilinear(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)(srcW - 1) / dstW;
        float yRatio = (float)(srcH - 1) / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float srcYf = y * yRatio;
            int srcY0 = (int)srcYf;
            int srcY1 = Math.Min(srcY0 + 1, srcH - 1);
            float yFrac = srcYf - srcY0;

            for (int x = 0; x < dstW; x++)
            {
                float srcXf = x * xRatio;
                int srcX0 = (int)srcXf;
                int srcX1 = Math.Min(srcX0 + 1, srcW - 1);
                float xFrac = srcXf - srcX0;

                int idx00 = (srcY0 * srcW + srcX0) * 4;
                int idx01 = (srcY0 * srcW + srcX1) * 4;
                int idx10 = (srcY1 * srcW + srcX0) * 4;
                int idx11 = (srcY1 * srcW + srcX1) * 4;
                int dstIdx = (y * dstW + x) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float v00 = source[idx00 + c];
                    float v01 = source[idx01 + c];
                    float v10 = source[idx10 + c];
                    float v11 = source[idx11 + c];

                    float v0 = v00 + (v01 - v00) * xFrac;
                    float v1 = v10 + (v11 - v10) * xFrac;
                    float v = v0 + (v1 - v0) * yFrac;

                    dest[dstIdx + c] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
                }
            }
        }
    }

    // Bicubic interpolation
    private static void ScaleBicubic(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float srcYf = y * yRatio - 0.5f;
            int srcY = (int)MathF.Floor(srcYf);
            float yFrac = srcYf - srcY;

            for (int x = 0; x < dstW; x++)
            {
                float srcXf = x * xRatio - 0.5f;
                int srcX = (int)MathF.Floor(srcXf);
                float xFrac = srcXf - srcX;

                int dstIdx = (y * dstW + x) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float result = 0;

                    for (int j = -1; j <= 2; j++)
                    {
                        int py = Math.Clamp(srcY + j, 0, srcH - 1);
                        float wy = CubicWeight(j - yFrac);

                        for (int i = -1; i <= 2; i++)
                        {
                            int px = Math.Clamp(srcX + i, 0, srcW - 1);
                            float wx = CubicWeight(i - xFrac);

                            int idx = (py * srcW + px) * 4;
                            result += source[idx + c] * wx * wy;
                        }
                    }

                    dest[dstIdx + c] = (byte)Math.Clamp((int)(result + 0.5f), 0, 255);
                }
            }
        }
    }

    // Lanczos resampling (highest quality)
    private static void ScaleLanczos(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float srcYf = (y + 0.5f) * yRatio - 0.5f;
            int srcY = (int)MathF.Floor(srcYf);
            float yFrac = srcYf - srcY;

            for (int x = 0; x < dstW; x++)
            {
                float srcXf = (x + 0.5f) * xRatio - 0.5f;
                int srcX = (int)MathF.Floor(srcXf);
                float xFrac = srcXf - srcX;

                int dstIdx = (y * dstW + x) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float result = 0;
                    float weightSum = 0;

                    for (int j = -LanczosLobes + 1; j <= LanczosLobes; j++)
                    {
                        int py = Math.Clamp(srcY + j, 0, srcH - 1);
                        float wy = LanczosWeight(j - yFrac);

                        for (int i = -LanczosLobes + 1; i <= LanczosLobes; i++)
                        {
                            int px = Math.Clamp(srcX + i, 0, srcW - 1);
                            float wx = LanczosWeight(i - xFrac);

                            float w = wx * wy;
                            int idx = (py * srcW + px) * 4;
                            result += source[idx + c] * w;
                            weightSum += w;
                        }
                    }

                    if (weightSum > 0)
                        result /= weightSum;

                    dest[dstIdx + c] = (byte)Math.Clamp((int)(result + 0.5f), 0, 255);
                }
            }
        }
    }

    // Y-plane scaling (single channel)
    private static void ScaleNearestNeighborY(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            int srcY = Math.Min((int)(y * yRatio), srcH - 1);
            for (int x = 0; x < dstW; x++)
            {
                int srcX = Math.Min((int)(x * xRatio), srcW - 1);
                dest[y * dstW + x] = source[srcY * srcW + srcX];
            }
        }
    }

    private static void ScaleBilinearY(
        ReadOnlySpan<byte> source, int srcW, int srcH,
        Span<byte> dest, int dstW, int dstH)
    {
        float xRatio = (float)(srcW - 1) / dstW;
        float yRatio = (float)(srcH - 1) / dstH;

        for (int y = 0; y < dstH; y++)
        {
            float srcYf = y * yRatio;
            int srcY0 = (int)srcYf;
            int srcY1 = Math.Min(srcY0 + 1, srcH - 1);
            float yFrac = srcYf - srcY0;

            for (int x = 0; x < dstW; x++)
            {
                float srcXf = x * xRatio;
                int srcX0 = (int)srcXf;
                int srcX1 = Math.Min(srcX0 + 1, srcW - 1);
                float xFrac = srcXf - srcX0;

                float v00 = source[srcY0 * srcW + srcX0];
                float v01 = source[srcY0 * srcW + srcX1];
                float v10 = source[srcY1 * srcW + srcX0];
                float v11 = source[srcY1 * srcW + srcX1];

                float v0 = v00 + (v01 - v00) * xFrac;
                float v1 = v10 + (v11 - v10) * xFrac;
                float v = v0 + (v1 - v0) * yFrac;

                dest[y * dstW + x] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }
        }
    }

    // Cubic interpolation weight (Catmull-Rom spline)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CubicWeight(float x)
    {
        float absX = MathF.Abs(x);
        
        if (absX < 1.0f)
        {
            return 1.5f * absX * absX * absX - 2.5f * absX * absX + 1.0f;
        }
        else if (absX < 2.0f)
        {
            return -0.5f * absX * absX * absX + 2.5f * absX * absX - 4.0f * absX + 2.0f;
        }
        
        return 0.0f;
    }

    // Lanczos kernel
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LanczosWeight(float x)
    {
        if (x == 0.0f)
            return 1.0f;
        
        float absX = MathF.Abs(x);
        if (absX >= LanczosLobes)
            return 0.0f;

        float piX = MathF.PI * x;
        return LanczosLobes * MathF.Sin(piX) * MathF.Sin(piX / LanczosLobes) / (piX * piX);
    }
}

/// <summary>
/// Cropping and padding operations for video frames.
/// </summary>
public static class VideoCrop
{
    /// <summary>
    /// Crops a region from a BGRA image.
    /// </summary>
    public static void CropBgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination,
        int cropX, int cropY, int cropWidth, int cropHeight)
    {
        cropX = Math.Clamp(cropX, 0, sourceWidth - 1);
        cropY = Math.Clamp(cropY, 0, sourceHeight - 1);
        cropWidth = Math.Min(cropWidth, sourceWidth - cropX);
        cropHeight = Math.Min(cropHeight, sourceHeight - cropY);

        for (int y = 0; y < cropHeight; y++)
        {
            int srcOffset = ((cropY + y) * sourceWidth + cropX) * 4;
            int dstOffset = y * cropWidth * 4;
            
            source.Slice(srcOffset, cropWidth * 4).CopyTo(destination.Slice(dstOffset));
        }
    }

    /// <summary>
    /// Adds padding (letterbox/pillarbox) around a BGRA image.
    /// </summary>
    public static void PadBgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destWidth, int destHeight,
        byte padR = 0, byte padG = 0, byte padB = 0)
    {
        // Fill destination with padding color
        for (int i = 0; i < destination.Length; i += 4)
        {
            destination[i] = padB;
            destination[i + 1] = padG;
            destination[i + 2] = padR;
            destination[i + 3] = 255;
        }

        // Center the source
        int offsetX = (destWidth - sourceWidth) / 2;
        int offsetY = (destHeight - sourceHeight) / 2;

        for (int y = 0; y < sourceHeight; y++)
        {
            int srcOffset = y * sourceWidth * 4;
            int dstOffset = ((offsetY + y) * destWidth + offsetX) * 4;
            
            source.Slice(srcOffset, sourceWidth * 4).CopyTo(destination.Slice(dstOffset));
        }
    }

    /// <summary>
    /// Flips image horizontally.
    /// </summary>
    public static void FlipHorizontalBgra(Span<byte> data, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            for (int x = 0; x < width / 2; x++)
            {
                int leftIdx = rowOffset + x * 4;
                int rightIdx = rowOffset + (width - 1 - x) * 4;

                // Swap pixels
                for (int c = 0; c < 4; c++)
                {
                    byte temp = data[leftIdx + c];
                    data[leftIdx + c] = data[rightIdx + c];
                    data[rightIdx + c] = temp;
                }
            }
        }
    }

    /// <summary>
    /// Flips image vertically.
    /// </summary>
    public static void FlipVerticalBgra(Span<byte> data, int width, int height)
    {
        int rowSize = width * 4;
        Span<byte> tempRow = stackalloc byte[Math.Min(rowSize, 4096)];
        byte[]? heapBuffer = rowSize > 4096 ? new byte[rowSize] : null;
        Span<byte> buffer = heapBuffer ?? tempRow[..rowSize];

        for (int y = 0; y < height / 2; y++)
        {
            int topOffset = y * rowSize;
            int bottomOffset = (height - 1 - y) * rowSize;

            // Swap rows
            data.Slice(topOffset, rowSize).CopyTo(buffer);
            data.Slice(bottomOffset, rowSize).CopyTo(data.Slice(topOffset));
            buffer.CopyTo(data.Slice(bottomOffset));
        }
    }

    /// <summary>
    /// Rotates image 90 degrees clockwise.
    /// </summary>
    public static void Rotate90Bgra(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination)
    {
        // Destination dimensions are swapped
        int destWidth = sourceHeight;
        int destHeight = sourceWidth;

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int srcIdx = (y * sourceWidth + x) * 4;
                int dstX = sourceHeight - 1 - y;
                int dstY = x;
                int dstIdx = (dstY * destWidth + dstX) * 4;

                destination[dstIdx] = source[srcIdx];
                destination[dstIdx + 1] = source[srcIdx + 1];
                destination[dstIdx + 2] = source[srcIdx + 2];
                destination[dstIdx + 3] = source[srcIdx + 3];
            }
        }
    }

    /// <summary>
    /// Rotates image 180 degrees.
    /// </summary>
    public static void Rotate180Bgra(Span<byte> data, int width, int height)
    {
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount / 2; i++)
        {
            int frontIdx = i * 4;
            int backIdx = (pixelCount - 1 - i) * 4;

            for (int c = 0; c < 4; c++)
            {
                byte temp = data[frontIdx + c];
                data[frontIdx + c] = data[backIdx + c];
                data[backIdx + c] = temp;
            }
        }
    }
}

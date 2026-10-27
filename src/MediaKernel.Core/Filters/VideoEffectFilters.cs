// MediaKernel - Video Effect Filters
// Additional creative/artistic video effects (motion, blur, edge detection)

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Edge detection filter using Sobel operator.
/// </summary>
public sealed class EdgeDetectionFilter
{
    private float _threshold = 50f;
    private bool _invertOutput;
    private bool _showOriginalWithEdges;
    
    /// <summary>
    /// Edge detection threshold (0-255). Lower = more sensitive.
    /// </summary>
    public float Threshold
    {
        get => _threshold;
        set => _threshold = Math.Clamp(value, 0f, 255f);
    }
    
    /// <summary>
    /// Whether to invert the output (white background, black edges).
    /// </summary>
    public bool InvertOutput
    {
        get => _invertOutput;
        set => _invertOutput = value;
    }
    
    /// <summary>
    /// Whether to show edges overlaid on original image.
    /// </summary>
    public bool ShowOriginalWithEdges
    {
        get => _showOriginalWithEdges;
        set => _showOriginalWithEdges = value;
    }
    
    /// <summary>
    /// Apply edge detection to RGBA frame.
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        // Need temporary buffer for grayscale source
        byte[] grayBuffer = new byte[width * height];
        
        // Convert to grayscale
        for (int i = 0; i < width * height; i++)
        {
            int offset = i * 4;
            grayBuffer[i] = (byte)((rgba[offset] * 77 + rgba[offset + 1] * 150 + rgba[offset + 2] * 29) >> 8);
        }
        
        // Apply Sobel operator
        // Sobel kernels:
        // Gx: [-1, 0, 1], [-2, 0, 2], [-1, 0, 1]
        // Gy: [-1, -2, -1], [0, 0, 0], [1, 2, 1]
        
        byte[] edgeBuffer = new byte[width * height];
        
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                // Get 3x3 neighborhood
                int p00 = grayBuffer[(y - 1) * width + (x - 1)];
                int p01 = grayBuffer[(y - 1) * width + x];
                int p02 = grayBuffer[(y - 1) * width + (x + 1)];
                int p10 = grayBuffer[y * width + (x - 1)];
                int p12 = grayBuffer[y * width + (x + 1)];
                int p20 = grayBuffer[(y + 1) * width + (x - 1)];
                int p21 = grayBuffer[(y + 1) * width + x];
                int p22 = grayBuffer[(y + 1) * width + (x + 1)];
                
                // Sobel X gradient
                int gx = -p00 + p02 - 2 * p10 + 2 * p12 - p20 + p22;
                
                // Sobel Y gradient
                int gy = -p00 - 2 * p01 - p02 + p20 + 2 * p21 + p22;
                
                // Magnitude
                float magnitude = MathF.Sqrt(gx * gx + gy * gy);
                
                edgeBuffer[y * width + x] = magnitude > _threshold ? (byte)255 : (byte)0;
            }
        }
        
        // Apply to output
        for (int i = 0; i < width * height; i++)
        {
            int offset = i * 4;
            byte edge = edgeBuffer[i];
            
            if (_showOriginalWithEdges)
            {
                // Blend edges with original
                if (edge > 0)
                {
                    rgba[offset] = _invertOutput ? (byte)0 : (byte)255;
                    rgba[offset + 1] = _invertOutput ? (byte)0 : (byte)255;
                    rgba[offset + 2] = _invertOutput ? (byte)0 : (byte)255;
                }
            }
            else
            {
                byte output = _invertOutput ? (byte)(255 - edge) : edge;
                rgba[offset] = output;
                rgba[offset + 1] = output;
                rgba[offset + 2] = output;
            }
        }
    }
}

/// <summary>
/// Motion blur filter - blends current frame with previous frames.
/// </summary>
public sealed class MotionBlurFilter
{
    private byte[]? _previousFrame;
    private int _factor = 80;
    
    /// <summary>
    /// Blur factor (1-127). Higher = more blur/trails.
    /// </summary>
    public int Factor
    {
        get => _factor;
        set => _factor = Math.Clamp(value, 1, 127);
    }
    
    /// <summary>
    /// Reset accumulated frame data.
    /// </summary>
    public void Reset()
    {
        _previousFrame = null;
    }
    
    /// <summary>
    /// Apply motion blur to RGBA frame.
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        int frameSize = width * height * 4;
        
        // Initialize previous frame buffer if needed
        if (_previousFrame == null || _previousFrame.Length != frameSize)
        {
            _previousFrame = new byte[frameSize];
            rgba.CopyTo(_previousFrame);
            return;
        }
        
        // Blend current with previous
        // output = (current * (128 - factor) + previous * factor) / 128
        int currentWeight = 128 - _factor;
        int previousWeight = _factor;
        
        for (int i = 0; i < frameSize; i += 4)
        {
            // Process RGB, skip alpha
            rgba[i] = (byte)((rgba[i] * currentWeight + _previousFrame[i] * previousWeight) >> 7);
            rgba[i + 1] = (byte)((rgba[i + 1] * currentWeight + _previousFrame[i + 1] * previousWeight) >> 7);
            rgba[i + 2] = (byte)((rgba[i + 2] * currentWeight + _previousFrame[i + 2] * previousWeight) >> 7);
            
            // Update previous frame
            _previousFrame[i] = rgba[i];
            _previousFrame[i + 1] = rgba[i + 1];
            _previousFrame[i + 2] = rgba[i + 2];
            _previousFrame[i + 3] = rgba[i + 3];
        }
    }
}

/// <summary>
/// Gaussian blur filter.
/// </summary>
public sealed class GaussianBlurFilter
{
    private float _sigma = 1.5f;
    private int _radius = 3;
    private float[]? _kernel;
    
    /// <summary>
    /// Blur sigma (standard deviation). Higher = more blur.
    /// </summary>
    public float Sigma
    {
        get => _sigma;
        set
        {
            _sigma = Math.Max(0.1f, value);
            _kernel = null; // Invalidate kernel
        }
    }
    
    /// <summary>
    /// Blur radius in pixels (1-20).
    /// </summary>
    public int Radius
    {
        get => _radius;
        set
        {
            _radius = Math.Clamp(value, 1, 20);
            _kernel = null; // Invalidate kernel
        }
    }
    
    /// <summary>
    /// Apply Gaussian blur to RGBA frame (separable 2-pass).
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        EnsureKernel();
        
        // Temporary buffer for intermediate result
        byte[] tempBuffer = new byte[width * height * 4];
        
        // Horizontal pass
        ApplyHorizontalPass(rgba, tempBuffer.AsSpan(), width, height);
        
        // Vertical pass
        ApplyVerticalPass(tempBuffer.AsSpan(), rgba, width, height);
    }
    
    private void EnsureKernel()
    {
        if (_kernel != null)
            return;
        
        int size = _radius * 2 + 1;
        _kernel = new float[size];
        
        float sum = 0;
        float twoSigmaSq = 2 * _sigma * _sigma;
        
        for (int i = 0; i < size; i++)
        {
            int x = i - _radius;
            _kernel[i] = MathF.Exp(-(x * x) / twoSigmaSq);
            sum += _kernel[i];
        }
        
        // Normalize
        for (int i = 0; i < size; i++)
        {
            _kernel[i] /= sum;
        }
    }
    
    private void ApplyHorizontalPass(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        float[] kernel = _kernel!;
        int radius = _radius;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                
                for (int k = -radius; k <= radius; k++)
                {
                    int sampleX = Math.Clamp(x + k, 0, width - 1);
                    int srcOffset = (y * width + sampleX) * 4;
                    float weight = kernel[k + radius];
                    
                    r += source[srcOffset] * weight;
                    g += source[srcOffset + 1] * weight;
                    b += source[srcOffset + 2] * weight;
                    a += source[srcOffset + 3] * weight;
                }
                
                int dstOffset = (y * width + x) * 4;
                destination[dstOffset] = (byte)Math.Clamp(r, 0, 255);
                destination[dstOffset + 1] = (byte)Math.Clamp(g, 0, 255);
                destination[dstOffset + 2] = (byte)Math.Clamp(b, 0, 255);
                destination[dstOffset + 3] = (byte)Math.Clamp(a, 0, 255);
            }
        }
    }
    
    private void ApplyVerticalPass(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        float[] kernel = _kernel!;
        int radius = _radius;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                
                for (int k = -radius; k <= radius; k++)
                {
                    int sampleY = Math.Clamp(y + k, 0, height - 1);
                    int srcOffset = (sampleY * width + x) * 4;
                    float weight = kernel[k + radius];
                    
                    r += source[srcOffset] * weight;
                    g += source[srcOffset + 1] * weight;
                    b += source[srcOffset + 2] * weight;
                    a += source[srcOffset + 3] * weight;
                }
                
                int dstOffset = (y * width + x) * 4;
                destination[dstOffset] = (byte)Math.Clamp(r, 0, 255);
                destination[dstOffset + 1] = (byte)Math.Clamp(g, 0, 255);
                destination[dstOffset + 2] = (byte)Math.Clamp(b, 0, 255);
                destination[dstOffset + 3] = (byte)Math.Clamp(a, 0, 255);
            }
        }
    }
}

/// <summary>
/// Noise/grain filter for film grain effect.
/// </summary>
public sealed class GrainFilter
{
    private float _intensity = 30f;
    private bool _monochrome = true;
    private Random? _random;
    
    /// <summary>
    /// Grain intensity (0-100).
    /// </summary>
    public float Intensity
    {
        get => _intensity;
        set => _intensity = Math.Clamp(value, 0f, 100f);
    }
    
    /// <summary>
    /// Whether grain is monochrome (gray) or colored.
    /// </summary>
    public bool Monochrome
    {
        get => _monochrome;
        set => _monochrome = value;
    }
    
    /// <summary>
    /// Apply grain effect to RGBA frame.
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        _random ??= new Random();
        
        int pixelCount = width * height;
        float scale = _intensity * 2.55f; // Scale 0-100 to 0-255
        
        if (_monochrome)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                int noise = (int)((_random.NextSingle() - 0.5f) * scale);
                
                rgba[offset] = (byte)Math.Clamp(rgba[offset] + noise, 0, 255);
                rgba[offset + 1] = (byte)Math.Clamp(rgba[offset + 1] + noise, 0, 255);
                rgba[offset + 2] = (byte)Math.Clamp(rgba[offset + 2] + noise, 0, 255);
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                int noiseR = (int)((_random.NextSingle() - 0.5f) * scale);
                int noiseG = (int)((_random.NextSingle() - 0.5f) * scale);
                int noiseB = (int)((_random.NextSingle() - 0.5f) * scale);
                
                rgba[offset] = (byte)Math.Clamp(rgba[offset] + noiseR, 0, 255);
                rgba[offset + 1] = (byte)Math.Clamp(rgba[offset + 1] + noiseG, 0, 255);
                rgba[offset + 2] = (byte)Math.Clamp(rgba[offset + 2] + noiseB, 0, 255);
            }
        }
    }
}

/// <summary>
/// Color threshold filter - extracts pixels matching a specific color.
/// </summary>
public sealed class ColorThresholdFilter
{
    private byte _targetRed = 255;
    private byte _targetGreen;
    private byte _targetBlue;
    private int _tolerance = 50;
    private bool _showMask;
    
    /// <summary>Target red component.</summary>
    public byte TargetRed { get => _targetRed; set => _targetRed = value; }
    
    /// <summary>Target green component.</summary>
    public byte TargetGreen { get => _targetGreen; set => _targetGreen = value; }
    
    /// <summary>Target blue component.</summary>
    public byte TargetBlue { get => _targetBlue; set => _targetBlue = value; }
    
    /// <summary>Color tolerance (0-255).</summary>
    public int Tolerance
    {
        get => _tolerance;
        set => _tolerance = Math.Clamp(value, 0, 255);
    }
    
    /// <summary>Whether to show binary mask instead of filtered image.</summary>
    public bool ShowMask { get => _showMask; set => _showMask = value; }
    
    /// <summary>
    /// Apply color threshold to RGBA frame.
    /// Non-matching pixels become grayscale.
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        int pixelCount = width * height;
        int toleranceSq = _tolerance * _tolerance * 3; // 3 channels
        
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            
            int dr = rgba[offset] - _targetRed;
            int dg = rgba[offset + 1] - _targetGreen;
            int db = rgba[offset + 2] - _targetBlue;
            int distSq = dr * dr + dg * dg + db * db;
            
            if (distSq > toleranceSq)
            {
                if (_showMask)
                {
                    rgba[offset] = 0;
                    rgba[offset + 1] = 0;
                    rgba[offset + 2] = 0;
                }
                else
                {
                    // Convert to grayscale
                    byte gray = (byte)((rgba[offset] * 77 + rgba[offset + 1] * 150 + rgba[offset + 2] * 29) >> 8);
                    rgba[offset] = gray;
                    rgba[offset + 1] = gray;
                    rgba[offset + 2] = gray;
                }
            }
            else if (_showMask)
            {
                rgba[offset] = 255;
                rgba[offset + 1] = 255;
                rgba[offset + 2] = 255;
            }
        }
    }
}

/// <summary>
/// Pixelate/mosaic filter.
/// </summary>
public sealed class PixelateFilter
{
    private int _blockSize = 8;
    
    /// <summary>
    /// Size of pixel blocks (2-100).
    /// </summary>
    public int BlockSize
    {
        get => _blockSize;
        set => _blockSize = Math.Clamp(value, 2, 100);
    }
    
    /// <summary>
    /// Apply pixelate effect to RGBA frame.
    /// </summary>
    public void Apply(Span<byte> rgba, int width, int height)
    {
        int blockSize = _blockSize;
        
        for (int blockY = 0; blockY < height; blockY += blockSize)
        {
            for (int blockX = 0; blockX < width; blockX += blockSize)
            {
                // Calculate average color for block
                int sumR = 0, sumG = 0, sumB = 0, count = 0;
                
                int endY = Math.Min(blockY + blockSize, height);
                int endX = Math.Min(blockX + blockSize, width);
                
                for (int y = blockY; y < endY; y++)
                {
                    for (int x = blockX; x < endX; x++)
                    {
                        int offset = (y * width + x) * 4;
                        sumR += rgba[offset];
                        sumG += rgba[offset + 1];
                        sumB += rgba[offset + 2];
                        count++;
                    }
                }
                
                byte avgR = (byte)(sumR / count);
                byte avgG = (byte)(sumG / count);
                byte avgB = (byte)(sumB / count);
                
                // Fill block with average color
                for (int y = blockY; y < endY; y++)
                {
                    for (int x = blockX; x < endX; x++)
                    {
                        int offset = (y * width + x) * 4;
                        rgba[offset] = avgR;
                        rgba[offset + 1] = avgG;
                        rgba[offset + 2] = avgB;
                    }
                }
            }
        }
    }
}

/// <summary>
/// Wave distortion filter that creates horizontal or vertical wave effect.
/// </summary>
public sealed class WaveFilter
{
    public float Amplitude { get; set; } = 10f;
    public float Frequency { get; set; } = 0.05f;
    public float Phase { get; set; }
    public bool Horizontal { get; set; } = true;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride)
            return;

        Span<byte> tempBuffer = new byte[height * stride];
        rgba.CopyTo(tempBuffer);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcX, srcY;

                if (Horizontal)
                {
                    float offset = Amplitude * MathF.Sin(Frequency * y + Phase);
                    srcX = (int)(x + offset);
                    srcY = y;
                }
                else
                {
                    float offset = Amplitude * MathF.Sin(Frequency * x + Phase);
                    srcX = x;
                    srcY = (int)(y + offset);
                }

                srcX = Math.Clamp(srcX, 0, width - 1);
                srcY = Math.Clamp(srcY, 0, height - 1);

                int destOffset = y * stride + x * 4;
                int srcOffset = srcY * stride + srcX * 4;

                rgba[destOffset] = tempBuffer[srcOffset];
                rgba[destOffset + 1] = tempBuffer[srcOffset + 1];
                rgba[destOffset + 2] = tempBuffer[srcOffset + 2];
                rgba[destOffset + 3] = tempBuffer[srcOffset + 3];
            }
        }
    }

    public void AdvancePhase(float delta = 0.1f)
    {
        Phase += delta;
        if (Phase > MathF.PI * 2)
            Phase -= MathF.PI * 2;
    }
}

/// <summary>
/// Ripple distortion filter for water-like effect.
/// </summary>
public sealed class RippleFilter
{
    public float CenterX { get; set; } = 0.5f;
    public float CenterY { get; set; } = 0.5f;
    public float Amplitude { get; set; } = 20f;
    public float Wavelength { get; set; } = 30f;
    public float Phase { get; set; }
    public float Decay { get; set; } = 0.01f;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride)
            return;

        Span<byte> tempBuffer = new byte[height * stride];
        rgba.CopyTo(tempBuffer);

        float centerPx = CenterX * width;
        float centerPy = CenterY * height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerPx;
                float dy = y - centerPy;
                float distance = MathF.Sqrt(dx * dx + dy * dy);

                if (distance < 0.001f)
                    continue;

                float damping = MathF.Exp(-distance * Decay);
                float offset = Amplitude * damping * MathF.Sin(distance / Wavelength * MathF.PI * 2 - Phase);

                float angle = MathF.Atan2(dy, dx);
                int srcX = (int)(x - offset * MathF.Cos(angle));
                int srcY = (int)(y - offset * MathF.Sin(angle));

                srcX = Math.Clamp(srcX, 0, width - 1);
                srcY = Math.Clamp(srcY, 0, height - 1);

                int destOffset = y * stride + x * 4;
                int srcOffset = srcY * stride + srcX * 4;

                rgba[destOffset] = tempBuffer[srcOffset];
                rgba[destOffset + 1] = tempBuffer[srcOffset + 1];
                rgba[destOffset + 2] = tempBuffer[srcOffset + 2];
                rgba[destOffset + 3] = tempBuffer[srcOffset + 3];
            }
        }
    }

    public void AdvancePhase(float delta = 0.1f)
    {
        Phase += delta;
        if (Phase > MathF.PI * 2)
            Phase -= MathF.PI * 2;
    }
}

/// <summary>
/// Old movie effect filter (sepia, scratches, vignette, flicker).
/// </summary>
public sealed class OldMovieFilter
{
    private readonly Random random = new();
    
    public float ScratchDensity { get; set; } = 0.01f;
    public float FlickerIntensity { get; set; } = 0.05f;
    public float VignetteStrength { get; set; } = 0.3f;
    public bool AddSepia { get; set; } = true;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride)
            return;

        float flickerFactor = 1f - (float)(random.NextDouble() * FlickerIntensity);
        int scratchCount = (int)(width * ScratchDensity);

        // Generate scratch positions
        Span<int> scratchX = stackalloc int[Math.Min(scratchCount, 20)];
        for (int i = 0; i < scratchX.Length; i++)
        {
            scratchX[i] = random.Next(width);
        }

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;

            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;

                byte r = rgba[offset];
                byte g = rgba[offset + 1];
                byte b = rgba[offset + 2];

                // Convert to grayscale first
                byte gray = (byte)((r * 77 + g * 150 + b * 29) >> 8);

                if (AddSepia)
                {
                    // Apply sepia tone
                    int sepiaR = Math.Min(255, (int)(gray * 1.07f + 112 * 0.2f));
                    int sepiaG = Math.Min(255, (int)(gray * 0.93f + 112 * 0.1f));
                    int sepiaB = Math.Min(255, (int)(gray * 0.74f));
                    
                    r = (byte)sepiaR;
                    g = (byte)sepiaG;
                    b = (byte)sepiaB;
                }
                else
                {
                    r = g = b = gray;
                }

                // Apply flicker
                r = (byte)(r * flickerFactor);
                g = (byte)(g * flickerFactor);
                b = (byte)(b * flickerFactor);

                // Apply vignette
                float fx = (x - width * 0.5f) / (width * 0.5f);
                float fy = (y - height * 0.5f) / (height * 0.5f);
                float vignette = 1f - (fx * fx + fy * fy) * VignetteStrength;
                vignette = Math.Clamp(vignette, 0f, 1f);

                r = (byte)(r * vignette);
                g = (byte)(g * vignette);
                b = (byte)(b * vignette);

                // Apply scratches
                for (int i = 0; i < scratchX.Length; i++)
                {
                    if (Math.Abs(x - scratchX[i]) <= 1)
                    {
                        r = g = b = 200;
                        break;
                    }
                }

                rgba[offset] = r;
                rgba[offset + 1] = g;
                rgba[offset + 2] = b;
            }
        }
    }
}

/// <summary>
/// Freeze frame filter that holds the last captured frame.
/// </summary>
public sealed class FreezeFilter
{
    private byte[]? frozenFrame;
    private int frozenWidth;
    private int frozenHeight;
    
    public bool IsFrozen { get; private set; }

    public void Freeze(ReadOnlySpan<byte> rgba, int width, int height)
    {
        frozenFrame = rgba.ToArray();
        frozenWidth = width;
        frozenHeight = height;
        IsFrozen = true;
    }

    public void Unfreeze()
    {
        IsFrozen = false;
    }

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (!IsFrozen || frozenFrame == null)
            return;

        if (width == frozenWidth && height == frozenHeight)
        {
            int frozenStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                var srcRow = frozenFrame.AsSpan(y * frozenStride, frozenStride);
                var destRow = rgba.Slice(y * stride, width * 4);
                srcRow[..(width * 4)].CopyTo(destRow);
            }
        }
    }

    public void ClearFrozenFrame()
    {
        frozenFrame = null;
        frozenWidth = 0;
        frozenHeight = 0;
        IsFrozen = false;
    }
}

/// <summary>
/// VHS tape effect filter (noise, color bleeding, scan lines).
/// </summary>
public sealed class VhsFilter
{
    private readonly Random random = new();
    
    public float NoiseIntensity { get; set; } = 0.1f;
    public float ColorBleedAmount { get; set; } = 3f;
    public float ScanLineOpacity { get; set; } = 0.15f;
    public float TrackingError { get; set; } = 0f;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride)
            return;

        // Color bleeding (shift red channel horizontally)
        if (ColorBleedAmount > 0)
        {
            int bleedPixels = (int)ColorBleedAmount;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = width - 1; x >= bleedPixels; x--)
                {
                    int destOffset = rowOffset + x * 4;
                    int srcOffset = rowOffset + (x - bleedPixels) * 4;
                    rgba[destOffset] = rgba[srcOffset]; // Shift red channel
                }
            }
        }

        // Add noise and scan lines
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            
            // Tracking error (horizontal jitter)
            int trackingOffset = 0;
            if (TrackingError > 0 && y > height * 0.8f)
            {
                trackingOffset = (int)(random.NextDouble() * TrackingError * 2 - TrackingError);
            }

            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Clamp(x + trackingOffset, 0, width - 1);
                int offset = rowOffset + x * 4;
                int srcOffset = rowOffset + sourceX * 4;

                byte r = rgba[srcOffset];
                byte g = rgba[srcOffset + 1];
                byte b = rgba[srcOffset + 2];

                // Add noise
                if (NoiseIntensity > 0)
                {
                    float noise = (float)(random.NextDouble() * 2 - 1) * NoiseIntensity * 255;
                    r = (byte)Math.Clamp(r + noise, 0, 255);
                    g = (byte)Math.Clamp(g + noise, 0, 255);
                    b = (byte)Math.Clamp(b + noise, 0, 255);
                }

                // Scan lines (darken every other line pair)
                if ((y / 2) % 2 == 0)
                {
                    r = (byte)(r * (1f - ScanLineOpacity));
                    g = (byte)(g * (1f - ScanLineOpacity));
                    b = (byte)(b * (1f - ScanLineOpacity));
                }

                rgba[offset] = r;
                rgba[offset + 1] = g;
                rgba[offset + 2] = b;
            }
        }
    }
}

/// <summary>
/// Anaglyph 3D filter (for red-cyan 3D glasses).
/// </summary>
public sealed class AnaglyphFilter
{
    public int HorizontalOffset { get; set; } = 5;
    public float RedIntensity { get; set; } = 1f;
    public float CyanIntensity { get; set; } = 1f;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride || HorizontalOffset == 0)
            return;

        Span<byte> tempBuffer = new byte[height * stride];
        rgba.CopyTo(tempBuffer);

        int absOffset = Math.Abs(HorizontalOffset);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;

            for (int x = absOffset; x < width - absOffset; x++)
            {
                int offset = rowOffset + x * 4;

                // Left eye (red channel from offset left)
                int leftX = x - HorizontalOffset;
                int leftOffset = rowOffset + leftX * 4;
                
                // Right eye (cyan from offset right - or current position)
                int rightX = x + HorizontalOffset;
                int rightOffset = rowOffset + rightX * 4;

                // Get grayscale of left/right shifted images
                byte leftGray = (byte)((tempBuffer[leftOffset] * 77 + 
                                         tempBuffer[leftOffset + 1] * 150 + 
                                         tempBuffer[leftOffset + 2] * 29) >> 8);
                
                byte rightGray = (byte)((tempBuffer[rightOffset] * 77 + 
                                          tempBuffer[rightOffset + 1] * 150 + 
                                          tempBuffer[rightOffset + 2] * 29) >> 8);

                // Red from left eye, cyan from right eye
                rgba[offset] = (byte)(leftGray * RedIntensity);      // Red
                rgba[offset + 1] = (byte)(rightGray * CyanIntensity); // Green (part of cyan)
                rgba[offset + 2] = (byte)(rightGray * CyanIntensity); // Blue (part of cyan)
            }
        }
    }
}

/// <summary>
/// Magnifying glass filter.
/// </summary>
public sealed class MagnifyFilter
{
    public float CenterX { get; set; } = 0.5f;
    public float CenterY { get; set; } = 0.5f;
    public float Radius { get; set; } = 100f;
    public float Magnification { get; set; } = 2f;

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride || Magnification <= 1f)
            return;

        Span<byte> tempBuffer = new byte[height * stride];
        rgba.CopyTo(tempBuffer);

        float centerPx = CenterX * width;
        float centerPy = CenterY * height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerPx;
                float dy = y - centerPy;
                float distance = MathF.Sqrt(dx * dx + dy * dy);

                int offset = y * stride + x * 4;

                if (distance < Radius)
                {
                    // Inside magnification area
                    float srcX = centerPx + dx / Magnification;
                    float srcY = centerPy + dy / Magnification;

                    int x0 = Math.Clamp((int)srcX, 0, width - 1);
                    int y0 = Math.Clamp((int)srcY, 0, height - 1);

                    int srcOffset = y0 * stride + x0 * 4;

                    rgba[offset] = tempBuffer[srcOffset];
                    rgba[offset + 1] = tempBuffer[srcOffset + 1];
                    rgba[offset + 2] = tempBuffer[srcOffset + 2];
                    rgba[offset + 3] = tempBuffer[srcOffset + 3];
                }
            }
        }
    }
}

/// <summary>
/// Chromakey (green/blue screen) filter.
/// </summary>
public sealed class ChromakeyFilter
{
    public byte KeyR { get; set; } = 0;
    public byte KeyG { get; set; } = 255;
    public byte KeyB { get; set; } = 0;
    public float Tolerance { get; set; } = 0.3f;
    public float Softness { get; set; } = 0.1f;

    public void SetGreenScreen() => (KeyR, KeyG, KeyB) = (0, 255, 0);
    public void SetBlueScreen() => (KeyR, KeyG, KeyB) = (0, 0, 255);

    public void Process(Span<byte> rgba, int width, int height, int stride)
    {
        if (rgba.Length < height * stride)
            return;

        float tolerance = Tolerance * 255;
        float softness = Softness * 255;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;

            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;

                byte r = rgba[offset];
                byte g = rgba[offset + 1];
                byte b = rgba[offset + 2];

                // Calculate color distance
                float dr = r - KeyR;
                float dg = g - KeyG;
                float db = b - KeyB;
                float distance = MathF.Sqrt(dr * dr + dg * dg + db * db);

                if (distance < tolerance)
                {
                    // Fully transparent
                    rgba[offset + 3] = 0;
                }
                else if (distance < tolerance + softness && softness > 0)
                {
                    // Soft edge
                    float alpha = (distance - tolerance) / softness;
                    rgba[offset + 3] = (byte)(rgba[offset + 3] * alpha);
                }
            }
        }
    }

    public void ProcessWithBackground(
        Span<byte> foreground, ReadOnlySpan<byte> background,
        int width, int height, int stride)
    {
        if (foreground.Length < height * stride || background.Length < height * stride)
            return;

        float tolerance = Tolerance * 255;
        float softness = Softness * 255;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;

            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;

                byte r = foreground[offset];
                byte g = foreground[offset + 1];
                byte b = foreground[offset + 2];

                float dr = r - KeyR;
                float dg = g - KeyG;
                float db = b - KeyB;
                float distance = MathF.Sqrt(dr * dr + dg * dg + db * db);

                float alpha;
                if (distance < tolerance)
                {
                    alpha = 0f;
                }
                else if (distance < tolerance + softness && softness > 0)
                {
                    alpha = (distance - tolerance) / softness;
                }
                else
                {
                    alpha = 1f;
                }

                // Blend foreground and background
                byte bgR = background[offset];
                byte bgG = background[offset + 1];
                byte bgB = background[offset + 2];

                foreground[offset] = (byte)(r * alpha + bgR * (1 - alpha));
                foreground[offset + 1] = (byte)(g * alpha + bgG * (1 - alpha));
                foreground[offset + 2] = (byte)(b * alpha + bgB * (1 - alpha));
            }
        }
    }
}

/// <summary>
/// High Quality 3D Denoiser - removes noise using spatial and temporal filtering.
/// Based on VLC's hqdn3d.c which was ported from MPlayer.
/// 
/// Uses low-pass filtering in three dimensions:
/// - Horizontal (left-to-right pixel correlation)
/// - Vertical (top-to-bottom pixel correlation)  
/// - Temporal (frame-to-frame correlation)
/// </summary>
public sealed class Hqdn3dFilter
{
    private const int CoefTableSize = 512 * 16;
    private const float DefaultLumaSpatial = 4.0f;
    private const float DefaultChromaSpatial = 3.0f;
    private const float DefaultLumaTemporal = 6.0f;
    
    // Precomputed coefficient tables
    private readonly int[] horizontalCoefs = new int[CoefTableSize];
    private readonly int[] verticalCoefs = new int[CoefTableSize];
    private readonly int[] temporalCoefs = new int[CoefTableSize];
    
    // State buffers
    private uint[]? lineBuffer;
    private ushort[]? previousFrameY;
    private ushort[]? previousFrameU;
    private ushort[]? previousFrameV;
    private int lastWidth;
    private int lastHeight;
    
    // Configuration
    private float lumaSpatialStrength;
    private float chromaSpatialStrength;
    private float lumaTemporalStrength;
    private float chromaTemporalStrength;
    
    /// <summary>
    /// Spatial luma denoising strength (0-254). Higher = more smoothing.
    /// Default is 4.0.
    /// </summary>
    public float LumaSpatialStrength
    {
        get => lumaSpatialStrength;
        set
        {
            lumaSpatialStrength = Math.Clamp(value, 0f, 254f);
            PrecalcCoefficients(horizontalCoefs, lumaSpatialStrength);
            PrecalcCoefficients(verticalCoefs, lumaSpatialStrength);
        }
    }
    
    /// <summary>
    /// Spatial chroma denoising strength (0-254). Higher = more smoothing.
    /// Default is 3.0.
    /// </summary>
    public float ChromaSpatialStrength
    {
        get => chromaSpatialStrength;
        set => chromaSpatialStrength = Math.Clamp(value, 0f, 254f);
    }
    
    /// <summary>
    /// Temporal luma denoising strength (0-254). Higher = more smoothing across frames.
    /// Default is 6.0.
    /// </summary>
    public float LumaTemporalStrength
    {
        get => lumaTemporalStrength;
        set
        {
            lumaTemporalStrength = Math.Clamp(value, 0f, 254f);
            PrecalcCoefficients(temporalCoefs, lumaTemporalStrength);
        }
    }
    
    /// <summary>
    /// Temporal chroma denoising strength (0-254). Higher = more smoothing across frames.
    /// Default is LumaTemporalStrength * 1.5 (auto-calculated).
    /// </summary>
    public float ChromaTemporalStrength
    {
        get => chromaTemporalStrength;
        set => chromaTemporalStrength = Math.Clamp(value, 0f, 254f);
    }
    
    /// <summary>
    /// Creates a new 3D denoiser with default settings.
    /// </summary>
    public Hqdn3dFilter()
    {
        lumaSpatialStrength = DefaultLumaSpatial;
        chromaSpatialStrength = DefaultChromaSpatial;
        lumaTemporalStrength = DefaultLumaTemporal;
        chromaTemporalStrength = lumaTemporalStrength * 1.5f;
        
        PrecalcCoefficients(horizontalCoefs, lumaSpatialStrength);
        PrecalcCoefficients(verticalCoefs, lumaSpatialStrength);
        PrecalcCoefficients(temporalCoefs, lumaTemporalStrength);
    }
    
    /// <summary>
    /// Process a BGRA frame with 3D denoising.
    /// </summary>
    /// <param name="frame">BGRA pixel data (modified in place).</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        // For BGRA, we'll process luminance only (simple approach)
        // Convert to grayscale equivalent for processing
        EnsureBuffers(width, height);
        
        // Extract Y channel (luminance approximation)
        byte[] yPlane = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            int yOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                byte b = frame[offset];
                byte g = frame[offset + 1];
                byte r = frame[offset + 2];
                // BT.601 luminance
                yPlane[yOffset + x] = (byte)((r * 66 + g * 129 + b * 25 + 128) >> 8);
            }
        }
        
        // Process Y plane
        byte[] yOutput = new byte[width * height];
        DenoiseFrame(yPlane, yOutput, width, height, width, width, 
            horizontalCoefs, verticalCoefs, temporalCoefs, ref previousFrameY);
        
        // Apply denoised luminance back while preserving color
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            int yOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                byte originalY = yPlane[yOffset + x];
                byte denoisedY = yOutput[yOffset + x];
                
                if (originalY == 0) continue;
                
                // Scale RGB by the ratio of denoised to original luminance
                float scale = (float)denoisedY / originalY;
                scale = Math.Clamp(scale, 0.5f, 1.5f);
                
                frame[offset] = (byte)Math.Clamp(frame[offset] * scale, 0, 255);
                frame[offset + 1] = (byte)Math.Clamp(frame[offset + 1] * scale, 0, 255);
                frame[offset + 2] = (byte)Math.Clamp(frame[offset + 2] * scale, 0, 255);
            }
        }
    }
    
    /// <summary>
    /// Process a Y plane (grayscale/luminance) with 3D denoising.
    /// </summary>
    /// <param name="input">Input Y plane.</param>
    /// <param name="output">Output Y plane.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="inputStride">Input row stride in bytes.</param>
    /// <param name="outputStride">Output row stride in bytes.</param>
    public void ProcessYPlane(ReadOnlySpan<byte> input, Span<byte> output, 
        int width, int height, int inputStride, int outputStride)
    {
        EnsureBuffers(width, height);
        
        byte[] inputArray = input.ToArray();
        byte[] outputArray = new byte[output.Length];
        
        DenoiseFrame(inputArray, outputArray, width, height, inputStride, outputStride,
            horizontalCoefs, verticalCoefs, temporalCoefs, ref previousFrameY);
        
        outputArray.CopyTo(output);
    }
    
    /// <summary>
    /// Reset temporal state (call when seeking or changing source).
    /// </summary>
    public void Reset()
    {
        previousFrameY = null;
        previousFrameU = null;
        previousFrameV = null;
        lineBuffer = null;
        lastWidth = 0;
        lastHeight = 0;
    }
    
    private void EnsureBuffers(int width, int height)
    {
        if (lastWidth != width || lastHeight != height)
        {
            lineBuffer = new uint[width];
            previousFrameY = null;
            previousFrameU = null;
            previousFrameV = null;
            lastWidth = width;
            lastHeight = height;
        }
    }
    
    /// <summary>
    /// Core denoising function combining spatial and temporal filtering.
    /// </summary>
    private void DenoiseFrame(byte[] input, byte[] output, 
        int width, int height, int inputStride, int outputStride,
        int[] horizontal, int[] vertical, int[] temporal,
        ref ushort[]? frameHistory)
    {
        // Initialize frame history if needed
        if (frameHistory == null)
        {
            frameHistory = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                int srcOffset = y * inputStride;
                int histOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    frameHistory[histOffset + x] = (ushort)(input[srcOffset + x] << 8);
                }
            }
        }
        
        // Spatial-only or temporal-only shortcuts
        bool doSpatial = horizontal[256 * 16] != 0 || vertical[256 * 16] != 0;
        bool doTemporal = temporal[256 * 16] != 0;
        
        if (!doSpatial && !doTemporal)
        {
            // No denoising, just copy
            for (int y = 0; y < height; y++)
            {
                int srcOffset = y * inputStride;
                int dstOffset = y * outputStride;
                for (int x = 0; x < width; x++)
                {
                    output[dstOffset + x] = input[srcOffset + x];
                }
            }
            return;
        }
        
        if (!doSpatial)
        {
            DenoiseTemporal(input, output, frameHistory, width, height, inputStride, outputStride, temporal);
            return;
        }
        
        if (!doTemporal)
        {
            DenoiseSpatial(input, output, width, height, inputStride, outputStride, horizontal, vertical);
            return;
        }
        
        // Full 3D denoising
        Denoise3D(input, output, frameHistory, width, height, inputStride, outputStride, 
            horizontal, vertical, temporal);
    }
    
    private void DenoiseTemporal(byte[] input, byte[] output, ushort[] frameHistory,
        int width, int height, int inputStride, int outputStride, int[] temporal)
    {
        for (int y = 0; y < height; y++)
        {
            int srcOffset = y * inputStride;
            int dstOffset = y * outputStride;
            int histOffset = y * width;
            
            for (int x = 0; x < width; x++)
            {
                uint pixelDst = LowPassMul((uint)frameHistory[histOffset + x] << 8, 
                    (uint)input[srcOffset + x] << 16, temporal);
                frameHistory[histOffset + x] = (ushort)((pixelDst + 0x1000007F) >> 8);
                output[dstOffset + x] = (byte)((pixelDst + 0x10007FFF) >> 16);
            }
        }
    }
    
    private void DenoiseSpatial(byte[] input, byte[] output,
        int width, int height, int inputStride, int outputStride,
        int[] horizontal, int[] vertical)
    {
        uint[] lineAnt = lineBuffer!;
        
        // First pixel
        uint pixelAnt = lineAnt[0] = (uint)input[0] << 16;
        output[0] = (byte)((pixelAnt + 0x10007FFF) >> 16);
        
        // First line (no top neighbor)
        for (int x = 1; x < width; x++)
        {
            uint pixelDst = lineAnt[x] = LowPassMul(pixelAnt, (uint)input[x] << 16, horizontal);
            output[x] = (byte)((pixelDst + 0x10007FFF) >> 16);
            pixelAnt = pixelDst;
        }
        
        // Rest of lines
        for (int y = 1; y < height; y++)
        {
            int srcOffset = y * inputStride;
            int dstOffset = y * outputStride;
            
            // First pixel (no left neighbor)
            pixelAnt = (uint)input[srcOffset] << 16;
            uint pixelDst = lineAnt[0] = LowPassMul(lineAnt[0], pixelAnt, vertical);
            output[dstOffset] = (byte)((pixelDst + 0x10007FFF) >> 16);
            
            for (int x = 1; x < width; x++)
            {
                pixelAnt = LowPassMul(pixelAnt, (uint)input[srcOffset + x] << 16, horizontal);
                pixelDst = lineAnt[x] = LowPassMul(lineAnt[x], pixelAnt, vertical);
                output[dstOffset + x] = (byte)((pixelDst + 0x10007FFF) >> 16);
            }
        }
    }
    
    private void Denoise3D(byte[] input, byte[] output, ushort[] frameHistory,
        int width, int height, int inputStride, int outputStride,
        int[] horizontal, int[] vertical, int[] temporal)
    {
        uint[] lineAnt = lineBuffer!;
        
        // First pixel
        uint pixelAnt = lineAnt[0] = (uint)input[0] << 16;
        uint pixelDst = LowPassMul((uint)frameHistory[0] << 8, pixelAnt, temporal);
        frameHistory[0] = (ushort)((pixelDst + 0x1000007F) >> 8);
        output[0] = (byte)((pixelDst + 0x10007FFF) >> 16);
        
        // First line
        for (int x = 1; x < width; x++)
        {
            pixelAnt = lineAnt[x] = LowPassMul(pixelAnt, (uint)input[x] << 16, horizontal);
            pixelDst = LowPassMul((uint)frameHistory[x] << 8, pixelAnt, temporal);
            frameHistory[x] = (ushort)((pixelDst + 0x1000007F) >> 8);
            output[x] = (byte)((pixelDst + 0x10007FFF) >> 16);
        }
        
        // Rest of frame
        for (int y = 1; y < height; y++)
        {
            int srcOffset = y * inputStride;
            int dstOffset = y * outputStride;
            int histOffset = y * width;
            
            // First pixel of row
            pixelAnt = (uint)input[srcOffset] << 16;
            lineAnt[0] = LowPassMul(lineAnt[0], pixelAnt, vertical);
            pixelDst = LowPassMul((uint)frameHistory[histOffset] << 8, lineAnt[0], temporal);
            frameHistory[histOffset] = (ushort)((pixelDst + 0x1000007F) >> 8);
            output[dstOffset] = (byte)((pixelDst + 0x10007FFF) >> 16);
            
            for (int x = 1; x < width; x++)
            {
                pixelAnt = LowPassMul(pixelAnt, (uint)input[srcOffset + x] << 16, horizontal);
                lineAnt[x] = LowPassMul(lineAnt[x], pixelAnt, vertical);
                pixelDst = LowPassMul((uint)frameHistory[histOffset + x] << 8, lineAnt[x], temporal);
                frameHistory[histOffset + x] = (ushort)((pixelDst + 0x1000007F) >> 8);
                output[dstOffset + x] = (byte)((pixelDst + 0x10007FFF) >> 16);
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LowPassMul(uint prevMul, uint currMul, int[] coef)
    {
        int dMul = (int)prevMul - (int)currMul;
        uint d = (uint)((dMul + 0x10007FF) >> 12);
        int coefIndex = (int)Math.Clamp(256 * 16 + (int)d, 0, CoefTableSize - 1);
        return (uint)((int)currMul + coef[coefIndex]);
    }
    
    /// <summary>
    /// Precalculate coefficient table for a given strength.
    /// </summary>
    private static void PrecalcCoefficients(int[] coef, float dist25)
    {
        if (dist25 == 0)
        {
            Array.Clear(coef);
            return;
        }

        double gamma = Math.Log(0.25) / Math.Log(1.0 - dist25 / 255.0 - 0.00001);

        for (int i = -255 * 16; i <= 255 * 16; i++)
        {
            double simil = 1.0 - Math.Abs(i) / (16.0 * 255.0);
            double c = Math.Pow(simil, gamma) * 65536.0 * i / 16.0;
            coef[16 * 256 + i] = (int)(c < 0 ? c - 0.5 : c + 0.5);
        }

        coef[0] = 1; // Flag that denoising is active
    }
}

/// <summary>
/// Psychedelic color cycling effect that shifts hue over time.
/// Based on VLC's psychedelic.c which creates trippy color variations.
/// </summary>
public sealed class PsychedelicFilter
{
    private float hueOffset;
    private float saturationBoost;
    private ulong frameCount;
    
    /// <summary>
    /// Speed of hue cycling (degrees per frame). Default is 5.
    /// </summary>
    public float HueCycleSpeed { get; set; } = 5f;
    
    /// <summary>
    /// Amount to boost saturation (0-2). Default is 1.5.
    /// </summary>
    public float SaturationBoost
    {
        get => saturationBoost;
        set => saturationBoost = Math.Clamp(value, 0f, 2f);
    }
    
    /// <summary>
    /// Whether to vary hue based on position for more trippy effect.
    /// </summary>
    public bool SpatialVariation { get; set; } = true;
    
    public PsychedelicFilter()
    {
        saturationBoost = 1.5f;
    }
    
    /// <summary>
    /// Process a BGRA frame with psychedelic color cycling.
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        frameCount++;
        hueOffset = (hueOffset + HueCycleSpeed) % 360f;
        
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            float spatialHueOffset = SpatialVariation ? (float)y / height * 60f : 0f;
            
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                
                byte b = frame[offset];
                byte g = frame[offset + 1];
                byte r = frame[offset + 2];
                
                // Convert RGB to HSV
                RgbToHsv(r, g, b, out float h, out float s, out float v);
                
                // Apply psychedelic transformations
                float spatialXOffset = SpatialVariation ? (float)x / width * 60f : 0f;
                h = (h + hueOffset + spatialHueOffset + spatialXOffset) % 360f;
                s = Math.Min(s * saturationBoost, 1f);
                
                // Convert back to RGB
                HsvToRgb(h, s, v, out byte newR, out byte newG, out byte newB);
                
                frame[offset] = newB;
                frame[offset + 1] = newG;
                frame[offset + 2] = newR;
            }
        }
    }
    
    /// <summary>
    /// Reset the animation state.
    /// </summary>
    public void Reset()
    {
        hueOffset = 0f;
        frameCount = 0;
    }
    
    private static void RgbToHsv(byte r, byte g, byte b, out float h, out float s, out float v)
    {
        float rf = r / 255f;
        float gf = g / 255f;
        float bf = b / 255f;
        
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;
        
        v = max;
        s = max == 0 ? 0 : delta / max;
        
        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60f * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60f * ((bf - rf) / delta + 2);
        }
        else
        {
            h = 60f * ((rf - gf) / delta + 4);
        }
        
        if (h < 0) h += 360;
    }
    
    private static void HsvToRgb(float h, float s, float v, out byte r, out byte g, out byte b)
    {
        float c = v * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = v - c;
        
        float rf, gf, bf;
        
        if (h < 60) { rf = c; gf = x; bf = 0; }
        else if (h < 120) { rf = x; gf = c; bf = 0; }
        else if (h < 180) { rf = 0; gf = c; bf = x; }
        else if (h < 240) { rf = 0; gf = x; bf = c; }
        else if (h < 300) { rf = x; gf = 0; bf = c; }
        else { rf = c; gf = 0; bf = x; }
        
        r = (byte)((rf + m) * 255);
        g = (byte)((gf + m) * 255);
        b = (byte)((bf + m) * 255);
    }
}

/// <summary>
/// Motion detection filter that highlights areas of movement.
/// Based on VLC's motiondetect.c.
/// </summary>
public sealed class MotionDetectionFilter
{
    private byte[]? previousFrame;
    private int lastWidth;
    private int lastHeight;
    
    /// <summary>
    /// Threshold for motion detection (0-255). Lower = more sensitive.
    /// </summary>
    public byte Threshold { get; set; } = 20;
    
    /// <summary>
    /// Color to highlight motion (BGRA format).
    /// </summary>
    public uint HighlightColor { get; set; } = 0xFF00FF00; // Green
    
    /// <summary>
    /// Whether to show only motion (true) or overlay on original (false).
    /// </summary>
    public bool MotionOnly { get; set; }
    
    /// <summary>
    /// Process a BGRA frame for motion detection.
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        int frameSize = width * height * 4;
        
        if (previousFrame == null || lastWidth != width || lastHeight != height)
        {
            previousFrame = frame.ToArray();
            lastWidth = width;
            lastHeight = height;
            return;
        }
        
        byte highlightB = (byte)(HighlightColor & 0xFF);
        byte highlightG = (byte)((HighlightColor >> 8) & 0xFF);
        byte highlightR = (byte)((HighlightColor >> 16) & 0xFF);
        
        byte[] currentCopy = frame.ToArray();
        
        for (int i = 0; i < frameSize; i += 4)
        {
            int diff = Math.Abs(frame[i] - previousFrame[i]) +
                       Math.Abs(frame[i + 1] - previousFrame[i + 1]) +
                       Math.Abs(frame[i + 2] - previousFrame[i + 2]);
            
            if (diff > Threshold * 3)
            {
                // Motion detected
                frame[i] = highlightB;
                frame[i + 1] = highlightG;
                frame[i + 2] = highlightR;
            }
            else if (MotionOnly)
            {
                // No motion and motion-only mode
                frame[i] = 0;
                frame[i + 1] = 0;
                frame[i + 2] = 0;
            }
        }
        
        currentCopy.CopyTo(previousFrame, 0);
    }
    
    /// <summary>
    /// Reset the motion history.
    /// </summary>
    public void Reset()
    {
        previousFrame = null;
    }
}

/// <summary>
/// Antiflicker filter to reduce flicker in interlaced or problematic video.
/// Based on VLC's antiflicker.c.
/// </summary>
public sealed class AntiflickerFilter
{
    private float[]? previousLuminance;
    private int lastWidth;
    private int lastHeight;
    
    /// <summary>
    /// Softening factor (0-1). Higher = more temporal smoothing.
    /// </summary>
    public float SofteningFactor { get; set; } = 0.5f;
    
    /// <summary>
    /// Process a BGRA frame to reduce flicker.
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        int pixelCount = width * height;
        
        if (previousLuminance == null || lastWidth != width || lastHeight != height)
        {
            previousLuminance = new float[pixelCount];
            lastWidth = width;
            lastHeight = height;
            
            // Initialize with current frame luminance
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width * 4;
                int lumOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int offset = rowOffset + x * 4;
                    byte b = frame[offset];
                    byte g = frame[offset + 1];
                    byte r = frame[offset + 2];
                    previousLuminance[lumOffset + x] = 0.299f * r + 0.587f * g + 0.114f * b;
                }
            }
            return;
        }
        
        float softening = Math.Clamp(SofteningFactor, 0f, 1f);
        float oneMinusSoftening = 1f - softening;
        
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            int lumOffset = y * width;
            
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                
                byte b = frame[offset];
                byte g = frame[offset + 1];
                byte r = frame[offset + 2];
                
                float currentLuminance = 0.299f * r + 0.587f * g + 0.114f * b;
                float prevLuminance = previousLuminance[lumOffset + x];
                
                // Temporal blend of luminance
                float targetLuminance = prevLuminance * softening + currentLuminance * oneMinusSoftening;
                
                // Scale RGB to match target luminance
                if (currentLuminance > 0.001f)
                {
                    float scale = targetLuminance / currentLuminance;
                    scale = Math.Clamp(scale, 0.5f, 1.5f);
                    
                    frame[offset] = (byte)Math.Clamp(b * scale, 0, 255);
                    frame[offset + 1] = (byte)Math.Clamp(g * scale, 0, 255);
                    frame[offset + 2] = (byte)Math.Clamp(r * scale, 0, 255);
                }
                
                previousLuminance[lumOffset + x] = targetLuminance;
            }
        }
    }
    
    /// <summary>
    /// Reset the filter state.
    /// </summary>
    public void Reset()
    {
        previousLuminance = null;
    }
}

/// <summary>
/// Sharpen filter to enhance image edges and details.
/// Uses unsharp mask technique.
/// </summary>
public sealed class SharpenFilter
{
    /// <summary>
    /// Sharpening strength (0-3). Default is 0.5.
    /// </summary>
    public float Strength { get; set; } = 0.5f;
    
    /// <summary>
    /// Process a BGRA frame with sharpening.
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height, Span<byte> workBuffer)
    {
        // Copy original to work buffer
        frame.CopyTo(workBuffer);
        
        // Apply 3x3 unsharp mask
        float strength = Math.Clamp(Strength, 0f, 3f);
        float center = 1f + 4f * strength;
        float neighbor = -strength;
        
        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * width * 4;
            int prevRow = (y - 1) * width * 4;
            int nextRow = (y + 1) * width * 4;
            
            for (int x = 1; x < width - 1; x++)
            {
                int offset = rowOffset + x * 4;
                
                for (int c = 0; c < 3; c++)
                {
                    float sum = workBuffer[offset + c] * center +
                                workBuffer[offset - 4 + c] * neighbor +
                                workBuffer[offset + 4 + c] * neighbor +
                                workBuffer[prevRow + x * 4 + c] * neighbor +
                                workBuffer[nextRow + x * 4 + c] * neighbor;
                    
                    frame[offset + c] = (byte)Math.Clamp(sum, 0f, 255f);
                }
            }
        }
    }
    
    /// <summary>
    /// Process a BGRA frame with sharpening (allocates work buffer).
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        byte[] workBuffer = new byte[frame.Length];
        ProcessBgra(frame, width, height, workBuffer);
    }
}

/// <summary>
/// Debanding filter that removes banding artifacts in gradients.
/// Based on VLC's gradfun filter (from libav).
/// Uses box blur followed by threshold-based blending and dithering.
/// </summary>
public sealed class GradfunFilter
{
    // 8x8 ordered dither matrix (Bayer pattern)
    private static readonly ushort[,] DitherMatrix = 
    {
        {  0, 96, 24,120,  6,102, 30,126 },
        { 64, 32, 88, 56, 70, 38, 94, 62 },
        { 16,112,  8,104, 22,118, 14,110 },
        { 80, 48, 72, 40, 86, 54, 78, 46 },
        {  4,100, 28,124,  2, 98, 26,122 },
        { 68, 36, 92, 60, 66, 34, 90, 58 },
        { 20,116, 12,108, 18,114, 10,106 },
        { 84, 52, 76, 44, 82, 50, 74, 42 },
    };
    
    private readonly int radius;
    private readonly int threshold;
    private ushort[]? blurBuffer;
    private ushort[]? accumulatorBuffer;
    private ushort[]? columnDeltaBuffer;
    
    /// <summary>
    /// Radius for box blur (4-32). Larger values detect larger gradients.
    /// </summary>
    public int Radius => radius;
    
    /// <summary>
    /// Creates a new gradfun debanding filter.
    /// </summary>
    /// <param name="strength">Filter strength (0.51-255). Higher values affect more pixels. Default 1.2.</param>
    /// <param name="radius">Blur radius (4-32). Higher values detect larger gradients. Default 16.</param>
    public GradfunFilter(float strength = 1.2f, int radius = 16)
    {
        this.radius = Math.Clamp(radius, 4, 32);
        
        // Convert strength to threshold (inverse relationship)
        strength = Math.Clamp(strength, 0.51f, 255f);
        threshold = (int)(65536.0f / strength);
    }
    
    /// <summary>
    /// Process a Y-plane (luma) with debanding.
    /// </summary>
    /// <param name="source">Source plane (read-only)</param>
    /// <param name="destination">Destination plane (output)</param>
    /// <param name="width">Width in pixels</param>
    /// <param name="height">Height in pixels</param>
    /// <param name="sourceStride">Source stride in bytes</param>
    /// <param name="destinationStride">Destination stride in bytes</param>
    public void ProcessPlane(ReadOnlySpan<byte> source, Span<byte> destination, 
        int width, int height, int sourceStride, int destinationStride)
    {
        if (width < radius * 2 || height < radius * 2)
        {
            // Too small, just copy
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * sourceStride, width)
                      .CopyTo(destination.Slice(y * destinationStride, width));
            }
            return;
        }
        
        int blurStride = ((width + 15) & ~15) / 2;
        int bufferSize = blurStride * (radius + 16);
        
        // Ensure buffers are allocated
        if (blurBuffer == null || blurBuffer.Length < bufferSize)
        {
            blurBuffer = new ushort[bufferSize];
            accumulatorBuffer = new ushort[bufferSize];
        }
        
        if (columnDeltaBuffer == null || columnDeltaBuffer.Length < blurStride + 16)
        {
            columnDeltaBuffer = new ushort[blurStride + 16];
        }
        
        // DC factor for normalizing the box blur sum
        uint dcFactor = (uint)((1 << 21) / (radius * radius));
        
        Array.Clear(columnDeltaBuffer, 0, columnDeltaBuffer.Length);
        Array.Clear(blurBuffer, 0, blurBuffer.Length);
        
        // Build initial blur accumulator for first 'radius' rows
        for (int y = 0; y < radius; y++)
        {
            BlurLine(source, sourceStride, width, y * 2, blurStride, y);
        }
        
        // Process each output row
        for (int y = 0; y < height; )
        {
            // Add new row to blur if we haven't reached the bottom
            if (y + radius < height)
            {
                int modIndex = ((y + radius) / 2) % radius;
                BlurLine(source, sourceStride, width, y + radius, blurStride, modIndex);
                
                // Compute horizontal running sum in columnDeltaBuffer
                int runningSum = 0;
                for (int x = 0; x < radius; x++)
                {
                    runningSum += columnDeltaBuffer[x];
                }
                
                for (int x = radius; x < width / 2; x++)
                {
                    runningSum += columnDeltaBuffer[x] - columnDeltaBuffer[x - radius];
                    int dcValue = (int)((runningSum * dcFactor) >> 16);
                    columnDeltaBuffer[x - radius] = (ushort)dcValue;
                }
                
                // Handle tail
                for (int x = width / 2; x < (width + radius + 1) / 2; x++)
                {
                    int dcValue = (int)((runningSum * dcFactor) >> 16);
                    if (x - radius >= 0)
                        columnDeltaBuffer[x - radius] = (ushort)dcValue;
                }
                
                // Extend left edge
                for (int x = -radius / 2; x < 0; x++)
                {
                    if (x + radius / 2 < columnDeltaBuffer.Length)
                        columnDeltaBuffer[x + radius / 2] = columnDeltaBuffer[0];
                }
            }
            
            // Output rows
            if (y == radius)
            {
                // Output the initial rows that were waiting for blur data
                for (int initialY = 0; initialY < radius; initialY++)
                {
                    FilterLine(source, destination, sourceStride, destinationStride, 
                              width, initialY);
                }
            }
            
            FilterLine(source, destination, sourceStride, destinationStride, width, y);
            y++;
            
            if (y >= height) break;
            
            FilterLine(source, destination, sourceStride, destinationStride, width, y);
            y++;
        }
    }
    
    private void BlurLine(ReadOnlySpan<byte> source, int sourceStride, int width, 
        int y, int blurStride, int bufferIndex)
    {
        if (y * 2 + 1 >= source.Length / sourceStride) return;
        
        int srcOffset1 = y * sourceStride;
        int srcOffset2 = srcOffset1 + sourceStride;
        
        for (int x = 0; x < width / 2 && x < blurBuffer!.Length; x++)
        {
            int srcX = x * 2;
            if (srcX + 1 >= width) break;
            
            int v = source[srcOffset1 + srcX] + 
                    source[srcOffset1 + srcX + 1] + 
                    (srcOffset2 + srcX + 1 < source.Length 
                        ? source[srcOffset2 + srcX] + source[srcOffset2 + srcX + 1] 
                        : 0);
            
            int bufIdx = bufferIndex * blurStride + x;
            if (bufIdx < blurBuffer.Length)
            {
                int oldValue = blurBuffer[bufIdx];
                blurBuffer[bufIdx] = (ushort)v;
                
                if (x < columnDeltaBuffer!.Length)
                {
                    columnDeltaBuffer[x] = (ushort)(v - oldValue);
                }
            }
        }
    }
    
    private void FilterLine(ReadOnlySpan<byte> source, Span<byte> destination,
        int sourceStride, int destinationStride, int width, int y)
    {
        int srcRowOffset = y * sourceStride;
        int dstRowOffset = y * destinationStride;
        int ditherRow = y & 7;
        
        for (int x = 0; x < width; x++)
        {
            int srcIdx = srcRowOffset + x;
            int dstIdx = dstRowOffset + x;
            
            if (srcIdx >= source.Length || dstIdx >= destination.Length)
                break;
            
            int pixValue = source[srcIdx] << 7;
            
            // Get DC value (blurred average) for this position
            int dcIndex = x / 2;
            int dcValue = dcIndex < columnDeltaBuffer!.Length 
                ? columnDeltaBuffer[dcIndex] << 7 
                : pixValue;
            
            // Calculate delta between pixel and blur
            int delta = dcValue - pixValue;
            int absDelta = Math.Abs(delta);
            
            // Calculate modification factor based on threshold
            int modification = (absDelta * threshold) >> 16;
            modification = Math.Max(0, 127 - modification);
            modification = (modification * modification * delta) >> 14;
            
            // Add dither and modification
            int dither = DitherMatrix[ditherRow, x & 7];
            int result = pixValue + modification + dither;
            
            // Clamp and store
            destination[dstIdx] = (byte)Math.Clamp(result >> 7, 0, 255);
        }
    }
    
    /// <summary>
    /// Process a BGRA frame with debanding (processes luma component only).
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        // Extract Y channel, process, and apply back
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            int ditherRow = y & 7;
            
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                
                // Get RGB
                int b = frame[offset];
                int g = frame[offset + 1];
                int r = frame[offset + 2];
                
                // Convert to Y (BT.601)
                int yValue = (66 * r + 129 * g + 25 * b + 128) >> 8 + 16;
                yValue = Math.Clamp(yValue, 16, 235);
                
                // Apply debanding to luminance with simpler approach
                int dither = (DitherMatrix[ditherRow, x & 7] - 64) >> 4;
                
                // Subtle luminance modification
                float factor = (yValue + dither) / (float)Math.Max(1, yValue);
                factor = Math.Clamp(factor, 0.95f, 1.05f);
                
                frame[offset] = (byte)Math.Clamp((int)(b * factor), 0, 255);
                frame[offset + 1] = (byte)Math.Clamp((int)(g * factor), 0, 255);
                frame[offset + 2] = (byte)Math.Clamp((int)(r * factor), 0, 255);
            }
        }
    }
    
    /// <summary>
    /// Gets a dither value from the Bayer matrix.
    /// </summary>
    internal static int GetDitherValue(int x, int y)
    {
        return DitherMatrix[y & 7, x & 7];
    }
}

/// <summary>
/// Dithering filter for reducing color depth with various algorithms.
/// Based on VLC's dither filter.
/// </summary>
public sealed class DitherFilter
{
    private readonly DitherAlgorithm algorithm;
    private readonly int[] palette;
    private int[]? errorBufferR;
    private int[]? errorBufferG;
    private int[]? errorBufferB;
    
    /// <summary>
    /// The dithering algorithm to use.
    /// </summary>
    public DitherAlgorithm Algorithm => algorithm;
    
    /// <summary>
    /// Creates a new dither filter with a color palette.
    /// </summary>
    /// <param name="palette">Array of target colors (as packed ARGB integers)</param>
    /// <param name="algorithm">Dithering algorithm to use</param>
    public DitherFilter(int[] palette, DitherAlgorithm algorithm = DitherAlgorithm.FloydSteinberg)
    {
        this.palette = palette.Length > 0 ? palette : new[] { unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000) };
        this.algorithm = algorithm;
    }
    
    /// <summary>
    /// Creates a dither filter with default black and white palette.
    /// </summary>
    public DitherFilter(DitherAlgorithm algorithm = DitherAlgorithm.FloydSteinberg)
        : this(new[] { unchecked((int)0xFFFFFFFF), unchecked((int)0xFF000000) }, algorithm)
    {
    }
    
    /// <summary>
    /// Creates a dither filter for reducing to N colors from current.
    /// </summary>
    /// <param name="colorBits">Bits per channel (1-8). 1=2 colors, 2=4 colors, etc.</param>
    /// <param name="algorithm">Dithering algorithm</param>
    public static DitherFilter CreateForBitDepth(int colorBits, DitherAlgorithm algorithm = DitherAlgorithm.FloydSteinberg)
    {
        colorBits = Math.Clamp(colorBits, 1, 8);
        int levels = 1 << colorBits;
        var palette = new List<int>();
        
        for (int r = 0; r < levels; r++)
        {
            for (int g = 0; g < levels; g++)
            {
                for (int b = 0; b < levels; b++)
                {
                    int rv = (r * 255) / (levels - 1);
                    int gv = (g * 255) / (levels - 1);
                    int bv = (b * 255) / (levels - 1);
                    palette.Add(unchecked((int)0xFF000000) | (rv << 16) | (gv << 8) | bv);
                }
            }
        }
        
        return new DitherFilter(palette.ToArray(), algorithm);
    }
    
    /// <summary>
    /// Process a BGRA frame with dithering.
    /// </summary>
    public void ProcessBgra(Span<byte> frame, int width, int height)
    {
        // Ensure error buffers are allocated
        if (errorBufferR == null || errorBufferR.Length < width + 2)
        {
            errorBufferR = new int[width + 2];
            errorBufferG = new int[width + 2];
            errorBufferB = new int[width + 2];
        }
        
        switch (algorithm)
        {
            case DitherAlgorithm.FloydSteinberg:
                ProcessFloydSteinberg(frame, width, height);
                break;
            case DitherAlgorithm.SierraLite:
                ProcessSierraLite(frame, width, height);
                break;
            case DitherAlgorithm.JarvisJudiceNinke:
                ProcessJarvisJudiceNinke(frame, width, height);
                break;
            case DitherAlgorithm.Random:
                ProcessRandom(frame, width, height);
                break;
            case DitherAlgorithm.Ordered:
                ProcessOrdered(frame, width, height);
                break;
            default:
                ProcessFloydSteinberg(frame, width, height);
                break;
        }
    }
    
    private void ProcessFloydSteinberg(Span<byte> frame, int width, int height)
    {
        Array.Clear(errorBufferR!, 0, errorBufferR.Length);
        Array.Clear(errorBufferG!, 0, errorBufferG.Length);
        Array.Clear(errorBufferB!, 0, errorBufferB.Length);
        
        for (int y = 0; y < height; y++)
        {
            int[] nextErrorR = new int[width + 2];
            int[] nextErrorG = new int[width + 2];
            int[] nextErrorB = new int[width + 2];
            
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                
                // Get current pixel with error from previous rows
                int b = frame[offset] + errorBufferB![x + 1];
                int g = frame[offset + 1] + errorBufferG![x + 1];
                int r = frame[offset + 2] + errorBufferR![x + 1];
                
                b = Math.Clamp(b, 0, 255);
                g = Math.Clamp(g, 0, 255);
                r = Math.Clamp(r, 0, 255);
                
                // Find nearest palette color
                int nearestColor = FindNearestColor(r, g, b);
                int nr = (nearestColor >> 16) & 0xFF;
                int ng = (nearestColor >> 8) & 0xFF;
                int nb = nearestColor & 0xFF;
                
                // Store result
                frame[offset] = (byte)nb;
                frame[offset + 1] = (byte)ng;
                frame[offset + 2] = (byte)nr;
                
                // Calculate error
                int errR = r - nr;
                int errG = g - ng;
                int errB = b - nb;
                
                // Distribute error (Floyd-Steinberg pattern)
                // Right: 7/16
                errorBufferR![x + 2] += (errR * 7) >> 4;
                errorBufferG![x + 2] += (errG * 7) >> 4;
                errorBufferB![x + 2] += (errB * 7) >> 4;
                
                // Bottom-left: 3/16
                nextErrorR[x] += (errR * 3) >> 4;
                nextErrorG[x] += (errG * 3) >> 4;
                nextErrorB[x] += (errB * 3) >> 4;
                
                // Bottom: 5/16
                nextErrorR[x + 1] += (errR * 5) >> 4;
                nextErrorG[x + 1] += (errG * 5) >> 4;
                nextErrorB[x + 1] += (errB * 5) >> 4;
                
                // Bottom-right: 1/16
                nextErrorR[x + 2] += (errR * 1) >> 4;
                nextErrorG[x + 2] += (errG * 1) >> 4;
                nextErrorB[x + 2] += (errB * 1) >> 4;
            }
            
            errorBufferR = nextErrorR;
            errorBufferG = nextErrorG;
            errorBufferB = nextErrorB;
        }
    }
    
    private void ProcessSierraLite(Span<byte> frame, int width, int height)
    {
        // Sierra Filter-Lite: 2-row, simpler than full Sierra
        Array.Clear(errorBufferR!, 0, errorBufferR.Length);
        Array.Clear(errorBufferG!, 0, errorBufferG.Length);
        Array.Clear(errorBufferB!, 0, errorBufferB.Length);
        
        for (int y = 0; y < height; y++)
        {
            int[] nextErrorR = new int[width + 2];
            int[] nextErrorG = new int[width + 2];
            int[] nextErrorB = new int[width + 2];
            
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                
                int b = Math.Clamp(frame[offset] + errorBufferB![x + 1], 0, 255);
                int g = Math.Clamp(frame[offset + 1] + errorBufferG![x + 1], 0, 255);
                int r = Math.Clamp(frame[offset + 2] + errorBufferR![x + 1], 0, 255);
                
                int nearestColor = FindNearestColor(r, g, b);
                int nr = (nearestColor >> 16) & 0xFF;
                int ng = (nearestColor >> 8) & 0xFF;
                int nb = nearestColor & 0xFF;
                
                frame[offset] = (byte)nb;
                frame[offset + 1] = (byte)ng;
                frame[offset + 2] = (byte)nr;
                
                int errR = r - nr, errG = g - ng, errB = b - nb;
                
                // Sierra-Lite: Right 2/4, Bottom-left 1/4, Bottom 1/4
                errorBufferR![x + 2] += errR >> 1;
                errorBufferG![x + 2] += errG >> 1;
                errorBufferB![x + 2] += errB >> 1;
                
                nextErrorR[x] += errR >> 2;
                nextErrorG[x] += errG >> 2;
                nextErrorB[x] += errB >> 2;
                
                nextErrorR[x + 1] += errR >> 2;
                nextErrorG[x + 1] += errG >> 2;
                nextErrorB[x + 1] += errB >> 2;
            }
            
            errorBufferR = nextErrorR;
            errorBufferG = nextErrorG;
            errorBufferB = nextErrorB;
        }
    }
    
    private void ProcessJarvisJudiceNinke(Span<byte> frame, int width, int height)
    {
        // JJN uses 3 rows of error diffusion
        var errorRow1R = new int[width + 4];
        var errorRow1G = new int[width + 4];
        var errorRow1B = new int[width + 4];
        var errorRow2R = new int[width + 4];
        var errorRow2G = new int[width + 4];
        var errorRow2B = new int[width + 4];
        
        for (int y = 0; y < height; y++)
        {
            var nextRow1R = new int[width + 4];
            var nextRow1G = new int[width + 4];
            var nextRow1B = new int[width + 4];
            var nextRow2R = new int[width + 4];
            var nextRow2G = new int[width + 4];
            var nextRow2B = new int[width + 4];
            
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                
                int b = Math.Clamp(frame[offset] + errorRow1B[x + 2], 0, 255);
                int g = Math.Clamp(frame[offset + 1] + errorRow1G[x + 2], 0, 255);
                int r = Math.Clamp(frame[offset + 2] + errorRow1R[x + 2], 0, 255);
                
                int nearestColor = FindNearestColor(r, g, b);
                int nr = (nearestColor >> 16) & 0xFF;
                int ng = (nearestColor >> 8) & 0xFF;
                int nb = nearestColor & 0xFF;
                
                frame[offset] = (byte)nb;
                frame[offset + 1] = (byte)ng;
                frame[offset + 2] = (byte)nr;
                
                int errR = r - nr, errG = g - ng, errB = b - nb;
                
                // JJN kernel: /48
                // Right pixels on current row
                errorRow1R[x + 3] += (errR * 7) / 48;
                errorRow1G[x + 3] += (errG * 7) / 48;
                errorRow1B[x + 3] += (errB * 7) / 48;
                
                errorRow1R[x + 4] += (errR * 5) / 48;
                errorRow1G[x + 4] += (errG * 5) / 48;
                errorRow1B[x + 4] += (errB * 5) / 48;
                
                // Next row
                nextRow1R[x] += (errR * 3) / 48;
                nextRow1G[x] += (errG * 3) / 48;
                nextRow1B[x] += (errB * 3) / 48;
                
                nextRow1R[x + 1] += (errR * 5) / 48;
                nextRow1G[x + 1] += (errG * 5) / 48;
                nextRow1B[x + 1] += (errB * 5) / 48;
                
                nextRow1R[x + 2] += (errR * 7) / 48;
                nextRow1G[x + 2] += (errG * 7) / 48;
                nextRow1B[x + 2] += (errB * 7) / 48;
                
                nextRow1R[x + 3] += (errR * 5) / 48;
                nextRow1G[x + 3] += (errG * 5) / 48;
                nextRow1B[x + 3] += (errB * 5) / 48;
                
                nextRow1R[x + 4] += (errR * 3) / 48;
                nextRow1G[x + 4] += (errG * 3) / 48;
                nextRow1B[x + 4] += (errB * 3) / 48;
                
                // Row after next
                nextRow2R[x] += (errR * 1) / 48;
                nextRow2G[x] += (errG * 1) / 48;
                nextRow2B[x] += (errB * 1) / 48;
                
                nextRow2R[x + 1] += (errR * 3) / 48;
                nextRow2G[x + 1] += (errG * 3) / 48;
                nextRow2B[x + 1] += (errB * 3) / 48;
                
                nextRow2R[x + 2] += (errR * 5) / 48;
                nextRow2G[x + 2] += (errG * 5) / 48;
                nextRow2B[x + 2] += (errB * 5) / 48;
                
                nextRow2R[x + 3] += (errR * 3) / 48;
                nextRow2G[x + 3] += (errG * 3) / 48;
                nextRow2B[x + 3] += (errB * 3) / 48;
                
                nextRow2R[x + 4] += (errR * 1) / 48;
                nextRow2G[x + 4] += (errG * 1) / 48;
                nextRow2B[x + 4] += (errB * 1) / 48;
            }
            
            // Shift rows and add pending second row
            for (int i = 0; i < width + 4; i++)
            {
                nextRow1R[i] += errorRow2R[i];
                nextRow1G[i] += errorRow2G[i];
                nextRow1B[i] += errorRow2B[i];
            }
            
            errorRow1R = nextRow1R;
            errorRow1G = nextRow1G;
            errorRow1B = nextRow1B;
            errorRow2R = nextRow2R;
            errorRow2G = nextRow2G;
            errorRow2B = nextRow2B;
        }
    }
    
    private void ProcessRandom(Span<byte> frame, int width, int height)
    {
        var random = new Random();
        const int noiseAmplitude = 16;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                
                int b = frame[offset] + random.Next(-noiseAmplitude, noiseAmplitude + 1);
                int g = frame[offset + 1] + random.Next(-noiseAmplitude, noiseAmplitude + 1);
                int r = frame[offset + 2] + random.Next(-noiseAmplitude, noiseAmplitude + 1);
                
                b = Math.Clamp(b, 0, 255);
                g = Math.Clamp(g, 0, 255);
                r = Math.Clamp(r, 0, 255);
                
                int nearestColor = FindNearestColor(r, g, b);
                
                frame[offset] = (byte)(nearestColor & 0xFF);
                frame[offset + 1] = (byte)((nearestColor >> 8) & 0xFF);
                frame[offset + 2] = (byte)((nearestColor >> 16) & 0xFF);
            }
        }
    }
    
    private void ProcessOrdered(Span<byte> frame, int width, int height)
    {
        // 8x8 Bayer ordered dithering
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                
                // Get threshold from dither matrix (-0.5 to 0.5 range, scaled)
                int threshold = (GradfunFilter.GetDitherValue(x, y) - 64) / 2;
                
                int b = Math.Clamp(frame[offset] + threshold, 0, 255);
                int g = Math.Clamp(frame[offset + 1] + threshold, 0, 255);
                int r = Math.Clamp(frame[offset + 2] + threshold, 0, 255);
                
                int nearestColor = FindNearestColor(r, g, b);
                
                frame[offset] = (byte)(nearestColor & 0xFF);
                frame[offset + 1] = (byte)((nearestColor >> 8) & 0xFF);
                frame[offset + 2] = (byte)((nearestColor >> 16) & 0xFF);
            }
        }
    }
    
    private int FindNearestColor(int r, int g, int b)
    {
        int minDistance = int.MaxValue;
        int nearestColor = palette[0];
        
        foreach (int color in palette)
        {
            int pr = (color >> 16) & 0xFF;
            int pg = (color >> 8) & 0xFF;
            int pb = color & 0xFF;
            
            int dr = r - pr;
            int dg = g - pg;
            int db = b - pb;
            
            // Weighted Euclidean distance (human perception weights)
            int distance = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);
            
            if (distance < minDistance)
            {
                minDistance = distance;
                return nearestColor;
            }
        }
        
        return nearestColor;
    }
}

/// <summary>
/// Dithering algorithms available.
/// </summary>
public enum DitherAlgorithm
{
    /// <summary>Floyd-Steinberg error diffusion (most common).</summary>
    FloydSteinberg,
    
    /// <summary>Sierra Filter-Lite (faster, less quality).</summary>
    SierraLite,
    
    /// <summary>Jarvis, Judice & Ninke (higher quality, slower).</summary>
    JarvisJudiceNinke,
    
    /// <summary>Random noise dithering.</summary>
    Random,
    
    /// <summary>Ordered (Bayer) dithering.</summary>
    Ordered
}

/// <summary>
/// Logo/watermark eraser using inpainting interpolation.
/// Replaces marked regions with interpolated content from surrounding pixels.
/// Based on VLC's erase.c filter.
/// </summary>
public sealed class LogoEraserFilter
{
    private int _x;
    private int _y;
    private int _maskWidth;
    private int _maskHeight;
    private byte[]? _mask;
    
    /// <summary>X position of mask region.</summary>
    public int X
    {
        get => _x;
        set => _x = Math.Max(0, value);
    }
    
    /// <summary>Y position of mask region.</summary>
    public int Y
    {
        get => _y;
        set => _y = Math.Max(0, value);
    }
    
    /// <summary>
    /// Sets a rectangular mask region to erase.
    /// </summary>
    public void SetRectangularMask(int x, int y, int width, int height)
    {
        _x = Math.Max(0, x);
        _y = Math.Max(0, y);
        _maskWidth = width;
        _maskHeight = height;
        _mask = new byte[width * height];
        Array.Fill(_mask, (byte)255);
    }
    
    /// <summary>
    /// Sets a custom mask where values > 127 indicate pixels to erase.
    /// </summary>
    public void SetCustomMask(int x, int y, int width, int height, ReadOnlySpan<byte> maskData)
    {
        _x = Math.Max(0, x);
        _y = Math.Max(0, y);
        _maskWidth = width;
        _maskHeight = height;
        _mask = maskData.ToArray();
    }
    
    /// <summary>
    /// Erases the masked region using horizontal linear interpolation.
    /// </summary>
    /// <param name="pixels">BGRA pixel data.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="stride">Row stride in bytes.</param>
    public void Process(Span<byte> pixels, int width, int height, int stride)
    {
        if (_mask == null || _maskWidth == 0 || _maskHeight == 0)
            return;
        
        // Clamp mask region to image bounds
        int maskX = Math.Min(_x, width - 1);
        int maskY = Math.Min(_y, height - 1);
        int effectiveWidth = Math.Min(_maskWidth, width - maskX);
        int effectiveHeight = Math.Min(_maskHeight, height - maskY);
        
        if (effectiveWidth <= 0 || effectiveHeight <= 0)
            return;
        
        // Process each row with horizontal interpolation
        for (int row = 0; row < effectiveHeight; row++)
        {
            int imageY = maskY + row;
            int rowOffset = imageY * stride;
            int maskRowOffset = row * _maskWidth;
            
            int col = 0;
            while (col < effectiveWidth)
            {
                int maskIdx = maskRowOffset + col;
                
                if (_mask[maskIdx] > 127)
                {
                    // Found start of masked region - find end
                    int startCol = col;
                    while (col < effectiveWidth && _mask[maskRowOffset + col] > 127)
                        col++;
                    int endCol = col;
                    
                    // Get boundary colors for interpolation
                    int imageStartX = maskX + startCol;
                    int imageEndX = maskX + endCol;
                    
                    // Left boundary color
                    int leftB, leftG, leftR;
                    if (imageStartX > 0)
                    {
                        int leftIdx = rowOffset + (imageStartX - 1) * 4;
                        leftB = pixels[leftIdx];
                        leftG = pixels[leftIdx + 1];
                        leftR = pixels[leftIdx + 2];
                    }
                    else if (imageY > 0)
                    {
                        int upIdx = rowOffset - stride + imageStartX * 4;
                        leftB = pixels[upIdx];
                        leftG = pixels[upIdx + 1];
                        leftR = pixels[upIdx + 2];
                    }
                    else
                    {
                        leftB = leftG = leftR = 128;
                    }
                    
                    // Right boundary color
                    int rightB, rightG, rightR;
                    if (imageEndX < width)
                    {
                        int rightIdx = rowOffset + imageEndX * 4;
                        rightB = pixels[rightIdx];
                        rightG = pixels[rightIdx + 1];
                        rightR = pixels[rightIdx + 2];
                    }
                    else if (imageY < height - 1)
                    {
                        int downIdx = rowOffset + stride + (imageEndX - 1) * 4;
                        rightB = pixels[downIdx];
                        rightG = pixels[downIdx + 1];
                        rightR = pixels[downIdx + 2];
                    }
                    else
                    {
                        rightB = rightG = rightR = 128;
                    }
                    
                    // Interpolate across masked region
                    int span = endCol - startCol;
                    for (int x = startCol; x < endCol; x++)
                    {
                        float t = (float)(x - startCol + 1) / (span + 1);
                        int pixelIdx = rowOffset + (maskX + x) * 4;
                        
                        pixels[pixelIdx] = (byte)(leftB + t * (rightB - leftB));
                        pixels[pixelIdx + 1] = (byte)(leftG + t * (rightG - leftG));
                        pixels[pixelIdx + 2] = (byte)(leftR + t * (rightR - leftR));
                    }
                }
                else
                {
                    col++;
                }
            }
        }
    }
}

/// <summary>
/// Fast bilateral denoise filter for noise reduction while preserving edges.
/// Uses a simplified bilateral filter approach.
/// </summary>
public sealed class BilateralDenoiseFilter
{
    private float _spatialSigma = 3f;
    private float _colorSigma = 25f;
    private int _radius = 3;
    
    /// <summary>Spatial sigma (controls blur spread). Default 3.0.</summary>
    public float SpatialSigma
    {
        get => _spatialSigma;
        set => _spatialSigma = Math.Clamp(value, 0.5f, 10f);
    }
    
    /// <summary>Color sigma (controls edge preservation). Default 25.0.</summary>
    public float ColorSigma
    {
        get => _colorSigma;
        set => _colorSigma = Math.Clamp(value, 1f, 100f);
    }
    
    /// <summary>Filter radius in pixels. Default 3.</summary>
    public int Radius
    {
        get => _radius;
        set => _radius = Math.Clamp(value, 1, 7);
    }
    
    /// <summary>
    /// Applies bilateral denoise filter to BGRA image.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride)
    {
        // Precompute Gaussian lookup tables
        float spatialFactor = -0.5f / (_spatialSigma * _spatialSigma);
        float colorFactor = -0.5f / (_colorSigma * _colorSigma);
        
        int r = _radius;
        
        // Process each pixel
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 0; x < width; x++)
            {
                int centerIdx = rowOffset + x * 4;
                int centerB = input[centerIdx];
                int centerG = input[centerIdx + 1];
                int centerR = input[centerIdx + 2];
                
                float sumB = 0, sumG = 0, sumR = 0;
                float sumWeight = 0;
                
                // Sample neighborhood
                int yMin = Math.Max(0, y - r);
                int yMax = Math.Min(height - 1, y + r);
                int xMin = Math.Max(0, x - r);
                int xMax = Math.Min(width - 1, x + r);
                
                for (int ny = yMin; ny <= yMax; ny++)
                {
                    int neighborRow = ny * stride;
                    int dy = ny - y;
                    
                    for (int nx = xMin; nx <= xMax; nx++)
                    {
                        int neighborIdx = neighborRow + nx * 4;
                        int dx = nx - x;
                        
                        int neighborB = input[neighborIdx];
                        int neighborG = input[neighborIdx + 1];
                        int neighborR = input[neighborIdx + 2];
                        
                        // Spatial weight
                        float spatialDist = dx * dx + dy * dy;
                        float spatialWeight = MathF.Exp(spatialDist * spatialFactor);
                        
                        // Color weight
                        int db = neighborB - centerB;
                        int dg = neighborG - centerG;
                        int dr = neighborR - centerR;
                        float colorDist = db * db + dg * dg + dr * dr;
                        float colorWeight = MathF.Exp(colorDist * colorFactor);
                        
                        float weight = spatialWeight * colorWeight;
                        
                        sumB += neighborB * weight;
                        sumG += neighborG * weight;
                        sumR += neighborR * weight;
                        sumWeight += weight;
                    }
                }
                
                // Normalize and write output
                if (sumWeight > 0)
                {
                    output[centerIdx] = (byte)(sumB / sumWeight);
                    output[centerIdx + 1] = (byte)(sumG / sumWeight);
                    output[centerIdx + 2] = (byte)(sumR / sumWeight);
                    output[centerIdx + 3] = input[centerIdx + 3];
                }
                else
                {
                    output[centerIdx] = input[centerIdx];
                    output[centerIdx + 1] = input[centerIdx + 1];
                    output[centerIdx + 2] = input[centerIdx + 2];
                    output[centerIdx + 3] = input[centerIdx + 3];
                }
            }
        }
    }
}

/// <summary>
/// Color quantization filter - reduces image to N colors.
/// </summary>
public sealed class ColorQuantizeFilter
{
    private int _numColors = 16;
    private byte[][]? _palette;
    
    /// <summary>Number of colors to reduce to (2-256). Default 16.</summary>
    public int NumberOfColors
    {
        get => _numColors;
        set => _numColors = Math.Clamp(value, 2, 256);
    }
    
    /// <summary>
    /// Sets a fixed palette. If null, palette is auto-generated.
    /// </summary>
    public void SetPalette(byte[][] palette)
    {
        _palette = palette;
    }
    
    /// <summary>
    /// Quantizes image colors using median-cut or fixed palette.
    /// </summary>
    public void Process(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride)
    {
        if (_palette != null && _palette.Length >= _numColors)
        {
            // Use fixed palette
            QuantizeWithPalette(input, output, width, height, stride, _palette);
        }
        else
        {
            // Generate palette via simple uniform quantization
            var uniformPalette = GenerateUniformPalette(_numColors);
            QuantizeWithPalette(input, output, width, height, stride, uniformPalette);
        }
    }
    
    private static byte[][] GenerateUniformPalette(int numColors)
    {
        // Determine cube subdivisions per axis
        int perAxis = (int)Math.Ceiling(Math.Pow(numColors, 1.0 / 3.0));
        var palette = new List<byte[]>();
        
        int step = 255 / Math.Max(1, perAxis - 1);
        
        for (int r = 0; r < perAxis && palette.Count < numColors; r++)
        {
            for (int g = 0; g < perAxis && palette.Count < numColors; g++)
            {
                for (int b = 0; b < perAxis && palette.Count < numColors; b++)
                {
                    palette.Add(new byte[] { (byte)(b * step), (byte)(g * step), (byte)(r * step) });
                }
            }
        }
        
        return palette.ToArray();
    }
    
    private static void QuantizeWithPalette(ReadOnlySpan<byte> input, Span<byte> output, 
        int width, int height, int stride, byte[][] palette)
    {
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x * 4;
                int b = input[idx];
                int g = input[idx + 1];
                int r = input[idx + 2];
                
                // Find nearest palette color
                int minDist = int.MaxValue;
                byte[] nearestColor = palette[0];
                
                foreach (var color in palette)
                {
                    int db = b - color[0];
                    int dg = g - color[1];
                    int dr = r - color[2];
                    int dist = db * db + dg * dg + dr * dr;
                    
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestColor = color;
                    }
                }
                
                output[idx] = nearestColor[0];
                output[idx + 1] = nearestColor[1];
                output[idx + 2] = nearestColor[2];
                output[idx + 3] = input[idx + 3];
            }
        }
    }
}

/// <summary>
/// Lens distortion correction filter for fisheye/barrel/pincushion effects.
/// </summary>
public sealed class LensDistortionFilter
{
    private float _k1 = 0f;   // Radial distortion coefficient
    private float _k2 = 0f;   // Secondary radial coefficient
    private float _centerX = 0.5f;
    private float _centerY = 0.5f;
    
    /// <summary>Primary radial distortion. Positive = barrel, Negative = pincushion. Default 0.</summary>
    public float K1
    {
        get => _k1;
        set => _k1 = Math.Clamp(value, -1f, 1f);
    }
    
    /// <summary>Secondary radial distortion coefficient. Default 0.</summary>
    public float K2
    {
        get => _k2;
        set => _k2 = Math.Clamp(value, -1f, 1f);
    }
    
    /// <summary>Distortion center X (0-1). Default 0.5.</summary>
    public float CenterX
    {
        get => _centerX;
        set => _centerX = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>Distortion center Y (0-1). Default 0.5.</summary>
    public float CenterY
    {
        get => _centerY;
        set => _centerY = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Applies lens distortion correction to BGRA image.
    /// </summary>
    public void Process(ReadOnlySpan<byte> input, Span<byte> output, int width, int height, int stride)
    {
        float cx = width * _centerX;
        float cy = height * _centerY;
        float maxRadius = MathF.Sqrt(width * width + height * height) * 0.5f;
        
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 0; x < width; x++)
            {
                // Normalize coordinates
                float dx = (x - cx) / maxRadius;
                float dy = (y - cy) / maxRadius;
                float r2 = dx * dx + dy * dy;
                float r4 = r2 * r2;
                
                // Apply distortion model
                float distortion = 1f + _k1 * r2 + _k2 * r4;
                
                // Map to source coordinates
                float srcX = cx + dx * maxRadius * distortion;
                float srcY = cy + dy * maxRadius * distortion;
                
                int destIdx = rowOffset + x * 4;
                
                // Bilinear interpolation
                if (srcX >= 0 && srcX < width - 1 && srcY >= 0 && srcY < height - 1)
                {
                    int x0 = (int)srcX;
                    int y0 = (int)srcY;
                    float fx = srcX - x0;
                    float fy = srcY - y0;
                    
                    int idx00 = y0 * stride + x0 * 4;
                    int idx01 = idx00 + 4;
                    int idx10 = idx00 + stride;
                    int idx11 = idx10 + 4;
                    
                    float w00 = (1 - fx) * (1 - fy);
                    float w01 = fx * (1 - fy);
                    float w10 = (1 - fx) * fy;
                    float w11 = fx * fy;
                    
                    output[destIdx] = (byte)(input[idx00] * w00 + input[idx01] * w01 + 
                                             input[idx10] * w10 + input[idx11] * w11);
                    output[destIdx + 1] = (byte)(input[idx00 + 1] * w00 + input[idx01 + 1] * w01 + 
                                                  input[idx10 + 1] * w10 + input[idx11 + 1] * w11);
                    output[destIdx + 2] = (byte)(input[idx00 + 2] * w00 + input[idx01 + 2] * w01 + 
                                                  input[idx10 + 2] * w10 + input[idx11 + 2] * w11);
                    output[destIdx + 3] = 255;
                }
                else
                {
                    // Out of bounds - black
                    output[destIdx] = 0;
                    output[destIdx + 1] = 0;
                    output[destIdx + 2] = 0;
                    output[destIdx + 3] = 255;
                }
            }
        }
    }
}

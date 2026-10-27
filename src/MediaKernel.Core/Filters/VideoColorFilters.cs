// MediaKernel - Video Color Adjustment Filters
// Brightness, contrast, saturation, hue, gamma, and color effects

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using MediaKernel.Core.Video;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Filter for adjusting brightness, contrast, and gamma of video frames.
/// </summary>
public sealed class BrightnessContrastFilter
{
    private float brightness = 0f;
    private float contrast = 1f;
    private float gamma = 1f;
    private byte[] lookupTable;
    private bool lookupTableDirty = true;
    
    /// <summary>
    /// Brightness adjustment from -1.0 (black) to 1.0 (white). Default is 0.
    /// </summary>
    public float Brightness
    {
        get => brightness;
        set
        {
            brightness = Math.Clamp(value, -1f, 1f);
            lookupTableDirty = true;
        }
    }
    
    /// <summary>
    /// Contrast multiplier from 0.0 (gray) to 3.0 (high contrast). Default is 1.0.
    /// </summary>
    public float Contrast
    {
        get => contrast;
        set
        {
            contrast = Math.Clamp(value, 0f, 3f);
            lookupTableDirty = true;
        }
    }
    
    /// <summary>
    /// Gamma correction from 0.1 to 10.0. Default is 1.0.
    /// Values less than 1.0 brighten midtones, greater than 1.0 darken midtones.
    /// </summary>
    public float Gamma
    {
        get => gamma;
        set
        {
            gamma = Math.Clamp(value, 0.1f, 10f);
            lookupTableDirty = true;
        }
    }
    
    public BrightnessContrastFilter()
    {
        lookupTable = new byte[256];
        RebuildLookupTable();
    }
    
    private void RebuildLookupTable()
    {
        if (!lookupTableDirty) return;
        
        float invGamma = 1f / gamma;
        
        for (int i = 0; i < 256; i++)
        {
            // Normalize to 0-1
            float value = i / 255f;
            
            // Apply gamma
            if (gamma != 1f)
            {
                value = MathF.Pow(value, invGamma);
            }
            
            // Apply contrast (centered at 0.5)
            value = (value - 0.5f) * contrast + 0.5f;
            
            // Apply brightness
            value += brightness;
            
            // Clamp and convert back to byte
            lookupTable[i] = (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
        }
        
        lookupTableDirty = false;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    /// <param name="frameData">RGBA pixel data.</param>
    public void ProcessRgba(Span<byte> frameData)
    {
        RebuildLookupTable();
        
        // Skip if no adjustment needed
        if (brightness == 0f && contrast == 1f && gamma == 1f)
            return;
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            frameData[i] = lookupTable[frameData[i]];         // R
            frameData[i + 1] = lookupTable[frameData[i + 1]]; // G
            frameData[i + 2] = lookupTable[frameData[i + 2]]; // B
            // Alpha unchanged
        }
    }
    
    /// <summary>
    /// Process planar Y component (for YUV formats).
    /// </summary>
    public void ProcessYPlane(Span<byte> yData)
    {
        RebuildLookupTable();
        
        if (brightness == 0f && contrast == 1f && gamma == 1f)
            return;
        
        for (int i = 0; i < yData.Length; i++)
        {
            yData[i] = lookupTable[yData[i]];
        }
    }
}

/// <summary>
/// Filter for adjusting saturation and hue of video frames.
/// </summary>
public sealed class SaturationHueFilter
{
    private float saturation = 1f;
    private float hue = 0f;
    private float hueCos;
    private float hueSin;
    private bool matrixDirty = true;
    
    /// <summary>
    /// Saturation level from 0.0 (grayscale) to 3.0 (oversaturated). Default is 1.0.
    /// </summary>
    public float Saturation
    {
        get => saturation;
        set
        {
            saturation = Math.Clamp(value, 0f, 3f);
            matrixDirty = true;
        }
    }
    
    /// <summary>
    /// Hue rotation in degrees from -180 to 180. Default is 0.
    /// </summary>
    public float HueDegrees
    {
        get => hue;
        set
        {
            hue = value;
            while (hue > 180) hue -= 360;
            while (hue < -180) hue += 360;
            matrixDirty = true;
        }
    }
    
    public SaturationHueFilter()
    {
        UpdateMatrix();
    }
    
    private void UpdateMatrix()
    {
        if (!matrixDirty) return;
        
        float hueRad = hue * MathF.PI / 180f;
        hueCos = MathF.Cos(hueRad);
        hueSin = MathF.Sin(hueRad);
        
        matrixDirty = false;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        UpdateMatrix();
        
        if (saturation == 1f && hue == 0f)
            return;
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            float r = frameData[i] / 255f;
            float g = frameData[i + 1] / 255f;
            float b = frameData[i + 2] / 255f;
            
            // Convert RGB to YUV
            float y = 0.299f * r + 0.587f * g + 0.114f * b;
            float u = -0.14713f * r - 0.28886f * g + 0.436f * b;
            float v = 0.615f * r - 0.51499f * g - 0.10001f * b;
            
            // Apply saturation
            u *= saturation;
            v *= saturation;
            
            // Apply hue rotation
            if (hue != 0f)
            {
                float uNew = u * hueCos - v * hueSin;
                float vNew = u * hueSin + v * hueCos;
                u = uNew;
                v = vNew;
            }
            
            // Convert back to RGB
            r = y + 1.13983f * v;
            g = y - 0.39465f * u - 0.58060f * v;
            b = y + 2.03211f * u;
            
            frameData[i] = (byte)Math.Clamp((int)(r * 255f + 0.5f), 0, 255);
            frameData[i + 1] = (byte)Math.Clamp((int)(g * 255f + 0.5f), 0, 255);
            frameData[i + 2] = (byte)Math.Clamp((int)(b * 255f + 0.5f), 0, 255);
        }
    }
    
    /// <summary>
    /// Process YUV planar data (faster than RGBA).
    /// </summary>
    public void ProcessUVPlanes(Span<byte> uData, Span<byte> vData)
    {
        UpdateMatrix();
        
        if (saturation == 1f && hue == 0f)
            return;
        
        int count = Math.Min(uData.Length, vData.Length);
        
        for (int i = 0; i < count; i++)
        {
            // UV are stored as unsigned bytes centered at 128
            float u = (uData[i] - 128) / 128f;
            float v = (vData[i] - 128) / 128f;
            
            // Apply saturation
            u *= saturation;
            v *= saturation;
            
            // Apply hue rotation
            if (hue != 0f)
            {
                float uNew = u * hueCos - v * hueSin;
                float vNew = u * hueSin + v * hueCos;
                u = uNew;
                v = vNew;
            }
            
            uData[i] = (byte)Math.Clamp((int)(u * 128f + 128.5f), 0, 255);
            vData[i] = (byte)Math.Clamp((int)(v * 128f + 128.5f), 0, 255);
        }
    }
}

/// <summary>
/// Sepia tone filter for vintage film effect.
/// </summary>
public sealed class SepiaFilter
{
    private float intensity = 1f;
    
    /// <summary>
    /// Sepia intensity from 0 (no effect) to 1 (full sepia). Default is 1.0.
    /// </summary>
    public float Intensity
    {
        get => intensity;
        set => intensity = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        if (intensity == 0f)
            return;
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            int r = frameData[i];
            int g = frameData[i + 1];
            int b = frameData[i + 2];
            
            // Standard sepia transformation matrix
            int sepiaR = (int)(r * 0.393f + g * 0.769f + b * 0.189f);
            int sepiaG = (int)(r * 0.349f + g * 0.686f + b * 0.168f);
            int sepiaB = (int)(r * 0.272f + g * 0.534f + b * 0.131f);
            
            // Blend based on intensity
            frameData[i] = (byte)Math.Clamp((int)(r * (1 - intensity) + sepiaR * intensity), 0, 255);
            frameData[i + 1] = (byte)Math.Clamp((int)(g * (1 - intensity) + sepiaG * intensity), 0, 255);
            frameData[i + 2] = (byte)Math.Clamp((int)(b * (1 - intensity) + sepiaB * intensity), 0, 255);
        }
    }
}

/// <summary>
/// Grayscale conversion filter.
/// </summary>
public sealed class GrayscaleFilter
{
    /// <summary>
    /// Method for calculating luminance.
    /// </summary>
    public enum LuminanceMethod
    {
        /// <summary>Standard ITU-R BT.601 weights.</summary>
        Bt601,
        /// <summary>ITU-R BT.709 (HDTV) weights.</summary>
        Bt709,
        /// <summary>Simple average of RGB.</summary>
        Average,
        /// <summary>Lightness (max + min) / 2.</summary>
        Lightness
    }
    
    private LuminanceMethod method = LuminanceMethod.Bt709;
    
    /// <summary>
    /// Luminance calculation method.
    /// </summary>
    public LuminanceMethod Method
    {
        get => method;
        set => method = value;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        for (int i = 0; i < frameData.Length; i += 4)
        {
            byte r = frameData[i];
            byte g = frameData[i + 1];
            byte b = frameData[i + 2];
            
            byte gray = method switch
            {
                LuminanceMethod.Bt601 => (byte)(r * 0.299f + g * 0.587f + b * 0.114f),
                LuminanceMethod.Bt709 => (byte)(r * 0.2126f + g * 0.7152f + b * 0.0722f),
                LuminanceMethod.Average => (byte)((r + g + b) / 3),
                LuminanceMethod.Lightness => (byte)((Math.Max(Math.Max(r, g), b) + Math.Min(Math.Min(r, g), b)) / 2),
                _ => (byte)(r * 0.2126f + g * 0.7152f + b * 0.0722f)
            };
            
            frameData[i] = gray;
            frameData[i + 1] = gray;
            frameData[i + 2] = gray;
        }
    }
}

/// <summary>
/// Color inversion (negative) filter.
/// </summary>
public sealed class InvertFilter
{
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        // SIMD path for better performance
        if (Avx2.IsSupported && frameData.Length >= 32)
        {
            ProcessAvx2(frameData);
            return;
        }
        
        // Scalar fallback
        for (int i = 0; i < frameData.Length; i += 4)
        {
            frameData[i] = (byte)(255 - frameData[i]);
            frameData[i + 1] = (byte)(255 - frameData[i + 1]);
            frameData[i + 2] = (byte)(255 - frameData[i + 2]);
            // Alpha unchanged
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessAvx2(Span<byte> frameData)
    {
        Vector256<byte> onesVec = Vector256.Create((byte)255);
        Vector256<byte> alphaMask = Vector256.Create(
            (byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255,
            0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
        
        int i = 0;
        int vectorEnd = frameData.Length - 31;
        
        for (; i < vectorEnd; i += 32)
        {
            var data = Vector256.Create(frameData.Slice(i, 32));
            var alpha = Avx2.And(data, alphaMask);
            var inverted = Avx2.Xor(data, onesVec);
            var result = Avx2.Or(Avx2.AndNot(alphaMask, inverted), alpha);
            result.CopyTo(frameData.Slice(i, 32));
        }
        
        // Handle remaining pixels
        for (; i < frameData.Length; i += 4)
        {
            frameData[i] = (byte)(255 - frameData[i]);
            frameData[i + 1] = (byte)(255 - frameData[i + 1]);
            frameData[i + 2] = (byte)(255 - frameData[i + 2]);
        }
    }
}

/// <summary>
/// Posterize filter to reduce color depth.
/// </summary>
public sealed class PosterizeFilter
{
    private int levels = 4;
    private byte[] lookupTable;
    private bool lookupTableDirty = true;
    
    /// <summary>
    /// Number of color levels per channel (2-256). Default is 4.
    /// </summary>
    public int Levels
    {
        get => levels;
        set
        {
            levels = Math.Clamp(value, 2, 256);
            lookupTableDirty = true;
        }
    }
    
    public PosterizeFilter()
    {
        lookupTable = new byte[256];
        RebuildLookupTable();
    }
    
    private void RebuildLookupTable()
    {
        if (!lookupTableDirty) return;
        
        float step = 255f / (levels - 1);
        
        for (int i = 0; i < 256; i++)
        {
            int level = (int)((i / 255f) * (levels - 1) + 0.5f);
            lookupTable[i] = (byte)(level * step);
        }
        
        lookupTableDirty = false;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        RebuildLookupTable();
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            frameData[i] = lookupTable[frameData[i]];
            frameData[i + 1] = lookupTable[frameData[i + 1]];
            frameData[i + 2] = lookupTable[frameData[i + 2]];
        }
    }
}

/// <summary>
/// Color threshold filter for creating high-contrast black and white images.
/// </summary>
public sealed class ThresholdFilter
{
    private byte threshold = 128;
    
    /// <summary>
    /// Threshold value (0-255). Pixels below become black, above become white.
    /// </summary>
    public byte Threshold
    {
        get => threshold;
        set => threshold = value;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        for (int i = 0; i < frameData.Length; i += 4)
        {
            // Calculate luminance
            int luma = (int)(frameData[i] * 0.299f + frameData[i + 1] * 0.587f + frameData[i + 2] * 0.114f);
            
            byte value = luma >= threshold ? (byte)255 : (byte)0;
            
            frameData[i] = value;
            frameData[i + 1] = value;
            frameData[i + 2] = value;
        }
    }
}

/// <summary>
/// Color channel extraction filter.
/// </summary>
public sealed class ExtractChannelFilter
{
    /// <summary>
    /// Color channel to extract.
    /// </summary>
    public enum Channel
    {
        Red,
        Green,
        Blue,
        Alpha
    }
    
    private Channel channel = Channel.Red;
    
    /// <summary>
    /// Channel to extract.
    /// </summary>
    public Channel ExtractChannel
    {
        get => channel;
        set => channel = value;
    }
    
    /// <summary>
    /// Process RGBA frame data - extracts specified channel as grayscale.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        int channelOffset = channel switch
        {
            Channel.Red => 0,
            Channel.Green => 1,
            Channel.Blue => 2,
            Channel.Alpha => 3,
            _ => 0
        };
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            byte value = frameData[i + channelOffset];
            frameData[i] = value;
            frameData[i + 1] = value;
            frameData[i + 2] = value;
            // Keep alpha as is unless extracting alpha
            if (channel != Channel.Alpha)
                frameData[i + 3] = 255;
        }
    }
}

/// <summary>
/// Color balance filter for adjusting RGB levels independently.
/// </summary>
public sealed class ColorBalanceFilter
{
    private float redBalance = 0f;
    private float greenBalance = 0f;
    private float blueBalance = 0f;
    
    /// <summary>
    /// Red channel adjustment from -1.0 to 1.0. Default is 0.
    /// </summary>
    public float RedBalance
    {
        get => redBalance;
        set => redBalance = Math.Clamp(value, -1f, 1f);
    }
    
    /// <summary>
    /// Green channel adjustment from -1.0 to 1.0. Default is 0.
    /// </summary>
    public float GreenBalance
    {
        get => greenBalance;
        set => greenBalance = Math.Clamp(value, -1f, 1f);
    }
    
    /// <summary>
    /// Blue channel adjustment from -1.0 to 1.0. Default is 0.
    /// </summary>
    public float BlueBalance
    {
        get => blueBalance;
        set => blueBalance = Math.Clamp(value, -1f, 1f);
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData)
    {
        if (redBalance == 0f && greenBalance == 0f && blueBalance == 0f)
            return;
        
        int rAdjust = (int)(redBalance * 255);
        int gAdjust = (int)(greenBalance * 255);
        int bAdjust = (int)(blueBalance * 255);
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            frameData[i] = (byte)Math.Clamp(frameData[i] + rAdjust, 0, 255);
            frameData[i + 1] = (byte)Math.Clamp(frameData[i + 1] + gAdjust, 0, 255);
            frameData[i + 2] = (byte)Math.Clamp(frameData[i + 2] + bAdjust, 0, 255);
        }
    }
}

/// <summary>
/// Vignette effect filter that darkens frame edges.
/// </summary>
public sealed class VignetteFilter
{
    private float strength = 0.5f;
    private float radius = 0.8f;
    
    /// <summary>
    /// Vignette strength from 0 (none) to 1 (full black corners). Default is 0.5.
    /// </summary>
    public float Strength
    {
        get => strength;
        set => strength = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>
    /// Vignette radius from 0 (center) to 2 (beyond edges). Default is 0.8.
    /// </summary>
    public float Radius
    {
        get => radius;
        set => radius = Math.Clamp(value, 0f, 2f);
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    /// <param name="frameData">RGBA pixel data.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    public void ProcessRgba(Span<byte> frameData, int width, int height)
    {
        if (strength == 0f)
            return;
        
        float centerX = width * 0.5f;
        float centerY = height * 0.5f;
        float maxDistance = MathF.Sqrt(centerX * centerX + centerY * centerY);
        
        for (int y = 0; y < height; y++)
        {
            float dy = y - centerY;
            
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerX;
                float distance = MathF.Sqrt(dx * dx + dy * dy) / maxDistance;
                
                // Calculate vignette factor
                float factor = 1f;
                if (distance > radius)
                {
                    float falloff = (distance - radius) / (1f - radius + 0.001f);
                    factor = 1f - strength * falloff * falloff;
                    factor = Math.Max(0f, factor);
                }
                
                int i = (y * width + x) * 4;
                if (i + 2 < frameData.Length)
                {
                    frameData[i] = (byte)(frameData[i] * factor);
                    frameData[i + 1] = (byte)(frameData[i + 1] * factor);
                    frameData[i + 2] = (byte)(frameData[i + 2] * factor);
                }
            }
        }
    }
}

/// <summary>
/// Histogram equalization filter for improving contrast.
/// Redistributes pixel intensity values to use the full range.
/// </summary>
public sealed class HistogramEqualizationFilter
{
    private bool processLuminanceOnly = true;
    
    /// <summary>
    /// If true, only equalize luminance (preserves color). If false, equalize each channel independently.
    /// </summary>
    public bool ProcessLuminanceOnly
    {
        get => processLuminanceOnly;
        set => processLuminanceOnly = value;
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData, int width, int height)
    {
        if (processLuminanceOnly)
        {
            ProcessLuminance(frameData);
        }
        else
        {
            ProcessChannels(frameData);
        }
    }
    
    private void ProcessLuminance(Span<byte> frameData)
    {
        // Build luminance histogram
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        
        int pixelCount = frameData.Length / 4;
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            // Calculate luminance: Y = 0.299*R + 0.587*G + 0.114*B
            int luminance = (299 * frameData[i] + 587 * frameData[i + 1] + 114 * frameData[i + 2]) / 1000;
            histogram[Math.Clamp(luminance, 0, 255)]++;
        }
        
        // Build CDF (cumulative distribution function)
        Span<int> cdf = stackalloc int[256];
        cdf[0] = histogram[0];
        for (int i = 1; i < 256; i++)
        {
            cdf[i] = cdf[i - 1] + histogram[i];
        }
        
        // Find CDF min (first non-zero value)
        int cdfMin = 0;
        for (int i = 0; i < 256; i++)
        {
            if (cdf[i] > 0)
            {
                cdfMin = cdf[i];
                break;
            }
        }
        
        // Build equalization lookup table
        Span<byte> lookupTable = stackalloc byte[256];
        int divisor = pixelCount - cdfMin;
        if (divisor <= 0) return;
        
        for (int i = 0; i < 256; i++)
        {
            lookupTable[i] = (byte)Math.Clamp((cdf[i] - cdfMin) * 255 / divisor, 0, 255);
        }
        
        // Apply equalization
        for (int i = 0; i < frameData.Length; i += 4)
        {
            int oldLum = (299 * frameData[i] + 587 * frameData[i + 1] + 114 * frameData[i + 2]) / 1000;
            int newLum = lookupTable[Math.Clamp(oldLum, 0, 255)];
            
            if (oldLum > 0)
            {
                float factor = (float)newLum / oldLum;
                frameData[i] = (byte)Math.Clamp(frameData[i] * factor, 0, 255);
                frameData[i + 1] = (byte)Math.Clamp(frameData[i + 1] * factor, 0, 255);
                frameData[i + 2] = (byte)Math.Clamp(frameData[i + 2] * factor, 0, 255);
            }
            else if (newLum > 0)
            {
                frameData[i] = (byte)newLum;
                frameData[i + 1] = (byte)newLum;
                frameData[i + 2] = (byte)newLum;
            }
        }
    }
    
    private void ProcessChannels(Span<byte> frameData)
    {
        // Build histogram for each channel
        Span<int> histR = stackalloc int[256];
        Span<int> histG = stackalloc int[256];
        Span<int> histB = stackalloc int[256];
        histR.Clear();
        histG.Clear();
        histB.Clear();
        
        int pixelCount = frameData.Length / 4;
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            histR[frameData[i]]++;
            histG[frameData[i + 1]]++;
            histB[frameData[i + 2]]++;
        }
        
        // Build CDF for each channel
        Span<int> cdfR = stackalloc int[256];
        Span<int> cdfG = stackalloc int[256];
        Span<int> cdfB = stackalloc int[256];
        
        cdfR[0] = histR[0];
        cdfG[0] = histG[0];
        cdfB[0] = histB[0];
        
        for (int i = 1; i < 256; i++)
        {
            cdfR[i] = cdfR[i - 1] + histR[i];
            cdfG[i] = cdfG[i - 1] + histG[i];
            cdfB[i] = cdfB[i - 1] + histB[i];
        }
        
        // Find CDF min for each channel
        int cdfMinR = FindCdfMin(cdfR);
        int cdfMinG = FindCdfMin(cdfG);
        int cdfMinB = FindCdfMin(cdfB);
        
        // Build lookup tables
        Span<byte> lutR = stackalloc byte[256];
        Span<byte> lutG = stackalloc byte[256];
        Span<byte> lutB = stackalloc byte[256];
        
        BuildLut(cdfR, cdfMinR, pixelCount, lutR);
        BuildLut(cdfG, cdfMinG, pixelCount, lutG);
        BuildLut(cdfB, cdfMinB, pixelCount, lutB);
        
        // Apply equalization
        for (int i = 0; i < frameData.Length; i += 4)
        {
            frameData[i] = lutR[frameData[i]];
            frameData[i + 1] = lutG[frameData[i + 1]];
            frameData[i + 2] = lutB[frameData[i + 2]];
        }
    }
    
    private static int FindCdfMin(Span<int> cdf)
    {
        for (int i = 0; i < 256; i++)
        {
            if (cdf[i] > 0) return cdf[i];
        }
        return 0;
    }
    
    private static void BuildLut(Span<int> cdf, int cdfMin, int pixelCount, Span<byte> lut)
    {
        int divisor = pixelCount - cdfMin;
        if (divisor <= 0)
        {
            for (int i = 0; i < 256; i++) lut[i] = (byte)i;
            return;
        }
        
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp((cdf[i] - cdfMin) * 255 / divisor, 0, 255);
        }
    }
}

/// <summary>
/// Alpha mask processing filter.
/// Applies an external mask image to control transparency.
/// </summary>
public sealed class AlphaMaskFilter
{
    private byte[]? maskData;
    private int maskWidth;
    private int maskHeight;
    private bool invertMask;
    
    /// <summary>
    /// Invert the mask (black becomes opaque, white becomes transparent).
    /// </summary>
    public bool InvertMask
    {
        get => invertMask;
        set => invertMask = value;
    }
    
    /// <summary>
    /// Set the mask image data (grayscale or RGBA).
    /// </summary>
    public void SetMask(byte[] mask, int width, int height, bool isRgba = false)
    {
        maskWidth = width;
        maskHeight = height;
        
        if (isRgba)
        {
            // Extract luminance channel as mask
            maskData = new byte[width * height];
            for (int i = 0; i < width * height; i++)
            {
                int srcIdx = i * 4;
                // Use alpha channel if available, otherwise luminance
                if (mask[srcIdx + 3] < 255)
                {
                    maskData[i] = mask[srcIdx + 3];
                }
                else
                {
                    maskData[i] = (byte)((299 * mask[srcIdx] + 587 * mask[srcIdx + 1] + 114 * mask[srcIdx + 2]) / 1000);
                }
            }
        }
        else
        {
            maskData = mask;
        }
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData, int width, int height)
    {
        if (maskData == null) return;
        
        for (int y = 0; y < height; y++)
        {
            // Map frame Y to mask Y
            int maskY = y * maskHeight / height;
            
            for (int x = 0; x < width; x++)
            {
                // Map frame X to mask X
                int maskX = x * maskWidth / width;
                
                int maskIdx = maskY * maskWidth + maskX;
                if (maskIdx >= maskData.Length) continue;
                
                byte maskValue = maskData[maskIdx];
                if (invertMask)
                    maskValue = (byte)(255 - maskValue);
                
                int frameIdx = (y * width + x) * 4 + 3; // Alpha channel
                if (frameIdx < frameData.Length)
                {
                    // Multiply existing alpha with mask
                    frameData[frameIdx] = (byte)(frameData[frameIdx] * maskValue / 255);
                }
            }
        }
    }
    
    /// <summary>
    /// Create a gradient mask.
    /// </summary>
    public static byte[] CreateGradientMask(int width, int height, 
        GradientDirection direction = GradientDirection.TopToBottom)
    {
        byte[] mask = new byte[width * height];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float t = direction switch
                {
                    GradientDirection.TopToBottom => (float)y / height,
                    GradientDirection.BottomToTop => 1f - (float)y / height,
                    GradientDirection.LeftToRight => (float)x / width,
                    GradientDirection.RightToLeft => 1f - (float)x / width,
                    GradientDirection.RadialIn => 1f - MathF.Sqrt(
                        MathF.Pow((x - width / 2f) / (width / 2f), 2) +
                        MathF.Pow((y - height / 2f) / (height / 2f), 2)) / 1.414f,
                    GradientDirection.RadialOut => MathF.Sqrt(
                        MathF.Pow((x - width / 2f) / (width / 2f), 2) +
                        MathF.Pow((y - height / 2f) / (height / 2f), 2)) / 1.414f,
                    _ => (float)y / height
                };
                
                mask[y * width + x] = (byte)Math.Clamp(t * 255, 0, 255);
            }
        }
        
        return mask;
    }
}

public enum GradientDirection
{
    TopToBottom,
    BottomToTop,
    LeftToRight,
    RightToLeft,
    RadialIn,
    RadialOut
}

/// <summary>
/// Marquee text overlay filter (scrolling text).
/// </summary>
public sealed class MarqueeFilter
{
    private string text = "";
    private int fontSize = 24;
    private uint textColor = 0xFFFFFFFF; // White
    private uint backgroundColor = 0x80000000; // Semi-transparent black
    private MarqueePosition position = MarqueePosition.Bottom;
    private MarqueeDirection scrollDirection = MarqueeDirection.LeftToRight;
    private float scrollSpeed = 50f; // Pixels per second
    private float currentOffset;
    private DateTime lastUpdate = DateTime.UtcNow;
    
    /// <summary>
    /// Text to display.
    /// </summary>
    public string Text
    {
        get => text;
        set => text = value ?? "";
    }
    
    /// <summary>
    /// Font size in pixels.
    /// </summary>
    public int FontSize
    {
        get => fontSize;
        set => fontSize = Math.Clamp(value, 8, 128);
    }
    
    /// <summary>
    /// Text color in ARGB format.
    /// </summary>
    public uint TextColor
    {
        get => textColor;
        set => textColor = value;
    }
    
    /// <summary>
    /// Background color in ARGB format.
    /// </summary>
    public uint BackgroundColor
    {
        get => backgroundColor;
        set => backgroundColor = value;
    }
    
    /// <summary>
    /// Position of the marquee on screen.
    /// </summary>
    public MarqueePosition Position
    {
        get => position;
        set => position = value;
    }
    
    /// <summary>
    /// Scroll direction.
    /// </summary>
    public MarqueeDirection ScrollDirection
    {
        get => scrollDirection;
        set => scrollDirection = value;
    }
    
    /// <summary>
    /// Scroll speed in pixels per second.
    /// </summary>
    public float ScrollSpeed
    {
        get => scrollSpeed;
        set => scrollSpeed = Math.Max(0, value);
    }
    
    /// <summary>
    /// Process RGBA frame data.
    /// </summary>
    public void ProcessRgba(Span<byte> frameData, int width, int height)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        // Update scroll position
        DateTime now = DateTime.UtcNow;
        float elapsed = (float)(now - lastUpdate).TotalSeconds;
        lastUpdate = now;
        
        float direction = scrollDirection switch
        {
            MarqueeDirection.LeftToRight => 1f,
            MarqueeDirection.RightToLeft => -1f,
            _ => 1f
        };
        
        currentOffset += scrollSpeed * elapsed * direction;
        
        // Estimate text width (simplified)
        int textWidth = text.Length * fontSize * 6 / 10;
        
        // Wrap offset
        if (currentOffset > width + textWidth)
            currentOffset = -textWidth;
        else if (currentOffset < -textWidth)
            currentOffset = width + textWidth;
        
        // Calculate marquee area
        int marqueeHeight = fontSize + 10;
        int startY = position switch
        {
            MarqueePosition.Top => 0,
            MarqueePosition.Bottom => height - marqueeHeight,
            MarqueePosition.Center => (height - marqueeHeight) / 2,
            _ => height - marqueeHeight
        };
        
        // Draw background
        byte bgA = (byte)(backgroundColor >> 24);
        byte bgR = (byte)(backgroundColor >> 16);
        byte bgG = (byte)(backgroundColor >> 8);
        byte bgB = (byte)backgroundColor;
        
        if (bgA > 0)
        {
            for (int y = startY; y < startY + marqueeHeight && y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    if (idx + 3 < frameData.Length)
                    {
                        // Alpha blend background
                        float alpha = bgA / 255f;
                        frameData[idx] = (byte)(frameData[idx] * (1 - alpha) + bgR * alpha);
                        frameData[idx + 1] = (byte)(frameData[idx + 1] * (1 - alpha) + bgG * alpha);
                        frameData[idx + 2] = (byte)(frameData[idx + 2] * (1 - alpha) + bgB * alpha);
                    }
                }
            }
        }
        
        // Draw text (simplified - just draw colored rectangles for each character)
        // Real implementation would use a font rasterizer
        byte textA = (byte)(textColor >> 24);
        byte textR = (byte)(textColor >> 16);
        byte textG = (byte)(textColor >> 8);
        byte textB = (byte)textColor;
        
        int textX = (int)currentOffset;
        int textY = startY + 5;
        int charWidth = fontSize * 6 / 10;
        int charHeight = fontSize;
        
        for (int c = 0; c < text.Length; c++)
        {
            int charX = textX + c * charWidth;
            
            // Skip if off-screen
            if (charX + charWidth < 0 || charX >= width) continue;
            
            // Draw simplified character block (real impl would use glyph data)
            for (int py = 0; py < charHeight; py++)
            {
                int y = textY + py;
                if (y < 0 || y >= height) continue;
                
                for (int px = 1; px < charWidth - 1; px++)
                {
                    int x = charX + px;
                    if (x < 0 || x >= width) continue;
                    
                    int idx = (y * width + x) * 4;
                    if (idx + 3 < frameData.Length)
                    {
                        float alpha = textA / 255f;
                        frameData[idx] = (byte)(frameData[idx] * (1 - alpha) + textR * alpha);
                        frameData[idx + 1] = (byte)(frameData[idx + 1] * (1 - alpha) + textG * alpha);
                        frameData[idx + 2] = (byte)(frameData[idx + 2] * (1 - alpha) + textB * alpha);
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Reset scroll position.
    /// </summary>
    public void Reset()
    {
        currentOffset = 0;
        lastUpdate = DateTime.UtcNow;
    }
}

public enum MarqueePosition
{
    Top,
    Center,
    Bottom
}

public enum MarqueeDirection
{
    LeftToRight,
    RightToLeft
}

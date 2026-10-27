// Video adjustment filter: brightness, contrast, saturation, hue, gamma
// Reference: VLC's modules/video_filter/adjust.c

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Video color adjustment filter supporting brightness, contrast, saturation, hue, and gamma.
/// Operates on YUV data (Y, U, V planes).
/// </summary>
public sealed class VideoAdjust
{
    // Lookup tables for fast processing
    private readonly byte[] yLookupTable = new byte[256];
    private readonly byte[] uLookupTable = new byte[256 * 256];  // Indexed by [U * 256 + V]
    private readonly byte[] vLookupTable = new byte[256 * 256];

    // Parameters
    private float brightness;    // -1.0 to 1.0 (default 0)
    private float contrast;      // 0.0 to 2.0 (default 1)
    private float saturation;    // 0.0 to 3.0 (default 1)
    private float hue;           // -180 to 180 degrees (default 0)
    private float gamma;         // 0.1 to 10.0 (default 1)
    private bool tablesNeedUpdate = true;
    private readonly object syncLock = new();

    /// <summary>
    /// Brightness adjustment (-1.0 to 1.0). 0 = no change.
    /// </summary>
    public float Brightness
    {
        get => brightness;
        set
        {
            lock (syncLock)
            {
                brightness = Math.Clamp(value, -1.0f, 1.0f);
                tablesNeedUpdate = true;
            }
        }
    }

    /// <summary>
    /// Contrast adjustment (0.0 to 2.0). 1 = no change.
    /// </summary>
    public float Contrast
    {
        get => contrast;
        set
        {
            lock (syncLock)
            {
                contrast = Math.Clamp(value, 0.0f, 2.0f);
                tablesNeedUpdate = true;
            }
        }
    }

    /// <summary>
    /// Saturation adjustment (0.0 to 3.0). 1 = no change, 0 = grayscale.
    /// </summary>
    public float Saturation
    {
        get => saturation;
        set
        {
            lock (syncLock)
            {
                saturation = Math.Clamp(value, 0.0f, 3.0f);
                tablesNeedUpdate = true;
            }
        }
    }

    /// <summary>
    /// Hue rotation in degrees (-180 to 180). 0 = no change.
    /// </summary>
    public float Hue
    {
        get => hue;
        set
        {
            lock (syncLock)
            {
                hue = Math.Clamp(value, -180.0f, 180.0f);
                tablesNeedUpdate = true;
            }
        }
    }

    /// <summary>
    /// Gamma correction (0.1 to 10.0). 1 = no change.
    /// </summary>
    public float Gamma
    {
        get => gamma;
        set
        {
            lock (syncLock)
            {
                gamma = Math.Clamp(value, 0.1f, 10.0f);
                tablesNeedUpdate = true;
            }
        }
    }

    /// <summary>
    /// Creates a video adjustment filter with default settings.
    /// </summary>
    public VideoAdjust()
    {
        brightness = 0.0f;
        contrast = 1.0f;
        saturation = 1.0f;
        hue = 0.0f;
        gamma = 1.0f;
        UpdateLookupTables();
    }

    /// <summary>
    /// Applies adjustment to a YUV420 frame in-place.
    /// </summary>
    /// <param name="yPlane">Luma plane (width × height)</param>
    /// <param name="uPlane">Cb plane (width/2 × height/2)</param>
    /// <param name="vPlane">Cr plane (width/2 × height/2)</param>
    public void ProcessYuv420(Span<byte> yPlane, Span<byte> uPlane, Span<byte> vPlane)
    {
        lock (syncLock)
        {
            if (tablesNeedUpdate)
                UpdateLookupTables();
        }

        // Process luma (Y) with brightness, contrast, gamma
        ProcessLuma(yPlane);

        // Process chroma (U, V) with saturation and hue
        ProcessChroma(uPlane, vPlane);
    }

    /// <summary>
    /// Applies adjustment to packed BGRA data in-place.
    /// </summary>
    public void ProcessBgra(Span<byte> bgraData, int width, int height)
    {
        lock (syncLock)
        {
            if (tablesNeedUpdate)
                UpdateLookupTables();
        }

        // Convert to YUV, apply adjustments, convert back
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            byte b = bgraData[i];
            byte g = bgraData[i + 1];
            byte r = bgraData[i + 2];
            // Alpha at [i + 3] unchanged

            // RGB to YUV (BT.601)
            int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

            y = Math.Clamp(y, 0, 255);
            u = Math.Clamp(u, 0, 255);
            v = Math.Clamp(v, 0, 255);

            // Apply Y adjustment
            y = yLookupTable[y];

            // Apply UV adjustment
            int uvIndex = u * 256 + v;
            u = uLookupTable[uvIndex];
            v = vLookupTable[uvIndex];

            // YUV to RGB (BT.601)
            int c = y - 16;
            int d = u - 128;
            int e = v - 128;

            r = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
            g = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
            b = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);

            bgraData[i] = b;
            bgraData[i + 1] = g;
            bgraData[i + 2] = r;
        }
    }

    /// <summary>
    /// Resets all adjustments to default values.
    /// </summary>
    public void Reset()
    {
        lock (syncLock)
        {
            brightness = 0.0f;
            contrast = 1.0f;
            saturation = 1.0f;
            hue = 0.0f;
            gamma = 1.0f;
            tablesNeedUpdate = true;
        }
    }

    private void ProcessLuma(Span<byte> yPlane)
    {
        if (Sse2.IsSupported && yPlane.Length >= 16)
        {
            ProcessLumaSse2(yPlane);
        }
        else
        {
            ProcessLumaScalar(yPlane);
        }
    }

    private void ProcessLumaScalar(Span<byte> yPlane)
    {
        for (int i = 0; i < yPlane.Length; i++)
        {
            yPlane[i] = yLookupTable[yPlane[i]];
        }
    }

    private unsafe void ProcessLumaSse2(Span<byte> yPlane)
    {
        // Process 16 bytes at a time using lookup table
        // SSE2 doesn't have efficient gather, so we batch scalar lookups
        int i = 0;
        int simdEnd = yPlane.Length - 16;

        fixed (byte* yPtr = yPlane)
        fixed (byte* lutPtr = yLookupTable)
        {
            for (; i <= simdEnd; i += 16)
            {
                // Unroll 16 lookups
                yPtr[i + 0] = lutPtr[yPtr[i + 0]];
                yPtr[i + 1] = lutPtr[yPtr[i + 1]];
                yPtr[i + 2] = lutPtr[yPtr[i + 2]];
                yPtr[i + 3] = lutPtr[yPtr[i + 3]];
                yPtr[i + 4] = lutPtr[yPtr[i + 4]];
                yPtr[i + 5] = lutPtr[yPtr[i + 5]];
                yPtr[i + 6] = lutPtr[yPtr[i + 6]];
                yPtr[i + 7] = lutPtr[yPtr[i + 7]];
                yPtr[i + 8] = lutPtr[yPtr[i + 8]];
                yPtr[i + 9] = lutPtr[yPtr[i + 9]];
                yPtr[i + 10] = lutPtr[yPtr[i + 10]];
                yPtr[i + 11] = lutPtr[yPtr[i + 11]];
                yPtr[i + 12] = lutPtr[yPtr[i + 12]];
                yPtr[i + 13] = lutPtr[yPtr[i + 13]];
                yPtr[i + 14] = lutPtr[yPtr[i + 14]];
                yPtr[i + 15] = lutPtr[yPtr[i + 15]];
            }

            // Handle remainder
            for (; i < yPlane.Length; i++)
            {
                yPtr[i] = lutPtr[yPtr[i]];
            }
        }
    }

    private void ProcessChroma(Span<byte> uPlane, Span<byte> vPlane)
    {
        if (uPlane.Length != vPlane.Length)
            return;

        for (int i = 0; i < uPlane.Length; i++)
        {
            int u = uPlane[i];
            int v = vPlane[i];
            int uvIndex = u * 256 + v;

            uPlane[i] = uLookupTable[uvIndex];
            vPlane[i] = vLookupTable[uvIndex];
        }
    }

    private void UpdateLookupTables()
    {
        // Build Y lookup table (brightness, contrast, gamma)
        float brightnessOffset = brightness * 128.0f;  // -128 to +128
        
        for (int i = 0; i < 256; i++)
        {
            // Apply contrast around midpoint (128)
            float y = (i - 128.0f) * contrast + 128.0f;
            
            // Apply brightness
            y += brightnessOffset;
            
            // Apply gamma (convert to 0-1, apply power, convert back)
            if (gamma != 1.0f && y > 0)
            {
                float normalized = Math.Clamp(y / 255.0f, 0.0f, 1.0f);
                y = MathF.Pow(normalized, 1.0f / gamma) * 255.0f;
            }
            
            yLookupTable[i] = (byte)Math.Clamp((int)(y + 0.5f), 0, 255);
        }

        // Build UV lookup tables (saturation and hue)
        float hueRadians = hue * MathF.PI / 180.0f;
        float cosHue = MathF.Cos(hueRadians);
        float sinHue = MathF.Sin(hueRadians);

        for (int u = 0; u < 256; u++)
        {
            for (int v = 0; v < 256; v++)
            {
                // Center U and V around 128
                float uCentered = u - 128.0f;
                float vCentered = v - 128.0f;

                // Apply saturation
                uCentered *= saturation;
                vCentered *= saturation;

                // Apply hue rotation
                float uNew = uCentered * cosHue - vCentered * sinHue;
                float vNew = uCentered * sinHue + vCentered * cosHue;

                // Convert back to 0-255 range
                int uvIndex = u * 256 + v;
                uLookupTable[uvIndex] = (byte)Math.Clamp((int)(uNew + 128.5f), 0, 255);
                vLookupTable[uvIndex] = (byte)Math.Clamp((int)(vNew + 128.5f), 0, 255);
            }
        }

        tablesNeedUpdate = false;
    }
}

/// <summary>
/// Video color correction with white balance and color temperature.
/// </summary>
public static class ColorCorrection
{
    /// <summary>
    /// Adjusts color temperature (warm/cool) on BGRA data.
    /// </summary>
    /// <param name="bgraData">Pixel data</param>
    /// <param name="temperature">Temperature adjustment (-100 = cool/blue, +100 = warm/orange)</param>
    public static void AdjustTemperature(Span<byte> bgraData, float temperature)
    {
        // Temperature affects red and blue channels inversely
        float tempNormalized = temperature / 100.0f;
        float redMultiplier = 1.0f + tempNormalized * 0.1f;
        float blueMultiplier = 1.0f - tempNormalized * 0.1f;

        for (int i = 0; i < bgraData.Length; i += 4)
        {
            int b = (int)(bgraData[i] * blueMultiplier);
            int r = (int)(bgraData[i + 2] * redMultiplier);
            
            bgraData[i] = (byte)Math.Clamp(b, 0, 255);
            bgraData[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
    }

    /// <summary>
    /// Applies auto white balance using gray world assumption.
    /// </summary>
    public static void AutoWhiteBalance(Span<byte> bgraData)
    {
        if (bgraData.Length < 4)
            return;

        // Calculate average of each channel
        long sumR = 0, sumG = 0, sumB = 0;
        int pixelCount = bgraData.Length / 4;

        for (int i = 0; i < bgraData.Length; i += 4)
        {
            sumB += bgraData[i];
            sumG += bgraData[i + 1];
            sumR += bgraData[i + 2];
        }

        float avgR = (float)sumR / pixelCount;
        float avgG = (float)sumG / pixelCount;
        float avgB = (float)sumB / pixelCount;

        // Gray world: assume average should be gray
        float avgGray = (avgR + avgG + avgB) / 3.0f;
        
        float scaleR = avgGray / avgR;
        float scaleG = avgGray / avgG;
        float scaleB = avgGray / avgB;

        // Apply correction
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            int b = (int)(bgraData[i] * scaleB);
            int g = (int)(bgraData[i + 1] * scaleG);
            int r = (int)(bgraData[i + 2] * scaleR);
            
            bgraData[i] = (byte)Math.Clamp(b, 0, 255);
            bgraData[i + 1] = (byte)Math.Clamp(g, 0, 255);
            bgraData[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
    }

    /// <summary>
    /// Inverts colors (negative effect).
    /// </summary>
    public static void Invert(Span<byte> bgraData)
    {
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            bgraData[i] = (byte)(255 - bgraData[i]);         // B
            bgraData[i + 1] = (byte)(255 - bgraData[i + 1]); // G
            bgraData[i + 2] = (byte)(255 - bgraData[i + 2]); // R
            // Alpha unchanged
        }
    }

    /// <summary>
    /// Converts to grayscale using luminance weights.
    /// </summary>
    public static void Grayscale(Span<byte> bgraData)
    {
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            // ITU-R BT.709 weights
            int gray = (bgraData[i + 2] * 54 + bgraData[i + 1] * 183 + bgraData[i] * 19) >> 8;
            bgraData[i] = (byte)gray;
            bgraData[i + 1] = (byte)gray;
            bgraData[i + 2] = (byte)gray;
        }
    }

    /// <summary>
    /// Applies sepia tone effect.
    /// </summary>
    public static void Sepia(Span<byte> bgraData)
    {
        for (int i = 0; i < bgraData.Length; i += 4)
        {
            int b = bgraData[i];
            int g = bgraData[i + 1];
            int r = bgraData[i + 2];

            // Sepia matrix
            int newR = (int)(r * 0.393f + g * 0.769f + b * 0.189f);
            int newG = (int)(r * 0.349f + g * 0.686f + b * 0.168f);
            int newB = (int)(r * 0.272f + g * 0.534f + b * 0.131f);

            bgraData[i] = (byte)Math.Min(newB, 255);
            bgraData[i + 1] = (byte)Math.Min(newG, 255);
            bgraData[i + 2] = (byte)Math.Min(newR, 255);
        }
    }
}

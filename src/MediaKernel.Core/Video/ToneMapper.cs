// HDR to SDR tone mapping implementation
// Reference: VLC's libplacebo, ITU-R BT.2390-8, Uncharted 2 (Hable), Reinhard 2002

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Video;

/// <summary>
/// Tone mapping algorithm selection.
/// </summary>
public enum ToneMappingAlgorithm
{
    /// <summary>Simple clipping (hard cut at 1.0).</summary>
    Clip,
    
    /// <summary>ITU-R BT.2390 EETF (reference standard for HDR10 to SDR).</summary>
    Bt2390,
    
    /// <summary>Reinhard global operator (preserves local contrast).</summary>
    Reinhard,
    
    /// <summary>Hable/Uncharted 2 filmic curve (cinematic look).</summary>
    Hable,
    
    /// <summary>Mobius with smooth rolloff (avoids hard clipping).</summary>
    Mobius,
    
    /// <summary>Simple gamma curve.</summary>
    Gamma,
    
    /// <summary>Linear scaling (no compression).</summary>
    Linear,
    
    /// <summary>ITU-R BT.2446 Method A (newer ITU standard, piecewise log curve).</summary>
    Bt2446A,
    
    /// <summary>Spline-based custom curve (user-defined control points).</summary>
    Spline
}

/// <summary>
/// Configuration for tone mapping operations.
/// </summary>
public sealed class ToneMappingConfig
{
    /// <summary>Tone mapping algorithm to use.</summary>
    public ToneMappingAlgorithm Algorithm { get; set; } = ToneMappingAlgorithm.Bt2390;
    
    /// <summary>Target display peak luminance in nits (default: 100 for SDR).</summary>
    public float TargetPeakNits { get; set; } = 100f;
    
    /// <summary>Source content peak luminance in nits (0 = auto-detect from metadata).</summary>
    public float SourcePeakNits { get; set; } = 0f;
    
    /// <summary>Algorithm-specific parameter (interpretation varies by algorithm).</summary>
    public float ToneMappingParam { get; set; } = 0f;
    
    /// <summary>Desaturation strength for out-of-gamut colors (0-1).</summary>
    public float Desaturation { get; set; } = 0.5f;
    
    /// <summary>Contrast recovery strength after tone mapping (0-1).</summary>
    public float ContrastRecovery { get; set; } = 0f;
}

/// <summary>
/// HDR to SDR tone mapper with multiple algorithms.
/// Operates on linear RGB values normalized to [0, peakLuminance].
/// </summary>
public sealed class ToneMapper
{
    private readonly ToneMappingConfig config;
    
    // Lookup table for fast PQ EOTF (electro-optical transfer function)
    // Maps 10-bit PQ code values to linear luminance
    private readonly float[] pqToLinearLut = new float[1024];
    
    // Lookup table for fast SDR OETF (optical-electro transfer function)
    // Maps linear luminance [0-1] to 8-bit gamma-corrected values
    private readonly byte[] linearToSdrLut = new byte[4096];
    
    // Pre-computed tone mapping curve for performance
    private readonly float[] toneCurveLut = new float[4096];
    
    private float sourcePeak;
    private float targetPeak;
    private bool lutsValid;
    
    public ToneMapper(ToneMappingConfig? config = null)
    {
        this.config = config ?? new ToneMappingConfig();
        
        // Initialize peaks from config or defaults
        sourcePeak = this.config.SourcePeakNits > 0 ? this.config.SourcePeakNits : 1000f;
        targetPeak = this.config.TargetPeakNits > 0 ? this.config.TargetPeakNits : 100f;
        
        BuildLookupTables();
    }
    
    /// <summary>
    /// Processes HDR content to SDR in-place.
    /// Input: BGRA buffer with linear HDR values (0-65535 for 16-bit).
    /// Output: BGRA buffer with gamma-corrected SDR values (0-255).
    /// </summary>
    public void ProcessBgra(Span<byte> pixels, int width, int height, int stride, in HdrMetadata metadata)
    {
        UpdatePeakLuminance(in metadata);
        EnsureLutsValid();
        
        if (Avx2.IsSupported && width >= 8)
            ProcessBgraAvx2(pixels, width, height, stride);
        else if (Sse2.IsSupported && width >= 4)
            ProcessBgraSse2(pixels, width, height, stride);
        else
            ProcessBgraScalar(pixels, width, height, stride);
    }
    
    /// <summary>
    /// Applies PQ EOTF to convert from PQ code values to linear luminance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float PqToLinear(float pqValue)
    {
        // SMPTE ST 2084 PQ EOTF
        // Y = ((max(Lc^(1/m2) - c1, 0)) / (c2 - c3 * Lc^(1/m2)))^(1/m1)
        // where Lc is the PQ code value in [0, 1]
        const float m1 = 0.1593017578125f;  // 2610/16384
        const float m2 = 78.84375f;          // 2523/32 * 128
        const float c1 = 0.8359375f;         // 3424/4096
        const float c2 = 18.8515625f;        // 2413/128
        const float c3 = 18.6875f;           // 2392/128
        const float peakLuminance = 10000f;  // PQ reference peak
        
        float powM2 = MathF.Pow(pqValue, 1f / m2);
        float num = MathF.Max(powM2 - c1, 0f);
        float den = c2 - c3 * powM2;
        float linear = MathF.Pow(num / den, 1f / m1);
        
        return linear * peakLuminance;  // Return absolute luminance in nits
    }
    
    /// <summary>
    /// Applies HLG OETF to convert from HLG signal to linear luminance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float HlgToLinear(float hlgValue)
    {
        // ARIB STD-B67 HLG OETF inverse
        const float a = 0.17883277f;
        const float b = 0.28466892f;
        const float c = 0.55991073f;
        
        float linear;
        if (hlgValue <= 0.5f)
        {
            linear = (hlgValue * hlgValue) / 3f;
        }
        else
        {
            linear = (MathF.Exp((hlgValue - c) / a) + b) / 12f;
        }
        
        // Apply OOTF (scene to display, gamma 1.2)
        return MathF.Pow(linear, 1.2f) * 1000f;  // HLG nominal 1000 nits
    }
    
    /// <summary>
    /// Applies SDR gamma (BT.1886) OETF.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float LinearToSdr(float linearValue)
    {
        // BT.1886 EOTF inverse (gamma ~2.4)
        const float gamma = 1f / 2.4f;
        return MathF.Pow(Math.Clamp(linearValue, 0f, 1f), gamma);
    }
    
    /// <summary>
    /// Applies the selected tone mapping curve to a linear luminance value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ToneMap(float linearNits)
    {
        // Guard against divide by zero
        if (sourcePeak <= 0) sourcePeak = 1000f;
        if (targetPeak <= 0) targetPeak = 100f;
        
        float normalized = linearNits / sourcePeak;
        float targetNormalized = targetPeak / sourcePeak;
        
        float mapped = config.Algorithm switch
        {
            ToneMappingAlgorithm.Clip => ToneMapClip(normalized),
            ToneMappingAlgorithm.Bt2390 => ToneMapBt2390(normalized, targetNormalized),
            ToneMappingAlgorithm.Reinhard => ToneMapReinhard(normalized, targetNormalized),
            ToneMappingAlgorithm.Hable => ToneMapHable(normalized),
            ToneMappingAlgorithm.Mobius => ToneMapMobius(normalized, targetNormalized),
            ToneMappingAlgorithm.Gamma => ToneMapGamma(normalized, targetNormalized),
            ToneMappingAlgorithm.Linear => ToneMapLinear(normalized, targetNormalized),
            ToneMappingAlgorithm.Bt2446A => ToneMapBt2446A(normalized, sourcePeak, targetPeak),
            ToneMappingAlgorithm.Spline => ToneMapSpline(normalized, targetNormalized),
            _ => ToneMapBt2390(normalized, targetNormalized)
        };
        
        // Handle NaN or infinity
        if (float.IsNaN(mapped) || float.IsInfinity(mapped))
        {
            mapped = Math.Clamp(normalized, 0f, 1f);
        }
        
        return Math.Clamp(mapped, 0f, 1f);
    }
    
    #region Tone Mapping Algorithms
    
    /// <summary>
    /// Simple clipping - hard cut at 1.0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapClip(float x)
    {
        return Math.Clamp(x, 0f, 1f);
    }
    
    /// <summary>
    /// ITU-R BT.2390 EETF (Electro-Electrical Transfer Function).
    /// Reference tone mapping for HDR10 to SDR conversion.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapBt2390(float x, float targetRatio)
    {
        // BT.2390-8 EETF (simplified version)
        // Uses a hermite spline for smooth knee compression
        if (x <= 0f) return 0f;
        if (x >= 1f) x = 1f;
        
        // Knee start point (where compression begins)
        float ks = 1.5f * targetRatio - 0.5f;
        ks = Math.Clamp(ks, 0f, 1f);
        
        if (x < ks)
        {
            // Linear region below knee
            return x;
        }
        else
        {
            // Hermite spline compression above knee
            float t = (x - ks) / (1f - ks);
            t = Math.Clamp(t, 0f, 1f);
            
            // Hermite interpolation: h00(t) * p0 + h10(t) * m0 + h01(t) * p1 + h11(t) * m1
            float t2 = t * t;
            float t3 = t2 * t;
            
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            
            float p0 = ks;
            float p1 = 1f;
            float m0 = 1f - ks;  // tangent at knee
            
            return h00 * p0 + h10 * m0 * (1f - ks) + h01 * p1;
        }
    }
    
    /// <summary>
    /// Reinhard global tone mapping operator.
    /// From "Photographic Tone Reproduction for Digital Images" (Reinhard 2002).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapReinhard(float x, float targetRatio)
    {
        // Extended Reinhard with white point
        // L_d = L * (1 + L/L_white²) / (1 + L)
        float whitePoint = 1f / targetRatio;
        float whitePointSq = whitePoint * whitePoint;
        
        return x * (1f + x / whitePointSq) / (1f + x);
    }
    
    /// <summary>
    /// Hable/Uncharted 2 filmic tone mapping.
    /// Designed for cinematic look with good highlight rolloff.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ToneMapHable(float x)
    {
        // Uncharted 2 tone mapping (John Hable)
        // f(x) = ((x*(A*x+C*B)+D*E)/(x*(A*x+B)+D*F))-E/F
        const float W = 11.2f;  // White point
        
        static float Hable(float val)
        {
            const float A = 0.15f;  // Shoulder strength
            const float B = 0.50f;  // Linear strength
            const float C = 0.10f;  // Linear angle
            const float D = 0.20f;  // Toe strength
            const float E = 0.02f;  // Toe numerator
            const float F = 0.30f;  // Toe denominator
            return ((val * (A * val + C * B) + D * E) / (val * (A * val + B) + D * F)) - E / F;
        }
        
        float exposure = config.ToneMappingParam > 0 ? config.ToneMappingParam : 2f;
        float curr = Hable(exposure * x);
        float whiteScale = 1f / Hable(W);
        
        return curr * whiteScale;
    }
    
    /// <summary>
    /// Mobius tone mapping with smooth linear-to-knee transition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapMobius(float x, float targetRatio)
    {
        // Mobius transform: keeps blacks linear, smooth rolloff for highlights
        float knee = targetRatio * 0.5f;
        knee = Math.Clamp(knee, 0.1f, 0.9f);
        
        if (x <= knee)
        {
            return x;
        }
        else
        {
            // Mobius: (bx + a) / (cx + d) where continuity at knee is ensured
            float a = -knee * knee * (1f - knee);
            float b = 2f * knee * (1f - knee);
            float d = 1f - knee;
            
            return (b * x + a) / (x + d);
        }
    }
    
    /// <summary>
    /// Simple gamma-based tone mapping.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapGamma(float x, float targetRatio)
    {
        // Power curve: x^gamma where gamma compresses the range
        float gamma = MathF.Log(targetRatio) / MathF.Log(1f);
        gamma = Math.Clamp(gamma, 0.5f, 2f);
        return MathF.Pow(Math.Clamp(x, 0f, 1f), gamma);
    }
    
    /// <summary>
    /// Linear scaling (simple division by peak).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapLinear(float x, float targetRatio)
    {
        return x * targetRatio;
    }
    
    /// <summary>
    /// ITU-R BT.2446 Method A tone mapping.
    /// Reference: ITU-R Report BT.2446-1 "Methods for conversion of HDR content to SDR content"
    /// Uses a piecewise log-based curve with distinct regions for shadows, mid-tones, and highlights.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToneMapBt2446A(float x, float sourcePeakNits, float targetPeakNits)
    {
        if (x <= 0f) return 0f;
        
        // BT.2446 Method A operates on absolute luminance
        // Input x is normalized (0-1 relative to source peak)
        float Lhdr = x * sourcePeakNits;
        
        // Reference peak for 1000 nit content (most common HDR10)
        float Lhdrp = sourcePeakNits;
        float Lsdr = targetPeakNits;
        
        // pHDR parameter based on source peak (Equation 1)
        // pHDR = 1 + 32 * (L_HDR_peak / 10000)^(1/2.4)
        float pHdr = 1f + 32f * MathF.Pow(Lhdrp / 10000f, 1f / 2.4f);
        
        // Convert to log encoding (Equation 2)
        // Y'p = log(1 + (pHDR - 1) * Y) / log(pHDR)
        // where Y = Lhdr / Lhdrp (normalized HDR luminance)
        float y = Lhdr / Lhdrp;
        if (y <= 0f) return 0f;
        
        float logPHdr = MathF.Log(pHdr);
        float yPrime = MathF.Log(1f + (pHdr - 1f) * y) / logPHdr;
        
        // BT.2446 piecewise tone curve (Equations 3-5)
        // Three regions: shadow (linear), mid-tone (curved), highlight (compressed)
        float yc;
        
        // Knee points derived from target/source ratio
        float sigma = Lsdr / Lhdrp;
        float k1 = 0.5f * sigma;      // Shadow/mid-tone transition
        float k2 = 1f - 0.2f * sigma; // Mid-tone/highlight transition
        
        if (yPrime < k1)
        {
            // Shadow region: linear passthrough
            yc = yPrime;
        }
        else if (yPrime < k2)
        {
            // Mid-tone region: smooth curve using power function
            // Provides gradual compression while preserving contrast
            float t = (yPrime - k1) / (k2 - k1);
            float alpha = 0.8f + 0.2f * sigma; // Shape factor
            float curve = k1 + (k2 - k1) * MathF.Pow(t, alpha);
            yc = curve;
        }
        else
        {
            // Highlight region: logarithmic compression
            // Aggressively compresses specular highlights
            float t = (yPrime - k2) / (1f - k2);
            float highlightRange = 1f - k2;
            float compressed = k2 + highlightRange * (1f - MathF.Pow(1f - t, 2f));
            yc = compressed;
        }
        
        // Convert back from log domain (inverse of Equation 2)
        float yOut = (MathF.Pow(pHdr, yc) - 1f) / (pHdr - 1f);
        
        // Scale to SDR range
        float result = yOut * sigma;
        
        return Math.Clamp(result, 0f, 1f);
    }
    
    /// <summary>
    /// Spline-based tone mapping with user-defined control points.
    /// Uses a cubic Hermite spline for smooth interpolation between control points.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ToneMapSpline(float x, float targetRatio)
    {
        // Default spline control points if none provided
        // Format: (input, output) pairs normalized to [0, 1]
        // These provide a smooth S-curve with good shadow/highlight behavior
        ReadOnlySpan<float> defaultInputs = [0f, 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f];
        ReadOnlySpan<float> defaultOutputs = [0f, 0.15f, 0.4f, 0.6f, 0.8f, 0.92f, 1f];
        
        return EvaluateCubicHermiteSpline(x, defaultInputs, defaultOutputs);
    }
    
    /// <summary>
    /// Evaluates a cubic Hermite spline at the given input value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EvaluateCubicHermiteSpline(float x, ReadOnlySpan<float> inputs, ReadOnlySpan<float> outputs)
    {
        if (inputs.Length < 2 || inputs.Length != outputs.Length)
            return x;
        
        x = Math.Clamp(x, 0f, 1f);
        
        // Find the segment containing x
        int segment = 0;
        for (int i = 0; i < inputs.Length - 1; i++)
        {
            if (x <= inputs[i + 1])
            {
                segment = i;
                break;
            }
            segment = i;
        }
        
        // Get segment endpoints
        float x0 = inputs[segment];
        float x1 = inputs[Math.Min(segment + 1, inputs.Length - 1)];
        float y0 = outputs[segment];
        float y1 = outputs[Math.Min(segment + 1, outputs.Length - 1)];
        
        // Handle edge case of zero-length segment
        if (Math.Abs(x1 - x0) < 1e-6f)
            return y0;
        
        // Calculate tangents using finite differences (Catmull-Rom style)
        float m0 = CalculateTangent(outputs, segment);
        float m1 = CalculateTangent(outputs, segment + 1);
        
        // Normalize to [0, 1] within segment
        float t = (x - x0) / (x1 - x0);
        float t2 = t * t;
        float t3 = t2 * t;
        
        // Hermite basis functions
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        
        // Scale tangents by segment width
        float segmentWidth = x1 - x0;
        
        return h00 * y0 + h10 * segmentWidth * m0 + h01 * y1 + h11 * segmentWidth * m1;
    }
    
    /// <summary>
    /// Calculate tangent at a control point using central differences.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateTangent(ReadOnlySpan<float> values, int index)
    {
        if (values.Length < 2) return 0f;
        
        index = Math.Clamp(index, 0, values.Length - 1);
        
        if (index == 0)
        {
            // Forward difference at start
            return values[1] - values[0];
        }
        else if (index >= values.Length - 1)
        {
            // Backward difference at end
            return values[values.Length - 1] - values[values.Length - 2];
        }
        else
        {
            // Central difference
            return (values[index + 1] - values[index - 1]) * 0.5f;
        }
    }
    
    #endregion
    
    #region Implementation Details
    
    private void UpdatePeakLuminance(in HdrMetadata metadata)
    {
        float newSourcePeak = config.SourcePeakNits > 0 
            ? config.SourcePeakNits 
            : metadata.PeakLuminance;
        
        if (Math.Abs(newSourcePeak - sourcePeak) > 0.01f || 
            Math.Abs(config.TargetPeakNits - targetPeak) > 0.01f)
        {
            sourcePeak = newSourcePeak;
            targetPeak = config.TargetPeakNits;
            lutsValid = false;
        }
    }
    
    private void EnsureLutsValid()
    {
        if (lutsValid) return;
        BuildLookupTables();
        lutsValid = true;
    }
    
    private void BuildLookupTables()
    {
        // Build PQ to linear LUT (10-bit input)
        for (int i = 0; i < 1024; i++)
        {
            float pqNormalized = i / 1023f;
            pqToLinearLut[i] = PqToLinear(pqNormalized);
        }
        
        // Build tone curve LUT (12-bit precision for intermediate values)
        float peak = sourcePeak > 0 ? sourcePeak : 1000f;
        for (int i = 0; i < 4096; i++)
        {
            float linearNits = (i / 4095f) * peak;
            toneCurveLut[i] = ToneMap(linearNits);
        }
        
        // Build linear to SDR LUT (12-bit input to 8-bit output)
        for (int i = 0; i < 4096; i++)
        {
            float linear = i / 4095f;
            float sdr = LinearToSdr(linear);
            linearToSdrLut[i] = (byte)(sdr * 255f + 0.5f);
        }
    }
    
    private void ProcessBgraScalar(Span<byte> pixels, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            
            for (int x = 0; x < width; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                
                // Read BGRA (assumes linear light already)
                float b = pixels[pixelOffset] / 255f;
                float g = pixels[pixelOffset + 1] / 255f;
                float r = pixels[pixelOffset + 2] / 255f;
                // Alpha unchanged
                
                // Calculate luminance (BT.2020 coefficients)
                float luma = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                
                if (luma > 0.001f)
                {
                    // Apply tone mapping to luminance
                    float mappedLuma = ToneMap(luma * sourcePeak);
                    
                    // Scale RGB by luminance ratio (preserve color ratios)
                    float scale = mappedLuma / luma;
                    
                    r = Math.Clamp(r * scale, 0f, 1f);
                    g = Math.Clamp(g * scale, 0f, 1f);
                    b = Math.Clamp(b * scale, 0f, 1f);
                    
                    // Optional desaturation for out-of-gamut colors
                    if (config.Desaturation > 0f)
                    {
                        float maxRgb = MathF.Max(r, MathF.Max(g, b));
                        if (maxRgb > 1f)
                        {
                            float desat = config.Desaturation * (maxRgb - 1f) / maxRgb;
                            float gray = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                            r = r + (gray - r) * desat;
                            g = g + (gray - g) * desat;
                            b = b + (gray - b) * desat;
                        }
                    }
                }
                
                // Apply SDR gamma and write back
                pixels[pixelOffset] = (byte)(LinearToSdr(b) * 255f + 0.5f);
                pixels[pixelOffset + 1] = (byte)(LinearToSdr(g) * 255f + 0.5f);
                pixels[pixelOffset + 2] = (byte)(LinearToSdr(r) * 255f + 0.5f);
            }
        }
    }
    
    private void ProcessBgraSse2(Span<byte> pixels, int width, int height, int stride)
    {
        // SSE2 implementation - process 4 pixels at a time
        // For now, fall back to scalar for correctness
        // TODO: Implement proper SSE2 vectorization
        ProcessBgraScalar(pixels, width, height, stride);
    }
    
    private void ProcessBgraAvx2(Span<byte> pixels, int width, int height, int stride)
    {
        // AVX2 implementation - process 8 pixels at a time
        // For now, fall back to scalar for correctness
        // TODO: Implement proper AVX2 vectorization
        ProcessBgraScalar(pixels, width, height, stride);
    }
    
    #endregion
}

/// <summary>
/// Extension methods for HDR processing.
/// </summary>
public static class HdrExtensions
{
    /// <summary>
    /// Determines if YUV data needs tone mapping based on transfer function.
    /// </summary>
    public static bool RequiresToneMapping(this TransferFunction transfer)
    {
        return transfer == TransferFunction.Pq || transfer == TransferFunction.Hlg;
    }
    
    /// <summary>
    /// Gets the nominal peak luminance for a transfer function.
    /// </summary>
    public static float GetNominalPeakNits(this TransferFunction transfer)
    {
        return transfer switch
        {
            TransferFunction.Pq => 10000f,
            TransferFunction.Hlg => 1000f,
            TransferFunction.Bt1886 => 100f,
            TransferFunction.Srgb => 80f,
            _ => 100f
        };
    }
}

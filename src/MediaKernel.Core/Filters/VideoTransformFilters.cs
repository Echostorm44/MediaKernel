// MediaKernel - Video Transform Filters
// Rotation, mirroring, and canvas padding filters

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using MediaKernel.Core.Video;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Rotation angle for video frames.
/// </summary>
public enum RotationAngle
{
    None = 0,
    Clockwise90 = 90,
    Clockwise180 = 180,
    Clockwise270 = 270
}

/// <summary>
/// Mirror mode for video frames.
/// </summary>
public enum MirrorMode
{
    None,
    Horizontal,
    Vertical,
    Both
}

/// <summary>
/// Filter to rotate video frames by 90, 180, or 270 degrees.
/// </summary>
public sealed class RotateFilter
{
    private RotationAngle _angle;
    
    public RotateFilter(RotationAngle angle = RotationAngle.Clockwise90)
    {
        _angle = angle;
    }
    
    public RotationAngle Angle
    {
        get => _angle;
        set => _angle = value;
    }
    
    /// <summary>
    /// Calculate output dimensions after rotation.
    /// </summary>
    public (int width, int height) GetOutputDimensions(int inputWidth, int inputHeight)
    {
        return _angle switch
        {
            RotationAngle.Clockwise90 or RotationAngle.Clockwise270 => (inputHeight, inputWidth),
            _ => (inputWidth, inputHeight)
        };
    }
    
    /// <summary>
    /// Rotate frame data in-place or to a destination buffer.
    /// </summary>
    public void Rotate(ReadOnlySpan<byte> source, Span<byte> destination,
        int sourceWidth, int sourceHeight, int bytesPerPixel = 4)
    {
        if (_angle == RotationAngle.None)
        {
            source.CopyTo(destination);
            return;
        }
        
        int sourceStride = sourceWidth * bytesPerPixel;
        
        switch (_angle)
        {
            case RotationAngle.Clockwise90:
                Rotate90(source, destination, sourceWidth, sourceHeight, bytesPerPixel);
                break;
            case RotationAngle.Clockwise180:
                Rotate180(source, destination, sourceWidth, sourceHeight, bytesPerPixel);
                break;
            case RotationAngle.Clockwise270:
                Rotate270(source, destination, sourceWidth, sourceHeight, bytesPerPixel);
                break;
        }
    }
    
    private static void Rotate90(ReadOnlySpan<byte> source, Span<byte> destination,
        int sourceWidth, int sourceHeight, int bytesPerPixel)
    {
        int destWidth = sourceHeight;
        int sourceStride = sourceWidth * bytesPerPixel;
        int destStride = destWidth * bytesPerPixel;
        
        for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            int destX = sourceHeight - 1 - sourceY;
            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                int destY = sourceX;
                int sourceIndex = sourceY * sourceStride + sourceX * bytesPerPixel;
                int destIndex = destY * destStride + destX * bytesPerPixel;
                
                for (int b = 0; b < bytesPerPixel; b++)
                    destination[destIndex + b] = source[sourceIndex + b];
            }
        }
    }
    
    private static void Rotate180(ReadOnlySpan<byte> source, Span<byte> destination,
        int sourceWidth, int sourceHeight, int bytesPerPixel)
    {
        int stride = sourceWidth * bytesPerPixel;
        
        for (int y = 0; y < sourceHeight; y++)
        {
            int destY = sourceHeight - 1 - y;
            for (int x = 0; x < sourceWidth; x++)
            {
                int destX = sourceWidth - 1 - x;
                int sourceIndex = y * stride + x * bytesPerPixel;
                int destIndex = destY * stride + destX * bytesPerPixel;
                
                for (int b = 0; b < bytesPerPixel; b++)
                    destination[destIndex + b] = source[sourceIndex + b];
            }
        }
    }
    
    private static void Rotate270(ReadOnlySpan<byte> source, Span<byte> destination,
        int sourceWidth, int sourceHeight, int bytesPerPixel)
    {
        int destWidth = sourceHeight;
        int sourceStride = sourceWidth * bytesPerPixel;
        int destStride = destWidth * bytesPerPixel;
        
        for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            int destX = sourceY;
            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                int destY = sourceWidth - 1 - sourceX;
                int sourceIndex = sourceY * sourceStride + sourceX * bytesPerPixel;
                int destIndex = destY * destStride + destX * bytesPerPixel;
                
                for (int b = 0; b < bytesPerPixel; b++)
                    destination[destIndex + b] = source[sourceIndex + b];
            }
        }
    }
}

/// <summary>
/// Filter to mirror/flip video frames horizontally or vertically.
/// </summary>
public sealed class MirrorFilter
{
    private MirrorMode _mode;
    
    public MirrorFilter(MirrorMode mode = MirrorMode.Horizontal)
    {
        _mode = mode;
    }
    
    public MirrorMode Mode
    {
        get => _mode;
        set => _mode = value;
    }
    
    /// <summary>
    /// Mirror frame data to destination buffer.
    /// </summary>
    public void Mirror(ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int bytesPerPixel = 4)
    {
        if (_mode == MirrorMode.None)
        {
            source.CopyTo(destination);
            return;
        }
        
        int stride = width * bytesPerPixel;
        
        switch (_mode)
        {
            case MirrorMode.Horizontal:
                MirrorHorizontal(source, destination, width, height, bytesPerPixel);
                break;
            case MirrorMode.Vertical:
                MirrorVertical(source, destination, width, height, bytesPerPixel);
                break;
            case MirrorMode.Both:
                MirrorBoth(source, destination, width, height, bytesPerPixel);
                break;
        }
    }
    
    private static void MirrorHorizontal(ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int destX = width - 1 - x;
                int sourceIndex = y * stride + x * bytesPerPixel;
                int destIndex = y * stride + destX * bytesPerPixel;
                
                for (int b = 0; b < bytesPerPixel; b++)
                    destination[destIndex + b] = source[sourceIndex + b];
            }
        }
    }
    
    private static void MirrorVertical(ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        
        for (int y = 0; y < height; y++)
        {
            int destY = height - 1 - y;
            source.Slice(y * stride, stride).CopyTo(destination.Slice(destY * stride, stride));
        }
    }
    
    private static void MirrorBoth(ReadOnlySpan<byte> source, Span<byte> destination,
        int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        
        for (int y = 0; y < height; y++)
        {
            int destY = height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int destX = width - 1 - x;
                int sourceIndex = y * stride + x * bytesPerPixel;
                int destIndex = destY * stride + destX * bytesPerPixel;
                
                for (int b = 0; b < bytesPerPixel; b++)
                    destination[destIndex + b] = source[sourceIndex + b];
            }
        }
    }
}

/// <summary>
/// Filter to add letterbox (top/bottom) or pillarbox (left/right) padding.
/// </summary>
public sealed class CanvasFilter
{
    private int _targetWidth;
    private int _targetHeight;
    private byte _paddingRed;
    private byte _paddingGreen;
    private byte _paddingBlue;
    private byte _paddingAlpha;
    
    public CanvasFilter(int targetWidth, int targetHeight,
        byte red = 0, byte green = 0, byte blue = 0, byte alpha = 255)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Target dimensions must be positive");
        
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _paddingRed = red;
        _paddingGreen = green;
        _paddingBlue = blue;
        _paddingAlpha = alpha;
    }
    
    public int TargetWidth => _targetWidth;
    public int TargetHeight => _targetHeight;
    
    public void SetPaddingColor(byte red, byte green, byte blue, byte alpha = 255)
    {
        _paddingRed = red;
        _paddingGreen = green;
        _paddingBlue = blue;
        _paddingAlpha = alpha;
    }
    
    /// <summary>
    /// Calculate the rectangle where source content will be placed.
    /// </summary>
    public (int offsetX, int offsetY, int scaledWidth, int scaledHeight) CalculatePlacement(
        int sourceWidth, int sourceHeight, bool preserveAspectRatio = true)
    {
        if (!preserveAspectRatio)
        {
            return (0, 0, _targetWidth, _targetHeight);
        }
        
        float sourceAspect = (float)sourceWidth / sourceHeight;
        float targetAspect = (float)_targetWidth / _targetHeight;
        
        int scaledWidth, scaledHeight;
        int offsetX, offsetY;
        
        if (sourceAspect > targetAspect)
        {
            // Source is wider - add letterbox (top/bottom padding)
            scaledWidth = _targetWidth;
            scaledHeight = (int)(_targetWidth / sourceAspect);
            offsetX = 0;
            offsetY = (_targetHeight - scaledHeight) / 2;
        }
        else
        {
            // Source is taller - add pillarbox (left/right padding)
            scaledHeight = _targetHeight;
            scaledWidth = (int)(_targetHeight * sourceAspect);
            offsetX = (_targetWidth - scaledWidth) / 2;
            offsetY = 0;
        }
        
        return (offsetX, offsetY, scaledWidth, scaledHeight);
    }
    
    /// <summary>
    /// Apply canvas padding to a frame. Source should already be scaled to fit.
    /// </summary>
    public void ApplyPadding(ReadOnlySpan<byte> scaledSource, Span<byte> destination,
        int scaledWidth, int scaledHeight, int offsetX, int offsetY, int bytesPerPixel = 4)
    {
        int destStride = _targetWidth * bytesPerPixel;
        int sourceStride = scaledWidth * bytesPerPixel;
        
        // Fill entire destination with padding color first
        if (bytesPerPixel == 4)
        {
            FillWithPaddingRgba(destination);
        }
        else
        {
            // Fallback: fill with black
            destination.Fill(0);
        }
        
        // Copy scaled source to destination at offset
        for (int y = 0; y < scaledHeight; y++)
        {
            int destY = offsetY + y;
            if (destY < 0 || destY >= _targetHeight)
                continue;
            
            int sourceRowStart = y * sourceStride;
            int destRowStart = destY * destStride + offsetX * bytesPerPixel;
            
            int copyWidth = Math.Min(scaledWidth, _targetWidth - offsetX);
            if (copyWidth > 0 && offsetX >= 0)
            {
                scaledSource.Slice(sourceRowStart, copyWidth * bytesPerPixel)
                    .CopyTo(destination.Slice(destRowStart, copyWidth * bytesPerPixel));
            }
        }
    }
    
    private void FillWithPaddingRgba(Span<byte> destination)
    {
        // Create 4-byte pattern for RGBA
        uint pattern = (uint)_paddingRed | 
                       ((uint)_paddingGreen << 8) |
                       ((uint)_paddingBlue << 16) |
                       ((uint)_paddingAlpha << 24);
        
        if (Avx2.IsSupported && destination.Length >= 32)
        {
            var patternVec = Vector256.Create(pattern);
            FillSimdAvx2(destination, patternVec);
        }
        else if (Sse2.IsSupported && destination.Length >= 16)
        {
            var patternVec = Vector128.Create(pattern);
            FillSimdSse2(destination, patternVec);
        }
        else
        {
            // Scalar fallback
            for (int i = 0; i < destination.Length; i += 4)
            {
                destination[i] = _paddingRed;
                destination[i + 1] = _paddingGreen;
                destination[i + 2] = _paddingBlue;
                destination[i + 3] = _paddingAlpha;
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillSimdAvx2(Span<byte> destination, Vector256<uint> pattern)
    {
        unsafe
        {
            fixed (byte* ptr = destination)
            {
                float* floatPtr = (float*)ptr;
                int i = 0;
                int vectorCount = destination.Length / 32;
                for (int v = 0; v < vectorCount; v++)
                {
                    Avx.Store(floatPtr + (i / 4), pattern.AsSingle());
                    i += 32;
                }
                
                // Handle remainder
                while (i + 3 < destination.Length)
                {
                    *(uint*)(ptr + i) = pattern.GetElement(0);
                    i += 4;
                }
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillSimdSse2(Span<byte> destination, Vector128<uint> pattern)
    {
        unsafe
        {
            fixed (byte* ptr = destination)
            {
                float* floatPtr = (float*)ptr;
                int i = 0;
                int vectorCount = destination.Length / 16;
                for (int v = 0; v < vectorCount; v++)
                {
                    Sse.Store(floatPtr + (i / 4), pattern.AsSingle());
                    i += 16;
                }
                
                // Handle remainder
                while (i + 3 < destination.Length)
                {
                    *(uint*)(ptr + i) = pattern.GetElement(0);
                    i += 4;
                }
            }
        }
    }
}

/// <summary>
/// Filter for brightness, contrast, saturation, and hue adjustments.
/// </summary>
public sealed class ColorAdjustFilter
{
    private float _brightness; // -1.0 to 1.0, 0 = no change
    private float _contrast;   // 0.0 to 4.0, 1.0 = no change
    private float _saturation; // 0.0 to 4.0, 1.0 = no change
    private float _hue;        // -180 to 180 degrees
    
    // Cached transformation matrix
    private float _m00, _m01, _m02;
    private float _m10, _m11, _m12;
    private float _m20, _m21, _m22;
    private float _addR, _addG, _addB;
    private bool _matrixDirty = true;
    
    public ColorAdjustFilter(float brightness = 0f, float contrast = 1f, 
        float saturation = 1f, float hue = 0f)
    {
        _brightness = Math.Clamp(brightness, -1f, 1f);
        _contrast = Math.Clamp(contrast, 0f, 4f);
        _saturation = Math.Clamp(saturation, 0f, 4f);
        _hue = hue % 360f;
        _matrixDirty = true;
    }
    
    public float Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, -1f, 1f); _matrixDirty = true; }
    }
    
    public float Contrast
    {
        get => _contrast;
        set { _contrast = Math.Clamp(value, 0f, 4f); _matrixDirty = true; }
    }
    
    public float Saturation
    {
        get => _saturation;
        set { _saturation = Math.Clamp(value, 0f, 4f); _matrixDirty = true; }
    }
    
    public float Hue
    {
        get => _hue;
        set { _hue = value % 360f; _matrixDirty = true; }
    }
    
    /// <summary>
    /// Apply color adjustments to RGBA pixel data.
    /// </summary>
    public void Apply(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Source and destination must be same size");
        
        if (_matrixDirty)
            UpdateMatrix();
        
        // Check if no adjustment needed
        if (IsIdentity())
        {
            source.CopyTo(destination);
            return;
        }
        
        ApplyMatrix(source, destination);
    }
    
    private bool IsIdentity()
    {
        return _brightness == 0f && _contrast == 1f && _saturation == 1f && _hue == 0f;
    }
    
    private void UpdateMatrix()
    {
        // Build combined color transformation matrix
        // Order: Brightness -> Contrast -> Saturation -> Hue
        
        float hueRad = _hue * MathF.PI / 180f;
        float cosH = MathF.Cos(hueRad);
        float sinH = MathF.Sin(hueRad);
        
        // Luminance coefficients (BT.709)
        const float lumR = 0.2126f;
        const float lumG = 0.7152f;
        const float lumB = 0.0722f;
        
        // Pure saturation matrix (without hue)
        // s=0 gives grayscale, s=1 gives original colors
        float satR = lumR * (1 - _saturation);
        float satG = lumG * (1 - _saturation);
        float satB = lumB * (1 - _saturation);
        
        // Base saturation matrix
        float s00 = satR + _saturation;
        float s01 = satG;
        float s02 = satB;
        
        float s10 = satR;
        float s11 = satG + _saturation;
        float s12 = satB;
        
        float s20 = satR;
        float s21 = satG;
        float s22 = satB + _saturation;
        
        // Apply contrast scaling
        _m00 = s00 * _contrast;
        _m01 = s01 * _contrast;
        _m02 = s02 * _contrast;
        
        _m10 = s10 * _contrast;
        _m11 = s11 * _contrast;
        _m12 = s12 * _contrast;
        
        _m20 = s20 * _contrast;
        _m21 = s21 * _contrast;
        _m22 = s22 * _contrast;
        
        // Apply hue rotation in YIQ color space (simplified)
        if (_hue != 0f)
        {
            // Hue rotation modifies the color matrix
            // This is a simplified rotation in the UV plane
            float cosH2 = cosH;
            float sinH2 = sinH;
            
            // Rotate color channels 
            float t00 = _m00;
            float t01 = _m01 * cosH2 - _m02 * sinH2;
            float t02 = _m01 * sinH2 + _m02 * cosH2;
            _m00 = t00;
            _m01 = t01;
            _m02 = t02;
            
            float t10 = _m10;
            float t11 = _m11 * cosH2 - _m12 * sinH2;
            float t12 = _m11 * sinH2 + _m12 * cosH2;
            _m10 = t10;
            _m11 = t11;
            _m12 = t12;
            
            float t20 = _m20;
            float t21 = _m21 * cosH2 - _m22 * sinH2;
            float t22 = _m21 * sinH2 + _m22 * cosH2;
            _m20 = t20;
            _m21 = t21;
            _m22 = t22;
        }
        
        // Brightness offset (after contrast scaling)
        float brightnessOffset = _brightness * 255f + 128f * (1f - _contrast);
        _addR = brightnessOffset;
        _addG = brightnessOffset;
        _addB = brightnessOffset;
        
        _matrixDirty = false;
    }
    
    private void ApplyMatrix(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            float r = source[i];
            float g = source[i + 1];
            float b = source[i + 2];
            byte a = source[i + 3];
            
            float newR = _m00 * r + _m01 * g + _m02 * b + _addR;
            float newG = _m10 * r + _m11 * g + _m12 * b + _addG;
            float newB = _m20 * r + _m21 * g + _m22 * b + _addB;
            
            destination[i] = ClampToByte(newR);
            destination[i + 1] = ClampToByte(newG);
            destination[i + 2] = ClampToByte(newB);
            destination[i + 3] = a;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(float value)
    {
        int intVal = (int)(value + 0.5f);
        return (byte)Math.Clamp(intVal, 0, 255);
    }
}

/// <summary>
/// Simple scene change detection based on frame histogram difference.
/// </summary>
public sealed class SceneChangeDetector
{
    private readonly float _threshold;
    private int[]? _previousHistogram;
    
    public SceneChangeDetector(float threshold = 0.3f)
    {
        if (threshold <= 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1");
        
        _threshold = threshold;
    }
    
    public float Threshold => _threshold;
    
    /// <summary>
    /// Detect if a scene change occurred between this frame and the previous.
    /// </summary>
    /// <returns>True if scene change detected, along with the difference score.</returns>
    public (bool isSceneChange, float difference) Detect(ReadOnlySpan<byte> frameData)
    {
        // Build luminance histogram for current frame
        int[] histogram = new int[256];
        
        for (int i = 0; i < frameData.Length; i += 4)
        {
            // Calculate luminance: 0.299*R + 0.587*G + 0.114*B
            int luminance = (77 * frameData[i] + 150 * frameData[i + 1] + 29 * frameData[i + 2]) >> 8;
            histogram[luminance]++;
        }
        
        if (_previousHistogram == null)
        {
            _previousHistogram = histogram;
            return (false, 0f);
        }
        
        // Calculate histogram difference (sum of absolute differences)
        long totalPixels = frameData.Length / 4;
        long difference = 0;
        
        for (int i = 0; i < 256; i++)
        {
            difference += Math.Abs(histogram[i] - _previousHistogram[i]);
        }
        
        float normalizedDiff = (float)difference / (totalPixels * 2);
        bool isSceneChange = normalizedDiff >= _threshold;
        
        _previousHistogram = histogram;
        
        return (isSceneChange, normalizedDiff);
    }
    
    /// <summary>
    /// Reset the detector state.
    /// </summary>
    public void Reset()
    {
        _previousHistogram = null;
    }
}

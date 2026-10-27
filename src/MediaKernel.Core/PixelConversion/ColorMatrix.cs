// Color space transformation matrices for YUV↔RGB conversion.
// All matrices use fixed-point arithmetic (shifted integers) for speed and precision.
// Supports BT.601 (SD), BT.709 (HD), BT.2020 (UHD) with full/limited range.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.PixelConversion;

/// <summary>
/// Identifies the color space standard for YUV↔RGB conversion.
/// </summary>
public enum ColorSpace
{
    /// <summary>BT.601 — standard definition (SD) video.</summary>
    Bt601,

    /// <summary>BT.709 — high definition (HD) video.</summary>
    Bt709,

    /// <summary>BT.2020 — ultra high definition (UHD/HDR) video.</summary>
    Bt2020,
}

/// <summary>
/// Whether the Y/Cb/Cr values use full [0,255] range or limited (studio) [16,235]/[16,240] range.
/// </summary>
public enum ColorRange
{
    /// <summary>Limited/studio range: Y=[16,235], Cb/Cr=[16,240] for 8-bit.</summary>
    Limited,

    /// <summary>Full/PC range: Y=[0,255], Cb/Cr=[0,255] for 8-bit.</summary>
    Full,
}

/// <summary>
/// Pre-computed fixed-point coefficients for a specific YUV↔RGB conversion.
/// Uses 16-bit fractional precision (shift 16) for YUV→RGB and 15-bit for RGB→YUV.
/// All values are ready for multiply-then-shift operations with no floating point.
/// </summary>
public readonly struct ColorMatrix
{
    // ── YUV→RGB coefficients (fixed-point, shift 16) ──
    // R = yMul * (Y - yOffset) + crToR * (Cr - 128) >> SHIFT
    // G = yMul * (Y - yOffset) + crToG * (Cr - 128) + cbToG * (Cb - 128) >> SHIFT
    // B = yMul * (Y - yOffset) + cbToB * (Cb - 128) >> SHIFT

    /// <summary>Y multiplier (includes range expansion for limited range).</summary>
    public readonly int YMultiplier;

    /// <summary>Offset subtracted from Y before multiplication (16 for limited, 0 for full).</summary>
    public readonly int YOffset;

    /// <summary>Cr contribution to R channel.</summary>
    public readonly int CrToR;

    /// <summary>Cr contribution to G channel (negative).</summary>
    public readonly int CrToG;

    /// <summary>Cb contribution to G channel (negative).</summary>
    public readonly int CbToG;

    /// <summary>Cb contribution to B channel.</summary>
    public readonly int CbToB;

    // ── RGB→YUV coefficients (fixed-point, shift 15) ──
    // Y  = (rToY * R + gToY * G + bToY * B + yBias) >> SHIFT
    // Cb = (rToCb * R + gToCb * G + bToCb * B + cBias) >> SHIFT
    // Cr = (rToCr * R + gToCr * G + bToCr * B + cBias) >> SHIFT

    public readonly int RToY, GToY, BToY;
    public readonly int RToCb, GToCb, BToCb;
    public readonly int RToCr, GToCr, BToCr;

    /// <summary>Bias added to Y computation (for limited range offset).</summary>
    public readonly int YBias;

    /// <summary>Bias added to Cb/Cr computation (128 << shift for centering).</summary>
    public readonly int CBias;

    /// <summary>Right-shift count for YUV→RGB (16).</summary>
    public const int YuvToRgbShift = 16;

    /// <summary>Right-shift count for RGB→YUV (15).</summary>
    public const int RgbToYuvShift = 15;

    /// <summary>Chroma center value (128 for 8-bit).</summary>
    public readonly int ChromaCenter;

    /// <summary>The color space this matrix represents.</summary>
    public readonly ColorSpace Space;

    /// <summary>The color range this matrix represents.</summary>
    public readonly ColorRange Range;

    private ColorMatrix(
        ColorSpace space, ColorRange range,
        double kr, double kb)
    {
        Space = space;
        Range = range;

        double kg = 1.0 - kr - kb;
        ChromaCenter = 128;

        // ── YUV→RGB (shift 16) ──
        if (range == ColorRange.Limited)
        {
            // Limited range: Y=[16,235], C=[16,240]
            // yMul = 255/219 = 1.1644 → scale to expand to [0,255]
            // Chroma scale = 255/224 = 1.1384
            double yScale = 255.0 / 219.0;
            double cScale = 255.0 / 224.0;
            YOffset = 16;

            YMultiplier = (int)Math.Round(yScale * (1 << YuvToRgbShift));
            CrToR = (int)Math.Round(2.0 * (1.0 - kr) * cScale * (1 << YuvToRgbShift));
            CrToG = (int)Math.Round(-2.0 * kr * (1.0 - kr) / kg * cScale * (1 << YuvToRgbShift));
            CbToG = (int)Math.Round(-2.0 * kb * (1.0 - kb) / kg * cScale * (1 << YuvToRgbShift));
            CbToB = (int)Math.Round(2.0 * (1.0 - kb) * cScale * (1 << YuvToRgbShift));
        }
        else
        {
            // Full range: no scaling needed, Y and C are already [0,255]
            YOffset = 0;
            YMultiplier = 1 << YuvToRgbShift;
            CrToR = (int)Math.Round(2.0 * (1.0 - kr) * (1 << YuvToRgbShift));
            CrToG = (int)Math.Round(-2.0 * kr * (1.0 - kr) / kg * (1 << YuvToRgbShift));
            CbToG = (int)Math.Round(-2.0 * kb * (1.0 - kb) / kg * (1 << YuvToRgbShift));
            CbToB = (int)Math.Round(2.0 * (1.0 - kb) * (1 << YuvToRgbShift));
        }

        // ── RGB→YUV (shift 15) ──
        // Y  = kr*R + kg*G + kb*B
        // Cb = (B - Y) / (2*(1-kb)) + 128
        // Cr = (R - Y) / (2*(1-kr)) + 128
        if (range == ColorRange.Limited)
        {
            double yOutScale = 219.0 / 255.0;
            double cOutScale = 224.0 / 255.0;

            RToY = (int)Math.Round(kr * yOutScale * (1 << RgbToYuvShift));
            GToY = (int)Math.Round(kg * yOutScale * (1 << RgbToYuvShift));
            BToY = (int)Math.Round(kb * yOutScale * (1 << RgbToYuvShift));
            YBias = 16 << RgbToYuvShift;

            double cbScale = cOutScale / (2.0 * (1.0 - kb));
            RToCb = (int)Math.Round(-kr * cbScale * (1 << RgbToYuvShift));
            GToCb = (int)Math.Round(-kg * cbScale * (1 << RgbToYuvShift));
            BToCb = (int)Math.Round((1.0 - kb) * cbScale * (1 << RgbToYuvShift));

            double crScale = cOutScale / (2.0 * (1.0 - kr));
            RToCr = (int)Math.Round((1.0 - kr) * crScale * (1 << RgbToYuvShift));
            GToCr = (int)Math.Round(-kg * crScale * (1 << RgbToYuvShift));
            BToCr = (int)Math.Round(-kb * crScale * (1 << RgbToYuvShift));
        }
        else
        {
            RToY = (int)Math.Round(kr * (1 << RgbToYuvShift));
            GToY = (int)Math.Round(kg * (1 << RgbToYuvShift));
            BToY = (int)Math.Round(kb * (1 << RgbToYuvShift));
            YBias = 0;

            double cbFactor = 1.0 / (2.0 * (1.0 - kb));
            RToCb = (int)Math.Round(-kr * cbFactor * (1 << RgbToYuvShift));
            GToCb = (int)Math.Round(-kg * cbFactor * (1 << RgbToYuvShift));
            BToCb = (int)Math.Round((1.0 - kb) * cbFactor * (1 << RgbToYuvShift));

            double crFactor = 1.0 / (2.0 * (1.0 - kr));
            RToCr = (int)Math.Round((1.0 - kr) * crFactor * (1 << RgbToYuvShift));
            GToCr = (int)Math.Round(-kg * crFactor * (1 << RgbToYuvShift));
            BToCr = (int)Math.Round(-kb * crFactor * (1 << RgbToYuvShift));
        }

        CBias = 128 << RgbToYuvShift;
    }

    // ── Pre-built matrices ──────────────────────────────────────────

    /// <summary>BT.601 limited range (most SD content).</summary>
    public static readonly ColorMatrix Bt601Limited = new(ColorSpace.Bt601, ColorRange.Limited, Kr601, Kb601);

    /// <summary>BT.601 full range (JPEG-style).</summary>
    public static readonly ColorMatrix Bt601Full = new(ColorSpace.Bt601, ColorRange.Full, Kr601, Kb601);

    /// <summary>BT.709 limited range (most HD content).</summary>
    public static readonly ColorMatrix Bt709Limited = new(ColorSpace.Bt709, ColorRange.Limited, Kr709, Kb709);

    /// <summary>BT.709 full range.</summary>
    public static readonly ColorMatrix Bt709Full = new(ColorSpace.Bt709, ColorRange.Full, Kr709, Kb709);

    /// <summary>BT.2020 limited range (UHD/HDR content).</summary>
    public static readonly ColorMatrix Bt2020Limited = new(ColorSpace.Bt2020, ColorRange.Limited, Kr2020, Kb2020);

    /// <summary>BT.2020 full range.</summary>
    public static readonly ColorMatrix Bt2020Full = new(ColorSpace.Bt2020, ColorRange.Full, Kr2020, Kb2020);

    /// <summary>
    /// Get the standard matrix for a given color space and range combination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ColorMatrix Get(ColorSpace space, ColorRange range)
    {
        if (space == ColorSpace.Bt601)
            return range == ColorRange.Full ? Bt601Full : Bt601Limited;
        if (space == ColorSpace.Bt2020)
            return range == ColorRange.Full ? Bt2020Full : Bt2020Limited;
        // Default: BT.709
        return range == ColorRange.Full ? Bt709Full : Bt709Limited;
    }

    // ITU-R BT.601: Kr = 0.299, Kb = 0.114
    private const double Kr601 = 0.299;
    private const double Kb601 = 0.114;

    // ITU-R BT.709: Kr = 0.2126, Kb = 0.0722
    private const double Kr709 = 0.2126;
    private const double Kb709 = 0.0722;

    // ITU-R BT.2020: Kr = 0.2627, Kb = 0.0593
    private const double Kr2020 = 0.2627;
    private const double Kb2020 = 0.0593;
}

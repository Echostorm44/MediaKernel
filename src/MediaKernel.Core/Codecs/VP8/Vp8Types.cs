// VP8 decoder types and enumerations
// Ported from FFmpeg libavcodec/vp8.h

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.VP8;

// --- Enumerations ---

/// <summary>VP8 reference frame types.</summary>
public enum Vp8FrameType : sbyte
{
    None = -1,
    Current = 0,
    Previous = 1,
    Golden = 2,
    Altref = 3
}

/// <summary>VP8 DCT coefficient token types (12 total, EOB last).</summary>
public enum Vp8DctToken : byte
{
    Dct0 = 0,
    Dct1 = 1,
    Dct2 = 2,
    Dct3 = 3,
    Dct4 = 4,
    DctCat1 = 5,
    DctCat2 = 6,
    DctCat3 = 7,
    DctCat4 = 8,
    DctCat5 = 9,
    DctCat6 = 10,
    DctEob = 11,

    Count = 12
}

/// <summary>VP8 intra 16x16 / chroma prediction modes (uses H.264 naming).</summary>
public enum Vp8Pred16x16Mode : byte
{
    Dc = 0,       // DC_PRED8x8
    Horizontal = 1, // HOR_PRED8x8
    Vertical = 2,   // VERT_PRED8x8
    TrueMotion = 3, // PLANE_PRED8x8 (VP8 uses TM instead of plane)
    I4x4 = 4        // MODE_I4x4 — signals 4x4 sub-block intra prediction
}

/// <summary>VP8 intra 4x4 prediction modes (H.264-style).</summary>
public enum Vp8Pred4x4Mode : byte
{
    Vertical = 0,         // VERT_PRED
    Horizontal = 1,       // HOR_PRED
    Dc = 2,               // DC_PRED
    DiagDownLeft = 3,     // DIAG_DOWN_LEFT_PRED
    DiagDownRight = 4,    // DIAG_DOWN_RIGHT_PRED
    VerticalRight = 5,    // VERT_RIGHT_PRED
    HorizontalDown = 6,   // HOR_DOWN_PRED
    VerticalLeft = 7,     // VERT_LEFT_PRED
    HorizontalUp = 8,     // HOR_UP_PRED
    TrueMotion = 9,       // TM_VP8_PRED
    LeftDc = 10,          // LEFT_DC_PRED (no top neighbors)
    TopDc = 11            // TOP_DC_PRED (no left neighbors)
}

/// <summary>VP8 inter macroblock motion vector modes.</summary>
/// <remarks>
/// Values start at 5 because the mode field shares the same space as
/// Vp8Pred16x16Mode (0=DC, 1=HOR, 2=VERT, 3=TM, 4=I4x4).
/// FFmpeg defines VP8_MVMODE_ZERO = MODE_I4x4 + 1 = 5.
/// </remarks>
public enum Vp8InterMvMode : byte
{
    /// <summary>Zero MV.</summary>
    Zero = 5,
    /// <summary>Non-zero MV (nearest, near, or new).</summary>
    Mv = 6,
    /// <summary>Split MV (sub-block motion).</summary>
    Split = 7
}

/// <summary>VP8 split MV partition modes.</summary>
public enum Vp8SplitMvMode : byte
{
    /// <summary>2 16x8 blocks (top/bottom halves).</summary>
    TopBottom = 0,
    /// <summary>2 8x16 blocks (left/right halves).</summary>
    LeftRight = 1,
    /// <summary>4 8x8 blocks (2x2 grid).</summary>
    Quarters = 2,
    /// <summary>16 4x4 blocks (full split).</summary>
    Full4x4 = 3,
    /// <summary>Sentinel: no split (only used in prediction context).</summary>
    None = 4
}

// --- Structures ---

/// <summary>VP8 motion vector (quarter-pel precision).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8Mv
{
    public short X;
    public short Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vp8Mv(short x, short y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Reinterpret the (X, Y) pair as a single uint32 for fast comparison.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint AsUint() => (uint)(ushort)X | ((uint)(ushort)Y << 16);

    /// <summary>Create a Vp8Mv from a packed uint32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vp8Mv FromUint(uint v) => new((short)(ushort)v, (short)(ushort)(v >> 16));

    public static readonly Vp8Mv Zero = default;
}

/// <summary>VP8 loop filter strength for a macroblock.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8FilterStrength
{
    public byte FilterLevel;
    public byte InnerLimit;
    public byte InnerFilter;
}

/// <summary>VP8 macroblock decoded state.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8Macroblock
{
    public byte Skip;
    public byte Mode;              // Vp8Pred16x16Mode or Vp8InterMvMode+MODE_I4x4
    public byte RefFrame;          // Vp8FrameType cast
    public byte Partitioning;      // Vp8SplitMvMode
    public byte ChromaPredMode;    // Vp8Pred16x16Mode (DC/V/H/TM for chroma)
    public byte Segment;

    // 4x4 intra prediction modes for this MB (16 sub-blocks in raster order)
    public unsafe fixed byte Intra4x4PredMode[16];

    // Top-edge 4x4 intra modes propagated to next row (4 values)
    public unsafe fixed byte Intra4x4PredModeTop[4];

    public Vp8Mv Mv;

    // Per-sub-block MVs for split mode (16 sub-blocks in raster order)
    public unsafe fixed short Bmv[32]; // 16 * (x,y) = 32 shorts
}

/// <summary>VP8 motion vector bounds for clamping.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8MvBounds
{
    public int MinX, MinY;
    public int MaxX, MaxY;
}

/// <summary>VP8 per-segment quantization multipliers.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8QuantMatrix
{
    /// <summary>DC/AC quant for luma (4x4 AC blocks).</summary>
    public short LumaDcQmul, LumaAcQmul;

    /// <summary>DC/AC quant for luma DC-only block (Y2, WHT).</summary>
    public short LumaDcOnlyDcQmul, LumaDcOnlyAcQmul;

    /// <summary>DC/AC quant for chroma.</summary>
    public short ChromaDcQmul, ChromaAcQmul;
}

/// <summary>VP8 raw quantization values from frame header.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp8RawQuant
{
    public int YacQi;
    public int YdcDelta;
    public int Y2dcDelta;
    public int Y2acDelta;
    public int UvdcDelta;
    public int UvacDelta;
}

/// <summary>VP8 per-thread coefficient decode state.</summary>
public sealed class Vp8ThreadData
{
    /// <summary>Decoded DCT coefficients: [6 blocks][4 sub-blocks][16 coeffs].</summary>
    public readonly short[] Block = new short[6 * 4 * 16];

    /// <summary>Luma DC block coefficients for WHT (16 values).</summary>
    public readonly short[] BlockDc = new short[16];

    /// <summary>
    /// Non-zero coefficient count cache: [6 blocks][4 sub-blocks].
    /// 0 = no coeffs, 1 = DC only (special transform), 2+ = full transform.
    /// </summary>
    public readonly byte[] NonZeroCountCache = new byte[6 * 4];

    /// <summary>
    /// Left-neighbor NNZ for coeff decode (9 values: 4 luma + 2 Cb + 2 Cr + 1 luma DC).
    /// </summary>
    public readonly byte[] LeftNnz = new byte[9];

    /// <summary>Edge emulation buffer for motion compensation near frame edges.</summary>
    public readonly byte[] EdgeEmuBuffer = new byte[21 * 32];

    /// <summary>Per-MB filter strengths for this row.</summary>
    public Vp8FilterStrength[]? FilterStrength;

    public Vp8MvBounds MvBounds;
}

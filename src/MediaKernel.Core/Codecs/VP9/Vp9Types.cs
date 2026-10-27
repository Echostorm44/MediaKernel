// VP9 decoder types and enumerations
// Ported from FFmpeg libavcodec/vp9.h, vp9shared.h, vp9dec.h

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.Vp9;

// --- Enumerations ---

/// <summary>VP9 transform sizes.</summary>
public enum Vp9TxSize : byte
{
    Tx4x4 = 0,
    Tx8x8 = 1,
    Tx16x16 = 2,
    Tx32x32 = 3,
    Count = 4,
    Switchable = 4
}

/// <summary>VP9 transform type (DCT/ADST combination). Matches FFmpeg TxfmType enum.</summary>
public enum Vp9TxType : byte
{
    DctDct = 0,
    AdstDct = 1,   // ADST on rows, DCT on columns
    DctAdst = 2,   // DCT on rows, ADST on columns
    AdstAdst = 3,
    Count = 4
}

/// <summary>VP9 intra prediction modes.</summary>
public enum Vp9IntraMode : byte
{
    Vertical = 0,
    Horizontal = 1,
    Dc = 2,
    DiagonalDownLeft = 3,
    DiagonalDownRight = 4,
    VerticalRight = 5,
    HorizontalDown = 6,
    VerticalLeft = 7,
    HorizontalUp = 8,
    TrueMotion = 9,
    LeftDc = 10,
    TopDc = 11,
    Dc128 = 12,
    Dc127 = 13,
    Dc129 = 14,
    Count = 15
}

/// <summary>VP9 interpolation filter modes.</summary>
public enum Vp9FilterMode : byte
{
    EightTapSmooth = 0,
    EightTapRegular = 1,
    EightTapSharp = 2,
    Bilinear = 3,
    Count = 4,
    Switchable = 4
}

/// <summary>VP9 block partition types.</summary>
public enum Vp9Partition : byte
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Split = 3
}

/// <summary>VP9 inter prediction modes.</summary>
public enum Vp9InterMode : byte
{
    NearestMv = 10,
    NearMv = 11,
    ZeroMv = 12,
    NewMv = 13
}

/// <summary>VP9 compound prediction modes.</summary>
public enum Vp9CompPredMode : byte
{
    SingleRef = 0,
    CompRef = 1,
    Switchable = 2
}

/// <summary>VP9 block level in the partition tree (64→32→16→8).</summary>
public enum Vp9BlockLevel : byte
{
    Bl64x64 = 0,
    Bl32x32 = 1,
    Bl16x16 = 2,
    Bl8x8 = 3
}

/// <summary>VP9 block sizes.</summary>
public enum Vp9BlockSize : byte
{
    Bs64x64 = 0,
    Bs64x32 = 1,
    Bs32x64 = 2,
    Bs32x32 = 3,
    Bs32x16 = 4,
    Bs16x32 = 5,
    Bs16x16 = 6,
    Bs16x8 = 7,
    Bs8x16 = 8,
    Bs8x8 = 9,
    Bs8x4 = 10,
    Bs4x8 = 11,
    Bs4x4 = 12,
    Count = 13
}

/// <summary>VP9 reference frame indices.</summary>
public enum Vp9ReferenceFrame : byte
{
    None = 0,
    Last = 1,
    Golden = 2,
    AltRef = 3,
    Count = 4
}

/// <summary>VP9 MV joint types.</summary>
public enum Vp9MvJoint : byte
{
    Zero = 0,
    Horizontal = 1,
    Vertical = 2,
    HorizontalVertical = 3
}

// --- Structures ---

/// <summary>VP9 motion vector (x, y in 1/8 pel).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp9Mv
{
    public short X;
    public short Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint AsUint32() => Unsafe.As<Vp9Mv, uint>(ref Unsafe.AsRef(in this));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Vp9Mv a, Vp9Mv b) => a.AsUint32() == b.AsUint32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Vp9Mv a, Vp9Mv b) => a.AsUint32() != b.AsUint32();

    public override readonly bool Equals(object? obj) => obj is Vp9Mv other && this == other;
    public override readonly int GetHashCode() => (int)AsUint32();
}

/// <summary>MV pair for a block — one per reference frame.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vp9MvRefPair
{
    public Vp9Mv Mv0;
    public Vp9Mv Mv1;
    public sbyte Ref0;
    public sbyte Ref1;
}

/// <summary>Decoded block information.</summary>
public struct Vp9Block
{
    public byte SegmentId;
    public byte Intra;
    public byte Comp;
    public byte Ref0;
    public byte Ref1;
    public byte Mode0;
    public byte Mode1;
    public byte Mode2;
    public byte Mode3;
    public byte UvMode;
    public byte Skip;
    public Vp9FilterMode Filter;
    // mv[4 sub-blocks][2 refs]
    public Vp9Mv Mv0Ref0, Mv0Ref1;
    public Vp9Mv Mv1Ref0, Mv1Ref1;
    public Vp9Mv Mv2Ref0, Mv2Ref1;
    public Vp9Mv Mv3Ref0, Mv3Ref1;
    public Vp9BlockSize BlockSize;
    public Vp9TxSize Tx;
    public Vp9TxSize UvTx;
    public Vp9BlockLevel Level;
    public Vp9Partition Partition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte GetMode(int index) => index switch
    {
        0 => Mode0, 1 => Mode1, 2 => Mode2, _ => Mode3
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMode(int index, byte value)
    {
        switch (index)
        {
            case 0: Mode0 = value; break;
            case 1: Mode1 = value; break;
            case 2: Mode2 = value; break;
            default: Mode3 = value; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vp9Mv GetMv(int subBlock, int refIdx)
    {
        return (subBlock, refIdx) switch
        {
            (0, 0) => Mv0Ref0, (0, 1) => Mv0Ref1,
            (1, 0) => Mv1Ref0, (1, 1) => Mv1Ref1,
            (2, 0) => Mv2Ref0, (2, 1) => Mv2Ref1,
            _ => refIdx == 0 ? Mv3Ref0 : Mv3Ref1
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMv(int subBlock, int refIdx, Vp9Mv mv)
    {
        switch (subBlock)
        {
            case 0: if (refIdx == 0) Mv0Ref0 = mv; else Mv0Ref1 = mv; break;
            case 1: if (refIdx == 0) Mv1Ref0 = mv; else Mv1Ref1 = mv; break;
            case 2: if (refIdx == 0) Mv2Ref0 = mv; else Mv2Ref1 = mv; break;
            default: if (refIdx == 0) Mv3Ref0 = mv; else Mv3Ref1 = mv; break;
        }
    }
}

/// <summary>VP9 probability context for entropy coding.</summary>
public struct Vp9ProbContext
{
    // y_mode[4][9]
    public unsafe fixed byte YMode[4 * 9];
    // uv_mode[10][9]
    public unsafe fixed byte UvMode[10 * 9];
    // filter[4][2]
    public unsafe fixed byte Filter[4 * 2];
    // mv_mode[7][3]
    public unsafe fixed byte MvMode[7 * 3];
    // intra[4]
    public unsafe fixed byte Intra[4];
    // comp[5]
    public unsafe fixed byte Comp[5];
    // single_ref[5][2]
    public unsafe fixed byte SingleRef[5 * 2];
    // comp_ref[5]
    public unsafe fixed byte CompRef[5];
    // tx32p[2][3]
    public unsafe fixed byte Tx32p[2 * 3];
    // tx16p[2][2]
    public unsafe fixed byte Tx16p[2 * 2];
    // tx8p[2]
    public unsafe fixed byte Tx8p[2];
    // skip[3]
    public unsafe fixed byte Skip[3];
    // mv_joint[3]
    public unsafe fixed byte MvJoint[3];
    // partition[4][4][3]
    public unsafe fixed byte Partition[4 * 4 * 3];

    // MV component probabilities (2 components: x and y)
    public Vp9MvCompProb MvComp0;
    public Vp9MvCompProb MvComp1;
}

/// <summary>MV component probability set.</summary>
public struct Vp9MvCompProb
{
    public byte Sign;
    public unsafe fixed byte Classes[10];
    public byte Class0;
    public unsafe fixed byte Bits[10];
    // class0_fp[2][3]
    public unsafe fixed byte Class0Fp[2 * 3];
    // fp[3]
    public unsafe fixed byte Fp[3];
    public byte Class0Hp;
    public byte Hp;
}

/// <summary>Segmentation feature set for one segment.</summary>
public struct Vp9SegmentFeatures
{
    public byte QEnabled;
    public byte LfEnabled;
    public byte RefEnabled;
    public byte SkipEnabled;
    public byte RefVal;
    public short QVal;
    public sbyte LfVal;
    // qmul[2 planes][2 ac/dc]
    public short QmulYDc, QmulYAc;
    public short QmulUvDc, QmulUvAc;
    // lflvl[4 ref][2 mode] — expanded to named fields to avoid unsafe
    public byte LflvlRef0Mode0, LflvlRef0Mode1;
    public byte LflvlRef1Mode0, LflvlRef1Mode1;
    public byte LflvlRef2Mode0, LflvlRef2Mode1;
    public byte LflvlRef3Mode0, LflvlRef3Mode1;

    /// <summary>Look up loop filter level: lflvl[refIdx][modeIdx].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte GetLflvl(int refIdx, int modeIdx)
    {
        return (refIdx, modeIdx) switch
        {
            (0, 0) => LflvlRef0Mode0, (0, _) => LflvlRef0Mode1,
            (1, 0) => LflvlRef1Mode0, (1, _) => LflvlRef1Mode1,
            (2, 0) => LflvlRef2Mode0, (2, _) => LflvlRef2Mode1,
            (3, 0) => LflvlRef3Mode0, _ => LflvlRef3Mode1,
        };
    }
}

/// <summary>VP9 segmentation state.</summary>
public struct Vp9Segmentation
{
    public byte Enabled;
    public byte Temporal;
    public byte AbsoluteVals;
    public byte UpdateMap;

    // Segmentation tree probabilities (7 nodes) and temporal prediction probs (3 contexts)
    private byte prob0, prob1, prob2, prob3, prob4, prob5, prob6;
    private byte predProb0, predProb1, predProb2;

    /// <summary>Get segmentation tree probability span (7 elements).</summary>
    public Span<byte> Prob
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref prob0, 7);
    }

    /// <summary>Get temporal prediction probability span (3 elements).</summary>
    public Span<byte> PredProb
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref predProb0, 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetProb(int index, byte value) { Prob[index] = value; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPredProb(int index, byte value) { PredProb[index] = value; }

    // 8 segment features
    public Vp9SegmentFeatures Feat0, Feat1, Feat2, Feat3;
    public Vp9SegmentFeatures Feat4, Feat5, Feat6, Feat7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref readonly Vp9SegmentFeatures GetFeat(int index)
    {
        // Unsafe but necessary for perf — all 8 segments are laid out contiguously
        return ref Unsafe.Add(ref Unsafe.AsRef(in Feat0), index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Vp9SegmentFeatures GetFeatMut(int index)
    {
        return ref Unsafe.Add(ref Unsafe.AsRef(ref Feat0), index);
    }
}

/// <summary>VP9 bitstream header (parsed from uncompressed + compressed headers).</summary>
public struct Vp9BitstreamHeader
{
    public byte Profile;
    public byte Bpp;
    public byte Keyframe;
    public byte Invisible;
    public byte ErrorRes;
    public byte IntraOnly;
    public byte ResetCtx;
    public byte RefreshRefMask;
    public byte HighPrecisionMvs;
    public byte FilterMode; // 0-3 = specific filter, 4 = switchable
    public byte AllowCompInter;
    public byte RefreshCtx;
    public byte ParallelMode;
    public byte FrameCtxId;
    public byte UseLastFrameMvs;
    public byte RefIdx0, RefIdx1, RefIdx2;
    public byte SignBias0, SignBias1, SignBias2;
    public byte FixCompRef;
    public byte VarCompRef0, VarCompRef1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte GetSignBias(int refFrame) => refFrame switch
    {
        0 => SignBias0, 1 => SignBias1, _ => SignBias2
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte GetRefIdx(int refFrame) => refFrame switch
    {
        0 => RefIdx0, 1 => RefIdx1, _ => RefIdx2
    };

    // Loop filter
    public byte FilterLevel;
    public byte Sharpness;
    public byte LfDeltaEnabled;
    public byte LfDeltaUpdated;
    public sbyte LfDeltaRef0, LfDeltaRef1, LfDeltaRef2, LfDeltaRef3;
    public sbyte LfDeltaMode0, LfDeltaMode1;

    // Quantization
    public byte YacQi;
    public sbyte YdcQdelta, UvdcQdelta, UvacQdelta;
    public byte Lossless;

    // Segmentation
    public Vp9Segmentation Segmentation;

    // Transform mode (0=4x4, 1=8x8, 2=16x16, 3=32x32, 4=switchable)
    public byte TxfmMode;

    // Compound prediction mode (0=single, 1=comp, 2=switchable)
    public byte CompPredMode;

    // Tiling
    public byte Log2TileCols, Log2TileRows;
    public ushort TileCols, TileRows;

    public ushort UncompressedHeaderSize;
    public ushort CompressedHeaderSize;
}

/// <summary>VP9 reference frame buffer.</summary>
public class Vp9ReferenceFrameBuffer
{
    public byte[]? LumaData;
    public byte[]? ChromaUData;
    public byte[]? ChromaVData;
    public int Width;
    public int Height;
    public int LumaStride;
    public int ChromaStride;
    public byte[]? SegmentationMap;
    public Vp9MvRefPair[]? Mvs;
    public bool Valid;

    /// <summary>
    /// Where <see cref="SegmentationMap"/> and <see cref="Mvs"/> are parked when this reference
    /// stops carrying temporal data.
    /// </summary>
    /// <remarks>
    /// Null on those two fields means "no temporal data here", and callers test for it, so the
    /// null cannot be replaced by an empty array or a length of zero. Assigning null outright
    /// drops the buffer, though, and the next frame that does carry data has to allocate a fresh
    /// one - on an intra frame followed by inter frames that is a full-size map every few frames.
    /// Parking the array here keeps the null visible to callers and the buffer available to reuse.
    /// </remarks>
    internal byte[]? SpareSegmentationMap;
    internal Vp9MvRefPair[]? SpareMvs;
}

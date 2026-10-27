// VP9 decoder context — state management, reference frames, context arrays
// Ported from FFmpeg libavcodec/vp9dec.h (VP9Context + VP9TileData)

using System.Buffers;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>VP9 probability context: frame-level probabilities for arithmetic coding.</summary>
/// <remarks>
/// Mirrors FFmpeg's ProbContext struct. Stored as flat arrays for cache-friendly access.
/// Each prob_ctx[i] stores base probabilities + coefficient probs.
/// </remarks>
public class Vp9ProbabilityContext
{
    // Base probabilities (ProbContext)
    public readonly byte[] YMode = new byte[4 * 9];          // [4][9]
    public readonly byte[] UvMode = new byte[10 * 9];         // [10][9]
    public readonly byte[] Filter = new byte[4 * 2];          // [4][2]
    public readonly byte[] MvMode = new byte[7 * 3];          // [7][3]
    public readonly byte[] Intra = new byte[4];               // [4]
    public readonly byte[] Comp = new byte[5];                // [5]
    public readonly byte[] SingleRef = new byte[5 * 2];       // [5][2]
    public readonly byte[] CompRef = new byte[5];             // [5]
    public readonly byte[] Tx32p = new byte[2 * 3];           // [2][3]
    public readonly byte[] Tx16p = new byte[2 * 2];           // [2][2]
    public readonly byte[] Tx8p = new byte[2];                // [2]
    public readonly byte[] Skip = new byte[3];                // [3]
    public readonly byte[] MvJoint = new byte[3];             // [3]
    public readonly byte[] MvComp = new byte[2 * 33];         // [2] × {sign,classes[10],class0,bits[10],class0_fp[2][3],fp[3],class0_hp,hp}
    public readonly byte[] Partition = new byte[4 * 4 * 3];   // [4][4][3]

    // Coefficient probabilities — stored separately per prob_ctx
    public readonly byte[] Coef = new byte[4 * 2 * 2 * 6 * 6 * 3]; // [4][2][2][6][6][3]

    /// <summary>Reset all probabilities to FFmpeg default values.</summary>
    public void ResetToDefaults()
    {
        Vp9Data.DefaultYModeProbs.CopyTo(YMode);
        Vp9Data.DefaultUvModeProbs.CopyTo(UvMode);
        Vp9Data.DefaultFilterProbs.CopyTo(Filter);
        Vp9Data.DefaultMvModeProbs.CopyTo(MvMode);
        Vp9Data.DefaultIntraProbs.CopyTo(Intra);
        Vp9Data.DefaultCompProbs.CopyTo(Comp);
        Vp9Data.DefaultSingleRefProbs.CopyTo(SingleRef);
        Vp9Data.DefaultCompRefProbs.CopyTo(CompRef);
        Vp9Data.DefaultTx32pProbs.CopyTo(Tx32p);
        Vp9Data.DefaultTx16pProbs.CopyTo(Tx16p);
        Vp9Data.DefaultTx8pProbs.CopyTo(Tx8p);
        Vp9Data.DefaultSkipProbs.CopyTo(Skip);
        Vp9Data.DefaultMvJointProbs.CopyTo(MvJoint);
        Vp9Data.DefaultMvCompProbs.CopyTo(MvComp);
        Vp9Data.DefaultPartitionProbs.CopyTo(Partition);
        Vp9Data.DefaultCoefProbs.CopyTo(Coef);
    }

    /// <summary>Copy all probabilities from another context.</summary>
    public void CopyFrom(Vp9ProbabilityContext other)
    {
        other.YMode.AsSpan().CopyTo(YMode);
        other.UvMode.AsSpan().CopyTo(UvMode);
        other.Filter.AsSpan().CopyTo(Filter);
        other.MvMode.AsSpan().CopyTo(MvMode);
        other.Intra.AsSpan().CopyTo(Intra);
        other.Comp.AsSpan().CopyTo(Comp);
        other.SingleRef.AsSpan().CopyTo(SingleRef);
        other.CompRef.AsSpan().CopyTo(CompRef);
        other.Tx32p.AsSpan().CopyTo(Tx32p);
        other.Tx16p.AsSpan().CopyTo(Tx16p);
        other.Tx8p.AsSpan().CopyTo(Tx8p);
        other.Skip.AsSpan().CopyTo(Skip);
        other.MvJoint.AsSpan().CopyTo(MvJoint);
        other.MvComp.AsSpan().CopyTo(MvComp);
        other.Partition.AsSpan().CopyTo(Partition);
        other.Coef.AsSpan().CopyTo(Coef);
    }
}

/// <summary>
/// VP9 adaptive count accumulators — collected during frame decode, used for probability adaptation.
/// </summary>
/// <remarks>
/// Mirrors FFmpeg's VP9TileData.counts. Each entry counts occurrences of symbol choices.
/// After frame decode, these are used to adapt probabilities for the next frame.
/// </remarks>
public class Vp9AdaptiveCounts
{
    public readonly uint[] YMode = new uint[4 * 10];
    public readonly uint[] UvMode = new uint[10 * 10];
    public readonly uint[] Filter = new uint[4 * 3];
    public readonly uint[] MvMode = new uint[7 * 4];
    public readonly uint[] Intra = new uint[4 * 2];
    public readonly uint[] Comp = new uint[5 * 2];
    public readonly uint[] SingleRef = new uint[5 * 2 * 2];
    public readonly uint[] CompRef = new uint[5 * 2];
    public readonly uint[] Tx32p = new uint[2 * 4];
    public readonly uint[] Tx16p = new uint[2 * 3];
    public readonly uint[] Tx8p = new uint[2 * 2];
    public readonly uint[] Skip = new uint[3 * 2];
    public readonly uint[] MvJoint = new uint[4];
    public readonly uint[] MvSign = new uint[2 * 2];
    public readonly uint[] MvClasses = new uint[2 * 11];
    public readonly uint[] MvClass0 = new uint[2 * 2];
    public readonly uint[] MvBits = new uint[2 * 10 * 2];
    public readonly uint[] MvClass0Fp = new uint[2 * 2 * 4];
    public readonly uint[] MvFp = new uint[2 * 4];
    public readonly uint[] MvClass0Hp = new uint[2 * 2];
    public readonly uint[] MvHp = new uint[2 * 2];
    public readonly uint[] Partition = new uint[4 * 4 * 4];
    public readonly uint[] Coef = new uint[4 * 2 * 2 * 6 * 6 * 3];
    public readonly uint[] Eob = new uint[4 * 2 * 2 * 6 * 6 * 2];

    public void Clear()
    {
        Array.Clear(YMode);
        Array.Clear(UvMode);
        Array.Clear(Filter);
        Array.Clear(MvMode);
        Array.Clear(Intra);
        Array.Clear(Comp);
        Array.Clear(SingleRef);
        Array.Clear(CompRef);
        Array.Clear(Tx32p);
        Array.Clear(Tx16p);
        Array.Clear(Tx8p);
        Array.Clear(Skip);
        Array.Clear(MvJoint);
        Array.Clear(MvSign);
        Array.Clear(MvClasses);
        Array.Clear(MvClass0);
        Array.Clear(MvBits);
        Array.Clear(MvClass0Fp);
        Array.Clear(MvFp);
        Array.Clear(MvClass0Hp);
        Array.Clear(MvHp);
        Array.Clear(Partition);
        Array.Clear(Coef);
        Array.Clear(Eob);
    }
}

/// <summary>VP9 decoder context — manages decoder state, reference frames, and context arrays.</summary>
/// <remarks>
/// Mirrors FFmpeg's VP9Context + VP9TileData. Single-threaded for now.
/// </remarks>
public sealed class Vp9DecoderContext : IDisposable
{
    // Frame dimensions
    public int Width;
    public int Height;
    public int SuperblockCols;
    public int SuperblockRows;
    public int Rows;  // in 8-pel units
    public int Cols;  // in 8-pel units

    // Subsampling
    /// <summary>
    /// <c>s-&gt;pix_fmt</c>, which read_colorspace_details picks from the bit depth, the
    /// subsampling and whether the colour space is RGB. Nothing read it before: the frame
    /// extraction assumed 4:2:0 eight-bit, so a 4:4:4 stream was handed out as a quarter of
    /// each chroma plane with the wrong geometry on it.
    /// </summary>
    public PixelFormat OutputPixelFormat = PixelFormat.Yuv420P;

    private int lastSubsamplingX = -1;
    private int lastSubsamplingY = -1;
    private int lastBytesPerPixel = -1;

    public byte SubsamplingX;
    public byte SubsamplingY;
    public byte BytesPerPixel;
    public byte BitsPerComponent;

    // Bitstream header for current frame
    public Vp9BitstreamHeader Header;

    // Probability contexts — 4 slots as per spec
    public readonly Vp9ProbabilityContext[] ProbCtx = new Vp9ProbabilityContext[4];

    // Active frame probabilities (expanded from 3-value coef probs to 11 values)
    public readonly Vp9ProbabilityContext ActiveProb = new();

    // Expanded coefficient probabilities [4][2][2][6][6][11]
    public readonly byte[] CoefProbs = new byte[4 * 2 * 2 * 6 * 6 * 11];

    // Adaptive counts for current frame
    public readonly Vp9AdaptiveCounts Counts = new();

    // Loop filter LUT
    public readonly Vp9FilterLut FilterLut = new();

    // Reference frame buffers (8 slots)
    public readonly Vp9ReferenceFrameBuffer[] Refs = new Vp9ReferenceFrameBuffer[8];

    // Current frame + reference working copies
    public readonly Vp9ReferenceFrameBuffer[] Frames = new Vp9ReferenceFrameBuffer[4];
    public const int CurFrame = 0;
    public const int RefFrameMvPair = 1;
    public const int RefFrameSegMap = 2;
    public const int BlankFrame = 3;

    // MV scaling for reference frames
    public readonly ushort[] MvScale = new ushort[3 * 2];  // [3][2] — 0xFFFF = invalid
    public readonly byte[] MvStep = new byte[3 * 2];       // [3][2]

    // Above context arrays (allocated per superblock-column)
    public byte[]? AbovePartitionCtx;
    public byte[]? AboveModeCtx;
    public byte[]? AboveYNnzCtx;
    public byte[]? AboveUvNnzCtx0;
    public byte[]? AboveUvNnzCtx1;
    public byte[]? AboveSkipCtx;
    public byte[]? AboveTxfmCtx;
    public byte[]? AboveSegpredCtx;
    public byte[]? AboveIntraCtx;
    public byte[]? AboveCompCtx;
    public byte[]? AboveRefCtx;
    public byte[]? AboveFilterCtx;
    public Vp9Mv[]? AboveMvCtx;  // [cols*8][2] flat

    // Left context arrays (per tile decode, sized for max superblock height = 8 blocks)
    public readonly byte[] LeftPartitionCtx = new byte[8];
    public readonly byte[] LeftModeCtx = new byte[16];
    public readonly byte[] LeftYNnzCtx = new byte[16];
    public readonly byte[] LeftUvNnzCtx0 = new byte[16];
    public readonly byte[] LeftUvNnzCtx1 = new byte[16];
    public readonly byte[] LeftSkipCtx = new byte[8];
    public readonly byte[] LeftTxfmCtx = new byte[8];
    public readonly byte[] LeftSegpredCtx = new byte[8];
    public readonly byte[] LeftIntraCtx = new byte[8];
    public readonly byte[] LeftCompCtx = new byte[8];
    public readonly byte[] LeftRefCtx = new byte[8];
    public readonly byte[] LeftFilterCtx = new byte[8];
    public readonly Vp9Mv[] LeftMvCtx = new Vp9Mv[16 * 2];

    // Intra prediction edge data (row above current superblock row, per plane)
    public byte[]? IntraPredDataY;
    public byte[]? IntraPredDataU;
    public byte[]? IntraPredDataV;

    // Temporary diagnostic counter
    public int DiagBlockCount;
    public bool DiagCoefLog;

    // Loop filter level data per superblock
    public Vp9Filter[]? LoopFilterLevels;

    // Filter lookup tables
    public readonly byte[] LimLut = new byte[64];
    public readonly byte[] MbLimLut = new byte[64];

    // Block decode temporaries
    public short[]? BlockBase;
    public short[]? UvBlockBase0;
    public short[]? UvBlockBase1;
    public byte[]? EobBase;
    public byte[]? UvEobBase0;
    public byte[]? UvEobBase1;
    public byte[]? EdgeEmuBuffer;

    // Tile decode temporaries
    public readonly byte[] TmpY = new byte[64 * 64 * 2];
    public readonly byte[] TmpUv0 = new byte[64 * 64 * 2];
    public readonly byte[] TmpUv1 = new byte[64 * 64 * 2];

    // Current block state
    public Vp9Block CurrentBlock;
    public int CurrentRow;
    public int CurrentCol;
    public int TileColStart;  // Left boundary of current tile column (in 8-pel units)
    public Vp9BoolDecoder ActiveBoolDecoder;
    public int CurrentLflvlIdx; // Loop filter level index for current superblock

    // Reconstruction destination pointers (set per-block, may point to frame or temp buffer)
    public byte[] DstY = Array.Empty<byte>();
    public byte[] DstU = Array.Empty<byte>();
    public byte[] DstV = Array.Empty<byte>();
    public int DstYOffset;
    public int DstUOffset;
    public int DstVOffset;
    public int YStride;
    public int UvStride;

    // MV clamping bounds (computed per-block)
    public Vp9Mv MinMv;
    public Vp9Mv MaxMv;

    // Previous frame state
    public byte LastKeyframe;
    public byte LastBpp;

    public Vp9DecoderContext()
    {
        for (int i = 0; i < 4; i++)
            ProbCtx[i] = new Vp9ProbabilityContext();

        for (int i = 0; i < 8; i++)
            Refs[i] = new Vp9ReferenceFrameBuffer();

        for (int i = 0; i < 4; i++)
            Frames[i] = new Vp9ReferenceFrameBuffer();

        // Initialize all probability contexts to defaults
        for (int i = 0; i < 4; i++)
            ProbCtx[i].ResetToDefaults();
    }

    /// <summary>Allocate or reallocate context arrays for the current frame dimensions.</summary>
    public void AllocateContextArrays()
    {
        int sbCols = (Width + 63) >> 6;
        int sbRows = (Height + 63) >> 6;
        int cols8 = (Width + 7) >> 3;
        int rows8 = (Height + 7) >> 3;

        // The pixel format is part of the condition, as it is in update_size's guard
        // (`s->pix_fmt == s->last_fmt`): a stream that changes subsampling or bit depth
        // without changing its dimensions needs these arrays rebuilt.
        if (cols8 != Cols || rows8 != Rows
            || SubsamplingX != lastSubsamplingX || SubsamplingY != lastSubsamplingY
            || BytesPerPixel != lastBytesPerPixel)
        {
            lastSubsamplingX = SubsamplingX;
            lastSubsamplingY = SubsamplingY;
            lastBytesPerPixel = BytesPerPixel;

            Cols = cols8;
            Rows = rows8;
            SuperblockCols = sbCols;
            SuperblockRows = sbRows;

            // Above context arrays: match FFmpeg allocation (sb_cols * N)
            // 4x4-unit arrays: sb_cols * 16 entries (one per 4-pel column)
            // 8x8-unit arrays: sb_cols * 8 entries (one per 8-pel column)
            int above4 = sbCols * 16;  // total 4x4 columns
            int above8 = sbCols * 8;   // total 8x8 columns
            AbovePartitionCtx = new byte[above8];
            AboveModeCtx = new byte[above4];
            AboveYNnzCtx = new byte[above4]; // one per 4x4 column

            // 16 per superblock column, NOT 8 - the same as the luma array, and
            // deliberately so. update_size assigns all three at 16 with the comment
            // "we slightly over-allocate here for subsampled chroma, but a little bit of
            // padding shouldn't affect performance", and the padding is what 4:4:4 needs:
            // SPLAT_ZERO_YUV writes at `col << !ss_h` for `w4 << !ss_h` entries, so with
            // ss_h = 0 the chroma arrays are indexed exactly like the luma one. Allocating
            // the subsampled size instead - which is what this did - is an
            // IndexOutOfRangeException on the first 4:4:4 frame, and VP9's own FATE streams
            // are all 4:2:0.
            AboveUvNnzCtx0 = new byte[above4];
            AboveUvNnzCtx1 = new byte[above4];
            AboveSkipCtx = new byte[above8];
            AboveTxfmCtx = new byte[above8];
            AboveSegpredCtx = new byte[above8];
            AboveIntraCtx = new byte[above8];
            AboveCompCtx = new byte[above8];
            AboveRefCtx = new byte[above8];
            AboveFilterCtx = new byte[above8];
            AboveMvCtx = new Vp9Mv[above4 * 2]; // 2 MVs per 4x4 column

            // Intra prediction data: one row of pixels above current superblock row
            int pixCols = sbCols * 64;
            // All three at the FULL width, for the same reason and from the same three
            // lines of update_size: intra_pred_data[1] and [2] are assigned
            // 64 * bytesperpixel per superblock column with no shift by ss_h.
            IntraPredDataY = new byte[pixCols * BytesPerPixel + 16];
            IntraPredDataU = new byte[pixCols * BytesPerPixel + 16];
            IntraPredDataV = new byte[pixCols * BytesPerPixel + 16];

            // Loop filter levels per superblock
            LoopFilterLevels = new Vp9Filter[sbCols];
            for (int i = 0; i < sbCols; i++)
                LoopFilterLevels[i] = new Vp9Filter();

            // Block coefficient storage (max 32x32 = 1024 coefficients per block)
            int blockCount = sbCols * 64;  // max blocks per row
            BlockBase = new short[blockCount * 64];
            UvBlockBase0 = new short[blockCount * 64];
            UvBlockBase1 = new short[blockCount * 64];
            EobBase = new byte[blockCount];
            UvEobBase0 = new byte[blockCount];
            UvEobBase1 = new byte[blockCount];
            EdgeEmuBuffer = new byte[135 * 144 * 2];
        }
    }

    /// <summary>Allocate a frame buffer for the given dimensions.</summary>
    public static void AllocateFrameBuffer(Vp9ReferenceFrameBuffer frame, int width, int height, byte ssX, byte ssY)
    {
        int lumaStride = (width + 63) & ~63;  // 64-byte aligned
        int lumaSize = lumaStride * ((height + 63) & ~63);
        int chromaW = (width + (1 << ssX) - 1) >> ssX;
        int chromaH = (height + (1 << ssY) - 1) >> ssY;
        int chromaStride = (chromaW + 63) & ~63;
        int chromaSize = chromaStride * ((chromaH + 63) & ~63);
        int sbCols = (width + 63) >> 6;
        int sbRows = (height + 63) >> 6;
        int sbCols8 = sbCols * 8; // stride in 8x8 units (matches FFmpeg)
        // FFmpeg: sz = 64 * sb_cols * sb_rows — covers the full SB grid
        // Must use sbRows*8 (not rows8) because blocks in the last SB row
        // can extend beyond the actual frame boundary.
        int mapSize = sbCols8 * sbRows * 8;

        frame.Width = width;
        frame.Height = height;
        frame.LumaStride = lumaStride;
        frame.ChromaStride = chromaStride;
        // Reused rather than reallocated. This runs for every frame, and allocating five arrays a
        // frame - one of them the whole luma plane - was 0.56 MiB per frame and four full
        // collections per hundred frames. Behaviour is unchanged: new gives zeroed memory, and the
        // reuse path clears the region that will be read, which is the same zeroing minus the
        // allocation and the collection that follows it.
        frame.LumaData = ReuseOrAllocate(frame.LumaData, lumaSize);
        frame.ChromaUData = ReuseOrAllocate(frame.ChromaUData, chromaSize);
        frame.ChromaVData = ReuseOrAllocate(frame.ChromaVData, chromaSize);
        frame.SegmentationMap = ReuseOrAllocate(frame.SegmentationMap, mapSize);
        frame.Mvs = ReuseOrAllocate(frame.Mvs, mapSize);
        frame.Valid = true;
    }

    /// <summary>
    /// Returns <paramref name="existing"/> cleared for the first <paramref name="length"/> elements
    /// when it is big enough, or a new array when it is not.
    /// </summary>
    /// <remarks>
    /// Nothing reads past <paramref name="length"/>, so clearing only that much matches what a
    /// fresh allocation would have given the caller.
    /// </remarks>
    private static T[] ReuseOrAllocate<T>(T[]? existing, int length)
    {
        if (existing is null || existing.Length < length)
            return new T[length];

        Array.Clear(existing, 0, length);
        return existing;
    }

    /// <summary>Reset left context arrays for a new superblock row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetLeftContext()
    {
        Array.Clear(LeftPartitionCtx);
        Array.Clear(LeftModeCtx);
        Array.Clear(LeftYNnzCtx);
        Array.Clear(LeftUvNnzCtx0);
        Array.Clear(LeftUvNnzCtx1);
        Array.Clear(LeftSkipCtx);
        Array.Clear(LeftTxfmCtx);
        Array.Clear(LeftSegpredCtx);
        Array.Clear(LeftIntraCtx);
        Array.Clear(LeftCompCtx);
        Array.Clear(LeftRefCtx);
        Array.Clear(LeftFilterCtx);
        Array.Clear(LeftMvCtx);
    }

    /// <summary>Build loop filter lookup tables from the filter level and sharpness.</summary>
    public void BuildFilterLookupTables(int filterLevel, int sharpness)
    {
        for (int i = 0; i < 64; i++)
        {
            int limit = i;
            if (sharpness > 0)
            {
                limit >>= (sharpness + 3) >> 2;
                limit = Math.Min(limit, 9 - sharpness);
            }
            limit = Math.Max(limit, 1);

            LimLut[i] = (byte)limit;
            MbLimLut[i] = (byte)(2 * (i + 2) + limit);
        }
    }

    /// <summary>
    /// Expand 3-value coefficient probabilities to 11 values using the Pareto model.
    /// </summary>
    /// <remarks>
    /// The compressed header stores 3 probs per coefficient position.
    /// These are expanded to 11 probs using the model_pareto8 table.
    /// FFmpeg: vp9.c, line ~470: for (k = 0; k < 3; k++) { ... coef[..][k] = ... }
    /// </remarks>
    public void ExpandCoefProbs()
    {
        ReadOnlySpan<byte> coef3 = ActiveProb.Coef;
        Span<byte> coef11 = CoefProbs;

        // For each position in [4][2][2][6][6]: expand 3 probs to 11
        int positions = 4 * 2 * 2 * 6 * 6;
        for (int i = 0; i < positions; i++)
        {
            int src = i * 3;
            int dst = i * 11;

            byte p0 = coef3[src];
            byte p1 = coef3[src + 1];
            byte p2 = coef3[src + 2];

            // First 3 are the original probabilities
            coef11[dst] = p0;
            coef11[dst + 1] = p1;
            coef11[dst + 2] = p2;

            // Next 8 are derived from the pareto8 model using p2 as index
            ReadOnlySpan<byte> pareto = Vp9Data.ModelPareto8.Slice(p2 * 8, 8);
            coef11[dst + 3] = pareto[0];
            coef11[dst + 4] = pareto[1];
            coef11[dst + 5] = pareto[2];
            coef11[dst + 6] = pareto[3];
            coef11[dst + 7] = pareto[4];
            coef11[dst + 8] = pareto[5];
            coef11[dst + 9] = pareto[6];
            coef11[dst + 10] = pareto[7];
        }
    }

    public void Reset()
    {
        // Reset decoder state (e.g., after seek)
        LastKeyframe = 0;
        LastBpp = 0;
        for (int i = 0; i < 8; i++)
        {
            Refs[i] = new Vp9ReferenceFrameBuffer();
        }
        for (int i = 0; i < 4; i++)
        {
            ProbCtx[i] = new Vp9ProbabilityContext();
            ProbCtx[i].ResetToDefaults();
        }
    }

    public void Dispose()
    {
        // Nothing pooled yet — all managed arrays will be GC'd
    }
}

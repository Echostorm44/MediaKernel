// VP8 decoder context — frame state, probability tables, reference frames
// Ported from FFmpeg libavcodec/vp8.h (VP8Context)

using System.Buffers;

namespace MediaKernel.Core.Codecs.VP8;

/// <summary>
/// VP8 probability state — all updatable probabilities for binary decisions.
/// Persists across inter-frames (only implicitly reset on keyframes).
/// </summary>
public sealed class Vp8Probabilities
{
    /// <summary>Segmentation ID probabilities: [3 tree nodes].</summary>
    public readonly byte[] SegmentId = new byte[3];

    /// <summary>Macroblock skip probability.</summary>
    public byte MbSkip;

    /// <summary>Intra prediction probability (inter frames: prob of being intra).</summary>
    public byte Intra;

    /// <summary>Last reference frame probability.</summary>
    public byte Last;

    /// <summary>Golden reference frame probability.</summary>
    public byte Golden;

    /// <summary>16x16 prediction mode probabilities: [4 tree nodes].</summary>
    public readonly byte[] Pred16x16 = new byte[4];

    /// <summary>Chroma prediction mode probabilities: [3 tree nodes].</summary>
    public readonly byte[] Pred8x8c = new byte[3];

    /// <summary>
    /// Coefficient token probabilities: [4 coeff types][16 coeff positions][3 contexts][11 tokens].
    /// VP8 has 4 types: Y2(DC), Y(AC), U, V.
    /// The 16-position dimension maps to 8 bands via Vp8Data.CoeffBand.
    /// </summary>
    public readonly byte[,,,] Token = new byte[4, 16, 3, Vp8Data.NumDctTokens - 1];

    /// <summary>Motion vector component probabilities: [2 components (y,x)][19 probs].</summary>
    public readonly byte[,] Mvc = new byte[2, 19];

    /// <summary>
    /// Custom coefficient scan order (16 positions).
    /// Updated per-frame based on coefficient band statistics.
    /// </summary>
    public readonly byte[] Scan = new byte[16];

    /// <summary>Reset all probabilities to keyframe defaults.</summary>
    public void ResetToKeyframeDefaults()
    {
        // Segment probabilities default to 255
        SegmentId[0] = SegmentId[1] = SegmentId[2] = 255;

        MbSkip = 0;
        Intra = 0;
        Last = 0;
        Golden = 0;

        Vp8Data.Pred16x16ProbIntra.CopyTo(Pred16x16);
        Vp8Data.Pred8x8cProbIntra.CopyTo(Pred8x8c);

        // Copy default token probabilities using band mapping
        // Default probs are [4][8 bands][3][11]; runtime is [4][16 positions][3][11]
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 16; j++)
            {
                int band = Vp8Data.CoeffBand[j];
                for (int k = 0; k < 3; k++)
                    for (int l = 0; l < Vp8Data.NumDctTokens - 1; l++)
                        Token[i, j, k, l] = Vp8Data.TokenDefaultProbs[i, band, k, l];
            }

        // Copy default MV probabilities
        Buffer.BlockCopy(Vp8Data.MvDefaultProb, 0, Mvc, 0, 2 * 19);

        // Default scan order (zigzag)
        Vp8Data.DefaultScanOrder.CopyTo(Scan);
    }

    /// <summary>Copy all probabilities from another instance.</summary>
    public void CopyFrom(Vp8Probabilities other)
    {
        other.SegmentId.AsSpan().CopyTo(SegmentId);
        MbSkip = other.MbSkip;
        Intra = other.Intra;
        Last = other.Last;
        Golden = other.Golden;
        other.Pred16x16.AsSpan().CopyTo(Pred16x16);
        other.Pred8x8c.AsSpan().CopyTo(Pred8x8c);
        Buffer.BlockCopy(other.Token, 0, Token, 0, 4 * 16 * 3 * (Vp8Data.NumDctTokens - 1));
        Buffer.BlockCopy(other.Mvc, 0, Mvc, 0, 2 * 19);
        other.Scan.AsSpan().CopyTo(Scan);
    }
}

/// <summary>
/// VP8 decoded frame — holds pixel data and reference frame metadata.
/// </summary>
public sealed class Vp8Frame : IDisposable
{
    /// <summary>Luma plane (Y), stride = Linesize.</summary>
    public byte[]? DataY;

    /// <summary>Chroma Cb plane, stride = UvLinesize.</summary>
    public byte[]? DataCb;

    /// <summary>Chroma Cr plane, stride = UvLinesize.</summary>
    public byte[]? DataCr;

    public int Linesize;
    public int UvLinesize;
    public int Width;
    public int Height;

    /// <summary>Per-macroblock segment map (for segmentation persistence).</summary>
    public byte[]? SegMap;

    public bool IsAllocated => DataY != null;

    private byte[]? rentedY, rentedCb, rentedCr;

    public void Allocate(int width, int height)
    {
        Width = width;
        Height = height;

        // Pad to MB boundary (16-pixel alignment) + extra border for MC
        int mbWidth = (width + 15) / 16;
        int mbHeight = (height + 15) / 16;
        int alignedW = mbWidth * 16;
        int alignedH = mbHeight * 16;

        // Add border for motion compensation edge emulation
        const int border = 32;
        Linesize = alignedW + 2 * border;
        UvLinesize = (alignedW / 2) + 2 * (border / 2);

        int ySize = Linesize * (alignedH + 2 * border);
        int uvSize = UvLinesize * (alignedH / 2 + 2 * (border / 2));

        rentedY = ArrayPool<byte>.Shared.Rent(ySize);
        rentedCb = ArrayPool<byte>.Shared.Rent(uvSize);
        rentedCr = ArrayPool<byte>.Shared.Rent(uvSize);

        // Set data pointers past the border
        DataY = rentedY;
        DataCb = rentedCb;
        DataCr = rentedCr;

        SegMap = new byte[mbWidth * mbHeight];
    }

    /// <summary>Byte offset to first valid pixel (past border) in luma plane.</summary>
    public int YOffset => 32 * Linesize + 32;

    /// <summary>Byte offset to first valid pixel (past border) in chroma plane.</summary>
    public int UvOffset => 16 * UvLinesize + 16;

    public void Dispose()
    {
        if (rentedY != null) { ArrayPool<byte>.Shared.Return(rentedY); rentedY = null; DataY = null; }
        if (rentedCb != null) { ArrayPool<byte>.Shared.Return(rentedCb); rentedCb = null; DataCb = null; }
        if (rentedCr != null) { ArrayPool<byte>.Shared.Return(rentedCr); rentedCr = null; DataCr = null; }
    }
}

/// <summary>
/// VP8 decoder context — holds all frame-level state for decoding.
/// Mirrors FFmpeg's VP8Context struct.
/// </summary>
public sealed class Vp8DecoderContext : IDisposable
{
    // --- Frame dimensions ---
    public int Width;
    public int Height;
    public int MbWidth;     // Number of horizontal macroblocks
    public int MbHeight;    // Number of vertical macroblocks

    // --- Frame type flags ---
    public bool Keyframe;
    public bool Invisible;
    public byte Profile;
    public byte Colorspace;  // 0 = BT.601 (only valid value)
    public byte Fullrange;

    // --- Reference frame management ---
    /// <summary>Reference frames: [0]=current, [1]=previous, [2]=golden, [3]=altref.</summary>
    public readonly Vp8Frame[] Frames = new Vp8Frame[5];

    /// <summary>Frame pointers indexed by VP8FrameType.</summary>
    public readonly Vp8Frame?[] FrameRef = new Vp8Frame?[4];

    public bool UpdateLast;
    public int UpdateGolden;   // Vp8FrameType.None if not updated, or which frame to copy
    public int UpdateAltref;

    // --- Deblock / filter ---
    public bool DeblockFilter;
    public bool MbSkipEnabled;

    public struct FilterParams
    {
        public bool Simple;
        public byte Level;
        public byte Sharpness;
    }
    public FilterParams Filter;

    // --- Segmentation ---
    public struct SegmentationParams
    {
        public bool Enabled;
        public bool AbsoluteVals;
        public bool UpdateMap;
        public bool UpdateFeatureData;
        public sbyte[] BaseQuant;    // [4]
        public sbyte[] FilterLevel;  // [4]

        public void Init()
        {
            BaseQuant ??= new sbyte[4];
            FilterLevel ??= new sbyte[4];
        }
    }
    public SegmentationParams Segmentation;

    // --- Loop filter deltas ---
    public struct LfDeltaParams
    {
        public bool Enabled;
        public bool Update;
        /// <summary>
        /// Filter strength adjustment by mode:
        /// [0-3]=i16x16 (always 0), [4]=i4x4, [5]=zero mv, [6]=inter, [7]=split mv.
        /// </summary>
        public sbyte[] Mode;   // [8]
        /// <summary>
        /// Filter strength adjustment by reference frame:
        /// [0]=intra, [1]=previous, [2]=golden, [3]=altref.
        /// </summary>
        public sbyte[] Ref;    // [4]

        public void Init()
        {
            Mode ??= new sbyte[8];
            Ref ??= new sbyte[4];
        }
    }
    public LfDeltaParams LfDelta;

    // --- Quantization ---
    /// <summary>Per-segment quant matrices: [4 segments].</summary>
    public readonly Vp8QuantMatrix[] Qmat = new Vp8QuantMatrix[4];

    /// <summary>Raw quant deltas from frame header.</summary>
    public Vp8RawQuant RawQuant;

    // --- Sign bias ---
    /// <summary>MV sign bias per reference frame type: [4].</summary>
    public readonly sbyte[] SignBias = new sbyte[4];

    // --- Probabilities ---
    /// <summary>
    /// Two probability sets: [0] = current frame, [1] = saved for next frame.
    /// If update_probabilities is false, prob[0] changes are discarded after decode.
    /// </summary>
    public readonly Vp8Probabilities[] Prob = [new Vp8Probabilities(), new Vp8Probabilities()];
    public bool UpdateProbabilities;

    // --- Macroblock state ---
    public Vp8Macroblock[]? Macroblocks;

    /// <summary>Top-edge 4x4 intra prediction modes: [mb_width * 4].</summary>
    public byte[]? Intra4x4PredModeTop;

    /// <summary>Left-edge 4x4 intra prediction modes: [4].</summary>
    public readonly byte[] Intra4x4PredModeLeft = new byte[4];

    // --- Coefficient partitions ---
    /// <summary>Number of coefficient partitions (1, 2, 4, or 8).</summary>
    public int NumCoeffPartitions;

    /// <summary>Header partition size in bytes.</summary>
    public int HeaderPartitionSize;

    // --- NNZ (non-zero coefficient tracking) ---
    /// <summary>Top-edge NNZ: flat [mb_width * 9] (4 luma + 2 Cb + 2 Cr + 1 luma DC per MB).</summary>
    public byte[]? TopNnz;

    /// <summary>Top-edge border pixels for intra prediction: flat [mb_width * 32] (16Y + 8Cb + 8Cr per MB).</summary>
    public byte[]? TopBorder;

    // --- Thread data ---
    public Vp8ThreadData[]? ThreadData;

    // --- Reference counts ---
    public readonly int[] RefCount = new int[3];

    public Vp8DecoderContext()
    {
        for (int i = 0; i < Frames.Length; i++)
            Frames[i] = new Vp8Frame();
        Segmentation.Init();
        LfDelta.Init();
    }

    /// <summary>Allocate or reallocate frame-level arrays for the current dimensions.</summary>
    public void AllocateFrameArrays()
    {
        int mbW = MbWidth;
        int mbH = MbHeight;

        // Macroblocks: width+1 for left-edge sentinel
        Macroblocks = new Vp8Macroblock[(mbW + 1) * (mbH + 1)];

        Intra4x4PredModeTop = new byte[mbW * 4];
        TopNnz = new byte[mbW * 9];
        TopBorder = new byte[mbW * 32];

        // Thread data (single-threaded for now)
        ThreadData = new Vp8ThreadData[1];
        ThreadData[0] = new Vp8ThreadData
        {
            FilterStrength = new Vp8FilterStrength[mbW]
        };
    }

    public void Dispose()
    {
        for (int i = 0; i < Frames.Length; i++)
            Frames[i].Dispose();
    }
}

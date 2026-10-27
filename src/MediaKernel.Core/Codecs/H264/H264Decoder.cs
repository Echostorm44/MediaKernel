// H.264/AVC software video decoder
// Reference: ITU-T H.264, VLC's modules/codec/h264_parse.c

using System;
using System.Diagnostics;
using System.Buffers;
using MediaKernel.Core.Buffers;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264;

/// <summary>
/// Software H.264/AVC video decoder implementing IVideoDecoder.
/// Provides pure-software decoding for H.264 Baseline, Main, and High profiles.
/// </summary>
/// <remarks>
/// This decoder orchestrates the existing H.264 decoding components:
/// - NAL unit parsing (NalUnitParser)
/// - Parameter set management (H264DecoderContext)
/// - Entropy decoding (CabacDecoder, CavlcDecoder)
/// - Macroblock decoding (MacroblockDecoder)
/// - Transform and reconstruction (H264Transform)
/// - Motion compensation (MotionCompensation)
/// - Deblocking filter (DeblockingFilter)
/// </remarks>
[Codec("h264", CodecType.Video, DisplayName = "H.264/AVC Software Decoder", Priority = 5)]
public sealed partial class H264Decoder : IVideoDecoder
{
    private const int MaxDpbFrames = 16;
    private const int MaxRefFrames = 16;
    private const int CabacContextStorageSize = 460;

    /// <summary>Diagnostic flag: skip deblocking filter for PSNR isolation testing.</summary>
    /// <summary>
    /// Ticks spent in entropy decode, reconstruction and the loop filter. Only accumulated in a
    /// build defining MK_STAGE_TIMING; the counters exist unconditionally so a reader can find
    /// them.
    /// </summary>
    /// <remarks>
    /// These exist for comparison rather than profiling. FFmpeg's decoder can be built with the
    /// same three counters around the same three calls, which turns "where does our time go"
    /// into "which stage are we worst at relative to theirs" - different questions that pointed
    /// at different work. By our own profile the loop filter looked like the place to dig; set
    /// beside FFmpeg it is the stage we do best, and entropy decoding, which looked unremarkable
    /// at a tenth of our time, is the worst.
    ///
    /// Compile-time rather than a runtime switch because reading the clock twice per macroblock
    /// per stage costs about 3.4% even when the branch is never taken - the locals and branches
    /// disturb the decode loop whether or not they do anything. See docs/comparing-stages.md.
    /// </remarks>
    public static long StageEntropyTicks, StageReconstructTicks, StageDeblockTicks;

    /// <summary>
    /// Reconstruction split the way FFmpeg's own h264_mb_template.c splits it - intra
    /// prediction, inter prediction, and skipped macroblocks - so the two can be put side by
    /// side per macroblock rather than per stage. FFmpeg counts a skip as inter, so compare
    /// their inter arm against ours plus skip. Same MK_STAGE_TIMING gate as the stages above.
    /// </summary>
    public static long StageIntraTicks, StageInterTicks, StageSkipTicks;
    public static long StageIntraCount, StageInterCount, StageSkipCount;

    /// <summary>Residual coefficient decoding, the part of entropy decode FFmpeg spends a third of its CABAC time in.</summary>
    public static long StageResidualTicks, StageResidualCalls;

    public bool SkipDeblocking { get; set; }
    /// <summary>Diagnostic flag: disable weighted prediction for isolation testing.</summary>
    public bool SkipWeightedPred { get; set; }
    /// <summary>Diagnostic flag: skip inter 8x8 residual for isolation testing.</summary>
    public bool SkipInter8x8Residual { get; set; }
    /// <summary>Diagnostic: trace MB details for a specific MB address (-1 = disabled).</summary>
    public int DiagTraceMbAddr { get; set; } = -1;
    private bool diagTraceMbActive;
    /// <summary>Diagnostic: trace reconstruction for specific MB coords in a specific frame (-1 = disabled).</summary>
    public static int DiagTraceMbX { get; set; } = -1;
    public static int DiagTraceMbY { get; set; } = -1;
    public static int DiagTraceFrame { get; set; } = -1;
    /// <summary>Diagnostic: expose MBAFF field decoding flags for test comparison.</summary>
    public bool[]? GetMbFieldDecodingFlags() => mbFieldDecodingFlag;
    /// <summary>
    /// When true, uses new byte[] instead of ArrayPool to prevent pool-buffer contamination
    /// between sequential decoder instances. ArrayPool returns oversized buffers whose tails
    /// can carry stale data that MC interpolation reads near frame edges.
    /// Performance impact is minimal: ~50 frame buffers per stream at ~38KB each.
    /// </summary>
    public static bool BypassArrayPool = false;

    // Frame buffers come from FrameBufferPool, not ArrayPool.Shared. They are large enough to land
    // on the large object heap, and an allocation there triggers a gen2 collection, which makes the
    // shared pool trim, which guarantees the next rent misses. That loop cost a full collection
    // every 25 frames here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] RentBuffer(int size) =>
        BypassArrayPool ? new byte[size] : FrameBufferPool<byte>.Rent(size);

    /// <summary>
    /// Returns the current frame buffer if this decoder owns it, and forgets it either way.
    /// </summary>
    private void ReleaseCurrentFrameBuffer()
    {
        if (ownsCurrentFrameBuffer)
            ReturnBuffer(currentFrameBuffer);

        currentFrameBuffer = null;
        ownsCurrentFrameBuffer = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnBuffer(byte[]? buffer)
    {
        if (buffer != null && !BypassArrayPool)
            FrameBufferPool<byte>.Return(buffer);
    }
    
    // H.264 spec Table A-1: Level → MaxDPBMbs mapping for DPB size calculation.
    // Used to derive max_num_reorder_frames when VUI bitstream_restriction is absent.
    // Matches FFmpeg's level_max_dpb_mbs[] in h264_ps.c.
    private static readonly (int LevelIdc, int MaxDpbMbs)[] LevelMaxDpbMbs =
    {
        (10, 396),    (11, 900),    (12, 2376),   (13, 2376),
        (20, 2376),   (21, 4752),   (22, 8100),   (30, 8100),
        (31, 18000),  (32, 20480),  (40, 32768),  (41, 32768),
        (42, 34816),  (50, 110400), (51, 184320), (52, 184320),
    };
    
    // H.264 Table 8-15: QPi → QPc mapping for chroma (8-bit, 52 entries)
    internal static readonly byte[] ChromaQpTable8 =
    {
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 30, 31, 32, 32, 33, 34, 34, 35, 35,
        36, 36, 37, 37, 37, 38, 38, 38, 39, 39,
        39, 39
    };
    
    // H.264 Table 8-15 extended for 10-bit (64 entries, from FFmpeg h264data.c ff_h264_chroma_qp[2])
    private static readonly byte[] ChromaQpTable10 =
    {
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
        40, 41, 41, 42, 43, 44, 44, 45, 46, 46,
        47, 47, 48, 48, 49, 49, 49, 50, 50, 50,
        51, 51, 51, 51
    };
    
    // Selected at init based on bitDepth (defaults to 8-bit)
    private byte[] chromaQpTable = ChromaQpTable8;
    private int maxQp = 51;
    
    // H.264 zigzag scan order for 4x4 blocks: scan position → raster position (FFmpeg ff_zigzag_scan)
    private static readonly byte[] ZigzagScan4x4 =
    {
        0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15
    };
    
    // H.264 field scan for 4x4 blocks (spec Table 8-13): column-major order for field pictures
    // Used when field_pic_flag=1 or mb_field_decoding_flag=1 (vertical correlation is weaker)
    private static readonly byte[] FieldScan4x4 =
    {
        0, 4, 1, 8, 12, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15
    };
    
    // H.264 chroma DC scan for 4:2:0 (2x2): scan position → raster position
    private static readonly byte[] ChromaDcScan =
    {
        0, 1, 2, 3
    };
    
    // Z-scan index → spatial X position (in 4x4 block units) within a macroblock
    private static readonly byte[] ZscanToBlockX =
    {
        0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3
    };
    
    // Z-scan index → spatial Y position (in 4x4 block units) within a macroblock
    private static readonly byte[] ZscanToBlockY =
    {
        0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3
    };
    
    // Spatial (y*4+x) → z-scan index
    private static readonly byte[] RasterToZscan =
    {
        0,  1,  4,  5,
        2,  3,  6,  7,
        8,  9, 12, 13,
       10, 11, 14, 15
    };
    
    // scan8 lookup: maps 4x4 block index (0-15) → position in 8-wide neighbor cache
    // Cache layout: col 3 = left border, row 0 = top border; MB data at cols 4-7, rows 1-4
    // Neighbor access: left = Scan8[n]-1, top = Scan8[n]-8
    private static readonly byte[] Scan8 =
    {
        12, 13, 20, 21,   // blocks 0-3  (8x8 block 0: top-left)
        14, 15, 22, 23,   // blocks 4-7  (8x8 block 1: top-right)
        28, 29, 36, 37,   // blocks 8-11 (8x8 block 2: bottom-left)
        30, 31, 38, 39,   // blocks 12-15 (8x8 block 3: bottom-right)
    };
    
    private readonly H264DecoderContext context;
    
    // Decoded Picture Buffer (DPB)
    /// <summary>Stand-in a missing neighbour refers to, so the reference is always valid.</summary>
    private static Macroblock MissingMacroblock;

    private readonly ReferenceFrame[] dpb;

    /// <summary>
    /// How far down the picture being decoded is finished and safe for another thread to read.
    /// </summary>
    /// <remarks>
    /// Published during deblocking, because a reference is read after the loop filter has run
    /// over it. Single-threaded nothing ever waits on this, but it is maintained anyway so the
    /// producing half of frame threading is in place and exercised by the whole conformance
    /// suite rather than only by threaded runs.
    /// </remarks>
    private readonly Threading.PictureProgress currentProgress = new();
    private int dpbCount;
    
    // Frame reorder buffer: holds decoded frames and outputs them in POC (display) order.
    // H.264 B-frames are decoded after the reference P-frame but displayed before it.
    // Without reordering, frames would be output in decode order causing visual artifacts.
    /// <summary>
    /// Whether <c>currentFrameBuffer</c> is ours to return, or is aliasing a DPB entry.
    /// </summary>
    /// <remarks>
    /// Decoding the second field of a complementary pair points currentFrameBuffer straight at
    /// the first field's DPB buffer, which the DPB still owns. Without knowing which case it is
    /// in, the decoder rented a fresh buffer for every picture and never released the old one -
    /// a full frame leaked per frame, 2.76 MiB at 720p, and the pool never had anything to hand
    /// back so every rent allocated on the large object heap and forced a gen2 collection.
    /// </remarks>
    /// <summary>
    /// The access unit handed to Decode, copied once so NAL payloads can slice it, and the NAL
    /// list parsed out of it. Both are reused between calls.
    /// </summary>
    /// <summary>
    /// The current slice with its emulation prevention bytes removed, reused between slices so
    /// that unescaping allocates nothing after the first frame. Only the first
    /// <c>sliceRbspLength</c> bytes are meaningful.
    /// </summary>
    private byte[]? sliceRbsp;
    private int[]? sliceRbspRemoved;
    private int sliceRbspLength;

    private byte[]? accessUnit;
    private NalUnit[]? nalUnitBuffer;
    private int nalUnitCount = -1;



    private bool ownsCurrentFrameBuffer;

    private readonly PendingOutputFrame[] reorderBuffer;

    /// <summary>
    /// The width the CONTAINER declares, or 0. <c>h-&gt;width_from_caller</c> in the C.
    /// </summary>
    /// <remarks>
    /// Set it before decoding. init_dimensions uses it to crop a picture whose SPS says more
    /// than the container does - see <see cref="EffectiveDisplaySize"/> - and that is the only
    /// thing that says a 1920x1088 sequence is a 1920x1080 picture when the SPS carries no
    /// cropping of its own.
    /// </remarks>
    public int ContainerWidth { get; set; }

    /// <summary>The height the container declares, or 0. <c>h-&gt;height_from_caller</c>.</summary>
    public int ContainerHeight { get; set; }

    /// <summary>
    /// Set once the container's dimensions have failed init_dimensions' test, which is what
    /// the C's zeroing of width_from_caller in the else arm amounts to: it does not try again.
    /// </summary>
    private bool containerDimsRejected;

    /// <summary>Scratch for <see cref="PeekFirstMbInSlice"/>; a slice header prefix.</summary>
    private byte[] peekRbsp = new byte[32];
    private int[] peekRemoved = new int[32];
    private int reorderBufferCount;
    private int maxNumReorderFrames;
    
    // Dynamic reorder depth tracking (matches FFmpeg's h264_select_output_frame logic).
    // FFmpeg starts with has_b_frames=0 and increases it when out-of-order POC is detected.
    // This avoids buffering too many frames for streams with small actual reorder depth.
    private int dynamicReorderDepth;
    private readonly int[] lastPocs = new int[MaxDpbFrames];
    
    // Output FIFO: frames ready for display in correct order.
    // Filled when reorder buffer exceeds threshold or at IDR/flush boundaries.
    private readonly Queue<DecodedVideoFrame> outputQueue = new();
    
    // Bit depth and pixel format (set from SPS, cached for performance)
    private int bitDepth = 8;          // Luma bit depth (8, 10, 12, 14)
    private int chromaBitDepth = 8;    // Chroma bit depth
    private int pixelShift;            // 0 for 8-bit, 1 for 10+bit (byte offset multiplier)

    /// <summary><c>FRAME_RECOVERED_IDR</c> - an IDR has been seen, so every picture after it
    /// in DECODED order is correctly decodable.</summary>
    private const int FrameRecoveredIdr = 1 << 0;

    /// <summary><c>FRAME_RECOVERED_SEI</c> - enough pictures have been decoded since a
    /// recovery point SEI, so every picture after it in DISPLAY order is correct.</summary>
    private const int FrameRecoveredSei = 1 << 1;

    /// <summary><c>FRAME_RECOVERED_HEURISTIC</c> - an unmarked random access point, detected
    /// from the reference counts rather than declared by the stream.</summary>
    private const int FrameRecoveredHeuristic = 1 << 2;

    /// <summary>
    /// <c>h-&gt;frame_recovered</c> - which recovery conditions have been met so far. A stream
    /// joined mid-GOP decodes pictures whose references were never sent; until one of these
    /// bits is set, ffmpeg decodes those pictures and does not hand them out, and this
    /// decoder handed out all of them.
    /// </summary>
    private int frameRecovered;

    /// <summary><c>h-&gt;cur_pic_ptr-&gt;recovered</c> for the picture being decoded.</summary>
    private int currentPictureRecovered;

    /// <summary><c>h-&gt;non_gray</c> - an intra picture has been seen since a grey gap
    /// picture, so a grey reference is worth replacing rather than all there is.</summary>
    private bool nonGray;

    /// <summary><c>h-&gt;cur_pic_ptr-&gt;gray</c> for the picture being decoded.</summary>
    private bool currentPictureGray;

    /// <summary>
    /// <c>h-&gt;sei.recovery_point.recovery_frame_cnt</c> - how many frames after this access
    /// unit the picture is fully correct, or -1 when this access unit carried no recovery
    /// point SEI. <c>ff_h264_sei_uninit</c> resets it per access unit, so it describes the
    /// access unit being decoded and not the stream.
    /// </summary>
    private int seiRecoveryFrameCount = -1;

    /// <summary>
    /// <c>h-&gt;recovery_frame</c> - the frame_num at which the next picture is fully
    /// constructed, or -1 when no recovery point is outstanding.
    /// </summary>
    private int recoveryFrame = -1;

    /// <summary><c>h-&gt;valid_recovery_point</c>.</summary>
    private bool validRecoveryPoint;

    /// <summary><c>MAX_LOG2_MAX_FRAME_NUM</c>, the bound decode_recovery_point rejects
    /// against.</summary>
    private const int MaxLog2MaxFrameNum = 12;
    private int maxPixelValue = 255;   // (1 << bitDepth) - 1
    private int neutralChroma = 128;   // 1 << (bitDepth - 1)
    private int qpBdOffset;            // 6 * (bitDepth - 8), QP extension for high bit depth
    private int chromaFormatIdc = 1;   // 1=4:2:0, 2=4:2:2, 3=4:4:4
    private bool isChroma422;          // True when chromaFormatIdc == 2
    private int subHeightC = 2;        // Vertical chroma subsampling: 2 for 4:2:0, 1 for 4:2:2
    
    // Chroma residual buffer layout offsets (vary by chroma format)
    private int chromaCrAcBase = 64;   // Cr AC block offset: 64 (4:2:0) or 128 (4:2:2)
    private int chromaCbDcBase = 128;  // Cb DC coefficient offset: 128 (4:2:0) or 256 (4:2:2)
    private int chromaCrDcBase = 132;  // Cr DC coefficient offset: 132 (4:2:0) or 264 (4:2:2)
    private int chromaBlocksPerComponent = 4;  // Chroma AC blocks per Cb/Cr: 4 (4:2:0) or 8 (4:2:2)
    private int chromaDcCoeffs = 4;    // Chroma DC coefficients per component: 4 (4:2:0) or 8 (4:2:2)
    private int nzCountsPerMb = 24;    // NZC entries per MB: 24 (4:2:0: 16L+8C) or 32 (4:2:2: 16L+16C)
    
    // Current frame being decoded
    private byte[]? currentFrameBuffer;
    private int currentPoc;
    private int currentFrameNum;
    private bool currentIsReference;

    /// <summary>
    /// <c>h-&gt;cur_pic_ptr-&gt;f-&gt;pict_type == AV_PICTURE_TYPE_I</c> - every slice of the
    /// picture so far has been I or SI. Set when the picture starts and cleared by the first
    /// slice that is not, so a picture with one P slice in it is not an I picture.
    /// </summary>
    private bool currentPictureIsIntra = true;

    /// <summary>
    /// <c>h-&gt;has_recovery_point</c> - a recovery point SEI has been seen in this stream.
    /// It only loosens the random-access heuristic's reference-count bound; a stream that
    /// declares its recovery points does not need the heuristic to guess at them.
    /// </summary>
    private bool hasRecoveryPoint;
    private bool currentIsIdr;
    private bool currentIsKeyFrame;
    private bool currentMmcoReset;
    private long currentPts;
    
    // Field picture state
    private int currentTopFieldPoc;
    private int currentBottomFieldPoc;
    private int currentPictureStructure;  // ReferenceFrame.PictFrame/PictTopField/PictBottomField
    private bool currentFieldPicture;     // true when decoding a field (not a frame)
    
    // When decoding the second field of a complementary pair, this points to the
    // DPB index of the first field's entry so we can merge them into the same buffer.
    private int firstFieldDpbIndex = -1;
    
    // Non-reference field pairing: when a non-reference first field is decoded,
    // we save its metadata here so the complementary second field can pair with it.
    // Reference fields are paired via DPB entries; non-reference fields aren't in the DPB.
    private bool pendingNonRefField;
    private int pendingNonRefFieldFrameNum;
    private int pendingNonRefFieldStructure;
    private int pendingNonRefFieldTopPoc;
    private int pendingNonRefFieldBottomPoc;
    private bool isNonRefComplement; // true when currently decoding the complement of a pending non-ref field
    
    /// <summary>POC of the most recently output frame (for diagnostic correlation).</summary>
    public int LastOutputPoc { get; private set; }
    
    // POC state (from previous reference picture per H.264 spec 8.2.1.1).
    //
    // h264dec.c starts these at 1<<16 and -1, not at zero, and the values are load-bearing
    // rather than arbitrary. A stream joined mid-GOP has no IDR to anchor POC to, so the
    // first picture's POC is prev_poc_msb plus its own pic_order_cnt_lsb; starting the MSB at
    // zero put this decoder's POCs at -128 and downwards where ffmpeg's are 65920 and upwards.
    // Nothing in a conformant stream can see a constant offset - POC is only ever compared
    // against another POC - but a frame_num gap stand-in is not conformant: h264_frame_start
    // does not give it a POC at all, so it keeps the zero its recycled slot held. Above the
    // real POCs in this decoder, below them in ffmpeg. That put every gap picture in the wrong
    // reference list, and it is why the grey-reference replacement never fired here.
    //
    // prev_poc_lsb is a sentinel: negative means "no previous picture", and the first picture
    // adopts its own lsb so the wrap arithmetic below has nothing to wrap.
    private int prevPocLsb = -1;
    private int prevPocMsb = 1 << 16;
    // POC type 1/2 state (H.264 spec 8.2.1.2/8.2.1.3). prev_frame_num is -1 and not 0 before
    // the first picture, so a stream whose first frame_num is 1 is a gap and not a sequence.
    private int prevFrameNum = -1;
    private int prevFrameNumOffset;
    
    // Macroblock working buffers
    private short[]? residualLumaBuffer;
    private short[]? residualChromaBuffer;
    private int[]? coefficientBuffer;
    private byte[]? neighborNonZeroCounts;
    private byte[]? cabacContextStorage;
    
    // CABAC neighbor tracking: stores mb_type per macroblock for context derivation
    private int[]? cabacMbTypes;
    // Current MB address and width for CABAC context derivation (set per-MB in DecodeMacroblockCabac)
    private int cabacCurrentMbAddr;
    private int cabacCurrentMbWidth;
    // CABAC neighbor tracking: stores cbpLuma per macroblock
    private int[]? cabacCbpLuma;
    // CABAC neighbor tracking: stores cbpChroma per macroblock
    private int[]? cabacCbpChroma;
    // Temporary: tracks partially-decoded cbpLuma within current MB for intra-MB block context
    private int cbpLumaPartial;
    // CABAC neighbor tracking: per-MB luma coded_block_flag bitfield (16 bits for 16 4x4 blocks)
    private int[]? cabacLumaCbf;
    // CABAC neighbor tracking: per-MB I16x16 luma DC coded flag (1 if DC has nonzero coefficients)
    private int[]? cabacLumaDcCoded;
    // CABAC neighbor tracking: per-MB chroma coded_block_flag bitfield
    // bits 0-3: Cb AC blocks, bits 4-7: Cr AC blocks, bit 8: Cb DC, bit 9: Cr DC
    private int[]? cabacChromaCbf;
    // CABAC state: previous MB's QP delta (for mb_qp_delta context)
    private int previousMbQpDelta;
    // CABAC state: previous MB's QP (for QP delta accumulation)
    private int previousMbQp;
    // CABAC neighbor tracking: per-MB intra chroma prediction mode
    private int[]? cabacChromaPredMode;
    // CABAC neighbor tracking: per-MB flag indicating Intra_16x16 prediction mode
    // Used by CBP luma context derivation (spec 9.3.3.1.1.3: I16x16 neighbor → condTermFlag = 0)
    private bool[]? cabacIsIntra16x16;
    // Per-MB flag indicating Intra4x4 prediction mode for MPM neighbor derivation
    private bool[]? cabacIsIntra4x4;
    // Per-MB transform_size_8x8_flag for neighbor context derivation (context 399+neighborSize)
    private int[]? cabacTransform8x8;
    // Per-MB intra4x4 prediction modes storage (16 modes per MB) for MPM computation
    private byte[]? mbIntra4x4Modes;
    // Per-MB flag: true if MB is coded with ANY intra prediction mode (I4x4, I16x16, I_PCM)
    // Used by constrained intra prediction to exclude inter-coded neighbor pixels
    private bool[]? mbIsIntra;
    
    
    // MVD (motion vector difference) cache for inter prediction CABAC context derivation
    // Uses scan8 layout (8-wide grid, 40 entries); left neighbor at pos-1, top at pos-8
    // L0 cache (used by P-slices and B-slice L0)
    private readonly int[] mvdCacheX = new int[40];
    private readonly int[] mvdCacheY = new int[40];
    // L1 cache (used by B-slice L1 only)
    private readonly int[] mvdCacheL1X = new int[40];
    private readonly int[] mvdCacheL1Y = new int[40];
    // Per-MB absolute mvd boundary storage, indexed [mbAddr * 4 + i] for i=0..3
    // Right column stores col 3 values (4x4 rows 0-3); bottom row stores row 3 values (4x4 cols 0-3)
    private int[]? cabacAbsMvdXRight;
    private int[]? cabacAbsMvdYRight;
    private int[]? cabacAbsMvdXBottom;
    private int[]? cabacAbsMvdYBottom;
    private int[]? cabacAbsMvdL1XRight;
    private int[]? cabacAbsMvdL1YRight;
    private int[]? cabacAbsMvdL1XBottom;
    private int[]? cabacAbsMvdL1YBottom;
    
    // Reference index cache for CABAC ref_idx context derivation (scan8 layout)
    // Stores ref values: -1 = unavailable, 0+ = actual ref idx. 0 for skip/intra/Direct.
    private readonly int[] refCacheL0 = new int[40];
    private readonly int[] refCacheL1 = new int[40];
    // Per-MB ref boundary storage (same layout as MVD boundaries)
    private int[]? cabacRefL0Right;
    private int[]? cabacRefL0Bottom;
    private int[]? cabacRefL1Right;
    private int[]? cabacRefL1Bottom;
    
    // Per-4x4-block Direct mode flag cache (scan8 layout, like ffmpeg's direct_cache)
    // Used in CABAC ref_idx context: Direct blocks suppress condTermFlag even if ref > 0.
    // Keeps actual ref values unclamped so PredMotion and spatial direct get correct refs.
    private readonly int[] directCache = new int[40];
    private int[]? directBoundaryRight;
    private int[]? directBoundaryBottom;
    
    // Actual motion vector cache (scan8 layout, quarter-pixel units, signed)
    // These store the final MV = MVPred + signedMVD, used for motion compensation
    private readonly short[] mvCacheL0X = new short[40];
    private readonly short[] mvCacheL0Y = new short[40];
    private readonly short[] mvCacheL1X = new short[40];
    private readonly short[] mvCacheL1Y = new short[40];
    // Per-MB actual MV boundary storage (same layout as MVD boundaries)
    // Right column: rightmost 4x4 column MVs; Bottom row: bottom 4x4 row MVs
    private short[]? mbMvL0XRight;
    private short[]? mbMvL0YRight;
    private short[]? mbMvL0XBottom;
    private short[]? mbMvL0YBottom;
    private short[]? mbMvL1XRight;
    private short[]? mbMvL1YRight;
    private short[]? mbMvL1XBottom;
    private short[]? mbMvL1YBottom;
    // Per-MB ref index storage for motion compensation
    // Indexed [mbAddr * 4 + sub8x8Idx] for 4 8x8 sub-blocks per MB
    private sbyte[]? mbRefIdxL0;
    private sbyte[]? mbRefIdxL1;
    
    // Per-4x4-block MV and ref storage for deblocking filter bS calculation
    // Indexed [mbAddr * 16 + y4 * 4 + x4] (y4=0..3, x4=0..3) in raster order
    /// <summary>One entry per macroblock: does it carry the same motion in all sixteen blocks?</summary>
    private bool[]? deblockMotionUniform;

    /// <summary>
    /// Per-4x4 motion vectors for the loop filter, x and y adjacent - <c>[2*block]</c> is x and
    /// <c>[2*block + 1]</c> is y, which is FFmpeg's <c>motion_val[list][block][2]</c> layout.
    /// </summary>
    /// <remarks>
    /// These were four separate arrays, so reading one vector meant two loads from two places a
    /// quarter of a megabyte apart, and the boundary-strength derivation reads them constantly.
    /// Interleaved, a vector is one cache line and its two halves arrive together.
    /// </remarks>
    private short[]? deblockMvL0;
    private short[]? deblockMvL1;
    private sbyte[]? deblockRefL0;
    private sbyte[]? deblockRefL1;
    // Per-4x4-block reference picture identity (POC) for deblocking bS comparison.
    // Unlike raw ref indices, POC correctly identifies pictures across L0/L1 lists.
    private int[]? deblockRefPicL0;
    private int[]? deblockRefPicL1;
    
    // Reference picture lists built per-slice for ref_idx → DPB mapping
    private readonly byte[]?[] refPicListL0 = new byte[]?[MaxRefFrames + 1];
    private readonly byte[]?[] refPicListL1 = new byte[]?[MaxRefFrames + 1];
    private readonly int[] refPicListL0Poc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1Poc = new int[MaxRefFrames + 1];
    // FFmpeg-compatible reference POC: for field pairs, this is the second decoded field's POC.
    // Used in dist_scale_factor and implicit weight table (NOT for reference matching).
    private readonly int[] refPicListL0RefPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1RefPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL0FrameNum = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1FrameNum = new int[MaxRefFrames + 1];
    private readonly bool[] refPicListL0IsLongTerm = new bool[MaxRefFrames + 1];
    private readonly bool[] refPicListL1IsLongTerm = new bool[MaxRefFrames + 1];
    private int refPicListL0Count;
    private int refPicListL1Count;
    
    // Per-reference luma stride and offset for field picture MC.
    // For frame references: stride=codedWidth, offset=0.
    // For field references: stride=codedWidth*2, offset=0 (top) or codedWidth (bottom).
    // Chroma derived: chromaStride=lumaStride/2, chromaOffset=lumaOffset/2.
    private readonly int[] refPicListL0RefStride = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL0RefOffset = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1RefStride = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1RefOffset = new int[MaxRefFrames + 1];
    // Per-reference field parity: true=top field, false=bottom field. Only meaningful for field pictures.
    private readonly bool[] refPicListL0IsTopField = new bool[MaxRefFrames + 1];
    private readonly bool[] refPicListL1IsTopField = new bool[MaxRefFrames + 1];
    // Per-reference DPB entry POC: the frame-level POC of the DPB entry this ref came from.
    // For field refs split from a frame, this is the frame POC (≠ field POC).
    // Used in PicAFF temporal direct mode to correctly map collocated frame-level ref POCs.
    private readonly int[] refPicListL0DpbPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1DpbPoc = new int[MaxRefFrames + 1];
    // Per-reference top/bottom field POCs for MBAFF field weight computation.
    // For frame refs, stores the individual field POCs from the DPB entry.
    private readonly int[] refPicListL0TopFieldPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL0BotFieldPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1TopFieldPoc = new int[MaxRefFrames + 1];
    private readonly int[] refPicListL1BotFieldPoc = new int[MaxRefFrames + 1];
    
    // Implicit weighted bi-prediction table (weighted_bipred_idc == 2)
    // Indexed [refIdxL0, refIdxL1], stores w1 (L1 weight); w0 = 64 - w1
    // Used with logWeightDenom=5, offsets=0
    private readonly int[,] implicitWeightL1 = new int[MaxRefFrames, MaxRefFrames];
    // MBAFF field-level implicit weights: indexed [fieldRefL0, fieldRefL1, curFieldParity].
    // fieldRef = original field ref index before >>1 conversion (0..2*refCount-1).
    // curFieldParity = mbY & 1 (0=top, 1=bottom).
    // Computed using per-field POCs for accurate field-to-field distance.
    private readonly int[,,] implicitWeightL1Field = new int[2 * MaxRefFrames, 2 * MaxRefFrames, 2];
    private bool useImplicitWeightedPred;
    
    // MMCO operation 6 deferred flag: marks current frame as long-term when added to DPB
    private bool currentFrameIsLongTerm;
    private int currentFrameLongTermIdx;
    
    // Current slice header (needed by reconstruction for direct mode etc.)
    private SliceHeader? currentSliceHeader;
    // Active 4x4 coefficient scan table: frame zigzag for progressive, field scan for field pictures
    /// <summary>
    /// The four-by-four coefficient scan for the macroblock currently being parsed.
    /// </summary>
    /// <remarks>
    /// Read in exactly one place - <c>DecodeBlockCabac</c>, which stores each coefficient at its
    /// raster position as it is decoded. Written per slice below, and again per macroblock in
    /// MBAFF once <c>mb_field_decoding_flag</c> is known. It used to be set a fourth and fifth
    /// time immediately before <c>ReconstructMacroblock</c>, because reconstruction was where the
    /// scan was applied; those writes are gone with the reordering they served, and setting this
    /// after a macroblock is parsed would now be setting it too late.
    /// </remarks>
    private byte[] activeScan4x4 = ZigzagScan4x4;
    // Constrained intra prediction flag from current PPS (cached for CIP checks in neighbor derivation)
    private bool constrainedIntraPred;
    
    // Per-MB slice membership tracking for cross-slice boundary checks (MV prediction + deblocking).
    // MBs in different slices must NOT share MV/ref data for prediction (H.264 §6.4.11.1).
    // Deblocking must skip cross-slice MB edges when DisableDeblockingFilterIdc == 2 (H.264 §8.7.2.1).
    private int[]? mbSliceIndex;
    private int currentSliceIdx;
    
    // Per-slice deblocking parameters (indexed by currentSliceIdx).
    private uint[]? sliceDeblockIdc;
    private int[]? sliceFilterOffsetA;
    private int[]? sliceFilterOffsetB;
    
    // Per-frame macroblock storage for deblocking filter (needs neighbor MB data)
    private Macroblock[]? frameMacroblocks;
    
    // MBAFF (Macroblock-Adaptive Frame-Field) support
    // When true, the current picture uses MBAFF coding (SPS mb_adaptive_frame_field + frame picture)
    private bool isMbaffFrame;
    // Per-MB field decoding flag: true = field MB, false = frame MB (only valid when isMbaffFrame)
    private bool[]? mbFieldDecodingFlag;
    // MBAFF CABAC state: tracks skip pair interaction across top→bottom MB
    private bool mbaffPrevMbSkipped;
    private bool mbaffNextMbSkipped;
    // Current pair's mb_field_decoding_flag (equivalent to FFmpeg's sl->mb_field_decoding_flag)
    private bool mbaffCurrentFieldFlag;

    // MBAFF neighbor derivation state (computed per-MB by ComputeMbaffNeighbors)
    // These map to FFmpeg's sl->left_mb_xy[LTOP/LBOT], sl->top_mb_xy, etc.
    private int mbaffLeftXYTop;      // left_xy[LTOP]
    private int mbaffLeftXYBot;      // left_xy[LBOT]
    private int mbaffTopXY;          // top_xy
    private int mbaffTopLeftXY;      // topleft_xy
    private int mbaffTopRightXY;     // topright_xy
    private int mbaffLeftBlockIdx;   // Index into LeftBlockOptions (0-3)
    private int mbaffTopleftPartition; // -1 default (bottom-right), 0 for bottom-frame+left-field (top-right)
    
    // MBAFF diagonal fetch state — set by PrepareMvCache, read by FetchDiagonalMv.
    // These provide the current MB's position needed for the cross-type diagonal path
    // (FFmpeg h264_mvpred.h fetch_diagonal_mv lines 177-206).
    private int currentDecodeMbY;
    private int currentDecodeMbWidth;

    /// <summary>
    /// Sub-block index remapping tables for MBAFF left neighbor access.
    /// Maps to FFmpeg's left_block_options[4][8] (first 8 entries are luma block row indices).
    /// Index: [table][position] where position 0-3 map to left border rows for LTOP(0,1) and LBOT(2,3).
    /// Values are row indices (0-3) within the neighbor MB's right column boundary data.
    /// </summary>
    private static readonly int[][] LeftBlockOptions = new int[4][]
    {
        // Option 0: Frame→Frame (default, both frame-coded)
        new int[] { 0, 1, 2, 3 },
        // Option 1: Frame→Field (current=frame, left=field)
        new int[] { 2, 2, 3, 3 },
        // Option 2: Field→Frame (current=field, left=frame) — top half
        new int[] { 0, 0, 1, 1 },
        // Option 3: Field→Field (current=field, left=field) — cross-pair
        new int[] { 0, 2, 0, 2 }
    };
    
    // Chroma 4:2:0 left-block remapping for MBAFF (2x2 chroma grid).
    // Derived from FFmpeg's left_block_options indices 12-15:
    //   Option 0: nnz[17,21] = Cb row 0, row 1 → identity {0, 1}
    //   Option 1: nnz[21,21] = Cb row 1, row 1 → both map to bottom {1, 1}
    //   Option 2: nnz[17,17] = Cb row 0, row 0 → both map to top {0, 0}
    //   Option 3: nnz[17,17] = Cb row 0, row 0 → both map to top {0, 0}
    // 4:2:0: 2 chroma block rows per component — only entries [0],[1] used
    private static readonly int[][] ChromaLeftBlockOptions420 = new int[4][]
    {
        new int[] { 0, 1 },  // Frame→Frame: identity
        new int[] { 1, 1 },  // Frame→Field: both rows → bottom of field
        new int[] { 0, 0 },  // Field→Frame: both rows → top of frame
        new int[] { 0, 0 },  // Field→Field cross-pair: both rows → top
    };
    // 4:2:2: 4 chroma block rows per component — all 4 entries used.
    // Derived from FFmpeg left_block_options[4][32] chroma indices (h264_mvpred.h:491-496).
    private static readonly int[][] ChromaLeftBlockOptions422 = new int[4][]
    {
        new int[] { 0, 1, 2, 3 },  // Frame→Frame: identity
        new int[] { 2, 2, 3, 3 },  // Frame→Field: top half→field row 2, bottom→row 3
        new int[] { 0, 0, 1, 1 },  // Field→Frame: pairs collapse to field rows 0,1
        new int[] { 0, 2, 0, 2 },  // Field→Frame cross: interleaved (even→0, odd→2)
    };
    
    // NAL unit length size for AVCC format (0 = Annex B, 1-4 = length prefix size)
    private int nalLengthSize;
    // Actual working NAL length size (auto-detected from first packet if declared size fails)
    private int detectedNalLengthSize;
    
    // Debug counter
    private int debugChromaCounter;
    private int blockExceptionCounter;
    
    private bool isInitialized;
    private bool isDisposed;

    public string CodecId => "h264";
    public bool IsHardwareAccelerated => false;
    public int Width => context.Width;
    public int Height => context.Height;
    public PixelFormat OutputFormat
    {
        get
        {
            if (isChroma422)
                return bitDepth > 8 ? PixelFormat.Yuv422P10 : PixelFormat.Yuv422P;
            return bitDepth > 8 ? PixelFormat.Yuv420P10 : PixelFormat.Yuv420P;
        }
    }
    
    /// <summary>
    /// Debug: Total frames decoded since initialization.
    /// </summary>
    public long FramesDecoded { get; private set; }
    
    /// <summary>
    /// Debug: Total NAL units processed.
    /// </summary>
    public long NalUnitsProcessed { get; private set; }
    
    /// <summary>
    /// Debug: CABAC macroblock decode diagnostics (updated each slice).
    /// </summary>
    public int CabacMbsDecoded { get; private set; }
    public int CabacTotalMbs { get; private set; }
    public Exception? CabacFirstException { get; private set; }
    /// <summary>Why the CABAC slice loop stopped, formatted only if something asks.</summary>
    /// <remarks>
    /// This used to be an interpolated string built where the loop exits. Every slice exits - the
    /// normal ending is terminate_bin=1 - so a diagnostic string nobody reads was allocated for
    /// every slice of every frame. The pieces are kept instead and assembled on demand, which
    /// leaves every reader seeing exactly the text it saw before.
    /// </remarks>
    public string? CabacExitReason => cabacExit switch
    {
        CabacExitKind.TerminateBinPair =>
            $"terminate_bin=1 at pair ({cabacExitMbX},{cabacExitMbY}), bp={cabacExitBitPos}, remaining={cabacExitRemaining}",
        CabacExitKind.StreamEndPair =>
            $"stream_end at pair ({cabacExitMbX},{cabacExitMbY}), EOS={cabacExitEos}, remaining={cabacExitRemaining}",
        CabacExitKind.ExceptionTop =>
            $"exception at top MB {cabacExitMbAddr} ({cabacExitMbX},{cabacExitMbY}): {cabacExitMessage}",
        CabacExitKind.ExceptionBottom =>
            $"exception at bottom MB {cabacExitMbAddr} ({cabacExitMbX},{cabacExitMbY + 1}): {cabacExitMessage}",
        CabacExitKind.TerminateBin =>
            $"terminate_bin=1 at MB {cabacExitMbAddr} ({cabacExitMbX},{cabacExitMbY}), bp={cabacExitBitPos}, remaining={cabacExitRemaining}",
        CabacExitKind.Exception =>
            $"exception at MB {cabacExitMbAddr} ({cabacExitMbX},{cabacExitMbY})",
        CabacExitKind.StreamEnd =>
            $"stream_end at MB {cabacExitMbAddr} ({cabacExitMbX},{cabacExitMbY}), EOS={cabacExitEos}, remaining={cabacExitRemaining}",
        _ => null,
    };

    private enum CabacExitKind
    {
        None, TerminateBinPair, StreamEndPair, ExceptionTop, ExceptionBottom,
        TerminateBin, Exception, StreamEnd,
    }

    private CabacExitKind cabacExit;
    private int cabacExitMbAddr, cabacExitMbX, cabacExitMbY, cabacExitBitPos, cabacExitRemaining;
    private bool cabacExitEos;
    private string? cabacExitMessage;
    public int CabacBlockExceptionsSwallowed { get; private set; }
    
    /// <summary>
    /// Debug: Slice type of the most recently decoded slice (I=2, P=0, B=1).
    /// </summary>
    public SliceType LastSliceType { get; private set; }
    
    /// <summary>
    /// Debug: Per-MB CABAC state snapshots for the most recent slice.
    /// Each entry: "MB{addr}({x},{y}) type={mbType} bitPos={pos} range={range} offset={offset}"
    /// </summary>
    public List<string>? CabacMbDiagnostics { get; private set; }
    
    // Per-frame timing (ms) for performance profiling
    /// <summary>Milliseconds since a Stopwatch.GetTimestamp() reading.</summary>
    private static double ElapsedMs(long startTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
        / System.Diagnostics.Stopwatch.Frequency;

    public double TimingCabacMs { get; private set; }
    public double TimingReconstructMs { get; private set; }
    public double TimingDeblockMs { get; private set; }
    public double TimingRefListMs { get; private set; }
    
    /// <summary>
    /// Debug: The max_num_reorder_frames used by the decoder's internal reorder buffer.
    /// 0 means reordering is disabled (no B-frames or VUI not present).
    /// </summary>
    public int MaxNumReorderFrames => maxNumReorderFrames;
    
    /// <summary>
    /// Debug: Returns true if decoder is initialized with SPS/PPS.
    /// </summary>
    public bool IsReady => isInitialized;
    
    /// <summary>
    /// Debug: Returns true if PPS uses CABAC entropy coding.
    /// </summary>
    public bool UsesCabac => context.ActivePps?.EntropyCodingMode ?? false;

    public H264Decoder()
    {
        context = new H264DecoderContext();
        dpb = new ReferenceFrame[MaxDpbFrames];
        reorderBuffer = new PendingOutputFrame[MaxDpbFrames + 1];
        Array.Fill(lastPocs, int.MinValue);
    }

    /// <summary>
    /// Initializes the decoder with codec configuration data (SPS/PPS from container).
    /// </summary>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.Length == 0)
        {
            // The parameter sets are IN BAND, and isInitialized deliberately stays false: it
            // means "a parameter set pair is active and the buffers are allocated", and the
            // only thing that does that for an Annex-B stream is the `if (!isInitialized)`
            // arm on the first slice NAL. Setting it true here claimed the work was done and
            // skipped that arm, so DecodeSliceData's own guard - ActiveSps or ActivePps null
            // - refused every slice for the life of the decoder.
            //
            // Nothing noticed because the Annex-B path that IS gated never calls this: the
            // FrameCrc harness calls Initialize only when there is codec private data, and
            // the 185 FATE streams are raw .264 files. DecodePipeline calls it always, with
            // an empty span for a transport stream - so every H.264 TS in this tree decoded
            // nothing, which is what the playability sweep reported.
            return true;
        }

        // Parse AVC decoder configuration record (avcC box format)
        if (codecPrivate.Length >= 7 && codecPrivate[0] == 1)
        {
            return ParseAvccConfiguration(codecPrivate);
        }

        // Try parsing as raw NAL units (Annex B format)
        ParseAnnexBParameterSets(codecPrivate);
        
        isInitialized = context.ActiveSps != null;
        if (isInitialized)
        {
            AllocateBuffers();
        }
        
        return isInitialized;
    }

    private bool ParseAvccConfiguration(ReadOnlySpan<byte> config)
    {
        if (config.Length < 7)
            return false;

        // configurationVersion = config[0] (should be 1)
        // AVCProfileIndication = config[1]
        // profile_compatibility = config[2]
        // AVCLevelIndication = config[3]
        // lengthSizeMinusOne = config[4] & 0x03 (NAL length prefix size minus 1)
        nalLengthSize = (config[4] & 0x03) + 1;
        
        int numSps = config[5] & 0x1F;
        int offset = 6;
        
        // Parse SPS NAL units
        for (int i = 0; i < numSps && offset + 2 <= config.Length; i++)
        {
            int spsLen = (config[offset] << 8) | config[offset + 1];
            offset += 2;
            
            if (offset + spsLen > config.Length || spsLen < 1)
                break;

            // avcC contains full NAL units including the 1-byte NAL header
            // Pass just the RBSP (skip the NAL header byte)
            var spsPayload = config.Slice(offset + 1, spsLen - 1);
            ProcessParameterSetNal(NalUnitType.SequenceParameterSet, spsPayload);
            offset += spsLen;
        }

        if (offset >= config.Length)
            return context.ActiveSps != null;

        // Parse PPS NAL units
        int numPps = config[offset++];
        for (int i = 0; i < numPps && offset + 2 <= config.Length; i++)
        {
            int ppsLen = (config[offset] << 8) | config[offset + 1];
            offset += 2;
            
            if (offset + ppsLen > config.Length || ppsLen < 1)
                break;

            // Skip the NAL header byte, pass just the RBSP
            var ppsPayload = config.Slice(offset + 1, ppsLen - 1);
            ProcessParameterSetNal(NalUnitType.PictureParameterSet, ppsPayload);
            offset += ppsLen;
        }

        // Activate the first PPS (which will also activate its SPS)
        // avcC always has SPS ID 0 and PPS ID 0 as primary
        context.ActivatePps(0);
        
        isInitialized = context.ActiveSps != null && context.ActivePps != null;
        if (isInitialized)
        {
            AllocateBuffers();
        }
        
        return isInitialized;
    }

    private void ParseAnnexBParameterSets(ReadOnlySpan<byte> data)
    {
        foreach (var nal in NalUnitParser.ParseAnnexB(data))
        {
            context.ProcessNalUnit(nal);
        }
    }

    private void ProcessParameterSetNal(NalUnitType type, ReadOnlySpan<byte> payload)
    {
        // Create a NAL unit wrapper for the context
        var nalData = new byte[payload.Length + 1];
        nalData[0] = (byte)((int)type | (3 << 5)); // nal_ref_idc = 3 for SPS/PPS
        payload.CopyTo(nalData.AsSpan(1));
        
        var nal = new NalUnit(type, 3, nalData.AsMemory(1), nalData.AsMemory(), 0);
        context.ProcessNalUnit(nal);
    }

    private void AllocateBuffers()
    {
        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int lumaSize = width * height;
        
        // maxNumReorderFramesfrom SPS VUI is used as a minimum for the dynamic depth.
        // The dynamic depth (dynamicReorderDepth) starts at 0 and increases as
        // out-of-order POC is detected, matching FFmpeg's has_b_frames logic.
        // The level-based heuristic is NOT used as the primary threshold — it's
        // only a cap to prevent unbounded growth.
        var sps = context.ActiveSps;
        
        if (sps?.Vui != null && sps.Vui.BitstreamRestrictionPresent)
        {
            maxNumReorderFrames = (int)sps.Vui.MaxNumReorderFrames;
        }
        else
        {
            // Without VUI bitstream_restriction, use max_num_ref_frames as a
            // conservative initial reorder depth. Hierarchical P-frame streams
            // (like HCBP2) have non-monotonic POC even without B-frames, requiring
            // deep reorder buffering from the start.
            maxNumReorderFrames = sps?.MaxNumRefFrames ?? 0;
        }

        // Set bit depth fields from SPS (default 8-bit if not specified)
        bitDepth = sps?.BitDepthLuma ?? 8;
        chromaBitDepth = sps?.BitDepthChroma ?? 8;
        pixelShift = bitDepth > 8 ? 1 : 0;
        maxPixelValue = (1 << bitDepth) - 1;
        neutralChroma = 1 << (bitDepth - 1);
        qpBdOffset = 6 * (bitDepth - 8);
        maxQp = 51 + qpBdOffset;
        chromaQpTable = bitDepth > 8 ? ChromaQpTable10 : ChromaQpTable8;
        
        // Set chroma format fields from SPS
        chromaFormatIdc = sps != null ? (int)sps.ChromaFormat : 1;
        isChroma422 = chromaFormatIdc == 2;
        // subHeightC: 2 for 4:2:0, 1 for 4:2:2. Keep 2 for 4:4:4 to avoid
        // regressions (4:4:4 support not yet implemented).
        subHeightC = isChroma422 ? 1 : 2;
        
        // Chroma residual buffer layout offsets
        if (isChroma422)
        {
            chromaBlocksPerComponent = 8;   // 8 AC blocks per Cb/Cr
            chromaCrAcBase = 128;           // Cr AC starts after 8 Cb blocks (8*16=128)
            chromaCbDcBase = 256;           // Cb DC after all AC (128+128=256)
            chromaCrDcBase = 264;           // Cr DC after Cb DC (256+8=264)
            chromaDcCoeffs = 8;             // 8 DC coefficients per component
            nzCountsPerMb = 32;            // 16 luma + 16 chroma NZC entries
        }
        else
        {
            chromaBlocksPerComponent = 4;
            chromaCrAcBase = 64;
            chromaCbDcBase = 128;
            chromaCrDcBase = 132;
            chromaDcCoeffs = 4;
            nzCountsPerMb = 24;            // 16 luma + 8 chroma NZC entries
        }
        
        // Chroma plane size: 4:2:0 = lumaSize/2, 4:2:2 = lumaSize (half width, full height)
        int chromaSize = chromaFormatIdc == 2 ? lumaSize : lumaSize / 2;
        
        // For 10-bit, each pixel sample takes 2 bytes (ushort) instead of 1
        int bytesPerSample = 1 + pixelShift;
        int frameSizeBytes = (lumaSize + chromaSize) * bytesPerSample;

        ReleaseCurrentFrameBuffer();
        currentFrameBuffer = RentBuffer(frameSizeBytes);
        ownsCurrentFrameBuffer = true;
        // Clear entire rented buffer — ArrayPool may return oversized buffers,
        // and MC interpolation near frame edges can read beyond frameSize
        Array.Clear(currentFrameBuffer);
        // Initialize chroma (U+V) to neutral so unwritten chroma is gray, not green
        if (pixelShift == 0)
        {
            currentFrameBuffer.AsSpan(lumaSize, chromaSize).Fill(128);
        }
        else
        {
            // 10-bit: fill chroma with neutral value (512) as little-endian ushort
            var chromaSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                currentFrameBuffer.AsSpan(lumaSize * bytesPerSample, chromaSize * bytesPerSample));
            chromaSpan.Fill((ushort)neutralChroma);
        }
        
        int mbCount = context.TotalMacroblocks;
        residualLumaBuffer = new short[256];      // 16x16 luma residuals
        int chromaResidualSize = isChroma422 ? 272 : 256;  // 4:2:2 needs 8+8 AC blocks + 8+8 DC coeffs
        residualChromaBuffer = new short[chromaResidualSize];
        coefficientBuffer = new int[256];          // Transform coefficients
        neighborNonZeroCounts = new byte[mbCount * nzCountsPerMb]; // nZ for context
        cabacContextStorage = new byte[CabacContextStorageSize];
        cabacMbTypes = new int[mbCount];
        cabacCbpLuma = new int[mbCount];
        cabacCbpChroma = new int[mbCount];
        cabacLumaCbf = new int[mbCount];
        cabacLumaDcCoded = new int[mbCount];
        cabacChromaCbf = new int[mbCount];
        cabacChromaPredMode = new int[mbCount];
        cabacIsIntra16x16 = new bool[mbCount];
        cabacIsIntra4x4 = new bool[mbCount];
        cabacTransform8x8 = new int[mbCount];
        mbIntra4x4Modes = new byte[mbCount * 16];
        mbIsIntra = new bool[mbCount];
        cabacAbsMvdXRight = new int[mbCount * 4];
        cabacAbsMvdYRight = new int[mbCount * 4];
        cabacAbsMvdXBottom = new int[mbCount * 4];
        cabacAbsMvdYBottom = new int[mbCount * 4];
        cabacAbsMvdL1XRight = new int[mbCount * 4];
        cabacAbsMvdL1YRight = new int[mbCount * 4];
        cabacAbsMvdL1XBottom = new int[mbCount * 4];
        cabacAbsMvdL1YBottom = new int[mbCount * 4];
        cabacRefL0Right = new int[mbCount * 4];
        cabacRefL0Bottom = new int[mbCount * 4];
        cabacRefL1Right = new int[mbCount * 4];
        cabacRefL1Bottom = new int[mbCount * 4];
        directBoundaryRight = new int[mbCount * 4];
        directBoundaryBottom = new int[mbCount * 4];
        
        // Actual MV boundary storage for motion compensation
        mbMvL0XRight = new short[mbCount * 4];
        mbMvL0YRight = new short[mbCount * 4];
        mbMvL0XBottom = new short[mbCount * 4];
        mbMvL0YBottom = new short[mbCount * 4];
        mbMvL1XRight = new short[mbCount * 4];
        mbMvL1YRight = new short[mbCount * 4];
        mbMvL1XBottom = new short[mbCount * 4];
        mbMvL1YBottom = new short[mbCount * 4];
        // Per-MB ref index (4 per MB for 4 8x8 sub-blocks)
        mbRefIdxL0 = new sbyte[mbCount * 4];
        mbRefIdxL1 = new sbyte[mbCount * 4];
        // Per-4x4-block MV and ref data for deblocking (16 per MB)
        deblockMotionUniform = new bool[mbCount];
        deblockMvL0 = new short[mbCount * 32];
        deblockMvL1 = new short[mbCount * 32];
        deblockRefL0 = new sbyte[mbCount * 16];
        deblockRefL1 = new sbyte[mbCount * 16];
        // Reference identity is a property of the 8x8 partition, not the 4x4 block, so four
        // entries a macroblock carry everything sixteen did. At 1080p that is 128 KB a list
        // rather than 510 - and the deblocking metadata as a whole, which the loop filter
        // streams for every macroblock, drops from 2.3 MB a frame to 1.5. FFmpeg keeps the same
        // thing per partition, as one int8_t.
        deblockRefPicL0 = new int[mbCount * 4];
        deblockRefPicL1 = new int[mbCount * 4];
        // Per-frame macroblock array for deblocking
        frameMacroblocks = new Macroblock[mbCount];
        // MBAFF per-MB field decoding flag
        mbFieldDecodingFlag = new bool[mbCount];
        // Slice membership for cross-slice availability checks
        mbSliceIndex = new int[mbCount];
        // Per-slice deblocking params (max slices = mbCount for FMO worst case)
        sliceDeblockIdc = new uint[mbCount];
        sliceFilterOffsetA = new int[mbCount];
        sliceFilterOffsetB = new int[mbCount];
    }

    /// <summary>
    /// Decodes a compressed H.264 access unit.
    /// </summary>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        // End-of-stream flush: drain reorder buffer one frame at a time
        if (data.IsEmpty && reorderBuffer != null)
        {
            if (outputQueue.Count > 0)
                return outputQueue.Dequeue();
            var flushed = OutputLowestPocFrame();
            if (flushed != null)
                return flushed;
            return null;
        }

        // Parse NAL units using appropriate format
        NalUnit[] nalUnits;
        if (nalLengthSize > 0)
        {
            if (detectedNalLengthSize > 0)
            {
                // Auto-detection has settled, so split without copying: hold the access unit in a
                // buffer this decoder owns and let each NAL slice it. The span path copies every
                // payload twice, once for Payload and once for RawData, on every frame.
                if (accessUnit is null || accessUnit.Length < data.Length)
                    accessUnit = new byte[Math.Max(data.Length, 65536)];
                data.CopyTo(accessUnit);

                NalUnitParser.ParseLengthPrefixedInto(
                    accessUnit.AsMemory(0, data.Length), detectedNalLengthSize,
                    ref nalUnitBuffer, out int parsed);
                nalUnits = nalUnitBuffer!;
                nalUnitCount = parsed;
            }
            else
            {
                // AVCC format (length-prefixed) — use auto-detection for broken muxers.
                // Some MKV files declare nalLengthSize=4 in avcC but store data with 3-byte prefixes.
                nalUnits = NalUnitParser.ParseLengthPrefixedWithAutoDetect(
                    data, nalLengthSize, ref detectedNalLengthSize);
                nalUnitCount = nalUnits.Length;

                // If auto-detect signaled Annex B mode (-1), switch permanently
                if (detectedNalLengthSize == -1)
                    nalLengthSize = 0;
            }
        }
        else
        {
            // Annex B format (start codes) - used by transport streams
            nalUnits = NalUnitParser.ParseAnnexB(data);
            nalUnitCount = nalUnits.Length;
        }
        
        NalUnitsProcessed += nalUnitCount;
        
        if (DiagnosticOutput != null)
        {
            var nalTypes = string.Join(",", nalUnits.Take(nalUnitCount).Select(n => n.Type.ToString()));
            DiagnosticOutput.WriteLine($"DECODE-CALL: nalCount={nalUnitCount} types=[{nalTypes}] dataLen={data.Length}");
        }
        
        // Process all NAL units in this access unit.
        // A single frame may contain multiple slices — we must decode them all
        // before applying deblocking or producing output.
        bool hasDecodedSlice = false;
        bool isIdrFrame = false;
        // h->first_field: a field pair's two halves are one access unit's worth of SEI, and
        // the second field must still see the recovery point the first one carried.
        if (!(currentFieldPicture && firstFieldDpbIndex >= 0))
            seiRecoveryFrameCount = -1;
        TimingRefListMs = 0;
        TimingCabacMs = 0;
        
        // nalUnits may be a reused buffer longer than the NALs in it, so walk the count.
        for (int ni = 0; ni < nalUnitCount; ni++)
        {
            var nal = nalUnits[ni];
            try
            {
            switch (nal.Type)
            {
                case NalUnitType.SequenceParameterSet:
                case NalUnitType.PictureParameterSet:
                    context.ProcessNalUnit(nal);
                    break;

                case NalUnitType.CodedSliceIdr:
                    DiagnosticOutput?.WriteLine($"IDR-NAL: isInit={isInitialized} activeSps={context.ActiveSps != null} activePps={context.ActivePps != null}");

                    // A slice numbered from macroblock zero cannot continue the picture
                    // already being decoded, so that picture ends here. See
                    // PeekFirstMbInSlice.
                    if (hasDecodedSlice && PeekFirstMbInSlice(nal) == 0)
                    {
                        var completed = FinishPicture(presentationTimeTicks, isIdrFrame);
                        if (completed != null)
                            outputQueue.Enqueue(completed.Value);
                        hasDecodedSlice = false;
                        isIdrFrame = false;
                    }

                    if (!isInitialized)
                    {
                        // For Annex-B streams, SPS/PPS arrive in-band. The slice header
                        // references a PPS which references an SPS — activating them here.
                        context.ActivatePpsFromSlice(nal.Payload.Span);
                        if (context.ActiveSps != null)
                        {
                            isInitialized = true;
                            if (currentFrameBuffer == null)
                                AllocateBuffers();
                        }
                    }
                    if (!isInitialized)
                        continue;
                    
                    if (!hasDecodedSlice)
                    {
                        DrainReorderBuffer();
                        // Don't ClearDpb here for field pictures — the second IDR field
                        // needs the first field's DPB entry to survive until DecodeSliceData
                        // can detect the complementary pair. FlushDpb in DecodeSliceData
                        // handles the actual clearing (with first-field protection).
                        bool hasUnpairedFirstField = false;
                        for (int i = 0; i < dpbCount; i++)
                        {
                            if (dpb[i].FieldPicture && dpb[i].Reference != ReferenceFrame.PictFrame)
                            {
                                hasUnpairedFirstField = true;
                                break;
                            }
                        }
                        if (!hasUnpairedFirstField)
                            ClearDpb();
                        
                        prevFrameNum = 0;
                        prevFrameNumOffset = 0;
                        Array.Fill(lastPocs, int.MinValue);
                        dynamicReorderDepth = 0;
                    }
                    
                    if (DecodeSliceData(nal, presentationTimeTicks, isIdr: true, isFirstSlice: !hasDecodedSlice))
                    {
                        DiagnosticOutput?.WriteLine($"IDR-DECODED: fn={currentFrameNum} poc={currentPoc} hasDecoded={hasDecodedSlice}");
                        hasDecodedSlice = true;
                        isIdrFrame = true;
                    }
                    break;

                case NalUnitType.CodedSliceNonIdr:
                case NalUnitType.CodedSliceDataPartitionA:
                    if (hasDecodedSlice && PeekFirstMbInSlice(nal) == 0)
                    {
                        var completed = FinishPicture(presentationTimeTicks, isIdrFrame);
                        if (completed != null)
                            outputQueue.Enqueue(completed.Value);
                        hasDecodedSlice = false;
                        isIdrFrame = false;
                    }

                    if (!isInitialized)
                    {
                        context.ActivatePpsFromSlice(nal.Payload.Span);
                        if (context.ActiveSps != null)
                        {
                            isInitialized = true;
                            if (currentFrameBuffer == null)
                                AllocateBuffers();
                        }
                    }
                    if (!isInitialized)
                        continue;
                    
                    if (DecodeSliceData(nal, presentationTimeTicks, isIdr: false, isFirstSlice: !hasDecodedSlice))
                        hasDecodedSlice = true;
                    break;

                case NalUnitType.SupplementalEnhancementInfo:
                    DecodeSei(nal.Payload.Span);
                    break;

                case NalUnitType.AccessUnitDelimiter:
                    // AU delimiter - marks boundary
                    break;
            }
            }
            catch (Exception ex)
            {
                DiagnosticOutput?.WriteLine($"NAL-EXCEPTION: type={nal.Type} ex={ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine($"NAL-EXCEPTION: type={nal.Type} ex={ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        if (hasDecodedSlice)
        {
            var finished = FinishPicture(presentationTimeTicks, isIdrFrame);
            if (finished != null)
                return finished;
        }

        if (outputQueue.Count > 0)
            return outputQueue.Dequeue();

        return null;
    }

    /// <summary>
    /// Completes the picture whose slices have just been decoded: finalises it, stores it as
    /// a reference if it is one, and either holds it as the first field of a pair or puts it
    /// through the reorder buffer.
    /// </summary>
    /// <remarks>
    /// Called at the end of an access unit, and ALSO from the middle of one when a slice
    /// starts a new picture - see the boundary test in the NAL loop.
    /// </remarks>
    private DecodedVideoFrame? FinishPicture(long presentationTimeTicks, bool isIdrFrame)
    {
        // Capture whether this is a second field BEFORE FinalizeFrame resets state
        bool isSecondField = currentFieldPicture && (firstFieldDpbIndex >= 0 || isNonRefComplement);

        FinalizeFrame();
        FramesDecoded++;

        if (currentFieldPicture && !isSecondField)
        {
            // First field of a complementary pair: don't output yet.
            // Reference fields are stored in DPB by FinalizeFrame/StoreReferenceFrame.
            // Non-reference fields need metadata saved for pairing.
            if (!currentIsReference)
            {
                pendingNonRefField = true;
                pendingNonRefFieldFrameNum = currentFrameNum;
                pendingNonRefFieldStructure = currentPictureStructure;
                pendingNonRefFieldTopPoc = currentTopFieldPoc;
                pendingNonRefFieldBottomPoc = currentBottomFieldPoc;
            }

            return null;
        }

        // Frame picture, or second field completing a pair - output now
        AddToReorderBuffer(presentationTimeTicks, isIdrFrame);
        return TryOutputReorderedFrame();
    }

    /// <summary>
    /// <c>first_mb_in_slice</c>, read without parsing the rest of the slice header. Zero
    /// means this slice starts a new picture - or the second field of one - and so ends the
    /// picture before it.
    /// </summary>
    /// <remarks>
    /// This is the test <c>decode_slice_header</c> makes, under its own comment "FIXME better
    /// field boundary detection": a slice numbered from macroblock zero cannot be a
    /// continuation of the picture already being decoded. Without it an access unit holding
    /// TWO pictures - which is what an MP4 carrying two fields per sample is, and what
    /// h264/mixed-nal-coding.mp4 is - decodes the second picture's slices into the first, and
    /// the result is no frames at all.
    /// </remarks>
    /// <returns>The value, or -1 if it could not be read.</returns>
    private int PeekFirstMbInSlice(NalUnit nal)
    {
        ReadOnlySpan<byte> payload = nal.Payload.Span;
        if (payload.IsEmpty)
            return -1;

        // The field is the first in the slice header and is at most a few bytes of
        // exp-Golomb, so a short prefix is enough - and it is unescaped properly rather than
        // read raw, because three bytes of it could be an emulation prevention sequence.
        int prefix = Math.Min(payload.Length, 24);
        NalUnitParser.RemoveEmulationPreventionBytesInto(
            payload[..prefix], ref peekRbsp, out int peekLength, ref peekRemoved, out _);

        if (peekLength == 0)
            return -1;

        try
        {
            var reader = new BitstreamReader(peekRbsp.AsSpan(0, peekLength));
            return (int)reader.ReadExpGolombUnsigned();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Decodes a single slice's macroblocks without finalizing the frame.
    /// Multiple slices may be decoded before FinalizeFrame is called.
    /// </summary>
    private bool DecodeSliceData(NalUnit nal, long pts, bool isIdr, bool isFirstSlice)
    {
        if (context.ActiveSps == null || context.ActivePps == null)
            return false;

        // Parse slice header
        // Unescape once here: the header parse and the slice data below both need this slice's
        // RBSP, and it was being copied for each.
        NalUnitParser.RemoveEmulationPreventionBytesInto(
            nal.Payload.Span, ref sliceRbsp, out sliceRbspLength, ref sliceRbspRemoved, out _);
        var sliceHeader = context.ParseSliceHeaderRbsp(nal, sliceRbsp.AsSpan(0, sliceRbspLength));
        if (sliceHeader == null)
            return false;

        // H.264 §7.4.3: redundant_pic_cnt > 0 indicates a redundant coded picture.
        // These are backup representations and must be discarded during normal decoding.
        if (sliceHeader.RedundantPictureCount > 0)
            return false;

        var sps = context.ActiveSps;
        var pps = context.ActivePps;

        if (isFirstSlice)
        {
            // currentPictureRecovered is NOT cleared here: in the C it lives on the picture
            // and a complementary field pair is one picture, so the second field must not
            // throw away what the first established. It is cleared where the picture is
            // handed to the reorder buffer.
            currentPictureGray = false;
        }

        if (isFirstSlice)
        {
            // Determine picture structure (frame vs field) FIRST — needed for
            // field pair detection which must happen before IDR DPB flush.
            currentIsReference = nal.ReferenceIdc > 0;
            currentIsIdr = isIdr;
            currentFieldPicture = sliceHeader.FieldPicture;
            // MBAFF: mb_adaptive_frame_field_flag in SPS + this is a frame picture (not field)
            isMbaffFrame = sps != null && sps.MbAdaptiveFrameField && !sliceHeader.FieldPicture;
            if (sliceHeader.FieldPicture)
                currentPictureStructure = sliceHeader.BottomField 
                    ? ReferenceFrame.PictBottomField 
                    : ReferenceFrame.PictTopField;
            else
                currentPictureStructure = ReferenceFrame.PictFrame;
            
            // For field pictures, check if this is the second field of a complementary pair
            // BEFORE any DPB flush. The second field of an IDR pair needs the first field's
            // DPB entry to survive the flush so it can share its buffer.
            if (currentFieldPicture)
            {
                FindComplementaryFieldInDpb(sliceHeader.FrameNumber);
                
                // Check for non-reference first field waiting for complement.
                // Non-reference fields aren't stored in the DPB, so FindComplementaryFieldInDpb
                // won't find them. We detect the complement via saved metadata instead.
                if (firstFieldDpbIndex < 0 && pendingNonRefField
                    && sliceHeader.FrameNumber == pendingNonRefFieldFrameNum)
                {
                    bool currentIsTop = currentPictureStructure == ReferenceFrame.PictTopField;
                    bool pendingIsTop = pendingNonRefFieldStructure == ReferenceFrame.PictTopField;
                    if (currentIsTop != pendingIsTop)
                    {
                        isNonRefComplement = true;
                        pendingNonRefField = false;
                        DiagnosticOutput?.WriteLine($"FIELD-PAIR-NONREF: complement detected fn={sliceHeader.FrameNumber} pending={pendingNonRefFieldStructure} current={currentPictureStructure}");
                    }
                }
                
                // Orphan: pending non-ref field that doesn't match this field
                if (!isNonRefComplement && pendingNonRefField)
                {
                    DiagnosticOutput?.WriteLine($"FIELD-PAIR-ORPHAN: non-ref first field fn={pendingNonRefFieldFrameNum} orphaned by fn={sliceHeader.FrameNumber}");
                    pendingNonRefField = false;
                }
            }
            else
            {
                firstFieldDpbIndex = -1;
                pendingNonRefField = false;
                isNonRefComplement = false;
            }
            
            bool isSecondFieldOfPair = currentFieldPicture && firstFieldDpbIndex >= 0;
            
            // H.264 spec 8.2.5.1: IDR flushes all reference pictures from the DPB.
            // Skip for second field of a complementary pair — the first field already
            // flushed the DPB, and its entry must survive for buffer sharing.
            if (isIdr && !isSecondFieldOfPair)
                FlushDpb();
            
            // h264_field_start: shorten the gap before walking it, and do not walk it at
            // all while a first field is waiting for its pair.
            ShortenFrameNumGap(sps, sliceHeader.FrameNumber);

            // H.264 spec 8.2.5.2: Handle gaps in frame_num
            if (!isIdr && !isSecondFieldOfPair)
                HandleFrameNumGap(sps, sliceHeader.FrameNumber);
            
            // A new picture starts with nothing readable. Reset rather than replace: allocating
            // one of these per frame would be a per-frame allocation, which the decoder does not
            // do. Frame threading will need one per picture in flight, taken from a pool.
            if (!isSecondFieldOfPair)
            {
                currentProgress.Reset();

                // h264_frame_start: pic->f->pict_type = h->slice_ctx[0].slice_type. It is
                // the FIRST slice's type and it is set once per picture, so a picture whose
                // first slice is I is an I picture whatever its later slices say, and the
                // second field of a pair inherits the first field's. Testing every slice
                // instead silently withheld the random access point on any multi-slice
                // picture, and a file ffmpeg decodes three frames of produced none.
                currentPictureIsIntra = sliceHeader.Type == SliceType.ISlice
                                     || sliceHeader.Type == SliceType.SiSlice;
            }

            int poc = CalculatePoc(sps, sliceHeader, isIdr);
            currentPoc = poc;
            currentFrameNum = sliceHeader.FrameNumber;
            currentPts = pts;

            if (DiagnosticOutput != null)
                DiagnosticOutput.WriteLine($"FRAME-START: isIdr={isIdr} refIdc={nal.ReferenceIdc} fn={sliceHeader.FrameNumber} poc={currentPoc} dpbCount={dpbCount} sliceType={sliceHeader.Type} pictStruct={currentPictureStructure} fieldPic={currentFieldPicture}");

            if (currentFrameBuffer == null)
            {
                AllocateBuffers();
            }
            else if (firstFieldDpbIndex < 0 && !isNonRefComplement)
            {
                // New picture (not a complementary second field): allocate a fresh frame buffer.
                // After decoding a second field, currentFrameBuffer points to a DPB buffer;
                // reusing it would corrupt the reference. Always rent fresh for safety.
                int width = context.CodedWidth;
                int height = context.CodedHeight;
                int lumaPixels = width * height;
                int chromaPixels = isChroma422 ? lumaPixels : lumaPixels / 2;
                int bps = 1 + pixelShift;
                int frameSizeBytes = (lumaPixels + chromaPixels) * bps;
                ReleaseCurrentFrameBuffer();
                currentFrameBuffer = RentBuffer(frameSizeBytes);
                ownsCurrentFrameBuffer = true;
                Array.Clear(currentFrameBuffer);
                if (pixelShift == 0)
                {
                    currentFrameBuffer.AsSpan(lumaPixels, chromaPixels).Fill(128);
                }
                else
                {
                    var chromaSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                        currentFrameBuffer.AsSpan(lumaPixels * bps, chromaPixels * bps));
                    chromaSpan.Fill((ushort)neutralChroma);
                }
            }
            
            // Clear per-frame macroblock metadata so partially-decoded frames
            // don't leave stale neighbor data for the deblocking filter
            if (frameMacroblocks != null)
                Array.Clear(frameMacroblocks);
        }

        // Build reference picture lists for this slice
        currentSliceHeader = sliceHeader;
        activeScan4x4 = sliceHeader.FieldPicture ? FieldScan4x4 : ZigzagScan4x4;
        // Timestamps, not Stopwatch instances. Stopwatch.StartNew allocates an object, and these
        // two run per slice - eighty bytes a slice for timing that is on whether or not anyone
        // reads it. GetTimestamp is a static returning a long and measures the same thing.
        long refListStart = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildReferencePictureLists(sliceHeader);
        
        // Compute implicit weight table for B-slices with weighted_bipred_idc == 2
        useImplicitWeightedPred = sliceHeader.IsBSlice && pps!.WeightedBiprediction == 2;
        if (useImplicitWeightedPred)
            ComputeImplicitWeightTable();
        
        // Diagnostic: allow disabling all weighted prediction for isolation testing
        if (SkipWeightedPred)
        {
            useImplicitWeightedPred = false;
            sliceHeader.HasWeightedPred = false;
        }
        
        // Diagnostic: dump slice header WP info for first few P-slices
        if (DiagnosticOutput != null && sliceHeader.IsPSlice)
        {
            DiagnosticOutput.WriteLine($"P-SLICE: frame_num={sliceHeader.FrameNumber} poc={currentPoc} NumRefIdxL0Active={sliceHeader.NumRefIdxL0ActiveMinus1+1} override={sliceHeader.NumRefIdxActiveOverride} dpbCount={dpbCount} isFirstSlice={isFirstSlice} isRef={currentIsReference}");
            DiagnosticOutput.WriteLine($"  WP: HasWeightedPred={sliceHeader.HasWeightedPred} LumaLog2Denom={sliceHeader.LumaLog2WeightDenom}");
            for (int ri = 0; ri <= sliceHeader.NumRefIdxL0ActiveMinus1 && ri < refPicListL0Count; ri++)
                DiagnosticOutput.WriteLine($"  L0[{ri}]: poc={refPicListL0Poc[ri]} fn={refPicListL0FrameNum[ri]} w={sliceHeader.LumaWeightL0[ri]} o={sliceHeader.LumaOffsetL0[ri]}");
        }
        if (DiagnosticOutput != null && sliceHeader.IsBSlice)
        {
            DiagnosticOutput.WriteLine($"B-SLICE: frame_num={sliceHeader.FrameNumber} poc={currentPoc} NumRefL0={sliceHeader.NumRefIdxL0ActiveMinus1+1} NumRefL1={sliceHeader.NumRefIdxL1ActiveMinus1+1} implicitWP={useImplicitWeightedPred} HasWP={sliceHeader.HasWeightedPred} wbi={pps!.WeightedBiprediction} directSpatial={sliceHeader.DirectSpatialMvPrediction} fieldPic={currentFieldPicture} struct={currentPictureStructure}");
            var sb = new System.Text.StringBuilder();
            sb.Append("  L0=[");
            for (int ri = 0; ri < refPicListL0Count; ri++)
            {
                if (ri > 0) sb.Append(',');
                sb.Append($"POC{refPicListL0Poc[ri]}");
                if (currentFieldPicture) sb.Append(refPicListL0IsTopField[ri] ? "T" : "B");
            }
            sb.Append("] L1=[");
            for (int ri = 0; ri < refPicListL1Count; ri++)
            {
                if (ri > 0) sb.Append(',');
                sb.Append($"POC{refPicListL1Poc[ri]}");
                if (currentFieldPicture) sb.Append(refPicListL1IsTopField[ri] ? "T" : "B");
            }
            sb.Append($"] dpbCount={dpbCount}");
            DiagnosticOutput.WriteLine(sb.ToString());
            // Dump DPB contents for debugging reference list construction
            var dbg = new System.Text.StringBuilder("  DPB: ");
            for (int di = 0; di < dpbCount; di++)
                dbg.Append($"[{di}]poc={dpb[di].Poc} fn={dpb[di].FrameNum} lt={dpb[di].IsLongTerm} lti={dpb[di].LongTermFrameIdx} ");
            DiagnosticOutput.WriteLine(dbg.ToString());
            // Dump weight tables for B-slice explicit WP
            if (sliceHeader.HasWeightedPred)
            {
                DiagnosticOutput.Write($"  WP-LUMA: log2Denom={sliceHeader.LumaLog2WeightDenom}");
                for (int ri = 0; ri <= sliceHeader.NumRefIdxL0ActiveMinus1; ri++)
                    DiagnosticOutput.Write($" L0[{ri}]=(w={sliceHeader.LumaWeightL0[ri]},o={sliceHeader.LumaOffsetL0[ri]})");
                for (int ri = 0; ri <= sliceHeader.NumRefIdxL1ActiveMinus1; ri++)
                    DiagnosticOutput.Write($" L1[{ri}]=(w={sliceHeader.LumaWeightL1[ri]},o={sliceHeader.LumaOffsetL1[ri]})");
                DiagnosticOutput.WriteLine();
                DiagnosticOutput.Write($"  WP-CHROMA: log2Denom={sliceHeader.ChromaLog2WeightDenom}");
                for (int ri = 0; ri <= sliceHeader.NumRefIdxL0ActiveMinus1; ri++)
                    DiagnosticOutput.Write($" L0[{ri}]=(wCb={sliceHeader.ChromaWeightL0[ri,0]},oCb={sliceHeader.ChromaOffsetL0[ri,0]},wCr={sliceHeader.ChromaWeightL0[ri,1]},oCr={sliceHeader.ChromaOffsetL0[ri,1]})");
                for (int ri = 0; ri <= sliceHeader.NumRefIdxL1ActiveMinus1; ri++)
                    DiagnosticOutput.Write($" L1[{ri}]=(wCb={sliceHeader.ChromaWeightL1[ri,0]},oCb={sliceHeader.ChromaOffsetL1[ri,0]},wCr={sliceHeader.ChromaWeightL1[ri,1]},oCr={sliceHeader.ChromaOffsetL1[ri,1]})");
                DiagnosticOutput.WriteLine();
            }
        }
        TimingRefListMs += ElapsedMs(refListStart);
        
        // Decode all macroblocks in this slice (CABAC + reconstruction interleaved)
        long cabacStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // One test per slice picks the shape, and every macroblock below is decoded by a body
        // the JIT has already folded it into. FFmpeg compiles h264_mb_template.c once per shape
        // for the same reason; a generic over a struct is instantiated per type argument, so
        // TShape.Mbaff is a literal inside and the dead half is deleted.
        if (isMbaffFrame)
            DecodeSliceMacroblocks<MbaffShape>(nal, sliceHeader, sps, pps);
        else
            DecodeSliceMacroblocks<ProgressiveShape>(nal, sliceHeader, sps, pps);
        TimingCabacMs += ElapsedMs(cabacStart);
        
        DiagnosticOutput?.WriteLine($"SLICE-DIAG: type={sliceHeader.Type} fn={sliceHeader.FrameNumber} firstMb={sliceHeader.FirstMacroblockAddress} decoded={CabacMbsDecoded}/{CabacTotalMbs} exit={CabacExitReason} ex={CabacFirstException?.Message} bitOff={sliceHeader.SliceDataBitOffset} constrainedIP={pps.ConstrainedIntraPrediction} t8x8={pps.Transform8x8Mode} profile={sps.Profile} cabac={pps.EntropyCodingMode} mbW={context.WidthInMacroblocks} mbH={context.HeightInMacroblocks} fieldPic={currentFieldPicture}");
        return true;
    }
    
    /// <summary>
    /// Applies deblocking filter and stores reference frame after all slices are decoded.
    /// </summary>
    private void FinalizeFrame()
    {
        var pps = context.ActivePps;
        
        if (DiagnosticOutput != null)
            DiagnosticOutput.WriteLine($"FINALIZE: poc={currentPoc} isRef={currentIsReference} bufNull={currentFrameBuffer == null}");
        

        
        // Dump MB type grid for diagnostic target POC
        if (DiagnosticOutput != null && DiagMbGridPoc >= 0 && currentPoc == DiagMbGridPoc && cabacMbTypes != null)
        {
            int mbW = context.WidthInMacroblocks;
            int mbH = context.HeightInMacroblocks;
            DiagnosticOutput.WriteLine($"MB-GRID poc={currentPoc} ({mbW}x{mbH}):");
            for (int my = 0; my < mbH; my++)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{my * 16,3} ");
                for (int mx = 0; mx < mbW; mx++)
                {
                    int addr = my * mbW + mx;
                    int t = cabacMbTypes[addr];
                    bool isIntra = mbIsIntra != null && addr < mbIsIntra.Length && mbIsIntra[addr];
                    string sym;
                    if (t == -1) sym = "d "; // B_Skip  
                    else if (isIntra && cabacIsIntra16x16 != null && cabacIsIntra16x16[addr]) sym = "I ";
                    else if (isIntra) sym = "i ";
                    else if (t == 0) sym = "D0"; // B_Direct_16x16
                    else if (t == 1) sym = "> "; // B_L0_16x16
                    else if (t == 2) sym = "< "; // B_L1_16x16
                    else if (t == 3) sym = "X+"; // B_Bi_16x16
                    else if (t >= 4 && t <= 5) sym = ">|"; // B_L0_L0_16x8, 8x16
                    else if (t >= 6 && t <= 7) sym = "<|"; // B_L1_L1
                    else if (t >= 8 && t <= 11) sym = "XM"; // B_L0_L1 or B_L1_L0 mixed
                    else if (t >= 12 && t <= 21) sym = "XP"; // Various Bi-mixed partitions
                    else if (t == 22) sym = "88"; // B_8x8
                    else sym = $"{t,2}";
                    sb.Append(sym);
                }
                DiagnosticOutput.WriteLine($"MB-GRID {sb}");
            }
        }
        
        // Apply deblocking filter
        long deblockStart = System.Diagnostics.Stopwatch.GetTimestamp();
        DeblockingFilter.DeblockCallCount++;
        if (!SkipDeblocking && pps != null)
        {
            // Diagnostic: capture pixel (0,0) before deblocking
            byte preDeblock = 0;
            if (DiagnosticDumpAllMbsForPoc == currentPoc && currentFrameBuffer != null && DiagnosticOutput != null)
                preDeblock = currentFrameBuffer[0];
            
            ApplyDeblocking(pps);
            
            // Post-deblock diagnostic for field pair


        }
        TimingDeblockMs = ElapsedMs(deblockStart);
#if MK_STAGE_TIMING
        StageDeblockTicks += Stopwatch.GetTimestamp() - deblockStart;
#endif

        MarkPictureRecovered();

        // Store as reference if needed
        if (currentIsReference)
        {
        // Deblocking is finished, so every row of this picture is now readable.
        currentProgress.MarkComplete();

            StoreReferenceFrame();
        }
        else if (isNonRefComplement)
        {
            // Non-reference second field: merge POCs from the pending first field
            // so the reorder buffer uses the correct combined display-order POC.
            if (currentPictureStructure == ReferenceFrame.PictTopField)
            {
                // We decoded top field; pending had bottom field
                currentBottomFieldPoc = pendingNonRefFieldBottomPoc;
            }
            else
            {
                // We decoded bottom field; pending had top field
                currentTopFieldPoc = pendingNonRefFieldTopPoc;
            }
            currentPoc = Math.Min(currentTopFieldPoc, currentBottomFieldPoc);
            isNonRefComplement = false;
            DiagnosticOutput?.WriteLine($"FINALIZE-NONREF-MERGE: topPoc={currentTopFieldPoc} botPoc={currentBottomFieldPoc} combinedPoc={currentPoc}");
        }
        

    }

    private int CalculatePoc(SequenceParameterSet sps, SliceHeader sliceHeader, bool isIdr)
    {
        int topFieldPoc, bottomFieldPoc;
        switch (sps.PictureOrderCountType)
        {
            case 0: CalculatePocType0(sps, sliceHeader, isIdr, out topFieldPoc, out bottomFieldPoc); break;
            case 1: CalculatePocType1(sps, sliceHeader, isIdr, out topFieldPoc, out bottomFieldPoc); break;
            case 2: CalculatePocType2(sps, sliceHeader, isIdr, out topFieldPoc, out bottomFieldPoc); break;
            default: topFieldPoc = 0; bottomFieldPoc = 0; break;
        }
        
        // For field pictures, only the decoded field's POC is meaningful.
        // The other field's POC stays at int.MaxValue until it's decoded.
        if (currentPictureStructure == ReferenceFrame.PictTopField)
        {
            currentTopFieldPoc = topFieldPoc;
            currentBottomFieldPoc = int.MaxValue;
        }
        else if (currentPictureStructure == ReferenceFrame.PictBottomField)
        {
            currentTopFieldPoc = int.MaxValue;
            currentBottomFieldPoc = bottomFieldPoc;
        }
        else
        {
            currentTopFieldPoc = topFieldPoc;
            currentBottomFieldPoc = bottomFieldPoc;
        }
        
        int poc = Math.Min(currentTopFieldPoc, currentBottomFieldPoc);
        
        DiagnosticOutput?.WriteLine($"POC-CALC: type={sps.PictureOrderCountType} fn={sliceHeader.FrameNumber} isIdr={isIdr} poc={poc} topPoc={currentTopFieldPoc} botPoc={currentBottomFieldPoc} pictStruct={currentPictureStructure} delta0={sliceHeader.DeltaPictureOrderCount0} delta1={sliceHeader.DeltaPictureOrderCount1}");
        return poc;
    }

    /// <summary>POC type 0: pic_order_cnt_lsb + MSB wrapping (H.264 spec 8.2.1.1).
    /// Matches FFmpeg's ff_h264_init_poc() — IDR uses a sentinel prev_poc_lsb = -1
    /// so that the regular wrapping logic produces correct results without special-casing.
    /// </summary>
    private void CalculatePocType0(SequenceParameterSet sps, SliceHeader sliceHeader, bool isIdr,
        out int topFieldPoc, out int bottomFieldPoc)
    {
        int log2MaxPocLsb = sps.Log2MaxPictureOrderCountLsb;
        int maxPocLsb = 1 << log2MaxPocLsb;
        int pocLsb = (int)sliceHeader.PictureOrderCountLsb;

        if (isIdr)
        {
            prevPocMsb = 1 << 16;
            prevPocLsb = pocLsb;
        }

        // ff_h264_init_poc: the sentinel means there is no previous picture to wrap against,
        // so this one becomes its own reference point.
        if (prevPocLsb < 0)
            prevPocLsb = pocLsb;

        int pocMsb;
        if (pocLsb < prevPocLsb && (prevPocLsb - pocLsb) >= maxPocLsb / 2)
            pocMsb = prevPocMsb + maxPocLsb;
        else if (pocLsb > prevPocLsb && (pocLsb - prevPocLsb) > maxPocLsb / 2)
            pocMsb = prevPocMsb - maxPocLsb;
        else
            pocMsb = prevPocMsb;

        // H.264 spec 8.2.1.1: TopFieldOrderCnt = PicOrderCntMsb + pic_order_cnt_lsb
        // BottomFieldOrderCnt = TopFieldOrderCnt + delta_pic_order_cnt_bottom (for frames)
        // For field pictures, only the active field's POC is valid.
        topFieldPoc = pocMsb + pocLsb;
        bottomFieldPoc = pocMsb + pocLsb;
        if (currentPictureStructure == ReferenceFrame.PictFrame)
            bottomFieldPoc += sliceHeader.DeltaPicOrderCountBottom;

        if (currentIsReference)
        {
            prevPocMsb = pocMsb;
            prevPocLsb = pocLsb;
        }

        // Update prevFrameNum for frame_num gap detection (spec 8.2.5.2).
        prevFrameNum = sliceHeader.FrameNumber;
    }

    /// <summary>POC type 1: frame_num based with delta offsets (H.264 spec 8.2.1.2).</summary>
    private void CalculatePocType1(SequenceParameterSet sps, SliceHeader sliceHeader, bool isIdr,
        out int topFieldPoc, out int bottomFieldPoc)
    {
        int frameNum = sliceHeader.FrameNumber;
        int maxFrameNum = sps.MaxFrameNumber;

        int frameNumOffset;
        if (isIdr)
        {
            frameNumOffset = 0;
        }
        else if (prevFrameNum > frameNum)
        {
            frameNumOffset = prevFrameNumOffset + maxFrameNum;
        }
        else
        {
            frameNumOffset = prevFrameNumOffset;
        }

        int absFrameNum;
        if (sps.NumRefFramesInPocCycle != 0)
            absFrameNum = frameNumOffset + frameNum;
        else
            absFrameNum = 0;

        if (!currentIsReference && absFrameNum > 0)
            absFrameNum--;

        int expectedPoc = 0;
        if (absFrameNum > 0)
        {
            int expectedDeltaPerCycle = 0;
            for (int i = 0; i < sps.NumRefFramesInPocCycle; i++)
                expectedDeltaPerCycle += sps.OffsetForRefFrame![i];

            int picOrderCntCycleCnt = (absFrameNum - 1) / sps.NumRefFramesInPocCycle;
            int frameNumInCycle = (absFrameNum - 1) % sps.NumRefFramesInPocCycle;

            expectedPoc = picOrderCntCycleCnt * expectedDeltaPerCycle;
            for (int i = 0; i <= frameNumInCycle; i++)
                expectedPoc += sps.OffsetForRefFrame![i];
        }

        if (!currentIsReference)
            expectedPoc += sps.OffsetForNonRefPic;

        // H.264 spec 8.2.1.2:
        // TopFieldOrderCnt = expectedPoc + delta_pic_order_cnt[0]
        // BottomFieldOrderCnt = TopFieldOrderCnt + offset_for_top_to_bottom_field + delta_pic_order_cnt[1]
        topFieldPoc = expectedPoc + sliceHeader.DeltaPictureOrderCount0;
        bottomFieldPoc = topFieldPoc + sps.OffsetForTopToBottomField;
        if (currentPictureStructure == ReferenceFrame.PictFrame)
            bottomFieldPoc += sliceHeader.DeltaPictureOrderCount1;

        prevFrameNumOffset = frameNumOffset;
        prevFrameNum = frameNum;
    }

    /// <summary>POC type 2: derived directly from frame_num (H.264 spec 8.2.1.3).</summary>
    private void CalculatePocType2(SequenceParameterSet sps, SliceHeader sliceHeader, bool isIdr,
        out int topFieldPoc, out int bottomFieldPoc)
    {
        int frameNum = sliceHeader.FrameNumber;
        int maxFrameNum = sps.MaxFrameNumber;

        int frameNumOffset;
        if (isIdr)
        {
            frameNumOffset = 0;
        }
        else if (prevFrameNum > frameNum)
        {
            frameNumOffset = prevFrameNumOffset + maxFrameNum;
        }
        else
        {
            frameNumOffset = prevFrameNumOffset;
        }

        int poc;
        if (isIdr)
            poc = 0;
        else if (!currentIsReference)
            poc = 2 * (frameNumOffset + frameNum) - 1;
        else
            poc = 2 * (frameNumOffset + frameNum);

        topFieldPoc = poc;
        bottomFieldPoc = poc;

        prevFrameNumOffset = frameNumOffset;
        prevFrameNum = frameNum;
    }

    private void DecodeSliceMacroblocks<TShape>(NalUnit nal, SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps)
        where TShape : struct, IPictureShape
    {
        int mbWidth = context.WidthInMacroblocks;
        int mbHeight = context.HeightInMacroblocks;
        int startMb = (int)sliceHeader.FirstMacroblockAddress;
        int totalMb = context.TotalMacroblocks;
        
        // For field pictures, the picture height is half the frame height.
        // H.264 spec 7.4.3: PicHeightInMbs = FrameHeightInMbs / (1 + field_pic_flag)
        if (currentFieldPicture)
        {
            mbHeight /= 2;
            totalMb = mbWidth * mbHeight;
        }
        
        constrainedIntraPred = pps.ConstrainedIntraPrediction;

        // Already unescaped for the header parse; reuse it rather than making a second copy of the
        // slice, which at 720p is kilobytes every frame.
        byte[] rbsp = sliceRbsp!;

        if (pps.EntropyCodingMode)
        {
            DecodeWithCabac<TShape>(rbsp, sliceRbspLength, sliceHeader, sps, pps, startMb, totalMb, mbWidth);
        }
        else
        {
            DecodeWithCavlc<TShape>(rbsp, sliceRbspLength, sliceHeader, sps, pps, startMb, totalMb, mbWidth);
        }
    }

    private void DecodeWithCavlc<TShape>(byte[] rbsp, int rbspLength, SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps, int startMb, int totalMb, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // CAVLC slice data starts at exact bit position (no byte alignment like CABAC)
        int bitOffset = sliceHeader.SliceDataBitOffset;
        int byteOffset = bitOffset / 8;
        int subByteBits = bitOffset % 8;
        var decoder = new CavlcDecoder(rbsp.AsSpan(byteOffset, rbspLength - byteOffset));
        if (subByteBits > 0)
            decoder.ReadBits(subByteBits);  // skip sub-byte remainder

        // Initialize intra4x4 modes to DC(2) for first slice so non-I4 neighbors return correct default
        if (startMb == 0)
        {
            Array.Fill(mbIntra4x4Modes!, (byte)2);
            if (mbIsIntra != null) Array.Clear(mbIsIntra);
            currentSliceIdx = 0;
            Array.Fill(mbSliceIndex!, -1);
            // Clear MB type tracking (shared with CABAC path) so B-direct neighbor
            // ref override in DeriveBDirectSpatialMotionVectors doesn't fire on stale zeros
            Array.Clear(cabacMbTypes);
            // Clear boundary ref/MV arrays so PrepareRefCache/PrepareMvCache reads correct neighbor data.
            // Without this, intra MBs retain stale ref=0 from allocation or previous frames,
            // causing MBAFF cross-type diagonal to return wrong refs for intra neighbors.
            if (cabacRefL0Right != null) Array.Clear(cabacRefL0Right);
            if (cabacRefL0Bottom != null) Array.Clear(cabacRefL0Bottom);
            if (cabacRefL1Right != null) Array.Clear(cabacRefL1Right);
            if (cabacRefL1Bottom != null) Array.Clear(cabacRefL1Bottom);
            if (mbMvL0XRight != null) Array.Clear(mbMvL0XRight);
            if (mbMvL0YRight != null) Array.Clear(mbMvL0YRight);
            if (mbMvL0XBottom != null) Array.Clear(mbMvL0XBottom);
            if (mbMvL0YBottom != null) Array.Clear(mbMvL0YBottom);
            if (mbMvL1XRight != null) Array.Clear(mbMvL1XRight);
            if (mbMvL1YRight != null) Array.Clear(mbMvL1YRight);
            if (mbMvL1XBottom != null) Array.Clear(mbMvL1XBottom);
            if (mbMvL1YBottom != null) Array.Clear(mbMvL1YBottom);
            if (mbRefIdxL0 != null) Array.Fill(mbRefIdxL0, (sbyte)-1);
            if (mbRefIdxL1 != null) Array.Fill(mbRefIdxL1, (sbyte)-1);
            if (deblockMotionUniform != null) Array.Clear(deblockMotionUniform);
            if (deblockMvL0 != null) Array.Clear(deblockMvL0);
            if (deblockMvL1 != null) Array.Clear(deblockMvL1);
            if (deblockRefL0 != null) Array.Fill(deblockRefL0, (sbyte)-1);
            if (deblockRefL1 != null) Array.Fill(deblockRefL1, (sbyte)-1);
            if (deblockRefPicL0 != null) Array.Fill(deblockRefPicL0, -1);
            if (deblockRefPicL1 != null) Array.Fill(deblockRefPicL1, -1);
            if (mbFieldDecodingFlag != null) Array.Clear(mbFieldDecodingFlag);
        }

        // Diagnostic: dump first 16 RBSP bytes at CAVLC data start for MB 0 verification
        if (DiagnosticOutput != null && startMb == 0)
        {
            DiagnosticOutput.Write($"CAVLC-START: bitOff={bitOffset} byteOff={byteOffset} subBits={subByteBits} rbspLen={rbspLength} bytes=");
            for (int d = 0; d < 40 && byteOffset + d < rbspLength; d++)
                DiagnosticOutput.Write($"{rbsp[byteOffset + d]:X2} ");
            DiagnosticOutput.WriteLine();
        }

        var mbDecoder = new MacroblockDecoder(sps, pps, sliceHeader, coefficientBuffer.AsSpan(), mbIntra4x4Modes.AsSpan(), mbIsIntra.AsSpan());

        Span<short> residualLuma = residualLumaBuffer.AsSpan();
        Span<short> residualChroma = residualChromaBuffer.AsSpan();
        Span<byte> allNzCounts = neighborNonZeroCounts.AsSpan();

        bool isInterSlice = sliceHeader.Type != SliceType.ISlice && sliceHeader.Type != SliceType.SiSlice;
        int mbAddr = startMb;
        currentSliceIdx++;
        
        // Store per-slice deblocking parameters for later ApplyDeblocking use
        sliceDeblockIdc![currentSliceIdx] = sliceHeader.DisableDeblockingFilterIdc;
        sliceFilterOffsetA![currentSliceIdx] = sliceHeader.SliceAlphaC0OffsetDiv2 * 2;
        sliceFilterOffsetB![currentSliceIdx] = sliceHeader.SliceBetaOffsetDiv2 * 2;
        
        int prevQp = sliceHeader.GetSliceQp(pps, qpBdOffset) + qpBdOffset;

        if (DiagnosticOutput != null)
            DiagnosticOutput.WriteLine($"CAVLC-MBAFF: isMbaff={isMbaffFrame} isInter={isInterSlice} totalMb={totalMb} mbWidth={mbWidth}");

        if (isMbaffFrame)
        {
            // MBAFF pair-based CAVLC loop: process top MB then bottom MB per pair.
            // Maps to FFmpeg h264_slice.c:2675-2750 MBAFF CAVLC loop.
            int startMbX = startMb % mbWidth;
            int startMbY = (startMb / mbWidth) * 2;
            int mbHeight = context.HeightInMacroblocks;
            int mbSkipRun = -1;

            mbaffCurrentFieldFlag = false;
            mbaffPrevMbSkipped = false;

            int mbX = startMbX;
            int mbY = startMbY;

            while (mbY < mbHeight && (decoder.HasMoreRbspData || mbSkipRun > 0))
            {
                int topMbAddr = mbY * mbWidth + mbX;
                int bottomMbAddr = (mbY + 1) * mbWidth + mbX;

                // --- Process TOP MB ---
                DecodeCavlcMbMbaff<TShape>(ref decoder, ref mbDecoder, sliceHeader, sps, pps,
                    topMbAddr, mbX, mbY, mbWidth, isInterSlice, ref mbSkipRun, ref prevQp,
                    residualLuma, residualChroma, allNzCounts);

                // --- Process BOTTOM MB ---
                if (!decoder.HasMoreRbspData && mbSkipRun <= 0)
                    break;
                DecodeCavlcMbMbaff<TShape>(ref decoder, ref mbDecoder, sliceHeader, sps, pps,
                    bottomMbAddr, mbX, mbY + 1, mbWidth, isInterSlice, ref mbSkipRun, ref prevQp,
                    residualLuma, residualChroma, allNzCounts);

                // Advance pair
                mbX++;
                if (mbX >= mbWidth)
                {
                    mbX = 0;
                    mbY += 2;
                    if (mbY < mbHeight)
                        PredictFieldDecodingFlag(mbX, mbY, mbWidth);
                }

                if (!decoder.HasMoreRbspData && mbSkipRun <= 0)
                    break;
            }
        }
        else
        {
        // Non-MBAFF: existing linear loop (unchanged)
        while (mbAddr < totalMb && decoder.HasMoreRbspData)
        {
            // Per H.264 spec 7.3.4: for P/B slices, read mb_skip_run before each coded MB
            if (isInterSlice)
            {
                uint mbSkipRun = decoder.ReadExpGolombUnsigned();

                // Process skipped macroblocks(zero residual, copy-from-reference or zero-MV)
                for (uint i = 0; i < mbSkipRun && mbAddr < totalMb; i++)
                {
                    int mbX = mbAddr % mbWidth;
                    int mbY = mbAddr / mbWidth;

                    var skippedMb = new Macroblock
                    {
                        Address = mbAddr,
                        PositionX = mbX,
                        PositionY = mbY,
                        IsSkipped = true,
                        IsIntra = false,
                        QuantizationParameter = prevQp,
                        InterPartitionMode = -1,
                        BSliceMbType = -1
                    };

                    // Clear NZC for skipped MB
                    allNzCounts.Slice(mbAddr * nzCountsPerMb, nzCountsPerMb).Clear();
                    residualLuma.Clear();
                    residualChroma.Clear();
                    mbIsIntra![mbAddr] = false;
                    mbSliceIndex![mbAddr] = currentSliceIdx;
                    cabacMbTypes![mbAddr] = -1; // Mark skip MB for B-direct neighbor override

                    // Derive skip motion vector and fill caches (matching CABAC path)
                    PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                    PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                    
                    if (sliceHeader.IsBSlice)
                    {
                        PrepareDirectCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                        if (sliceHeader.DirectSpatialMvPrediction)
                            DeriveBDirectSpatialMotionVectors<TShape>(mbAddr);
                        else
                            DeriveBDirectTemporalMotionVectors<TShape>(mbAddr);
                        FillDirectCache(Scan8[0], 4, 4, 1);
                        StoreDirectBoundaries(mbAddr);
                    }
                    else
                    {
                        DerivePSkipMotionVector<TShape>(mbAddr);
                        FillRefCache(Scan8[0], 4, 4, 0);
                        FillRefCacheL1(Scan8[0], 4, 4, -1);
                    }
                    StoreMvBoundaries(mbAddr);
                    StoreRefBoundaries(mbAddr);
                    StoreBlockDataForDeblocking<TShape>(mbAddr);

                    #if MK_STAGE_TIMING
                    long _stage6 = Stopwatch.GetTimestamp();
                    #endif
                    ReconstructMacroblock<TShape>(ref skippedMb, mbX, mbY, sps, residualLuma, residualChroma);
                    #if MK_STAGE_TIMING
                    StageReconstructTicks += Stopwatch.GetTimestamp() - _stage6;
                    #endif

                    if (frameMacroblocks != null && mbAddr < frameMacroblocks.Length)
                        frameMacroblocks[mbAddr] = skippedMb;

                    mbAddr++;
                }

                // After skip run, check if more data remains
                if (mbAddr >= totalMb || !decoder.HasMoreRbspData)
                {
                    break;
                }
            }

            // Decode the next coded macroblock
            int curMbX = mbAddr % mbWidth;
            int curMbY = mbAddr / mbWidth;
            mbSliceIndex![mbAddr] = currentSliceIdx;

            #if MK_STAGE_TIMING
            long _stage1 = Stopwatch.GetTimestamp();
            #endif
            mbDecoder.DecodeMacroblockCavlc(
                ref decoder,
                mbAddr,
                allNzCounts,
                ref prevQp,
                out Macroblock mb,
                residualLuma,
                residualChroma);
            #if MK_STAGE_TIMING
            StageEntropyTicks += Stopwatch.GetTimestamp() - _stage1;
            #endif

            // Fill MV/ref cachesbased on MB type (matching CABAC path)
            if (!mb.IsIntra && mb.BSliceMbType >= 0)
            {
                // B-slice inter MB: use B-slice processing
                ProcessCavlcBSliceInterMacroblock<TShape>(ref mb, mbAddr, curMbX, curMbY, mbWidth);
            }
            else if (!mb.IsIntra && mb.InterPartitionMode >= 0)
            {
                ProcessCavlcInterMacroblock<TShape>(ref mb, mbAddr, curMbX, curMbY, mbWidth);
            }
            else
            {
                // Intra MB: clear caches so future neighbors see MV=0, ref=-1
                PrepareRefCache<TShape>(mbAddr, curMbX, curMbY, mbWidth);
                PrepareMvCache<TShape>(mbAddr, curMbX, curMbY, mbWidth);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
                FillRefCache(Scan8[0], 4, 4, -1);
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                ClearMvBoundaries(mbAddr);
                ClearRefBoundaries(mbAddr);
                ClearDirectBoundaries(mbAddr);
                ClearBlockDataForDeblocking(mbAddr);
            }

            // Track MB type for B-direct neighbor ref override (shared with CABAC path).
            // B-slice inter: store raw B-slice mb_type (0=Direct, 1-22=coded).
            // Intra/P-slice: store sentinel that won't match -1 (skip) or 0 (Direct).
            mbIsIntra![mbAddr] = mb.IsIntra;
            if (!mb.IsIntra && mb.BSliceMbType >= 0)
                cabacMbTypes![mbAddr] = mb.BSliceMbType;
            else
                cabacMbTypes![mbAddr] = 99;

            // Diagnostic: dump intra modes and pre-reconstruction residual for MB 0
            if (DiagnosticOutput != null && mbAddr == 0 && mb.IsIntra && mb.IntraType == IntraMacroblockType.Intra4x4)
            {
                DiagnosticOutput.Write($"  I4x4-MODES: ");
                unsafe
                {
                    for (int m = 0; m < 16; m++)
                        DiagnosticOutput.Write($"{mb.Intra4x4PredModes[m]} ");
                }
                DiagnosticOutput.WriteLine();
                DiagnosticOutput.Write($"  RESID-PRE[blk0]: ");
                for (int r = 0; r < 16; r++)
                    DiagnosticOutput.Write($"{residualLuma[r]} ");
                DiagnosticOutput.WriteLine();
                DiagnosticOutput.Write($"  RESID-PRE[blk1]: ");
                for (int r = 16; r < 32; r++)
                    DiagnosticOutput.Write($"{residualLuma[r]} ");
                DiagnosticOutput.WriteLine();
                DiagnosticOutput.Write($"  CBP: luma={mb.CodedBlockPatternLuma} chroma={mb.CodedBlockPatternChroma}");
                DiagnosticOutput.WriteLine();
            }

            #if MK_STAGE_TIMING
            long _stage7 = Stopwatch.GetTimestamp();
            #endif
            ReconstructMacroblock<TShape>(ref mb, curMbX, curMbY, sps, residualLuma, residualChroma);
            #if MK_STAGE_TIMING
            StageReconstructTicks += Stopwatch.GetTimestamp() - _stage7;
            #endif
            
            // Store for deblocking pass (matches CABAC path)
            if (frameMacroblocks != null && mbAddr < frameMacroblocks.Length)
                frameMacroblocks[mbAddr] = mb;
            
            if (DiagnosticOutput != null && mbAddr < 2)
            {
                DiagnosticOutput.WriteLine($"MB[{mbAddr}] ({curMbX},{curMbY}): intra={mb.IsIntra} type={mb.IntraType} qp={mb.QuantizationParameter}");
                if (mb.IsIntra && currentFrameBuffer != null)
                {
                    int stride = context.CodedWidth;
                    if (currentFieldPicture) stride *= 2;
                    int baseOff = currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField ? context.CodedWidth : 0;
                    int lumaOff = baseOff + curMbY * 16 * stride + curMbX * 16;
                    DiagnosticOutput.Write($"  Y[0..15]: ");
                    for (int p = 0; p < 16 && lumaOff + p < currentFrameBuffer.Length; p++)
                        DiagnosticOutput.Write($"{currentFrameBuffer[lumaOff + p]:X2} ");
                    DiagnosticOutput.WriteLine();
                    DiagnosticOutput.Write($"  Resid[0..15]: ");
                    for (int p = 0; p < 16; p++)
                        DiagnosticOutput.Write($"{residualLuma[p]} ");
                    DiagnosticOutput.WriteLine();
                }
            }

            mbAddr++;
        }
        } // end non-MBAFF CAVLC
    }

    /// <summary>
    /// Processes a single macroblock within the MBAFF CAVLC pair loop.
    /// Handles skip_run state, mb_field_decoding_flag parsing, skip/coded MB dispatch.
    /// Maps to FFmpeg h264_cavlc.c:680-750 (ff_h264_decode_mb_cavlc with MBAFF).
    /// </summary>
    private void DecodeCavlcMbMbaff<TShape>(
        ref CavlcDecoder decoder, ref MacroblockDecoder mbDecoder,
        SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps,
        int mbAddr, int mbX, int mbY, int mbWidth,
        bool isInterSlice, ref int mbSkipRun, ref int prevQp,
        Span<short> residualLuma, Span<short> residualChroma, Span<byte> allNzCounts)
        where TShape : struct, IPictureShape
    {
        bool isTopOfPair = (mbY & 1) == 0;

        // For inter slices: consume or read skip_run
        if (isInterSlice)
        {
            if (mbSkipRun == -1)
                mbSkipRun = (int)decoder.ReadExpGolombUnsigned();

            if (mbSkipRun > 0)
            {
                mbSkipRun--;

                // MBAFF: at top of pair, if skip_run just reached 0, bottom will be coded.
                // Read 1-bit mb_field_decoding_flag between the skip and the coded bottom.
                // Maps to FFmpeg h264_cavlc.c:691-693.
                if (isTopOfPair && mbSkipRun == 0)
                {
                    mbaffCurrentFieldFlag = decoder.ReadBits(1) != 0;
                    mbFieldDecodingFlag![mbAddr] = mbaffCurrentFieldFlag;
                    mbFieldDecodingFlag[mbAddr + mbWidth] = mbaffCurrentFieldFlag;
                }
                else
                {
                    // Both MBs skipped (or bottom): inherit predicted/current field flag
                    mbFieldDecodingFlag![mbAddr] = mbaffCurrentFieldFlag;
                    if (DiagnosticOutput != null && mbY < 2)
                        DiagnosticOutput.WriteLine($"SKIP-FLAG: mbAddr={mbAddr} ({mbX},{mbY}) isTop={isTopOfPair} flag={mbaffCurrentFieldFlag} skipRun={mbSkipRun}");
                }

                // Process as skip MB
                ProcessCavlcSkipMb<TShape>(ref decoder, sliceHeader, sps, mbAddr, mbX, mbY, mbWidth,
                    prevQp, residualLuma, residualChroma, allNzCounts);
                mbaffPrevMbSkipped = true;
                return;
            }

            // skip_run was 0 → coded MB; reset for next read
            mbSkipRun = -1;
        }

        // Non-skip MB: read mb_field_decoding_flag at top of pair (1 bit for CAVLC)
        if (isTopOfPair)
        {
            uint flagBit = decoder.ReadBits(1);
            mbaffCurrentFieldFlag = flagBit != 0;
            mbFieldDecodingFlag![mbAddr] = mbaffCurrentFieldFlag;
            mbFieldDecodingFlag[mbAddr + mbWidth] = mbaffCurrentFieldFlag;
            if (DiagnosticOutput != null && mbY < 4)
                DiagnosticOutput.WriteLine($"FIELD-FLAG: col={mbX} mbAddr={mbAddr} bottomAddr={mbAddr + mbWidth} bit={flagBit} field={mbaffCurrentFieldFlag} bitPos={decoder.BitPosition}");
        }

        mbSliceIndex![mbAddr] = currentSliceIdx;

        // Compute MBAFF neighbor addresses for NNZ context
        ComputeMbaffNeighbors(mbAddr, mbX, mbY, mbWidth);

        // Validate neighbors against slice boundaries (must be same slice, not just decoded)
        int leftTop = (mbaffLeftXYTop >= 0 && mbaffLeftXYTop < mbSliceIndex.Length
            && mbSliceIndex[mbaffLeftXYTop] == currentSliceIdx) ? mbaffLeftXYTop : -1;
        int leftBot = (mbaffLeftXYBot >= 0 && mbaffLeftXYBot < mbSliceIndex.Length
            && mbSliceIndex[mbaffLeftXYBot] == currentSliceIdx) ? mbaffLeftXYBot : -1;
        int topNbr = (mbaffTopXY >= 0 && mbaffTopXY < mbSliceIndex.Length
            && mbSliceIndex[mbaffTopXY] == currentSliceIdx) ? mbaffTopXY : -1;

        mbDecoder.SetMbaffNeighbors(leftTop, leftBot, topNbr, mbaffLeftBlockIdx, mbaffCurrentFieldFlag);

        int bitBeforeMb = decoder.BitPosition;
        Macroblock mb;
        #if MK_STAGE_TIMING
        long _stage2 = Stopwatch.GetTimestamp();
        #endif
        mbDecoder.DecodeMacroblockCavlc(
            ref decoder, mbAddr, allNzCounts,
            ref prevQp, out mb,
            residualLuma, residualChroma);
        #if MK_STAGE_TIMING
        StageEntropyTicks += Stopwatch.GetTimestamp() - _stage2;
        #endif
        if (DiagnosticOutput != null && (mbY < 4 || mbAddr == DiagDumpDetailMbAddr))
            DiagnosticOutput.WriteLine($"MB-BITS: addr={mbAddr} ({mbX},{mbY}) bits={decoder.BitPosition - bitBeforeMb} bitStart={bitBeforeMb} bitEnd={decoder.BitPosition} type={mb.IntraType} inter={mb.InterPartitionMode} cbpL={mb.CodedBlockPatternLuma} cbpC={mb.CodedBlockPatternChroma} qp={mb.QuantizationParameter} skip={mb.IsSkipped} leftT={leftTop} leftB={leftBot} top={topNbr} field={mbaffCurrentFieldFlag}");

        // Fill MV/ref caches based on MB type
        if (!mb.IsIntra && mb.BSliceMbType >= 0)
        {
            ProcessCavlcBSliceInterMacroblock<TShape>(ref mb, mbAddr, mbX, mbY, mbWidth);
        }
        else if (!mb.IsIntra && mb.InterPartitionMode >= 0)
        {
            ProcessCavlcInterMacroblock<TShape>(ref mb, mbAddr, mbX, mbY, mbWidth);
        }
        else
        {
            PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, 0, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
            FillRefCache(Scan8[0], 4, 4, -1);
            FillRefCacheL1(Scan8[0], 4, 4, -1);
            ClearMvBoundaries(mbAddr);
            ClearRefBoundaries(mbAddr);
            ClearDirectBoundaries(mbAddr);
            ClearBlockDataForDeblocking(mbAddr);
        }

        mbIsIntra![mbAddr] = mb.IsIntra;
        if (!mb.IsIntra && mb.BSliceMbType >= 0)
            cabacMbTypes![mbAddr] = mb.BSliceMbType;
        else
            cabacMbTypes![mbAddr] = 99;

        #if MK_STAGE_TIMING
        long _stage8 = Stopwatch.GetTimestamp();
        #endif
        ReconstructMacroblock<TShape>(ref mb, mbX, mbY, sps, residualLuma, residualChroma);
        #if MK_STAGE_TIMING
        StageReconstructTicks += Stopwatch.GetTimestamp() - _stage8;
        #endif

        if (frameMacroblocks != null && mbAddr < frameMacroblocks.Length)
            frameMacroblocks[mbAddr] = mb;

        mbaffPrevMbSkipped = false;
    }

    /// <summary>
    /// Processes a skip MB in the MBAFF CAVLC path. Shared logic extracted from the non-MBAFF skip loop.
    /// </summary>
    private void ProcessCavlcSkipMb<TShape>(
        ref CavlcDecoder decoder, SliceHeader sliceHeader, SequenceParameterSet sps,
        int mbAddr, int mbX, int mbY, int mbWidth,
        int qp, Span<short> residualLuma, Span<short> residualChroma, Span<byte> allNzCounts)
        where TShape : struct, IPictureShape
    {
        var skippedMb = new Macroblock
        {
            Address = mbAddr,
            PositionX = mbX,
            PositionY = mbY,
            IsSkipped = true,
            IsIntra = false,
            QuantizationParameter = qp,
            InterPartitionMode = -1,
            BSliceMbType = -1
        };

        allNzCounts.Slice(mbAddr * nzCountsPerMb, nzCountsPerMb).Clear();
        residualLuma.Clear();
        residualChroma.Clear();
        mbIsIntra![mbAddr] = false;
        mbSliceIndex![mbAddr] = currentSliceIdx;
        cabacMbTypes![mbAddr] = -1;

        // Compute MBAFF neighbors for skip MB (needed by PrepareRefCache/PrepareMvCache)
        if (mbFieldDecodingFlag != null)
            ComputeMbaffNeighbors(mbAddr, mbX, mbY, mbWidth);

        PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
        PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);

        if (sliceHeader.IsBSlice)
        {
            PrepareDirectCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            if (sliceHeader.DirectSpatialMvPrediction)
                DeriveBDirectSpatialMotionVectors<TShape>(mbAddr);
            else
                DeriveBDirectTemporalMotionVectors<TShape>(mbAddr);
            FillDirectCache(Scan8[0], 4, 4, 1);
            StoreDirectBoundaries(mbAddr);
        }
        else
        {
            DerivePSkipMotionVector<TShape>(mbAddr);
            FillRefCache(Scan8[0], 4, 4, 0);
            FillRefCacheL1(Scan8[0], 4, 4, -1);
        }
        if (DiagTraceMbAddr >= 0 && mbAddr == DiagTraceMbAddr)
        {
            Console.WriteLine($"[DIAG-SKIP] mbAddr={mbAddr} mbX={mbX} mbY={mbY} isB={sliceHeader.IsBSlice} isField={mbFieldDecodingFlag?[mbAddr]}");
            Console.Write($"[DIAG-SKIP] L0 MV:");
            for (int blk = 0; blk < 16; blk++)
            {
                int s8 = Scan8[blk];
                Console.Write($" [{blk}]=({mvCacheL0X[s8]},{mvCacheL0Y[s8]},r={refCacheL0[s8]})");
            }
            Console.WriteLine();
        }
        StoreMvBoundaries(mbAddr);
        StoreRefBoundaries(mbAddr);
        StoreBlockDataForDeblocking<TShape>(mbAddr);

        #if MK_STAGE_TIMING
        long _stage9 = Stopwatch.GetTimestamp();
        #endif
        ReconstructMacroblock<TShape>(ref skippedMb, mbX, mbY, sps, residualLuma, residualChroma);
        #if MK_STAGE_TIMING
        StageReconstructTicks += Stopwatch.GetTimestamp() - _stage9;
        #endif

        if (frameMacroblocks != null && mbAddr < frameMacroblocks.Length)
            frameMacroblocks[mbAddr] = skippedMb;
    }

    private void DecodeWithCabac<TShape>(byte[] rbsp, int rbspLength, SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps, int startMb, int totalMb, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Use the bit offset from slice header parsing and align to byte
        int bitOffset = sliceHeader.SliceDataBitOffset;
        // CABAC starts on byte boundary after cabac_alignment_one_bit
        int byteOffset = (bitOffset + 7) / 8;  // Round up to next byte

        // SliceQPY, bounded to the range 7.4.3 allows it - which for ten-bit video reaches as
        // low as -QpBdOffsetY, so it must not be clipped to 0..51 here. CABAC context
        // initialisation applies the spec's own Clip3(0, 51, SliceQPY) itself
        // (CabacTables.ContextInit.GetInitialState), and previousMbQp below needs the
        // unclipped value because it is the quantiser the first mb_qp_delta is relative to.
        int sliceQp = sliceHeader.GetSliceQp(pps, qpBdOffset);
        int cabacInitIdc = (int)sliceHeader.CabacInitIdc;
        bool isISlice = sliceHeader.Type == SliceType.ISlice || sliceHeader.Type == SliceType.SiSlice;
        
        var cabacSpan = rbsp.AsSpan(byteOffset, rbspLength - byteOffset);
        
        // CABAC trace diagnostic: dump start info for target POC
        // DiagCabacTracePoc == -2: trace the very first slice decoded (any POC)
        bool cabacTrace = DiagnosticOutput != null && (
            (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) ||
            DiagCabacTracePoc == -2);
        // Also trace adjacent B-frames for comparison
        bool cabacTraceAdjacent = DiagCabacTracePoc >= 0 && DiagnosticOutput != null 
            && (currentPoc == DiagCabacTracePoc - 2 || currentPoc == DiagCabacTracePoc + 2);
        if (cabacTrace || cabacTraceAdjacent)
        {
            DiagnosticOutput!.WriteLine($"CABAC-TRACE poc={currentPoc} sliceType={sliceHeader.Type} sliceQp={sliceQp} cabacInitIdc={cabacInitIdc}");
            DiagnosticOutput.WriteLine($"  PPS: picInitQp={pps.PictureInitQpMinus26} t8x8={pps.Transform8x8Mode} wp={pps.WeightedPrediction} wbip={pps.WeightedBiprediction} entropy={pps.EntropyCodingMode}");
            DiagnosticOutput.WriteLine($"  SPS: profile={sps.Profile} level={sps.Level} chroma={sps.ChromaFormat} bitDepthLuma={sps.BitDepthLuma}");
            DiagnosticOutput.WriteLine($"  bitOffset={bitOffset} byteOffset={byteOffset} rbspLen={rbspLength} cabacLen={rbspLength - byteOffset}");
            int showBytes = Math.Min(16, rbspLength - byteOffset);
            DiagnosticOutput.Write("  CABAC first bytes:");
            for (int i = 0; i < showBytes; i++)
                DiagnosticOutput.Write($" {rbsp[byteOffset + i]:X2}");
            DiagnosticOutput.WriteLine();
            int initOffset = (rbsp[byteOffset] << 1) | (rbsp[byteOffset + 1] >> 7);
            DiagnosticOutput.WriteLine($"  Initial codIOffset={initOffset} codIRange=510");
            // Show the alignment byte boundary
            int alignByte = bitOffset / 8;
            int alignBit = bitOffset % 8;
            DiagnosticOutput.WriteLine($"  Alignment: sliceHeaderEndByte={alignByte} bit={alignBit} rbsp[{alignByte}]=0x{rbsp[alignByte]:X2} rbsp[{alignByte+1}]=0x{rbsp[alignByte+1]:X2}");
            DiagnosticOutput.WriteLine($"  MBAFF: isMbaffFrame={isMbaffFrame} mbAdaptive={sps.MbAdaptiveFrameField}");
        }
        
        
        var decoder = new CabacDecoder(
            cabacSpan, 
            cabacContextStorage.AsSpan(),
            sliceQp, 
            cabacInitIdc,
            isISlice);

        // Clear neighbor tracking arrays only for the first slice in the frame.
        // Subsequent slices need to see the previous slice's MB data for neighbor context.
        if (startMb == 0)
        {
            currentSliceIdx = 0;
            Array.Fill(mbSliceIndex!, -1);
            Array.Clear(cabacMbTypes);
            Array.Clear(cabacCbpLuma);
            Array.Clear(cabacCbpChroma);
            Array.Clear(cabacLumaCbf);
            Array.Clear(cabacLumaDcCoded);
            Array.Clear(cabacChromaCbf);
            Array.Clear(cabacChromaPredMode);
            Array.Clear(cabacIsIntra16x16!);
            Array.Clear(cabacIsIntra4x4!);
            if (mbIsIntra != null) Array.Clear(mbIsIntra);
            if (cabacTransform8x8 != null) Array.Clear(cabacTransform8x8);
            // Initialize intra4x4 modes to DC(2) so non-Intra4x4 neighbors return correct default
            Array.Fill(mbIntra4x4Modes!, (byte)2);
            if (cabacAbsMvdXRight != null) Array.Clear(cabacAbsMvdXRight);
            if (cabacAbsMvdYRight != null) Array.Clear(cabacAbsMvdYRight);
            if (cabacAbsMvdXBottom != null) Array.Clear(cabacAbsMvdXBottom);
            if (cabacAbsMvdYBottom != null) Array.Clear(cabacAbsMvdYBottom);
            if (cabacAbsMvdL1XRight != null) Array.Clear(cabacAbsMvdL1XRight);
            if (cabacAbsMvdL1YRight != null) Array.Clear(cabacAbsMvdL1YRight);
            if (cabacAbsMvdL1XBottom != null) Array.Clear(cabacAbsMvdL1XBottom);
            if (cabacAbsMvdL1YBottom != null) Array.Clear(cabacAbsMvdL1YBottom);
            if (cabacRefL0Right != null) Array.Clear(cabacRefL0Right);
            if (cabacRefL0Bottom != null) Array.Clear(cabacRefL0Bottom);
            if (cabacRefL1Right != null) Array.Clear(cabacRefL1Right);
            if (cabacRefL1Bottom != null) Array.Clear(cabacRefL1Bottom);
            // Clear MV boundary and ref_idx arrays for this slice
            if (mbMvL0XRight != null) Array.Clear(mbMvL0XRight);
            if (mbMvL0YRight != null) Array.Clear(mbMvL0YRight);
            if (mbMvL0XBottom != null) Array.Clear(mbMvL0XBottom);
            if (mbMvL0YBottom != null) Array.Clear(mbMvL0YBottom);
            if (mbMvL1XRight != null) Array.Clear(mbMvL1XRight);
            if (mbMvL1YRight != null) Array.Clear(mbMvL1YRight);
            if (mbMvL1XBottom != null) Array.Clear(mbMvL1XBottom);
            if (mbMvL1YBottom != null) Array.Clear(mbMvL1YBottom);
            if (mbRefIdxL0 != null) Array.Fill(mbRefIdxL0, (sbyte)-1);
            if (mbRefIdxL1 != null) Array.Fill(mbRefIdxL1, (sbyte)-1);
            if (deblockMotionUniform != null) Array.Clear(deblockMotionUniform);
            if (deblockMvL0 != null) Array.Clear(deblockMvL0);
            if (deblockMvL1 != null) Array.Clear(deblockMvL1);
            if (deblockRefL0 != null) Array.Fill(deblockRefL0, (sbyte)-1);
            if (deblockRefL1 != null) Array.Fill(deblockRefL1, (sbyte)-1);
            if (deblockRefPicL0 != null) Array.Fill(deblockRefPicL0, -1);
            if (deblockRefPicL1 != null) Array.Fill(deblockRefPicL1, -1);
            if (mbFieldDecodingFlag != null) Array.Clear(mbFieldDecodingFlag);
        }
        diagTdTraceCount = 0;
        previousMbQpDelta = 0;
        previousMbQp = sliceQp + qpBdOffset;
        blockExceptionCounter = 0;
        currentSliceIdx++;
        
        // Store per-slice deblocking parameters for later ApplyDeblocking use
        sliceDeblockIdc![currentSliceIdx] = sliceHeader.DisableDeblockingFilterIdc;
        sliceFilterOffsetA![currentSliceIdx] = sliceHeader.SliceAlphaC0OffsetDiv2 * 2;
        sliceFilterOffsetB![currentSliceIdx] = sliceHeader.SliceBetaOffsetDiv2 * 2;

        Span<short> residualLuma = residualLumaBuffer.AsSpan();
        Span<short> residualChroma = residualChromaBuffer.AsSpan();

        int mbsDecoded = 0;
        Exception? firstMbException = null;
        cabacExit = CabacExitKind.None;
        cabacExitMessage = null;
        
        if (isMbaffFrame)
        {
            // MBAFF pair-based loop: decode top MB, then bottom MB, then terminate once per pair.
            // Maps to FFmpeg h264_slice.c:2606-2674 MBAFF CABAC loop.
            // first_mb_in_slice is in pair units for MBAFF (H.264 spec 7.4.3).
            int startMbX = startMb % mbWidth;
            int startMbY = (startMb / mbWidth) * 2;  // Convert pair-row to MB-row
            int mbHeight = context.HeightInMacroblocks;

            // Initialize MBAFF state for this slice
            mbaffPrevMbSkipped = false;
            mbaffNextMbSkipped = false;
            mbaffCurrentFieldFlag = false;

            int mbX = startMbX;
            int mbY = startMbY;

            while (mbY < mbHeight)
            {
                int topMbAddr = mbY * mbWidth + mbX;
                int bottomMbAddr = (mbY + 1) * mbWidth + mbX;

                // --- Decode and reconstruct TOP MB ---
                mbSliceIndex![topMbAddr] = currentSliceIdx;
                try
                {
                    int binsBeforeMb = decoder.TotalBinsDecoded;
                    #if MK_STAGE_TIMING
                    long _stage3 = Stopwatch.GetTimestamp();
                    #endif
                    DecodeMacroblockCabac<TShape>(ref decoder, sliceHeader, sps, pps, topMbAddr, out Macroblock topMb, residualLuma, residualChroma);
                    #if MK_STAGE_TIMING
                    StageEntropyTicks += Stopwatch.GetTimestamp() - _stage3;
                    #endif
                    int binsForMb = decoder.TotalBinsDecoded - binsBeforeMb;

                    if (DiagnosticOutput != null && cabacTrace && (topMbAddr < startMbY * mbWidth + startMbX + DiagCabacTraceMbCount))
                        DiagnosticOutput.WriteLine($"[MB_TRACE] mb={topMbAddr} ({mbX},{mbY}) type={cabacMbTypes![topMbAddr]} intra={topMb.IntraType} cbpL={topMb.CodedBlockPatternLuma} cbpC={topMb.CodedBlockPatternChroma} qp={topMb.QuantizationParameter} bins={binsForMb} {decoder.DiagState} field={mbFieldDecodingFlag![topMbAddr]}");

                    cabacCbpLuma![topMbAddr] = topMb.CodedBlockPatternLuma;
                    cabacCbpChroma![topMbAddr] = topMb.CodedBlockPatternChroma;
                    if (DiagZeroResidualForPoc == currentPoc)
                    {
                        residualLuma.Clear();
                        residualChroma.Clear();
                    }
                    #if MK_STAGE_TIMING
                    long _stage10 = Stopwatch.GetTimestamp();
                    #endif
                    ReconstructMacroblock<TShape>(ref topMb, mbX, mbY, sps, residualLuma, residualChroma);
                    #if MK_STAGE_TIMING
                    StageReconstructTicks += Stopwatch.GetTimestamp() - _stage10;
                    #endif

                    if (frameMacroblocks != null && topMbAddr < frameMacroblocks.Length)
                        frameMacroblocks[topMbAddr] = topMb;
                    mbsDecoded++;
                    
                    // Per-MB summary trace for MBAFF B-frame diagnosis
                    if (DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc && deblockMvL0 != null)
                    {
                        int fbase = topMbAddr * 16;
                        DiagnosticOutput.WriteLine($"MBSUM ({mbX},{mbY}) s={Convert.ToInt32(topMb.IsSkipped)} i={Convert.ToInt32(topMb.IsIntra)} t={cabacMbTypes![topMbAddr]} f={Convert.ToInt32(mbFieldDecodingFlag![topMbAddr])} r0={deblockRefL0![fbase]} r1={deblockRefL1![fbase]} mv0=({deblockMvL0![2 * fbase]},{deblockMvL0![2 * fbase + 1]}) mv1=({deblockMvL1![2 * fbase]},{deblockMvL1![2 * fbase + 1]})");
                    }
                }
                catch (Exception ex)
                {
                    firstMbException ??= ex;
                    cabacExit = CabacExitKind.ExceptionTop;
                    cabacExitMbAddr = topMbAddr; cabacExitMbX = mbX; cabacExitMbY = mbY;
                    cabacExitMessage = ex.Message;
                    break;
                }

                // --- Decode and reconstruct BOTTOM MB ---
                mbSliceIndex![bottomMbAddr] = currentSliceIdx;
                try
                {
                    int binsBeforeMb = decoder.TotalBinsDecoded;
                    #if MK_STAGE_TIMING
                    long _stage4 = Stopwatch.GetTimestamp();
                    #endif
                    DecodeMacroblockCabac<TShape>(ref decoder, sliceHeader, sps, pps, bottomMbAddr, out Macroblock bottomMb, residualLuma, residualChroma);
                    #if MK_STAGE_TIMING
                    StageEntropyTicks += Stopwatch.GetTimestamp() - _stage4;
                    #endif
                    int binsForMb = decoder.TotalBinsDecoded - binsBeforeMb;

                    if (DiagnosticOutput != null && cabacTrace && (bottomMbAddr < startMbY * mbWidth + startMbX + DiagCabacTraceMbCount))
                        DiagnosticOutput.WriteLine($"[MB_TRACE] mb={bottomMbAddr} ({mbX},{mbY + 1}) type={cabacMbTypes![bottomMbAddr]} intra={bottomMb.IntraType} cbpL={bottomMb.CodedBlockPatternLuma} cbpC={bottomMb.CodedBlockPatternChroma} qp={bottomMb.QuantizationParameter} bins={binsForMb} {decoder.DiagState} field={mbFieldDecodingFlag![bottomMbAddr]}");

                    cabacCbpLuma![bottomMbAddr] = bottomMb.CodedBlockPatternLuma;
                    cabacCbpChroma![bottomMbAddr] = bottomMb.CodedBlockPatternChroma;
                    if (DiagZeroResidualForPoc == currentPoc)
                    {
                        residualLuma.Clear();
                        residualChroma.Clear();
                    }
                    #if MK_STAGE_TIMING
                    long _stage11 = Stopwatch.GetTimestamp();
                    #endif
                    ReconstructMacroblock<TShape>(ref bottomMb, mbX, mbY + 1, sps, residualLuma, residualChroma);
                    #if MK_STAGE_TIMING
                    StageReconstructTicks += Stopwatch.GetTimestamp() - _stage11;
                    #endif

                    if (frameMacroblocks != null && bottomMbAddr < frameMacroblocks.Length)
                        frameMacroblocks[bottomMbAddr] = bottomMb;
                    mbsDecoded++;
                    
                    // Per-MB summary trace for MBAFF B-frame diagnosis
                    if (DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc && deblockMvL0 != null)
                    {
                        int fbase = bottomMbAddr * 16;
                        DiagnosticOutput.WriteLine($"MBSUM ({mbX},{mbY + 1}) s={Convert.ToInt32(bottomMb.IsSkipped)} i={Convert.ToInt32(bottomMb.IsIntra)} t={cabacMbTypes![bottomMbAddr]} f={Convert.ToInt32(mbFieldDecodingFlag![bottomMbAddr])} r0={deblockRefL0![fbase]} r1={deblockRefL1![fbase]} mv0=({deblockMvL0![2 * fbase]},{deblockMvL0![2 * fbase + 1]}) mv1=({deblockMvL1![2 * fbase]},{deblockMvL1![2 * fbase + 1]})");
                    }
                }
                catch (Exception ex)
                {
                    firstMbException ??= ex;
                    cabacExit = CabacExitKind.ExceptionBottom;
                    cabacExitMbAddr = bottomMbAddr; cabacExitMbX = mbX; cabacExitMbY = mbY;
                    cabacExitMessage = ex.Message;
                    break;
                }

                // --- Decode terminate (once per pair, per H.264 spec 7.3.4) ---
                {
                    int endFlag = decoder.DecodeTerminate();
                    if (cabacTrace)
                        DiagnosticOutput!.WriteLine($"  PAIR({mbX},{mbY}) top={topMbAddr} bot={bottomMbAddr} {decoder.DiagState} term={endFlag}");
                    if (endFlag == 1)
                    {
                        cabacExit = CabacExitKind.TerminateBinPair;
                        cabacExitMbX = mbX; cabacExitMbY = mbY;
                        cabacExitBitPos = decoder.CurrentBitPosition; cabacExitRemaining = decoder.RemainingBytes;
                        break;
                    }
                }

                // --- Advance to next pair ---
                mbX++;
                if (mbX >= mbWidth)
                {
                    mbX = 0;
                    mbY += 2;
                    // Predict field flag for new row (FFmpeg h264_slice.c:2660-2661)
                    if (mbY < mbHeight)
                        PredictFieldDecodingFlag(mbX, mbY, mbWidth);
                }

                if (decoder.IsEndOfStream || decoder.RemainingBytes < 0)
                {
                    cabacExit = CabacExitKind.StreamEndPair;
                    cabacExitMbX = mbX; cabacExitMbY = mbY;
                    cabacExitEos = decoder.IsEndOfStream; cabacExitRemaining = decoder.RemainingBytes;
                    break;
                }
            }
            // Snapshot field flags for diagnostic comparison (before next frame overwrites)
            if (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc && mbFieldDecodingFlag != null)
                DiagFieldFlagSnapshot = (bool[])mbFieldDecodingFlag.Clone();
        }
        else
        {
        // Non-MBAFF: existing linear loop (unchanged)
        for (int mbAddr = startMb; mbAddr < totalMb; mbAddr++)
        {
            int mbX = mbAddr % mbWidth;
            int mbY = mbAddr / mbWidth;
            mbSliceIndex![mbAddr] = currentSliceIdx;

            try
            {
                int binsBeforeMb = decoder.TotalBinsDecoded;
                
                // Decode macroblock with CABAC
                #if MK_STAGE_TIMING
                long _stage5 = Stopwatch.GetTimestamp();
                #endif
                DecodeMacroblockCabac<TShape>(ref decoder, sliceHeader, sps, pps, mbAddr, out Macroblock mb, residualLuma, residualChroma);
                #if MK_STAGE_TIMING
                StageEntropyTicks += Stopwatch.GetTimestamp() - _stage5;
                #endif
                
                int binsForMb = decoder.TotalBinsDecoded - binsBeforeMb;
                
                // MB diagnostic trace: log all MBs when DiagnosticOutput is set
                if (DiagnosticOutput != null && (mbAddr < startMb + DiagCabacTraceMbCount || mb.IntraType == IntraMacroblockType.IntraPcm))
                    DiagnosticOutput.WriteLine($"[MB_TRACE] mb={mbAddr} ({mbX},{mbY}) type={cabacMbTypes![mbAddr]} intra={mb.IntraType} cbpL={mb.CodedBlockPatternLuma} cbpC={mb.CodedBlockPatternChroma} qp={mb.QuantizationParameter} bins={binsForMb} {decoder.DiagState}");
                
                // Store MB state for neighbor context derivation (mb_type stored in DecodeMacroblockCabac)
                cabacCbpLuma![mbAddr] = mb.CodedBlockPatternLuma;
                cabacCbpChroma![mbAddr] = mb.CodedBlockPatternChroma;

                // Reconstruct macroblock pixels
                #if MK_STAGE_TIMING
                long _stage12 = Stopwatch.GetTimestamp();
                #endif
                ReconstructMacroblock<TShape>(ref mb, mbX, mbY, sps, residualLuma, residualChroma);
                #if MK_STAGE_TIMING
                StageReconstructTicks += Stopwatch.GetTimestamp() - _stage12;
                #endif
                
                // Store for deblocking pass
                if (frameMacroblocks != null && mbAddr < frameMacroblocks.Length)
                    frameMacroblocks[mbAddr] = mb;
                
                mbsDecoded++;

                // Check for end of slice (CABAC uses terminate bin).
                // Per H.264 spec 7.3.4, end_of_slice_flag is decoded after EVERY macroblock,
                // including I_PCM (whose CABAC engine was reinitialized inside macroblock_layer).
                {
                    int endFlag = decoder.DecodeTerminate();
                    
                    if (cabacTrace && mbAddr < startMb + DiagCabacTraceMbCount)
                        DiagnosticOutput!.WriteLine($"  MB({mbX},{mbY}) addr={mbAddr} type={cabacMbTypes![mbAddr]} {decoder.DiagState} term={endFlag}");
                    
                    if (endFlag == 1)
                    {
                        cabacExit = CabacExitKind.TerminateBin;
                        cabacExitMbAddr = mbAddr; cabacExitMbX = mbX; cabacExitMbY = mbY;
                        cabacExitBitPos = decoder.CurrentBitPosition; cabacExitRemaining = decoder.RemainingBytes;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                firstMbException ??= ex;
                cabacExit = CabacExitKind.Exception;
                cabacExitMbAddr = mbAddr; cabacExitMbX = mbX; cabacExitMbY = mbY;
                break;
            }
            
            if (decoder.IsEndOfStream || decoder.RemainingBytes < 0)
            {
                cabacExit = CabacExitKind.StreamEnd;
                cabacExitMbAddr = mbAddr; cabacExitMbX = mbX; cabacExitMbY = mbY;
                cabacExitEos = decoder.IsEndOfStream; cabacExitRemaining = decoder.RemainingBytes;
                break;
            }
        }
        } // end non-MBAFF
        
        CabacMbsDecoded = mbsDecoded;
        CabacTotalMbs = totalMb;
        CabacFirstException = firstMbException;
        CabacBlockExceptionsSwallowed = blockExceptionCounter;
        LastSliceType = sliceHeader.Type;
        CabacMbDiagnostics = null;
    }



    /// <summary>
    /// Peeks at a slice NAL's first_mb_in_slice and frame_num to detect picture boundaries.
    /// H.264 spec 7.4.1.2.4: a new primary coded picture is detected when frame_num changes,
    /// IDR flag changes, or other key slice header fields differ.
    /// </summary>
    private bool IsNewPicture(NalUnit nal)
    {
        if (context.ActiveSps == null || context.ActivePps == null)
            return false;
        
        var sps = context.ActiveSps;

        // Only the first few Exp-Golomb values are read, so unescaping the whole slice to get them
        // allocated a copy of it per frame for nothing.
        int peekLength = Math.Min(nal.Payload.Length, 32);
        byte[] rbsp = NalUnitParser.RemoveEmulationPreventionBytes(nal.Payload.Span[..peekLength]);
        var reader = new BitstreamReader(rbsp);
        
        try
        {
            uint firstMb = reader.ReadExpGolombUnsigned();
            reader.ReadExpGolombUnsigned(); // slice_type
            reader.ReadExpGolombUnsigned(); // pps_id
            
            if (sps.SeparateColorPlane)
                reader.ReadBits(2);
            
            int frameNumBits = sps.Log2MaxFrameNumber;
            ushort frameNum = (ushort)reader.ReadBits(frameNumBits);
            
            // H.264 spec 7.4.1.2.4: first_mb_in_slice > 0 means this is a continuation
            // slice within the current picture — never a new picture boundary.
            // Must check BEFORE IDR/frame_num checks to handle multi-slice IDR pictures.
            if (firstMb > 0 && frameNum == currentFrameNum)
                return false;
            
            // IDR always starts a new picture (when first_mb == 0)
            if (nal.Type == NalUnitType.CodedSliceIdr)
                return true;
            
            // Different frame_num means new picture
            if (frameNum != currentFrameNum)
                return true;
            
            // Same frame_num with first_mb_in_slice == 0 while we already decoded MBs
            // indicates a new picture (can't have two slices both starting at MB 0)
            if (firstMb == 0)
                return true;
        }
        catch
        {
            // Parse error — treat as new picture for safety
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Finalizes the current frame and adds it to the reorder buffer.
    /// Returns any frame ready for output from the reorder buffer.
    /// </summary>
    private DecodedVideoFrame? FinalizeAndOutputCurrentFrame(long pts, bool isIdr)
    {
        FinalizeFrame();
        FramesDecoded++;
        AddToReorderBuffer(pts, isIdr);
        return TryOutputReorderedFrame();
    }

    private void SkipSliceHeaderBits(ref BitstreamReader reader, SliceHeader sliceHeader, SequenceParameterSet sps)
    {
        // Skip through slice header fields (simplified)
        reader.ReadExpGolombUnsigned(); // first_mb_in_slice
        reader.ReadExpGolombUnsigned(); // slice_type
        reader.ReadExpGolombUnsigned(); // pic_parameter_set_id
        
        if (sps.SeparateColorPlane)
            reader.ReadBits(2);
            
        reader.ReadBits(sps.Log2MaxFrameNumber); // frame_num
        
        // More fields would be skipped here for complete implementation
    }

    private void ApplyDeblocking(PictureParameterSet pps)
    {
        if (SkipDeblocking)
            return;
        
        if (currentFrameBuffer == null || frameMacroblocks == null || mbSliceIndex == null)
            return;
        
        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int mbWidth = context.WidthInMacroblocks;
        int mbHeight = context.HeightInMacroblocks;
        
        int lumaStride = width;
        int chromaStride = width / 2;
        int fieldBaseOffsetLuma = 0;
        int fieldBaseOffsetChroma = 0;
        
        // Field pictures: halve MB grid height and double strides to operate on
        // every other row. Bottom field starts one row down in the frame buffer.
        if (currentFieldPicture)
        {
            mbHeight /= 2;
            if (currentPictureStructure == ReferenceFrame.PictBottomField)
            {
                fieldBaseOffsetLuma = lumaStride;
                fieldBaseOffsetChroma = chromaStride;
            }
            lumaStride *= 2;
            chromaStride *= 2;
        }
        
        int totalMb = mbWidth * mbHeight;
        
        var buffer = currentFrameBuffer.AsSpan();
        int bytesPerPixel = 1 << pixelShift; // 1 for 8-bit, 2 for 10-bit
        int lumaPlaneSize = width * height;
        // Chroma: 4:2:0 = (W/2)*(H/2), 4:2:2 = (W/2)*H
        int chromaPlaneSize = (width / 2) * (height / subHeightC);
        int lumaPlaneSizeBytes = lumaPlaneSize * bytesPerPixel;
        int chromaPlaneSizeBytes = chromaPlaneSize * bytesPerPixel;
        int fieldBaseOffsetLumaBytes = fieldBaseOffsetLuma * bytesPerPixel;
        int fieldBaseOffsetChromaBytes = fieldBaseOffsetChroma * bytesPerPixel;
        var lumaSpan = buffer.Slice(fieldBaseOffsetLumaBytes, lumaPlaneSizeBytes - fieldBaseOffsetLumaBytes);
        var cbSpan = buffer.Slice(lumaPlaneSizeBytes + fieldBaseOffsetChromaBytes, chromaPlaneSizeBytes - fieldBaseOffsetChromaBytes);
        var crSpan = buffer.Slice(lumaPlaneSizeBytes + chromaPlaneSizeBytes + fieldBaseOffsetChromaBytes, chromaPlaneSizeBytes - fieldBaseOffsetChromaBytes);
        
        DeblockingFilter.CurrentDeblockPoc = currentPoc;

        // Built once for the picture and passed by reference from here down. ff_h264_filter_mb
        // reads this material off h and sl; we used to re-state all of it as arguments on every
        // macroblock, which measured 1.0% of decode on its own - see DeblockPicture.
        var deblockPicture = new DeblockingFilter.DeblockPicture(
            lumaSpan, cbSpan, crSpan, lumaStride, chromaStride,
            deblockMvL0, deblockMvL1, deblockRefPicL0, deblockRefPicL1, deblockMotionUniform,
            pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset,
            currentFieldPicture, isMbaffFrame, pixelShift, bitDepth, isChroma422);

        // And one of these for the whole picture, rather than one argument list per macroblock.
        // Most of its fields hold the same value for every macroblock in a progressive picture.
        DeblockingFilter.DeblockMacroblock deblockMb = default;
        deblockMb.LumaStride = lumaStride;
        deblockMb.ChromaStride = chromaStride;
        
        // Save pre-deblocking luma snapshot for diagnostic comparison
        if (PreDeblockLumaSnapshot != null && PreDeblockDecodeIndex >= 0 &&
            DeblockingFilter.DeblockCallCount == PreDeblockDecodeIndex)
        {
            if (PreDeblockLumaSnapshot.Length >= lumaPlaneSizeBytes)
                lumaSpan.Slice(0, lumaPlaneSizeBytes).CopyTo(PreDeblockLumaSnapshot.AsSpan());
        }
        
        // MBAFF deblocking uses pair-based order: for each pair row, iterate columns,
        // deblocking top MB then bottom MB before moving to the next column.
        // This ensures pair boundary modifications (in-place) are visible to the
        // next pair's left edge filter, matching FFmpeg's ff_h264_filter_mb order.
        if (isMbaffFrame)
        {
            int pairRows = mbHeight / 2;
            
            for (int pairRow = 0; pairRow < pairRows; pairRow++)
            {
                for (int mbX = 0; mbX < mbWidth; mbX++)
                {
                    for (int pairIdx = 0; pairIdx < 2; pairIdx++)
                    {
                        int mbY = pairRow * 2 + pairIdx;
                        int mbAddr = mbY * mbWidth + mbX;
                        
                        DeblockOneMb(deblockPicture, ref deblockMb, mbAddr, mbX, mbY, mbWidth);
                    }
                }
            }
        }
        else
        {
            for (int mbAddr = 0; mbAddr < totalMb; mbAddr++)
            {
                int mbX = mbAddr % mbWidth;
                int mbY = mbAddr / mbWidth;
                
                DeblockOneMb(deblockPicture, ref deblockMb, mbAddr, mbX, mbY, mbWidth);

                // Filtering a macroblock row reaches three rows back across its top edge, so
                // once row mbY is done everything above that reach is final and readable.
                if (mbX == mbWidth - 1)
                    currentProgress.Publish(Math.Max(0, mbY * 16 - 3));
            }
        }
    }

    /// <summary>
    /// Splits an interleaved motion array back into separate x and y arrays for the co-located
    /// snapshot, which the direct-mode paths still read that way. Once per frame, not per
    /// macroblock, so the split costs far less than the interleaving saves.
    /// </summary>
    private static void Deinterleave(short[] source, short[] xs, short[] ys, int count)
    {
        for (int i = 0; i < count; i++)
        {
            xs[i] = source[2 * i];
            ys[i] = source[2 * i + 1];
        }
    }

    private void DeblockOneMb(in DeblockingFilter.DeblockPicture picture,
        ref DeblockingFilter.DeblockMacroblock mb, int mbAddr, int mbX, int mbY, int mbWidth)
    {
        if (mbAddr < 0 || mbAddr >= mbSliceIndex!.Length)
            return;
        int lumaStride = picture.LumaStride;
        int chromaStride = picture.ChromaStride;
        int sliceIdx = mbSliceIndex![mbAddr];
        uint idc = sliceDeblockIdc != null ? sliceDeblockIdc[sliceIdx] : 0;
        
        if (idc == 1)
            return;
        
        int filterOffsetA = sliceFilterOffsetA != null ? sliceFilterOffsetA[sliceIdx] : 0;
        int filterOffsetB = sliceFilterOffsetB != null ? sliceFilterOffsetB[sliceIdx] : 0;
        
        bool currentIsField = isMbaffFrame && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        
        // For MBAFF field-coded MBs, deblocking operates on each field independently:
        // - Stride is doubled (every other physical row belongs to the same field)
        // - Bottom field dest is adjusted to start at odd row of the pair region
        // - Top neighbor is the same-field MB from the pair ABOVE (2*mbWidth away)
        int effectiveLumaStride = lumaStride;
        int effectiveChromaStride = chromaStride;
        int lumaBaseOffset = 0;
        int chromaCbBaseOffset = 0;
        int chromaCrBaseOffset = 0;
        int effectiveMbY = mbY;
        
        if (currentIsField)
        {
            effectiveLumaStride = lumaStride * 2;
            effectiveChromaStride = chromaStride * 2;
            
            // FilterMacroblock computes pixel position as mbY*16*doubledStride,
            // but FFmpeg uses mbY*16*originalStride. Compensate for the difference.
            int chromaHeightPerMb = isChroma422 ? 16 : 8;
            lumaBaseOffset = -mbY * 16 * lumaStride;
            chromaCbBaseOffset = -mbY * chromaHeightPerMb * chromaStride;
            chromaCrBaseOffset = -mbY * chromaHeightPerMb * chromaStride;
            
            if ((mbY & 1) != 0)
            {
                // Bottom field: shift to odd rows within pair region
                // FFmpeg: dest_y -= sl->linesize * 15
                lumaBaseOffset -= lumaStride * 15;
                chromaCbBaseOffset -= chromaStride * (chromaHeightPerMb - 1);
                chromaCrBaseOffset -= chromaStride * (chromaHeightPerMb - 1);
            }
        }
        
        // Neighbour computation.
        //
        // These are references into the macroblock array rather than copies. Macroblock is 244
        // bytes, so a Nullable<Macroblock> copied it once to populate the local and again on
        // every .Value read - and the strength derivation reads a field of a neighbour dozens of
        // times per macroblock. Reading one field through Nullable<T>.Value on a struct this
        // size measured 4.35x the cost of reading it directly.
        ref readonly Macroblock leftMb = ref MissingMacroblock;
        ref readonly Macroblock topMb = ref MissingMacroblock;
        bool hasLeftMb = false;
        bool hasTopMb = false;
        int leftMbAddr = -1;
        int topMbAddr = -1;
        
        if (mbX > 0)
        {
            int leftAddr = mbAddr - 1;
            bool crossSlice = mbSliceIndex[leftAddr] != sliceIdx;
            if (!crossSlice || idc != 2)
            {
                leftMb = ref frameMacroblocks![leftAddr];
                hasLeftMb = true;
                leftMbAddr = leftAddr;
            }
        }
        
        if (currentIsField)
        {
            // Field-coded MB at top of pair: top neighbor from pair above.
            // FFmpeg fill_filter_caches (h264_slice.c:2308-2310):
            //   top_xy = mb_xy - 2*mb_stride
            //   if pair above is frame-coded: top_xy += mb_stride → use BOTTOM of pair above
            //   if pair above is field-coded: no adjustment → use TOP of pair above (same field)
            int pairRow = mbY / 2;
            if (pairRow > 0)
            {
                int topAddr = mbAddr - 2 * mbWidth;
                
                // Adjust: if current is top-of-pair and pair above is frame-coded,
                // use bottom of pair above (mbAddr - mbWidth) instead of top (mbAddr - 2*mbWidth)
                if ((mbY & 1) == 0 && mbFieldDecodingFlag != null)
                {
                    int topPairBottomAddr = mbAddr - mbWidth;
                    if (topPairBottomAddr >= 0 && !mbFieldDecodingFlag[topPairBottomAddr])
                        topAddr = topPairBottomAddr;
                }
                
                if (topAddr >= 0)
                {
                    bool crossSlice = mbSliceIndex[topAddr] != sliceIdx;
                    if (!crossSlice || idc != 2)
                    {
                        topMb = ref frameMacroblocks![topAddr];
                hasTopMb = true;
                        topMbAddr = topAddr;
                    }
                }
            }
        }
        else if (mbY > 0)
        {
            int topAddr = mbAddr - mbWidth;
            bool crossSlice = mbSliceIndex[topAddr] != sliceIdx;
            if (!crossSlice || idc != 2)
            {
                topMb = ref frameMacroblocks![topAddr];
                hasTopMb = true;
                topMbAddr = topAddr;
            }
        }
        
        bool leftIsField = isMbaffFrame && mbFieldDecodingFlag != null && leftMbAddr >= 0 && mbFieldDecodingFlag[leftMbAddr];
        bool topIsField = isMbaffFrame && mbFieldDecodingFlag != null && topMbAddr >= 0 && mbFieldDecodingFlag[topMbAddr];
        
        // Special MBAFF horizontal edge 0: frame-coded current at top-of-pair with
        // field-coded pair above. FFmpeg h264_loopfilter.c:494-542.
        // Condition: IS_INTERLACED(mbm_type & ~mb_type) — neighbor interlaced, current NOT.
        // Filter edge 0 with doubled stride in two passes (one per field from above pair).
        // NOTE: This must run AFTER V-edges but BEFORE H-edges (FFmpeg order: V→special-H→H).
        bool skipHorizontalEdge0 = false;
        bool needsMbaffSpecialHEdge0 = false;
        if (isMbaffFrame && !currentIsField && (mbY & 1) == 0 && mbY > 0)
        {
            int topNeighborAddr = mbAddr - mbWidth;
            bool topNeighborIsField = mbFieldDecodingFlag != null && topNeighborAddr >= 0 && mbFieldDecodingFlag[topNeighborAddr];
            if (topNeighborIsField)
            {
                skipHorizontalEdge0 = true;
                needsMbaffSpecialHEdge0 = true;
            }
        }
        
        // MBAFF pair boundary vertical edge: current and left pair have different interlacing.
        // FFmpeg ff_h264_filter_mb (h264_loopfilter.c:730-832).
        // Uses MBAFF-specific 8-row filter functions with half-stride.
        bool skipVerticalEdge0 = false;
        if (isMbaffFrame && hasLeftMb && currentIsField != leftIsField)
        {
            skipVerticalEdge0 = true;
            FilterMbaffPairBoundaryVertical(
                picture.Luma, lumaStride, picture.ChromaCb, picture.ChromaCr, chromaStride,
                mbX, mbY, mbAddr, mbWidth, sliceIdx,
                frameMacroblocks![mbAddr].QuantizationParameter,
                filterOffsetA, filterOffsetB,
                picture.ChromaQpIndexOffsetCb, picture.ChromaQpIndexOffsetCr,
                currentIsField, leftIsField,
                pixelShift: pixelShift, bitDepth: bitDepth);
        }
        
        // One write per field that actually moved. The point of holding this context across
        // the picture is that most of it does not move: the strides, the base offsets and the
        // interlacing flags are what a progressive picture's macroblocks all share, so they are
        // set once by the caller and touched here only where MBAFF can change them. Writing them
        // unconditionally measured flat against the forty-argument call it replaced - the stores
        // had moved rather than gone.
        mb.MbX = mbX;
        mb.MbY = mbY;
        mb.Qp = frameMacroblocks![mbAddr].QuantizationParameter;
        mb.FilterOffsetA = filterOffsetA;
        mb.FilterOffsetB = filterOffsetB;
        mb.CurrentMbAddr = mbAddr;
        mb.LeftMbAddr = leftMbAddr;
        mb.TopMbAddr = topMbAddr;
        mb.HasLeftMb = hasLeftMb;
        mb.HasTopMb = hasTopMb;

        if (isMbaffFrame)
        {
            mb.LumaStride = effectiveLumaStride;
            mb.ChromaStride = effectiveChromaStride;
            mb.LumaBaseOffset = lumaBaseOffset;
            mb.ChromaCbBaseOffset = chromaCbBaseOffset;
            mb.ChromaCrBaseOffset = chromaCrBaseOffset;
            mb.CurrentMbIsField = currentIsField;
            mb.LeftMbIsField = leftIsField;
            mb.TopMbIsField = topIsField;
        }

        if (needsMbaffSpecialHEdge0)
        {
            // FFmpeg order: V-edges first, then special H-edge 0, then regular H-edges.
            // Split FilterMacroblock into V-only pass, then special H-edge, then H-only pass.
            mb.SkipHorizontalEdge0 = true;
            mb.SkipVerticalEdge0 = skipVerticalEdge0;
            mb.VerticalOnly = true;
            mb.HorizontalOnly = false;
            DeblockingFilter.FilterMacroblock(
                picture, mb, leftMb, topMb, frameMacroblocks[mbAddr]);
            
            FilterMbaffHorizontalEdge0(
                picture.Luma, lumaStride, picture.ChromaCb, picture.ChromaCr, chromaStride,
                mbX, mbY, mbAddr, mbWidth, sliceIdx,
                frameMacroblocks![mbAddr].QuantizationParameter,
                filterOffsetA, filterOffsetB,
                picture.ChromaQpIndexOffsetCb, picture.ChromaQpIndexOffsetCr,
                pixelShift: pixelShift, bitDepth: bitDepth);
            
            mb.SkipHorizontalEdge0 = true;
            mb.SkipVerticalEdge0 = true;
            mb.VerticalOnly = false;
            mb.HorizontalOnly = true;
            DeblockingFilter.FilterMacroblock(
                picture, mb, leftMb, topMb, frameMacroblocks[mbAddr]);
        }
        else
        {
            mb.SkipHorizontalEdge0 = skipHorizontalEdge0;
            mb.SkipVerticalEdge0 = skipVerticalEdge0;
            if (isMbaffFrame)
            {
                mb.VerticalOnly = false;
                mb.HorizontalOnly = false;
            }
            DeblockingFilter.FilterMacroblock(
                picture, mb, leftMb, topMb, frameMacroblocks[mbAddr]);
        }
    }
    
    /// <summary>
    /// Special MBAFF horizontal edge 0 filter for frame-coded MB at top of pair when
    /// the pair above has field-coded MBs. FFmpeg h264_loopfilter.c:494-542.
    /// Filters with doubled stride in two passes: even rows vs top-field neighbor,
    /// odd rows vs bottom-field neighbor from the pair above.
    /// </summary>
    private void FilterMbaffHorizontalEdge0(
        Span<byte> luma, int lumaStride,
        Span<byte> chromaCb, Span<byte> chromaCr, int chromaStride,
        int mbX, int mbY, int mbAddr, int mbWidth, int sliceIdx,
        int currentQp, int filterOffsetA, int filterOffsetB,
        int chromaQpIndexOffsetCb, int chromaQpIndexOffsetCr,
        int pixelShift = 0, int bitDepth = 8)
    {
        int maxPixelValue = (1 << bitDepth) - 1;
        int bitDepthScale = 1 << (bitDepth - 8);
        int qpBdOffset = 6 * (bitDepth - 8);
        int lumaX = mbX * 16;
        int lumaY = mbY * 16;
        int chromaX = mbX * 8;
        int chromaY = isChroma422 ? mbY * 16 : mbY * 8;
        int doubledLumaStride = lumaStride * 2;
        int doubledChromaStride = chromaStride * 2;
        
        // Two top neighbors from the pair above: top field MB and bottom field MB
        int topFieldAddr = mbAddr - 2 * mbWidth;
        int bottomFieldAddr = mbAddr - mbWidth;
        
        for (int j = 0; j < 2; j++)
        {
            int neighborAddr = (j == 0) ? topFieldAddr : bottomFieldAddr;
            if (neighborAddr < 0) continue;
            
            bool crossSlice = mbSliceIndex![neighborAddr] != sliceIdx;
            uint idc = sliceDeblockIdc != null ? sliceDeblockIdc[sliceIdx] : 0;
            if (crossSlice && idc == 2) continue;
            
            int neighborQp = frameMacroblocks![neighborAddr].QuantizationParameter;
            bool neighborIsIntra = frameMacroblocks[neighborAddr].IsIntra;
            bool currentIsIntra = frameMacroblocks[mbAddr].IsIntra;
            
            // QP averaging
            int lumaQp = (currentQp + neighborQp + 1) >> 1;
            int lumaIndexA = Math.Clamp(lumaQp - qpBdOffset + filterOffsetA, 0, 51);
            int lumaIndexB = Math.Clamp(lumaQp - qpBdOffset + filterOffsetB, 0, 51);
            int lumaAlpha = DeblockingFilter.GetAlpha(lumaIndexA) * bitDepthScale;
            int lumaBeta = DeblockingFilter.GetBeta(lumaIndexB) * bitDepthScale;
            
            int chromaQpCb = (DeblockingFilter.LumaToChromaQp(currentQp - qpBdOffset, chromaQpIndexOffsetCb)
                + DeblockingFilter.LumaToChromaQp(neighborQp - qpBdOffset, chromaQpIndexOffsetCb) + 1) >> 1;
            int chromaQpCr = (DeblockingFilter.LumaToChromaQp(currentQp - qpBdOffset, chromaQpIndexOffsetCr)
                + DeblockingFilter.LumaToChromaQp(neighborQp - qpBdOffset, chromaQpIndexOffsetCr) + 1) >> 1;
            int chromaIndexACb = Math.Clamp(chromaQpCb + filterOffsetA, 0, 51);
            int chromaAlphaCb = DeblockingFilter.GetAlpha(chromaIndexACb) * bitDepthScale;
            int chromaBetaCb = DeblockingFilter.GetBeta(Math.Clamp(chromaQpCb + filterOffsetB, 0, 51)) * bitDepthScale;
            int chromaIndexACr = Math.Clamp(chromaQpCr + filterOffsetA, 0, 51);
            int chromaAlphaCr = DeblockingFilter.GetAlpha(chromaIndexACr) * bitDepthScale;
            int chromaBetaCr = DeblockingFilter.GetBeta(Math.Clamp(chromaQpCr + filterOffsetB, 0, 51)) * bitDepthScale;
            
            // Filter 16 luma columns with doubled stride
            // j=0 → even rows (top field partner), j=1 → odd rows (bottom field partner)
            for (int seg = 0; seg < 4; seg++)
            {
                int bS;
                if (currentIsIntra || neighborIsIntra)
                {
                    bS = 3;  // Always 3 at interlaced horizontal boundary
                }
                else
                {
                    bool hasCoeffsP = frameMacroblocks[neighborAddr].HasNonZeroCoeffs4x4(seg, 3);
                    bool hasCoeffsQ = frameMacroblocks[mbAddr].HasNonZeroCoeffs4x4(seg, 0);
                    bS = (hasCoeffsP || hasCoeffsQ) ? 2 : 1;
                }
                
                if (bS == 0) continue;
                
                for (int col = 0; col < 4; col++)
                {
                    int x = seg * 4 + col;
                    int offset = (lumaY + j) * lumaStride + lumaX + x;
                    DeblockingFilter.FilterEdgeLumaPublic(luma, offset, doubledLumaStride,
                        lumaAlpha, lumaBeta, bS, lumaIndexA,
                        pixelShift, maxPixelValue, bitDepthScale);
                }
            }
            
            // Filter 8 chroma columns with doubled stride.
            // FFmpeg uses 4 bS values (one per luma 4x4 block column), each applied to
            // 2 chroma pixels (inner_iters=2 in h264_loop_filter_chroma).
            for (int lumaSeg = 0; lumaSeg < 4; lumaSeg++)
            {
                int bS;
                if (currentIsIntra || neighborIsIntra)
                    bS = 3;
                else
                {
                    bool hasCoeffsP = frameMacroblocks[neighborAddr].HasNonZeroCoeffs4x4(lumaSeg, 3);
                    bool hasCoeffsQ = frameMacroblocks[mbAddr].HasNonZeroCoeffs4x4(lumaSeg, 0);
                    bS = (hasCoeffsP || hasCoeffsQ) ? 2 : 1;
                }
                
                if (bS == 0) continue;
                
                for (int col = 0; col < 2; col++)
                {
                    int x = lumaSeg * 2 + col;
                    int cbOffset = (chromaY + j) * chromaStride + chromaX + x;
                    int crOffset = cbOffset;
                    DeblockingFilter.FilterEdgeChromaPublic(chromaCb, cbOffset, doubledChromaStride,
                        chromaAlphaCb, chromaBetaCb, bS, chromaIndexACb,
                        pixelShift, maxPixelValue, bitDepthScale);
                    DeblockingFilter.FilterEdgeChromaPublic(chromaCr, crOffset, doubledChromaStride,
                        chromaAlphaCr, chromaBetaCr, bS, chromaIndexACr,
                        pixelShift, maxPixelValue, bitDepthScale);
                }
            }
        }
    }

    /// <summary>
    /// MBAFF pair boundary vertical edge filter. FFmpeg ff_h264_filter_mb (h264_loopfilter.c:730-832).
    /// Called when current and left pair have different interlacing (field vs frame).
    /// Uses 8 bS values (4 per left pair member), 2 QPs (one per left pair member),
    /// and MBAFF-specific filter functions that process consecutive physical rows
    /// (ystride = effectiveStride/2) instead of field rows.
    /// </summary>
    private void FilterMbaffPairBoundaryVertical(
        Span<byte> luma, int lumaStride,
        Span<byte> chromaCb, Span<byte> chromaCr, int chromaStride,
        int mbX, int mbY, int mbAddr, int mbWidth, int sliceIdx,
        int currentQp, int filterOffsetA, int filterOffsetB,
        int chromaQpIndexOffsetCb, int chromaQpIndexOffsetCr,
        bool currentIsField, bool leftIsField,
        int pixelShift = 0, int bitDepth = 8)
    {
        int maxPixelValue = (1 << bitDepth) - 1;
        int bitDepthScale = 1 << (bitDepth - 8);
        int qpBdOffset = 6 * (bitDepth - 8);
        // Compute 8 bS values. For intra MBs: all bS=4 (vertical edges in MBAFF always use 4).
        // For inter: bS from NNZ/MV of each left pair member.
        // FFmpeg h264_loopfilter.c:746-774
        Scratch.Of8<int> bSStorage = default;
        Span<int> bS = bSStorage;
        
        bool currentIsIntra = frameMacroblocks![mbAddr].IsIntra;
        
        if (currentIsIntra)
        {
            bS.Fill(4);
        }
        else
        {
            // The left pair has two members: leftTop (mbAddr-1 with same mbY parity)
            // and leftBottom (mbAddr-1 with opposite parity)
            int leftPairTopAddr = (mbAddr - 1) & ~0; // same as mbAddr - 1 for top
            int leftPairBottomAddr;
            
            // In the MBAFF grid, for current at mbAddr (within pair column mbX),
            // the left pair occupies addresses (mbAddr-1) and (mbAddr-1 ± mbWidth) depending on position.
            // But since we iterate pair by pair (top then bottom), the left pair is at:
            //   left top = (pairRow*2)*mbWidth + (mbX-1)
            //   left bottom = (pairRow*2+1)*mbWidth + (mbX-1)
            int pairRow = mbY / 2;
            leftPairTopAddr = pairRow * 2 * mbWidth + (mbX - 1);
            leftPairBottomAddr = (pairRow * 2 + 1) * mbWidth + (mbX - 1);
            
            // FFmpeg offset table for NNZ lookup — selects which 4x4 block row of the
            // left neighbor maps to each of the 8 bS positions.
            // offset[MB_FIELD][mb_y&1][i] — h264_loopfilter.c:750-758
            // Frame-coded current (MB_FIELD=0):
            //   top-of-pair (mb_y&1=0): {3+4*0, 3+4*0, 3+4*0, 3+4*0, 3+4*1, 3+4*1, 3+4*1, 3+4*1}
            //   bot-of-pair (mb_y&1=1): {3+4*2, 3+4*2, 3+4*2, 3+4*2, 3+4*3, 3+4*3, 3+4*3, 3+4*3}
            // Field-coded current (MB_FIELD=1):
            //   both parities:          {3+4*0, 3+4*1, 3+4*2, 3+4*3, 3+4*0, 3+4*1, 3+4*2, 3+4*3}
            for (int i = 0; i < 8; i++)
            {
                // j selects which left pair member to use:
                // Field-coded current: j = i >> 2 (first 4 from top, last 4 from bottom)
                // Frame-coded current: j = i & 1 (alternating top/bottom)
                int j = currentIsField ? (i >> 2) : (i & 1);
                int leftAddr = (j == 0) ? leftPairTopAddr : leftPairBottomAddr;
                
                if (leftAddr < 0 || leftAddr >= frameMacroblocks.Length)
                {
                    bS[i] = 0;
                    continue;
                }
                
                bool leftMbIsIntra = frameMacroblocks[leftAddr].IsIntra;
                if (leftMbIsIntra)
                {
                    bS[i] = 4;
                }
                else
                {
                    // Current NNZ: left column (x4=0) at row i>>1
                    // FFmpeg: non_zero_count_cache[12+8*(i>>1)]
                    bool currentNnz = frameMacroblocks[mbAddr].HasNonZeroCoeffs4x4(0, i >> 1);
                    
                    // Left neighbor NNZ: uses FFmpeg offset table for correct block mapping.
                    // The offset values (3,7,11,15) map to (x4=3, y4=0..3).
                    int leftRow;
                    if (currentIsField)
                    {
                        // Field: all 4 rows sequentially per half: i&3 → y4=0,1,2,3
                        leftRow = i & 3;
                    }
                    else
                    {
                        // Frame: groups of 4 bS share same row, shifted by parity.
                        // top-of-pair (mbY&1==0): i<4 → y4=0, i>=4 → y4=1
                        // bot-of-pair (mbY&1==1): i<4 → y4=2, i>=4 → y4=3
                        leftRow = (mbY & 1) * 2 + (i >> 2);
                    }
                    
                    bool leftNnz = frameMacroblocks[leftAddr].HasNonZeroCoeffs4x4(3, leftRow);
                    bS[i] = (currentNnz || leftNnz) ? 2 : 1;
                }
            }
        }
        
        // Two left pair members' QPs
        int pairRowQ = mbY / 2;
        int leftTopAddr = pairRowQ * 2 * mbWidth + (mbX - 1);
        int leftBotAddr = (pairRowQ * 2 + 1) * mbWidth + (mbX - 1);
        
        int mbQp = currentQp;
        int leftTopQp = (leftTopAddr >= 0 && leftTopAddr < frameMacroblocks.Length)
            ? frameMacroblocks[leftTopAddr].QuantizationParameter : currentQp;
        int leftBotQp = (leftBotAddr >= 0 && leftBotAddr < frameMacroblocks.Length)
            ? frameMacroblocks[leftBotAddr].QuantizationParameter : currentQp;
        
        int[] lumaQp = { (mbQp + leftTopQp + 1) >> 1, (mbQp + leftBotQp + 1) >> 1 };
        
        int chromaQpCurCb = DeblockingFilter.LumaToChromaQp(mbQp - qpBdOffset, chromaQpIndexOffsetCb);
        int chromaQpCurCr = DeblockingFilter.LumaToChromaQp(mbQp - qpBdOffset, chromaQpIndexOffsetCr);
        int[] chromaQpCb = {
            (chromaQpCurCb + DeblockingFilter.LumaToChromaQp(leftTopQp - qpBdOffset, chromaQpIndexOffsetCb) + 1) >> 1,
            (chromaQpCurCb + DeblockingFilter.LumaToChromaQp(leftBotQp - qpBdOffset, chromaQpIndexOffsetCb) + 1) >> 1
        };
        int[] chromaQpCr = {
            (chromaQpCurCr + DeblockingFilter.LumaToChromaQp(leftTopQp - qpBdOffset, chromaQpIndexOffsetCr) + 1) >> 1,
            (chromaQpCurCr + DeblockingFilter.LumaToChromaQp(leftBotQp - qpBdOffset, chromaQpIndexOffsetCr) + 1) >> 1
        };
        
        // Compute pixel base position for this MB
        // FFmpeg: dest_y = data + (mbX*16 + mbY * linesize * 16)
        // For field-coded bottom: dest_y -= linesize * 15
        int lumaBaseY = mbY * 16 * lumaStride + mbX * 16;
        int chromaBaseY = mbY * (isChroma422 ? 16 : 8) * chromaStride + mbX * 8;
        if (currentIsField && (mbY & 1) != 0)
        {
            lumaBaseY -= lumaStride * 15;
            chromaBaseY -= chromaStride * (isChroma422 ? 15 : 7);
        }
        
        // FFmpeg h264_loopfilter.c:794-831: dispatch based on MB_FIELD
        // In BOTH cases, the filter stride is 2*original_linesize.
        // MBAFF luma filter: 8 rows (4 bS groups × 2 rows each) at the given stride.
        // MBAFF chroma filter (4:2:0): 4 rows (4 bS groups × 1 row each) at the given stride.
        // Two calls per MB cover all rows: field splits top/bottom halves,
        // frame splits even/odd rows.
        int lumaFilterStride = 2 * lumaStride;
        int chromaFilterStride = 2 * chromaStride;
        
        // Compute 4 bS values per call using bsi-based indexing from the 8-element bS array.
        // Field (bsi=1): Call A uses bS[0..3], Call B uses bS[4..7]
        // Frame (bsi=2): Call A uses bS[0,2,4,6], Call B uses bS[1,3,5,7]
        Scratch.Of4<int> bsCallAStorage = default;
        Span<int> bsCallA = bsCallAStorage;
        Scratch.Of4<int> bsCallBStorage = default;
        Span<int> bsCallB = bsCallBStorage;
        
        int callAStart, callBStart;
        int chromaCallAStart, chromaCallBStart;
        
        if (currentIsField)
        {
            // Field: bsi=1 → straight indexing
            // Call A at img_y, stride=linesize(=2*orig), bS[0..3]
            // Call B at img_y + 8*linesize, stride=linesize, bS[4..7]
            int linesize = 2 * lumaStride;
            int uvlinesize = 2 * chromaStride;
            
            for (int k = 0; k < 4; k++) bsCallA[k] = bS[k];
            for (int k = 0; k < 4; k++) bsCallB[k] = bS[4 + k];
            
            callAStart = lumaBaseY;
            callBStart = lumaBaseY + 8 * linesize;
            chromaCallAStart = chromaBaseY;
            chromaCallBStart = chromaBaseY + (isChroma422 ? 8 : 4) * uvlinesize;
        }
        else
        {
            // Frame: bsi=2 → every-other indexing
            // Call A at img_y, stride=2*linesize(=2*orig), bS[0,2,4,6]
            // Call B at img_y + linesize, stride=2*linesize, bS[1,3,5,7]
            for (int k = 0; k < 4; k++) bsCallA[k] = bS[k * 2];
            for (int k = 0; k < 4; k++) bsCallB[k] = bS[1 + k * 2];
            
            callAStart = lumaBaseY;
            callBStart = lumaBaseY + lumaStride;
            chromaCallAStart = chromaBaseY;
            chromaCallBStart = chromaBaseY + chromaStride;
        }
        
        // Apply luma filter: 2 calls × 8 rows each = 16 rows per MB
        for (int call = 0; call < 2; call++)
        {
            int start = (call == 0) ? callAStart : callBStart;
            ReadOnlySpan<int> bsLocal = (call == 0) ? bsCallA : bsCallB;
            int qpIdx = call;
            
            int indexA = Math.Clamp(lumaQp[qpIdx] - qpBdOffset + filterOffsetA, 0, 51);
            int indexB = Math.Clamp(lumaQp[qpIdx] - qpBdOffset + filterOffsetB, 0, 51);
            int alpha = DeblockingFilter.GetAlpha(indexA) * bitDepthScale;
            int beta = DeblockingFilter.GetBeta(indexB) * bitDepthScale;
            if (alpha == 0 || beta == 0) continue;
            
            // 4 groups × 2 rows = 8 rows at lumaFilterStride
            for (int group = 0; group < 4; group++)
            {
                int bs = bsLocal[group];
                if (bs == 0) continue;
                
                for (int row = 0; row < 2; row++)
                {
                    int offset = start + (group * 2 + row) * lumaFilterStride;
                    int byteOff = offset << pixelShift;
                    if (byteOff < (3 << pixelShift) || byteOff + (3 << pixelShift) >= luma.Length) continue;
                    DeblockingFilter.FilterEdgeLumaPublic(luma, offset, 1, alpha, beta, bs, indexA,
                        pixelShift, maxPixelValue, bitDepthScale);
                }
            }
        }
        
        // Apply chroma filter: 2 calls × 4 rows each = 8 rows per MB
        for (int call = 0; call < 2; call++)
        {
            int start = (call == 0) ? chromaCallAStart : chromaCallBStart;
            ReadOnlySpan<int> bsLocal = (call == 0) ? bsCallA : bsCallB;
            int qpIdx = call;
            
            int cIndexACb = Math.Clamp(chromaQpCb[qpIdx] + filterOffsetA, 0, 51);
            int cAlphaCb = DeblockingFilter.GetAlpha(cIndexACb) * bitDepthScale;
            int cBetaCb = DeblockingFilter.GetBeta(Math.Clamp(chromaQpCb[qpIdx] + filterOffsetB, 0, 51)) * bitDepthScale;
            int cIndexACr = Math.Clamp(chromaQpCr[qpIdx] + filterOffsetA, 0, 51);
            int cAlphaCr = DeblockingFilter.GetAlpha(cIndexACr) * bitDepthScale;
            int cBetaCr = DeblockingFilter.GetBeta(Math.Clamp(chromaQpCr[qpIdx] + filterOffsetB, 0, 51)) * bitDepthScale;
            if (cAlphaCb == 0 && cAlphaCr == 0) continue;
            
            // 4:2:0: 4 groups × 1 row = 4 rows; 4:2:2: 4 groups × 2 rows = 8 rows
            int chromaRowsPerGroup = isChroma422 ? 2 : 1;
            for (int group = 0; group < 4; group++)
            {
                int bs = bsLocal[group];
                if (bs == 0) continue;
                
                for (int row = 0; row < chromaRowsPerGroup; row++)
                {
                    int cbOff = start + (group * chromaRowsPerGroup + row) * chromaFilterStride;
                    int cbByteOff = cbOff << pixelShift;
                    if (cbByteOff < (3 << pixelShift) || cbByteOff + (3 << pixelShift) >= chromaCb.Length) continue;
                    if (cAlphaCb != 0 && cBetaCb != 0)
                    {
                        DeblockingFilter.FilterEdgeChromaPublic(chromaCb, cbOff, 1, cAlphaCb, cBetaCb, bs, cIndexACb,
                            pixelShift, maxPixelValue, bitDepthScale);
                    }
                    if (cAlphaCr != 0 && cBetaCr != 0)
                    {
                        DeblockingFilter.FilterEdgeChromaPublic(chromaCr, cbOff, 1, cAlphaCr, cBetaCr, bs, cIndexACr,
                            pixelShift, maxPixelValue, bitDepthScale);
                    }
                }
            }
        }
    }

    /// <summary>
    /// reference frames for each missing frame_num value. These synthetic frames have
    /// no pixel data but occupy DPB slots for correct sliding window eviction.
    /// </summary>
    /// <summary>
    /// <c>h264_field_start</c>'s "Shorten frame num gaps so we don't have to allocate reference
    /// frames just to throw them away". A gap wider than the SPS's reference frame count is
    /// replaced by one exactly that wide: everything older can no longer be referenced by
    /// anything, so walking the rest of the gap builds pictures to evict them again.
    /// </summary>
    /// <remarks>
    /// This is not an optimisation, it is part of the decode. <c>h264_intra_first-small.ts</c>
    /// starts mid-GOP at frame_num 351 with prev_frame_num 0 and <c>ref_frame_count</c> 3, so
    /// without the clamp this decoder walked THREE HUNDRED AND FIFTY gap pictures where ffmpeg
    /// walks three - renting and filling a 1920x1088 buffer for each one, and arriving at a
    /// different DPB, a different <c>prevFrameNum</c> and therefore a different POC for the
    /// first real picture. ffmpeg's own log says "Frame num gap 351 347", and 347 is this
    /// arithmetic: 351 - 3 - 1.
    /// </remarks>
    private void ShortenFrameNumGap(SequenceParameterSet sps, int frameNum)
    {
        if (frameNum == prevFrameNum)
            return;

        int unwrapPrevFrameNum = prevFrameNum;
        int maxFrameNum = sps.MaxFrameNumber;

        if (unwrapPrevFrameNum > frameNum)
            unwrapPrevFrameNum -= maxFrameNum;

        if (frameNum - unwrapPrevFrameNum > sps.MaxNumRefFrames)
        {
            unwrapPrevFrameNum = frameNum - sps.MaxNumRefFrames - 1;
            if (unwrapPrevFrameNum < 0)
                unwrapPrevFrameNum += maxFrameNum;

            prevFrameNum = unwrapPrevFrameNum;
        }
    }

    private void HandleFrameNumGap(SequenceParameterSet sps, int currentFrameNum)
    {
        int maxFrameNum = sps.MaxFrameNumber;
        int expectedFrameNum = (prevFrameNum + 1) % maxFrameNum;
        
        if (currentFrameNum == prevFrameNum || currentFrameNum == expectedFrameNum)
            return; // No gap
        
        DiagnosticOutput?.WriteLine($"FRAME-GAP: detected gap from fn={prevFrameNum} to fn={currentFrameNum} (expected {expectedFrameNum})");
        
        // Safety limit: avoid infinite loop if frame_num is corrupt
        int maxGapFrames = maxFrameNum;
        int gapCount = 0;
        
        int gapFrameNum = expectedFrameNum;
        while (gapFrameNum != currentFrameNum && gapCount < maxGapFrames)
        {
            // Create a synthetic non-existing reference frame. h264_slice.c does not leave
            // it blank: if there is a previous short-term reference of the same geometry it
            // copies that one ("Error concealment: If a ref is missing, copy the previous ref
            // in its place"), and only when there is none, and no IDR has been seen, does it
            // paint the picture mid-grey with color_frame. A zero fill is black, which is
            // neither, and is what this did - every concealed picture differed from ffmpeg's
            // in every pixel.
            int frameSize = currentFrameBuffer?.Length ?? 0;
            byte[]? gapBuffer = null;
            bool gapIsGray = false;
            int gapPoc = 0;
            if (frameSize > 0)
            {
                gapBuffer = RentBuffer(frameSize);

                // h->short_ref[0], read BEFORE this iteration's marking inserts the new
                // picture - so the first gap of a mid-GOP start has none and is painted.
                int prevIdx = MostRecentShortTermIndex();
                byte[]? prev = prevIdx >= 0 ? dpb[prevIdx].Buffer : null;

                if (prev != null && prev.Length >= frameSize)
                {
                    prev.AsSpan(0, frameSize).CopyTo(gapBuffer);
                    gapIsGray = dpb[prevIdx].Gray;

                    // h->short_ref[0]->poc = prev->poc + 2U. Not cosmetic: deblocking asks
                    // whether two neighbouring blocks name the same reference picture, and
                    // POC is how a picture is named. Every gap picture sharing POC 0 made
                    // them all one picture to that test, and the boundary strengths came out
                    // wrong by one all over the second field.
                    gapPoc = dpb[prevIdx].Poc + 2;
                }
                else
                {
                    Array.Clear(gapBuffer);
                    if (frameRecovered == 0)
                    {
                        PaintGray(gapBuffer);
                        gapIsGray = true;
                    }
                }
            }
            
            // Apply sliding window eviction before adding (same as StoreReferenceFrame)
            int maxFrames = Math.Min(MaxDpbFrames, context.MaxRefFrames);
            while (dpbCount >= maxFrames)
            {
                int minIdx = -1;
                int minPicNum = int.MaxValue;
                for (int i = 0; i < dpbCount; i++)
                {
                    if (dpb[i].IsLongTerm) continue;
                    int picNum = dpb[i].FrameNum > gapFrameNum
                        ? dpb[i].FrameNum - maxFrameNum : dpb[i].FrameNum;
                    if (picNum < minPicNum) { minPicNum = picNum; minIdx = i; }
                }
                if (minIdx < 0) break;
                RemoveDpbEntry(minIdx);
            }
            
            // Add synthetic frame to DPB as short-term reference
            if (dpbCount < dpb.Length)
            {
                dpb[dpbCount] = new ReferenceFrame
                {
                    Buffer = gapBuffer,
                    FrameNum = gapFrameNum,
                    // DIVERGENCE: in the grey branch the C leaves whatever POC the
                    // recycled DPB slot held - h264_frame_start does not set one - so there
                    // is no value to copy. Zero at least keeps the first gap picture
                    // distinct from the copies, which count up from it.
                    Poc = gapPoc,
                    IsLongTerm = false,
                    LongTermFrameIdx = -1,
                    Gray = gapIsGray,
                    // h264_frame_start gives the gap picture a reference state like any
                    // other. Leaving these at zero made a frame_num gap stand-in invisible
                    // to field reference list construction, which filters on them, so a
                    // field picture's list was shorter than ffmpeg's.
                    PictureStructure = ReferenceFrame.PictFrame,
                    Reference = ReferenceFrame.PictFrame,
                    TopFieldPoc = gapPoc,
                    BottomFieldPoc = gapPoc,
                    RefPoc = gapPoc,
                };
                dpbCount++;
            }
            
            DiagnosticOutput?.WriteLine($"FRAME-GAP: inserted synthetic ref fn={gapFrameNum} dpbCount={dpbCount}");
            
            // Advance prevFrameNum so POC calculations for the real frame see correct state
            prevFrameNum = gapFrameNum;
            
            gapFrameNum = (gapFrameNum + 1) % maxFrameNum;
            gapCount++;
        }
    }

    /// <summary>
    /// <c>ff_h264_sei_decode</c>, for the one message this decoder acts on. The loop over
    /// payload type and size is the C's; every other type is skipped by its stated length.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: the C dispatches a dozen message types into an H264SEIContext. Only
    /// <c>SEI_TYPE_RECOVERY_POINT</c> (6) changes what this decoder outputs, so the rest are
    /// stepped over rather than parsed into fields nothing reads. Adding one is adding a
    /// case; what must not be added is a guess about a length, which is why an ill-formed
    /// size abandons the whole NAL rather than resynchronising.
    /// </remarks>
    private void DecodeSei(ReadOnlySpan<byte> payload)
    {
        NalUnitParser.RemoveEmulationPreventionBytesInto(
            payload, ref seiRbsp, out int length, ref seiRbspRemoved, out _);
        ReadOnlySpan<byte> rbsp = seiRbsp.AsSpan(0, length);

        int at = 0;
        while (rbsp.Length - at > 2 && (rbsp[at] != 0 || rbsp[at + 1] != 0))
        {
            int type = 0, size = 0;
            byte b;
            do
            {
                if (at >= rbsp.Length) return;
                b = rbsp[at++];
                type += b;
            } while (b == 255);

            do
            {
                if (at >= rbsp.Length) return;
                b = rbsp[at++];
                size += b;
            } while (b == 255);

            if (size > rbsp.Length - at)
            {
                DiagnosticOutput?.WriteLine($"SEI: type {type} size {size} truncated");
                return;
            }

            if (type == 6)
                DecodeRecoveryPoint(rbsp.Slice(at, size));

            at += size;
        }
    }

    /// <summary><c>av_zero_extend</c> - keep the low <paramref name="bits"/> bits.</summary>
    private static int ZeroExtend(int value, int bits) => value & ((1 << bits) - 1);

    /// <summary><c>decode_recovery_point</c>.</summary>
    private void DecodeRecoveryPoint(ReadOnlySpan<byte> payload)
    {
        var reader = new BitstreamReader(payload);
        uint count = reader.ReadExpGolombUnsigned();
        if (count >= 1u << MaxLog2MaxFrameNum)
        {
            DiagnosticOutput?.WriteLine($"SEI: recovery_frame_cnt {count} out of range");
            return;
        }

        seiRecoveryFrameCount = (int)count;
        hasRecoveryPoint = true;
        // 1b exact_match_flag, 1b broken_link_flag, 2b changing_slice_group_idc: read by the
        // C and used by nothing, kept so the message's shape is visible here.
    }

    private byte[]? seiRbsp;
    private int[]? seiRbspRemoved;

    /// <summary>
    /// The recovery and grey state of the picture that has just finished decoding:
    /// <c>h264_slice.c</c>'s IDR arm and <c>h264_refs.c</c>'s unmarked-random-access
    /// heuristic, which is what lets a stream with no IDR in it ever produce a picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heuristic is the one that matters for a stream joined mid-GOP. There is no IDR and
    /// no recovery point SEI; what ffmpeg does instead is notice that an I picture arrived
    /// while the reference counts are small enough that nothing before it can still be
    /// referenced, and declare the stream recovered from there. The condition is copied from
    /// <c>ff_h264_execute_ref_pic_marking</c> rather than reasoned about - the three
    /// alternatives in the middle are not equivalent and the port has no business tidying
    /// them.
    /// </para>
    /// <para>
    /// DIVERGENCE: the C reads <c>pps_ref_count</c> across every PPS it has parsed, through
    /// <c>h-&gt;ps.pps_list</c>; here the context walks its PPS map for the same maximum.
    /// </para>
    /// </remarks>
    private void MarkPictureRecovered()
    {
        if (seiRecoveryFrameCount >= 0)
        {
            int count = seiRecoveryFrameCount;
            int log2MaxFrameNum = context.ActiveSps?.Log2MaxFrameNumber ?? 4;

            if (currentFrameNum != count || !currentPictureIsIntra)
                validRecoveryPoint = true;

            if (recoveryFrame < 0
                || ZeroExtend(recoveryFrame - currentFrameNum, log2MaxFrameNum) > count)
            {
                recoveryFrame = ZeroExtend(currentFrameNum + count, log2MaxFrameNum);

                // A stream whose first recovery point names its own frame as an I picture
                // has told us nothing we can check, so it recovers immediately rather than
                // after a count we have no reason to trust.
                if (!validRecoveryPoint)
                    recoveryFrame = currentFrameNum;
            }
        }

        if (currentIsIdr)
        {
            // An IDR makes every picture after it in DECODED order decodable.
            currentPictureRecovered |= FrameRecoveredIdr;
            frameRecovered |= FrameRecoveredIdr;
            hasRecoveryPoint = true;
        }

        if (recoveryFrame == currentFrameNum && currentIsReference)
        {
            recoveryFrame = -1;
            currentPictureRecovered |= FrameRecoveredSei;
        }

        if (currentIsReference && currentPictureIsIntra)
        {
            int longRefCount = 0;
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm)
                    longRefCount++;
            }
            int shortRefCount = dpbCount - longRefCount;

            (int ppsRef0, int ppsRef1) = context.MaxPpsRefCounts();
            bool hasBFrames = Math.Max(dynamicReorderDepth, maxNumReorderFrames) > 0;
            int fieldBonus = currentPictureStructure != ReferenceFrame.PictFrame ? 1 : 0;

            if (longRefCount == 0
                && (shortRefCount <= 2
                    || (ppsRef0 <= 2 && ppsRef1 <= 1 && hasBFrames)
                    || (ppsRef0 <= 1 + fieldBonus && ppsRef1 <= 1))
                && ppsRef0 <= 2 + fieldBonus + 2 * (hasRecoveryPoint ? 0 : 1))
            {
                currentPictureRecovered |= FrameRecoveredHeuristic;
                if (!hasBFrames)
                    frameRecovered |= FrameRecoveredHeuristic;
            }
        }

        currentPictureRecovered |= frameRecovered;
    }

    /// <summary>
    /// <c>h->short_ref[0]</c> - the most recently stored short-term reference, which is the
    /// one the C copies into a gap picture. The DPB here is not kept in short_ref's order, so
    /// it is the largest FrameNum rather than a fixed slot.
    /// </summary>
    private int MostRecentShortTermIndex()
    {
        int best = -1, bestFrameNum = int.MinValue;
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].IsLongTerm || dpb[i].Buffer == null)
                continue;
            if (dpb[i].FrameNum > bestFrameNum)
            {
                bestFrameNum = dpb[i].FrameNum;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// <c>color_frame</c> with the C's own <c>c[4]</c>: mid-grey luma and neutral chroma, at
    /// this stream's bit depth. The C fills only the visible rectangle and lets edge
    /// emulation clamp past it; filling the whole plane reaches the same samples through a
    /// decoder that clamps rather than emulating.
    /// </summary>
    private void PaintGray(byte[] buffer)
    {
        int lumaSize = context.CodedWidth * context.CodedHeight;
        int chromaSize = isChroma422 ? lumaSize : lumaSize / 2;

        if (pixelShift == 0)
        {
            int neutralLuma = 1 << (bitDepth - 1);
            buffer.AsSpan(0, Math.Min(lumaSize, buffer.Length)).Fill((byte)neutralLuma);
            if (buffer.Length > lumaSize)
                buffer.AsSpan(lumaSize, Math.Min(chromaSize, buffer.Length - lumaSize))
                      .Fill((byte)neutralChroma);
            return;
        }

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
            buffer.AsSpan(0, (lumaSize + chromaSize) * 2));
        samples[..lumaSize].Fill((ushort)(1 << (bitDepth - 1)));
        samples.Slice(lumaSize, chromaSize).Fill((ushort)neutralChroma);
    }

    /// <summary>
    /// Flushes all reference frames from the DPB, returning pooled buffers.
    /// Called at IDR boundaries per H.264 spec 8.2.5.1.
    /// </summary>
    private void FlushDpb()
    {
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].Buffer != null)
                ReturnBuffer(dpb[i].Buffer!);
            ReleaseColocated(ref dpb[i]);
            dpb[i] = default;
        }
        dpbCount = 0;
    }

    /// <summary>
    /// Searches the DPB for the first field of a complementary field pair (same frame_num).
    /// If found, sets firstFieldDpbIndex and reuses its buffer for the second field.
    /// H.264 spec 7.4.3: complementary field pairs share the same frame_num.
    /// </summary>
    private void FindComplementaryFieldInDpb(int frameNum)
    {
        firstFieldDpbIndex = -1;
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].FieldPicture && dpb[i].FrameNum == frameNum 
                && dpb[i].Reference != ReferenceFrame.PictFrame)
            {
                // Found the first field — verify it's the opposite parity
                bool firstIsTop = (dpb[i].Reference & ReferenceFrame.PictTopField) != 0;
                bool currentIsTop = currentPictureStructure == ReferenceFrame.PictTopField;
                if (firstIsTop != currentIsTop)
                {
                    firstFieldDpbIndex = i;
                    // Reuse the first field's buffer for decoding the second field. The DPB
                    // still owns it, so release ours before pointing at it and do not return it.
                    ReleaseCurrentFrameBuffer();
                    currentFrameBuffer = dpb[i].Buffer;
                    ownsCurrentFrameBuffer = false;
                    DiagnosticOutput?.WriteLine($"FIELD-PAIR: found first field at dpb[{i}] fn={frameNum} firstRef={dpb[i].Reference} currentStruct={currentPictureStructure}");
                    return;
                }
            }
        }
        DiagnosticOutput?.WriteLine($"FIELD-PAIR: no complementary field found for fn={frameNum} struct={currentPictureStructure}");
    }

    private void StoreReferenceFrame()
    {
        if (currentFrameBuffer == null)
            return;

        // H.264 §8.2.5: Reference picture marking (MMCO, sliding window, dedup) is
        // NOT invoked for the second field of a complementary reference field pair.
        // The second field merges into the existing DPB entry without triggering any
        // marking operations. Without this guard, the sliding window can evict the
        // first field's DPB entry before the merge, causing buffer corruption.
        bool isSecondFieldOfPair = currentFieldPicture && firstFieldDpbIndex >= 0;

        if (DiagnosticOutput != null)
            DiagnosticOutput.WriteLine($"STORE-REF: poc={currentPoc} fn={currentFrameNum} dpbBefore={dpbCount} maxRef={context.MaxRefFrames} secondField={isSecondFieldOfPair}");

        if (!isSecondFieldOfPair)
        {
            if (currentSliceHeader != null && currentSliceHeader.AdaptiveRefPicMarkingMode)
            {
                // Adaptive reference picture marking (spec 8.2.5.4)
                ApplyAdaptiveRefPicMarking(currentSliceHeader);
                if (DiagnosticOutput != null)
                    DiagnosticOutput.WriteLine($"STORE-AFTER-MMCO: dpb={dpbCount} cmds={currentSliceHeader.MmcoCommandCount}");
            }
            
            // H.264 §8.2.5.1 / §8.2.5.4.5: IDR long_term_reference_flag
            // When set, mark the current IDR picture as long-term with LongTermFrameIdx=0
            // and set MaxLongTermFrameIdx=0 (all other LT indices are invalid).
            if (currentIsIdr && currentSliceHeader is { LongTermReference: true })
            {
                currentFrameIsLongTerm = true;
                currentFrameLongTermIdx = 0;
                if (DiagnosticOutput != null)
                    DiagnosticOutput.WriteLine($"IDR-LT: marking IDR poc={currentPoc} as long-term idx=0");
            }
            
            // H.264 §8.2.5.1: If an existing short-term reference picture has the same
            // frame_num as the current picture, mark it as "unused for reference" before
            // adding the current picture. For field pictures, don't remove the complementary
            // first field — that's a valid field pair sharing the same frame_num.
            if (!currentFieldPicture || firstFieldDpbIndex < 0)
            {
                for (int i = dpbCount - 1; i >= 0; i--)
                {
                    if (!dpb[i].IsLongTerm && dpb[i].FrameNum == currentFrameNum)
                    {
                        // Skip removing if this is the first field of our complementary pair
                        if (currentFieldPicture && i == firstFieldDpbIndex)
                            continue;
                        DiagnosticOutput?.WriteLine($"DPB-DEDUP: removing existing ref fn={dpb[i].FrameNum} poc={dpb[i].Poc} (same frame_num as current)");
                        RemoveDpbEntry(i);
                    }
                }
            }
            
            // Ensure DPB doesn't exceed capacity (sliding window eviction).
            // Sliding window ONLY removes short-term references (H.264 §8.2.5.3).
            // Long-term references are protected from sliding window eviction.
            int maxFrames = Math.Min(MaxDpbFrames, context.MaxRefFrames);
            while (dpbCount >= maxFrames)
        {
            // Find oldest short-term ref (smallest FrameNumWrap)
            int maxFrameNum = context.ActiveSps?.MaxFrameNumber ?? 256;
            int minIdx = -1;
            int minPicNum = int.MaxValue;
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm) continue;
                int picNum = dpb[i].FrameNum > currentFrameNum
                    ? dpb[i].FrameNum - maxFrameNum : dpb[i].FrameNum;
                if (picNum < minPicNum)
                {
                    minPicNum = picNum;
                    minIdx = i;
                }
            }
            
            if (minIdx < 0)
                break; // Only long-term refs remain, can't evict
            
            if (DiagnosticOutput != null)
                DiagnosticOutput.WriteLine($"SLIDING-WINDOW: evicting dpb[{minIdx}] fn={dpb[minIdx].FrameNum} poc={dpb[minIdx].Poc} lt={dpb[minIdx].IsLongTerm} (dpb={dpbCount} max={maxFrames})");
            RemoveDpbEntry(minIdx);
            }
        } // end if (!isSecondFieldOfPair)

        // Store per-4x4-block co-located motion data for B_Direct temporal lookups
        int totalMbs = context.TotalMacroblocks;
        short[]? colMvX = null;
        short[]? colMvY = null;
        sbyte[]? colRef = null;
        short[]? colMvL1X = null;
        short[]? colMvL1Y = null;
        sbyte[]? colRefL1 = null;
        int[]? colL0Pocs = null;
        int colL0Count = 0;
        bool[]? colL0IsTop = null;
        if (deblockMvL0 != null && totalMbs > 0)
        {
            int totalBlocks = totalMbs * 16;
            // Rented, not allocated: at 720p these six are 576 KB per frame, and every one of
            // them is fully overwritten by the copy that follows. Nothing indexes them by
            // Length - the bound is the macroblock count - so a larger array from the pool is
            // safe. They are released when the DPB entry that owns them is removed.
            colMvX = FrameBufferPool<short>.Rent(totalBlocks);
            colMvY = FrameBufferPool<short>.Rent(totalBlocks);
            colRef = FrameBufferPool<sbyte>.Rent(totalBlocks);
            Deinterleave(deblockMvL0!, colMvX, colMvY, totalBlocks);
            Array.Copy(deblockRefL0, 0, colRef, 0, totalBlocks);
            
            // Store L1 data for spatial direct colZeroFlag fallback
            if (deblockMvL1 != null)
            {
                colMvL1X = FrameBufferPool<short>.Rent(totalBlocks);
                colMvL1Y = FrameBufferPool<short>.Rent(totalBlocks);
                colRefL1 = FrameBufferPool<sbyte>.Rent(totalBlocks);
                Deinterleave(deblockMvL1!, colMvL1X, colMvL1Y, totalBlocks);
                Array.Copy(deblockRefL1, 0, colRefL1, 0, totalBlocks);
            }
            
            // Save this frame's L0 reference POCs for temporal direct ref mapping
            colL0Count = refPicListL0Count;
            if (colL0Count > 0)
            {
                // Pooled, not allocated. These are attached to the DPB entry and read back off
                // the collocated picture, so they outlive the frame and cannot be reused in place;
                // ReleaseColocated returns them when the entry goes. Every reader bounds itself by
                // the matching Count field, so a pooled array longer than the count is fine.
                // One length, not this frame's count. The pool keys by exact length, so renting the
                // count made a separate class per distinct value and missed whenever a new one came
                // up. Readers bound themselves by the Count field, so a longer buffer is fine.
                colL0Pocs = FrameBufferPool<int>.Rent(refPicListL0Poc.Length);
                Array.Copy(refPicListL0Poc, 0, colL0Pocs, 0, colL0Count);
                // Only field pictures keep these: the entry stores them under TopField/BotField,
                // and both of those are null for a progressive frame. Renting regardless meant
                // every progressive frame took a buffer, filled it, and dropped it without ever
                // storing it, so it was never returned either - 682 rents and 0 returns over a
                // 600-frame run, every one of them an allocation.
                if (currentFieldPicture)
                {
                    colL0IsTop = FrameBufferPool<bool>.Rent(colL0Count);
                    Array.Copy(refPicListL0IsTopField, 0, colL0IsTop, 0, colL0Count);
                }
            }
        }
        
        // Save L1 reference POCs for temporal direct L1 fallback
        int[]? colL1Pocs = null;
        int colL1Count = refPicListL1Count;
        bool[]? colL1IsTop = null;
        if (colL1Count > 0)
        {
            colL1Pocs = FrameBufferPool<int>.Rent(refPicListL1Poc.Length);
            Array.Copy(refPicListL1Poc, 0, colL1Pocs, 0, colL1Count);
            if (currentFieldPicture)
            {
                colL1IsTop = FrameBufferPool<bool>.Rent(colL1Count);
                Array.Copy(refPicListL1IsTopField, 0, colL1IsTop, 0, colL1Count);
            }
        }

        // For the second field of a complementary pair, update the existing DPB entry
        // instead of creating a new one. The buffer is shared; merge the field POCs and
        // update the reference bitmask to indicate both fields are now available.
        if (currentFieldPicture && firstFieldDpbIndex >= 0 && firstFieldDpbIndex < dpbCount)
        {
            ref var existing = ref dpb[firstFieldDpbIndex];
            if (currentPictureStructure == ReferenceFrame.PictTopField)
            {
                existing.TopFieldPoc = currentTopFieldPoc;
                currentBottomFieldPoc = existing.BottomFieldPoc;
            }
            else
            {
                existing.BottomFieldPoc = currentBottomFieldPoc;
                currentTopFieldPoc = existing.TopFieldPoc;
            }
            existing.Reference |= currentPictureStructure;
            existing.Poc = Math.Min(existing.TopFieldPoc, existing.BottomFieldPoc);
            // RefPoc = second (current) decoded field's POC, matching FFmpeg convention
            // where H264Picture.poc is overwritten by each field decode, ending at last field's POC.
            existing.RefPoc = currentPoc;
            
            // Update currentPoc to combined POC for correct reorder buffer output
            currentPoc = existing.Poc;
            
            // Store second field's collocated data into the correct per-field slot
            if (currentPictureStructure == ReferenceFrame.PictTopField)
            {
                existing.TopFieldColocMvX = colMvX;
                existing.TopFieldColocMvY = colMvY;
                existing.TopFieldColocRefIdx = colRef;
                existing.TopFieldColocMvL1X = colMvL1X;
                existing.TopFieldColocMvL1Y = colMvL1Y;
                existing.TopFieldColocRefIdxL1 = colRefL1;
                existing.TopFieldColocL0Pocs = colL0Pocs;
                existing.TopFieldColocL0Count = colL0Count;
                existing.TopFieldColocL1Pocs = colL1Pocs;
                existing.TopFieldColocL1Count = colL1Count;
                existing.TopFieldColocL0IsTop = colL0IsTop;
                existing.TopFieldColocL1IsTop = colL1IsTop;
            }
            else
            {
                existing.BotFieldColocMvX = colMvX;
                existing.BotFieldColocMvY = colMvY;
                existing.BotFieldColocRefIdx = colRef;
                existing.BotFieldColocMvL1X = colMvL1X;
                existing.BotFieldColocMvL1Y = colMvL1Y;
                existing.BotFieldColocRefIdxL1 = colRefL1;
                existing.BotFieldColocL0Pocs = colL0Pocs;
                existing.BotFieldColocL0Count = colL0Count;
                existing.BotFieldColocL1Pocs = colL1Pocs;
                existing.BotFieldColocL1Count = colL1Count;
                existing.BotFieldColocL0IsTop = colL0IsTop;
                existing.BotFieldColocL1IsTop = colL1IsTop;
            }
            // Keep generic Colocated* pointing to latest field for backward compatibility
            existing.ColocatedMvX = colMvX;
            existing.ColocatedMvY = colMvY;
            existing.ColocatedRefIdx = colRef;
            existing.ColocatedMvL1X = colMvL1X;
            existing.ColocatedMvL1Y = colMvL1Y;
            existing.ColocatedRefIdxL1 = colRefL1;
            existing.ColocatedL0Pocs = colL0Pocs;
            existing.ColocatedL0Count = colL0Count;
            existing.ColocatedL1Pocs = colL1Pocs;
            existing.ColocatedL1Count = colL1Count;
            DiagnosticOutput?.WriteLine($"STORE-REF-FIELD2: updated dpb[{firstFieldDpbIndex}] fn={currentFrameNum} ref={existing.Reference} topPoc={existing.TopFieldPoc} botPoc={existing.BottomFieldPoc} fieldStruct={currentPictureStructure}");
            firstFieldDpbIndex = -1;
            currentFrameIsLongTerm = false;
            return;
        }
        
        // Allocate new buffer and copy for first field or frame picture.
        // (Second field returned above — it decoded directly into the DPB buffer.)
        int frameSize = currentFrameBuffer.Length;
        byte[] refBuffer = RentBuffer(frameSize);

        // No clear: the pool hands back a buffer of exactly frameSize and the copy below
        // overwrites every byte of it. Clearing first wrote three megabytes per reference frame
        // that the next statement immediately replaced.
        currentFrameBuffer.AsSpan(0, frameSize).CopyTo(refBuffer);
        
        dpb[dpbCount++] = new ReferenceFrame
        {
            Buffer = refBuffer,
            Gray = currentPictureGray,
            Poc = currentPoc,
            RefPoc = currentPoc,
            TopFieldPoc = currentTopFieldPoc,
            BottomFieldPoc = currentBottomFieldPoc,
            FrameNum = currentFrameNum,
            PresentationTimeTicks = currentPts,
            IsLongTerm = currentFrameIsLongTerm,
            LongTermFrameIdx = currentFrameIsLongTerm ? currentFrameLongTermIdx : 0,
            PictureStructure = currentPictureStructure,
            Reference = currentFieldPicture ? currentPictureStructure : ReferenceFrame.PictFrame,
            FieldPicture = currentFieldPicture,
            MbFieldFlags = isMbaffFrame && mbFieldDecodingFlag != null
                ? (bool[])mbFieldDecodingFlag.Clone() : null,
            ColocatedMvX = colMvX,
            ColocatedMvY = colMvY,
            ColocatedRefIdx = colRef,
            ColocatedMvL1X = colMvL1X,
            ColocatedMvL1Y = colMvL1Y,
            ColocatedRefIdxL1 = colRefL1,
            ColocatedL0Pocs = colL0Pocs,
            ColocatedL0Count = colL0Count,
            ColocatedL1Pocs = colL1Pocs,
            ColocatedL1Count = colL1Count,
            // For first field of a field pair, populate the correct per-field slot
            TopFieldColocMvX = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colMvX : null,
            TopFieldColocMvY = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colMvY : null,
            TopFieldColocRefIdx = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colRef : null,
            TopFieldColocMvL1X = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colMvL1X : null,
            TopFieldColocMvL1Y = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colMvL1Y : null,
            TopFieldColocRefIdxL1 = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colRefL1 : null,
            TopFieldColocL0Pocs = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL0Pocs : null,
            TopFieldColocL0Count = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL0Count : 0,
            TopFieldColocL1Pocs = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL1Pocs : null,
            TopFieldColocL1Count = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL1Count : 0,
            TopFieldColocL0IsTop = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL0IsTop : null,
            TopFieldColocL1IsTop = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictTopField) ? colL1IsTop : null,
            BotFieldColocMvX = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colMvX : null,
            BotFieldColocMvY = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colMvY : null,
            BotFieldColocRefIdx = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colRef : null,
            BotFieldColocMvL1X = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colMvL1X : null,
            BotFieldColocMvL1Y = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colMvL1Y : null,
            BotFieldColocRefIdxL1 = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colRefL1 : null,
            BotFieldColocL0Pocs = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL0Pocs : null,
            BotFieldColocL0Count = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL0Count : 0,
            BotFieldColocL1Pocs = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL1Pocs : null,
            BotFieldColocL1Count = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL1Count : 0,
            BotFieldColocL0IsTop = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL0IsTop : null,
            BotFieldColocL1IsTop = (currentFieldPicture && currentPictureStructure == ReferenceFrame.PictBottomField) ? colL1IsTop : null,
        };
        firstFieldDpbIndex = -1;
        currentFrameIsLongTerm = false;
    }

    /// <summary>
    /// Apply adaptive reference picture marking (H.264 spec 8.2.5.4).
    /// Processes MMCO commands to explicitly manage DPB entries.
    /// </summary>
    private void ApplyAdaptiveRefPicMarking(SliceHeader sliceHeader)
    {
        int maxFrameNum = context.ActiveSps?.MaxFrameNumber ?? 256;
        currentFrameIsLongTerm = false;

        // For field pictures, picNum = 2*FrameNum (spec 8.2.4.1)
        // currPicNum = 2*frame_num + 1 for the current field (always odd)
        // maxPicNum = 2*maxFrameNum
        // For frame pictures, picNum = FrameNum, maxPicNum = maxFrameNum
        bool isField = currentFieldPicture;
        int currPicNum = isField ? 2 * (int)currentFrameNum + 1 : (int)currentFrameNum;
        int maxPicNum = isField ? 2 * maxFrameNum : maxFrameNum;

        for (int c = 0; c < sliceHeader.MmcoCommandCount; c++)
        {
            ref var cmd = ref sliceHeader.MmcoCommands[c];
            switch (cmd.Operation)
            {
                case 1:
                {
                    // Mark short-term reference as "unused for reference"
                    int shortPicNum = (currPicNum - (int)cmd.DifferenceOfPicNumsMinus1 - 1) & (maxPicNum - 1);
                    int frameNumX = isField ? shortPicNum >> 1 : shortPicNum;

                    if (DiagnosticOutput != null)
                        DiagnosticOutput.WriteLine($"MMCO-1: fn={currentFrameNum} diff={cmd.DifferenceOfPicNumsMinus1} → remove frameNum={frameNumX} (picNum={shortPicNum}, field={isField})");

                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (!dpb[i].IsLongTerm && dpb[i].FrameNum == frameNumX)
                        {
                            RemoveDpbEntry(i);
                            break;
                        }
                    }
                    break;
                }
                case 2:
                {
                    // Mark long-term reference as "unused for reference"
                    int longTermPicNum = (int)cmd.LongTermPicNum;
                    // For field pictures, LongTermPicNum = 2*LongTermFrameIdx + field_parity
                    // Extract the LongTermFrameIdx
                    int longTermFrameIdx = isField ? longTermPicNum >> 1 : longTermPicNum;
                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (dpb[i].IsLongTerm && dpb[i].LongTermFrameIdx == longTermFrameIdx)
                        {
                            RemoveDpbEntry(i);
                            break;
                        }
                    }
                    break;
                }
                case 3:
                {
                    // Assign long_term_frame_idx to a short-term reference picture
                    int shortPicNum = (currPicNum - (int)cmd.DifferenceOfPicNumsMinus1 - 1) & (maxPicNum - 1);
                    int frameNumX = isField ? shortPicNum >> 1 : shortPicNum;
                    int longTermIdx = (int)cmd.LongTermFrameIdx;

                    if (DiagnosticOutput != null)
                        DiagnosticOutput.WriteLine($"MMCO-3: fn={currentFrameNum} diff={cmd.DifferenceOfPicNumsMinus1} → promote frameNum={frameNumX} (picNum={shortPicNum}) to LT idx={longTermIdx} (field={isField})");

                    // First evict any existing long-term ref at this index
                    // (but not if it's the same picture we're about to promote)
                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (dpb[i].IsLongTerm && dpb[i].LongTermFrameIdx == longTermIdx)
                        {
                            if (dpb[i].FrameNum != frameNumX)
                                RemoveDpbEntry(i);
                            break;
                        }
                    }

                    // Convert the short-term ref to long-term
                    bool found = false;
                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (!dpb[i].IsLongTerm && dpb[i].FrameNum == frameNumX)
                        {
                            dpb[i].IsLongTerm = true;
                            dpb[i].LongTermFrameIdx = longTermIdx;
                            found = true;
                            break;
                        }
                    }
                    // For field pictures, the second MMCO-3 targeting the same frame
                    // will find it already promoted to LT — this is expected (silently skip)
                    if (!found && isField)
                    {
                        for (int i = 0; i < dpbCount; i++)
                        {
                            if (dpb[i].IsLongTerm && dpb[i].LongTermFrameIdx == longTermIdx &&
                                dpb[i].FrameNum == frameNumX)
                            {
                                // Already promoted by the first field's MMCO-3 — expected
                                break;
                            }
                        }
                    }
                    break;
                }
                case 4:
                {
                    // Set max long-term frame index; evict all with idx > max
                    int maxLongTermIdx = (int)cmd.MaxLongTermFrameIdxPlus1 - 1;
                    if (DiagnosticOutput != null)
                        DiagnosticOutput.WriteLine($"MMCO-4: maxLTidx={maxLongTermIdx} (plus1={cmd.MaxLongTermFrameIdxPlus1})");
                    for (int i = dpbCount - 1; i >= 0; i--)
                    {
                        if (dpb[i].IsLongTerm && dpb[i].LongTermFrameIdx > maxLongTermIdx)
                            RemoveDpbEntry(i);
                    }
                    break;
                }
                case 5:
                {
                    // Mark all references as "unused for reference" (DPB flush)
                    // H.264 §8.2.5.4.5: After this operation, the current picture's
                    // frame_num is inferred to be 0 for all subsequent decoding,
                    // and the current picture's POC is set to 0.
                    if (DiagnosticOutput != null)
                        DiagnosticOutput.WriteLine($"MMCO-5: flushing DPB ({dpbCount} entries), resetting prevFrameNum and POC to 0");
                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (dpb[i].Buffer != null)
                            ReturnBuffer(dpb[i].Buffer!);
                        ReleaseColocated(ref dpb[i]);
                        dpb[i] = default;
                    }
                    dpbCount = 0;
                    
                    // The current picture's frame_num is treated as 0 for subsequent use
                    prevFrameNum = 0;
                    prevFrameNumOffset = 0;
                    currentFrameNum = 0;
                    
                    // H.264 §8.2.1: After MMCO-5, the current picture's POC = 0.
                    // For POC type 0: prevPicOrderCntMsb=0, prevPicOrderCntLsb=TopFieldOrderCnt (=0)
                    // For POC type 1/2: prevFrameNumOffset already reset above
                    currentPoc = 0;
                    prevPocMsb = 0;
                    prevPocLsb = 0;
                    
                    currentMmcoReset = true;
                    break;
                }
                case 6:
                {
                    // Assign long_term_frame_idx to the current picture
                    int longTermIdx = (int)cmd.LongTermFrameIdx;

                    if (DiagnosticOutput != null)
                        DiagnosticOutput.WriteLine($"MMCO-6: fn={currentFrameNum} marking current as LT idx={longTermIdx}");

                    // Evict any existing long-term ref at this index
                    for (int i = 0; i < dpbCount; i++)
                    {
                        if (dpb[i].IsLongTerm && dpb[i].LongTermFrameIdx == longTermIdx)
                        {
                            RemoveDpbEntry(i);
                            break;
                        }
                    }

                    // Defer: current frame will be marked long-term when added to DPB
                    currentFrameIsLongTerm = true;
                    currentFrameLongTermIdx = longTermIdx;
                    break;
                }
            }
        }
    }

    /// <summary>Remove a DPB entry by index, returning its buffer to the pool.</summary>
    private void RemoveDpbEntry(int index)
    {
        if (dpb[index].Buffer != null)
            ReturnBuffer(dpb[index].Buffer!);
        ReleaseColocated(ref dpb[index]);
        for (int j = index; j < dpbCount - 1; j++)
            dpb[j] = dpb[j + 1];
        dpb[dpbCount - 1] = default;
        dpbCount--;
    }

    // Scratch for the release below. The counts cover the generic set plus both field sets.
    private readonly short[]?[] releasedShortArrays = new short[]?[12];
    private readonly int[]?[] releasedIntArrays = new int[]?[6];
    private readonly bool[]?[] releasedBoolArrays = new bool[]?[4];
    private readonly sbyte[]?[] releasedSByteArrays = new sbyte[]?[6];

    /// <summary>
    /// Returns a frame's pooled colocated motion arrays, each exactly once.
    /// </summary>
    /// <remarks>
    /// The generic Colocated* fields are kept pointing at the most recently decoded field of a
    /// pair, so for a field picture they alias the Top or Bot arrays rather than owning separate
    /// ones. Returning both sets blindly would hand the same array to the pool twice, which is a
    /// far worse bug than the leak it replaces: two renters would then be handed one array.
    /// </remarks>
    private void ReleaseColocated(ref ReferenceFrame frame)
    {
        int shortCount = 0, sbyteCount = 0, intCount = 0, boolCount = 0;

        ReleaseShort(frame.ColocatedMvX, ref shortCount);
        ReleaseShort(frame.ColocatedMvY, ref shortCount);
        ReleaseShort(frame.ColocatedMvL1X, ref shortCount);
        ReleaseShort(frame.ColocatedMvL1Y, ref shortCount);
        ReleaseShort(frame.TopFieldColocMvX, ref shortCount);
        ReleaseShort(frame.TopFieldColocMvY, ref shortCount);
        ReleaseShort(frame.TopFieldColocMvL1X, ref shortCount);
        ReleaseShort(frame.TopFieldColocMvL1Y, ref shortCount);
        ReleaseShort(frame.BotFieldColocMvX, ref shortCount);
        ReleaseShort(frame.BotFieldColocMvY, ref shortCount);
        ReleaseShort(frame.BotFieldColocMvL1X, ref shortCount);
        ReleaseShort(frame.BotFieldColocMvL1Y, ref shortCount);

        ReleaseSByte(frame.ColocatedRefIdx, ref sbyteCount);
        ReleaseSByte(frame.ColocatedRefIdxL1, ref sbyteCount);
        ReleaseSByte(frame.TopFieldColocRefIdx, ref sbyteCount);
        ReleaseSByte(frame.TopFieldColocRefIdxL1, ref sbyteCount);
        ReleaseSByte(frame.BotFieldColocRefIdx, ref sbyteCount);
        ReleaseSByte(frame.BotFieldColocRefIdxL1, ref sbyteCount);

        // A single rented array can be referenced by both a frame-level and a field-level field,
        // so these dedupe by reference the same way the motion vectors above do.
        ReleaseInt(frame.ColocatedL0Pocs, ref intCount);
        ReleaseInt(frame.ColocatedL1Pocs, ref intCount);
        ReleaseInt(frame.TopFieldColocL0Pocs, ref intCount);
        ReleaseInt(frame.TopFieldColocL1Pocs, ref intCount);
        ReleaseInt(frame.BotFieldColocL0Pocs, ref intCount);
        ReleaseInt(frame.BotFieldColocL1Pocs, ref intCount);

        ReleaseBool(frame.TopFieldColocL0IsTop, ref boolCount);
        ReleaseBool(frame.TopFieldColocL1IsTop, ref boolCount);
        ReleaseBool(frame.BotFieldColocL0IsTop, ref boolCount);
        ReleaseBool(frame.BotFieldColocL1IsTop, ref boolCount);

        frame.ColocatedMvX = null;
        frame.ColocatedMvY = null;
        frame.ColocatedMvL1X = null;
        frame.ColocatedMvL1Y = null;
        frame.ColocatedRefIdx = null;
        frame.ColocatedRefIdxL1 = null;
        frame.TopFieldColocMvX = null;
        frame.TopFieldColocMvY = null;
        frame.TopFieldColocMvL1X = null;
        frame.TopFieldColocMvL1Y = null;
        frame.TopFieldColocRefIdx = null;
        frame.TopFieldColocRefIdxL1 = null;
        frame.BotFieldColocMvX = null;
        frame.BotFieldColocMvY = null;
        frame.BotFieldColocMvL1X = null;
        frame.BotFieldColocMvL1Y = null;
        frame.BotFieldColocRefIdx = null;
        frame.BotFieldColocRefIdxL1 = null;

        frame.ColocatedL0Pocs = null;
        frame.ColocatedL1Pocs = null;
        frame.TopFieldColocL0Pocs = null;
        frame.TopFieldColocL1Pocs = null;
        frame.BotFieldColocL0Pocs = null;
        frame.BotFieldColocL1Pocs = null;
        frame.TopFieldColocL0IsTop = null;
        frame.TopFieldColocL1IsTop = null;
        frame.BotFieldColocL0IsTop = null;
        frame.BotFieldColocL1IsTop = null;
    }

    private void ReleaseShort(short[]? array, ref int count)
    {
        if (array is null)
            return;

        for (int i = 0; i < count; i++)
            if (ReferenceEquals(releasedShortArrays[i], array))
                return;

        releasedShortArrays[count++] = array;
        FrameBufferPool<short>.Return(array);
    }

    private void ReleaseInt(int[]? array, ref int count)
    {
        if (array is null)
            return;

        for (int i = 0; i < count; i++)
            if (ReferenceEquals(releasedIntArrays[i], array))
                return;

        releasedIntArrays[count++] = array;
        FrameBufferPool<int>.Return(array);
    }

    private void ReleaseBool(bool[]? array, ref int count)
    {
        if (array is null)
            return;

        for (int i = 0; i < count; i++)
            if (ReferenceEquals(releasedBoolArrays[i], array))
                return;

        releasedBoolArrays[count++] = array;
        FrameBufferPool<bool>.Return(array);
    }

    private void ReleaseSByte(sbyte[]? array, ref int count)
    {
        if (array is null)
            return;

        for (int i = 0; i < count; i++)
            if (ReferenceEquals(releasedSByteArrays[i], array))
                return;

        releasedSByteArrays[count++] = array;
        FrameBufferPool<sbyte>.Return(array);
    }

    private DecodedVideoFrame? CreateOutputFrame(long pts)
    {

        if (currentFrameBuffer == null)
            return null;

        int displayWidth = context.Width;
        int displayHeight = context.Height;
        int codedWidth = context.CodedWidth;
        int codedHeight = context.CodedHeight;
        int bps = 1 + pixelShift; // bytes per sample
        
        int yPixels = codedWidth * codedHeight;
        int uvPixels = isChroma422 ? yPixels / 2 : yPixels / 4;

        // Transfer ownership of buffer to frame
        var buffer = currentFrameBuffer;
        LastOutputPoc = currentPoc;
        
        // The old buffer's ownership has just passed to the output frame, so it is not released
        // here - the frame returns it when it is disposed.
        currentFrameBuffer = RentBuffer(buffer.Length);
        ownsCurrentFrameBuffer = true;
        Array.Clear(currentFrameBuffer);
        // Initialize chroma to neutral for new frame
        if (pixelShift == 0)
        {
            currentFrameBuffer.AsSpan(yPixels, uvPixels * 2).Fill(128);
        }
        else
        {
            var chromaSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(
                currentFrameBuffer.AsSpan(yPixels * bps, uvPixels * 2 * bps));
            chromaSpan.Fill((ushort)neutralChroma);
        }

        int cropLeft = GetCropLeftLumaPixels();
        int cropTop = GetCropTopLumaPixels();
        // Crop offsets in pixels, then convert to byte offsets for DecodedVideoFrame
        int yCropPixelOffset = cropTop * codedWidth + cropLeft;
        // Chroma crop: horizontal always /2, vertical /subHeightC (1 for 422, 2 for 420)
        int uvCropPixelOffset = (cropTop / subHeightC) * (codedWidth / 2) + (cropLeft / 2);
        var format = OutputFormat;

        return new DecodedVideoFrame(
            displayWidth, displayHeight,
            format,
            pts,
            buffer,
            yOffset: yCropPixelOffset * bps, yStride: codedWidth * bps,
            uOffset: yPixels * bps + uvCropPixelOffset * bps, uStride: (codedWidth / 2) * bps,
            vOffset: (yPixels + uvPixels) * bps + uvCropPixelOffset * bps, vStride: (codedWidth / 2) * bps,
            bufferPool: BypassArrayPool ? null : FrameBufferPool<byte>.Instance);
    }

    /// <summary>
    /// <c>init_dimensions</c>: the display size, which is the SPS's cropped size EXCEPT when
    /// the container's is a legitimate tighter crop of it.
    /// </summary>
    /// <remarks>
    /// The C calls this arm "handle container cropping", and the conditions are all five of
    /// its own: the container said something, the SPS crops nothing off the top or left, both
    /// sizes round up to the same multiple of 16, and the container's is no larger. That last
    /// pair is what makes it safe - the container is describing the same coded picture with
    /// the padding rows and columns taken off, not a different one.
    ///
    /// Without it a 1920x1088 sequence whose container says 1920x1080 is handed out eight rows
    /// too tall, which is not a crash, not a refusal, and not what ffmpeg does.
    /// </remarks>
    private (int Width, int Height) EffectiveDisplaySize()
    {
        var sps = context.ActiveSps;
        if (sps == null)
            return (0, 0);

        int width = sps.DisplayWidth;
        int height = sps.DisplayHeight;

        if (containerDimsRejected || ContainerWidth <= 0 || ContainerHeight <= 0)
            return (width, height);

        if (sps.FrameCropTopOffset == 0 && sps.FrameCropLeftOffset == 0
            && FfAlign16(ContainerWidth) == FfAlign16(width)
            && FfAlign16(ContainerHeight) == FfAlign16(height)
            && ContainerWidth <= width && ContainerHeight <= height)
        {
            return (ContainerWidth, ContainerHeight);
        }

        // The C zeroes width_from_caller here, so it never reconsiders.
        containerDimsRejected = true;
        return (width, height);
    }

    /// <summary><c>FFALIGN(x, 16)</c>.</summary>
    private static int FfAlign16(int value) => (value + 15) & ~15;

    /// <summary>
    /// Returns the left crop offset in luma pixels (accounting for CropUnitX).
    /// </summary>
    private int GetCropLeftLumaPixels()
    {
        var sps = context.ActiveSps;
        if (sps == null || !sps.FrameCroppingFlag) return 0;
        int chromaArrayType = sps.SeparateColorPlane ? 0 : (int)sps.ChromaFormat;
        int subWidthC = (chromaArrayType == 1 || chromaArrayType == 2) ? 2 : 1;
        int cropUnitX = chromaArrayType == 0 ? 1 : subWidthC;
        return sps.FrameCropLeftOffset * cropUnitX;
    }

    /// <summary>
    /// Returns the top crop offset in luma pixels (accounting for CropUnitY).
    /// </summary>
    private int GetCropTopLumaPixels()
    {
        var sps = context.ActiveSps;
        if (sps == null || !sps.FrameCroppingFlag) return 0;
        int chromaArrayType = sps.SeparateColorPlane ? 0 : (int)sps.ChromaFormat;
        int subHeightC = (chromaArrayType == 1) ? 2 : 1;
        int cropUnitY = (sps.FrameMacroblockOnly ? 1 : 2) * (chromaArrayType == 0 ? 1 : subHeightC);
        return sps.FrameCropTopOffset * cropUnitY;
    }

    /// <summary>
    /// Adds the just-decoded frame to the reorder buffer (copies the current frame buffer).
    /// Also updates the dynamic reorder depth using FFmpeg's POC tracking algorithm
    /// (h264_select_output_frame in h264_slice.c).
    /// </summary>
    private void AddToReorderBuffer(long pts, bool isKeyFrame)
    {
        if (currentFrameBuffer == null || reorderBufferCount >= reorderBuffer.Length)
            return;

        // MMCO-5 resets POC to 0, creating a sequence boundary. Drain all buffered
        // frames before adding the MMCO-5 frame so that pre-reset and post-reset
        // POC values never coexist in the reorder buffer. Without this, post-reset
        // frames (low POC) would be output before pre-reset frames (high POC).
        if (currentMmcoReset && reorderBufferCount > 0)
        {
            DiagnosticOutput?.WriteLine($"REORDER-DRAIN: mmcoReset draining {reorderBufferCount} buffered frames before adding poc={currentPoc}");
            DrainReorderBuffer();
        }

        int codedWidth = context.CodedWidth;
        int codedHeight = context.CodedHeight;
        int yPixels = codedWidth * codedHeight;
        int uvPixels = isChroma422 ? yPixels / 2 : yPixels / 4;
        int bps = 1 + pixelShift;
        int frameSizeBytes = (yPixels + uvPixels * 2) * bps;

        // Copy the current frame buffer into a new buffer for the reorder queue
        byte[] buffer = RentBuffer(currentFrameBuffer.Length);
        Array.Clear(buffer); // Clear entire buffer before partial copy
        currentFrameBuffer.AsSpan(0, frameSizeBytes).CopyTo(buffer);
        
        // Field pair diagnostic: verify rows 0 and 1 at copy time
        if (DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc > 0
            && (currentPoc == DiagnosticDumpAllMbsForPoc || currentPoc == DiagnosticDumpAllMbsForPoc - 1 || currentPoc == DiagnosticDumpAllMbsForPoc + 1))
        {
            var sb = new System.Text.StringBuilder($"REORDER-COPY: poc={currentPoc} row0=[");
            for (int i = 0; i < 8; i++) sb.Append($"{currentFrameBuffer[i]},");
            sb.Append($"] row1=[");
            for (int i = 0; i < 8; i++) sb.Append($"{currentFrameBuffer[codedWidth + i]},");
            sb.Append(']');
            DiagnosticOutput.WriteLine(sb.ToString());
            // Check position 22896 (row 65, col 16 of 352-wide frame) for FREXT4 debugging
            if (22896 < currentFrameBuffer.Length)
                DiagnosticOutput.WriteLine($"  REORDER-VERIFY: poc={currentPoc} cfb[22896]={currentFrameBuffer[22896]} copy[22896]={buffer[22896]}");
        }
        



        reorderBuffer[reorderBufferCount++] = new PendingOutputFrame
        {
            Buffer = buffer,
            Poc = currentPoc,
            PresentationTimeTicks = pts,
            DisplayWidth = EffectiveDisplaySize().Width,
            DisplayHeight = EffectiveDisplaySize().Height,
            CodedWidth = codedWidth,
            CodedHeight = codedHeight,
            CropLeftLuma = GetCropLeftLumaPixels(),
            CropTopLuma = GetCropTopLumaPixels(),
            IsKeyFrame = isKeyFrame,
            MmcoReset = currentMmcoReset,
            Recovered = currentPictureRecovered,
        };

        // The picture is now the reorder buffer's; the next one starts from nothing.
        currentPictureRecovered = 0;

        DiagnosticOutput?.WriteLine($"REORDER-ADD: poc={currentPoc} bufCount={reorderBufferCount} mmcoReset={currentMmcoReset}");

        // Reset per-frame mmco flag after capturing it
        currentMmcoReset = false;

        UpdateDynamicReorderDepth(currentPoc);
    }

    /// <summary>
    /// FFmpeg-style dynamic reorder depth detection (h264_select_output_frame).
    /// Tracks recent POC values in a sorted array and increases the reorder depth
    /// when out-of-order POC is detected. Starts at 0 (no delay) and only increases.
    /// </summary>
    private void UpdateDynamicReorderDepth(int poc)
    {
        // Insertion sort of poc into lastPocs (sorted ascending, INT_MIN = empty)
        // FFmpeg: h264_slice.c lines 1309-1317
        int i;
        for (i = 0; ; i++)
        {
            if (i == MaxDpbFrames || poc < lastPocs[i])
            {
                if (i > 0) lastPocs[i - 1] = poc;
                break;
            }
            else if (i > 0)
            {
                lastPocs[i - 1] = lastPocs[i];
            }
        }
        int outOfOrder = MaxDpbFrames - i;

        // FFmpeg: if current is B-frame OR the last two POCs differ by > 2,
        // the reorder depth is at least 1 (h264_slice.c lines 1319-1321)
        if (lastPocs[MaxDpbFrames - 2] > int.MinValue
            && (long)lastPocs[MaxDpbFrames - 1] - lastPocs[MaxDpbFrames - 2] > 2)
        {
            outOfOrder = Math.Max(outOfOrder, 1);
        }

        // Handle invalid POC sequence — reset tracking (FFmpeg lines 1322-1327)
        if (outOfOrder == MaxDpbFrames)
        {
            for (int j = 1; j < MaxDpbFrames; j++)
                lastPocs[j] = int.MinValue;
            lastPocs[0] = poc;
        }
        else if (dynamicReorderDepth < outOfOrder)
        {
            // Only increase, never decrease (within a sequence)
            dynamicReorderDepth = outOfOrder;
        }
    }

    /// <summary>
    /// If the reorder buffer has more frames than the current dynamic reorder depth,
    /// output the frame with the lowest POC (earliest display order).
    /// Uses VUI max_num_reorder_frames as a minimum when present.
    /// </summary>
    private DecodedVideoFrame? TryOutputReorderedFrame()
    {
        // Use the larger of: dynamic depth, VUI max_num_reorder_frames, or ref-based estimate
        int effectiveDepth = Math.Max(dynamicReorderDepth, maxNumReorderFrames);

        if (reorderBufferCount <= effectiveDepth)
        {
            DiagnosticOutput?.WriteLine($"REORDER-WAIT: bufCount={reorderBufferCount} effDepth={effectiveDepth} dynDepth={dynamicReorderDepth} maxReorder={maxNumReorderFrames}");
            return null;
        }

        DiagnosticOutput?.WriteLine($"REORDER-OUTPUT: bufCount={reorderBufferCount} effDepth={effectiveDepth}");
        return OutputLowestPocFrame();
    }

    /// <summary>
    /// Removes and returns the frame with the lowest POC from the reorder buffer.
    /// Stops searching past key frames (IDR) to keep GOP boundaries intact,
    /// matching FFmpeg h264_slice.c lines 1346-1353.
    /// </summary>
    private DecodedVideoFrame? OutputLowestPocFrame()
    {
        if (reorderBufferCount == 0)
            return null;

        // Find frame with lowest POC, stopping at key frame and MMCO-5 boundaries.
        // MMCO-5 resets POC to 0, so post-reset frames have lower POC values than
        // pre-reset frames. Without this boundary check, post-reset frames would
        // be emitted before pre-reset frames, scrambling the output order.
        // Mirrors FFmpeg h264_slice.c lines 1346-1348.
        int minIdx = 0;
        int minPoc = reorderBuffer[0].Poc;
        for (int i = 1; i < reorderBufferCount; i++)
        {
            if (reorderBuffer[i].IsKeyFrame || reorderBuffer[i].MmcoReset)
                break;
            if (reorderBuffer[i].Poc < minPoc)
            {
                minPoc = reorderBuffer[i].Poc;
                minIdx = i;
            }
        }

        var frame = reorderBuffer[minIdx];

        // h264_select_output_frame: frame_recovered accumulates in DISPLAY order, and a
        // picture that no recovery condition has reached is dropped rather than returned.
        // Without this a stream joined mid-GOP hands out every picture it managed to
        // reconstruct out of references that were never sent.
        frameRecovered |= frame.Recovered;
        frame.Recovered |= frameRecovered & FrameRecoveredSei;
        if (frame.Recovered == 0)
        {
            DiagnosticOutput?.WriteLine($"REORDER-DROP: poc={frame.Poc} not recovered");
            ReturnBuffer(frame.Buffer!);
            for (int i = minIdx; i < reorderBufferCount - 1; i++)
                reorderBuffer[i] = reorderBuffer[i + 1];
            reorderBuffer[--reorderBufferCount] = default;
            return OutputLowestPocFrame();
        }

        DiagnosticOutput?.WriteLine($"REORDER-EMIT: poc={frame.Poc} pts={frame.PresentationTimeTicks} minIdx={minIdx} bufCount={reorderBufferCount}");
        

        
        LastOutputPoc = frame.Poc;

        // Remove from buffer (shift remaining entries down)
        for (int i = minIdx; i < reorderBufferCount - 1; i++)
            reorderBuffer[i] = reorderBuffer[i + 1];
        reorderBuffer[--reorderBufferCount] = default;

        int yPixels = frame.CodedWidth * frame.CodedHeight;
        int uvPixels = isChroma422 ? yPixels / 2 : yPixels / 4;
        int bps = 1 + pixelShift;
        
        int yCropPixelOffset = frame.CropTopLuma * frame.CodedWidth + frame.CropLeftLuma;
        int uvCropPixelOffset = (frame.CropTopLuma / subHeightC) * (frame.CodedWidth / 2) + (frame.CropLeftLuma / 2);
        var format = OutputFormat;

        return new DecodedVideoFrame(
            frame.DisplayWidth, frame.DisplayHeight,
            format,
            frame.PresentationTimeTicks,
            frame.Buffer,
            yOffset: yCropPixelOffset * bps, yStride: frame.CodedWidth * bps,
            uOffset: yPixels * bps + uvCropPixelOffset * bps, uStride: (frame.CodedWidth / 2) * bps,
            vOffset: (yPixels + uvPixels) * bps + uvCropPixelOffset * bps, vStride: (frame.CodedWidth / 2) * bps,
            bufferPool: BypassArrayPool ? null : FrameBufferPool<byte>.Instance);
    }

    /// <summary>
    /// Drains all frames from the reorder buffer in POC order into the output queue.
    /// Called at IDR boundaries and during flush so pending frames are not lost.
    /// </summary>
    private void DrainReorderBuffer()
    {
        while (reorderBufferCount > 0)
        {
            var frame = OutputLowestPocFrame();
            if (frame != null)
                outputQueue.Enqueue(frame.Value);
        }
    }

    private void ClearDpb()
    {
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].Buffer != null)
                ReturnBuffer(dpb[i].Buffer!);
            ReleaseColocated(ref dpb[i]);
            dpb[i] = default;
        }
        dpbCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ClipPixel(int value) => Math.Clamp(value, 0, maxPixelValue);

    /// <summary>
    /// Reads a pixel value from the frame buffer at the given pixel index.
    /// For 8-bit: reads a byte. For 10-bit: reads a ushort (2 bytes, little-endian).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPixel(ReadOnlySpan<byte> buffer, int pixelIndex)
    {
        if (pixelShift == 0) return buffer[pixelIndex];
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex];
    }

    /// <summary>
    /// Reads a pixel value from the frame buffer at the given pixel index.
    /// For 8-bit: reads a byte. For 10-bit: reads a ushort (2 bytes, little-endian).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPixel(Span<byte> buffer, int pixelIndex)
    {
        if (pixelShift == 0) return buffer[pixelIndex];
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex];
    }

    /// <summary>
    /// Writes a pixel value to the frame buffer at the given pixel index.
    /// For 8-bit: writes a byte. For 10-bit: writes a ushort (2 bytes, little-endian).
    /// Value is NOT clipped — caller must ClipPixel() first if needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePixel(Span<byte> buffer, int pixelIndex, int value)
    {
        if (pixelShift == 0) buffer[pixelIndex] = (byte)value;
        else System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex] = (ushort)value;
    }

    /// <summary>
    /// Reads a pixel the way <see cref="ReadPixel(Span{byte}, int)"/> does, but with the sample
    /// width supplied by the caller depth parameter instead of read out of a field.
    /// </summary>
    /// <remarks>
    /// The field form loads <c>pixelShift</c>, compares it and branches on every sample. It is one
    /// value for the whole run, and the reconstruction layer already knows it statically - that
    /// layer is compiled once per depth. Intra prediction alone gathers about 133 reference
    /// samples per macroblock one call at a time, so the field read is not a rounding error there.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPixel<TDepth>(ReadOnlySpan<byte> buffer, int pixelIndex)
        where TDepth : struct, IPixelDepth
    {
        if (TDepth.PixelShift == 0) return buffer[pixelIndex];
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex];
    }

    /// <inheritdoc cref="ReadPixel{TDepth}(ReadOnlySpan{byte}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPixel<TDepth>(Span<byte> buffer, int pixelIndex)
        where TDepth : struct, IPixelDepth
    {
        if (TDepth.PixelShift == 0) return buffer[pixelIndex];
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex];
    }

    /// <inheritdoc cref="ReadPixel{TDepth}(ReadOnlySpan{byte}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePixel<TDepth>(Span<byte> buffer, int pixelIndex, int value)
        where TDepth : struct, IPixelDepth
    {
        if (TDepth.PixelShift == 0) buffer[pixelIndex] = (byte)value;
        else System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex] = (ushort)value;
    }

    /// <summary>
    /// Clips to the sample range. At eight bits that range is 0..255 whatever the sequence
    /// parameter set says, so the bound folds; above eight bits it still has to be read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ClipPixel<TDepth>(int value)
        where TDepth : struct, IPixelDepth
        => TDepth.PixelShift == 0 ? Math.Clamp(value, 0, 255) : Math.Clamp(value, 0, maxPixelValue);

    public void DrainPendingFrames()
    {
        DrainReorderBuffer();
    }

    public void Flush()
    {
        DrainReorderBuffer();
        // Return any output queue frames to the pool
        while (outputQueue.Count > 0)
        {
            var frame = outputQueue.Dequeue();
            frame.Dispose();
        }
        ClearDpb();
        currentPoc = 0;
        currentFrameNum = 0;
        // h264dec.c's idr() and ff_h264_flush_change, which reset to the sentinels and not
        // to zero - see the field declarations for why the values matter.
        prevPocMsb = 1 << 16;
        prevPocLsb = -1;
        prevFrameNum = -1;
        prevFrameNumOffset = 0;
        dynamicReorderDepth = 0;
        frameRecovered = 0;
        currentPictureRecovered = 0;
        recoveryFrame = -1;
        seiRecoveryFrameCount = -1;
        // h264_decode_flush resets recovery_frame, frame_recovered, non_gray and the SEI
        // state, and does NOT reset valid_recovery_point or has_recovery_point: those two
        // describe the stream rather than the position in it, and survive a seek.
        nonGray = false;
        currentPictureGray = false;
        Array.Fill(lastPocs, int.MinValue);
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        Flush();
        context.Dispose();

        ReleaseCurrentFrameBuffer();

        isDisposed = true;
    }
}

/// <summary>
/// Reference frame in the DPB. Supports both frame and field pictures.
/// For frames: TopFieldPoc and BottomFieldPoc are both valid, Reference=3.
/// For fields: only one field POC is valid, Reference=1 (top) or 2 (bottom).
/// </summary>
internal struct ReferenceFrame
{
    // H.264 picture structure constants (matches FFmpeg PICT_*)
    public const int PictFrame = 3;
    public const int PictTopField = 1;
    public const int PictBottomField = 2;
    
    public byte[]? Buffer;
    public int Poc;              // min(TopFieldPoc, BottomFieldPoc) — used for frame-level ordering
    public int RefPoc;           // FFmpeg-compatible ref POC: second field's POC for field pairs, same as Poc for frames
    public int TopFieldPoc;      // TopFieldOrderCnt (INT_MAX if not yet decoded)
    public int BottomFieldPoc;   // BottomFieldOrderCnt (INT_MAX if not yet decoded)
    public int FrameNum;
    public long PresentationTimeTicks;
    public bool IsLongTerm;
    public int LongTermFrameIdx;
    public int PictureStructure; // PictFrame, PictTopField, or PictBottomField
    public int Reference;        // Bitmask: 1=top field available, 2=bottom available, 3=both (frame)
    public bool FieldPicture;    // True if this DPB entry was decoded as a field picture

    /// <summary>
    /// <c>H264Picture.gray</c> - this picture is a frame_num gap stand-in painted mid-grey,
    /// or an inter picture that referenced one. It is not a picture the encoder sent.
    /// </summary>
    public bool Gray;
    
    // Per-MB field decoding flags — needed for MBAFF cross-type B_Direct addressing.
    // When L1[0] is an MBAFF frame, individual MBs may be field-coded (IS_INTERLACED per-MB).
    // Indexed by mbAddr. Only populated for MBAFF frame pictures.
    public bool[]? MbFieldFlags;
    
    // Per-4x4-block motion data for co-located block lookups in B_Direct temporal mode.
    // For frame pictures: indexed by [mbAddr * 16 + y4 * 4 + x4].
    // For field pictures: use TopField*/BottomField* arrays below instead.
    public short[]? ColocatedMvX;
    public short[]? ColocatedMvY;
    public sbyte[]? ColocatedRefIdx;
    
    // L1 co-located data for spatial direct colZeroFlag fallback (when co-located L0 ref < 0)
    public short[]? ColocatedMvL1X;
    public short[]? ColocatedMvL1Y;
    public sbyte[]? ColocatedRefIdxL1;
    
    // L0 reference POCs from when this frame was decoded (for temporal direct ref mapping).
    public int[]? ColocatedL0Pocs;
    public int ColocatedL0Count;
    
    // L1 reference POCs from when this frame was decoded (for temporal direct L1 fallback).
    public int[]? ColocatedL1Pocs;
    public int ColocatedL1Count;
    
    // Per-field collocated data — prevents second field from overwriting first field's data.
    // Only populated for field picture DPB entries.
    public short[]? TopFieldColocMvX;
    public short[]? TopFieldColocMvY;
    public sbyte[]? TopFieldColocRefIdx;
    public short[]? TopFieldColocMvL1X;
    public short[]? TopFieldColocMvL1Y;
    public sbyte[]? TopFieldColocRefIdxL1;
    public int[]? TopFieldColocL0Pocs;
    public int TopFieldColocL0Count;
    public int[]? TopFieldColocL1Pocs;
    public int TopFieldColocL1Count;
    public bool[]? TopFieldColocL0IsTop;
    public bool[]? TopFieldColocL1IsTop;
    
    public short[]? BotFieldColocMvX;
    public short[]? BotFieldColocMvY;
    public sbyte[]? BotFieldColocRefIdx;
    public short[]? BotFieldColocMvL1X;
    public short[]? BotFieldColocMvL1Y;
    public sbyte[]? BotFieldColocRefIdxL1;
    public int[]? BotFieldColocL0Pocs;
    public int BotFieldColocL0Count;
    public int[]? BotFieldColocL1Pocs;
    public int BotFieldColocL1Count;
    public bool[]? BotFieldColocL0IsTop;
    public bool[]? BotFieldColocL1IsTop;
}

/// <summary>
/// Frame waiting in the reorder buffer to be output in POC (display) order.
/// </summary>
internal struct PendingOutputFrame
{
    public byte[] Buffer;
    public int Poc;
    public long PresentationTimeTicks;
    public int DisplayWidth;
    public int DisplayHeight;
    public int CodedWidth;
    public int CodedHeight;
    public int CropLeftLuma;
    public int CropTopLuma;
    public bool IsKeyFrame;
    public bool MmcoReset;

    /// <summary>
    /// <c>H264Picture.recovered</c> - the FRAME_RECOVERED_* bits that were true when this
    /// picture finished decoding. Zero means no recovery condition has been reached and the
    /// picture is not handed out.
    /// </summary>
    public int Recovered;
}

// FFmpeg's mpeg4videodec.c - the MPEG-4 Part 2 bitstream decoder. Being built up in gated
// stages; see the port checklist for what is translated.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// <c>Mpeg4DecContext</c> - the MPEG-4 decoder's state.
/// </summary>
/// <remarks>
/// The C embeds <c>H263DecContext h</c> as its first member and converts between the two with
/// a cast (<c>h263_to_mpeg4</c>), which is what inheritance is here. Fields are in the C's
/// declaration order.
/// </remarks>
public sealed class Mpeg4DecContext : H263DecContext
{
    /// <summary>Forward MV resolution.</summary>
    public int FCode;

    /// <summary>Backward MV resolution, for B-frames.</summary>
    public int BCode;

    /// <summary>Bits representing the fractional part of time.</summary>
    public int TimeIncrementBits;

    public int Shape;
    public int VolSpriteUsage;
    public int SpriteBrightnessChange;
    public int SpriteWarpingAccuracy;
    public int NumSpriteWarpingPoints;
    public int RealSpriteWarpingPoints;

    /// <summary><c>sprite_offset[isChroma][isMVY]</c>.</summary>
    public readonly int[,] SpriteOffset = new int[2, 2];

    /// <summary><c>sprite_delta[isY][isMVY]</c>.</summary>
    public readonly int[,] SpriteDelta = new int[2, 2];

    /// <summary>Sprite trajectory points.</summary>
    public readonly ushort[,] SpriteTraj = new ushort[4, 2];

    /// <summary><c>sprite_shift[isChroma]</c>.</summary>
    public readonly int[] SpriteShift = new int[2];

    public int MpegQuant;

    /// <summary>Reversible VLC.</summary>
    public int Rvlc;

    /// <summary>Could this stream contain resync markers.</summary>
    public int ResyncMarker;

    /// <summary>Time distance of the first I to B, used for interlaced B-frames.</summary>
    public int TFrame;

    public int NewPred;
    public int EnhancementType;
    public int Scalability;
    public int QuantPrecision;

    /// <summary>QP above which the AC VLC should be used for the intra DC.</summary>
    public int IntraDcThreshold;

    // Bug workarounds.
    public int DivxVersion;
    public int DivxBuild;
    public int XvidBuild;
    public int LavcBuild;

    /// <summary>
    /// DivX 5.01 puts several frames inside one, and this holds what is left over.
    /// </summary>
    public byte[]? BitstreamBuffer;

    /// <summary>How much of <see cref="BitstreamBuffer"/> is in use.</summary>
    public int BitstreamBufferSize;

    /// <summary>
    /// Where the leftover starts in <see cref="BitstreamBuffer"/>. The C moves the
    /// <c>AVBufferRef</c>'s <c>data</c> pointer into the packet it references; a C# array
    /// reference cannot be moved, so the displacement is carried alongside it.
    /// </summary>
    public int BitstreamBufferOffset;

    public int VoType;

    /// <summary>Whether the warning about invalid DivX B-frames has been shown.</summary>
    public int ShowedPackedWarning;

    /// <summary>
    /// Whether the stream carries the <c>low_delay</c> flag; used to work around encoders
    /// that do not write it.
    /// </summary>
    public int VolControlParameters;

    public int CplxEstimationTrashI;
    public int CplxEstimationTrashP;
    public int CplxEstimationTrashB;

    public int Rgb;

    /// <summary>
    /// The three unquantisers the C keeps as function pointers. MPEG-4 needs more than one at
    /// a time - the MPEG-2 quantiser for inter blocks and the H.263 one for intra - so unlike
    /// H.263 this cannot be the single <c>DctUnquantize</c> kind on the shared context.
    /// </summary>
    public UnquantizeKind DctUnquantizeMpeg2Inter;
    public UnquantizeKind DctUnquantizeMpeg2Intra;
    public UnquantizeKind DctUnquantizeH263Intra;

    /// <summary>
    /// Which inter unquantiser the reconstruction should run, or <c>None</c>. The C sets
    /// <c>s.dct_unquantize_inter</c> to NULL when mpeg_quant is off, and that NULL is what
    /// tells ff_mpv_reconstruct_mb not to unquantise inter blocks at all.
    /// </summary>
    public UnquantizeKind DctUnquantizeInterActive;

    /// <summary>
    /// <c>block32[12][64]</c>, the Studio Profile's wider coefficients. A union in the C with
    /// <c>dpcm_macroblock[3][256]</c>; both are Studio-only and the two are kept apart here
    /// because C# has no unions and nothing reads them at once.
    /// </summary>
    public readonly int[] Block32 = new int[12 * 64];

    /// <summary><c>dpcm_macroblock[3][256]</c>.</summary>
    public readonly short[] DpcmMacroblock = new short[3 * 256];

    /// <summary>0 = DCT, 1 = DPCM top to bottom, -1 = DPCM bottom to top.</summary>
    public int DpcmDirection;

    public int DctPrecision;
}

/// <summary>The MPEG-4 Part 2 bitstream decoder (<c>mpeg4videodec.c</c>).</summary>
public static class Mpeg4VideoDec
{
    /// <summary><c>SPRITE_TRAJ_VLC_BITS</c>.</summary>
    public const int SpriteTrajVlcBits = 6;

    /// <summary><c>DC_VLC_BITS</c>.</summary>
    public const int DcVlcBits = 9;

    /// <summary><c>MB_TYPE_B_VLC_BITS</c>.</summary>
    public const int MbTypeBVlcBits = 4;

    /// <summary><c>STUDIO_INTRA_BITS</c>.</summary>
    public const int StudioIntraBits = 9;

    /// <summary><c>mpeg4_block_count</c> - blocks per macroblock, indexed by chroma format.</summary>
    public static ReadOnlySpan<byte> Mpeg4BlockCount => [0, 6, 8, 12];

    /// <summary><c>mb_type_b_map</c> - what each of the four B-macroblock codes means.</summary>
    public static ReadOnlySpan<int> MbTypeBMap =>
    [
        MbType.Direct2    | MbType.BidirMv,
        MbType.BidirMv    | MbType.Size16X16,
        MbType.BackwardMv | MbType.Size16X16,
        MbType.ForwardMv  | MbType.Size16X16,
    ];

    /// <summary><c>dc_lum[512]</c>.</summary>
    public static readonly VLCElem[] DcLum = new VLCElem[512];

    /// <summary><c>dc_chrom[512]</c>.</summary>
    public static readonly VLCElem[] DcChrom = new VLCElem[512];

    /// <summary><c>sprite_trajectory[128]</c>.</summary>
    public static VLCElem[] SpriteTrajectory = [];

    /// <summary><c>mb_type_b_vlc[16]</c>.</summary>
    public static readonly VLCElem[] MbTypeBVlc = new VLCElem[16];

    /// <summary><c>ff_mpeg4_rl_intra</c>, built and with its 32 per-quantiser tables.</summary>
    public static RlTable Mpeg4RlIntra = null!;

    /// <summary><c>ff_rvlc_rl_inter</c>.</summary>
    public static RlTable RvlcRlInter = null!;

    /// <summary><c>ff_rvlc_rl_intra</c>.</summary>
    public static RlTable RvlcRlIntra = null!;

    private static readonly object initLock = new();
    private static bool initialised;

    /// <summary><c>mpeg4_init_static</c> - every table the decoder reads, built once.</summary>
    /// <remarks>
    /// The Studio Profile's books (<c>studio_luma_dc</c>, <c>studio_chroma_dc</c> and the
    /// twelve <c>studio_intra_tab</c>) are not built: the Studio Profile is out of scope and
    /// every path that would read them throws. Recorded as <c>diverged</c> rather than skipped
    /// silently.
    /// </remarks>
    public static void Mpeg4InitStatic()
    {
        lock (initLock)
        {
            if (initialised)
                return;

            Mpeg4RlIntra = Mpeg4Data.Mpeg4RlIntra();
            Rl.Init(Mpeg4RlIntra);
            // INIT_FIRST_VLC_RL: quantiser zero's table is the one the others are derived
            // from, so it is the only one allocated here and Rl.InitVlc fills the rest.
            Mpeg4RlIntra.RlVlc[0] = new VLCElem[554];
            Rl.InitVlc(Mpeg4RlIntra, 554);

            RvlcRlInter = Mpeg4Data.RvlcRlInter();
            Rl.Init(RvlcRlInter);
            for (int q = 0; q < 32; q++)
                RvlcRlInter.RlVlc[q] = new VLCElem[1072];
            Rl.InitVlc(RvlcRlInter, 1072);

            RvlcRlIntra = Mpeg4Data.RvlcRlIntra();
            Rl.Init(RvlcRlIntra);
            RvlcRlIntra.RlVlc[0] = new VLCElem[1072];
            Rl.InitVlc(RvlcRlIntra, 1072);

            byte[] dcLum = Mpeg4Data.Mpeg4DCtabLum;
            Vlc.InitTableSparse(DcLum, DcVlcBits, 10,
                                i => dcLum[2 * i + 1], i => dcLum[2 * i]);

            byte[] dcChrom = Mpeg4Data.Mpeg4DCtabChrom;
            Vlc.InitTableSparse(DcChrom, DcVlcBits, 10,
                                i => dcChrom[2 * i + 1], i => dcChrom[2 * i]);

            byte[] trajLens = Mpeg4Data.SpriteTrajectoryLens;
            SpriteTrajectory = Vlc.InitFromLengths(SpriteTrajVlcBits, 15,
                                                   i => trajLens[i], null);

            byte[] mbTypeB = Mpeg4Data.MbTypeBTab;
            Vlc.InitTableSparse(MbTypeBVlc, MbTypeBVlcBits, 4,
                                i => mbTypeB[2 * i + 1], i => mbTypeB[2 * i],
                                i => MbTypeBMap[i]);

            initialised = true;
        }
    }

    // --- header parsing ------------------------------------------------------------------

    /// <summary><c>FRAME_SKIPPED</c> (h263dec.h) - the frame is not coded.</summary>
    public const int FrameSkipped = 100;

    // Shapes.
    public const int RectShape = 0;
    public const int BinShape = 1;
    public const int BinOnlyShape = 2;
    public const int GrayShape = 3;

    // Visual object types.
    public const int SimpleVoType = 1;
    public const int CoreVoType = 3;
    public const int MainVoType = 4;
    public const int NbitVoType = 5;
    public const int ArtsVoType = 10;
    public const int AceVoType = 12;
    public const int SimpleStudioVoType = 14;
    public const int CoreStudioVoType = 15;
    public const int AdvSimpleVoType = 17;

    public const int VotVideoId = 1;
    public const int VotStillTextureId = 2;

    /// <summary><c>EXTENDED_PAR</c> - the aspect-ratio code that carries its own numbers.</summary>
    public const int ExtendedPar = 15;

    // vol_sprite_usage / sprite_enable.
    public const int StaticSprite = 1;
    public const int GmcSprite = 2;

    // Start codes.
    public const uint VosStartcode = 0x1B0;
    public const uint UserDataStartcode = 0x1B2;
    public const uint GopStartcode = 0x1B3;
    public const uint VisualObjStartcode = 0x1B5;
    public const uint VopStartcode = 0x1B6;
    public const uint SliceStartcode = 0x1B7;
    public const uint ExtStartcode = 0x1B8;

    public const int QuantMatrixExtId = 0x3;

    /// <summary><c>MAX_NVOP_SIZE</c> - smaller packets likely hold no real frame.</summary>
    public const int MaxNvopSize = 19;

    /// <summary><c>CHROMA_420</c>, the only chroma format this decoder accepts.</summary>
    private const int Chroma420 = 1;

    /// <summary><c>AV_RL32("MP4S")</c>, the one codec tag the VOL header treats specially.</summary>
    private const uint TagMp4S = 0x5334504D;

    /// <summary>
    /// The IDCT permutation. This port's IDCT is FFmpeg's C one, whose permutation is the
    /// identity, so <c>s.idsp.idct_permutation[i]</c> is <c>i</c> - the same choice the MPEG-2
    /// port made. Named rather than elided so the next IDCT to arrive has something to change.
    /// </summary>
    private static ReadOnlySpan<byte> IdctPermutation => IdctDsp.IdentityPermutation;

    /// <summary><c>mpeg4_decode_gop_header</c>.</summary>
    private static int Mpeg4DecodeGopHeader(ref MpegEncContext s, ref GetBitContext gb)
    {
        if (gb.ShowBits(23) == 0)
            return -1;                                  // GOP header invalid

        int hours = (int)gb.GetBits(5);
        int minutes = (int)gb.GetBits(6);
        gb.CheckMarker();                               // in gop_header
        int seconds = (int)gb.GetBits(6);

        s.TimeBase = seconds + 60 * (minutes + 60 * hours);

        gb.SkipBits1();
        gb.SkipBits1();

        return 0;
    }

    /// <summary><c>mpeg4_decode_profile_level</c>.</summary>
    private static int Mpeg4DecodeProfileLevel(ref GetBitContext gb, out int profile, out int level)
    {
        profile = (int)gb.GetBits(4);
        level = (int)gb.GetBits(4);

        // Simple profile, level 0.
        if (profile == 0 && level == 8)
            level = 0;

        return 0;
    }

    /// <summary><c>mpeg4_decode_visual_object</c>.</summary>
    private static int Mpeg4DecodeVisualObject(Mpeg4DecContext ctx, ref GetBitContext gb)
    {
        int isVisualObjectIdentifier = (int)gb.GetBits1();
        if (isVisualObjectIdentifier != 0)
            gb.SkipBits(4 + 3);

        int visualObjectType = (int)gb.GetBits(4);

        if (visualObjectType == VotVideoId || visualObjectType == VotStillTextureId)
        {
            int videoSignalType = (int)gb.GetBits1();
            if (videoSignalType != 0)
            {
                gb.SkipBits(3);                         // video_format
                int videoRange = (int)gb.GetBits1();
                int colorDescription = (int)gb.GetBits1();

                // AVCOL_RANGE_JPEG is 2, AVCOL_RANGE_MPEG 1.
                ctx.Avctx.ColorRange = videoRange != 0 ? 2 : 1;

                if (colorDescription != 0)
                {
                    ctx.Avctx.ColorPrimaries = (int)gb.GetBits(8);
                    ctx.Avctx.ColorTrc = (int)gb.GetBits(8);
                    ctx.Avctx.Colorspace = (int)gb.GetBits(8);
                }
            }
        }

        return 0;
    }

    /// <summary><c>mpeg4_load_default_matrices</c>.</summary>
    private static void Mpeg4LoadDefaultMatrices(ref MpegEncContext s)
    {
        ReadOnlySpan<byte> perm = IdctPermutation;
        for (int i = 0; i < 64; i++)
        {
            int j = perm[i];
            int v = Mpeg4Data.Mpeg4DefaultIntraMatrix[i];
            s.IntraMatrix[j] = (ushort)v;
            s.ChromaIntraMatrix[j] = (ushort)v;

            v = Mpeg4Data.Mpeg4DefaultNonIntraMatrix[i];
            s.InterMatrix[j] = (ushort)v;
            s.ChromaInterMatrix[j] = (ushort)v;
        }
    }

    /// <summary>
    /// <c>extension_and_user_data</c> - the only thing the C reads here is a quantiser-matrix
    /// extension, and only for the Studio Profile ids.
    /// </summary>
    /// <remarks>
    /// The extension itself is not ported, so this consumes the start code the C does and then
    /// throws, rather than skipping it and decoding the rest with the wrong matrices.
    /// </remarks>
    private static void ExtensionAndUserData(ref GetBitContext gb, int id)
    {
        uint startcode = gb.ShowBits(32);
        if (startcode == UserDataStartcode || startcode == ExtStartcode)
        {
            if ((id == 2 || id == 4) && startcode == ExtStartcode)
            {
                gb.SkipBits(32);
                int extensionType = (int)gb.GetBits(4);
                if (extensionType == QuantMatrixExtId)
                    throw new NotSupportedException(
                        "MPEG-4 quantiser matrix extension: Studio Profile, not ported.");
            }
        }
    }

    /// <summary><c>decode_vol_header</c> - the video object layer header.</summary>
    private static int DecodeVolHeader(Mpeg4DecContext ctx, ref GetBitContext gb)
    {
        ref MpegEncContext s = ref ctx.C;
        int voVerId;

        gb.SkipBits(1);                                 // random access
        ctx.VoType = (int)gb.GetBits(8);

        if (ctx.VoType == CoreStudioVoType || ctx.VoType == SimpleStudioVoType)
        {
            if (ctx.Avctx.Profile != AvProfile.Unknown &&
                ctx.Avctx.Profile != AvProfile.Mpeg4SimpleStudio)
                return -1;
            s.StudioProfile = 1;
            ctx.Avctx.Profile = AvProfile.Mpeg4SimpleStudio;
            throw new NotSupportedException("MPEG-4 Studio Profile VOL header: not ported.");
        }

        if (s.StudioProfile != 0)
            throw new NotSupportedException("MPEG-4 Studio Profile: not ported.");

        if (gb.GetBits1() != 0)                         // is_ol_id
        {
            voVerId = (int)gb.GetBits(4);               // vo_ver_id
            gb.SkipBits(3);                             // vo_priority
        }
        else
        {
            voVerId = 1;
        }

        int aspectRatioInfo = (int)gb.GetBits(4);
        if (aspectRatioInfo == ExtendedPar)
        {
            int parWidth = (int)gb.GetBits(8);
            int parHeight = (int)gb.GetBits(8);
            ctx.Avctx.SampleAspectRatio = new AvRational(parWidth, parHeight);
        }
        else
        {
            ctx.Avctx.SampleAspectRatio = new AvRational(
                H263Data.H263PixelAspect[2 * aspectRatioInfo],
                H263Data.H263PixelAspect[2 * aspectRatioInfo + 1]);
        }

        ctx.VolControlParameters = (int)gb.GetBits1();
        if (ctx.VolControlParameters != 0)
        {
            int chromaFormat = (int)gb.GetBits(2);
            if (chromaFormat != Chroma420)
                throw new NotSupportedException(
                    $"MPEG-4 chroma format {chromaFormat}: only 4:2:0 is defined here.");

            s.LowDelay = (int)gb.GetBits1();
            if (gb.GetBits1() != 0)                     // vbv parameters
            {
                gb.GetBits(15);                         // first_half_bitrate
                gb.CheckMarker();
                gb.GetBits(15);                         // latter_half_bitrate
                gb.CheckMarker();
                gb.GetBits(15);                         // first_half_vbv_buffer_size
                gb.CheckMarker();
                gb.GetBits(3);                          // latter_half_vbv_buffer_size
                gb.GetBits(11);                         // first_half_vbv_occupancy
                gb.CheckMarker();
                gb.GetBits(15);                         // latter_half_vbv_occupancy
                gb.CheckMarker();
            }
        }
        else
        {
            // Setting low_delay only once is deliberate in the C: a later header must not
            // override what detection decided.
            if (ctx.PictureNumber == 0)
            {
                s.LowDelay = ctx.VoType switch
                {
                    SimpleVoType or AdvSimpleVoType => 1,
                    _ => 0,
                };
            }
        }

        ctx.Shape = (int)gb.GetBits(2);                 // vol shape
        if (ctx.Shape != RectShape)
            throw new NotSupportedException(
                $"MPEG-4 video object layer shape {ctx.Shape}: only rectangular is ported.");

        gb.CheckMarker();                               // before time_increment_resolution

        ctx.Avctx.Framerate = new AvRational((int)gb.GetBits(16), ctx.Avctx.Framerate.Den);
        if (ctx.Avctx.Framerate.Num == 0)
            return -1;                                  // framerate == 0

        ctx.TimeIncrementBits = MathOps.AvLog2((uint)(ctx.Avctx.Framerate.Num - 1)) + 1;
        if (ctx.TimeIncrementBits < 1)
            ctx.TimeIncrementBits = 1;

        gb.CheckMarker();                               // before fixed_vop_rate

        ctx.Avctx.Framerate = new AvRational(
            ctx.Avctx.Framerate.Num,
            gb.GetBits1() != 0 ? (int)gb.GetBits(ctx.TimeIncrementBits) : 1);

        ctx.TFrame = 0;

        if (ctx.Shape != BinOnlyShape)
        {
            if (ctx.Shape == RectShape)
            {
                gb.CheckMarker();                       // before width
                int width = (int)gb.GetBits(13);
                gb.CheckMarker();                       // before height
                int height = (int)gb.GetBits(13);
                gb.CheckMarker();                       // after height
                // MP4S carries a size in the container that the VOL must not overwrite.
                if (width != 0 && height != 0 &&
                    !(s.Width != 0 && ctx.Avctx.CodecTag == TagMp4S))
                {
                    if (s.Width != 0 && s.Height != 0 && (s.Width != width || s.Height != height))
                        s.ContextReinit = 1;
                    s.Width = width;
                    s.Height = height;
                }
            }

            s.ProgressiveSequence = (int)gb.GetBits1() ^ 1;
            s.ProgressiveFrame = s.ProgressiveSequence;
            s.InterlacedDct = 0;
            gb.GetBits1();                              // OBMC disable; MPEG-4 has no OBMC

            if (voVerId == 1)
                ctx.VolSpriteUsage = (int)gb.GetBits1();
            else
                ctx.VolSpriteUsage = (int)gb.GetBits(2);

            if (ctx.VolSpriteUsage == StaticSprite)
                throw new NotSupportedException("MPEG-4 static sprites: not ported.");

            if (ctx.VolSpriteUsage == GmcSprite)
            {
                ctx.NumSpriteWarpingPoints = (int)gb.GetBits(6);
                if (ctx.NumSpriteWarpingPoints > 3)
                {
                    ctx.NumSpriteWarpingPoints = 0;
                    return -1;
                }
                ctx.SpriteWarpingAccuracy = (int)gb.GetBits(2);
                ctx.SpriteBrightnessChange = (int)gb.GetBits1();
            }

            if (gb.GetBits1() == 1)                     // not_8_bit
            {
                ctx.QuantPrecision = (int)gb.GetBits(4);
                if (gb.GetBits(4) != 8)                 // bits_per_pixel
                    throw new NotSupportedException("MPEG-4 N-bit: not ported.");
                if (ctx.QuantPrecision < 3 || ctx.QuantPrecision > 9)
                    ctx.QuantPrecision = 5;
            }
            else
            {
                ctx.QuantPrecision = 5;
            }

            ctx.MpegQuant = (int)gb.GetBits1();         // vol_quant_type
            if (ctx.MpegQuant != 0)
            {
                ReadOnlySpan<byte> perm = IdctPermutation;
                ReadOnlySpan<byte> zigzag = IdctDsp.ZigzagDirect;

                Mpeg4LoadDefaultMatrices(ref s);

                // Custom intra matrix.
                if (gb.GetBits1() != 0)
                {
                    int last = 0, i;
                    for (i = 0; i < 64; i++)
                    {
                        if (gb.GetBitsLeft() < 8)
                            return -1;                  // insufficient data for custom matrix
                        int v = (int)gb.GetBits(8);
                        if (v == 0)
                            break;
                        last = v;
                        s.IntraMatrix[perm[zigzag[i]]] = (ushort)last;
                    }
                    for (; i < 64; i++)                 // replicate the last value
                        s.IntraMatrix[perm[zigzag[i]]] = (ushort)last;
                }

                // Custom non-intra matrix. The C stores v in the loop and last in the tail;
                // they are the same number on every iteration that reaches the store.
                if (gb.GetBits1() != 0)
                {
                    int last = 0, i;
                    for (i = 0; i < 64; i++)
                    {
                        if (gb.GetBitsLeft() < 8)
                            return -1;
                        int v = (int)gb.GetBits(8);
                        if (v == 0)
                            break;
                        last = v;
                        s.InterMatrix[perm[zigzag[i]]] = (ushort)v;
                    }
                    for (; i < 64; i++)
                        s.InterMatrix[perm[zigzag[i]]] = (ushort)last;
                }
            }

            if (voVerId != 1)
                s.QuarterSample = (int)gb.GetBits1();
            else
                s.QuarterSample = 0;

            if (gb.GetBitsLeft() < 4)
                return -1;                              // VOL header truncated

            if (gb.GetBits1() == 0)
            {
                int pos = gb.GetBitsCount();
                int estimationMethod = (int)gb.GetBits(2);
                if (estimationMethod >= 2)
                    throw new NotSupportedException(
                        $"MPEG-4 complexity estimation method {estimationMethod} is not defined.");

                if (gb.GetBits1() == 0)
                {
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // opaque
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // transparent
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // intra_cae
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // inter_cae
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // no_update
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // upsampling
                }
                if (gb.GetBits1() == 0)
                {
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // intra_blocks
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // inter_blocks
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // inter4v_blocks
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // not coded blocks
                }
                if (!gb.CheckMarker())                  // in complexity estimation part 1
                {
                    gb.SkipBits(pos - gb.GetBitsCount());
                    NoCplxEst(ctx);
                    goto afterCplxEst;
                }
                if (gb.GetBits1() == 0)
                {
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // dct_coeffs
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // dct_lines
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // vlc_syms
                    ctx.CplxEstimationTrashI += 4 * (int)gb.GetBits1();   // vlc_bits
                }
                if (gb.GetBits1() == 0)
                {
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // apm
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // npm
                    ctx.CplxEstimationTrashB += 8 * (int)gb.GetBits1();   // interpolate_mc_q
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // forwback_mc_q
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // halfpel2
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // halfpel4
                }
                if (!gb.CheckMarker())                  // in complexity estimation part 2
                {
                    gb.SkipBits(pos - gb.GetBitsCount());
                    NoCplxEst(ctx);
                    goto afterCplxEst;
                }
                if (estimationMethod == 1)
                {
                    ctx.CplxEstimationTrashI += 8 * (int)gb.GetBits1();   // sadct
                    ctx.CplxEstimationTrashP += 8 * (int)gb.GetBits1();   // qpel
                }
            }
            else
            {
                NoCplxEst(ctx);
            }

        afterCplxEst:

            ctx.ResyncMarker = gb.GetBits1() == 0 ? 1 : 0;   // resync_marker_disabled

            ctx.DataPartitioning = (int)gb.GetBits1();
            if (ctx.DataPartitioning != 0)
                ctx.Rvlc = (int)gb.GetBits1();

            if (voVerId != 1)
            {
                ctx.NewPred = (int)gb.GetBits1();
                if (ctx.NewPred != 0)
                    throw new NotSupportedException("MPEG-4 new prediction: not ported.");
                if (gb.GetBits1() != 0)                 // reduced_res_vop
                    throw new NotSupportedException("MPEG-4 reduced resolution VOP: not ported.");
            }
            else
            {
                ctx.NewPred = 0;
            }

            ctx.Scalability = (int)gb.GetBits1();

            if (ctx.Scalability != 0)
            {
                GetBitContext bak = gb;

                gb.SkipBits1();                         // hierarchy_type
                gb.SkipBits(4);                         // ref_layer_id
                gb.SkipBits1();                         // ref_layer_sampling_dir
                int hSamplingFactorN = (int)gb.GetBits(5);
                int hSamplingFactorM = (int)gb.GetBits(5);
                int vSamplingFactorN = (int)gb.GetBits(5);
                int vSamplingFactorM = (int)gb.GetBits(5);
                ctx.EnhancementType = (int)gb.GetBits1();

                if (hSamplingFactorN == 0 || hSamplingFactorM == 0 ||
                    vSamplingFactorN == 0 || vSamplingFactorM == 0)
                {
                    // An illegal scalability header from a very broken encoder; the C rewinds
                    // and carries on as though the bit had been zero.
                    ctx.Scalability = 0;
                    gb = bak;
                }
                else
                {
                    throw new NotSupportedException("MPEG-4 scalability: not ported.");
                }
            }
        }

        return 0;
    }

    /// <summary>The C's <c>no_cplx_est</c> label, which two marker failures jump to.</summary>
    private static void NoCplxEst(Mpeg4DecContext ctx)
    {
        ctx.CplxEstimationTrashI = 0;
        ctx.CplxEstimationTrashP = 0;
        ctx.CplxEstimationTrashB = 0;
    }

    /// <summary>
    /// <c>decode_user_data</c> - the user data string, and with it the encoder detection the
    /// bug workarounds key off.
    /// </summary>
    /// <remarks>
    /// The C parses the string with four <c>sscanf</c> formats. They are translated as explicit
    /// scans rather than as a regex, because what matters is exactly which prefixes match and
    /// how many fields each one takes: <c>e</c>, the field count, is what decides whether the
    /// next format is tried, and a looser match here changes which build number a stream is
    /// credited with and therefore which workarounds it gets.
    /// </remarks>
    private static int DecodeUserData(Mpeg4DecContext ctx, ref GetBitContext gb)
    {
        Span<char> buf = stackalloc char[256];
        int i;
        for (i = 0; i < 255 && gb.GetBitsCount() < gb.SizeInBits; i++)
        {
            if (gb.ShowBits(23) == 0)
                break;
            buf[i] = (char)gb.GetBits(8);
        }
        // A span, not a string. The C's buf is a char array on the stack and every scan below
        // already works on spans; materialising a string here allocated about two hundred bytes
        // on every user data block, which is every keyframe of an Xvid stream.
        ReadOnlySpan<char> text = buf[..i];

        // DivX detection: "DivX%dBuild%d%c", then "DivX%db%d%c".
        if (ScanDivx(text, "DivXBuild", out int ver, out int build, out char last) ||
            ScanDivx(text, "DivXb", out ver, out build, out last))
        {
            ctx.DivxVersion = ver;
            ctx.DivxBuild = build;
            ctx.DivxPacked = last == 'p' ? 1 : 0;
        }

        // libavcodec detection. The C tries three formats in turn and only takes the result
        // when all four fields were filled, which is why each of these is all-or-nothing.
        bool haveBuild = false;
        int lavcBuild = 0;

        if (ScanFfmpegB(text, out lavcBuild))
        {
            haveBuild = true;
        }
        else if (ScanFfmpegVersion(text, out int fv, out int fv2, out int fv3, out int fb))
        {
            lavcBuild = fb;
            haveBuild = true;
        }
        else if (ScanLavc(text, out int lv, out int lv2, out int lv3))
        {
            // The C clamps each component to eight bits, with a warning when one overflows.
            lavcBuild = ((lv & 0xFF) << 16) + ((lv2 & 0xFF) << 8) + (lv3 & 0xFF);
            haveBuild = true;
        }
        else if (text.SequenceEqual("ffmpeg"))
        {
            ctx.LavcBuild = 4600;
        }

        if (haveBuild)
            ctx.LavcBuild = lavcBuild;

        // Xvid detection: "XviD%d".
        if (text.StartsWith("XviD", StringComparison.Ordinal) &&
            TryParseLeadingInt(text[4..], out int xvid, out _))
            ctx.XvidBuild = xvid;

        return 0;
    }

    /// <summary>
    /// <c>sscanf(buf, "DivX%d&lt;tail&gt;%d%c", ...)</c> - true when both numbers were read,
    /// with <paramref name="last"/> set only if a character followed.
    /// </summary>
    private static bool ScanDivx(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern,
                                 out int ver, out int build, out char last)
    {
        ver = build = 0;
        last = '\0';

        // "DivXBuild" stands for "DivX" + a number + "Build"; "DivXb" for "DivX" + a number
        // + "b". The literal between the two numbers is whatever follows "DivX" here.
        const string Head = "DivX";
        ReadOnlySpan<char> middle = pattern[Head.Length..];

        if (!text.StartsWith(Head, StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> rest = text[Head.Length..];
        if (!TryParseLeadingInt(rest, out ver, out int used))
            return false;
        rest = rest[used..];
        if (!rest.StartsWith(middle, StringComparison.Ordinal))
            return false;
        rest = rest[middle.Length..];
        if (!TryParseLeadingInt(rest, out build, out used))
            return false;
        rest = rest[used..];
        if (rest.Length > 0)
            last = rest[0];
        return true;
    }

    /// <summary><c>sscanf(buf, "FFmpe%*[^b]b%d", &amp;build)</c>.</summary>
    private static bool ScanFfmpegB(ReadOnlySpan<char> text, out int build)
    {
        build = 0;
        const string Head = "FFmpe";
        if (!text.StartsWith(Head, StringComparison.Ordinal))
            return false;
        // %*[^b] is one or more non-b characters, discarded, then a literal 'b'.
        int at = Head.Length;
        int start = at;
        while (at < text.Length && text[at] != 'b')
            at++;
        if (at == start || at >= text.Length)
            return false;
        at++;                                   // the 'b'
        return TryParseLeadingInt(text[at..], out build, out _);
    }

    /// <summary>
    /// <c>sscanf(buf, "FFmpeg v%d.%d.%d / libavcodec build: %d", ...)</c>.
    /// </summary>
    private static bool ScanFfmpegVersion(ReadOnlySpan<char> text, out int a, out int b,
                                          out int c, out int build)
    {
        a = b = c = build = 0;
        ReadOnlySpan<char> rest = text;
        return TakeLiteral(ref rest, "FFmpeg v") && TakeInt(ref rest, out a)
            && TakeLiteral(ref rest, ".") && TakeInt(ref rest, out b)
            && TakeLiteral(ref rest, ".") && TakeInt(ref rest, out c)
            && TakeLiteral(ref rest, " / libavcodec build: ") && TakeInt(ref rest, out build);
    }

    /// <summary><c>sscanf(buf, "Lavc%d.%d.%d", ...)</c>, all three fields required.</summary>
    private static bool ScanLavc(ReadOnlySpan<char> text, out int a, out int b, out int c)
    {
        a = b = c = 0;
        ReadOnlySpan<char> rest = text;
        return TakeLiteral(ref rest, "Lavc") && TakeInt(ref rest, out a)
            && TakeLiteral(ref rest, ".") && TakeInt(ref rest, out b)
            && TakeLiteral(ref rest, ".") && TakeInt(ref rest, out c);
    }

    private static bool TakeLiteral(ref ReadOnlySpan<char> s, string literal)
    {
        if (!s.StartsWith(literal, StringComparison.Ordinal))
            return false;
        s = s[literal.Length..];
        return true;
    }

    private static bool TakeInt(ref ReadOnlySpan<char> s, out int value)
    {
        if (!TryParseLeadingInt(s, out value, out int used))
            return false;
        s = s[used..];
        return true;
    }

    /// <summary>
    /// What <c>%d</c> accepts: optional sign, then at least one digit. Returns how many
    /// characters it consumed.
    /// </summary>
    private static bool TryParseLeadingInt(ReadOnlySpan<char> s, out int value, out int used)
    {
        value = 0;
        used = 0;
        int at = 0;
        bool negative = false;
        if (at < s.Length && (s[at] == '-' || s[at] == '+'))
        {
            negative = s[at] == '-';
            at++;
        }
        int digits = 0;
        long acc = 0;
        while (at < s.Length && s[at] >= '0' && s[at] <= '9')
        {
            acc = acc * 10 + (s[at] - '0');
            if (acc > int.MaxValue)
                acc = int.MaxValue;
            at++;
            digits++;
        }
        if (digits == 0)
            return false;
        value = (int)(negative ? -acc : acc);
        used = at;
        return true;
    }

    /// <summary><c>decode_vop_header</c> - the video object plane header, one per picture.</summary>
    private static int DecodeVopHeader(Mpeg4DecContext ctx, ref GetBitContext gb, bool parseOnly)
    {
        ref MpegEncContext s = ref ctx.C;

        s.Mcsel = 0;
        s.PictType = (AvPictureType)((int)gb.GetBits(2) + (int)AvPictureType.I);
        if (s.PictType == AvPictureType.B && s.LowDelay != 0 &&
            ctx.VolControlParameters == 0 && (ctx.Avctx.Flags & AvCodecFlagLowDelay) == 0)
        {
            // low_delay set incorrectly by the encoder.
            s.LowDelay = 0;
        }

        ctx.PartitionedFrame =
            ctx.DataPartitioning != 0 && s.PictType != AvPictureType.B ? 1 : 0;

        int timeIncr = 0;
        while (gb.GetBits1() != 0)
            timeIncr++;

        gb.CheckMarker();                               // before time_increment

        if (ctx.TimeIncrementBits == 0 ||
            (gb.ShowBits(ctx.TimeIncrementBits + 1) & 1) == 0)
        {
            // time_increment_bits does not fit the bitstream, which usually means the VOL
            // header is missing. The C recovers it by looking for the bit pattern that must
            // follow.
            for (ctx.TimeIncrementBits = 1;
                 ctx.TimeIncrementBits < 16;
                 ctx.TimeIncrementBits++)
            {
                if (s.PictType == AvPictureType.P ||
                    (s.PictType == AvPictureType.S && ctx.VolSpriteUsage == GmcSprite))
                {
                    if ((gb.ShowBits(ctx.TimeIncrementBits + 6) & 0x37) == 0x30)
                        break;
                }
                else if ((gb.ShowBits(ctx.TimeIncrementBits + 5) & 0x1F) == 0x18)
                {
                    break;
                }
            }
        }

        int timeIncrement = (int)gb.GetBits(ctx.TimeIncrementBits);

        if (s.PictType != AvPictureType.B)
        {
            s.LastTimeBase = s.TimeBase;
            s.TimeBase += timeIncr;
            s.Time = s.TimeBase * (long)ctx.Avctx.Framerate.Num + timeIncrement;
            if ((s.WorkaroundBugs & FfBugUmp4) != 0)
            {
                if (s.Time < s.LastNonBTime)
                {
                    // Header is not MPEG-4 compatible; a broken encoder.
                    s.TimeBase++;
                    s.Time += ctx.Avctx.Framerate.Num;
                }
            }
            s.PpTime = (ushort)(s.Time - s.LastNonBTime);
            s.LastNonBTime = s.Time;
        }
        else
        {
            s.Time = (s.LastTimeBase + timeIncr) * (long)ctx.Avctx.Framerate.Num + timeIncrement;
            s.PbTime = (ushort)(s.PpTime - (s.LastNonBTime - s.Time));
            if (s.PpTime <= s.PbTime ||
                s.PpTime <= s.PpTime - s.PbTime ||
                s.PpTime <= 0)
            {
                // Messed up order, maybe after seeking; skip this B picture.
                return FrameSkipped;
            }
            Mpeg4Video.Mpeg4InitDirectMv(ref s);

            if (ctx.TFrame == 0)
                ctx.TFrame = s.PbTime;
            if (ctx.TFrame == 0)
                ctx.TFrame = 1;                         // 1/0 protection
            s.PpFieldTime = (ushort)((RoundedDiv(s.LastNonBTime, ctx.TFrame) -
                                      RoundedDiv(s.LastNonBTime - s.PpTime, ctx.TFrame)) * 2);
            s.PbFieldTime = (ushort)((RoundedDiv(s.Time, ctx.TFrame) -
                                      RoundedDiv(s.LastNonBTime - s.PpTime, ctx.TFrame)) * 2);
            if (s.PpFieldTime <= s.PbFieldTime || s.PbFieldTime <= 1)
            {
                s.PbFieldTime = 2;
                s.PpFieldTime = 4;
                if (s.ProgressiveSequence == 0)
                    return FrameSkipped;
            }
        }

        gb.CheckMarker();                               // before vop_coded

        if (gb.GetBits1() != 1)                         // vop coded
        {
            ctx.SkippedLastFrame = 1;
            return FrameSkipped;
        }

        if (ctx.NewPred != 0)
            throw new NotSupportedException("MPEG-4 new prediction: not ported.");

        if (ctx.Shape != BinOnlyShape &&
            (s.PictType == AvPictureType.P ||
             (s.PictType == AvPictureType.S && ctx.VolSpriteUsage == GmcSprite)))
        {
            s.NoRounding = (int)gb.GetBits1();          // rounding type
        }
        else
        {
            s.NoRounding = 0;
        }

        if (ctx.Shape != BinOnlyShape)
        {
            gb.SkipBits(ctx.CplxEstimationTrashI);
            if (s.PictType != AvPictureType.I)
                gb.SkipBits(ctx.CplxEstimationTrashP);
            if (s.PictType == AvPictureType.B)
                gb.SkipBits(ctx.CplxEstimationTrashB);

            if (gb.GetBitsLeft() < 3)
                return -1;                              // header truncated

            ctx.IntraDcThreshold = Mpeg4Data.Mpeg4DcThreshold[(int)gb.GetBits(3)];
            if (s.ProgressiveSequence == 0)
            {
                s.TopFieldFirst = (int)gb.GetBits1();
                s.AlternateScan = (int)gb.GetBits1();
            }
            else
            {
                s.AlternateScan = 0;
            }
        }

        // Only parsing: what follows needs the sprite trajectory VLC and is of no use to a
        // parser.
        if (!parseOnly)
        {
            ReadOnlySpan<byte> perm = IdctPermutation;
            if (s.AlternateScan != 0)
            {
                MpegVideoUnquantize.InitScantable(ref s.IntraScantable, perm,
                                                  MpegVideoData.AlternateVerticalScanArray);
                IdctDsp.PermuteScantable(ctx.PermutatedIntraHScantable,
                                         MpegVideoData.AlternateVerticalScan, perm);
            }
            else
            {
                MpegVideoUnquantize.InitScantable(ref s.IntraScantable, perm,
                                                  IdctDsp.ZigzagDirectArray);
                IdctDsp.PermuteScantable(ctx.PermutatedIntraHScantable,
                                         MpegVideoData.AlternateHorizontalScan, perm);
            }
            IdctDsp.PermuteScantable(ctx.PermutatedIntraVScantable,
                                     MpegVideoData.AlternateVerticalScan, perm);

            if (s.PictType == AvPictureType.S)
            {
                if (ctx.VolSpriteUsage == StaticSprite || ctx.VolSpriteUsage == GmcSprite)
                    throw new NotSupportedException("MPEG-4 sprite trajectory: not ported.");

                Array.Clear(ctx.SpriteOffset);
                Array.Clear(ctx.SpriteDelta);
            }

            ctx.FCode = 1;
            ctx.BCode = 1;
            if (ctx.Shape != BinOnlyShape)
            {
                s.Qscale = (int)gb.GetBits(ctx.QuantPrecision);
                s.ChromaQscale = s.Qscale;
                if (s.Qscale == 0)
                    return -1;                          // damaged, or not an MPEG-4 header

                if (s.PictType != AvPictureType.I)
                {
                    ctx.FCode = (int)gb.GetBits(3);     // fcode_for
                    if (ctx.FCode == 0)
                    {
                        ctx.FCode = 1;
                        return -1;
                    }
                }

                if (s.PictType == AvPictureType.B)
                {
                    ctx.BCode = (int)gb.GetBits(3);
                    if (ctx.BCode == 0)
                    {
                        ctx.BCode = 1;
                        return -1;
                    }
                }

                if (ctx.Scalability == 0)
                {
                    if (ctx.Shape != RectShape && s.PictType != AvPictureType.I)
                        gb.SkipBits1();                 // vop shape coding type
                }
                else
                {
                    if (ctx.EnhancementType != 0 && gb.GetBits1() != 0)
                        throw new NotSupportedException(
                            "MPEG-4 load backward shape: not ported.");
                    gb.SkipBits(2);                     // ref_select_code
                }
            }

            // The C swaps two function pointers here. MPEG-4 is the one codec that needs a
            // different unquantiser for intra and inter at the same time, and the inter one
            // being absent is what tells ff_mpv_reconstruct_mb not to unquantise at all.
            s.DctUnquantize = ctx.MpegQuant != 0 ? ctx.DctUnquantizeMpeg2Intra
                                                 : ctx.DctUnquantizeH263Intra;
            ctx.DctUnquantizeInterActive = ctx.MpegQuant != 0 ? ctx.DctUnquantizeMpeg2Inter
                                                              : UnquantizeKind.None;
        }

        // Detect buggy encoders which do not set the low_delay flag: divx4, old xvid,
        // opendivx. divx5 without B-frames cannot be detected this way, although it is buggy
        // too.
        if (ctx.VoType == 0 && ctx.VolControlParameters == 0 &&
            ctx.DivxVersion == -1 && ctx.PictureNumber == 0)
        {
            s.LowDelay = 1;
        }

        ctx.PictureNumber++;

        return 0;
    }

    /// <summary><c>ROUNDED_DIV</c> (libavutil/common.h), for non-negative divisors.</summary>
    private static long RoundedDiv(long a, long b) => (a >= 0 ? a + b / 2 : a - b / 2) / b;

    /// <summary><c>AV_CODEC_FLAG_LOW_DELAY</c>.</summary>
    public const int AvCodecFlagLowDelay = 1 << 19;

    /// <summary><c>FF_BUG_UMP4</c>.</summary>
    public const int FfBugUmp4 = 8;

    /// <summary>
    /// <c>ff_mpeg4_parse_picture_header</c> - walks start codes until a VOP, decoding each
    /// header it passes, then decodes the VOP header itself.
    /// </summary>
    public static int Mpeg4ParsePictureHeader(Mpeg4DecContext ctx, ref GetBitContext gb,
                                              bool header, bool parseOnly)
    {
        ref MpegEncContext s = ref ctx.C;
        int vol = 0;

        gb.AlignGetBits();

        // Without a switch to studio profile we also did not switch bits_per_raw_sample, so a
        // non-zero value is some other instance's state and is reset.
        if (s.StudioProfile == 0 && ctx.Avctx.BitsPerRawSample != 8)
            ctx.Avctx.BitsPerRawSample = 0;

        if (ctx.Avctx.CodecTag == TagWv1F && gb.ShowBits(24) == 0x575630)
        {
            gb.SkipBits(24);
            if (gb.GetBits(8) == 0xF0)
                goto end;
        }

        uint startcode = 0xff;
        for (; ; )
        {
            if (gb.GetBitsCount() >= gb.SizeInBits)
            {
                if ((gb.SizeInBits == 8 && (ctx.DivxVersion >= 0 || ctx.XvidBuild >= 0)) ||
                    ctx.Avctx.CodecTag == TagQmp4)
                    return FrameSkipped;                // divx bug
                if (header && gb.GetBitsCount() == gb.SizeInBits)
                    return 0;                           // ordinary return when parsing extradata
                return -1;                              // end of stream
            }

            uint v = gb.GetBits(8);
            startcode = ((startcode << 8) | v) & 0xffffffff;

            if ((startcode & 0xFFFFFF00) != 0x100)
                continue;                               // no start code

            if (startcode >= 0x120 && startcode <= 0x12F)
            {
                if (vol != 0)
                    continue;                           // ignoring multiple VOL headers
                vol++;
                int ret = DecodeVolHeader(ctx, ref gb);
                if (ret < 0)
                    return ret;
            }
            else if (startcode == UserDataStartcode)
            {
                DecodeUserData(ctx, ref gb);
            }
            else if (startcode == GopStartcode)
            {
                Mpeg4DecodeGopHeader(ref s, ref gb);
            }
            else if (startcode == VosStartcode)
            {
                Mpeg4DecodeProfileLevel(ref gb, out int profile, out int level);
                if (profile == AvProfile.Mpeg4SimpleStudio && level > 0 && level < 9)
                {
                    s.StudioProfile = 1;
                    throw new NotSupportedException("MPEG-4 Studio Profile: not ported.");
                }
                if (s.StudioProfile != 0)
                    throw new NotSupportedException(
                        "MPEG-4 mix of studio and non-studio profile: not ported.");
                ctx.Avctx.Profile = profile;
                ctx.Avctx.Level = level;
            }
            else if (startcode == VisualObjStartcode)
            {
                if (s.StudioProfile != 0)
                    throw new NotSupportedException(
                        "MPEG-4 Studio Profile visual object: not ported.");
                Mpeg4DecodeVisualObject(ctx, ref gb);
            }
            else if (startcode == VopStartcode)
            {
                break;
            }

            gb.AlignGetBits();
            startcode = 0xff;
        }

    end:
        if ((ctx.Avctx.Flags & AvCodecFlagLowDelay) != 0)
            s.LowDelay = 1;

        if (s.StudioProfile != 0)
            throw new NotSupportedException("MPEG-4 Studio Profile VOP header: not ported.");

        return DecodeVopHeader(ctx, ref gb, parseOnly);
    }

    /// <summary><c>AV_RL32("WV1F")</c>.</summary>
    private const uint TagWv1F = 0x46315657;

    /// <summary><c>AV_RL32("QMP4")</c>.</summary>
    private const uint TagQmp4 = 0x34504D51;

    // --- prediction, blocks and macroblocks ----------------------------------------------

    /// <summary>
    /// <c>mpeg4_pred_dc</c> - the DC predictor, and the direction it came from.
    /// </summary>
    /// <remarks>
    /// The neighbourhood is B C / A X. Outside a slice the C substitutes 1024 rather than
    /// clearing the table, because error resilience still needs the real DC values.
    /// </remarks>
    private static int Mpeg4PredDc(ref MpegEncContext s, int n, out int dirPtr)
    {
        short[] dcVal = s.DcValBase!;
        int at = s.DcValOffset + s.BlockIndex[n];
        int wrap = s.BlockWrap[n];

        int a = dcVal[at - 1];
        int b = dcVal[at - 1 - wrap];
        int c = dcVal[at - wrap];

        if (s.FirstSliceLine != 0 && n != 3)
        {
            if (n != 2)
            {
                b = 1024;
                c = 1024;
            }
            if (n != 1 && s.MbX == s.ResyncMbX)
            {
                b = 1024;
                a = 1024;
            }
        }
        if (s.MbX == s.ResyncMbX && s.MbY == s.ResyncMbY + 1)
        {
            if (n == 0 || n == 4 || n == 5)
                b = 1024;
        }

        if (Math.Abs(a - b) < Math.Abs(b - c))
        {
            dirPtr = 1;                                 // top
            return c;
        }

        dirPtr = 0;                                     // left
        return a;
    }

    /// <summary><c>mpeg4_get_level_dc</c> - the DC, rescaled and stored for the next block.</summary>
    private static int Mpeg4GetLevelDc(ref MpegEncContext s, int n, int pred, int level)
    {
        int scale = n < 4 ? s.YDcScale : s.CDcScale;

        // The C's comment: pred is assumed positive, so this is a plain division.
        pred = (pred + (scale >> 1)) / scale;

        level += pred;
        int ret = level;
        level *= scale;
        if ((level & ~2047) != 0)
        {
            if (level < 0)
                level = 0;
            else if ((s.WorkaroundBugs & FfBugDcClip) == 0)
                level = 2047;
        }
        s.DcValBase![s.DcValOffset + s.BlockIndex[n]] = (short)level;

        return ret;
    }

    /// <summary><c>mpeg4_decode_dc</c> - the quantised DC of one block.</summary>
    private static int Mpeg4DecodeDc(Mpeg4DecContext ctx, int n, out int dirPtr)
    {
        ref MpegEncContext s = ref ctx.C;
        int code = ctx.Gb.GetVlc2(n < 4 ? DcLum : DcChrom, DcVlcBits, 1);

        if (code < 0)
        {
            dirPtr = 0;
            return -1;                                  // illegal dc vlc
        }

        int level;
        if (code == 0)
        {
            level = 0;
        }
        else
        {
            level = ctx.Gb.GetXBits(code);

            if (code > 8)
            {
                // The marker bit. FFmpeg only rejects a missing one under an err_recognition
                // flag this port does not carry, so a zero here is tolerated as it is there.
                ctx.Gb.SkipBits1();
            }
        }

        int pred = Mpeg4PredDc(ref s, n, out dirPtr);
        return Mpeg4GetLevelDc(ref s, n, pred, level);
    }

    /// <summary>
    /// <c>ff_mpeg4_pred_ac</c> - adds the AC prediction to a block, and leaves this block's
    /// own first row and column behind for the next one.
    /// </summary>
    /// <remarks>
    /// The copy at the end happens whether or not <c>ac_pred</c> is on: a block that predicts
    /// nothing is still a predictor for its neighbours.
    /// </remarks>
    public static void Mpeg4PredAc(Mpeg4DecContext ctx, Span<short> block, int n, int dir)
    {
        ref MpegEncContext s = ref ctx.C;
        sbyte[] qscaleTable = s.CurPic.QscaleTable!;
        int qo = s.CurPic.QscaleTableOffset;
        ReadOnlySpan<byte> perm = IdctPermutation;

        short[] acValBase = s.AcValBase!;
        int acVal1 = s.AcValOffset + s.BlockIndex[n] * 16;
        int acVal = acVal1;

        if (s.AcPred != 0)
        {
            if (dir == 0)
            {
                int xy = s.MbX - 1 + s.MbY * s.MbStride;
                acVal -= 16;                            // left prediction

                if (s.MbX == 0 || s.Qscale == qscaleTable[qo + xy] || n == 1 || n == 3)
                {
                    for (int i = 1; i < 8; i++)
                        block[perm[i << 3]] += acValBase[acVal + i];
                }
                else
                {
                    for (int i = 1; i < 8; i++)
                        block[perm[i << 3]] += (short)RoundedDiv(
                            acValBase[acVal + i] * qscaleTable[qo + xy], s.Qscale);
                }
            }
            else
            {
                int xy = s.MbX + s.MbY * s.MbStride - s.MbStride;
                acVal -= 16 * s.BlockWrap[n];           // top prediction

                if (s.MbY == 0 || s.Qscale == qscaleTable[qo + xy] || n == 2 || n == 3)
                {
                    for (int i = 1; i < 8; i++)
                        block[perm[i]] += acValBase[acVal + i + 8];
                }
                else
                {
                    for (int i = 1; i < 8; i++)
                        block[perm[i]] += (short)RoundedDiv(
                            acValBase[acVal + i + 8] * qscaleTable[qo + xy], s.Qscale);
                }
            }
        }

        for (int i = 1; i < 8; i++)                     // left copy
            acValBase[acVal1 + i] = block[perm[i << 3]];

        for (int i = 1; i < 8; i++)                     // top copy
            acValBase[acVal1 + 8 + i] = block[perm[i]];
    }

    /// <summary><c>mpeg4_resync_prefix</c>.</summary>
    private static ReadOnlySpan<ushort> Mpeg4ResyncPrefix =>
        [0x7F00, 0x7E00, 0x7C00, 0x7800, 0x7000, 0x6000, 0x4000, 0x0000];

    /// <summary>
    /// <c>mpeg4_is_resync</c> - whether what comes next is a resync marker or the end of the
    /// stream, and if so which macroblock it restarts at.
    /// </summary>
    /// <returns>0 when it is neither.</returns>
    private static int Mpeg4IsResync(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;
        int bitsCount = ctx.Gb.GetBitsCount();
        int v = (int)ctx.Gb.ShowBits(16);

        if ((s.WorkaroundBugs & FfBugNoPadding) != 0 && ctx.ResyncMarker == 0)
            return 0;

        while (v <= 0xFF)
        {
            if (s.PictType == AvPictureType.B ||
                (v >> (8 - (int)s.PictType)) != 1 || ctx.PartitionedFrame != 0)
                break;
            ctx.Gb.SkipBits(8 + (int)s.PictType);
            bitsCount += 8 + (int)s.PictType;
            v = (int)ctx.Gb.ShowBits(16);
        }

        if (bitsCount + 8 >= ctx.Gb.SizeInBits)
        {
            v >>= 8;
            v |= 0x7F >> (7 - (bitsCount & 7));

            if (v == 0x7F)
                return s.MbNum;
        }
        else if (v == Mpeg4ResyncPrefix[bitsCount & 7])
        {
            int mbNumBits = MathOps.AvLog2((uint)(s.MbNum - 1)) + 1;
            GetBitContext bak = ctx.Gb;

            ctx.Gb.SkipBits(1);
            ctx.Gb.AlignGetBits();

            int len;
            for (len = 0; len < 32; len++)
            {
                if (ctx.Gb.GetBits1() != 0)
                    break;
            }

            int mbNum = (int)ctx.Gb.GetBits(mbNumBits);
            if (mbNum == 0 || mbNum > s.MbNum ||
                ctx.Gb.GetBitsCount() + 6 > ctx.Gb.SizeInBits)
                mbNum = -1;

            ctx.Gb = bak;

            if (len >= Mpeg4Video.Mpeg4GetVideoPacketPrefixLength(s.PictType, ctx.FCode,
                                                                  ctx.BCode))
                return mbNum;
        }

        return 0;
    }

    /// <summary><c>FF_BUG_DC_CLIP</c>.</summary>
    public const int FfBugDcClip = 4096;

    /// <summary><c>FF_BUG_NO_PADDING</c>.</summary>
    public const int FfBugNoPadding = 16;

    /// <summary><c>FF_BUG_XVID_ILACE</c>.</summary>
    public const int FfBugXvidIlace = 4;

    /// <summary>
    /// <c>mpeg4_decode_block</c> - one 8x8 block of coefficients.
    /// </summary>
    /// <remarks>
    /// The three escape ladders are what makes this long. The first adds the table's maximum
    /// level for this run, the second adds the maximum run for this level, and the third
    /// spells both out - so the first two are only legal where the table has a maximum to
    /// add, which is why their indexing reads out of <c>max_level</c> and <c>max_run</c>
    /// rather than out of the bitstream.
    /// </remarks>
    private static int Mpeg4DecodeBlock(Mpeg4DecContext ctx, Span<short> block, int n,
                                        bool coded, bool intra, bool useIntraDcVlc, bool rvlc)
    {
        ref MpegEncContext s = ref ctx.C;
        int i, qmul, qadd, pred = 0, dcPredDir = 0;
        RlTable rl;
        VLCElem[] rlVlc;
        ReadOnlySpan<byte> scanTable;

        if (intra)
        {
            if (useIntraDcVlc)
            {
                int level;
                if (ctx.PartitionedFrame != 0)
                {
                    level = s.DcValBase![s.DcValOffset + s.BlockIndex[n]];
                    level = n < 4 ? (level + (s.YDcScale >> 1)) / s.YDcScale
                                  : (level + (s.CDcScale >> 1)) / s.CDcScale;
                    dcPredDir = (s.PredDirTable![s.MbX + s.MbY * s.MbStride] << n) & 32;
                }
                else
                {
                    level = Mpeg4DecodeDc(ctx, n, out dcPredDir);
                    if (level < 0)
                        return level;
                }
                block[0] = (short)level;
                i = 0;
            }
            else
            {
                i = -1;
                pred = Mpeg4PredDc(ref s, n, out dcPredDir);
            }

            if (!coded)
                goto notCoded;

            if (rvlc)
            {
                rl = RvlcRlIntra;
                rlVlc = RvlcRlIntra.RlVlc[0]!;
            }
            else
            {
                rl = Mpeg4RlIntra;
                rlVlc = Mpeg4RlIntra.RlVlc[0]!;
            }

            if (s.AcPred != 0)
            {
                scanTable = dcPredDir == 0 ? ctx.PermutatedIntraVScantable   // left
                                           : ctx.PermutatedIntraHScantable;  // top
            }
            else
            {
                scanTable = s.IntraScantable.Permutated;
            }

            qmul = 1;
            qadd = 0;
        }
        else
        {
            i = -1;
            if (!coded)
            {
                s.BlockLastIndex[n] = i;
                return 0;
            }

            rl = rvlc ? RvlcRlInter : ItuH263Dec.H263RlInter;
            scanTable = s.IntraScantable.Permutated;

            if (ctx.MpegQuant != 0)
            {
                qmul = 1;
                qadd = 0;
                rlVlc = (rvlc ? RvlcRlInter : ItuH263Dec.H263RlInter).RlVlc[0]!;
            }
            else
            {
                qmul = s.Qscale << 1;
                qadd = (s.Qscale - 1) | 1;
                rlVlc = (rvlc ? RvlcRlInter : ItuH263Dec.H263RlInter).RlVlc[s.Qscale]!;
            }
        }

        for (; ; )
        {
            ctx.Gb.GetRlVlc(rlVlc, ItuH263Dec.TexVlcBits, 2, out int level, out int run);

            if (level == 0)
            {
                if (rvlc)
                    throw new NotSupportedException("MPEG-4 reversible VLC: not ported.");

                uint cache = ctx.Gb.ShowBits(32);

                if ((cache & 0x80000000u) != 0)
                {
                    if ((cache & 0x40000000u) != 0)
                    {
                        // Third escape: run and level both spelled out.
                        ctx.Gb.SkipBits(2);
                        int last = (int)ctx.Gb.GetBits1();
                        run = (int)ctx.Gb.GetBits(6);

                        ctx.Gb.SkipBits1();             // 1. marker bit
                        level = ctx.Gb.GetSBits(12);
                        ctx.Gb.SkipBits1();             // 2. marker bit

                        level = level > 0 ? level * qmul + qadd : level * qmul - qadd;

                        if ((uint)(level + 2048) > 4095)
                            level = level < 0 ? -2048 : 2047;

                        i += run + 1;
                        if (last != 0)
                            i += 192;
                    }
                    else
                    {
                        // Second escape: the run is this run plus the longest the table can
                        // code for this level.
                        ctx.Gb.SkipBits(2);
                        ctx.Gb.GetRlVlc(rlVlc, ItuH263Dec.TexVlcBits, 2, out level, out run);
                        i += run + rl.MaxRun[run >> 7]![level / qmul] + 1;
                        int sign = -(int)ctx.Gb.GetBits1();
                        level = (level ^ sign) - sign;
                    }
                }
                else
                {
                    // First escape: the level is this level plus the largest the table can
                    // code for this run.
                    ctx.Gb.SkipBits(1);
                    ctx.Gb.GetRlVlc(rlVlc, ItuH263Dec.TexVlcBits, 2, out level, out run);
                    i += run;
                    level += rl.MaxLevel[run >> 7]![(run - 1) & 63] * qmul;
                    int sign = -(int)ctx.Gb.GetBits1();
                    level = (level ^ sign) - sign;
                }
            }
            else
            {
                i += run;
                int sign = -(int)ctx.Gb.GetBits1();
                level = (level ^ sign) - sign;
            }

            if (i > 62)
            {
                i -= 192;
                if ((i & ~63) != 0)
                    return -1;                          // ac-tex damaged

                block[scanTable[i]] = (short)level;
                break;
            }

            block[scanTable[i]] = (short)level;
        }

    notCoded:
        if (intra)
        {
            if (!useIntraDcVlc)
            {
                block[0] = (short)Mpeg4GetLevelDc(ref s, n, pred, block[0]);
                i -= i >> 31;                           // if (i == -1) i = 0
            }

            Mpeg4PredAc(ctx, block, n, dcPredDir);
            if (s.AcPred != 0)
                i = 63;
        }

        s.BlockLastIndex[n] = i;
        return 0;
    }

    /// <summary>
    /// The C's <c>intra:</c> label inside <c>mpeg4_decode_mb</c> - everything an intra
    /// macroblock reads before its blocks.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: in the C this is a label inside the I-picture arm that the
    /// P-picture arm jumps into with <c>goto intra</c>. C# cannot jump into a block, so it is
    /// a method and both arms call it. The body is the C's, line for line.
    /// </remarks>
    /// <returns>cbp, or negative when the macroblock is damaged.</returns>
    private static int DecodeIntraMb(Mpeg4DecContext ctx, int xy, int cbpc, int dquant,
                                     out bool useIntraDcVlc)
    {
        ref MpegEncContext s = ref ctx.C;

        s.AcPred = (int)ctx.Gb.GetBits1();
        s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] =
            (uint)(s.AcPred != 0 ? MbType.Intra | MbType.Acpred : MbType.Intra);

        int cbpy = ctx.Gb.GetVlc2(ItuH263Dec.H263CbpyVlc, ItuH263Dec.CbpyVlcBits, 1);
        if (cbpy < 0)
        {
            useIntraDcVlc = false;
            return -1;                                  // I cbpy damaged
        }
        int cbp = (cbpc & 3) | (cbpy << 2);

        useIntraDcVlc = s.Qscale < ctx.IntraDcThreshold;

        if (dquant != 0)
            MpegVideoDec.SetQscale(ref s, s.Qscale + QuantTab[(int)ctx.Gb.GetBits(2)]);

        if (s.ProgressiveSequence == 0)
            s.InterlacedDct = (int)ctx.Gb.GetBits1();

        BlockDsp.ClearBlocks(ctx.Block);
        return cbp;
    }

    /// <summary><c>quant_tab</c> - what the two-bit dquant code adds to the quantiser.</summary>
    private static ReadOnlySpan<sbyte> QuantTab => [-1, -2, 1, 2];

    /// <summary>
    /// The C's <c>last_mv[dir][1][c] = last_mv[dir][0][c] = mv[dir][0][c] = m</c> chain, which
    /// writes both field rows of the predictor so that a later field-coded macroblock in the
    /// same picture predicts from a frame-coded one.
    /// </summary>
    /// <remarks><c>last_mv[2][2][2]</c> flattens to index <c>dir * 4 + i * 2 + component</c>.</remarks>
    private static void SetLastMv(ref MpegEncContext s, int dir, int mx, int my)
    {
        s.LastMv[dir * 4 + 0] = mx;
        s.LastMv[dir * 4 + 1] = my;
        s.LastMv[dir * 4 + 2] = mx;
        s.LastMv[dir * 4 + 3] = my;
    }

    /// <summary><c>mpeg4_decode_mb</c> - one macroblock.</summary>
    /// <returns>A <see cref="H263SliceResult"/> value, or negative on a damaged macroblock.</returns>
    public static int Mpeg4DecodeMb(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;
        int cbpc, cbpy, cbp, mx, my, dquant;
        int predX, predY;
        int xy = s.MbX + s.MbY * s.MbStride;
        bool intraPath = false;
        bool useIntraDcVlc = false;

        cbp = 0;

        if (s.PictType == AvPictureType.P || s.PictType == AvPictureType.S)
        {
            do
            {
                if (ctx.Gb.GetBits1() != 0)
                {
                    // Skipped macroblock.
                    s.MbIntra = 0;
                    for (int i = 0; i < 6; i++)
                        s.BlockLastIndex[i] = -1;
                    s.MvDir = MpegVideoConstants.MvDirForward;
                    s.MvType = MpegVideoConstants.MvType16X16;

                    if (s.PictType == AvPictureType.S && ctx.VolSpriteUsage == GmcSprite)
                        throw new NotSupportedException("MPEG-4 GMC sprites: not ported.");

                    s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] =
                        (uint)(MbType.Skip | MbType.Size16X16 | MbType.ForwardMv);
                    s.Mcsel = 0;
                    s.Mv[0] = 0;
                    s.Mv[1] = 0;
                    s.CurPic.MbskipTable![xy] = 1;
                    s.MbSkipped = 1;
                    goto end;
                }

                cbpc = ctx.Gb.GetVlc2(ItuH263Dec.H263InterMcbpcVlc, ItuH263Dec.InterMcbpcVlcBits, 2);
                if (cbpc < 0)
                    return -1;                          // mcbpc damaged
            }
            while (cbpc == 20);

            dquant = cbpc & 8;
            s.MbIntra = (cbpc & 4) != 0 ? 1 : 0;
            if (s.MbIntra != 0)
            {
                // The C reaches the I-picture arm here with "goto intra". C# cannot jump into
                // a block, so that arm is DecodeIntraMb and both callers call it.
                intraPath = true;
                cbp = DecodeIntraMb(ctx, xy, cbpc, dquant, out useIntraDcVlc);
                if (cbp < 0)
                    return -1;
                goto decodeBlocks;
            }

            BlockDsp.ClearBlocks(ctx.Block);

            if (s.PictType == AvPictureType.S && ctx.VolSpriteUsage == GmcSprite &&
                (cbpc & 16) == 0)
                throw new NotSupportedException("MPEG-4 GMC sprites: not ported.");

            s.Mcsel = 0;
            cbpy = ctx.Gb.GetVlc2(ItuH263Dec.H263CbpyVlc, ItuH263Dec.CbpyVlcBits, 1);
            if (cbpy < 0)
                return -1;                              // P cbpy damaged
            cbpy ^= 0x0F;

            cbp = (cbpc & 3) | (cbpy << 2);
            if (dquant != 0)
                MpegVideoDec.SetQscale(ref s, s.Qscale + QuantTab[(int)ctx.Gb.GetBits(2)]);

            if (s.ProgressiveSequence == 0 &&
                (cbp != 0 || (s.WorkaroundBugs & FfBugXvidIlace) != 0))
                s.InterlacedDct = (int)ctx.Gb.GetBits1();

            s.MvDir = MpegVideoConstants.MvDirForward;
            if ((cbpc & 16) == 0)
            {
                if (s.ProgressiveSequence == 0 && ctx.Gb.GetBits1() != 0)
                    throw new NotSupportedException("MPEG-4 field motion: not ported.");

                s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] =
                    (uint)(MbType.Size16X16 | MbType.ForwardMv);
                s.MvType = MpegVideoConstants.MvType16X16;
                H263.H263PredMotion(ref s, 0, 0, out predX, out predY);
                mx = ItuH263Dec.H263DecodeMotion(ctx, predX, ctx.FCode);
                if (mx >= 0xffff)
                    return -1;
                my = ItuH263Dec.H263DecodeMotion(ctx, predY, ctx.FCode);
                if (my >= 0xffff)
                    return -1;
                s.Mv[0] = (short)mx;
                s.Mv[1] = (short)my;
            }
            else
            {
                s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] =
                    (uint)(MbType.Size8X8 | MbType.ForwardMv);
                s.MvType = MpegVideoConstants.MvType8X8;
                for (int i = 0; i < 4; i++)
                {
                    int motVal = H263.H263PredMotion(ref s, i, 0, out predX, out predY);
                    mx = ItuH263Dec.H263DecodeMotion(ctx, predX, ctx.FCode);
                    if (mx >= 0xffff)
                        return -1;
                    my = ItuH263Dec.H263DecodeMotion(ctx, predY, ctx.FCode);
                    if (my >= 0xffff)
                        return -1;
                    s.Mv[i * 2 + 0] = (short)mx;
                    s.Mv[i * 2 + 1] = (short)my;
                    s.CurPic.MotionValL0![motVal + 0] = (short)mx;
                    s.CurPic.MotionValL0![motVal + 1] = (short)my;
                }
            }
        }
        else if (s.PictType == AvPictureType.B)
        {
            s.MbIntra = 0;                              // B pictures never contain intra blocks
            s.Mcsel = 0;                                //      ...               true GMC blocks

            if (s.MbX == 0)
            {
                // last_mv[2][2][2] flattened: index is (dir * 2 + i) * 2 + component.
                for (int i = 0; i < 2; i++)
                {
                    s.LastMv[i * 4 + 0] = 0;
                    s.LastMv[i * 4 + 1] = 0;
                    s.LastMv[i * 4 + 2] = 0;
                    s.LastMv[i * 4 + 3] = 0;
                }
            }

            // If the following P picture skipped this macroblock, skip it here too. The table
            // is zero where that P picture used GMC, which is why a skip here is unambiguous.
            s.MbSkipped = s.NextPic.MbskipTable![s.MbY * s.MbStride + s.MbX];

            if (s.MbSkipped != 0)
            {
                for (int i = 0; i < 6; i++)
                    s.BlockLastIndex[i] = -1;

                s.MvDir = MpegVideoConstants.MvDirForward;
                s.MvType = MpegVideoConstants.MvType16X16;
                s.Mv[0] = 0;
                s.Mv[1] = 0;
                s.Mv[8] = 0;
                s.Mv[9] = 0;
                s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] =
                    (uint)(MbType.Skip | MbType.Size16X16 | MbType.ForwardMv);
                goto end;
            }

            int mbType;
            int modb1 = (int)ctx.Gb.GetBits1();
            if (modb1 != 0)
            {
                // Like MB_TYPE_DIRECT2 but with no vectors coded.
                mbType = MbType.Direct2 | MbType.Skip | MbType.BidirMv;
                cbp = 0;
            }
            else
            {
                int modb2 = (int)ctx.Gb.GetBits1();
                // The book is built sparse with MbTypeBMap as its symbol table, so this is
                // already the macroblock type and not an index into the map.
                mbType = ctx.Gb.GetVlc2(MbTypeBVlc, MbTypeBVlcBits, 1);
                if (mbType < 0)
                    return -1;                          // illegal MB_type

                if (modb2 != 0)
                {
                    cbp = 0;
                }
                else
                {
                    BlockDsp.ClearBlocks(ctx.Block);
                    cbp = (int)ctx.Gb.GetBits(6);
                }

                if ((mbType & MbType.Direct2) == 0 && cbp != 0)
                {
                    if (ctx.Gb.GetBits1() != 0)
                        MpegVideoDec.SetQscale(ref s, s.Qscale + (int)ctx.Gb.GetBits1() * 4 - 2);
                }

                if (s.ProgressiveSequence == 0)
                {
                    if (cbp != 0)
                        s.InterlacedDct = (int)ctx.Gb.GetBits1();

                    if ((mbType & MbType.Direct2) == 0 && ctx.Gb.GetBits1() != 0)
                        throw new NotSupportedException("MPEG-4 field motion: not ported.");
                }

                s.MvDir = 0;
                if ((mbType & (MbType.Direct2 | MbType.Interlaced)) == 0)
                {
                    s.MvType = MpegVideoConstants.MvType16X16;

                    // The B picture's two predictors are its own running last_mv, not the
                    // neighbour prediction a P picture uses: a B picture is never predicted
                    // from, so there is no spatial context to build.
                    if ((mbType & MbType.ForwardMv) != 0)
                    {
                        s.MvDir = MpegVideoConstants.MvDirForward;

                        mx = ItuH263Dec.H263DecodeMotion(ctx, s.LastMv[0], ctx.FCode);
                        my = ItuH263Dec.H263DecodeMotion(ctx, s.LastMv[1], ctx.FCode);
                        SetLastMv(ref s, 0, mx, my);
                        s.Mv[0] = (short)mx;
                        s.Mv[1] = (short)my;
                    }

                    if ((mbType & MbType.BackwardMv) != 0)
                    {
                        s.MvDir |= MpegVideoConstants.MvDirBackward;

                        mx = ItuH263Dec.H263DecodeMotion(ctx, s.LastMv[4], ctx.BCode);
                        my = ItuH263Dec.H263DecodeMotion(ctx, s.LastMv[5], ctx.BCode);
                        SetLastMv(ref s, 1, mx, my);
                        s.Mv[8] = (short)mx;
                        s.Mv[9] = (short)my;
                    }
                }
                else if ((mbType & MbType.Direct2) == 0)
                {
                    throw new NotSupportedException("MPEG-4 field motion: not ported.");
                }
            }

            if ((mbType & MbType.Direct2) != 0)
            {
                if ((mbType & MbType.Skip) != 0)
                {
                    mx = 0;
                    my = 0;
                }
                else
                {
                    // f_code 1: the direct-mode delta is always coded at the finest scale.
                    mx = ItuH263Dec.H263DecodeMotion(ctx, 0, 1);
                    my = ItuH263Dec.H263DecodeMotion(ctx, 0, 1);
                }

                s.MvDir = MpegVideoConstants.MvDirForward |
                          MpegVideoConstants.MvDirBackward | MpegVideoConstants.MvDirect;
                mbType |= Mpeg4Video.Mpeg4SetDirectMv(ref s, mx, my);
            }

            s.CurPic.MbType![s.CurPic.MbTypeOffset + xy] = (uint)mbType;
        }
        else
        {
            // I picture.
            do
            {
                cbpc = ctx.Gb.GetVlc2(ItuH263Dec.H263IntraMcbpcVlc,
                                      ItuH263Dec.IntraMcbpcVlcBits, 2);
                if (cbpc < 0)
                    return -1;                          // I cbpc damaged
            }
            while (cbpc == 8);

            dquant = cbpc & 4;
            s.MbIntra = 1;
            intraPath = true;
            cbp = DecodeIntraMb(ctx, xy, cbpc, dquant, out useIntraDcVlc);
            if (cbp < 0)
                return -1;
        }

    decodeBlocks:

        // Each block. The intra and inter arms differ only in the three flags, which is how
        // the C writes it too - one loop, inlined twice by the compiler.
        for (int i = 0; i < 6; i++)
        {
            int ret = Mpeg4DecodeBlock(ctx, ctx.Block.AsSpan(i * 64, 64), i, (cbp & 32) != 0,
                                       intraPath, useIntraDcVlc, false);
            if (ret < 0)
                return -1;
            cbp += cbp;
        }

    end:
        // Per-macroblock end-of-slice check.
        int next = Mpeg4IsResync(ctx);
        if (next != 0)
        {
            if (s.MbX + s.MbY * s.MbWidth + 1 >= next)
                return (int)H263SliceResult.End;

            if (s.PictType == AvPictureType.B)
            {
                // A B picture whose NEXT macroblock was skipped in the following P picture has
                // no bits for it, so the slice continues past the resync marker rather than
                // ending at it. The delta steps over the stride padding at the row's end.
                int delta = s.MbX + 1 == s.MbWidth ? 2 : 1;
                if (s.NextPic.MbskipTable![xy + delta] != 0)
                    return (int)H263SliceResult.Ok;
            }

            return (int)H263SliceResult.End;
        }

        return (int)H263SliceResult.Ok;
    }

    // --- the decoder itself ---------------------------------------------------------------

    /// <summary><c>FF_BUG_AUTODETECT</c>.</summary>
    public const int FfBugAutodetect = 1;

    /// <summary><c>FF_BUG_QPEL_CHROMA</c>.</summary>
    public const int FfBugQpelChroma = 64;

    /// <summary><c>FF_BUG_STD_QPEL</c>.</summary>
    public const int FfBugStdQpel = 128;

    /// <summary><c>FF_BUG_QPEL_CHROMA2</c>.</summary>
    public const int FfBugQpelChroma2 = 256;

    /// <summary><c>FF_BUG_DIRECT_BLOCKSIZE</c>.</summary>
    public const int FfBugDirectBlocksize = 512;

    /// <summary><c>FF_BUG_EDGE</c>.</summary>
    public const int FfBugEdge = 1024;

    /// <summary><c>FF_BUG_HPEL_CHROMA</c>.</summary>
    public const int FfBugHpelChroma = 2048;

    /// <summary><c>FF_BUG_IEDGE</c>.</summary>
    public const int FfBugIedge = 32768;

    private const uint TagXvid = 0x44495658;
    private const uint TagXvix = 0x58495658;
    private const uint TagRmp4 = 0x34504D52;
    private const uint TagZmp4 = 0x34504D5A;
    private const uint TagSipp = 0x50504953;
    private const uint TagDivx = 0x58564944;
    private const uint TagUmp4 = 0x34504D55;

    /// <summary>
    /// <c>ff_mpeg4_workaround_bugs</c> - works out which encoder wrote the stream and which
    /// of its bugs have to be reproduced.
    /// </summary>
    /// <remarks>
    /// The codec tag is consulted only when the user data said nothing, which is why the
    /// order here matters and is the C's.
    /// </remarks>
    public static void Mpeg4WorkaroundBugs(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;

        if (ctx.XvidBuild == -1 && ctx.DivxVersion == -1 && ctx.LavcBuild == -1)
        {
            uint tag = ctx.Avctx.CodecTag;
            if (tag == TagXvid || tag == TagXvix || tag == TagRmp4 || tag == TagZmp4 ||
                tag == TagSipp)
                ctx.XvidBuild = 0;
        }

        if (ctx.XvidBuild == -1 && ctx.DivxVersion == -1 && ctx.LavcBuild == -1)
        {
            if (ctx.Avctx.CodecTag == TagDivx && ctx.VoType == 0 &&
                ctx.VolControlParameters == 0)
                ctx.DivxVersion = 400;                  // divx 4
        }

        if (ctx.XvidBuild >= 0 && ctx.DivxVersion >= 0)
        {
            ctx.DivxVersion = -1;
            ctx.DivxBuild = -1;
        }

        if ((s.WorkaroundBugs & FfBugAutodetect) != 0)
        {
            if (ctx.Avctx.CodecTag == TagXvix)
                s.WorkaroundBugs |= FfBugXvidIlace;

            if (ctx.Avctx.CodecTag == TagUmp4)
                s.WorkaroundBugs |= FfBugUmp4;

            if (ctx.DivxVersion >= 500 && ctx.DivxBuild < 1814)
                s.WorkaroundBugs |= FfBugQpelChroma;

            if (ctx.DivxVersion > 502 && ctx.DivxBuild < 1814)
                s.WorkaroundBugs |= FfBugQpelChroma2;

            if ((uint)ctx.XvidBuild <= 3U)
                ctx.PaddingBugScore = 256 * 256 * 256 * 64;

            if ((uint)ctx.XvidBuild <= 1U)
                s.WorkaroundBugs |= FfBugQpelChroma;

            if ((uint)ctx.XvidBuild <= 12U)
                s.WorkaroundBugs |= FfBugEdge;

            if ((uint)ctx.XvidBuild <= 32U)
                s.WorkaroundBugs |= FfBugDcClip;

            if ((uint)ctx.LavcBuild < 4653U)
                s.WorkaroundBugs |= FfBugStdQpel;

            if ((uint)ctx.LavcBuild < 4655U)
                s.WorkaroundBugs |= FfBugDirectBlocksize;

            if ((uint)ctx.LavcBuild < 4670U)
                s.WorkaroundBugs |= FfBugEdge;

            if ((uint)ctx.LavcBuild <= 4712U)
                s.WorkaroundBugs |= FfBugDcClip;

            if ((ctx.LavcBuild & 0xFF) >= 100)
            {
                if (ctx.LavcBuild > 3621476 && ctx.LavcBuild < 3752552 &&
                    (ctx.LavcBuild < 3752037 || ctx.LavcBuild > 3752191))   // 3.2.1+
                    s.WorkaroundBugs |= FfBugIedge;
            }

            if (ctx.DivxVersion >= 0)
                s.WorkaroundBugs |= FfBugDirectBlocksize;
            if (ctx.DivxVersion == 501 && ctx.DivxBuild == 20020416)
                ctx.PaddingBugScore = 256 * 256 * 256 * 64;

            if ((uint)ctx.DivxVersion < 500U)
                s.WorkaroundBugs |= FfBugEdge;

            if (ctx.DivxVersion >= 0)
                s.WorkaroundBugs |= FfBugHpelChroma;
        }

        if ((s.WorkaroundBugs & FfBugStdQpel) != 0)
            throw new NotSupportedException(
                "MPEG-4 FF_BUG_STD_QPEL: the old quarter-pel kernels are not ported. This "
                + "stream was written by libavcodec older than build 4653.");

        // The C's condition also tests idct_algo == FF_IDCT_AUTO, which is the only value this
        // decoder is ever constructed with: there is no option plumbing here for a caller to
        // ask for a particular transform.
        if (ctx.XvidBuild >= 0 && s.StudioProfile == 0)
            SwitchToXvidIdct(ctx);
    }

    /// <summary>
    /// <c>permute_quant_matrix</c> - moves a quantiser matrix from one IDCT's coefficient
    /// permutation to another's.
    /// </summary>
    private static void PermuteQuantMatrix(Span<ushort> matrix, ReadOnlySpan<byte> newPerm,
                                           ReadOnlySpan<byte> oldPerm)
    {
        Span<ushort> tmp = stackalloc ushort[64];
        matrix[..64].CopyTo(tmp);
        for (int i = 0; i < 64; i++)
            matrix[newPerm[i]] = tmp[oldPerm[i]];
    }

    /// <summary>
    /// <c>switch_to_xvid_idct</c> - a stream Xvid wrote is decoded with Xvid's IDCT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not an optimisation and not optional. An IDCT is specified only to a tolerance, so two
    /// conforming ones disagree by a count here and there and the counts accumulate down a
    /// chain of predicted pictures. Decoding an Xvid stream with the simple transform produces
    /// a near-miss, which is the most expensive kind of wrong.
    /// </para>
    /// <para>
    /// Both transforms here use <c>FF_IDCT_PERM_NONE</c>, so the three re-permutations below
    /// are the identity composed with itself. They are translated rather than dropped because
    /// the C does them and because that would stop being true the moment a permuting transform
    /// is ported - and a scan table silently left in the wrong permutation is invisible until
    /// the pictures are wrong.
    /// </para>
    /// </remarks>
    private static void SwitchToXvidIdct(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;

        ReadOnlySpan<byte> oldPermutation = IdctDsp.IdentityPermutation;

        s.IdctAlgo = MpegIdct.Xvid;
        ReadOnlySpan<byte> newPermutation = IdctDsp.IdentityPermutation;

        // ff_mpv_idct_init's second half: the two main scan tables, reloaded under the new
        // permutation. MPEG-4 is never alternate_scan here - the VOL header rejects the
        // interlaced streams that would set it - but the branch is the C's.
        byte[] scan = s.AlternateScan != 0 ? MpegVideoData.AlternateVerticalScanArray
                                          : IdctDsp.ZigzagDirectArray;
        MpegVideoUnquantize.InitScantable(ref s.InterScantable, newPermutation, scan);
        MpegVideoUnquantize.InitScantable(ref s.IntraScantable, newPermutation, scan);

        IdctDsp.PermuteScantable(ctx.PermutatedIntraHScantable,
                                 s.AlternateScan != 0 ? MpegVideoData.AlternateVerticalScan
                                                      : MpegVideoData.AlternateHorizontalScan,
                                 newPermutation);
        IdctDsp.PermuteScantable(ctx.PermutatedIntraVScantable,
                                 MpegVideoData.AlternateVerticalScan, newPermutation);

        // Normal (that is, non-studio) MPEG-4 does not use the chroma matrices.
        PermuteQuantMatrix(s.InterMatrix, newPermutation, oldPermutation);
        PermuteQuantMatrix(s.IntraMatrix, newPermutation, oldPermutation);
    }

    /// <summary>
    /// <c>mpeg4_decode_picture_header</c> - the <c>decode_header</c> the frame loop calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first half is the DivX 5.01 and Xvid frame reorder. Such an encoder puts a P
    /// picture and the B picture that displays before it into one packet, so decoding the
    /// packet yields the P and leaves the B behind; <c>Mpeg4FrameEnd</c> stashes the leftover
    /// and this reads the NEXT call's picture out of the stash instead of out of the packet
    /// that call brought. The packet is not lost - the next call stashes it in turn.
    /// </para>
    /// <para>
    /// The stash is dropped rather than used in two cases: a packet that opens a new visual
    /// object sequence (start code 0xB0) means the stream restarted and what was held belongs
    /// to the old one, and a packet too large to be an N-VOP means this is not a reorder at
    /// all but a stream that merely looked like one.
    /// </para>
    /// </remarks>
    public static int Mpeg4DecodePictureHeader(Mpeg4DecContext ctx)
    {
        ctx.SkippedLastFrame = 0;

        if (ctx.BitstreamBuffer is not null)
        {
            int bufSize = (int)((uint)ctx.Gb.GetBitsLeft() / 8U);
            int bitstreamBufferSize = ctx.BitstreamBufferSize;
            byte[] buf = ctx.Gb.Buffer!;
            int at = ctx.Gb.StartIndex >> 3;

            if (ctx.DivxPacked != 0)
            {
                for (int i = 0; i < bufSize - 3; i++)
                {
                    if (buf[at + i] == 0 && buf[at + i + 1] == 0 && buf[at + i + 2] == 1)
                    {
                        if (buf[at + i + 3] == 0xB0)
                        {
                            // Excessive bitstream in packed xvid - discard it.
                            bitstreamBufferSize = 0;
                        }
                        break;
                    }
                }
            }

            ctx.BitstreamBufferSize = 0;
            if (bitstreamBufferSize != 0 && (ctx.DivxPacked != 0 || bufSize <= MaxNvopSize))
            {
                ctx.Gb.InitGetBits8(ctx.BitstreamBuffer, ctx.BitstreamBufferOffset,
                                    bitstreamBufferSize);
            }
            else
            {
                ctx.BitstreamBuffer = null;
            }
        }

        return Mpeg4ParsePictureHeader(ctx, ref ctx.Gb, header: false, parseOnly: false);
    }

    /// <summary>
    /// <c>ff_mpeg4_frame_end</c> - the DivX 5.01 bitstream-reorder buffer, which is the whole
    /// of the function: its body is one <c>if (h-&gt;divx_packed)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DivX-era encoder puts a P picture and the B that displays before it into one packet.
    /// Decoding that packet yields the P and leaves the B behind, so this stashes the leftover
    /// and the next call reads its picture out of the stash instead of out of the packet that
    /// call brought - see <c>Mpeg4DecodePictureHeader</c>, which is the consuming half.
    /// </para>
    /// <para>
    /// The stash holds a reference to the caller's packet and does not copy it, which is what
    /// the C does: <c>av_buffer_replace(&amp;ctx-&gt;bitstream_buffer, pkt-&gt;buf)</c> takes a
    /// reference on the packet's buffer and then points into it. That carries a lifetime
    /// contract, and it is the caller's: a packet handed to <c>H263DecodeFrame</c> must stay
    /// unmodified until the call after it returns. FFmpeg gets this from refcounting - a
    /// demuxer that reuses an AVPacket allocates a new buffer for it - and a demuxer here that
    /// hands over a pooled array it then refills would feed the next picture its own
    /// successor's bytes. Stated rather than assumed because nothing in the type system says it.
    /// </para>
    /// </remarks>
    public static void Mpeg4FrameEnd(H263DecContext h, byte[] buf, int bufSize)
    {
        var ctx = (Mpeg4DecContext)h;

        if (h.DivxPacked == 0)
            return;

        // Where in THIS packet to start looking. If the picture just decoded came out of the
        // stash, nothing of this packet has been read, so the search starts at its beginning;
        // otherwise it starts after what the picture consumed.
        int currentPos = ReadingFromStash(ctx, h) ? 0 : h.Gb.GetBitsCount() >> 3;
        bool startcodeFound = false;

        if (bufSize - currentPos > 7)
        {
            for (int i = currentPos; i < bufSize - 4; i++)
            {
                if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1 && buf[i + 3] == 0xB6)
                {
                    // Bit 0x40 is the low bit of vop_coding_type: clear for I (00) and B (10),
                    // set for P (01) and S (11). A packed packet carries a P followed by the B
                    // that displays before it, so this is what says a second picture follows.
                    startcodeFound = (buf[i + 4] & 0x40) == 0;
                    break;
                }
            }
        }

        if (startcodeFound)
        {
            ctx.BitstreamBuffer = buf;
            ctx.BitstreamBufferOffset = currentPos;
            ctx.BitstreamBufferSize = bufSize - currentPos;
        }
    }

    /// <summary>
    /// The C's <c>h-&gt;gb.buffer == ctx-&gt;bitstream_buffer-&gt;data</c> - whether the reader
    /// is positioned on the stashed leftover rather than on the packet just handed in.
    /// </summary>
    private static bool ReadingFromStash(Mpeg4DecContext ctx, H263DecContext h) =>
        ctx.BitstreamBuffer is not null &&
        ReferenceEquals(h.Gb.Buffer, ctx.BitstreamBuffer) &&
        h.Gb.StartIndex == ctx.BitstreamBufferOffset * 8;

    /// <summary>
    /// <c>mpeg4_flush</c> - the packed-bitstream stash goes as well as the reference pictures.
    /// What it holds is a reference into a packet from before the seek, and the picture it would
    /// produce belongs to a part of the stream nobody is watching any more.
    /// </summary>
    public static void Mpeg4Flush(Mpeg4DecContext ctx)
    {
        ctx.BitstreamBuffer = null;
        ctx.BitstreamBufferOffset = 0;
        ctx.BitstreamBufferSize = 0;

        MpegVideoDec.MpegFlush(ref ctx.C);
    }

    /// <summary><c>decode_init</c> - the MPEG-4 decoder's one-time setup.</summary>
    public static void Mpeg4DecodeInit(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;

        ctx.DivxVersion = -1;
        ctx.DivxBuild = -1;
        ctx.XvidBuild = -1;
        ctx.LavcBuild = -1;

        H263Dec.H263DecodeInit(ctx);

        // The C keeps three unquantisers as function pointers, because MPEG-4 needs the H.263
        // one for intra and the MPEG-2 one for inter at the same time.
        ctx.DctUnquantizeH263Intra = UnquantizeKind.H263Intra;
        ctx.DctUnquantizeMpeg2Intra = UnquantizeKind.Mpeg2Intra;
        ctx.DctUnquantizeMpeg2Inter = UnquantizeKind.Mpeg2Inter;

        s.CodecId = MpegCodecId.Mpeg4;
        s.YDcScaleTable = Mpeg4Data.Mpeg4YDcScaleTable;
        s.CDcScaleTable = Mpeg4Data.Mpeg4CDcScaleTable;

        s.H263Pred = 1;
        // The default; a VOL header may override it.
        s.LowDelay = 0;

        ctx.DecodeHeader = static h => Mpeg4DecodePictureHeader((Mpeg4DecContext)h);
        ctx.DecodeMb = static h => Mpeg4DecodeMb((Mpeg4DecContext)h);

        ctx.TimeIncrementBits = 4;                      // default for broken headers
        ctx.QuantPrecision = 5;

        ctx.Avctx.ChromaSampleLocation = 2;             // AVCHROMA_LOC_LEFT
        ctx.Avctx.Profile = AvProfile.Unknown;
        s.WorkaroundBugs |= FfBugAutodetect;

        Mpeg4InitStatic();
    }

    /// <summary>
    /// <c>ff_mpeg4_decode_video_packet_header</c> - the header that restarts decoding after a
    /// resync marker.
    /// </summary>
    /// <returns>Less than zero when what follows is not a video packet header.</returns>
    public static int Mpeg4DecodeVideoPacketHeader(Mpeg4DecContext ctx)
    {
        ref MpegEncContext s = ref ctx.C;
        int mbNumBits = MathOps.AvLog2((uint)(s.MbNum - 1)) + 1;
        int headerExtension = 0;

        // Is there room left for a video packet and its header?
        if (ctx.Gb.GetBitsCount() > ctx.Gb.SizeInBits - 20)
            return -1;

        int len;
        for (len = 0; len < 32; len++)
        {
            if (ctx.Gb.GetBits1() != 0)
                break;
        }

        if (len != Mpeg4Video.Mpeg4GetVideoPacketPrefixLength(s.PictType, ctx.FCode, ctx.BCode))
            return -1;                                  // marker does not match f_code

        // ctx.Shape is always RECT_SHAPE here: decode_vol_header throws on anything else.
        int mbNum = (int)ctx.Gb.GetBits(mbNumBits);
        if (mbNum >= s.MbNum || mbNum == 0)
            return -1;                                  // illegal mb_num in video packet

        s.MbX = mbNum % s.MbWidth;
        s.MbY = mbNum / s.MbWidth;

        int qscale = (int)ctx.Gb.GetBits(ctx.QuantPrecision);
        if (qscale != 0)
        {
            s.Qscale = qscale;
            s.ChromaQscale = qscale;
        }

        headerExtension = (int)ctx.Gb.GetBits1();

        if (headerExtension != 0)
        {
            while (ctx.Gb.GetBits1() != 0)
            {
                // modulo_time_base
            }

            ctx.Gb.CheckMarker();                       // before time_increment
            ctx.Gb.SkipBits(ctx.TimeIncrementBits);
            ctx.Gb.CheckMarker();                       // before vop_coding_type

            ctx.Gb.SkipBits(2);                         // vop coding type
            ctx.Gb.SkipBits(3);                         // intra dc vlc threshold

            if (s.PictType == AvPictureType.S && ctx.VolSpriteUsage == GmcSprite)
                throw new NotSupportedException("MPEG-4 GMC sprites: not ported.");

            if (s.PictType != AvPictureType.I)
                ctx.Gb.GetBits(3);                      // fcode_for, discarded as in the C
            if (s.PictType == AvPictureType.B)
                ctx.Gb.GetBits(3);                      // b_code, likewise
        }

        if (ctx.NewPred != 0)
            throw new NotSupportedException("MPEG-4 new prediction: not ported.");

        return 0;
    }
}

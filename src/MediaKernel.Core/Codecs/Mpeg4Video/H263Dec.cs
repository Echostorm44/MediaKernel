// FFmpeg's h263dec.c and the H263DecContext of h263dec.h - the frame and slice loop shared by
// H.263, MPEG-4 Part 2, the MSMPEG-4 family and RV10. Being built up in gated stages; see
// the port checklist for what is translated.
//
// SCOPE, recorded here because it is the thing most likely to mislead a later reader: only
// the baseline H.263 path is translated so far. Every branch belonging to a codec that shares
// this file but is not ported - MPEG-4, MSMPEG-4, WMV, RV10, FLV, H.263I, the Studio Profile,
// data partitioning, PB-frames and hardware acceleration - throws rather than being quietly
// skipped, so an unported stream fails loudly instead of decoding to something plausible.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>The AVCodecContext fields this decoder reads and writes.</summary>
public struct H263CodecContext
{
    /// <summary><c>sample_aspect_ratio</c>.</summary>
    public AvRational SampleAspectRatio;

    /// <summary><c>framerate</c>.</summary>
    public AvRational Framerate;

    /// <summary><c>chroma_sample_location</c>.</summary>
    public int ChromaSampleLocation;

    /// <summary><c>has_b_frames</c>.</summary>
    public int HasBFrames;

    /// <summary><c>coded_width</c>.</summary>
    public int CodedWidth;

    /// <summary><c>coded_height</c>.</summary>
    public int CodedHeight;

    /// <summary><c>frame_num</c>.</summary>
    public long FrameNum;

    /// <summary><c>codec_tag</c>.</summary>
    public uint CodecTag;

    /// <summary><c>profile</c>; <see cref="AvProfile.Unknown"/> until a VOS header sets it.</summary>
    public int Profile;

    /// <summary><c>level</c>.</summary>
    public int Level;

    /// <summary><c>bits_per_raw_sample</c>; non-zero only in the Studio Profile.</summary>
    public int BitsPerRawSample;

    /// <summary><c>color_range</c>.</summary>
    public int ColorRange;

    /// <summary><c>color_primaries</c>.</summary>
    public int ColorPrimaries;

    /// <summary><c>color_trc</c>.</summary>
    public int ColorTrc;

    /// <summary><c>colorspace</c>.</summary>
    public int Colorspace;

    /// <summary><c>flags</c>, of which only AV_CODEC_FLAG_LOW_DELAY is read here.</summary>
    public int Flags;
}

/// <summary>The <c>AV_PROFILE_*</c> values this decoder compares against.</summary>
public static class AvProfile
{
    /// <summary><c>AV_PROFILE_UNKNOWN</c>.</summary>
    public const int Unknown = -99;

    /// <summary><c>AV_PROFILE_MPEG4_SIMPLE_STUDIO</c>.</summary>
    public const int Mpeg4SimpleStudio = 14;
}

/// <summary>
/// <c>H263DecContext</c> - the decoder's own state, which wraps the shared
/// <see cref="MpegEncContext"/> as <c>h-&gt;c</c>.
/// </summary>
/// <remarks>
/// A class rather than a struct: it carries the <c>MpegEncContext</c>, which is already far
/// past the size where passing by value would be sane, and there is exactly one per decoder.
/// The fields are in the order the C declares them.
/// </remarks>
// Not sealed: the C embeds this as Mpeg4DecContext's first member and converts with a cast
// (h263_to_mpeg4), which is what inheritance is here.
public class H263DecContext
{
    /// <summary><c>c</c> - the shared MPEG-video context.</summary>
    public MpegEncContext C;

    /// <summary><c>gb</c>.</summary>
    public GetBitContext Gb;

    /// <summary><c>mb_num_left</c> - MBs left in this video packet (partitioned slices only).</summary>
    public int MbNumLeft;

    /// <summary>
    /// The picture this call output, which is not always the one it decoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIVERGENCE, recorded: the C hands the frame back through <c>av_frame_ref(pict, ...)</c>
    /// into the caller's AVFrame. There is no AVFrame here, so the choice the C makes between
    /// <c>cur_pic</c> and <c>last_pic</c> is recorded as a view instead, and the caller reads
    /// this rather than <c>C.CurPic</c>.
    /// </para>
    /// <para>
    /// The choice is the whole of MPEG-4's output reordering. A B picture, and any picture in a
    /// low-delay stream, is output as soon as it is decoded. An I or P picture in a stream with
    /// B pictures is held: what comes out is the previous one, because the B pictures that
    /// display between them have not been decoded yet. So the first picture of a reordered
    /// stream outputs nothing at all, and the decoder runs one picture behind for the rest.
    /// </para>
    /// </remarks>
    public MPVWorkPicture OutputPic;

    /// <summary><c>picture_number</c>.</summary>
    public int PictureNumber;

    /// <summary><c>pb_frame</c> - PB-frame mode: 0 none, 1 base, 2 improved.</summary>
    public int PbFrame;

    /// <summary><c>h263_long_vectors</c> - H.263v1 long vector mode.</summary>
    public int H263LongVectors;

    /// <summary><c>flv</c>.</summary>
    public int Flv;

    /// <summary><c>ehc_mode</c>.</summary>
    public int EhcMode;

    /// <summary><c>gob_index</c>.</summary>
    public int GobIndex;

    /// <summary><c>custom_pcf</c>.</summary>
    public int CustomPcf;

    /// <summary><c>umvplus</c> - H.263+ unrestricted motion vectors.</summary>
    public int Umvplus;

    /// <summary><c>h263_slice_structured</c>.</summary>
    public int H263SliceStructured;

    /// <summary><c>alt_inter_vlc</c>.</summary>
    public int AltInterVlc;

    /// <summary><c>loop_filter</c>.</summary>
    public int LoopFilter;

    /// <summary><c>modified_quant</c>.</summary>
    public int ModifiedQuant;

    /// <summary><c>padding_bug_score</c>.</summary>
    public int PaddingBugScore;

    /// <summary><c>skipped_last_frame</c>.</summary>
    public int SkippedLastFrame;

    /// <summary><c>divx_packed</c>.</summary>
    public int DivxPacked;

    /// <summary><c>data_partitioning</c>.</summary>
    public int DataPartitioning;

    /// <summary><c>partitioned_frame</c>.</summary>
    public int PartitionedFrame;

    /// <summary><c>slice_height</c> - in macroblocks (MSMPEG-4).</summary>
    public int SliceHeight;

    /// <summary><c>last_dc[3]</c>.</summary>
    public readonly int[] LastDc = new int[3];

    /// <summary><c>rv10_version</c>.</summary>
    public int Rv10Version;

    /// <summary><c>rv10_first_dc_coded[3]</c>.</summary>
    public readonly int[] Rv10FirstDcCoded = new int[3];

    /// <summary><c>last_resync_gb</c> - where the search for the next resync marker restarts.</summary>
    public GetBitContext LastResyncGb;

    /// <summary><c>permutated_intra_h_scantable[64]</c>.</summary>
    public readonly byte[] PermutatedIntraHScantable = new byte[64];

    /// <summary><c>permutated_intra_v_scantable[64]</c>.</summary>
    public readonly byte[] PermutatedIntraVScantable = new byte[64];

    /// <summary><c>block[6][64]</c>.</summary>
    public readonly short[] Block = new short[6 * 64];

    /// <summary>
    /// <c>decode_mb</c> - the per-macroblock decoder, which each codec's init installs. A
    /// function pointer in the C, and a delegate here for the same reason: H.263, MPEG-4 and
    /// the codecs after them share decode_slice and differ only in this.
    /// </summary>
    public Func<H263DecContext, int> DecodeMb = null!;

    /// <summary><c>decode_header</c> - the picture header parser, likewise installed by init.</summary>
    public Func<H263DecContext, int> DecodeHeader = null!;

    /// <summary>The AVCodecContext fields the decoder uses.</summary>
    public H263CodecContext Avctx;
}

/// <summary>Return values of <c>h-&gt;decode_mb</c>.</summary>
public enum H263SliceResult
{
    /// <summary><c>SLICE_OK</c>.</summary>
    Ok = 0,

    /// <summary><c>SLICE_ERROR</c>.</summary>
    Error = -1,

    /// <summary><c>SLICE_END</c> - end marker found.</summary>
    End = -2,

    /// <summary><c>SLICE_NOEND</c> - no end marker, but the macroblock count was exceeded.</summary>
    NoEnd = -3,
}

/// <summary>The frame and slice loop (<c>h263dec.c</c>).</summary>
public static class H263Dec
{
    /// <summary>
    /// <c>ff_h263_decode_init</c> - the part of it that belongs to plain H.263.
    /// </summary>
    /// <remarks>
    /// The C's switch over <c>avctx-&gt;codec-&gt;id</c> sets up five other codecs that share
    /// this file. Only H.263 is translated; the rest throw from their own entry points rather
    /// than being silently taken down this path.
    /// </remarks>
    public static void H263DecodeInit(H263DecContext h)
    {
        ref MpegEncContext s = ref h.C;

        s.OutFormat = OutputFormat.H263;
        s.CodecId = MpegCodecId.H263;
        s.LowDelay = 1;
        s.ChromaFormat = MpegVideoConstants.Chroma420;
        s.ChromaXShift = 1;
        s.ChromaYShift = 1;

        s.YDcScaleTable =
        s.CDcScaleTable = MpegVideoData.Mpeg1DcScaleTable;
        s.ChromaQscaleTable = null;

        s.DctUnquantize = UnquantizeKind.H263;

        // H.263 has no field pictures. InitBlockIndex and the reconstruction both branch on
        // this, and the default of zero is PICT_TOP_FIELD, which halves the vertical step
        // between macroblock rows.
        s.PictureStructure = MpegVideoConstants.PictFrame;
        s.ProgressiveSequence = 1;
        s.ProgressiveFrame = 1;

        // The two alternate scans AIC uses, permuted by the IDCT's permutation - which is the
        // identity here, as it is for every codec this port drives.
        IdctDsp.PermuteScantable(h.PermutatedIntraHScantable,
                                 MpegVideoData.AlternateHorizontalScan,
                                 IdctDsp.IdentityPermutation);
        IdctDsp.PermuteScantable(h.PermutatedIntraVScantable,
                                 MpegVideoData.AlternateVerticalScan,
                                 IdctDsp.IdentityPermutation);

        // The zig-zag scan, which is what a non-AIC block reads through. InitScantable rather
        // than PermuteScantable, because the unquantisers read raster_end.
        MpegVideoUnquantize.InitScantable(ref s.IntraScantable, IdctDsp.IdentityPermutation,
                                          IdctDsp.ZigzagDirectArray);
        MpegVideoUnquantize.InitScantable(ref s.InterScantable, IdctDsp.IdentityPermutation,
                                          IdctDsp.ZigzagDirectArray);

        h.Avctx.ChromaSampleLocation = 1;   // AVCHROMA_LOC_CENTER

        h.DecodeMb = static x => (int)ItuH263Dec.H263DecodeMb(x);
        h.DecodeHeader = ItuH263Dec.H263DecodePictureHeader;

        ItuH263Dec.H263DecodeInitVlc();
    }

    /// <summary>
    /// <c>ff_mpv_init_context_frame</c>'s H.263 part - the per-context tables that depend on
    /// the picture size, allocated once the header has given it.
    /// </summary>
    /// <remarks>
    /// The DC values start at 1024 rather than zero, which is the C's loop and not an
    /// optimisation: 1024 is the "no prediction available" marker <c>h263_pred_acdc</c> tests
    /// for, so a zeroed table would read as a valid prediction of zero everywhere.
    /// </remarks>
    public static void MpvInitContextFrame(H263DecContext h)
    {
        ref MpegEncContext s = ref h.C;

        s.MbWidth = (s.Width + 15) / 16;
        s.MbHeight = (s.Height + 15) / 16;
        s.MbStride = s.MbWidth + 1;
        s.B8Stride = s.MbWidth * 2 + 1;
        s.MbNum = s.MbWidth * s.MbHeight;
        s.HEdgePos = s.MbWidth * 16;
        s.VEdgePos = s.MbHeight * 16;

        s.BlockWrap[0] =
        s.BlockWrap[1] =
        s.BlockWrap[2] =
        s.BlockWrap[3] = s.B8Stride;
        s.BlockWrap[4] =
        s.BlockWrap[5] = s.MbStride;

        int ySize = s.B8Stride * (2 * s.MbHeight + 1);
        int cSize = s.MbStride * (s.MbHeight + 1);
        int ycSize = ySize + 2 * cSize;

        s.AcValBase = new short[ycSize * 16];
        s.AcValOffset = (s.B8Stride + 1) * 16;

        s.DcValBase = new short[ycSize];
        s.DcValOffset = s.B8Stride + 1;
        s.DcValBase.AsSpan().Fill(1024);

        MpegEr.MpegErInit(ref s);

        s.ContextInitialized = 1;
    }

    /// <summary>
    /// <c>decode_slice</c> - one group of blocks, from the current reader position to the end
    /// marker or the end of the picture.
    /// </summary>
    public static int DecodeSlice(H263DecContext h)
    {
        const int partMask = 0x7F;      // h->partitioned_frame is always 0 for H.263

        h.LastResyncGb = h.Gb;
        h.C.FirstSliceLine = 1;
        h.C.ResyncMbX = h.C.MbX;
        h.C.ResyncMbY = h.C.MbY;

        MpegVideoDec.SetQscale(ref h.C, h.C.Qscale);

        for (; h.C.MbY < h.C.MbHeight; h.C.MbY++)
        {
            MpegVideoDec.InitBlockIndex(ref h.C);
            for (; h.C.MbX < h.C.MbWidth; h.C.MbX++)
            {
                MpegVideoDec.UpdateBlockIndex(ref h.C, bitsPerRawSample: 8,
                                              chromaXShift: h.C.ChromaXShift);

                if (h.C.ResyncMbX == h.C.MbX && h.C.ResyncMbY + 1 == h.C.MbY)
                    h.C.FirstSliceLine = 0;

                h.C.MvDir = MpegVideoConstants.MvDirForward;
                h.C.MvType = MpegVideoConstants.MvType16X16;

                var ret = (H263SliceResult)h.DecodeMb(h);

                if (h.C.H263Pred != 0 || h.C.H263Aic != 0)
                {
                    int mbXy = h.C.MbY * h.C.MbStride + h.C.MbX;
                    if (h.C.MbIntra == 0)
                        H263.H263CleanIntraTableEntries(ref h.C, mbXy);
                    else
                        h.C.MbintraTable![mbXy] = 1;
                }

                if (h.C.PictType != AvPictureType.B)
                    H263.H263UpdateMotionVal(ref h.C);

                if (ret < 0)
                {
                    if (ret == H263SliceResult.End)
                    {
                        MpegVideoDec.MpvReconstructMb(ref h.C, h.Block);
                        if (h.LoopFilter != 0)
                            H263.H263LoopFilter(ref h.C);

                        ErrorResilience.ErAddSlice(ref h.C.Er, h.C.ResyncMbX, h.C.ResyncMbY,
                                                   h.C.MbX, h.C.MbY, ErStatus.MbEnd & partMask);

                        h.PaddingBugScore--;

                        if (++h.C.MbX >= h.C.MbWidth)
                        {
                            h.C.MbX = 0;
                            h.C.MbY++;
                        }
                        return 0;
                    }
                    if (ret == H263SliceResult.NoEnd)
                    {
                        ErrorResilience.ErAddSlice(ref h.C.Er, h.C.ResyncMbX, h.C.ResyncMbY,
                                                   h.C.MbX + 1, h.C.MbY,
                                                   ErStatus.MbEnd & partMask);
                        return -1;
                    }

                    ErrorResilience.ErAddSlice(ref h.C.Er, h.C.ResyncMbX, h.C.ResyncMbY,
                                               h.C.MbX, h.C.MbY, ErStatus.MbError & partMask);
                    return -1;
                }

                MpegVideoDec.MpvReconstructMb(ref h.C, h.Block);
                if (h.LoopFilter != 0)
                    H263.H263LoopFilter(ref h.C);
            }

            h.C.MbX = 0;
        }

        // The padding-bug heuristics the C runs here are all MPEG-4 or autodetect-flag paths
        // except this one, which is H.263's.
        if (h.C.CodecId == MpegCodecId.H263 &&
            h.Gb.GetBitsLeft() >= 8 && h.Gb.GetBitsLeft() < 300 &&
            h.C.PictType == AvPictureType.I &&
            h.Gb.ShowBits(8) == 0)
        {
            h.PaddingBugScore += 32;
        }

        ErrorResilience.ErAddSlice(ref h.C.Er, h.C.ResyncMbX, h.C.ResyncMbY, h.C.MbX, h.C.MbY,
                                   ErStatus.MbEnd & partMask);

        // The C returns AVERROR_INVALIDDATA here for a stream with unique end markers, which
        // H.263 has: reaching the bottom of the picture without having seen one is an error,
        // and the slice that did see one returned above.
        return -1;
    }

    /// <summary>
    /// <c>ff_h263_decode_frame</c> - one packet, which for H.263 is one picture.
    /// </summary>
    /// <returns>True when a picture was decoded into <c>C.CurPic</c>.</returns>
    public static bool H263DecodeFrame(H263DecContext h, byte[] buf, int bufSize)
    {
        if (bufSize == 0)
        {
            // Flush. A reordered stream still holds its last non-B picture: everything that
            // displays before it has been output, and nothing is left to decode, so an empty
            // packet is what asks for it. next_pic is that picture - see MpvFrameStart - and
            // it is unreferenced here so a second flush yields nothing.
            if ((h.C.LowDelay == 0 || h.SkippedLastFrame != 0) && h.C.NextPic.Ptr != 0)
            {
                h.OutputPic = h.C.NextPic;
                h.C.BufferPools.Pool!.UnrefPicture(ref h.C.NextPic);
                return true;
            }
            return false;
        }

        h.Gb.InitGetBits8(buf, bufSize);

        int bakWidth = h.C.Width;
        int bakHeight = h.C.Height;

        int ret = h.DecodeHeader(h);
        if (ret < 0 || ret == Mpeg4VideoDec.FrameSkipped)
        {
            // Reverting picture dimensions change due to header decoding failure.
            if (h.C.Width != bakWidth || h.C.Height != bakHeight)
            {
                h.C.Width = bakWidth;
                h.C.Height = bakHeight;
            }
        }

        // FRAME_SKIPPED is not a failure: an N-VOP is a packet that codes "the previous
        // picture again", and MPEG-4 streams from DivX-era encoders are full of them. The C
        // returns buf_size here with got_frame still zero, so the packet is consumed and no
        // picture comes out. Falling through instead would decode the bits after a header
        // that said there are none.
        if (ret == Mpeg4VideoDec.FrameSkipped)
            return false;

        if (ret < 0)
            return false;

        h.Avctx.HasBFrames = h.C.LowDelay != 0 ? 0 : 1;

        if (h.C.CodecId == MpegCodecId.Mpeg4)
        {
            // Half a bit per macroblock is less than the shortest possible coded macroblock,
            // so a packet under that cannot be the picture its header claims.
            if (h.C.PictType != AvPictureType.B && h.C.MbNum / 2 > h.Gb.GetBitsLeft())
                return false;

            // The encoder-quirk table, and with it the choice of transform. It is read every
            // picture rather than once because the fields it reads - xvid_build, divx_version,
            // lavc_build - arrive in the VOL header, and a stream may carry a new one at any
            // point.
            Mpeg4VideoDec.Mpeg4WorkaroundBugs((Mpeg4DecContext)h);

            // The C follows this with "if (studio_profile != (idsp.idct == NULL))
            // ff_mpv_idct_init(s)", which re-installs the transform when a stream crosses
            // into or out of the studio profile. This port has no studio profile - the VOL
            // header throws on one - so the condition is never true and the call is dropped.
        }

        // After the header we have the dimensions, so the picture can be set up. H.263 can
        // change size at any time, which is why this is tested every frame rather than once.
        if (h.C.Width != h.Avctx.CodedWidth || h.C.Height != h.Avctx.CodedHeight ||
            h.C.ContextReinit != 0 || h.C.ContextInitialized == 0)
        {
            h.C.ContextReinit = 0;
            h.Avctx.CodedWidth = h.C.Width;
            h.Avctx.CodedHeight = h.C.Height;
            MpvInitContextFrame(h);
        }

        // Skip a predicted picture with no reference to predict from.
        if (h.C.LastPic.Ptr == 0 && h.C.PictType == AvPictureType.B)
            return false;

        MpegVideoDec.MpvFrameStart(ref h.C);
        MpegEr.MpvErFrameStartExt(ref h.C, h.PartitionedFrame, h.C.PpTime, h.C.PbTime);

        h.C.MbX = 0;
        h.C.MbY = 0;

        DecodeSlice(h);
        while (h.C.MbY < h.C.MbHeight)
        {
            int prevX = h.C.MbX, prevY = h.C.MbY;
            if (ItuH263Dec.H263Resync(h) < 0)
                break;
            if (prevY * h.C.MbWidth + prevX < h.C.MbY * h.C.MbWidth + h.C.MbX)
                h.C.Er.ErrorOccurred = 1;

            DecodeSlice(h);
        }

        // A slice that failed is not a frame that failed: error resilience conceals what was
        // not decoded and the picture is output either way, which is what the C does with its
        // slice_ret - it only suppresses the output when the whole frame is unusable, and for
        // H.263 that is the header failing, which returned above.
        ErrorResilience.ErFrameEnd(ref h.C);
        MpegVideoDec.MpvFrameEnd(ref h.C);

        if (h.C.CodecId == MpegCodecId.Mpeg4)
            Mpeg4VideoDec.Mpeg4FrameEnd(h, buf, bufSize);

        // Which picture this call outputs - see OutputPic. An I or P picture in a reordered
        // stream outputs the one before it, and the very first outputs nothing.
        if (h.C.PictType == AvPictureType.B || h.C.LowDelay != 0)
            h.OutputPic = h.C.CurPic;
        else if (h.C.LastPic.Ptr != 0)
            h.OutputPic = h.C.LastPic;

        h.Avctx.FrameNum++;
        return h.C.LastPic.Ptr != 0 || h.C.LowDelay != 0;
    }
}

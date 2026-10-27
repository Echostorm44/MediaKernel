// The MPEG-1/2 decoder - a translation of ffmpeg-master/libavcodec/mpeg12dec.c.
//
// Being built up a piece at a time; see the port checklist for what is translated and what is
// still todo.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>
/// <c>enum Mpeg2ClosedCaptionsFormat</c> (<c>mpeg12dec.c:64</c>) - which of the several
/// incompatible caption carriages a stream's user data uses.
/// </summary>
public enum Mpeg2ClosedCaptionsFormat
{
    Auto = 0,
    A53Part4 = 1,
    Scte20 = 2,
    Dvd = 3,
    Dish = 4,
}

/// <summary>
/// <c>Mpeg12SliceContext</c> (<c>mpeg12dec.c:72</c>) - everything one slice needs while decoding.
/// </summary>
/// <remarks>
/// A class and not a struct, deliberately, against this project's general preference. It embeds
/// the whole of <see cref="MpegEncContext"/>, so passing or assigning it by value would copy a
/// hundred and twenty-eight fields; this codebase has already measured a 244-byte struct copy
/// costing 4.35x with the profiler blaming the caller. There is one of these per slice decoder,
/// not one per block, so the reference costs nothing that matters.
/// </remarks>
public sealed class Mpeg12SliceContext
{
    /// <summary><c>MPVContext c</c> - the shared MPEG-video context.</summary>
    public MpegEncContext C;

    /// <summary><c>GetBitContext gb</c>.</summary>
    public GetBitContext Gb;

    /// <summary>
    /// <c>last_dc[3]</c> - the DC predictors, one per component. Reset at the start of each
    /// slice and carried from block to block within it, which is why a lost slice loses the
    /// whole row's DC.
    /// </summary>
    public readonly int[] LastDc = new int[3];

    /// <summary>
    /// <c>block[12][64]</c> - the coefficient blocks of one macroblock. Twelve rather than six
    /// because 4:4:4 has four blocks per chroma component; 4:2:0 uses the first six.
    /// </summary>
    public readonly short[] Block = new short[12 * 64];

    /// <summary>The coefficients of block <paramref name="n"/>.</summary>
    public Span<short> BlockAt(int n) => Block.AsSpan(n * 64, 64);
}



/// <summary>
/// The fields of <c>AVCodecContext</c> that <c>mpeg_decode_postinit</c> writes, and nothing else.
/// </summary>
/// <remarks>
/// There is no AVCodecContext in this project. FFmpeg's is the decoder's whole public surface -
/// several hundred fields shared by every codec, muxer and filter - and standing one up to hold
/// the eight that MPEG-2 sets would be a large piece of scaffolding carrying no weight. These
/// are the eight, named as FFmpeg names them, so that <c>avctx-&gt;sample_aspect_ratio</c> in the
/// C is <c>Avctx.SampleAspectRatio</c> here and the postinit translation reads line for line.
///
/// All of them are sequence properties: they change when a sequence header does, not per picture.
/// </remarks>
public struct Mpeg12CodecContext
{
    /// <summary><c>sample_aspect_ratio</c> - the shape of one sample, not of the picture.</summary>
    public AvRational SampleAspectRatio;

    /// <summary><c>framerate</c>.</summary>
    public AvRational Framerate;

    /// <summary><c>chroma_sample_location</c>, as <c>AVChromaLocation</c>.</summary>
    public int ChromaSampleLocation;

    /// <summary>
    /// <c>has_b_frames</c> - the number of frames of reordering delay, which for MPEG is one
    /// when the stream may contain B pictures and zero when it may not.
    /// </summary>
    public int HasBFrames;

    /// <summary><c>color_primaries</c>, from the sequence display extension.</summary>
    public int ColorPrimaries;

    /// <summary><c>color_trc</c>.</summary>
    public int ColorTrc;

    /// <summary><c>colorspace</c>.</summary>
    public int Colorspace;

    /// <summary>
    /// <c>bit_rate</c> for MPEG-1 and <c>rc_max_rate</c> for MPEG-2. The two are never both
    /// meaningful - MPEG-1's bit rate field is the stream's rate, MPEG-2's is a ceiling.
    /// </summary>
    public long BitRate;

    /// <inheritdoc cref="BitRate"/>
    public long RcMaxRate;
}

/// <summary>
/// The <c>AVStereo3DType</c> values MPEG-2 user data can signal, with libavutil's numbering.
/// </summary>
/// <remarks>
/// Only four of the nine occur here, because the JP3D user data defines only four; the others
/// are left out rather than carried as unreachable names.
/// </remarks>
public static class AvStereo3dType
{
    /// <summary><c>AV_STEREO3D_2D</c> - signalled explicitly to turn stereo off.</summary>
    public const int TwoDimensional = 0;

    /// <summary><c>AV_STEREO3D_SIDEBYSIDE</c>.</summary>
    public const int SideBySide = 1;

    /// <summary><c>AV_STEREO3D_TOPBOTTOM</c>.</summary>
    public const int TopBottom = 2;

    /// <summary><c>AV_STEREO3D_SIDEBYSIDE_QUINCUNX</c>.</summary>
    public const int SideBySideQuincunx = 5;
}

/// <summary><c>AVChromaLocation</c> - where a chroma sample sits relative to its luma samples.</summary>
public static class AvChromaLocation
{
    /// <summary><c>AVCHROMA_LOC_UNSPECIFIED</c>.</summary>
    public const int Unspecified = 0;

    /// <summary><c>AVCHROMA_LOC_LEFT</c> - MPEG-2 4:2:0, halfway down between two luma rows.</summary>
    public const int Left = 1;

    /// <summary><c>AVCHROMA_LOC_CENTER</c> - MPEG-1 4:2:0, in the middle of the four luma samples.</summary>
    public const int Center = 2;

    /// <summary><c>AVCHROMA_LOC_TOPLEFT</c> - 4:2:2 and 4:4:4, co-sited.</summary>
    public const int TopLeft = 3;
}
/// <summary>
/// The part of libavutil's <c>AVPanScan</c> that MPEG-2 fills: the display rectangle from the
/// sequence display extension, and up to three centre offsets from the picture display extension.
/// </summary>
/// <remarks>
/// FFmpeg's AVPanScan also carries an id field that no decoder in the tree sets, so it is not
/// here. The three positions are per displayed frame, not per coded picture - a progressive
/// sequence repeating a field displays the same coded picture up to three times, and each
/// showing may be panned differently.
/// </remarks>
public struct AvPanScan
{
    /// <summary><c>width</c> - in samples, already multiplied up from the coded units of 16.</summary>
    public int Width;

    /// <inheritdoc cref="Width"/>
    public int Height;

    /// <summary><c>position[3][2]</c> - horizontal then vertical offset, in sixteenths of a sample.</summary>
    public PanScanPositions Position;

    /// <summary>Three (x, y) pairs, inline so a context carries no reference to chase.</summary>
    [System.Runtime.CompilerServices.InlineArray(6)]
    public struct PanScanPositions
    {
        private short element;
    }
}
/// <summary>
/// <c>Mpeg1Context</c> (<c>mpeg12dec.c:81</c>) - per-stream state that outlives a slice.
/// </summary>
public sealed class Mpeg1Context
{
    /// <summary><c>slice</c>.</summary>
    public readonly Mpeg12SliceContext Slice = new();

    /// <summary><c>has_stereo3d</c>.</summary>
    public int HasStereo3d;

    /// <summary><c>cc_format</c>.</summary>
    public Mpeg2ClosedCaptionsFormat CcFormat;

    /// <summary><c>afd</c> - active format description.</summary>
    public byte Afd;

    /// <summary><c>has_afd</c>.</summary>
    public int HasAfd;

    /// <summary><c>slice_count</c>.</summary>
    public int SliceCount;

    /// <summary>The AVCodecContext fields this decoder sets. See <see cref="Mpeg12CodecContext"/>.</summary>
    public Mpeg12CodecContext Avctx;

    /// <summary><c>stereo3d_type</c> - meaningful only when <see cref="HasStereo3d"/> is set.</summary>
    public int Stereo3dType;

    /// <summary>
    /// <c>a53_buf_ref</c> - closed-caption triplets accumulated for the picture being decoded.
    /// </summary>
    /// <remarks>
    /// The C grows a reference-counted buffer per picture and hands it to the frame. Here it is
    /// allocated once at the C's own maximum and refilled, because a per-picture allocation is
    /// a defect in this project. <see cref="A53Size"/> is how much of it is live.
    /// </remarks>
    public readonly byte[] A53Buf = new byte[3 * Mpeg12Dec.A53MaxCcCount];

    /// <inheritdoc cref="A53Buf"/>
    public int A53Size;

    /// <summary>
    /// A fixed buffer the SCTE-20 caption parser reads its bits out of, so that it allocates
    /// nothing per picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because <see cref="GetBitContext"/> holds a <c>byte[]</c> while the decoder is
    /// handed a <c>ReadOnlySpan&lt;byte&gt;</c>, so there is no array to point it at without
    /// copying. User data arrives once per picture and a per-picture allocation is a defect in
    /// this project.
    /// </para>
    /// <para>
    /// 256 bytes is provably enough rather than merely generous, which is the only reason a
    /// fixed size is safe here. SCTE-20's count is five bits, so at most 31 entries of 26 bits
    /// each, plus the count itself: 811 bits, or 102 bytes. A payload shorter than this buffer
    /// is copied whole and behaves identically; a longer one is clamped, and the clamp cannot
    /// change the result because the loop stops on the entry count long before
    /// <c>get_bits_left</c> could run out.
    /// </para>
    /// </remarks>
    public readonly byte[] UserDataScratch = new byte[256];

    /// <summary><c>context_initialized</c> - whether a sequence has been set up at all yet.</summary>
    public int ContextInitialized;

    /// <summary><c>avctx-&gt;coded_width</c> - the size the context was last set up for.</summary>
    public int CodedWidth;

    /// <inheritdoc cref="CodedWidth"/>
    public int CodedHeight;

    /// <summary><c>pan_scan</c> - the display rectangle and pan offsets, if the stream sends them.</summary>
    public AvPanScan PanScan;

    /// <summary><c>aspect_ratio_info</c>.</summary>
    public uint AspectRatioInfo;

    /// <summary><c>save_progressive_seq</c>.</summary>
    public int SaveProgressiveSeq;

    /// <summary><c>save_chroma_format</c>.</summary>
    public int SaveChromaFormat;

    /// <summary><c>frame_rate_ext</c> - the MPEG-2 frame rate modifier.</summary>
    public AvRational FrameRateExt;

    /// <summary><c>frame_rate_index</c>.</summary>
    public uint FrameRateIndex;

    /// <summary><c>sync</c> - set once a GOP, sequence or key frame has been seen.</summary>
    public int Sync;

    /// <summary><c>closed_gop</c>.</summary>
    public int ClosedGop;

    /// <summary><c>tmpgexs</c>.</summary>
    public int Tmpgexs;

    /// <summary><c>first_slice</c>.</summary>
    public int FirstSlice;

    /// <summary><c>extradata_decoded</c>.</summary>
    public int ExtradataDecoded;

    /// <summary><c>vbv_delay</c>.</summary>
    public int VbvDelay;

    /// <summary><c>bit_rate</c>.</summary>
    public long BitRate;

    /// <summary><c>timecode_frame_start</c> - the GOP's timecode, in non-drop-frame form.</summary>
    public long TimecodeFrameStart;
}

/// <summary>The MPEG-1/2 decoder (<c>mpeg12dec.c</c>).</summary>
public static partial class Mpeg12Dec
{
    /// <summary><c>MAX_INDEX</c> - the last coefficient position in a block.</summary>
    private const int MaxIndex = 63;

    /// <summary><c>A53_MAX_CC_COUNT</c>.</summary>
    public const int A53MaxCcCount = 2000;

    /// <summary>
    /// <c>mpeg1_decode_sequence</c> - the sequence header, which sets the picture size and the
    /// default quantiser matrices.
    /// </summary>
    /// <remarks>
    /// Everything it sets describes MPEG-1. A stream that is really MPEG-2 follows this with a
    /// sequence extension that overrides the parts that differ, which is why the MPEG-2
    /// defaults are written here as if the stream were MPEG-1 - FFmpeg's comment at this point
    /// says the same.
    /// </remarks>
    public static bool Mpeg1DecodeSequence(Mpeg1Context s1, byte[] buf, int offset, int length)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        var gb = default(GetBitContext);
        gb.InitGetBits8(buf, offset, length);

        int width = (int)gb.GetBits(12);
        int height = (int)gb.GetBits(12);
        if (width == 0 || height == 0) return false;

        s1.AspectRatioInfo = gb.GetBits(4);
        s1.FrameRateIndex = gb.GetBits(4);
        if (s1.FrameRateIndex == 0 || s1.FrameRateIndex > 13)
            s1.FrameRateIndex = 1;

        s1.BitRate = (long)gb.GetBits(18) * 400;
        if (!gb.CheckMarker()) return false;

        gb.SkipBits(10);                            // vbv buffer size
        gb.SkipBits(1);                             // constrained parameters flag

        ReadOnlySpan<byte> perm = IdctDsp.IdentityPermutation;

        if (gb.GetBits1() != 0)
        {
            if (!LoadMatrix(ref gb, c.ChromaIntraMatrix, c.IntraMatrix, perm, intra: true))
                return false;
        }
        else
        {
            for (int i = 0; i < 64; i++)
            {
                int j = perm[i];
                ushort v = Mpeg12Data.Mpeg1DefaultIntraMatrix[i];
                c.IntraMatrix[j] = v;
                c.ChromaIntraMatrix[j] = v;
            }
        }

        if (gb.GetBits1() != 0)
        {
            if (!LoadMatrix(ref gb, c.ChromaInterMatrix, c.InterMatrix, perm, intra: false))
                return false;
        }
        else
        {
            for (int i = 0; i < 64; i++)
            {
                int j = perm[i];
                ushort v = Mpeg12Data.Mpeg1DefaultNonIntraMatrix[i];
                c.InterMatrix[j] = v;
                c.ChromaInterMatrix[j] = v;
            }
        }

        if (gb.ShowBits(23) != 0) return false;     // the header should end here

        c.Width = width;
        c.Height = height;

        // MPEG-1 defaults, which a following sequence extension overrides for MPEG-2.
        c.ProgressiveSequence = 1;
        c.ProgressiveFrame = 1;
        c.PictureStructure = PictFrame;
        c.FirstField = 0;
        c.FramePredFrameDct = 1;
        c.ChromaFormat = MpegVideoConstants.Chroma420;

        // The default scan, until a picture coding extension says otherwise.
        IdctDsp.PermuteScantable(c.IntraScantable.Permutated, IdctDsp.ZigzagDirect, perm);

        return true;
    }

    /// <summary>
    /// <c>skip_1stop_8data_bits</c> - skips the optional extra slice information, which is a
    /// run of bytes each introduced by a set bit.
    /// </summary>
    private static bool Skip1Stop8DataBits(ref GetBitContext gb)
    {
        if (gb.GetBitsLeft() <= 0) return false;

        while (gb.GetBits1() != 0)
        {
            if (gb.GetBitsLeft() <= 8) return false;
            gb.SkipBits(8);
        }
        return true;
    }

    /// <summary>Outcome of <see cref="MpegDecodeSlice"/>.</summary>
    public enum SliceResult
    {
        /// <summary>The slice decoded to its end.</summary>
        Ok,
        /// <summary>The bitstream was corrupt; the row is abandoned.</summary>
        Error,
    }

    /// <summary>
    /// <c>mpeg_decode_slice</c> - decodes one slice, which is a run of macroblocks within a
    /// single row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A slice carries its own quantiser scale and its own DC predictors, which is what makes
    /// it a resynchronisation point: a decoder that loses one can resume at the next without
    /// the picture's remaining rows being wrong.
    /// </para>
    /// <para>
    /// The macroblock address increment at the head of the slice says where in the row it
    /// starts. Codes of 33 and above are escapes and stuffing rather than increments, which is
    /// why the loop continues past them instead of treating them as a position.
    /// </para>
    /// </remarks>
    /// <param name="s">The slice context, already pointed at the picture.</param>
    /// <param name="mbY">The macroblock row this slice belongs to.</param>
    /// <param name="buf">The slice payload, after the start code.</param>
    /// <param name="isMpeg2">True for MPEG-2, which changes how blocks are read.</param>
    /// <param name="consumed">Bytes of <paramref name="buf"/> the slice used.</param>
    public static SliceResult MpegDecodeSlice(
        Mpeg12SliceContext s, int mbY, byte[] buf, int offset, int length, bool isMpeg2,
        out int consumed)
    {
        ref MpegEncContext c = ref s.C;
        consumed = 0;

        c.ResyncMbX = -1;
        c.ResyncMbY = -1;

        if (mbY >= c.MbHeight) return SliceResult.Error;

        s.Gb.InitGetBits8(buf, offset, length);

        // A tall MPEG-2 picture carries three extra bits of slice vertical position.
        if (isMpeg2 && c.MbHeight > 2800 / 16)
            s.Gb.SkipBits(3);

        c.InterlacedDct = 0;
        c.Qscale = MpegGetQscale(ref s.Gb, c.QScaleType);
        if (c.Qscale == 0) return SliceResult.Error;

        if (!Skip1Stop8DataBits(ref s.Gb)) return SliceResult.Error;

        c.MbX = 0;

        while (s.Gb.GetBitsLeft() > 0)
        {
            int code = s.Gb.GetVlc2(Mpeg12.MbIncrVlc, Mpeg12Vlc.MbIncrVlcBits, 2);
            if (code < 0) return SliceResult.Error;

            if (code >= 33)
            {
                // 33 is the escape that adds a full 33; anything else here is stuffing.
                if (code == 33) c.MbX += 33;
            }
            else
            {
                c.MbX += code;
                break;
            }
        }

        if ((uint)c.MbX >= (uint)c.MbWidth) return SliceResult.Error;

        c.ResyncMbX = c.MbX;
        c.ResyncMbY = c.MbY = mbY;
        MpegVideoDec.InitBlockIndex(ref c);

        // The DC predictors and motion predictors reset at every slice, which is what lets a
        // decoder resynchronise here.
        s.LastDc[0] = s.LastDc[1] = s.LastDc[2] = 128 << c.IntraDcPrecision;
        for (int i = 0; i < 8; i++) c.LastMv[i] = 0;

        int fieldPic = c.PictureStructure != PictFrame ? 1 : 0;
        int mbSkipRun = 0;

        while (true)
        {
            if (!MpegDecodeMb(s, ref mbSkipRun, isMpeg2)) return SliceResult.Error;

            // The destinations advance before reconstruction, which is why InitBlockIndex
            // leaves them one macroblock to the left.
            c.Dest[0] += 16;
            c.Dest[1] += 16 >> c.ChromaXShift;
            c.Dest[2] += 16 >> c.ChromaXShift;

            MpegVideoDec.MpvReconstructMb(ref c, s.Block);

            if (++c.MbX >= c.MbWidth)
            {
                c.MbX = 0;
                c.MbY += 1 << fieldPic;

                if (c.MbY >= c.MbHeight)
                {
                    // The picture is complete; whatever follows belongs to the next one.
                    break;
                }

                MpegVideoDec.InitBlockIndex(ref c);
            }

            if (mbSkipRun == -1)
            {
                // Read the next address increment, which says how many macroblocks to skip.
                mbSkipRun = 0;
                bool endOfSlice = false;

                while (true)
                {
                    int code = s.Gb.GetVlc2(Mpeg12.MbIncrVlc, Mpeg12Vlc.MbIncrVlcBits, 2);
                    if (code < 0) return SliceResult.Error;

                    if (code >= 33)
                    {
                        if (code == 33)
                        {
                            mbSkipRun += 33;
                        }
                        else if (code == 35)
                        {
                            // End of slice. It may only appear where nothing is pending.
                            if (mbSkipRun != 0 || s.Gb.ShowBits(15) != 0) return SliceResult.Error;
                            endOfSlice = true;
                            break;
                        }
                        // Otherwise stuffing.
                    }
                    else
                    {
                        mbSkipRun += code;
                        break;
                    }
                }

                if (endOfSlice) break;

                if (mbSkipRun != 0)
                {
                    // A skip in an I picture is meaningless: there is no prediction to repeat.
                    if (c.PictType == AvPictureType.I) return SliceResult.Error;

                    c.MbIntra = 0;
                    for (int i = 0; i < 12; i++) c.BlockLastIndex[i] = -1;
                    s.LastDc[0] = s.LastDc[1] = s.LastDc[2] = 128 << c.IntraDcPrecision;

                    c.MvType = c.PictureStructure == PictFrame
                        ? MpegVideoConstants.MvType16X16
                        : MpegVideoConstants.MvTypeField;

                    if (c.PictType == AvPictureType.P)
                    {
                        // A skipped P macroblock has an implied zero vector.
                        c.MvDir = MpegVideoConstants.MvDirForward;
                        c.Mv[MvIndex(0, 0, 0)] = 0;
                        c.Mv[MvIndex(0, 0, 1)] = 0;
                        c.LastMv[LastMvIndex(0, 0, 0)] = 0;
                        c.LastMv[LastMvIndex(0, 0, 1)] = 0;
                        c.LastMv[LastMvIndex(0, 1, 0)] = 0;
                        c.LastMv[LastMvIndex(0, 1, 1)] = 0;
                        c.FieldSelect[FieldSelectIndex(0, 0)] = (c.PictureStructure - 1) & 1;
                    }
                    else
                    {
                        // A skipped B macroblock repeats the previous vectors and directions.
                        c.Mv[MvIndex(0, 0, 0)] = c.LastMv[LastMvIndex(0, 0, 0)];
                        c.Mv[MvIndex(0, 0, 1)] = c.LastMv[LastMvIndex(0, 0, 1)];
                        c.Mv[MvIndex(1, 0, 0)] = c.LastMv[LastMvIndex(1, 0, 0)];
                        c.Mv[MvIndex(1, 0, 1)] = c.LastMv[LastMvIndex(1, 0, 1)];
                        c.FieldSelect[FieldSelectIndex(0, 0)] = (c.PictureStructure - 1) & 1;
                        c.FieldSelect[FieldSelectIndex(1, 0)] = (c.PictureStructure - 1) & 1;
                    }
                }
            }
        }

        if (s.Gb.GetBitsLeft() < 0) return SliceResult.Error;

        consumed = (s.Gb.GetBitsCount() - 1) / 8;
        return SliceResult.Ok;
    }

    /// <summary>
    /// <c>mpeg1_decode_picture</c> - the picture header: type, and the motion ranges a
    /// predicted picture will use.
    /// </summary>
    /// <remarks>
    /// An f_code of zero is illegal but tolerated, because real streams contain it; FFmpeg
    /// bumps it to one rather than refusing the picture, and only rejects it when the caller
    /// asked for strict bitstream checking.
    /// </remarks>
    public static bool Mpeg1DecodePicture(Mpeg1Context s1, byte[] buf, int offset, int length)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        var gb = default(GetBitContext);
        gb.InitGetBits8(buf, offset, length);

        gb.SkipBits(10);                            // temporal reference
        int pictType = (int)gb.GetBits(3);
        if (pictType is 0 or > 3) return false;
        c.PictType = (AvPictureType)pictType;

        s1.VbvDelay = (int)gb.GetBits(16);

        if (c.PictType is AvPictureType.P or AvPictureType.B)
        {
            c.FullPel[0] = (int)gb.GetBits1();
            int fCode = (int)gb.GetBits(3);
            if (fCode == 0) fCode = 1;
            c.MpegFCode[FCodeIndex(0, 0)] = fCode;
            c.MpegFCode[FCodeIndex(0, 1)] = fCode;
        }

        if (c.PictType == AvPictureType.B)
        {
            c.FullPel[1] = (int)gb.GetBits1();
            int fCode = (int)gb.GetBits(3);
            if (fCode == 0) fCode = 1;
            c.MpegFCode[FCodeIndex(1, 0)] = fCode;
            c.MpegFCode[FCodeIndex(1, 1)] = fCode;
        }

        return true;
    }

    /// <summary>
    /// <c>ff_reverse[256]</c> (<c>libavutil/reverse.c</c>) - each byte with its bits in the
    /// opposite order. SCTE-20 sends its caption bytes backwards and this is what rights them.
    /// </summary>
    public static ReadOnlySpan<byte> FfReverse =>
    [
        0x00,0x80,0x40,0xC0,0x20,0xA0,0x60,0xE0,0x10,0x90,0x50,0xD0,0x30,0xB0,0x70,0xF0,
        0x08,0x88,0x48,0xC8,0x28,0xA8,0x68,0xE8,0x18,0x98,0x58,0xD8,0x38,0xB8,0x78,0xF8,
        0x04,0x84,0x44,0xC4,0x24,0xA4,0x64,0xE4,0x14,0x94,0x54,0xD4,0x34,0xB4,0x74,0xF4,
        0x0C,0x8C,0x4C,0xCC,0x2C,0xAC,0x6C,0xEC,0x1C,0x9C,0x5C,0xDC,0x3C,0xBC,0x7C,0xFC,
        0x02,0x82,0x42,0xC2,0x22,0xA2,0x62,0xE2,0x12,0x92,0x52,0xD2,0x32,0xB2,0x72,0xF2,
        0x0A,0x8A,0x4A,0xCA,0x2A,0xAA,0x6A,0xEA,0x1A,0x9A,0x5A,0xDA,0x3A,0xBA,0x7A,0xFA,
        0x06,0x86,0x46,0xC6,0x26,0xA6,0x66,0xE6,0x16,0x96,0x56,0xD6,0x36,0xB6,0x76,0xF6,
        0x0E,0x8E,0x4E,0xCE,0x2E,0xAE,0x6E,0xEE,0x1E,0x9E,0x5E,0xDE,0x3E,0xBE,0x7E,0xFE,
        0x01,0x81,0x41,0xC1,0x21,0xA1,0x61,0xE1,0x11,0x91,0x51,0xD1,0x31,0xB1,0x71,0xF1,
        0x09,0x89,0x49,0xC9,0x29,0xA9,0x69,0xE9,0x19,0x99,0x59,0xD9,0x39,0xB9,0x79,0xF9,
        0x05,0x85,0x45,0xC5,0x25,0xA5,0x65,0xE5,0x15,0x95,0x55,0xD5,0x35,0xB5,0x75,0xF5,
        0x0D,0x8D,0x4D,0xCD,0x2D,0xAD,0x6D,0xED,0x1D,0x9D,0x5D,0xDD,0x3D,0xBD,0x7D,0xFD,
        0x03,0x83,0x43,0xC3,0x23,0xA3,0x63,0xE3,0x13,0x93,0x53,0xD3,0x33,0xB3,0x73,0xF3,
        0x0B,0x8B,0x4B,0xCB,0x2B,0xAB,0x6B,0xEB,0x1B,0x9B,0x5B,0xDB,0x3B,0xBB,0x7B,0xFB,
        0x07,0x87,0x47,0xC7,0x27,0xA7,0x67,0xE7,0x17,0x97,0x57,0xD7,0x37,0xB7,0x77,0xF7,
        0x0F,0x8F,0x4F,0xCF,0x2F,0xAF,0x6F,0xEF,0x1F,0x9F,0x5F,0xDF,0x3F,0xBF,0x7F,0xFF,
    ];

    /// <summary>
    /// <c>mpeg_set_cc_format</c> - records which caption carriage this stream uses, the first
    /// time one is recognised.
    /// </summary>
    /// <remarks>
    /// It latches. A stream may contain byte sequences that look like a second format, and
    /// switching halfway would interleave two incompatible caption streams into one buffer, so
    /// once a format is chosen every other format's branch stops matching.
    /// </remarks>
    public static void MpegSetCcFormat(Mpeg1Context s1, Mpeg2ClosedCaptionsFormat format)
    {
        if (format == Mpeg2ClosedCaptionsFormat.Auto)
            throw new ArgumentOutOfRangeException(nameof(format),
                "Auto is the absence of a format, not one that can be set.");

        if (s1.CcFormat == Mpeg2ClosedCaptionsFormat.Auto)
            s1.CcFormat = format;
    }

    /// <summary>
    /// Appends <paramref name="count"/> caption triplets' worth of space to the picture's
    /// buffer and returns where to write them, or an empty span if that would overflow.
    /// </summary>
    /// <remarks>
    /// This stands in for <c>av_buffer_realloc(&amp;s1->a53_buf_ref, new_size)</c>. The C grows a
    /// reference-counted buffer per picture and hands it to the frame; here the buffer is
    /// allocated once at its maximum, <c>3 * A53_MAX_CC_COUNT</c>, and refilled, because a
    /// per-picture allocation is a defect in this project. The cap is the C's, and the C's
    /// behaviour on exceeding it - return without appending - is kept.
    /// </remarks>
    private static Span<byte> A53Append(Mpeg1Context s1, int count, int bytesEach)
    {
        int oldSize = s1.A53Size;
        long newSize = oldSize + (long)count * bytesEach;

        if (newSize > 3 * A53MaxCcCount) return default;

        s1.A53Size = (int)newSize;
        return s1.A53Buf.AsSpan(oldSize, (int)newSize - oldSize);
    }

    /// <summary>
    /// <c>mpeg_decode_a53_cc</c> - recognises four incompatible closed-caption carriages in
    /// user data and normalises all of them to A/53 Part 4 triplets.
    /// </summary>
    /// <returns>True if the user data was captions, whichever format.</returns>
    /// <remarks>
    /// The four are tried in order and each is gated on the latched format, so a stream commits
    /// to one. Three of them are transformed into A/53's shape rather than passed through:
    /// SCTE-20 carries its two caption bytes bit-reversed and its field number in a different
    /// encoding; DVD packs two fields into one six-byte block and lies about the count in its
    /// own header, so the count is obtained by walking the blocks; Dish Network sends two or
    /// four bytes with a repeat rule for characters below 32.
    /// </remarks>
    public static bool MpegDecodeA53Cc(Mpeg1Context s1, ReadOnlySpan<byte> p)
    {
        int bufSize = p.Length;

        if ((s1.CcFormat == Mpeg2ClosedCaptionsFormat.Auto ||
             s1.CcFormat == Mpeg2ClosedCaptionsFormat.A53Part4) &&
            bufSize >= 6 &&
            p[0] == (byte)'G' && p[1] == (byte)'A' && p[2] == (byte)'9' && p[3] == (byte)'4' &&
            p[4] == 3 && (p[5] & 0x40) != 0)
        {
            int ccCount = p[5] & 0x1f;
            if (ccCount > 0 && bufSize >= 7 + ccCount * 3)
            {
                Span<byte> cap = A53Append(s1, ccCount, 3);
                if (!cap.IsEmpty)
                    p.Slice(7, ccCount * 3).CopyTo(cap);

                MpegSetCcFormat(s1, Mpeg2ClosedCaptionsFormat.A53Part4);
            }
            return true;
        }

        if ((s1.CcFormat == Mpeg2ClosedCaptionsFormat.Auto ||
             s1.CcFormat == Mpeg2ClosedCaptionsFormat.Scte20) &&
            bufSize >= 2 && p[0] == 0x03 && (p[1] & 0x7f) == 0x01)
        {
            // init_get_bits8(&gb, p + 2, buf_size - 2), without the allocation a span
            // cannot avoid. See UserDataScratch for why 256 bytes is exact and not a guess.
            int scratchLength = Math.Min(bufSize - 2, s1.UserDataScratch.Length);
            p.Slice(2, scratchLength).CopyTo(s1.UserDataScratch);

            var gb = default(GetBitContext);
            gb.InitGetBits8(s1.UserDataScratch, scratchLength);

            int ccCount = (int)gb.GetBits(5);
            if (ccCount > 0)
            {
                Span<byte> cap = A53Append(s1, ccCount, 3);
                if (!cap.IsEmpty)
                {
                    cap.Clear();
                    int at = 0;
                    for (int i = 0; i < ccCount && gb.GetBitsLeft() >= 26; i++)
                    {
                        gb.SkipBits(2);                     // priority
                        int field = (int)gb.GetBits(2);
                        gb.SkipBits(5);                     // line offset
                        int cc1 = (int)gb.GetBits(8);
                        int cc2 = (int)gb.GetBits(8);
                        gb.SkipBits(1);                     // marker

                        if (field == 0)
                        {
                            // Forbidden; the C writes three zero bytes rather than dropping the
                            // entry, so the triplet count stays in step with the header's.
                            cap[at] = cap[at + 1] = cap[at + 2] = 0x00;
                        }
                        else
                        {
                            int f = field == 2 ? 1 : 0;
                            if (s1.Slice.C.TopFieldFirst == 0) f = f == 0 ? 1 : 0;
                            cap[at] = (byte)(0x04 | f);
                            cap[at + 1] = FfReverse[cc1];
                            cap[at + 2] = FfReverse[cc2];
                        }
                        at += 3;
                    }
                }

                MpegSetCcFormat(s1, Mpeg2ClosedCaptionsFormat.Scte20);
            }
            return true;
        }

        if ((s1.CcFormat == Mpeg2ClosedCaptionsFormat.Auto ||
             s1.CcFormat == Mpeg2ClosedCaptionsFormat.Dvd) &&
            bufSize >= 11 &&
            p[0] == (byte)'C' && p[1] == (byte)'C' && p[2] == 0x01 && p[3] == 0xf8)
        {
            // The block count in the header is often wrong, so the blocks are counted. Each is
            // six bytes and begins with a filler byte whose low bit is the field.
            int ccCount = 0;
            for (int i = 5; i + 6 <= bufSize && (p[i] & 0xfe) == 0xfe; i += 6)
                ccCount++;

            if (ccCount > 0)
            {
                Span<byte> cap = A53Append(s1, ccCount, 6);
                if (!cap.IsEmpty)
                {
                    int field1 = (p[4] & 0x80) != 0 ? 1 : 0;
                    ReadOnlySpan<byte> q = p[5..];
                    int at = 0;
                    for (int i = 0; i < ccCount; i++)
                    {
                        cap[at] = (byte)(q[0] == 0xff && field1 != 0 ? 0xfc : 0xfd);
                        cap[at + 1] = q[1];
                        cap[at + 2] = q[2];
                        cap[at + 3] = (byte)(q[3] == 0xff && field1 == 0 ? 0xfc : 0xfd);
                        cap[at + 4] = q[4];
                        cap[at + 5] = q[5];
                        at += 6;
                        q = q[6..];
                    }
                }

                MpegSetCcFormat(s1, Mpeg2ClosedCaptionsFormat.Dvd);
            }
            return true;
        }

        if ((s1.CcFormat == Mpeg2ClosedCaptionsFormat.Auto ||
             s1.CcFormat == Mpeg2ClosedCaptionsFormat.Dish) &&
            bufSize >= 12 && p[0] == 0x05 && p[1] == 0x02)
        {
            // 0xf8 marks a caption block, 0x04 says it is valid, 0x00 says line 21 field 1.
            const byte CcHeader = 0xf8 | 0x04 | 0x00;

            Span<byte> ccData = stackalloc byte[4];
            ccData.Clear();
            int ccCount = 0;

            int ccType = p[7];
            ReadOnlySpan<byte> q = p[8..];

            if (ccType == 0x05 && q.Length >= 7)
            {
                ccType = q[6];
                q = q[7..];
            }

            if (ccType == 0x02 && q.Length >= 4)
            {
                // Two-byte caption, which may be repeated.
                ccCount = 1;
                ccData[0] = q[1];
                ccData[1] = q[2];
                ccType = q[3];

                // Only repeat when the next type flag is 0x04 and the characters are
                // repeatable - below 32 with the parity bit stripped.
                if (ccType == 0x04 && (ccData[0] & 0x7f) < 32)
                {
                    ccCount = 2;
                    ccData[2] = ccData[0];
                    ccData[3] = ccData[1];
                }
            }
            else if (ccType == 0x04 && q.Length >= 5)
            {
                // Four-byte caption, never repeated.
                ccCount = 2;
                ccData[0] = q[1];
                ccData[1] = q[2];
                ccData[2] = q[3];
                ccData[3] = q[4];
            }

            if (ccCount > 0)
            {
                Span<byte> cap = A53Append(s1, ccCount, 3);
                if (!cap.IsEmpty)
                {
                    cap[0] = CcHeader;
                    cap[1] = ccData[0];
                    cap[2] = ccData[1];
                    if (ccCount == 2)
                    {
                        cap[3] = CcHeader;
                        cap[4] = ccData[2];
                        cap[5] = ccData[3];
                    }
                }

                MpegSetCcFormat(s1, Mpeg2ClosedCaptionsFormat.Dish);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>mpeg_decode_user_data</c> - the user data start code carries no samples, but it is
    /// where captions, the DTG active format description and stereoscopic signalling live.
    /// </summary>
    /// <remarks>
    /// The TMPGEnc marker is checked first and separately, because unlike the rest it is not a
    /// prefix: the C scans the first twenty bytes for it. It matters because a stream written by
    /// that encoder needs an intra DC precision the stream itself does not state.
    /// </remarks>
    public static void MpegDecodeUserData(Mpeg1Context s1, ReadOnlySpan<byte> p)
    {
        int bufSize = p.Length;

        if (bufSize > 29)
        {
            ReadOnlySpan<byte> marker = "\0TMPGEXS\0"u8;
            for (int i = 0; i < 20; i++)
                if (p.Slice(i, 9).SequenceEqual(marker))
                    s1.Tmpgexs = 1;
        }

        // The DTG active format information.
        if (bufSize >= 5 &&
            p[0] == (byte)'D' && p[1] == (byte)'T' && p[2] == (byte)'G' && p[3] == (byte)'1')
        {
            int flags = p[4];
            ReadOnlySpan<byte> q = p[5..];

            if ((flags & 0x80) != 0)
            {
                if (q.Length < 2) return;
                q = q[2..];                             // skip event id
            }
            if ((flags & 0x40) != 0)
            {
                if (q.Length < 1) return;
                s1.HasAfd = 1;
                s1.Afd = (byte)(q[0] & 0x0f);
            }
            return;
        }

        if (bufSize >= 6 &&
            p[0] == (byte)'J' && p[1] == (byte)'P' && p[2] == (byte)'3' && p[3] == (byte)'D' &&
            p[4] == 0x03)                               // S3D_video_format_length
        {
            // The 0x7F mask ignores the reserved bit.
            int formatType = p[5] & 0x7F;

            switch (formatType)
            {
                case 0x03: s1.HasStereo3d = 1; s1.Stereo3dType = AvStereo3dType.SideBySide; break;
                case 0x04: s1.HasStereo3d = 1; s1.Stereo3dType = AvStereo3dType.TopBottom; break;
                case 0x08: s1.HasStereo3d = 1; s1.Stereo3dType = AvStereo3dType.TwoDimensional; break;
                case 0x23: s1.HasStereo3d = 1; s1.Stereo3dType = AvStereo3dType.SideBySideQuincunx; break;
                default: break;                         // a type this decoder does not signal
            }
            return;
        }

        MpegDecodeA53Cc(s1, p);
    }

    /// <summary>
    /// <c>mpeg_decode_sequence_display_extension</c> - the display rectangle and the colour
    /// description.
    /// </summary>
    /// <remarks>
    /// The rectangle is the part of the coded picture a display should show, in units of 16
    /// samples, and it is what <c>mpeg_decode_postinit</c> divides the coded aspect ratio by.
    /// The three remaining bits of the extension are zero padding and are not read.
    /// </remarks>
    public static void MpegDecodeSequenceDisplayExtension(Mpeg1Context s1, ref GetBitContext gb)
    {
        gb.SkipBits(3);                             // video format

        int colorDescription = (int)gb.GetBits1();
        if (colorDescription != 0)
        {
            s1.Avctx.ColorPrimaries = (int)gb.GetBits(8);
            s1.Avctx.ColorTrc = (int)gb.GetBits(8);
            s1.Avctx.Colorspace = (int)gb.GetBits(8);
        }

        int w = (int)gb.GetBits(14);
        gb.SkipBits(1);                             // marker
        int h = (int)gb.GetBits(14);

        s1.PanScan.Width = 16 * w;
        s1.PanScan.Height = 16 * h;
    }

    /// <summary>
    /// <c>mpeg_decode_picture_display_extension</c> - where to centre the display rectangle.
    /// </summary>
    /// <remarks>
    /// One offset per displayed frame, and how many that is depends on field repetition: a
    /// progressive sequence can show one coded picture up to three times, and an interlaced one
    /// up to three fields. Reading the wrong count here does not corrupt the picture, it
    /// desynchronises the rest of the extension.
    /// </remarks>
    public static void MpegDecodePictureDisplayExtension(Mpeg1Context s1, ref GetBitContext gb)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        int nofco = 1;
        if (c.ProgressiveSequence != 0)
        {
            if (c.RepeatFirstField != 0)
            {
                nofco++;
                if (c.TopFieldFirst != 0) nofco++;
            }
        }
        else if (c.PictureStructure == MpegVideoConstants.PictFrame)
        {
            nofco++;
            if (c.RepeatFirstField != 0) nofco++;
        }

        for (int i = 0; i < nofco; i++)
        {
            s1.PanScan.Position[i * 2] = (short)gb.GetSBits(16);
            gb.SkipBits(1);                         // marker
            s1.PanScan.Position[i * 2 + 1] = (short)gb.GetSBits(16);
            gb.SkipBits(1);                         // marker
        }
    }

    /// <summary>
    /// <c>av_image_check_sar</c>, reduced to the case this decoder reaches.
    /// </summary>
    /// <remarks>
    /// The C calls <c>av_rescale_rnd</c> with <c>AV_ROUND_ZERO</c>. By the time the ratio gets
    /// here the early returns have established that the numerator and denominator are both
    /// positive and unequal, and the dimensions are positive, so that call is a truncating
    /// division of positive operands and nothing else - av_rescale_rnd's overflow handling and
    /// rounding modes are unreachable. The whole function asks one question: does scaling the
    /// picture by this ratio leave it with a dimension of at least one sample.
    /// </remarks>
    private static bool CheckSar(int w, int h, AvRational sar)
    {
        if (sar.Den <= 0 || sar.Num < 0) return false;
        if (sar.Num == 0 || sar.Num == sar.Den) return true;

        long scaledDim = sar.Num < sar.Den
            ? (long)w * sar.Num / sar.Den
            : (long)h * sar.Den / sar.Num;

        return scaledDim > 0;
    }

    /// <summary>
    /// <c>mpeg_decode_postinit</c> - turns the parsed sequence headers into the sample aspect
    /// ratio, frame rate, chroma siting and reordering delay a player needs.
    /// </summary>
    /// <remarks>
    /// Two things in here are worth reading before changing anything.
    ///
    /// The MPEG-2 aspect ratio deliberately contradicts the standard, and FFmpeg says so in a
    /// comment naming the files that forced it. By the standard the aspect code is a DISPLAY
    /// aspect ratio to be divided by the display rectangle; in practice encoders disagree about
    /// which rectangle they meant, so FFmpeg computes the display aspect both ways and keeps the
    /// one that lands on 4:3 or 16:9. Anything else is treated as the coded picture's aspect.
    /// This is a guess, and it is the same guess FFmpeg makes, which is the only thing that
    /// matters for a port.
    ///
    /// MPEG-1's aspect table is a table of floats, and FFmpeg feeds it through <c>av_d2q</c> and
    /// then INVERTS the result - the table holds sample aspect ratios the wrong way up.
    ///
    /// The context reinitialisation the C does at the end - ff_set_dimensions, ff_mpv_common_end
    /// and ff_mpv_common_init - is the decoder's own InitialiseDimensions here, called from the
    /// same place. This function is the rest.
    /// </remarks>
    public static void MpegDecodePostinit(Mpeg1Context s1, bool isMpeg2)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        if (!isMpeg2)
        {
            // The table is indexed by the four-bit code, so it cannot overrun, and entry 0 is
            // 0.0 which av_d2q turns into 0/1 - inverted to 1/0, which CheckSar then rejects.
            AvRational aspectInv = AvRationalMath.FromDouble(
                Mpeg12Data.Mpeg1Aspect[(int)s1.AspectRatioInfo], 255);
            s1.Avctx.SampleAspectRatio = new AvRational(aspectInv.Den, aspectInv.Num);
        }
        else if (s1.AspectRatioInfo > 1)
        {
            AvRational coded = Mpeg12Data.Mpeg2Aspect[(int)s1.AspectRatioInfo];
            AvRational display = new(s1.PanScan.Width, s1.PanScan.Height);

            AvRational dar = AvRationalMath.Mul(
                AvRationalMath.Div(coded, display), new AvRational(c.Width, c.Height));

            // See the remarks: no display rectangle, or a display aspect that is neither 4:3
            // nor 16:9, means the encoder did not mean what the standard says it meant.
            bool trustCodedSize =
                s1.PanScan.Width == 0 || s1.PanScan.Height == 0 ||
                (AvRationalMath.Cmp(dar, new AvRational(4, 3)) != 0 &&
                 AvRationalMath.Cmp(dar, new AvRational(16, 9)) != 0);

            s1.Avctx.SampleAspectRatio = AvRationalMath.Div(
                coded, trustCodedSize ? new AvRational(c.Width, c.Height) : display);
        }
        else
        {
            s1.Avctx.SampleAspectRatio = Mpeg12Data.Mpeg2Aspect[(int)s1.AspectRatioInfo];
        }

        if (!CheckSar(c.Width, c.Height, s1.Avctx.SampleAspectRatio))
            s1.Avctx.SampleAspectRatio = new AvRational(0, 1);

        // Everything below this line is inside the C's reinitialisation guard, and the guard is
        // the reason this function is cheap enough to call on every picture: the aspect ratio
        // above is recomputed each time, as in the C, but the frame rate reduction and the rest
        // run only when the sequence actually changed.
        //
        // The progressive_sequence half of the test is conditional on the height rounding
        // differently to 16 and to 32, because that is the only case where the flag changes the
        // macroblock grid. A 480-line picture rounds to 480 either way and needs no rebuild.
        bool heightRoundsDifferently = ((c.Height + 15) & ~15) != ((c.Height + 31) & ~31);

        bool needsReinit =
            s1.ContextInitialized == 0 ||
            s1.CodedWidth != c.Width ||
            s1.CodedHeight != c.Height ||
            s1.SaveChromaFormat != c.ChromaFormat ||
            (s1.SaveProgressiveSeq != c.ProgressiveSequence && heightRoundsDifferently);

        if (!needsReinit) return;

        s1.ContextInitialized = 1;
        s1.CodedWidth = c.Width;
        s1.CodedHeight = c.Height;

        // 0x3FFFF*400 is the escape value meaning "unknown" in both standards. MPEG-2's field
        // is a ceiling and MPEG-1's is the stream's actual rate, so they land in different
        // places; MPEG-1 additionally accepts the escape when vbv_delay says the rate is real.
        if (isMpeg2 && s1.BitRate != 0 && s1.BitRate != 0x3FFFF * 400L)
        {
            s1.Avctx.RcMaxRate = s1.BitRate;
        }
        else if (!isMpeg2 && s1.BitRate != 0 &&
                 (s1.BitRate != 0x3FFFF * 400L || s1.VbvDelay != 0xFFFF))
        {
            s1.Avctx.BitRate = s1.BitRate;
        }

        s1.SaveProgressiveSeq = c.ProgressiveSequence;
        s1.SaveChromaFormat = c.ChromaFormat;

        // low_delay may be forced, in which case the stream carries B pictures that behave like
        // P pictures and there is no reordering delay to declare.
        s1.Avctx.HasBFrames = c.LowDelay != 0 ? 0 : 1;

        if (!isMpeg2)
        {
            s1.Avctx.Framerate = Mpeg12Data.Mpeg12FrameRateTab[(int)s1.FrameRateIndex];
            s1.Avctx.ChromaSampleLocation = AvChromaLocation.Center;
            return;
        }

        AvRational baseRate = Mpeg12Data.Mpeg12FrameRateTab[(int)s1.FrameRateIndex];
        AvRationalMath.Reduce(
            out int num, out int den,
            (long)baseRate.Num * s1.FrameRateExt.Num,
            (long)baseRate.Den * s1.FrameRateExt.Den,
            1 << 30);
        s1.Avctx.Framerate = new AvRational(num, den);

        s1.Avctx.ChromaSampleLocation = c.ChromaFormat switch
        {
            MpegVideoConstants.Chroma420 => AvChromaLocation.Left,
            MpegVideoConstants.Chroma422 or MpegVideoConstants.Chroma444 => AvChromaLocation.TopLeft,
            _ => throw new InvalidOperationException(
                $"chroma_format {c.ChromaFormat} is not one of the three MPEG-2 defines; "
              + "the sequence extension parser should have made this unreachable."),
        };
    }

    /// <summary>
    /// <c>mpeg_decode_sequence_extension</c> - the extension that makes a stream MPEG-2.
    /// </summary>
    /// <remarks>
    /// The width and height here are extension bits that are OR-ed into the twelve already read
    /// from the sequence header, which is how MPEG-2 reaches resolutions MPEG-1 could not
    /// express.
    /// </remarks>
    public static void MpegDecodeSequenceExtension(Mpeg1Context s1, ref GetBitContext gb)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        gb.SkipBits(1);                             // profile and level escape
        gb.SkipBits(3);                             // profile
        gb.SkipBits(4);                             // level

        c.ProgressiveSequence = (int)gb.GetBits1();
        c.ChromaFormat = (int)gb.GetBits(2);        // 1 = 4:2:0, 2 = 4:2:2, 3 = 4:4:4

        if (c.ChromaFormat == 0)
            c.ChromaFormat = MpegVideoConstants.Chroma420;

        int horizSizeExt = (int)gb.GetBits(2);
        int vertSizeExt = (int)gb.GetBits(2);
        c.Width |= horizSizeExt << 12;
        c.Height |= vertSizeExt << 12;

        int bitRateExt = (int)gb.GetBits(12);
        s1.BitRate += (long)(bitRateExt << 18) * 400L;
        gb.CheckMarker();
        gb.SkipBits(8);                             // vbv buffer size extension

        c.LowDelay = (int)gb.GetBits1();

        s1.FrameRateExt = new AvRational((int)gb.GetBits(2) + 1, (int)gb.GetBits(5) + 1);
    }

    /// <summary>
    /// <c>load_matrix</c> - reads a quantiser matrix, which arrives in scan order and is stored
    /// in raster order.
    /// </summary>
    /// <remarks>
    /// A zero entry is rejected outright: it would divide by zero downstream. An intra matrix
    /// whose first entry is not 8 is forced to 8 rather than refused, because real streams
    /// contain that and FFmpeg carries a comment naming the file which forced the workaround.
    /// </remarks>
    public static bool LoadMatrix(
        ref GetBitContext gb, Span<ushort> matrix0, Span<ushort> matrix1,
        ReadOnlySpan<byte> permutation, bool intra)
    {
        for (int i = 0; i < 64; i++)
        {
            int j = permutation[IdctDsp.ZigzagDirect[i]];
            int v = (int)gb.GetBits(8);

            if (v == 0) return false;               // a damaged matrix
            if (intra && i == 0 && v != 8) v = 8;

            matrix0[j] = (ushort)v;
            if (!matrix1.IsEmpty) matrix1[j] = (ushort)v;
        }
        return true;
    }

    /// <summary><c>mpeg_decode_quant_matrix_extension</c>.</summary>
    /// <remarks>
    /// Four optional matrices in a fixed order: intra, inter, chroma intra, chroma inter. The
    /// first two also overwrite the luma matrices, because a stream that sends only those means
    /// them to apply to both.
    /// </remarks>
    public static void MpegDecodeQuantMatrixExtension(Mpeg12SliceContext s, ref GetBitContext gb)
    {
        ref MpegEncContext c = ref s.C;
        ReadOnlySpan<byte> perm = IdctDsp.IdentityPermutation;

        if (gb.GetBits1() != 0)
            LoadMatrix(ref gb, c.ChromaIntraMatrix, c.IntraMatrix, perm, intra: true);
        if (gb.GetBits1() != 0)
            LoadMatrix(ref gb, c.ChromaInterMatrix, c.InterMatrix, perm, intra: false);
        if (gb.GetBits1() != 0)
            LoadMatrix(ref gb, c.ChromaIntraMatrix, Span<ushort>.Empty, perm, intra: true);
        if (gb.GetBits1() != 0)
            LoadMatrix(ref gb, c.ChromaInterMatrix, Span<ushort>.Empty, perm, intra: false);
    }

    /// <summary>
    /// <c>mpeg_decode_picture_coding_extension</c> - the per-picture MPEG-2 switches, including
    /// the ones that change how every later block is read.
    /// </summary>
    /// <remarks>
    /// <c>alternate_scan</c> is the one with the longest reach: it selects a different
    /// coefficient scan, so the scan table is rebuilt here and every block in the picture is
    /// read through it.
    /// </remarks>
    public static void MpegDecodePictureCodingExtension(Mpeg1Context s1, ref GetBitContext gb)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        c.FullPel[0] = 0;
        c.FullPel[1] = 0;

        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                int f = (int)gb.GetBits(4);
                c.MpegFCode[FCodeIndex(i, j)] = f == 0 ? 1 : f;
            }

        c.IntraDcPrecision = (int)gb.GetBits(2);
        c.PictureStructure = (int)gb.GetBits(2);
        c.TopFieldFirst = (int)gb.GetBits1();
        c.FramePredFrameDct = (int)gb.GetBits1();
        c.ConcealmentMotionVectors = (int)gb.GetBits1();
        c.QScaleType = (int)gb.GetBits1();
        c.IntraVlcFormat = (int)gb.GetBits1();
        c.AlternateScan = (int)gb.GetBits1();
        c.RepeatFirstField = (int)gb.GetBits1();
        c.Chroma420Type = (int)gb.GetBits1();
        c.ProgressiveFrame = (int)gb.GetBits1();

        // Only the permuted intra scan table is built, which is all the decoder reads - FFmpeg
        // says the same in a comment at this spot.
        IdctDsp.PermuteScantable(
            c.IntraScantable.Permutated,
            c.AlternateScan != 0 ? MpegVideoData.AlternateVerticalScan : IdctDsp.ZigzagDirect,
            IdctDsp.IdentityPermutation);

        // Composite display is not parsed, as in the C.
    }

    /// <summary>Motion types (<c>mpeg12dec.c:401</c>). MT_16X8 and MT_FRAME share a value.</summary>
    private const int MtField = 1;
    private const int MtFrame = 2;
    private const int MtDmv = 3;

    /// <summary>Picture structures (<c>mpegutils.h:31</c>).</summary>
    public const int PictTopField = 1;
    public const int PictBottomField = 2;
    public const int PictFrame = 3;

    /// <summary><c>IS_INTRA</c> - the low three bits cover every intra type.</summary>
    private static bool IsIntra(int mbType) => (mbType & 7) != 0;

    /// <summary><c>IS_QUANT</c>.</summary>
    private static bool IsQuant(int mbType) => (mbType & MbType.Quant) != 0;

    /// <summary><c>HAS_CBP</c>.</summary>
    private static bool HasCbp(int mbType) => (mbType & MbType.Cbp) != 0;

    /// <summary><c>HAS_MV</c> - direction 0 is forward, 1 backward.</summary>
    private static bool HasMv(int mbType, int dir) => (mbType & (MbType.ForwardMv << dir)) != 0;

    /// <summary><c>MB_TYPE_MV_2_MV_DIR</c>.</summary>
    private static int MbTypeToMvDir(int mbType)
        => (mbType >> 12) & (MpegVideoConstants.MvDirForward | MpegVideoConstants.MvDirBackward);

    /// <summary>Index of the current macroblock in the picture's mb_type table.</summary>
    private static int MbTypeIndex(ref MpegEncContext c)
        => c.CurPic.MbTypeOffset + c.MbX + c.MbY * c.MbStride;

    /// <summary><c>mv[i][j][k]</c>, flattened as the inline array stores it.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary><c>last_mv[i][j][k]</c>.</summary>
    private static int LastMvIndex(int i, int j, int k) => i * 4 + j * 2 + k;

    /// <summary><c>field_select[i][j]</c>.</summary>
    private static int FieldSelectIndex(int i, int j) => i * 2 + j;

    /// <summary><c>mpeg_f_code[i][j]</c>.</summary>
    private static int FCodeIndex(int i, int j) => i * 2 + j;

    /// <summary>
    /// <c>mpeg_get_qscale</c> (<c>mpegvideodec.h:80</c>) - reads the quantiser scale and scales
    /// it immediately.
    /// </summary>
    /// <remarks>
    /// The value stored on the context is already doubled, or already through the non-linear
    /// table. That is why the MPEG-1/2 block readers multiply by it directly, and why
    /// reconstruction does not call MpegVideoUnquantize for this codec - doing both would
    /// quantise twice.
    /// </remarks>
    public static int MpegGetQscale(ref GetBitContext gb, int qScaleType)
    {
        int qscale = (int)gb.GetBits(5);
        return qScaleType != 0 ? MpegVideoData.Mpeg2NonLinearQscale[qscale] : qscale << 1;
    }

    /// <summary><c>get_dmv</c> - the dual prime differential, which is -1, 0 or 1.</summary>
    public static int GetDmv(ref GetBitContext gb)
        => gb.GetBits1() != 0 ? 1 - ((int)gb.GetBits1() << 1) : 0;

    /// <summary>
    /// <c>mpeg_decode_mb</c> - reads one macroblock: its type, its motion vectors and its
    /// coefficient blocks.
    /// </summary>
    /// <remarks>
    /// <paramref name="mbSkipRun"/> is post-decremented even when already zero, which is
    /// FFmpeg's behaviour and deliberate: the run counts macroblocks to skip before this one, so
    /// zero means decode normally and leaves -1 behind for the next call.
    /// </remarks>
    /// <returns>False on corrupt input, where FFmpeg returns AVERROR_INVALIDDATA.</returns>
    public static bool MpegDecodeMb(Mpeg12SliceContext s, ref int mbSkipRun, bool isMpeg2)
    {
        ref MpegEncContext c = ref s.C;
        int mbBlockCount = 4 + (1 << c.ChromaFormat);
        int mbType;

        if (mbSkipRun-- != 0)
            return DecodeSkippedMacroblock(s);

        switch (c.PictType)
        {
            case AvPictureType.P:
                mbType = s.Gb.GetVlc2(Mpeg12.MbPTypeVlc, Mpeg12Vlc.MbPTypeVlcBits, 1);
                if (mbType < 0) return false;
                break;

            case AvPictureType.B:
                mbType = s.Gb.GetVlc2(Mpeg12.MbBTypeVlc, Mpeg12Vlc.MbBTypeVlcBits, 1);
                if (mbType < 0) return false;
                break;

            default:
                // I pictures, and FFmpeg's default arm, which treats anything else as intra.
                if (s.Gb.GetBits1() == 0)
                {
                    if (s.Gb.GetBits1() == 0) return false;
                    mbType = MbType.Quant | MbType.Intra;
                }
                else
                {
                    mbType = MbType.Intra;
                }
                break;
        }

        bool ok = IsIntra(mbType)
            ? DecodeIntraMacroblock(s, mbType, mbBlockCount, isMpeg2)
            : DecodeInterMacroblock(s, ref mbType, mbBlockCount, isMpeg2);

        if (!ok) return false;

        c.CurPic.MbType![MbTypeIndex(ref c)] = (uint)mbType;
        return true;
    }

    /// <summary>The skipped-macroblock arm.</summary>
    /// <remarks>
    /// A skip in a P picture repeats the co-located macroblock with a zero vector. In a B
    /// picture it inherits the left neighbour's type, because a B skip means "the same
    /// prediction as the macroblock before" - and inheriting an intra type is invalid, since an
    /// intra macroblock has no prediction to repeat.
    /// </remarks>
    private static bool DecodeSkippedMacroblock(Mpeg12SliceContext s)
    {
        ref MpegEncContext c = ref s.C;
        int at = MbTypeIndex(ref c);

        if (c.PictType == AvPictureType.P)
        {
            c.MbSkipped = 1;
            c.CurPic.MbType![at] = (uint)(MbType.Skip | MbType.ForwardMv | MbType.Size16X16);
            return true;
        }

        int neighbour = c.MbX != 0
            ? (int)c.CurPic.MbType![at - 1]
            : (int)c.CurPic.MbType![c.CurPic.MbTypeOffset + c.MbWidth + (c.MbY - 1) * c.MbStride - 1];

        if (IsIntra(neighbour)) return false;

        c.CurPic.MbType![at] = (uint)(neighbour | MbType.Skip);

        if ((c.Mv[MvIndex(0, 0, 0)] | c.Mv[MvIndex(0, 0, 1)]
           | c.Mv[MvIndex(1, 0, 0)] | c.Mv[MvIndex(1, 0, 1)]) == 0)
            c.MbSkipped = 1;

        return true;
    }

    /// <summary>The intra arm: no motion, every block coded.</summary>
    private static bool DecodeIntraMacroblock(
        Mpeg12SliceContext s, int mbType, int mbBlockCount, bool isMpeg2)
    {
        ref MpegEncContext c = ref s.C;

        BlockDsp.ClearBlocks(s.Block);
        if (c.ChromaYShift == 0)
            BlockDsp.ClearBlocks(s.Block.AsSpan(6 * 64));

        // Frame pictures may switch to field DCT per macroblock unless the picture header
        // already fixed it.
        if (c.PictureStructure == PictFrame && c.FramePredFrameDct == 0)
            c.InterlacedDct = (int)s.Gb.GetBits1();

        if (IsQuant(mbType))
            c.Qscale = MpegGetQscale(ref s.Gb, c.QScaleType);

        if (c.ConcealmentMotionVectors != 0)
        {
            // Parsed and kept as the predictor, though an intra macroblock does not use them:
            // they exist so a decoder can conceal a lost following macroblock.
            if (c.PictureStructure != PictFrame)
                s.Gb.SkipBits1();      // field select

            int mx = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(0, 0)],
                                      c.LastMv[LastMvIndex(0, 0, 0)]);
            c.Mv[MvIndex(0, 0, 0)] = mx;
            c.LastMv[LastMvIndex(0, 0, 0)] = mx;
            c.LastMv[LastMvIndex(0, 1, 0)] = mx;

            int my = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(0, 1)],
                                      c.LastMv[LastMvIndex(0, 0, 1)]);
            c.Mv[MvIndex(0, 0, 1)] = my;
            c.LastMv[LastMvIndex(0, 0, 1)] = my;
            c.LastMv[LastMvIndex(0, 1, 1)] = my;

            s.Gb.CheckMarker();
        }
        else
        {
            for (int i = 0; i < 8; i++) c.LastMv[i] = 0;
        }

        c.MbIntra = 1;

        if (isMpeg2)
        {
            for (int i = 0; i < mbBlockCount; i++)
                if (!Mpeg2DecodeBlockIntra(ref s.Gb, c.IntraMatrix, c.ChromaIntraMatrix,
                                           c.IntraScantable.Permutated, s.LastDc, s.BlockAt(i),
                                           i, c.Qscale, c.IntraDcPrecision, c.IntraVlcFormat != 0))
                    return false;
        }
        else
        {
            for (int i = 0; i < 6; i++)
                if (!Mpeg12.Mpeg1DecodeBlockIntra(ref s.Gb, c.IntraMatrix,
                                                  c.IntraScantable.Permutated, s.LastDc,
                                                  s.BlockAt(i), i, c.Qscale))
                    return false;
        }

        return true;
    }

    /// <summary>The inter arm: motion vectors, then only the blocks the coded pattern names.</summary>
    private static bool DecodeInterMacroblock(
        Mpeg12SliceContext s, ref int mbType, int mbBlockCount, bool isMpeg2)
    {
        ref MpegEncContext c = ref s.C;

        if ((mbType & MbType.ZeroMv) != 0)
        {
            // A coded macroblock with no transmitted vector: predict from the co-located block.
            c.MvDir = MpegVideoConstants.MvDirForward;

            if (c.PictureStructure == PictFrame)
            {
                if (c.FramePredFrameDct == 0)
                    c.InterlacedDct = (int)s.Gb.GetBits1();
                c.MvType = MpegVideoConstants.MvType16X16;
            }
            else
            {
                c.MvType = MpegVideoConstants.MvTypeField;
                mbType |= MbType.Interlaced;
                c.FieldSelect[FieldSelectIndex(0, 0)] = c.PictureStructure - 1;
            }

            if (IsQuant(mbType))
                c.Qscale = MpegGetQscale(ref s.Gb, c.QScaleType);

            c.LastMv[LastMvIndex(0, 0, 0)] = 0;
            c.LastMv[LastMvIndex(0, 0, 1)] = 0;
            c.LastMv[LastMvIndex(0, 1, 0)] = 0;
            c.LastMv[LastMvIndex(0, 1, 1)] = 0;
            c.Mv[MvIndex(0, 0, 0)] = 0;
            c.Mv[MvIndex(0, 0, 1)] = 0;
        }
        else
        {
            int motionType;
            if (c.PictureStructure == PictFrame && c.FramePredFrameDct != 0)
            {
                motionType = MtFrame;
            }
            else
            {
                motionType = (int)s.Gb.GetBits(2);
                if (c.PictureStructure == PictFrame && HasCbp(mbType))
                    c.InterlacedDct = (int)s.Gb.GetBits1();
            }

            if (IsQuant(mbType))
                c.Qscale = MpegGetQscale(ref s.Gb, c.QScaleType);

            c.MvDir = MbTypeToMvDir(mbType);

            if (!DecodeMotionVectors(s, ref mbType, motionType)) return false;
        }

        c.MbIntra = 0;

        // The DC predictors are reset for every inter macroblock, because an inter macroblock
        // breaks the chain of intra DC prediction.
        s.LastDc[0] = s.LastDc[1] = s.LastDc[2] = 128 << c.IntraDcPrecision;

        if (HasCbp(mbType))
        {
            BlockDsp.ClearBlocks(s.Block);

            int cbp = s.Gb.GetVlc2(Mpeg12.MbPatVlc, Mpeg12Vlc.MbPatVlcBits, 1);

            if (mbBlockCount > 6)
            {
                // 4:2:2 and 4:4:4 carry extra pattern bits for the additional chroma blocks.
                cbp *= 1 << (mbBlockCount - 6);
                cbp |= (int)s.Gb.GetBits(mbBlockCount - 6);
                BlockDsp.ClearBlocks(s.Block.AsSpan(6 * 64));
            }

            if (cbp <= 0) return false;

            if (isMpeg2)
            {
                cbp <<= 12 - mbBlockCount;

                for (int i = 0; i < mbBlockCount; i++)
                {
                    if ((cbp & (1 << 11)) != 0)
                    {
                        int last = Mpeg2DecodeBlockNonIntra(
                            ref s.Gb, c.IntraScantable.Permutated,
                            c.InterMatrix, c.ChromaInterMatrix, s.BlockAt(i), i, c.Qscale);
                        if (last == -2) return false;
                        c.BlockLastIndex[i] = last;
                    }
                    else
                    {
                        c.BlockLastIndex[i] = -1;
                    }
                    cbp += cbp;
                }
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    if ((cbp & 32) != 0)
                    {
                        int last = Mpeg12Dec.Mpeg1DecodeBlockInter(
                            ref s.Gb, c.IntraScantable.Permutated, c.InterMatrix,
                            s.BlockAt(i), c.Qscale);
                        if (last == -2) return false;
                        c.BlockLastIndex[i] = last;
                    }
                    else
                    {
                        c.BlockLastIndex[i] = -1;
                    }
                    cbp += cbp;
                }
            }
        }
        else
        {
            for (int i = 0; i < 12; i++)
                c.BlockLastIndex[i] = -1;
        }

        return true;
    }

    /// <summary>
    /// The motion vector section of an inter macroblock, which is where MPEG-2's interlacing
    /// shows up most.
    /// </summary>
    private static bool DecodeMotionVectors(Mpeg12SliceContext s, ref int mbType, int motionType)
    {
        ref MpegEncContext c = ref s.C;

        switch (motionType)
        {
            case MtFrame:   // and MT_16X8, which shares the value
                if (c.PictureStructure == PictFrame)
                {
                    mbType |= MbType.Size16X16;
                    c.MvType = MpegVideoConstants.MvType16X16;

                    for (int i = 0; i < 2; i++)
                    {
                        if (!HasMv(mbType, i)) continue;

                        int mx = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 0)],
                                                  c.LastMv[LastMvIndex(i, 0, 0)]);
                        c.Mv[MvIndex(i, 0, 0)] = mx;
                        c.LastMv[LastMvIndex(i, 0, 0)] = mx;
                        c.LastMv[LastMvIndex(i, 1, 0)] = mx;

                        int my = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 1)],
                                                  c.LastMv[LastMvIndex(i, 0, 1)]);
                        c.Mv[MvIndex(i, 0, 1)] = my;
                        c.LastMv[LastMvIndex(i, 0, 1)] = my;
                        c.LastMv[LastMvIndex(i, 1, 1)] = my;

                        // full_pel is MPEG-1 only: the vector is in whole pixels, so it is
                        // doubled into the half-pel units everything downstream expects.
                        if (c.FullPel[i] != 0)
                        {
                            c.Mv[MvIndex(i, 0, 0)] *= 2;
                            c.Mv[MvIndex(i, 0, 1)] *= 2;
                        }
                    }
                }
                else
                {
                    mbType |= MbType.Size16X8 | MbType.Interlaced;
                    c.MvType = MpegVideoConstants.MvType16X8;

                    for (int i = 0; i < 2; i++)
                    {
                        if (!HasMv(mbType, i)) continue;

                        for (int j = 0; j < 2; j++)
                        {
                            c.FieldSelect[FieldSelectIndex(i, j)] = (int)s.Gb.GetBits1();
                            for (int k = 0; k < 2; k++)
                            {
                                int val = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, k)],
                                                           c.LastMv[LastMvIndex(i, j, k)]);
                                c.LastMv[LastMvIndex(i, j, k)] = val;
                                c.Mv[MvIndex(i, j, k)] = val;
                            }
                        }
                    }
                }
                break;

            case MtField:
                c.MvType = MpegVideoConstants.MvTypeField;

                if (c.PictureStructure == PictFrame)
                {
                    mbType |= MbType.Size16X8 | MbType.Interlaced;

                    for (int i = 0; i < 2; i++)
                    {
                        if (!HasMv(mbType, i)) continue;

                        for (int j = 0; j < 2; j++)
                        {
                            c.FieldSelect[FieldSelectIndex(i, j)] = (int)s.Gb.GetBits1();

                            int val = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 0)],
                                                       c.LastMv[LastMvIndex(i, j, 0)]);
                            c.LastMv[LastMvIndex(i, j, 0)] = val;
                            c.Mv[MvIndex(i, j, 0)] = val;

                            // The vertical component is in field units here, so the predictor is
                            // halved going in and doubled coming out.
                            val = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 1)],
                                                   c.LastMv[LastMvIndex(i, j, 1)] >> 1);
                            c.LastMv[LastMvIndex(i, j, 1)] = 2 * val;
                            c.Mv[MvIndex(i, j, 1)] = val;
                        }
                    }
                }
                else
                {
                    mbType |= MbType.Size16X16 | MbType.Interlaced;

                    for (int i = 0; i < 2; i++)
                    {
                        if (!HasMv(mbType, i)) continue;

                        c.FieldSelect[FieldSelectIndex(i, 0)] = (int)s.Gb.GetBits1();
                        for (int k = 0; k < 2; k++)
                        {
                            int val = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, k)],
                                                       c.LastMv[LastMvIndex(i, 0, k)]);
                            c.LastMv[LastMvIndex(i, 0, k)] = val;
                            c.LastMv[LastMvIndex(i, 1, k)] = val;
                            c.Mv[MvIndex(i, 0, k)] = val;
                        }
                    }
                }
                break;

            case MtDmv:
                // Dual prime: one vector plus a small differential, from which the two field
                // predictions are derived rather than transmitted.
                if (c.ProgressiveSequence != 0) return false;

                c.MvType = MpegVideoConstants.MvTypeDmv;

                for (int i = 0; i < 2; i++)
                {
                    if (!HasMv(mbType, i)) continue;

                    int myShift = c.PictureStructure == PictFrame ? 1 : 0;

                    int mx = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 0)],
                                              c.LastMv[LastMvIndex(i, 0, 0)]);
                    c.LastMv[LastMvIndex(i, 0, 0)] = mx;
                    c.LastMv[LastMvIndex(i, 1, 0)] = mx;
                    int dmx = GetDmv(ref s.Gb);

                    int my = MpegDecodeMotion(ref s.Gb, c.MpegFCode[FCodeIndex(i, 1)],
                                              c.LastMv[LastMvIndex(i, 0, 1)] >> myShift);
                    int dmy = GetDmv(ref s.Gb);

                    c.LastMv[LastMvIndex(i, 0, 1)] = my * (1 << myShift);
                    c.LastMv[LastMvIndex(i, 1, 1)] = my * (1 << myShift);

                    c.Mv[MvIndex(i, 0, 0)] = mx;
                    c.Mv[MvIndex(i, 0, 1)] = my;
                    c.Mv[MvIndex(i, 1, 0)] = mx;   // not used
                    c.Mv[MvIndex(i, 1, 1)] = my;   // not used

                    if (c.PictureStructure == PictFrame)
                    {
                        mbType |= MbType.Size16X16 | MbType.Interlaced;

                        // The derived field vectors: scaled by temporal distance, with the
                        // (mx > 0) term rounding toward zero as the standard requires.
                        int m = c.TopFieldFirst != 0 ? 1 : 3;

                        c.Mv[MvIndex(i, 2, 0)] = ((mx * m + (mx > 0 ? 1 : 0)) >> 1) + dmx;
                        c.Mv[MvIndex(i, 2, 1)] = ((my * m + (my > 0 ? 1 : 0)) >> 1) + dmy - 1;

                        m = 4 - m;
                        c.Mv[MvIndex(i, 3, 0)] = ((mx * m + (mx > 0 ? 1 : 0)) >> 1) + dmx;
                        c.Mv[MvIndex(i, 3, 1)] = ((my * m + (my > 0 ? 1 : 0)) >> 1) + dmy + 1;
                    }
                    else
                    {
                        mbType |= MbType.Size16X16;

                        c.Mv[MvIndex(i, 2, 0)] = ((mx + (mx > 0 ? 1 : 0)) >> 1) + dmx;
                        c.Mv[MvIndex(i, 2, 1)] = ((my + (my > 0 ? 1 : 0)) >> 1) + dmy;

                        if (c.PictureStructure == PictTopField)
                            c.Mv[MvIndex(i, 2, 1)]--;
                        else
                            c.Mv[MvIndex(i, 2, 1)]++;
                    }
                }
                break;

            default:
                // Motion type 0 is not defined; FFmpeg logs "00 motion_type" and bails.
                return false;
        }

        return true;
    }

    /// <summary>
    /// <c>mpeg_decode_motion</c> - one motion vector component, as a difference from a
    /// prediction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code gives a magnitude; <paramref name="fcode"/> says how many extra bits of
    /// precision follow, so a stream with a wide search range spends more bits per vector. A
    /// code of zero means "same as the prediction" and is the common case in still areas.
    /// </para>
    /// <para>
    /// The final <c>sign_extend(val, 5 + shift)</c> is modulo decoding, and it is not a
    /// safety clamp: motion vectors wrap within the range <paramref name="fcode"/> allows, so a
    /// prediction near the limit plus a difference legitimately comes out the other side. Clamp
    /// instead of wrapping and large motion decodes to the wrong place.
    /// </para>
    /// </remarks>
    /// <returns>The vector component, or 0xFFFF when the code was invalid.</returns>
    public static int MpegDecodeMotion(ref GetBitContext gb, int fcode, int pred)
    {
        int code = gb.GetVlc2(Mpeg12.MvVlc, Mpeg12Vlc.MvVlcBits, 2);
        if (code == 0) return pred;
        if (code < 0) return 0xFFFF;

        int sign = (int)gb.GetBits1();
        int shift = fcode - 1;
        int val = code;

        if (shift != 0)
        {
            val = (val - 1) << shift;
            val |= (int)gb.GetBits(shift);
            val++;
        }

        if (sign != 0) val = -val;
        val += pred;

        // Modulo decoding: wrap within the range fcode allows.
        int bits = 5 + shift;
        return (val << (32 - bits)) >> (32 - bits);
    }

    /// <summary>
    /// <c>mpeg1_decode_block_inter</c> - reads one inter block of an MPEG-1 picture.
    /// </summary>
    /// <remarks>
    /// The first coefficient is special-cased because the end-of-block code <c>10</c> and the
    /// shortest run/level code <c>1s</c> share their leading bit, and a block cannot open with
    /// end-of-block. So a leading 1 here is always a coefficient of magnitude one, read inline
    /// rather than through the table.
    /// </remarks>
    /// <returns>The index of the last coded coefficient, or -2 when the block is corrupt.</returns>
    public static int Mpeg1DecodeBlockInter(
        ref GetBitContext gb, ReadOnlySpan<byte> scantable, ReadOnlySpan<ushort> interMatrix,
        Span<short> block, int qscale)
    {
        int i = -1;
        bool overflowed = false;
        bool ended = false;

        // Leading 1: a magnitude-one coefficient at position 0, with the next bit as its sign.
        if (gb.ShowBits(1) != 0)
        {
            int first = (3 * qscale * interMatrix[0]) >> 5;
            first = (first - 1) | 1;
            if (gb.ShowBits(2) == 3) first = -first;   // the 0x40000000 cache test
            block[0] = (short)first;
            i++;
            gb.SkipBits(2);
            if (gb.ShowBits(2) == 2) ended = true;
        }

        while (!ended)
        {
            gb.GetRlVlc(Mpeg12.Mpeg1RlVlc, Mpeg12Vlc.TexVlcBits, 2, out int level, out int run);
            int j;

            if (level != 0)
            {
                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                level = ((level * 2 + 1) * qscale * interMatrix[j]) >> 5;
                level = (level - 1) | 1;

                int sign = -(int)gb.ShowBits(1);
                level = (level ^ sign) - sign;
                gb.SkipBits(1);
            }
            else
            {
                run = (int)gb.ShowBits(6) + 1;
                gb.SkipBits(6);
                level = gb.GetSBits(8);

                if (level == -128) level = (int)gb.GetBits(8) - 256;
                else if (level == 0) level = (int)gb.GetBits(8);

                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                if (level < 0)
                {
                    level = -level;
                    level = ((level * 2 + 1) * qscale * interMatrix[j]) >> 5;
                    level = (level - 1) | 1;
                    level = -level;
                }
                else
                {
                    level = ((level * 2 + 1) * qscale * interMatrix[j]) >> 5;
                    level = (level - 1) | 1;
                }
            }

            block[j] = (short)level;
            if (gb.ShowBits(2) == 2) break;
        }

        gb.SkipBits(2);
        return overflowed ? -2 : i;
    }

    /// <summary>
    /// <c>mpeg2_decode_block_non_intra</c> - reads one inter block of an MPEG-2 picture.
    /// </summary>
    /// <remarks>
    /// Same shape as the MPEG-1 reader with MPEG-2's differences: a twelve-bit escape, no
    /// oddification, mismatch control across the block, and chroma on its own matrix.
    /// </remarks>
    /// <returns>The index of the last coded coefficient, or -2 when the block is corrupt.</returns>
    public static int Mpeg2DecodeBlockNonIntra(
        ref GetBitContext gb, ReadOnlySpan<byte> scantable,
        ReadOnlySpan<ushort> interMatrix, ReadOnlySpan<ushort> chromaInterMatrix,
        Span<short> block, int n, int qscale)
    {
        ReadOnlySpan<ushort> quantMatrix = n < 4 ? interMatrix : chromaInterMatrix;

        int mismatch = 1;
        int i = -1;
        bool overflowed = false;
        bool ended = false;

        if (gb.ShowBits(1) != 0)
        {
            int first = (3 * qscale * quantMatrix[0]) >> 5;
            if (gb.ShowBits(2) == 3) first = -first;
            block[0] = (short)first;
            mismatch ^= first;
            i++;
            gb.SkipBits(2);
            if (gb.ShowBits(2) == 2) ended = true;
        }

        while (!ended)
        {
            gb.GetRlVlc(Mpeg12.Mpeg1RlVlc, Mpeg12Vlc.TexVlcBits, 2, out int level, out int run);
            int j;

            if (level != 0)
            {
                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                level = ((level * 2 + 1) * qscale * quantMatrix[j]) >> 5;

                int sign = -(int)gb.ShowBits(1);
                level = (level ^ sign) - sign;
                gb.SkipBits(1);
            }
            else
            {
                run = (int)gb.ShowBits(6) + 1;
                gb.SkipBits(6);
                level = gb.GetSBits(12);

                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                if (level < 0)
                {
                    level = ((-level * 2 + 1) * qscale * quantMatrix[j]) >> 5;
                    level = -level;
                }
                else
                {
                    level = ((level * 2 + 1) * qscale * quantMatrix[j]) >> 5;
                }
            }

            mismatch ^= level;
            block[j] = (short)level;
            if (gb.ShowBits(2) == 2) break;
        }

        gb.SkipBits(2);
        block[63] ^= (short)(mismatch & 1);
        return overflowed ? -2 : i;
    }

    /// <summary>
    /// <c>mpeg2_decode_block_intra</c> - reads one intra block of an MPEG-2 picture,
    /// dequantising as it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four things differ from the MPEG-1 reader, and none of them is cosmetic.
    /// </para>
    /// <para>
    /// End of block is the decoded level 127 rather than a two-bit peek, because MPEG-2 may use
    /// either coefficient table and their EOB codes differ - which is also why the sentinel is
    /// built into the run-length tables rather than tested in the bitstream.
    /// </para>
    /// <para>
    /// The escape is a six-bit run and a twelve-bit signed level, where MPEG-1 uses eight bits
    /// with two extensions.
    /// </para>
    /// <para>
    /// The DC is scaled by <c>1 &lt;&lt; (3 - intra_dc_precision)</c> rather than by the quantiser
    /// matrix's first entry, and chroma reads its own matrix.
    /// </para>
    /// <para>
    /// There is no oddification. Instead the mismatch control runs over the block: the running
    /// exclusive-or starts from the DC and the lowest bit of coefficient 63 is flipped at the
    /// end. That is the same correction MpegVideoUnquantize applies for blocks dequantised
    /// later, done here because this reader dequantises inline.
    /// </para>
    /// </remarks>
    /// <returns>False when the coefficient index ran past 63, meaning the bitstream is corrupt.</returns>
    public static bool Mpeg2DecodeBlockIntra(
        ref GetBitContext gb,
        ReadOnlySpan<ushort> intraMatrix, ReadOnlySpan<ushort> chromaIntraMatrix,
        ReadOnlySpan<byte> scantable, Span<int> lastDc, Span<short> block,
        int n, int qscale, int intraDcPrecision, bool intraVlcFormat)
    {
        ReadOnlySpan<ushort> quantMatrix;
        int component;

        if (n < 4)
        {
            quantMatrix = intraMatrix;
            component = 0;
        }
        else
        {
            quantMatrix = chromaIntraMatrix;
            component = (n & 1) + 1;
        }

        int diff = Mpeg12.DecodeDc(ref gb, component);
        int dc = lastDc[component] + diff;
        lastDc[component] = dc;

        block[0] = (short)(dc * (1 << (3 - intraDcPrecision)));

        int mismatch = block[0] ^ 1;
        int i = 0;
        bool overflowed = false;

        ReadOnlySpan<VLCElem> rlVlc = intraVlcFormat ? Mpeg12.Mpeg2RlVlc : Mpeg12.Mpeg1RlVlc;

        while (true)
        {
            gb.GetRlVlc(rlVlc, Mpeg12Vlc.TexVlcBits, 2, out int level, out int run);
            int j;

            if (level == 127)
            {
                // End of block.
                break;
            }

            if (level != 0)
            {
                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                level = (level * qscale * quantMatrix[j]) >> 4;

                int sign = -(int)gb.ShowBits(1);
                level = (level ^ sign) - sign;
                gb.SkipBits(1);
            }
            else
            {
                // Escape: six-bit run, twelve-bit signed level.
                run = (int)gb.ShowBits(6) + 1;
                gb.SkipBits(6);
                level = gb.GetSBits(12);

                i += run;
                if (i > MaxIndex) { overflowed = true; break; }

                j = scantable[i];
                if (level < 0)
                {
                    level = (-level * qscale * quantMatrix[j]) >> 4;
                    level = -level;
                }
                else
                {
                    level = (level * qscale * quantMatrix[j]) >> 4;
                }
            }

            mismatch ^= level;
            block[j] = (short)level;
        }

        block[63] ^= (short)(mismatch & 1);
        return !overflowed;
    }

    /// <summary>
    /// <c>mpeg_decode_gop</c> - the group-of-pictures header, which carries the timecode and
    /// whether the group is closed.
    /// </summary>
    /// <remarks>
    /// <c>closed_gop</c> is the only part the decode path reads, and it matters: in an open
    /// group the B pictures that follow the first intra picture reference a picture from the
    /// previous group, so starting there means skipping them rather than predicting from
    /// nothing.
    /// </remarks>
    public static void MpegDecodeGop(Mpeg1Context s1, byte[] buf, int offset, int length)
    {
        var gb = default(GetBitContext);
        gb.InitGetBits8(buf, offset, length);

        s1.TimecodeFrameStart = gb.GetBits(25);
        s1.ClosedGop = (int)gb.GetBits1();

        // broken_link: the references of the first B pictures after the intra picture are
        // missing because the stream was edited here. Read and discarded, as in the C.
        gb.SkipBits(1);
    }

    /// <summary>
    /// <c>mpeg_field_start</c> - called before the first slice of a picture, to take a frame to
    /// decode into and, for a field picture, to point the work picture at the right field.
    /// </summary>
    /// <remarks>
    /// A field picture writes alternate lines of a frame. Rather than special-case every
    /// destination calculation, FFmpeg doubles the work picture's line sizes and, for the bottom
    /// field, advances each plane by one line. Every subsequent address calculation then works
    /// in field coordinates without knowing it, which is why the macroblock layer has almost no
    /// field-specific code.
    /// </remarks>
    public static void MpegFieldStart(Mpeg1Context s1)
    {
        ref MpegEncContext c = ref s1.Slice.C;

        if (c.FirstField != 0 || c.PictureStructure == MpegVideoConstants.PictFrame)
        {
            MpegVideoDec.MpvFrameStart(ref c);

            ref MPVPicture pic = ref c.BufferPools.Pool!.Picture(c.CurPic.Ptr);

            // ff_frame_new_side_data_from_buf(AV_FRAME_DATA_A53_CC). The C MOVES the buffer
            // reference onto the frame, which empties the accumulator; here the bytes are
            // copied and the accumulator is reset by hand, to the same effect. Resetting is
            // not optional: without it every later picture would repeat these captions.
            pic.ClosedCaptionsLength = s1.A53Size;
            if (s1.A53Size > 0)
            {
                pic.ClosedCaptions ??= new byte[3 * A53MaxCcCount];
                s1.A53Buf.AsSpan(0, s1.A53Size).CopyTo(pic.ClosedCaptions);
                s1.A53Size = 0;
            }

            if (c.PictureStructure != MpegVideoConstants.PictFrame)
            {
                pic.TopFieldFirst =
                    c.PictureStructure == MpegVideoConstants.PictTopField ? 1 : 0;

                for (int i = 0; i < MpegPicture.MaxPlanes; i++)
                {
                    if (c.PictureStructure == MpegVideoConstants.PictBottomField)
                        c.CurPic.Data[i] += c.CurPic.Linesize[i];
                    c.CurPic.Linesize[i] *= 2;
                }
            }

            MpegEr.MpegErBindTables(ref c);
            MpegEr.MpegErFrameStart(ref c);

            // repeat_first_field asks the display to hold the picture for extra field periods.
            pic.RepeatPict = 0;
            if (c.RepeatFirstField != 0)
            {
                if (c.ProgressiveSequence != 0)
                    pic.RepeatPict = c.TopFieldFirst != 0 ? 4 : 2;
                else if (c.ProgressiveFrame != 0)
                    pic.RepeatPict = 1;
            }
        }
        else
        {
            // The second field of a picture whose first field is already decoded: no new frame,
            // just the other half of the one in hand.
            if (c.CurPic.Ptr == 0)
                throw new InvalidOperationException("Second field with no first field.");

            MpegVideoDec.MpvAllocDummyFrames(ref c);

            ref MPVPicture pic = ref c.BufferPools.Pool!.Picture(c.CurPic.Ptr);
            for (int i = 0; i < MpegPicture.MaxPlanes; i++)
            {
                c.CurPic.Data[i] = pic.Data[i];
                if (c.PictureStructure == MpegVideoConstants.PictBottomField)
                    c.CurPic.Data[i] += pic.Linesize[i];
            }
        }
    }
}

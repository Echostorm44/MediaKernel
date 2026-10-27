// The MPEG-1/2 decoder's entry point - a translation of decode_chunks, slice_end and the parts
// of mpeg_decode_frame that decide which picture comes out, from
// ffmpeg-master/libavcodec/mpeg12dec.c.
//
// One structural difference, recorded in the port checklist. FFmpeg is handed one picture
// per call, because mpegvideo_parser.c has already split the stream at picture boundaries, and
// decode_chunks therefore ends the picture when it runs out of buffer. Here the whole stream may
// arrive at once, so the picture is ended when the next picture start code is seen. That is what
// the parser and decode_chunks do between them; it is written in one place because the parser is
// not in scope for this port.
//
// Output order is the other thing worth reading slowly. A B picture is output as soon as it is
// decoded, because nothing is coded after it that comes before it. Any other picture is held
// back one frame and the PREVIOUS reference is output instead, because a B picture coded later
// is displayed earlier. That single frame of latency is the whole of MPEG reordering.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>A decoded picture's planes, as offsets into one buffer.</summary>
/// <remarks>
/// The buffer belongs to the decoder and is recycled, which is why a picture is delivered to a
/// callback rather than returned: it is valid for the duration of the call and no longer. FFmpeg
/// hands out a reference-counted AVFrame instead; this project does not refcount frames outside
/// a decoder, so the lifetime is made explicit rather than left to a convention.
/// </remarks>
public readonly struct Mpeg12Picture
{
    /// <summary>The allocation holding all three planes.</summary>
    public byte[] Buffer { get; init; }

    /// <summary>Offset of each plane's first sample.</summary>
    public int[] PlaneOffset { get; init; }

    /// <summary>Stride of each plane.</summary>
    public int[] Stride { get; init; }

    /// <summary>Picture width in samples.</summary>
    public int Width { get; init; }

    /// <summary>Picture height in samples.</summary>
    public int Height { get; init; }

    /// <summary>Chroma subsampling shifts.</summary>
    public int ChromaXShift { get; init; }

    /// <inheritdoc cref="ChromaXShift"/>
    public int ChromaYShift { get; init; }

    /// <summary>Whether this picture was coded as intra, predicted or bidirectional.</summary>
    public AvPictureType PictureType { get; init; }

    /// <summary>
    /// The presentation timestamp of the access unit this picture was coded in, carried through
    /// the reorder. <c>AVFrame.pts</c>.
    /// </summary>
    public long Pts { get; init; }

    /// <summary>
    /// <c>AV_FRAME_DATA_A53_CC</c> - the closed-caption triplets carried with this picture,
    /// empty if it has none.
    /// </summary>
    /// <remarks>
    /// Always A/53 Part 4 triplets whatever the stream sent: SCTE-20, DVD and Dish Network
    /// carriages are converted on the way in. Like the sample planes, this points into a
    /// buffer the decoder recycles and is valid only for the duration of the call.
    /// </remarks>
    public ReadOnlyMemory<byte> ClosedCaptions { get; init; }
}

/// <summary>Receives each decoded picture in display order.</summary>
/// <param name="picture">Valid only for the duration of the call.</param>
public delegate void Mpeg12PictureSink(in Mpeg12Picture picture);

/// <summary>The MPEG-1/2 video decoder.</summary>
public sealed class Mpeg12Decoder
{
    private readonly Mpeg1Context context = new();

    /// <summary>
    /// Builds the shared VLC tables, as <c>mpeg_decode_init</c> does.
    /// </summary>
    /// <remarks>
    /// Not optional and not merely an optimisation: the tables are static storage that starts
    /// out zeroed, and a zeroed VLC table decodes every code to the invalid symbol. Leaving
    /// this out produced a decoder that failed on the first macroblock while every table and
    /// VLC test still passed, because those tests build the tables themselves.
    /// </remarks>
    public Mpeg12Decoder() => Mpeg12.Mpeg12InitVlcsOnce();

    /// <summary>
    /// The sequence properties <c>mpeg_decode_postinit</c> derives: sample aspect ratio, frame
    /// rate, chroma siting, reordering delay and the colour description.
    /// </summary>
    /// <remarks>
    /// Valid once the first picture start code after a sequence header has been seen, which is
    /// where FFmpeg calls postinit. Reading it before then gives the zeroed struct.
    /// </remarks>
    public Mpeg12CodecContext SequenceInfo => context.Avctx;

    /// <summary>Holds the input when <see cref="Decode(ReadOnlySpan{byte}, Mpeg12PictureSink)"/>
    /// has to copy it, reused across calls and grown only when a larger one arrives.</summary>
    private byte[] inputBuffer = [];

    private bool sequenceSeen;
    private bool isMpeg2;

    /// <summary>
    /// Pictures skipped because no sequence header had arrived yet, as
    /// <c>decode_chunks</c>'s <c>if (!s2->context_initialized) break;</c> skips them.
    /// </summary>
    /// <remarks>
    /// Reported rather than silent so the skipping cannot become the normal case unnoticed. A
    /// handful after a seek into a program stream is correct; a number equal to the picture
    /// count means no sequence header was ever found and the decoder produced nothing, which is
    /// a failure however quietly it happens.
    /// </remarks>
    public long PicturesSkippedBeforeSequenceHeader { get; private set; }

    /// <summary><c>last_code</c> in <c>decode_chunks</c> - what the previous start code was.</summary>
    private int lastCode;

    /// <summary>Reusable plane offset and stride arrays, so delivering a picture allocates nothing.</summary>
    private readonly int[] planeOffsets = new int[3];
    private readonly int[] planeStrides = new int[3];

    /// <summary>Start codes (<c>mpeg12.h</c>).</summary>
    private const int SequenceHeaderCode = 0xB3;
    private const int ExtensionStartCode = 0xB5;
    private const int GopStartCode = 0xB8;
    private const int PictureStartCode = 0x00;
    private const int SequenceEndCode = 0xB7;
    private const int UserDataStartCode = 0xB2;
    private const int SliceMinStartCode = 0x01;
    private const int SliceMaxStartCode = 0xAF;

    /// <summary>Extension identifiers, from the first four bits of an extension.</summary>
    private const int SequenceExtensionId = 1;
    private const int SequenceDisplayExtensionId = 2;
    private const int QuantMatrixExtensionId = 3;
    private const int PictureDisplayExtensionId = 7;
    private const int PictureCodingExtensionId = 8;

    /// <summary>
    /// <c>decode_chunks</c> - walks the start codes and hands each payload to its parser,
    /// delivering every completed picture to <paramref name="sink"/> in display order.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> data, Mpeg12PictureSink sink, long pts = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);

        // The parsers take an array and a window rather than a span, because the bit reader
        // holds a byte[] and the alternative was a copy per slice. A caller who already has an
        // array should use the overload below and skip this copy entirely.
        if (inputBuffer.Length < data.Length) inputBuffer = new byte[data.Length];
        data.CopyTo(inputBuffer);
        Decode(inputBuffer, data.Length, sink, pts);
    }

    /// <summary>
    /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, Mpeg12PictureSink)" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// The array overload, which copies nothing. <paramref name="length"/> is how much of
    /// <paramref name="data"/> is real, so a caller reusing a larger buffer does not have to
    /// trim it.
    /// </remarks>
    public void Decode(byte[] data, int length, Mpeg12PictureSink sink, long pts = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, data.Length);

        // Every picture this call starts belongs to this access unit, and MpvFrameStart stamps
        // it onto the picture so that the reorder carries it to the right frame.
        context.Slice.C.CurrentPts = pts;

        bool skipFrame = false;
        bool firstSlice = false;

        int i = 0;
        while (i + 4 <= length)
        {
            if (!(data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1))
            {
                i++;
                continue;
            }

            int startCode = data[i + 3];
            int payloadStart = i + 4;
            int payloadEnd = FindNextStartCode(data, length, payloadStart);
            int payloadLength = payloadEnd - payloadStart;

            // The parser's packet boundary. FFmpeg's caller hands it one access unit at a time,
            // so decode_chunks starts each call with last_code at zero; here the access unit
            // ends at the first header of the next one.
            if (startCode is SequenceHeaderCode or GopStartCode or PictureStartCode
                && lastCode == SliceMinStartCode)
            {
                EndAccessUnit(sink, ref firstSlice, ref skipFrame);
            }

            switch (startCode)
            {
                case SequenceHeaderCode:
                    if (lastCode == 0)
                    {
                        if (!Mpeg12Dec.Mpeg1DecodeSequence(context, data, payloadStart, payloadLength))
                            throw new InvalidOperationException("MPEG sequence header is damaged.");
                        isMpeg2 = false;
                        sequenceSeen = true;
                        context.Sync = 1;
                        InitialiseDimensions();
                    }
                    break;

                case PictureStartCode:
                    // No sequence header yet: skip, as decode_chunks does.
                    //
                    //     if (!s2->context_initialized)
                    //         break;                            -- mpeg12dec.c:2482
                    //
                    // THIS USED TO THROW, and it made every seek into an MPEG program stream an
                    // unhandled exception: `transcode -ss 20` on Video-MPG-MPG.mpg died with
                    // "Picture before any sequence header" before writing anything. A program
                    // stream has no index, so a seek lands at a byte estimate and the first
                    // pictures arrive mid-GOP with their sequence header behind them - which is
                    // the ordinary case, not damaged input, and is why the C skips rather than
                    // complains. The count is kept so a gate can tell "skipped a few pictures
                    // after a seek" from "skipped every picture in the file".
                    if (!sequenceSeen)
                    {
                        PicturesSkippedBeforeSequenceHeader++;
                        break;
                    }

                    if (lastCode == 0)
                    {
                        // Where decode_chunks calls mpeg_decode_postinit: the sequence headers
                        // are all in by now, so this is the first point at which the aspect
                        // ratio and frame rate are known.
                        Mpeg12Dec.MpegDecodePostinit(context, isMpeg2);

                        if (!Mpeg12Dec.Mpeg1DecodePicture(context, data, payloadStart, payloadLength))
                            context.Slice.C.PictType = AvPictureType.None;
                        firstSlice = true;
                        lastCode = PictureStartCode | 0x100;
                    }
                    break;

                case ExtensionStartCode:
                    HandleExtension(data, payloadStart, payloadLength);
                    break;

                case GopStartCode:
                    if (lastCode == 0)
                    {
                        context.Slice.C.FirstField = 0;
                        Mpeg12Dec.MpegDecodeGop(context, data, payloadStart, payloadLength);
                        context.Sync = 1;
                    }
                    break;

                case SequenceEndCode:
                    EndAccessUnit(sink, ref firstSlice, ref skipFrame);
                    break;

                case UserDataStartCode:
                    // No sample data, but this is where closed captions, the DTG active format
                    // description and stereoscopic signalling live.
                    Mpeg12Dec.MpegDecodeUserData(context, data.AsSpan(payloadStart, payloadLength));
                    break;

                default:
                    if (startCode is >= SliceMinStartCode and <= SliceMaxStartCode)
                    {
                        int consumed = DecodeSliceChunk(
                            startCode, data, length, payloadStart, ref firstSlice, ref skipFrame);

                        if (consumed > 0)
                        {
                            int resume = payloadStart + consumed;
                            if (resume > i) { i = resume; continue; }
                        }
                    }
                    break;
            }

            i = payloadEnd;
        }

        EndAccessUnit(sink, ref firstSlice, ref skipFrame);
    }

    /// <summary>
    /// The end of what FFmpeg would have been handed as one packet: output whatever picture is
    /// now due, then clear the per-picture state that <c>decode_chunks</c> clears by returning.
    /// </summary>
    /// <remarks>
    /// Clearing it is not housekeeping. A picture that was skipped - the B pictures at the head
    /// of an open group, which reference a picture from before the start point - never reaches
    /// <see cref="SliceEnd"/>, so if the reset lived there the decoder would be left believing
    /// a picture was still in progress and would ignore every header that followed. It did
    /// exactly that, and decoded one picture out of thirty.
    /// </remarks>
    private void EndAccessUnit(Mpeg12PictureSink sink, ref bool firstSlice, ref bool skipFrame)
    {
        if (!skipFrame)
            SliceEnd(sink, ref firstSlice);

        lastCode = 0;
        firstSlice = false;
        skipFrame = false;
        context.Slice.C.PictType = AvPictureType.None;
    }

    /// <summary>
    /// The slice arm of <c>decode_chunks</c>: the per-picture checks that run at every slice,
    /// the first-slice call into <c>mpeg_field_start</c>, and the slice itself.
    /// </summary>
    /// <returns>Bytes of the slice payload consumed, or zero if the slice was not decoded.</returns>
    private int DecodeSliceChunk(
        int startCode, byte[] data, int length, int payloadStart,
        ref bool firstSlice, ref bool skipFrame)
    {
        ref MpegEncContext c = ref context.Slice.C;

        if (lastCode != (PictureStartCode | 0x100) && lastCode != SliceMinStartCode)
            return 0;

        if (lastCode == (PictureStartCode | 0x100))
        {
            // Checks FFmpeg runs once, when the first slice of a picture arrives and the
            // picture headers are all in.
            if (c.ProgressiveSequence != 0 && c.ProgressiveFrame == 0)
                c.ProgressiveFrame = 1;

            if (c.PictureStructure == 0 ||
                (c.ProgressiveFrame != 0 && c.PictureStructure != MpegVideoConstants.PictFrame))
            {
                c.PictureStructure = MpegVideoConstants.PictFrame;
            }

            if (c.PictureStructure == MpegVideoConstants.PictFrame)
            {
                c.FirstField = 0;
                c.VEdgePos = 16 * c.MbHeight;
            }
            else
            {
                c.FirstField ^= 1;
                c.VEdgePos = 8 * c.MbHeight;
                if (c.MbskipTable is not null)
                    Array.Clear(c.MbskipTable, 0, c.MbStride * c.MbHeight);
            }
        }

        int fieldPic = c.PictureStructure != MpegVideoConstants.PictFrame ? 1 : 0;
        int mbY = startCode - SliceMinStartCode;
        lastCode = SliceMinStartCode;

        mbY <<= fieldPic;
        if (c.PictureStructure == MpegVideoConstants.PictBottomField) mbY++;

        if (mbY >= c.MbHeight)
            throw new InvalidOperationException($"Slice below image ({mbY} >= {c.MbHeight}).");

        // A B picture at the head of an open group references a picture from before the start
        // point, and a P picture before the first intra picture has nothing to predict from.
        // Both are skipped rather than decoded into garbage.
        if (c.LastPic.Ptr == 0 && c.PictType == AvPictureType.B && context.ClosedGop == 0)
        {
            skipFrame = true;
            return 0;
        }

        if (c.PictType == AvPictureType.I) context.Sync = 1;

        if (c.NextPic.Ptr == 0 && c.PictType == AvPictureType.P && context.Sync == 0)
        {
            skipFrame = true;
            return 0;
        }

        if (c.PictType == AvPictureType.None)
            throw new InvalidOperationException("Slice with no picture header.");

        if (firstSlice)
        {
            skipFrame = false;
            firstSlice = false;
            Mpeg12Dec.MpegFieldStart(context);
        }

        if (c.CurPic.Ptr == 0)
            throw new InvalidOperationException("Slice with no current picture.");

        // The slice decoder is given everything from here to the end of the input, not just up
        // to the next start code. A slice ends with an eight-bit zero code followed by padding,
        // and the reader looks past that code before accepting it; truncating the buffer at the
        // next start code makes those reads overrun and the slice reports damage. FFmpeg does
        // the same and advances by the byte count returned.
        var result = Mpeg12Dec.MpegDecodeSlice(
            context.Slice, mbY, data, payloadStart, length - payloadStart, isMpeg2,
            out int consumed);

        // A damaged slice is reported to error resilience rather than thrown: the macroblocks it
        // did not deliver are concealed at the end of the picture. FFmpeg throws - returns the
        // error up - only under AV_EF_EXPLODE, which this port does not implement.
        if (result != Mpeg12Dec.SliceResult.Ok)
        {
            if (c.ResyncMbX >= 0 && c.ResyncMbY >= 0)
            {
                ErrorResilience.ErAddSlice(ref c.Er, c.ResyncMbX, c.ResyncMbY, c.MbX, c.MbY,
                                           ErStatus.AcError | ErStatus.DcError | ErStatus.MvError);
            }

            return 0;
        }

        ErrorResilience.ErAddSlice(ref c.Er, c.ResyncMbX, c.ResyncMbY, c.MbX - 1, c.MbY,
                                   ErStatus.AcEnd | ErStatus.DcEnd | ErStatus.MvEnd);

        return consumed;
    }

    /// <summary>
    /// <c>slice_end</c> - the picture is complete, so decide which picture is now due for
    /// display and hand it over.
    /// </summary>
    private void SliceEnd(Mpeg12PictureSink sink, ref bool firstSlice)
    {
        ref MpegEncContext c = ref context.Slice.C;

        if (c.CurPic.Ptr == 0) return;
        if (c.FirstField != 0 || firstSlice) return;

        ErrorResilience.ErFrameEnd(ref c);

        MpegVideoDec.MpvFrameEnd(ref c);

        MpvPicturePool pool = c.BufferPools.Pool!;

        if (c.PictType == AvPictureType.B || c.LowDelay != 0)
        {
            Deliver(sink, c.CurPic.Ptr);
        }
        else if (c.LastPic.Ptr != 0 && pool.Picture(c.LastPic.Ptr).Dummy == 0)
        {
            // One frame of latency: the picture before this one is the next in display order.
            Deliver(sink, c.LastPic.Ptr);
        }

        pool.UnrefPicture(ref c.CurPic);
    }

    /// <summary>
    /// <c>flush</c> in <c>mpeg12dec.c</c>, and the <c>ff_mpeg_flush</c> it calls - the seek
    /// reset. Discards the reference pictures and the sync state so that decoding can resume at
    /// an arbitrary point in the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named after <c>avcodec_flush_buffers</c>, the public entry point that reaches FFmpeg's
    /// <c>flush</c>, because <see cref="Flush(Mpeg12PictureSink)"/> is already taken by the
    /// end-of-stream drain and the two are opposites: the drain EMITS the picture being held
    /// back, this one THROWS IT AWAY. Calling the wrong one after a seek shows the viewer a
    /// frame from before the seek.
    /// </para>
    /// <para>
    /// One reset here has no counterpart in the C, and it follows from a divergence recorded at
    /// the top of this file. FFmpeg is handed one picture per call because its parser has
    /// already split the stream, so <c>last_code</c> is a local in <c>decode_chunks</c> that
    /// starts at zero every call. Here it is a field, because a picture ends when the next
    /// picture start code arrives. After a seek it has to go back to zero by hand, or the first
    /// picture of the new position is dropped as "ignoring pic after X".
    /// </para>
    /// <para>
    /// <c>sequence_seen</c> deliberately survives, as <c>context_initialized</c> does in the C:
    /// a seek within one file does not invalidate the sequence header, and requiring a new one
    /// would refuse to decode any stream that sends it once at the start.
    /// </para>
    /// </remarks>
    public void FlushBuffers()
    {
        ref MpegEncContext c = ref context.Slice.C;

        context.Sync = 0;
        context.ClosedGop = 0;

        // ff_mpeg_flush. The pool is null until the first picture has been decoded, and
        // flushing before then is legal - a seek before the first frame is just a seek.
        if (c.BufferPools.Pool is { } pool)
        {
            pool.UnrefPicture(ref c.CurPic);
            pool.UnrefPicture(ref c.LastPic);
            pool.UnrefPicture(ref c.NextPic);
        }

        c.MbX = 0;
        c.MbY = 0;
        c.PpTime = 0;

        // av_buffer_unref(&s->a53_buf_ref): captions accumulated for a picture that will now
        // never be decoded belong to no picture at all.
        context.A53Size = 0;

        lastCode = 0;
    }

    /// <summary>
    /// Flushes the one picture a non-low-delay stream is still holding back.
    /// </summary>
    /// <remarks>
    /// This is <c>mpeg_decode_frame</c>'s empty-packet case. Without it the last reference
    /// picture of a stream is decoded and never shown, which is an off-by-one that only a
    /// whole-file comparison catches.
    /// </remarks>
    public void Flush(Mpeg12PictureSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        ref MpegEncContext c = ref context.Slice.C;
        if (c.LowDelay != 0 || c.NextPic.Ptr == 0) return;

        Deliver(sink, c.NextPic.Ptr);
        c.BufferPools.Pool!.UnrefPicture(ref c.NextPic);
    }

    private void Deliver(Mpeg12PictureSink sink, int slot)
    {
        ref MpegEncContext c = ref context.Slice.C;
        ref MPVPicture pic = ref c.BufferPools.Pool!.Picture(slot);

        for (int i = 0; i < 3; i++)
        {
            planeOffsets[i] = pic.Data[i];
            planeStrides[i] = pic.Linesize[i];
        }

        ReadOnlyMemory<byte> captions = pic.ClosedCaptionsLength > 0
            ? pic.ClosedCaptions.AsMemory(0, pic.ClosedCaptionsLength)
            : default;

        var picture = new Mpeg12Picture
        {
            Buffer = pic.Frame!,
            PlaneOffset = planeOffsets,
            Stride = planeStrides,
            Width = c.Width,
            Height = c.Height,
            ChromaXShift = c.ChromaXShift,
            ChromaYShift = c.ChromaYShift,
            PictureType = pic.PictType,
            Pts = pic.Pts,
            ClosedCaptions = captions,
        };

        sink(in picture);
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int length, int from)
    {
        for (int i = from; i + 3 <= length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                return i;
        return length;
    }

    private void HandleExtension(byte[] data, int offset, int length)
    {
        if (length == 0) return;

        var gb = default(GetBitContext);
        gb.InitGetBits8(data, offset, length);

        int id = (int)gb.GetBits(4);
        switch (id)
        {
            case SequenceExtensionId:
                if (lastCode != 0) break;
                Mpeg12Dec.MpegDecodeSequenceExtension(context, ref gb);
                isMpeg2 = true;
                InitialiseDimensions();
                break;

            // No last_code guard on either display extension: the C has none, and inventing
            // one here would silently drop an extension FFmpeg accepts.
            case SequenceDisplayExtensionId:
                Mpeg12Dec.MpegDecodeSequenceDisplayExtension(context, ref gb);
                break;

            case QuantMatrixExtensionId:
                Mpeg12Dec.MpegDecodeQuantMatrixExtension(context.Slice, ref gb);
                break;

            case PictureCodingExtensionId:
                if (lastCode != (PictureStartCode | 0x100)) break;
                Mpeg12Dec.MpegDecodePictureCodingExtension(context, ref gb);
                break;

            case PictureDisplayExtensionId:
                Mpeg12Dec.MpegDecodePictureDisplayExtension(context, ref gb);
                break;

            default:
                // Copyright and the rest carry nothing this decoder reads.
                break;
        }
    }

    /// <summary>
    /// The part of <c>ff_mpv_common_init</c> that the decode path depends on: the macroblock
    /// grid and the strides derived from the picture size.
    /// </summary>
    private void InitialiseDimensions()
    {
        ref MpegEncContext c = ref context.Slice.C;

        // Error resilience has one repair that FFmpeg applies to MPEG-2 and not to MPEG-1, so
        // it has to be able to tell them apart. This is the only reader of CodecId.
        c.CodecId = isMpeg2 ? MpegCodecId.Mpeg2Video : MpegCodecId.Mpeg1Video;

        c.MbWidth = (c.Width + 15) / 16;

        // An interlaced MPEG-2 sequence is rounded up to an EVEN number of macroblock rows, so
        // that each field has a whole number of them. A 240-line picture is 15 macroblock rows
        // progressive and 16 interlaced, and getting this wrong rejects the last slice of every
        // picture with "slice below image" rather than producing a wrong one.
        c.MbHeight = isMpeg2 && c.ProgressiveSequence == 0
            ? (c.Height + 31) / 32 * 2
            : (c.Height + 15) / 16;

        // mb_stride is mb_width + 1 so the left and top neighbours of the first macroblock in a
        // row are addressable without a special case; b8_stride is the same trick at 8x8.
        c.MbStride = c.MbWidth + 1;
        c.B8Stride = c.MbWidth * 2 + 1;
        c.MbNum = c.MbWidth * c.MbHeight;

        (c.ChromaXShift, c.ChromaYShift) = c.ChromaFormat switch
        {
            MpegVideoConstants.Chroma420 => (1, 1),
            MpegVideoConstants.Chroma422 => (1, 0),
            MpegVideoConstants.Chroma444 => (0, 0),
            // Unknown input fails loudly: a silent default here would decode chroma at the
            // wrong size and look like a motion bug.
            _ => throw new InvalidOperationException($"Unsupported chroma format {c.ChromaFormat}."),
        };

        c.HEdgePos = c.MbWidth * 16;
        c.VEdgePos = c.MbHeight * 16;

        MpegEr.MpegErInit(ref c);
    }
}

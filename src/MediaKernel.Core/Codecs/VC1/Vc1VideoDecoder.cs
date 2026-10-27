// VC-1 and WMV3 as the rest of the project sees them: an IVideoDecoder the codec registry can
// hand a stream to.
//
// There is no access-unit assembly here, unlike MPEG-1/2. Every container that carries VC-1 or
// WMV3 - ASF, Matroska, MP4, AVI - stores one picture per sample, which is what vc1_decode_frame
// expects; a VC-1 elementary stream is split into pictures by Vc1EsDemuxer before it reaches a
// decoder. The one picture that spans two "samples" is an interlaced frame's second field, and
// VC-1 carries that inside the same packet behind a field marker, which the frame loop handles.

using System;
using System.Buffers;
using System.Collections.Generic;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>VC-1, WMV3 and their container tags, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// A packet handed to <see cref="Decode"/> completes at most one picture, and because VC-1
/// holds every non-bidirectional picture back by one frame when the stream has B pictures it
/// may complete none. Frames are queued and returned one per call, with
/// <see cref="DrainPendingFrames"/> releasing the one the stream is still holding at the end.
/// </para>
/// <para>
/// REQUIRED BEFORE <see cref="Initialize"/>, and it is not optional: a Simple or Main Profile
/// stream does not code its picture size anywhere in the bitstream - the sequence header in the
/// extradata carries no dimensions at all - so the container's are the only ones there are, and
/// <see cref="CodedWidth"/> and <see cref="CodedHeight"/> have to be set first. FFmpeg has the
/// same requirement and meets it through <c>avctx-&gt;coded_width</c>; this interface has no
/// channel for it, so they are properties. Initialising without them fails rather than
/// decoding a zero-sized picture.
/// </para>
/// <para>
/// Timestamps follow the reorder. A VC-1 stream with bidirectional pictures outputs the
/// picture BEFORE the one the packet carried whenever that packet is not bidirectional, so
/// attaching the packet's own timestamp to whatever came out stamps every non-B frame with a
/// time that belongs to a later picture. The reorder is exactly one picture deep, so one
/// stored timestamp is the whole of it - see <c>pendingPts</c>.
/// </para>
/// <para>
/// NOT GATED, and said plainly rather than implied: this is correct given per-packet display
/// timestamps, and no stream in the test set supplies them together with bidirectional
/// pictures. The conformance streams are elementary streams and VC-1 test bitstreams, which
/// carry no timestamps at all (AVFMT_NOTIMESTAMPS) and are stamped with a packet index; the
/// fragmented-MP4 one has composition offsets this port does not yet turn into the display
/// times FFmpeg derives; and the WMV3 clip that does have container timestamps has no
/// bidirectional pictures, so the delay line is a no-op on it. The conformance gate compares
/// in the order the decoder emits frames, which for VC-1 is display order, and therefore says
/// nothing about this.
/// </para>
/// </remarks>
// One registry id per class - CodecAttribute is AllowMultiple = false - so the container tags
// WMV3, WVC1 and WMVA reach this through DecodePipeline.NormalizeCodecId, which also decides
// which of the two extradata layouts to expect. See StreamCodecId.
[Codec("vc1", CodecType.Video, DisplayName = "VC-1 / WMV9 Software Decoder", Priority = 5)]
public sealed class Vc1VideoDecoder : IVideoDecoder
{
    private readonly Vc1Context context = new();
    private readonly Queue<DecodedVideoFrame> ready = new();

    private bool initialized;
    private long currentPts;

    /// <summary>
    /// The timestamp of the picture the decoder is holding back - the last non-bidirectional
    /// one, which comes out when the next non-bidirectional packet arrives.
    /// </summary>
    private long pendingPts;

    /// <inheritdoc/>
    public string CodecId => "vc1";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>
    /// The picture size the container declares, which for Simple and Main Profile is the only
    /// place it appears. Set before <see cref="Initialize"/>.
    /// </summary>
    public int CodedWidth { get; set; }

    /// <inheritdoc cref="CodedWidth"/>
    public int CodedHeight { get; set; }

    /// <summary>
    /// Which of <c>vc1dec.c</c>'s decoders this is. The two extradata layouts cannot be told
    /// apart from their bytes, so the container's tag decides; see
    /// <see cref="Vc1CodecContext.CodecId"/>.
    /// </summary>
    public Vc1CodecId StreamCodecId { get; set; } = Vc1CodecId.Vc1;

    /// <inheritdoc/>
    public int Width => context.Avctx.Width;

    /// <inheritdoc/>
    public int Height => context.Avctx.Height;

    /// <summary>
    /// 4:2:0 planar, for every VC-1 stream this decoder accepts - the sequence header rejects
    /// any other chroma format.
    /// </summary>
    public PixelFormat OutputFormat => PixelFormat.Yuv420P;

    /// <summary>
    /// Whether the extradata was accepted and a picture can be decoded.
    /// </summary>
    public bool IsReady => initialized;

    /// <inheritdoc/>
    /// <remarks>
    /// The extradata is mandatory for both layouts: this is where the sequence header lives,
    /// and VC-1's entry point with it. A stream that reaches a decoder without one cannot be
    /// decoded at all, which is why this returns false rather than waiting for in-band
    /// configuration the way H.264 can.
    /// </remarks>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.IsEmpty)
        {
            // A raw VC-1 elementary stream has no out-of-band configuration: the sequence
            // header and entry point are at the front of the first access unit, and FFmpeg's
            // demuxer layer lifts them out with the extract_extradata bitstream filter before
            // the decoder ever opens. That is done on the first packet instead - see Decode -
            // because this interface has no filter stage to put it in.
            deferred = true;
            return true;
        }

        return InitializeFrom(codecPrivate);
    }

    private bool InitializeFrom(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.IsEmpty || CodedWidth <= 0 || CodedHeight <= 0)
            return false;

        ref Vc1CodecContext avctx = ref context.Avctx;
        avctx = Vc1CodecContext.Default();
        avctx.CodecId = StreamCodecId;
        avctx.Extradata = codecPrivate.ToArray();

        // ff_set_dimensions, which is what a caller of avcodec_open2 has already done by the
        // time vc1_decode_init runs: it sets coded_width/coded_height and the scaled pair.
        if (avctx.SetDimensions(CodedWidth, CodedHeight) < 0)
            return false;

        if (Vc1Decoder.Vc1DecodeInit(context) < 0)
            return false;

        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        if (!initialized)
        {
            // An empty call on a decoder that has not started is a drain, and a drain of a
            // decoder that was never fed has nothing to give. A pipeline does this at end of
            // stream for every decoder it created, including one whose track it never
            // selected, so it must not be an error.
            if (data.IsEmpty)
                return ready.Count > 0 ? ready.Dequeue() : null;

            if (!deferred)
                throw new InvalidOperationException(
                    "VC-1: Decode before a successful Initialize. The extradata carries the "
                  + "sequence header, and without it there is nothing to decode against.");

            // A packet that carries no sequence header cannot be started from. Not an
            // exception: an elementary stream opened part way through is a legitimate thing
            // for a caller to hand over, and the next entry point will carry one.
            if (!InitializeFromFirstPacket(data))
                return null;
        }

        if (!data.IsEmpty)
        {
            // An empty call is the caller pulling what is already queued, not a new packet, so
            // it must not restamp the pictures still to come with its placeholder timestamp.
            currentPts = presentationTimeTicks;

            byte[] packet = GrowPacket(data.Length);
            data.CopyTo(packet);

            if (Vc1Decoder.Vc1DecodeFrame(context, packet, data.Length) >= 0)
            {
                // Which picture came out decides which timestamp goes with it: cur_pic is the
                // one this packet carried, last_pic is the one held back since the previous
                // non-bidirectional packet.
                bool outputIsCurrent = context.OutputPic.Ptr != 0
                                    && context.OutputPic.Ptr == context.S.CurPic.Ptr;
                Collect(outputIsCurrent ? currentPts : pendingPts);

                if (context.S.PictType != AvPictureType.B)
                    pendingPts = currentPts;
            }
        }

        return ready.Count > 0 ? ready.Dequeue() : null;
    }

    /// <inheritdoc/>
    public void DrainPendingFrames()
    {
        if (!initialized)
            return;

        // vc1_decode_frame's empty-packet case: a reordered stream is still holding its last
        // non-bidirectional picture, and an empty packet is what asks for it. That picture is
        // the one pendingPts belongs to.
        Vc1Decoder.Vc1DecodeFrame(context, [], 0);
        Collect(pendingPts);
    }

    /// <summary>
    /// Drops every frame still queued and the three reference pictures, so that decoding
    /// restarts from whatever packet arrives next. Anything already handed to the caller is
    /// theirs.
    /// </summary>
    /// <remarks>
    /// <c>vc1_sprite_flush</c> is the C's flush and belongs to the sprite compositor, which is
    /// out of scope; the plain decoders use <c>ff_mpeg_flush</c>, which is what this is.
    /// </remarks>
    public void Flush()
    {
        while (ready.Count > 0)
            ready.Dequeue().Dispose();

        if (initialized)
            MpegVideoDec.MpegFlush(ref context.S);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Flush();
        if (initialized)
        {
            Vc1Decoder.Vc1DecodeEnd(context);
            initialized = false;
        }
        packetBuffer = [];
    }

    private bool deferred;

    private byte[] packetBuffer = [];

    /// <summary>
    /// The <c>extract_extradata</c> bitstream filter, run on the first packet of an elementary
    /// stream: the sequence header and entry point at its front become the extradata, and the
    /// picture size comes with them.
    /// </summary>
    /// <remarks>
    /// The prefix is deliberately NOT removed from the packet. FFmpeg's filter removes it only
    /// when the caller asks, and <c>vc1_decode_frame</c>'s marker loop has no arm for a
    /// sequence header, so leaving it in costs one skipped unit on one packet and keeps the
    /// bytes the decoder is handed identical to the bytes the demuxer produced.
    /// </remarks>
    private bool InitializeFromFirstPacket(ReadOnlySpan<byte> data)
    {
        int size = Vc1Common.Vc1ExtractExtradata(data, data.Length);
        if (size <= 0)
            return false;

        // Advanced Profile codes its own dimensions: the sequence header sets
        // max_coded_width/height and the entry point calls ff_set_dimensions, so unlike a
        // Simple or Main Profile stream in a container there is nothing to supply from
        // outside. A nonzero placeholder gets past the check in InitializeFrom, which exists
        // for the container case; the header overwrites it.
        if (CodedWidth <= 0 || CodedHeight <= 0)
        {
            CodedWidth = 16;
            CodedHeight = 16;
        }

        StreamCodecId = Vc1CodecId.Vc1;
        deferred = false;
        return InitializeFrom(data[..size]);
    }

    /// <summary>
    /// The packet, copied into an array because <c>vc1_decode_frame</c> indexes it from a
    /// marker scan and hands slices of it to several bit readers at once, which a
    /// <see cref="ReadOnlySpan{T}"/> argument cannot survive being stored across.
    /// </summary>
    /// <remarks>
    /// Grown to the largest packet seen and then kept, rather than rented per packet: packet
    /// lengths differ frame to frame, and a per-frame allocation is a defect here.
    /// </remarks>
    private byte[] GrowPacket(int length)
    {
        int needed = length + Vc1.InputBufferPaddingSize;
        if (packetBuffer.Length < needed)
            packetBuffer = new byte[needed];

        // The padding exists so that a bit reader which runs past the end reads zeroes; it has
        // to be zeroes every packet, not just the first.
        Array.Clear(packetBuffer, length, packetBuffer.Length - length);
        return packetBuffer;
    }

    /// <summary>
    /// Copies whatever picture the last call chose to output into a frame the caller owns.
    /// </summary>
    /// <remarks>
    /// The decoder's own planes are padded and pooled, and the slot is reused as soon as the
    /// next picture needs it, so the frame handed out has to be a copy - the same shape as
    /// <c>Mpeg2VideoDecoder.Collect</c>, and for the same reason.
    /// </remarks>
    private void Collect(long pts)
    {
        if (context.OutputPic.Ptr == 0)
            return;

        ref MpegEncContext s = ref context.S;
        ref MPVPicture pic = ref s.BufferPools.Pool!.Picture(context.OutputPic.Ptr);

        // A dummy picture is the grey stand-in ff_mpv_alloc_dummy_frames invents for a stream
        // that starts on a predicted picture. FFmpeg never outputs one.
        if (pic.Dummy != 0)
        {
            context.OutputPic = default;
            return;
        }

        int width = s.Width;
        int height = s.Height;
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;

        int lumaSize = width * height;
        int chromaSize = chromaWidth * chromaHeight;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(lumaSize + 2 * chromaSize);

        CopyPlane(ref pic, 0, buffer, 0, width, height);
        CopyPlane(ref pic, 1, buffer, lumaSize, chromaWidth, chromaHeight);
        CopyPlane(ref pic, 2, buffer, lumaSize + chromaSize, chromaWidth, chromaHeight);

        ready.Enqueue(new DecodedVideoFrame(
            width, height, PixelFormat.Yuv420P, pts,
            buffer,
            0, width,
            lumaSize, chromaWidth,
            lumaSize + chromaSize, chromaWidth));

        context.OutputPic = default;
    }

    private static void CopyPlane(
        ref MPVPicture pic, int plane, byte[] destination, int destinationOffset,
        int planeWidth, int planeHeight)
    {
        int source = pic.Data[plane];
        int stride = pic.Linesize[plane];

        for (int y = 0; y < planeHeight; y++)
            pic.Frame.AsSpan(source + y * stride, planeWidth)
               .CopyTo(destination.AsSpan(destinationOffset + y * planeWidth));
    }
}

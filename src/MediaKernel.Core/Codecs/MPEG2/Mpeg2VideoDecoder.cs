// The MPEG-1/2 decoder as the rest of the project sees it: an IVideoDecoder the codec registry
// can hand a stream to.
//
// Mpeg12Decoder delivers pictures through a callback, because its picture buffers are pooled and
// recycled and the lifetime has to be explicit. IVideoDecoder returns one frame per call and the
// caller owns it, so each picture is copied into a rented dense buffer here - the same shape as
// VP8's ExtractOutputFrame, and for the same reason.
//
// This is also where the access unit is assembled. A container hands over whatever a PES packet
// held, which is not a picture: a picture can span several packets and a packet can hold the
// tail of one picture and the head of the next. Mpeg12Decoder needs whole access units, because
// a slice is decoded against the rest of its picture. FFmpeg does the assembly in
// mpegvideo_parser.c, which is not in scope for the codec port; it is done here instead, which
// is the same split - parser then decoder - in one fewer file.

using System;
using System.Buffers;
using System.Collections.Generic;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>MPEG-1 and MPEG-2 video, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// A packet handed to <see cref="Decode"/> may complete more than one picture, and because
/// MPEG holds every non-bidirectional picture back by one frame it may complete none. Frames
/// are therefore queued and returned one per call, with <see cref="DrainPendingFrames"/>
/// releasing the one the stream is still holding at the end.
/// </para>
/// <para>
/// TIMESTAMPS TRAVEL WITH THE PICTURE. A packet's timestamp belongs to the access unit that
/// STARTS in it, and that access unit is not usually the one the same call completes: an access
/// unit ends only where the next picture's header begins, which arrives in the following
/// packet. So the timestamps are queued against the byte offset they were stated at and claimed
/// once, as <c>ff_fetch_timestamp</c> does, and the one claimed is stamped onto the picture
/// itself (<c>AVFrame.pts</c>) so that the decoder's reorder carries it to the right frame.
/// </para>
/// <para>
/// This replaces a limitation this class used to state rather than fix: the timestamp was
/// attached to whatever picture a call happened to complete. Frame ORDER was right and the
/// timestamps were not, so a program stream's frames came out at
/// <c>760 793 926 860 893 1027</c> ms - each I or P picture carrying a stamp three slots ahead
/// of its place - and any player that trusts a timestamp dropped two frames in three.
/// </para>
/// </remarks>
[Codec("mpeg2video", CodecType.Video, DisplayName = "MPEG-1/2 Video Software Decoder", Priority = 5)]
public sealed class Mpeg2VideoDecoder : IVideoDecoder
{
    private readonly Mpeg12Decoder decoder = new();
    private readonly Queue<DecodedVideoFrame> ready = new();
    private readonly Mpeg12PictureSink sink;

    /// <summary>
    /// Where each packet's timestamp sits in the elementary stream, as an absolute byte offset,
    /// for the packets whose timestamp no access unit has claimed yet.
    /// </summary>
    /// <remarks>
    /// <c>AVCodecParserContext</c>'s <c>cur_frame_offset/cur_frame_pts</c> ring, which
    /// <c>ff_fetch_timestamp</c> reads and then clears. The MPEG audio adapter in this project
    /// carries the same queue for the same reason.
    /// </remarks>
    private readonly Queue<(long Offset, long Pts)> packetTimes = new();

    /// <summary>Bytes of the elementary stream dropped from the front of the assembly buffer.</summary>
    private long consumed;

    /// <summary>Bytes seen but not yet part of a complete access unit.</summary>
    private byte[] assembly = [];
    private int assembled;

    private int width;
    private int height;
    private PixelFormat outputFormat = PixelFormat.Yuv420P;

    public Mpeg2VideoDecoder() => sink = Collect;

    /// <inheritdoc/>
    public string CodecId => "mpeg2video";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int Width => width;

    /// <inheritdoc/>
    public int Height => height;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => outputFormat;

    /// <summary>
    /// MPEG-1 and MPEG-2 carry their whole configuration in band, in the sequence header and its
    /// extension, so there is nothing to initialise from.
    /// </summary>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        if (!data.IsEmpty)
        {
            // An empty call is the caller pulling what is already queued, not a new packet, so
            // it contributes no timestamp: the pictures still to come keep the ones they were
            // coded with.
            // A packet with no timestamp of its own contributes none: the picture that starts
            // in it is stamped by the pipeline afterwards, as guess_correct_pts does.
            if (presentationTimeTicks != DecodedAudioFrame.NoTimestamp)
                packetTimes.Enqueue((consumed + assembled, presentationTimeTicks));

            Append(data);

            // One access unit per pass, each with the timestamp claimed at ITS start. A packet
            // can hold several pictures, and handing the decoder all of them in one call gave
            // every one of them the same stamp - which is how an MPEG-2 P picture ended up with
            // the timestamp of a B picture coded three slots earlier.
            int complete;
            while ((complete = FirstAccessUnitLength()) > 0)
            {
                // The access unit starts at the front of the buffer, so the timestamp it
                // carries is the one stated at or before that offset.
                long pts = FetchTimestamp(consumed);

                // The array overload, not the span one: this assembly buffer is already an
                // array, and the span overload would copy it into one more.
                decoder.Decode(assembly, complete, sink, pts);

                assembly.AsSpan(complete, assembled - complete).CopyTo(assembly);
                assembled -= complete;
                consumed += complete;
            }
        }

        return ready.Count > 0 ? ready.Dequeue() : null;
    }

    /// <inheritdoc/>
    public void DrainPendingFrames()
    {
        // Whatever is left is the last access unit: nothing follows it to mark its end.
        if (assembled > 0)
        {
            decoder.Decode(assembly, assembled, sink, FetchTimestamp(consumed));
            consumed += assembled;
            assembled = 0;
        }

        decoder.Flush(sink);
    }

    /// <summary>
    /// Drops every frame still queued and every byte not yet decoded. Anything already handed to
    /// the caller is theirs.
    /// </summary>
    public void Flush()
    {
        assembled = 0;
        consumed = 0;
        packetTimes.Clear();
        while (ready.Count > 0)
            ready.Dequeue().Dispose();
    }

    /// <summary>
    /// <c>ff_fetch_timestamp</c>: the timestamp of the packet an access unit starts inside,
    /// claimed once.
    /// </summary>
    /// <param name="start">The access unit's first byte, as an absolute stream offset.</param>
    /// <remarks>
    /// The C keeps four slots and sets the one it uses to <c>INT64_MAX</c> so no second picture
    /// can read it; here the entries are a queue in stream order and claiming one removes it,
    /// which is the same rule with the bookkeeping the ordering already gives.
    /// </remarks>
    private long FetchTimestamp(long start)
    {
        // No match is no timestamp, not the one before: a picture the container did not stamp
        // is stamped by the pipeline afterwards, from the picture shown before it.
        long pts = DecodedAudioFrame.NoTimestamp;

        while (packetTimes.Count > 0 && packetTimes.Peek().Offset <= start)
            pts = packetTimes.Dequeue().Pts;

        return pts;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (assembled + data.Length > assembly.Length)
        {
            int capacity = Math.Max(assembled + data.Length, Math.Max(assembly.Length * 2, 1 << 16));
            var grown = new byte[capacity];
            assembly.AsSpan(0, assembled).CopyTo(grown);
            assembly = grown;
        }

        data.CopyTo(assembly.AsSpan(assembled));
        assembled += data.Length;
    }

    /// <summary>
    /// How many leading bytes form whole access units: everything up to the last header that
    /// starts a new one.
    /// </summary>
    /// <remarks>
    /// An access unit ends where the next one begins, and the next one begins at the first
    /// sequence, group or picture header after a slice. Scanning for that is the whole of what
    /// the parser has to do here - MPEG start codes cannot be emulated inside slice data, so a
    /// prefix found this way is always a real header.
    /// </remarks>
    /// <summary>
    /// The length of the FIRST complete access unit in the buffer, or zero if none has ended
    /// yet. An access unit ends where the next picture's header begins.
    /// </summary>
    /// <remarks>
    /// The first, not the last: this used to scan the whole buffer and return the last boundary
    /// it found, which handed the decoder every picture the buffer held in a single call. The
    /// pictures decoded correctly - every pixel gate stayed green - but one call can carry only
    /// one timestamp, so every picture in it got the same one.
    /// </remarks>
    private int FirstAccessUnitLength()
    {
        const int SequenceHeader = 0xB3;
        const int GroupHeader = 0xB8;
        const int PictureHeader = 0x00;
        const int SliceMin = 0x01;
        const int SliceMax = 0xAF;

        bool sliceSeen = false;
        int boundary = 0;

        for (int i = 0; i + 4 <= assembled; i++)
        {
            if (!(assembly[i] == 0 && assembly[i + 1] == 0 && assembly[i + 2] == 1)) continue;

            int code = assembly[i + 3];
            if (code is >= SliceMin and <= SliceMax)
            {
                sliceSeen = true;
            }
            else if (sliceSeen && code is SequenceHeader or GroupHeader or PictureHeader)
            {
                return i;
            }

            i += 2;
        }

        return boundary;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Flush();
        assembly = [];
    }

    private void Collect(in Mpeg12Picture picture)
    {
        width = picture.Width;
        height = picture.Height;

        outputFormat = (picture.ChromaXShift, picture.ChromaYShift) switch
        {
            (1, 1) => PixelFormat.Yuv420P,
            (1, 0) => PixelFormat.Yuv422P,
            (0, 0) => PixelFormat.Yuv444P,
            // Unknown input fails loudly: a wrong guess here hands the renderer chroma planes
            // of the wrong size, which looks like a decode bug rather than a format one.
            _ => throw new InvalidOperationException(
                $"Unsupported chroma subsampling {picture.ChromaXShift}/{picture.ChromaYShift}."),
        };

        int chromaWidth = picture.Width >> picture.ChromaXShift;
        int chromaHeight = picture.Height >> picture.ChromaYShift;

        int lumaSize = picture.Width * picture.Height;
        int chromaSize = chromaWidth * chromaHeight;
        int total = lumaSize + 2 * chromaSize;

        // The decoder's own planes are padded and pooled, and the slot is reused as soon as the
        // next picture needs it, so the frame handed out has to be a copy.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);

        CopyPlane(picture, 0, buffer, 0, picture.Width, picture.Height);
        CopyPlane(picture, 1, buffer, lumaSize, chromaWidth, chromaHeight);
        CopyPlane(picture, 2, buffer, lumaSize + chromaSize, chromaWidth, chromaHeight);

        ready.Enqueue(new DecodedVideoFrame(
            // The picture's own stamp, not the call's: this one may be the frame the
            // decoder has been holding back since the access unit before last.
            picture.Width, picture.Height, outputFormat, picture.Pts,
            buffer,
            0, picture.Width,
            lumaSize, chromaWidth,
            lumaSize + chromaSize, chromaWidth));
    }

    private static void CopyPlane(
        in Mpeg12Picture picture, int plane, byte[] destination, int destinationOffset,
        int planeWidth, int planeHeight)
    {
        int source = picture.PlaneOffset[plane];
        int stride = picture.Stride[plane];

        for (int y = 0; y < planeHeight; y++)
            picture.Buffer.AsSpan(source + y * stride, planeWidth)
                   .CopyTo(destination.AsSpan(destinationOffset + y * planeWidth));
    }
}

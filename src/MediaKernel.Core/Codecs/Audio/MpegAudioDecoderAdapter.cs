// MPEG audio (layers I, II and III) as one decoder, for streams that arrive in container
// packets rather than as whole frames.
//
// Two things this does that the plain Mp3DecoderAdapter cannot, both needed before an MPEG
// program stream can be heard:
//
//  1. It frames. A PES packet is a slice of the elementary stream, so it starts mid-frame and
//     usually ends mid-frame; Mp3Decoder.DecodeFrame wants a buffer that begins at a frame
//     header. Bytes are accumulated here and whole frames handed on, which is what the
//     container's parser does in FFmpeg.
//  2. It dispatches on the layer. FFmpeg has one mpegaudio decoder for all three layers; this
//     project has Mp2Decoder and Mp3Decoder, and the layer is in every frame header, so the
//     header decides. A program stream's audio is usually layer II and is labelled "mp2", which
//     nothing was registered for - the track was found, no decoder answered to the name, and
//     the file played silently.

using System;
using System.Collections.Generic;
using MediaKernel.Core.Codecs.MP3;

namespace MediaKernel.Core.Codecs.Audio;

/// <summary>MPEG-1/2 audio layers I-III, framed from a packet stream.</summary>
[Codec("mp2", CodecType.Audio, DisplayName = "MPEG Audio (Layer I/II/III) Software Decoder", Priority = 5)]
public sealed class MpegAudioDecoderAdapter : AudioDecoderAdapterBase
{
    /// <summary>
    /// Four bytes of sync word and fields, before any CRC. Enough to decide whether a frame
    /// starts here and how long it is.
    /// </summary>
    private const int MpegAudioHeaderBytes = 4;

    private readonly Mp2Decoder layer2 = new();
    private readonly Mp3Decoder layer3 = new();

    /// <summary>Bytes seen but not yet part of a whole frame.</summary>
    private byte[] assembly = new byte[1 << 13];
    private int assembled;

    private int sampleRate;
    private int channels;

    /// <summary>Set once the caller says no more data is coming.</summary>
    private bool endOfStream;

    /// <summary>
    /// Where each packet's timestamp sits in the stream, as an absolute byte offset, for the
    /// packets whose timestamp no frame has claimed yet.
    /// </summary>
    /// <remarks>
    /// <c>AVCodecParserContext</c>'s <c>cur_frame_offset/cur_frame_pts</c> ring, which
    /// <c>ff_fetch_timestamp</c> reads and then clears: a container timestamp belongs to the
    /// frame that starts inside that packet, and to exactly one frame. Every other frame gets
    /// <see cref="DecodedAudioFrame.NoTimestamp"/> and the caller carries the clock forward.
    /// </remarks>
    private readonly Queue<(long Offset, long Pts)> packetTimes = new();

    /// <summary>Bytes of the elementary stream dropped from the front of the assembly buffer.</summary>
    private long consumed;

    /// <inheritdoc/>
    public override string CodecId => "mp2";

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override int Channels => channels;

    /// <summary>MPEG audio is self-describing; every frame header carries rate, mode and layer.</summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    /// <inheritdoc/>
    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!data.IsEmpty)
        {
            // A packet with no timestamp of its own contributes none; the frames in it are
            // timed from the stream's running clock.
            if (presentationTimeTicks != DecodedAudioFrame.NoTimestamp)
                packetTimes.Enqueue((consumed + assembled, presentationTimeTicks));

            Append(data);
        }

        return DecodeOneFrame();
    }

    /// <summary>
    /// No more data will arrive, so the last frame may be decoded even though it is short.
    /// </summary>
    /// <remarks>
    /// The final frame of a captured stream is commonly truncated - this project's own MPEG
    /// clip ends 290 bytes into a 731-byte frame - and FFmpeg decodes it anyway, reading the
    /// zero padding past the end. Refusing it leaves the soundtrack one frame short of
    /// ffmpeg's, which is a truncation and so a failure, not a rounding difference.
    /// </remarks>
    public override void DrainPendingFrames() => endOfStream = true;

    /// <inheritdoc/>
    public override void Flush()
    {
        assembled = 0;
        endOfStream = false;
        consumed = 0;
        packetTimes.Clear();
        layer2.Reset();
        layer3.Reset();
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (assembled + data.Length > assembly.Length)
        {
            int capacity = Math.Max(assembled + data.Length, assembly.Length * 2);
            var grown = new byte[capacity];
            assembly.AsSpan(0, assembled).CopyTo(grown);
            assembly = grown;
        }

        data.CopyTo(assembly.AsSpan(assembled));
        assembled += data.Length;
    }

    /// <summary>
    /// Finds the next whole frame and decodes it, or returns null if the bytes in hand do not
    /// contain one yet.
    /// </summary>
    private DecodedAudioFrame? DecodeOneFrame()
    {
        int at = 0;

        while (true)
        {
            // Find a header. A stray byte pair that looks like a sync word is possible, so the
            // header has to parse as well as match.
            while (at + MpegAudioHeaderBytes <= assembled &&
                   !Mp3FrameParser.TryParseHeader(assembly.AsSpan(at, assembled - at), out _))
            {
                at++;
            }

            if (at + MpegAudioHeaderBytes > assembled)
            {
                // No header in hand. Keep the tail - a header may straddle the next packet.
                Consume(Math.Max(0, assembled - MpegAudioHeaderBytes));
                return null;
            }

            Mp3FrameParser.TryParseHeader(assembly.AsSpan(at, assembled - at), out var header);

            if (at + header.FrameSize > assembled && !endOfStream)
            {
                // The frame has started but not arrived. Drop whatever preceded it and wait.
                Consume(at);
                return null;
            }

            // At end of stream a short frame is decoded from what there is; the bit reader
            // returns zeros past the end, as FFmpeg's does from its input padding.
            int available = Math.Min(header.FrameSize, assembled - at);

            long pts = FetchTimestamp(consumed + at);
            var frame = assembly.AsSpan(at, available);

            sampleRate = header.SampleRate;
            channels = header.ChannelCount;

            EnsureBuffer(header.SamplesPerFrame * channels);

            bool decoded = header.Layer switch
            {
                MpegLayer.Layer2 => layer2.DecodeFrame(frame, SampleBuffer, out _),
                MpegLayer.Layer3 => layer3.DecodeFrame(frame, SampleBuffer, out _),
                // Layer I has no decoder in this project yet. Skipping it loudly is wrong for
                // playback and silently decoding it as layer II is worse, so the frame is
                // dropped and the stream continues; the track simply has no sound.
                _ => false,
            };

            Consume(at + available);

            if (decoded)
                return MakeFrame(header.SamplesPerFrame, pts);

            // A frame that failed to decode is not the end of the stream; try the next one.
            at = 0;
        }
    }

    /// <summary>Drops <paramref name="count"/> leading bytes and the packet times they covered.</summary>
    private void Consume(int count)
    {
        if (count <= 0) return;

        assembly.AsSpan(count, assembled - count).CopyTo(assembly);
        assembled -= count;
        consumed += count;
    }

    /// <summary>
    /// <c>ff_fetch_timestamp</c>: the timestamp of the packet a frame starts inside, claimed
    /// once, or <see cref="DecodedAudioFrame.NoTimestamp"/> when the frame began in a packet
    /// whose timestamp an earlier frame already took.
    /// </summary>
    /// <param name="frameStart">The frame's first byte, as an absolute stream offset.</param>
    /// <remarks>
    /// The C keeps four slots and sets the one it uses to <c>INT64_MAX</c> so no second frame
    /// can read it; here the entries are a queue in stream order and claiming one removes it,
    /// which is the same rule with the bookkeeping the ordering already gives. When two packet
    /// boundaries fall inside one frame the later timestamp wins, as the C's loop over its
    /// slots does.
    /// </remarks>
    private long FetchTimestamp(long frameStart)
    {
        long pts = DecodedAudioFrame.NoTimestamp;

        while (packetTimes.Count > 0 && packetTimes.Peek().Offset <= frameStart)
            pts = packetTimes.Dequeue().Pts;

        return pts;
    }
}

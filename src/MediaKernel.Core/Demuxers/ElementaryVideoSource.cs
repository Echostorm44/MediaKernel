// The video half of the elementary-stream path, and the reason it exists.
//
// DecodePipeline could reach a container holding one AUDIO track through
// IElementaryAudioSource - MP3, ADTS, AC-3, WAV, AIFF, AU, FLAC, DTS, CAF, VOC, XA, TTA all
// arrive that way - and had nothing at all for a container holding one VIDEO track. So seven
// demuxers this project already had were unreachable from the player, and with them three
// codecs that are FATE-conformant:
//
//   IvfDemuxer          22 VP8 test vectors and four AV1 files; VP8 is 7/7 FATE
//   Vc1EsDemuxer        six VC-1 elementary streams; VC-1 is 9/9 FATE
//   Vc1TestDemuxer      the two RCV wrappers of the same
//   Mpeg4VideoDemuxer   five .m4v files; MPEG-4 Part 2 is 271/271 frames bit-exact
//   RawVideoDemuxer     Y4M, whose rawvideo decoder is new
//
// The playability sweep is what found them, by opening every container in the tree and asking
// which tracks we claim to decode and then do not. Each of these answered either "Could not
// detect media format" - DemuxerFactory had no probe for it - or "Demuxer type X is not yet
// supported in the decode pipeline".
//
// A source here is one video track by definition, the same way an audio one is: there is no
// track table in an elementary stream.

using System;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// A container holding exactly one video track, read front to back.
/// </summary>
internal interface IElementaryVideoSource : IDisposable
{
    /// <summary>The ffmpeg codec name, which is what selects a decoder.</summary>
    string CodecId { get; }

    /// <summary>Picture width in pixels.</summary>
    int Width { get; }

    /// <summary>Picture height in pixels.</summary>
    int Height { get; }

    /// <summary>Frames per second, or 0 when the stream does not say.</summary>
    double FrameRate { get; }

    /// <summary>Stream duration, or <see cref="TimeSpan.Zero"/> when it cannot be known.</summary>
    TimeSpan Duration => TimeSpan.Zero;

    /// <summary>
    /// Out-of-band configuration the decoder needs before the first packet. Null for a
    /// format that configures itself from the bitstream, which is most of them - an
    /// elementary stream carries its own sequence headers by definition.
    /// </summary>
    byte[]? CodecPrivate => null;

    /// <summary>
    /// The container's four-character code, or 0. Raw video needs it: it is the only thing
    /// that says which of 173 pixel formats the bytes are.
    /// </summary>
    uint CodecTag => 0;

    /// <summary>
    /// Returns the next frame, or null at end of stream. The payload may be a window onto
    /// the source's own buffer - see the lifetime note on <see cref="DemuxerPacket.Data"/>.
    /// </summary>
    DemuxerPacket? ReadFrame();
}

/// <summary>An IVF file: a 32-byte header and then length-prefixed frames.</summary>
/// <remarks>
/// IVF is how the VP8, VP9 and AV1 test vectors are distributed, so it is the container most
/// of this tree's video samples are in.
/// </remarks>
internal sealed class IvfVideoSource : IElementaryVideoSource
{
    private readonly IvfDemuxer demuxer;
    private readonly DemuxerTrackInfo track;

    public IvfVideoSource(IvfDemuxer ivf)
    {
        demuxer = ivf;
        track = ivf.GetTrackInfo();
    }

    public string CodecId => track.CodecId;

    public int Width => track.Width;

    public int Height => track.Height;

    public double FrameRate => track.FrameRate;

    public DemuxerPacket? ReadFrame() => demuxer.ReadFrame();

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A raw MPEG-4 Part 2 elementary stream, split by <c>mpeg4_find_frame_end</c>.
/// </summary>
/// <remarks>
/// Mpeg4AccessUnitReader and NOT Mpeg4VideoDemuxer, for the same reason as VC-1 above: the
/// demuxer's packetizer was written from format notes and produces 47 packets where ffmpeg
/// produces 30 frames on TestMedia/Video-mpeg4-qcif.m4v, with the pictures diverging at frame
/// 16. The dimensions come from the demuxer's own header parse, which is correct - it is only
/// the framing that is not - and the decoder reads them again from the VOL header anyway.
/// </remarks>
internal sealed class Mpeg4VideoSource : IElementaryVideoSource
{
    /// <summary>What ffmpeg's raw demuxers default to when a stream states no frame rate.</summary>
    private const double DefaultFrameRate = 25.0;

    private readonly Codecs.Mpeg4Video.Mpeg4AccessUnitReader reader;
    private long frameIndex;

    public Mpeg4VideoSource(Codecs.Mpeg4Video.Mpeg4AccessUnitReader accessUnits) =>
        reader = accessUnits;

    public string CodecId => "mpeg4";

    public int Width => 0;

    public int Height => 0;

    public double FrameRate => DefaultFrameRate;

    public DemuxerPacket? ReadFrame()
    {
        if (!reader.ReadAccessUnit(out ReadOnlyMemory<byte> unit))
            return null;

        long pts = (long)(frameIndex * TimeSpan.TicksPerSecond / DefaultFrameRate);
        long duration = (long)(TimeSpan.TicksPerSecond / DefaultFrameRate);
        frameIndex++;

        return new DemuxerPacket(0, pts, pts, duration, isKeyframe: frameIndex == 1, unit);
    }

    public void Dispose() => reader.Dispose();
}

/// <summary>A raw VC-1 Advanced Profile elementary stream, split by <c>vc1_parse</c>.</summary>
/// <remarks>
/// Vc1AccessUnitReader and NOT Vc1EsDemuxer, which frames the stream with Vc1Packetizer -
/// written from the spec rather than translated, and wrong in two ways that the FrameCrc
/// harness already records: it starts a new access unit at a FIELD marker where vc1_parse
/// continues the current one, and it does not treat end of input as the end of a frame. That
/// is one picture too many per interlaced frame and one too few per file, and on
/// fate-suite/vc1/SA00040.vc1 it is 14 pictures where ffmpeg has 15, with the first one
/// already wrong.
///
/// The dimensions are zero here deliberately: a raw VC-1 stream carries its sequence header
/// in band and the decoder lifts it out of the first access unit, the way ffmpeg's
/// extract_extradata bitstream filter does.
/// </remarks>
internal sealed class Vc1ElementarySource : IElementaryVideoSource
{
    /// <summary>
    /// What ffmpeg's raw VC-1 demuxer defaults to, as its <c>framerate</c> option: a raw
    /// elementary stream states no frame rate anywhere.
    /// </summary>
    private const double DefaultFrameRate = 25.0;

    private readonly Codecs.Vc1Video.Vc1AccessUnitReader reader;
    private long frameIndex;

    public Vc1ElementarySource(Codecs.Vc1Video.Vc1AccessUnitReader accessUnits) =>
        reader = accessUnits;

    public string CodecId => "vc1";

    public int Width => 0;

    public int Height => 0;

    public double FrameRate => DefaultFrameRate;

    public DemuxerPacket? ReadFrame()
    {
        if (!reader.ReadAccessUnit(out ReadOnlyMemory<byte> unit))
            return null;

        long pts = (long)(frameIndex * TimeSpan.TicksPerSecond / DefaultFrameRate);
        long duration = (long)(TimeSpan.TicksPerSecond / DefaultFrameRate);
        frameIndex++;

        // Every access unit of an Advanced Profile stream is independently framed; which
        // ones are key frames is in the picture header, which only the decoder reads.
        return new DemuxerPacket(0, pts, pts, duration, isKeyframe: frameIndex == 1, unit);
    }

    public void Dispose() => reader.Dispose();
}

/// <summary>
/// An RCV file: SMPTE's own wrapper around a VC-1 Simple or Main Profile stream, with the
/// sequence header in a fixed-size file header rather than in band.
/// </summary>
/// <remarks>
/// This is the one elementary video source with codec private data, and it has to have it:
/// Simple and Main Profile carry no sequence header in the bitstream at all, so the four
/// bytes in the RCV header are the whole of the stream's configuration.
/// </remarks>
internal sealed class Vc1TestVideoSource : IElementaryVideoSource
{
    private readonly Vc1TestDemuxer demuxer;
    private long frameIndex;

    public Vc1TestVideoSource(Vc1TestDemuxer vc1) => demuxer = vc1;

    public string CodecId => demuxer.Codec;

    public int Width => demuxer.Width;

    public int Height => demuxer.Height;

    public double FrameRate => demuxer.FrameRate;

    public byte[]? CodecPrivate => demuxer.Extradata.Length > 0 ? demuxer.Extradata : null;

    public DemuxerPacket? ReadFrame()
    {
        if (!demuxer.ReadPacket(out Vc1TestPacket packet))
            return null;

        // The RCV frame header's timestamp is in milliseconds; a file that leaves it zero
        // gets timestamps from the frame rate, as the elementary sources do.
        long pts = packet.Pts != 0
            ? packet.Pts * TimeSpan.TicksPerMillisecond
            : FrameRate > 0 ? (long)(frameIndex * TimeSpan.TicksPerSecond / FrameRate) : 0;
        long duration = FrameRate > 0 ? (long)(TimeSpan.TicksPerSecond / FrameRate) : 0;
        frameIndex++;

        return new DemuxerPacket(0, pts, pts, duration, packet.IsKeyframe, packet.Data);
    }

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A YUV4MPEG2 file: a text header, then a FRAME line and a whole uncompressed picture per
/// frame.
/// </summary>
/// <remarks>
/// The fourcc is synthesised from the header's colour space, because that is what the
/// rawvideo decoder reads - a Y4M states its format as text ("C420mpeg2") where every other
/// container states it as four bytes, and the decoder is a translation of rawdec.c, which
/// knows only the four bytes.
/// </remarks>
internal sealed class RawVideoSource : IElementaryVideoSource
{
    private readonly RawVideoDemuxer demuxer;
    private readonly RawVideoInfo info;

    public RawVideoSource(RawVideoDemuxer raw, RawVideoInfo parsed)
    {
        demuxer = raw;
        info = parsed;
    }

    public string CodecId => "rawvideo";

    public int Width => info.Width;

    public int Height => info.Height;

    public double FrameRate => info.FramesPerSecond;

    public TimeSpan Duration => info.TotalFrames > 0 && info.FramesPerSecond > 0
        ? TimeSpan.FromSeconds(info.TotalFrames / info.FramesPerSecond)
        : TimeSpan.Zero;

    /// <summary>
    /// <c>MKTAG</c> for the format the Y4M header named. An unmapped one is left at zero,
    /// which makes the decoder refuse the stream by name rather than read it as something
    /// close - see RawDec's scope note.
    /// </summary>
    public uint CodecTag => info.Format switch
    {
        RawVideoFormat.I420 => MkTag('I', '4', '2', '0'),
        RawVideoFormat.I422 => MkTag('Y', '4', '2', 'B'),
        RawVideoFormat.I444 => MkTag('Y', '4', '2', '4'),
        RawVideoFormat.Nv12 => MkTag('N', 'V', '1', '2'),
        RawVideoFormat.Nv21 => MkTag('N', 'V', '2', '1'),
        RawVideoFormat.Yuy2 => MkTag('Y', 'U', 'Y', '2'),
        RawVideoFormat.Uyvy => MkTag('U', 'Y', 'V', 'Y'),
        RawVideoFormat.Gray8 => MkTag('Y', '8', '0', '0'),
        RawVideoFormat.Rgb24 => MkTag('R', 'G', 'B', '2'),
        RawVideoFormat.Bgr24 => MkTag('B', 'G', 'R', '3'),
        _ => 0,
    };

    private static uint MkTag(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    public DemuxerPacket? ReadFrame()
    {
        RawVideoFrame? frame = demuxer.ReadFrame();
        if (frame is null)
            return null;

        return new DemuxerPacket(
            0, frame.PresentationTimestamp, frame.PresentationTimestamp, frame.Duration,
            isKeyframe: true, frame.Data);
    }

    public void Dispose() => demuxer.Dispose();
}

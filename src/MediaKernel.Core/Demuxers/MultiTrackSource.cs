// The containers that hold a video track and an audio track and nothing else, read front to
// back - and the last two demuxers in this project that DecodePipeline could not reach.
//
// WHY A THIRD SHAPE. The pipeline knows three already: a demuxer with a track table and a
// sample index (MP4, Matroska, AVI, the two MPEG streams, FLV, Ogg, ASF), each with a field and
// an arm of its own; an IElementaryAudioSource for a container holding one audio track; and now
// an IElementaryVideoSource for one holding one video track. SMJPEG and THP are neither: each
// holds exactly two tracks and no index, interleaved, with one read handing back whichever came
// next.
//
// Both have working demuxers with passing gates of their own - SmjpegDemuxer was translated
// from smjpegdec.c and ThpDemuxer from thp.c - and both answered "Demuxer type X is not yet
// supported in the decode pipeline" when the playability sweep opened them. A fourth field and
// a fourth arm would have done it; one interface that says "a handful of tracks and no index"
// does it for the next such container too.

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// A container with a small fixed set of tracks and no sample index: one read hands back the
/// next packet of whichever track it belongs to.
/// </summary>
internal interface IMultiTrackSource : IDisposable
{
    /// <summary>The tracks, in the order the container declares them.</summary>
    IReadOnlyList<DemuxerTrackInfo> Tracks { get; }

    /// <summary>Stream duration, or <see cref="TimeSpan.Zero"/> when it cannot be known.</summary>
    TimeSpan Duration => TimeSpan.Zero;

    /// <summary>
    /// The next packet, with <see cref="DemuxerPacket.TrackId"/> saying which track it is, or
    /// null at end of stream. The payload may be a window onto the source's own buffer - see
    /// the lifetime note on <see cref="DemuxerPacket.Data"/>.
    /// </summary>
    DemuxerPacket? ReadFrame();
}

/// <summary>
/// A Loki SDL Motion JPEG: a text header, a chunk chain, and Motion JPEG interleaved with IMA
/// ADPCM.
/// </summary>
internal sealed class SmjpegSource : IMultiTrackSource
{
    private readonly SmjpegDemuxer demuxer;
    private readonly List<DemuxerTrackInfo> tracks = [];

    public SmjpegSource(SmjpegDemuxer smjpeg)
    {
        demuxer = smjpeg;

        // The stream indices are the demuxer's own - _VID_ is 0 and _SND_ is 1 when both are
        // present - and they are what its packets carry, so they are used as the track ids
        // rather than renumbered.
        if (smjpeg.Video is SmjpegStream video)
        {
            tracks.Add(new DemuxerTrackInfo
            {
                TrackId = video.Index,
                TrackType = DemuxerTrackType.Video,
                CodecId = video.CodecId,
                IsDefault = true,
                Width = smjpeg.Width,
                Height = smjpeg.Height,
                CodecTag = video.CodecTag,
            });
        }

        if (smjpeg.Audio is SmjpegStream audio)
        {
            tracks.Add(new DemuxerTrackInfo
            {
                TrackId = audio.Index,
                TrackType = DemuxerTrackType.Audio,
                CodecId = audio.CodecId,
                SampleRate = smjpeg.SampleRate,
                ChannelCount = smjpeg.Channels,
                BitsPerCodedSample = smjpeg.BitsPerCodedSample,
                CodecTag = audio.CodecTag,
            });
        }
    }

    public IReadOnlyList<DemuxerTrackInfo> Tracks => tracks;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(demuxer.DurationMs);

    public DemuxerPacket? ReadFrame()
    {
        if (!demuxer.ReadPacket(out SmjpegPacket packet))
            return null;

        long pts = packet.Pts * TimeSpan.TicksPerMillisecond;
        return new DemuxerPacket(
            packet.StreamIndex, pts, pts, 0, isKeyframe: true, packet.Data);
    }

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A GameCube THP: a frame-offset chain rather than an index, with each frame holding its
/// video component and, when the file has audio, its audio component beside it.
/// </summary>
internal sealed class ThpSource : IMultiTrackSource
{
    private readonly ThpDemuxer demuxer;
    private readonly List<DemuxerTrackInfo> tracks = [];
    private readonly double frameRate;

    public ThpSource(ThpDemuxer thp)
    {
        demuxer = thp;
        frameRate = thp.FpsDen > 0 ? (double)thp.FpsNum / thp.FpsDen : 0;

        tracks.Add(new DemuxerTrackInfo
        {
            TrackId = thp.VideoStreamIndex,
            TrackType = DemuxerTrackType.Video,
            CodecId = thp.VideoCodecId,
            IsDefault = true,
            Width = thp.Width,
            Height = thp.Height,
            FrameRate = frameRate,
        });

        if (thp.AudioStreamIndex >= 0)
        {
            tracks.Add(new DemuxerTrackInfo
            {
                TrackId = thp.AudioStreamIndex,
                TrackType = DemuxerTrackType.Audio,
                CodecId = thp.AudioCodecId,
                SampleRate = thp.SampleRate,
                ChannelCount = thp.Channels,
            });
        }
    }

    public IReadOnlyList<DemuxerTrackInfo> Tracks => tracks;

    public TimeSpan Duration => frameRate > 0
        ? TimeSpan.FromSeconds(demuxer.FrameCount / frameRate)
        : TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        if (!demuxer.ReadPacket(out ThpPacket packet))
            return null;

        // A THP states a frame rate and nothing else: there are no timestamps in the file, so
        // the frame index is the timestamp, which is what thp.c does.
        long pts = frameRate > 0
            ? (long)(packet.FrameIndex * TimeSpan.TicksPerSecond / frameRate)
            : 0;
        long duration = frameRate > 0 ? (long)(TimeSpan.TicksPerSecond / frameRate) : 0;

        return new DemuxerPacket(
            packet.StreamIndex, pts, pts, duration, isKeyframe: true, packet.Data);
    }

    public void Dispose() => demuxer.Dispose();
}

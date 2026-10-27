// libavformat/seek.c, the part of it that seeks a format with no index: ff_seek_frame_binary,
// ff_gen_search and ff_find_last_ts.
//
// In FFmpeg these are generic code in seek.c and every demuxer that cannot index itself hands
// them a read_timestamp callback - mpegps_read_dts, mpegts_get_dts, and a dozen others. They
// were first translated as private methods of MpegPsDemuxer, because the program stream was the
// first file here that needed them; the transport stream is the second, and a second copy would
// be two C# methods for one C function and two places for a fault to be fixed in. They live
// here now, with the callback the C has, and each demuxer keeps only its own read_timestamp.
//
// What this replaced in both demuxers was a byte position computed from a stated bitrate:
// fifty times too small in the program stream (program_mux_rate is in units of 50 bytes per
// second) and a hardcoded 20 Mbit/s in the transport stream, which is 13.6 times the real rate
// of the only transport stream in this tree. Neither seek landed anywhere near its target.

using System;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// <c>FFInputFormat.read_timestamp</c>: the decode timestamp of the first packet of one stream
/// at or after a byte position, and where that packet starts.
/// </summary>
/// <param name="streamId">The stream whose timestamps are wanted; the others are skipped.</param>
/// <param name="position">
/// On entry, where to start reading. On return, the start of the packet the timestamp came
/// from - which is what the search narrows its bounds with.
/// </param>
/// <param name="positionLimit">
/// How far to read before giving up. The C passes <c>INT64_MAX</c> everywhere but the backward
/// walk of <see cref="FormatSeek.FindLastTimestamp"/>, where a bounded window is the point.
/// </param>
internal delegate long ReadTimestampCallback(int streamId, ref long position, long positionLimit);

/// <summary>
/// The index-free seek: find a byte position by reading timestamps out of the file and narrowing
/// in on the target.
/// </summary>
internal static class FormatSeek
{
    /// <summary>No timestamp: <c>AV_NOPTS_VALUE</c>.</summary>
    public const long NoTimestamp = long.MinValue;

    /// <summary>
    /// <c>ff_seek_frame_binary</c> with no index: the byte position to resume reading from so
    /// that the stream starts at or before <paramref name="targetTimestamp"/>.
    /// </summary>
    public static long SeekFrameBinary(int streamId, long targetTimestamp, long fileSize,
                                       ReadTimestampCallback readTimestamp)
    {
        long positionMin = 0;
        long positionMax = 0;
        long positionLimit = -1;
        long timestampMin = NoTimestamp;
        long timestampMax = NoTimestamp;

        return GenSearch(streamId, targetTimestamp, positionMin, positionMax, positionLimit,
                         timestampMin, timestampMax, fileSize, readTimestamp);
    }

    /// <summary>
    /// <c>ff_gen_search</c>: narrows a byte range by reading the timestamp at a position,
    /// interpolating rather than bisecting while that keeps making progress.
    /// </summary>
    /// <remarks>
    /// The three-stage search is the C's and is there for a reason: interpolation converges in
    /// a couple of reads on a stream with a steady bitrate, bisection takes over when
    /// interpolation stops moving the bounds, and a linear walk is the last resort when there
    /// are too few timestamps between the two for either to split them.
    /// </remarks>
    public static long GenSearch(int streamId, long targetTimestamp,
                                 long positionMin, long positionMax, long positionLimit,
                                 long timestampMin, long timestampMax,
                                 long fileSize, ReadTimestampCallback readTimestamp)
    {
        if (timestampMin == NoTimestamp)
        {
            positionMin = 0;
            timestampMin = readTimestamp(streamId, ref positionMin, long.MaxValue);
            if (timestampMin == NoTimestamp)
                return -1;
        }

        if (timestampMin >= targetTimestamp)
            return positionMin;

        if (timestampMax == NoTimestamp)
        {
            if (!FindLastTimestamp(streamId, fileSize, readTimestamp,
                                   out timestampMax, out positionMax))
                return -1;
            positionLimit = positionMax;
        }

        if (timestampMax <= targetTimestamp)
            return positionMax;

        int noChange = 0;

        while (positionMin < positionLimit)
        {
            long position;

            if (noChange == 0)
            {
                // "interpolate position (better than dichotomy)"
                long approximateKeyframeDistance = positionMax - positionLimit;
                position = AvRescale(targetTimestamp - timestampMin,
                                     positionMax - positionMin,
                                     timestampMax - timestampMin)
                         + positionMin - approximateKeyframeDistance;
            }
            else if (noChange == 1)
            {
                // "bisection if interpolation did not change min / max pos last time"
                position = (positionMin + positionLimit) >> 1;
            }
            else
            {
                // "linear search if bisection failed, can only happen if there are very few or
                // no keyframes between min/max"
                position = positionMin;
            }

            if (position <= positionMin)
                position = positionMin + 1;
            else if (position > positionLimit)
                position = positionLimit;

            long startPosition = position;
            long timestamp = readTimestamp(streamId, ref position, long.MaxValue);

            noChange = position == positionMax ? noChange + 1 : 0;

            if (timestamp == NoTimestamp)
                return -1;

            if (targetTimestamp <= timestamp)
            {
                positionLimit = startPosition - 1;
                positionMax = position;
                timestampMax = timestamp;
            }

            if (targetTimestamp >= timestamp)
            {
                positionMin = position;
                timestampMin = timestamp;
            }
        }

        // AVSEEK_FLAG_BACKWARD, which is what a seek before decoding wants: land at or before
        // the target and let the trim throw away what precedes it.
        return positionMin;
    }

    /// <summary>
    /// <c>ff_find_last_ts</c>: the last timestamp in the file, found by stepping back from the
    /// end in doubling jumps until one turns up.
    /// </summary>
    public static bool FindLastTimestamp(int streamId, long fileSize,
                                         ReadTimestampCallback readTimestamp,
                                         out long timestamp, out long position)
    {
        long step = 1024;
        long positionMax = fileSize - 1;
        long limit;
        long timestampMax;

        do
        {
            limit = positionMax;
            positionMax = Math.Max(0, positionMax - step);
            timestampMax = readTimestamp(streamId, ref positionMax, limit);
            step += step;
        }
        while (timestampMax == NoTimestamp && 2 * limit > step);

        if (timestampMax == NoTimestamp)
        {
            timestamp = NoTimestamp;
            position = -1;
            return false;
        }

        // Then forward from there, so the figure really is the LAST one and not just the last
        // one in the window that happened to be searched.
        while (true)
        {
            long nextPosition = positionMax + 1;
            long nextTimestamp = readTimestamp(streamId, ref nextPosition, long.MaxValue);

            if (nextTimestamp == NoTimestamp || nextPosition <= positionMax)
                break;

            timestampMax = nextTimestamp;
            positionMax = nextPosition;

            if (nextPosition >= fileSize)
                break;
        }

        timestamp = timestampMax;
        position = positionMax;
        return true;
    }

    /// <summary>
    /// <c>av_rescale</c> with the default rounding: to nearest, halves away from zero.
    /// </summary>
    public static long AvRescale(long value, long multiplier, long divisor)
    {
        if (divisor == 0)
            return 0;

        Int128 product = (Int128)value * multiplier;
        Int128 half = divisor / 2;
        return (long)((product >= 0 ? product + half : product - half) / divisor);
    }
}

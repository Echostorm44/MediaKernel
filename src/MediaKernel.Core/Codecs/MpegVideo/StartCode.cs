// The MPEG-family start code scanner - a translation of avpriv_find_start_code from
// ffmpeg-master/libavcodec/utils.c, declared in startcode.h.
//
// It lives beside the MPEG-video code rather than inside one codec because it is shared: VC-1's
// find_next_marker is its only caller here so far, and MPEG-1/2, MPEG-4 and the parsers all use
// it in FFmpeg.

using System;
using System.Buffers.Binary;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Start code scanning (<c>utils.c</c>, <c>startcode.h</c>).</summary>
public static class StartCode
{
    /// <summary>
    /// <c>avpriv_find_start_code</c> - scans <paramref name="buffer"/> from
    /// <paramref name="start"/> for a three-byte 00 00 01 prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the index just past the start code's fourth byte, or <paramref name="end"/> if
    /// none was found, and leaves the last four bytes read in <paramref name="state"/> - so a
    /// caller tests <c>state</c> for the code it wanted and backs up four from the return.
    /// </para>
    /// <para>
    /// The three-step loop at the top is not an optimisation: it feeds the caller's carried
    /// <c>state</c> one byte at a time so a start code split across two calls is still found,
    /// and it is the reason the scan can then assume three bytes of history behind <c>p</c>.
    /// </para>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg's SIMD <c>ff_startcode_find_candidate</c> is not ported, and
    /// neither is the scalar <c>ff_startcode_find_candidate_c</c> it falls back to - they are
    /// used by the parsers, not by this function, which does its own three-at-a-time walk.
    /// </para>
    /// </remarks>
    /// <param name="buffer">The bytes to scan.</param>
    /// <param name="start">Where to start, inclusive.</param>
    /// <param name="end">Where to stop, exclusive.</param>
    /// <param name="state">The last four bytes read, carried in and out.</param>
    /// <returns>The index just past the start code, or <paramref name="end"/>.</returns>
    public static int AvprivFindStartCode(
        ReadOnlySpan<byte> buffer, int start, int end, ref uint state)
    {
        if (start > end)
            throw new ArgumentOutOfRangeException(nameof(start), "p must not run past end.");

        int p = start;
        if (p >= end)
            return end;

        for (int i = 0; i < 3; i++)
        {
            uint tmp = state << 8;
            state = tmp + buffer[p++];
            if (tmp == 0x100 || p == end)
                return p;
        }

        while (p < end)
        {
            if (buffer[p - 1] > 1)
                p += 3;
            else if (buffer[p - 2] != 0)
                p += 2;
            else if ((buffer[p - 3] | (buffer[p - 1] - 1)) != 0)
                p++;
            else
            {
                p++;
                break;
            }
        }

        p = Math.Min(p, end) - 4;
        state = BinaryPrimitives.ReadUInt32BigEndian(buffer[p..]);

        return p + 4;
    }
}

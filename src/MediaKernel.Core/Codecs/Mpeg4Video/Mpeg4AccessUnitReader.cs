// Translated from ffmpeg-master/libavcodec/mpeg4video_parser.c's mpeg4_find_frame_end, with
// the ff_combine_frame loop around it that av_parser_parse2 provides.
//
// WHY A SECOND SPLITTER. Mpeg4VideoDemuxer frames a .m4v with a packetizer written from
// format notes, and it disagrees with ffmpeg: on TestMedia/Video-mpeg4-qcif.m4v it produces 47
// packets where ffmpeg produces 30 frames, and the pictures stop matching at frame 16. The
// same thing happened to VC-1 - see Vc1AccessUnitReader, which exists for the same reason and
// says so - and the lesson recorded from that one is the one being applied here: framing is
// part of the port, so port the parser and gate on whole-file frame counts.
//
// WHAT THE C DOES, in the order it does it:
//
//   find a VOP start code (0x000001B6); then scan on for the NEXT start code that is not a
//   slice (0x1B7) or an extension (0x1B8), and end the access unit three bytes before it.
//
// The headers before the first VOP - the visual object sequence, the visual object and the
// video object layer - therefore travel with the first access unit, which is what the decoder
// needs: ff_mpeg4_parse_picture_header scans forward through them.
//
// END OF INPUT IS THE END OF A FRAME, which is the half a hand-written splitter always
// forgets: `if (buf_size == 0) return 0`. Without it the last picture of every file is lost,
// and a thirty-frame comparison that stops before the end cannot see it.

using System;
using System.IO;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// Splits an MPEG-4 Part 2 elementary stream into access units, one picture each.
/// </summary>
public sealed class Mpeg4AccessUnitReader : IDisposable
{
    /// <summary><c>VOP_STARTCODE</c>.</summary>
    private const uint VopStartCode = 0x1B6;

    /// <summary><c>SLICE_STARTCODE</c>.</summary>
    private const uint SliceStartCode = 0x1B7;

    /// <summary><c>EXT_STARTCODE</c>.</summary>
    private const uint ExtStartCode = 0x1B8;

    private const int ChunkSize = 16384;

    private readonly Stream stream;
    private readonly bool leaveOpen;

    private readonly byte[] chunk = new byte[ChunkSize];
    private int chunkLength;
    private int chunkOffset;
    private bool endOfStream;
    private bool flushed;

    // ff_combine_frame's buffer: the bytes of the access unit being assembled, which can span
    // any number of reads.
    private byte[] assembled = new byte[1 << 16];
    private int assembledLength;

    // ParseContext's two fields of state, which have to survive between calls because a start
    // code can straddle a read.
    private bool frameStartFound;
    private uint state = unchecked((uint)-1);

    private bool disposed;

    /// <summary>Opens a reader over an MPEG-4 Part 2 elementary stream.</summary>
    public Mpeg4AccessUnitReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Reads until one whole access unit is available.
    /// </summary>
    /// <returns>True when <paramref name="unit"/> holds one; false at end of stream.</returns>
    public bool ReadAccessUnit(out ReadOnlyMemory<byte> unit)
    {
        unit = default;

        while (true)
        {
            if (chunkOffset >= chunkLength)
            {
                if (endOfStream)
                {
                    if (flushed)
                        return false;

                    // The flush. End of input ends the access unit in progress, and whatever
                    // has been assembled is the last picture.
                    flushed = true;
                    if (assembledLength > 0)
                    {
                        unit = new ReadOnlyMemory<byte>(assembled, 0, assembledLength);
                        assembledLength = 0;
                        return true;
                    }
                    return false;
                }

                chunkLength = stream.Read(chunk, 0, ChunkSize);
                chunkOffset = 0;
                if (chunkLength == 0)
                {
                    endOfStream = true;
                    continue;
                }
            }

            int available = chunkLength - chunkOffset;
            int end = Mpeg4FindFrameEnd(chunk.AsSpan(chunkOffset, available));

            if (end < 0)
            {
                // END_NOT_FOUND: the whole of what is left belongs to this access unit.
                Append(chunk.AsSpan(chunkOffset, available));
                chunkOffset = chunkLength;
                continue;
            }

            // The C's `return i - 3` can be NEGATIVE, meaning the access unit ended in the
            // bytes already assembled - three of them belong to the next one. ff_combine_frame
            // handles that by taking those bytes back off the end of the buffer, and so does
            // this.
            if (end >= 0)
            {
                Append(chunk.AsSpan(chunkOffset, end));
                chunkOffset += end;
            }

            if (assembledLength > 0)
            {
                unit = new ReadOnlyMemory<byte>(assembled, 0, assembledLength);
                assembledLength = 0;
                return true;
            }
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        if (assembledLength + bytes.Length > assembled.Length)
            Array.Resize(ref assembled, Math.Max(assembledLength + bytes.Length,
                                                 assembled.Length * 2));
        bytes.CopyTo(assembled.AsSpan(assembledLength));
        assembledLength += bytes.Length;
    }

    /// <summary>
    /// <c>mpeg4_find_frame_end</c>. Returns the offset within <paramref name="buf"/> at which
    /// the current access unit ends, or a negative value for END_NOT_FOUND.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: the C can return a value three less than the index it found, which may be
    /// negative when the start code straddled the previous read; it leaves the caller to
    /// give those bytes back. This clamps the return to zero and keeps the three bytes with
    /// the NEXT access unit by not consuming them here - the start code is rediscovered on
    /// the following call, because `state` carries across exactly as the C's does.
    /// </remarks>
    private int Mpeg4FindFrameEnd(ReadOnlySpan<byte> buf)
    {
        bool vopFound = frameStartFound;
        uint current = state;

        int i = 0;
        if (!vopFound)
        {
            for (i = 0; i < buf.Length; i++)
            {
                current = (current << 8) | buf[i];
                if (current == VopStartCode)
                {
                    i++;
                    vopFound = true;
                    break;
                }
            }
        }

        if (vopFound)
        {
            // EOF considered as end of frame
            if (buf.Length == 0)
            {
                frameStartFound = false;
                state = unchecked((uint)-1);
                return 0;
            }

            for (; i < buf.Length; i++)
            {
                current = (current << 8) | buf[i];
                if ((current & 0xFFFFFF00) == 0x100)
                {
                    if (current == SliceStartCode || current == ExtStartCode)
                        continue;

                    frameStartFound = false;
                    state = unchecked((uint)-1);
                    return Math.Max(0, i - 3);
                }
            }
        }

        frameStartFound = vopFound;
        state = current;
        return -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (!leaveOpen)
            stream.Dispose();
    }
}

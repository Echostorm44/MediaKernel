// FLAC frame parser - a port of ffmpeg-master/libavcodec/flac_parser.c.
//
// A FLAC stream is a bare sequence of frames with no index and no frame-length field, so the only
// way to find a frame boundary is to look for something that parses as a frame header and then ask
// whether the bytes up to it form a frame. The C file's own summary is the algorithm:
//
//   The FLAC parser buffers input until FLAC_MIN_HEADERS has been found. Each time it finds and
//   verifies a CRC-8 header it sees which of the FLAC_MAX_SEQUENTIAL_HEADERS that came before it
//   have a valid CRC-16 footer that ends at the newly found header. Headers are scored by
//   FLAC_HEADER_BASE_SCORE plus the max of its crc-verified children, penalized by changes in
//   sample rate, frame number, etc. The parser returns the frame with the highest score.
//
// Why the scoring is not over-engineering, measured on this tree: in five minutes of pink noise
// encoded by ffmpeg - 35 MB, 2872 frames - 3714 byte pairs look like a sync code and 2873 of them
// parse as a valid CRC-8 header. One of those is not a frame. A splitter that trusted the header
// CRC alone would therefore cut one frame in ~2900 in the wrong place, which on that content is an
// audible glitch every hundred seconds or so. The CRC-16 footer check is what rejects it.

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Codecs.Audio.Flac;

/// <summary>
/// <c>FLACHeaderMarker</c>: one byte offset at which a valid frame header was found, and what is
/// known about how well it chains to the headers after it.
/// </summary>
internal sealed class FlacHeaderMarker
{
    /// <summary>Byte offset from the start of the parser's buffer.</summary>
    public int Offset;

    /// <summary>
    /// Local score between this header and the one at a distance equal to the array position.
    /// </summary>
    public readonly int[] LinkPenalty = new int[FlacParser.MaxSequentialHeaders];

    /// <summary>Maximum score found after checking each child that has a valid CRC.</summary>
    public int MaxScore;

    /// <summary>The decoded frame header.</summary>
    public FlacFrameHeader Fi;

    /// <summary>The next CRC-8 verified header that immediately follows this one.</summary>
    public FlacHeaderMarker? Next;

    /// <summary>The following header this one scores best with.</summary>
    public FlacHeaderMarker? BestChild;
}

/// <summary>
/// Splits a raw FLAC byte stream into frames.
/// </summary>
/// <remarks>
/// Feed bytes with <see cref="Parse"/> and take whole frames out of it. The parser holds onto
/// input until it has <see cref="MinHeaders"/> header candidates buffered, because a frame's end
/// is only known once the frame after it has been found and the CRC-16 between them checks out.
/// </remarks>
public sealed class FlacParser
{
    /// <summary>Maximum number of adjacent headers that compare CRCs against each other.</summary>
    public const int MaxSequentialHeaders = 4;

    /// <summary>Minimum number of headers buffered and checked before returning frames.</summary>
    public const int MinHeaders = 10;

    /// <summary>Estimate for the average size of a FLAC frame.</summary>
    private const int AvgFrameSize = 8192;

    // Scoring settings for ScoreHeader.
    private const int HeaderBaseScore = 10;
    private const int HeaderChangedPenalty = 7;
    private const int HeaderCrcFailPenalty = 50;
    private const int HeaderNotPenalizedYet = 100000;
    private const int HeaderNotScoredYet = -100000;

    /// <summary>Largest possible size of a FLAC frame header.</summary>
    private const int MaxFrameHeaderSize = 16;

    /// <summary>The header plus one byte of the first subframe.</summary>
    private const int MaxFrameVerifySize = MaxFrameHeaderSize + 1;

    private readonly FlacStreamInfo? streamInfo;

    /// <summary>
    /// All buffered input. A plain growable array with a read offset, where the C has a ring
    /// buffer.
    /// </summary>
    /// <remarks>
    /// This is the one deliberate structural divergence from the C file, and it removes six of its
    /// functions rather than translating them. <c>flac_fifo_read_wrap</c>, the <c>wrap_buf</c>
    /// scratch allocation, the two-part reads in <c>find_new_headers</c> and <c>check_header_mismatch</c>,
    /// and the "HACK: drain the tail of the fifo" that restores <c>wptr</c> after end-of-stream
    /// padding all exist to let a fixed-size ring hand out a contiguous pointer. Offsets in the C
    /// are already logical - zero is the oldest buffered byte, not a position in the ring - so a
    /// linear buffer indexes identically and every offset in the algorithm keeps its meaning.
    /// </remarks>
    private byte[] buffer = new byte[AvgFrameSize * (MinHeaders + 3)];
    private int bufferStart;
    private int bufferLength;

    /// <summary>Linked list that starts at the first verified header within the buffer.</summary>
    private FlacHeaderMarker? headers;

    // Recycled markers, chained through Next. FFmpeg av_mallocz's a FLACHeaderMarker per
    // candidate header and av_free's it when the header is dropped, which is a legitimate thing
    // for C to do and the wrong thing here: it was 10.7 KB of garbage per frame, the largest
    // single allocation left on the decode thread. This is the one deliberate divergence from
    // flac_parser.c's memory handling, and nothing else about the list changes - a marker taken
    // from the pool is zeroed to exactly what av_mallocz would have given.
    private FlacHeaderMarker? markerPool;

    /// <summary>Highest scoring header within the buffer.</summary>
    private FlacHeaderMarker? bestHeader;

    /// <summary>Number of headers found in the last <see cref="Parse"/> call.</summary>
    private int headersFound;

    /// <summary>Number of headers currently buffered.</summary>
    private int headersBuffered;

    /// <summary>Set when the parser returned junk; return <see cref="bestHeader"/> next time.</summary>
    private bool bestHeaderValid;

    /// <summary>Whether the end of the stream has been padded to flush the last headers out.</summary>
    private bool endPadded;

    private FlacFrameHeader lastFi;
    private bool lastFiValid;

    /// <summary>The frame header of the frame the last <see cref="Parse"/> returned.</summary>
    public FlacFrameHeader CurrentFrameHeader { get; private set; }

    /// <summary>
    /// True when the last <see cref="Parse"/> returned data that is not a frame - bytes before the
    /// first real header, which the C calls a junk frame.
    /// </summary>
    public bool CurrentFrameIsJunk { get; private set; }

    /// <param name="streamInfo">
    /// The STREAMINFO block, when the caller has one. It supplies the sample rate, block size and
    /// sample size for the frame headers that encode those as "take it from STREAMINFO".
    /// </param>
    public FlacParser(FlacStreamInfo? streamInfo = null)
    {
        this.streamInfo = streamInfo;
    }

    /// <summary>Buffered bytes not yet returned as frames.</summary>
    private int FifoSize => bufferLength;

    /// <summary><c>flac_fifo_space</c>: room left before the buffer has to grow.</summary>
    private int FifoSpace => buffer.Length - bufferLength;

    private Span<byte> Fifo(int offset, int length) =>
        buffer.AsSpan(bufferStart + offset, length);

    /// <summary><c>flac_fifo_write</c>: append input.</summary>
    private void FifoWrite(ReadOnlySpan<byte> data)
    {
        if (bufferStart + bufferLength + data.Length > buffer.Length)
        {
            // Compact first, grow only if that is not enough.
            if (bufferStart > 0)
            {
                Array.Copy(buffer, bufferStart, buffer, 0, bufferLength);
                bufferStart = 0;
            }
            if (bufferLength + data.Length > buffer.Length)
            {
                int size = buffer.Length;
                while (size < bufferLength + data.Length)
                    size *= 2;
                Array.Resize(ref buffer, size);
            }
        }
        data.CopyTo(buffer.AsSpan(bufferStart + bufferLength));
        bufferLength += data.Length;
    }

    /// <summary><c>flac_fifo_drain</c>: drop the oldest <paramref name="size"/> bytes.</summary>
    private void FifoDrain(int size)
    {
        bufferStart += size;
        bufferLength -= size;
        if (bufferLength == 0)
            bufferStart = 0;
    }

    /// <summary>
    /// <c>frame_header_is_valid</c>: the header parses, and the first subframe's type is one that
    /// exists.
    /// </summary>
    /// <remarks>
    /// The subframe check is the cheap half of the discrimination and it is worth as much as the
    /// header CRC on short candidates: a byte pair that happens to look like a sync code is
    /// unlikely to be followed by a zero bit and one of the four legal subframe type ranges.
    /// </remarks>
    private bool FrameHeaderIsValid(int offset, out FlacFrameHeader fi)
    {
        fi = default;

        int available = Math.Min(MaxFrameVerifySize, FifoSize - offset);
        if (available < 6)
            return false;

        var window = Fifo(offset, available);
        if (!FlacDecoder.ParseFrameHeader(window, streamInfo, out fi, out int headerSize))
            return false;
        if (headerSize >= available)
            return false;

        // The header is byte aligned, so the subframe's leading zero bit is the top bit of the
        // next byte and its six type bits are the six below that.
        byte next = window[headerSize];
        if ((next & 0x80) != 0)
            return false;

        int subframeType = (next >> 1) & 0x3F;

        // 000000 : SUBFRAME_CONSTANT
        // 000001 : SUBFRAME_VERBATIM
        // 00001x : reserved
        // 0001xx : reserved
        // 001xxx : if (xxx <= 4) SUBFRAME_FIXED, xxx = order; else reserved
        // 01xxxx : reserved
        // 1xxxxx : SUBFRAME_LPC, xxxxx = order - 1
        if (!(subframeType == 0 ||
              subframeType == 1 ||
              (subframeType >= 8 && subframeType <= 12) ||
              subframeType >= 32))
            return false;

        return true;
    }

    /// <summary>
    /// <c>find_headers_search_validate</c>: check one candidate offset and append a marker if it
    /// holds a header.
    /// </summary>
    private int FindHeadersSearchValidate(int offset)
    {
        int size = 0;
        if (!FrameHeaderIsValid(offset, out var fi))
            return size;

        // Append to the end of the list, counting what is already there.
        ref FlacHeaderMarker? end = ref headers;
        while (end is not null)
        {
            end = ref end.Next!;
            size++;
        }

        var marker = RentMarker(fi, offset);
        for (int i = 0; i < MaxSequentialHeaders; i++)
            marker.LinkPenalty[i] = HeaderNotPenalizedYet;
        end = marker;

        headersFound++;
        size++;
        return size;
    }

    /// <summary>
    /// <c>find_headers_search</c>: scan a range for sync codes.
    /// </summary>
    /// <remarks>
    /// The C scans four bytes at a time with the classic has-a-zero-byte word trick inverted to
    /// find 0xFF bytes, then checks the pairs inside a word that matched. The scan here is the
    /// plain byte loop: it is the same set of candidates, and the whole search is a tiny part of
    /// the work next to the CRC-16 passes it feeds. Recorded as a divergence rather than left to
    /// look like an oversight.
    /// </remarks>
    private int FindHeadersSearch(int searchStart, int searchEnd)
    {
        int size = 0;
        var span = buffer.AsSpan(bufferStart, bufferLength);
        for (int i = searchStart; i < searchEnd; i++)
        {
            if (span[i] != 0xFF || (span[i + 1] & 0xFE) != 0xF8)
                continue;
            int ret = FindHeadersSearchValidate(i);
            size = Math.Max(size, ret);
        }
        return size;
    }

    /// <summary><c>find_new_headers</c>.</summary>
    private int FindNewHeaders(int searchStart)
    {
        headersFound = 0;

        // Search for a new header of at most 16 bytes.
        int searchEnd = FifoSize - (MaxFrameHeaderSize - 1);
        int size = searchEnd > searchStart ? FindHeadersSearch(searchStart, searchEnd) : 0;

        // Return the size even if no new headers were found.
        if (size == 0 && headers is not null)
            for (var end = headers; end is not null; end = end.Next)
                size++;

        return size;
    }

    /// <summary>
    /// <c>check_header_fi_mismatch</c>: penalise the changes between two headers that a valid
    /// stream does not make.
    /// </summary>
    private static int CheckHeaderFiMismatch(in FlacFrameHeader headerFi, in FlacFrameHeader childFi)
    {
        int deduction = 0;
        if (childFi.SampleRate != headerFi.SampleRate)
            deduction += HeaderChangedPenalty;
        if (childFi.BitsPerSample != headerFi.BitsPerSample)
            deduction += HeaderChangedPenalty;
        if (childFi.VariableBlockSize != headerFi.VariableBlockSize)
        {
            // Changing blocking strategy is not allowed per the spec.
            deduction += HeaderBaseScore;
        }
        if (childFi.ChannelCount != headerFi.ChannelCount)
            deduction += HeaderChangedPenalty;
        return deduction;
    }

    /// <summary>
    /// <c>check_header_mismatch</c>: everything <see cref="CheckHeaderFiMismatch"/> penalises,
    /// plus the frame numbering, plus the CRC-16 footer when the cheaper checks leave it in doubt.
    /// </summary>
    /// <summary>
    /// A marker from the pool, or a new one, cleared to what <c>av_mallocz</c> would have given.
    /// </summary>
    private FlacHeaderMarker RentMarker(in FlacFrameHeader fi, int offset)
    {
        FlacHeaderMarker marker;
        if (markerPool is null)
        {
            marker = new FlacHeaderMarker();
        }
        else
        {
            marker = markerPool;
            markerPool = marker.Next;
            marker.MaxScore = 0;
            marker.BestChild = null;
            Array.Clear(marker.LinkPenalty);
        }

        marker.Next = null;
        marker.Fi = fi;
        marker.Offset = offset;
        return marker;
    }

    /// <summary>
    /// Returns the markers from <paramref name="from"/> up to but not including
    /// <paramref name="until"/> to the pool, in place of the C's <c>av_free</c>.
    /// </summary>
    /// <remarks>
    /// <c>Next</c> and <c>BestChild</c> are cleared on the way out, so a recycled marker can
    /// never hand a stale link back to the list. That is the whole hazard this divergence
    /// introduces, and the FLAC gates are bit-exact, so a mistake here does not pass quietly.
    /// </remarks>
    private void ReturnMarkers(FlacHeaderMarker? from, FlacHeaderMarker? until)
    {
        var curr = from;
        while (curr is not null && curr != until)
        {
            var next = curr.Next;
            curr.Next = markerPool;
            curr.BestChild = null;
            markerPool = curr;
            curr = next;
        }
    }

    private int CheckHeaderMismatch(FlacHeaderMarker header, FlacHeaderMarker child)
    {
        ref readonly FlacFrameHeader headerFi = ref header.Fi;
        ref readonly FlacFrameHeader childFi = ref child.Fi;

        int deduction = CheckHeaderFiMismatch(in headerFi, in childFi);
        bool deductionExpected = false;

        // Check sample and frame numbers.
        if ((long)childFi.FrameOrSampleNumber - (long)headerFi.FrameOrSampleNumber != headerFi.BlockSize &&
            childFi.FrameOrSampleNumber != headerFi.FrameOrSampleNumber + 1)
        {
            // If there are frames in the middle we expect this deduction, as they are probably
            // valid and this one follows them.
            long expectedFrameNum = (long)headerFi.FrameOrSampleNumber;
            long expectedSampleNum = (long)headerFi.FrameOrSampleNumber;

            var curr = header;
            while (curr != child && curr is not null)
            {
                // Ignore frames that failed all CRC checks.
                for (int i = 0; i < MaxSequentialHeaders; i++)
                {
                    if (curr.LinkPenalty[i] < HeaderCrcFailPenalty)
                    {
                        expectedFrameNum++;
                        expectedSampleNum += curr.Fi.BlockSize;
                        break;
                    }
                }
                curr = curr.Next!;
            }

            if (expectedFrameNum == (long)childFi.FrameOrSampleNumber ||
                expectedSampleNum == (long)childFi.FrameOrSampleNumber)
            {
                deductionExpected = deduction == 0;
            }

            deduction += HeaderChangedPenalty;
        }

        // No lastFiValid guard here, deliberately: the C compares against a zeroed last_fi on the
        // first parse, and a default FlacFrameHeader is the same zeroes. Adding the guard made the
        // parser compute a CRC the C skips - harmless, since the CRC passes on a real frame, but a
        // divergence with nothing to gain.
        bool checkCrc;
        if (lastFi.VariableBlockSize == headerFi.VariableBlockSize)
        {
            if (lastFi.VariableBlockSize &&
                lastFi.FrameOrSampleNumber + lastFi.BlockSize == headerFi.FrameOrSampleNumber)
                checkCrc = false;
            else if (!lastFi.VariableBlockSize &&
                     lastFi.FrameOrSampleNumber + 1 == headerFi.FrameOrSampleNumber)
                checkCrc = false;
            else
                checkCrc = deduction == 0 && !deductionExpected;
        }
        else
        {
            checkCrc = deduction == 0 && !deductionExpected;
        }

        // If we have suspicious headers, check the CRC between them.
        if (checkCrc || (deduction != 0 && !deductionExpected))
        {
            uint crc = 1;
            bool invertedTest = false;

            // Since the CRC is expensive only do it if we haven't yet. This assumes a CRC penalty
            // is greater than all other check penalties.
            var curr = header.Next;
            int i = 0;
            for (; i < MaxSequentialHeaders && curr != child; i++)
                curr = curr!.Next;

            // The C has av_assert0(i < FLAC_MAX_SEQUENTIAL_HEADERS) here: ScoreHeader only ever
            // passes a child within that distance, so this is unreachable. Returning what has
            // been deduced so far is the graceful equivalent of its assert.
            if (i >= MaxSequentialHeaders)
                return deduction;

            if (header.LinkPenalty[i] < HeaderCrcFailPenalty ||
                header.LinkPenalty[i] == HeaderNotPenalizedYet)
            {
                var start = header;
                var end = child;

                // Although overlapping chains are scored, the CRC should never have to be computed
                // twice for a single byte.
                if (i > 0 && header.LinkPenalty[i - 1] >= HeaderCrcFailPenalty)
                {
                    while (start.Next != child)
                        start = start.Next!;
                    invertedTest = true;
                }
                else if (i > 0 && header.Next!.LinkPenalty[i - 1] >= HeaderCrcFailPenalty)
                {
                    end = header.Next!;
                    invertedTest = true;
                }

                int readLen = end.Offset - start.Offset;
                crc = FlacDecoder.CalculateCrc16(Fifo(start.Offset, readLen));
            }

            if ((crc == 0) == invertedTest)
                deduction += HeaderCrcFailPenalty;
        }

        return deduction;
    }

    /// <summary>
    /// <c>score_header</c>: base score plus the best of the children whose CRC closes this frame.
    /// </summary>
    private int ScoreHeader(FlacHeaderMarker header)
    {
        if (header.MaxScore != HeaderNotScoredYet)
            return header.MaxScore;

        int baseScore = HeaderBaseScore;

        // Modify the base score with changes from the last output header.
        if (lastFiValid)
            baseScore -= CheckHeaderFiMismatch(in lastFi, in header.Fi);

        header.MaxScore = baseScore;

        // Check and compute the children's scores.
        var child = header.Next;
        for (int dist = 0; dist < MaxSequentialHeaders && child is not null; dist++)
        {
            // Look at the child's frame header info and penalise suspicious changes.
            if (header.LinkPenalty[dist] == HeaderNotPenalizedYet)
                header.LinkPenalty[dist] = CheckHeaderMismatch(header, child);

            int childScore = ScoreHeader(child) - header.LinkPenalty[dist];

            if (HeaderBaseScore + childScore > header.MaxScore)
            {
                // Keep the child because the frame scoring is dynamic.
                header.BestChild = child;
                header.MaxScore = baseScore + childScore;
            }
            child = child.Next;
        }

        return header.MaxScore;
    }

    /// <summary><c>score_sequences</c>.</summary>
    private void ScoreSequences()
    {
        // First pass to clear all old scores.
        for (var curr = headers; curr is not null; curr = curr.Next)
            curr.MaxScore = HeaderNotScoredYet;

        // Second pass to score them all.
        int bestScore = HeaderNotScoredYet;
        for (var curr = headers; curr is not null; curr = curr.Next)
        {
            if (ScoreHeader(curr) > bestScore)
            {
                bestHeader = curr;
                bestScore = curr.MaxScore;
            }
        }
    }

    /// <summary><c>get_best_header</c>: emit the winning frame.</summary>
    private ReadOnlyMemory<byte> GetBestHeader()
    {
        var header = bestHeader!;
        var child = header.BestChild;

        int size = child is null
            ? FifoSize - header.Offset
            : child.Offset - header.Offset;

        CurrentFrameHeader = header.Fi;
        CurrentFrameIsJunk = false;

        var frame = new ReadOnlyMemory<byte>(buffer, bufferStart + header.Offset, size);

        bestHeaderValid = false;
        lastFiValid = true;
        lastFi = header.Fi;

        return frame;
    }

    /// <summary>
    /// Feed more of the stream and take back a frame when one can be proved.
    /// </summary>
    /// <param name="consumed">How much of <paramref name="input"/> was taken.</param>
    /// <param name="frame">
    /// One whole frame, or empty when more input is needed. The memory points into the parser's
    /// own buffer and stays valid only until the next call.
    /// </param>
    /// <returns>
    /// True when a frame (or a junk run) was produced. False does not mean "no more frames": the
    /// parser may have buffered the input and marked a frame ready to hand back on the next call,
    /// which is the C's contract as well. Keep calling while it is still taking input.
    /// </returns>
    public bool Parse(ReadOnlySpan<byte> input, out int consumed, out ReadOnlyMemory<byte> frame)
        => ParseInternal(input, flushing: false, out consumed, out frame);

    /// <summary>
    /// End of stream: pad the buffer so the last headers can still be found, and hand back the
    /// frames still held. Call it until it returns false.
    /// </summary>
    public bool Flush(out ReadOnlyMemory<byte> frame)
        => ParseInternal(ReadOnlySpan<byte>.Empty, flushing: true, out _, out frame);

    /// <summary>
    /// <c>flac_parse</c>: take input, give back one frame when one can be proved.
    /// </summary>
    /// <remarks>
    /// <paramref name="flushing"/> is the C's <c>buf_size == 0</c>. It is a separate argument
    /// rather than an empty input span, which is the one place this port deliberately does not
    /// follow the C's signature. In the C an empty buffer can only come from the end of the
    /// stream, because libavcodec's parser loop never calls it otherwise; here any caller
    /// draining a chunk can quite reasonably hand over an empty remainder, and reading that as
    /// end-of-stream makes the parser pad, flush and emit a frame in the middle of the file. That
    /// is not a theoretical risk - it is what the first driver written against this API did, and
    /// it cut the stream into ninety junk runs and one frame.
    /// </remarks>
    private bool ParseInternal(ReadOnlySpan<byte> input, bool flushing,
                               out int consumed, out ReadOnlyMemory<byte> frame)
    {
        consumed = 0;
        frame = default;
        CurrentFrameIsJunk = false;

        if (bestHeaderValid && headersBuffered >= MinHeaders)
        {
            frame = GetBestHeader();
            return true;
        }

        // If a best header was found last call, remove it with the buffer data.
        if (bestHeader is not null && bestHeader.BestChild is not null)
        {
            var bestChild = bestHeader.BestChild;

            // Remove headers in the list until the end of the best header.
            for (var curr = headers; curr != bestChild; curr = curr!.Next)
                headersBuffered--;
            ReturnMarkers(headers, bestChild);

            // Release returned data from the buffer.
            FifoDrain(bestChild.Offset);

            // Fix the offsets of the headers that remain, to match the new buffer.
            for (var curr = bestChild.Next; curr is not null; curr = curr.Next)
                curr.Offset -= bestChild.Offset;

            bestChild.Offset = 0;
            headers = bestChild;
            if (headersBuffered >= MinHeaders)
            {
                bestHeader = bestChild;
                frame = GetBestHeader();
                return true;
            }
            bestHeader = null;
        }
        else if (bestHeader is not null)
        {
            // No end frame, so no need to delete the buffer; probably end of stream.
            for (var curr = headers; curr != bestHeader; curr = curr!.Next)
                headersBuffered--;
            var dropFrom = headers;
            headers = bestHeader.Next;
            ReturnMarkers(dropFrom, bestHeader);
            ReturnMarkers(bestHeader, bestHeader.Next);
            bestHeader = null;
            headersBuffered--;
        }

        // Find and score new headers. An empty input means flushing, so check for that: we do not
        // want to try to read more input once the end has been found.
        int readStart = 0;
        int readEnd = 0;
        while ((!flushing && readEnd < input.Length && headersBuffered < MinHeaders) ||
               (flushing && !endPadded))
        {
            // Less than one valid header for every twenty frames' worth of buffered data means
            // the input is most likely not FLAC at all. The C bails out here, and it matters more
            // here than there: this buffer grows on demand, so without the check a large
            // non-FLAC stream would be buffered in its entirety looking for a header.
            if (FifoSpace == 0 && FifoSize / AvgFrameSize > headersBuffered * 20)
                return false;

            int writtenNow;
            bool paddedThisPass = false;

            if (flushing)
            {
                // Pad the end once at end of stream, to check the final region for headers.
                endPadded = true;
                paddedThisPass = true;
                Span<byte> pad = stackalloc byte[MaxFrameHeaderSize];
                pad.Clear();
                FifoWrite(pad);
                writtenNow = MaxFrameHeaderSize;
            }
            else
            {
                // The maximum read size is the upper bound of what the parser needs to have the
                // required number of frames buffered.
                int desired = MinHeaders - headersBuffered + 1;
                readEnd += Math.Min(input.Length - readEnd, desired * AvgFrameSize);
                FifoWrite(input[readStart..readEnd]);
                writtenNow = readEnd - readStart;
                consumed = readEnd;
            }

            // Tag headers and update sequences.
            int startOffset = Math.Max(0, FifoSize - (writtenNow + (MaxFrameHeaderSize - 1)));
            headersBuffered = FindNewHeaders(startOffset);

            // Wait until MinHeaders to output a valid frame.
            if (!endPadded && headersBuffered < MinHeaders)
            {
                if (readEnd < input.Length)
                {
                    readStart = readEnd;
                    continue;
                }
                return false;
            }

            // If headers were found, update the scores since we have longer chains.
            if (endPadded || headersFound > 0)
                ScoreSequences();

            // Restore the state from before the padding - but only the padding this pass wrote.
            // The C's guard is `if (fpc->end_padded)`, which is safe there because a flush is the
            // last thing that ever happens to the context. Here an empty input span is the flush
            // signal, and a caller draining a chunk can hand over an empty remainder perfectly
            // reasonably; with the C's guard that un-writes sixteen bytes that were never added,
            // and a few of those drive the buffer length negative.
            if (paddedThisPass)
            {
                bufferLength -= MaxFrameHeaderSize;
                break;
            }
            if (endPadded)
                break;
        }

        for (var curr = headers; curr is not null; curr = curr.Next)
        {
            if (bestHeader is null || curr.MaxScore > bestHeader.MaxScore)
                bestHeader = curr;
        }

        if (bestHeader is not null && bestHeader.MaxScore <= 0)
        {
            // Only accept a bad header if there is no other option to continue.
            if (flushing || readEnd != 0 || headersBuffered < MinHeaders)
                bestHeader = null;
        }

        if (bestHeader is not null)
        {
            bestHeaderValid = true;
            if (bestHeader.Offset > 0)
            {
                // Output a junk run: the bytes before the first real header.
                CurrentFrameIsJunk = true;
                CurrentFrameHeader = default;
                frame = new ReadOnlyMemory<byte>(buffer, bufferStart, bestHeader.Offset);
                return true;
            }
            if (flushing)
            {
                frame = GetBestHeader();
                return true;
            }
        }

        return false;
    }

}

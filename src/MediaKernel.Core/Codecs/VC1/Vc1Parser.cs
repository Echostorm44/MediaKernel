// Ported from FFmpeg's libavcodec/vc1_parser.c, with the two pieces of libavcodec/parser.c it
// stands on: ParseContext and ff_combine_frame.
//
// This is what turns a VC-1 elementary stream into the whole access units vc1_decode_frame
// expects. It is not a nicety: the start codes in the stream mark sequence headers, entry
// points, frames, fields, slices and user data, and only some of those begin a new picture.
// FFmpeg's rule is in vc1_parse and it is exact - a FIELD, SLICE or ENDOFSEQ marker CONTINUES
// the current access unit, so an interlaced frame's two fields are one unit, and anything else
// ends it. Reconstructing that rule by hand gets interlaced streams wrong by one picture per
// frame, which is how ffmpeg-master/tests/ref/fate/vc1_ilaced_twomv came to disagree with this
// port's hand-written packetizer.

using System;
using System.IO;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// <c>ParseContext</c> (<c>parser.h</c>) - the buffer a parser accumulates a partial access
/// unit in, plus the state it carries across calls.
/// </summary>
/// <remarks>
/// A class rather than a struct: it owns a growable buffer and there is exactly one per parser.
/// </remarks>
public sealed class ParseContext
{
    /// <summary><c>buffer</c> - the accumulated bytes of an access unit not yet complete.</summary>
    public byte[]? Buffer;

    /// <summary><c>index</c> - how much of <see cref="Buffer"/> is in use.</summary>
    public int Index;

    /// <summary><c>last_index</c> - <see cref="Index"/> as it was on entry, for the overread walk.</summary>
    public int LastIndex;

    /// <summary><c>state</c> - the last four bytes read, in MSB order.</summary>
    public uint State;

    /// <summary><c>frame_start_found</c>.</summary>
    public int FrameStartFound;

    /// <summary>
    /// <c>overread</c> - bytes irreversibly read from the next access unit, which the next
    /// call copies back to the front of the buffer.
    /// </summary>
    public int Overread;

    /// <summary><c>overread_index</c> - where those bytes are.</summary>
    public int OverreadIndex;

    /// <summary><c>state64</c> - the last eight bytes read, in MSB order.</summary>
    public ulong State64;

    /// <summary><c>END_NOT_FOUND</c>.</summary>
    public const int EndNotFound = -100;

    /// <summary>
    /// <c>ff_combine_frame</c> (<c>parser.c</c>) - accumulates input until an access unit is
    /// complete, then hands back the whole thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="next"/> is where the parser found the end of the access unit within
    /// this input, <see cref="EndNotFound"/> when it has not found one, or negative when the
    /// end lies that many bytes BEFORE the start of this input - which happens because a start
    /// code straddles two calls, and is what the overread machinery at the bottom exists for.
    /// </para>
    /// <para>
    /// On success the three <c>ref</c> parameters describe the complete access unit, which is
    /// either the caller's own input (when nothing had been accumulated) or this context's
    /// buffer. That is the C's <c>*buf</c> being redirected to <c>pc-&gt;buffer</c>.
    /// </para>
    /// <para>
    /// DIVERGENCE, recorded: where the C copies <c>next + AV_INPUT_BUFFER_PADDING_SIZE</c>
    /// bytes out of the input - reading into the padding every AVPacket is guaranteed to have -
    /// this clamps the copy to what the input actually holds and zeroes the rest, because a
    /// C# array has no such guarantee and the read would throw rather than return zeroes.
    /// </para>
    /// </remarks>
    /// <returns>0 when a complete access unit is available, -1 when more input is needed.</returns>
    public int CombineFrame(int next, ref byte[] buf, ref int bufOffset, ref int bufSize)
    {
        ArgumentNullException.ThrowIfNull(buf);

        // Copy overread bytes from last frame into buffer.
        for (; Overread > 0; Overread--)
            Buffer![Index++] = Buffer[OverreadIndex++];

        if (next > bufSize)
            return Vc1.AverrorEinval;

        // flush remaining if EOF
        if (bufSize == 0 && next == EndNotFound)
            next = 0;

        LastIndex = Index;

        // copy into buffer and return
        if (next == EndNotFound)
        {
            Grow(bufSize + Index + Vc1.InputBufferPaddingSize);
            buf.AsSpan(bufOffset, bufSize).CopyTo(Buffer.AsSpan(Index));
            Buffer.AsSpan(Index + bufSize, Vc1.InputBufferPaddingSize).Clear();
            Index += bufSize;
            return -1;
        }

        bufSize = OverreadIndex = Index + next;

        // append to buffer
        if (Index != 0)
        {
            Grow(next + Index + Vc1.InputBufferPaddingSize);
            if (next > -Vc1.InputBufferPaddingSize)
            {
                int want = next + Vc1.InputBufferPaddingSize;
                int have = Math.Max(0, Math.Min(want, buf.Length - bufOffset));
                buf.AsSpan(bufOffset, have).CopyTo(Buffer.AsSpan(Index));
                Buffer.AsSpan(Index + have, want - have).Clear();
            }
            Index = 0;
            buf = Buffer!;
            bufOffset = 0;
        }

        if (next < -8)
        {
            Overread += -8 - next;
            next = -8;
        }

        // store overread bytes
        for (; next < 0; next++)
        {
            State = State << 8 | Buffer![LastIndex + next];
            State64 = State64 << 8 | Buffer[LastIndex + next];
            Overread++;
        }

        return 0;
    }

    /// <summary>
    /// <c>av_fast_realloc</c> on the accumulation buffer: grows and keeps, never shrinks.
    /// </summary>
    private void Grow(int size)
    {
        if (Buffer is not null && Buffer.Length >= size)
            return;

        var grown = new byte[Math.Max(size, (Buffer?.Length ?? 0) * 2)];
        Buffer?.AsSpan(0, Index).CopyTo(grown);
        Buffer = grown;
    }

    /// <summary><c>ff_parse_close</c>, as far as it applies: drop the buffer.</summary>
    public void Close() => Buffer = null;
}

/// <summary>
/// <c>vc1_parser.c</c> - finds access-unit boundaries in a VC-1 elementary stream, and reads
/// each header it passes for the metadata a container would otherwise supply.
/// </summary>
public sealed class Vc1Parser
{
    /// <summary>
    /// <c>UNESCAPED_THRESHOLD</c> - the most bytes of a sequence, entry point or frame header
    /// whose values are paid any attention.
    /// </summary>
    private const int UnescapedThreshold = 37;

    /// <summary>
    /// <c>UNESCAPED_LIMIT</c> - the most bytes of a header that must be valid memory, because
    /// <c>skip_bits</c> refills its cache from them.
    /// </summary>
    private const int UnescapedLimit = 144;

    /// <summary><c>VC1ParseSearchState</c>.</summary>
    private enum SearchState
    {
        NoMatch,
        OneZero,
        TwoZeros,
        One,
    }

    /// <summary><c>pc</c>.</summary>
    public ParseContext Pc { get; } = new();

    /// <summary><c>v</c> - the headers read so far, which is where the metadata below comes from.</summary>
    public Vc1Context V { get; } = new();

    /// <summary><c>s->pict_type</c>, with BI kept internal to VC-1 as the C does.</summary>
    public AvPictureType PictType { get; private set; }

    /// <summary><c>s->repeat_pict</c>.</summary>
    public int RepeatPict { get; private set; }

    /// <summary><c>s->field_order</c>, as an <see cref="AvFieldOrder"/>.</summary>
    public AvFieldOrder FieldOrder { get; private set; } = AvFieldOrder.Progressive;

    /// <summary><c>s->width</c> and <c>s->height</c>, 0 until a header has supplied them.</summary>
    public int Width { get; private set; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; private set; }

    /// <summary><c>s->coded_width</c> and <c>s->coded_height</c>, rounded up to 16.</summary>
    public int CodedWidth { get; private set; }

    /// <inheritdoc cref="CodedWidth"/>
    public int CodedHeight { get; private set; }

    /// <summary>
    /// The access unit the last <see cref="Parse"/> completed: its buffer, where in it the unit
    /// starts, and how long it is. Zero length means none.
    /// </summary>
    public byte[]? OutBuffer { get; private set; }

    /// <inheritdoc cref="OutBuffer"/>
    public int OutOffset { get; private set; }

    /// <inheritdoc cref="OutBuffer"/>
    public int OutSize { get; private set; }

    private byte prevStartCode;
    private int bytesToSkip;
    private readonly byte[] unescBuffer = new byte[UnescapedLimit];
    private int unescIndex;
    private SearchState searchState = SearchState.NoMatch;

    /// <summary>Builds a parser and runs <see cref="Vc1ParseInit"/> over it.</summary>
    public Vc1Parser() => Vc1ParseInit();

    /// <summary><c>vc1_parse_init</c>.</summary>
    /// <remarks>
    /// A method rather than the constructor body, so the checklist's mapping stays something
    /// a reader can search for - the C has a function here and so does this.
    /// </remarks>
    public void Vc1ParseInit()
    {
        V.S.SliceContextCount = 1;
        V.FirstPicHeaderFlag = 1;
        V.ParseOnly = 1;
        prevStartCode = 0;
        bytesToSkip = 0;
        unescIndex = 0;
        searchState = SearchState.NoMatch;

        // The C's ff_vc1dsp_init here is only for startcode_find_candidate, which is a static
        // method in this port. The VLC tables the header parsers read are built by
        // ff_vc1_init_static, which ff_vc1_init_common calls - and nothing else in a parse-only
        // context needs doing, so that one call stands in for the whole of the C's init.
        Vc1Decoder.Vc1InitCommon(V);
    }

    /// <summary>
    /// <c>vc1_extract_header</c> - reads the header just unescaped, for its metadata.
    /// </summary>
    /// <remarks>
    /// The return values of the three header parsers are deliberately ignored exactly as the C
    /// ignores them for the sequence header and entry point: a parser is a best-effort reader
    /// of someone else's stream, and a header it cannot make sense of must not stop it finding
    /// the next access unit boundary.
    /// </remarks>
    private void Vc1ExtractHeader(byte[] buf, int bufSize)
    {
        var gb = default(GetBitContext);
        gb.InitGetBits8(buf, bufSize);

        if (prevStartCode == (byte)(Vc1Code.SeqHdr & 0xFF))
        {
            Vc1.Vc1DecodeSequenceHeader(V, ref gb);
        }
        else if (prevStartCode == (byte)(Vc1Code.EntryPoint & 0xFF))
        {
            Vc1.Vc1DecodeEntryPoint(V, ref gb);
        }
        else if (prevStartCode == (byte)(Vc1Code.Frame & 0xFF))
        {
            int ret = V.Profile < (int)Vc1Profile.Advanced
                ? Vc1.Vc1ParseFrameHeader(V, ref gb)
                : Vc1.Vc1ParseFrameHeaderAdv(V, ref gb);

            if (ret >= 0)
            {
                // keep AV_PICTURE_TYPE_BI internal to VC1
                PictType = V.S.PictType == AvPictureType.Bi ? AvPictureType.B : V.S.PictType;

                if (V.Broadcast != 0)
                {
                    // process pulldown flags.
                    // Pulldown flags are only valid when 'broadcast' has been set.
                    RepeatPict = V.Rff != 0 ? 2
                               : V.Rptfrm != 0 ? V.Rptfrm * 2 + 1
                               : 1;
                }
                else
                {
                    RepeatPict = 0;
                }

                FieldOrder = V.Broadcast != 0 && V.Interlace != 0 && V.Psf == 0
                    ? (V.Tff != 0 ? AvFieldOrder.TopTop : AvFieldOrder.BottomBottom)
                    : AvFieldOrder.Progressive;
            }
        }

        if (V.Avctx.Width != 0 && V.Avctx.Height != 0)
        {
            Width = V.Avctx.Width;
            Height = V.Avctx.Height;
            CodedWidth = (V.Avctx.CodedWidth + 15) & ~15;
            CodedHeight = (V.Avctx.CodedHeight + 15) & ~15;
        }
    }

    /// <summary>
    /// <c>vc1_parse</c> - searches for access-unit boundaries and reads the headers it passes,
    /// in one pass, unescaping only the start of each header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call with the next chunk of the stream, or with an empty span at end of input to flush
    /// the last access unit - the C treats EOF as the end of a frame and so does this.
    /// </para>
    /// <para>
    /// The C's <c>PARSER_FLAG_COMPLETE_FRAMES</c> arms are not translated: that flag says the
    /// caller already has whole access units and wants only the header metadata, which is the
    /// container case, and a container that frames VC-1 for itself does not need a parser. An
    /// elementary stream is the case this exists for.
    /// </para>
    /// </remarks>
    /// <returns>
    /// How many bytes of <paramref name="buf"/> were consumed, or
    /// <see cref="ParseContext.EndNotFound"/>. When <see cref="OutSize"/> is non-zero an access
    /// unit is ready in <see cref="OutBuffer"/>.
    /// </returns>
    public int Vc1Parse(byte[] buf, int bufOffset, int bufSize)
    {
        ArgumentNullException.ThrowIfNull(buf);

        OutBuffer = null;
        OutOffset = 0;
        OutSize = 0;

        int picFound = Pc.FrameStartFound;
        bool startCodeFound = false;
        int next = ParseContext.EndNotFound;
        int i = bytesToSkip;
        byte b = 0;

        if (picFound != 0 && bufSize == 0)
        {
            // EOF considered as end of frame
            unescBuffer.AsSpan(unescIndex, UnescapedThreshold - unescIndex).Clear();
            Vc1ExtractHeader(unescBuffer, unescIndex);
            next = 0;
        }

        while (i < bufSize)
        {
            startCodeFound = false;
            while (i < bufSize && unescIndex < UnescapedThreshold)
            {
                b = buf[bufOffset + i++];
                unescBuffer[unescIndex++] = b;
                if (searchState <= SearchState.OneZero)
                {
                    searchState = b != 0 ? SearchState.NoMatch : searchState + 1;
                }
                else if (searchState == SearchState.TwoZeros)
                {
                    if (b == 1)
                    {
                        searchState = SearchState.One;
                    }
                    else if (b > 1)
                    {
                        if (b == 3)
                            unescIndex--;       // swallow emulation prevention byte
                        searchState = SearchState.NoMatch;
                    }
                }
                else
                {
                    // searchState == One: header unescaping terminates early due to detection
                    // of the next start code
                    searchState = SearchState.NoMatch;
                    startCodeFound = true;
                    break;
                }
            }

            if (unescIndex >= UnescapedThreshold && !startCodeFound)
            {
                while (i < bufSize)
                {
                    if (searchState == SearchState.NoMatch)
                    {
                        i += StartcodeFindCandidate(buf, bufOffset + i, bufOffset + bufSize);
                        if (i < bufSize)
                            searchState = SearchState.OneZero;
                        i++;
                    }
                    else
                    {
                        b = buf[bufOffset + i++];
                        if (searchState == SearchState.OneZero)
                            searchState = b != 0 ? SearchState.NoMatch : SearchState.TwoZeros;
                        else if (searchState == SearchState.TwoZeros)
                        {
                            if (b >= 1)
                                searchState = b == 1 ? SearchState.One : SearchState.NoMatch;
                        }
                        else
                        {
                            searchState = SearchState.NoMatch;
                            startCodeFound = true;
                            break;
                        }
                    }
                }
            }

            if (startCodeFound)
            {
                Vc1ExtractHeader(unescBuffer, unescIndex);

                prevStartCode = b;
                unescIndex = 0;

                if (picFound == 0 && (b == (byte)(Vc1Code.Frame & 0xFF) ||
                                      b == (byte)(Vc1Code.Field & 0xFF)))
                {
                    picFound = 1;
                }
                else if (picFound != 0 && b != (byte)(Vc1Code.Field & 0xFF)
                                       && b != (byte)(Vc1Code.Slice & 0xFF)
                                       && b != (byte)(Vc1Code.EndOfSeq & 0xFF))
                {
                    next = i - 4;
                    picFound = b == (byte)(Vc1Code.Frame & 0xFF) ? 1 : 0;
                    break;
                }
            }
        }

        // The C copies its unesc_index and search_state locals back into the context here;
        // this uses the fields directly throughout, so there is nothing to copy back.
        Pc.FrameStartFound = picFound;

        byte[] outBuf = buf;
        int outOffset = bufOffset;
        int outSize = bufSize;
        if (Pc.CombineFrame(next, ref outBuf, ref outOffset, ref outSize) < 0)
        {
            bytesToSkip = 0;
            return bufSize;
        }

        // If we return with a valid pointer to a combined frame buffer then on the next call
        // we'll have been unhelpfully rewound by up to 4 bytes (depending upon whether the
        // start code overlapped the input buffer, and if so by how much). We don't want this:
        // it will either cause spurious second detections of the start code we've already
        // seen, or cause extra bytes to be inserted at the start of the unescaped buffer.
        bytesToSkip = 4;
        if (next < 0 && next != ParseContext.EndNotFound)
            bytesToSkip += next;

        OutBuffer = outBuf;
        OutOffset = outOffset;
        OutSize = outSize;
        return next;
    }

    /// <summary>
    /// <c>ff_startcode_find_candidate_c</c> - the offset of the next zero byte, which is the
    /// only byte a start code can begin with.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: the C reads eight bytes at a time with a bit trick and relies on
    /// <c>AV_INPUT_BUFFER_PADDING_SIZE</c> to let the last read run past <c>size</c>. This is
    /// the plain loop the C falls back to, which answers identically; the vectorised form is a
    /// performance question and VC-1 is not conformant yet.
    /// </remarks>
    private static int StartcodeFindCandidate(byte[] buf, int offset, int size)
    {
        for (int i = offset; i < size; i++)
            if (buf[i] == 0)
                return i - offset;

        return size - offset;
    }
}

/// <summary><c>AVFieldOrder</c> (<c>avcodec.h</c>), as far as a VC-1 stream can state it.</summary>
public enum AvFieldOrder
{
    /// <summary><c>AV_FIELD_UNKNOWN</c>.</summary>
    Unknown = 0,

    /// <summary><c>AV_FIELD_PROGRESSIVE</c>.</summary>
    Progressive = 1,

    /// <summary><c>AV_FIELD_TT</c> - top coded first, top displayed first.</summary>
    TopTop = 2,

    /// <summary><c>AV_FIELD_BB</c> - bottom coded first, bottom displayed first.</summary>
    BottomBottom = 3,
}

/// <summary>
/// Drives a <see cref="Vc1Parser"/> over a stream and hands back one access unit at a time -
/// FFmpeg's raw VC-1 demuxer and <c>av_parser_parse2</c> loop, which together are what turn a
/// <c>.vc1</c> file into packets a decoder can be fed.
/// </summary>
public sealed class Vc1AccessUnitReader : IDisposable
{
    private const int ChunkSize = 16384;

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly Vc1Parser parser = new();

    // The chunk carries AV_INPUT_BUFFER_PADDING_SIZE of zeroes past what was read, because the
    // parser's start-code scan is allowed to read a few bytes past the end of the data.
    private readonly byte[] chunk = new byte[ChunkSize + Vc1.InputBufferPaddingSize];
    private int chunkLength;
    private int chunkOffset;
    private bool endOfStream;
    private bool flushed;

    /// <summary>The parser, for the metadata its headers supplied.</summary>
    public Vc1Parser Parser => parser;

    /// <summary>Opens a reader over a VC-1 elementary stream.</summary>
    public Vc1AccessUnitReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Reads until one whole access unit is available.
    /// </summary>
    /// <remarks>
    /// The empty call at the end is not optional: <c>vc1_parse</c> treats end of input as the
    /// end of the access unit it is in the middle of, so the last picture of every stream
    /// arrives only on a flush. Leaving it out loses exactly one frame per file, which is the
    /// kind of error a whole-file comparison catches and a thirty-frame one does not.
    /// </remarks>
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

                    flushed = true;
                    parser.Vc1Parse(chunk, 0, 0);
                    if (parser.OutSize > 0)
                    {
                        unit = parser.OutBuffer.AsMemory(parser.OutOffset, parser.OutSize);
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

                chunk.AsSpan(chunkLength, Vc1.InputBufferPaddingSize).Clear();
            }

            int consumed = parser.Vc1Parse(chunk, chunkOffset, chunkLength - chunkOffset);

            // av_parser_parse2: "WARNING: the returned index can be negative", and it clamps.
            // A negative answer means the access unit ended before this chunk began, which the
            // parser compensates for with its own four-byte rewind on the next call.
            if (consumed < 0)
                consumed = 0;
            chunkOffset += consumed;

            if (parser.OutSize > 0)
            {
                unit = parser.OutBuffer.AsMemory(parser.OutOffset, parser.OutSize);
                return true;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        parser.Pc.Close();
        if (!leaveOpen)
            stream.Dispose();
    }
}

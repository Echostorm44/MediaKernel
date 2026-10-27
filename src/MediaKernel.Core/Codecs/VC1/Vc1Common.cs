// VC-1's start-code markers and the escape removal every Advanced Profile unit goes through -
// a translation of ffmpeg-master/libavcodec/vc1_common.h.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary><c>enum Profile</c> (vc1_common.h).</summary>
public enum Vc1Profile
{
    /// <summary><c>PROFILE_SIMPLE</c>.</summary>
    Simple = 0,

    /// <summary><c>PROFILE_MAIN</c>.</summary>
    Main = 1,

    /// <summary><c>PROFILE_COMPLEX</c> - WMV9 specific.</summary>
    Complex = 2,

    /// <summary><c>PROFILE_ADVANCED</c>.</summary>
    Advanced = 3,
}

/// <summary><c>enum VC1Code</c> - the markers in VC-1 Advanced Profile frame data.</summary>
public static class Vc1Code
{
    /// <summary><c>VC1_CODE_RES0</c>.</summary>
    public const uint Res0 = 0x00000100;

    /// <summary><c>VC1_CODE_ENDOFSEQ</c>.</summary>
    public const uint EndOfSeq = 0x0000010A;

    /// <summary><c>VC1_CODE_SLICE</c>.</summary>
    public const uint Slice = 0x0000010B;

    /// <summary><c>VC1_CODE_FIELD</c>.</summary>
    public const uint Field = 0x0000010C;

    /// <summary><c>VC1_CODE_FRAME</c>.</summary>
    public const uint Frame = 0x0000010D;

    /// <summary><c>VC1_CODE_ENTRYPOINT</c>.</summary>
    public const uint EntryPoint = 0x0000010E;

    /// <summary><c>VC1_CODE_SEQHDR</c>.</summary>
    public const uint SeqHdr = 0x0000010F;
}

/// <summary>Shared VC-1 helpers (<c>vc1_common.h</c>).</summary>
public static class Vc1Common
{
    /// <summary><c>IS_MARKER(x)</c> - true when <paramref name="x"/> is 00 00 01 xx.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ISMarker(uint x) => (x & ~0xFFu) == Vc1Code.Res0;

    /// <summary>
    /// <c>find_next_marker</c> - the index where the next marker starts, or
    /// <paramref name="end"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The returned index points at the marker's first byte, four back from where the scan
    /// stopped. Under four bytes left, there is no room for one and the C does not look.
    /// </remarks>
    public static int FindNextMarker(ReadOnlySpan<byte> buffer, int src, int end)
    {
        if (end - src >= 4)
        {
            uint mrk = 0xFFFFFFFF;
            src = StartCode.AvprivFindStartCode(buffer, src, end, ref mrk);
            if (ISMarker(mrk))
                return src - 4;
        }
        return end;
    }

    /// <summary>
    /// <c>extract_extradata_vc1</c> (<c>bsf/extract_extradata.c</c>) - how a VC-1 elementary
    /// stream gets the out-of-band configuration <c>vc1_decode_init</c> insists on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A raw VC-1 stream has no extradata: the sequence header and entry point are in band, at
    /// the front of the first access unit. FFmpeg's demuxer layer runs this bitstream filter to
    /// lift that prefix out into <c>codecpar-&gt;extradata</c>, which is then exactly the
    /// marker-delimited layout the decoder's VC-1 arm expects. Without it the decoder refuses
    /// the stream - <c>ffmpeg -i file.vc1</c> prints "Failed to open codec in
    /// avformat_find_stream_info" for precisely that reason, and succeeds afterwards because
    /// this filter has run by then.
    /// </para>
    /// <para>
    /// The prefix runs from the start of the packet to the first marker after a sequence header
    /// or entry point - so an entry point that follows a sequence header is included, and the
    /// frame marker that follows them both is where it stops.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The length of the prefix, which is 0 when the packet carries no configuration. The C's
    /// <c>remove</c> option, which the caller sets, is the caller's business here: the prefix
    /// is the first <c>n</c> bytes of <paramref name="packet"/> either way.
    /// </returns>
    public static int Vc1ExtractExtradata(ReadOnlySpan<byte> packet, int size)
    {
        bool hasExtradata = false;
        uint state = uint.MaxValue;
        int ptr = 0;

        while (ptr < size)
        {
            ptr = StartCode.AvprivFindStartCode(packet, ptr, size, ref state);
            if (state == Vc1Code.SeqHdr || state == Vc1Code.EntryPoint)
                hasExtradata = true;
            else if (hasExtradata && ISMarker(state))
                return ptr - 4;
        }

        return 0;
    }

    /// <summary>
    /// <c>vc1_unescape_buffer</c> - removes the 00 00 03 emulation-prevention bytes, writing
    /// the result to <paramref name="dst"/> and returning its length.
    /// </summary>
    /// <remarks>
    /// The escape is dropped only when a 03 follows two zeroes <i>and</i> the byte after it is
    /// below four - so 00 00 03 FF keeps its 03, which is the detail that makes this different
    /// from H.264's unconditional rule. Under four bytes the buffer is copied unchanged, because
    /// no escape sequence fits.
    /// </remarks>
    public static int Vc1UnescapeBuffer(ReadOnlySpan<byte> src, int size, Span<byte> dst)
    {
        int dsize = 0;

        if (size < 4)
        {
            for (dsize = 0; dsize < size; dsize++)
                dst[dsize] = src[dsize];
            return size;
        }

        for (int i = 0; i < size; i++)
        {
            if (src[i] == 3 && i >= 2 && src[i - 1] == 0 && src[i - 2] == 0
                && i < size - 1 && src[i + 1] < 4)
            {
                dst[dsize++] = src[i + 1];
                i++;
            }
            else
            {
                dst[dsize++] = src[i];
            }
        }
        return dsize;
    }
}

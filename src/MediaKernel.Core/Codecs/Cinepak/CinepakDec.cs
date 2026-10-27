// Ported from FFmpeg's libavcodec/cinepak.c - Cinepak, the codec most mid-90s QuickTime and
// AVI video is in.
//
// A frame is a list of STRIPS, each a horizontal band with its own pair of codebooks: 256
// four-pixel vectors (V1) and 256 two-by-two vectors (V4). The picture is built from 4x4
// blocks, each either one V1 entry stretched over the block or four V4 entries, and a block
// may also be left exactly as the previous frame had it - which is why the decoder owns a
// frame across calls rather than filling a fresh one.
//
// Everything in the bitstream is a flag bit in a big-endian 32-bit word read on demand, and
// the chunk id decides which of the three flag layouts applies. That is the whole format.

using System;

namespace MediaKernel.Core.Codecs.Cinepak;

/// <summary>A strip's geometry and its two codebooks.</summary>
/// <remarks>
/// <c>cvid_strip</c>. The codebooks are 256 entries of twelve bytes - four pixels of three
/// components - and they are copied from the previous strip unless the frame header says
/// each strip carries its own.
/// </remarks>
public sealed class CinepakStrip
{
    /// <summary><c>id</c>. 0x10 marks a key frame.</summary>
    public int Id;

    /// <summary><c>x1</c>.</summary>
    public int X1;

    /// <summary><c>y1</c>.</summary>
    public int Y1;

    /// <summary><c>x2</c>.</summary>
    public int X2;

    /// <summary><c>y2</c>.</summary>
    public int Y2;

    /// <summary><c>v4_codebook[256]</c>, flattened to twelve bytes an entry.</summary>
    public readonly byte[] V4Codebook = new byte[256 * 12];

    /// <summary><c>v1_codebook[256]</c>.</summary>
    public readonly byte[] V1Codebook = new byte[256 * 12];
}

/// <summary><c>CinepakContext</c>.</summary>
public sealed class CinepakContext
{
    /// <summary><c>MAX_STRIPS</c>.</summary>
    public const int MaxStrips = 32;

    /// <summary><c>width</c>, rounded up to a multiple of four.</summary>
    public int Width;

    /// <summary><c>height</c>, rounded up to a multiple of four.</summary>
    public int Height;

    /// <summary><c>avctx-&gt;width</c>, which the block writer clips the bottom edge against.</summary>
    public int DisplayWidth;

    /// <summary><c>avctx-&gt;height</c>.</summary>
    public int DisplayHeight;

    /// <summary><c>palette_video</c> - the stream is 8-bit paletted rather than RGB24.</summary>
    public bool PaletteVideo;

    /// <summary><c>sega_film_skip_bytes</c>, -1 until the first frame settles it.</summary>
    public int SegaFilmSkipBytes = -1;

    /// <summary><c>s-&gt;strips</c>.</summary>
    public readonly CinepakStrip[] Strips = CreateStrips();

    /// <summary>
    /// <c>s-&gt;frame</c> - the picture, which PERSISTS between calls.
    /// </summary>
    /// <remarks>
    /// Cinepak is an inter codec with no motion compensation: a block that did not change is
    /// simply not coded, and the previous frame's pixels stand. <c>ff_reget_buffer</c> is
    /// what expresses that in the C, and here it is a buffer the context owns.
    /// </remarks>
    public byte[] Frame = [];

    /// <summary>The frame's stride in bytes.</summary>
    public int LineSize;

    /// <summary><c>s-&gt;pal</c> - 256 BGRA entries, for a paletted stream.</summary>
    public readonly byte[] Palette = new byte[256 * 4];

    /// <summary>Whether the last decoded frame was a key frame.</summary>
    public bool KeyFrame;

    private static CinepakStrip[] CreateStrips()
    {
        var strips = new CinepakStrip[MaxStrips];
        for (int i = 0; i < strips.Length; i++)
            strips[i] = new CinepakStrip();
        return strips;
    }
}

/// <summary><c>cinepak.c</c> - the Cinepak decoder.</summary>
public static class CinepakDec
{
    /// <summary><c>AVERROR_INVALIDDATA</c>.</summary>
    public const int AverrorInvalidData = -1094995529;

    /// <summary><c>AVERROR_PATCHWELCOME</c>.</summary>
    public const int AverrorPatchwelcome = -1163346256;

    /// <summary>
    /// <c>cinepak_decode_init</c>.
    /// </summary>
    /// <param name="s">The context.</param>
    /// <param name="width"><c>avctx-&gt;width</c>.</param>
    /// <param name="height"><c>avctx-&gt;height</c>.</param>
    /// <param name="bitsPerCodedSample">
    /// From the container. EXACTLY 8 means a paletted stream; anything else, including
    /// unset, means RGB24. The C tests it the same way round, so a container that forgets
    /// the field gives RGB24 rather than a palette lookup into nothing.
    /// </param>
    public static void CinepakDecodeInit(CinepakContext s, int width, int height,
                                         int bitsPerCodedSample)
    {
        ArgumentNullException.ThrowIfNull(s);

        s.DisplayWidth = width;
        s.DisplayHeight = height;
        s.Width = (width + 3) & ~3;
        s.Height = (height + 3) & ~3;
        s.SegaFilmSkipBytes = -1;       // "uninitialized state"
        s.PaletteVideo = bitsPerCodedSample == 8;

        int bytesPerPixel = s.PaletteVideo ? 1 : 3;
        s.LineSize = s.Width * bytesPerPixel;

        // DISPLAY height, not the rounded one, which is what ff_reget_buffer gives the C.
        // The difference matters: strip coordinates are checked against the ROUNDED height,
        // so a 187-line picture can carry a strip ending at 188, and what keeps the last
        // block inside the buffer is the row clamp in CinepakDecodeVectors. Allocating the
        // rounded height instead would make that clamp protect nothing - and a perturbation
        // sweep showed exactly that: deleting the clamp was GREEN until this line was
        // changed.
        s.Frame = new byte[s.LineSize * s.DisplayHeight];
    }

    /// <summary>
    /// <c>cinepak_decode_codebook</c> - fill one codebook from a chunk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chunk id carries the layout in its low bits: bit 2 says four-element entries
    /// (greyscale, no chroma) rather than six, and bit 0 says the entries are SELECTIVELY
    /// updated behind a bitmap, where a clear bit leaves the existing entry alone.
    /// </para>
    /// <para>
    /// A six-element entry is four luma samples and a shared Cb/Cr pair, and the YUV to RGB
    /// here is Cinepak's own - <c>r = y + 2v</c>, <c>g = y - v - u/2</c>, <c>b = y + 2u</c>,
    /// each clipped. The <c>u/2</c> is C integer division on a SIGNED value, so it truncates
    /// toward zero; an arithmetic shift rounds the other way for negative u and is wrong.
    /// </para>
    /// </remarks>
    public static void CinepakDecodeCodebook(byte[] codebook, int chunkId, int size,
                                             ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(codebook);

        int eod = size;
        int at = 0;
        uint flag = 0, mask = 0;
        int p = 0;

        // "check if this chunk contains 4- or 6-element vectors"
        int n = (chunkId & 0x04) != 0 ? 4 : 6;

        for (int i = 0; i < 256; i++)
        {
            if ((chunkId & 0x01) != 0 && (mask >>= 1) == 0)
            {
                if (at + 4 > eod)
                    break;
                flag = ReadBe32(data, at);
                at += 4;
                mask = 0x80000000;
            }

            if ((chunkId & 0x01) == 0 || (flag & mask) != 0)
            {
                if (at + n > eod)
                    break;

                for (int k = 0; k < 4; ++k)
                {
                    int r = data[at++];
                    for (int kk = 0; kk < 3; ++kk)
                        codebook[p++] = (byte)r;
                }

                if (n == 6)
                {
                    int u = (sbyte)data[at++];
                    int v = (sbyte)data[at++];
                    p -= 12;
                    for (int k = 0; k < 4; ++k)
                    {
                        int r = codebook[p] + v * 2;
                        int g = codebook[p + 1] - (u / 2) - v;
                        int b = codebook[p + 2] + u * 2;
                        codebook[p++] = ClipUint8(r);
                        codebook[p++] = ClipUint8(g);
                        codebook[p++] = ClipUint8(b);
                    }
                }
            }
            else
            {
                p += 12;
            }
        }
    }

    /// <summary>
    /// <c>cinepak_decode_vectors</c> - the blocks of one strip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Up to two flag bits are consumed per block, from the same rolling 32-bit word. The
    /// first says whether the block is coded at all; the second, when the chunk id does not
    /// fix it, says V1 or V4. A block that is not coded keeps whatever the previous frame
    /// left there.
    /// </para>
    /// <para>
    /// The four row pointers are clamped at the bottom edge, because "take care of y
    /// dimension not being multiple of 4, such streams exist". The C's trick is to write
    /// each block from the BOTTOM up, so a clamped row is overwritten more than once and
    /// ends with the right data, instead of testing inside the loop.
    /// </para>
    /// </remarks>
    public static int CinepakDecodeVectors(CinepakContext s, CinepakStrip strip,
                                           int chunkId, int size, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(strip);

        int eod = size;
        int at = 0;
        uint flag = 0, mask = 0;
        byte[] frame = s.Frame;
        int stride = s.LineSize;
        int step = s.PaletteVideo ? 4 : 12;

        for (int y = strip.Y1; y < strip.Y2; y += 4)
        {
            int rowStart = (s.PaletteVideo ? strip.X1 : strip.X1 * 3) + y * stride;
            int ip0 = rowStart, ip1 = rowStart, ip2 = rowStart, ip3 = rowStart;

            if (s.DisplayHeight - y > 1)
            {
                ip1 = ip0 + stride;
                if (s.DisplayHeight - y > 2)
                {
                    ip2 = ip1 + stride;
                    if (s.DisplayHeight - y > 3)
                        ip3 = ip2 + stride;
                }
            }

            for (int x = strip.X1; x < strip.X2; x += 4)
            {
                if ((chunkId & 0x01) != 0 && (mask >>= 1) == 0)
                {
                    if (at + 4 > eod)
                        return AverrorInvalidData;
                    flag = ReadBe32(data, at);
                    at += 4;
                    mask = 0x80000000;
                }

                if ((chunkId & 0x01) == 0 || (flag & mask) != 0)
                {
                    if ((chunkId & 0x02) == 0 && (mask >>= 1) == 0)
                    {
                        if (at + 4 > eod)
                            return AverrorInvalidData;
                        flag = ReadBe32(data, at);
                        at += 4;
                        mask = 0x80000000;
                    }

                    if ((chunkId & 0x02) != 0 || (~flag & mask) != 0)
                    {
                        if (at >= eod)
                            return AverrorInvalidData;

                        int p = data[at++] * 12;
                        byte[] cb = strip.V1Codebook;

                        if (s.PaletteVideo)
                        {
                            frame[ip3] = frame[ip3 + 1] = frame[ip2] = frame[ip2 + 1] = cb[p + 6];
                            frame[ip3 + 2] = frame[ip3 + 3] = frame[ip2 + 2] = frame[ip2 + 3] = cb[p + 9];
                            frame[ip1] = frame[ip1 + 1] = frame[ip0] = frame[ip0 + 1] = cb[p];
                            frame[ip1 + 2] = frame[ip1 + 3] = frame[ip0 + 2] = frame[ip0 + 3] = cb[p + 3];
                        }
                        else
                        {
                            Copy3(cb, p + 6, frame, ip3 + 0); Copy3(cb, p + 6, frame, ip3 + 3);
                            Copy3(cb, p + 6, frame, ip2 + 0); Copy3(cb, p + 6, frame, ip2 + 3);
                            Copy3(cb, p + 9, frame, ip3 + 6); Copy3(cb, p + 9, frame, ip3 + 9);
                            Copy3(cb, p + 9, frame, ip2 + 6); Copy3(cb, p + 9, frame, ip2 + 9);
                            Copy3(cb, p + 0, frame, ip1 + 0); Copy3(cb, p + 0, frame, ip1 + 3);
                            Copy3(cb, p + 0, frame, ip0 + 0); Copy3(cb, p + 0, frame, ip0 + 3);
                            Copy3(cb, p + 3, frame, ip1 + 6); Copy3(cb, p + 3, frame, ip1 + 9);
                            Copy3(cb, p + 3, frame, ip0 + 6); Copy3(cb, p + 3, frame, ip0 + 9);
                        }
                    }
                    else if ((flag & mask) != 0)
                    {
                        if (at + 4 > eod)
                            return AverrorInvalidData;

                        byte[] cb = strip.V4Codebook;
                        int cb0 = data[at++] * 12;
                        int cb1 = data[at++] * 12;
                        int cb2 = data[at++] * 12;
                        int cb3 = data[at++] * 12;

                        if (s.PaletteVideo)
                        {
                            frame[ip3 + 0] = cb[cb2 + 6];
                            frame[ip3 + 1] = cb[cb2 + 9];
                            frame[ip3 + 2] = cb[cb3 + 6];
                            frame[ip3 + 3] = cb[cb3 + 9];
                            frame[ip2 + 0] = cb[cb2 + 0];
                            frame[ip2 + 1] = cb[cb2 + 3];
                            frame[ip2 + 2] = cb[cb3 + 0];
                            frame[ip2 + 3] = cb[cb3 + 3];
                            frame[ip1 + 0] = cb[cb0 + 6];
                            frame[ip1 + 1] = cb[cb0 + 9];
                            frame[ip1 + 2] = cb[cb1 + 6];
                            frame[ip1 + 3] = cb[cb1 + 9];
                            frame[ip0 + 0] = cb[cb0 + 0];
                            frame[ip0 + 1] = cb[cb0 + 3];
                            frame[ip0 + 2] = cb[cb1 + 0];
                            frame[ip0 + 3] = cb[cb1 + 3];
                        }
                        else
                        {
                            Copy6(cb, cb2 + 6, frame, ip3 + 0);
                            Copy6(cb, cb3 + 6, frame, ip3 + 6);
                            Copy6(cb, cb2 + 0, frame, ip2 + 0);
                            Copy6(cb, cb3 + 0, frame, ip2 + 6);
                            Copy6(cb, cb0 + 6, frame, ip1 + 0);
                            Copy6(cb, cb1 + 6, frame, ip1 + 6);
                            Copy6(cb, cb0 + 0, frame, ip0 + 0);
                            Copy6(cb, cb1 + 0, frame, ip0 + 6);
                        }
                    }
                }

                ip0 += step; ip1 += step; ip2 += step; ip3 += step;
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>cinepak_decode_strip</c> - one strip's chunks, until its vectors are reached.
    /// </summary>
    /// <remarks>
    /// Returning as soon as a vector chunk is handled is the C's structure, not an
    /// optimisation: a strip holds its codebooks first and its blocks last, and anything
    /// after the blocks is not part of this strip. A strip that reaches the end with no
    /// vector chunk is invalid data.
    /// </remarks>
    public static int CinepakDecodeStrip(CinepakContext s, CinepakStrip strip,
                                         ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(strip);

        int eod = data.Length;
        int at = 0;

        // "coordinate sanity checks"
        if (strip.X2 > s.Width || strip.Y2 > s.Height || strip.X1 >= strip.X2
            || strip.Y1 >= strip.Y2)
        {
            return AverrorInvalidData;
        }

        while (at + 4 <= eod)
        {
            int chunkId = data[at];
            int chunkSize = ReadBe24(data, at + 1) - 4;
            if (chunkSize < 0)
                return AverrorInvalidData;

            at += 4;
            if (at + chunkSize > eod)
                chunkSize = eod - at;

            switch (chunkId)
            {
                case 0x20:
                case 0x21:
                case 0x24:
                case 0x25:
                    CinepakDecodeCodebook(strip.V4Codebook, chunkId, chunkSize,
                                          data.Slice(at, chunkSize));
                    break;

                case 0x22:
                case 0x23:
                case 0x26:
                case 0x27:
                    CinepakDecodeCodebook(strip.V1Codebook, chunkId, chunkSize,
                                          data.Slice(at, chunkSize));
                    break;

                case 0x30:
                case 0x31:
                case 0x32:
                    return CinepakDecodeVectors(s, strip, chunkId, chunkSize,
                                                data.Slice(at, chunkSize));
            }

            at += chunkSize;
        }

        return AverrorInvalidData;
    }

    /// <summary>
    /// <c>cinepak_predecode_check</c> - the header checks, and the Sega FILM sniff.
    /// </summary>
    /// <remarks>
    /// A Sega FILM/CPK file puts two or six extra bytes after the frame header, and nothing
    /// says which: the C decides on the FIRST frame by comparing the size the header
    /// declares against the size the container gave, and then six specific bytes decide
    /// between 2 and 6. That decision is made once and kept.
    /// </remarks>
    public static int CinepakPredecodeCheck(CinepakContext s, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(s);

        int numStrips = ReadBe16(data, 8);
        int encodedBufSize = ReadBe24(data, 1);

        // discard_damaged_percentage defaults to 95, so the C accepts a frame holding at
        // least 5% of what its header claims. Kept, because a truncated sample is exactly
        // what the FATE "partial" files are.
        if (data.Length < (long)encodedBufSize * 5 / 100)
            return AverrorInvalidData;

        if (s.SegaFilmSkipBytes == -1)
        {
            if (encodedBufSize == 0)
                return AverrorPatchwelcome;     // "encoded_buf_size 0"

            if (encodedBufSize != data.Length && data.Length % encodedBufSize != 0)
            {
                s.SegaFilmSkipBytes = data.Length >= 16
                    && data[10] == 0xFE && data[11] == 0x00 && data[12] == 0x00
                    && data[13] == 0x06 && data[14] == 0x00 && data[15] == 0x00
                    ? 6 : 2;
            }
            else
            {
                s.SegaFilmSkipBytes = 0;
            }
        }

        if (data.Length < 10 + s.SegaFilmSkipBytes + numStrips * 12)
            return AverrorInvalidData;

        if (numStrips != 0)
        {
            int stripSize = ReadBe24(data, 10 + s.SegaFilmSkipBytes + 1);
            if (stripSize < 12 || stripSize > encodedBufSize)
                return AverrorInvalidData;
        }

        return 0;
    }

    /// <summary>
    /// <c>cinepak_decode</c> - the strip list.
    /// </summary>
    /// <remarks>
    /// A strip whose <c>y1</c> is ZERO is "relative to the previous stripe": its top is the
    /// previous strip's bottom and its <c>y2</c> field is a HEIGHT rather than a coordinate.
    /// And unless bit 0 of the frame flags is set, each strip inherits the previous strip's
    /// codebooks, which is why they are copied rather than reset.
    /// </remarks>
    public static int CinepakDecode(CinepakContext s, ReadOnlySpan<byte> packet)
    {
        ArgumentNullException.ThrowIfNull(s);

        int eod = packet.Length;
        int frameFlags = packet[0];
        int numStrips = ReadBe16(packet, 8);

        int at = 10 + s.SegaFilmSkipBytes;
        int y0 = 0;

        numStrips = Math.Min(numStrips, CinepakContext.MaxStrips);
        s.KeyFrame = false;

        for (int i = 0; i < numStrips; i++)
        {
            if (at + 12 > eod)
                return AverrorInvalidData;

            CinepakStrip strip = s.Strips[i];
            strip.Id = packet[at];

            strip.Y1 = ReadBe16(packet, at + 4);
            if (strip.Y1 == 0)
            {
                strip.Y1 = y0;
                strip.Y2 = y0 + ReadBe16(packet, at + 8);
            }
            else
            {
                strip.Y2 = ReadBe16(packet, at + 8);
            }
            strip.X1 = ReadBe16(packet, at + 6);
            strip.X2 = ReadBe16(packet, at + 10);

            if (strip.Id == 0x10)
                s.KeyFrame = true;

            int stripSize = ReadBe24(packet, at + 1) - 12;
            if (stripSize < 0)
                return AverrorInvalidData;

            at += 12;
            if (at + stripSize > eod)
                stripSize = eod - at;

            if (i > 0 && (frameFlags & 0x01) == 0)
            {
                CinepakStrip previous = s.Strips[i - 1];
                previous.V4Codebook.CopyTo(strip.V4Codebook.AsSpan());
                previous.V1Codebook.CopyTo(strip.V1Codebook.AsSpan());
            }

            int result = CinepakDecodeStrip(s, strip, packet.Slice(at, stripSize));
            if (result != 0)
                return result;

            at += stripSize;
            y0 = strip.Y2;
        }

        return 0;
    }

    /// <summary>
    /// <c>cinepak_decode_frame</c>.
    /// </summary>
    /// <returns>
    /// The bytes consumed, or a negative error. Zero strips is NOT an error: it is a frame
    /// that changed nothing, and the previous picture stands.
    /// </returns>
    public static int CinepakDecodeFrame(CinepakContext s, ReadOnlySpan<byte> packet,
                                         out bool gotFrame)
    {
        ArgumentNullException.ThrowIfNull(s);

        gotFrame = false;

        if (packet.Length < 10)
            return AverrorInvalidData;

        int numStrips = ReadBe16(packet, 8);

        // "Empty frame, do not waste time". The C also returns here for a paletted stream
        // with no palette side data; this port is handed the palette separately, so the
        // test is the strip count alone.
        if (numStrips == 0)
            return packet.Length;

        int ret = CinepakPredecodeCheck(s, packet);
        if (ret < 0)
            return ret;

        // The C logs "cinepak_decode failed" here and CARRIES ON - there is no return. A
        // strip that runs off the end of a truncated packet leaves the frame partly
        // updated, and that partly updated frame is what gets handed out; the blocks the
        // strip did write are real, and the rest is the previous picture. FFmpeg's own
        // cvid-partial reference depends on it: the file is truncated at exactly 1 MiB and
        // its last frame IS in the reference, with ffmpeg logging the failure as it emits
        // it. Returning the error instead loses that frame, which is how this was found.
        _ = CinepakDecode(s, packet);

        gotFrame = true;
        return packet.Length;
    }

    private static void Copy3(byte[] source, int from, byte[] destination, int to)
    {
        destination[to] = source[from];
        destination[to + 1] = source[from + 1];
        destination[to + 2] = source[from + 2];
    }

    private static void Copy6(byte[] source, int from, byte[] destination, int to) =>
        source.AsSpan(from, 6).CopyTo(destination.AsSpan(to));

    /// <summary><c>av_clip_uint8</c>.</summary>
    private static byte ClipUint8(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    private static uint ReadBe32(ReadOnlySpan<byte> d, int at) =>
        (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

    private static int ReadBe24(ReadOnlySpan<byte> d, int at) =>
        (d[at] << 16) | (d[at + 1] << 8) | d[at + 2];

    private static int ReadBe16(ReadOnlySpan<byte> d, int at) => (d[at] << 8) | d[at + 1];
}

// Ported from FFmpeg's libavcodec/mjpegbdec.c - Apple MJPEG-B, the QuickTime variant.
//
// MJPEG-B is ordinary JPEG with the marker structure taken away. There is no SOI, no 0xFF
// marker before each segment, and no 0xFF 0x00 escaping in the entropy data; instead a small
// fixed header at the front of every frame gives the BYTE OFFSET of each piece - DQT, DHT,
// SOF, SOS and the start of data - and the decoder jumps to each in turn. Everything after
// that jump is the same code the ordinary decoder runs, which is why this file is a hundred
// lines and not a second decoder.
//
// Two consequences of losing the markers are worth stating, because both would otherwise look
// like bugs in shared code:
//
//   * SOS and the start of image data are separate offsets, and there can be padding between
//     them. mjpb_skiptosod is that gap, and ff_mjpeg_decode_sos skips it.
//   * An interlaced frame is TWO of these headers in one packet, the second at
//     second_field_offs, and the loop that reads the second one is a goto back to the top.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary><c>mjpegbdec.c</c> - Apple MJPEG-B.</summary>
public static class MjpegbDec
{
    /// <summary><c>MKBETAG('m','j','p','g')</c>.</summary>
    private const uint MjpgTag = 0x6D6A7067;

    /// <summary>
    /// <c>read_offs</c> - an offset from the header, rejected if it points outside the frame.
    /// </summary>
    /// <remarks>
    /// The C logs and returns 0, which every caller treats as "this piece is absent". An
    /// out-of-range offset is therefore not an error: a frame may legitimately carry no DQT
    /// because the previous one already defined the tables.
    /// </remarks>
    private static uint ReadOffs(ref GetBitContext gb, uint size)
    {
        uint offs = gb.GetBits(32);
        return offs >= size ? 0 : offs;
    }

    /// <summary>
    /// <c>mjpegb_decode_frame</c> - one MJPEG-B packet, which is one or two fields.
    /// </summary>
    /// <returns>The bytes consumed, or a negative error.</returns>
    public static int MjpegbDecodeFrame(MJpegDecodeContext s, byte[] buf, int bufSize)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(buf);

        int bufPtr = 0;
        int bufEnd = bufSize;

        s.GotPicture = 0;
        s.AdobeTransform = -1;
        s.BufSize = bufSize;

        // MJPEG-B has no marker structure and no 0xFF 0x00 escaping, so the scan's buffer is
        // the entropy data verbatim. The C expresses this as a codec-id test inside
        // ff_mjpeg_unescape_sos; here it is a flag, set once.
        s.InputIsUnescaped = true;

        // ff_get_buffer's place. DecodeImage installs this for the ordinary decoder; MJPEG-B
        // reaches ff_mjpeg_decode_sof by a different route and has to install it too, or the
        // scan writes into a null picture and every linesize == 0 guard quietly skips the
        // block. That failure mode is a decode that returns success and no pixels.
        s.PictureAllocator = MjpegDec.AllocPicture;

        while (true)       // read_header: the C's goto, for the second field
        {
            // "reset on every SOI" - except there is no SOI here, so it is every header.
            s.RestartInterval = 0;
            s.MjpbSkiptosod = 0;

            uint available = (uint)(bufEnd - bufPtr);
            var hgb = default(GetBitContext);
            hgb.InitGetBits8(buf, bufPtr, (int)available);

            hgb.SkipBits(32);                           // reserved zeros

            if (hgb.GetBits(32) != MjpgTag)
                return MjpegDec.AverrorInvalidData;     // "not mjpeg-b (bad fourcc)"

            uint fieldSize = hgb.GetBits(32);
            hgb.SkipBits(32);                           // padded field size
            uint secondFieldOffs = ReadOffs(ref hgb, available);

            uint dqtOffs = ReadOffs(ref hgb, available);
            if (dqtOffs != 0)
            {
                s.GB.Init(buf, bufPtr + (int)dqtOffs, bufEnd - (bufPtr + (int)dqtOffs));
                MjpegDec.MjpegDecodeDqt(s);
                // The C only propagates this error under AV_EF_EXPLODE, which is off.
            }

            uint dhtOffs = ReadOffs(ref hgb, available);
            if (dhtOffs != 0)
            {
                s.GB.Init(buf, bufPtr + (int)dhtOffs, bufEnd - (bufPtr + (int)dhtOffs));
                MjpegDec.MjpegDecodeDht(s);
            }

            uint sofOffs = ReadOffs(ref hgb, available);
            if (sofOffs != 0)
            {
                s.GB.Init(buf, bufPtr + (int)sofOffs, bufEnd - (bufPtr + (int)sofOffs));
                int ret = MjpegDec.MjpegDecodeSof(s);
                if (ret < 0)
                    return ret;
            }

            uint sosOffs = ReadOffs(ref hgb, available);
            uint sodOffs = ReadOffs(ref hgb, available);

            if (sosOffs != 0)
            {
                // FFMIN(field_size, ...) and not the rest of the packet: the scan of the
                // FIRST field must not run into the second field's header.
                int remaining = bufEnd - bufPtr - (int)sosOffs;
                int window = (int)Math.Min(fieldSize, (uint)Math.Max(remaining, 0));
                s.GB.Init(buf, bufPtr + (int)sosOffs, window);

                // The padding between the SOS segment and the image data. The SOS segment's
                // own length is the big-endian 16 bits sitting at sos_offs, so the gap is
                // what is left of (sod_offs - sos_offs) after it.
                s.MjpbSkiptosod = (int)sodOffs - (int)sosOffs - s.GB.PeekBe16();

                int ret = MjpegDec.MjpegDecodeSos(s);
                if (ret < 0)
                    return ret;
            }

            if (s.Interlaced != 0)
            {
                s.BottomField ^= 1;
                // Not the bottom field yet, so there is a second one to read and no picture
                // to hand out. This is the C's `goto read_header`.
                if (s.BottomField != s.InterlacePolarity && secondFieldOffs != 0)
                {
                    bufPtr = (int)secondFieldOffs;
                    continue;
                }
            }

            break;
        }

        if (s.GotPicture == 0)
            return bufSize;                             // "no picture"

        return bufSize;
    }
}

// Ported from FFmpeg's libavcodec/sp5xdec.c - AMV Video and Sunplus JPEG (SP5X).
//
// Neither format carries a single JPEG marker. A packet is entropy-coded data and nothing
// else: no SOI, no DQT, no DHT, no SOF, no SOS. The decoder builds a complete JPEG around it
// from the fixed templates in sp5x.h - patching the picture size into SOF - and then runs the
// ordinary decoder over the result, which is why the two formats cost a hundred lines between
// them rather than a decoder each.
//
// The two differ in exactly one loop. SP5X's payload starts at byte 14 and is NOT escaped, so
// the recoder has to insert the 0xFF 0x00 stuffing as it copies; AMV's starts at byte 2 and is
// copied verbatim, because an AMV stream is already escaped. Getting that backwards produces a
// stream that decodes for a while and then desynchronises at the first 0xFF.

using System;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary><c>sp5xdec.c</c> - AMV and SP5X, decoded by rebuilding a JPEG around the payload.</summary>
public static class Sp5xDec
{
    /// <summary>The extra room <c>av_mallocz(buf_size + 1024)</c> gives the rebuilt stream.</summary>
    private const int RecodeHeadroom = 1024;

    /// <summary>
    /// <c>sp5x_decode_frame</c> - rebuild a JPEG around the payload and decode it.
    /// </summary>
    /// <param name="s">The decoder context.</param>
    /// <param name="buf">The packet.</param>
    /// <param name="bufSize">Its length.</param>
    /// <param name="width">
    /// <c>avctx-&gt;coded_width</c>. The packet does not carry the picture size - the
    /// container does - and the C refuses the frame outright when it is unset.
    /// </param>
    /// <param name="height"><c>avctx-&gt;coded_height</c>.</param>
    /// <param name="isAmv">
    /// True for AMV, false for SP5X. The one difference between the two.
    /// </param>
    /// <param name="scratch">
    /// The buffer the rebuilt JPEG is written into, grown by the caller so a per-frame
    /// allocation is not made here.
    /// </param>
    /// <returns>The bytes consumed, or a negative error.</returns>
    public static int Sp5xDecodeFrame(MJpegDecodeContext s, byte[] buf, int bufSize,
                                      int width, int height, bool isAmv, ref byte[] scratch)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(buf);

        if (width == 0 || height == 0)
            return -1;      // the C's bare `return -1`, not an AVERROR

        int capacity = bufSize + RecodeHeadroom;
        if (scratch.Length < capacity + MjpegDec.InputBufferPaddingSize)
            scratch = new byte[capacity + MjpegDec.InputBufferPaddingSize];

        byte[] recoded = scratch;
        Array.Clear(recoded);           // av_mallocz
        int j = 0;

        // SOI
        recoded[j++] = 0xFF;
        recoded[j++] = 0xD8;

        Sp5xTables.DataDqt.CopyTo(recoded.AsSpan(j));
        Sp5xTables.QscaleFiveQuantTableLuma.CopyTo(recoded.AsSpan(j + 5));
        Sp5xTables.QscaleFiveQuantTableChroma.CopyTo(recoded.AsSpan(j + 70));
        j += Sp5xTables.DataDqt.Length;

        Sp5xTables.DataDht.CopyTo(recoded.AsSpan(j));
        j += Sp5xTables.DataDht.Length;

        Sp5xTables.DataSof.CopyTo(recoded.AsSpan(j));
        recoded[j + 5] = (byte)(height >> 8);       // AV_WB16(recoded+j+5, coded_height)
        recoded[j + 6] = (byte)height;
        recoded[j + 7] = (byte)(width >> 8);        // AV_WB16(recoded+j+7, coded_width)
        recoded[j + 8] = (byte)width;
        j += Sp5xTables.DataSof.Length;

        Sp5xTables.DataSos.CopyTo(recoded.AsSpan(j));
        j += Sp5xTables.DataSos.Length;

        if (isAmv)
        {
            // Already escaped, and the first two bytes are not payload.
            for (int i = 2; i < bufSize - 2 && j < capacity - 2; i++)
                recoded[j++] = buf[i];
        }
        else
        {
            // SP5X is unescaped, so every 0xFF has to be stuffed on the way in.
            for (int i = 14; i < bufSize && j < capacity - 3; i++)
            {
                recoded[j++] = buf[i];
                if (buf[i] == 0xFF)
                    recoded[j++] = 0;
            }
        }

        // EOI
        recoded[j++] = 0xFF;
        recoded[j++] = 0xD9;

        int ret = MjpegDec.DecodeImage(s, recoded, j);
        return ret < 0 ? ret : bufSize;
    }
}

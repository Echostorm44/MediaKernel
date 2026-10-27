// Ported from FFmpeg's libavcodec/bsf/media100_to_mjpegb.c - the bitstream filter that turns
// a Media 100 packet into an MJPEG-B one.
//
// Media 100 is MJPEG-B with the JPEG segments left out: a packet carries its quantisation
// tables as 32-bit values and then the entropy data, and nothing else - no header, no SOF, no
// SOS. This builds the missing structure around it, which is why ff_media100_decoder in the C
// is ff_mjpegb_decoder with `.bsfs = "media100_to_mjpegb"` and no decoder code of its own.
//
// Everything written here is CONSTANT except the two quantisation tables, the picture size
// and the offsets - the SOF and SOS the filter emits are fixed 4:2:2 three-component segments,
// because that is the only shape Media 100 has.

using System;
using System.Buffers.Binary;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary><c>ff_media100_to_mjpegb_bsf</c> - Media 100 to MJPEG-B.</summary>
public static class Media100ToMjpegb
{
    /// <summary><c>av_new_packet(out, in->size + 1024)</c>.</summary>
    private const int Headroom = 1024;

    /// <summary>
    /// <c>filter</c> - rewrite one packet.
    /// </summary>
    /// <param name="input">The Media 100 packet.</param>
    /// <param name="width"><c>par_in-&gt;width</c>.</param>
    /// <param name="height">
    /// <c>par_in-&gt;height</c>. The SOF the filter writes carries HALF of it, because a
    /// Media 100 packet is two fields and each one is half the frame.
    /// </param>
    /// <param name="output">
    /// The buffer the MJPEG-B packet is written into, grown by the caller.
    /// </param>
    /// <returns>The length of the rewritten packet, or a negative error.</returns>
    public static int Filter(ReadOnlySpan<byte> input, int width, int height,
                             ref byte[] output)
    {
        int capacity = input.Length + Headroom;
        if (output.Length < capacity + MjpegDec.InputBufferPaddingSize)
            output = new byte[capacity + MjpegDec.InputBufferPaddingSize];

        Array.Clear(output);

        Span<byte> pb = output;
        int put = 0;                    // bytestream2_tell_p
        int get = 0;                    // bytestream2_tell

        Span<int> sofOffset = stackalloc int[2];
        Span<int> sosOffset = stackalloc int[2];
        Span<int> dqtOffset = stackalloc int[2];
        Span<int> dhtOffset = stackalloc int[2];
        Span<int> sodOffset = stackalloc int[2];

        int secondFieldOffset = 0;
        int nextField = 0;

        for (int field = 0; field < 2; field++)
        {
            // The MJPEG-B frame header: a reserved zero, the 'mjpg' tag, the field sizes,
            // and six offset slots that are patched at the end once they are known.
            PutBe32(pb, ref put, 0);
            PutBe32(pb, ref put, 0x6D6A7067);       // AV_RB32("mjpg")
            PutBe32(pb, ref put, 0);
            PutBe32(pb, ref put, 0);
            for (int i = 0; i < 6; i++)
                PutBe32(pb, ref put, 0);

            // SOF. Fixed: 8 bits, three components, 2x1 luma and 1x1 chroma - 4:2:2, which
            // is the only shape Media 100 has - and the FIELD height, not the frame's.
            sofOffset[field] = put;
            PutBe16(pb, ref put, 17);
            pb[put++] = 8;
            PutBe16(pb, ref put, (ushort)(height / 2));
            PutBe16(pb, ref put, (ushort)width);
            pb[put++] = 3;
            pb[put++] = 1; pb[put++] = 0x21; pb[put++] = 0;
            pb[put++] = 2; pb[put++] = 0x11; pb[put++] = 1;
            pb[put++] = 3; pb[put++] = 0x11; pb[put++] = 1;

            // SOS, equally fixed.
            sosOffset[field] = put;
            PutBe16(pb, ref put, 12);
            pb[put++] = 3;
            pb[put++] = 1; pb[put++] = 0;
            pb[put++] = 2; pb[put++] = 0x11;
            pb[put++] = 3; pb[put++] = 0x11;
            pb[put++] = 0; pb[put++] = 0; pb[put++] = 0;

            // DQT. The input stores each coefficient as a 32-BIT big-endian value and only
            // its low byte is kept, which is the one thing here that reads the packet.
            dqtOffset[field] = put;
            PutBe16(pb, ref put, 132);
            pb[put++] = 0;
            get += 4;
            for (int i = 0; i < 64; i++)
                pb[put++] = (byte)GetBe32(input, ref get);
            pb[put++] = 1;
            for (int i = 0; i < 64; i++)
                pb[put++] = (byte)GetBe32(input, ref get);

            // No DHT at all: the decoder falls back to the standard tables, which is what a
            // zero offset means to the MJPEG-B frame reader.
            dhtOffset[field] = 0;
            sodOffset[field] = put;

            // Where the second field starts: the first 0x00000001 at or after the current
            // position plus eight. On the SECOND pass nextField is already the packet's
            // length, so this loop does not run - the C expresses that with its
            // `next_field == 0` condition rather than with a branch.
            for (int i = get + 8; nextField == 0 && i < input.Length - 4; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(input[i..]) == 1)
                {
                    nextField = i;
                    break;
                }
            }

            get += 8;
            int run = nextField - get;
            if (run < 0 || get + run > input.Length || put + run + 8 > capacity)
                return MjpegDec.AverrorInvalidData;

            input.Slice(get, run).CopyTo(pb[put..]);
            get += run;
            put += run;

            PutBe32(pb, ref put, 0);        // bytestream2_put_be64(&pb, 0)
            PutBe32(pb, ref put, 0);

            if (field == 0)
            {
                secondFieldOffset = put;
                nextField = input.Length;
            }
        }

        // The six offsets of the first field's header, plus the three copies of the second
        // field's position that the frame reader uses as its field size.
        WriteBe32(pb, 8, (uint)secondFieldOffset);
        WriteBe32(pb, 12, (uint)secondFieldOffset);
        WriteBe32(pb, 16, (uint)secondFieldOffset);
        WriteBe32(pb, 20, (uint)dqtOffset[0]);
        WriteBe32(pb, 24, (uint)dhtOffset[0]);
        WriteBe32(pb, 28, (uint)sofOffset[0]);
        WriteBe32(pb, 32, (uint)sosOffset[0]);
        WriteBe32(pb, 36, (uint)sodOffset[0]);

        // The second field's are relative to its own header, and its "second field offset"
        // is zero because there is no third.
        WriteBe32(pb, secondFieldOffset + 8, (uint)(put - secondFieldOffset));
        WriteBe32(pb, secondFieldOffset + 12, (uint)(put - secondFieldOffset));
        WriteBe32(pb, secondFieldOffset + 16, 0);
        WriteBe32(pb, secondFieldOffset + 20, (uint)(dqtOffset[1] - secondFieldOffset));
        WriteBe32(pb, secondFieldOffset + 24, (uint)dhtOffset[1]);
        WriteBe32(pb, secondFieldOffset + 28, (uint)(sofOffset[1] - secondFieldOffset));
        WriteBe32(pb, secondFieldOffset + 32, (uint)(sosOffset[1] - secondFieldOffset));
        WriteBe32(pb, secondFieldOffset + 36, (uint)(sodOffset[1] - secondFieldOffset));

        return put;
    }

    private static void PutBe16(Span<byte> pb, ref int put, ushort v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(pb[put..], v);
        put += 2;
    }

    private static void PutBe32(Span<byte> pb, ref int put, uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(pb[put..], v);
        put += 4;
    }

    private static void WriteBe32(Span<byte> pb, int at, uint v) =>
        BinaryPrimitives.WriteUInt32BigEndian(pb[at..], v);

    private static uint GetBe32(ReadOnlySpan<byte> gb, ref int get)
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(gb[get..]);
        get += 4;
        return v;
    }
}

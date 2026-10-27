// MediaKernel - TTA (True Audio) lossless decoder.
//
// A translation of ffmpeg-master/libavcodec/tta.c, ttadata.c and ttadsp.c. TTA is adaptive
// Rice coding followed by an eight-tap sign-sign LMS filter and a fixed-order predictor, and
// every one of those three stages is defined in terms of wrapping unsigned arithmetic - the C
// aliases int32_t buffers as uint32_t in two places specifically to get it. Those casts are
// reproduced rather than tidied; see the notes on FilterProcess and Rice below.
//
// The bit reader is get_bits.h in BITSTREAM_READER_LE mode: bits come out of each byte
// least-significant first, bytes in file order.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Tta;

/// <summary><c>TTAFilter</c> of ttadata.h.</summary>
public struct TtaFilter
{
    public const int MaxOrder = 16;

    public int Shift;
    public int Round;
    public int Error;

    public TtaArray16 Qm;
    public TtaArray16 Dx;
    public TtaArray16 Dl;
}

/// <summary><c>int32_t [MAX_ORDER]</c>, inline so a channel's state is one allocation.</summary>
[InlineArray(TtaFilter.MaxOrder)]
public struct TtaArray16
{
    private int element;
}

/// <summary><c>TTARice</c> of ttadata.h.</summary>
public struct TtaRice
{
    public uint K0, K1, Sum0, Sum1;
}

/// <summary><c>TTAChannel</c> of ttadata.h.</summary>
public struct TtaChannel
{
    public int Predictor;
    public TtaFilter Filter;
    public TtaRice Rice;
}

/// <summary>ttadata.c - the two shift tables and the per-depth filter orders.</summary>
public static class TtaData
{
    /// <summary><c>ff_tta_shift_1[41]</c>.</summary>
    public static ReadOnlySpan<uint> Shift1 =>
    [
        0x00000001, 0x00000002, 0x00000004, 0x00000008,
        0x00000010, 0x00000020, 0x00000040, 0x00000080,
        0x00000100, 0x00000200, 0x00000400, 0x00000800,
        0x00001000, 0x00002000, 0x00004000, 0x00008000,
        0x00010000, 0x00020000, 0x00040000, 0x00080000,
        0x00100000, 0x00200000, 0x00400000, 0x00800000,
        0x01000000, 0x02000000, 0x04000000, 0x08000000,
        0x10000000, 0x20000000, 0x40000000, 0x80000000,
        0x80000000, 0x80000000, 0x80000000, 0x80000000,
        0x80000000, 0x80000000, 0x80000000, 0x80000000,
        0xFFFFFFFF,
    ];

    /// <summary>
    /// <c>ff_tta_shift_16</c>, which in the C is <c>ff_tta_shift_1 + 4</c> - the same table
    /// read from its fifth entry, not a second table.
    /// </summary>
    public static uint Shift16(uint index) => Shift1[(int)index + 4];

    /// <summary><c>ff_tta_filter_configs[4]</c> - the filter shift for each byte width.</summary>
    public static ReadOnlySpan<byte> FilterConfigs => [10, 9, 10, 12];

    /// <summary><c>ff_tta_rice_init</c>.</summary>
    public static void RiceInit(ref TtaRice c, uint k0, uint k1)
    {
        c.K0 = k0;
        c.K1 = k1;
        c.Sum0 = Shift16(k0);
        c.Sum1 = Shift16(k1);
    }

    /// <summary><c>ff_tta_filter_init</c>.</summary>
    public static void FilterInit(ref TtaFilter c, int shift)
    {
        c = default;
        c.Shift = shift;
        c.Round = (int)Shift1[shift - 1];
    }
}

/// <summary>ttadsp.c's <c>tta_filter_process_c</c>.</summary>
public static class TtaDsp
{
    /// <summary>
    /// The eight-tap sign-sign LMS filter, one sample in place.
    /// </summary>
    /// <remarks>
    /// The C aliases <c>qm</c> as <c>uint32_t *</c> for the whole function, so both the
    /// coefficient update and the dot product are unsigned and wrap; <c>round</c> is an
    /// <c>int32_t</c> that the unsigned sum is assigned back into. Doing any of that in
    /// checked or widened arithmetic changes the output on real files, because the filter is
    /// deliberately allowed to overflow.
    /// </remarks>
    public static void FilterProcess(
        ref TtaFilter f, ref int input, int shift, int round)
    {
        Span<int> qm = f.Qm;
        Span<int> dx = f.Dx;
        Span<int> dl = f.Dl;

        unchecked
        {
            if (f.Error < 0)
            {
                qm[0] -= dx[0]; qm[1] -= dx[1]; qm[2] -= dx[2]; qm[3] -= dx[3];
                qm[4] -= dx[4]; qm[5] -= dx[5]; qm[6] -= dx[6]; qm[7] -= dx[7];
            }
            else if (f.Error > 0)
            {
                qm[0] += dx[0]; qm[1] += dx[1]; qm[2] += dx[2]; qm[3] += dx[3];
                qm[4] += dx[4]; qm[5] += dx[5]; qm[6] += dx[6]; qm[7] += dx[7];
            }

            uint acc = (uint)round;
            acc += (uint)dl[0] * (uint)qm[0] + (uint)dl[1] * (uint)qm[1]
                 + (uint)dl[2] * (uint)qm[2] + (uint)dl[3] * (uint)qm[3]
                 + (uint)dl[4] * (uint)qm[4] + (uint)dl[5] * (uint)qm[5]
                 + (uint)dl[6] * (uint)qm[6] + (uint)dl[7] * (uint)qm[7];
            round = (int)acc;

            dx[0] = dx[1]; dx[1] = dx[2]; dx[2] = dx[3]; dx[3] = dx[4];
            dl[0] = dl[1]; dl[1] = dl[2]; dl[2] = dl[3]; dl[3] = dl[4];

            dx[4] = (dl[4] >> 30) | 1;
            dx[5] = ((dl[5] >> 30) | 2) & ~1;
            dx[6] = ((dl[6] >> 30) | 2) & ~1;
            dx[7] = ((dl[7] >> 30) | 4) & ~3;

            f.Error = input;
            input += round >> shift;

            dl[4] = -dl[5]; dl[5] = -dl[6];
            dl[6] = input - dl[7]; dl[7] = input;
            dl[5] += dl[6]; dl[4] += dl[5];
        }
    }
}

/// <summary>
/// get_bits.h with BITSTREAM_READER_LE: bits least-significant-first inside each byte, bytes
/// in file order. TTA is the only format here that reads that way.
/// </summary>
public ref struct TtaBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> buffer = data;
    private int index;

    public readonly int SizeInBits => buffer.Length * 8;

    public readonly int BitsLeft => SizeInBits - index;

    /// <summary><c>get_bits1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit1()
    {
        if (index >= SizeInBits) { index++; return 0; }
        int bit = (buffer[index >> 3] >> (index & 7)) & 1;
        index++;
        return bit;
    }

    /// <summary><c>get_bits</c>, up to 25 bits.</summary>
    public uint GetBits(int n)
    {
        uint value = 0;
        for (int i = 0; i < n; i++)
            value |= (uint)GetBit1() << i;
        return value;
    }

    /// <summary><c>skip_bits_long</c>.</summary>
    public void SkipBits(int n) => index += n;

    /// <summary><c>align_get_bits</c>.</summary>
    public void Align()
    {
        int n = -index & 7;
        if (n != 0) SkipBits(n);
    }

    /// <summary>unary.h's <c>get_unary(gb, 0, len)</c> - ones until a zero, which it eats.</summary>
    public uint GetUnary(int len)
    {
        int i;
        for (i = 0; i < len && GetBit1() != 0; i++) { }
        return (uint)i;
    }
}

/// <summary>
/// The TTA decoder proper. One instance per stream; the filter and Rice state are reset at
/// the start of every frame, so a frame is independent of the one before it.
/// </summary>
public sealed class TtaDecoder
{
    private const int FormatSimple = 1;
    private const int FormatEncrypted = 2;

    /// <summary><c>MIN_CACHE_BITS</c> of get_bits.h on a 32-bit cache.</summary>
    private const int MinCacheBits = 25;

    private TtaChannel[] channelContexts = [];
    private int[] decodeBuffer = [];

    /// <summary>1 for simple, 2 for encrypted - which this decoder refuses.</summary>
    public int Format { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Bytes per sample: 1, 2 or 3.</summary>
    public int Bps { get; private set; }

    public int BitsPerRawSample { get; private set; }

    public int SampleRate { get; private set; }

    /// <summary>Total sample frames the header declares.</summary>
    public uint DataLength { get; private set; }

    public int FrameLength { get; private set; }

    public int LastFrameLength { get; private set; }

    /// <summary><c>tta_decode_init</c> - everything comes out of the 22-byte TTA1 header.</summary>
    public void Initialize(ReadOnlySpan<byte> extradata)
    {
        // 22 bytes for a TTA1 header
        if (extradata.Length < 22)
            throw new InvalidOperationException("TTA: extradata is shorter than a TTA1 header.");

        var gb = new TtaBitReader(extradata);

        if (gb.GetBits(8) != 'T' || gb.GetBits(8) != 'T' || gb.GetBits(8) != 'A' || gb.GetBits(8) != '1')
            throw new InvalidOperationException("TTA: wrong extradata present.");

        Format = (int)gb.GetBits(16);
        if (Format > 2)
            throw new InvalidOperationException($"TTA: invalid format {Format}.");
        if (Format == FormatEncrypted)
            throw new NotSupportedException(
                "TTA: the stream is encrypted. The C takes a password option; nothing in this "
              + "tree carries one, so the stream is refused rather than decoded to noise.");

        Channels = (int)gb.GetBits(16);
        BitsPerRawSample = (int)gb.GetBits(16);
        Bps = (BitsPerRawSample + 7) / 8;
        SampleRate = (int)gb.GetBits(32);
        DataLength = gb.GetBits(32);
        gb.SkipBits(32); // CRC32 of header

        if (Channels == 0 || Channels > 16)
            throw new InvalidOperationException($"TTA: invalid number of channels {Channels}.");
        if (SampleRate == 0)
            throw new InvalidOperationException("TTA: invalid samplerate.");

        if (Bps is < 1 or > 3)
            throw new NotSupportedException(
                $"TTA: {BitsPerRawSample} bits per sample. The C handles 8, 16 and 24 and "
              + "rejects everything else, including the 32-bit case its own switch comments out.");

        // prevent overflow
        if (SampleRate > 0x7FFFFF)
            throw new InvalidOperationException("TTA: sample_rate too large.");

        FrameLength = 256 * SampleRate / 245;
        LastFrameLength = (int)(DataLength % (uint)FrameLength);

        if (FrameLength >= int.MaxValue / (Channels * sizeof(int)))
            throw new InvalidOperationException("TTA: frame_length too large.");

        // allocate_buffers
        decodeBuffer = new int[FrameLength * Channels];
        channelContexts = new TtaChannel[Channels];
    }

    /// <summary>
    /// <c>tta_decode_frame</c>. Writes interleaved samples into <paramref name="output"/> and
    /// returns how many sample frames it produced.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: the C decodes 24-bit straight into the output frame by pointing
    /// decode_buffer at it, and allocates a separate buffer otherwise. Here there is always a
    /// separate buffer and the conversion always copies, which costs one pass over a frame and
    /// removes the aliasing the C has to undo on every error path.
    /// </remarks>
    public int DecodeFrame(ReadOnlySpan<byte> packet, Span<int> output)
    {
        if (channelContexts.Length == 0)
            throw new InvalidOperationException("TTA: DecodeFrame before Initialize.");

        var gb = new TtaBitReader(packet);

        int framelen = FrameLength;
        int curChan = 0;

        // init per channel states
        for (int i = 0; i < Channels; i++)
        {
            channelContexts[i].Predictor = 0;
            TtaData.FilterInit(ref channelContexts[i].Filter, TtaData.FilterConfigs[Bps - 1]);
            TtaData.RiceInit(ref channelContexts[i].Rice, 10, 10);
        }

        int[] buf = decodeBuffer;
        int total = framelen * Channels;
        int i2 = 0;
        int p = 0;

        for (; p < total; p++)
        {
            ref TtaChannel ch = ref channelContexts[curChan];
            ref TtaRice rice = ref ch.Rice;

            uint depth, k;
            int value;

            uint unary = gb.GetUnary(gb.BitsLeft);

            if (unary == 0)
            {
                depth = 0;
                k = rice.K0;
            }
            else
            {
                depth = 1;
                k = rice.K1;
                unary--;
            }

            if (gb.BitsLeft < k)
                throw new InvalidOperationException("TTA: the frame ends inside a Rice code.");

            if (k != 0)
            {
                if (k > MinCacheBits || unary > int.MaxValue >> (int)k)
                    throw new InvalidOperationException("TTA: a Rice code does not fit in 32 bits.");
                value = (int)((unary << (int)k) + gb.GetBits((int)k));
            }
            else
            {
                value = (int)unary;
            }

            // FIXME: copy paste from original
            unchecked
            {
                if (depth == 1)
                {
                    rice.Sum1 += (uint)value - (rice.Sum1 >> 4);
                    if (rice.K1 > 0 && rice.Sum1 < TtaData.Shift16(rice.K1))
                        rice.K1--;
                    else if (rice.Sum1 > TtaData.Shift16(rice.K1 + 1))
                        rice.K1++;
                    value = (int)((uint)value + TtaData.Shift1[(int)rice.K0]);
                }

                rice.Sum0 += (uint)value - (rice.Sum0 >> 4);
                if (rice.K0 > 0 && rice.Sum0 < TtaData.Shift16(rice.K0))
                    rice.K0--;
                else if (rice.Sum0 > TtaData.Shift16(rice.K0 + 1))
                    rice.K0++;

                // extract coded value
                buf[p] = 1 + ((value >> 1) ^ ((value & 1) - 1));

                // run hybrid filter
                TtaDsp.FilterProcess(ref ch.Filter, ref buf[p], ch.Filter.Shift, ch.Filter.Round);

                // fixed order prediction
                buf[p] += Bps switch
                {
                    1 => Pred(ch.Predictor, 4),
                    2 or 3 => Pred(ch.Predictor, 5),
                    _ => ch.Predictor,
                };
                ch.Predictor = buf[p];

                // flip channels
                if (curChan < Channels - 1)
                {
                    curChan++;
                }
                else
                {
                    // decorrelate in case of multiple channels
                    if (Channels > 1)
                    {
                        int r = p - 1;
                        buf[p] += buf[r] / 2;
                        for (; r > p - Channels; r--)
                            buf[r] = buf[r + 1] - buf[r];
                    }
                    curChan = 0;
                    i2++;
                    // check for last frame
                    if (i2 == LastFrameLength && gb.BitsLeft / 8 == 4)
                    {
                        framelen = LastFrameLength;
                        p++;
                        break;
                    }
                }
            }
        }

        gb.Align();
        if (gb.BitsLeft < 32)
            throw new InvalidOperationException("TTA: the frame ends before its own checksum.");
        gb.SkipBits(32); // frame crc

        // convert to output buffer
        int count = framelen * Channels;
        switch (Bps)
        {
            case 1:
                for (int i = 0; i < count; i++) output[i] = buf[i] + 0x80;
                break;
            case 2:
                for (int i = 0; i < count; i++) output[i] = (short)buf[i];
                break;
            case 3:
                // shift samples for 24-bit sample format
                for (int i = 0; i < count; i++) output[i] = unchecked(buf[i] * 256);
                break;
        }

        return framelen;
    }

    /// <summary>
    /// The C's <c>PRED(x, k)</c>. The shift is done in 64 bits on a sign-extended value and
    /// the result is truncated back to 32, which is not the same as <c>x - (x &gt;&gt; k)</c>
    /// once x is large enough for the shift to carry out of the low word.
    /// </summary>
    private static int Pred(int x, int k)
        => (int)(((((ulong)(long)x) << k) - (ulong)(long)x) >> k);
}

/// <summary>The registry entry for TTA.</summary>
[Codec("tta", CodecType.Audio, DisplayName = "TTA (True Audio) Software Decoder", Priority = 5)]
public sealed class TtaDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly TtaDecoder decoder = new();
    private int[] scratch = [];
    private bool configured;

    public override string CodecId => "tta";

    public override int SampleRate => decoder.SampleRate;

    public override int Channels => decoder.Channels;

    /// <summary>
    /// TTA configures itself entirely from the 22-byte file header, which the demuxer hands
    /// over as codec private data - there is nothing in a frame that says the rate or the
    /// width, which is why ffmpeg's decoder refuses a stream with no extradata.
    /// </summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.Length < 22) return false;
        decoder.Initialize(codecPrivate);
        scratch = new int[decoder.FrameLength * decoder.Channels];
        configured = true;
        return true;
    }

    /// <summary>
    /// Nothing to flush: every TTA frame resets the filter, the Rice state and the predictor
    /// before its first sample, so a frame decoded after a seek is as correct as one decoded
    /// in sequence.
    /// </summary>
    public override void Flush() { }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!configured)
            throw new InvalidOperationException("tta: Decode before Initialize.");
        if (data.Length == 0) return null;

        int frames = decoder.DecodeFrame(data, scratch);
        if (frames <= 0) return null;

        int total = frames * decoder.Channels;
        EnsureBuffer(total);

        // The C's sample formats are U8, S16 and S32-holding-24-bit; this pipeline is float.
        float scale = decoder.Bps switch
        {
            1 => 1f / 128f,
            2 => 1f / 32768f,
            _ => 1f / 2147483648f,
        };
        int bias = decoder.Bps == 1 ? -128 : 0;
        for (int i = 0; i < total; i++)
            SampleBuffer[i] = (scratch[i] + bias) * scale;

        return MakeFrame(frames, presentationTimeTicks);
    }
}

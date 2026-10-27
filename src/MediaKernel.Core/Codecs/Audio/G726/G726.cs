// Translated from ffmpeg-master/libavcodec/g726.c - the decoder half.
//
// G.726 is ITU-T's ADPCM for telephony, and the C file calls itself "a very straightforward
// rendition of the G.726 Section 4 Computational Details". It is the one codec in this tree
// whose arithmetic is specified as an 11-bit floating point format with a 1-bit sign, a 4-bit
// exponent and a 6-bit mantissa (Float11 below), multiplied by adding exponents and shifting -
// not an approximation of the spec but the spec itself, which is why every intermediate here
// is an exact translation and not a simplification.
//
// FOUR FUNCTIONS HERE RETURN int16_t FROM ARITHMETIC THAT A C# int WOULD KEEP, so each one
// casts to short where the C declared the narrower type. A perturbation sweep says two of the
// four are observable and two are not, and the difference is worth writing down rather than
// claiming all four as load-bearing:
//
//   mult         OBSERVABLE. The 11-bit product is shifted LEFT when the exponents are large,
//                so it leaves int16 on loud audio and the wrap changes the predictor.
//   g726_decode  OBSERVABLE. re_signal * 4 is clipped to +/-0xffff and THEN truncated, so a
//                sample past full scale wraps instead of clipping - a square wave at 0.98 of
//                full scale produces 4664 sign flips between values above 30000 in two
//                seconds, which is what wrapping looks like and what clamping cannot make.
//   inverse_quant  UNREACHABLE, and arithmetic rather than luck says so. y is clamped by the
//                yu clamp (544..5120) and the yl feedback to 5120, so dql is at most 566 +
//                1280, which gives dex 14 and dqt 182: (182 << 14) >> 7 is 23296.
//   re_signal    UNREACHABLE on any stream whose predictor tracks its signal. se is a sum of
//                eight mult() results and could in principle reach 262136, but it is an
//                estimate of a signal that fits in int16, and nothing in this tree or in two
//                seconds of full-scale square wave takes it out of range.
//
// Both unreachable casts stay, because they are what the C declares and the cost is nothing.
// What they do not get is a comment calling them load-bearing when the gate cannot see them.
//
// SCOPE: the encoder (quant, g726_encode and the four quant tables it needs) is not ported.
// Nothing in this project encodes G.726, and an ungated encoder is what this tree's own history
// warns about. The entries stay in the port checklist as `diverged` with that reason.

using System;

namespace MediaKernel.Core.Codecs.Audio.G726;

/// <summary>
/// The G.726 11-bit float: 1 bit of sign, 4 bits of exponent, 6 bits of mantissa.
/// </summary>
/// <remarks>
/// <c>Float11</c> in the C, field for field. A struct rather than a class because the context
/// holds eight of them and copies them around (<c>c-&gt;sr[1] = c-&gt;sr[0]</c>,
/// <c>c-&gt;dq[i] = c-&gt;dq[i-1]</c>), which is an assignment of the whole value in C and has
/// to stay one here.
/// </remarks>
internal struct Float11
{
    /// <summary>1 bit sign.</summary>
    public byte Sign;

    /// <summary>4 bits exponent.</summary>
    public byte Exp;

    /// <summary>6 bits mantissa.</summary>
    public byte Mant;
}

/// <summary>
/// The decode-side tables for one bit rate: <c>G726Tables</c> in the C, minus the
/// <c>quant</c> table that only the encoder reads.
/// </summary>
internal readonly struct G726Tables(short[] iquant, short[] w, byte[] f)
{
    /// <summary>Inverse quantization table.</summary>
    public readonly short[] Iquant = iquant;

    /// <summary>Special table #1 ;-)</summary>
    public readonly short[] W = w;

    /// <summary>Special table #2</summary>
    public readonly byte[] F = f;
}

/// <summary>
/// G.726 ADPCM, 2 to 5 bits per sample (16, 24, 32 and 40 kbit/s).
/// </summary>
/// <remarks>
/// <para>
/// The decoder is a single running state machine over the whole stream: every sample updates
/// the predictor coefficients, the scale factors and the tone detector, so a packet boundary
/// is not a resynchronisation point and the only thing a packet has to be is a whole number
/// of codes. That is why <c>adpcm_g726</c> has no block alignment worth the name - the WAV
/// format chunk says 1 - and why the decoder keeps its context across calls.
/// </para>
/// <para>
/// MONO ONLY, as in the C: <c>g726_decode_init</c> refuses more than one channel outright
/// rather than decoding the channels as one interleaved stream, which is what the running
/// state would make of them.
/// </para>
/// </remarks>
public sealed class G726
{
    // -- Tables (g726.c) -------------------------------------------------

    private static readonly short[] IquantTbl16 = [116, 365, 365, 116];
    private static readonly short[] WTbl16 = [-22, 439, 439, -22];
    private static readonly byte[] FTbl16 = [0, 7, 7, 0];

    private static readonly short[] IquantTbl24 =
        [short.MinValue, 135, 273, 373, 373, 273, 135, short.MinValue];
    private static readonly short[] WTbl24 = [-4, 30, 137, 582, 582, 137, 30, -4];
    private static readonly byte[] FTbl24 = [0, 1, 2, 7, 7, 2, 1, 0];

    private static readonly short[] IquantTbl32 =
    [
        short.MinValue,   4, 135, 213, 273, 323, 373, 425,
                   425, 373, 323, 273, 213, 135,   4, short.MinValue,
    ];
    private static readonly short[] WTbl32 =
    [
         -12,  18,  41,  64, 112, 198, 355, 1122,
        1122, 355, 198, 112,  64,  41,  18,  -12,
    ];
    private static readonly byte[] FTbl32 = [0, 0, 0, 1, 1, 1, 3, 7, 7, 3, 1, 1, 1, 0, 0, 0];

    private static readonly short[] IquantTbl40 =
    [
        short.MinValue, -66,  28, 104, 169, 224, 274, 318,
                   358, 395, 429, 459, 488, 514, 539, 566,
                   566, 539, 514, 488, 459, 429, 395, 358,
                   318, 274, 224, 169, 104,  28, -66, short.MinValue,
    ];
    private static readonly short[] WTbl40 =
    [
         14,  14,  24,  39,  40,  41,  58, 100,
        141, 179, 219, 280, 358, 440, 529, 696,
        696, 529, 440, 358, 280, 219, 179, 141,
        100,  58,  41,  40,  39,  24,  14,  14,
    ];
    private static readonly byte[] FTbl40 =
    [
        0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 6,
        6, 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
    ];

    /// <summary><c>G726Tables_pool</c>, indexed by <c>code_size - 2</c>.</summary>
    private static readonly G726Tables[] G726TablesPool =
    [
        new(IquantTbl16, WTbl16, FTbl16),
        new(IquantTbl24, WTbl24, FTbl24),
        new(IquantTbl32, WTbl32, FTbl32),
        new(IquantTbl40, WTbl40, FTbl40),
    ];

    // -- Context (G726Context) -------------------------------------------

    private G726Tables tbls;

    /// <summary>Previous reconstructed samples.</summary>
    private readonly Float11[] sr = new Float11[2];

    /// <summary>Previous differences.</summary>
    private readonly Float11[] dq = new Float11[6];

    /// <summary>Second order predictor coefficients.</summary>
    private readonly int[] a = new int[2];

    /// <summary>Sixth order predictor coefficients.</summary>
    private readonly int[] b = new int[6];

    /// <summary>Signs of the previous two <c>sez + dq</c>.</summary>
    private readonly int[] pk = new int[2];

    /// <summary>Scale factor control.</summary>
    private int ap;

    /// <summary>Fast scale factor.</summary>
    private int yu;

    /// <summary>Slow scale factor.</summary>
    private int yl;

    /// <summary>Short average magnitude of F[i].</summary>
    private int dms;

    /// <summary>Long average magnitude of F[i].</summary>
    private int dml;

    /// <summary>Tone detect.</summary>
    private int td;

    /// <summary>Estimated signal for the next iteration.</summary>
    private int se;

    /// <summary>Estimated second order prediction.</summary>
    private int sez;

    /// <summary>Quantizer scaling factor for the next iteration.</summary>
    private int y;

    private int codeSize;
    private bool littleEndian;

    /// <summary>Bits per code: 2, 3, 4 or 5. <c>code_size</c> in the C.</summary>
    public int CodeSize => codeSize;

    /// <summary>
    /// Whether the codes are packed least-significant-bit first, which is what AIFF and Sun AU
    /// carry and what the C calls <c>g726le</c>. <c>little_endian</c> in the C.
    /// </summary>
    public bool LittleEndian => littleEndian;

    /// <summary>
    /// <c>g726_decode_init</c>. <paramref name="bitsPerCodedSample"/> is
    /// <c>avctx-&gt;bits_per_coded_sample</c>, which for a WAV is the bit rate divided by the
    /// sample rate.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The code size is outside 2..5, which the C rejects with AVERROR(EINVAL) rather than
    /// clamping - a wrong code size decodes to noise.
    /// </exception>
    public void G726DecodeInit(int bitsPerCodedSample, bool packedLittleEndian)
    {
        if (bitsPerCodedSample < 2 || bitsPerCodedSample > 5)
            throw new ArgumentOutOfRangeException(
                nameof(bitsPerCodedSample), bitsPerCodedSample,
                "G.726: invalid number of bits; g726.c accepts 2 to 5.");

        codeSize = bitsPerCodedSample;
        littleEndian = packedLittleEndian;
        G726Reset();
    }

    /// <summary><c>g726_reset</c>.</summary>
    public void G726Reset()
    {
        tbls = G726TablesPool[codeSize - 2];

        Array.Clear(sr);
        Array.Clear(dq);
        Array.Clear(a);
        Array.Clear(b);

        for (int i = 0; i < 2; i++)
        {
            sr[i].Mant = 1 << 5;
            pk[i] = 1;
        }
        for (int i = 0; i < 6; i++)
            dq[i].Mant = 1 << 5;

        ap = 0;
        dms = 0;
        dml = 0;
        td = 0;
        se = 0;
        sez = 0;

        yu = 544;
        yl = 34816;

        y = 544;
    }

    /// <summary>
    /// <c>g726_decode_frame</c>: <c>buf_size * 8 / code_size</c> samples, one per code.
    /// </summary>
    /// <remarks>
    /// The C logs "Frame invalidly split, missing parser?" when bits are left over, because
    /// the leftover bits are the start of a code whose remaining bits are in the next packet
    /// and are silently dropped. That is a container fault rather than a bitstream one, so
    /// this throws: a packet size that is not a whole number of codes decodes everything
    /// after it at the wrong bit offset.
    /// </remarks>
    /// <returns>The number of samples written.</returns>
    public int G726DecodeFrame(ReadOnlySpan<byte> buf, Span<short> samples)
    {
        if (codeSize == 0)
            throw new InvalidOperationException("G.726: decode before G726DecodeInit.");

        int outSamples = buf.Length * 8 / codeSize;
        if (outSamples > samples.Length)
            throw new ArgumentException(
                $"G.726: {buf.Length} bytes decode to {outSamples} samples and the output "
              + $"holds {samples.Length}.", nameof(samples));

        if (buf.Length * 8 != outSamples * codeSize)
            throw new ArgumentException(
                $"G.726: a packet of {buf.Length} bytes is {buf.Length * 8} bits, which is not "
              + $"a whole number of {codeSize}-bit codes. ffmpeg logs 'Frame invalidly split, "
              + "missing parser?' here and drops the remainder.", nameof(buf));

        var gb = new G726BitReader(buf);
        for (int i = 0; i < outSamples; i++)
        {
            samples[i] = G726Decode(littleEndian
                ? gb.GetBitsLe(codeSize)
                : gb.GetBits(codeSize));
        }

        return outSamples;
    }

    /// <summary><c>i2f</c>: an integer into the 11-bit float.</summary>
    private static void I2f(int i, ref Float11 f)
    {
        f.Sign = (byte)(i < 0 ? 1 : 0);
        if (f.Sign != 0)
            i = -i;
        f.Exp = (byte)(AvLog216Bit(i) + (i != 0 ? 1 : 0));
        f.Mant = (byte)(i != 0 ? (i << 6) >> f.Exp : 1 << 5);
    }

    /// <summary>
    /// <c>mult</c>: the product of two 11-bit floats, by adding exponents and shifting the
    /// mantissa product into place.
    /// </summary>
    private static short Mult(in Float11 f1, in Float11 f2)
    {
        int exp = f1.Exp + f2.Exp;
        int res = ((f1.Mant * f2.Mant) + 0x30) >> 4;
        res = exp > 19 ? res << (exp - 19) : res >> (19 - exp);
        return (short)((f1.Sign ^ f2.Sign) != 0 ? -res : res);
    }

    /// <summary><c>sgn</c>: 1 for zero and above, -1 below. Note that sgn(0) is 1.</summary>
    private static int Sgn(int value) => value < 0 ? -1 : 1;

    /// <summary>
    /// <c>av_log2_16bit</c>. Identical to the C's table-driven version for every value that
    /// fits in 16 bits, which is the whole range g726.c passes it.
    /// </summary>
    private static int AvLog216Bit(int v) =>
        v == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    /// <summary><c>av_clip_intp2</c>.</summary>
    private static int AvClipIntp2(int value, int p) =>
        value < -(1 << p) ? -(1 << p) : (value > (1 << p) - 1 ? (1 << p) - 1 : value);

    /// <summary>
    /// Paragraph 4.2.3 page 22: inverse adaptive quantizer. <c>inverse_quant</c>.
    /// </summary>
    private short InverseQuant(int i)
    {
        int dql = tbls.Iquant[i] + (y >> 2);
        int dex = (dql >> 7) & 0xf;        /* 4-bit exponent */
        int dqt = (1 << 7) + (dql & 0x7f); /* log2 -> linear */
        return (short)(dql < 0 ? 0 : (dqt << dex) >> 7);
    }

    /// <summary><c>g726_decode</c>: one code in, one sample out.</summary>
    private short G726Decode(int i)
    {
        Float11 f = default;
        int iSig = i >> (codeSize - 1);

        int dqv = InverseQuant(i);

        /* Transition detect */
        int ylint = yl >> 15;
        int ylfrac = (yl >> 10) & 0x1f;
        int thr2 = ylint > 9 ? 0x1f << 10 : (0x20 + ylfrac) << ylint;
        bool tr = td == 1 && dqv > (3 * thr2) >> 2;

        if (iSig != 0)  /* get the sign */
            dqv = -dqv;
        int reSignal = (short)(se + dqv);

        /* Update second order predictor coefficient A2 and A1 */
        int pk0 = (sez + dqv) != 0 ? Sgn(sez + dqv) : 0;
        int dq0 = dqv != 0 ? Sgn(dqv) : 0;
        if (tr)
        {
            a[0] = 0;
            a[1] = 0;
            for (int k = 0; k < 6; k++)
                b[k] = 0;
        }
        else
        {
            /* This is a bit crazy, but it really is +255 not +256 */
            int fa1 = AvClipIntp2((-a[0] * pk[0] * pk0) >> 5, 8);

            a[1] += 128 * pk0 * pk[1] + fa1 - (a[1] >> 7);
            a[1] = Math.Clamp(a[1], -12288, 12288);
            a[0] += 64 * 3 * pk0 * pk[0] - (a[0] >> 8);
            a[0] = Math.Clamp(a[0], -(15360 - a[1]), 15360 - a[1]);

            for (int k = 0; k < 6; k++)
                b[k] += 128 * dq0 * Sgn(-dq[k].Sign) - (b[k] >> 8);
        }

        /* Update Dq and Sr and Pk */
        pk[1] = pk[0];
        pk[0] = pk0 != 0 ? pk0 : 1;
        sr[1] = sr[0];
        I2f(reSignal, ref sr[0]);
        for (int k = 5; k > 0; k--)
            dq[k] = dq[k - 1];
        I2f(dqv, ref dq[0]);
        dq[0].Sign = (byte)iSig; /* Isn't it crazy ?!?! */

        td = a[1] < -11776 ? 1 : 0;

        /* Update Ap */
        dms += (tbls.F[i] << 4) + ((-dms) >> 5);
        dml += (tbls.F[i] << 4) + ((-dml) >> 7);
        if (tr)
        {
            ap = 256;
        }
        else
        {
            ap += (-ap) >> 4;
            if (y <= 1535 || td != 0 || Math.Abs((dms << 2) - dml) >= dml >> 3)
                ap += 0x20;
        }

        /* Update Yu and Yl */
        yu = Math.Clamp(y + tbls.W[i] + ((-y) >> 5), 544, 5120);
        yl += yu + ((-yl) >> 6);

        /* Next iteration for Y */
        int al = ap >= 256 ? 1 << 6 : ap >> 2;
        y = (yl + (yu - (yl >> 6)) * al) >> 6;

        /* Next iteration for SE and SEZ */
        se = 0;
        for (int k = 0; k < 6; k++)
        {
            I2f(b[k] >> 2, ref f);
            se += Mult(in f, in dq[k]);
        }
        sez = se >> 1;
        for (int k = 0; k < 2; k++)
        {
            I2f(a[k] >> 2, ref f);
            se += Mult(in f, in sr[k]);
        }
        se >>= 1;

        return (short)Math.Clamp(reSignal * 4, -0xffff, 0xffff);
    }
}

/// <summary>
/// <c>get_bits.h</c> reduced to the two calls g726.c makes: <c>get_bits</c> for the
/// most-significant-bit-first packing and <c>get_bits_le</c> for the right-justified packing
/// AIFF and Sun AU use.
/// </summary>
/// <remarks>
/// A code is at most 5 bits and never spans more than two bytes, so this reads bit by bit
/// rather than keeping the C's 32-bit cache. Reading past the end is impossible:
/// <see cref="G726.G726DecodeFrame"/> rejects a packet whose bits are not a whole number of
/// codes before any read happens.
/// </remarks>
internal ref struct G726BitReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> buffer = buffer;
    private int bitsRead;

    /// <summary><c>get_bits(n)</c> - most significant bit of the code first.</summary>
    public int GetBits(int n)
    {
        int value = 0;
        for (int i = 0; i < n; i++)
        {
            int index = bitsRead + i;
            value = (value << 1) | ((buffer[index >> 3] >> (7 - (index & 7))) & 1);
        }
        bitsRead += n;
        return value;
    }

    /// <summary><c>get_bits_le(n)</c> - least significant bit of the code first.</summary>
    public int GetBitsLe(int n)
    {
        int value = 0;
        for (int i = 0; i < n; i++)
        {
            int index = bitsRead + i;
            value |= ((buffer[index >> 3] >> (index & 7)) & 1) << i;
        }
        bitsRead += n;
        return value;
    }
}

/// <summary>The registry entry for G.726 as a WAV, an ASF or a MOV carries it.</summary>
[Codec("adpcm_g726", CodecType.Audio, DisplayName = "G.726 ADPCM", Priority = 5)]
public sealed class G726DecoderAdapter : G726DecoderAdapterBase
{
    public override string CodecId => "adpcm_g726";

    /// <inheritdoc/>
    protected override bool PackedLittleEndian => false;
}

/// <summary>
/// The registry entry for the right-justified packing, which the C registers as a second
/// decoder with the same body and a different name.
/// </summary>
/// <remarks>
/// AIFF-C names it <c>G726</c> and Sun AU gives it four codec numbers of its own
/// (<c>aiff.c</c>, <c>au.c</c>). Those are the containers that carry it; a WAV never does.
/// </remarks>
[Codec("adpcm_g726le", CodecType.Audio, DisplayName = "G.726 ADPCM little-endian",
       Priority = 5)]
public sealed class G726LeDecoderAdapter : G726DecoderAdapterBase
{
    public override string CodecId => "adpcm_g726le";

    /// <inheritdoc/>
    protected override bool PackedLittleEndian => true;
}

/// <summary>Shared body for the two G.726 adapters.</summary>
public abstract class G726DecoderAdapterBase : AudioDecoderAdapterBase
{
    private readonly G726 decoder = new();
    private short[] scratch = new short[8192];
    private int sampleRate = 8000;
    private int channels;
    private int bitsPerCodedSample;
    private bool configured;

    /// <summary>Whether this variant packs its codes least-significant-bit first.</summary>
    protected abstract bool PackedLittleEndian { get; }

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override int Channels => channels;

    /// <summary>
    /// G.726 needs the code size, which is <c>bits_per_coded_sample</c> and nothing else:
    /// the bitstream carries no header of any kind, so a stream read at the wrong code size
    /// decodes to noise without ever failing. <c>ff_get_wav_header</c> derives it from the
    /// bit rate rather than trusting the format chunk's bits-per-sample field.
    /// </summary>
    public override void ConfigureAudio(
        int rate, int channelCount, int align = 0, int bits = 0)
    {
        if (rate > 0) sampleRate = rate;
        channels = channelCount;
        bitsPerCodedSample = bits;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// More than one channel is refused rather than decoded, which is what
    /// <c>g726_decode_init</c> does (<c>avpriv_request_sample</c> and
    /// AVERROR_PATCHWELCOME). The decoder's whole state is per-stream, so two channels
    /// interleaved into it would adapt to the sum of both.
    /// </remarks>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (channels != 1) return false;
        if (bitsPerCodedSample < 2 || bitsPerCodedSample > 5) return false;

        decoder.G726DecodeInit(bitsPerCodedSample, PackedLittleEndian);
        configured = true;
        return true;
    }

    /// <inheritdoc/>
    public override DecodedAudioFrame? Decode(
        ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!configured)
            throw new InvalidOperationException($"{CodecId}: Decode before Initialize.");
        if (data.Length == 0) return null;

        int samples = data.Length * 8 / bitsPerCodedSample;
        if (samples <= 0) return null;
        if (scratch.Length < samples) scratch = new short[samples];

        int got = decoder.G726DecodeFrame(data, scratch);
        if (got <= 0) return null;

        EnsureBuffer(got);
        for (int i = 0; i < got; i++) SampleBuffer[i] = scratch[i] * (1f / 32768f);

        return MakeFrame(got, presentationTimeTicks);
    }

    /// <summary>
    /// <c>g726_decode_flush</c>, which is a full <c>g726_reset</c>. The state is the whole
    /// stream's history, so a seek that kept it would predict from audio that was never
    /// played.
    /// </summary>
    public override void Flush() => decoder.G726Reset();
}

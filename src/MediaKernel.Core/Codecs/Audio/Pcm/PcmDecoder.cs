// Translated from ffmpeg-master/libavcodec/pcm.c and pcm_tablegen.h - the decoder half.
//
// WHAT IS HERE AND WHAT IS NOT. pcm.c holds an encoder and a decoder behind one set of macros.
// This is the decoder: pcm_decode_init, pcm_scale_decode_init, pcm_lut_decode_init,
// pcm_decode_frame and the DECODE/DECODE_PLANAR macros, plus alaw2linear, ulaw2linear and
// vidc2linear from pcm_tablegen.h. The encoder comes back when something in this tree writes a
// WAV; its entries stay todo in the port checklist rather than disappearing.
//
// TWO DIVERGENCES, BOTH RECORDED IN THE REGISTRY.
//
// 1. The C declares one FFCodec per sample format through PCM_DECODER and friends, so the
//    codec id selects the decoder. Here one class switches on PcmCodecId, because thirty-odd
//    near-identical classes would be thirty-odd places for a typo and the [Codec] attribute
//    does the registration either way.
//
// 2. The C's DECODE macro is instantiated by the preprocessor for each (size, endianness,
//    shift, offset), and writes the frame in the codec's own sample format - S16, S32, FLT and
//    so on. IAudioDecoder hands back interleaved float, which is what every other audio decoder
//    in this tree produces and what the pipeline mixes, so the conversion ffmpeg would do in
//    swresample happens here at the store. The scaling is swresample's: an integer format is
//    divided by 2^(bits-1) of its own width, u8 is biased by 128 first, and a float format
//    passes through. The gate compares against `ffmpeg -f f32le`, which is that same chain.
//
// What is NOT diverged, and matters: the C reads every format as an unsigned integer of the
// container width, subtracts an offset and then shifts left, so that a 24-bit sample becomes a
// 32-bit one with its low byte zero rather than being sign-extended. That is reproduced
// exactly, because the shift is what makes U24 and S24 differ by nothing but their offset.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Pcm;

/// <summary>
/// The <c>AV_CODEC_ID_PCM_*</c> values this decoder handles, with the C's own names.
/// </summary>
public enum PcmCodecId
{
    /// <summary>Not set; <see cref="PcmDecoder"/> throws rather than guessing.</summary>
    None = 0,

    S8, S8Planar,
    U8, Sga,
    S16Le, S16Be, S16LePlanar, S16BePlanar,
    U16Le, U16Be,
    S24Le, S24Be, S24LePlanar, S24Daud,
    U24Le, U24Be,
    S32Le, S32Be, S32LePlanar,
    U32Le, U32Be,
    S64Le, S64Be,
    F32Le, F32Be,
    F64Le, F64Be,

    /// <summary>G.711 A-law.</summary>
    Alaw,

    /// <summary>G.711 mu-law.</summary>
    Mulaw,

    /// <summary>Acorn VIDC, which is mu-law with its fields in other places.</summary>
    Vidc,

    /// <summary>Linear Acoustic LXF, 20 bits packed two samples to five bytes.</summary>
    Lxf,

    /// <summary>16-bit float, scaled by <c>bits_per_coded_sample</c>.</summary>
    F16Le,

    /// <summary>24-bit float, scaled by <c>bits_per_coded_sample</c>.</summary>
    F24Le,
}

/// <summary>
/// <c>alaw2linear</c>, <c>ulaw2linear</c> and <c>vidc2linear</c> of <c>pcm_tablegen.h</c>, and
/// the 256-entry tables <c>pcm_lut_decode_init</c> builds from them.
/// </summary>
/// <remarks>
/// The tables are built once into <c>static readonly</c> arrays rather than per decoder
/// instance. The C builds them into <c>priv_data</c> because a codec context owns its state;
/// here they are 512 bytes of constants that cannot differ between instances, and a per-frame
/// decoder should not be allocating them.
/// </remarks>
public static class PcmTables
{
    private const int SignBit = 0x80;       // Sign bit for an A-law byte
    private const int QuantMask = 0xF;      // Quantization field mask
    private const int SegShift = 4;         // Left shift for segment number
    private const int SegMask = 0x70;       // Segment field mask
    private const int Bias = 0x84;          // Bias for linear code

    private const int VidcSignBit = 1;
    private const int VidcQuantMask = 0x1E;
    private const int VidcQuantShift = 1;
    private const int VidcSegShift = 5;
    private const int VidcSegMask = 0xE0;

    /// <summary><c>alaw2linear</c> - an A-law byte as 16-bit linear PCM.</summary>
    public static int Alaw2Linear(byte aVal)
    {
        int a = aVal ^ 0x55;

        int t = a & QuantMask;
        int seg = (a & SegMask) >> SegShift;
        if (seg != 0) t = (t + t + 1 + 32) << (seg + 2);
        else t = (t + t + 1) << 3;

        return (a & SignBit) != 0 ? t : -t;
    }

    /// <summary><c>ulaw2linear</c>.</summary>
    public static int Ulaw2Linear(byte uVal)
    {
        // Complement to obtain normal u-law value.
        int u = (byte)~uVal;

        // Extract and bias the quantization bits, then shift up by the segment number and
        // subtract out the bias.
        int t = ((u & QuantMask) << 3) + Bias;
        t <<= (u & SegMask) >> SegShift;

        return (u & SignBit) != 0 ? Bias - t : t - Bias;
    }

    /// <summary><c>vidc2linear</c>.</summary>
    public static int Vidc2Linear(byte uVal)
    {
        int t = (((uVal & VidcQuantMask) >> VidcQuantShift) << 3) + Bias;
        t <<= (uVal & VidcSegMask) >> VidcSegShift;

        return (uVal & VidcSignBit) != 0 ? Bias - t : t - Bias;
    }

    /// <summary><c>PCMLUTDecode.table</c> for <c>AV_CODEC_ID_PCM_ALAW</c>.</summary>
    public static readonly short[] AlawTable = BuildTable(Alaw2Linear);

    /// <summary><c>PCMLUTDecode.table</c> for <c>AV_CODEC_ID_PCM_MULAW</c>.</summary>
    public static readonly short[] MulawTable = BuildTable(Ulaw2Linear);

    /// <summary><c>PCMLUTDecode.table</c> for <c>AV_CODEC_ID_PCM_VIDC</c>.</summary>
    public static readonly short[] VidcTable = BuildTable(Vidc2Linear);

    private static short[] BuildTable(Func<byte, int> convert)
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++) table[i] = (short)convert((byte)i);
        return table;
    }
}

/// <summary>
/// <c>pcm.c</c>'s decoder: linear PCM in every width and byte order FFmpeg knows, plus G.711
/// A-law and mu-law, Acorn VIDC, and LXF's packed 20-bit samples.
/// </summary>
public sealed class PcmDecoder
{
    private PcmCodecId codecId;
    private int sampleSize;         // PCMDecode.sample_size
    private float scale = 1f;       // PCMScaleDecode.scale, for F16LE and F24LE
    private float[] output = [];

    /// <summary>A name for diagnostics; the adapters carry the registry's codec ids.</summary>
    public string Name => $"PCM ({codecId})";

    /// <summary>Sample rate in Hz, from the container.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Channel count, from the container.</summary>
    public int Channels { get; private set; }

    /// <summary>Bits the coded format carries, which is not always the container width.</summary>
    public int BitsPerSample { get; private set; }

    /// <summary>Which <c>AV_CODEC_ID_PCM_*</c> this instance decodes.</summary>
    public PcmCodecId CodecId => codecId;

    /// <summary>
    /// <c>sample_size</c> - how many bytes one sample of the coded format occupies. It is the
    /// container width and not the useful width: S24LE is four, because the C reads it with
    /// <c>bytestream_get_le24</c> into a 32-bit store.
    /// </summary>
    public int SampleSize => sampleSize;

    /// <summary>
    /// <c>pcm_decode_init</c>, <c>pcm_lut_decode_init</c> and <c>pcm_scale_decode_init</c>,
    /// which the C selects between in the codec declaration rather than at run time.
    /// </summary>
    /// <param name="id">The PCM variant.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count; the C rejects zero.</param>
    /// <param name="bitsPerCodedSample">
    /// Only <see cref="PcmCodecId.F16Le"/> and <see cref="PcmCodecId.F24Le"/> read it, to build
    /// <c>scale</c>. The C rejects anything outside 1 to 24.
    /// </param>
    public void Initialize(PcmCodecId id, int sampleRate, int channels, int bitsPerCodedSample = 0)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(channels), channels, "PCM: invalid number of channels.");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "PCM: invalid sample rate.");

        codecId = id;
        SampleRate = sampleRate;
        Channels = channels;

        // pcm_decode_init's codec_id_to_samplefmt table, as a switch. sample_size is the
        // container width in bytes; bits is what the format actually carries.
        (sampleSize, BitsPerSample) = id switch
        {
            PcmCodecId.S8 or PcmCodecId.S8Planar or PcmCodecId.U8 or PcmCodecId.Sga => (1, 8),
            PcmCodecId.S16Le or PcmCodecId.S16Be or PcmCodecId.S16LePlanar
                or PcmCodecId.S16BePlanar or PcmCodecId.U16Le or PcmCodecId.U16Be => (2, 16),
            PcmCodecId.S24Le or PcmCodecId.S24Be or PcmCodecId.S24LePlanar
                or PcmCodecId.S24Daud or PcmCodecId.U24Le or PcmCodecId.U24Be => (3, 24),
            PcmCodecId.S32Le or PcmCodecId.S32Be or PcmCodecId.S32LePlanar
                or PcmCodecId.U32Le or PcmCodecId.U32Be or PcmCodecId.F32Le
                or PcmCodecId.F32Be => (4, 32),
            PcmCodecId.S64Le or PcmCodecId.S64Be or PcmCodecId.F64Le
                or PcmCodecId.F64Be => (8, 64),
            PcmCodecId.Alaw or PcmCodecId.Mulaw or PcmCodecId.Vidc => (1, 8),
            PcmCodecId.Lxf => (5, 20),
            PcmCodecId.F16Le or PcmCodecId.F24Le => (4, 32),
            _ => throw new NotSupportedException(
                $"PCM: no sample size for {id}. Every AV_CODEC_ID_PCM_* pcm_decode_init knows "
              + "has an entry; a missing one is a gap in this switch, not an unsupported file."),
        };

        if (id is PcmCodecId.F16Le or PcmCodecId.F24Le)
        {
            // pcm_scale_decode_init.
            if (bitsPerCodedSample < 1 || bitsPerCodedSample > 24)
                throw new ArgumentOutOfRangeException(
                    nameof(bitsPerCodedSample), bitsPerCodedSample,
                    "PCM: F16LE and F24LE need bits_per_coded_sample between 1 and 24.");
            scale = 1f / (1 << (bitsPerCodedSample - 1));
        }
    }


    /// <summary>
    /// <c>pcm_decode_frame</c>. Returns the number of interleaved float samples written to
    /// <paramref name="buffer"/>, which is the decoder's own and is reused.
    /// </summary>
    public int DecodeToFloat(ReadOnlySpan<byte> data, out float[] buffer)
    {
        int bufSize = data.Length;

        // samples_per_block: two per 40-bit block for LXF, one otherwise.
        int samplesPerBlock = codecId == PcmCodecId.Lxf ? 2 : 1;

        int n = Channels * sampleSize;
        if (n != 0 && bufSize % n != 0)
        {
            if (bufSize < n)
                throw new InvalidOperationException(
                    $"PCM: invalid packet, data has size {bufSize} but at least {n} was "
                  + "expected.");
            bufSize -= bufSize % n;
        }

        n = bufSize / sampleSize;
        int total = n * samplesPerBlock;
        if (output.Length < total) output = new float[total];
        buffer = output;
        Span<float> dst = output.AsSpan(0, total);

        switch (codecId)
        {
            // The C reads each of these as an unsigned integer of the container width,
            // subtracts the offset and shifts left, so the useful bits sit at the top.
            case PcmCodecId.U32Le: DecodeU32(data, dst, n, littleEndian: true, 0x80000000u); break;
            case PcmCodecId.U32Be: DecodeU32(data, dst, n, littleEndian: false, 0x80000000u); break;
            case PcmCodecId.S32Le: DecodeU32(data, dst, n, littleEndian: true, 0u); break;
            case PcmCodecId.S32Be: DecodeU32(data, dst, n, littleEndian: false, 0u); break;

            case PcmCodecId.S24Le: Decode24(data, dst, n, littleEndian: true, 0u); break;
            case PcmCodecId.S24Be: Decode24(data, dst, n, littleEndian: false, 0u); break;
            case PcmCodecId.U24Le: Decode24(data, dst, n, littleEndian: true, 0x800000u); break;
            case PcmCodecId.U24Be: Decode24(data, dst, n, littleEndian: false, 0x800000u); break;

            case PcmCodecId.S16Le: DecodeU16(data, dst, n, littleEndian: true, 0); break;
            case PcmCodecId.S16Be: DecodeU16(data, dst, n, littleEndian: false, 0); break;
            case PcmCodecId.U16Le: DecodeU16(data, dst, n, littleEndian: true, 0x8000); break;
            case PcmCodecId.U16Be: DecodeU16(data, dst, n, littleEndian: false, 0x8000); break;

            case PcmCodecId.S8: Decode8(data, dst, n, offset: 0x80); break;
            case PcmCodecId.U8: Decode8(data, dst, n, offset: 0); break;
            case PcmCodecId.Sga: Decode8(data, dst, n, offset: 0); break;

            case PcmCodecId.S8Planar: DecodePlanar8(data, dst, n, offset: 0x80); break;
            case PcmCodecId.S16LePlanar: DecodePlanar16(data, dst, n, littleEndian: true); break;
            case PcmCodecId.S16BePlanar: DecodePlanar16(data, dst, n, littleEndian: false); break;
            case PcmCodecId.S24LePlanar: DecodePlanar24(data, dst, n); break;
            case PcmCodecId.S32LePlanar: DecodePlanar32(data, dst, n); break;

            case PcmCodecId.S64Le: DecodeS64(data, dst, n, littleEndian: true); break;
            case PcmCodecId.S64Be: DecodeS64(data, dst, n, littleEndian: false); break;

            case PcmCodecId.F32Le: DecodeF32(data, dst, n, littleEndian: true); break;
            case PcmCodecId.F32Be: DecodeF32(data, dst, n, littleEndian: false); break;
            case PcmCodecId.F64Le: DecodeF64(data, dst, n, littleEndian: true); break;
            case PcmCodecId.F64Be: DecodeF64(data, dst, n, littleEndian: false); break;

            // F16LE and F24LE are F32LE read then multiplied by scale, which is what
            // pcm_decode_frame does at the end through vector_fmul_scalar.
            case PcmCodecId.F16Le:
            case PcmCodecId.F24Le:
                DecodeF32(data, dst, n, littleEndian: true);
                for (int i = 0; i < total; i++) dst[i] *= scale;
                break;

            case PcmCodecId.S24Daud: DecodeS24Daud(data, dst, n); break;

            case PcmCodecId.Alaw: DecodeLut(data, dst, n, PcmTables.AlawTable); break;
            case PcmCodecId.Mulaw: DecodeLut(data, dst, n, PcmTables.MulawTable); break;
            case PcmCodecId.Vidc: DecodeLut(data, dst, n, PcmTables.VidcTable); break;

            case PcmCodecId.Lxf: DecodeLxf(data, dst, n); break;

            default:
                throw new NotSupportedException(
                    $"PCM: {codecId} has no case in pcm_decode_frame.");
        }

        return total;
    }

    // ---- the DECODE macro, one instantiation per width -------------------------------------
    //
    // The C's `AV_WN32A(dst, (uint32_t)(v - offset) << shift)` stores an unsigned value whose
    // bit pattern is read back as signed. Each of these does the same arithmetic and then
    // divides by the full-scale value of that width, which is swresample's conversion.

    private const float S16Scale = 1f / 32768f;
    private const float S32Scale = 1f / 2147483648f;

    private static void DecodeU32(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian, uint offset)
    {
        for (int i = 0, o = 0; i < n; i++, o += 4)
        {
            uint v = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(src[o..])
                : BinaryPrimitives.ReadUInt32BigEndian(src[o..]);
            dst[i] = (int)(v - offset) * S32Scale;
        }
    }

    private static void Decode24(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian, uint offset)
    {
        for (int i = 0, o = 0; i < n; i++, o += 3)
        {
            uint v = littleEndian
                ? (uint)(src[o] | (src[o + 1] << 8) | (src[o + 2] << 16))
                : (uint)((src[o] << 16) | (src[o + 1] << 8) | src[o + 2]);
            // shift 8: a 24-bit sample becomes a 32-bit one with a zero low byte.
            dst[i] = (int)((v - offset) << 8) * S32Scale;
        }
    }

    private static void DecodeU16(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian, ushort offset)
    {
        for (int i = 0, o = 0; i < n; i++, o += 2)
        {
            ushort v = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(src[o..])
                : BinaryPrimitives.ReadUInt16BigEndian(src[o..]);
            dst[i] = (short)(ushort)(v - offset) * S16Scale;
        }
    }

    private static void Decode8(ReadOnlySpan<byte> src, Span<float> dst, int n, int offset)
    {
        // The C decodes S8 and U8 into AV_SAMPLE_FMT_U8, so both end up as an unsigned byte
        // around 128 and the conversion to float subtracts the bias again.
        for (int i = 0; i < n; i++)
            dst[i] = ((byte)(src[i] - offset) - 128) * (1f / 128f);
    }

    private static void DecodeLut(
        ReadOnlySpan<byte> src, Span<float> dst, int n, short[] lut)
    {
        for (int i = 0; i < n; i++) dst[i] = lut[src[i]] * S16Scale;
    }

    private static void DecodeS64(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian)
    {
        for (int i = 0, o = 0; i < n; i++, o += 8)
        {
            long v = littleEndian
                ? BinaryPrimitives.ReadInt64LittleEndian(src[o..])
                : BinaryPrimitives.ReadInt64BigEndian(src[o..]);
            dst[i] = (float)(v / 9223372036854775808.0);
        }
    }

    private static void DecodeF32(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian)
    {
        for (int i = 0, o = 0; i < n; i++, o += 4)
            dst[i] = littleEndian
                ? BinaryPrimitives.ReadSingleLittleEndian(src[o..])
                : BinaryPrimitives.ReadSingleBigEndian(src[o..]);
    }

    private static void DecodeF64(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian)
    {
        for (int i = 0, o = 0; i < n; i++, o += 8)
            dst[i] = (float)(littleEndian
                ? BinaryPrimitives.ReadDoubleLittleEndian(src[o..])
                : BinaryPrimitives.ReadDoubleBigEndian(src[o..]));
    }

    /// <summary>
    /// <c>AV_CODEC_ID_PCM_S24DAUD</c>: three bytes in, one 16-bit sample out, with the D-Cinema
    /// bit order reversed. The C does this with <c>ff_reverse</c>.
    /// </summary>
    private static void DecodeS24Daud(ReadOnlySpan<byte> src, Span<float> dst, int n)
    {
        for (int i = 0, o = 0; i < n; i++, o += 3)
        {
            uint v = (uint)((src[o] << 16) | (src[o + 1] << 8) | src[o + 2]);
            uint tmp = ((v >> 4) & 0xFF00) | Reverse((byte)(v >> 16)) | ((v & 0xFF) << 8);
            dst[i] = (short)tmp * S16Scale;
        }
    }

    /// <summary><c>ff_reverse</c> for one byte, as <c>ff_reverse[x]</c> would give it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Reverse(byte x)
    {
        uint v = x;
        v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
        v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
        v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
        return v;
    }

    // ---- DECODE_PLANAR -----------------------------------------------------------------
    //
    // The C writes one channel per plane. IAudioDecoder hands back interleaved samples, so
    // these read plane by plane and write with a channel stride, which is the same loop with
    // the destination index transposed.

    private void DecodePlanar8(ReadOnlySpan<byte> src, Span<float> dst, int n, int offset)
    {
        int per = n / Channels;
        for (int c = 0, o = 0; c < Channels; c++)
            for (int i = 0; i < per; i++, o++)
                dst[i * Channels + c] = ((byte)(src[o] - offset) - 128) * (1f / 128f);
    }

    private void DecodePlanar16(
        ReadOnlySpan<byte> src, Span<float> dst, int n, bool littleEndian)
    {
        int per = n / Channels;
        for (int c = 0, o = 0; c < Channels; c++)
            for (int i = 0; i < per; i++, o += 2)
            {
                short v = littleEndian
                    ? BinaryPrimitives.ReadInt16LittleEndian(src[o..])
                    : BinaryPrimitives.ReadInt16BigEndian(src[o..]);
                dst[i * Channels + c] = v * S16Scale;
            }
    }

    private void DecodePlanar24(ReadOnlySpan<byte> src, Span<float> dst, int n)
    {
        int per = n / Channels;
        for (int c = 0, o = 0; c < Channels; c++)
            for (int i = 0; i < per; i++, o += 3)
            {
                uint v = (uint)(src[o] | (src[o + 1] << 8) | (src[o + 2] << 16));
                dst[i * Channels + c] = (int)(v << 8) * S32Scale;
            }
    }

    private void DecodePlanar32(ReadOnlySpan<byte> src, Span<float> dst, int n)
    {
        int per = n / Channels;
        for (int c = 0, o = 0; c < Channels; c++)
            for (int i = 0; i < per; i++, o += 4)
                dst[i * Channels + c] =
                    BinaryPrimitives.ReadInt32LittleEndian(src[o..]) * S32Scale;
    }

    /// <summary>
    /// <c>AV_CODEC_ID_PCM_LXF</c>: two 20-bit samples packed into five bytes, each expanded to
    /// 32 bits by repeating its own bits into the low end.
    /// </summary>
    private void DecodeLxf(ReadOnlySpan<byte> src, Span<float> dst, int n)
    {
        int per = n / Channels;
        int o = 0;
        for (int c = 0; c < Channels; c++)
            for (int i = 0; i < per; i++, o += 5)
            {
                int low = (int)(((uint)src[o + 2] << 28)
                              | ((uint)src[o + 1] << 20)
                              | ((uint)src[o] << 12)
                              | ((uint)(src[o + 2] & 0x0F) << 8)
                              | src[o + 1]);
                int high = (int)(((uint)src[o + 4] << 24)
                               | ((uint)src[o + 3] << 16)
                               | ((uint)(src[o + 2] & 0xF0) << 8)
                               | ((uint)src[o + 4] << 4)
                               | ((uint)src[o + 3] >> 4));
                dst[(i * 2) * Channels + c] = low * S32Scale;
                dst[(i * 2 + 1) * Channels + c] = high * S32Scale;
            }
    }
}

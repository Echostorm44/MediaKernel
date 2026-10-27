// Translated from ffmpeg-master/libavcodec/adpcm.c, adpcm.h and adpcm_data.c - the two variants
// a WAV or an AVI carries.
//
// SCOPE, STATED RATHER THAN IMPLIED. adpcm.c is 3,019 lines covering some forty variants, most of
// them for one game console or one studio tool. The ones here are the ones a file somebody has
// actually got will hold, and the rule for adding one is that it must be gateable: ffmpeg has to
// be able to write a sample, or fate-suite has to ship one, or it stays `todo`.
//
//   ADPCM_MS           Microsoft's, WAV format tag 2
//   ADPCM_IMA_WAV      IMA's in a RIFF wrapper, tag 0x11
//   ADPCM_EA_MAXIS_XA  Electronic Arts' as Maxis wrote it into a .XA
//   ADPCM_YAMAHA       tag 0x20, and what an AICA or a Dreamcast sound bank holds
//   ADPCM_SWF          tag 'SF', Flash's own, with its variable nibble width
//   ADPCM_IMA_QT       QuickTime's 34-byte chunks, which is what a .caf or a .mov carries
//
// Every other variant stays `todo` in the port checklist with its own entry, so the
// checklist says what is missing instead of this comment being the only record. The ones left
// that ffmpeg cannot encode - adpcm_ct, adpcm_ima_dk3, adpcm_ima_dk4 and most of the console
// formats - cannot be gated from anything in this tree, and are not written until they can be:
// an ungated decoder is the thing this file's own history is a warning about.
//
// There was a 474-line reverse-engineered AdpcmDecoder.cs in this tree, in the same position the
// old ALAC decoder was: written from format notes, registered nowhere, gated by nothing. It is
// gone.
//
// ADPCM decode is integer arithmetic end to end - a step table, a predictor and a clamp - so the
// gate is exact equality with ffmpeg and not a tolerance. The two places that have to be
// translated and not reasoned about are av_clip_int16 on the predictor, which is what keeps the
// decoder in step with the encoder when a signal clips, and MS-ADPCM's division by 64 of a
// product that can be negative: C's `/ 64` truncates toward zero where an arithmetic shift would
// round toward negative infinity, and the two differ on every negative odd product.

using System;
using System.IO;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Adpcm;

/// <summary>The <c>AV_CODEC_ID_ADPCM_*</c> variants this decoder handles.</summary>
public enum AdpcmCodecId
{
    /// <summary>Not set; the decoder throws rather than guessing.</summary>
    None = 0,

    /// <summary><c>AV_CODEC_ID_ADPCM_MS</c> - Microsoft ADPCM, WAV format tag 2.</summary>
    Ms,

    /// <summary><c>AV_CODEC_ID_ADPCM_IMA_WAV</c> - IMA ADPCM in RIFF, WAV format tag 0x11.</summary>
    ImaWav,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_YAMAHA</c> - WAV format tag 0x20. A step and a predictor and
    /// nothing else: no block header at all, so the state runs the length of the stream.
    /// </summary>
    Yamaha,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_SWF</c> - Flash's, WAV format tag ('S' &lt;&lt; 8) + 'F'. The only
    /// variant here whose nibble width is in the stream rather than in the container, and
    /// the only one read with a bit reader rather than a byte pointer.
    /// </summary>
    Swf,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_IMA_AMV</c> - the audio of an AMV file. Mono only, with an
    /// eight-byte header per packet whose last four bytes are the sample count.
    /// </summary>
    ImaAmv,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_IMA_SMJPEG</c> - the audio of a Loki SDL Motion JPEG. QuickTime's
    /// nibble expansion with a four-byte-per-channel header of its own, and no block
    /// structure beyond the packet the container hands over.
    /// </summary>
    ImaSmjpeg,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_THP</c> - the audio of a GameCube THP. Not an IMA variant at
    /// all: a second-order predictor with sixteen coefficients PER CHANNEL carried in every
    /// packet, and fourteen samples to a block of eight bytes.
    /// </summary>
    Thp,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_IMA_QT</c> - QuickTime's IMA, 34 bytes and 64 samples per
    /// channel per chunk, channel data interleaved per chunk. A .caf or a .mov carries it.
    /// </summary>
    ImaQt,

    /// <summary>
    /// <c>AV_CODEC_ID_ADPCM_EA_MAXIS_XA</c> - Electronic Arts' ADPCM as Maxis wrote it into a
    /// .XA file. A block is one byte of coefficients and shift per channel followed by nibble
    /// pairs, and unlike the two above it has no per-block predictor state reset.
    /// </summary>
    EaMaxisXa,
}

/// <summary><c>ADPCMChannelStatus</c> of <c>adpcm.h</c>, for the fields these variants use.</summary>
public struct AdpcmChannelStatus
{
    /// <summary><c>predictor</c> - IMA's running sample value.</summary>
    public int Predictor;

    /// <summary><c>step_index</c> - IMA's index into the step table, 0 to 88.</summary>
    public int StepIndex;

    /// <summary><c>sample1</c> - MS's previous sample.</summary>
    public int Sample1;

    /// <summary><c>sample2</c> - MS's sample before that.</summary>
    public int Sample2;

    /// <summary><c>coeff1</c> - MS's first predictor coefficient, already divided by four.</summary>
    public int Coeff1;

    /// <summary><c>coeff2</c> - MS's second predictor coefficient, already divided by four.</summary>
    public int Coeff2;

    /// <summary><c>idelta</c> - MS's adaptive step.</summary>
    public int Idelta;

    /// <summary>
    /// <c>step</c> - Yamaha's adaptive step, which is a step size and not an index into a
    /// table. Zero means "not started": the C tests it and seeds 127 on the first nibble,
    /// which is how a stream with no block header initialises itself.
    /// </summary>
    public int Step;
}

/// <summary><c>adpcm_data.c</c>'s tables.</summary>
public static class AdpcmTables
{
    /// <summary>
    /// <c>ea_adpcm_table[20]</c> of adpcm.c - EA's predictor coefficients, the first eight
    /// used as two rows of four by the Maxis XA arm and the rest by the EA R variants.
    /// </summary>
    public static ReadOnlySpan<short> EaAdpcmTable =>
    [
        0,  240,  460,  392,
        0,    0, -208, -220,
        0,    1,    3,    4,
        7,    8,   10,   11,
        0,   -1,   -3,   -4,
    ];

    /// <summary><c>ff_adpcm_index_table[16]</c>.</summary>
    public static ReadOnlySpan<sbyte> IndexTable =>
    [
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    ];

    /// <summary><c>ff_adpcm_step_table[89]</c>, from the ADPCM reference source.</summary>
    public static ReadOnlySpan<short> StepTable =>
    [
            7,     8,     9,    10,    11,    12,    13,    14,    16,    17,
           19,    21,    23,    25,    28,    31,    34,    37,    41,    45,
           50,    55,    60,    66,    73,    80,    88,    97,   107,   118,
          130,   143,   157,   173,   190,   209,   230,   253,   279,   307,
          337,   371,   408,   449,   494,   544,   598,   658,   724,   796,
          876,   963,  1060,  1166,  1282,  1411,  1552,  1707,  1878,  2066,
         2272,  2499,  2749,  3024,  3327,  3660,  4026,  4428,  4871,  5358,
         5894,  6484,  7132,  7845,  8630,  9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    /// <summary><c>ff_adpcm_AdaptationTable[16]</c>, from libsndfile.</summary>
    public static ReadOnlySpan<short> AdaptationTable =>
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    ];

    /// <summary><c>ff_adpcm_AdaptCoeff1[7]</c> - divided by four to fit in a byte.</summary>
    public static ReadOnlySpan<byte> AdaptCoeff1 => [64, 128, 0, 48, 60, 115, 98];

    /// <summary><c>ff_adpcm_AdaptCoeff2[7]</c> - divided by four to fit in a byte.</summary>
    public static ReadOnlySpan<sbyte> AdaptCoeff2 => [0, -64, 0, 16, 0, -52, -58];

    /// <summary><c>ff_adpcm_ima_block_sizes[4]</c>, indexed by bits per coded sample minus 2.</summary>
    public static ReadOnlySpan<byte> ImaBlockSizes => [4, 12, 4, 20];

    /// <summary><c>ff_adpcm_ima_block_samples[4]</c>.</summary>
    public static ReadOnlySpan<byte> ImaBlockSamples => [16, 32, 8, 32];

    /// <summary><c>ff_adpcm_yamaha_indexscale[16]</c>.</summary>
    public static ReadOnlySpan<short> YamahaIndexScale =>
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        230, 230, 230, 230, 307, 409, 512, 614,
    ];

    /// <summary><c>ff_adpcm_yamaha_difflookup[16]</c>.</summary>
    public static ReadOnlySpan<sbyte> YamahaDiffLookup =>
    [
         1,  3,  5,  7,  9,  11,  13,  15,
        -1, -3, -5, -7, -9, -11, -13, -15,
    ];

    /// <summary>
    /// <c>swf_index_tables[4][16]</c>, indexed by nibble width minus two. "padded to zero
    /// where table size is less then 16" - the C's own comment, and the padding is reached:
    /// the index is <c>delta &amp; ~signmask</c>, which for a two-bit stream is 0 or 1 but
    /// for a five-bit one runs to 15.
    /// </summary>
    public static ReadOnlySpan<sbyte> SwfIndexTables =>
    [
        /*2*/ -1,  2,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*3*/ -1, -1,  2,  4,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*4*/ -1, -1, -1, -1,  2,  4,  6,  8,  0,  0,  0,  0,  0,  0,  0,  0,
        /*5*/ -1, -1, -1, -1, -1, -1, -1, -1,  1,  2,  4,  6,  8, 10, 13, 16,
    ];
}

/// <summary><c>adpcm.c</c>'s MS and IMA-in-WAV decoders.</summary>
public sealed class Adpcm
{
    /// <summary>
    /// Which nibble expansion the four-bit <c>ADPCM_IMA_WAV</c> arm uses. False is
    /// <c>adpcm_ima_expand_nibble(cs, nibble, 3)</c>, true is
    /// <c>ff_adpcm_ima_qt_expand_nibble(cs, nibble)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two forms are not equivalent: for <c>step = 7</c> and <c>nibble = 7</c>, the
    /// multiply form gives <c>((2*7+1)*7) &gt;&gt; 3 = 13</c> and the accumulate form gives
    /// <c>0 + 7 + 3 + 1 = 11</c>, because each of its three shifts truncates separately. A
    /// stream decoded with the wrong one is recognisable audio that is not ffmpeg's, wrong from
    /// the second sample of the first block by 2 units in 32768.
    /// </para>
    /// <para>
    /// <b>This constant existed because the pinned C source and the pinned reference binary
    /// disagreed.</b> <c>ffmpeg-master</c> (N-123116-g0465a9bb8f, 2026-02-21) calls
    /// <c>ff_adpcm_ima_qt_expand_nibble</c> from the four-bit <c>ADPCM_IMA_WAV</c> arm; the
    /// reference binary was 2025-05-29-git-75960ac270, nine months older, and behaved as
    /// <c>adpcm_ima_expand_nibble</c> with a shift of 3. The port followed the binary, because
    /// the gate is this project's verdict. The binary is now 2026-02-23-git-7b15039cdb, two
    /// days after the pinned source, so the two agree and this is <c>true</c> - the flip was
    /// one line and a gate run, which is the whole point of keeping both forms translated.
    /// Same arrangement as <c>HaveSseXrpow</c> in the LAME port.
    /// </para>
    /// </remarks>
    public const bool ImaWavUsesQtNibble = true;

    private AdpcmChannelStatus[] status = new AdpcmChannelStatus[2];

    /// <summary>
    /// The first byte of the block <see cref="DecodeBlock"/> is working on. The C's
    /// get_nb_samples reads from the same GetByteContext the decoder will use, so for
    /// ADPCM_SWF - whose nibble width is the first two bits of the block - it peeks. This
    /// is that peek, set before the count is asked for.
    /// </summary>
    private byte swfFirstByte;

    /// <summary>Which variant this instance decodes.</summary>
    public AdpcmCodecId CodecId { get; private set; }

    /// <summary>Sample rate in Hz, from the container.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Channel count, from the container.</summary>
    public int Channels { get; private set; }

    /// <summary><c>avctx->block_align</c> - the bytes one coded block occupies.</summary>
    public int BlockAlign { get; private set; }

    /// <summary><c>avctx->bits_per_coded_sample</c> - 4 for everything common.</summary>
    public int BitsPerCodedSample { get; private set; }

    /// <summary>Sets up for one stream. All four values come from the container.</summary>
    public void Initialize(
        AdpcmCodecId id, int sampleRate, int channels, int blockAlign, int bitsPerCodedSample)
    {
        // "av_assert0(channels == 1)" - the C asserts it rather than handling it.
        if (id == AdpcmCodecId.ImaAmv && channels != 1)
            throw new NotSupportedException(
                $"ADPCM IMA AMV: {channels} channels. The format is mono, and the C asserts "
              + "it rather than handling anything else.");

        if (channels is < 1 or > 2)
            throw new NotSupportedException(
                $"ADPCM: {channels} channels. The C handles more for MS, but no WAV or AVI in "
              + "this tree carries it and an untested path is worse than a refusal.");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "ADPCM: rate.");
        // SMJPEG, AMV and THP are the variants here with no block at all: a packet is a
        // header and then nibbles to the end, whatever length the container made it, so
        // block_align is absent and GetNbSamples must not clamp to it.
        if (blockAlign <= 0
            && id is not (AdpcmCodecId.ImaSmjpeg or AdpcmCodecId.ImaAmv or AdpcmCodecId.Thp))
            throw new ArgumentOutOfRangeException(
                nameof(blockAlign), blockAlign,
                "ADPCM: block_align is not set, and these variants are block-based - the C "
              + "refuses the stream for the same reason.");

        if (id == AdpcmCodecId.ImaWav && bitsPerCodedSample is < 2 or > 5)
            throw new NotSupportedException(
                $"ADPCM IMA WAV: {bitsPerCodedSample} bits per coded sample. The block size "
              + "tables only have entries for 2 to 5.");
        if (id == AdpcmCodecId.Ms && bitsPerCodedSample != 4)
            throw new NotSupportedException(
                $"ADPCM MS: {bitsPerCodedSample} bits per coded sample; MS ADPCM is 4.");
        if (id == AdpcmCodecId.ImaQt && bitsPerCodedSample != 4)
            throw new NotSupportedException(
                $"ADPCM IMA QT: {bitsPerCodedSample} bits per coded sample; it is 4.");
        if (id == AdpcmCodecId.None)
            throw new NotSupportedException("ADPCM: no variant selected.");

        CodecId = id;
        SampleRate = sampleRate;
        Channels = channels;
        BlockAlign = blockAlign;
        BitsPerCodedSample = bitsPerCodedSample;
        status = new AdpcmChannelStatus[2];
    }

    /// <summary>
    /// The first eight bytes of the current AMV packet, because its sample count lives
    /// there and <see cref="GetNbSamples"/> is given only a length.
    /// </summary>
    private readonly byte[] amvHeader = new byte[8];

    /// <summary>
    /// <c>get_nb_samples</c> for these two variants - how many samples per channel a block
    /// holds.
    /// </summary>
    public int GetNbSamples(int bufSize)
    {
        int ch = Channels;

        // THE BLOCK-ALIGN CLAMP IS PER VARIANT IN THE C, inside the arms that want it, and
        // this had it hoisted to the top where it applied to all of them. Of the variants
        // here only IMA WAV and MS clamp; Yamaha, SWF, IMA QT, Maxis XA, SMJPEG and AMV do
        // not. It looked harmless because for most of them the container's block align
        // either equals the packet size or is zero - and then an AMV turned up, whose
        // WAVEFORMAT says 2 because that is its OUTPUT sample size. Two bytes clamped every
        // packet to less than its own eight-byte header, so get_nb_samples answered 0 and
        // AMV audio produced no frames at all through the pipeline, on a file whose decoder
        // is gated frame by frame elsewhere.
        switch (CodecId)
        {
            case AdpcmCodecId.ImaWav:
            {
                int bsize = AdpcmTables.ImaBlockSizes[BitsPerCodedSample - 2];
                int bsamples = AdpcmTables.ImaBlockSamples[BitsPerCodedSample - 2];
                if (BlockAlign > 0) bufSize = Math.Min(bufSize, BlockAlign);
                if (bufSize < 4 * ch) return 0;
                return 1 + (bufSize - 4 * ch) / (bsize * ch) * bsamples;
            }
            case AdpcmCodecId.Ms:
                if (BlockAlign > 0) bufSize = Math.Min(bufSize, BlockAlign);
                if (bufSize < 7 * ch) return 0;
                return (bufSize - 6 * ch) * 2 / ch;
            case AdpcmCodecId.EaMaxisXa:
                return (bufSize - ch) / ch * 2;

            // "constant, only check buf_size" - a chunk is 34 bytes and 64 samples per
            // channel, whatever the rate.
            case AdpcmCodecId.ImaQt:
                if (bufSize < 34 * ch) return 0;
                return 64;

            // AMV states its sample count in the header rather than implying it from the
            // packet length, and takes the SMALLER of the two: a packet may be padded, and
            // a truncated one must not be read past its end.
            case AdpcmCodecId.ImaAmv:
            {
                if (bufSize < 8) return 0;
                uint coded = (uint)(amvHeader[4] | (amvHeader[5] << 8)
                                  | (amvHeader[6] << 16) | (amvHeader[7] << 24));
                return (int)Math.Min((uint)Math.Max((bufSize - 8) * 2, 0), coded);
            }

            // THP's packet is eight bytes of header and then 36 per channel - a sixteen
            // entry coefficient table and the two previous samples - before any nibbles.
            // Fourteen samples come out of every eight bytes, and the C's approx flag says
            // the remainder is an estimate rather than a count.
            case AdpcmCodecId.Thp:
            {
                int payload = bufSize - (8 + 36 * ch);
                if (payload <= 0) return 0;
                payload /= ch;
                int samples = payload / 8 * 14;
                if (payload % 8 > 1)
                    samples += (payload % 8 - 1) * 2;
                return samples;
            }

            // "simple 4-bit adpcm, with header" - four header bytes a channel, then two
            // samples a byte. The C does not guard the subtraction; the caller's
            // nbSamples <= 0 test is what catches a packet shorter than its own header.
            case AdpcmCodecId.ImaSmjpeg:
                return (bufSize - 4 * ch) * 2 / ch;

            // "simple 4-bit adpcm" - two samples a byte and no header anywhere.
            case AdpcmCodecId.Yamaha:
                return bufSize * 2 / ch;

            case AdpcmCodecId.Swf:
            {
                // The nibble width is the first two bits of the block, so the sample count
                // cannot be worked out without reading them.
                if (bufSize < 1) return 0;
                int bufBits = bufSize * 8 - 2;
                int nbits = (swfFirstByte >> 6) + 2;
                int blockHeaderSize = 22 * ch;
                int blockSize = blockHeaderSize + nbits * ch * 4095;
                int nblocks = bufBits / blockSize;
                int bitsLeft = bufBits - nblocks * blockSize;
                int samples = nblocks * 4096;
                if (bitsLeft >= blockHeaderSize)
                    samples += 1 + (bitsLeft - blockHeaderSize) / (nbits * ch);
                return samples;
            }
            default:
                throw new NotSupportedException($"ADPCM: {CodecId} has no sample count.");
        }
    }

    /// <summary><c>av_clip_int16</c>.</summary>
    private static int ClipInt16(int v) => v < short.MinValue ? short.MinValue
                                         : v > short.MaxValue ? short.MaxValue : v;

    /// <summary><c>av_clip(v, 0, 88)</c> for the step index.</summary>
    private static int ClipStepIndex(int v) => v < 0 ? 0 : v > 88 ? 88 : v;

    /// <summary>
    /// <c>ff_adpcm_ima_qt_expand_nibble</c> - accumulates <c>step/8 + step + step/2 +
    /// step/4</c> by tested bits, each shift truncating separately.
    /// </summary>
    /// <remarks>See <see cref="ImaWavUsesQtNibble"/> for which arm the port takes and why.</remarks>
    private static short ImaQtExpandNibble(ref AdpcmChannelStatus c, int nibble)
    {
        int step = AdpcmTables.StepTable[c.StepIndex];
        int stepIndex = ClipStepIndex(c.StepIndex + AdpcmTables.IndexTable[nibble]);

        int diff = step >> 3;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 1) != 0) diff += step >> 2;

        int predictor = (nibble & 8) != 0 ? c.Predictor - diff : c.Predictor + diff;

        c.Predictor = ClipInt16(predictor);
        c.StepIndex = stepIndex;
        return (short)c.Predictor;
    }

    /// <summary>
    /// <c>adpcm_ima_expand_nibble</c> - the IMA reference algorithm's multiply form,
    /// <c>((2 * delta + 1) * step) &gt;&gt; shift</c>, which the C's own comment says replaces
    /// the reference implementation's series of jumps because a modern CPU can multiply.
    /// </summary>
    private static short ImaExpandNibble(ref AdpcmChannelStatus c, int nibble, int shift)
    {
        int step = AdpcmTables.StepTable[c.StepIndex];
        int stepIndex = ClipStepIndex(c.StepIndex + AdpcmTables.IndexTable[nibble]);

        int sign = nibble & 8;
        int delta = nibble & 7;
        int diff = (2 * delta + 1) * step >> shift;
        int predictor = sign != 0 ? c.Predictor - diff : c.Predictor + diff;

        c.Predictor = ClipInt16(predictor);
        c.StepIndex = stepIndex;
        return (short)c.Predictor;
    }

    /// <summary>The expansion the four-bit IMA WAV arm uses; see <see cref="ImaWavUsesQtNibble"/>.</summary>
    private static short ImaWavExpandNibble(ref AdpcmChannelStatus c, int nibble) =>
        ImaWavUsesQtNibble
            ? ImaQtExpandNibble(ref c, nibble)
            : ImaExpandNibble(ref c, nibble, 3);

    /// <summary><c>adpcm_ms_expand_nibble</c>.</summary>
    /// <remarks>
    /// The <c>/ 64</c> is C's integer division and truncates toward zero. An arithmetic
    /// <c>&gt;&gt; 6</c> rounds toward negative infinity instead, and the two differ by one on
    /// every negative product that is not a multiple of 64 - which is most of them once the
    /// signal goes below zero.
    /// </remarks>
    private static short MsExpandNibble(ref AdpcmChannelStatus c, int nibble)
    {
        int predictor = (c.Sample1 * c.Coeff1 + c.Sample2 * c.Coeff2) / 64;
        predictor += ((nibble & 0x08) != 0 ? nibble - 0x10 : nibble) * c.Idelta;

        c.Sample2 = c.Sample1;
        c.Sample1 = ClipInt16(predictor);
        c.Idelta = AdpcmTables.AdaptationTable[nibble] * c.Idelta >> 8;
        if (c.Idelta < 16) c.Idelta = 16;
        if (c.Idelta > int.MaxValue / 768) c.Idelta = int.MaxValue / 768;

        return (short)c.Sample1;
    }

    /// <summary>
    /// Decodes one block into interleaved 16-bit samples. Returns samples per channel.
    /// </summary>
    /// <summary>
    /// <c>get_nb_samples</c> as the C has it: given the BYTES and not just the length.
    /// </summary>
    /// <remarks>
    /// AMV states its sample count in its own eight-byte header, so a length is not enough -
    /// and the length-only overload answers 0 for it until something has shown it a packet.
    /// That is a deadlock for any caller that asks how many samples a packet holds BEFORE
    /// decoding it, which is what an adapter sizing its output buffer does: AMV audio
    /// produced no frames at all through DecodePipeline, on a file whose 576 audio packets
    /// all arrive correctly and whose decoder is gated frame by frame elsewhere. The C has
    /// no such split - get_nb_samples takes the GetByteContext.
    /// </remarks>
    public int GetNbSamples(ReadOnlySpan<byte> block)
    {
        if (CodecId == AdpcmCodecId.ImaAmv && block.Length >= 8)
            block[..8].CopyTo(amvHeader);

        return GetNbSamples(block.Length);
    }

    public int DecodeBlock(ReadOnlySpan<byte> block, Span<short> output)
    {
        if (CodecId == AdpcmCodecId.None)
            throw new InvalidOperationException("ADPCM: DecodeBlock before Initialize.");

        if (block.Length > 0) swfFirstByte = block[0];

        // AMV's sample count is in its header, so GetNbSamples needs the bytes and not just
        // the length. The same shape as swfFirstByte above, and for the same reason.
        if (CodecId == AdpcmCodecId.ImaAmv && block.Length >= 8)
            block[..8].CopyTo(amvHeader);
        int nbSamples = GetNbSamples(block.Length);
        if (nbSamples <= 0) return 0;

        return CodecId switch
        {
            AdpcmCodecId.Thp => DecodeThp(block, output, nbSamples),
            AdpcmCodecId.Ms => DecodeMs(block, output, nbSamples),
            AdpcmCodecId.ImaWav => DecodeImaWav(block, output, nbSamples),
            AdpcmCodecId.EaMaxisXa => DecodeEaMaxisXa(block, output, nbSamples),
            AdpcmCodecId.ImaQt => DecodeImaQt(block, output),
            AdpcmCodecId.ImaSmjpeg => DecodeImaSmjpeg(block, output, nbSamples),
            AdpcmCodecId.ImaAmv => DecodeImaAmv(block, output, nbSamples),
            AdpcmCodecId.Yamaha => DecodeYamaha(block, output, nbSamples),
            AdpcmCodecId.Swf => DecodeSwf(block, output, nbSamples),
            _ => throw new NotSupportedException($"ADPCM: {CodecId} has no decoder."),
        };
    }

    /// <summary><c>CASE(ADPCM_EA_MAXIS_XA, ...)</c>.</summary>
    /// <remarks>
    /// The predictor state carries across blocks: nothing here resets sample1 or sample2, which
    /// is why a .XA has to be decoded from its first packet. The samples come out pairwise -
    /// LL RR for stereo, LL LL for mono - which is the inner loop running the high nibble of
    /// every channel's byte before any low nibble.
    /// </remarks>
    private int DecodeEaMaxisXa(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int st = channels == 2 ? 1 : 0;
        int p = 0;
        int o = 0;

        Span<int> coeff = stackalloc int[4];     // [channel][i], two per channel
        Span<int> shift = stackalloc int[2];

        for (int channel = 0; channel < channels; channel++)
        {
            int b = buf[p++];
            for (int i = 0; i < 2; i++)
                coeff[channel * 2 + i] = AdpcmTables.EaAdpcmTable[(b >> 4) + 4 * i];
            shift[channel] = 20 - (b & 0x0F);
        }

        Span<int> bytes = stackalloc int[2];
        for (int count1 = 0; count1 < nbSamples / 2; count1++)
        {
            bytes[0] = buf[p++];
            if (st != 0) bytes[1] = buf[p++];
            for (int i = 4; i >= 0; i -= 4)
            {
                /* Pairwise samples LL RR (st) or LL LL (mono) */
                for (int channel = 0; channel < channels; channel++)
                {
                    int sample = SignExtend4(bytes[channel] >> i) * (1 << shift[channel]);
                    sample = (sample
                            + status[channel].Sample1 * coeff[channel * 2]
                            + status[channel].Sample2 * coeff[channel * 2 + 1] + 0x80) >> 8;
                    status[channel].Sample2 = status[channel].Sample1;
                    status[channel].Sample1 = ClipInt16(sample);
                    output[o++] = (short)status[channel].Sample1;
                }
            }
        }

        return nbSamples;
    }

    /// <summary><c>sign_extend(v, 4)</c> of libavutil - the low four bits as a signed value.</summary>
    private static int SignExtend4(int v) => ((v & 0x0F) ^ 8) - 8;

    /// <summary><c>adpcm_yamaha_expand_nibble</c>.</summary>
    /// <remarks>
    /// The <c>/ 8</c> is C's integer division of a product that can be negative, so it
    /// truncates toward zero where <c>&gt;&gt; 3</c> would round toward negative infinity.
    /// Same trap as MS-ADPCM's <c>/ 64</c>, and the difflookup table is signed, so half the
    /// nibbles take the negative path.
    /// </remarks>
    private static short YamahaExpandNibble(ref AdpcmChannelStatus c, int nibble)
    {
        if (c.Step == 0)
        {
            c.Predictor = 0;
            c.Step = 127;
        }

        c.Predictor += c.Step * AdpcmTables.YamahaDiffLookup[nibble] / 8;
        c.Predictor = ClipInt16(c.Predictor);
        c.Step = c.Step * AdpcmTables.YamahaIndexScale[nibble] >> 8;
        c.Step = c.Step < 127 ? 127 : c.Step > 24576 ? 24576 : c.Step;
        return (short)c.Predictor;
    }

    /// <summary><c>CASE(ADPCM_YAMAHA, ...)</c>.</summary>
    /// <remarks>
    /// For stereo the two nibbles of a byte are the two channels, so the output is already
    /// interleaved; for mono they are two consecutive samples, which is what <c>st</c>
    /// selecting <c>status[0]</c> twice means.
    /// </remarks>
    private int DecodeYamaha(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int st = Channels == 2 ? 1 : 0;
        int p = 0, o = 0;

        for (int n = nbSamples >> (1 - st); n > 0; n--)
        {
            int v = buf[p++];
            output[o++] = YamahaExpandNibble(ref status[0], v & 0x0F);
            output[o++] = YamahaExpandNibble(ref status[st], v >> 4);
        }

        return nbSamples;
    }

    /// <summary><c>CASE(ADPCM_IMA_QT, ...)</c>.</summary>
    /// <remarks>
    /// <para>
    /// "In QuickTime, IMA is encoded by chunks of 34 bytes (=64 samples). Channel data is
    /// interleaved per-chunk." - the C's own comment. Two bytes of header per channel hold
    /// the top nine bits of the predictor and the step index in the low seven, overlapping:
    /// <c>(pppppp) (piiiiiii)</c>.
    /// </para>
    /// <para>
    /// The header is <b>not</b> taken at face value. The C keeps its own predictor when the
    /// step index matches and the predictor is within 0x7F of it, because the nine bits in
    /// the header are a rounded copy of a value the encoder had in full, and trusting them
    /// would reset the predictor to something slightly wrong 64 samples at a time. The
    /// <c>goto update</c> in the middle of that test is restructured here, which is the
    /// divergence the translation rules allow for a goto.
    /// </para>
    /// <para>
    /// The C writes to <c>samples_p[channel]</c> because this decoder's output format is
    /// planar; this writes interleaved, because that is what every other variant here and
    /// the adapter above expect.
    /// </para>
    /// </remarks>
    private int DecodeImaQt(ReadOnlySpan<byte> buf, Span<short> output)
    {
        int channels = Channels;

        for (int channel = 0; channel < channels; channel++)
        {
            int at = channel * 34;

            // "Bits 15-7 are the _top_ 9 bits of the 16-bit initial predictor value"
            int predictor = (short)((buf[at] << 8) | buf[at + 1]);
            int stepIndex = predictor & 0x7F;
            predictor &= ~0x7F;

            bool update = true;
            if (status[channel].StepIndex == stepIndex)
            {
                int diff = predictor - status[channel].Predictor;
                if (diff < 0) diff = -diff;
                update = diff > 0x7F;
            }

            if (update)
            {
                status[channel].StepIndex = stepIndex;
                status[channel].Predictor = predictor;
            }

            if ((uint)status[channel].StepIndex > 88u)
                throw new InvalidDataException(
                    $"ADPCM IMA QT: step_index[{channel}] = {status[channel].StepIndex}.");

            for (int m = 0; m < 64; m += 2)
            {
                int b = buf[at + 2 + m / 2];
                output[(m) * channels + channel] =
                    ImaQtExpandNibble(ref status[channel], b & 0x0F);
                output[(m + 1) * channels + channel] =
                    ImaQtExpandNibble(ref status[channel], b >> 4);
            }
        }

        return 64;
    }

    /// <summary><c>CASE(ADPCM_IMA_AMV, ...)</c>.</summary>
    /// <remarks>
    /// <para>
    /// Mono only - the C asserts it - with an eight-byte header: a LITTLE-endian predictor,
    /// a step index, a reserved byte, and a four-byte sample count. "Some implementations
    /// have step_index as 16-bits, but others only use the lower 8 and store garbage in the
    /// upper 8", which is the C's comment and the reason only one byte of it is read.
    /// </para>
    /// <para>
    /// The expansion is the MULTIPLY form at shift 3, not QuickTime's accumulate - the
    /// opposite of its SMJPEG cousin, which is the kind of difference that decodes to
    /// something recognisable and wrong.
    /// </para>
    /// <para>
    /// An odd sample count leaves a final high nibble whose low partner is dropped. The C
    /// warns when that low nibble is nonzero and says it holds true on every known sample
    /// that it is zero; the sample is skipped either way.
    /// </para>
    /// </remarks>
    private int DecodeImaAmv(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        status[0].Predictor = (short)(buf[0] | (buf[1] << 8));
        status[0].StepIndex = buf[2];
        // buf[3] is reserved and buf[4..8] is the sample count, both skipped here - the C's
        // bytestream2_skipu(&gb, 5) after the step index.

        if ((uint)status[0].StepIndex > 88u)
            throw new InvalidDataException(
                $"ADPCM IMA AMV: step_index = {status[0].StepIndex}.");

        int from = 8;
        int to = 0;

        for (int n = nbSamples >> 1; n > 0; n--)
        {
            int v = buf[from++];
            output[to++] = ImaExpandNibble(ref status[0], v >> 4, 3);
            output[to++] = ImaExpandNibble(ref status[0], v & 0xF, 3);
        }

        if ((nbSamples & 1) != 0)
        {
            int v = buf[from++];
            output[to++] = ImaExpandNibble(ref status[0], v >> 4, 3);
            // The low nibble of that byte is dropped, which is what the C warns about.
        }

        return nbSamples;
    }

    /// <summary><c>CASE(ADPCM_IMA_SMJPEG, ...)</c>.</summary>
    /// <remarks>
    /// <para>
    /// The header is four bytes a channel - a big-endian predictor, a step index, and a
    /// padding byte the C reads and discards - and then the packet is nothing but nibbles.
    /// Unlike QuickTime's IMA there is no block within the packet and no second-guessing of
    /// the header: the predictor it states is the predictor, because this encoder wrote all
    /// sixteen bits of it rather than the top nine.
    /// </para>
    /// <para>
    /// The nibble expansion IS QuickTime's, <c>ff_adpcm_ima_qt_expand_nibble</c> - the
    /// accumulate-by-tested-bits form, not the multiply. Swapping in
    /// <see cref="ImaExpandNibble"/> at shift 3 is red from the first packet.
    /// </para>
    /// <para>
    /// The high nibble comes first, which is the opposite of the QuickTime decoder above, and
    /// the two channels alternate within a byte rather than within a block: for stereo, one
    /// byte carries the left sample in its high nibble and the right in its low. The C writes
    /// that straight out through an interleaved pointer - this variant takes the default
    /// AV_SAMPLE_FMT_S16 arm of adpcm_decode_init, not the planar one - so there is no
    /// divergence here.
    /// </para>
    /// </remarks>
    private int DecodeImaSmjpeg(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int st = channels == 2 ? 1 : 0;

        for (int channel = 0; channel < channels; channel++)
        {
            int at = channel * 4;
            status[channel].Predictor = (short)((buf[at] << 8) | buf[at + 1]);
            status[channel].StepIndex = buf[at + 2];
            // buf[at + 3] is bytestream2_skipu(&gb, 1).

            if ((uint)status[channel].StepIndex > 88u)
                throw new InvalidDataException(
                    $"ADPCM IMA SMJPEG: step_index[{channel}] = {status[channel].StepIndex}.");
        }

        int from = 4 * channels;
        int to = 0;

        for (int n = nbSamples >> (1 - st); n > 0; n--)
        {
            int v = buf[from++];
            output[to++] = ImaQtExpandNibble(ref status[0], v >> 4);
            output[to++] = ImaQtExpandNibble(ref status[st], v & 0xF);
        }

        return nbSamples;
    }

    /// <summary><c>adpcm_swf_decode</c>, which <c>CASE(ADPCM_SWF, ...)</c> is a call to.</summary>
    /// <remarks>
    /// <para>
    /// The only variant here whose nibble width is in the stream: two bits at the front of
    /// the block say 2, 3, 4 or 5, and everything after is read at that width. That is also
    /// why this is the only one read with a bit reader.
    /// </para>
    /// <para>
    /// The delta expansion is "similar to IMA adpcm" (the C's comment) but not the same: it
    /// walks the delta's bits from <c>k0</c> down, halving the step each time and adding it
    /// where the bit is set, then adds the final halved step - which is the
    /// <c>(delta + 0.5) * step / 4</c> the comment names, accumulated with a separate
    /// truncation per bit. It cannot be rewritten as a multiply.
    /// </para>
    /// </remarks>
    private int DecodeSwf(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int size = buf.Length * 8;
        var gb = new AdpcmBitReader(buf);

        int nbBits = gb.GetBits(2) + 2;
        ReadOnlySpan<sbyte> table = AdpcmTables.SwfIndexTables.Slice((nbBits - 2) * 16, 16);
        int k0 = 1 << (nbBits - 2);
        int signmask = 1 << (nbBits - 1);

        int o = 0;
        while (gb.BitsRead <= size - 22 * channels)
        {
            for (int i = 0; i < channels; i++)
            {
                status[i].Predictor = gb.GetSBits(16);
                output[o++] = (short)status[i].Predictor;
                status[i].StepIndex = gb.GetBits(6);
            }

            for (int count = 0;
                 gb.BitsRead <= size - nbBits * channels && count < 4095;
                 count++)
            {
                for (int i = 0; i < channels; i++)
                {
                    int delta = gb.GetBits(nbBits);
                    int step = AdpcmTables.StepTable[status[i].StepIndex];
                    int vpdiff = 0;   // vpdiff = (delta+0.5)*step/4
                    int k = k0;

                    do
                    {
                        if ((delta & k) != 0) vpdiff += step;
                        step >>= 1;
                        k >>= 1;
                    }
                    while (k != 0);
                    vpdiff += step;

                    if ((delta & signmask) != 0) status[i].Predictor -= vpdiff;
                    else status[i].Predictor += vpdiff;

                    status[i].StepIndex += table[delta & ~signmask];

                    status[i].StepIndex = ClipStepIndex(status[i].StepIndex);
                    status[i].Predictor = ClipInt16(status[i].Predictor);

                    output[o++] = (short)status[i].Predictor;
                }
            }
        }

        // The C hands get_nb_samples' count to the frame and lets the decoder fill what it
        // can; the two agree on a well-formed block, and the caller is told what was written
        // rather than what was predicted.
        return o / channels;
    }

    /// <summary><c>CASE(ADPCM_MS, ...)</c>, the one- and two-channel arm.</summary>
    /// <summary>
    /// <c>CASE(ADPCM_THP)</c> - the audio of a GameCube THP, big-endian.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not an IMA variant: a second-order predictor whose sixteen coefficients are carried
    /// PER CHANNEL in every packet, eight of them pairs selected by the top nibble of each
    /// block header byte. Fourteen samples come out of eight bytes - a header byte holding
    /// the predictor index and an exponent, then thirteen nibbles - which is why the sample
    /// loop reads a byte on the even step and the low nibble of that same byte on the odd.
    /// </para>
    /// <para>
    /// The two previous samples are in the packet too, and are read ONLY on the first one:
    /// after that the C skips those four bytes per channel and keeps the state it has, which
    /// is what makes this a running decode rather than a per-packet one.
    /// </para>
    /// <para>
    /// DIVERGENCE: the C writes PLANAR output and this writes interleaved, the same as the
    /// IMA QT arm and for the same reason - DecodedAudioFrame carries interleaved samples.
    /// </para>
    /// </remarks>
    private int DecodeThp(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int p = 8;  // channel size and coded sample count, both read by GetNbSamples

        // table[ch][16]
        Span<int> table = stackalloc int[14 * 16];
        for (int ch = 0; ch < channels; ch++)
        {
            for (int n = 0; n < 16; n++)
            {
                table[ch * 16 + n] = (short)((buf[p] << 8) | buf[p + 1]);
                p += 2;
            }
        }

        if (!thpHasStatus)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                status[ch].Sample1 = (short)((buf[p] << 8) | buf[p + 1]);
                status[ch].Sample2 = (short)((buf[p + 2] << 8) | buf[p + 3]);
                p += 4;
            }
            thpHasStatus = true;
        }
        else
        {
            p += channels * 4;
        }

        for (int ch = 0; ch < channels; ch++)
        {
            int at = p;

            for (int i = 0; i < (nbSamples + 13) / 14; i++)
            {
                int header = buf[at++];
                int index = (header >> 4) & 7;
                int exp = header & 0x0F;
                long factor1 = table[ch * 16 + index * 2];
                long factor2 = table[ch * 16 + index * 2 + 1];

                for (int n = 0; n < 14 && i * 14 + n < nbSamples; n++)
                {
                    int sampledat;
                    if ((n & 1) != 0)
                    {
                        sampledat = (header & 0x0F) << 28 >> 28;
                    }
                    else
                    {
                        header = buf[at++];
                        sampledat = (header >> 4) << 28 >> 28;
                    }

                    long predicted =
                        ((status[ch].Sample1 * factor1 + status[ch].Sample2 * factor2) >> 11)
                        + ((long)sampledat << exp);
                    short clipped = (short)Math.Clamp(predicted, short.MinValue, short.MaxValue);

                    output[(i * 14 + n) * channels + ch] = clipped;
                    status[ch].Sample2 = status[ch].Sample1;
                    status[ch].Sample1 = clipped;
                }
            }

            // Each channel's nibbles follow the previous channel's, and the C advances its
            // own cursor the same way by decoding them in order.
            p = at;
        }

        return nbSamples;
    }

    /// <summary>
    /// <c>c-&gt;has_status</c> for THP: whether the two previous samples have been read out
    /// of a packet already. The first packet carries them and the rest repeat the bytes
    /// without meaning them.
    /// </summary>
    private bool thpHasStatus;

    private int DecodeMs(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int st = channels == 2 ? 1 : 0;
        int p = 0;

        int blockPredictor = buf[p++];
        if (blockPredictor > 6)
            throw new InvalidOperationException(
                $"ADPCM MS: block_predictor[0] = {blockPredictor}.");
        status[0].Coeff1 = AdpcmTables.AdaptCoeff1[blockPredictor];
        status[0].Coeff2 = AdpcmTables.AdaptCoeff2[blockPredictor];

        if (st != 0)
        {
            blockPredictor = buf[p++];
            if (blockPredictor > 6)
                throw new InvalidOperationException(
                    $"ADPCM MS: block_predictor[1] = {blockPredictor}.");
            status[1].Coeff1 = AdpcmTables.AdaptCoeff1[blockPredictor];
            status[1].Coeff2 = AdpcmTables.AdaptCoeff2[blockPredictor];
        }

        status[0].Idelta = ReadS16(buf, ref p);
        if (st != 0) status[1].Idelta = ReadS16(buf, ref p);

        status[0].Sample1 = ReadS16(buf, ref p);
        if (st != 0) status[1].Sample1 = ReadS16(buf, ref p);
        status[0].Sample2 = ReadS16(buf, ref p);
        if (st != 0) status[1].Sample2 = ReadS16(buf, ref p);

        int o = 0;
        output[o++] = (short)status[0].Sample2;
        if (st != 0) output[o++] = (short)status[1].Sample2;
        output[o++] = (short)status[0].Sample1;
        if (st != 0) output[o++] = (short)status[1].Sample1;

        for (int n = (nbSamples - 2) >> (1 - st); n > 0; n--)
        {
            if (p >= buf.Length) break;
            int b = buf[p++];
            output[o++] = MsExpandNibble(ref status[0], b >> 4);
            output[o++] = MsExpandNibble(ref status[st], b & 0x0F);
        }

        return o / channels;
    }

    /// <summary>
    /// <c>CASE(ADPCM_IMA_WAV, ...)</c>, the four-bit arm - which is every IMA WAV anything
    /// writes.
    /// </summary>
    /// <remarks>
    /// The C decodes into planar buffers and interleaves later; this writes interleaved
    /// directly, which is the same loop with the destination index transposed. The nibble order
    /// within a byte is low first, unlike MS ADPCM, and the channels alternate every four bytes
    /// rather than every nibble.
    /// </remarks>
    private int DecodeImaWav(ReadOnlySpan<byte> buf, Span<short> output, int nbSamples)
    {
        int channels = Channels;
        int p = 0;

        for (int i = 0; i < channels; i++)
        {
            status[i].Predictor = ReadS16(buf, ref p);
            output[i] = (short)status[i].Predictor;

            status[i].StepIndex = buf[p++];
            p++;    // the C skips a byte here: it is reserved
            if ((uint)status[i].StepIndex > 88u)
                throw new InvalidOperationException(
                    $"ADPCM IMA WAV: step_index[{i}] = {status[i].StepIndex}.");
        }

        if (BitsPerCodedSample != 4)
            throw new NotSupportedException(
                $"ADPCM IMA WAV: {BitsPerCodedSample} bits per coded sample. The 2-, 3- and "
              + "5-bit arms of the C exist and are not translated; nothing writes them, and "
              + "they stay todo in the registry rather than being approximated here.");

        for (int n = 0; n < (nbSamples - 1) / 8; n++)
        {
            for (int i = 0; i < channels; i++)
            {
                for (int m = 0; m < 8; m += 2)
                {
                    if (p >= buf.Length) return nbSamples;
                    int v = buf[p++];
                    int baseIndex = (1 + n * 8 + m) * channels + i;
                    output[baseIndex] = ImaWavExpandNibble(ref status[i], v & 0x0F);
                    output[baseIndex + channels] = ImaWavExpandNibble(ref status[i], v >> 4);
                }
            }
        }

        return nbSamples;
    }

    /// <summary><c>sign_extend(bytestream2_get_le16u(&amp;gb), 16)</c>.</summary>
    private static int ReadS16(ReadOnlySpan<byte> buf, ref int p)
    {
        int v = buf[p] | (buf[p + 1] << 8);
        p += 2;
        return (short)v;
    }
}

/// <summary>
/// <c>get_bits.h</c> in its default most-significant-bit-first mode, reduced to the three
/// calls <c>adpcm_swf_decode</c> makes: <c>get_bits</c>, <c>get_sbits</c> and
/// <c>get_bits_count</c>. SWF is the only ADPCM variant that needs one.
/// </summary>
/// <remarks>
/// Reading past the end returns zero bits rather than throwing, which is what
/// <c>init_get_bits</c>'s size check gives the C: <c>adpcm_swf_decode</c>'s two loop
/// conditions are both bit-count tests against the buffer size, so a well-formed block
/// never reaches the end and a truncated one stops at it.
/// </remarks>
internal ref struct AdpcmBitReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> buffer = buffer;
    private int bitsRead;

    /// <summary><c>get_bits_count</c>.</summary>
    public int BitsRead => bitsRead;

    /// <summary><c>get_bits(n)</c>, for n up to 24.</summary>
    public int GetBits(int n)
    {
        int value = 0;
        for (int i = 0; i < n; i++)
        {
            int index = bitsRead >> 3;
            int bit = index < buffer.Length
                ? (buffer[index] >> (7 - (bitsRead & 7))) & 1
                : 0;
            value = (value << 1) | bit;
            bitsRead++;
        }
        return value;
    }

    /// <summary><c>get_sbits(n)</c> - the same bits, sign-extended from bit n-1.</summary>
    public int GetSBits(int n)
    {
        int value = GetBits(n);
        int sign = 1 << (n - 1);
        return (value ^ sign) - sign;
    }
}

/// <summary>The registry entry for Microsoft ADPCM.</summary>
[Codec("adpcm_ms", CodecType.Audio, DisplayName = "Microsoft ADPCM", Priority = 5)]
public sealed class AdpcmMsDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ms";
    protected override AdpcmCodecId Variant => AdpcmCodecId.Ms;
}

/// <summary>The registry entry for IMA ADPCM in a RIFF wrapper.</summary>
[Codec("adpcm_ima_wav", CodecType.Audio, DisplayName = "IMA ADPCM (WAV)", Priority = 5)]
public sealed class AdpcmImaWavDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ima_wav";
    protected override AdpcmCodecId Variant => AdpcmCodecId.ImaWav;
}

/// <summary>The registry entry for EA's ADPCM as Maxis wrote it into a .XA file.</summary>
[Codec("adpcm_ea_maxis_xa", CodecType.Audio, DisplayName = "EA Maxis XA ADPCM", Priority = 5)]
public sealed class AdpcmEaMaxisXaDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ea_maxis_xa";
    protected override AdpcmCodecId Variant => AdpcmCodecId.EaMaxisXa;
}

/// <summary>The registry entry for Yamaha ADPCM.</summary>
[Codec("adpcm_yamaha", CodecType.Audio, DisplayName = "Yamaha ADPCM", Priority = 5)]
public sealed class AdpcmYamahaDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_yamaha";
    protected override AdpcmCodecId Variant => AdpcmCodecId.Yamaha;
}

/// <summary>The registry entry for Flash's ADPCM.</summary>
[Codec("adpcm_swf", CodecType.Audio, DisplayName = "Shockwave Flash ADPCM", Priority = 5)]
public sealed class AdpcmSwfDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_swf";
    protected override AdpcmCodecId Variant => AdpcmCodecId.Swf;
}

/// <summary>The registry entry for QuickTime's IMA ADPCM.</summary>
[Codec("adpcm_ima_qt", CodecType.Audio, DisplayName = "IMA ADPCM (QuickTime)", Priority = 5)]
public sealed class AdpcmImaQtDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ima_qt";
    protected override AdpcmCodecId Variant => AdpcmCodecId.ImaQt;
}

/// <summary>The registry entry for the audio of an AMV file.</summary>
[Codec("adpcm_ima_amv", CodecType.Audio, DisplayName = "IMA ADPCM (AMV)", Priority = 5)]
public sealed class AdpcmImaAmvDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ima_amv";
    protected override AdpcmCodecId Variant => AdpcmCodecId.ImaAmv;

    /// <inheritdoc/>
    /// <remarks>
    /// An AMV packet states its own sample count in its header, so there is no block
    /// alignment and nothing to clamp a packet to.
    /// </remarks>
    protected override bool RequiresBlockAlign => false;
}

/// <summary>The registry entry for the audio of a GameCube THP.</summary>
[Codec("adpcm_thp", CodecType.Audio, DisplayName = "ADPCM (GameCube THP)", Priority = 5)]
public sealed class AdpcmThpDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_thp";
    protected override AdpcmCodecId Variant => AdpcmCodecId.Thp;

    /// <inheritdoc/>
    /// <remarks>
    /// A THP has no block alignment: its audio component is whatever length the frame made
    /// it, and every packet states its own sample count.
    /// </remarks>
    protected override bool RequiresBlockAlign => false;
}

/// <summary>The registry entry for the audio of a Loki SDL Motion JPEG.</summary>
[Codec("adpcm_ima_smjpeg", CodecType.Audio, DisplayName = "IMA ADPCM (Loki SDL MJPEG)",
       Priority = 5)]
public sealed class AdpcmImaSmjpegDecoderAdapter : AdpcmDecoderAdapterBase
{
    public override string CodecId => "adpcm_ima_smjpeg";
    protected override AdpcmCodecId Variant => AdpcmCodecId.ImaSmjpeg;

    /// <inheritdoc/>
    /// <remarks>
    /// SMJPEG has no block: a packet is a four-byte-per-channel header and then nibbles to
    /// its end, whatever length the container made it, so there is no alignment to require
    /// and nothing to clamp a packet to.
    /// </remarks>
    protected override bool RequiresBlockAlign => false;
}

/// <summary>Shared body for the ADPCM adapters.</summary>
public abstract class AdpcmDecoderAdapterBase : AudioDecoderAdapterBase
{
    private readonly Adpcm decoder = new();
    private short[] scratch = new short[8192];
    private int sampleRate;
    private int channels;
    private int blockAlign;
    private int bitsPerCodedSample = 4;
    private bool configured;

    /// <summary>Which variant this adapter decodes.</summary>
    protected abstract AdpcmCodecId Variant { get; }

    /// <summary>
    /// Whether this variant is block-based, and so cannot be initialised without the block
    /// alignment the container carries. True for everything but SMJPEG.
    /// </summary>
    protected virtual bool RequiresBlockAlign => true;

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override int Channels => channels;

    /// <summary>
    /// ADPCM needs the block alignment and the coded sample width as well as the rate: both
    /// live in the WAV format chunk and not in any codec-specific blob, and the block alignment
    /// decides how many samples a packet holds.
    /// </summary>
    public override void ConfigureAudio(
        int rate, int channelCount, int align = 0, int bits = 0)
    {
        sampleRate = rate;
        channels = channelCount;
        blockAlign = align;
        if (bits > 0) bitsPerCodedSample = bits;
    }

    /// <inheritdoc/>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (sampleRate <= 0 || channels <= 0) return false;
        if (RequiresBlockAlign && blockAlign <= 0) return false;
        decoder.Initialize(Variant, sampleRate, channels, blockAlign, bitsPerCodedSample);
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

        // The BYTES, not the length: AMV's sample count is in its packet header, and the
        // length-only overload cannot see it.
        int perChannel = decoder.GetNbSamples(data);
        if (perChannel <= 0) return null;

        int total = perChannel * channels;
        if (scratch.Length < total) scratch = new short[total];
        Array.Clear(scratch, 0, total);

        int got = decoder.DecodeBlock(data, scratch);
        if (got <= 0) return null;

        total = got * channels;
        EnsureBuffer(total);
        for (int i = 0; i < total; i++) SampleBuffer[i] = scratch[i] * (1f / 32768f);

        return MakeFrame(got, presentationTimeTicks);
    }

    /// <summary>Each block carries its own predictor state, so a flush drops nothing.</summary>
    public override void Flush() { }
}

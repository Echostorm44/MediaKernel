// Translated from ffmpeg-master/libavcodec/gsmdec.c and msgsmdec.c - the frame level, and the
// one difference between the two codecs these files register.
//
// GSM 06.10 full rate is 33 bytes to 160 samples: 4 bits of magic, 36 bits of log-area ratios
// and four subframes of 56. Microsoft's variant drops the magic, packs the identical fields
// least-significant-bit first and puts TWO blocks in 65 bytes - 520 bits, which is 2 x 260
// exactly, so the second block starts mid-byte and the whole thing only works because the
// reader never realigns.
//
// MSN's reduced rates come out of the block alignment and nowhere else: mode is
// (65 - block_align) / 3, so a 41-byte block is mode 8 at 8.2 kbit/s. There is no sample in
// this tree at any rate but 13000, so the reduced modes are translated and ungated, and the
// function registry says so rather than this comment being the only record.

using System;

namespace MediaKernel.Core.Codecs.Audio.Gsm;

/// <summary>Which of the two codecs <c>gsmdec.c</c> registers a stream is.</summary>
public enum GsmCodecId
{
    /// <summary>Not set; the decoder throws rather than guessing a bit order.</summary>
    None = 0,

    /// <summary><c>AV_CODEC_ID_GSM</c> - 33-byte blocks, most significant bit first.</summary>
    Gsm,

    /// <summary>
    /// <c>AV_CODEC_ID_GSM_MS</c> - two blocks in 65 bytes, least significant bit first.
    /// </summary>
    GsmMs,
}

/// <summary>GSM 06.10 and its Microsoft variant.</summary>
public sealed class GsmDec
{
    private readonly GsmContext ctx = new();
    private GsmCodecId codecId;
    private int blockAlign;
    private int frameSize;

    /// <summary>Which codec this instance decodes.</summary>
    public GsmCodecId CodecId => codecId;

    /// <summary><c>avctx-&gt;block_align</c> - the bytes one packet must hold.</summary>
    public int BlockAlign => blockAlign;

    /// <summary><c>avctx-&gt;frame_size</c> - the samples one packet decodes to.</summary>
    public int FrameSize => frameSize;

    /// <summary>
    /// <c>gsm_init</c>. <paramref name="containerBlockAlign"/> is the container's block
    /// alignment, which for the Microsoft variant selects the bit rate and for plain GSM is
    /// ignored.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The block alignment is not one the Microsoft variant has a mode for. The C returns
    /// AVERROR_INVALIDDATA here: there is no mode to fall back to, and decoding at the wrong
    /// one reads the subframes at the wrong widths.
    /// </exception>
    public void GsmInit(GsmCodecId id, int containerBlockAlign)
    {
        switch (id)
        {
            case GsmCodecId.Gsm:
                frameSize = GsmSizes.GsmFrameSize;
                blockAlign = GsmSizes.GsmBlockSize;
                break;

            case GsmCodecId.GsmMs:
                frameSize = 2 * GsmSizes.GsmFrameSize;
                if (containerBlockAlign == 0)
                {
                    blockAlign = GsmSizes.GsmMsBlockSize;
                }
                else
                {
                    if (containerBlockAlign < GsmSizes.MsnMinBlockSize ||
                        containerBlockAlign > GsmSizes.GsmMsBlockSize ||
                        (containerBlockAlign - GsmSizes.MsnMinBlockSize) % 3 != 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(containerBlockAlign), containerBlockAlign,
                            "GSM MS: invalid block alignment; gsmdec.c accepts 41 to 65 in "
                          + "steps of three.");
                    }
                    blockAlign = containerBlockAlign;
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(id), id, "GSM: no codec id, so no bit order.");
        }

        codecId = id;
        ctx.Clear();
    }

    /// <summary><c>gsm_flush</c>.</summary>
    public void GsmFlush() => ctx.Clear();

    /// <summary>
    /// <c>gsm_decode_frame</c>. One packet is one block alignment and decodes to
    /// <see cref="FrameSize"/> samples.
    /// </summary>
    /// <returns>The number of samples written.</returns>
    public int GsmDecodeFrame(ReadOnlySpan<byte> buf, Span<short> samples, out bool magicMissing)
    {
        magicMissing = false;

        if (codecId == GsmCodecId.None)
            throw new InvalidOperationException("GSM: decode before GsmInit.");

        if (buf.Length < blockAlign)
            throw new ArgumentException(
                $"GSM: a packet of {buf.Length} bytes is smaller than the {blockAlign}-byte "
              + "block alignment. The C logs 'Packet is too small' and returns "
              + "AVERROR_INVALIDDATA.", nameof(buf));

        if (samples.Length < frameSize)
            throw new ArgumentException(
                $"GSM: {frameSize} samples per packet and the output holds {samples.Length}.",
                nameof(samples));

        switch (codecId)
        {
            case GsmCodecId.Gsm:
            {
                var gb = new GsmBitReader(buf, packedLittleEndian: false);

                // The C logs "Missing GSM magic!" and decodes anyway, because the four bits
                // are a sync aid and not a checksum - a stream cut at the wrong offset is
                // still worth decoding. Reported rather than swallowed so a test can assert
                // on it; see the note on well-formed output hiding a fault.
                if (gb.GetBits(4) != 0xd)
                    magicMissing = true;

                GsmDecTemplate.GsmDecodeBlock(ctx, samples, ref gb, (int)GsmMode.Gsm13000);
                break;
            }

            case GsmCodecId.GsmMs:
                FfMsgsmDecodeBlock(samples, buf,
                                   (GsmSizes.GsmMsBlockSize - blockAlign) / 3);
                break;

            default:
                throw new InvalidOperationException($"GSM: no decode path for {codecId}.");
        }

        return frameSize;
    }

    /// <summary>
    /// <c>ff_msgsm_decode_block</c>: two blocks out of one 65-byte packet, from a single
    /// reader, so the second starts wherever the first left off.
    /// </summary>
    private void FfMsgsmDecodeBlock(Span<short> samples, ReadOnlySpan<byte> buf, int mode)
    {
        var gb = new GsmBitReader(buf, packedLittleEndian: true);
        GsmDecTemplate.GsmDecodeBlock(ctx, samples, ref gb, mode);
        GsmDecTemplate.GsmDecodeBlock(
            ctx, samples.Slice(GsmSizes.GsmFrameSize, GsmSizes.GsmFrameSize), ref gb, mode);
    }
}

/// <summary>The registry entry for plain GSM 06.10, which a raw .gsm file carries.</summary>
[Codec("gsm", CodecType.Audio, DisplayName = "GSM 06.10", Priority = 5)]
public sealed class GsmDecoderAdapter : GsmDecoderAdapterBase
{
    public override string CodecId => "gsm";
    protected override GsmCodecId Variant => GsmCodecId.Gsm;
}

/// <summary>
/// The registry entry for the Microsoft variant, which is WAV format tag 0x31.
/// </summary>
[Codec("gsm_ms", CodecType.Audio, DisplayName = "GSM Microsoft variant", Priority = 5)]
public sealed class GsmMsDecoderAdapter : GsmDecoderAdapterBase
{
    public override string CodecId => "gsm_ms";
    protected override GsmCodecId Variant => GsmCodecId.GsmMs;
}

/// <summary>Shared body for the two GSM adapters.</summary>
public abstract class GsmDecoderAdapterBase : AudioDecoderAdapterBase
{
    private readonly GsmDec decoder = new();
    private short[] scratch = [];
    private int sampleRate = 8000;
    private int channels = 1;
    private int blockAlign;
    private bool configured;

    /// <summary>Which of the two codecs this adapter decodes.</summary>
    protected abstract GsmCodecId Variant { get; }

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override int Channels => channels;

    /// <summary>
    /// GSM needs the block alignment, which is the only thing that says which of the nine
    /// bit rates a Microsoft-variant stream uses. <c>gsm_init</c> defaults the rate to 8000
    /// and the layout to mono, and so does this.
    /// </summary>
    public override void ConfigureAudio(
        int rate, int channelCount, int align = 0, int bits = 0)
    {
        if (rate > 0) sampleRate = rate;
        if (channelCount > 0) channels = channelCount;
        blockAlign = align;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Mono, as in the C: <c>gsm_init</c> overwrites the channel layout with
    /// AV_CHANNEL_LAYOUT_MONO rather than honouring what the container said, because the
    /// synthesis filters' state is per-stream.
    /// </remarks>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (channels != 1) return false;

        decoder.GsmInit(Variant, blockAlign);
        scratch = new short[decoder.FrameSize];
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
        if (data.Length < decoder.BlockAlign) return null;

        int got = decoder.GsmDecodeFrame(data, scratch, out bool magicMissing);
        if (magicMissing) MagicMissingBlocks++;
        if (got <= 0) return null;

        EnsureBuffer(got);
        for (int i = 0; i < got; i++) SampleBuffer[i] = scratch[i] * (1f / 32768f);

        return MakeFrame(got, presentationTimeTicks);
    }

    /// <summary>
    /// How many blocks did not start with plain GSM's 0xd magic. The C logs a warning and
    /// decodes on; this counts them so a test can assert the count is zero rather than
    /// trusting that well-formed output means the stream was read at the right offset.
    /// </summary>
    public int MagicMissingBlocks { get; private set; }

    /// <inheritdoc/>
    public override void Flush()
    {
        decoder.GsmFlush();
        MagicMissingBlocks = 0;
    }
}

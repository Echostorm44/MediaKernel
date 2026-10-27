// One registry entry per PCM codec id, which is the shape pcm.c has: PCM_DECODER declares a
// separate FFCodec for every sample format, all of them sharing pcm_decode_frame. Here each
// class is six lines over a common base and names the same PcmCodecId the C's id names, so a
// container that says pcm_s24be gets a decoder that reads pcm_s24be and nothing else.
//
// The alternative - one "pcm" codec that works out its format later - would have put the format
// somewhere other than the codec id, and the codec id is where every container already puts it.

using MediaKernel.Core.Codecs.Audio;

namespace MediaKernel.Core.Codecs.Audio.Pcm;

/// <summary>
/// Shared body for the PCM adapters: the registry creates one, the pipeline tells it the rate
/// and channel count the container declared, and every frame is a whole number of samples.
/// </summary>
public abstract class PcmDecoderAdapterBase : AudioDecoderAdapterBase
{
    private readonly PcmDecoder decoder = new();
    private int sampleRate;
    private int channels;
    private bool configured;

    /// <summary>Which variant this adapter decodes.</summary>
    protected abstract PcmCodecId Variant { get; }

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override int Channels => channels;

    /// <summary>The bits the coded format carries - 24 for pcm_s24le, not 32.</summary>
    public int CodedBitsPerSample => decoder.BitsPerSample;

    /// <inheritdoc/>
    public override void ConfigureAudio(
        int rate, int channelCount, int blockAlign = 0, int bitsPerCodedSample = 0)
    {
        sampleRate = rate;
        channels = channelCount;
    }

    /// <summary>
    /// Raw PCM has no codec configuration, so there is nothing to read here; what it needs
    /// came through <see cref="ConfigureAudio"/>, and this refuses rather than inventing a
    /// default when the container did not say.
    /// </summary>
    public override bool Initialize(System.ReadOnlySpan<byte> codecPrivate)
    {
        if (sampleRate <= 0 || channels <= 0) return false;
        decoder.Initialize(Variant, sampleRate, channels);
        configured = true;
        return true;
    }

    /// <inheritdoc/>
    public override DecodedAudioFrame? Decode(
        System.ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!configured)
            throw new System.InvalidOperationException(
                $"{CodecId}: Decode before Initialize.");
        if (data.Length == 0) return null;

        int total = decoder.DecodeToFloat(data, out float[] samples);
        if (total == 0) return null;

        EnsureBuffer(total);
        samples.AsSpan(0, total).CopyTo(SampleBuffer);
        return MakeFrame(total / channels, presentationTimeTicks);
    }

    /// <summary>PCM holds nothing back, so a flush has nothing to drop.</summary>
    public override void Flush() { }
}

[Codec("pcm_u8", CodecType.Audio, DisplayName = "PCM unsigned 8-bit", Priority = 5)]
public sealed class PcmU8DecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_u8";
    protected override PcmCodecId Variant => PcmCodecId.U8;
}

[Codec("pcm_s8", CodecType.Audio, DisplayName = "PCM signed 8-bit", Priority = 5)]
public sealed class PcmS8DecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s8";
    protected override PcmCodecId Variant => PcmCodecId.S8;
}

[Codec("pcm_s16le", CodecType.Audio, DisplayName = "PCM signed 16-bit little-endian", Priority = 5)]
public sealed class PcmS16LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s16le";
    protected override PcmCodecId Variant => PcmCodecId.S16Le;
}

[Codec("pcm_s16be", CodecType.Audio, DisplayName = "PCM signed 16-bit big-endian", Priority = 5)]
public sealed class PcmS16BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s16be";
    protected override PcmCodecId Variant => PcmCodecId.S16Be;
}

[Codec("pcm_u16le", CodecType.Audio, DisplayName = "PCM unsigned 16-bit little-endian", Priority = 5)]
public sealed class PcmU16LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_u16le";
    protected override PcmCodecId Variant => PcmCodecId.U16Le;
}

[Codec("pcm_u16be", CodecType.Audio, DisplayName = "PCM unsigned 16-bit big-endian", Priority = 5)]
public sealed class PcmU16BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_u16be";
    protected override PcmCodecId Variant => PcmCodecId.U16Be;
}

[Codec("pcm_s24le", CodecType.Audio, DisplayName = "PCM signed 24-bit little-endian", Priority = 5)]
public sealed class PcmS24LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s24le";
    protected override PcmCodecId Variant => PcmCodecId.S24Le;
}

[Codec("pcm_s24be", CodecType.Audio, DisplayName = "PCM signed 24-bit big-endian", Priority = 5)]
public sealed class PcmS24BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s24be";
    protected override PcmCodecId Variant => PcmCodecId.S24Be;
}

[Codec("pcm_s32le", CodecType.Audio, DisplayName = "PCM signed 32-bit little-endian", Priority = 5)]
public sealed class PcmS32LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s32le";
    protected override PcmCodecId Variant => PcmCodecId.S32Le;
}

[Codec("pcm_s32be", CodecType.Audio, DisplayName = "PCM signed 32-bit big-endian", Priority = 5)]
public sealed class PcmS32BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_s32be";
    protected override PcmCodecId Variant => PcmCodecId.S32Be;
}

[Codec("pcm_f32le", CodecType.Audio, DisplayName = "PCM 32-bit float little-endian", Priority = 5)]
public sealed class PcmF32LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_f32le";
    protected override PcmCodecId Variant => PcmCodecId.F32Le;
}

[Codec("pcm_f32be", CodecType.Audio, DisplayName = "PCM 32-bit float big-endian", Priority = 5)]
public sealed class PcmF32BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_f32be";
    protected override PcmCodecId Variant => PcmCodecId.F32Be;
}

[Codec("pcm_f64le", CodecType.Audio, DisplayName = "PCM 64-bit float little-endian", Priority = 5)]
public sealed class PcmF64LeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_f64le";
    protected override PcmCodecId Variant => PcmCodecId.F64Le;
}

[Codec("pcm_f64be", CodecType.Audio, DisplayName = "PCM 64-bit float big-endian", Priority = 5)]
public sealed class PcmF64BeDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_f64be";
    protected override PcmCodecId Variant => PcmCodecId.F64Be;
}

[Codec("pcm_alaw", CodecType.Audio, DisplayName = "G.711 A-law", Priority = 5)]
public sealed class PcmAlawDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_alaw";
    protected override PcmCodecId Variant => PcmCodecId.Alaw;
}

[Codec("pcm_mulaw", CodecType.Audio, DisplayName = "G.711 mu-law", Priority = 5)]
public sealed class PcmMulawDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_mulaw";
    protected override PcmCodecId Variant => PcmCodecId.Mulaw;
}

[Codec("pcm_vidc", CodecType.Audio, DisplayName = "PCM Archimedes VIDC", Priority = 5)]
public sealed class PcmVidcDecoderAdapter : PcmDecoderAdapterBase
{
    public override string CodecId => "pcm_vidc";
    protected override PcmCodecId Variant => PcmCodecId.Vidc;
}

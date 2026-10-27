// The registry entry for ALAC, over the translation of ffmpeg's alac.c.
//
// AlacDecoder.cs beside this is the older reverse-engineered decoder. It is not registered and
// nothing in the pipeline reaches it; this one is what a .m4a with an alac track decodes
// through, and it is what the alac gate holds to ffmpeg's bytes.

using System;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Alac;

[Codec("alac", CodecType.Audio, DisplayName = "ALAC (Apple Lossless) Software Decoder", Priority = 5)]
public sealed class AlacDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly Alac decoder = new();
    private bool configured;
    private int containerSampleRate;
    private int containerChannels;

    public override string CodecId => "alac";

    /// <summary>
    /// The ALACSpecificConfig carries the rate, and it is the one the decoder must use: a
    /// container's own sample rate field can disagree, and ffmpeg takes the codec's.
    /// </summary>
    public override int SampleRate =>
        configured && decoder.SampleRate > 0 ? decoder.SampleRate : containerSampleRate;

    public override int Channels =>
        configured && decoder.Channels > 0 ? decoder.Channels : containerChannels;

    public override void ConfigureAudio(
        int sampleRate, int channels, int blockAlign = 0, int bitsPerCodedSample = 0)
    {
        containerSampleRate = sampleRate;
        containerChannels = channels;
    }

    /// <summary>
    /// <c>alac_decode_init</c>. The codec private data is the <c>alac</c> atom's payload, and
    /// ALAC cannot be decoded without it - there is no in-band configuration to fall back on.
    /// </summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // What ffmpeg calls ALAC extradata is the whole 36-byte 'alac' atom: four bytes of
        // size, the 'alac' type, four of version and flags, then the fields. Containers hand
        // it over in two shapes, and alac_set_info skips the first twelve bytes whichever it
        // is, so both are normalised to the 36-byte form here rather than making the
        // translation of alac_set_info take an offset.
        //
        //   * 36 bytes with 'alac' at offset 4: the atom as ffmpeg stores it. Used as is.
        //   * 28 bytes: the atom's payload, which is what Mp4Demuxer reads after consuming the
        //     box header, and what Matroska stores in CodecPrivate.
        Span<byte> config = new byte[Alac.ExtradataSize];

        if (codecPrivate.Length >= Alac.ExtradataSize
            && codecPrivate[4] == (byte)'a' && codecPrivate[5] == (byte)'l'
            && codecPrivate[6] == (byte)'a' && codecPrivate[7] == (byte)'c')
        {
            codecPrivate[..Alac.ExtradataSize].CopyTo(config);
        }
        else if (codecPrivate.Length >= Alac.ExtradataSize - 8)
        {
            // The eight bytes left out are a size and a type that alac_set_info never reads.
            codecPrivate[..(Alac.ExtradataSize - 8)].CopyTo(config[8..]);
        }
        else
        {
            return false;
        }

        decoder.Initialize(config);
        configured = true;
        return true;
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!configured)
            throw new InvalidOperationException("ALAC: Decode before Initialize.");
        if (data.Length == 0) return null;

        int samples = decoder.DecodeFrame(data);
        if (samples <= 0) return null;

        int channels = decoder.Channels;
        EnsureBuffer(samples * channels);

        // The C emits planar samples at the codec's own depth; this tree's audio frames are
        // interleaved float, so the planes are interleaved and scaled here. 20- and 24-bit
        // samples were already shifted up into 32 bits by decode_element, which is why they
        // all divide by the same 2^31.
        float scale = decoder.SampleSize == 16 ? 1f / 32768f : 1f / 2147483648f;
        for (int ch = 0; ch < channels; ch++)
        {
            int[] plane = decoder.OutputPlanes[ch];
            for (int i = 0; i < samples; i++)
                SampleBuffer[i * channels + ch] = plane[i] * scale;
        }

        return MakeFrame(samples, presentationTimeTicks);
    }

    /// <summary>Every ALAC frame is independent, so a flush has nothing to drop.</summary>
    public override void Flush() { }
}

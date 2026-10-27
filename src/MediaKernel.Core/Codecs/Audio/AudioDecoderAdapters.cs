// Adapters that expose the audio decoders through IAudioDecoder so DecodePipeline can use them.
//
// The decoders themselves are standalone classes with bespoke APIs (DecodeFrame, DecodePacket,
// DecodeFrameMultiChannel) and were only ever driven directly by their FATE tests. Nothing
// implemented IAudioDecoder, so the generated CodecRegistry held only the four video decoders and
// DecodePipeline.CreateAudioDecoder returned null for every audio track ever opened — every file
// played silently. These adapters are the missing wiring.
//
// Contract note: DecodedAudioFrame carries INTERLEAVED float samples and SampleCount is
// per-channel (MkPlay indexes as sampleIndex * Channels). All the decoders below already emit
// interleaved floats except FLAC, which emits interleaved ints that we scale.

using System;
using MediaKernel.Core.Codecs.AAC;
using MediaKernel.Core.Codecs.AC3;
using MediaKernel.Core.Codecs.Audio.Flac;
using MediaKernel.Core.Codecs.MP3;
using MediaKernel.Core.Codecs.Opus;
using MediaKernel.Core.Codecs.Vorbis;

namespace MediaKernel.Core.Codecs.Audio;

/// <summary>Shared plumbing for the audio decoder adapters.</summary>
public abstract class AudioDecoderAdapterBase : IAudioDecoder
{
    protected float[] SampleBuffer = new float[8192];
    private bool isDisposed;

    // One frame object per adapter, pointed at a window of SampleBuffer by MakeFrame. See the
    // lifetime note on DecodedAudioFrame: the samples are the decoder's own buffer and are valid
    // until the next Decode call, which is the contract AudioFrame.TakeOwnership honours by
    // copying into a pooled buffer.
    private readonly DecodedAudioFrame reusedFrame =
        new(0, 0, 0, 0, ReadOnlyMemory<float>.Empty);

    public abstract string CodecId { get; }
    public bool IsHardwareAccelerated => false;
    public abstract int SampleRate { get; }
    public abstract int Channels { get; }

    /// <summary>Decoded samples are always 32-bit float.</summary>
    public int BitsPerSample => 32;

    public abstract bool Initialize(ReadOnlySpan<byte> codecPrivate);
    public abstract DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks);
    public abstract void Flush();

    /// <summary>
    /// Nothing to drain by default: these decoders are handed whole frames by their container.
    /// The MPEG audio adapter, which frames its own input, overrides it.
    /// </summary>
    public virtual void DrainPendingFrames() { }

    /// <summary>
    /// Nothing to configure by default: these codecs describe themselves. The PCM adapters,
    /// whose bytes are the samples, override it. See IAudioDecoder.ConfigureAudio.
    /// </summary>
    public virtual void ConfigureAudio(
        int sampleRate, int channels, int blockAlign = 0, int bitsPerCodedSample = 0) { }

    /// <summary>Grows the scratch buffer to hold <paramref name="floatCount"/> interleaved samples.</summary>
    protected void EnsureBuffer(int floatCount)
    {
        if (SampleBuffer.Length < floatCount)
            SampleBuffer = new float[Math.Max(floatCount, SampleBuffer.Length * 2)];
    }

    /// <summary>
    /// Presents the first <paramref name="samplesPerChannel"/> interleaved frames of the scratch
    /// buffer as a decoded frame. Returns null for an empty result so callers never emit a
    /// zero-length frame.
    /// </summary>
    /// <remarks>
    /// Nothing is copied and nothing is allocated: the frame is a window onto
    /// <see cref="SampleBuffer"/> and the frame object itself is reused. That is the contract
    /// <see cref="DecodedAudioFrame"/> documents, and it is why
    /// <see cref="MediaKernel.Core.AudioFrame.TakeOwnership"/> copies rather than aliases - the
    /// pipeline pumps the decoder again immediately after handing a frame to its channel, so a
    /// frame that aliased this buffer would be overwritten before its reader saw it.
    /// </remarks>
    protected DecodedAudioFrame? MakeFrame(int samplesPerChannel, long pts)
    {
        int channels = Channels;
        if (samplesPerChannel <= 0 || channels <= 0)
            return null;

        int total = samplesPerChannel * channels;
        if (SampleBuffer.Length < total)
            throw new InvalidOperationException(
                $"{CodecId}: decoded {total} interleaved samples into a buffer of "
              + $"{SampleBuffer.Length}; EnsureBuffer was not called for this frame.");

        reusedFrame.Set(samplesPerChannel, channels, SampleRate, pts,
                        new ReadOnlyMemory<float>(SampleBuffer, 0, total));
        return reusedFrame;
    }

    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }
}

// ── MP3 ──────────────────────────────────────────────────────────────────

[Codec("mp3", CodecType.Audio, DisplayName = "MP3 (MPEG-1/2 Layer III) Software Decoder", Priority = 5)]
public sealed class Mp3DecoderAdapter : AudioDecoderAdapterBase
{
    private readonly Mp3Decoder decoder = new();

    public override string CodecId => "mp3";
    public override int SampleRate => decoder.SampleRate;
    public override int Channels => decoder.ChannelCount;

    /// <summary>MP3 is self-describing; every frame header carries rate and channel mode.</summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        // 1152 samples x 2 channels is the largest MP3 frame.
        EnsureBuffer(1152 * 2);
        if (!decoder.DecodeFrame(data, SampleBuffer, out int samplesPerChannel))
            return null;
        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    public override void Flush() => decoder.Reset();
}

// ── AAC ──────────────────────────────────────────────────────────────────

[Codec("aac", CodecType.Audio, DisplayName = "AAC (LC/Main) Software Decoder", Priority = 5)]
public sealed class AacDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly AacDecoder decoder = new();
    private float[][] channelBuffers = [];
    private int channels;
    private bool configured;

    public override string CodecId => "aac";
    public override int SampleRate => decoder.SampleRate;
    public override int Channels => channels;

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // Matroska stores a bare AudioSpecificConfig. MP4 stores the whole esds box, with the
        // config buried in a DecoderSpecificInfo descriptor — feeding that to the config parser
        // yields nonsense (a 48 kHz stereo file was read as 96 kHz, and the wrong sample-rate
        // index then drove the scale-factor band tables out of bounds). Raw ADTS streams carry
        // no config at all and configure themselves from the first frame header.
        if (codecPrivate.Length == 0)
            return true;

        ReadOnlySpan<byte> asc = TryExtractAudioSpecificConfig(codecPrivate, out var extracted)
            ? extracted
            : codecPrivate;

        var config = AdtsParser.ParseAudioSpecificConfig(asc);
        if (config is null)
            return false;

        decoder.Initialize(config);
        configured = true;
        return true;
    }

    /// <summary>
    /// Walks an MP4 <c>esds</c> box (ES_Descriptor → DecoderConfigDescriptor →
    /// DecoderSpecificInfo) and returns the AudioSpecificConfig payload.
    /// Returns false when the data is not an esds, in which case it is already a bare config.
    /// </summary>
    private static bool TryExtractAudioSpecificConfig(ReadOnlySpan<byte> esds, out ReadOnlySpan<byte> config)
    {
        config = default;

        // esds is a FullBox: 1 byte version + 3 bytes flags, then the ES_Descriptor.
        int pos = 0;
        if (esds.Length >= 5 && esds[0] == 0 && esds[4] == 0x03) pos = 4;
        else if (esds.Length >= 1 && esds[0] == 0x03) pos = 0;
        else return false;

        if (!TryReadDescriptor(esds, ref pos, 0x03, out int esEnd)) return false;

        // ES_ID (2) + flags (1), then optional dependency / URL / OCR fields.
        if (pos + 3 > esEnd) return false;
        byte flags = esds[pos + 2];
        pos += 3;
        if ((flags & 0x80) != 0) pos += 2;                        // streamDependenceFlag
        if ((flags & 0x40) != 0)                                   // URL_Flag
        {
            if (pos >= esEnd) return false;
            pos += 1 + esds[pos];
        }
        if ((flags & 0x20) != 0) pos += 2;                          // OCRstreamFlag

        if (!TryReadDescriptor(esds, ref pos, 0x04, out int dcEnd)) return false;

        // objectTypeIndication(1) + streamType(1) + bufferSizeDB(3) + maxBitrate(4) + avgBitrate(4)
        pos += 13;
        if (pos > dcEnd) return false;

        if (!TryReadDescriptor(esds, ref pos, 0x05, out int dsiEnd)) return false;
        if (dsiEnd > esds.Length || dsiEnd <= pos) return false;

        config = esds[pos..dsiEnd];
        return true;
    }

    /// <summary>
    /// Reads an MPEG-4 descriptor header with the expandable 7-bit length encoding, verifying
    /// the tag. On success <paramref name="pos"/> points at the payload and
    /// <paramref name="end"/> is the exclusive payload end.
    /// </summary>
    private static bool TryReadDescriptor(ReadOnlySpan<byte> data, ref int pos, byte expectedTag, out int end)
    {
        end = 0;
        if (pos >= data.Length || data[pos] != expectedTag) return false;
        pos++;

        int length = 0;
        for (int i = 0; i < 4; i++)
        {
            if (pos >= data.Length) return false;
            byte b = data[pos++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }

        end = pos + length;
        return end <= data.Length;
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        // An ADTS frame carries a header the decoder must not see: DecodeFrameMultiChannel reads
        // raw AAC payload, so leaving the header on made it misparse the first element and return
        // nothing. Containers (MP4, MKV) hand over bare payload and are passed through untouched.
        var payload = data;
        if (AdtsParser.TryParseHeader(data, out var header) && header.IsValid &&
            header.FrameLength <= data.Length)
        {
            if (!configured)
            {
                // Raw ADTS: take the configuration from the first frame we see.
                decoder.InitializeFromAdts(in header);
                configured = true;
            }
            payload = data[header.HeaderSize..header.FrameLength];
        }
        else if (!configured)
        {
            return null;
        }

        EnsureChannelBuffers();
        int decodedChannels = decoder.DecodeFrameMultiChannel(payload, channelBuffers, out int channelCount);
        if (decodedChannels <= 0 || channelCount <= 0)
            return null;

        channels = channelCount;

        // AAC decodes into planar per-channel buffers; interleave for DecodedAudioFrame.
        int samplesPerChannel = decodedChannels;
        EnsureBuffer(samplesPerChannel * channelCount);
        for (int ch = 0; ch < channelCount; ch++)
        {
            var source = channelBuffers[ch];
            for (int i = 0; i < samplesPerChannel; i++)
                SampleBuffer[i * channelCount + ch] = source[i];
        }

        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    private void EnsureChannelBuffers()
    {
        const int MaxChannels = 8;
        const int MaxSamplesPerFrame = 2048; // 1024 long block, doubled for SBR headroom
        if (channelBuffers.Length >= MaxChannels) return;

        channelBuffers = new float[MaxChannels][];
        for (int i = 0; i < MaxChannels; i++)
            channelBuffers[i] = new float[MaxSamplesPerFrame];
    }

    public override void Flush() { /* AacDecoder keeps no cross-frame state we can reset */ }
}

// ── FLAC ─────────────────────────────────────────────────────────────────

[Codec("flac", CodecType.Audio, DisplayName = "FLAC Software Decoder", Priority = 5)]
public sealed class FlacDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly FlacDecoder decoder = new();
    private int[] intBuffer = new int[8192];

    public override string CodecId => "flac";
    public override int SampleRate => (int)(decoder.StreamInfo?.SampleRate ?? 0);
    public override int Channels => decoder.StreamInfo?.ChannelCount ?? 0;

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // Matroska stores the whole "fLaC" header block chain as codec private data.
        if (codecPrivate.Length == 0)
            return true;
        return decoder.ParseStreamHeader(codecPrivate, out _);
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        int channels = Channels;
        if (channels <= 0)
            return null;

        int capacity = 65535 * channels;
        if (intBuffer.Length < capacity)
            intBuffer = new int[capacity];

        if (!decoder.DecodeFrame(data, intBuffer, out int samplesPerChannel, out _))
            return null;
        if (samplesPerChannel <= 0)
            return null;

        // FLAC yields integer samples at the stream's bit depth; scale to normalised float.
        int bitsPerSample = decoder.StreamInfo?.BitsPerSample ?? 16;
        float scale = 1f / (1 << (bitsPerSample - 1));

        int total = samplesPerChannel * channels;
        EnsureBuffer(total);
        for (int i = 0; i < total; i++)
            SampleBuffer[i] = intBuffer[i] * scale;

        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    public override void Flush() { }
}

// ── Vorbis ───────────────────────────────────────────────────────────────

[Codec("vorbis", CodecType.Audio, DisplayName = "Vorbis Software Decoder", Priority = 5)]
public sealed class VorbisDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly VorbisDecoder decoder = new();
    private bool initialized;

    public override string CodecId => "vorbis";
    public override int SampleRate => initialized ? decoder.SampleRate : 0;
    public override int Channels => initialized ? decoder.ChannelCount : 0;

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // Vorbis needs all three header packets. Matroska packs them with Xiph lacing:
        // [count-1][len0 as 255,255,...,rem][len1 ...][ident][comment][setup].
        if (codecPrivate.Length == 0)
            return false;

        if (!TrySplitXiphHeaders(codecPrivate, out var ident, out var comment, out var setup))
            return false;

        decoder.Initialize(ident, comment, setup);
        initialized = true;
        return true;
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!initialized)
            return null;

        var pcm = decoder.DecodePacket(data);
        int channels = Channels;
        if (pcm.Length == 0 || channels <= 0)
            return null;

        int samplesPerChannel = pcm.Length / channels;
        EnsureBuffer(pcm.Length);
        pcm.CopyTo(SampleBuffer);
        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    /// <summary>Splits Xiph-laced codec private data into the three Vorbis header packets.</summary>
    private static bool TrySplitXiphHeaders(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> ident, out ReadOnlySpan<byte> comment, out ReadOnlySpan<byte> setup)
    {
        ident = comment = setup = default;
        if (data.Length < 3 || data[0] != 2)
            return false;

        int pos = 1;
        int len0 = 0, len1 = 0;
        while (pos < data.Length && data[pos] == 255) { len0 += 255; pos++; }
        if (pos >= data.Length) return false;
        len0 += data[pos++];
        while (pos < data.Length && data[pos] == 255) { len1 += 255; pos++; }
        if (pos >= data.Length) return false;
        len1 += data[pos++];

        if (pos + len0 + len1 > data.Length)
            return false;

        ident = data.Slice(pos, len0);
        comment = data.Slice(pos + len0, len1);
        setup = data.Slice(pos + len0 + len1);
        return true;
    }

    public override void Flush() { }
}

// ── Opus ─────────────────────────────────────────────────────────────────

[Codec("opus", CodecType.Audio, DisplayName = "Opus Software Decoder", Priority = 5)]
public sealed class OpusDecoderAdapter : AudioDecoderAdapterBase
{
    private OpusDecoder? decoder;
    private int channels = 2;

    public override string CodecId => "opus";
    public override int SampleRate => decoder?.SampleRate ?? 48000;
    public override int Channels => channels;

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // The OpusHead identification header carries the channel count at byte 9.
        if (codecPrivate.Length >= 10 && codecPrivate[..8].SequenceEqual("OpusHead"u8))
            channels = codecPrivate[9];

        decoder = new OpusDecoder(channels);
        return codecPrivate.Length == 0 || decoder.Initialize(codecPrivate);
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (decoder is null)
            return null;

        // An empty span is the pipeline asking whether anything is still buffered, not a lost
        // packet. The Opus decoder conceals a lost packet by generating one, so without this
        // the drain loop invented 64 frames of its own for every real one - 64,192 frames out
        // of a file holding 1,001.
        if (data.IsEmpty)
            return null;

        // Largest Opus frame is 120 ms at 48 kHz.
        EnsureBuffer(5760 * channels);
        if (!decoder.DecodePacket(data, SampleBuffer, out int samplesPerChannel))
            return null;
        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    public override void Flush() { }
}

// ── AC-3 ─────────────────────────────────────────────────────────────────

[Codec("ac3", CodecType.Audio, DisplayName = "AC-3 Software Decoder", Priority = 5)]
public sealed class Ac3DecoderAdapter : AudioDecoderAdapterBase
{
    private readonly Ac3Decoder decoder = new();

    public override string CodecId => "ac3";
    public override int SampleRate => decoder.SampleRate;
    public override int Channels => decoder.ChannelCount;

    /// <summary>AC-3 is self-describing; the sync frame carries rate and channel layout.</summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        // 6 blocks x 256 samples across up to 6 channels.
        EnsureBuffer(1536 * 6);
        if (!decoder.DecodeFrame(data, SampleBuffer, out int samplesPerChannel))
            return null;
        return MakeFrame(samplesPerChannel, presentationTimeTicks);
    }

    public override void Flush() => decoder.Reset();
}

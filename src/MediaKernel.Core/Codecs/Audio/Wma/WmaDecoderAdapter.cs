// The registry entries that let the pipeline play a WMA stream.

using System;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Wma;

/// <summary>
/// Drives <see cref="WmaDecoder"/> from a container's WAVEFORMATEX.
/// </summary>
/// <remarks>
/// <para>
/// WMA is the one codec here that cannot be configured from a codec-private blob alone:
/// <c>ff_wma_init</c> derives the band layout, the coded range and the coefficient code set from
/// the <i>bit rate</i>, and needs the block alignment to know how much of a packet to read. Both
/// live in the WAVEFORMATEX rather than in the extradata that follows it, so the whole
/// WAVEFORMATEX is what the demuxers hand over as codec-private data - which is also how Matroska
/// stores WMA, in an <c>A_MS/ACM</c> track.
/// </para>
/// <para>
/// The priming samples are dropped here. The decoder emits <see cref="WmaDecoder.Delay"/> samples
/// of overlap-add warm-up that are not audio, and ffmpeg drops the same count through
/// skip_samples; a player that kept them would start every WMA file with a short artefact.
/// </para>
/// </remarks>
public abstract class WmaDecoderAdapterBase : AudioDecoderAdapterBase
{
    private readonly WmaDecoder decoder = new();
    private bool initialized;
    private int samplesToSkip;
    private bool atEndOfStream;
    private bool tailEmitted;

    private int configuredSampleRate;
    private int configuredChannels;
    private int configuredBlockAlign;

    /// <summary>1 for WMAV1 (wave format tag 0x160), 2 for WMAV2 (0x161).</summary>
    protected abstract int Version { get; }

    public override int SampleRate => initialized ? decoder.SampleRate : 0;

    public override int Channels => initialized ? decoder.Channels : 0;

    public override void ConfigureAudio(int sampleRate, int channels, int blockAlign = 0,
                                        int bitsPerCodedSample = 0)
    {
        configuredSampleRate = sampleRate;
        configuredChannels = channels;
        configuredBlockAlign = blockAlign;
    }

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // A WAVEFORMATEX is at least 18 bytes: tag, channels, rate, bytes per second, block
        // align, bits per sample, and the size of what follows.
        if (codecPrivate.Length < 18)
            return false;

        int tag = codecPrivate[0] | (codecPrivate[1] << 8);
        int channels = codecPrivate[2] | (codecPrivate[3] << 8);
        int sampleRate = codecPrivate[4] | (codecPrivate[5] << 8) |
                         (codecPrivate[6] << 16) | (codecPrivate[7] << 24);
        int avgBytesPerSec = codecPrivate[8] | (codecPrivate[9] << 8) |
                             (codecPrivate[10] << 16) | (codecPrivate[11] << 24);
        int blockAlign = codecPrivate[12] | (codecPrivate[13] << 8);
        int extraSize = codecPrivate[16] | (codecPrivate[17] << 8);

        int expectedTag = Version == 1 ? 0x0160 : 0x0161;
        if (tag != expectedTag)
            return false;

        ReadOnlySpan<byte> extradata = codecPrivate.Length >= 18 + extraSize
            ? codecPrivate.Slice(18, extraSize)
            : codecPrivate[18..];

        // The container's track header wins where it disagrees with the WAVEFORMATEX, because a
        // demuxer may have corrected it; where it says nothing, the WAVEFORMATEX stands.
        if (configuredSampleRate > 0) sampleRate = configuredSampleRate;
        if (configuredChannels > 0) channels = configuredChannels;
        if (configuredBlockAlign > 0) blockAlign = configuredBlockAlign;

        if (!decoder.Initialize(Version, sampleRate, channels, avgBytesPerSec * 8L,
                                blockAlign, extradata))
            return false;

        initialized = true;
        samplesToSkip = decoder.Delay;
        return true;
    }

    /// <summary>
    /// The pipeline calls this once, after the last packet, before its final empty
    /// <see cref="Decode"/> call. That is the only signal that distinguishes the end of the
    /// stream from the empty call it makes after every packet.
    /// </summary>
    public override void DrainPendingFrames() => atEndOfStream = true;

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (!initialized)
            return null;

        // An empty payload is not end of stream. The pipeline pumps every audio decoder with one
        // after each packet - "one packet can hold several audio frames... so the decoder is
        // pumped until it has nothing left" - and this decoder never holds frames back: a
        // superframe yields all of its frames in one call. Treating the empty call as end of
        // stream makes the decoder emit its overlap tail after every packet, which is one extra
        // frame per packet and corrupts everything after the first. The real end arrives through
        // DrainPendingFrames, and only then is the tail worth emitting - ffmpeg emits the same
        // 2048 samples there, through AV_CODEC_CAP_DELAY.
        if (data.IsEmpty)
        {
            if (!atEndOfStream || tailEmitted)
                return null;
            tailEmitted = true;
        }

        if (!decoder.DecodeSuperframe(data, out int produced) || produced <= 0)
            return null;

        int channels = decoder.Channels;

        // Drop the priming samples, which are overlap-add warm-up rather than audio.
        int offset = 0;
        if (samplesToSkip > 0)
        {
            int dropped = Math.Min(samplesToSkip, produced);
            samplesToSkip -= dropped;
            offset = dropped;
            produced -= dropped;
            if (produced <= 0)
                return null;
        }

        int total = produced * channels;
        EnsureBuffer(total);
        for (int i = 0; i < produced; i++)
            for (int ch = 0; ch < channels; ch++)
                SampleBuffer[i * channels + ch] = decoder.Samples[ch][offset + i];

        return MakeFrame(produced, presentationTimeTicks);
    }

    public override void Flush()
    {
        if (!initialized)
            return;
        decoder.Flush();
        samplesToSkip = decoder.Delay;
        atEndOfStream = false;
        tailEmitted = false;
    }
}

/// <summary>The registry entry for Windows Media Audio 1.</summary>
[Codec("wmav1", CodecType.Audio, DisplayName = "Windows Media Audio 1 Software Decoder",
       Priority = 5)]
public sealed class WmaV1DecoderAdapter : WmaDecoderAdapterBase
{
    public override string CodecId => "wmav1";

    protected override int Version => 1;
}

/// <summary>The registry entry for Windows Media Audio 2.</summary>
[Codec("wmav2", CodecType.Audio, DisplayName = "Windows Media Audio 2 Software Decoder",
       Priority = 5)]
public sealed class WmaV2DecoderAdapter : WmaDecoderAdapterBase
{
    public override string CodecId => "wmav2";

    protected override int Version => 2;
}

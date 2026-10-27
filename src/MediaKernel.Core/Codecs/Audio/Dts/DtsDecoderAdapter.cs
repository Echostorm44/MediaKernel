// The registry entry that lets the pipeline play a DTS stream.

using System;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary>
/// Drives <see cref="DcaCore"/> from a container's DTS packets.
/// </summary>
/// <remarks>
/// <para>
/// DTS needs no codec-private data: every frame carries its own sample rate, channel
/// arrangement and bit rate in its 18-byte header, which is why a raw <c>.dts</c> file is
/// playable with no container at all. <see cref="Initialize"/> therefore succeeds whatever it is
/// handed, including nothing.
/// </para>
/// <para>
/// There are no priming samples to drop. The QMF history starts at zero here and in ffmpeg, and
/// the two produce the same sample count from the same frames - 1,440,768 floats from 469 frames
/// in the gate - so an offset correction would be a bug rather than a fix.
/// </para>
/// <para>
/// SCOPE: the core substream. A stream carrying XCH, XXCH, X96, XBR, XLL or LBR extension data
/// decodes to its core, which is a complete, correct 5.1 (or less) programme at the core's own
/// bit rate - exactly what ffmpeg -core_only 1 produces, and what the dts-core gate compares
/// against. It is not silently partial: the extensions add channels or lossless refinement on
/// top of a core that stands on its own.
/// </para>
/// </remarks>
[Codec("dts", CodecType.Audio, DisplayName = "DTS Coherent Acoustics Software Decoder",
       Priority = 5)]
public sealed class DtsDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly DcaContext decoder = new();
    private bool initialized;

    /// <summary>
    /// The packet, copied so the decoder has a <c>byte[]</c> with room past the end for the bit
    /// reader's over-read.
    /// </summary>
    private byte[] packetBuffer = [];

    private const int PacketPadding = 64;

    public override string CodecId => "dts";

    public override int SampleRate => decoder.SampleRate;

    public override int Channels => decoder.ChannelRemap.Length;

    public override void ConfigureAudio(int sampleRate, int channels, int blockAlign = 0,
                                        int bitsPerCodedSample = 0)
    {
        // Deliberately ignored: a DTS frame header is authoritative over anything a container
        // claims, and a container that disagrees is wrong rather than a correction.
    }

    public override bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        decoder.Init();
        initialized = true;
        return true;
    }

    public override DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        // An empty payload is not end of stream: the pipeline pumps every audio decoder with one
        // after each packet. This decoder holds nothing back - one packet is one frame - so there
        // is nothing to emit here and nothing to flush at the end either.
        if (!initialized || data.IsEmpty)
            return null;

        int wanted = data.Length + PacketPadding;
        if (packetBuffer.Length < wanted)
            packetBuffer = new byte[wanted];

        Array.Clear(packetBuffer);
        data.CopyTo(packetBuffer);

        // DcaContext does the rest: normalise whichever of the four bitstream layouts this is,
        // parse the core, parse the extension substream header, parse the core extensions out
        // of whichever of the two carries them, and filter.
        if (!decoder.DecodeFrame(packetBuffer, data.Length))
            return null;

        int channels = decoder.ChannelRemap.Length;
        int produced = decoder.Npcmsamples;
        if (channels <= 0 || produced <= 0)
            return null;

        int total = produced * channels;
        EnsureBuffer(total);

        // WHICH PLANES, because the decoder has two sets and the wrong one is not a wrong
        // sample - it is an IndexOutOfRangeException. A packet carrying an XLL asset decodes
        // through the lossless layer, whose channel count and sample count are its own and
        // larger than the core's, and whose output is S32P. Reading the core's FLOAT planes
        // with the XLL layer's remap and sample count is what this did, and it crashed on
        // every .dtshd file in the dcadec suite - files dts-xll decodes losslessly through
        // its own harness, which reads PlaneFixed as ffmpeg's dcadec does.
        //
        // The fixed planes hold the s32 convention: 24 significant bits shifted up by 8, or
        // 16 shifted up by 16, so both fill the 32-bit range and one divisor converts either.
        if (decoder.LastFrameWasFixed)
        {
            const float ToFloat = 1f / 2147483648f;
            for (int ch = 0; ch < channels; ch++)
            {
                ReadOnlySpan<int> plane = decoder.PlaneFixed(ch);
                for (int i = 0; i < produced; i++)
                    SampleBuffer[i * channels + ch] = plane[i] * ToFloat;
            }
        }
        else
        {
            for (int ch = 0; ch < channels; ch++)
            {
                ReadOnlySpan<float> plane = decoder.Plane(ch);
                for (int i = 0; i < produced; i++)
                    SampleBuffer[i * channels + ch] = plane[i];
            }
        }

        return MakeFrame(produced, presentationTimeTicks);
    }

    public override void Flush()
    {
        if (initialized)
            decoder.Flush();
    }
}

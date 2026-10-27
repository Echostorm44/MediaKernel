// Translated from ffmpeg-master/libavcodec/s302m.c - SMPTE 302M, which is how linear PCM rides
// inside an MPEG transport stream. Broadcast contribution feeds and professional capture cards
// emit it, and there is no decoding to it in the usual sense: the samples are already linear,
// just packed with every byte's bits reversed and two samples straddling each group.
//
// It is always 48 kHz, 16, 20 or 24 bits, and 2, 4, 6 or 8 channels.
//
// NON-PCM. A 302M stream can carry an AES3 non-PCM payload - Dolby E, most often - announced by
// a sync word in the first samples. ffmpeg's default non_pcm_mode is decode_drop, which refuses
// such a frame outright; this port does the same and says which data type it found. Handing the
// bits on as though they were audio would produce full-scale noise, which is how this fault
// usually reaches somebody's ears.

using System;
using MediaKernel.Core.Codecs;

namespace MediaKernel.Core.Codecs.Audio.S302m;

/// <summary><c>s302m.c</c>.</summary>
public sealed class S302m
{
    /// <summary><c>AES3_HEADER_LEN</c>.</summary>
    public const int Aes3HeaderLen = 4;

    /// <summary>
    /// <c>ff_reverse</c> of <c>libavutil/reverse.c</c> - each byte with its bits reversed.
    /// </summary>
    /// <remarks>
    /// AES3 sends each subframe least significant bit first, so every byte of the payload is
    /// bit-reversed before it means anything. FFmpeg keeps this as a 256-byte table and so does
    /// this; computing it per byte would be arithmetic in the inner loop for no gain.
    /// </remarks>
    public static readonly byte[] Reverse = BuildReverse();

    private static byte[] BuildReverse()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i;
            v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
            v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
            v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
            t[i] = (byte)v;
        }
        return t;
    }

    /// <summary>Bits each sample carries: 16, 20 or 24.</summary>
    public int BitsPerRawSample { get; private set; }

    /// <summary>Channel count: 2, 4, 6 or 8.</summary>
    public int Channels { get; private set; }

    /// <summary>302M is 48 kHz and nothing else.</summary>
    public int SampleRate => 48000;

    /// <summary>
    /// The AES3 data type of the last frame when it carried non-PCM, or -1. Dolby E is 28.
    /// </summary>
    public int NonPcmDataType { get; private set; } = -1;

    /// <summary>
    /// <c>s302m_parse_frame_header</c>. Returns the frame size the header declares.
    /// </summary>
    /// <remarks>
    /// The header is four bytes: 16 bits of size, 2 of channel count, 8 of channel id, 2 of
    /// sample width and 4 of alignment. The size must account for the whole packet exactly,
    /// which is the only check that a stream is really 302M and not something else on the pid.
    /// </remarks>
    public int ParseFrameHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length <= Aes3HeaderLen)
            throw new InvalidOperationException("S302M: frame is too short.");

        uint h = ((uint)buf[0] << 24) | ((uint)buf[1] << 16) | ((uint)buf[2] << 8) | buf[3];
        int frameSize = (int)((h >> 16) & 0xFFFF);
        int channels = (int)((h >> 14) & 0x0003) * 2 + 2;
        int bits = (int)((h >> 4) & 0x0003) * 4 + 16;

        if (Aes3HeaderLen + frameSize != buf.Length || bits > 24)
            throw new InvalidOperationException(
                $"S302M: frame has invalid header - declared size {frameSize} against a "
              + $"{buf.Length}-byte packet, {bits} bits per sample.");

        BitsPerRawSample = bits;
        Channels = channels;
        return frameSize;
    }

    /// <summary>
    /// <c>s302m_decode_frame</c>. Writes interleaved samples, left-aligned in 32 bits for the
    /// 20- and 24-bit widths and in 16 for the 16-bit one, exactly as the C does.
    /// </summary>
    /// <returns>Samples per channel.</returns>
    public int DecodeFrame(ReadOnlySpan<byte> packet, Span<int> output32, Span<short> output16)
    {
        ParseFrameHeader(packet);
        NonPcmDataType = -1;

        ReadOnlySpan<byte> buf = packet[Aes3HeaderLen..];
        int bufSize = buf.Length;

        // block_size is the bytes one PAIR of samples occupies: 5, 6 or 7.
        int blockSize = (BitsPerRawSample + 4) / 4;
        int channels = Channels;
        int nbSamples = 2 * (bufSize / blockSize) / channels;
        if (nbSamples <= 0) return 0;

        bufSize = nbSamples * channels / 2 * blockSize;

        int o = 0, p = 0;
        if (BitsPerRawSample == 24)
        {
            for (; bufSize > 6; bufSize -= 7)
            {
                output32[o++] = (int)(((uint)Reverse[buf[p + 2]] << 24)
                                    | ((uint)Reverse[buf[p + 1]] << 16)
                                    | ((uint)Reverse[buf[p + 0]] << 8));
                output32[o++] = (int)(((uint)Reverse[buf[p + 6] & 0xF0] << 28)
                                    | ((uint)Reverse[buf[p + 5]] << 20)
                                    | ((uint)Reverse[buf[p + 4]] << 12)
                                    | ((uint)Reverse[buf[p + 3] & 0x0F] << 4));
                p += 7;
            }
            DetectNonPcm32(output32, nbSamples, channels, 0x96F87200U, 0xA54E1F00U);
        }
        else if (BitsPerRawSample == 20)
        {
            for (; bufSize > 5; bufSize -= 6)
            {
                output32[o++] = (int)(((uint)Reverse[buf[p + 2] & 0xF0] << 28)
                                    | ((uint)Reverse[buf[p + 1]] << 20)
                                    | ((uint)Reverse[buf[p + 0]] << 12));
                output32[o++] = (int)(((uint)Reverse[buf[p + 5] & 0xF0] << 28)
                                    | ((uint)Reverse[buf[p + 4]] << 20)
                                    | ((uint)Reverse[buf[p + 3]] << 12));
                p += 6;
            }
            DetectNonPcm32(output32, nbSamples, channels, 0x6F872000U, 0x54E1F000U);
        }
        else
        {
            for (; bufSize > 4; bufSize -= 5)
            {
                output16[o++] = (short)((Reverse[buf[p + 1]] << 8) | Reverse[buf[p + 0]]);
                output16[o++] = (short)((Reverse[buf[p + 4] & 0xF0] << 12)
                                      | (Reverse[buf[p + 3]] << 4)
                                      | (Reverse[buf[p + 2]] >> 4));
                p += 5;
            }
            DetectNonPcm16(output16, nbSamples, channels);
        }

        if (NonPcmDataType != -1)
            throw new NotSupportedException(
                $"S302M: non-PCM payload with AES3 data type {NonPcmDataType}"
              + (NonPcmDataType == 28 ? " (Dolby E)" : "")
              + ". ffmpeg's default non_pcm_mode refuses it, and passing the bits on as audio "
              + "would be full-scale noise.");

        return nbSamples;
    }

    /// <summary>
    /// The non-PCM sync word search of the C: four silent samples followed by the AES3 preamble
    /// Pa Pb, with the data type in the next sample. Only stereo streams are checked, which is
    /// the C's own restriction.
    /// </summary>
    private void DetectNonPcm32(
        ReadOnlySpan<int> o, int nbSamples, int channels, uint pa, uint pb)
    {
        if (channels != 2) return;
        for (int i = 0; i < nbSamples * 2 - 6; i += 2)
        {
            if (o[i] != 0 || o[i + 1] != 0 || o[i + 2] != 0 || o[i + 3] != 0) break;
            if ((uint)o[i + 4] == pa && (uint)o[i + 5] == pb)
            {
                NonPcmDataType = (int)(((uint)o[i + 6] >> 16) & 0x1F);
                break;
            }
        }
    }

    private void DetectNonPcm16(ReadOnlySpan<short> o, int nbSamples, int channels)
    {
        if (channels != 2) return;
        for (int i = 0; i < nbSamples * 2 - 6; i += 2)
        {
            if (o[i] != 0 || o[i + 1] != 0 || o[i + 2] != 0 || o[i + 3] != 0) break;
            if ((ushort)o[i + 4] == 0xF872 && (ushort)o[i + 5] == 0x4E1F)
            {
                NonPcmDataType = o[i + 6] & 0x1F;
                break;
            }
        }
    }
}

/// <summary>The registry entry for SMPTE 302M.</summary>
[Codec("s302m", CodecType.Audio, DisplayName = "SMPTE 302M (AES3 PCM) Decoder", Priority = 5)]
public sealed class S302mDecoderAdapter : AudioDecoderAdapterBase
{
    private readonly S302m decoder = new();
    private int[] scratch32 = new int[8192];
    private short[] scratch16 = new short[8192];

    /// <inheritdoc/>
    public override string CodecId => "s302m";

    /// <summary>302M declares its own rate, and it is always 48 kHz.</summary>
    public override int SampleRate => decoder.SampleRate;

    /// <summary>
    /// The channel count comes from each frame's header, so it is not known until one has been
    /// decoded. The C has AV_CODEC_CAP_CHANNEL_CONF for the same reason.
    /// </summary>
    public override int Channels => decoder.Channels;

    /// <summary>302M carries no out-of-band configuration; every frame describes itself.</summary>
    public override bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    /// <inheritdoc/>
    public override DecodedAudioFrame? Decode(
        ReadOnlySpan<byte> data, long presentationTimeTicks)
    {
        if (data.Length <= S302m.Aes3HeaderLen) return null;

        // Two samples per block of 5, 6 or 7 bytes, so this is a generous bound either way.
        int capacity = data.Length;
        if (scratch32.Length < capacity) scratch32 = new int[capacity];
        if (scratch16.Length < capacity) scratch16 = new short[capacity];

        int samples = decoder.DecodeFrame(data, scratch32, scratch16);
        if (samples <= 0) return null;

        int channels = decoder.Channels;
        int total = samples * channels;
        EnsureBuffer(total);

        // The C leaves 20- and 24-bit samples left-aligned in 32 bits, so both divide by 2^31.
        if (decoder.BitsPerRawSample > 16)
            for (int i = 0; i < total; i++)
                SampleBuffer[i] = scratch32[i] * (1f / 2147483648f);
        else
            for (int i = 0; i < total; i++)
                SampleBuffer[i] = scratch16[i] * (1f / 32768f);

        return MakeFrame(samples, presentationTimeTicks);
    }

    /// <summary>Every 302M frame is independent.</summary>
    public override void Flush() { }
}

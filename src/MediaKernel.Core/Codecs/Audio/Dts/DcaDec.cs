// DTS decoder orchestration - a port of ffmpeg-master/libavcodec/dcadec.c.
//
// One DTS packet is not one thing. It is a core frame, optionally followed by an extension
// substream whose header says which of XBR, XXCH, X96, XLL and LBR it carries and where each
// one sits. Deciding what to parse, in what order, and which component gets to produce the
// output is this file's whole job; the parsing itself lives in DcaCore, DcaExssParser and the
// extension files.
//
// The order matters and is not arbitrary. The core is parsed first because the extensions refine
// it, the EXSS header second because it says where they are, and the core extensions last
// because they need both. The sample a listener hears comes from the highest layer present.

using System;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>enum DCAPacketFlags</c> - which components the last packet turned out to hold.</summary>
internal static class DcaPacket
{
    public const int Core = 0x01;
    public const int Exss = 0x02;
    public const int Xll = 0x04;
    public const int Lbr = 0x08;
    public const int Recovery = 0x10;

    /// <summary>
    /// <c>DCA_PACKET_RESIDUAL</c> - the core of the PREVIOUS packet was filtered through the
    /// fixed point path, so it is usable for residual decoding.
    /// </summary>
    /// <remarks>
    /// This is state about the packet before, not the one in hand, and it is the only thing that
    /// distinguishes "the first XLL frame after a seek" from "the next one in a run". The C uses
    /// it to force one frame of lossy downmixed output on entry, which stops an audible click.
    /// </remarks>
    public const int Residual = 0x20;
}

/// <summary>
/// <c>DCAContext</c> - a whole DTS decoder: the core, the extension substream header, and the
/// decision about which of them produces the samples.
/// </summary>
internal sealed class DcaContext
{
    /// <summary><c>MIN_PACKET_SIZE</c> / <c>MAX_PACKET_SIZE</c>.</summary>
    private const int MinPacketSize = 16;
    private const int MaxPacketSize = 1 << 24;

    private readonly DcaCore core = new();
    private readonly DcaExssParser exss = new();
    private readonly DcaXllDecoder xll = new();

    /// <summary><c>prev_packet</c> - the flags from the packet before this one.</summary>
    private int prevPacket;

    /// <summary>Scratch for <c>avpriv_dca_convert_bitstream</c>.</summary>
    private byte[] buffer = new byte[1 << 16];

    /// <summary><c>s-&gt;packet</c>: the <see cref="DcaPacket"/> flags for the last frame.</summary>
    public int Packet { get; private set; }

    /// <summary>
    /// <c>s-&gt;core_only</c>. Setting it reproduces <c>ffmpeg -core_only 1</c>, which is the
    /// reference the core gate compares against.
    /// </summary>
    public bool CoreOnly { get; set; }

    /// <summary>
    /// <c>s-&gt;request_channel_layout</c>, converted from <c>-downmix</c> the way
    /// <c>dcadec_init</c> converts it: <see cref="DcaSpeakerLayout.Stereo"/>,
    /// <see cref="DcaSpeakerLayout.Layout5Point0"/> or
    /// <see cref="DcaSpeakerLayout.Layout5Point1"/>, or 0 for the stream's own layout.
    /// </summary>
    /// <remarks>
    /// Only the stereo request can actually fold channels together, and then only when the
    /// stream's auxiliary data carries an LoRo or LtRt matrix. The other two values do their
    /// work by suppressing the (X)XCH extensions, which is why asking for 5.1 from a 6.1 stream
    /// gives the core's six channels rather than a mix of its seven.
    /// </remarks>
    public int RequestChannelLayout
    {
        get => core.RequestChannelLayout;
        set
        {
            core.RequestChannelLayout = value;
            xll.RequestChannelLayout = value;
        }
    }

    /// <summary>
    /// <c>AV_CODEC_FLAG_BITEXACT</c>: decode through the fixed point path, as
    /// <c>ffmpeg -bitexact</c> does.
    /// </summary>
    /// <remarks>
    /// This is not a quality switch and the two paths are not a rounding of one another. The
    /// float path is what ffmpeg uses by default and emits <c>fltp</c>; the fixed path emits
    /// <c>s32p</c> with 24 significant bits and is reproducible to the last bit on any machine,
    /// which is the only reason it exists. Read the samples with
    /// <see cref="PlaneFixed"/> when this is set and <see cref="Plane"/> when it is not -
    /// whichever one the last frame did not fill holds the frame before it.
    /// </remarks>
    public bool BitExact { get; set; }

    /// <summary>
    /// True when the last decoded frame came out of the fixed point path, whether because
    /// <see cref="BitExact"/> asked for it or because the packet carried an XLL asset.
    /// </summary>
    public bool LastFrameWasFixed { get; private set; }

    public string LastError { get; private set; } = "";

    /// <summary>
    /// True when the last decoded frame came out of the XLL lossless layer rather than the
    /// core.
    /// </summary>
    public bool LastFrameWasLossless { get; private set; }

    public int SampleRate => LastFrameWasLossless ? xll.SampleRate : core.OutputRate;
    public int Npcmsamples => LastFrameWasLossless ? xll.Npcmsamples : core.Npcmsamples;

    public ReadOnlySpan<int> ChannelRemap =>
        LastFrameWasLossless ? xll.ChannelRemap : core.ChannelRemap;

    public ReadOnlySpan<float> Plane(int index) => core.Plane(index);

    /// <summary>
    /// The storage resolution the last lossless frame declared: 16, 20 or 24 bits. Zero when
    /// the last frame was not lossless.
    /// </summary>
    public int StorageBitRes => LastFrameWasLossless ? xll.StorageBitRes : 0;

    /// <summary>
    /// One decoded plane from the fixed point path, as 32-bit samples. Valid only when
    /// <see cref="LastFrameWasFixed"/> is true.
    /// </summary>
    public ReadOnlySpan<int> PlaneFixed(int index) =>
        LastFrameWasLossless ? xll.PlaneFixed(index) : core.PlaneFixed(index);

    /// <summary><c>dcadec_init</c>.</summary>
    public void Init() => core.Init();

    /// <summary><c>dcadec_flush</c>.</summary>
    public void Flush()
    {
        core.Flush();
        xll.Flush();
        prevPacket = 0;
    }

    /// <summary>
    /// <c>dcadec_decode_frame</c> - decode one packet into PCM planes.
    /// </summary>
    /// <remarks>
    /// Returns false only when nothing decoded. A broken extension substream, or a core
    /// extension that fails to parse, leaves the core's samples in place and reports success:
    /// that is the C's behaviour away from <c>AV_EF_EXPLODE</c>, and it is the right one, since
    /// the alternative is silence where a listener would have heard correct 5.1.
    /// </remarks>
    public bool DecodeFrame(byte[] data, int size)
    {
        LastError = "";
        prevPacket = Packet;
        Packet = 0;
        LastFrameWasLossless = false;

        if (size < MinPacketSize || size > MaxPacketSize)
        {
            LastError = $"invalid packet size ({size})";
            return false;
        }

        byte[] input = data;
        int offset = 0;
        int inputSize = size;

        // Convert the input to big-endian form unless it already is. The four layouts and the
        // search for the one that parses are Dca.ConvertBitstream's business.
        uint mrk = ReadBe32(data, 0);
        if (mrk != DcaSyncwords.CoreBe && mrk != DcaSyncwords.Substream)
        {
            if (buffer.Length < size + 8)
                buffer = new byte[size + 8];

            int converted = -1;
            for (int i = 0; i < size - MinPacketSize + 1 && converted < 0; i++)
                converted = Dca.ConvertBitstream(data.AsSpan(i, size - i), buffer);

            if (converted < 0)
            {
                LastError = "not a valid DTS frame in any of the four bitstream layouts";
                return false;
            }

            input = buffer;
            inputSize = converted;
        }

        // Parse the backward compatible core sub-stream.
        if (ReadBe32(input, offset) == DcaSyncwords.CoreBe)
        {
            if (!core.Parse(input, inputSize))
            {
                LastError = core.LastError;
                return false;
            }

            Packet |= DcaPacket.Core;

            // EXSS data is aligned on a 4-byte boundary after the core frame.
            int frameSize = (core.FrameSize + 3) & ~3;
            if (inputSize - 4 > frameSize)
            {
                offset += frameSize;
                inputSize -= frameSize;
            }
        }

        var asset = default(DcaExssAsset);

        if (!CoreOnly)
        {
            // Parse the extension sub-stream header.
            if (ReadBe32(input, offset) == DcaSyncwords.Substream)
            {
                if (exss.Parse(input, offset, inputSize))
                {
                    Packet |= DcaPacket.Exss;
                    asset = exss.Asset;
                }
                else
                {
                    // Recorded, not fatal: the core's samples are already decoded.
                    LastError = exss.LastError;
                }
            }

            // Parse the XLL component in the EXSS.
            if ((Packet & DcaPacket.Exss) != 0
                && (asset.ExtensionMask & DcaExtensionMask.ExssXll) != 0)
            {
                DcaXllStatus status = xll.Parse(input, offset, asset);
                if (status == DcaXllStatus.Ok)
                {
                    Packet |= DcaPacket.Xll;
                }
                else if (status == DcaXllStatus.Again)
                {
                    // Conceal an XLL synchronisation error. This is NOT a failure: if the
                    // previous packet decoded XLL and this one has a core, the frame is produced
                    // from the core in recovery mode, which is what stops a seek from clicking.
                    // Reporting it as an error here would mute a frame FFmpeg plays.
                    if ((prevPacket & DcaPacket.Xll) != 0 && (Packet & DcaPacket.Core) != 0)
                        Packet |= DcaPacket.Xll | DcaPacket.Recovery;
                    else
                        LastError = xll.LastError;
                }
                else
                {
                    LastError = xll.LastError;
                }
            }

            // LBR would be parsed here. It is not translated and cannot be silently skipped,
            // because an LBR asset means there is no core to fall back on at all.
            if ((asset.ExtensionMask & DcaExtensionMask.ExssLbr) != 0)
                LastError = "the LBR extension (DTS Express) is not translated, and it has no "
                          + "core to fall back on";

            // Parse the core extensions, in the EXSS or in the core sub-stream.
            if ((Packet & DcaPacket.Core) != 0)
                core.ParseCoreExtensions(input, offset, asset);
        }

        // A packet is decodable if ANY layer in it is, which is not the same as "it has a core".
        // DTS-HD Master Audio can be authored with no backward-compatible core at all - the
        // suite's xll_51_24_48_none is exactly that - so requiring one here rejects a stream
        // that decodes perfectly well, and reports it as an unsupported container rather than
        // as the deliberate authoring choice it is.
        if ((Packet & (DcaPacket.Core | DcaPacket.Xll)) == 0)
        {
            LastError = LastError.Length > 0
                ? LastError
                : "no decodable sub-stream in this packet: no core, and no extension decoder "
                + "for what it does carry";
            return false;
        }

        // Filter the frame, in dcadec_decode_frame's order of preference: LBR, then XLL, then
        // the core. The layer that produces the samples is the highest one present, and for XLL
        // the core is still filtered first, because a residual channel is the sum of the two.
        if ((Packet & DcaPacket.Xll) != 0)
        {
            if ((Packet & DcaPacket.Core) != 0)
            {
                // -1, not 0: the core must NOT synthesise X96 of its own accord here. The one
                // case that wants the 64-band filter bank is an XLL stream at 96 kHz over a
                // 48 kHz core, and it is named explicitly. Passing 0 would let the core's own
                // X96 extension turn synthesis on and double a rate XLL did not ask to double.
                int x96Synth = xll.Chset[0].Freq == 96000 && core.SampleRate == 48000 ? 1 : -1;

                if (!core.FilterFixed(x96Synth))
                {
                    LastError = core.LastError;
                    return false;
                }

                // Force lossy downmixed output on the first core frame filtered. This stops an
                // audible click when seeking and matches what the reference decoder does when
                // there are multiple channel sets.
                if ((prevPacket & DcaPacket.Residual) == 0 && xll.Nreschsets > 0
                    && xll.Nchsets > 1)
                {
                    Packet |= DcaPacket.Recovery;
                }

                // Mark the core valid for the NEXT frame's residual decoding.
                Packet |= DcaPacket.Residual;
            }

            if (xll.FilterFrame(core, (Packet & DcaPacket.Core) != 0,
                                (Packet & DcaPacket.Recovery) != 0))
            {
                LastFrameWasLossless = true;
                LastFrameWasFixed = true;
                return true;
            }

            // Fall back to the core unless there is nothing to fall back to.
            if ((Packet & DcaPacket.Core) == 0)
            {
                LastError = xll.LastError;
                return false;
            }
            LastError = xll.LastError;
        }

        // ff_dca_core_filter_frame's choice of path. The fixed one is FORCED when the packet
        // carries an XLL asset, not merely preferred: XLL's residual mode reconstructs the
        // lossless signal by adding its own samples to the core's, which only works if the
        // core's are the integer ones XLL was coded against. The float path would be close and
        // not lossless, which is the whole point of the layer.
        LastFrameWasFixed = BitExact || (asset.ExtensionMask & DcaExtensionMask.ExssXll) != 0;

        if (LastFrameWasFixed)
        {
            if (!core.FilterFrameFixed(skipCoreFilter: (Packet & DcaPacket.Xll) != 0))
            {
                LastError = core.LastError;
                return false;
            }
        }
        else if (!core.FilterFrame())
        {
            LastError = core.LastError;
            return false;
        }
        else if ((core.FilterMode & DcaCore.FilterModeFixedFlag) != 0)
        {
            Packet |= DcaPacket.Residual;
        }

        return true;
    }

    private static uint ReadBe32(byte[] data, int at)
        => at + 4 <= data.Length
            ? (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3])
            : 0;
}

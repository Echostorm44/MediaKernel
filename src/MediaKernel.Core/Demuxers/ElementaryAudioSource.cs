using System;
using System.Buffers.Binary;
using MediaKernel.Core.Demuxers;
using MediaKernel.Core.Codecs.Audio.Flac;
using MediaKernel.Core.Codecs.Audio.Dts;
using System.IO;

// The demuxer and the decoder each declare a FlacStreamInfo, and this file needs both.
using CodecStreamInfo = MediaKernel.Core.Codecs.Audio.Flac.FlacStreamInfo;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// A container-less audio stream: MP3, ADTS AAC or AC-3 bytes with no wrapper around them.
/// Shoutcast and Icecast serve exactly this, so the pipeline needs to treat it as a first-class
/// input rather than a special case.
/// </summary>
/// <remarks>
/// The three demuxers behind this already expose the same shape, but they share no base type.
/// Adapting them here keeps <see cref="DecodePipeline"/> from growing another nullable field and
/// another branch in every method for each new elementary format.
/// </remarks>
public interface IElementaryAudioSource : IDisposable
{
    /// <summary>Codec id as the decoder registry knows it, e.g. "mp3", "aac", "ac3", "eac3".</summary>
    string CodecId { get; }

    int SampleRate { get; }

    int ChannelCount { get; }

    /// <summary>Stream duration, or <see cref="TimeSpan.Zero"/> when it cannot be known - which is
    /// the normal case for a live stream.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Returns the next frame, or null at end of stream.
    /// </summary>
    /// <remarks>
    /// The frame is always whole - unlike the container demuxers this never requires seeking back
    /// into the source, which is what makes it usable on a non-seekable network stream - but it is
    /// not necessarily a copy. See the lifetime note on <see cref="DemuxerPacket.Data"/>: a source
    /// may hand out a window onto its own read buffer, valid until the next call here. This used
    /// to say "the packet owns its payload"; it stopped being true when the MPEG audio, ADTS and
    /// AC-3 sources stopped allocating a fresh array for every frame, and a caller that keeps a
    /// payload has to copy it.
    /// </remarks>
    DemuxerPacket? ReadFrame();

    /// <summary>
    /// Bytes in one coded block, for a format whose decoder cannot work it out: ADPCM carries
    /// its predictor in a block header, so the block size is part of the format and not of the
    /// payload. Zero for everything else.
    /// </summary>
    int BlockAlign => 0;

    /// <summary>Bits per CODED sample, where the container states it. Zero otherwise.</summary>
    int BitsPerCodedSample => 0;

    /// <summary>
    /// Out-of-band configuration the decoder needs before the first packet - a FLAC header
    /// chain, an ALAC magic cookie, an OpusHead. Null for a format that configures itself from
    /// every frame header.
    /// </summary>
    byte[]? CodecPrivate => null;
}

internal sealed class MpegAudioSource(MpegAudioDemuxer demuxer) : IElementaryAudioSource
{
    public string CodecId => demuxer.Codec;
    public int SampleRate => demuxer.SampleRate;
    public int ChannelCount => demuxer.ChannelCount;
    public TimeSpan Duration => demuxer.Duration;
    public DemuxerPacket? ReadFrame() => demuxer.ReadFrame();
    public void Dispose() => demuxer.Dispose();
}

internal sealed class AdtsAudioSource(AacDemuxer demuxer) : IElementaryAudioSource
{
    public string CodecId => demuxer.Codec;
    public int SampleRate => demuxer.SampleRate;
    public int ChannelCount => demuxer.ChannelCount;
    public TimeSpan Duration => demuxer.Duration;
    public DemuxerPacket? ReadFrame() => demuxer.ReadFrame();
    public void Dispose() => demuxer.Dispose();
}

internal sealed class Ac3AudioSource(Ac3Demuxer demuxer) : IElementaryAudioSource
{
    public string CodecId => demuxer.Codec;
    public int SampleRate => demuxer.SampleRate;
    public int ChannelCount => demuxer.ChannelCount;
    public TimeSpan Duration => demuxer.Duration;
    public DemuxerPacket? ReadFrame() => demuxer.ReadFrame();
    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A WAV file as an elementary audio stream: the format chunk says what the samples are, and
/// the data chunk is nothing but samples.
/// </summary>
/// <remarks>
/// <para>
/// The packet size is a choice this class makes and the file does not: raw PCM has no frames.
/// One tenth of a second keeps a packet small enough to stay in cache and large enough that a
/// long file is not a million packets, and it is always a whole number of sample frames because
/// <c>WavDemuxer.ReadSamples</c> aligns to <c>BlockAlign</c>.
/// </para>
/// <para>
/// The codec id is the ffmpeg name for the format - <c>pcm_s16le</c>, <c>pcm_f32le</c>,
/// <c>pcm_alaw</c> - because that is what selects the decoder, and it is derived from the format
/// tag and the bit width rather than assumed. A tag this decoder does not handle fails loudly
/// here instead of being read as 16-bit by default.
/// </para>
/// </remarks>
internal sealed class WavAudioSource : IElementaryAudioSource
{
    private readonly WavDemuxer demuxer;
    private readonly byte[] buffer;
    private readonly int bytesPerFrame;
    private long samplesRead;

    public WavAudioSource(WavDemuxer wav)
    {
        demuxer = wav;
        WavFormat f = wav.Format;

        CodecId = CodecIdFor(f);
        SampleRate = (int)f.SampleRate;
        ChannelCount = f.Channels;

        // For PCM this is one sample frame; for a block-coded stream BlockAlign is the whole
        // block, which is what ReadSamples aligns to and what the decoder expects per packet.
        bytesPerFrame = f.BlockAlign > 0
            ? f.BlockAlign
            : Math.Max(1, f.Channels * (Math.Max(8, (int)f.BitsPerSample) / 8));

        BlockAlign = f.BlockAlign;

        // ff_get_wav_header overrides bits_per_coded_sample for G.726 from the bit rate
        // (riffdec.c), because for G.726 the format chunk's bits-per-sample field is not the
        // code size. The code size is the ONLY thing that says how to read the bitstream -
        // there is no header of any kind in it - so a stream read at the wrong one decodes to
        // noise without ever failing.
        BitsPerCodedSample = CodecId == "adpcm_g726" && SampleRate > 0
            ? (int)(f.BytesPerSecond * 8 / (uint)SampleRate)
            : f.BitsPerSample;

        // A block-based codec must be fed whole blocks, and one block is one packet: ADPCM
        // carries its predictor in the block header, so a packet cut anywhere else decodes to
        // noise. Raw PCM has no such constraint, and a tenth of a second keeps the packet
        // count sensible.
        int packetBytes = (IsBlockCoded ? 1 : Math.Max(1, SampleRate / 10)) * bytesPerFrame;

        // G.726 is the one exception to both halves of that. Its codes run continuously for
        // the length of the stream with no block structure at all - the format chunk says the
        // block alignment is one byte - so a packet may be any size, as long as its BITS are a
        // whole number of codes. A packet that is a multiple of code_size bytes always is.
        if (CodecId == "adpcm_g726" && BitsPerCodedSample > 0)
            packetBytes = Math.Max(1, packetBytes / BitsPerCodedSample) * BitsPerCodedSample;

        buffer = new byte[packetBytes];
    }

    /// <summary>
    /// True when one packet must be exactly one coded block. G.726 is named here as the
    /// exception it is: it is an `adpcm_` codec with no blocks, and feeding it one byte at a
    /// time would work but would make a second of audio four thousand packets.
    /// </summary>
    private bool IsBlockCoded =>
        (CodecId.StartsWith("adpcm", StringComparison.Ordinal) && CodecId != "adpcm_g726")
        || CodecId is "gsm" or "gsm_ms";

    public string CodecId { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    /// <summary>
    /// <c>BlockAlign</c> from the format chunk. A block-based codec needs it and cannot work
    /// it out; raw PCM ignores it.
    /// </summary>
    public int BlockAlign { get; }

    /// <summary><c>BitsPerSample</c> from the format chunk - the CODED width.</summary>
    public int BitsPerCodedSample { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(demuxer.DurationSeconds);

    /// <summary>
    /// The ffmpeg codec name for a WAV format chunk: <c>ff_wav_codec_get_id</c> over
    /// <c>ff_codec_wav_tags</c>, which is what <c>ff_get_wav_header</c> does.
    /// </summary>
    /// <remarks>
    /// What this replaces was a hand-written list of eight tags that threw for the rest -
    /// the third such list found in this tree, after AVI's and ASF's. The table has 84 rows,
    /// so a WAV holding GSM or Truespeech or any of the forty-odd ADPCM variants is now
    /// named correctly and finds no decoder, instead of refusing to open.
    ///
    /// WAVE_FORMAT_EXTENSIBLE is handled the way <c>parse_waveformatex</c> handles it, and
    /// that matters: <c>ff_get_wav_header</c> deliberately does not look tag 0xFFFE up in
    /// the table, because there is a row for DFPWM on that tag and it would win. The real
    /// format is in the subformat GUID, and a GUID whose last twelve bytes are one of the
    /// three base GUIDs wraps a wave format tag in its first four.
    /// </remarks>
    public static string CodecIdFor(WavFormat f)
    {
        uint tag = (ushort)f.FormatTag;

        if (f.FormatTag == WavFormatTag.Extensible)
        {
            Span<byte> guid = stackalloc byte[16];
            if (!f.SubFormat.TryWriteBytes(guid))
                throw new NotSupportedException("WAV: could not read the subformat GUID.");

            if (RiffTags.IsFormatTagGuid(guid))
            {
                tag = BinaryPrimitives.ReadUInt32LittleEndian(guid);
            }
            else
            {
                return RiffTags.CodecGuidGetId(RiffTags.CodecWavGuids, guid)
                    ?? throw new NotSupportedException(
                        $"WAV: WAVEFORMATEXTENSIBLE subformat {f.SubFormat} is in no table "
                      + "in ffmpeg, so there is no codec to name.");
            }
        }

        return RiffTags.WavCodecGetId(tag, f.BitsPerSample)
            ?? throw new NotSupportedException(
                $"WAV: format tag 0x{tag:X4} is in ff_codec_wav_tags nowhere, so there is no "
              + "codec to name. Reading it as PCM would produce noise rather than an error.");
    }

    public DemuxerPacket? ReadFrame()
    {
        int read = demuxer.ReadSamples(buffer);
        if (read <= 0) return null;

        // For a block-coded stream bytesPerFrame is a whole block, so this counts blocks and
        // the timestamps below are in blocks rather than samples. They stay monotonic, and a
        // packet's own sample count comes from the decoder.
        long frames = read / bytesPerFrame;
        long ptsTicks = SampleRate > 0
            ? samplesRead * TimeSpan.TicksPerSecond / SampleRate
            : 0;
        long durationTicks = SampleRate > 0
            ? frames * TimeSpan.TicksPerSecond / SampleRate
            : 0;
        samplesRead += frames;

        // A WINDOW onto the read buffer, not a copy. The interface's contract changed when
        // the MPEG audio, ADTS and AC-3 sources stopped allocating per frame - see the
        // lifetime note on DemuxerPacket.Data - and this one was left behind, so every
        // packet of every WAV allocated its own array. The no-alloc gate measured 824 bytes
        // a read on a G.726 WAV and 96 on a GSM one, which is the packet size each time.
        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, new ReadOnlyMemory<byte>(buffer, 0, read));
    }

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// An AIFF or AIFF-C file as an elementary audio stream. AIFF samples are big-endian, which is
/// the whole reason <c>pcm_s16be</c> exists.
/// </summary>
internal sealed class AiffAudioSource : IElementaryAudioSource
{
    private readonly AiffDemuxer demuxer;
    private readonly byte[] buffer;
    private readonly int bytesPerFrame;
    private long samplesRead;

    public AiffAudioSource(AiffDemuxer aiff, AiffInfo info)
    {
        demuxer = aiff;
        CodecId = CodecIdFor(info);
        SampleRate = info.SampleRate;
        ChannelCount = info.NumChannels;

        bytesPerFrame = Math.Max(1, info.NumChannels * ((info.SampleSize + 7) / 8));
        int framesPerPacket = Math.Max(1, SampleRate / 10);
        buffer = new byte[framesPerPacket * bytesPerFrame];
    }

    public string CodecId { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public TimeSpan Duration => SampleRate > 0
        ? TimeSpan.FromSeconds((double)demuxer.Info.NumSampleFrames / SampleRate)
        : TimeSpan.Zero;

    /// <summary>
    /// The ffmpeg codec name for an AIFF COMM chunk. AIFF-C names its compression in a four
    /// character code; the uncompressed ones are NONE and sowt, and sowt is the same samples
    /// the other way round, which is the one case where an AIFF is little-endian.
    /// </summary>
    public static string CodecIdFor(AiffInfo info)
    {
        string compression = (info.CompressionType ?? "NONE").Trim();
        if (compression.Length == 0) compression = "NONE";

        switch (compression)
        {
            case "NONE":
            case "twos":
                return info.SampleSize switch
                {
                    8 => "pcm_s8",
                    16 => "pcm_s16be",
                    24 => "pcm_s24be",
                    32 => "pcm_s32be",
                    _ => throw new NotSupportedException(
                        $"AIFF: {info.SampleSize}-bit NONE is not a width pcm.c decodes."),
                };

            case "sowt":
                return info.SampleSize switch
                {
                    8 => "pcm_s8",
                    16 => "pcm_s16le",
                    24 => "pcm_s24le",
                    32 => "pcm_s32le",
                    _ => throw new NotSupportedException(
                        $"AIFF: {info.SampleSize}-bit sowt is not a width pcm.c decodes."),
                };

            case "fl32":
            case "FL32":
                return "pcm_f32be";

            case "fl64":
            case "FL64":
                return "pcm_f64be";

            case "alaw":
            case "ALAW":
                return "pcm_alaw";

            case "ulaw":
            case "ULAW":
                return "pcm_mulaw";

            default:
                throw new NotSupportedException(
                    $"AIFF: compression type '{compression}' is not PCM. Reading it as PCM "
                  + "would produce noise rather than an error.");
        }
    }

    public DemuxerPacket? ReadFrame()
    {
        int read = demuxer.ReadSamples(buffer);
        if (read <= 0) return null;

        read -= read % bytesPerFrame;
        if (read <= 0) return null;

        long frames = read / bytesPerFrame;
        long ptsTicks = SampleRate > 0 ? samplesRead * TimeSpan.TicksPerSecond / SampleRate : 0;
        long durationTicks = SampleRate > 0 ? frames * TimeSpan.TicksPerSecond / SampleRate : 0;
        samplesRead += frames;

        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsSpan(0, read).ToArray());
    }

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A Sun/NeXT AU file as an elementary audio stream. Everything in it is big-endian, and the
/// format was G.711's first home: mu-law is encoding 1 and A-law 27.
/// </summary>
internal sealed class AuAudioSource : IElementaryAudioSource
{
    private readonly AuDemuxer demuxer;
    private readonly byte[] buffer;
    private readonly int bytesPerFrame;
    private long samplesRead;

    public AuAudioSource(AuDemuxer au)
    {
        demuxer = au;
        AuFileHeader h = au.Header;

        CodecId = CodecIdFor(h.Encoding);
        SampleRate = (int)h.SampleRate;
        ChannelCount = (int)h.ChannelCount;

        bytesPerFrame = Math.Max(1, h.BytesPerFrame);
        int framesPerPacket = Math.Max(1, SampleRate / 10);
        buffer = new byte[framesPerPacket * bytesPerFrame];
    }

    public string CodecId { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(demuxer.Duration);

    /// <summary>
    /// The ffmpeg codec name for an AU encoding type. The ADPCM and G.722 encodings are listed
    /// in AuEncodingType and are not PCM; they throw here rather than being read as linear,
    /// which would be noise.
    /// </summary>
    public static string CodecIdFor(AuEncodingType encoding) => encoding switch
    {
        AuEncodingType.MuLaw8 => "pcm_mulaw",
        AuEncodingType.ALaw8 => "pcm_alaw",
        AuEncodingType.Linear8 => "pcm_s8",
        AuEncodingType.Linear16 => "pcm_s16be",
        AuEncodingType.Linear24 => "pcm_s24be",
        AuEncodingType.Linear32 => "pcm_s32be",
        AuEncodingType.Float32 => "pcm_f32be",
        AuEncodingType.Float64 => "pcm_f64be",
        _ => throw new NotSupportedException(
            $"AU: encoding {encoding} is not PCM. G.721, G.722 and G.723 need an ADPCM "
          + "decoder, and reading one as linear PCM would produce noise rather than an error."),
    };

    public DemuxerPacket? ReadFrame()
    {
        int read = demuxer.ReadSamples(buffer);
        if (read <= 0) return null;

        long frames = read / bytesPerFrame;
        long ptsTicks = SampleRate > 0 ? samplesRead * TimeSpan.TicksPerSecond / SampleRate : 0;
        long durationTicks = SampleRate > 0 ? frames * TimeSpan.TicksPerSecond / SampleRate : 0;
        samplesRead += frames;

        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsSpan(0, read).ToArray());
    }

    public void Dispose() => demuxer.Dispose();
}

/// <summary>
/// A native FLAC file as an elementary audio stream.
/// </summary>
/// <remarks>
/// <para>
/// A FLAC stream carries no frame index and no frame-length field, so finding frame boundaries is
/// the whole job; <see cref="FlacParser"/> does it, and this class only moves bytes between the
/// stream and the parser. The old <c>FlacDemuxer.TryReadFrame</c> is not used: it cuts the stream
/// at the next byte pair that looks like a sync code, and on these very test files 116 byte pairs
/// look like one where there are 91 frames.
/// </para>
/// <para>
/// The codec private data is the file's own header chain, from "fLaC" to the first frame, because
/// that is what <c>FlacDecoderAdapter.Initialize</c> parses - the same bytes Matroska stores in
/// CodecPrivate. Read from the file rather than rebuilt from the parsed STREAMINFO, so that a
/// serialisation bug here cannot misreport the stream to the decoder.
/// </para>
/// </remarks>
internal sealed class FlacAudioSource : IElementaryAudioSource
{
    private readonly Stream stream;
    private readonly FlacStreamInfo streamInfo;
    private readonly FlacParser parser;
    private readonly byte[] readBuffer = new byte[16 * 1024];
    private long samplesEmitted;
    private bool endOfStream;

    public FlacAudioSource(Stream stream, FlacFile file)
    {
        this.stream = stream;
        streamInfo = file.StreamInfo;
        parser = new FlacParser(ToCodecStreamInfo(streamInfo));

        // The header chain, for the decoder. The stream is positioned at the first frame.
        CodecPrivate = new byte[file.AudioDataOffset];
        long resume = stream.Position;
        stream.Position = 0;
        stream.ReadExactly(CodecPrivate, 0, CodecPrivate.Length);
        stream.Position = resume;
    }

    /// <summary>"fLaC" plus the metadata blocks, as Matroska would store them.</summary>
    public byte[] CodecPrivate { get; }

    /// <summary>
    /// The demuxer's STREAMINFO as the decoder's, which is the shape
    /// <see cref="FlacDecoder.ParseFrameHeader"/> takes. The parser needs only the three fields a
    /// frame header can omit - sample rate, block size and sample size - but passing the whole
    /// block keeps the two in step if that changes.
    /// </summary>
    private static CodecStreamInfo ToCodecStreamInfo(in FlacStreamInfo info) => new(
        (ushort)info.MinBlockSize,
        (ushort)info.MaxBlockSize,
        (uint)info.MinFrameSize,
        (uint)info.MaxFrameSize,
        (uint)info.SampleRate,
        (byte)info.Channels,
        (byte)info.BitsPerSample,
        (ulong)info.TotalSamples,
        info.Md5Signature ?? []);

    public string CodecId => "flac";

    public int SampleRate => (int)streamInfo.SampleRate;

    public int ChannelCount => streamInfo.Channels;

    public TimeSpan Duration => streamInfo.Duration;

    public DemuxerPacket? ReadFrame()
    {
        while (true)
        {
            // Anything the parser can already prove, before reading more.
            if (TryTakeFrame(out var ready))
                return ready;

            if (endOfStream)
            {
                while (parser.Flush(out var tail))
                {
                    if (parser.CurrentFrameIsJunk)
                        continue;
                    return MakePacket(tail);
                }
                return null;
            }

            int read = stream.ReadAtLeast(readBuffer, readBuffer.Length, throwOnEndOfStream: false);
            if (read <= 0)
            {
                endOfStream = true;
                continue;
            }

            pending = readBuffer.AsMemory(0, read);
            pendingOffset = 0;
        }
    }

    private Memory<byte> pending;
    private int pendingOffset;

    /// <summary>Where a frame is copied out of the parser; see <see cref="MakePacket"/>.</summary>
    private byte[] frameCopy = new byte[1 << 14];

    /// <summary>
    /// Push what is left of the last read into the parser until it yields a frame or runs out.
    /// </summary>
    /// <remarks>
    /// A false return from <see cref="FlacParser.Parse"/> does not mean there is no frame: the
    /// parser buffers, decides, and hands the frame over on the following call. The loop therefore
    /// ends only when the parser stops taking input.
    /// </remarks>
    private bool TryTakeFrame(out DemuxerPacket? packet)
    {
        packet = null;
        while (pendingOffset < pending.Length)
        {
            bool got = parser.Parse(pending.Span[pendingOffset..], out int consumed, out var frame);
            pendingOffset += consumed;

            if (got)
            {
                if (parser.CurrentFrameIsJunk)
                    continue;
                packet = MakePacket(frame);
                return true;
            }

            if (consumed == 0)
                return false;
        }
        return false;
    }

    private DemuxerPacket MakePacket(ReadOnlyMemory<byte> frame)
    {
        long blockSize = parser.CurrentFrameHeader.BlockSize;
        long ptsTicks = SampleRate > 0 ? samplesEmitted * TimeSpan.TicksPerSecond / SampleRate : 0;
        long durationTicks = SampleRate > 0 ? blockSize * TimeSpan.TicksPerSecond / SampleRate : 0;
        samplesEmitted += blockSize;

        // The parser's memory is only valid until its next call, so the frame is copied out -
        // into a buffer this source reuses, not a fresh array. That is the lifetime
        // DemuxerPacket.Data documents: valid until the next ReadFrame here. A fresh array cost
        // about 11 KB per frame, which on a FLAC stream is the largest single allocation on the
        // decode thread.
        if (frameCopy.Length < frame.Length)
            frameCopy = new byte[Math.Max(frame.Length, frameCopy.Length * 2)];
        frame.Span.CopyTo(frameCopy);

        return new DemuxerPacket(0, ptsTicks, ptsTicks, durationTicks, true,
                                 frameCopy.AsMemory(0, frame.Length));
    }

    public void Dispose() { }
}

/// <summary>
/// A raw DTS elementary stream: nothing but core frames, each carrying its own length.
/// </summary>
/// <remarks>
/// <para>
/// Far simpler than <see cref="FlacAudioSource"/>, and for one reason: a DTS core frame header
/// states its own byte size, so finding the next frame is addition rather than the CRC-scored
/// search <c>flac_parser.c</c> needs. The sync word is still checked at every candidate position
/// so that a stream which starts mid-frame resynchronises instead of cutting every frame at the
/// wrong place.
/// </para>
/// <para>
/// Only the big-endian 16-bit layout is handled here, which is what the probe detects.
/// <see cref="MediaKernel.Core.Codecs.Audio.Dts.Dca.ConvertBitstream"/> in the decoder handles
/// all four, so a little-endian or 14-bit frame arriving from a container still decodes; it is
/// only the raw-file case that is not probed for.
/// </para>
/// </remarks>
internal sealed class DtsAudioSource : IElementaryAudioSource
{
    private readonly Stream stream;
    private byte[] buffer = new byte[1 << 16];
    private int length;
    private int position;
    private bool endOfStream;
    private long samplesEmitted;
    private long bytesRemaining;

    private int sampleRate;
    private int channelCount;

    /// <param name="byteLimit">
    /// How many bytes from the stream's current position belong to this stream. A raw <c>.dts</c>
    /// file runs to the end, but a DTS-HD container's elementary data is one chunk inside a file
    /// whose later chunks contain encoded frames of their own, so reading past the chunk decodes
    /// bytes that are not part of the programme.
    /// </param>
    public DtsAudioSource(Stream stream, long byteLimit = long.MaxValue)
    {
        this.stream = stream;
        bytesRemaining = byteLimit;

        // Read the first frame's header so the track can be described before anything decodes.
        // The position is left where it was, because ReadFrame starts from the beginning.
        Fill();
        int at = FindSync(out bool isExss);
        if (at >= 0 && !isExss && TryDescribe(at))
        {
            // Described from the core header; nothing consumed.
        }
        else if (at >= 0 && isExss)
        {
            // No core to describe the track with. The sample rate comes out of the XLL common
            // header instead, which is also where the frame duration comes from, and the
            // channel count comes from the container - a raw core-less .dts has no way to
            // state it, which is why a .dtshd carries AUPR-HDR.
            ExssXllSamplesAt(at, Math.Min(ExssFrameSizeAt(at), length - at));
        }
    }

    public string CodecId => "dts";

    public int SampleRate => sampleRate;

    public int ChannelCount => channelCount;

    /// <summary>
    /// Not known. A raw DTS file has no header and no index, and the frame count is only
    /// discoverable by walking the whole file - which is what
    /// <see cref="TimeSpan.Zero"/> means to every caller of this interface.
    /// </summary>
    public TimeSpan Duration => TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        while (true)
        {
            int at = FindSync(out bool isExss);
            if (at >= 0)
            {
                // An EXSS sync word with no core in front of it: the substream is the whole
                // frame, and its declared size is the frame size.
                int frameSize = isExss ? ExssFrameSizeAt(at) : FrameSizeAt(at);

                if (isExss && at + 10 > length && !endOfStream)
                {
                    // Not enough bytes to read the substream's own length yet.
                    position = at;
                    Fill();
                    continue;
                }

                // A DTS-HD frame is the core frame AND the extension substream that follows it,
                // delivered as one packet - that is what dca_parser.c's frame-end rule produces
                // and what the decoder expects, because the extensions refine the core's own
                // subbands. Splitting them hands the decoder a core frame it decodes at core
                // quality and then an orphan substream it cannot use.
                if (frameSize > 0 && !isExss)
                {
                    int exssAt = (at + frameSize + 3) & ~3;
                    if (exssAt + 10 <= length)
                    {
                        if (IsExssSyncAt(exssAt))
                        {
                            int exssSize = ExssFrameSizeAt(exssAt);
                            if (exssSize > 0)
                                frameSize = exssAt - at + exssSize;
                        }
                    }
                    else if (!endOfStream)
                    {
                        // Not enough bytes to tell yet. Read more rather than guess.
                        position = at;
                        Fill();
                        continue;
                    }
                }

                if (frameSize > 0 && at + frameSize <= length)
                {
                    position = at + frameSize;

                    if (sampleRate == 0 && !isExss)
                        TryDescribe(at);

                    int samples = isExss
                        ? ExssXllSamplesAt(at, frameSize)
                        : SamplesAt(at);
                    if (samples < 0)
                        samples = 0;
                    long ptsTicks = sampleRate > 0
                        ? samplesEmitted * TimeSpan.TicksPerSecond / sampleRate : 0;
                    long durationTicks = sampleRate > 0
                        ? (long)samples * TimeSpan.TicksPerSecond / sampleRate : 0;
                    samplesEmitted += samples;

                    return new DemuxerPacket(0, ptsTicks, ptsTicks, durationTicks, true,
                                             buffer.AsSpan(at, frameSize).ToArray());
                }

                // A frame the buffer cannot hold yet. Keep it and read more.
                position = at;
            }
            else
            {
                // Nothing usable; keep the last three bytes in case a sync word straddles.
                position = Math.Max(0, length - 3);
            }

            if (endOfStream)
                return null;

            Fill();
        }
    }

    /// <summary>Compact what is left and read more on top of it.</summary>
    private void Fill()
    {
        if (position > 0)
        {
            Array.Copy(buffer, position, buffer, 0, length - position);
            length -= position;
            position = 0;
        }

        if (length == buffer.Length)
            Array.Resize(ref buffer, buffer.Length * 2);

        int want = (int)Math.Min(buffer.Length - length, bytesRemaining);
        if (want <= 0)
        {
            endOfStream = true;
            return;
        }

        int read = stream.Read(buffer, length, want);
        if (read <= 0)
        {
            endOfStream = true;
        }
        else
        {
            length += read;
            bytesRemaining -= read;
        }
    }

    /// <summary>The next big-endian core sync word at or after <see cref="position"/>, or -1.</summary>
    /// <summary>
    /// The next frame start, which is EITHER a core sync word or an extension substream one.
    /// </summary>
    /// <remarks>
    /// <c>dca_parser.c</c>'s <c>IS_MARKER</c> is <c>IS_CORE_MARKER || IS_EXSS_MARKER</c>, and
    /// the second half is not redundant: a DTS-HD Master Audio stream authored with no
    /// backward-compatible core - <c>xll_51_24_48_none</c> in FFmpeg's own suite - contains no
    /// core sync word anywhere, so looking only for that one finds no frames at all and reports
    /// an empty track rather than an error.
    /// </remarks>
    private int FindSync(out bool isExss)
    {
        for (int at = position; at + DcaLimits.CoreFrameHeaderSize <= length; at++)
        {
            if (buffer[at] == 0x7F && buffer[at + 1] == 0xFE
                && buffer[at + 2] == 0x80 && buffer[at + 3] == 0x01)
            {
                isExss = false;
                return at;
            }
            if (IsExssSyncAt(at))
            {
                isExss = true;
                return at;
            }
        }
        isExss = false;
        return -1;
    }

    /// <summary>
    /// The sample count of an extension substream frame that carries XLL and no core.
    /// </summary>
    /// <remarks>
    /// <c>dca_parse_params</c>'s XLL branch, which reads the sample count straight out of the
    /// XLL common header rather than from an asset field: 32 bits of sync word, 4 of version, 8
    /// of header size, then the frame-size field whose WIDTH is itself the next five bits plus
    /// one, 4 bits of channel set count, and then the two four-bit logs whose SUM is
    /// log2(samples per frame). Returns -1 when the header is not there or not XLL.
    /// <para>
    /// The doubling above 96 kHz is the second frequency band: the header counts samples per
    /// band, and two bands interleave to twice as many.
    /// </para>
    /// </remarks>
    private int ExssXllSamplesAt(int at, int frameSize)
    {
        var reader = default(MediaKernel.Core.Codecs.MpegVideo.GetBitContext);
        int window = Math.Min(frameSize, length - at);
        if (window < 16)
            return -1;

        byte[] bytes = buffer.AsSpan(at, window).ToArray();
        var exss = new DcaExssParser();
        if (!exss.Parse(bytes, 0, window))
            return -1;
        if ((exss.Asset.ExtensionMask & DcaExtensionMask.ExssXll) == 0)
            return -1;

        int xllAt = exss.Asset.XllOffset;
        if (xllAt < 0 || xllAt + 8 > window)
            return -1;

        reader.InitGetBits8(bytes, xllAt, Math.Min(exss.Asset.XllSize, window - xllAt));
        if (reader.GetBits(32) != DcaSyncwords.Xll)
            return -1;
        if (reader.GetBits(4) != 0)
            return -1;

        reader.SkipBits(8);
        reader.SkipBits((int)reader.GetBits(5) + 1);
        reader.SkipBits(4);
        int nsamplesLog2 = (int)reader.GetBits(4) + (int)reader.GetBits(4);
        if (nsamplesLog2 > 24)
            return -1;

        int rate = exss.Asset.MaxSampleRate;
        if (rate > 0)
            sampleRate = rate;

        return (1 + (rate > 96000 ? 1 : 0)) << nsamplesLog2;
    }

    /// <summary>
    /// frame_size from the header: 14 bits starting at bit 46, plus one.
    /// </summary>
    private int FrameSizeAt(int at)
    {
        int size = ((buffer[at + 5] & 0x03) << 12 | buffer[at + 6] << 4 | buffer[at + 7] >> 4) + 1;
        return size >= 96 ? size : -1;
    }

    /// <summary>True when the extension substream sync word sits at this offset.</summary>
    private bool IsExssSyncAt(int at)
        => buffer[at] == 0x64 && buffer[at + 1] == 0x58
        && buffer[at + 2] == 0x20 && buffer[at + 3] == 0x25;

    /// <summary>
    /// <c>EXSS_FRAMESIZE</c> from dca_parser.c: the byte length an extension substream declares
    /// for itself.
    /// </summary>
    /// <remarks>
    /// Counting the header out from the sync word: 32 bits of sync, 8 user defined, 2 of
    /// substream index, then a flag saying whether the two length fields are the short or the
    /// long form. Short is an 8-bit header length and a 16-bit substream size; long is 12 and
    /// 20. So the field this wants starts at bit 51 or bit 55 and the widest case ends at bit
    /// 74, which is why the caller needs ten bytes in hand before asking.
    /// </remarks>
    private int ExssFrameSizeAt(int at)
    {
        bool wideHdr = (buffer[at + 5] & 0x20) != 0;
        int bitPos = 43 + 8 + 4 * (wideHdr ? 1 : 0);
        int nbits = 16 + 4 * (wideHdr ? 1 : 0);

        int value = 0;
        for (int i = 0; i < nbits; i++)
        {
            int bit = bitPos + i;
            value = value << 1 | (buffer[at + (bit >> 3)] >> (7 - (bit & 7))) & 1;
        }
        return value + 1;
    }

    /// <summary>npcmblocks * 32: the PCM samples per channel this frame decodes to.</summary>
    private int SamplesAt(int at)
    {
        int npcmblocks = ((buffer[at + 4] & 0x01) << 6 | buffer[at + 5] >> 2) + 1;
        return npcmblocks * DcaLimits.PcmBlockSamples;
    }

    private bool TryDescribe(int at)
    {
        var header = default(DcaCoreFrameHeader);
        var reader = default(MediaKernel.Core.Codecs.MpegVideo.GetBitContext);
        byte[] window = buffer.AsSpan(at, Math.Min(32, length - at)).ToArray();
        reader.InitGetBits8(window, window.Length);

        if (Dca.ParseCoreFrameHeader(ref header, ref reader) != DcaParseError.None)
            return false;

        sampleRate = (int)DcaData.SampleRates[header.SrCode];

        // The channel count comes from the audio mode plus the LFE flag, which is what the core
        // decoder derives its speaker mask from.
        channelCount = DcaData.Channels[header.AudioMode] + (header.LfePresent != 0 ? 1 : 0);
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// A CAF file as an elementary audio stream. <see cref="CafDec"/> does the reading; this turns
/// its packets into the pipeline's, and its frame counts into timestamps.
/// </summary>
/// <remarks>
/// The priming and remainder the packet table declares are reported rather than applied. CAF
/// states an encoder delay the same way an MP4 edit list does, and nothing in this pipeline
/// trims decoded samples yet - MP4's skip is read by its own caller too. Reporting it here at
/// least puts the number where a caller can reach it instead of losing it in the demuxer.
/// </remarks>
internal sealed class CafAudioSource : IElementaryAudioSource
{
    private readonly CafDec caf;
    private byte[] buffer = new byte[4096];

    public CafAudioSource(CafDec caf) => this.caf = caf;

    public string CodecId => caf.CodecId;

    public int SampleRate => caf.SampleRate;

    public int ChannelCount => caf.ChannelCount;

    public int BlockAlign => caf.BlockAlign;

    public int BitsPerCodedSample => caf.BitsPerCodedSample;

    public byte[]? CodecPrivate => caf.Extradata;

    /// <summary>Encoder delay in sample frames, zero when the file declares none.</summary>
    public int InitialPadding => caf.InitialPadding;

    /// <summary>Padding in sample frames at the end of the last packet.</summary>
    public uint TrailingPadding => caf.TrailingPadding;

    public TimeSpan Duration => caf.SampleRate > 0 && caf.StreamDuration > 0
        ? TimeSpan.FromSeconds(caf.StreamDuration / (double)caf.SampleRate)
        : TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        if (!caf.ReadPacket(ref buffer, out CafPacketInfo info))
            return null;

        // The packet table counts in sample frames and a negative timestamp is how CAF says
        // "this much of the first packet is priming". The pipeline's timestamps are ticks and
        // start at zero, so the priming is where the clock starts.
        long startFrame = Math.Max(0, info.Pts);
        long ptsTicks = caf.SampleRate > 0
            ? startFrame * TimeSpan.TicksPerSecond / caf.SampleRate
            : 0;
        long durationTicks = caf.SampleRate > 0
            ? info.Frames * TimeSpan.TicksPerSecond / caf.SampleRate
            : 0;

        // A window onto the reused buffer, which the interface allows: valid until the next
        // call here. Copying would be an allocation on every packet.
        //
        // info.Remainder is the 'pakt' chunk's trailing padding, which is only ever set on
        // the last packet. A block-coded format decodes whole blocks, so without this the
        // pipeline emits the encoder's padding past the end of the file - 60 samples on a
        // two-second IMA ADPCM CAF, which ffmpeg trims and nothing here did.
        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsMemory(0, info.Size),
            endTrimSamples: (int)info.Remainder);
    }

    public void Dispose() { }
}

/// <summary>
/// A VOC file as an elementary audio stream.
/// </summary>
/// <remarks>
/// A VOC states its sample rate, channel count and codec in its first data block rather than in
/// a header - ffmpeg sets AVFMTCTX_NOHEADER on this format for exactly that reason - so the
/// first packet is read here in the constructor and held until the first ReadFrame asks for it.
/// Everything this interface exposes about the track is only knowable afterwards.
/// </remarks>
internal sealed class VocAudioSource : IElementaryAudioSource
{
    private readonly VocDec voc;
    private byte[] buffer = new byte[2048];
    private VocPacketInfo first;
    private bool firstPending;

    public VocAudioSource(VocDec voc)
    {
        this.voc = voc;
        firstPending = voc.ReadPacket(ref buffer, out first);
    }

    public string CodecId => voc.CodecId;

    public int SampleRate => voc.SampleRate;

    public int ChannelCount => voc.ChannelCount;

    public int BitsPerCodedSample => voc.BitsPerCodedSample;

    /// <summary>
    /// Not known. A VOC is a chain of blocks with no total length, and the last one may declare
    /// no size at all, so the duration costs a walk of the whole file.
    /// </summary>
    public TimeSpan Duration => TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        VocPacketInfo info;
        if (firstPending)
        {
            firstPending = false;
            info = first;
        }
        else if (!voc.ReadPacket(ref buffer, out info))
        {
            return null;
        }

        // A packet of a codec whose frame duration ffmpeg cannot work out carries NOPTS, and
        // the pipeline's timestamps are ticks from zero. Clamping to zero there would claim a
        // timestamp the file does not have, so such a packet is given the only honest answer
        // available to a monotonic clock: the previous one's.
        long ptsTicks = info.Pts != VocDec.NoPts && voc.SampleRate > 0
            ? info.Pts * TimeSpan.TicksPerSecond / voc.SampleRate
            : lastPtsTicks;
        long durationTicks = voc.SampleRate > 0
            ? info.Duration * TimeSpan.TicksPerSecond / voc.SampleRate
            : 0;
        lastPtsTicks = ptsTicks;

        // A window onto the reused buffer, valid until the next call, as the interface allows.
        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsMemory(0, info.Size));
    }

    private long lastPtsTicks;

    public void Dispose() { }
}

/// <summary>
/// A Maxis XA file as an elementary audio stream. One packet is one ADPCM block, which is what
/// the decoder has to be fed: the block carries its own coefficients and shift, and the
/// predictor runs on across blocks, so a packet cut anywhere else decodes to noise.
/// </summary>
internal sealed class XaAudioSource : IElementaryAudioSource
{
    private readonly XaDec xa;
    private readonly byte[] buffer;
    private long samplesEmitted;

    public XaAudioSource(XaDec xa)
    {
        this.xa = xa;
        buffer = new byte[xa.BlockAlign];
    }

    public string CodecId => xa.CodecId;

    public int SampleRate => xa.SampleRate;

    public int ChannelCount => xa.ChannelCount;

    /// <summary>
    /// The block size, which the ADPCM decoder needs and the file does not state - the
    /// header's own block-align field is one of the three xa_read_header skips.
    /// </summary>
    public int BlockAlign => xa.BlockAlign;

    /// <summary>Four, which is what EA ADPCM codes a sample in.</summary>
    public int BitsPerCodedSample => 4;

    /// <summary>
    /// From the decoded size in the header, which is a count of OUTPUT bytes: sixteen-bit
    /// samples, one per channel.
    /// </summary>
    /// <remarks>
    /// It used to read that figure as a count of input blocks and multiply by the 28 frames
    /// each one decodes to, which made <c>SC2KBUG.XA</c> 211 seconds long where ffprobe
    /// measures 56.6 - and 4,991,624 output bytes is exactly 1,247,906 stereo frames at
    /// sixteen bits, which is 56.6 seconds at 22050 Hz.
    /// </remarks>
    public TimeSpan Duration => xa.SampleRate > 0 && xa.ChannelCount > 0
        ? TimeSpan.FromSeconds(xa.OutSize / (2.0 * xa.ChannelCount) / xa.SampleRate)
        : TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        if (!xa.ReadPacket(buffer, out int size, out int duration))
            return null;

        long ptsTicks = xa.SampleRate > 0
            ? samplesEmitted * TimeSpan.TicksPerSecond / xa.SampleRate
            : 0;
        long durationTicks = xa.SampleRate > 0
            ? duration * TimeSpan.TicksPerSecond / xa.SampleRate
            : 0;
        samplesEmitted += duration;

        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsMemory(0, size));
    }

    public void Dispose() { }
}

/// <summary>
/// A TTA file as an elementary audio stream. One packet is one frame, and its length comes
/// from the seek table rather than from the frame itself.
/// </summary>
internal sealed class TtaAudioSource : IElementaryAudioSource
{
    private readonly TtaDec tta;
    private byte[] buffer = new byte[1 << 16];

    public TtaAudioSource(TtaDec tta) => this.tta = tta;

    public string CodecId => tta.CodecId;

    public int SampleRate => tta.SampleRate;

    public int ChannelCount => tta.ChannelCount;

    public int BitsPerCodedSample => tta.BitsPerCodedSample;

    /// <summary>
    /// The 22-byte TTA1 header. The decoder reads the format, channel count, bit depth, rate
    /// and total length out of it and refuses a stream without it, exactly as ffmpeg's does -
    /// a TTA frame carries none of that.
    /// </summary>
    public byte[]? CodecPrivate => tta.Extradata;

    public TimeSpan Duration => tta.SampleRate > 0
        ? TimeSpan.FromSeconds(tta.NbSamples / (double)tta.SampleRate)
        : TimeSpan.Zero;

    public DemuxerPacket? ReadFrame()
    {
        if (!tta.ReadPacket(ref buffer, out int size, out long dts, out int duration))
            return null;

        long ptsTicks = tta.SampleRate > 0
            ? dts * TimeSpan.TicksPerSecond / tta.SampleRate
            : 0;
        long durationTicks = tta.SampleRate > 0
            ? duration * TimeSpan.TicksPerSecond / tta.SampleRate
            : 0;

        return new DemuxerPacket(
            0, ptsTicks, ptsTicks, durationTicks, true, buffer.AsMemory(0, size));
    }

    public void Dispose() { }
}

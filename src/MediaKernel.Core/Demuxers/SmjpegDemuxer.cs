// Ported from FFmpeg's libavformat/smjpegdec.c and the tag tables of smjpeg.c - the Loki SDL
// Motion JPEG container, extension .mjpg.
//
// It is the simplest container in this project: an eight-byte magic, a version, a duration,
// then a run of length-prefixed header chunks terminated by HEND, then a run of data chunks
// terminated by DONE. There is no index, no chunk nesting and no per-stream time base - both
// streams are milliseconds.
//
// One thing about it is genuinely odd and is faithfully copied: the chunk TYPES are read
// LITTLE-endian while every length, size and timestamp inside them is read BIG-endian. The C
// does this because a type is four ASCII bytes compared against MKTAG, which packs them in
// reverse; reading a type big-endian compares against nothing and the file looks corrupt
// immediately, which is at least a loud failure.

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>One data chunk of an SMJPEG file.</summary>
public readonly struct SmjpegPacket
{
    /// <summary>The payload, without the twelve-byte chunk header.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The stream this chunk belongs to, 0 for the first declared.</summary>
    public int StreamIndex { get; init; }

    /// <summary><c>pkt-&gt;pts</c>, in milliseconds - the container's only time base.</summary>
    public uint Pts { get; init; }

    /// <summary><c>pkt-&gt;pos</c>, the offset of the chunk header in the file.</summary>
    public long Position { get; init; }
}

/// <summary>A stream SMJPEG's header declares.</summary>
public readonly struct SmjpegStream
{
    /// <summary>The index <see cref="SmjpegPacket.StreamIndex"/> uses.</summary>
    public int Index { get; init; }

    /// <summary>The codec id this project's registry knows the stream by.</summary>
    public string CodecId { get; init; }

    /// <summary>The four-character tag the header carried, little-endian as the C reads it.</summary>
    public uint CodecTag { get; init; }

    /// <summary>The declared duration, in milliseconds. The same for every stream.</summary>
    public uint DurationMs { get; init; }
}

/// <summary>
/// <c>ff_smjpeg_demuxer</c> (<c>smjpegdec.c</c>) - the Loki SDL Motion JPEG container.
/// </summary>
public sealed class SmjpegDemuxer : IDisposable
{
    /// <summary><c>SMJPEG_MAGIC</c>, which is <c>"\0\nSMJPEG"</c>.</summary>
    private static ReadOnlySpan<byte> Magic => [0x00, 0x0A, 0x53, 0x4D, 0x4A, 0x50, 0x45, 0x47];

    // The header and data chunk types, little-endian as MKTAG packs them and as the C reads
    // them. SMJPEG_DONE, _HEND, _SND, _SNDD, _TXT, _VID, _VIDD.
    private const uint Done = 0x454E4F44;   // 'D','O','N','E'
    private const uint Hend = 0x444E4548;   // 'H','E','N','D'
    private const uint Snd = 0x444E535F;    // '_','S','N','D'
    private const uint Sndd = 0x44646E73;   // 's','n','d','D'
    private const uint Txt = 0x5458545F;    // '_','T','X','T'
    private const uint Vid = 0x4449565F;    // '_','V','I','D'
    private const uint Vidd = 0x44646976;   // 'v','i','d','D'

    // ff_codec_smjpeg_video_tags and ff_codec_smjpeg_audio_tags. Three entries between them,
    // so a table would be longer than the switches that read it.
    private const uint TagJfif = 0x4649464A; // 'J','F','I','F'
    private const uint TagApcm = 0x4D435041; // 'A','P','C','M'
    private const uint TagNone = 0x454E4F4E; // 'N','O','N','E'

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private byte[] packetBuffer = [];
    private bool headerRead;
    private bool finished;

    private int audioStreamIndex = -1;
    private int videoStreamIndex = -1;
    private int streamCount;

    /// <summary>Opens a demuxer over an SMJPEG file.</summary>
    public SmjpegDemuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>The audio stream, when the header declared one.</summary>
    public SmjpegStream? Audio { get; private set; }

    /// <summary>The video stream, when the header declared one.</summary>
    public SmjpegStream? Video { get; private set; }

    /// <summary><c>codecpar-&gt;sample_rate</c> of the audio stream.</summary>
    public int SampleRate { get; private set; }

    /// <summary><c>codecpar-&gt;bits_per_coded_sample</c>.</summary>
    public int BitsPerCodedSample { get; private set; }

    /// <summary><c>codecpar-&gt;ch_layout.nb_channels</c>.</summary>
    public int Channels { get; private set; }

    /// <summary><c>codecpar-&gt;width</c> of the video stream.</summary>
    public int Width { get; private set; }

    /// <summary><c>codecpar-&gt;height</c>.</summary>
    public int Height { get; private set; }

    /// <summary><c>st-&gt;nb_frames</c>, which the header states rather than the index implying.</summary>
    public uint FrameCount { get; private set; }

    /// <summary>The whole file's duration in milliseconds, from the fixed header.</summary>
    public uint DurationMs { get; private set; }

    /// <summary>The <c>_TXT</c> chunk, which the C stores as the "comment" metadata key.</summary>
    public string? Comment { get; private set; }

    /// <summary><c>smjpeg_probe</c> - the magic and nothing else, as the C has it.</summary>
    public static bool Probe(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Magic);

    /// <summary>
    /// <c>smjpeg_read_header</c> - the fixed header, then header chunks until <c>HEND</c>.
    /// </summary>
    /// <remarks>
    /// Every length check the C makes is kept. They matter here more than they look: a short
    /// <c>_SND</c> or <c>_VID</c> would otherwise have its missing fields read out of the next
    /// chunk, and because the loop then resynchronises on whatever byte it lands on, the file
    /// would be reported as having an unknown header rather than a truncated one.
    /// </remarks>
    /// <returns>False when the file is not an SMJPEG, or its header is malformed.</returns>
    public bool ReadHeader()
    {
        if (headerRead)
            return Audio is not null || Video is not null;
        headerRead = true;

        Span<byte> buf = stackalloc byte[12];

        if (!ReadExactly(buf[..8]) || !buf[..8].SequenceEqual(Magic))
            return false;

        if (!ReadExactly(buf[..8]))
            return false;

        // The C calls avpriv_request_sample on a nonzero version and carries on. There is no
        // second version of this format and no sample of one, so an unknown version is an
        // unknown layout and is refused rather than guessed at.
        uint version = BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (version != 0)
            return false;

        DurationMs = BinaryPrimitives.ReadUInt32BigEndian(buf[4..]);

        while (true)
        {
            if (!ReadExactly(buf[..4]))
                return false;                       // avio_feof: the C returns AVERROR_EOF

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buf);

            switch (type)
            {
                case Txt:
                {
                    if (!ReadExactly(buf[..4]))
                        return false;
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(buf);
                    if (length == 0 || length > 512)
                        return false;

                    byte[] comment = new byte[length];
                    if (!ReadExactly(comment))
                        return false;
                    Comment = System.Text.Encoding.UTF8.GetString(comment);
                    break;
                }

                case Snd:
                {
                    if (Audio is not null)
                        return false;               // "Multiple audio streams", AVERROR_PATCHWELCOME
                    if (!ReadExactly(buf[..4]))
                        return false;
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(buf);
                    if (length < 8)
                        return false;

                    if (!ReadExactly(buf[..8]))
                        return false;
                    SampleRate = BinaryPrimitives.ReadUInt16BigEndian(buf);
                    BitsPerCodedSample = buf[2];
                    Channels = buf[3];
                    uint tag = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);

                    audioStreamIndex = streamCount++;
                    Audio = new SmjpegStream
                    {
                        Index = audioStreamIndex,
                        CodecId = AudioCodecId(tag),
                        CodecTag = tag,
                        DurationMs = DurationMs,
                    };

                    if (!Skip(length - 8))
                        return false;
                    break;
                }

                case Vid:
                {
                    if (Video is not null)
                        return false;               // "Multiple video streams"
                    if (!ReadExactly(buf[..4]))
                        return false;
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(buf);
                    if (length < 12)
                        return false;

                    if (!ReadExactly(buf[..12]))
                        return false;
                    FrameCount = BinaryPrimitives.ReadUInt32BigEndian(buf);
                    Width = BinaryPrimitives.ReadUInt16BigEndian(buf[4..]);
                    Height = BinaryPrimitives.ReadUInt16BigEndian(buf[6..]);
                    uint tag = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);

                    videoStreamIndex = streamCount++;
                    Video = new SmjpegStream
                    {
                        Index = videoStreamIndex,
                        CodecId = VideoCodecId(tag),
                        CodecTag = tag,
                        DurationMs = DurationMs,
                    };

                    if (!Skip(length - 12))
                        return false;
                    break;
                }

                case Hend:
                    return Audio is not null || Video is not null;

                default:
                    // "unknown header %x" - the C refuses rather than skipping, because
                    // without a length it has no idea where the chunk ends.
                    return false;
            }
        }
    }

    /// <summary>
    /// <c>smjpeg_read_packet</c> - one data chunk.
    /// </summary>
    /// <returns>False at <c>DONE</c>, at end of file, or on an unknown chunk.</returns>
    public bool ReadPacket(out SmjpegPacket packet)
    {
        packet = default;
        if (finished)
            return false;
        if (!headerRead && !ReadHeader())
            return false;

        Span<byte> buf = stackalloc byte[8];
        long position = stream.Position;

        if (!ReadExactly(buf[..4]))
        {
            finished = true;
            return false;
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        int index;

        switch (type)
        {
            case Sndd:
                if (audioStreamIndex < 0) { finished = true; return false; }
                index = audioStreamIndex;
                break;

            case Vidd:
                if (videoStreamIndex < 0) { finished = true; return false; }
                index = videoStreamIndex;
                break;

            case Done:
                finished = true;
                return false;

            default:
                // "unknown chunk %x", AVERROR_INVALIDDATA. There is no length to skip by, so
                // the only honest thing is to stop.
                finished = true;
                return false;
        }

        if (!ReadExactly(buf[..8]))
        {
            finished = true;
            return false;
        }

        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(buf);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(buf[4..]);

        // Geometric, for the reason the THP demuxer carries the same comment: an
        // exact-size buffer reallocates on every chunk bigger than the last, which over a
        // whole file is a per-packet allocation wearing a disguise.
        if (packetBuffer.Length < size)
            packetBuffer = new byte[Math.Max((int)size, packetBuffer.Length * 2)];

        // av_get_packet returns the short count on a truncated read and the caller still gets
        // the bytes; a chunk whose size runs off the end of the file is the end of the file.
        int read = ReadUpTo(packetBuffer.AsSpan(0, (int)size));
        if (read <= 0)
        {
            finished = true;
            return false;
        }

        packet = new SmjpegPacket
        {
            Data = packetBuffer.AsMemory(0, read),
            StreamIndex = index,
            Pts = timestamp,
            Position = position,
        };
        return true;
    }

    /// <summary><c>ff_codec_smjpeg_video_tags</c>.</summary>
    private static string VideoCodecId(uint tag) => tag switch
    {
        TagJfif => "mjpeg",
        _ => throw new NotSupportedException(
            $"SMJPEG: video tag 0x{tag:X8} ('{TagText(tag)}'). ff_codec_smjpeg_video_tags has "
          + "one entry, JFIF, so anything else is a file this format's own table does not "
          + "describe."),
    };

    /// <summary><c>ff_codec_smjpeg_audio_tags</c>.</summary>
    private static string AudioCodecId(uint tag) => tag switch
    {
        TagApcm => "adpcm_ima_smjpeg",
        TagNone => "pcm_s16le",
        _ => throw new NotSupportedException(
            $"SMJPEG: audio tag 0x{tag:X8} ('{TagText(tag)}'). ff_codec_smjpeg_audio_tags has "
          + "two entries, APCM and NONE."),
    };

    private static string TagText(uint tag) => new(
    [
        (char)(byte)tag, (char)(byte)(tag >> 8), (char)(byte)(tag >> 16), (char)(byte)(tag >> 24),
    ]);

    private bool ReadExactly(Span<byte> destination) =>
        ReadUpTo(destination) == destination.Length;

    private int ReadUpTo(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private bool Skip(uint count)
    {
        if (count == 0)
            return true;
        if (stream.CanSeek)
        {
            long target = stream.Position + count;
            if (target > stream.Length)
                return false;
            stream.Position = target;
            return true;
        }

        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, (uint)scratch.Length);
            if (!ReadExactly(scratch[..chunk]))
                return false;
            count -= (uint)chunk;
        }
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        packetBuffer = [];
        if (!leaveOpen)
            stream.Dispose();
    }
}

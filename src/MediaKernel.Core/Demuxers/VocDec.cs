// MediaKernel - Creative Voice File (VOC) demuxer.
//
// A translation of ffmpeg-master/libavformat/vocdec.c and voc_packet.c, with the tag table and
// magic from voc.c. The two libavcodec helpers voc_packet.c leans on - av_get_bits_per_sample
// and av_get_audio_frame_duration2 - are translated here for the codecs a VOC can hold, with a
// throwing default, rather than as the whole of libavcodec/utils.c.
//
// A VOC has no header worth the name: the file header says only its own size and version, and
// everything about the audio arrives in the first data block, which is why ffmpeg sets
// AVFMTCTX_NOHEADER on it. A block can also change the format mid-file, and the C only warns.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>The block types a VOC file is made of - voc.h's <c>VocType</c>.</summary>
public enum VocType
{
    Eof = 0x00,
    VoiceData = 0x01,
    VoiceDataCont = 0x02,
    Silence = 0x03,
    Marker = 0x04,
    Ascii = 0x05,
    RepetitionStart = 0x06,
    RepetitionEnd = 0x07,
    Extended = 0x08,
    NewVoiceData = 0x09,
}

/// <summary>
/// Creative's Voice File. Blocks of audio with a one-byte type and a 24-bit size, where the
/// format is announced by the blocks rather than by a header.
/// </summary>
public sealed class VocDec
{
    /// <summary>The C's AV_NOPTS_VALUE.</summary>
    public const long NoPts = long.MinValue;

    /// <summary>The C's AVPROBE_SCORE_MAX.</summary>
    public const int ProbeScoreMax = 100;

    /// <summary>voc.c's <c>ff_voc_magic</c>, without its terminating NUL.</summary>
    private static ReadOnlySpan<byte> VocMagic => "Creative Voice File\x1A"u8;

    private readonly Stream stream;

    // ── VocDecContext ───────────────────────────────────────────────────
    private long remainingSize;
    private long pts;

    // ── the AVStream fields ff_voc_get_packet sets ──────────────────────
    private int sampleRate;
    private int channels = 1;
    private int bitsPerCodedSample;

    // av_add_index_entry, called at the top of every ff_voc_get_packet. read_seek is the only
    // reader, and it wants the position, the timestamp and the size.
    private readonly List<(long Pos, long Timestamp, long Size)> indexEntries = [];

    private bool eof;

    public VocDec(Stream input) => stream = input;

    /// <summary>
    /// The codec name as ffmpeg knows it, empty until the first data block has been read -
    /// a VOC states its format in the stream, not in the header.
    /// </summary>
    public string CodecId { get; private set; } = "";

    public int SampleRate => sampleRate;

    public int ChannelCount => channels;

    public int BitsPerCodedSample => bitsPerCodedSample;

    public long BitRate { get; private set; }

    // ── probe ───────────────────────────────────────────────────────────

    /// <summary>
    /// vocdec.c's <c>voc_probe</c>. The checksum over the version number is what separates a
    /// certain match from a merely plausible one, so this returns 100 or 10, as the C does.
    /// </summary>
    public static int Probe(ReadOnlySpan<byte> p)
    {
        if (p.Length < 26)
            return 0;
        if (!p[..VocMagic.Length].SequenceEqual(VocMagic))
            return 0;
        int version = BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
        int check = BinaryPrimitives.ReadUInt16LittleEndian(p[24..]);
        if ((~version & 0xFFFF) + 0x1234 != check)
            return 10;

        return ProbeScoreMax;
    }

    // ── header ──────────────────────────────────────────────────────────

    /// <summary>vocdec.c's <c>voc_read_header</c>.</summary>
    public void ReadHeader()
    {
        Skip(20);
        int headerSize = Rl16() - 22;
        if (headerSize != 4)
            throw new InvalidDataException($"VOC: unknown header size: {headerSize}.");
        Skip(headerSize);

        remainingSize = 0;
    }

    // ── packets ─────────────────────────────────────────────────────────

    /// <summary>
    /// voc_packet.c's <c>ff_voc_get_packet</c>, which is also vocdec.c's
    /// <c>voc_read_packet</c> - that function does nothing but create the stream on the first
    /// call, and here the stream is this object. Returns false at end of file.
    /// </summary>
    /// <remarks>
    /// The C's av_get_packet allocates per packet; here the caller owns the buffer, as it does
    /// for every other demuxer in this tree.
    /// </remarks>
    public bool ReadPacket(ref byte[] buffer, out VocPacketInfo info)
    {
        info = default;

        int maxSize = 0;
        int tmpCodec = -1;
        int blockSampleRate = 0;
        int blockChannels = 1;

        indexEntries.Add((Tell(), pts, remainingSize));

        while (remainingSize == 0)
        {
            if (maxSize < 4)
                maxSize = 0;
            var type = (VocType)R8();
            if (eof || type == VocType.Eof)
                return false;
            remainingSize = Rl24();
            if (remainingSize == 0)
            {
                long filesize = stream.Length;
                if (filesize - Tell() > int.MaxValue)
                    throw new InvalidDataException("VOC: a block with no size declares more "
                                                 + "than 2 GB of audio.");
                remainingSize = filesize - Tell();
            }
            maxSize -= 4;

            switch (type)
            {
                case VocType.VoiceData:
                    if (remainingSize < 2)
                    {
                        remainingSize = 0;
                        throw new InvalidDataException("VOC: a voice data block is shorter than "
                                                     + "its own two-byte format field.");
                    }
                    if (sampleRate == 0)
                    {
                        sampleRate = 1000000 / (256 - R8());
                        if (blockSampleRate != 0)
                            sampleRate = blockSampleRate;
                        channels = blockChannels;
                        // Zero on the first block of a type-1 file, exactly as in the C: the
                        // codec id is still unset here because the tag is two bytes further on,
                        // and this arm runs only while the sample rate is unknown, so it never
                        // runs again. ffprobe reports the same zero.
                        bitsPerCodedSample = GetBitsPerSample(CodecId);
                    }
                    else
                    {
                        Skip(1);
                    }
                    tmpCodec = R8();
                    remainingSize -= 2;
                    maxSize -= 2;
                    blockChannels = 1;
                    break;

                case VocType.VoiceDataCont:
                    break;

                case VocType.Extended:
                    blockSampleRate = Rl16();
                    R8();
                    blockChannels = R8() + 1;
                    blockSampleRate = 256000000 / (blockChannels * (65536 - blockSampleRate));
                    remainingSize = 0;
                    maxSize -= 4;
                    break;

                case VocType.NewVoiceData:
                    if (remainingSize < 12)
                    {
                        remainingSize = 0;
                        throw new InvalidDataException("VOC: a new-voice-data block is shorter "
                                                     + "than its own twelve-byte header.");
                    }
                    if (sampleRate == 0)
                    {
                        sampleRate = (int)Rl32();
                        bitsPerCodedSample = R8();
                        blockChannels = R8();
                        channels = blockChannels;
                    }
                    else
                    {
                        Skip(6);
                    }
                    tmpCodec = Rl16();
                    Skip(4);
                    remainingSize -= 12;
                    maxSize -= 12;
                    break;

                default:
                    Skip(remainingSize);
                    maxSize -= (int)remainingSize;
                    remainingSize = 0;
                    break;
            }
        }

        if (sampleRate <= 0)
            throw new InvalidDataException($"VOC: invalid sample rate {sampleRate}.");

        if (tmpCodec >= 0)
        {
            string? fromTag = CodecGetId(tmpCodec);
            if (CodecId.Length == 0)
            {
                // The C logs "unknown codec tag" and carries on with codec_id NONE unless the
                // caller named a codec; nothing here can play a stream whose format is unknown,
                // so it says so at the point the format is decided.
                CodecId = fromTag
                    ?? throw new NotSupportedException(
                        $"VOC: codec tag {tmpCodec} is not one voc.c's table knows.");
            }
            else if (CodecId != fromTag)
            {
                // "Ignoring mid-stream change in audio codec" - the C warns and keeps the first.
            }
        }

        BitRate = (long)sampleRate * channels * bitsPerCodedSample;

        if (maxSize <= 0)
            maxSize = 2048;
        int size = (int)Math.Min(remainingSize, maxSize);
        remainingSize -= size;

        if (buffer.Length < size)
            buffer = new byte[size];
        int read = Read(buffer.AsSpan(0, size));

        long packetPts = pts;
        long duration = AudioFrameDuration(CodecId, channels, bitsPerCodedSample, size);
        pts = duration > 0 && pts != NoPts ? pts + duration : NoPts;

        info = new VocPacketInfo(read, packetPts, duration);
        return read > 0;
    }

    /// <summary>
    /// vocdec.c's <c>voc_read_seek</c>. Returns false where the C returns -1 - which it does
    /// whenever the target is at or past the last index entry, leaving the context positioned
    /// for the generic seek to finish the job.
    /// </summary>
    public bool ReadSeek(long timestamp)
    {
        if (indexEntries.Count == 0)
            throw new InvalidOperationException("VOC: cannot seek while no stream was found yet.");

        int index = IndexSearchTimestamp(timestamp);

        if (index >= 0 && index < indexEntries.Count - 1)
        {
            var e = indexEntries[index];
            Seek(e.Pos);
            pts = e.Timestamp;
            remainingSize = e.Size;
            return true;
        }

        if (indexEntries[0].Timestamp <= timestamp)
        {
            // prepare context for seek_frame_generic()
            var e = indexEntries[^1];
            pts = e.Timestamp;
            remainingSize = e.Size;
        }
        return false;
    }

    /// <summary>
    /// av_index_search_timestamp with no flags: the last entry at or before the target. An
    /// entry whose timestamp is NOPTS cannot be compared and ends the usable part of the index,
    /// which is what a VOC of a codec with no frame duration produces after its first packet.
    /// </summary>
    private int IndexSearchTimestamp(long timestamp)
    {
        int best = -1;
        for (int i = 0; i < indexEntries.Count; i++)
        {
            if (indexEntries[i].Timestamp == NoPts) break;
            if (indexEntries[i].Timestamp <= timestamp) best = i;
            else break;
        }
        return best;
    }

    // ── the tag table and the two libavcodec helpers ────────────────────

    /// <summary>voc.c's <c>ff_voc_codec_tags</c>.</summary>
    internal static string? CodecGetId(int tag) => tag switch
    {
        0x00 => "pcm_u8",
        0x01 => "adpcm_sbpro_4",
        0x02 => "adpcm_sbpro_3",
        0x03 => "adpcm_sbpro_2",
        0x04 => "pcm_s16le",
        0x06 => "pcm_alaw",
        0x07 => "pcm_mulaw",
        0x0200 => "adpcm_ct",
        _ => null,
    };

    /// <summary>
    /// libavcodec/utils.c's <c>av_get_exact_bits_per_sample</c>, for the eight codecs a VOC can
    /// hold. Zero where the C returns zero, which is what sends a codec down the
    /// bits_per_coded_sample path in the duration helper below.
    /// </summary>
    internal static int GetExactBitsPerSample(string codecId) => codecId switch
    {
        "pcm_u8" or "pcm_alaw" or "pcm_mulaw" => 8,
        "pcm_s16le" => 16,
        "adpcm_ct" => 4,
        "adpcm_sbpro_2" or "adpcm_sbpro_3" or "adpcm_sbpro_4" => 0,
        "" => 0,
        _ => throw new NotSupportedException(
            $"VOC: {codecId} is not a codec a VOC file can hold."),
    };

    /// <summary>libavcodec/utils.c's <c>av_get_bits_per_sample</c>, same eight codecs.</summary>
    internal static int GetBitsPerSample(string codecId) => codecId switch
    {
        "adpcm_sbpro_2" => 2,
        "adpcm_sbpro_3" => 3,
        "adpcm_sbpro_4" => 4,
        _ => GetExactBitsPerSample(codecId),
    };

    /// <summary>
    /// libavcodec/utils.c's <c>av_get_audio_frame_duration2</c> over
    /// <c>get_audio_frame_duration</c>, for the codecs this demuxer can produce.
    /// </summary>
    /// <remarks>
    /// The C function is a table for every codec in libavcodec and is not reproduced whole. Of
    /// the eight a VOC can hold, five have an exact bits-per-sample and take the first branch;
    /// the three Sound Blaster Pro ADPCM variants match nothing further down and fall out of
    /// the bottom at zero, which is how a VOC of one of them ends up with NOPTS timestamps on
    /// every packet after the first. ffprobe prints exactly that.
    /// </remarks>
    internal static long AudioFrameDuration(string codecId, int ch, int bitsPerCodedSample, int frameBytes)
    {
        int bps = GetExactBitsPerSample(codecId);

        /* codecs with an exact constant bits per sample */
        if (bps > 0 && ch > 0 && frameBytes > 0 && ch < 32768 && bps < 32768)
            return frameBytes * 8L / (bps * ch);

        return 0;
    }

    // ── the AVIOContext calls, over a Stream ────────────────────────────

    private int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = stream.Read(destination[total..]);
            if (n <= 0) break;
            total += n;
        }
        if (total < destination.Length)
        {
            eof = true;
            destination[total..].Clear();
        }
        return total;
    }

    private byte R8()
    {
        Span<byte> b = stackalloc byte[1];
        Read(b);
        return b[0];
    }

    private int Rl16()
    {
        Span<byte> b = stackalloc byte[2];
        Read(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    private int Rl24()
    {
        Span<byte> b = stackalloc byte[3];
        Read(b);
        return b[0] | (b[1] << 8) | (b[2] << 16);
    }

    private uint Rl32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private long Tell() => stream.Position;

    private void Seek(long position)
    {
        stream.Position = position;
        eof = position >= stream.Length;
    }

    private void Skip(long count) => Seek(stream.Position + count);
}

/// <summary>One packet as <see cref="VocDec.ReadPacket"/> reports it.</summary>
public readonly struct VocPacketInfo(int size, long pts, long duration)
{
    /// <summary>Bytes of the caller's buffer this packet fills.</summary>
    public readonly int Size = size;

    /// <summary>
    /// Presentation timestamp in sample frames, or <see cref="VocDec.NoPts"/> once a codec with
    /// no computable frame duration has made the clock meaningless.
    /// </summary>
    public readonly long Pts = pts;

    /// <summary>Sample frames the packet decodes to, zero where the C cannot work it out.</summary>
    public readonly long Duration = duration;
}

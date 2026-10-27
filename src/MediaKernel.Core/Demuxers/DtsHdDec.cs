// Raw DTS-HD container reader - a port of ffmpeg-master/libavformat/dtshddec.c.
//
// A .dtshd file is a flat list of chunks: an eight-byte ASCII id, an eight-byte big-endian
// length, then that many bytes. The elementary stream lives in STRMDATA; everything else is
// metadata, and one chunk of it matters - AUPR-HDR carries the sample rate, the channel mask and
// the sample counts, which a raw DTS stream has no way to state.
//
// Reading the chunks is not optional politeness. A .dtshd file also contains a BLACKOUT chunk
// whose payload is encoded DTS frames for the silent lead-in, so scanning the whole file for a
// sync word - which is what DtsAudioSource does on a raw stream - finds frames outside STRMDATA
// and decodes them as if they were part of the programme.

using System;
using System.IO;
using MediaKernel.Core.Codecs.Audio.Dts;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// The DTS-HD container: chunk ids as the eight ASCII bytes read big-endian, exactly as
/// <c>dtshddec.c</c> spells them.
/// </summary>
internal static class DtsHdChunk
{
    public const ulong AuprHdr = 0x415550522D484452ul;   // "AUPR-HDR"
    public const ulong AuprInfo = 0x41555052494E464Ful;  // "AUPRINFO"
    public const ulong BitShVtb = 0x4249545348565442ul;  // "BITSHVTB"
    public const ulong Blackout = 0x424C41434B4F5554ul;  // "BLACKOUT"
    public const ulong BranchPt = 0x4252414E43485054ul;  // "BRANCHPT"
    public const ulong BuildVer = 0x4255494C44564552ul;  // "BUILDVER"
    public const ulong CoreSsmd = 0x434F524553534D44ul;  // "CORESSMD"
    public const ulong DtsHdHdr = 0x4454534844484452ul;  // "DTSHDHDR"
    public const ulong ExtSsMd = 0x45585453535F4D44ul;   // "EXTSS_MD"
    public const ulong FileInfo = 0x46494C45494E464Ful;  // "FILEINFO"
    public const ulong NaviTbl = 0x4E4156492D54424Cul;   // "NAVI-TBL"
    public const ulong StrmData = 0x5354524D44415441ul;  // "STRMDATA"
    public const ulong TimeCode = 0x54494D45434F4445ul;  // "TIMECODE"
}

/// <summary>
/// A DTS-HD file as an elementary audio source: the chunk list is read once, then frames come
/// from STRMDATA through the same splitter a raw <c>.dts</c> uses.
/// </summary>
internal sealed class DtsHdAudioSource : IElementaryAudioSource
{
    private readonly Stream stream;
    private readonly DtsAudioSource inner;

    private readonly int sampleRate;
    private readonly int channelCount;
    private readonly TimeSpan duration;

    /// <summary>
    /// <c>initial_padding</c> from AUPR-HDR: samples the encoder prepended, which a player is
    /// meant to drop. Nothing drops them here, because FFmpeg only drops them when
    /// <c>skip_manual</c> is off and the conformance references are generated with it on - so
    /// discarding them would make our output shorter than every reference. Exposed so a caller
    /// that wants gapless playback can.
    /// </summary>
    public int InitialPadding { get; }

    /// <summary>Speaker mask from AUPR-HDR, in DTS speaker-bit order.</summary>
    public int SpeakerMask { get; }

    public DtsHdAudioSource(Stream stream)
    {
        this.stream = stream;

        if (!DtshdReadHeader(stream, out long dataStart, out long dataEnd,
                             out sampleRate, out channelCount, out long durationSamples,
                             out int initialPadding, out int speakerMask))
            throw new InvalidDataException("not a DTS-HD file: no DTSHDHDR chunk, or no STRMDATA.");

        InitialPadding = initialPadding;
        SpeakerMask = speakerMask;
        duration = sampleRate > 0
            ? TimeSpan.FromTicks(durationSamples * TimeSpan.TicksPerSecond / sampleRate)
            : TimeSpan.Zero;

        stream.Position = dataStart;
        inner = new DtsAudioSource(stream, dataEnd - dataStart);
    }

    /// <summary>
    /// True when the first eight bytes are <c>DTSHDHDR</c>, which is
    /// <c>dtshd_probe</c>'s whole test.
    /// </summary>
    public static bool DtshdProbe(ReadOnlySpan<byte> data)
        => data.Length >= 8 && ReadBe64(data) == DtsHdChunk.DtsHdHdr;

    public string CodecId => "dts";

    public int SampleRate => sampleRate != 0 ? sampleRate : inner.SampleRate;

    public int ChannelCount => channelCount != 0 ? channelCount : inner.ChannelCount;

    public TimeSpan Duration => duration;

    public DemuxerPacket? ReadFrame() => inner.ReadFrame();

    public void Dispose() => inner.Dispose();

    /// <summary>
    /// Walks the chunk list, recording where STRMDATA is and what AUPR-HDR says about the
    /// programme. Leaves the stream wherever it finished; the caller seeks.
    /// </summary>
    private static bool DtshdReadHeader(Stream stream, out long dataStart, out long dataEnd,
                                        out int sampleRate, out int channelCount,
                                        out long durationSamples, out int initialPadding,
                                        out int speakerMask)
    {
        dataStart = dataEnd = 0;
        sampleRate = channelCount = initialPadding = speakerMask = 0;
        durationSamples = 0;

        Span<byte> header = stackalloc byte[16];
        Span<byte> aupr = stackalloc byte[21];
        bool first = true;

        while (true)
        {
            if (!TryReadExactly(stream, header))
                break;

            ulong chunkType = ReadBe64(header);
            ulong chunkSize = ReadBe64(header[8..]);

            // dtshd_probe only looks at the first eight bytes, so the first chunk has to be the
            // header chunk or this is not a DTS-HD file at all.
            if (first && chunkType != DtsHdChunk.DtsHdHdr)
                return false;
            first = false;

            // dtshddec.c's two bounds, kept as they are: a chunk cannot be smaller than its own
            // length field's minimum, and 2^61 is where the arithmetic below would overflow.
            if (chunkSize < 4 || chunkSize > 1ul << 61)
                return false;

            long payload = stream.Position;

            switch (chunkType)
            {
                case DtsHdChunk.StrmData:
                    dataStart = payload;
                    dataEnd = payload + (long)chunkSize;
                    break;

                case DtsHdChunk.AuprHdr:
                    if (chunkSize < 21)
                        return false;
                    if (!TryReadExactly(stream, aupr))
                        return false;

                    // Three bytes of version and flags, then the fields dtshddec.c reads in
                    // order: rate, frame count, samples per frame, the original sample count as
                    // 40 bits, the speaker mask, the initial padding.
                    sampleRate = aupr[3] << 16 | aupr[4] << 8 | aupr[5];
                    if (sampleRate == 0)
                        return false;

                    long frames = (long)aupr[6] << 24 | (long)aupr[7] << 16
                                | (long)aupr[8] << 8 | aupr[9];
                    int samplesPerFrame = aupr[10] << 8 | aupr[11];
                    durationSamples = frames * samplesPerFrame;

                    speakerMask = aupr[17] << 8 | aupr[18];
                    channelCount = Dca.CountChsForMask(speakerMask);
                    initialPadding = aupr[19] << 8 | aupr[20];
                    break;
            }

            // Every branch above leaves the stream somewhere inside the payload, so seek rather
            // than skip: a chunk is addressed from its own start, not from how much of it was read.
            long next = payload + (long)chunkSize;
            if (next > stream.Length)
                break;
            stream.Position = next;
        }

        return dataEnd > dataStart;
    }

    private static ulong ReadBe64(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
            value = value << 8 | data[i];
        return value;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> into)
    {
        int at = 0;
        while (at < into.Length)
        {
            int read = stream.Read(into[at..]);
            if (read <= 0)
                return false;
            at += read;
        }
        return true;
    }
}

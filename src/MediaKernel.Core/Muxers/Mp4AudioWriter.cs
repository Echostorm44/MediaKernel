// MediaKernel - a single-track audio .m4a writer
//
// ALAC and AAC both live in an MP4 container, and this tree had no MP4 writer at all: the AAC
// ripping stub wrote an ftyp box and nothing else, and ALAC had nowhere to put its packets. This
// is the smallest writer that produces a file iTunes, ffmpeg and this tree's own Mp4Demuxer all
// read: ftyp, mdat, then moov.
//
// moov comes last on purpose. The sample table has to name the size of every packet and the
// duration of every frame, and neither is known until the encoder has seen the whole stream, so
// a writer that puts moov first either buffers the entire audio in memory or guesses and
// rewrites. Writing the samples straight through and the table afterwards is what a tagger does
// too; the only cost is that the file is not progressively streamable, which is not what a
// ripper produces files for.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Muxers;

/// <summary>What the sample table needs to know about the one track being written.</summary>
public sealed class Mp4AudioTrack
{
    /// <summary>The sample entry's four-character code, e.g. <c>alac</c> or <c>mp4a</c>.</summary>
    public required string SampleEntryType { get; init; }

    /// <summary>
    /// The codec's own configuration box, complete with its size and type - for ALAC this is the
    /// 36-byte magic cookie, which already starts with its own <c>00 00 00 24 'alac'</c> header.
    /// It is appended inside the sample entry verbatim.
    /// </summary>
    public required byte[] CodecConfiguration { get; init; }

    public required int SampleRate { get; init; }
    public required int Channels { get; init; }

    /// <summary>
    /// The sample size written into the audio sample entry. A version 0 entry cannot express
    /// anything but 8 or 16, and every reader takes the real depth from the configuration box,
    /// so 16 is what iTunes and ffmpeg write for 24-bit ALAC as well.
    /// </summary>
    public int SampleSizeInBits { get; init; } = 16;

    /// <summary>iTunes-style tags, written into <c>udta/meta/ilst</c>. Optional.</summary>
    public Mp4AudioTags? Tags { get; init; }
}

/// <summary>The tags this writer knows how to put in an <c>ilst</c> box.</summary>
public sealed class Mp4AudioTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Composer { get; init; }
    public string? Genre { get; init; }
    public string? Comment { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public byte[]? CoverArt { get; init; }
    public string? CoverArtMimeType { get; init; }
}

/// <summary>
/// Writes one audio track into a .m4a file: <c>ftyp</c>, the packets in a single <c>mdat</c>,
/// then <c>moov</c> with the sample table the packets produced.
/// </summary>
/// <remarks>
/// The output stream must be seekable, because <c>mdat</c>'s size is only known once the last
/// packet has been written and is patched in at the end.
/// </remarks>
public sealed class Mp4AudioWriter
{
    private readonly Stream output;
    private readonly Mp4AudioTrack track;

    // One entry per packet, which is what stsz carries, and the frame lengths that stts groups.
    private readonly List<int> sampleSizes = [];
    private readonly List<int> sampleDurations = [];

    private long mdatHeaderPosition = -1;
    private long mdatDataPosition = -1;
    private long totalSamples;
    private bool started;
    private bool finished;

    public Mp4AudioWriter(Stream output, Mp4AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(track);

        if (!output.CanSeek)
        {
            throw new ArgumentException(
                "An .m4a is written with its sample table at the end, so the stream must be "
              + "seekable to patch the mdat size.", nameof(output));
        }
        if (track.CodecConfiguration.Length == 0)
            throw new ArgumentException("The codec configuration box is empty.", nameof(track));
        if (track.SampleEntryType.Length != 4)
            throw new ArgumentException("A sample entry type is four characters.", nameof(track));

        this.output = output;
        this.track = track;
    }

    /// <summary>Total audio samples (per channel) written so far.</summary>
    public long TotalSamples => totalSamples;

    /// <summary>Writes <c>ftyp</c> and opens <c>mdat</c>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started) throw new InvalidOperationException("The writer has already been started.");

        var ftyp = new MemoryStream();
        WriteFourCc(ftyp, "M4A ");               // major brand
        WriteUInt32(ftyp, 0x200);                // minor version, as iTunes writes it
        WriteFourCc(ftyp, "M4A ");
        WriteFourCc(ftyp, "mp42");
        WriteFourCc(ftyp, "isom");
        await WriteBoxAsync("ftyp", ftyp.ToArray(), cancellationToken);

        mdatHeaderPosition = output.Position;
        var placeholder = new byte[8];
        WriteFourCcAt(placeholder, 4, "mdat");   // size stays zero until FinishAsync patches it
        await output.WriteAsync(placeholder, cancellationToken);
        mdatDataPosition = output.Position;

        started = true;
    }

    /// <summary>
    /// Appends one coded packet and records its size and the number of audio samples it carries.
    /// </summary>
    public async Task WriteSampleAsync(ReadOnlyMemory<byte> packet, int sampleCount,
        CancellationToken cancellationToken = default)
    {
        if (!started || finished)
            throw new InvalidOperationException("The writer is not open for samples.");
        if (packet.Length == 0)
            throw new ArgumentException("An empty packet has no place in a sample table.", nameof(packet));
        if (sampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount,
                "A packet carries at least one audio sample.");

        await output.WriteAsync(packet, cancellationToken);
        sampleSizes.Add(packet.Length);
        sampleDurations.Add(sampleCount);
        totalSamples += sampleCount;
    }

    /// <summary>Patches <c>mdat</c>'s size and writes <c>moov</c>.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        if (!started) throw new InvalidOperationException("The writer was never started.");
        if (finished) return;
        finished = true;

        long mdatBytes = output.Position - mdatHeaderPosition;
        if (mdatBytes > uint.MaxValue)
        {
            throw new NotSupportedException(
                "The audio exceeds 4 GB, which needs a 64-bit mdat this writer does not emit.");
        }

        long end = output.Position;
        output.Position = mdatHeaderPosition;
        var size = new byte[4];
        WriteUInt32At(size, 0, (uint)mdatBytes);
        await output.WriteAsync(size, cancellationToken);
        output.Position = end;

        await WriteBoxAsync("moov", BuildMoov(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    // ── moov ────────────────────────────────────────────────────────────────────

    private byte[] BuildMoov()
    {
        var moov = new MemoryStream();
        WriteBox(moov, "mvhd", BuildMvhd());
        WriteBox(moov, "trak", BuildTrak());
        if (track.Tags != null)
        {
            byte[] udta = BuildUdta(track.Tags);
            if (udta.Length > 0) WriteBox(moov, "udta", udta);
        }
        return moov.ToArray();
    }

    private byte[] BuildMvhd()
    {
        var b = new MemoryStream();
        WriteUInt32(b, 0);                                  // version 0, no flags
        WriteUInt32(b, 0);                                  // creation time
        WriteUInt32(b, 0);                                  // modification time
        WriteUInt32(b, (uint)track.SampleRate);             // timescale: one tick per sample
        WriteUInt32(b, (uint)totalSamples);                 // duration
        WriteUInt32(b, 0x00010000);                         // rate 1.0
        WriteUInt16(b, 0x0100);                             // volume 1.0
        WriteUInt16(b, 0);                                  // reserved
        WriteUInt32(b, 0);
        WriteUInt32(b, 0);
        WriteUnityMatrix(b);
        for (int i = 0; i < 6; i++) WriteUInt32(b, 0);      // predefined
        WriteUInt32(b, 2);                                  // next track id
        return b.ToArray();
    }

    private byte[] BuildTrak()
    {
        var trak = new MemoryStream();
        WriteBox(trak, "tkhd", BuildTkhd());
        WriteBox(trak, "mdia", BuildMdia());
        return trak.ToArray();
    }

    private byte[] BuildTkhd()
    {
        var b = new MemoryStream();
        WriteUInt32(b, 0x0000_0007);                        // version 0; enabled, in movie, in preview
        WriteUInt32(b, 0);                                  // creation time
        WriteUInt32(b, 0);                                  // modification time
        WriteUInt32(b, 1);                                  // track id
        WriteUInt32(b, 0);                                  // reserved
        WriteUInt32(b, (uint)totalSamples);                 // duration, in the movie timescale
        WriteUInt32(b, 0);
        WriteUInt32(b, 0);
        WriteUInt16(b, 0);                                  // layer
        WriteUInt16(b, 0);                                  // alternate group
        WriteUInt16(b, 0x0100);                             // volume 1.0
        WriteUInt16(b, 0);                                  // reserved
        WriteUnityMatrix(b);
        WriteUInt32(b, 0);                                  // width
        WriteUInt32(b, 0);                                  // height
        return b.ToArray();
    }

    private byte[] BuildMdia()
    {
        var mdia = new MemoryStream();

        var mdhd = new MemoryStream();
        WriteUInt32(mdhd, 0);                               // version 0, no flags
        WriteUInt32(mdhd, 0);                               // creation time
        WriteUInt32(mdhd, 0);                               // modification time
        WriteUInt32(mdhd, (uint)track.SampleRate);          // timescale
        WriteUInt32(mdhd, (uint)totalSamples);              // duration
        WriteUInt16(mdhd, 0x55C4);                          // language: packed "und"
        WriteUInt16(mdhd, 0);                               // predefined
        WriteBox(mdia, "mdhd", mdhd.ToArray());

        var hdlr = new MemoryStream();
        WriteUInt32(hdlr, 0);                               // version 0, no flags
        WriteUInt32(hdlr, 0);                               // predefined
        WriteFourCc(hdlr, "soun");                          // handler type
        WriteUInt32(hdlr, 0);
        WriteUInt32(hdlr, 0);
        WriteUInt32(hdlr, 0);                               // reserved
        WriteNullTerminated(hdlr, "SoundHandler");
        WriteBox(mdia, "hdlr", hdlr.ToArray());

        WriteBox(mdia, "minf", BuildMinf());
        return mdia.ToArray();
    }

    private byte[] BuildMinf()
    {
        var minf = new MemoryStream();

        var smhd = new MemoryStream();
        WriteUInt32(smhd, 0);                               // version 0, no flags
        WriteUInt16(smhd, 0);                               // balance
        WriteUInt16(smhd, 0);                               // reserved
        WriteBox(minf, "smhd", smhd.ToArray());

        // The media is in this file, which a self-contained 'url ' entry says by its flags.
        var url = new MemoryStream();
        WriteUInt32(url, 0x0000_0001);
        var dref = new MemoryStream();
        WriteUInt32(dref, 0);                               // version 0, no flags
        WriteUInt32(dref, 1);                               // one entry
        WriteBox(dref, "url ", url.ToArray());
        var dinf = new MemoryStream();
        WriteBox(dinf, "dref", dref.ToArray());
        WriteBox(minf, "dinf", dinf.ToArray());

        WriteBox(minf, "stbl", BuildStbl());
        return minf.ToArray();
    }

    private byte[] BuildStbl()
    {
        var stbl = new MemoryStream();

        // stsd: one version 0 audio sample entry, with the codec's configuration box inside it.
        var entry = new MemoryStream();
        for (int i = 0; i < 6; i++) entry.WriteByte(0);     // reserved
        WriteUInt16(entry, 1);                              // data reference index
        WriteUInt16(entry, 0);                              // version
        WriteUInt16(entry, 0);                              // revision
        WriteUInt32(entry, 0);                              // vendor
        WriteUInt16(entry, (ushort)track.Channels);
        WriteUInt16(entry, (ushort)track.SampleSizeInBits);
        WriteUInt16(entry, 0);                              // compression id
        WriteUInt16(entry, 0);                              // packet size
        // 16.16 fixed point, so a rate above 65535 cannot be expressed here. Every reader takes
        // the real rate from the configuration box; this field only has to be consistent.
        WriteUInt32(entry, (uint)(Math.Min(track.SampleRate, 65535) << 16));
        entry.Write(track.CodecConfiguration);

        var stsd = new MemoryStream();
        WriteUInt32(stsd, 0);                               // version 0, no flags
        WriteUInt32(stsd, 1);                               // one entry
        WriteBox(stsd, track.SampleEntryType, entry.ToArray());
        WriteBox(stbl, "stsd", stsd.ToArray());

        // stts: runs of equal frame length. A ripped track is one run plus a shorter last frame.
        var runs = new List<(int Count, int Duration)>();
        foreach (int d in sampleDurations)
        {
            if (runs.Count > 0 && runs[^1].Duration == d)
                runs[^1] = (runs[^1].Count + 1, d);
            else
                runs.Add((1, d));
        }
        var stts = new MemoryStream();
        WriteUInt32(stts, 0);
        WriteUInt32(stts, (uint)runs.Count);
        foreach ((int count, int duration) in runs)
        {
            WriteUInt32(stts, (uint)count);
            WriteUInt32(stts, (uint)duration);
        }
        WriteBox(stbl, "stts", stts.ToArray());

        // One chunk holding every packet, which is what a single contiguous mdat is.
        var stsc = new MemoryStream();
        WriteUInt32(stsc, 0);
        WriteUInt32(stsc, 1);                               // one entry
        WriteUInt32(stsc, 1);                               // first chunk
        WriteUInt32(stsc, (uint)Math.Max(sampleSizes.Count, 1));
        WriteUInt32(stsc, 1);                               // sample description index
        WriteBox(stbl, "stsc", stsc.ToArray());

        var stsz = new MemoryStream();
        WriteUInt32(stsz, 0);
        WriteUInt32(stsz, 0);                               // sizes vary, so they are listed
        WriteUInt32(stsz, (uint)sampleSizes.Count);
        foreach (int s in sampleSizes) WriteUInt32(stsz, (uint)s);
        WriteBox(stbl, "stsz", stsz.ToArray());

        var stco = new MemoryStream();
        WriteUInt32(stco, 0);
        WriteUInt32(stco, 1);                               // one chunk
        WriteUInt32(stco, (uint)mdatDataPosition);
        WriteBox(stbl, "stco", stco.ToArray());

        return stbl.ToArray();
    }

    // ── tags ────────────────────────────────────────────────────────────────────

    private static byte[] BuildUdta(Mp4AudioTags tags)
    {
        var ilst = new MemoryStream();

        AddText(ilst, "©nam", tags.Title);
        AddText(ilst, "©ART", tags.Artist);
        AddText(ilst, "©alb", tags.Album);
        AddText(ilst, "aART", tags.AlbumArtist);
        AddText(ilst, "©wrt", tags.Composer);
        AddText(ilst, "©gen", tags.Genre);
        AddText(ilst, "©cmt", tags.Comment);
        AddText(ilst, "©day", tags.Year?.ToString());

        if (tags.TrackNumber.HasValue)
            AddPair(ilst, "trkn", tags.TrackNumber.Value, tags.TrackCount ?? 0);
        if (tags.DiscNumber.HasValue)
            AddPair(ilst, "disk", tags.DiscNumber.Value, tags.DiscCount ?? 0);

        if (tags.CoverArt is { Length: > 0 })
        {
            // 13 is JPEG and 14 is PNG, which are the only two iTunes recognises.
            int flag = (tags.CoverArtMimeType ?? "image/jpeg").Contains("png",
                StringComparison.OrdinalIgnoreCase) ? 14 : 13;
            AddData(ilst, "covr", flag, tags.CoverArt);
        }

        if (ilst.Length == 0) return [];

        var meta = new MemoryStream();
        WriteUInt32(meta, 0);                               // version 0, no flags
        // The metadata handler, byte for byte as ffmpeg's mov muxer writes it.
        WriteUInt32(meta, 33);
        WriteFourCc(meta, "hdlr");
        WriteUInt32(meta, 0);                               // version 0, no flags
        WriteUInt32(meta, 0);                               // predefined
        WriteFourCc(meta, "mdir");
        WriteFourCc(meta, "appl");
        WriteUInt32(meta, 0);
        WriteUInt32(meta, 0);
        meta.WriteByte(0);
        WriteBox(meta, "ilst", ilst.ToArray());

        var udta = new MemoryStream();
        WriteBox(udta, "meta", meta.ToArray());
        return udta.ToArray();
    }

    private static void AddText(Stream ilst, string type, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        AddData(ilst, type, 1, Encoding.UTF8.GetBytes(value));   // 1: UTF-8 text
    }

    private static void AddPair(Stream ilst, string type, int number, int total)
    {
        var payload = new byte[8];
        payload[2] = (byte)(number >> 8);
        payload[3] = (byte)number;
        payload[4] = (byte)(total >> 8);
        payload[5] = (byte)total;
        AddData(ilst, type, 0, payload);                         // 0: implicit, a binary pair
    }

    private static void AddData(Stream ilst, string type, int typeFlag, byte[] payload)
    {
        var data = new MemoryStream();
        data.WriteByte(0);                                       // version
        data.WriteByte((byte)(typeFlag >> 16));
        data.WriteByte((byte)(typeFlag >> 8));
        data.WriteByte((byte)typeFlag);
        WriteUInt32(data, 0);                                    // locale
        data.Write(payload);

        var item = new MemoryStream();
        WriteBox(item, "data", data.ToArray());
        WriteBox(ilst, type, item.ToArray());
    }

    // ── box plumbing ────────────────────────────────────────────────────────────

    private async Task WriteBoxAsync(string type, byte[] content, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        WriteUInt32At(header, 0, (uint)(8 + content.Length));
        WriteFourCcAt(header, 4, type);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(content, cancellationToken);
    }

    private static void WriteBox(Stream s, string type, byte[] content)
    {
        WriteUInt32(s, (uint)(8 + content.Length));
        WriteFourCc(s, type);
        s.Write(content);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteUInt16(Stream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteUInt32At(byte[] a, int at, uint v)
    {
        a[at] = (byte)(v >> 24);
        a[at + 1] = (byte)(v >> 16);
        a[at + 2] = (byte)(v >> 8);
        a[at + 3] = (byte)v;
    }

    /// <summary>
    /// A four-character code is four BYTES, which is why the box types here are written through
    /// Latin-1 rather than UTF-8: the iTunes tag types begin with the copyright sign, and UTF-8
    /// would spend two bytes on it and shift the whole box.
    /// </summary>
    private static void WriteFourCc(Stream s, string type)
    {
        foreach (char c in type) s.WriteByte((byte)c);
    }

    private static void WriteFourCcAt(byte[] a, int at, string type)
    {
        for (int i = 0; i < type.Length; i++) a[at + i] = (byte)type[i];
    }

    private static void WriteNullTerminated(Stream s, string value)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(value)) s.WriteByte(b);
        s.WriteByte(0);
    }

    private static void WriteUnityMatrix(Stream s)
    {
        WriteUInt32(s, 0x00010000); WriteUInt32(s, 0); WriteUInt32(s, 0);
        WriteUInt32(s, 0); WriteUInt32(s, 0x00010000); WriteUInt32(s, 0);
        WriteUInt32(s, 0); WriteUInt32(s, 0); WriteUInt32(s, 0x40000000);
    }
}

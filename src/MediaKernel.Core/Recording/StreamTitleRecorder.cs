using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaKernel.Core.Codecs.AAC;
using MediaKernel.Core.Codecs.MP3;
using MediaKernel.Core.Input;

namespace MediaKernel.Core.Recording;

/// <summary>
/// One recorded track: where it was written, what the station called it, and how big it is.
/// </summary>
public sealed class RecordedTrack(string path, string? title, long bytes, bool partial)
{
    /// <summary>The file written.</summary>
    public string Path { get; } = path;

    /// <summary>The station's <c>StreamTitle</c> for it, or null for the lead-in.</summary>
    public string? Title { get; } = title;

    /// <summary>Bytes written, which are the station's own bytes and not a re-encode.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>
    /// True when this track was already playing when recording started, or was still playing
    /// when it stopped, so the file holds part of a track and not all of one. A recorder that
    /// does not mark these makes its caller guess.
    /// </summary>
    public bool Partial { get; } = partial;
}

/// <summary>
/// Records a radio stream to one file per announced track while the same bytes go on to a
/// decoder, cutting at frame boundaries and never re-encoding.
/// </summary>
/// <remarks>
/// <para>
/// This is a <see cref="MediaInput"/> that wraps another one, so the caller plays through it and
/// the recording is a side effect of the read it was going to do anyway:
/// <c>DecodePipeline.Open(recorder.AsStream())</c>. One socket, one pass over the bytes. Taking
/// a second connection to record what is already being played would double the bandwidth and
/// give two streams that drift apart.
/// </para>
/// <para>
/// <b>The cut is at a frame boundary, not at the title.</b> A station announces a new track by
/// splicing a metadata block into the audio wherever it happens to be - almost always inside an
/// audio frame. Cutting the bytes there leaves a fragment at the end of one file and a headless
/// fragment at the start of the next, and a decoder handed the second one has to resynchronise
/// before it produces anything. So the title is held until the next frame header, and the whole
/// frame the announcement arrived in belongs to the track that was playing. Concatenating every
/// file this writes reproduces the stream byte for byte.
/// </para>
/// <para>
/// <b>What this does not do.</b> It writes no tags: an ID3v2 header would make the file no
/// longer a byte-exact copy of what the station sent, and which of the two a caller wants is
/// the caller's decision. <see cref="RecordedTrack.Title"/> carries the text for whoever wants
/// to tag afterwards.
/// </para>
/// </remarks>
public sealed class StreamTitleRecorder : MediaInput
{
    /// <summary>The container-less audio formats a radio stream can be, for cutting.</summary>
    private enum StreamFormat
    {
        Unknown,
        MpegAudio,
        Adts,
    }

    private readonly MediaInput source;
    private readonly HttpMediaInput? icySource;
    private readonly string directory;
    private readonly string extension;
    private readonly List<RecordedTrack> tracks = [];

    // Audio read from the source and not yet written out. A cut can only happen at a frame
    // header, so bytes sit here until the frame they are in is complete.
    private byte[] pending = [];
    private int pendingLength;

    private StreamFormat format = StreamFormat.Unknown;
    private FileStream? output;
    private string? currentPath;
    private string? currentTitle;
    private long currentBytes;
    private bool currentIsPartial = true;   // the lead-in is always a partial track
    private int fileIndex;

    private string? pendingTitle;
    private bool cutPending;
    private bool finished;

    /// <summary>Raised when a track's file has been closed and is complete.</summary>
    public event EventHandler<RecordedTrack>? TrackCompleted;

    /// <param name="source">
    /// The stream to record. When it is an <see cref="HttpMediaInput"/> its
    /// <see cref="HttpMediaInput.StreamTitleChanged"/> drives the cuts; any other input records
    /// to a single file, which is the honest behaviour for a source that announces nothing.
    /// </param>
    /// <param name="directory">Where to write. Created if it does not exist.</param>
    /// <param name="extension">File extension, with the dot. ".mp3" or ".aac".</param>
    public StreamTitleRecorder(MediaInput source, string directory, string extension = ".mp3")
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.extension = extension ?? throw new ArgumentNullException(nameof(extension));

        icySource = source as HttpMediaInput;
        if (icySource != null)
            icySource.StreamTitleChanged += OnStreamTitleChanged;
    }

    /// <summary>The tracks written so far, in order. The last one is still open until Finish.</summary>
    public IReadOnlyList<RecordedTrack> Tracks => tracks;

    public override MediaSourceInfo Info => source.Info;
    public override long Position => source.Position;
    public override bool IsOpen => source.IsOpen;

    public override ValueTask OpenAsync(CancellationToken cancellationToken = default)
        => source.OpenAsync(cancellationToken);

    public override ValueTask SeekAsync(long position, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A recording cannot seek: the bytes are being written as they arrive, and seeking "
          + "would either lose a span of them or write it twice.");

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await source.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
            await FinishAsync(cancellationToken);
            return 0;
        }

        Append(buffer.Span[..read]);
        await DrainAsync(cancellationToken);
        return read;
    }

    /// <summary>
    /// Closes the open file and marks it partial, which it is: the station had not announced
    /// another track when recording stopped. Called for you when the source ends.
    /// </summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken = default)
    {
        if (finished)
            return;
        finished = true;

        // Whatever is left is a fragment of the frame that was arriving. It belongs to the
        // track that was playing, so it goes to the open file rather than being dropped -
        // concatenating the files has to reproduce the stream.
        if (pendingLength > 0)
        {
            await EnsureOutputAsync(cancellationToken);
            await output!.WriteAsync(pending.AsMemory(0, pendingLength), cancellationToken);
            currentBytes += pendingLength;
            pendingLength = 0;
        }

        await CloseCurrentAsync(partial: true, cancellationToken);
    }

    private void OnStreamTitleChanged(object? sender, StreamTitleChangedEventArgs e)
    {
        // The ICY reader returns short at every metadata boundary, so this fires before any
        // audio byte that follows the block has been handed out. The cut therefore belongs at
        // the next frame header in what arrives from here on, and no byte offset into the
        // current buffer is needed.
        pendingTitle = e.Title;
        cutPending = true;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (pendingLength + data.Length > pending.Length)
        {
            int size = Math.Max(pendingLength + data.Length, Math.Max(8192, pending.Length * 2));
            byte[] grown = new byte[size];
            pending.AsSpan(0, pendingLength).CopyTo(grown);
            pending = grown;
        }

        data.CopyTo(pending.AsSpan(pendingLength));
        pendingLength += data.Length;
    }

    /// <summary>
    /// Writes out every complete frame in <see cref="pending"/>, cutting the file at the first
    /// frame header at or after a title change.
    /// </summary>
    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        int at = 0;

        while (true)
        {
            if (format == StreamFormat.Unknown)
            {
                format = DetectFormat(pending.AsSpan(at, pendingLength - at));
                if (format == StreamFormat.Unknown)
                    break;      // not enough bytes yet to be sure
            }

            int sync = FindFrameStart(pending.AsSpan(at, pendingLength - at));
            if (sync < 0)
                break;

            // Bytes before the first frame header are not part of any frame - a partial frame
            // at the very start of the recording, or padding. They belong to whatever is open.
            if (sync > 0)
            {
                await WriteAsync(pending.AsMemory(at, sync), cancellationToken);
                at += sync;
                continue;
            }

            if (!TryFrameSize(pending.AsSpan(at, pendingLength - at), out int frameSize))
                break;          // header is there, the rest of the frame is not

            // The cut happens HERE, at a header, and not where the announcement arrived.
            if (cutPending)
            {
                await CloseCurrentAsync(partial: false, cancellationToken);
                currentTitle = pendingTitle;
                currentIsPartial = false;
                cutPending = false;
                pendingTitle = null;
            }

            await WriteAsync(pending.AsMemory(at, frameSize), cancellationToken);
            at += frameSize;
        }

        if (at > 0)
        {
            pending.AsSpan(at, pendingLength - at).CopyTo(pending.AsSpan(0));
            pendingLength -= at;
        }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await EnsureOutputAsync(cancellationToken);
        await output!.WriteAsync(data, cancellationToken);
        currentBytes += data.Length;
    }

    private async ValueTask EnsureOutputAsync(CancellationToken cancellationToken)
    {
        if (output != null)
            return;

        Directory.CreateDirectory(directory);
        currentPath = Path.Combine(directory, FileName(fileIndex, currentTitle) + extension);
        output = new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                                bufferSize: 64 * 1024, useAsync: true);
        await ValueTask.CompletedTask;
    }

    private async ValueTask CloseCurrentAsync(bool partial, CancellationToken cancellationToken)
    {
        if (output == null)
            return;

        await output.FlushAsync(cancellationToken);
        await output.DisposeAsync();
        output = null;

        var track = new RecordedTrack(currentPath!, currentTitle, currentBytes,
                                      currentIsPartial || partial);
        tracks.Add(track);
        TrackCompleted?.Invoke(this, track);

        currentPath = null;
        currentBytes = 0;
        fileIndex++;
    }

    /// <summary>
    /// A file name from the index and the title. The index comes first so the files sort into
    /// broadcast order whatever the titles are, and every character a file system objects to is
    /// replaced rather than dropped, so two different titles cannot collide into one name.
    /// </summary>
    private static string FileName(int index, string? title)
    {
        var name = new StringBuilder();
        name.Append(index.ToString("D3"));
        name.Append(" - ");

        if (string.IsNullOrWhiteSpace(title))
        {
            name.Append("unknown");
            return name.ToString();
        }

        foreach (char c in title.Trim())
        {
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }

        // Windows drops a trailing dot or space silently, which would make the name on disk
        // differ from the name reported.
        while (name.Length > 0 && (name[^1] == '.' || name[^1] == ' '))
            name.Length--;

        return name.ToString();
    }

    /// <summary>
    /// Which elementary format this is, from the first header that parses. Unknown input fails
    /// loudly rather than being copied to one undivided file: a caller that asked for one file
    /// per track and got one file has been told nothing about why.
    /// </summary>
    private static StreamFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        const int Window = 8192;

        for (int i = 0; i + 7 < data.Length; i++)
        {
            if (Mp3FrameParser.TryParseHeader(data[i..], out _))
                return StreamFormat.MpegAudio;
            if (AdtsParser.TryParseHeader(data[i..], out _))
                return StreamFormat.Adts;
        }

        if (data.Length >= Window)
            throw new NotSupportedException(
                $"StreamTitleRecorder: no MPEG audio or ADTS frame header in the first "
              + $"{data.Length} bytes. This records elementary radio streams - MP3 or AAC in "
              + "ADTS - and cannot find frame boundaries to cut on in anything else.");

        return StreamFormat.Unknown;
    }

    /// <summary>Offset of the next frame header, or -1 when there is none in what we have.</summary>
    private int FindFrameStart(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 7 < data.Length; i++)
        {
            if (IsFrameStart(data[i..]))
                return i;
        }
        return -1;
    }

    private bool IsFrameStart(ReadOnlySpan<byte> data) => format switch
    {
        StreamFormat.MpegAudio => Mp3FrameParser.TryParseHeader(data, out _),
        StreamFormat.Adts => AdtsParser.TryParseHeader(data, out _),
        _ => throw new InvalidOperationException(
                 "StreamTitleRecorder: the stream format is still unknown here, which means "
               + "DetectFormat was skipped rather than failing."),
    };

    private bool TryFrameSize(ReadOnlySpan<byte> data, out int frameSize)
    {
        frameSize = 0;

        switch (format)
        {
            case StreamFormat.MpegAudio:
                if (!Mp3FrameParser.TryParseHeader(data, out Mp3FrameHeader mp3))
                    return false;
                frameSize = mp3.FrameSize;
                break;

            case StreamFormat.Adts:
                if (!AdtsParser.TryParseHeader(data, out AdtsHeader adts))
                    return false;
                frameSize = adts.FrameLength;
                break;

            default:
                throw new InvalidOperationException(
                    "StreamTitleRecorder: no frame size for an unknown format.");
        }

        // A frame size of zero would make the drain loop spin forever on the same header.
        if (frameSize <= 0)
            throw new InvalidOperationException(
                $"StreamTitleRecorder: {format} frame header parsed with size {frameSize}.");

        return frameSize <= data.Length;
    }

    public override void Dispose()
    {
        if (icySource != null)
            icySource.StreamTitleChanged -= OnStreamTitleChanged;

        output?.Dispose();
        output = null;
        source.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (icySource != null)
            icySource.StreamTitleChanged -= OnStreamTitleChanged;

        await FinishAsync();
        await source.DisposeAsync();
    }
}

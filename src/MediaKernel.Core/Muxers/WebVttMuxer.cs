// WebVTT Muxer - Web Video Text Tracks subtitle format
// Standard format for HTML5 video subtitles

using System.Globalization;
using System.Text;

namespace MediaKernel.Core.Muxers;

/// <summary>
/// Muxer for WebVTT subtitle files.
/// Outputs W3C standard WebVTT format for HTML5 video.
/// </summary>
public sealed class WebVttMuxer : Muxer
{
    private static readonly byte[] FileHeader = Encoding.UTF8.GetBytes("WEBVTT\r\n\r\n");
    private static readonly byte[] CueArrow = Encoding.UTF8.GetBytes(" --> ");
    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes("\r\n");
    private static readonly byte[] DoubleNewLine = Encoding.UTF8.GetBytes("\r\n\r\n");

    private int subtitleTrackId = -1;
    private long cueCount;

    public WebVttMuxer(Stream outputStream, MuxerConfiguration config)
        : base(outputStream, config)
    {
    }

    public override MuxerFormat Format => MuxerFormat.WebVtt;

    /// <summary>
    /// Gets the number of cues written.
    /// </summary>
    public long CueCount => cueCount;

    public override int AddTrack(MuxerTrackInfo track)
    {
        if (track.TrackType != MuxerTrackType.Subtitle)
            throw new NotSupportedException("WebVTT muxer only supports subtitle tracks");

        if (subtitleTrackId >= 0)
            throw new InvalidOperationException("WebVTT muxer only supports a single subtitle track");

        subtitleTrackId = base.AddTrack(track);
        return subtitleTrackId;
    }

    public override async ValueTask WriteHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (subtitleTrackId < 0)
            throw new InvalidOperationException("No subtitle track added");

        if (_headerWritten)
            return;

        // Write WEBVTT header
        await _outputStream.WriteAsync(FileHeader, cancellationToken);

        // Write optional metadata if provided
        if (!string.IsNullOrEmpty(_config.Title))
        {
            byte[] note = Encoding.UTF8.GetBytes($"NOTE {_config.Title}\r\n\r\n");
            await _outputStream.WriteAsync(note, cancellationToken);
        }

        _headerWritten = true;
    }

    public override async ValueTask WriteSampleAsync(MuxerSample sample, CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            throw new InvalidOperationException("Header must be written before samples");

        if (sample.TrackId != subtitleTrackId)
            throw new ArgumentException($"Invalid track ID: {sample.TrackId}", nameof(sample));

        if (sample.Data.Length == 0)
            return;

        // Decode the subtitle text
        string text = Encoding.UTF8.GetString(sample.Data.Span);
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Calculate timestamps
        long startMicros = sample.PresentationTimestamp;
        long endMicros = startMicros + (sample.Duration > 0 ? sample.Duration : 3_000_000); // Default 3 seconds

        // Write optional cue identifier
        cueCount++;
        byte[] cueId = Encoding.UTF8.GetBytes(cueCount.ToString());
        await _outputStream.WriteAsync(cueId, cancellationToken);
        await _outputStream.WriteAsync(NewLine, cancellationToken);

        // Write timestamps: HH:MM:SS.mmm --> HH:MM:SS.mmm
        byte[] startTime = Encoding.UTF8.GetBytes(FormatTimestamp(startMicros));
        byte[] endTime = Encoding.UTF8.GetBytes(FormatTimestamp(endMicros));
        
        await _outputStream.WriteAsync(startTime, cancellationToken);
        await _outputStream.WriteAsync(CueArrow, cancellationToken);
        await _outputStream.WriteAsync(endTime, cancellationToken);
        await _outputStream.WriteAsync(NewLine, cancellationToken);

        // Write cue payload (the text)
        // Ensure text ends with proper line breaks
        string cleanText = text.TrimEnd('\r', '\n');
        byte[] textBytes = Encoding.UTF8.GetBytes(cleanText);
        await _outputStream.WriteAsync(textBytes, cancellationToken);
        
        // Blank line to separate cues
        await _outputStream.WriteAsync(DoubleNewLine, cancellationToken);
    }

    /// <summary>
    /// Write a subtitle cue directly with start/end times and text.
    /// Convenience method that doesn't require MuxerSample.
    /// </summary>
    public async ValueTask WriteCueAsync(
        TimeSpan startTime,
        TimeSpan endTime,
        string text,
        string? cueId = null,
        string? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            throw new InvalidOperationException("Header must be written before cues");

        if (string.IsNullOrWhiteSpace(text))
            return;

        cueCount++;

        // Write cue identifier (optional)
        string id = cueId ?? cueCount.ToString();
        byte[] idBytes = Encoding.UTF8.GetBytes(id);
        await _outputStream.WriteAsync(idBytes, cancellationToken);
        await _outputStream.WriteAsync(NewLine, cancellationToken);

        // Write timestamps
        byte[] startBytes = Encoding.UTF8.GetBytes(FormatTimestamp(startTime));
        byte[] endBytes = Encoding.UTF8.GetBytes(FormatTimestamp(endTime));
        
        await _outputStream.WriteAsync(startBytes, cancellationToken);
        await _outputStream.WriteAsync(CueArrow, cancellationToken);
        await _outputStream.WriteAsync(endBytes, cancellationToken);

        // Write optional settings (position, alignment, etc.)
        if (!string.IsNullOrEmpty(settings))
        {
            byte[] space = Encoding.UTF8.GetBytes(" ");
            byte[] settingsBytes = Encoding.UTF8.GetBytes(settings);
            await _outputStream.WriteAsync(space, cancellationToken);
            await _outputStream.WriteAsync(settingsBytes, cancellationToken);
        }

        await _outputStream.WriteAsync(NewLine, cancellationToken);

        // Write text
        string cleanText = text.TrimEnd('\r', '\n');
        byte[] textBytes = Encoding.UTF8.GetBytes(cleanText);
        await _outputStream.WriteAsync(textBytes, cancellationToken);
        await _outputStream.WriteAsync(DoubleNewLine, cancellationToken);
    }

    public override async ValueTask WriteTrailerAsync(CancellationToken cancellationToken = default)
    {
        // WebVTT files don't have a trailer - just flush
        await _outputStream.FlushAsync(cancellationToken);
    }

    private static string FormatTimestamp(long microseconds)
    {
        long totalMillis = microseconds / 1000;
        long hours = totalMillis / 3_600_000;
        long minutes = (totalMillis % 3_600_000) / 60_000;
        long seconds = (totalMillis % 60_000) / 1000;
        long millis = totalMillis % 1000;

        return string.Format(CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
            hours, minutes, seconds, millis);
    }

    private static string FormatTimestamp(TimeSpan time)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
            (int)time.TotalHours,
            time.Minutes,
            time.Seconds,
            time.Milliseconds);
    }
}

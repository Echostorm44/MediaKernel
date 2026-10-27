// MPJPEG Muxer - HTTP Multipart JPEG streaming format
// Used for IP cameras, webcam streaming, and Motion JPEG over HTTP

using System.Text;

namespace MediaKernel.Core.Muxers;

/// <summary>
/// Muxer for HTTP multipart JPEG (Motion JPEG streaming).
/// Outputs multipart/x-mixed-replace content suitable for HTTP streaming.
/// </summary>
public sealed class MpJpegMuxer : Muxer
{
    // Standard MIME boundary marker
    private const string Boundary = "7b3cc56e5f51db803f790dad720ed50a";
    private static readonly byte[] BoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + Boundary + "\r\n");
    private static readonly byte[] ContentTypeHeader = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\n");
    private static readonly byte[] ContentLengthPrefix = Encoding.ASCII.GetBytes("Content-Length: ");
    private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");
    private static readonly byte[] FinalBoundary = Encoding.ASCII.GetBytes("\r\n--" + Boundary + "--\r\n");

    private int videoTrackId = -1;
    private long frameCount;

    public MpJpegMuxer(Stream outputStream, MuxerConfiguration config)
        : base(outputStream, config)
    {
    }

    /// <summary>
    /// Gets the MIME content type for this stream.
    /// Use this value in HTTP Content-Type header.
    /// </summary>
    public static string ContentType => $"multipart/x-mixed-replace; boundary={Boundary}";

    public override MuxerFormat Format => MuxerFormat.MpJpeg;

    /// <summary>
    /// Gets the number of frames written.
    /// </summary>
    public long FrameCount => frameCount;

    public override int AddTrack(MuxerTrackInfo track)
    {
        if (track.TrackType != MuxerTrackType.Video)
            throw new NotSupportedException("MPJPEG muxer only supports video tracks");

        if (videoTrackId >= 0)
            throw new InvalidOperationException("MPJPEG muxer only supports a single video track");

        // Verify it's MJPEG codec
        string? codec = track.Codec?.ToUpperInvariant();
        if (codec != "MJPEG" && codec != "JPEG" && codec != "MJPG")
            throw new NotSupportedException("MPJPEG muxer only supports MJPEG/JPEG codec");

        videoTrackId = base.AddTrack(track);
        return videoTrackId;
    }

    public override ValueTask WriteHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (videoTrackId < 0)
            throw new InvalidOperationException("No video track added");

        // MPJPEG doesn't have a file header - each frame is self-contained
        // But we mark header as written to allow samples
        _headerWritten = true;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask WriteSampleAsync(MuxerSample sample, CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            throw new InvalidOperationException("Header must be written before samples");

        if (sample.TrackId != videoTrackId)
            throw new ArgumentException($"Invalid track ID: {sample.TrackId}", nameof(sample));

        if (sample.Data.Length == 0)
            return;

        // Write boundary
        await _outputStream.WriteAsync(BoundaryBytes, cancellationToken);

        // Write headers
        await _outputStream.WriteAsync(ContentTypeHeader, cancellationToken);
        
        // Write content length
        byte[] contentLength = Encoding.ASCII.GetBytes(sample.Data.Length.ToString());
        await _outputStream.WriteAsync(ContentLengthPrefix, cancellationToken);
        await _outputStream.WriteAsync(contentLength, cancellationToken);
        await _outputStream.WriteAsync(HeaderEnd, cancellationToken);

        // Write JPEG data
        await _outputStream.WriteAsync(sample.Data, cancellationToken);

        frameCount++;
    }

    public override async ValueTask WriteTrailerAsync(CancellationToken cancellationToken = default)
    {
        // Write final boundary to signal end of stream
        await _outputStream.WriteAsync(FinalBoundary, cancellationToken);
        await _outputStream.FlushAsync(cancellationToken);
    }
}

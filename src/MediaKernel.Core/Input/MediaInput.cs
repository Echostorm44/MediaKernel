// MediaKernel - Media Input Layer
// Provides unified access to various media sources (file, HTTP, etc.)

using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Input;

/// <summary>
/// Type of media source.
/// </summary>
public enum MediaSourceType
{
    /// <summary>Unknown or auto-detect.</summary>
    Unknown,
    /// <summary>Local file.</summary>
    File,
    /// <summary>HTTP or HTTPS URL.</summary>
    Http,
    /// <summary>FTP URL.</summary>
    Ftp,
    /// <summary>Memory buffer.</summary>
    Memory,
    /// <summary>Pipe or stdin.</summary>
    Pipe
}

/// <summary>
/// Information about a media source.
/// </summary>
public readonly struct MediaSourceInfo
{
    /// <summary>Original URI or path.</summary>
    public string Uri { get; init; }
    
    /// <summary>Type of source.</summary>
    public MediaSourceType Type { get; init; }
    
    /// <summary>Total size in bytes, or -1 if unknown (streaming).</summary>
    public long Size { get; init; }
    
    /// <summary>Whether seeking is supported.</summary>
    public bool CanSeek { get; init; }
    
    /// <summary>Whether the source is live (no seeking, no size).</summary>
    public bool IsLive { get; init; }
    
    /// <summary>Content type if known (e.g., "video/mp4").</summary>
    public string? ContentType { get; init; }
    
    /// <summary>Detected format based on extension or content sniffing.</summary>
    public string? DetectedFormat { get; init; }
}

/// <summary>
/// Abstract base for media input sources.
/// </summary>
public abstract class MediaInput : IDisposable, IAsyncDisposable
{
    /// <summary>Gets source information.</summary>
    public abstract MediaSourceInfo Info { get; }
    
    /// <summary>Current read position.</summary>
    public abstract long Position { get; }
    
    /// <summary>Whether the input is open and ready.</summary>
    public abstract bool IsOpen { get; }
    
    /// <summary>Open the input source.</summary>
    public abstract ValueTask OpenAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Read data into buffer.</summary>
    /// <returns>Number of bytes read, or 0 at end of stream.</returns>
    public abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    
    /// <summary>Seek to position if supported.</summary>
    public abstract ValueTask SeekAsync(long position, CancellationToken cancellationToken = default);
    
    /// <summary>Read all remaining data (use with caution on large files).</summary>
    public async ValueTask<byte[]> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (Info.Size > 0 && Info.Size < 100_000_000) // 100MB limit
        {
            byte[] data = new byte[Info.Size];
            int totalRead = 0;
            while (totalRead < data.Length)
            {
                int read = await ReadAsync(data.AsMemory(totalRead), cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }
            return data.AsSpan(0, totalRead).ToArray();
        }
        
        // Unknown size - use growing buffer
        using var stream = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await ReadAsync(buffer, cancellationToken)) > 0)
            {
                stream.Write(buffer, 0, read);
            }
            return stream.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    /// <summary>Get a Stream wrapper for compatibility.</summary>
    public virtual Stream AsStream() => new MediaInputStream(this);
    
    public abstract void Dispose();
    public abstract ValueTask DisposeAsync();
    
    /// <summary>
    /// Create appropriate input for a URI.
    /// </summary>
    public static MediaInput Create(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            throw new ArgumentNullException(nameof(uri));
        
        // Check for URI scheme
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpMediaInput(uri);
        }
        
        if (uri.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("FTP not yet implemented");
        }
        
        // Assume file path
        return new FileMediaInput(uri);
    }
    
    /// <summary>
    /// Create input from existing memory.
    /// </summary>
    public static MediaInput FromMemory(byte[] data, string? name = null)
    {
        return new MemoryMediaInput(data, name ?? "memory");
    }
    
    /// <summary>
    /// Create input from existing stream.
    /// </summary>
    public static MediaInput FromStream(Stream stream, string? name = null)
    {
        return new StreamMediaInput(stream, name ?? "stream");
    }
}

/// <summary>
/// File-based media input.
/// </summary>
public sealed class FileMediaInput : MediaInput
{
    private readonly string filePath;
    private FileStream? stream;
    private MediaSourceInfo info;
    
    public FileMediaInput(string path)
    {
        filePath = path ?? throw new ArgumentNullException(nameof(path));
    }
    
    public override MediaSourceInfo Info => info;
    public override long Position => stream?.Position ?? 0;
    public override bool IsOpen => stream != null;
    
    public override ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (stream != null)
            return ValueTask.CompletedTask;
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Media file not found", filePath);
        
        var fileInfo = new FileInfo(filePath);
        stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 
                                bufferSize: 81920, useAsync: true);
        
        info = new MediaSourceInfo
        {
            Uri = filePath,
            Type = MediaSourceType.File,
            Size = fileInfo.Length,
            CanSeek = true,
            IsLive = false,
            ContentType = GetContentType(filePath),
            DetectedFormat = DetectFormat(filePath)
        };
        
        return ValueTask.CompletedTask;
    }
    
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (stream == null)
            throw new InvalidOperationException("Input not open");
        
        return stream.ReadAsync(buffer, cancellationToken);
    }
    
    public override ValueTask SeekAsync(long position, CancellationToken cancellationToken = default)
    {
        if (stream == null)
            throw new InvalidOperationException("Input not open");
        
        stream.Position = position;
        return ValueTask.CompletedTask;
    }
    
    public override void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
    
    public override async ValueTask DisposeAsync()
    {
        if (stream != null)
        {
            await stream.DisposeAsync();
            stream = null;
        }
    }
    
    private static string? GetContentType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".flv" => "video/x-flv",
            ".ts" or ".mts" or ".m2ts" => "video/mp2t",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" or ".oga" => "audio/ogg",
            ".wav" => "audio/wav",
            ".wma" => "audio/x-ms-wma",
            ".opus" => "audio/opus",
            _ => null
        };
    }
    
    private static string? DetectFormat(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" or ".m4a" => "mp4",
            ".mkv" or ".mka" or ".webm" => "matroska",
            ".avi" => "avi",
            ".mov" => "mov",
            ".flv" => "flv",
            ".ts" or ".mts" or ".m2ts" => "mpegts",
            ".mp3" => "mp3",
            ".flac" => "flac",
            ".ogg" or ".oga" or ".ogv" => "ogg",
            ".wav" => "wav",
            _ => null
        };
    }
}

/// <summary>
/// HTTP-based media input with range request support.
/// </summary>
public sealed class HttpMediaInput : MediaInput
{
    private static readonly HttpClient SharedClient;
    
    static HttpMediaInput()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = true
        };
        SharedClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        SharedClient.DefaultRequestHeaders.UserAgent.ParseAdd("MediaKernel/1.0");
    }
    
    private readonly string url;
    private MediaSourceInfo info;
    private long currentPosition;
    private Stream? responseStream;
    private HttpResponseMessage? currentResponse;
    private bool isOpen;

    // ICY (Shoutcast/Icecast) in-band metadata. The server interleaves a short block into the
    // audio every MetadataInterval bytes; those bytes are stripped here so that everything above
    // sees a clean elementary stream.
    private readonly IcyMetadata icy = new();
    private int bytesUntilMetadata;
    private byte[]? metadataBuffer;

    public HttpMediaInput(string url)
    {
        this.url = url ?? throw new ArgumentNullException(nameof(url));
    }
    
    public override MediaSourceInfo Info => info;
    public override long Position => currentPosition;
    public override bool IsOpen => isOpen;

    /// <summary>
    /// Station details and now-playing text, populated when the server answers with ICY headers.
    /// Always non-null; check <see cref="IcyMetadata.IsIcyStream"/> to tell a radio stream from an
    /// ordinary HTTP file.
    /// </summary>
    public IcyMetadata Icy => icy;

    /// <summary>
    /// Raised when the station announces a new track. Fires on the reading thread, so handlers
    /// should not block - a slow handler stalls playback.
    /// </summary>
    public event EventHandler<StreamTitleChangedEventArgs>? StreamTitleChanged;

    public override async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (isOpen)
            return;
        
        // Probe with HEAD for size and range support. Radio servers commonly reject or ignore
        // HEAD, and a live stream has neither a length nor seeking anyway, so a failure here is
        // not fatal - fall through to the GET and let it describe the stream.
        long size = -1;
        bool canSeek = false;
        string? contentType = null;
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await SharedClient.SendAsync(headRequest, cancellationToken);
            if (headResponse.IsSuccessStatusCode)
            {
                size = headResponse.Content.Headers.ContentLength ?? -1;
                canSeek = headResponse.Headers.AcceptRanges.Contains("bytes");
                contentType = headResponse.Content.Headers.ContentType?.MediaType;
            }
        }
        catch (HttpRequestException)
        {
            // Treated as "the server will not tell us"; the GET below still has to succeed.
        }
        
        info = new MediaSourceInfo
        {
            Uri = url,
            Type = MediaSourceType.Http,
            Size = size,
            CanSeek = canSeek,
            IsLive = size < 0,
            ContentType = contentType,
            DetectedFormat = DetectFormatFromContentType(contentType) ?? DetectFormatFromUrl(url)
        };
        
        // Open GET stream at position 0
        await OpenStreamAtPositionAsync(0, cancellationToken);

        // The GET is authoritative for anything HEAD could not supply, and it is the only request
        // that carries the ICY headers.
        if (currentResponse is not null)
        {
            ReadIcyHeaders(currentResponse);

            contentType ??= currentResponse.Content.Headers.ContentType?.MediaType;
            if (size < 0)
                size = currentResponse.Content.Headers.ContentLength ?? -1;

            // A stream carrying in-band metadata is live by definition, and its byte offsets are
            // meaningless to a range request.
            if (icy.MetadataInterval > 0)
            {
                canSeek = false;
                size = -1;
            }

            info = info with
            {
                Size = size,
                CanSeek = canSeek,
                IsLive = size < 0,
                ContentType = contentType,
                DetectedFormat = DetectFormatFromContentType(contentType) ?? DetectFormatFromUrl(url)
            };
        }

        isOpen = true;
    }
    
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (responseStream == null)
            throw new InvalidOperationException("Input not open");

        if (icy.MetadataInterval <= 0)
        {
            int plain = await responseStream.ReadAsync(buffer, cancellationToken);
            currentPosition += plain;
            return plain;
        }

        // Consume any metadata block sitting at the read position, then hand back audio only up to
        // the next block boundary. Returning short is fine and keeps this free of buffering: the
        // caller loops, as every Stream consumer must.
        while (bytesUntilMetadata == 0)
        {
            if (!await ConsumeMetadataBlockAsync(cancellationToken))
                return 0;
        }

        int want = Math.Min(buffer.Length, bytesUntilMetadata);
        int read = await responseStream.ReadAsync(buffer[..want], cancellationToken);
        bytesUntilMetadata -= read;
        currentPosition += read;
        return read;
    }
    
    public override async ValueTask SeekAsync(long position, CancellationToken cancellationToken = default)
    {
        if (!info.CanSeek)
            throw new NotSupportedException("HTTP source does not support seeking");
        
        if (position == currentPosition)
            return;
        
        // Close current stream and open new one at position
        await CloseCurrentStreamAsync();
        await OpenStreamAtPositionAsync(position, cancellationToken);
        currentPosition = position;
    }
    
    private async Task OpenStreamAtPositionAsync(long position, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Ask for in-band metadata. Servers that know nothing about ICY ignore this header, so it
        // is safe to send on every request.
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        if (position > 0)
        {
            request.Headers.Range = new RangeHeaderValue(position, null);
        }
        
        currentResponse = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        currentResponse.EnsureSuccessStatusCode();
        responseStream = await currentResponse.Content.ReadAsStreamAsync(cancellationToken);
    }
    
    /// <summary>
    /// Reads the ICY station headers from a response and arms metadata stripping.
    /// </summary>
    private void ReadIcyHeaders(HttpResponseMessage response)
    {
        icy.StationName = FirstHeader(response, "icy-name");
        icy.Genre = FirstHeader(response, "icy-genre");
        icy.Description = FirstHeader(response, "icy-description");
        icy.StationUrl = FirstHeader(response, "icy-url");

        if (int.TryParse(FirstHeader(response, "icy-br"), out int bitrate))
            icy.BitrateKbps = bitrate;

        // Only strip metadata if the server actually agreed to send it.
        if (int.TryParse(FirstHeader(response, "icy-metaint"), out int interval) && interval > 0)
        {
            icy.MetadataInterval = interval;
            bytesUntilMetadata = interval;
            metadataBuffer = new byte[16 * 255]; // the length byte counts 16-byte units, max 255
        }
        else
        {
            icy.MetadataInterval = 0;
            bytesUntilMetadata = 0;
            metadataBuffer = null;
        }
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (string value in values)
                return value;
        }

        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            foreach (string value in contentValues)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Reads one metadata block and rearms the interval. Returns false at end of stream.
    /// </summary>
    /// <remarks>
    /// A block is one length byte counting 16-byte units, then that many bytes. Length zero is by
    /// far the common case - stations resend the title only when it changes - so this stays cheap.
    /// </remarks>
    private async Task<bool> ConsumeMetadataBlockAsync(CancellationToken cancellationToken)
    {
        byte[] lengthByte = new byte[1];
        if (!await ReadExactlyAsync(lengthByte, cancellationToken))
            return false;

        int length = lengthByte[0] * 16;
        bytesUntilMetadata = icy.MetadataInterval;

        if (length == 0 || metadataBuffer is null)
            return true;

        var block = metadataBuffer.AsMemory(0, length);
        if (!await ReadExactlyAsync(block, cancellationToken))
            return false;

        ApplyMetadataBlock(block.Span);
        return true;
    }

    private void ApplyMetadataBlock(ReadOnlySpan<byte> block)
    {
        // Trailing padding is NUL bytes. Stations are inconsistent about encoding; UTF-8 is the
        // modern norm and turns Latin-1-only text into replacement characters rather than failing,
        // which is the right trade for something that must never interrupt playback.
        int end = block.IndexOf((byte)0);
        if (end >= 0)
            block = block[..end];
        if (block.IsEmpty)
            return;

        string text = System.Text.Encoding.UTF8.GetString(block);

        var fields = IcyMetadata.ParseBlock(text);
        fields.TryGetValue("StreamTitle", out string? title);
        fields.TryGetValue("StreamUrl", out string? streamUrl);

        if (streamUrl is not null)
            icy.StreamUrl = streamUrl;

        // Stations resend the same title routinely; only surface real changes.
        if (title is null || title == icy.StreamTitle)
            return;

        icy.StreamTitle = title;
        StreamTitleChanged?.Invoke(this, new StreamTitleChangedEventArgs(title, icy.StreamUrl));
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = await responseStream!.ReadAsync(destination[filled..], cancellationToken);
            if (read == 0)
                return false;
            filled += read;
        }
        return true;
    }

    private async Task CloseCurrentStreamAsync()
    {
        if (responseStream != null)
        {
            await responseStream.DisposeAsync();
            responseStream = null;
        }
        currentResponse?.Dispose();
        currentResponse = null;
    }
    
    public override void Dispose()
    {
        responseStream?.Dispose();
        currentResponse?.Dispose();
        isOpen = false;
    }
    
    public override async ValueTask DisposeAsync()
    {
        await CloseCurrentStreamAsync();
        isOpen = false;
    }
    
    private static string? DetectFormatFromContentType(string? contentType)
    {
        return contentType switch
        {
            "video/mp4" => "mp4",
            "video/x-matroska" => "matroska",
            "video/webm" => "matroska",
            "video/x-msvideo" => "avi",
            "video/mp2t" => "mpegts",
            "video/x-flv" => "flv",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "mp4",
            "audio/flac" => "flac",
            "audio/ogg" => "ogg",
            "application/vnd.apple.mpegurl" => "hls",
            "application/dash+xml" => "dash",
            _ => null
        };
    }
    
    private static string? DetectFormatFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".m4v" => "mp4",
                ".mkv" or ".webm" => "matroska",
                ".m3u8" => "hls",
                ".mpd" => "dash",
                ".ts" => "mpegts",
                ".flv" => "flv",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Memory-based media input.
/// </summary>
public sealed class MemoryMediaInput : MediaInput
{
    private readonly byte[] data;
    private readonly string name;
    private int position;
    private MediaSourceInfo info;
    
    public MemoryMediaInput(byte[] data, string name)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        this.name = name;
        info = new MediaSourceInfo
        {
            Uri = name,
            Type = MediaSourceType.Memory,
            Size = data.Length,
            CanSeek = true,
            IsLive = false
        };
    }
    
    public override MediaSourceInfo Info => info;
    public override long Position => position;
    public override bool IsOpen => true;
    
    public override ValueTask OpenAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
    
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int available = data.Length - position;
        int toRead = Math.Min(buffer.Length, available);
        
        if (toRead <= 0)
            return ValueTask.FromResult(0);
        
        data.AsSpan(position, toRead).CopyTo(buffer.Span);
        position += toRead;
        return ValueTask.FromResult(toRead);
    }
    
    public override ValueTask SeekAsync(long newPosition, CancellationToken cancellationToken = default)
    {
        position = (int)Math.Clamp(newPosition, 0, data.Length);
        return ValueTask.CompletedTask;
    }
    
    public override void Dispose() { }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Stream wrapper for existing streams.
/// </summary>
public sealed class StreamMediaInput : MediaInput
{
    private readonly Stream stream;
    private readonly string name;
    private readonly bool ownsStream;
    private MediaSourceInfo info;
    
    public StreamMediaInput(Stream stream, string name, bool ownsStream = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.name = name;
        this.ownsStream = ownsStream;
        
        info = new MediaSourceInfo
        {
            Uri = name,
            Type = MediaSourceType.Pipe,
            Size = stream.CanSeek ? stream.Length : -1,
            CanSeek = stream.CanSeek,
            IsLive = !stream.CanSeek
        };
    }
    
    public override MediaSourceInfo Info => info;
    public override long Position => stream.CanSeek ? stream.Position : -1;
    public override bool IsOpen => stream.CanRead;
    
    public override ValueTask OpenAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
    
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => stream.ReadAsync(buffer, cancellationToken);
    
    public override ValueTask SeekAsync(long position, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
            throw new NotSupportedException("Stream does not support seeking");
        
        stream.Position = position;
        return ValueTask.CompletedTask;
    }
    
    public override void Dispose()
    {
        if (ownsStream)
            stream.Dispose();
    }
    
    public override async ValueTask DisposeAsync()
    {
        if (ownsStream)
            await stream.DisposeAsync();
    }
}

/// <summary>
/// Stream adapter for MediaInput.
/// </summary>
internal sealed class MediaInputStream : Stream
{
    private readonly MediaInput input;
    
    public MediaInputStream(MediaInput input)
    {
        this.input = input;
    }
    
    public override bool CanRead => input.IsOpen;
    public override bool CanSeek => input.Info.CanSeek;
    public override bool CanWrite => false;
    public override long Length => input.Info.Size;
    
    public override long Position
    {
        get => input.Position;
        set => input.SeekAsync(value).AsTask().Wait();
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        return input.ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;
    }
    
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await input.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }
    
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return await input.ReadAsync(buffer, cancellationToken);
    }
    
    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => input.Position + offset,
            SeekOrigin.End => input.Info.Size + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        input.SeekAsync(newPos).AsTask().Wait();
        return input.Position;
    }
    
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

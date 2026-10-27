// Stream filter that automatically decompresses gzip/zlib/deflate streams
// Uses .NET's built-in compression - no external dependencies

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace MediaKernel.Core.IO;

/// <summary>
/// Compression format detected in stream.
/// </summary>
public enum CompressionFormat
{
    /// <summary>No compression or unknown format.</summary>
    None,
    
    /// <summary>Raw deflate (RFC 1951).</summary>
    Deflate,
    
    /// <summary>Zlib wrapper around deflate (RFC 1950).</summary>
    Zlib,
    
    /// <summary>Gzip wrapper around deflate (RFC 1952).</summary>
    Gzip
}

/// <summary>
/// Wraps a stream and automatically decompresses gzip or zlib content.
/// Forward-only (non-seekable) due to decompression nature.
/// </summary>
public sealed class DecompressingStream : Stream
{
    private readonly Stream source;
    private readonly Stream decompressor;
    private readonly bool leaveOpen;
    private bool disposed;
    
    /// <summary>Detected compression format.</summary>
    public CompressionFormat Format { get; }
    
    private DecompressingStream(Stream source, Stream decompressor, CompressionFormat format, bool leaveOpen)
    {
        this.source = source;
        this.decompressor = decompressor;
        this.Format = format;
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>
    /// Wraps a stream with automatic decompression if compressed content is detected.
    /// Returns null if no compression detected (use original stream).
    /// </summary>
    public static DecompressingStream? Create(Stream source, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        
        if (!source.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(source));
        
        var format = DetectFormat(source);
        if (format == CompressionFormat.None)
            return null;
        
        Stream decompressor = format switch
        {
            CompressionFormat.Gzip => new GZipStream(source, CompressionMode.Decompress, leaveOpen),
            CompressionFormat.Zlib => CreateZlibStream(source, leaveOpen),
            CompressionFormat.Deflate => new DeflateStream(source, CompressionMode.Decompress, leaveOpen),
            _ => throw new InvalidOperationException("Unexpected format")
        };
        
        return new DecompressingStream(source, decompressor, format, leaveOpen);
    }
    
    /// <summary>
    /// Creates a decompressing stream for the specified format (no auto-detection).
    /// </summary>
    public static DecompressingStream Create(Stream source, CompressionFormat format, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        
        if (format == CompressionFormat.None)
            throw new ArgumentException("Must specify a compression format", nameof(format));
        
        Stream decompressor = format switch
        {
            CompressionFormat.Gzip => new GZipStream(source, CompressionMode.Decompress, leaveOpen),
            CompressionFormat.Zlib => CreateZlibStream(source, leaveOpen),
            CompressionFormat.Deflate => new DeflateStream(source, CompressionMode.Decompress, leaveOpen),
            _ => throw new ArgumentException("Invalid format", nameof(format))
        };
        
        return new DecompressingStream(source, decompressor, format, leaveOpen);
    }
    
    private static Stream CreateZlibStream(Stream source, bool leaveOpen)
    {
        // .NET 6+ has ZLibStream
        return new ZLibStream(source, CompressionMode.Decompress, leaveOpen);
    }
    
    /// <summary>
    /// Detects the compression format from stream header bytes.
    /// Stream position is preserved.
    /// </summary>
    public static CompressionFormat DetectFormat(Stream source)
    {
        if (!source.CanRead)
            return CompressionFormat.None;
        
        long originalPosition = source.CanSeek ? source.Position : -1;
        
        Span<byte> header = stackalloc byte[2];
        int read = source.Read(header);
        
        // Restore position if possible
        if (source.CanSeek)
            source.Position = originalPosition;
        
        if (read < 2)
            return CompressionFormat.None;
        
        return DetectFormat(header);
    }
    
    /// <summary>
    /// Detects the compression format from header bytes.
    /// </summary>
    public static CompressionFormat DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
            return CompressionFormat.None;
        
        // Gzip: 0x1F 0x8B magic bytes
        if (header[0] == 0x1F && header[1] == 0x8B)
            return CompressionFormat.Gzip;
        
        // Zlib: CMF byte + FLG byte with valid checksum
        // CMF: low 4 bits = compression method (8 = deflate)
        //      high 4 bits = compression info (window size, must be <= 7)
        // FLG: (CMF * 256 + FLG) % 31 == 0
        byte cmf = header[0];
        byte flg = header[1];
        
        int compressionMethod = cmf & 0x0F;
        int compressionInfo = cmf >> 4;
        
        if (compressionMethod == 8 && compressionInfo <= 7)
        {
            int check = (cmf << 8) | flg;
            if (check % 31 == 0)
                return CompressionFormat.Zlib;
        }
        
        return CompressionFormat.None;
    }
    
    #region Stream Implementation
    
    public override bool CanRead => !disposed && decompressor.CanRead;
    public override bool CanSeek => false; // Decompression is forward-only
    public override bool CanWrite => false;
    
    public override long Length => throw new NotSupportedException("Decompressed length unknown");
    
    public override long Position
    {
        get => throw new NotSupportedException("Position not supported for decompressing stream");
        set => throw new NotSupportedException("Position not supported for decompressing stream");
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return decompressor.Read(buffer, offset, count);
    }
    
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return decompressor.Read(buffer);
    }
    
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("Seek not supported for decompressing stream");
    
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            if (disposing)
            {
                decompressor.Dispose();
                // Note: decompressor already handles source based on leaveOpen
            }
        }
        base.Dispose(disposing);
    }
    
    #endregion
}

/// <summary>
/// Static utilities for detecting and decompressing data.
/// </summary>
public static class CompressionDetector
{
    /// <summary>
    /// Checks if data appears to be gzip compressed.
    /// </summary>
    public static bool IsGzip(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
    
    /// <summary>
    /// Checks if data appears to be zlib compressed.
    /// </summary>
    public static bool IsZlib(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;
        
        byte cmf = data[0];
        byte flg = data[1];
        
        int compressionMethod = cmf & 0x0F;
        int compressionInfo = cmf >> 4;
        
        if (compressionMethod != 8 || compressionInfo > 7)
            return false;
        
        int check = (cmf << 8) | flg;
        return check % 31 == 0;
    }
    
    /// <summary>
    /// Decompresses gzip data to a byte array.
    /// </summary>
    public static byte[] DecompressGzip(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
    
    /// <summary>
    /// Decompresses zlib data to a byte array.
    /// </summary>
    public static byte[] DecompressZlib(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
    
    /// <summary>
    /// Decompresses raw deflate data to a byte array.
    /// </summary>
    public static byte[] DecompressDeflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
    
    /// <summary>
    /// Compresses data using gzip.
    /// </summary>
    public static byte[] CompressGzip(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, level, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }
    
    /// <summary>
    /// Compresses data using zlib.
    /// </summary>
    public static byte[] CompressZlib(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }
    
    /// <summary>
    /// Compresses data using raw deflate.
    /// </summary>
    public static byte[] CompressDeflate(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }
}

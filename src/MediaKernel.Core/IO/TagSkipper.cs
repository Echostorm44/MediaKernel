// Stream filter that automatically skips ID3v2 and APEv2 tags at stream beginning
// Essential for audio playback to prevent metadata from being misinterpreted as content

using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;

namespace MediaKernel.Core.IO;

/// <summary>
/// Tag information extracted from audio stream headers.
/// </summary>
public readonly struct AudioTagInfo
{
    /// <summary>Type of tag found.</summary>
    public AudioTagType Type { get; init; }
    
    /// <summary>Tag version (major.minor format, e.g., 2.4 for ID3v2.4).</summary>
    public Version Version { get; init; }
    
    /// <summary>Raw tag data including header.</summary>
    public ReadOnlyMemory<byte> RawData { get; init; }
    
    /// <summary>Offset in original stream where tag started.</summary>
    public long Offset { get; init; }
    
    /// <summary>Total size of tag including header and footer.</summary>
    public int Size { get; init; }
}

/// <summary>
/// Types of audio metadata tags that can be detected.
/// </summary>
public enum AudioTagType
{
    /// <summary>ID3v2.x tag (ID3v2.2, 2.3, 2.4).</summary>
    Id3v2,
    
    /// <summary>APE tag (APEv1 or APEv2).</summary>
    Ape
}

/// <summary>
/// Wraps a stream and automatically skips ID3v2 and APEv2 tags at the beginning.
/// Tags are preserved and accessible via the Tags property.
/// </summary>
public sealed class TagSkippingStream : Stream
{
    private readonly Stream source;
    private readonly bool leaveOpen;
    private readonly long contentStart;
    private long position;
    
    /// <summary>Tags found and skipped at the beginning of the stream.</summary>
    public IReadOnlyList<AudioTagInfo> Tags { get; }
    
    /// <summary>Total bytes skipped from tag headers.</summary>
    public long SkippedBytes => contentStart;
    
    private TagSkippingStream(Stream source, bool leaveOpen, long contentStart, List<AudioTagInfo> tags)
    {
        this.source = source;
        this.leaveOpen = leaveOpen;
        this.contentStart = contentStart;
        this.Tags = tags;
        position = 0;
    }
    
    /// <summary>
    /// Creates a TagSkippingStream that automatically skips any ID3v2/APE tags.
    /// If no tags are found, returns null (use original stream).
    /// </summary>
    public static TagSkippingStream? Create(Stream source, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        
        if (!source.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(source));
        
        var tags = new List<AudioTagInfo>();
        long totalSkipped = 0;
        
        const int maxTags = 16;
        const int maxTagSize = 1 << 21; // 2MB max per tag
        
        while (tags.Count < maxTags)
        {
            var tagInfo = TryDetectTag(source, totalSkipped, maxTagSize);
            if (tagInfo == null)
                break;
            
            tags.Add(tagInfo.Value);
            totalSkipped += tagInfo.Value.Size;
            
            // Seek past the tag
            source.Position = totalSkipped;
        }
        
        if (totalSkipped == 0)
            return null;
        
        return new TagSkippingStream(source, leaveOpen, totalSkipped, tags);
    }
    
    private static AudioTagInfo? TryDetectTag(Stream source, long offset, int maxSize)
    {
        source.Position = offset;
        
        // Try ID3v2 first (10-byte header)
        Span<byte> header = stackalloc byte[32];
        int read = source.Read(header[..10]);
        if (read < 10)
            return null;
        
        // ID3v2 detection: "ID3" magic
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            byte version = header[3];
            byte revision = header[4];
            byte flags = header[5];
            bool hasFooter = (flags & 0x10) != 0;
            
            // Syncsafe integer (7 bits per byte)
            int size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            int totalSize = 10 + size + (hasFooter ? 10 : 0);
            
            if (totalSize > maxSize)
            {
                // Tag too large - skip without storing
                return new AudioTagInfo
                {
                    Type = AudioTagType.Id3v2,
                    Version = new Version(2, version, revision, 0),
                    Offset = offset,
                    Size = totalSize,
                    RawData = ReadOnlyMemory<byte>.Empty
                };
            }
            
            // Read full tag
            source.Position = offset;
            byte[] rawData = new byte[totalSize];
            if (source.Read(rawData) < totalSize)
                return null;
            
            return new AudioTagInfo
            {
                Type = AudioTagType.Id3v2,
                Version = new Version(2, version, revision, 0),
                Offset = offset,
                Size = totalSize,
                RawData = rawData
            };
        }
        
        // APE tag detection: "APETAGEX" magic (need 32 bytes)
        source.Position = offset;
        read = source.Read(header);
        if (read < 32)
            return null;
        
        if (header[0] == 'A' && header[1] == 'P' && header[2] == 'E' &&
            header[3] == 'T' && header[4] == 'A' && header[5] == 'G' &&
            header[6] == 'E' && header[7] == 'X')
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (version != 1000 && version != 2000)
                return null;
            
            uint tagSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            
            int totalSize = (int)tagSize;
            
            if (version >= 2000)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
                
                // Bit 29: tag contains header
                if ((flags & (1u << 29)) == 0)
                    return null;
                
                // Bit 30: tag contains footer (add header size)
                if ((flags & (1u << 30)) != 0)
                {
                    if (tagSize > uint.MaxValue - 32)
                        return null;
                    totalSize = (int)tagSize + 32;
                }
            }
            
            if (totalSize > maxSize)
            {
                return new AudioTagInfo
                {
                    Type = AudioTagType.Ape,
                    Version = new Version((int)(version / 1000), 0),
                    Offset = offset,
                    Size = totalSize,
                    RawData = ReadOnlyMemory<byte>.Empty
                };
            }
            
            // Read full tag
            source.Position = offset;
            byte[] rawData = new byte[totalSize];
            if (source.Read(rawData) < totalSize)
                return null;
            
            return new AudioTagInfo
            {
                Type = AudioTagType.Ape,
                Version = new Version((int)(version / 1000), 0),
                Offset = offset,
                Size = totalSize,
                RawData = rawData
            };
        }
        
        return null;
    }
    
    #region Stream Implementation
    
    public override bool CanRead => true;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    
    public override long Length => source.Length - contentStart;
    
    public override long Position
    {
        get => position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            position = value;
            source.Position = contentStart + value;
        }
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        source.Position = contentStart + position;
        int read = source.Read(buffer, offset, count);
        position += read;
        return read;
    }
    
    public override int Read(Span<byte> buffer)
    {
        source.Position = contentStart + position;
        int read = source.Read(buffer);
        position += read;
        return read;
    }
    
    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };
        
        if (newPos < 0)
            throw new IOException("Seek before beginning of stream");
        
        position = newPos;
        return position;
    }
    
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => source.Flush();
    
    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
            source.Dispose();
        base.Dispose(disposing);
    }
    
    #endregion
}

/// <summary>
/// Static utilities for detecting and parsing audio metadata tags.
/// </summary>
public static class TagDetector
{
    /// <summary>
    /// Detects ID3v2 tag at the current stream position.
    /// </summary>
    /// <returns>Tag size including header/footer, or 0 if not found.</returns>
    public static int DetectId3v2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return 0;
        
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3')
            return 0;
        
        // Validate syncsafe integers (each byte must have bit 7 clear)
        if ((data[6] | data[7] | data[8] | data[9]) >= 0x80)
            return 0;
        
        byte flags = data[5];
        bool hasFooter = (flags & 0x10) != 0;
        
        int size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
        return 10 + size + (hasFooter ? 10 : 0);
    }
    
    /// <summary>
    /// Detects APE tag at the current stream position.
    /// </summary>
    /// <returns>Tag size including header/footer, or 0 if not found.</returns>
    public static int DetectApeTag(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            return 0;
        
        // Check magic "APETAGEX"
        ReadOnlySpan<byte> magic = "APETAGEX"u8;
        if (!data[..8].SequenceEqual(magic))
            return 0;
        
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (version != 1000 && version != 2000)
            return 0;
        
        uint tagSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        
        if (version >= 2000)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            
            // Bit 29: this is a header (not footer)
            if ((flags & (1u << 29)) == 0)
                return 0;
            
            // Bit 30: tag has footer - add 32 for header size
            if ((flags & (1u << 30)) != 0)
            {
                if (tagSize > int.MaxValue - 32)
                    return 0;
                return (int)tagSize + 32;
            }
        }
        
        return (int)tagSize;
    }
    
    /// <summary>
    /// Checks if data starts with any supported audio tag.
    /// </summary>
    public static bool HasTag(ReadOnlySpan<byte> data)
    {
        return DetectId3v2(data) > 0 || DetectApeTag(data) > 0;
    }
}

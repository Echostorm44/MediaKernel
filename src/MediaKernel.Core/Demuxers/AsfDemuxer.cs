// MediaKernel - ASF Demuxer
// Demuxer for Advanced Systems Format (WMV, WMA, ASF) containers
// Microsoft's container format for Windows Media

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// ASF object types.
/// </summary>
public enum AsfObjectType
{
    Unknown,
    Header,
    Data,
    SimpleIndex,
    Index,
    FileProperties,
    StreamProperties,
    HeaderExtension,
    CodecList,
    ContentDescription,
    ExtendedContentDescription,
    Metadata,
    Padding
}

/// <summary>
/// ASF stream type.
/// </summary>
public enum AsfStreamType
{
    Unknown,
    Audio,
    Video,
    Command,
    Binary
}

/// <summary>
/// Information about an ASF stream.
/// </summary>
public sealed class AsfStreamInfo
{
    /// <summary>Stream number (1-127).</summary>
    public int StreamNumber { get; init; }
    
    /// <summary>Stream type.</summary>
    public AsfStreamType Type { get; init; }
    
    /// <summary>Codec identifier (FourCC for video, format tag for audio).</summary>
    public uint CodecId { get; init; }
    
    /// <summary>Codec name.</summary>
    public string CodecName { get; init; } = string.Empty;
    
    /// <summary>Bitrate in bits per second.</summary>
    public uint Bitrate { get; init; }
    
    /// <summary>For video: width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>For video: height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>For audio: sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>For audio: number of channels.</summary>
    public int Channels { get; init; }
    
    /// <summary>For audio: bits per sample.</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Is this stream encrypted?</summary>
    public bool IsEncrypted { get; init; }
    
    /// <summary>Type-specific data (codec private data).</summary>
    public ReadOnlyMemory<byte> TypeSpecificData { get; init; }

    /// <summary>
    /// The four-character code the stream properties object carries - "WMV3", "WVC1", "MP4V"
    /// and so on. <see cref="CodecName"/> is a display name ("WMV9"), which nothing can be
    /// looked up by.
    /// </summary>
    public string FourCc { get; init; } = string.Empty;

    /// <summary>
    /// Out-of-band codec configuration: for video, whatever follows the 40-byte
    /// BITMAPINFOHEADER inside the type-specific data. For WMV3 that is a bare sequence
    /// header and for VC-1 a marker-delimited sequence header and entry point, and neither
    /// codec can be initialised without it.
    /// </summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; init; }
}

/// <summary>
/// Information about an ASF file.
/// </summary>
public sealed class AsfFileInfo
{
    /// <summary>Whether the file was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>File ID (GUID).</summary>
    public Guid FileId { get; init; }
    
    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }
    
    /// <summary>Creation date.</summary>
    public DateTime CreationDate { get; init; }
    
    /// <summary>Total number of data packets.</summary>
    public long DataPacketCount { get; init; }
    
    /// <summary>Play duration in 100-nanosecond units.</summary>
    public long PlayDuration { get; init; }
    
    /// <summary>Send duration in 100-nanosecond units.</summary>
    public long SendDuration { get; init; }
    
    /// <summary>Preroll time in milliseconds.</summary>
    public long PrerollMilliseconds { get; init; }
    
    /// <summary>Is this a broadcast stream?</summary>
    public bool IsBroadcast { get; init; }
    
    /// <summary>Is this file seekable?</summary>
    public bool IsSeekable { get; init; }
    
    /// <summary>Minimum data packet size.</summary>
    public int MinPacketSize { get; init; }
    
    /// <summary>Maximum data packet size.</summary>
    public int MaxPacketSize { get; init; }
    
    /// <summary>Maximum bitrate in bits per second.</summary>
    public uint MaxBitrate { get; init; }
    
    /// <summary>Title metadata.</summary>
    public string Title { get; init; } = string.Empty;
    
    /// <summary>Author metadata.</summary>
    public string Author { get; init; } = string.Empty;
    
    /// <summary>Copyright metadata.</summary>
    public string Copyright { get; init; } = string.Empty;
    
    /// <summary>Description metadata.</summary>
    public string Description { get; init; } = string.Empty;
    
    /// <summary>List of streams.</summary>
    public IReadOnlyList<AsfStreamInfo> Streams { get; init; } = Array.Empty<AsfStreamInfo>();
    
    /// <summary>Data section offset in file.</summary>
    public long DataOffset { get; init; }
    
    /// <summary>Data section size.</summary>
    public long DataSize { get; init; }
}

/// <summary>
/// An ASF data packet.
/// </summary>
public sealed class AsfPacket
{
    /// <summary>Packet number (0-based).</summary>
    public long PacketNumber { get; init; }
    
    /// <summary>Send time in 100-nanosecond units.</summary>
    public long SendTime { get; init; }
    
    /// <summary>Duration in 100-nanosecond units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Payloads in this packet.</summary>
    public IReadOnlyList<AsfPayload> Payloads { get; init; } = Array.Empty<AsfPayload>();
}

/// <summary>
/// A payload within an ASF packet.
/// </summary>
public sealed class AsfPayload
{
    /// <summary>Stream number.</summary>
    public int StreamNumber { get; init; }
    
    /// <summary>Is this a keyframe?</summary>
    public bool IsKeyframe { get; init; }
    
    /// <summary>Presentation time in 100-nanosecond units.</summary>
    public long PresentationTime { get; init; }
    
    /// <summary>Media object number.</summary>
    public int MediaObjectNumber { get; init; }
    
    /// <summary>Offset within the media object.</summary>
    public int OffsetIntoMediaObject { get; init; }
    
    /// <summary>Payload data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// One complete media object - a frame, which is what a decoder is given. An
/// <see cref="AsfPayload"/> is a fragment of one.
/// </summary>
public readonly struct AsfFrame
{
    /// <summary>The ASF stream number this frame belongs to; streams are numbered from 1.</summary>
    public int StreamNumber { get; init; }

    /// <summary>The frame. Valid until the next read from the same stream.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Presentation time in milliseconds, with the file's preroll already taken off.</summary>
    public long PresentationMilliseconds { get; init; }

    public bool IsKeyframe { get; init; }
}

/// <summary>
/// Simple index entry for seeking.
/// </summary>
public readonly struct AsfIndexEntry
{
    /// <summary>Packet number.</summary>
    public uint PacketNumber { get; init; }
    
    /// <summary>Packet count.</summary>
    public ushort PacketCount { get; init; }
}

/// <summary>
/// Demuxer for ASF (Advanced Systems Format) files.
/// Supports WMV, WMA, and ASF containers.
/// </summary>
public sealed class AsfDemuxer : IDisposable
{
    // ASF Header GUID: 75B22630-668E-11CF-A6D9-00AA0062CE6C
    private static readonly byte[] HeaderGuid = 
    {
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };
    
    // ASF Data GUID: 75B22636-668E-11CF-A6D9-00AA0062CE6C
    private static readonly byte[] DataGuid = 
    {
        0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };
    
    // File Properties GUID: 8CABDCA1-A947-11CF-8EE4-00C00C205365
    private static readonly byte[] FilePropertiesGuid = 
    {
        0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11,
        0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };
    
    // Stream Properties GUID: B7DC0791-A9B7-11CF-8EE6-00C00C205365
    private static readonly byte[] StreamPropertiesGuid = 
    {
        0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11,
        0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };
    
    // Content Description GUID: 75B22633-668E-11CF-A6D9-00AA0062CE6C
    private static readonly byte[] ContentDescriptionGuid = 
    {
        0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };
    
    // Simple Index GUID: 33000890-E5B1-11CF-89F4-00A0C90349CB
    private static readonly byte[] SimpleIndexGuid = 
    {
        0x90, 0x08, 0x00, 0x33, 0xB1, 0xE5, 0xCF, 0x11,
        0x89, 0xF4, 0x00, 0xA0, 0xC9, 0x03, 0x49, 0xCB
    };
    
    // Audio stream type GUID: F8699E40-5B4D-11CF-A8FD-00805F5C442B
    private static readonly byte[] AudioStreamGuid = 
    {
        0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11,
        0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
    };
    
    // Video stream type GUID: BC19EFC0-5B4D-11CF-A8FD-00805F5C442B
    private static readonly byte[] VideoStreamGuid = 
    {
        0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11,
        0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
    };
    
    private const int GuidSize = 16;
    private const int ObjectHeaderSize = 24; // GUID (16) + Size (8)
    private const int TicksPerMillisecond = 10_000;
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] buffer = new byte[4096];
    private readonly List<AsfStreamInfo> streams = new();
    private readonly List<AsfIndexEntry> indexEntries = new();
    
    private AsfFileInfo? fileInfo;
    private long dataOffset;
    private long dataSize;
    private long currentPacket;
    private int packetSize;
    private bool disposed;
    
    /// <summary>
    /// Creates a new ASF demuxer.
    /// </summary>
    public AsfDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>
    /// Checks if data starts with ASF header.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < GuidSize)
            return false;
        
        return header[..GuidSize].SequenceEqual(HeaderGuid);
    }
    
    /// <summary>
    /// Parses the ASF header and returns file info.
    /// </summary>
    public AsfFileInfo Parse()
    {
        if (fileInfo != null)
            return fileInfo;
        
        stream.Position = 0;
        
        // Read and validate header object
        if (stream.Read(buffer, 0, ObjectHeaderSize) < ObjectHeaderSize)
        {
            fileInfo = new AsfFileInfo { IsValid = false };
            return fileInfo;
        }
        
        if (!buffer.AsSpan(0, GuidSize).SequenceEqual(HeaderGuid))
        {
            fileInfo = new AsfFileInfo { IsValid = false };
            return fileInfo;
        }
        
        long headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(GuidSize));
        
        // Read number of header objects
        if (stream.Read(buffer, 0, 6) < 6)
        {
            fileInfo = new AsfFileInfo { IsValid = false };
            return fileInfo;
        }
        
        int objectCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        // byte reserved1 = buffer[4]; // Should be 0x01
        // byte reserved2 = buffer[5]; // Should be 0x02
        
        // Parse header sub-objects
        Guid fileId = Guid.Empty;
        long fileSize = 0;
        DateTime creationDate = DateTime.MinValue;
        long dataPacketCount = 0;
        long playDuration = 0;
        long sendDuration = 0;
        long prerollMs = 0;
        bool isBroadcast = false;
        bool isSeekable = false;
        int minPacketSize = 0;
        int maxPacketSize = 0;
        uint maxBitrate = 0;
        string title = string.Empty;
        string author = string.Empty;
        string copyright = string.Empty;
        string description = string.Empty;
        
        long headerEndPos = headerSize;
        
        for (int i = 0; i < objectCount && stream.Position < headerEndPos; i++)
        {
            long objectPos = stream.Position;
            
            if (stream.Read(buffer, 0, ObjectHeaderSize) < ObjectHeaderSize)
                break;
            
            ReadOnlySpan<byte> objectGuid = buffer.AsSpan(0, GuidSize);
            long objectSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(GuidSize));
            
            if (objectSize < ObjectHeaderSize)
                break;
            
            long objectDataSize = objectSize - ObjectHeaderSize;
            
            if (objectGuid.SequenceEqual(FilePropertiesGuid))
            {
                ParseFileProperties(objectDataSize, out fileId, out fileSize, out creationDate,
                    out dataPacketCount, out playDuration, out sendDuration, out prerollMs,
                    out isBroadcast, out isSeekable, out minPacketSize, out maxPacketSize, out maxBitrate);
                packetSize = maxPacketSize;
            }
            else if (objectGuid.SequenceEqual(StreamPropertiesGuid))
            {
                ParseStreamProperties(objectDataSize);
            }
            else if (objectGuid.SequenceEqual(ContentDescriptionGuid))
            {
                ParseContentDescription(objectDataSize, out title, out author, out copyright, out description);
            }
            else
            {
                // Skip unknown object
                stream.Position = objectPos + objectSize;
            }
        }
        
        // Find data object
        stream.Position = headerSize;
        
        if (stream.Read(buffer, 0, ObjectHeaderSize) >= ObjectHeaderSize)
        {
            if (buffer.AsSpan(0, GuidSize).SequenceEqual(DataGuid))
            {
                dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(GuidSize));
                
                // Read data header (16 bytes: GUID + packet count + reserved)
                if (stream.Read(buffer, 0, 26) >= 26)
                {
                    // Skip file GUID (16 bytes)
                    // Total data packets (8 bytes) - already have this from file properties
                    // Reserved (2 bytes)
                    dataOffset = stream.Position;
                }
            }
        }
        
        fileInfo = new AsfFileInfo
        {
            IsValid = true,
            FileId = fileId,
            FileSize = fileSize,
            CreationDate = creationDate,
            DataPacketCount = dataPacketCount,
            PlayDuration = playDuration,
            SendDuration = sendDuration,
            PrerollMilliseconds = prerollMs,
            IsBroadcast = isBroadcast,
            IsSeekable = isSeekable,
            MinPacketSize = minPacketSize,
            MaxPacketSize = maxPacketSize,
            MaxBitrate = maxBitrate,
            Title = title,
            Author = author,
            Copyright = copyright,
            Description = description,
            Streams = streams.ToArray(),
            DataOffset = dataOffset,
            DataSize = dataSize
        };
        
        return fileInfo;
    }
    
    private void ParseFileProperties(long objectDataSize, out Guid fileId, out long fileSize,
        out DateTime creationDate, out long dataPacketCount, out long playDuration,
        out long sendDuration, out long prerollMs, out bool isBroadcast, out bool isSeekable,
        out int minPacketSize, out int maxPacketSize, out uint maxBitrate)
    {
        // File Properties: 80 bytes minimum
        byte[] data = new byte[Math.Min(objectDataSize, 80)];
        int read = stream.Read(data, 0, data.Length);
        
        fileId = Guid.Empty;
        fileSize = 0;
        creationDate = DateTime.MinValue;
        dataPacketCount = 0;
        playDuration = 0;
        sendDuration = 0;
        prerollMs = 0;
        isBroadcast = false;
        isSeekable = false;
        minPacketSize = 0;
        maxPacketSize = 0;
        maxBitrate = 0;
        
        if (read < 80)
            return;
        
        fileId = new Guid(data.AsSpan(0, 16));
        fileSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16));
        
        // Creation date: FILETIME (100-ns intervals since Jan 1, 1601)
        long fileTime = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24));
        if (fileTime > 0)
        {
            try
            {
                creationDate = DateTime.FromFileTimeUtc(fileTime);
            }
            catch
            {
                creationDate = DateTime.MinValue;
            }
        }
        
        dataPacketCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32));
        playDuration = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40));
        sendDuration = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48));
        prerollMs = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(56));
        
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(64));
        isBroadcast = (flags & 0x01) != 0;
        isSeekable = (flags & 0x02) != 0;
        
        minPacketSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(68));
        maxPacketSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(72));
        maxBitrate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(76));
        
        // Skip remaining data if any
        if (objectDataSize > 80)
            stream.Position += objectDataSize - 80;
    }
    
    private void ParseStreamProperties(long objectDataSize)
    {
        // Stream Properties: at least 54 bytes + type-specific data
        if (objectDataSize < 54)
        {
            stream.Position += objectDataSize;
            return;
        }
        
        byte[] header = new byte[54];
        if (stream.Read(header, 0, 54) < 54)
            return;
        
        ReadOnlySpan<byte> streamTypeGuid = header.AsSpan(0, 16);
        // Error correction type GUID: bytes 16-31
        long timeOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        int typeSpecificDataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40));
        int errorCorrectionDataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
        ushort streamFlags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(48));
        // Reserved: bytes 50-53
        
        int streamNumber = streamFlags & 0x7F;
        bool isEncrypted = (streamFlags & 0x8000) != 0;
        
        AsfStreamType streamType = AsfStreamType.Unknown;
        if (streamTypeGuid.SequenceEqual(AudioStreamGuid))
            streamType = AsfStreamType.Audio;
        else if (streamTypeGuid.SequenceEqual(VideoStreamGuid))
            streamType = AsfStreamType.Video;
        
        // Read type-specific data
        byte[] typeSpecificData = new byte[typeSpecificDataLength];
        if (typeSpecificDataLength > 0)
            stream.Read(typeSpecificData, 0, typeSpecificDataLength);
        
        // Skip error correction data
        if (errorCorrectionDataLength > 0)
            stream.Position += errorCorrectionDataLength;
        
        // Parse stream-specific info
        int width = 0, height = 0;
        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        uint codecId = 0;
        byte[]? codecPrivate = null;
        
        if (streamType == AsfStreamType.Audio && typeSpecificDataLength >= 18)
        {
            // WAVEFORMATEX structure
            codecId = BinaryPrimitives.ReadUInt16LittleEndian(typeSpecificData);
            channels = BinaryPrimitives.ReadUInt16LittleEndian(typeSpecificData.AsSpan(2));
            sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(typeSpecificData.AsSpan(4));
            // Bytes per second: typeSpecificData[8..12]
            // Block align: typeSpecificData[12..14]
            bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(typeSpecificData.AsSpan(14));
        }
        else if (streamType == AsfStreamType.Video && typeSpecificDataLength >= 51)
        {
            // Video format: encoded width/height (4 bytes each), one reserved byte, a 2-byte
            // format data size, then the BITMAPINFOHEADER at offset 11.
            //
            //   11  biSize          4
            //   15  biWidth         4
            //   19  biHeight        4
            //   23  biPlanes        2
            //   25  biBitCount      2
            //   27  biCompression   4   (the FourCC)
            //   31  image size, resolutions, colours used and important - 20 bytes
            //   51  the codec's own configuration, biSize - 40 bytes of it
            //
            // The dimensions come from the BITMAPINFOHEADER rather than from the encoded pair
            // at offset 0, which is what asfdec_f.c reads; its own comment records files that
            // carry the wrong size in the ASF fields and the right one further in.
            int biSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(typeSpecificData.AsSpan(11));
            width = (int)BinaryPrimitives.ReadUInt32LittleEndian(typeSpecificData.AsSpan(15));
            height = (int)BinaryPrimitives.ReadUInt32LittleEndian(typeSpecificData.AsSpan(19));
            codecId = BinaryPrimitives.ReadUInt32LittleEndian(typeSpecificData.AsSpan(27));

            // biSize counts the 40-byte header plus the codec configuration after it, so
            // anything over 40 is configuration. Clamped to what the object actually holds:
            // the two lengths disagree in files written by more than one muxer.
            if (biSize > 40)
            {
                int extraSize = Math.Min(biSize - 40, typeSpecificDataLength - 51);
                if (extraSize > 0)
                    codecPrivate = typeSpecificData.AsSpan(51, extraSize).ToArray();
            }
        }
        
        streams.Add(new AsfStreamInfo
        {
            StreamNumber = streamNumber,
            Type = streamType,
            CodecId = codecId,
            CodecName = GetCodecName(streamType, codecId),
            FourCc = streamType == AsfStreamType.Video ? FourCcString(codecId) : string.Empty,
            CodecPrivate = codecPrivate,
            Width = width,
            Height = height,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            IsEncrypted = isEncrypted,
            TypeSpecificData = typeSpecificData
        });
        
        // Skip remaining data
        long remaining = objectDataSize - 54 - typeSpecificDataLength - errorCorrectionDataLength;
        if (remaining > 0)
            stream.Position += remaining;
    }
    
    /// <summary>A little-endian FourCC as the four characters it stands for.</summary>
    private static string FourCcString(uint codecId)
    {
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)(codecId & 0xFF);
        chars[1] = (char)((codecId >> 8) & 0xFF);
        chars[2] = (char)((codecId >> 16) & 0xFF);
        chars[3] = (char)((codecId >> 24) & 0xFF);

        return new string(chars).TrimEnd('\0');
    }

    private void ParseContentDescription(long objectDataSize, out string title, out string author,
        out string copyright, out string description)
    {
        title = string.Empty;
        author = string.Empty;
        copyright = string.Empty;
        description = string.Empty;
        
        if (objectDataSize < 10)
        {
            stream.Position += objectDataSize;
            return;
        }
        
        byte[] lengths = new byte[10];
        if (stream.Read(lengths, 0, 10) < 10)
            return;
        
        int titleLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths);
        int authorLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths.AsSpan(2));
        int copyrightLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths.AsSpan(4));
        int descriptionLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths.AsSpan(6));
        int ratingLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths.AsSpan(8));
        
        title = ReadUtf16String(titleLength);
        author = ReadUtf16String(authorLength);
        copyright = ReadUtf16String(copyrightLength);
        description = ReadUtf16String(descriptionLength);
        
        // Skip rating
        if (ratingLength > 0)
            stream.Position += ratingLength;
    }
    
    private string ReadUtf16String(int byteLength)
    {
        if (byteLength <= 0)
            return string.Empty;
        
        byte[] data = new byte[byteLength];
        if (stream.Read(data, 0, byteLength) < byteLength)
            return string.Empty;
        
        // Remove null terminator if present
        int actualLength = byteLength;
        if (byteLength >= 2 && data[byteLength - 2] == 0 && data[byteLength - 1] == 0)
            actualLength -= 2;
        
        return Encoding.Unicode.GetString(data, 0, actualLength);
    }
    
    private static string GetCodecName(AsfStreamType type, uint codecId)
    {
        if (type == AsfStreamType.Audio)
        {
            return codecId switch
            {
                0x0001 => "PCM",
                0x0002 => "ADPCM",
                0x0006 => "A-Law",
                0x0007 => "μ-Law",
                0x0055 => "MP3",
                0x00FF => "AAC",
                0x0160 => "WMA v1",
                0x0161 => "WMA v2",
                0x0162 => "WMA Pro",
                0x0163 => "WMA Lossless",
                0x0164 => "WMA Speech",
                0x1600 => "AAC LC",
                0x1601 => "AAC HE",
                0xA106 => "AAC HE v2",
                0xFFFE => "Extensible",
                _ => $"Audio 0x{codecId:X4}"
            };
        }
        else if (type == AsfStreamType.Video)
        {
            string fourcc = FourCcString(codecId);
            
            return fourcc.ToUpperInvariant() switch
            {
                "WMV1" => "WMV7",
                "WMV2" => "WMV8",
                "WMV3" => "WMV9",
                "WMVA" => "WMV Advanced",
                "WVC1" => "VC-1",
                "MP4V" => "MPEG-4",
                "H264" or "AVC1" => "H.264",
                "HEVC" or "HVC1" => "H.265",
                "VP80" => "VP8",
                "VP90" => "VP9",
                "MJPG" => "Motion JPEG",
                "DIV3" or "DIVX" => "DivX",
                "XVID" => "XviD",
                _ => fourcc
            };
        }
        
        return "Unknown";
    }
    
    /// <summary>
    /// Reads the next data packet.
    /// </summary>
    /// <summary>
    /// Reads the next data packet and the payload fragments in it.
    /// </summary>
    /// <remarks>
    /// A payload is a fragment of a media object, not a frame: one object can be spread over
    /// several packets and several objects can share one payload. <see cref="ReadNextFrame"/>
    /// is what hands out whole frames, and the two walk the same cursor, so a caller uses one
    /// or the other and not both.
    /// </remarks>
    public AsfPacket? ReadPacket()
    {
        if (fileInfo == null)
            Parse();

        if (fileInfo == null || !fileInfo.IsValid || packetSize <= 0 || dataOffset == 0)
            return null;

        long packetNumber = currentPacket;
        if (!GetPacket())
            return null;

        var payloads = new List<AsfPayload>();

        while (packetSizeLeft >= FrameHeaderSize && (packetSegments >= 1 || packetTimeStart != 0))
        {
            if (packetTimeStart == 0 && !ReadFrameHeader())
                break;

            int fragmentSize = packetFragSize;
            long timestamp = packetFragTimestamp;

            if (packetReplicSize == 1)
            {
                // Compressed payload: each object inside carries its own length byte.
                timestamp = packetTimeStart;
                packetTimeStart += packetTimeDelta;
                fragmentSize = currentPacketData![at++];
                packetSizeLeft--;
                packetMultiSize--;

                if (packetMultiSize < fragmentSize)
                {
                    packetTimeStart = 0;
                    at += packetMultiSize;
                    packetSizeLeft -= packetMultiSize;
                    continue;
                }

                packetMultiSize -= fragmentSize;
            }

            packetSizeLeft -= fragmentSize;
            if (packetSizeLeft < 0 || at + fragmentSize > currentPacketData!.Length)
                break;

            var data = new byte[fragmentSize];
            Array.Copy(currentPacketData, at, data, 0, fragmentSize);
            at += fragmentSize;

            payloads.Add(new AsfPayload
            {
                StreamNumber = packetStreamNumber,
                IsKeyframe = packetKeyFrame,
                PresentationTime = (timestamp - fileInfo.PrerollMilliseconds) * TicksPerMillisecond,
                MediaObjectNumber = packetSeq,
                OffsetIntoMediaObject = packetFragOffset,
                Data = data,
            });
        }

        return new AsfPacket
        {
            PacketNumber = packetNumber,
            SendTime = packetTimestamp * TicksPerMillisecond,
            Duration = 0,
            Payloads = payloads,
        };
    }

    // ── Data packets ────────────────────────────────────────────────────
    // A translation of asf_get_packet, asf_read_frame_header and asf_parse_packet from
    // libavformat/asfdec_f.c. The names are FFmpeg's field names, PascalCased.
    //
    // An ASF data packet is not a frame. It holds a header, then one or more payloads, each a
    // fragment of a media object, and the fragments of one object can be spread over several
    // packets - or several whole objects can be packed into one payload when the replicated
    // data length is 1, which is how a low bitrate audio stream is carried. Nothing here is
    // optional: a reader that does not reassemble produces rubbish on every real file.

    /// <summary>Smallest payload header, from FFmpeg's FRAME_HEADER_SIZE.</summary>
    private const int FrameHeaderSize = 6;

    // State carried across calls, one ASF packet at a time.
    private byte[]? currentPacketData;
    private int at;
    private int packetFlags;
    private int packetProperty;
    private int packetSegments;
    private int packetSegSizeType;
    private int packetSizeLeft;
    private int packetPadSize;
    private long packetTimestamp;
    private int packetSeq;
    private int packetFragOffset;
    private int packetReplicSize;
    private int packetFragSize;
    private long packetFragTimestamp;
    private int packetTimeStart;
    private int packetTimeDelta;
    private int packetMultiSize;
    private int packetStreamNumber;
    private bool packetKeyFrame;

    /// <summary>A media object being reassembled from the fragments of one stream.</summary>
    private sealed class PendingFrame
    {
        public byte[] Data = [];
        public int Size;
        public int FragmentOffset;
        public long TimestampMs;
        public bool IsKeyframe;
        public int ObjectSize;
    }

    private readonly Dictionary<int, PendingFrame> pendingFrames = new();

    /// <summary>
    /// Reads the next complete media object - one frame, the unit a decoder is fed.
    /// </summary>
    /// <remarks>
    /// This is asf_parse_packet: it loops over payloads, reading a new ASF packet whenever the
    /// current one is spent, and returns as soon as one stream's object is whole.
    /// </remarks>
    public bool ReadNextFrame(out AsfFrame frame)
    {
        frame = default;

        if (fileInfo == null)
            Parse();
        if (fileInfo == null || !fileInfo.IsValid || packetSize <= 0 || dataOffset == 0)
            return false;

        while (true)
        {
            if (currentPacketData == null || packetSizeLeft < FrameHeaderSize ||
                (packetSegments < 1 && packetTimeStart == 0))
            {
                if (!GetPacket())
                    return false;
                continue;
            }

            if (packetTimeStart == 0)
            {
                if (!ReadFrameHeader())
                {
                    packetTimeStart = 0;
                    packetSegments = 0;
                    continue;
                }

                if (FindStream(packetStreamNumber) == null)
                {
                    // Unknown stream: skip its fragment rather than guess what it is.
                    packetTimeStart = 0;
                    at += packetFragSize;
                    packetSizeLeft -= packetFragSize;
                    continue;
                }
            }

            if (!pendingFrames.TryGetValue(packetStreamNumber, out var pending))
            {
                pending = new PendingFrame();
                pendingFrames[packetStreamNumber] = pending;
            }

            // A fragment that starts partway into an object we never saw the start of belongs
            // to an object this reader cannot complete.
            if (pending.FragmentOffset == 0 && packetFragOffset != 0)
            {
                at += packetFragSize;
                packetSizeLeft -= packetFragSize;
                continue;
            }

            if (packetReplicSize == 1)
            {
                // Several whole objects in one payload. The fragment offset was the first
                // object's timestamp and each object carries its own length byte.
                packetFragTimestamp = packetTimeStart;
                packetTimeStart += packetTimeDelta;
                pending.ObjectSize = packetFragSize = currentPacketData![at++];
                packetSizeLeft--;
                packetMultiSize--;

                if (packetMultiSize < pending.ObjectSize)
                {
                    packetTimeStart = 0;
                    at += packetMultiSize;
                    packetSizeLeft -= packetMultiSize;
                    continue;
                }

                packetMultiSize -= pending.ObjectSize;
            }

            if (pending.Size != pending.ObjectSize ||
                pending.FragmentOffset + packetFragSize > pending.Size)
            {
                // A new object. Anything half-assembled under this stream is abandoned, which
                // is what FFmpeg does and says it is doing.
                pending.FragmentOffset = 0;
                pending.Size = pending.ObjectSize;
                if (pending.Data.Length < pending.Size)
                    pending.Data = new byte[pending.Size];

                // The preroll is a delay the whole file is shifted by; FFmpeg takes it off
                // every timestamp, and so the first frame of a file lands at zero.
                pending.TimestampMs = packetFragTimestamp - (fileInfo?.PrerollMilliseconds ?? 0);
                pending.IsKeyframe = packetKeyFrame;
            }

            packetSizeLeft -= packetFragSize;
            if (packetSizeLeft < 0)
                continue;

            if (packetFragOffset >= pending.Size ||
                packetFragSize > pending.Size - packetFragOffset)
                continue;

            Array.Copy(currentPacketData!, at, pending.Data, packetFragOffset, packetFragSize);
            at += packetFragSize;
            pending.FragmentOffset += packetFragSize;

            if (pending.FragmentOffset == pending.Size)
            {
                frame = new AsfFrame
                {
                    StreamNumber = packetStreamNumber,
                    Data = pending.Data.AsMemory(0, pending.Size),
                    PresentationMilliseconds = pending.TimestampMs,
                    IsKeyframe = pending.IsKeyframe,
                };
                pending.FragmentOffset = 0;
                pending.Size = 0;
                return true;
            }
        }
    }

    /// <summary>
    /// asf_get_packet: reads one data packet and its header into the fields above.
    /// </summary>
    private bool GetPacket()
    {
        if (dataSize > 0 && stream.Position - dataOffset >= dataSize)
            return false;

        currentPacketData ??= new byte[packetSize];
        int read = 0;
        while (read < packetSize)
        {
            int got = stream.Read(currentPacketData, read, packetSize - read);
            if (got == 0)
                return false;
            read += got;
        }

        at = 0;
        int rsize = 8;

        // Error correction data, if the first byte says there is any.
        int c = currentPacketData[at++];
        if ((c & 0x80) != 0)
        {
            rsize++;
            if ((c & 0x60) == 0)
            {
                at += 2;
                at += (c & 0xF) - 2;
                rsize += c & 0xF;
            }
            c = currentPacketData[at++];
        }
        int d = currentPacketData[at++];

        packetFlags = c;
        packetProperty = d;

        int packetLength = Do2Bits(packetFlags >> 5, packetSize, ref rsize);
        Do2Bits(packetFlags >> 1, 0, ref rsize);                   // sequence, ignored
        int padSize = Do2Bits(packetFlags >> 3, 0, ref rsize);     // padding length

        if (packetLength <= 0 || packetLength > packetSize || padSize >= packetLength)
            return false;

        packetTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(currentPacketData.AsSpan(at));
        at += 4;
        at += 2; // duration

        if ((packetFlags & 0x01) != 0)
        {
            packetSegSizeType = currentPacketData[at++];
            rsize++;
            packetSegments = packetSegSizeType & 0x3F;
        }
        else
        {
            packetSegments = 1;
            packetSegSizeType = 0x80;
        }

        if (rsize > packetLength - padSize)
        {
            packetSizeLeft = 0;
            return false;
        }

        packetSizeLeft = packetLength - padSize - rsize;
        packetPadSize = padSize;
        packetTimeStart = 0;
        currentPacket++;
        return true;
    }

    /// <summary>asf_read_frame_header: the header in front of one payload.</summary>
    private bool ReadFrameHeader()
    {
        int rsize = 1;
        int num = currentPacketData![at++];

        packetSegments--;
        packetKeyFrame = (num >> 7) != 0;
        packetStreamNumber = num & 0x7F;

        packetSeq = Do2Bits(packetProperty >> 4, 0, ref rsize);
        packetFragOffset = Do2Bits(packetProperty >> 2, 0, ref rsize);
        packetReplicSize = Do2Bits(packetProperty, 0, ref rsize);

        if (rsize + packetReplicSize > packetSizeLeft)
            return false;

        if (packetReplicSize >= 8)
        {
            int end = at + packetReplicSize;
            if (end > currentPacketData.Length)
                return false;

            int objectSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(currentPacketData.AsSpan(at));
            at += 4;
            if (objectSize >= (1 << 24) || objectSize < 0)
                return false;

            packetFragTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(currentPacketData.AsSpan(at));

            // Payload extension systems live in the rest of the replicated data. Reading them
            // needs the Extended Stream Properties object, which this header parser does not
            // read, so they are skipped - which is also what FFmpeg does for every type it
            // does not recognise. The one that matters when it appears is 0x2A, a DVR-MS
            // timestamp that overrides the one above.
            at = end;
            rsize += packetReplicSize;

            if (!pendingFrames.TryGetValue(packetStreamNumber, out var pending))
            {
                pending = new PendingFrame();
                pendingFrames[packetStreamNumber] = pending;
            }
            pending.ObjectSize = objectSize;
        }
        else if (packetReplicSize == 1)
        {
            // Compressed payload: the fragment offset is the first object's timestamp.
            packetTimeStart = packetFragOffset;
            packetFragOffset = 0;
            packetFragTimestamp = packetTimestamp;

            packetTimeDelta = currentPacketData[at++];
            rsize++;
        }
        else if (packetReplicSize != 0)
        {
            return false;
        }

        if ((packetFlags & 0x01) != 0)
        {
            packetFragSize = Do2Bits(packetSegSizeType >> 6, 0, ref rsize);
            if (rsize > packetSizeLeft)
                return false;

            if (packetFragSize > packetSizeLeft - rsize)
            {
                if (packetFragSize > packetSizeLeft - rsize + packetPadSize)
                    return false;

                int difference = packetFragSize - (packetSizeLeft - rsize);
                packetSizeLeft += difference;
                packetPadSize -= difference;
            }
        }
        else
        {
            packetFragSize = packetSizeLeft - rsize;
        }

        if (packetReplicSize == 1)
        {
            packetMultiSize = packetFragSize;
            if (packetMultiSize > packetSizeLeft)
                return false;
        }

        packetSizeLeft -= rsize;
        return true;
    }

    /// <summary>
    /// FFmpeg's DO_2BITS: a field whose width the low two bits of <paramref name="bits"/> give,
    /// absent when they are zero. Advancing the cursor is the whole point - a version of this
    /// that read the value without moving on left every later field in the payload header
    /// reading someone else's bytes.
    /// </summary>
    private int Do2Bits(int bits, int defaultValue, ref int rsize)
    {
        switch (bits & 3)
        {
            case 3:
                int wide = (int)BinaryPrimitives.ReadUInt32LittleEndian(currentPacketData.AsSpan(at));
                at += 4;
                rsize += 4;
                return wide;
            case 2:
                int half = BinaryPrimitives.ReadUInt16LittleEndian(currentPacketData.AsSpan(at));
                at += 2;
                rsize += 2;
                return half;
            case 1:
                rsize++;
                return currentPacketData![at++];
            default:
                return defaultValue;
        }
    }

    private AsfStreamInfo? FindStream(int streamNumber)
    {
        foreach (var candidate in streams)
            if (candidate.StreamNumber == streamNumber)
                return candidate;
        return null;
    }

    /// <summary>
    /// Seeks to a specific time.
    /// </summary>
    public bool SeekToTime(long ticks)
    {
        if (fileInfo == null)
            Parse();
        
        if (fileInfo == null || !fileInfo.IsValid || !fileInfo.IsSeekable)
            return false;
        
        if (dataOffset == 0 || packetSize <= 0)
            return false;
        
        // Convert ticks to packet number (approximate)
        // This is a simple linear seek; a real implementation would use the index
        long durationPerPacket = fileInfo.DataPacketCount > 0
            ? fileInfo.PlayDuration / fileInfo.DataPacketCount
            : 0;
        
        if (durationPerPacket <= 0)
            return false;
        
        long targetPacket = ticks / durationPerPacket;
        if (targetPacket < 0)
            targetPacket = 0;
        if (targetPacket >= fileInfo.DataPacketCount)
            targetPacket = fileInfo.DataPacketCount - 1;
        
        long position = dataOffset + (targetPacket * packetSize);
        stream.Position = position;
        currentPacket = targetPacket;
        
        return true;
    }
    
    /// <summary>
    /// Resets to the beginning of data.
    /// </summary>
    public void Reset()
    {
        if (dataOffset > 0)
        {
            stream.Position = dataOffset;
            currentPacket = 0;
        }
    }
    
    /// <summary>
    /// Current position in the stream.
    /// </summary>
    public long Position => stream.Position;
    
    /// <summary>
    /// Current packet number.
    /// </summary>
    public long CurrentPacket => currentPacket;
    
    public void Dispose()
    {
        if (disposed)
            return;
        
        disposed = true;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

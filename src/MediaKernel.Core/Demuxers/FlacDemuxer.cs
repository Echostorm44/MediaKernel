// MediaKernel - FLAC (Free Lossless Audio Codec) Demuxer
// Parses native .flac files and extracts audio frames and metadata
// Reference: VLC's demux/flac.c and FLAC specification

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// FLAC metadata block types.
/// </summary>
public enum FlacMetadataBlockType : byte
{
    StreamInfo = 0,
    Padding = 1,
    Application = 2,
    SeekTable = 3,
    VorbisComment = 4,
    CueSheet = 5,
    Picture = 6,
    Invalid = 127
}

/// <summary>
/// FLAC picture types (same as ID3v2 APIC).
/// </summary>
public enum FlacPictureType : uint
{
    Other = 0,
    FileIcon = 1,
    OtherFileIcon = 2,
    FrontCover = 3,
    BackCover = 4,
    LeafletPage = 5,
    Media = 6,
    LeadArtist = 7,
    Artist = 8,
    Conductor = 9,
    Band = 10,
    Composer = 11,
    Lyricist = 12,
    RecordingLocation = 13,
    DuringRecording = 14,
    DuringPerformance = 15,
    VideoScreenCapture = 16,
    Fish = 17,
    Illustration = 18,
    BandLogo = 19,
    PublisherLogo = 20
}

/// <summary>
/// FLAC stream info metadata.
/// </summary>
public readonly struct FlacStreamInfo
{
    /// <summary>Minimum block size in samples.</summary>
    public int MinBlockSize { get; init; }
    
    /// <summary>Maximum block size in samples.</summary>
    public int MaxBlockSize { get; init; }
    
    /// <summary>Minimum frame size in bytes (0 = unknown).</summary>
    public int MinFrameSize { get; init; }
    
    /// <summary>Maximum frame size in bytes (0 = unknown).</summary>
    public int MaxFrameSize { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of channels (1-8).</summary>
    public int Channels { get; init; }
    
    /// <summary>Bits per sample (4-32).</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Total samples in stream (0 = unknown).</summary>
    public long TotalSamples { get; init; }
    
    /// <summary>MD5 signature of unencoded audio data.</summary>
    public byte[] Md5Signature { get; init; }
    
    /// <summary>Duration in seconds.</summary>
    public double DurationSeconds => SampleRate > 0 ? (double)TotalSamples / SampleRate : 0;
    
    /// <summary>Duration as TimeSpan.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}

/// <summary>
/// FLAC seek point.
/// </summary>
public readonly struct FlacSeekPoint
{
    /// <summary>Sample number of first sample in target frame (0xFFFFFFFFFFFFFFFF for placeholder).</summary>
    public ulong SampleNumber { get; init; }
    
    /// <summary>Byte offset from first frame header.</summary>
    public ulong ByteOffset { get; init; }
    
    /// <summary>Number of samples in target frame.</summary>
    public ushort FrameSamples { get; init; }
    
    /// <summary>Whether this is a placeholder point.</summary>
    public bool IsPlaceholder => SampleNumber == 0xFFFFFFFFFFFFFFFF;
}

/// <summary>
/// FLAC embedded picture.
/// </summary>
public sealed class FlacPicture
{
    /// <summary>Picture type.</summary>
    public FlacPictureType Type { get; init; }
    
    /// <summary>MIME type string.</summary>
    public string MimeType { get; init; } = "";
    
    /// <summary>Description.</summary>
    public string Description { get; init; } = "";
    
    /// <summary>Width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Color depth in bits per pixel.</summary>
    public int ColorDepth { get; init; }
    
    /// <summary>Number of colors for indexed images (0 for non-indexed).</summary>
    public int IndexedColorCount { get; init; }
    
    /// <summary>Picture data.</summary>
    public byte[] Data { get; init; } = [];
}

/// <summary>
/// FLAC cue sheet track.
/// </summary>
public readonly struct FlacCueSheetTrack
{
    /// <summary>Track offset in samples.</summary>
    public ulong Offset { get; init; }
    
    /// <summary>Track number (1-99, or 170 for lead-out).</summary>
    public byte Number { get; init; }
    
    /// <summary>ISRC code (12 characters).</summary>
    public string Isrc { get; init; }
    
    /// <summary>Whether this is audio track.</summary>
    public bool IsAudio { get; init; }
    
    /// <summary>Whether pre-emphasis is applied.</summary>
    public bool HasPreEmphasis { get; init; }
    
    /// <summary>Index points within track.</summary>
    public IReadOnlyList<FlacCueSheetIndex> IndexPoints { get; init; }
}

/// <summary>
/// FLAC cue sheet index point.
/// </summary>
public readonly struct FlacCueSheetIndex
{
    /// <summary>Offset in samples relative to track offset.</summary>
    public ulong Offset { get; init; }
    
    /// <summary>Index number (0-99).</summary>
    public byte Number { get; init; }
}

/// <summary>
/// FLAC cue sheet metadata.
/// </summary>
public sealed class FlacCueSheet
{
    /// <summary>Media catalog number.</summary>
    public string MediaCatalogNumber { get; init; } = "";
    
    /// <summary>Number of lead-in samples.</summary>
    public ulong LeadInSamples { get; init; }
    
    /// <summary>Whether this is a CD cue sheet.</summary>
    public bool IsCompactDisc { get; init; }
    
    /// <summary>Tracks in the cue sheet.</summary>
    public IReadOnlyList<FlacCueSheetTrack> Tracks { get; init; } = [];
}

/// <summary>
/// Parsed FLAC file with metadata and audio frames.
/// </summary>
public sealed class FlacFile
{
    /// <summary>Stream info metadata (required).</summary>
    public FlacStreamInfo StreamInfo { get; init; }
    
    /// <summary>Seek table (optional).</summary>
    public IReadOnlyList<FlacSeekPoint> SeekTable { get; init; } = [];
    
    /// <summary>Vorbis comments / tags (optional).</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    
    /// <summary>Embedded pictures (optional).</summary>
    public IReadOnlyList<FlacPicture> Pictures { get; init; } = [];
    
    /// <summary>Cue sheet (optional).</summary>
    public FlacCueSheet? CueSheet { get; init; }
    
    /// <summary>Application metadata blocks.</summary>
    public IReadOnlyList<(string ApplicationId, byte[] Data)> ApplicationBlocks { get; init; } = [];
    
    /// <summary>Offset to first audio frame.</summary>
    public long AudioDataOffset { get; init; }
    
    /// <summary>Duration.</summary>
    public TimeSpan Duration => StreamInfo.Duration;
}

/// <summary>
/// FLAC audio frame header information.
/// </summary>
public readonly struct FlacFrameHeader
{
    /// <summary>Whether this uses variable block size (sample number) vs fixed (frame number).</summary>
    public bool IsVariableBlockSize { get; init; }
    
    /// <summary>Block size in samples.</summary>
    public int BlockSize { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Channel assignment (0-7 = channels, 8 = left/side, 9 = right/side, 10 = mid/side).</summary>
    public int ChannelAssignment { get; init; }
    
    /// <summary>Bits per sample.</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Frame or sample number.</summary>
    public ulong FrameOrSampleNumber { get; init; }
    
    /// <summary>CRC-8 of header.</summary>
    public byte HeaderCrc { get; init; }
}

/// <summary>
/// Demuxer for FLAC (Free Lossless Audio Codec) files.
/// </summary>
public sealed class FlacDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool disposed;
    
    private static readonly byte[] FlacMagic = "fLaC"u8.ToArray();
    
    public FlacDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>
    /// Checks if the stream contains a valid FLAC file.
    /// </summary>
    public bool TryParseHeader(out FlacStreamInfo streamInfo)
    {
        streamInfo = default;
        
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) < 4)
            return false;
        
        if (!magic.SequenceEqual(FlacMagic))
            return false;
        
        // Read first metadata block header
        Span<byte> blockHeader = stackalloc byte[4];
        if (stream.Read(blockHeader) < 4)
            return false;
        
        bool isLast = (blockHeader[0] & 0x80) != 0;
        var blockType = (FlacMetadataBlockType)(blockHeader[0] & 0x7F);
        int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
        
        // First block must be STREAMINFO
        if (blockType != FlacMetadataBlockType.StreamInfo || blockLength != 34)
            return false;
        
        byte[] streamInfoData = new byte[34];
        if (stream.Read(streamInfoData) < 34)
            return false;
        
        streamInfo = ParseStreamInfo(streamInfoData);
        return true;
    }
    
    /// <summary>
    /// Parses the entire FLAC file metadata.
    /// </summary>
    public FlacFile? Parse()
    {
        stream.Seek(0, SeekOrigin.Begin);
        
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) < 4 || !magic.SequenceEqual(FlacMagic))
            return null;
        
        FlacStreamInfo streamInfo = default;
        var seekTable = new List<FlacSeekPoint>();
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pictures = new List<FlacPicture>();
        var applicationBlocks = new List<(string, byte[])>();
        FlacCueSheet? cueSheet = null;
        
        bool hasStreamInfo = false;
        bool isLast = false;
        
        while (!isLast)
        {
            Span<byte> blockHeader = stackalloc byte[4];
            if (stream.Read(blockHeader) < 4)
                break;
            
            isLast = (blockHeader[0] & 0x80) != 0;
            var blockType = (FlacMetadataBlockType)(blockHeader[0] & 0x7F);
            int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            
            if (blockLength == 0 && blockType != FlacMetadataBlockType.Padding)
            {
                // Skip empty non-padding blocks
                continue;
            }
            
            byte[] blockData = new byte[blockLength];
            if (stream.Read(blockData) < blockLength)
                break;
            
            switch (blockType)
            {
                case FlacMetadataBlockType.StreamInfo:
                    if (blockLength == 34)
                    {
                        streamInfo = ParseStreamInfo(blockData);
                        hasStreamInfo = true;
                    }
                    break;
                    
                case FlacMetadataBlockType.SeekTable:
                    ParseSeekTable(blockData, seekTable);
                    break;
                    
                case FlacMetadataBlockType.VorbisComment:
                    ParseVorbisComment(blockData, tags);
                    break;
                    
                case FlacMetadataBlockType.Picture:
                    var picture = ParsePicture(blockData);
                    if (picture != null)
                        pictures.Add(picture);
                    break;
                    
                case FlacMetadataBlockType.CueSheet:
                    cueSheet = ParseCueSheet(blockData);
                    break;
                    
                case FlacMetadataBlockType.Application:
                    if (blockLength >= 4)
                    {
                        string appId = Encoding.ASCII.GetString(blockData, 0, 4);
                        byte[] appData = new byte[blockLength - 4];
                        Array.Copy(blockData, 4, appData, 0, appData.Length);
                        applicationBlocks.Add((appId, appData));
                    }
                    break;
                    
                case FlacMetadataBlockType.Padding:
                    // Skip padding
                    break;
            }
        }
        
        if (!hasStreamInfo)
            return null;
        
        return new FlacFile
        {
            StreamInfo = streamInfo,
            SeekTable = seekTable,
            Tags = tags,
            Pictures = pictures,
            CueSheet = cueSheet,
            ApplicationBlocks = applicationBlocks,
            AudioDataOffset = stream.Position
        };
    }
    
    private static FlacStreamInfo ParseStreamInfo(byte[] data)
    {
        // STREAMINFO block is 34 bytes
        int minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0));
        int maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        int minFrameSize = (data[4] << 16) | (data[5] << 8) | data[6];
        int maxFrameSize = (data[7] << 16) | (data[8] << 8) | data[9];
        
        // Sample rate (20 bits), channels (3 bits), bits per sample (5 bits), total samples (36 bits)
        // Bits 10-12 contain: sample rate bits 0-7
        // Bits 13-17 contain: sample rate bits 8-19, channel bits 0-2, bps bits 0-4
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(10));
        
        int sampleRate = (int)((packed >> 44) & 0xFFFFF);
        int channels = (int)((packed >> 41) & 0x7) + 1;
        int bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
        long totalSamples = (long)(packed & 0xFFFFFFFFF);
        
        byte[] md5 = new byte[16];
        Array.Copy(data, 18, md5, 0, 16);
        
        return new FlacStreamInfo
        {
            MinBlockSize = minBlockSize,
            MaxBlockSize = maxBlockSize,
            MinFrameSize = minFrameSize,
            MaxFrameSize = maxFrameSize,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            TotalSamples = totalSamples,
            Md5Signature = md5
        };
    }
    
    private static void ParseSeekTable(byte[] data, List<FlacSeekPoint> seekTable)
    {
        // Each seek point is 18 bytes
        int pointCount = data.Length / 18;
        
        for (int i = 0; i < pointCount; i++)
        {
            int offset = i * 18;
            
            ulong sampleNumber = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
            ulong byteOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8));
            ushort frameSamples = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 16));
            
            seekTable.Add(new FlacSeekPoint
            {
                SampleNumber = sampleNumber,
                ByteOffset = byteOffset,
                FrameSamples = frameSamples
            });
        }
    }
    
    private static void ParseVorbisComment(byte[] data, Dictionary<string, string> tags)
    {
        if (data.Length < 4)
            return;
        
        int offset = 0;
        
        // Vendor string length (little-endian)
        int vendorLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        
        if (offset + vendorLength > data.Length)
            return;
        
        // Skip vendor string
        offset += vendorLength;
        
        if (offset + 4 > data.Length)
            return;
        
        // Number of comments
        int commentCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        
        for (int i = 0; i < commentCount && offset + 4 <= data.Length; i++)
        {
            int commentLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            
            if (offset + commentLength > data.Length)
                break;
            
            string comment = Encoding.UTF8.GetString(data, offset, commentLength);
            offset += commentLength;
            
            int equalsIndex = comment.IndexOf('=');
            if (equalsIndex > 0)
            {
                string key = comment[..equalsIndex].ToUpperInvariant();
                string value = comment[(equalsIndex + 1)..];
                
                // Handle multiple values for same key
                if (tags.TryGetValue(key, out string? existing))
                    tags[key] = existing + "; " + value;
                else
                    tags[key] = value;
            }
        }
    }
    
    private static FlacPicture? ParsePicture(byte[] data)
    {
        if (data.Length < 32)
            return null;
        
        int offset = 0;
        
        var pictureType = (FlacPictureType)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        
        int mimeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        
        if (offset + mimeLength > data.Length)
            return null;
        
        string mimeType = Encoding.ASCII.GetString(data, offset, mimeLength);
        offset += mimeLength;
        
        if (offset + 4 > data.Length)
            return null;
        
        int descLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        
        if (offset + descLength > data.Length)
            return null;
        
        string description = Encoding.UTF8.GetString(data, offset, descLength);
        offset += descLength;
        
        if (offset + 20 > data.Length)
            return null;
        
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
        int colorDepth = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8));
        int indexedColors = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 12));
        int dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 16));
        offset += 20;
        
        if (offset + dataLength > data.Length)
            return null;
        
        byte[] pictureData = new byte[dataLength];
        Array.Copy(data, offset, pictureData, 0, dataLength);
        
        return new FlacPicture
        {
            Type = pictureType,
            MimeType = mimeType,
            Description = description,
            Width = width,
            Height = height,
            ColorDepth = colorDepth,
            IndexedColorCount = indexedColors,
            Data = pictureData
        };
    }
    
    private static FlacCueSheet? ParseCueSheet(byte[] data)
    {
        if (data.Length < 396)
            return null;
        
        string catalogNumber = Encoding.ASCII.GetString(data, 0, 128).TrimEnd('\0');
        ulong leadInSamples = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(128));
        bool isCompactDisc = (data[136] & 0x80) != 0;
        // 258 bytes reserved
        byte trackCount = data[395];
        
        var tracks = new List<FlacCueSheetTrack>();
        int offset = 396;
        
        for (int t = 0; t < trackCount && offset + 36 <= data.Length; t++)
        {
            ulong trackOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
            byte trackNumber = data[offset + 8];
            string isrc = Encoding.ASCII.GetString(data, offset + 9, 12).TrimEnd('\0');
            bool isAudio = (data[offset + 21] & 0x80) == 0;
            bool hasPreEmphasis = (data[offset + 21] & 0x40) != 0;
            // 13 bytes reserved
            byte indexCount = data[offset + 35];
            offset += 36;
            
            var indices = new List<FlacCueSheetIndex>();
            for (int idx = 0; idx < indexCount && offset + 12 <= data.Length; idx++)
            {
                ulong indexOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
                byte indexNumber = data[offset + 8];
                // 3 bytes reserved
                offset += 12;
                
                indices.Add(new FlacCueSheetIndex
                {
                    Offset = indexOffset,
                    Number = indexNumber
                });
            }
            
            tracks.Add(new FlacCueSheetTrack
            {
                Offset = trackOffset,
                Number = trackNumber,
                Isrc = isrc,
                IsAudio = isAudio,
                HasPreEmphasis = hasPreEmphasis,
                IndexPoints = indices
            });
        }
        
        return new FlacCueSheet
        {
            MediaCatalogNumber = catalogNumber,
            LeadInSamples = leadInSamples,
            IsCompactDisc = isCompactDisc,
            Tracks = tracks
        };
    }
    
    // The frame reader that used to live here is deleted rather than kept.
    //
    // It scanned for the next 0xFF 0xF8 byte pair and cut the frame there, with its own comment
    // admitting "this is a simplified approach - real implementation would parse subframes". FLAC
    // residual data is full of those byte pairs: on the nine conformance streams in
    // tests/MediaKernel.Tests/TestData/AudioFateRefs/flac there are 116 of them where there are 91
    // frames, and 258 where there are 118. Nothing used it, including this file's own tests.
    //
    // Frame boundaries come from FlacParser, the port of libavcodec/flac_parser.c, which validates
    // each candidate header's CRC-8 and then checks the CRC-16 footer between candidates. This
    // demuxer's job is the header chain - Parse() - and FlacAudioSource drives the parser over the
    // frames that follow it.
    
    /// <summary>
    /// Seeks to a specific sample position using the seek table.
    /// </summary>
    public bool SeekToSample(FlacFile file, long sampleNumber)
    {
        if (file.SeekTable.Count == 0)
        {
            // No seek table, seek to start of audio
            stream.Seek(file.AudioDataOffset, SeekOrigin.Begin);
            return true;
        }
        
        // Find closest seek point before target sample
        FlacSeekPoint? closestPoint = null;
        
        foreach (var point in file.SeekTable)
        {
            if (point.IsPlaceholder)
                continue;
            
            if ((long)point.SampleNumber <= sampleNumber)
                closestPoint = point;
            else
                break;
        }
        
        if (closestPoint.HasValue)
        {
            long seekPosition = file.AudioDataOffset + (long)closestPoint.Value.ByteOffset;
            stream.Seek(seekPosition, SeekOrigin.Begin);
            return true;
        }
        
        // Seek to beginning of audio
        stream.Seek(file.AudioDataOffset, SeekOrigin.Begin);
        return true;
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            if (!leaveOpen)
                stream.Dispose();
            disposed = true;
        }
    }
}

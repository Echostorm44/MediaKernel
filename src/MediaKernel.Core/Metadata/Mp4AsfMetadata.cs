// MediaKernel - MP4/iTunes Atoms Metadata Parser
// Parses metadata from MP4, M4A, M4V, MOV files (iTunes-style atoms)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// Parses iTunes-style metadata atoms from MP4/M4A/MOV files.
/// Reference: https://developer.apple.com/library/archive/documentation/QuickTime/QTFF/Metadata/Metadata.html
/// </summary>
public sealed class Mp4MetadataParser
{
    // Well-known iTunes metadata atom types
    private static readonly Dictionary<string, string> AtomToFieldMap = new()
    {
        { "©nam", "TITLE" },
        { "©ART", "ARTIST" },
        { "©alb", "ALBUM" },
        { "aART", "ALBUMARTIST" },
        { "©wrt", "COMPOSER" },
        { "©gen", "GENRE" },
        { "gnre", "GENRE" },
        { "©day", "YEAR" },
        { "trkn", "TRACK" },
        { "disk", "DISC" },
        { "©cmt", "COMMENT" },
        { "©lyr", "LYRICS" },
        { "cprt", "COPYRIGHT" },
        { "©too", "ENCODER" },
        { "covr", "ARTWORK" },
        { "tmpo", "BPM" },
        { "cpil", "COMPILATION" },
        { "pgap", "GAPLESS" },
        { "pcst", "PODCAST" },
        { "©grp", "GROUPING" },
        { "soal", "SORTALBUM" },
        { "soar", "SORTARTIST" },
        { "sonm", "SORTTITLE" }
    };

    /// <summary>
    /// Parse metadata from an MP4/M4A stream.
    /// </summary>
    public MediaMetadata Parse(Stream stream)
    {
        var metadata = new MediaMetadata();

        if (!stream.CanSeek)
            return metadata;

        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            ParseAtoms(stream, stream.Length, metadata);
        }
        finally
        {
            stream.Position = originalPosition;
        }

        return metadata;
    }

    /// <summary>
    /// Parse metadata from MP4 data.
    /// </summary>
    public MediaMetadata Parse(ReadOnlySpan<byte> data)
    {
        var metadata = new MediaMetadata();
        ParseAtoms(data, 0, metadata);
        return metadata;
    }

    private void ParseAtoms(Stream stream, long endPosition, MediaMetadata metadata)
    {
        Span<byte> header = stackalloc byte[8];

        while (stream.Position + 8 <= endPosition)
        {
            long atomStart = stream.Position;

            if (stream.Read(header) < 8)
                break;

            uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));

            long atomSize = size;
            long dataStart = stream.Position;

            // Handle extended size
            if (size == 1)
            {
                Span<byte> extSize = stackalloc byte[8];
                if (stream.Read(extSize) < 8)
                    break;
                atomSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extSize);
                dataStart = stream.Position;
            }
            else if (size == 0)
            {
                atomSize = endPosition - atomStart;
            }

            if (atomSize < 8 || atomStart + atomSize > endPosition)
                break;

            long atomEnd = atomStart + atomSize;
            long dataSize = atomEnd - dataStart;

            // Navigate to metadata containers
            if (type == "moov" || type == "udta" || type == "meta" || type == "ilst")
            {
                // Skip version/flags for meta atom
                if (type == "meta" && dataSize >= 4)
                {
                    stream.Position += 4;
                }

                ParseAtoms(stream, atomEnd, metadata);
            }
            else if (IsMetadataAtom(type))
            {
                ParseMetadataAtom(stream, type, dataSize, metadata);
            }

            stream.Position = atomEnd;
        }
    }

    private void ParseAtoms(ReadOnlySpan<byte> data, int offset, MediaMetadata metadata)
    {
        while (offset + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
            string type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));

            int atomSize = (int)size;
            int dataStart = offset + 8;

            if (size == 1 && offset + 16 <= data.Length)
            {
                atomSize = (int)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8));
                dataStart = offset + 16;
            }
            else if (size == 0)
            {
                atomSize = data.Length - offset;
            }

            if (atomSize < 8 || offset + atomSize > data.Length)
                break;

            int atomEnd = offset + atomSize;

            if (type == "moov" || type == "udta" || type == "meta" || type == "ilst")
            {
                int childStart = dataStart;
                if (type == "meta")
                    childStart += 4; // Skip version/flags

                ParseAtoms(data, childStart, metadata);
            }
            else if (IsMetadataAtom(type))
            {
                ParseMetadataAtomSpan(data.Slice(dataStart, atomEnd - dataStart), type, metadata);
            }

            offset = atomEnd;
        }
    }

    private static bool IsMetadataAtom(string type)
    {
        return AtomToFieldMap.ContainsKey(type) || 
               type.StartsWith("©") || 
               type == "----"; // Freeform atom
    }

    private void ParseMetadataAtom(Stream stream, string type, long dataSize, MediaMetadata metadata)
    {
        if (dataSize > 10_000_000)
            return;

        byte[] atomData = new byte[(int)dataSize];
        if (stream.Read(atomData) < atomData.Length)
            return;

        ParseMetadataAtomSpan(atomData, type, metadata);
    }

    private void ParseMetadataAtomSpan(ReadOnlySpan<byte> data, string atomType, MediaMetadata metadata)
    {
        // Look for 'data' sub-atom
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
            string type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));

            if (size < 8 || offset + size > data.Length)
                break;

            if (type == "data")
            {
                // Data atom format: 4 bytes type indicator, 4 bytes locale, then value
                if (size < 16)
                {
                    offset += (int)size;
                    continue;
                }

                int typeIndicator = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8));
                var valueData = data.Slice(offset + 16, (int)size - 16);

                ProcessMetadataValue(atomType, typeIndicator, valueData, metadata);
            }
            else if (type == "mean" || type == "name")
            {
                // Part of freeform atom, skip for now
            }

            offset += (int)size;
        }
    }

    private void ProcessMetadataValue(string atomType, int typeIndicator, ReadOnlySpan<byte> value, MediaMetadata metadata)
    {
        // Type indicators:
        // 0 = implicit (binary)
        // 1 = UTF-8
        // 2 = UTF-16
        // 13 = JPEG
        // 14 = PNG
        // 21 = signed integer (big-endian)
        // 22 = unsigned integer (big-endian)

        string fieldName = AtomToFieldMap.GetValueOrDefault(atomType, atomType);

        switch (atomType)
        {
            case "covr":
                metadata.AlbumArt = value.ToArray();
                metadata.AlbumArtMimeType = typeIndicator switch
                {
                    13 => "image/jpeg",
                    14 => "image/png",
                    _ => DetectImageType(value)
                };
                break;

            case "trkn":
                if (value.Length >= 4)
                {
                    // Format: 2 bytes padding, 2 bytes track, 2 bytes total
                    metadata.TrackNumber = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2));
                    if (value.Length >= 6)
                        metadata.TrackTotal = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(4));
                }
                break;

            case "disk":
                if (value.Length >= 4)
                {
                    metadata.DiscNumber = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2));
                    if (value.Length >= 6)
                        metadata.DiscTotal = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(4));
                }
                break;

            case "gnre":
                // ID3v1 genre index + 1
                if (value.Length >= 2)
                {
                    int genreIndex = BinaryPrimitives.ReadUInt16BigEndian(value) - 1;
                    metadata.Genre ??= GetGenreName(genreIndex);
                }
                break;

            case "tmpo":
                // BPM as integer
                if (value.Length >= 2)
                {
                    int bpm = BinaryPrimitives.ReadUInt16BigEndian(value);
                    metadata.CustomTags["BPM"] = bpm.ToString();
                }
                break;

            default:
                // Text fields
                if (typeIndicator == 1) // UTF-8
                {
                    string text = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    MapTextToMetadata(fieldName, text, metadata);
                }
                else if (typeIndicator == 2) // UTF-16
                {
                    string text = Encoding.BigEndianUnicode.GetString(value).TrimEnd('\0');
                    MapTextToMetadata(fieldName, text, metadata);
                }
                break;
        }
    }

    private void MapTextToMetadata(string field, string value, MediaMetadata metadata)
    {
        metadata.CustomTags.TryAdd(field, value);

        switch (field)
        {
            case "TITLE": metadata.Title ??= value; break;
            case "ARTIST": metadata.Artist ??= value; break;
            case "ALBUM": metadata.Album ??= value; break;
            case "ALBUMARTIST": metadata.AlbumArtist ??= value; break;
            case "COMPOSER": metadata.Composer ??= value; break;
            case "GENRE": metadata.Genre ??= value; break;
            case "YEAR":
                metadata.Year ??= value.Length >= 4 ? value.Substring(0, 4) : value;
                break;
            case "COMMENT": metadata.Comment ??= value; break;
            case "LYRICS": metadata.Lyrics ??= value; break;
            case "COPYRIGHT": metadata.Copyright ??= value; break;
            case "ENCODER": metadata.Encoder ??= value; break;
        }
    }

    private static string DetectImageType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3)
        {
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "image/jpeg";
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E)
                return "image/png";
        }
        return "image/jpeg";
    }

    private static string GetGenreName(int index)
    {
        string[] genres =
        {
            "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
            "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock",
            "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack"
        };
        return index >= 0 && index < genres.Length ? genres[index] : "Unknown";
    }
}

/// <summary>
/// Parses ASF/WMA/WMV metadata.
/// Reference: https://docs.microsoft.com/en-us/windows/win32/wmformat/asf-specification
/// </summary>
public sealed class AsfMetadataParser
{
    // ASF GUIDs
    private static readonly Guid AsfHeaderGuid = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ContentDescriptionGuid = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ExtendedContentGuid = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");

    // Extended content attribute names
    private static readonly Dictionary<string, string> AttributeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "WM/AlbumTitle", "ALBUM" },
        { "WM/AlbumArtist", "ALBUMARTIST" },
        { "WM/Genre", "GENRE" },
        { "WM/Year", "YEAR" },
        { "WM/TrackNumber", "TRACK" },
        { "WM/PartOfSet", "DISC" },
        { "WM/Composer", "COMPOSER" },
        { "WM/Lyrics", "LYRICS" },
        { "WM/Picture", "ARTWORK" },
        { "WM/BeatsPerMinute", "BPM" },
        { "WM/Publisher", "PUBLISHER" },
        { "WM/EncodedBy", "ENCODER" },
        { "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_GAIN" },
        { "REPLAYGAIN_ALBUM_GAIN", "REPLAYGAIN_ALBUM_GAIN" }
    };

    /// <summary>
    /// Parse ASF metadata from a stream.
    /// </summary>
    public MediaMetadata Parse(Stream stream)
    {
        var metadata = new MediaMetadata();

        if (!stream.CanSeek || stream.Length < 30)
            return metadata;

        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            ParseAsfHeader(stream, metadata);
        }
        finally
        {
            stream.Position = originalPosition;
        }

        return metadata;
    }

    private void ParseAsfHeader(Stream stream, MediaMetadata metadata)
    {
        Span<byte> headerBytes = stackalloc byte[30];
        if (stream.Read(headerBytes) < 30)
            return;

        // Check ASF header GUID
        var headerGuid = new Guid(headerBytes.Slice(0, 16));
        if (headerGuid != AsfHeaderGuid)
            return;

        long headerSize = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(16));
        int objectCount = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(24));

        // Skip reserved bytes
        stream.Position += 2;

        // Parse header objects
        for (int i = 0; i < objectCount && stream.Position + 24 <= headerSize + 30; i++)
        {
            Span<byte> objectHeader = stackalloc byte[24];
            if (stream.Read(objectHeader) < 24)
                break;

            var objectGuid = new Guid(objectHeader.Slice(0, 16));
            long objectSize = BinaryPrimitives.ReadInt64LittleEndian(objectHeader.Slice(16));

            if (objectSize < 24)
                break;

            long dataSize = objectSize - 24;
            long nextObject = stream.Position + dataSize;

            if (objectGuid == ContentDescriptionGuid)
            {
                ParseContentDescription(stream, dataSize, metadata);
            }
            else if (objectGuid == ExtendedContentGuid)
            {
                ParseExtendedContent(stream, dataSize, metadata);
            }

            stream.Position = nextObject;
        }
    }

    private void ParseContentDescription(Stream stream, long dataSize, MediaMetadata metadata)
    {
        if (dataSize < 10)
            return;

        Span<byte> lengths = stackalloc byte[10];
        if (stream.Read(lengths) < 10)
            return;

        int titleLen = BinaryPrimitives.ReadUInt16LittleEndian(lengths);
        int authorLen = BinaryPrimitives.ReadUInt16LittleEndian(lengths.Slice(2));
        int copyrightLen = BinaryPrimitives.ReadUInt16LittleEndian(lengths.Slice(4));
        int descriptionLen = BinaryPrimitives.ReadUInt16LittleEndian(lengths.Slice(6));
        int ratingLen = BinaryPrimitives.ReadUInt16LittleEndian(lengths.Slice(8));

        metadata.Title = ReadUnicodeString(stream, titleLen);
        metadata.Artist = ReadUnicodeString(stream, authorLen);
        metadata.Copyright = ReadUnicodeString(stream, copyrightLen);
        metadata.Comment = ReadUnicodeString(stream, descriptionLen);
        // Skip rating
        stream.Position += ratingLen;
    }

    private void ParseExtendedContent(Stream stream, long dataSize, MediaMetadata metadata)
    {
        if (dataSize < 2)
            return;

        Span<byte> countBytes = stackalloc byte[2];
        if (stream.Read(countBytes) < 2)
            return;

        int descriptorCount = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        long endPosition = stream.Position + dataSize - 2;

        for (int i = 0; i < descriptorCount && stream.Position + 4 < endPosition; i++)
        {
            // Name length
            Span<byte> nameLenBytes = stackalloc byte[2];
            if (stream.Read(nameLenBytes) < 2)
                break;

            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(nameLenBytes);
            if (nameLen <= 0 || stream.Position + nameLen + 4 > endPosition)
                break;

            string name = ReadUnicodeString(stream, nameLen);

            // Value type and length
            Span<byte> typeAndLen = stackalloc byte[4];
            if (stream.Read(typeAndLen) < 4)
                break;

            int valueType = BinaryPrimitives.ReadUInt16LittleEndian(typeAndLen);
            int valueLen = BinaryPrimitives.ReadUInt16LittleEndian(typeAndLen.Slice(2));

            if (valueLen < 0 || stream.Position + valueLen > endPosition)
                break;

            // Read value based on type
            byte[] valueData = new byte[valueLen];
            if (stream.Read(valueData) < valueLen)
                break;

            ProcessExtendedAttribute(name, valueType, valueData, metadata);
        }
    }

    private void ProcessExtendedAttribute(string name, int valueType, byte[] valueData, MediaMetadata metadata)
    {
        string? stringValue = null;

        switch (valueType)
        {
            case 0: // Unicode string
                stringValue = Encoding.Unicode.GetString(valueData).TrimEnd('\0');
                break;
            case 1: // Byte array (binary)
                if (name == "WM/Picture" && valueData.Length > 0)
                {
                    ParseWmPicture(valueData, metadata);
                }
                return;
            case 2: // Bool
            case 3: // DWORD
                if (valueData.Length >= 4)
                {
                    int intValue = BinaryPrimitives.ReadInt32LittleEndian(valueData);
                    stringValue = intValue.ToString();
                }
                break;
            case 4: // QWORD
                if (valueData.Length >= 8)
                {
                    long longValue = BinaryPrimitives.ReadInt64LittleEndian(valueData);
                    stringValue = longValue.ToString();
                }
                break;
            case 5: // WORD
                if (valueData.Length >= 2)
                {
                    int shortValue = BinaryPrimitives.ReadInt16LittleEndian(valueData);
                    stringValue = shortValue.ToString();
                }
                break;
        }

        if (stringValue == null)
            return;

        metadata.CustomTags.TryAdd(name, stringValue);

        // Map to standard fields
        if (AttributeMap.TryGetValue(name, out string? field))
        {
            switch (field)
            {
                case "ALBUM": metadata.Album ??= stringValue; break;
                case "ALBUMARTIST": metadata.AlbumArtist ??= stringValue; break;
                case "GENRE": metadata.Genre ??= stringValue; break;
                case "YEAR": metadata.Year ??= stringValue.Length >= 4 ? stringValue.Substring(0, 4) : stringValue; break;
                case "TRACK":
                    if (int.TryParse(stringValue, out int track))
                        metadata.TrackNumber = track;
                    break;
                case "DISC":
                    ParseDiscNumber(stringValue, metadata);
                    break;
                case "COMPOSER": metadata.Composer ??= stringValue; break;
                case "LYRICS": metadata.Lyrics ??= stringValue; break;
                case "ENCODER": metadata.Encoder ??= stringValue; break;
                case "REPLAYGAIN_TRACK_GAIN":
                    if (TryParseReplayGain(stringValue, out double trackGain))
                        metadata.ReplayGainTrack = trackGain;
                    break;
                case "REPLAYGAIN_ALBUM_GAIN":
                    if (TryParseReplayGain(stringValue, out double albumGain))
                        metadata.ReplayGainAlbum = albumGain;
                    break;
            }
        }
    }

    private void ParseWmPicture(byte[] data, MediaMetadata metadata)
    {
        if (data.Length < 5)
            return;

        // WM/Picture format:
        // 1 byte: picture type
        // 4 bytes: data length
        // null-terminated Unicode MIME type
        // null-terminated Unicode description
        // image data

        int offset = 5;

        // Skip MIME type
        while (offset + 2 < data.Length)
        {
            if (data[offset] == 0 && data[offset + 1] == 0)
            {
                offset += 2;
                break;
            }
            offset += 2;
        }

        // Skip description
        while (offset + 2 < data.Length)
        {
            if (data[offset] == 0 && data[offset + 1] == 0)
            {
                offset += 2;
                break;
            }
            offset += 2;
        }

        if (offset < data.Length)
        {
            metadata.AlbumArt = new byte[data.Length - offset];
            Array.Copy(data, offset, metadata.AlbumArt, 0, metadata.AlbumArt.Length);

            // Detect MIME type
            if (metadata.AlbumArt.Length >= 3)
            {
                if (metadata.AlbumArt[0] == 0xFF && metadata.AlbumArt[1] == 0xD8)
                    metadata.AlbumArtMimeType = "image/jpeg";
                else if (metadata.AlbumArt[0] == 0x89 && metadata.AlbumArt[1] == 0x50)
                    metadata.AlbumArtMimeType = "image/png";
            }
        }
    }

    private static void ParseDiscNumber(string value, MediaMetadata metadata)
    {
        int slashPos = value.IndexOf('/');
        if (slashPos > 0)
        {
            if (int.TryParse(value.AsSpan(0, slashPos), out int disc))
                metadata.DiscNumber = disc;
            if (int.TryParse(value.AsSpan(slashPos + 1), out int total))
                metadata.DiscTotal = total;
        }
        else if (int.TryParse(value, out int disc))
        {
            metadata.DiscNumber = disc;
        }
    }

    private static bool TryParseReplayGain(string value, out double gain)
    {
        gain = 0;
        value = value.Trim();
        if (value.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - 2).Trim();
        return double.TryParse(value, out gain);
    }

    private static string ReadUnicodeString(Stream stream, int byteLength)
    {
        if (byteLength <= 0)
            return string.Empty;

        byte[] data = new byte[byteLength];
        stream.Read(data);
        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }
}

/// <summary>
/// Unified metadata reader that detects file format and uses appropriate parser.
/// </summary>
public sealed class MetadataReader
{
    private readonly Id3TagParser id3Parser = new();
    private readonly VorbisCommentParser vorbisParser = new();
    private readonly ApeTagParser apeParser = new();
    private readonly Mp4MetadataParser mp4Parser = new();
    private readonly AsfMetadataParser asfParser = new();

    /// <summary>
    /// Read metadata from a file.
    /// </summary>
    public MediaMetadata ReadFromFile(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        using var stream = File.OpenRead(filePath);
        return ReadFromStream(stream, extension);
    }

    /// <summary>
    /// Read metadata from a stream with format hint.
    /// </summary>
    public MediaMetadata ReadFromStream(Stream stream, string extension)
    {
        return extension switch
        {
            ".mp3" => ParseMp3(stream),
            ".flac" => ParseFlac(stream),
            ".ogg" or ".oga" => ParseOgg(stream),
            ".opus" => ParseOpus(stream),
            ".m4a" or ".m4b" or ".m4p" or ".mp4" or ".m4v" or ".mov" => mp4Parser.Parse(stream),
            ".wma" or ".wmv" or ".asf" => asfParser.Parse(stream),
            ".ape" or ".mpc" or ".wv" => ParseApeTagged(stream),
            _ => TryAllParsers(stream)
        };
    }

    private MediaMetadata ParseMp3(Stream stream)
    {
        var metadata = id3Parser.Parse(stream);

        // Also check for APE tags at end
        if (stream.CanSeek && stream.Length >= 32)
        {
            long pos = stream.Position;
            stream.Position = Math.Max(0, stream.Length - 8192);
            byte[] endData = new byte[(int)Math.Min(8192, stream.Length)];
            stream.Read(endData);
            stream.Position = pos;

            if (apeParser.TryParse(endData, out var apeMetadata))
            {
                MergeMetadata(metadata, apeMetadata);
            }
        }

        return metadata;
    }

    private MediaMetadata ParseFlac(Stream stream)
    {
        var metadata = new MediaMetadata();

        if (!stream.CanSeek)
            return metadata;

        stream.Position = 0;
        Span<byte> header = stackalloc byte[4];
        if (stream.Read(header) < 4)
            return metadata;

        // Check FLAC signature
        if (header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
            return metadata;

        // Parse metadata blocks
        while (stream.Position + 4 <= stream.Length)
        {
            Span<byte> blockHeader = stackalloc byte[4];
            if (stream.Read(blockHeader) < 4)
                break;

            bool isLast = (blockHeader[0] & 0x80) != 0;
            int blockType = blockHeader[0] & 0x7F;
            int blockSize = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

            if (blockSize <= 0 || stream.Position + blockSize > stream.Length)
                break;

            if (blockType == 4) // Vorbis comment
            {
                byte[] blockData = new byte[blockSize];
                stream.Read(blockData);
                var vorbisMetadata = vorbisParser.ParseFlacBlock(blockData);
                MergeMetadata(metadata, vorbisMetadata);
            }
            else if (blockType == 6) // Picture
            {
                ParseFlacPicture(stream, blockSize, metadata);
            }
            else
            {
                stream.Position += blockSize;
            }

            if (isLast)
                break;
        }

        return metadata;
    }

    private void ParseFlacPicture(Stream stream, int blockSize, MediaMetadata metadata)
    {
        if (blockSize < 32)
        {
            stream.Position += blockSize;
            return;
        }

        Span<byte> pictureHeader = stackalloc byte[8];
        stream.Read(pictureHeader);

        int pictureType = BinaryPrimitives.ReadInt32BigEndian(pictureHeader);
        int mimeLength = BinaryPrimitives.ReadInt32BigEndian(pictureHeader.Slice(4));

        if (mimeLength <= 0 || mimeLength > 256)
        {
            stream.Position += blockSize - 8;
            return;
        }

        byte[] mimeBytes = new byte[mimeLength];
        stream.Read(mimeBytes);
        string mimeType = Encoding.ASCII.GetString(mimeBytes);

        // Skip description length and description
        Span<byte> descLen = stackalloc byte[4];
        stream.Read(descLen);
        int descLength = BinaryPrimitives.ReadInt32BigEndian(descLen);
        stream.Position += descLength;

        // Skip width, height, depth, colors (16 bytes)
        stream.Position += 16;

        // Read data length and data
        Span<byte> dataLen = stackalloc byte[4];
        stream.Read(dataLen);
        int dataLength = BinaryPrimitives.ReadInt32BigEndian(dataLen);

        if (dataLength > 0 && dataLength < 50_000_000 && metadata.AlbumArt == null)
        {
            metadata.AlbumArt = new byte[dataLength];
            stream.Read(metadata.AlbumArt);
            metadata.AlbumArtMimeType = mimeType;
        }
    }

    private MediaMetadata ParseOgg(Stream stream)
    {
        // Simplified OGG parsing - would need full OGG demuxer for complete support
        return vorbisParser.Parse(ReadOnlySpan<byte>.Empty);
    }

    private MediaMetadata ParseOpus(Stream stream)
    {
        return vorbisParser.Parse(ReadOnlySpan<byte>.Empty);
    }

    private MediaMetadata ParseApeTagged(Stream stream)
    {
        var metadata = new MediaMetadata();

        if (stream.CanSeek && stream.Length >= 32)
        {
            stream.Position = Math.Max(0, stream.Length - 8192);
            byte[] endData = new byte[(int)Math.Min(8192, stream.Length)];
            stream.Read(endData);

            apeParser.TryParse(endData, out metadata);
        }

        return metadata ?? new MediaMetadata();
    }

    private MediaMetadata TryAllParsers(Stream stream)
    {
        var metadata = id3Parser.Parse(stream);
        if (!string.IsNullOrEmpty(metadata.Title) || !string.IsNullOrEmpty(metadata.Artist))
            return metadata;

        // Try other formats
        stream.Position = 0;
        metadata = mp4Parser.Parse(stream);
        if (!string.IsNullOrEmpty(metadata.Title))
            return metadata;

        stream.Position = 0;
        metadata = asfParser.Parse(stream);
        return metadata;
    }

    private static void MergeMetadata(MediaMetadata target, MediaMetadata source)
    {
        target.Title ??= source.Title;
        target.Artist ??= source.Artist;
        target.Album ??= source.Album;
        target.AlbumArtist ??= source.AlbumArtist;
        target.Genre ??= source.Genre;
        target.Year ??= source.Year;
        target.Composer ??= source.Composer;
        target.Comment ??= source.Comment;
        target.Lyrics ??= source.Lyrics;
        target.Copyright ??= source.Copyright;
        target.Encoder ??= source.Encoder;

        if (target.TrackNumber == 0) target.TrackNumber = source.TrackNumber;
        if (target.TrackTotal == 0) target.TrackTotal = source.TrackTotal;
        if (target.DiscNumber == 0) target.DiscNumber = source.DiscNumber;
        if (target.DiscTotal == 0) target.DiscTotal = source.DiscTotal;

        target.ReplayGainTrack ??= source.ReplayGainTrack;
        target.ReplayGainAlbum ??= source.ReplayGainAlbum;
        target.AlbumArt ??= source.AlbumArt;
        target.AlbumArtMimeType ??= source.AlbumArtMimeType;

        foreach (var kvp in source.CustomTags)
        {
            target.CustomTags.TryAdd(kvp.Key, kvp.Value);
        }
    }
}

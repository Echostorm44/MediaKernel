// MediaKernel - ID3 Tag Parser
// Parses ID3v1 and ID3v2 metadata from audio files (MP3, FLAC, etc.)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// Parsed media metadata from ID3 tags or other sources.
/// </summary>
public sealed class MediaMetadata
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public string? Year { get; set; }
    public int TrackNumber { get; set; }
    public int TrackTotal { get; set; }
    public int DiscNumber { get; set; }
    public int DiscTotal { get; set; }
    public string? Composer { get; set; }
    public string? Comment { get; set; }
    public string? Lyrics { get; set; }
    public string? Copyright { get; set; }
    public string? Encoder { get; set; }
    public double? ReplayGainTrack { get; set; }
    public double? ReplayGainAlbum { get; set; }
    public byte[]? AlbumArt { get; set; }
    public string? AlbumArtMimeType { get; set; }
    public long DurationMilliseconds { get; set; }

    /// <summary>Raw tag frames for advanced access.</summary>
    public Dictionary<string, string> CustomTags { get; } = new();
}

/// <summary>
/// ID3v2 tag version.
/// </summary>
public enum Id3Version
{
    None = 0,
    V1 = 1,
    V2_2 = 22,
    V2_3 = 23,
    V2_4 = 24
}

/// <summary>
/// Parses ID3v1 and ID3v2 tags from audio files.
/// Reference: https://id3.org/id3v2.4.0-structure
/// </summary>
public sealed class Id3TagParser
{
    /// <summary>
    /// Check if stream contains ID3v2 header.
    /// </summary>
    public static bool HasId3v2Header(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 10)
            return false;

        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[10];
            if (stream.Read(header) < 10)
                return false;

            return IsId3v2Header(header);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Parse ID3 tags from a stream.
    /// </summary>
    public MediaMetadata Parse(Stream stream)
    {
        var metadata = new MediaMetadata();

        if (!stream.CanSeek)
            return metadata;

        long originalPosition = stream.Position;
        try
        {
            // Try ID3v2 first (at beginning of file)
            stream.Position = 0;
            if (TryParseId3v2(stream, metadata))
            {
                // Successfully parsed ID3v2
            }

            // Also try ID3v1 at end of file
            if (stream.Length >= 128)
            {
                stream.Position = stream.Length - 128;
                TryParseId3v1(stream, metadata);
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }

        return metadata;
    }

    /// <summary>
    /// Parse ID3 tags from a byte buffer.
    /// </summary>
    public MediaMetadata Parse(ReadOnlySpan<byte> data)
    {
        var metadata = new MediaMetadata();

        // Try ID3v2
        if (data.Length >= 10 && IsId3v2Header(data))
        {
            ParseId3v2Frames(data, metadata);
        }

        // Try ID3v1 at end
        if (data.Length >= 128)
        {
            var id3v1 = data.Slice(data.Length - 128);
            if (id3v1[0] == 'T' && id3v1[1] == 'A' && id3v1[2] == 'G')
            {
                ParseId3v1Data(id3v1, metadata);
            }
        }

        return metadata;
    }

    private static bool IsId3v2Header(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return false;

        // "ID3" signature
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3')
            return false;

        // Version: major < 0xFF, revision < 0xFF
        if (data[3] >= 0xFF || data[4] >= 0xFF)
            return false;

        // Size: syncsafe integer (no 0x80 bits set)
        uint sizeCheck = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(6));
        return (sizeCheck & 0x80808080) == 0;
    }

    private bool TryParseId3v2(Stream stream, MediaMetadata metadata)
    {
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header) < 10)
            return false;

        if (!IsId3v2Header(header))
            return false;

        int majorVersion = header[3];
        int flags = header[5];
        int tagSize = ReadSyncSafeInt(header.Slice(6, 4));

        if (tagSize <= 0 || tagSize > 10_000_000) // Sanity check: 10MB max
            return false;

        byte[] tagData = new byte[tagSize];
        if (stream.Read(tagData) < tagSize)
            return false;

        ParseId3v2Frames(tagData, metadata, majorVersion, flags);
        return true;
    }

    private void ParseId3v2Frames(ReadOnlySpan<byte> data, MediaMetadata metadata)
    {
        if (data.Length < 10 || !IsId3v2Header(data))
            return;

        int majorVersion = data[3];
        int flags = data[5];
        int tagSize = ReadSyncSafeInt(data.Slice(6, 4));

        if (tagSize > data.Length - 10)
            return;

        ParseId3v2Frames(data.Slice(10, tagSize), metadata, majorVersion, flags);
    }

    private void ParseId3v2Frames(ReadOnlySpan<byte> frameData, MediaMetadata metadata, int majorVersion, int flags)
    {
        int offset = 0;

        // Handle extended header if present
        if ((flags & 0x40) != 0 && frameData.Length >= 4)
        {
            int extHeaderSize = majorVersion == 3
                ? BinaryPrimitives.ReadInt32BigEndian(frameData) + 4
                : ReadSyncSafeInt(frameData.Slice(0, 4));

            if (extHeaderSize < frameData.Length)
                offset = extHeaderSize;
        }

        // Frame header size depends on version
        int frameHeaderSize = majorVersion >= 3 ? 10 : 6;
        int frameIdSize = majorVersion >= 3 ? 4 : 3;

        while (offset + frameHeaderSize <= frameData.Length)
        {
            // Check for padding (null bytes)
            if (frameData[offset] == 0)
                break;

            // Read frame ID
            string frameId = Encoding.ASCII.GetString(frameData.Slice(offset, frameIdSize));
            offset += frameIdSize;

            // Read frame size
            int frameSize;
            if (majorVersion == 3)
            {
                frameSize = BinaryPrimitives.ReadInt32BigEndian(frameData.Slice(offset, 4));
                offset += 4;
            }
            else if (majorVersion >= 4)
            {
                frameSize = ReadSyncSafeInt(frameData.Slice(offset, 4));
                offset += 4;
            }
            else // v2.2
            {
                frameSize = (frameData[offset] << 16) | (frameData[offset + 1] << 8) | frameData[offset + 2];
                offset += 3;
            }

            // Read flags (v2.3+)
            if (majorVersion >= 3)
            {
                offset += 2; // Skip frame flags
            }

            if (frameSize <= 0 || offset + frameSize > frameData.Length)
                break;

            ReadOnlySpan<byte> frameContent = frameData.Slice(offset, frameSize);
            ProcessFrame(frameId, frameContent, metadata);

            offset += frameSize;
        }
    }

    private void ProcessFrame(string frameId, ReadOnlySpan<byte> content, MediaMetadata metadata)
    {
        if (content.Length == 0)
            return;

        // Text frames start with 'T' (except TXXX)
        if (frameId.StartsWith('T') && frameId != "TXXX")
        {
            string text = DecodeTextFrame(content);

            switch (frameId)
            {
                case "TIT2": case "TT2": metadata.Title ??= text; break;
                case "TPE1": case "TP1": metadata.Artist ??= text; break;
                case "TPE2": case "TP2": metadata.AlbumArtist ??= text; break;
                case "TALB": case "TAL": metadata.Album ??= text; break;
                case "TCON": case "TCO": metadata.Genre ??= ParseGenre(text); break;
                case "TYER": case "TYE": metadata.Year ??= text; break;
                case "TDRC": metadata.Year ??= text.Length >= 4 ? text.Substring(0, 4) : text; break;
                case "TRCK": case "TRK": ParseTrackNumber(text, metadata); break;
                case "TPOS": case "TPA": ParseDiscNumber(text, metadata); break;
                case "TCOM": case "TCM": metadata.Composer ??= text; break;
                case "TCOP": case "TCR": metadata.Copyright ??= text; break;
                case "TENC": case "TEN": metadata.Encoder ??= text; break;
            }

            metadata.CustomTags.TryAdd(frameId, text);
        }
        else if (frameId == "TXXX" || frameId == "TXX")
        {
            // User-defined text frame
            (string description, string value) = DecodeUserTextFrame(content);

            if (description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseReplayGain(value, out double gain))
                    metadata.ReplayGainTrack = gain;
            }
            else if (description.Equals("REPLAYGAIN_ALBUM_GAIN", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseReplayGain(value, out double gain))
                    metadata.ReplayGainAlbum = gain;
            }

            metadata.CustomTags.TryAdd($"TXXX:{description}", value);
        }
        else if (frameId == "COMM" || frameId == "COM")
        {
            metadata.Comment ??= DecodeCommentFrame(content);
        }
        else if (frameId == "USLT" || frameId == "ULT")
        {
            metadata.Lyrics ??= DecodeUnsyncLyricsFrame(content);
        }
        else if (frameId == "APIC" || frameId == "PIC")
        {
            if (metadata.AlbumArt == null)
            {
                (byte[] imageData, string mimeType) = DecodeApicFrame(content, frameId == "PIC");
                metadata.AlbumArt = imageData;
                metadata.AlbumArtMimeType = mimeType;
            }
        }
    }

    private static string DecodeTextFrame(ReadOnlySpan<byte> content)
    {
        if (content.Length < 1)
            return string.Empty;

        byte encoding = content[0];
        ReadOnlySpan<byte> textData = content.Slice(1);

        return DecodeString(textData, encoding);
    }

    private static (string description, string value) DecodeUserTextFrame(ReadOnlySpan<byte> content)
    {
        if (content.Length < 2)
            return (string.Empty, string.Empty);

        byte encoding = content[0];
        ReadOnlySpan<byte> textData = content.Slice(1);

        // Find null terminator for description
        int nullPos = FindNullTerminator(textData, encoding);
        if (nullPos < 0)
            return (DecodeString(textData, encoding), string.Empty);

        string description = DecodeString(textData.Slice(0, nullPos), encoding);
        int valueOffset = nullPos + (encoding == 1 || encoding == 2 ? 2 : 1);

        if (valueOffset >= textData.Length)
            return (description, string.Empty);

        string value = DecodeString(textData.Slice(valueOffset), encoding);
        return (description, value);
    }

    private static string DecodeCommentFrame(ReadOnlySpan<byte> content)
    {
        if (content.Length < 4)
            return string.Empty;

        byte encoding = content[0];
        // Skip language (3 bytes) and short description
        ReadOnlySpan<byte> textData = content.Slice(4);

        int nullPos = FindNullTerminator(textData, encoding);
        if (nullPos >= 0 && nullPos + 1 < textData.Length)
        {
            int valueOffset = nullPos + (encoding == 1 || encoding == 2 ? 2 : 1);
            if (valueOffset < textData.Length)
                return DecodeString(textData.Slice(valueOffset), encoding);
        }

        return DecodeString(textData, encoding);
    }

    private static string DecodeUnsyncLyricsFrame(ReadOnlySpan<byte> content)
    {
        // Same structure as comment
        return DecodeCommentFrame(content);
    }

    private static (byte[] imageData, string mimeType) DecodeApicFrame(ReadOnlySpan<byte> content, bool isV22)
    {
        if (content.Length < 4)
            return (Array.Empty<byte>(), string.Empty);

        byte encoding = content[0];
        int offset = 1;

        string mimeType;
        if (isV22)
        {
            // v2.2: 3-character image format
            mimeType = Encoding.ASCII.GetString(content.Slice(1, 3)).TrimEnd('\0');
            if (mimeType == "JPG") mimeType = "image/jpeg";
            else if (mimeType == "PNG") mimeType = "image/png";
            offset = 4;
        }
        else
        {
            // v2.3+: null-terminated MIME type
            int nullPos = content.Slice(1).IndexOf((byte)0);
            if (nullPos < 0) return (Array.Empty<byte>(), string.Empty);
            mimeType = Encoding.ASCII.GetString(content.Slice(1, nullPos));
            offset = 2 + nullPos;
        }

        if (offset >= content.Length)
            return (Array.Empty<byte>(), mimeType);

        // Picture type (1 byte)
        offset++;

        // Description (null-terminated)
        int descNullPos = FindNullTerminator(content.Slice(offset), encoding);
        if (descNullPos >= 0)
        {
            offset += descNullPos + (encoding == 1 || encoding == 2 ? 2 : 1);
        }

        if (offset >= content.Length)
            return (Array.Empty<byte>(), mimeType);

        // Remaining is image data
        return (content.Slice(offset).ToArray(), mimeType);
    }

    private static string DecodeString(ReadOnlySpan<byte> data, byte encoding)
    {
        if (data.Length == 0)
            return string.Empty;

        Encoding textEncoding = encoding switch
        {
            0 => Encoding.Latin1,  // ISO-8859-1
            1 => Encoding.Unicode, // UTF-16 with BOM
            2 => Encoding.BigEndianUnicode, // UTF-16BE
            3 => Encoding.UTF8,
            _ => Encoding.Latin1
        };

        // Handle BOM for UTF-16
        if (encoding == 1 && data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
            {
                textEncoding = Encoding.Unicode; // Little-endian
                data = data.Slice(2);
            }
            else if (data[0] == 0xFE && data[1] == 0xFF)
            {
                textEncoding = Encoding.BigEndianUnicode;
                data = data.Slice(2);
            }
        }

        // Remove null terminators
        int endPos = FindNullTerminator(data, encoding);
        if (endPos >= 0)
            data = data.Slice(0, endPos);

        return textEncoding.GetString(data).TrimEnd('\0');
    }

    private static int FindNullTerminator(ReadOnlySpan<byte> data, byte encoding)
    {
        if (encoding == 1 || encoding == 2) // UTF-16
        {
            for (int i = 0; i < data.Length - 1; i += 2)
            {
                if (data[i] == 0 && data[i + 1] == 0)
                    return i;
            }
        }
        else
        {
            int idx = data.IndexOf((byte)0);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static int ReadSyncSafeInt(ReadOnlySpan<byte> data)
    {
        return ((data[0] & 0x7F) << 21) |
               ((data[1] & 0x7F) << 14) |
               ((data[2] & 0x7F) << 7) |
               (data[3] & 0x7F);
    }

    private static string ParseGenre(string text)
    {
        // Handle ID3v1 genre references like "(13)" or "(13)Pop"
        if (text.StartsWith('('))
        {
            int closePos = text.IndexOf(')');
            if (closePos > 1 && int.TryParse(text.AsSpan(1, closePos - 1), out int genreId))
            {
                string genreName = GetId3v1Genre(genreId);
                if (closePos + 1 < text.Length)
                    return text.Substring(closePos + 1);
                return genreName;
            }
        }
        return text;
    }

    private static void ParseTrackNumber(string text, MediaMetadata metadata)
    {
        int slashPos = text.IndexOf('/');
        if (slashPos > 0)
        {
            if (int.TryParse(text.AsSpan(0, slashPos), out int track))
                metadata.TrackNumber = track;
            if (int.TryParse(text.AsSpan(slashPos + 1), out int total))
                metadata.TrackTotal = total;
        }
        else if (int.TryParse(text, out int track))
        {
            metadata.TrackNumber = track;
        }
    }

    private static void ParseDiscNumber(string text, MediaMetadata metadata)
    {
        int slashPos = text.IndexOf('/');
        if (slashPos > 0)
        {
            if (int.TryParse(text.AsSpan(0, slashPos), out int disc))
                metadata.DiscNumber = disc;
            if (int.TryParse(text.AsSpan(slashPos + 1), out int total))
                metadata.DiscTotal = total;
        }
        else if (int.TryParse(text, out int disc))
        {
            metadata.DiscNumber = disc;
        }
    }

    private static bool TryParseReplayGain(string value, out double gain)
    {
        gain = 0;
        // Format: "+2.34 dB" or "-1.5 dB"
        value = value.Trim();
        if (value.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(0, value.Length - 2).Trim();
        }
        return double.TryParse(value, out gain);
    }

    private bool TryParseId3v1(Stream stream, MediaMetadata metadata)
    {
        Span<byte> tag = stackalloc byte[128];
        if (stream.Read(tag) < 128)
            return false;

        // Check "TAG" signature
        if (tag[0] != 'T' || tag[1] != 'A' || tag[2] != 'G')
            return false;

        ParseId3v1Data(tag, metadata);
        return true;
    }

    private static void ParseId3v1Data(ReadOnlySpan<byte> tag, MediaMetadata metadata)
    {
        // ID3v1: fixed-length fields
        metadata.Title ??= Encoding.Latin1.GetString(tag.Slice(3, 30)).TrimEnd('\0', ' ');
        metadata.Artist ??= Encoding.Latin1.GetString(tag.Slice(33, 30)).TrimEnd('\0', ' ');
        metadata.Album ??= Encoding.Latin1.GetString(tag.Slice(63, 30)).TrimEnd('\0', ' ');
        metadata.Year ??= Encoding.Latin1.GetString(tag.Slice(93, 4)).TrimEnd('\0', ' ');

        // Comment field - check for ID3v1.1 track number
        if (tag[125] == 0 && tag[126] != 0)
        {
            // ID3v1.1: byte 126 is track number
            if (metadata.TrackNumber == 0)
                metadata.TrackNumber = tag[126];
            metadata.Comment ??= Encoding.Latin1.GetString(tag.Slice(97, 28)).TrimEnd('\0', ' ');
        }
        else
        {
            metadata.Comment ??= Encoding.Latin1.GetString(tag.Slice(97, 30)).TrimEnd('\0', ' ');
        }

        // Genre (byte index)
        int genreIndex = tag[127];
        if (genreIndex < 192) // Valid range
        {
            metadata.Genre ??= GetId3v1Genre(genreIndex);
        }
    }

    private static string GetId3v1Genre(int index)
    {
        string[] genres =
        {
            "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
            "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock",
            "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack",
            "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance",
            "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
            "Alternative Rock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop",
            "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic",
            "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
            "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American", "Cabaret",
            "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal",
            "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock"
        };

        return index >= 0 && index < genres.Length ? genres[index] : "Unknown";
    }
}

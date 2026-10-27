// MediaKernel - Vorbis Comments Parser
// Parses metadata from OGG, FLAC, Opus, and Speex files

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// Parses Vorbis comments (Xiph format) used in OGG, FLAC, Opus, and Speex.
/// Reference: https://xiph.org/vorbis/doc/v-comment.html
/// </summary>
public sealed class VorbisCommentParser
{
    /// <summary>
    /// Parse Vorbis comments from raw data.
    /// </summary>
    /// <param name="data">Comment block data (after metadata block header for FLAC)</param>
    public MediaMetadata Parse(ReadOnlySpan<byte> data)
    {
        var metadata = new MediaMetadata();
        
        if (data.Length < 8)
            return metadata;
        
        int offset = 0;
        
        // Vendor string length (little-endian)
        if (offset + 4 > data.Length) return metadata;
        uint vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
        offset += 4;
        
        if (vendorLength > 1000000 || offset + vendorLength > data.Length)
            return metadata;
        
        // Skip vendor string
        string vendor = Encoding.UTF8.GetString(data.Slice(offset, (int)vendorLength));
        metadata.CustomTags["VENDOR"] = vendor;
        offset += (int)vendorLength;
        
        // Comment count
        if (offset + 4 > data.Length) return metadata;
        uint commentCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
        offset += 4;
        
        if (commentCount > 1000000) // Sanity check
            return metadata;
        
        // Parse each comment
        for (int i = 0; i < commentCount && offset + 4 <= data.Length; i++)
        {
            uint commentLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
            offset += 4;
            
            if (commentLength > 10000000 || offset + commentLength > data.Length)
                break;
            
            string comment = Encoding.UTF8.GetString(data.Slice(offset, (int)commentLength));
            offset += (int)commentLength;
            
            ProcessComment(comment, metadata);
        }
        
        return metadata;
    }
    
    /// <summary>
    /// Parse Vorbis comments from a FLAC metadata block.
    /// The block should start right after the METADATA_BLOCK_HEADER.
    /// </summary>
    public MediaMetadata ParseFlacBlock(ReadOnlySpan<byte> blockData)
    {
        // FLAC Vorbis comment block is same format as standard Vorbis comments
        return Parse(blockData);
    }
    
    /// <summary>
    /// Parse Vorbis comments from an OGG comment packet.
    /// </summary>
    /// <param name="packetData">Full OGG packet data including framing</param>
    /// <param name="codecType">Codec type to determine header format</param>
    public MediaMetadata ParseOggPacket(ReadOnlySpan<byte> packetData, VorbisCodecType codecType)
    {
        int headerOffset = codecType switch
        {
            VorbisCodecType.Vorbis => 7, // "\x03vorbis" header
            VorbisCodecType.Opus => 8,   // "OpusTags" header
            VorbisCodecType.Theora => 7, // "\x81theora" header
            _ => 0
        };
        
        // Verify and skip header
        if (packetData.Length <= headerOffset)
            return new MediaMetadata();
        
        bool headerValid = codecType switch
        {
            VorbisCodecType.Vorbis => packetData[0] == 0x03 && 
                                      packetData.Slice(1, 6).SequenceEqual("vorbis"u8),
            VorbisCodecType.Opus => packetData.Slice(0, 8).SequenceEqual("OpusTags"u8),
            VorbisCodecType.Theora => packetData[0] == 0x81 &&
                                      packetData.Slice(1, 6).SequenceEqual("theora"u8),
            _ => true
        };
        
        if (!headerValid)
            return new MediaMetadata();
        
        return Parse(packetData.Slice(headerOffset));
    }
    
    private void ProcessComment(string comment, MediaMetadata metadata)
    {
        int equalsPos = comment.IndexOf('=');
        if (equalsPos <= 0)
            return;
        
        string key = comment.Substring(0, equalsPos).ToUpperInvariant();
        string value = comment.Substring(equalsPos + 1);
        
        // Store in custom tags
        if (!metadata.CustomTags.ContainsKey(key))
            metadata.CustomTags[key] = value;
        else
            metadata.CustomTags[key] += "; " + value; // Append for multi-value
        
        // Map to standard fields
        switch (key)
        {
            case "TITLE":
                metadata.Title ??= value;
                break;
            case "ARTIST":
            case "PERFORMER":
                metadata.Artist ??= value;
                break;
            case "ALBUMARTIST":
            case "ALBUM ARTIST":
                metadata.AlbumArtist ??= value;
                break;
            case "ALBUM":
                metadata.Album ??= value;
                break;
            case "GENRE":
                metadata.Genre ??= value;
                break;
            case "DATE":
            case "YEAR":
                if (value.Length >= 4)
                    metadata.Year ??= value.Substring(0, Math.Min(4, value.Length));
                else
                    metadata.Year ??= value;
                break;
            case "TRACKNUMBER":
                {
                    int trackNum = metadata.TrackNumber, trackTotal = metadata.TrackTotal;
                    ParseTrackOrDisc(value, ref trackNum, ref trackTotal);
                    metadata.TrackNumber = trackNum;
                    metadata.TrackTotal = trackTotal;
                }
                break;
            case "TRACKTOTAL":
            case "TOTALTRACKS":
                if (int.TryParse(value, out int total))
                    metadata.TrackTotal = total;
                break;
            case "DISCNUMBER":
                {
                    int discNum = metadata.DiscNumber, discTotal = metadata.DiscTotal;
                    ParseTrackOrDisc(value, ref discNum, ref discTotal);
                    metadata.DiscNumber = discNum;
                    metadata.DiscTotal = discTotal;
                }
                break;
            case "DISCTOTAL":
            case "TOTALDISCS":
                if (int.TryParse(value, out int totalDiscs))
                    metadata.DiscTotal = totalDiscs;
                break;
            case "COMPOSER":
                metadata.Composer ??= value;
                break;
            case "COMMENT":
            case "DESCRIPTION":
                metadata.Comment ??= value;
                break;
            case "LYRICS":
            case "UNSYNCEDLYRICS":
                metadata.Lyrics ??= value;
                break;
            case "COPYRIGHT":
                metadata.Copyright ??= value;
                break;
            case "ENCODER":
            case "ENCODED-BY":
                metadata.Encoder ??= value;
                break;
            case "REPLAYGAIN_TRACK_GAIN":
                if (TryParseReplayGain(value, out double trackGain))
                    metadata.ReplayGainTrack = trackGain;
                break;
            case "REPLAYGAIN_ALBUM_GAIN":
                if (TryParseReplayGain(value, out double albumGain))
                    metadata.ReplayGainAlbum = albumGain;
                break;
        }
    }
    
    private static void ParseTrackOrDisc(string value, ref int number, ref int total)
    {
        int slashPos = value.IndexOf('/');
        if (slashPos > 0)
        {
            if (int.TryParse(value.AsSpan(0, slashPos), out int num))
                number = num;
            if (int.TryParse(value.AsSpan(slashPos + 1), out int tot))
                total = tot;
        }
        else if (int.TryParse(value, out int num))
        {
            number = num;
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
}

/// <summary>
/// Codec types for OGG Vorbis comment parsing.
/// </summary>
public enum VorbisCodecType
{
    /// <summary>Raw Vorbis comments (FLAC block).</summary>
    Raw,
    /// <summary>Vorbis audio codec (\x03vorbis header).</summary>
    Vorbis,
    /// <summary>Opus audio codec (OpusTags header).</summary>
    Opus,
    /// <summary>Theora video codec (\x81theora header).</summary>
    Theora
}

/// <summary>
/// Parses APE tags commonly used in WavPack, MusePack, and some MP3 files.
/// Reference: http://wiki.hydrogenaud.io/index.php?title=APE
/// </summary>
public sealed class ApeTagParser
{
    private const uint ApeTagPreamble = 0x45504154; // "APET" in little-endian
    private const uint ApeTagPreamble2 = 0x41474558; // "AGEX" - "APETAGEX" continuation
    
    /// <summary>
    /// Try to find and parse APE tags at end of file.
    /// </summary>
    public bool TryParse(ReadOnlySpan<byte> fileEnd, out MediaMetadata metadata)
    {
        metadata = new MediaMetadata();
        
        // APE tag footer is 32 bytes at end of file
        if (fileEnd.Length < 32)
            return false;
        
        // Look for "APETAGEX" signature at end
        var footer = fileEnd.Slice(fileEnd.Length - 32);
        if (!IsApeTagHeader(footer))
            return false;
        
        // Read tag size from footer
        int version = BinaryPrimitives.ReadInt32LittleEndian(footer.Slice(8));
        int tagSize = BinaryPrimitives.ReadInt32LittleEndian(footer.Slice(12));
        int itemCount = BinaryPrimitives.ReadInt32LittleEndian(footer.Slice(16));
        int flags = BinaryPrimitives.ReadInt32LittleEndian(footer.Slice(20));
        
        if (version < 1000 || version > 2000)
            return false;
        
        if (tagSize < 32 || tagSize > 10_000_000 || itemCount > 1000)
            return false;
        
        // Check if we have enough data (tag data + footer)
        bool hasHeader = (flags & 0x80000000) != 0;
        int totalSize = tagSize + (hasHeader ? 32 : 0);
        
        if (fileEnd.Length < totalSize)
            return false;
        
        // Parse items
        var tagData = fileEnd.Slice(fileEnd.Length - tagSize);
        ParseItems(tagData.Slice(0, tagSize - 32), itemCount, metadata);
        
        return true;
    }
    
    /// <summary>
    /// Parse APE tag data directly.
    /// </summary>
    public MediaMetadata Parse(ReadOnlySpan<byte> data)
    {
        var metadata = new MediaMetadata();
        
        if (data.Length < 32)
            return metadata;
        
        // Check for header
        if (!IsApeTagHeader(data))
            return metadata;
        
        int tagSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12));
        int itemCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16));
        
        if (itemCount > 1000 || tagSize > data.Length)
            return metadata;
        
        // Items start after header
        ParseItems(data.Slice(32, Math.Min(tagSize - 32, data.Length - 32)), itemCount, metadata);
        
        return metadata;
    }
    
    private static bool IsApeTagHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return false;
        
        // "APETAGEX" signature
        return data[0] == 'A' && data[1] == 'P' && data[2] == 'E' && data[3] == 'T' &&
               data[4] == 'A' && data[5] == 'G' && data[6] == 'E' && data[7] == 'X';
    }
    
    private void ParseItems(ReadOnlySpan<byte> data, int itemCount, MediaMetadata metadata)
    {
        int offset = 0;
        
        for (int i = 0; i < itemCount && offset + 8 < data.Length; i++)
        {
            // Item header: 4 bytes size, 4 bytes flags
            int valueSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
            int flags = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4));
            offset += 8;
            
            if (valueSize < 0 || valueSize > 10_000_000)
                break;
            
            // Key is null-terminated string
            int keyEnd = data.Slice(offset).IndexOf((byte)0);
            if (keyEnd < 0 || offset + keyEnd >= data.Length)
                break;
            
            string key = Encoding.ASCII.GetString(data.Slice(offset, keyEnd));
            offset += keyEnd + 1; // Skip null terminator
            
            if (offset + valueSize > data.Length)
                break;
            
            // Item type from flags
            int itemType = (flags >> 1) & 3;
            
            if (itemType == 0) // UTF-8 text
            {
                string value = Encoding.UTF8.GetString(data.Slice(offset, valueSize));
                ProcessItem(key, value, metadata);
            }
            else if (itemType == 1) // Binary data
            {
                // Could be album art
                if (key.Equals("Cover Art (front)", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Cover Art (back)", StringComparison.OrdinalIgnoreCase))
                {
                    // APE cover art has filename first, then binary data
                    var binaryData = data.Slice(offset, valueSize);
                    int nullPos = binaryData.IndexOf((byte)0);
                    if (nullPos >= 0 && nullPos + 1 < binaryData.Length)
                    {
                        metadata.AlbumArt = binaryData.Slice(nullPos + 1).ToArray();
                        // Detect MIME type from magic bytes
                        if (metadata.AlbumArt.Length >= 3)
                        {
                            if (metadata.AlbumArt[0] == 0xFF && metadata.AlbumArt[1] == 0xD8)
                                metadata.AlbumArtMimeType = "image/jpeg";
                            else if (metadata.AlbumArt[0] == 0x89 && metadata.AlbumArt[1] == 0x50)
                                metadata.AlbumArtMimeType = "image/png";
                        }
                    }
                }
            }
            
            offset += valueSize;
        }
    }
    
    private void ProcessItem(string key, string value, MediaMetadata metadata)
    {
        // Store raw
        metadata.CustomTags.TryAdd(key.ToUpperInvariant(), value);
        
        // Map to standard fields
        switch (key.ToUpperInvariant())
        {
            case "TITLE":
                metadata.Title ??= value;
                break;
            case "ARTIST":
                metadata.Artist ??= value;
                break;
            case "ALBUM ARTIST":
            case "ALBUMARTIST":
                metadata.AlbumArtist ??= value;
                break;
            case "ALBUM":
                metadata.Album ??= value;
                break;
            case "GENRE":
                metadata.Genre ??= value;
                break;
            case "YEAR":
                metadata.Year ??= value.Length >= 4 ? value.Substring(0, 4) : value;
                break;
            case "TRACK":
                {
                    int trackNum = metadata.TrackNumber, trackTotal = metadata.TrackTotal;
                    ParseTrackOrDisc(value, ref trackNum, ref trackTotal);
                    metadata.TrackNumber = trackNum;
                    metadata.TrackTotal = trackTotal;
                }
                break;
            case "DISC":
                {
                    int discNum = metadata.DiscNumber, discTotal = metadata.DiscTotal;
                    ParseTrackOrDisc(value, ref discNum, ref discTotal);
                    metadata.DiscNumber = discNum;
                    metadata.DiscTotal = discTotal;
                }
                break;
            case "COMPOSER":
                metadata.Composer ??= value;
                break;
            case "COMMENT":
                metadata.Comment ??= value;
                break;
            case "COPYRIGHT":
                metadata.Copyright ??= value;
                break;
            case "REPLAYGAIN_TRACK_GAIN":
                if (TryParseReplayGain(value, out double trackGain))
                    metadata.ReplayGainTrack = trackGain;
                break;
            case "REPLAYGAIN_ALBUM_GAIN":
                if (TryParseReplayGain(value, out double albumGain))
                    metadata.ReplayGainAlbum = albumGain;
                break;
        }
    }
    
    private static void ParseTrackOrDisc(string value, ref int number, ref int total)
    {
        int slashPos = value.IndexOf('/');
        if (slashPos > 0)
        {
            if (int.TryParse(value.AsSpan(0, slashPos), out int num))
                number = num;
            if (int.TryParse(value.AsSpan(slashPos + 1), out int tot))
                total = tot;
        }
        else if (int.TryParse(value, out int num))
        {
            number = num;
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
}

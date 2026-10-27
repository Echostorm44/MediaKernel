// MediaKernel - VobSub Demuxer
// Demuxes VobSub (.idx/.sub) subtitle files

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// VobSub subtitle entry with timing and file position.
/// </summary>
public readonly record struct VobSubEntry(
    long StartTimeMicroseconds,
    long SubFilePosition,
    int TrackIndex);

/// <summary>
/// VobSub track (language) information.
/// </summary>
public sealed class VobSubTrack
{
    public int TrackId { get; init; }
    public string? Language { get; init; }
    public string? LanguageId { get; init; }
    public List<VobSubEntry> Entries { get; } = new();
    public int CurrentEntryIndex { get; set; }
}

/// <summary>
/// Demuxer for VobSub subtitle files (.idx + .sub).
/// VobSub is the standard format for DVD subtitles extracted to files.
/// </summary>
public sealed partial class VobSubDemuxer : IDisposable
{
    private Stream? idxStream;
    private Stream? subStream;
    private bool ownsStreams;
    private bool isInitialized;
    
    private readonly List<VobSubTrack> tracks = new();
    private uint[] palette = new uint[16];
    private bool hasPalette;
    private int originalWidth;
    private int originalHeight;
    private long durationMicroseconds;
    
    // MPEG PS constants
    private const uint PackStartCode = 0x000001BA;
    private const uint PesPrivateStream1 = 0x000001BD;
    
    /// <summary>
    /// Number of subtitle tracks (languages).
    /// </summary>
    public int TrackCount => tracks.Count;
    
    /// <summary>
    /// Total duration in microseconds.
    /// </summary>
    public long DurationMicroseconds => durationMicroseconds;
    
    /// <summary>
    /// Original video frame width (for subtitle positioning).
    /// </summary>
    public int OriginalWidth => originalWidth;
    
    /// <summary>
    /// Original video frame height (for subtitle positioning).
    /// </summary>
    public int OriginalHeight => originalHeight;
    
    /// <summary>
    /// Whether a palette was defined in the IDX file.
    /// </summary>
    public bool HasPalette => hasPalette;
    
    /// <summary>
    /// 16-color palette in ARGB format.
    /// </summary>
    public ReadOnlySpan<uint> Palette => palette;
    
    /// <summary>
    /// Get track information.
    /// </summary>
    public VobSubTrack? GetTrack(int index)
    {
        if (index < 0 || index >= tracks.Count)
            return null;
        return tracks[index];
    }
    
    /// <summary>
    /// Initialize the demuxer from a .idx file path.
    /// The .sub file is expected to be in the same directory with the same base name.
    /// </summary>
    public bool Initialize(string idxPath)
    {
        if (isInitialized)
            return false;
        
        if (!File.Exists(idxPath))
            return false;
        
        // Derive .sub path
        string subPath = Path.ChangeExtension(idxPath, ".sub");
        if (!File.Exists(subPath))
        {
            // Try lowercase
            subPath = Path.ChangeExtension(idxPath, ".SUB");
            if (!File.Exists(subPath))
                return false;
        }
        
        try
        {
            idxStream = File.OpenRead(idxPath);
            subStream = File.OpenRead(subPath);
            ownsStreams = true;
            
            return ParseIdxFile();
        }
        catch
        {
            Dispose();
            return false;
        }
    }
    
    /// <summary>
    /// Initialize from streams.
    /// </summary>
    public bool Initialize(Stream idxStream, Stream subStream, bool takeOwnership = false)
    {
        if (isInitialized)
            return false;
        
        this.idxStream = idxStream;
        this.subStream = subStream;
        this.ownsStreams = takeOwnership;
        
        return ParseIdxFile();
    }
    
    private bool ParseIdxFile()
    {
        if (idxStream == null)
            return false;
        
        using var reader = new StreamReader(idxStream, Encoding.UTF8, leaveOpen: true);
        
        VobSubTrack? currentTrack = null;
        
        while (!reader.EndOfStream)
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;
            
            // Parse size
            if (line.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                var match = SizeRegex().Match(line);
                if (match.Success)
                {
                    originalWidth = int.Parse(match.Groups[1].Value);
                    originalHeight = int.Parse(match.Groups[2].Value);
                }
            }
            // Parse palette
            else if (line.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
            {
                ParsePalette(line);
            }
            // Parse language track
            else if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                var match = IdRegex().Match(line);
                if (match.Success)
                {
                    string langId = match.Groups[1].Value;
                    int trackIndex = int.Parse(match.Groups[2].Value);
                    
                    currentTrack = new VobSubTrack
                    {
                        TrackId = trackIndex,
                        LanguageId = langId,
                        Language = GetLanguageName(langId)
                    };
                    tracks.Add(currentTrack);
                }
            }
            // Parse timestamp entry
            else if (line.StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentTrack != null)
                {
                    var match = TimestampRegex().Match(line);
                    if (match.Success)
                    {
                        int hours = int.Parse(match.Groups[1].Value);
                        int minutes = int.Parse(match.Groups[2].Value);
                        int seconds = int.Parse(match.Groups[3].Value);
                        int ms = int.Parse(match.Groups[4].Value);
                        
                        long timeMicros = ((hours * 3600L + minutes * 60L + seconds) * 1000L + ms) * 1000L;
                        
                        // Parse file position
                        long filePos = 0;
                        if (match.Groups[5].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            filePos = long.Parse(match.Groups[5].Value[2..], NumberStyles.HexNumber);
                        }
                        else
                        {
                            filePos = long.Parse(match.Groups[5].Value);
                        }
                        
                        currentTrack.Entries.Add(new VobSubEntry(
                            timeMicros,
                            filePos,
                            currentTrack.TrackId));
                        
                        // Update duration
                        if (timeMicros > durationMicroseconds)
                            durationMicroseconds = timeMicros;
                    }
                }
            }
        }
        
        // Add some buffer to duration
        if (durationMicroseconds > 0)
            durationMicroseconds += 1_000_000; // +1 second
        
        isInitialized = tracks.Count > 0;
        return isInitialized;
    }
    
    private void ParsePalette(string line)
    {
        // palette: RRGGBB, RRGGBB, ... (16 values)
        var match = PaletteRegex().Match(line);
        if (!match.Success)
            return;
        
        string values = match.Groups[1].Value;
        string[] parts = values.Split(',', StringSplitOptions.TrimEntries);
        
        for (int i = 0; i < Math.Min(parts.Length, 16); i++)
        {
            if (uint.TryParse(parts[i], NumberStyles.HexNumber, null, out uint color))
            {
                // Convert RGB to ARGB (add full alpha)
                palette[i] = 0xFF000000 | color;
            }
        }
        
        hasPalette = true;
    }
    
    /// <summary>
    /// Read a subtitle packet from the .sub file at the specified position.
    /// Returns the raw SPU (subtitle picture unit) data.
    /// </summary>
    public byte[]? ReadSubtitlePacket(long filePosition, out long timestampMicroseconds)
    {
        timestampMicroseconds = 0;
        
        if (subStream == null || !subStream.CanSeek)
            return null;
        
        try
        {
            subStream.Position = filePosition;
            return ReadPesPacket(out timestampMicroseconds);
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Read the next subtitle for a track.
    /// </summary>
    public byte[]? ReadNextSubtitle(int trackIndex, out long timestampMicroseconds, out int entryIndex)
    {
        timestampMicroseconds = 0;
        entryIndex = -1;
        
        if (trackIndex < 0 || trackIndex >= tracks.Count)
            return null;
        
        var track = tracks[trackIndex];
        if (track.CurrentEntryIndex >= track.Entries.Count)
            return null;
        
        var entry = track.Entries[track.CurrentEntryIndex];
        entryIndex = track.CurrentEntryIndex;
        track.CurrentEntryIndex++;
        
        timestampMicroseconds = entry.StartTimeMicroseconds;
        return ReadSubtitlePacket(entry.SubFilePosition, out _);
    }
    
    /// <summary>
    /// Seek to a specific time for all tracks.
    /// </summary>
    public void Seek(long timeMicroseconds)
    {
        foreach (var track in tracks)
        {
            track.CurrentEntryIndex = 0;
            
            while (track.CurrentEntryIndex < track.Entries.Count &&
                   track.Entries[track.CurrentEntryIndex].StartTimeMicroseconds < timeMicroseconds)
            {
                track.CurrentEntryIndex++;
            }
        }
    }
    
    /// <summary>
    /// Reset all tracks to the beginning.
    /// </summary>
    public void Reset()
    {
        foreach (var track in tracks)
        {
            track.CurrentEntryIndex = 0;
        }
    }
    
    private byte[]? ReadPesPacket(out long ptsMicroseconds)
    {
        ptsMicroseconds = 0;
        
        if (subStream == null)
            return null;
        
        Span<byte> header = stackalloc byte[14];
        
        // Read MPEG PS pack header
        if (subStream.Read(header[..4]) != 4)
            return null;
        
        uint startCode = BinaryPrimitives.ReadUInt32BigEndian(header);
        
        // Skip pack header if present
        if (startCode == PackStartCode)
        {
            // MPEG-2 pack header
            if (subStream.Read(header[..10]) != 10)
                return null;
            
            // Check for stuffing bytes
            int stuffing = header[9] & 0x07;
            if (stuffing > 0)
            {
                Span<byte> stuff = stackalloc byte[stuffing];
                subStream.Read(stuff);
            }
            
            // Read next start code
            if (subStream.Read(header[..4]) != 4)
                return null;
            
            startCode = BinaryPrimitives.ReadUInt32BigEndian(header);
        }
        
        // Expect private stream 1 (DVD subtitles)
        if (startCode != PesPrivateStream1)
            return null;
        
        // Read PES packet length
        if (subStream.Read(header[..2]) != 2)
            return null;
        
        int pesLength = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (pesLength <= 0)
            return null;
        
        // Read PES data
        byte[] pesData = new byte[pesLength];
        int bytesRead = 0;
        while (bytesRead < pesLength)
        {
            int read = subStream.Read(pesData, bytesRead, pesLength - bytesRead);
            if (read <= 0)
                break;
            bytesRead += read;
        }
        
        if (bytesRead < 3)
            return null;
        
        // Parse PES header
        int headerDataLength = pesData[2];
        int dataStart = 3 + headerDataLength;
        
        // Check for PTS
        if ((pesData[1] & 0x80) != 0 && headerDataLength >= 5)
        {
            // PTS present
            long pts = ParsePts(pesData.AsSpan(3, 5));
            ptsMicroseconds = pts * 1000000 / 90000; // Convert 90kHz to microseconds
        }
        
        // Skip stream ID byte in private stream 1
        if (dataStart < pesData.Length)
        {
            dataStart++; // Skip subtitle stream ID
        }
        
        if (dataStart >= pesData.Length)
            return null;
        
        // Return SPU data
        int spuLength = pesData.Length - dataStart;
        byte[] spuData = new byte[spuLength];
        Array.Copy(pesData, dataStart, spuData, 0, spuLength);
        
        return spuData;
    }
    
    private static long ParsePts(ReadOnlySpan<byte> data)
    {
        // PTS is 33 bits encoded in 5 bytes
        long pts = ((long)(data[0] & 0x0E) << 29) |
                   ((long)data[1] << 22) |
                   ((long)(data[2] & 0xFE) << 14) |
                   ((long)data[3] << 7) |
                   ((long)(data[4] & 0xFE) >> 1);
        return pts;
    }
    
    private static string GetLanguageName(string langId)
    {
        return langId.ToLowerInvariant() switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            "nl" => "Dutch",
            "sv" => "Swedish",
            "no" => "Norwegian",
            "da" => "Danish",
            "fi" => "Finnish",
            "pl" => "Polish",
            "cs" => "Czech",
            "hu" => "Hungarian",
            "tr" => "Turkish",
            "ar" => "Arabic",
            "he" => "Hebrew",
            "th" => "Thai",
            "vi" => "Vietnamese",
            _ => langId
        };
    }
    
    /// <summary>
    /// Check if a file is a VobSub IDX file.
    /// </summary>
    public static bool IsVobSubFile(string path)
    {
        if (!File.Exists(path))
            return false;
        
        try
        {
            using var reader = new StreamReader(path);
            string? firstLine = reader.ReadLine();
            return firstLine != null && 
                   firstLine.Contains("VobSub index file", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Check if a stream contains VobSub IDX data.
    /// </summary>
    public static bool IsVobSubStream(Stream stream)
    {
        if (!stream.CanRead)
            return false;
        
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            Span<byte> header = stackalloc byte[64];
            int read = stream.Read(header);
            
            if (stream.CanSeek)
                stream.Position = originalPosition;
            
            string text = Encoding.UTF8.GetString(header[..read]);
            return text.Contains("VobSub index file", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
            return false;
        }
    }
    
    public void Dispose()
    {
        if (ownsStreams)
        {
            idxStream?.Dispose();
            subStream?.Dispose();
        }
        idxStream = null;
        subStream = null;
        tracks.Clear();
        isInitialized = false;
    }
    
    [GeneratedRegex(@"size:\s*(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
    
    [GeneratedRegex(@"id:\s*(\w+),\s*index:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdRegex();
    
    [GeneratedRegex(@"timestamp:\s*(\d+):(\d+):(\d+):(\d+),\s*filepos:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampRegex();
    
    [GeneratedRegex(@"palette:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex PaletteRegex();
}

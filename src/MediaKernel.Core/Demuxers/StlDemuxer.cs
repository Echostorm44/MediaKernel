// MediaKernel - EBU STL Subtitle Demuxer
// Demuxer for EBU STL (European Broadcasting Union Subtitling)
// Used in professional broadcast subtitling

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// EBU STL frame rate standard.
/// </summary>
public enum StlFrameRate
{
    /// <summary>25 fps (PAL standard).</summary>
    Fps25,
    
    /// <summary>30 fps (NTSC standard).</summary>
    Fps30
}

/// <summary>
/// EBU STL subtitle entry.
/// </summary>
public sealed record StlSubtitleEntry
{
    /// <summary>Start time in microseconds.</summary>
    public long StartTime { get; init; }
    
    /// <summary>End time in microseconds.</summary>
    public long EndTime { get; init; }
    
    /// <summary>Duration in microseconds.</summary>
    public long Duration => EndTime - StartTime;
    
    /// <summary>Subtitle text content.</summary>
    public string Text { get; init; } = string.Empty;
    
    /// <summary>Subtitle number.</summary>
    public int SubtitleNumber { get; init; }
    
    /// <summary>Is this a comment (not displayed).</summary>
    public bool IsComment { get; init; }
    
    /// <summary>Vertical position (0-22, 0 = bottom).</summary>
    public int VerticalPosition { get; init; }
    
    /// <summary>Justification code (0=unchanged, 1=left, 2=center, 3=right).</summary>
    public int JustificationCode { get; init; }
}

/// <summary>
/// EBU STL file information.
/// </summary>
public sealed class StlFileInfo
{
    /// <summary>Whether parsing was successful.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Frame rate (25 or 30 fps).</summary>
    public StlFrameRate FrameRate { get; init; }
    
    /// <summary>Frames per second.</summary>
    public double FramesPerSecond => FrameRate == StlFrameRate.Fps25 ? 25.0 : 30.0;
    
    /// <summary>Character code table (CCT).</summary>
    public int CharacterCodeTable { get; init; }
    
    /// <summary>Country of origin code.</summary>
    public string CountryOfOrigin { get; init; } = string.Empty;
    
    /// <summary>Original program title.</summary>
    public string OriginalProgramTitle { get; init; } = string.Empty;
    
    /// <summary>Translated program title.</summary>
    public string TranslatedProgramTitle { get; init; } = string.Empty;
    
    /// <summary>Program start time in microseconds.</summary>
    public long ProgramStartTime { get; init; }
    
    /// <summary>Total subtitle count (TTI count).</summary>
    public int SubtitleCount { get; init; }
    
    /// <summary>Maximum number of displayable rows.</summary>
    public int MaxRows { get; init; }
    
    /// <summary>Maximum characters per row.</summary>
    public int MaxCharactersPerRow { get; init; }
    
    /// <summary>Total duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
}

/// <summary>
/// Demuxer for EBU STL (European Broadcasting Union Subtitling) format.
/// Used in professional broadcast for teletext and closed captions.
/// </summary>
public sealed class StlDemuxer : IDisposable
{
    private const int GsiBlockSize = 1024;  // General Subtitle Information block
    private const int TtiBlockSize = 128;   // Text and Timing Information block
    
    // GSI field offsets (per EBU Tech 3264)
    private const int OffsetCodePageNumber = 0;       // 3 bytes (CPN)
    private const int OffsetDiskFormatCode = 3;       // 8 bytes (DFC: STL25.01 or STL30.01)
    private const int OffsetDisplayStandardCode = 11; // 1 byte (DSC)
    private const int OffsetCharacterCodeTable = 12;  // 2 bytes (CCT)
    private const int OffsetLanguageCode = 14;        // 2 bytes (LC)
    private const int OffsetOriginalProgramTitle = 16;    // 32 bytes (OPT)
    private const int OffsetOriginalEpisodeTitle = 48;    // 32 bytes (OET)
    private const int OffsetTranslatedProgramTitle = 80;  // 32 bytes (TPT)
    private const int OffsetTranslatedEpisodeTitle = 112; // 32 bytes (TET)
    private const int OffsetTranslatorName = 144;         // 32 bytes (TN)
    private const int OffsetTotalNumberOfTTI = 224;       // 6 bytes (TNB) - actual per EBU spec
    private const int OffsetMaxCharactersPerRow = 238;    // 5 bytes (MNC)
    private const int OffsetMaxNumberOfRows = 243;        // 5 bytes (MNR)  
    private const int OffsetTimecodeStartOfProgram = 256; // 8 bytes (TCP: hh:mm:ss:ff text)
    private const int OffsetCountryOfOrigin = 274;        // 3 bytes (CO)
    
    // TTI field offsets (within 128-byte block)
    private const int TtiSubtitleGroupNumber = 0;    // 1 byte
    private const int TtiSubtitleNumber = 1;         // 2 bytes
    private const int TtiExtensionBlockNumber = 3;   // 1 byte
    private const int TtiCumulativeStatus = 4;       // 1 byte
    private const int TtiTimecodeIn = 5;             // 4 bytes
    private const int TtiTimecodeOut = 9;            // 4 bytes
    private const int TtiVerticalPosition = 13;      // 1 byte
    private const int TtiJustificationCode = 14;     // 1 byte
    private const int TtiCommentFlag = 15;           // 1 byte
    private const int TtiTextField = 16;             // 112 bytes
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] gsiBlock = new byte[GsiBlockSize];
    private readonly byte[] ttiBlock = new byte[TtiBlockSize];
    
    private StlFrameRate frameRate;
    private double fps;
    private int characterCodeTable;
    private long programStartTime;
    private int ttiCount;
    private List<StlSubtitleEntry> subtitles = new();
    private int currentIndex;
    private bool disposed;
    
    /// <summary>
    /// Creates a new STL demuxer.
    /// </summary>
    public StlDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
    }
    
    /// <summary>
    /// Parse the STL file and extract all subtitles.
    /// </summary>
    public StlFileInfo Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        // Read GSI block
        int read = stream.Read(gsiBlock, 0, GsiBlockSize);
        if (read < GsiBlockSize)
        {
            return new StlFileInfo { IsValid = false };
        }
        
        // Check format identifier
        string formatCode = Encoding.ASCII.GetString(gsiBlock, OffsetDiskFormatCode, 8);
        if (formatCode == "STL25.01")
        {
            frameRate = StlFrameRate.Fps25;
            fps = 25.0;
        }
        else if (formatCode == "STL30.01")
        {
            frameRate = StlFrameRate.Fps30;
            fps = 30.0;
        }
        else
        {
            return new StlFileInfo { IsValid = false };
        }
        
        // Parse GSI fields
        characterCodeTable = ParseInteger(gsiBlock, OffsetCharacterCodeTable, 2);
        string countryOfOrigin = Encoding.ASCII.GetString(gsiBlock, OffsetCountryOfOrigin, 3).Trim();
        string originalTitle = ParseText(gsiBlock, OffsetOriginalProgramTitle, 32);
        string translatedTitle = ParseText(gsiBlock, OffsetTranslatedProgramTitle, 32);
        programStartTime = ParseTextTimecode(gsiBlock, OffsetTimecodeStartOfProgram);
        ttiCount = ParseInteger(gsiBlock, OffsetTotalNumberOfTTI, 6);
        int maxCharsPerRow = ParseInteger(gsiBlock, OffsetMaxCharactersPerRow, 5);
        int maxRows = ParseInteger(gsiBlock, OffsetMaxNumberOfRows, 5);
        
        if (ttiCount <= 0)
        {
            return new StlFileInfo { IsValid = false };
        }
        
        // Parse all TTI blocks
        subtitles.Clear();
        StringBuilder currentText = new();
        StlSubtitleEntry? currentEntry = null;
        long lastEndTime = 0;
        
        for (int i = 0; i < ttiCount; i++)
        {
            read = stream.Read(ttiBlock, 0, TtiBlockSize);
            if (read < TtiBlockSize)
                break;
            
            int extensionBlockNumber = ttiBlock[TtiExtensionBlockNumber];
            
            // Skip certain block types
            if (extensionBlockNumber >= 0xF0 && extensionBlockNumber <= 0xFD)
                continue;
            if (extensionBlockNumber == 0xFE)
                continue;
            
            bool isComment = ttiBlock[TtiCommentFlag] != 0;
            
            if (currentEntry == null || extensionBlockNumber == 0xFF)
            {
                // New subtitle entry
                if (currentEntry != null && currentText.Length > 0)
                {
                    // Save previous entry
                    subtitles.Add(currentEntry with { Text = currentText.ToString().Trim() });
                    currentText.Clear();
                }
                
                long startTime = ParseBinaryTimecode(ttiBlock, TtiTimecodeIn) - programStartTime;
                long endTime = ParseBinaryTimecode(ttiBlock, TtiTimecodeOut) - programStartTime;
                
                if (startTime < 0) startTime = 0;
                if (endTime > lastEndTime) lastEndTime = endTime;
                
                currentEntry = new StlSubtitleEntry
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    SubtitleNumber = subtitles.Count + 1,
                    IsComment = isComment,
                    VerticalPosition = ttiBlock[TtiVerticalPosition],
                    JustificationCode = ttiBlock[TtiJustificationCode]
                };
            }
            
            // Extract text field
            string text = DecodeTextField(ttiBlock, TtiTextField, 112);
            currentText.Append(text);
            
            // End of subtitle block
            if (extensionBlockNumber == 0xFF && !isComment)
            {
                subtitles.Add(currentEntry with { Text = currentText.ToString().Trim() });
                currentEntry = null;
                currentText.Clear();
            }
        }
        
        // Add final entry if pending
        if (currentEntry != null && currentText.Length > 0)
        {
            subtitles.Add(currentEntry with { Text = currentText.ToString().Trim() });
        }
        
        return new StlFileInfo
        {
            IsValid = true,
            FrameRate = frameRate,
            CharacterCodeTable = characterCodeTable,
            CountryOfOrigin = countryOfOrigin,
            OriginalProgramTitle = originalTitle,
            TranslatedProgramTitle = translatedTitle,
            ProgramStartTime = programStartTime,
            SubtitleCount = subtitles.Count,
            MaxRows = maxRows > 0 ? maxRows : 23,
            MaxCharactersPerRow = maxCharsPerRow > 0 ? maxCharsPerRow : 40,
            DurationMicroseconds = lastEndTime
        };
    }
    
    /// <summary>
    /// Get all parsed subtitles.
    /// </summary>
    public IReadOnlyList<StlSubtitleEntry> GetSubtitles()
    {
        return subtitles;
    }
    
    /// <summary>
    /// Get the next subtitle entry.
    /// </summary>
    public bool TryGetNextSubtitle(out StlSubtitleEntry? subtitle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (currentIndex >= subtitles.Count)
        {
            subtitle = null;
            return false;
        }
        
        subtitle = subtitles[currentIndex++];
        return true;
    }
    
    /// <summary>
    /// Seek to a specific time and return the next subtitle at or after that time.
    /// </summary>
    public bool SeekToTime(long timeMicroseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        // Binary search for the subtitle
        int left = 0;
        int right = subtitles.Count - 1;
        
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            
            if (subtitles[mid].StartTime <= timeMicroseconds)
            {
                if (mid == subtitles.Count - 1 || subtitles[mid + 1].StartTime > timeMicroseconds)
                {
                    currentIndex = mid;
                    return true;
                }
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }
        
        currentIndex = 0;
        return subtitles.Count > 0;
    }
    
    /// <summary>
    /// Reset to the beginning of the subtitle list.
    /// </summary>
    public void Reset()
    {
        currentIndex = 0;
    }
    
    /// <summary>
    /// Check if data is a valid STL file.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 11)
            return false;
        
        // Check for STL25.01 or STL30.01 at offset 3
        var formatCode = header.Slice(3, 8);
        
        if (formatCode.SequenceEqual("STL25.01"u8))
            return true;
        if (formatCode.SequenceEqual("STL30.01"u8))
            return true;
        
        return false;
    }
    
    private static int ParseInteger(byte[] data, int offset, int length)
    {
        var span = data.AsSpan(offset, length);
        int result = 0;
        
        foreach (byte b in span)
        {
            if (b >= '0' && b <= '9')
            {
                result = result * 10 + (b - '0');
            }
        }
        
        return result;
    }
    
    private static string ParseText(byte[] data, int offset, int length)
    {
        return Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');
    }
    
    private long ParseTextTimecode(byte[] data, int offset)
    {
        // Format: HHMMSSFF (text)
        int hours = ParseInteger(data, offset, 2);
        int minutes = ParseInteger(data, offset + 2, 2);
        int seconds = ParseInteger(data, offset + 4, 2);
        int frames = ParseInteger(data, offset + 6, 2);
        
        return TimecodeToMicroseconds(hours, minutes, seconds, frames);
    }
    
    private long ParseBinaryTimecode(byte[] data, int offset)
    {
        // Binary format: 4 bytes (hours, minutes, seconds, frames)
        int hours = data[offset];
        int minutes = data[offset + 1];
        int seconds = data[offset + 2];
        int frames = data[offset + 3];
        
        return TimecodeToMicroseconds(hours, minutes, seconds, frames);
    }
    
    private long TimecodeToMicroseconds(int hours, int minutes, int seconds, int frames)
    {
        double totalSeconds = hours * 3600 + minutes * 60 + seconds + frames / fps;
        return (long)(totalSeconds * 1_000_000);
    }
    
    private string DecodeTextField(byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        
        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            
            // Handle special characters
            if (b == 0x8A)
            {
                // Line break
                sb.AppendLine();
            }
            else if (b == 0x8F)
            {
                // End of text
                break;
            }
            else if (b >= 0x80 && b <= 0x87)
            {
                // Color codes - skip
            }
            else if (b >= 0x20 && b < 0x80)
            {
                // Printable ASCII
                sb.Append((char)b);
            }
            else if (b >= 0xA0)
            {
                // Extended Latin (CCT 00 - Latin ISO 6937)
                // Simplified: treat as extended ASCII
                sb.Append((char)b);
            }
        }
        
        return sb.ToString();
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (!leaveOpen)
                stream.Dispose();
        }
    }
}

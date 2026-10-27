// MediaKernel - Audiobook Manager
// Chapter navigation, sleep timer, bookmarks, resume position

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaKernel.Core.Podcast;

namespace MediaKernel.Core.Audiobook;

/// <summary>
/// The shapes this file persists, generated at compile time rather than discovered by
/// reflection - see the note on <c>SettingsJsonContext</c> for why that is the only form
/// that survives trimming and AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<AudiobookEntry>))]
[JsonSerializable(typeof(Dictionary<string, List<Bookmark>>))]
internal sealed partial class AudiobookJsonContext : JsonSerializerContext;

/// <summary>
/// Audiobook playback management with chapters, bookmarks, and resume position.
/// </summary>
public sealed class AudiobookManager : IAsyncDisposable
{
    private readonly string libraryPath;
    private readonly ConcurrentDictionary<string, AudiobookEntry> audiobooks = new();
    private readonly ConcurrentDictionary<string, List<Bookmark>> bookmarks = new();
    private Timer? sleepTimer;
    private DateTimeOffset? sleepTimerEnd;
    private bool disposed;
    
    public event EventHandler? SleepTimerTriggered;
    public event EventHandler<TimeSpan>? SleepTimerTick;
    
    public AudiobookManager(string libraryPath)
    {
        this.libraryPath = libraryPath;
        Directory.CreateDirectory(libraryPath);
    }
    
    /// <summary>
    /// Add an audiobook to the library.
    /// </summary>
    public async Task<AudiobookEntry> AddAudiobookAsync(string path,
        CancellationToken cancellationToken = default)
    {
        string id = GenerateId(path);
        
        var entry = new AudiobookEntry
        {
            Id = id,
            FilePath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            AddedDate = DateTimeOffset.UtcNow
        };
        
        // Try to extract chapters and metadata
        await ExtractChaptersAsync(entry, cancellationToken);
        
        audiobooks[id] = entry;
        await SaveLibraryAsync(cancellationToken);
        
        return entry;
    }
    
    /// <summary>
    /// Add a multi-file audiobook (folder of MP3s).
    /// </summary>
    public async Task<AudiobookEntry> AddMultiFileAudiobookAsync(string folderPath, string title,
        CancellationToken cancellationToken = default)
    {
        string id = GenerateId(folderPath);
        
        var audioFiles = Directory.GetFiles(folderPath)
            .Where(f => IsAudioFile(f))
            .OrderBy(f => f)
            .ToList();
        
        var entry = new AudiobookEntry
        {
            Id = id,
            FilePath = folderPath,
            Title = title,
            IsMultiFile = true,
            AddedDate = DateTimeOffset.UtcNow
        };
        
        // Each file becomes a chapter
        TimeSpan cumulativeDuration = TimeSpan.Zero;
        
        for (int i = 0; i < audioFiles.Count; i++)
        {
            var chapter = new ChapterMarker
            {
                StartTime = cumulativeDuration,
                Title = Path.GetFileNameWithoutExtension(audioFiles[i])
            };
            
            entry.Chapters.Add(chapter);
            entry.FileChapters.Add(new AudiobookFileChapter
            {
                FilePath = audioFiles[i],
                ChapterIndex = i,
                StartOffset = cumulativeDuration
            });
            
            // Would need to probe file for actual duration
            // For now, use placeholder
            cumulativeDuration += TimeSpan.FromMinutes(30);
        }
        
        entry.TotalDuration = cumulativeDuration;
        
        audiobooks[id] = entry;
        await SaveLibraryAsync(cancellationToken);
        
        return entry;
    }
    
    /// <summary>
    /// Get resume position for an audiobook.
    /// </summary>
    public AudiobookPosition GetResumePosition(string audiobookId)
    {
        if (audiobooks.TryGetValue(audiobookId, out var entry))
        {
            return new AudiobookPosition
            {
                Position = entry.ResumePosition,
                ChapterIndex = entry.ResumeChapterIndex,
                FileIndex = entry.ResumeFileIndex,
                LastPlayed = entry.LastPlayed
            };
        }
        
        return new AudiobookPosition();
    }
    
    /// <summary>
    /// Save playback position for an audiobook.
    /// </summary>
    public async Task SavePositionAsync(string audiobookId, TimeSpan position, 
        int chapterIndex = 0, int fileIndex = 0,
        CancellationToken cancellationToken = default)
    {
        if (audiobooks.TryGetValue(audiobookId, out var entry))
        {
            entry.ResumePosition = position;
            entry.ResumeChapterIndex = chapterIndex;
            entry.ResumeFileIndex = fileIndex;
            entry.LastPlayed = DateTimeOffset.UtcNow;
            
            await SaveLibraryAsync(cancellationToken);
        }
    }
    
    /// <summary>
    /// Get chapters for an audiobook.
    /// </summary>
    public IReadOnlyList<ChapterMarker> GetChapters(string audiobookId)
    {
        return audiobooks.TryGetValue(audiobookId, out var entry)
            ? entry.Chapters
            : [];
    }
    
    /// <summary>
    /// Navigate to a specific chapter.
    /// </summary>
    public ChapterNavigationResult NavigateToChapter(string audiobookId, int chapterIndex)
    {
        if (!audiobooks.TryGetValue(audiobookId, out var entry))
        {
            return new ChapterNavigationResult { Success = false };
        }
        
        if (chapterIndex < 0 || chapterIndex >= entry.Chapters.Count)
        {
            return new ChapterNavigationResult { Success = false };
        }
        
        var chapter = entry.Chapters[chapterIndex];
        
        string? filePath = entry.FilePath;
        TimeSpan seekPosition = chapter.StartTime;
        
        // For multi-file audiobooks, find the correct file
        if (entry.IsMultiFile && entry.FileChapters.Count > chapterIndex)
        {
            var fileChapter = entry.FileChapters[chapterIndex];
            filePath = fileChapter.FilePath;
            seekPosition = TimeSpan.Zero; // Seek to start of file
        }
        
        return new ChapterNavigationResult
        {
            Success = true,
            FilePath = filePath,
            SeekPosition = seekPosition,
            ChapterIndex = chapterIndex,
            ChapterTitle = chapter.Title
        };
    }
    
    /// <summary>
    /// Add a bookmark at the current position.
    /// </summary>
    public async Task<Bookmark> AddBookmarkAsync(string audiobookId, TimeSpan position,
        string? note = null, CancellationToken cancellationToken = default)
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid().ToString("N"),
            Position = position,
            Note = note ?? "",
            CreatedDate = DateTimeOffset.UtcNow
        };
        
        if (!bookmarks.ContainsKey(audiobookId))
        {
            bookmarks[audiobookId] = [];
        }
        
        bookmarks[audiobookId].Add(bookmark);
        await SaveBookmarksAsync(cancellationToken);
        
        return bookmark;
    }
    
    /// <summary>
    /// Remove a bookmark.
    /// </summary>
    public async Task RemoveBookmarkAsync(string audiobookId, string bookmarkId,
        CancellationToken cancellationToken = default)
    {
        if (bookmarks.TryGetValue(audiobookId, out var list))
        {
            list.RemoveAll(b => b.Id == bookmarkId);
            await SaveBookmarksAsync(cancellationToken);
        }
    }
    
    /// <summary>
    /// Get all bookmarks for an audiobook.
    /// </summary>
    public IReadOnlyList<Bookmark> GetBookmarks(string audiobookId)
    {
        return bookmarks.TryGetValue(audiobookId, out var list)
            ? list.OrderBy(b => b.Position).ToList()
            : [];
    }
    
    /// <summary>
    /// Start the sleep timer.
    /// </summary>
    public void StartSleepTimer(TimeSpan duration)
    {
        StopSleepTimer();
        
        sleepTimerEnd = DateTimeOffset.UtcNow + duration;
        
        sleepTimer = new Timer(_ =>
        {
            var remaining = sleepTimerEnd.Value - DateTimeOffset.UtcNow;
            
            if (remaining <= TimeSpan.Zero)
            {
                StopSleepTimer();
                SleepTimerTriggered?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SleepTimerTick?.Invoke(this, remaining);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
    
    /// <summary>
    /// Start sleep timer to end at chapter end.
    /// </summary>
    public void StartSleepTimerEndOfChapter(TimeSpan currentPosition, TimeSpan chapterEnd)
    {
        TimeSpan remaining = chapterEnd - currentPosition;
        if (remaining > TimeSpan.Zero)
        {
            StartSleepTimer(remaining);
        }
    }
    
    /// <summary>
    /// Stop the sleep timer.
    /// </summary>
    public void StopSleepTimer()
    {
        sleepTimer?.Dispose();
        sleepTimer = null;
        sleepTimerEnd = null;
    }
    
    /// <summary>
    /// Extend the sleep timer.
    /// </summary>
    public void ExtendSleepTimer(TimeSpan additional)
    {
        if (sleepTimerEnd.HasValue)
        {
            sleepTimerEnd = sleepTimerEnd.Value + additional;
        }
    }
    
    /// <summary>
    /// Get remaining sleep timer duration.
    /// </summary>
    public TimeSpan? GetSleepTimerRemaining()
    {
        if (sleepTimerEnd.HasValue)
        {
            var remaining = sleepTimerEnd.Value - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
        return null;
    }
    
    /// <summary>
    /// Get all audiobooks in the library.
    /// </summary>
    public IReadOnlyList<AudiobookEntry> GetAudiobooks()
    {
        return audiobooks.Values
            .OrderByDescending(a => a.LastPlayed ?? a.AddedDate)
            .ToList();
    }
    
    /// <summary>
    /// Load library from disk.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string libraryFile = Path.Combine(libraryPath, "audiobooks.json");
        if (File.Exists(libraryFile))
        {
            string json = await File.ReadAllTextAsync(libraryFile, cancellationToken);
            var entries = JsonSerializer.Deserialize(json, AudiobookJsonContext.Default.ListAudiobookEntry);
            
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    audiobooks[entry.Id] = entry;
                }
            }
        }
        
        string bookmarksFile = Path.Combine(libraryPath, "audiobook-bookmarks.json");
        if (File.Exists(bookmarksFile))
        {
            string json = await File.ReadAllTextAsync(bookmarksFile, cancellationToken);
            var data = JsonSerializer.Deserialize(json, AudiobookJsonContext.Default.DictionaryStringListBookmark);
            
            if (data != null)
            {
                foreach (var (id, marks) in data)
                {
                    bookmarks[id] = marks;
                }
            }
        }
    }
    
    private async Task SaveLibraryAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(audiobooks.Values.ToList(),
            AudiobookJsonContext.Default.ListAudiobookEntry);
        
        await File.WriteAllTextAsync(
            Path.Combine(libraryPath, "audiobooks.json"), json, cancellationToken);
    }
    
    private async Task SaveBookmarksAsync(CancellationToken cancellationToken)
    {
        var data = bookmarks.ToDictionary(k => k.Key, v => v.Value);
        string json = JsonSerializer.Serialize(data,
            AudiobookJsonContext.Default.DictionaryStringListBookmark);
        
        await File.WriteAllTextAsync(
            Path.Combine(libraryPath, "audiobook-bookmarks.json"), json, cancellationToken);
    }
    
    private async Task ExtractChaptersAsync(AudiobookEntry entry, 
        CancellationToken cancellationToken)
    {
        // Extract chapters from M4B (MP4 chapter atoms) or MP3 (ID3 chapters)
        // This would use the demuxer infrastructure
        
        string extension = Path.GetExtension(entry.FilePath).ToLowerInvariant();
        
        if (extension == ".m4b" || extension == ".m4a" || extension == ".mp4")
        {
            await ExtractMp4ChaptersAsync(entry, cancellationToken);
        }
        else if (extension == ".mp3")
        {
            await ExtractMp3ChaptersAsync(entry, cancellationToken);
        }
    }
    
    private async Task ExtractMp4ChaptersAsync(AudiobookEntry entry, 
        CancellationToken cancellationToken)
    {
        // Parse MP4 chpl or chap atoms
        // This is a simplified version - real implementation would use the MP4 demuxer
        
        try
        {
            await using var stream = File.OpenRead(entry.FilePath);
            using var reader = new BinaryReader(stream);
            
            // Find moov atom
            while (stream.Position < stream.Length - 8)
            {
                uint size = ReadUInt32BE(reader);
                string type = new string(reader.ReadChars(4));
                
                if (size == 0) break;
                if (size == 1) // 64-bit size
                {
                    size = (uint)ReadUInt64BE(reader);
                }
                
                if (type == "moov")
                {
                    // Search for udta/chpl
                    long moovEnd = stream.Position + size - 8;
                    await SearchForChaptersInMoovAsync(entry, reader, moovEnd, cancellationToken);
                    break;
                }
                else
                {
                    stream.Position += size - 8;
                }
            }
        }
        catch
        {
            // Ignore chapter extraction errors
        }
    }
    
    private async Task SearchForChaptersInMoovAsync(AudiobookEntry entry, BinaryReader reader,
        long moovEnd, CancellationToken cancellationToken)
    {
        var stream = reader.BaseStream;
        
        while (stream.Position < moovEnd - 8)
        {
            uint size = ReadUInt32BE(reader);
            string type = new string(reader.ReadChars(4));
            
            if (size == 0) break;
            
            if (type == "udta")
            {
                long udtaEnd = stream.Position + size - 8;
                
                while (stream.Position < udtaEnd - 8)
                {
                    uint childSize = ReadUInt32BE(reader);
                    string childType = new string(reader.ReadChars(4));
                    
                    if (childType == "chpl")
                    {
                        // Parse chapter list
                        reader.ReadBytes(4); // version/flags
                        reader.ReadBytes(4); // reserved
                        byte chapterCount = reader.ReadByte();
                        
                        for (int i = 0; i < chapterCount; i++)
                        {
                            long timestamp = (long)ReadUInt64BE(reader); // 100ns units
                            byte titleLength = reader.ReadByte();
                            string title = new string(reader.ReadChars(titleLength));
                            
                            entry.Chapters.Add(new ChapterMarker
                            {
                                StartTime = TimeSpan.FromTicks(timestamp / 100),
                                Title = title
                            });
                        }
                        
                        return;
                    }
                    else
                    {
                        stream.Position += childSize - 8;
                    }
                }
            }
            else
            {
                stream.Position += size - 8;
            }
        }
        
        await Task.CompletedTask;
    }
    
    private async Task ExtractMp3ChaptersAsync(AudiobookEntry entry, 
        CancellationToken cancellationToken)
    {
        // Parse ID3v2 CHAP and CTOC frames
        // Simplified - would use ID3 parser from Metadata
        
        try
        {
            await using var stream = File.OpenRead(entry.FilePath);
            byte[] header = new byte[10];
            await stream.ReadAsync(header.AsMemory(0, 10), cancellationToken);
            
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
                return;
            
            int tagSize = (header[6] << 21) | (header[7] << 14) | 
                (header[8] << 7) | header[9];
            
            byte[] tagData = new byte[tagSize];
            await stream.ReadAsync(tagData.AsMemory(), cancellationToken);
            
            // Parse frames looking for CHAP
            int pos = 0;
            while (pos < tagData.Length - 10)
            {
                string frameId = System.Text.Encoding.ASCII.GetString(tagData, pos, 4);
                int frameSize = (tagData[pos + 4] << 24) | (tagData[pos + 5] << 16) |
                    (tagData[pos + 6] << 8) | tagData[pos + 7];
                
                if (frameSize <= 0) break;
                
                if (frameId == "CHAP")
                {
                    // Parse CHAP frame
                    int dataStart = pos + 10;
                    
                    // Skip element ID (null-terminated)
                    int idEnd = Array.IndexOf(tagData, (byte)0, dataStart);
                    if (idEnd > dataStart)
                    {
                        int timeStart = idEnd + 1;
                        if (timeStart + 16 <= tagData.Length)
                        {
                            uint startMs = (uint)((tagData[timeStart] << 24) | 
                                (tagData[timeStart + 1] << 16) |
                                (tagData[timeStart + 2] << 8) | 
                                tagData[timeStart + 3]);
                            
                            // Look for embedded TIT2 frame for chapter title
                            string title = $"Chapter {entry.Chapters.Count + 1}";
                            
                            entry.Chapters.Add(new ChapterMarker
                            {
                                StartTime = TimeSpan.FromMilliseconds(startMs),
                                Title = title
                            });
                        }
                    }
                }
                
                pos += 10 + frameSize;
            }
        }
        catch
        {
            // Ignore chapter extraction errors
        }
    }
    
    private static uint ReadUInt32BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }
    
    private static ulong ReadUInt64BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(8);
        return ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) |
            ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32) |
            ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) |
            ((ulong)bytes[6] << 8) | bytes[7];
    }
    
    private static bool IsAudioFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or 
            ".opus" or ".flac" or ".wav" or ".wma";
    }
    
    private static string GenerateId(string path)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
    
    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        
        StopSleepTimer();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Audiobook library entry.
/// </summary>
public sealed class AudiobookEntry
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? Narrator { get; set; }
    public string? Description { get; set; }
    public string? CoverPath { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public bool IsMultiFile { get; set; }
    public DateTimeOffset AddedDate { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
    
    // Resume state
    public TimeSpan ResumePosition { get; set; }
    public int ResumeChapterIndex { get; set; }
    public int ResumeFileIndex { get; set; }
    
    // Chapters
    public List<ChapterMarker> Chapters { get; set; } = [];
    public List<AudiobookFileChapter> FileChapters { get; set; } = [];
}

/// <summary>
/// File chapter mapping for multi-file audiobooks.
/// </summary>
public sealed class AudiobookFileChapter
{
    public required string FilePath { get; init; }
    public int ChapterIndex { get; set; }
    public TimeSpan StartOffset { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Audiobook playback position.
/// </summary>
public readonly struct AudiobookPosition
{
    public TimeSpan Position { get; init; }
    public int ChapterIndex { get; init; }
    public int FileIndex { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
}

/// <summary>
/// Chapter navigation result.
/// </summary>
public readonly struct ChapterNavigationResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public TimeSpan SeekPosition { get; init; }
    public int ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
}

/// <summary>
/// Bookmark in an audiobook.
/// </summary>
public sealed class Bookmark
{
    public required string Id { get; init; }
    public TimeSpan Position { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset CreatedDate { get; set; }
}

/// <summary>
/// Playback speed presets for audiobooks and podcasts.
/// </summary>
public static class PlaybackSpeedPresets
{
    public static readonly float[] StandardSpeeds = 
    [
        0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.5f, 3.0f
    ];
    
    /// <summary>
    /// Get the next faster speed preset.
    /// </summary>
    public static float GetNextFaster(float current)
    {
        foreach (float speed in StandardSpeeds)
        {
            if (speed > current + 0.01f)
                return speed;
        }
        return StandardSpeeds[^1];
    }
    
    /// <summary>
    /// Get the next slower speed preset.
    /// </summary>
    public static float GetNextSlower(float current)
    {
        for (int i = StandardSpeeds.Length - 1; i >= 0; i--)
        {
            if (StandardSpeeds[i] < current - 0.01f)
                return StandardSpeeds[i];
        }
        return StandardSpeeds[0];
    }
    
    /// <summary>
    /// Format speed for display.
    /// </summary>
    public static string FormatSpeed(float speed)
    {
        return speed == 1.0f ? "Normal" : $"{speed:0.##}x";
    }
}

// MediaKernel - Chapter Support
// Data structures for media chapter navigation

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Represents a single chapter marker in a media file.
/// </summary>
public sealed class Chapter
{
    /// <summary>Unique ID for this chapter (from container, or auto-generated).</summary>
    public ulong Id { get; init; }
    
    /// <summary>Chapter start time.</summary>
    public TimeSpan StartTime { get; init; }
    
    /// <summary>Chapter end time (may be zero if not specified).</summary>
    public TimeSpan EndTime { get; init; }
    
    /// <summary>Chapter title in the primary language.</summary>
    public string Title { get; init; } = string.Empty;
    
    /// <summary>Language code (ISO 639-2 or BCP-47).</summary>
    public string Language { get; init; } = "und";
    
    /// <summary>Whether this chapter is hidden from the user.</summary>
    public bool IsHidden { get; init; }
    
    /// <summary>Whether this chapter is enabled for playback.</summary>
    public bool IsEnabled { get; init; } = true;
    
    /// <summary>Nested sub-chapters within this chapter.</summary>
    public List<Chapter> SubChapters { get; init; } = new();
    
    /// <summary>Duration of this chapter.</summary>
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
    
    /// <summary>
    /// Gets the display title (title or "Chapter N" if no title).
    /// </summary>
    public string DisplayTitle(int index) => 
        string.IsNullOrWhiteSpace(Title) ? $"Chapter {index + 1}" : Title;
    
    public override string ToString() => 
        $"[{StartTime:hh\\:mm\\:ss\\.fff}] {Title}";
}

/// <summary>
/// Represents an edition (set of chapters) in a media file.
/// Matroska supports multiple editions for alternate chapter structures.
/// </summary>
public sealed class ChapterEdition
{
    /// <summary>Edition unique ID.</summary>
    public ulong Id { get; init; }
    
    /// <summary>Whether this edition is hidden from the user.</summary>
    public bool IsHidden { get; init; }
    
    /// <summary>Whether this is the default edition to display.</summary>
    public bool IsDefault { get; init; }
    
    /// <summary>Whether chapters are ordered (playback order matters).</summary>
    public bool IsOrdered { get; init; }
    
    /// <summary>Chapters in this edition.</summary>
    public List<Chapter> Chapters { get; init; } = new();
    
    /// <summary>Total count of chapters (including nested).</summary>
    public int TotalChapterCount => CountChaptersRecursive(Chapters);
    
    private static int CountChaptersRecursive(List<Chapter> chapters)
    {
        int count = chapters.Count;
        foreach (var ch in chapters)
            count += CountChaptersRecursive(ch.SubChapters);
        return count;
    }
}

/// <summary>
/// Chapter collection with navigation support.
/// </summary>
public sealed class ChapterList
{
    private readonly List<ChapterEdition> editions = new();
    private int currentEditionIndex;
    private int currentChapterIndex;
    
    /// <summary>All editions in the media file.</summary>
    public IReadOnlyList<ChapterEdition> Editions => editions;
    
    /// <summary>Whether there are any chapters.</summary>
    public bool HasChapters => editions.Count > 0 && editions[0].Chapters.Count > 0;
    
    /// <summary>Chapters in the current edition.</summary>
    public IReadOnlyList<Chapter> CurrentEditionChapters => 
        editions.Count > 0 ? editions[currentEditionIndex].Chapters : Array.Empty<Chapter>();
    
    /// <summary>Current edition index.</summary>
    public int CurrentEditionIndex
    {
        get => currentEditionIndex;
        set => currentEditionIndex = Math.Clamp(value, 0, Math.Max(0, editions.Count - 1));
    }
    
    /// <summary>Current chapter index within the current edition.</summary>
    public int CurrentChapterIndex
    {
        get => currentChapterIndex;
        set
        {
            var chapters = CurrentEditionChapters;
            currentChapterIndex = Math.Clamp(value, 0, Math.Max(0, chapters.Count - 1));
        }
    }
    
    /// <summary>Gets the current chapter.</summary>
    public Chapter? CurrentChapter
    {
        get
        {
            var chapters = CurrentEditionChapters;
            return currentChapterIndex >= 0 && currentChapterIndex < chapters.Count 
                ? chapters[currentChapterIndex] 
                : null;
        }
    }
    
    /// <summary>Add an edition with its chapters.</summary>
    public void AddEdition(ChapterEdition edition)
    {
        editions.Add(edition);
        
        // If this is default edition, select it
        if (edition.IsDefault && editions.Count > 1)
            currentEditionIndex = editions.Count - 1;
    }
    
    /// <summary>Clear all editions and chapters.</summary>
    public void Clear()
    {
        editions.Clear();
        currentEditionIndex = 0;
        currentChapterIndex = 0;
    }
    
    /// <summary>
    /// Find the chapter containing the given timestamp.
    /// </summary>
    /// <param name="position">Current playback position.</param>
    /// <returns>Chapter index, or -1 if not found.</returns>
    public int FindChapterAtTime(TimeSpan position)
    {
        var chapters = CurrentEditionChapters;
        
        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (position >= chapters[i].StartTime)
            {
                // Check end time if specified
                if (chapters[i].EndTime > TimeSpan.Zero && position > chapters[i].EndTime)
                    continue;
                    
                return i;
            }
        }
        
        return chapters.Count > 0 ? 0 : -1;
    }
    
    /// <summary>
    /// Navigate to the next chapter.
    /// </summary>
    /// <returns>The next chapter, or null if at end.</returns>
    public Chapter? NextChapter()
    {
        var chapters = CurrentEditionChapters;
        if (currentChapterIndex < chapters.Count - 1)
        {
            currentChapterIndex++;
            return CurrentChapter;
        }
        return null;
    }
    
    /// <summary>
    /// Navigate to the previous chapter.
    /// </summary>
    /// <returns>The previous chapter, or null if at beginning.</returns>
    public Chapter? PreviousChapter()
    {
        if (currentChapterIndex > 0)
        {
            currentChapterIndex--;
            return CurrentChapter;
        }
        return null;
    }
    
    /// <summary>
    /// Update current chapter based on playback position.
    /// </summary>
    /// <param name="position">Current playback position.</param>
    /// <returns>True if chapter changed.</returns>
    public bool UpdateCurrentChapter(TimeSpan position)
    {
        int newIndex = FindChapterAtTime(position);
        if (newIndex >= 0 && newIndex != currentChapterIndex)
        {
            currentChapterIndex = newIndex;
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Get all visible (non-hidden) chapters flattened.
    /// </summary>
    public IEnumerable<Chapter> GetVisibleChapters()
    {
        foreach (var chapter in CurrentEditionChapters)
        {
            if (!chapter.IsHidden && chapter.IsEnabled)
            {
                yield return chapter;
                
                foreach (var sub in GetVisibleChaptersRecursive(chapter.SubChapters))
                    yield return sub;
            }
        }
    }
    
    private static IEnumerable<Chapter> GetVisibleChaptersRecursive(List<Chapter> chapters)
    {
        foreach (var chapter in chapters)
        {
            if (!chapter.IsHidden && chapter.IsEnabled)
            {
                yield return chapter;
                
                foreach (var sub in GetVisibleChaptersRecursive(chapter.SubChapters))
                    yield return sub;
            }
        }
    }
}

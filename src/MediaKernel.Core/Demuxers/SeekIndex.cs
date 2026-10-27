// SeekIndex - High-performance seek table for random access
// Provides binary search for keyframe locations

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// High-performance seek index for random access to media streams.
/// Uses binary search for O(log n) seek point lookup.
/// </summary>
public sealed class SeekIndex
{
    private readonly List<SeekPoint> entries = [];
    private bool isSorted = true;

    /// <summary>
    /// Gets the number of entries in the index.
    /// </summary>
    public int Count => entries.Count;
    
    /// <summary>
    /// Gets whether the index is empty.
    /// </summary>
    public bool IsEmpty => entries.Count == 0;
    
    /// <summary>
    /// Gets all entries as a read-only list.
    /// </summary>
    public IReadOnlyList<SeekPoint> Entries => entries;

    /// <summary>
    /// Adds a seek point to the index.
    /// </summary>
    public void Add(SeekPoint entry)
    {
        entries.Add(entry);
        
        // Check if still sorted
        if (entries.Count > 1)
        {
            var prev = entries[^2];
            if (entry.TimestampTicks < prev.TimestampTicks)
                isSorted = false;
        }
    }
    
    /// <summary>
    /// Adds a seek point using individual values.
    /// </summary>
    public void Add(long timestampTicks, long filePosition, int trackId = 0, long sampleIndex = 0)
    {
        Add(new SeekPoint(timestampTicks, filePosition, trackId, sampleIndex));
    }

    /// <summary>
    /// Ensures the index is sorted by timestamp.
    /// </summary>
    public void EnsureSorted()
    {
        if (!isSorted)
        {
            entries.Sort((a, b) => a.TimestampTicks.CompareTo(b.TimestampTicks));
            isSorted = true;
        }
    }

    /// <summary>
    /// Finds the seek point at or before the specified timestamp.
    /// Returns the nearest keyframe position for seeking.
    /// </summary>
    /// <param name="timestampTicks">Target timestamp in ticks.</param>
    /// <returns>The seek point, or null if the index is empty.</returns>
    public SeekPoint? FindSeekPoint(long timestampTicks)
    {
        if (entries.Count == 0)
            return null;
        
        EnsureSorted();
        
        // Binary search for the entry at or before the target
        int low = 0;
        int high = entries.Count - 1;
        int result = 0;
        
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            
            if (entries[mid].TimestampTicks <= timestampTicks)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        
        return entries[result];
    }
    
    /// <summary>
    /// Finds the seek point at or before the specified timestamp in milliseconds.
    /// </summary>
    public SeekPoint? FindSeekPoint(double timestampMs)
    {
        long ticks = (long)(timestampMs * TimeSpan.TicksPerMillisecond);
        return FindSeekPoint(ticks);
    }
    
    /// <summary>
    /// Finds the seek point at or after the specified timestamp.
    /// </summary>
    public SeekPoint? FindNextSeekPoint(long timestampTicks)
    {
        if (entries.Count == 0)
            return null;
        
        EnsureSorted();
        
        // Binary search for the first entry at or after the target
        int low = 0;
        int high = entries.Count - 1;
        int result = -1;
        
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            
            if (entries[mid].TimestampTicks >= timestampTicks)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        
        return result >= 0 ? entries[result] : null;
    }
    
    /// <summary>
    /// Gets the seek point at the specified index.
    /// </summary>
    public SeekPoint this[int index] => entries[index];
    
    /// <summary>
    /// Clears all entries from the index.
    /// </summary>
    public void Clear()
    {
        entries.Clear();
        isSorted = true;
    }
    
    /// <summary>
    /// Gets the total duration covered by the index (last entry timestamp).
    /// </summary>
    public long DurationTicks
    {
        get
        {
            if (entries.Count == 0)
                return 0;
            
            EnsureSorted();
            return entries[^1].TimestampTicks;
        }
    }
    
    /// <summary>
    /// Gets the average interval between seek points in ticks.
    /// Useful for estimating seek accuracy.
    /// </summary>
    public long AverageIntervalTicks
    {
        get
        {
            if (entries.Count < 2)
                return 0;
            
            EnsureSorted();
            return (entries[^1].TimestampTicks - entries[0].TimestampTicks) / (entries.Count - 1);
        }
    }
    
    /// <summary>
    /// Filters entries by track ID.
    /// </summary>
    public IEnumerable<SeekPoint> ForTrack(int trackId)
    {
        return entries.Where(e => e.TrackId == trackId || e.TrackId == 0);
    }
    
    /// <summary>
    /// Creates a seek index from an existing collection.
    /// </summary>
    public static SeekIndex FromEntries(IEnumerable<SeekPoint> seekPoints)
    {
        var index = new SeekIndex();
        foreach (var entry in seekPoints)
            index.entries.Add(entry);
        
        index.isSorted = false;
        index.EnsureSorted();
        return index;
    }
    
    /// <summary>
    /// Creates a seek index from legacy SeekEntry collection.
    /// </summary>
    public static SeekIndex FromLegacyEntries(IEnumerable<SeekEntry> legacyEntries)
    {
        var index = new SeekIndex();
        foreach (var entry in legacyEntries)
            index.entries.Add(new SeekPoint(entry.TimestampTicks, entry.FilePosition));
        
        index.isSorted = false;
        index.EnsureSorted();
        return index;
    }
}

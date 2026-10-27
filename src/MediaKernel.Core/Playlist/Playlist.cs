// Playlist data model for MediaKernel
// Reference: VLC include/vlc_playlist.h

using System.Collections.ObjectModel;

namespace MediaKernel.Core.Playlist;

/// <summary>
/// Represents a single item in a playlist.
/// </summary>
public sealed class PlaylistItem
{
    /// <summary>Unique identifier for this item.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>File path or URL of the media.</summary>
    public string Uri { get; set; }

    /// <summary>Display title (from metadata or filename).</summary>
    public string Title { get; set; }

    /// <summary>Artist name (if available).</summary>
    public string Artist { get; set; }

    /// <summary>Album name (if available).</summary>
    public string Album { get; set; }

    /// <summary>Duration in milliseconds (0 if unknown).</summary>
    public long DurationMilliseconds { get; set; }

    /// <summary>Track number within album (0 if unknown).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Whether metadata has been loaded.</summary>
    public bool IsMetadataLoaded { get; set; }

    /// <summary>Gets the duration as TimeSpan.</summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMilliseconds);

    public PlaylistItem(string uri)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Title = ExtractTitleFromUri(uri);
        Artist = string.Empty;
        Album = string.Empty;
    }

    public PlaylistItem(string uri, string title, long durationMs = 0)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Title = string.IsNullOrWhiteSpace(title) ? ExtractTitleFromUri(uri) : title;
        Artist = string.Empty;
        Album = string.Empty;
        DurationMilliseconds = durationMs;
    }

    private static string ExtractTitleFromUri(string uri)
    {
        // Try to get filename without extension
        try
        {
            if (System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                var segments = parsed.Segments;
                if (segments.Length > 0)
                {
                    var lastSegment = segments[^1].TrimEnd('/');
                    if (!string.IsNullOrEmpty(lastSegment))
                    {
                        // Remove extension
                        int dotIndex = lastSegment.LastIndexOf('.');
                        if (dotIndex > 0)
                            return System.Uri.UnescapeDataString(lastSegment[..dotIndex]);
                        return System.Uri.UnescapeDataString(lastSegment);
                    }
                }
            }
            // Fallback: just get file name
            return Path.GetFileNameWithoutExtension(uri);
        }
        catch
        {
            return uri;
        }
    }

    public override string ToString() => Title;
}

/// <summary>
/// Repeat mode for playlist playback.
/// </summary>
public enum RepeatMode
{
    /// <summary>No repeat - stop after last item.</summary>
    None,

    /// <summary>Repeat the current item.</summary>
    One,

    /// <summary>Repeat the entire playlist.</summary>
    All
}

/// <summary>
/// Sort key for playlist items.
/// </summary>
public enum PlaylistSortKey
{
    Title,
    Artist,
    Album,
    Duration,
    TrackNumber,
    Uri
}

/// <summary>
/// Sort order for playlist items.
/// </summary>
public enum SortOrder
{
    Ascending,
    Descending
}

/// <summary>
/// Manages a collection of media items for playback.
/// </summary>
public sealed class Playlist
{
    private readonly List<PlaylistItem> items = [];
    private readonly Random shuffleRandom = new();
    private readonly object syncLock = new();

    private int currentIndex = -1;
    private int[]? shuffleOrder;
    private int shufflePosition;

    /// <summary>Event raised when items are added to the playlist.</summary>
    public event EventHandler<PlaylistChangedEventArgs>? ItemsAdded;

    /// <summary>Event raised when items are removed from the playlist.</summary>
    public event EventHandler<PlaylistChangedEventArgs>? ItemsRemoved;

    /// <summary>Event raised when items are moved within the playlist.</summary>
    public event EventHandler<PlaylistMovedEventArgs>? ItemsMoved;

    /// <summary>Event raised when the current item changes.</summary>
    public event EventHandler<CurrentItemChangedEventArgs>? CurrentItemChanged;

    /// <summary>Event raised when the playlist is cleared or reset.</summary>
    public event EventHandler? PlaylistReset;

    /// <summary>Gets whether shuffle mode is enabled.</summary>
    public bool IsShuffled { get; private set; }

    /// <summary>Gets the current repeat mode.</summary>
    public RepeatMode RepeatMode { get; set; }
    
    /// <summary>Gets or sets the playlist title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets the number of items in the playlist.</summary>
    public int Count
    {
        get { lock (syncLock) return items.Count; }
    }

    /// <summary>Gets the current item index (-1 if none).</summary>
    public int CurrentIndex
    {
        get { lock (syncLock) return currentIndex; }
    }

    /// <summary>Gets the current item (null if none).</summary>
    public PlaylistItem? CurrentItem
    {
        get
        {
            lock (syncLock)
            {
                if (currentIndex >= 0 && currentIndex < items.Count)
                    return items[currentIndex];
                return null;
            }
        }
    }

    /// <summary>Gets whether there is a previous item available.</summary>
    public bool HasPrevious
    {
        get
        {
            lock (syncLock)
            {
                if (items.Count == 0) return false;
                if (RepeatMode == RepeatMode.One) return true;
                if (RepeatMode == RepeatMode.All) return true;

                if (IsShuffled)
                    return shufflePosition > 0;
                return currentIndex > 0;
            }
        }
    }

    /// <summary>Gets whether there is a next item available.</summary>
    public bool HasNext
    {
        get
        {
            lock (syncLock)
            {
                if (items.Count == 0) return false;
                if (RepeatMode == RepeatMode.One) return true;
                if (RepeatMode == RepeatMode.All) return true;

                if (IsShuffled)
                    return shufflePosition < items.Count - 1;
                return currentIndex < items.Count - 1;
            }
        }
    }

    /// <summary>Gets all items as a read-only collection.</summary>
    public ReadOnlyCollection<PlaylistItem> Items
    {
        get { lock (syncLock) return items.AsReadOnly(); }
    }

    /// <summary>Gets an item by index.</summary>
    public PlaylistItem this[int index]
    {
        get
        {
            lock (syncLock)
            {
                if (index < 0 || index >= items.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return items[index];
            }
        }
    }

    /// <summary>Adds an item to the end of the playlist.</summary>
    public void Add(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        int addedIndex;
        lock (syncLock)
        {
            addedIndex = items.Count;
            items.Add(item);
            InvalidateShuffleOrder();
        }

        ItemsAdded?.Invoke(this, new PlaylistChangedEventArgs(addedIndex, 1));
    }

    /// <summary>Adds multiple items to the end of the playlist.</summary>
    public void AddRange(IEnumerable<PlaylistItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        var itemList = newItems.ToList();
        if (itemList.Count == 0) return;

        int addedIndex;
        lock (syncLock)
        {
            addedIndex = items.Count;
            items.AddRange(itemList);
            InvalidateShuffleOrder();
        }

        ItemsAdded?.Invoke(this, new PlaylistChangedEventArgs(addedIndex, itemList.Count));
    }

    /// <summary>Inserts an item at the specified index.</summary>
    public void Insert(int index, PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (syncLock)
        {
            if (index < 0 || index > items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            items.Insert(index, item);

            // Adjust current index if needed
            if (currentIndex >= index)
                currentIndex++;

            InvalidateShuffleOrder();
        }

        ItemsAdded?.Invoke(this, new PlaylistChangedEventArgs(index, 1));
    }

    /// <summary>Removes an item at the specified index.</summary>
    public void RemoveAt(int index)
    {
        PlaylistItem? newCurrent = null;
        int newIndex;

        lock (syncLock)
        {
            if (index < 0 || index >= items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            items.RemoveAt(index);

            // Adjust current index
            if (currentIndex == index)
            {
                // Current item was removed - stay at same index if possible
                if (currentIndex >= items.Count)
                    currentIndex = items.Count - 1;
                newCurrent = currentIndex >= 0 ? items[currentIndex] : null;
            }
            else if (currentIndex > index)
            {
                currentIndex--;
            }

            newIndex = currentIndex;
            InvalidateShuffleOrder();
        }

        ItemsRemoved?.Invoke(this, new PlaylistChangedEventArgs(index, 1));

        if (newCurrent != null || (index <= newIndex))
        {
            CurrentItemChanged?.Invoke(this, new CurrentItemChangedEventArgs(newIndex, newCurrent));
        }
    }

    /// <summary>Removes an item from the playlist.</summary>
    public bool Remove(PlaylistItem item)
    {
        int index;
        lock (syncLock)
        {
            index = items.IndexOf(item);
            if (index < 0) return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>Moves an item from one position to another.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        lock (syncLock)
        {
            if (fromIndex < 0 || fromIndex >= items.Count)
                throw new ArgumentOutOfRangeException(nameof(fromIndex));
            if (toIndex < 0 || toIndex >= items.Count)
                throw new ArgumentOutOfRangeException(nameof(toIndex));
            if (fromIndex == toIndex)
                return;

            var item = items[fromIndex];
            items.RemoveAt(fromIndex);
            items.Insert(toIndex, item);

            // Adjust current index
            if (currentIndex == fromIndex)
            {
                currentIndex = toIndex;
            }
            else if (fromIndex < currentIndex && toIndex >= currentIndex)
            {
                currentIndex--;
            }
            else if (fromIndex > currentIndex && toIndex <= currentIndex)
            {
                currentIndex++;
            }

            InvalidateShuffleOrder();
        }

        ItemsMoved?.Invoke(this, new PlaylistMovedEventArgs(fromIndex, toIndex, 1));
    }

    /// <summary>Clears all items from the playlist.</summary>
    public void Clear()
    {
        lock (syncLock)
        {
            items.Clear();
            currentIndex = -1;
            shuffleOrder = null;
            shufflePosition = 0;
        }

        PlaylistReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the current item by index.</summary>
    public void SetCurrentIndex(int index)
    {
        PlaylistItem? newCurrent;

        lock (syncLock)
        {
            if (index < -1 || index >= items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (currentIndex == index) return;

            currentIndex = index;
            newCurrent = index >= 0 ? items[index] : null;

            // Update shuffle position if shuffled
            if (IsShuffled && shuffleOrder != null && index >= 0)
            {
                shufflePosition = Array.IndexOf(shuffleOrder, index);
                if (shufflePosition < 0) shufflePosition = 0;
            }
        }

        CurrentItemChanged?.Invoke(this, new CurrentItemChangedEventArgs(index, newCurrent));
    }

    /// <summary>Moves to the next item in the playlist.</summary>
    /// <returns>True if moved to next item, false if at end.</returns>
    public bool Next()
    {
        int newIndex;
        PlaylistItem? newCurrent;

        lock (syncLock)
        {
            if (items.Count == 0) return false;

            if (RepeatMode == RepeatMode.One)
            {
                // Stay on current item
                newIndex = currentIndex;
            }
            else if (IsShuffled && shuffleOrder != null)
            {
                shufflePosition++;
                if (shufflePosition >= items.Count)
                {
                    if (RepeatMode == RepeatMode.All)
                    {
                        shufflePosition = 0;
                        RegenerateShuffleOrder();
                    }
                    else
                    {
                        shufflePosition = items.Count - 1;
                        return false;
                    }
                }
                newIndex = shuffleOrder[shufflePosition];
            }
            else
            {
                newIndex = currentIndex + 1;
                if (newIndex >= items.Count)
                {
                    if (RepeatMode == RepeatMode.All)
                        newIndex = 0;
                    else
                        return false;
                }
            }

            currentIndex = newIndex;
            newCurrent = items[newIndex];
        }

        CurrentItemChanged?.Invoke(this, new CurrentItemChangedEventArgs(newIndex, newCurrent));
        return true;
    }

    /// <summary>Moves to the previous item in the playlist.</summary>
    /// <returns>True if moved to previous item, false if at beginning.</returns>
    public bool Previous()
    {
        int newIndex;
        PlaylistItem? newCurrent;

        lock (syncLock)
        {
            if (items.Count == 0) return false;

            if (RepeatMode == RepeatMode.One)
            {
                // Stay on current item
                newIndex = currentIndex;
            }
            else if (IsShuffled && shuffleOrder != null)
            {
                shufflePosition--;
                if (shufflePosition < 0)
                {
                    if (RepeatMode == RepeatMode.All)
                        shufflePosition = items.Count - 1;
                    else
                    {
                        shufflePosition = 0;
                        return false;
                    }
                }
                newIndex = shuffleOrder[shufflePosition];
            }
            else
            {
                newIndex = currentIndex - 1;
                if (newIndex < 0)
                {
                    if (RepeatMode == RepeatMode.All)
                        newIndex = items.Count - 1;
                    else
                        return false;
                }
            }

            currentIndex = newIndex;
            newCurrent = items[newIndex];
        }

        CurrentItemChanged?.Invoke(this, new CurrentItemChangedEventArgs(newIndex, newCurrent));
        return true;
    }

    /// <summary>Enables or disables shuffle mode.</summary>
    public void SetShuffle(bool shuffle)
    {
        lock (syncLock)
        {
            if (IsShuffled == shuffle) return;

            IsShuffled = shuffle;

            if (shuffle)
            {
                GenerateShuffleOrder();
            }
            else
            {
                shuffleOrder = null;
                shufflePosition = 0;
            }
        }

        PlaylistReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles shuffle mode.</summary>
    public void ToggleShuffle() => SetShuffle(!IsShuffled);

    /// <summary>Cycles through repeat modes (None -> All -> One -> None).</summary>
    public void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.None
        };
    }

    /// <summary>Sorts the playlist by the specified key.</summary>
    public void Sort(PlaylistSortKey key, SortOrder order = SortOrder.Ascending)
    {
        lock (syncLock)
        {
            var currentItem = CurrentItem;

            items.Sort((a, b) =>
            {
                int result = key switch
                {
                    PlaylistSortKey.Title => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
                    PlaylistSortKey.Artist => string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase),
                    PlaylistSortKey.Album => string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase),
                    PlaylistSortKey.Duration => a.DurationMilliseconds.CompareTo(b.DurationMilliseconds),
                    PlaylistSortKey.TrackNumber => a.TrackNumber.CompareTo(b.TrackNumber),
                    PlaylistSortKey.Uri => string.Compare(a.Uri, b.Uri, StringComparison.OrdinalIgnoreCase),
                    _ => 0
                };

                return order == SortOrder.Descending ? -result : result;
            });

            // Restore current item reference
            if (currentItem != null)
            {
                currentIndex = items.IndexOf(currentItem);
            }

            InvalidateShuffleOrder();
        }

        PlaylistReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gets the index of an item by its ID.</summary>
    public int IndexOf(Guid id)
    {
        lock (syncLock)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Id == id)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>Gets the index of an item.</summary>
    public int IndexOf(PlaylistItem item)
    {
        lock (syncLock)
        {
            return items.IndexOf(item);
        }
    }

    /// <summary>Gets the total duration of all items with known durations.</summary>
    public TimeSpan TotalDuration
    {
        get
        {
            lock (syncLock)
            {
                long totalMs = 0;
                foreach (var item in items)
                {
                    totalMs += item.DurationMilliseconds;
                }
                return TimeSpan.FromMilliseconds(totalMs);
            }
        }
    }

    private void GenerateShuffleOrder()
    {
        shuffleOrder = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
            shuffleOrder[i] = i;

        // Fisher-Yates shuffle
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = shuffleRandom.Next(i + 1);
            (shuffleOrder[i], shuffleOrder[j]) = (shuffleOrder[j], shuffleOrder[i]);
        }

        // Ensure current item is first in shuffle if one is selected
        if (currentIndex >= 0)
        {
            int currentShufflePos = Array.IndexOf(shuffleOrder, currentIndex);
            if (currentShufflePos > 0)
            {
                (shuffleOrder[0], shuffleOrder[currentShufflePos]) = (shuffleOrder[currentShufflePos], shuffleOrder[0]);
            }
            shufflePosition = 0;
        }
    }

    private void RegenerateShuffleOrder()
    {
        if (shuffleOrder == null) return;

        // Reshuffle for next round
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = shuffleRandom.Next(i + 1);
            (shuffleOrder[i], shuffleOrder[j]) = (shuffleOrder[j], shuffleOrder[i]);
        }
    }

    private void InvalidateShuffleOrder()
    {
        if (IsShuffled)
        {
            GenerateShuffleOrder();
        }
    }
}

/// <summary>Event args for playlist item changes.</summary>
public sealed class PlaylistChangedEventArgs : EventArgs
{
    public int Index { get; }
    public int Count { get; }

    public PlaylistChangedEventArgs(int index, int count)
    {
        Index = index;
        Count = count;
    }
}

/// <summary>Event args for playlist item moves.</summary>
public sealed class PlaylistMovedEventArgs : EventArgs
{
    public int FromIndex { get; }
    public int ToIndex { get; }
    public int Count { get; }

    public PlaylistMovedEventArgs(int fromIndex, int toIndex, int count)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
        Count = count;
    }
}

/// <summary>Event args for current item changes.</summary>
public sealed class CurrentItemChangedEventArgs : EventArgs
{
    public int Index { get; }
    public PlaylistItem? Item { get; }

    public CurrentItemChangedEventArgs(int index, PlaylistItem? item)
    {
        Index = index;
        Item = item;
    }
}

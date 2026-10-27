// Playlist file format serialization (M3U, M3U8, PLS, XSPF, ASX, WPL)
// Reference: VLC modules/demux/playlist/

using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace MediaKernel.Core.Playlist;

/// <summary>
/// Supported playlist file formats.
/// </summary>
public enum PlaylistFormat
{
    /// <summary>Standard M3U format (basic, no metadata).</summary>
    M3U,

    /// <summary>Extended M3U format (with EXTINF metadata).</summary>
    M3U8,

    /// <summary>PLS format (used by Winamp, etc).</summary>
    PLS,
    
    /// <summary>XSPF (XML Shareable Playlist Format).</summary>
    XSPF,
    
    /// <summary>ASX (Advanced Stream Redirector - Windows Media).</summary>
    ASX,
    
    /// <summary>WPL (Windows Media Player Playlist).</summary>
    WPL
}

/// <summary>
/// Reads and writes playlist files in various formats.
/// </summary>
public static class PlaylistSerializer
{
    private const string M3UHeader = "#EXTM3U";
    private const string ExtInfPrefix = "#EXTINF:";
    private const string PlsHeader = "[playlist]";
    private static readonly XNamespace XspfNamespace = "http://xspf.org/ns/0/";

    /// <summary>
    /// Loads a playlist from a file.
    /// </summary>
    /// <param name="filePath">Path to the playlist file.</param>
    /// <returns>A new Playlist populated with items from the file.</returns>
    public static Playlist Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Playlist file not found.", filePath);

        var content = File.ReadAllText(filePath);
        var format = DetectFormat(content, filePath);
        var baseDir = Path.GetDirectoryName(filePath) ?? string.Empty;

        return format switch
        {
            PlaylistFormat.M3U or PlaylistFormat.M3U8 => ParseM3U(content, baseDir),
            PlaylistFormat.PLS => ParsePLS(content, baseDir),
            PlaylistFormat.XSPF => ParseXSPF(content, baseDir),
            PlaylistFormat.ASX => ParseASX(content, baseDir),
            PlaylistFormat.WPL => ParseWPL(content, baseDir),
            _ => throw new NotSupportedException($"Unsupported playlist format: {format}")
        };
    }

    /// <summary>
    /// Loads a playlist from a stream.
    /// </summary>
    /// <param name="stream">Stream containing playlist data.</param>
    /// <param name="format">Format of the playlist data.</param>
    /// <param name="baseDirectory">Base directory for resolving relative paths.</param>
    /// <returns>A new Playlist populated with items from the stream.</returns>
    public static Playlist Load(Stream stream, PlaylistFormat format, string baseDirectory = "")
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var content = reader.ReadToEnd();

        return format switch
        {
            PlaylistFormat.M3U or PlaylistFormat.M3U8 => ParseM3U(content, baseDirectory),
            PlaylistFormat.PLS => ParsePLS(content, baseDirectory),
            PlaylistFormat.XSPF => ParseXSPF(content, baseDirectory),
            PlaylistFormat.ASX => ParseASX(content, baseDirectory),
            PlaylistFormat.WPL => ParseWPL(content, baseDirectory),
            _ => throw new NotSupportedException($"Unsupported playlist format: {format}")
        };
    }

    /// <summary>
    /// Saves a playlist to a file.
    /// </summary>
    /// <param name="playlist">The playlist to save.</param>
    /// <param name="filePath">Destination file path.</param>
    /// <param name="format">Format to save in (defaults to M3U8).</param>
    /// <param name="useRelativePaths">Whether to use relative paths from the playlist file location.</param>
    public static void Save(Playlist playlist, string filePath, PlaylistFormat format = PlaylistFormat.M3U8, bool useRelativePaths = false)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var content = format switch
        {
            PlaylistFormat.M3U => GenerateM3U(playlist, baseDir, useRelativePaths, extended: false),
            PlaylistFormat.M3U8 => GenerateM3U(playlist, baseDir, useRelativePaths, extended: true),
            PlaylistFormat.PLS => GeneratePLS(playlist, baseDir, useRelativePaths),
            PlaylistFormat.XSPF => GenerateXSPF(playlist, baseDir, useRelativePaths),
            _ => throw new NotSupportedException($"Unsupported playlist format: {format}")
        };

        var encoding = format == PlaylistFormat.M3U8 || format == PlaylistFormat.XSPF ? Encoding.UTF8 : Encoding.Default;
        File.WriteAllText(filePath, content, encoding);
    }

    /// <summary>
    /// Saves a playlist to a stream.
    /// </summary>
    public static void Save(Playlist playlist, Stream stream, PlaylistFormat format = PlaylistFormat.M3U8, string baseDirectory = "", bool useRelativePaths = false)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(stream);

        var content = format switch
        {
            PlaylistFormat.M3U => GenerateM3U(playlist, baseDirectory, useRelativePaths, extended: false),
            PlaylistFormat.M3U8 => GenerateM3U(playlist, baseDirectory, useRelativePaths, extended: true),
            PlaylistFormat.PLS => GeneratePLS(playlist, baseDirectory, useRelativePaths),
            PlaylistFormat.XSPF => GenerateXSPF(playlist, baseDirectory, useRelativePaths),
            _ => throw new NotSupportedException($"Unsupported playlist format: {format}")
        };

        var encoding = format == PlaylistFormat.M3U8 || format == PlaylistFormat.XSPF ? Encoding.UTF8 : Encoding.Default;
        var bytes = encoding.GetBytes(content);
        stream.Write(bytes);
    }

    /// <summary>
    /// Detects the format of a playlist file.
    /// </summary>
    public static PlaylistFormat DetectFormat(string content, string filePath = "")
    {
        // Check by content first
        var firstLine = content.AsSpan().TrimStart();
        if (firstLine.StartsWith(M3UHeader.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return PlaylistFormat.M3U8;

        if (firstLine.StartsWith(PlsHeader.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return PlaylistFormat.PLS;
        
        // XML-based formats - need to distinguish between XSPF, ASX, WPL
        if (firstLine.StartsWith("<?xml".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            firstLine.StartsWith("<".AsSpan()))
        {
            if (content.Contains("<asx", StringComparison.OrdinalIgnoreCase))
                return PlaylistFormat.ASX;
            if (content.Contains("<smil", StringComparison.OrdinalIgnoreCase))
                return PlaylistFormat.WPL;
            if (content.Contains("<playlist", StringComparison.OrdinalIgnoreCase))
                return PlaylistFormat.XSPF;
        }

        // Fallback to extension
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".m3u8" => PlaylistFormat.M3U8,
            ".m3u" => PlaylistFormat.M3U,
            ".pls" => PlaylistFormat.PLS,
            ".xspf" => PlaylistFormat.XSPF,
            ".asx" or ".wax" or ".wvx" => PlaylistFormat.ASX,
            ".wpl" => PlaylistFormat.WPL,
            _ => PlaylistFormat.M3U // Default assumption
        };
    }

    private static Playlist ParseM3U(string content, string baseDirectory)
    {
        var playlist = new Playlist();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        string? pendingTitle = null;
        long pendingDuration = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
                continue;

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(ExtInfPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Parse EXTINF: duration,title
                    var info = line.AsSpan(ExtInfPrefix.Length);
                    int commaIndex = info.IndexOf(',');

                    if (commaIndex > 0)
                    {
                        var durationPart = info[..commaIndex].Trim();
                        if (int.TryParse(durationPart, out int durationSec))
                        {
                            pendingDuration = durationSec * 1000L; // Convert to ms
                        }

                        pendingTitle = info[(commaIndex + 1)..].Trim().ToString();
                    }
                }
                // Skip other comments/directives
                continue;
            }

            // This is a media URI
            string uri = ResolveUri(line, baseDirectory);
            var item = new PlaylistItem(uri, pendingTitle ?? string.Empty, pendingDuration);
            playlist.Add(item);

            // Reset pending metadata
            pendingTitle = null;
            pendingDuration = 0;
        }

        return playlist;
    }

    private static Playlist ParsePLS(string content, string baseDirectory)
    {
        var playlist = new Playlist();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // Parse into a dictionary by entry number
        var entries = new Dictionary<int, (string? File, string? Title, long Duration)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith('['))
                continue;

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();

            // Extract entry number from key (e.g., File1, Title1, Length1)
            if (TryParseEntryKey(key, "File", out int fileNum))
            {
                if (!entries.TryGetValue(fileNum, out var entry))
                    entry = (null, null, 0);
                entries[fileNum] = (value, entry.Title, entry.Duration);
            }
            else if (TryParseEntryKey(key, "Title", out int titleNum))
            {
                if (!entries.TryGetValue(titleNum, out var entry))
                    entry = (null, null, 0);
                entries[titleNum] = (entry.File, value, entry.Duration);
            }
            else if (TryParseEntryKey(key, "Length", out int lengthNum))
            {
                if (int.TryParse(value, out int durationSec) && durationSec > 0)
                {
                    if (!entries.TryGetValue(lengthNum, out var entry))
                        entry = (null, null, 0);
                    entries[lengthNum] = (entry.File, entry.Title, durationSec * 1000L);
                }
            }
        }

        // Create playlist items in order
        foreach (var num in entries.Keys.Order())
        {
            var entry = entries[num];
            if (string.IsNullOrEmpty(entry.File))
                continue;

            string uri = ResolveUri(entry.File, baseDirectory);
            var item = new PlaylistItem(uri, entry.Title ?? string.Empty, entry.Duration);
            playlist.Add(item);
        }

        return playlist;
    }

    private static bool TryParseEntryKey(string key, string prefix, out int number)
    {
        number = 0;
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var numPart = key.AsSpan(prefix.Length);
        return int.TryParse(numPart, out number);
    }

    private static string ResolveUri(string uri, string baseDirectory)
    {
        // Check if it's already an absolute path or URL
        if (Uri.TryCreate(uri, UriKind.Absolute, out _))
            return uri;

        if (Path.IsPathRooted(uri))
            return uri;

        // Resolve relative path
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var resolved = Path.Combine(baseDirectory, uri);
            if (File.Exists(resolved))
                return Path.GetFullPath(resolved);
        }

        return uri;
    }

    private static string GenerateM3U(Playlist playlist, string baseDirectory, bool useRelativePaths, bool extended)
    {
        var builder = new StringBuilder();

        if (extended)
        {
            builder.AppendLine(M3UHeader);
        }

        foreach (var item in playlist.Items)
        {
            if (extended)
            {
                int durationSeconds = (int)(item.DurationMilliseconds / 1000);
                string title = string.IsNullOrEmpty(item.Title) ? Path.GetFileName(item.Uri) : item.Title;

                if (!string.IsNullOrEmpty(item.Artist))
                {
                    title = $"{item.Artist} - {title}";
                }

                builder.AppendLine($"{ExtInfPrefix}{durationSeconds},{title}");
            }

            string uri = useRelativePaths ? MakeRelativePath(item.Uri, baseDirectory) : item.Uri;
            builder.AppendLine(uri);
        }

        return builder.ToString();
    }

    private static string GeneratePLS(Playlist playlist, string baseDirectory, bool useRelativePaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine(PlsHeader);

        int index = 1;
        foreach (var item in playlist.Items)
        {
            string uri = useRelativePaths ? MakeRelativePath(item.Uri, baseDirectory) : item.Uri;
            builder.AppendLine($"File{index}={uri}");

            string title = string.IsNullOrEmpty(item.Title) ? Path.GetFileName(item.Uri) : item.Title;
            if (!string.IsNullOrEmpty(item.Artist))
            {
                title = $"{item.Artist} - {title}";
            }
            builder.AppendLine($"Title{index}={title}");

            int durationSeconds = (int)(item.DurationMilliseconds / 1000);
            builder.AppendLine($"Length{index}={durationSeconds}");

            index++;
        }

        builder.AppendLine($"NumberOfEntries={playlist.Count}");
        builder.AppendLine("Version=2");

        return builder.ToString();
    }

    private static string MakeRelativePath(string fullPath, string baseDirectory)
    {
        if (string.IsNullOrEmpty(baseDirectory))
            return fullPath;

        try
        {
            // Check if it's a URL
            if (Uri.TryCreate(fullPath, UriKind.Absolute, out var uri) && !uri.IsFile)
                return fullPath;

            var basePath = Path.GetFullPath(baseDirectory);
            var filePath = Path.GetFullPath(fullPath);

            // Use Path.GetRelativePath (.NET Core 2.0+)
            return Path.GetRelativePath(basePath, filePath);
        }
        catch
        {
            return fullPath;
        }
    }
    
    private static Playlist ParseXSPF(string content, string baseDirectory)
    {
        var playlist = new Playlist();
        
        try
        {
            var doc = XDocument.Parse(content);
            var root = doc.Root;
            if (root == null)
                return playlist;
            
            XNamespace ns = root.Name.Namespace ?? XNamespace.None;
            
            // Get playlist title if available
            string? playlistTitle = root.Element(ns + "title")?.Value;
            if (!string.IsNullOrEmpty(playlistTitle))
            {
                // Could store in playlist properties if Playlist class supported it
            }
            
            var trackList = root.Element(ns + "trackList");
            if (trackList == null)
                return playlist;
            
            foreach (var track in trackList.Elements(ns + "track"))
            {
                string? location = track.Element(ns + "location")?.Value;
                if (string.IsNullOrEmpty(location))
                    continue;
                
                string uri = ResolveUri(location, baseDirectory);
                string? title = track.Element(ns + "title")?.Value;
                string? artist = track.Element(ns + "creator")?.Value;
                string? album = track.Element(ns + "album")?.Value;
                
                long durationMs = 0;
                string? durationStr = track.Element(ns + "duration")?.Value;
                if (!string.IsNullOrEmpty(durationStr) && long.TryParse(durationStr, out long ms))
                {
                    durationMs = ms;
                }
                
                var item = new PlaylistItem(uri, title ?? string.Empty, durationMs)
                {
                    Artist = artist,
                    Album = album
                };
                playlist.Add(item);
            }
        }
        catch
        {
            // Return empty playlist on parse error
        }
        
        return playlist;
    }
    
    private static string GenerateXSPF(Playlist playlist, string baseDirectory, bool useRelativePaths)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(XspfNamespace + "playlist",
                new XAttribute("version", "1"),
                new XAttribute(XNamespace.Xmlns + "xspf", XspfNamespace)
            )
        );
        
        var root = doc.Root!;
        
        var trackList = new XElement(XspfNamespace + "trackList");
        
        foreach (var item in playlist.Items)
        {
            string uri = useRelativePaths ? MakeRelativePath(item.Uri, baseDirectory) : item.Uri;
            
            var track = new XElement(XspfNamespace + "track",
                new XElement(XspfNamespace + "location", uri)
            );
            
            if (!string.IsNullOrEmpty(item.Title))
                track.Add(new XElement(XspfNamespace + "title", item.Title));
            if (!string.IsNullOrEmpty(item.Artist))
                track.Add(new XElement(XspfNamespace + "creator", item.Artist));
            if (!string.IsNullOrEmpty(item.Album))
                track.Add(new XElement(XspfNamespace + "album", item.Album));
            if (item.DurationMilliseconds > 0)
                track.Add(new XElement(XspfNamespace + "duration", item.DurationMilliseconds));
            
            trackList.Add(track);
        }
        
        root.Add(trackList);
        
        using var writer = new StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }
    
    /// <summary>
    /// Parses an ASX (Advanced Stream Redirector) playlist.
    /// </summary>
    private static Playlist ParseASX(string content, string baseDirectory)
    {
        var playlist = new Playlist();
        
        try
        {
            // ASX files may have uppercase tags or other quirks - normalize
            var doc = XDocument.Parse(content);
            var root = doc.Root;
            
            if (root == null)
                return playlist;
            
            // Extract base URL if present
            string baseUrl = string.Empty;
            var baseElement = FindElementIgnoreCase(root, "BASE");
            if (baseElement != null)
            {
                baseUrl = GetAttributeIgnoreCase(baseElement, "HREF") ?? string.Empty;
            }
            
            // Extract global title
            var titleElement = FindElementIgnoreCase(root, "TITLE");
            if (titleElement != null)
            {
                playlist.Title = titleElement.Value.Trim();
            }
            
            // Parse entries
            foreach (var entry in FindElementsIgnoreCase(root, "ENTRY"))
            {
                var refElement = FindElementIgnoreCase(entry, "REF");
                if (refElement == null)
                    continue;
                
                var href = GetAttributeIgnoreCase(refElement, "HREF");
                if (string.IsNullOrEmpty(href))
                    continue;
                
                // Resolve path
                string resolvedPath = ResolveAsxPath(href, baseUrl, baseDirectory);
                
                // Extract metadata first
                string? itemTitle = null;
                string? itemArtist = null;
                long itemDuration = 0;
                
                var entryTitle = FindElementIgnoreCase(entry, "TITLE");
                if (entryTitle != null)
                    itemTitle = entryTitle.Value.Trim();
                
                var author = FindElementIgnoreCase(entry, "AUTHOR");
                if (author != null)
                    itemArtist = author.Value.Trim();
                
                var duration = FindElementIgnoreCase(entry, "DURATION");
                if (duration != null)
                {
                    var durationValue = GetAttributeIgnoreCase(duration, "VALUE") ?? duration.Value;
                    if (TryParseAsxDuration(durationValue, out long durationMs))
                        itemDuration = durationMs;
                }
                
                // Create item with constructor
                var item = new PlaylistItem(resolvedPath, itemTitle ?? string.Empty, itemDuration);
                if (!string.IsNullOrEmpty(itemArtist))
                    item.Artist = itemArtist;
                
                playlist.Add(item);
            }
        }
        catch
        {
            // Failed to parse as XML - return empty playlist
        }
        
        return playlist;
    }
    
    private static string ResolveAsxPath(string href, string baseUrl, string baseDirectory)
    {
        // If it's already an absolute URL, use it directly
        if (Uri.TryCreate(href, UriKind.Absolute, out _))
            return href;
        
        // Try combining with base URL
        if (!string.IsNullOrEmpty(baseUrl))
        {
            if (Uri.TryCreate(new Uri(baseUrl), href, out var combinedUri))
                return combinedUri.ToString();
        }
        
        // Try as local path
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var fullPath = Path.Combine(baseDirectory, href.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                return fullPath;
        }
        
        return href;
    }
    
    private static bool TryParseAsxDuration(string value, out long milliseconds)
    {
        milliseconds = 0;
        
        if (string.IsNullOrEmpty(value))
            return false;
        
        // Format: HH:MM:SS.mmm or HH:MM:SS
        var parts = value.Trim().Split(':');
        if (parts.Length < 2)
            return false;
        
        try
        {
            int hours = 0;
            int minutes = 0;
            double seconds = 0;
            
            if (parts.Length == 3)
            {
                hours = int.Parse(parts[0]);
                minutes = int.Parse(parts[1]);
                seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            else if (parts.Length == 2)
            {
                minutes = int.Parse(parts[0]);
                seconds = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            
            milliseconds = (long)((hours * 3600 + minutes * 60 + seconds) * 1000);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Parses a WPL (Windows Media Player Playlist) file.
    /// WPL uses SMIL-like XML structure.
    /// </summary>
    private static Playlist ParseWPL(string content, string baseDirectory)
    {
        var playlist = new Playlist();
        
        try
        {
            var doc = XDocument.Parse(content);
            var root = doc.Root;
            
            if (root == null)
                return playlist;
            
            // Extract title from head/title
            var head = FindElementIgnoreCase(root, "head");
            if (head != null)
            {
                var title = FindElementIgnoreCase(head, "title");
                if (title != null)
                    playlist.Title = title.Value.Trim();
            }
            
            // Find the seq element containing media items
            var body = FindElementIgnoreCase(root, "body");
            if (body == null)
                return playlist;
            
            var seq = FindElementIgnoreCase(body, "seq");
            if (seq == null)
                return playlist;
            
            // Parse media elements
            foreach (var media in FindElementsIgnoreCase(seq, "media"))
            {
                var src = GetAttributeIgnoreCase(media, "src");
                if (string.IsNullOrEmpty(src))
                    continue;
                
                // Resolve path
                string resolvedPath = src;
                if (!Path.IsPathRooted(src) && !Uri.TryCreate(src, UriKind.Absolute, out _))
                {
                    if (!string.IsNullOrEmpty(baseDirectory))
                        resolvedPath = Path.Combine(baseDirectory, src.Replace('/', Path.DirectorySeparatorChar));
                }
                
                var item = new PlaylistItem(resolvedPath);
                playlist.Add(item);
            }
        }
        catch
        {
            // Failed to parse - return empty playlist
        }
        
        return playlist;
    }
    
    // Helper methods for case-insensitive XML parsing
    private static XElement? FindElementIgnoreCase(XElement parent, string name)
    {
        return parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    }
    
    private static IEnumerable<XElement> FindElementsIgnoreCase(XElement parent, string name)
    {
        return parent.Elements()
            .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    }
    
    private static string? GetAttributeIgnoreCase(XElement element, string name)
    {
        var attr = element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        return attr?.Value;
    }
}

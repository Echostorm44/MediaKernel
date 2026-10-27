// MediaKernel - Album Art Finder
// Scans folders for album art images

using System;
using System.Collections.Generic;
using System.IO;

namespace MediaKernel.Core.Metadata;

/// <summary>
/// Result of an album art search.
/// </summary>
public readonly struct AlbumArtResult
{
    public string FilePath { get; }
    public AlbumArtType Type { get; }
    public int Priority { get; }
    
    public AlbumArtResult(string filePath, AlbumArtType type, int priority)
    {
        FilePath = filePath;
        Type = type;
        Priority = priority;
    }
    
    public bool IsValid => !string.IsNullOrEmpty(FilePath);
}

/// <summary>
/// Type of album art based on filename.
/// </summary>
public enum AlbumArtType
{
    /// <summary>Front cover - highest priority</summary>
    Cover,
    /// <summary>Folder.jpg/png - common Windows standard</summary>
    Folder,
    /// <summary>Album-specific art</summary>
    Album,
    /// <summary>Front.jpg/png</summary>
    Front,
    /// <summary>Back cover</summary>
    Back,
    /// <summary>CD/disc image</summary>
    Disc,
    /// <summary>Artist image</summary>
    Artist,
    /// <summary>Fanart/background</summary>
    Fanart,
    /// <summary>Generic image in folder</summary>
    Unknown
}

/// <summary>
/// Configuration for album art discovery.
/// </summary>
public sealed class AlbumArtFinderOptions
{
    /// <summary>Filename patterns to search for (case-insensitive).</summary>
    public string[] SearchPatterns { get; set; } =
    {
        "cover.jpg", "cover.png", "cover.jpeg",
        "folder.jpg", "folder.png",
        "album.jpg", "album.png",
        "front.jpg", "front.png",
        "albumart.jpg", "albumart.png",
        "albumartsmall.jpg", // Windows Media Player
        "thumb.jpg", "thumb.png"
    };
    
    /// <summary>Image extensions to consider.</summary>
    public string[] ImageExtensions { get; set; } = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
    
    /// <summary>Maximum file size in bytes (0 = no limit).</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
    
    /// <summary>Search parent folders up to this depth.</summary>
    public int SearchParentDepth { get; set; } = 1;
    
    /// <summary>Whether to search subdirectories like "Artwork".</summary>
    public bool SearchArtworkSubfolders { get; set; } = true;
    
    /// <summary>Subfolder names to check for artwork.</summary>
    public string[] ArtworkSubfolderNames { get; set; } = { "artwork", "art", "covers", "scans", "images" };
}

/// <summary>
/// Finds album art images in the filesystem based on media file location.
/// Reference: VLC modules/meta_engine/folder.c
/// </summary>
public sealed class AlbumArtFinder
{
    private readonly AlbumArtFinderOptions options;
    
    public AlbumArtFinder() : this(new AlbumArtFinderOptions())
    {
    }
    
    public AlbumArtFinder(AlbumArtFinderOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }
    
    /// <summary>
    /// Find album art for a media file.
    /// </summary>
    /// <param name="mediaFilePath">Path to the media file</param>
    /// <returns>Best matching album art, or invalid result if none found</returns>
    public AlbumArtResult FindAlbumArt(string mediaFilePath)
    {
        if (string.IsNullOrEmpty(mediaFilePath))
            return default;
        
        string? directory = Path.GetDirectoryName(mediaFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return default;
        
        var results = new List<AlbumArtResult>();
        
        // Search current directory
        SearchDirectory(directory, results);
        
        // Search artwork subfolders
        if (options.SearchArtworkSubfolders)
        {
            foreach (string subfolderName in options.ArtworkSubfolderNames)
            {
                string subPath = Path.Combine(directory, subfolderName);
                if (Directory.Exists(subPath))
                {
                    SearchDirectory(subPath, results);
                }
            }
        }
        
        // Search parent directories
        string? parentDir = directory;
        for (int i = 0; i < options.SearchParentDepth; i++)
        {
            parentDir = Path.GetDirectoryName(parentDir);
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                break;
            
            SearchDirectory(parentDir, results);
            
            // Also check artwork subfolders in parent
            if (options.SearchArtworkSubfolders)
            {
                foreach (string subfolderName in options.ArtworkSubfolderNames)
                {
                    string subPath = Path.Combine(parentDir, subfolderName);
                    if (Directory.Exists(subPath))
                    {
                        SearchDirectory(subPath, results);
                    }
                }
            }
        }
        
        // Return best match (lowest priority number)
        if (results.Count == 0)
            return default;
        
        var best = results[0];
        for (int i = 1; i < results.Count; i++)
        {
            if (results[i].Priority < best.Priority)
                best = results[i];
        }
        
        return best;
    }
    
    /// <summary>
    /// Find all album art images in folder.
    /// </summary>
    public IReadOnlyList<AlbumArtResult> FindAllAlbumArt(string mediaFilePath)
    {
        var results = new List<AlbumArtResult>();
        
        if (string.IsNullOrEmpty(mediaFilePath))
            return results;
        
        string? directory = Path.GetDirectoryName(mediaFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return results;
        
        // Collect from all locations
        SearchDirectory(directory, results);
        
        if (options.SearchArtworkSubfolders)
        {
            foreach (string subfolderName in options.ArtworkSubfolderNames)
            {
                string subPath = Path.Combine(directory, subfolderName);
                if (Directory.Exists(subPath))
                {
                    SearchDirectory(subPath, results);
                }
            }
        }
        
        // Sort by priority
        results.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return results;
    }
    
    private void SearchDirectory(string directory, List<AlbumArtResult> results)
    {
        try
        {
            // First check known patterns
            foreach (string pattern in options.SearchPatterns)
            {
                string fullPath = Path.Combine(directory, pattern);
                if (File.Exists(fullPath) && IsValidImageFile(fullPath))
                {
                    var type = ClassifyFilename(pattern);
                    results.Add(new AlbumArtResult(fullPath, type, GetPriority(type)));
                }
            }
            
            // Then scan for any image files
            foreach (string ext in options.ImageExtensions)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*" + ext))
                    {
                        // Skip if already found
                        bool alreadyFound = false;
                        foreach (var result in results)
                        {
                            if (string.Equals(result.FilePath, file, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyFound = true;
                                break;
                            }
                        }
                        
                        if (!alreadyFound && IsValidImageFile(file))
                        {
                            string filename = Path.GetFileName(file);
                            var type = ClassifyFilename(filename);
                            results.Add(new AlbumArtResult(file, type, GetPriority(type)));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible directories
                }
            }
        }
        catch (Exception)
        {
            // Ignore directory access errors
        }
    }
    
    private bool IsValidImageFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            
            if (!info.Exists)
                return false;
            
            if (options.MaxFileSizeBytes > 0 && info.Length > options.MaxFileSizeBytes)
                return false;
            
            // Quick check for valid image header
            using var stream = info.OpenRead();
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) < 8)
                return false;
            
            // Check common image signatures
            // JPEG: FF D8 FF
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return true;
            
            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return true;
            
            // GIF: GIF87a or GIF89a
            if (header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
                return true;
            
            // BMP: BM
            if (header[0] == 'B' && header[1] == 'M')
                return true;
            
            // WebP: RIFF....WEBP
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F')
                return true;
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private static AlbumArtType ClassifyFilename(string filename)
    {
        string lower = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        
        return lower switch
        {
            "cover" or "albumart" or "albumartsmall" => AlbumArtType.Cover,
            "folder" or "thumb" => AlbumArtType.Folder,
            "album" => AlbumArtType.Album,
            "front" => AlbumArtType.Front,
            "back" => AlbumArtType.Back,
            "disc" or "cd" => AlbumArtType.Disc,
            "artist" => AlbumArtType.Artist,
            "fanart" or "background" => AlbumArtType.Fanart,
            _ => AlbumArtType.Unknown
        };
    }
    
    private static int GetPriority(AlbumArtType type)
    {
        return type switch
        {
            AlbumArtType.Cover => 0,
            AlbumArtType.Folder => 1,
            AlbumArtType.Album => 2,
            AlbumArtType.Front => 3,
            AlbumArtType.Back => 10,
            AlbumArtType.Disc => 11,
            AlbumArtType.Artist => 15,
            AlbumArtType.Fanart => 20,
            AlbumArtType.Unknown => 50,
            _ => 100
        };
    }
    
    /// <summary>
    /// Load album art bytes from best matching image.
    /// </summary>
    public byte[]? LoadAlbumArt(string mediaFilePath)
    {
        var result = FindAlbumArt(mediaFilePath);
        if (!result.IsValid)
            return null;
        
        try
        {
            return File.ReadAllBytes(result.FilePath);
        }
        catch
        {
            return null;
        }
    }
}

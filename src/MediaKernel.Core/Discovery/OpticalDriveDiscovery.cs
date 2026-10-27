// Optical drive discovery for Windows
// Detects CD/DVD/Blu-ray drives and provides access to disc media

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaKernel.Core.Discovery;

/// <summary>
/// Type of optical drive.
/// </summary>
public enum OpticalDriveType
{
    Unknown,
    CdRom,
    DvdRom,
    BluRay
}

/// <summary>
/// Represents a discovered optical drive.
/// </summary>
public readonly struct OpticalDriveInfo
{
    /// <summary>
    /// Drive letter (e.g., "D:").
    /// </summary>
    public string DriveLetter { get; init; }
    
    /// <summary>
    /// Drive path (e.g., "D:\").
    /// </summary>
    public string DrivePath { get; init; }
    
    /// <summary>
    /// Volume label if a disc is inserted, null otherwise.
    /// </summary>
    public string? VolumeLabel { get; init; }
    
    /// <summary>
    /// True if a disc is currently inserted.
    /// </summary>
    public bool HasDisc { get; init; }
    
    /// <summary>
    /// Type of optical drive (if detectable).
    /// </summary>
    public OpticalDriveType DriveType { get; init; }
    
    /// <summary>
    /// File URL for the drive (e.g., "file:///D:/").
    /// </summary>
    public string FileUrl => $"file:///{DriveLetter}/";
    
    /// <summary>
    /// Returns a friendly display name.
    /// </summary>
    public string DisplayName => VolumeLabel != null 
        ? $"{DriveLetter} ({VolumeLabel})" 
        : DriveLetter;
}

/// <summary>
/// Discovers optical (CD/DVD/Blu-ray) drives on the system.
/// </summary>
public static class OpticalDriveDiscovery
{
    // Windows API for drive type detection
    private const uint DRIVE_CDROM = 5;
    
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern uint GetDriveType(string lpRootPathName);
    
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();
    
    /// <summary>
    /// Discovers all optical drives on the system.
    /// </summary>
    /// <returns>List of discovered optical drives.</returns>
    public static List<OpticalDriveInfo> DiscoverDrives()
    {
        var drives = new List<OpticalDriveInfo>();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DiscoverWindowsDrives(drives);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DiscoverLinuxDrives(drives);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            DiscoverMacOsDrives(drives);
        }
        
        return drives;
    }
    
    /// <summary>
    /// Discovers optical drives using .NET DriveInfo (cross-platform fallback).
    /// </summary>
    public static List<OpticalDriveInfo> DiscoverDrivesCrossPlatform()
    {
        var drives = new List<OpticalDriveInfo>();
        
        try
        {
            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                if (driveInfo.DriveType == DriveType.CDRom)
                {
                    var info = CreateDriveInfo(driveInfo);
                    drives.Add(info);
                }
            }
        }
        catch (Exception)
        {
            // Access denied or other issues
        }
        
        return drives;
    }
    
    [SupportedOSPlatform("windows")]
    private static void DiscoverWindowsDrives(List<OpticalDriveInfo> drives)
    {
        try
        {
            uint driveMask = GetLogicalDrives();
            
            for (int i = 0; i < 26; i++)
            {
                if ((driveMask & (1u << i)) == 0)
                    continue;
                    
                char letter = (char)('A' + i);
                string rootPath = $"{letter}:\\";
                
                if (GetDriveType(rootPath) == DRIVE_CDROM)
                {
                    var info = CreateDriveInfoFromPath(rootPath);
                    drives.Add(info);
                }
            }
        }
        catch (Exception)
        {
            // Fall back to .NET method
            foreach (var drive in DiscoverDrivesCrossPlatform())
                drives.Add(drive);
        }
    }
    
    private static void DiscoverLinuxDrives(List<OpticalDriveInfo> drives)
    {
        // Check common Linux optical drive paths
        string[] possiblePaths = 
        {
            "/dev/cdrom",
            "/dev/dvd",
            "/dev/sr0",
            "/dev/sr1",
            "/dev/sr2"
        };
        
        foreach (var path in possiblePaths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                // Check for symlink target
                string devicePath = path;
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.LinkTarget != null)
                        devicePath = fileInfo.LinkTarget;
                }
                catch { }
                
                // Check mount point
                string? mountPoint = GetLinuxMountPoint(devicePath);
                
                drives.Add(new OpticalDriveInfo
                {
                    DriveLetter = Path.GetFileName(path),
                    DrivePath = mountPoint ?? path,
                    VolumeLabel = mountPoint != null ? GetVolumeLabel(mountPoint) : null,
                    HasDisc = mountPoint != null,
                    DriveType = path.Contains("dvd") ? OpticalDriveType.DvdRom : OpticalDriveType.CdRom
                });
            }
        }
        
        // Also check /media and /mnt for mounted discs
        CheckLinuxMountDirectories(drives, "/media");
        CheckLinuxMountDirectories(drives, "/mnt");
    }
    
    private static void CheckLinuxMountDirectories(List<OpticalDriveInfo> drives, string baseDir)
    {
        if (!Directory.Exists(baseDir))
            return;
            
        try
        {
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                // Check if it looks like a disc mount
                string videoTsPath = Path.Combine(dir, "VIDEO_TS");
                string bdmvPath = Path.Combine(dir, "BDMV");
                
                if (Directory.Exists(videoTsPath))
                {
                    drives.Add(new OpticalDriveInfo
                    {
                        DriveLetter = Path.GetFileName(dir),
                        DrivePath = dir,
                        VolumeLabel = Path.GetFileName(dir),
                        HasDisc = true,
                        DriveType = OpticalDriveType.DvdRom
                    });
                }
                else if (Directory.Exists(bdmvPath))
                {
                    drives.Add(new OpticalDriveInfo
                    {
                        DriveLetter = Path.GetFileName(dir),
                        DrivePath = dir,
                        VolumeLabel = Path.GetFileName(dir),
                        HasDisc = true,
                        DriveType = OpticalDriveType.BluRay
                    });
                }
            }
        }
        catch { }
    }
    
    private static string? GetLinuxMountPoint(string devicePath)
    {
        try
        {
            if (!File.Exists("/proc/mounts"))
                return null;
                
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && parts[0] == devicePath)
                    return parts[1];
            }
        }
        catch { }
        
        return null;
    }
    
    private static void DiscoverMacOsDrives(List<OpticalDriveInfo> drives)
    {
        // Check /Volumes for mounted discs
        string volumesPath = "/Volumes";
        
        if (!Directory.Exists(volumesPath))
            return;
            
        try
        {
            foreach (var dir in Directory.GetDirectories(volumesPath))
            {
                // Check for DVD or Blu-ray structure
                string videoTsPath = Path.Combine(dir, "VIDEO_TS");
                string bdmvPath = Path.Combine(dir, "BDMV");
                
                OpticalDriveType driveType = OpticalDriveType.Unknown;
                if (Directory.Exists(videoTsPath))
                    driveType = OpticalDriveType.DvdRom;
                else if (Directory.Exists(bdmvPath))
                    driveType = OpticalDriveType.BluRay;
                else
                    continue; // Not an optical disc
                    
                drives.Add(new OpticalDriveInfo
                {
                    DriveLetter = Path.GetFileName(dir),
                    DrivePath = dir,
                    VolumeLabel = Path.GetFileName(dir),
                    HasDisc = true,
                    DriveType = driveType
                });
            }
        }
        catch { }
    }
    
    private static OpticalDriveInfo CreateDriveInfo(DriveInfo driveInfo)
    {
        string? volumeLabel = null;
        bool hasDisc = false;
        
        try
        {
            hasDisc = driveInfo.IsReady;
            if (hasDisc)
                volumeLabel = driveInfo.VolumeLabel;
        }
        catch { }
        
        return new OpticalDriveInfo
        {
            DriveLetter = driveInfo.Name.TrimEnd('\\'),
            DrivePath = driveInfo.RootDirectory.FullName,
            VolumeLabel = volumeLabel,
            HasDisc = hasDisc,
            DriveType = DetectDriveType(driveInfo.RootDirectory.FullName, hasDisc)
        };
    }
    
    private static OpticalDriveInfo CreateDriveInfoFromPath(string rootPath)
    {
        string? volumeLabel = null;
        bool hasDisc = false;
        
        try
        {
            var driveInfo = new DriveInfo(rootPath);
            hasDisc = driveInfo.IsReady;
            if (hasDisc)
                volumeLabel = driveInfo.VolumeLabel;
        }
        catch { }
        
        return new OpticalDriveInfo
        {
            DriveLetter = rootPath.TrimEnd('\\'),
            DrivePath = rootPath,
            VolumeLabel = volumeLabel,
            HasDisc = hasDisc,
            DriveType = DetectDriveType(rootPath, hasDisc)
        };
    }
    
    private static string? GetVolumeLabel(string path)
    {
        try
        {
            var driveInfo = new DriveInfo(path);
            return driveInfo.IsReady ? driveInfo.VolumeLabel : null;
        }
        catch
        {
            return null;
        }
    }
    
    private static OpticalDriveType DetectDriveType(string rootPath, bool hasDisc)
    {
        if (!hasDisc)
            return OpticalDriveType.Unknown;
            
        try
        {
            // Check for Blu-ray structure
            if (Directory.Exists(Path.Combine(rootPath, "BDMV")))
                return OpticalDriveType.BluRay;
                
            // Check for DVD structure
            if (Directory.Exists(Path.Combine(rootPath, "VIDEO_TS")))
                return OpticalDriveType.DvdRom;
                
            // Check for Audio CD (CDDA)
            string[] files = Directory.GetFiles(rootPath, "*.cda");
            if (files.Length > 0)
                return OpticalDriveType.CdRom;
        }
        catch { }
        
        return OpticalDriveType.CdRom; // Default to CD-ROM
    }
    
    /// <summary>
    /// Checks if a disc is a DVD-Video disc.
    /// </summary>
    public static bool IsDvdVideo(string drivePath)
    {
        return Directory.Exists(Path.Combine(drivePath, "VIDEO_TS"));
    }
    
    /// <summary>
    /// Checks if a disc is a Blu-ray disc.
    /// </summary>
    public static bool IsBluRay(string drivePath)
    {
        return Directory.Exists(Path.Combine(drivePath, "BDMV"));
    }
    
    /// <summary>
    /// Checks if a disc is an Audio CD.
    /// </summary>
    public static bool IsAudioCd(string drivePath)
    {
        try
        {
            return Directory.GetFiles(drivePath, "*.cda").Length > 0;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Gets the main video file for a DVD disc.
    /// </summary>
    public static string? GetDvdMainTitle(string drivePath)
    {
        string videoTsPath = Path.Combine(drivePath, "VIDEO_TS");
        if (!Directory.Exists(videoTsPath))
            return null;
            
        // Look for VTS_01_1.VOB (usually main feature)
        string mainVob = Path.Combine(videoTsPath, "VTS_01_1.VOB");
        if (File.Exists(mainVob))
            return mainVob;
            
        // Look for any VOB file
        var vobFiles = Directory.GetFiles(videoTsPath, "*.VOB");
        return vobFiles.Length > 0 ? vobFiles[0] : null;
    }
    
    /// <summary>
    /// Gets the main playlist for a Blu-ray disc.
    /// </summary>
    public static string? GetBluRayMainPlaylist(string drivePath)
    {
        string playlistPath = Path.Combine(drivePath, "BDMV", "PLAYLIST");
        if (!Directory.Exists(playlistPath))
            return null;
            
        // Look for 00000.mpls (usually main feature)
        string mainPlaylist = Path.Combine(playlistPath, "00000.mpls");
        if (File.Exists(mainPlaylist))
            return mainPlaylist;
            
        // Return first playlist
        var playlists = Directory.GetFiles(playlistPath, "*.mpls");
        return playlists.Length > 0 ? playlists[0] : null;
    }
}

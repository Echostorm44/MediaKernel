// MediaKernel - File Association Manager
// Register file associations, context menus, thumbnail providers

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Platform;

/// <summary>
/// Cross-platform file association management.
/// </summary>
public sealed class FileAssociationManager
{
    private readonly string applicationPath;
    private readonly string applicationName;
    private readonly IPlatformFileAssociation platformHandler;
    
    public FileAssociationManager(string applicationPath, string applicationName = "MediaKernel")
    {
        this.applicationPath = applicationPath;
        this.applicationName = applicationName;
        platformHandler = CreatePlatformHandler();
    }
    
    private IPlatformFileAssociation CreatePlatformHandler()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsFileAssociation(applicationPath, applicationName);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxFileAssociation(applicationPath, applicationName);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsFileAssociation(applicationPath, applicationName);
        else
            return new NullFileAssociation();
    }
    
    /// <summary>
    /// All supported video extensions.
    /// </summary>
    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".webm", ".flv",
        ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".ogv",
        ".3gp", ".3g2", ".divx", ".xvid", ".rm", ".rmvb", ".asf"
    ];
    
    /// <summary>
    /// All supported audio extensions.
    /// </summary>
    public static readonly string[] AudioExtensions =
    [
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus",
        ".wma", ".ape", ".mpc", ".wv", ".tta", ".aiff", ".aif",
        ".dsf", ".dff", ".ac3", ".dts", ".mid", ".midi"
    ];
    
    /// <summary>
    /// All supported playlist extensions.
    /// </summary>
    public static readonly string[] PlaylistExtensions =
    [
        ".m3u", ".m3u8", ".pls", ".xspf", ".wpl", ".asx", ".cue"
    ];
    
    /// <summary>
    /// Register as default handler for specified extensions.
    /// </summary>
    public async Task RegisterAsync(IEnumerable<string>? extensions = null, bool asDefault = false,
        CancellationToken cancellationToken = default)
    {
        var exts = extensions ?? VideoExtensions.Concat(AudioExtensions);
        await platformHandler.RegisterAsync(exts, asDefault, cancellationToken);
    }
    
    /// <summary>
    /// Unregister file associations.
    /// </summary>
    public async Task UnregisterAsync(IEnumerable<string>? extensions = null,
        CancellationToken cancellationToken = default)
    {
        var exts = extensions ?? VideoExtensions.Concat(AudioExtensions);
        await platformHandler.UnregisterAsync(exts, cancellationToken);
    }
    
    /// <summary>
    /// Check if the application is the default handler for an extension.
    /// </summary>
    public bool IsDefaultHandler(string extension)
    {
        return platformHandler.IsDefaultHandler(extension);
    }
    
    /// <summary>
    /// Add context menu integration ("Open with MediaKernel").
    /// </summary>
    public async Task AddContextMenuAsync(CancellationToken cancellationToken = default)
    {
        await platformHandler.AddContextMenuAsync(cancellationToken);
    }
    
    /// <summary>
    /// Remove context menu integration.
    /// </summary>
    public async Task RemoveContextMenuAsync(CancellationToken cancellationToken = default)
    {
        await platformHandler.RemoveContextMenuAsync(cancellationToken);
    }
}

/// <summary>
/// Platform-specific file association interface.
/// </summary>
internal interface IPlatformFileAssociation
{
    Task RegisterAsync(IEnumerable<string> extensions, bool asDefault, CancellationToken cancellationToken);
    Task UnregisterAsync(IEnumerable<string> extensions, CancellationToken cancellationToken);
    bool IsDefaultHandler(string extension);
    Task AddContextMenuAsync(CancellationToken cancellationToken);
    Task RemoveContextMenuAsync(CancellationToken cancellationToken);
}

// ============================================================================
// Windows File Associations
// ============================================================================

/// <summary>
/// Windows file association using registry.
/// </summary>
internal sealed class WindowsFileAssociation : IPlatformFileAssociation
{
    private readonly string applicationPath;
    private readonly string applicationName;
    private readonly string progId;
    
    public WindowsFileAssociation(string applicationPath, string applicationName)
    {
        this.applicationPath = applicationPath;
        this.applicationName = applicationName;
        this.progId = $"{applicationName}.MediaFile";
    }
    
    public async Task RegisterAsync(IEnumerable<string> extensions, bool asDefault,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        
        await Task.Run(() =>
        {
            // Create ProgID
            // HKCU\Software\Classes\MediaKernel.MediaFile
            //   (Default) = "MediaKernel Media File"
            //   \DefaultIcon
            //     (Default) = "path\to\app.exe,0"
            //   \shell\open\command
            //     (Default) = "\"path\to\app.exe\" \"%1\""
            
            string progIdPath = $@"Software\Classes\{progId}";
            
            using var progIdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(progIdPath);
            progIdKey?.SetValue(null, $"{applicationName} Media File");
            
            using var iconKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{progIdPath}\DefaultIcon");
            iconKey?.SetValue(null, $"\"{applicationPath}\",0");
            
            using var commandKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{progIdPath}\shell\open\command");
            commandKey?.SetValue(null, $"\"{applicationPath}\" \"%1\"");
            
            // Register each extension
            foreach (string ext in extensions)
            {
                string extKey = ext.StartsWith('.') ? ext : $".{ext}";
                
                // HKCU\Software\Classes\.ext
                using var extRegKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extKey}");
                
                if (asDefault)
                {
                    extRegKey?.SetValue(null, progId);
                }
                
                // Add to OpenWithProgIds
                using var openWithKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extKey}\OpenWithProgIds");
                openWithKey?.SetValue(progId, string.Empty);
            }
            
            // Notify shell of changes
            NotifyShellOfChange();
        }, cancellationToken);
    }
    
    public async Task UnregisterAsync(IEnumerable<string> extensions, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        
        await Task.Run(() =>
        {
            foreach (string ext in extensions)
            {
                string extKey = ext.StartsWith('.') ? ext : $".{ext}";
                
                try
                {
                    using var openWithKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        $@"Software\Classes\{extKey}\OpenWithProgIds", true);
                    openWithKey?.DeleteValue(progId, false);
                    
                    // If we were the default, clear it
                    using var extRegKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        $@"Software\Classes\{extKey}", true);
                    if (extRegKey?.GetValue(null)?.ToString() == progId)
                    {
                        extRegKey.SetValue(null, string.Empty);
                    }
                }
                catch
                {
                    // Ignore errors during unregistration
                }
            }
            
            // Delete ProgID
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", false);
            }
            catch { }
            
            NotifyShellOfChange();
        }, cancellationToken);
    }
    
    public bool IsDefaultHandler(string extension)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        
        string extKey = extension.StartsWith('.') ? extension : $".{extension}";
        
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extKey}");
            return key?.GetValue(null)?.ToString() == progId;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task AddContextMenuAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        
        await Task.Run(() =>
        {
            // Add to * (all files) context menu
            // HKCU\Software\Classes\*\shell\MediaKernel
            //   (Default) = "Open with MediaKernel"
            //   Icon = "path\to\app.exe,0"
            //   \command
            //     (Default) = "\"path\to\app.exe\" \"%1\""
            
            string menuPath = $@"Software\Classes\*\shell\{applicationName}";
            
            using var menuKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(menuPath);
            menuKey?.SetValue(null, $"Open with {applicationName}");
            menuKey?.SetValue("Icon", $"\"{applicationPath}\"");
            
            using var commandKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{menuPath}\command");
            commandKey?.SetValue(null, $"\"{applicationPath}\" \"%1\"");
            
            // Also add to Directory\Background for folders
            string folderPath = $@"Software\Classes\Directory\Background\shell\{applicationName}";
            
            using var folderKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(folderPath);
            folderKey?.SetValue(null, $"Open folder with {applicationName}");
            folderKey?.SetValue("Icon", $"\"{applicationPath}\"");
            
            using var folderCommandKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{folderPath}\command");
            folderCommandKey?.SetValue(null, $"\"{applicationPath}\" \"%V\"");
            
            NotifyShellOfChange();
        }, cancellationToken);
    }
    
    public async Task RemoveContextMenuAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        
        await Task.Run(() =>
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\*\shell\{applicationName}", false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\Directory\Background\shell\{applicationName}", false);
            }
            catch { }
            
            NotifyShellOfChange();
        }, cancellationToken);
    }
    
    private static void NotifyShellOfChange()
    {
        // SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, NULL, NULL)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }
    
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}

// ============================================================================
// Linux File Associations (XDG)
// ============================================================================

/// <summary>
/// Linux file associations using XDG desktop entries and MIME types.
/// </summary>
internal sealed class LinuxFileAssociation : IPlatformFileAssociation
{
    private readonly string applicationPath;
    private readonly string applicationName;
    private readonly string desktopFileName;
    
    public LinuxFileAssociation(string applicationPath, string applicationName)
    {
        this.applicationPath = applicationPath;
        this.applicationName = applicationName;
        this.desktopFileName = $"{applicationName.ToLowerInvariant()}.desktop";
    }
    
    public async Task RegisterAsync(IEnumerable<string> extensions, bool asDefault,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        
        // Create .desktop file in ~/.local/share/applications/
        string applicationsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        
        Directory.CreateDirectory(applicationsDir);
        
        // Build MIME types list
        var mimeTypes = extensions.Select(GetMimeType).Distinct().ToList();
        
        string desktopEntry = $"""
            [Desktop Entry]
            Type=Application
            Name={applicationName}
            Comment=Media Player
            Exec="{applicationPath}" %U
            Icon={applicationName.ToLowerInvariant()}
            Terminal=false
            Categories=AudioVideo;Audio;Video;Player;
            MimeType={string.Join(";", mimeTypes)};
            """;
        
        string desktopPath = Path.Combine(applicationsDir, desktopFileName);
        await File.WriteAllTextAsync(desktopPath, desktopEntry, cancellationToken);
        
        // Update MIME database
        if (asDefault)
        {
            foreach (string mime in mimeTypes)
            {
                await SetDefaultApplicationAsync(mime, cancellationToken);
            }
        }
        
        // Update desktop database
        await RunCommandAsync("update-desktop-database", applicationsDir, cancellationToken);
    }
    
    public async Task UnregisterAsync(IEnumerable<string> extensions, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        
        string applicationsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        
        string desktopPath = Path.Combine(applicationsDir, desktopFileName);
        
        if (File.Exists(desktopPath))
        {
            File.Delete(desktopPath);
        }
        
        await RunCommandAsync("update-desktop-database", applicationsDir, cancellationToken);
    }
    
    public bool IsDefaultHandler(string extension)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;
        
        string mime = GetMimeType(extension);
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-mime",
                Arguments = $"query default {mime}",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            
            using var process = Process.Start(psi);
            if (process == null) return false;
            
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            
            return output.Trim() == desktopFileName;
        }
        catch
        {
            return false;
        }
    }
    
    private async Task SetDefaultApplicationAsync(string mimeType, CancellationToken cancellationToken)
    {
        await RunCommandAsync("xdg-mime", $"default {desktopFileName} {mimeType}", cancellationToken);
    }
    
    public Task AddContextMenuAsync(CancellationToken cancellationToken)
    {
        // Context menus in Linux are file manager-specific
        // Nautilus, Dolphin, Thunar all have different mechanisms
        return Task.CompletedTask;
    }
    
    public Task RemoveContextMenuAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    
    private static string GetMimeType(string extension)
    {
        string ext = extension.TrimStart('.').ToLowerInvariant();
        
        return ext switch
        {
            "mp4" or "m4v" => "video/mp4",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "wmv" => "video/x-ms-wmv",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            "flv" => "video/x-flv",
            "mpg" or "mpeg" => "video/mpeg",
            "ts" => "video/mp2t",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "m4a" or "aac" => "audio/mp4",
            "wma" => "audio/x-ms-wma",
            "m3u" or "m3u8" => "audio/x-mpegurl",
            _ => $"application/x-{ext}"
        };
    }
    
    private static async Task RunCommandAsync(string command, string args, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
            // Command not available
        }
    }
}

// ============================================================================
// macOS File Associations
// ============================================================================

/// <summary>
/// macOS file associations via Launch Services.
/// </summary>
internal sealed class MacOsFileAssociation : IPlatformFileAssociation
{
    private readonly string applicationPath;
    private readonly string applicationName;
    private readonly string bundleIdentifier;
    
    public MacOsFileAssociation(string applicationPath, string applicationName)
    {
        this.applicationPath = applicationPath;
        this.applicationName = applicationName;
        this.bundleIdentifier = $"com.sharpvideo.{applicationName.ToLowerInvariant()}";
    }
    
    public async Task RegisterAsync(IEnumerable<string> extensions, bool asDefault,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;
        
        // macOS file associations are defined in the app bundle's Info.plist
        // At runtime, we can use LSSetDefaultRoleHandlerForContentType
        
        if (asDefault)
        {
            foreach (string ext in extensions)
            {
                string uti = GetUti(ext);
                await SetDefaultHandlerAsync(uti, cancellationToken);
            }
        }
    }
    
    public Task UnregisterAsync(IEnumerable<string> extensions, CancellationToken cancellationToken)
    {
        // Cannot unregister from Info.plist at runtime
        return Task.CompletedTask;
    }
    
    public bool IsDefaultHandler(string extension)
    {
        // LSCopyDefaultRoleHandlerForContentType
        return false;
    }
    
    private async Task SetDefaultHandlerAsync(string uti, CancellationToken cancellationToken)
    {
        // duti -s com.sharpvideo public.mpeg-4 all
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "duti",
                    Arguments = $"-s {bundleIdentifier} {uti} all",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                process?.WaitForExit();
            }
            catch { }
        }, cancellationToken);
    }
    
    public Task AddContextMenuAsync(CancellationToken cancellationToken)
    {
        // macOS uses Finder Extensions or Services for context menus
        // This requires a separate helper app
        return Task.CompletedTask;
    }
    
    public Task RemoveContextMenuAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    
    private static string GetUti(string extension)
    {
        string ext = extension.TrimStart('.').ToLowerInvariant();
        
        return ext switch
        {
            "mp4" or "m4v" => "public.mpeg-4",
            "mov" => "com.apple.quicktime-movie",
            "avi" => "public.avi",
            "mkv" => "io.matroska.mkv",
            "mp3" => "public.mp3",
            "flac" => "org.xiph.flac",
            "wav" => "com.microsoft.waveform-audio",
            "aac" or "m4a" => "public.aac-audio",
            _ => $"public.{ext}"
        };
    }
}

// ============================================================================
// Null Implementation
// ============================================================================

internal sealed class NullFileAssociation : IPlatformFileAssociation
{
    public Task RegisterAsync(IEnumerable<string> extensions, bool asDefault, CancellationToken cancellationToken) 
        => Task.CompletedTask;
    public Task UnregisterAsync(IEnumerable<string> extensions, CancellationToken cancellationToken) 
        => Task.CompletedTask;
    public bool IsDefaultHandler(string extension) => false;
    public Task AddContextMenuAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RemoveContextMenuAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

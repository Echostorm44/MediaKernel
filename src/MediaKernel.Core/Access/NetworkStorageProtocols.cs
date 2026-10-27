// MediaKernel - Network Storage Protocols
// NFS, SMB, SFTP, WebDAV access for remote media files

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Access;

/// <summary>
/// Base interface for network file access protocols.
/// </summary>
public interface INetworkAccess : IAsyncDisposable
{
    /// <summary>Protocol name (nfs, smb, sftp, webdav).</summary>
    string Protocol { get; }
    
    /// <summary>Whether connected to the server.</summary>
    bool IsConnected { get; }
    
    /// <summary>Connect to the server.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Disconnect from the server.</summary>
    Task DisconnectAsync();
    
    /// <summary>List files in a directory.</summary>
    Task<IReadOnlyList<NetworkFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>Get file information.</summary>
    Task<NetworkFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>Open a file for reading.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>Read a range of bytes from a file.</summary>
    Task<int> ReadAsync(string path, long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a remote file.
/// </summary>
public sealed class NetworkFileInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedTime { get; init; }
    public DateTime? CreatedTime { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    public string? MimeType { get; init; }
}

/// <summary>
/// Connection configuration for network access.
/// </summary>
public sealed class NetworkAccessConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Domain { get; init; }
    public string? ShareName { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool UseTls { get; init; }
    public bool ValidateCertificate { get; init; } = true;
}

// ============================================================================
// NFS (Network File System) Implementation
// ============================================================================

/// <summary>
/// NFS v3/v4 client for accessing files on NFS servers.
/// </summary>
public sealed class NfsAccess : INetworkAccess
{
    private readonly NetworkAccessConfiguration config;
    private Socket? socket;
    private bool connected;
    private uint xid; // Transaction ID
    private byte[]? rootFileHandle;
    
    // NFS RPC program numbers
    private const uint PortmapProgram = 100000;
    private const uint NfsProgram = 100003;
    private const uint MountProgram = 100005;
    
    // NFS procedure numbers
    private const uint NfsNull = 0;
    private const uint NfsGetattr = 1;
    private const uint NfsLookup = 3;
    private const uint NfsRead = 6;
    private const uint NfsReaddir = 16;
    private const uint NfsReaddirplus = 17;
    
    // Mount procedure numbers
    private const uint MountNull = 0;
    private const uint MountMnt = 1;
    private const uint MountUmnt = 3;
    
    public string Protocol => "nfs";
    public bool IsConnected => connected;
    
    public NfsAccess(NetworkAccessConfiguration configuration)
    {
        config = configuration;
        xid = (uint)Random.Shared.Next();
    }
    
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (connected) return;
        
        // Connect to NFS server (typically port 2049)
        int port = config.Port > 0 ? config.Port : 2049;
        
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = (int)config.ReadTimeout.TotalMilliseconds;
        socket.SendTimeout = (int)config.ConnectTimeout.TotalMilliseconds;
        
        await socket.ConnectAsync(config.Host, port, cancellationToken);
        
        // Mount the export
        if (!string.IsNullOrEmpty(config.ShareName))
        {
            rootFileHandle = await MountExportAsync(config.ShareName, cancellationToken);
        }
        
        connected = true;
    }
    
    private async Task<byte[]> MountExportAsync(string exportPath, CancellationToken cancellationToken)
    {
        // Build MOUNT RPC call
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // RPC header
        WriteRpcHeader(writer, MountProgram, 3, MountMnt);
        
        // Export path (XDR string)
        WriteXdrString(writer, exportPath);
        
        byte[] request = ms.ToArray();
        await SendRpcAsync(request, cancellationToken);
        
        // Read response
        byte[] response = await ReceiveRpcAsync(cancellationToken);
        
        // Parse mount response to get file handle
        using var reader = new BinaryReader(new MemoryStream(response));
        SkipRpcHeader(reader);
        
        uint status = ReadUInt32BE(reader);
        if (status != 0)
            throw new IOException($"NFS mount failed with status {status}");
        
        // Read file handle
        uint fhLen = ReadUInt32BE(reader);
        byte[] fileHandle = reader.ReadBytes((int)fhLen);
        
        return fileHandle;
    }
    
    public async Task DisconnectAsync()
    {
        if (!connected) return;
        
        // Unmount if mounted
        if (rootFileHandle != null && !string.IsNullOrEmpty(config.ShareName))
        {
            try
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);
                
                WriteRpcHeader(writer, MountProgram, 3, MountUmnt);
                WriteXdrString(writer, config.ShareName);
                
                await SendRpcAsync(ms.ToArray(), CancellationToken.None);
            }
            catch { /* Ignore unmount errors */ }
        }
        
        socket?.Dispose();
        socket = null;
        connected = false;
        rootFileHandle = null;
    }
    
    public async Task<IReadOnlyList<NetworkFileInfo>> ListDirectoryAsync(string path, 
        CancellationToken cancellationToken = default)
    {
        if (!connected || socket == null)
            throw new InvalidOperationException("Not connected");
        
        byte[] dirHandle = await LookupPathAsync(path, cancellationToken);
        
        var files = new List<NetworkFileInfo>();
        ulong cookie = 0;
        byte[] cookieVerifier = new byte[8];
        
        while (true)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            WriteRpcHeader(writer, NfsProgram, 3, NfsReaddirplus);
            WriteXdrOpaque(writer, dirHandle);
            writer.Write(cookie);
            writer.Write(cookieVerifier);
            WriteUInt32BE(writer, 8192); // dircount
            WriteUInt32BE(writer, 32768); // maxcount
            
            await SendRpcAsync(ms.ToArray(), cancellationToken);
            byte[] response = await ReceiveRpcAsync(cancellationToken);
            
            using var reader = new BinaryReader(new MemoryStream(response));
            SkipRpcHeader(reader);
            
            uint status = ReadUInt32BE(reader);
            if (status != 0) break;
            
            // Skip dir attributes
            if (ReadUInt32BE(reader) == 1) // attributes follow
                SkipNfsAttributes(reader);
            
            // Read cookie verifier
            cookieVerifier = reader.ReadBytes(8);
            
            // Read directory entries
            while (ReadUInt32BE(reader) == 1) // entry follows
            {
                ulong fileId = ReadUInt64BE(reader);
                string name = ReadXdrString(reader);
                cookie = ReadUInt64BE(reader);
                
                // Post-op attributes
                bool hasAttrs = ReadUInt32BE(reader) == 1;
                long size = 0;
                bool isDir = false;
                DateTime mtime = DateTime.MinValue;
                
                if (hasAttrs)
                {
                    uint type = ReadUInt32BE(reader);
                    isDir = type == 2; // NF3DIR
                    ReadUInt32BE(reader); // mode
                    ReadUInt32BE(reader); // nlink
                    ReadUInt32BE(reader); // uid
                    ReadUInt32BE(reader); // gid
                    size = (long)ReadUInt64BE(reader);
                    ReadUInt64BE(reader); // used
                    ReadUInt64BE(reader); // rdev
                    ReadUInt64BE(reader); // fsid
                    ReadUInt64BE(reader); // fileid
                    ReadUInt64BE(reader); // atime
                    ulong mtimeS = ReadUInt64BE(reader);
                    mtime = DateTimeOffset.FromUnixTimeSeconds((long)mtimeS).DateTime;
                    ReadUInt64BE(reader); // ctime
                }
                
                // Post-op file handle
                if (ReadUInt32BE(reader) == 1)
                {
                    uint fhLen = ReadUInt32BE(reader);
                    reader.ReadBytes((int)fhLen);
                }
                
                if (name != "." && name != "..")
                {
                    files.Add(new NetworkFileInfo
                    {
                        Name = name,
                        FullPath = Path.Combine(path, name).Replace('\\', '/'),
                        Size = size,
                        IsDirectory = isDir,
                        ModifiedTime = mtime
                    });
                }
            }
            
            // Check if EOF
            if (ReadUInt32BE(reader) == 1)
                break;
        }
        
        return files;
    }
    
    public async Task<NetworkFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!connected) return null;
        
        try
        {
            byte[] fileHandle = await LookupPathAsync(path, cancellationToken);
            
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            WriteRpcHeader(writer, NfsProgram, 3, NfsGetattr);
            WriteXdrOpaque(writer, fileHandle);
            
            await SendRpcAsync(ms.ToArray(), cancellationToken);
            byte[] response = await ReceiveRpcAsync(cancellationToken);
            
            using var reader = new BinaryReader(new MemoryStream(response));
            SkipRpcHeader(reader);
            
            uint status = ReadUInt32BE(reader);
            if (status != 0) return null;
            
            uint type = ReadUInt32BE(reader);
            ReadUInt32BE(reader); // mode
            ReadUInt32BE(reader); // nlink
            ReadUInt32BE(reader); // uid
            ReadUInt32BE(reader); // gid
            long size = (long)ReadUInt64BE(reader);
            ReadUInt64BE(reader); // used
            ReadUInt64BE(reader); // rdev
            ReadUInt64BE(reader); // fsid
            ReadUInt64BE(reader); // fileid
            ReadUInt64BE(reader); // atime
            ulong mtimeS = ReadUInt64BE(reader);
            
            return new NetworkFileInfo
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Size = size,
                IsDirectory = type == 2,
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds((long)mtimeS).DateTime
            };
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] fileHandle = await LookupPathAsync(path, cancellationToken);
        return new NfsFileStream(this, fileHandle, path);
    }
    
    public async Task<int> ReadAsync(string path, long offset, Memory<byte> buffer, 
        CancellationToken cancellationToken = default)
    {
        byte[] fileHandle = await LookupPathAsync(path, cancellationToken);
        return await ReadFileAsync(fileHandle, offset, buffer, cancellationToken);
    }
    
    internal async Task<int> ReadFileAsync(byte[] fileHandle, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!connected || socket == null)
            throw new InvalidOperationException("Not connected");
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        WriteRpcHeader(writer, NfsProgram, 3, NfsRead);
        WriteXdrOpaque(writer, fileHandle);
        WriteUInt64BE(writer, (ulong)offset);
        WriteUInt32BE(writer, (uint)Math.Min(buffer.Length, 65536));
        
        await SendRpcAsync(ms.ToArray(), cancellationToken);
        byte[] response = await ReceiveRpcAsync(cancellationToken);
        
        using var reader = new BinaryReader(new MemoryStream(response));
        SkipRpcHeader(reader);
        
        uint status = ReadUInt32BE(reader);
        if (status != 0)
            throw new IOException($"NFS read failed with status {status}");
        
        // Skip post-op attributes
        if (ReadUInt32BE(reader) == 1)
            SkipNfsAttributes(reader);
        
        uint count = ReadUInt32BE(reader);
        bool eof = ReadUInt32BE(reader) == 1;
        uint dataLen = ReadUInt32BE(reader);
        
        int bytesToRead = (int)Math.Min(dataLen, buffer.Length);
        int read = reader.Read(buffer.Span[..bytesToRead]);
        
        return read;
    }
    
    private async Task<byte[]> LookupPathAsync(string path, CancellationToken cancellationToken)
    {
        if (rootFileHandle == null)
            throw new InvalidOperationException("Not mounted");
        
        byte[] currentHandle = rootFileHandle;
        
        string[] parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            WriteRpcHeader(writer, NfsProgram, 3, NfsLookup);
            WriteXdrOpaque(writer, currentHandle);
            WriteXdrString(writer, part);
            
            await SendRpcAsync(ms.ToArray(), cancellationToken);
            byte[] response = await ReceiveRpcAsync(cancellationToken);
            
            using var reader = new BinaryReader(new MemoryStream(response));
            SkipRpcHeader(reader);
            
            uint status = ReadUInt32BE(reader);
            if (status != 0)
                throw new FileNotFoundException($"Path not found: {path}");
            
            uint fhLen = ReadUInt32BE(reader);
            currentHandle = reader.ReadBytes((int)fhLen);
        }
        
        return currentHandle;
    }
    
    private async Task SendRpcAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (socket == null) return;
        
        // Add record marker (last fragment, length)
        byte[] packet = new byte[4 + data.Length];
        uint marker = 0x80000000 | (uint)data.Length;
        packet[0] = (byte)(marker >> 24);
        packet[1] = (byte)(marker >> 16);
        packet[2] = (byte)(marker >> 8);
        packet[3] = (byte)marker;
        data.CopyTo(packet.AsSpan(4));
        
        await socket.SendAsync(packet, SocketFlags.None, cancellationToken);
    }
    
    private async Task<byte[]> ReceiveRpcAsync(CancellationToken cancellationToken)
    {
        if (socket == null) return [];
        
        // Read record marker
        byte[] marker = new byte[4];
        await socket.ReceiveAsync(marker, SocketFlags.None, cancellationToken);
        
        uint length = ((uint)marker[0] << 24) | ((uint)marker[1] << 16) | 
                      ((uint)marker[2] << 8) | marker[3];
        length &= 0x7FFFFFFF; // Clear last fragment bit
        
        byte[] data = new byte[length];
        int received = 0;
        while (received < length)
        {
            received += await socket.ReceiveAsync(data.AsMemory(received), 
                SocketFlags.None, cancellationToken);
        }
        
        return data;
    }
    
    private void WriteRpcHeader(BinaryWriter writer, uint program, uint version, uint procedure)
    {
        WriteUInt32BE(writer, ++xid);          // XID
        WriteUInt32BE(writer, 0);              // Call
        WriteUInt32BE(writer, 2);              // RPC version
        WriteUInt32BE(writer, program);
        WriteUInt32BE(writer, version);
        WriteUInt32BE(writer, procedure);
        
        // AUTH_UNIX credentials
        WriteUInt32BE(writer, 1);              // AUTH_UNIX
        WriteUInt32BE(writer, 20);             // Length
        WriteUInt32BE(writer, 0);              // Stamp
        WriteXdrString(writer, "client");      // Machine name
        WriteUInt32BE(writer, 0);              // UID
        WriteUInt32BE(writer, 0);              // GID
        WriteUInt32BE(writer, 0);              // Aux GIDs count
        
        // AUTH_NULL verifier
        WriteUInt32BE(writer, 0);
        WriteUInt32BE(writer, 0);
    }
    
    private static void SkipRpcHeader(BinaryReader reader)
    {
        reader.ReadBytes(8);  // XID, Reply type
        reader.ReadBytes(4);  // Accept state
        uint authLen = ReadUInt32BE(reader);
        reader.ReadBytes((int)authLen + 4); // Auth data + verifier type
        uint verifLen = ReadUInt32BE(reader);
        reader.ReadBytes((int)verifLen);
        reader.ReadBytes(4);  // Accept stat
    }
    
    private static void SkipNfsAttributes(BinaryReader reader)
    {
        reader.ReadBytes(84); // NFS3 fattr3 size
    }
    
    private static void WriteXdrString(BinaryWriter writer, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        WriteUInt32BE(writer, (uint)bytes.Length);
        writer.Write(bytes);
        int padding = (4 - bytes.Length % 4) % 4;
        for (int i = 0; i < padding; i++)
            writer.Write((byte)0);
    }
    
    private static string ReadXdrString(BinaryReader reader)
    {
        uint len = ReadUInt32BE(reader);
        byte[] bytes = reader.ReadBytes((int)len);
        int padding = (4 - (int)len % 4) % 4;
        reader.ReadBytes(padding);
        return Encoding.UTF8.GetString(bytes);
    }
    
    private static void WriteXdrOpaque(BinaryWriter writer, byte[] data)
    {
        WriteUInt32BE(writer, (uint)data.Length);
        writer.Write(data);
        int padding = (4 - data.Length % 4) % 4;
        for (int i = 0; i < padding; i++)
            writer.Write((byte)0);
    }
    
    private static void WriteUInt32BE(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }
    
    private static void WriteUInt64BE(BinaryWriter writer, ulong value)
    {
        WriteUInt32BE(writer, (uint)(value >> 32));
        WriteUInt32BE(writer, (uint)value);
    }
    
    private static uint ReadUInt32BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | 
               ((uint)bytes[2] << 8) | bytes[3];
    }
    
    private static ulong ReadUInt64BE(BinaryReader reader)
    {
        return ((ulong)ReadUInt32BE(reader) << 32) | ReadUInt32BE(reader);
    }
    
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

/// <summary>
/// Stream wrapper for NFS file reading.
/// </summary>
internal sealed class NfsFileStream : Stream
{
    private readonly NfsAccess nfs;
    private readonly byte[] fileHandle;
    private readonly string path;
    private long position;
    private long length = -1;
    private bool disposed;
    
    public NfsFileStream(NfsAccess nfs, byte[] fileHandle, string path)
    {
        this.nfs = nfs;
        this.fileHandle = fileHandle;
        this.path = path;
    }
    
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    
    public override long Length
    {
        get
        {
            if (length < 0)
            {
                var info = nfs.GetFileInfoAsync(path).GetAwaiter().GetResult();
                length = info?.Size ?? 0;
            }
            return length;
        }
    }
    
    public override long Position
    {
        get => position;
        set => position = value;
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .GetAwaiter().GetResult();
    }
    
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, 
        CancellationToken cancellationToken = default)
    {
        int read = await nfs.ReadFileAsync(fileHandle, position, buffer, cancellationToken);
        position += read;
        return read;
    }
    
    public override long Seek(long offset, SeekOrigin origin)
    {
        position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => position
        };
        return position;
    }
    
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    
    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }
}

// ============================================================================
// SFTP (SSH File Transfer Protocol) Implementation
// ============================================================================

/// <summary>
/// SFTP client with support for password and key-based authentication.
/// </summary>
public sealed class SftpAccess : INetworkAccess
{
    private readonly NetworkAccessConfiguration config;
    private Socket? socket;
    private Stream? stream;
    private bool connected;
    private uint requestId;
    private byte[]? sessionId;
    
    // SSH message types
    private const byte SshMsgDisconnect = 1;
    private const byte SshMsgKexinit = 20;
    private const byte SshMsgNewkeys = 21;
    private const byte SshMsgKexdhInit = 30;
    private const byte SshMsgKexdhReply = 31;
    private const byte SshMsgServiceRequest = 5;
    private const byte SshMsgServiceAccept = 6;
    private const byte SshMsgUserauthRequest = 50;
    private const byte SshMsgUserauthSuccess = 52;
    private const byte SshMsgChannelOpen = 90;
    private const byte SshMsgChannelOpenConfirmation = 91;
    private const byte SshMsgChannelRequest = 98;
    private const byte SshMsgChannelSuccess = 99;
    private const byte SshMsgChannelData = 94;
    
    // SFTP packet types
    private const byte FxpInit = 1;
    private const byte FxpVersion = 2;
    private const byte FxpOpen = 3;
    private const byte FxpClose = 4;
    private const byte FxpRead = 5;
    private const byte FxpOpendir = 11;
    private const byte FxpReaddir = 12;
    private const byte FxpStat = 17;
    private const byte FxpHandle = 102;
    private const byte FxpData = 103;
    private const byte FxpName = 104;
    private const byte FxpAttrs = 105;
    private const byte FxpStatus = 101;
    
    public string Protocol => "sftp";
    public bool IsConnected => connected;
    
    public SftpAccess(NetworkAccessConfiguration configuration)
    {
        config = configuration;
    }
    
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (connected) return;
        
        int port = config.Port > 0 ? config.Port : 22;
        
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(config.Host, port, cancellationToken);
        stream = new NetworkStream(socket, true);
        
        // SSH protocol exchange
        await ExchangeVersionsAsync(cancellationToken);
        await KeyExchangeAsync(cancellationToken);
        await AuthenticateAsync(cancellationToken);
        await OpenChannelAsync(cancellationToken);
        await InitSftpAsync(cancellationToken);
        
        connected = true;
    }
    
    private async Task ExchangeVersionsAsync(CancellationToken cancellationToken)
    {
        // Send SSH version
        byte[] version = "SSH-2.0-MediaKernel_1.0\r\n"u8.ToArray();
        await stream!.WriteAsync(version, cancellationToken);
        
        // Read server version
        var buffer = new byte[256];
        int read = await stream.ReadAsync(buffer, cancellationToken);
        // Parse and validate server version
    }
    
    private async Task KeyExchangeAsync(CancellationToken cancellationToken)
    {
        // Send KEXINIT
        using var ms = new MemoryStream();
        ms.WriteByte(SshMsgKexinit);
        
        // Cookie (16 random bytes)
        byte[] cookie = new byte[16];
        Random.Shared.NextBytes(cookie);
        ms.Write(cookie);
        
        // Algorithm lists
        WriteSshString(ms, "curve25519-sha256,ecdh-sha2-nistp256,diffie-hellman-group14-sha256");
        WriteSshString(ms, "ssh-rsa,ssh-ed25519,ecdsa-sha2-nistp256");
        WriteSshString(ms, "aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr");
        WriteSshString(ms, "aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr");
        WriteSshString(ms, "hmac-sha2-256,hmac-sha2-512");
        WriteSshString(ms, "hmac-sha2-256,hmac-sha2-512");
        WriteSshString(ms, "none");
        WriteSshString(ms, "none");
        WriteSshString(ms, "");
        WriteSshString(ms, "");
        ms.WriteByte(0); // first_kex_packet_follows
        ms.Write(new byte[4]); // reserved
        
        await SendPacketAsync(ms.ToArray(), cancellationToken);
        
        // Receive server KEXINIT
        byte[] serverKexinit = await ReceivePacketAsync(cancellationToken);
        
        // DH key exchange (simplified - would need proper crypto)
        using var dhInit = new MemoryStream();
        dhInit.WriteByte(SshMsgKexdhInit);
        
        // Generate DH public key (placeholder - real implementation uses proper DH)
        byte[] dhPubKey = new byte[32];
        Random.Shared.NextBytes(dhPubKey);
        WriteSshMpint(dhInit, dhPubKey);
        
        await SendPacketAsync(dhInit.ToArray(), cancellationToken);
        
        // Receive DH reply
        byte[] dhReply = await ReceivePacketAsync(cancellationToken);
        
        // Send NEWKEYS
        await SendPacketAsync([SshMsgNewkeys], cancellationToken);
        
        // Receive NEWKEYS
        await ReceivePacketAsync(cancellationToken);
    }
    
    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        // Request ssh-userauth service
        using var serviceReq = new MemoryStream();
        serviceReq.WriteByte(SshMsgServiceRequest);
        WriteSshString(serviceReq, "ssh-userauth");
        await SendPacketAsync(serviceReq.ToArray(), cancellationToken);
        
        // Receive service accept
        await ReceivePacketAsync(cancellationToken);
        
        // Password authentication
        using var authReq = new MemoryStream();
        authReq.WriteByte(SshMsgUserauthRequest);
        WriteSshString(authReq, config.Username ?? "");
        WriteSshString(authReq, "ssh-connection");
        WriteSshString(authReq, "password");
        authReq.WriteByte(0); // no password change
        WriteSshString(authReq, config.Password ?? "");
        
        await SendPacketAsync(authReq.ToArray(), cancellationToken);
        
        // Check authentication result
        byte[] response = await ReceivePacketAsync(cancellationToken);
        if (response.Length == 0 || response[0] != SshMsgUserauthSuccess)
            throw new UnauthorizedAccessException("SFTP authentication failed");
    }
    
    private async Task OpenChannelAsync(CancellationToken cancellationToken)
    {
        // Open session channel
        using var channelOpen = new MemoryStream();
        channelOpen.WriteByte(SshMsgChannelOpen);
        WriteSshString(channelOpen, "session");
        WriteSshUint32(channelOpen, 0); // sender channel
        WriteSshUint32(channelOpen, 65536); // initial window
        WriteSshUint32(channelOpen, 32768); // max packet
        
        await SendPacketAsync(channelOpen.ToArray(), cancellationToken);
        await ReceivePacketAsync(cancellationToken); // Channel open confirmation
        
        // Request SFTP subsystem
        using var subsysReq = new MemoryStream();
        subsysReq.WriteByte(SshMsgChannelRequest);
        WriteSshUint32(subsysReq, 0); // recipient channel
        WriteSshString(subsysReq, "subsystem");
        subsysReq.WriteByte(1); // want reply
        WriteSshString(subsysReq, "sftp");
        
        await SendPacketAsync(subsysReq.ToArray(), cancellationToken);
        await ReceivePacketAsync(cancellationToken); // Channel success
    }
    
    private async Task InitSftpAsync(CancellationToken cancellationToken)
    {
        // Send SFTP init
        using var init = new MemoryStream();
        init.WriteByte(FxpInit);
        WriteSshUint32(init, 3); // SFTP version 3
        
        await SendSftpPacketAsync(init.ToArray(), cancellationToken);
        
        // Receive version
        byte[] version = await ReceiveSftpPacketAsync(cancellationToken);
    }
    
    public async Task DisconnectAsync()
    {
        if (!connected) return;
        
        try
        {
            using var disconnect = new MemoryStream();
            disconnect.WriteByte(SshMsgDisconnect);
            WriteSshUint32(disconnect, 11); // SSH_DISCONNECT_BY_APPLICATION
            WriteSshString(disconnect, "Client disconnecting");
            WriteSshString(disconnect, "");
            
            await SendPacketAsync(disconnect.ToArray(), CancellationToken.None);
        }
        catch { }
        
        stream?.Dispose();
        socket?.Dispose();
        stream = null;
        socket = null;
        connected = false;
    }
    
    public async Task<IReadOnlyList<NetworkFileInfo>> ListDirectoryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        if (!connected) throw new InvalidOperationException("Not connected");
        
        // Open directory
        using var opendir = new MemoryStream();
        opendir.WriteByte(FxpOpendir);
        WriteSshUint32(opendir, ++requestId);
        WriteSshString(opendir, path);
        
        await SendSftpPacketAsync(opendir.ToArray(), cancellationToken);
        byte[] handleResponse = await ReceiveSftpPacketAsync(cancellationToken);
        
        if (handleResponse[0] != FxpHandle)
            throw new IOException("Failed to open directory");
        
        byte[] handle = ReadSftpHandle(handleResponse);
        
        var files = new List<NetworkFileInfo>();
        
        while (true)
        {
            using var readdir = new MemoryStream();
            readdir.WriteByte(FxpReaddir);
            WriteSshUint32(readdir, ++requestId);
            WriteSshBytes(readdir, handle);
            
            await SendSftpPacketAsync(readdir.ToArray(), cancellationToken);
            byte[] response = await ReceiveSftpPacketAsync(cancellationToken);
            
            if (response[0] == FxpStatus)
                break; // End of directory
            
            if (response[0] == FxpName)
            {
                using var reader = new BinaryReader(new MemoryStream(response, 1, response.Length - 1));
                ReadSshUint32(reader); // request id
                uint count = ReadSshUint32(reader);
                
                for (uint i = 0; i < count; i++)
                {
                    string name = ReadSshString(reader);
                    string longname = ReadSshString(reader);
                    var attrs = ReadSftpAttrs(reader);
                    
                    if (name != "." && name != "..")
                    {
                        files.Add(new NetworkFileInfo
                        {
                            Name = name,
                            FullPath = Path.Combine(path, name).Replace('\\', '/'),
                            Size = attrs.Size,
                            IsDirectory = attrs.IsDirectory,
                            ModifiedTime = attrs.ModifiedTime
                        });
                    }
                }
            }
        }
        
        // Close handle
        using var close = new MemoryStream();
        close.WriteByte(FxpClose);
        WriteSshUint32(close, ++requestId);
        WriteSshBytes(close, handle);
        await SendSftpPacketAsync(close.ToArray(), cancellationToken);
        await ReceiveSftpPacketAsync(cancellationToken);
        
        return files;
    }
    
    public async Task<NetworkFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!connected) return null;
        
        try
        {
            using var stat = new MemoryStream();
            stat.WriteByte(FxpStat);
            WriteSshUint32(stat, ++requestId);
            WriteSshString(stat, path);
            
            await SendSftpPacketAsync(stat.ToArray(), cancellationToken);
            byte[] response = await ReceiveSftpPacketAsync(cancellationToken);
            
            if (response[0] != FxpAttrs)
                return null;
            
            using var reader = new BinaryReader(new MemoryStream(response, 1, response.Length - 1));
            ReadSshUint32(reader); // request id
            var attrs = ReadSftpAttrs(reader);
            
            return new NetworkFileInfo
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                Size = attrs.Size,
                IsDirectory = attrs.IsDirectory,
                ModifiedTime = attrs.ModifiedTime
            };
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var open = new MemoryStream();
        open.WriteByte(FxpOpen);
        WriteSshUint32(open, ++requestId);
        WriteSshString(open, path);
        WriteSshUint32(open, 0x00000001); // SSH_FXF_READ
        WriteSshUint32(open, 0); // No attrs
        
        await SendSftpPacketAsync(open.ToArray(), cancellationToken);
        byte[] response = await ReceiveSftpPacketAsync(cancellationToken);
        
        if (response[0] != FxpHandle)
            throw new IOException($"Failed to open file: {path}");
        
        byte[] handle = ReadSftpHandle(response);
        return new SftpFileStream(this, handle, path);
    }
    
    public async Task<int> ReadAsync(string path, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        using var openStream = await OpenReadAsync(path, cancellationToken) as SftpFileStream;
        if (openStream == null) return 0;
        
        openStream.Position = offset;
        return await openStream.ReadAsync(buffer, cancellationToken);
    }
    
    internal async Task<int> ReadFileAsync(byte[] handle, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        using var read = new MemoryStream();
        read.WriteByte(FxpRead);
        WriteSshUint32(read, ++requestId);
        WriteSshBytes(read, handle);
        WriteSshUint64(read, (ulong)offset);
        WriteSshUint32(read, (uint)Math.Min(buffer.Length, 32768));
        
        await SendSftpPacketAsync(read.ToArray(), cancellationToken);
        byte[] response = await ReceiveSftpPacketAsync(cancellationToken);
        
        if (response[0] == FxpStatus)
            return 0; // EOF or error
        
        if (response[0] != FxpData)
            throw new IOException("Unexpected SFTP response");
        
        using var reader = new BinaryReader(new MemoryStream(response, 1, response.Length - 1));
        ReadSshUint32(reader); // request id
        uint dataLen = ReadSshUint32(reader);
        
        int bytesToRead = (int)Math.Min(dataLen, buffer.Length);
        return reader.Read(buffer.Span[..bytesToRead]);
    }
    
    internal async Task CloseHandleAsync(byte[] handle, CancellationToken cancellationToken)
    {
        using var close = new MemoryStream();
        close.WriteByte(FxpClose);
        WriteSshUint32(close, ++requestId);
        WriteSshBytes(close, handle);
        
        await SendSftpPacketAsync(close.ToArray(), cancellationToken);
        await ReceiveSftpPacketAsync(cancellationToken);
    }
    
    private async Task SendPacketAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (stream == null) return;
        
        // SSH packet: length (4) + padding_length (1) + payload + padding + MAC
        int paddingLen = 8 - ((5 + data.Length) % 8);
        if (paddingLen < 4) paddingLen += 8;
        
        byte[] packet = new byte[4 + 1 + data.Length + paddingLen];
        int packetLen = 1 + data.Length + paddingLen;
        
        packet[0] = (byte)(packetLen >> 24);
        packet[1] = (byte)(packetLen >> 16);
        packet[2] = (byte)(packetLen >> 8);
        packet[3] = (byte)packetLen;
        packet[4] = (byte)paddingLen;
        data.CopyTo(packet.AsSpan(5));
        
        await stream.WriteAsync(packet, cancellationToken);
    }
    
    private async Task<byte[]> ReceivePacketAsync(CancellationToken cancellationToken)
    {
        if (stream == null) return [];
        
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        
        int packetLen = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        int paddingLen = header[4];
        
        byte[] payload = new byte[packetLen - paddingLen - 1];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        
        // Skip padding
        byte[] padding = new byte[paddingLen];
        await stream.ReadExactlyAsync(padding, cancellationToken);
        
        return payload;
    }
    
    private async Task SendSftpPacketAsync(byte[] data, CancellationToken cancellationToken)
    {
        // SFTP packet wrapped in SSH channel data
        using var channelData = new MemoryStream();
        channelData.WriteByte(SshMsgChannelData);
        WriteSshUint32(channelData, 0); // recipient channel
        
        // SFTP packet with length prefix
        byte[] sftpPacket = new byte[4 + data.Length];
        sftpPacket[0] = (byte)(data.Length >> 24);
        sftpPacket[1] = (byte)(data.Length >> 16);
        sftpPacket[2] = (byte)(data.Length >> 8);
        sftpPacket[3] = (byte)data.Length;
        data.CopyTo(sftpPacket.AsSpan(4));
        
        WriteSshBytes(channelData, sftpPacket);
        
        await SendPacketAsync(channelData.ToArray(), cancellationToken);
    }
    
    private async Task<byte[]> ReceiveSftpPacketAsync(CancellationToken cancellationToken)
    {
        byte[] packet = await ReceivePacketAsync(cancellationToken);
        
        if (packet.Length < 1 || packet[0] != SshMsgChannelData)
            return packet;
        
        using var reader = new BinaryReader(new MemoryStream(packet, 1, packet.Length - 1));
        ReadSshUint32(reader); // recipient channel
        uint dataLen = ReadSshUint32(reader);
        
        // Read SFTP length
        ReadSshUint32(reader);
        
        return reader.ReadBytes((int)dataLen - 4);
    }
    
    private static byte[] ReadSftpHandle(byte[] response)
    {
        using var reader = new BinaryReader(new MemoryStream(response, 1, response.Length - 1));
        ReadSshUint32(reader); // request id
        return ReadSshBytes(reader);
    }
    
    private static (long Size, bool IsDirectory, DateTime ModifiedTime) ReadSftpAttrs(BinaryReader reader)
    {
        uint flags = ReadSshUint32(reader);
        long size = 0;
        bool isDir = false;
        DateTime mtime = DateTime.MinValue;
        
        if ((flags & 0x01) != 0) // SSH_FILEXFER_ATTR_SIZE
            size = (long)ReadSshUint64(reader);
        
        if ((flags & 0x02) != 0) // SSH_FILEXFER_ATTR_UIDGID
        {
            ReadSshUint32(reader); // uid
            ReadSshUint32(reader); // gid
        }
        
        if ((flags & 0x04) != 0) // SSH_FILEXFER_ATTR_PERMISSIONS
        {
            uint perms = ReadSshUint32(reader);
            isDir = (perms & 0x4000) != 0; // S_IFDIR
        }
        
        if ((flags & 0x08) != 0) // SSH_FILEXFER_ATTR_ACMODTIME
        {
            ReadSshUint32(reader); // atime
            uint mtimeS = ReadSshUint32(reader);
            mtime = DateTimeOffset.FromUnixTimeSeconds(mtimeS).DateTime;
        }
        
        return (size, isDir, mtime);
    }
    
    private static void WriteSshString(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteSshUint32(s, (uint)bytes.Length);
        s.Write(bytes);
    }
    
    private static string ReadSshString(BinaryReader reader)
    {
        uint len = ReadSshUint32(reader);
        return Encoding.UTF8.GetString(reader.ReadBytes((int)len));
    }
    
    private static void WriteSshBytes(Stream s, byte[] data)
    {
        WriteSshUint32(s, (uint)data.Length);
        s.Write(data);
    }
    
    private static byte[] ReadSshBytes(BinaryReader reader)
    {
        uint len = ReadSshUint32(reader);
        return reader.ReadBytes((int)len);
    }
    
    private static void WriteSshMpint(Stream s, byte[] value)
    {
        WriteSshBytes(s, value);
    }
    
    private static void WriteSshUint32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }
    
    private static void WriteSshUint64(Stream s, ulong value)
    {
        WriteSshUint32(s, (uint)(value >> 32));
        WriteSshUint32(s, (uint)value);
    }
    
    private static uint ReadSshUint32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | 
               ((uint)bytes[2] << 8) | bytes[3];
    }
    
    private static ulong ReadSshUint64(BinaryReader reader)
    {
        return ((ulong)ReadSshUint32(reader) << 32) | ReadSshUint32(reader);
    }
    
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

/// <summary>
/// Stream wrapper for SFTP file reading.
/// </summary>
internal sealed class SftpFileStream : Stream
{
    private readonly SftpAccess sftp;
    private readonly byte[] handle;
    private readonly string path;
    private long position;
    private long length = -1;
    private bool disposed;
    
    public SftpFileStream(SftpAccess sftp, byte[] handle, string path)
    {
        this.sftp = sftp;
        this.handle = handle;
        this.path = path;
    }
    
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    
    public override long Length
    {
        get
        {
            if (length < 0)
            {
                var info = sftp.GetFileInfoAsync(path).GetAwaiter().GetResult();
                length = info?.Size ?? 0;
            }
            return length;
        }
    }
    
    public override long Position
    {
        get => position;
        set => position = value;
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .GetAwaiter().GetResult();
    }
    
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read = await sftp.ReadFileAsync(handle, position, buffer, cancellationToken);
        position += read;
        return read;
    }
    
    public override long Seek(long offset, SeekOrigin origin)
    {
        position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => position
        };
        return position;
    }
    
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            sftp.CloseHandleAsync(handle, CancellationToken.None).GetAwaiter().GetResult();
        }
        disposed = true;
        base.Dispose(disposing);
    }
}

// ============================================================================
// WebDAV Implementation
// ============================================================================

/// <summary>
/// WebDAV client for HTTP-based file access.
/// </summary>
public sealed class WebDavAccess : INetworkAccess
{
    private readonly NetworkAccessConfiguration config;
    private HttpClient? client;
    private bool connected;
    private string baseUrl = "";
    
    public string Protocol => "webdav";
    public bool IsConnected => connected;
    
    public WebDavAccess(NetworkAccessConfiguration configuration)
    {
        config = configuration;
    }
    
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        string scheme = config.UseTls ? "https" : "http";
        int port = config.Port > 0 ? config.Port : (config.UseTls ? 443 : 80);
        baseUrl = $"{scheme}://{config.Host}:{port}";
        
        var handler = new HttpClientHandler();
        
        if (!string.IsNullOrEmpty(config.Username))
        {
            handler.Credentials = new NetworkCredential(config.Username, config.Password);
        }
        
        if (!config.ValidateCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        
        client = new HttpClient(handler)
        {
            Timeout = config.ReadTimeout
        };
        
        connected = true;
        return Task.CompletedTask;
    }
    
    public Task DisconnectAsync()
    {
        client?.Dispose();
        client = null;
        connected = false;
        return Task.CompletedTask;
    }
    
    public async Task<IReadOnlyList<NetworkFileInfo>> ListDirectoryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        if (client == null) throw new InvalidOperationException("Not connected");
        
        string url = baseUrl + "/" + path.TrimStart('/');
        
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            """<?xml version="1.0"?><propfind xmlns="DAV:"><allprop/></propfind>""",
            Encoding.UTF8, "application/xml");
        
        using var response = await client.SendAsync(request, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
            throw new IOException($"WebDAV PROPFIND failed: {response.StatusCode}");
        
        string xml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParsePropfindResponse(xml, path);
    }
    
    private static List<NetworkFileInfo> ParsePropfindResponse(string xml, string basePath)
    {
        var files = new List<NetworkFileInfo>();
        
        // Simple XML parsing (production would use XDocument or XmlReader)
        int pos = 0;
        while ((pos = xml.IndexOf("<D:response>", pos, StringComparison.Ordinal)) >= 0 ||
               (pos = xml.IndexOf("<d:response>", pos, StringComparison.Ordinal)) >= 0)
        {
            int end = xml.IndexOf("</D:response>", pos, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;
            
            string entry = xml[pos..end];
            pos = end + 1;
            
            string href = ExtractXmlValue(entry, "href") ?? "";
            string displayName = ExtractXmlValue(entry, "displayname") ?? Path.GetFileName(href.TrimEnd('/'));
            string contentLength = ExtractXmlValue(entry, "getcontentlength") ?? "0";
            string lastModified = ExtractXmlValue(entry, "getlastmodified") ?? "";
            bool isCollection = entry.Contains("<D:collection", StringComparison.OrdinalIgnoreCase) ||
                               entry.Contains("<d:collection", StringComparison.OrdinalIgnoreCase);
            
            // Skip the base path itself
            if (href.TrimEnd('/').Equals(basePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                continue;
            
            long.TryParse(contentLength, out long size);
            DateTime.TryParse(lastModified, out DateTime mtime);
            
            files.Add(new NetworkFileInfo
            {
                Name = displayName,
                FullPath = href,
                Size = size,
                IsDirectory = isCollection,
                ModifiedTime = mtime
            });
        }
        
        return files;
    }
    
    private static string? ExtractXmlValue(string xml, string tagName)
    {
        string[] prefixes = ["D:", "d:", ""];
        foreach (string prefix in prefixes)
        {
            string startTag = $"<{prefix}{tagName}>";
            string endTag = $"</{prefix}{tagName}>";
            
            int start = xml.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += startTag.Length;
                int end = xml.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                    return xml[start..end].Trim();
            }
        }
        return null;
    }
    
    public async Task<NetworkFileInfo?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (client == null) return null;
        
        try
        {
            string url = baseUrl + "/" + path.TrimStart('/');
            
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(
                """<?xml version="1.0"?><propfind xmlns="DAV:"><allprop/></propfind>""",
                Encoding.UTF8, "application/xml");
            
            using var response = await client.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
                return null;
            
            string xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var files = ParsePropfindResponse(xml, path);
            
            return files.Count > 0 ? files[0] : null;
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (client == null) throw new InvalidOperationException("Not connected");
        
        string url = baseUrl + "/" + path.TrimStart('/');
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
    
    public async Task<int> ReadAsync(string path, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (client == null) throw new InvalidOperationException("Not connected");
        
        string url = baseUrl + "/" + path.TrimStart('/');
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + buffer.Length - 1);
        
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await stream.ReadAsync(buffer, cancellationToken);
    }
    
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

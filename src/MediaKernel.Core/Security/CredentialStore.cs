// In-memory credential store for streaming authentication
// Stores protocol, host, realm, username, and secret securely

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MediaKernel.Core.Security;

/// <summary>
/// Keys used to identify credentials in the store.
/// </summary>
public enum CredentialKey
{
    Protocol,  // e.g., "rtsp", "http", "ftp"
    Host,      // e.g., "stream.example.com"
    Port,      // e.g., "554"
    Path,      // e.g., "/live/stream1"
    Realm,     // HTTP realm or service name
    Username,  // User identifier
    Authtype   // e.g., "Basic", "Digest", "NTLM"
}

/// <summary>
/// A stored credential entry with optional metadata and secret.
/// </summary>
public sealed class CredentialEntry
{
    private readonly Dictionary<CredentialKey, string> values = new();
    private byte[]? encryptedSecret;
    private string? label;
    
    /// <summary>
    /// Gets or sets a credential key value.
    /// </summary>
    public string? this[CredentialKey key]
    {
        get => values.TryGetValue(key, out var val) ? val : null;
        set
        {
            if (value == null)
                values.Remove(key);
            else
                values[key] = value;
        }
    }
    
    /// <summary>
    /// Human-readable label for this credential.
    /// </summary>
    public string? Label
    {
        get => label;
        set => label = value;
    }
    
    /// <summary>
    /// Protocol (e.g., "rtsp", "http").
    /// </summary>
    public string? Protocol
    {
        get => this[CredentialKey.Protocol];
        set => this[CredentialKey.Protocol] = value;
    }
    
    /// <summary>
    /// Host name or IP address.
    /// </summary>
    public string? Host
    {
        get => this[CredentialKey.Host];
        set => this[CredentialKey.Host] = value;
    }
    
    /// <summary>
    /// Port number as string.
    /// </summary>
    public string? Port
    {
        get => this[CredentialKey.Port];
        set => this[CredentialKey.Port] = value;
    }
    
    /// <summary>
    /// Path component of URL.
    /// </summary>
    public string? Path
    {
        get => this[CredentialKey.Path];
        set => this[CredentialKey.Path] = value;
    }
    
    /// <summary>
    /// Authentication realm.
    /// </summary>
    public string? Realm
    {
        get => this[CredentialKey.Realm];
        set => this[CredentialKey.Realm] = value;
    }
    
    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string? Username
    {
        get => this[CredentialKey.Username];
        set => this[CredentialKey.Username] = value;
    }
    
    /// <summary>
    /// Authentication type (e.g., "Basic", "Digest").
    /// </summary>
    public string? AuthType
    {
        get => this[CredentialKey.Authtype];
        set => this[CredentialKey.Authtype] = value;
    }
    
    /// <summary>
    /// Returns true if a secret is stored.
    /// </summary>
    public bool HasSecret => encryptedSecret != null && encryptedSecret.Length > 0;
    
    /// <summary>
    /// Sets the secret (password) for this credential.
    /// The secret is encrypted in memory using DPAPI on Windows.
    /// </summary>
    public void SetSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
        {
            encryptedSecret = null;
            return;
        }
        
        try
        {
            // Use DPAPI to encrypt in memory (Windows) or just store (other platforms)
            encryptedSecret = ProtectedData.Protect(
                secret.ToArray(),
                null,
                DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException)
        {
            // On non-Windows platforms, store as-is (consider alternative encryption)
            encryptedSecret = secret.ToArray();
        }
    }
    
    /// <summary>
    /// Sets the secret from a string password.
    /// </summary>
    public void SetSecret(string password)
    {
        SetSecret(Encoding.UTF8.GetBytes(password));
    }
    
    /// <summary>
    /// Gets the decrypted secret.
    /// </summary>
    public byte[]? GetSecret()
    {
        if (encryptedSecret == null)
            return null;
            
        try
        {
            return ProtectedData.Unprotect(
                encryptedSecret,
                null,
                DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException)
        {
            // On non-Windows platforms, return as-is
            return (byte[])encryptedSecret.Clone();
        }
    }
    
    /// <summary>
    /// Gets the secret as a string password.
    /// </summary>
    public string? GetSecretAsString()
    {
        var secret = GetSecret();
        return secret == null ? null : Encoding.UTF8.GetString(secret);
    }
    
    /// <summary>
    /// Clears the stored secret from memory.
    /// </summary>
    public void ClearSecret()
    {
        if (encryptedSecret != null)
        {
            CryptographicOperations.ZeroMemory(encryptedSecret);
            encryptedSecret = null;
        }
    }
    
    /// <summary>
    /// Returns all key-value pairs in this entry.
    /// </summary>
    public IReadOnlyDictionary<CredentialKey, string> GetValues() => values;
    
    /// <summary>
    /// Checks if this entry matches the given search criteria.
    /// Null values in the search are treated as wildcards.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<CredentialKey, string?>? searchCriteria)
    {
        if (searchCriteria == null)
            return true;
            
        foreach (var kvp in searchCriteria)
        {
            if (kvp.Value == null)
                continue; // Wildcard
                
            if (!values.TryGetValue(kvp.Key, out var ourValue))
                return false;
                
            if (!string.Equals(ourValue, kvp.Value, StringComparison.Ordinal))
                return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Creates a copy of this entry (without the secret).
    /// </summary>
    public CredentialEntry CloneWithoutSecret()
    {
        var clone = new CredentialEntry { label = label };
        foreach (var kvp in values)
            clone.values[kvp.Key] = kvp.Value;
        return clone;
    }
    
    /// <summary>
    /// Creates a full copy of this entry (including the secret).
    /// </summary>
    public CredentialEntry Clone()
    {
        var clone = CloneWithoutSecret();
        if (encryptedSecret != null)
            clone.encryptedSecret = (byte[])encryptedSecret.Clone();
        return clone;
    }
}

/// <summary>
/// Thread-safe in-memory credential store.
/// Credentials are encrypted in memory and cleared on disposal.
/// </summary>
public sealed class CredentialStore : IDisposable
{
    private readonly List<CredentialEntry> entries = new();
    private readonly object syncLock = new();
    private bool disposed;
    
    /// <summary>
    /// Gets the number of stored credentials.
    /// </summary>
    public int Count
    {
        get
        {
            lock (syncLock)
                return entries.Count;
        }
    }
    
    /// <summary>
    /// Stores or updates a credential.
    /// If a matching entry exists (by all non-null search keys), it is updated.
    /// </summary>
    /// <param name="protocol">Protocol (e.g., "rtsp", "http").</param>
    /// <param name="host">Host name.</param>
    /// <param name="username">Username.</param>
    /// <param name="password">Password or secret.</param>
    /// <param name="realm">Optional authentication realm.</param>
    /// <param name="label">Optional human-readable label.</param>
    /// <returns>True if stored successfully.</returns>
    public bool Store(string protocol, string host, string username, string password, 
                      string? realm = null, string? label = null)
    {
        var entry = new CredentialEntry
        {
            Protocol = protocol,
            Host = host,
            Username = username,
            Realm = realm,
            Label = label
        };
        entry.SetSecret(password);
        
        return Store(entry);
    }
    
    /// <summary>
    /// Stores or updates a credential entry.
    /// </summary>
    public bool Store(CredentialEntry entry)
    {
        if (disposed)
            return false;
            
        lock (syncLock)
        {
            // Check if matching entry exists
            var searchCriteria = new Dictionary<CredentialKey, string?>();
            foreach (var kvp in entry.GetValues())
                searchCriteria[kvp.Key] = kvp.Value;
            
            // Remove existing match
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Matches(searchCriteria))
                {
                    entries[i].ClearSecret();
                    entries.RemoveAt(i);
                    break;
                }
            }
            
            entries.Add(entry.Clone());
            return true;
        }
    }
    
    /// <summary>
    /// Finds credentials matching the search criteria.
    /// </summary>
    /// <param name="protocol">Protocol to match (null = any).</param>
    /// <param name="host">Host to match (null = any).</param>
    /// <param name="username">Username to match (null = any).</param>
    /// <param name="realm">Realm to match (null = any).</param>
    /// <returns>List of matching credentials (cloned).</returns>
    public List<CredentialEntry> Find(string? protocol = null, string? host = null, 
                                       string? username = null, string? realm = null)
    {
        var criteria = new Dictionary<CredentialKey, string?>();
        if (protocol != null) criteria[CredentialKey.Protocol] = protocol;
        if (host != null) criteria[CredentialKey.Host] = host;
        if (username != null) criteria[CredentialKey.Username] = username;
        if (realm != null) criteria[CredentialKey.Realm] = realm;
        
        return Find(criteria);
    }
    
    /// <summary>
    /// Finds credentials matching the search criteria.
    /// </summary>
    public List<CredentialEntry> Find(IReadOnlyDictionary<CredentialKey, string?>? criteria)
    {
        var results = new List<CredentialEntry>();
        
        if (disposed)
            return results;
            
        lock (syncLock)
        {
            foreach (var entry in entries)
            {
                if (entry.Matches(criteria))
                    results.Add(entry.Clone());
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Finds the first credential matching the criteria.
    /// </summary>
    public CredentialEntry? FindFirst(string? protocol = null, string? host = null,
                                       string? username = null, string? realm = null)
    {
        var results = Find(protocol, host, username, realm);
        return results.Count > 0 ? results[0] : null;
    }
    
    /// <summary>
    /// Removes credentials matching the search criteria.
    /// </summary>
    /// <returns>Number of removed entries.</returns>
    public int Remove(string? protocol = null, string? host = null,
                      string? username = null, string? realm = null)
    {
        var criteria = new Dictionary<CredentialKey, string?>();
        if (protocol != null) criteria[CredentialKey.Protocol] = protocol;
        if (host != null) criteria[CredentialKey.Host] = host;
        if (username != null) criteria[CredentialKey.Username] = username;
        if (realm != null) criteria[CredentialKey.Realm] = realm;
        
        return Remove(criteria);
    }
    
    /// <summary>
    /// Removes credentials matching the search criteria.
    /// </summary>
    public int Remove(IReadOnlyDictionary<CredentialKey, string?>? criteria)
    {
        if (disposed)
            return 0;
            
        int removed = 0;
        
        lock (syncLock)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Matches(criteria))
                {
                    entries[i].ClearSecret();
                    entries.RemoveAt(i);
                    removed++;
                }
            }
        }
        
        return removed;
    }
    
    /// <summary>
    /// Clears all stored credentials.
    /// </summary>
    public void Clear()
    {
        lock (syncLock)
        {
            foreach (var entry in entries)
                entry.ClearSecret();
            entries.Clear();
        }
    }
    
    /// <summary>
    /// Gets all stored credentials (cloned, without secrets).
    /// </summary>
    public List<CredentialEntry> GetAllWithoutSecrets()
    {
        var results = new List<CredentialEntry>();
        
        lock (syncLock)
        {
            foreach (var entry in entries)
                results.Add(entry.CloneWithoutSecret());
        }
        
        return results;
    }
    
    public void Dispose()
    {
        if (disposed)
            return;
            
        disposed = true;
        Clear();
    }
}

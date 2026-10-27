// Data URL scheme parser (RFC 2397)
// Parses data: URIs with embedded content (base64 or plain text)

using System;
using System.Text;

namespace MediaKernel.Core.IO;

/// <summary>
/// Represents parsed data from a data: URL (RFC 2397).
/// </summary>
public readonly struct DataUrlContent
{
    /// <summary>
    /// The MIME type of the content (e.g., "text/plain", "image/png").
    /// Defaults to "text/plain;charset=US-ASCII" if not specified.
    /// </summary>
    public string MediaType { get; init; }
    
    /// <summary>
    /// The decoded binary content.
    /// </summary>
    public byte[] Data { get; init; }
    
    /// <summary>
    /// Whether the content was base64 encoded.
    /// </summary>
    public bool WasBase64Encoded { get; init; }
    
    /// <summary>
    /// Returns true if this represents valid parsed content.
    /// </summary>
    public bool IsValid => Data != null && Data.Length > 0;
}

/// <summary>
/// Parses data: URLs (RFC 2397) into binary content.
/// Format: data:[&lt;mediatype&gt;][;base64],&lt;data&gt;
/// </summary>
public static class DataUrlParser
{
    private const string DataScheme = "data:";
    private const string Base64Marker = ";base64";
    private const string DefaultMediaType = "text/plain;charset=US-ASCII";
    
    /// <summary>
    /// Checks if a URL is a data: URL.
    /// </summary>
    public static bool IsDataUrl(string url)
    {
        return !string.IsNullOrEmpty(url) && 
               url.StartsWith(DataScheme, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Checks if a URL is a data: URL.
    /// </summary>
    public static bool IsDataUrl(ReadOnlySpan<char> url)
    {
        return url.Length >= DataScheme.Length &&
               url.StartsWith(DataScheme, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Parses a data: URL and extracts its content.
    /// </summary>
    /// <param name="url">The complete data: URL string.</param>
    /// <returns>Parsed content, or default if parsing fails.</returns>
    public static DataUrlContent Parse(string url)
    {
        if (string.IsNullOrEmpty(url))
            return default;
            
        return Parse(url.AsSpan());
    }
    
    /// <summary>
    /// Parses a data: URL and extracts its content.
    /// </summary>
    /// <param name="url">The complete data: URL span.</param>
    /// <returns>Parsed content, or default if parsing fails.</returns>
    public static DataUrlContent Parse(ReadOnlySpan<char> url)
    {
        // Check for data: scheme
        if (!IsDataUrl(url))
            return default;
            
        // Skip "data:" prefix
        var remaining = url.Slice(DataScheme.Length);
        
        // Find the comma separator between metadata and data
        int commaIndex = remaining.IndexOf(',');
        if (commaIndex < 0)
            return default; // Invalid format - no comma
            
        // Extract metadata portion (before comma)
        var metadata = remaining.Slice(0, commaIndex);
        var encodedData = remaining.Slice(commaIndex + 1);
        
        // Check for base64 encoding marker
        bool isBase64 = metadata.EndsWith(Base64Marker, StringComparison.OrdinalIgnoreCase);
        
        // Extract media type (everything before ;base64, if present)
        string mediaType;
        if (isBase64)
        {
            var mediaTypeSpan = metadata.Slice(0, metadata.Length - Base64Marker.Length);
            mediaType = mediaTypeSpan.Length > 0 ? new string(mediaTypeSpan) : DefaultMediaType;
        }
        else
        {
            mediaType = metadata.Length > 0 ? new string(metadata) : DefaultMediaType;
        }
        
        // Decode the data
        byte[]? data;
        if (isBase64)
        {
            data = DecodeBase64(encodedData);
        }
        else
        {
            data = DecodeUrlEncoded(encodedData);
        }
        
        if (data == null)
            return default;
            
        return new DataUrlContent
        {
            MediaType = mediaType,
            Data = data,
            WasBase64Encoded = isBase64
        };
    }
    
    /// <summary>
    /// Tries to parse a data: URL.
    /// </summary>
    /// <param name="url">The URL to parse.</param>
    /// <param name="content">The parsed content if successful.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(string url, out DataUrlContent content)
    {
        content = Parse(url);
        return content.IsValid;
    }
    
    /// <summary>
    /// Creates a data: URL from binary content.
    /// </summary>
    /// <param name="data">The binary data to encode.</param>
    /// <param name="mediaType">The MIME type (e.g., "image/png").</param>
    /// <param name="useBase64">Whether to use base64 encoding (recommended for binary data).</param>
    /// <returns>A complete data: URL string.</returns>
    public static string Create(ReadOnlySpan<byte> data, string mediaType, bool useBase64 = true)
    {
        if (data.IsEmpty)
            return $"data:{mediaType},";
            
        var sb = new StringBuilder();
        sb.Append("data:");
        sb.Append(mediaType);
        
        if (useBase64)
        {
            sb.Append(";base64,");
            sb.Append(Convert.ToBase64String(data));
        }
        else
        {
            sb.Append(',');
            // URL-encode the data
            foreach (byte b in data)
            {
                if (IsUnreserved(b))
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('%');
                    sb.Append(b.ToString("X2"));
                }
            }
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Creates a data: URL from text content.
    /// </summary>
    /// <param name="text">The text content.</param>
    /// <param name="mediaType">The MIME type (default: "text/plain").</param>
    /// <param name="useBase64">Whether to use base64 encoding.</param>
    /// <returns>A complete data: URL string.</returns>
    public static string CreateFromText(string text, string mediaType = "text/plain", bool useBase64 = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Create(bytes, mediaType, useBase64);
    }
    
    /// <summary>
    /// Gets the media type from a data URL without fully decoding the data.
    /// </summary>
    public static string? GetMediaType(string url)
    {
        if (!IsDataUrl(url))
            return null;
            
        var remaining = url.AsSpan().Slice(DataScheme.Length);
        int commaIndex = remaining.IndexOf(',');
        if (commaIndex < 0)
            return null;
            
        var metadata = remaining.Slice(0, commaIndex);
        
        // Remove ;base64 suffix if present
        if (metadata.EndsWith(Base64Marker, StringComparison.OrdinalIgnoreCase))
            metadata = metadata.Slice(0, metadata.Length - Base64Marker.Length);
            
        return metadata.Length > 0 ? new string(metadata) : DefaultMediaType;
    }
    
    /// <summary>
    /// Gets the estimated size of the decoded data without actually decoding.
    /// </summary>
    public static int EstimateDataSize(string url)
    {
        if (!IsDataUrl(url))
            return 0;
            
        var remaining = url.AsSpan().Slice(DataScheme.Length);
        int commaIndex = remaining.IndexOf(',');
        if (commaIndex < 0)
            return 0;
            
        var metadata = remaining.Slice(0, commaIndex);
        var encodedData = remaining.Slice(commaIndex + 1);
        
        bool isBase64 = metadata.EndsWith(Base64Marker, StringComparison.OrdinalIgnoreCase);
        
        if (isBase64)
        {
            // Base64 expands data by ~33%, so decoded is ~75% of encoded
            return (encodedData.Length * 3) / 4;
        }
        else
        {
            // URL encoding expands data, so decoded is smaller or equal
            return encodedData.Length;
        }
    }
    
    private static byte[]? DecodeBase64(ReadOnlySpan<char> encoded)
    {
        if (encoded.IsEmpty)
            return Array.Empty<byte>();
            
        try
        {
            // Handle URL-safe base64 variants (replace - with + and _ with /)
            Span<char> normalized = stackalloc char[encoded.Length];
            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                normalized[i] = c switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => c
                };
            }
            
            // Remove any whitespace and calculate proper padding
            int nonWhitespace = 0;
            foreach (char c in normalized)
            {
                if (!char.IsWhiteSpace(c))
                    nonWhitespace++;
            }
            
            // Compact whitespace
            Span<char> compacted = stackalloc char[nonWhitespace];
            int j = 0;
            foreach (char c in normalized)
            {
                if (!char.IsWhiteSpace(c))
                    compacted[j++] = c;
            }
            
            // Add padding if needed
            int padNeeded = (4 - (compacted.Length % 4)) % 4;
            if (padNeeded > 0)
            {
                Span<char> padded = stackalloc char[compacted.Length + padNeeded];
                compacted.CopyTo(padded);
                for (int i = 0; i < padNeeded; i++)
                    padded[compacted.Length + i] = '=';
                return Convert.FromBase64String(new string(padded));
            }
            
            return Convert.FromBase64String(new string(compacted));
        }
        catch
        {
            return null;
        }
    }
    
    private static byte[]? DecodeUrlEncoded(ReadOnlySpan<char> encoded)
    {
        if (encoded.IsEmpty)
            return Array.Empty<byte>();
            
        try
        {
            // First pass: count decoded bytes
            int decodedLength = 0;
            int i = 0;
            while (i < encoded.Length)
            {
                if (encoded[i] == '%' && i + 2 < encoded.Length)
                {
                    i += 3; // Skip %XX
                }
                else
                {
                    i++;
                }
                decodedLength++;
            }
            
            // Second pass: decode
            var result = new byte[decodedLength];
            int pos = 0;
            i = 0;
            while (i < encoded.Length)
            {
                if (encoded[i] == '%' && i + 2 < encoded.Length)
                {
                    // Decode hex byte
                    int high = HexValue(encoded[i + 1]);
                    int low = HexValue(encoded[i + 2]);
                    if (high >= 0 && low >= 0)
                    {
                        result[pos++] = (byte)((high << 4) | low);
                    }
                    i += 3;
                }
                else
                {
                    // Direct character (treat as byte)
                    result[pos++] = (byte)encoded[i];
                    i++;
                }
            }
            
            // Trim if we had any invalid sequences
            if (pos != decodedLength)
            {
                var trimmed = new byte[pos];
                Array.Copy(result, trimmed, pos);
                return trimmed;
            }
            
            return result;
        }
        catch
        {
            return null;
        }
    }
    
    private static int HexValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => -1
        };
    }
    
    // RFC 3986 unreserved characters: ALPHA / DIGIT / "-" / "." / "_" / "~"
    private static bool IsUnreserved(byte b)
    {
        return b switch
        {
            >= (byte)'A' and <= (byte)'Z' => true,
            >= (byte)'a' and <= (byte)'z' => true,
            >= (byte)'0' and <= (byte)'9' => true,
            (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' => true,
            _ => false
        };
    }
}

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Input;

/// <summary>
/// Station details and now-playing text from a Shoutcast or Icecast stream.
/// </summary>
/// <remarks>
/// ICY is not a container: the server interleaves short metadata blocks into the audio bytes
/// themselves, every <c>icy-metaint</c> bytes. The audio is a plain MP3 or ADTS elementary stream
/// once those blocks are removed, which is why <see cref="HttpMediaInput"/> strips them before the
/// bytes ever reach a demuxer.
/// </remarks>
public sealed class IcyMetadata
{
    /// <summary>Station name (<c>icy-name</c>).</summary>
    public string? StationName { get; internal set; }

    /// <summary>Genre as the station describes it (<c>icy-genre</c>).</summary>
    public string? Genre { get; internal set; }

    /// <summary>Free-text description (<c>icy-description</c>).</summary>
    public string? Description { get; internal set; }

    /// <summary>Station home page (<c>icy-url</c>).</summary>
    public string? StationUrl { get; internal set; }

    /// <summary>Nominal bitrate in kbps (<c>icy-br</c>), or 0 when the server does not say.</summary>
    public int BitrateKbps { get; internal set; }

    /// <summary>
    /// Bytes of audio between metadata blocks (<c>icy-metaint</c>). Zero when the server sends no
    /// in-band metadata, which is normal - it only does so if the client asked with
    /// <c>Icy-MetaData: 1</c>, and some stations decline anyway.
    /// </summary>
    public int MetadataInterval { get; internal set; }

    /// <summary>
    /// Current track, from the most recent <c>StreamTitle</c> block. Null until the first block
    /// arrives, which can take most of a metadata interval.
    /// </summary>
    public string? StreamTitle { get; internal set; }

    /// <summary>URL the station attached to the current track (<c>StreamUrl</c>), often artwork.</summary>
    public string? StreamUrl { get; internal set; }

    /// <summary>True when the server answered with ICY headers at all.</summary>
    public bool IsIcyStream => MetadataInterval > 0 || StationName != null;

    /// <summary>
    /// Parses one metadata block: semicolon-separated <c>Key='value'</c> pairs, null-padded to a
    /// multiple of 16 bytes. Unknown keys are returned so callers can see anything a station
    /// invents; malformed input yields whatever pairs could still be read rather than throwing,
    /// since a bad block must never interrupt playback.
    /// </summary>
    public static Dictionary<string, string> ParseBlock(ReadOnlySpan<char> block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int index = 0;
        while (index < block.Length)
        {
            int equals = block[index..].IndexOf('=');
            if (equals < 0)
                break;
            equals += index;

            string key = block[index..equals].Trim().ToString();
            int valueStart = equals + 1;
            if (valueStart >= block.Length)
                break;

            string value;
            if (block[valueStart] == '\'')
            {
                // Quoted. Terminated by "';" where possible, so a value may itself contain a quote
                // - track titles with apostrophes are common and must not truncate.
                int searchFrom = valueStart + 1;
                int close = block[searchFrom..].IndexOf("';".AsSpan());
                if (close < 0)
                {
                    close = block[searchFrom..].LastIndexOf('\'');
                    if (close < 0)
                        break;
                }
                close += searchFrom;
                value = block[(valueStart + 1)..close].ToString();
                index = close + 1;
                if (index < block.Length && block[index] == ';')
                    index++;
            }
            else
            {
                int semicolon = block[valueStart..].IndexOf(';');
                int end = semicolon < 0 ? block.Length : valueStart + semicolon;
                value = block[valueStart..end].ToString();
                index = end + 1;
            }

            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }
}

/// <summary>Raised when a station announces a new track.</summary>
public sealed class StreamTitleChangedEventArgs(string? title, string? url) : EventArgs
{
    /// <summary>The new <c>StreamTitle</c>, typically "Artist - Track".</summary>
    public string? Title { get; } = title;

    /// <summary>The accompanying <c>StreamUrl</c>, if the station sent one.</summary>
    public string? Url { get; } = url;
}

// Teletext Decoder
// ETSI EN 300 706 - Enhanced Teletext specification
// Level 1.5 subtitle support for legacy European broadcasts

using System.Runtime.CompilerServices;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoder for European Teletext subtitles (Level 1.5).
/// Supports page 888 convention and national character sets.
/// </summary>
public sealed class TeletextDecoder
{
    /// <summary>Standard teletext width in characters.</summary>
    public const int CharsPerLine = 40;
    
    /// <summary>Maximum lines per page.</summary>
    public const int LinesPerPage = 24;
    
    /// <summary>Standard subtitle page number.</summary>
    public const int SubtitlePage = 888;
    
    private readonly TeletextMagazineState[] magazines = new TeletextMagazineState[9]; // 1-8 plus overflow
    private readonly int targetPage;
    private long lastPts;
    
    /// <summary>
    /// Event raised when a complete subtitle page is decoded.
    /// </summary>
    public event Action<TeletextSubtitleCue>? SubtitleReady;
    
    /// <summary>
    /// Creates a new teletext decoder targeting the specified page.
    /// </summary>
    /// <param name="page">Page number to decode (default 888 for subtitles).</param>
    public TeletextDecoder(int page = SubtitlePage)
    {
        targetPage = page;
        for (int i = 0; i < magazines.Length; i++)
        {
            magazines[i] = new TeletextMagazineState();
        }
    }
    
    /// <summary>
    /// Decodes teletext packets from PES data.
    /// </summary>
    /// <param name="data">Teletext PES packet data.</param>
    /// <param name="ptsMicroseconds">Presentation timestamp in microseconds.</param>
    /// <returns>List of decoded subtitle cues.</returns>
    public List<TeletextSubtitleCue> Decode(ReadOnlySpan<byte> data, long ptsMicroseconds)
    {
        var cues = new List<TeletextSubtitleCue>();
        lastPts = ptsMicroseconds;
        
        // Teletext PES data contains multiple 46-byte packets
        int offset = 0;
        
        // Skip data unit header if present
        if (data.Length > 0 && data[0] >= 0x10 && data[0] <= 0x1F)
        {
            offset = 1;
        }
        
        while (offset + 46 <= data.Length)
        {
            var packet = data.Slice(offset, 46);
            ProcessPacket(packet, cues);
            offset += 46;
        }
        
        return cues;
    }
    
    /// <summary>
    /// Processes a single 46-byte teletext packet.
    /// </summary>
    private void ProcessPacket(ReadOnlySpan<byte> packet, List<TeletextSubtitleCue> cues)
    {
        // Bytes 0-3: Data unit header
        // Bytes 4-5: MPAG (Magazine/Packet Address Group)
        // Bytes 6-45: Packet data (40 bytes)
        
        if (packet.Length < 46)
            return;
        
        // Decode MPAG using Hamming(8,4)
        int mpag = DecodeHamming84(packet[4]) | (DecodeHamming84(packet[5]) << 4);
        if (mpag < 0)
            return; // Hamming error
        
        int magazine = mpag & 0x07;
        if (magazine == 0) magazine = 8;
        
        int row = (mpag >> 3) & 0x1F;
        
        var state = magazines[magazine];
        
        if (row == 0)
        {
            // Page header packet
            ProcessHeaderPacket(packet.Slice(6), state, magazine, cues);
        }
        else if (row >= 1 && row <= 23)
        {
            // Content row
            ProcessContentRow(packet.Slice(6), state, row);
        }
        else if (row == 24)
        {
            // FastText navigation row (ignored for subtitles)
        }
        else if (row == 25)
        {
            // Alternative header (some pages use this)
        }
    }
    
    /// <summary>
    /// Processes a page header packet (row 0).
    /// </summary>
    private void ProcessHeaderPacket(ReadOnlySpan<byte> data, TeletextMagazineState state, int magazine, List<TeletextSubtitleCue> cues)
    {
        // Bytes 0-1: Page number (units and tens)
        // Bytes 2-5: Subpage code
        // Bytes 6-9: Control bits
        // Bytes 10-39: Page title
        
        if (data.Length < 40)
            return;
        
        int pageUnits = DecodeHamming84(data[0]);
        int pageTens = DecodeHamming84(data[1]);
        
        if (pageUnits < 0 || pageTens < 0)
            return;
        
        int pageNumber = magazine * 100 + pageTens * 10 + pageUnits;
        
        // Decode control bits (bytes 3, 5, 6, 7, 8, 9)
        int c4 = DecodeHamming84(data[3]) & 0x08;
        int c5c11 = DecodeHamming84(data[5]);
        int c12c14 = DecodeHamming84(data[6]);
        
        if (c5c11 < 0 || c12c14 < 0)
            return;
        
        var flags = TeletextPageFlags.None;
        
        // Erase page flag (C4)
        if (c4 != 0)
            flags |= TeletextPageFlags.ErasePage;
        
        // Subtitle flag (C5)
        if ((c5c11 & 0x01) != 0)
            flags |= TeletextPageFlags.Subtitle;
        
        // National character set (C12-C14)
        int charset = c12c14 & 0x07;
        state.CharacterSet = (TeletextCharacterSet)charset;
        
        // Check if previous page is complete and should be emitted
        if (state.CurrentPage != 0 && state.CurrentPage == targetPage)
        {
            EmitSubtitle(state, magazine, cues, (flags & TeletextPageFlags.ErasePage) != 0);
        }
        
        // Start new page
        state.CurrentPage = pageNumber;
        state.IsSubtitle = (flags & TeletextPageFlags.Subtitle) != 0;
        
        if ((flags & TeletextPageFlags.ErasePage) != 0)
        {
            Array.Clear(state.Lines);
        }
    }
    
    /// <summary>
    /// Processes a content row (rows 1-23).
    /// </summary>
    private void ProcessContentRow(ReadOnlySpan<byte> data, TeletextMagazineState state, int row)
    {
        if (state.CurrentPage != targetPage || data.Length < 40)
            return;
        
        var sb = new StringBuilder(40);
        bool skipMode = false;
        
        for (int i = 0; i < 40 && i < data.Length; i++)
        {
            byte b = ByteReverse(data[i]);
            b &= 0x7F; // Strip parity bit
            
            // Control code handling
            if (b < 0x20)
            {
                if (b == 0x0D)
                {
                    // Double height toggle - skip until 0x0B
                    skipMode = true;
                }
                else if (b == 0x0B)
                {
                    skipMode = false;
                }
                else if (b >= 0x00 && b <= 0x07)
                {
                    // Alpha color codes (space character)
                    sb.Append(' ');
                }
                else if (b >= 0x10 && b <= 0x17)
                {
                    // Mosaic color codes (space character)
                    sb.Append(' ');
                }
                else
                {
                    // Other control codes become space
                    sb.Append(' ');
                }
            }
            else if (skipMode)
            {
                sb.Append(' ');
            }
            else
            {
                // Normal character - apply national character set
                char c = DecodeCharacter(b, state.CharacterSet);
                sb.Append(c);
            }
        }
        
        state.Lines[row] = sb.ToString().TrimEnd();
    }
    
    /// <summary>
    /// Emits a completed subtitle page.
    /// </summary>
    private void EmitSubtitle(TeletextMagazineState state, int magazine, List<TeletextSubtitleCue> cues, bool erasePage)
    {
        // Collect non-empty lines
        var lines = new List<string>();
        for (int i = 1; i <= 23; i++)
        {
            if (!string.IsNullOrWhiteSpace(state.Lines[i]))
            {
                lines.Add(state.Lines[i]!);
            }
        }
        
        if (lines.Count > 0)
        {
            var cue = new TeletextSubtitleCue(state.CurrentPage, magazine, lastPts, [.. lines], erasePage);
            cues.Add(cue);
            SubtitleReady?.Invoke(cue);
        }
    }
    
    /// <summary>
    /// Decodes a character using the specified national character set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char DecodeCharacter(byte b, TeletextCharacterSet charset)
    {
        // Check for national character substitution positions
        // These positions have different characters in each national set
        int subst = GetNationalSubstitution(b, charset);
        if (subst != 0)
            return (char)subst;
        
        // Standard ASCII range
        if (b >= 0x20 && b <= 0x7E)
            return (char)b;
        
        return ' ';
    }
    
    /// <summary>
    /// Gets the national character substitution for a given byte.
    /// </summary>
    private static int GetNationalSubstitution(byte b, TeletextCharacterSet charset)
    {
        // National character substitution table
        // Each national set has different characters at these positions:
        // 0x23, 0x24, 0x40, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x7B, 0x7C, 0x7D, 0x7E
        
        return b switch
        {
            0x23 => charset switch // # position
            {
                TeletextCharacterSet.English => '£',
                TeletextCharacterSet.German => '§',
                TeletextCharacterSet.Italian => '£',
                _ => '#'
            },
            0x24 => charset switch // $ position
            {
                TeletextCharacterSet.English => '$',
                TeletextCharacterSet.French => 'é',
                TeletextCharacterSet.German => '$',
                TeletextCharacterSet.PortugueseSpanish => '$',
                _ => '$'
            },
            0x40 => charset switch // @ position
            {
                TeletextCharacterSet.English => '@',
                TeletextCharacterSet.French => 'à',
                TeletextCharacterSet.German => '§',
                TeletextCharacterSet.PortugueseSpanish => '¡',
                TeletextCharacterSet.Italian => 'é',
                _ => '@'
            },
            0x5B => charset switch // [ position
            {
                TeletextCharacterSet.French => '°',
                TeletextCharacterSet.German => 'Ä',
                TeletextCharacterSet.PortugueseSpanish => '¿',
                TeletextCharacterSet.Italian => '°',
                _ => '['
            },
            0x5C => charset switch // \ position
            {
                TeletextCharacterSet.French => 'ç',
                TeletextCharacterSet.German => 'Ö',
                TeletextCharacterSet.PortugueseSpanish => 'ç',
                TeletextCharacterSet.Italian => 'ç',
                _ => '\\'
            },
            0x5D => charset switch // ] position
            {
                TeletextCharacterSet.French => '→',
                TeletextCharacterSet.German => 'Ü',
                TeletextCharacterSet.PortugueseSpanish => '→',
                TeletextCharacterSet.Italian => '→',
                _ => ']'
            },
            0x5E => charset switch // ^ position
            {
                TeletextCharacterSet.French => '↑',
                TeletextCharacterSet.German => '^',
                _ => '^'
            },
            0x5F => charset switch // _ position
            {
                TeletextCharacterSet.French => '#',
                TeletextCharacterSet.German => '_',
                _ => '_'
            },
            0x60 => charset switch // ` position
            {
                TeletextCharacterSet.French => 'ù',
                TeletextCharacterSet.German => '°',
                TeletextCharacterSet.Italian => 'ù',
                _ => '`'
            },
            0x7B => charset switch // { position
            {
                TeletextCharacterSet.French => 'ê',
                TeletextCharacterSet.German => 'ä',
                TeletextCharacterSet.PortugueseSpanish => 'ü',
                TeletextCharacterSet.Italian => 'à',
                _ => '{'
            },
            0x7C => charset switch // | position
            {
                TeletextCharacterSet.French => 'î',
                TeletextCharacterSet.German => 'ö',
                TeletextCharacterSet.PortugueseSpanish => 'ñ',
                TeletextCharacterSet.Italian => 'ò',
                _ => '|'
            },
            0x7D => charset switch // } position
            {
                TeletextCharacterSet.French => 'è',
                TeletextCharacterSet.German => 'ü',
                TeletextCharacterSet.PortugueseSpanish => 'è',
                TeletextCharacterSet.Italian => 'è',
                _ => '}'
            },
            0x7E => charset switch // ~ position
            {
                TeletextCharacterSet.French => 'ë',
                TeletextCharacterSet.German => 'ß',
                TeletextCharacterSet.PortugueseSpanish => 'ì',
                TeletextCharacterSet.Italian => 'ì',
                _ => '~'
            },
            _ => 0
        };
    }
    
    /// <summary>
    /// Decodes a Hamming(8,4) encoded byte to 4 data bits.
    /// Returns -1 on uncorrectable error.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeHamming84(byte b)
    {
        // Bit-reverse first (teletext uses LSB first)
        b = ByteReverse(b);
        
        // Hamming(8,4) decoding table
        // Returns 4 data bits or -1 for error
        return Hamming84Table[b];
    }
    
    /// <summary>
    /// Reverses the bits in a byte (teletext uses LSB first).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ByteReverse(byte b)
    {
        return ByteReverseTable[b];
    }
    
    /// <summary>
    /// Clears all decoder state.
    /// </summary>
    public void Reset()
    {
        foreach (var mag in magazines)
        {
            mag.Clear();
        }
        lastPts = 0;
    }
    
    /// <summary>
    /// Gets the target page number this decoder is configured for.
    /// </summary>
    public int TargetPage => targetPage;
    
    // Hamming(8,4) decode table: maps 256 values to 4 data bits or -1 for error
    private static readonly int[] Hamming84Table =
    [
        0x01, -1, 0x01, 0x01, -1, 0x00, 0x01, -1, -1, 0x02, 0x01, -1, 0x0A, -1, -1, 0x07,
        -1, 0x00, 0x01, -1, 0x00, 0x00, -1, 0x00, 0x06, -1, -1, 0x0B, -1, 0x00, 0x03, -1,
        -1, 0x0C, 0x01, -1, 0x04, -1, -1, 0x07, 0x06, -1, -1, 0x07, -1, 0x07, 0x07, 0x07,
        0x06, -1, -1, 0x05, -1, 0x00, 0x0D, -1, 0x06, 0x06, 0x06, -1, 0x06, -1, -1, 0x07,
        -1, 0x02, 0x01, -1, 0x04, -1, -1, 0x09, 0x02, 0x02, -1, 0x02, -1, 0x02, 0x03, -1,
        0x08, -1, -1, 0x05, -1, 0x00, 0x03, -1, -1, 0x02, 0x03, -1, 0x03, -1, 0x03, 0x03,
        0x04, -1, -1, 0x05, 0x04, 0x04, 0x04, -1, -1, 0x02, 0x0F, -1, 0x04, -1, -1, 0x07,
        -1, 0x05, 0x05, 0x05, 0x04, -1, -1, 0x05, 0x06, -1, -1, 0x05, -1, 0x0E, 0x03, -1,
        -1, 0x0C, 0x01, -1, 0x0A, -1, -1, 0x09, 0x0A, -1, -1, 0x0B, 0x0A, 0x0A, 0x0A, -1,
        0x08, -1, -1, 0x0B, -1, 0x00, 0x0D, -1, -1, 0x0B, 0x0B, 0x0B, 0x0A, -1, -1, 0x0B,
        0x0C, 0x0C, -1, 0x0C, -1, 0x0C, 0x0D, -1, -1, 0x0C, 0x0F, -1, 0x0A, -1, -1, 0x07,
        -1, 0x0C, 0x0D, -1, 0x0D, -1, 0x0D, 0x0D, 0x06, -1, -1, 0x0B, -1, 0x0E, 0x0D, -1,
        0x08, -1, -1, 0x09, -1, 0x09, 0x09, 0x09, -1, 0x02, 0x0F, -1, 0x0A, -1, -1, 0x09,
        0x08, 0x08, 0x08, -1, 0x08, -1, -1, 0x09, 0x08, -1, -1, 0x0B, -1, 0x0E, 0x03, -1,
        -1, 0x0C, 0x0F, -1, 0x04, -1, -1, 0x09, 0x0F, -1, 0x0F, 0x0F, -1, 0x0E, 0x0F, -1,
        0x08, -1, -1, 0x05, -1, 0x0E, 0x0D, -1, -1, 0x0E, 0x0F, -1, 0x0E, 0x0E, -1, 0x0E
    ];
    
    // Bit reverse lookup table
    private static readonly byte[] ByteReverseTable =
    [
        0x00, 0x80, 0x40, 0xC0, 0x20, 0xA0, 0x60, 0xE0, 0x10, 0x90, 0x50, 0xD0, 0x30, 0xB0, 0x70, 0xF0,
        0x08, 0x88, 0x48, 0xC8, 0x28, 0xA8, 0x68, 0xE8, 0x18, 0x98, 0x58, 0xD8, 0x38, 0xB8, 0x78, 0xF8,
        0x04, 0x84, 0x44, 0xC4, 0x24, 0xA4, 0x64, 0xE4, 0x14, 0x94, 0x54, 0xD4, 0x34, 0xB4, 0x74, 0xF4,
        0x0C, 0x8C, 0x4C, 0xCC, 0x2C, 0xAC, 0x6C, 0xEC, 0x1C, 0x9C, 0x5C, 0xDC, 0x3C, 0xBC, 0x7C, 0xFC,
        0x02, 0x82, 0x42, 0xC2, 0x22, 0xA2, 0x62, 0xE2, 0x12, 0x92, 0x52, 0xD2, 0x32, 0xB2, 0x72, 0xF2,
        0x0A, 0x8A, 0x4A, 0xCA, 0x2A, 0xAA, 0x6A, 0xEA, 0x1A, 0x9A, 0x5A, 0xDA, 0x3A, 0xBA, 0x7A, 0xFA,
        0x06, 0x86, 0x46, 0xC6, 0x26, 0xA6, 0x66, 0xE6, 0x16, 0x96, 0x56, 0xD6, 0x36, 0xB6, 0x76, 0xF6,
        0x0E, 0x8E, 0x4E, 0xCE, 0x2E, 0xAE, 0x6E, 0xEE, 0x1E, 0x9E, 0x5E, 0xDE, 0x3E, 0xBE, 0x7E, 0xFE,
        0x01, 0x81, 0x41, 0xC1, 0x21, 0xA1, 0x61, 0xE1, 0x11, 0x91, 0x51, 0xD1, 0x31, 0xB1, 0x71, 0xF1,
        0x09, 0x89, 0x49, 0xC9, 0x29, 0xA9, 0x69, 0xE9, 0x19, 0x99, 0x59, 0xD9, 0x39, 0xB9, 0x79, 0xF9,
        0x05, 0x85, 0x45, 0xC5, 0x25, 0xA5, 0x65, 0xE5, 0x15, 0x95, 0x55, 0xD5, 0x35, 0xB5, 0x75, 0xF5,
        0x0D, 0x8D, 0x4D, 0xCD, 0x2D, 0xAD, 0x6D, 0xED, 0x1D, 0x9D, 0x5D, 0xDD, 0x3D, 0xBD, 0x7D, 0xFD,
        0x03, 0x83, 0x43, 0xC3, 0x23, 0xA3, 0x63, 0xE3, 0x13, 0x93, 0x53, 0xD3, 0x33, 0xB3, 0x73, 0xF3,
        0x0B, 0x8B, 0x4B, 0xCB, 0x2B, 0xAB, 0x6B, 0xEB, 0x1B, 0x9B, 0x5B, 0xDB, 0x3B, 0xBB, 0x7B, 0xFB,
        0x07, 0x87, 0x47, 0xC7, 0x27, 0xA7, 0x67, 0xE7, 0x17, 0x97, 0x57, 0xD7, 0x37, 0xB7, 0x77, 0xF7,
        0x0F, 0x8F, 0x4F, 0xCF, 0x2F, 0xAF, 0x6F, 0xEF, 0x1F, 0x9F, 0x5F, 0xDF, 0x3F, 0xBF, 0x7F, 0xFF
    ];
}

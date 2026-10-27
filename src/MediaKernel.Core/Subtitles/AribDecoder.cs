// ARIB STD-B24 Subtitle Decoder
// Japanese broadcast (ISDB) caption decoder
// Supports A-Profile (HD) and C-Profile (SD/1seg)

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// ARIB caption profile (transmission mode).
/// </summary>
public enum AribProfile
{
    /// <summary>A-Profile for HD broadcasting (ISDB-T, BS/CS digital).</summary>
    AProfile,
    /// <summary>C-Profile for SD/1seg broadcasting (mobile reception).</summary>
    CProfile
}

/// <summary>
/// ARIB character sets defined in STD-B24.
/// </summary>
public enum AribCharacterSet
{
    /// <summary>Kanji (2-byte, JIS X 0208 extension).</summary>
    Kanji = 0x42,
    /// <summary>Alphanumeric (1-byte, JIS X 0201 Roman).</summary>
    Alphanumeric = 0x4A,
    /// <summary>Proportional alphanumeric.</summary>
    ProportionalAlphanumeric = 0x36,
    /// <summary>Hiragana (1-byte).</summary>
    Hiragana = 0x30,
    /// <summary>Proportional Hiragana.</summary>
    ProportionalHiragana = 0x37,
    /// <summary>Katakana (1-byte).</summary>
    Katakana = 0x31,
    /// <summary>Proportional Katakana.</summary>
    ProportionalKatakana = 0x38,
    /// <summary>JIS X 0201 Katakana.</summary>
    JisKatakana = 0x49,
    /// <summary>Mosaic A (pseudographics).</summary>
    MosaicA = 0x32,
    /// <summary>Mosaic B (pseudographics).</summary>
    MosaicB = 0x33,
    /// <summary>Mosaic C (non-spacing).</summary>
    MosaicC = 0x34,
    /// <summary>Mosaic D (non-spacing).</summary>
    MosaicD = 0x35,
    /// <summary>DRCS (Dynamic Runtime Character Set) 0.</summary>
    Drcs0 = 0x40,
    /// <summary>DRCS 1-15.</summary>
    Drcs1 = 0x41,
    /// <summary>Macro characters.</summary>
    Macro = 0x70,
    /// <summary>Additional symbols.</summary>
    AdditionalSymbols = 0x3B
}

/// <summary>
/// Text region in ARIB subtitles with positioning and styling.
/// </summary>
public struct AribTextRegion
{
    /// <summary>Decoded text content.</summary>
    public string Text;
    /// <summary>Font name (optional).</summary>
    public string? FontName;
    /// <summary>Foreground color (ARGB).</summary>
    public uint ForegroundColor;
    /// <summary>Background color (ARGB).</summary>
    public uint BackgroundColor;
    /// <summary>Plane width in pixels.</summary>
    public int PlaneWidth;
    /// <summary>Plane height in pixels.</summary>
    public int PlaneHeight;
    /// <summary>Font width in pixels.</summary>
    public int FontWidth;
    /// <summary>Font height in pixels.</summary>
    public int FontHeight;
    /// <summary>Vertical interval between characters.</summary>
    public int VerticalInterval;
    /// <summary>Horizontal interval between characters.</summary>
    public int HorizontalInterval;
    /// <summary>Character left position.</summary>
    public int CharLeft;
    /// <summary>Character bottom position.</summary>
    public int CharBottom;
    /// <summary>Horizontal position adjustment.</summary>
    public int HorizontalAdjustment;
    /// <summary>Vertical position adjustment.</summary>
    public int VerticalAdjustment;
    /// <summary>Whether this is ruby (furigana) text.</summary>
    public bool IsRuby;
}

/// <summary>
/// ARIB caption event with timing and text regions.
/// </summary>
public class AribEvent
{
    /// <summary>Start time in microseconds.</summary>
    public long StartTimeMicroseconds { get; set; }
    /// <summary>End time in microseconds (-1 if unknown).</summary>
    public long EndTimeMicroseconds { get; set; } = -1;
    /// <summary>Duration in microseconds (0 if ephemeral).</summary>
    public long DurationMicroseconds { get; set; }
    /// <summary>Text regions with styling.</summary>
    public List<AribTextRegion> Regions { get; } = new();
    /// <summary>Whether this event clears previous captions.</summary>
    public bool ClearsPrevious { get; set; }
}

/// <summary>
/// ARIB STD-B24 caption decoder for Japanese broadcast subtitles.
/// </summary>
public class AribDecoder
{
    // ISO-2022 escape sequences
    private const byte Esc = 0x1B;
    private const byte Ls0 = 0x0F;  // Locking shift G0 to GL
    private const byte Ls1 = 0x0E;  // Locking shift G1 to GL
    private const byte Ss2 = 0x19;  // Single shift G2
    private const byte Ss3 = 0x1D;  // Single shift G3
    
    // Control codes
    private const byte Nul = 0x00;
    private const byte Bel = 0x07;
    private const byte Apb = 0x08;  // Active position backward
    private const byte Apf = 0x09;  // Active position forward
    private const byte Apd = 0x0A;  // Active position down
    private const byte Apu = 0x0B;  // Active position up
    private const byte Cs = 0x0C;   // Clear screen
    private const byte Apr = 0x0D;  // Active position return
    private const byte Papf = 0x16; // Parameterized APF
    private const byte Can = 0x18;  // Cancel
    private const byte Sp = 0x20;   // Space
    private const byte Del = 0x7F;  // Delete
    
    // C1 control codes (0x80-0x9F or ESC + 0x40-0x5F)
    private const byte Csi = 0x9B;  // Control sequence introducer
    
    // Default colors (ARIB color palette)
    private static readonly uint[] DefaultPalette = 
    [
        0xFF000000, // 0: Black
        0xFFFF0000, // 1: Red
        0xFF00FF00, // 2: Green
        0xFFFFFF00, // 3: Yellow
        0xFF0000FF, // 4: Blue
        0xFFFF00FF, // 5: Magenta
        0xFF00FFFF, // 6: Cyan
        0xFFFFFFFF, // 7: White
        0x00000000, // 8: Transparent
        0x80000000, // 9: Half-intensity Black
        0x80FF0000, // 10: Half-intensity Red
        0x8000FF00, // 11: Half-intensity Green
        0x80FFFF00, // 12: Half-intensity Yellow
        0x800000FF, // 13: Half-intensity Blue
        0x80FF00FF, // 14: Half-intensity Magenta
        0x8000FFFF, // 15: Half-intensity Cyan
    ];
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> source)
        => (source[0] << 16) | (source[1] << 8) | source[2];
    
    private readonly List<AribEvent> events = new();
    private readonly StringBuilder textBuffer = new();
    
    private AribProfile profile = AribProfile.AProfile;
    private bool ignoreRuby;
    
    // Character set state (ISO-2022)
    private AribCharacterSet g0 = AribCharacterSet.Kanji;
    private AribCharacterSet g1 = AribCharacterSet.Alphanumeric;
    private AribCharacterSet g2 = AribCharacterSet.Hiragana;
    private AribCharacterSet g3 = AribCharacterSet.Katakana;
    private int glSet;  // 0-3: which G set is in GL
    private int grSet = 2;  // 0-3: which G set is in GR
    private int singleShift = -1;  // -1: none, 2: SS2, 3: SS3
    
    // Current formatting state
    private uint foregroundColor = 0xFFFFFFFF;
    private uint backgroundColor = 0x00000000;
    private int fontWidth = 36;
    private int fontHeight = 36;
    private int planeWidth = 960;
    private int planeHeight = 540;
    private int charLeft;
    private int charBottom;
    private int horizontalInterval;
    private int verticalInterval;
    
    /// <summary>Events decoded from caption data.</summary>
    public IReadOnlyList<AribEvent> Events => events;
    
    /// <summary>Gets or sets the caption profile.</summary>
    public AribProfile Profile
    {
        get => profile;
        set
        {
            profile = value;
            InitializeForProfile();
        }
    }
    
    /// <summary>Gets or sets whether to ignore ruby (furigana) text.</summary>
    public bool IgnoreRuby
    {
        get => ignoreRuby;
        set => ignoreRuby = value;
    }
    
    /// <summary>
    /// Probes data to check if it might be ARIB caption data.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return false;
        
        // ARIB captions in PES start with data_identifier (0x80) or private_stream
        // Check for common control codes and ISO-2022 escape sequences
        for (int i = 0; i < Math.Min(data.Length, 32); i++)
        {
            byte b = data[i];
            // ESC sequence start
            if (b == Esc && i + 1 < data.Length)
                return true;
            // CSI control
            if (b == Csi)
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Decodes ARIB caption data from a PES packet.
    /// </summary>
    /// <param name="data">Raw caption data.</param>
    /// <param name="ptsMicroseconds">Presentation timestamp in microseconds.</param>
    public void DecodePacket(ReadOnlySpan<byte> data, long ptsMicroseconds)
    {
        if (data.Length < 3)
            return;
        
        // Parse data group (caption management or caption data)
        int offset = 0;
        
        // Skip data identifier if present (0x80 for A-profile, 0x81 for C-profile)
        if (data[0] == 0x80 || data[0] == 0x81)
        {
            profile = data[0] == 0x80 ? AribProfile.AProfile : AribProfile.CProfile;
            offset++;
        }
        
        // Skip private stream id if present
        if (offset < data.Length && data[offset] == 0xFF)
            offset++;
        
        if (offset >= data.Length)
            return;
        
        // Parse data group
        if (!ParseDataGroup(data[offset..], ptsMicroseconds))
            return;
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        events.Clear();
        textBuffer.Clear();
        InitializeForProfile();
    }
    
    /// <summary>
    /// Converts events to subtitle cues.
    /// </summary>
    public IEnumerable<SubtitleCue> ToSubtitleCues()
    {
        foreach (var ev in events)
        {
            if (ev.Regions.Count == 0)
                continue;
            
            // Combine all text regions
            var sb = new StringBuilder();
            foreach (var region in ev.Regions)
            {
                if (ignoreRuby && region.IsRuby)
                    continue;
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(region.Text);
            }
            
            if (sb.Length == 0)
                continue;
            
            long endTime = ev.EndTimeMicroseconds > 0 
                ? ev.EndTimeMicroseconds 
                : ev.StartTimeMicroseconds + Math.Max(ev.DurationMicroseconds, 5_000_000);
            
            yield return new SubtitleCue(
                ev.StartTimeMicroseconds,
                endTime,
                sb.ToString(),
                SubtitleType.Text);
        }
    }
    
    private void InitializeForProfile()
    {
        if (profile == AribProfile.AProfile)
        {
            // A-Profile defaults (HD)
            g0 = AribCharacterSet.Kanji;
            g1 = AribCharacterSet.Alphanumeric;
            g2 = AribCharacterSet.Hiragana;
            g3 = AribCharacterSet.Macro;
            planeWidth = 960;
            planeHeight = 540;
            fontWidth = 36;
            fontHeight = 36;
        }
        else
        {
            // C-Profile defaults (SD/1seg)
            g0 = AribCharacterSet.Kanji;
            g1 = AribCharacterSet.Alphanumeric;
            g2 = AribCharacterSet.Hiragana;
            g3 = AribCharacterSet.Katakana;
            planeWidth = 320;
            planeHeight = 180;
            fontWidth = 16;
            fontHeight = 16;
        }
        
        glSet = 0;
        grSet = 2;
        singleShift = -1;
        foregroundColor = 0xFFFFFFFF;
        backgroundColor = 0x00000000;
        charLeft = 0;
        charBottom = fontHeight;
        horizontalInterval = 0;
        verticalInterval = 0;
    }
    
    private bool ParseDataGroup(ReadOnlySpan<byte> data, long ptsMicroseconds)
    {
        if (data.Length < 5)
            return false;
        
        // Data group header
        byte groupId = (byte)(data[0] >> 2);
        int dataGroupSize = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        
        if (5 + dataGroupSize > data.Length)
            return false;
        
        var groupData = data.Slice(5, dataGroupSize);
        
        // Group IDs: 0x00-0x01 = Caption management, 0x02-0x0F = Caption data
        if (groupId >= 0x02)
        {
            // Caption data group
            return ParseCaptionData(groupData, ptsMicroseconds);
        }
        
        // Caption management - skip for now
        return true;
    }
    
    private bool ParseCaptionData(ReadOnlySpan<byte> data, long ptsMicroseconds)
    {
        if (data.Length < 4)
            return false;
        
        // TMD (time control mode)
        byte tmd = (byte)(data[0] >> 6);
        
        int offset = 1;
        long durationUs = 0;
        
        // If TMD indicates timing data
        if (tmd == 0x01 || tmd == 0x02)
        {
            if (offset + 5 > data.Length)
                return false;
            
            // Parse STM (presentation start time) - 36 bits BCD
            // Skip for now, use PTS
            offset += 5;
        }
        
        if (offset >= data.Length)
            return false;
        
        int dataUnitLoopLength = ReadUInt24BigEndian(data[offset..]);
        offset += 3;
        
        if (offset + dataUnitLoopLength > data.Length)
            return false;
        
        // Parse data units
        var ev = new AribEvent { StartTimeMicroseconds = ptsMicroseconds };
        
        int endOffset = offset + dataUnitLoopLength;
        while (offset + 5 <= endOffset)
        {
            byte unitSeparator = data[offset++];
            if (unitSeparator != 0x1F)
                break;
            
            byte dataUnitParameter = data[offset++];
            int dataUnitSize = ReadUInt24BigEndian(data[offset..]);
            offset += 3;
            
            if (offset + dataUnitSize > endOffset)
                break;
            
            var unitData = data.Slice(offset, dataUnitSize);
            offset += dataUnitSize;
            
            // Data unit types
            switch (dataUnitParameter)
            {
                case 0x20: // Caption text
                    ParseCaptionText(unitData, ev);
                    break;
                case 0x28: // Geometric
                case 0x29: // Synthesized sound
                case 0x2C: // 1-byte DRCS
                case 0x2D: // 2-byte DRCS
                case 0x30: // Bitmap
                case 0x31: // Bitmap (FLNC)
                case 0x34: // Color map
                case 0x35: // Raster
                    // Skip for now
                    break;
            }
        }
        
        if (ev.Regions.Count > 0)
        {
            events.Add(ev);
        }
        
        return true;
    }
    
    private void ParseCaptionText(ReadOnlySpan<byte> data, AribEvent ev)
    {
        textBuffer.Clear();
        int i = 0;
        
        while (i < data.Length)
        {
            byte b = data[i];
            
            // C0 control codes (0x00-0x1F)
            if (b < 0x20)
            {
                i += ProcessC0Control(data[i..], ev);
                continue;
            }
            
            // GL area (0x20-0x7F)
            if (b < 0x80)
            {
                i += DecodeGlCharacter(data[i..]);
                continue;
            }
            
            // C1 control codes (0x80-0x9F)
            if (b < 0xA0)
            {
                i += ProcessC1Control(data[i..], ev);
                continue;
            }
            
            // GR area (0xA0-0xFF)
            i += DecodeGrCharacter(data[i..]);
        }
        
        // Flush remaining text
        FlushTextRegion(ev);
    }
    
    private int ProcessC0Control(ReadOnlySpan<byte> data, AribEvent ev)
    {
        byte b = data[0];
        
        switch (b)
        {
            case Nul:
                return 1;
            
            case Bel:
                // Bell - flash or attention
                return 1;
            
            case Apb:
                // Active position backward
                charLeft -= fontWidth + horizontalInterval;
                if (charLeft < 0) charLeft = 0;
                return 1;
            
            case Apf:
                // Active position forward
                charLeft += fontWidth + horizontalInterval;
                return 1;
            
            case Apd:
                // Active position down
                FlushTextRegion(ev);
                charBottom += fontHeight + verticalInterval;
                charLeft = 0;
                return 1;
            
            case Apu:
                // Active position up
                FlushTextRegion(ev);
                charBottom -= fontHeight + verticalInterval;
                if (charBottom < fontHeight) charBottom = fontHeight;
                charLeft = 0;
                return 1;
            
            case Cs:
                // Clear screen
                FlushTextRegion(ev);
                ev.ClearsPrevious = true;
                charLeft = 0;
                charBottom = fontHeight;
                return 1;
            
            case Apr:
                // Active position return (newline)
                FlushTextRegion(ev);
                charLeft = 0;
                charBottom += fontHeight + verticalInterval;
                return 1;
            
            case Ls0:
                // Locking shift G0 to GL
                glSet = 0;
                return 1;
            
            case Ls1:
                // Locking shift G1 to GL
                glSet = 1;
                return 1;
            
            case Papf:
                // Parameterized APF
                if (data.Length >= 2)
                {
                    int count = data[1] & 0x3F;
                    charLeft += count * (fontWidth + horizontalInterval);
                    return 2;
                }
                return 1;
            
            case Ss2:
                // Single shift G2
                singleShift = 2;
                return 1;
            
            case Ss3:
                // Single shift G3
                singleShift = 3;
                return 1;
            
            case Esc:
                // Escape sequence
                return ProcessEscapeSequence(data);
            
            case Sp:
                // Space
                textBuffer.Append(' ');
                charLeft += fontWidth + horizontalInterval;
                return 1;
            
            default:
                return 1;
        }
    }
    
    private int ProcessC1Control(ReadOnlySpan<byte> data, AribEvent ev)
    {
        byte b = data[0];
        
        if (b == Csi)
        {
            // Control sequence introducer
            return ProcessCsi(data, ev);
        }
        
        // Other C1 codes
        switch (b)
        {
            case 0x80: // BKF - Black foreground
                foregroundColor = DefaultPalette[0];
                return 1;
            case 0x81: // RDF - Red foreground
                foregroundColor = DefaultPalette[1];
                return 1;
            case 0x82: // GRF - Green foreground
                foregroundColor = DefaultPalette[2];
                return 1;
            case 0x83: // YLF - Yellow foreground
                foregroundColor = DefaultPalette[3];
                return 1;
            case 0x84: // BLF - Blue foreground
                foregroundColor = DefaultPalette[4];
                return 1;
            case 0x85: // MGF - Magenta foreground
                foregroundColor = DefaultPalette[5];
                return 1;
            case 0x86: // CNF - Cyan foreground
                foregroundColor = DefaultPalette[6];
                return 1;
            case 0x87: // WHF - White foreground
                foregroundColor = DefaultPalette[7];
                return 1;
            case 0x88: // SSZ - Small size
                fontHeight = profile == AribProfile.AProfile ? 18 : 8;
                fontWidth = fontHeight;
                return 1;
            case 0x89: // MSZ - Middle size
                fontHeight = profile == AribProfile.AProfile ? 18 : 8;
                fontWidth = fontHeight / 2;
                return 1;
            case 0x8A: // NSZ - Normal size
                fontHeight = profile == AribProfile.AProfile ? 36 : 16;
                fontWidth = fontHeight;
                return 1;
            case 0x8B: // SZX - Character size
                if (data.Length >= 2)
                {
                    // Parameter follows
                    return 2;
                }
                return 1;
            case 0x90: // COL - Color control
            case 0x91: // FLC - Flashing control
            case 0x92: // CDC - Conceal display control
            case 0x93: // POL - Pattern polarity
            case 0x94: // WMM - Writing mode modification
            case 0x95: // MACRO
            case 0x97: // HLC - Highlighting character block
            case 0x98: // RPC - Repeat character
            case 0x99: // SPL - Stop lining
            case 0x9A: // STL - Start lining
            case 0x9B: // CSI (already handled above)
            case 0x9D: // TIME
                // These may have parameters
                return 1;
            default:
                return 1;
        }
    }
    
    private int ProcessEscapeSequence(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return 1;
        
        byte b1 = data[1];
        
        // LS2 and LS3 (locking shifts)
        if (b1 == 0x6E) { glSet = 2; return 2; }  // LS2
        if (b1 == 0x6F) { glSet = 3; return 2; }  // LS3
        
        // LS1R, LS2R, LS3R (locking shifts to GR)
        if (b1 == 0x7E) { grSet = 1; return 2; }  // LS1R
        if (b1 == 0x7D) { grSet = 2; return 2; }  // LS2R
        if (b1 == 0x7C) { grSet = 3; return 2; }  // LS3R
        
        // Character set designation
        if (b1 == 0x24)  // 2-byte set designation
        {
            if (data.Length < 3)
                return 2;
            
            byte b2 = data[2];
            if (b2 >= 0x28 && b2 <= 0x2B)  // G0-G3
            {
                if (data.Length < 4)
                    return 3;
                
                int gx = b2 - 0x28;
                AribCharacterSet cs = (AribCharacterSet)data[3];
                SetCharacterSet(gx, cs);
                return 4;
            }
            else
            {
                // Direct designation to G0
                AribCharacterSet cs = (AribCharacterSet)b2;
                SetCharacterSet(0, cs);
                return 3;
            }
        }
        else if (b1 >= 0x28 && b1 <= 0x2B)  // 1-byte set designation to G0-G3
        {
            if (data.Length < 3)
                return 2;
            
            int gx = b1 - 0x28;
            AribCharacterSet cs = (AribCharacterSet)data[2];
            SetCharacterSet(gx, cs);
            return 3;
        }
        
        return 2;
    }
    
    private int ProcessCsi(ReadOnlySpan<byte> data, AribEvent ev)
    {
        // Find end of CSI sequence (terminated by 0x40-0x7E)
        int i = 1;
        while (i < data.Length && (data[i] < 0x40 || data[i] > 0x7E))
            i++;
        
        if (i >= data.Length)
            return data.Length;
        
        // Parse CSI parameters
        byte finalByte = data[i];
        
        switch (finalByte)
        {
            case 0x53: // SWF - Set writing format
            case 0x54: // CCC - Composite character composition
            case 0x56: // SDF - Set display format
            case 0x57: // SSM - Character composition dot designation
            case 0x58: // SHS - Set horizontal spacing
            case 0x59: // SVS - Set vertical spacing
            case 0x5B: // PLD - Partial line down
            case 0x5C: // PLU - Partial line up
            case 0x5D: // GAA - Coloring block
            case 0x5E: // SRC - Raster color designation
            case 0x5F: // SDP - Set display position
            case 0x61: // ACPS - Active coordinate position set
            case 0x62: // TCC - Time control code
            case 0x63: // ORN - Ornament control
            case 0x64: // MDF - Font
            case 0x65: // CFS - Character font set
            case 0x66: // XCS
            case 0x68: // PRA - Built-in sound replay
            case 0x69: // ACS - Alternative character set
            case 0x6E: // RCS - Raster color command
            case 0x6F: // SCS
                break;
        }
        
        return i + 1;
    }
    
    private void SetCharacterSet(int gx, AribCharacterSet cs)
    {
        switch (gx)
        {
            case 0: g0 = cs; break;
            case 1: g1 = cs; break;
            case 2: g2 = cs; break;
            case 3: g3 = cs; break;
        }
    }
    
    private AribCharacterSet GetCurrentGlSet()
    {
        if (singleShift >= 0)
        {
            int ss = singleShift;
            singleShift = -1;
            return ss switch
            {
                2 => g2,
                3 => g3,
                _ => GetGSet(glSet)
            };
        }
        return GetGSet(glSet);
    }
    
    private AribCharacterSet GetCurrentGrSet()
    {
        return GetGSet(grSet);
    }
    
    private AribCharacterSet GetGSet(int gx)
    {
        return gx switch
        {
            0 => g0,
            1 => g1,
            2 => g2,
            3 => g3,
            _ => g0
        };
    }
    
    private int DecodeGlCharacter(ReadOnlySpan<byte> data)
    {
        var cs = GetCurrentGlSet();
        return DecodeCharacter(data, cs, 0x00);
    }
    
    private int DecodeGrCharacter(ReadOnlySpan<byte> data)
    {
        var cs = GetCurrentGrSet();
        return DecodeCharacter(data, cs, 0x80);
    }
    
    private int DecodeCharacter(ReadOnlySpan<byte> data, AribCharacterSet cs, byte mask)
    {
        if (data.Length == 0)
            return 0;
        
        byte b1 = (byte)(data[0] & 0x7F);
        
        // Check if this is a 2-byte character set
        bool isTwoByte = cs == AribCharacterSet.Kanji || 
                          cs == AribCharacterSet.Drcs0 ||
                          cs == AribCharacterSet.AdditionalSymbols;
        
        if (isTwoByte)
        {
            if (data.Length < 2)
                return 1;
            
            byte b2 = (byte)(data[1] & 0x7F);
            char c = DecodeKanjiChar(b1, b2);
            if (c != '\0')
            {
                textBuffer.Append(c);
                charLeft += fontWidth + horizontalInterval;
            }
            return 2;
        }
        else
        {
            // Single-byte character set
            char c = DecodeSingleByteChar(b1, cs);
            if (c != '\0')
            {
                textBuffer.Append(c);
                charLeft += fontWidth + horizontalInterval;
            }
            return 1;
        }
    }
    
    private static char DecodeKanjiChar(byte b1, byte b2)
    {
        // Convert JIS X 0208 to Unicode
        // Row = b1 - 0x21, Cell = b2 - 0x21
        // Then use Shift_JIS or direct JIS to Unicode mapping
        
        int row = b1 - 0x21;
        int cell = b2 - 0x21;
        
        if (row < 0 || row > 93 || cell < 0 || cell > 93)
            return '\0';
        
        // Convert to Shift_JIS and then to Unicode
        int sjis;
        if (row <= 61)
        {
            sjis = ((row / 2) + (row < 62 ? 0x81 : 0xC1)) << 8;
            if ((row & 1) == 0)
                sjis |= cell + (cell < 63 ? 0x40 : 0x41);
            else
                sjis |= cell + 0x9F;
        }
        else
        {
            sjis = ((row - 62) / 2 + 0xE0) << 8;
            if ((row & 1) == 0)
                sjis |= cell + (cell < 63 ? 0x40 : 0x41);
            else
                sjis |= cell + 0x9F;
        }
        
        // Use .NET's encoding to convert
        try
        {
            var bytes = new byte[] { (byte)(sjis >> 8), (byte)(sjis & 0xFF) };
            var chars = Encoding.GetEncoding(932).GetString(bytes);
            return chars.Length > 0 ? chars[0] : '\0';
        }
        catch
        {
            return '\0';
        }
    }
    
    private static char DecodeSingleByteChar(byte b, AribCharacterSet cs)
    {
        switch (cs)
        {
            case AribCharacterSet.Alphanumeric:
            case AribCharacterSet.ProportionalAlphanumeric:
                // JIS X 0201 Roman (similar to ASCII)
                if (b == 0x5C) return '¥';  // Yen sign
                if (b == 0x7E) return '‾';  // Overline
                if (b >= 0x21 && b <= 0x7E)
                    return (char)b;
                return '\0';
            
            case AribCharacterSet.Hiragana:
            case AribCharacterSet.ProportionalHiragana:
                // Hiragana (JIS X 0208 row 4 layout)
                if (b >= 0x21 && b <= 0x73)
                    return (char)(0x3041 + (b - 0x21));  // Unicode Hiragana block
                return '\0';
            
            case AribCharacterSet.Katakana:
            case AribCharacterSet.ProportionalKatakana:
                // Katakana (JIS X 0208 row 5 layout)
                if (b >= 0x21 && b <= 0x76)
                    return (char)(0x30A1 + (b - 0x21));  // Unicode Katakana block
                return '\0';
            
            case AribCharacterSet.JisKatakana:
                // JIS X 0201 Katakana (half-width)
                if (b >= 0x21 && b <= 0x5F)
                    return (char)(0xFF61 + (b - 0x21));  // Unicode half-width Katakana
                return '\0';
            
            case AribCharacterSet.MosaicA:
            case AribCharacterSet.MosaicB:
            case AribCharacterSet.MosaicC:
            case AribCharacterSet.MosaicD:
                // Mosaic graphics - return replacement character
                return '□';
            
            default:
                return '\0';
        }
    }
    
    private void FlushTextRegion(AribEvent ev)
    {
        if (textBuffer.Length == 0)
            return;
        
        var region = new AribTextRegion
        {
            Text = textBuffer.ToString(),
            ForegroundColor = foregroundColor,
            BackgroundColor = backgroundColor,
            PlaneWidth = planeWidth,
            PlaneHeight = planeHeight,
            FontWidth = fontWidth,
            FontHeight = fontHeight,
            HorizontalInterval = horizontalInterval,
            VerticalInterval = verticalInterval,
            CharLeft = charLeft - (textBuffer.Length * (fontWidth + horizontalInterval)),
            CharBottom = charBottom,
            IsRuby = fontHeight == 18  // Ruby text typically uses smaller font
        };
        
        ev.Regions.Add(region);
        textBuffer.Clear();
    }
}

/// <summary>
/// Binary reader extensions for ARIB data.
/// </summary>
file static class BinaryPrimitivesExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadUInt24BigEndian(ReadOnlySpan<byte> source)
    {
        return (source[0] << 16) | (source[1] << 8) | source[2];
    }
}

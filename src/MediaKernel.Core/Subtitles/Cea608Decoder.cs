// MediaKernel - CEA-608 Closed Caption Decoder
// Decodes EIA-608 closed captions embedded in video streams.
// Reference: VLC cc.c, EIA/CEA-608-B standard
//
// Supports:
// - Pop-up captions (CC1/CC2)
// - Roll-up captions (2/3/4 rows)
// - Paint-on captions
// - Text mode
// - Styling (colors, italics, underline)
// - Extended characters (Spanish, French, Portuguese, German, Danish)
// - Special characters (registered, copyright, trademark, etc.)

using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// CEA-608 display mode.
/// </summary>
public enum Cea608Mode
{
    PopUp = 0,
    RollUp2 = 1,
    RollUp3 = 2,
    RollUp4 = 3,
    PaintOn = 4,
    Text = 5
}

/// <summary>
/// CEA-608 text colors.
/// </summary>
public enum Cea608Color
{
    White = 0,
    Green = 1,
    Blue = 2,
    Cyan = 3,
    Red = 4,
    Yellow = 5,
    Magenta = 6,
    Black = 7
}

/// <summary>
/// CEA-608 font styles.
/// </summary>
[Flags]
public enum Cea608Font
{
    Regular = 0x00,
    Italics = 0x01,
    Underline = 0x02,
    ItalicsUnderline = Italics | Underline
}

/// <summary>
/// Represents a CEA-608 screen buffer.
/// </summary>
public sealed class Cea608Screen
{
    public const int Rows = 15;
    public const int Columns = 32;
    
    private readonly char[,] characters = new char[Rows, Columns + 1];
    private readonly Cea608Color[,] colors = new Cea608Color[Rows, Columns + 1];
    private readonly Cea608Font[,] fonts = new Cea608Font[Rows, Columns + 1];
    private readonly bool[] rowUsed = new bool[Rows];
    
    public Cea608Screen()
    {
        Clear();
    }
    
    public char GetCharacter(int row, int col) => characters[row, col];
    public Cea608Color GetColor(int row, int col) => colors[row, col];
    public Cea608Font GetFont(int row, int col) => fonts[row, col];
    public bool IsRowUsed(int row) => rowUsed[row];
    
    public void SetCell(int row, int col, char c, Cea608Color color, Cea608Font font)
    {
        characters[row, col] = c;
        colors[row, col] = color;
        fonts[row, col] = font;
        rowUsed[row] = true;
    }
    
    public void ClearRow(int row, int fromCol = 0)
    {
        for (int col = fromCol; col <= Columns; col++)
        {
            characters[row, col] = col < Columns ? ' ' : '\0';
            colors[row, col] = Cea608Color.White;
            fonts[row, col] = Cea608Font.Regular;
        }
        
        // Check if row still has content
        if (fromCol == 0)
        {
            rowUsed[row] = false;
        }
        else
        {
            rowUsed[row] = false;
            for (int col = 0; col < fromCol; col++)
            {
                if (characters[row, col] != ' ')
                {
                    rowUsed[row] = true;
                    break;
                }
            }
        }
    }
    
    public void Clear()
    {
        for (int row = 0; row < Rows; row++)
            ClearRow(row);
    }
    
    public void CopyFrom(Cea608Screen other)
    {
        Array.Copy(other.characters, characters, characters.Length);
        Array.Copy(other.colors, colors, colors.Length);
        Array.Copy(other.fonts, fonts, fonts.Length);
        Array.Copy(other.rowUsed, rowUsed, rowUsed.Length);
    }
    
    /// <summary>
    /// Scrolls the screen up by one row.
    /// </summary>
    public void ScrollUp(int baseRow, int numRows)
    {
        if (numRows < 2) return;
        
        int startRow = baseRow - numRows + 1;
        if (startRow < 0) startRow = 0;
        
        // Move rows up
        for (int row = startRow; row < baseRow; row++)
        {
            for (int col = 0; col <= Columns; col++)
            {
                characters[row, col] = characters[row + 1, col];
                colors[row, col] = colors[row + 1, col];
                fonts[row, col] = fonts[row + 1, col];
            }
            rowUsed[row] = rowUsed[row + 1];
        }
        
        // Clear bottom row
        ClearRow(baseRow);
    }
    
    /// <summary>
    /// Extracts visible text from the screen.
    /// </summary>
    public string GetText()
    {
        var sb = new StringBuilder();
        bool first = true;
        
        for (int row = 0; row < Rows; row++)
        {
            if (!rowUsed[row]) continue;
            
            if (!first) sb.Append('\n');
            first = false;
            
            // Find line extent
            int start = 0, end = Columns - 1;
            while (start < Columns && characters[row, start] == ' ') start++;
            while (end > start && characters[row, end] == ' ') end--;
            
            for (int col = start; col <= end; col++)
            {
                sb.Append(characters[row, col]);
            }
        }
        
        return sb.ToString();
    }
}

/// <summary>
/// Status flags returned from CEA-608 parsing.
/// </summary>
[Flags]
public enum Cea608Status
{
    None = 0,
    Changed = 0x01,
    CaptionEnded = 0x02,
    CaptionCleared = 0x04,
    Display = CaptionCleared | CaptionEnded
}

/// <summary>
/// CEA-608 closed caption decoder.
/// </summary>
public sealed class Cea608Decoder
{
    // Character lookup tables
    private static readonly char[] BasicChars = CreateBasicChars();
    private static readonly char[] SpecialChars = 
    [
        '®', '°', '½', '¿', '™', '¢', '£', '♪',
        'à', ' ', 'è', 'â', 'ê', 'î', 'ô', 'û'
    ];
    private static readonly char[] ExtendedSpanishFrench =
    [
        'Á', 'É', 'Ó', 'Ú', 'Ü', 'ü', '\u2019', '¡',
        '*', '\'', '-', '©', '\u2120', '•', '"', '"',
        'À', 'Â', 'Ç', 'È', 'Ê', 'Ë', 'ë', 'Î',
        'Ï', 'ï', 'Ô', 'Ù', 'ù', 'Û', '«', '»'
    ];
    private static readonly char[] ExtendedPortugueseGerman =
    [
        'Ã', 'ã', 'Í', 'Ì', 'ì', 'Ò', 'ò', 'Õ',
        'õ', '{', '}', '\\', '^', '_', '|', '~',
        'Ä', 'ä', 'Ö', 'ö', 'ß', '¥', '¤', '│',
        'Å', 'å', 'Ø', 'ø', '┌', '┐', '└', '┘'
    ];
    
    private int channel;
    private int displayedScreen;
    private readonly Cea608Screen[] screens = [new Cea608Screen(), new Cea608Screen()];
    private int cursorRow;
    private int cursorColumn;
    private Cea608Mode mode = Cea608Mode.PopUp;
    private Cea608Color color = Cea608Color.White;
    private Cea608Font font = Cea608Font.Regular;
    private int rollUpBaseRow = 14; // Default bottom row
    
    // Duplicate command detection
    private byte lastD1;
    private byte lastD2;
    
    /// <summary>
    /// Gets the currently displayed caption text.
    /// </summary>
    public string CurrentText => screens[displayedScreen].GetText();
    
    /// <summary>
    /// Gets the current display mode.
    /// </summary>
    public Cea608Mode Mode => mode;
    
    /// <summary>
    /// Creates a new CEA-608 decoder for the specified channel.
    /// </summary>
    /// <param name="channelNumber">Channel 1-4 (CC1-CC4)</param>
    public Cea608Decoder(int channelNumber = 1)
    {
        channel = Math.Clamp(channelNumber, 1, 4);
        Reset();
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        screens[0].Clear();
        screens[1].Clear();
        displayedScreen = 0;
        cursorRow = 14;
        cursorColumn = 0;
        mode = Cea608Mode.PopUp;
        color = Cea608Color.White;
        font = Cea608Font.Regular;
        lastD1 = 0;
        lastD2 = 0;
    }
    
    /// <summary>
    /// Parses a CEA-608 byte pair.
    /// </summary>
    /// <param name="data">Two-byte CEA-608 data</param>
    /// <param name="targetChannel">Channel to decode (1-4)</param>
    /// <returns>Status flags indicating what changed</returns>
    public Cea608Status Parse(ReadOnlySpan<byte> data, int targetChannel = 1)
    {
        if (data.Length < 2)
            return Cea608Status.None;
        
        return Parse(data[0], data[1], targetChannel);
    }
    
    /// <summary>
    /// Parses a CEA-608 byte pair.
    /// </summary>
    public Cea608Status Parse(byte d1, byte d2, int targetChannel = 1)
    {
        // Strip parity bits
        d1 &= 0x7F;
        d2 &= 0x7F;
        
        // Check for null padding
        if (d1 == 0 && d2 == 0)
            return Cea608Status.None;
        
        // Duplicate command detection
        if (d1 == lastD1 && d2 == lastD2)
        {
            // Clear last command - only reject first duplicate
            lastD1 = 0;
            lastD2 = 0;
            return Cea608Status.None;
        }
        
        var status = Cea608Status.None;
        
        // Control codes have odd parity in d1
        if ((d1 >= 0x10 && d1 <= 0x1F))
        {
            // Store for duplicate detection
            lastD1 = d1;
            lastD2 = d2;
            
            // Check channel
            int packetChannel = 1 + ((d1 >= 0x18) ? 2 : 0) + ((d2 >= 0x40) ? 0 : (d1 & 0x01));
            
            // Only process if this is our channel
            if ((d1 & 0x01) == ((targetChannel - 1) & 0x01) ||
                d1 >= 0x18 == (targetChannel >= 3))
            {
                status = ParseControlCode(d1, d2);
            }
        }
        else if (d1 >= 0x20)
        {
            // Basic characters
            lastD1 = 0;
            lastD2 = 0;
            
            // Write first character
            if (d1 >= 0x20 && d1 <= 0x7F)
            {
                WriteCharacter(BasicChars[d1 - 0x20]);
                status = Cea608Status.Changed;
            }
            
            // Write second character
            if (d2 >= 0x20 && d2 <= 0x7F)
            {
                WriteCharacter(BasicChars[d2 - 0x20]);
                status = Cea608Status.Changed;
            }
        }
        
        return status;
    }
    
    private Cea608Status ParseControlCode(byte d1, byte d2)
    {
        // Preamble Address Codes (PAC)
        if ((d1 & 0x70) == 0x10 && (d2 & 0x40) == 0x40)
        {
            return ParsePac(d1, d2);
        }
        
        // Mid-row codes
        if ((d1 == 0x11 || d1 == 0x19) && (d2 >= 0x20 && d2 <= 0x2F))
        {
            return ParseMidRow(d2);
        }
        
        // Special characters
        if ((d1 == 0x11 || d1 == 0x19) && (d2 >= 0x30 && d2 <= 0x3F))
        {
            // Backspace and replace
            MoveCursor(-1);
            WriteCharacter(SpecialChars[d2 - 0x30]);
            return Cea608Status.Changed;
        }
        
        // Extended characters (Spanish/French)
        if ((d1 == 0x12 || d1 == 0x1A) && (d2 >= 0x20 && d2 <= 0x3F))
        {
            MoveCursor(-1);
            WriteCharacter(ExtendedSpanishFrench[d2 - 0x20]);
            return Cea608Status.Changed;
        }
        
        // Extended characters (Portuguese/German/Danish)
        if ((d1 == 0x13 || d1 == 0x1B) && (d2 >= 0x20 && d2 <= 0x3F))
        {
            MoveCursor(-1);
            WriteCharacter(ExtendedPortugueseGerman[d2 - 0x20]);
            return Cea608Status.Changed;
        }
        
        // Miscellaneous control codes
        if ((d1 == 0x14 || d1 == 0x1C || d1 == 0x15 || d1 == 0x1D) && 
            (d2 >= 0x20 && d2 <= 0x2F))
        {
            return ParseMiscControl(d1, d2);
        }
        
        return Cea608Status.None;
    }
    
    private Cea608Status ParsePac(byte d1, byte d2)
    {
        // Determine row from PAC
        int row = GetRowFromPac(d1, d2);
        
        // Get attributes
        int attrIndex = d2 & 0x0F;
        if ((d2 & 0x10) != 0) attrIndex += 16;
        
        // Set cursor position
        cursorRow = row;
        
        // Column and style from lookup
        if (attrIndex < 16)
        {
            // Color + underline
            color = (Cea608Color)(attrIndex >> 1);
            font = ((attrIndex & 1) != 0) ? Cea608Font.Underline : Cea608Font.Regular;
            
            // Set cursor to left margin based on mode
            if (mode == Cea608Mode.PopUp || mode == Cea608Mode.PaintOn)
                cursorColumn = 0;
        }
        else
        {
            // Indent + underline
            cursorColumn = ((attrIndex - 16) >> 1) * 4;
            font = ((attrIndex & 1) != 0) ? Cea608Font.Underline : Cea608Font.Regular;
            color = Cea608Color.White;
        }
        
        // Handle roll-up base row
        if (mode is Cea608Mode.RollUp2 or Cea608Mode.RollUp3 or Cea608Mode.RollUp4)
        {
            rollUpBaseRow = row;
        }
        
        return Cea608Status.Changed;
    }
    
    private static int GetRowFromPac(byte d1, byte d2)
    {
        // Row mapping from EIA-608 spec
        int rowCode = ((d1 & 0x07) << 1) | ((d2 & 0x20) >> 5);
        return rowCode switch
        {
            0 => 0,   // Row 1
            1 => 1,   // Row 2
            2 => 2,   // Row 3
            3 => 3,   // Row 4
            4 => 4,   // Row 5
            5 => 5,   // Row 6
            6 => 6,   // Row 7
            7 => 7,   // Row 8
            8 => 8,   // Row 9
            9 => 9,   // Row 10
            10 => 10, // Row 11
            11 => 12, // Row 13
            12 => 13, // Row 14
            13 => 14, // Row 15
            14 => 10, // Row 11
            15 => 11, // Row 12
            _ => 14
        };
    }
    
    private Cea608Status ParseMidRow(byte d2)
    {
        // Space with style change
        WriteCharacter(' ');
        
        int code = d2 & 0x0F;
        if (code <= 0x0D)
        {
            color = (Cea608Color)(code >> 1);
            font = ((code & 1) != 0) ? Cea608Font.Underline : Cea608Font.Regular;
        }
        else if (code == 0x0E)
        {
            font = Cea608Font.Italics;
        }
        else
        {
            font = Cea608Font.ItalicsUnderline;
        }
        
        return Cea608Status.Changed;
    }
    
    private Cea608Status ParseMiscControl(byte d1, byte d2)
    {
        switch (d2)
        {
            case 0x20: // Resume Caption Loading (RCL)
                mode = Cea608Mode.PopUp;
                break;
                
            case 0x21: // Backspace (BS)
                Backspace();
                return Cea608Status.Changed;
                
            case 0x22: // Reserved (AOF)
            case 0x23: // Reserved (AON)
                break;
                
            case 0x24: // Delete to End of Row (DER)
                DeleteToEndOfRow();
                return Cea608Status.Changed;
                
            case 0x25: // Roll-Up 2 rows
                SwitchToRollUp(2);
                break;
                
            case 0x26: // Roll-Up 3 rows
                SwitchToRollUp(3);
                break;
                
            case 0x27: // Roll-Up 4 rows
                SwitchToRollUp(4);
                break;
                
            case 0x28: // Flash On
                break;
                
            case 0x29: // Resume Direct Captioning (RDC)
                mode = Cea608Mode.PaintOn;
                break;
                
            case 0x2A: // Text Restart (TR)
                mode = Cea608Mode.Text;
                break;
                
            case 0x2B: // Resume Text Display (RTD)
                mode = Cea608Mode.Text;
                break;
                
            case 0x2C: // Erase Displayed Memory (EDM)
                screens[displayedScreen].Clear();
                return Cea608Status.CaptionCleared;
                
            case 0x2D: // Carriage Return (CR)
                CarriageReturn();
                return Cea608Status.Changed;
                
            case 0x2E: // Erase Non-Displayed Memory (ENM)
                screens[1 - displayedScreen].Clear();
                break;
                
            case 0x2F: // End of Caption (EOC) - flip buffers
                FlipBuffers();
                return Cea608Status.CaptionEnded;
        }
        
        return Cea608Status.None;
    }
    
    private void WriteCharacter(char c)
    {
        if (mode == Cea608Mode.Text)
            return;
        
        int screenIndex = GetWritingScreenIndex();
        screens[screenIndex].SetCell(cursorRow, cursorColumn, c, color, font);
        MoveCursor(1);
    }
    
    private int GetWritingScreenIndex()
    {
        return mode switch
        {
            Cea608Mode.PopUp => 1 - displayedScreen,
            _ => displayedScreen
        };
    }
    
    private void MoveCursor(int delta)
    {
        cursorColumn += delta;
        if (cursorColumn < 0) cursorColumn = 0;
        if (cursorColumn >= Cea608Screen.Columns) cursorColumn = Cea608Screen.Columns - 1;
    }
    
    private void Backspace()
    {
        MoveCursor(-1);
        int screenIndex = GetWritingScreenIndex();
        screens[screenIndex].SetCell(cursorRow, cursorColumn, ' ', color, font);
    }
    
    private void DeleteToEndOfRow()
    {
        int screenIndex = GetWritingScreenIndex();
        screens[screenIndex].ClearRow(cursorRow, cursorColumn);
    }
    
    private void SwitchToRollUp(int rows)
    {
        mode = rows switch
        {
            2 => Cea608Mode.RollUp2,
            3 => Cea608Mode.RollUp3,
            _ => Cea608Mode.RollUp4
        };
        
        // Roll-up always works on displayed buffer
        // Clear non-displayed buffer
        screens[1 - displayedScreen].Clear();
    }
    
    private void CarriageReturn()
    {
        if (mode is not (Cea608Mode.RollUp2 or Cea608Mode.RollUp3 or Cea608Mode.RollUp4))
            return;
        
        int numRows = mode switch
        {
            Cea608Mode.RollUp2 => 2,
            Cea608Mode.RollUp3 => 3,
            _ => 4
        };
        
        screens[displayedScreen].ScrollUp(rollUpBaseRow, numRows);
        cursorColumn = 0;
    }
    
    private void FlipBuffers()
    {
        displayedScreen = 1 - displayedScreen;
    }
    
    private static char[] CreateBasicChars()
    {
        var chars = new char[96];
        for (int i = 0; i < 96; i++)
        {
            chars[i] = (char)(0x20 + i);
        }
        
        // Special character overrides for CEA-608
        chars['*' - 0x20] = 'á'; // 0x2A -> á
        chars['\\' - 0x20] = 'é'; // 0x5C -> é
        chars['^' - 0x20] = 'í'; // 0x5E -> í
        chars['_' - 0x20] = 'ó'; // 0x5F -> ó
        chars['`' - 0x20] = 'ú'; // 0x60 -> ú
        chars['{' - 0x20] = 'ç'; // 0x7B -> ç
        chars['|' - 0x20] = '÷'; // 0x7C -> ÷
        chars['}' - 0x20] = 'Ñ'; // 0x7D -> Ñ
        chars['~' - 0x20] = 'ñ'; // 0x7E -> ñ
        chars[0x7F - 0x20] = '█'; // 0x7F -> solid block
        
        return chars;
    }
    
    /// <summary>
    /// Creates a subtitle cue from current displayed captions.
    /// </summary>
    public SubtitleCue CreateCue(long startMicroseconds, long endMicroseconds)
    {
        return new SubtitleCue(
            startMicroseconds,
            endMicroseconds,
            CurrentText,
            SubtitleType.Text);
    }
}

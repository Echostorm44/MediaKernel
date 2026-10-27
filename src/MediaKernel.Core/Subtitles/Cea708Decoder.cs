// CEA-708 Closed Caption Decoder
// Reference: VLC's modules/codec/cea708.c, ANSI/CTA-708-E, FCC 47 CFR 79.102

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Pen/character size for CEA-708 captions.
/// </summary>
public enum Cea708PenSize
{
    Small = 0,
    Standard = 1,
    Large = 2
}

/// <summary>
/// Font type for CEA-708 captions.
/// </summary>
public enum Cea708FontType
{
    Default = 0,
    MonospacedSerif = 1,
    ProportionalSerif = 2,
    MonospacedSansSerif = 3,
    ProportionalSansSerif = 4,
    Casual = 5,
    Cursive = 6,
    SmallCaps = 7
}

/// <summary>
/// Text offset (subscript/superscript) for CEA-708 captions.
/// </summary>
public enum Cea708TextOffset
{
    Subscript = 0,
    Normal = 1,
    Superscript = 2
}

/// <summary>
/// Opacity level for colors in CEA-708 captions.
/// </summary>
public enum Cea708Opacity
{
    Solid = 0,
    Flash = 1,
    Translucent = 2,
    Transparent = 3
}

/// <summary>
/// Edge type for characters in CEA-708 captions.
/// </summary>
public enum Cea708EdgeType
{
    None = 0,
    Raised = 1,
    Depressed = 2,
    Uniform = 3,
    DropShadowLeft = 4,
    DropShadowRight = 5
}

/// <summary>
/// Text justification for CEA-708 windows.
/// </summary>
public enum Cea708Justification
{
    Left = 0,
    Right = 1,
    Center = 2,
    Full = 3
}

/// <summary>
/// Print direction for CEA-708 windows.
/// </summary>
public enum Cea708Direction
{
    LeftToRight = 0,
    RightToLeft = 1,
    TopToBottom = 2,
    BottomToTop = 3
}

/// <summary>
/// Window anchor points for CEA-708 positioning.
/// </summary>
public enum Cea708AnchorPoint
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    MiddleLeft = 3,
    MiddleCenter = 4,
    MiddleRight = 5,
    BottomLeft = 6,
    BottomCenter = 7,
    BottomRight = 8
}

/// <summary>
/// Pen style (text attributes) for CEA-708 captions.
/// </summary>
public struct Cea708PenStyle
{
    public Cea708PenSize Size;
    public Cea708FontType Font;
    public Cea708TextOffset Offset;
    public bool Italics;
    public bool Underline;
    public Cea708EdgeType EdgeType;
    public byte ForegroundColor;      // 6-bit color (RGB 2-2-2)
    public Cea708Opacity ForegroundOpacity;
    public byte BackgroundColor;
    public Cea708Opacity BackgroundOpacity;
    public byte EdgeColor;
    
    public static Cea708PenStyle Default => new()
    {
        Size = Cea708PenSize.Standard,
        Font = Cea708FontType.Default,
        Offset = Cea708TextOffset.Normal,
        Italics = false,
        Underline = false,
        EdgeType = Cea708EdgeType.None,
        ForegroundColor = 0x3F,       // White (RGB 3-3-3)
        ForegroundOpacity = Cea708Opacity.Solid,
        BackgroundColor = 0x00,       // Black
        BackgroundOpacity = Cea708Opacity.Solid,
        EdgeColor = 0x00
    };
    
    public uint ToArgbForeground()
    {
        return ColorToArgb(ForegroundColor, ForegroundOpacity);
    }
    
    public uint ToArgbBackground()
    {
        return ColorToArgb(BackgroundColor, BackgroundOpacity);
    }
    
    private static uint ColorToArgb(byte color6bit, Cea708Opacity opacity)
    {
        // Convert 6-bit color (RGB 2-2-2) to ARGB
        int r = ((color6bit >> 4) & 0x03) * 85;  // Scale 0-3 to 0-255
        int g = ((color6bit >> 2) & 0x03) * 85;
        int b = (color6bit & 0x03) * 85;
        
        int a = opacity switch
        {
            Cea708Opacity.Solid => 255,
            Cea708Opacity.Flash => 255,       // Handle flash separately
            Cea708Opacity.Translucent => 128,
            Cea708Opacity.Transparent => 0,
            _ => 255
        };
        
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }
}

/// <summary>
/// Window style for CEA-708 windows.
/// </summary>
public struct Cea708WindowStyle
{
    public Cea708Justification Justify;
    public Cea708Direction PrintDirection;
    public Cea708Direction ScrollDirection;
    public bool WordWrap;
    public byte FillColor;
    public Cea708Opacity FillOpacity;
    public byte BorderColor;
    public byte BorderType;
    
    public static Cea708WindowStyle Default => new()
    {
        Justify = Cea708Justification.Left,
        PrintDirection = Cea708Direction.LeftToRight,
        ScrollDirection = Cea708Direction.BottomToTop,
        WordWrap = false,
        FillColor = 0x00,
        FillOpacity = Cea708Opacity.Solid,
        BorderColor = 0x00,
        BorderType = 0
    };
}

/// <summary>
/// A single character cell in a CEA-708 window.
/// </summary>
public struct Cea708Cell
{
    public char Character;
    public Cea708PenStyle Style;
    
    public bool IsEmpty => Character == '\0' || Character == ' ';
}

/// <summary>
/// CEA-708 window (up to 8 per service).
/// </summary>
public sealed class Cea708Window
{
    public const int MaxRows = 15;
    public const int MaxColumns = 42;
    
    public bool Defined { get; set; }
    public bool Visible { get; set; }
    public int Priority { get; set; }
    public bool RowLock { get; set; }
    public bool ColumnLock { get; set; }
    public bool RelativePositioning { get; set; }
    public int AnchorVertical { get; set; }
    public int AnchorHorizontal { get; set; }
    public Cea708AnchorPoint AnchorPoint { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public int PenStyleId { get; set; }
    public int WindowStyleId { get; set; }
    
    public Cea708PenStyle PenStyle { get; set; }
    public Cea708WindowStyle WindowStyle { get; set; }
    
    public int CursorRow { get; set; }
    public int CursorColumn { get; set; }
    
    private readonly Cea708Cell[,] cells = new Cea708Cell[MaxRows, MaxColumns];
    
    public Cea708Window()
    {
        Clear();
    }
    
    public void Clear()
    {
        Defined = false;
        Visible = false;
        CursorRow = 0;
        CursorColumn = 0;
        PenStyle = Cea708PenStyle.Default;
        WindowStyle = Cea708WindowStyle.Default;
        
        for (int r = 0; r < MaxRows; r++)
        {
            for (int c = 0; c < MaxColumns; c++)
            {
                cells[r, c] = new Cea708Cell { Character = ' ', Style = Cea708PenStyle.Default };
            }
        }
    }
    
    public void ClearText()
    {
        for (int r = 0; r < MaxRows; r++)
        {
            for (int c = 0; c < MaxColumns; c++)
            {
                cells[r, c] = new Cea708Cell { Character = ' ', Style = PenStyle };
            }
        }
        CursorRow = 0;
        CursorColumn = 0;
    }
    
    public void WriteCharacter(char ch)
    {
        if (CursorRow >= 0 && CursorRow < RowCount && 
            CursorColumn >= 0 && CursorColumn < ColumnCount)
        {
            cells[CursorRow, CursorColumn] = new Cea708Cell { Character = ch, Style = PenStyle };
            AdvanceCursor();
        }
    }
    
    public void Backspace()
    {
        if (WindowStyle.PrintDirection == Cea708Direction.LeftToRight)
        {
            if (CursorColumn > 0)
            {
                CursorColumn--;
                cells[CursorRow, CursorColumn] = new Cea708Cell { Character = ' ', Style = PenStyle };
            }
        }
        else if (WindowStyle.PrintDirection == Cea708Direction.RightToLeft)
        {
            if (CursorColumn < ColumnCount - 1)
            {
                CursorColumn++;
                cells[CursorRow, CursorColumn] = new Cea708Cell { Character = ' ', Style = PenStyle };
            }
        }
    }
    
    public void CarriageReturn()
    {
        // Move to start of next line
        if (WindowStyle.PrintDirection == Cea708Direction.LeftToRight ||
            WindowStyle.PrintDirection == Cea708Direction.RightToLeft)
        {
            CursorRow++;
            CursorColumn = WindowStyle.PrintDirection == Cea708Direction.LeftToRight ? 0 : ColumnCount - 1;
            
            // Scroll if needed
            if (CursorRow >= RowCount)
            {
                ScrollUp();
                CursorRow = RowCount - 1;
            }
        }
    }
    
    public void FormFeed()
    {
        ClearText();
    }
    
    private void AdvanceCursor()
    {
        switch (WindowStyle.PrintDirection)
        {
            case Cea708Direction.LeftToRight:
                CursorColumn++;
                if (CursorColumn >= ColumnCount)
                {
                    if (WindowStyle.WordWrap)
                    {
                        CursorColumn = 0;
                        CursorRow++;
                        if (CursorRow >= RowCount)
                        {
                            ScrollUp();
                            CursorRow = RowCount - 1;
                        }
                    }
                    else
                    {
                        CursorColumn = ColumnCount - 1;
                    }
                }
                break;
                
            case Cea708Direction.RightToLeft:
                CursorColumn--;
                if (CursorColumn < 0)
                {
                    if (WindowStyle.WordWrap)
                    {
                        CursorColumn = ColumnCount - 1;
                        CursorRow++;
                        if (CursorRow >= RowCount)
                        {
                            ScrollUp();
                            CursorRow = RowCount - 1;
                        }
                    }
                    else
                    {
                        CursorColumn = 0;
                    }
                }
                break;
                
            case Cea708Direction.TopToBottom:
                CursorRow++;
                if (CursorRow >= RowCount)
                {
                    CursorRow = RowCount - 1;
                }
                break;
                
            case Cea708Direction.BottomToTop:
                CursorRow--;
                if (CursorRow < 0)
                {
                    CursorRow = 0;
                }
                break;
        }
    }
    
    private void ScrollUp()
    {
        // Shift rows up
        for (int r = 0; r < RowCount - 1; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                cells[r, c] = cells[r + 1, c];
            }
        }
        // Clear last row
        for (int c = 0; c < ColumnCount; c++)
        {
            cells[RowCount - 1, c] = new Cea708Cell { Character = ' ', Style = PenStyle };
        }
    }
    
    /// <summary>
    /// Extracts text content from the window.
    /// </summary>
    public string GetText()
    {
        var sb = new System.Text.StringBuilder();
        
        for (int r = 0; r < RowCount; r++)
        {
            int lastNonSpace = -1;
            for (int c = ColumnCount - 1; c >= 0; c--)
            {
                if (!cells[r, c].IsEmpty)
                {
                    lastNonSpace = c;
                    break;
                }
            }
            
            if (lastNonSpace >= 0)
            {
                for (int c = 0; c <= lastNonSpace; c++)
                {
                    char ch = cells[r, c].Character;
                    sb.Append(ch == '\0' ? ' ' : ch);
                }
            }
            
            if (r < RowCount - 1)
            {
                sb.AppendLine();
            }
        }
        
        return sb.ToString().TrimEnd();
    }
    
    public Cea708Cell GetCell(int row, int column)
    {
        if (row >= 0 && row < MaxRows && column >= 0 && column < MaxColumns)
        {
            return cells[row, column];
        }
        return new Cea708Cell { Character = ' ', Style = Cea708PenStyle.Default };
    }
}

/// <summary>
/// CEA-708 service (caption track).
/// </summary>
public sealed class Cea708Service
{
    public const int WindowCount = 8;
    
    private readonly Cea708Window[] windows = new Cea708Window[WindowCount];
    private int currentWindowId;
    
    public int ServiceNumber { get; }
    public Cea708Window CurrentWindow => windows[currentWindowId];
    
    public Cea708Service(int serviceNumber)
    {
        ServiceNumber = serviceNumber;
        for (int i = 0; i < WindowCount; i++)
        {
            windows[i] = new Cea708Window();
        }
    }
    
    public void SetCurrentWindow(int windowId)
    {
        if (windowId >= 0 && windowId < WindowCount)
        {
            currentWindowId = windowId;
        }
    }
    
    public Cea708Window GetWindow(int windowId)
    {
        if (windowId >= 0 && windowId < WindowCount)
        {
            return windows[windowId];
        }
        return windows[0];
    }
    
    public void ClearWindows(byte windowMask)
    {
        for (int i = 0; i < WindowCount; i++)
        {
            if ((windowMask & (1 << i)) != 0)
            {
                windows[i].ClearText();
            }
        }
    }
    
    public void ShowWindows(byte windowMask)
    {
        for (int i = 0; i < WindowCount; i++)
        {
            if ((windowMask & (1 << i)) != 0)
            {
                windows[i].Visible = true;
            }
        }
    }
    
    public void HideWindows(byte windowMask)
    {
        for (int i = 0; i < WindowCount; i++)
        {
            if ((windowMask & (1 << i)) != 0)
            {
                windows[i].Visible = false;
            }
        }
    }
    
    public void ToggleWindows(byte windowMask)
    {
        for (int i = 0; i < WindowCount; i++)
        {
            if ((windowMask & (1 << i)) != 0)
            {
                windows[i].Visible = !windows[i].Visible;
            }
        }
    }
    
    public void DeleteWindows(byte windowMask)
    {
        for (int i = 0; i < WindowCount; i++)
        {
            if ((windowMask & (1 << i)) != 0)
            {
                windows[i].Clear();
            }
        }
    }
    
    public void Reset()
    {
        currentWindowId = 0;
        for (int i = 0; i < WindowCount; i++)
        {
            windows[i].Clear();
        }
    }
    
    /// <summary>
    /// Gets combined text from all visible windows.
    /// </summary>
    public string GetVisibleText()
    {
        var texts = new List<string>();
        
        for (int i = 0; i < WindowCount; i++)
        {
            if (windows[i].Visible && windows[i].Defined)
            {
                string text = windows[i].GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
            }
        }
        
        return string.Join("\n", texts);
    }
}

/// <summary>
/// CEA-708 Digital Television Closed Caption (DTVCC) decoder.
/// Decodes CEA-708 caption data from ATSC streams.
/// </summary>
public sealed class Cea708Decoder
{
    public const int MaxServices = 63;
    
    private readonly Cea708Service[] services = new Cea708Service[MaxServices];
    private readonly byte[] inputBuffer = new byte[128];
    private int bufferStart;
    private int bufferLength;
    private int currentServiceNumber;
    private bool needOutput;
    
    /// <summary>
    /// Event raised when caption text changes.
    /// </summary>
    public event Action<int, string>? CaptionChanged;
    
    public Cea708Decoder()
    {
        for (int i = 0; i < MaxServices; i++)
        {
            services[i] = new Cea708Service(i + 1);
        }
    }
    
    /// <summary>
    /// Decodes a DTVCC packet containing caption data.
    /// </summary>
    /// <param name="data">Raw DTVCC packet data</param>
    public void DecodePacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return;
        
        int pos = 0;
        
        // Parse service blocks
        while (pos < data.Length)
        {
            if (pos + 1 > data.Length) break;
            
            byte header = data[pos++];
            int serviceNumber = (header >> 5) & 0x07;
            int blockSize = header & 0x1F;
            
            if (serviceNumber == 0x07 && pos < data.Length)
            {
                // Extended service number
                byte extHeader = data[pos++];
                serviceNumber = extHeader & 0x3F;
            }
            
            if (blockSize == 0 || pos + blockSize > data.Length)
            {
                break;
            }
            
            if (serviceNumber > 0 && serviceNumber <= MaxServices)
            {
                currentServiceNumber = serviceNumber;
                DecodeServiceBlock(data.Slice(pos, blockSize));
            }
            
            pos += blockSize;
        }
    }
    
    /// <summary>
    /// Decodes CEA-708 caption data embedded in video frames.
    /// </summary>
    /// <param name="ccData">CC data bytes (field, cc1, cc2 triplets)</param>
    public void DecodeCcData(ReadOnlySpan<byte> ccData)
    {
        // CC data comes in 3-byte packets: marker, cc_data_1, cc_data_2
        for (int i = 0; i + 2 < ccData.Length; i += 3)
        {
            byte marker = ccData[i];
            byte cc1 = ccData[i + 1];
            byte cc2 = ccData[i + 2];
            
            // Check valid marker (bit 2 indicates valid)
            if ((marker & 0x04) == 0) continue;
            
            int ccType = marker & 0x03;
            
            // Type 0-1: CEA-608 (Line 21), Type 2-3: CEA-708 DTVCC
            if (ccType >= 2)
            {
                // Add to input buffer for DTVCC processing
                AddToBuffer(cc1);
                AddToBuffer(cc2);
            }
        }
        
        // Process buffered data
        ProcessBuffer();
    }
    
    /// <summary>
    /// Gets the caption text for a specific service.
    /// </summary>
    public string GetText(int serviceNumber = 1)
    {
        if (serviceNumber >= 1 && serviceNumber <= MaxServices)
        {
            return services[serviceNumber - 1].GetVisibleText();
        }
        return string.Empty;
    }
    
    /// <summary>
    /// Resets all caption state.
    /// </summary>
    public void Reset()
    {
        bufferStart = 0;
        bufferLength = 0;
        currentServiceNumber = 1;
        needOutput = false;
        
        for (int i = 0; i < MaxServices; i++)
        {
            services[i].Reset();
        }
    }
    
    private void AddToBuffer(byte b)
    {
        if (bufferLength < inputBuffer.Length)
        {
            int pos = (bufferStart + bufferLength) % inputBuffer.Length;
            inputBuffer[pos] = b;
            bufferLength++;
        }
    }
    
    private byte PeekBuffer(int offset)
    {
        if (offset < bufferLength)
        {
            return inputBuffer[(bufferStart + offset) % inputBuffer.Length];
        }
        return 0;
    }
    
    private void ConsumeBuffer(int count)
    {
        if (count <= bufferLength)
        {
            bufferStart = (bufferStart + count) % inputBuffer.Length;
            bufferLength -= count;
        }
    }
    
    private void ProcessBuffer()
    {
        // Look for DTVCC packet start
        while (bufferLength >= 2)
        {
            byte header = PeekBuffer(0);
            
            // Check for packet header
            if ((header & 0xC0) == 0x40)  // DTVCC packet start
            {
                int packetSize = (header & 0x3F) * 2;
                if (packetSize == 0) packetSize = 128;
                
                if (bufferLength >= packetSize + 1)
                {
                    // Extract packet and decode
                    Span<byte> packet = stackalloc byte[packetSize];
                    for (int i = 0; i < packetSize; i++)
                    {
                        packet[i] = PeekBuffer(i + 1);
                    }
                    DecodePacket(packet);
                    ConsumeBuffer(packetSize + 1);
                }
                else
                {
                    break;  // Wait for more data
                }
            }
            else
            {
                ConsumeBuffer(1);  // Skip invalid byte
            }
        }
    }
    
    private void DecodeServiceBlock(ReadOnlySpan<byte> data)
    {
        if (currentServiceNumber < 1 || currentServiceNumber > MaxServices)
        {
            return;
        }
        
        var service = services[currentServiceNumber - 1];
        int pos = 0;
        
        while (pos < data.Length)
        {
            byte code = data[pos++];
            
            if (code <= 0x1F)
            {
                // C0 control codes
                pos += DecodeC0(service, code, data.Slice(pos));
            }
            else if (code <= 0x7F)
            {
                // G0 printable characters (ASCII)
                char ch = code == 0x7F ? '♪' : (char)code;
                service.CurrentWindow.WriteCharacter(ch);
            }
            else if (code <= 0x9F)
            {
                // C1 control codes
                pos += DecodeC1(service, code, data.Slice(pos));
            }
            else
            {
                // G1 extended characters
                char ch = DecodeG1(code);
                service.CurrentWindow.WriteCharacter(ch);
            }
        }
        
        if (needOutput)
        {
            needOutput = false;
            string text = service.GetVisibleText();
            CaptionChanged?.Invoke(currentServiceNumber, text);
        }
    }
    
    private int DecodeC0(Cea708Service service, byte code, ReadOnlySpan<byte> remaining)
    {
        switch (code)
        {
            case 0x00:  // NUL - ignored
                break;
                
            case 0x03:  // ETX - End of Text (trigger output)
                needOutput = true;
                break;
                
            case 0x08:  // BS - Backspace
                service.CurrentWindow.Backspace();
                break;
                
            case 0x0C:  // FF - Form Feed (clear window)
                service.CurrentWindow.FormFeed();
                break;
                
            case 0x0D:  // CR - Carriage Return
                service.CurrentWindow.CarriageReturn();
                needOutput = true;
                break;
                
            case 0x0E:  // HCR - Horizontal Carriage Return
                service.CurrentWindow.CursorColumn = 0;
                break;
                
            case 0x10:  // EXT1 - Extended code set 1
                if (remaining.Length >= 1)
                {
                    // Handle extended codes (P16, etc.)
                    return 1;
                }
                break;
                
            case 0x18:  // P16 - 2-byte UTF-16 character
                if (remaining.Length >= 2)
                {
                    char ch = (char)((remaining[0] << 8) | remaining[1]);
                    service.CurrentWindow.WriteCharacter(ch);
                    return 2;
                }
                break;
        }
        
        return 0;
    }
    
    private int DecodeC1(Cea708Service service, byte code, ReadOnlySpan<byte> remaining)
    {
        // CW0-CW7: Set Current Window
        if (code >= 0x80 && code <= 0x87)
        {
            service.SetCurrentWindow(code - 0x80);
            return 0;
        }
        
        switch (code)
        {
            case 0x88:  // CLW - Clear Windows
                if (remaining.Length >= 1)
                {
                    service.ClearWindows(remaining[0]);
                    needOutput = true;
                    return 1;
                }
                break;
                
            case 0x89:  // DSW - Display Windows
                if (remaining.Length >= 1)
                {
                    service.ShowWindows(remaining[0]);
                    needOutput = true;
                    return 1;
                }
                break;
                
            case 0x8A:  // HDW - Hide Windows
                if (remaining.Length >= 1)
                {
                    service.HideWindows(remaining[0]);
                    needOutput = true;
                    return 1;
                }
                break;
                
            case 0x8B:  // TGW - Toggle Windows
                if (remaining.Length >= 1)
                {
                    service.ToggleWindows(remaining[0]);
                    needOutput = true;
                    return 1;
                }
                break;
                
            case 0x8C:  // DLW - Delete Windows
                if (remaining.Length >= 1)
                {
                    service.DeleteWindows(remaining[0]);
                    needOutput = true;
                    return 1;
                }
                break;
                
            case 0x8D:  // DLY - Delay
                if (remaining.Length >= 1)
                {
                    // Delay in 100ms units (ignored for now)
                    return 1;
                }
                break;
                
            case 0x8E:  // DLC - Delete to End of Row
                // Not commonly used, skip for now
                break;
                
            case 0x8F:  // RST - Reset
                service.Reset();
                needOutput = true;
                break;
                
            case 0x90:  // SPA - Set Pen Attributes
                if (remaining.Length >= 2)
                {
                    DecodeSpa(service, remaining[0], remaining[1]);
                    return 2;
                }
                break;
                
            case 0x91:  // SPC - Set Pen Color
                if (remaining.Length >= 3)
                {
                    DecodeSpc(service, remaining[0], remaining[1], remaining[2]);
                    return 3;
                }
                break;
                
            case 0x92:  // SPL - Set Pen Location
                if (remaining.Length >= 2)
                {
                    service.CurrentWindow.CursorRow = remaining[0] & 0x0F;
                    service.CurrentWindow.CursorColumn = remaining[1] & 0x3F;
                    return 2;
                }
                break;
                
            case 0x97:  // SWA - Set Window Attributes
                if (remaining.Length >= 4)
                {
                    DecodeSwa(service, remaining.Slice(0, 4));
                    return 4;
                }
                break;
                
            // DF0-DF7: Define Window 0-7
            case 0x98:
            case 0x99:
            case 0x9A:
            case 0x9B:
            case 0x9C:
            case 0x9D:
            case 0x9E:
            case 0x9F:
                if (remaining.Length >= 6)
                {
                    DecodeDefineWindow(service, code - 0x98, remaining.Slice(0, 6));
                    return 6;
                }
                break;
        }
        
        return 0;
    }
    
    private void DecodeSpa(Cea708Service service, byte b1, byte b2)
    {
        var style = service.CurrentWindow.PenStyle;
        
        style.Size = (Cea708PenSize)((b1 >> 6) & 0x03);
        style.Font = (Cea708FontType)((b1 >> 3) & 0x07);
        style.Offset = (Cea708TextOffset)(b1 & 0x03);
        
        style.Italics = ((b2 >> 7) & 0x01) != 0;
        style.Underline = ((b2 >> 6) & 0x01) != 0;
        style.EdgeType = (Cea708EdgeType)((b2 >> 3) & 0x07);
        
        service.CurrentWindow.PenStyle = style;
    }
    
    private void DecodeSpc(Cea708Service service, byte b1, byte b2, byte b3)
    {
        var style = service.CurrentWindow.PenStyle;
        
        style.ForegroundOpacity = (Cea708Opacity)((b1 >> 6) & 0x03);
        style.ForegroundColor = (byte)(b1 & 0x3F);
        
        style.BackgroundOpacity = (Cea708Opacity)((b2 >> 6) & 0x03);
        style.BackgroundColor = (byte)(b2 & 0x3F);
        
        style.EdgeColor = (byte)(b3 & 0x3F);
        
        service.CurrentWindow.PenStyle = style;
    }
    
    private void DecodeSwa(Cea708Service service, ReadOnlySpan<byte> data)
    {
        var style = service.CurrentWindow.WindowStyle;
        
        style.FillOpacity = (Cea708Opacity)((data[0] >> 6) & 0x03);
        style.FillColor = (byte)(data[0] & 0x3F);
        
        style.BorderType = (byte)((data[1] >> 6) & 0x03);
        style.BorderColor = (byte)(data[1] & 0x3F);
        
        style.Justify = (Cea708Justification)((data[2] >> 6) & 0x03);
        style.ScrollDirection = (Cea708Direction)((data[2] >> 4) & 0x03);
        style.PrintDirection = (Cea708Direction)((data[2] >> 2) & 0x03);
        style.WordWrap = (data[2] & 0x01) != 0;
        
        service.CurrentWindow.WindowStyle = style;
    }
    
    private void DecodeDefineWindow(Cea708Service service, int windowId, ReadOnlySpan<byte> data)
    {
        var window = service.GetWindow(windowId);
        
        bool wasVisible = window.Visible;
        
        window.Visible = ((data[0] >> 5) & 0x01) != 0;
        window.RowLock = ((data[0] >> 4) & 0x01) != 0;
        window.ColumnLock = ((data[0] >> 3) & 0x01) != 0;
        window.Priority = data[0] & 0x07;
        
        window.RelativePositioning = ((data[1] >> 7) & 0x01) != 0;
        window.AnchorVertical = data[1] & 0x7F;
        
        window.AnchorHorizontal = data[2];
        
        window.AnchorPoint = (Cea708AnchorPoint)((data[3] >> 4) & 0x0F);
        window.RowCount = (data[3] & 0x0F) + 1;
        
        window.ColumnCount = (data[4] & 0x3F) + 1;
        
        window.WindowStyleId = (data[5] >> 3) & 0x07;
        window.PenStyleId = data[5] & 0x07;
        
        window.Defined = true;
        
        // Apply predefined styles if specified
        if (window.WindowStyleId > 0)
        {
            ApplyPredefinedWindowStyle(window, window.WindowStyleId);
        }
        if (window.PenStyleId > 0)
        {
            ApplyPredefinedPenStyle(window, window.PenStyleId);
        }
        
        // If window became visible, trigger output
        if (window.Visible && !wasVisible)
        {
            needOutput = true;
        }
        
        service.SetCurrentWindow(windowId);
    }
    
    private static void ApplyPredefinedWindowStyle(Cea708Window window, int styleId)
    {
        // CEA-708 predefined window styles
        window.WindowStyle = styleId switch
        {
            1 => new Cea708WindowStyle
            {
                Justify = Cea708Justification.Left,
                PrintDirection = Cea708Direction.LeftToRight,
                ScrollDirection = Cea708Direction.BottomToTop,
                WordWrap = false,
                FillOpacity = Cea708Opacity.Solid,
                FillColor = 0x00
            },
            2 => new Cea708WindowStyle
            {
                Justify = Cea708Justification.Left,
                PrintDirection = Cea708Direction.LeftToRight,
                ScrollDirection = Cea708Direction.BottomToTop,
                WordWrap = true,
                FillOpacity = Cea708Opacity.Solid,
                FillColor = 0x00
            },
            3 => new Cea708WindowStyle
            {
                Justify = Cea708Justification.Center,
                PrintDirection = Cea708Direction.LeftToRight,
                ScrollDirection = Cea708Direction.BottomToTop,
                WordWrap = false,
                FillOpacity = Cea708Opacity.Solid,
                FillColor = 0x00
            },
            _ => Cea708WindowStyle.Default
        };
    }
    
    private static void ApplyPredefinedPenStyle(Cea708Window window, int styleId)
    {
        // CEA-708 predefined pen styles
        window.PenStyle = styleId switch
        {
            1 => new Cea708PenStyle
            {
                Size = Cea708PenSize.Standard,
                Font = Cea708FontType.Default,
                ForegroundColor = 0x3F,
                ForegroundOpacity = Cea708Opacity.Solid,
                BackgroundColor = 0x00,
                BackgroundOpacity = Cea708Opacity.Solid
            },
            2 => new Cea708PenStyle
            {
                Size = Cea708PenSize.Standard,
                Font = Cea708FontType.MonospacedSerif,
                ForegroundColor = 0x3F,
                ForegroundOpacity = Cea708Opacity.Solid,
                BackgroundColor = 0x00,
                BackgroundOpacity = Cea708Opacity.Solid
            },
            _ => Cea708PenStyle.Default
        };
    }
    
    private static char DecodeG1(byte code)
    {
        // G1 character set (Latin-1 extension)
        // Map to Unicode
        return code switch
        {
            0xA0 => '\u00A0',  // Non-breaking space
            0xA1 => '¡',
            0xA2 => '¢',
            0xA3 => '£',
            0xA4 => '¤',
            0xA5 => '¥',
            0xA6 => '¦',
            0xA7 => '§',
            0xA8 => '¨',
            0xA9 => '©',
            0xAA => 'ª',
            0xAB => '«',
            0xAC => '¬',
            0xAD => '\u00AD',  // Soft hyphen
            0xAE => '®',
            0xAF => '¯',
            0xB0 => '°',
            0xB1 => '±',
            0xB2 => '²',
            0xB3 => '³',
            0xB4 => '´',
            0xB5 => 'µ',
            0xB6 => '¶',
            0xB7 => '·',
            0xB8 => '¸',
            0xB9 => '¹',
            0xBA => 'º',
            0xBB => '»',
            0xBC => '¼',
            0xBD => '½',
            0xBE => '¾',
            0xBF => '¿',
            _ => (char)code
        };
    }
}

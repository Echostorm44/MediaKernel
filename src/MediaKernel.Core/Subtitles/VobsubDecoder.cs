// VOBSUB/SPU (Sub Picture Unit) Decoder
// Decodes DVD subtitle bitmaps

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoder for DVD VOBSUB subtitles (SPU format).
/// </summary>
public sealed class VobsubDecoder
{
    private VobsubPalette palette;
    private long basePts;
    
    /// <summary>
    /// Creates a new VOBSUB decoder with default palette.
    /// </summary>
    public VobsubDecoder()
    {
        palette = VobsubPalette.CreateDefault();
    }
    
    /// <summary>
    /// Sets the color palette for decoding.
    /// </summary>
    public void SetPalette(VobsubPalette newPalette)
    {
        palette = newPalette;
    }
    
    /// <summary>
    /// Sets the base PTS for relative timestamp calculations.
    /// </summary>
    public void SetBasePts(long ptsMicroseconds)
    {
        basePts = ptsMicroseconds;
    }
    
    /// <summary>
    /// Decodes an SPU packet.
    /// </summary>
    public VobsubSubtitleImage? DecodePacket(ReadOnlySpan<byte> data)
    {
        if (!SpuPacketHeader.TryParse(data, out var header))
            return null;
        
        if (data.Length < header.PacketSize)
            return null;
        
        // Parse control sequence
        if (!TryParseControlSequence(data, header.ControlSequenceOffset, out var control))
            return null;
        
        // Check if we should display
        if (control.StopDisplay && !control.StartDisplay && !control.ForceDisplay)
            return null;  // Clear display command
        
        // Decode RLE data
        var pixels = DecodeRle(data, control, header);
        
        return new VobsubSubtitleImage
        {
            StartTimeMicroseconds = basePts + control.DisplayTimeMicroseconds,
            EndTimeMicroseconds = 0,  // Set when stop display received
            X = control.X1,
            Y = control.Y1,
            Width = control.Width,
            Height = control.Height,
            Pixels = pixels,
            IsForced = control.ForceDisplay
        };
    }
    
    /// <summary>
    /// Parses SPU control sequence.
    /// </summary>
    private bool TryParseControlSequence(ReadOnlySpan<byte> data, int offset, out SpuControlSequence control)
    {
        control = new SpuControlSequence
        {
            PaletteIndices = [0, 1, 2, 3],  // Default palette
            AlphaValues = [0x0, 0xF, 0xF, 0xF]  // Color 0 transparent, rest opaque
        };
        
        if (offset + 4 > data.Length)
            return false;
        
        // First 2 bytes: display time in 11ms units (convert to microseconds)
        int displayTime11Ms = (data[offset] << 8) | data[offset + 1];
        control.DisplayTimeMicroseconds = displayTime11Ms * 11000L;
        
        // Next 2 bytes: offset to next sequence
        control.NextSequenceOffset = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
        
        int cmdOffset = offset + 4;
        bool done = false;
        
        while (!done && cmdOffset < data.Length)
        {
            var cmd = (SpuCommand)data[cmdOffset++];
            
            switch (cmd)
            {
                case SpuCommand.ForceDisplay:
                    control.ForceDisplay = true;
                    break;
                    
                case SpuCommand.StartDisplay:
                    control.StartDisplay = true;
                    break;
                    
                case SpuCommand.StopDisplay:
                    control.StopDisplay = true;
                    break;
                    
                case SpuCommand.SetPalette:
                    // 2 bytes: 4 nibbles for palette indices
                    if (cmdOffset + 2 > data.Length)
                        return false;
                    
                    control.PaletteIndices =
                    [
                        (byte)((data[cmdOffset] >> 4) & 0x0F),
                        (byte)(data[cmdOffset] & 0x0F),
                        (byte)((data[cmdOffset + 1] >> 4) & 0x0F),
                        (byte)(data[cmdOffset + 1] & 0x0F)
                    ];
                    cmdOffset += 2;
                    break;
                    
                case SpuCommand.SetAlphaChannel:
                    // 2 bytes: 4 nibbles for alpha values
                    if (cmdOffset + 2 > data.Length)
                        return false;
                    
                    control.AlphaValues =
                    [
                        (byte)((data[cmdOffset] >> 4) & 0x0F),
                        (byte)(data[cmdOffset] & 0x0F),
                        (byte)((data[cmdOffset + 1] >> 4) & 0x0F),
                        (byte)(data[cmdOffset + 1] & 0x0F)
                    ];
                    cmdOffset += 2;
                    break;
                    
                case SpuCommand.SetCoordinates:
                    // 6 bytes: X1, X2, Y1, Y2 (12-bit each)
                    if (cmdOffset + 6 > data.Length)
                        return false;
                    
                    // X1 = bytes[0]:8, bytes[1]:4 upper (12 bits)
                    // X2 = bytes[1]:4 lower, bytes[2]:8 (12 bits)
                    // Y1 = bytes[3]:8, bytes[4]:4 upper (12 bits)
                    // Y2 = bytes[4]:4 lower, bytes[5]:8 (12 bits)
                    control.X1 = (ushort)((data[cmdOffset] << 4) | (data[cmdOffset + 1] >> 4));
                    control.X2 = (ushort)(((data[cmdOffset + 1] & 0x0F) << 8) | data[cmdOffset + 2]);
                    control.Y1 = (ushort)((data[cmdOffset + 3] << 4) | (data[cmdOffset + 4] >> 4));
                    control.Y2 = (ushort)(((data[cmdOffset + 4] & 0x0F) << 8) | data[cmdOffset + 5]);
                    cmdOffset += 6;
                    break;
                    
                case SpuCommand.SetOffsets:
                    // 4 bytes: two 16-bit offsets for even/odd fields
                    if (cmdOffset + 4 > data.Length)
                        return false;
                    
                    control.EvenFieldOffset = (ushort)((data[cmdOffset] << 8) | data[cmdOffset + 1]);
                    control.OddFieldOffset = (ushort)((data[cmdOffset + 2] << 8) | data[cmdOffset + 3]);
                    cmdOffset += 4;
                    break;
                    
                case SpuCommand.SetColorContrast:
                    // Variable length PXCTLI data - skip it
                    // Format: total_size:2, then 6-byte entries
                    if (cmdOffset + 2 > data.Length)
                        return false;
                    
                    int pxctliSize = (data[cmdOffset] << 8) | data[cmdOffset + 1];
                    cmdOffset += pxctliSize;
                    break;
                    
                case SpuCommand.End:
                    done = true;
                    break;
                    
                default:
                    // Unknown command - stop parsing
                    done = true;
                    break;
            }
        }
        
        return control.Width > 0 && control.Height > 0;
    }
    
    /// <summary>
    /// Decodes RLE data to ARGB pixels.
    /// </summary>
    /// <remarks>
    /// SPU uses nibble-based RLE with interlaced fields:
    /// - Lower 2 bits: color index (0-3)
    /// - Upper bits: run length
    /// - Code less than 0x04 signals end of line
    /// </remarks>
    private uint[] DecodeRle(ReadOnlySpan<byte> data, SpuControlSequence control, SpuPacketHeader header)
    {
        int width = control.Width;
        int height = control.Height;
        var pixels = new uint[width * height];
        
        // Build palette lookup
        uint[] paletteColors =
        [
            palette.GetArgb(control.PaletteIndices[0], control.AlphaValues[0]),
            palette.GetArgb(control.PaletteIndices[1], control.AlphaValues[1]),
            palette.GetArgb(control.PaletteIndices[2], control.AlphaValues[2]),
            palette.GetArgb(control.PaletteIndices[3], control.AlphaValues[3])
        ];
        
        // Decode interlaced fields
        int[] fieldOffsets = [(control.EvenFieldOffset > 0 ? control.EvenFieldOffset : 4), control.OddFieldOffset];
        int[] nibblePositions = [fieldOffsets[0] * 2, fieldOffsets[1] * 2];
        
        for (int y = 0; y < height; y++)
        {
            int field = y & 1;  // Alternate between even (0) and odd (1) fields
            int rowOffset = y * width;
            int x = 0;
            
            while (x < width)
            {
                // Read nibble-encoded RLE code
                int code = GetNibbleCode(data, ref nibblePositions[field]);
                
                if (code < 0)
                    break;  // Error reading
                
                if (code < 0x0004)
                {
                    // End of line - fill remaining pixels with color 0
                    while (x < width)
                    {
                        pixels[rowOffset + x] = paletteColors[0];
                        x++;
                    }
                    
                    // Align to byte boundary
                    if ((nibblePositions[field] & 1) != 0)
                        nibblePositions[field]++;
                }
                else
                {
                    int runLength = code >> 2;
                    int colorIndex = code & 0x03;
                    uint color = paletteColors[colorIndex];
                    
                    for (int i = 0; i < runLength && x < width; i++)
                    {
                        pixels[rowOffset + x] = color;
                        x++;
                    }
                }
            }
        }
        
        return pixels;
    }
    
    /// <summary>
    /// Reads a nibble-encoded RLE code (2-4 nibbles).
    /// </summary>
    private static int GetNibbleCode(ReadOnlySpan<byte> data, ref int nibblePosition)
    {
        int code = 0;
        int minValue = 1;
        
        // Read nibbles until we have a valid code
        // Valid code: code >= minValue where minValue starts at 1 and multiplies by 4 each nibble
        for (int i = 0; i < 4; i++)
        {
            int nibble = GetNibble(data, nibblePosition++);
            if (nibble < 0)
                return -1;
            
            code = (code << 4) | nibble;
            
            if (code >= minValue)
                return code;
            
            minValue <<= 2;  // Multiply by 4 for next iteration
        }
        
        return code;
    }
    
    /// <summary>
    /// Gets a single nibble from the data.
    /// </summary>
    private static int GetNibble(ReadOnlySpan<byte> data, int nibblePosition)
    {
        int bytePosition = nibblePosition >> 1;
        
        if (bytePosition >= data.Length)
            return -1;
        
        byte b = data[bytePosition];
        
        // Even nibble position = high nibble, odd = low nibble
        return (nibblePosition & 1) == 0 ? (b >> 4) : (b & 0x0F);
    }
    
    /// <summary>
    /// Resets decoder state.
    /// </summary>
    public void Reset()
    {
        basePts = 0;
    }
}

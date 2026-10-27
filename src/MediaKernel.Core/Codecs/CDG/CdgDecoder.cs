// MediaKernel - CDG (CD+G) Karaoke Graphics Decoder
// Decodes CDG subcode packets into video frames for karaoke display

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.CDG;

/// <summary>
/// CDG (CD+G) instruction types.
/// </summary>
public enum CdgInstruction : byte
{
    /// <summary>Fill entire screen with a color.</summary>
    MemoryPreset = 1,
    
    /// <summary>Fill screen border with a color.</summary>
    BorderPreset = 2,
    
    /// <summary>Draw a 6x12 tile block (normal mode).</summary>
    TileBlockNormal = 6,
    
    /// <summary>Scroll screen without copy/wrap.</summary>
    ScrollPreset = 20,
    
    /// <summary>Scroll screen with copy/wrap.</summary>
    ScrollCopy = 24,
    
    /// <summary>Define transparent color index (not commonly implemented).</summary>
    DefineTransparentColor = 28,
    
    /// <summary>Load colors 0-7 into palette.</summary>
    LoadColorTableLow = 30,
    
    /// <summary>Load colors 8-15 into palette.</summary>
    LoadColorTableHigh = 31,
    
    /// <summary>Draw a 6x12 tile block (XOR mode).</summary>
    TileBlockXor = 38
}

/// <summary>
/// CDG decoder state containing screen buffer, palette, and scroll offsets.
/// </summary>
public sealed class CdgDecoderState
{
    /// <summary>
    /// Full screen width including borders (300 pixels).
    /// </summary>
    public const int ScreenWidth = 300;
    
    /// <summary>
    /// Full screen height including borders (216 pixels).
    /// </summary>
    public const int ScreenHeight = 216;
    
    /// <summary>
    /// Border width on each side (6 pixels).
    /// </summary>
    public const int BorderWidth = 6;
    
    /// <summary>
    /// Border height on top and bottom (12 pixels).
    /// </summary>
    public const int BorderHeight = 12;
    
    /// <summary>
    /// Display area width (288 pixels = 300 - 2*6).
    /// </summary>
    public const int DisplayWidth = ScreenWidth - 2 * BorderWidth;
    
    /// <summary>
    /// Display area height (192 pixels = 216 - 2*12).
    /// </summary>
    public const int DisplayHeight = ScreenHeight - 2 * BorderHeight;
    
    /// <summary>
    /// CDG packet size in bytes.
    /// </summary>
    public const int PacketSize = 24;
    
    /// <summary>
    /// CDG command mask (bits 0-5 of first byte).
    /// </summary>
    public const byte CommandMask = 0x3F;
    
    /// <summary>
    /// CDG command value for graphics data.
    /// </summary>
    public const byte CdgCommand = 0x09;
    
    /// <summary>
    /// Indexed color screen buffer (palette indices 0-15).
    /// </summary>
    public byte[] ScreenBuffer { get; } = new byte[ScreenWidth * ScreenHeight];
    
    /// <summary>
    /// 16-color palette. Each entry is RGB (3 bytes).
    /// </summary>
    public byte[,] Palette { get; } = new byte[16, 3];
    
    /// <summary>
    /// Horizontal scroll offset (0-5 pixels).
    /// </summary>
    public int HorizontalOffset { get; set; }
    
    /// <summary>
    /// Vertical scroll offset (0-11 pixels).
    /// </summary>
    public int VerticalOffset { get; set; }
    
    /// <summary>
    /// Transparent color index (-1 if none).
    /// </summary>
    public int TransparentColorIndex { get; set; } = -1;
    
    /// <summary>
    /// Number of packets decoded (for frame timing).
    /// </summary>
    public int PacketCount { get; set; }
    
    /// <summary>
    /// Reset state to initial values.
    /// </summary>
    public void Reset()
    {
        Array.Clear(ScreenBuffer);
        Array.Clear(Palette);
        HorizontalOffset = 0;
        VerticalOffset = 0;
        TransparentColorIndex = -1;
        PacketCount = 0;
    }
}

/// <summary>
/// CDG (CD+G) decoder for karaoke graphics.
/// Decodes 24-byte CDG subcode packets into a 288x192 video frame.
/// </summary>
public sealed class CdgDecoder
{
    private readonly CdgDecoderState state = new();
    private readonly byte[] scrollCopyBuffer = new byte[CdgDecoderState.ScreenWidth * CdgDecoderState.ScreenHeight];
    
    /// <summary>
    /// Gets the decoder state for external inspection or modification.
    /// </summary>
    public CdgDecoderState State => state;
    
    /// <summary>
    /// Gets the display width in pixels.
    /// </summary>
    public int DisplayWidth => CdgDecoderState.DisplayWidth;
    
    /// <summary>
    /// Gets the display height in pixels.
    /// </summary>
    public int DisplayHeight => CdgDecoderState.DisplayHeight;
    
    /// <summary>
    /// Decode a CDG packet. Returns true if a frame should be rendered.
    /// CDG runs at 300 packets/second but renders at 25 fps (every 12 packets).
    /// </summary>
    /// <param name="packet">24-byte CDG packet.</param>
    /// <returns>True if a new frame is ready to render (every 12th packet).</returns>
    public bool DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < CdgDecoderState.PacketSize)
            return false;
        
        state.PacketCount++;
        
        // Only process CDG command (0x09)
        byte command = (byte)(packet[0] & CdgDecoderState.CommandMask);
        if (command != CdgDecoderState.CdgCommand)
            return ShouldRenderFrame();
        
        byte instruction = (byte)(packet[1] & CdgDecoderState.CommandMask);
        ReadOnlySpan<byte> data = packet.Slice(4, 16); // Data starts at byte 4
        
        switch ((CdgInstruction)instruction)
        {
            case CdgInstruction.MemoryPreset:
                DecodeMemoryPreset(data);
                break;
                
            case CdgInstruction.BorderPreset:
                DecodeBorderPreset(data);
                break;
                
            case CdgInstruction.TileBlockNormal:
                DecodeTileBlock(data, useXor: false);
                break;
                
            case CdgInstruction.TileBlockXor:
                DecodeTileBlock(data, useXor: true);
                break;
                
            case CdgInstruction.ScrollPreset:
                DecodeScroll(data, copyMode: false);
                break;
                
            case CdgInstruction.ScrollCopy:
                DecodeScroll(data, copyMode: true);
                break;
                
            case CdgInstruction.LoadColorTableLow:
                DecodeLoadColorTable(data, baseIndex: 0);
                break;
                
            case CdgInstruction.LoadColorTableHigh:
                DecodeLoadColorTable(data, baseIndex: 8);
                break;
                
            case CdgInstruction.DefineTransparentColor:
                DecodeDefineTransparentColor(data);
                break;
        }
        
        return ShouldRenderFrame();
    }
    
    /// <summary>
    /// Render the current screen state to an RGBA buffer.
    /// </summary>
    /// <param name="outputBuffer">Output buffer (288 * 192 * 4 = 221,184 bytes minimum).</param>
    /// <returns>True if rendering succeeded.</returns>
    public bool Render(Span<byte> outputBuffer)
    {
        int requiredSize = CdgDecoderState.DisplayWidth * CdgDecoderState.DisplayHeight * 4;
        if (outputBuffer.Length < requiredSize)
            return false;
        
        int outputIndex = 0;
        
        for (int y = 0; y < CdgDecoderState.DisplayHeight; y++)
        {
            int screenY = y + state.VerticalOffset + CdgDecoderState.BorderHeight;
            
            for (int x = 0; x < CdgDecoderState.DisplayWidth; x++)
            {
                int screenX = x + state.HorizontalOffset + CdgDecoderState.BorderWidth;
                int screenIndex = screenY * CdgDecoderState.ScreenWidth + screenX;
                
                byte colorIndex = state.ScreenBuffer[screenIndex];
                byte r = state.Palette[colorIndex, 0];
                byte g = state.Palette[colorIndex, 1];
                byte b = state.Palette[colorIndex, 2];
                
                // Handle transparency
                byte a = (colorIndex == state.TransparentColorIndex) ? (byte)0 : (byte)255;
                
                // Output as RGBA
                outputBuffer[outputIndex++] = r;
                outputBuffer[outputIndex++] = g;
                outputBuffer[outputIndex++] = b;
                outputBuffer[outputIndex++] = a;
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Render the current screen state to a BGRA buffer (common for Windows/DirectX).
    /// </summary>
    /// <param name="outputBuffer">Output buffer (288 * 192 * 4 = 221,184 bytes minimum).</param>
    /// <returns>True if rendering succeeded.</returns>
    public bool RenderBgra(Span<byte> outputBuffer)
    {
        int requiredSize = CdgDecoderState.DisplayWidth * CdgDecoderState.DisplayHeight * 4;
        if (outputBuffer.Length < requiredSize)
            return false;
        
        int outputIndex = 0;
        
        for (int y = 0; y < CdgDecoderState.DisplayHeight; y++)
        {
            int screenY = y + state.VerticalOffset + CdgDecoderState.BorderHeight;
            
            for (int x = 0; x < CdgDecoderState.DisplayWidth; x++)
            {
                int screenX = x + state.HorizontalOffset + CdgDecoderState.BorderWidth;
                int screenIndex = screenY * CdgDecoderState.ScreenWidth + screenX;
                
                byte colorIndex = state.ScreenBuffer[screenIndex];
                byte r = state.Palette[colorIndex, 0];
                byte g = state.Palette[colorIndex, 1];
                byte b = state.Palette[colorIndex, 2];
                
                // Handle transparency
                byte a = (colorIndex == state.TransparentColorIndex) ? (byte)0 : (byte)255;
                
                // Output as BGRA
                outputBuffer[outputIndex++] = b;
                outputBuffer[outputIndex++] = g;
                outputBuffer[outputIndex++] = r;
                outputBuffer[outputIndex++] = a;
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Reset the decoder to initial state.
    /// </summary>
    public void Reset()
    {
        state.Reset();
    }
    
    /// <summary>
    /// Returns true every 12th packet (300 packets/sec → 25 fps).
    /// VLC uses every 3rd packet at 75 packets/sec input, we use every 12th at 300/sec.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldRenderFrame()
    {
        // CDG runs at 300 packets/second, we want 25 fps output
        // 300 / 25 = 12 packets per frame
        return (state.PacketCount % 12) == 0;
    }
    
    /// <summary>
    /// Memory preset: fill entire screen with a color.
    /// </summary>
    private void DecodeMemoryPreset(ReadOnlySpan<byte> data)
    {
        byte colorIndex = (byte)(data[0] & 0x0F);
        // Repeat flag (data[1] & 0x0F) is ignored for reliable streams
        
        Array.Fill(state.ScreenBuffer, colorIndex);
    }
    
    /// <summary>
    /// Border preset: fill screen border with a color.
    /// </summary>
    private void DecodeBorderPreset(ReadOnlySpan<byte> data)
    {
        byte colorIndex = (byte)(data[0] & 0x0F);
        
        // Top border
        FillRect(0, 0, CdgDecoderState.ScreenWidth, CdgDecoderState.BorderHeight, colorIndex);
        
        // Bottom border
        FillRect(0, CdgDecoderState.ScreenHeight - CdgDecoderState.BorderHeight,
                 CdgDecoderState.ScreenWidth, CdgDecoderState.BorderHeight, colorIndex);
        
        // Left border (excluding top/bottom corners already filled)
        FillRect(0, CdgDecoderState.BorderHeight,
                 CdgDecoderState.BorderWidth, CdgDecoderState.ScreenHeight - 2 * CdgDecoderState.BorderHeight, colorIndex);
        
        // Right border
        FillRect(CdgDecoderState.ScreenWidth - CdgDecoderState.BorderWidth, CdgDecoderState.BorderHeight,
                 CdgDecoderState.BorderWidth, CdgDecoderState.ScreenHeight - 2 * CdgDecoderState.BorderHeight, colorIndex);
    }
    
    /// <summary>
    /// Load color table: set 8 palette entries.
    /// </summary>
    private void DecodeLoadColorTable(ReadOnlySpan<byte> data, int baseIndex)
    {
        for (int i = 0; i < 8; i++)
        {
            // Each color is 2 bytes (big-endian): ----RRRRGGGGBBBB
            int color = (data[i * 2] << 8) | data[i * 2 + 1];
            
            // Extract 4-bit color components
            int r = (color >> 10) & 0x0F;
            int g = ((color >> 6) & 0x0C) | ((color >> 4) & 0x03); // 4 bits from two nibbles
            int b = color & 0x0F;
            
            // Scale 4-bit to 8-bit (multiply by 17 or shift left by 4)
            state.Palette[baseIndex + i, 0] = (byte)(r << 4);
            state.Palette[baseIndex + i, 1] = (byte)(g << 4);
            state.Palette[baseIndex + i, 2] = (byte)(b << 4);
        }
    }
    
    /// <summary>
    /// Tile block: draw a 6x12 pixel tile.
    /// </summary>
    private void DecodeTileBlock(ReadOnlySpan<byte> data, bool useXor)
    {
        byte color0 = (byte)(data[0] & 0x0F);
        byte color1 = (byte)(data[1] & 0x0F);
        
        // Tile position (in tile units)
        int tileRow = data[2] & 0x1F;    // 0-17 (18 rows of 12 pixels = 216)
        int tileColumn = data[3] & 0x3F; // 0-49 (50 columns of 6 pixels = 300)
        
        int startY = tileRow * 12;
        int startX = tileColumn * 6;
        
        // Each of 12 bytes defines one row of 6 pixels
        for (int row = 0; row < 12; row++)
        {
            byte rowData = data[4 + row];
            
            for (int col = 0; col < 6; col++)
            {
                // Bit 5 is leftmost pixel, bit 0 is rightmost
                int bit = (rowData >> (5 - col)) & 0x01;
                byte colorIndex = bit == 0 ? color0 : color1;
                
                int screenX = startX + col;
                int screenY = startY + row;
                
                // Bounds check
                if (screenX >= CdgDecoderState.ScreenWidth || screenY >= CdgDecoderState.ScreenHeight)
                    continue;
                
                int screenIndex = screenY * CdgDecoderState.ScreenWidth + screenX;
                
                if (useXor)
                    state.ScreenBuffer[screenIndex] ^= colorIndex;
                else
                    state.ScreenBuffer[screenIndex] = colorIndex;
            }
        }
    }
    
    /// <summary>
    /// Scroll: shift screen content.
    /// </summary>
    private void DecodeScroll(ReadOnlySpan<byte> data, bool copyMode)
    {
        byte fillColor = (byte)(data[0] & 0x0F);
        
        // Horizontal offset and direction
        byte horizontalCommand = data[1];
        state.HorizontalOffset = horizontalCommand & 0x07;
        if (state.HorizontalOffset >= CdgDecoderState.BorderWidth)
            state.HorizontalOffset = CdgDecoderState.BorderWidth - 1;
        
        int horizontalShift = ((horizontalCommand >> 4) & 0x03) switch
        {
            0x01 => 6,  // Shift right
            0x02 => -6, // Shift left
            _ => 0
        };
        
        // Vertical offset and direction
        byte verticalCommand = data[2];
        state.VerticalOffset = verticalCommand & 0x0F;
        if (state.VerticalOffset >= CdgDecoderState.BorderHeight)
            state.VerticalOffset = CdgDecoderState.BorderHeight - 1;
        
        int verticalShift = ((verticalCommand >> 4) & 0x03) switch
        {
            0x01 => 12,  // Shift down
            0x02 => -12, // Shift up
            _ => 0
        };
        
        // No shift needed
        if (horizontalShift == 0 && verticalShift == 0)
            return;
        
        // Make a copy of the screen
        state.ScreenBuffer.CopyTo(scrollCopyBuffer.AsSpan());
        
        // Fill entire screen with fill color
        Array.Fill(state.ScreenBuffer, fillColor);
        
        // Copy back with shift
        for (int y = 0; y < CdgDecoderState.ScreenHeight; y++)
        {
            int destY = y + verticalShift;
            
            for (int x = 0; x < CdgDecoderState.ScreenWidth; x++)
            {
                int destX = x + horizontalShift;
                
                if (copyMode)
                {
                    // Wrap around
                    destY = ((destY % CdgDecoderState.ScreenHeight) + CdgDecoderState.ScreenHeight) % CdgDecoderState.ScreenHeight;
                    destX = ((destX % CdgDecoderState.ScreenWidth) + CdgDecoderState.ScreenWidth) % CdgDecoderState.ScreenWidth;
                }
                else
                {
                    // Skip out-of-bounds pixels
                    if (destX < 0 || destX >= CdgDecoderState.ScreenWidth ||
                        destY < 0 || destY >= CdgDecoderState.ScreenHeight)
                        continue;
                }
                
                int srcIndex = y * CdgDecoderState.ScreenWidth + x;
                int destIndex = destY * CdgDecoderState.ScreenWidth + destX;
                state.ScreenBuffer[destIndex] = scrollCopyBuffer[srcIndex];
            }
        }
    }
    
    /// <summary>
    /// Define transparent color (optional, not widely used).
    /// </summary>
    private void DecodeDefineTransparentColor(ReadOnlySpan<byte> data)
    {
        state.TransparentColorIndex = data[0] & 0x0F;
    }
    
    /// <summary>
    /// Fill a rectangle in the screen buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillRect(int x, int y, int width, int height, byte colorIndex)
    {
        for (int row = y; row < y + height && row < CdgDecoderState.ScreenHeight; row++)
        {
            int rowStart = row * CdgDecoderState.ScreenWidth;
            for (int col = x; col < x + width && col < CdgDecoderState.ScreenWidth; col++)
            {
                state.ScreenBuffer[rowStart + col] = colorIndex;
            }
        }
    }
}

// VP8 video header parsing support
// Reference: VP8 Bitstream Specification (RFC 6386)

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp8;

/// <summary>
/// VP8 frame types.
/// </summary>
public enum Vp8FrameType : byte
{
    /// <summary>Keyframe (intra-only, can be seeked to).</summary>
    KeyFrame = 0,
    
    /// <summary>Inter-frame (requires reference frames).</summary>
    InterFrame = 1
}

/// <summary>
/// VP8 version/profile values.
/// </summary>
public enum Vp8Version : byte
{
    /// <summary>Version 0: Bicubic, normal loop filter.</summary>
    Version0 = 0,
    
    /// <summary>Version 1: Bilinear, normal loop filter.</summary>
    Version1 = 1,
    
    /// <summary>Version 2: Bilinear, no loop filter.</summary>
    Version2 = 2,
    
    /// <summary>Version 3: Bilinar, no loop filter, minimal DCT.</summary>
    Version3 = 3
}

/// <summary>
/// VP8 color space values (keyframe only).
/// </summary>
public enum Vp8ColorSpace : byte
{
    /// <summary>ITU-R BT.601 color space (default).</summary>
    Bt601 = 0,
    
    /// <summary>Reserved for future use.</summary>
    Reserved = 1
}

/// <summary>
/// VP8 uncompressed frame header information.
/// </summary>
public readonly struct Vp8FrameHeader
{
    /// <summary>Frame type (keyframe or inter-frame).</summary>
    public Vp8FrameType FrameType { get; init; }
    
    /// <summary>VP8 version/profile.</summary>
    public Vp8Version Version { get; init; }
    
    /// <summary>Whether to show this frame.</summary>
    public bool ShowFrame { get; init; }
    
    /// <summary>Size of the first data partition in bytes.</summary>
    public int FirstPartitionSize { get; init; }
    
    /// <summary>Frame width in pixels (keyframe only).</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels (keyframe only).</summary>
    public int Height { get; init; }
    
    /// <summary>Horizontal scale factor (0-3).</summary>
    public int HorizontalScale { get; init; }
    
    /// <summary>Vertical scale factor (0-3).</summary>
    public int VerticalScale { get; init; }
    
    /// <summary>Color space (keyframe only).</summary>
    public Vp8ColorSpace ColorSpace { get; init; }
    
    /// <summary>Whether clamping is required (keyframe only).</summary>
    public bool ClampingRequired { get; init; }
    
    /// <summary>Number of bytes consumed from the input buffer.</summary>
    public int BytesConsumed { get; init; }
    
    /// <summary>True if this is a keyframe.</summary>
    public bool IsKeyframe => FrameType == Vp8FrameType.KeyFrame;
    
    /// <summary>Effective display width after scaling.</summary>
    public int DisplayWidth => Width * (HorizontalScale + 1);
    
    /// <summary>Effective display height after scaling.</summary>
    public int DisplayHeight => Height * (VerticalScale + 1);
}

/// <summary>
/// VP8 header parser for frame header parsing.
/// </summary>
/// <remarks>
/// Parses VP8 uncompressed frame headers for demuxer and seeking support.
/// VP8 is simpler than VP9 - only 8-bit YUV 4:2:0 is supported.
/// </remarks>
public static class Vp8HeaderParser
{
    /// <summary>VP8 keyframe sync code bytes.</summary>
    private const byte SyncCode0 = 0x9D;
    private const byte SyncCode1 = 0x01;
    private const byte SyncCode2 = 0x2A;
    
    /// <summary>
    /// Attempts to parse a VP8 uncompressed frame header.
    /// </summary>
    /// <param name="data">Data containing the VP8 frame.</param>
    /// <param name="header">Parsed header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseFrameHeader(ReadOnlySpan<byte> data, out Vp8FrameHeader header)
    {
        header = default;
        
        if (data.Length < 3)
            return false;
        
        // First 3 bytes: frame tag
        // Bits 0: frame_type (0 = keyframe, 1 = inter)
        // Bits 1-3: version
        // Bit 4: show_frame
        // Bits 5-7 + bytes 1-2: first_part_size (19 bits)
        
        byte byte0 = data[0];
        byte byte1 = data[1];
        byte byte2 = data[2];
        
        var frameType = (Vp8FrameType)(byte0 & 1);
        var version = (Vp8Version)((byte0 >> 1) & 0x07);
        bool showFrame = ((byte0 >> 4) & 1) == 1;
        
        // First partition size: bits 5-7 of byte0 + bytes 1-2
        int firstPartitionSize = ((byte0 >> 5) & 0x07) |
                                 (byte1 << 3) |
                                 (byte2 << 11);
        
        int bytesConsumed = 3;
        int width = 0;
        int height = 0;
        int horizontalScale = 0;
        int verticalScale = 0;
        var colorSpace = Vp8ColorSpace.Bt601;
        bool clampingRequired = true;
        
        // Keyframe has additional header
        if (frameType == Vp8FrameType.KeyFrame)
        {
            if (data.Length < 10)
                return false;
            
            // Sync code (3 bytes)
            if (data[3] != SyncCode0 || data[4] != SyncCode1 || data[5] != SyncCode2)
                return false;
            
            // Width and horizontal scale (2 bytes, little-endian)
            ushort widthBytes = (ushort)(data[6] | (data[7] << 8));
            width = widthBytes & 0x3FFF;
            horizontalScale = (widthBytes >> 14) & 0x03;
            
            // Height and vertical scale (2 bytes, little-endian)
            ushort heightBytes = (ushort)(data[8] | (data[9] << 8));
            height = heightBytes & 0x3FFF;
            verticalScale = (heightBytes >> 14) & 0x03;
            
            bytesConsumed = 10;
            
            // After header, first partition starts
            // First byte of partition contains color_space and clamping_type in keyframe
            if (data.Length > 10 && firstPartitionSize > 0)
            {
                // These are actually in the compressed data, but we can peek
                // The first bits of the first partition for keyframe:
                // Bit 0: color_space
                // Bit 1: clamping_type
                // We'd need to read the arithmetic-coded data, so skip for now
            }
        }
        
        header = new Vp8FrameHeader
        {
            FrameType = frameType,
            Version = version,
            ShowFrame = showFrame,
            FirstPartitionSize = firstPartitionSize,
            Width = width,
            Height = height,
            HorizontalScale = horizontalScale,
            VerticalScale = verticalScale,
            ColorSpace = colorSpace,
            ClampingRequired = clampingRequired,
            BytesConsumed = bytesConsumed
        };
        
        return true;
    }
    
    /// <summary>
    /// Checks if the data starts with a valid VP8 keyframe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVp8Keyframe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return false;
        
        // Check frame type bit and sync code
        bool isKeyframe = (data[0] & 1) == 0;
        bool hasSyncCode = data[3] == SyncCode0 && data[4] == SyncCode1 && data[5] == SyncCode2;
        
        return isKeyframe && hasSyncCode;
    }
    
    /// <summary>
    /// Gets a human-readable description of the frame header.
    /// </summary>
    public static string GetDescription(in Vp8FrameHeader header)
    {
        string frameType = header.IsKeyframe ? "I" : "P";
        
        if (header.IsKeyframe && header.Width > 0 && header.Height > 0)
        {
            return $"VP8 {header.Width}x{header.Height} 8bit YUV420 {frameType}-frame";
        }
        
        return $"VP8 {frameType}-frame";
    }
}

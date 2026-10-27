// VP9 video header parsing support
// Reference: VP9 Bitstream & Decoding Process Specification

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>
/// VP9 profile codes. Higher profiles support more color depths and formats.
/// </summary>
public enum Vp9Profile : byte
{
    /// <summary>Profile 0: 8-bit YUV 4:2:0 only.</summary>
    Profile0 = 0,
    
    /// <summary>Profile 1: 8-bit YUV 4:2:2, 4:4:4.</summary>
    Profile1 = 1,
    
    /// <summary>Profile 2: 10/12-bit YUV 4:2:0.</summary>
    Profile2 = 2,
    
    /// <summary>Profile 3: 10/12-bit YUV 4:2:2, 4:4:4.</summary>
    Profile3 = 3
}

/// <summary>
/// VP9 frame types.
/// </summary>
public enum Vp9FrameType : byte
{
    /// <summary>Keyframe (intra-only, can be seeked to).</summary>
    KeyFrame = 0,
    
    /// <summary>Inter-frame (requires reference frames).</summary>
    InterFrame = 1
}

/// <summary>
/// VP9 color space values.
/// </summary>
public enum Vp9ColorSpace : byte
{
    /// <summary>Unknown color space.</summary>
    Unknown = 0,
    
    /// <summary>BT.601 (SMPTE 170M, NTSC).</summary>
    Bt601 = 1,
    
    /// <summary>BT.709 (ITU-R Rec. 709).</summary>
    Bt709 = 2,
    
    /// <summary>SMPTE 170M.</summary>
    Smpte170M = 3,
    
    /// <summary>SMPTE 240M.</summary>
    Smpte240M = 4,
    
    /// <summary>BT.2020 (Rec. 2020, HDR).</summary>
    Bt2020 = 5,
    
    /// <summary>Reserved.</summary>
    Reserved = 6,
    
    /// <summary>sRGB (RGB, Profiles 1/3 only).</summary>
    Srgb = 7
}

/// <summary>
/// VP9 chroma subsampling format.
/// </summary>
public enum Vp9ChromaFormat : byte
{
    /// <summary>4:4:4 subsampling.</summary>
    Yuv444 = 0,
    
    /// <summary>4:4:0 subsampling (vertically subsampled).</summary>
    Yuv440 = 1,
    
    /// <summary>4:2:2 subsampling (horizontally subsampled).</summary>
    Yuv422 = 2,
    
    /// <summary>4:2:0 subsampling (both dimensions).</summary>
    Yuv420 = 3
}

/// <summary>
/// VP9 uncompressed frame header information.
/// </summary>
public readonly struct Vp9FrameHeader
{
    /// <summary>VP9 profile (0-3).</summary>
    public Vp9Profile Profile { get; init; }
    
    /// <summary>Bit depth (8, 10, or 12 bits).</summary>
    public int BitDepth { get; init; }
    
    /// <summary>Frame type (keyframe or inter-frame).</summary>
    public Vp9FrameType FrameType { get; init; }
    
    /// <summary>Whether this frame should be displayed.</summary>
    public bool ShowFrame { get; init; }
    
    /// <summary>Whether error resilient mode is enabled.</summary>
    public bool ErrorResilientMode { get; init; }
    
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Render width (may differ from coded width for aspect ratio).</summary>
    public int RenderWidth { get; init; }
    
    /// <summary>Render height.</summary>
    public int RenderHeight { get; init; }
    
    /// <summary>Whether this displays an existing reference frame.</summary>
    public bool ShowExistingFrame { get; init; }
    
    /// <summary>Reference frame index to display (if ShowExistingFrame).</summary>
    public int FrameToShowMapIndex { get; init; }
    
    /// <summary>Color space of the frame.</summary>
    public Vp9ColorSpace ColorSpace { get; init; }
    
    /// <summary>Chroma subsampling format.</summary>
    public Vp9ChromaFormat ChromaFormat { get; init; }
    
    /// <summary>Whether full color range (0-255) or video range (16-235).</summary>
    public bool FullColorRange { get; init; }
    
    /// <summary>Whether frame uses intra-only mode.</summary>
    public bool IntraOnly { get; init; }
    
    /// <summary>Reference frame refresh flags (8 bits, one per slot).</summary>
    public byte RefreshFrameFlags { get; init; }
    
    /// <summary>Number of bytes consumed from the input buffer.</summary>
    public int BytesConsumed { get; init; }
    
    /// <summary>True if this is a keyframe or intra-only frame (can be seeked to).</summary>
    public bool IsKeyframe => FrameType == Vp9FrameType.KeyFrame || IntraOnly;
}

/// <summary>
/// Byte sizes of the frames in a superframe.
/// </summary>
/// <remarks>
/// Inline rather than an int[] because the sizes were being allocated on every parse, and the
/// parse runs once per Decode call whether or not the packet is a superframe. A superframe holds
/// at most eight frames, so the maximum fits in the struct.
/// </remarks>
[InlineArray(Vp9HeaderParser.MaxSuperframeFrames)]
public struct Vp9SuperframeSizes
{
    private int firstElement;
}

/// <summary>
/// VP9 superframe information. Superframes contain multiple frames.
/// </summary>
public readonly struct Vp9SuperframeInfo
{
    /// <summary>Whether a superframe index was found.</summary>
    public bool HasSuperframeIndex { get; init; }
    
    /// <summary>Number of frames in the superframe (1-8).</summary>
    public int FrameCount { get; init; }
    
    /// <summary>Byte sizes of each frame. Only the first <see cref="FrameCount"/> are meaningful.</summary>
    public Vp9SuperframeSizes FrameSizes { get; init; }
    
    /// <summary>Total size of the superframe index in bytes.</summary>
    public int IndexSize { get; init; }
}

/// <summary>
/// VP9 header parser for frame and superframe parsing.
/// </summary>
/// <remarks>
/// Parses VP9 uncompressed frame headers for demuxer and seeking support.
/// Actual decoding requires entropy decoding, transform, and prediction.
/// </remarks>
public static class Vp9HeaderParser
{
    /// <summary>VP9 frame marker value (2 bits, always 0b10).</summary>
    private const int FrameMarker = 0b10;

    /// <summary>Most frames a superframe can hold - the count field is three bits plus one.</summary>
    public const int MaxSuperframeFrames = 8;
    
    /// <summary>VP9 sync code bytes.</summary>
    private const byte SyncCode0 = 0x49; // 'I'
    private const byte SyncCode1 = 0x83;
    private const byte SyncCode2 = 0x42;
    
    /// <summary>Superframe marker: bits [7:5] must be 0b110 (0xC0).</summary>
    /// <remarks>
    /// VP9 superframe index byte format: 110NNSSS
    ///   bits [7:5] = 110 (marker)
    ///   bits [4:3] = bytes_per_framesize - 1
    ///   bits [2:0] = frames_in_superframe - 1
    /// </remarks>
    private const int SuperframeMarkerMask = 0xE0;
    private const int SuperframeMarkerValue = 0xC0;
    
    /// <summary>
    /// Attempts to parse a VP9 uncompressed frame header.
    /// </summary>
    /// <param name="data">Data containing the VP9 frame.</param>
    /// <param name="header">Parsed header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseFrameHeader(ReadOnlySpan<byte> data, out Vp9FrameHeader header)
    {
        header = default;
        
        if (data.Length < 3)
            return false;
        
        var reader = new Vp9BitReader(data);
        
        // frame_marker (2 bits) - must be 0b10
        int frameMarker = reader.ReadBits(2);
        if (frameMarker != FrameMarker)
            return false;
        
        // profile_low_bit (1 bit) + profile_high_bit (1 bit)
        int profileLowBit = reader.ReadBits(1);
        int profileHighBit = reader.ReadBits(1);
        var profile = (Vp9Profile)((profileHighBit << 1) | profileLowBit);
        
        // Profile 3 has an additional reserved bit
        if (profile == Vp9Profile.Profile3)
        {
            int reservedZero = reader.ReadBits(1);
            if (reservedZero != 0)
                return false; // Reserved bit should be 0
        }
        
        // show_existing_frame (1 bit)
        bool showExistingFrame = reader.ReadBits(1) == 1;
        int frameToShowMapIndex = 0;
        
        if (showExistingFrame)
        {
            // frame_to_show_map_idx (3 bits)
            frameToShowMapIndex = reader.ReadBits(3);
            
            header = new Vp9FrameHeader
            {
                Profile = profile,
                ShowExistingFrame = true,
                FrameToShowMapIndex = frameToShowMapIndex,
                ShowFrame = true,
                BytesConsumed = reader.BytePosition + (reader.BitPosition > 0 ? 1 : 0)
            };
            return true;
        }
        
        // frame_type (1 bit) - 0 = keyframe, 1 = inter
        var frameType = (Vp9FrameType)reader.ReadBits(1);
        
        // show_frame (1 bit)
        bool showFrame = reader.ReadBits(1) == 1;
        
        // error_resilient_mode (1 bit)
        bool errorResilientMode = reader.ReadBits(1) == 1;
        
        int bitDepth = 8;
        var colorSpace = Vp9ColorSpace.Unknown;
        var chromaFormat = Vp9ChromaFormat.Yuv420;
        bool fullColorRange = false;
        int width = 0;
        int height = 0;
        int renderWidth = 0;
        int renderHeight = 0;
        bool intraOnly = false;
        byte refreshFrameFlags = 0;
        
        if (frameType == Vp9FrameType.KeyFrame)
        {
            // frame_sync_code (24 bits)
            if (!reader.CanRead(24))
                return false;
            
            byte sync0 = (byte)reader.ReadBits(8);
            byte sync1 = (byte)reader.ReadBits(8);
            byte sync2 = (byte)reader.ReadBits(8);
            
            if (sync0 != SyncCode0 || sync1 != SyncCode1 || sync2 != SyncCode2)
                return false;
            
            // color_config
            if (!TryParseColorConfig(ref reader, profile, out bitDepth, out colorSpace, out chromaFormat, out fullColorRange))
                return false;
            
            // frame_size
            if (!TryParseFrameSize(ref reader, out width, out height))
                return false;
            
            // render_size
            if (!TryParseRenderSize(ref reader, width, height, out renderWidth, out renderHeight))
                return false;
            
            // All 8 reference slots are refreshed on keyframe
            refreshFrameFlags = 0xFF;
        }
        else
        {
            // Inter-frame
            if (!showFrame)
            {
                // intra_only (1 bit)
                intraOnly = reader.ReadBits(1) == 1;
            }
            
            if (!errorResilientMode)
            {
                // reset_frame_context (2 bits) - ignored for header parsing
                _ = reader.ReadBits(2);
            }
            
            if (intraOnly)
            {
                // frame_sync_code (24 bits)
                if (!reader.CanRead(24))
                    return false;
                
                byte sync0 = (byte)reader.ReadBits(8);
                byte sync1 = (byte)reader.ReadBits(8);
                byte sync2 = (byte)reader.ReadBits(8);
                
                if (sync0 != SyncCode0 || sync1 != SyncCode1 || sync2 != SyncCode2)
                    return false;
                
                // color_config for profiles 0/1 or 2/3
                if (profile > Vp9Profile.Profile1)
                {
                    if (!TryParseColorConfig(ref reader, profile, out bitDepth, out colorSpace, out chromaFormat, out fullColorRange))
                        return false;
                }
                else
                {
                    // Profile 0/1: 8-bit, BT.601, YUV420
                    bitDepth = 8;
                    colorSpace = Vp9ColorSpace.Bt601;
                    chromaFormat = Vp9ChromaFormat.Yuv420;
                }
                
                // refresh_frame_flags (8 bits)
                refreshFrameFlags = (byte)reader.ReadBits(8);
                
                // frame_size
                if (!TryParseFrameSize(ref reader, out width, out height))
                    return false;
                
                // render_size
                if (!TryParseRenderSize(ref reader, width, height, out renderWidth, out renderHeight))
                    return false;
            }
            else
            {
                // Normal inter-frame: reference frame indices and flags
                
                // refresh_frame_flags (8 bits)
                refreshFrameFlags = (byte)reader.ReadBits(8);
                
                // ref_frame_idx[3] (3 x 3 bits) + ref_frame_sign_bias[3] (3 x 1 bit)
                for (int i = 0; i < 3; i++)
                {
                    _ = reader.ReadBits(3); // ref_frame_idx
                    _ = reader.ReadBits(1); // ref_frame_sign_bias
                }
                
                // frame_size_with_refs
                bool foundRef = reader.ReadBits(1) == 1;
                if (!foundRef)
                {
                    foundRef = reader.ReadBits(1) == 1;
                    if (!foundRef)
                    {
                        foundRef = reader.ReadBits(1) == 1;
                    }
                }
                
                if (!foundRef)
                {
                    // frame_size
                    if (!TryParseFrameSize(ref reader, out width, out height))
                        return false;
                }
                // else: width/height inherited from reference frame (not available in header-only parsing)
                
                // render_size
                if (!TryParseRenderSize(ref reader, width, height, out renderWidth, out renderHeight))
                    return false;
                
                // allow_high_precision_mv (1 bit)
                _ = reader.ReadBits(1);
                
                // read_interpolation_filter
                if (!TryParseInterpolationFilter(ref reader))
                    return false;
            }
        }
        
        header = new Vp9FrameHeader
        {
            Profile = profile,
            BitDepth = bitDepth,
            FrameType = frameType,
            ShowFrame = showFrame,
            ErrorResilientMode = errorResilientMode,
            Width = width,
            Height = height,
            RenderWidth = renderWidth,
            RenderHeight = renderHeight,
            ShowExistingFrame = showExistingFrame,
            FrameToShowMapIndex = frameToShowMapIndex,
            ColorSpace = colorSpace,
            ChromaFormat = chromaFormat,
            FullColorRange = fullColorRange,
            IntraOnly = intraOnly,
            RefreshFrameFlags = refreshFrameFlags,
            BytesConsumed = reader.BytePosition + (reader.BitPosition > 0 ? 1 : 0)
        };
        
        return true;
    }
    
    /// <summary>
    /// Attempts to parse VP9 superframe index from the end of the data.
    /// </summary>
    /// <param name="data">Complete frame data.</param>
    /// <param name="info">Superframe information on success.</param>
    /// <returns>True if a valid superframe index was found.</returns>
    public static bool TryParseSuperframeIndex(ReadOnlySpan<byte> data, out Vp9SuperframeInfo info)
    {
        info = default;
        
        if (data.Length < 2)
            return false;
        
        // Superframe index is at the end of the frame
        byte lastByte = data[^1];
        
        // Check superframe marker (last 3 bits must be 0b110)
        if ((lastByte & SuperframeMarkerMask) != SuperframeMarkerValue)
        {
            // Not a superframe, treat as single frame
            var singleSize = default(Vp9SuperframeSizes);
            singleSize[0] = data.Length;
            info = new Vp9SuperframeInfo
            {
                HasSuperframeIndex = false,
                FrameCount = 1,
                FrameSizes = singleSize,
                IndexSize = 0
            };
            return true;
        }
        
        // Parse superframe header
        // Bits [4:3]: bytes_per_framesize - 1 (szBytes = value + 1)
        // Bits [2:0]: frames_in_superframe - 1 (frameCount = value + 1)
        int sizeFieldBytes = ((lastByte >> 3) & 0x03) + 1;
        int frameCount = (lastByte & 0x07) + 1;
        
        // Calculate index size: header (1) + frame sizes (frameCount * sizeFieldBytes) + trailer (1)
        int indexSize = 2 + frameCount * sizeFieldBytes;
        
        if (data.Length < indexSize)
            return false;
        
        // Verify header byte matches trailer byte
        byte headerByte = data[data.Length - indexSize];
        if (headerByte != lastByte)
            return false;
        
        // Parse frame sizes
        var frameSizes = default(Vp9SuperframeSizes);
        int offset = data.Length - indexSize + 1;
        
        for (int i = 0; i < frameCount; i++)
        {
            int frameSize = 0;
            for (int j = 0; j < sizeFieldBytes; j++)
            {
                frameSize |= data[offset++] << (j * 8);
            }
            frameSizes[i] = frameSize;
        }
        
        info = new Vp9SuperframeInfo
        {
            HasSuperframeIndex = true,
            FrameCount = frameCount,
            FrameSizes = frameSizes,
            IndexSize = indexSize
        };
        
        return true;
    }
    
    /// <summary>
    /// Checks if the data starts with a valid VP9 frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVp9Frame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return false;
        
        // Frame marker is in bits 6-7 of first byte (value 0b10)
        int frameMarker = (data[0] >> 6) & 0x03;
        return frameMarker == FrameMarker;
    }
    
    /// <summary>
    /// Gets a human-readable description of the frame header.
    /// </summary>
    public static string GetDescription(in Vp9FrameHeader header)
    {
        string frameType = header.IsKeyframe ? "I" : "P";
        string colorSpace = header.ColorSpace switch
        {
            Vp9ColorSpace.Bt601 => "BT.601",
            Vp9ColorSpace.Bt709 => "BT.709",
            Vp9ColorSpace.Bt2020 => "BT.2020",
            Vp9ColorSpace.Srgb => "sRGB",
            _ => "Unknown"
        };
        
        return $"VP9 Profile{(int)header.Profile} {header.Width}x{header.Height} " +
               $"{header.BitDepth}bit {colorSpace} {frameType}-frame";
    }
    
    #region Private Parsing Helpers
    
    private static bool TryParseColorConfig(
        ref Vp9BitReader reader,
        Vp9Profile profile,
        out int bitDepth,
        out Vp9ColorSpace colorSpace,
        out Vp9ChromaFormat chromaFormat,
        out bool fullColorRange)
    {
        bitDepth = 8;
        colorSpace = Vp9ColorSpace.Unknown;
        chromaFormat = Vp9ChromaFormat.Yuv420;
        fullColorRange = false;
        
        // Profiles 2/3 have high bit depth support
        if (profile >= Vp9Profile.Profile2)
        {
            // ten_or_twelve_bit (1 bit)
            int tenOrTwelveBit = reader.ReadBits(1);
            bitDepth = tenOrTwelveBit == 1 ? 12 : 10;
        }
        
        // color_space (3 bits)
        colorSpace = (Vp9ColorSpace)reader.ReadBits(3);
        
        if (colorSpace != Vp9ColorSpace.Srgb)
        {
            // color_range (1 bit)
            fullColorRange = reader.ReadBits(1) == 1;
            
            // Profiles 1/3 can have non-4:2:0 subsampling
            if (profile == Vp9Profile.Profile1 || profile == Vp9Profile.Profile3)
            {
                // subsampling_x (1 bit) + subsampling_y (1 bit)
                int subsamplingX = reader.ReadBits(1);
                int subsamplingY = reader.ReadBits(1);
                chromaFormat = (subsamplingX, subsamplingY) switch
                {
                    (0, 0) => Vp9ChromaFormat.Yuv444,
                    (0, 1) => Vp9ChromaFormat.Yuv440,
                    (1, 0) => Vp9ChromaFormat.Yuv422,
                    (1, 1) => Vp9ChromaFormat.Yuv420,
                    _ => Vp9ChromaFormat.Yuv420
                };
                
                // reserved_zero (1 bit)
                int reservedZero = reader.ReadBits(1);
                if (reservedZero != 0)
                    return false;
            }
            else
            {
                // Profiles 0/2: always 4:2:0
                chromaFormat = Vp9ChromaFormat.Yuv420;
            }
        }
        else
        {
            // sRGB: full range, 4:4:4
            fullColorRange = true;
            chromaFormat = Vp9ChromaFormat.Yuv444;
            
            if (profile == Vp9Profile.Profile1 || profile == Vp9Profile.Profile3)
            {
                // reserved_zero (1 bit)
                int reservedZero = reader.ReadBits(1);
                if (reservedZero != 0)
                    return false;
            }
        }
        
        return true;
    }
    
    private static bool TryParseFrameSize(ref Vp9BitReader reader, out int width, out int height)
    {
        width = 0;
        height = 0;
        
        if (!reader.CanRead(32))
            return false;
        
        // frame_width_minus_1 (16 bits) + frame_height_minus_1 (16 bits)
        width = reader.ReadBits(16) + 1;
        height = reader.ReadBits(16) + 1;
        
        return true;
    }
    
    private static bool TryParseRenderSize(
        ref Vp9BitReader reader,
        int width,
        int height,
        out int renderWidth,
        out int renderHeight)
    {
        renderWidth = width;
        renderHeight = height;
        
        if (!reader.CanRead(1))
            return false;
        
        // render_and_frame_size_different (1 bit)
        bool different = reader.ReadBits(1) == 1;
        
        if (different)
        {
            if (!reader.CanRead(32))
                return false;
            
            renderWidth = reader.ReadBits(16) + 1;
            renderHeight = reader.ReadBits(16) + 1;
        }
        
        return true;
    }
    
    private static bool TryParseInterpolationFilter(ref Vp9BitReader reader)
    {
        if (!reader.CanRead(1))
            return false;
        
        // is_filter_switchable (1 bit)
        bool isSwitchable = reader.ReadBits(1) == 1;
        
        if (!isSwitchable)
        {
            if (!reader.CanRead(2))
                return false;
            
            // raw_interpolation_filter (2 bits)
            _ = reader.ReadBits(2);
        }
        
        return true;
    }
    
    #endregion
}

/// <summary>
/// Simple bit reader for VP9 parsing.
/// </summary>
internal ref struct Vp9BitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;
    
    public int BytePosition => bytePosition;
    public int BitPosition => bitPosition;
    
    public Vp9BitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanRead(int bits)
    {
        int totalBits = bytePosition * 8 + bitPosition + bits;
        return totalBits <= data.Length * 8;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        int result = 0;
        
        for (int i = 0; i < count; i++)
        {
            if (bytePosition >= data.Length)
                return result;
            
            int bit = (data[bytePosition] >> (7 - bitPosition)) & 1;
            result = (result << 1) | bit;
            
            bitPosition++;
            if (bitPosition >= 8)
            {
                bitPosition = 0;
                bytePosition++;
            }
        }
        
        return result;
    }
}

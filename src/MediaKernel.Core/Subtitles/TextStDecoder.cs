// HDMV TextST Subtitle Decoder
// Decodes Blu-ray text subtitles (HDMV TextST stream type 0x92)
// Based on Blu-ray Disc specification for TextST

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// TextST segment types (from Blu-ray specification).
/// </summary>
public enum TextStSegmentType : byte
{
    DialogStyle = 0x81,
    DialogPresentation = 0x82,
    Unknown = 0xFF
}

/// <summary>
/// TextST text flow direction.
/// </summary>
public enum TextStTextFlow : byte
{
    LeftToRight = 0,
    RightToLeft = 1,
    TopToBottom = 2
}

/// <summary>
/// TextST halign (horizontal alignment).
/// </summary>
public enum TextStHAlign : byte
{
    Left = 0,
    Center = 1,
    Right = 2
}

/// <summary>
/// TextST valign (vertical alignment).
/// </summary>
public enum TextStVAlign : byte
{
    Top = 0,
    Middle = 1,
    Bottom = 2
}

/// <summary>
/// TextST font style flags.
/// </summary>
[Flags]
public enum TextStFontStyle : byte
{
    None = 0,
    Bold = 0x01,
    Italic = 0x02,
    Outline = 0x04
}

/// <summary>
/// TextST dialog region style definition.
/// </summary>
public readonly struct TextStRegionStyle
{
    public readonly byte RegionStyleId;
    public readonly ushort RegionHorizontalPosition;
    public readonly ushort RegionVerticalPosition;
    public readonly ushort RegionWidth;
    public readonly ushort RegionHeight;
    public readonly byte BackgroundColorPaletteEntry;
    public readonly TextStTextFlow TextFlow;
    public readonly TextStHAlign TextHAlign;
    public readonly TextStVAlign TextVAlign;
    public readonly byte LineSpace;
    public readonly byte FontIdRef;
    public readonly byte FontStyle;
    public readonly byte FontSize;
    public readonly byte FontColorPaletteEntry;
    public readonly byte OutlineColorPaletteEntry;
    public readonly byte OutlineThickness;

    public TextStRegionStyle(ReadOnlySpan<byte> data)
    {
        if (data.Length < 25)
        {
            this = default;
            return;
        }

        RegionStyleId = data[0];
        RegionHorizontalPosition = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1));
        RegionVerticalPosition = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3));
        RegionWidth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5));
        RegionHeight = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(7));
        BackgroundColorPaletteEntry = data[9];
        TextFlow = (TextStTextFlow)(data[10] >> 6);
        TextHAlign = (TextStHAlign)((data[10] >> 4) & 0x03);
        TextVAlign = (TextStVAlign)((data[10] >> 2) & 0x03);
        LineSpace = data[11];
        FontIdRef = data[12];
        FontStyle = data[13];
        FontSize = data[14];
        FontColorPaletteEntry = data[15];
        OutlineColorPaletteEntry = data[16];
        OutlineThickness = data[17];
        // bytes 18-24 are user styles (skip for now)
    }

    public const int Size = 25;
}

/// <summary>
/// TextST palette entry (YCbCrT format).
/// </summary>
public readonly struct TextStPaletteEntry
{
    public readonly byte Index;
    public readonly byte Y;
    public readonly byte Cr;
    public readonly byte Cb;
    public readonly byte T; // Transparency (0=opaque, 255=transparent)
    public readonly uint Argb;

    public TextStPaletteEntry(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            this = default;
            return;
        }

        Index = data[0];
        Y = data[1];
        Cr = data[2];
        Cb = data[3];
        T = data[4];

        // Convert YCbCr to RGB (BT.709 coefficients, limited range)
        float y = Y;
        float cr = Cr - 128;
        float cb = Cb - 128;

        int r = (int)(y + 1.402f * cr);
        int g = (int)(y - 0.34414f * cb - 0.71414f * cr);
        int b = (int)(y + 1.772f * cb);

        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        int a = 255 - T; // Convert transparency to alpha

        Argb = (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    public const int Size = 5;
}

/// <summary>
/// TextST dialog style segment containing palette and region styles.
/// </summary>
public sealed class TextStDialogStyle
{
    public ushort Width { get; private set; }
    public ushort Height { get; private set; }
    public byte FrameRate { get; private set; }
    public uint[] Palette { get; } = new uint[256];
    public TextStRegionStyle[] RegionStyles { get; private set; } = [];
    public TextStRegionStyle DefaultStyle { get; private set; }

    public bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14)
            return false;

        // Segment header: player_version_flag (1 bit), reserved (7 bits)
        // int playerVersionFlag = data[0] >> 7;

        int offset = 1;

        // Dialog region info
        // width 12 bits, height 12 bits
        Width = (ushort)(((data[offset] & 0x0F) << 8) | data[offset + 1]);
        Height = (ushort)((data[offset + 2] << 4) | ((data[offset + 3] >> 4) & 0x0F));
        FrameRate = (byte)(data[offset + 3] & 0x0F);
        offset += 4;

        // Palette update
        if (offset + 2 > data.Length)
            return false;

        ushort paletteSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        offset += 2;

        if (offset + paletteSize > data.Length)
            return false;

        // Parse palette entries
        int paletteEnd = offset + paletteSize;
        while (offset + TextStPaletteEntry.Size <= paletteEnd)
        {
            var entry = new TextStPaletteEntry(data.Slice(offset));
            Palette[entry.Index] = entry.Argb;
            offset += TextStPaletteEntry.Size;
        }
        offset = paletteEnd;

        // Region styles count
        if (offset >= data.Length)
            return false;

        byte regionStyleCount = data[offset];
        offset++;

        // Parse region styles
        var styles = new List<TextStRegionStyle>(regionStyleCount);
        for (int i = 0; i < regionStyleCount && offset + TextStRegionStyle.Size <= data.Length; i++)
        {
            var style = new TextStRegionStyle(data.Slice(offset));
            styles.Add(style);
            offset += TextStRegionStyle.Size;
        }
        RegionStyles = styles.ToArray();

        if (RegionStyles.Length > 0)
            DefaultStyle = RegionStyles[0];

        return true;
    }
}

/// <summary>
/// TextST inline data types (escape codes).
/// </summary>
public enum TextStDataType : byte
{
    String = 0x01,
    FontId = 0x02,
    FontStyle = 0x03,
    FontSize = 0x04,
    FontColor = 0x05,
    NewLine = 0x0A,
    ResetStyle = 0x0B
}

/// <summary>
/// TextST text segment with style information.
/// </summary>
public readonly struct TextStTextSegment
{
    public readonly string Text;
    public readonly TextStFontStyle FontStyle;
    public readonly byte FontSize;
    public readonly uint FontColor;
    public readonly uint OutlineColor;
    public readonly bool IsNewLine;

    public TextStTextSegment(string text, TextStFontStyle fontStyle, byte fontSize, 
        uint fontColor, uint outlineColor, bool isNewLine = false)
    {
        Text = text;
        FontStyle = fontStyle;
        FontSize = fontSize;
        FontColor = fontColor;
        OutlineColor = outlineColor;
        IsNewLine = isNewLine;
    }
}

/// <summary>
/// Decoded TextST dialog presentation subtitle.
/// </summary>
public sealed class TextStSubtitle
{
    public long StartPts { get; init; }
    public long EndPts { get; init; }
    public bool Forced { get; init; }
    public List<TextStTextSegment> Segments { get; } = new();
    public TextStRegionStyle RegionStyle { get; init; }
    
    public string GetPlainText()
    {
        var sb = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (segment.IsNewLine)
                sb.AppendLine();
            else
                sb.Append(segment.Text);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// HDMV TextST subtitle decoder for Blu-ray discs.
/// Decodes text subtitles embedded in MPEG-TS stream type 0x92.
/// </summary>
public sealed class TextStDecoder
{
    private TextStDialogStyle? dialogStyle;
    private readonly List<TextStSubtitle> decodedSubtitles = new();

    /// <summary>
    /// Gets the current dialog style (palette and region definitions).
    /// </summary>
    public TextStDialogStyle? DialogStyle => dialogStyle;

    /// <summary>
    /// Gets decoded subtitles from the last decode operation.
    /// </summary>
    public IReadOnlyList<TextStSubtitle> DecodedSubtitles => decodedSubtitles;

    /// <summary>
    /// Decodes a TextST PES packet.
    /// </summary>
    /// <param name="data">PES packet data (after PES header).</param>
    /// <param name="pts">Presentation timestamp in 90kHz units.</param>
    /// <returns>List of decoded subtitles.</returns>
    public IReadOnlyList<TextStSubtitle> Decode(ReadOnlySpan<byte> data, long pts)
    {
        decodedSubtitles.Clear();

        if (data.Length < 3)
            return decodedSubtitles;

        // TextST segment header: segment_type (8), segment_length (16)
        int offset = 0;
        while (offset + 3 <= data.Length)
        {
            byte segmentType = data[offset];
            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 1));
            offset += 3;

            if (offset + segmentLength > data.Length)
                break;

            var segmentData = data.Slice(offset, segmentLength);

            switch ((TextStSegmentType)segmentType)
            {
                case TextStSegmentType.DialogStyle:
                    dialogStyle = new TextStDialogStyle();
                    dialogStyle.Parse(segmentData);
                    break;

                case TextStSegmentType.DialogPresentation:
                    DecodeDialogPresentation(segmentData, pts);
                    break;
            }

            offset += segmentLength;
        }

        return decodedSubtitles;
    }

    private void DecodeDialogPresentation(ReadOnlySpan<byte> data, long pts)
    {
        if (data.Length < 7)
            return;

        // Presentation header
        // start_pts (33 bits as 5 bytes), end_pts (33 bits as 5 bytes)
        // palette_update_flag (1 bit), continuous_present_flag (1 bit), forced_on_flag (1 bit)

        int offset = 0;

        // Skip start/end PTS from presentation (use PES PTS instead)
        // Actually parse start_pts (33 bits) and end_pts (33 bits)
        long startPts = ReadPts33(data.Slice(offset));
        offset += 5;

        if (offset + 5 > data.Length)
            return;

        long endPts = ReadPts33(data.Slice(offset));
        offset += 5;

        if (offset >= data.Length)
            return;

        byte flags = data[offset];
        bool paletteUpdateFlag = (flags & 0x80) != 0;
        // bool continuousPresent = (flags & 0x40) != 0;
        bool forcedOn = (flags & 0x20) != 0;
        offset++;

        // Palette update (if flag set)
        if (paletteUpdateFlag && dialogStyle != null)
        {
            if (offset + 2 > data.Length)
                return;

            ushort paletteSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
            offset += 2;

            int paletteEnd = Math.Min(offset + paletteSize, data.Length);
            while (offset + TextStPaletteEntry.Size <= paletteEnd)
            {
                var entry = new TextStPaletteEntry(data.Slice(offset));
                dialogStyle.Palette[entry.Index] = entry.Argb;
                offset += TextStPaletteEntry.Size;
            }
            offset = paletteEnd;
        }

        // Region count
        if (offset >= data.Length)
            return;

        byte regionCount = data[offset];
        offset++;

        // Parse regions
        for (int i = 0; i < regionCount && offset + 4 <= data.Length; i++)
        {
            var subtitle = DecodeRegion(data.Slice(offset), startPts, endPts, forcedOn, out int bytesRead);
            if (subtitle != null)
                decodedSubtitles.Add(subtitle);
            offset += bytesRead;
        }
    }

    private TextStSubtitle? DecodeRegion(ReadOnlySpan<byte> data, long startPts, long endPts, 
        bool forced, out int bytesRead)
    {
        bytesRead = 0;

        if (data.Length < 4)
            return null;

        // Region header
        // continuous_present_flag (1), forced_on_flag (1), reserved (6)
        // region_style_id_ref (8)
        // region_data_length (16)

        byte regionFlags = data[0];
        bool regionForced = (regionFlags & 0x40) != 0;
        byte regionStyleId = data[1];
        ushort regionDataLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
        bytesRead = 4 + regionDataLength;

        if (data.Length < bytesRead)
        {
            bytesRead = data.Length;
            return null;
        }

        // Find matching region style
        var regionStyle = GetRegionStyle(regionStyleId);

        var subtitle = new TextStSubtitle
        {
            StartPts = startPts,
            EndPts = endPts,
            Forced = forced || regionForced,
            RegionStyle = regionStyle
        };

        // Parse inline data
        int dataOffset = 4;
        int dataEnd = 4 + regionDataLength;

        // Current style state
        var currentFontStyle = (TextStFontStyle)regionStyle.FontStyle;
        byte currentFontSize = regionStyle.FontSize;
        uint currentFontColor = GetPaletteColor(regionStyle.FontColorPaletteEntry);
        uint currentOutlineColor = GetPaletteColor(regionStyle.OutlineColorPaletteEntry);

        while (dataOffset + 3 <= dataEnd && dataOffset < data.Length)
        {
            byte code = data[dataOffset];

            // Check for escape code (0x1B)
            if (code != 0x1B)
            {
                dataOffset++;
                continue;
            }

            if (dataOffset + 3 > data.Length)
                break;

            byte type = data[dataOffset + 1];
            byte length = data[dataOffset + 2];
            dataOffset += 3;

            if (dataOffset + length > data.Length || dataOffset + length > dataEnd)
                break;

            var payload = data.Slice(dataOffset, length);

            switch ((TextStDataType)type)
            {
                case TextStDataType.String:
                    string text = Encoding.UTF8.GetString(payload);
                    subtitle.Segments.Add(new TextStTextSegment(
                        text, currentFontStyle, currentFontSize, 
                        currentFontColor, currentOutlineColor));
                    break;

                case TextStDataType.FontId:
                    // Font ID reference (ignored for now, would need font mapping)
                    break;

                case TextStDataType.FontStyle:
                    if (length >= 3)
                    {
                        currentFontStyle = TextStFontStyle.None;
                        if ((payload[0] & 0x01) != 0) currentFontStyle |= TextStFontStyle.Bold;
                        if ((payload[0] & 0x02) != 0) currentFontStyle |= TextStFontStyle.Italic;
                        if ((payload[0] & 0x04) != 0) currentFontStyle |= TextStFontStyle.Outline;
                        currentOutlineColor = GetPaletteColor(payload[1]);
                        // payload[2] is outline thickness
                    }
                    break;

                case TextStDataType.FontSize:
                    if (length >= 1)
                        currentFontSize = payload[0];
                    break;

                case TextStDataType.FontColor:
                    if (length >= 2)
                        currentFontColor = GetPaletteColor(payload[1]);
                    break;

                case TextStDataType.NewLine:
                    subtitle.Segments.Add(new TextStTextSegment(
                        string.Empty, currentFontStyle, currentFontSize, 
                        currentFontColor, currentOutlineColor, isNewLine: true));
                    break;

                case TextStDataType.ResetStyle:
                    // Reset to region default style
                    currentFontStyle = (TextStFontStyle)regionStyle.FontStyle;
                    currentFontSize = regionStyle.FontSize;
                    currentFontColor = GetPaletteColor(regionStyle.FontColorPaletteEntry);
                    currentOutlineColor = GetPaletteColor(regionStyle.OutlineColorPaletteEntry);
                    break;
            }

            dataOffset += length;
        }

        return subtitle.Segments.Count > 0 ? subtitle : null;
    }

    private TextStRegionStyle GetRegionStyle(byte styleId)
    {
        if (dialogStyle?.RegionStyles != null)
        {
            foreach (var style in dialogStyle.RegionStyles)
            {
                if (style.RegionStyleId == styleId)
                    return style;
            }
        }

        return dialogStyle?.DefaultStyle ?? default;
    }

    private uint GetPaletteColor(byte index)
    {
        if (dialogStyle?.Palette != null && index < dialogStyle.Palette.Length)
            return dialogStyle.Palette[index];
        return 0xFFFFFFFF; // Default white
    }

    private static long ReadPts33(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return 0;

        // 33-bit PTS packed into 5 bytes (MSB first, bit 32 in high bit of first byte)
        long pts = ((long)(data[0] & 0x01) << 32) |
                   ((long)data[1] << 24) |
                   ((long)data[2] << 16) |
                   ((long)data[3] << 8) |
                   data[4];
        return pts;
    }

    /// <summary>
    /// Converts a TextST subtitle to a SubtitleCue.
    /// </summary>
    public static SubtitleCue ToSubtitleCue(TextStSubtitle subtitle)
    {
        // Convert 90kHz PTS to microseconds
        long startUs = subtitle.StartPts * 1_000_000 / 90_000;
        long endUs = subtitle.EndPts * 1_000_000 / 90_000;

        string text = subtitle.GetPlainText();

        var position = new SubtitlePosition(
            50f, // Default center
            subtitle.RegionStyle.TextVAlign switch
            {
                TextStVAlign.Top => 10f,
                TextStVAlign.Middle => 50f,
                _ => 90f
            },
            subtitle.RegionStyle.TextHAlign switch
            {
                TextStHAlign.Left => SubtitleAlignment.Left,
                TextStHAlign.Right => SubtitleAlignment.Right,
                _ => SubtitleAlignment.Center
            },
            subtitle.RegionStyle.TextVAlign switch
            {
                TextStVAlign.Top => SubtitleVerticalAlignment.Top,
                TextStVAlign.Middle => SubtitleVerticalAlignment.Middle,
                _ => SubtitleVerticalAlignment.Bottom
            });

        return new SubtitleCue(startUs, endUs, text, SubtitleType.StyledText, null, position);
    }

    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        dialogStyle = null;
        decodedSubtitles.Clear();
    }
}

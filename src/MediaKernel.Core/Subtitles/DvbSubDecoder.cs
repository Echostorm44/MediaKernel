// DVB Subtitle Decoder
// EN 300 743 - Digital Video Broadcasting (DVB) subtitling systems
// Supports European and Asian broadcast subtitle formats

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoder for DVB (Digital Video Broadcasting) subtitles.
/// Handles segment parsing, RLE decompression, and region composition.
/// </summary>
public sealed class DvbSubDecoder
{
    private readonly Dictionary<byte, DvbSubClut> cluts = [];
    private readonly Dictionary<byte, DvbSubRegion> regions = [];
    private readonly Dictionary<ushort, DvbSubObject> objects = [];
    
    private DvbSubPage? currentPage;
    private DvbSubDisplayDefinition displayDefinition = DvbSubDisplayDefinition.DefaultSD;
    private long currentPts;
    
    /// <summary>
    /// Event raised when a subtitle display is ready.
    /// </summary>
    public event Action<DvbSubtitleImage>? SubtitleReady;
    
    /// <summary>
    /// Decodes a DVB subtitle PES packet.
    /// </summary>
    /// <param name="data">PES packet data (after PES header).</param>
    /// <param name="ptsMicroseconds">Presentation timestamp in microseconds.</param>
    /// <returns>List of decoded subtitle images.</returns>
    public List<DvbSubtitleImage> Decode(ReadOnlySpan<byte> data, long ptsMicroseconds)
    {
        var images = new List<DvbSubtitleImage>();
        currentPts = ptsMicroseconds;
        
        // Skip data_identifier and subtitle_stream_id (2 bytes)
        if (data.Length < 2)
            return images;
        
        int offset = 2;
        
        // Parse segments until end of data
        while (offset + 6 <= data.Length)
        {
            // Sync byte check
            if (data[offset] != 0x0F)
            {
                offset++;
                continue;
            }
            
            var segmentType = (DvbSubSegmentType)data[offset + 1];
            ushort pageId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2));
            
            offset += 6;
            
            if (offset + segmentLength > data.Length)
                break;
            
            var segmentData = data.Slice(offset, segmentLength);
            ProcessSegment(segmentType, pageId, segmentData, images);
            
            offset += segmentLength;
        }
        
        return images;
    }
    
    /// <summary>
    /// Processes a single segment.
    /// </summary>
    private void ProcessSegment(DvbSubSegmentType type, ushort pageId, ReadOnlySpan<byte> data, List<DvbSubtitleImage> images)
    {
        switch (type)
        {
            case DvbSubSegmentType.PageComposition:
                ProcessPageComposition(data);
                break;
                
            case DvbSubSegmentType.RegionComposition:
                ProcessRegionComposition(data);
                break;
                
            case DvbSubSegmentType.ClutDefinition:
                ProcessClutDefinition(data);
                break;
                
            case DvbSubSegmentType.ObjectData:
                ProcessObjectData(data);
                break;
                
            case DvbSubSegmentType.DisplayDefinition:
                ProcessDisplayDefinition(data);
                break;
                
            case DvbSubSegmentType.EndOfDisplay:
                RenderPage(images);
                break;
                
            case DvbSubSegmentType.Stuffing:
            case DvbSubSegmentType.AlternateClut:
                // Ignore stuffing and unsupported alternate CLUT
                break;
        }
    }
    
    /// <summary>
    /// Processes a page composition segment.
    /// </summary>
    private void ProcessPageComposition(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return;
        
        byte timeout = data[0];
        byte versionState = data[1];
        byte version = (byte)((versionState >> 4) & 0x0F);
        var state = (DvbSubPageState)((versionState >> 2) & 0x03);
        
        // Create or update page
        if (currentPage == null || state == DvbSubPageState.ModeChange)
        {
            // Mode change - clear all buffers
            cluts.Clear();
            regions.Clear();
            objects.Clear();
            currentPage = new DvbSubPage();
        }
        else if (currentPage.Version == version)
        {
            // Same version, no change
            return;
        }
        
        currentPage.TimeoutSeconds = timeout;
        currentPage.Version = version;
        currentPage.State = state;
        currentPage.Regions.Clear();
        
        // Parse region definitions
        int offset = 2;
        while (offset + 6 <= data.Length)
        {
            byte regionId = data[offset];
            // byte reserved = data[offset + 1];
            ushort horizontalAddress = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            ushort verticalAddress = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2));
            
            currentPage.Regions.Add(new DvbSubRegionDef(regionId, horizontalAddress, verticalAddress));
            offset += 6;
        }
    }
    
    /// <summary>
    /// Processes a region composition segment.
    /// </summary>
    private void ProcessRegionComposition(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return;
        
        byte regionId = data[0];
        byte versionFill = data[1];
        byte version = (byte)((versionFill >> 4) & 0x0F);
        bool fillFlag = (versionFill & 0x08) != 0;
        
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        
        byte depthClut = data[6];
        var depth = (DvbSubRegionDepth)((depthClut >> 5) & 0x07);
        byte clutId = data[7];
        
        byte codes = data[8];
        byte backgroundCode8 = data[9];
        byte backgroundCode4 = (byte)((codes >> 4) & 0x0F);
        byte backgroundCode2 = (byte)((codes >> 2) & 0x03);
        
        // Get or create region
        if (!regions.TryGetValue(regionId, out var region))
        {
            region = new DvbSubRegion(regionId);
            regions[regionId] = region;
        }
        
        // Check version
        if (region.Version == version && region.Width == width && region.Height == height)
            return;
        
        region.Version = version;
        region.Width = width;
        region.Height = height;
        region.Depth = depth;
        region.ClutId = clutId;
        region.Objects.Clear();
        
        // Determine background code based on depth
        region.BackgroundPixelCode = depth switch
        {
            DvbSubRegionDepth.Bpp2 => backgroundCode2,
            DvbSubRegionDepth.Bpp4 => backgroundCode4,
            DvbSubRegionDepth.Bpp8 => backgroundCode8,
            _ => backgroundCode4
        };
        
        if (fillFlag)
        {
            region.EnsurePixelBuffer();
        }
        
        // Parse object references
        int offset = 10;
        while (offset + 6 <= data.Length)
        {
            ushort objectId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            byte typeProvider = data[offset + 2];
            var objectType = (DvbSubObjectType)((typeProvider >> 6) & 0x03);
            byte provider = (byte)((typeProvider >> 4) & 0x03);
            ushort horizontalPos = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2)) & 0x0FFF);
            ushort verticalPos = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2)) & 0x0FFF);
            
            offset += 6;
            
            byte fgCode = 0, bgCode = 0;
            if (objectType == DvbSubObjectType.BasicCharacter || objectType == DvbSubObjectType.CompositeString)
            {
                if (offset + 2 <= data.Length)
                {
                    fgCode = data[offset];
                    bgCode = data[offset + 1];
                    offset += 2;
                }
            }
            
            region.Objects.Add(new DvbSubObjectRef(objectId, objectType, fgCode, bgCode, horizontalPos, verticalPos));
        }
    }
    
    /// <summary>
    /// Processes a CLUT definition segment.
    /// </summary>
    private void ProcessClutDefinition(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return;
        
        byte clutId = data[0];
        byte version = (byte)((data[1] >> 4) & 0x0F);
        
        // Get or create CLUT
        if (!cluts.TryGetValue(clutId, out var clut))
        {
            clut = new DvbSubClut(clutId);
            cluts[clutId] = clut;
        }
        
        if (clut.Version == version)
            return;
        
        clut.Version = version;
        
        // Parse CLUT entries
        int offset = 2;
        while (offset + 2 <= data.Length)
        {
            byte entryId = data[offset];
            byte flags = data[offset + 1];
            
            bool flag2Bit = (flags & 0x80) != 0;
            bool flag4Bit = (flags & 0x40) != 0;
            bool flag8Bit = (flags & 0x20) != 0;
            bool fullRange = (flags & 0x01) != 0;
            
            offset += 2;
            
            DvbSubColor color;
            if (fullRange)
            {
                // Full range: 4 bytes (Y, Cr, Cb, T)
                if (offset + 4 > data.Length)
                    break;
                
                byte y = data[offset];
                byte cr = data[offset + 1];
                byte cb = data[offset + 2];
                byte t = data[offset + 3];
                offset += 4;
                
                color = DvbSubColor.FromYCbCrT(y, cb, cr, t);
            }
            else
            {
                // Reduced range: 2 bytes
                if (offset + 2 > data.Length)
                    break;
                
                // Y[7:2] Cr[7:6] : Cr[5:2] Cb[7:4] T[7:6]
                byte byte1 = data[offset];
                byte byte2 = data[offset + 1];
                offset += 2;
                
                byte y = (byte)((byte1 & 0xFC) | ((byte1 >> 6) & 0x03));
                byte cr = (byte)(((byte1 & 0x03) << 6) | ((byte2 & 0xF0) >> 2) | 0x10);
                byte cb = (byte)(((byte2 & 0x0F) << 4) | 0x08);
                byte t = (byte)(((byte2 & 0xC0)) | 0x3F); // Simplified
                
                color = DvbSubColor.FromYCbCrT(y, cb, cr, t);
            }
            
            // Apply to appropriate palettes
            if (flag2Bit && entryId < 4)
                clut.Palette2Bit[entryId] = color;
            if (flag4Bit && entryId < 16)
                clut.Palette4Bit[entryId] = color;
            if (flag8Bit)
                clut.Palette8Bit[entryId] = color;
        }
    }
    
    /// <summary>
    /// Processes an object data segment.
    /// </summary>
    private void ProcessObjectData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return;
        
        ushort objectId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
        byte versionCoding = data[2];
        byte version = (byte)((versionCoding >> 4) & 0x0F);
        var codingMethod = (DvbSubObjectCoding)((versionCoding >> 2) & 0x03);
        bool nonModifyingColor = (versionCoding & 0x01) != 0;
        
        // Get or create object
        if (!objects.TryGetValue(objectId, out var obj))
        {
            obj = new DvbSubObject(objectId);
            objects[objectId] = obj;
        }
        
        obj.Version = version;
        obj.CodingMethod = codingMethod;
        obj.NonModifyingColorFlag = nonModifyingColor;
        
        if (codingMethod == DvbSubObjectCoding.Pixels)
        {
            // Bitmap object
            if (data.Length < 7)
                return;
            
            ushort topFieldLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2));
            ushort bottomFieldLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5, 2));
            
            int offset = 7;
            
            if (offset + topFieldLength <= data.Length)
            {
                obj.TopFieldData = data.Slice(offset, topFieldLength).ToArray();
                offset += topFieldLength;
            }
            
            if (bottomFieldLength > 0 && offset + bottomFieldLength <= data.Length)
            {
                obj.BottomFieldData = data.Slice(offset, bottomFieldLength).ToArray();
            }
            else
            {
                // Progressive - bottom field same as top
                obj.BottomFieldData = null;
            }
        }
        else
        {
            // Character object (not commonly used)
            // TODO: Implement character object decoding if needed
            obj.Text = null;
        }
    }
    
    /// <summary>
    /// Processes a display definition segment.
    /// </summary>
    private void ProcessDisplayDefinition(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return;
        
        byte versionFlags = data[0];
        byte version = (byte)((versionFlags >> 4) & 0x0F);
        bool displayWindow = (versionFlags & 0x08) != 0;
        
        ushort displayWidth = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2)) + 1);
        ushort displayHeight = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2)) + 1);
        
        ushort windowHMin = 0, windowHMax = (ushort)(displayWidth - 1);
        ushort windowVMin = 0, windowVMax = (ushort)(displayHeight - 1);
        
        if (displayWindow && data.Length >= 13)
        {
            windowHMin = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5, 2));
            windowHMax = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(7, 2));
            windowVMin = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(9, 2));
            windowVMax = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(11, 2));
        }
        
        displayDefinition = new DvbSubDisplayDefinition(
            version, displayWindow,
            displayWidth, displayHeight,
            windowHMin, windowHMax,
            windowVMin, windowVMax);
    }
    
    /// <summary>
    /// Renders the current page composition.
    /// </summary>
    private void RenderPage(List<DvbSubtitleImage> images)
    {
        if (currentPage == null)
            return;
        
        long duration = currentPage.TimeoutSeconds * 1_000_000L;
        
        foreach (var regionDef in currentPage.Regions)
        {
            if (!regions.TryGetValue(regionDef.RegionId, out var region))
                continue;
            
            // Ensure region has pixel buffer
            region.EnsurePixelBuffer();
            
            // Decode objects into region
            foreach (var objRef in region.Objects)
            {
                if (objects.TryGetValue(objRef.ObjectId, out var obj))
                {
                    DecodeObjectIntoRegion(obj, region, objRef);
                }
            }
            
            // Get CLUT for region
            var clut = cluts.TryGetValue(region.ClutId, out var c) ? c : new DvbSubClut(0);
            var palette = clut.GetPalette(region.Depth);
            
            // Convert palette indices to BGRA
            var pixelData = ConvertToArgb(region.PixelBuffer!, region.Width, region.Height, palette);
            
            // Apply windowing offset
            ushort x = (ushort)(regionDef.HorizontalAddress + (displayDefinition.DisplayWindow ? displayDefinition.WindowHorizontalMin : 0));
            ushort y = (ushort)(regionDef.VerticalAddress + (displayDefinition.DisplayWindow ? displayDefinition.WindowVerticalMin : 0));
            
            var image = new DvbSubtitleImage(currentPts, duration, x, y, region.Width, region.Height, pixelData);
            images.Add(image);
            SubtitleReady?.Invoke(image);
        }
    }
    
    /// <summary>
    /// Decodes an object's RLE data into a region's pixel buffer.
    /// </summary>
    private void DecodeObjectIntoRegion(DvbSubObject obj, DvbSubRegion region, DvbSubObjectRef objRef)
    {
        if (obj.TopFieldData == null || region.PixelBuffer == null)
            return;
        
        // Decode top field (even lines)
        DecodeRleField(obj.TopFieldData, region, objRef.HorizontalPosition, objRef.VerticalPosition, 2);
        
        // Decode bottom field (odd lines) - use same data if progressive
        var bottomData = obj.BottomFieldData ?? obj.TopFieldData;
        DecodeRleField(bottomData, region, objRef.HorizontalPosition, (ushort)(objRef.VerticalPosition + 1), 2);
    }
    
    /// <summary>
    /// Decodes an RLE field into a region at the specified position.
    /// </summary>
    private void DecodeRleField(byte[] rleData, DvbSubRegion region, int startX, int startY, int lineStride)
    {
        var reader = new RleBitReader(rleData);
        int x = startX;
        int y = startY;
        int maxY = region.Height;
        int maxX = region.Width;
        
        while (!reader.EndOfData && y < maxY)
        {
            byte dataType = reader.ReadBits(8);
            
            switch (dataType)
            {
                case 0x10: // 2-bit pixel data
                    Decode2BitPixelData(ref reader, region, ref x, ref y, startX, maxX, maxY, lineStride);
                    break;
                    
                case 0x11: // 4-bit pixel data
                    Decode4BitPixelData(ref reader, region, ref x, ref y, startX, maxX, maxY, lineStride);
                    break;
                    
                case 0x12: // 8-bit pixel data
                    Decode8BitPixelData(ref reader, region, ref x, ref y, startX, maxX, maxY, lineStride);
                    break;
                    
                case 0x20: // 2-to-4 bit map table
                case 0x21: // 2-to-8 bit map table
                case 0x22: // 4-to-8 bit map table
                    // Skip map tables (not commonly used)
                    SkipMapTable(ref reader, dataType);
                    break;
                    
                case 0xF0: // End of line
                    x = startX;
                    y += lineStride;
                    reader.ByteAlign();
                    break;
                    
                default:
                    // Unknown data type, skip
                    break;
            }
        }
    }
    
    /// <summary>
    /// Decodes 2-bit RLE pixel data.
    /// </summary>
    private static void Decode2BitPixelData(ref RleBitReader reader, DvbSubRegion region, 
        ref int x, ref int y, int startX, int maxX, int maxY, int lineStride)
    {
        var buffer = region.PixelBuffer!;
        int width = region.Width;
        
        while (!reader.EndOfData && y < maxY)
        {
            byte pixel = reader.ReadBits(2);
            
            if (pixel != 0)
            {
                // Single pixel
                if (x < maxX)
                {
                    buffer[y * width + x] = pixel;
                    x++;
                }
            }
            else
            {
                // Escape sequence
                byte switchBit = reader.ReadBits(1);
                if (switchBit == 1)
                {
                    // Run length 3-10
                    byte runLength = (byte)(reader.ReadBits(3) + 3);
                    byte color = reader.ReadBits(2);
                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                }
                else
                {
                    byte switch2 = reader.ReadBits(1);
                    if (switch2 == 0)
                    {
                        byte switch3 = reader.ReadBits(2);
                        switch (switch3)
                        {
                            case 0: // End of 2-bit string
                                reader.ByteAlign();
                                return;
                            case 1: // Run of 2 pixels
                                WritePixels(buffer, width, ref x, y, maxX, 2, 0);
                                break;
                            case 2: // Run length 12-27
                                {
                                    byte runLength = (byte)(reader.ReadBits(4) + 12);
                                    byte color = reader.ReadBits(2);
                                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                                }
                                break;
                            case 3: // Run length 29-284
                                {
                                    byte runLength = (byte)(reader.ReadBits(8) + 29);
                                    byte color = reader.ReadBits(2);
                                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                                }
                                break;
                        }
                    }
                    else
                    {
                        // Single pixel color 0
                        if (x < maxX)
                        {
                            buffer[y * width + x] = 0;
                            x++;
                        }
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Decodes 4-bit RLE pixel data.
    /// </summary>
    private static void Decode4BitPixelData(ref RleBitReader reader, DvbSubRegion region,
        ref int x, ref int y, int startX, int maxX, int maxY, int lineStride)
    {
        var buffer = region.PixelBuffer!;
        int width = region.Width;
        
        while (!reader.EndOfData && y < maxY)
        {
            byte pixel = reader.ReadBits(4);
            
            if (pixel != 0)
            {
                // Single pixel
                if (x < maxX)
                {
                    buffer[y * width + x] = pixel;
                    x++;
                }
            }
            else
            {
                // Escape sequence
                byte switchBit = reader.ReadBits(1);
                if (switchBit == 0)
                {
                    byte runLength = reader.ReadBits(3);
                    if (runLength == 0)
                    {
                        // End of 4-bit string
                        reader.ByteAlign();
                        return;
                    }
                    // Run of background (2 + runLength)
                    WritePixels(buffer, width, ref x, y, maxX, (byte)(runLength + 2), 0);
                }
                else
                {
                    byte switch2 = reader.ReadBits(1);
                    if (switch2 == 0)
                    {
                        // Run length 4-7
                        byte runLength = (byte)(reader.ReadBits(2) + 4);
                        byte color = reader.ReadBits(4);
                        WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                    }
                    else
                    {
                        byte switch3 = reader.ReadBits(2);
                        switch (switch3)
                        {
                            case 0: // Single pixel color 0
                                if (x < maxX)
                                {
                                    buffer[y * width + x] = 0;
                                    x++;
                                }
                                break;
                            case 1: // Run of 2 pixels
                                WritePixels(buffer, width, ref x, y, maxX, 2, 0);
                                break;
                            case 2: // Run length 9-24
                                {
                                    byte runLength = (byte)(reader.ReadBits(4) + 9);
                                    byte color = reader.ReadBits(4);
                                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                                }
                                break;
                            case 3: // Run length 25-280
                                {
                                    byte runLength = (byte)(reader.ReadBits(8) + 25);
                                    byte color = reader.ReadBits(4);
                                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                                }
                                break;
                        }
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Decodes 8-bit RLE pixel data.
    /// </summary>
    private static void Decode8BitPixelData(ref RleBitReader reader, DvbSubRegion region,
        ref int x, ref int y, int startX, int maxX, int maxY, int lineStride)
    {
        var buffer = region.PixelBuffer!;
        int width = region.Width;
        
        while (!reader.EndOfData && y < maxY)
        {
            byte pixel = reader.ReadBits(8);
            
            if (pixel != 0)
            {
                // Single pixel
                if (x < maxX)
                {
                    buffer[y * width + x] = pixel;
                    x++;
                }
            }
            else
            {
                // Escape sequence
                byte switchBit = reader.ReadBits(1);
                if (switchBit == 0)
                {
                    // Run length 0-127 of background color
                    byte runLength = reader.ReadBits(7);
                    if (runLength == 0)
                    {
                        // End of 8-bit string
                        return;
                    }
                    WritePixels(buffer, width, ref x, y, maxX, runLength, 0);
                }
                else
                {
                    // Run length 1-127 with color
                    byte runLength = reader.ReadBits(7);
                    byte color = reader.ReadBits(8);
                    WritePixels(buffer, width, ref x, y, maxX, runLength, color);
                }
            }
        }
    }
    
    /// <summary>
    /// Writes a run of pixels to the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixels(byte[] buffer, int width, ref int x, int y, int maxX, byte count, byte color)
    {
        int endX = Math.Min(x + count, maxX);
        int baseOffset = y * width;
        
        for (int i = x; i < endX; i++)
        {
            buffer[baseOffset + i] = color;
        }
        
        x = endX;
    }
    
    /// <summary>
    /// Skips a map table in the RLE stream.
    /// </summary>
    private static void SkipMapTable(ref RleBitReader reader, byte tableType)
    {
        int entries = tableType switch
        {
            0x20 => 4,   // 2-to-4 bit: 4 entries of 4 bits
            0x21 => 4,   // 2-to-8 bit: 4 entries of 8 bits
            0x22 => 16,  // 4-to-8 bit: 16 entries of 8 bits
            _ => 0
        };
        
        int bitsPerEntry = tableType switch
        {
            0x20 => 4,
            0x21 => 8,
            0x22 => 8,
            _ => 0
        };
        
        for (int i = 0; i < entries; i++)
        {
            reader.ReadBits((byte)bitsPerEntry);
        }
    }
    
    /// <summary>
    /// Converts palette indices to BGRA pixel data.
    /// </summary>
    private static byte[] ConvertToArgb(byte[] indices, int width, int height, DvbSubColor[] palette)
    {
        var result = new byte[width * height * 4];
        int paletteLength = palette.Length;
        
        for (int i = 0; i < indices.Length; i++)
        {
            int idx = indices[i];
            if (idx >= paletteLength)
                idx = 0;
            
            var color = palette[idx];
            int offset = i * 4;
            result[offset] = color.B;
            result[offset + 1] = color.G;
            result[offset + 2] = color.R;
            result[offset + 3] = color.A;
        }
        
        return result;
    }
    
    /// <summary>
    /// Clears all decoder state.
    /// </summary>
    public void Reset()
    {
        cluts.Clear();
        regions.Clear();
        objects.Clear();
        currentPage = null;
        displayDefinition = DvbSubDisplayDefinition.DefaultSD;
        currentPts = 0;
    }
    
    /// <summary>
    /// Gets the current display dimensions.
    /// </summary>
    public (ushort Width, ushort Height) GetDisplaySize() => 
        (displayDefinition.DisplayWidth, displayDefinition.DisplayHeight);
}

/// <summary>
/// Bit reader for RLE data streams.
/// </summary>
internal ref struct RleBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int byteOffset;
    private int bitOffset;
    
    public RleBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        byteOffset = 0;
        bitOffset = 0;
    }
    
    public bool EndOfData => byteOffset >= data.Length;
    
    /// <summary>
    /// Reads up to 8 bits from the stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadBits(int count)
    {
        if (count == 0 || byteOffset >= data.Length)
            return 0;
        
        int result = 0;
        int bitsRemaining = count;
        
        while (bitsRemaining > 0 && byteOffset < data.Length)
        {
            int bitsAvailable = 8 - bitOffset;
            int bitsToRead = Math.Min(bitsRemaining, bitsAvailable);
            
            int mask = (1 << bitsToRead) - 1;
            int shift = bitsAvailable - bitsToRead;
            int bits = (data[byteOffset] >> shift) & mask;
            
            result = (result << bitsToRead) | bits;
            bitsRemaining -= bitsToRead;
            bitOffset += bitsToRead;
            
            if (bitOffset >= 8)
            {
                bitOffset = 0;
                byteOffset++;
            }
        }
        
        return (byte)result;
    }
    
    /// <summary>
    /// Aligns the reader to the next byte boundary.
    /// </summary>
    public void ByteAlign()
    {
        if (bitOffset > 0)
        {
            bitOffset = 0;
            byteOffset++;
        }
    }
}

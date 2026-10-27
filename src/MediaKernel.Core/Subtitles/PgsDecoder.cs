// PGS (Presentation Graphics Stream) Decoder
// Decodes Blu-ray subtitle bitmaps

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoder for PGS (Presentation Graphics Stream) Blu-ray subtitles.
/// </summary>
public sealed class PgsDecoder
{
    private readonly Dictionary<byte, PgsPaletteEntry[]> palettes = [];
    private readonly Dictionary<ushort, PgsObjectDefinition> objects = [];
    private readonly Dictionary<byte, PgsWindowDefinition> windows = [];
    private readonly List<byte[]> fragmentBuffer = [];
    
    private ushort currentFragmentObjectId;
    private int expectedDataLength;
    private int accumulatedDataLength;
    private ushort fragmentWidth;
    private ushort fragmentHeight;
    
    private PgsPresentationComposition? currentComposition;
    private long lastPresentationTime;
    
    /// <summary>
    /// Decodes a complete PGS display set from data.
    /// </summary>
    /// <returns>List of decoded subtitle images for this display set.</returns>
    public List<PgsSubtitleImage> DecodeDisplaySet(ReadOnlySpan<byte> data)
    {
        var images = new List<PgsSubtitleImage>();
        int offset = 0;
        
        while (offset < data.Length)
        {
            if (!PgsSegmentHeader.TryParse(data[offset..], out var header, out int headerBytes))
                break;
            
            offset += headerBytes;
            
            if (offset + header.SegmentSize > data.Length)
                break;
            
            var segmentData = data.Slice(offset, header.SegmentSize);
            offset += header.SegmentSize;
            
            switch (header.SegmentType)
            {
                case PgsSegmentType.PresentationComposition:
                    ProcessPresentationComposition(segmentData, header.PresentationTimeMicroseconds, images);
                    break;
                    
                case PgsSegmentType.WindowDefinition:
                    ProcessWindowDefinition(segmentData);
                    break;
                    
                case PgsSegmentType.PaletteDefinition:
                    ProcessPaletteDefinition(segmentData);
                    break;
                    
                case PgsSegmentType.ObjectDefinition:
                    ProcessObjectDefinition(segmentData);
                    break;
                    
                case PgsSegmentType.EndOfDisplaySet:
                    // End of display set - render current composition
                    if (currentComposition.HasValue)
                    {
                        RenderComposition(currentComposition.Value, images);
                    }
                    break;
            }
        }
        
        return images;
    }
    
    /// <summary>
    /// Processes a single PGS segment.
    /// </summary>
    /// <returns>Decoded image if segment completes a display, otherwise null.</returns>
    public PgsSubtitleImage? ProcessSegment(ReadOnlySpan<byte> data)
    {
        if (!PgsSegmentHeader.TryParse(data, out var header, out int headerBytes))
            return null;
        
        var segmentData = data.Slice(headerBytes, Math.Min(header.SegmentSize, data.Length - headerBytes));
        
        switch (header.SegmentType)
        {
            case PgsSegmentType.PresentationComposition:
                ProcessPresentationComposition(segmentData, header.PresentationTimeMicroseconds, null);
                break;
                
            case PgsSegmentType.WindowDefinition:
                ProcessWindowDefinition(segmentData);
                break;
                
            case PgsSegmentType.PaletteDefinition:
                ProcessPaletteDefinition(segmentData);
                break;
                
            case PgsSegmentType.ObjectDefinition:
                ProcessObjectDefinition(segmentData);
                break;
                
            case PgsSegmentType.EndOfDisplaySet:
                if (currentComposition.HasValue)
                {
                    var images = new List<PgsSubtitleImage>();
                    RenderComposition(currentComposition.Value, images);
                    return images.FirstOrDefault();
                }
                break;
        }
        
        return null;
    }
    
    private void ProcessPresentationComposition(ReadOnlySpan<byte> data, long presentationTime, List<PgsSubtitleImage>? images)
    {
        if (!PgsPresentationComposition.TryParse(data, out var pcs))
            return;
        
        // If this is an epoch start, clear cached data
        if (pcs.CompositionState == PgsCompositionState.EpochStart)
        {
            objects.Clear();
            fragmentBuffer.Clear();
        }
        
        // Set end time on previous composition's images
        if (images != null && currentComposition.HasValue)
        {
            foreach (var img in images)
            {
                if (img.EndTimeMicroseconds == 0)
                    img.EndTimeMicroseconds = presentationTime;
            }
        }
        
        currentComposition = pcs;
        lastPresentationTime = presentationTime;
    }
    
    private void ProcessWindowDefinition(ReadOnlySpan<byte> data)
    {
        if (!PgsWindowDefinition.TryParse(data, out var windowDefs))
            return;
        
        foreach (var window in windowDefs)
        {
            windows[window.WindowId] = window;
        }
    }
    
    private void ProcessPaletteDefinition(ReadOnlySpan<byte> data)
    {
        if (!PgsPaletteDefinition.TryParse(data, out var palette))
            return;
        
        palettes[palette.PaletteId] = palette.Entries;
    }
    
    private void ProcessObjectDefinition(ReadOnlySpan<byte> data)
    {
        if (!PgsObjectDefinition.TryParse(data, out var obj))
            return;
        
        if (obj.IsFirstInSequence)
        {
            // Start new fragment assembly
            fragmentBuffer.Clear();
            currentFragmentObjectId = obj.ObjectId;
            expectedDataLength = obj.DataLength;
            accumulatedDataLength = 0;
            fragmentWidth = obj.Width;
            fragmentHeight = obj.Height;
        }
        
        if (obj.RleData.Length > 0)
        {
            fragmentBuffer.Add(obj.RleData);
            accumulatedDataLength += obj.RleData.Length;
        }
        
        if (obj.IsLastInSequence)
        {
            // Assemble complete object
            var completeRle = new byte[accumulatedDataLength];
            int offset = 0;
            foreach (var fragment in fragmentBuffer)
            {
                fragment.CopyTo(completeRle, offset);
                offset += fragment.Length;
            }
            
            var completeObj = new PgsObjectDefinition
            {
                ObjectId = currentFragmentObjectId,
                Width = fragmentWidth,
                Height = fragmentHeight,
                RleData = completeRle,
                DataLength = accumulatedDataLength,
                IsFirstInSequence = true,
                IsLastInSequence = true
            };
            
            objects[completeObj.ObjectId] = completeObj;
            fragmentBuffer.Clear();
        }
    }
    
    private void RenderComposition(PgsPresentationComposition pcs, List<PgsSubtitleImage> images)
    {
        if (pcs.CompositionObjects.Length == 0)
        {
            // Empty composition - clear display
            currentComposition = null;
            return;
        }
        
        // Get palette
        if (!palettes.TryGetValue(pcs.PaletteId, out var palette))
            palette = new PgsPaletteEntry[256];
        
        foreach (var compObj in pcs.CompositionObjects)
        {
            if (!objects.TryGetValue(compObj.ObjectId, out var obj))
                continue;
            
            // Decode RLE to ARGB pixels
            var pixels = DecodeRle(obj.RleData, obj.Width, obj.Height, palette);
            
            var image = new PgsSubtitleImage
            {
                StartTimeMicroseconds = lastPresentationTime,
                EndTimeMicroseconds = 0, // Set when next composition arrives
                X = compObj.HorizontalPosition,
                Y = compObj.VerticalPosition,
                Width = obj.Width,
                Height = obj.Height,
                Pixels = pixels
            };
            
            images.Add(image);
        }
    }
    
    /// <summary>
    /// Decodes RLE-compressed bitmap data to ARGB pixels.
    /// </summary>
    /// <remarks>
    /// RLE encoding per US Patent 7912305 B1:
    /// - CCCCCCCC: One pixel in color C
    /// - 00000000 00LLLLLL: L pixels in color 0 (L = 1-63)
    /// - 00000000 01LLLLLL LLLLLLLL: L pixels in color 0 (L = 64-16383)
    /// - 00000000 10LLLLLL CCCCCCCC: L pixels in color C (L = 3-63)
    /// - 00000000 11LLLLLL LLLLLLLL CCCCCCCC: L pixels in color C (L = 64-16383)
    /// - 00000000 00000000: End of line
    /// </remarks>
    public static uint[] DecodeRle(ReadOnlySpan<byte> rleData, int width, int height, PgsPaletteEntry[] palette)
    {
        var pixels = new uint[width * height];
        int pixelIndex = 0;
        int lineStart = 0;
        int rleOffset = 0;
        
        while (rleOffset < rleData.Length && pixelIndex < pixels.Length)
        {
            byte first = rleData[rleOffset++];
            
            if (first != 0)
            {
                // Single pixel in color 'first'
                pixels[pixelIndex++] = palette[first].ToArgb();
            }
            else
            {
                // Extended code
                if (rleOffset >= rleData.Length)
                    break;
                
                byte second = rleData[rleOffset++];
                
                if (second == 0)
                {
                    // End of line - pad to line width
                    int currentLine = lineStart / width;
                    int nextLineStart = (currentLine + 1) * width;
                    while (pixelIndex < nextLineStart && pixelIndex < pixels.Length)
                    {
                        pixels[pixelIndex++] = 0; // Transparent
                    }
                    lineStart = pixelIndex;
                }
                else if ((second & 0xC0) == 0)
                {
                    // 00LLLLLL: L pixels in color 0
                    int runLength = second & 0x3F;
                    uint color = palette[0].ToArgb();
                    for (int i = 0; i < runLength && pixelIndex < pixels.Length; i++)
                    {
                        pixels[pixelIndex++] = color;
                    }
                }
                else if ((second & 0xC0) == 0x40)
                {
                    // 01LLLLLL LLLLLLLL: L pixels in color 0
                    if (rleOffset >= rleData.Length)
                        break;
                    
                    int runLength = ((second & 0x3F) << 8) | rleData[rleOffset++];
                    uint color = palette[0].ToArgb();
                    for (int i = 0; i < runLength && pixelIndex < pixels.Length; i++)
                    {
                        pixels[pixelIndex++] = color;
                    }
                }
                else if ((second & 0xC0) == 0x80)
                {
                    // 10LLLLLL CCCCCCCC: L pixels in color C
                    if (rleOffset >= rleData.Length)
                        break;
                    
                    int runLength = second & 0x3F;
                    byte colorIndex = rleData[rleOffset++];
                    uint color = palette[colorIndex].ToArgb();
                    for (int i = 0; i < runLength && pixelIndex < pixels.Length; i++)
                    {
                        pixels[pixelIndex++] = color;
                    }
                }
                else // (second & 0xC0) == 0xC0
                {
                    // 11LLLLLL LLLLLLLL CCCCCCCC: L pixels in color C
                    if (rleOffset + 1 >= rleData.Length)
                        break;
                    
                    int runLength = ((second & 0x3F) << 8) | rleData[rleOffset++];
                    byte colorIndex = rleData[rleOffset++];
                    uint color = palette[colorIndex].ToArgb();
                    for (int i = 0; i < runLength && pixelIndex < pixels.Length; i++)
                    {
                        pixels[pixelIndex++] = color;
                    }
                }
            }
        }
        
        return pixels;
    }
    
    /// <summary>
    /// Resets decoder state.
    /// </summary>
    public void Reset()
    {
        palettes.Clear();
        objects.Clear();
        windows.Clear();
        fragmentBuffer.Clear();
        currentComposition = null;
    }
}

// HEIF/AVIF Image Demuxer - ISO/IEC 23008-12 still image container

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Demuxer for HEIF (High Efficiency Image File Format) and AVIF image containers.
/// Supports HEVC, AVC (H.264), and AV1 compressed still images.
/// </summary>
public sealed class HeifDemuxer
{
    private readonly Stream stream;
    private readonly List<HeifItem> items = new();
    private readonly List<HeifItemProperty> properties = new();
    private readonly List<HeifPropertyAssociation> associations = new();
    private int primaryItemId;
    private HeifBrand brand;

    public HeifDemuxer(Stream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// The primary item ID (main image).
    /// </summary>
    public int PrimaryItemId => primaryItemId;

    /// <summary>
    /// All items in the file (images, thumbnails, metadata).
    /// </summary>
    public IReadOnlyList<HeifItem> Items => items;

    /// <summary>
    /// The detected brand (HEIF, AVIF, or MIAF).
    /// </summary>
    public HeifBrand Brand => brand;

    /// <summary>
    /// Probe the stream to check if it's a HEIF/AVIF file.
    /// </summary>
    public static bool Probe(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return false;

        long startPosition = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) < 12)
                return false;

            // Check for ftyp box
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4));
            if (boxType != FourCC("ftyp"))
                return false;

            // Check brand
            uint majorBrand = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8));
            return IsHeifBrand(majorBrand);
        }
        finally
        {
            stream.Position = startPosition;
        }
    }

    /// <summary>
    /// Parse the HEIF file structure.
    /// </summary>
    public bool Parse()
    {
        stream.Position = 0;
        items.Clear();
        properties.Clear();
        associations.Clear();
        primaryItemId = 0;

        try
        {
            while (stream.Position < stream.Length)
            {
                if (!TryReadBoxHeader(out uint boxType, out long boxSize, out long dataStart))
                    break;

                long boxEnd = dataStart + boxSize - (dataStart - stream.Position + (stream.Position - dataStart));
                boxEnd = stream.Position - 8 + boxSize; // Adjust for header

                if (boxType == FourCC("ftyp"))
                {
                    ParseFtyp(boxEnd);
                }
                else if (boxType == FourCC("meta"))
                {
                    ParseMeta(boxEnd);
                }
                else
                {
                    // Skip other boxes
                    stream.Position = boxEnd;
                }
            }

            return items.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the primary image item.
    /// </summary>
    public HeifItem? GetPrimaryItem()
    {
        return items.FirstOrDefault(i => i.ItemId == primaryItemId);
    }

    /// <summary>
    /// Get all displayable image items (not hidden, not derived reference).
    /// </summary>
    public IEnumerable<HeifItem> GetDisplayableItems()
    {
        return items.Where(i => !i.IsHidden && IsDisplayableType(i.ItemType));
    }

    /// <summary>
    /// Read the image data for an item.
    /// </summary>
    public byte[]? ReadItemData(HeifItem item)
    {
        if (item.Extents.Count == 0)
            return null;

        using var ms = new MemoryStream();

        foreach (var extent in item.Extents)
        {
            long offset = item.BaseOffset + extent.Offset;
            stream.Position = offset;

            byte[] buffer = new byte[extent.Length];
            int bytesRead = stream.Read(buffer, 0, (int)extent.Length);
            if (bytesRead != extent.Length)
                return null;

            ms.Write(buffer, 0, bytesRead);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Get codec configuration data for an item (hvcC, avcC, av1C).
    /// </summary>
    public byte[]? GetCodecConfig(HeifItem item)
    {
        foreach (var assoc in associations.Where(a => a.ItemId == item.ItemId))
        {
            if (assoc.PropertyIndex > 0 && assoc.PropertyIndex <= properties.Count)
            {
                var prop = properties[assoc.PropertyIndex - 1];
                if (prop.Type == FourCC("hvcC") || prop.Type == FourCC("avcC") ||
                    prop.Type == FourCC("av1C"))
                {
                    return prop.Data;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Get image dimensions from ispe property.
    /// </summary>
    public (int Width, int Height) GetDimensions(HeifItem item)
    {
        foreach (var assoc in associations.Where(a => a.ItemId == item.ItemId))
        {
            if (assoc.PropertyIndex > 0 && assoc.PropertyIndex <= properties.Count)
            {
                var prop = properties[assoc.PropertyIndex - 1];
                if (prop.Type == FourCC("ispe") && prop.Data.Length >= 8)
                {
                    int width = (int)BinaryPrimitives.ReadUInt32BigEndian(prop.Data);
                    int height = (int)BinaryPrimitives.ReadUInt32BigEndian(prop.Data.AsSpan(4));
                    return (width, height);
                }
            }
        }
        return (0, 0);
    }

    /// <summary>
    /// Get rotation in degrees (clockwise) from irot property.
    /// </summary>
    public int GetRotation(HeifItem item)
    {
        foreach (var assoc in associations.Where(a => a.ItemId == item.ItemId))
        {
            if (assoc.PropertyIndex > 0 && assoc.PropertyIndex <= properties.Count)
            {
                var prop = properties[assoc.PropertyIndex - 1];
                if (prop.Type == FourCC("irot") && prop.Data.Length >= 1)
                {
                    // Value is counter-clockwise, convert to clockwise
                    int ccw = prop.Data[0] & 0x03;
                    return ccw switch
                    {
                        1 => 270, // 90 CCW = 270 CW
                        2 => 180,
                        3 => 90,  // 270 CCW = 90 CW
                        _ => 0
                    };
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Get color information from colr property.
    /// </summary>
    public HeifColorInfo? GetColorInfo(HeifItem item)
    {
        foreach (var assoc in associations.Where(a => a.ItemId == item.ItemId))
        {
            if (assoc.PropertyIndex > 0 && assoc.PropertyIndex <= properties.Count)
            {
                var prop = properties[assoc.PropertyIndex - 1];
                if (prop.Type == FourCC("colr") && prop.Data.Length >= 10)
                {
                    uint colorType = BinaryPrimitives.ReadUInt32BigEndian(prop.Data);
                    if (colorType == FourCC("nclx"))
                    {
                        return new HeifColorInfo
                        {
                            ColorPrimaries = BinaryPrimitives.ReadUInt16BigEndian(prop.Data.AsSpan(4)),
                            TransferCharacteristics = BinaryPrimitives.ReadUInt16BigEndian(prop.Data.AsSpan(6)),
                            MatrixCoefficients = BinaryPrimitives.ReadUInt16BigEndian(prop.Data.AsSpan(8)),
                            FullRange = prop.Data.Length > 10 && (prop.Data[10] & 0x80) != 0
                        };
                    }
                }
            }
        }
        return null;
    }

    private void ParseFtyp(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (stream.Read(buffer) < 4)
            return;

        uint majorBrand = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        brand = majorBrand switch
        {
            0x68656963 => HeifBrand.Heic, // heic
            0x68656978 => HeifBrand.Heix, // heix
            0x6D696631 => HeifBrand.Miaf, // mif1
            0x61766966 => HeifBrand.Avif, // avif
            0x61766973 => HeifBrand.Avis, // avis (AVIF sequence)
            _ => HeifBrand.Unknown
        };

        stream.Position = boxEnd;
    }

    private void ParseMeta(long boxEnd)
    {
        // meta is a full box - skip version and flags
        Span<byte> versionFlags = stackalloc byte[4];
        if (stream.Read(versionFlags) < 4)
            return;

        while (stream.Position < boxEnd)
        {
            if (!TryReadBoxHeader(out uint boxType, out long boxSize, out long dataStart))
                break;

            long childEnd = stream.Position - 8 + boxSize;

            switch (boxType)
            {
                case 0x7069746D: // pitm - primary item
                    ParsePitm(childEnd);
                    break;
                case 0x696C6F63: // iloc - item location
                    ParseIloc(childEnd);
                    break;
                case 0x69696E66: // iinf - item info
                    ParseIinf(childEnd);
                    break;
                case 0x69707270: // iprp - item properties
                    ParseIprp(childEnd);
                    break;
                default:
                    stream.Position = childEnd;
                    break;
            }
        }
    }

    private void ParsePitm(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[6];
        if (stream.Read(buffer) < 4)
            return;

        int version = buffer[0];
        if (version == 0)
        {
            primaryItemId = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(4));
        }
        else
        {
            if (stream.Read(buffer.Slice(4, 2)) < 2)
                return;
            primaryItemId = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4));
        }

        stream.Position = boxEnd;
    }

    private void ParseIloc(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (stream.Read(buffer) < 4)
            return;

        int version = buffer[0];

        if (stream.Read(buffer.Slice(0, 2)) < 2)
            return;

        int offsetSize = (buffer[0] >> 4) & 0x0F;
        int lengthSize = buffer[0] & 0x0F;
        int baseOffsetSize = (buffer[1] >> 4) & 0x0F;
        int indexSize = version >= 1 ? (buffer[1] & 0x0F) : 0;

        Span<byte> countBuf = stackalloc byte[4];
        if (stream.Read(countBuf.Slice(0, version < 2 ? 2 : 4)) < (version < 2 ? 2 : 4))
            return;

        int itemCount = version < 2
            ? BinaryPrimitives.ReadUInt16BigEndian(countBuf)
            : (int)BinaryPrimitives.ReadUInt32BigEndian(countBuf);

        for (int i = 0; i < itemCount && stream.Position < boxEnd; i++)
        {
            // Item ID
            int itemId;
            if (version < 2)
            {
                if (stream.Read(countBuf.Slice(0, 2)) < 2) break;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(countBuf);
            }
            else
            {
                if (stream.Read(countBuf) < 4) break;
                itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(countBuf);
            }

            // Construction method (version >= 1)
            int constructionMethod = 0;
            if (version >= 1)
            {
                if (stream.Read(countBuf.Slice(0, 2)) < 2) break;
                constructionMethod = BinaryPrimitives.ReadUInt16BigEndian(countBuf) & 0x0F;
            }

            // Data reference index
            if (stream.Read(countBuf.Slice(0, 2)) < 2) break;

            // Base offset
            long baseOffset = ReadVariableInt(baseOffsetSize);

            // Extent count
            if (stream.Read(countBuf.Slice(0, 2)) < 2) break;
            int extentCount = BinaryPrimitives.ReadUInt16BigEndian(countBuf);

            var item = items.FirstOrDefault(x => x.ItemId == itemId);
            if (item == null)
            {
                item = new HeifItem { ItemId = itemId };
                items.Add(item);
            }
            item.BaseOffset = baseOffset;
            item.ConstructionMethod = constructionMethod;

            for (int j = 0; j < extentCount && stream.Position < boxEnd; j++)
            {
                // Index (if present)
                if (indexSize > 0)
                    ReadVariableInt(indexSize);

                long extentOffset = ReadVariableInt(offsetSize);
                long extentLength = ReadVariableInt(lengthSize);

                item.Extents.Add(new HeifExtent { Offset = extentOffset, Length = extentLength });
            }
        }

        stream.Position = boxEnd;
    }

    private void ParseIinf(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (stream.Read(buffer) < 4)
            return;

        int version = buffer[0];

        if (stream.Read(buffer.Slice(0, version == 0 ? 2 : 4)) < (version == 0 ? 2 : 4))
            return;

        int entryCount = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(buffer)
            : (int)BinaryPrimitives.ReadUInt32BigEndian(buffer);

        for (int i = 0; i < entryCount && stream.Position < boxEnd; i++)
        {
            if (!TryReadBoxHeader(out uint boxType, out long boxSize, out _))
                break;

            long infeEnd = stream.Position - 8 + boxSize;

            if (boxType == FourCC("infe"))
            {
                ParseInfe(infeEnd);
            }
            else
            {
                stream.Position = infeEnd;
            }
        }
    }

    private void ParseInfe(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[12];
        if (stream.Read(buffer.Slice(0, 4)) < 4)
            return;

        int version = buffer[0];
        int flags = (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];

        int itemId;
        ushort itemProtectionIndex;
        uint itemType = 0;
        string itemName = "";
        string contentType = "";

        if (version < 2)
        {
            if (stream.Read(buffer.Slice(0, 4)) < 4) return;
            itemId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            itemProtectionIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2));
            itemName = ReadNullTerminatedString(boxEnd);
            contentType = ReadNullTerminatedString(boxEnd);
        }
        else
        {
            if (version == 2)
            {
                if (stream.Read(buffer.Slice(0, 6)) < 6) return;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
                itemProtectionIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2));
                itemType = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(2));
            }
            else // version == 3
            {
                if (stream.Read(buffer.Slice(0, 12)) < 12) return;
                itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer);
                itemProtectionIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(4));
                itemType = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(6));
            }
            
            // Re-read properly for version 2/3
            stream.Position -= (version == 2 ? 2 : 4);
            if (stream.Read(buffer.Slice(0, 4)) < 4) return;
            itemType = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            
            itemName = ReadNullTerminatedString(boxEnd);

            if (itemType == FourCC("mime"))
            {
                contentType = ReadNullTerminatedString(boxEnd);
            }
        }

        var item = items.FirstOrDefault(x => x.ItemId == itemId);
        if (item == null)
        {
            item = new HeifItem { ItemId = itemId };
            items.Add(item);
        }

        item.ItemType = itemType;
        item.ItemName = itemName;
        item.ContentType = contentType;
        item.IsHidden = (flags & 0x01) != 0;

        stream.Position = boxEnd;
    }

    private void ParseIprp(long boxEnd)
    {
        while (stream.Position < boxEnd)
        {
            if (!TryReadBoxHeader(out uint boxType, out long boxSize, out _))
                break;

            long childEnd = stream.Position - 8 + boxSize;

            if (boxType == FourCC("ipco"))
            {
                ParseIpco(childEnd);
            }
            else if (boxType == FourCC("ipma"))
            {
                ParseIpma(childEnd);
            }
            else
            {
                stream.Position = childEnd;
            }
        }
    }

    private void ParseIpco(long boxEnd)
    {
        while (stream.Position < boxEnd)
        {
            long propStart = stream.Position;
            if (!TryReadBoxHeader(out uint boxType, out long boxSize, out _))
                break;

            long propEnd = propStart + boxSize;
            int dataSize = (int)(propEnd - stream.Position);

            byte[] propData = new byte[dataSize];
            stream.Read(propData, 0, dataSize);

            properties.Add(new HeifItemProperty
            {
                Type = boxType,
                Data = propData
            });

            stream.Position = propEnd;
        }
    }

    private void ParseIpma(long boxEnd)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (stream.Read(buffer.Slice(0, 4)) < 4)
            return;

        int version = buffer[0];
        int flags = (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];

        if (stream.Read(buffer.Slice(0, 4)) < 4)
            return;

        int entryCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer);

        for (int i = 0; i < entryCount && stream.Position < boxEnd; i++)
        {
            int itemId;
            if (version < 1)
            {
                if (stream.Read(buffer.Slice(0, 2)) < 2) break;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            }
            else
            {
                if (stream.Read(buffer.Slice(0, 4)) < 4) break;
                itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer);
            }

            if (stream.Read(buffer.Slice(0, 1)) < 1) break;
            int assocCount = buffer[0];

            for (int j = 0; j < assocCount && stream.Position < boxEnd; j++)
            {
                int propIndex;
                bool essential;

                if ((flags & 0x01) != 0)
                {
                    if (stream.Read(buffer.Slice(0, 2)) < 2) break;
                    essential = (buffer[0] & 0x80) != 0;
                    propIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer) & 0x7FFF;
                }
                else
                {
                    if (stream.Read(buffer.Slice(0, 1)) < 1) break;
                    essential = (buffer[0] & 0x80) != 0;
                    propIndex = buffer[0] & 0x7F;
                }

                associations.Add(new HeifPropertyAssociation
                {
                    ItemId = itemId,
                    PropertyIndex = propIndex,
                    Essential = essential
                });
            }
        }

        stream.Position = boxEnd;
    }

    private bool TryReadBoxHeader(out uint boxType, out long boxSize, out long dataStart)
    {
        boxType = 0;
        boxSize = 0;
        dataStart = 0;

        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < 8)
            return false;

        boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        boxType = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4));
        dataStart = stream.Position;

        if (boxSize == 1)
        {
            // Extended size
            Span<byte> extSize = stackalloc byte[8];
            if (stream.Read(extSize) < 8)
                return false;
            boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(extSize);
            dataStart = stream.Position;
        }
        else if (boxSize == 0)
        {
            // Box extends to end of file
            boxSize = stream.Length - stream.Position + 8;
        }

        return boxSize >= 8;
    }

    private long ReadVariableInt(int size)
    {
        if (size == 0) return 0;

        Span<byte> buffer = stackalloc byte[8];
        if (stream.Read(buffer.Slice(0, size)) < size)
            return 0;

        return size switch
        {
            1 => buffer[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(buffer),
            4 => BinaryPrimitives.ReadUInt32BigEndian(buffer),
            8 => (long)BinaryPrimitives.ReadUInt64BigEndian(buffer),
            _ => 0
        };
    }

    private string ReadNullTerminatedString(long maxPosition)
    {
        var sb = new StringBuilder();
        while (stream.Position < maxPosition)
        {
            int b = stream.ReadByte();
            if (b <= 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static uint FourCC(string code)
    {
        if (code.Length != 4)
            throw new ArgumentException("FourCC must be 4 characters");
        return (uint)((code[0] << 24) | (code[1] << 16) | (code[2] << 8) | code[3]);
    }

    private static bool IsHeifBrand(uint brand)
    {
        // Common HEIF/AVIF brands
        return brand == FourCC("heic") || // HEVC main profile
               brand == FourCC("heix") || // HEVC extended
               brand == FourCC("hevc") || // HEVC brand
               brand == FourCC("hevx") || // HEVC extended
               brand == FourCC("heim") || // HEVC image mono
               brand == FourCC("heis") || // HEVC image sequence
               brand == FourCC("avci") || // AVC image
               brand == FourCC("avcs") || // AVC sequence
               brand == FourCC("mif1") || // MIAF
               brand == FourCC("miaf") || // MIAF
               brand == FourCC("avif") || // AVIF
               brand == FourCC("avis") || // AVIF sequence
               brand == FourCC("msf1");   // MIAF sequence
    }

    private static bool IsDisplayableType(uint itemType)
    {
        return itemType == FourCC("hvc1") ||
               itemType == FourCC("avc1") ||
               itemType == FourCC("av01") ||
               itemType == FourCC("jpeg") ||
               itemType == FourCC("grid") ||
               itemType == FourCC("iden");
    }
}

/// <summary>
/// HEIF item (image, thumbnail, or metadata).
/// </summary>
public sealed class HeifItem
{
    public int ItemId { get; set; }
    public uint ItemType { get; set; }
    public string ItemName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public bool IsHidden { get; set; }
    public long BaseOffset { get; set; }
    public int ConstructionMethod { get; set; }
    public List<HeifExtent> Extents { get; } = new();

    /// <summary>
    /// Get the item type as a FourCC string.
    /// </summary>
    public string ItemTypeString => new string(new[]
    {
        (char)((ItemType >> 24) & 0xFF),
        (char)((ItemType >> 16) & 0xFF),
        (char)((ItemType >> 8) & 0xFF),
        (char)(ItemType & 0xFF)
    });

    /// <summary>
    /// Check if this is an HEVC image.
    /// </summary>
    public bool IsHevc => ItemType == 0x68766331; // hvc1

    /// <summary>
    /// Check if this is an AVC/H.264 image.
    /// </summary>
    public bool IsAvc => ItemType == 0x61766331; // avc1

    /// <summary>
    /// Check if this is an AV1 image.
    /// </summary>
    public bool IsAv1 => ItemType == 0x61763031; // av01

    /// <summary>
    /// Check if this is a JPEG image.
    /// </summary>
    public bool IsJpeg => ItemType == 0x6A706567; // jpeg

    /// <summary>
    /// Check if this is a grid (composite) image.
    /// </summary>
    public bool IsGrid => ItemType == 0x67726964; // grid
}

/// <summary>
/// Data extent for an item.
/// </summary>
public struct HeifExtent
{
    public long Offset { get; set; }
    public long Length { get; set; }
}

/// <summary>
/// Item property.
/// </summary>
public sealed class HeifItemProperty
{
    public uint Type { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public string TypeString => new string(new[]
    {
        (char)((Type >> 24) & 0xFF),
        (char)((Type >> 16) & 0xFF),
        (char)((Type >> 8) & 0xFF),
        (char)(Type & 0xFF)
    });
}

/// <summary>
/// Property association linking items to properties.
/// </summary>
public struct HeifPropertyAssociation
{
    public int ItemId { get; set; }
    public int PropertyIndex { get; set; }
    public bool Essential { get; set; }
}

/// <summary>
/// Color information from colr property.
/// </summary>
public sealed class HeifColorInfo
{
    public ushort ColorPrimaries { get; set; }
    public ushort TransferCharacteristics { get; set; }
    public ushort MatrixCoefficients { get; set; }
    public bool FullRange { get; set; }

    /// <summary>
    /// Check if this is BT.709 (sRGB compatible).
    /// </summary>
    public bool IsBt709 => ColorPrimaries == 1 && TransferCharacteristics == 1;

    /// <summary>
    /// Check if this is BT.2020 (HDR/wide gamut).
    /// </summary>
    public bool IsBt2020 => ColorPrimaries == 9;

    /// <summary>
    /// Check if this uses PQ (HDR10) transfer.
    /// </summary>
    public bool IsPq => TransferCharacteristics == 16;

    /// <summary>
    /// Check if this uses HLG transfer.
    /// </summary>
    public bool IsHlg => TransferCharacteristics == 18;
}

/// <summary>
/// HEIF file brand.
/// </summary>
public enum HeifBrand
{
    Unknown,
    Heic,  // HEVC image
    Heix,  // HEVC extended
    Miaf,  // MIAF (Media Image Application Format)
    Avif,  // AV1 image
    Avis   // AV1 image sequence
}

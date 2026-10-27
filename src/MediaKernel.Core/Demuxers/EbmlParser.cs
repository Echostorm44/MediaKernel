using System.Buffers.Binary;
using System.Numerics;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// EBML (Extensible Binary Meta Language) primitives for parsing Matroska/WebM files.
/// EBML is the binary format underlying MKV containers.
/// </summary>
/// <remarks>
/// EBML uses variable-length integers (VINTs) where the leading bits indicate the byte length.
/// This implementation uses BitOperations.LeadingZeroCount for hardware-accelerated parsing.
/// </remarks>
public static class EbmlParser
{
    /// <summary>
    /// Maximum supported VINT length (8 bytes = 56 bits of value).
    /// </summary>
    public const int MaxVintLength = 8;

    /// <summary>
    /// Reads an EBML Variable-Size Integer (VINT) from the buffer.
    /// VINTs encode both element IDs and data sizes in EBML.
    /// </summary>
    /// <param name="data">The buffer to read from.</param>
    /// <param name="value">The decoded value (with length marker bits cleared).</param>
    /// <param name="bytesConsumed">Number of bytes consumed from the buffer.</param>
    /// <returns>True if parsing succeeded, false if buffer too small or invalid.</returns>
    public static bool TryReadVint(ReadOnlySpan<byte> data, out ulong value, out int bytesConsumed)
    {
        value = 0;
        bytesConsumed = 0;

        if (data.IsEmpty)
            return false;

        byte firstByte = data[0];
        if (firstByte == 0)
            return false; // Invalid: leading byte cannot be zero

        // Count leading zeros to determine VINT length
        // LeadingZeroCount on a byte (cast to uint, shifted left 24 bits) gives 0-7
        int leadingZeros = BitOperations.LeadingZeroCount((uint)firstByte) - 24;
        int vintLength = leadingZeros + 1;

        if (vintLength > MaxVintLength || data.Length < vintLength)
            return false;

        // Clear the length marker bit from the first byte
        byte firstByteValue = (byte)(firstByte & (0xFF >> vintLength));
        value = firstByteValue;

        // Read remaining bytes
        for (int i = 1; i < vintLength; i++)
        {
            value = (value << 8) | data[i];
        }

        bytesConsumed = vintLength;
        return true;
    }

    /// <summary>
    /// Reads an EBML Element ID. Element IDs use the same VINT encoding but preserve
    /// the length marker bits as part of the ID value.
    /// </summary>
    public static bool TryReadElementId(ReadOnlySpan<byte> data, out uint elementId, out int bytesConsumed)
    {
        elementId = 0;
        bytesConsumed = 0;

        if (data.IsEmpty)
            return false;

        byte firstByte = data[0];
        if (firstByte == 0)
            return false;

        int leadingZeros = BitOperations.LeadingZeroCount((uint)firstByte) - 24;
        int idLength = leadingZeros + 1;

        if (idLength > 4 || data.Length < idLength) // Element IDs max 4 bytes
            return false;

        // For element IDs, we keep the marker bit as part of the ID
        elementId = firstByte;
        for (int i = 1; i < idLength; i++)
        {
            elementId = (elementId << 8) | data[i];
        }

        bytesConsumed = idLength;
        return true;
    }

    /// <summary>
    /// Reads a signed integer from EBML data (big-endian, variable length 1-8 bytes).
    /// </summary>
    public static long ReadSignedInteger(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > 8)
            throw new ArgumentException("Invalid integer length");

        // Sign-extend the first byte
        long value = (sbyte)data[0];
        for (int i = 1; i < data.Length; i++)
        {
            value = (value << 8) | data[i];
        }
        return value;
    }

    /// <summary>
    /// Reads an unsigned integer from EBML data (big-endian, variable length 1-8 bytes).
    /// </summary>
    public static ulong ReadUnsignedInteger(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > 8)
            throw new ArgumentException("Invalid integer length");

        ulong value = 0;
        for (int i = 0; i < data.Length; i++)
        {
            value = (value << 8) | data[i];
        }
        return value;
    }

    /// <summary>
    /// Reads a float from EBML data (4 or 8 bytes, big-endian IEEE 754).
    /// </summary>
    public static double ReadFloat(ReadOnlySpan<byte> data)
    {
        return data.Length switch
        {
            0 => 0.0,
            4 => BinaryPrimitives.ReadSingleBigEndian(data),
            8 => BinaryPrimitives.ReadDoubleBigEndian(data),
            _ => throw new ArgumentException($"Invalid float length: {data.Length}")
        };
    }

    /// <summary>
    /// Reads a UTF-8 string from EBML data.
    /// </summary>
    public static string ReadString(ReadOnlySpan<byte> data)
    {
        // EBML strings may be null-terminated; trim trailing zeros
        int length = data.Length;
        while (length > 0 && data[length - 1] == 0)
            length--;

        return System.Text.Encoding.UTF8.GetString(data[..length]);
    }

    /// <summary>
    /// Reads a date from EBML data (signed 64-bit nanoseconds since 2001-01-01).
    /// </summary>
    public static DateTime ReadDate(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
            throw new ArgumentException("Date must be 8 bytes");

        long nanoseconds = BinaryPrimitives.ReadInt64BigEndian(data);
        
        // EBML epoch is 2001-01-01 00:00:00 UTC
        var ebmlEpoch = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return ebmlEpoch.AddTicks(nanoseconds / 100); // 100 nanoseconds per tick
    }
}

/// <summary>
/// Represents a seek index entry (cue point) for random access.
/// </summary>
public readonly struct SeekEntry
{
    /// <summary>Timestamp in ticks (100ns units).</summary>
    public readonly long TimestampTicks;
    
    /// <summary>Absolute file position of the cluster.</summary>
    public readonly long FilePosition;

    public SeekEntry(long timestampTicks, long filePosition)
    {
        TimestampTicks = timestampTicks;
        FilePosition = filePosition;
    }
}

/// <summary>
/// Represents a parsed EBML element header (ID + size).
/// </summary>
public readonly struct EbmlElementHeader
{
    /// <summary>The element ID (includes the VINT marker bits).</summary>
    public readonly uint ElementId;
    
    /// <summary>The data size in bytes. May be ulong.MaxValue for unknown-size elements.</summary>
    public readonly ulong DataSize;
    
    /// <summary>Total bytes consumed by this header (ID + size VINTs).</summary>
    public readonly int HeaderSize;

    public EbmlElementHeader(uint elementId, ulong dataSize, int headerSize)
    {
        ElementId = elementId;
        DataSize = dataSize;
        HeaderSize = headerSize;
    }

    /// <summary>
    /// Tries to parse an element header from the buffer.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out EbmlElementHeader header)
    {
        header = default;

        if (!EbmlParser.TryReadElementId(data, out uint elementId, out int idLength))
            return false;

        if (!EbmlParser.TryReadVint(data[idLength..], out ulong dataSize, out int sizeLength))
            return false;

        header = new EbmlElementHeader(elementId, dataSize, idLength + sizeLength);
        return true;
    }

    /// <summary>
    /// Returns true if this is an unknown-size element (all data bits set to 1).
    /// </summary>
    public bool IsUnknownSize => DataSize == GetUnknownSizeMarker(HeaderSize);

    private static ulong GetUnknownSizeMarker(int headerSize)
    {
        // For each VINT length, all value bits set to 1 means "unknown size"
        // e.g., for 1-byte VINT: 0xFF with marker = 0x7F
        return headerSize switch
        {
            2 => 0x7F,
            3 => 0x3FFF,
            4 => 0x1FFFFF,
            5 => 0x0FFFFFFF,
            6 => 0x07FFFFFFFF,
            7 => 0x03FFFFFFFFFF,
            8 => 0x01FFFFFFFFFFFF,
            9 => 0x00FFFFFFFFFFFFFF,
            _ => 0x7F
        };
    }
}

// NuppelVideo (NUV) demuxer
// Reference: VLC modules/demux/nuv.c
// NUV is a container format used by MythTV for DVR recordings

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// NUV (NuppelVideo/MythTV) file type.
/// </summary>
public enum NuvType
{
    /// <summary>Original NuppelVideo format.</summary>
    NuppelVideo,

    /// <summary>MythTV variant with extended features.</summary>
    MythTVVideo
}

/// <summary>
/// NUV video mode.
/// </summary>
public enum NuvVideoMode
{
    /// <summary>Progressive scan.</summary>
    Progressive,

    /// <summary>Interlaced video.</summary>
    Interlaced
}

/// <summary>
/// NUV compression method for video frames.
/// </summary>
public enum NuvVideoCompression : byte
{
    /// <summary>Uncompressed raw video.</summary>
    Uncompressed = 0,

    /// <summary>RTJpeg compression.</summary>
    RtJpeg = 1,

    /// <summary>RTJpeg with LZO compression.</summary>
    RtJpegLzo = 2,

    /// <summary>Black frame (no data).</summary>
    BlackFrame = (byte)'N',

    /// <summary>Copy of last frame.</summary>
    CopyLast = (byte)'L'
}

/// <summary>
/// NUV compression method for audio frames.
/// </summary>
public enum NuvAudioCompression : byte
{
    /// <summary>Uncompressed PCM audio.</summary>
    Uncompressed = 0,

    /// <summary>LZO compressed PCM.</summary>
    Lzo = 1,

    /// <summary>MPEG Layer 2.</summary>
    Mp2 = 2,

    /// <summary>MPEG Layer 3.</summary>
    Mp3 = 3,

    /// <summary>FLAC lossless.</summary>
    Flac = (byte)'F',

    /// <summary>Shorten lossless.</summary>
    Shorten = (byte)'S',

    /// <summary>Null frame (no data).</summary>
    Null = (byte)'N',

    /// <summary>Copy of last frame.</summary>
    CopyLast = (byte)'L'
}

/// <summary>
/// NUV frame type identifier.
/// </summary>
public enum NuvFrameType : byte
{
    /// <summary>Audio frame.</summary>
    Audio = (byte)'A',

    /// <summary>Video frame.</summary>
    Video = (byte)'V',

    /// <summary>Sync info frame.</summary>
    Sync = (byte)'S',

    /// <summary>Codec extra data.</summary>
    CodecData = (byte)'D',

    /// <summary>Extended header.</summary>
    Extended = (byte)'X',

    /// <summary>Seek table.</summary>
    SeekTable = (byte)'Q',

    /// <summary>Seek point.</summary>
    SeekPoint = (byte)'R',

    /// <summary>Text/subtitle data.</summary>
    Text = (byte)'T'
}

/// <summary>
/// Main NUV file header (72 bytes).
/// </summary>
public readonly struct NuvHeader
{
    public readonly NuvType Type;
    public readonly string Version;
    public readonly int Width;
    public readonly int Height;
    public readonly int DesiredWidth;
    public readonly int DesiredHeight;
    public readonly NuvVideoMode Mode;
    public readonly double AspectRatio;
    public readonly double FrameRate;
    public readonly int VideoBlocks;
    public readonly int AudioBlocks;
    public readonly int TextBlocks;
    public readonly int KeyframeDistance;

    public NuvHeader(
        NuvType type, string version,
        int width, int height, int desiredWidth, int desiredHeight,
        NuvVideoMode mode, double aspectRatio, double frameRate,
        int videoBlocks, int audioBlocks, int textBlocks, int keyframeDistance)
    {
        Type = type;
        Version = version;
        Width = width;
        Height = height;
        DesiredWidth = desiredWidth;
        DesiredHeight = desiredHeight;
        Mode = mode;
        AspectRatio = aspectRatio;
        FrameRate = frameRate;
        VideoBlocks = videoBlocks;
        AudioBlocks = audioBlocks;
        TextBlocks = textBlocks;
        KeyframeDistance = keyframeDistance;
    }

    public bool HasVideo => VideoBlocks != 0;
    public bool HasAudio => AudioBlocks != 0;
    public bool HasText => TextBlocks != 0;
}

/// <summary>
/// NUV frame header (12 bytes).
/// </summary>
public readonly struct NuvFrameHeader
{
    public readonly NuvFrameType FrameType;
    public readonly byte Compression;
    public readonly bool IsKeyframe;
    public readonly byte Filters;
    public readonly int TimecodeMs;
    public readonly int PayloadLength;

    public NuvFrameHeader(
        NuvFrameType frameType, byte compression, bool isKeyframe,
        byte filters, int timecodeMs, int payloadLength)
    {
        FrameType = frameType;
        Compression = compression;
        IsKeyframe = isKeyframe;
        Filters = filters;
        TimecodeMs = timecodeMs;
        PayloadLength = payloadLength;
    }
}

/// <summary>
/// NUV extended header (512 bytes) with codec info.
/// </summary>
public readonly struct NuvExtendedHeader
{
    public readonly int Version;
    public readonly uint VideoFourCC;
    public readonly uint AudioFourCC;
    public readonly int AudioSampleRate;
    public readonly int AudioBitsPerSample;
    public readonly int AudioChannels;
    public readonly int RtJpegQuality;
    public readonly int RtJpegLumaFilter;
    public readonly int RtJpegChromaFilter;
    public readonly int LavcBitrate;
    public readonly int LavcQMin;
    public readonly int LavcQMax;
    public readonly int LavcMaxBFrames;
    public readonly long SeekTableOffset;
    public readonly long KeyframeAdjustOffset;

    public NuvExtendedHeader(
        int version, uint videoFourCC, uint audioFourCC,
        int audioSampleRate, int audioBitsPerSample, int audioChannels,
        int rtJpegQuality, int rtJpegLumaFilter, int rtJpegChromaFilter,
        int lavcBitrate, int lavcQMin, int lavcQMax, int lavcMaxBFrames,
        long seekTableOffset, long keyframeAdjustOffset)
    {
        Version = version;
        VideoFourCC = videoFourCC;
        AudioFourCC = audioFourCC;
        AudioSampleRate = audioSampleRate;
        AudioBitsPerSample = audioBitsPerSample;
        AudioChannels = audioChannels;
        RtJpegQuality = rtJpegQuality;
        RtJpegLumaFilter = rtJpegLumaFilter;
        RtJpegChromaFilter = rtJpegChromaFilter;
        LavcBitrate = lavcBitrate;
        LavcQMin = lavcQMin;
        LavcQMax = lavcQMax;
        LavcMaxBFrames = lavcMaxBFrames;
        SeekTableOffset = seekTableOffset;
        KeyframeAdjustOffset = keyframeAdjustOffset;
    }

    public string VideoCodec => FourCCToString(VideoFourCC);
    public string AudioCodec => FourCCToString(AudioFourCC);

    private static string FourCCToString(uint fourcc)
    {
        if (fourcc == 0) return string.Empty;
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)(fourcc & 0xFF);
        chars[1] = (char)((fourcc >> 8) & 0xFF);
        chars[2] = (char)((fourcc >> 16) & 0xFF);
        chars[3] = (char)((fourcc >> 24) & 0xFF);
        return new string(chars).TrimEnd('\0');
    }
}

/// <summary>
/// NUV seek table entry.
/// </summary>
public readonly struct NuvSeekEntry
{
    public readonly long FileOffset;
    public readonly int KeyframeNumber;

    public NuvSeekEntry(long fileOffset, int keyframeNumber)
    {
        FileOffset = fileOffset;
        KeyframeNumber = keyframeNumber;
    }
}

/// <summary>
/// Result of parsing a NUV file.
/// </summary>
public sealed class NuvParseResult
{
    public NuvHeader Header { get; init; }
    public NuvExtendedHeader? ExtendedHeader { get; init; }
    public IReadOnlyList<NuvSeekEntry> SeekTable { get; init; } = [];
    public long FirstFrameOffset { get; init; }
    public long TotalDurationMs { get; init; }
    public int VideoTrackId { get; init; } = -1;
    public int AudioTrackId { get; init; } = -1;
}

/// <summary>
/// NuppelVideo (NUV) demuxer for MythTV DVR recordings.
/// </summary>
/// <remarks>
/// NUV files contain:
/// - 72-byte main header with format info
/// - Optional 512-byte extended header (frame type 'X')
/// - Interleaved audio/video frames with 12-byte frame headers
/// - Optional seek table for keyframe navigation
/// 
/// Video codecs: RTJpeg, MPEG-4, MJPEG, H.264
/// Audio codecs: MP3, FLAC, PCM
/// </remarks>
public static class NuvDemuxer
{
    private const int MainHeaderSize = 72;
    private const int FrameHeaderSize = 12;
    private const int ExtendedHeaderSize = 512;

    private static readonly byte[] NuppelVideoSignature = "NuppelVideo\0"u8.ToArray();
    private static readonly byte[] MythTVVideoSignature = "MythTVVideo\0"u8.ToArray();

    /// <summary>
    /// Checks if data appears to be a NUV file.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MainHeaderSize)
            return false;

        return data[..12].SequenceEqual(NuppelVideoSignature) ||
               data[..12].SequenceEqual(MythTVVideoSignature);
    }

    /// <summary>
    /// Parses the NUV file header and structure.
    /// </summary>
    public static NuvParseResult? Parse(ReadOnlySpan<byte> data)
    {
        if (!CanParse(data))
            return null;

        var header = ParseMainHeader(data[..MainHeaderSize]);
        if (header == null)
            return null;

        long offset = MainHeaderSize;
        NuvExtendedHeader? extendedHeader = null;
        var seekTable = new List<NuvSeekEntry>();
        long maxTimecode = 0;

        // Scan for extended header and seek table
        while (offset + FrameHeaderSize <= data.Length)
        {
            var frameHeader = ParseFrameHeader(data.Slice((int)offset, FrameHeaderSize));
            if (frameHeader == null)
                break;

            offset += FrameHeaderSize;

            if (frameHeader.Value.PayloadLength < 0 || offset + frameHeader.Value.PayloadLength > data.Length)
                break;

            switch (frameHeader.Value.FrameType)
            {
                case NuvFrameType.Extended when frameHeader.Value.PayloadLength >= ExtendedHeaderSize:
                    extendedHeader = ParseExtendedHeader(data.Slice((int)offset, ExtendedHeaderSize));
                    break;

                case NuvFrameType.SeekTable:
                    ParseSeekTable(data.Slice((int)offset, frameHeader.Value.PayloadLength), seekTable);
                    break;

                case NuvFrameType.Video:
                case NuvFrameType.Audio:
                    if (frameHeader.Value.TimecodeMs > maxTimecode)
                        maxTimecode = frameHeader.Value.TimecodeMs;
                    break;
            }

            offset += frameHeader.Value.PayloadLength;
        }

        return new NuvParseResult
        {
            Header = header.Value,
            ExtendedHeader = extendedHeader,
            SeekTable = seekTable,
            FirstFrameOffset = MainHeaderSize,
            TotalDurationMs = maxTimecode,
            VideoTrackId = header.Value.HasVideo ? 0 : -1,
            AudioTrackId = header.Value.HasAudio ? 1 : -1
        };
    }

    /// <summary>
    /// Parses the 72-byte main header.
    /// </summary>
    public static NuvHeader? ParseMainHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < MainHeaderSize)
            return null;

        NuvType type;
        if (data[..12].SequenceEqual(NuppelVideoSignature))
            type = NuvType.NuppelVideo;
        else if (data[..12].SequenceEqual(MythTVVideoSignature))
            type = NuvType.MythTVVideo;
        else
            return null;

        // Version string at offset 12, 5 bytes
        string version = Encoding.ASCII.GetString(data.Slice(12, 5)).TrimEnd('\0');

        int pos = 20; // After ID (12) + version (5) + padding (3)

        int width = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int height = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int desiredWidth = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int desiredHeight = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;

        char modeChar = (char)data[pos];
        pos += 4; // mode (1) + padding (3)

        var mode = modeChar == 'I' ? NuvVideoMode.Interlaced : NuvVideoMode.Progressive;

        double aspectRatio = BitConverter.ToDouble(data.Slice(pos, 8));
        pos += 8;
        double frameRate = BitConverter.ToDouble(data.Slice(pos, 8));
        pos += 8;

        int videoBlocks = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int audioBlocks = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int textBlocks = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
        pos += 4;
        int keyframeDistance = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);

        // Validate dimensions
        if (width <= 0 || width > 8192 || height <= 0 || height > 8192)
            return null;

        if (frameRate <= 0 || frameRate > 120)
            frameRate = 29.97; // Default NTSC

        return new NuvHeader(
            type, version,
            width, height, desiredWidth, desiredHeight,
            mode, aspectRatio, frameRate,
            videoBlocks, audioBlocks, textBlocks, keyframeDistance);
    }

    /// <summary>
    /// Parses a 12-byte frame header.
    /// </summary>
    public static NuvFrameHeader? ParseFrameHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < FrameHeaderSize)
            return null;

        byte typeRaw = data[0];
        byte compression = data[1];
        byte keyframe = data[2];
        byte filters = data[3];
        int timecodeMs = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        // Validate frame type
        if (!IsValidFrameType(typeRaw))
            return null;

        return new NuvFrameHeader(
            (NuvFrameType)typeRaw,
            compression,
            keyframe == 0,
            filters,
            timecodeMs,
            payloadLength);
    }

    /// <summary>
    /// Parses the 512-byte extended header.
    /// </summary>
    public static NuvExtendedHeader? ParseExtendedHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < ExtendedHeaderSize)
            return null;

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[0..]);
        uint videoFourCC = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint audioFourCC = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        int audioSampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int audioBitsPerSample = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        int audioChannels = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);

        int rtJpegQuality = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        int rtJpegLumaFilter = BinaryPrimitives.ReadInt32LittleEndian(data[28..]);
        int rtJpegChromaFilter = BinaryPrimitives.ReadInt32LittleEndian(data[32..]);

        int lavcBitrate = BinaryPrimitives.ReadInt32LittleEndian(data[36..]);
        int lavcQMin = BinaryPrimitives.ReadInt32LittleEndian(data[40..]);
        int lavcQMax = BinaryPrimitives.ReadInt32LittleEndian(data[44..]);
        int lavcMaxBFrames = BinaryPrimitives.ReadInt32LittleEndian(data[48..]);

        long seekTableOffset = BinaryPrimitives.ReadInt64LittleEndian(data[52..]);
        long keyframeAdjustOffset = BinaryPrimitives.ReadInt64LittleEndian(data[60..]);

        // Validate audio parameters
        if (audioSampleRate < 0) audioSampleRate = 0;
        if (audioSampleRate > 192000) audioSampleRate = 48000;
        if (audioBitsPerSample <= 0 || audioBitsPerSample > 32) audioBitsPerSample = 16;
        if (audioChannels <= 0 || audioChannels > 8) audioChannels = 2;

        return new NuvExtendedHeader(
            version, videoFourCC, audioFourCC,
            audioSampleRate, audioBitsPerSample, audioChannels,
            rtJpegQuality, rtJpegLumaFilter, rtJpegChromaFilter,
            lavcBitrate, lavcQMin, lavcQMax, lavcMaxBFrames,
            seekTableOffset, keyframeAdjustOffset);
    }

    private static void ParseSeekTable(ReadOnlySpan<byte> data, List<NuvSeekEntry> seekTable)
    {
        // Seek table entries are 12 bytes each: offset (8 bytes) + keyframe number (4 bytes)
        const int entrySize = 12;
        int entryCount = data.Length / entrySize;

        for (int i = 0; i < entryCount; i++)
        {
            int offset = i * entrySize;
            long fileOffset = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
            int keyframeNum = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 8)..]);

            if (fileOffset > 0)
                seekTable.Add(new NuvSeekEntry(fileOffset, keyframeNum));
        }
    }

    private static bool IsValidFrameType(byte type)
    {
        return type is (byte)'A' or (byte)'V' or (byte)'S' or (byte)'D'
            or (byte)'X' or (byte)'Q' or (byte)'R' or (byte)'T';
    }

    /// <summary>
    /// Gets the codec description for the video stream.
    /// </summary>
    public static string GetVideoCodecDescription(NuvParseResult result)
    {
        if (result.ExtendedHeader != null)
        {
            string fourcc = result.ExtendedHeader.Value.VideoCodec;
            if (!string.IsNullOrEmpty(fourcc))
                return $"Video ({fourcc})";
        }

        return "RTJpeg Video";
    }

    /// <summary>
    /// Gets the codec description for the audio stream.
    /// </summary>
    public static string GetAudioCodecDescription(NuvParseResult result)
    {
        if (result.ExtendedHeader != null)
        {
            string fourcc = result.ExtendedHeader.Value.AudioCodec;
            if (!string.IsNullOrEmpty(fourcc))
                return $"Audio ({fourcc})";

            var ext = result.ExtendedHeader.Value;
            if (ext.AudioSampleRate > 0)
                return $"MPEG Audio {ext.AudioSampleRate}Hz {ext.AudioChannels}ch";
        }

        return "MPEG Audio";
    }

    /// <summary>
    /// Iterates through frames in a NUV file.
    /// </summary>
    public static List<(NuvFrameHeader Header, int DataOffset, int DataLength)> GetFrames(byte[] data)
    {
        var frames = new List<(NuvFrameHeader Header, int DataOffset, int DataLength)>();

        if (!CanParse(data))
            return frames;

        int offset = MainHeaderSize;

        while (offset + FrameHeaderSize <= data.Length)
        {
            var frameHeader = ParseFrameHeader(data.AsSpan(offset, FrameHeaderSize));
            if (frameHeader == null)
                break;

            int dataOffset = offset + FrameHeaderSize;
            int dataLength = frameHeader.Value.PayloadLength;

            if (dataOffset + dataLength > data.Length)
                break;

            frames.Add((frameHeader.Value, dataOffset, dataLength));

            offset = dataOffset + dataLength;
        }

        return frames;
    }
}

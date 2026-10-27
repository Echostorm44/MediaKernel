// HX demuxer - IP camera recording format
// Reference: VLC modules/demux/hx.c
// HX is a container format used by IP cameras for H.264/HEVC video with audio

using System.Buffers.Binary;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// HX file video codec type.
/// </summary>
public enum HxVideoCodec
{
    /// <summary>H.264/AVC video.</summary>
    H264,

    /// <summary>H.265/HEVC video.</summary>
    Hevc
}

/// <summary>
/// HX keyframe index entry.
/// </summary>
public readonly struct HxIndexEntry
{
    /// <summary>File offset to the frame.</summary>
    public uint Offset { get; init; }

    /// <summary>Timestamp in milliseconds.</summary>
    public uint TimeMilliseconds { get; init; }
}

/// <summary>
/// HX track information.
/// </summary>
public sealed class HxTrack
{
    /// <summary>Track index (0 = video, 1 = audio).</summary>
    public int Index { get; init; }

    /// <summary>Track type (video or audio).</summary>
    public string Type { get; init; } = "";

    /// <summary>Codec identifier.</summary>
    public string Codec { get; init; } = "";

    /// <summary>Video width (video only).</summary>
    public int Width { get; init; }

    /// <summary>Video height (video only).</summary>
    public int Height { get; init; }

    /// <summary>Sample rate in Hz (audio only).</summary>
    public int SampleRate { get; init; }

    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Bits per sample (audio only).</summary>
    public int BitsPerSample { get; init; }
}

/// <summary>
/// HX demuxer packet.
/// </summary>
public readonly struct HxPacket
{
    /// <summary>Track index.</summary>
    public int TrackIndex { get; init; }

    /// <summary>Packet data.</summary>
    public byte[] Data { get; init; }

    /// <summary>Presentation timestamp in microseconds.</summary>
    public long Pts { get; init; }

    /// <summary>Decoding timestamp in microseconds.</summary>
    public long Dts { get; init; }

    /// <summary>Whether this is a keyframe.</summary>
    public bool IsKeyframe { get; init; }
}

/// <summary>
/// Demuxer for HX IP camera recording format.
/// Supports H.264 and HEVC video with A-law audio.
/// </summary>
public sealed class HxDemuxer : IDisposable
{
    // HX marker tags
    private static readonly byte[] HxvsMarker = "HXVS"u8.ToArray(); // H.264 file
    private static readonly byte[] HxvtMarker = "HXVT"u8.ToArray(); // HEVC file
    private static readonly byte[] HxvfMarker = "HXVF"u8.ToArray(); // Video frame
    private static readonly byte[] HxafMarker = "HXAF"u8.ToArray(); // Audio frame
    private static readonly byte[] HxfiMarker = "HXFI"u8.ToArray(); // Index

    private const int HeaderSize = 16;
    private const int IndexSize = 200000;
    private const int AudioPrefixSize = 4;

    // Expected audio format prefix: mono 8kHz A-law
    private static readonly byte[] ExpectedAudioPrefix = { 0x00, 0x01, 0x50, 0x00 };

    private readonly Stream stream;
    private readonly List<HxTrack> tracks = new();
    private readonly List<HxIndexEntry> keyframeIndex = new();

    private bool isInitialized;
    private bool isDisposed;
    private HxVideoCodec videoCodec;
    private int videoWidth;
    private int videoHeight;
    private uint durationMilliseconds;

    private long videoPtsOffset = -1;
    private long audioPtsOffset = -1;
    private long currentPcr;

    public HxDemuxer(Stream inputStream)
    {
        stream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
    }

    /// <summary>Gets all discovered tracks.</summary>
    public IReadOnlyList<HxTrack> Tracks => tracks;

    /// <summary>Gets the video codec type (H.264 or HEVC).</summary>
    public HxVideoCodec VideoCodec => videoCodec;

    /// <summary>Gets the video width in pixels.</summary>
    public int VideoWidth => videoWidth;

    /// <summary>Gets the video height in pixels.</summary>
    public int VideoHeight => videoHeight;

    /// <summary>Gets the duration in milliseconds from the index.</summary>
    public uint DurationMilliseconds => durationMilliseconds;

    /// <summary>Gets the keyframe index for seeking.</summary>
    public IReadOnlyList<HxIndexEntry> KeyframeIndex => keyframeIndex;

    /// <summary>Gets whether the demuxer has been initialized.</summary>
    public bool IsInitialized => isInitialized;

    /// <summary>
    /// Probe if the stream contains HX format data.
    /// </summary>
    public static bool Probe(Stream inputStream)
    {
        if (!inputStream.CanRead)
            return false;

        long originalPosition = inputStream.CanSeek ? inputStream.Position : -1;

        try
        {
            Span<byte> header = stackalloc byte[20];
            int bytesRead = inputStream.Read(header);
            if (bytesRead < 20)
                return false;

            // Check for HXVS (H.264) or HXVT (HEVC) header
            bool hasHxHeader = header[..4].SequenceEqual(HxvsMarker) ||
                               header[..4].SequenceEqual(HxvtMarker);

            // First video frame marker should follow at offset 16
            bool hasVideoFrame = header[16..20].SequenceEqual(HxvfMarker);

            return hasHxHeader && hasVideoFrame;
        }
        finally
        {
            if (originalPosition >= 0 && inputStream.CanSeek)
                inputStream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Initializes the demuxer by parsing the HX header.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        try
        {
            ParseHeader();
            isInitialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ParseHeader()
    {
        Span<byte> header = stackalloc byte[HeaderSize];

        if (stream.Read(header) != HeaderSize)
            throw new InvalidDataException("Failed to read HX file header");

        // Determine video codec from file marker
        if (header[..4].SequenceEqual(HxvsMarker))
        {
            videoCodec = HxVideoCodec.H264;
        }
        else if (header[..4].SequenceEqual(HxvtMarker))
        {
            videoCodec = HxVideoCodec.Hevc;
        }
        else
        {
            throw new InvalidDataException("Invalid HX file signature");
        }

        // Header format: [tag.4][width.4][height.4][?.4]
        videoWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        videoHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);

        // Create video track
        tracks.Add(new HxTrack
        {
            Index = 0,
            Type = "video",
            Codec = videoCodec == HxVideoCodec.H264 ? "H264" : "HEVC",
            Width = videoWidth,
            Height = videoHeight
        });

        // Create audio track (A-law 8kHz mono)
        tracks.Add(new HxTrack
        {
            Index = 1,
            Type = "audio",
            Codec = "ALAW",
            SampleRate = 8000,
            ChannelCount = 1,
            BitsPerSample = 8
        });

        // Try to load index from end of file
        if (stream.CanSeek)
        {
            long currentPosition = stream.Position;
            TryLoadIndex();
            stream.Position = currentPosition;
        }
    }

    private void TryLoadIndex()
    {
        long fileSize = stream.Length;
        if (fileSize < IndexSize + HeaderSize)
            return;

        // Index is at fixed location near end of file
        long indexPosition = fileSize - IndexSize - HeaderSize;
        stream.Position = indexPosition;

        Span<byte> indexHeader = stackalloc byte[HeaderSize];
        if (stream.Read(indexHeader) != HeaderSize)
            return;

        if (!indexHeader[..4].SequenceEqual(HxfiMarker))
            return;

        // [HXFI.4][size.4][duration.4][?.4]
        durationMilliseconds = BinaryPrimitives.ReadUInt32LittleEndian(indexHeader[8..]);

        // Read index entries (each 8 bytes: offset.4, time.4)
        Span<byte> entryBuffer = stackalloc byte[8];
        uint previousTime = uint.MaxValue;

        while (true)
        {
            if (stream.Read(entryBuffer) != 8)
                break;

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer);
            uint time = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[4..]);

            if (offset == 0)
                break;

            // Skip duplicates
            if (time != previousTime)
            {
                previousTime = time;
                keyframeIndex.Add(new HxIndexEntry
                {
                    Offset = offset,
                    TimeMilliseconds = time
                });
            }
        }
    }

    /// <summary>
    /// Reads the next packet from the stream.
    /// </summary>
    /// <returns>The next packet, or null if end of stream.</returns>
    public HxPacket? ReadPacket()
    {
        if (!isInitialized)
            throw new InvalidOperationException("Demuxer not initialized");

        Span<byte> header = stackalloc byte[HeaderSize];

        while (true)
        {
            int bytesRead = stream.Read(header);
            if (bytesRead < HeaderSize)
                return null; // EOF

            // Identify frame type
            if (header[..4].SequenceEqual(HxvfMarker))
            {
                return ReadVideoFrame(header);
            }
            else if (header[..4].SequenceEqual(HxafMarker))
            {
                return ReadAudioFrame(header);
            }
            else if (header[..4].SequenceEqual(HxvsMarker) ||
                     header[..4].SequenceEqual(HxvtMarker))
            {
                // Skip file header if encountered mid-stream
                continue;
            }
            else if (header[..4].SequenceEqual(HxfiMarker))
            {
                // Index marker - EOF
                return null;
            }
            else
            {
                // Unknown marker - EOF
                return null;
            }
        }
    }

    private HxPacket? ReadVideoFrame(ReadOnlySpan<byte> header)
    {
        // [HXVF.4][size.4][pts.4][?.4]
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint ptsMillis = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);

        if (size == 0 || size > 50_000_000)
            return null;

        byte[] data = new byte[size];
        int bytesRead = stream.Read(data);
        if (bytesRead < (int)size)
            return null;

        // Calculate PTS relative to first frame
        long pts;
        if (videoPtsOffset < 0)
        {
            videoPtsOffset = ptsMillis;
            pts = 0;
        }
        else
        {
            pts = (ptsMillis - videoPtsOffset) * 1000; // Convert to microseconds
        }

        // Update PCR
        if (currentPcr < pts)
            currentPcr = pts;

        // Check if this is a keyframe
        bool isKeyframe = IsKeyframe(data);

        return new HxPacket
        {
            TrackIndex = 0,
            Data = data,
            Pts = pts,
            Dts = pts,
            IsKeyframe = isKeyframe
        };
    }

    private HxPacket? ReadAudioFrame(ReadOnlySpan<byte> header)
    {
        // [HXAF.4][size.4][pts.4][?.4]
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint ptsMillis = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);

        if (size == 0 || size > 10_000_000)
            return null;

        byte[] rawData = new byte[size];
        int bytesRead = stream.Read(rawData);
        if (bytesRead < (int)size)
            return null;

        // Check for and skip audio format prefix
        byte[] data;
        if (size >= AudioPrefixSize &&
            rawData[0..AudioPrefixSize].AsSpan().SequenceEqual(ExpectedAudioPrefix))
        {
            data = rawData[AudioPrefixSize..];
        }
        else
        {
            // Unsupported audio format - skip
            return null;
        }

        // Calculate PTS relative to first frame
        long pts;
        if (audioPtsOffset < 0)
        {
            audioPtsOffset = ptsMillis;
            pts = 0;
        }
        else
        {
            pts = (ptsMillis - audioPtsOffset) * 1000; // Convert to microseconds
        }

        // Update PCR from audio
        if (currentPcr < pts)
            currentPcr = pts;

        return new HxPacket
        {
            TrackIndex = 1,
            Data = data,
            Pts = pts,
            Dts = pts,
            IsKeyframe = true // Audio frames are always keyframes
        };
    }

    private bool IsKeyframe(byte[] data)
    {
        if (data.Length < 5)
            return false;

        // Find NAL unit start code
        int offset = 0;
        while (offset < data.Length - 4)
        {
            // Check for start codes: 0x00 0x00 0x01 or 0x00 0x00 0x00 0x01
            if (data[offset] == 0x00 && data[offset + 1] == 0x00)
            {
                int nalOffset;
                if (data[offset + 2] == 0x01)
                {
                    nalOffset = offset + 3;
                }
                else if (data[offset + 2] == 0x00 && offset + 3 < data.Length && data[offset + 3] == 0x01)
                {
                    nalOffset = offset + 4;
                }
                else
                {
                    offset++;
                    continue;
                }

                if (nalOffset >= data.Length)
                    break;

                byte nalByte = data[nalOffset];

                if (videoCodec == HxVideoCodec.H264)
                {
                    // H.264: NAL type is in bits 0-4
                    int nalType = nalByte & 0x1F;
                    if (nalType == 5) // IDR picture
                        return true;
                    if (nalType == 7 || nalType == 8) // SPS or PPS
                        return true;
                }
                else
                {
                    // HEVC: NAL type is in bits 1-6
                    int nalType = (nalByte >> 1) & 0x3F;
                    if (nalType >= 16 && nalType <= 21) // BLA, IDR, CRA
                        return true;
                    if (nalType == 32 || nalType == 33 || nalType == 34) // VPS, SPS, PPS
                        return true;
                }

                offset = nalOffset;
            }
            else
            {
                offset++;
            }
        }

        return false;
    }

    /// <summary>
    /// Seeks to the nearest keyframe before the specified timestamp.
    /// </summary>
    /// <param name="timestampMicroseconds">Target timestamp in microseconds.</param>
    /// <returns>True if seek was successful.</returns>
    public bool Seek(long timestampMicroseconds)
    {
        if (!stream.CanSeek || keyframeIndex.Count == 0)
            return false;

        uint targetMillis = (uint)(timestampMicroseconds / 1000);

        // Binary search for nearest keyframe
        HxIndexEntry? entry = FindNearestKeyframe(targetMillis);
        if (entry == null)
            return false;

        stream.Position = entry.Value.Offset;

        // Reset timing
        currentPcr = entry.Value.TimeMilliseconds * 1000L;
        videoPtsOffset = -1;
        audioPtsOffset = -1;

        return true;
    }

    private HxIndexEntry? FindNearestKeyframe(uint targetMillis)
    {
        if (keyframeIndex.Count == 0)
            return null;

        int low = 0;
        int high = keyframeIndex.Count - 1;
        HxIndexEntry? result = null;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            var entry = keyframeIndex[mid];

            if (entry.TimeMilliseconds > targetMillis)
            {
                high = mid - 1;
            }
            else
            {
                result = entry;
                if (entry.TimeMilliseconds == targetMillis)
                    break;
                low = mid + 1;
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        keyframeIndex.Clear();
        tracks.Clear();
        isDisposed = true;
    }
}

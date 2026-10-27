using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// FLV (Flash Video) demuxer for legacy streaming content.
/// </summary>
public sealed class FlvDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer;

    /// <summary>
    /// Holds the frame <see cref="ReadNextPacket"/> is returning. The payload travels with the
    /// packet because an FLV file is read strictly forwards: a caller that seeks the same stream
    /// to fetch the payload itself leaves the demuxer pointing somewhere else, and the next tag
    /// header is then read out of the middle of a frame.
    /// </summary>
    private byte[] payloadBuffer;
    private readonly List<FlvTrack> tracks = new();
    private readonly SeekIndex seekIndex = new();
    private readonly List<FlvKeyframeEntry> keyframes = new();

    private bool hasAudio;
    private bool hasVideo;
    private double durationMs;
    private bool isInitialized;
    private bool isDisposed;
    private bool hasKeyframeIndex;
    private uint headerSize;

    private const int DefaultBufferSize = 64 * 1024;
    private const byte FlvSignature1 = (byte)'F';
    private const byte FlvSignature2 = (byte)'L';
    private const byte FlvSignature3 = (byte)'V';

    public FlvDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
        readBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        payloadBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>Gets all discovered tracks.</summary>
    public IReadOnlyList<FlvTrack> Tracks => tracks;

    /// <summary>Gets the duration in milliseconds if available.</summary>
    public double DurationMilliseconds => durationMs;

    /// <summary>Gets whether a keyframe index is available.</summary>
    public bool HasKeyframeIndex => hasKeyframeIndex;

    /// <summary>
    /// Initializes the demuxer by parsing the FLV header.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        // Read FLV header (9 bytes minimum)
        Span<byte> header = stackalloc byte[13];
        if (inputStream.Read(header) < 13)
            return false;

        // Validate signature
        if (header[0] != FlvSignature1 || header[1] != FlvSignature2 || header[2] != FlvSignature3)
            return false;

        byte version = header[3];
        byte flags = header[4];
        hasAudio = (flags & 0x04) != 0;
        hasVideo = (flags & 0x01) != 0;

        headerSize = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(5, 4));

        // Skip to first tag (header includes previous tag size 0)
        if (headerSize > 9)
        {
            inputStream.Position = headerSize + 4; // +4 for previous tag size
        }

        // Read first few tags to discover tracks and look for metadata
        int tagsRead = 0;
        bool foundMetadata = false;
        while (tagsRead < 50 && (tracks.Count == 0 || !foundMetadata ||
               (hasVideo && !tracks.Any(t => t.TrackType == FlvTrackType.Video)) ||
               (hasAudio && !tracks.Any(t => t.TrackType == FlvTrackType.Audio)) ||
               tracks.Any(t => NeedsConfigurationRecord(t) && t.CodecPrivateData.Length == 0)))
        {
            if (!TryReadTagHeader(out var tag))
                break;

            if (tag.TagType == FlvTagType.Script && !foundMetadata)
            {
                TryParseScriptMetadata(tag);
                foundMetadata = true;
            }
            else
            {
                DiscoverTrack(tag);

                // A decoder cannot be built without its configuration record, and the pipeline
                // asks for one before it reads a single packet. The record is in the first tag
                // of each track, which is a tag this loop is already walking past.
                var wanted = tag.TagType == FlvTagType.Audio ? FlvTrackType.Audio
                           : tag.TagType == FlvTagType.Video ? FlvTrackType.Video
                           : FlvTrackType.Unknown;
                int configTrack = wanted == FlvTrackType.Unknown ? -1 : tracks.FindIndex(t => t.TrackType == wanted);
                if (configTrack >= 0)
                    TryReadFrame(tag, configTrack, out _);
            }

            // Skip tag data and previous tag size
            inputStream.Position = tag.DataOffset + tag.DataSize + 4;
            tagsRead++;
        }

        // Build seek index from keyframe entries
        BuildSeekIndex();

        // Reset to first tag for reading
        inputStream.Position = headerSize + 4;

        isInitialized = tracks.Count > 0;
        return isInitialized;
    }

    /// <summary>
    /// Gets the seek index for keyframe-based seeking.
    /// </summary>
    public SeekIndex GetSeekIndex()
    {
        return seekIndex;
    }

    /// <summary>
    /// Reads the next audio or video frame.
    /// </summary>
    /// <remarks>
    /// <para>This follows the body of FFmpeg's <c>flv_read_packet</c> in
    /// <c>libavformat/flvdec.c</c>, and it does three things the tag header alone does not.</para>
    /// <para>A tag's data begins with a codec header - one byte for every tag, plus one
    /// <c>AVCPacketType</c> byte and a 24-bit signed composition offset for AVC/HEVC, plus one
    /// <c>AACPacketType</c> byte for AAC. That header belongs to the container, not the codec,
    /// and handing it to a decoder puts five bytes of rubbish in front of every access unit.</para>
    /// <para>A tag whose packet type is zero carries the decoder configuration record rather than
    /// a frame; FFmpeg turns it into extradata and returns <c>FFERROR_REDO</c>, so it is never a
    /// packet. The same goes for a tag left empty once its header is removed, which is how the
    /// AVC end-of-sequence tag reads.</para>
    /// <para>And an AVC composition offset makes the presentation time differ from the decode
    /// time. Without it every B-frame is presented in decode order.</para>
    /// </remarks>
    public bool ReadNextPacket(out FlvPacket packet)
    {
        packet = default;

        if (!isInitialized)
            throw new InvalidOperationException("Demuxer not initialized.");

        while (TryReadTagHeader(out var tag))
        {
            long nextTag = tag.DataOffset + tag.DataSize + 4;

            if (tag.TagType == FlvTagType.Audio || tag.TagType == FlvTagType.Video)
            {
                var wanted = tag.TagType == FlvTagType.Audio ? FlvTrackType.Audio : FlvTrackType.Video;
                int trackIndex = tracks.FindIndex(t => t.TrackType == wanted);

                if (trackIndex >= 0 && TryReadFrame(tag, trackIndex, out packet))
                {
                    inputStream.Position = nextTag;
                    return true;
                }
            }

            inputStream.Position = nextTag;
        }

        return false;
    }

    /// <summary>
    /// Strips a tag's codec header and reads what is left. Returns false for a tag that carries
    /// configuration rather than a frame, having stored that configuration on the track.
    /// </summary>
    private bool TryReadFrame(FlvTag tag, int trackIndex, out FlvPacket packet)
    {
        packet = default;

        var track = tracks[trackIndex];
        int remaining = (int)tag.DataSize;
        if (remaining < 1)
            return false;

        inputStream.Position = tag.DataOffset;

        // The one byte every tag begins with: frame type and codec for video, format and sample
        // layout for audio. Both were read during discovery; here it is only consumed.
        inputStream.ReadByte();
        remaining--;

        bool isAvc = track.TrackType == FlvTrackType.Video
                     && (track.CodecId == FlvVideoCodec.Avc || track.CodecId == FlvVideoCodec.Hevc);
        bool isAac = track.TrackType == FlvTrackType.Audio && track.CodecId == FlvAudioCodec.Aac;

        int packetType = 1;
        int compositionOffsetMs = 0;

        if (isAvc || isAac)
        {
            if (remaining < 1)
                return false;
            packetType = inputStream.ReadByte();
            remaining--;
        }

        if (isAvc)
        {
            if (remaining < 3)
                return false;
            Span<byte> cts = stackalloc byte[3];
            inputStream.ReadExactly(cts);
            remaining -= 3;

            // 24-bit two's complement, sign extended the way flvdec.c does it.
            compositionOffsetMs = ((cts[0] << 16) | (cts[1] << 8) | cts[2]) + unchecked((int)0xff800000);
            compositionOffsetMs ^= unchecked((int)0xff800000);
        }

        if (packetType == 0)
        {
            // The decoder configuration record - an AVCDecoderConfigurationRecord for AVC, an
            // AudioSpecificConfig for AAC. FFmpeg makes it extradata and reads the next tag.
            if (remaining > 0 && track.CodecPrivateData.Length == 0)
            {
                var config = new byte[remaining];
                inputStream.ReadExactly(config);
                track.CodecPrivateData = config;

                if (isAac)
                    ApplyAudioSpecificConfig(track, config);
            }
            return false;
        }

        // An AVC end-of-sequence tag is a header and nothing else; FFmpeg skips it as an empty
        // packet rather than handing a decoder zero bytes.
        if (remaining <= 0)
            return false;

        long payloadPosition = inputStream.Position;
        EnsurePayloadCapacity(remaining);
        inputStream.ReadExactly(payloadBuffer, 0, remaining);

        packet = new FlvPacket
        {
            TrackIndex = trackIndex,
            TimestampMs = tag.Timestamp,
            PresentationTimestampMs = (long)tag.Timestamp + compositionOffsetMs,
            IsKeyframe = track.TrackType == FlvTrackType.Audio || tag.IsKeyframe,
            PayloadPosition = payloadPosition,
            PayloadSize = remaining,
            PayloadData = payloadBuffer.AsMemory(0, remaining),
        };
        return true;
    }

    private static bool NeedsConfigurationRecord(FlvTrack track) =>
        track.TrackType == FlvTrackType.Video
            ? track.CodecId is FlvVideoCodec.Avc or FlvVideoCodec.Hevc
            : track.CodecId == FlvAudioCodec.Aac;

    /// <summary>
    /// Takes the real sample rate and channel count from an AudioSpecificConfig.
    /// </summary>
    /// <remarks>
    /// The sample rate in an FLV audio tag's header byte is a two-bit index that cannot express
    /// 48 kHz, so every AAC track read from the tag alone claims 44100. FFmpeg ignores the tag
    /// byte for AAC for exactly this reason and reads the configuration record instead.
    /// </remarks>
    private static void ApplyAudioSpecificConfig(FlvTrack track, ReadOnlySpan<byte> config)
    {
        if (config.Length < 2)
            return;

        int bits = (config[0] << 8) | config[1];
        int frequencyIndex = (bits >> 7) & 0x0F;
        int channelConfiguration = (bits >> 3) & 0x0F;

        if (frequencyIndex == 0x0F)
        {
            // An explicit 24-bit rate follows, which needs a third byte and part of a fourth.
            if (config.Length < 5)
                return;
            track.AudioSampleRate = (int)((((uint)config[1] & 0x7F) << 17)
                                        | ((uint)config[2] << 9)
                                        | ((uint)config[3] << 1)
                                        | ((uint)config[4] >> 7));
            channelConfiguration = (config[4] >> 3) & 0x0F;
        }
        else
        {
            track.AudioSampleRate = AacSampleRates[frequencyIndex];
        }

        if (channelConfiguration is > 0 and <= 7)
            track.AudioChannels = channelConfiguration == 7 ? 8 : channelConfiguration;
    }

    /// <summary>ff_mpeg4audio_sample_rates, the AudioSpecificConfig sampling frequency table.</summary>
    private static ReadOnlySpan<int> AacSampleRates =>
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];

    private void EnsurePayloadCapacity(int size)
    {
        if (payloadBuffer.Length >= size)
            return;

        int capacity = payloadBuffer.Length;
        while (capacity < size)
            capacity *= 2;

        ArrayPool<byte>.Shared.Return(payloadBuffer);
        payloadBuffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <summary>
    /// Seeks to the specified timestamp.
    /// </summary>
    public bool Seek(double timestampMs)
    {
        // Use keyframe index if available
        if (hasKeyframeIndex && keyframes.Count > 0)
        {
            // Binary search for nearest preceding keyframe
            int targetIndex = 0;
            for (int i = keyframes.Count - 1; i >= 0; i--)
            {
                if (keyframes[i].TimestampMs <= timestampMs)
                {
                    targetIndex = i;
                    break;
                }
            }

            inputStream.Position = keyframes[targetIndex].FilePosition;
            return true;
        }

        // Fall back to seeking to start
        inputStream.Position = headerSize + 4;
        return true;
    }

    private void DiscoverTrack(FlvTag tag)
    {
        if (tag.TagType == FlvTagType.Audio && !tracks.Any(t => t.TrackType == FlvTrackType.Audio))
        {
            // Read audio header byte
            Span<byte> buffer = stackalloc byte[2];
            long pos = inputStream.Position;
            inputStream.Position = tag.DataOffset;
            inputStream.Read(buffer);
            inputStream.Position = pos;

            byte audioFlags = buffer[0];
            int soundFormat = (audioFlags >> 4) & 0x0F;
            int sampleRateIndex = (audioFlags >> 2) & 0x03;
            bool is16Bit = ((audioFlags >> 1) & 0x01) != 0;
            bool isStereo = (audioFlags & 0x01) != 0;

            var track = new FlvTrack
            {
                TrackType = FlvTrackType.Audio,
                CodecId = soundFormat,
                AudioSampleRate = GetAudioSampleRate(sampleRateIndex, soundFormat),
                AudioChannels = isStereo ? 2 : 1,
                AudioBitsPerSample = is16Bit ? 16 : 8
            };

            tracks.Add(track);
        }
        else if (tag.TagType == FlvTagType.Video && !tracks.Any(t => t.TrackType == FlvTrackType.Video))
        {
            // Read video header byte
            Span<byte> buffer = stackalloc byte[5];
            long pos = inputStream.Position;
            inputStream.Position = tag.DataOffset;
            inputStream.Read(buffer);
            inputStream.Position = pos;

            byte videoFlags = buffer[0];
            int frameType = (videoFlags >> 4) & 0x0F;
            int codecId = videoFlags & 0x0F;

            var track = new FlvTrack
            {
                TrackType = FlvTrackType.Video,
                CodecId = codecId
            };

            // For AVC, read additional config
            if (codecId == FlvVideoCodec.Avc && buffer.Length >= 5)
            {
                byte avcPacketType = buffer[1];
                if (avcPacketType == 0) // AVC sequence header
                {
                    // Would parse SPS/PPS here for resolution
                }
            }

            tracks.Add(track);
        }
    }

    private static int GetAudioSampleRate(int index, int format)
    {
        // AAC always signals sample rate in AudioSpecificConfig
        if (format == FlvAudioCodec.Aac)
            return 44100; // Default, actual rate in codec config

        return index switch
        {
            0 => 5512,
            1 => 11025,
            2 => 22050,
            3 => 44100,
            _ => 44100
        };
    }

    private bool TryReadTagHeader(out FlvTag tag)
    {
        tag = default;

        Span<byte> header = stackalloc byte[11];
        if (inputStream.Read(header) < 11)
            return false;

        byte tagType = header[0];
        uint dataSize = (uint)((header[1] << 16) | (header[2] << 8) | header[3]);
        uint timestamp = (uint)((header[4] << 16) | (header[5] << 8) | header[6]);
        timestamp |= (uint)(header[7] << 24); // Extended timestamp

        // Read first byte of data for keyframe detection
        bool isKeyframe = false;
        if (tagType == FlvTagType.Video && dataSize > 0)
        {
            int firstByte = inputStream.ReadByte();
            if (firstByte >= 0)
            {
                isKeyframe = ((firstByte >> 4) & 0x0F) == 1;
                inputStream.Position--;
            }
        }

        tag = new FlvTag
        {
            TagType = tagType,
            DataSize = dataSize,
            Timestamp = timestamp,
            DataOffset = inputStream.Position,
            IsKeyframe = isKeyframe
        };

        return true;
    }

    /// <summary>
    /// Parses FLV script metadata (AMF format) to extract duration and keyframe index.
    /// </summary>
    private void TryParseScriptMetadata(FlvTag tag)
    {
        if (tag.DataSize < 10)
            return;

        long savedPos = inputStream.Position;
        inputStream.Position = tag.DataOffset;

        try
        {
            // Read script data (AMF format)
            // First value should be "onMetaData" string
            int type = inputStream.ReadByte();
            if (type != AmfType.String)
                return;

            string? name = ReadAmfString();
            if (name != "onMetaData")
                return;

            // Second value should be an ECMA array or object
            type = inputStream.ReadByte();
            if (type == AmfType.EcmaArray)
            {
                // Read array length (ignored, AMF ECMA arrays are terminated by 0x00 0x00 0x09)
                Span<byte> lenBytes = stackalloc byte[4];
                inputStream.Read(lenBytes);

                ParseAmfObjectProperties();
            }
            else if (type == AmfType.Object)
            {
                ParseAmfObjectProperties();
            }
        }
        catch
        {
            // Ignore metadata parsing errors
        }
        finally
        {
            inputStream.Position = savedPos;
        }
    }

    private void ParseAmfObjectProperties()
    {
        List<double>? keyframeTimes = null;
        List<double>? keyframePositions = null;

        while (true)
        {
            // Read property name (short string)
            Span<byte> lenBytes = stackalloc byte[2];
            if (inputStream.Read(lenBytes) < 2)
                break;

            ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            if (nameLen == 0)
            {
                // End of object marker (0x00 0x00 0x09)
                int endMarker = inputStream.ReadByte();
                if (endMarker == AmfType.ObjectEnd)
                    break;
                continue;
            }

            byte[] nameBytes = new byte[nameLen];
            inputStream.Read(nameBytes);
            string propName = Encoding.UTF8.GetString(nameBytes);

            int valueType = inputStream.ReadByte();

            switch (propName)
            {
                case "duration":
                    if (valueType == AmfType.Number)
                    {
                        durationMs = ReadAmfNumber() * 1000.0;
                    }
                    else
                    {
                        SkipAmfValue(valueType);
                    }
                    break;

                case "keyframes":
                    if (valueType == AmfType.Object)
                    {
                        (keyframeTimes, keyframePositions) = ParseKeyframesObject();
                    }
                    else
                    {
                        SkipAmfValue(valueType);
                    }
                    break;

                default:
                    SkipAmfValue(valueType);
                    break;
            }
        }

        // Build keyframe entries from times and positions
        if (keyframeTimes != null && keyframePositions != null)
        {
            int count = Math.Min(keyframeTimes.Count, keyframePositions.Count);
            for (int i = 0; i < count; i++)
            {
                keyframes.Add(new FlvKeyframeEntry
                {
                    TimestampMs = keyframeTimes[i] * 1000.0,
                    FilePosition = (long)keyframePositions[i]
                });
            }
        }
    }

    private (List<double>? times, List<double>? positions) ParseKeyframesObject()
    {
        List<double>? times = null;
        List<double>? positions = null;

        while (true)
        {
            Span<byte> lenBytes = stackalloc byte[2];
            if (inputStream.Read(lenBytes) < 2)
                break;

            ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            if (nameLen == 0)
            {
                inputStream.ReadByte(); // End marker
                break;
            }

            byte[] nameBytes = new byte[nameLen];
            inputStream.Read(nameBytes);
            string propName = Encoding.UTF8.GetString(nameBytes);

            int valueType = inputStream.ReadByte();

            if (valueType == AmfType.StrictArray)
            {
                var array = ReadAmfStrictArray();
                if (propName == "times")
                    times = array;
                else if (propName == "filepositions")
                    positions = array;
            }
            else
            {
                SkipAmfValue(valueType);
            }
        }

        return (times, positions);
    }

    private List<double> ReadAmfStrictArray()
    {
        Span<byte> lenBytes = stackalloc byte[4];
        inputStream.Read(lenBytes);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);

        var result = new List<double>((int)Math.Min(count, 10000));
        for (uint i = 0; i < count; i++)
        {
            int type = inputStream.ReadByte();
            if (type == AmfType.Number)
            {
                result.Add(ReadAmfNumber());
            }
            else
            {
                SkipAmfValue(type);
            }
        }

        return result;
    }

    private double ReadAmfNumber()
    {
        Span<byte> bytes = stackalloc byte[8];
        inputStream.Read(bytes);
        long bits = BinaryPrimitives.ReadInt64BigEndian(bytes);
        return BitConverter.Int64BitsToDouble(bits);
    }

    private string? ReadAmfString()
    {
        Span<byte> lenBytes = stackalloc byte[2];
        if (inputStream.Read(lenBytes) < 2)
            return null;

        ushort len = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
        if (len == 0)
            return string.Empty;

        byte[] bytes = new byte[len];
        inputStream.Read(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private void SkipAmfValue(int type)
    {
        switch (type)
        {
            case AmfType.Number:
                inputStream.Position += 8;
                break;
            case AmfType.Boolean:
                inputStream.Position += 1;
                break;
            case AmfType.String:
                {
                    Span<byte> lenBytes = stackalloc byte[2];
                    inputStream.Read(lenBytes);
                    ushort len = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
                    inputStream.Position += len;
                }
                break;
            case AmfType.Object:
                SkipAmfObject();
                break;
            case AmfType.Null:
            case AmfType.Undefined:
                break;
            case AmfType.EcmaArray:
                inputStream.Position += 4; // Skip count
                SkipAmfObject();
                break;
            case AmfType.StrictArray:
                {
                    Span<byte> lenBytes = stackalloc byte[4];
                    inputStream.Read(lenBytes);
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
                    for (uint i = 0; i < count; i++)
                    {
                        int itemType = inputStream.ReadByte();
                        SkipAmfValue(itemType);
                    }
                }
                break;
            case AmfType.Date:
                inputStream.Position += 10; // 8 bytes timestamp + 2 bytes timezone
                break;
            case AmfType.LongString:
                {
                    Span<byte> lenBytes = stackalloc byte[4];
                    inputStream.Read(lenBytes);
                    uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
                    inputStream.Position += len;
                }
                break;
            default:
                // Unknown type, can't skip reliably
                break;
        }
    }

    private void SkipAmfObject()
    {
        while (true)
        {
            Span<byte> lenBytes = stackalloc byte[2];
            if (inputStream.Read(lenBytes) < 2)
                break;

            ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            if (nameLen == 0)
            {
                int marker = inputStream.ReadByte();
                if (marker == AmfType.ObjectEnd)
                    break;
                continue;
            }

            inputStream.Position += nameLen;
            int valueType = inputStream.ReadByte();
            SkipAmfValue(valueType);
        }
    }

    private void BuildSeekIndex()
    {
        if (keyframes.Count == 0)
            return;

        // Find video track index
        int videoTrackIndex = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].TrackType == FlvTrackType.Video)
            {
                videoTrackIndex = i;
                break;
            }
        }

        // Add keyframes to seek index
        for (int i = 0; i < keyframes.Count; i++)
        {
            var kf = keyframes[i];
            long timestampTicks = (long)(kf.TimestampMs * TimeSpan.TicksPerMillisecond);
            seekIndex.Add(new SeekPoint(timestampTicks, kf.FilePosition, videoTrackIndex, i));
        }

        hasKeyframeIndex = seekIndex.Count > 0;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
        ArrayPool<byte>.Shared.Return(readBuffer);
        ArrayPool<byte>.Shared.Return(payloadBuffer);
    }
}

/// <summary>
/// AMF (Action Message Format) type markers.
/// </summary>
internal static class AmfType
{
    public const int Number = 0x00;
    public const int Boolean = 0x01;
    public const int String = 0x02;
    public const int Object = 0x03;
    public const int Null = 0x05;
    public const int Undefined = 0x06;
    public const int EcmaArray = 0x08;
    public const int ObjectEnd = 0x09;
    public const int StrictArray = 0x0A;
    public const int Date = 0x0B;
    public const int LongString = 0x0C;
}

/// <summary>
/// FLV keyframe index entry.
/// </summary>
internal struct FlvKeyframeEntry
{
    public double TimestampMs;
    public long FilePosition;
}

/// <summary>
/// FLV tag types.
/// </summary>
public static class FlvTagType
{
    public const byte Audio = 8;
    public const byte Video = 9;
    public const byte Script = 18;
}

/// <summary>
/// FLV audio codec IDs.
/// </summary>
public static class FlvAudioCodec
{
    public const int LinearPcm = 0;
    public const int Adpcm = 1;
    public const int Mp3 = 2;
    public const int LinearPcmLe = 3;
    public const int Aac = 10;
    public const int Speex = 11;
    public const int Mp3_8kHz = 14;
}

/// <summary>
/// FLV video codec IDs.
/// </summary>
public static class FlvVideoCodec
{
    public const int Jpeg = 1;      // Sorenson H.263
    public const int H263 = 2;      // Sorenson Spark
    public const int Screen = 3;    // Screen video
    public const int Vp6 = 4;       // On2 VP6
    public const int Vp6Alpha = 5;  // VP6 with alpha
    public const int Screen2 = 6;   // Screen video v2
    public const int Avc = 7;       // H.264/AVC
    public const int Hevc = 12;     // H.265/HEVC (enhanced FLV)
}

/// <summary>
/// FLV track type.
/// </summary>
public enum FlvTrackType
{
    Unknown,
    Video,
    Audio
}

/// <summary>
/// FLV track information.
/// </summary>
public class FlvTrack
{
    public FlvTrackType TrackType { get; set; }
    public int CodecId { get; set; }
    
    // Audio
    public int AudioSampleRate { get; set; }
    public int AudioChannels { get; set; }
    public int AudioBitsPerSample { get; set; }

    // Video
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// The decoder configuration record this track's first packet-type-zero tag carried - an
    /// AVCDecoderConfigurationRecord, or an AudioSpecificConfig. Empty until one is seen.
    /// </summary>
    public byte[] CodecPrivateData { get; set; } = [];

    public string GetCodecName()
    {
        if (TrackType == FlvTrackType.Video)
        {
            return CodecId switch
            {
                FlvVideoCodec.H263 => "h263",
                FlvVideoCodec.Vp6 or FlvVideoCodec.Vp6Alpha => "vp6",
                FlvVideoCodec.Avc => "h264",
                FlvVideoCodec.Hevc => "hevc",
                _ => "unknown"
            };
        }
        else if (TrackType == FlvTrackType.Audio)
        {
            return CodecId switch
            {
                FlvAudioCodec.Mp3 or FlvAudioCodec.Mp3_8kHz => "mp3",
                FlvAudioCodec.Aac => "aac",
                FlvAudioCodec.Speex => "speex",
                FlvAudioCodec.LinearPcm or FlvAudioCodec.LinearPcmLe => "pcm",
                FlvAudioCodec.Adpcm => "adpcm",
                _ => "unknown"
            };
        }
        return "unknown";
    }
}

/// <summary>
/// Parsed FLV tag header.
/// </summary>
internal struct FlvTag
{
    public byte TagType;
    public uint DataSize;
    public uint Timestamp;
    public long DataOffset;
    public bool IsKeyframe;
}

/// <summary>
/// FLV packet for output.
/// </summary>
public struct FlvPacket
{
    public int TrackIndex;

    /// <summary>The tag's own timestamp, which is the decode time.</summary>
    public uint TimestampMs;

    /// <summary>
    /// The decode time plus the AVC composition offset. Equal to <see cref="TimestampMs"/> for
    /// every format that has no such offset.
    /// </summary>
    public long PresentationTimestampMs;

    public bool IsKeyframe;

    /// <summary>Where the frame starts in the file, past the tag's codec header.</summary>
    public long PayloadPosition;

    public int PayloadSize;

    /// <summary>
    /// The frame itself. Valid until the next <see cref="FlvDemuxer.ReadNextPacket"/> call.
    /// </summary>
    public ReadOnlyMemory<byte> PayloadData;
}

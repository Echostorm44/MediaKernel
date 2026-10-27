// Demuxer Types and Interfaces
// Common types shared across all demuxer implementations

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Represents a seek index entry for random access.
/// Maps a timestamp to a file position at or before a keyframe.
/// </summary>
public readonly struct SeekPoint
{
    /// <summary>Timestamp in ticks (100ns units).</summary>
    public readonly long TimestampTicks;
    
    /// <summary>Absolute file position.</summary>
    public readonly long FilePosition;
    
    /// <summary>Track ID this seek point applies to (0 for container-level).</summary>
    public readonly int TrackId;
    
    /// <summary>Sample/frame number at this position (if known).</summary>
    public readonly long SampleIndex;

    public SeekPoint(long timestampTicks, long filePosition, int trackId = 0, long sampleIndex = 0)
    {
        TimestampTicks = timestampTicks;
        FilePosition = filePosition;
        TrackId = trackId;
        SampleIndex = sampleIndex;
    }
    
    /// <summary>
    /// Gets the timestamp in milliseconds.
    /// </summary>
    public double TimestampMilliseconds => TimestampTicks / (double)TimeSpan.TicksPerMillisecond;
    
    /// <summary>
    /// Gets the timestamp as a TimeSpan.
    /// </summary>
    public TimeSpan Timestamp => TimeSpan.FromTicks(TimestampTicks);
}

/// <summary>
/// Track type enumeration.
/// </summary>
public enum DemuxerTrackType
{
    Unknown = 0,
    Video = 1,
    Audio = 2,
    Subtitle = 3
}

/// <summary>
/// Information about a media track in a container.
/// </summary>
public sealed class DemuxerTrackInfo
{
    /// <summary>Track ID within the container.</summary>
    public required int TrackId { get; init; }
    
    /// <summary>Track type (video/audio/subtitle).</summary>
    public required DemuxerTrackType TrackType { get; init; }
    
    /// <summary>Codec identifier (e.g., "h264", "aac", "mp3").</summary>
    public required string CodecId { get; init; }
    
    /// <summary>Human-readable track name.</summary>
    public string? Name { get; init; }
    
    /// <summary>Language code (ISO 639).</summary>
    public string? Language { get; init; }
    
    /// <summary>Whether this is the default track for its type.</summary>
    public bool IsDefault { get; init; }
    
    /// <summary>Codec-specific initialization data (SPS/PPS, AudioSpecificConfig, etc.).</summary>
    public byte[]? CodecPrivateData { get; init; }
    
    // Video-specific properties
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate (frames per second).</summary>
    public double FrameRate { get; init; }
    
    // Audio-specific properties
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }

    /// <summary>
    /// Bytes one coded block occupies, for a codec whose frames are blocks of fixed size.
    /// </summary>
    /// <remarks>
    /// ADPCM needs it and cannot work it out: the block alignment is in the WAV format chunk,
    /// not in any codec-specific blob, and it decides how many samples a packet holds.
    /// </remarks>
    public int BlockAlign { get; init; }

    /// <summary>Bits one coded sample occupies - 4 for the common ADPCM variants.</summary>
    public int BitsPerCodedSample { get; init; }
    
    /// <summary>Bits per sample (for PCM).</summary>
    public int BitsPerSample { get; init; }

    /// <summary>
    /// The container's four-character code for this track, packed as <c>MKTAG</c> packs it,
    /// or zero when the container has none.
    /// </summary>
    /// <remarks>
    /// <c>codec_tag</c> in ffmpeg. Nothing needed it until uncompressed video: an AVI's
    /// <c>biCompression</c> is the ONLY thing that says which of 173 pixel formats a raw
    /// track holds, and for several of them it is also the only thing that says the chroma
    /// planes are the other way round. Every other codec here is named by its tag and then
    /// reads its own format out of the bitstream.
    /// </remarks>
    public uint CodecTag { get; init; }

    /// <summary>
    /// Whether this track came from a QuickTime-family container. Raw video needs it: a
    /// depth-only lookup reads <c>pix_fmt_bps_mov</c> there and <c>pix_fmt_bps_avi</c>
    /// elsewhere, and the two disagree about 16, 24 and 32 bits.
    /// </summary>
    public bool IsQuickTime { get; init; }

    /// <summary>
    /// The track's colour table as 256 packed 0xAARRGGBB entries, or null when it has none.
    /// </summary>
    /// <remarks>
    /// <c>ff_get_qtpalette</c>, which reads it 82 bytes into a QuickTime video sample
    /// description. A paletted codec cannot work it out - the frame holds indices and
    /// nothing else - and an all-zero palette is indistinguishable from a missing one,
    /// which is a black picture of the right size.
    /// </remarks>
    public uint[]? Palette { get; init; }
}

/// <summary>
/// Result of a demuxer seek operation.
/// </summary>
public readonly struct SeekResult
{
    /// <summary>Whether the seek was successful.</summary>
    public readonly bool Success;
    
    /// <summary>Actual timestamp seeked to (may differ from requested).</summary>
    public readonly long ActualTimestampTicks;
    
    /// <summary>Whether seeking was approximate (e.g., no keyframes available).</summary>
    public readonly bool IsApproximate;
    
    /// <summary>Error message if seek failed.</summary>
    public readonly string? ErrorMessage;

    private SeekResult(bool success, long actualTimestamp, bool isApproximate, string? error)
    {
        Success = success;
        ActualTimestampTicks = actualTimestamp;
        IsApproximate = isApproximate;
        ErrorMessage = error;
    }

    public static SeekResult Succeeded(long actualTimestamp, bool isApproximate = false)
        => new(true, actualTimestamp, isApproximate, null);
    
    public static SeekResult Failed(string error)
        => new(false, 0, false, error);
    
    /// <summary>
    /// Gets the actual timestamp as a TimeSpan.
    /// </summary>
    public TimeSpan ActualTimestamp => TimeSpan.FromTicks(ActualTimestampTicks);
}

/// <summary>
/// Capabilities of a demuxer.
/// </summary>
[Flags]
public enum DemuxerCapabilities
{
    None = 0,
    
    /// <summary>Supports seeking to an exact position.</summary>
    CanSeek = 1,
    
    /// <summary>Has a pre-built seek index (fast seeking).</summary>
    HasSeekIndex = 2,
    
    /// <summary>Can build seek index during playback.</summary>
    CanBuildIndex = 4,
    
    /// <summary>Supports reading duration.</summary>
    HasDuration = 8,
    
    /// <summary>Supports multiple tracks.</summary>
    HasMultipleTracks = 16
}

/// <summary>
/// A media packet extracted from a container.
/// </summary>
public readonly struct DemuxerPacket
{
    /// <summary>Track this packet belongs to.</summary>
    public readonly int TrackId;
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public readonly long PresentationTimestampTicks;
    
    /// <summary>Decode timestamp in ticks (same as PTS if not specified).</summary>
    public readonly long DecodeTimestampTicks;
    
    /// <summary>Duration in ticks (0 if unknown).</summary>
    public readonly long DurationTicks;
    
    /// <summary>Whether this is a keyframe (sync point).</summary>
    public readonly bool IsKeyframe;
    
    /// <summary>Packet data.</summary>
    /// <remarks>
    /// <b>Lifetime:</b> this is usually a window onto the demuxer's own read buffer, not a
    /// copy, and it is valid only until the next read from that demuxer. A caller that needs
    /// the bytes for longer copies them. Demuxers used to hand back a fresh array per packet,
    /// which made the question moot at the cost of about a kilobyte of garbage per packet on
    /// every elementary stream - and a collection during playback is a dropped frame.
    /// </remarks>
    public readonly ReadOnlyMemory<byte> Data;

    /// <summary>
    /// Sample frames to drop from the end of this packet's decoded output, which is
    /// FFmpeg's <c>AV_PKT_DATA_SKIP_SAMPLES</c> trailing count.
    /// </summary>
    /// <remarks>
    /// A block-coded format decodes whole blocks, so the last packet of a stream yields
    /// more audio than the file holds and only the container knows how much. CAF says so
    /// in its 'pakt' chunk; without this the pipeline emitted 60 samples of the encoder's
    /// padding past the end of a two-second file, which ffmpeg does not.
    /// </remarks>
    public readonly int EndTrimSamples;

    public DemuxerPacket(
        int trackId,
        long presentationTimestamp,
        long decodeTimestamp,
        long duration,
        bool isKeyframe,
        ReadOnlyMemory<byte> data,
        int endTrimSamples = 0)
    {
        TrackId = trackId;
        PresentationTimestampTicks = presentationTimestamp;
        DecodeTimestampTicks = decodeTimestamp;
        DurationTicks = duration;
        IsKeyframe = isKeyframe;
        Data = data;
        EndTrimSamples = endTrimSamples;
    }
    
    /// <summary>
    /// Gets the presentation timestamp as a TimeSpan.
    /// </summary>
    public TimeSpan PresentationTimestamp => TimeSpan.FromTicks(PresentationTimestampTicks);
}

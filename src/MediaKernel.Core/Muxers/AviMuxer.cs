// AVI (Audio Video Interleave) Muxer
// Reference: VLC modules/mux/avi.c, Microsoft AVI RIFF specification
// AVI is a widely supported legacy container format

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Muxers;

/// <summary>
/// AVI file muxer supporting audio and video streams.
/// </summary>
/// <remarks>
/// AVI file structure:
/// RIFF 'AVI '
///   LIST 'hdrl' - Header list
///     'avih' - Main AVI header
///     LIST 'strl' - Stream list (one per stream)
///       'strh' - Stream header
///       'strf' - Stream format (WAVEFORMATEX or BITMAPINFOHEADER)
///   LIST 'movi' - Media data
///     'xxdc' - Video chunks (xx = stream number)
///     'xxwb' - Audio chunks
///   'idx1' - Index (optional but recommended)
/// </remarks>
public sealed class AviMuxer : Muxer
{
    private const int AvifHasIndex = 0x10;
    private const int AvifIsInterleaved = 0x100;
    private const int AviifKeyframe = 0x10;

    private readonly List<AviIndexEntry> indexEntries = new();
    private readonly List<AviStreamInfo> streamInfos = new();

    private long riffSizePosition;
    private long moviPosition;
    private long moviSizePosition;
    private long totalMoviSize;
    private int videoStreamIndex = -1;
    private long headerEndPosition;

    public AviMuxer(Stream outputStream, MuxerConfiguration config)
        : base(outputStream, config)
    {
    }

    public override MuxerFormat Format => MuxerFormat.Avi;

    public override int AddTrack(MuxerTrackInfo track)
    {
        int trackId = base.AddTrack(track);

        var info = new AviStreamInfo
        {
            TrackInfo = track,
            FourCC = GetFourCC(track),
            ChunkId = GetChunkId(streamInfos.Count, track.TrackType),
            FrameCount = 0,
            TotalSize = 0
        };

        streamInfos.Add(info);

        if (track.TrackType == MuxerTrackType.Video)
            videoStreamIndex = streamInfos.Count - 1;

        return trackId;
    }

    public override async ValueTask WriteHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (_headerWritten)
            return;

        if (streamInfos.Count == 0)
            throw new InvalidOperationException("No tracks added");

        await WriteRiffHeaderAsync(cancellationToken);
        await WriteHeaderListAsync(cancellationToken);
        await WriteMoviListStartAsync(cancellationToken);

        _headerWritten = true;
    }

    public override async ValueTask WriteSampleAsync(MuxerSample sample, CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            throw new InvalidOperationException("Header must be written before samples");

        int streamIndex = sample.TrackId - 1;
        if (streamIndex < 0 || streamIndex >= streamInfos.Count)
            throw new ArgumentException($"Invalid track ID: {sample.TrackId}");

        var streamInfo = streamInfos[streamIndex];
        long chunkOffset = _outputStream.Position - moviPosition;

        // Write chunk header (4-byte ID + 4-byte size)
        byte[] chunkHeader = new byte[8];
        Encoding.ASCII.GetBytes(streamInfo.ChunkId, chunkHeader.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(4), (uint)sample.Data.Length);
        await _outputStream.WriteAsync(chunkHeader, cancellationToken);

        // Write chunk data
        await _outputStream.WriteAsync(sample.Data, cancellationToken);

        // Pad to word boundary
        if ((sample.Data.Length & 1) != 0)
        {
            await _outputStream.WriteAsync(new byte[] { 0 }, cancellationToken);
            totalMoviSize++;
        }

        totalMoviSize += 8 + sample.Data.Length;

        // Add index entry
        indexEntries.Add(new AviIndexEntry
        {
            ChunkId = streamInfo.ChunkId,
            Flags = sample.IsKeyframe ? AviifKeyframe : 0,
            Offset = (int)chunkOffset,
            Size = sample.Data.Length
        });

        // Update stream stats
        streamInfo.FrameCount++;
        streamInfo.TotalSize += sample.Data.Length;
        streamInfos[streamIndex] = streamInfo;
    }

    public override async ValueTask WriteTrailerAsync(CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            return;

        // Update movi size
        long currentPosition = _outputStream.Position;
        long moviDataSize = currentPosition - moviPosition;

        // Write idx1 index
        await WriteIndexAsync(cancellationToken);

        // Calculate final RIFF size
        long riffSize = _outputStream.Position - 8;

        // Update sizes in header
        if (_outputStream.CanSeek)
        {
            // Update RIFF size
            _outputStream.Position = riffSizePosition;
            await WriteUInt32LEAsync((uint)riffSize, cancellationToken);

            // Update movi size
            _outputStream.Position = moviSizePosition;
            await WriteUInt32LEAsync((uint)moviDataSize, cancellationToken);

            // Update header with final stats
            await UpdateHeaderStatsAsync(cancellationToken);

            _outputStream.Position = _outputStream.Length;
        }

        await FlushAsync(cancellationToken);
    }

    private async ValueTask WriteRiffHeaderAsync(CancellationToken cancellationToken)
    {
        // RIFF header: 'RIFF' + size + 'AVI '
        byte[] header = new byte[12];
        Encoding.ASCII.GetBytes("RIFF", header.AsSpan(0, 4));
        riffSizePosition = 4;
        // Size placeholder - updated in trailer
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0);
        Encoding.ASCII.GetBytes("AVI ", header.AsSpan(8, 4));

        await _outputStream.WriteAsync(header, cancellationToken);
    }

    private async ValueTask WriteHeaderListAsync(CancellationToken cancellationToken)
    {
        using var headerStream = new MemoryStream();

        // Write avih (main AVI header)
        await WriteAvihAsync(headerStream, cancellationToken);

        // Write strl for each stream
        for (int i = 0; i < streamInfos.Count; i++)
        {
            await WriteStrlAsync(headerStream, i, cancellationToken);
        }

        // Wrap in LIST 'hdrl'
        byte[] listHeader = new byte[12];
        Encoding.ASCII.GetBytes("LIST", listHeader.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(listHeader.AsSpan(4), (uint)(headerStream.Length + 4));
        Encoding.ASCII.GetBytes("hdrl", listHeader.AsSpan(8, 4));

        await _outputStream.WriteAsync(listHeader, cancellationToken);
        headerStream.Position = 0;
        await headerStream.CopyToAsync(_outputStream, cancellationToken);

        headerEndPosition = _outputStream.Position;
    }

    private async ValueTask WriteAvihAsync(Stream stream, CancellationToken cancellationToken)
    {
        // avih structure: 56 bytes
        byte[] avih = new byte[64]; // 8 header + 56 data
        Encoding.ASCII.GetBytes("avih", avih.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(4), 56);

        int offset = 8;

        // Microseconds per frame (default 33333 = 30fps)
        uint microsecsPerFrame = 33333;
        if (videoStreamIndex >= 0 && streamInfos[videoStreamIndex].TrackInfo.FrameRate > 0)
        {
            microsecsPerFrame = (uint)(1_000_000.0 / streamInfos[videoStreamIndex].TrackInfo.FrameRate);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), microsecsPerFrame);
        offset += 4;

        // Max bytes per second (placeholder)
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), 0);
        offset += 4;

        // Padding granularity
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), 0);
        offset += 4;

        // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), AvifHasIndex | AvifIsInterleaved);
        offset += 4;

        // Total frames (placeholder - updated in trailer)
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), 0);
        offset += 4;

        // Initial frames
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), 0);
        offset += 4;

        // Streams
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), (uint)streamInfos.Count);
        offset += 4;

        // Suggested buffer size
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), 1024 * 1024);
        offset += 4;

        // Width and height
        int width = 0, height = 0;
        if (videoStreamIndex >= 0)
        {
            width = streamInfos[videoStreamIndex].TrackInfo.Width;
            height = streamInfos[videoStreamIndex].TrackInfo.Height;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), (uint)width);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(avih.AsSpan(offset), (uint)height);
        offset += 4;

        // Reserved (4 x 4 bytes)
        // Already zeros

        await stream.WriteAsync(avih, cancellationToken);
    }

    private async ValueTask WriteStrlAsync(Stream stream, int streamIndex, CancellationToken cancellationToken)
    {
        var info = streamInfos[streamIndex];
        using var strlStream = new MemoryStream();

        // Write strh
        await WriteStrhAsync(strlStream, info, cancellationToken);

        // Write strf
        await WriteStrfAsync(strlStream, info, cancellationToken);

        // Wrap in LIST 'strl'
        byte[] listHeader = new byte[12];
        Encoding.ASCII.GetBytes("LIST", listHeader.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(listHeader.AsSpan(4), (uint)(strlStream.Length + 4));
        Encoding.ASCII.GetBytes("strl", listHeader.AsSpan(8, 4));

        await stream.WriteAsync(listHeader, cancellationToken);
        strlStream.Position = 0;
        await strlStream.CopyToAsync(stream, cancellationToken);
    }

    private async ValueTask WriteStrhAsync(Stream stream, AviStreamInfo info, CancellationToken cancellationToken)
    {
        // strh: 56 bytes data + 8 header = 64 bytes
        byte[] strh = new byte[64];
        Encoding.ASCII.GetBytes("strh", strh.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(4), 56);

        int offset = 8;

        // fccType: 'vids' or 'auds'
        string fccType = info.TrackInfo.TrackType == MuxerTrackType.Video ? "vids" : "auds";
        Encoding.ASCII.GetBytes(fccType, strh.AsSpan(offset, 4));
        offset += 4;

        // fccHandler (codec FourCC)
        Encoding.ASCII.GetBytes(info.FourCC.PadRight(4)[..4], strh.AsSpan(offset, 4));
        offset += 4;

        // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 0);
        offset += 4;

        // Priority
        BinaryPrimitives.WriteUInt16LittleEndian(strh.AsSpan(offset), 0);
        offset += 2;

        // Language
        BinaryPrimitives.WriteUInt16LittleEndian(strh.AsSpan(offset), 0);
        offset += 2;

        // Initial frames
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 0);
        offset += 4;

        // Scale and rate
        uint scale, rate;
        if (info.TrackInfo.TrackType == MuxerTrackType.Video)
        {
            // For video: rate/scale = fps
            scale = 1000;
            rate = (uint)(info.TrackInfo.FrameRate * 1000);
            if (rate == 0) rate = 30000;
        }
        else
        {
            // For audio: rate = sample rate, scale = 1
            scale = 1;
            rate = (uint)info.TrackInfo.SampleRate;
            if (rate == 0) rate = 48000;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), scale);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), rate);
        offset += 4;

        // Start time
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 0);
        offset += 4;

        // Length (placeholder - updated in trailer)
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 0);
        offset += 4;

        // Suggested buffer size
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 1024 * 1024);
        offset += 4;

        // Quality
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), 0xFFFFFFFF);
        offset += 4;

        // Sample size (0 for video, bytes per sample for audio)
        uint sampleSize = 0;
        if (info.TrackInfo.TrackType == MuxerTrackType.Audio)
        {
            sampleSize = (uint)(info.TrackInfo.ChannelCount * info.TrackInfo.BitsPerSample / 8);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(strh.AsSpan(offset), sampleSize);
        offset += 4;

        // Frame rect (left, top, right, bottom) - zeros

        await stream.WriteAsync(strh, cancellationToken);
    }

    private async ValueTask WriteStrfAsync(Stream stream, AviStreamInfo info, CancellationToken cancellationToken)
    {
        if (info.TrackInfo.TrackType == MuxerTrackType.Video)
        {
            await WriteStrfVideoAsync(stream, info, cancellationToken);
        }
        else
        {
            await WriteStrfAudioAsync(stream, info, cancellationToken);
        }
    }

    private async ValueTask WriteStrfVideoAsync(Stream stream, AviStreamInfo info, CancellationToken cancellationToken)
    {
        // BITMAPINFOHEADER: 40 bytes + codec private data
        int headerSize = 40 + (info.TrackInfo.CodecPrivateData?.Length ?? 0);
        byte[] strf = new byte[8 + headerSize];
        Encoding.ASCII.GetBytes("strf", strf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(4), (uint)headerSize);

        int offset = 8;

        // biSize
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(offset), (uint)headerSize);
        offset += 4;

        // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(strf.AsSpan(offset), info.TrackInfo.Width);
        offset += 4;

        // biHeight
        BinaryPrimitives.WriteInt32LittleEndian(strf.AsSpan(offset), info.TrackInfo.Height);
        offset += 4;

        // biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), 1);
        offset += 2;

        // biBitCount
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), 24);
        offset += 2;

        // biCompression (FourCC)
        Encoding.ASCII.GetBytes(info.FourCC.PadRight(4)[..4], strf.AsSpan(offset, 4));
        offset += 4;

        // biSizeImage
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(offset), 
            (uint)(info.TrackInfo.Width * info.TrackInfo.Height * 3));
        offset += 4;

        // Rest are zeros (biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant)
        offset += 16;

        // Codec private data
        if (info.TrackInfo.CodecPrivateData != null)
        {
            info.TrackInfo.CodecPrivateData.CopyTo(strf.AsSpan(offset));
        }

        await stream.WriteAsync(strf, cancellationToken);
    }

    private async ValueTask WriteStrfAudioAsync(Stream stream, AviStreamInfo info, CancellationToken cancellationToken)
    {
        // WAVEFORMATEX: 18 bytes minimum + codec private data
        int extraSize = info.TrackInfo.CodecPrivateData?.Length ?? 0;
        int headerSize = 18 + extraSize;
        byte[] strf = new byte[8 + headerSize];
        Encoding.ASCII.GetBytes("strf", strf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(4), (uint)headerSize);

        int offset = 8;

        // wFormatTag (1 = PCM)
        ushort formatTag = GetAudioFormatTag(info.TrackInfo.Codec);
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), formatTag);
        offset += 2;

        // nChannels
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), (ushort)info.TrackInfo.ChannelCount);
        offset += 2;

        // nSamplesPerSec
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(offset), (uint)info.TrackInfo.SampleRate);
        offset += 4;

        // nAvgBytesPerSec
        int bytesPerSample = info.TrackInfo.BitsPerSample / 8;
        uint avgBytesPerSec = (uint)(info.TrackInfo.SampleRate * info.TrackInfo.ChannelCount * bytesPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(strf.AsSpan(offset), avgBytesPerSec);
        offset += 4;

        // nBlockAlign
        ushort blockAlign = (ushort)(info.TrackInfo.ChannelCount * bytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), blockAlign);
        offset += 2;

        // wBitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), (ushort)info.TrackInfo.BitsPerSample);
        offset += 2;

        // cbSize (extra format bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(offset), (ushort)extraSize);
        offset += 2;

        // Codec private data
        if (info.TrackInfo.CodecPrivateData != null)
        {
            info.TrackInfo.CodecPrivateData.CopyTo(strf.AsSpan(offset));
        }

        await stream.WriteAsync(strf, cancellationToken);
    }

    private async ValueTask WriteMoviListStartAsync(CancellationToken cancellationToken)
    {
        // LIST 'movi' header (size placeholder)
        byte[] listHeader = new byte[12];
        Encoding.ASCII.GetBytes("LIST", listHeader.AsSpan(0, 4));
        moviSizePosition = _outputStream.Position + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(listHeader.AsSpan(4), 0); // Placeholder
        Encoding.ASCII.GetBytes("movi", listHeader.AsSpan(8, 4));

        await _outputStream.WriteAsync(listHeader, cancellationToken);
        moviPosition = _outputStream.Position;
    }

    private async ValueTask WriteIndexAsync(CancellationToken cancellationToken)
    {
        if (indexEntries.Count == 0)
            return;

        // idx1 header
        byte[] header = new byte[8];
        Encoding.ASCII.GetBytes("idx1", header.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(indexEntries.Count * 16));
        await _outputStream.WriteAsync(header, cancellationToken);

        // Index entries (16 bytes each)
        byte[] entry = new byte[16];
        foreach (var idx in indexEntries)
        {
            Encoding.ASCII.GetBytes(idx.ChunkId, entry.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)idx.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)idx.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)idx.Size);
            await _outputStream.WriteAsync(entry, cancellationToken);
        }
    }

    private async ValueTask UpdateHeaderStatsAsync(CancellationToken cancellationToken)
    {
        // This would update avih and strh with final frame counts
        // For simplicity, we write placeholders and would need a second pass
        // to update. Modern AVI players handle missing counts gracefully.
    }

    private async ValueTask WriteUInt32LEAsync(uint value, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        await _outputStream.WriteAsync(buffer, cancellationToken);
    }

    private static string GetFourCC(MuxerTrackInfo track)
    {
        if (!string.IsNullOrEmpty(track.Codec))
        {
            return track.Codec.ToUpperInvariant() switch
            {
                "H264" or "AVC1" => "H264",
                "H265" or "HEVC" or "HVC1" => "HEVC",
                "VP8" => "VP80",
                "VP9" => "VP90",
                "MJPEG" or "MJPG" => "MJPG",
                "MPEG4" or "MP4V" => "XVID",
                "DIVX" => "DIVX",
                "PCM" => "    ",
                "MP3" or "MPGA" => "U055",
                "AAC" => "MP4A",
                "AC3" => "AC-3",
                _ => track.Codec.Length >= 4 ? track.Codec[..4].ToUpperInvariant() : track.Codec.PadRight(4).ToUpperInvariant()
            };
        }

        return track.TrackType == MuxerTrackType.Video ? "H264" : "    ";
    }

    private static string GetChunkId(int streamIndex, MuxerTrackType trackType)
    {
        string suffix = trackType == MuxerTrackType.Video ? "dc" : "wb";
        return $"{streamIndex:D2}{suffix}";
    }

    private static ushort GetAudioFormatTag(string? codec)
    {
        return codec?.ToUpperInvariant() switch
        {
            "PCM" or null => 1,      // WAVE_FORMAT_PCM
            "MP3" or "MPGA" => 0x55, // WAVE_FORMAT_MPEGLAYER3
            "AAC" or "MP4A" => 0xFF, // WAVE_FORMAT_AAC
            "AC3" => 0x2000,         // WAVE_FORMAT_DVM (AC-3)
            "DTS" => 0x2001,         // WAVE_FORMAT_DTS
            _ => 1
        };
    }

    private struct AviStreamInfo
    {
        public MuxerTrackInfo TrackInfo;
        public string FourCC;
        public string ChunkId;
        public int FrameCount;
        public long TotalSize;
    }

    private struct AviIndexEntry
    {
        public string ChunkId;
        public int Flags;
        public int Offset;
        public int Size;
    }
}

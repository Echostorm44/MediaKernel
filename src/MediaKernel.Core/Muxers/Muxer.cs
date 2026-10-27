// MediaKernel - Media Muxer Infrastructure
// Muxers combine audio, video, and subtitle tracks into container formats

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace MediaKernel.Core.Muxers;

/// <summary>
/// Supported muxer output formats.
/// </summary>
public enum MuxerFormat
{
    Unknown,
    Mp4,
    Matroska,
    Avi,
    Wav,
    Ogg,
    MpegTs,
    Flv,
    MpJpeg,    // HTTP multipart JPEG streaming
    WebVtt     // WebVTT subtitle file
}

/// <summary>
/// Configuration for creating a muxer.
/// </summary>
public sealed class MuxerConfiguration
{
    public MuxerFormat Format { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Copyright { get; init; }
    public string? Comment { get; init; }
    public bool EnableFastStart { get; init; } = true; // Move moov atom to start for MP4
    public bool FragmentedOutput { get; init; } = false; // Fragmented MP4 for streaming
    public TimeSpan? MaxDuration { get; init; }
    public long? MaxFileSize { get; init; }
}

/// <summary>
/// Track type for muxer.
/// </summary>
public enum MuxerTrackType
{
    Audio,
    Video,
    Subtitle
}

/// <summary>
/// Describes a track to be muxed.
/// </summary>
public readonly struct MuxerTrackInfo
{
    public int TrackId { get; init; }
    public MuxerTrackType TrackType { get; init; }
    public string? Codec { get; init; }
    public byte[]? CodecPrivateData { get; init; }
    public string? Language { get; init; }
    public string? Name { get; init; }
    public bool IsDefault { get; init; }
    
    // Audio-specific
    public int SampleRate { get; init; }
    public int ChannelCount { get; init; }
    public int BitsPerSample { get; init; }
    
    // Video-specific
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public (int num, int den) PixelAspectRatio { get; init; }
}

/// <summary>
/// Sample data to write to the muxer.
/// </summary>
public readonly struct MuxerSample
{
    public int TrackId { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
    public long PresentationTimestamp { get; init; } // In microseconds
    public long DecodingTimestamp { get; init; } // In microseconds
    public long Duration { get; init; } // In microseconds
    public bool IsKeyframe { get; init; }
    
    public static MuxerSample Create(int trackId, ReadOnlyMemory<byte> data, 
        long pts, bool isKeyframe = false, long duration = 0, long dts = -1)
    {
        return new MuxerSample
        {
            TrackId = trackId,
            Data = data,
            PresentationTimestamp = pts,
            DecodingTimestamp = dts >= 0 ? dts : pts,
            Duration = duration,
            IsKeyframe = isKeyframe
        };
    }
}

/// <summary>
/// Abstract base class for all muxers.
/// </summary>
public abstract class Muxer : IDisposable
{
    protected readonly Stream _outputStream;
    protected readonly MuxerConfiguration _config;
    protected readonly List<MuxerTrackInfo> _tracks = new();
    protected bool _headerWritten;
    protected bool _isDisposed;
    
    protected Muxer(Stream outputStream, MuxerConfiguration config)
    {
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        
        if (!_outputStream.CanWrite)
            throw new ArgumentException("Output stream must be writable", nameof(outputStream));
    }
    
    /// <summary>
    /// Gets the output format of this muxer.
    /// </summary>
    public abstract MuxerFormat Format { get; }
    
    /// <summary>
    /// Gets whether the header has been written.
    /// </summary>
    public bool HeaderWritten => _headerWritten;
    
    /// <summary>
    /// Gets the current position in the output stream.
    /// </summary>
    public long Position => _outputStream.Position;
    
    /// <summary>
    /// Add a track to the muxer.
    /// </summary>
    public virtual int AddTrack(MuxerTrackInfo track)
    {
        if (_headerWritten)
            throw new InvalidOperationException("Cannot add tracks after header is written");
        
        var newTrack = track with { TrackId = _tracks.Count + 1 };
        _tracks.Add(newTrack);
        return newTrack.TrackId;
    }
    
    /// <summary>
    /// Write the container header. Must be called after all tracks are added.
    /// </summary>
    public abstract ValueTask WriteHeaderAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Write a sample to the specified track.
    /// </summary>
    public abstract ValueTask WriteSampleAsync(MuxerSample sample, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Write the container footer/trailer and finalize the file.
    /// </summary>
    public abstract ValueTask WriteTrailerAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Flush any buffered data to the output stream.
    /// </summary>
    public virtual async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _outputStream.FlushAsync(cancellationToken);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _outputStream.Dispose();
            }
            _isDisposed = true;
        }
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Factory for creating muxers.
/// </summary>
public static class MuxerFactory
{
    /// <summary>
    /// Create a muxer for the specified format.
    /// </summary>
    public static Muxer Create(Stream outputStream, MuxerConfiguration config)
    {
        return config.Format switch
        {
            MuxerFormat.Wav => new WavMuxer(outputStream, config),
            MuxerFormat.Avi => new AviMuxer(outputStream, config),
            MuxerFormat.MpJpeg => new MpJpegMuxer(outputStream, config),
            MuxerFormat.WebVtt => new WebVttMuxer(outputStream, config),
            _ => throw new NotSupportedException($"Muxer format not implemented: {config.Format}")
        };
    }
    
    /// <summary>
    /// Create a muxer and open a file for writing.
    /// </summary>
    public static Muxer CreateFile(string path, MuxerConfiguration config)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        return Create(stream, config);
    }
    
    /// <summary>
    /// Get the file extension for a muxer format.
    /// </summary>
    public static string GetFileExtension(MuxerFormat format)
    {
        return format switch
        {
            MuxerFormat.Mp4 => ".mp4",
            MuxerFormat.Matroska => ".mkv",
            MuxerFormat.Avi => ".avi",
            MuxerFormat.Wav => ".wav",
            MuxerFormat.Ogg => ".ogg",
            MuxerFormat.MpegTs => ".ts",
            MuxerFormat.Flv => ".flv",
            MuxerFormat.MpJpeg => ".mjpeg",
            MuxerFormat.WebVtt => ".vtt",
            _ => ".bin"
        };
    }
}

/// <summary>
/// WAV file muxer - audio only container.
/// </summary>
public sealed class WavMuxer : Muxer
{
    private long _dataChunkPosition;
    private long _totalSamplesWritten;
    private int _audioTrackId = -1;
    private MuxerTrackInfo _audioTrack;
    
    public WavMuxer(Stream outputStream, MuxerConfiguration config) 
        : base(outputStream, config)
    {
    }
    
    public override MuxerFormat Format => MuxerFormat.Wav;
    
    public override int AddTrack(MuxerTrackInfo track)
    {
        if (track.TrackType != MuxerTrackType.Audio)
            throw new NotSupportedException("WAV muxer only supports audio tracks");
        
        if (_audioTrackId >= 0)
            throw new InvalidOperationException("WAV muxer only supports a single audio track");
        
        _audioTrack = track;
        _audioTrackId = base.AddTrack(track);
        return _audioTrackId;
    }
    
    public override async ValueTask WriteHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (_audioTrackId < 0)
            throw new InvalidOperationException("No audio track added");
        
        if (_headerWritten)
            return;
        
        // Write RIFF header
        byte[] header = new byte[44];
        
        // "RIFF" chunk
        header[0] = (byte)'R';
        header[1] = (byte)'I';
        header[2] = (byte)'F';
        header[3] = (byte)'F';
        // File size (placeholder - updated in trailer)
        WriteUInt32LE(header, 4, 0);
        // "WAVE"
        header[8] = (byte)'W';
        header[9] = (byte)'A';
        header[10] = (byte)'V';
        header[11] = (byte)'E';
        
        // "fmt " sub-chunk
        header[12] = (byte)'f';
        header[13] = (byte)'m';
        header[14] = (byte)'t';
        header[15] = (byte)' ';
        // Sub-chunk size (16 for PCM)
        WriteUInt32LE(header, 16, 16);
        // Audio format (1 = PCM, 3 = IEEE float)
        ushort audioFormat = _audioTrack.BitsPerSample == 32 ? (ushort)3 : (ushort)1;
        WriteUInt16LE(header, 20, audioFormat);
        // Number of channels
        WriteUInt16LE(header, 22, (ushort)_audioTrack.ChannelCount);
        // Sample rate
        WriteUInt32LE(header, 24, (uint)_audioTrack.SampleRate);
        // Byte rate
        int bytesPerSample = _audioTrack.BitsPerSample / 8;
        uint byteRate = (uint)(_audioTrack.SampleRate * _audioTrack.ChannelCount * bytesPerSample);
        WriteUInt32LE(header, 28, byteRate);
        // Block align
        ushort blockAlign = (ushort)(_audioTrack.ChannelCount * bytesPerSample);
        WriteUInt16LE(header, 32, blockAlign);
        // Bits per sample
        WriteUInt16LE(header, 34, (ushort)_audioTrack.BitsPerSample);
        
        // "data" sub-chunk
        header[36] = (byte)'d';
        header[37] = (byte)'a';
        header[38] = (byte)'t';
        header[39] = (byte)'a';
        // Data size (placeholder - updated in trailer)
        WriteUInt32LE(header, 40, 0);
        
        await _outputStream.WriteAsync(header, cancellationToken);
        _dataChunkPosition = _outputStream.Position;
        _headerWritten = true;
    }
    
    public override async ValueTask WriteSampleAsync(MuxerSample sample, CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            throw new InvalidOperationException("Header must be written before samples");
        
        if (sample.TrackId != _audioTrackId)
            throw new ArgumentException($"Unknown track ID: {sample.TrackId}");
        
        await _outputStream.WriteAsync(sample.Data, cancellationToken);
        _totalSamplesWritten += sample.Data.Length;
    }
    
    public override async ValueTask WriteTrailerAsync(CancellationToken cancellationToken = default)
    {
        if (!_headerWritten)
            return;
        
        // Seek back and update sizes if possible
        if (_outputStream.CanSeek)
        {
            long currentPosition = _outputStream.Position;
            
            // Update data chunk size
            _outputStream.Position = _dataChunkPosition - 4;
            byte[] sizeBytes = new byte[4];
            WriteUInt32LE(sizeBytes, 0, (uint)_totalSamplesWritten);
            await _outputStream.WriteAsync(sizeBytes, cancellationToken);
            
            // Update RIFF chunk size (file size - 8)
            _outputStream.Position = 4;
            WriteUInt32LE(sizeBytes, 0, (uint)(currentPosition - 8));
            await _outputStream.WriteAsync(sizeBytes, cancellationToken);
            
            _outputStream.Position = currentPosition;
        }
        
        await FlushAsync(cancellationToken);
    }
    
    private static void WriteUInt16LE(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
    
    private static void WriteUInt32LE(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

/// <summary>
/// Transcoding pipeline that reads from a demuxer and writes to a muxer.
/// </summary>
public sealed class RemuxPipeline : IDisposable
{
    private readonly Muxer _muxer;
    private readonly Dictionary<int, int> _trackMapping = new();
    private bool _isDisposed;
    
    public RemuxPipeline(Muxer muxer)
    {
        _muxer = muxer ?? throw new ArgumentNullException(nameof(muxer));
    }
    
    /// <summary>
    /// Map a source track to a destination track.
    /// </summary>
    public void MapTrack(int sourceTrackId, MuxerTrackInfo trackInfo)
    {
        int destTrackId = _muxer.AddTrack(trackInfo);
        _trackMapping[sourceTrackId] = destTrackId;
    }
    
    /// <summary>
    /// Start the remuxing process.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _muxer.WriteHeaderAsync(cancellationToken);
    }
    
    /// <summary>
    /// Write a packet from the source to the destination.
    /// </summary>
    public async ValueTask WritePacketAsync(int sourceTrackId, ReadOnlyMemory<byte> data,
        long pts, long dts, bool isKeyframe, CancellationToken cancellationToken = default)
    {
        if (!_trackMapping.TryGetValue(sourceTrackId, out int destTrackId))
            return; // Track not mapped, skip
        
        var sample = MuxerSample.Create(destTrackId, data, pts, isKeyframe, 0, dts);
        await _muxer.WriteSampleAsync(sample, cancellationToken);
    }
    
    /// <summary>
    /// Finish the remuxing process.
    /// </summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken = default)
    {
        await _muxer.WriteTrailerAsync(cancellationToken);
    }
    
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _muxer.Dispose();
            _isDisposed = true;
        }
    }
}

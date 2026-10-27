// MediaKernel - Audio Encoders for Ripping
// FLAC, ALAC, MP3, AAC, Opus, Vorbis encoders

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MediaKernel.Core.Codecs.Audio.Flac;
using MediaKernel.Core.Codecs.MP3.Encoder;
using MediaKernel.Core.Muxers;

namespace MediaKernel.Core.Ripping;

/// <summary>
/// Base interface for audio encoders.
/// </summary>
public interface IAudioEncoder : IAsyncDisposable
{
    /// <summary>Encoder name.</summary>
    string Name { get; }
    
    /// <summary>File extension (without dot).</summary>
    string Extension { get; }
    
    /// <summary>Whether this is a lossless encoder.</summary>
    bool IsLossless { get; }
    
    /// <summary>Initialize the encoder with output stream.</summary>
    Task InitializeAsync(Stream output, AudioEncoderSettings settings, 
        CancellationToken cancellationToken = default);
    
    /// <summary>Encode audio samples.</summary>
    Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples, CancellationToken cancellationToken = default);
    
    /// <summary>Finalize the encoding and write trailer.</summary>
    Task FinalizeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Audio encoder settings.
/// </summary>
public sealed class AudioEncoderSettings
{
    public int SampleRate { get; init; } = 44100;
    public int Channels { get; init; } = 2;
    public int BitsPerSample { get; init; } = 16;
    
    // Lossy encoder settings
    public int Bitrate { get; init; } = 320; // kbps for lossy
    public EncoderQuality Quality { get; init; } = EncoderQuality.High;
    public bool UseVbr { get; init; } = true;
    
    // FLAC settings
    public int FlacCompressionLevel { get; init; } = 5; // 0-8
    
    // Metadata
    public AudioMetadata? Metadata { get; init; }
}

/// <summary>
/// Encoder quality preset.
/// </summary>
public enum EncoderQuality
{
    Low,      // Fast encoding, smaller file
    Medium,
    High,     // Best quality/speed balance
    Maximum   // Slowest, best quality
}

/// <summary>
/// Audio metadata for embedding in encoded files.
/// </summary>
public sealed class AudioMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }
    public string? Comment { get; init; }
    public string? Composer { get; init; }
    public byte[]? CoverArt { get; init; }
    public string? CoverArtMimeType { get; init; }
    
    // ReplayGain
    public double? TrackGain { get; init; } // dB
    public double? TrackPeak { get; init; }
    public double? AlbumGain { get; init; } // dB
    public double? AlbumPeak { get; init; }
}

// ============================================================================
// FLAC Encoder
// ============================================================================

/// <summary>
/// FLAC (Free Lossless Audio Codec) encoder.
/// </summary>
/// <remarks>
/// The encoder itself is <see cref="FlacEnc"/>, a translation of FFmpeg's <c>flacenc.c</c> that
/// the <c>flacenc</c> gate holds byte-identical to <c>ffmpeg -c:a flac</c> over 42 cases -
/// every compression level, 16-, 24- and 32-bit input, every stereo mode - and round-trips
/// back through ffmpeg's decoder unchanged. This class is the thin part: it writes the metadata
/// blocks, splits the caller's interleaved bytes into blocks of the encoder's frame size, and
/// rewrites STREAMINFO at the end with the real frame sizes and MD5.
///
/// Until 2026-10-19 this class wrote verbatim subframes, left STREAMINFO's sizes and MD5 at
/// zero, and threw from <c>InitializeAsync</c> rather than produce a file nobody had compared
/// against anything.
/// </remarks>
public sealed class FlacEncoder : IAudioEncoder
{
    private Stream? output;
    private AudioEncoderSettings settings = new();
    private FlacEnc? encoder;
    private long streamInfoPosition = -1;

    // Interleaved input, buffered until a whole block of FrameSize samples is available.
    private byte[]? pending;
    private int pendingBytes;
    private short[]? block16;
    private int[]? block32;
    private byte[]? packet;

    private bool initialized;
    private bool finalized;

    public string Name => "FLAC";
    public string Extension => "flac";
    public bool IsLossless => true;

    public async Task InitializeAsync(Stream output, AudioEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.BitsPerSample != 16 && settings.BitsPerSample != 24)
        {
            throw new NotSupportedException(
                $"FLAC input must be 16- or 24-bit; got {settings.BitsPerSample}.");
        }

        this.output = output;
        this.settings = settings;

        encoder = new FlacEnc(new FlacCompressionOptions
        {
            CompressionLevel = Math.Clamp(settings.FlacCompressionLevel, 0, 12),
        });
        if (settings.BitsPerSample == 16)
            encoder.Init(settings.SampleRate, settings.Channels, FlacSampleFormat.S16);
        else
            encoder.Init(settings.SampleRate, settings.Channels, FlacSampleFormat.S32, 24);

        int bytesPerFrame = settings.Channels * (settings.BitsPerSample / 8);
        pending = new byte[encoder.FrameSize * bytesPerFrame];
        pendingBytes = 0;
        if (settings.BitsPerSample == 16)
            block16 = new short[encoder.FrameSize * settings.Channels];
        else
            block32 = new int[encoder.FrameSize * settings.Channels];
        packet = new byte[encoder.MaxFramesize + 64];

        await output.WriteAsync("fLaC"u8.ToArray(), cancellationToken);

        // STREAMINFO is written twice: a placeholder now, and the real thing from the encoder
        // once the stream is complete, which is where the MD5 and the frame sizes come from.
        bool haveComment = settings.Metadata != null;
        bool havePicture = settings.Metadata?.CoverArt != null;

        streamInfoPosition = output.Position;
        var header = new byte[4];
        header[0] = (byte)(haveComment || havePicture ? 0x00 : 0x80);
        header[1] = 0;
        header[2] = 0;
        header[3] = 34;
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(encoder.Extradata, cancellationToken);

        if (haveComment)
            await WriteVorbisCommentBlockAsync(settings.Metadata!, !havePicture, cancellationToken);
        if (havePicture)
        {
            await WritePictureBlockAsync(settings.Metadata!.CoverArt!,
                settings.Metadata.CoverArtMimeType, cancellationToken);
        }

        initialized = true;
    }

    private async Task WriteVorbisCommentBlockAsync(AudioMetadata meta, bool isLast,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);

        // Vendor string, then the comments; both counts are little-endian, which is the one
        // place a FLAC file is not big-endian.
        byte[] vendor = Encoding.UTF8.GetBytes("MediaKernel FLAC Encoder");
        writer.Write((uint)vendor.Length);
        writer.Write(vendor);

        var comments = new List<string>();

        if (!string.IsNullOrEmpty(meta.Title))
            comments.Add($"TITLE={meta.Title}");
        if (!string.IsNullOrEmpty(meta.Artist))
            comments.Add($"ARTIST={meta.Artist}");
        if (!string.IsNullOrEmpty(meta.Album))
            comments.Add($"ALBUM={meta.Album}");
        if (!string.IsNullOrEmpty(meta.AlbumArtist))
            comments.Add($"ALBUMARTIST={meta.AlbumArtist}");
        if (meta.TrackNumber.HasValue)
            comments.Add($"TRACKNUMBER={meta.TrackNumber}");
        if (meta.TrackCount.HasValue)
            comments.Add($"TRACKTOTAL={meta.TrackCount}");
        if (meta.DiscNumber.HasValue)
            comments.Add($"DISCNUMBER={meta.DiscNumber}");
        if (meta.Year.HasValue)
            comments.Add($"DATE={meta.Year}");
        if (!string.IsNullOrEmpty(meta.Genre))
            comments.Add($"GENRE={meta.Genre}");
        if (!string.IsNullOrEmpty(meta.Comment))
            comments.Add($"COMMENT={meta.Comment}");

        if (meta.TrackGain.HasValue)
            comments.Add($"REPLAYGAIN_TRACK_GAIN={meta.TrackGain:F2} dB");
        if (meta.TrackPeak.HasValue)
            comments.Add($"REPLAYGAIN_TRACK_PEAK={meta.TrackPeak:F6}");
        if (meta.AlbumGain.HasValue)
            comments.Add($"REPLAYGAIN_ALBUM_GAIN={meta.AlbumGain:F2} dB");
        if (meta.AlbumPeak.HasValue)
            comments.Add($"REPLAYGAIN_ALBUM_PEAK={meta.AlbumPeak:F6}");

        writer.Write((uint)comments.Count);

        foreach (string comment in comments)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(comment);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        byte[] commentData = ms.ToArray();

        byte[] header = new byte[4];
        header[0] = (byte)(isLast ? 0x84 : 0x04);   // VORBIS_COMMENT
        header[1] = (byte)(commentData.Length >> 16);
        header[2] = (byte)(commentData.Length >> 8);
        header[3] = (byte)commentData.Length;

        await output!.WriteAsync(header, cancellationToken);
        await output.WriteAsync(commentData, cancellationToken);
    }

    private async Task WritePictureBlockAsync(byte[] pictureData, string? mimeType,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteUInt32BE(ms, 3);                        // picture type: front cover

        string mime = mimeType ?? "image/jpeg";
        WriteUInt32BE(ms, (uint)mime.Length);
        writer.Write(Encoding.ASCII.GetBytes(mime));

        WriteUInt32BE(ms, 0);                        // description
        WriteUInt32BE(ms, 0);                        // width
        WriteUInt32BE(ms, 0);                        // height
        WriteUInt32BE(ms, 0);                        // colour depth
        WriteUInt32BE(ms, 0);                        // indexed colours

        WriteUInt32BE(ms, (uint)pictureData.Length);
        writer.Write(pictureData);

        byte[] blockData = ms.ToArray();

        byte[] header = new byte[4];
        header[0] = 0x86;                            // last block, PICTURE
        header[1] = (byte)(blockData.Length >> 16);
        header[2] = (byte)(blockData.Length >> 8);
        header[3] = (byte)blockData.Length;

        await output!.WriteAsync(header, cancellationToken);
        await output.WriteAsync(blockData, cancellationToken);
    }

    public async Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples,
        CancellationToken cancellationToken = default)
    {
        if (!initialized || finalized || output == null || encoder == null || pending == null)
            throw new InvalidOperationException("The FLAC encoder is not initialised.");

        int offset = 0;
        while (offset < samples.Length)
        {
            int toCopy = Math.Min(samples.Length - offset, pending.Length - pendingBytes);
            samples.Slice(offset, toCopy).CopyTo(pending.AsMemory(pendingBytes));
            pendingBytes += toCopy;
            offset += toCopy;

            if (pendingBytes == pending.Length)
            {
                await EncodeBufferedAsync(encoder.FrameSize, cancellationToken);
                pendingBytes = 0;
            }
        }
    }

    /// <summary>Encodes the first <paramref name="sampleCount"/> frames of the pending buffer.</summary>
    private async Task EncodeBufferedAsync(int sampleCount, CancellationToken cancellationToken)
    {
        int values = sampleCount * settings.Channels;
        int bytes;

        if (settings.BitsPerSample == 16)
        {
            for (int i = 0; i < values; i++)
                block16![i] = (short)(pending![2 * i] | (pending[2 * i + 1] << 8));
            bytes = encoder!.EncodeFrame(block16.AsSpan(0, values), sampleCount, packet!);
        }
        else
        {
            // 24-bit little-endian in, left-aligned in 32 bits out, which is what libavcodec
            // hands flacenc and what the encoder's S32 path expects.
            for (int i = 0; i < values; i++)
            {
                block32![i] = (pending![3 * i] << 8) | (pending[3 * i + 1] << 16)
                            | (pending[3 * i + 2] << 24);
            }
            bytes = encoder!.EncodeFrame(block32.AsSpan(0, values), sampleCount, packet!);
        }

        await output!.WriteAsync(packet.AsMemory(0, bytes), cancellationToken);
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (!initialized || finalized || output == null || encoder == null) return;

        int bytesPerFrame = settings.Channels * (settings.BitsPerSample / 8);
        int remaining = pendingBytes / bytesPerFrame;
        if (remaining > 0)
            await EncodeBufferedAsync(remaining, cancellationToken);
        pendingBytes = 0;

        encoder.Finish();

        // STREAMINFO is only right once the stream is complete: it carries the total sample
        // count, the smallest and largest frame actually written, and the MD5 of the input.
        if (output.CanSeek && streamInfoPosition >= 0)
        {
            long resume = output.Position;
            output.Position = streamInfoPosition + 4;
            await output.WriteAsync(encoder.Extradata, cancellationToken);
            output.Position = resume;
        }

        encoder.Close();
        finalized = true;
    }

    private static void WriteUInt32BE(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    public ValueTask DisposeAsync()
    {
        encoder?.Close();
        return ValueTask.CompletedTask;
    }
}

// ============================================================================
// MP3 Encoder (using LAME-style encoding)
// ============================================================================

/// <summary>
/// MP3 encoder using LAME algorithm principles.
/// </summary>
/// <remarks>
/// The encoder itself is <see cref="LameApi"/>, a
/// translation of lame-3.100 that the <c>mp3enc-bitexact</c> gate holds to byte equality with
/// ffmpeg's libmp3lame over 54 combinations of sample rate, bitrate and channel mode. This
/// class is the thin part: it writes the ID3v2 tag, splits the caller's interleaved PCM into
/// the planar channels LAME wants, and drives the buffer and flush calls.
///
/// Until 2026-10-17 this class wrote a correct frame header and filled the frame with zeros,
/// so it produced a well-formed, correctly tagged MP3 of exactly the right duration that
/// played silence. Nothing in Ripping was gated, which is why that survived.
/// </remarks>
public sealed class Mp3Encoder : IAudioEncoder
{
    private Stream? output;
    private AudioEncoderSettings settings = new();
    private bool initialized;
    private bool finalized;

    private LameGlobalFlags? gfp;
    private int channels = 2;
    private long encodedSamples;
    private short[]? left;
    private short[]? right;
    private byte[]? mp3Buffer;
    private readonly byte[] carry = new byte[8];
    private int carryLength;

    /// <summary>How many sample frames have gone into the encoder.</summary>
    public long EncodedSamples => encodedSamples;

    public string Name => "MP3 (LAME)";
    public string Extension => "mp3";
    public bool IsLossless => false;

    public async Task InitializeAsync(Stream output, AudioEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.BitsPerSample != 16)
            throw new NotSupportedException(
                $"MP3 encoder: {settings.BitsPerSample}-bit input is not supported; LAME's "
              + "entry point here takes 16-bit PCM.");
        if (settings.Channels is < 1 or > 2)
            throw new NotSupportedException(
                $"MP3 encoder: {settings.Channels} channels; MP3 carries one or two.");

        this.output = output;
        this.settings = settings;
        channels = settings.Channels;

        // Write ID3v2 header if metadata present
        if (settings.Metadata != null)
        {
            await WriteId3v2HeaderAsync(settings.Metadata, cancellationToken);
        }

        gfp = LameApi.LameInit()
            ?? throw new InvalidOperationException("MP3 encoder: lame_init failed.");

        LameSetGet.SetNumChannels(gfp, channels);
        LameSetGet.SetMode(gfp, channels > 1
            ? MpegMode.JointStereo
            : MpegMode.Mono);
        LameSetGet.SetInSamplerate(gfp, settings.SampleRate);
        LameSetGet.SetOutSamplerate(gfp, settings.SampleRate);
        LameSetGet.SetBrate(gfp, settings.Bitrate);

        // The Xing tag is the muxer's business, and nothing here writes one.
        LameSetGet.SetWriteVbrTag(gfp, 0);
        LameSetGet.SetDisableReservoir(gfp, 0);
        LameSetGet.SetCopyright(gfp, 0);
        LameSetGet.SetOriginal(gfp, 1);

        int rc = LameApi.LameInitParams(gfp);
        if (rc < 0)
            throw new InvalidOperationException(
                $"MP3 encoder: lame_init_params rejected {settings.SampleRate} Hz, "
              + $"{channels} channel(s) at {settings.Bitrate} kbps (returned {rc}).");

        initialized = true;
    }
    
    private async Task WriteId3v2HeaderAsync(AudioMetadata meta, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        
        // ID3v2.4 header
        ms.Write("ID3"u8);
        ms.WriteByte(4); // Version 2.4
        ms.WriteByte(0); // Revision
        ms.WriteByte(0); // Flags
        
        // Placeholder for size (syncsafe integer)
        long sizePos = ms.Position;
        ms.Write(new byte[4]);
        
        // Write frames
        void WriteTextFrame(string frameId, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            
            byte[] text = Encoding.UTF8.GetBytes(value);
            ms.Write(Encoding.ASCII.GetBytes(frameId));
            
            // Size (syncsafe)
            int size = text.Length + 1; // +1 for encoding byte
            ms.WriteByte((byte)((size >> 21) & 0x7F));
            ms.WriteByte((byte)((size >> 14) & 0x7F));
            ms.WriteByte((byte)((size >> 7) & 0x7F));
            ms.WriteByte((byte)(size & 0x7F));
            
            ms.WriteByte(0); // Flags
            ms.WriteByte(0);
            
            ms.WriteByte(3); // UTF-8 encoding
            ms.Write(text);
        }
        
        WriteTextFrame("TIT2", meta.Title);
        WriteTextFrame("TPE1", meta.Artist);
        WriteTextFrame("TALB", meta.Album);
        WriteTextFrame("TPE2", meta.AlbumArtist);
        WriteTextFrame("TCON", meta.Genre);
        
        if (meta.TrackNumber.HasValue)
        {
            string track = meta.TrackCount.HasValue 
                ? $"{meta.TrackNumber}/{meta.TrackCount}"
                : meta.TrackNumber.ToString()!;
            WriteTextFrame("TRCK", track);
        }
        
        if (meta.Year.HasValue)
            WriteTextFrame("TYER", meta.Year.ToString());
        
        // Write cover art (APIC frame)
        if (meta.CoverArt != null)
        {
            ms.Write("APIC"u8);
            
            string mime = meta.CoverArtMimeType ?? "image/jpeg";
            byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);
            int apicSize = 1 + mimeBytes.Length + 1 + 1 + 1 + meta.CoverArt.Length;
            
            ms.WriteByte((byte)((apicSize >> 21) & 0x7F));
            ms.WriteByte((byte)((apicSize >> 14) & 0x7F));
            ms.WriteByte((byte)((apicSize >> 7) & 0x7F));
            ms.WriteByte((byte)(apicSize & 0x7F));
            
            ms.WriteByte(0); // Flags
            ms.WriteByte(0);
            
            ms.WriteByte(0); // Text encoding (ISO-8859-1)
            ms.Write(mimeBytes);
            ms.WriteByte(0); // Null terminator
            ms.WriteByte(3); // Picture type (front cover)
            ms.WriteByte(0); // Empty description
            ms.Write(meta.CoverArt);
        }
        
        // Update size
        long tagSize = ms.Position - 10;
        ms.Position = sizePos;
        ms.WriteByte((byte)((tagSize >> 21) & 0x7F));
        ms.WriteByte((byte)((tagSize >> 14) & 0x7F));
        ms.WriteByte((byte)((tagSize >> 7) & 0x7F));
        ms.WriteByte((byte)(tagSize & 0x7F));
        
        await output!.WriteAsync(ms.ToArray(), cancellationToken);
    }
    
    /// <summary>
    /// Encodes interleaved 16-bit PCM through the ported LAME.
    /// </summary>
    /// <remarks>
    /// The caller hands over interleaved bytes; LAME wants planar <c>short</c> per channel,
    /// so they are split here. Whole samples only: a trailing partial sample is kept and
    /// prepended to the next call, because splitting one across two calls would shift every
    /// sample after it by a byte.
    /// </remarks>
    public async Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples,
        CancellationToken cancellationToken = default)
    {
        if (!initialized || output is null || gfp is null)
            throw new InvalidOperationException(
                "MP3 encoder: EncodeSamplesAsync before InitializeAsync.");
        if (finalized)
            throw new InvalidOperationException(
                "MP3 encoder: EncodeSamplesAsync after FinalizeAsync.");

        ReadOnlyMemory<byte> input = samples;
        int bytesPerFrame = 2 * channels;

        if (carryLength > 0)
        {
            int wanted = Math.Min(bytesPerFrame - carryLength, input.Length);
            input.Span[..wanted].CopyTo(carry.AsSpan(carryLength));
            carryLength += wanted;
            input = input[wanted..];
            if (carryLength < bytesPerFrame) return;
            await EncodeInterleavedAsync(carry.AsMemory(0, bytesPerFrame), cancellationToken);
            carryLength = 0;
        }

        int whole = input.Length / bytesPerFrame * bytesPerFrame;
        if (whole > 0)
            await EncodeInterleavedAsync(input[..whole], cancellationToken);

        int rest = input.Length - whole;
        if (rest > 0)
        {
            input.Span[whole..].CopyTo(carry);
            carryLength = rest;
        }
    }

    private async Task EncodeInterleavedAsync(
        ReadOnlyMemory<byte> interleavedMemory, CancellationToken cancellationToken)
    {
        int count = interleavedMemory.Length / (2 * channels);
        if (count == 0) return;

        if (left == null || left.Length < count)
        {
            left = new short[count];
            right = new short[count];
        }

        // LAME's own worst case: 1.25 bytes a sample plus 7200 for the reservoir and tags.
        int needed = 1 + (int)(1.25 * count) + 7200;
        if (mp3Buffer == null || mp3Buffer.Length < needed) mp3Buffer = new byte[needed];

        int written = Deinterleave(interleavedMemory.Span, count);

        if (written < 0)
            throw new InvalidOperationException(
                $"MP3 encoder: lame_encode_buffer returned {written}.");

        if (written > 0)
            await output!.WriteAsync(mp3Buffer.AsMemory(0, written), cancellationToken);

        encodedSamples += count;
    }

    /// <summary>Splits interleaved PCM into planar channels and encodes one buffer.</summary>
    /// <remarks>
    /// Separated from the async method only because a <c>Span</c> cannot cross an await.
    /// </remarks>
    private int Deinterleave(ReadOnlySpan<byte> interleaved, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 2 * channels;
            left![i] = (short)(interleaved[o] | (interleaved[o + 1] << 8));
            if (channels == 2)
                right![i] = (short)(interleaved[o + 2] | (interleaved[o + 3] << 8));
        }

        return LameApi.LameEncodeBuffer(
            gfp!, left.AsSpan(0, count),
            channels == 2 ? right!.AsSpan(0, count) : default,
            count, mp3Buffer!, mp3Buffer!.Length);
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (finalized) return;
        if (!initialized || gfp is null || output is null)
            throw new InvalidOperationException(
                "MP3 encoder: FinalizeAsync before InitializeAsync.");
        finalized = true;

        // A partial sample at the end cannot be encoded and would corrupt the channel
        // interleaving if it were; it is dropped, which costs at most one sample frame.
        carryLength = 0;

        mp3Buffer ??= new byte[7200];
        int written = LameApi.LameEncodeFlush(gfp, mp3Buffer, mp3Buffer.Length);
        if (written < 0)
            throw new InvalidOperationException(
                $"MP3 encoder: lame_encode_flush returned {written}.");
        if (written > 0)
            await output.WriteAsync(mp3Buffer.AsMemory(0, written), cancellationToken);

        LameApi.LameClose(gfp);
        gfp = null;
    }

    public ValueTask DisposeAsync()
    {
        if (gfp != null)
        {
            LameApi.LameClose(gfp);
            gfp = null;
        }
        return ValueTask.CompletedTask;
    }
}

// ============================================================================
// Opus Encoder
// ============================================================================

/// <summary>
/// Opus encoder for high-quality audio at low bitrates.
/// </summary>
public sealed class OpusEncoder : IAudioEncoder
{
    private Stream? output;
    private AudioEncoderSettings settings = new();
    private bool initialized;
    private bool finalized;
    private uint granulePosition;
    private uint serialNumber;
    private uint pageNumber;
    
    public string Name => "Opus";
    public string Extension => "opus";
    public bool IsLossless => false;
    
    public async Task InitializeAsync(Stream output, AudioEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        // Until this encoder is gated against FFmpeg it refuses to run. A file that has the
        // right length, the right header and the right tag but the wrong audio is worse than
        // an exception: it is indistinguishable from a good one until somebody listens. That
        // is exactly how the MP3 stub survived. See the project notes, Phase 7.
        throw new NotImplementedException(
            "The Opus encoder is a stub: it writes OpusHead and OpusTags and then a hundred zero bytes per frame. The only encoder in this file that works is "
          + "Mp3Encoder, which the ripping-mp3 gate holds byte-identical to libmp3lame.");

        this.output = output;
        this.settings = settings;
        serialNumber = (uint)Random.Shared.Next();
        
        // Write Ogg header page with OpusHead
        await WriteOpusHeadAsync(cancellationToken);
        
        // Write Ogg comment page with OpusTags
        await WriteOpusTagsAsync(settings.Metadata, cancellationToken);
        
        initialized = true;
    }
    
    private async Task WriteOpusHeadAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        
        // OpusHead packet
        ms.Write("OpusHead"u8);
        ms.WriteByte(1); // Version
        ms.WriteByte((byte)settings.Channels);
        
        // Pre-skip (encoder delay)
        ms.WriteByte(0);
        ms.WriteByte(0);
        
        // Input sample rate (little-endian)
        ms.WriteByte((byte)settings.SampleRate);
        ms.WriteByte((byte)(settings.SampleRate >> 8));
        ms.WriteByte((byte)(settings.SampleRate >> 16));
        ms.WriteByte((byte)(settings.SampleRate >> 24));
        
        // Output gain (0 dB)
        ms.WriteByte(0);
        ms.WriteByte(0);
        
        // Channel mapping family
        ms.WriteByte(0);
        
        byte[] opusHead = ms.ToArray();
        
        // Wrap in Ogg page
        await WriteOggPageAsync(opusHead, true, false, 0, cancellationToken);
        pageNumber++;
    }
    
    private async Task WriteOpusTagsAsync(AudioMetadata? meta, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        
        ms.Write("OpusTags"u8);
        
        // Vendor string
        string vendor = "MediaKernel Opus Encoder";
        writer.Write((uint)vendor.Length);
        writer.Write(Encoding.UTF8.GetBytes(vendor));
        
        // Comments
        var comments = new List<string>();
        
        if (meta != null)
        {
            if (!string.IsNullOrEmpty(meta.Title))
                comments.Add($"TITLE={meta.Title}");
            if (!string.IsNullOrEmpty(meta.Artist))
                comments.Add($"ARTIST={meta.Artist}");
            if (!string.IsNullOrEmpty(meta.Album))
                comments.Add($"ALBUM={meta.Album}");
            if (meta.TrackNumber.HasValue)
                comments.Add($"TRACKNUMBER={meta.TrackNumber}");
            if (meta.Year.HasValue)
                comments.Add($"DATE={meta.Year}");
            if (!string.IsNullOrEmpty(meta.Genre))
                comments.Add($"GENRE={meta.Genre}");
        }
        
        writer.Write((uint)comments.Count);
        foreach (string comment in comments)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(comment);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }
        
        byte[] opusTags = ms.ToArray();
        
        // Wrap in Ogg page
        await WriteOggPageAsync(opusTags, false, false, 0, cancellationToken);
        pageNumber++;
    }
    
    private async Task WriteOggPageAsync(byte[] data, bool bos, bool eos, ulong granule,
        CancellationToken cancellationToken)
    {
        using var page = new MemoryStream();
        
        // Ogg page header
        page.Write("OggS"u8);
        page.WriteByte(0); // Version
        
        byte headerType = 0;
        if (bos) headerType |= 0x02;
        if (eos) headerType |= 0x04;
        page.WriteByte(headerType);
        
        // Granule position (little-endian)
        for (int i = 0; i < 8; i++)
            page.WriteByte((byte)(granule >> (i * 8)));
        
        // Serial number
        for (int i = 0; i < 4; i++)
            page.WriteByte((byte)(serialNumber >> (i * 8)));
        
        // Page number
        for (int i = 0; i < 4; i++)
            page.WriteByte((byte)(pageNumber >> (i * 8)));
        
        // CRC placeholder
        long crcPos = page.Position;
        page.Write(new byte[4]);
        
        // Segment table
        int segments = (data.Length + 254) / 255;
        page.WriteByte((byte)segments);
        
        int remaining = data.Length;
        for (int i = 0; i < segments; i++)
        {
            page.WriteByte((byte)Math.Min(255, remaining));
            remaining -= 255;
        }
        
        // Data
        page.Write(data);
        
        // Calculate and write CRC
        byte[] pageData = page.ToArray();
        uint crc = CalculateOggCrc(pageData);
        pageData[crcPos] = (byte)crc;
        pageData[crcPos + 1] = (byte)(crc >> 8);
        pageData[crcPos + 2] = (byte)(crc >> 16);
        pageData[crcPos + 3] = (byte)(crc >> 24);
        
        await output!.WriteAsync(pageData, cancellationToken);
    }
    
    private static uint CalculateOggCrc(byte[] data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ OggCrcTable[(crc >> 24) ^ b];
        }
        return crc;
    }
    
    private static readonly uint[] OggCrcTable = GenerateOggCrcTable();
    
    private static uint[] GenerateOggCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i << 24;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04C11DB7u : 0);
            }
            table[i] = crc;
        }
        return table;
    }
    
    public async Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples,
        CancellationToken cancellationToken = default)
    {
        if (!initialized || finalized || output == null)
            return;
        
        // Opus encoding (simplified - uses SILK/CELT hybrid)
        // Frame sizes: 2.5, 5, 10, 20, 40, 60 ms
        
        int samplesPerFrame = 960; // 20ms at 48kHz
        int bytesPerSample = settings.BitsPerSample / 8;
        int bytesPerFrame = samplesPerFrame * settings.Channels * bytesPerSample;
        
        int offset = 0;
        while (offset + bytesPerFrame <= samples.Length)
        {
            // Encode frame (placeholder)
            byte[] encodedFrame = new byte[100]; // Compressed data
            
            granulePosition += (uint)samplesPerFrame;
            
            await WriteOggPageAsync(encodedFrame, false, false, granulePosition, cancellationToken);
            pageNumber++;
            
            offset += bytesPerFrame;
        }
    }
    
    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (!initialized || finalized || output == null) return;
        
        // Write final page with EOS flag
        await WriteOggPageAsync([], false, true, granulePosition, cancellationToken);
        
        finalized = true;
    }
    
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

// ============================================================================
// AAC Encoder
// ============================================================================

/// <summary>
/// AAC encoder for high-quality lossy audio.
/// </summary>
public sealed class AacEncoder : IAudioEncoder
{
    private Stream? output;
    private AudioEncoderSettings settings = new();
    private bool initialized;
    private bool finalized;
    
    public string Name => "AAC";
    public string Extension => "m4a";
    public bool IsLossless => false;
    
    public async Task InitializeAsync(Stream output, AudioEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        // Until this encoder is gated against FFmpeg it refuses to run. A file that has the
        // right length, the right header and the right tag but the wrong audio is worse than
        // an exception: it is indistinguishable from a good one until somebody listens. That
        // is exactly how the MP3 stub survived. See the project notes, Phase 7.
        throw new NotImplementedException(
            "The AAC encoder is a stub: it writes an ftyp box and discards every sample handed to it. The only encoder in this file that works is "
          + "Mp3Encoder, which the ripping-mp3 gate holds byte-identical to libmp3lame.");

        this.output = output;
        this.settings = settings;
        
        // Write MP4/M4A container header (ftyp, moov atoms)
        await WriteMp4HeaderAsync(cancellationToken);
        
        initialized = true;
    }
    
    private async Task WriteMp4HeaderAsync(CancellationToken cancellationToken)
    {
        // Minimal MP4 header for AAC
        // ftyp box
        using var ms = new MemoryStream();
        
        // ftyp
        WriteAtom(ms, "ftyp", writer =>
        {
            writer.Write("M4A "u8); // Brand
            writer.Write(new byte[4]); // Version
            writer.Write("M4A "u8); // Compatible brand
            writer.Write("mp42"u8);
            writer.Write("isom"u8);
        });
        
        await output!.WriteAsync(ms.ToArray(), cancellationToken);
    }
    
    private static void WriteAtom(Stream s, string type, Action<BinaryWriter> writeContent)
    {
        using var content = new MemoryStream();
        using var writer = new BinaryWriter(content);
        writeContent(writer);
        
        byte[] data = content.ToArray();
        int size = 8 + data.Length;
        
        s.WriteByte((byte)(size >> 24));
        s.WriteByte((byte)(size >> 16));
        s.WriteByte((byte)(size >> 8));
        s.WriteByte((byte)size);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
    }
    
    public Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples,
        CancellationToken cancellationToken = default)
    {
        if (!initialized || finalized || output == null)
            return Task.CompletedTask;
        
        // AAC encoding (simplified - uses MDCT + psychoacoustic model)
        // Would use FDK-AAC or similar for production
        
        return Task.CompletedTask;
    }
    
    public Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        finalized = true;
        return Task.CompletedTask;
    }
    
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

// ============================================================================
// Encoder Factory
// ============================================================================

// ============================================================================
// WAV (PCM) Encoder
// ============================================================================

/// <summary>
/// WAV output. There is no encoder here and that is the point: WAV carries the samples as they
/// are, so this is the one audio output that needs nothing but a header and a correct
/// float-to-integer conversion from the caller.
/// </summary>
/// <remarks>
/// <see cref="AudioFormat.Wav"/> was in the enum and in
/// <see cref="AudioEncoderFactory.GetExtension"/> from the start, and
/// <see cref="AudioEncoderFactory.Create"/> threw for it, so nothing could write one.
/// <para>
/// Sixteen-bit input is written as <c>pcm_s16le</c> and 32-bit as <c>pcm_f32le</c>, which is
/// what <see cref="WavMuxer"/> describes in the <c>fmt </c> chunk (it picks format tag 3 for
/// 32-bit). Eight and 24 bits are refused rather than written on the strength of the muxer
/// alone: both are legal WAV, nothing in this tree produces those bytes, and a file that is
/// well-formed and wrong is worse than a refusal.
/// </para>
/// Unlike ffmpeg's wav muxer this writes no LIST/INFO chunk, so a file from here and a file
/// from ffmpeg agree on their PCM and not on their length.
/// </remarks>
public sealed class WavEncoder : IAudioEncoder
{
    private WavMuxer? muxer;
    private int trackId = -1;
    private bool initialized;
    private bool finalized;
    private long pcmBytesWritten;

    /// <summary>Bytes of PCM handed to the muxer, header excluded.</summary>
    public long PcmBytesWritten => pcmBytesWritten;

    public string Name => "PCM (WAV)";
    public string Extension => "wav";
    public bool IsLossless => true;

    public async Task InitializeAsync(Stream output, AudioEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.BitsPerSample is not (16 or 32))
        {
            throw new NotSupportedException(
                $"WAV output here is {settings.BitsPerSample}-bit; this writes 16-bit PCM "
              + "(pcm_s16le) or 32-bit float (pcm_f32le). 8- and 24-bit are legal WAV but "
              + "nothing in this build produces those samples.");
        }
        if (settings.Channels < 1)
            throw new NotSupportedException($"WAV output: {settings.Channels} channels.");
        if (settings.SampleRate < 1)
            throw new NotSupportedException($"WAV output: {settings.SampleRate} Hz.");

        muxer = new WavMuxer(output, new MuxerConfiguration
        {
            Format = MuxerFormat.Wav,
            Title = settings.Metadata?.Title,
            Author = settings.Metadata?.Artist,
        });

        trackId = muxer.AddTrack(new MuxerTrackInfo
        {
            TrackType = MuxerTrackType.Audio,
            Codec = settings.BitsPerSample == 16 ? "pcm_s16le" : "pcm_f32le",
            SampleRate = settings.SampleRate,
            ChannelCount = settings.Channels,
            BitsPerSample = settings.BitsPerSample,
        });

        await muxer.WriteHeaderAsync(cancellationToken);
        initialized = true;
    }

    public async Task EncodeSamplesAsync(ReadOnlyMemory<byte> samples,
        CancellationToken cancellationToken = default)
    {
        if (!initialized || muxer is null)
            throw new InvalidOperationException("WAV output: InitializeAsync was not called.");
        if (finalized)
            throw new InvalidOperationException("WAV output: already finalized.");
        if (samples.IsEmpty)
            return;

        await muxer.WriteSampleAsync(
            MuxerSample.Create(trackId, samples, pts: 0, isKeyframe: true),
            cancellationToken);
        pcmBytesWritten += samples.Length;
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (!initialized || muxer is null || finalized)
            return;

        // The RIFF and data chunk sizes are placeholders until this runs, so a file that never
        // reaches here claims a length of zero and no player will play it.
        await muxer.WriteTrailerAsync(cancellationToken);
        finalized = true;
    }

    public ValueTask DisposeAsync()
    {
        muxer?.Dispose();
        muxer = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Factory for creating audio encoders.
/// </summary>
public static class AudioEncoderFactory
{
    /// <summary>
    /// Create an encoder for the specified format.
    /// </summary>
    public static IAudioEncoder Create(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Wav => new WavEncoder(),
            AudioFormat.Flac => new FlacEncoder(),
            AudioFormat.Mp3 => new Mp3Encoder(),
            AudioFormat.Opus => new OpusEncoder(),
            AudioFormat.Aac => new AacEncoder(),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }
    
    /// <summary>
    /// Get the file extension for a format.
    /// </summary>
    public static string GetExtension(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Flac => "flac",
            AudioFormat.Alac => "m4a",
            AudioFormat.Mp3 => "mp3",
            AudioFormat.Aac => "m4a",
            AudioFormat.Opus => "opus",
            AudioFormat.Vorbis => "ogg",
            AudioFormat.Wav => "wav",
            _ => "bin"
        };
    }
}

/// <summary>
/// Audio encoding format.
/// </summary>
public enum AudioFormat
{
    Flac,   // Lossless
    Alac,   // Lossless
    Mp3,    // Lossy
    Aac,    // Lossy
    Opus,   // Lossy (best quality/size)
    Vorbis, // Lossy
    Wav     // Uncompressed
}

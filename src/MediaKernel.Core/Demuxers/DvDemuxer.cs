// MediaKernel - Raw DV (Digital Video) Demuxer
// Demuxer for raw DV streams from camcorders and archival footage
// Supports NTSC (525/60) and PAL (625/50) formats

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// DV video standard (NTSC or PAL).
/// </summary>
public enum DvVideoStandard
{
    /// <summary>NTSC: 525 lines, 29.97fps, 120000 bytes/frame.</summary>
    Ntsc,
    
    /// <summary>PAL: 625 lines, 25fps, 144000 bytes/frame.</summary>
    Pal
}

/// <summary>
/// DV stream information.
/// </summary>
public sealed class DvStreamInfo
{
    /// <summary>Whether the stream was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Video standard (NTSC or PAL).</summary>
    public DvVideoStandard Standard { get; init; }
    
    /// <summary>Frame width in pixels (always 720).</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels (480 for NTSC, 576 for PAL).</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate (29.97 for NTSC, 25 for PAL).</summary>
    public double FrameRate { get; init; }
    
    /// <summary>Size of one frame in bytes.</summary>
    public int FrameSize { get; init; }
    
    /// <summary>Audio sample rate if audio is present (32000, 44100, or 48000).</summary>
    public int AudioSampleRate { get; init; }
    
    /// <summary>Audio bits per sample (always 16).</summary>
    public int AudioBitsPerSample { get; init; }
    
    /// <summary>Number of audio channels (typically 2).</summary>
    public int AudioChannels { get; init; }
    
    /// <summary>Whether audio is present in the stream.</summary>
    public bool HasAudio { get; init; }
    
    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }
    
    /// <summary>Total frame count (0 if unknown).</summary>
    public long TotalFrames { get; init; }
    
    /// <summary>Total duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
}

/// <summary>
/// DV frame data containing both video and extracted audio.
/// </summary>
public readonly struct DvFrame
{
    /// <summary>Raw DV video frame data.</summary>
    public ReadOnlyMemory<byte> VideoData { get; init; }
    
    /// <summary>Extracted PCM audio data (16-bit stereo).</summary>
    public ReadOnlyMemory<byte> AudioData { get; init; }
    
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Frame index.</summary>
    public long FrameIndex { get; init; }
    
    /// <summary>Number of audio samples in this frame.</summary>
    public int AudioSampleCount { get; init; }
}

/// <summary>
/// Demuxer for raw DV (Digital Video) streams.
/// DV was the standard format for consumer and prosumer camcorders.
/// </summary>
public sealed class DvDemuxer : IDisposable
{
    // DV frame sizes
    private const int NtscFrameSize = 10 * 150 * 80;  // 120000 bytes
    private const int PalFrameSize = 12 * 150 * 80;   // 144000 bytes
    
    // Section types
    private const int SectionTypeHeader = 0;
    
    // Audio shuffle tables for interleaved audio extraction
    private static readonly ushort[,] AudioShuffle525 = new ushort[10, 9]
    {
        {  0, 30, 60, 20, 50, 80, 10, 40, 70 }, // 1st channel
        {  6, 36, 66, 26, 56, 86, 16, 46, 76 },
        { 12, 42, 72,  2, 32, 62, 22, 52, 82 },
        { 18, 48, 78,  8, 38, 68, 28, 58, 88 },
        { 24, 54, 84, 14, 44, 74,  4, 34, 64 },
        {  1, 31, 61, 21, 51, 81, 11, 41, 71 }, // 2nd channel
        {  7, 37, 67, 27, 57, 87, 17, 47, 77 },
        { 13, 43, 73,  3, 33, 63, 23, 53, 83 },
        { 19, 49, 79,  9, 39, 69, 29, 59, 89 },
        { 25, 55, 85, 15, 45, 75,  5, 35, 65 },
    };
    
    private static readonly ushort[,] AudioShuffle625 = new ushort[12, 9]
    {
        {   0,  36,  72,  26,  62,  98,  16,  52,  88}, // 1st channel
        {   6,  42,  78,  32,  68, 104,  22,  58,  94},
        {  12,  48,  84,   2,  38,  74,  28,  64, 100},
        {  18,  54,  90,   8,  44,  80,  34,  70, 106},
        {  24,  60,  96,  14,  50,  86,   4,  40,  76},
        {  30,  66, 102,  20,  56,  92,  10,  46,  82},
        {   1,  37,  73,  27,  63,  99,  17,  53,  89}, // 2nd channel
        {   7,  43,  79,  33,  69, 105,  23,  59,  95},
        {  13,  49,  85,   3,  39,  75,  29,  65, 101},
        {  19,  55,  91,   9,  45,  81,  35,  71, 107},
        {  25,  61,  97,  15,  51,  87,   5,  41,  77},
        {  31,  67, 103,  21,  57,  93,  11,  47,  83},
    };
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] frameBuffer;
    
    private DvVideoStandard standard;
    private int frameSize;
    private double frameRate;
    private int audioSampleRate;
    private bool hasAudio;
    private long fileSize;
    private long totalFrames;
    private long frameIndex;
    private bool disposed;
    
    /// <summary>
    /// Creates a new DV demuxer.
    /// </summary>
    public DvDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        // Allocate buffer for largest frame size (PAL)
        frameBuffer = new byte[PalFrameSize];
        fileSize = stream.CanSeek ? stream.Length : 0;
    }
    
    /// <summary>
    /// Parse the stream and detect DV format.
    /// </summary>
    public DvStreamInfo Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        // Read enough for format detection
        int read = stream.Read(frameBuffer, 0, NtscFrameSize);
        if (read < 80)
        {
            return new DvStreamInfo { IsValid = false };
        }
        
        // Parse DIF block header
        if (!TryParseDifHeader(frameBuffer.AsSpan(0, read), out var header))
        {
            return new DvStreamInfo { IsValid = false };
        }
        
        // Determine format from DSF flag
        standard = header.Dsf ? DvVideoStandard.Pal : DvVideoStandard.Ntsc;
        frameSize = header.Dsf ? PalFrameSize : NtscFrameSize;
        frameRate = header.Dsf ? 25.0 : 29.97;
        
        // Check for audio
        hasAudio = false;
        audioSampleRate = 0;
        if (read >= (80 * 6 + 80 * 16 * 3 + 4))
        {
            int aauxOffset = 80 * 6 + 80 * 16 * 3 + 3;
            if (frameBuffer[aauxOffset] == 0x50)
            {
                hasAudio = true;
                audioSampleRate = ParseAudioSampleRate(frameBuffer.AsSpan(aauxOffset + 1, 4));
            }
        }
        
        // Calculate totals
        if (fileSize > 0)
        {
            totalFrames = fileSize / frameSize;
        }
        
        long durationMicroseconds = totalFrames > 0 ? (long)(totalFrames / frameRate * 1_000_000) : 0;
        
        // Seek back to start
        if (stream.CanSeek)
            stream.Position = 0;
        
        return new DvStreamInfo
        {
            IsValid = true,
            Standard = standard,
            Width = 720,
            Height = standard == DvVideoStandard.Pal ? 576 : 480,
            FrameRate = frameRate,
            FrameSize = frameSize,
            AudioSampleRate = audioSampleRate,
            AudioBitsPerSample = 16,
            AudioChannels = 2,
            HasAudio = hasAudio,
            FileSize = fileSize,
            TotalFrames = totalFrames,
            DurationMicroseconds = durationMicroseconds
        };
    }
    
    /// <summary>
    /// Read the next DV frame from the stream.
    /// </summary>
    public bool TryReadFrame(out DvFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        frame = default;
        
        int read = stream.Read(frameBuffer, 0, frameSize);
        if (read < frameSize)
            return false;
        
        // Extract audio if present
        byte[]? audioData = null;
        int sampleCount = 0;
        if (hasAudio)
        {
            (audioData, sampleCount) = ExtractAudio(frameBuffer.AsSpan(0, frameSize));
        }
        
        long pts = (long)(frameIndex / frameRate * 1_000_000);
        
        frame = new DvFrame
        {
            VideoData = frameBuffer.AsMemory(0, frameSize).ToArray(),
            AudioData = audioData ?? Array.Empty<byte>(),
            PresentationTimestamp = pts,
            FrameIndex = frameIndex,
            AudioSampleCount = sampleCount
        };
        
        frameIndex++;
        return true;
    }
    
    /// <summary>
    /// Seek to a specific frame.
    /// </summary>
    public bool SeekToFrame(long targetFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek || frameSize == 0)
            return false;
        
        if (targetFrame < 0)
            targetFrame = 0;
        
        if (totalFrames > 0 && targetFrame >= totalFrames)
            targetFrame = totalFrames - 1;
        
        long position = targetFrame * frameSize;
        stream.Position = position;
        frameIndex = targetFrame;
        
        return true;
    }
    
    /// <summary>
    /// Seek to a specific time in microseconds.
    /// </summary>
    public bool SeekToTime(long timeMicroseconds)
    {
        if (frameRate <= 0)
            return false;
        
        long targetFrame = (long)(timeMicroseconds * frameRate / 1_000_000);
        return SeekToFrame(targetFrame);
    }
    
    /// <summary>
    /// Check if data starts with a valid DV header.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 80) // Need at least one DIF block
            return false;

        // The previous check only tested a handful of scattered bits, which any byte sequence
        // beginning 0x0B 0x77 satisfies - so every AC-3 file was claimed as DV. Require the DIF
        // header marker FFmpeg looks for instead (libavformat/dv.c dv_probe:
        // (state & 0xff07ff7f) == 0x1f07003f on the first big-endian 32-bit word).
        uint state = ((uint)header[0] << 24) | ((uint)header[1] << 16) |
                     ((uint)header[2] << 8) | header[3];
        return (state & 0xff07ff7fu) == 0x1f07003fu;
    }
    
    private static bool TryParseDifHeader(ReadOnlySpan<byte> data, out DifHeader header)
    {
        header = default;
        
        if (data.Length < 8)
            return false;
        
        // Parse DIF ID
        int sectionType = data[0] >> 5;
        if (sectionType != SectionTypeHeader)
            return false;
        
        // DIF sequence number, First/Second channel, Block number
        int difSequenceNumber = (data[1] & 0x0F);
        int firstSecondChannel = (data[1] >> 7) & 0x01;
        int difBlockNumber = data[2];
        
        // Parse header
        bool dsf = (data[3] & 0x80) != 0; // 0 = NTSC, 1 = PAL
        
        // Check "incorrect" bit
        if ((data[3] & 0x40) != 0)
            return false;
        
        header = new DifHeader
        {
            SectionType = sectionType,
            DifSequenceNumber = difSequenceNumber,
            FirstSecondChannel = firstSecondChannel,
            DifBlockNumber = difBlockNumber,
            Dsf = dsf
        };
        
        return true;
    }
    
    private static int ParseAudioSampleRate(ReadOnlySpan<byte> aauxData)
    {
        // Sample rate is in bits 3-5 of AAUX byte 3
        int sampleRateCode = (aauxData[3] >> 3) & 0x07;
        
        return sampleRateCode switch
        {
            0 => 48000,
            1 => 44100,
            2 => 32000,
            _ => 48000
        };
    }
    
    private (byte[] audioData, int sampleCount) ExtractAudio(ReadOnlySpan<byte> frameData)
    {
        bool isPal = standard == DvVideoStandard.Pal;
        int segmentCount = isPal ? 12 : 10;
        int halfChannelCount = segmentCount / 2;
        
        // Get audio quantization and sample count
        int aauxOffset = 80 * 6 + 80 * 16 * 3 + 3;
        if (frameData[aauxOffset] != 0x50)
            return (Array.Empty<byte>(), 0);
        
        var aauxData = frameData.Slice(aauxOffset + 1, 4);
        int audioQuant = aauxData[3] & 0x07; // 0 = 16-bit, 1 = 12-bit
        
        // Calculate sample count
        int baseSamples = aauxData[0] & 0x3F;
        int sampleCount = audioQuant switch
        {
            0 when isPal => baseSamples + 1896,
            0 => baseSamples + 1580,
            1 when isPal => baseSamples + 1742,
            1 => baseSamples + 1452,
            _ when isPal => baseSamples + 1264,
            _ => baseSamples + 1053
        };
        
        // Allocate audio buffer (stereo 16-bit)
        byte[] audioBuffer = new byte[sampleCount * 4];
        var audioShuffle = isPal ? AudioShuffle625 : AudioShuffle525;
        int samplesPerRow = isPal ? 108 : 90;
        
        int frameOffset = 0;
        
        for (int segment = 0; segment < segmentCount; segment++)
        {
            frameOffset += 6 * 80; // Skip DIF segment header
            
            // For 12-bit, only process first half of segments
            if (audioQuant == 1 && segment == halfChannelCount)
                break;
            
            for (int block = 0; block < 9; block++)
            {
                for (int d = 8; d < 80; d += 2)
                {
                    if (audioQuant == 0)
                    {
                        // 16-bit quantization
                        int outputOffset = audioShuffle[segment, block] + (d - 8) / 2 * samplesPerRow;
                        if (outputOffset * 2 >= audioBuffer.Length)
                            continue;
                        
                        // Convert from big-endian to little-endian
                        audioBuffer[outputOffset * 2] = frameData[frameOffset + d + 1];
                        audioBuffer[outputOffset * 2 + 1] = frameData[frameOffset + d];
                        
                        // Handle invalid sample marker
                        if (audioBuffer[outputOffset * 2 + 1] == 0x80 && audioBuffer[outputOffset * 2] == 0x00)
                            audioBuffer[outputOffset * 2 + 1] = 0;
                    }
                    else
                    {
                        // 12-bit quantization - convert to 16-bit
                        ushort leftChannel = (ushort)((frameData[frameOffset + d] << 4) | (frameData[frameOffset + d + 2] >> 4));
                        ushort rightChannel = (ushort)((frameData[frameOffset + d + 1] << 4) | (frameData[frameOffset + d + 2] & 0x0F));
                        
                        leftChannel = leftChannel == 0x800 ? (ushort)0 : Audio12To16(leftChannel);
                        rightChannel = rightChannel == 0x800 ? (ushort)0 : Audio12To16(rightChannel);
                        
                        int leftOffset = audioShuffle[segment, block] + (d - 8) / 3 * samplesPerRow;
                        if (leftOffset * 2 < audioBuffer.Length)
                        {
                            audioBuffer[leftOffset * 2] = (byte)(leftChannel & 0xFF);
                            audioBuffer[leftOffset * 2 + 1] = (byte)(leftChannel >> 8);
                        }
                        
                        int rightOffset = audioShuffle[segment + halfChannelCount, block] + (d - 8) / 3 * samplesPerRow;
                        if (rightOffset * 2 < audioBuffer.Length)
                        {
                            audioBuffer[rightOffset * 2] = (byte)(rightChannel & 0xFF);
                            audioBuffer[rightOffset * 2 + 1] = (byte)(rightChannel >> 8);
                        }
                        
                        d++; // Extra increment for 12-bit (3 bytes per 2 samples)
                    }
                }
                
                frameOffset += 16 * 80; // 15 Video DIFs + 1 Audio DIF
            }
        }
        
        return (audioBuffer, sampleCount);
    }
    
    private static ushort Audio12To16(ushort sample)
    {
        // Convert 12-bit non-linear to 16-bit linear
        sample = sample < 0x800 ? sample : (ushort)(sample | 0xF000);
        int shift = (sample & 0xF00) >> 8;
        
        ushort result;
        if (shift < 0x2 || shift > 0xD)
        {
            result = sample;
        }
        else if (shift < 0x8)
        {
            shift--;
            result = (ushort)((sample - (256 * shift)) << shift);
        }
        else
        {
            shift = 0xE - shift;
            result = (ushort)(((sample + (256 * shift + 1)) << shift) - 1);
        }
        
        return result;
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (!leaveOpen)
                stream.Dispose();
        }
    }
    
    private struct DifHeader
    {
        public int SectionType;
        public int DifSequenceNumber;
        public int FirstSecondChannel;
        public int DifBlockNumber;
        public bool Dsf;
    }
}

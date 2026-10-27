// WAV/RIFF audio demuxer
// Reference: VLC's modules/demux/wav.c, Microsoft WAV format specification

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// WAV audio format codes.
/// </summary>
public enum WavFormatTag : ushort
{
    Unknown = 0x0000,
    Pcm = 0x0001,
    Adpcm = 0x0002,
    IeeeFloat = 0x0003,
    Alaw = 0x0006,
    Mulaw = 0x0007,
    ImaAdpcm = 0x0011,
    Gsm610 = 0x0031,
    MsAdpcm = 0x0002,
    G723_1 = 0x0042,
    MpegLayer3 = 0x0055,
    DolbyAc3 = 0x2000,
    Wma1 = 0x0160,
    Wma2 = 0x0161,
    WmaPro = 0x0162,
    WmaLossless = 0x0163,
    Extensible = 0xFFFE
}

/// <summary>
/// WAV file format information.
/// </summary>
public readonly struct WavFormat
{
    public readonly WavFormatTag FormatTag;
    public readonly ushort Channels;
    public readonly uint SampleRate;
    public readonly uint BytesPerSecond;
    public readonly ushort BlockAlign;
    public readonly ushort BitsPerSample;
    public readonly ushort ExtensionSize;
    public readonly ushort ValidBitsPerSample;
    public readonly uint ChannelMask;
    public readonly Guid SubFormat;

    public WavFormat(WavFormatTag formatTag, ushort channels, uint sampleRate,
        uint bytesPerSecond, ushort blockAlign, ushort bitsPerSample,
        ushort extensionSize = 0, ushort validBitsPerSample = 0,
        uint channelMask = 0, Guid subFormat = default)
    {
        FormatTag = formatTag;
        Channels = channels;
        SampleRate = sampleRate;
        BytesPerSecond = bytesPerSecond;
        BlockAlign = blockAlign;
        BitsPerSample = bitsPerSample;
        ExtensionSize = extensionSize;
        ValidBitsPerSample = validBitsPerSample == 0 ? bitsPerSample : validBitsPerSample;
        ChannelMask = channelMask;
        SubFormat = subFormat;
    }

    /// <summary>Gets whether this is a PCM format.</summary>
    public bool IsPcm => FormatTag == WavFormatTag.Pcm ||
        (FormatTag == WavFormatTag.Extensible && SubFormat == PcmSubFormatGuid);

    /// <summary>Gets whether this is IEEE float format.</summary>
    public bool IsFloat => FormatTag == WavFormatTag.IeeeFloat ||
        (FormatTag == WavFormatTag.Extensible && SubFormat == FloatSubFormatGuid);

    /// <summary>PCM subformat GUID.</summary>
    public static readonly Guid PcmSubFormatGuid = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>IEEE Float subformat GUID.</summary>
    public static readonly Guid FloatSubFormatGuid = new("00000003-0000-0010-8000-00aa00389b71");
}

/// <summary>
/// WAV/RIFF audio file demuxer.
/// Supports PCM, IEEE Float, A-law, μ-law, and ADPCM formats.
/// </summary>
public sealed class WavDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer = new byte[8192];

    private WavFormat format;
    private long dataChunkOffset;
    private long dataChunkSize;
    private long currentPosition;
    private long totalSamples;
    private bool isRf64;
    private bool isInitialized;
    private bool isDisposed;

    private const uint RiffFourCc = 0x46464952; // "RIFF"
    private const uint Rf64FourCc = 0x34364652; // "RF64"
    private const uint WaveFourCc = 0x45564157; // "WAVE"
    private const uint FmtFourCc = 0x20746D66;  // "fmt "
    private const uint DataFourCc = 0x61746164; // "data"
    private const uint FactFourCc = 0x74636166; // "fact"
    private const uint Ds64FourCc = 0x34367364; // "ds64"

    /// <summary>
    /// Creates a new WAV demuxer for the given stream.
    /// </summary>
    public WavDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>Gets the audio format information.</summary>
    public WavFormat Format => format;

    /// <summary>Gets the total number of samples.</summary>
    public long TotalSamples => totalSamples;

    /// <summary>Gets the duration in seconds.</summary>
    public double DurationSeconds => format.SampleRate > 0 ? (double)totalSamples / format.SampleRate : 0;

    /// <summary>Gets whether this is an RF64 extended file.</summary>
    public bool IsRf64 => isRf64;

    /// <summary>Gets the current position as a sample index.</summary>
    public long CurrentSamplePosition => currentPosition / format.BlockAlign;

    /// <summary>Gets the current position in bytes within the data chunk.</summary>
    public long CurrentBytePosition => currentPosition;

    /// <summary>
    /// Initializes the demuxer by parsing the WAV header.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        if (!inputStream.CanRead || !inputStream.CanSeek)
            return false;

        inputStream.Position = 0;

        // Read RIFF header
        if (inputStream.Read(readBuffer, 0, 12) != 12)
            return false;

        uint riffId = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer);
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(4));
        uint waveId = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(8));

        if (riffId == Rf64FourCc)
            isRf64 = true;
        else if (riffId != RiffFourCc)
            return false;

        if (waveId != WaveFourCc)
            return false;

        // Parse chunks
        long rf64DataSize = -1;
        long rf64TotalSamples = -1;
        bool foundFmt = false;
        bool foundData = false;

        while (inputStream.Position < inputStream.Length)
        {
            if (inputStream.Read(readBuffer, 0, 8) != 8)
                break;

            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(4));
            long chunkStart = inputStream.Position;

            switch (chunkId)
            {
                case Ds64FourCc:
                    // RF64 extended size information
                    if (inputStream.Read(readBuffer, 0, 24) == 24)
                    {
                        rf64DataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(readBuffer.AsSpan(8));
                        rf64TotalSamples = (long)BinaryPrimitives.ReadUInt64LittleEndian(readBuffer.AsSpan(16));
                    }
                    break;

                case FmtFourCc:
                    if (!ParseFormatChunk(chunkSize))
                        return false;
                    foundFmt = true;
                    break;

                case DataFourCc:
                    dataChunkOffset = inputStream.Position;
                    dataChunkSize = isRf64 && rf64DataSize >= 0 ? rf64DataSize : chunkSize;
                    foundData = true;
                    break;

                case FactFourCc:
                    if (inputStream.Read(readBuffer, 0, 4) == 4)
                    {
                        totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer);
                    }
                    break;
            }

            // Skip to next chunk (pad to even boundary)
            long nextChunk = chunkStart + chunkSize + (chunkSize & 1);
            if (nextChunk > inputStream.Length)
                break;
            inputStream.Position = nextChunk;
        }

        if (!foundFmt || !foundData)
            return false;

        // Calculate total samples if not in fact chunk
        if (totalSamples == 0 && format.BlockAlign > 0)
        {
            totalSamples = dataChunkSize / format.BlockAlign;
        }

        if (isRf64 && rf64TotalSamples >= 0)
            totalSamples = rf64TotalSamples;

        // Seek to start of data
        inputStream.Position = dataChunkOffset;
        currentPosition = 0;
        isInitialized = true;

        return true;
    }

    /// <summary>
    /// Reads audio samples from the WAV file.
    /// </summary>
    /// <param name="buffer">Buffer to read into.</param>
    /// <returns>Number of bytes read, 0 if end of data.</returns>
    public int ReadSamples(Span<byte> buffer)
    {
        if (!isInitialized)
            return 0;

        long remaining = dataChunkSize - currentPosition;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);
        
        // Align to block boundary
        if (format.BlockAlign > 0)
            toRead = (toRead / format.BlockAlign) * format.BlockAlign;

        if (toRead == 0)
            return 0;

        int bytesRead = inputStream.Read(buffer[..toRead]);
        currentPosition += bytesRead;

        return bytesRead;
    }

    /// <summary>
    /// Reads a specific number of samples.
    /// </summary>
    /// <param name="sampleBuffer">Buffer for samples (length = samples × channels × bytesPerSample)</param>
    /// <param name="sampleCount">Number of samples to read.</param>
    /// <returns>Actual number of samples read.</returns>
    public int ReadSamples(Span<byte> sampleBuffer, int sampleCount)
    {
        int bytesNeeded = sampleCount * format.BlockAlign;
        if (bytesNeeded > sampleBuffer.Length)
            bytesNeeded = (sampleBuffer.Length / format.BlockAlign) * format.BlockAlign;

        int bytesRead = ReadSamples(sampleBuffer[..bytesNeeded]);
        return bytesRead / format.BlockAlign;
    }

    /// <summary>
    /// Seeks to a specific sample position.
    /// </summary>
    public bool SeekToSample(long sampleIndex)
    {
        if (!isInitialized || format.BlockAlign == 0)
            return false;

        if (sampleIndex < 0)
            sampleIndex = 0;
        if (sampleIndex > totalSamples)
            sampleIndex = totalSamples;

        long byteOffset = sampleIndex * format.BlockAlign;
        if (byteOffset > dataChunkSize)
            byteOffset = dataChunkSize;

        inputStream.Position = dataChunkOffset + byteOffset;
        currentPosition = byteOffset;

        return true;
    }

    /// <summary>
    /// Seeks to a specific time position.
    /// </summary>
    public bool SeekToTime(double seconds)
    {
        if (!isInitialized || format.SampleRate == 0)
            return false;

        long sampleIndex = (long)(seconds * format.SampleRate);
        return SeekToSample(sampleIndex);
    }

    /// <summary>
    /// Gets the current time position in seconds.
    /// </summary>
    public double GetCurrentTime()
    {
        if (format.SampleRate == 0 || format.BlockAlign == 0)
            return 0;
        return (double)(currentPosition / format.BlockAlign) / format.SampleRate;
    }

    private bool ParseFormatChunk(uint chunkSize)
    {
        if (chunkSize < 16)
            return false;

        int toRead = (int)Math.Min(chunkSize, readBuffer.Length);
        if (inputStream.Read(readBuffer, 0, toRead) != toRead)
            return false;

        var formatTag = (WavFormatTag)BinaryPrimitives.ReadUInt16LittleEndian(readBuffer);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(readBuffer.AsSpan(2));
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(4));
        uint bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(8));
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(readBuffer.AsSpan(12));
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(readBuffer.AsSpan(14));

        ushort extensionSize = 0;
        ushort validBitsPerSample = 0;
        uint channelMask = 0;
        Guid subFormat = default;

        if (chunkSize >= 18)
        {
            extensionSize = BinaryPrimitives.ReadUInt16LittleEndian(readBuffer.AsSpan(16));
        }

        if (formatTag == WavFormatTag.Extensible && chunkSize >= 40 && extensionSize >= 22)
        {
            validBitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(readBuffer.AsSpan(18));
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(20));
            subFormat = new Guid(readBuffer.AsSpan(24, 16));
        }

        // Validate
        if (channels == 0 || channels > 32)
            return false;
        if (sampleRate == 0 || sampleRate > 384000)
            return false;

        format = new WavFormat(formatTag, channels, sampleRate, bytesPerSecond,
            blockAlign, bitsPerSample, extensionSize, validBitsPerSample, channelMask, subFormat);

        return true;
    }


    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
        // Don't dispose the stream - caller owns it
    }
}

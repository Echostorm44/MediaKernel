// MediaKernel - AIFF (Audio Interchange File Format) Demuxer
// Supports Apple's AIFF and AIFF-C audio formats

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// AIFF audio format types.
/// </summary>
public enum AiffFormat
{
    Unknown,
    Aiff,      // Standard AIFF (uncompressed)
    AiffC      // AIFF-C (compressed)
}

/// <summary>
/// AIFF file information.
/// </summary>
public readonly struct AiffInfo
{
    public AiffFormat Format { get; init; }
    public int NumChannels { get; init; }
    public long NumSampleFrames { get; init; }
    public int SampleSize { get; init; }  // Bits per sample
    public int SampleRate { get; init; }
    public string CompressionType { get; init; }  // For AIFF-C
    public long DataOffset { get; init; }
    public long DataSize { get; init; }
    public TimeSpan Duration => SampleRate > 0 
        ? TimeSpan.FromSeconds((double)NumSampleFrames / SampleRate) 
        : TimeSpan.Zero;
}

/// <summary>
/// Demuxer for AIFF (Audio Interchange File Format) files.
/// Used primarily in professional audio and Apple systems.
/// </summary>
public sealed class AiffDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private AiffInfo info;
    private long currentSampleFrame;
    private bool disposed;
    
    /// <summary>
    /// Creates an AIFF demuxer for the specified stream.
    /// </summary>
    public AiffDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>
    /// Gets the AIFF file information after initialization.
    /// </summary>
    public AiffInfo Info => info;
    
    /// <summary>
    /// Gets the current playback position.
    /// </summary>
    public TimeSpan Position => info.SampleRate > 0
        ? TimeSpan.FromSeconds((double)currentSampleFrame / info.SampleRate)
        : TimeSpan.Zero;
    
    /// <summary>
    /// Initializes the demuxer by reading the file header.
    /// </summary>
    public async ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[12];
        int bytesRead = await stream.ReadAsync(header, cancellationToken);
        if (bytesRead < 12)
            return false;
        
        // Check for FORM header
        if (header[0] != 'F' || header[1] != 'O' || header[2] != 'R' || header[3] != 'M')
            return false;
        
        // Get file size (big-endian)
        uint fileSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        
        // Check form type (AIFF or AIFC)
        string formType = Encoding.ASCII.GetString(header, 8, 4);
        AiffFormat format = formType switch
        {
            "AIFF" => AiffFormat.Aiff,
            "AIFC" => AiffFormat.AiffC,
            _ => AiffFormat.Unknown
        };
        
        if (format == AiffFormat.Unknown)
            return false;
        
        // Parse chunks
        int numChannels = 0;
        long numSampleFrames = 0;
        int sampleSize = 0;
        int sampleRate = 0;
        string compressionType = "NONE";
        long dataOffset = 0;
        long dataSize = 0;
        
        byte[] chunkHeader = new byte[8];
        long position = 12;
        
        while (position < fileSize + 8)
        {
            stream.Position = position;
            bytesRead = await stream.ReadAsync(chunkHeader, cancellationToken);
            if (bytesRead < 8)
                break;
            
            string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(4));
            
            switch (chunkId)
            {
                case "COMM":
                    // Common chunk - audio format info
                    byte[] commData = new byte[chunkSize];
                    await stream.ReadAsync(commData, cancellationToken);
                    
                    numChannels = BinaryPrimitives.ReadInt16BigEndian(commData);
                    numSampleFrames = BinaryPrimitives.ReadUInt32BigEndian(commData.AsSpan(2));
                    sampleSize = BinaryPrimitives.ReadInt16BigEndian(commData.AsSpan(6));
                    sampleRate = ReadExtendedPrecision(commData.AsSpan(8, 10));
                    
                    // AIFF-C has compression type
                    if (format == AiffFormat.AiffC && chunkSize >= 22)
                    {
                        compressionType = Encoding.ASCII.GetString(commData, 18, 4).Trim('\0');
                    }
                    break;
                    
                case "SSND":
                    // Sound data chunk
                    byte[] ssndHeader = new byte[8];
                    await stream.ReadAsync(ssndHeader, cancellationToken);
                    
                    uint offset = BinaryPrimitives.ReadUInt32BigEndian(ssndHeader);
                    uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(ssndHeader.AsSpan(4));
                    
                    dataOffset = position + 8 + 8 + offset;
                    dataSize = chunkSize - 8 - offset;
                    break;
            }
            
            // Move to next chunk (padded to even boundary)
            position += 8 + chunkSize + (chunkSize & 1);
        }
        
        if (numChannels == 0 || sampleRate == 0 || dataOffset == 0)
            return false;
        
        info = new AiffInfo
        {
            Format = format,
            NumChannels = numChannels,
            NumSampleFrames = numSampleFrames,
            SampleSize = sampleSize,
            SampleRate = sampleRate,
            CompressionType = compressionType,
            DataOffset = dataOffset,
            DataSize = dataSize
        };
        
        // Seek to data start
        stream.Position = dataOffset;
        currentSampleFrame = 0;
        
        return true;
    }
    
    /// <summary>
    /// Reads audio samples from the AIFF file.
    /// </summary>
    /// <param name="buffer">Buffer to receive sample data.</param>
    /// <returns>Number of bytes read.</returns>
    public int ReadSamples(Span<byte> buffer)
    {
        if (info.DataOffset == 0)
            return 0;
        
        int bytesPerFrame = info.NumChannels * (info.SampleSize / 8);
        int maxFrames = buffer.Length / bytesPerFrame;
        long remainingFrames = info.NumSampleFrames - currentSampleFrame;
        int framesToRead = (int)Math.Min(maxFrames, remainingFrames);
        
        if (framesToRead <= 0)
            return 0;
        
        int bytesToRead = framesToRead * bytesPerFrame;
        int bytesRead = stream.Read(buffer.Slice(0, bytesToRead));
        
        int framesRead = bytesRead / bytesPerFrame;
        currentSampleFrame += framesRead;
        
        return bytesRead;
    }
    
    /// <summary>
    /// Reads audio samples as floating-point values.
    /// </summary>
    /// <param name="output">Buffer to receive float samples.</param>
    /// <returns>Number of samples read.</returns>
    public int ReadSamplesFloat(Span<float> output)
    {
        int bytesPerSample = info.SampleSize / 8;
        int bytesToRead = output.Length * bytesPerSample;
        
        byte[] rawBuffer = new byte[bytesToRead];
        int bytesRead = ReadSamples(rawBuffer);
        int samplesRead = bytesRead / bytesPerSample;
        
        // Convert to float based on sample size
        for (int i = 0; i < samplesRead; i++)
        {
            int byteOffset = i * bytesPerSample;
            
            float sample = info.SampleSize switch
            {
                8 => (rawBuffer[byteOffset] - 128) / 128f,
                16 => BinaryPrimitives.ReadInt16BigEndian(rawBuffer.AsSpan(byteOffset)) / 32768f,
                24 => ReadInt24BigEndian(rawBuffer.AsSpan(byteOffset)) / 8388608f,
                32 => BinaryPrimitives.ReadInt32BigEndian(rawBuffer.AsSpan(byteOffset)) / 2147483648f,
                _ => 0f
            };
            
            output[i] = sample;
        }
        
        return samplesRead;
    }
    
    /// <summary>
    /// Seeks to a specific time position.
    /// </summary>
    public bool Seek(TimeSpan position)
    {
        if (info.DataOffset == 0 || info.SampleRate == 0)
            return false;
        
        long targetFrame = (long)(position.TotalSeconds * info.SampleRate);
        targetFrame = Math.Clamp(targetFrame, 0, info.NumSampleFrames);
        
        int bytesPerFrame = info.NumChannels * (info.SampleSize / 8);
        long byteOffset = targetFrame * bytesPerFrame;
        
        stream.Position = info.DataOffset + byteOffset;
        currentSampleFrame = targetFrame;
        
        return true;
    }
    
    /// <summary>
    /// Reads an 80-bit extended precision floating point number (big-endian).
    /// Used by AIFF for sample rate.
    /// </summary>
    private static int ReadExtendedPrecision(ReadOnlySpan<byte> data)
    {
        // 80-bit extended precision: 1 sign, 15 exponent, 64 mantissa.
        //
        // The mantissa is UNSIGNED. Unlike the 32- and 64-bit formats, this one stores the
        // leading integer bit explicitly, so bit 63 is set for every normal number and reading
        // the mantissa as Int64 makes it negative. An arithmetic shift of that then fills from
        // the sign and truncates to nonsense: 48000 Hz came back as -17536, which is 48000 read
        // as a 16-bit signed value, and the demuxer rejected the file or the pipeline produced
        // a stream at a negative rate. Nothing caught it because until PCM was decodable
        // nothing opened an AIFF at all.
        int exponent = ((data[0] & 0x7F) << 8) | data[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(2));
        
        if (exponent == 0)
            return 0;
        
        // Bias is 16383 for 80-bit
        int shift = exponent - 16383 - 63;
        
        if (shift >= 0)
            return shift < 64 ? (int)(mantissa << shift) : 0;
        else
            return -shift < 64 ? (int)(mantissa >> -shift) : 0;
    }
    
    /// <summary>
    /// Reads a 24-bit signed integer (big-endian).
    /// </summary>
    private static int ReadInt24BigEndian(ReadOnlySpan<byte> data)
    {
        int value = (data[0] << 16) | (data[1] << 8) | data[2];
        // Sign extend
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000);
        return value;
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            if (!leaveOpen)
                stream.Dispose();
            disposed = true;
        }
    }
}

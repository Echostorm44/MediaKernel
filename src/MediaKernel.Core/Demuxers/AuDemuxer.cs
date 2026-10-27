// Sun/NeXT AU audio file demuxer
// Reference: VLC au.c, https://en.wikipedia.org/wiki/Au_file_format

using System.Buffers.Binary;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Audio encoding types in AU files.
/// </summary>
public enum AuEncodingType : uint
{
    Unknown = 0,
    MuLaw8 = 1,       // 8-bit ISDN u-law
    Linear8 = 2,      // 8-bit linear PCM (signed)
    Linear16 = 3,     // 16-bit linear PCM
    Linear24 = 4,     // 24-bit linear PCM  
    Linear32 = 5,     // 32-bit linear PCM
    Float32 = 6,      // 32-bit IEEE float
    Float64 = 7,      // 64-bit IEEE float
    AdpcmG721 = 23,   // 4-bit CCITT G.721 ADPCM
    AdpcmG722 = 24,   // CCITT G.722 ADPCM
    AdpcmG723_3 = 25, // CCITT G.723 3-bit ADPCM
    AdpcmG723_5 = 26, // CCITT G.723 5-bit ADPCM
    ALaw8 = 27        // 8-bit ISDN A-law
}

/// <summary>
/// AU file header information.
/// </summary>
public readonly struct AuFileHeader
{
    public readonly uint DataOffset;
    public readonly uint DataSize;
    public readonly AuEncodingType Encoding;
    public readonly uint SampleRate;
    public readonly uint ChannelCount;
    
    public AuFileHeader(uint dataOffset, uint dataSize, AuEncodingType encoding,
        uint sampleRate, uint channelCount)
    {
        DataOffset = dataOffset;
        DataSize = dataSize;
        Encoding = encoding;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }
    
    /// <summary>Gets the bits per sample for PCM encodings.</summary>
    public int BitsPerSample => Encoding switch
    {
        AuEncodingType.MuLaw8 or AuEncodingType.ALaw8 or AuEncodingType.Linear8 => 8,
        AuEncodingType.Linear16 => 16,
        AuEncodingType.Linear24 => 24,
        AuEncodingType.Linear32 or AuEncodingType.Float32 => 32,
        AuEncodingType.Float64 => 64,
        _ => 0
    };
    
    /// <summary>Gets bytes per sample frame (all channels).</summary>
    public int BytesPerFrame => (BitsPerSample / 8) * (int)ChannelCount;
}

/// <summary>
/// Demuxer for Sun/NeXT AU audio files.
/// </summary>
/// <remarks>
/// AU is a simple audio format common on Unix systems.
/// Header: ".snd" magic, then big-endian fields for offset, size, encoding, rate, channels.
/// </remarks>
public sealed class AuDemuxer : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private AuFileHeader _header;
    private long _dataStartPosition;
    private long _currentPosition;
    private bool _isInitialized;
    
    /// <summary>File header information.</summary>
    public AuFileHeader Header => _header;
    
    /// <summary>Duration in seconds, or 0 if unknown.</summary>
    public double Duration
    {
        get
        {
            if (_header.SampleRate == 0 || _header.BytesPerFrame == 0)
                return 0;
            
            long dataSize = _header.DataSize != 0xFFFFFFFF 
                ? _header.DataSize 
                : _stream.Length - _dataStartPosition;
            
            long totalFrames = dataSize / _header.BytesPerFrame;
            return (double)totalFrames / _header.SampleRate;
        }
    }
    
    /// <summary>Current position in seconds.</summary>
    public double Position
    {
        get
        {
            if (_header.SampleRate == 0 || _header.BytesPerFrame == 0)
                return 0;
            long framesRead = _currentPosition / _header.BytesPerFrame;
            return (double)framesRead / _header.SampleRate;
        }
    }
    
    public AuDemuxer(Stream stream, bool ownsStream = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }
    
    /// <summary>
    /// Initializes the demuxer by reading the AU header.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
            return true;
        
        byte[] headerBytes = new byte[24];
        int bytesRead = await _stream.ReadAsync(headerBytes, cancellationToken);
        if (bytesRead < 24)
            return false;
        
        // Check magic ".snd"
        if (headerBytes[0] != '.' || headerBytes[1] != 's' || 
            headerBytes[2] != 'n' || headerBytes[3] != 'd')
            return false;
        
        // Parse header (all big-endian)
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(4));
        uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(8));
        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(12));
        uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(16));
        uint channelCount = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(20));
        
        if (dataOffset < 24 || sampleRate == 0 || channelCount == 0)
            return false;
        
        _header = new AuFileHeader(
            dataOffset,
            dataSize,
            (AuEncodingType)encoding,
            sampleRate,
            channelCount);
        
        // Skip any extra header data (annotations, etc.)
        if (dataOffset > 24)
        {
            int skipBytes = (int)(dataOffset - 24);
            _stream.Seek(skipBytes, SeekOrigin.Current);
        }
        
        _dataStartPosition = _stream.Position;
        _currentPosition = 0;
        _isInitialized = true;
        
        return true;
    }
    
    /// <summary>
    /// Reads audio samples and converts to normalized float.
    /// </summary>
    /// <returns>Number of sample frames read.</returns>
    /// <summary>
    /// Reads raw sample bytes, aligned to a whole number of sample frames, without decoding
    /// them.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="DecodePipeline"/> uses, so that the samples go through the
    /// translation of ffmpeg's pcm.c and the gate that holds it to ffmpeg's bytes.
    /// <see cref="ReadSamplesFloat"/> is a second, independent PCM decoder living in this
    /// file, written before there was one; nothing in the pipeline calls it.
    /// </remarks>
    public int ReadSamples(Span<byte> buffer)
    {
        if (!_isInitialized)
            return 0;

        int bytesPerFrame = _header.BytesPerFrame;
        if (bytesPerFrame == 0)
            return 0;

        int toRead = buffer.Length / bytesPerFrame * bytesPerFrame;

        // The header's data size is 0xFFFFFFFF in a stream of unknown length, which is a
        // legitimate AU file and not a 4 GB one.
        if (_header.DataSize != 0 && _header.DataSize != uint.MaxValue)
        {
            long remaining = _header.DataSize - _currentPosition;
            if (remaining <= 0) return 0;
            if (toRead > remaining) toRead = (int)(remaining / bytesPerFrame) * bytesPerFrame;
        }
        if (toRead <= 0) return 0;

        int read = _stream.Read(buffer[..toRead]);
        _currentPosition += read;
        return read / bytesPerFrame * bytesPerFrame;
    }

    public int ReadSamplesFloat(Span<float> outputSamples)
    {
        if (!_isInitialized)
            return 0;
        
        int bytesPerFrame = _header.BytesPerFrame;
        if (bytesPerFrame == 0)
            return 0;
        
        int maxFrames = outputSamples.Length / (int)_header.ChannelCount;
        int bytesToRead = maxFrames * bytesPerFrame;
        
        byte[] buffer = new byte[bytesToRead];
        int bytesRead = _stream.Read(buffer, 0, bytesToRead);
        if (bytesRead == 0)
            return 0;
        
        int framesRead = bytesRead / bytesPerFrame;
        _currentPosition += bytesRead;
        
        // Decode based on encoding type
        return _header.Encoding switch
        {
            AuEncodingType.Linear8 => DecodeLinear8(buffer, outputSamples, framesRead),
            AuEncodingType.Linear16 => DecodeLinear16(buffer, outputSamples, framesRead),
            AuEncodingType.Linear24 => DecodeLinear24(buffer, outputSamples, framesRead),
            AuEncodingType.Linear32 => DecodeLinear32(buffer, outputSamples, framesRead),
            AuEncodingType.Float32 => DecodeFloat32(buffer, outputSamples, framesRead),
            AuEncodingType.Float64 => DecodeFloat64(buffer, outputSamples, framesRead),
            AuEncodingType.MuLaw8 => DecodeMuLaw(buffer, outputSamples, framesRead),
            AuEncodingType.ALaw8 => DecodeALaw(buffer, outputSamples, framesRead),
            _ => 0
        };
    }
    
    /// <summary>
    /// Seeks to a specific time position.
    /// </summary>
    public bool Seek(double timeSeconds)
    {
        if (!_isInitialized || _header.BytesPerFrame == 0)
            return false;
        
        long frameOffset = (long)(timeSeconds * _header.SampleRate);
        long byteOffset = frameOffset * _header.BytesPerFrame;
        
        // Clamp to valid range
        long maxOffset = _header.DataSize != 0xFFFFFFFF 
            ? _header.DataSize 
            : _stream.Length - _dataStartPosition;
        
        byteOffset = Math.Clamp(byteOffset, 0, maxOffset);
        
        _stream.Seek(_dataStartPosition + byteOffset, SeekOrigin.Begin);
        _currentPosition = byteOffset;
        
        return true;
    }
    
    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }
    
    #region Decoding Methods
    
    private int DecodeLinear8(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 128.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        
        for (int i = 0; i < sampleCount && i < buffer.Length; i++)
        {
            // AU uses signed 8-bit for Linear8
            sbyte sample = (sbyte)buffer[i];
            output[i] = sample * scale;
        }
        
        return frameCount;
    }
    
    private int DecodeLinear16(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 32768.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        int offset = 0;
        
        for (int i = 0; i < sampleCount && offset + 1 < buffer.Length; i++)
        {
            short sample = BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(offset));
            output[i] = sample * scale;
            offset += 2;
        }
        
        return frameCount;
    }
    
    private int DecodeLinear24(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 8388608.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        int offset = 0;
        
        for (int i = 0; i < sampleCount && offset + 2 < buffer.Length; i++)
        {
            // Big-endian 24-bit, sign extend to 32-bit
            int sample = (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);
            
            output[i] = sample * scale;
            offset += 3;
        }
        
        return frameCount;
    }
    
    private int DecodeLinear32(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 2147483648.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        int offset = 0;
        
        for (int i = 0; i < sampleCount && offset + 3 < buffer.Length; i++)
        {
            int sample = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset));
            output[i] = sample * scale;
            offset += 4;
        }
        
        return frameCount;
    }
    
    private int DecodeFloat32(byte[] buffer, Span<float> output, int frameCount)
    {
        int sampleCount = frameCount * (int)_header.ChannelCount;
        int offset = 0;
        
        for (int i = 0; i < sampleCount && offset + 3 < buffer.Length; i++)
        {
            // Big-endian float - need to reverse bytes
            uint bits = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
            output[i] = BitConverter.UInt32BitsToSingle(bits);
            offset += 4;
        }
        
        return frameCount;
    }
    
    private int DecodeFloat64(byte[] buffer, Span<float> output, int frameCount)
    {
        int sampleCount = frameCount * (int)_header.ChannelCount;
        int offset = 0;
        
        for (int i = 0; i < sampleCount && offset + 7 < buffer.Length; i++)
        {
            // Big-endian double - need to reverse bytes
            ulong bits = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset));
            double sample = BitConverter.UInt64BitsToDouble(bits);
            output[i] = (float)sample;
            offset += 8;
        }
        
        return frameCount;
    }
    
    private int DecodeMuLaw(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 32768.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        
        for (int i = 0; i < sampleCount && i < buffer.Length; i++)
        {
            short decoded = MuLawDecode(buffer[i]);
            output[i] = decoded * scale;
        }
        
        return frameCount;
    }
    
    private int DecodeALaw(byte[] buffer, Span<float> output, int frameCount)
    {
        const float scale = 1.0f / 32768.0f;
        int sampleCount = frameCount * (int)_header.ChannelCount;
        
        for (int i = 0; i < sampleCount && i < buffer.Length; i++)
        {
            short decoded = ALawDecode(buffer[i]);
            output[i] = decoded * scale;
        }
        
        return frameCount;
    }
    
    /// <summary>
    /// Decodes a mu-law encoded byte to 16-bit linear PCM.
    /// </summary>
    private static short MuLawDecode(byte muLaw)
    {
        // Complement the mu-law value
        muLaw = (byte)~muLaw;
        
        int sign = muLaw & 0x80;
        int exponent = (muLaw >> 4) & 0x07;
        int mantissa = muLaw & 0x0F;
        
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        
        return (short)(sign != 0 ? -sample : sample);
    }
    
    /// <summary>
    /// Decodes an A-law encoded byte to 16-bit linear PCM.
    /// </summary>
    private static short ALawDecode(byte aLaw)
    {
        // XOR to undo even bit inversion
        aLaw ^= 0x55;
        
        int sign = aLaw & 0x80;
        int exponent = (aLaw >> 4) & 0x07;
        int mantissa = aLaw & 0x0F;
        
        int sample;
        if (exponent == 0)
        {
            sample = (mantissa << 4) + 8;
        }
        else
        {
            sample = ((mantissa << 4) + 0x108) << (exponent - 1);
        }
        
        return (short)(sign != 0 ? -sample : sample);
    }
    
    #endregion
    
    /// <summary>
    /// Checks if the given stream appears to be an AU file.
    /// </summary>
    public static bool CanHandle(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            return false;
        
        long originalPosition = stream.Position;
        try
        {
            byte[] magic = new byte[4];
            if (stream.Read(magic, 0, 4) < 4)
                return false;
            
            return magic[0] == '.' && magic[1] == 's' && 
                   magic[2] == 'n' && magic[3] == 'd';
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}

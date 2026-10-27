// VorbisPacketizer - Handles Vorbis audio packets from containers like MKV/WebM
// Manages header packets and audio frame extraction

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// Vorbis packetizer that prepares demuxer output for decoder consumption.
/// Handles Vorbis header packets (identification, comment, setup) and audio frames.
/// </summary>
public sealed class VorbisPacketizer : IDisposable
{
    private readonly List<AudioAccessUnit> outputQueue = new();
    
    // Header packets (required for decoder initialization)
    private byte[]? identificationHeader;
    private byte[]? commentHeader;
    private byte[]? setupHeader;
    
    // Stream info from identification header
    private int channelCount;
    private int sampleRate;
    private int blockSize0;
    private int blockSize1;
    
    // Timestamp tracking
    private long lastPts = long.MinValue;
    private long sampleDurationTicks;
    private long totalSamplesDecoded;
    
    // Previous block type for calculating sample count
    private bool previousBlockLong;
    private bool hasPreviousBlock;
    
    /// <summary>Gets the identification header data.</summary>
    public ReadOnlySpan<byte> IdentificationHeader => identificationHeader;
    
    /// <summary>Gets the comment header data.</summary>
    public ReadOnlySpan<byte> CommentHeader => commentHeader;
    
    /// <summary>Gets the setup header data.</summary>
    public ReadOnlySpan<byte> SetupHeader => setupHeader;
    
    /// <summary>Gets whether all headers have been received.</summary>
    public bool HasHeaders => identificationHeader != null && commentHeader != null && setupHeader != null;
    
    /// <summary>Gets the sample rate in Hz.</summary>
    public int SampleRate => sampleRate;
    
    /// <summary>Gets the number of audio channels.</summary>
    public int Channels => channelCount;
    
    /// <summary>Gets the short block size (samples).</summary>
    public int BlockSize0 => blockSize0;
    
    /// <summary>Gets the long block size (samples).</summary>
    public int BlockSize1 => blockSize1;
    
    /// <summary>
    /// Configures the packetizer from codec private data (MKV/WebM format).
    /// Format: [count] [header1_len] [header2_len] [header1_data...] [header2_data...] [header3_data...]
    /// </summary>
    /// <param name="codecPrivate">Codec private data from container.</param>
    public void ConfigureFromCodecPrivate(ReadOnlySpan<byte> codecPrivate)
    {
        if (codecPrivate.Length < 3)
            return;
        
        int offset = 0;
        
        // Xiph lacing: first byte is number of packets minus 1 (should be 2)
        int packetCount = codecPrivate[offset++] + 1;
        if (packetCount != 3)
            return; // Vorbis requires exactly 3 header packets
        
        // Read packet sizes (Xiph-style variable length)
        int[] packetSizes = new int[packetCount - 1]; // Last packet size is implicit
        int totalHeaderSize = 0;
        
        for (int i = 0; i < packetCount - 1; i++)
        {
            int size = 0;
            while (offset < codecPrivate.Length)
            {
                int b = codecPrivate[offset++];
                size += b;
                if (b < 255)
                    break;
            }
            packetSizes[i] = size;
            totalHeaderSize += size;
        }
        
        // Calculate third packet size
        int thirdPacketSize = codecPrivate.Length - offset - totalHeaderSize;
        if (thirdPacketSize <= 0)
            return;
        
        // Extract headers
        if (offset + packetSizes[0] <= codecPrivate.Length)
        {
            var header = codecPrivate.Slice(offset, packetSizes[0]);
            if (IsIdentificationHeader(header))
            {
                identificationHeader = header.ToArray();
                ParseIdentificationHeader(header);
            }
            offset += packetSizes[0];
        }
        
        if (offset + packetSizes[1] <= codecPrivate.Length)
        {
            var header = codecPrivate.Slice(offset, packetSizes[1]);
            if (IsCommentHeader(header))
            {
                commentHeader = header.ToArray();
            }
            offset += packetSizes[1];
        }
        
        if (offset + thirdPacketSize <= codecPrivate.Length)
        {
            var header = codecPrivate.Slice(offset, thirdPacketSize);
            if (IsSetupHeader(header))
            {
                setupHeader = header.ToArray();
            }
        }
        
        // Calculate sample duration in ticks
        if (sampleRate > 0)
        {
            sampleDurationTicks = TimeSpan.TicksPerSecond / sampleRate;
        }
    }
    
    /// <summary>
    /// Processes a Vorbis packet from the demuxer.
    /// </summary>
    /// <param name="data">Raw packet data.</param>
    /// <param name="pts">Presentation timestamp in 100-nanosecond units.</param>
    public void ProcessPacket(ReadOnlySpan<byte> data, long pts)
    {
        if (data.Length == 0)
            return;
        
        // Check if this is a header packet
        if (data.Length >= 7 && data[0] >= 1 && data[0] <= 5 && (data[0] & 1) == 1)
        {
            // Check for "vorbis" signature
            if (data[1] == 'v' && data[2] == 'o' && data[3] == 'r' && 
                data[4] == 'b' && data[5] == 'i' && data[6] == 's')
            {
                ProcessHeaderPacket(data);
                return;
            }
        }
        
        // Audio packet
        ProcessAudioPacket(data, pts);
    }
    
    /// <summary>
    /// Processes a header packet.
    /// </summary>
    private void ProcessHeaderPacket(ReadOnlySpan<byte> data)
    {
        int packetType = data[0];
        
        switch (packetType)
        {
            case 1: // Identification header
                if (IsIdentificationHeader(data))
                {
                    identificationHeader = data.ToArray();
                    ParseIdentificationHeader(data);
                }
                break;
                
            case 3: // Comment header
                if (IsCommentHeader(data))
                {
                    commentHeader = data.ToArray();
                }
                break;
                
            case 5: // Setup header
                if (IsSetupHeader(data))
                {
                    setupHeader = data.ToArray();
                }
                break;
        }
    }
    
    /// <summary>
    /// Processes an audio data packet.
    /// </summary>
    private void ProcessAudioPacket(ReadOnlySpan<byte> data, long pts)
    {
        if (!HasHeaders)
            return; // Can't process audio without headers
        
        // Determine block size from mode number
        int modeNumber = data[0] & 1; // First bit is packet type (0 = audio)
        
        // For audio packets, first bit is 0, next bits are mode number
        // We need setup header to determine number of modes
        bool isLongBlock = (data[0] & 0x02) != 0; // Simplified - actual implementation needs mode lookup
        
        // Calculate sample count from block size
        int currentBlockSize = isLongBlock ? blockSize1 : blockSize0;
        int sampleCount;
        
        if (hasPreviousBlock)
        {
            int previousBlockSize = previousBlockLong ? blockSize1 : blockSize0;
            sampleCount = (previousBlockSize + currentBlockSize) / 4;
        }
        else
        {
            sampleCount = currentBlockSize / 2;
        }
        
        previousBlockLong = isLongBlock;
        hasPreviousBlock = true;
        
        // Create access unit
        long effectivePts = pts != long.MinValue ? pts : lastPts + sampleCount * sampleDurationTicks;
        
        var accessUnit = new AudioAccessUnit
        {
            Data = data.ToArray(),
            PresentationTimestamp = effectivePts,
            DurationTicks = sampleCount * sampleDurationTicks
        };
        
        outputQueue.Add(accessUnit);
        lastPts = effectivePts;
        totalSamplesDecoded += sampleCount;
    }
    
    /// <summary>
    /// Gets the next decoded audio access unit, or null if none available.
    /// </summary>
    public AudioAccessUnit? GetNextAccessUnit()
    {
        if (outputQueue.Count == 0)
            return null;
        
        var unit = outputQueue[0];
        outputQueue.RemoveAt(0);
        return unit;
    }
    
    /// <summary>
    /// Gets all pending access units.
    /// </summary>
    public IReadOnlyList<AudioAccessUnit> GetPendingUnits() => outputQueue;
    
    /// <summary>
    /// Clears pending access units.
    /// </summary>
    public void ClearPending() => outputQueue.Clear();
    
    /// <summary>
    /// Resets the packetizer state for seeking.
    /// </summary>
    public void Reset()
    {
        outputQueue.Clear();
        lastPts = long.MinValue;
        totalSamplesDecoded = 0;
        hasPreviousBlock = false;
    }
    
    /// <summary>
    /// Checks if data is a Vorbis identification header.
    /// </summary>
    private static bool IsIdentificationHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return false;
        
        return data[0] == 0x01 && 
               data[1] == 'v' && data[2] == 'o' && data[3] == 'r' &&
               data[4] == 'b' && data[5] == 'i' && data[6] == 's';
    }
    
    /// <summary>
    /// Checks if data is a Vorbis comment header.
    /// </summary>
    private static bool IsCommentHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
            return false;
        
        return data[0] == 0x03 && 
               data[1] == 'v' && data[2] == 'o' && data[3] == 'r' &&
               data[4] == 'b' && data[5] == 'i' && data[6] == 's';
    }
    
    /// <summary>
    /// Checks if data is a Vorbis setup header.
    /// </summary>
    private static bool IsSetupHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
            return false;
        
        return data[0] == 0x05 && 
               data[1] == 'v' && data[2] == 'o' && data[3] == 'r' &&
               data[4] == 'b' && data[5] == 'i' && data[6] == 's';
    }
    
    /// <summary>
    /// Parses the identification header to extract stream parameters.
    /// </summary>
    private void ParseIdentificationHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return;
        
        // Skip magic (7 bytes) and version (4 bytes)
        int offset = 11;
        
        channelCount = data[offset++];
        sampleRate = data[offset] | (data[offset + 1] << 8) | 
                     (data[offset + 2] << 16) | (data[offset + 3] << 24);
        offset += 4;
        
        // Skip bitrates (12 bytes)
        offset += 12;
        
        // Block sizes
        int blockSizeByte = data[offset];
        int blockSize0Exp = blockSizeByte & 0x0F;
        int blockSize1Exp = (blockSizeByte >> 4) & 0x0F;
        
        blockSize0 = 1 << blockSize0Exp;
        blockSize1 = 1 << blockSize1Exp;
        
        // Calculate sample duration
        if (sampleRate > 0)
        {
            sampleDurationTicks = TimeSpan.TicksPerSecond / sampleRate;
        }
    }
    
    public void Dispose()
    {
        outputQueue.Clear();
    }
}

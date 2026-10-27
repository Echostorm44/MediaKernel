// AacPacketizer - Converts demuxer output into complete AAC audio frames
// Handles ADTS/raw format, AudioSpecificConfig management, and frame boundary detection

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// AAC packetizer that prepares demuxer output for decoder consumption.
/// Handles ADTS frames and raw AAC data from MP4/MKV containers.
/// </summary>
public sealed class AacPacketizer : IDisposable
{
    private readonly List<AudioAccessUnit> outputQueue = new();
    private byte[]? audioSpecificConfig;
    private bool isAdtsFormat;
    
    // Parsed from AudioSpecificConfig
    private int objectType;
    private int sampleRateIndex;
    private int sampleRate;
    private int channelConfiguration;
    private int samplesPerFrame = 1024; // AAC-LC default
    
    // Timestamp tracking
    private long lastPts = long.MinValue;
    private long sampleDurationTicks;
    
    /// <summary>Gets the AudioSpecificConfig data.</summary>
    public ReadOnlySpan<byte> AudioSpecificConfig => audioSpecificConfig;
    
    /// <summary>Gets the sample rate in Hz.</summary>
    public int SampleRate => sampleRate;
    
    /// <summary>Gets the number of audio channels.</summary>
    public int Channels => channelConfiguration;
    
    /// <summary>Gets the audio object type (1=AAC-Main, 2=AAC-LC, etc.).</summary>
    public int ObjectType => objectType;
    
    // Standard AAC sample rates
    private static readonly int[] SampleRates = 
    { 
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 
        16000, 12000, 11025, 8000, 7350, 0, 0, 0 
    };
    
    /// <summary>
    /// Configures the packetizer from AudioSpecificConfig (from esds box or codec private data).
    /// </summary>
    /// <param name="ascData">AudioSpecificConfig bytes (typically 2 bytes for basic AAC-LC).</param>
    public void ConfigureFromAudioSpecificConfig(ReadOnlySpan<byte> ascData)
    {
        if (ascData.Length < 2)
            return;
        
        audioSpecificConfig = ascData.ToArray();
        isAdtsFormat = false;
        
        // Parse AudioSpecificConfig
        // First 5 bits: audioObjectType
        // If 31, next 6 bits + 32
        // Next 4 bits: samplingFrequencyIndex
        // If 15, next 24 bits: samplingFrequency
        // Next 4 bits: channelConfiguration
        
        int byte0 = ascData[0];
        int byte1 = ascData[1];
        
        objectType = (byte0 >> 3) & 0x1F;
        
        if (objectType == 31)
        {
            // Extended object type
            objectType = 32 + (((byte0 & 0x07) << 3) | ((byte1 >> 5) & 0x07));
            sampleRateIndex = (byte1 >> 1) & 0x0F;
        }
        else
        {
            sampleRateIndex = ((byte0 & 0x07) << 1) | ((byte1 >> 7) & 0x01);
        }
        
        if (sampleRateIndex == 15)
        {
            // Explicit sample rate (24 bits) - need more bytes
            if (ascData.Length >= 5)
            {
                sampleRate = (ascData[1] & 0x7F) << 17 |
                             ascData[2] << 9 |
                             ascData[3] << 1 |
                             (ascData[4] >> 7);
            }
            else
            {
                sampleRate = 44100; // Default
            }
            channelConfiguration = sampleRateIndex == 15 && ascData.Length >= 5 
                ? (ascData[4] >> 3) & 0x0F 
                : (byte1 >> 3) & 0x0F;
        }
        else
        {
            sampleRate = SampleRates[sampleRateIndex];
            channelConfiguration = (byte1 >> 3) & 0x0F;
        }
        
        // Determine samples per frame based on object type
        samplesPerFrame = objectType switch
        {
            29 => 2048, // HE-AAC v2 (PS)
            5 => 2048,  // HE-AAC (SBR)
            23 => 480,  // LD
            39 => 512,  // ELD
            _ => 1024   // AAC-LC and others
        };
        
        sampleDurationTicks = (long)((double)samplesPerFrame / sampleRate * TimeSpan.TicksPerSecond);
    }
    
    /// <summary>
    /// Configures the packetizer for ADTS format (self-framing streams).
    /// </summary>
    public void ConfigureAdts()
    {
        isAdtsFormat = true;
        sampleDurationTicks = (long)((double)1024 / 44100 * TimeSpan.TicksPerSecond); // Default
    }
    
    /// <summary>
    /// Feeds raw data from the demuxer into the packetizer.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, long pts, long dts)
    {
        if (isAdtsFormat)
        {
            ParseAdtsData(data, pts);
        }
        else
        {
            // Raw AAC frame from container (MP4, MKV)
            ParseRawFrame(data, pts);
        }
    }
    
    /// <summary>
    /// Retrieves the next complete audio frame.
    /// </summary>
    public bool TryGetAccessUnit(out AudioAccessUnit accessUnit)
    {
        if (outputQueue.Count > 0)
        {
            accessUnit = outputQueue[0];
            outputQueue.RemoveAt(0);
            return true;
        }
        
        accessUnit = default;
        return false;
    }
    
    /// <summary>
    /// Resets packetizer state.
    /// </summary>
    public void Reset()
    {
        outputQueue.Clear();
        lastPts = long.MinValue;
    }
    
    private void ParseAdtsData(ReadOnlySpan<byte> data, long pts)
    {
        int offset = 0;
        
        while (offset + 7 <= data.Length)
        {
            // ADTS sync word: 0xFFF (12 bits)
            if (data[offset] != 0xFF || (data[offset + 1] & 0xF0) != 0xF0)
            {
                // Scan for sync
                offset++;
                continue;
            }
            
            // Parse ADTS header
            int byte1 = data[offset + 1];
            int byte2 = data[offset + 2];
            int byte3 = data[offset + 3];
            int byte4 = data[offset + 4];
            int byte5 = data[offset + 5];
            int byte6 = data[offset + 6];
            
            // protection_absent (1 bit at position 0 of byte1)
            bool protectionAbsent = (byte1 & 0x01) != 0;
            int headerSize = protectionAbsent ? 7 : 9; // CRC adds 2 bytes
            
            // profile (2 bits) + sampling_frequency_index (4 bits)
            objectType = ((byte2 >> 6) & 0x03) + 1; // profile + 1 = object type
            sampleRateIndex = (byte2 >> 2) & 0x0F;
            sampleRate = SampleRates[sampleRateIndex];
            
            // channel_configuration (3 bits across bytes 2 and 3)
            channelConfiguration = ((byte2 & 0x01) << 2) | ((byte3 >> 6) & 0x03);
            
            // frame_length (13 bits)
            int frameLength = ((byte3 & 0x03) << 11) | (byte4 << 3) | ((byte5 >> 5) & 0x07);
            
            if (frameLength < headerSize || offset + frameLength > data.Length)
                break;
            
            // Extract raw AAC data (skip ADTS header)
            var frameData = data.Slice(offset + headerSize, frameLength - headerSize).ToArray();
            
            // Interpolate timestamp
            if (pts < 0 && lastPts >= 0)
            {
                pts = lastPts + sampleDurationTicks;
            }
            else if (pts < 0)
            {
                pts = 0;
            }
            
            sampleDurationTicks = (long)((double)samplesPerFrame / sampleRate * TimeSpan.TicksPerSecond);
            
            outputQueue.Add(new AudioAccessUnit
            {
                Data = frameData,
                PresentationTimestamp = pts,
                DurationTicks = sampleDurationTicks,
                SampleRate = sampleRate,
                Channels = channelConfiguration,
                SamplesPerFrame = samplesPerFrame
            });
            
            lastPts = pts;
            pts += sampleDurationTicks; // For next frame
            offset += frameLength;
        }
    }
    
    private void ParseRawFrame(ReadOnlySpan<byte> data, long pts)
    {
        if (data.Length == 0)
            return;
        
        // Interpolate timestamp
        if (pts < 0 && lastPts >= 0)
        {
            pts = lastPts + sampleDurationTicks;
        }
        else if (pts < 0)
        {
            pts = 0;
        }
        
        outputQueue.Add(new AudioAccessUnit
        {
            Data = data.ToArray(),
            PresentationTimestamp = pts,
            DurationTicks = sampleDurationTicks,
            SampleRate = sampleRate,
            Channels = channelConfiguration,
            SamplesPerFrame = samplesPerFrame
        });
        
        lastPts = pts;
    }
    
    /// <summary>
    /// Generates an ADTS header for raw AAC data.
    /// </summary>
    public static byte[] GenerateAdtsHeader(int frameLength, int objectType, int sampleRateIndex, int channels)
    {
        int fullFrameLength = frameLength + 7; // Include header
        
        var header = new byte[7];
        
        // Sync word (12 bits) + MPEG version (1 bit) + Layer (2 bits) + protection_absent (1 bit)
        header[0] = 0xFF;
        header[1] = 0xF1; // MPEG-4, Layer 0, no CRC
        
        // Profile (2 bits) + sampling_frequency_index (4 bits) + private_bit (1 bit) + channel_configuration (1 bit)
        header[2] = (byte)(((objectType - 1) << 6) | (sampleRateIndex << 2) | ((channels >> 2) & 0x01));
        
        // channel_configuration (2 bits) + original_copy (1 bit) + home (1 bit) + copyright_id_bit (1 bit) + copyright_id_start (1 bit) + frame_length (2 bits)
        header[3] = (byte)(((channels & 0x03) << 6) | ((fullFrameLength >> 11) & 0x03));
        
        // frame_length (8 bits)
        header[4] = (byte)((fullFrameLength >> 3) & 0xFF);
        
        // frame_length (3 bits) + adts_buffer_fullness (5 bits)
        header[5] = (byte)(((fullFrameLength & 0x07) << 5) | 0x1F);
        
        // adts_buffer_fullness (6 bits) + number_of_raw_data_blocks_in_frame (2 bits)
        header[6] = 0xFC;
        
        return header;
    }
    
    public void Dispose()
    {
        outputQueue.Clear();
    }
}

/// <summary>
/// A complete AAC audio frame ready for decoding.
/// </summary>
public struct AudioAccessUnit
{
    /// <summary>Raw AAC frame data (without ADTS header).</summary>
    public byte[] Data;
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestamp;
    
    /// <summary>Duration in ticks.</summary>
    public long DurationTicks;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate;
    
    /// <summary>Number of channels.</summary>
    public int Channels;
    
    /// <summary>Number of samples in this frame.</summary>
    public int SamplesPerFrame;
}

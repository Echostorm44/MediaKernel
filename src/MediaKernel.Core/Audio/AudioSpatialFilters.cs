// MediaKernel - Audio Spatial Effects
// Phaser, Headphone Virtualization, Dolby Surround Decoding
// Based on VLC's spatializer and headphone modules

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Phaser effect using allpass filters with modulated coefficients.
/// Creates sweeping, jet-like tonal effects.
/// </summary>
public sealed class PhaserFilter
{
    private readonly int sampleRate;
    private readonly float[] allpassDelays;
    private readonly float[] allpassOutputs;
    private double lfoPhase;
    
    private float rate = 0.5f;
    private float depth = 0.7f;
    private float feedback = 0.3f;
    private float wetLevel = 0.5f;
    private float dryLevel = 0.8f;
    private int stages = 6;
    private float lastOutput;
    
    /// <summary>LFO rate in Hz (sweep speed).</summary>
    public float Rate
    {
        get => rate;
        set => rate = Math.Clamp(value, 0.01f, 10f);
    }
    
    /// <summary>Modulation depth from 0 to 1.</summary>
    public float Depth
    {
        get => depth;
        set => depth = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>Feedback amount from -0.9 to 0.9.</summary>
    public float Feedback
    {
        get => feedback;
        set => feedback = Math.Clamp(value, -0.9f, 0.9f);
    }
    
    /// <summary>Wet (effected) signal level.</summary>
    public float WetLevel
    {
        get => wetLevel;
        set => wetLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>Dry (original) signal level.</summary>
    public float DryLevel
    {
        get => dryLevel;
        set => dryLevel = Math.Clamp(value, 0f, 1f);
    }
    
    /// <summary>Number of allpass stages (2-12, even numbers). More = deeper effect.</summary>
    public int Stages
    {
        get => stages;
        set => stages = Math.Clamp(value & ~1, 2, 12); // Force even
    }
    
    public PhaserFilter(int sampleRate = 44100)
    {
        this.sampleRate = sampleRate;
        allpassDelays = new float[12]; // Max stages
        allpassOutputs = new float[12];
        lfoPhase = 0;
        lastOutput = 0;
    }
    
    public void ProcessMono(Span<float> samples)
    {
        double phaseIncrement = 2 * Math.PI * rate / sampleRate;
        
        // Frequency range for allpass filter (100Hz to 4kHz)
        const float minFreq = 100f;
        const float maxFreq = 4000f;
        
        for (int i = 0; i < samples.Length; i++)
        {
            // Calculate LFO value (0 to 1)
            float lfoValue = (float)(Math.Sin(lfoPhase) * 0.5 + 0.5) * depth;
            float frequency = minFreq + lfoValue * (maxFreq - minFreq);
            
            // Calculate allpass coefficient for this frequency
            float coefficient = (1f - MathF.Tan(MathF.PI * frequency / sampleRate)) /
                               (1f + MathF.Tan(MathF.PI * frequency / sampleRate));
            
            // Input with feedback
            float input = samples[i] + lastOutput * feedback;
            float output = input;
            
            // Process through allpass stages
            for (int stage = 0; stage < stages; stage++)
            {
                float delayed = allpassDelays[stage];
                float filtered = coefficient * (output - allpassOutputs[stage]) + delayed;
                
                allpassDelays[stage] = output;
                allpassOutputs[stage] = filtered;
                output = filtered;
            }
            
            lastOutput = output;
            
            // Mix wet and dry
            samples[i] = samples[i] * dryLevel + output * wetLevel;
            
            lfoPhase += phaseIncrement;
            if (lfoPhase >= 2 * Math.PI) lfoPhase -= 2 * Math.PI;
        }
    }
    
    public void ProcessStereo(Span<float> samples)
    {
        // Process as mono (sum) then apply to both channels for simplicity
        // More sophisticated would use two independent phasers with offset LFO
        int pairs = samples.Length / 2;
        Span<float> mono = stackalloc float[pairs];
        
        for (int i = 0; i < pairs; i++)
            mono[i] = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
        
        ProcessMono(mono);
        
        for (int i = 0; i < pairs; i++)
        {
            float wet = mono[i] * wetLevel;
            samples[i * 2] = samples[i * 2] * dryLevel + wet;
            samples[i * 2 + 1] = samples[i * 2 + 1] * dryLevel + wet;
        }
    }
    
    public void Reset()
    {
        Array.Clear(allpassDelays);
        Array.Clear(allpassOutputs);
        lfoPhase = 0;
        lastOutput = 0;
    }
}

// Note: KaraokeFilter, HeadphoneVirtualizer, DolbySurroundDecoder, and ChannelRemapper
// are defined in StereoFilters.cs and ChannelDownmixer.cs

/// <summary>
/// S/PDIF passthrough encapsulator for compressed audio.
/// Wraps AC3/DTS bitstreams in IEC 61937 format for digital output.
/// </summary>
public sealed class SpdifEncapsulator
{
    // IEC 61937 burst preamble
    private const ushort SyncWord1 = 0xF872; // Pa
    private const ushort SyncWord2 = 0x4E1F; // Pb
    
    // Data type codes
    public const byte DataTypeAc3 = 0x01;
    public const byte DataTypeDts1 = 0x0B; // DTS Type I (512 samples)
    public const byte DataTypeDts2 = 0x0C; // DTS Type II (1024 samples)
    public const byte DataTypeDts3 = 0x0D; // DTS Type III (2048 samples)
    public const byte DataTypeTrueHd = 0x16;
    public const byte DataTypeEAc3 = 0x15;
    
    private readonly byte[] burstBuffer;
    
    /// <summary>Creates an S/PDIF encapsulator.</summary>
    public SpdifEncapsulator()
    {
        // Maximum S/PDIF burst size is 6144 bytes (for AC3) or 32768 for HD formats
        burstBuffer = new byte[6144];
    }
    
    /// <summary>
    /// Encapsulates compressed audio frame in IEC 61937 burst format.
    /// </summary>
    /// <param name="compressedFrame">Compressed AC3/DTS/etc. frame data.</param>
    /// <param name="dataType">Data type code (AC3, DTS, etc.).</param>
    /// <param name="output">Output buffer for encapsulated data.</param>
    /// <returns>Number of bytes written to output.</returns>
    public int EncapsulateBurst(ReadOnlySpan<byte> compressedFrame, byte dataType, Span<byte> output)
    {
        int frameSize = compressedFrame.Length;
        
        // Determine burst size based on data type
        int burstSize = dataType switch
        {
            DataTypeAc3 => 6144,     // 1536 samples * 2 ch * 2 bytes
            DataTypeDts1 => 2048,    // 512 samples
            DataTypeDts2 => 4096,    // 1024 samples
            DataTypeDts3 => 8192,    // 2048 samples
            _ => 6144
        };
        
        if (output.Length < burstSize)
            return 0;
        
        // Clear output
        output[..burstSize].Clear();
        
        int pos = 0;
        
        // Write preamble (little-endian for S/PDIF)
        output[pos++] = (byte)(SyncWord1 & 0xFF);
        output[pos++] = (byte)(SyncWord1 >> 8);
        output[pos++] = (byte)(SyncWord2 & 0xFF);
        output[pos++] = (byte)(SyncWord2 >> 8);
        
        // Write burst info (Pc)
        ushort burstInfo = dataType;
        output[pos++] = (byte)(burstInfo & 0xFF);
        output[pos++] = (byte)(burstInfo >> 8);
        
        // Write payload length in bits (Pd)
        ushort lengthBits = (ushort)(frameSize * 8);
        output[pos++] = (byte)(lengthBits & 0xFF);
        output[pos++] = (byte)(lengthBits >> 8);
        
        // Copy and byte-swap payload (S/PDIF uses big-endian sample pairs)
        for (int i = 0; i < frameSize - 1; i += 2)
        {
            output[pos + i] = compressedFrame[i + 1];
            output[pos + i + 1] = compressedFrame[i];
        }
        
        // Handle odd byte
        if (frameSize % 2 == 1)
        {
            output[pos + frameSize - 1] = 0;
            output[pos + frameSize] = compressedFrame[frameSize - 1];
        }
        
        return burstSize;
    }
    
    /// <summary>
    /// Determines the appropriate data type for AC3 frame.
    /// </summary>
    public static byte GetAc3DataType(ReadOnlySpan<byte> frame)
    {
        return DataTypeAc3;
    }
    
    /// <summary>
    /// Determines the appropriate data type for DTS frame based on frame size.
    /// </summary>
    public static byte GetDtsDataType(int frameSamples)
    {
        return frameSamples switch
        {
            <= 512 => DataTypeDts1,
            <= 1024 => DataTypeDts2,
            _ => DataTypeDts3
        };
    }
}

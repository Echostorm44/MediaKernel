using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MediaKernel.Core.Audio.PulseAudio.PulseAudioInterop;

namespace MediaKernel.Core.Audio.PulseAudio;

/// <summary>
/// PulseAudio audio output implementation for Linux.
/// 
/// Uses the pa_simple API for synchronous, straightforward audio playback.
/// Target buffer duration: ~40ms for responsive A/V sync.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PulseAudioOutput : IAudioOutput
{
    private const int TargetBufferMilliseconds = 40;
    private const string ApplicationName = "MediaKernel";

    // PulseAudio connection handle
    private nint connection;

    // Format info
    private AudioFormat format;
    private pa_sample_spec sampleSpec;
    private int bytesPerFrame;

    // State
    private volatile bool isInitialized;
    private volatile bool isStarted;
    private volatile bool isPaused;
    private bool disposed;

    // Statistics
    private AudioOutputStatistics statistics;
    private long totalBytesWritten;

    // Volume (applied in software - pa_simple doesn't support volume)
    private float volume = 1.0f;
    private bool isMuted;

    // Device list (PulseAudio handles device selection via pa_simple_new)
    private readonly List<AudioOutputDevice> devices = [];

    /// <inheritdoc/>
    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <inheritdoc/>
    public bool IsMuted
    {
        get => isMuted;
        set => isMuted = value;
    }

    /// <inheritdoc/>
    public bool Initialize(AudioFormat audioFormat)
    {
        if (isInitialized)
            return true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        format = audioFormat;
        bytesPerFrame = format.BytesPerFrame;

        // Map sample format to PulseAudio format
        int paFormat = format.SampleFormat switch
        {
            AudioSampleFormat.UInt8 => PA_SAMPLE_U8,
            AudioSampleFormat.Int16 => BitConverter.IsLittleEndian ? PA_SAMPLE_S16LE : PA_SAMPLE_S16BE,
            AudioSampleFormat.Int24 => BitConverter.IsLittleEndian ? PA_SAMPLE_S24LE : PA_SAMPLE_S24BE,
            AudioSampleFormat.Int32 => BitConverter.IsLittleEndian ? PA_SAMPLE_S32LE : PA_SAMPLE_S32BE,
            AudioSampleFormat.Float32 => BitConverter.IsLittleEndian ? PA_SAMPLE_FLOAT32LE : PA_SAMPLE_FLOAT32BE,
            _ => PA_SAMPLE_S16LE
        };

        // Build sample spec
        sampleSpec = new pa_sample_spec
        {
            format = paFormat,
            rate = (uint)format.SampleRate,
            channels = (byte)format.ChannelCount
        };

        // Calculate buffer attributes for low latency
        uint bytesPerSecond = (uint)(format.SampleRate * bytesPerFrame);
        uint targetBytes = (uint)(bytesPerSecond * TargetBufferMilliseconds / 1000);
        uint minReq = targetBytes / 4; // Request size

        var bufferAttr = new pa_buffer_attr
        {
            maxlength = uint.MaxValue,  // Let server decide
            tlength = targetBytes,
            prebuf = minReq,
            minreq = minReq,
            fragsize = uint.MaxValue    // Not used for playback
        };

        try
        {
            // Create connection with buffer attributes
            connection = pa_simple_new_with_attr(
                0,                          // Default server
                ApplicationName,            // App name
                PA_STREAM_PLAYBACK,         // Playback direction
                0,                          // Default sink
                "Audio Output",             // Stream name
                in sampleSpec,
                0,                          // Default channel map
                in bufferAttr,
                out int error);

            if (connection == 0)
            {
                Console.Error.WriteLine($"PulseAudio: Failed to connect - {GetErrorString(error)}");
                return false;
            }

            // Populate device list with default device
            devices.Clear();
            devices.Add(new AudioOutputDevice { DeviceId = "default", DisplayName = "Default Audio Output", IsDefault = true });

            isInitialized = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"PulseAudio: Library not found - {ex.Message}");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.Error.WriteLine($"PulseAudio: Entry point not found - {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (!isInitialized || isStarted)
            return;

        // pa_simple starts playback on first write
        isStarted = true;
        isPaused = false;
        totalBytesWritten = 0;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (!isInitialized || !isStarted)
            return;

        // Drain remaining audio
        if (connection != 0)
        {
            _ = pa_simple_drain(connection, out _);
        }

        isStarted = false;
        isPaused = false;
    }

    /// <inheritdoc/>
    public void Pause()
    {
        if (!isInitialized || !isStarted)
            return;

        // pa_simple doesn't have native pause - we just stop accepting writes
        isPaused = true;
    }

    /// <inheritdoc/>
    public void Resume()
    {
        if (!isInitialized || !isStarted)
            return;

        isPaused = false;
    }

    /// <inheritdoc/>
    public int Write(in AudioBuffer buffer)
    {
        if (!isInitialized || !isStarted || isPaused || connection == 0)
            return 0;

        if (buffer.DataPointer == 0 || buffer.FrameCount == 0)
            return 0;

        int frameCount = buffer.FrameCount;
        int byteCount = frameCount * bytesPerFrame;

        // Get source data as span
        ReadOnlySpan<byte> sourceData;
        unsafe
        {
            sourceData = new ReadOnlySpan<byte>((void*)buffer.DataPointer, byteCount);
        }

        // Apply volume/mute in software if needed
        byte[]? tempBuffer = null;
        Span<byte> writeData;

        if (isMuted)
        {
            // Write silence
            tempBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            writeData = tempBuffer.AsSpan(0, byteCount);
            writeData.Clear();
        }
        else if (volume < 0.999f)
        {
            // Apply volume scaling
            tempBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            writeData = tempBuffer.AsSpan(0, byteCount);
            ApplyVolume(sourceData, writeData, volume, format.SampleFormat);
        }
        else
        {
            // Direct write - need to copy to get a pinnable buffer
            tempBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            sourceData.CopyTo(tempBuffer);
            writeData = tempBuffer.AsSpan(0, byteCount);
        }

        int result;
        unsafe
        {
            fixed (byte* ptr = writeData)
            {
                result = pa_simple_write(connection, (nint)ptr, (nuint)writeData.Length, out int error);

                if (result < 0)
                {
                    Console.Error.WriteLine($"PulseAudio: Write error - {GetErrorString(error)}");
                    statistics.FramesDropped++;
                }
            }
        }

        if (tempBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(tempBuffer);
        }

        if (result >= 0)
        {
            totalBytesWritten += byteCount;
            statistics.FramesWritten += frameCount;
            return frameCount;
        }

        return 0;
    }

    /// <inheritdoc/>
    public void Flush()
    {
        if (!isInitialized || connection == 0)
            return;

        _ = pa_simple_flush(connection, out _);
    }

    /// <inheritdoc/>
    public long GetPositionMicroseconds()
    {
        if (!isInitialized || !isStarted)
            return 0;

        // Calculate position from bytes written minus latency
        long latencyUs = GetLatencyMicroseconds();
        long writtenUs = (long)(totalBytesWritten * 1_000_000L / (format.SampleRate * bytesPerFrame));
        return Math.Max(0, writtenUs - latencyUs);
    }

    /// <inheritdoc/>
    public long GetLatencyMicroseconds()
    {
        if (!isInitialized || connection == 0)
            return 0;

        ulong latency = pa_simple_get_latency(connection, out int error);
        if (error != PA_OK)
            return 0;

        return (long)latency;
    }

    /// <inheritdoc/>
    public AudioOutputStatistics GetStatistics()
    {
        if (!isInitialized || connection == 0)
            return statistics;

        // Update latency in statistics
        statistics.LatencyMicroseconds = GetLatencyMicroseconds();
        return statistics;
    }

    /// <inheritdoc/>
    public ReadOnlySpan<AudioOutputDevice> GetDevices()
    {
        // pa_simple API doesn't enumerate devices - just return default
        if (devices.Count == 0)
        {
            devices.Add(new AudioOutputDevice { DeviceId = "default", DisplayName = "Default Audio Output", IsDefault = true });
        }
        return CollectionsMarshal.AsSpan(devices);
    }

    /// <inheritdoc/>
    public bool SetDevice(string deviceId)
    {
        // pa_simple uses system default; changing device requires reconnection
        // For full device selection, use the async API (pa_context, pa_stream)
        return deviceId == "default";
    }

    /// <summary>Applies volume scaling to audio data.</summary>
    private static void ApplyVolume(ReadOnlySpan<byte> source, Span<byte> dest, float vol, AudioSampleFormat sampleFormat)
    {
        switch (sampleFormat)
        {
            case AudioSampleFormat.Float32:
            {
                var srcFloats = MemoryMarshal.Cast<byte, float>(source);
                var dstFloats = MemoryMarshal.Cast<byte, float>(dest);
                for (int i = 0; i < srcFloats.Length; i++)
                {
                    dstFloats[i] = srcFloats[i] * vol;
                }
                break;
            }
            case AudioSampleFormat.Int16:
            {
                var srcShorts = MemoryMarshal.Cast<byte, short>(source);
                var dstShorts = MemoryMarshal.Cast<byte, short>(dest);
                for (int i = 0; i < srcShorts.Length; i++)
                {
                    dstShorts[i] = (short)(srcShorts[i] * vol);
                }
                break;
            }
            case AudioSampleFormat.Int32:
            {
                var srcInts = MemoryMarshal.Cast<byte, int>(source);
                var dstInts = MemoryMarshal.Cast<byte, int>(dest);
                for (int i = 0; i < srcInts.Length; i++)
                {
                    dstInts[i] = (int)(srcInts[i] * vol);
                }
                break;
            }
            default:
                // Fallback - just copy
                source.CopyTo(dest);
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        if (connection != 0)
        {
            pa_simple_free(connection);
            connection = 0;
        }

        isInitialized = false;
        isStarted = false;
    }
}

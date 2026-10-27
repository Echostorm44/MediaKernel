using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using static MediaKernel.Core.Audio.CoreAudio.CoreAudioInterop;

namespace MediaKernel.Core.Audio.CoreAudio;

/// <summary>
/// Core Audio output implementation for macOS.
/// 
/// Uses AudioUnit (kAudioUnitSubType_DefaultOutput) with render callback
/// for low-latency audio playback. Target buffer: ~10ms per callback.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutput : IAudioOutput
{
    // Buffer configuration
    private const int RingBufferSizeMs = 200;  // 200ms ring buffer
    private const int TargetLatencyMs = 40;     // Target latency

    // AudioUnit handle
    private nint audioUnit;

    // Format info
    private AudioFormat format;
    private AudioStreamBasicDescription asbd;
    private int bytesPerFrame;

    // Ring buffer for async rendering
    private byte[] ringBuffer = [];
    private volatile int readPos;
    private volatile int writePos;
    private int ringBufferSize;
    private readonly object bufferLock = new();

    // Render callback (must be kept alive to prevent GC)
    private AURenderCallback? renderCallback;
    private GCHandle callbackHandle;

    // State
    private volatile bool isInitialized;
    private volatile bool isStarted;
    private volatile bool isPaused;
    private bool disposed;

    // Statistics
    private AudioOutputStatistics statistics;
    private long totalFramesWritten;

    // Volume
    private float volume = 1.0f;
    private bool isMuted;

    // Device list
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

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        format = audioFormat;
        bytesPerFrame = format.BytesPerFrame;

        // Map sample format to CoreAudio format
        bool isFloat = format.SampleFormat == AudioSampleFormat.Float32 || format.SampleFormat == AudioSampleFormat.Float64;
        int bitsPerSample = format.BytesPerSample * 8;

        // Create AudioStreamBasicDescription
        asbd = CreateASBD(format.SampleRate, format.ChannelCount, bitsPerSample, isFloat);

        // Allocate ring buffer
        ringBufferSize = format.SampleRate * bytesPerFrame * RingBufferSizeMs / 1000;
        ringBuffer = new byte[ringBufferSize];
        readPos = 0;
        writePos = 0;

        try
        {
            // Find default output component
            var desc = new AudioComponentDescription
            {
                componentType = kAudioUnitType_Output,
                componentSubType = kAudioUnitSubType_DefaultOutput,
                componentManufacturer = kAudioUnitManufacturer_Apple,
                componentFlags = 0,
                componentFlagsMask = 0
            };

            var component = AudioComponentFindNext(0, in desc);
            if (component == 0)
            {
                Console.Error.WriteLine("CoreAudio: Could not find default output component");
                return false;
            }

            // Create audio unit instance
            int status = AudioComponentInstanceNew(component, out audioUnit);
            if (status != 0)
            {
                Console.Error.WriteLine($"CoreAudio: Failed to create audio unit - {GetErrorString(status)}");
                return false;
            }

            // Set stream format
            unsafe
            {
                fixed (AudioStreamBasicDescription* asbdPtr = &asbd)
                {
                    status = AudioUnitSetProperty(
                        audioUnit,
                        kAudioUnitProperty_StreamFormat,
                        kAudioUnitScope_Input,
                        0,  // Output bus 0
                        (nint)asbdPtr,
                        (uint)sizeof(AudioStreamBasicDescription));
                }
            }

            if (status != 0)
            {
                Console.Error.WriteLine($"CoreAudio: Failed to set stream format - {GetErrorString(status)}");
                DisposeAudioUnit();
                return false;
            }

            // Set render callback
            renderCallback = RenderCallback;
            callbackHandle = GCHandle.Alloc(this);

            var callbackStruct = new AURenderCallbackStruct
            {
                inputProc = Marshal.GetFunctionPointerForDelegate(renderCallback),
                inputProcRefCon = GCHandle.ToIntPtr(callbackHandle)
            };

            unsafe
            {
                status = AudioUnitSetProperty(
                    audioUnit,
                    kAudioUnitProperty_SetRenderCallback,
                    kAudioUnitScope_Input,
                    0,
                    (nint)(&callbackStruct),
                    (uint)sizeof(AURenderCallbackStruct));
            }

            if (status != 0)
            {
                Console.Error.WriteLine($"CoreAudio: Failed to set render callback - {GetErrorString(status)}");
                DisposeAudioUnit();
                return false;
            }

            // Initialize audio unit
            status = AudioUnitInitialize(audioUnit);
            if (status != 0)
            {
                Console.Error.WriteLine($"CoreAudio: Failed to initialize audio unit - {GetErrorString(status)}");
                DisposeAudioUnit();
                return false;
            }

            // Populate device list
            devices.Clear();
            devices.Add(new AudioOutputDevice { DeviceId = "default", DisplayName = "Default Audio Output", IsDefault = true });

            isInitialized = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"CoreAudio: Framework not found - {ex.Message}");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.Error.WriteLine($"CoreAudio: Entry point not found - {ex.Message}");
            return false;
        }
    }

    /// <summary>Render callback invoked by Core Audio when it needs audio data.</summary>
    private static int RenderCallback(
        nint inRefCon,
        ref uint ioActionFlags,
        ref AudioTimeStamp inTimeStamp,
        uint inBusNumber,
        uint inNumberFrames,
        nint ioData)
    {
        if (inRefCon == 0 || ioData == 0)
            return 0;

        try
        {
            var handle = GCHandle.FromIntPtr(inRefCon);
            if (!handle.IsAllocated)
                return 0;

            var output = (CoreAudioOutput?)handle.Target;
            output?.FillBuffer(ioData, (int)inNumberFrames);
        }
        catch
        {
            // Never throw in callback
        }

        return 0;
    }

    /// <summary>Fills the audio buffer from the ring buffer.</summary>
    private void FillBuffer(nint bufferListPtr, int frameCount)
    {
        if (isPaused || isMuted)
        {
            // Fill with silence
            FillWithSilence(bufferListPtr, frameCount);
            return;
        }

        int bytesNeeded = frameCount * bytesPerFrame;

        lock (bufferLock)
        {
            int available = GetAvailableBytes();

            if (available < bytesNeeded)
            {
                // Underrun - fill what we have, then silence
                statistics.FramesDropped++;

                if (available > 0)
                {
                    WriteToBufferList(bufferListPtr, available);
                }

                // Fill remainder with silence
                int silenceBytes = bytesNeeded - available;
                FillWithSilence(bufferListPtr, silenceBytes / bytesPerFrame, available);
                return;
            }

            WriteToBufferList(bufferListPtr, bytesNeeded);
        }
    }

    /// <summary>Writes data from ring buffer to AudioBufferList.</summary>
    private void WriteToBufferList(nint bufferListPtr, int byteCount)
    {
        // AudioBufferList: uint mNumberBuffers, then AudioBuffer[]
        int numBuffers = Marshal.ReadInt32(bufferListPtr);
        int offset = 4; // Skip mNumberBuffers

        for (int i = 0; i < numBuffers; i++)
        {
            nint bufferPtr = bufferListPtr + offset;
            // AudioBuffer: uint mNumberChannels, uint mDataByteSize, IntPtr mData
            nint dataPtr = Marshal.ReadIntPtr(bufferPtr + 8);
            int dataSize = Marshal.ReadInt32(bufferPtr + 4);

            if (dataPtr != 0 && dataSize > 0)
            {
                int toCopy = Math.Min(byteCount, dataSize);

                // Copy from ring buffer with potential wrap
                int firstChunk = Math.Min(toCopy, ringBufferSize - readPos);
                Marshal.Copy(ringBuffer, readPos, dataPtr, firstChunk);
                readPos = (readPos + firstChunk) % ringBufferSize;

                if (firstChunk < toCopy)
                {
                    int secondChunk = toCopy - firstChunk;
                    Marshal.Copy(ringBuffer, 0, dataPtr + firstChunk, secondChunk);
                    readPos = secondChunk;
                }

                // Apply volume if needed
                if (volume < 0.999f)
                {
                    ApplyVolumeToBuffer(dataPtr, toCopy);
                }
            }

            offset += 16; // sizeof(AudioBuffer)
        }
    }

    /// <summary>Applies volume to buffer data in-place.</summary>
    private unsafe void ApplyVolumeToBuffer(nint dataPtr, int byteCount)
    {
        bool isFloat = format.SampleFormat == AudioSampleFormat.Float32 || format.SampleFormat == AudioSampleFormat.Float64;

        if (isFloat)
        {
            float* floats = (float*)dataPtr;
            int count = byteCount / 4;
            for (int i = 0; i < count; i++)
            {
                floats[i] *= volume;
            }
        }
        else if (format.BytesPerSample == 2)
        {
            short* shorts = (short*)dataPtr;
            int count = byteCount / 2;
            for (int i = 0; i < count; i++)
            {
                shorts[i] = (short)(shorts[i] * volume);
            }
        }
    }

    /// <summary>Fills AudioBufferList with silence.</summary>
    private void FillWithSilence(nint bufferListPtr, int frameCount, int offset = 0)
    {
        int numBuffers = Marshal.ReadInt32(bufferListPtr);
        int bufferOffset = 4;

        for (int i = 0; i < numBuffers; i++)
        {
            nint bufferPtr = bufferListPtr + bufferOffset;
            nint dataPtr = Marshal.ReadIntPtr(bufferPtr + 8);
            int dataSize = Marshal.ReadInt32(bufferPtr + 4);

            if (dataPtr != 0 && dataSize > 0)
            {
                int silenceBytes = Math.Min(frameCount * bytesPerFrame, dataSize - offset);
                unsafe
                {
                    new Span<byte>((void*)(dataPtr + offset), silenceBytes).Clear();
                }
            }

            bufferOffset += 16;
        }
    }

    /// <summary>Gets bytes available to read from ring buffer.</summary>
    private int GetAvailableBytes()
    {
        int write = writePos;
        int read = readPos;

        if (write >= read)
            return write - read;
        else
            return ringBufferSize - read + write;
    }

    /// <summary>Gets free space in ring buffer.</summary>
    private int GetFreeBytes()
    {
        return ringBufferSize - GetAvailableBytes() - 1; // -1 to distinguish full from empty
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (!isInitialized || isStarted || audioUnit == 0)
            return;

        int status = AudioOutputUnitStart(audioUnit);
        if (status != 0)
        {
            Console.Error.WriteLine($"CoreAudio: Failed to start - {GetErrorString(status)}");
            return;
        }

        isStarted = true;
        isPaused = false;
        totalFramesWritten = 0;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (!isInitialized || !isStarted || audioUnit == 0)
            return;

        int status = AudioOutputUnitStop(audioUnit);
        if (status != 0)
        {
            Console.Error.WriteLine($"CoreAudio: Failed to stop - {GetErrorString(status)}");
        }

        isStarted = false;
        isPaused = false;

        // Clear ring buffer
        lock (bufferLock)
        {
            readPos = 0;
            writePos = 0;
        }
    }

    /// <inheritdoc/>
    public void Pause()
    {
        if (!isInitialized || !isStarted)
            return;

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
        if (!isInitialized || !isStarted || buffer.DataPointer == 0 || buffer.FrameCount == 0)
            return 0;

        int bytesToWrite = buffer.SizeBytes;
        int frameCount = buffer.FrameCount;

        // Get source data as span
        ReadOnlySpan<byte> sourceData;
        unsafe
        {
            sourceData = new ReadOnlySpan<byte>((void*)buffer.DataPointer, bytesToWrite);
        }

        lock (bufferLock)
        {
            int freeSpace = GetFreeBytes();
            if (bytesToWrite > freeSpace)
            {
                // Buffer full - write what we can
                bytesToWrite = freeSpace / bytesPerFrame * bytesPerFrame;
                if (bytesToWrite == 0)
                    return 0;
            }

            // Write to ring buffer with potential wrap
            int firstChunk = Math.Min(bytesToWrite, ringBufferSize - writePos);
            sourceData[..firstChunk].CopyTo(ringBuffer.AsSpan(writePos));
            writePos = (writePos + firstChunk) % ringBufferSize;

            if (firstChunk < bytesToWrite)
            {
                int secondChunk = bytesToWrite - firstChunk;
                sourceData.Slice(firstChunk, secondChunk).CopyTo(ringBuffer.AsSpan(0));
                writePos = secondChunk;
            }

            int framesWritten = bytesToWrite / bytesPerFrame;
            totalFramesWritten += framesWritten;
            statistics.FramesWritten += framesWritten;

            return framesWritten;
        }
    }

    /// <inheritdoc/>
    public void Flush()
    {
        lock (bufferLock)
        {
            readPos = 0;
            writePos = 0;
        }
    }

    /// <inheritdoc/>
    public long GetPositionMicroseconds()
    {
        if (!isInitialized || !isStarted)
            return 0;

        long latencyUs = GetLatencyMicroseconds();
        long writtenUs = totalFramesWritten * 1_000_000L / format.SampleRate;
        return Math.Max(0, writtenUs - latencyUs);
    }

    /// <inheritdoc/>
    public long GetLatencyMicroseconds()
    {
        if (!isInitialized || audioUnit == 0)
            return 0;

        // Get audio unit latency
        double latency = 0;
        uint size = sizeof(double);
        unsafe
        {
            _ = AudioUnitGetProperty(
                audioUnit,
                kAudioUnitProperty_Latency,
                kAudioUnitScope_Global,
                0,
                (nint)(&latency),
                ref size);
        }

        // Add ring buffer latency
        int bufferBytes = GetAvailableBytes();
        long bufferUs = (long)bufferBytes * 1_000_000L / (format.SampleRate * bytesPerFrame);

        return (long)(latency * 1_000_000) + bufferUs;
    }

    /// <inheritdoc/>
    public AudioOutputStatistics GetStatistics()
    {
        statistics.LatencyMicroseconds = GetLatencyMicroseconds();
        return statistics;
    }

    /// <inheritdoc/>
    public ReadOnlySpan<AudioOutputDevice> GetDevices()
    {
        if (devices.Count == 0)
        {
            devices.Add(new AudioOutputDevice { DeviceId = "default", DisplayName = "Default Audio Output", IsDefault = true });
        }
        return CollectionsMarshal.AsSpan(devices);
    }

    /// <inheritdoc/>
    public bool SetDevice(string deviceId)
    {
        // Default output only - full device enumeration requires AudioHardware API
        return deviceId == "default";
    }

    private void DisposeAudioUnit()
    {
        if (audioUnit != 0)
        {
            AudioUnitUninitialize(audioUnit);
            AudioComponentInstanceDispose(audioUnit);
            audioUnit = 0;
        }

        if (callbackHandle.IsAllocated)
        {
            callbackHandle.Free();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        if (isStarted)
        {
            Stop();
        }

        DisposeAudioUnit();
        renderCallback = null;
        isInitialized = false;
    }
}

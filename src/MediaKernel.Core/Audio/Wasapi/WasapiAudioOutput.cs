using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MediaKernel.Core.Audio.Wasapi
{
    /// <summary>
    /// WASAPI audio output implementation for Windows.
    /// 
    /// Uses shared mode with event-driven callbacks for low-latency playback.
    /// Target buffer size: ~40ms for responsive A/V sync.
    /// </summary>
    public sealed class WasapiAudioOutput : IAudioOutput
    {
        // Target buffer duration (40ms for low latency)
        private const int TargetBufferMilliseconds = 40;
        
        // COM interface pointers
        private nint deviceEnumerator;
        private nint audioDevice;
        private nint audioClient;
        private nint renderClient;
        private nint audioClock;
        
        // Format and buffer info
        private AudioFormat format;
        private WaveFormatExtensible waveFormat;
        private uint bufferFrameCount;
        private int bytesPerFrame;
        
        // Clock tracking
        private ulong clockFrequency;
        private long streamLatencyRefTime;
        
        // State
        private volatile bool isInitialized;
        private volatile bool isStarted;
        private volatile bool isPaused;
        private bool disposed;
        
        // Statistics
        private AudioOutputStatistics statistics;
        
        // Volume
        private float volume = 1.0f;
        private bool isMuted;
        
        // Device list cache
        private List<AudioOutputDevice> cachedDevices = [];

        /// <summary>
        /// Creates a new WASAPI audio output.
        /// </summary>
        public WasapiAudioOutput()
        {
            // Initialize COM for this thread
            WasapiInterop.CoInitializeEx(0, WasapiInterop.COINIT_MULTITHREADED);
        }

        /// <inheritdoc/>
        public bool Initialize(AudioFormat audioFormat)
        {
            if (isInitialized)
                return true;
            
            format = audioFormat;
            bytesPerFrame = format.BytesPerFrame;
            waveFormat = WaveFormatExtensible.Create(format);
            
            try
            {
                // Create device enumerator
                int hr = WasapiInterop.CoCreateInstance(
                    in WasapiGuids.CLSID_MMDeviceEnumerator,
                    0,
                    WasapiInterop.CLSCTX_ALL,
                    in WasapiGuids.IID_IMMDeviceEnumerator,
                    out deviceEnumerator);
                
                if (hr < 0)
                    return false;
                
                // Get default audio endpoint
                var enumeratorVtable = new DeviceEnumeratorVtable(deviceEnumerator);
                hr = enumeratorVtable.GetDefaultAudioEndpoint(
                    deviceEnumerator,
                    DataFlow.Render,
                    Role.Multimedia,
                    out audioDevice);
                
                if (hr < 0)
                    return false;
                
                // Activate audio client
                var deviceVtable = new DeviceVtable(audioDevice);
                hr = deviceVtable.Activate(
                    audioDevice,
                    in WasapiGuids.IID_IAudioClient,
                    WasapiInterop.CLSCTX_ALL,
                    0,
                    out audioClient);
                
                if (hr < 0)
                    return false;
                
                // Initialize audio client in shared mode
                var clientVtable = new AudioClientVtable(audioClient);
                
                // Check format support
                hr = clientVtable.IsFormatSupported(
                    audioClient,
                    AudioClientShareMode.Shared,
                    in waveFormat,
                    out nint closestMatch);
                
                if (hr == WasapiInterop.S_FALSE && closestMatch != 0)
                {
                    // Use closest match if our format isn't supported exactly
                    waveFormat = Marshal.PtrToStructure<WaveFormatExtensible>(closestMatch);
                    Marshal.FreeCoTaskMem(closestMatch);
                }
                else if (hr < 0)
                {
                    return false;
                }
                
                // Initialize with target buffer duration
                long bufferDuration = WasapiInterop.MillisecondsToRefTime(TargetBufferMilliseconds);
                
                hr = clientVtable.Initialize(
                    audioClient,
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    bufferDuration,
                    0, // Periodicity (0 for shared mode)
                    in waveFormat,
                    0);
                
                if (hr < 0)
                    return false;
                
                // Get actual buffer size
                hr = clientVtable.GetBufferSize(audioClient, out bufferFrameCount);
                if (hr < 0)
                    return false;
                
                // Get stream latency
                clientVtable.GetStreamLatency(audioClient, out streamLatencyRefTime);
                
                // Get render client
                hr = clientVtable.GetService(
                    audioClient,
                    in WasapiGuids.IID_IAudioRenderClient,
                    out renderClient);
                
                if (hr < 0)
                    return false;
                
                // Get audio clock
                hr = clientVtable.GetService(
                    audioClient,
                    in WasapiGuids.IID_IAudioClock,
                    out audioClock);
                
                if (hr >= 0)
                {
                    var clockVtable = new AudioClockVtable(audioClock);
                    clockVtable.GetFrequency(audioClock, out clockFrequency);
                }
                
                isInitialized = true;
                return true;
            }
            catch
            {
                Cleanup();
                return false;
            }
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (!isInitialized || isStarted)
                return;
            
            var clientVtable = new AudioClientVtable(audioClient);
            int hr = clientVtable.Start(audioClient);
            
            if (hr >= 0)
            {
                isStarted = true;
                isPaused = false;
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (!isStarted)
                return;
            
            var clientVtable = new AudioClientVtable(audioClient);
            clientVtable.Stop(audioClient);
            clientVtable.Reset(audioClient);
            
            isStarted = false;
            isPaused = false;
        }

        /// <inheritdoc/>
        public void Pause()
        {
            if (!isStarted || isPaused)
                return;
            
            var clientVtable = new AudioClientVtable(audioClient);
            clientVtable.Stop(audioClient);
            isPaused = true;
        }

        /// <inheritdoc/>
        public void Resume()
        {
            if (!isStarted || !isPaused)
                return;
            
            var clientVtable = new AudioClientVtable(audioClient);
            clientVtable.Start(audioClient);
            isPaused = false;
        }

        /// <inheritdoc/>
        public unsafe int Write(in AudioBuffer buffer)
        {
            if (!isInitialized || !isStarted || isPaused)
                return 0;
            
            var clientVtable = new AudioClientVtable(audioClient);
            var renderVtable = new AudioRenderClientVtable(renderClient);
            
            // Get current buffer padding (frames already in buffer)
            int hr = clientVtable.GetCurrentPadding(audioClient, out uint paddingFrames);
            if (hr < 0)
                return 0;
            
            // Calculate available frames
            uint availableFrames = bufferFrameCount - paddingFrames;
            uint framesToWrite = Math.Min((uint)buffer.FrameCount, availableFrames);
            
            if (framesToWrite == 0)
                return 0;
            
            // Get buffer from WASAPI
            hr = renderVtable.GetBuffer(renderClient, framesToWrite, out nint bufferPtr);
            if (hr < 0)
                return 0;
            
            // Copy audio data
            int bytesToCopy = (int)(framesToWrite * bytesPerFrame);
            Buffer.MemoryCopy(
                (void*)buffer.DataPointer,
                (void*)bufferPtr,
                bytesToCopy,
                bytesToCopy);
            
            // Apply volume scaling if not at 1.0
            if (Math.Abs(volume - 1.0f) > 0.001f && !isMuted)
            {
                ApplyVolume(bufferPtr, framesToWrite);
            }
            else if (isMuted)
            {
                // Zero the buffer if muted
                new Span<byte>((void*)bufferPtr, bytesToCopy).Clear();
            }
            
            // Release buffer
            renderVtable.ReleaseBuffer(renderClient, framesToWrite, 0);
            
            // Update statistics
            Interlocked.Add(ref statistics.FramesWritten, framesToWrite);
            statistics.BufferFillLevel = (float)(paddingFrames + framesToWrite) / bufferFrameCount;
            
            return (int)framesToWrite;
        }

        /// <summary>
        /// Applies volume scaling to the buffer.
        /// </summary>
        private unsafe void ApplyVolume(nint bufferPtr, uint frameCount)
        {
            // Handle based on format
            if (format.SampleFormat == AudioSampleFormat.Float32)
            {
                var samples = new Span<float>((void*)bufferPtr, (int)(frameCount * format.ChannelCount));
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= volume;
                }
            }
            else if (format.SampleFormat == AudioSampleFormat.Int16)
            {
                var samples = new Span<short>((void*)bufferPtr, (int)(frameCount * format.ChannelCount));
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(samples[i] * volume);
                }
            }
        }

        /// <inheritdoc/>
        public void Flush()
        {
            if (!isInitialized)
                return;
            
            var clientVtable = new AudioClientVtable(audioClient);
            
            bool wasStarted = isStarted && !isPaused;
            if (wasStarted)
                clientVtable.Stop(audioClient);
            
            clientVtable.Reset(audioClient);
            
            if (wasStarted)
                clientVtable.Start(audioClient);
        }

        /// <inheritdoc/>
        public long GetPositionMicroseconds()
        {
            if (audioClock == 0 || clockFrequency == 0)
                return 0;
            
            var clockVtable = new AudioClockVtable(audioClock);
            int hr = clockVtable.GetPosition(audioClock, out ulong position, out _);
            
            if (hr < 0)
                return 0;
            
            // Convert position to microseconds
            return (long)(position * 1_000_000 / clockFrequency);
        }

        /// <inheritdoc/>
        public long GetLatencyMicroseconds()
        {
            return WasapiInterop.RefTimeToMicroseconds(streamLatencyRefTime);
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
            if (deviceEnumerator == 0)
                return [];
            
            cachedDevices.Clear();
            
            var enumeratorVtable = new DeviceEnumeratorVtable(deviceEnumerator);
            
            // Get default device for comparison
            int hr = enumeratorVtable.GetDefaultAudioEndpoint(
                deviceEnumerator,
                DataFlow.Render,
                Role.Multimedia,
                out nint defaultDevice);
            
            string? defaultDeviceId = null;
            if (hr >= 0 && defaultDevice != 0)
            {
                var defaultVtable = new DeviceVtable(defaultDevice);
                if (defaultVtable.GetId(defaultDevice, out nint idPtr) >= 0 && idPtr != 0)
                {
                    defaultDeviceId = Marshal.PtrToStringUni(idPtr);
                    Marshal.FreeCoTaskMem(idPtr);
                }
                defaultVtable.Release(defaultDevice);
            }
            
            // Enumerate all render devices
            hr = enumeratorVtable.EnumAudioEndpoints(
                deviceEnumerator,
                DataFlow.Render,
                DeviceState.Active,
                out nint deviceCollection);
            
            if (hr < 0 || deviceCollection == 0)
                return [];
            
            // Get count and enumerate
            // Note: Simplified - full implementation would use IMMDeviceCollection vtable
            
            return cachedDevices.ToArray();
        }

        /// <inheritdoc/>
        public bool SetDevice(string deviceId)
        {
            // Would need to reinitialize with new device
            // For now, just return false (use default device)
            return false;
        }

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

        /// <summary>
        /// Gets the actual buffer size in frames.
        /// </summary>
        public uint BufferFrameCount => bufferFrameCount;

        /// <summary>
        /// Gets the actual buffer duration in milliseconds.
        /// </summary>
        public int BufferDurationMilliseconds => 
            format.SampleRate > 0 ? (int)(bufferFrameCount * 1000 / format.SampleRate) : 0;

        private void Cleanup()
        {
            if (audioClock != 0)
            {
                new AudioClockVtable(audioClock).Release(audioClock);
                audioClock = 0;
            }
            
            if (renderClient != 0)
            {
                new AudioRenderClientVtable(renderClient).Release(renderClient);
                renderClient = 0;
            }
            
            if (audioClient != 0)
            {
                if (isStarted)
                {
                    var clientVtable = new AudioClientVtable(audioClient);
                    clientVtable.Stop(audioClient);
                    clientVtable.Reset(audioClient);
                }
                new AudioClientVtable(audioClient).Release(audioClient);
                audioClient = 0;
            }
            
            if (audioDevice != 0)
            {
                new DeviceVtable(audioDevice).Release(audioDevice);
                audioDevice = 0;
            }
            
            if (deviceEnumerator != 0)
            {
                new DeviceEnumeratorVtable(deviceEnumerator).Release(deviceEnumerator);
                deviceEnumerator = 0;
            }
            
            isInitialized = false;
            isStarted = false;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
                return;
            
            Cleanup();
            WasapiInterop.CoUninitialize();
            
            disposed = true;
        }
    }
}

// MediaKernel - Capture Devices
// Screen capture, webcam, audio capture, TV tuners

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Access;

/// <summary>
/// Base interface for capture devices.
/// </summary>
public interface ICaptureDevice : IAsyncDisposable
{
    /// <summary>Device identifier.</summary>
    string DeviceId { get; }
    
    /// <summary>Human-readable device name.</summary>
    string DisplayName { get; }
    
    /// <summary>Device type.</summary>
    CaptureDeviceType Type { get; }
    
    /// <summary>Whether the device is currently capturing.</summary>
    bool IsCapturing { get; }
    
    /// <summary>Start capturing.</summary>
    Task StartCaptureAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Stop capturing.</summary>
    Task StopCaptureAsync();
}

/// <summary>
/// Capture device types.
/// </summary>
public enum CaptureDeviceType
{
    Screen,
    Webcam,
    AudioInput,
    AudioLoopback,
    TvTuner
}

/// <summary>
/// Captured frame data.
/// </summary>
public readonly struct CapturedFrame
{
    public ReadOnlyMemory<byte> Data { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public PixelFormat Format { get; init; }
    public long Timestamp { get; init; }
    public int Stride { get; init; }
}

/// <summary>
/// Captured audio data.
/// </summary>
public readonly struct CapturedAudio
{
    public ReadOnlyMemory<byte> Data { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public long Timestamp { get; init; }
}

// PixelFormat is defined in MediaKernel.Core.MediaTypes — single canonical location.
// Capture-specific formats (Yuy2, Bgr24, etc.) will be added there as needed.

// ============================================================================
// Screen Capture
// ============================================================================

/// <summary>
/// Screen capture using platform-native APIs.
/// Windows: DXGI Desktop Duplication API
/// Linux: X11 XGetImage or PipeWire
/// macOS: CGDisplayStream
/// </summary>
public sealed class ScreenCapture : ICaptureDevice
{
    private readonly ScreenCaptureConfiguration config;
    private bool capturing;
    private CancellationTokenSource? captureCts;
    private Task? captureTask;
    private nint duplicator;
    
    public string DeviceId => config.DisplayId ?? "primary";
    public string DisplayName => $"Screen {config.DisplayIndex}";
    public CaptureDeviceType Type => CaptureDeviceType.Screen;
    public bool IsCapturing => capturing;
    
    /// <summary>Event raised when a frame is captured.</summary>
    public event EventHandler<CapturedFrame>? FrameCaptured;
    
    public ScreenCapture(ScreenCaptureConfiguration configuration)
    {
        config = configuration;
    }
    
    public Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (capturing) return Task.CompletedTask;
        
        captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        capturing = true;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            captureTask = CaptureLoopWindowsAsync(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            captureTask = CaptureLoopLinuxAsync(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            captureTask = CaptureLoopMacOSAsync(captureCts.Token);
        }
        
        return Task.CompletedTask;
    }
    
    private async Task CaptureLoopWindowsAsync(CancellationToken cancellationToken)
    {
        // DXGI Desktop Duplication API
        // This is a simplified stub - full implementation requires COM interop
        
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // In production: Call IDXGIOutputDuplication.AcquireNextFrame
                // Copy texture to staging texture
                // Map staging texture to get pixel data
                
                // Placeholder frame
                int width = config.CaptureWidth > 0 ? config.CaptureWidth : 1920;
                int height = config.CaptureHeight > 0 ? config.CaptureHeight : 1080;
                int stride = width * 4;
                byte[] frameData = ArrayPool<byte>.Shared.Rent(stride * height);
                
                try
                {
                    // Generate test pattern or capture actual screen
                    GenerateTestPattern(frameData, width, height, stride);
                    
                    FrameCaptured?.Invoke(this, new CapturedFrame
                    {
                        Data = frameData.AsMemory(0, stride * height),
                        Width = width,
                        Height = height,
                        Format = PixelFormat.Bgra,
                        Stride = stride,
                        Timestamp = Environment.TickCount64 * 10000 // 100ns units
                    });
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameData);
                }
                
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopLinuxAsync(CancellationToken cancellationToken)
    {
        // X11 or PipeWire screen capture
        // XShmGetImage for shared memory capture
        // or PipeWire for Wayland
        
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Placeholder - would use X11 or PipeWire
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopMacOSAsync(CancellationToken cancellationToken)
    {
        // CGDisplayStream for macOS screen capture
        
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Placeholder - would use CGDisplayStream
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private static void GenerateTestPattern(byte[] buffer, int width, int height, int stride)
    {
        // Generate a simple test pattern
        long time = Environment.TickCount64;
        int offset = (int)((time / 50) % width);
        
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int px = rowOffset + x * 4;
                
                // Moving gradient pattern
                byte r = (byte)((x + offset) % 256);
                byte g = (byte)((y + offset / 2) % 256);
                byte b = (byte)((x + y + offset) % 256);
                
                buffer[px] = b;     // B
                buffer[px + 1] = g; // G
                buffer[px + 2] = r; // R
                buffer[px + 3] = 255; // A
            }
        }
    }
    
    public async Task StopCaptureAsync()
    {
        if (!capturing) return;
        
        captureCts?.Cancel();
        if (captureTask != null)
        {
            try { await captureTask; } catch { }
        }
        
        capturing = false;
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        captureCts?.Dispose();
    }
}

/// <summary>
/// Screen capture configuration.
/// </summary>
public sealed class ScreenCaptureConfiguration
{
    public int DisplayIndex { get; init; }
    public string? DisplayId { get; init; }
    public int CaptureX { get; init; }
    public int CaptureY { get; init; }
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public int FrameRate { get; init; } = 30;
    public bool CaptureCursor { get; init; } = true;
    public bool UseHardwareAcceleration { get; init; } = true;
}

// ============================================================================
// Webcam Capture
// ============================================================================

/// <summary>
/// Webcam capture using platform-native APIs.
/// Windows: DirectShow or Media Foundation
/// Linux: V4L2 (Video4Linux)
/// macOS: AVFoundation
/// </summary>
public sealed class WebcamCapture : ICaptureDevice
{
    private readonly WebcamCaptureConfiguration config;
    private bool capturing;
    private CancellationTokenSource? captureCts;
    private Task? captureTask;
    
    public string DeviceId => config.DeviceId ?? "default";
    public string DisplayName => config.DisplayName ?? "Webcam";
    public CaptureDeviceType Type => CaptureDeviceType.Webcam;
    public bool IsCapturing => capturing;
    
    /// <summary>Event raised when a frame is captured.</summary>
    public event EventHandler<CapturedFrame>? FrameCaptured;
    
    public WebcamCapture(WebcamCaptureConfiguration configuration)
    {
        config = configuration;
    }
    
    /// <summary>
    /// Enumerate available webcam devices.
    /// </summary>
    public static IReadOnlyList<WebcamDeviceInfo> EnumerateDevices()
    {
        var devices = new List<WebcamDeviceInfo>();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // DirectShow or Media Foundation enumeration
            // Would use SetupAPI or MF device enumeration
            devices.Add(new WebcamDeviceInfo
            {
                DeviceId = "\\\\?\\usb#vid_0000&pid_0000",
                DisplayName = "Default Webcam",
                Capabilities = [new WebcamCapability(1920, 1080, 30), 
                               new WebcamCapability(1280, 720, 60)]
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // V4L2 device enumeration from /dev/video*
            for (int i = 0; i < 10; i++)
            {
                string devicePath = $"/dev/video{i}";
                if (File.Exists(devicePath))
                {
                    devices.Add(new WebcamDeviceInfo
                    {
                        DeviceId = devicePath,
                        DisplayName = $"Video Device {i}",
                        Capabilities = [new WebcamCapability(1920, 1080, 30)]
                    });
                }
            }
        }
        
        return devices;
    }
    
    public Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (capturing) return Task.CompletedTask;
        
        captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        capturing = true;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            captureTask = CaptureLoopWindowsAsync(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            captureTask = CaptureLoopV4L2Async(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            captureTask = CaptureLoopAVFoundationAsync(captureCts.Token);
        }
        
        return Task.CompletedTask;
    }
    
    private async Task CaptureLoopWindowsAsync(CancellationToken cancellationToken)
    {
        // Media Foundation or DirectShow capture
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // In production: Use IMFSourceReader to get frames
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopV4L2Async(CancellationToken cancellationToken)
    {
        // V4L2 capture using ioctl calls
        // VIDIOC_QUERYCAP, VIDIOC_S_FMT, VIDIOC_REQBUFS, VIDIOC_STREAMON
        
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // In production: Use V4L2 mmap buffers
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopAVFoundationAsync(CancellationToken cancellationToken)
    {
        // AVFoundation AVCaptureSession
        int frameInterval = 1000 / Math.Max(1, config.FrameRate);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(frameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    public async Task StopCaptureAsync()
    {
        if (!capturing) return;
        
        captureCts?.Cancel();
        if (captureTask != null)
        {
            try { await captureTask; } catch { }
        }
        
        capturing = false;
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        captureCts?.Dispose();
    }
}

/// <summary>
/// Webcam device information.
/// </summary>
public sealed class WebcamDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<WebcamCapability> Capabilities { get; init; } = [];
}

/// <summary>
/// Webcam capability (resolution/framerate).
/// </summary>
public readonly record struct WebcamCapability(int Width, int Height, int FrameRate);

/// <summary>
/// Webcam capture configuration.
/// </summary>
public sealed class WebcamCaptureConfiguration
{
    public string? DeviceId { get; init; }
    public string? DisplayName { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 30;
    public PixelFormat PreferredFormat { get; init; } = PixelFormat.Nv12;
}

// ============================================================================
// Audio Capture
// ============================================================================

/// <summary>
/// Audio capture for microphone and system loopback.
/// Windows: WASAPI
/// Linux: PulseAudio/PipeWire
/// macOS: Core Audio
/// </summary>
public sealed class AudioCapture : ICaptureDevice
{
    private readonly AudioCaptureConfiguration config;
    private bool capturing;
    private CancellationTokenSource? captureCts;
    private Task? captureTask;
    
    public string DeviceId => config.DeviceId ?? "default";
    public string DisplayName => config.DisplayName ?? "Audio Input";
    public CaptureDeviceType Type => config.CaptureLoopback 
        ? CaptureDeviceType.AudioLoopback 
        : CaptureDeviceType.AudioInput;
    public bool IsCapturing => capturing;
    
    /// <summary>Event raised when audio samples are captured.</summary>
    public event EventHandler<CapturedAudio>? AudioCaptured;
    
    public AudioCapture(AudioCaptureConfiguration configuration)
    {
        config = configuration;
    }
    
    /// <summary>
    /// Enumerate available audio input devices.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateDevices(bool includeLoopback = true)
    {
        var devices = new List<AudioDeviceInfo>();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // WASAPI device enumeration
            // IMMDeviceEnumerator with eCapture or eRender (for loopback)
            devices.Add(new AudioDeviceInfo
            {
                DeviceId = "{default}",
                DisplayName = "Default Microphone",
                IsLoopback = false,
                SampleRate = 48000,
                Channels = 2
            });
            
            if (includeLoopback)
            {
                devices.Add(new AudioDeviceInfo
                {
                    DeviceId = "{loopback}",
                    DisplayName = "System Audio (Loopback)",
                    IsLoopback = true,
                    SampleRate = 48000,
                    Channels = 2
                });
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // PulseAudio/PipeWire source enumeration
            devices.Add(new AudioDeviceInfo
            {
                DeviceId = "default",
                DisplayName = "Default Audio Input",
                IsLoopback = false,
                SampleRate = 48000,
                Channels = 2
            });
        }
        
        return devices;
    }
    
    public Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (capturing) return Task.CompletedTask;
        
        captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        capturing = true;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            captureTask = CaptureLoopWasapiAsync(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            captureTask = CaptureLoopPulseAudioAsync(captureCts.Token);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            captureTask = CaptureLoopCoreAudioAsync(captureCts.Token);
        }
        
        return Task.CompletedTask;
    }
    
    private async Task CaptureLoopWasapiAsync(CancellationToken cancellationToken)
    {
        // WASAPI capture client
        // IAudioCaptureClient with event-driven capture
        
        int bufferMs = 20; // 20ms buffer
        int samplesPerBuffer = config.SampleRate * bufferMs / 1000;
        int bytesPerBuffer = samplesPerBuffer * config.Channels * (config.BitsPerSample / 8);
        byte[] buffer = new byte[bytesPerBuffer];
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // In production: Call IAudioCaptureClient.GetBuffer
                // Copy audio data and release buffer
                
                // Generate test audio (silence or tone)
                Array.Clear(buffer);
                
                AudioCaptured?.Invoke(this, new CapturedAudio
                {
                    Data = buffer.AsMemory(),
                    SampleRate = config.SampleRate,
                    Channels = config.Channels,
                    BitsPerSample = config.BitsPerSample,
                    Timestamp = Environment.TickCount64 * 10000
                });
                
                await Task.Delay(bufferMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopPulseAudioAsync(CancellationToken cancellationToken)
    {
        // PulseAudio simple API or async API
        
        int bufferMs = 20;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(bufferMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private async Task CaptureLoopCoreAudioAsync(CancellationToken cancellationToken)
    {
        // Core Audio AudioQueue or Audio Unit
        
        int bufferMs = 20;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(bufferMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    public async Task StopCaptureAsync()
    {
        if (!capturing) return;
        
        captureCts?.Cancel();
        if (captureTask != null)
        {
            try { await captureTask; } catch { }
        }
        
        capturing = false;
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        captureCts?.Dispose();
    }
}

/// <summary>
/// Audio device information.
/// </summary>
public sealed class AudioDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsLoopback { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}

/// <summary>
/// Audio capture configuration.
/// </summary>
public sealed class AudioCaptureConfiguration
{
    public string? DeviceId { get; init; }
    public string? DisplayName { get; init; }
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 2;
    public int BitsPerSample { get; init; } = 16;
    public bool CaptureLoopback { get; init; }
}

// ============================================================================
// TV Tuner (DVB)
// ============================================================================

/// <summary>
/// Digital TV tuner capture for DVB-S/C/T.
/// </summary>
public sealed class TvTunerCapture : ICaptureDevice
{
    private readonly TvTunerConfiguration config;
    private bool capturing;
    private CancellationTokenSource? captureCts;
    private Task? captureTask;
    
    public string DeviceId => config.AdapterPath ?? "/dev/dvb/adapter0";
    public string DisplayName => config.DisplayName ?? "TV Tuner";
    public CaptureDeviceType Type => CaptureDeviceType.TvTuner;
    public bool IsCapturing => capturing;
    
    /// <summary>Event raised when MPEG-TS data is received.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? TransportStreamReceived;
    
    /// <summary>Current signal strength (0-100).</summary>
    public int SignalStrength { get; private set; }
    
    /// <summary>Current signal quality (0-100).</summary>
    public int SignalQuality { get; private set; }
    
    /// <summary>Whether signal is locked.</summary>
    public bool SignalLocked { get; private set; }
    
    public TvTunerCapture(TvTunerConfiguration configuration)
    {
        config = configuration;
    }
    
    /// <summary>
    /// Enumerate available DVB adapters.
    /// </summary>
    public static IReadOnlyList<TvTunerInfo> EnumerateAdapters()
    {
        var adapters = new List<TvTunerInfo>();
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check /dev/dvb/adapter* devices
            for (int i = 0; i < 8; i++)
            {
                string adapterPath = $"/dev/dvb/adapter{i}";
                if (Directory.Exists(adapterPath))
                {
                    adapters.Add(new TvTunerInfo
                    {
                        AdapterPath = adapterPath,
                        DisplayName = $"DVB Adapter {i}",
                        Type = DetectDvbType(adapterPath)
                    });
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // BDA (Broadcast Driver Architecture) enumeration
            // Would use DirectShow filter graph
            adapters.Add(new TvTunerInfo
            {
                AdapterPath = "BDA\\Tuner0",
                DisplayName = "Digital TV Tuner",
                Type = DvbType.DvbT
            });
        }
        
        return adapters;
    }
    
    private static DvbType DetectDvbType(string adapterPath)
    {
        // Read from frontend0 to determine DVB type
        // In production: Use ioctl FE_GET_INFO
        return DvbType.DvbT;
    }
    
    /// <summary>
    /// Tune to a specific frequency/transponder.
    /// </summary>
    public async Task TuneAsync(TuningParameters parameters, CancellationToken cancellationToken = default)
    {
        // DVB frontend tuning
        // Linux: ioctl on /dev/dvb/adapter*/frontend*
        // FE_SET_FRONTEND with dvb_frontend_parameters
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Open frontend device
            // Set parameters based on DVB type
            // Wait for lock
            
            // Simulate tuning delay
            await Task.Delay(1000, cancellationToken);
            
            SignalLocked = true;
            SignalStrength = 85;
            SignalQuality = 90;
        }
    }
    
    public Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (capturing) return Task.CompletedTask;
        
        if (!SignalLocked)
            throw new InvalidOperationException("Must tune before capturing");
        
        captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        capturing = true;
        captureTask = CaptureLoopAsync(captureCts.Token);
        
        return Task.CompletedTask;
    }
    
    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        // Read from DVR device (/dev/dvb/adapter*/dvr*)
        // or demux device for filtered PIDs
        
        byte[] buffer = new byte[188 * 7]; // 7 TS packets
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // In production: Read from DVR device
                // Apply PID filter if configured
                
                // Placeholder: Generate null TS packets
                for (int i = 0; i < 7; i++)
                {
                    int offset = i * 188;
                    buffer[offset] = 0x47; // Sync byte
                    buffer[offset + 1] = 0x1F; // PID high (null packet = 0x1FFF)
                    buffer[offset + 2] = 0xFF; // PID low
                    buffer[offset + 3] = 0x10; // Adaptation field control
                }
                
                TransportStreamReceived?.Invoke(this, buffer.AsMemory());
                
                await Task.Delay(10, cancellationToken); // ~7 packets per 10ms
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    public async Task StopCaptureAsync()
    {
        if (!capturing) return;
        
        captureCts?.Cancel();
        if (captureTask != null)
        {
            try { await captureTask; } catch { }
        }
        
        capturing = false;
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        captureCts?.Dispose();
    }
}

/// <summary>
/// TV tuner information.
/// </summary>
public sealed class TvTunerInfo
{
    public required string AdapterPath { get; init; }
    public required string DisplayName { get; init; }
    public DvbType Type { get; init; }
}

/// <summary>
/// DVB delivery system type.
/// </summary>
public enum DvbType
{
    DvbS,   // Satellite
    DvbS2,  // Satellite 2nd gen
    DvbC,   // Cable
    DvbC2,  // Cable 2nd gen
    DvbT,   // Terrestrial
    DvbT2,  // Terrestrial 2nd gen
    Atsc,   // North American digital TV
    IsdbT   // Japanese digital TV
}

/// <summary>
/// TV tuner configuration.
/// </summary>
public sealed class TvTunerConfiguration
{
    public string? AdapterPath { get; init; }
    public string? DisplayName { get; init; }
    public int FrontendIndex { get; init; }
    public int DemuxIndex { get; init; }
    public int DvrIndex { get; init; }
    public int[]? PidFilter { get; init; } // null = full transport stream
}

/// <summary>
/// Tuning parameters for DVB.
/// </summary>
public sealed class TuningParameters
{
    // Common
    public required long Frequency { get; init; } // Hz
    
    // DVB-S/S2
    public int SymbolRate { get; init; } // Symbols/sec
    public DvbPolarization Polarization { get; init; }
    public DvbFecRate FecRate { get; init; }
    
    // DVB-T/T2
    public DvbBandwidth Bandwidth { get; init; }
    public DvbGuardInterval GuardInterval { get; init; }
    public DvbTransmissionMode TransmissionMode { get; init; }
    
    // DVB-C
    public DvbModulation Modulation { get; init; }
}

public enum DvbPolarization { Horizontal, Vertical, CircularLeft, CircularRight }
public enum DvbFecRate { Auto, Rate1_2, Rate2_3, Rate3_4, Rate5_6, Rate7_8, Rate8_9 }
public enum DvbBandwidth { Bw6Mhz, Bw7Mhz, Bw8Mhz, Bw5Mhz, Bw1_7Mhz, Bw10Mhz }
public enum DvbGuardInterval { Auto, Interval1_4, Interval1_8, Interval1_16, Interval1_32 }
public enum DvbTransmissionMode { Auto, Mode2K, Mode8K, Mode1K, Mode4K, Mode16K, Mode32K }
public enum DvbModulation { Auto, Qpsk, Qam16, Qam32, Qam64, Qam128, Qam256, Psk8, Apsk16, Apsk32 }

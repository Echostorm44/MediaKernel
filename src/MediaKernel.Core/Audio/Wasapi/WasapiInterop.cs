using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Audio.Wasapi
{
    /// <summary>
    /// WASAPI COM interface GUIDs.
    /// </summary>
    internal static class WasapiGuids
    {
        // MMDevice API
        public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IMMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
        public static readonly Guid IID_IMMDeviceCollection = new("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E");
        
        // WASAPI
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_IAudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");
        public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        // Audio formats
        public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");
    }

    /// <summary>
    /// WASAPI stream flags.
    /// </summary>
    [Flags]
    internal enum AudioClientStreamFlags : uint
    {
        None = 0,
        CrossProcess = 0x00010000,
        Loopback = 0x00020000,
        EventCallback = 0x00040000,
        NoPersist = 0x00080000,
        RateAdjust = 0x00100000,
        AutoConvertPcm = 0x80000000,
        SrcDefaultQuality = 0x08000000
    }

    /// <summary>
    /// Share mode for audio stream.
    /// </summary>
    internal enum AudioClientShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    /// <summary>
    /// Device state for MMDevice.
    /// </summary>
    [Flags]
    internal enum DeviceState : uint
    {
        Active = 0x00000001,
        Disabled = 0x00000002,
        NotPresent = 0x00000004,
        Unplugged = 0x00000008,
        All = Active | Disabled | NotPresent | Unplugged
    }

    /// <summary>
    /// Data flow direction.
    /// </summary>
    internal enum DataFlow
    {
        Render = 0,  // Output (speakers)
        Capture = 1, // Input (microphone)
        All = 2
    }

    /// <summary>
    /// Device role.
    /// </summary>
    internal enum Role
    {
        Console = 0,       // Games, system sounds
        Multimedia = 1,    // Music, movies
        Communications = 2 // Voice chat
    }

    /// <summary>
    /// WAVEFORMATEX structure for basic audio format.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public const ushort WAVE_FORMAT_PCM = 1;
        public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
        public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    }

    /// <summary>
    /// WAVEFORMATEXTENSIBLE for detailed audio format.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;

        // Channel mask bits
        public const uint SPEAKER_FRONT_LEFT = 0x1;
        public const uint SPEAKER_FRONT_RIGHT = 0x2;
        public const uint SPEAKER_FRONT_CENTER = 0x4;
        public const uint SPEAKER_LOW_FREQUENCY = 0x8;
        public const uint SPEAKER_BACK_LEFT = 0x10;
        public const uint SPEAKER_BACK_RIGHT = 0x20;
        public const uint SPEAKER_FRONT_LEFT_OF_CENTER = 0x40;
        public const uint SPEAKER_FRONT_RIGHT_OF_CENTER = 0x80;
        public const uint SPEAKER_BACK_CENTER = 0x100;
        public const uint SPEAKER_SIDE_LEFT = 0x200;
        public const uint SPEAKER_SIDE_RIGHT = 0x400;

        public static uint GetChannelMask(int channelCount) => channelCount switch
        {
            1 => SPEAKER_FRONT_CENTER,
            2 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT,
            3 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_LOW_FREQUENCY,
            4 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT,
            5 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT,
            6 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER | SPEAKER_LOW_FREQUENCY | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT,
            8 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER | SPEAKER_LOW_FREQUENCY | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT | SPEAKER_SIDE_LEFT | SPEAKER_SIDE_RIGHT,
            _ => 0
        };

        public static WaveFormatExtensible Create(AudioFormat format)
        {
            bool isFloat = format.SampleFormat == AudioSampleFormat.Float32 || 
                          format.SampleFormat == AudioSampleFormat.Float64;
            
            int bitsPerSample = format.SampleFormat switch
            {
                AudioSampleFormat.UInt8 => 8,
                AudioSampleFormat.Int16 => 16,
                AudioSampleFormat.Int24 => 24,
                AudioSampleFormat.Int24In32 => 32,
                AudioSampleFormat.Int32 => 32,
                AudioSampleFormat.Float32 => 32,
                AudioSampleFormat.Float64 => 64,
                _ => 16
            };

            int validBits = format.SampleFormat == AudioSampleFormat.Int24In32 ? 24 : bitsPerSample;
            int blockAlign = format.ChannelCount * (bitsPerSample / 8);

            return new WaveFormatExtensible
            {
                Format = new WaveFormatEx
                {
                    FormatTag = WaveFormatEx.WAVE_FORMAT_EXTENSIBLE,
                    Channels = (ushort)format.ChannelCount,
                    SamplesPerSec = (uint)format.SampleRate,
                    AvgBytesPerSec = (uint)(format.SampleRate * blockAlign),
                    BlockAlign = (ushort)blockAlign,
                    BitsPerSample = (ushort)bitsPerSample,
                    ExtraSize = 22
                },
                ValidBitsPerSample = (ushort)validBits,
                ChannelMask = GetChannelMask(format.ChannelCount),
                SubFormat = isFloat ? WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT 
                                   : WasapiGuids.KSDATAFORMAT_SUBTYPE_PCM
            };
        }
    }

    /// <summary>
    /// Property key for device name.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        // Device name property
        public static PropertyKey DeviceDescription => new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 2
        };

        public static PropertyKey FriendlyName => new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };
    }

    /// <summary>
    /// PROPVARIANT for property values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint Data1;
        public nint Data2;

        public const ushort VT_LPWSTR = 31;

        public readonly string? GetString()
        {
            if (vt == VT_LPWSTR && Data1 != 0)
                return Marshal.PtrToStringUni(Data1);
            return null;
        }

        public void Clear()
        {
            if (vt == VT_LPWSTR && Data1 != 0)
            {
                Marshal.FreeCoTaskMem(Data1);
                Data1 = 0;
            }
            vt = 0;
        }
    }

    /// <summary>
    /// WASAPI COM interop via function pointers.
    /// </summary>
    internal static partial class WasapiInterop
    {
        private const string Ole32 = "ole32.dll";

        [LibraryImport(Ole32)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial int CoCreateInstance(
            in Guid rclsid,
            nint pUnkOuter,
            uint dwClsContext,
            in Guid riid,
            out nint ppv);

        [LibraryImport(Ole32)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

        [LibraryImport(Ole32)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial void CoUninitialize();

        public const uint CLSCTX_INPROC_SERVER = 1;
        public const uint CLSCTX_ALL = 23;
        public const uint COINIT_MULTITHREADED = 0;
        public const uint COINIT_APARTMENTTHREADED = 2;

        // HRESULT codes
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        public const int AUDCLNT_E_BUFFER_TOO_LARGE = unchecked((int)0x88890006);
        public const int AUDCLNT_E_OUT_OF_ORDER = unchecked((int)0x88890007);
        public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
        public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);

        /// <summary>
        /// Reference time units (100-nanosecond intervals).
        /// </summary>
        public const long REFTIMES_PER_SEC = 10_000_000;
        public const long REFTIMES_PER_MILLISEC = 10_000;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MicrosecondsToRefTime(long microseconds) => microseconds * 10;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long RefTimeToMicroseconds(long refTime) => refTime / 10;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MillisecondsToRefTime(int milliseconds) => milliseconds * REFTIMES_PER_MILLISEC;
    }

    /// <summary>
    /// COM vtable wrapper for IMMDeviceEnumerator.
    /// </summary>
    internal readonly unsafe struct DeviceEnumeratorVtable
    {
        private readonly nint* vtable;

        public DeviceEnumeratorVtable(nint ptr)
        {
            vtable = *(nint**)ptr;
        }

        // IUnknown
        public int QueryInterface(nint thisPtr, in Guid riid, out nint ppv)
        {
            fixed (Guid* pRiid = &riid)
            fixed (nint* pPpv = &ppv)
            {
                return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0])(thisPtr, pRiid, pPpv);
            }
        }

        public uint AddRef(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[1])(thisPtr);

        public uint Release(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(thisPtr);

        // IMMDeviceEnumerator methods (vtable indices 3-6)
        public int EnumAudioEndpoints(nint thisPtr, DataFlow dataFlow, DeviceState stateMask, out nint ppDevices)
        {
            fixed (nint* pPpDevices = &ppDevices)
            {
                return ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)vtable[3])(
                    thisPtr, (int)dataFlow, (uint)stateMask, pPpDevices);
            }
        }

        public int GetDefaultAudioEndpoint(nint thisPtr, DataFlow dataFlow, Role role, out nint ppEndpoint)
        {
            fixed (nint* pPpEndpoint = &ppEndpoint)
            {
                return ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)vtable[4])(
                    thisPtr, (int)dataFlow, (int)role, pPpEndpoint);
            }
        }

        public int GetDevice(nint thisPtr, string pwstrId, out nint ppDevice)
        {
            fixed (char* pPwstrId = pwstrId)
            fixed (nint* pPpDevice = &ppDevice)
            {
                return ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)vtable[5])(
                    thisPtr, pPwstrId, pPpDevice);
            }
        }
    }

    /// <summary>
    /// COM vtable wrapper for IMMDevice.
    /// </summary>
    internal readonly unsafe struct DeviceVtable
    {
        private readonly nint* vtable;

        public DeviceVtable(nint ptr)
        {
            vtable = *(nint**)ptr;
        }

        public uint Release(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(thisPtr);

        // IMMDevice::Activate (vtable[3])
        public int Activate(nint thisPtr, in Guid iid, uint dwClsCtx, nint pActivationParams, out nint ppInterface)
        {
            fixed (Guid* pIid = &iid)
            fixed (nint* pPpInterface = &ppInterface)
            {
                return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)vtable[3])(
                    thisPtr, pIid, dwClsCtx, pActivationParams, pPpInterface);
            }
        }

        // IMMDevice::OpenPropertyStore (vtable[4])
        public int OpenPropertyStore(nint thisPtr, uint stgmAccess, out nint ppProperties)
        {
            fixed (nint* pPpProperties = &ppProperties)
            {
                return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[4])(
                    thisPtr, stgmAccess, pPpProperties);
            }
        }

        // IMMDevice::GetId (vtable[5])
        public int GetId(nint thisPtr, out nint ppstrId)
        {
            fixed (nint* pPpstrId = &ppstrId)
            {
                return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[5])(thisPtr, pPpstrId);
            }
        }
    }

    /// <summary>
    /// COM vtable wrapper for IAudioClient.
    /// </summary>
    internal readonly unsafe struct AudioClientVtable
    {
        private readonly nint* vtable;

        public AudioClientVtable(nint ptr)
        {
            vtable = *(nint**)ptr;
        }

        public uint Release(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(thisPtr);

        // IAudioClient::Initialize (vtable[3])
        public int Initialize(
            nint thisPtr,
            AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            in WaveFormatExtensible pFormat,
            nint audioSessionGuid)
        {
            fixed (WaveFormatExtensible* pPFormat = &pFormat)
            {
                return ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatExtensible*, nint, int>)vtable[3])(
                    thisPtr, (int)shareMode, (uint)streamFlags, hnsBufferDuration, hnsPeriodicity, pPFormat, audioSessionGuid);
            }
        }

        // IAudioClient::GetBufferSize (vtable[4])
        public int GetBufferSize(nint thisPtr, out uint pNumBufferFrames)
        {
            fixed (uint* pPNumBufferFrames = &pNumBufferFrames)
            {
                return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[4])(thisPtr, pPNumBufferFrames);
            }
        }

        // IAudioClient::GetStreamLatency (vtable[5])
        public int GetStreamLatency(nint thisPtr, out long phnsLatency)
        {
            fixed (long* pPhnsLatency = &phnsLatency)
            {
                return ((delegate* unmanaged[Stdcall]<nint, long*, int>)vtable[5])(thisPtr, pPhnsLatency);
            }
        }

        // IAudioClient::GetCurrentPadding (vtable[6])
        public int GetCurrentPadding(nint thisPtr, out uint pNumPaddingFrames)
        {
            fixed (uint* pPNumPaddingFrames = &pNumPaddingFrames)
            {
                return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vtable[6])(thisPtr, pPNumPaddingFrames);
            }
        }

        // IAudioClient::IsFormatSupported (vtable[7])
        public int IsFormatSupported(
            nint thisPtr,
            AudioClientShareMode shareMode,
            in WaveFormatExtensible pFormat,
            out nint ppClosestMatch)
        {
            fixed (WaveFormatExtensible* pPFormat = &pFormat)
            fixed (nint* pPpClosestMatch = &ppClosestMatch)
            {
                return ((delegate* unmanaged[Stdcall]<nint, int, WaveFormatExtensible*, nint*, int>)vtable[7])(
                    thisPtr, (int)shareMode, pPFormat, pPpClosestMatch);
            }
        }

        // IAudioClient::GetMixFormat (vtable[8])
        public int GetMixFormat(nint thisPtr, out nint ppDeviceFormat)
        {
            fixed (nint* pPpDeviceFormat = &ppDeviceFormat)
            {
                return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[8])(thisPtr, pPpDeviceFormat);
            }
        }

        // IAudioClient::GetDevicePeriod (vtable[9])
        public int GetDevicePeriod(nint thisPtr, out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod)
        {
            fixed (long* pDefault = &phnsDefaultDevicePeriod)
            fixed (long* pMin = &phnsMinimumDevicePeriod)
            {
                return ((delegate* unmanaged[Stdcall]<nint, long*, long*, int>)vtable[9])(thisPtr, pDefault, pMin);
            }
        }

        // IAudioClient::Start (vtable[10])
        public int Start(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, int>)vtable[10])(thisPtr);

        // IAudioClient::Stop (vtable[11])
        public int Stop(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, int>)vtable[11])(thisPtr);

        // IAudioClient::Reset (vtable[12])
        public int Reset(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, int>)vtable[12])(thisPtr);

        // IAudioClient::SetEventHandle (vtable[13])
        public int SetEventHandle(nint thisPtr, nint eventHandle) => 
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[13])(thisPtr, eventHandle);

        // IAudioClient::GetService (vtable[14])
        public int GetService(nint thisPtr, in Guid riid, out nint ppv)
        {
            fixed (Guid* pRiid = &riid)
            fixed (nint* pPpv = &ppv)
            {
                return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[14])(thisPtr, pRiid, pPpv);
            }
        }
    }

    /// <summary>
    /// COM vtable wrapper for IAudioRenderClient.
    /// </summary>
    internal readonly unsafe struct AudioRenderClientVtable
    {
        private readonly nint* vtable;

        public AudioRenderClientVtable(nint ptr)
        {
            vtable = *(nint**)ptr;
        }

        public uint Release(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(thisPtr);

        // IAudioRenderClient::GetBuffer (vtable[3])
        public int GetBuffer(nint thisPtr, uint numFramesRequested, out nint ppData)
        {
            fixed (nint* pPpData = &ppData)
            {
                return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[3])(
                    thisPtr, numFramesRequested, pPpData);
            }
        }

        // IAudioRenderClient::ReleaseBuffer (vtable[4])
        public int ReleaseBuffer(nint thisPtr, uint numFramesWritten, uint dwFlags) =>
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)vtable[4])(thisPtr, numFramesWritten, dwFlags);

        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    }

    /// <summary>
    /// COM vtable wrapper for IAudioClock.
    /// </summary>
    internal readonly unsafe struct AudioClockVtable
    {
        private readonly nint* vtable;

        public AudioClockVtable(nint ptr)
        {
            vtable = *(nint**)ptr;
        }

        public uint Release(nint thisPtr) => 
            ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(thisPtr);

        // IAudioClock::GetFrequency (vtable[3])
        public int GetFrequency(nint thisPtr, out ulong pu64Frequency)
        {
            fixed (ulong* pFreq = &pu64Frequency)
            {
                return ((delegate* unmanaged[Stdcall]<nint, ulong*, int>)vtable[3])(thisPtr, pFreq);
            }
        }

        // IAudioClock::GetPosition (vtable[4])
        public int GetPosition(nint thisPtr, out ulong pu64Position, out ulong pu64QPCPosition)
        {
            fixed (ulong* pPos = &pu64Position)
            fixed (ulong* pQpc = &pu64QPCPosition)
            {
                return ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)vtable[4])(thisPtr, pPos, pQpc);
            }
        }
    }
}

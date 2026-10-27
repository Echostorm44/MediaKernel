using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaKernel.Core.Audio.CoreAudio;

/// <summary>
/// Core Audio native interop for macOS audio output.
/// Uses AudioUnit (AudioOutputUnit) for low-latency playback.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class CoreAudioInterop
{
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string AudioUnitLib = "/System/Library/Frameworks/AudioUnit.framework/AudioUnit";

    // OSStatus error codes
    public const int kAudioUnitErr_InvalidProperty = -10879;
    public const int kAudioUnitErr_InvalidParameter = -10878;
    public const int kAudioUnitErr_InvalidElement = -10877;
    public const int kAudioUnitErr_NoConnection = -10876;
    public const int kAudioUnitErr_FailedInitialization = -10875;
    public const int kAudioUnitErr_TooManyFramesToProcess = -10874;
    public const int kAudioUnitErr_InvalidFile = -10871;
    public const int kAudioUnitErr_FormatNotSupported = -10868;
    public const int kAudioUnitErr_Uninitialized = -10867;
    public const int kAudioUnitErr_InvalidScope = -10866;
    public const int kAudioUnitErr_PropertyNotWritable = -10865;
    public const int kAudioUnitErr_CannotDoInCurrentContext = -10863;
    public const int kAudioUnitErr_InvalidPropertyValue = -10851;
    public const int kAudioUnitErr_PropertyNotInUse = -10850;
    public const int kAudioUnitErr_Initialized = -10849;
    public const int kAudioUnitErr_InvalidOfflineRender = -10848;
    public const int kAudioUnitErr_Unauthorized = -10847;

    // Audio component types
    public const uint kAudioUnitType_Output = 0x61756F75; // 'auou'
    public const uint kAudioUnitSubType_DefaultOutput = 0x64656620; // 'def '
    public const uint kAudioUnitSubType_HALOutput = 0x61686F6C; // 'ahol'
    public const uint kAudioUnitManufacturer_Apple = 0x6170706C; // 'appl'

    // Audio unit scopes
    public const uint kAudioUnitScope_Global = 0;
    public const uint kAudioUnitScope_Input = 1;
    public const uint kAudioUnitScope_Output = 2;

    // Audio unit properties
    public const uint kAudioUnitProperty_StreamFormat = 8;
    public const uint kAudioUnitProperty_SetRenderCallback = 23;
    public const uint kAudioUnitProperty_Latency = 12;
    public const uint kAudioUnitProperty_SampleRate = 2;
    public const uint kAudioOutputUnitProperty_CurrentDevice = 2000;
    public const uint kAudioOutputUnitProperty_EnableIO = 2003;

    // AudioStreamBasicDescription format IDs
    public const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'

    // AudioStreamBasicDescription format flags
    public const uint kAudioFormatFlagIsFloat = 1;
    public const uint kAudioFormatFlagIsBigEndian = 2;
    public const uint kAudioFormatFlagIsSignedInteger = 4;
    public const uint kAudioFormatFlagIsPacked = 8;
    public const uint kAudioFormatFlagIsAlignedHigh = 16;
    public const uint kAudioFormatFlagIsNonInterleaved = 32;
    public const uint kAudioFormatFlagIsNonMixable = 64;
    public const uint kAudioFormatFlagsNativeEndian = 0; // Little endian on macOS/iOS

    // Standard flags for native float
    public const uint kAudioFormatFlagsNativeFloatPacked =
        kAudioFormatFlagIsFloat | kAudioFormatFlagsNativeEndian | kAudioFormatFlagIsPacked;

    /// <summary>Describes the format of an audio stream.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    /// <summary>Describes an audio component (unit) type.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioComponentDescription
    {
        public uint componentType;
        public uint componentSubType;
        public uint componentManufacturer;
        public uint componentFlags;
        public uint componentFlagsMask;
    }

    /// <summary>Audio buffer for render callback.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint mNumberChannels;
        public uint mDataByteSize;
        public nint mData;
    }

    /// <summary>Audio buffer list for render callback.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        public uint mNumberBuffers;
        // AudioBuffer mBuffers[1] - variable length, access via pointer offset
    }

    /// <summary>Timestamp for audio rendering.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioTimeStamp
    {
        public double mSampleTime;
        public ulong mHostTime;
        public double mRateScalar;
        public ulong mWordClockTime;
        public uint mSMPTETimeFlags;      // SMPTETime struct
        public short mSMPTETimeHours;
        public short mSMPTETimeMinutes;
        public short mSMPTETimeSeconds;
        public short mSMPTETimeFrames;
        public uint mSMPTETimeSubframes;
        public uint mSMPTETimeSubframeDivisor;
        public uint mSMPTETimeType;
        public uint mFlags;
        public uint mReserved;
    }

    /// <summary>Render callback function signature.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AURenderCallback(
        nint inRefCon,
        ref uint ioActionFlags,
        ref AudioTimeStamp inTimeStamp,
        uint inBusNumber,
        uint inNumberFrames,
        nint ioData); // AudioBufferList*

    /// <summary>Render callback info struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AURenderCallbackStruct
    {
        public nint inputProc;
        public nint inputProcRefCon;
    }

    // AudioComponent functions
    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentFindNext")]
    public static partial nint AudioComponentFindNext(nint inComponent, in AudioComponentDescription inDesc);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentInstanceNew")]
    public static partial int AudioComponentInstanceNew(nint inComponent, out nint outInstance);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentInstanceDispose")]
    public static partial int AudioComponentInstanceDispose(nint inInstance);

    // AudioUnit functions
    [LibraryImport(AudioUnitLib, EntryPoint = "AudioUnitInitialize")]
    public static partial int AudioUnitInitialize(nint inUnit);

    [LibraryImport(AudioUnitLib, EntryPoint = "AudioUnitUninitialize")]
    public static partial int AudioUnitUninitialize(nint inUnit);

    [LibraryImport(AudioUnitLib, EntryPoint = "AudioOutputUnitStart")]
    public static partial int AudioOutputUnitStart(nint inUnit);

    [LibraryImport(AudioUnitLib, EntryPoint = "AudioOutputUnitStop")]
    public static partial int AudioOutputUnitStop(nint inUnit);

    [LibraryImport(AudioUnitLib, EntryPoint = "AudioUnitSetProperty")]
    public static partial int AudioUnitSetProperty(
        nint inUnit,
        uint inID,
        uint inScope,
        uint inElement,
        nint inData,
        uint inDataSize);

    [LibraryImport(AudioUnitLib, EntryPoint = "AudioUnitGetProperty")]
    public static partial int AudioUnitGetProperty(
        nint inUnit,
        uint inID,
        uint inScope,
        uint inElement,
        nint outData,
        ref uint ioDataSize);

    /// <summary>Creates an AudioStreamBasicDescription for the given format.</summary>
    public static AudioStreamBasicDescription CreateASBD(int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        uint formatFlags;
        if (isFloat)
        {
            formatFlags = kAudioFormatFlagsNativeFloatPacked;
        }
        else
        {
            formatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagsNativeEndian | kAudioFormatFlagIsPacked;
        }

        uint bytesPerFrame = (uint)(channels * bitsPerSample / 8);

        return new AudioStreamBasicDescription
        {
            mSampleRate = sampleRate,
            mFormatID = kAudioFormatLinearPCM,
            mFormatFlags = formatFlags,
            mBytesPerPacket = bytesPerFrame,
            mFramesPerPacket = 1,
            mBytesPerFrame = bytesPerFrame,
            mChannelsPerFrame = (uint)channels,
            mBitsPerChannel = (uint)bitsPerSample,
            mReserved = 0
        };
    }

    /// <summary>Gets a human-readable error description.</summary>
    public static string GetErrorString(int status)
    {
        return status switch
        {
            0 => "No error",
            kAudioUnitErr_InvalidProperty => "Invalid property",
            kAudioUnitErr_InvalidParameter => "Invalid parameter",
            kAudioUnitErr_InvalidElement => "Invalid element",
            kAudioUnitErr_NoConnection => "No connection",
            kAudioUnitErr_FailedInitialization => "Failed initialization",
            kAudioUnitErr_TooManyFramesToProcess => "Too many frames to process",
            kAudioUnitErr_FormatNotSupported => "Format not supported",
            kAudioUnitErr_Uninitialized => "Uninitialized",
            kAudioUnitErr_InvalidScope => "Invalid scope",
            kAudioUnitErr_PropertyNotWritable => "Property not writable",
            kAudioUnitErr_CannotDoInCurrentContext => "Cannot do in current context",
            kAudioUnitErr_InvalidPropertyValue => "Invalid property value",
            _ => $"OSStatus error {status}"
        };
    }
}

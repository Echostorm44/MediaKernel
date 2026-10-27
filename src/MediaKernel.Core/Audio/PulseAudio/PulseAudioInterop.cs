using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaKernel.Core.Audio.PulseAudio;

/// <summary>
/// PulseAudio native interop for Linux audio output.
/// Uses libpulse-simple for synchronous audio playback.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class PulseAudioInterop
{
    private const string LibPulseSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";

    // Sample format enum
    public const int PA_SAMPLE_U8 = 0;
    public const int PA_SAMPLE_ALAW = 1;
    public const int PA_SAMPLE_ULAW = 2;
    public const int PA_SAMPLE_S16LE = 3;
    public const int PA_SAMPLE_S16BE = 4;
    public const int PA_SAMPLE_FLOAT32LE = 5;
    public const int PA_SAMPLE_FLOAT32BE = 6;
    public const int PA_SAMPLE_S32LE = 7;
    public const int PA_SAMPLE_S32BE = 8;
    public const int PA_SAMPLE_S24LE = 9;
    public const int PA_SAMPLE_S24BE = 10;
    public const int PA_SAMPLE_S24_32LE = 11;
    public const int PA_SAMPLE_S24_32BE = 12;

    // Stream direction
    public const int PA_STREAM_PLAYBACK = 1;
    public const int PA_STREAM_RECORD = 2;
    public const int PA_STREAM_UPLOAD = 3;

    // Error codes
    public const int PA_OK = 0;
    public const int PA_ERR_ACCESS = 1;
    public const int PA_ERR_COMMAND = 2;
    public const int PA_ERR_INVALID = 3;
    public const int PA_ERR_EXIST = 4;
    public const int PA_ERR_NOENTITY = 5;
    public const int PA_ERR_CONNECTIONREFUSED = 6;
    public const int PA_ERR_PROTOCOL = 7;
    public const int PA_ERR_TIMEOUT = 8;
    public const int PA_ERR_AUTHKEY = 9;
    public const int PA_ERR_INTERNAL = 10;
    public const int PA_ERR_CONNECTIONTERMINATED = 11;
    public const int PA_ERR_KILLED = 12;
    public const int PA_ERR_INVALIDSERVER = 13;
    public const int PA_ERR_MODINIT = 14;
    public const int PA_ERR_BADSTATE = 15;
    public const int PA_ERR_NODATA = 16;
    public const int PA_ERR_VERSION = 17;
    public const int PA_ERR_TOOLARGE = 18;
    public const int PA_ERR_NOTSUPPORTED = 19;
    public const int PA_ERR_UNKNOWN = 20;
    public const int PA_ERR_NOEXTENSION = 21;
    public const int PA_ERR_OBSOLETE = 22;
    public const int PA_ERR_NOTIMPLEMENTED = 23;
    public const int PA_ERR_FORKED = 24;
    public const int PA_ERR_IO = 25;
    public const int PA_ERR_BUSY = 26;

    /// <summary>Sample specification for audio stream.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct pa_sample_spec
    {
        public int format;      // pa_sample_format_t
        public uint rate;       // Sample rate
        public byte channels;   // Number of channels
    }

    /// <summary>Buffer attributes for latency control.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct pa_buffer_attr
    {
        public uint maxlength;  // Maximum length of the buffer
        public uint tlength;    // Target length of the buffer (playback only)
        public uint prebuf;     // Pre-buffering (playback only)
        public uint minreq;     // Minimum request size (playback only)
        public uint fragsize;   // Fragment size (recording only)
    }

    /// <summary>Channel map for multi-channel audio.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct pa_channel_map
    {
        public byte channels;
        public fixed byte map[32]; // PA_CHANNELS_MAX
    }

    // pa_simple API (synchronous, simple API)
    
    /// <summary>
    /// Create a new connection to the PulseAudio server.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_new")]
    public static partial nint pa_simple_new(
        nint server,                    // Server name, or NULL for default
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,  // Application name
        int dir,                        // Stream direction
        nint dev,                       // Sink/source name, or NULL for default
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stream_name, // Stream name
        in pa_sample_spec ss,           // Sample format
        nint map,                       // Channel map, or NULL for default
        nint attr,                      // Buffer attributes, or NULL for default
        out int error);                 // Error code output

    /// <summary>
    /// Create a new connection with buffer attributes.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_new")]
    public static partial nint pa_simple_new_with_attr(
        nint server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int dir,
        nint dev,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stream_name,
        in pa_sample_spec ss,
        nint map,
        in pa_buffer_attr attr,
        out int error);

    /// <summary>
    /// Write audio data to the stream.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_write")]
    public static partial int pa_simple_write(nint s, nint data, nuint bytes, out int error);

    /// <summary>
    /// Wait until all data in the buffer has been played.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_drain")]
    public static partial int pa_simple_drain(nint s, out int error);

    /// <summary>
    /// Flush the playback buffer, discarding all data.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_flush")]
    public static partial int pa_simple_flush(nint s, out int error);

    /// <summary>
    /// Get the current latency in microseconds.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_get_latency")]
    public static partial ulong pa_simple_get_latency(nint s, out int error);

    /// <summary>
    /// Close and free the connection.
    /// </summary>
    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_free")]
    public static partial void pa_simple_free(nint s);

    // Utility functions from libpulse

    /// <summary>
    /// Get a human-readable error string.
    /// </summary>
    [LibraryImport(LibPulse, EntryPoint = "pa_strerror")]
    public static partial nint pa_strerror(int error);

    /// <summary>
    /// Calculate the number of bytes per frame.
    /// </summary>
    [LibraryImport(LibPulse, EntryPoint = "pa_frame_size")]
    public static partial nuint pa_frame_size(in pa_sample_spec spec);

    /// <summary>
    /// Calculate bytes to microseconds.
    /// </summary>
    [LibraryImport(LibPulse, EntryPoint = "pa_bytes_to_usec")]
    public static partial ulong pa_bytes_to_usec(ulong length, in pa_sample_spec spec);

    /// <summary>
    /// Calculate microseconds to bytes.
    /// </summary>
    [LibraryImport(LibPulse, EntryPoint = "pa_usec_to_bytes")]
    public static partial nuint pa_usec_to_bytes(ulong t, in pa_sample_spec spec);

    // Channel position constants (for pa_channel_map)
    public const byte PA_CHANNEL_POSITION_MONO = 0;
    public const byte PA_CHANNEL_POSITION_FRONT_LEFT = 1;
    public const byte PA_CHANNEL_POSITION_FRONT_RIGHT = 2;
    public const byte PA_CHANNEL_POSITION_FRONT_CENTER = 3;
    public const byte PA_CHANNEL_POSITION_LFE = 5;
    public const byte PA_CHANNEL_POSITION_REAR_LEFT = 4;
    public const byte PA_CHANNEL_POSITION_REAR_RIGHT = 6;
    public const byte PA_CHANNEL_POSITION_SIDE_LEFT = 9;
    public const byte PA_CHANNEL_POSITION_SIDE_RIGHT = 10;

    /// <summary>Helper to get error string as managed string.</summary>
    public static string GetErrorString(int error)
    {
        var ptr = pa_strerror(error);
        return ptr != 0 ? Marshal.PtrToStringUTF8(ptr) ?? $"Error {error}" : $"Error {error}";
    }

    /// <summary>Maps our AudioFormat to PulseAudio sample format.</summary>
    public static int GetPaSampleFormat(int bitsPerSample, bool isFloat)
    {
        if (isFloat)
        {
            return BitConverter.IsLittleEndian ? PA_SAMPLE_FLOAT32LE : PA_SAMPLE_FLOAT32BE;
        }

        return bitsPerSample switch
        {
            8 => PA_SAMPLE_U8,
            16 => BitConverter.IsLittleEndian ? PA_SAMPLE_S16LE : PA_SAMPLE_S16BE,
            24 => BitConverter.IsLittleEndian ? PA_SAMPLE_S24LE : PA_SAMPLE_S24BE,
            32 => BitConverter.IsLittleEndian ? PA_SAMPLE_S32LE : PA_SAMPLE_S32BE,
            _ => PA_SAMPLE_S16LE
        };
    }
}

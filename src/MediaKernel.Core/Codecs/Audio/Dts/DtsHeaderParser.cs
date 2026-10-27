// DTS (Digital Theater Systems) audio codec support
// Reference: ETSI TS 102 114, DTS Coherent Acoustics specification

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary>
/// DTS sync word formats (different byte orderings and bit packings).
/// </summary>
public enum DtsSyncFormat
{
    /// <summary>Core stream, 16-bit big-endian words.</summary>
    Core16BitBigEndian,
    
    /// <summary>Core stream, 16-bit little-endian words.</summary>
    Core16BitLittleEndian,
    
    /// <summary>Core stream, 14-bit words in 16-bit big-endian containers.</summary>
    Core14BitBigEndian,
    
    /// <summary>Core stream, 14-bit words in 16-bit little-endian containers.</summary>
    Core14BitLittleEndian,
    
    /// <summary>DTS-HD substream extension.</summary>
    SubstreamExtension,
    
    /// <summary>DTS Express (LBR) substream.</summary>
    SubstreamLowBitrate,
    
    /// <summary>Unknown or unsupported format.</summary>
    Unknown
}

/// <summary>
/// DTS audio channel configuration.
/// </summary>
public enum DtsChannelLayout : byte
{
    Mono = 0,                    // A
    DualMono = 1,               // A + B
    Stereo = 2,                 // L + R (LoRo)
    StereoSumo = 3,             // (L+R) + (L-R)
    StereoTotal = 4,            // LT + RT
    ThreeZeroStereo = 5,        // C + L + R
    TwoOneStereo = 6,           // L + R + S
    ThreeOneStereo = 7,         // C + L + R + S
    TwoTwoStereo = 8,           // L + R + Ls + Rs
    ThreeTwoStereo = 9,         // C + L + R + Ls + Rs
    ThreeTwoLfe = 10,           // C + L + R + Ls + Rs + LFE
    ThreeTwoBack = 11,          // C + L + R + Ls + Rs + Cs
    Reserved12 = 12,
    Reserved13 = 13,
    Reserved14 = 14,
    Reserved15 = 15,
    // Higher values encode discrete channel counts
}

/// <summary>
/// DTS codec profile/variant.
/// </summary>
public enum DtsProfile
{
    /// <summary>Standard DTS core audio.</summary>
    Core,
    
    /// <summary>DTS-HD with extension substreams (lossless or lossy).</summary>
    DtsHd,
    
    /// <summary>DTS Express (low bitrate variant).</summary>
    DtsExpress,
    
    /// <summary>DTS-HD Master Audio (lossless).</summary>
    DtsHdMasterAudio
}

/// <summary>
/// DTS frame header information.
/// </summary>
public readonly struct DtsFrameHeader
{
    /// <summary>Sync word format detected.</summary>
    public DtsSyncFormat SyncFormat { get; init; }
    
    /// <summary>DTS profile/variant.</summary>
    public DtsProfile Profile { get; init; }
    
    /// <summary>Whether this is a termination frame.</summary>
    public bool IsTerminationFrame { get; init; }
    
    /// <summary>Number of PCM sample blocks (NBLKS + 1) × 32.</summary>
    public int SampleBlockCount { get; init; }
    
    /// <summary>Total frame size in bytes.</summary>
    public int FrameSize { get; init; }
    
    /// <summary>Audio channel configuration.</summary>
    public DtsChannelLayout ChannelLayout { get; init; }
    
    /// <summary>Number of physical audio channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Nominal bitrate in bits per second (0 = variable/open).</summary>
    public int Bitrate { get; init; }
    
    /// <summary>Whether CRC is present in the frame.</summary>
    public bool HasCrc { get; init; }
    
    /// <summary>Whether LFE channel is present.</summary>
    public bool HasLfe { get; init; }
    
    /// <summary>LFE sample count per frame (0, 64, or 128).</summary>
    public int LfeSampleCount { get; init; }
    
    /// <summary>Whether dynamic range control metadata is present.</summary>
    public bool HasDynamicRange { get; init; }
    
    /// <summary>Whether timestamp is embedded in the frame.</summary>
    public bool HasTimestamp { get; init; }
    
    /// <summary>Whether auxiliary data is present.</summary>
    public bool HasAuxiliaryData { get; init; }
    
    /// <summary>Whether HDCD mastering is indicated.</summary>
    public bool IsHdcdMastered { get; init; }
    
    /// <summary>Extension audio identifier (0 = none).</summary>
    public int ExtensionAudioId { get; init; }
    
    /// <summary>Whether extension audio stream is present.</summary>
    public bool HasExtensionAudio { get; init; }
    
    /// <summary>Total samples in this frame.</summary>
    public int SamplesPerFrame => SampleBlockCount * 32;
    
    /// <summary>Frame duration in microseconds.</summary>
    public long DurationMicroseconds => SampleRate > 0 ? (long)SamplesPerFrame * 1_000_000 / SampleRate : 0;
}

/// <summary>
/// DTS frame header parser.
/// </summary>
/// <remarks>
/// Parses DTS core and extension headers for demuxer support.
/// Full decoding requires MDCT and polyphase filterbank implementations.
/// </remarks>
public static class DtsHeaderParser
{
    // Sync words
    private const uint SyncCore16BigEndian = 0x7FFE8001;
    private const uint SyncCore16LittleEndian = 0xFE7F0180;
    private const uint SyncCore14BigEndian1 = 0x1FFFE800;
    private const uint SyncCore14LittleEndian1 = 0xFF1F00E8;
    private const uint SyncSubstream = 0x64582025;
    private const uint SyncLowBitrate = 0x0A801921;
    
    // Sample rate lookup table (index 0-15)
    private static readonly int[] SampleRateLookup =
    [
        0,      // Invalid
        8000,
        16000,
        32000,
        0,      // Invalid
        0,      // Invalid
        11025,
        22050,
        44100,
        0,      // Invalid
        0,      // Invalid
        12000,
        24000,
        48000,
        96000,
        192000
    ];
    
    // Bitrate lookup table (index 0-31)
    private static readonly int[] BitrateLookup =
    [
        32000,    // 0
        56000,    // 1
        64000,    // 2
        96000,    // 3
        112000,   // 4
        128000,   // 5
        192000,   // 6
        224000,   // 7
        256000,   // 8
        320000,   // 9
        384000,   // 10
        448000,   // 11
        512000,   // 12
        576000,   // 13
        640000,   // 14
        768000,   // 15
        960000,   // 16
        1024000,  // 17
        1152000,  // 18
        1280000,  // 19
        1344000,  // 20
        1408000,  // 21
        1411200,  // 22
        1472000,  // 23
        1536000,  // 24
        1920000,  // 25
        2048000,  // 26
        3072000,  // 27
        3840000,  // 28
        0,        // 29 - Open (variable)
        0,        // 30 - Invalid
        0         // 31 - Invalid
    ];
    
    // Channel count lookup for audio modes 0-15
    private static readonly int[] ChannelCountLookup =
    [
        1,  // 0: Mono
        2,  // 1: Dual mono
        2,  // 2: Stereo
        2,  // 3: Stereo sum/difference
        2,  // 4: Stereo total
        3,  // 5: 3/0 (C + L + R)
        3,  // 6: 2/1 (L + R + S)
        4,  // 7: 3/1 (C + L + R + S)
        4,  // 8: 2/2 (L + R + Ls + Rs)
        5,  // 9: 3/2 (C + L + R + Ls + Rs)
        6,  // 10: 3/2 + LFE
        6,  // 11: 3/2 + Cs
        6,  // 12: Reserved
        7,  // 13: Reserved
        8,  // 14: Reserved
        8   // 15: Reserved (up to 8 channels)
    ];
    
    /// <summary>
    /// Attempts to detect and parse a DTS frame header.
    /// </summary>
    /// <param name="data">Input data (at least 14 bytes recommended).</param>
    /// <param name="header">Parsed header on success.</param>
    /// <returns>True if a valid DTS header was parsed.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out DtsFrameHeader header)
    {
        header = default;
        
        if (data.Length < 14)
            return false;
        
        // Detect sync format
        var syncFormat = DetectSyncFormat(data);
        
        if (syncFormat == DtsSyncFormat.Unknown)
            return false;
        
        // Handle substream extensions
        if (syncFormat == DtsSyncFormat.SubstreamExtension)
            return TryParseSubstreamHeader(data, out header);
        
        if (syncFormat == DtsSyncFormat.SubstreamLowBitrate)
            return TryParseLbrHeader(data, out header);
        
        // Parse core header
        return TryParseCoreHeader(data, syncFormat, out header);
    }
    
    /// <summary>
    /// Detects the DTS sync format from the first bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DtsSyncFormat DetectSyncFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return DtsSyncFormat.Unknown;
        
        uint sync = BinaryPrimitives.ReadUInt32BigEndian(data);
        
        // Check 32-bit sync words first
        switch (sync)
        {
            case SyncCore16BigEndian:
                return DtsSyncFormat.Core16BitBigEndian;
            case SyncCore16LittleEndian:
                return DtsSyncFormat.Core16BitLittleEndian;
            case SyncSubstream:
                return DtsSyncFormat.SubstreamExtension;
            case SyncLowBitrate:
                return DtsSyncFormat.SubstreamLowBitrate;
        }
        
        // Check 14-bit formats (need 6 bytes for full sync detection)
        if (data.Length >= 6)
        {
            // 14-bit big-endian: 0x1FFFE800 followed by 0x07F for full pattern
            if ((sync & 0xFFFFFFF0) == 0x1FFFE800)
                return DtsSyncFormat.Core14BitBigEndian;
            
            // 14-bit little-endian: 0xFF1F00E8 pattern
            if (sync == SyncCore14LittleEndian1)
                return DtsSyncFormat.Core14BitLittleEndian;
        }
        
        return DtsSyncFormat.Unknown;
    }
    
    /// <summary>
    /// Finds the next DTS sync word in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="format">Detected sync format (if found).</param>
    /// <returns>Offset to the sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data, out DtsSyncFormat format)
    {
        format = DtsSyncFormat.Unknown;
        
        for (int i = 0; i <= data.Length - 6; i++)
        {
            var detected = DetectSyncFormat(data[i..]);
            if (detected != DtsSyncFormat.Unknown)
            {
                format = detected;
                return i;
            }
        }
        
        return -1;
    }
    
    /// <summary>
    /// Parses a DTS core frame header.
    /// </summary>
    private static bool TryParseCoreHeader(ReadOnlySpan<byte> data, DtsSyncFormat format, out DtsFrameHeader header)
    {
        header = default;
        
        // Pre-allocate buffer for format conversion
        Span<byte> convertedBuffer = stackalloc byte[16];
        bool useConverted = false;
        
        if (format == DtsSyncFormat.Core14BitBigEndian || format == DtsSyncFormat.Core14BitLittleEndian)
        {
            if (data.Length < 18)
                return false;
            
            Convert14BitTo16Bit(data, convertedBuffer, format == DtsSyncFormat.Core14BitLittleEndian);
            useConverted = true;
        }
        else if (format == DtsSyncFormat.Core16BitLittleEndian)
        {
            // Swap bytes for little-endian
            if (data.Length < 14)
                return false;
            
            for (int i = 0; i < 14; i += 2)
            {
                convertedBuffer[i] = data[i + 1];
                convertedBuffer[i + 1] = data[i];
            }
            useConverted = true;
        }
        
        // Select which buffer to use - can't assign span to another span across branches
        // so we read directly from the appropriate source
        
        // Skip sync word (4 bytes) and start parsing at bit 32
        // Parse bits using big-endian bit addressing
        
        // Bit 32: FTYPE (frame type)
        // Bit 33-37: SHORT (deficit sample count, 0-31)
        // Bit 38: CPF (CRC present flag)
        // Bit 39-45: NBLKS (number of PCM blocks minus 1)
        // Bit 46-59: FSIZE (frame byte size minus 1)
        // Bit 60-65: AMODE (audio channel arrangement)
        // Bit 66-69: SFREQ (sample frequency)
        // Bit 70-74: RATE (bit rate)
        // Bit 75: Fixed bit (must be 1)
        // Bit 76: DYNF (embedded dynamic range)
        // Bit 77: TIMEF (embedded time stamp)
        // Bit 78: AUXF (auxiliary data)
        // Bit 79: HDCD (HDCD mastered)
        // Bit 80-82: EXT_AUDIO_ID
        // Bit 83: EXT_AUDIO
        // Bit 84: ASPF (audio sync word present)
        // Bit 85-86: LFE (LFE channel flag)
        
        // Read bytes 4-13 for header fields - use correct buffer
        byte b4, b5, b6, b7, b8, b9, b10, b11, b12, b13;
        if (useConverted)
        {
            b4 = convertedBuffer[4]; b5 = convertedBuffer[5]; b6 = convertedBuffer[6]; b7 = convertedBuffer[7];
            b8 = convertedBuffer[8]; b9 = convertedBuffer[9]; b10 = convertedBuffer[10]; b11 = convertedBuffer[11];
            b12 = convertedBuffer[12]; b13 = convertedBuffer[13];
        }
        else
        {
            b4 = data[4]; b5 = data[5]; b6 = data[6]; b7 = data[7];
            b8 = data[8]; b9 = data[9]; b10 = data[10]; b11 = data[11];
            b12 = data[12]; b13 = data[13];
        }
        
        ulong bits0 = ((ulong)b4 << 56) | ((ulong)b5 << 48) |
                      ((ulong)b6 << 40) | ((ulong)b7 << 32) |
                      ((ulong)b8 << 24) | ((ulong)b9 << 16) |
                      ((ulong)b10 << 8) | b11;
        
        uint bits1 = ((uint)b12 << 8) | b13;
        
        // Extract fields from bits
        bool frameType = ((bits0 >> 63) & 1) != 0;
        // int shortDeficit = (int)((bits0 >> 58) & 0x1F);
        bool hasCrc = ((bits0 >> 57) & 1) != 0;
        int nblks = (int)((bits0 >> 50) & 0x7F);
        int fsize = (int)((bits0 >> 36) & 0x3FFF);
        int amode = (int)((bits0 >> 30) & 0x3F);
        int sfreq = (int)((bits0 >> 26) & 0x0F);
        int rate = (int)((bits0 >> 21) & 0x1F);
        // bool fixedBit = ((bits0 >> 20) & 1) != 0;
        bool dynf = ((bits0 >> 19) & 1) != 0;
        bool timef = ((bits0 >> 18) & 1) != 0;
        bool auxf = ((bits0 >> 17) & 1) != 0;
        bool hdcd = ((bits0 >> 16) & 1) != 0;
        int extAudioId = (int)((bits0 >> 13) & 0x07);
        bool extAudio = ((bits0 >> 12) & 1) != 0;
        // bool aspf = ((bits0 >> 11) & 1) != 0;
        int lfe = (int)((bits0 >> 9) & 0x03);
        
        // Validate NBLKS (must be 5-127)
        if (nblks < 5)
            return false;
        
        // Sample rate
        if (sfreq >= SampleRateLookup.Length || SampleRateLookup[sfreq] == 0)
            return false;
        
        int sampleRate = SampleRateLookup[sfreq];
        
        // Bitrate
        int bitrate = (rate < BitrateLookup.Length) ? BitrateLookup[rate] : 0;
        
        // Channel layout and count
        var channelLayout = (amode < 16) ? (DtsChannelLayout)amode : DtsChannelLayout.Reserved15;
        int channelCount = (amode < ChannelCountLookup.Length) ? ChannelCountLookup[amode] : amode - 7;
        
        // LFE samples per frame
        int lfeSamples = lfe switch
        {
            1 => 64,
            2 => 128,
            _ => 0
        };
        
        // Add LFE to channel count if present
        if (lfe > 0)
            channelCount++;
        
        header = new DtsFrameHeader
        {
            SyncFormat = format,
            Profile = extAudio ? DtsProfile.DtsHd : DtsProfile.Core,
            IsTerminationFrame = frameType,
            SampleBlockCount = nblks + 1,
            FrameSize = fsize + 1,
            ChannelLayout = channelLayout,
            ChannelCount = channelCount,
            SampleRate = sampleRate,
            Bitrate = bitrate,
            HasCrc = hasCrc,
            HasLfe = lfe > 0,
            LfeSampleCount = lfeSamples,
            HasDynamicRange = dynf,
            HasTimestamp = timef,
            HasAuxiliaryData = auxf,
            IsHdcdMastered = hdcd,
            ExtensionAudioId = extAudioId,
            HasExtensionAudio = extAudio
        };
        
        return true;
    }
    
    /// <summary>
    /// Converts 14-bit packed data to 16-bit words.
    /// </summary>
    private static void Convert14BitTo16Bit(ReadOnlySpan<byte> input, Span<byte> output, bool littleEndian)
    {
        // 14-bit format: 2 bytes contain 14 bits of data + 2 padding bits
        // Every 7 input bytes become 6 output bytes (14*4 = 56 bits = 7 bytes, produces 4*14 = 56 bits)
        
        int outIndex = 0;
        int bitBuffer = 0;
        int bitsInBuffer = 0;
        
        for (int i = 0; i < input.Length && outIndex < output.Length; i += 2)
        {
            if (i + 1 >= input.Length)
                break;
            
            // Read 16-bit word
            int word;
            if (littleEndian)
                word = input[i] | (input[i + 1] << 8);
            else
                word = (input[i] << 8) | input[i + 1];
            
            // Extract 14 bits (high bits are data, low 2 are padding)
            int bits = word >> 2;
            
            bitBuffer = (bitBuffer << 14) | bits;
            bitsInBuffer += 14;
            
            // Output complete bytes
            while (bitsInBuffer >= 8 && outIndex < output.Length)
            {
                bitsInBuffer -= 8;
                output[outIndex++] = (byte)(bitBuffer >> bitsInBuffer);
            }
        }
    }
    
    /// <summary>
    /// Parses a DTS-HD substream extension header.
    /// </summary>
    private static bool TryParseSubstreamHeader(ReadOnlySpan<byte> data, out DtsFrameHeader header)
    {
        header = default;
        
        if (data.Length < 12)
            return false;
        
        // DTS-HD extension header parsing
        // The extension follows the core stream and provides lossless audio
        
        // For now, return a basic header indicating DTS-HD presence
        header = new DtsFrameHeader
        {
            SyncFormat = DtsSyncFormat.SubstreamExtension,
            Profile = DtsProfile.DtsHd,
            IsTerminationFrame = false,
            SampleBlockCount = 0, // Determined by core
            FrameSize = (data[4] << 8) | data[5], // Approximate from header
            ChannelLayout = DtsChannelLayout.ThreeTwoStereo,
            ChannelCount = 6,
            SampleRate = 48000, // Common default
            Bitrate = 0, // Variable
            HasCrc = false,
            HasLfe = true,
            LfeSampleCount = 0,
            HasDynamicRange = false,
            HasTimestamp = false,
            HasAuxiliaryData = false,
            IsHdcdMastered = false,
            ExtensionAudioId = 0,
            HasExtensionAudio = true
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses a DTS Express (LBR) substream header.
    /// </summary>
    private static bool TryParseLbrHeader(ReadOnlySpan<byte> data, out DtsFrameHeader header)
    {
        header = default;
        
        if (data.Length < 8)
            return false;
        
        // DTS Express (Low Bitrate) - simplified header
        header = new DtsFrameHeader
        {
            SyncFormat = DtsSyncFormat.SubstreamLowBitrate,
            Profile = DtsProfile.DtsExpress,
            IsTerminationFrame = false,
            SampleBlockCount = 32, // Typical for LBR
            FrameSize = data.Length,
            ChannelLayout = DtsChannelLayout.Stereo,
            ChannelCount = 2,
            SampleRate = 48000,
            Bitrate = 128000, // Common for Express
            HasCrc = false,
            HasLfe = false,
            LfeSampleCount = 0,
            HasDynamicRange = false,
            HasTimestamp = false,
            HasAuxiliaryData = false,
            IsHdcdMastered = false,
            ExtensionAudioId = 0,
            HasExtensionAudio = false
        };
        
        return true;
    }
    
    /// <summary>
    /// Gets a human-readable description of the DTS frame.
    /// </summary>
    public static string GetDescription(in DtsFrameHeader header)
    {
        string profile = header.Profile switch
        {
            DtsProfile.Core => "DTS",
            DtsProfile.DtsHd => "DTS-HD",
            DtsProfile.DtsExpress => "DTS Express",
            DtsProfile.DtsHdMasterAudio => "DTS-HD MA",
            _ => "DTS"
        };
        
        string channels = header.ChannelCount switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{header.ChannelCount}ch"
        };
        
        string lfe = header.HasLfe ? " + LFE" : "";
        string bitrate = header.Bitrate > 0 ? $", {header.Bitrate / 1000} kbps" : "";
        
        return $"{profile} {channels}{lfe}, {header.SampleRate} Hz{bitrate}";
    }
}

// RETIRED 2026-10-19: class DtsDecoder lived here. It held a DecodeFrame that set
// samplesDecoded = 0, returned false and carried a comment listing the five things it would need
// to do - and it sat in the same namespace as the real decoder, which is a trap rather than a
// placeholder. DTS decoding is DcaCore (a port of dca_core.c) reached through
// DtsDecoderAdapter, gated by dts-core against ffmpeg -core_only 1.
//
// DtsHeaderParser above is kept because its sync format detection and 14-bit conversion are
// exercised by twenty passing tests and nothing else in the tree does that job yet. It is a
// RECONSTRUCTION, not a port: its lookup tables were written from the specification rather than
// translated from dca.c, and it has no production callers. Dca.ParseCoreFrameHeader is the
// translated one and is authoritative where the two disagree. Do not add callers to this.

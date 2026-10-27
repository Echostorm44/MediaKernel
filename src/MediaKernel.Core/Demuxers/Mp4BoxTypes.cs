namespace MediaKernel.Core.Demuxers;

/// <summary>
/// MP4/MOV box types (FourCC codes) as 32-bit integers.
/// Boxes are the fundamental structural elements in ISOBMFF containers.
/// </summary>
/// <remarks>
/// FourCC codes are stored big-endian: 'ftyp' = 0x66747970
/// </remarks>
public static class Mp4BoxType
{
    // File-level boxes
    public const uint FileType = 0x66747970;         // 'ftyp'
    public const uint Movie = 0x6D6F6F76;            // 'moov'
    public const uint MediaData = 0x6D646174;        // 'mdat'
    public const uint MovieFragment = 0x6D6F6F66;    // 'moof'
    public const uint Free = 0x66726565;             // 'free'
    public const uint Skip = 0x736B6970;             // 'skip'
    public const uint Uuid = 0x75756964;             // 'uuid'

    // Movie header boxes
    public const uint MovieHeader = 0x6D766864;      // 'mvhd'
    public const uint Track = 0x7472616B;            // 'trak'
    public const uint TrackHeader = 0x746B6864;      // 'tkhd'
    public const uint TrackReference = 0x74726566;   // 'tref'
    public const uint EditList = 0x656C7374;         // 'elst'
    public const uint Edit = 0x65647473;             // 'edts'

    // Media boxes
    public const uint Media = 0x6D646961;            // 'mdia'
    public const uint MediaHeader = 0x6D646864;      // 'mdhd'
    public const uint Handler = 0x68646C72;          // 'hdlr'
    public const uint MediaInfo = 0x6D696E66;        // 'minf'

    // Video/Audio specific info boxes
    public const uint VideoMediaHeader = 0x766D6864; // 'vmhd'
    public const uint SoundMediaHeader = 0x736D6864; // 'smhd'
    public const uint HintMediaHeader = 0x686D6864;  // 'hmhd'
    public const uint NullMediaHeader = 0x6E6D6864;  // 'nmhd'

    // Sample table boxes
    public const uint SampleTable = 0x7374626C;      // 'stbl'
    public const uint SampleDescription = 0x73747364; // 'stsd'
    public const uint TimeToSample = 0x73747473;     // 'stts'
    public const uint CompositionOffset = 0x63747473; // 'ctts'
    public const uint SampleToChunk = 0x73747363;    // 'stsc'
    public const uint SampleSize = 0x7374737A;       // 'stsz'
    public const uint CompactSampleSize = 0x73747A32; // 'stz2'
    public const uint ChunkOffset = 0x7374636F;      // 'stco'
    public const uint ChunkOffset64 = 0x636F3634;    // 'co64'
    public const uint SyncSample = 0x73747373;       // 'stss'
    public const uint ShadowSync = 0x73747368;       // 'stsh'

    // Data information boxes
    public const uint DataInfo = 0x64696E66;         // 'dinf'
    public const uint DataReference = 0x64726566;    // 'dref'
    public const uint DataEntryUrl = 0x75726C20;     // 'url '
    public const uint DataEntryUrn = 0x75726E20;     // 'urn '

    // Video codec boxes
    public const uint Avc1 = 0x61766331;             // 'avc1' (H.264)
    public const uint Avc2 = 0x61766332;             // 'avc2'
    public const uint Avc3 = 0x61766333;             // 'avc3'
    public const uint Avc4 = 0x61766334;             // 'avc4'
    public const uint AvcC = 0x61766343;             // 'avcC' (AVC decoder config)
    public const uint Hvc1 = 0x68766331;             // 'hvc1' (HEVC)
    public const uint Hev1 = 0x68657631;             // 'hev1' (HEVC)
    public const uint HvcC = 0x68766343;             // 'hvcC' (HEVC decoder config)
    public const uint Vp08 = 0x76703038;             // 'vp08' (VP8)
    public const uint Vp09 = 0x76703039;             // 'vp09' (VP9)
    public const uint VpcC = 0x76706343;             // 'vpcC' (VP9 decoder config)
    public const uint Av01 = 0x61763031;             // 'av01' (AV1)
    public const uint Av1C = 0x61763143;             // 'av1C' (AV1 decoder config)
    public const uint Dvc1 = 0x64766331;             // 'dvc1' (VC-1 decoder config)
    public const uint MovieExtends = 0x6D766578;     // 'mvex' (movie is fragmented)

    // The MJPEG family's QuickTime sample entries, from isom_tags.c. 'jpeg', 'mjpa', 'AVDJ',
    // 'AVRn' and 'dmb1' are all AV_CODEC_ID_MJPEG; 'mjpb' is format B, a different bitstream
    // with its own decoder; and the six Media 100 tags are a fourth.
    public const uint Jpeg = 0x6A706567;             // 'jpeg' (PhotoJPEG)
    public const uint Mjpa = 0x6D6A7061;             // 'mjpa' (Motion-JPEG format A)
    public const uint Avdj = 0x4156444A;             // 'AVDJ' (AVID JFIF meridien)
    public const uint Avrn = 0x4156526E;             // 'AVRn' (AVID ABVB / Truevision NuVista)
    public const uint Dmb1 = 0x646D6231;             // 'dmb1' (Motion JPEG OpenDML)
    public const uint Mjpb = 0x6D6A7062;             // 'mjpb' (Motion-JPEG format B)
    public const uint Cvid = 0x63766964;             // 'cvid' (Cinepak)
    public const uint M100_601N = 0x3630314E;        // '601N' (Media 100)
    public const uint M100_601P = 0x36303150;        // '601P'
    public const uint M100_dtnt = 0x64746E74;        // 'dtnt'
    public const uint M100_dtNT = 0x64744E54;        // 'dtNT'
    public const uint M100_dtpa = 0x64747061;        // 'dtpa'
    public const uint M100_dtPA = 0x64745041;        // 'dtPA'
    public const uint Mp4v = 0x6D703476;             // 'mp4v' (MPEG-4 Visual)
    public const uint Encv = 0x656E6376;             // 'encv' (encrypted video)

    // Audio codec boxes
    public const uint Mp4a = 0x6D703461;             // 'mp4a' (AAC)
    public const uint Ac3 = 0x61632D33;              // 'ac-3' (AC3)
    public const uint Ec3 = 0x65632D33;              // 'ec-3' (E-AC3)
    public const uint Opus = 0x4F707573;             // 'Opus'
    public const uint DOpus = 0x644F7073;            // 'dOps' (Opus decoder config)
    public const uint Flac = 0x664C6143;             // 'fLaC' (FLAC)
    public const uint Alac = 0x616C6163;             // 'alac' (Apple Lossless)
    public const uint Enca = 0x656E6361;             // 'enca' (encrypted audio)

    // Raw PCM sample entries. 'ipcm' and 'fpcm' are ISO 23003-5 and carry a 'pcmC' box
    // that states the width and the byte order; the rest are QuickTime's, where the tag
    // itself says the width and an 'enda' box may override the byte order.
    public const uint Ipcm = 0x6970636D;             // 'ipcm' (ISO integer PCM)
    public const uint Fpcm = 0x6670636D;             // 'fpcm' (ISO floating-point PCM)
    public const uint Sowt = 0x736F7774;             // 'sowt' (16-bit little-endian)
    public const uint Twos = 0x74776F73;             // 'twos' (16-bit big-endian)
    public const uint RawPcm = 0x72617720;           // 'raw ' (unsigned 8-bit)
    public const uint NonePcm = 0x4E4F4E45;          // 'NONE' (unsigned 8-bit)
    public const uint In24 = 0x696E3234;             // 'in24' (24-bit)
    public const uint In32 = 0x696E3332;             // 'in32' (32-bit)
    public const uint Fl32 = 0x666C3332;             // 'fl32' (32-bit float)
    public const uint Fl64 = 0x666C3634;             // 'fl64' (64-bit float)
    public const uint Alaw = 0x616C6177;             // 'alaw' (G.711 A-law)
    public const uint Ulaw = 0x756C6177;             // 'ulaw' (G.711 mu-law)

    // Subtitle/text boxes
    public const uint Text = 0x74657874;             // 'text'
    public const uint Tx3g = 0x74783367;             // 'tx3g' (3GPP timed text)
    public const uint Wvtt = 0x77767474;             // 'wvtt' (WebVTT)

    // Decoder config boxes
    public const uint Esds = 0x65736473;             // 'esds' (Elementary Stream Descriptor)
    public const uint Dac3 = 0x64616333;             // 'dac3' (AC3 decoder config)
    public const uint Dec3 = 0x64656333;             // 'dec3' (E-AC3 decoder config)
    public const uint DfLa = 0x64664C61;             // 'dfLa' (FLAC decoder config)
    public const uint PcmC = 0x70636D43;             // 'pcmC' (raw PCM configuration)
    public const uint Enda = 0x656E6461;             // 'enda' (QuickTime byte order)
    public const uint Wave = 0x77617665;             // 'wave' (QuickTime siDecompressionParam)

    // Color/HDR info
    public const uint Colr = 0x636F6C72;             // 'colr'
    public const uint Pasp = 0x70617370;             // 'pasp' (pixel aspect ratio)

    // Fragmented MP4 boxes
    public const uint MovieFragmentHeader = 0x6D666864;   // 'mfhd'
    public const uint TrackFragment = 0x74726166;        // 'traf'
    public const uint TrackFragmentHeader = 0x74666864;  // 'tfhd'
    public const uint TrackFragmentDecodeTime = 0x74666474; // 'tfdt'
    public const uint TrackRun = 0x7472756E;             // 'trun'
    public const uint SegmentIndex = 0x73696478;         // 'sidx'
    public const uint MovieFragmentRandomAccess = 0x6D667261; // 'mfra'
    public const uint TrackFragmentRandomAccess = 0x74667261; // 'tfra'
    public const uint MovieFragmentRandomAccessOffset = 0x6D66726F; // 'mfro'

    // Metadata boxes
    public const uint UserData = 0x75647461;         // 'udta'
    public const uint Meta = 0x6D657461;             // 'meta'
    public const uint Ilst = 0x696C7374;             // 'ilst' (iTunes metadata)
    public const uint ChapterList = 0x6368706C;      // 'chpl' (Nero chapter list)

    /// <summary>
    /// Converts a FourCC code to its string representation.
    /// </summary>
    public static string FourCCToString(uint fourCC)
    {
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)((fourCC >> 24) & 0xFF);
        chars[1] = (char)((fourCC >> 16) & 0xFF);
        chars[2] = (char)((fourCC >> 8) & 0xFF);
        chars[3] = (char)(fourCC & 0xFF);
        return new string(chars);
    }

    /// <summary>
    /// Converts a string FourCC to its numeric representation.
    /// </summary>
    public static uint StringToFourCC(ReadOnlySpan<char> fourCC)
    {
        if (fourCC.Length < 4)
            return 0;
        return ((uint)fourCC[0] << 24) | ((uint)fourCC[1] << 16) | 
               ((uint)fourCC[2] << 8) | (uint)fourCC[3];
    }
}

/// <summary>
/// Handler types that identify the media type within a track.
/// </summary>
public static class Mp4HandlerType
{
    public const uint Video = 0x76696465;   // 'vide'
    public const uint Audio = 0x736F756E;   // 'soun'
    public const uint Hint = 0x68696E74;    // 'hint'
    public const uint Text = 0x74657874;    // 'text'
    public const uint Subtitle = 0x7375626D; // 'subm' (subtitle media)
}

/// <summary>
/// Track types in MP4 containers.
/// </summary>
public enum Mp4TrackType
{
    Unknown,
    Video,
    Audio,
    Subtitle,
    Hint,
    Metadata
}

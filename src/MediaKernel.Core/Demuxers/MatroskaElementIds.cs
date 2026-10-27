namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Matroska/WebM element IDs as defined in the EBML specification.
/// These are the binary identifiers for each element type in an MKV file.
/// </summary>
/// <remarks>
/// Element IDs include their VINT length marker bits, making them directly
/// comparable to parsed values from EbmlParser.TryReadElementId().
/// 
/// Reference: https://www.matroska.org/technical/elements.html
/// </remarks>
public static class MatroskaElementId
{
    // EBML Header elements (Level 0)
    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint EbmlVersion = 0x4286;
    public const uint EbmlReadVersion = 0x42F7;
    public const uint EbmlMaxIdLength = 0x42F2;
    public const uint EbmlMaxSizeLength = 0x42F3;
    public const uint DocType = 0x4282;
    public const uint DocTypeVersion = 0x4287;
    public const uint DocTypeReadVersion = 0x4285;

    // Segment (Level 0)
    public const uint Segment = 0x18538067;

    // Top-level segment children (Level 1)
    public const uint SeekHead = 0x114D9B74;
    public const uint SegmentInfo = 0x1549A966;
    public const uint Cluster = 0x1F43B675;
    public const uint Tracks = 0x1654AE6B;
    public const uint Cues = 0x1C53BB6B;
    public const uint Attachments = 0x1941A469;
    public const uint Chapters = 0x1043A770;
    public const uint Tags = 0x1254C367;

    // SeekHead children
    public const uint Seek = 0x4DBB;
    public const uint SeekId = 0x53AB;
    public const uint SeekPosition = 0x53AC;

    // SegmentInfo children
    public const uint SegmentUid = 0x73A4;
    public const uint SegmentFilename = 0x7384;
    public const uint PrevUid = 0x3CB923;
    public const uint PrevFilename = 0x3C83AB;
    public const uint NextUid = 0x3EB923;
    public const uint NextFilename = 0x3E83BB;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint DateUtc = 0x4461;
    public const uint Title = 0x7BA9;
    public const uint MuxingApp = 0x4D80;
    public const uint WritingApp = 0x5741;

    // Tracks children
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackUid = 0x73C5;
    public const uint TrackType = 0x83;
    public const uint FlagEnabled = 0xB9;
    public const uint FlagDefault = 0x88;
    public const uint FlagForced = 0x55AA;
    public const uint FlagLacing = 0x9C;
    public const uint MinCache = 0x6DE7;
    public const uint MaxCache = 0x6DF8;
    public const uint DefaultDuration = 0x23E383;
    public const uint TrackTimestampScale = 0x23314F;
    public const uint MaxBlockAdditionId = 0x55EE;
    public const uint Name = 0x536E;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint CodecName = 0x258688;
    public const uint AttachmentLink = 0x7446;

    // Video track settings
    public const uint Video = 0xE0;
    public const uint FlagInterlaced = 0x9A;
    public const uint FieldOrder = 0x9D;
    public const uint StereoMode = 0x53B8;
    public const uint AlphaMode = 0x53C0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint PixelCropBottom = 0x54AA;
    public const uint PixelCropTop = 0x54BB;
    public const uint PixelCropLeft = 0x54CC;
    public const uint PixelCropRight = 0x54DD;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;
    public const uint DisplayUnit = 0x54B2;
    public const uint AspectRatioType = 0x54B3;
    public const uint ColourSpace = 0x2EB524;
    public const uint Colour = 0x55B0;
    public const uint MatrixCoefficients = 0x55B1;
    public const uint BitsPerChannel = 0x55B2;
    public const uint ChromaSubsamplingHorz = 0x55B3;
    public const uint ChromaSubsamplingVert = 0x55B4;
    public const uint TransferCharacteristics = 0x55BA;
    public const uint Primaries = 0x55BB;
    public const uint MaxCll = 0x55BC;
    public const uint MaxFall = 0x55BD;
    public const uint Range = 0x55B9;

    // Audio track settings
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint OutputSamplingFrequency = 0x78B5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;

    // Content encoding (compression)
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentEncodingOrder = 0x5031;
    public const uint ContentEncodingScope = 0x5032;
    public const uint ContentEncodingType = 0x5033;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;

    // Cluster children
    public const uint Timestamp = 0xE7;
    public const uint Position = 0xA7;
    public const uint PrevSize = 0xAB;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockAdditions = 0x75A1;
    public const uint BlockMore = 0xA6;
    public const uint BlockAddId = 0xEE;
    public const uint BlockAdditional = 0xA5;
    public const uint BlockDuration = 0x9B;
    public const uint ReferencePriority = 0xFA;
    public const uint ReferenceBlock = 0xFB;
    public const uint DiscardPadding = 0x75A2;

    // Cues children (seek index)
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint CueRelativePosition = 0xF0;
    public const uint CueDuration = 0xB2;
    public const uint CueBlockNumber = 0x5378;

    // Chapters children
    public const uint EditionEntry = 0x45B9;
    public const uint EditionUid = 0x45BC;
    public const uint EditionFlagHidden = 0x45BD;
    public const uint EditionFlagDefault = 0x45DB;
    public const uint EditionFlagOrdered = 0x45DD;
    public const uint ChapterAtom = 0xB6;
    public const uint ChapterUid = 0x73C4;
    public const uint ChapterStringUid = 0x5654;
    public const uint ChapterTimeStart = 0x91;
    public const uint ChapterTimeEnd = 0x92;
    public const uint ChapterFlagHidden = 0x98;
    public const uint ChapterFlagEnabled = 0x4598;
    public const uint ChapterDisplay = 0x80;
    public const uint ChapString = 0x85;
    public const uint ChapLanguage = 0x437C;
    public const uint ChapLanguageBcp47 = 0x437D;
    public const uint ChapCountry = 0x437E;

    // Void element (padding/deleted content)
    public const uint Void = 0xEC;
    public const uint Crc32 = 0xBF;
}

/// <summary>
/// Track types in Matroska.
/// </summary>
public enum MatroskaTrackType : byte
{
    Video = 1,
    Audio = 2,
    Complex = 3,
    Logo = 0x10,
    Subtitle = 0x11,
    Buttons = 0x12,
    Control = 0x20,
    Metadata = 0x21
}

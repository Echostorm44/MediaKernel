namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// NAL unit types as defined in ITU-T H.264 / ISO 14496-10 Table 7-1.
    /// </summary>
    public enum NalUnitType : byte
    {
        /// <summary>Unspecified NAL unit type.</summary>
        Unspecified = 0,

        /// <summary>Non-IDR coded slice.</summary>
        CodedSliceNonIdr = 1,

        /// <summary>Coded slice data partition A.</summary>
        CodedSliceDataPartitionA = 2,

        /// <summary>Coded slice data partition B.</summary>
        CodedSliceDataPartitionB = 3,

        /// <summary>Coded slice data partition C.</summary>
        CodedSliceDataPartitionC = 4,

        /// <summary>Coded slice of an IDR picture (key frame).</summary>
        CodedSliceIdr = 5,

        /// <summary>Supplemental enhancement information (SEI).</summary>
        SupplementalEnhancementInfo = 6,

        /// <summary>Sequence parameter set (SPS).</summary>
        SequenceParameterSet = 7,

        /// <summary>Picture parameter set (PPS).</summary>
        PictureParameterSet = 8,

        /// <summary>Access unit delimiter.</summary>
        AccessUnitDelimiter = 9,

        /// <summary>End of sequence.</summary>
        EndOfSequence = 10,

        /// <summary>End of stream.</summary>
        EndOfStream = 11,

        /// <summary>Filler data.</summary>
        FillerData = 12,

        /// <summary>Sequence parameter set extension.</summary>
        SequenceParameterSetExtension = 13,

        /// <summary>Prefix NAL unit.</summary>
        PrefixNalUnit = 14,

        /// <summary>Subset sequence parameter set.</summary>
        SubsetSequenceParameterSet = 15,

        /// <summary>Depth parameter set (reserved).</summary>
        DepthParameterSet = 16,

        // 17-18: Reserved

        /// <summary>Coded slice of an auxiliary coded picture without partitioning.</summary>
        CodedSliceAuxiliary = 19,

        /// <summary>Coded slice extension.</summary>
        CodedSliceExtension = 20,

        /// <summary>Coded slice extension for depth view components.</summary>
        CodedSliceExtensionDepth = 21,

        // 22-23: Reserved

        // 24-31: Unspecified
    }

    /// <summary>
    /// H.264 profile identifiers as defined in Annex A.
    /// </summary>
    public enum H264Profile : byte
    {
        /// <summary>Baseline profile for low-complexity applications.</summary>
        Baseline = 66,

        /// <summary>Main profile for standard applications.</summary>
        Main = 77,

        /// <summary>Scalable baseline profile.</summary>
        ScalableBaseline = 83,

        /// <summary>Scalable high profile.</summary>
        ScalableHigh = 86,

        /// <summary>Extended profile.</summary>
        Extended = 88,

        /// <summary>High profile for broadcast and storage.</summary>
        High = 100,

        /// <summary>High 10 profile with 10-bit samples.</summary>
        High10 = 110,

        /// <summary>Multiview high profile.</summary>
        MultiviewHigh = 118,

        /// <summary>High 4:2:2 profile.</summary>
        High422 = 122,

        /// <summary>Stereo high profile for 3D.</summary>
        StereoHigh = 128,

        /// <summary>Multiview depth high profile.</summary>
        MultiviewDepthHigh = 138,

        /// <summary>High 4:4:4 predictive profile.</summary>
        High444Predictive = 244,
    }

    /// <summary>
    /// Slice types as defined in Table 7-6.
    /// </summary>
    public enum SliceType
    {
        /// <summary>P slice - uses prediction from previous frames.</summary>
        PSlice = 0,

        /// <summary>B slice - uses bi-directional prediction.</summary>
        BSlice = 1,

        /// <summary>I slice - intra-coded only.</summary>
        ISlice = 2,

        /// <summary>SP slice - switching P slice.</summary>
        SpSlice = 3,

        /// <summary>SI slice - switching I slice.</summary>
        SiSlice = 4,

        // Values 5-9 are the same slice types but indicate all slices in picture are this type
        PSliceAll = 5,
        BSliceAll = 6,
        ISliceAll = 7,
        SpSliceAll = 8,
        SiSliceAll = 9,
    }

    /// <summary>
    /// Chroma format identifiers as defined in Table 6-1.
    /// </summary>
    public enum ChromaFormat : byte
    {
        /// <summary>Monochrome - no chroma samples.</summary>
        Monochrome = 0,

        /// <summary>4:2:0 - chroma subsampled by 2 in both dimensions.</summary>
        Chroma420 = 1,

        /// <summary>4:2:2 - chroma subsampled by 2 horizontally only.</summary>
        Chroma422 = 2,

        /// <summary>4:4:4 - no chroma subsampling.</summary>
        Chroma444 = 3,
    }
}

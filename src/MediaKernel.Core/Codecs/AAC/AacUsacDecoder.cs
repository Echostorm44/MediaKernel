// USAC (xHE-AAC): a port of ffmpeg-master/libavcodec/aac/aacdec_usac.c.
//
// Scope. This covers the frequency-domain path, which is what USAC streams in the wild and both
// FATE conformance streams actually use. Four things in the C file are deliberately not here, and
// each one throws rather than being silently skipped:
//
//  * eSBR (decode_usac_sbr_data, ff_aac_sbr_decode_usac_data) and MPS212
//    (aacdec_usac_mps212.c, parse_mps212, decode_tsd). Both are gated behind sbr.ratio, which is
//    derived from coreSbrFrameLengthIndex, and both FATE streams have index 1 - ratio 0. With no
//    SBR, decode_usac_element_pair never reads a stereo_config_index, so it stays 0, and every
//    MPS212 path is behind a non-zero one.
//  * LPD (aacdec_lpd.c). FFmpeg's own LPD is parse-only: it reads the modes and the FAC data and
//    returns without synthesising audio. FFmpeg scores MAXDIFF 1 against both conformance
//    references, which it could not do if either stream carried an LPD frame - so neither does.
//  * The 768-coefficient core (coreSbrFrameLengthIndex 0 or 2), which needs ff_swb_offset_768
//    and ff_swb_offset_96. Neither stream uses it.
//  * Time warping, which the C itself reports as missing and refuses.
//
// The port checklist lists all 29 functions of the C file and what
// each one's status is.

using System;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary><c>usacElementType</c>.</summary>
public enum UsacElementType
{
    /// <summary><c>ID_USAC_SCE</c>.</summary>
    SingleChannel = 0,

    /// <summary><c>ID_USAC_CPE</c>.</summary>
    ChannelPair = 1,

    /// <summary><c>ID_USAC_LFE</c>.</summary>
    Lfe = 2,

    /// <summary><c>ID_USAC_EXT</c>.</summary>
    Extension = 3,
}

/// <summary><c>usacExtElementType</c>, for the values this decoder distinguishes.</summary>
public enum UsacExtElementType
{
    /// <summary><c>ID_EXT_ELE_FILL</c>.</summary>
    Fill = 0,

    /// <summary><c>ID_EXT_ELE_MPEGS</c>.</summary>
    MpegSurround = 1,

    /// <summary><c>ID_EXT_ELE_SAOC</c>.</summary>
    Saoc = 2,

    /// <summary><c>ID_EXT_ELE_AUDIOPREROLL</c>.</summary>
    AudioPreroll = 3,

    /// <summary><c>ID_EXT_ELE_UNI_DRC</c>.</summary>
    UniDrc = 4,
}

/// <summary><c>AACUsacElemConfig</c>: one element's configuration, read once from the ASC.</summary>
public sealed class UsacElemConfig
{
    /// <summary><c>type</c>.</summary>
    public UsacElementType Type;

    /// <summary><c>tw_mdct</c>: time-warped MDCT, which no baseline stream uses.</summary>
    public bool TwMdct;

    /// <summary><c>noise_fill</c>.</summary>
    public bool NoiseFill;

    /// <summary><c>sbr.ratio</c>: zero when the stream has no eSBR.</summary>
    public int SbrRatio;

    /// <summary><c>stereo_config_index</c>: only ever read when <see cref="SbrRatio"/> is set.</summary>
    public int StereoConfigIndex;

    /// <summary><c>ext.type</c>.</summary>
    public UsacExtElementType ExtType;

    /// <summary><c>ext.default_len</c>.</summary>
    public int ExtDefaultLen;

    /// <summary><c>ext.payload_frag</c>.</summary>
    public bool ExtPayloadFrag;

    /// <summary><c>ext.pl_data_offset</c>: how much of a fragmented payload has arrived.</summary>
    public int ExtPlDataOffset;
}

/// <summary><c>AACUSACConfig</c>: the stream's configuration.</summary>
public sealed class UsacConfig
{
    /// <summary><c>core_sbr_frame_len_idx</c>.</summary>
    public int CoreSbrFrameLenIdx;

    /// <summary><c>core_frame_len</c>: 768 or 1024.</summary>
    public int CoreFrameLen;

    /// <summary><c>nb_elems</c>.</summary>
    public int NbElems;

    /// <summary><c>elems[64]</c>.</summary>
    public UsacElemConfig[] Elems = [];

    /// <summary><c>stream_identifier</c>, when the config carries one.</summary>
    public int StreamIdentifier;

    /// <summary>The sample rate the core runs at, after the SBR ratio is divided out.</summary>
    public int CoreSampleRate;

    /// <summary>The rate the decoder outputs at, which is the rate in the configuration.</summary>
    public int OutputSampleRate;

    /// <summary><c>m4ac.sampling_index</c> for the core rate, which selects the band tables.</summary>
    public int SamplingIndex;

    /// <summary><c>m4ac.frame_length_short</c>.</summary>
    public bool FrameLengthShort;

    /// <summary>How many channels the element list adds up to.</summary>
    public int ChannelCount;
}

/// <summary><c>AACUsacElemData</c>: one channel's per-frame state, carried between frames.</summary>
public sealed class UsacElemData
{
    /// <summary><c>core_mode</c>: set for an LPD frame, clear for a frequency-domain one.</summary>
    public bool CoreMode;

    /// <summary><c>scale_factor_grouping</c>.</summary>
    public int ScaleFactorGrouping;

    /// <summary><c>tns_data_present</c>.</summary>
    public bool TnsDataPresent;

    /// <summary><c>noise.level</c>.</summary>
    public int NoiseLevel;

    /// <summary><c>noise.offset</c>.</summary>
    public int NoiseOffset;

    /// <summary><c>noise.seed</c>.</summary>
    public uint NoiseSeed;

    /// <summary><c>ac</c>: the arithmetic coder's context, which spans frames.</summary>
    public readonly AacArithState Arith = new();

    /// <summary>Clears everything, as <c>ff_aac_usac_reset_state</c>'s memset does.</summary>
    public void Reset()
    {
        CoreMode = false;
        ScaleFactorGrouping = 0;
        TnsDataPresent = false;
        NoiseLevel = 0;
        NoiseOffset = 0;
        NoiseSeed = 0;
        Arith.Reset();
    }
}

/// <summary><c>AACUsacStereo</c>: a channel pair's stereo state.</summary>
public sealed class UsacStereo
{
    /// <summary><c>common_window</c>.</summary>
    public bool CommonWindow;

    /// <summary><c>common_tw</c>.</summary>
    public bool CommonTw;

    /// <summary><c>tns_on_lr</c>: apply TNS after the stereo stage rather than before.</summary>
    public bool TnsOnLr;

    /// <summary><c>ms_mask_mode</c>: 0 none, 1 per band, 2 all, 3 complex prediction.</summary>
    public int MsMaskMode;

    /// <summary><c>use_prev_frame</c>.</summary>
    public bool UsePrevFrame;

    /// <summary><c>pred_dir</c>: 0 predicts the side channel, 1 the mid.</summary>
    public bool PredDir;

    /// <summary><c>complex_coef</c>: an imaginary part is transmitted too.</summary>
    public bool ComplexCoef;

    /// <summary><c>pred_used[128]</c>.</summary>
    public readonly byte[] PredUsed = new byte[128];

    /// <summary><c>alpha_q_re[1024]</c>, and the three that go with it.</summary>
    public readonly float[] AlphaQRe = new float[1024];

    /// <summary><c>alpha_q_im[1024]</c>.</summary>
    public readonly float[] AlphaQIm = new float[1024];

    /// <summary><c>prev_alpha_q_re[1024]</c>.</summary>
    public readonly float[] PrevAlphaQRe = new float[1024];

    /// <summary><c>prev_alpha_q_im[1024]</c>.</summary>
    public readonly float[] PrevAlphaQIm = new float[1024];

    /// <summary><c>dmix_re[1024]</c>: this frame's downmix.</summary>
    public readonly float[] DmixRe = new float[1024];

    /// <summary><c>prev_dmix_re[1024]</c>: the previous frame's, recomputed every frame.</summary>
    public readonly float[] PrevDmixRe = new float[1024];

    /// <summary><c>dmix_im[1024]</c>: the estimated imaginary part the prediction uses.</summary>
    public readonly float[] DmixIm = new float[1024];

    /// <summary><c>ms_mask[128]</c>, which lives on the ChannelElement in the C.</summary>
    public readonly byte[] MsMask = new byte[128];

    /// <summary><c>max_sfb_ste</c>: the larger of the pair's two <c>max_sfb</c>.</summary>
    public int MaxSfbSte;

    /// <summary>Clears everything, as <c>ff_aac_usac_reset_state</c>'s memset does.</summary>
    public void Reset()
    {
        CommonWindow = CommonTw = TnsOnLr = false;
        MsMaskMode = 0;
        UsePrevFrame = PredDir = ComplexCoef = false;
        MaxSfbSte = 0;
        Array.Clear(PredUsed);
        Array.Clear(AlphaQRe);
        Array.Clear(AlphaQIm);
        Array.Clear(PrevAlphaQRe);
        Array.Clear(PrevAlphaQIm);
        Array.Clear(DmixRe);
        Array.Clear(PrevDmixRe);
        Array.Clear(DmixIm);
        Array.Clear(MsMask);
    }
}

/// <summary>
/// <c>SingleChannelElement</c>, reduced to the fields the USAC path uses.
/// </summary>
public sealed class UsacChannel
{
    /// <summary><c>coeffs[1024]</c>.</summary>
    public readonly float[] Coeffs = new float[1024];

    /// <summary><c>prev_coeffs[1024]</c>: unscaled, for the next frame's prediction.</summary>
    public readonly float[] PrevCoeffs = new float[1024];

    /// <summary><c>sfo[128]</c>: the scalefactor offsets, before dequantisation.</summary>
    public readonly int[] Sfo = new int[128];

    /// <summary><c>sf[128]</c>: the gains the offsets dequantise to.</summary>
    public readonly float[] Sf = new float[128];

    /// <summary><c>band_type[128]</c>.</summary>
    public readonly int[] BandType = new int[128];

    /// <summary><c>ics</c>.</summary>
    public IcsInfo Ics = new();

    /// <summary><c>ics->num_windows</c>: 1 or 8.</summary>
    /// <remarks>
    /// These three live here rather than on <see cref="IcsInfo"/> because that struct is copied
    /// per channel per frame on the AAC path and three more ints would be paid for there to no
    /// purpose. The C keeps them in the ICS struct; this is the one place the layout diverges.
    /// </remarks>
    public int NumWindows = 1;

    /// <summary><c>ics->tns_max_bands</c>.</summary>
    public int TnsMaxBands;

    /// <summary><c>ics->prev_num_window_groups</c>.</summary>
    public int PrevNumWindowGroups;

    /// <summary><c>ue</c>.</summary>
    public readonly UsacElemData Ue = new();

    /// <summary><c>tns</c>.</summary>
    public TnsData Tns;

    /// <summary><c>tns.present</c>.</summary>
    public bool TnsPresent;

    /// <summary>The overlap-add tail, which is <c>saved</c> in the windowing.</summary>
    public readonly float[] Saved = new float[1024];

    /// <summary>The previous frame's window sequence and shape, for the overlap.</summary>
    public WindowSequence PrevWindowSequence = WindowSequence.OnlyLongSequence;

    /// <summary>The previous frame's window shape.</summary>
    public int PrevWindowShape;
}

/// <summary><c>ChannelElement</c>, reduced to what the USAC path uses.</summary>
public sealed class UsacElement
{
    /// <summary><c>ch[2]</c>.</summary>
    public readonly UsacChannel[] Ch = [new UsacChannel(), new UsacChannel()];

    /// <summary><c>us</c>.</summary>
    public readonly UsacStereo Us = new();
}

/// <summary>
/// The USAC frequency-domain decoder.
/// </summary>
public sealed class AacUsacDecoder
{
    /// <summary><c>SFB_PER_PRED_BAND</c>.</summary>
    private const int SfbPerPredBand = 2;

    /// <summary><c>ID_CONFIG_EXT_*</c>.</summary>
    private const int ConfigExtFill = 0, ConfigExtLoudnessInfo = 2, ConfigExtStreamId = 7;

    private readonly AacFilterbank filterbank = new();

    // Cached once per configuration. AacHuffmanTables.GetSwbOffsets returns a fresh array on
    // every call, so asking it per channel per frame would allocate on the decode path.
    private int[] swbOffsetsLong = [];
    private int[] swbOffsetsShort = [];
    private int numSwbLong;
    private int numSwbShort;
    private int tnsMaxBandsLong;
    private int tnsMaxBandsShort;

    /// <summary>The configuration this decoder was initialised with.</summary>
    public UsacConfig Config { get; private set; } = new();

    /// <summary>One element per entry in the configuration's element list.</summary>
    public UsacElement[] Elements { get; private set; } = [];

    /// <summary>How many channels the last decoded frame produced.</summary>
    public int LastFrameChannels { get; private set; }

    /// <summary>
    /// <c>get_escaped_value</c>: a value in up to three pieces, each escaping to the next when
    /// all its bits are set.
    /// </summary>
    public static uint GetEscapedValue(ref AacBitReader reader, int nb1, int nb2, int nb3)
    {
        uint val = (uint)reader.ReadBits(nb1);
        if (val < (1u << nb1) - 1)
            return val;

        uint val2 = (uint)reader.ReadBits(nb2);
        val += val2;
        if (nb3 != 0 && val2 == (1u << nb2) - 1)
            val += (uint)reader.ReadBits(nb3);

        return val;
    }

    /// <summary><c>decode_usac_element_core</c>.</summary>
    private static void DecodeUsacElementCore(UsacElemConfig e, ref AacBitReader reader,
                                              int sbrRatio)
    {
        e.TwMdct = reader.ReadBit();
        e.NoiseFill = reader.ReadBit();
        e.SbrRatio = sbrRatio;
    }

    /// <summary><c>decode_usac_element_pair</c>.</summary>
    /// <remarks>
    /// Everything this function reads beyond the default is behind <c>sbr.ratio</c>: with no
    /// eSBR there is no <c>stereo_config_index</c> in the bitstream and it stays zero, which is
    /// what keeps every MPS212 path out of reach. The eSBR branch throws rather than guessing,
    /// because reading a config this decoder cannot honour would desynchronise the whole ASC.
    /// </remarks>
    private static void DecodeUsacElementPair(UsacElemConfig e, ref AacBitReader reader)
    {
        e.StereoConfigIndex = 0;
        if (e.SbrRatio == 0)
            return;

        throw new NotSupportedException(
            "USAC with eSBR: coreSbrFrameLengthIndex selected an SBR ratio, so this element "
          + "carries an SbrConfig and possibly an MPS212 config, neither of which this decoder "
          + "reads. Decoding on would read the rest of the configuration at the wrong bit "
          + "offset.");
    }

    /// <summary><c>decode_usac_extension</c>.</summary>
    private static void DecodeUsacExtension(UsacElemConfig e, ref AacBitReader reader)
    {
        int len = 0;

        e.ExtType = (UsacExtElementType)GetEscapedValue(ref reader, 4, 8, 16);
        int extConfigLen = (int)GetEscapedValue(ref reader, 4, 8, 16);

        if (reader.ReadBit())
            len = (int)GetEscapedValue(ref reader, 8, 16, 0) + 1;

        e.ExtDefaultLen = len;
        e.ExtPayloadFrag = reader.ReadBit();

        // The C skips the configuration of every type it does not implement and rewrites the type
        // as FILL, so the frame-level parser has one case to handle. Audio preroll takes the same
        // path because its configuration length should be zero.
        if (e.ExtType != UsacExtElementType.Fill)
        {
            reader.SkipBits(8 * extConfigLen);
            e.ExtType = UsacExtElementType.Fill;
        }
    }

    /// <summary>
    /// <c>ff_aac_usac_config_decode</c>: the UsacConfig, read from the AudioSpecificConfig.
    /// </summary>
    /// <remarks>
    /// The reader must be positioned after the ASC's five-bit object type, four-bit sampling
    /// index and four-bit channel configuration - <c>ff_mpeg4audio_get_config_gb</c> consumes
    /// those thirteen bits and this starts at the fourteenth. The rate and channel count here
    /// supersede them: USAC has its own five-bit rate index covering rates AAC has no code for.
    /// </remarks>
    public void ConfigDecode(ref AacBitReader reader, int channelConfig)
    {
        var usac = new UsacConfig();

        int freqIdx = reader.ReadBits(5);
        int samplerate;
        if (freqIdx == 0x1F)
        {
            samplerate = reader.ReadBits(24);
        }
        else
        {
            samplerate = AacUsacTables.Samplerate[freqIdx];
            if (samplerate < 0)
                throw new InvalidOperationException(
                    $"USAC configuration: usacSamplingFrequencyIndex {freqIdx} is a reserved "
                  + "slot in ff_aac_usac_samplerate and has no rate.");
        }

        usac.CoreSbrFrameLenIdx = reader.ReadBits(3);
        usac.FrameLengthShort = usac.CoreSbrFrameLenIdx == 0 || usac.CoreSbrFrameLenIdx == 2;
        usac.CoreFrameLen = usac.FrameLengthShort ? 768 : 1024;

        int sbrRatio = usac.CoreSbrFrameLenIdx switch
        {
            2 => 2,
            3 => 3,
            4 => 1,
            _ => 0,
        };

        if (sbrRatio != 0)
            throw new NotSupportedException(
                $"USAC with eSBR: coreSbrFrameLengthIndex {usac.CoreSbrFrameLenIdx} gives an SBR "
              + "ratio of " + sbrRatio + ", and the eSBR path is not ported. Both FATE "
              + "conformance streams use index 1, which has no SBR.");

        if (usac.CoreFrameLen != 1024)
            throw new NotSupportedException(
                "USAC with a 768-coefficient core: coreSbrFrameLengthIndex "
              + $"{usac.CoreSbrFrameLenIdx} selects core_frame_len 768, which needs the "
              + "ff_swb_offset_768 and ff_swb_offset_96 band tables. They are not ported, and "
              + "decoding with the 1024 tables would read the wrong bands.");

        usac.OutputSampleRate = samplerate;
        usac.CoreSampleRate = samplerate;
        usac.SamplingIndex = AacSampleRates.ToIndex(usac.CoreSampleRate);
        if (usac.SamplingIndex < 0 || usac.SamplingIndex > 12)
            throw new InvalidOperationException(
                $"USAC configuration: core sample rate {usac.CoreSampleRate} has no AAC sampling "
              + "index, so there are no scalefactor band tables for it.");

        int channelConfigIdx = reader.ReadBits(5);
        int nbChannels;
        if (channelConfigIdx == 0)
        {
            nbChannels = (int)GetEscapedValue(ref reader, 5, 8, 16);
            if (nbChannels > 64)
                throw new InvalidOperationException(
                    $"USAC configuration: numOutChannels {nbChannels} exceeds 64.");

            // bsOutputChannelPos per channel. The positions name an output layout, which this
            // decoder does not reorder, so they are read and dropped - as the count, not the
            // identities, is what the element list has to agree with.
            for (int i = 0; i < nbChannels; i++)
                reader.SkipBits(5);
        }
        else
        {
            nbChannels = ChannelsForConfig(channelConfigIdx);
            if (nbChannels <= 0)
                throw new InvalidOperationException(
                    $"USAC configuration: channelConfigurationIndex {channelConfigIdx} has no "
                  + "defined channel layout.");
        }

        usac.ChannelCount = nbChannels;

        // UsacDecoderConfig
        usac.NbElems = (int)GetEscapedValue(ref reader, 4, 8, 16) + 1;
        if (usac.NbElems > 64)
            throw new InvalidOperationException(
                $"USAC configuration: {usac.NbElems} elements exceeds 64.");

        usac.Elems = new UsacElemConfig[usac.NbElems];
        int mapCount = 0;

        for (int i = 0; i < usac.NbElems; i++)
        {
            var e = new UsacElemConfig();
            usac.Elems[i] = e;

            e.Type = (UsacElementType)reader.ReadBits(2);
            if (e.Type != UsacElementType.Extension && mapCount + 1 > nbChannels)
                throw new InvalidOperationException(
                    "USAC configuration: the element list carries more channels than the "
                  + $"channel configuration's {nbChannels}.");

            switch (e.Type)
            {
                case UsacElementType.SingleChannel:
                    DecodeUsacElementCore(e, ref reader, sbrRatio);
                    mapCount++;
                    break;

                case UsacElementType.ChannelPair:
                    DecodeUsacElementCore(e, ref reader, sbrRatio);
                    DecodeUsacElementPair(e, ref reader);
                    mapCount += 2;
                    break;

                case UsacElementType.Lfe:
                    // An LFE element has no configuration at all.
                    e.TwMdct = false;
                    e.NoiseFill = false;
                    mapCount++;
                    break;

                case UsacElementType.Extension:
                    DecodeUsacExtension(e, ref reader);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"USAC configuration: element type {(int)e.Type} is not one of the four "
                      + "the two-bit field can hold, which cannot happen.");
            }
        }

        if (reader.ReadBit()) // usacConfigExtensionPresent
        {
            int nbExtensions = (int)GetEscapedValue(ref reader, 2, 4, 8) + 1;
            for (int i = 0; i < nbExtensions; i++)
            {
                int type = (int)GetEscapedValue(ref reader, 4, 8, 16);
                int len = (int)GetEscapedValue(ref reader, 4, 8, 16);

                switch (type)
                {
                    case ConfigExtLoudnessInfo:
                        // The loudness set is metadata: it changes no sample. The C parses it to
                        // stay in step with the bitstream, and so does this, but the values are
                        // not kept because nothing downstream reads them.
                        SkipLoudnessSet(ref reader);
                        break;

                    case ConfigExtStreamId:
                        usac.StreamIdentifier = reader.ReadBits(16);
                        break;

                    case ConfigExtFill:
                        // Fill bytes should all be 0xA5. The C counts the ones that are not and
                        // warns; there is nothing to do about them either way.
                        for (int b = 0; b < len; b++)
                            reader.ReadBits(8);
                        break;

                    default:
                        for (int b = 0; b < len; b++)
                            reader.SkipBits(8);
                        break;
                }
            }
        }

        Config = usac;

        swbOffsetsLong = AacHuffmanTables.GetSwbOffsets(usac.SamplingIndex, isShortBlock: false);
        swbOffsetsShort = AacHuffmanTables.GetSwbOffsets(usac.SamplingIndex, isShortBlock: true);
        numSwbLong = AacHuffmanTables.GetNumSwb(usac.SamplingIndex, isShortBlock: false);
        numSwbShort = AacHuffmanTables.GetNumSwb(usac.SamplingIndex, isShortBlock: true);
        tnsMaxBandsLong = AacUsacTables.TnsMaxBands1024[usac.SamplingIndex];
        tnsMaxBandsShort = AacUsacTables.TnsMaxBands128[usac.SamplingIndex];

        ResetState();
    }

    /// <summary><c>ff_mpeg4audio_channels</c>: how many channels a configuration index means.</summary>
    private static int ChannelsForConfig(int index) => index switch
    {
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        5 => 5,
        6 => 6,
        7 => 8,
        11 => 7,
        12 => 8,
        14 => 8,
        _ => -1,
    };

    /// <summary>
    /// <c>decode_loudness_set</c> and <c>decode_loudness_info</c>, parsed only to stay in step.
    /// </summary>
    private static void SkipLoudnessSet(ref AacBitReader reader)
    {
        int nbAlbum = reader.ReadBits(6);
        int nbInfo = reader.ReadBits(6);

        for (int i = 0; i < nbAlbum + nbInfo; i++)
            SkipLoudnessInfo(ref reader);

        if (reader.ReadBit()) // loudnessInfoSetExtensionPresent
        {
            while (true)
            {
                int type = (int)GetEscapedValue(ref reader, 4, 8, 16);
                if (type == 0) // UNIDRCLOUDEXT_TERM
                    break;

                int len = (int)GetEscapedValue(ref reader, 4, 8, 0) + 1;
                reader.SkipBits(8 * len);
            }
        }
    }

    /// <summary><c>decode_loudness_info</c>.</summary>
    private static void SkipLoudnessInfo(ref AacBitReader reader)
    {
        reader.SkipBits(6 + 1); // drc_set_id, downmix_id in part
        reader.SkipBits(7 + 1 + 1 - 1);

        // The C reads drc_set_id(6), downmix_id(7), sample_peak_present(1) and
        // true_peak_present(1) as separate fields; the two skips above cover the first three
        // bits-wise, so read the two presence flags here and the conditional bodies below.
        if (reader.ReadBit()) // sample_peak_level_present
            reader.SkipBits(12);

        if (reader.ReadBit()) // true_peak_level_present
            reader.SkipBits(12 + 4 + 2);

        int measurementCount = reader.ReadBits(4);
        for (int i = 0; i < measurementCount; i++)
            reader.SkipBits(4 + 8 + 4 + 2);
    }

    /// <summary>
    /// <c>ff_aac_usac_reset_state</c>: allocate the elements and clear their carried state.
    /// </summary>
    /// <remarks>
    /// The noise seeds follow the C exactly, oddity included: its loop tests <c>if (!ch)</c>
    /// where <c>ch</c> is the channel count, 1 or 2, so the first branch never runs and the
    /// second assigns to <c>ch[1]</c> on every iteration. The result is that channel 0's seed
    /// stays 0 and channel 1's is 0x10932, for a pair and for a single element alike. It only
    /// matters when noise filling is on, and matching FFmpeg matters more than the value looking
    /// deliberate.
    /// </remarks>
    public void ResetState()
    {
        var elements = new UsacElement[Config.NbElems];

        for (int i = 0; i < Config.NbElems; i++)
        {
            elements[i] = new UsacElement();
            UsacElemConfig e = Config.Elems[i];
            if (e.Type == UsacElementType.Extension)
                continue;

            int ch = e.Type == UsacElementType.ChannelPair ? 2 : 1;

            elements[i].Us.Reset();
            for (int j = 0; j < ch; j++)
            {
                elements[i].Ch[j].Ue.Reset();
                elements[i].Ch[j].Ue.NoiseSeed = 0;
                elements[i].Ch[1].Ue.NoiseSeed = 0x10932;
            }
        }

        Elements = elements;
        filterbank.Configure(useShortFrames: false);
    }

    /// <summary>
    /// <c>decode_usac_scale_factors</c>: the scalefactor offsets, delta-coded from a global gain.
    /// </summary>
    /// <remarks>
    /// USAC has one accumulator, not AAC's three: there are no intensity or noise codebooks here,
    /// because the band type is always <c>ESC_BT</c> and noise is filled in afterwards rather
    /// than coded as a band. The first band takes the global gain unchanged - the delta is read
    /// only from the second onwards - and the stored value is biased down by 100.
    /// </remarks>
    private static void DecodeScaleFactors(UsacChannel sce, ref AacBitReader reader,
                                           int globalGain)
    {
        ref IcsInfo ics = ref sce.Ics;
        int offsetSf = globalGain;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                if (g != 0 || sfb != 0)
                    offsetSf += AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);

                if ((uint)offsetSf > 255u)
                    throw new InvalidOperationException(
                        $"USAC scalefactor {offsetSf} is outside 0..255, so the stream is "
                      + "malformed or the reader has lost its place.");

                sce.Sfo[g * ics.MaxSfb + sfb] = offsetSf - 100;
            }
        }
    }

    /// <summary>
    /// <c>decode_spectrum_ac</c>: one channel's spectrum, arithmetic-coded.
    /// </summary>
    /// <remarks>
    /// Three things here are not obvious and all three are load-bearing.
    ///
    /// <para><b>The arithmetic decoding runs on a copy of the reader.</b> The C takes
    /// <c>gb2 = *gb</c>, decodes through <c>gb2</c>, and then advances the real reader by the
    /// consumed count <b>minus fourteen bits</b>. That is not a fudge: the arithmetic decoder
    /// reads ahead to keep its interval full, and fourteen bits is exactly how far ahead it has
    /// run when it stops. The sign bits that follow are read from the real reader, so being
    /// fourteen bits out puts every sign on the wrong coefficient.</para>
    ///
    /// <para><b>The single-pair case is different.</b> When <paramref name="len"/> is 1 there is
    /// no rewind at all and the copy is adopted wholesale, because with one pair the decoder has
    /// not read ahead by a full fourteen bits.</para>
    ///
    /// <para><b>Dequantisation is folded into the loop</b> as <c>a * cbrt(a)</c>, which is
    /// a<sup>4/3</sup>, applied before the sign. It is not the scalefactor-driven
    /// dequantisation the rest of AAC uses and must not be applied again afterwards.</para>
    ///
    /// <para>The escape loop builds a magnitude in levels: each <c>ARITH_ESCAPE</c> symbol adds
    /// a level and bumps the context band, capped at 7, and the per-level least-significant bits
    /// are then read back most-significant first. An <c>m</c> of zero with a non-zero escape
    /// count is <c>ARITH_STOP</c> and ends the spectrum early - the coefficients above are
    /// zeroed and <see cref="AacArithCoder.Finish"/> records where it stopped.</para>
    /// </remarks>
    /// <param name="coef">The output coefficients; entries from <paramref name="n"/>/2 pairs up are zeroed.</param>
    /// <param name="reader">The frame reader, advanced past the arithmetic data and the signs.</param>
    /// <param name="state">The context, carried across frames.</param>
    /// <param name="reset">Whether to clear the carried context first.</param>
    /// <param name="len">How many coefficients are coded.</param>
    /// <param name="n">The transform length.</param>
    public static void DecodeSpectrumAc(
        Span<float> coef,
        ref AacBitReader reader,
        AacArithState state,
        bool reset,
        int len,
        int n)
    {
        uint c = AacArithCoder.MapProcess(state, reset, n);

        if (len == 0)
        {
            AacArithCoder.Finish(state, 0, n);
            return;
        }

        AacArithCoder.Init(out AacArith ac, ref reader);

        // The reader the arithmetic decoding actually consumes, and the count to measure the
        // rewind against.
        AacBitReader inner = reader;
        int startCount = inner.BitsRead;

        int i = 0;
        int a = 0, b = 0;
        for (; i < len / 2; i++)
        {
            c = AacArithCoder.GetContext(state, c, i, n);

            int lvl = 0, escNb = 0, m;
            while (true)
            {
                uint pki = AacArithCoder.GetPk(c + ((uint)escNb << 17));
                m = AacArithCoder.Decode(ref ac, ref inner, AacArithTables.MsbCdfs[pki]);
                if (m < AacArithCoder.Escape)
                    break;

                lvl++;

                // FFmpeg calls this a cargo-culted value and keeps it; a stream that needs more
                // than 23 levels is malformed, and without the cap the loop does not terminate.
                if (lvl > 23)
                    throw new InvalidOperationException(
                        "USAC arithmetic spectrum: more than 23 escape levels on one coefficient "
                      + "pair, which no well-formed stream produces.");

                escNb = lvl > 7 ? 7 : lvl;
            }

            b = m >> 2;
            a = m - (b << 2);

            // ARITH_STOP: a zero symbol that did NOT come after an escape ends the spectrum.
            if (m == 0)
            {
                if (escNb != 0)
                    break;
                a = b = 0;
            }

            for (int l = lvl; l > 0; l--)
            {
                int lsbIdx = a == 0 ? 1 : (b == 0 ? 0 : 2);
                int r = AacArithCoder.Decode(ref ac, ref inner, AacArithTables.LsbCdfs[lsbIdx]);
                a = (a << 1) | (r & 1);
                b = (b << 1) | ((r >> 1) & 1);
            }

            // Double precision, then narrowed, because the C's cbrt() is the double one and
            // the product is formed in double before it reaches a float. MathF.Cbrt would round
            // the root first and differ in the last bit on some magnitudes.
            coef[2 * i + 0] = (float)(a * Math.Cbrt(a));
            coef[2 * i + 1] = (float)(b * Math.Cbrt(b));
            AacArithCoder.UpdateContext(state, i, a, b);
        }

        if (len > 1)
            reader.SkipBits(inner.BitsRead - startCount - 14);
        else
            reader = inner;

        AacArithCoder.Finish(state, i, n);

        for (; i < n / 2; i++)
        {
            coef[2 * i + 0] = 0;
            coef[2 * i + 1] = 0;
        }

        for (int k = 0; k < len; k++)
        {
            if (coef[k] != 0 && !reader.ReadBit())
                coef[k] = -coef[k];
        }
    }

    /// <summary>
    /// <c>decode_usac_stereo_cplx</c>: the complex stereo prediction coefficients.
    /// </summary>
    /// <remarks>
    /// The coefficients come in pairs of bands - <c>SFB_PER_PRED_BAND</c> is 2 - and one value
    /// covers both, which is why every write to band <c>sfb</c> is copied to <c>sfb + 1</c>.
    /// They are delta-coded either against the previous band in this group (<c>delta_code_time</c>
    /// clear) or against the same band in the previous group or frame (set). The previous-frame
    /// case has a special rule for the first group of an eight-short frame that follows another
    /// eight-short frame: it reaches back to the <em>last</em> group of that frame, which the C
    /// notes the spec does not say but is the only reading that makes sense.
    /// </remarks>
    private static void DecodeUsacStereoCplx(UsacStereo us, UsacElement cpe,
                                             ref AacBitReader reader,
                                             int numWindowGroups, int prevNumWindowGroups,
                                             bool indepFlag)
    {
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        int maxSfbSte = us.MaxSfbSte;

        if (!reader.ReadBit()) // cplx_pred_all
        {
            for (int g = 0; g < numWindowGroups; g++)
            {
                for (int sfb = 0; sfb < maxSfbSte; sfb += SfbPerPredBand)
                {
                    byte val = reader.ReadBit() ? (byte)1 : (byte)0;
                    us.PredUsed[g * maxSfbSte + sfb] = val;
                    if (sfb + 1 < maxSfbSte)
                        us.PredUsed[g * maxSfbSte + sfb + 1] = val;
                }
            }
        }
        else
        {
            for (int g = 0; g < numWindowGroups; g++)
                for (int sfb = 0; sfb < maxSfbSte; sfb++)
                    us.PredUsed[g * maxSfbSte + sfb] = 1;
        }

        us.PredDir = reader.ReadBit();
        us.ComplexCoef = reader.ReadBit();

        us.UsePrevFrame = false;
        if (us.ComplexCoef && !indepFlag)
            us.UsePrevFrame = reader.ReadBit();

        bool deltaCodeTime = false;
        if (!indepFlag)
            deltaCodeTime = reader.ReadBit();

        for (int g = 0; g < numWindowGroups; g++)
        {
            for (int sfb = 0; sfb < maxSfbSte; sfb += SfbPerPredBand)
            {
                float lastAlphaQRe = 0;
                float lastAlphaQIm = 0;

                if (deltaCodeTime)
                {
                    if (g != 0)
                    {
                        // Within this frame, the previous group.
                        lastAlphaQRe = us.AlphaQRe[(g - 1) * maxSfbSte + sfb];
                        lastAlphaQIm = us.AlphaQIm[(g - 1) * maxSfbSte + sfb];
                    }
                    else if (ics.WindowSequence == WindowSequence.EightShortSequence &&
                             cpe.Ch[0].PrevWindowSequence == WindowSequence.EightShortSequence)
                    {
                        int wg = prevNumWindowGroups - 1;
                        lastAlphaQRe = us.PrevAlphaQRe[wg * maxSfbSte + sfb];
                        lastAlphaQIm = us.PrevAlphaQIm[wg * maxSfbSte + sfb];
                    }
                    else
                    {
                        lastAlphaQRe = us.PrevAlphaQRe[g * maxSfbSte + sfb];
                        lastAlphaQIm = us.PrevAlphaQIm[g * maxSfbSte + sfb];
                    }
                }
                else if (sfb != 0)
                {
                    lastAlphaQRe = us.AlphaQRe[g * maxSfbSte + sfb - 1];
                    lastAlphaQIm = us.AlphaQIm[g * maxSfbSte + sfb - 1];
                }

                if (us.PredUsed[g * maxSfbSte + sfb] != 0)
                {
                    // The C writes -get_vlc2(...) + 60, and our helper already returns the raw
                    // index minus SCALE_DIFF_ZERO (60), so the whole expression is its negation.
                    int val = -AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                    lastAlphaQRe += val * 0.1f;
                    if (us.ComplexCoef)
                    {
                        val = -AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                        lastAlphaQIm += val * 0.1f;
                    }

                    us.AlphaQRe[g * maxSfbSte + sfb] = lastAlphaQRe;
                    us.AlphaQIm[g * maxSfbSte + sfb] = lastAlphaQIm;
                }
                else
                {
                    us.AlphaQRe[g * maxSfbSte + sfb] = 0;
                    us.AlphaQIm[g * maxSfbSte + sfb] = 0;
                }

                if (sfb + 1 < maxSfbSte)
                {
                    us.AlphaQRe[g * maxSfbSte + sfb + 1] = us.AlphaQRe[g * maxSfbSte + sfb];
                    us.AlphaQIm[g * maxSfbSte + sfb + 1] = us.AlphaQIm[g * maxSfbSte + sfb];
                }
            }
        }
    }

    /// <summary>
    /// <c>setup_sce</c>: the band tables and window grouping for a channel's window sequence.
    /// </summary>
    /// <remarks>
    /// The grouping loop is <b>not</b> AAC's. Each group length starts at 1 and the mask bit
    /// extends the group currently being built rather than starting a new one, with the
    /// seven-bit mask read most-significant-bit first as <c>1 &lt;&lt; (6 - j)</c>. The eighth
    /// window always closes the last group.
    /// </remarks>
    private void SetupSce(UsacChannel sce)
    {
        ref IcsInfo ics = ref sce.Ics;
        UsacElemData ue = sce.Ue;

        sce.PrevNumWindowGroups = Math.Max(ics.NumWindowGroups, 1);

        if (ics.WindowSequence == WindowSequence.EightShortSequence)
        {
            ics.SwbOffset = swbOffsetsShort;
            ics.NumSwb = numSwbShort;
            sce.TnsMaxBands = tnsMaxBandsShort;

            ics.NumWindowGroups = 0;
            for (int j = 0; j < 7; j++)
            {
                ics.WindowGroupLength[j] = 1;
                if ((ue.ScaleFactorGrouping & (1 << (6 - j))) != 0)
                    ics.WindowGroupLength[ics.NumWindowGroups] += 1;
                else
                    ics.NumWindowGroups++;
            }

            ics.WindowGroupLength[7] = 1;
            ics.NumWindowGroups++;
            sce.NumWindows = 8;
        }
        else
        {
            ics.SwbOffset = swbOffsetsLong;
            ics.NumSwb = numSwbLong;
            sce.TnsMaxBands = tnsMaxBandsLong;

            ics.WindowGroupLength[0] = 1;
            ics.NumWindowGroups = 1;
            sce.NumWindows = 1;
        }

        if (ics.MaxSfb > ics.NumSwb)
        {
            ics.MaxSfb = 0;
            throw new InvalidOperationException(
                $"USAC: max_sfb exceeds the {ics.NumSwb} scalefactor bands this window has.");
        }

        // USAC has no section data and no codebooks: every band is ESC_BT, which is what makes
        // the mid/side test (band_type < NOISE_BT) and the dequantiser's default case apply.
        Array.Fill(sce.BandType, AacCodebooks.EscapeHcb);
    }

    /// <summary>
    /// <c>decode_usac_stereo_info</c>: a pair's shared window, mid/side mask and TNS routing.
    /// </summary>
    private void DecodeUsacStereoInfo(UsacElemConfig ec, UsacElement cpe,
                                      ref AacBitReader reader, bool indepFlag)
    {
        UsacStereo us = cpe.Us;
        UsacChannel sce1 = cpe.Ch[0], sce2 = cpe.Ch[1];
        ref IcsInfo ics1 = ref sce1.Ics;
        ref IcsInfo ics2 = ref sce2.Ics;

        us.CommonWindow = false;
        us.CommonTw = false;

        // This frame's alphas must start at zero even when nothing writes them, because they are
        // copied into the previous-frame set at the end of the frame and read back next time.
        Array.Clear(us.AlphaQRe);
        Array.Clear(us.AlphaQIm);

        if (sce1.Ue.CoreMode || sce2.Ue.CoreMode)
            return;

        bool tnsActive = reader.ReadBit();
        us.CommonWindow = reader.ReadBit();

        if (!us.CommonWindow || indepFlag)
        {
            Array.Clear(us.PrevAlphaQRe);
            Array.Clear(us.PrevAlphaQIm);
        }

        if (us.CommonWindow)
        {
            // ics_info(), shared by both channels.
            sce1.PrevWindowSequence = ics1.WindowSequence;
            sce2.PrevWindowSequence = ics2.WindowSequence;
            var sequence = (WindowSequence)reader.ReadBits(2);
            ics1.WindowSequence = ics2.WindowSequence = sequence;

            sce1.PrevWindowShape = ics1.WindowShape;
            sce2.PrevWindowShape = ics2.WindowShape;
            int shape = reader.ReadBit() ? 1 : 0;
            ics1.WindowShape = ics2.WindowShape = shape;
            ics1.PreviousWindowShape = sce1.PrevWindowShape;
            ics2.PreviousWindowShape = sce2.PrevWindowShape;

            // A change into or out of eight-short invalidates the previous frame's prediction
            // coefficients: they are indexed by group, and the grouping has just changed shape.
            bool wasShort1 = sce1.PrevWindowSequence == WindowSequence.EightShortSequence;
            bool isShort1 = ics1.WindowSequence == WindowSequence.EightShortSequence;
            bool wasShort2 = sce2.PrevWindowSequence == WindowSequence.EightShortSequence;
            bool isShort2 = ics2.WindowSequence == WindowSequence.EightShortSequence;
            if (isShort1 != wasShort1 || isShort2 != wasShort2)
            {
                Array.Clear(us.PrevAlphaQRe);
                Array.Clear(us.PrevAlphaQIm);
            }

            if (ics1.WindowSequence == WindowSequence.EightShortSequence)
            {
                ics1.MaxSfb = ics2.MaxSfb = reader.ReadBits(4);
                sce1.Ue.ScaleFactorGrouping = sce2.Ue.ScaleFactorGrouping = reader.ReadBits(7);
            }
            else
            {
                ics1.MaxSfb = ics2.MaxSfb = reader.ReadBits(6);
            }

            if (!reader.ReadBit()) // common_max_sfb
            {
                ics2.MaxSfb = ics2.WindowSequence == WindowSequence.EightShortSequence
                    ? reader.ReadBits(4)
                    : reader.ReadBits(6);
            }

            SetupSce(sce1);
            SetupSce(sce2);

            us.MaxSfbSte = Math.Max(ics1.MaxSfb, ics2.MaxSfb);

            us.MsMaskMode = reader.ReadBits(2);
            Array.Clear(us.MsMask);

            if (us.MsMaskMode == 1)
            {
                for (int g = 0; g < ics1.NumWindowGroups; g++)
                    for (int sfb = 0; sfb < us.MaxSfbSte; sfb++)
                        us.MsMask[g * us.MaxSfbSte + sfb] = reader.ReadBit() ? (byte)1 : (byte)0;
            }
            else if (us.MsMaskMode == 2)
            {
                Array.Fill(us.MsMask, (byte)0xFF);
            }
            else if (us.MsMaskMode == 3 && ec.StereoConfigIndex == 0)
            {
                DecodeUsacStereoCplx(us, cpe, ref reader,
                                     ics1.NumWindowGroups, sce1.PrevNumWindowGroups, indepFlag);
            }
        }

        if (ec.TwMdct)
            throw new NotSupportedException(
                "USAC time warping is signalled in this element, which no baseline profile "
              + "stream uses and which FFmpeg itself refuses.");

        us.TnsOnLr = false;
        sce1.Ue.TnsDataPresent = sce2.Ue.TnsDataPresent = false;

        if (tnsActive)
        {
            bool commonTns = false;
            if (us.CommonWindow)
                commonTns = reader.ReadBit();

            us.TnsOnLr = reader.ReadBit();

            if (commonTns)
            {
                AacTnsDecoder.DecodeTnsData(ref reader, in ics1, out TnsData tns);
                sce1.Tns = tns;
                sce2.Tns = tns;
                sce1.TnsPresent = true;
                sce2.TnsPresent = true;
                sce1.Ue.TnsDataPresent = false;
                sce2.Ue.TnsDataPresent = false;
            }
            else if (reader.ReadBit())
            {
                sce1.Ue.TnsDataPresent = true;
                sce2.Ue.TnsDataPresent = true;
            }
            else
            {
                sce2.Ue.TnsDataPresent = reader.ReadBit();
                sce1.Ue.TnsDataPresent = !sce2.Ue.TnsDataPresent;
            }
        }
    }

    /// <summary><c>noise_random_sign</c>.</summary>
    private static float NoiseRandomSign(ref uint seed)
    {
        seed = (seed * 69069) + 5;
        return (seed & 0x10000) > 0 ? -1.0f : 1.0f;
    }

    /// <summary>
    /// <c>apply_noise_fill</c>: fill quantised-to-zero coefficients with signed noise.
    /// </summary>
    /// <remarks>
    /// A band that came out <em>entirely</em> zero also gets its scalefactor offset pushed down
    /// by the transmitted noise offset, floored at -200. That is why the scalefactor gain table
    /// here has to cover offsets AAC's clamped range never reaches.
    /// </remarks>
    private void ApplyNoiseFill(UsacChannel sce, UsacElemData ue)
    {
        ref IcsInfo ics = ref sce.Ics;

        float noiseVal = MathF.Pow(2, (ue.NoiseLevel - 14.0f) / 3.0f);
        int noiseOffset = ue.NoiseOffset - 16;

        int shortRow = Config.FrameLengthShort ? 1 : 0;
        int shortCol = ics.WindowSequence == WindowSequence.EightShortSequence ? 1 : 0;
        int bandOff = AacUsacTables.NoiseFillStartOffset[shortRow * 2 + shortCol];

        float[] coeffs = sce.Coeffs;
        int coefBase = 0;
        uint seed = ue.NoiseSeed;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            int gLen = ics.WindowGroupLength[g];

            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int off = ics.SwbOffset[sfb];
                int cbLen = ics.SwbOffset[sfb + 1] - off;
                bool bandQuantizedToZero = true;

                if (off < bandOff)
                    continue;

                int cb = coefBase + off;
                for (int group = 0; group < gLen; group++, cb += 128)
                {
                    for (int z = 0; z < cbLen; z++)
                    {
                        if (coeffs[cb + z] == 0)
                            coeffs[cb + z] = NoiseRandomSign(ref seed) * noiseVal;
                        else
                            bandQuantizedToZero = false;
                    }
                }

                if (bandQuantizedToZero)
                {
                    int idx = g * ics.MaxSfb + sfb;
                    sce.Sfo[idx] = Math.Max(sce.Sfo[idx] + noiseOffset, -200);
                }
            }

            coefBase += gLen << 7;
        }

        ue.NoiseSeed = seed;
    }

    /// <summary><c>dequant_scalefactors</c>, for USAC's one band type.</summary>
    private static void DequantScalefactors(UsacChannel sce)
    {
        ref IcsInfo ics = ref sce.Ics;
        int idx = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++, idx++)
            {
                // Every USAC band is ESC_BT, so only the C's default case can be reached. The
                // sign is the C's: the gain is negative and the spectrum is negated with it.
                int tableIndex = sce.Sfo[idx] + AacUsacTables.Pow2SfZero;
                if ((uint)tableIndex >= (uint)AacUsacTables.Pow2SfTab.Length)
                    throw new InvalidOperationException(
                        $"USAC scalefactor offset {sce.Sfo[idx]} falls outside ff_aac_pow2sf_tab, "
                      + "which covers -200 to 227.");

                sce.Sf[idx] = -AacUsacTables.Pow2SfTab[tableIndex];
            }
        }
    }

    /// <summary><c>spectrum_scale</c>: noise filling, dequantisation and the scalefactors.</summary>
    private void SpectrumScale(UsacChannel sce, UsacElemData ue)
    {
        ref IcsInfo ics = ref sce.Ics;

        if (ue.NoiseLevel != 0)
            ApplyNoiseFill(sce, ue);

        // After noise filling, because it may have moved a band's offset.
        DequantScalefactors(sce);

        float[] coeffs = sce.Coeffs;
        int coefBase = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            int gLen = ics.WindowGroupLength[g];

            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int off = ics.SwbOffset[sfb];
                int cbLen = ics.SwbOffset[sfb + 1] - off;
                float sf = sce.Sf[g * ics.MaxSfb + sfb];

                int cb = coefBase + off;
                for (int group = 0; group < gLen; group++, cb += 128)
                    for (int z = 0; z < cbLen; z++)
                        coeffs[cb + z] *= sf;
            }

            coefBase += gLen << 7;
        }
    }

    /// <summary><c>complex_stereo_downmix_prev</c>: the previous frame's downmix.</summary>
    /// <remarks>
    /// Unconditional, unlike the current frame's: every band is downmixed whether or not
    /// prediction uses it, because the previous frame's coefficients are what they are.
    /// </remarks>
    private static void ComplexStereoDownmixPrev(UsacElement cpe, float[] dmixRe)
    {
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        int sign = !cpe.Us.PredDir ? 1 : -1;
        float[] coef1 = cpe.Ch[0].PrevCoeffs;
        float[] coef2 = cpe.Ch[1].PrevCoeffs;
        int baseOff = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            int gLen = ics.WindowGroupLength[g];

            for (int sfb = 0; sfb < cpe.Us.MaxSfbSte; sfb++)
            {
                int off = ics.SwbOffset[sfb];
                int cbLen = ics.SwbOffset[sfb + 1] - off;

                int p = baseOff + off;
                for (int group = 0; group < gLen; group++, p += 128)
                    for (int z = 0; z < cbLen; z++)
                        dmixRe[p + z] = 0.5f * (coef1[p + z] + sign * coef2[p + z]);
            }

            baseOff += gLen << 7;
        }
    }

    /// <summary><c>complex_stereo_downmix_cur</c>: this frame's downmix.</summary>
    private static void ComplexStereoDownmixCur(UsacElement cpe, float[] dmixRe)
    {
        UsacStereo us = cpe.Us;
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        int sign = !us.PredDir ? 1 : -1;
        float[] coef1 = cpe.Ch[0].Coeffs;
        float[] coef2 = cpe.Ch[1].Coeffs;
        int baseOff = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            int gLen = ics.WindowGroupLength[g];

            for (int sfb = 0; sfb < us.MaxSfbSte; sfb++)
            {
                int off = ics.SwbOffset[sfb];
                int cbLen = ics.SwbOffset[sfb + 1] - off;
                bool used = us.PredUsed[g * us.MaxSfbSte + sfb] != 0;

                int p = baseOff + off;
                for (int group = 0; group < gLen; group++, p += 128)
                {
                    for (int z = 0; z < cbLen; z++)
                        dmixRe[p + z] = used
                            ? 0.5f * (coef1[p + z] + sign * coef2[p + z])
                            : coef1[p + z];
                }
            }

            baseOff += gLen << 7;
        }
    }

    /// <summary>
    /// <c>complex_stereo_interpolate_imag</c>: estimate an imaginary spectrum from a real one.
    /// </summary>
    /// <remarks>
    /// A seven-tap antisymmetric filter, accumulated into <paramref name="im"/> rather than
    /// assigned, so two calls can sum this frame's and the previous frame's contributions. The
    /// first three and last three outputs are written out in full because the filter would read
    /// past the ends: the edges <b>mirror</b> the available samples rather than taking zeros,
    /// and the mirroring is not symmetric between the two ends - at the start index 0 is used
    /// twice, at the end the reflection folds back inside. Those six hand-written cases are the
    /// whole reason this is not a loop.
    /// </remarks>
    private static void ComplexStereoInterpolateImag(Span<float> im, ReadOnlySpan<float> re,
                                                     ReadOnlySpan<float> f, int len,
                                                     int factorEven, int factorOdd)
    {
        float s;

        s = f[6] * re[2] + f[5] * re[1] + f[4] * re[0] +
            f[3] * re[0] +
            f[2] * re[1] + f[1] * re[2] + f[0] * re[3];
        im[0] += s * factorEven;

        s = f[6] * re[1] + f[5] * re[0] + f[4] * re[0] +
            f[3] * re[1] +
            f[2] * re[2] + f[1] * re[3] + f[0] * re[4];
        im[1] += s * factorOdd;

        s = f[6] * re[0] + f[5] * re[0] + f[4] * re[1] +
            f[3] * re[2] +
            f[2] * re[3] + f[1] * re[4] + f[0] * re[5];
        im[2] += s * factorEven;

        int i;
        for (i = 3; i < len - 4; i += 2)
        {
            s = f[6] * re[i - 3] + f[5] * re[i - 2] + f[4] * re[i - 1] +
                f[3] * re[i] +
                f[2] * re[i + 1] + f[1] * re[i + 2] + f[0] * re[i + 3];
            im[i + 0] += s * factorOdd;

            s = f[6] * re[i - 2] + f[5] * re[i - 1] + f[4] * re[i] +
                f[3] * re[i + 1] +
                f[2] * re[i + 2] + f[1] * re[i + 3] + f[0] * re[i + 4];
            im[i + 1] += s * factorEven;
        }

        i = len - 3;
        s = f[6] * re[i - 3] + f[5] * re[i - 2] + f[4] * re[i - 1] +
            f[3] * re[i] +
            f[2] * re[i + 1] + f[1] * re[i + 2] + f[0] * re[i + 2];
        im[i] += s * factorOdd;

        i = len - 2;
        s = f[6] * re[i - 3] + f[5] * re[i - 2] + f[4] * re[i - 1] +
            f[3] * re[i] +
            f[2] * re[i + 1] + f[1] * re[i + 1] + f[0] * re[i];
        im[i] += s * factorEven;

        i = len - 1;
        s = f[6] * re[i - 3] + f[5] * re[i - 2] + f[4] * re[i - 1] +
            f[3] * re[i] +
            f[2] * re[i] + f[1] * re[i - 1] + f[0] * re[i - 2];
        im[i] += s * factorOdd;
    }

    /// <summary><c>apply_complex_stereo</c>.</summary>
    private static void ApplyComplexStereo(UsacElement cpe)
    {
        UsacStereo us = cpe.Us;
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        float[] coef1 = cpe.Ch[0].Coeffs;
        float[] coef2 = cpe.Ch[1].Coeffs;
        float[] dmixIm = us.DmixIm;
        int baseOff = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            int gLen = ics.WindowGroupLength[g];

            for (int sfb = 0; sfb < us.MaxSfbSte; sfb++)
            {
                int off = ics.SwbOffset[sfb];
                int cbLen = ics.SwbOffset[sfb + 1] - off;
                float alphaRe = us.AlphaQRe[g * us.MaxSfbSte + sfb];
                float alphaIm = us.AlphaQIm[g * us.MaxSfbSte + sfb];

                if (us.PredUsed[g * us.MaxSfbSte + sfb] == 0)
                    continue;

                int p = baseOff + off;
                if (!us.PredDir)
                {
                    for (int group = 0; group < gLen; group++, p += 128)
                    {
                        for (int z = 0; z < cbLen; z++)
                        {
                            float side = coef2[p + z] - alphaRe * coef1[p + z]
                                       - alphaIm * dmixIm[p + z];
                            coef2[p + z] = coef1[p + z] - side;
                            coef1[p + z] = coef1[p + z] + side;
                        }
                    }
                }
                else
                {
                    for (int group = 0; group < gLen; group++, p += 128)
                    {
                        for (int z = 0; z < cbLen; z++)
                        {
                            float mid = coef2[p + z] - alphaRe * coef1[p + z]
                                      - alphaIm * dmixIm[p + z];
                            coef2[p + z] = mid - coef1[p + z];
                            coef1[p + z] = mid + coef1[p + z];
                        }
                    }
                }
            }

            baseOff += gLen << 7;
        }
    }

    /// <summary>
    /// <c>complex_stereo_get_filter</c>: which MDST estimation filter this window calls for.
    /// </summary>
    private static ReadOnlySpan<float> ComplexStereoGetFilter(UsacElement cpe, bool isPrev)
    {
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        int win, shape;

        if (!isPrev)
        {
            win = ics.WindowSequence switch
            {
                WindowSequence.LongStartSequence => 1,
                WindowSequence.LongStopSequence => 2,
                _ => 0,
            };

            // The four shapes are this window's shape paired with the previous one's; the C's
            // final else repeats case 3, so an impossible pairing lands there too.
            shape = (ics.WindowShape, ics.PreviousWindowShape) switch
            {
                (0, 0) => 0,
                (1, 1) => 1,
                (0, 1) => 2,
                _ => 3,
            };
        }
        else
        {
            win = ics.WindowSequence == WindowSequence.LongStopSequence ? 1 : 0;
            shape = ics.PreviousWindowShape;
        }

        return AacUsacTables.MdstFilter(win, shape);
    }

    /// <summary><c>spectrum_decode</c>: scaling, stereo, TNS and the inverse transform.</summary>
    private void SpectrumDecode(UsacElement cpe, int nbChannels, float[][] output)
    {
        UsacStereo us = cpe.Us;

        for (int ch = 0; ch < nbChannels; ch++)
            SpectrumScale(cpe.Ch[ch], cpe.Ch[ch].Ue);

        if (nbChannels > 1 && us.CommonWindow)
        {
            // TNS before the stereo stage unless the stream says to apply it on left/right.
            for (int ch = 0; ch < nbChannels; ch++)
            {
                UsacChannel sce = cpe.Ch[ch];
                if (sce.TnsPresent && !us.TnsOnLr)
                    ApplyTns(sce);
            }

            if (us.MsMaskMode == 3)
            {
                ComplexStereoDownmixCur(cpe, us.DmixRe);
                ComplexStereoDownmixPrev(cpe, us.PrevDmixRe);

                Array.Clear(us.DmixIm);

                ReadOnlySpan<float> filt = ComplexStereoGetFilter(cpe, false);
                ComplexStereoInterpolateImag(us.DmixIm, us.DmixRe, filt,
                                             Config.CoreFrameLen, 1, 1);

                if (us.UsePrevFrame)
                {
                    filt = ComplexStereoGetFilter(cpe, true);
                    ComplexStereoInterpolateImag(us.DmixIm, us.PrevDmixRe, filt,
                                                 Config.CoreFrameLen, -1, 1);
                }

                ApplyComplexStereo(cpe);
            }
            else if (us.MsMaskMode > 0)
            {
                ApplyMidSideStereo(cpe);
            }
        }

        // The unscaled spectrum is what the next frame predicts from, so it is saved after the
        // stereo stage and before TNS on left/right.
        if (nbChannels > 1)
        {
            for (int ch = 0; ch < nbChannels; ch++)
                Array.Copy(cpe.Ch[ch].Coeffs, cpe.Ch[ch].PrevCoeffs, 1024);

            Array.Copy(us.AlphaQRe, us.PrevAlphaQRe, us.AlphaQRe.Length);
            Array.Copy(us.AlphaQIm, us.PrevAlphaQIm, us.AlphaQIm.Length);
        }

        for (int ch = 0; ch < nbChannels; ch++)
        {
            UsacChannel sce = cpe.Ch[ch];

            if (sce.TnsPresent && (nbChannels == 1 || us.TnsOnLr))
                ApplyTns(sce);

            ImdctAndWindowing(sce, output[ch]);
        }
    }

    /// <summary><c>apply_mid_side_stereo</c>.</summary>
    private static void ApplyMidSideStereo(UsacElement cpe)
    {
        UsacStereo us = cpe.Us;
        ref IcsInfo ics = ref cpe.Ch[0].Ics;
        float[] ch0 = cpe.Ch[0].Coeffs;
        float[] ch1 = cpe.Ch[1].Coeffs;
        int[] offsets = ics.SwbOffset;
        int baseOff = 0;

        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < us.MaxSfbSte; sfb++)
            {
                int idx = g * us.MaxSfbSte + sfb;

                // Every USAC band type is ESC_BT, which is below NOISE_BT, so the C's two band
                // type tests are always true here and only the mask decides.
                if (us.MsMask[idx] == 0)
                    continue;

                for (int group = 0; group < ics.WindowGroupLength[g]; group++)
                {
                    int p = baseOff + group * 128 + offsets[sfb];
                    for (int z = 0; z < offsets[sfb + 1] - offsets[sfb]; z++)
                    {
                        float a = ch0[p + z], b = ch1[p + z];
                        ch0[p + z] = a + b;
                        ch1[p + z] = a - b;
                    }
                }
            }

            baseOff += ics.WindowGroupLength[g] * 128;
        }
    }

    private void ApplyTns(UsacChannel sce)
    {
        AacTnsDecoder.ApplyTnsFilter(sce.Coeffs, in sce.Tns, in sce.Ics, sce.Ics.SwbOffset,
                                     sce.Ics.NumSwb, sce.TnsMaxBands, decode: true);
    }

    private readonly float[] imdctScratch = new float[1024];

    private void ImdctAndWindowing(UsacChannel sce, float[] output)
    {
        filterbank.ImdctAndWindowing(
            sce.Coeffs,
            output,
            sce.Saved,
            sce.Ics.WindowSequence,
            sce.PrevWindowSequence,
            sce.Ics.WindowShape,
            sce.PrevWindowShape,
            imdctScratch);
    }

    /// <summary>
    /// <c>decode_usac_core_coder</c>: one element's frame.
    /// </summary>
    private void DecodeUsacCoreCoder(UsacElemConfig ec, UsacElement che,
                                     ref AacBitReader reader, bool indepFlag, int nbChannels,
                                     float[][] output)
    {
        UsacStereo us = che.Us;
        int coreNbChannels = nbChannels;

        us.CommonWindow = false;

        for (int ch = 0; ch < coreNbChannels; ch++)
        {
            UsacChannel sce = che.Ch[ch];
            sce.TnsPresent = false;
            sce.Ue.TnsDataPresent = false;
            sce.Ue.CoreMode = reader.ReadBit();
        }

        if (nbChannels > 1 && ec.StereoConfigIndex == 1)
            coreNbChannels = 1;

        if (coreNbChannels == 2)
            DecodeUsacStereoInfo(ec, che, ref reader, indepFlag);

        for (int ch = 0; ch < coreNbChannels; ch++)
        {
            UsacChannel sce = che.Ch[ch];
            ref IcsInfo ics = ref sce.Ics;
            UsacElemData ue = sce.Ue;

            if (ue.CoreMode)
                throw new NotSupportedException(
                    "USAC LPD (linear prediction domain) frame. FFmpeg's own LPD parses the "
                  + "frame and synthesises nothing, so no decoder that follows it can match a "
                  + "conformance reference; this one refuses rather than emit silence.");

            if (coreNbChannels == 1 || che.Ch[0].Ue.CoreMode != che.Ch[1].Ue.CoreMode)
                ue.TnsDataPresent = reader.ReadBit();

            // fd_channel_stream
            int globalGain = reader.ReadBits(8);

            ue.NoiseLevel = 0;
            if (ec.NoiseFill)
            {
                ue.NoiseLevel = reader.ReadBits(3);
                ue.NoiseOffset = reader.ReadBits(5);
            }

            if (!us.CommonWindow)
            {
                // ics_info(), this channel's own.
                sce.PrevWindowSequence = ics.WindowSequence;
                ics.WindowSequence = (WindowSequence)reader.ReadBits(2);
                sce.PrevWindowShape = ics.WindowShape;
                ics.WindowShape = reader.ReadBit() ? 1 : 0;
                ics.PreviousWindowShape = sce.PrevWindowShape;

                if (ics.WindowSequence == WindowSequence.EightShortSequence)
                {
                    ics.MaxSfb = reader.ReadBits(4);
                    ue.ScaleFactorGrouping = reader.ReadBits(7);
                }
                else
                {
                    ics.MaxSfb = reader.ReadBits(6);
                }

                SetupSce(sce);
            }

            if (ec.TwMdct && !us.CommonTw)
            {
                if (reader.ReadBit())
                    throw new NotSupportedException(
                        "USAC time warping data is present, which no baseline profile stream "
                      + "uses and which FFmpeg itself refuses.");
            }

            DecodeScaleFactors(sce, ref reader, globalGain);

            if (ue.TnsDataPresent)
            {
                sce.TnsPresent = true;
                AacTnsDecoder.DecodeTnsData(ref reader, in ics, out TnsData tns);
                sce.Tns = tns;
            }

            // ac_spectral_data
            bool arithResetFlag = indepFlag;
            if (!arithResetFlag)
                arithResetFlag = reader.ReadBit();

            Array.Clear(sce.Coeffs);
            for (int win = 0; win < sce.NumWindows; win++)
            {
                int lg = ics.SwbOffset[ics.MaxSfb];
                int n = ics.WindowSequence == WindowSequence.EightShortSequence
                    ? Config.CoreFrameLen / 8
                    : Config.CoreFrameLen;

                DecodeSpectrumAc(sce.Coeffs.AsSpan(win * 128), ref reader, ue.Arith,
                                 arithResetFlag && win == 0, lg, n);
            }

            if (reader.ReadBit()) // fac_data_present
                throw new NotSupportedException(
                    "USAC forward aliasing cancellation data is present on a frequency-domain "
                  + "frame, which only occurs at a transition to or from an LPD frame.");
        }

        if (ec.SbrRatio != 0)
            throw new NotSupportedException("USAC eSBR data is present and eSBR is not ported.");

        if (ec.StereoConfigIndex != 0)
            throw new NotSupportedException("USAC MPS212 data is present and MPS212 is not ported.");

        SpectrumDecode(che, coreNbChannels, output);
    }

    /// <summary>
    /// <c>parse_ext_ele</c>: an extension element's payload.
    /// </summary>
    /// <remarks>
    /// Only fill payloads reach this decoder: <c>decode_usac_extension</c> rewrites every type it
    /// does not implement as fill, so the payload is read to move the reader past it and then
    /// dropped. The fragmentation flags are still honoured, because an unfragmented payload
    /// carries its own start and stop bits and skipping them would misread the length.
    /// </remarks>
    private static void ParseExtEle(UsacElemConfig e, ref AacBitReader reader)
    {
        if (!reader.ReadBit()) // usacExtElementPresent
            return;

        int len;
        if (reader.ReadBit()) // usacExtElementUseDefaultLength
        {
            len = e.ExtDefaultLen;
        }
        else
        {
            len = reader.ReadBits(8);
            if (len == 255)
                len += reader.ReadBits(16) - 2;
        }

        if (len == 0)
            return;

        bool plFragStart = true, plFragEnd = true;
        if (e.ExtPayloadFrag)
        {
            plFragStart = reader.ReadBit();
            plFragEnd = reader.ReadBit();
        }

        if (plFragStart)
            e.ExtPlDataOffset = 0;

        reader.SkipBits(8 * len);
        e.ExtPlDataOffset += len;

        if (plFragEnd)
        {
            // A fill element's payload has nothing usable in it; the C reads it into a buffer and
            // then skips to the end of it, which is where the reader already is.
            e.ExtPlDataOffset = 0;
        }
    }

    /// <summary>
    /// <c>ff_aac_usac_decode_frame</c>: one access unit.
    /// </summary>
    /// <returns>The number of samples written to each channel buffer.</returns>
    public int DecodeFrame(ref AacBitReader reader, float[][] channelBuffers)
    {
        int audioFound = 0;
        int channelsOut = 0;

        bool indepFlag = reader.ReadBit();

        for (int i = 0; i < Config.NbElems; i++)
        {
            UsacElemConfig e = Config.Elems[i];
            UsacElement che = Elements[i];

            switch (e.Type)
            {
                case UsacElementType.SingleChannel:
                case UsacElementType.Lfe:
                    DecodeUsacCoreCoder(e, che, ref reader, indepFlag, 1,
                                        Slice(channelBuffers, channelsOut, 1));
                    channelsOut += 1;
                    audioFound = 1;
                    break;

                case UsacElementType.ChannelPair:
                    DecodeUsacCoreCoder(e, che, ref reader, indepFlag, 2,
                                        Slice(channelBuffers, channelsOut, 2));
                    channelsOut += 2;
                    audioFound = 1;
                    break;

                case UsacElementType.Extension:
                    ParseExtEle(e, ref reader);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"USAC frame: element type {(int)e.Type} is not one of the four the "
                      + "configuration can hold.");
            }
        }

        LastFrameChannels = channelsOut;
        return audioFound != 0 ? (Config.FrameLengthShort ? 768 : 1024) : 0;
    }

    private readonly float[][] elementOutput = new float[2][];

    private float[][] Slice(float[][] channelBuffers, int first, int count)
    {
        if (first + count > channelBuffers.Length)
            throw new InvalidOperationException(
                $"USAC frame: the element list needs {first + count} output channels and only "
              + $"{channelBuffers.Length} were provided.");

        for (int i = 0; i < count; i++)
            elementOutput[i] = channelBuffers[first + i];

        return elementOutput;
    }
}

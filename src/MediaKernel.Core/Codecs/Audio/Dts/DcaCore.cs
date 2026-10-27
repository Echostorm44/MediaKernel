// Port of ffmpeg-master/libavcodec/dca_core.c - the DTS Coherent Acoustics core substream.
//
// SCOPE: the core substream and all four of its extensions - XCH, XXCH, X96 and XBR - plus
// the auxiliary data's dynamic downmix. The extension substream PAYLOADS (XLL, LBR) are not
// here; their entries in the port checklist are still todo. Gating is against FFmpeg's own
// committed references in fate-suite/dts/dcadec-suite, one sample per feature, and against
// "ffmpeg -core_only 1" for the core alone.
//
// The layout comes first and is FFmpeg's: subband samples live in one flat int[] with a base
// offset per (channel, band), because its int32_t **subband_samples is a table of pointers into
// one allocation. Keeping that means the four samples of ADPCM history sit immediately before
// each band's data, which is what lets the predictor read ptr[j - 4] with no special case at the
// start of a frame, and it means the history update at the end of a frame is a four-int copy
// rather than a separate array.

using System;
using MediaKernel.Core.Codecs.MpegVideo;
using MediaKernel.Core.Dsp.Tx;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>enum HeaderType</c>.</summary>
internal enum DcaHeaderType
{
    Core,
    Xch,
    Xxch,
}

/// <summary>
/// <c>DCACoreDecoder</c> - one DTS core substream decoder. Fields are in the C's declared order.
/// </summary>
internal sealed class DcaCore
{
    /// <summary><c>DCA_ADPCM_COEFFS</c> and friends from dcadata.h.</summary>
    public const int AdpcmCoeffs = 4;

    private GetBitContext gb;

    // Bit stream header
    public int CrcPresent;
    public int Npcmblocks;
    public int FrameSize;
    public int AudioMode;
    public int SampleRate;
    public int BitRate;
    public int DrcPresent;
    public int TsPresent;
    public int AuxPresent;
    public int ExtAudioType;
    public int ExtAudioPresent;
    public int SyncSsf;
    public int LfePresent;
    public int PredictorHistory;
    public int FilterPerfect;
    public int SourcePcmRes;
    public int EsFormat;
    public int SumdiffFront;
    public int SumdiffSurround;

    // Primary audio coding header
    private int nsubframes;
    private int nchannels;
    public int ChMask;
    private readonly sbyte[] nsubbands = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] subbandVqStart = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] jointIntensityIndex = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] transitionModeSel = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] scaleFactorSel = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] bitAllocationSel = new sbyte[DcaLimits.Channels];
    private readonly sbyte[] quantIndexSel = new sbyte[DcaLimits.Channels * DcaLimits.CodeBooks];
    private readonly int[] scaleFactorAdj = new int[DcaLimits.Channels * DcaLimits.CodeBooks];

    // Primary audio coding side information
    private readonly sbyte[] nsubsubframes = new sbyte[DcaLimits.Subframes];
    private readonly sbyte[] predictionMode = new sbyte[DcaLimits.Channels * DcaLimits.SubbandsX96];
    private readonly short[] predictionVqIndex = new short[DcaLimits.Channels * DcaLimits.SubbandsX96];
    private readonly sbyte[] bitAllocation = new sbyte[DcaLimits.Channels * DcaLimits.SubbandsX96];
    private readonly sbyte[] transitionMode =
        new sbyte[DcaLimits.Subframes * DcaLimits.Channels * DcaLimits.Subbands];
    private readonly int[] scaleFactors = new int[DcaLimits.Channels * DcaLimits.Subbands * 2];

    // XBR's own bit allocation and scale factors. The C declares these on the stack of
    // parse_xbr_subframe; they are fields here because xbr_scale_factors alone is 2 KB, which
    // is past the stackalloc budget this project works to, and a method that stackallocs never
    // reaches Tier1.
    private readonly sbyte[] xbrBitAllocation = new sbyte[DcaLimits.Channels * DcaLimits.Subbands];
    private readonly int[] xbrScaleFactors =
        new int[DcaLimits.Channels * DcaLimits.Subbands * 2];
    private readonly sbyte[] jointScaleSel = new sbyte[DcaLimits.Channels];
    private readonly int[] jointScaleFactors = new int[DcaLimits.Channels * DcaLimits.SubbandsX96];

    // Auxiliary data
    public int PrimDmixEmbedded;
    public int PrimDmixType;
    private readonly int[] primDmixCoeff =
        new int[DcaLimits.DmixChannelsMax * DcaLimits.CoreChannelsMax];

    /// <summary>
    /// <c>dca-&gt;request_channel_layout</c>: the speaker layout the caller asked for, or 0 for
    /// whatever the stream carries.
    /// </summary>
    /// <remarks>
    /// This is not only a downmix switch. Any non-zero value also stops the (X)XCH extensions
    /// being parsed at all - the C guards both sync searches and the EXSS branch on it - because
    /// a caller that wants fewer channels than the core has will not want the extension's extra
    /// ones either. That is why a 5.1 request on a 6.1 XCH stream decodes to the core's own six
    /// channels rather than to a mix of seven.
    /// </remarks>
    public int RequestChannelLayout;

    /// <summary><c>s-&gt;request_mask</c>: what the layout request resolved to for this frame.</summary>
    private int requestMask;

    // Core extensions
    public int ExtAudioMask;

    // Sample buffers. subbandBuffer holds every channel's every band back to back, each band
    // preceded by its four samples of ADPCM history; subbandOffset[ch * 32 + band] is where a
    // band's first real sample sits.
    private int[] subbandBuffer = [];
    private readonly int[] subbandOffset = new int[DcaLimits.Channels * DcaLimits.Subbands];
    private int lfeSamplesAt;

    // X96's own subband samples: 64 bands per channel rather than 32, in a buffer of their own
    // because the low 32 are residual coded against the core's and both are needed at once.
    private int[] x96SubbandBuffer = [];
    private readonly int[] x96SubbandOffset =
        new int[DcaLimits.Channels * DcaLimits.SubbandsX96];
    private int x96AllocatedNpcmblocks = -1;

    // X96 state, in the C's declared order.
    private uint x96Rand;
    private int x96RevNo;
    private int x96CrcPresent;
    private int x96NchannelsField;
    private int x96HighRes;
    private int x96SubbandStart;

    /// <summary><c>s-&gt;output_history_lfe_float</c>: the LFE x96 filter's carried sample.</summary>
    private float outputHistoryLfeFloat;

    /// <summary><c>s-&gt;output_history_lfe_fixed</c>: the same, for the integer path.</summary>
    private int outputHistoryLfeFixed;
    private int allocatedNpcmblocks = -1;

    // Per-channel QMF history. FFmpeg's DCADSPData: hist1[1024], hist2[64] and an offset.
    //
    // In the C the float and integer histories are a UNION over the same memory, with one
    // shared offset; here they are separate arrays, because a union of float[] and int[] has no
    // C# equivalent that is not unsafe. The behaviour is identical as long as SetFilterMode
    // clears BOTH sets, which it does: the C has to clear on every switch between the two
    // representations precisely because they overlap, and it signals that switch through
    // DCA_FILTER_MODE_FIXED in the mode word. Keeping that bit here keeps the clearing
    // schedule the same rather than merely similar.
    private readonly float[][] hist1 = new float[DcaLimits.Channels][];
    private readonly float[][] hist2 = new float[DcaLimits.Channels][];
    private readonly int[][] hist1Fixed = new int[DcaLimits.Channels][];
    private readonly int[][] hist2Fixed = new int[DcaLimits.Channels][];
    private readonly int[] histOffset = new int[DcaLimits.Channels];

    /// <summary><c>DCA_FILTER_MODE_X96</c> and <c>DCA_FILTER_MODE_FIXED</c>.</summary>
    private const int FilterModeX96 = 0x01;
    private const int FilterModeFixed = 0x02;

    /// <summary><c>DCA_FILTER_MODE_FIXED</c>, for callers testing <see cref="FilterMode"/>.</summary>
    public const int FilterModeFixedFlag = FilterModeFixed;

    private TxMdct? imdct32;
    private TxMdct? imdct64;

    // PCM output, one plane per speaker. FFmpeg writes straight into the AVFrame's planes for
    // the speakers the layout asks for and into a scratch buffer for the rest; there is no
    // AVFrame here, so every present speaker gets a plane and the caller picks.
    private readonly float[]?[] outputSamples = new float[(int)DcaSpeaker.Count][];
    private readonly int[]?[] outputSamplesFixed = new int[(int)DcaSpeaker.Count][];
    private readonly int[] chRemap = new int[(int)DcaSpeaker.Count];
    private int nchannelsOut;

    public int Npcmsamples { get; private set; }
    public int OutputRate { get; private set; }

    private int filterMode;

    /// <summary><c>s-&gt;filter_mode</c>, which dcadec.c reads to decide residual validity.</summary>
    public int FilterMode => filterMode;

    /// <summary>
    /// <c>s-&gt;gb_in</c>: the reader as it was at the start of the frame. A core extension is
    /// found by its sync word at a byte offset inside the core frame, so parsing one means
    /// rewinding to the frame start and skipping to that bit position.
    /// </summary>
    private GetBitContext gbIn;

    /// <summary>
    /// <c>s-&gt;xch_pos</c>: bit position of the XCH channel set header, or 0 when the frame has
    /// no XCH extension. Zero doubles as "absent" in the C too, since bit 0 is the sync word.
    /// </summary>
    private int xchPos;

    // XXCH, in the C's declared order.
    private int xxchCrcPresent;
    private int xxchMaskNbits;
    private int xxchCoreMask;
    private int xxchSpkrMask;
    private int xxchDmixEmbedded;
    private int xxchDmixScaleInv;
    private readonly int[] xxchDmixMask = new int[DcaLimits.XxchChannelsMax];
    private readonly int[] xxchDmixCoeff =
        new int[DcaLimits.XxchChannelsMax * DcaLimits.CoreChannelsMax];
    private int xxchPos;

    /// <summary><c>s-&gt;x96_pos</c>.</summary>
    private int x96Pos;

    /// <summary>The speaker each output plane belongs to, in output order.</summary>
    public ReadOnlySpan<int> ChannelRemap => chRemap.AsSpan(0, nchannelsOut);

    /// <summary>One decoded plane, in the order <see cref="ChannelRemap"/> gives.</summary>
    public ReadOnlySpan<float> Plane(int index) =>
        outputSamples[chRemap[index]].AsSpan(0, Npcmsamples);

    /// <summary>
    /// One decoded plane from the fixed point path, as the 32-bit samples FFmpeg emits.
    /// </summary>
    /// <remarks>
    /// These are <c>AV_SAMPLE_FMT_S32P</c>: 24 significant bits shifted up by 8, exactly as
    /// <c>filter_frame_fixed</c>'s final loop writes them, so the low byte of every sample is
    /// zero. The shift is part of the format and not a scale the caller may choose - FFmpeg sets
    /// <c>bits_per_raw_sample = 24</c> alongside it.
    /// </remarks>
    public ReadOnlySpan<int> PlaneFixed(int index) =>
        outputSamplesFixed[chRemap[index]].AsSpan(0, Npcmsamples);

    /// <summary>Why the last <see cref="Parse"/> or <see cref="FilterFrame"/> returned false.</summary>
    public string LastError { get; private set; } = "";

    // 5.3.1 - Bit stream header
    private static readonly byte[] AudioModeChMask =
    [
        DcaSpeakerLayout.Mono,
        DcaSpeakerLayout.Stereo,
        DcaSpeakerLayout.Stereo,
        DcaSpeakerLayout.Stereo,
        DcaSpeakerLayout.Stereo,
        DcaSpeakerLayout.Layout3_0,
        DcaSpeakerLayout.Layout2_1,
        DcaSpeakerLayout.Layout3_1,
        DcaSpeakerLayout.Layout2_2,
        DcaSpeakerLayout.Layout5Point0,
    ];

    /// <summary><c>prm_ch_to_spkr_map[DCA_AMODE_COUNT][5]</c>.</summary>
    private static readonly sbyte[] PrmChToSpkrMap =
    [
        (sbyte)DcaSpeaker.C, -1, -1, -1, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, -1, -1, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, -1, -1, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, -1, -1, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, -1, -1, -1,
        (sbyte)DcaSpeaker.C, (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, -1, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, (sbyte)DcaSpeaker.Cs, -1, -1,
        (sbyte)DcaSpeaker.C, (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, (sbyte)DcaSpeaker.Cs, -1,
        (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, (sbyte)DcaSpeaker.Ls, (sbyte)DcaSpeaker.Rs, -1,
        (sbyte)DcaSpeaker.C, (sbyte)DcaSpeaker.L, (sbyte)DcaSpeaker.R, (sbyte)DcaSpeaker.Ls,
            (sbyte)DcaSpeaker.Rs,
    ];

    private static readonly byte[] BlockCodeNbits = [7, 10, 12, 13, 15, 17, 19];

    /// <summary><c>ff_dca_core_init</c>.</summary>
    public void Init()
    {
        DcaHuff.InitVlcs();

        // av_tx_init(AV_TX_FLOAT_MDCT, inverse, 32, scale = 1.0f, 0), and the 64-point one
        // X96 needs. FFmpeg builds both up front too, as imdct[0] and imdct[1].
        imdct32 ??= TxMdct.Create(32, inverse: true, full: false, scale: 1.0f);
        imdct64 ??= TxMdct.Create(64, inverse: true, full: false, scale: 1.0f);

        for (int ch = 0; ch < DcaLimits.Channels; ch++)
        {
            hist1[ch] ??= new float[1024];
            hist2[ch] ??= new float[64];
            hist1Fixed[ch] ??= new int[1024];
            hist2Fixed[ch] ??= new int[64];
        }
    }

    /// <summary><c>ff_dca_core_flush</c> / <c>erase_dsp_history</c>.</summary>
    public void Flush()
    {
        EraseDspHistory();
        filterMode = 0;
    }

    /// <summary><c>erase_dsp_history</c>.</summary>
    /// <remarks>
    /// The two LFE carried samples are part of the DSP history and the C clears them here, not
    /// only on a flush. Leaving them out leaks one sample of the previous frame's LFE across a
    /// filter mode change, which is audible on nothing and wrong on an X96 bit-exact comparison.
    /// </remarks>
    private void EraseDspHistory()
    {
        for (int ch = 0; ch < DcaLimits.Channels; ch++)
        {
            if (hist1[ch] is not null) Array.Clear(hist1[ch]!);
            if (hist2[ch] is not null) Array.Clear(hist2[ch]!);
            if (hist1Fixed[ch] is not null) Array.Clear(hist1Fixed[ch]!);
            if (hist2Fixed[ch] is not null) Array.Clear(hist2Fixed[ch]!);
            histOffset[ch] = 0;
        }
        outputHistoryLfeFloat = 0;
        outputHistoryLfeFixed = 0;
    }

    /// <summary><c>dca_get_vlc</c>.</summary>
    private int DcaGetVlc(VLCElem[] table, int bits) => gb.GetVlc2(table, bits, 2);

    /// <summary><c>get_array</c> - <paramref name="size"/> signed values of <paramref name="n"/> bits.</summary>
    private void GetArray(Span<int> array, int size, int n)
    {
        for (int i = 0; i < size; i++)
            array[i] = gb.GetSBits(n);
    }

    /// <summary><c>ff_dca_seek_bits</c>.</summary>
    private bool SeekBits(int p)
    {
        if (p < gb.GetBitsCount() || p > gb.SizeInBits)
            return false;
        gb.SkipBits(p - gb.GetBitsCount());
        return true;
    }

    // 5.3.1 - Bit stream header
    private bool ParseFrameHeader()
    {
        var h = default(DcaCoreFrameHeader);
        DcaParseError err = Dca.ParseCoreFrameHeader(ref h, ref gb);

        if (err != DcaParseError.None)
        {
            LastError = err switch
            {
                DcaParseError.SyncWord => "no core sync word",
                DcaParseError.DeficitSamples => "deficit samples are not supported",
                DcaParseError.PcmBlocks => $"unsupported number of PCM sample blocks ({h.Npcmblocks})",
                DcaParseError.FrameSize => $"invalid core frame size ({h.FrameSize} bytes)",
                DcaParseError.AudioMode => $"unsupported audio channel arrangement ({h.AudioMode})",
                DcaParseError.SampleRate => "invalid core audio sampling frequency",
                DcaParseError.ReservedBit => "reserved bit set",
                DcaParseError.LfeFlag => "invalid low frequency effects flag",
                DcaParseError.PcmRes => "invalid source PCM resolution",
                _ => "unknown core frame header error",
            };
            return false;
        }

        CrcPresent = h.CrcPresent;
        Npcmblocks = h.Npcmblocks;
        FrameSize = h.FrameSize;
        AudioMode = h.AudioMode;
        SampleRate = (int)DcaData.SampleRates[h.SrCode];
        BitRate = (int)DcaData.BitRates[h.BrCode];
        DrcPresent = h.DrcPresent;
        TsPresent = h.TsPresent;
        AuxPresent = h.AuxPresent;
        ExtAudioType = h.ExtAudioType;
        ExtAudioPresent = h.ExtAudioPresent;
        SyncSsf = h.SyncSsf;
        LfePresent = h.LfePresent;
        PredictorHistory = h.PredictorHistory;
        FilterPerfect = h.FilterPerfect;
        SourcePcmRes = DcaData.BitsPerSample[h.PcmrCode];
        EsFormat = h.PcmrCode & 1;
        SumdiffFront = h.SumdiffFront;
        SumdiffSurround = h.SumdiffSurround;

        return true;
    }

    // 5.3.2 - Primary audio coding header
    private bool ParseCodingHeader(DcaHeaderType header, int xchBase)
    {
        int headerSize = 0;
        int headerPos = gb.GetBitsCount();

        if (gb.GetBitsLeft() < 0)
        {
            LastError = "ran out of bits before the coding header";
            return false;
        }

        switch (header)
        {
            case DcaHeaderType.Core:
                // Number of subframes
                nsubframes = (int)gb.GetBits(4) + 1;

                // Number of primary audio channels
                nchannels = (int)gb.GetBits(3) + 1;
                if (nchannels != DcaData.Channels[AudioMode])
                {
                    LastError = $"invalid number of primary audio channels ({nchannels}) for "
                              + $"audio channel arrangement ({AudioMode})";
                    return false;
                }

                ChMask = AudioModeChMask[AudioMode];

                // Add LFE channel if present
                if (LfePresent != 0)
                    ChMask |= DcaSpeakerMask.Lfe1;
                break;

            case DcaHeaderType.Xch:
                // One extra primary channel, the rear centre. Everything else about the channel
                // set - subband activity, prediction, scale factors - is parsed by the shared
                // code below, which runs from xchBase rather than from zero.
                nchannels = DcaData.Channels[AudioMode] + 1;
                ChMask |= DcaSpeakerMask.Cs;
                break;

            case DcaHeaderType.Xxch:
            {
                // Channel set header length
                headerSize = (int)gb.GetBits(7) + 1;

                // The CRC check the C does here is a no-op unless AV_EF_CRCCHECK is set, which
                // FFmpeg does not set by default; see CheckCrc.
                if (xxchCrcPresent != 0
                    && !CheckCrc(headerPos, headerPos + headerSize * 8))
                {
                    LastError = "invalid XXCH channel set header checksum";
                    return false;
                }

                // Number of channels in a channel set
                int setChannels = (int)gb.GetBits(3) + 1;
                if (setChannels > DcaLimits.XxchChannelsMax)
                {
                    LastError = $"{setChannels} XXCH channels is more than this decoder handles";
                    return false;
                }
                nchannels = DcaData.Channels[AudioMode] + setChannels;

                // Loudspeaker layout mask. It is stored relative to Cs, because every speaker
                // below Cs belongs to the core by definition.
                int layout = (int)gb.GetBits(xxchMaskNbits - (int)DcaSpeaker.Cs);
                xxchSpkrMask = layout << (int)DcaSpeaker.Cs;

                if (System.Numerics.BitOperations.PopCount((uint)xxchSpkrMask) != setChannels)
                {
                    LastError = $"invalid XXCH speaker layout mask (0x{xxchSpkrMask:X})";
                    return false;
                }

                if ((xxchCoreMask & xxchSpkrMask) != 0)
                {
                    LastError = $"XXCH speaker layout mask (0x{xxchSpkrMask:X}) overlaps with "
                              + $"core (0x{xxchCoreMask:X})";
                    return false;
                }

                // Combine core and XXCH masks together
                ChMask = xxchCoreMask | xxchSpkrMask;

                // Downmix coefficients present in stream
                if (gb.GetBits1() != 0)
                {
                    int coeffAt = 0;

                    // Downmix already performed by encoder
                    xxchDmixEmbedded = (int)gb.GetBits1();

                    // Downmix scale factor
                    int index = (int)gb.GetBits(6) * 4 - (DcaData.Dmixtable.Length - DcaData.InvDmixtable.Length) - 3;
                    if ((uint)index >= DcaData.InvDmixtable.Length)
                    {
                        LastError = $"invalid XXCH downmix scale index ({index})";
                        return false;
                    }
                    xxchDmixScaleInv = (int)DcaData.InvDmixtable[index];

                    // Downmix channel mapping mask
                    for (int ch = 0; ch < setChannels; ch++)
                    {
                        int dmixMask = (int)gb.GetBits(xxchMaskNbits);
                        if ((dmixMask & xxchCoreMask) != dmixMask)
                        {
                            LastError = $"invalid XXCH downmix channel mapping mask "
                                      + $"(0x{dmixMask:X})";
                            return false;
                        }
                        xxchDmixMask[ch] = dmixMask;
                    }

                    // Downmix coefficients, one per set bit of each channel's mapping mask.
                    for (int ch = 0; ch < setChannels; ch++)
                    {
                        for (int n = 0; n < xxchMaskNbits; n++)
                        {
                            if ((xxchDmixMask[ch] & (1 << n)) == 0)
                                continue;

                            int code = (int)gb.GetBits(7);
                            int sign = (code >> 6) - 1;
                            code &= 63;
                            if (code != 0)
                            {
                                index = code * 4 - 3;
                                if ((uint)index >= DcaData.Dmixtable.Length)
                                {
                                    LastError = $"invalid XXCH downmix coefficient index "
                                              + $"({index})";
                                    return false;
                                }
                                xxchDmixCoeff[coeffAt++] = (DcaData.Dmixtable[index] ^ sign) - sign;
                            }
                            else
                            {
                                xxchDmixCoeff[coeffAt++] = 0;
                            }
                        }
                    }
                }
                else
                {
                    xxchDmixEmbedded = 0;
                }

                break;
            }

            default:
                throw new NotSupportedException(
                    $"DTS core header type {header} is not translated.");
        }

        // Subband activity count
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            nsubbands[ch] = (sbyte)(gb.GetBits(5) + 2);
            if (nsubbands[ch] > DcaLimits.Subbands)
            {
                LastError = $"invalid subband activity count ({nsubbands[ch]}) for channel {ch}";
                return false;
            }
        }

        // High frequency VQ start subband
        for (int ch = xchBase; ch < nchannels; ch++)
            subbandVqStart[ch] = (sbyte)(gb.GetBits(5) + 1);

        // Joint intensity coding index
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            int n = (int)gb.GetBits(3);
            if (n != 0 && header == DcaHeaderType.Xxch)
                n += xchBase - 1;
            if (n > nchannels)
            {
                LastError = $"invalid joint intensity coding index ({n}) for channel {ch}";
                return false;
            }
            jointIntensityIndex[ch] = (sbyte)n;
        }

        // Transient mode code book
        for (int ch = xchBase; ch < nchannels; ch++)
            transitionModeSel[ch] = (sbyte)gb.GetBits(2);

        // Scale factor code book
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            scaleFactorSel[ch] = (sbyte)gb.GetBits(3);
            if (scaleFactorSel[ch] == 7)
            {
                LastError = $"invalid scale factor code book for channel {ch}";
                return false;
            }
        }

        // Bit allocation quantizer select
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            bitAllocationSel[ch] = (sbyte)gb.GetBits(3);
            if (bitAllocationSel[ch] == 7)
            {
                LastError = $"invalid bit allocation quantizer select for channel {ch}";
                return false;
            }
        }

        // Quantization index codebook select
        for (int n = 0; n < DcaLimits.CodeBooks; n++)
            for (int ch = xchBase; ch < nchannels; ch++)
                quantIndexSel[ch * DcaLimits.CodeBooks + n] =
                    (sbyte)gb.GetBits(DcaData.QuantIndexSelNbits[n]);

        // Scale factor adjustment index. Only sent for a book that uses Huffman codes, and left
        // at whatever it was otherwise - the C does not clear it either, because a book that is
        // not Huffman coded never reads it back.
        for (int n = 0; n < DcaLimits.CodeBooks; n++)
            for (int ch = xchBase; ch < nchannels; ch++)
                if (quantIndexSel[ch * DcaLimits.CodeBooks + n] < DcaData.QuantIndexGroupSize[n])
                    scaleFactorAdj[ch * DcaLimits.CodeBooks + n] =
                        (int)DcaData.ScaleFactorAdj[(int)gb.GetBits(2)];

        if (header == DcaHeaderType.Xxch)
        {
            // Reserved, byte align, CRC16 of the channel set header: all three are skipped by
            // seeking to the end of the header the length field declared.
            if (!SeekBits(headerPos + headerSize * 8))
            {
                LastError = "read past end of XXCH channel set header";
                return false;
            }
        }
        else
        {
            // Audio header CRC check word
            if (CrcPresent != 0)
                gb.SkipBits(16);
        }

        return true;
    }

    /// <summary>
    /// <c>ff_dca_check_crc</c> (dcadec.h) - true when the CRC over the given bit range is
    /// acceptable.
    /// </summary>
    /// <remarks>
    /// FFmpeg only computes this when <c>AV_EF_CRCCHECK</c> or <c>AV_EF_CAREFUL</c> is set, and
    /// it sets neither by default, so on the reference path this returns "fine" without looking
    /// at the data. Keeping that is deliberate: the references this is gated against were made
    /// by a decoder that did not check, so a frame FFmpeg accepts has to be accepted here too.
    /// The range check is kept because it costs nothing and catches a caller passing nonsense.
    /// </remarks>
    private bool CheckCrc(int p1, int p2)
    {
        if (((p1 | p2) & 7) != 0 || p1 < 0 || p2 > gb.SizeInBits || p2 - p1 < 16)
            return false;
        return true;
    }

    /// <summary><c>parse_scale</c> - returns the scale factor, or -1 on a bad index.</summary>
    private int ParseScale(ref int scaleIndex, int sel)
    {
        ReadOnlySpan<uint> scaleTable = sel > 5 ? DcaData.ScaleFactorQuant7
                                                : DcaData.ScaleFactorQuant6;

        // If a Huffman code was used, the DIFFERENCE of scales was encoded.
        if (sel < 5)
            scaleIndex += gb.GetVlc2(DcaHuff.ScaleFactor[sel], DcaHuff.ScalesVlcBits, 2);
        else
            scaleIndex = (int)gb.GetBits(sel + 1);

        if ((uint)scaleIndex >= (uint)scaleTable.Length)
        {
            LastError = $"invalid scale factor index {scaleIndex}";
            return -1;
        }

        return (int)scaleTable[scaleIndex];
    }

    /// <summary><c>parse_joint_scale</c>.</summary>
    private int ParseJointScale(int sel)
    {
        int scaleIndex;

        // The absolute value was encoded even when a Huffman code was used.
        if (sel < 5)
            scaleIndex = gb.GetVlc2(DcaHuff.ScaleFactor[sel], DcaHuff.ScalesVlcBits, 2);
        else
            scaleIndex = (int)gb.GetBits(sel + 1);

        // Bias by 64
        scaleIndex += 64;

        if ((uint)scaleIndex >= (uint)DcaData.JointScaleFactors.Length)
        {
            LastError = $"invalid joint scale factor index {scaleIndex}";
            return -1;
        }

        return (int)DcaData.JointScaleFactors[scaleIndex];
    }

    // 5.4.1 - Primary audio coding side information
    private bool ParseSubframeHeader(int sf, DcaHeaderType header, int xchBase)
    {
        if (gb.GetBitsLeft() < 0)
        {
            LastError = $"ran out of bits before subframe {sf}";
            return false;
        }

        if (header == DcaHeaderType.Core)
        {
            // Subsubframe count
            nsubsubframes[sf] = (sbyte)(gb.GetBits(2) + 1);

            // Partial subsubframe sample count
            gb.SkipBits(3);
        }

        // Prediction mode
        for (int ch = xchBase; ch < nchannels; ch++)
            for (int band = 0; band < nsubbands[ch]; band++)
                predictionMode[ch * DcaLimits.SubbandsX96 + band] = (sbyte)gb.GetBits1();

        // Prediction coefficients VQ address
        for (int ch = xchBase; ch < nchannels; ch++)
            for (int band = 0; band < nsubbands[ch]; band++)
                if (predictionMode[ch * DcaLimits.SubbandsX96 + band] != 0)
                    predictionVqIndex[ch * DcaLimits.SubbandsX96 + band] = (short)gb.GetBits(12);

        // Bit allocation index
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            int sel = bitAllocationSel[ch];

            for (int band = 0; band < subbandVqStart[ch]; band++)
            {
                int abits;

                if (sel < 5)
                    abits = DcaGetVlc(DcaHuff.BitAllocation[sel], DcaHuff.BitAllocationBits[sel]);
                else
                    abits = (int)gb.GetBits(sel - 1);

                if (abits > DcaLimits.AbitsMax)
                {
                    LastError = $"invalid bit allocation index {abits}";
                    return false;
                }

                bitAllocation[ch * DcaLimits.SubbandsX96 + band] = (sbyte)abits;
            }
        }

        // Transition mode
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            int at = (sf * DcaLimits.Channels + ch) * DcaLimits.Subbands;

            // Clear transition mode for all subbands
            Array.Clear(transitionMode, at, DcaLimits.Subbands);

            // A transient is possible only if there is more than one subsubframe.
            if (nsubsubframes[sf] > 1)
            {
                int sel = transitionModeSel[ch];
                for (int band = 0; band < subbandVqStart[ch]; band++)
                    if (bitAllocation[ch * DcaLimits.SubbandsX96 + band] != 0)
                        transitionMode[at + band] = (sbyte)gb.GetVlc2(
                            DcaHuff.TransitionMode[sel], DcaHuff.TmodeVlcBits, 1);
            }
        }

        // Scale factors
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            int sel = scaleFactorSel[ch];
            int scaleIndex = 0;
            int tm = (sf * DcaLimits.Channels + ch) * DcaLimits.Subbands;
            int sfAt = ch * DcaLimits.Subbands * 2;

            // Extract scales for subbands up to the VQ start.
            for (int band = 0; band < subbandVqStart[ch]; band++)
            {
                if (bitAllocation[ch * DcaLimits.SubbandsX96 + band] != 0)
                {
                    int ret = ParseScale(ref scaleIndex, sel);
                    if (ret < 0) return false;
                    scaleFactors[sfAt + band * 2] = ret;

                    if (transitionMode[tm + band] != 0)
                    {
                        ret = ParseScale(ref scaleIndex, sel);
                        if (ret < 0) return false;
                        scaleFactors[sfAt + band * 2 + 1] = ret;
                    }
                }
                else
                {
                    scaleFactors[sfAt + band * 2] = 0;
                }
            }

            // High frequency VQ subbands
            for (int band = subbandVqStart[ch]; band < nsubbands[ch]; band++)
            {
                int ret = ParseScale(ref scaleIndex, sel);
                if (ret < 0) return false;
                scaleFactors[sfAt + band * 2] = ret;
            }
        }

        // Joint subband codebook select
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            if (jointIntensityIndex[ch] != 0)
            {
                jointScaleSel[ch] = (sbyte)gb.GetBits(3);
                if (jointScaleSel[ch] == 7)
                {
                    LastError = $"invalid joint scale factor code book for channel {ch}";
                    return false;
                }
            }
        }

        // Scale factors for joint subband coding
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            int srcCh = jointIntensityIndex[ch] - 1;
            if (srcCh >= 0)
            {
                int sel = jointScaleSel[ch];
                for (int band = nsubbands[ch]; band < nsubbands[srcCh]; band++)
                {
                    int ret = ParseJointScale(sel);
                    if (ret < 0) return false;
                    jointScaleFactors[ch * DcaLimits.SubbandsX96 + band] = ret;
                }
            }
        }

        // Dynamic range coefficient
        if (DrcPresent != 0 && header == DcaHeaderType.Core)
            gb.SkipBits(8);

        // Side information CRC check word
        if (CrcPresent != 0)
            gb.SkipBits(16);

        return true;
    }

    /// <summary>
    /// <c>decode_blockcodes</c> - unpack eight samples from two base-<paramref name="levels"/>
    /// integers.
    /// </summary>
    /// <remarks>
    /// Returns the leftover of both codes, which must be zero: a nonzero remainder means the
    /// code did not come from this many levels and the frame is corrupt. That check is the
    /// return value, not a side effect.
    /// </remarks>
    private static int DecodeBlockcodes(int code1, int code2, int levels, Span<int> audio)
    {
        int offset = (levels - 1) / 2;
        int n;

        for (n = 0; n < DcaLimits.SubbandSamples / 2; n++)
        {
            int div = code1 / levels;
            audio[n] = code1 - div * levels - offset;
            code1 = div;
        }
        for (; n < DcaLimits.SubbandSamples; n++)
        {
            int div = code2 / levels;
            audio[n] = code2 - div * levels - offset;
            code2 = div;
        }

        return code1 | code2;
    }

    /// <summary><c>parse_block_codes</c>.</summary>
    private bool ParseBlockCodes(Span<int> audio, int abits)
    {
        int code1 = (int)gb.GetBits(BlockCodeNbits[abits - 1]);
        int code2 = (int)gb.GetBits(BlockCodeNbits[abits - 1]);
        int levels = (int)DcaData.QuantLevels[abits];

        if (DecodeBlockcodes(code1, code2, levels, audio) != 0)
        {
            LastError = "failed to decode block code(s)";
            return false;
        }

        return true;
    }

    /// <summary><c>parse_huffman_codes</c>.</summary>
    private void ParseHuffmanCodes(Span<int> audio, int abits, int sel)
    {
        for (int i = 0; i < DcaLimits.SubbandSamples; i++)
            audio[i] = DcaGetVlc(DcaHuff.QuantIndex[abits - 1][sel],
                                 DcaHuff.QuantIndexBits[abits - 1][sel]);
    }

    /// <summary>
    /// <c>extract_audio</c> - eight sub-band samples in whichever of the three codings applies.
    /// </summary>
    /// <returns>
    /// 1 when Huffman codes were used, 0 otherwise, and -1 on a corrupt block code. The
    /// distinction matters: only the Huffman case applies the scale factor adjustment.
    /// </returns>
    private int ExtractAudio(Span<int> audio, int abits, int ch)
    {
        if (abits == 0)
        {
            // No bits allocated
            audio[..DcaLimits.SubbandSamples].Clear();
            return 0;
        }

        if (abits <= DcaLimits.CodeBooks)
        {
            int sel = quantIndexSel[ch * DcaLimits.CodeBooks + abits - 1];
            if (sel < DcaData.QuantIndexGroupSize[abits - 1])
            {
                ParseHuffmanCodes(audio, abits, sel);
                return 1;
            }
            if (abits <= 7)
                return ParseBlockCodes(audio, abits) ? 0 : -1;
        }

        // No further encoding
        GetArray(audio, DcaLimits.SubbandSamples, abits - 3);
        return 0;
    }

    // 5.5 - Primary audio data arrays
    private bool ParseSubframeAudio(int sf, DcaHeaderType header, int xchBase,
                                    ref int subPos, ref int lfePos)
    {
        Span<int> audio = stackalloc int[16];
        Span<int> vqIndex = stackalloc int[DcaLimits.Subbands];
        Span<int> bandOffset = stackalloc int[DcaLimits.Subbands];

        // Check the number of subband samples in this subframe
        int nsamples = nsubsubframes[sf] * DcaLimits.SubbandSamples;
        if (subPos + nsamples > Npcmblocks)
        {
            LastError = "subband sample buffer overflow";
            return false;
        }

        if (gb.GetBitsLeft() < 0)
        {
            LastError = $"ran out of bits in subframe {sf}";
            return false;
        }

        // VQ encoded subbands
        for (int ch = xchBase; ch < nchannels; ch++)
        {
            for (int band = subbandVqStart[ch]; band < nsubbands[ch]; band++)
                vqIndex[band] = (int)gb.GetBits(10);

            if (subbandVqStart[ch] < nsubbands[ch])
            {
                for (int band = 0; band < DcaLimits.Subbands; band++)
                    bandOffset[band] = subbandOffset[ch * DcaLimits.Subbands + band];

                DcaDsp.DecodeHf(subbandBuffer, bandOffset, vqIndex, DcaData.HighFreqVq,
                                scaleFactors.AsSpan(ch * DcaLimits.Subbands * 2),
                                subbandVqStart[ch], nsubbands[ch], subPos, nsamples);
            }
        }

        // Low frequency effect data
        if (LfePresent != 0 && header == DcaHeaderType.Core)
        {
            // Determine the number of LFE samples in this subframe
            int nlfesamples = 2 * LfePresent * nsubsubframes[sf];

            GetArray(audio, nlfesamples, 8);

            uint index = gb.GetBits(8);
            if (index >= (uint)DcaData.ScaleFactorQuant7.Length)
            {
                LastError = $"invalid LFE scale factor index {index}";
                return false;
            }

            // Look up the 7-bit root square quantization table, then account for the quantizer
            // step size, which is 0.035: 4697620 is 0.035 * (1 << 27).
            int scale = (int)DcaData.ScaleFactorQuant7[(int)index];
            scale = DcaMath.Mul23(4697620, scale);

            int ofs = lfePos;
            for (int n = 0; n < nlfesamples; n++, ofs++)
                subbandBuffer[lfeSamplesAt + ofs] = DcaMath.Clip23((int)((long)audio[n] * scale >> 4));

            lfePos = ofs;
        }

        // Audio data
        {
            int ofs = subPos;
            for (int ssf = 0; ssf < nsubsubframes[sf]; ssf++)
            {
                for (int ch = xchBase; ch < nchannels; ch++)
                {
                    if (gb.GetBitsLeft() < 0)
                    {
                        LastError = $"ran out of bits in subsubframe {ssf}";
                        return false;
                    }

                    // Not high frequency VQ subbands
                    for (int band = 0; band < subbandVqStart[ch]; band++)
                    {
                        int abits = bitAllocation[ch * DcaLimits.SubbandsX96 + band];

                        int ret = ExtractAudio(audio, abits, ch);
                        if (ret < 0)
                            return false;

                        // Select the quantization step size table and look the step size up.
                        int stepSize = BitRate == 3
                            ? (int)DcaData.LosslessQuant[abits]
                            : (int)DcaData.LossyQuant[abits];

                        // Identify the transient location
                        int transSsf = transitionMode[(sf * DcaLimits.Channels + ch)
                                                      * DcaLimits.Subbands + band];

                        // Determine the proper scale factor
                        int sfAt = ch * DcaLimits.Subbands * 2 + band * 2;
                        int scale = transSsf == 0 || ssf < transSsf
                            ? scaleFactors[sfAt]
                            : scaleFactors[sfAt + 1];

                        // Adjust the scale factor when SEL indicates a Huffman code.
                        if (ret > 0)
                        {
                            long adj = scaleFactorAdj[ch * DcaLimits.CodeBooks + abits - 1];
                            scale = DcaMath.Clip23((int)(adj * scale >> 22));
                        }

                        DcaDsp.Dequantize(subbandBuffer,
                                          subbandOffset[ch * DcaLimits.Subbands + band] + ofs,
                                          audio, stepSize, scale, residual: false,
                                          DcaLimits.SubbandSamples);
                    }
                }

                // DSYNC
                if ((ssf == nsubsubframes[sf] - 1 || SyncSsf != 0) && gb.GetBits(16) != 0xffff)
                {
                    LastError = "DSYNC check failed";
                    return false;
                }

                ofs += DcaLimits.SubbandSamples;
            }

            // Inverse ADPCM
            for (int ch = xchBase; ch < nchannels; ch++)
            {
                for (int band = 0; band < DcaLimits.Subbands; band++)
                    bandOffset[band] = subbandOffset[ch * DcaLimits.Subbands + band];

                DcaDsp.InverseAdpcm(subbandBuffer, bandOffset,
                                    predictionVqIndex.AsSpan(ch * DcaLimits.SubbandsX96),
                                    predictionMode.AsSpan(ch * DcaLimits.SubbandsX96),
                                    0, nsubbands[ch], subPos, nsamples);
            }

            // Joint subband coding
            for (int ch = xchBase; ch < nchannels; ch++)
            {
                int srcCh = jointIntensityIndex[ch] - 1;
                if (srcCh >= 0)
                {
                    DcaDsp.DecodeJoint(subbandBuffer,
                                       subbandOffset.AsSpan(ch * DcaLimits.Subbands),
                                       subbandOffset.AsSpan(srcCh * DcaLimits.Subbands),
                                       jointScaleFactors.AsSpan(ch * DcaLimits.SubbandsX96),
                                       nsubbands[ch], nsubbands[srcCh], subPos, nsamples);
                }
            }

            subPos = ofs;
        }

        return true;
    }

    /// <summary><c>erase_adpcm_history</c>.</summary>
    private void EraseAdpcmHistory()
    {
        for (int ch = 0; ch < DcaLimits.Channels; ch++)
            for (int band = 0; band < DcaLimits.Subbands; band++)
                Array.Clear(subbandBuffer,
                            subbandOffset[ch * DcaLimits.Subbands + band] - AdpcmCoeffs,
                            AdpcmCoeffs);
    }

    /// <summary>
    /// <c>alloc_sample_buffer</c> - size the subband buffer and lay the band offsets out in it.
    /// </summary>
    /// <remarks>
    /// The offsets are recomputed only when the size changes, as in the C, so a steady stream
    /// does this once. Each band's data is preceded by <see cref="AdpcmCoeffs"/> history
    /// samples, and the decimated LFE samples sit past the end of all of them.
    /// </remarks>
    private void AllocSampleBuffer()
    {
        int nchsamples = AdpcmCoeffs + Npcmblocks;
        int nframesamples = nchsamples * DcaLimits.Channels * DcaLimits.Subbands;
        int nlfesamples = DcaLimits.LfeHistory + Npcmblocks / 2;
        int wanted = nframesamples + nlfesamples;

        if (subbandBuffer.Length < wanted || allocatedNpcmblocks != Npcmblocks)
        {
            if (subbandBuffer.Length < wanted)
                subbandBuffer = new int[wanted];
            else
                Array.Clear(subbandBuffer);

            for (int ch = 0; ch < DcaLimits.Channels; ch++)
                for (int band = 0; band < DcaLimits.Subbands; band++)
                    subbandOffset[ch * DcaLimits.Subbands + band] =
                        (ch * DcaLimits.Subbands + band) * nchsamples + AdpcmCoeffs;

            lfeSamplesAt = nframesamples;
            allocatedNpcmblocks = Npcmblocks;
        }

        if (PredictorHistory == 0)
            EraseAdpcmHistory();
    }

    private bool ParseFrameData(DcaHeaderType header, int xchBase)
    {
        if (!ParseCodingHeader(header, xchBase))
            return false;

        int subPos = 0;
        int lfePos = DcaLimits.LfeHistory;
        for (int sf = 0; sf < nsubframes; sf++)
        {
            if (!ParseSubframeHeader(sf, header, xchBase))
                return false;
            if (!ParseSubframeAudio(sf, header, xchBase, ref subPos, ref lfePos))
                return false;
        }

        for (int ch = xchBase; ch < nchannels; ch++)
        {
            // Determine the number of active subbands for this channel
            int active = nsubbands[ch];
            if (jointIntensityIndex[ch] != 0)
                active = Math.Max(active, nsubbands[jointIntensityIndex[ch] - 1]);

            int band = 0;

            // Update the ADPCM history: the last four samples of this frame become the four
            // that sit in front of the next one.
            for (; band < active; band++)
            {
                int at = subbandOffset[ch * DcaLimits.Subbands + band] - AdpcmCoeffs;
                Array.Copy(subbandBuffer, at + Npcmblocks, subbandBuffer, at, AdpcmCoeffs);
            }

            // Clear inactive subbands
            for (; band < DcaLimits.Subbands; band++)
            {
                int at = subbandOffset[ch * DcaLimits.Subbands + band] - AdpcmCoeffs;
                Array.Clear(subbandBuffer, at, AdpcmCoeffs + Npcmblocks);
            }
        }

        return true;
    }

    /// <summary>
    /// <c>parse_optional_info</c>, restricted to what the core needs.
    /// </summary>
    /// <remarks>
    /// The time code stamp is skipped. The auxiliary data is parsed for its dynamic downmix
    /// matrix, and nothing else in it is read: <see cref="Parse"/> seeks to the end of the frame
    /// straight afterwards, so bits left unconsumed here are not lost. When there is no
    /// auxiliary data, or parsing it fails, <c>prim_dmix_embedded</c> is cleared - which is
    /// exactly what the C does, by leaving its <c>ret</c> negative.
    /// </remarks>
    private void ParseOptionalInfo()
    {
        if (TsPresent != 0)
            gb.SkipBits(32);

        bool haveAux = AuxPresent != 0 && ParseAuxData();
        if (!haveAux)
            PrimDmixEmbedded = 0;

        if (ExtAudioPresent == 0 || gb.Buffer is null)
            return;

        // The search runs BACKWARDS from the end of the core frame. That is not a style choice:
        // the XCH sync word is four identical bytes, so ordinary audio data aliases it readily,
        // and the last match in the frame is the real one.
        int syncPos = Math.Min(FrameSize / 4, gb.SizeInBits / 32) - 1;
        int lastPos = gb.GetBitsCount() / 32;

        switch (ExtAudioType)
        {
            case DcaCoreExtAudioType.Xch:
            {
                // Not searched for at all when the caller asked for a particular layout: the
                // extra channel is not wanted, so finding it would only cost work.
                if (RequestChannelLayout != 0)
                    break;

                // The distance from the sync word to the end of the core frame must equal the
                // XCH frame size, off by one allowed for legacy encoders, and the minimum size
                // is 96 bytes. AMODE and PCHS are checked as well to cut down alias matches.
                uint w2 = 0;
                for (; syncPos >= lastPos; syncPos--)
                {
                    uint w1 = ReadBe32(gb.Buffer, syncPos * 4);
                    if (w1 == DcaSyncwords.Xch)
                    {
                        int size = (int)(w2 >> 22) + 1;
                        int dist = FrameSize - syncPos * 4;
                        if (size >= 96
                            && (size == dist || size - 1 == dist)
                            && (w2 >> 15 & 0x7f) == 0x08)
                        {
                            xchPos = syncPos * 32 + 49;
                            break;
                        }
                    }
                    w2 = w1;
                }

                if (xchPos == 0)
                    LastError = "XCH sync word not found";
                break;
            }

            case DcaCoreExtAudioType.X96:
            {
                // The distance from the sync word to the end of the core frame must equal the
                // X96 frame size exactly - no off-by-one allowance here, unlike XCH - and the
                // minimum size is 96 bytes.
                uint w2 = 0;
                for (; syncPos >= lastPos; syncPos--)
                {
                    uint w1 = ReadBe32(gb.Buffer, syncPos * 4);
                    if (w1 == DcaSyncwords.X96)
                    {
                        int size = (int)(w2 >> 20) + 1;
                        int dist = FrameSize - syncPos * 4;
                        if (size >= 96 && size == dist)
                        {
                            x96Pos = syncPos * 32 + 44;
                            break;
                        }
                    }
                    w2 = w1;
                }

                if (x96Pos == 0)
                    LastError = "X96 sync word not found";
                break;
            }

            case DcaCoreExtAudioType.Xxch:
            {
                // Skipped for the same reason as XCH when a layout was asked for.
                if (RequestChannelLayout != 0)
                    break;

                // The XXCH frame header CRC must be valid, and the minimum header is 11 bytes.
                // Unlike XCH there is no size-and-distance test to lean on, so the CRC is what
                // separates the real sync word from an alias.
                uint w2 = 0;
                for (; syncPos >= lastPos; syncPos--)
                {
                    uint w1 = ReadBe32(gb.Buffer, syncPos * 4);
                    if (w1 == DcaSyncwords.Xxch)
                    {
                        int size = (int)(w2 >> 26) + 1;
                        int dist = gb.SizeInBits / 8 - syncPos * 4;
                        if (size >= 11 && size <= dist
                            && Crc16Ccitt(gb.Buffer, (syncPos + 1) * 4, size - 4) == 0)
                        {
                            xxchPos = syncPos * 32;
                            break;
                        }
                    }
                    w2 = w1;
                }

                if (xxchPos == 0)
                    LastError = "XXCH sync word not found";
                break;
            }
        }
    }

    /// <summary>
    /// <c>av_crc</c> over <c>AV_CRC_16_CCITT</c> with the 0xffff start value DTS uses:
    /// polynomial 0x1021, most significant bit first, no final inversion.
    /// </summary>
    /// <remarks>
    /// Computed bit by bit rather than from a 256-entry table. This runs once per frame, over
    /// an XXCH header of a dozen bytes or so, while a table would be 512 bytes of cache for a
    /// loop that touches it eleven times.
    /// </remarks>
    private static int Crc16Ccitt(byte[] data, int at, int length)
    {
        int crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[at + i] << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            crc &= 0xFFFF;
        }
        return crc;
    }

    /// <summary>
    /// <c>parse_xxch_frame</c> - the XXCH frame header, then one channel set.
    /// </summary>
    private bool ParseXxchFrame()
    {
        int headerPos = gb.GetBitsCount();

        // XXCH sync word
        if (gb.GetBits(32) != DcaSyncwords.Xxch)
        {
            LastError = "invalid XXCH sync word";
            return false;
        }

        // XXCH frame header length
        int headerSize = (int)gb.GetBits(6) + 1;

        if (!CheckCrc(headerPos + 32, headerPos + headerSize * 8))
        {
            LastError = "invalid XXCH frame header checksum";
            return false;
        }

        // CRC presence flag for channel set header
        xxchCrcPresent = (int)gb.GetBits1();

        // Number of bits for loudspeaker mask
        xxchMaskNbits = (int)gb.GetBits(5) + 1;
        if (xxchMaskNbits <= (int)DcaSpeaker.Cs)
        {
            LastError = $"invalid number of bits for XXCH speaker mask ({xxchMaskNbits})";
            return false;
        }

        // Number of channel sets
        int nchsets = (int)gb.GetBits(2) + 1;
        if (nchsets > 1)
        {
            LastError = $"{nchsets} XXCH channel sets is more than this decoder handles";
            return false;
        }

        // Channel set 0 data byte size
        int xxchFrameSize = (int)gb.GetBits(14) + 1;

        // Core loudspeaker activity mask
        xxchCoreMask = (int)gb.GetBits(xxchMaskNbits);

        // Validate the core mask. A 7.1 stream places its surrounds at Lss/Rss rather than
        // Ls/Rs, so the core's own mask has to be rewritten before the two can be compared.
        int mask = ChMask;

        if ((mask & DcaSpeakerMask.Ls) != 0 && (xxchCoreMask & DcaSpeakerMask.Lss) != 0)
            mask = (mask & ~DcaSpeakerMask.Ls) | DcaSpeakerMask.Lss;

        if ((mask & DcaSpeakerMask.Rs) != 0 && (xxchCoreMask & DcaSpeakerMask.Rss) != 0)
            mask = (mask & ~DcaSpeakerMask.Rs) | DcaSpeakerMask.Rss;

        if (mask != xxchCoreMask)
        {
            LastError = $"XXCH core speaker activity mask (0x{xxchCoreMask:X}) disagrees with "
                      + $"core (0x{mask:X})";
            return false;
        }

        // Reserved, byte align, CRC16 of the XXCH frame header.
        if (!SeekBits(headerPos + headerSize * 8))
        {
            LastError = "read past end of XXCH frame header";
            return false;
        }

        // Parse XXCH channel set 0
        if (!ParseFrameData(DcaHeaderType.Xxch, nchannels))
            return false;

        if (!SeekBits(headerPos + headerSize * 8 + xxchFrameSize * 8))
        {
            LastError = "read past end of XXCH channel set";
            return false;
        }

        return true;
    }

    private static uint ReadBe32(byte[] data, int at)
        => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);

    /// <summary>
    /// <c>parse_xch_frame</c> - the XCH channel set, which adds the rear centre speaker.
    /// </summary>
    private bool ParseXchFrame()
    {
        if ((ChMask & DcaSpeakerMask.Cs) != 0)
        {
            LastError = "XCH with Cs speaker already present";
            return false;
        }

        if (!ParseFrameData(DcaHeaderType.Xch, nchannels))
            return false;

        // Seek to the end of the core frame; the XCH frame size is not trusted.
        if (!SeekBits(FrameSize * 8))
        {
            LastError = "read past end of XCH frame";
            return false;
        }

        return true;
    }


    /// <summary>
    /// <c>parse_xbr_subframe</c> - one subframe of the XBR extension, added on top of the
    /// subband samples the core already produced.
    /// </summary>
    /// <remarks>
    /// XBR does not replace the core's samples; it refines them. Every band it carries is
    /// dequantized and ADDED to what the core put in the same place, which is why this runs
    /// after the core's own parse and writes through the same subband buffer.
    /// </remarks>
    private bool ParseXbrSubframe(int xbrBaseCh, int xbrNchannels,
                                  ReadOnlySpan<int> xbrNsubbands, int xbrTransitionMode,
                                  int sf, ref int subPos)
    {
        Span<int> xbrNabits = stackalloc int[DcaLimits.Channels];
        Span<int> xbrScaleNbits = stackalloc int[DcaLimits.Channels];

        // Check the number of subband samples in this subframe
        if (subPos + nsubsubframes[sf] * DcaLimits.SubbandSamples > Npcmblocks)
        {
            LastError = "XBR subband sample buffer overflow";
            return false;
        }

        if (gb.GetBitsLeft() < 0)
        {
            LastError = "ran out of bits in an XBR subframe";
            return false;
        }

        // Number of bits for the XBR bit allocation index
        for (int ch = xbrBaseCh; ch < xbrNchannels; ch++)
            xbrNabits[ch] = (int)gb.GetBits(2) + 2;

        // XBR bit allocation index
        for (int ch = xbrBaseCh; ch < xbrNchannels; ch++)
        {
            for (int band = 0; band < xbrNsubbands[ch]; band++)
            {
                int abits = (int)gb.GetBits(xbrNabits[ch]);
                if (abits > DcaLimits.AbitsMax)
                {
                    LastError = $"invalid XBR bit allocation index ({abits})";
                    return false;
                }
                xbrBitAllocation[ch * DcaLimits.Subbands + band] = (sbyte)abits;
            }
        }

        // Number of bits for scale indices
        for (int ch = xbrBaseCh; ch < xbrNchannels; ch++)
        {
            xbrScaleNbits[ch] = (int)gb.GetBits(3);
            if (xbrScaleNbits[ch] == 0)
            {
                LastError = "invalid number of bits for an XBR scale factor index";
                return false;
            }
        }

        // XBR scale factors
        for (int ch = xbrBaseCh; ch < xbrNchannels; ch++)
        {
            ReadOnlySpan<uint> scaleTable = scaleFactorSel[ch] > 5
                ? DcaData.ScaleFactorQuant7
                : DcaData.ScaleFactorQuant6;

            for (int band = 0; band < xbrNsubbands[ch]; band++)
            {
                if (xbrBitAllocation[ch * DcaLimits.Subbands + band] == 0)
                    continue;

                int at = (ch * DcaLimits.Subbands + band) * 2;

                int scaleIndex = (int)gb.GetBits(xbrScaleNbits[ch]);
                if (scaleIndex >= scaleTable.Length)
                {
                    LastError = $"invalid XBR scale factor index ({scaleIndex})";
                    return false;
                }
                xbrScaleFactors[at] = (int)scaleTable[scaleIndex];

                if (xbrTransitionMode != 0
                    && transitionMode[(sf * DcaLimits.Channels + ch) * DcaLimits.Subbands + band] != 0)
                {
                    scaleIndex = (int)gb.GetBits(xbrScaleNbits[ch]);
                    if (scaleIndex >= scaleTable.Length)
                    {
                        LastError = $"invalid XBR scale factor index ({scaleIndex})";
                        return false;
                    }
                    xbrScaleFactors[at + 1] = (int)scaleTable[scaleIndex];
                }
            }
        }

        // Audio data
        Span<int> audio = stackalloc int[DcaLimits.SubbandSamples];
        int ofs = subPos;
        for (int ssf = 0; ssf < nsubsubframes[sf]; ssf++)
        {
            for (int ch = xbrBaseCh; ch < xbrNchannels; ch++)
            {
                if (gb.GetBitsLeft() < 0)
                {
                    LastError = "ran out of bits in XBR audio data";
                    return false;
                }

                for (int band = 0; band < xbrNsubbands[ch]; band++)
                {
                    int abits = xbrBitAllocation[ch * DcaLimits.Subbands + band];

                    // Extract bits from the bit stream
                    if (abits > 7)
                    {
                        // No further encoding
                        GetArray(audio, DcaLimits.SubbandSamples, abits - 3);
                    }
                    else if (abits > 0)
                    {
                        // Block codes
                        if (!ParseBlockCodes(audio, abits))
                            return false;
                    }
                    else
                    {
                        // No bits allocated
                        continue;
                    }

                    // Look up the quantization step size
                    int stepSize = (int)DcaData.LosslessQuant[abits];

                    // Identify the transient location
                    int transSsf = xbrTransitionMode != 0
                        ? transitionMode[(sf * DcaLimits.Channels + ch) * DcaLimits.Subbands + band]
                        : 0;

                    // Determine the proper scale factor
                    int at = (ch * DcaLimits.Subbands + band) * 2;
                    int scale = transSsf == 0 || ssf < transSsf
                        ? xbrScaleFactors[at]
                        : xbrScaleFactors[at + 1];

                    // residual: true, so this ADDS to the core's samples rather than
                    // overwriting them. That one argument is the whole of what XBR is.
                    DcaDsp.Dequantize(subbandBuffer,
                                      subbandOffset[ch * DcaLimits.Subbands + band] + ofs,
                                      audio, stepSize, scale, true, DcaLimits.SubbandSamples);
                }
            }

            // DSYNC
            if ((ssf == nsubsubframes[sf] - 1 || SyncSsf != 0) && gb.GetBits(16) != 0xffff)
            {
                LastError = "XBR-DSYNC check failed";
                return false;
            }

            ofs += DcaLimits.SubbandSamples;
        }

        // Advance the subband sample pointer for the next subframe
        subPos = ofs;
        return true;
    }

    /// <summary>
    /// <c>parse_xbr_frame</c> - the XBR frame header, then each channel set's data.
    /// </summary>
    private bool ParseXbrFrame()
    {
        Span<int> xbrFrameSize = stackalloc int[DcaLimits.ExssChsetsMax];
        Span<int> xbrNchannels = stackalloc int[DcaLimits.ExssChsetsMax];
        Span<int> xbrNsubbands =
            stackalloc int[DcaLimits.ExssChsetsMax * DcaLimits.ExssChannelsMax];

        int headerPos = gb.GetBitsCount();

        // XBR sync word
        if (gb.GetBits(32) != DcaSyncwords.Xbr)
        {
            LastError = "invalid XBR sync word";
            return false;
        }

        // XBR frame header length
        int headerSize = (int)gb.GetBits(6) + 1;

        if (!CheckCrc(headerPos + 32, headerPos + headerSize * 8))
        {
            LastError = "invalid XBR frame header checksum";
            return false;
        }

        // Number of channel sets
        int xbrNchsets = (int)gb.GetBits(2) + 1;

        // Channel set data byte size
        for (int i = 0; i < xbrNchsets; i++)
            xbrFrameSize[i] = (int)gb.GetBits(14) + 1;

        // Transition mode flag
        int xbrTransitionMode = (int)gb.GetBits1();

        // Channel set headers
        for (int i = 0, ch2 = 0; i < xbrNchsets; i++)
        {
            xbrNchannels[i] = (int)gb.GetBits(3) + 1;
            int xbrBandNbits = (int)gb.GetBits(2) + 5;
            for (int ch1 = 0; ch1 < xbrNchannels[i]; ch1++, ch2++)
            {
                xbrNsubbands[ch2] = (int)gb.GetBits(xbrBandNbits) + 1;
                if (xbrNsubbands[ch2] > DcaLimits.Subbands)
                {
                    LastError = $"invalid number of active XBR subbands ({xbrNsubbands[ch2]})";
                    return false;
                }
            }
        }

        // Reserved, byte align, CRC16 of the XBR frame header.
        if (!SeekBits(headerPos + headerSize * 8))
        {
            LastError = "read past end of XBR frame header";
            return false;
        }

        // Channel set data
        for (int i = 0, xbrBaseCh = 0; i < xbrNchsets; i++)
        {
            headerPos = gb.GetBitsCount();

            // A channel set for channels the core did not send is skipped, not an error: the
            // encoder may have written XBR for more channels than this core frame carries.
            if (xbrBaseCh + xbrNchannels[i] <= nchannels)
            {
                int subPos = 0;
                for (int sf = 0; sf < nsubframes; sf++)
                {
                    if (!ParseXbrSubframe(xbrBaseCh, xbrBaseCh + xbrNchannels[i],
                                          xbrNsubbands, xbrTransitionMode, sf, ref subPos))
                        return false;
                }
            }

            xbrBaseCh += xbrNchannels[i];

            if (!SeekBits(headerPos + xbrFrameSize[i] * 8))
            {
                LastError = "read past end of XBR channel set";
                return false;
            }
        }

        return true;
    }


    /// <summary>
    /// <c>rand_x96</c> - a modified ISO/IEC 9899 linear congruential generator, returning a
    /// value in [-2^30, 2^30 - 1].
    /// </summary>
    /// <remarks>
    /// Noise fill for a band X96 allocated no bits to but gave a scale factor above 1. The exact
    /// sequence matters: FFmpeg's reference output contains this generator's values, so any
    /// other generator - however well distributed - decodes to a different file.
    /// </remarks>
    private int RandX96()
    {
        x96Rand = 1103515245u * x96Rand + 12345u;
        return (int)(x96Rand & 0x7fffffff) - 0x40000000;
    }

    /// <summary><c>parse_x96_subframe_audio</c>.</summary>
    private bool ParseX96SubframeAudio(int sf, int xchBase, ref int subPos)
    {
        int nsamples = nsubsubframes[sf] * DcaLimits.SubbandSamples;
        if (subPos + nsamples > Npcmblocks)
        {
            LastError = "X96 subband sample buffer overflow";
            return false;
        }

        if (gb.GetBitsLeft() < 0)
        {
            LastError = "ran out of bits in an X96 subframe";
            return false;
        }

        // VQ encoded or unallocated subbands
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
            {
                int at = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band] + subPos;
                int scale = scaleFactors[(ch * DcaLimits.Subbands + (band >> 1)) * 2 + (band & 1)];

                switch (bitAllocation[ch * DcaLimits.SubbandsX96 + band])
                {
                    case 0: // No bits allocated for the subband
                        if (scale <= 1)
                        {
                            Array.Clear(x96SubbandBuffer, at, nsamples);
                        }
                        else
                        {
                            for (int n = 0; n < nsamples; n++)
                                x96SubbandBuffer[at + n] = DcaMath.Mul31(RandX96(), scale);
                        }
                        break;

                    case 1: // VQ encoded subband
                        int outAt = at;
                        for (int ssf = 0; ssf < (nsubsubframes[sf] + 1) / 2; ssf++)
                        {
                            int vqAt = (int)gb.GetBits(10) * DcaData.HighFreqVqStride;
                            int count = Math.Min(nsamples - ssf * 16, 16);
                            for (int n = 0; n < count; n++)
                                x96SubbandBuffer[outAt++] = DcaMath.Clip23(
                                    (DcaData.HighFreqVq[vqAt + n] * scale + (1 << 3)) >> 4);
                        }
                        break;
                }
            }
        }

        // Audio data
        Span<int> audio = stackalloc int[DcaLimits.SubbandSamples];
        int ofs = subPos;
        for (int ssf = 0; ssf < nsubsubframes[sf]; ssf++)
        {
            for (int ch = xchBase; ch < x96NchannelsField; ch++)
            {
                if (gb.GetBitsLeft() < 0)
                {
                    LastError = "ran out of bits in X96 audio data";
                    return false;
                }

                for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
                {
                    int abits = bitAllocation[ch * DcaLimits.SubbandsX96 + band] - 1;

                    // Not VQ encoded, and not unallocated
                    if (abits < 1)
                        continue;

                    if (ExtractAudio(audio, abits, ch) < 0)
                        return false;

                    int stepSize = BitRate == 3
                        ? (int)DcaData.LosslessQuant[abits]
                        : (int)DcaData.LossyQuant[abits];

                    int scale = scaleFactors[(ch * DcaLimits.Subbands + (band >> 1)) * 2
                                             + (band & 1)];

                    DcaDsp.Dequantize(x96SubbandBuffer,
                                      x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band] + ofs,
                                      audio, stepSize, scale, false, DcaLimits.SubbandSamples);
                }
            }

            // DSYNC
            if ((ssf == nsubsubframes[sf] - 1 || SyncSsf != 0) && gb.GetBits(16) != 0xffff)
            {
                LastError = "X96-DSYNC check failed";
                return false;
            }

            ofs += DcaLimits.SubbandSamples;
        }

        // Inverse ADPCM
        Span<int> bandOffset = stackalloc int[DcaLimits.SubbandsX96];
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            for (int band = 0; band < DcaLimits.SubbandsX96; band++)
                bandOffset[band] = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band];

            DcaDsp.InverseAdpcm(x96SubbandBuffer, bandOffset,
                                predictionVqIndex.AsSpan(ch * DcaLimits.SubbandsX96),
                                predictionMode.AsSpan(ch * DcaLimits.SubbandsX96),
                                x96SubbandStart, nsubbands[ch], subPos, nsamples);
        }

        // Joint subband coding
        Span<int> srcOffset = stackalloc int[DcaLimits.SubbandsX96];
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int srcCh = jointIntensityIndex[ch] - 1;
            if (srcCh < 0)
                continue;

            for (int band = 0; band < DcaLimits.SubbandsX96; band++)
            {
                bandOffset[band] = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band];
                srcOffset[band] = x96SubbandOffset[srcCh * DcaLimits.SubbandsX96 + band];
            }

            DcaDsp.DecodeJoint(x96SubbandBuffer, bandOffset, srcOffset,
                               jointScaleFactors.AsSpan(ch * DcaLimits.SubbandsX96),
                               nsubbands[ch], nsubbands[srcCh], subPos, nsamples);
        }

        subPos = ofs;
        return true;
    }

    /// <summary><c>erase_x96_adpcm_history</c>.</summary>
    private void EraseX96AdpcmHistory()
    {
        for (int ch = 0; ch < DcaLimits.Channels; ch++)
            for (int band = 0; band < DcaLimits.SubbandsX96; band++)
                Array.Clear(x96SubbandBuffer,
                            x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band] - AdpcmCoeffs,
                            AdpcmCoeffs);
    }

    /// <summary><c>alloc_x96_sample_buffer</c>.</summary>
    private void AllocX96SampleBuffer()
    {
        int nchsamples = AdpcmCoeffs + Npcmblocks;
        int nframesamples = nchsamples * DcaLimits.Channels * DcaLimits.SubbandsX96;

        if (x96SubbandBuffer.Length < nframesamples || x96AllocatedNpcmblocks != Npcmblocks)
        {
            if (x96SubbandBuffer.Length < nframesamples)
                x96SubbandBuffer = new int[nframesamples];
            else
                Array.Clear(x96SubbandBuffer);

            for (int ch = 0; ch < DcaLimits.Channels; ch++)
                for (int band = 0; band < DcaLimits.SubbandsX96; band++)
                    x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band] =
                        (ch * DcaLimits.SubbandsX96 + band) * nchsamples + AdpcmCoeffs;

            x96AllocatedNpcmblocks = Npcmblocks;
        }

        if (PredictorHistory == 0)
            EraseX96AdpcmHistory();
    }

    /// <summary><c>parse_x96_subframe_header</c>.</summary>
    private bool ParseX96SubframeHeader(int xchBase)
    {
        if (gb.GetBitsLeft() < 0)
        {
            LastError = "ran out of bits before an X96 subframe header";
            return false;
        }

        // Prediction mode
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
            for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
                predictionMode[ch * DcaLimits.SubbandsX96 + band] = (sbyte)gb.GetBits1();

        // Prediction coefficients VQ address
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
            for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
                if (predictionMode[ch * DcaLimits.SubbandsX96 + band] != 0)
                    predictionVqIndex[ch * DcaLimits.SubbandsX96 + band] = (short)gb.GetBits(12);

        // Bit allocation index
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int sel = bitAllocationSel[ch];
            int abits = 0;

            for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
            {
                // If a Huffman code was used, the DIFFERENCE of abits was encoded.
                if (sel < 7)
                    abits += DcaGetVlc(DcaHuff.QuantIndex[5 + 2 * x96HighRes][sel],
                                       DcaHuff.QuantIndexBits[5 + 2 * x96HighRes][sel]);
                else
                    abits = (int)gb.GetBits(3 + x96HighRes);

                if (abits < 0 || abits > 7 + 8 * x96HighRes)
                {
                    LastError = $"invalid X96 bit allocation index ({abits})";
                    return false;
                }

                bitAllocation[ch * DcaLimits.SubbandsX96 + band] = (sbyte)abits;
            }
        }

        // Scale factors
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int sel = scaleFactorSel[ch];
            int scaleIndex = 0;

            for (int band = x96SubbandStart; band < nsubbands[ch]; band++)
            {
                int scale = ParseScale(ref scaleIndex, sel);
                if (scale < 0)
                    return false;
                scaleFactors[(ch * DcaLimits.Subbands + (band >> 1)) * 2 + (band & 1)] = scale;
            }
        }

        // Joint subband codebook select
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            if (jointIntensityIndex[ch] != 0)
            {
                jointScaleSel[ch] = (sbyte)gb.GetBits(3);
                if (jointScaleSel[ch] == 7)
                {
                    LastError = "invalid X96 joint scale factor code book";
                    return false;
                }
            }
        }

        // Scale factors for joint subband coding
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int srcCh = jointIntensityIndex[ch] - 1;
            if (srcCh < 0)
                continue;

            int sel = jointScaleSel[ch];
            for (int band = nsubbands[ch]; band < nsubbands[srcCh]; band++)
            {
                int scale = ParseJointScale(sel);
                if (scale < 0)
                    return false;
                jointScaleFactors[ch * DcaLimits.SubbandsX96 + band] = scale;
            }
        }

        // Side information CRC check word
        if (CrcPresent != 0)
            gb.SkipBits(16);

        return true;
    }

    /// <summary><c>parse_x96_coding_header</c>.</summary>
    private bool ParseX96CodingHeader(bool exss, int xchBase)
    {
        int headerSize = 0;
        int headerPos = gb.GetBitsCount();

        if (gb.GetBitsLeft() < 0)
        {
            LastError = "ran out of bits before the X96 coding header";
            return false;
        }

        if (exss)
        {
            // Channel set header length
            headerSize = (int)gb.GetBits(7) + 1;

            if (x96CrcPresent != 0 && !CheckCrc(headerPos, headerPos + headerSize * 8))
            {
                LastError = "invalid X96 channel set header checksum";
                return false;
            }
        }

        // High resolution flag
        x96HighRes = (int)gb.GetBits1();

        // First encoded subband. Revision 8 and above code no low bands at all, so the
        // extension starts where the core's 32 bands end and nothing is residual coded.
        if (x96RevNo < 8)
        {
            x96SubbandStart = (int)gb.GetBits(5);
            if (x96SubbandStart > 27)
            {
                LastError = $"invalid X96 subband start index ({x96SubbandStart})";
                return false;
            }
        }
        else
        {
            x96SubbandStart = DcaLimits.Subbands;
        }

        // Subband activity count
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            nsubbands[ch] = (sbyte)((int)gb.GetBits(6) + 1);
            if (nsubbands[ch] < DcaLimits.Subbands)
            {
                LastError = $"invalid X96 subband activity count ({nsubbands[ch]})";
                return false;
            }
        }

        // Joint intensity coding index
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int n = (int)gb.GetBits(3);
            if (n != 0 && xchBase != 0)
                n += xchBase - 1;
            if (n > x96NchannelsField)
            {
                LastError = $"invalid X96 joint intensity coding index ({n})";
                return false;
            }
            jointIntensityIndex[ch] = (sbyte)n;
        }

        // Scale factor code book
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            scaleFactorSel[ch] = (sbyte)gb.GetBits(3);
            if (scaleFactorSel[ch] >= 6)
            {
                LastError = "invalid X96 scale factor code book";
                return false;
            }
        }

        // Bit allocation quantizer select
        for (int ch = xchBase; ch < x96NchannelsField; ch++)
            bitAllocationSel[ch] = (sbyte)gb.GetBits(3);

        // Quantization index codebook select
        for (int n = 0; n < 6 + 4 * x96HighRes; n++)
            for (int ch = xchBase; ch < x96NchannelsField; ch++)
                quantIndexSel[ch * DcaLimits.CodeBooks + n] =
                    (sbyte)gb.GetBits(DcaData.QuantIndexSelNbits[n]);

        if (exss)
        {
            // Reserved, byte align, CRC16 of the channel set header.
            if (!SeekBits(headerPos + headerSize * 8))
            {
                LastError = "read past end of X96 channel set header";
                return false;
            }
        }
        else
        {
            if (CrcPresent != 0)
                gb.SkipBits(16);
        }

        return true;
    }

    /// <summary><c>parse_x96_frame_data</c>.</summary>
    private bool ParseX96FrameData(bool exss, int xchBase)
    {
        if (!ParseX96CodingHeader(exss, xchBase))
            return false;

        int subPos = 0;
        for (int sf = 0; sf < nsubframes; sf++)
        {
            if (!ParseX96SubframeHeader(xchBase))
                return false;
            if (!ParseX96SubframeAudio(sf, xchBase, ref subPos))
                return false;
        }

        for (int ch = xchBase; ch < x96NchannelsField; ch++)
        {
            int active = nsubbands[ch];
            if (jointIntensityIndex[ch] != 0)
                active = Math.Max(active, nsubbands[jointIntensityIndex[ch] - 1]);

            // Update the ADPCM history, and clear the inactive subbands.
            for (int band = 0; band < DcaLimits.SubbandsX96; band++)
            {
                int at = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band] - AdpcmCoeffs;
                if (band >= x96SubbandStart && band < active)
                    Array.Copy(x96SubbandBuffer, at + Npcmblocks, x96SubbandBuffer, at,
                               AdpcmCoeffs);
                else
                    Array.Clear(x96SubbandBuffer, at, AdpcmCoeffs + Npcmblocks);
            }
        }

        return true;
    }

    /// <summary><c>parse_x96_frame</c> - X96 carried inside the core frame.</summary>
    private bool ParseX96Frame()
    {
        // Revision number
        x96RevNo = (int)gb.GetBits(4);
        if (x96RevNo < 1 || x96RevNo > 8)
        {
            LastError = $"invalid X96 revision ({x96RevNo})";
            return false;
        }

        x96CrcPresent = 0;
        x96NchannelsField = nchannels;

        AllocX96SampleBuffer();

        if (!ParseX96FrameData(false, 0))
            return false;

        // Seek to the end of the core frame.
        if (!SeekBits(FrameSize * 8))
        {
            LastError = "read past end of X96 frame";
            return false;
        }

        return true;
    }

    /// <summary><c>parse_x96_frame_exss</c> - X96 carried in an extension substream.</summary>
    private bool ParseX96FrameExss()
    {
        Span<int> x96FrameSize = stackalloc int[DcaLimits.ExssChsetsMax];
        Span<int> x96Nchannels = stackalloc int[DcaLimits.ExssChsetsMax];

        int headerPos = gb.GetBitsCount();

        // X96 sync word
        if (gb.GetBits(32) != DcaSyncwords.X96)
        {
            LastError = "invalid X96 sync word";
            return false;
        }

        // X96 frame header length
        int headerSize = (int)gb.GetBits(6) + 1;

        if (!CheckCrc(headerPos + 32, headerPos + headerSize * 8))
        {
            LastError = "invalid X96 frame header checksum";
            return false;
        }

        // Revision number
        x96RevNo = (int)gb.GetBits(4);
        if (x96RevNo < 1 || x96RevNo > 8)
        {
            LastError = $"invalid X96 revision ({x96RevNo})";
            return false;
        }

        // CRC presence flag for the channel set header
        x96CrcPresent = (int)gb.GetBits1();

        // Number of channel sets
        int x96Nchsets = (int)gb.GetBits(2) + 1;

        // Channel set data byte size
        for (int i = 0; i < x96Nchsets; i++)
            x96FrameSize[i] = (int)gb.GetBits(12) + 1;

        // Number of channels in the channel set
        for (int i = 0; i < x96Nchsets; i++)
            x96Nchannels[i] = (int)gb.GetBits(3) + 1;

        // Reserved, byte align, CRC16 of the X96 frame header.
        if (!SeekBits(headerPos + headerSize * 8))
        {
            LastError = "read past end of X96 frame header";
            return false;
        }

        AllocX96SampleBuffer();

        // Channel set data
        x96NchannelsField = 0;
        for (int i = 0, x96BaseCh = 0; i < x96Nchsets; i++)
        {
            headerPos = gb.GetBitsCount();

            if (x96BaseCh + x96Nchannels[i] <= nchannels)
            {
                x96NchannelsField = x96BaseCh + x96Nchannels[i];
                if (!ParseX96FrameData(true, x96BaseCh))
                    return false;
            }

            x96BaseCh += x96Nchannels[i];

            if (!SeekBits(headerPos + x96FrameSize[i] * 8))
            {
                LastError = "read past end of X96 channel set";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>ff_dca_core_parse_exss</c> - the core extensions, from the extension substream or
    /// from the core frame itself.
    /// </summary>
    /// <remarks>
    /// A failed extension is not a failed frame. The C reverts to the primary channel set and
    /// carries on, because the core samples are already decoded and correct - dropping them
    /// over a broken extension would turn a quality loss into silence.
    /// <para>
    /// <paramref name="asset"/> with an <c>ExtensionMask</c> of zero stands for the C's NULL
    /// asset: a frame with no extension substream, whose extensions can only be the ones found
    /// inside the core frame itself.
    /// </para>
    /// </remarks>
    public void ParseCoreExtensions(byte[] data, int exssOffset, in DcaExssAsset asset)
    {
        // Parse (X)XCH. The C runs this block unconditionally and leaves ret at 0 when the
        // frame has no channel extension, so the XBR pass below still happens - returning
        // early here cost xbr_51_24_48_3840 its match while the two XBR samples that happen
        // to carry XCH or XXCH as well passed.
        bool ok = true;
        int ext = 0;

        // "Parse (X)XCH unless downmixing" - the C's own comment. A caller that asked for a
        // layout gets the primary channel set, extension or not.
        if (RequestChannelLayout != 0)
        {
            // Nothing to parse and nothing to revert: fall through to XBR and X96, which the C
            // reaches the same way.
        }
        else if ((asset.ExtensionMask & DcaExtensionMask.ExssXxch) != 0)
        {
            gb = default;
            gb.InitGetBits8(data, exssOffset + asset.XxchOffset, asset.XxchSize);
            ok = ParseXxchFrame();
            ext = DcaExtensionMask.ExssXxch;
        }
        else if (xxchPos != 0)
        {
            gb = gbIn;
            gb.SkipBits(xxchPos);
            ok = ParseXxchFrame();
            ext = DcaExtensionMask.CssXxch;
        }
        else if (xchPos != 0)
        {
            gb = gbIn;
            gb.SkipBits(xchPos);
            ok = ParseXchFrame();
            ext = DcaExtensionMask.CssXch;
        }

        if (ok)
        {
            ExtAudioMask |= ext;
        }
        else
        {
            // Revert to the primary channel set.
            nchannels = DcaData.Channels[AudioMode];
            ChMask = AudioModeChMask[AudioMode];
            if (LfePresent != 0)
                ChMask |= DcaSpeakerMask.Lfe1;
        }

        ParseXbr(data, exssOffset, asset);
        ParseX96(data, exssOffset, asset);
    }

    /// <summary>
    /// The X96 half of <c>ff_dca_core_parse_exss</c>: from the extension substream when an
    /// asset carries it, otherwise from the sync word found inside the core frame.
    /// </summary>
    /// <remarks>
    /// The C skips this entirely while decoding XLL, because XLL supersedes it. There is no XLL
    /// decoder here, so that condition cannot arise.
    /// </remarks>
    private void ParseX96(byte[] data, int exssOffset, in DcaExssAsset asset)
    {
        if ((asset.ExtensionMask & DcaExtensionMask.ExssX96) != 0)
        {
            gb = default;
            gb.InitGetBits8(data, exssOffset + asset.X96Offset, asset.X96Size);
            if (ParseX96FrameExss())
                ExtAudioMask |= DcaExtensionMask.ExssX96;
        }
        else if (x96Pos != 0)
        {
            gb = gbIn;
            gb.SkipBits(x96Pos);
            if (ParseX96Frame())
                ExtAudioMask |= DcaExtensionMask.CssX96;
        }
    }

    /// <summary>
    /// The XBR half of <c>ff_dca_core_parse_exss</c>. XBR only ever arrives in an extension
    /// substream - there is no core-frame form of it - so there is no sync search here.
    /// </summary>
    private void ParseXbr(byte[] data, int exssOffset, in DcaExssAsset asset)
    {
        if ((asset.ExtensionMask & DcaExtensionMask.ExssXbr) == 0)
            return;

        gb = default;
        gb.InitGetBits8(data, exssOffset + asset.XbrOffset, asset.XbrSize);

        if (ParseXbrFrame())
            ExtAudioMask |= DcaExtensionMask.ExssXbr;
    }

    /// <summary>
    /// <c>ff_dca_core_parse</c> - parse one core substream frame into subband samples.
    /// </summary>
    public bool Parse(byte[] data, int size)
    {
        LastError = "";
        ExtAudioMask = 0;
        xchPos = xxchPos = x96Pos = 0;

        gb = default;
        gb.InitGetBits8(data, size);
        gbIn = gb;

        if (!ParseFrameHeader())
            return false;

        AllocSampleBuffer();

        if (!ParseFrameData(DcaHeaderType.Core, 0))
            return false;

        ParseOptionalInfo();

        // Workaround for DTS in WAV
        if (FrameSize > size)
            FrameSize = size;

        if (!SeekBits(FrameSize * 8))
        {
            // The C logs this and carries on unless AV_EF_EXPLODE is set. The samples are
            // already decoded at this point, so carrying on is right.
            LastError = "read past end of core frame";
        }

        return true;
    }

    /// <summary><c>map_prm_ch_to_spkr</c>, core path only.</summary>
    private int MapPrmChToSpkr(int ch)
    {
        // Try to map this channel to the core first.
        int pos = DcaData.Channels[AudioMode];
        if (ch < pos)
        {
            int coreSpkr = PrmChToSpkrMap[AudioMode * 5 + ch];
            if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.ExssXxch)) == 0)
                return coreSpkr;

            // With XXCH present the core's own surrounds may have moved to the side positions,
            // which is what the mask rewriting in ParseXxchFrame established.
            if ((xxchCoreMask & (1 << coreSpkr)) != 0)
                return coreSpkr;
            if (coreSpkr == (int)DcaSpeaker.Ls && (xxchCoreMask & DcaSpeakerMask.Lss) != 0)
                return (int)DcaSpeaker.Lss;
            if (coreSpkr == (int)DcaSpeaker.Rs && (xxchCoreMask & DcaSpeakerMask.Rss) != 0)
                return (int)DcaSpeaker.Rss;
            return -1;
        }

        // Then XCH, which contributes exactly one channel immediately after the core's.
        if ((ExtAudioMask & DcaExtensionMask.CssXch) != 0 && ch == pos)
            return (int)DcaSpeaker.Cs;

        // Then XXCH, whose channels are in speaker order from Cs upwards.
        if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.ExssXxch)) != 0)
        {
            for (int spkr = (int)DcaSpeaker.Cs; spkr < xxchMaskNbits; spkr++)
                if ((xxchSpkrMask & (1 << spkr)) != 0)
                    if (pos++ == ch)
                        return spkr;
        }

        // No mapping
        return -1;
    }

    /// <summary><c>set_filter_mode</c>.</summary>
    private void SetFilterMode(int mode)
    {
        if (filterMode != mode)
        {
            EraseDspHistory();
            filterMode = mode;
        }
    }

    /// <summary>
    /// <c>filter_frame_float</c> - subband samples to PCM, one plane per present speaker.
    /// </summary>
    public bool FilterFrame()
    {
        // X96 doubles the output rate, and every length below doubles with it.
        bool x96Synth = (ExtAudioMask
                         & (DcaExtensionMask.CssX96 | DcaExtensionMask.ExssX96)) != 0;
        int x96Nchannels = x96Synth ? x96NchannelsField : 0;

        OutputRate = SampleRate << (x96Synth ? 1 : 0);
        Npcmsamples = (Npcmblocks * DcaLimits.PcmBlockSamples) << (x96Synth ? 1 : 0);

        // ff_dca_core_filter_frame's downmix request handling, which has to run before the
        // channel layout is built because it is the layout.
        requestMask =
            RequestChannelLayout == DcaSpeakerLayout.Stereo
            && AudioMode > (int)DcaCoreAudioMode.Mono
            && PrimDmixEmbedded != 0
            && (PrimDmixType == (int)DcaDmixType.LoRo || PrimDmixType == (int)DcaDmixType.LtRt)
                ? DcaSpeakerLayout.Stereo
                : ChMask;

        // Build the speaker to channel mapping. CHANNEL_ORDER_CODED is not what FFmpeg defaults
        // to, so this is the WAV order its dca2wav_norm table produces.
        nchannelsOut = SetChannelLayout(requestMask);

        for (int spkr = 0; spkr < (int)DcaSpeaker.Count; spkr++)
        {
            if ((ChMask & (1 << spkr)) == 0)
            {
                outputSamples[spkr] = null;
                continue;
            }
            if (outputSamples[spkr] is null || outputSamples[spkr]!.Length < Npcmsamples)
                outputSamples[spkr] = new float[Npcmsamples];
        }

        // Handle a change of filtering mode. The QMF history is meaningless across a switch
        // between the 32-band and 64-band filter, so it is cleared.
        SetFilterMode(x96Synth ? FilterModeX96 : 0);

        // Select the filter
        ReadOnlySpan<float> filterCoeff = x96Synth
            ? DcaData.Fir64bands
            : FilterPerfect != 0
                ? DcaData.Fir32bandsPerfect
                : DcaData.Fir32bandsNonperfect;

        Span<int> bandOffset = stackalloc int[DcaLimits.Subbands];
        Span<int> x96BandOffset = stackalloc int[DcaLimits.SubbandsX96];

        // Filter the primary channels
        for (int ch = 0; ch < nchannels; ch++)
        {
            int spkr = MapPrmChToSpkr(ch);
            if (spkr < 0)
            {
                LastError = $"primary channel {ch} maps to no speaker in mask 0x{ChMask:X}";
                return false;
            }

            for (int band = 0; band < DcaLimits.Subbands; band++)
                bandOffset[band] = subbandOffset[ch * DcaLimits.Subbands + band];

            if (x96Synth)
            {
                for (int band = 0; band < DcaLimits.SubbandsX96; band++)
                    x96BandOffset[band] = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band];

                DcaDsp.SubQmf64Float(imdct64!, outputSamples[spkr].AsSpan(0, Npcmsamples),
                                     subbandBuffer, bandOffset,
                                     x96SubbandBuffer, x96BandOffset, ch < x96Nchannels,
                                     hist1[ch]!, ref histOffset[ch], hist2[ch]!,
                                     filterCoeff, Npcmblocks, 1.0f / (1 << 16));
            }
            else
            {
                DcaDsp.SubQmf32Float(imdct32!, outputSamples[spkr].AsSpan(0, Npcmsamples),
                                     subbandBuffer, bandOffset,
                                     hist1[ch]!, ref histOffset[ch], hist2[ch]!,
                                     filterCoeff, Npcmblocks, 1.0f / (1 << 17));
            }
        }

        // Filter the LFE channel
        if (LfePresent != 0)
        {
            int decSelect = LfePresent == DcaCoreLfeFlag.Lfe128 ? 1 : 0;
            float[] samples = outputSamples[(int)DcaSpeaker.Lfe1]!;
            int nlfesamples = Npcmblocks >> (decSelect + 1);

            // With X96 the first interpolation writes into the SECOND half of the plane and
            // LfeX96Float then expands it over the whole, so the two passes never overlap.
            int lfeAt = x96Synth ? Npcmsamples / 2 : 0;

            ReadOnlySpan<float> lfeCoeff = decSelect != 0 ? DcaData.LfeFir128 : DcaData.LfeFir64;

            DcaDsp.LfeFirFloat(samples.AsSpan(lfeAt, Npcmsamples - lfeAt), subbandBuffer,
                               lfeSamplesAt + DcaLimits.LfeHistory,
                               lfeCoeff, Npcmblocks, decSelect);

            if (x96Synth)
            {
                // Attenuate the 47.6-48.0 kHz image the interpolation above leaves behind.
                DcaDsp.LfeX96Float(samples.AsSpan(0, Npcmsamples),
                                   samples.AsSpan(lfeAt, Npcmsamples / 2),
                                   ref outputHistoryLfeFloat, Npcmsamples / 2);
            }

            // Update the LFE history, backwards so a short frame cannot overwrite what it is
            // about to read.
            for (int n = DcaLimits.LfeHistory - 1; n >= 0; n--)
                subbandBuffer[lfeSamplesAt + n] = subbandBuffer[lfeSamplesAt + nlfesamples + n];
        }

        // Undo the embedded XCH downmix. An ES-format core carries Ls and Rs with the rear
        // centre already mixed into them, so that a decoder without XCH still plays something
        // sensible; a decoder with XCH has to take it back out.
        if (EsFormat != 0 && (ExtAudioMask & DcaExtensionMask.CssXch) != 0
            && AudioMode >= (int)DcaCoreAudioMode.Mode2F2R)
        {
            const float invSqrt2 = 0.70710678118654752440f;
            VectorFmacScalar(outputSamples[(int)DcaSpeaker.Ls]!,
                             outputSamples[(int)DcaSpeaker.Cs]!, -invSqrt2, Npcmsamples);
            VectorFmacScalar(outputSamples[(int)DcaSpeaker.Rs]!,
                             outputSamples[(int)DcaSpeaker.Cs]!, -invSqrt2, Npcmsamples);
        }

        // Undo the embedded XXCH downmix, the same idea as XCH's but with coefficients the
        // stream carries rather than a fixed 1/sqrt(2), and a pre-scale applied to every core
        // channel so the sum could not clip before the extension was taken back out.
        if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.ExssXxch)) != 0
            && xxchDmixEmbedded != 0)
        {
            float scaleInv = xxchDmixScaleInv * (1.0f / (1 << 16));
            int coeffAt = 0;
            int xchBase = DcaData.Channels[AudioMode];

            for (int ch = xchBase; ch < nchannels; ch++)
            {
                int srcSpkr = MapPrmChToSpkr(ch);
                if (srcSpkr < 0)
                {
                    LastError = $"XXCH channel {ch} maps to no speaker";
                    return false;
                }

                for (int spkr = 0; spkr < xxchMaskNbits; spkr++)
                {
                    if ((xxchDmixMask[ch - xchBase] & (1 << spkr)) == 0)
                        continue;

                    int coeff = xxchDmixCoeff[coeffAt++];
                    if (coeff != 0)
                    {
                        VectorFmacScalar(outputSamples[spkr]!, outputSamples[srcSpkr]!,
                                         coeff * (-1.0f / (1 << 15)), Npcmsamples);
                    }
                }
            }

            // Undo the embedded core downmix pre-scaling.
            for (int spkr = 0; spkr < xxchMaskNbits; spkr++)
                if ((xxchCoreMask & (1 << spkr)) != 0)
                    VectorFmulScalar(outputSamples[spkr]!, scaleInv, Npcmsamples);
        }

        // Sum/difference decoding is skipped entirely when a channel extension is present: the
        // extension's channel set is coded discretely, so the pairs are no longer sum and
        // difference. Leaving this out cost rms 0.17 on Ls and Rs while every other channel,
        // the new Cs included, matched to 3e-7.
        if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.CssXch
                             | DcaExtensionMask.ExssXxch)) == 0)
        {
            // Front sum/difference decoding
            if ((SumdiffFront != 0 && AudioMode > (int)DcaCoreAudioMode.Mono)
                || AudioMode == (int)DcaCoreAudioMode.StereoSumDiff)
            {
                Butterflies(outputSamples[(int)DcaSpeaker.L]!, outputSamples[(int)DcaSpeaker.R]!,
                            Npcmsamples);
            }

            // Surround sum/difference decoding
            if (SumdiffSurround != 0 && AudioMode >= (int)DcaCoreAudioMode.Mode2F2R)
            {
                Butterflies(outputSamples[(int)DcaSpeaker.Ls]!, outputSamples[(int)DcaSpeaker.Rs]!,
                            Npcmsamples);
            }
        }

        // Downmix the primary channel set to stereo.
        if (requestMask != ChMask)
            DownmixToStereoFloat(primDmixCoeff, Npcmsamples, ChMask);

        return true;
    }

    /// <summary>
    /// <c>ff_dca_downmix_to_stereo_float</c> (dcadec.c) - fold every present speaker into L and R
    /// with the matrix the auxiliary data carried.
    /// </summary>
    /// <remarks>
    /// The coefficient layout is the subtle part. <paramref name="coeff"/> is one row per output
    /// channel, each row one entry per present speaker in speaker order, so the right channel's
    /// row begins <c>popcount(chMask)</c> entries in. L and R are first SCALED rather than
    /// accumulated, by the coefficient that would otherwise be applied to themselves - and the
    /// index of that coefficient is offset by one when a centre channel is present, because C
    /// sorts before L in speaker order. That is the whole of what <c>pos</c> does, and getting
    /// it wrong scales the pair by a surround channel's coefficient instead of its own.
    /// </remarks>
    private void DownmixToStereoFloat(int[] coeff, int nsamples, int chMask)
    {
        int maxSpkr = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)chMask);
        int coeffR = System.Numerics.BitOperations.PopCount((uint)chMask);
        const float scale = 1.0f / (1 << 15);

        if (!DcaSpeakerLayout.HasStereo(chMask))
        {
            LastError = $"a stereo downmix needs L and R present; channel mask {chMask:x}";
            return;
        }

        int pos = chMask & DcaSpeakerMask.C;

        VectorFmulScalar(outputSamples[(int)DcaSpeaker.L]!, coeff[pos] * scale, nsamples);
        VectorFmulScalar(outputSamples[(int)DcaSpeaker.R]!, coeff[coeffR + pos + 1] * scale,
                         nsamples);

        int l = 0, r = coeffR;
        for (int spkr = 0; spkr <= maxSpkr; spkr++)
        {
            if ((chMask & (1 << spkr)) == 0)
                continue;

            if (coeff[l] != 0 && spkr != (int)DcaSpeaker.L)
            {
                VectorFmacScalar(outputSamples[(int)DcaSpeaker.L]!, outputSamples[spkr]!,
                                 coeff[l] * scale, nsamples);
            }

            if (coeff[r] != 0 && spkr != (int)DcaSpeaker.R)
            {
                VectorFmacScalar(outputSamples[(int)DcaSpeaker.R]!, outputSamples[spkr]!,
                                 coeff[r] * scale, nsamples);
            }

            l++;
            r++;
        }
    }

    /// <summary>
    /// <c>ff_dca_core_filter_fixed</c> - subband samples to 24-bit integer PCM.
    /// </summary>
    /// <param name="x96Synth">
    /// Tri-state, exactly as the C's parameter is. A negative value means "no X96 synthesis at
    /// all"; zero means "decide from the extension mask"; a positive value means "synthesise at
    /// the doubled rate but discard any X96 subband data". That last case is not reachable from
    /// the lossy core and exists for the XLL residual decoder, which wants the 64-band filter
    /// bank running over its own samples and nothing from the core's X96 extension.
    /// </param>
    /// <remarks>
    /// This is the integer twin of <see cref="FilterFrame"/>'s first half, and it is a separate
    /// function in the C rather than a flag on one, because <c>ff_dca_xll_filter_frame</c> calls
    /// it directly with that tri-state argument.
    /// </remarks>
    public bool FilterFixed(int x96Synth)
    {
        int x96Nchannels = 0;

        if (x96Synth == 0
            && (ExtAudioMask & (DcaExtensionMask.CssX96 | DcaExtensionMask.ExssX96)) != 0)
        {
            x96Nchannels = x96NchannelsField;
            x96Synth = 1;
        }
        if (x96Synth < 0)
            x96Synth = 0;

        OutputRate = SampleRate << x96Synth;
        Npcmsamples = (Npcmblocks * DcaLimits.PcmBlockSamples) << x96Synth;

        for (int spkr = 0; spkr < (int)DcaSpeaker.Count; spkr++)
        {
            if ((ChMask & (1 << spkr)) == 0)
            {
                outputSamplesFixed[spkr] = null;
                continue;
            }
            if (outputSamplesFixed[spkr] is null
                || outputSamplesFixed[spkr]!.Length < Npcmsamples)
            {
                outputSamplesFixed[spkr] = new int[Npcmsamples];
            }
        }

        // The FIXED bit is what makes a switch between the float and integer paths clear the
        // QMF history, which it must: the two hold the same signal at different scales.
        SetFilterMode((x96Synth != 0 ? FilterModeX96 : 0) | FilterModeFixed);

        ReadOnlySpan<int> filterCoeff = x96Synth != 0
            ? DcaData.Fir64bandsFixed
            : FilterPerfect != 0
                ? DcaData.Fir32bandsPerfectFixed
                : DcaData.Fir32bandsNonperfectFixed;

        Span<int> bandOffset = stackalloc int[DcaLimits.Subbands];
        Span<int> x96BandOffset = stackalloc int[DcaLimits.SubbandsX96];

        // Filter the primary channels.
        for (int ch = 0; ch < nchannels; ch++)
        {
            int spkr = MapPrmChToSpkr(ch);
            if (spkr < 0)
            {
                LastError = $"primary channel {ch} maps to no speaker in mask 0x{ChMask:X}";
                return false;
            }

            for (int band = 0; band < DcaLimits.Subbands; band++)
                bandOffset[band] = subbandOffset[ch * DcaLimits.Subbands + band];

            if (x96Synth != 0)
            {
                for (int band = 0; band < DcaLimits.SubbandsX96; band++)
                    x96BandOffset[band] = x96SubbandOffset[ch * DcaLimits.SubbandsX96 + band];

                DcaDsp.SubQmf64Fixed(outputSamplesFixed[spkr].AsSpan(0, Npcmsamples),
                                     subbandBuffer, bandOffset,
                                     x96SubbandBuffer, x96BandOffset, ch < x96Nchannels,
                                     hist1Fixed[ch]!, ref histOffset[ch], hist2Fixed[ch]!,
                                     filterCoeff, Npcmblocks);
            }
            else
            {
                DcaDsp.SubQmf32Fixed(outputSamplesFixed[spkr].AsSpan(0, Npcmsamples),
                                     subbandBuffer, bandOffset,
                                     hist1Fixed[ch]!, ref histOffset[ch], hist2Fixed[ch]!,
                                     filterCoeff, Npcmblocks);
            }
        }

        // Filter the LFE channel.
        if (LfePresent != 0)
        {
            // There is no 128-tap integer LFE filter in FFmpeg - ff_dca_lfe_fir_128_fixed does
            // not exist - so the C refuses the frame rather than filtering it with the wrong
            // table. Refusing loudly here is the same decision.
            if (LfePresent == DcaCoreLfeFlag.Lfe128)
            {
                LastError = "the fixed point path does not support LFF=1 (128-tap LFE "
                          + "interpolation); FFmpeg has no integer table for it either";
                return false;
            }

            int[] samples = outputSamplesFixed[(int)DcaSpeaker.Lfe1]!;
            int nlfesamples = Npcmblocks >> 1;

            // With X96 the first interpolation writes into the SECOND half of the plane and
            // LfeX96Fixed then expands it over the whole, so the two passes never overlap.
            int lfeAt = x96Synth != 0 ? Npcmsamples / 2 : 0;

            DcaDsp.LfeFirFixed(samples.AsSpan(lfeAt, Npcmsamples - lfeAt), subbandBuffer,
                               lfeSamplesAt + DcaLimits.LfeHistory,
                               DcaData.LfeFir64Fixed, Npcmblocks);

            if (x96Synth != 0)
            {
                DcaDsp.LfeX96Fixed(samples.AsSpan(0, Npcmsamples),
                                   samples.AsSpan(lfeAt, Npcmsamples / 2),
                                   ref outputHistoryLfeFixed, Npcmsamples / 2);
            }

            // Update the LFE history, backwards so a short frame cannot overwrite what it is
            // about to read.
            for (int n = DcaLimits.LfeHistory - 1; n >= 0; n--)
                subbandBuffer[lfeSamplesAt + n] = subbandBuffer[lfeSamplesAt + nlfesamples + n];
        }

        return true;
    }

    /// <summary>
    /// <c>filter_frame_fixed</c> - the whole integer frame, including the channel fix-ups.
    /// </summary>
    /// <param name="skipCoreFilter">
    /// <c>dca->packet &amp; DCA_PACKET_XLL</c>: when XLL has already filtered the core's
    /// subbands, this runs the channel fix-ups over that output instead of filtering twice.
    /// </param>
    /// <remarks>
    /// The second half of this function is the same sequence of corrections as
    /// <see cref="FilterFrame"/>'s - undo the embedded XCH downmix, undo the embedded XXCH
    /// downmix, sum/difference decoding, the stereo downmix request - with every step in its
    /// integer form. The order is load-bearing and is the C's: XXCH's pre-scale is undone
    /// <em>before</em> the downmix it protected, which is the opposite of the float path's
    /// order and not a mistake in either. In the float path the scale is a multiply that
    /// commutes with the accumulate; here it is a rounding Q16 multiply that does not.
    /// </remarks>
    public bool FilterFrameFixed(bool skipCoreFilter = false)
    {
        // Don't filter twice when falling back from XLL.
        if (!skipCoreFilter && !FilterFixed(0))
            return false;

        int nsamples = Npcmsamples;

        // ff_dca_core_filter_frame's downmix request handling, which has to run before the
        // channel layout is built because it is the layout.
        requestMask =
            RequestChannelLayout == DcaSpeakerLayout.Stereo
            && AudioMode > (int)DcaCoreAudioMode.Mono
            && PrimDmixEmbedded != 0
            && (PrimDmixType == (int)DcaDmixType.LoRo || PrimDmixType == (int)DcaDmixType.LtRt)
                ? DcaSpeakerLayout.Stereo
                : ChMask;

        nchannelsOut = SetChannelLayout(requestMask);

        // Undo the embedded XCH downmix.
        if (EsFormat != 0 && (ExtAudioMask & DcaExtensionMask.CssXch) != 0
            && AudioMode >= (int)DcaCoreAudioMode.Mode2F2R)
        {
            DcaDsp.DmixSubXch(outputSamplesFixed[(int)DcaSpeaker.Ls].AsSpan(0, nsamples),
                              outputSamplesFixed[(int)DcaSpeaker.Rs].AsSpan(0, nsamples),
                              outputSamplesFixed[(int)DcaSpeaker.Cs].AsSpan(0, nsamples),
                              nsamples);
        }

        // Undo the embedded XXCH downmix.
        if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.ExssXxch)) != 0
            && xxchDmixEmbedded != 0)
        {
            int scaleInv = xxchDmixScaleInv;
            int coeffAt = 0;
            int xchBase = DcaData.Channels[AudioMode];

            // Undo the embedded core downmix pre-scaling, FIRST - see the remarks above.
            for (int spkr = 0; spkr < xxchMaskNbits; spkr++)
                if ((xxchCoreMask & (1 << spkr)) != 0)
                    DcaDsp.DmixScaleInv(outputSamplesFixed[spkr].AsSpan(0, nsamples),
                                        scaleInv, nsamples);

            // Undo the downmix. Each coefficient is folded with the same inverse scale before
            // use, and a coefficient that comes out zero is skipped rather than applied - which
            // matters, because mul16 of a small coefficient CAN round to zero and the C's
            // `if (coeff)` tests the rounded value, not the stored one.
            for (int ch = xchBase; ch < nchannels; ch++)
            {
                int srcSpkr = MapPrmChToSpkr(ch);
                if (srcSpkr < 0)
                {
                    LastError = $"XXCH channel {ch} maps to no speaker";
                    return false;
                }

                for (int spkr = 0; spkr < xxchMaskNbits; spkr++)
                {
                    if ((xxchDmixMask[ch - xchBase] & (1 << spkr)) == 0)
                        continue;

                    int coeff = DcaMath.Mul16(xxchDmixCoeff[coeffAt++], scaleInv);
                    if (coeff != 0)
                    {
                        DcaDsp.DmixSub(outputSamplesFixed[spkr].AsSpan(0, nsamples),
                                       outputSamplesFixed[srcSpkr].AsSpan(0, nsamples),
                                       coeff, nsamples);
                    }
                }
            }
        }

        // Sum/difference decoding is skipped entirely when a channel extension is present: the
        // extension's channel set is coded discretely, so the pairs are no longer sum and
        // difference.
        if ((ExtAudioMask & (DcaExtensionMask.CssXxch | DcaExtensionMask.CssXch
                             | DcaExtensionMask.ExssXxch)) == 0)
        {
            if ((SumdiffFront != 0 && AudioMode > (int)DcaCoreAudioMode.Mono)
                || AudioMode == (int)DcaCoreAudioMode.StereoSumDiff)
            {
                ButterfliesFixed(outputSamplesFixed[(int)DcaSpeaker.L]!,
                                 outputSamplesFixed[(int)DcaSpeaker.R]!, nsamples);
            }

            if (SumdiffSurround != 0 && AudioMode >= (int)DcaCoreAudioMode.Mode2F2R)
            {
                ButterfliesFixed(outputSamplesFixed[(int)DcaSpeaker.Ls]!,
                                 outputSamplesFixed[(int)DcaSpeaker.Rs]!, nsamples);
            }
        }

        // Downmix the primary channel set to stereo.
        if (requestMask != ChMask)
            DownmixToStereoFixed(primDmixCoeff, nsamples, ChMask);

        // Into AV_SAMPLE_FMT_S32P: 24 significant bits in the top of a 32-bit sample. The
        // clip23 is not redundant with the ones inside the filter bank - the channel fix-ups
        // above all run after those and can push a sample back out of range.
        for (int i = 0; i < nchannelsOut; i++)
        {
            int[] plane = outputSamplesFixed[chRemap[i]]!;
            for (int n = 0; n < nsamples; n++)
                plane[n] = DcaMath.Clip23(plane[n]) * (1 << 8);
        }

        return true;
    }

    /// <summary>
    /// <c>ff_dca_core_map_spkr</c> (dca_core.h) - this core's plane index for a speaker.
    /// </summary>
    /// <remarks>
    /// The two fallbacks are not cosmetic. A side surround channel is served by the plain
    /// surround one when the core has no side pair, which is how an XLL channel set that names
    /// Lss/Rss finds its residual partner in a core that only has Ls/Rs. Returning -1 for those
    /// would make every such channel fall back to lossy.
    /// </remarks>
    public int MapSpkr(int spkr)
    {
        if ((ChMask & (1u << spkr)) != 0)
            return spkr;
        if (spkr == (int)DcaSpeaker.Lss && (ChMask & DcaSpeakerMask.Ls) != 0)
            return (int)DcaSpeaker.Ls;
        if (spkr == (int)DcaSpeaker.Rss && (ChMask & DcaSpeakerMask.Rs) != 0)
            return (int)DcaSpeaker.Rs;
        return -1;
    }

    /// <summary>
    /// <c>s-&gt;output_samples[spkr]</c> from the fixed point path, by SPEAKER index and
    /// before the s32p conversion.
    /// </summary>
    /// <remarks>
    /// This is what <c>combine_residual_frame</c> reads, and the distinction from
    /// <see cref="PlaneFixed"/> matters twice over: the index is a speaker rather than an output
    /// position, and the samples are the raw 24-bit ones rather than the
    /// <c>clip23(x) &lt;&lt; 8</c> form. The shift has not happened because
    /// <c>filter_frame_fixed</c> is skipped entirely when a packet carries XLL - it is
    /// <see cref="FilterFixed"/> alone that runs - so reading the shifted planes here would be
    /// reading a buffer nothing had written.
    /// </remarks>
    public ReadOnlySpan<int> RawFixedPlane(int spkr, int nsamples) =>
        outputSamplesFixed[spkr].AsSpan(0, nsamples);

    /// <summary>
    /// <c>ff_dca_downmix_to_stereo_fixed</c> (dcadec.c) - the integer stereo downmix.
    /// </summary>
    /// <remarks>
    /// The coefficient layout is <see cref="DownmixToStereoFloat"/>'s and the same warning
    /// applies to <c>pos</c>. What differs is only that the scale is applied by a rounding Q15
    /// multiply rather than a float one, so the two paths are not expected to agree to the last
    /// bit - the float path is not a rounding of this one, it is a different computation.
    /// </remarks>
    private void DownmixToStereoFixed(int[] coeff, int nsamples, int chMask)
    {
        int maxSpkr = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)chMask);
        int coeffR = System.Numerics.BitOperations.PopCount((uint)chMask);

        if (!DcaSpeakerLayout.HasStereo(chMask))
            throw new InvalidOperationException(
                $"a stereo downmix was requested from a layout with no L/R pair (0x{chMask:X})");

        // Scale left and right. The index is offset by one when a centre channel is present,
        // because C sorts before L in speaker order. DCA_SPEAKER_MASK_C is 1, so the C's
        // `pos = ch_mask & DCA_SPEAKER_MASK_C` is an index of 0 or 1 and not a mask test that
        // happens to work.
        int pos = chMask & DcaSpeakerMask.C;
        DcaDsp.DmixScale(outputSamplesFixed[(int)DcaSpeaker.L].AsSpan(0, nsamples),
                         coeff[pos], nsamples);
        DcaDsp.DmixScale(outputSamplesFixed[(int)DcaSpeaker.R].AsSpan(0, nsamples),
                         coeff[coeffR + pos + 1], nsamples);

        int l = 0, r = coeffR;
        for (int spkr = 0; spkr <= maxSpkr; spkr++)
        {
            if ((chMask & (1 << spkr)) == 0)
                continue;

            if (coeff[l] != 0 && spkr != (int)DcaSpeaker.L)
            {
                DcaDsp.DmixAdd(outputSamplesFixed[(int)DcaSpeaker.L].AsSpan(0, nsamples),
                               outputSamplesFixed[spkr].AsSpan(0, nsamples),
                               coeff[l], nsamples);
            }

            if (coeff[r] != 0 && spkr != (int)DcaSpeaker.R)
            {
                DcaDsp.DmixAdd(outputSamplesFixed[(int)DcaSpeaker.R].AsSpan(0, nsamples),
                               outputSamplesFixed[spkr].AsSpan(0, nsamples),
                               coeff[r], nsamples);
            }

            l++;
            r++;
        }
    }

    /// <summary>
    /// <c>butterflies_fixed_c</c> (libavutil/fixed_dsp.c) - the integer sum/difference pass.
    /// </summary>
    /// <remarks>
    /// The C runs this through an <c>unsigned</c> alias so the overflow is defined rather than
    /// undefined; C# integer arithmetic already wraps, so the alias has no counterpart here.
    /// There is deliberately <b>no</b> saturation: sum/difference decoding has to be exactly
    /// invertible, and clipping it would not be.
    /// </remarks>
    private static void ButterfliesFixed(int[] v1, int[] v2, int len)
    {
        for (int i = 0; i < len; i++)
        {
            int t = v1[i] - v2[i];
            v1[i] += v2[i];
            v2[i] = t;
        }
    }

    /// <summary>
    /// <c>parse_aux_data</c> - the auxiliary data block, read for its dynamic downmix matrix.
    /// </summary>
    /// <remarks>
    /// The declared byte count is skipped rather than used: the C's comment is "can't be
    /// trusted". Everything after the sync word is positional, and the matrix is
    /// <c>m</c> rows of <c>n</c> columns where <c>m</c> comes from the downmix type and
    /// <c>n</c> from the audio mode plus the LFE.
    /// </remarks>
    private bool ParseAuxData()
    {
        if (gb.GetBitsLeft() < 0)
            return false;

        // Auxiliary data byte count - can't be trusted, so it is skipped rather than read.
        gb.SkipBits(6);

        // 4-byte align.
        gb.SkipBits(-gb.GetBitsCount() & 31);

        if (gb.GetBits(32) != DcaSyncwords.Rev1Aux)
        {
            LastError = "invalid auxiliary data sync word";
            return false;
        }

        // Auxiliary decode time stamp flag.
        if (gb.GetBits1() != 0)
            gb.SkipBits(47);

        // Auxiliary dynamic downmix flag.
        PrimDmixEmbedded = (int)gb.GetBits1();
        if (PrimDmixEmbedded == 0)
            return true;

        PrimDmixType = (int)gb.GetBits(3);
        if (PrimDmixType >= (int)DcaDmixType.Count)
        {
            LastError = $"invalid primary channel set downmix type {PrimDmixType}";
            return false;
        }

        int m = DcaData.DmixPrimaryNch[PrimDmixType];
        int n = DcaData.Channels[AudioMode] + (LfePresent != 0 ? 1 : 0);

        for (int i = 0; i < m * n; i++)
        {
            int code = (int)gb.GetBits(9);
            int sign = (code >> 8) - 1;
            int index = code & 0xff;
            if ((uint)index >= DcaData.Dmixtable.Length)
            {
                LastError = $"invalid downmix coefficient index {index}";
                return false;
            }
            primDmixCoeff[i] = (DcaData.Dmixtable[index] ^ sign) - sign;
        }

        return true;
    }

    /// <summary><c>vector_fmul_scalar</c> - <c>dst[i] *= scale</c>.</summary>
    private static void VectorFmulScalar(float[] dst, float scale, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] *= scale;
    }

    /// <summary><c>vector_fmac_scalar</c> - <c>dst[i] += src[i] * scale</c>.</summary>
    private static void VectorFmacScalar(float[] dst, float[] src, float scale, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] += src[i] * scale;
    }

    /// <summary>
    /// <c>butterflies_float</c> - <c>t = a - b; a += b; b = t;</c> over a whole plane.
    /// </summary>
    private static void Butterflies(float[] v1, float[] v2, int len)
    {
        for (int i = 0; i < len; i++)
        {
            float t = v1[i] - v2[i];
            v1[i] += v2[i];
            v2[i] = t;
        }
    }

    /// <summary>
    /// <c>ff_dca_set_channel_layout</c> (dcadec.c) in its default WAV ordering.
    /// </summary>
    /// <remarks>
    /// The two-step mapping is the C's and is not a simple sort: each DTS speaker maps to a WAV
    /// channel number, the FIRST DTS speaker claiming a WAV slot keeps it, and the output order
    /// is then by WAV number. That is why the table can map two DTS speakers to one WAV channel
    /// without producing two output planes.
    /// </remarks>
    private int SetChannelLayout(int chMask) => SetChannelLayoutInto(chRemap, chMask);

    /// <summary>
    /// <c>ff_dca_set_channel_layout</c> writing into a caller's map, so XLL can share it.
    /// </summary>
    public static int SetChannelLayoutInto(int[] chRemap, int chMask)
    {
        ReadOnlySpan<byte> dca2wavNorm =
        [
             2,  0, 1, 9, 10,  3,  8,  4,  5,  9, 10, 6, 7, 12,
            13, 14, 3, 6,  7, 11, 12, 14, 16, 15, 17, 8, 4,  5,
        ];
        ReadOnlySpan<byte> dca2wavWide =
        [
             2,  0, 1, 4,  5,  3,  8,  4,  5,  9, 10, 6, 7, 12,
            13, 14, 3, 9, 10, 11, 12, 14, 16, 15, 17, 8, 4,  5,
        ];

        ReadOnlySpan<byte> dca2wav =
            chMask == DcaSpeakerLayout.Layout7Point0Wide
            || chMask == DcaSpeakerLayout.Layout7Point1Wide
                ? dca2wavWide
                : dca2wavNorm;

        int wavMask = 0;
        Span<int> wavMap = stackalloc int[18];

        for (int dcaCh = 0; dcaCh < 28; dcaCh++)
        {
            if ((chMask & (1 << dcaCh)) != 0)
            {
                int wavCh = dca2wav[dcaCh];
                if ((wavMask & (1 << wavCh)) == 0)
                {
                    wavMap[wavCh] = dcaCh;
                    wavMask |= 1 << wavCh;
                }
            }
        }

        int n = 0;
        for (int wavCh = 0; wavCh < 18; wavCh++)
            if ((wavMask & (1 << wavCh)) != 0)
                chRemap[n++] = wavMap[wavCh];

        return n;
    }
}

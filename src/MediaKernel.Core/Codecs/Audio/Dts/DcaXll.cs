// DTS-HD Master Audio's lossless extension - a port of ffmpeg-master/libavcodec/dca_xll.c.
//
// XLL is not a refinement of the core the way XBR and X96 are. It is a complete, independently
// coded lossless signal, and the core is present in the same stream only as a lossy fallback for
// decoders that cannot read this layer. Where a channel IS coded as a residual, though, the two
// are added together - which is why this needs the core's FIXED point output and not its float
// output, and why ff_dca_core_filter_frame forces filter_frame_fixed whenever an XLL asset is
// present. See combine_residual_frame.
//
// Three things make this file worth reading before changing it:
//
//  * The sample buffers are addressed by OFFSET, not by pointer, and the offsets are reordered
//    rather than the data. FFmpeg keeps an array of int32_t* per channel and permutes the
//    POINTERS to undo the encoder's channel reordering (chs_filter_band_data) and to point the
//    output at whichever buffer last wrote it (output_samples). Translating that into data
//    copies would work and would be slower and further from the C; translating it into offsets
//    keeps the shape exactly.
//  * Several of those windows are read from BEFORE their nominal start. The decimator history
//    sits in the eight int32s in front of band 0's samples, and assemble_freq_bands walks src0
//    backwards eight more. chs_alloc_msb_band_data is what leaves that room, by offsetting the
//    first channel's base by ndecisamples - so the headroom is part of the allocation contract,
//    not slack.
//  * The PBR (peak bit rate) smoothing buffer means an XLL frame is NOT one packet. A frame may
//    span packets and a packet may hold the tail of one frame and the head of the next, so the
//    parse is a two-state machine over a 240 KB buffer, and a frame that needs data which has
//    not arrived yet returns Again rather than failing.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>DCA_XLL_*</c> limits.</summary>
internal static class DcaXllLimits
{
    public const int ChSetsMax = 3;
    public const int ChannelsMax = 8;
    public const int BandsMax = 2;
    public const int AdaptPredOrderMax = 16;
    public const int DeciHistoryMax = 8;
    public const int DmixScalesMax = (ChSetsMax - 1) * ChannelsMax;
    public const int DmixCoeffsMax = DmixScalesMax * ChannelsMax;
    public const int PbrBufferMax = 240 << 10;
    public const int SampleBuffersMax = 3;
}

/// <summary>
/// What <see cref="DcaXllDecoder.Parse"/> concluded about a packet.
/// </summary>
/// <remarks>
/// <see cref="Again"/> is not a failure and must not be treated as one. It means the frame is
/// genuinely incomplete - the packet landed in the middle of a PBR smoothing period, or a
/// decoding delay has not expired - and the C's callers respond by falling back to the lossy
/// core for this frame, not by reporting an error. Collapsing it into a failure code mutes
/// output that FFmpeg would have played.
/// </remarks>
internal enum DcaXllStatus
{
    Ok,
    Again,
    Invalid,
}

/// <summary><c>DCAXllBand</c> - one frequency band of one channel set.</summary>
internal sealed class DcaXllBand
{
    /// <summary>Pairwise channel decorrelation flag.</summary>
    public int DecorEnabled;

    /// <summary>Original channel order.</summary>
    public readonly int[] OrigOrder = new int[DcaXllLimits.ChannelsMax];

    /// <summary>Pairwise channel coefficients.</summary>
    public readonly int[] DecorCoeff = new int[DcaXllLimits.ChannelsMax / 2];

    /// <summary>Adaptive predictor order.</summary>
    public readonly int[] AdaptPredOrder = new int[DcaXllLimits.ChannelsMax];

    /// <summary>Highest adaptive predictor order.</summary>
    public int HighestPredOrder;

    /// <summary>Fixed predictor order.</summary>
    public readonly int[] FixedPredOrder = new int[DcaXllLimits.ChannelsMax];

    /// <summary>Adaptive predictor reflection coefficients.</summary>
    public readonly int[,] AdaptReflCoeff =
        new int[DcaXllLimits.ChannelsMax, DcaXllLimits.AdaptPredOrderMax];

    /// <summary>Downmix performed by the encoder in this frequency band.</summary>
    public int DmixEmbedded;

    /// <summary>Size of the LSB section in any segment.</summary>
    public int LsbSectionSize;

    /// <summary>Number of bits representing the samples in the LSB part.</summary>
    public readonly int[] Nscalablelsbs = new int[DcaXllLimits.ChannelsMax];

    /// <summary>Number of bits discarded by authoring.</summary>
    public readonly int[] BitWidthAdjust = new int[DcaXllLimits.ChannelsMax];

    /// <summary>
    /// Offsets into <see cref="DcaXllChSet.SampleBuffer"/>[0] of each channel's MSB samples.
    /// </summary>
    /// <remarks>
    /// The C's <c>int32_t *msb_sample_buffer[]</c>. These are PERMUTED rather than the data
    /// being moved - see <c>ChsFilterBandData</c> - and each one has
    /// <see cref="DcaXllLimits.DeciHistoryMax"/> int32s of readable room in front of it.
    /// </remarks>
    public readonly int[] MsbOffset = new int[DcaXllLimits.ChannelsMax];

    /// <summary>
    /// Offsets into <see cref="DcaXllChSet.SampleBuffer"/>[1] of each channel's LSB samples, or
    /// -1 when this band has no MSB/LSB split.
    /// </summary>
    public readonly int[] LsbOffset = new int[DcaXllLimits.ChannelsMax];
}

/// <summary><c>DCAXllChSet</c> - one channel set.</summary>
internal sealed class DcaXllChSet
{
    // Channel set header.
    public int Nchannels;
    public int ResidualEncode;
    public int PcmBitRes;
    public int StorageBitRes;
    public int Freq;

    public int PrimaryChset;
    public int DmixCoeffsPresent;
    public int DmixEmbedded;
    public int DmixType;
    public int HierChset;

    /// <summary>Number of preceding channels in the hierarchy (M).</summary>
    public int HierOfs;

    public readonly int[] DmixCoeff = new int[DcaXllLimits.DmixCoeffsMax];
    public readonly int[] DmixScale = new int[DcaXllLimits.DmixScalesMax];
    public readonly int[] DmixScaleInv = new int[DcaXllLimits.DmixScalesMax];
    public int ChMask;
    public readonly int[] ChRemap = new int[DcaXllLimits.ChannelsMax];

    public int Nfreqbands;
    public int Nabits;

    public readonly DcaXllBand[] Bands =
    [
        new DcaXllBand(), new DcaXllBand(),
    ];

    // Frequency band coding parameters.
    public int SegCommon;
    public readonly int[] RiceCodeFlag = new int[DcaXllLimits.ChannelsMax];
    public readonly int[] BitallocHybridLinear = new int[DcaXllLimits.ChannelsMax];
    public readonly int[] BitallocPartA = new int[DcaXllLimits.ChannelsMax];
    public readonly int[] BitallocPartB = new int[DcaXllLimits.ChannelsMax];
    public readonly int[] NsamplesPartA = new int[DcaXllLimits.ChannelsMax];

    /// <summary>Decimator history for frequency band 1.</summary>
    public readonly int[,] DeciHistory =
        new int[DcaXllLimits.ChannelsMax, DcaXllLimits.DeciHistoryMax];

    /// <summary>
    /// The three sample buffers: 0 is MSB, 1 is LSB, 2 is the frequency band assembly output.
    /// </summary>
    public readonly int[]?[] SampleBuffer = new int[DcaXllLimits.SampleBuffersMax][];

    public void Clear()
    {
        Nchannels = ResidualEncode = PcmBitRes = StorageBitRes = Freq = 0;
        PrimaryChset = DmixCoeffsPresent = DmixEmbedded = DmixType = HierChset = 0;
        HierOfs = ChMask = Nfreqbands = Nabits = SegCommon = 0;
        Array.Clear(DmixCoeff);
        Array.Clear(DmixScale);
        Array.Clear(DmixScaleInv);
        Array.Clear(ChRemap);
        Array.Clear(DeciHistory);
    }
}

/// <summary><c>DCAXllDecoder</c> - <c>dca_xll.c</c>.</summary>
internal sealed class DcaXllDecoder
{
    private GetBitContext gb;

    public int FrameSize;
    public int Nchsets;
    public int Nframesegs;
    public int NsegsamplesLog2;
    public int Nsegsamples;
    public int NframesamplesLog2;
    public int Nframesamples;
    public int SegSizeNbits;
    public int BandCrcPresent;
    public int ScalableLsbs;
    public int ChMaskNbits;
    public int FixedLsbWidth;

    public readonly DcaXllChSet[] Chset =
    [
        new DcaXllChSet(), new DcaXllChSet(), new DcaXllChSet(),
    ];

    private int[] navi = new int[64];
    private int naviCount;

    public int Nfreqbands;
    public int Nchannels;
    public int Nreschsets;
    public int Nactivechsets;

    /// <summary>Previous DTS-HD stream ID, for detecting a change.</summary>
    private int hdStreamId = -1;

    private byte[]? pbrBuffer;
    private int pbrLength;
    private int pbrDelay;

    public int XSyncwordPresent;
    public int XImaxSyncwordPresent;

    public int OutputMask;

    // The C's int32_t *output_samples[DCA_SPEAKER_COUNT]. A speaker's samples can live in any
    // channel set's MSB buffer or in its assembly buffer, so the array and the offset are both
    // needed; one array of pointers in the C becomes two here.
    private readonly int[]?[] outputBuffer = new int[(int)DcaSpeaker.Count][];
    private readonly int[] outputOffset = new int[(int)DcaSpeaker.Count];

    /// <summary>
    /// <c>dca->request_channel_layout</c>, which XLL reads to decide how many channel sets to
    /// decode at all - not merely how to fold the result.
    /// </summary>
    public int RequestChannelLayout { get; set; }

    public string LastError { get; private set; } = "";

    public int SampleRate { get; private set; }
    public int Npcmsamples { get; private set; }
    public int StorageBitRes { get; private set; }
    public int PcmBitRes { get; private set; }

    private readonly int[] chRemap = new int[(int)DcaSpeaker.Count];
    private int nchannelsOut;

    /// <summary>The speaker each output plane belongs to, in output order.</summary>
    public ReadOnlySpan<int> ChannelRemap => chRemap.AsSpan(0, nchannelsOut);

    // The decoded planes, as 32-bit samples in the s32p convention the C emits.
    private readonly int[]?[] planes = new int[(int)DcaSpeaker.Count][];

    /// <summary>One decoded plane, in the order <see cref="ChannelRemap"/> gives.</summary>
    public ReadOnlySpan<int> PlaneFixed(int index) =>
        planes[chRemap[index]].AsSpan(0, Npcmsamples);

    // ---- Bitstream primitives ------------------------------------------------------------

    /// <summary><c>get_linear</c> - a signed value in zigzag form.</summary>
    private int GetLinear(int n)
    {
        uint v = gb.GetBits(n);
        return (int)((v >> 1) ^ (uint)-(int)(v & 1));
    }

    /// <summary><c>get_rice_un</c> - an unsigned Rice code with parameter k.</summary>
    private int GetRiceUn(int k)
    {
        uint v = (uint)GetUnary(1, gb.GetBitsLeft());
        return (int)((v << k) | gb.GetBits(k));
    }

    /// <summary><c>get_rice</c> - a signed Rice code.</summary>
    private int GetRice(int k)
    {
        uint v = (uint)GetRiceUn(k);
        return (int)((v >> 1) ^ (uint)-(int)(v & 1));
    }

    /// <summary><c>get_unary</c> (unary.h) - count bits until <paramref name="stop"/>.</summary>
    private int GetUnary(int stop, int len)
    {
        int i = 0;
        while (i < len && gb.GetBits1() != stop)
            i++;
        return i;
    }

    /// <summary><c>get_array</c> - <paramref name="size"/> unsigned values of n bits.</summary>
    private void GetArray(int[] array, int at, int size, int n)
    {
        for (int i = 0; i < size; i++)
            array[at + i] = (int)gb.GetBits(n);
    }

    /// <summary><c>get_linear_array</c>.</summary>
    /// <remarks>
    /// A width of zero is not "read zero bits", it is "every value is zero", and the C writes
    /// the zeros rather than leaving the buffer alone. The distinction matters because these
    /// buffers are reused between frames.
    /// </remarks>
    private void GetLinearArray(int[] array, int at, int size, int n)
    {
        if (n == 0)
            Array.Clear(array, at, size);
        else
            for (int i = 0; i < size; i++)
                array[at + i] = GetLinear(n);
    }

    /// <summary><c>get_rice_array</c>.</summary>
    private bool GetRiceArray(int[] array, int at, int size, int k)
    {
        int i = 0;
        for (; i < size && gb.GetBitsLeft() > k; i++)
            array[at + i] = GetRice(k);

        return i >= size;
    }

    /// <summary><c>ff_dca_check_crc</c> - see DcaCore's copy for why this does not compute.</summary>
    private bool CheckCrc(int p1, int p2)
    {
        if (((p1 | p2) & 7) != 0 || p1 < 0 || p2 > gb.SizeInBits || p2 - p1 < 16)
            return false;
        return true;
    }

    /// <summary><c>ff_dca_seek_bits</c>.</summary>
    private bool SeekBits(int p)
    {
        if (p < gb.GetBitsCount() || p > gb.SizeInBits)
            return false;
        gb.SkipBits(p - gb.GetBitsCount());
        return true;
    }

    // ---- Header parsing ------------------------------------------------------------------

    /// <summary><c>parse_dmix_coeffs</c>.</summary>
    /// <remarks>
    /// The matrix is <c>m</c> rows by <c>nchannels</c> columns, and <c>m</c> comes from a
    /// different place for the primary channel set than for the others: the downmix type for the
    /// primary, the count of channels above it in the hierarchy for the rest.
    /// <para>
    /// A non-primary set also carries a per-row SCALE, and its coefficients are multiplied by
    /// the inverse of that scale as they are read - <c>mul16(scale_inv, coeff)</c> - which turns
    /// a downmix coefficient into an undo-downmix coefficient on the spot. The scale index is
    /// offset by <c>FF_DCA_DMIXTABLE_OFFSET</c> because the inverse table covers only the part
    /// of the range that can be inverted.
    /// </para>
    /// </remarks>
    private bool ParseDmixCoeffs(DcaXllChSet c)
    {
        int m = c.PrimaryChset != 0 ? DcaData.DmixPrimaryNch[c.DmixType] : c.HierOfs;
        int coeffAt = 0;

        for (int i = 0; i < m; i++)
        {
            int scaleInv = 0;

            // Downmix scale, only for non-primary channel sets.
            if (c.PrimaryChset == 0)
            {
                int code = (int)gb.GetBits(9);
                int sign = (code >> 8) - 1;
                uint index = (uint)((code & 0xff) - DcaData.DmixtableOffset);
                if (index >= (uint)DcaData.InvDmixtable.Length)
                {
                    LastError = $"invalid XLL downmix scale index {code & 0xff}";
                    return false;
                }
                int scale = DcaData.Dmixtable[(int)index + DcaData.DmixtableOffset];
                scaleInv = (int)DcaData.InvDmixtable[(int)index];
                if ((uint)i >= (uint)c.DmixScale.Length)
                {
                    LastError = $"XLL downmix scale row {i} is past the {c.DmixScale.Length} "
                              + "the format allows";
                    return false;
                }
                c.DmixScale[i] = (scale ^ sign) - sign;
                c.DmixScaleInv[i] = (scaleInv ^ sign) - sign;
            }

            for (int j = 0; j < c.Nchannels; j++)
            {
                int code = (int)gb.GetBits(9);
                int sign = (code >> 8) - 1;
                uint index = (uint)(code & 0xff);
                if (index >= (uint)DcaData.Dmixtable.Length)
                {
                    LastError = $"invalid XLL downmix coefficient index {index}";
                    return false;
                }
                int coeff = DcaData.Dmixtable[(int)index];
                if (c.PrimaryChset == 0)
                {
                    // Multiply by |InvDmixScale| to get |UndoDmixScale|.
                    coeff = DcaMath.Mul16(scaleInv, coeff);
                }
                if (coeffAt >= c.DmixCoeff.Length)
                {
                    LastError = "XLL downmix coefficient matrix is larger than the format allows";
                    return false;
                }
                c.DmixCoeff[coeffAt++] = (coeff ^ sign) - sign;
            }
        }

        return true;
    }

    /// <summary><c>chs_parse_header</c> - one channel set's sub-header.</summary>
    private bool ChsParseHeader(DcaXllChSet c, in DcaExssAsset asset)
    {
        int headerPos = gb.GetBitsCount();
        DcaXllChSet p = Chset[0];

        int headerSize = (int)gb.GetBits(10) + 1;

        if (!CheckCrc(headerPos, headerPos + headerSize * 8))
        {
            LastError = "invalid XLL sub-header checksum";
            return false;
        }

        c.Nchannels = (int)gb.GetBits(4) + 1;
        if (c.Nchannels > DcaXllLimits.ChannelsMax)
        {
            LastError = $"{c.Nchannels} XLL channels in a channel set, which the format caps at "
                      + $"{DcaXllLimits.ChannelsMax}";
            return false;
        }

        c.ResidualEncode = (int)gb.GetBits(c.Nchannels);
        c.PcmBitRes = (int)gb.GetBits(5) + 1;

        c.StorageBitRes = (int)gb.GetBits(5) + 1;
        if (c.StorageBitRes is not (16 or 20 or 24))
        {
            LastError = $"{c.StorageBitRes}-bit XLL storage resolution is not one of 16, 20 or 24";
            return false;
        }

        if (c.PcmBitRes > c.StorageBitRes)
        {
            LastError = $"invalid PCM bit resolution for an XLL channel set "
                      + $"({c.PcmBitRes} > {c.StorageBitRes})";
            return false;
        }

        c.Freq = (int)DcaData.SamplingFreqs[(int)gb.GetBits(4)];
        if (c.Freq > 192000)
        {
            LastError = $"{c.Freq} Hz XLL sampling frequency is above the 192 kHz maximum";
            return false;
        }

        if (gb.GetBits(2) != 0)
        {
            LastError = "an XLL sampling frequency modifier is set, which FFmpeg does not "
                      + "implement either";
            return false;
        }

        if (gb.GetBits(2) != 0)
        {
            LastError = "an XLL replacement set is declared, which FFmpeg does not implement "
                      + "either";
            return false;
        }

        if (asset.OneToOneMapChToSpkr != 0)
        {
            c.PrimaryChset = (int)gb.GetBits1();
            if ((c.PrimaryChset != 0) != ReferenceEquals(c, p))
            {
                LastError = "the first (and only) XLL channel set must be the primary one";
                return false;
            }

            c.DmixCoeffsPresent = (int)gb.GetBits1();
            c.DmixEmbedded = c.DmixCoeffsPresent != 0 && gb.GetBits1() != 0 ? 1 : 0;

            if (c.DmixCoeffsPresent != 0 && c.PrimaryChset != 0)
            {
                c.DmixType = (int)gb.GetBits(3);
                if (c.DmixType >= (int)DcaDmixType.Count)
                {
                    LastError = $"invalid XLL primary channel set downmix type {c.DmixType}";
                    return false;
                }
            }

            c.HierChset = (int)gb.GetBits1();
            if (c.HierChset == 0 && Nchsets != 1)
            {
                LastError = "an XLL channel set outside the hierarchy, which FFmpeg does not "
                          + "implement either";
                return false;
            }

            if (c.DmixCoeffsPresent != 0 && !ParseDmixCoeffs(c))
                return false;

            if (gb.GetBits1() == 0)
            {
                LastError = "the XLL channel mask is disabled, which FFmpeg does not implement "
                          + "either";
                return false;
            }

            c.ChMask = (int)gb.GetBits(ChMaskNbits);
            if (System.Numerics.BitOperations.PopCount((uint)c.ChMask) != c.Nchannels)
            {
                LastError = $"invalid XLL channel mask 0x{c.ChMask:X} for {c.Nchannels} channels";
                return false;
            }

            for (int i = 0, j = 0; i < ChMaskNbits; i++)
                if ((c.ChMask & (1u << i)) != 0)
                    c.ChRemap[j++] = i;
        }
        else
        {
            // The LtRt case: two channels with no speaker map, which is read as plain stereo.
            if (c.Nchannels != 2 || Nchsets != 1 || gb.GetBits1() != 0)
            {
                LastError = "a custom XLL channel to speaker mapping, which FFmpeg does not "
                          + "implement either";
                return false;
            }

            c.PrimaryChset = 1;
            c.DmixCoeffsPresent = 0;
            c.DmixEmbedded = 0;
            c.HierChset = 0;
            c.ChMask = DcaSpeakerLayout.Stereo;
            c.ChRemap[0] = (int)DcaSpeaker.L;
            c.ChRemap[1] = (int)DcaSpeaker.R;
        }

        if (c.Freq > 96000)
        {
            if (gb.GetBits1() != 0)
            {
                LastError = "extra XLL frequency bands, which FFmpeg does not implement either";
                return false;
            }
            c.Nfreqbands = 2;
        }
        else
        {
            c.Nfreqbands = 1;
        }

        // The declared frequency is the assembled one; band 0 runs at half it when there are
        // two bands, and the doubling is put back after the bands are assembled.
        c.Freq >>= c.Nfreqbands - 1;

        if (!ReferenceEquals(c, p)
            && (c.Nfreqbands != p.Nfreqbands || c.Freq != p.Freq
                || c.PcmBitRes != p.PcmBitRes || c.StorageBitRes != p.StorageBitRes))
        {
            LastError = "XLL channel sets with different audio characteristics, which FFmpeg "
                      + "does not implement either";
            return false;
        }

        // Bits needed for the bit allocation coding parameter.
        c.Nabits = c.StorageBitRes > 16 ? 5 : c.StorageBitRes > 8 ? 4 : 3;

        // Account for the embedded downmix and the decimator's saturation.
        if ((Nchsets > 1 || c.Nfreqbands > 1) && c.Nabits < 5)
            c.Nabits++;

        for (int band = 0; band < c.Nfreqbands; band++)
        {
            DcaXllBand b = c.Bands[band];

            b.DecorEnabled = (int)gb.GetBits1();
            if (b.DecorEnabled != 0 && c.Nchannels > 1)
            {
                int chNbits = CeilLog2(c.Nchannels);

                for (int i = 0; i < c.Nchannels; i++)
                {
                    b.OrigOrder[i] = (int)gb.GetBits(chNbits);
                    if (b.OrigOrder[i] >= c.Nchannels)
                    {
                        LastError = $"invalid XLL original channel order {b.OrigOrder[i]}";
                        return false;
                    }
                }

                for (int i = 0; i < c.Nchannels / 2; i++)
                    b.DecorCoeff[i] = gb.GetBits1() != 0 ? GetLinear(7) : 0;
            }
            else
            {
                for (int i = 0; i < c.Nchannels; i++)
                    b.OrigOrder[i] = i;
                for (int i = 0; i < c.Nchannels / 2; i++)
                    b.DecorCoeff[i] = 0;
            }

            b.HighestPredOrder = 0;
            for (int i = 0; i < c.Nchannels; i++)
            {
                b.AdaptPredOrder[i] = (int)gb.GetBits(4);
                if (b.AdaptPredOrder[i] > b.HighestPredOrder)
                    b.HighestPredOrder = b.AdaptPredOrder[i];
            }
            if (b.HighestPredOrder > Nsegsamples)
            {
                LastError = $"XLL adaptive prediction order {b.HighestPredOrder} exceeds the "
                          + $"{Nsegsamples} samples in a segment";
                return false;
            }

            for (int i = 0; i < c.Nchannels; i++)
                b.FixedPredOrder[i] =
                    b.AdaptPredOrder[i] != 0 ? 0 : (int)gb.GetBits(2);

            for (int i = 0; i < c.Nchannels; i++)
            {
                for (int j = 0; j < b.AdaptPredOrder[i]; j++)
                {
                    int k = GetLinear(8);
                    if (k == -128)
                    {
                        LastError = "invalid XLL reflection coefficient index -128";
                        return false;
                    }
                    b.AdaptReflCoeff[i, j] = k < 0
                        ? -DcaData.XllReflCoeff[-k]
                        : DcaData.XllReflCoeff[k];
                }
            }

            // Downmix performed by the encoder in the extension frequency band.
            b.DmixEmbedded =
                c.DmixEmbedded != 0 && (band == 0 || gb.GetBits1() != 0) ? 1 : 0;

            // MSB/LSB split flag in the extension frequency band.
            if ((band == 0 && ScalableLsbs != 0) || (band != 0 && gb.GetBits1() != 0))
            {
                b.LsbSectionSize = (int)gb.GetBits(SegSizeNbits);
                if (b.LsbSectionSize < 0 || b.LsbSectionSize > FrameSize)
                {
                    LastError = $"invalid XLL LSB section size {b.LsbSectionSize}";
                    return false;
                }

                // Account for the optional CRC bytes after the LSB section.
                if (b.LsbSectionSize != 0
                    && (BandCrcPresent > 2 || (band == 0 && BandCrcPresent > 1)))
                {
                    b.LsbSectionSize += 2;
                }

                for (int i = 0; i < c.Nchannels; i++)
                {
                    b.Nscalablelsbs[i] = (int)gb.GetBits(4);
                    if (b.Nscalablelsbs[i] != 0 && b.LsbSectionSize == 0)
                    {
                        LastError = "an XLL LSB section is missing although its width is "
                                  + "non-zero";
                        return false;
                    }
                }
            }
            else
            {
                b.LsbSectionSize = 0;
                for (int i = 0; i < c.Nchannels; i++)
                    b.Nscalablelsbs[i] = 0;
            }

            // Scalable resolution flag in the extension frequency band.
            if ((band == 0 && ScalableLsbs != 0) || (band != 0 && gb.GetBits1() != 0))
            {
                for (int i = 0; i < c.Nchannels; i++)
                    b.BitWidthAdjust[i] = (int)gb.GetBits(4);
            }
            else
            {
                for (int i = 0; i < c.Nchannels; i++)
                    b.BitWidthAdjust[i] = 0;
            }
        }

        // Reserved, byte align, CRC16 of the sub-header.
        if (!SeekBits(headerPos + headerSize * 8))
        {
            LastError = "read past the end of the XLL sub-header";
            return false;
        }

        return true;
    }

    /// <summary><c>av_ceil_log2</c>.</summary>
    private static int CeilLog2(int v) =>
        v <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(v - 1));

    // ---- Band data -----------------------------------------------------------------------

    /// <summary><c>chs_alloc_msb_band_data</c>.</summary>
    /// <remarks>
    /// The leading <c>ndecisamples</c> of headroom is the contract that lets band 0's samples be
    /// read from before their start: the decimator history is copied in there by
    /// <c>ChsAssembleFreqBands</c>, and <c>AssembleFreqBands</c> walks eight further back
    /// still. Dropping the offset would corrupt the previous channel's tail instead of reading
    /// history, silently and only on two-band (96 kHz and above) streams.
    /// </remarks>
    private bool ChsAllocMsbBandData(DcaXllChSet c)
    {
        int ndecisamples = c.Nfreqbands > 1 ? DcaXllLimits.DeciHistoryMax : 0;
        int nchsamples = Nframesamples + ndecisamples;
        int nsamples = nchsamples * c.Nchannels * c.Nfreqbands;

        if (c.SampleBuffer[0] is null || c.SampleBuffer[0]!.Length < nsamples + ndecisamples)
            c.SampleBuffer[0] = new int[nsamples + ndecisamples];

        int ptr = ndecisamples;
        for (int i = 0; i < c.Nfreqbands; i++)
        {
            for (int j = 0; j < c.Nchannels; j++)
            {
                c.Bands[i].MsbOffset[j] = ptr;
                ptr += nchsamples;
            }
        }

        return true;
    }

    /// <summary><c>chs_alloc_lsb_band_data</c>.</summary>
    private bool ChsAllocLsbBandData(DcaXllChSet c)
    {
        int nsamples = 0;
        for (int i = 0; i < c.Nfreqbands; i++)
            if (c.Bands[i].LsbSectionSize != 0)
                nsamples += Nframesamples * c.Nchannels;

        if (nsamples == 0)
        {
            // Not a no-op: a band that had an LSB section last frame and does not have one now
            // must not keep its stale offsets.
            for (int i = 0; i < c.Nfreqbands; i++)
                for (int j = 0; j < c.Nchannels; j++)
                    c.Bands[i].LsbOffset[j] = -1;
            return true;
        }

        if (c.SampleBuffer[1] is null || c.SampleBuffer[1]!.Length < nsamples)
            c.SampleBuffer[1] = new int[nsamples];

        int ptr = 0;
        for (int i = 0; i < c.Nfreqbands; i++)
        {
            if (c.Bands[i].LsbSectionSize != 0)
            {
                for (int j = 0; j < c.Nchannels; j++)
                {
                    c.Bands[i].LsbOffset[j] = ptr;
                    ptr += Nframesamples;
                }
            }
            else
            {
                for (int j = 0; j < c.Nchannels; j++)
                    c.Bands[i].LsbOffset[j] = -1;
            }
        }

        return true;
    }

    /// <summary><c>chs_parse_band_data</c> - one segment of one band of one channel set.</summary>
    /// <remarks>
    /// A segment is split into part A and part B. Part A exists only in segment 0 and holds
    /// exactly as many samples as the predictor order, because those are the ones the predictor
    /// cannot reconstruct; part B is the rest. The two parts can use different coding
    /// parameters, which is the whole reason for the split.
    /// <para>
    /// <c>seg_common</c> says whether one set of parameters covers all channels, and when it
    /// does the parameter index is 0 for every channel rather than the channel number. Reading
    /// that wrong gives channel 0 the right samples and the rest garbage.
    /// </para>
    /// <para>
    /// The hybrid Rice path marks isolated samples by writing -1 into their slots and then uses
    /// the non-zero marker to decide, per sample, between a linear and a Rice code. That is why
    /// part B is cleared first: the marker IS the buffer.
    /// </para>
    /// </remarks>
    private bool ChsParseBandData(DcaXllChSet c, int band, int seg, int bandDataEnd)
    {
        DcaXllBand b = c.Bands[band];
        int[] msb = c.SampleBuffer[0]!;

        // Start unpacking the MSB portion of the segment.
        if (!(seg != 0 && gb.GetBits1() != 0))
        {
            c.SegCommon = (int)gb.GetBits1();

            int k = c.SegCommon != 0 ? 1 : c.Nchannels;

            for (int i = 0; i < k; i++)
            {
                c.RiceCodeFlag[i] = (int)gb.GetBits1();
                if (c.SegCommon == 0 && c.RiceCodeFlag[i] != 0 && gb.GetBits1() != 0)
                    c.BitallocHybridLinear[i] = (int)gb.GetBits(c.Nabits) + 1;
                else
                    c.BitallocHybridLinear[i] = 0;
            }

            for (int i = 0; i < k; i++)
            {
                if (seg == 0)
                {
                    c.BitallocPartA[i] = (int)gb.GetBits(c.Nabits);

                    // Adjust for the linear code.
                    if (c.RiceCodeFlag[i] == 0 && c.BitallocPartA[i] != 0)
                        c.BitallocPartA[i]++;

                    c.NsamplesPartA[i] = c.SegCommon == 0
                        ? b.AdaptPredOrder[i]
                        : b.HighestPredOrder;
                }
                else
                {
                    c.BitallocPartA[i] = 0;
                    c.NsamplesPartA[i] = 0;
                }

                c.BitallocPartB[i] = (int)gb.GetBits(c.Nabits);

                if (c.RiceCodeFlag[i] == 0 && c.BitallocPartB[i] != 0)
                    c.BitallocPartB[i]++;
            }
        }

        // Unpack the entropy codes.
        for (int i = 0; i < c.Nchannels; i++)
        {
            int k = c.SegCommon != 0 ? 0 : i;

            int partA = b.MsbOffset[i] + seg * Nsegsamples;
            int partB = partA + c.NsamplesPartA[k];
            int nsamplesPartB = Nsegsamples - c.NsamplesPartA[k];

            if (gb.GetBitsLeft() < 0)
            {
                LastError = "ran out of bits in an XLL segment";
                return false;
            }

            if (c.RiceCodeFlag[k] == 0)
            {
                GetLinearArray(msb, partA, c.NsamplesPartA[k], c.BitallocPartA[k]);
                GetLinearArray(msb, partB, nsamplesPartB, c.BitallocPartB[k]);
            }
            else
            {
                if (!GetRiceArray(msb, partA, c.NsamplesPartA[k], c.BitallocPartA[k]))
                {
                    LastError = "ran out of bits in an XLL Rice-coded part A";
                    return false;
                }

                if (c.BitallocHybridLinear[k] != 0)
                {
                    int nisosamples = (int)gb.GetBits(NsegsamplesLog2);

                    Array.Clear(msb, partB, nsamplesPartB);

                    for (int j = 0; j < nisosamples; j++)
                    {
                        int loc = (int)gb.GetBits(NsegsamplesLog2);
                        if (loc >= nsamplesPartB)
                        {
                            LastError = $"invalid XLL isolated sample location {loc}";
                            return false;
                        }
                        msb[partB + loc] = -1;
                    }

                    for (int j = 0; j < nsamplesPartB; j++)
                    {
                        msb[partB + j] = msb[partB + j] != 0
                            ? GetLinear(c.BitallocHybridLinear[k])
                            : GetRice(c.BitallocPartB[k]);
                    }
                }
                else if (!GetRiceArray(msb, partB, nsamplesPartB, c.BitallocPartB[k]))
                {
                    LastError = "ran out of bits in an XLL Rice-coded part B";
                    return false;
                }
            }
        }

        // Unpack the decimator history for frequency band 1. Index 0 is deliberately left at
        // zero: the C starts at j = 1.
        if (seg == 0 && band == 1)
        {
            int nbits = (int)gb.GetBits(5) + 1;
            for (int i = 0; i < c.Nchannels; i++)
                for (int j = 1; j < DcaXllLimits.DeciHistoryMax; j++)
                    c.DeciHistory[i, j] = gb.GetSBits(nbits);
        }

        // Start unpacking the LSB portion of the segment, which sits at the END of the band
        // data rather than after the MSBs.
        if (b.LsbSectionSize != 0)
        {
            if (!SeekBits(bandDataEnd - b.LsbSectionSize * 8))
            {
                LastError = "read past the end of the XLL band data seeking its LSB section";
                return false;
            }

            int[] lsb = c.SampleBuffer[1]!;
            for (int i = 0; i < c.Nchannels; i++)
                if (b.Nscalablelsbs[i] != 0)
                    GetArray(lsb, b.LsbOffset[i] + seg * Nsegsamples, Nsegsamples,
                             b.Nscalablelsbs[i]);
        }

        if (!SeekBits(bandDataEnd))
        {
            LastError = "read past the end of the XLL band data";
            return false;
        }

        return true;
    }

    /// <summary><c>chs_clear_band_data</c>.</summary>
    /// <remarks>
    /// A negative <paramref name="seg"/> means the whole frame rather than one segment, and it
    /// also clears the LSB widths and the authoring adjustments. The two cases are not
    /// interchangeable: the per-segment one is the error path for a corrupt segment, the
    /// whole-frame one is used when the frame is being replaced by lossy output.
    /// </remarks>
    private void ChsClearBandData(DcaXllChSet c, int band, int seg)
    {
        DcaXllBand b = c.Bands[band];
        int offset, nsamples;

        if (seg < 0)
        {
            offset = 0;
            nsamples = Nframesamples;
        }
        else
        {
            offset = seg * Nsegsamples;
            nsamples = Nsegsamples;
        }

        for (int i = 0; i < c.Nchannels; i++)
        {
            Array.Clear(c.SampleBuffer[0]!, b.MsbOffset[i] + offset, nsamples);
            if (b.LsbSectionSize != 0 && b.LsbOffset[i] >= 0)
                Array.Clear(c.SampleBuffer[1]!, b.LsbOffset[i] + offset, nsamples);
        }

        if (seg <= 0 && band != 0)
            Array.Clear(c.DeciHistory);

        if (seg < 0)
        {
            Array.Clear(b.Nscalablelsbs);
            Array.Clear(b.BitWidthAdjust);
        }
    }

    /// <summary><c>chs_filter_band_data</c> - undo prediction and decorrelation.</summary>
    /// <remarks>
    /// <para>
    /// The reflection-to-direct-form conversion is Levinson-Durbin run backwards, in place, and
    /// it reads <c>coeff</c> entries this loop has not written yet for <c>j = 0</c> - which is
    /// safe only because the inner loop runs <c>(j + 1) / 2</c> times, which is zero on the
    /// first pass. An implementation that initialised <c>coeff</c> would be no more correct and
    /// an implementation that reordered the two assignments would be wrong, because each uses
    /// the other's old value.
    /// </para>
    /// <para>
    /// The prediction loop's final index is deliberately <c>buf[j + k]</c> with <c>k</c> left at
    /// <c>order</c> from the inner loop - C's scoping makes that legal and FFmpeg relies on it.
    /// Written out, it is <c>buf[j + order]</c>.
    /// </para>
    /// </remarks>
    private void ChsFilterBandData(DcaXllChSet c, int band)
    {
        DcaXllBand b = c.Bands[band];
        int nsamples = Nframesamples;
        int[] buffer = c.SampleBuffer[0]!;

        // Inverse adaptive or fixed prediction.
        for (int i = 0; i < c.Nchannels; i++)
        {
            int bufAt = b.MsbOffset[i];
            int order = b.AdaptPredOrder[i];
            if (order > 0)
            {
                Span<int> coeff = stackalloc int[DcaXllLimits.AdaptPredOrderMax];

                for (int j = 0; j < order; j++)
                {
                    int rc = b.AdaptReflCoeff[i, j];
                    for (int k = 0; k < (j + 1) / 2; k++)
                    {
                        int tmp1 = coeff[k];
                        int tmp2 = coeff[j - k - 1];
                        coeff[k] = tmp1 + DcaMath.Mul16(rc, tmp2);
                        coeff[j - k - 1] = tmp2 + DcaMath.Mul16(rc, tmp1);
                    }
                    coeff[j] = rc;
                }

                for (int j = 0; j < nsamples - order; j++)
                {
                    long err = 0;
                    for (int k = 0; k < order; k++)
                        err += (long)buffer[bufAt + j + k] * coeff[order - k - 1];
                    buffer[bufAt + j + order] -= DcaMath.Clip23(DcaMath.Norm16(err));
                }
            }
            else
            {
                // Inverse fixed coefficient prediction: a running sum, applied as many times as
                // the order says. Second order is the sum of the first order's output, so the
                // loops nest rather than multiply a coefficient.
                for (int j = 0; j < b.FixedPredOrder[i]; j++)
                    for (int k = 1; k < nsamples; k++)
                        buffer[bufAt + k] += buffer[bufAt + k - 1];
            }
        }

        // Inverse pairwise channel decorrelation.
        if (b.DecorEnabled != 0)
        {
            for (int i = 0; i < c.Nchannels / 2; i++)
            {
                int coeff = b.DecorCoeff[i];
                if (coeff != 0)
                {
                    DcaDsp.Decor(buffer.AsSpan(b.MsbOffset[i * 2 + 1], nsamples),
                                 buffer.AsSpan(b.MsbOffset[i * 2], nsamples),
                                 coeff, nsamples);
                }
            }

            // Reorder the channel OFFSETS to the original order - the samples do not move.
            Span<int> tmp = stackalloc int[DcaXllLimits.ChannelsMax];
            for (int i = 0; i < c.Nchannels; i++)
                tmp[i] = b.MsbOffset[i];
            for (int i = 0; i < c.Nchannels; i++)
                b.MsbOffset[b.OrigOrder[i]] = tmp[i];
        }

        // Map the output channel pointers for frequency band 0.
        if (c.Nfreqbands == 1)
        {
            for (int i = 0; i < c.Nchannels; i++)
            {
                outputBuffer[c.ChRemap[i]] = buffer;
                outputOffset[c.ChRemap[i]] = b.MsbOffset[i];
            }
        }
    }

    /// <summary><c>chs_get_lsb_width</c>.</summary>
    /// <remarks>
    /// The three cases are not one formula. A fixed LSB width from the common header REPLACES
    /// the per-channel value rather than adding to it, and when both a scalable width and an
    /// authoring adjustment are present the sum is one less than their total. Collapsing this
    /// to <c>shift + adj</c> is right in one of the three cases.
    /// </remarks>
    private int ChsGetLsbWidth(DcaXllChSet c, int band, int ch)
    {
        int adj = c.Bands[band].BitWidthAdjust[ch];
        int shift = c.Bands[band].Nscalablelsbs[ch];

        if (FixedLsbWidth != 0)
            shift = FixedLsbWidth;
        else if (shift != 0 && adj != 0)
            shift += adj - 1;
        else
            shift += adj;

        return shift;
    }

    /// <summary><c>chs_assemble_msbs_lsbs</c> - shift the MSBs up and drop the LSBs in.</summary>
    private void ChsAssembleMsbsLsbs(DcaXllChSet c, int band)
    {
        DcaXllBand b = c.Bands[band];
        int nsamples = Nframesamples;
        int[] buffer = c.SampleBuffer[0]!;

        for (int ch = 0; ch < c.Nchannels; ch++)
        {
            int shift = ChsGetLsbWidth(c, band, ch);
            if (shift == 0)
                continue;

            int msbAt = b.MsbOffset[ch];
            if (b.Nscalablelsbs[ch] != 0)
            {
                int[] lsbBuffer = c.SampleBuffer[1]!;
                int lsbAt = b.LsbOffset[ch];
                int adj = b.BitWidthAdjust[ch];
                for (int n = 0; n < nsamples; n++)
                {
                    buffer[msbAt + n] = buffer[msbAt + n] * (1 << shift)
                                      + (lsbBuffer[lsbAt + n] << adj);
                }
            }
            else
            {
                for (int n = 0; n < nsamples; n++)
                    buffer[msbAt + n] *= 1 << shift;
            }
        }
    }

    /// <summary><c>chs_assemble_freq_bands</c> - interleave the two bands back to one rate.</summary>
    private bool ChsAssembleFreqBands(DcaXllChSet c)
    {
        int nsamples = Nframesamples;

        int needed = 2 * nsamples * c.Nchannels;
        if (c.SampleBuffer[2] is null || c.SampleBuffer[2]!.Length < needed)
            c.SampleBuffer[2] = new int[needed];

        int[] buffer = c.SampleBuffer[0]!;
        int[] dst = c.SampleBuffer[2]!;

        int ptr = 0;
        for (int ch = 0; ch < c.Nchannels; ch++)
        {
            int band0 = c.Bands[0].MsbOffset[ch];
            int band1 = c.Bands[1].MsbOffset[ch];

            // Copy the decimator history into the headroom in front of band 0.
            for (int i = 0; i < DcaXllLimits.DeciHistoryMax; i++)
                buffer[band0 - DcaXllLimits.DeciHistoryMax + i] = c.DeciHistory[ch, i];

            DcaDsp.AssembleFreqBands(dst, ptr, buffer, band0, buffer, band1,
                                     DcaData.XllBandCoeff, nsamples);

            outputBuffer[c.ChRemap[ch]] = dst;
            outputOffset[c.ChRemap[ch]] = ptr;
            ptr += nsamples * 2;
        }

        return true;
    }

    // ---- Frame parsing -------------------------------------------------------------------

    /// <summary><c>parse_common_header</c>.</summary>
    private DcaXllStatus ParseCommonHeader()
    {
        if (gb.GetBits(32) != DcaSyncwords.Xll)
        {
            // Not an error: this is how a packet that begins inside a PBR smoothing period
            // announces itself, and the caller recovers by seeking to the next sync word.
            LastError = "no XLL sync word at the start of this asset";
            return DcaXllStatus.Again;
        }

        int streamVer = (int)gb.GetBits(4) + 1;
        if (streamVer > 1)
        {
            LastError = $"XLL stream version {streamVer}, which FFmpeg does not implement either";
            return DcaXllStatus.Invalid;
        }

        int headerSize = (int)gb.GetBits(8) + 1;

        if (!CheckCrc(32, headerSize * 8))
        {
            LastError = "invalid XLL common header checksum";
            return DcaXllStatus.Invalid;
        }

        int frameSizeNbits = (int)gb.GetBits(5) + 1;

        FrameSize = (int)gb.GetBits(frameSizeNbits);
        if (FrameSize < 0 || FrameSize >= DcaXllLimits.PbrBufferMax)
        {
            LastError = $"invalid XLL frame size ({FrameSize} bytes)";
            return DcaXllStatus.Invalid;
        }
        FrameSize++;

        Nchsets = (int)gb.GetBits(4) + 1;
        if (Nchsets > DcaXllLimits.ChSetsMax)
        {
            LastError = $"{Nchsets} XLL channel sets, which the format caps at "
                      + $"{DcaXllLimits.ChSetsMax}";
            return DcaXllStatus.Invalid;
        }

        int nframesegsLog2 = (int)gb.GetBits(4);
        Nframesegs = 1 << nframesegsLog2;
        if (Nframesegs > 1024)
        {
            LastError = $"too many segments per XLL frame ({Nframesegs})";
            return DcaXllStatus.Invalid;
        }

        NsegsamplesLog2 = (int)gb.GetBits(4);
        if (NsegsamplesLog2 == 0)
        {
            LastError = "too few samples per XLL segment";
            return DcaXllStatus.Invalid;
        }
        Nsegsamples = 1 << NsegsamplesLog2;
        if (Nsegsamples > 512)
        {
            LastError = $"too many samples per XLL segment ({Nsegsamples})";
            return DcaXllStatus.Invalid;
        }

        NframesamplesLog2 = NsegsamplesLog2 + nframesegsLog2;
        Nframesamples = 1 << NframesamplesLog2;
        if (Nframesamples > 65536)
        {
            LastError = $"too many samples per XLL frame ({Nframesamples})";
            return DcaXllStatus.Invalid;
        }

        SegSizeNbits = (int)gb.GetBits(5) + 1;

        // 0 - no CRC16 within a band; 1 - at the end of MSB0; 2 - at the end of MSB0 and LSB0;
        // 3 - at the end of MSB0, LSB0 and the other frequency bands.
        BandCrcPresent = (int)gb.GetBits(2);

        ScalableLsbs = (int)gb.GetBits1();
        ChMaskNbits = (int)gb.GetBits(5) + 1;
        FixedLsbWidth = ScalableLsbs != 0 ? (int)gb.GetBits(4) : 0;

        // Reserved, byte align, header CRC16.
        if (!SeekBits(headerSize * 8))
        {
            LastError = "read past the end of the XLL common header";
            return DcaXllStatus.Invalid;
        }

        return DcaXllStatus.Ok;
    }

    /// <summary><c>is_hier_dmix_chset</c>.</summary>
    private static bool IsHierDmixChset(DcaXllChSet c) =>
        c.PrimaryChset == 0 && c.DmixEmbedded != 0 && c.HierChset != 0;

    /// <summary><c>find_next_hier_dmix_chset</c>.</summary>
    private DcaXllChSet? FindNextHierDmixChset(DcaXllChSet c)
    {
        if (c.HierChset == 0)
            return null;

        int index = Array.IndexOf(Chset, c);
        for (int i = index + 1; i < Nchsets; i++)
            if (IsHierDmixChset(Chset[i]))
                return Chset[i];

        return null;
    }

    /// <summary><c>prescale_down_mix</c>.</summary>
    /// <remarks>
    /// Note the precisions, which are not interchangeable: the forward scale folds with
    /// <c>mul15</c>, the inverse with <c>mul16</c>, and each coefficient takes a <c>mul16</c>
    /// by the inverse scale followed by a <c>mul15</c> by the next set's forward scale. The two
    /// scale arrays are indexed differently too - <c>o-&gt;dmix_scale[i]</c> against
    /// <c>o-&gt;dmix_scale[c-&gt;hier_ofs + j]</c> - because one indexes the channels being
    /// mixed down and the other the channels being mixed into.
    /// </remarks>
    private static void PrescaleDownMix(DcaXllChSet c, DcaXllChSet o)
    {
        int coeffAt = 0;

        for (int i = 0; i < c.HierOfs; i++)
        {
            int scale = o.DmixScale[i];
            int scaleInv = o.DmixScaleInv[i];
            c.DmixScale[i] = DcaMath.Mul15(c.DmixScale[i], scale);
            c.DmixScaleInv[i] = DcaMath.Mul16(c.DmixScaleInv[i], scaleInv);
            for (int j = 0; j < c.Nchannels; j++)
            {
                int coeff = DcaMath.Mul16(c.DmixCoeff[coeffAt], scaleInv);
                c.DmixCoeff[coeffAt++] = DcaMath.Mul15(coeff, o.DmixScale[c.HierOfs + j]);
            }
        }
    }

    /// <summary><c>parse_sub_headers</c>.</summary>
    private bool ParseSubHeaders(in DcaExssAsset asset)
    {
        Nfreqbands = 0;
        Nchannels = 0;
        Nreschsets = 0;

        for (int i = 0; i < Nchsets; i++)
        {
            DcaXllChSet c = Chset[i];
            c.HierOfs = Nchannels;
            if (!ChsParseHeader(c, asset))
                return false;
            if (c.Nfreqbands > Nfreqbands)
                Nfreqbands = c.Nfreqbands;
            if (c.HierChset != 0)
                Nchannels += c.Nchannels;
            if (c.ResidualEncode != (1 << c.Nchannels) - 1)
                Nreschsets++;
        }

        // Pre-scale the downmixing coefficients for every non-primary channel set, from the LAST
        // set backwards: each one's scales are folded with the next one DOWN the hierarchy, so
        // running forwards would fold already-folded values.
        for (int i = Nchsets - 1; i > 0; i--)
        {
            DcaXllChSet c = Chset[i];
            if (IsHierDmixChset(c))
            {
                DcaXllChSet? o = FindNextHierDmixChset(c);
                if (o is not null)
                    PrescaleDownMix(c, o);
            }
        }

        // How many channel sets to decode at all. A layout request does not merely fold the
        // output here - it decides how much of the stream is read, which is why asking for
        // stereo from a 7.1 XLL stream is cheaper and not just quieter.
        Nactivechsets = RequestChannelLayout switch
        {
            DcaSpeakerLayout.Stereo => 1,
            DcaSpeakerLayout.Layout5Point0 or DcaSpeakerLayout.Layout5Point1 =>
                Chset[0].Nchannels < 5 && Nchsets > 1 ? 2 : 1,
            _ => Nchsets,
        };

        return true;
    }

    /// <summary><c>parse_navi_table</c> - the per-segment size table.</summary>
    private bool ParseNaviTable()
    {
        int naviNb = Nfreqbands * Nframesegs * Nchsets;
        if (naviNb > 1024)
        {
            LastError = $"too many XLL NAVI entries ({naviNb})";
            return false;
        }

        if (navi.Length < naviNb)
            navi = new int[naviNb];
        naviCount = naviNb;

        int naviPos = gb.GetBitsCount();
        int naviPtr = 0;
        for (int band = 0; band < Nfreqbands; band++)
        {
            for (int seg = 0; seg < Nframesegs; seg++)
            {
                for (int chs = 0; chs < Nchsets; chs++)
                {
                    int size = 0;
                    if (Chset[chs].Nfreqbands > band)
                    {
                        size = (int)gb.GetBits(SegSizeNbits);
                        if (size < 0 || size >= FrameSize)
                        {
                            LastError = $"invalid XLL NAVI segment size ({size} bytes)";
                            return false;
                        }
                        size++;
                    }
                    navi[naviPtr++] = size;
                }
            }
        }

        // Byte align, then the CRC16.
        gb.SkipBits(-gb.GetBitsCount() & 7);
        gb.SkipBits(16);

        if (!CheckCrc(naviPos, gb.GetBitsCount()))
        {
            LastError = "invalid XLL NAVI checksum";
            return false;
        }

        return true;
    }

    /// <summary><c>parse_band_data</c>.</summary>
    /// <remarks>
    /// The NAVI table makes every segment independently addressable, and this loop uses that in
    /// two ways: a segment belonging to an inactive channel set is SKIPPED without being parsed,
    /// and a segment that fails to parse is cleared and skipped past rather than aborting the
    /// frame. The second is what lets a damaged stream keep playing, and it means a parse
    /// failure here is not necessarily visible in the output.
    /// </remarks>
    private bool ParseBandData()
    {
        for (int chs = 0; chs < Nactivechsets; chs++)
        {
            if (!ChsAllocMsbBandData(Chset[chs]) || !ChsAllocLsbBandData(Chset[chs]))
                return false;
        }

        int naviPos = gb.GetBitsCount();
        int naviPtr = 0;
        for (int band = 0; band < Nfreqbands; band++)
        {
            for (int seg = 0; seg < Nframesegs; seg++)
            {
                for (int chs = 0; chs < Nchsets; chs++)
                {
                    DcaXllChSet c = Chset[chs];
                    if (c.Nfreqbands > band)
                    {
                        naviPos += navi[naviPtr] * 8;
                        if (naviPos > gb.SizeInBits)
                        {
                            LastError = "invalid XLL NAVI position";
                            return false;
                        }
                        if (chs < Nactivechsets && !ChsParseBandData(c, band, seg, naviPos))
                            ChsClearBandData(c, band, seg);
                        gb.SkipBits(naviPos - gb.GetBitsCount());
                    }
                    naviPtr++;
                }
            }
        }

        return true;
    }

    /// <summary><c>parse_frame</c>.</summary>
    private DcaXllStatus ParseFrameAt(byte[] data, int offset, int size, in DcaExssAsset asset)
    {
        gb.InitGetBits8(data, offset, size);

        DcaXllStatus status = ParseCommonHeader();
        if (status != DcaXllStatus.Ok)
            return status;

        if (!ParseSubHeaders(asset) || !ParseNaviTable() || !ParseBandData())
            return DcaXllStatus.Invalid;

        XSyncwordPresent = 0;
        XImaxSyncwordPresent = 0;

        if (FrameSize * 8 > ((gb.GetBitsCount() + 31) & ~31))
        {
            gb.SkipBits(-gb.GetBitsCount() & 31);
            uint extradataSyncword = gb.ShowBits(32);

            if (extradataSyncword == DcaSyncwords.XllX)
                XSyncwordPresent = 1;
            else if ((extradataSyncword >> 1) == (DcaSyncwords.XllXImax >> 1))
                XImaxSyncwordPresent = 1;
        }

        if (!SeekBits(FrameSize * 8))
        {
            LastError = "read past the end of the XLL frame";
            return DcaXllStatus.Invalid;
        }

        return DcaXllStatus.Ok;
    }

    /// <summary><c>clear_pbr</c>.</summary>
    private void ClearPbr()
    {
        pbrLength = 0;
        pbrDelay = 0;
    }

    /// <summary><c>copy_to_pbr</c>.</summary>
    private bool CopyToPbr(byte[] data, int offset, int size, int delay)
    {
        if (size > DcaXllLimits.PbrBufferMax)
        {
            LastError = $"XLL frame of {size} bytes does not fit the PBR buffer";
            return false;
        }

        pbrBuffer ??= new byte[DcaXllLimits.PbrBufferMax + 64];

        Array.Copy(data, offset, pbrBuffer, 0, size);
        Array.Clear(pbrBuffer, size, 64);
        pbrLength = size;
        pbrDelay = delay;
        return true;
    }

    /// <summary><c>parse_frame_no_pbr</c>.</summary>
    private DcaXllStatus ParseFrameNoPbr(byte[] data, int offset, int size,
                                         in DcaExssAsset asset)
    {
        DcaXllStatus status = ParseFrameAt(data, offset, size, asset);

        // If the packet did not start with a sync word we have jumped into the middle of a PBR
        // smoothing period.
        if (status == DcaXllStatus.Again && asset.XllSyncPresent != 0
            && asset.XllSyncOffset < size)
        {
            offset += asset.XllSyncOffset;
            size -= asset.XllSyncOffset;

            // With a decoding delay set, buffer the frame and report Again: the caller is
            // expected to fall back to the lossy core, or mute, until the delay expires.
            if (asset.XllDelayNframes > 0)
            {
                if (!CopyToPbr(data, offset, size, asset.XllDelayNframes))
                    return DcaXllStatus.Invalid;
                return DcaXllStatus.Again;
            }

            status = ParseFrameAt(data, offset, size, asset);
        }

        if (status != DcaXllStatus.Ok)
            return status;

        if (FrameSize > size)
        {
            LastError = $"XLL frame declares {FrameSize} bytes in an asset of {size}";
            return DcaXllStatus.Invalid;
        }

        // The decoder did not consume the whole packet, so a PBR smoothing period starts here.
        if (FrameSize < size && !CopyToPbr(data, offset + FrameSize, size - FrameSize, 0))
            return DcaXllStatus.Invalid;

        return DcaXllStatus.Ok;
    }

    /// <summary><c>parse_frame_pbr</c>.</summary>
    /// <remarks>
    /// The delay countdown is the C's <c>if (s->pbr_delay > 0 &amp;&amp; --s->pbr_delay)</c>,
    /// which decrements and then tests the NEW value - so a delay of 1 decrements to 0 and falls
    /// through to parse this frame, rather than waiting one more. Translating it as "decrement,
    /// then test for greater than zero" loses a frame on every recovery.
    /// </remarks>
    private DcaXllStatus ParseFramePbr(byte[] data, int offset, int size,
                                       in DcaExssAsset asset)
    {
        if (size > DcaXllLimits.PbrBufferMax - pbrLength)
        {
            LastError = "the XLL PBR buffer is full";
            ClearPbr();
            return DcaXllStatus.Invalid;
        }

        Array.Copy(data, offset, pbrBuffer!, pbrLength, size);
        pbrLength += size;
        Array.Clear(pbrBuffer!, pbrLength, 64);

        if (pbrDelay > 0 && --pbrDelay != 0)
            return DcaXllStatus.Again;

        DcaXllStatus status = ParseFrameAt(pbrBuffer!, 0, pbrLength, asset);
        if (status != DcaXllStatus.Ok)
        {
            // All PBR state is thrown out on failure. The C says it could be smarter and is not.
            ClearPbr();
            return status;
        }

        if (FrameSize > pbrLength)
        {
            LastError = $"XLL frame declares {FrameSize} bytes with {pbrLength} buffered";
            ClearPbr();
            return DcaXllStatus.Invalid;
        }

        if (FrameSize == pbrLength)
        {
            ClearPbr();
        }
        else
        {
            pbrLength -= FrameSize;
            Array.Copy(pbrBuffer!, FrameSize, pbrBuffer!, 0, pbrLength);
        }

        return DcaXllStatus.Ok;
    }

    /// <summary><c>ff_dca_xll_parse</c>.</summary>
    /// <param name="exssOffset">
    /// Where the extension substream starts in <paramref name="data"/>.
    /// </param>
    /// <remarks>
    /// <see cref="DcaExssAsset.XllOffset"/> is relative to the start of the EXSS substream, not
    /// to the start of the packet - the C hides that by advancing its own <c>input</c> pointer
    /// past the core frame before calling, so the addition happens in the caller. Taking the
    /// base offset explicitly here is the same arithmetic made visible; leaving it out looks for
    /// the sync word inside the core frame and reports a stream with no XLL in it.
    /// </remarks>
    public DcaXllStatus Parse(byte[] data, int exssOffset, in DcaExssAsset asset)
    {
        LastError = "";

        if (hdStreamId != asset.HdStreamId)
        {
            ClearPbr();
            hdStreamId = asset.HdStreamId;
        }

        int at = exssOffset + asset.XllOffset;

        return pbrLength != 0
            ? ParseFramePbr(data, at, asset.XllSize, asset)
            : ParseFrameNoPbr(data, at, asset.XllSize, asset);
    }

    // ---- Filtering -----------------------------------------------------------------------

    /// <summary><c>undo_down_mix</c>.</summary>
    private void UndoDownMix(DcaXllChSet o, int band)
    {
        int nchannels = 0;
        int coeffAt = 0;

        for (int i = 0; i < Nactivechsets; i++)
        {
            DcaXllChSet c = Chset[i];
            if (c.HierChset == 0)
                continue;

            for (int j = 0; j < c.Nchannels; j++)
            {
                for (int k = 0; k < o.Nchannels; k++)
                {
                    int coeff = o.DmixCoeff[coeffAt++];
                    if (coeff == 0)
                        continue;

                    DcaDsp.DmixSub(
                        c.SampleBuffer[0]!.AsSpan(c.Bands[band].MsbOffset[j], Nframesamples),
                        o.SampleBuffer[0]!.AsSpan(o.Bands[band].MsbOffset[k], Nframesamples),
                        coeff, Nframesamples);

                    if (band != 0)
                        DmixSubDeciHistory(c, j, o, k, coeff);
                }
            }

            nchannels += c.Nchannels;
            if (nchannels >= o.HierOfs)
                break;
        }
    }

    /// <summary>
    /// The decimator-history half of <c>undo_down_mix</c>, split out because the history is a
    /// rectangular array here rather than a row of pointers.
    /// </summary>
    private static void DmixSubDeciHistory(DcaXllChSet c, int cch, DcaXllChSet o, int och,
                                           int coeff)
    {
        for (int n = 0; n < DcaXllLimits.DeciHistoryMax; n++)
            c.DeciHistory[cch, n] -= DcaMath.Mul15(o.DeciHistory[och, n], coeff);
    }

    /// <summary><c>scale_down_mix</c>.</summary>
    /// <remarks>
    /// The <c>scale != (1 &lt;&lt; 15)</c> test is not an optimisation to taste: unity in Q15 is
    /// 32768 and <c>mul15</c> of it is not exactly the identity, so applying it anyway would
    /// change samples that must not change.
    /// </remarks>
    private void ScaleDownMix(DcaXllChSet o, int band)
    {
        int nchannels = 0;

        for (int i = 0; i < Nactivechsets; i++)
        {
            DcaXllChSet c = Chset[i];
            if (c.HierChset == 0)
                continue;

            for (int j = 0; j < c.Nchannels; j++)
            {
                int scale = o.DmixScale[nchannels++];
                if (scale == 1 << 15)
                    continue;

                DcaDsp.DmixScale(
                    c.SampleBuffer[0]!.AsSpan(c.Bands[band].MsbOffset[j], Nframesamples),
                    scale, Nframesamples);

                if (band != 0)
                    for (int n = 0; n < DcaXllLimits.DeciHistoryMax; n++)
                        c.DeciHistory[j, n] = DcaMath.Mul15(c.DeciHistory[j, n], scale);
            }

            if (nchannels >= o.HierOfs)
                break;
        }
    }

    /// <summary>
    /// <c>force_lossy_output</c> - clear every band and let the core supply these channels.
    /// </summary>
    private void ForceLossyOutput(DcaXllChSet c, DcaCore core)
    {
        for (int band = 0; band < c.Nfreqbands; band++)
            ChsClearBandData(c, band, -1);

        for (int ch = 0; ch < c.Nchannels; ch++)
        {
            if ((c.ResidualEncode & (1 << ch)) == 0)
                continue;
            if (core.MapSpkr(c.ChRemap[ch]) < 0)
                continue;
            c.ResidualEncode &= ~(1 << ch);
        }
    }

    /// <summary>
    /// <c>combine_residual_frame</c> - add the core's samples into the residual channels.
    /// </summary>
    /// <remarks>
    /// This is the one place the two layers meet, and it is why the core must have been filtered
    /// through its FIXED point path: these are integer adds against the core's own integer
    /// samples, and a float core would make the result merely close rather than lossless.
    /// <para>
    /// The shift that brings the core down to XLL's width is
    /// <c>24 - pcm_bit_res + lsb_width</c>, with a rounding term of half an LSB - and the
    /// rounding term is zero when the shift is zero, which is not the same as adding
    /// <c>1 &lt;&lt; -1</c>.
    /// </para>
    /// </remarks>
    private bool CombineResidualFrame(DcaXllChSet c, DcaCore core, bool corePresent)
    {
        int nsamples = Nframesamples;

        if (!corePresent)
        {
            LastError = "XLL residual encoded channels are present without a core sub-stream";
            return false;
        }

        if (c.Freq != core.OutputRate)
        {
            LastError = $"sample rate mismatch between the core ({core.OutputRate} Hz) and "
                      + $"XLL ({c.Freq} Hz)";
            return false;
        }

        if (nsamples != core.Npcmsamples)
        {
            LastError = $"samples per frame mismatch between the core ({core.Npcmsamples}) and "
                      + $"XLL ({nsamples})";
            return false;
        }

        // If this channel set is downmixed, the core's pre-scaling has to come off before the
        // residual is added, because the residual itself is not scaled.
        DcaXllChSet? o = FindNextHierDmixChset(c);

        for (int ch = 0; ch < c.Nchannels; ch++)
        {
            if ((c.ResidualEncode & (1 << ch)) != 0)
                continue;

            int spkr = core.MapSpkr(c.ChRemap[ch]);
            if (spkr < 0)
            {
                LastError = $"XLL residual encoded channel {c.ChRemap[ch]} references a core "
                          + "channel that is not present";
                return false;
            }

            int shift = 24 - c.PcmBitRes + ChsGetLsbWidth(c, 0, ch);
            if (shift > 24)
            {
                LastError = $"invalid core shift ({shift} bits) combining an XLL residual";
                return false;
            }

            int round = shift > 0 ? 1 << (shift - 1) : 0;

            ReadOnlySpan<int> src = core.RawFixedPlane(spkr, nsamples);
            int[] buffer = c.SampleBuffer[0]!;
            int dstAt = c.Bands[0].MsbOffset[ch];

            if (o is not null)
            {
                int scaleInv = o.DmixScaleInv[c.HierOfs + ch];
                for (int n = 0; n < nsamples; n++)
                    buffer[dstAt + n] +=
                        DcaMath.Clip23((DcaMath.Mul16(src[n], scaleInv) + round) >> shift);
            }
            else
            {
                for (int n = 0; n < nsamples; n++)
                    buffer[dstAt + n] += (src[n] + round) >> shift;
            }
        }

        return true;
    }

    /// <summary><c>ff_dca_xll_filter_frame</c> - the decoded frame, as integer planes.</summary>
    /// <param name="core">The core decoder, already filtered through its fixed point path.</param>
    /// <param name="corePresent">Whether this packet carried a core sub-stream at all.</param>
    /// <param name="recovery">
    /// <c>dca->packet &amp; DCA_PACKET_RECOVERY</c>: the stream is being resynchronised, so
    /// lossy downmixed output is forced for this frame.
    /// </param>
    public bool FilterFrame(DcaCore core, bool corePresent, bool recovery)
    {
        DcaXllChSet p = Chset[0];

        if (recovery)
        {
            for (int i = 0; i < Nchsets; i++)
            {
                if (i < Nactivechsets)
                    ForceLossyOutput(Chset[i], core);
                if (Chset[i].PrimaryChset == 0)
                    Chset[i].DmixEmbedded = 0;
            }

            ScalableLsbs = 0;
            FixedLsbWidth = 0;
        }

        // Filter the frequency bands of the active channel sets.
        OutputMask = 0;
        for (int i = 0; i < Nactivechsets; i++)
        {
            DcaXllChSet c = Chset[i];
            ChsFilterBandData(c, 0);

            if (c.ResidualEncode != (1 << c.Nchannels) - 1
                && !CombineResidualFrame(c, core, corePresent))
            {
                return false;
            }

            if (ScalableLsbs != 0)
                ChsAssembleMsbsLsbs(c, 0);

            if (c.Nfreqbands > 1)
            {
                ChsFilterBandData(c, 1);
                ChsAssembleMsbsLsbs(c, 1);
            }

            OutputMask |= c.ChMask;
        }

        // Undo the hierarchical downmix, or apply its scaling for a set that is not being
        // decoded. The break in the inactive branch is the C's and is not a continue: once an
        // inactive downmixed set has been scaled, the ones beyond it are not reached.
        for (int i = 1; i < Nchsets; i++)
        {
            DcaXllChSet c = Chset[i];
            if (!IsHierDmixChset(c))
                continue;

            if (i >= Nactivechsets)
            {
                for (int j = 0; j < c.Nfreqbands; j++)
                    if (c.Bands[j].DmixEmbedded != 0)
                        ScaleDownMix(c, j);
                break;
            }

            for (int j = 0; j < c.Nfreqbands; j++)
                if (c.Bands[j].DmixEmbedded != 0)
                    UndoDownMix(c, j);
        }

        // Assemble the frequency bands of the active channel sets.
        if (Nfreqbands > 1)
        {
            for (int i = 0; i < Nactivechsets; i++)
                if (!ChsAssembleFreqBands(Chset[i]))
                    return false;
        }

        // Normalise to a regular 5.1 layout when downmixing: a side surround channel becomes the
        // plain surround it stands in for, by moving the POINTER rather than the samples.
        if (RequestChannelLayout != 0)
        {
            if ((OutputMask & DcaSpeakerMask.Lss) != 0)
            {
                outputBuffer[(int)DcaSpeaker.Ls] = outputBuffer[(int)DcaSpeaker.Lss];
                outputOffset[(int)DcaSpeaker.Ls] = outputOffset[(int)DcaSpeaker.Lss];
                OutputMask = (OutputMask & ~DcaSpeakerMask.Lss) | DcaSpeakerMask.Ls;
            }
            if ((OutputMask & DcaSpeakerMask.Rss) != 0)
            {
                outputBuffer[(int)DcaSpeaker.Rs] = outputBuffer[(int)DcaSpeaker.Rss];
                outputOffset[(int)DcaSpeaker.Rs] = outputOffset[(int)DcaSpeaker.Rss];
                OutputMask = (OutputMask & ~DcaSpeakerMask.Rss) | DcaSpeakerMask.Rs;
            }
        }

        int requestMask =
            RequestChannelLayout == DcaSpeakerLayout.Stereo
            && DcaSpeakerLayout.HasStereo(OutputMask)
            && p.DmixEmbedded != 0
            && (p.DmixType == (int)DcaDmixType.LoRo || p.DmixType == (int)DcaDmixType.LtRt)
                ? DcaSpeakerLayout.Stereo
                : OutputMask;

        nchannelsOut = DcaCore.SetChannelLayoutInto(chRemap, requestMask);
        if (nchannelsOut == 0)
        {
            LastError = $"no channel layout for XLL output mask 0x{requestMask:X}";
            return false;
        }

        SampleRate = p.Freq << (Nfreqbands - 1);
        StorageBitRes = p.StorageBitRes;
        PcmBitRes = p.PcmBitRes;

        int shift;
        switch (p.StorageBitRes)
        {
            case 16:
                shift = 16 - p.PcmBitRes;
                break;
            case 20:
            case 24:
                shift = 24 - p.PcmBitRes;
                break;
            default:
                LastError = $"{p.StorageBitRes}-bit XLL storage resolution has no output format";
                return false;
        }

        Npcmsamples = Nframesamples << (Nfreqbands - 1);

        // Downmix the primary channel set to stereo.
        if (requestMask != OutputMask)
            DownmixToStereoFixed(p.DmixCoeff, Npcmsamples, OutputMask);

        for (int i = 0; i < nchannelsOut; i++)
        {
            int spkr = chRemap[i];
            int[] samples = outputBuffer[spkr]!;
            int at = outputOffset[spkr];

            if (planes[spkr] is null || planes[spkr]!.Length < Npcmsamples)
                planes[spkr] = new int[Npcmsamples];
            int[] plane = planes[spkr]!;

            if (p.StorageBitRes == 16)
            {
                // s16p in the C. The value is kept in an int here, scaled the same way the s32p
                // branch scales its own, so one accessor serves both; the 16-bit clip is still
                // applied first, because it is part of the format and not of the container.
                for (int k = 0; k < Npcmsamples; k++)
                {
                    int v = samples[at + k] * (1 << shift);
                    plane[k] = Math.Clamp(v, short.MinValue, short.MaxValue) * (1 << 16);
                }
            }
            else
            {
                for (int k = 0; k < Npcmsamples; k++)
                    plane[k] = DcaMath.Clip23(samples[at + k] * (1 << shift)) * (1 << 8);
            }
        }

        return true;
    }

    /// <summary>
    /// <c>ff_dca_downmix_to_stereo_fixed</c> over XLL's output pointers.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DcaCore"/>'s copy of the same C function because the sample
    /// planes it walks are XLL's, which live in whichever buffer last wrote each speaker. The
    /// coefficient layout and the <c>pos</c> offset are identical and carry the same warning.
    /// </remarks>
    private void DownmixToStereoFixed(int[] coeff, int nsamples, int chMask)
    {
        int maxSpkr = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)chMask);
        int coeffR = System.Numerics.BitOperations.PopCount((uint)chMask);

        if (!DcaSpeakerLayout.HasStereo(chMask))
            throw new InvalidOperationException(
                $"a stereo downmix was requested from an XLL layout with no L/R pair "
              + $"(0x{chMask:X})");

        int pos = chMask & DcaSpeakerMask.C;

        DcaDsp.DmixScale(Plane(DcaSpeaker.L, nsamples), coeff[pos], nsamples);
        DcaDsp.DmixScale(Plane(DcaSpeaker.R, nsamples), coeff[coeffR + pos + 1], nsamples);

        int l = 0, r = coeffR;
        for (int spkr = 0; spkr <= maxSpkr; spkr++)
        {
            if ((chMask & (1 << spkr)) == 0)
                continue;

            if (coeff[l] != 0 && spkr != (int)DcaSpeaker.L)
            {
                DcaDsp.DmixAdd(Plane(DcaSpeaker.L, nsamples), Plane(spkr, nsamples),
                               coeff[l], nsamples);
            }

            if (coeff[r] != 0 && spkr != (int)DcaSpeaker.R)
            {
                DcaDsp.DmixAdd(Plane(DcaSpeaker.R, nsamples), Plane(spkr, nsamples),
                               coeff[r], nsamples);
            }

            l++;
            r++;
        }
    }

    private Span<int> Plane(DcaSpeaker spkr, int nsamples) => Plane((int)spkr, nsamples);

    private Span<int> Plane(int spkr, int nsamples) =>
        outputBuffer[spkr]!.AsSpan(outputOffset[spkr], nsamples);

    /// <summary><c>ff_dca_xll_flush</c>.</summary>
    public void Flush() => ClearPbr();
}

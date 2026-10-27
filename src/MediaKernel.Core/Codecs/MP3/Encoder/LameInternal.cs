// Translated from lame-3.100/libmp3lame/util.h - lame_internal_flags and the state structs it
// holds. Fields are in the C's declared order.
//
// SHAPE, AND WHY IT DIFFERS FROM gr_info. Nothing in LAME ever copies one of these structs by
// value: they are reached only through gfc->, and no sizeof(EncStateVar_t) or equivalent appears
// anywhere in the source. So the large buffers inside them are C# arrays allocated once in
// Allocate(), which costs nothing per frame and keeps an 80 KB context off the copy path
// entirely. gr_info is the exception and is a full value type - see LameSideInfo.cs, where
// takehiro.c's four memcpys make that mandatory.
//
// lame_internal_flags itself is a sealed class, one per encoder, so that no call site can copy
// it by accident. Its nested state lives in struct fields, which is the C's inline storage: a
// field access through a class reference, gfc.Cfg.SamplerateOut, reads and writes in place.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>Bit_stream_struc</c> - the output bit buffer.</summary>
internal struct BitStreamStruc
{
    /// <summary><c>buf</c> - the bit stream buffer.</summary>
    public byte[]? Buf;

    /// <summary><c>buf_size</c> - in bytes.</summary>
    public int BufSize;

    /// <summary><c>totbit</c> - bits written so far.</summary>
    public int Totbit;

    /// <summary><c>buf_byte_idx</c>.</summary>
    public int BufByteIdx;

    /// <summary><c>buf_bit_idx</c>.</summary>
    public int BufBitIdx;
}

/// <summary>
/// <c>VBR_seek_info_t</c> - the Xing seek table. Out of scope: libmp3lame.c:137 calls
/// <c>lame_set_bWriteVbrTag(gfp, 0)</c>, so nothing here is ever written. Translated for layout
/// fidelity, and because <c>lame_init_params</c> touches it.
/// </summary>
internal struct VbrSeekInfo
{
    /// <summary><c>sum</c>.</summary>
    public int Sum;

    /// <summary><c>seen</c>.</summary>
    public int Seen;

    /// <summary><c>want</c>.</summary>
    public int Want;

    /// <summary><c>pos</c>.</summary>
    public int Pos;

    /// <summary><c>size</c>.</summary>
    public int Size;

    /// <summary><c>bag</c>.</summary>
    public int[]? Bag;

    /// <summary><c>nVbrNumFrames</c>.</summary>
    public uint NVbrNumFrames;

    /// <summary><c>nBytesWritten</c>.</summary>
    public ulong NBytesWritten;

    /// <summary><c>TotalFrameSize</c>.</summary>
    public uint TotalFrameSize;
}

/// <summary><c>ATH_t</c> - everything about the absolute threshold of hearing.</summary>
internal struct AthT
{
    /// <summary><c>use_adjust</c> - which auto-adjustment method.</summary>
    public int UseAdjust;

    /// <summary><c>aa_sensitivity_p</c>.</summary>
    public float AaSensitivityP;

    /// <summary><c>adjust_factor</c> - 1 means no lowering.</summary>
    public float AdjustFactor;

    /// <summary><c>adjust_limit</c>.</summary>
    public float AdjustLimit;

    /// <summary><c>decay</c>.</summary>
    public float Decay;

    /// <summary><c>floor</c> - the lowest ATH value.</summary>
    public float Floor;

    /// <summary><c>l[SBMAX_l]</c>.</summary>
    public float[]? L;

    /// <summary><c>s[SBMAX_s]</c>.</summary>
    public float[]? S;

    /// <summary><c>psfb21[PSFB21]</c>.</summary>
    public float[]? Psfb21;

    /// <summary><c>psfb12[PSFB12]</c>.</summary>
    public float[]? Psfb12;

    /// <summary><c>cb_l[CBANDS]</c>.</summary>
    public float[]? CbL;

    /// <summary><c>cb_s[CBANDS]</c>.</summary>
    public float[]? CbS;

    /// <summary><c>eql_w[BLKSIZE / 2]</c> - equal loudness weights.</summary>
    public float[]? EqlW;

    public void Allocate()
    {
        L = new float[LameConstants.SbMaxL];
        S = new float[LameConstants.SbMaxS];
        Psfb21 = new float[LameConstants.Psfb21];
        Psfb12 = new float[LameConstants.Psfb12];
        CbL = new float[LameConstants.CBands];
        CbS = new float[LameConstants.CBands];
        EqlW = new float[LameConstants.BlkSize / 2];
    }
}

/// <summary>
/// <c>PsyConst_CB2SB_t</c> - the mapping from the model's partition bands to scalefactor bands,
/// precomputed once per encoder.
/// </summary>
internal struct PsyConstCb2Sb
{
    /// <summary>The wider of the two scalefactor band counts - the C's <c>Max(SBMAX_l, SBMAX_s)</c>.</summary>
    public const int SbMaxEither = LameConstants.SbMaxL > LameConstants.SbMaxS
        ? LameConstants.SbMaxL
        : LameConstants.SbMaxS;

    /// <summary><c>masking_lower[CBANDS]</c>.</summary>
    public float[]? MaskingLower;

    /// <summary><c>minval[CBANDS]</c>.</summary>
    public float[]? Minval;

    /// <summary><c>rnumlines[CBANDS]</c>.</summary>
    public float[]? Rnumlines;

    /// <summary><c>mld_cb[CBANDS]</c>.</summary>
    public float[]? MldCb;

    /// <summary><c>mld[Max(SBMAX_l, SBMAX_s)]</c>.</summary>
    public float[]? Mld;

    /// <summary><c>bo_weight[Max(SBMAX_l, SBMAX_s)]</c> - band weight at the transition.</summary>
    public float[]? BoWeight;

    /// <summary><c>attack_threshold</c> - short block tuning.</summary>
    public float AttackThreshold;

    /// <summary><c>s3ind[CBANDS][2]</c> - flattened; index as <c>[band * 2 + which]</c>.</summary>
    public int[]? S3Ind;

    /// <summary><c>numlines[CBANDS]</c>.</summary>
    public int[]? Numlines;

    /// <summary><c>bm[Max(SBMAX_l, SBMAX_s)]</c>.</summary>
    public int[]? Bm;

    /// <summary><c>bo[Max(SBMAX_l, SBMAX_s)]</c>.</summary>
    public int[]? Bo;

    /// <summary><c>npart</c>.</summary>
    public int Npart;

    /// <summary><c>n_sb</c> - SBMAX_l or SBMAX_s.</summary>
    public int NSb;

    /// <summary>
    /// <c>s3</c> - the spreading function. A ragged table whose length falls out of
    /// <c>s3ind</c>, which is why the C allocates it at run time instead of declaring a bound.
    /// </summary>
    public float[]? S3;

    public void Allocate()
    {
        MaskingLower = new float[LameConstants.CBands];
        Minval = new float[LameConstants.CBands];
        Rnumlines = new float[LameConstants.CBands];
        MldCb = new float[LameConstants.CBands];
        Mld = new float[SbMaxEither];
        BoWeight = new float[SbMaxEither];
        S3Ind = new int[LameConstants.CBands * 2];
        Numlines = new int[LameConstants.CBands];
        Bm = new int[SbMaxEither];
        Bo = new int[SbMaxEither];
    }

    /// <summary>
    /// <c>memcpy(&amp;gd-&gt;l_to_s, &amp;gd-&gt;l, sizeof(gd-&gt;l_to_s))</c> - psymodel.c's last line
    /// but one.
    /// </summary>
    /// <remarks>
    /// This is the same trap as <c>gr_info</c>, with the opposite answer. In the C every array
    /// here is inline storage, so the memcpy duplicates its contents - except <c>s3</c>, which
    /// is a <c>FLOAT *</c>, so the copy shares the source's spreading function. Both halves
    /// matter: <c>init_numline</c> then overwrites <c>l_to_s</c>'s <c>numlines</c>, <c>bo</c>,
    /// <c>bm</c>, <c>bo_weight</c> and <c>mld</c> and must not reach back into <c>l</c>, while
    /// <c>s3</c> is deliberately the same table and a second copy of it would be an allocation
    /// nobody ever fills.
    /// </remarks>
    public void CopyFrom(in PsyConstCb2Sb other)
    {
        other.MaskingLower.AsSpan().CopyTo(MaskingLower);
        other.Minval.AsSpan().CopyTo(Minval);
        other.Rnumlines.AsSpan().CopyTo(Rnumlines);
        other.MldCb.AsSpan().CopyTo(MldCb);
        other.Mld.AsSpan().CopyTo(Mld);
        other.BoWeight.AsSpan().CopyTo(BoWeight);
        other.S3Ind.AsSpan().CopyTo(S3Ind);
        other.Numlines.AsSpan().CopyTo(Numlines);
        other.Bm.AsSpan().CopyTo(Bm);
        other.Bo.AsSpan().CopyTo(Bo);
        AttackThreshold = other.AttackThreshold;
        Npart = other.Npart;
        NSb = other.NSb;

        // A pointer in the C, so the copy aliases rather than duplicates.
        S3 = other.S3;
    }
}

/// <summary><c>PsyConst_t</c> - the psychoacoustic model's constants.</summary>
/// <remarks>
/// A sealed class rather than a struct, because the C holds it as <c>PsyConst_t *cd_psy</c> and
/// reads that pointer's nullness as "has psymodel_init already run": <c>psymodel_init</c> opens
/// with <c>if (gfc-&gt;cd_psy != 0) return 0;</c>. A nullable reference is that, exactly.
/// </remarks>
internal sealed class PsyConstT
{
    /// <summary><c>window[BLKSIZE]</c> - the long analysis window.</summary>
    public float[]? Window;

    /// <summary><c>window_s[BLKSIZE_s / 2]</c> - the short analysis window.</summary>
    public float[]? WindowS;

    /// <summary><c>l</c> - long blocks.</summary>
    public PsyConstCb2Sb L;

    /// <summary><c>s</c> - short blocks.</summary>
    public PsyConstCb2Sb S;

    /// <summary><c>l_to_s</c>.</summary>
    public PsyConstCb2Sb LToS;

    /// <summary><c>attack_threshold[4]</c>.</summary>
    public float[]? AttackThreshold;

    /// <summary><c>decay</c>.</summary>
    public float Decay;

    /// <summary><c>force_short_block_calc</c>.</summary>
    public int ForceShortBlockCalc;

    public void Allocate()
    {
        Window = new float[LameConstants.BlkSize];
        WindowS = new float[LameConstants.BlkSizeS / 2];
        AttackThreshold = new float[4];
        L.Allocate();
        S.Allocate();
        LToS.Allocate();
    }
}

/// <summary><c>PsyStateVar_t</c> - the psychoacoustic model's state between frames.</summary>
internal struct PsyStateVar
{
    /// <summary><c>nb_l1[4][CBANDS]</c> - flattened; index as <c>[ch * CBANDS + band]</c>.</summary>
    public float[]? NbL1;

    /// <summary><c>nb_l2[4][CBANDS]</c>.</summary>
    public float[]? NbL2;

    /// <summary><c>nb_s1[4][CBANDS]</c>.</summary>
    public float[]? NbS1;

    /// <summary><c>nb_s2[4][CBANDS]</c>.</summary>
    public float[]? NbS2;

    /// <summary><c>thm[4]</c>.</summary>
    public IiiPsyXmin[]? Thm;

    /// <summary><c>en[4]</c>.</summary>
    public IiiPsyXmin[]? En;

    /// <summary><c>loudness_sq_save[2]</c> - carries L3psycho_anal's granule delay.</summary>
    public Float2 LoudnessSqSave;

    /// <summary><c>tot_ener[4]</c>.</summary>
    public Float4 TotEner;

    /// <summary><c>last_en_subshort[4][9]</c> - flattened; index as <c>[ch * 9 + i]</c>.</summary>
    public float[]? LastEnSubshort;

    /// <summary><c>last_attacks[4]</c>.</summary>
    public Int4 LastAttacks;

    /// <summary><c>blocktype_old[2]</c>.</summary>
    public Int2 BlocktypeOld;

    public void Allocate()
    {
        NbL1 = new float[4 * LameConstants.CBands];
        NbL2 = new float[4 * LameConstants.CBands];
        NbS1 = new float[4 * LameConstants.CBands];
        NbS2 = new float[4 * LameConstants.CBands];
        Thm = new IiiPsyXmin[4];
        En = new IiiPsyXmin[4];
        LastEnSubshort = new float[4 * 9];
    }

    [InlineArray(2)] internal struct Float2 { private float element0; }

    [InlineArray(4)] internal struct Float4 { private float element0; }

    [InlineArray(2)] internal struct Int2 { private int element0; }

    [InlineArray(4)] internal struct Int4 { private int element0; }
}

/// <summary><c>PsyResult_t</c> - what the model reports for the frame just analysed.</summary>
internal struct PsyResult
{
    /// <summary>
    /// <c>loudness_sq[2][2]</c> - loudness squared per granule and channel. Flattened; index as
    /// <c>[gr * 2 + ch]</c>.
    /// </summary>
    public Float4 LoudnessSq;

    [InlineArray(4)] internal struct Float4 { private float element0; }
}

/// <summary>Sizes declared inside <c>EncStateVar_t</c> in the C.</summary>
internal static class LameEncStateConstants
{
    /// <summary><c>BPC</c> - the most filter convolution windows resampling precomputes.</summary>
    public const int Bpc = 320;

    /// <summary><c>MAX_HEADER_BUF</c> - must be a power of two.</summary>
    public const int MaxHeaderBuf = 256;

    /// <summary><c>MAX_HEADER_LEN</c> - the largest header is 38 bytes.</summary>
    public const int MaxHeaderLen = 40;

    /// <summary><c>MFSIZE</c> - <c>3 * 1152 + ENCDELAY - MDCTDELAY</c>.</summary>
    public const int MfSize = 3 * 1152 + LameConstants.EncDelay - LameConstants.MdctDelay;
}

/// <summary>One slot of <c>EncStateVar_t.header[MAX_HEADER_BUF]</c>.</summary>
internal struct HeaderSlot
{
    /// <summary><c>write_timing</c> - the bit position this header belongs at.</summary>
    public int WriteTiming;

    /// <summary><c>ptr</c>.</summary>
    public int Ptr;

    /// <summary><c>buf[MAX_HEADER_LEN]</c> - a <c>char</c> array in the C; 38 bytes are used.</summary>
    public Byte40 Buf;

    [InlineArray(LameEncStateConstants.MaxHeaderLen)] internal struct Byte40 { private byte element0; }
}

/// <summary><c>EncStateVar_t</c> - the encoder's state between frames.</summary>
internal struct EncStateVar
{
    /// <summary>
    /// <c>sb_sample[2][2][18][SBLIMIT]</c> - the filterbank's output. Flattened; index as
    /// <c>[((ch * 2 + which) * 18 + i) * 32 + sb]</c>.
    /// </summary>
    public float[]? SbSample;

    /// <summary><c>amp_filter[32]</c>.</summary>
    public float[]? AmpFilter;

    /// <summary><c>itime[2]</c> - a double in the C, which notes that float is not enough.</summary>
    public Double2 Itime;

    /// <summary><c>inbuf_old[2]</c>.</summary>
    public float[]?[]? InbufOld;

    /// <summary><c>blackfilt[2 * BPC + 1]</c> - the resampler's precomputed windows.</summary>
    public float[]?[]? Blackfilt;

    /// <summary><c>pefirbuf[19]</c>.</summary>
    public float[]? Pefirbuf;

    /// <summary><c>frac_SpF</c> - used for padding.</summary>
    public int FracSpF;

    /// <summary><c>slot_lag</c> - used for padding.</summary>
    public int SlotLag;

    /// <summary><c>header[MAX_HEADER_BUF]</c>.</summary>
    public HeaderSlot[]? Header;

    /// <summary><c>h_ptr</c>.</summary>
    public int HPtr;

    /// <summary><c>w_ptr</c>.</summary>
    public int WPtr;

    /// <summary><c>ancillary_flag</c>.</summary>
    public int AncillaryFlag;

    /// <summary><c>ResvSize</c> - the bit reservoir's current fill, in bits.</summary>
    public int ResvSize;

    /// <summary><c>ResvMax</c> - in bits.</summary>
    public int ResvMax;

    /// <summary><c>in_buffer_nsamples</c>.</summary>
    public int InBufferNsamples;

    /// <summary><c>in_buffer_0</c>.</summary>
    public float[]? InBuffer0;

    /// <summary><c>in_buffer_1</c>.</summary>
    public float[]? InBuffer1;

    /// <summary><c>mfbuf[2][MFSIZE]</c> - flattened; index as <c>[ch * MFSIZE + i]</c>.</summary>
    public float[]? Mfbuf;

    /// <summary><c>mf_samples_to_encode</c>.</summary>
    public int MfSamplesToEncode;

    /// <summary><c>mf_size</c>.</summary>
    public int MfSize;

    public void Allocate()
    {
        SbSample = new float[2 * 2 * 18 * 32];
        AmpFilter = new float[32];
        InbufOld = new float[2][];
        Blackfilt = new float[2 * LameEncStateConstants.Bpc + 1][];
        Pefirbuf = new float[19];
        Header = new HeaderSlot[LameEncStateConstants.MaxHeaderBuf];
        Mfbuf = new float[2 * LameEncStateConstants.MfSize];
    }

    [InlineArray(2)] internal struct Double2 { private double element0; }
}

/// <summary><c>EncResult_t</c> - what the encoder reports about the frame it just wrote.</summary>
internal struct EncResult
{
    /// <summary>
    /// <c>bitrate_channelmode_hist[16][4 + 1]</c> - flattened; index as <c>[br * 5 + mode]</c>.
    /// </summary>
    public int[]? BitrateChannelmodeHist;

    /// <summary>
    /// <c>bitrate_blocktype_hist[16][4 + 1 + 1]</c> - norm, start, short, stop, mixed, sum.
    /// Flattened; index as <c>[br * 6 + kind]</c>.
    /// </summary>
    public int[]? BitrateBlocktypeHist;

    /// <summary><c>bitrate_index</c>.</summary>
    public int BitrateIndex;

    /// <summary><c>frame_number</c> - frames encoded so far.</summary>
    public int FrameNumber;

    /// <summary><c>padding</c> - whether this frame carries a padding slot.</summary>
    public int Padding;

    /// <summary><c>mode_ext</c> - see <see cref="LameModeExt"/>.</summary>
    public int ModeExt;

    /// <summary><c>encoder_delay</c>.</summary>
    public int EncoderDelay;

    /// <summary><c>encoder_padding</c> - samples of padding appended to the input.</summary>
    public int EncoderPadding;

    public void Allocate()
    {
        BitrateChannelmodeHist = new int[16 * 5];
        BitrateBlocktypeHist = new int[16 * 6];
    }
}

/// <summary><c>QntStateVar_t</c> - the quantiser's state between frames.</summary>
internal struct QntStateVar
{
    /// <summary><c>longfact[SBMAX_l]</c> - nspsytune.</summary>
    public float[]? Longfact;

    /// <summary><c>shortfact[SBMAX_s]</c> - nspsytune.</summary>
    public float[]? Shortfact;

    /// <summary><c>masking_lower</c>.</summary>
    public float MaskingLower;

    /// <summary><c>mask_adjust</c> - the dbQ stuff.</summary>
    public float MaskAdjust;

    /// <summary><c>mask_adjust_short</c>.</summary>
    public float MaskAdjustShort;

    /// <summary><c>OldValue[2]</c>.</summary>
    public Int2 OldValue;

    /// <summary><c>CurrentStep[2]</c>.</summary>
    public Int2 CurrentStep;

    /// <summary><c>pseudohalf[SFBMAX]</c>.</summary>
    public int[]? Pseudohalf;

    /// <summary><c>sfb21_extra</c> - set in lame_init_params.</summary>
    public int Sfb21Extra;

    /// <summary>
    /// <c>substep_shaping</c> - 0 none, 1 at the last step (VBR only), 2 inside the loop,
    /// 3 inside the loop and at the last step.
    /// </summary>
    public int SubstepShaping;

    /// <summary><c>bv_scf[576]</c> - a <c>char</c> array in the C.</summary>
    public byte[]? BvScf;

    public void Allocate()
    {
        Longfact = new float[LameConstants.SbMaxL];
        Shortfact = new float[LameConstants.SbMaxS];
        Pseudohalf = new int[LameConstants.SfbMax];
        BvScf = new byte[LameConstants.CoefficientsPerGranule];
    }

    [InlineArray(2)] internal struct Int2 { private int element0; }
}

/// <summary>
/// <c>RpgResult_t</c> - ReplayGain and clipping results. Out of scope: <c>findReplayGain</c> and
/// <c>findPeakSample</c> are both off for ffmpeg, so nothing writes these.
/// </summary>
internal struct RpgResult
{
    /// <summary><c>noclipScale</c> - the scale needed to prevent clipping.</summary>
    public float NoclipScale;

    /// <summary><c>PeakSample</c>.</summary>
    public float PeakSample;

    /// <summary><c>RadioGain</c>.</summary>
    public int RadioGain;

    /// <summary><c>noclipGainChange</c>.</summary>
    public int NoclipGainChange;
}

/// <summary>
/// <c>SessionConfig_t</c> - everything <c>lame_init_params</c> derives from the user's settings
/// and then treats as fixed for the session.
/// </summary>
internal struct SessionConfig
{
    /// <summary><c>version</c> - 0 is MPEG-2/2.5, 1 is MPEG-1.</summary>
    public int Version;

    /// <summary><c>samplerate_index</c>.</summary>
    public int SamplerateIndex;

    /// <summary><c>sideinfo_len</c>.</summary>
    public int SideinfoLen;

    /// <summary><c>noise_shaping</c> - 0 none, 1 the ISO AAC model, 2 allows scalefac_select=1.</summary>
    public int NoiseShaping;

    /// <summary><c>subblock_gain</c>.</summary>
    public int SubblockGain;

    /// <summary><c>use_best_huffman</c> - 0 no, 1 outside the loop, 2 inside it (slow).</summary>
    public int UseBestHuffman;

    /// <summary>
    /// <c>noise_shaping_amp</c> - 0 amplify every distorted band (ISO), 1 within 50% of the
    /// maximum on a dB scale, 2 only the most distorted band, 3 method 1 refined with method 2.
    /// </summary>
    public int NoiseShapingAmp;

    /// <summary>
    /// <c>noise_shaping_stop</c> - 0 stop at over=0 or when a scalefactor maxes out, 1 stop once
    /// all are amplified or one maxes out, 2 stop once all are amplified.
    /// </summary>
    public int NoiseShapingStop;

    /// <summary><c>full_outer_loop</c> - 0 stops early once no distortion is found, 1 searches fully.</summary>
    public int FullOuterLoop;

    /// <summary><c>lowpassfreq</c>.</summary>
    public int Lowpassfreq;

    /// <summary><c>highpassfreq</c>.</summary>
    public int Highpassfreq;

    /// <summary><c>samplerate_in</c> - in Hz.</summary>
    public int SamplerateIn;

    /// <summary><c>samplerate_out</c>.</summary>
    public int SamplerateOut;

    /// <summary><c>channels_in</c>.</summary>
    public int ChannelsIn;

    /// <summary><c>channels_out</c>.</summary>
    public int ChannelsOut;

    /// <summary><c>mode_gr</c> - granules per frame.</summary>
    public int ModeGr;

    /// <summary><c>force_ms</c> - requires <c>mode</c> to be JOINT_STEREO.</summary>
    public int ForceMs;

    /// <summary><c>quant_comp</c>.</summary>
    public int QuantComp;

    /// <summary><c>quant_comp_short</c>.</summary>
    public int QuantCompShort;

    /// <summary><c>use_temporal_masking_effect</c>.</summary>
    public int UseTemporalMaskingEffect;

    /// <summary><c>use_safe_joint_stereo</c>.</summary>
    public int UseSafeJointStereo;

    /// <summary><c>preset</c>.</summary>
    public int Preset;

    /// <summary><c>vbr</c>.</summary>
    public VbrMode Vbr;

    /// <summary><c>vbr_avg_bitrate_kbps</c>.</summary>
    public int VbrAvgBitrateKbps;

    /// <summary><c>vbr_min_bitrate_index</c>.</summary>
    public int VbrMinBitrateIndex;

    /// <summary><c>vbr_max_bitrate_index</c>.</summary>
    public int VbrMaxBitrateIndex;

    /// <summary><c>avg_bitrate</c>.</summary>
    public int AvgBitrate;

    /// <summary>
    /// <c>enforce_min_bitrate</c> - the VBR minimum is normally enforced strictly, but analog
    /// silence violates it.
    /// </summary>
    public int EnforceMinBitrate;

    /// <summary><c>findReplayGain</c>.</summary>
    public int FindReplayGain;

    /// <summary><c>findPeakSample</c>.</summary>
    public int FindPeakSample;

    /// <summary><c>decode_on_the_fly</c>.</summary>
    public int DecodeOnTheFly;

    /// <summary><c>analysis</c>.</summary>
    public int Analysis;

    /// <summary><c>disable_reservoir</c> - ffmpeg sets this from its own reservoir option.</summary>
    public int DisableReservoir;

    /// <summary><c>buffer_constraint</c> - enforce the ISO spec as far as possible.</summary>
    public int BufferConstraint;

    /// <summary><c>free_format</c>.</summary>
    public int FreeFormat;

    /// <summary><c>write_lame_tag</c> - the Xing tag, which ffmpeg turns off.</summary>
    public int WriteLameTag;

    /// <summary><c>error_protection</c> - two bytes of CRC per frame.</summary>
    public int ErrorProtection;

    /// <summary><c>copyright</c>.</summary>
    public int Copyright;

    /// <summary><c>original</c> - ffmpeg's default is 1.</summary>
    public int Original;

    /// <summary><c>extension</c> - the MP3 private extension bit, which means nothing.</summary>
    public int Extension;

    /// <summary>
    /// <c>emphasis</c> - the input is emphasised PCM. Strongly not recommended by the C: psycho
    /// does not account for it, and most decoders ignore the bits.
    /// </summary>
    public int Emphasis;

    /// <summary><c>mode</c>.</summary>
    public MpegMode Mode;

    /// <summary><c>short_blocks</c>.</summary>
    public ShortBlock ShortBlocks;

    /// <summary><c>interChRatio</c>.</summary>
    public float InterChRatio;

    /// <summary><c>msfix</c> - Naoki's adjustment of the mid/side maskings.</summary>
    public float Msfix;

    /// <summary><c>ATH_offset_db</c>.</summary>
    public float AthOffsetDb;

    /// <summary><c>ATH_offset_factor</c> - derived from <c>ATH_offset_db</c>.</summary>
    public float AthOffsetFactor;

    /// <summary><c>ATHcurve</c> - shapes ATH formula 4.</summary>
    public float AthCurve;

    /// <summary><c>ATHtype</c>.</summary>
    public int AthType;

    /// <summary><c>ATHonly</c>.</summary>
    public int AthOnly;

    /// <summary><c>ATHshort</c>.</summary>
    public int AthShort;

    /// <summary><c>noATH</c>.</summary>
    public int NoAth;

    /// <summary><c>ATHfixpoint</c>.</summary>
    public float AthFixpoint;

    /// <summary><c>adjust_alto_db</c>.</summary>
    public float AdjustAltoDb;

    /// <summary><c>adjust_bass_db</c>.</summary>
    public float AdjustBassDb;

    /// <summary><c>adjust_treble_db</c>.</summary>
    public float AdjustTrebleDb;

    /// <summary><c>adjust_sfb21_db</c>.</summary>
    public float AdjustSfb21Db;

    /// <summary><c>compression_ratio</c>.</summary>
    public float CompressionRatio;

    /// <summary><c>lowpass1</c> - normalised frequency bound of the passband.</summary>
    public float Lowpass1;

    /// <summary><c>lowpass2</c>.</summary>
    public float Lowpass2;

    /// <summary><c>highpass1</c>.</summary>
    public float Highpass1;

    /// <summary><c>highpass2</c>.</summary>
    public float Highpass2;

    /// <summary>
    /// <c>pcm_transform[2][2]</c> - scales the input before encoding. Flattened; index as
    /// <c>[outChannel * 2 + inChannel]</c>.
    /// </summary>
    public Float4 PcmTransform;

    /// <summary><c>minval</c>.</summary>
    public float Minval;

    [InlineArray(4)] internal struct Float4 { private float element0; }
}

/// <summary>
/// <c>lame_internal_flags</c> - the encoder itself. One per stream, built by <c>lame_init</c>.
/// </summary>
/// <remarks>
/// A class, not a struct: it is roughly 80 KB, and the C only ever reaches it through
/// <c>gfc-&gt;</c>, so making a copy impossible is closer to the original than mirroring it as a
/// value type would be.
/// </remarks>
internal sealed class LameInternalFlags
{
    /// <summary><c>LAME_ID</c> - the sentinel <c>class_id</c> carries once the struct is live.</summary>
    public const uint LameId = 0xFFF88E3B;

    /// <summary><c>class_id</c>.</summary>
    public uint ClassId;

    /// <summary><c>lame_init_params_successful</c>.</summary>
    public int LameInitParamsSuccessful;

    /// <summary><c>lame_encode_frame_init</c>.</summary>
    public int LameEncodeFrameInit;

    /// <summary><c>iteration_init_init</c>.</summary>
    public int IterationInitInit;

    /// <summary><c>fill_buffer_resample_init</c>.</summary>
    public int FillBufferResampleInit;

    /// <summary><c>cfg</c>.</summary>
    public SessionConfig Cfg;

    /// <summary><c>bs</c>.</summary>
    public BitStreamStruc Bs;

    /// <summary><c>l3_side</c>.</summary>
    public IiiSideInfo L3Side;

    /// <summary><c>scalefac_band</c>.</summary>
    public ScalefacStruct ScalefacBand;

    /// <summary><c>sv_psy</c> - state owned by psymodel.c.</summary>
    public PsyStateVar SvPsy;

    /// <summary><c>ov_psy</c>.</summary>
    public PsyResult OvPsy;

    /// <summary><c>sv_enc</c> - state owned by encoder.c.</summary>
    public EncStateVar SvEnc;

    /// <summary><c>ov_enc</c>.</summary>
    public EncResult OvEnc;

    /// <summary><c>sv_qnt</c> - state owned by quantize.c.</summary>
    public QntStateVar SvQnt;

    /// <summary>
    /// <c>ov_rpg</c>. The C's <c>sv_rpg</c> holds only a ReplayGain context, which is out of
    /// scope, so it has no counterpart here.
    /// </summary>
    public RpgResult OvRpg;

    /// <summary><c>nMusicCRC</c>.</summary>
    public ushort NMusicCrc;

    /// <summary><c>VBR_seek_table</c> - for the Xing header, which ffmpeg disables.</summary>
    public VbrSeekInfo VbrSeekTable;

    /// <summary><c>ATH</c> - a pointer in the C, allocated once; inline storage here.</summary>
    public AthT Ath;

    /// <summary><c>cd_psy</c> - likewise a pointer in the C.</summary>
    public PsyConstT? CdPsy;

    /// <summary>
    /// The psychoacoustic model's scratch buffers - C stack locals too large to keep on the
    /// stack here. A divergence, recorded in the port checklist; see LamePsyModel.cs.
    /// </summary>
    public readonly LamePsyScratch PsyScratch = new();

    /// <summary>
    /// The iteration loops' scratch buffers - C stack locals too large to keep on the stack
    /// here. A divergence, recorded in the port checklist; see LameQuantize.cs.
    /// </summary>
    public readonly LameQuantizeScratch QuantizeScratch = new();

    /// <summary>
    /// The frame encoder's scratch - C stack locals too large to keep on the stack here. A
    /// divergence, recorded in the port checklist; see LameEncoder.cs.
    /// </summary>
    public readonly LameEncoderScratch EncoderScratch = new();

    /// <summary>
    /// Allocates what the C allocates across <c>lame_init</c> and <c>lame_init_params</c>.
    /// Called once per encoder, so none of it is on a per-frame path.
    /// </summary>
    public void Allocate()
    {
        SvPsy.Allocate();
        SvEnc.Allocate();
        OvEnc.Allocate();
        SvQnt.Allocate();
        Ath.Allocate();
    }
}

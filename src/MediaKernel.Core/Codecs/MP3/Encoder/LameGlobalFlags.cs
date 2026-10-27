// Translated from lame-3.100/libmp3lame/lame_global_flags.h - lame_global_struct, the settings
// the caller fills in before lame_init_params turns them into a SessionConfig.
//
// A sealed class, as lame_internal_flags is and for the same reason: the C passes it as
// lame_global_flags * everywhere, holds the two structs in a cycle (gfp->internal_flags and
// gfc->gfp), and never copies either. A struct here would make that cycle impossible to express
// and would put a 300-byte copy on every call that takes one.
//
// short_block_t is already translated as ShortBlock in LameEnums.cs; the report function
// pointers and the asm_optimizations block are recorded as diverged in the port checklist -
// the first is the caller's logging hook, which ffmpeg does not set, and the second selects
// hand-written assembly this port does not have.

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>lame_global_struct</c> - the user-facing settings, in the C's declared order.</summary>
internal sealed class LameGlobalFlags
{
    /// <summary><c>LAME_ID</c> - what <c>class_id</c> holds once the struct is initialised.</summary>
    public const uint LameId = 0xFFF88E3F;

    /// <summary><c>class_id</c>.</summary>
    public uint ClassId;

    // ---- input description ----

    /// <summary><c>num_samples</c> - default 2^32 - 1.</summary>
    public ulong NumSamples;

    /// <summary><c>num_channels</c> - default 2.</summary>
    public int NumChannels;

    /// <summary><c>samplerate_in</c> - in Hz, default 44100.</summary>
    public int SamplerateIn;

    /// <summary><c>samplerate_out</c> - 0 lets LAME choose.</summary>
    public int SamplerateOut;

    /// <summary><c>scale</c> - scale the input by this before encoding.</summary>
    public float Scale;

    /// <summary><c>scale_left</c>.</summary>
    public float ScaleLeft;

    /// <summary><c>scale_right</c>.</summary>
    public float ScaleRight;

    // ---- general control ----

    /// <summary><c>analysis</c> - collect data for a frame analyser.</summary>
    public int Analysis;

    /// <summary><c>write_lame_tag</c> - the Xing VBR tag; ffmpeg turns it off.</summary>
    public int WriteLameTag;

    /// <summary><c>decode_only</c>.</summary>
    public int DecodeOnly;

    /// <summary><c>quality</c> - 0 best, 9 worst, default 5.</summary>
    public int Quality;

    /// <summary><c>mode</c>.</summary>
    public MpegMode Mode;

    /// <summary><c>force_ms</c> - requires <c>mode</c> to be joint stereo.</summary>
    public int ForceMs;

    /// <summary><c>free_format</c>.</summary>
    public int FreeFormat;

    /// <summary><c>findReplayGain</c>.</summary>
    public int FindReplayGain;

    /// <summary><c>decode_on_the_fly</c>.</summary>
    public int DecodeOnTheFly;

    /// <summary><c>write_id3tag_automatic</c>.</summary>
    public int WriteId3TagAutomatic;

    /// <summary><c>nogap_total</c>.</summary>
    public int NogapTotal;

    /// <summary><c>nogap_current</c>.</summary>
    public int NogapCurrent;

    /// <summary><c>substep_shaping</c>.</summary>
    public int SubstepShaping;

    /// <summary><c>noise_shaping</c>.</summary>
    public int NoiseShaping;

    /// <summary><c>subblock_gain</c>.</summary>
    public int SubblockGain;

    /// <summary><c>use_best_huffman</c>.</summary>
    public int UseBestHuffman;

    /// <summary><c>brate</c> - set this or <c>compression_ratio</c>; LAME derives the other.</summary>
    public int Brate;

    /// <summary><c>compression_ratio</c> - default 11.025.</summary>
    public float CompressionRatio;

    // ---- frame params ----

    /// <summary><c>copyright</c>.</summary>
    public int Copyright;

    /// <summary><c>original</c> - default 1.</summary>
    public int Original;

    /// <summary><c>extension</c> - the MP3 private extension bit, which means nothing.</summary>
    public int Extension;

    /// <summary><c>emphasis</c>.</summary>
    public int Emphasis;

    /// <summary><c>error_protection</c> - two bytes of CRC per frame.</summary>
    public int ErrorProtection;

    /// <summary><c>strict_ISO</c>.</summary>
    public int StrictIso;

    /// <summary><c>disable_reservoir</c>.</summary>
    public int DisableReservoir;

    // ---- quantisation and noise shaping ----

    /// <summary><c>quant_comp</c>.</summary>
    public int QuantComp;

    /// <summary><c>quant_comp_short</c>.</summary>
    public int QuantCompShort;

    /// <summary><c>experimentalY</c>.</summary>
    public int ExperimentalY;

    /// <summary><c>experimentalZ</c> - becomes <c>force_short_block_calc</c>.</summary>
    public int ExperimentalZ;

    /// <summary><c>exp_nspsytune</c>.</summary>
    public int ExpNsPsytune;

    /// <summary><c>preset</c>.</summary>
    public int Preset;

    // ---- VBR control ----

    /// <summary><c>VBR</c>.</summary>
    public VbrMode Vbr;

    /// <summary><c>VBR_q_frac</c> - range [0, 1).</summary>
    public float VbrQFrac;

    /// <summary><c>VBR_q</c> - range [0, 9].</summary>
    public int VbrQ;

    /// <summary><c>VBR_mean_bitrate_kbps</c>.</summary>
    public int VbrMeanBitrateKbps;

    /// <summary><c>VBR_min_bitrate_kbps</c>.</summary>
    public int VbrMinBitrateKbps;

    /// <summary><c>VBR_max_bitrate_kbps</c>.</summary>
    public int VbrMaxBitrateKbps;

    /// <summary><c>VBR_hard_min</c>.</summary>
    public int VbrHardMin;

    // ---- resampling and filtering ----

    /// <summary><c>lowpassfreq</c> - 0 lets LAME choose, -1 means no filter.</summary>
    public int Lowpassfreq;

    /// <summary><c>highpassfreq</c>.</summary>
    public int Highpassfreq;

    /// <summary><c>lowpasswidth</c> - default 15 per cent.</summary>
    public int Lowpasswidth;

    /// <summary><c>highpasswidth</c>.</summary>
    public int Highpasswidth;

    // ---- psychoacoustics ----

    /// <summary><c>maskingadjust</c>.</summary>
    public float Maskingadjust;

    /// <summary><c>maskingadjust_short</c>.</summary>
    public float MaskingadjustShort;

    /// <summary><c>ATHonly</c>.</summary>
    public int AthOnly;

    /// <summary><c>ATHshort</c>.</summary>
    public int AthShort;

    /// <summary><c>noATH</c>.</summary>
    public int NoAth;

    /// <summary><c>ATHtype</c>.</summary>
    public int AthType;

    /// <summary><c>ATHcurve</c>.</summary>
    public float AthCurve;

    /// <summary><c>ATH_lower_db</c>.</summary>
    public float AthLowerDb;

    /// <summary><c>athaa_type</c>.</summary>
    public int AthaaType;

    /// <summary><c>athaa_sensitivity</c> - in dB.</summary>
    public float AthaaSensitivity;

    /// <summary><c>short_blocks</c>.</summary>
    public ShortBlock ShortBlocks;

    /// <summary><c>useTemporal</c>.</summary>
    public int UseTemporal;

    /// <summary><c>interChRatio</c>.</summary>
    public float InterChRatio;

    /// <summary><c>msfix</c>.</summary>
    public float Msfix;

    /// <summary><c>tune</c>.</summary>
    public int Tune;

    /// <summary><c>tune_value_a</c>.</summary>
    public float TuneValueA;

    /// <summary><c>attackthre</c> - the attack threshold for L, R and M.</summary>
    public float Attackthre;

    /// <summary><c>attackthre_s</c> - the attack threshold for S.</summary>
    public float AttackthreS;

    // report, which is three function pointers, is diverged - see the file header.

    /// <summary><c>lame_allocated_gfp</c>.</summary>
    public int LameAllocatedGfp;

    /// <summary><c>internal_flags</c>.</summary>
    public LameInternalFlags? InternalFlags;

    // asm_optimizations is diverged - see the file header.
}

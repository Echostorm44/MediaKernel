// Translated from lame-3.100/libmp3lame/set_get.c - the setters and getters on
// lame_global_flags.
//
// set_get.c holds 172 of them and most are one line. Only the ones libmp3lame.c calls, and the
// ones presets.c and lame_init_params call internally, are here; the rest stay todo in
// the port checklist rather than being written out for the sake of a count. The ffmpeg
// surface is: in_samplerate, out_samplerate, num_channels, mode, brate, quality, lowpassfreq,
// disable_reservoir, copyright, original, bWriteVbrTag, VBR, VBR_mean_bitrate_kbps,
// VBR_quality, encoder_delay and framesize.
//
// Every one of them guards on is_lame_global_flags_valid and returns -1 when it fails, which
// is how LAME reports being handed a struct that was never initialised. That guard is kept:
// it is the C's only defence against a caller using a freed or zeroed gfp, and dropping it
// would turn a -1 into a NullReferenceException somewhere else entirely.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>set_get.c</c>.</summary>
internal static class LameSetGet
{
    /// <summary><c>is_lame_global_flags_valid</c> (<c>lame.c</c>).</summary>
    public static bool IsLameGlobalFlagsValid(LameGlobalFlags? gfp)
        => gfp is not null && gfp.ClassId == LameGlobalFlags.LameId;

    /// <summary><c>is_lame_internal_flags_valid</c> (<c>lame.c</c>).</summary>
    public static bool IsLameInternalFlagsValid(LameInternalFlags? gfc)
        => gfc is not null
        && gfc.ClassId == LameInternalFlags.LameId
        && gfc.LameInitParamsSuccessful > 0;

    // ---- the input description ----

    /// <summary><c>lame_set_in_samplerate</c>.</summary>
    public static int SetInSamplerate(LameGlobalFlags gfp, int rate)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (rate < 1) return -1;
        gfp.SamplerateIn = rate;
        return 0;
    }

    /// <summary><c>lame_set_num_channels</c>.</summary>
    public static int SetNumChannels(LameGlobalFlags gfp, int channels)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (channels is < 1 or > 2) return -1;      // 0 or more than 2 makes no sense
        gfp.NumChannels = channels;
        return 0;
    }

    /// <summary><c>lame_set_out_samplerate</c>.</summary>
    public static int SetOutSamplerate(LameGlobalFlags gfp, int rate)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (rate != 0)
        {
            int v = LameUtil.SmpFrqIndex(rate, out _);
            if (v < 0) return -1;
        }
        gfp.SamplerateOut = rate;
        return 0;
    }

    /// <summary><c>lame_set_scale</c>.</summary>
    public static int SetScale(LameGlobalFlags gfp, float scale)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Scale = scale;
        return 0;
    }

    /// <summary><c>lame_get_scale</c>.</summary>
    public static float GetScale(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Scale : 0;

    // ---- general control ----

    /// <summary><c>lame_set_mode</c>.</summary>
    public static int SetMode(LameGlobalFlags gfp, MpegMode mode)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if ((int)mode < 0 || mode >= MpegMode.MaxIndicator) return -1;
        gfp.Mode = mode;
        return 0;
    }

    /// <summary><c>lame_set_quality</c>.</summary>
    public static int SetQuality(LameGlobalFlags gfp, int quality)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Quality = quality;
        return 0;
    }

    /// <summary><c>lame_set_brate</c>.</summary>
    public static int SetBrate(LameGlobalFlags gfp, int brate)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Brate = brate;
        if (brate > 320) gfp.StrictIso = LameBitstream.MdbMaximum;
        return 0;
    }

    /// <summary><c>lame_get_brate</c>.</summary>
    public static int GetBrate(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Brate : 0;

    /// <summary><c>lame_set_copyright</c>.</summary>
    public static int SetCopyright(LameGlobalFlags gfp, int copyright)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (copyright is < 0 or > 1) return -1;
        gfp.Copyright = copyright;
        return 0;
    }

    /// <summary><c>lame_set_original</c>.</summary>
    public static int SetOriginal(LameGlobalFlags gfp, int original)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (original is < 0 or > 1) return -1;
        gfp.Original = original;
        return 0;
    }

    /// <summary><c>lame_set_disable_reservoir</c>.</summary>
    public static int SetDisableReservoir(LameGlobalFlags gfp, int disable)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (disable is < 0 or > 1) return -1;
        gfp.DisableReservoir = disable;
        return 0;
    }

    /// <summary><c>lame_set_bWriteVbrTag</c>.</summary>
    public static int SetWriteVbrTag(LameGlobalFlags gfp, int value)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (value is < 0 or > 1) return -1;
        gfp.WriteLameTag = value;
        return 0;
    }

    /// <summary><c>lame_set_lowpassfreq</c>.</summary>
    public static int SetLowpassfreq(LameGlobalFlags gfp, int freq)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Lowpassfreq = freq;
        return 0;
    }

    // ---- VBR ----

    /// <summary><c>lame_set_VBR</c>.</summary>
    public static int SetVbr(LameGlobalFlags gfp, VbrMode mode)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if ((int)mode < 0 || mode >= VbrMode.MaxIndicator) return -1;
        gfp.Vbr = mode;
        return 0;
    }

    /// <summary><c>lame_get_VBR</c>.</summary>
    public static VbrMode GetVbr(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Vbr : VbrMode.Off;

    /// <summary><c>lame_set_VBR_q</c> - clamps, and clears the fractional part.</summary>
    public static int SetVbrQ(LameGlobalFlags gfp, int vbrQ)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        int ret = 0;
        if (vbrQ < 0) { ret = -1; vbrQ = 0; }       // unknown VBR quality level
        if (vbrQ > 9) { ret = -1; vbrQ = 9; }
        gfp.VbrQ = vbrQ;
        gfp.VbrQFrac = 0;
        return ret;
    }

    /// <summary><c>lame_get_VBR_q</c>.</summary>
    public static int GetVbrQ(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrQ : 0;

    /// <summary><c>lame_set_VBR_quality</c> - the fractional form.</summary>
    public static int SetVbrQuality(LameGlobalFlags gfp, float vbrQ)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        int ret = 0;
        if (vbrQ < 0) { ret = -1; vbrQ = 0; }
        if (vbrQ > 9.999f) { ret = -1; vbrQ = 9.999f; }
        gfp.VbrQ = (int)vbrQ;
        gfp.VbrQFrac = vbrQ - gfp.VbrQ;
        return ret;
    }

    /// <summary><c>lame_set_VBR_mean_bitrate_kbps</c>.</summary>
    public static int SetVbrMeanBitrateKbps(LameGlobalFlags gfp, int kbps)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.VbrMeanBitrateKbps = kbps;
        return 0;
    }

    /// <summary><c>lame_get_VBR_mean_bitrate_kbps</c>.</summary>
    public static int GetVbrMeanBitrateKbps(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrMeanBitrateKbps : 0;

    // ---- the psychoacoustic knobs the presets turn ----

    /// <summary><c>lame_set_quant_comp</c> / <c>lame_get_quant_comp</c>.</summary>
    public static int SetQuantComp(LameGlobalFlags gfp, int v)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.QuantComp = v;
        return 0;
    }

    /// <summary><c>lame_get_quant_comp</c>.</summary>
    public static int GetQuantComp(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.QuantComp : 0;

    /// <summary><c>lame_set_quant_comp_short</c>.</summary>
    public static int SetQuantCompShort(LameGlobalFlags gfp, int v)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.QuantCompShort = v;
        return 0;
    }

    /// <summary><c>lame_get_quant_comp_short</c>.</summary>
    public static int GetQuantCompShort(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.QuantCompShort : 0;

    /// <summary><c>lame_set_msfix</c> - returns void in the C.</summary>
    public static void SetMsfix(LameGlobalFlags gfp, double msfix)
    {
        if (IsLameGlobalFlagsValid(gfp)) gfp.Msfix = (float)msfix;
    }

    /// <summary><c>lame_get_msfix</c>.</summary>
    public static float GetMsfix(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Msfix : 0;

    /// <summary><c>lame_set_short_threshold_lrm</c> - writes <c>attackthre</c>.</summary>
    public static int SetShortThresholdLrm(LameGlobalFlags gfp, float lrm)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Attackthre = lrm;
        return 0;
    }

    /// <summary><c>lame_get_short_threshold_lrm</c>.</summary>
    public static float GetShortThresholdLrm(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Attackthre : 0;

    /// <summary><c>lame_set_short_threshold_s</c> - writes <c>attackthre_s</c>.</summary>
    public static int SetShortThresholdS(LameGlobalFlags gfp, float s)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AttackthreS = s;
        return 0;
    }

    /// <summary><c>lame_get_short_threshold_s</c>.</summary>
    public static float GetShortThresholdS(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AttackthreS : 0;

    /// <summary><c>lame_set_maskingadjust</c>.</summary>
    public static int SetMaskingadjust(LameGlobalFlags gfp, float adjust)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Maskingadjust = adjust;
        return 0;
    }

    /// <summary><c>lame_get_maskingadjust</c>.</summary>
    public static float GetMaskingadjust(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Maskingadjust : 0;

    /// <summary><c>lame_set_maskingadjust_short</c>.</summary>
    public static int SetMaskingadjustShort(LameGlobalFlags gfp, float adjust)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.MaskingadjustShort = adjust;
        return 0;
    }

    /// <summary><c>lame_get_maskingadjust_short</c>.</summary>
    public static float GetMaskingadjustShort(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.MaskingadjustShort : 0;

    /// <summary><c>lame_set_ATHtype</c>.</summary>
    public static int SetAthType(LameGlobalFlags gfp, int type)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthType = type;
        return 0;
    }

    /// <summary><c>lame_set_ATHlower</c> - writes <c>ATH_lower_db</c>.</summary>
    public static int SetAthLower(LameGlobalFlags gfp, float athLower)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthLowerDb = athLower;
        return 0;
    }

    /// <summary><c>lame_get_ATHlower</c>.</summary>
    public static float GetAthLower(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthLowerDb : 0;

    /// <summary><c>lame_set_ATHcurve</c>.</summary>
    public static int SetAthCurve(LameGlobalFlags gfp, float curve)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthCurve = curve;
        return 0;
    }

    /// <summary><c>lame_get_ATHcurve</c>.</summary>
    public static float GetAthCurve(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthCurve : 0;

    /// <summary><c>lame_set_athaa_sensitivity</c>.</summary>
    public static int SetAthaaSensitivity(LameGlobalFlags gfp, float sensitivity)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthaaSensitivity = sensitivity;
        return 0;
    }

    /// <summary><c>lame_get_athaa_sensitivity</c>.</summary>
    public static float GetAthaaSensitivity(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthaaSensitivity : 0;

    /// <summary><c>lame_set_interChRatio</c> - only accepts 0 to 1.</summary>
    public static int SetInterChRatio(LameGlobalFlags gfp, float ratio)
    {
        if (IsLameGlobalFlagsValid(gfp) && ratio is >= 0 and <= 1.0f)
        {
            gfp.InterChRatio = ratio;
            return 0;
        }
        return -1;
    }

    /// <summary><c>lame_get_interChRatio</c>.</summary>
    public static float GetInterChRatio(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.InterChRatio : 0;

    /// <summary><c>lame_set_exp_nspsytune</c>.</summary>
    public static int SetExpNsPsytune(LameGlobalFlags gfp, int value)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ExpNsPsytune = value;
        return 0;
    }

    /// <summary><c>lame_get_exp_nspsytune</c>.</summary>
    public static int GetExpNsPsytune(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ExpNsPsytune : 0;

    /// <summary><c>lame_set_experimentalY</c>.</summary>
    public static int SetExperimentalY(LameGlobalFlags gfp, int value)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ExperimentalY = value;
        return 0;
    }

    /// <summary>
    /// <c>lame_set_sfscale</c> - a scalefactor-scale switch spelt as a noise shaping level.
    /// </summary>
    public static int SetSfScale(LameGlobalFlags gfp, int val)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.NoiseShaping = val != 0 ? 2 : 1;
        return 0;
    }

    // ---- what the caller reads back ----

    /// <summary><c>lame_get_encoder_delay</c>.</summary>
    public static int GetEncoderDelay(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvEnc.EncoderDelay : 0;
    }

    /// <summary><c>lame_get_encoder_padding</c>.</summary>
    public static int GetEncoderPadding(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvEnc.EncoderPadding : 0;
    }

    /// <summary><c>lame_get_framesize</c>.</summary>
    public static int GetFramesize(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? 576 * gfc!.Cfg.ModeGr : 0;
    }

    /// <summary><c>lame_get_mf_samples_to_encode</c>.</summary>
    public static int GetMfSamplesToEncode(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.SvEnc.MfSamplesToEncode : 0;
    }

    // ---- the rest of set_get.c ----
    //
    // Nothing below is on the path libmp3lame.c drives, so none of it is reachable from the
    // bit-exact gates and all of it is `ported` rather than `verified` in the registry. It is
    // here because the file is not translated until every function in it is, and because an
    // application built on this port - RipShout among them - reaches for exactly these.

    /// <summary><c>lame_set_num_samples</c>.</summary>
    public static int SetNumSamples(LameGlobalFlags gfp, ulong numSamples)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.NumSamples = numSamples;    // default = 2^32-1
        return 0;
    }

    /// <summary><c>lame_get_num_samples</c>.</summary>
    public static ulong GetNumSamples(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.NumSamples : 0;

    /// <summary><c>lame_get_in_samplerate</c>.</summary>
    public static int GetInSamplerate(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.SamplerateIn : 0;

    /// <summary><c>lame_get_num_channels</c>.</summary>
    public static int GetNumChannels(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.NumChannels : 0;

    /// <summary><c>lame_set_scale_left</c>.</summary>
    public static int SetScaleLeft(LameGlobalFlags gfp, float scale)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ScaleLeft = scale;
        return 0;
    }

    /// <summary><c>lame_get_scale_left</c>.</summary>
    public static float GetScaleLeft(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ScaleLeft : 0;

    /// <summary><c>lame_set_scale_right</c>.</summary>
    public static int SetScaleRight(LameGlobalFlags gfp, float scale)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ScaleRight = scale;
        return 0;
    }

    /// <summary><c>lame_get_scale_right</c>.</summary>
    public static float GetScaleRight(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ScaleRight : 0;

    /// <summary><c>lame_get_out_samplerate</c>.</summary>
    public static int GetOutSamplerate(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.SamplerateOut : 0;

    /// <summary><c>lame_set_analysis</c> - collect data for the frame analyser.</summary>
    public static int SetAnalysis(LameGlobalFlags gfp, int analysis)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (analysis is < 0 or > 1) return -1;
        gfp.Analysis = analysis;
        return 0;
    }

    /// <summary><c>lame_get_analysis</c>.</summary>
    public static int GetAnalysis(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Analysis : 0;

    /// <summary><c>lame_get_bWriteVbrTag</c>.</summary>
    public static int GetWriteVbrTag(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.WriteLameTag : 0;

    /// <summary><c>lame_set_decode_only</c>.</summary>
    public static int SetDecodeOnly(LameGlobalFlags gfp, int decodeOnly)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (decodeOnly is < 0 or > 1) return -1;
        gfp.DecodeOnly = decodeOnly;
        return 0;
    }

    /// <summary><c>lame_get_decode_only</c>.</summary>
    public static int GetDecodeOnly(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.DecodeOnly : 0;

    /// <summary><c>lame_set_ogg</c> - deprecated; the C ignores both arguments.</summary>
    public static int SetOgg(LameGlobalFlags gfp, int ogg) => -1;

    /// <summary><c>lame_get_ogg</c> - deprecated; the C ignores its argument.</summary>
    public static int GetOgg(LameGlobalFlags gfp) => 0;

    /// <summary><c>lame_get_quality</c>.</summary>
    public static int GetQuality(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Quality : 0;

    /// <summary><c>lame_get_mode</c>.</summary>
    public static MpegMode GetMode(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Mode : MpegMode.NotSet;

    /// <summary><c>lame_set_mode_automs</c> - deprecated.</summary>
    /// <remarks>
    /// It validates its argument and then ignores it: any accepted value selects joint stereo,
    /// because the switching threshold it used to control is now unconditional.
    /// </remarks>
    public static int SetModeAutoms(LameGlobalFlags gfp, int modeAutoms)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (modeAutoms is < 0 or > 1) return -1;
        SetMode(gfp, MpegMode.JointStereo);
        return 0;
    }

    /// <summary><c>lame_get_mode_automs</c> - deprecated; always 1.</summary>
    public static int GetModeAutoms(LameGlobalFlags gfp) => 1;

    /// <summary><c>lame_set_force_ms</c> - force M/S on every frame. For testing only.</summary>
    public static int SetForceMs(LameGlobalFlags gfp, int forceMs)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (forceMs is < 0 or > 1) return -1;
        gfp.ForceMs = forceMs;
        return 0;
    }

    /// <summary><c>lame_get_force_ms</c>.</summary>
    public static int GetForceMs(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ForceMs : 0;

    /// <summary><c>lame_set_free_format</c>.</summary>
    public static int SetFreeFormat(LameGlobalFlags gfp, int freeFormat)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (freeFormat is < 0 or > 1) return -1;
        gfp.FreeFormat = freeFormat;
        return 0;
    }

    /// <summary><c>lame_get_free_format</c>.</summary>
    public static int GetFreeFormat(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.FreeFormat : 0;

    /// <summary><c>lame_set_findReplayGain</c>.</summary>
    /// <remarks>
    /// The flag is settable, but <c>gain_analysis.c</c> is out of scope and nothing in this
    /// port reads it, so the analysis never runs and <see cref="GetRadioGain"/> stays zero.
    /// </remarks>
    public static int SetFindReplayGain(LameGlobalFlags gfp, int findReplayGain)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (findReplayGain is < 0 or > 1) return -1;
        gfp.FindReplayGain = findReplayGain;
        return 0;
    }

    /// <summary><c>lame_get_findReplayGain</c>.</summary>
    public static int GetFindReplayGain(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.FindReplayGain : 0;

    /// <summary><c>lame_set_decode_on_the_fly</c>.</summary>
    /// <remarks>
    /// The C body is inside <c>#ifdef DECODE_ON_THE_FLY</c> and returns -1 when the decoder
    /// was not built in. This is the built-in branch, which is the default configuration,
    /// but the decoding itself is <c>mpglib</c> and out of scope, so the flag is only stored.
    /// </remarks>
    public static int SetDecodeOnTheFly(LameGlobalFlags gfp, int decodeOnTheFly)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (decodeOnTheFly is < 0 or > 1) return -1;
        gfp.DecodeOnTheFly = decodeOnTheFly;
        return 0;
    }

    /// <summary><c>lame_get_decode_on_the_fly</c>.</summary>
    public static int GetDecodeOnTheFly(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.DecodeOnTheFly : 0;

    /// <summary><c>lame_set_findPeakSample</c> - deprecated alias.</summary>
    public static int SetFindPeakSample(LameGlobalFlags gfp, int arg)
        => SetDecodeOnTheFly(gfp, arg);

    /// <summary><c>lame_get_findPeakSample</c> - deprecated alias.</summary>
    public static int GetFindPeakSample(LameGlobalFlags gfp) => GetDecodeOnTheFly(gfp);

    /// <summary><c>lame_set_ReplayGain_input</c> - deprecated alias.</summary>
    public static int SetReplayGainInput(LameGlobalFlags gfp, int arg)
        => SetFindReplayGain(gfp, arg);

    /// <summary><c>lame_get_ReplayGain_input</c> - deprecated alias.</summary>
    public static int GetReplayGainInput(LameGlobalFlags gfp) => GetFindReplayGain(gfp);

    /// <summary><c>lame_set_ReplayGain_decode</c> - deprecated; sets both flags.</summary>
    public static int SetReplayGainDecode(LameGlobalFlags gfp, int arg)
        => SetDecodeOnTheFly(gfp, arg) < 0 || SetFindReplayGain(gfp, arg) < 0 ? -1 : 0;

    /// <summary><c>lame_get_ReplayGain_decode</c> - deprecated; 1 only if both are on.</summary>
    public static int GetReplayGainDecode(LameGlobalFlags gfp)
        => GetDecodeOnTheFly(gfp) > 0 && GetFindReplayGain(gfp) > 0 ? 1 : 0;

    /// <summary><c>lame_set_nogap_total</c>.</summary>
    public static int SetNogapTotal(LameGlobalFlags gfp, int theNogapTotal)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.NogapTotal = theNogapTotal;
        return 0;
    }

    /// <summary><c>lame_get_nogap_total</c>.</summary>
    public static int GetNogapTotal(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.NogapTotal : 0;

    /// <summary><c>lame_set_nogap_currentindex</c>.</summary>
    public static int SetNogapCurrentindex(LameGlobalFlags gfp, int theNogapIndex)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.NogapCurrent = theNogapIndex;
        return 0;
    }

    /// <summary><c>lame_get_nogap_currentindex</c>.</summary>
    public static int GetNogapCurrentindex(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.NogapCurrent : 0;

    /// <summary><c>lame_set_compression_ratio</c>.</summary>
    public static int SetCompressionRatio(LameGlobalFlags gfp, float compressionRatio)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.CompressionRatio = compressionRatio;
        return 0;
    }

    /// <summary><c>lame_get_compression_ratio</c>.</summary>
    public static float GetCompressionRatio(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.CompressionRatio : 0;

    /// <summary><c>lame_get_copyright</c>.</summary>
    public static int GetCopyright(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Copyright : 0;

    /// <summary><c>lame_get_original</c>.</summary>
    public static int GetOriginal(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Original : 0;

    /// <summary><c>lame_set_error_protection</c> - two CRC bytes in every frame.</summary>
    public static int SetErrorProtection(LameGlobalFlags gfp, int errorProtection)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (errorProtection is < 0 or > 1) return -1;
        gfp.ErrorProtection = errorProtection;
        return 0;
    }

    /// <summary><c>lame_get_error_protection</c>.</summary>
    public static int GetErrorProtection(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ErrorProtection : 0;

    /// <summary><c>lame_set_padding_type</c> - obsolete; the C ignores both arguments.</summary>
    public static int SetPaddingType(LameGlobalFlags gfp, PaddingType paddingType) => 0;

    /// <summary><c>lame_get_padding_type</c> - obsolete; always PAD_ADJUST.</summary>
    public static PaddingType GetPaddingType(LameGlobalFlags gfp) => PaddingType.PadAdjust;

    /// <summary><c>lame_set_extension</c> - the header's private bit. Meaningless.</summary>
    public static int SetExtension(LameGlobalFlags gfp, int extension)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (extension is < 0 or > 1) return -1;
        gfp.Extension = extension;
        return 0;
    }

    /// <summary><c>lame_get_extension</c>.</summary>
    public static int GetExtension(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Extension : 0;

    /// <summary><c>lame_set_strict_ISO</c>.</summary>
    public static int SetStrictIso(LameGlobalFlags gfp, int val)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (val < LameBitstream.MdbDefault || val > LameBitstream.MdbMaximum) return -1;
        gfp.StrictIso = val;
        return 0;
    }

    /// <summary><c>lame_get_strict_ISO</c>.</summary>
    public static int GetStrictIso(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.StrictIso : 0;

    /// <summary><c>lame_get_disable_reservoir</c>.</summary>
    public static int GetDisableReservoir(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.DisableReservoir : 0;

    /// <summary><c>lame_set_experimentalX</c> - both quant_comp settings at once.</summary>
    public static int SetExperimentalX(LameGlobalFlags gfp, int experimentalX)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        SetQuantComp(gfp, experimentalX);
        SetQuantCompShort(gfp, experimentalX);
        return 0;
    }

    /// <summary><c>lame_get_experimentalX</c>.</summary>
    public static int GetExperimentalX(LameGlobalFlags gfp) => GetQuantComp(gfp);

    /// <summary><c>lame_get_experimentalY</c>.</summary>
    public static int GetExperimentalY(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ExperimentalY : 0;

    /// <summary><c>lame_set_experimentalZ</c>.</summary>
    public static int SetExperimentalZ(LameGlobalFlags gfp, int experimentalZ)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ExperimentalZ = experimentalZ;
        return 0;
    }

    /// <summary><c>lame_get_experimentalZ</c>.</summary>
    public static int GetExperimentalZ(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.ExperimentalZ : 0;

    /// <summary><c>lame_get_VBR_quality</c>.</summary>
    public static float GetVbrQuality(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrQ + gfp.VbrQFrac : 0;

    /// <summary><c>lame_set_VBR_min_bitrate_kbps</c>.</summary>
    public static int SetVbrMinBitrateKbps(LameGlobalFlags gfp, int kbps)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.VbrMinBitrateKbps = kbps;
        return 0;
    }

    /// <summary><c>lame_get_VBR_min_bitrate_kbps</c>.</summary>
    public static int GetVbrMinBitrateKbps(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrMinBitrateKbps : 0;

    /// <summary><c>lame_set_VBR_max_bitrate_kbps</c>.</summary>
    public static int SetVbrMaxBitrateKbps(LameGlobalFlags gfp, int kbps)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.VbrMaxBitrateKbps = kbps;
        return 0;
    }

    /// <summary><c>lame_get_VBR_max_bitrate_kbps</c>.</summary>
    public static int GetVbrMaxBitrateKbps(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrMaxBitrateKbps : 0;

    /// <summary>
    /// <c>lame_set_VBR_hard_min</c> - hold the minimum bitrate even through analog silence.
    /// </summary>
    public static int SetVbrHardMin(LameGlobalFlags gfp, int vbrHardMin)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (vbrHardMin is < 0 or > 1) return -1;
        gfp.VbrHardMin = vbrHardMin;
        return 0;
    }

    /// <summary><c>lame_get_VBR_hard_min</c>.</summary>
    public static int GetVbrHardMin(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.VbrHardMin : 0;

    /// <summary><c>lame_get_lowpassfreq</c>.</summary>
    public static int GetLowpassfreq(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Lowpassfreq : 0;

    /// <summary><c>lame_set_lowpasswidth</c>.</summary>
    public static int SetLowpasswidth(LameGlobalFlags gfp, int lowpasswidth)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Lowpasswidth = lowpasswidth;
        return 0;
    }

    /// <summary><c>lame_get_lowpasswidth</c>.</summary>
    public static int GetLowpasswidth(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Lowpasswidth : 0;

    /// <summary><c>lame_set_highpassfreq</c>.</summary>
    public static int SetHighpassfreq(LameGlobalFlags gfp, int highpassfreq)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Highpassfreq = highpassfreq;
        return 0;
    }

    /// <summary><c>lame_get_highpassfreq</c>.</summary>
    public static int GetHighpassfreq(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Highpassfreq : 0;

    /// <summary><c>lame_set_highpasswidth</c>.</summary>
    public static int SetHighpasswidth(LameGlobalFlags gfp, int highpasswidth)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Highpasswidth = highpasswidth;
        return 0;
    }

    /// <summary><c>lame_get_highpasswidth</c>.</summary>
    public static int GetHighpasswidth(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Highpasswidth : 0;

    /// <summary><c>lame_set_ATHonly</c> - use the ATH alone for masking.</summary>
    public static int SetAthOnly(LameGlobalFlags gfp, int athOnly)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthOnly = athOnly;
        return 0;
    }

    /// <summary><c>lame_get_ATHonly</c>.</summary>
    public static int GetAthOnly(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthOnly : 0;

    /// <summary><c>lame_set_ATHshort</c> - use the ATH alone for short blocks.</summary>
    public static int SetAthShort(LameGlobalFlags gfp, int athShort)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthShort = athShort;
        return 0;
    }

    /// <summary><c>lame_get_ATHshort</c>.</summary>
    public static int GetAthShort(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthShort : 0;

    /// <summary><c>lame_set_noATH</c>.</summary>
    public static int SetNoAth(LameGlobalFlags gfp, int noAth)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.NoAth = noAth;
        return 0;
    }

    /// <summary><c>lame_get_noATH</c>.</summary>
    public static int GetNoAth(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.NoAth : 0;

    /// <summary><c>lame_get_ATHtype</c>.</summary>
    public static int GetAthType(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthType : 0;

    /// <summary><c>lame_set_athaa_type</c>.</summary>
    public static int SetAthaaType(LameGlobalFlags gfp, int athaaType)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.AthaaType = athaaType;
        return 0;
    }

    /// <summary><c>lame_get_athaa_type</c>.</summary>
    public static int GetAthaaType(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.AthaaType : 0;

    /// <summary><c>lame_set_athaa_loudapprox</c> - obsolete; the C ignores both arguments.</summary>
    public static int SetAthaaLoudapprox(LameGlobalFlags gfp, int athaaLoudapprox) => 0;

    /// <summary>
    /// <c>lame_get_athaa_loudapprox</c> - obsolete; always 2, the only surviving type.
    /// </summary>
    public static int GetAthaaLoudapprox(LameGlobalFlags gfp) => 2;

    /// <summary><c>lame_set_cwlimit</c> - obsolete; the C ignores both arguments.</summary>
    public static int SetCwlimit(LameGlobalFlags gfp, int cwlimit) => 0;

    /// <summary><c>lame_get_cwlimit</c> - obsolete; always 0.</summary>
    public static int GetCwlimit(LameGlobalFlags gfp) => 0;

    /// <summary><c>lame_set_allow_diff_short</c> - may L and R choose block types freely?</summary>
    public static int SetAllowDiffShort(LameGlobalFlags gfp, int allowDiffShort)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.ShortBlocks = allowDiffShort != 0 ? ShortBlock.Allowed : ShortBlock.Coupled;
        return 0;
    }

    /// <summary><c>lame_get_allow_diff_short</c>.</summary>
    /// <remarks>
    /// Only <c>short_block_allowed</c> answers yes; not set, dispensed, forced and coupled
    /// all answer no, which is why this is not the inverse of the setter.
    /// </remarks>
    public static int GetAllowDiffShort(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) && gfp.ShortBlocks == ShortBlock.Allowed ? 1 : 0;

    /// <summary><c>lame_set_useTemporal</c>.</summary>
    public static int SetUseTemporal(LameGlobalFlags gfp, int useTemporal)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (useTemporal is < 0 or > 1) return -1;
        gfp.UseTemporal = useTemporal;
        return 0;
    }

    /// <summary><c>lame_get_useTemporal</c>.</summary>
    public static int GetUseTemporal(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.UseTemporal : 0;

    /// <summary><c>lame_set_substep</c> - pseudo substep noise shaping, 0 to 7.</summary>
    public static int SetSubstep(LameGlobalFlags gfp, int method)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (method is < 0 or > 7) return -1;
        gfp.SubstepShaping = method;
        return 0;
    }

    /// <summary><c>lame_get_substep</c>.</summary>
    public static int GetSubstep(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.SubstepShaping : 0;

    /// <summary><c>lame_get_sfscale</c> - whether noise_shaping is the scalefactor-scale one.</summary>
    public static int GetSfScale(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) && gfp.NoiseShaping == 2 ? 1 : 0;

    /// <summary><c>lame_set_subblock_gain</c>.</summary>
    public static int SetSubblockGain(LameGlobalFlags gfp, int sbgain)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.SubblockGain = sbgain;
        return 0;
    }

    /// <summary><c>lame_get_subblock_gain</c>.</summary>
    public static int GetSubblockGain(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.SubblockGain : 0;

    /// <summary><c>lame_set_no_short_blocks</c>.</summary>
    public static int SetNoShortBlocks(LameGlobalFlags gfp, int noShortBlocks)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (noShortBlocks is < 0 or > 1) return -1;
        gfp.ShortBlocks = noShortBlocks != 0 ? ShortBlock.Dispensed : ShortBlock.Allowed;
        return 0;
    }

    /// <summary><c>lame_get_no_short_blocks</c>.</summary>
    /// <remarks>-1 for not set, which is a third answer and not an error.</remarks>
    public static int GetNoShortBlocks(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        return gfp.ShortBlocks switch
        {
            ShortBlock.Dispensed => 1,
            ShortBlock.Allowed or ShortBlock.Coupled or ShortBlock.Forced => 0,
            _ => -1,            // short_block_not_set
        };
    }

    /// <summary><c>lame_set_force_short_blocks</c>.</summary>
    /// <remarks>
    /// Turning it off only undoes a previous force: any other setting is left alone, which is
    /// why this is not a plain assignment.
    /// </remarks>
    public static int SetForceShortBlocks(LameGlobalFlags gfp, int shortBlocks)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (shortBlocks is < 0 or > 1) return -1;

        if (shortBlocks == 1)
            gfp.ShortBlocks = ShortBlock.Forced;
        else if (gfp.ShortBlocks == ShortBlock.Forced)
            gfp.ShortBlocks = ShortBlock.Allowed;

        return 0;
    }

    /// <summary><c>lame_get_force_short_blocks</c>.</summary>
    public static int GetForceShortBlocks(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        return gfp.ShortBlocks switch
        {
            ShortBlock.Forced => 1,
            ShortBlock.Dispensed or ShortBlock.Allowed or ShortBlock.Coupled => 0,
            _ => -1,            // short_block_not_set
        };
    }

    /// <summary><c>lame_set_short_threshold</c> - both thresholds at once.</summary>
    public static int SetShortThreshold(LameGlobalFlags gfp, float lrm, float s)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        SetShortThresholdLrm(gfp, lrm);
        SetShortThresholdS(gfp, s);
        return 0;
    }

    /// <summary><c>lame_set_emphasis</c> - the input PCM is pre-emphasised.</summary>
    /// <remarks>
    /// The C's own comment advises against using this: the psychoacoustic model does not take
    /// it into account and most decoders ignore the bits.
    /// </remarks>
    public static int SetEmphasis(LameGlobalFlags gfp, int emphasis)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        if (emphasis is < 0 or >= 4) return -1;
        gfp.Emphasis = emphasis;
        return 0;
    }

    /// <summary><c>lame_get_emphasis</c>.</summary>
    public static int GetEmphasis(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.Emphasis : 0;

    // ---- internal values, readable only ----

    /// <summary><c>lame_get_version</c> - 0 MPEG-2, 1 MPEG-1, 2 MPEG-2.5.</summary>
    public static int GetVersion(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.Cfg.Version : 0;
    }

    /// <summary><c>lame_get_frameNum</c> - frames encoded so far.</summary>
    public static int GetFrameNum(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvEnc.FrameNumber : 0;
    }

    /// <summary><c>lame_get_size_mp3buffer</c> - bytes a flush would produce right now.</summary>
    public static int GetSizeMp3Buffer(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        if (!IsLameInternalFlagsValid(gfc)) return 0;
        LameBitstream.ComputeFlushbits(gfc!, out int size);
        return size;
    }

    /// <summary><c>lame_get_RadioGain</c>.</summary>
    /// <remarks>
    /// Always zero here: <c>gain_analysis.c</c> is out of scope, so nothing ever writes
    /// <c>ov_rpg</c>. The accessor is the C's, and the zero is honest rather than a stub.
    /// </remarks>
    public static int GetRadioGain(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvRpg.RadioGain : 0;
    }

    /// <summary><c>lame_get_AudiophileGain</c> - zero in the C too, whatever the state.</summary>
    public static int GetAudiophileGain(LameGlobalFlags gfp) => 0;

    /// <summary><c>lame_get_PeakSample</c>. See <see cref="GetRadioGain"/>.</summary>
    public static float GetPeakSample(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? (float)gfc!.OvRpg.PeakSample : 0;
    }

    /// <summary><c>lame_get_noclipGainChange</c>. See <see cref="GetRadioGain"/>.</summary>
    public static int GetNoclipGainChange(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvRpg.NoclipGainChange : 0;
    }

    /// <summary><c>lame_get_noclipScale</c>. See <see cref="GetRadioGain"/>.</summary>
    public static float GetNoclipScale(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc) ? gfc!.OvRpg.NoclipScale : 0;
    }

    /// <summary><c>lame_get_totalframes</c> - only meaningful if num_samples was set.</summary>
    /// <remarks>
    /// The estimate, not a count. The resampling branch works in double because the sample
    /// count can exceed what a float holds exactly, and it gives up rather than overflow;
    /// both are the C's, including the <c>(0ul-1ul)</c> sentinel for "unknown length".
    /// </remarks>
    public static int GetTotalframes(LameGlobalFlags gfp)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return 0;
        LameInternalFlags? gfc = gfp.InternalFlags;
        if (!IsLameInternalFlagsValid(gfc)) return 0;

        ref SessionConfig cfg = ref gfc!.Cfg;
        ulong pcmSamplesPerFrame = (ulong)(576 * cfg.ModeGr);
        ulong pcmSamplesToEncode = gfp.NumSamples;
        int frames;

        if (pcmSamplesToEncode == ulong.MaxValue) return 0;     // unknown

        if (cfg.SamplerateIn != cfg.SamplerateOut)
        {
            // resampling, so estimate the new sample count
            double resampledSamplesToEncode = 0.0;
            if (cfg.SamplerateIn > 0)
            {
                resampledSamplesToEncode = pcmSamplesToEncode;
                resampledSamplesToEncode *= cfg.SamplerateOut;
                resampledSamplesToEncode /= cfg.SamplerateIn;
            }
            if (resampledSamplesToEncode <= 0.0) return 0;      // no estimate to give

            double framesF = Math.Floor(resampledSamplesToEncode / pcmSamplesPerFrame);
            if (framesF >= int.MaxValue - 2) return 0;          // overflow; no estimate

            frames = (int)framesF;
            resampledSamplesToEncode -= (double)frames * pcmSamplesPerFrame;
            pcmSamplesToEncode = (ulong)Math.Ceiling(resampledSamplesToEncode);
        }
        else
        {
            frames = (int)(pcmSamplesToEncode / pcmSamplesPerFrame);
            pcmSamplesToEncode -= (ulong)frames * pcmSamplesPerFrame;
        }
        pcmSamplesToEncode += 576ul;

        ulong endPadding = pcmSamplesPerFrame - pcmSamplesToEncode % pcmSamplesPerFrame;
        if (endPadding < 576ul) endPadding += pcmSamplesPerFrame;

        pcmSamplesToEncode += endPadding;
        frames += (int)(pcmSamplesToEncode / pcmSamplesPerFrame);
        return frames;
    }

    /// <summary><c>lame_set_preset</c>.</summary>
    public static int SetPreset(LameGlobalFlags gfp, int preset)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;
        gfp.Preset = preset;
        return LamePresets.ApplyPreset(gfp, preset, 1);
    }

    /// <summary><c>lame_set_write_id3tag_automatic</c>.</summary>
    public static void SetWriteId3TagAutomatic(LameGlobalFlags gfp, int v)
    {
        if (IsLameGlobalFlagsValid(gfp)) gfp.WriteId3TagAutomatic = v;
    }

    /// <summary><c>lame_get_write_id3tag_automatic</c>.</summary>
    public static int GetWriteId3TagAutomatic(LameGlobalFlags gfp)
        => IsLameGlobalFlagsValid(gfp) ? gfp.WriteId3TagAutomatic : 1;

    /// <summary><c>lame_set_tune</c> - the C calls it "another daily changing developer switch".</summary>
    public static void SetTune(LameGlobalFlags gfp, float val)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return;
        gfp.TuneValueA = val;
        gfp.Tune = 1;
    }

    /// <summary><c>lame_set_preset_expopts</c> - obsolete; the C ignores both arguments.</summary>
    public static int SetPresetExpopts(LameGlobalFlags gfp, int presetExpopts) => 0;

    /// <summary><c>lame_set_preset_notune</c> - obsolete; the C ignores both arguments.</summary>
    public static int SetPresetNotune(LameGlobalFlags gfp, int presetNotune) => 0;

    /// <summary><c>calc_maximum_input_samples_for_buffer_size</c>.</summary>
    /// <remarks>
    /// An upper bound, not an exact figure: it assumes every frame is the largest the settings
    /// allow. The <c>kbps</c> ladder is the C's, including that free format and CBR both read
    /// <c>avg_bitrate</c> and so can exceed the ladder.
    /// </remarks>
    private static int CalcMaximumInputSamplesForBufferSize(
        LameInternalFlags gfc, long bufferSize)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int pcmSamplesPerFrame = 576 * cfg.ModeGr;
        int framesPerBuffer, inputSamplesPerBuffer;
        int kbps;

        if (cfg.SamplerateOut < 16000) kbps = 64;
        else if (cfg.SamplerateOut < 32000) kbps = 160;
        else kbps = 320;

        if (cfg.FreeFormat != 0) kbps = cfg.AvgBitrate;
        else if (cfg.Vbr == VbrMode.Off) kbps = cfg.AvgBitrate;

        {
            const int pad = 1;
            int bpf = (cfg.Version + 1) * 72000 * kbps / cfg.SamplerateOut + pad;
            framesPerBuffer = (int)(bufferSize / bpf);
        }
        {
            double ratio = (double)cfg.SamplerateIn / cfg.SamplerateOut;
            inputSamplesPerBuffer = (int)(pcmSamplesPerFrame * framesPerBuffer * ratio);
        }
        return inputSamplesPerBuffer;
    }

    /// <summary><c>lame_get_maximum_number_of_samples</c>.</summary>
    public static int GetMaximumNumberOfSamples(LameGlobalFlags gfp, long bufferSize)
    {
        if (!IsLameGlobalFlagsValid(gfp)) return -1;    // LAME_GENERICERROR
        LameInternalFlags? gfc = gfp.InternalFlags;
        return IsLameInternalFlagsValid(gfc)
            ? CalcMaximumInputSamplesForBufferSize(gfc!, bufferSize)
            : -1;
    }
}

// Translated from lame-3.100/libmp3lame/presets.c - the tuning tables lame_init_params applies
// before it finishes.
//
// This file matters for CBR even though its name says presets: lame_init_params ends its
// cbr/abr branch with apply_preset(gfp, VBR_mean_bitrate_kbps, 0), which runs
// apply_abr_preset. So a plain 128 kbps CBR encode picks up that row's masking adjustment, ATH
// offset, msfix, attack thresholds, interchannel ratio and minval - and its **scale of 0.95**,
// which multiplies the input samples. Missing that would make every sample 0.46 dB loud.
//
// SET_OPTION IS NOT AN ASSIGNMENT. The C's macro is
//
//     if (enforce) set(val); else if (!(fabs(get(gfp) - def) > 0)) set(val);
//
// so without enforce it writes only when the current value is still exactly the default - the
// caller's own setting wins. SetOption below is that, and the comparison is the C's: exact
// equality through fabs, not a tolerance.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary>One row of <c>vbr_presets_t</c>.</summary>
internal readonly record struct VbrPreset(
    int VbrQ, int QuantComp, int QuantCompS, int ExpY,
    float StLrm, float StS, float MaskingAdj, float MaskingAdjShort,
    float AthLower, float AthCurve, float AthSensitivity, float Interch,
    int Safejoint, float Sfb21Mod, float Msfix, float Minval, float AthFixpoint);

/// <summary>One row of <c>abr_presets_t</c>.</summary>
internal readonly record struct AbrPreset(
    int AbrKbps, int QuantComp, int QuantCompS, int Safejoint, float Nsmsfix,
    float StLrm, float StS, float Scale, float MaskingAdj, float AthLower,
    float AthCurve, float Interch, int Sfscale);

/// <summary><c>presets.c</c>.</summary>
internal static class LamePresets
{
    /// <summary>The preset constants of <c>lame.h</c> that <c>apply_preset</c> switches on.</summary>
    public const int V9 = 410, V8 = 420, V7 = 430, V6 = 440, V5 = 450;
    public const int V4 = 460, V3 = 470, V2 = 480, V1 = 490, V0 = 500;

    /// <summary>The legacy names <c>apply_preset</c> translates first.</summary>
    public const int R3Mix = 1000, Medium = 1001, MediumFast = 1002;
    public const int Standard = 1003, StandardFast = 1004;
    public const int Extreme = 1005, ExtremeFast = 1006, Insane = 1007;

    /// <summary><c>vbr_old_switch_map[]</c> - the switch mappings for <c>vbr_rh</c>.</summary>
    private static readonly VbrPreset[] vbrOldSwitchMap =
    [
        new(0, 9, 9, 0, 5.20f, 125.0f, -4.2f, -6.3f, 4.8f, 1f, 0f, 0f, 2, 21f, 0.97f, 5f, 100f),
        new(1, 9, 9, 0, 5.30f, 125.0f, -3.6f, -5.6f, 4.5f, 1.5f, 0f, 0f, 2, 21f, 1.35f, 5f, 100f),
        new(2, 9, 9, 0, 5.60f, 125.0f, -2.2f, -3.5f, 2.8f, 2f, 0f, 0f, 2, 21f, 1.49f, 5f, 100f),
        new(3, 9, 9, 1, 5.80f, 130.0f, -1.8f, -2.8f, 2.6f, 3f, -4f, 0f, 2, 20f, 1.64f, 5f, 100f),
        new(4, 9, 9, 1, 6.00f, 135.0f, -0.7f, -1.1f, 1.1f, 3.5f, -8f, 0f, 2, 0f, 1.79f, 5f, 100f),
        new(5, 9, 9, 1, 6.40f, 140.0f, 0.5f, 0.4f, -7.5f, 4f, -12f, 0.0002f, 0, 0f, 1.95f, 5f, 100f),
        new(6, 9, 9, 1, 6.60f, 145.0f, 0.67f, 0.65f, -14.7f, 6.5f, -19f, 0.0004f, 0, 0f, 2.30f, 5f, 100f),
        new(7, 9, 9, 1, 6.60f, 145.0f, 0.8f, 0.75f, -19.7f, 8f, -22f, 0.0006f, 0, 0f, 2.70f, 5f, 100f),
        new(8, 9, 9, 1, 6.60f, 145.0f, 1.2f, 1.15f, -27.5f, 10f, -23f, 0.0007f, 0, 0f, 0f, 5f, 100f),
        new(9, 9, 9, 1, 6.60f, 145.0f, 1.6f, 1.6f, -36f, 11f, -25f, 0.0008f, 0, 0f, 0f, 5f, 100f),
        new(10, 9, 9, 1, 6.60f, 145.0f, 2.0f, 2.0f, -36f, 12f, -25f, 0.0008f, 0, 0f, 0f, 5f, 100f),
    ];

    /// <summary>
    /// <c>vbr_mt_psy_switch_map[]</c> - for <c>vbr_mt</c> and <c>vbr_mtrh</c>.
    /// </summary>
    /// <remarks>
    /// Rows 6 and 7 appear twice in the C, with the first pair behind <c>#if 0</c>; the live
    /// values are the ones in the <c>#else</c>.
    /// </remarks>
    private static readonly VbrPreset[] vbrMtPsySwitchMap =
    [
        new(0, 9, 9, 0, 4.20f, 25.0f, -6.8f, -6.8f, 7.1f, 1f, 0f, 0f, 2, 31f, 1.000f, 5f, 100f),
        new(1, 9, 9, 0, 4.20f, 25.0f, -4.8f, -4.8f, 5.4f, 1.4f, -1f, 0f, 2, 27f, 1.122f, 5f, 98f),
        new(2, 9, 9, 0, 4.20f, 25.0f, -2.6f, -2.6f, 3.7f, 2.0f, -3f, 0f, 2, 23f, 1.288f, 5f, 97f),
        new(3, 9, 9, 1, 4.20f, 25.0f, -1.6f, -1.6f, 2.0f, 2.0f, -5f, 0f, 2, 18f, 1.479f, 5f, 96f),
        new(4, 9, 9, 1, 4.20f, 25.0f, -0.0f, -0.0f, 0.0f, 2.0f, -8f, 0f, 2, 12f, 1.698f, 5f, 95f),
        new(5, 9, 9, 1, 4.20f, 25.0f, 1.3f, 1.3f, -6f, 3.5f, -11f, 0f, 2, 8f, 1.950f, 5f, 94.2f),
        new(6, 9, 9, 1, 4.50f, 100.0f, 2.2f, 2.3f, -12.0f, 6.0f, -14f, 0f, 2, 4f, 2.239f, 3f, 93.9f),
        new(7, 9, 9, 1, 4.80f, 200.0f, 2.7f, 2.7f, -18.0f, 9.0f, -17f, 0f, 2, 0f, 2.570f, 1f, 93.6f),
        new(8, 9, 9, 1, 5.30f, 300.0f, 2.8f, 2.8f, -21.0f, 10.0f, -23f, 0.0002f, 0, 0f, 2.951f, 0f, 93.3f),
        new(9, 9, 9, 1, 6.60f, 300.0f, 2.8f, 2.8f, -23.0f, 11.0f, -25f, 0.0006f, 0, 0f, 3.388f, 0f, 93.3f),
        new(10, 9, 9, 1, 25.00f, 300.0f, 2.8f, 2.8f, -25.0f, 12.0f, -27f, 0.0025f, 0, 0f, 3.500f, 0f, 93.3f),
    ];

    /// <summary><c>abr_switch_map[]</c> - the switch mappings for ABR, and so for CBR.</summary>
    private static readonly AbrPreset[] abrSwitchMap =
    [
        new(8, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -30.0f, 11f, 0.0012f, 1),
        new(16, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -25.0f, 11f, 0.0010f, 1),
        new(24, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -20.0f, 11f, 0.0010f, 1),
        new(32, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -15.0f, 11f, 0.0010f, 1),
        new(40, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -10.0f, 11f, 0.0009f, 1),
        new(48, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -10.0f, 11f, 0.0009f, 1),
        new(56, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -6.0f, 11f, 0.0008f, 1),
        new(64, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, -2.0f, 11f, 0.0008f, 1),
        new(80, 9, 9, 0, 0f, 6.60f, 145f, 0.95f, 0f, 0f, 8f, 0.0007f, 1),
        new(96, 9, 9, 0, 2.50f, 6.60f, 145f, 0.95f, 0f, 1.0f, 5.5f, 0.0006f, 1),
        new(112, 9, 9, 0, 2.25f, 6.60f, 145f, 0.95f, 0f, 2.0f, 4.5f, 0.0005f, 1),
        new(128, 9, 9, 0, 1.95f, 6.40f, 140f, 0.95f, 0f, 3.0f, 4f, 0.0002f, 1),
        new(160, 9, 9, 1, 1.79f, 6.00f, 135f, 0.95f, -2f, 5.0f, 3.5f, 0f, 1),
        new(192, 9, 9, 1, 1.49f, 5.60f, 125f, 0.97f, -4f, 7.0f, 3f, 0f, 0),
        new(224, 9, 9, 1, 1.25f, 5.20f, 125f, 0.98f, -6f, 9.0f, 2f, 0f, 0),
        new(256, 9, 9, 1, 0.97f, 5.20f, 125f, 1.00f, -8f, 10.0f, 1f, 0f, 0),
        new(320, 9, 9, 1, 0.90f, 5.20f, 125f, 1.00f, -10f, 12.0f, 0f, 0f, 0),
    ];

    /// <summary><c>get_vbr_preset</c>.</summary>
    private static VbrPreset[] GetVbrPreset(VbrMode v)
        => v is VbrMode.Mtrh or VbrMode.Mt ? vbrMtPsySwitchMap : vbrOldSwitchMap;

    /// <summary><c>LERP(m)</c> - <c>p.m = p.m + x * (q.m - p.m)</c>.</summary>
    private static float Lerp(float p, float q, float x) => p + x * (q - p);

    /// <summary><c>SET_OPTION</c> for a float-valued option; see the file header.</summary>
    private static void SetOption(
        LameGlobalFlags gfp, Action<LameGlobalFlags, float> set, Func<LameGlobalFlags, float> get,
        float val, float def, int enforce)
    {
        if (enforce != 0) set(gfp, val);
        else if (!(Math.Abs(get(gfp) - def) > 0)) set(gfp, val);
    }

    /// <summary><c>SET_OPTION</c> for an int-valued option.</summary>
    private static void SetOptionInt(
        LameGlobalFlags gfp, Action<LameGlobalFlags, int> set, Func<LameGlobalFlags, int> get,
        int val, int def, int enforce)
    {
        if (enforce != 0) set(gfp, val);
        else if (!(Math.Abs(get(gfp) - def) > 0)) set(gfp, val);
    }

    /// <summary><c>apply_vbr_preset</c>.</summary>
    private static void ApplyVbrPreset(LameGlobalFlags gfp, int a, int enforce)
    {
        VbrPreset[] map = GetVbrPreset(LameSetGet.GetVbr(gfp));
        float x = gfp.VbrQFrac;
        VbrPreset p = map[a];
        VbrPreset q = map[a + 1];

        // NOOP on vbr_q, quant_comp, quant_comp_s, expY and safejoint; LERP on the rest.
        var set = p with
        {
            StLrm = Lerp(p.StLrm, q.StLrm, x),
            StS = Lerp(p.StS, q.StS, x),
            MaskingAdj = Lerp(p.MaskingAdj, q.MaskingAdj, x),
            MaskingAdjShort = Lerp(p.MaskingAdjShort, q.MaskingAdjShort, x),
            AthLower = Lerp(p.AthLower, q.AthLower, x),
            AthCurve = Lerp(p.AthCurve, q.AthCurve, x),
            AthSensitivity = Lerp(p.AthSensitivity, q.AthSensitivity, x),
            Interch = Lerp(p.Interch, q.Interch, x),
            Sfb21Mod = Lerp(p.Sfb21Mod, q.Sfb21Mod, x),
            Msfix = Lerp(p.Msfix, q.Msfix, x),
            Minval = Lerp(p.Minval, q.Minval, x),
            AthFixpoint = Lerp(p.AthFixpoint, q.AthFixpoint, x),
        };

        LameSetGet.SetVbrQ(gfp, set.VbrQ);
        SetOptionInt(gfp, static (g, v) => LameSetGet.SetQuantComp(g, v),
                     LameSetGet.GetQuantComp, set.QuantComp, -1, enforce);
        SetOptionInt(gfp, static (g, v) => LameSetGet.SetQuantCompShort(g, v),
                     LameSetGet.GetQuantCompShort, set.QuantCompS, -1, enforce);
        if (set.ExpY != 0) LameSetGet.SetExperimentalY(gfp, set.ExpY);

        SetOption(gfp, static (g, v) => LameSetGet.SetShortThresholdLrm(g, v),
                  LameSetGet.GetShortThresholdLrm, set.StLrm, -1, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetShortThresholdS(g, v),
                  LameSetGet.GetShortThresholdS, set.StS, -1, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetMaskingadjust(g, v),
                  LameSetGet.GetMaskingadjust, set.MaskingAdj, 0, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetMaskingadjustShort(g, v),
                  LameSetGet.GetMaskingadjustShort, set.MaskingAdjShort, 0, enforce);

        if (LameSetGet.GetVbr(gfp) is VbrMode.Mt or VbrMode.Mtrh)
            LameSetGet.SetAthType(gfp, 5);

        SetOption(gfp, static (g, v) => LameSetGet.SetAthLower(g, v),
                  LameSetGet.GetAthLower, set.AthLower, 0, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetAthCurve(g, v),
                  LameSetGet.GetAthCurve, set.AthCurve, -1, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetAthaaSensitivity(g, v),
                  LameSetGet.GetAthaaSensitivity, set.AthSensitivity, 0, enforce);
        if (set.Interch > 0)
            SetOption(gfp, static (g, v) => LameSetGet.SetInterChRatio(g, v),
                      LameSetGet.GetInterChRatio, set.Interch, -1, enforce);

        // the two with no proper set/get interface
        if (set.Safejoint > 0)
            LameSetGet.SetExpNsPsytune(gfp, LameSetGet.GetExpNsPsytune(gfp) | 2);

        if (set.Sfb21Mod > 0)
        {
            int nsp = LameSetGet.GetExpNsPsytune(gfp);
            int val = (nsp >> 20) & 63;
            if (val == 0)
                LameSetGet.SetExpNsPsytune(gfp, ((int)set.Sfb21Mod << 20) | nsp);
        }

        SetOption(gfp, static (g, v) => LameSetGet.SetMsfix(g, v),
                  LameSetGet.GetMsfix, set.Msfix, -1, enforce);

        if (enforce == 0)
        {
            gfp.VbrQ = a;
            gfp.VbrQFrac = x;
        }
        gfp.InternalFlags!.Cfg.Minval = set.Minval;
        {
            // take care of gain adjustments
            double gain = Math.Abs(gfp.Scale);
            double y = gain > 0.0f ? 10.0f * Math.Log10(gain) : 0.0f;
            gfp.InternalFlags.Cfg.AthFixpoint = (float)(set.AthFixpoint - y);
        }
    }

    /// <summary><c>apply_abr_preset</c> - also the path a plain CBR encode takes.</summary>
    private static int ApplyAbrPreset(LameGlobalFlags gfp, int preset, int enforce)
    {
        int actualBitrate = preset;
        int r = LameUtil.NearestBitrateFullIndex(preset);

        LameSetGet.SetVbr(gfp, VbrMode.Abr);
        LameSetGet.SetVbrMeanBitrateKbps(gfp, actualBitrate);
        LameSetGet.SetVbrMeanBitrateKbps(
            gfp, Math.Min(LameSetGet.GetVbrMeanBitrateKbps(gfp), 320));
        LameSetGet.SetVbrMeanBitrateKbps(
            gfp, Math.Max(LameSetGet.GetVbrMeanBitrateKbps(gfp), 8));
        LameSetGet.SetBrate(gfp, LameSetGet.GetVbrMeanBitrateKbps(gfp));

        // the two with no proper set/get interface
        if (abrSwitchMap[r].Safejoint > 0)
            LameSetGet.SetExpNsPsytune(gfp, LameSetGet.GetExpNsPsytune(gfp) | 2);

        if (abrSwitchMap[r].Sfscale > 0)
            LameSetGet.SetSfScale(gfp, 1);

        SetOptionInt(gfp, static (g, v) => LameSetGet.SetQuantComp(g, v),
                     LameSetGet.GetQuantComp, abrSwitchMap[r].QuantComp, -1, enforce);
        SetOptionInt(gfp, static (g, v) => LameSetGet.SetQuantCompShort(g, v),
                     LameSetGet.GetQuantCompShort, abrSwitchMap[r].QuantCompS, -1, enforce);

        SetOption(gfp, static (g, v) => LameSetGet.SetMsfix(g, v),
                  LameSetGet.GetMsfix, abrSwitchMap[r].Nsmsfix, -1, enforce);

        SetOption(gfp, static (g, v) => LameSetGet.SetShortThresholdLrm(g, v),
                  LameSetGet.GetShortThresholdLrm, abrSwitchMap[r].StLrm, -1, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetShortThresholdS(g, v),
                  LameSetGet.GetShortThresholdS, abrSwitchMap[r].StS, -1, enforce);

        // ABR clips badly at low bitrates, so the input is scaled down to compensate.
        LameSetGet.SetScale(gfp, LameSetGet.GetScale(gfp) * abrSwitchMap[r].Scale);

        SetOption(gfp, static (g, v) => LameSetGet.SetMaskingadjust(g, v),
                  LameSetGet.GetMaskingadjust, abrSwitchMap[r].MaskingAdj, 0, enforce);
        if (abrSwitchMap[r].MaskingAdj > 0)
            SetOption(gfp, static (g, v) => LameSetGet.SetMaskingadjustShort(g, v),
                      LameSetGet.GetMaskingadjustShort,
                      abrSwitchMap[r].MaskingAdj * .9f, 0, enforce);
        else
            SetOption(gfp, static (g, v) => LameSetGet.SetMaskingadjustShort(g, v),
                      LameSetGet.GetMaskingadjustShort,
                      abrSwitchMap[r].MaskingAdj * 1.1f, 0, enforce);

        SetOption(gfp, static (g, v) => LameSetGet.SetAthLower(g, v),
                  LameSetGet.GetAthLower, abrSwitchMap[r].AthLower, 0, enforce);
        SetOption(gfp, static (g, v) => LameSetGet.SetAthCurve(g, v),
                  LameSetGet.GetAthCurve, abrSwitchMap[r].AthCurve, -1, enforce);

        SetOption(gfp, static (g, v) => LameSetGet.SetInterChRatio(g, v),
                  LameSetGet.GetInterChRatio, abrSwitchMap[r].Interch, -1, enforce);

        gfp.InternalFlags!.Cfg.Minval = (float)(5.0 * (abrSwitchMap[r].AbrKbps / 320.0));

        return preset;
    }

    /// <summary><c>apply_preset</c>.</summary>
    public static int ApplyPreset(LameGlobalFlags gfp, int preset, int enforce)
    {
        // translate the legacy presets
        switch (preset)
        {
            case R3Mix:
                preset = V3;
                LameSetGet.SetVbr(gfp, VbrMode.Mtrh);
                break;
            case Medium:
            case MediumFast:
                preset = V4;
                LameSetGet.SetVbr(gfp, VbrMode.Mtrh);
                break;
            case Standard:
            case StandardFast:
                preset = V2;
                LameSetGet.SetVbr(gfp, VbrMode.Mtrh);
                break;
            case Extreme:
            case ExtremeFast:
                preset = V0;
                LameSetGet.SetVbr(gfp, VbrMode.Mtrh);
                break;
            case Insane:
                preset = 320;
                gfp.Preset = preset;
                ApplyAbrPreset(gfp, preset, enforce);
                LameSetGet.SetVbr(gfp, VbrMode.Off);
                return preset;
        }

        gfp.Preset = preset;
        switch (preset)
        {
            case V9: ApplyVbrPreset(gfp, 9, enforce); return preset;
            case V8: ApplyVbrPreset(gfp, 8, enforce); return preset;
            case V7: ApplyVbrPreset(gfp, 7, enforce); return preset;
            case V6: ApplyVbrPreset(gfp, 6, enforce); return preset;
            case V5: ApplyVbrPreset(gfp, 5, enforce); return preset;
            case V4: ApplyVbrPreset(gfp, 4, enforce); return preset;
            case V3: ApplyVbrPreset(gfp, 3, enforce); return preset;
            case V2: ApplyVbrPreset(gfp, 2, enforce); return preset;
            case V1: ApplyVbrPreset(gfp, 1, enforce); return preset;
            case V0: ApplyVbrPreset(gfp, 0, enforce); return preset;
            default: break;
        }

        if (preset is >= 8 and <= 320)
            return ApplyAbrPreset(gfp, preset, enforce);

        gfp.Preset = 0;
        return preset;
    }
}

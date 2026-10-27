// Translated from lame-3.100/libmp3lame/lame.c - the public encoder API, and
// lame_init_params, which turns the caller's settings into the SessionConfig everything else
// reads.
//
// SCOPE. The functions libmp3lame.c calls, plus what they call internally. The analyser
// printers (lame_print_config, lame_print_internals), the ID3 and Xing tag paths, the
// ReplayGain and decode-on-the-fly paths, the histogram readers and the deprecated
// lame_encode_buffer variants ffmpeg does not use stay todo in the port checklist rather
// than being written out for the sake of a count.
//
// WHY THIS FILE DECIDES BIT-EXACTNESS. Everything downstream reads cfg. If lame_init_params
// derives one field differently from the reference, every frame differs and no earlier gate
// says a word. Three of its steps are easy to miss and all three change the output:
//
//   1. It calls apply_preset(gfp, VBR_mean_bitrate_kbps, 0) even for plain CBR, which runs the
//      ABR table and so sets masking_adjust, ATH_lower, msfix, the attack thresholds, minval
//      and - the one with teeth - **scale = 0.95** at 128 kbps. The input is multiplied by it.
//   2. lame_init_qval turns gfp->quality into noise_shaping, noise_shaping_amp,
//      use_best_huffman, full_outer_loop and substep_shaping. The default quality is 3, not 5.
//   3. The polyphase filter's per-band amplitude, amp_filter[32], comes from
//      lame_init_params_ppflt and the lowpass that optimum_bandwidth chose from the bitrate.
//      At 128 kbps that is 17 kHz, and the bands above it are zeroed before quantisation.
//
// The C limps on three internal errors; see the notes at each site. This throws instead, as
// bitstream.c does, because each one means the configuration is already wrong.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>lame.c</c>.</summary>
internal static class LameApi
{
    /// <summary><c>LAME_DEFAULT_QUALITY</c> (<c>lame.h</c>).</summary>
    public const int LameDefaultQuality = 3;

    /// <summary><c>MAX_U_32_NUM</c>.</summary>
    private const ulong MaxU32Num = 0xFFFFFFFF;

    /// <summary>
    /// <c>freq_map[]</c> of <c>optimum_bandwidth</c> - the best lowpass for a bitrate.
    /// </summary>
    private static readonly int[] bandPassLowpass =
    [
        2000, 3700, 3900, 5500, 7000, 7500, 10000, 11000, 13500,
        15100, 15600, 17000, 17500, 18600, 19400, 19700, 20500,
    ];

    /// <summary><c>filter_coef</c>.</summary>
    private static float FilterCoef(float x)
    {
        if (x > 1.0f) return 0.0f;
        if (x <= 0.0f) return 1.0f;
        return (float)Math.Cos(Math.PI / 2 * x);
    }

    /// <summary>
    /// <c>lame_init_params_ppflt</c> - the polyphase filter's per-band amplitude.
    /// </summary>
    /// <remarks>
    /// The requested lowpass and highpass are snapped to what the 32-band filterbank can
    /// actually do, and then each band gets a raised-cosine taper. <c>amp_filter[]</c> is read
    /// by <c>mdct_sub48</c>, which zeroes a band outright below 1e-12.
    /// </remarks>
    private static void LameInitParamsPpflt(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;

        int band, maxband, minband;
        float freq;
        int lowpassBand = 32;
        int highpassBand = -1;

        if (cfg.Lowpass1 > 0)
        {
            minband = 999;
            for (band = 0; band <= 31; band++)
            {
                freq = (float)(band / 31.0);
                // this band and above will be zeroed
                if (freq >= cfg.Lowpass2) lowpassBand = Math.Min(lowpassBand, band);
                if (cfg.Lowpass1 < freq && freq < cfg.Lowpass2)
                    minband = Math.Min(minband, band);
            }

            // the transition band the polyphase filter actually implements
            if (minband == 999)
                cfg.Lowpass1 = (float)((lowpassBand - .75) / 31.0);
            else
                cfg.Lowpass1 = (float)((minband - .75) / 31.0);
            cfg.Lowpass2 = (float)(lowpassBand / 31.0);
        }

        // keep the highpass within 90% of what it will effectively be
        if (cfg.Highpass2 > 0)
        {
            if (cfg.Highpass2 < .9 * (.75 / 31.0))
            {
                cfg.Highpass1 = 0;
                cfg.Highpass2 = 0;
            }
        }

        if (cfg.Highpass2 > 0)
        {
            maxband = -1;
            for (band = 0; band <= 31; band++)
            {
                freq = (float)(band / 31.0);
                // this band and below will be zeroed
                if (freq <= cfg.Highpass1) highpassBand = Math.Max(highpassBand, band);
                if (cfg.Highpass1 < freq && freq < cfg.Highpass2)
                    maxband = Math.Max(maxband, band);
            }
            cfg.Highpass1 = (float)(highpassBand / 31.0);
            if (maxband == -1)
                cfg.Highpass2 = (float)((highpassBand + .75) / 31.0);
            else
                cfg.Highpass2 = (float)((maxband + .75) / 31.0);
        }

        for (band = 0; band < 32; band++)
        {
            float fc1, fc2;
            freq = band / 31.0f;
            if (cfg.Highpass2 > cfg.Highpass1)
                fc1 = FilterCoef(
                    (float)((cfg.Highpass2 - freq) / (cfg.Highpass2 - cfg.Highpass1 + 1e-20)));
            else
                fc1 = 1.0f;

            if (cfg.Lowpass2 > cfg.Lowpass1)
                fc2 = FilterCoef(
                    (float)((freq - cfg.Lowpass1) / (cfg.Lowpass2 - cfg.Lowpass1 + 1e-20)));
            else
                fc2 = 1.0f;

            gfc.SvEnc.AmpFilter![band] = fc1 * fc2;
        }
    }

    /// <summary><c>optimum_bandwidth</c> - the best lowpass for a total bitrate, in Hz.</summary>
    /// <remarks>
    /// The C also computes a highpass and then discards it; the commented-out formula and the
    /// <c>(void) upperlimit</c> are both still there, so nothing writes the second output.
    /// </remarks>
    private static void OptimumBandwidth(out double lowerlimit, int bitrate)
    {
        int tableIndex = LameUtil.NearestBitrateFullIndex(bitrate);
        lowerlimit = bandPassLowpass[tableIndex];
    }

    /// <summary>
    /// <c>optimum_samplefreq</c> - the output rate that keeps sfb21 out of play if it can.
    /// </summary>
    private static int OptimumSamplefreq(int lowpassfreq, int inputSamplefreq)
    {
        int suggested = 44100;

        if (inputSamplefreq >= 48000) suggested = 48000;
        else if (inputSamplefreq >= 44100) suggested = 44100;
        else if (inputSamplefreq >= 32000) suggested = 32000;
        else if (inputSamplefreq >= 24000) suggested = 24000;
        else if (inputSamplefreq >= 22050) suggested = 22050;
        else if (inputSamplefreq >= 16000) suggested = 16000;
        else if (inputSamplefreq >= 12000) suggested = 12000;
        else if (inputSamplefreq >= 11025) suggested = 11025;
        else if (inputSamplefreq >= 8000) suggested = 8000;

        if (lowpassfreq == -1) return suggested;

        if (lowpassfreq <= 15960) suggested = 44100;
        if (lowpassfreq <= 15250) suggested = 32000;
        if (lowpassfreq <= 11220) suggested = 24000;
        if (lowpassfreq <= 9970) suggested = 22050;
        if (lowpassfreq <= 7230) suggested = 16000;
        if (lowpassfreq <= 5420) suggested = 12000;
        if (lowpassfreq <= 4510) suggested = 11025;
        if (lowpassfreq <= 3970) suggested = 8000;

        if (inputSamplefreq < suggested)
        {
            // Choose a valid MPEG rate above the input rate, to avoid sfb21/12 bitrate bloat.
            if (inputSamplefreq > 44100) return 48000;
            if (inputSamplefreq > 32000) return 44100;
            if (inputSamplefreq > 24000) return 32000;
            if (inputSamplefreq > 22050) return 24000;
            if (inputSamplefreq > 16000) return 22050;
            if (inputSamplefreq > 12000) return 16000;
            if (inputSamplefreq > 11025) return 12000;
            if (inputSamplefreq > 8000) return 11025;
            return 8000;
        }
        return suggested;
    }

    /// <summary>
    /// <c>lame_init_qval</c> - the algorithm flags that <c>quality</c> stands for.
    /// </summary>
    /// <remarks>
    /// Case 8 assigns <c>quality = 7</c> and falls through into case 7 in the C; C# has no
    /// fallthrough, so the two share an arm through <c>goto</c>.
    /// </remarks>
    private static void LameInitQval(LameGlobalFlags gfp)
    {
        LameInternalFlags gfc = gfp.InternalFlags!;
        ref SessionConfig cfg = ref gfc.Cfg;

        switch (gfp.Quality)
        {
            default:
            case 9:     // no psymodel, no noise shaping
                cfg.NoiseShaping = 0;
                cfg.NoiseShapingAmp = 0;
                cfg.NoiseShapingStop = 0;
                cfg.UseBestHuffman = 0;
                cfg.FullOuterLoop = 0;
                break;

            case 8:
                gfp.Quality = 7;
                goto case 7;
            case 7:     // psymodel for short blocks and m/s, but no noise shaping
                cfg.NoiseShaping = 0;
                cfg.NoiseShapingAmp = 0;
                cfg.NoiseShapingStop = 0;
                cfg.UseBestHuffman = 0;
                cfg.FullOuterLoop = 0;
                if (cfg.Vbr == VbrMode.Mt || cfg.Vbr == VbrMode.Mtrh)
                    cfg.FullOuterLoop = -1;
                break;

            case 6:
            case 5:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                cfg.NoiseShapingAmp = 0;
                cfg.NoiseShapingStop = 0;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                cfg.UseBestHuffman = 0;
                cfg.FullOuterLoop = 0;
                break;

            case 4:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                cfg.NoiseShapingAmp = 0;
                cfg.NoiseShapingStop = 0;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                cfg.UseBestHuffman = 1;
                cfg.FullOuterLoop = 0;
                break;

            case 3:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                cfg.NoiseShapingAmp = 1;
                cfg.NoiseShapingStop = 1;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                cfg.UseBestHuffman = 1;
                cfg.FullOuterLoop = 0;
                break;

            case 2:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                if (gfc.SvQnt.SubstepShaping == 0) gfc.SvQnt.SubstepShaping = 2;
                cfg.NoiseShapingAmp = 1;
                cfg.NoiseShapingStop = 1;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                cfg.UseBestHuffman = 1;     // inner loop
                cfg.FullOuterLoop = 0;
                break;

            case 1:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                if (gfc.SvQnt.SubstepShaping == 0) gfc.SvQnt.SubstepShaping = 2;
                cfg.NoiseShapingAmp = 2;
                cfg.NoiseShapingStop = 1;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                cfg.UseBestHuffman = 1;
                cfg.FullOuterLoop = 0;
                break;

            case 0:
                if (cfg.NoiseShaping == 0) cfg.NoiseShaping = 1;
                if (gfc.SvQnt.SubstepShaping == 0) gfc.SvQnt.SubstepShaping = 2;
                cfg.NoiseShapingAmp = 2;
                cfg.NoiseShapingStop = 1;
                if (cfg.SubblockGain == -1) cfg.SubblockGain = 1;
                // type 2 is disabled for slowness, in favour of the full outer loop search
                cfg.UseBestHuffman = 1;
                cfg.FullOuterLoop = 1;
                break;
        }
    }

    /// <summary><c>linear_int</c>.</summary>
    private static double LinearInt(double a, double b, double m) => a + m * (b - a);

    /// <summary>
    /// <c>lame_init_params</c> - turn the caller's settings into the session configuration.
    /// </summary>
    /// <returns>0 on success, -1 if the settings make no sense.</returns>
    public static int LameInitParams(LameGlobalFlags gfp)
    {
        if (!LameSetGet.IsLameGlobalFlagsValid(gfp)) return -1;

        LameInternalFlags? gfcOrNull = gfp.InternalFlags;
        if (gfcOrNull is null) return -1;
        LameInternalFlags gfc = gfcOrNull;

        if (LameSetGet.IsLameInternalFlagsValid(gfc)) return -1;    // already initialised

        gfc.ClassId = LameInternalFlags.LameId;
        gfc.LameInitParamsSuccessful = 0;   // set to one only if we reach the end

        if (gfp.SamplerateIn < 1) return -1;
        if (gfp.NumChannels is < 1 or > 2) return -1;
        if (gfp.SamplerateOut != 0 && LameUtil.SmpFrqIndex(gfp.SamplerateOut, out _) < 0)
            return -1;

        ref SessionConfig cfg = ref gfc.Cfg;

        cfg.EnforceMinBitrate = gfp.VbrHardMin;
        cfg.Analysis = gfp.Analysis;
        if (cfg.Analysis != 0) gfp.WriteLameTag = 0;

        // The CPU feature detection is a divergence: there are no hand-written kernels here,
        // so has_MMX, has_3DNow, has_SSE and has_SSE2 have nothing to select.

        cfg.Vbr = gfp.Vbr;
        cfg.ErrorProtection = gfp.ErrorProtection;
        cfg.Copyright = gfp.Copyright;
        cfg.Original = gfp.Original;
        cfg.Extension = gfp.Extension;
        cfg.Emphasis = gfp.Emphasis;

        cfg.ChannelsIn = gfp.NumChannels;
        if (cfg.ChannelsIn == 1) gfp.Mode = MpegMode.Mono;
        cfg.ChannelsOut = gfp.Mode == MpegMode.Mono ? 1 : 2;
        if (gfp.Mode != MpegMode.JointStereo) gfp.ForceMs = 0;
        cfg.ForceMs = gfp.ForceMs;

        if (cfg.Vbr == VbrMode.Off && gfp.VbrMeanBitrateKbps != 128 && gfp.Brate == 0)
            gfp.Brate = gfp.VbrMeanBitrateKbps;

        switch (cfg.Vbr)
        {
            case VbrMode.Off:
            case VbrMode.Mtrh:
            case VbrMode.Mt:
                break;      // these modes can handle free format
            default:
                gfp.FreeFormat = 0;
                break;
        }

        cfg.FreeFormat = gfp.FreeFormat;

        if (cfg.Vbr == VbrMode.Off && gfp.Brate == 0)
        {
            // no bitrate or compression ratio given; 11.025 takes a CD to exactly 128000 bps
            if (gfp.CompressionRatio == 0) gfp.CompressionRatio = 11.025f;
        }

        if (cfg.Vbr == VbrMode.Off && gfp.CompressionRatio > 0)
        {
            if (gfp.SamplerateOut == 0)
                gfp.SamplerateOut = LameUtil.Map2Mp3Frequency((int)(0.97 * gfp.SamplerateIn));

            gfp.Brate = (int)(gfp.SamplerateOut * 16 * cfg.ChannelsOut
                              / (1.0e3 * gfp.CompressionRatio));

            cfg.SamplerateIndex = LameUtil.SmpFrqIndex(gfp.SamplerateOut, out cfg.Version);

            if (cfg.FreeFormat == 0)
                gfp.Brate = LameUtil.FindNearestBitrate(
                    gfp.Brate, cfg.Version, gfp.SamplerateOut);
        }
        if (gfp.SamplerateOut != 0)
        {
            if (gfp.SamplerateOut < 16000)
            {
                gfp.VbrMeanBitrateKbps = Math.Max(gfp.VbrMeanBitrateKbps, 8);
                gfp.VbrMeanBitrateKbps = Math.Min(gfp.VbrMeanBitrateKbps, 64);
            }
            else if (gfp.SamplerateOut < 32000)
            {
                gfp.VbrMeanBitrateKbps = Math.Max(gfp.VbrMeanBitrateKbps, 8);
                gfp.VbrMeanBitrateKbps = Math.Min(gfp.VbrMeanBitrateKbps, 160);
            }
            else
            {
                gfp.VbrMeanBitrateKbps = Math.Max(gfp.VbrMeanBitrateKbps, 32);
                gfp.VbrMeanBitrateKbps = Math.Min(gfp.VbrMeanBitrateKbps, 320);
            }
        }

        // The VBR-scale mapping the C marks WORK IN PROGRESS only runs when the output rate is
        // unset and the mode is vbr_mt or vbr_mtrh, neither of which this port encodes yet.
        if (gfp.SamplerateOut == 0 && (cfg.Vbr == VbrMode.Mt || cfg.Vbr == VbrMode.Mtrh))
        {
            throw new NotSupportedException(
                "MP3 encoder: the vbr_mt/vbr_mtrh quality mapping is not translated yet; it "
              + "belongs with VBR_new_iteration_loop. See the port checklist.");
        }

        // If no filter was asked for, pick one.
        if (gfp.Lowpassfreq == 0)
        {
            double lowpass = 16000;

            switch (cfg.Vbr)
            {
                case VbrMode.Off:
                    OptimumBandwidth(out lowpass, gfp.Brate);
                    break;
                case VbrMode.Abr:
                    OptimumBandwidth(out lowpass, gfp.VbrMeanBitrateKbps);
                    break;
                case VbrMode.Rh:
                {
                    int[] x =
                        [19500, 19000, 18600, 18000, 17500, 16000, 15600, 14900, 12500, 10000, 3950];
                    if (gfp.VbrQ is >= 0 and <= 9)
                        lowpass = LinearInt(x[gfp.VbrQ], x[gfp.VbrQ + 1], gfp.VbrQFrac);
                    else
                        lowpass = 19500;
                    break;
                }
                case VbrMode.Mtrh:
                case VbrMode.Mt:
                {
                    int[] x =
                        [24000, 19500, 18500, 18000, 17500, 17000, 16500, 15600, 15200, 7230, 3950];
                    if (gfp.VbrQ is >= 0 and <= 9)
                        lowpass = LinearInt(x[gfp.VbrQ], x[gfp.VbrQ + 1], gfp.VbrQFrac);
                    else
                        lowpass = 21500;
                    break;
                }
                default:
                {
                    int[] x =
                        [19500, 19000, 18500, 18000, 17500, 16500, 15500, 14500, 12500, 9500, 3950];
                    if (gfp.VbrQ is >= 0 and <= 9)
                        lowpass = LinearInt(x[gfp.VbrQ], x[gfp.VbrQ + 1], gfp.VbrQFrac);
                    else
                        lowpass = 19500;
                    break;
                }
            }

            if (gfp.Mode == MpegMode.Mono && (cfg.Vbr == VbrMode.Off || cfg.Vbr == VbrMode.Abr))
                lowpass *= 1.5;

            gfp.Lowpassfreq = (int)lowpass;
        }

        if (gfp.SamplerateOut == 0)
        {
            if (2 * gfp.Lowpassfreq > gfp.SamplerateIn)
                gfp.Lowpassfreq = gfp.SamplerateIn / 2;
            gfp.SamplerateOut = OptimumSamplefreq(gfp.Lowpassfreq, gfp.SamplerateIn);
        }
        if (cfg.Vbr == VbrMode.Mt || cfg.Vbr == VbrMode.Mtrh)
            gfp.Lowpassfreq = Math.Min(24000, gfp.Lowpassfreq);
        else
            gfp.Lowpassfreq = Math.Min(20500, gfp.Lowpassfreq);
        gfp.Lowpassfreq = Math.Min(gfp.SamplerateOut / 2, gfp.Lowpassfreq);

        if (cfg.Vbr == VbrMode.Off)
            gfp.CompressionRatio =
                (float)(gfp.SamplerateOut * 16 * cfg.ChannelsOut / (1.0e3 * gfp.Brate));
        if (cfg.Vbr == VbrMode.Abr)
            gfp.CompressionRatio = (float)(gfp.SamplerateOut * 16 * cfg.ChannelsOut
                                           / (1.0e3 * gfp.VbrMeanBitrateKbps));

        cfg.DisableReservoir = gfp.DisableReservoir;
        cfg.Lowpassfreq = gfp.Lowpassfreq;
        cfg.Highpassfreq = gfp.Highpassfreq;
        cfg.SamplerateIn = gfp.SamplerateIn;
        cfg.SamplerateOut = gfp.SamplerateOut;
        cfg.ModeGr = cfg.SamplerateOut <= 24000 ? 1 : 2;

        switch (cfg.Vbr)
        {
            case VbrMode.Mt:
            case VbrMode.Rh:
            case VbrMode.Mtrh:
            {
                // the numbers are odd, but they are what determines the lowpass
                float[] cmp = [5.7f, 6.5f, 7.3f, 8.2f, 10f, 11.9f, 13f, 14f, 15f, 16.5f];
                gfp.CompressionRatio = cmp[gfp.VbrQ];
                break;
            }
            case VbrMode.Abr:
                gfp.CompressionRatio = (float)(cfg.SamplerateOut * 16 * cfg.ChannelsOut
                                               / (1.0e3 * gfp.VbrMeanBitrateKbps));
                break;
            default:
                gfp.CompressionRatio =
                    (float)(cfg.SamplerateOut * 16 * cfg.ChannelsOut / (1.0e3 * gfp.Brate));
                break;
        }

        if (gfp.Mode == MpegMode.NotSet) gfp.Mode = MpegMode.JointStereo;
        cfg.Mode = gfp.Mode;

        // the user's high pass
        if (cfg.Highpassfreq > 0)
        {
            cfg.Highpass1 = (float)(2.0 * cfg.Highpassfreq);
            if (gfp.Highpasswidth >= 0)
                cfg.Highpass2 = (float)(2.0 * (cfg.Highpassfreq + gfp.Highpasswidth));
            else
                cfg.Highpass2 = (float)((1 + 0.00) * 2.0 * cfg.Highpassfreq);

            cfg.Highpass1 /= cfg.SamplerateOut;
            cfg.Highpass2 /= cfg.SamplerateOut;
        }
        else
        {
            cfg.Highpass1 = 0;
            cfg.Highpass2 = 0;
        }
        // the user's low pass
        cfg.Lowpass1 = 0;
        cfg.Lowpass2 = 0;
        if (cfg.Lowpassfreq > 0 && cfg.Lowpassfreq < cfg.SamplerateOut / 2)
        {
            cfg.Lowpass2 = (float)(2.0 * cfg.Lowpassfreq);
            if (gfp.Lowpasswidth >= 0)
            {
                cfg.Lowpass1 = (float)(2.0 * (cfg.Lowpassfreq - gfp.Lowpasswidth));
                if (cfg.Lowpass1 < 0) cfg.Lowpass1 = 0;
            }
            else
            {
                cfg.Lowpass1 = (float)((1 - 0.00) * 2.0 * cfg.Lowpassfreq);
            }
            cfg.Lowpass1 /= cfg.SamplerateOut;
            cfg.Lowpass2 /= cfg.SamplerateOut;
        }

        LameInitParamsPpflt(gfc);

        cfg.SamplerateIndex = LameUtil.SmpFrqIndex(cfg.SamplerateOut, out cfg.Version);

        if (cfg.Vbr == VbrMode.Off)
        {
            if (cfg.FreeFormat != 0)
            {
                gfc.OvEnc.BitrateIndex = 0;
            }
            else
            {
                gfp.Brate = LameUtil.FindNearestBitrate(
                    gfp.Brate, cfg.Version, cfg.SamplerateOut);
                gfc.OvEnc.BitrateIndex = LameUtil.BitrateIndex(
                    gfp.Brate, cfg.Version, cfg.SamplerateOut);
                if (gfc.OvEnc.BitrateIndex <= 0)
                {
                    // The C asserts here and then sets 8 anyway; FindNearestBitrate makes it
                    // impossible, so reaching it means one of the two tables is wrong.
                    throw new InvalidOperationException(
                        $"MP3 encoder: {gfp.Brate} kbps has no bitrate index at "
                      + $"{cfg.SamplerateOut} Hz, which FindNearestBitrate should have made "
                      + "impossible.");
                }
            }
        }
        else
        {
            gfc.OvEnc.BitrateIndex = 1;
        }

        LameBitstream.InitBitStreamW(gfc);

        LameQuantizePvtTables.InitScalefacBand(gfc);

        if (cfg.ModeGr == 2)    // MPEG-1
            cfg.SideinfoLen = cfg.ChannelsOut == 1 ? 4 + 17 : 4 + 32;
        else                    // MPEG-2
            cfg.SideinfoLen = cfg.ChannelsOut == 1 ? 4 + 9 : 4 + 17;

        if (cfg.ErrorProtection != 0) cfg.SideinfoLen += 2;

        for (int k = 0; k < 19; k++)
            gfc.SvEnc.Pefirbuf![k] = 700 * cfg.ModeGr * cfg.ChannelsOut;

        if (gfp.AthType == -1) gfp.AthType = 4;

        switch (cfg.Vbr)
        {
            case VbrMode.Mt:
            case VbrMode.Mtrh:
                if (gfp.StrictIso < 0) gfp.StrictIso = LameBitstream.MdbMaximum;
                if (gfp.UseTemporal < 0) gfp.UseTemporal = 0;   // off by default for this mode

                LamePresets.ApplyPreset(gfp, 500 - gfp.VbrQ * 10, 0);
                // The newer VBR code supports only a limited set of quality levels: 9 to 5 are
                // all 5, and 4 to 0 are all 0.
                if (gfp.Quality < 0) gfp.Quality = LameDefaultQuality;
                if (gfp.Quality < 5) gfp.Quality = 0;
                if (gfp.Quality > 7) gfp.Quality = 7;

                gfc.SvQnt.Sfb21Extra = gfp.ExperimentalY != 0
                    ? 0
                    : cfg.SamplerateOut > 44000 ? 1 : 0;
                break;

            case VbrMode.Rh:
                LamePresets.ApplyPreset(gfp, 500 - gfp.VbrQ * 10, 0);

                gfc.SvQnt.Sfb21Extra = gfp.ExperimentalY != 0
                    ? 0
                    : cfg.SamplerateOut > 44000 ? 1 : 0;

                // VBR needs at least GPSYCHO's output, which level 6 guarantees
                if (gfp.Quality > 6) gfp.Quality = 6;
                if (gfp.Quality < 0) gfp.Quality = LameDefaultQuality;
                break;

            default:    // cbr and abr
                gfc.SvQnt.Sfb21Extra = 0;   // no sfb21 extra with the CBR code

                if (gfp.Quality < 0) gfp.Quality = LameDefaultQuality;

                if (cfg.Vbr == VbrMode.Off)
                    LameSetGet.SetVbrMeanBitrateKbps(gfp, gfp.Brate);
                // then the parameters that depend on the bitrate
                LamePresets.ApplyPreset(gfp, gfp.VbrMeanBitrateKbps, 0);
                gfp.Vbr = cfg.Vbr;      // apply_abr_preset changed it; put it back
                break;
        }

        gfc.SvQnt.MaskAdjust = gfp.Maskingadjust;
        gfc.SvQnt.MaskAdjustShort = gfp.MaskingadjustShort;

        if (gfp.Tune != 0)
        {
            gfc.SvQnt.MaskAdjust += gfp.TuneValueA;
            gfc.SvQnt.MaskAdjustShort += gfp.TuneValueA;
        }

        if (cfg.Vbr != VbrMode.Off)
        {
            cfg.VbrMinBitrateIndex = 1;     // 8 kbps (MPEG-2) or 32 kbps (MPEG-1)
            cfg.VbrMaxBitrateIndex = 14;    // 160 kbps (MPEG-2) or 320 kbps (MPEG-1)
            if (cfg.SamplerateOut < 16000) cfg.VbrMaxBitrateIndex = 8;  // 64 kbps (MPEG-2.5)

            if (gfp.VbrMinBitrateKbps != 0)
            {
                gfp.VbrMinBitrateKbps = LameUtil.FindNearestBitrate(
                    gfp.VbrMinBitrateKbps, cfg.Version, cfg.SamplerateOut);
                cfg.VbrMinBitrateIndex = LameUtil.BitrateIndex(
                    gfp.VbrMinBitrateKbps, cfg.Version, cfg.SamplerateOut);
                if (cfg.VbrMinBitrateIndex < 0)
                    throw new InvalidOperationException(
                        "MP3 encoder: the VBR minimum bitrate has no index, which "
                      + "FindNearestBitrate should have made impossible.");
            }
            if (gfp.VbrMaxBitrateKbps != 0)
            {
                gfp.VbrMaxBitrateKbps = LameUtil.FindNearestBitrate(
                    gfp.VbrMaxBitrateKbps, cfg.Version, cfg.SamplerateOut);
                cfg.VbrMaxBitrateIndex = LameUtil.BitrateIndex(
                    gfp.VbrMaxBitrateKbps, cfg.Version, cfg.SamplerateOut);
                if (cfg.VbrMaxBitrateIndex < 0)
                    throw new InvalidOperationException(
                        "MP3 encoder: the VBR maximum bitrate has no index, which "
                      + "FindNearestBitrate should have made impossible.");
            }
            gfp.VbrMinBitrateKbps =
                LameTables.bitrate_table[cfg.Version][cfg.VbrMinBitrateIndex];
            gfp.VbrMaxBitrateKbps =
                LameTables.bitrate_table[cfg.Version][cfg.VbrMaxBitrateIndex];
            gfp.VbrMeanBitrateKbps = Math.Min(
                LameTables.bitrate_table[cfg.Version][cfg.VbrMaxBitrateIndex],
                gfp.VbrMeanBitrateKbps);
            gfp.VbrMeanBitrateKbps = Math.Max(
                LameTables.bitrate_table[cfg.Version][cfg.VbrMinBitrateIndex],
                gfp.VbrMeanBitrateKbps);
        }

        cfg.Preset = gfp.Preset;
        cfg.WriteLameTag = gfp.WriteLameTag;
        gfc.SvQnt.SubstepShaping = gfp.SubstepShaping;
        cfg.NoiseShaping = gfp.NoiseShaping;
        cfg.SubblockGain = gfp.SubblockGain;
        cfg.UseBestHuffman = gfp.UseBestHuffman;
        cfg.AvgBitrate = gfp.Brate;
        cfg.VbrAvgBitrateKbps = gfp.VbrMeanBitrateKbps;
        cfg.CompressionRatio = gfp.CompressionRatio;

        LameInitQval(gfp);

        gfc.Ath.UseAdjust = gfp.AthaaType < 0 ? 3 : gfp.AthaaType;
        gfc.Ath.AaSensitivityP = (float)Math.Pow(10.0, gfp.AthaaSensitivity / -10.0);

        if (gfp.ShortBlocks == ShortBlock.NotSet) gfp.ShortBlocks = ShortBlock.Allowed;

        // Note Jan/2003: many hardware decoders cannot handle short blocks in regular stereo
        // unless they are coupled, and LAME uses uncoupled ones about once a minute, so they
        // are turned off. rh 20040217: coupling makes no sense for mono or dual-mono.
        if (gfp.ShortBlocks == ShortBlock.Allowed
            && (cfg.Mode == MpegMode.JointStereo || cfg.Mode == MpegMode.Stereo))
            gfp.ShortBlocks = ShortBlock.Coupled;

        cfg.ShortBlocks = gfp.ShortBlocks;

        if (LameSetGet.GetQuantComp(gfp) < 0) LameSetGet.SetQuantComp(gfp, 1);
        if (LameSetGet.GetQuantCompShort(gfp) < 0) LameSetGet.SetQuantCompShort(gfp, 0);
        if (LameSetGet.GetMsfix(gfp) < 0) LameSetGet.SetMsfix(gfp, 0);

        // select the psychoacoustic model
        LameSetGet.SetExpNsPsytune(gfp, LameSetGet.GetExpNsPsytune(gfp) | 1);

        if (gfp.AthType < 0) gfp.AthType = 4;
        if (gfp.AthCurve < 0) gfp.AthCurve = 4;
        if (gfp.InterChRatio < 0) gfp.InterChRatio = 0;
        if (gfp.UseTemporal < 0) gfp.UseTemporal = 1;   // on by default

        cfg.InterChRatio = gfp.InterChRatio;
        cfg.Msfix = gfp.Msfix;
        cfg.AthOffsetDb = 0 - gfp.AthLowerDb;
        cfg.AthOffsetFactor = MathF.Pow(10.0f, cfg.AthOffsetDb * 0.1f);
        cfg.AthCurve = gfp.AthCurve;
        cfg.AthType = gfp.AthType;
        cfg.AthOnly = gfp.AthOnly;
        cfg.AthShort = gfp.AthShort;
        cfg.NoAth = gfp.NoAth;

        cfg.QuantComp = gfp.QuantComp;
        cfg.QuantCompShort = gfp.QuantCompShort;

        cfg.UseTemporalMaskingEffect = gfp.UseTemporal;
        cfg.UseSafeJointStereo = cfg.Mode == MpegMode.JointStereo
            ? gfp.ExpNsPsytune & 2
            : 0;

        {
            cfg.AdjustBassDb = (gfp.ExpNsPsytune >> 2) & 63;
            if (cfg.AdjustBassDb >= 32.0f) cfg.AdjustBassDb -= 64.0f;
            cfg.AdjustBassDb *= 0.25f;

            cfg.AdjustAltoDb = (gfp.ExpNsPsytune >> 8) & 63;
            if (cfg.AdjustAltoDb >= 32.0f) cfg.AdjustAltoDb -= 64.0f;
            cfg.AdjustAltoDb *= 0.25f;

            cfg.AdjustTrebleDb = (gfp.ExpNsPsytune >> 14) & 63;
            if (cfg.AdjustTrebleDb >= 32.0f) cfg.AdjustTrebleDb -= 64.0f;
            cfg.AdjustTrebleDb *= 0.25f;

            // To stay compatible with Naoki's original code, the next six bits set only how
            // much the treble changes for sfb21.
            cfg.AdjustSfb21Db = (gfp.ExpNsPsytune >> 20) & 63;
            if (cfg.AdjustSfb21Db >= 32.0f) cfg.AdjustSfb21Db -= 64.0f;
            cfg.AdjustSfb21Db *= 0.25f;
            cfg.AdjustSfb21Db += cfg.AdjustTrebleDb;
        }

        // The PCM input transform: the caller's rescaling, and a two-to-one downmix.
        {
            float m00 = 1.0f, m01 = 0.0f, m10 = 0.0f, m11 = 1.0f;

            m00 *= gfp.Scale; m01 *= gfp.Scale; m10 *= gfp.Scale; m11 *= gfp.Scale;
            m00 *= gfp.ScaleLeft; m01 *= gfp.ScaleLeft;
            m10 *= gfp.ScaleRight; m11 *= gfp.ScaleRight;

            if (cfg.ChannelsIn == 2 && cfg.ChannelsOut == 1)
            {
                m00 = 0.5f * (m00 + m10);
                m01 = 0.5f * (m01 + m11);
                m10 = 0;
                m11 = 0;
            }
            cfg.PcmTransform[0] = m00;
            cfg.PcmTransform[1] = m01;
            cfg.PcmTransform[2] = m10;
            cfg.PcmTransform[3] = m11;
        }

        // The padding cycle. There is no padding for the very first frame.
        gfc.SvEnc.SlotLag = gfc.SvEnc.FracSpF = 0;
        if (cfg.Vbr == VbrMode.Off)
            gfc.SvEnc.SlotLag = gfc.SvEnc.FracSpF =
                (int)((cfg.Version + 1) * 72000L * cfg.AvgBitrate % cfg.SamplerateOut);

        LameInitBitstream(gfp);

        LameQuantizePvt.IterationInit(gfc);
        LamePsyModel.PsymodelInit(gfp);

        cfg.BufferConstraint =
            LameBitstream.GetMaxFrameBufferSizeByConstraint(ref cfg, gfp.StrictIso);

        cfg.FindReplayGain = gfp.FindReplayGain;
        cfg.DecodeOnTheFly = gfp.DecodeOnTheFly;

        if (cfg.DecodeOnTheFly != 0) cfg.FindPeakSample = 1;

        if (cfg.FindReplayGain != 0)
        {
            throw new NotSupportedException(
                "MP3 encoder: ReplayGain is out of scope - gain_analysis.c is not translated, "
              + "and libmp3lame.c does not ask for it. See the port checklist.");
        }

        gfc.LameInitParamsSuccessful = 1;
        return 0;
    }

    /// <summary><c>lame_init_bitstream</c>.</summary>
    public static int LameInitBitstream(LameGlobalFlags gfp)
    {
        if (!LameSetGet.IsLameGlobalFlagsValid(gfp)) return -3;
        LameInternalFlags? gfc = gfp.InternalFlags;
        if (gfc is null) return -3;

        gfc.OvEnc.FrameNumber = 0;

        // id3tag_write_v2 belongs to id3tag.c, which is out of scope: libmp3lame.c lets
        // ffmpeg's muxer write the tags.

        Array.Clear(gfc.OvEnc.BitrateChannelmodeHist!);
        Array.Clear(gfc.OvEnc.BitrateBlocktypeHist!);

        gfc.OvRpg.PeakSample = 0.0f;

        if (gfc.Cfg.WriteLameTag != 0)
            throw new NotSupportedException(
                "MP3 encoder: the Xing/LAME tag is out of scope - libmp3lame.c disables it - "
              + "so InitVbrTag is not translated.");

        return 0;
    }

    /// <summary><c>calcNeeded</c> - how much input one frame needs buffered.</summary>
    private static int CalcNeeded(ref SessionConfig cfg)
    {
        int pcmSamplesPerFrame = 576 * cfg.ModeGr;

        int mfNeeded = LameConstants.BlkSize + pcmSamplesPerFrame - LameConstants.FftOffset;
        mfNeeded = Math.Max(mfNeeded, 512 + pcmSamplesPerFrame - 32);

        return mfNeeded;
    }

    /// <summary><c>update_inbuffer_size</c>.</summary>
    private static int UpdateInbufferSize(LameInternalFlags gfc, int nsamples)
    {
        ref EncStateVar esv = ref gfc.SvEnc;
        if (esv.InBuffer0 is null || esv.InBufferNsamples < nsamples)
        {
            esv.InBuffer0 = new float[nsamples];
            esv.InBuffer1 = new float[nsamples];
            esv.InBufferNsamples = nsamples;
        }
        return 0;
    }

    /// <summary>
    /// <c>fill_buffer</c> (<c>util.c</c>) - copy new samples into <c>mfbuf</c>.
    /// </summary>
    /// <remarks>
    /// Both branches are the C's. ffmpeg's libmp3lame.c sets the output rate equal to the
    /// input rate, so the ffmpeg path always takes the copy; <c>Ripping/</c>'s
    /// <c>Mp3Encoder</c> can take the resampling one, because a CD rip is 44.1 kHz and the
    /// caller chooses the output rate.
    ///
    /// <para>
    /// The C writes <c>n_in</c> once per channel from inside <c>fill_buffer_resample</c> and
    /// keeps only the last channel's <c>n_out</c>. Both channels agree, because
    /// <c>itime[0]</c> and <c>itime[1]</c> start equal and advance by the same amount, but
    /// the shape is the C's rather than a tidied one so that a future divergence shows up as
    /// a difference from the C instead of being absorbed here.
    /// </para>
    /// </remarks>
    private static void FillBuffer(
        LameInternalFlags gfc, int inBufferOffset, int nsamples, out int nIn, out int nOut)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int mfSize = gfc.SvEnc.MfSize;
        int framesize = 576 * cfg.ModeGr;
        int nch = cfg.ChannelsOut;

        if (LameUtil.IsResamplingNecessary(ref cfg))
        {
            nIn = 0;
            nOut = 0;
            for (int ch = 0; ch < nch; ch++)
            {
                float[] src = ch == 0 ? gfc.SvEnc.InBuffer0! : gfc.SvEnc.InBuffer1!;
                nOut = LameUtil.FillBufferResample(
                    gfc,
                    gfc.SvEnc.Mfbuf!, ch * LameEncStateConstants.MfSize + mfSize, framesize,
                    src, inBufferOffset, nsamples,
                    out nIn, ch);
            }
            return;
        }

        nOut = Math.Min(framesize, nsamples);
        for (int ch = 0; ch < nch; ch++)
        {
            float[] src = ch == 0 ? gfc.SvEnc.InBuffer0! : gfc.SvEnc.InBuffer1!;
            src.AsSpan(inBufferOffset, nOut).CopyTo(
                gfc.SvEnc.Mfbuf.AsSpan(ch * LameEncStateConstants.MfSize + mfSize, nOut));
        }
        nIn = nOut;
    }

    /// <summary>
    /// <c>lame_encode_buffer_sample_t</c> - the main encoding loop, in LAME's own PCM type.
    /// </summary>
    private static int LameEncodeBufferSampleT(
        LameInternalFlags gfc, int nsamples, Span<byte> mp3buf, int mp3bufSize)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        int pcmSamplesPerFrame = 576 * cfg.ModeGr;
        int mp3size = 0, ret, mfNeeded;
        int mp3out;
        int mp3buffered = 0;
        int inBufferOffset = 0;

        if (gfc.ClassId != LameInternalFlags.LameId) return -3;
        if (nsamples == 0) return 0;

        // copy out any tags already written into the bit buffer
        {
            int bufSize = mp3bufSize == 0 ? int.MaxValue : mp3bufSize;
            mp3out = LameBitstream.CopyBuffer(gfc, mp3buf, bufSize, 0);
        }
        if (mp3out < 0) return mp3out;      // not enough buffer space
        mp3buffered += mp3out;
        mp3size += mp3out;

        mfNeeded = CalcNeeded(ref cfg);

        while (nsamples > 0)
        {
            FillBuffer(gfc, inBufferOffset, nsamples, out int nIn, out int nOut);

            nsamples -= nIn;
            inBufferOffset += nIn;

            esv.MfSize += nOut;

            // lame_encode_flush may have zeroed this, so put it back when it has.
            if (esv.MfSamplesToEncode < 1)
                esv.MfSamplesToEncode = LameConstants.EncDelay + LameConstants.PostDelay;
            esv.MfSamplesToEncode += nOut;

            if (esv.MfSize >= mfNeeded)
            {
                int bufSize = mp3bufSize - mp3size;
                if (mp3bufSize == 0) bufSize = int.MaxValue;

                ret = LameEncoder.LameEncodeMp3Frame(
                    gfc,
                    esv.Mfbuf.AsSpan(0, LameEncStateConstants.MfSize),
                    esv.Mfbuf.AsSpan(LameEncStateConstants.MfSize, LameEncStateConstants.MfSize),
                    mp3buf[mp3buffered..], bufSize);

                if (ret < 0) return ret;
                mp3buffered += ret;
                mp3size += ret;

                // shift out the old samples
                esv.MfSize -= pcmSamplesPerFrame;
                esv.MfSamplesToEncode -= pcmSamplesPerFrame;
                for (int ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    int b = ch * LameEncStateConstants.MfSize;
                    esv.Mfbuf.AsSpan(b + pcmSamplesPerFrame, esv.MfSize)
                        .CopyTo(esv.Mfbuf.AsSpan(b, esv.MfSize));
                }
            }
        }

        return mp3size;
    }

    /// <summary><c>enum PCMSampleType</c>.</summary>
    public enum PcmSampleType { Short, Int, Long, Float, Double }

    /// <summary>
    /// <c>lame_copy_inbuffer</c> - widen the caller's PCM and apply the input transform.
    /// </summary>
    private static void LameCopyInbuffer<T>(
        LameInternalFlags gfc, ReadOnlySpan<T> l, ReadOnlySpan<T> r, int nsamples,
        int jump, float s) where T : struct
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        float[] ib0 = gfc.SvEnc.InBuffer0!;
        float[] ib1 = gfc.SvEnc.InBuffer1!;

        float m00 = s * cfg.PcmTransform[0];
        float m01 = s * cfg.PcmTransform[1];
        float m10 = s * cfg.PcmTransform[2];
        float m11 = s * cfg.PcmTransform[3];

        for (int i = 0; i < nsamples; i++)
        {
            float xl = Convert.ToSingle(l[i * jump]);
            float xr = Convert.ToSingle(r[i * jump]);
            ib0[i] = xl * m00 + xr * m01;
            ib1[i] = xl * m10 + xr * m11;
        }
    }

    /// <summary><c>lame_encode_buffer_template</c>.</summary>
    private static int LameEncodeBufferTemplate<T>(
        LameGlobalFlags gfp, ReadOnlySpan<T> bufferL, ReadOnlySpan<T> bufferR, int nsamples,
        Span<byte> mp3buf, int mp3bufSize, int aa, float norm) where T : struct
    {
        if (!LameSetGet.IsLameGlobalFlagsValid(gfp)) return -3;
        LameInternalFlags? gfc = gfp.InternalFlags;
        if (!LameSetGet.IsLameInternalFlagsValid(gfc)) return -3;

        if (nsamples == 0) return 0;
        if (UpdateInbufferSize(gfc!, nsamples) != 0) return -2;

        if (gfc!.Cfg.ChannelsIn > 1)
        {
            if (bufferL.IsEmpty || bufferR.IsEmpty) return 0;
            LameCopyInbuffer(gfc, bufferL, bufferR, nsamples, aa, norm);
        }
        else
        {
            if (bufferL.IsEmpty) return 0;
            LameCopyInbuffer(gfc, bufferL, bufferL, nsamples, aa, norm);
        }

        return LameEncodeBufferSampleT(gfc, nsamples, mp3buf, mp3bufSize);
    }

    /// <summary><c>lame_encode_buffer</c> - 16-bit PCM.</summary>
    public static int LameEncodeBuffer(
        LameGlobalFlags gfp, ReadOnlySpan<short> pcmL, ReadOnlySpan<short> pcmR, int nsamples,
        Span<byte> mp3buf, int mp3bufSize)
        => LameEncodeBufferTemplate(gfp, pcmL, pcmR, nsamples, mp3buf, mp3bufSize, 1, 1.0f);

    /// <summary>
    /// <c>lame_encode_buffer_float</c> - float PCM, normalised to plus or minus 32768.
    /// </summary>
    public static int LameEncodeBufferFloat(
        LameGlobalFlags gfp, ReadOnlySpan<float> pcmL, ReadOnlySpan<float> pcmR, int nsamples,
        Span<byte> mp3buf, int mp3bufSize)
        => LameEncodeBufferTemplate(gfp, pcmL, pcmR, nsamples, mp3buf, mp3bufSize, 1, 1.0f);

    /// <summary><c>lame_encode_buffer_int</c> - 32-bit PCM, scaled down to 16-bit range.</summary>
    public static int LameEncodeBufferInt(
        LameGlobalFlags gfp, ReadOnlySpan<int> pcmL, ReadOnlySpan<int> pcmR, int nsamples,
        Span<byte> mp3buf, int mp3bufSize)
        => LameEncodeBufferTemplate(
            gfp, pcmL, pcmR, nsamples, mp3buf, mp3bufSize, 1, 1.0f / (1 << (8 * (4 - 2))));

    /// <summary><c>lame_encode_flush</c> - flush the PCM buffers, then the bit buffer.</summary>
    public static int LameEncodeFlush(
        LameGlobalFlags gfp, Span<byte> mp3buffer, int mp3bufferSize)
    {
        if (!LameSetGet.IsLameGlobalFlagsValid(gfp)) return -3;
        LameInternalFlags? gfcOrNull = gfp.InternalFlags;
        if (!LameSetGet.IsLameInternalFlagsValid(gfcOrNull)) return -3;
        LameInternalFlags gfc = gfcOrNull!;

        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;

        // Was flush already called?
        if (esv.MfSamplesToEncode < 1) return 0;

        int pcmSamplesPerFrame = 576 * cfg.ModeGr;
        int mfNeeded = CalcNeeded(ref cfg);
        int samplesToEncode = esv.MfSamplesToEncode - LameConstants.PostDelay;
        int imp3 = 0, mp3count = 0, written = 0;
        double resampleRatio = 1;

        var buffer = new short[2 * 1152];

        if (LameUtil.IsResamplingNecessary(ref cfg))
        {
            resampleRatio = (double)cfg.SamplerateIn / cfg.SamplerateOut;
            // the delay the resampler adds; tied to the resampling code
            samplesToEncode += (int)(16.0 / resampleRatio);
        }

        // POSTDELAY = 288 of padding, so the last granule with real data decodes completely:
        // a 576-sample granule needs the next one for its last 288 output samples.
        int endPadding = pcmSamplesPerFrame - samplesToEncode % pcmSamplesPerFrame;
        if (endPadding < 576) endPadding += pcmSamplesPerFrame;
        gfc.OvEnc.EncoderPadding = endPadding;

        int framesLeft = (samplesToEncode + endPadding) / pcmSamplesPerFrame;
        while (framesLeft > 0 && imp3 >= 0)
        {
            int frameNum = gfc.OvEnc.FrameNumber;
            int bunch = mfNeeded - esv.MfSize;

            bunch = (int)(bunch * resampleRatio);
            if (bunch > 1152) bunch = 1152;
            if (bunch < 1) bunch = 1;

            int remaining = mp3bufferSize - mp3count;
            if (mp3bufferSize == 0) remaining = 0;

            // push silence through until every internal buffer is flushed
            imp3 = LameEncodeBuffer(
                gfp, buffer.AsSpan(0, 1152), buffer.AsSpan(1152, 1152), bunch,
                mp3buffer[written..], remaining);

            if (imp3 > 0) { written += imp3; mp3count += imp3; }

            int newFrames = gfc.OvEnc.FrameNumber - frameNum;
            if (newFrames > 0) framesLeft -= newFrames;
        }

        // Zero this so a second call in a row can be detected and short-circuited.
        esv.MfSamplesToEncode = 0;

        if (imp3 < 0) return imp3;

        int remaining2 = mp3bufferSize - mp3count;
        if (mp3bufferSize == 0) remaining2 = int.MaxValue;

        LameBitstream.FlushBitstream(gfc);
        imp3 = LameBitstream.CopyBuffer(gfc, mp3buffer[written..], remaining2, 1);
        if (imp3 < 0) return imp3;
        written += imp3;
        mp3count += imp3;

        // id3tag_write_v1 belongs to id3tag.c, which is out of scope.

        return mp3count;
    }

    /// <summary><c>lame_init_internal_flags</c>.</summary>
    private static int LameInitInternalFlags(LameInternalFlags gfc)
    {
        gfc.Cfg.VbrMinBitrateIndex = 1;     // the C's comment asks "not 0 ?????"
        gfc.Cfg.VbrMaxBitrateIndex = 13;    // and "not 14 ?????"
        gfc.Cfg.DecodeOnTheFly = 0;
        gfc.Cfg.FindReplayGain = 0;
        gfc.Cfg.FindPeakSample = 0;

        gfc.SvQnt.OldValue[0] = 180;
        gfc.SvQnt.OldValue[1] = 180;
        gfc.SvQnt.CurrentStep[0] = 4;
        gfc.SvQnt.CurrentStep[1] = 4;
        gfc.SvQnt.MaskingLower = 1;

        // ENCDELAY is the encoder's own delay; POSTDELAY = 288 covers the 50% MDCT overlap,
        // since synthesising the 576 samples under a granule needs the next granule's first
        // 288, which do not exist for the last one.
        gfc.SvEnc.MfSamplesToEncode = LameConstants.EncDelay + LameConstants.PostDelay;
        gfc.SvEnc.MfSize = LameConstants.EncDelay - LameConstants.MdctDelay;
        gfc.OvEnc.EncoderPadding = 0;
        gfc.OvEnc.EncoderDelay = LameConstants.EncDelay;

        gfc.OvRpg.RadioGain = 0;
        gfc.OvRpg.NoclipGainChange = 0;
        gfc.OvRpg.NoclipScale = -1.0f;

        return 0;
    }

    /// <summary><c>lame_init_old</c> - the non-zero defaults.</summary>
    private static int LameInitOld(LameGlobalFlags gfp)
    {
        gfp.ClassId = LameGlobalFlags.LameId;

        // -1 means "LAME computes the best value unless the caller set it".
        gfp.StrictIso = LameBitstream.MdbMaximum;

        gfp.Mode = MpegMode.NotSet;
        gfp.Original = 1;
        gfp.SamplerateIn = 44100;
        gfp.NumChannels = 2;
        gfp.NumSamples = MaxU32Num;

        gfp.WriteLameTag = 1;
        gfp.Quality = -1;
        gfp.ShortBlocks = ShortBlock.NotSet;
        gfp.SubblockGain = -1;

        gfp.Lowpassfreq = 0;
        gfp.Highpassfreq = 0;
        gfp.Lowpasswidth = -1;
        gfp.Highpasswidth = -1;

        gfp.Vbr = VbrMode.Off;
        gfp.VbrQ = 4;
        gfp.VbrMeanBitrateKbps = 128;
        gfp.VbrMinBitrateKbps = 0;
        gfp.VbrMaxBitrateKbps = 0;
        gfp.VbrHardMin = 0;

        gfp.QuantComp = -1;
        gfp.QuantCompShort = -1;

        gfp.Msfix = -1;

        gfp.Attackthre = -1;
        gfp.AttackthreS = -1;

        gfp.Scale = 1;
        gfp.ScaleLeft = 1;
        gfp.ScaleRight = 1;

        gfp.AthCurve = -1;
        gfp.AthType = -1;
        gfp.AthaaSensitivity = 0.0f;
        gfp.AthaaType = -1;
        gfp.UseTemporal = -1;
        gfp.InterChRatio = -1;

        gfp.FindReplayGain = 0;
        gfp.DecodeOnTheFly = 0;

        gfp.Preset = 0;
        gfp.WriteId3TagAutomatic = 1;

        gfp.InternalFlags = new LameInternalFlags();
        gfp.InternalFlags.Allocate();

        if (LameInitInternalFlags(gfp.InternalFlags) < 0)
        {
            gfp.InternalFlags = null;
            return -1;
        }
        return 0;
    }

    /// <summary><c>lame_init</c> - a fresh encoder with LAME's defaults.</summary>
    public static LameGlobalFlags? LameInit()
    {
        var gfp = new LameGlobalFlags();

        if (LameInitOld(gfp) != 0) return null;

        gfp.LameAllocatedGfp = 1;
        return gfp;
    }

    /// <summary><c>lame_close</c> - in the C this frees the buffers; here it invalidates.</summary>
    public static int LameClose(LameGlobalFlags? gfp)
    {
        int ret = 0;
        if (gfp is not null && gfp.ClassId == LameGlobalFlags.LameId)
        {
            LameInternalFlags? gfc = gfp.InternalFlags;
            gfp.ClassId = 0;
            if (gfc is null || gfc.ClassId != LameInternalFlags.LameId)
                ret = -3;
            else
                gfc.ClassId = 0;
            gfp.InternalFlags = null;
        }
        return ret;
    }
}

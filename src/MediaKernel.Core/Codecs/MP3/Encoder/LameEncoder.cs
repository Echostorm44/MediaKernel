// Translated from lame-3.100/libmp3lame/encoder.c - one frame, start to finish.
//
// The five stages, in the C's order: the psychoacoustic model, the polyphase filterbank and
// MDCT, the mid/side decision, the quantisation loop, and the bitstream.
//
// THE DELAYS ARE THE DESIGN. The encoder acts on inbuf[ch][0] but its MDCT coefficients come
// from inbuf[ch][-MDCTDELAY], and the psychoacoustic model runs a granule ahead, so it is fed
// at inbuf[ch][576 + gr * 576 - FFTOFFSET]. The C's comment block draws the overlap window by
// window and is worth reading before changing any index here.
//
// The if (cfg->analysis) blocks copy spectra and PCM into plotting_data for LAME's frame
// analyser and are omitted, as everywhere else. AddVbrFrame belongs to VbrTag.c, which
// libmp3lame.c disables; rather than silently skip it this throws if write_lame_tag is ever
// set, under the project's fail-loudly rule.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary>The per-frame scratch of <c>lame_encode_mp3_frame</c>.</summary>
/// <remarks>
/// <c>masking_LR</c> and <c>masking_MS</c> are four <c>III_psy_ratio</c> each, about 2 KB a
/// piece, and the C declares them as stack locals of a function called once per frame. Same
/// reasoning as LamePsyScratch and LameQuantizeScratch.
/// </remarks>
internal sealed class LameEncoderScratch
{
    /// <summary><c>III_psy_ratio masking_LR[2][2]</c> - index as <c>[gr * 2 + ch]</c>.</summary>
    public readonly IiiPsyRatio[] MaskingLr = new IiiPsyRatio[4];

    /// <summary><c>III_psy_ratio masking_MS[2][2]</c>.</summary>
    public readonly IiiPsyRatio[] MaskingMs = new IiiPsyRatio[4];

    /// <summary><c>FLOAT tot_ener[2][4]</c>.</summary>
    public readonly float[] TotEner = new float[2 * 4];

    /// <summary><c>FLOAT pe[2][2]</c>.</summary>
    public readonly float[] Pe = new float[4];

    /// <summary><c>FLOAT pe_MS[2][2]</c>.</summary>
    public readonly float[] PeMs = new float[4];

    /// <summary><c>FLOAT ms_ener_ratio[2]</c>.</summary>
    public readonly float[] MsEnerRatio = new float[2];

    /// <summary><c>int blocktype[2]</c>.</summary>
    public readonly int[] Blocktype = new int[2];

    /// <summary>
    /// <c>sample_t primebuff0/1[286 + 1152 + 576]</c> of lame_encode_frame_init, which runs
    /// once per encoder. Flattened; index as <c>[ch * PrimebuffLength + i]</c>.
    /// </summary>
    public float[]? Primebuff;

    /// <summary>The length of one channel of <see cref="Primebuff"/>.</summary>
    public const int PrimebuffLength = 286 + 1152 + 576;
}

/// <summary><c>encoder.c</c>.</summary>
internal static class LameEncoder
{
    /// <summary><c>fircoef[9]</c> of the perceptual entropy smoothing filter.</summary>
    private static readonly float[] fircoef =
    [
        (float)(-0.0207887 * 5), (float)(-0.0378413 * 5), (float)(-0.0432472 * 5),
        (float)(-0.031183 * 5), (float)(7.79609e-18 * 5), (float)(0.0467745 * 5),
        (float)(0.10091 * 5), (float)(0.151365 * 5), (float)(0.187098 * 5),
    ];

    /// <summary>
    /// <c>adjust_ATH</c> - Gabriel Bouvigne 2001, auto-adjust the ATH for low volume.
    /// </summary>
    /// <remarks>
    /// An increase in loudness sets the adjustment to its limit immediately, after one frame's
    /// delay; a decrease walks it back gradually. The curves are jd's continuous approximation
    /// to GB's original step values.
    /// </remarks>
    private static void AdjustAth(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        float gr2Max, maxPow;

        if (gfc.Ath.UseAdjust == 0)
        {
            gfc.Ath.AdjustFactor = 1.0f;    // no adjustment
            return;
        }

        // Loudness from the equal loudness curve, using the louder granule.
        maxPow = gfc.OvPsy.LoudnessSq[0 * 2 + 0];
        gr2Max = gfc.OvPsy.LoudnessSq[1 * 2 + 0];
        if (cfg.ChannelsOut == 2)
        {
            maxPow += gfc.OvPsy.LoudnessSq[0 * 2 + 1];
            gr2Max += gfc.OvPsy.LoudnessSq[1 * 2 + 1];
        }
        else
        {
            maxPow += maxPow;
            gr2Max += gr2Max;
        }
        if (cfg.ModeGr == 2) maxPow = Math.Max(maxPow, gr2Max);
        maxPow *= 0.5f;     // max_pow approaches 1.0 for full band noise

        maxPow *= gfc.Ath.AaSensitivityP;

        if (maxPow > 0.03125f)      // (1 - 0.000625) / 31.98, from the curve below
        {
            if (gfc.Ath.AdjustFactor >= 1.0f)
            {
                gfc.Ath.AdjustFactor = 1.0f;
            }
            else
            {
                // The preceding frame has a lower adjustment, so ascend only to its limit,
                // in case this is leading low volume.
                if (gfc.Ath.AdjustFactor < gfc.Ath.AdjustLimit)
                    gfc.Ath.AdjustFactor = gfc.Ath.AdjustLimit;
            }
            gfc.Ath.AdjustLimit = 1.0f;
        }
        else
        {
            // the adjustment curve, about 32 dB of range
            float adjLimNew = (float)(31.98 * maxPow + 0.000625);
            if (gfc.Ath.AdjustFactor >= adjLimNew)
            {
                // descend gradually
                gfc.Ath.AdjustFactor *= (float)(adjLimNew * 0.075 + 0.925);
                if (gfc.Ath.AdjustFactor < adjLimNew)
                    gfc.Ath.AdjustFactor = adjLimNew;   // stop descending
            }
            else
            {
                if (gfc.Ath.AdjustLimit >= adjLimNew)
                {
                    gfc.Ath.AdjustFactor = adjLimNew;
                }
                else
                {
                    // the preceding frame has a lower adjustment; ascend only to its limit
                    if (gfc.Ath.AdjustFactor < gfc.Ath.AdjustLimit)
                        gfc.Ath.AdjustFactor = gfc.Ath.AdjustLimit;
                }
            }
            gfc.Ath.AdjustLimit = adjLimNew;
        }
    }

    /// <summary><c>updateStats</c> - the bitrate and block type histograms.</summary>
    private static void UpdateStats(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        int gr, ch;

        // count bitrate indices
        eov.BitrateChannelmodeHist![eov.BitrateIndex * 5 + 4]++;
        eov.BitrateChannelmodeHist[15 * 5 + 4]++;

        // and every mode extension, when encoding two channels
        if (cfg.ChannelsOut == 2)
        {
            eov.BitrateChannelmodeHist[eov.BitrateIndex * 5 + eov.ModeExt]++;
            eov.BitrateChannelmodeHist[15 * 5 + eov.ModeExt]++;
        }
        for (gr = 0; gr < cfg.ModeGr; ++gr)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ++ch)
            {
                int bt = gfc.L3Side.Tt[gr * 2 + ch].BlockType;
                if (gfc.L3Side.Tt[gr * 2 + ch].MixedBlockFlag != 0) bt = 4;
                eov.BitrateBlocktypeHist![eov.BitrateIndex * 6 + bt]++;
                eov.BitrateBlocktypeHist[eov.BitrateIndex * 6 + 5]++;
                eov.BitrateBlocktypeHist[15 * 6 + bt]++;
                eov.BitrateBlocktypeHist[15 * 6 + 5]++;
            }
        }
    }

    /// <summary>
    /// <c>lame_encode_frame_init</c> - prime the filterbank with a short block.
    /// </summary>
    /// <remarks>
    /// The first frame has no history, so the C builds a buffer that is silence followed by
    /// the first frame's samples, marks every granule short, and runs the filterbank over it
    /// once to fill <c>sb_sample</c>. Nothing is written to the bitstream.
    /// </remarks>
    private static void LameEncodeFrameInit(
        LameInternalFlags gfc, ReadOnlySpan<float> inbuf0, ReadOnlySpan<float> inbuf1)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int ch, gr;

        if (gfc.LameEncodeFrameInit == 0)
        {
            const int n = LameEncoderScratch.PrimebuffLength;
            gfc.EncoderScratch.Primebuff ??= new float[2 * n];
            float[] primebuff = gfc.EncoderScratch.Primebuff;
            Array.Clear(primebuff);

            int framesize = 576 * cfg.ModeGr;
            int i, j;
            gfc.LameEncodeFrameInit = 1;

            for (i = 0, j = 0; i < 286 + 576 * (1 + cfg.ModeGr); ++i)
            {
                if (i < framesize)
                {
                    primebuff[i] = 0;
                    if (cfg.ChannelsOut == 2) primebuff[n + i] = 0;
                }
                else
                {
                    primebuff[i] = inbuf0[j];
                    if (cfg.ChannelsOut == 2) primebuff[n + i] = inbuf1[j];
                    ++j;
                }
            }

            for (gr = 0; gr < cfg.ModeGr; gr++)
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                    gfc.L3Side.Tt[gr * 2 + ch].BlockType = LameBlockType.Short;

            LameMdct.MdctSub48(gfc, primebuff.AsSpan(0, n), primebuff.AsSpan(n, n));
        }
    }

    /// <summary>
    /// <c>lame_encode_mp3_frame</c> - encode one frame of PCM into <paramref name="mp3buf"/>.
    /// </summary>
    /// <returns>The number of bytes written, or a negative LAME error code.</returns>
    public static int LameEncodeMp3Frame(
        LameInternalFlags gfc, ReadOnlySpan<float> inbufL, ReadOnlySpan<float> inbufR,
        Span<byte> mp3buf, int mp3bufSize)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        LameEncoderScratch scratch = gfc.EncoderScratch;
        int mp3count;
        IiiPsyRatio[] maskingLr = scratch.MaskingLr;
        IiiPsyRatio[] maskingMs = scratch.MaskingMs;
        IiiPsyRatio[] masking;

        float[] totEner = scratch.TotEner;
        float[] msEnerRatio = scratch.MsEnerRatio;
        float[] pe = scratch.Pe;
        float[] peMs = scratch.PeMs;
        float[] peUse;

        int ch, gr;

        msEnerRatio[0] = msEnerRatio[1] = .5f;
        Array.Clear(pe);
        Array.Clear(peMs);

        if (gfc.LameEncodeFrameInit == 0)
            LameEncodeFrameInit(gfc, inbufL, inbufR);

        // Padding, as described in "MPEG-Layer3 / Bitstream Syntax and Decoding" by Martin
        // Sieler and Ralph Sperschneider. There is no padding for the very first frame.
        gfc.OvEnc.Padding = 0;
        if ((gfc.SvEnc.SlotLag -= gfc.SvEnc.FracSpF) < 0)
        {
            gfc.SvEnc.SlotLag += cfg.SamplerateOut;
            gfc.OvEnc.Padding = 1;
        }

        // Stage 1: the psychoacoustic model, which runs one granule (576 samples) ahead.
        {
            int ret;
            int[] blocktype = scratch.Blocktype;

            for (gr = 0; gr < cfg.ModeGr; gr++)
            {
                int offset = 576 + gr * 576 - LameConstants.FftOffset;
                var bufp = new SampleBuffers(
                    inbufL[offset..],
                    cfg.ChannelsOut == 2 ? inbufR[offset..] : inbufL[offset..]);

                ret = LamePsyModel.L3PsychoAnalVbr(
                    gfc, bufp, gr, maskingLr, maskingMs,
                    pe.AsSpan(gr * 2, 2), peMs.AsSpan(gr * 2, 2),
                    totEner.AsSpan(gr * 4, 4), blocktype);
                if (ret != 0) return -4;

                if (cfg.Mode == MpegMode.JointStereo)
                {
                    msEnerRatio[gr] = totEner[gr * 4 + 2] + totEner[gr * 4 + 3];
                    if (msEnerRatio[gr] > 0)
                        msEnerRatio[gr] = totEner[gr * 4 + 3] / msEnerRatio[gr];
                }

                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];
                    codInfo.BlockType = blocktype[ch];
                    codInfo.MixedBlockFlag = 0;
                }
            }
        }

        AdjustAth(gfc);

        // Stage 2: polyphase filtering and MDCT.
        LameMdct.MdctSub48(gfc, inbufL, cfg.ChannelsOut == 2 ? inbufR : inbufL);

        // Stage 3: the mid/side decision.
        gfc.OvEnc.ModeExt = LameModeExtValue.LrLr;

        if (cfg.ForceMs != 0)
        {
            gfc.OvEnc.ModeExt = LameModeExtValue.MsLr;
        }
        else if (cfg.Mode == MpegMode.JointStereo)
        {
            // ms_ratio is scaled to look like side / total: 0 is wholly mono, 0.5 is L and R
            // uncorrelated.
            float sumPeMs = 0;
            float sumPeLr = 0;
            for (gr = 0; gr < cfg.ModeGr; gr++)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    sumPeMs += peMs[gr * 2 + ch];
                    sumPeLr += pe[gr * 2 + ch];
                }
            }

            // mid/side would not cost much more than left/right
            if (sumPeMs <= 1.00f * sumPeLr)
            {
                // The C indexes gi0[0] and gi0[1], which are tt[0][0] and tt[0][1], and
                // likewise for the last granule.
                int gi0 = 0 * 2;
                int gi1 = (cfg.ModeGr - 1) * 2;

                if (gfc.L3Side.Tt[gi0 + 0].BlockType == gfc.L3Side.Tt[gi0 + 1].BlockType
                    && gfc.L3Side.Tt[gi1 + 0].BlockType == gfc.L3Side.Tt[gi1 + 1].BlockType)
                {
                    gfc.OvEnc.ModeExt = LameModeExtValue.MsLr;
                }
            }
        }

        if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
        {
            masking = maskingMs;
            peUse = peMs;
        }
        else
        {
            masking = maskingLr;
            peUse = pe;
        }

        // Stage 4: the quantisation loop.
        if (cfg.Vbr == VbrMode.Off || cfg.Vbr == VbrMode.Abr)
        {
            int i;
            float f;

            for (i = 0; i < 18; i++)
                gfc.SvEnc.Pefirbuf![i] = gfc.SvEnc.Pefirbuf[i + 1];

            f = 0.0f;
            for (gr = 0; gr < cfg.ModeGr; gr++)
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                    f += peUse[gr * 2 + ch];
            gfc.SvEnc.Pefirbuf![18] = f;

            f = gfc.SvEnc.Pefirbuf[9];
            for (i = 0; i < 9; i++)
                f += (gfc.SvEnc.Pefirbuf[i] + gfc.SvEnc.Pefirbuf[18 - i]) * fircoef[i];

            f = 670 * 5 * cfg.ModeGr * cfg.ChannelsOut / f;
            for (gr = 0; gr < cfg.ModeGr; gr++)
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                    peUse[gr * 2 + ch] *= f;
        }

        switch (cfg.Vbr)
        {
            default:
            case VbrMode.Off:
                LameQuantize.CbrIterationLoop(gfc, peUse, msEnerRatio, masking);
                break;
            case VbrMode.Abr:
                LameQuantize.AbrIterationLoop(gfc, peUse, msEnerRatio, masking);
                break;
            case VbrMode.Rh:
                LameQuantize.VbrOldIterationLoop(gfc, peUse, msEnerRatio, masking);
                break;
            case VbrMode.Mt:
            case VbrMode.Mtrh:
                LameQuantize.VbrNewIterationLoop(gfc, peUse, msEnerRatio, masking);
                break;
        }

        // Stage 5: the bitstream.
        LameBitstream.FormatBitstream(gfc);
        mp3count = LameBitstream.CopyBuffer(gfc, mp3buf, mp3bufSize, 1);

        if (cfg.WriteLameTag != 0)
        {
            throw new NotSupportedException(
                "MP3 encoder: the Xing/LAME tag is out of scope - libmp3lame.c disables it - "
              + "so AddVbrFrame is not translated. See the port checklist, section mp3enc.");
        }

        ++gfc.OvEnc.FrameNumber;

        UpdateStats(gfc);

        return mp3count;
    }
}

// Licensed under the MIT License.
// Faithful port of FFmpeg's libavcodec/opus/silk.c — Opus SILK decoder.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Per-channel SILK frame state, persists across frames.
/// Direct port of FFmpeg's SilkFrame struct.
/// </summary>
internal class SilkFrame
{
    public bool Coded;
    public int LogGain;
    public readonly short[] Nlsf = new short[16];
    public readonly float[] Lpc = new float[16];

    // output[0..SILK_HISTORY-1] = history, output[SILK_HISTORY..] = current frame
    public readonly float[] Output = new float[2 * SilkTables.SilkHistory];
    public readonly float[] LpcHistory = new float[2 * SilkTables.SilkHistory];
    public int PrimaryLag;
    public bool PrevVoiced;

    public void Flush()
    {
        if (!Coded) return;
        Array.Clear(Output);
        Array.Clear(LpcHistory);
        Array.Clear(Lpc);
        Array.Clear(Nlsf);
        LogGain = 0;
        PrimaryLag = 0;
        PrevVoiced = false;
        Coded = false;
    }
}

/// <summary>
/// SILK decoder — direct port of FFmpeg's silk.c.
/// Decodes SILK superframes from the Opus range coder, producing float output at SILK sample rates.
/// </summary>
internal class SilkDecoder
{
    private const int LtpOrder = 5;
    private const int SilkMaxLag = 288 + LtpOrder / 2; // 290

    public int OutputChannels;

    private bool midOnly;
    private int subframes;
    private int sfLength;    // subframe length in samples
    private int fLength;     // frame length in samples
    private int nlsfInterpFactor;

    private int bandwidth;   // 0=NB, 1=MB, 2=WB
    private int wb;          // 1 if WB, else 0

    private readonly SilkFrame[] frame = [new SilkFrame(), new SilkFrame()];
    private readonly float[] prevStereoWeights = new float[2];
    private readonly float[] stereoWeights = new float[2];
    private int prevCodedChannels;

    // ---- Helper math (FFmpeg macros) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Mul64(int a, int b) => (long)a * b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MulH(int a, int b) => (int)(Mul64(a, b) >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RoundMull(int a, int b, int s) =>
        ((Mul64(a, b) >> (s - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Mull(int a, int b, int s) => Mul64(a, b) >> s;

    /// <summary>opus_ilog(i) = av_log2(i) + !!(i) = number of bits needed to represent i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OpusIlog(int i) => i > 0 ? BitOperations.Log2((uint)i) + 1 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipInt16(int v) => Math.Clamp(v, short.MinValue, short.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipUintP2(int v, int p) => Math.Clamp(v, 0, (1 << p) - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ClipF(float v, float min, float max) => MathF.Min(MathF.Max(v, min), max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SatSub32(int a, int b)
    {
        long r = (long)a - b;
        return r > int.MaxValue ? int.MaxValue : r < int.MinValue ? int.MinValue : (int)r;
    }

    // ---- silk_stabilize_lsf ----

    private static void StabilizeLsf(Span<short> nlsf, int order, ReadOnlySpan<ushort> minDelta)
    {
        for (int pass = 0; pass < 20; pass++)
        {
            int k = 0, minDiff = 0;
            for (int i = 0; i <= order; i++)
            {
                int low = i != 0 ? nlsf[i - 1] : 0;
                int high = i != order ? nlsf[i] : 32768;
                int diff = (high - low) - minDelta[i];

                if (diff < minDiff)
                {
                    minDiff = diff;
                    k = i;
                    if (pass == 20) break;
                }
            }

            if (minDiff == 0) return;

            if (k == 0)
            {
                nlsf[0] = (short)minDelta[0];
            }
            else if (k == order)
            {
                nlsf[order - 1] = (short)(32768 - minDelta[order]);
            }
            else
            {
                int minCenter = 0, maxCenter = 32768;
                for (int i = 0; i < k; i++)
                    minCenter += minDelta[i];
                minCenter += minDelta[k] >> 1;

                for (int i = order; i > k; i--)
                    maxCenter -= minDelta[i];
                maxCenter -= minDelta[k] >> 1;

                int centerVal = nlsf[k - 1] + nlsf[k];
                centerVal = (centerVal >> 1) + (centerVal & 1);
                centerVal = Math.Min(maxCenter, Math.Max(minCenter, centerVal));

                nlsf[k - 1] = (short)(centerVal - (minDelta[k] >> 1));
                nlsf[k] = (short)(nlsf[k - 1] + minDelta[k]);
            }
        }

        // Fallback: insertion sort
        for (int i = 1; i < order; i++)
        {
            int j, value = nlsf[i];
            for (j = i - 1; j >= 0 && nlsf[j] > value; j--)
                nlsf[j + 1] = nlsf[j];
            nlsf[j + 1] = (short)value;
        }

        // Push forwards
        if (nlsf[0] < minDelta[0])
            nlsf[0] = (short)minDelta[0];
        for (int i = 1; i < order; i++)
            nlsf[i] = (short)Math.Max(nlsf[i], Math.Min(nlsf[i - 1] + minDelta[i], 32767));

        // Push backwards
        if (nlsf[order - 1] > 32768 - minDelta[order])
            nlsf[order - 1] = (short)(32768 - minDelta[order]);
        for (int i = order - 2; i >= 0; i--)
            if (nlsf[i] > nlsf[i + 1] - minDelta[i + 1])
                nlsf[i] = (short)(nlsf[i + 1] - minDelta[i + 1]);
    }

    // ---- silk_is_lpc_stable ----

    private static bool IsLpcStable(ReadOnlySpan<short> lpc, int order)
    {
        int dcResp = 0;
        Span<int> row0 = stackalloc int[16];
        Span<int> row1 = stackalloc int[16];
        int totalInvGain = 1 << 30;

        for (int k = 0; k < order; k++)
        {
            dcResp += lpc[k];
            row0[k] = lpc[k] * 4096;
        }

        if (dcResp >= 4096) return false;

        var row = row0;
        for (int k = order - 1; ; k--)
        {
            if (Math.Abs(row[k]) > 16773022) return false;

            int rc = -(row[k] * 128);
            int gainDiv = (1 << 30) - MulH(rc, rc);
            totalInvGain = MulH(totalInvGain, gainDiv) << 2;

            if (k == 0) return totalInvGain >= 107374;

            int fbits = OpusIlog(gainDiv);
            int gain = ((1 << 29) - 1) / (gainDiv >> (fbits + 1 - 16));
            int error = (1 << 29) - (int)Mull(gainDiv << (15 + 16 - fbits), gain, 16);
            gain = (gain << 16) + (error * gain >> 13);

            var prevRow = row;
            row = (k & 1) != 0 ? row1 : row0;

            for (int j = 0; j < k; j++)
            {
                int x = SatSub32(prevRow[j], (int)RoundMull(prevRow[k - j - 1], rc, 31));
                long tmp = RoundMull(x, gain, fbits);
                if (tmp < int.MinValue || tmp > int.MaxValue) return false;
                row[j] = (int)tmp;
            }
        }
    }

    // ---- silk_lsp2poly ----

    private static void Lsp2Poly(ReadOnlySpan<int> lsp, Span<int> pol, int halfOrder)
    {
        pol[0] = 65536; // 1.0 in Q16
        pol[1] = -lsp[0];

        for (int i = 1; i < halfOrder; i++)
        {
            pol[i + 1] = pol[i - 1] * 2 - (int)RoundMull(lsp[2 * i], pol[i], 16);
            for (int j = i; j > 1; j--)
                pol[j] += pol[j - 2] - (int)RoundMull(lsp[2 * i], pol[j - 1], 16);
            pol[1] -= lsp[2 * i];
        }
    }

    // ---- silk_lsf2lpc ----

    private static void Lsf2Lpc(ReadOnlySpan<short> nlsf, Span<float> lpcf, int order)
    {
        Span<int> lsp = stackalloc int[16];
        Span<int> p = stackalloc int[9];
        Span<int> q = stackalloc int[9];
        Span<int> lpc32 = stackalloc int[16];
        Span<short> lpc = stackalloc short[16];

        var orderingNbMb = SilkTables.LsfOrderingNbMb;
        var orderingWb = SilkTables.LsfOrderingWb;
        var cosine = SilkTables.Cosine;

        // Convert LSFs to LSPs: 2*cos(LSF)
        for (int k = 0; k < order; k++)
        {
            int index = nlsf[k] >> 8;
            int offset = nlsf[k] & 255;
            int k2 = (order == 10) ? orderingNbMb[k] : orderingWb[k];

            lsp[k2] = cosine[index] * 256;
            lsp[k2] += (cosine[index + 1] - cosine[index]) * offset;
            lsp[k2] = (lsp[k2] + 4) >> 3;
        }

        Lsp2Poly(lsp, p, order >> 1);
        Lsp2Poly(lsp.Slice(1), q, order >> 1);

        // Reconstruct A(z)
        for (int k = 0; k < order >> 1; k++)
        {
            int pTmp = p[k + 1] + p[k];
            int qTmp = q[k + 1] - q[k];
            lpc32[k] = -qTmp - pTmp;
            lpc32[order - k - 1] = qTmp - pTmp;
        }

        // Bandwidth expansion to limit range
        int iter;
        for (iter = 0; iter < 10; iter++)
        {
            uint maxAbs = 0;
            int maxK = 0;
            // FFmpeg reads lpc32[k] (not j) — k starts at 0, only updates on new max.
            // This is a known quirk we must replicate for bit-exact output.
            for (int j = 0; j < order; j++)
            {
                uint x = (uint)Math.Abs(lpc32[maxK]);
                if (x > maxAbs) { maxAbs = x; maxK = j; }
            }

            maxAbs = (maxAbs + 16) >> 5;

            if (maxAbs > 32767)
            {
                maxAbs = Math.Min(maxAbs, 163838u);
                uint chirpBase, chirp;
                chirpBase = chirp = 65470 - ((maxAbs - 32767) << 14) / ((maxAbs * (uint)(maxK + 1)) >> 2);

                for (int k = 0; k < order; k++)
                {
                    lpc32[k] = (int)RoundMull(lpc32[k], (int)chirp, 16);
                    chirp = (chirpBase * chirp + 32768) >> 16;
                }
            }
            else break;
        }

        if (iter == 10)
        {
            for (int k = 0; k < order; k++)
            {
                int x = (lpc32[k] + 16) >> 5;
                lpc[k] = (short)ClipInt16(x);
                lpc32[k] = lpc[k] << 5;
            }
        }
        else
        {
            for (int k = 0; k < order; k++)
                lpc[k] = (short)((lpc32[k] + 16) >> 5);
        }

        // Stability check with further bandwidth expansion
        for (int i = 1; i <= 16 && !IsLpcStable(lpc, order); i++)
        {
            uint chirpBase, chirp;
            chirpBase = chirp = (uint)(65536 - (1 << i));

            for (int k = 0; k < order; k++)
            {
                lpc32[k] = (int)RoundMull(lpc32[k], (int)chirp, 16);
                lpc[k] = (short)((lpc32[k] + 16) >> 5);
                chirp = (chirpBase * chirp + 32768) >> 16;
            }
        }

        for (int i = 0; i < order; i++)
            lpcf[i] = lpc[i] / 4096.0f;
    }

    // ---- silk_decode_lpc ----

    private void DecodeLpc(SilkFrame frame, ref OpusRangeDecoder rc,
        Span<float> lpcLeadin, Span<float> lpcBody,
        out int lpcOrder, out bool hasLpcLeadin, int voiced)
    {
        int order = wb != 0 ? 16 : 10;
        lpcOrder = order;

        Span<sbyte> lsfI2 = stackalloc sbyte[16];
        Span<short> lsfRes = stackalloc short[16];
        Span<short> nlsf = stackalloc short[16];

        // Stage-1 codebook index
        int lsfI1 = (int)rc.DecCdf(SilkTables.GetModelLsfS1(wb, voiced));

        // Stage-2 indices
        for (int i = 0; i < order; i++)
        {
            int index = wb != 0
                ? SilkTables.GetLsfS2ModelSelWb(lsfI1, i)
                : SilkTables.GetLsfS2ModelSelNbMb(lsfI1, i);
            lsfI2[i] = (sbyte)((int)rc.DecCdf(SilkTables.GetModelLsfS2(index)) - 4);
            if (lsfI2[i] == -4)
                lsfI2[i] -= (sbyte)(int)rc.DecCdf(SilkTables.ModelLsfS2Ext);
            else if (lsfI2[i] == 4)
                lsfI2[i] += (sbyte)(int)rc.DecCdf(SilkTables.ModelLsfS2Ext);
        }

        // Reverse backwards-prediction
        for (int i = order - 1; i >= 0; i--)
        {
            int qstep = wb != 0 ? 9830 : 11796;

            lsfRes[i] = (short)(lsfI2[i] * 1024);
            if (lsfI2[i] < 0) lsfRes[i] += 102;
            else if (lsfI2[i] > 0) lsfRes[i] -= 102;
            lsfRes[i] = (short)((lsfRes[i] * qstep) >> 16);

            if (i + 1 < order)
            {
                int weight = wb != 0
                    ? SilkTables.GetLsfPredWeightWb(SilkTables.GetLsfWeightSelWb(lsfI1, i), i)
                    : SilkTables.GetLsfPredWeightNbMb(SilkTables.GetLsfWeightSelNbMb(lsfI1, i), i);
                lsfRes[i] += (short)((lsfRes[i + 1] * weight) >> 8);
            }
        }

        // Reconstruct NLSF from indices
        var codebookNbMb = SilkTables.LsfCodebookNbMb;
        var codebookWb = SilkTables.LsfCodebookWb;
        int cbStride = wb != 0 ? 16 : 10;

        for (int i = 0; i < order; i++)
        {
            int cur = wb != 0 ? codebookWb[lsfI1 * cbStride + i] : codebookNbMb[lsfI1 * cbStride + i];
            int prev = i != 0 ? (wb != 0 ? codebookWb[lsfI1 * cbStride + i - 1] : codebookNbMb[lsfI1 * cbStride + i - 1]) : 0;
            int next = (i + 1 < order) ? (wb != 0 ? codebookWb[lsfI1 * cbStride + i + 1] : codebookNbMb[lsfI1 * cbStride + i + 1]) : 256;

            int weightSq = (1024 / (cur - prev) + 1024 / (next - cur)) << 16;

            int ipart = OpusIlog(weightSq);
            int fpart = (weightSq >> (ipart - 8)) & 127;
            int y = ((ipart & 1) != 0 ? 32768 : 46214) >> ((32 - ipart) >> 1);
            int weight = y + ((213 * fpart * y) >> 16);

            int value = cur * 128 + (lsfRes[i] * 16384) / weight;
            nlsf[i] = (short)ClipUintP2(value, 15);
        }

        // Stabilize
        StabilizeLsf(nlsf, order, wb != 0 ? SilkTables.LsfMinSpacingWb : SilkTables.LsfMinSpacingNbMb);

        // Interpolation
        hasLpcLeadin = false;
        if (subframes == 4)
        {
            int offset = (int)rc.DecCdf(SilkTables.ModelLsfInterpolationOffset);
            if (offset != 4 && frame.Coded)
            {
                hasLpcLeadin = true;
                if (offset != 0)
                {
                    Span<short> nlsfLeadin = stackalloc short[16];
                    for (int i = 0; i < order; i++)
                        nlsfLeadin[i] = (short)(frame.Nlsf[i] + ((nlsf[i] - frame.Nlsf[i]) * offset >> 2));
                    Lsf2Lpc(nlsfLeadin, lpcLeadin, order);
                }
                else
                {
                    frame.Lpc.AsSpan(0, 16).CopyTo(lpcLeadin);
                }
            }
            else
            {
                offset = 4;
            }
            nlsfInterpFactor = offset;
            Lsf2Lpc(nlsf, lpcBody, order);
        }
        else
        {
            nlsfInterpFactor = 4;
            Lsf2Lpc(nlsf, lpcBody, order);
        }

        nlsf.Slice(0, order).CopyTo(frame.Nlsf);
        lpcBody.Slice(0, order).CopyTo(frame.Lpc);
    }

    // ---- silk_count_children ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CountChildren(ref OpusRangeDecoder rc, int model, int total,
        out int child0, out int child1)
    {
        if (total != 0)
        {
            var locationTable = SilkTables.GetModelPulseLocation(model);
            int tableOffset = ((total - 1 + 5) * (total - 1)) >> 1;
            child0 = (int)rc.DecCdf(locationTable.Slice(tableOffset));
            child1 = total - child0;
        }
        else
        {
            child0 = 0;
            child1 = 0;
        }
    }

    // ---- silk_decode_excitation ----

    private void DecodeExcitation(ref OpusRangeDecoder rc, Span<float> excitationF,
        int qoffsetHigh, int active, int voiced)
    {
        Span<byte> pulseCount = stackalloc byte[20];
        Span<byte> lsbCount = stackalloc byte[20];
        lsbCount.Clear();
        Span<int> excitation = stackalloc int[320];

        uint seed = (uint)rc.DecCdf(SilkTables.ModelLcgSeed);
        int shellBlocks = SilkTables.GetShellBlocks(bandwidth, subframes >> 2);
        int rateLevel = (int)rc.DecCdf(SilkTables.GetModelExcRate(voiced));

        for (int i = 0; i < shellBlocks; i++)
        {
            pulseCount[i] = (byte)rc.DecCdf(SilkTables.GetModelPulseCount(rateLevel));
            if (pulseCount[i] == 17)
            {
                while (pulseCount[i] == 17 && ++lsbCount[i] != 10)
                    pulseCount[i] = (byte)rc.DecCdf(SilkTables.GetModelPulseCount(9));
                if (lsbCount[i] == 10)
                    pulseCount[i] = (byte)rc.DecCdf(SilkTables.GetModelPulseCount(10));
            }
        }

        // Decode pulse locations using PVQ tree
        for (int i = 0; i < shellBlocks; i++)
        {
            if (pulseCount[i] != 0)
            {
                int baseIdx = 16 * i;

                CountChildren(ref rc, 0, pulseCount[i], out int l0, out int r0);
                for (int b = 0; b < 2; b++)
                {
                    int v1 = b == 0 ? l0 : r0;
                    CountChildren(ref rc, 1, v1, out int l1, out int r1);
                    for (int c = 0; c < 2; c++)
                    {
                        int v2 = c == 0 ? l1 : r1;
                        CountChildren(ref rc, 2, v2, out int l2, out int r2);
                        for (int d = 0; d < 2; d++)
                        {
                            int v3 = d == 0 ? l2 : r2;
                            int leafIdx = baseIdx + (b * 4 + c * 2 + d) * 2;
                            CountChildren(ref rc, 3, v3, out excitation[leafIdx], out excitation[leafIdx + 1]);
                        }
                    }
                }
            }
            else
            {
                excitation.Slice(16 * i, 16).Clear();
            }
        }

        // Decode LSBs
        int totalSamples = shellBlocks << 4;
        for (int i = 0; i < totalSamples; i++)
        {
            for (int bit = 0; bit < lsbCount[i >> 4]; bit++)
                excitation[i] = (excitation[i] << 1) | (int)rc.DecCdf(SilkTables.ModelExcitationLsb);
        }

        // Decode signs
        for (int i = 0; i < totalSamples; i++)
        {
            if (excitation[i] != 0)
            {
                int sign = (int)rc.DecCdf(SilkTables.GetModelExcitationSign(
                    active + voiced, qoffsetHigh, Math.Min((int)pulseCount[i >> 4], 6)));
                if (sign == 0)
                    excitation[i] *= -1;
            }
        }

        // Assemble excitation
        int qOffset = SilkTables.GetQuantOffset(voiced, qoffsetHigh);
        for (int i = 0; i < totalSamples; i++)
        {
            int value = excitation[i];
            excitation[i] = value * 256 | qOffset;
            if (value < 0) excitation[i] += 20;
            else if (value > 0) excitation[i] -= 20;

            seed = 196314165 * seed + 907633515;
            if ((seed & 0x80000000) != 0)
                excitation[i] *= -1;
            seed += (uint)value;

            excitationF[i] = excitation[i] / 8388608.0f;
        }
    }

    // ---- silk_decode_frame ----

    private void DecodeFrame(ref OpusRangeDecoder rc,
        int frameNum, int channel, int codedChannels,
        int active, int active1, bool redundant)
    {
        float[] lpcLeadin = new float[16];
        float[] lpcBody = new float[16];
        float[] residual = new float[SilkMaxLag + SilkTables.SilkHistory];
        bool hasLpcLeadin;
        float ltpScale;

        // Per-subframe data
        Span<float> sfGain = stackalloc float[4];
        Span<int> sfPitchLag = stackalloc int[4];
        // LTP taps: 4 subframes × 5 taps = 20 floats
        Span<float> sfLtpTaps = stackalloc float[20];

        SilkFrame fr = frame[channel];

        // Stereo weights (decoded once for channel 0)
        try {
        if (codedChannels == 2 && channel == 0)
        {
            int n = (int)rc.DecCdf(SilkTables.ModelStereoS1);
            int wi0 = (int)rc.DecCdf(SilkTables.ModelStereoS2) + 3 * (n / 5);
            int ws0 = (int)rc.DecCdf(SilkTables.ModelStereoS3);
            int wi1 = (int)rc.DecCdf(SilkTables.ModelStereoS2) + 3 * (n % 5);
            int ws1 = (int)rc.DecCdf(SilkTables.ModelStereoS3);

            var sw = SilkTables.StereoWeights;
            int w0 = sw[wi0] + (((sw[wi0 + 1] - sw[wi0]) * 6554) >> 16) * (ws0 * 2 + 1);
            int w1 = sw[wi1] + (((sw[wi1 + 1] - sw[wi1]) * 6554) >> 16) * (ws1 * 2 + 1);

            stereoWeights[0] = (w0 - w1) / 8192.0f;
            stereoWeights[1] = w1 / 8192.0f;

            midOnly = active1 != 0 ? false : (int)rc.DecCdf(SilkTables.ModelMidOnly) != 0;
        }
        } catch (Exception ex) { throw new Exception($"SILK stereo weights: {ex.Message}", ex); }

        // Frame type
        int voiced;
        int qoffsetHigh;
        if (active == 0)
        {
            qoffsetHigh = (int)rc.DecCdf(SilkTables.ModelFrameTypeInactive);
            voiced = 0;
        }
        else
        {
            int type = (int)rc.DecCdf(SilkTables.ModelFrameTypeActive);
            qoffsetHigh = type & 1;
            voiced = type >> 1;
        }

        // Subframe gains
        try {
        for (int i = 0; i < subframes; i++)
        {
            int logGain;
            if (i == 0 && (frameNum == 0 || !fr.Coded))
            {
                int x = (int)rc.DecCdf(SilkTables.GetModelGainHighbits(active + voiced));
                logGain = (x << 3) | (int)rc.DecCdf(SilkTables.ModelGainLowbits);
                if (fr.Coded)
                    logGain = Math.Max(logGain, fr.LogGain - 16);
            }
            else
            {
                int deltaGain = (int)rc.DecCdf(SilkTables.ModelGainDelta);
                logGain = ClipUintP2(Math.Max((deltaGain << 1) - 16, fr.LogGain + deltaGain - 4), 6);
            }

            fr.LogGain = logGain;

            logGain = (logGain * 0x1D1C71 >> 16) + 2090;
            int ipart = logGain >> 7;
            int fpart = logGain & 127;
            int linGain = (1 << ipart) + ((-174 * fpart * (128 - fpart) >> 16) + fpart) * ((1 << ipart) >> 7);
            sfGain[i] = linGain / 65536.0f;
        }
        } catch (Exception ex) { throw new Exception($"SILK gains: {ex.Message}", ex); }

        // LPC coefficients
        int order;
        try {
        DecodeLpc(fr, ref rc, lpcLeadin, lpcBody, out order, out hasLpcLeadin, voiced);
        } catch (Exception ex) { throw new Exception($"SILK LPC: {ex.Message}", ex); }

        // Pitch lags (voiced frames only)
        try {
        if (voiced != 0)
        {
            bool lagAbsolute = frameNum == 0 || !fr.PrevVoiced;
            int primaryLag = 0;

            if (!lagAbsolute)
            {
                int delta = (int)rc.DecCdf(SilkTables.ModelPitchDelta);
                if (delta != 0)
                    primaryLag = fr.PrimaryLag + delta - 9;
                else
                    lagAbsolute = true;
            }

            if (lagAbsolute)
            {
                int highBits = (int)rc.DecCdf(SilkTables.ModelPitchHighbits);

                // Low bits model depends on bandwidth
                // NB: ModelLcgSeed, MB: ModelPitchLowbitsMb, WB: ModelGainLowbits
                ReadOnlySpan<ushort> lowModel = bandwidth switch
                {
                    0 => SilkTables.ModelLcgSeed,           // NB
                    1 => SilkTables.ModelPitchLowbitsMb,    // MB
                    _ => SilkTables.ModelGainLowbits         // WB
                };
                int lowBits = (int)rc.DecCdf(lowModel);

                primaryLag = SilkTables.PitchMinLag[bandwidth] +
                             highBits * SilkTables.PitchScale[bandwidth] + lowBits;
            }
            fr.PrimaryLag = primaryLag;

            // Pitch contour offsets
            ReadOnlySpan<sbyte> offsets;
            if (subframes == 2)
            {
                if (bandwidth == 0) // NB
                {
                    int idx = (int)rc.DecCdf(SilkTables.ModelPitchContourNb10ms);
                    offsets = SilkTables.PitchOffsetNb10ms.Slice(idx * 2, 2);
                }
                else
                {
                    int idx = (int)rc.DecCdf(SilkTables.ModelPitchContourMbwb10ms);
                    offsets = SilkTables.PitchOffsetMbwb10ms.Slice(idx * 2, 2);
                }
            }
            else
            {
                if (bandwidth == 0) // NB
                {
                    int idx = (int)rc.DecCdf(SilkTables.ModelPitchContourNb20ms);
                    offsets = SilkTables.PitchOffsetNb20ms.Slice(idx * 4, 4);
                }
                else
                {
                    int idx = (int)rc.DecCdf(SilkTables.ModelPitchContourMbwb20ms);
                    offsets = SilkTables.PitchOffsetMbwb20ms.Slice(idx * 4, 4);
                }
            }

            for (int i = 0; i < subframes; i++)
                sfPitchLag[i] = Math.Clamp(primaryLag + offsets[i],
                    SilkTables.PitchMinLag[bandwidth], SilkTables.PitchMaxLag[bandwidth]);

            // LTP filter coefficients
            int ltpFilter = (int)rc.DecCdf(SilkTables.ModelLtpFilter);
            ReadOnlySpan<ushort> filterSel = ltpFilter switch
            {
                0 => SilkTables.ModelLtpFilter0Sel,
                1 => SilkTables.ModelLtpFilter1Sel,
                _ => SilkTables.ModelLtpFilter2Sel
            };
            var taps = SilkTables.GetLtpFilterTaps(ltpFilter);
            int tapsPerEntry = 5;
            int numEntries = ltpFilter switch { 0 => 8, 1 => 16, _ => 32 };

            for (int i = 0; i < subframes; i++)
            {
                int index = (int)rc.DecCdf(filterSel);
                for (int j = 0; j < 5; j++)
                    sfLtpTaps[i * 5 + j] = taps[index * tapsPerEntry + j] / 128.0f;
            }
        }
        } catch (Exception ex) { throw new Exception($"SILK pitch/LTP: {ex.Message}", ex); }

        // LTP scale factor
        if (voiced != 0 && frameNum == 0)
        {
            ltpScale = SilkTables.LtpScaleFactors[(int)rc.DecCdf(SilkTables.ModelLtpScaleIndex)] / 16384.0f;
        }
        else
        {
            ltpScale = 15565.0f / 16384.0f;
        }

        // Decode excitation
        try {
        var excitationSlice = residual.AsSpan(SilkMaxLag);
        DecodeExcitation(ref rc, excitationSlice, qoffsetHigh, active, voiced);
        } catch (Exception ex) { throw new Exception($"SILK excitation (bw={bandwidth} sf={subframes} sfLen={sfLength} fLen={fLength}): {ex.Message}", ex); }

        // Skip synthesis if we don't need the output
        if (OutputChannels == channel || redundant)
            return;

        // ---- Synthesis ----
        try {
        for (int i = 0; i < subframes; i++)
        {
            ReadOnlySpan<float> lpcCoeff = (i < 2 && hasLpcLeadin) ? lpcLeadin : lpcBody;
            int dstBase = SilkTables.SilkHistory + i * sfLength;
            int resBase = SilkMaxLag + i * sfLength;
            int lpcBase = SilkTables.SilkHistory + i * sfLength;
            float sum;

            if (voiced != 0)
            {
                int outEnd;
                float scale;

                if (i < 2 || nlsfInterpFactor == 4)
                {
                    outEnd = -i * sfLength;
                    scale = ltpScale;
                }
                else
                {
                    outEnd = -(i - 2) * sfLength;
                    scale = 1.0f;
                }

                // Re-whitening filter
                for (int j = -sfPitchLag[i] - LtpOrder / 2; j < outEnd; j++)
                {
                    sum = fr.Output[dstBase + j];
                    for (int k = 0; k < order; k++)
                        sum -= lpcCoeff[k] * fr.Output[dstBase + j - k - 1];
                    residual[resBase + j] = ClipF(sum, -1.0f, 1.0f) * scale / sfGain[i];
                }

                if (outEnd != 0)
                {
                    float rescale = sfGain[i - 1] / sfGain[i];
                    for (int j = outEnd; j < 0; j++)
                        residual[resBase + j] *= rescale;
                }

                // LTP synthesis
                for (int j = 0; j < sfLength; j++)
                {
                    sum = residual[resBase + j];
                    for (int k = 0; k < LtpOrder; k++)
                        sum += sfLtpTaps[i * 5 + k] * residual[resBase + j - sfPitchLag[i] + LtpOrder / 2 - k];
                    residual[resBase + j] = sum;
                }
            }

            // LPC synthesis
            for (int j = 0; j < sfLength; j++)
            {
                sum = residual[resBase + j] * sfGain[i];
                for (int k = 1; k <= order; k++)
                    sum += lpcCoeff[k - 1] * fr.LpcHistory[lpcBase + j - k];

                fr.LpcHistory[lpcBase + j] = sum;
                fr.Output[dstBase + j] = ClipF(sum, -1.0f, 1.0f);
            }
        }
        } catch (Exception ex) { throw new Exception($"SILK synthesis (v={voiced} sf={subframes} sfLen={sfLength} fLen={fLength}): {ex.Message}", ex); }

        fr.PrevVoiced = voiced != 0;
        Array.Copy(fr.LpcHistory, fLength, fr.LpcHistory, 0, SilkTables.SilkHistory);
        Array.Copy(fr.Output, fLength, fr.Output, 0, SilkTables.SilkHistory);

        fr.Coded = true;
    }

    // ---- silk_unmix_ms ----

    private void UnmixMs(Span<float> l, Span<float> r)
    {
        int midBase = SilkTables.SilkHistory - fLength;
        int sideBase = SilkTables.SilkHistory - fLength;
        float w0Prev = prevStereoWeights[0];
        float w1Prev = prevStereoWeights[1];
        float w0 = stereoWeights[0];
        float w1 = stereoWeights[1];
        int n1 = SilkTables.StereoInterpLen[bandwidth];

        for (int i = 0; i < n1; i++)
        {
            float interp0 = w0Prev + i * (w0 - w0Prev) / n1;
            float interp1 = w1Prev + i * (w1 - w1Prev) / n1;
            float p0 = 0.25f * (frame[0].Output[midBase + i - 2] + 2 * frame[0].Output[midBase + i - 1] + frame[0].Output[midBase + i]);

            l[i] = ClipF((1 + interp1) * frame[0].Output[midBase + i - 1] + frame[1].Output[sideBase + i - 1] + interp0 * p0, -1.0f, 1.0f);
            r[i] = ClipF((1 - interp1) * frame[0].Output[midBase + i - 1] - frame[1].Output[sideBase + i - 1] - interp0 * p0, -1.0f, 1.0f);
        }

        for (int i = n1; i < fLength; i++)
        {
            float p0 = 0.25f * (frame[0].Output[midBase + i - 2] + 2 * frame[0].Output[midBase + i - 1] + frame[0].Output[midBase + i]);

            l[i] = ClipF((1 + w1) * frame[0].Output[midBase + i - 1] + frame[1].Output[sideBase + i - 1] + w0 * p0, -1.0f, 1.0f);
            r[i] = ClipF((1 - w1) * frame[0].Output[midBase + i - 1] - frame[1].Output[sideBase + i - 1] - w0 * p0, -1.0f, 1.0f);
        }

        stereoWeights.CopyTo(prevStereoWeights, 0);
    }

    // ---- ff_silk_decode_superframe ----

    /// <summary>
    /// Decode a complete SILK superframe.
    /// </summary>
    /// <param name="rc">Range coder positioned at start of SILK data.</param>
    /// <param name="output">Output buffer: output[ch][sample]. Must have 2 channel arrays pre-allocated.</param>
    /// <param name="bandwidth">0=NB, 1=MB, 2=WB.</param>
    /// <param name="codedChannels">1 for mono, 2 for stereo.</param>
    /// <param name="durationMs">Frame duration in ms (10, 20, 40, or 60).</param>
    /// <returns>Total number of samples decoded per channel, or -1 on error.</returns>
    public int DecodeSuperframe(ref OpusRangeDecoder rc, float[][] output,
        int bandwidth, int codedChannels, int durationMs)
    {
        if (bandwidth > 2 || codedChannels > 2 || durationMs > 60)
            return -1;

        int nbFrames = 1 + (durationMs > 20 ? 1 : 0) + (durationMs > 40 ? 1 : 0);
        this.subframes = durationMs / nbFrames / 5;
        this.sfLength = 20 * (bandwidth + 2);
        this.fLength = sfLength * subframes;
        this.bandwidth = bandwidth;
        this.wb = bandwidth == 2 ? 1 : 0;

        // Flush side channel when switching mono→stereo
        if (codedChannels > prevCodedChannels)
            frame[1].Flush();
        prevCodedChannels = codedChannels;

        // Read LP-layer header: activity flags per channel per frame
        Span<int> active = stackalloc int[12]; // [2][6] flattened
        Span<int> redundancy = stackalloc int[2];

        for (int i = 0; i < codedChannels; i++)
        {
            for (int j = 0; j < nbFrames; j++)
                active[i * 6 + j] = (int)rc.DecLog(1);
            redundancy[i] = (int)rc.DecLog(1);
        }

        // Read per-frame LBRR flags
        for (int i = 0; i < codedChannels; i++)
        {
            if (redundancy[i] != 0 && durationMs > 20)
            {
                redundancy[i] = (int)rc.DecCdf(durationMs == 40
                    ? SilkTables.ModelLbrrFlags40
                    : SilkTables.ModelLbrrFlags60);
            }
        }

        // Decode LBRR frames (redundancy)
        for (int i = 0; i < nbFrames; i++)
        {
            for (int j = 0; j < codedChannels; j++)
            {
                if ((redundancy[j] & (1 << i)) != 0)
                {
                    int a1 = (j == 0 && (redundancy[1] & (1 << i)) == 0) ? 0 : 1;
                    DecodeFrame(ref rc, i, j, codedChannels, 1, a1, true);
                }
            }
            midOnly = false;
        }

        // Decode main frames
        for (int i = 0; i < nbFrames; i++)
        {
            for (int j = 0; j < codedChannels && !midOnly; j++)
            {
                int a1 = codedChannels > 1 ? active[1 * 6 + i] : 0;
                DecodeFrame(ref rc, i, j, codedChannels, active[j * 6 + i], a1, false);
            }

            // Reset side channel if not coded
            if (midOnly && frame[1].Coded)
                frame[1].Flush();

            if (codedChannels == 1 || OutputChannels == 1)
            {
                for (int j = 0; j < OutputChannels; j++)
                {
                    // Copy from SILK_HISTORY - fLength - 2 to match FFmpeg
                    Array.Copy(frame[0].Output, SilkTables.SilkHistory - fLength - 2,
                        output[j], i * fLength, fLength);
                }
            }
            else
            {
                UnmixMs(
                    output[0].AsSpan(i * fLength, fLength),
                    output[1].AsSpan(i * fLength, fLength));
            }

            midOnly = false;
        }

        return nbFrames * fLength;
    }

    /// <summary>
    /// Flushes SILK state for mode switching (matches FFmpeg's ff_silk_flush).
    /// Clears frame state and stereo weights but preserves coded_channels.
    /// </summary>
    public void Flush()
    {
        frame[0].Flush();
        frame[1].Flush();
        Array.Clear(prevStereoWeights);
    }
    
    /// <summary>Reset the decoder state.</summary>
    public void Reset()
    {
        frame[0].Flush();
        frame[1].Flush();
        Array.Clear(prevStereoWeights);
        Array.Clear(stereoWeights);
        prevCodedChannels = 0;
    }
}

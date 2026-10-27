// Licensed under the MIT License.
// Faithful port of FFmpeg's libavcodec/opus/pvq.c — CELT PVQ (Pyramid Vector Quantization) decoder.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// CELT PVQ (Pyramid Vector Quantization) decoder.
/// Ported from FFmpeg's pvq.c — decode-only path.
/// </summary>
public sealed class CeltPvq
{
    // Work buffers (allocated once, reused across frames)
    private readonly int[] qcoeff = new int[256];
    private readonly float[] hadamardTmp = new float[256];

    /// <summary>Gets the PVQ U-table value: U(n, k) = pvq_u_row[min(n,k)][max(n,k)].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PvqU(int n, int k)
    {
        int mn = Math.Min(n, k);
        int mx = Math.Max(n, k);
        return CeltTables.PvqU[CeltTables.PvqURowOffset[mn] + mx];
    }

    /// <summary>V(n, k) = U(n, k) + U(n, k+1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PvqV(int n, int k)
    {
        return PvqU(n, k) + PvqU(n, k + 1);
    }

    /// <summary>
    /// Approximate cosine for CELT angle coding. Port of celt_cos().
    /// Input x is in Q15 (0..16384 = 0..pi/2). Returns Q15 result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CeltCos(int x)
    {
        x = (x * x + 4096) >> 13;
        x = (32767 - x) + RoundMul16(x, -7651 + RoundMul16(x, 8277 + RoundMul16(-626, x)));
        return x + 1;
    }

    /// <summary>
    /// Log2(tan(angle)) approximation for mid/side bit allocation.
    /// Port of celt_log2tan().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CeltLog2Tan(int isin, int icos)
    {
        int lc = OpusIlog(icos);
        int ls = OpusIlog(isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return (ls << 11) - (lc << 11) +
               RoundMul16(isin, RoundMul16(isin, -2597) + 7932) -
               RoundMul16(icos, RoundMul16(icos, -2597) + 7932);
    }

    /// <summary>opus_ilog(i) = av_log2(i) + !!i</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OpusIlog(int i)
    {
        if (i <= 0) return 0;
        return BitOperations.Log2((uint)i) + 1;
    }

    /// <summary>ROUND_MUL16(a, b) = ((a*b) + 16384) >> 15</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundMul16(int a, int b)
    {
        return (a * b + 16384) >> 15;
    }

    /// <summary>
    /// Binary search to convert bits to pulse count using cache table.
    /// Port of celt_bits2pulses().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bits2Pulses(ReadOnlySpan<byte> cache, int bits)
    {
        int low = 0;
        int high = cache[0];
        bits--;

        for (int i = 0; i < 6; i++)
        {
            int center = (low + high + 1) >> 1;
            if (cache[center] >= bits)
                high = center;
            else
                low = center;
        }

        return (bits - (low == 0 ? -1 : cache[low]) <= cache[high] - bits) ? low : high;
    }

    /// <summary>
    /// Convert pulse count to bits using cache table.
    /// Port of celt_pulses2bits().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pulses2Bits(ReadOnlySpan<byte> cache, int pulses)
    {
        return pulses == 0 ? 0 : cache[pulses] + 1;
    }

    /// <summary>
    /// Normalize residual: X[i] = g * iy[i].
    /// Port of celt_normalize_residual().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NormalizeResidual(ReadOnlySpan<int> iy, Span<float> X, int N, float g)
    {
        for (int i = 0; i < N; i++)
            X[i] = g * iy[i];
    }

    /// <summary>
    /// Apply exponential rotation (decode direction).
    /// Port of celt_exp_rotation() with encode=0.
    /// </summary>
    private static void ExpRotation(Span<float> X, int len, int stride, int K, CeltSpread spread)
    {
        if (2 * K >= len || spread == CeltSpread.None)
            return;

        float gain = (float)len / (len + (20 - 5 * (int)spread) * K);
        float theta = MathF.PI * gain * gain / 4;

        float c = MathF.Cos(theta);
        float s = MathF.Sin(theta);

        int stride2 = 0;
        if (len >= stride << 3)
        {
            stride2 = 1;
            while ((stride2 * stride2 + stride2) * stride + (stride >> 2) < len)
                stride2++;
        }

        int subLen = len / stride;
        for (int i = 0; i < stride; i++)
        {
            var sub = X.Slice(i * subLen, subLen);
            // Decode direction: stride2 first, then stride1
            if (stride2 > 0)
                ExpRotationImpl(sub, subLen, stride2, s, c);
            ExpRotationImpl(sub, subLen, 1, c, s);
        }
    }

    /// <summary>Port of celt_exp_rotation_impl().</summary>
    private static void ExpRotationImpl(Span<float> X, int len, int stride, float c, float s)
    {
        // Forward pass
        for (int i = 0; i < len - stride; i++)
        {
            float x1 = X[i];
            float x2 = X[i + stride];
            X[i + stride] = c * x2 + s * x1;
            X[i] = c * x1 - s * x2;
        }

        // Reverse pass
        for (int i = len - 2 * stride - 1; i >= 0; i--)
        {
            float x1 = X[i];
            float x2 = X[i + stride];
            X[i + stride] = c * x2 + s * x1;
            X[i] = c * x1 - s * x2;
        }
    }

    /// <summary>
    /// Extract collapse mask from quantized vector.
    /// Port of celt_extract_collapse_mask().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ExtractCollapseMask(ReadOnlySpan<int> iy, int N, int B)
    {
        if (B <= 1)
            return 1;

        int N0 = N / B;
        uint mask = 0;
        for (int i = 0; i < B; i++)
            for (int j = 0; j < N0; j++)
                if (iy[i * N0 + j] != 0)
                    mask |= 1u << i;
        return mask;
    }

    /// <summary>
    /// Stereo merge: combine mid and side into left and right.
    /// Port of celt_stereo_merge().
    /// </summary>
    private static void StereoMerge(Span<float> X, Span<float> Y, float mid, int N)
    {
        float xp = 0, side = 0;
        for (int i = 0; i < N; i++)
        {
            xp += X[i] * Y[i];
            side += Y[i] * Y[i];
        }

        xp *= mid;
        float mid2 = mid;
        float e0 = mid2 * mid2 + side - 2 * xp;
        float e1 = mid2 * mid2 + side + 2 * xp;

        if (e0 < 6e-4f || e1 < 6e-4f)
        {
            X.Slice(0, N).CopyTo(Y);
            return;
        }

        float gain0 = 1.0f / MathF.Sqrt(e0);
        float gain1 = 1.0f / MathF.Sqrt(e1);

        for (int i = 0; i < N; i++)
        {
            float v0 = mid * X[i];
            float v1 = Y[i];
            X[i] = gain0 * (v0 - v1);
            Y[i] = gain1 * (v0 + v1);
        }
    }

    /// <summary>
    /// Interleave with Hadamard ordering. Port of celt_interleave_hadamard().
    /// </summary>
    private void InterleaveHadamard(Span<float> X, int N0, int stride, bool hadamard)
    {
        int N = N0 * stride;
        ReadOnlySpan<byte> order = CeltTables.HadamardOrder;
        int orderOffset = hadamard ? stride - 2 : 30;

        for (int i = 0; i < stride; i++)
            for (int j = 0; j < N0; j++)
                hadamardTmp[j * stride + i] = X[order[orderOffset + i] * N0 + j];

        hadamardTmp.AsSpan(0, N).CopyTo(X);
    }

    /// <summary>
    /// Deinterleave with Hadamard ordering. Port of celt_deinterleave_hadamard().
    /// </summary>
    private void DeinterleaveHadamard(Span<float> X, int N0, int stride, bool hadamard)
    {
        int N = N0 * stride;
        ReadOnlySpan<byte> order = CeltTables.HadamardOrder;
        int orderOffset = hadamard ? stride - 2 : 30;

        for (int i = 0; i < stride; i++)
            for (int j = 0; j < N0; j++)
                hadamardTmp[order[orderOffset + i] * N0 + j] = X[j * stride + i];

        hadamardTmp.AsSpan(0, N).CopyTo(X);
    }

    /// <summary>
    /// Haar wavelet transform for time-frequency resolution changes.
    /// Port of celt_haar1().
    /// </summary>
    private static void Haar1(Span<float> X, int N0, int stride)
    {
        float sqrt1_2 = MathF.Sqrt(0.5f);
        N0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < N0; j++)
            {
                float x0 = X[stride * (2 * j + 0) + i];
                float x1 = X[stride * (2 * j + 1) + i];
                X[stride * (2 * j + 0) + i] = (x0 + x1) * sqrt1_2;
                X[stride * (2 * j + 1) + i] = (x0 - x1) * sqrt1_2;
            }
        }
    }

    /// <summary>
    /// Compute quantization resolution qn. Port of celt_compute_qn().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeQn(int N, int b, int offset, int pulseCap, bool stereo)
    {
        int N2 = 2 * N - 1;
        if (stereo && N == 2)
            N2--;

        int qb = Math.Min(Math.Min(b - pulseCap - (4 << 3), (b + N2 * offset) / N2), 8 << 3);
        int qn = (qb < (1 << 3 >> 1))
            ? 1
            : ((CeltTables.QnExp2[qb & 0x7] >> (14 - (qb >> 3))) + 1) >> 1 << 1;
        return qn;
    }

    /// <summary>
    /// CWRS decoding: convert index to pulse vector.
    /// Port of celt_cwrsi(). Returns squared norm.
    /// </summary>
    private static ulong Cwrsi(int N, int K, uint idx, Span<int> y)
    {
        ulong norm = 0;
        uint q, p;
        int s, val, k0;
        int yIdx = 0;

        while (N > 2)
        {
            if (K >= N)
            {
                // Lots of pulses case
                ReadOnlySpan<uint> pvqU = CeltTables.PvqU;
                int rowN = CeltTables.PvqURowOffset[N];

                p = pvqU[rowN + K + 1];
                s = idx >= p ? -1 : 0;
                idx -= p & (uint)s;

                k0 = K;
                q = pvqU[rowN + N];
                if (q > idx)
                {
                    K = N;
                    do
                    {
                        K--;
                        p = pvqU[CeltTables.PvqURowOffset[K] + N];
                    } while (p > idx);
                }
                else
                {
                    for (p = pvqU[rowN + K]; p > idx; p = pvqU[rowN + K])
                        K--;
                }

                idx -= p;
                val = (k0 - K + s) ^ s;
                norm += (ulong)(val * val);
                y[yIdx++] = val;
            }
            else
            {
                // Lots of dimensions case
                p = CeltTables.PvqU[CeltTables.PvqURowOffset[K] + N];
                q = CeltTables.PvqU[CeltTables.PvqURowOffset[K + 1] + N];

                if (p <= idx && idx < q)
                {
                    idx -= p;
                    y[yIdx++] = 0;
                }
                else
                {
                    s = idx >= q ? -1 : 0;
                    idx -= q & (uint)s;

                    k0 = K;
                    do
                    {
                        K--;
                        p = CeltTables.PvqU[CeltTables.PvqURowOffset[K] + N];
                    } while (p > idx);

                    idx -= p;
                    val = (k0 - K + s) ^ s;
                    norm += (ulong)(val * val);
                    y[yIdx++] = val;
                }
            }
            N--;
        }

        // N == 2
        p = (uint)(2 * K + 1);
        s = idx >= p ? -1 : 0;
        idx -= p & (uint)s;
        k0 = K;
        K = (int)(idx + 1) / 2;

        if (K != 0)
            idx -= (uint)(2 * K - 1);

        val = (k0 - K + s) ^ s;
        norm += (ulong)(val * val);
        y[yIdx++] = val;

        // N == 1
        s = -(int)idx;
        val = (K + s) ^ s;
        norm += (ulong)(val * val);
        y[yIdx] = val;

        return norm;
    }

    /// <summary>
    /// Decode PVQ pulses from the range coder. Returns squared norm.
    /// Port of celt_decode_pulses().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DecodePulses(ref OpusRangeDecoder rc, Span<int> y, int N, int K)
    {
        uint idx = rc.DecUint(PvqV(N, K));
        return Cwrsi(N, K, idx, y);
    }

    /// <summary>
    /// Decode and dequantize a PVQ band (no split).
    /// Port of celt_alg_unquant().
    /// </summary>
    private uint AlgUnquant(ref OpusRangeDecoder rc, Span<float> X, int N, int K,
                            CeltSpread spread, int blocks, float gain)
    {
        gain /= MathF.Sqrt(DecodePulses(ref rc, qcoeff, N, K));
        NormalizeResidual(qcoeff, X, N, gain);
        ExpRotation(X, N, blocks, K, spread);
        return ExtractCollapseMask(qcoeff, N, blocks);
    }

    /// <summary>
    /// Renormalize vector to have given gain. Port of celt_renormalize_vector().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RenormalizeVector(Span<float> X, int N, float gain)
    {
        float g = 1e-15f;
        for (int i = 0; i < N; i++)
            g += X[i] * X[i];
        g = gain / MathF.Sqrt(g);
        for (int i = 0; i < N; i++)
            X[i] *= g;
    }

    /// <summary>
    /// Main band quantization/dequantization function (decode path).
    /// Port of quant_band_template() with quant=0.
    /// This is the heart of the CELT PVQ decoder.
    /// </summary>
    public uint QuantBand(ref CeltFrame f, ref OpusRangeDecoder rc, int band,
                          Span<float> X, Span<float> Y, int N, int b,
                          int blocks, Span<float> lowband, int duration,
                          Span<float> lowbandOut, int level, float gain,
                          Span<float> lowbandScratch, int fill)
    {
        bool stereo = !Y.IsEmpty;
        bool split = stereo;
        int imid = 0, iside = 0;
        int N0 = N;
        int N_B = N / blocks;
        int N_B0 = N_B;
        int B0 = blocks;
        int timeDivide = 0;
        int recombine = 0;
        int inv = 0;
        float mid = 0, side = 0;
        bool longblocks = (B0 == 1);
        uint cm = 0;

        if (N == 1)
        {
            // Single-sample bands
            Span<float> x = X;
            for (int i = 0; i <= (stereo ? 1 : 0); i++)
            {
                int sign = 0;
                if (f.Remaining2 >= 1 << 3)
                {
                    sign = (int)rc.GetRaw(1);
                    f.Remaining2 -= 1 << 3;
                }
                x[0] = 1.0f - 2.0f * sign;
                x = Y;
            }
            if (!lowbandOut.IsEmpty)
                lowbandOut[0] = X[0];
            return 1;
        }

        if (!stereo && level == 0)
        {
            int tfChange = f.TfChange[band];

            if (tfChange > 0)
                recombine = tfChange;

            if (!lowband.IsEmpty &&
                (recombine != 0 || ((N_B & 1) == 0 && tfChange < 0) || B0 > 1))
            {
                for (int i = 0; i < N; i++)
                    lowbandScratch[i] = lowband[i];
                lowband = lowbandScratch;
            }

            for (int k = 0; k < recombine; k++)
            {
                if (!lowband.IsEmpty)
                    Haar1(lowband, N >> k, 1 << k);
                fill = CeltTables.BitInterleave[fill & 0xF] | (CeltTables.BitInterleave[fill >> 4] << 2);
            }
            blocks >>= recombine;
            N_B <<= recombine;

            // Increasing time resolution
            while ((N_B & 1) == 0 && tfChange < 0)
            {
                if (!lowband.IsEmpty)
                    Haar1(lowband, N_B, blocks);
                fill |= fill << blocks;
                blocks <<= 1;
                N_B >>= 1;
                timeDivide++;
                tfChange++;
            }
            B0 = blocks;
            N_B0 = N_B;

            // Reorganize time→frequency order
            if (B0 > 1 && !lowband.IsEmpty)
                DeinterleaveHadamard(lowband, N_B >> recombine, B0 << recombine, longblocks);
        }

        // Split the band if needed
        ReadOnlySpan<byte> cacheBits = CeltTables.CacheBits;
        ReadOnlySpan<short> cacheIndex = CeltTables.CacheIndex;
        int cacheIdx = cacheIndex[(duration + 1) * CeltDecoder.MaxBands + band];
        ReadOnlySpan<byte> cache = cacheBits.Slice(cacheIdx);

        if (!stereo && duration >= 0 && b > cache[cache[0]] + 12 && N > 2)
        {
            N >>= 1;
            Y = X.Slice(N);
            split = true;
            duration -= 1;
            if (blocks == 1)
                fill = (fill & 1) | (fill << 1);
            blocks = (blocks + 1) >> 1;
        }

        if (split)
        {
            int itheta = 0;
            int mbits, sbits, delta;
            int qalloc;
            int pulseCap;
            int offset;

            pulseCap = CeltTables.LogFreqRange[band] + duration * 8;
            offset = (pulseCap >> 1) - (stereo && N == 2
                ? CeltDecoder.QthetaOffsetTwophase
                : CeltDecoder.QthetaOffset);
            int qn = (stereo && band >= f.IntensityStereo)
                ? 1
                : ComputeQn(N, b, offset, pulseCap, stereo);

            int tell = (int)rc.TellFrac();

            if (qn != 1)
            {
                // Decode theta
                if (stereo && N > 2)
                    itheta = (int)rc.DecUintStep(qn / 2);
                else if (stereo || B0 > 1)
                    itheta = (int)rc.DecUint((uint)(qn + 1));
                else
                    itheta = (int)rc.DecUintTri(qn);
                itheta = itheta * 16384 / qn;
            }
            else if (stereo)
            {
                inv = (b > 2 << 3 && f.Remaining2 > 2 << 3) ? (int)rc.DecLog(2) : 0;
                inv = f.ApplyPhaseInv ? inv : 0;
                itheta = 0;
            }

            qalloc = (int)rc.TellFrac() - tell;
            b -= qalloc;

            int origFill = fill;
            if (itheta == 0)
            {
                imid = 32767;
                iside = 0;
                fill = (int)(fill & ((1u << blocks) - 1));
                delta = -16384;
            }
            else if (itheta == 16384)
            {
                imid = 0;
                iside = 32767;
                fill &= (int)(((1u << blocks) - 1) << blocks);
                delta = 16384;
            }
            else
            {
                imid = CeltCos(itheta);
                iside = CeltCos(16384 - itheta);
                delta = RoundMul16((N - 1) << 7, CeltLog2Tan(iside, imid));
            }

            mid = imid / 32768.0f;
            side = iside / 32768.0f;

            if (N == 2 && stereo)
            {
                // Special N=2 stereo case
                int sign = 0;
                mbits = b;
                sbits = (itheta != 0 && itheta != 16384) ? 1 << 3 : 0;
                mbits -= sbits;
                int c = (itheta > 8192) ? 1 : 0;
                f.Remaining2 -= qalloc + sbits;

                Span<float> x2 = c != 0 ? Y : X;
                Span<float> y2 = c != 0 ? X : Y;

                if (sbits != 0)
                    sign = (int)rc.GetRaw(1);
                sign = 1 - 2 * sign;

                cm = QuantBand(ref f, ref rc, band, x2, Span<float>.Empty, N, mbits, blocks,
                               lowband, duration, lowbandOut, level, gain, lowbandScratch, origFill);

                y2[0] = -sign * x2[1];
                y2[1] = sign * x2[0];
                X[0] *= mid;
                X[1] *= mid;
                Y[0] *= side;
                Y[1] *= side;
                float tmp = X[0];
                X[0] = tmp - Y[0];
                Y[0] = tmp + Y[0];
                tmp = X[1];
                X[1] = tmp - Y[1];
                Y[1] = tmp + Y[1];
            }
            else
            {
                // Normal split code
                Span<float> nextLowband2 = Span<float>.Empty;
                Span<float> nextLowbandOut1 = Span<float>.Empty;
                int nextLevel = 0;

                if (B0 > 1 && !stereo && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192)
                        delta -= delta >> (4 - duration);
                    else
                        delta = Math.Min(0, delta + (N << 3 >> (5 - duration)));
                }
                mbits = Math.Clamp((b - delta) / 2, 0, b);
                sbits = b - mbits;
                f.Remaining2 -= qalloc;

                if (!lowband.IsEmpty && !stereo)
                    nextLowband2 = lowband.Slice(N);

                if (stereo)
                    nextLowbandOut1 = lowbandOut;
                else
                    nextLevel = level + 1;

                int rebalance = f.Remaining2;
                if (mbits >= sbits)
                {
                    cm = QuantBand(ref f, ref rc, band, X, Span<float>.Empty, N, mbits, blocks,
                                   lowband, duration, nextLowbandOut1, nextLevel,
                                   stereo ? 1.0f : gain * mid, lowbandScratch, fill);
                    rebalance = mbits - (rebalance - f.Remaining2);
                    if (rebalance > 3 << 3 && itheta != 0)
                        sbits += rebalance - (3 << 3);

                    uint cmt = QuantBand(ref f, ref rc, band, Y, Span<float>.Empty, N, sbits, blocks,
                                         nextLowband2, duration, Span<float>.Empty, nextLevel,
                                         gain * side, Span<float>.Empty, fill >> blocks);
                    cm |= cmt << ((B0 >> 1) & (stereo ? 0 : -1));
                }
                else
                {
                    cm = QuantBand(ref f, ref rc, band, Y, Span<float>.Empty, N, sbits, blocks,
                                   nextLowband2, duration, Span<float>.Empty, nextLevel,
                                   gain * side, Span<float>.Empty, fill >> blocks);
                    cm <<= ((B0 >> 1) & (stereo ? 0 : -1));
                    rebalance = sbits - (rebalance - f.Remaining2);
                    if (rebalance > 3 << 3 && itheta != 16384)
                        mbits += rebalance - (3 << 3);

                    cm |= QuantBand(ref f, ref rc, band, X, Span<float>.Empty, N, mbits, blocks,
                                    lowband, duration, nextLowbandOut1, nextLevel,
                                    stereo ? 1.0f : gain * mid, lowbandScratch, fill);
                }
            }
        }
        else
        {
            // No-split case: actual PVQ decode or zero-fill
            int q = Bits2Pulses(cache, b);
            int currBits = Pulses2Bits(cache, q);
            f.Remaining2 -= currBits;

            while (f.Remaining2 < 0 && q > 0)
            {
                f.Remaining2 += currBits;
                currBits = Pulses2Bits(cache, --q);
                f.Remaining2 -= currBits;
            }

            if (q != 0)
            {
                int K = (q < 8) ? q : (8 + (q & 7)) << ((q >> 3) - 1);
                cm = AlgUnquant(ref rc, X, N, K, f.Spread, blocks, gain);
            }
            else
            {
                // No pulses — fill with noise or folded spectrum
                uint cmMask = (uint)((1 << blocks) - 1);
                fill &= (int)cmMask;
                if (fill != 0)
                {
                    if (lowband.IsEmpty)
                    {
                        // Noise fill
                        for (int i = 0; i < N; i++)
                            X[i] = ((int)CeltRng(ref f) >> 20);
                        cm = cmMask;
                    }
                    else
                    {
                        // Folded spectrum
                        for (int i = 0; i < N; i++)
                            X[i] = lowband[i] + ((CeltRng(ref f) & 0x8000) != 0 ? 1.0f / 256 : -1.0f / 256);
                        cm = (uint)fill;
                    }
                    RenormalizeVector(X, N, gain);
                }
                else
                {
                    X.Slice(0, N).Clear();
                }
            }
        }

        // Post-processing
        if (stereo)
        {
            if (N > 2)
                StereoMerge(X, Y, mid, N);
            if (inv != 0)
            {
                for (int i = 0; i < N; i++)
                    Y[i] *= -1;
            }
        }
        else if (level == 0)
        {
            // Undo time-frequency manipulations
            if (B0 > 1)
                InterleaveHadamard(X, N_B >> recombine, B0 << recombine, longblocks);

            N_B = N_B0;
            blocks = B0;
            for (int k = 0; k < timeDivide; k++)
            {
                blocks >>= 1;
                N_B <<= 1;
                cm |= cm >> blocks;
                Haar1(X, N_B, blocks);
            }

            for (int k = 0; k < recombine; k++)
            {
                cm = CeltTables.BitDeinterleave[(int)cm];
                Haar1(X, N0 >> k, 1 << k);
            }
            blocks <<= recombine;

            // Scale output for later folding
            if (!lowbandOut.IsEmpty)
            {
                float n = MathF.Sqrt(N0);
                for (int i = 0; i < N0; i++)
                    lowbandOut[i] = n * X[i];
            }
            cm &= (uint)((1 << blocks) - 1);
        }

        return cm;
    }

    /// <summary>LCG for noise generation. Port of celt_rng().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CeltRng(ref CeltFrame f)
    {
        f.Seed = 1664525 * f.Seed + 1013904223;
        return f.Seed;
    }
}

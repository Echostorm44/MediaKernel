// Licensed under the MIT License.
// Faithful port of FFmpeg's libavcodec/opus/dec_celt.c + celt.c + dsp.c — CELT frame decoder.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

// ────────────────────────────────────────────────────────────────────────────
// Constants
// ────────────────────────────────────────────────────────────────────────────

public enum CeltSpread
{
    None = 0,
    Light = 1,
    Normal = 2,
    Aggressive = 3
}

// ────────────────────────────────────────────────────────────────────────────
// CeltBlock — per-channel persistent state
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-channel CELT decoder state. Persists across frames.
/// Port of FFmpeg's CeltBlock struct (celt.h).
/// </summary>
public sealed class CeltBlock
{
    public const int MaxBands = 21;
    public const int MaxFrameSize = 960;
    public const int BufSize = 2048;
    public const int Overlap = 120;

    public readonly float[] Energy = new float[MaxBands];
    public readonly float[] LinEnergy = new float[MaxBands];
    public readonly float[] ErrorEnergy = new float[MaxBands];
    public readonly float[][] PrevEnergy = [new float[MaxBands], new float[MaxBands]];

    public readonly byte[] CollapseMasks = new byte[MaxBands];

    /// <summary>Buffer for IMDCT output + postfilter. 2048 floats per FFmpeg.</summary>
    public readonly float[] Buf = new float[BufSize];

    /// <summary>MDCT coefficients for current frame.</summary>
    public readonly float[] Coeffs = new float[MaxFrameSize];

    // Postfilter parameters
    public int PfPeriodNew;
    public float[] PfGainsNew = new float[3];
    public int PfPeriod;
    public float[] PfGains = new float[3];
    public int PfPeriodOld;
    public float[] PfGainsOld = new float[3];

    public float EmphCoeff;

    public void Clear()
    {
        Array.Clear(Energy);
        Array.Clear(LinEnergy);
        Array.Clear(ErrorEnergy);
        Array.Clear(PrevEnergy[0]);
        Array.Clear(PrevEnergy[1]);
        Array.Clear(CollapseMasks);
        Array.Clear(Buf);
        Array.Clear(Coeffs);
        PfPeriodNew = 0;
        Array.Clear(PfGainsNew);
        PfPeriod = 0;
        Array.Clear(PfGains);
        PfPeriodOld = 0;
        Array.Clear(PfGainsOld);
        EmphCoeff = 0;
    }
}

// ────────────────────────────────────────────────────────────────────────────
// CeltFrame — per-frame decode state
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// CELT frame-level state. Some fields persist (block[], seed), most are per-frame.
/// Port of FFmpeg's CeltFrame struct (celt.h).
/// </summary>
public sealed class CeltFrame
{
    public readonly CeltBlock[] Block = [new CeltBlock(), new CeltBlock()];

    public int Channels;
    public int OutputChannels;
    public bool ApplyPhaseInv;

    public int Size;       // log2(frame_size / 120): 0..3
    public int StartBand;
    public int EndBand;
    public int CodedBands;
    public int Transient;
    public int Pfilter;
    public int SkipBandFloor;
    public int TfSelect;
    public int AllocTrim;
    public readonly int[] AllocBoost = new int[CeltDecoder.MaxBands];
    public int Blocks;     // number of IMDCT blocks per frame
    public int BlockSize;
    public int Silence;
    public int AnticollaspeNeeded;
    public int Anticollapse;
    public int IntensityStereo;
    public int DualStereo;
    public int Flushed;
    public uint Seed;
    public CeltSpread Spread;

    // Postfilter encode coefficients (for future encoder support)
    public int PfOctave;
    public int PfPeriod;
    public int PfTapset;
    public float PfGain;

    // Bit allocation
    public int FrameBits;
    public int Remaining;
    public int Remaining2;
    public readonly int[] Caps = new int[CeltDecoder.MaxBands];
    public readonly int[] FineBits = new int[CeltDecoder.MaxBands];
    public readonly int[] FinePriority = new int[CeltDecoder.MaxBands];
    public readonly int[] Pulses = new int[CeltDecoder.MaxBands];
    public readonly int[] TfChange = new int[CeltDecoder.MaxBands];
}

// ────────────────────────────────────────────────────────────────────────────
// CeltDecoder — main CELT frame decode orchestrator
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Complete CELT frame decoder. Orchestrates the full decode pipeline.
/// Port of FFmpeg's ff_celt_decode_frame() (dec_celt.c) and ff_celt_bitalloc/quant_bands (celt.c).
/// </summary>
public sealed class CeltDecoder
{
    public const int ShortBlockSize = 120;
    public const int MaxLogBlocks = 3;
    public const int MaxFrameSize = ShortBlockSize * (1 << MaxLogBlocks); // 960
    public const int MaxBands = 21;
    public const int Overlap = ShortBlockSize;
    public const int Vectors = 11;
    public const int AllocSteps = 6;
    public const int FineOffset = 21;
    public const int MaxFineBits = 8;
    public const int NormScale = 16384;
    public const int QthetaOffset = 4;
    public const int QthetaOffsetTwophase = 16;
    public const int PostfilterMinPeriod = 15;
    public const float EnergySilence = -28.0f;
    public const float EmphCoeff = 0.8500061035f;

    private readonly CeltFrame frame;
    private readonly CeltPvq pvq;
    private readonly CeltImdct[] imdct;

    /// <summary>
    /// Creates a new CELT decoder.
    /// </summary>
    /// <param name="outputChannels">1 or 2.</param>
    /// <param name="applyPhaseInv">Whether to apply phase inversion (default true).</param>
    public CeltDecoder(int outputChannels, bool applyPhaseInv = true)
    {
        frame = new CeltFrame
        {
            OutputChannels = outputChannels,
            ApplyPhaseInv = applyPhaseInv
        };

        pvq = new CeltPvq();

        // Create IMDCT instances for each block size: 120, 240, 480, 960
        imdct = new CeltImdct[4];
        for (int i = 0; i < 4; i++)
            imdct[i] = new CeltImdct(15 << (i + 3)); // 120, 240, 480, 960

        Flush();
    }

    /// <summary>
    /// Decodes a CELT frame from the range coder into float output.
    /// Port of ff_celt_decode_frame().
    /// </summary>
    /// <param name="rc">Range decoder positioned at start of CELT data.</param>
    /// <param name="output">Output buffers per channel (each must be ≥ frameSize).</param>
    /// <param name="channels">Number of coded channels (1 or 2).</param>
    /// <param name="frameSize">Frame size in samples (120, 240, 480, or 960).</param>
    /// <param name="startBand">Start band (0 for CELT-only, 17 for hybrid).</param>
    /// <param name="endBand">End band (13–21 depending on bandwidth).</param>
    /// <returns>True if decode succeeded.</returns>
    public bool DecodeFrame(ref OpusRangeDecoder rc, Span<float[]> output,
                            int channels, int frameSize, int startBand, int endBand)
    {
        var f = frame;
        bool downmix = false;

        if (channels < 1 || channels > 2) return false;
        if (startBand < 0 || startBand > endBand || endBand > MaxBands) return false;

        f.Silence = 0;
        f.Transient = 0;
        f.Anticollapse = 0;
        f.Flushed = 0;
        f.Channels = channels;
        f.StartBand = startBand;
        f.EndBand = endBand;
        f.FrameBits = (int)(rc.RawBytes * 8);

        // Determine block size class (LM: 0=120, 1=240, 2=480, 3=960)
        f.Size = Log2(frameSize / ShortBlockSize);
        if (f.Size > MaxLogBlocks || frameSize != ShortBlockSize * (1 << f.Size))
            return false;

        if (f.OutputChannels == 0)
            f.OutputChannels = channels;

        // Clear coefficient and collapse mask buffers
        for (int i = 0; i < f.Channels; i++)
        {
            Array.Clear(f.Block[i].Coeffs);
            Array.Clear(f.Block[i].CollapseMasks);
        }

        int consumed = (int)rc.Tell();

        // Obtain silence flag
        if (consumed >= f.FrameBits)
            f.Silence = 1;
        else if (consumed == 1)
            f.Silence = (int)rc.DecLog(15);

        if (f.Silence != 0)
        {
            consumed = f.FrameBits;
            rc.TotalBits += (uint)(f.FrameBits - (int)rc.Tell());
        }

        // Parse postfilter
        consumed = ParsePostfilter(f, ref rc, consumed);

        // Obtain transient flag
        if (f.Size != 0 && consumed + 3 <= f.FrameBits)
            f.Transient = (int)rc.DecLog(3);

        f.Blocks = f.Transient != 0 ? 1 << f.Size : 1;
        f.BlockSize = frameSize / f.Blocks;

        // Select IMDCT: transient uses short blocks (size 0), else full size
        int imdctIdx = f.Transient != 0 ? 0 : f.Size;

        // For mono, merge energies from both blocks
        if (channels == 1)
        {
            for (int i = 0; i < MaxBands; i++)
                f.Block[0].Energy[i] = Math.Max(f.Block[0].Energy[i], f.Block[1].Energy[i]);
        }

        // Main decode pipeline
        DecodeCoarseEnergy(f, ref rc);
        DecodeTfChanges(f, ref rc);
        BitAlloc(f, ref rc);
        DecodeFineEnergy(f, ref rc);
        QuantBands(f, ref rc);

        if (f.AnticollaspeNeeded != 0)
            f.Anticollapse = (int)rc.GetRaw(1);

        DecodeFinalEnergy(f, ref rc);

        // Apply anticollapse and denormalize
        for (int i = 0; i < f.Channels; i++)
        {
            CeltBlock block = f.Block[i];
            if (f.Anticollapse != 0)
                ProcessAnticollapse(f, block, block.Coeffs);
            Denormalize(f, block, block.Coeffs);
        }

        // Stereo → mono downmix
        if (f.OutputChannels < f.Channels)
        {
            VectorFmacScalar(f.Block[0].Coeffs, f.Block[1].Coeffs, 1.0f, frameSize);
            downmix = true;
        }
        else if (f.OutputChannels > f.Channels)
        {
            Array.Copy(f.Block[0].Coeffs, f.Block[1].Coeffs, frameSize);
        }

        // Silence: zero out and set energy to silence level
        if (f.Silence != 0)
        {
            for (int i = 0; i < 2; i++)
            {
                CeltBlock block = f.Block[i];
                for (int j = 0; j < MaxBands; j++)
                    block.Energy[j] = EnergySilence;
            }
            Array.Clear(f.Block[0].Coeffs);
            Array.Clear(f.Block[1].Coeffs);
        }

        // Transform and output for each channel
        for (int i = 0; i < f.OutputChannels; i++)
        {
            CeltBlock block = f.Block[i];
            CeltImdct tx = imdct[imdctIdx];

            // IMDCT and overlap-add
            for (int j = 0; j < f.Blocks; j++)
            {
                int dstOffset = 1024 + j * f.BlockSize;
                Span<float> dst = block.Buf.AsSpan(dstOffset);

                // IMDCT: N spectral coefficients → N time-domain samples (half-length)
                // Input stride = f.Blocks (interleaved for transient mode)
                // Output goes to dst + Overlap/2
                int imdctN = tx.Size;
                Span<float> imdctOut = stackalloc float[imdctN];
                ReadOnlySpan<float> imdctIn = new ReadOnlySpan<float>(block.Coeffs, j, block.Coeffs.Length - j);
                tx.Compute(imdctIn, imdctOut, f.Blocks);

                // Copy IMDCT output to buf at dst + Overlap/2
                imdctOut.CopyTo(dst.Slice(Overlap / 2));

                // Overlap-add window
                VectorFmulWindow(dst, dst, dst.Slice(Overlap / 2), CeltTables.WindowPadded.Slice(8), Overlap / 2);
            }

            if (downmix)
                VectorFmulScalar(block.Buf.AsSpan(1024), 0.5f, frameSize);

            // Postfilter
            Postfilter(f, block);

            // Deemphasis
            block.EmphCoeff = Deemphasis(output[i], block.Buf.AsSpan(1024 - frameSize), block.EmphCoeff, frameSize);
        }

        // Copy energy for mono → stereo expansion
        if (channels == 1)
            Array.Copy(f.Block[0].Energy, f.Block[1].Energy, MaxBands);

        // Update prev_energy
        for (int i = 0; i < 2; i++)
        {
            CeltBlock block = f.Block[i];
            if (f.Transient == 0)
            {
                Array.Copy(block.PrevEnergy[0], block.PrevEnergy[1], MaxBands);
                Array.Copy(block.Energy, block.PrevEnergy[0], MaxBands);
            }
            else
            {
                for (int j = 0; j < MaxBands; j++)
                    block.PrevEnergy[0][j] = Math.Min(block.PrevEnergy[0][j], block.Energy[j]);
            }

            for (int j = 0; j < f.StartBand; j++)
            {
                block.PrevEnergy[0][j] = EnergySilence;
                block.Energy[j] = 0.0f;
            }
            for (int j = f.EndBand; j < MaxBands; j++)
            {
                block.PrevEnergy[0][j] = EnergySilence;
                block.Energy[j] = 0.0f;
            }
        }

        f.Seed = rc.Range;

        return true;
    }

    /// <summary>
    /// Flush/reset CELT decoder state. Port of ff_celt_flush().
    /// </summary>
    public void Flush()
    {
        var f = frame;
        if (f.Flushed != 0)
            return;

        for (int i = 0; i < 2; i++)
        {
            CeltBlock block = f.Block[i];
            for (int j = 0; j < MaxBands; j++)
                block.PrevEnergy[0][j] = block.PrevEnergy[1][j] = EnergySilence;

            Array.Clear(block.Energy);
            Array.Clear(block.Buf);
            Array.Clear(block.PfGains);
            Array.Clear(block.PfGainsOld);
            Array.Clear(block.PfGainsNew);

            block.EmphCoeff = 0.0f;
        }
        f.Seed = 0;
        f.Flushed = 1;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Energy decoding (dec_celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode coarse energy using 2D z-transform prediction.
    /// Port of celt_decode_coarse_energy().
    /// </summary>
    private static void DecodeCoarseEnergy(CeltFrame f, ref OpusRangeDecoder rc)
    {
        float[] prev = [0, 0];
        float alpha = CeltTables.AlphaCoef[f.Size];
        float beta = CeltTables.BetaCoef[f.Size];

        // Coarse energy distribution: [lm][intra][band*2]
        // Flat index: lm*84 + intra*42 + i
        int modelBase = f.Size * 84;
        bool intra = false;

        // Check for intra frame
        if (rc.Tell() + 3 <= (uint)f.FrameBits && rc.DecLog(3) != 0)
        {
            alpha = 0.0f;
            beta = 1.0f - 4915.0f / 32768.0f;
            modelBase += 42; // intra=1
            intra = true;
        }

        ReadOnlySpan<byte> model = CeltTables.CoarseEnergyDist.Slice(modelBase);

        for (int i = 0; i < MaxBands; i++)
        {
            for (int j = 0; j < f.Channels; j++)
            {
                CeltBlock block = f.Block[j];

                if (i < f.StartBand || i >= f.EndBand)
                {
                    block.Energy[i] = 0.0f;
                    continue;
                }

                int available = f.FrameBits - (int)rc.Tell();
                float value;

                if (available >= 15)
                {
                    int k = Math.Min(i, 20) << 1;
                    value = rc.DecLaplace((uint)(model[k] << 7), model[k + 1] << 6);
                }
                else if (available >= 2)
                {
                    int x = (int)rc.DecCdf(CeltTables.ModelTapset); // ModelTapset == ModelEnergySmall
                    value = (x >> 1) ^ -(x & 1);
                }
                else if (available >= 1)
                {
                    value = -(float)rc.DecLog(1);
                }
                else
                {
                    value = -1;
                }

                float oldE = block.Energy[i];
                block.Energy[i] = Math.Max(-9.0f, block.Energy[i]) * alpha + prev[j] + value;

                // TEMP DIAG: first 3 frames, ch0 only, first 5 bands
                prev[j] += beta * value;
            }
        }
    }

    /// <summary>Port of celt_decode_fine_energy().</summary>
    private static void DecodeFineEnergy(CeltFrame f, ref OpusRangeDecoder rc)
    {
        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            if (f.FineBits[i] == 0)
                continue;

            for (int j = 0; j < f.Channels; j++)
            {
                int q2 = (int)rc.GetRaw((uint)f.FineBits[i]);
                float offset = (q2 + 0.5f) * (1 << (14 - f.FineBits[i])) / 16384.0f - 0.5f;
                f.Block[j].Energy[i] += offset;
            }
        }
    }

    /// <summary>Port of celt_decode_final_energy().</summary>
    private static void DecodeFinalEnergy(CeltFrame f, ref OpusRangeDecoder rc)
    {
        int bitsLeft = f.FrameBits - (int)rc.Tell();

        for (int priority = 0; priority < 2; priority++)
        {
            for (int i = f.StartBand; i < f.EndBand && bitsLeft >= f.Channels; i++)
            {
                if (f.FinePriority[i] != priority || f.FineBits[i] >= MaxFineBits)
                    continue;

                for (int j = 0; j < f.Channels; j++)
                {
                    int q2 = (int)rc.GetRaw(1);
                    float offset = (q2 - 0.5f) * (1 << (14 - f.FineBits[i] - 1)) / 16384.0f;
                    f.Block[j].Energy[i] += offset;
                    bitsLeft--;
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // TF changes (dec_celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Port of celt_decode_tf_changes().</summary>
    private static void DecodeTfChanges(CeltFrame f, ref OpusRangeDecoder rc)
    {
        int diff = 0, tfSelect = 0, tfChanged = 0;
        int bits = f.Transient != 0 ? 2 : 4;

        int consumed = (int)rc.Tell();
        int tfSelectBit = (f.Size != 0 && consumed + bits + 1 <= f.FrameBits) ? 1 : 0;

        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            if (consumed + bits + tfSelectBit <= f.FrameBits)
            {
                diff ^= (int)rc.DecLog((uint)bits);
                consumed = (int)rc.Tell();
                tfChanged |= diff;
            }
            f.TfChange[i] = diff;
            bits = f.Transient != 0 ? 4 : 5;
        }

        // TfSelect: flat table [lm*8 + isTransient*4 + tfSelect*2 + tfChange]
        ReadOnlySpan<sbyte> tfTable = CeltTables.TfSelect;
        int transientIdx = f.Transient != 0 ? 1 : 0;

        if (tfSelectBit != 0 &&
            tfTable[f.Size * 8 + transientIdx * 4 + 0 * 2 + tfChanged] !=
            tfTable[f.Size * 8 + transientIdx * 4 + 1 * 2 + tfChanged])
        {
            tfSelect = (int)rc.DecLog(1);
        }

        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            f.TfChange[i] = tfTable[f.Size * 8 + transientIdx * 4 + tfSelect * 2 + f.TfChange[i]];
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Denormalize (dec_celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Port of celt_denormalize().</summary>
    private static void Denormalize(CeltFrame f, CeltBlock block, float[] data)
    {
        ReadOnlySpan<byte> freqBands = CeltTables.FreqBands;
        ReadOnlySpan<byte> freqRange = CeltTables.FreqRange;
        ReadOnlySpan<float> meanEnergy = CeltTables.MeanEnergy;

        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            int dstOffset = freqBands[i] << f.Size;
            float logNorm = block.Energy[i] + meanEnergy[i];
            float norm = MathF.Pow(2.0f, Math.Min(logNorm, 32.0f));

            int rangeScaled = freqRange[i] << f.Size;
            for (int j = 0; j < rangeScaled; j++)
                data[dstOffset + j] *= norm;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Postfilter (dec_celt.c + dsp.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Port of parse_postfilter().</summary>
    private static int ParsePostfilter(CeltFrame f, ref OpusRangeDecoder rc, int consumed)
    {
        Array.Clear(f.Block[0].PfGainsNew);
        Array.Clear(f.Block[1].PfGainsNew);

        if (f.StartBand == 0 && consumed + 16 <= f.FrameBits)
        {
            int hasPostfilter = (int)rc.DecLog(1);
            if (hasPostfilter != 0)
            {
                int octave = (int)rc.DecUint(6);
                int period = (16 << octave) + (int)rc.GetRaw((uint)(4 + octave)) - 1;
                float gain = 0.09375f * ((int)rc.GetRaw(3) + 1);
                int tapset = ((int)rc.Tell() + 2 <= f.FrameBits)
                    ? (int)rc.DecCdf(CeltTables.ModelTapset) : 0;

                ReadOnlySpan<float> pfTaps = CeltTables.PostfilterTaps;

                for (int i = 0; i < 2; i++)
                {
                    CeltBlock block = f.Block[i];
                    block.PfPeriodNew = Math.Max(period, PostfilterMinPeriod);
                    block.PfGainsNew[0] = gain * pfTaps[tapset * 3 + 0];
                    block.PfGainsNew[1] = gain * pfTaps[tapset * 3 + 1];
                    block.PfGainsNew[2] = gain * pfTaps[tapset * 3 + 2];
                }
            }

            consumed = (int)rc.Tell();
        }

        return consumed;
    }

    /// <summary>
    /// Apply postfilter transition (overlap region).
    /// Port of celt_postfilter_apply_transition().
    /// Uses array+offset to allow negative indexing into history.
    /// </summary>
    private static void PostfilterApplyTransition(CeltBlock block, float[] buf, int dataOffset)
    {
        int T0 = block.PfPeriodOld;
        int T1 = block.PfPeriod;

        if (block.PfGains[0] == 0.0f && block.PfGainsOld[0] == 0.0f)
            return;

        float g00 = block.PfGainsOld[0];
        float g01 = block.PfGainsOld[1];
        float g02 = block.PfGainsOld[2];
        float g10 = block.PfGains[0];
        float g11 = block.PfGains[1];
        float g12 = block.PfGains[2];

        float x1 = buf[dataOffset - T1 + 1];
        float x2 = buf[dataOffset - T1];
        float x3 = buf[dataOffset - T1 - 1];
        float x4 = buf[dataOffset - T1 - 2];

        ReadOnlySpan<float> window2 = CeltTables.Window2;

        for (int i = 0; i < Overlap; i++)
        {
            float w = window2[i];
            float x0 = buf[dataOffset + i - T1 + 2];

            buf[dataOffset + i] += (1.0f - w) * g00 * buf[dataOffset + i - T0] +
                       (1.0f - w) * g01 * (buf[dataOffset + i - T0 - 1] + buf[dataOffset + i - T0 + 1]) +
                       (1.0f - w) * g02 * (buf[dataOffset + i - T0 - 2] + buf[dataOffset + i - T0 + 2]) +
                       w * g10 * x2 +
                       w * g11 * (x1 + x3) +
                       w * g12 * (x0 + x4);
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }
    }

    /// <summary>Port of celt_postfilter().</summary>
    private static void Postfilter(CeltFrame f, CeltBlock block)
    {
        int len = f.BlockSize * f.Blocks;
        int filterLen = len - 2 * Overlap;

        PostfilterApplyTransition(block, block.Buf, 1024);

        block.PfPeriodOld = block.PfPeriod;
        Array.Copy(block.PfGains, block.PfGainsOld, 3);

        block.PfPeriod = block.PfPeriodNew;
        Array.Copy(block.PfGainsNew, block.PfGains, 3);

        if (len > Overlap)
        {
            PostfilterApplyTransition(block, block.Buf, 1024 + Overlap);

            if (block.PfGains[0] > float.Epsilon && filterLen > 0)
                PostfilterSteadyState(block.Buf, 1024 + 2 * Overlap,
                                      block.PfPeriod, block.PfGains, filterLen);

            block.PfPeriodOld = block.PfPeriod;
            Array.Copy(block.PfGains, block.PfGainsOld, 3);
        }

        // memmove buf forward: keep 1024 + Overlap/2 samples of history
        Array.Copy(block.Buf, len, block.Buf, 0, 1024 + Overlap / 2);
    }

    /// <summary>Postfilter steady-state. Port of postfilter_c() from dsp.c.</summary>
    private static void PostfilterSteadyState(float[] buf, int dataOffset, int period, float[] gains, int len)
    {
        float g0 = gains[0], g1 = gains[1], g2 = gains[2];
        float x4 = buf[dataOffset - period - 2];
        float x3 = buf[dataOffset - period - 1];
        float x2 = buf[dataOffset - period];
        float x1 = buf[dataOffset - period + 1];

        for (int i = 0; i < len; i++)
        {
            float x0 = buf[dataOffset + i - period + 2];
            buf[dataOffset + i] += g0 * x2 + g1 * (x1 + x3) + g2 * (x0 + x4);
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Anticollapse (dec_celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Port of process_anticollapse().</summary>
    private static void ProcessAnticollapse(CeltFrame f, CeltBlock block, float[] X)
    {
        ReadOnlySpan<byte> freqBands = CeltTables.FreqBands;
        ReadOnlySpan<byte> freqRange = CeltTables.FreqRange;

        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            int depth = (1 + f.Pulses[i]) / (freqRange[i] << f.Size);
            float thresh = MathF.Pow(2.0f, -1.0f - 0.125f * depth);
            float sqrt1 = 1.0f / MathF.Sqrt(freqRange[i] << f.Size);

            int xOffset = freqBands[i] << f.Size;
            bool renormalize = false;

            float prev0 = block.PrevEnergy[0][i];
            float prev1 = block.PrevEnergy[1][i];
            if (f.Channels == 1)
            {
                CeltBlock block1 = f.Block[1];
                prev0 = Math.Max(prev0, block1.PrevEnergy[0][i]);
                prev1 = Math.Max(prev1, block1.PrevEnergy[1][i]);
            }
            float Ediff = Math.Max(0, block.Energy[i] - Math.Min(prev0, prev1));

            float r = MathF.Pow(2.0f, 1 - Ediff);
            if (f.Size == 3)
                r *= MathF.Sqrt(2.0f);
            r = Math.Min(thresh, r) * sqrt1;

            for (int k = 0; k < 1 << f.Size; k++)
            {
                if ((block.CollapseMasks[i] & (1 << k)) == 0)
                {
                    for (int j = 0; j < freqRange[i]; j++)
                        X[xOffset + (j << f.Size) + k] = (CeltRng(f) & 0x8000) != 0 ? r : -r;
                    renormalize = true;
                }
            }

            if (renormalize)
                RenormalizeVector(X.AsSpan(xOffset), freqRange[i] << f.Size, 1.0f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Deemphasis (dsp.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// First-order IIR deemphasis filter. Port of deemphasis_c() from dsp.c.
    /// Returns updated coefficient.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Deemphasis(float[] output, Span<float> input, float coeff, int len)
    {
        for (int i = 0; i < len; i++)
            coeff = output[i] = input[i] + coeff * EmphCoeff;
        return coeff;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Bit allocation (celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CELT bit allocation algorithm. Port of ff_celt_bitalloc() (decode path).
    /// </summary>
    private static void BitAlloc(CeltFrame f, ref OpusRangeDecoder rc)
    {
        int skipStartBand = f.StartBand;
        int skipBit = 0;
        int intensityStereoBit = 0;
        int dualStereoBit = 0;
        int dynalloc = 6;
        int extrabits = 0;

        int[] boost = new int[MaxBands];
        int[] trimOffset = new int[MaxBands];
        int[] threshold = new int[MaxBands];
        int[] bits1 = new int[MaxBands];
        int[] bits2 = new int[MaxBands];

        ReadOnlySpan<byte> freqRange = CeltTables.FreqRange;
        ReadOnlySpan<byte> freqBands = CeltTables.FreqBands;
        ReadOnlySpan<byte> logFreqRange = CeltTables.LogFreqRange;

        // NORMC macro: (bits) << (channels-1) << size >> 2
        int NormC(int bits) => bits << (f.Channels - 1) << f.Size >> 2;

        // Spread
        if (rc.Tell() + 4 <= (uint)f.FrameBits)
            f.Spread = (CeltSpread)rc.DecCdf(CeltTables.ModelSpread);
        else
            f.Spread = CeltSpread.Normal;

        // Initialize static allocation caps
        for (int i = 0; i < MaxBands; i++)
        {
            // StaticCaps: flat[lm*42 + (channels-1)*21 + band]
            int capIdx = f.Size * 42 + (f.Channels - 1) * 21 + i;
            f.Caps[i] = NormC((CeltTables.StaticCaps[capIdx] + 64) * freqRange[i]);
        }

        // Band boosts
        int tbits8ths = f.FrameBits << 3;
        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            int quanta = freqRange[i] << (f.Channels - 1) << f.Size;
            int bDynalloc = dynalloc;
            quanta = Math.Min(quanta << 3, Math.Max(6 << 3, quanta));

            while (rc.TellFrac() + (uint)(bDynalloc << 3) < (uint)tbits8ths && boost[i] < f.Caps[i])
            {
                int isBoost = (int)rc.DecLog((uint)bDynalloc);
                if (isBoost == 0)
                    break;

                boost[i] += quanta;
                tbits8ths -= quanta;
                bDynalloc = 1;
            }

            if (boost[i] != 0)
                dynalloc = Math.Max(dynalloc - 1, 2);
        }

        // Allocation trim
        f.AllocTrim = 5;
        if (rc.TellFrac() + (6u << 3) <= (uint)tbits8ths)
            f.AllocTrim = (int)rc.DecCdf(CeltTables.ModelAllocTrim);

        // Anti-collapse bit reservation
        tbits8ths = (f.FrameBits << 3) - (int)rc.TellFrac() - 1;
        f.AnticollaspeNeeded = 0;
        if (f.Transient != 0 && f.Size >= 2 && tbits8ths >= (f.Size + 2) << 3)
            f.AnticollaspeNeeded = 1 << 3;
        tbits8ths -= f.AnticollaspeNeeded;

        // Skip bit reservation
        if (tbits8ths >= 1 << 3)
            skipBit = 1 << 3;
        tbits8ths -= skipBit;

        // Intensity/dual stereo bit reservation
        if (f.Channels == 2)
        {
            intensityStereoBit = CeltTables.Log2Frac[f.EndBand - f.StartBand];
            if (intensityStereoBit <= tbits8ths)
            {
                tbits8ths -= intensityStereoBit;
                if (tbits8ths >= 1 << 3)
                {
                    dualStereoBit = 1 << 3;
                    tbits8ths -= 1 << 3;
                }
            }
            else
            {
                intensityStereoBit = 0;
            }
        }

        // Trim offsets
        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            int trim = f.AllocTrim - 5 - f.Size;
            int band = freqRange[i] * (f.EndBand - i - 1);
            int duration = f.Size + 3;
            int scale = duration + f.Channels - 1;

            threshold[i] = Math.Max(3 * freqRange[i] << duration >> 4, f.Channels << 3);
            trimOffset[i] = trim * (band << scale) >> 6;

            if ((freqRange[i] << f.Size) == 1)
                trimOffset[i] -= f.Channels << 3;
        }

        // First bisection
        int low = 1, high = Vectors - 1;
        while (low <= high)
        {
            int center = (low + high) >> 1;
            int done = 0, total = 0;

            for (int i = f.EndBand - 1; i >= f.StartBand; i--)
            {
                // StaticAlloc: flat[allocIndex*21 + band]
                int bandbits = NormC(freqRange[i] * CeltTables.StaticAlloc[center * 21 + i]);
                if (bandbits != 0)
                    bandbits = Math.Max(bandbits + trimOffset[i], 0);
                bandbits += boost[i];

                if (bandbits >= threshold[i] || done != 0)
                {
                    done = 1;
                    total += Math.Min(bandbits, f.Caps[i]);
                }
                else if (bandbits >= f.Channels << 3)
                {
                    total += f.Channels << 3;
                }
            }

            if (total > tbits8ths)
                high = center - 1;
            else
                low = center + 1;
        }
        high = low--;

        // Compute bits1 and bits2
        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            bits1[i] = NormC(freqRange[i] * CeltTables.StaticAlloc[low * 21 + i]);
            bits2[i] = high >= Vectors ? f.Caps[i]
                : NormC(freqRange[i] * CeltTables.StaticAlloc[high * 21 + i]);

            if (bits1[i] != 0)
                bits1[i] = Math.Max(bits1[i] + trimOffset[i], 0);
            if (bits2[i] != 0)
                bits2[i] = Math.Max(bits2[i] + trimOffset[i], 0);

            if (low != 0)
                bits1[i] += boost[i];
            bits2[i] += boost[i];

            if (boost[i] != 0)
                skipStartBand = i;
            bits2[i] = Math.Max(bits2[i] - bits1[i], 0);
        }

        // Second bisection
        low = 0;
        high = 1 << AllocSteps;
        for (int step = 0; step < AllocSteps; step++)
        {
            int center = (low + high) >> 1;
            int done = 0, total = 0;

            for (int j = f.EndBand - 1; j >= f.StartBand; j--)
            {
                int bandbits = bits1[j] + (center * bits2[j] >> AllocSteps);
                if (bandbits >= threshold[j] || done != 0)
                {
                    done = 1;
                    total += Math.Min(bandbits, f.Caps[j]);
                }
                else if (bandbits >= f.Channels << 3)
                {
                    total += f.Channels << 3;
                }
            }
            if (total > tbits8ths)
                high = center;
            else
                low = center;
        }

        // Final allocation
        {
            int done = 0, total = 0;
            for (int i = f.EndBand - 1; i >= f.StartBand; i--)
            {
                int bandbits = bits1[i] + (low * bits2[i] >> AllocSteps);
                if (bandbits >= threshold[i] || done != 0)
                    done = 1;
                else
                    bandbits = (bandbits >= f.Channels << 3) ? f.Channels << 3 : 0;

                bandbits = Math.Min(bandbits, f.Caps[i]);
                f.Pulses[i] = bandbits;
                total += bandbits;
            }

            // Band skipping
            for (f.CodedBands = f.EndBand; ; f.CodedBands--)
            {
                int j = f.CodedBands - 1;
                if (j == skipStartBand)
                {
                    tbits8ths += skipBit;
                    break;
                }

                int bandRemaining = tbits8ths - total;
                int bandbits = bandRemaining / (freqBands[j + 1] - freqBands[f.StartBand]);
                bandRemaining -= bandbits * (freqBands[j + 1] - freqBands[f.StartBand]);
                int allocation = f.Pulses[j] + bandbits * freqRange[j];
                allocation += Math.Max(bandRemaining - (freqBands[j] - freqBands[f.StartBand]), 0);

                if (allocation >= Math.Max(threshold[j], (f.Channels + 1) << 3))
                {
                    int doNotSkip = (int)rc.DecLog(1);
                    if (doNotSkip != 0)
                        break;

                    total += 1 << 3;
                    allocation -= 1 << 3;
                }

                total -= f.Pulses[j];
                if (intensityStereoBit != 0)
                {
                    total -= intensityStereoBit;
                    intensityStereoBit = CeltTables.Log2Frac[j - f.StartBand];
                    total += intensityStereoBit;
                }

                total += f.Pulses[j] = (allocation >= f.Channels << 3) ? f.Channels << 3 : 0;
            }

            // Intensity stereo start band
            f.IntensityStereo = 0;
            f.DualStereo = 0;
            if (intensityStereoBit != 0)
                f.IntensityStereo = f.StartBand + (int)rc.DecUint((uint)(f.CodedBands + 1 - f.StartBand));

            // Dual stereo flag
            if (f.IntensityStereo <= f.StartBand)
                tbits8ths += dualStereoBit;
            else if (dualStereoBit != 0)
                f.DualStereo = (int)rc.DecLog(1);

            // Distribute remaining bits
            int remaining = tbits8ths - total;
            int perBand = remaining / (freqBands[f.CodedBands] - freqBands[f.StartBand]);
            remaining -= perBand * (freqBands[f.CodedBands] - freqBands[f.StartBand]);
            for (int i = f.StartBand; i < f.CodedBands; i++)
            {
                int bitsExtra = Math.Min(remaining, freqRange[i]);
                f.Pulses[i] += bitsExtra + perBand * freqRange[i];
                remaining -= bitsExtra;
            }

            // Final allocation: determine fine_bits, fine_priority, pulses
            for (int i = f.StartBand; i < f.CodedBands; i++)
            {
                int N = freqRange[i] << f.Size;
                int prevExtra = extrabits;
                f.Pulses[i] += extrabits;

                if (N > 1)
                {
                    extrabits = Math.Max(f.Pulses[i] - f.Caps[i], 0);
                    f.Pulses[i] -= extrabits;

                    int dof = N * f.Channels +
                        (f.Channels == 2 && N > 2 && f.DualStereo == 0 && i < f.IntensityStereo ? 1 : 0);
                    int temp = dof * (logFreqRange[i] + (f.Size << 3));
                    int fineBitsOffset = (temp >> 1) - dof * FineOffset;
                    if (N == 2)
                        fineBitsOffset += dof << 1;

                    if (f.Pulses[i] + fineBitsOffset < 2 * (dof << 3))
                        fineBitsOffset += temp >> 2;
                    else if (f.Pulses[i] + fineBitsOffset < 3 * (dof << 3))
                        fineBitsOffset += temp >> 3;

                    int fineBitsCalc = (f.Pulses[i] + fineBitsOffset + (dof << 2)) / (dof << 3);
                    int maxBits = Math.Max(Math.Min((f.Pulses[i] >> 3) >> (f.Channels - 1), MaxFineBits), 0);
                    f.FineBits[i] = Math.Clamp(fineBitsCalc, 0, maxBits);
                    f.FinePriority[i] = (f.FineBits[i] * (dof << 3) >= f.Pulses[i] + fineBitsOffset) ? 1 : 0;
                    f.Pulses[i] -= f.FineBits[i] << (f.Channels - 1) << 3;
                }
                else
                {
                    extrabits = Math.Max(f.Pulses[i] - (f.Channels << 3), 0);
                    f.Pulses[i] -= extrabits;
                    f.FineBits[i] = 0;
                    f.FinePriority[i] = 1;
                }

                if (extrabits > 0)
                {
                    int fineextra = Math.Min(extrabits >> (f.Channels + 2), MaxFineBits - f.FineBits[i]);
                    f.FineBits[i] += fineextra;
                    fineextra <<= f.Channels + 2;
                    f.FinePriority[i] = (fineextra >= extrabits - prevExtra) ? 1 : 0;
                    extrabits -= fineextra;
                }
            }
            f.Remaining = extrabits;

            // Skipped bands: all bits for fine energy
            for (int i = f.CodedBands; i < f.EndBand; i++)
            {
                f.FineBits[i] = f.Pulses[i] >> (f.Channels - 1) >> 3;
                f.Pulses[i] = 0;
                f.FinePriority[i] = f.FineBits[i] < 1 ? 1 : 0;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Quant bands (celt.c)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode PVQ-quantized spectral bands.
    /// Port of ff_celt_quant_bands() (decode path).
    /// </summary>
    private void QuantBands(CeltFrame f, ref OpusRangeDecoder rc)
    {
        float[] lowbandScratch = new float[8 * 22];
        float[] norm1 = new float[2 * 8 * 100];
        float[] norm2Buf = norm1; // norm2 starts at offset 8*100
        int norm2Offset = 8 * 100;

        int totalBits = (f.FrameBits << 3) - f.AnticollaspeNeeded;
        int updateLowband = 1;
        int lowbandOffset = 0;

        ReadOnlySpan<byte> freqBands = CeltTables.FreqBands;
        ReadOnlySpan<byte> freqRange = CeltTables.FreqRange;

        for (int i = f.StartBand; i < f.EndBand; i++)
        {
            uint[] cm = [(1u << f.Blocks) - 1, (1u << f.Blocks) - 1];
            int bandOffset = freqBands[i] << f.Size;
            int bandSize = freqRange[i] << f.Size;

            int consumed = (int)rc.TellFrac();
            int effectiveLowband = -1;
            int b = 0;

            if (i != f.StartBand)
                f.Remaining -= consumed;
            f.Remaining2 = totalBits - consumed - 1;
            if (i <= f.CodedBands - 1)
            {
                int currBalance = f.Remaining / Math.Min(3, f.CodedBands - i);
                b = Math.Clamp(Math.Min(f.Remaining2 + 1, f.Pulses[i] + currBalance), 0, 16383);
            }

            if ((freqBands[i] - freqRange[i] >= freqBands[f.StartBand] || i == f.StartBand + 1) &&
                (updateLowband != 0 || lowbandOffset == 0))
                lowbandOffset = i;

            if (i == f.StartBand + 1)
            {
                int count = (freqRange[i] - freqRange[i - 1]) << f.Size;
                Array.Copy(norm1, bandOffset - count, norm1, bandOffset, count);
                if (f.Channels == 2)
                    Array.Copy(norm2Buf, norm2Offset + bandOffset - count, norm2Buf, norm2Offset + bandOffset, count);
            }

            // Get folding collapse masks
            if (lowbandOffset != 0 && (f.Spread != CeltSpread.Aggressive || f.Blocks > 1 || f.TfChange[i] < 0))
            {
                effectiveLowband = Math.Max(freqBands[f.StartBand],
                    freqBands[lowbandOffset] - freqRange[i]);
                int foldstart = lowbandOffset;
                while (freqBands[--foldstart] > effectiveLowband) ;
                int foldend = lowbandOffset - 1;
                while (++foldend < i && freqBands[foldend] < effectiveLowband + freqRange[i]) ;

                cm[0] = cm[1] = 0;
                for (int j = foldstart; j < foldend; j++)
                {
                    cm[0] |= f.Block[0].CollapseMasks[j];
                    cm[1] |= f.Block[f.Channels - 1].CollapseMasks[j];
                }
            }

            if (f.DualStereo != 0 && i == f.IntensityStereo)
            {
                f.DualStereo = 0;
                for (int j = freqBands[f.StartBand] << f.Size; j < bandOffset; j++)
                    norm1[j] = (norm1[j] + norm2Buf[norm2Offset + j]) / 2;
            }

            Span<float> normLoc1 = effectiveLowband != -1
                ? norm1.AsSpan(effectiveLowband << f.Size)
                : Span<float>.Empty;
            Span<float> normLoc2 = effectiveLowband != -1
                ? norm2Buf.AsSpan(norm2Offset + (effectiveLowband << f.Size))
                : Span<float>.Empty;

            Span<float> X = f.Block[0].Coeffs.AsSpan(bandOffset, bandSize);
            Span<float> Y = (f.Channels == 2)
                ? f.Block[1].Coeffs.AsSpan(bandOffset, bandSize)
                : Span<float>.Empty;

            if (f.DualStereo != 0)
            {
                cm[0] = pvq.QuantBand(ref f, ref rc, i, X, Span<float>.Empty, bandSize, b >> 1,
                    f.Blocks, normLoc1, f.Size,
                    norm1.AsSpan(bandOffset), 0, 1.0f,
                    lowbandScratch, (int)cm[0]);

                cm[1] = pvq.QuantBand(ref f, ref rc, i, Y, Span<float>.Empty, bandSize, b >> 1,
                    f.Blocks, normLoc2, f.Size,
                    norm2Buf.AsSpan(norm2Offset + bandOffset), 0, 1.0f,
                    lowbandScratch, (int)cm[1]);
            }
            else
            {
                cm[0] = pvq.QuantBand(ref f, ref rc, i, X, Y, bandSize, b >> 0,
                    f.Blocks, normLoc1, f.Size,
                    norm1.AsSpan(bandOffset), 0, 1.0f,
                    lowbandScratch, (int)(cm[0] | cm[1]));
                cm[1] = cm[0];
            }

            f.Block[0].CollapseMasks[i] = (byte)cm[0];
            f.Block[f.Channels - 1].CollapseMasks[i] = (byte)cm[1];
            f.Remaining += f.Pulses[i] + consumed;

            updateLowband = (b > bandSize << 3) ? 1 : 0;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Vector helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>dst += src * scalar</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorFmacScalar(float[] dst, float[] src, float scalar, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] += src[i] * scalar;
    }

    /// <summary>data *= scalar</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorFmulScalar(Span<float> data, float scalar, int len)
    {
        for (int i = 0; i < len; i++)
            data[i] *= scalar;
    }

    /// <summary>
    /// Windowed overlap-add. Port of vector_fmul_window_c().
    /// dst[i] = src0[i]*win[len-1-i] - src1[len-1-i]*win[i]  (first half)
    /// dst[len+j] = src0[len]*win[j] + src1[j]*win[len-1-j]   (second half)
    /// </summary>
    private static void VectorFmulWindow(Span<float> dst, ReadOnlySpan<float> src0,
                                          ReadOnlySpan<float> src1, ReadOnlySpan<float> win, int len)
    {
        // Port of vector_fmul_window_c from float_dsp.c
        // When src1 overlaps with the output upper half (dst[len..2*len-1]),
        // we must process from outside-in to avoid overwriting src1 values.
        // FFmpeg achieves this by advancing pointers and using negative indexing.
        for (int i = -len, j = len - 1; i < 0; i++, j--)
        {
            float s0 = src0[len + i];
            float s1 = src1[j];
            float wi = win[len + i];
            float wj = win[len + j];
            dst[len + i] = s0 * wj - s1 * wi;
            dst[len + j] = s0 * wi + s1 * wj;
        }
    }

    /// <summary>LCG for noise generation. Port of celt_rng().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CeltRng(CeltFrame f)
    {
        f.Seed = 1664525 * f.Seed + 1013904223;
        return f.Seed;
    }

    /// <summary>Renormalize vector to given gain.</summary>
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

    /// <summary>Integer log2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(int x)
    {
        if (x <= 0) return 0;
        return BitOperations.Log2((uint)x);
    }
}

using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// High frequency generation (14496-3 sp04 p215): builds the upper spectrum by copying patches of
/// the decoded low band upward and running each through a short prediction filter.
/// </summary>
/// <remarks>
/// The copy alone would sound obviously artificial, so each patched subband is filtered by a
/// two-tap predictor derived from the source band's own autocorrelation. How strongly that filter
/// is applied is controlled per noise band by a chirp factor, which the bitstream steers through
/// its inverse filtering modes and which is smoothed across frames so the character does not jump.
/// </remarks>
internal sealed class SbrHighFrequency
{
    /// <summary>Slots carried into the buffers before the current frame's own (14496-3: t_HFGen).</summary>
    public const int LeadSlots = 8;

    /// <summary>Slots per frame in the QMF domain.</summary>
    public const int FrameSlots = 32;

    /// <summary>Total slots the low and high band buffers hold: lead-in plus a frame.</summary>
    public const int BufferSlots = 40;

    /// <summary>
    /// Offset applied when reading into the adjustment stage, so the two-tap predictor always has
    /// the two slots of history it needs.
    /// </summary>
    public const int EnvelopeAdjustmentOffset = 2;

    /// <summary>Decoded low band in the QMF domain: [subband][slot][re/im].</summary>
    public readonly float[][][] LowBand = Allocate(32, BufferSlots);

    /// <summary>Generated high band: [subband][slot][re/im].</summary>
    public readonly float[][][] HighBand = Allocate(64, BufferSlots);

    private readonly float[][] alpha0 = AllocateComplex(32);
    private readonly float[][] alpha1 = AllocateComplex(32);

    /// <summary>Prediction coefficients per source subband, exposed so they can be checked.</summary>
    public float[][] PredictorAlpha0 => alpha0;
    public float[][] PredictorAlpha1 => alpha1;
    private readonly float[] phi = new float[3 * 2 * 2];

    private static float[][][] Allocate(int bands, int slots)
    {
        var result = new float[bands][][];
        for (int b = 0; b < bands; b++)
        {
            result[b] = new float[slots][];
            for (int s = 0; s < slots; s++)
                result[b][s] = new float[2];
        }
        return result;
    }

    private static float[][] AllocateComplex(int count)
    {
        var result = new float[count][];
        for (int i = 0; i < count; i++)
            result[i] = new float[2];
        return result;
    }

    public void Reset()
    {
        foreach (var band in LowBand)
            foreach (var slot in band)
                Array.Clear(slot);
        foreach (var band in HighBand)
            foreach (var slot in band)
                Array.Clear(slot);
        foreach (var a in alpha0) Array.Clear(a);
        foreach (var a in alpha1) Array.Clear(a);
    }

    /// <summary>
    /// Fills the low band from the analysis output (14496-3 sp04 p212). The first
    /// <see cref="LeadSlots"/> slots come from the previous frame's analysis, so the predictor and
    /// the envelope adjustment both have history to work with across the frame boundary.
    /// </summary>
    /// <param name="current">This frame's analysis output, [slot][band][re/im].</param>
    /// <param name="previous">The previous frame's analysis output.</param>
    /// <param name="startSubband">kx: the first subband SBR reconstructs.</param>
    /// <param name="previousStartSubband">kx from the previous frame, for the carried-over slots.</param>
    public void GenerateLowBand(float[][][] current, float[][][] previous,
                                int startSubband, int previousStartSubband)
    {
        foreach (var band in LowBand)
            foreach (var slot in band)
                Array.Clear(slot);

        for (int k = 0; k < startSubband && k < 32; k++)
        {
            for (int i = LeadSlots; i < FrameSlots + LeadSlots; i++)
            {
                LowBand[k][i][0] = current[i - LeadSlots][k][0];
                LowBand[k][i][1] = current[i - LeadSlots][k][1];
            }
        }

        for (int k = 0; k < previousStartSubband && k < 32; k++)
        {
            for (int i = 0; i < LeadSlots; i++)
            {
                LowBand[k][i][0] = previous[i + FrameSlots - LeadSlots][k][0];
                LowBand[k][i][1] = previous[i + FrameSlots - LeadSlots][k][1];
            }
        }
    }

    /// <summary>
    /// Updates the per-noise-band chirp factors (14496-3 sp04 p214). The new value is blended with
    /// the previous frame's so the amount of prediction changes gradually; a mode that disagrees
    /// with the previous frame's is treated as an intermediate setting rather than either extreme.
    /// </summary>
    public static void UpdateChirpFactors(SbrChannelData channel, float[] chirp, int noiseBandCount)
    {
        ReadOnlySpan<float> byMode = [0.0f, 0.75f, 0.9f, 0.98f];

        for (int i = 0; i < noiseBandCount; i++)
        {
            float target = channel.InverseFilterMode[0][i] + channel.InverseFilterMode[1][i] == 1
                ? 0.6f
                : byMode[channel.InverseFilterMode[0][i]];

            target = target < chirp[i]
                ? 0.75f * target + 0.25f * chirp[i]
                : 0.90625f * target + 0.09375f * chirp[i];

            chirp[i] = target < 0.015625f ? 0.0f : target;
        }
    }

    /// <summary>
    /// Derives the two-tap prediction coefficients for every source subband (14496-3 sp04 p214).
    /// </summary>
    public void ComputeInverseFilter(int sourceBandCount)
    {
        for (int k = 0; k < sourceBandCount && k < 32; k++)
        {
            Autocorrelate(LowBand[k]);

            // phi is indexed [lag][row][re/im], flattened.
            float phi21Re = phi[Index(2, 1, 0)];
            float phi10Re = phi[Index(1, 0, 0)];
            float phi11Re = phi[Index(1, 1, 0)];
            float phi11Im = phi[Index(1, 1, 1)];
            float phi00Re = phi[Index(0, 0, 0)];
            float phi00Im = phi[Index(0, 0, 1)];
            float phi01Re = phi[Index(0, 1, 0)];
            float phi01Im = phi[Index(0, 1, 1)];

            // The 1.000001 divisor keeps the determinant from reaching zero on a perfectly
            // predictable band, which would otherwise produce an unbounded filter.
            float determinant = phi21Re * phi10Re -
                                (phi11Re * phi11Re + phi11Im * phi11Im) / 1.000001f;

            if (determinant == 0)
            {
                alpha1[k][0] = 0;
                alpha1[k][1] = 0;
            }
            else
            {
                float real = phi00Re * phi11Re - phi00Im * phi11Im - phi01Re * phi10Re;
                float imaginary = phi00Re * phi11Im + phi00Im * phi11Re - phi01Im * phi10Re;
                alpha1[k][0] = real / determinant;
                alpha1[k][1] = imaginary / determinant;
            }

            if (phi10Re == 0)
            {
                alpha0[k][0] = 0;
                alpha0[k][1] = 0;
            }
            else
            {
                float real = phi00Re + alpha1[k][0] * phi11Re + alpha1[k][1] * phi11Im;
                float imaginary = phi00Im + alpha1[k][1] * phi11Re - alpha1[k][0] * phi11Im;
                alpha0[k][0] = -real / phi10Re;
                alpha0[k][1] = -imaginary / phi10Re;
            }

            // An unstable filter is discarded outright rather than clamped: the spec caps the
            // magnitude at 4, and a band that exceeds it is better left unfiltered than ringing.
            if (alpha1[k][0] * alpha1[k][0] + alpha1[k][1] * alpha1[k][1] >= 16.0f ||
                alpha0[k][0] * alpha0[k][0] + alpha0[k][1] * alpha0[k][1] >= 16.0f)
            {
                alpha0[k][0] = 0;
                alpha0[k][1] = 0;
                alpha1[k][0] = 0;
                alpha1[k][1] = 0;
            }
        }
    }

    private static int Index(int lag, int row, int part) => (lag * 2 + row) * 2 + part;

    /// <summary>
    /// Covariance of one subband at lags 0, 1 and 2, over the slots the predictor will use.
    /// </summary>
    private void Autocorrelate(float[][] band)
    {
        // float, not double: sbr_autocorrelate_c accumulates in float, and these sums feed
        // sbr_hf_inverse_filter, which FFmpeg's own comment calls not numerically stable. A more
        // precise accumulation here is a different accumulation, and the difference is amplified
        // rather than damped.
        float realSum2 = band[0][0] * band[2][0] + band[0][1] * band[2][1];
        float imagSum2 = band[0][0] * band[2][1] - band[0][1] * band[2][0];
        float realSum1 = 0, imagSum1 = 0, realSum0 = 0;

        for (int i = 1; i < 38; i++)
        {
            realSum0 += band[i][0] * band[i][0] + band[i][1] * band[i][1];
            realSum1 += band[i][0] * band[i + 1][0] + band[i][1] * band[i + 1][1];
            imagSum1 += band[i][0] * band[i + 1][1] - band[i][1] * band[i + 1][0];
            realSum2 += band[i][0] * band[i + 2][0] + band[i][1] * band[i + 2][1];
            imagSum2 += band[i][0] * band[i + 2][1] - band[i][1] * band[i + 2][0];
        }

        phi[Index(0, 1, 0)] = realSum2;
        phi[Index(0, 1, 1)] = imagSum2;
        phi[Index(2, 1, 0)] = realSum0 + band[0][0] * band[0][0] + band[0][1] * band[0][1];
        phi[Index(1, 0, 0)] = realSum0 + band[38][0] * band[38][0] + band[38][1] * band[38][1];
        phi[Index(1, 1, 0)] = realSum1 + band[0][0] * band[1][0] + band[0][1] * band[1][1];
        phi[Index(1, 1, 1)] = imagSum1 + band[0][0] * band[1][1] - band[0][1] * band[1][0];
        phi[Index(0, 0, 0)] = realSum1 + band[38][0] * band[39][0] + band[38][1] * band[39][1];
        phi[Index(0, 0, 1)] = imagSum1 + band[38][0] * band[39][1] - band[38][1] * band[39][0];
    }

    /// <summary>
    /// Builds the high band by patching the low band upward and applying the prediction filter
    /// (14496-3 sp04 p215).
    /// </summary>
    /// <returns>False when no noise band covers a subband, which means the tables are inconsistent.</returns>
    public bool Generate(SbrContext sbr, SbrTables tables, float[] chirp,
                         int firstBorder, int lastBorder)
    {
        int k = tables.StartSubband;
        int noiseBand = 0;

        for (int patch = 0; patch < tables.PatchCount; patch++)
        {
            for (int x = 0; x < tables.PatchSubbandCount[patch]; x++, k++)
            {
                int source = tables.PatchStartSubband[patch] + x;
                if (source < 0 || source >= 32 || k >= 64)
                    return false;

                while (noiseBand <= sbr.NoiseBandCount && k >= tables.Noise[noiseBand])
                    noiseBand++;
                noiseBand--;

                if (noiseBand < 0)
                    return false;

                Predict(HighBand[k], LowBand[source], alpha0[source], alpha1[source],
                        chirp[noiseBand], 2 * firstBorder, 2 * lastBorder);
            }
        }

        // Any subband the patches did not reach contributes nothing.
        for (int band = k; band < tables.StartSubband + tables.SubbandCount && band < 64; band++)
        {
            foreach (var slot in HighBand[band])
                Array.Clear(slot);
        }

        return true;
    }

    /// <summary>
    /// One patched subband: the copied signal plus a two-tap prediction of it, the predictor
    /// weakened by the chirp factor.
    /// </summary>
    private static void Predict(float[][] high, float[][] low, float[] a0, float[] a1,
                                float chirp, int start, int end)
    {
        float alpha0Real = a1[0] * chirp * chirp;
        float alpha0Imag = a1[1] * chirp * chirp;
        float alpha1Real = a0[0] * chirp;
        float alpha1Imag = a0[1] * chirp;

        for (int i = start + EnvelopeAdjustmentOffset; i < end + EnvelopeAdjustmentOffset; i++)
        {
            if (i < 2 || i >= BufferSlots)
                continue;

            high[i][0] = low[i - 2][0] * alpha0Real - low[i - 2][1] * alpha0Imag +
                         low[i - 1][0] * alpha1Real - low[i - 1][1] * alpha1Imag +
                         low[i][0];
            high[i][1] = low[i - 2][1] * alpha0Real + low[i - 2][0] * alpha0Imag +
                         low[i - 1][1] * alpha1Real + low[i - 1][0] * alpha1Imag +
                         low[i][1];
        }
    }
}

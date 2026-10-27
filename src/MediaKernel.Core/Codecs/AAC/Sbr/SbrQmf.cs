using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// The SBR QMF filterbanks (14496-3 sp04 p205-206): a 32-band complex analysis bank that takes the
/// AAC core into the subband domain, and a 64-band synthesis bank that takes the reconstructed
/// spectrum back to time samples at twice the rate.
/// </summary>
/// <remarks>
/// The pair is designed for near-perfect reconstruction: running a signal through analysis and then
/// synthesis returns it, delayed and scaled, with only small ripple. That property is what makes
/// this testable without a reference decoder, and the test is worth keeping - a sign error or an
/// off-by-one in the shuffles still produces plausible-looking output but destroys reconstruction.
/// </remarks>
internal sealed class SbrQmf
{
    /// <summary>QMF time slots per frame at the SBR rate: 16 slots x 2.</summary>
    public const int TimeSlots = 32;

    /// <summary>Bands the analysis bank produces.</summary>
    public const int AnalysisBands = 32;

    /// <summary>Bands the synthesis bank consumes.</summary>
    public const int SynthesisBands = 64;

    private const int SynthesisBufferSize = (1280 - 128) * 2;

    /// <summary>
    /// Scale folded into the analysis transform. SBR works on samples scaled to +/-32768, so the
    /// analysis bank scales up from the decoder's +/-1.0 and synthesis scales back down.
    /// </summary>
    private const float AnalysisScale = -2.0f * 32768f;

    private const float SynthesisScale = 1.0f / (64f * 32768f);

    /// <summary>
    /// The two 64-point inverse MDCTs <c>ff_aac_sbr_ctx_init</c> asks <c>av_tx</c> for, at the
    /// scales it asks for. Shared by every instance: an initialised transform is a pair of
    /// read-only tables.
    /// </summary>
    private static readonly Dsp.Tx.TxMdct AnalysisMdct =
        Dsp.Tx.TxMdct.Create(64, inverse: true, full: false, AnalysisScale);

    private static readonly Dsp.Tx.TxMdct SynthesisMdct =
        Dsp.Tx.TxMdct.Create(64, inverse: true, full: false, SynthesisScale);

    private readonly float[] analysisHistory = new float[1312];
    private readonly float[] scratch = new float[640];
    private readonly float[] synthesisBuffer = new float[SynthesisBufferSize];
    private int synthesisOffset = SynthesisBufferSize - (1280 - 128);
    private readonly float[][] synthesisTransform = [new float[64], new float[64]];

    public void Reset()
    {
        Array.Clear(analysisHistory);
        Array.Clear(synthesisBuffer);
        synthesisOffset = SynthesisBufferSize - (1280 - 128);
    }

    /// <summary>
    /// Analysis bank: 1024 time samples in, 32 slots of 32 complex subbands out.
    /// </summary>
    /// <param name="input">1024 samples from the AAC core.</param>
    /// <param name="output">[slot][band][re/im].</param>
    public void Analyse(ReadOnlySpan<float> input, float[][][] output)
    {
        // Slide the 320-sample tail of the previous call in front of the new samples: the
        // prototype filter is 320 taps long and each slot advances by 32.
        Array.Copy(analysisHistory, 1024, analysisHistory, 0, 320 - 32);
        input[..1024].CopyTo(analysisHistory.AsSpan(288));

        var z = scratch;
        for (int slot = 0; slot < TimeSlots; slot++)
        {
            int offset = slot * 32;

            // Windowing, reversed: z[i] = window[i] * x[319 - i].
            var window = SbrQmfWindows.Downsampled;
            for (int i = 0; i < 320; i++)
                z[i] = window[i] * analysisHistory[offset + 319 - i];

            // Fold the 320 taps down to 64 by summing the five polyphase branches.
            for (int k = 0; k < 64; k++)
                z[k] = z[k] + z[k + 64] + z[k + 128] + z[k + 192] + z[k + 256];

            PreShuffle(z);
            AnalysisMdct.Transform(z.AsSpan(128, 64), z.AsSpan(64, 64));
            PostShuffle(output[slot], z.AsSpan(128));
        }
    }

    /// <summary>
    /// Synthesis bank: 32 slots of subbands in, time samples out.
    /// </summary>
    /// <param name="spectrum">[re/im][slot][band].</param>
    /// <param name="output">
    /// 2048 samples at twice the core rate, or 1024 at the core rate when
    /// <paramref name="downsampled"/> is set.
    /// </param>
    /// <param name="downsampled">
    /// Runs the 32-band bank instead of the 64-band one, for the case where SBR is present but the
    /// output stays at the core sample rate. It is also what makes the filterbank pair exactly
    /// invertible, which is how this code is tested.
    /// </param>
    public void Synthesise(float[][][] spectrum, Span<float> output, bool downsampled = false)
    {
        var window = downsampled ? SbrQmfWindows.Downsampled : SbrQmfWindows.Upsampled;
        int div = downsampled ? 1 : 0;
        int bandCount = 64 >> div;
        int step = 128 >> div;
        int outIndex = 0;

        for (int slot = 0; slot < TimeSlots; slot++)
        {
            // The delay line runs backwards; when it reaches the front the tail is copied up and
            // the cursor restarts, which costs one move per frame instead of one per slot.
            if (synthesisOffset < step)
            {
                int saved = (1280 - 128) >> div;
                Array.Copy(synthesisBuffer, 0, synthesisBuffer, SynthesisBufferSize - saved, saved);
                synthesisOffset = SynthesisBufferSize - saved - step;
            }
            else
            {
                synthesisOffset -= step;
            }

            var real = spectrum[0][slot];
            var imaginary = spectrum[1][slot];
            var v = synthesisBuffer.AsSpan(synthesisOffset);

            if (downsampled)
            {
                // One transform over a 64-entry array built from the 32 real and 32 imaginary
                // values, then a negating deinterleave.
                var combined = synthesisTransform[0];
                for (int n = 0; n < 32; n++)
                {
                    combined[n] = -real[n];
                    combined[32 + n] = imaginary[31 - n];
                }

                SynthesisMdct.Transform(synthesisTransform[1], combined);

                for (int i = 0; i < 32; i++)
                {
                    v[i] = synthesisTransform[1][63 - 2 * i];
                    v[63 - i] = -synthesisTransform[1][63 - 2 * i - 1];
                }
            }
            else
            {
                // The imaginary half enters the transform with alternate signs flipped.
                for (int i = 1; i < 64; i += 4)
                {
                    imaginary[i] = -imaginary[i];
                    if (i + 2 < 64)
                        imaginary[i + 2] = -imaginary[i + 2];
                }

                SynthesisMdct.Transform(synthesisTransform[0], real);
                SynthesisMdct.Transform(synthesisTransform[1], imaginary);

                for (int i = 0; i < 64; i++)
                {
                    v[i] = synthesisTransform[1][i] - synthesisTransform[0][63 - i];
                    v[127 - i] = synthesisTransform[1][i] + synthesisTransform[0][63 - i];
                }
            }

            // Ten windowed taps of the delay line sum to one slot of output.
            var outSlice = output.Slice(outIndex, bandCount);
            for (int i = 0; i < bandCount; i++)
                outSlice[i] = v[i] * window[i];

            ReadOnlySpan<int> taps = [192, 256, 448, 512, 704, 768, 960, 1024, 1216];
            for (int t = 0; t < taps.Length; t++)
            {
                int source = taps[t] >> div;
                int windowOffset = bandCount * (t + 1);
                for (int i = 0; i < bandCount; i++)
                    outSlice[i] += synthesisBuffer[synthesisOffset + source + i] * window[windowOffset + i];
            }

            outIndex += bandCount;
        }
    }

    /// <summary>Rearranges the folded branches into the complex form the transform expects.</summary>
    private static void PreShuffle(Span<float> z)
    {
        z[64] = z[0];
        z[65] = z[1];
        for (int k = 1; k < 31; k += 2)
        {
            z[64 + 2 * k + 0] = -z[64 - k];
            z[64 + 2 * k + 1] = z[k + 1];
            z[64 + 2 * k + 2] = -z[63 - k];
            z[64 + 2 * k + 3] = z[k + 2];
        }
        z[64 + 62] = -z[64 - 31];
        z[64 + 63] = z[32];
    }

    /// <summary>Splits the transform output into the real and imaginary halves of each subband.</summary>
    private static void PostShuffle(float[][] bands, ReadOnlySpan<float> z)
    {
        for (int k = 0; k < 32; k += 2)
        {
            bands[k][0] = -z[63 - k];
            bands[k][1] = z[k];
            bands[k + 1][0] = -z[62 - k];
            bands[k + 1][1] = z[k + 1];
        }
    }

    // The 64-point inverse MDCT used to be a direct double-precision cosine sum here, with the
    // scale applied afterwards and the negation written out. It computed the right transform, and
    // SbrQmfTests held it to FFmpeg's own numbers - but it did not round the way av_tx rounds:
    // the scale belongs inside the exponent table as sqrt(|scale|), and a negative scale shifts
    // the angle rather than negating the result. The transform is now av_tx's, which TxTests
    // holds bit-exact at both of these scales.

    /// <summary>Allocates the [slot][band][re/im] array the analysis bank fills.</summary>
    public static float[][][] CreateSubbandBuffer(int slots, int bands)
    {
        var result = new float[slots][][];
        for (int i = 0; i < slots; i++)
        {
            result[i] = new float[bands][];
            for (int j = 0; j < bands; j++)
                result[i][j] = new float[2];
        }
        return result;
    }
}

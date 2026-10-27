using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// Everything one channel's SBR reconstruction has to carry from frame to frame: the two
/// filterbanks with their overlap, the previous frame's subband analysis, the previous frame's
/// adjusted high band, and the chirp factors.
/// </summary>
/// <remarks>
/// SBR is heavily stateful across frames by design. The analysis output is double buffered because
/// the low band for a frame is built from the tail of the previous frame's slots as well as this
/// one's, and the adjusted high band is double buffered for the same reason. Getting any of it
/// wrong produces a click at every frame boundary rather than anything obviously broken.
/// </remarks>
internal sealed class SbrChannelState
{
    public const int Slots = 32;
    public const int OutputSlots = 38;

    public readonly SbrQmf Filterbank = new();
    public readonly SbrEnvelopeAdjustment Adjustment = new();

    /// <summary>Analysis output, double buffered: [buffer][slot][band][re/im].</summary>
    public readonly float[][][][] Analysis =
    [
        SbrQmf.CreateSubbandBuffer(Slots, SbrQmf.AnalysisBands),
        SbrQmf.CreateSubbandBuffer(Slots, SbrQmf.AnalysisBands),
    ];

    /// <summary>Which analysis buffer the current frame wrote.</summary>
    public int AnalysisBuffer;

    /// <summary>Adjusted high band, double buffered: [buffer][slot][band][re/im].</summary>
    public readonly float[][][][] Adjusted =
    [
        SbrQmf.CreateSubbandBuffer(OutputSlots, SbrQmf.SynthesisBands),
        SbrQmf.CreateSubbandBuffer(OutputSlots, SbrQmf.SynthesisBands),
    ];

    /// <summary>Chirp factor per noise band, smoothed across frames.</summary>
    public readonly float[] Chirp = new float[5];

    /// <summary>
    /// Synthesis input for this frame: [re/im][slot][band].
    /// </summary>
    /// <remarks>
    /// 38 slots, not 32, because that is what FFmpeg's <c>X[2][38][64]</c> holds and Parametric
    /// Stereo reads every one of them: its hybrid filterbank is 13 taps wide, so slots 32 to 37
    /// of the low band reach the filtered output of slots 26 to 31. Synthesis still reads only
    /// the first 32.
    /// </remarks>
    public readonly float[][][] Spectrum = CreateSpectrum();

    private static float[][][] CreateSpectrum()
    {
        var result = new float[2][][];
        for (int part = 0; part < 2; part++)
        {
            result[part] = new float[OutputSlots][];
            for (int slot = 0; slot < OutputSlots; slot++)
                result[part][slot] = new float[SbrQmf.SynthesisBands];
        }
        return result;
    }

    public void Reset()
    {
        Filterbank.Reset();
        Adjustment.Reset();
        AnalysisBuffer = 0;
        Array.Clear(Chirp);

        foreach (var buffer in Analysis)
            foreach (var slot in buffer)
                foreach (var band in slot)
                    Array.Clear(band);

        foreach (var buffer in Adjusted)
            foreach (var slot in buffer)
                foreach (var band in slot)
                    Array.Clear(band);

        foreach (var part in Spectrum)
            foreach (var slot in part)
                Array.Clear(slot);
    }
}

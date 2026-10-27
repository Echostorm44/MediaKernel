using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>How an SBR frame divides its envelopes in time (bs_frame_class, 14496-3 4.6.18.3).</summary>
internal enum SbrFrameClass
{
    /// <summary>Envelopes of equal length spanning the frame.</summary>
    FixFix = 0,

    /// <summary>Fixed start border, variable end.</summary>
    FixVar = 1,

    /// <summary>Variable start border, fixed end.</summary>
    VarFix = 2,

    /// <summary>Both borders variable.</summary>
    VarVar = 3,
}

/// <summary>
/// The frequency-band layout parameters from an SBR header. Kept together because a change to any
/// of them invalidates every derived table and forces a reset.
/// </summary>
internal struct SbrSpectrumParameters : IEquatable<SbrSpectrumParameters>
{
    public byte StartFrequency;
    public byte StopFrequency;
    public byte CrossoverBand;
    public byte FrequencyScale;
    public byte AlterScale;
    public byte NoiseBands;

    /// <summary>
    /// Every field -1, which is <c>memset(&amp;sbr-&gt;spectrum_params, -1, ...)</c>: a value no
    /// real header can equal, so the next header always compares different and rebuilds.
    /// </summary>
    public static SbrSpectrumParameters Invalid => new()
    {
        StartFrequency = 0xFF,
        StopFrequency = 0xFF,
        CrossoverBand = 0xFF,
        FrequencyScale = 0xFF,
        AlterScale = 0xFF,
        NoiseBands = 0xFF,
    };

    public readonly bool Equals(SbrSpectrumParameters other) =>
        StartFrequency == other.StartFrequency &&
        StopFrequency == other.StopFrequency &&
        CrossoverBand == other.CrossoverBand &&
        FrequencyScale == other.FrequencyScale &&
        AlterScale == other.AlterScale &&
        NoiseBands == other.NoiseBands;

    public readonly override bool Equals(object? obj) => obj is SbrSpectrumParameters other && Equals(other);

    public readonly override int GetHashCode() =>
        HashCode.Combine(StartFrequency, StopFrequency, CrossoverBand, FrequencyScale, AlterScale, NoiseBands);
}

/// <summary>
/// Per-channel SBR state. Several fields deliberately survive across frames: envelope and noise
/// scalefactors are delta-coded against the previous frame, and the filterbank keeps overlap.
/// </summary>
internal sealed class SbrChannelData
{
    public const int MaxEnvelopes = 8;
    public const int MaxNoiseFloors = 2;
    public const int MaxBands = 48;
    public const int MaxNoiseBands = 5;

    public SbrFrameClass FrameClass;
    public int EnvelopeCount;
    public int NoiseFloorCount;
    public bool AmplitudeResolution;

    /// <summary>Frequency resolution per envelope; index 0 carries over from the previous frame.</summary>
    public readonly byte[] FrequencyResolution = new byte[MaxEnvelopes + 1];

    /// <summary>Delta-in-time flags for envelopes and noise floors.</summary>
    public readonly byte[] DeltaTimeEnvelope = new byte[MaxEnvelopes + 1];
    public readonly byte[] DeltaTimeNoise = new byte[MaxNoiseFloors + 1];

    /// <summary>Inverse filtering mode per noise band; [0] is current, [1] the previous frame.</summary>
    public readonly byte[][] InverseFilterMode =
    [
        new byte[MaxNoiseBands],
        new byte[MaxNoiseBands],
    ];

    public bool AddHarmonicFlag;
    public readonly byte[] AddHarmonic = new byte[MaxBands];

    /// <summary>Quantised envelope scalefactors; row 0 holds the previous frame's last envelope.</summary>
    public readonly byte[][] EnvelopeScalefactors = CreateRows(MaxEnvelopes + 1, MaxBands);

    /// <summary>Quantised noise floor scalefactors; row 0 carries over as above.</summary>
    public readonly byte[][] NoiseScalefactors = CreateRows(MaxNoiseFloors + 1, MaxNoiseBands);

    /// <summary>Envelope time borders, in QMF slots.</summary>
    public readonly byte[] EnvelopeBorders = new byte[MaxEnvelopes + 1];

    /// <summary>The previous frame's final envelope border, needed to line up the overlap.</summary>
    public byte PreviousFinalBorder;

    /// <summary>Noise floor time borders.</summary>
    public readonly byte[] NoiseBorders = new byte[MaxNoiseFloors + 1];

    /// <summary>l_APrev and l_A: the envelopes that must not get a sine added.</summary>
    public readonly int[] SineStartEnvelope = new int[2];

    /// <summary>Dequantised envelope levels, one row per envelope.</summary>
    public readonly float[][] EnvelopeLevels = CreateFloatRows(MaxEnvelopes + 1, MaxBands);

    /// <summary>Dequantised noise floor levels.</summary>
    public readonly float[][] NoiseLevels = CreateFloatRows(MaxNoiseFloors + 1, MaxNoiseBands);

    /// <summary>Where a sinusoid sits, per subband; row 0 carries over from the previous frame.</summary>
    public readonly byte[][] SineIndexMapped = CreateRows(MaxEnvelopes + 1, MaxBands);

    /// <summary>Running indices into the noise table and the sinusoid phase, kept across frames.</summary>
    public int NoiseIndex;
    public int SineIndex;

    private static float[][] CreateFloatRows(int rows, int columns)
    {
        var result = new float[rows][];
        for (int i = 0; i < rows; i++)
            result[i] = new float[columns];
        return result;
    }

    private static byte[][] CreateRows(int rows, int columns)
    {
        var result = new byte[rows][];
        for (int i = 0; i < rows; i++)
            result[i] = new byte[columns];
        return result;
    }

    public void Reset()
    {
        FrameClass = SbrFrameClass.FixFix;
        EnvelopeCount = 0;
        NoiseFloorCount = 0;
        AmplitudeResolution = false;
        Array.Clear(FrequencyResolution);
        Array.Clear(DeltaTimeEnvelope);
        Array.Clear(DeltaTimeNoise);
        Array.Clear(InverseFilterMode[0]);
        Array.Clear(InverseFilterMode[1]);
        AddHarmonicFlag = false;
        Array.Clear(AddHarmonic);
        foreach (var row in EnvelopeScalefactors) Array.Clear(row);
        foreach (var row in NoiseScalefactors) Array.Clear(row);
        Array.Clear(EnvelopeBorders);
        Array.Clear(NoiseBorders);
        PreviousFinalBorder = 0;
        SineStartEnvelope[0] = 0;
        SineStartEnvelope[1] = -1;
        foreach (var row in EnvelopeLevels) Array.Clear(row);
        foreach (var row in NoiseLevels) Array.Clear(row);
        foreach (var row in SineIndexMapped) Array.Clear(row);
        NoiseIndex = 0;
        SineIndex = 0;
    }
}

/// <summary>
/// Decoder state for Spectral Band Replication - the HE-AAC tool that reconstructs the top half of
/// the spectrum from the AAC core plus a small parametric side channel.
/// </summary>
/// <remarks>
/// Without it an HE-AAC stream still decodes, but only the core: half the sample rate and a dull,
/// band-limited result. Worse, the core's sample rate is what gets reported, so a player trusting a
/// container that says otherwise plays at the wrong speed.
/// </remarks>
internal sealed class SbrContext
{
    /// <summary>The SBR sample rate, which is twice the AAC core's.</summary>
    public int SampleRate;

    /// <summary>True once a header has been seen; until then there is nothing to apply.</summary>
    public bool Started;

    /// <summary>Set when the band layout changed and the derived tables must be rebuilt.</summary>
    public bool NeedsReset;

    /// <summary>
    /// Set when the header changed the limiter-band count but nothing else. The limiter table is
    /// derived from the band layout and has to be rebuilt; the rest of the state, including the
    /// envelope smoothing history, carries on. <see cref="NeedsReset"/> would discard it.
    /// </summary>
    public bool NeedsLimiterRebuild;

    /// <summary>Turned off for the rest of the stream when the payload cannot be trusted.</summary>
    public bool Disabled;

    public bool AmplitudeResolutionHeader;
    public SbrSpectrumParameters SpectrumParameters;

    public uint LimiterBands = 2;
    public uint LimiterGains = 2;
    public uint InterpolationFrequency = 1;
    public uint SmoothingMode = 1;

    /// <summary>True when a channel pair shares one set of envelopes, the second coded as a balance.</summary>
    public bool Coupling;

    /// <summary>Bit set of SBR extension ids seen, for diagnosis. Bit 2 is Parametric Stereo.</summary>
    public uint ExtensionsSeen;

    /// <summary>True once a Parametric Stereo payload has appeared.</summary>
    public bool SawParametricStereo;

    /// <summary>
    /// <c>sbr->ps</c>: the Parametric Stereo state. PS is carried inside the SBR payload and
    /// applied to SBR's own QMF spectra, so it belongs to the SBR context exactly as it does in
    /// FFmpeg rather than to the AAC decoder.
    /// </summary>
    public readonly Ps.PsContext Ps = new();

    /// <summary>
    /// <c>m4ac.ps</c>: -1 not yet known, 0 signalled absent, 1 present. The AAC decoder owns the
    /// value because it comes from the AudioSpecificConfig; SBR reads it to decide whether a PS
    /// payload may be parsed and whether to produce a second channel.
    /// </summary>
    public int PsMode = -1;

    /// <summary>
    /// <c>m4ac.ext_sample_rate</c>: the rate the configuration says the SBR output runs at. Equal
    /// to <see cref="SampleRate"/> for ordinary HE-AAC, and to the core rate for single-rate SBR.
    /// </summary>
    public int ExtensionSampleRate;

    /// <summary>k0, k1 and k2: the QMF band boundaries the band tables are built from.</summary>
    public readonly int[] K = new int[5];

    /// <summary>Number of noise bands.</summary>
    public int NoiseBandCount;

    /// <summary>N_Low and N_High: band counts at low and high frequency resolution.</summary>
    public readonly int[] BandCount = new int[2];

    public readonly SbrChannelData[] Channels =
    [
        new SbrChannelData(),
        new SbrChannelData(),
    ];

    public void Reset()
    {
        Started = false;
        NeedsReset = false;
        NeedsLimiterRebuild = false;
        Disabled = false;
        Coupling = false;
        Ps.Reset();
        Array.Clear(K);
        Array.Clear(BandCount);
        NoiseBandCount = 0;
        foreach (var channel in Channels)
            channel.Reset();
    }
}

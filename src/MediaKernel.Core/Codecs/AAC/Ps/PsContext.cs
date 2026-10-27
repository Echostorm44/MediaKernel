// aacps.h - Parametric Stereo definitions and the per-stream state.
//
// PS reconstructs a stereo image from one decoded channel plus a few hundred bits per frame of
// inter-channel intensity difference, coherence and phase. It runs on the QMF domain between
// SBR's reconstruction and SBR's synthesis filterbank, which is why it lives beside the SBR code
// and shares its bit reader.
//
// Every buffer here is allocated once with the context. PS carries a lot of state across frames -
// the hybrid filterbank's six-sample history, 91 bands of delay line, three all-pass links per
// band, the smoothing energies and the previous frame's mixing matrices - and a decoder that
// reallocated any of it per frame would both lose the history and allocate on the audio path.

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary>Sizes from 14496-3 section 8.6.4, named as <c>aacps.h</c> names them.</summary>
internal static class PsLimits
{
    public const int MaxNumEnv = 5;
    public const int MaxNrIidIcc = 34;
    public const int MaxNrIpdOpd = 17;
    public const int MaxSsb = 91;
    public const int MaxApBands = 50;
    public const int QmfTimeSlots = 32;
    public const int MaxDelay = 14;
    public const int ApLinks = 3;
    public const int MaxApDelay = 5;

    /// <summary>
    /// <c>PS_BASELINE</c>: baseline PS is 10 or 20 stereo bands, mixing mode A and no IPD/OPD.
    /// FFmpeg builds with it off, so the 34-band and phase paths are live, and so are they here.
    /// </summary>
    public const bool Baseline = false;

    /// <summary><c>numQMFSlots</c>.</summary>
    public const int NumQmfSlots = 32;
}

/// <summary>
/// <c>PSCommonContext</c>: everything the bitstream parser produces, which the fixed and float
/// decoders share.
/// </summary>
internal sealed class PsCommonContext
{
    public bool Start;
    public bool EnableIid;
    public int IidQuant;
    public int NrIidPar;
    public int NrIpdOpdPar;
    public bool EnableIcc;
    public int IccMode;
    public int NrIccPar;
    public bool EnableExt;
    public int FrameClass;
    public int NumEnvOld;
    public int NumEnv;
    public bool EnableIpdOpd;

    public readonly int[] BorderPosition = new int[PsLimits.MaxNumEnv + 1];

    /// <summary>Inter-channel Intensity Difference parameters.</summary>
    public readonly sbyte[][] IidPar = Rows(PsLimits.MaxNumEnv + 1, PsLimits.MaxNrIidIcc);

    /// <summary>Inter-Channel Coherence parameters.</summary>
    public readonly sbyte[][] IccPar = Rows(PsLimits.MaxNumEnv + 1, PsLimits.MaxNrIidIcc);

    /// <summary>Inter-channel Phase Difference parameters, sized like IID so the same readers work.</summary>
    public readonly sbyte[][] IpdPar = Rows(PsLimits.MaxNumEnv + 1, PsLimits.MaxNrIidIcc);

    /// <summary>Overall Phase Difference parameters.</summary>
    public readonly sbyte[][] OpdPar = Rows(PsLimits.MaxNumEnv + 1, PsLimits.MaxNrIidIcc);

    public bool Is34Bands;
    public bool Is34BandsOld;

    /// <summary>
    /// Why the last payload was rejected, or null when it was accepted. Set only on the failure
    /// path, so it costs nothing in the ordinary case, and it is the difference between "PS is
    /// not in this stream" and "PS is in this stream and the parse is wrong".
    /// </summary>
    /// <summary>
    /// Why the last PS payload was rejected, or null if it was accepted. A constant string, never
    /// a built one: this is set on a decode path, and formatting a message there costs an
    /// allocation on every frame that trips it. The number that went with it lives in
    /// <see cref="FailValue"/> and the two are put together only when something asks.
    /// </summary>
    public string? FailReason;

    /// <summary>
    /// The offending value behind <see cref="FailReason"/>, or <see cref="NoFailValue"/> when the
    /// reason needs no number.
    /// </summary>
    public int FailValue = NoFailValue;

    /// <summary>Sentinel for a failure reason that carries no number.</summary>
    public const int NoFailValue = int.MinValue;

    /// <summary>The failure as one line, built on demand for diagnostics.</summary>
    public string? DescribeFailure() =>
        FailReason is null ? null
        : FailValue == NoFailValue ? FailReason
        : $"{FailReason} ({FailValue})";

    /// <remarks>
    /// The C declares these as <c>[PS_MAX_NUM_ENV][PS_MAX_NR_IIDICC]</c> and then writes
    /// <c>par[ps->num_env]</c> while fixing up a short last envelope, with <c>num_env</c> able to
    /// reach <c>PS_MAX_NUM_ENV</c>. That write lands in the next row of the same object in C; here
    /// there is one extra row so it lands somewhere real rather than out of bounds.
    /// </remarks>
    private static sbyte[][] Rows(int rows, int columns)
    {
        var result = new sbyte[rows][];
        for (int i = 0; i < rows; i++) result[i] = new sbyte[columns];
        return result;
    }

    public void Reset()
    {
        Start = false;
        EnableIid = EnableIcc = EnableExt = EnableIpdOpd = false;
        IidQuant = NrIidPar = NrIpdOpdPar = IccMode = NrIccPar = 0;
        FrameClass = NumEnvOld = NumEnv = 0;
        Is34Bands = Is34BandsOld = false;
        System.Array.Clear(BorderPosition);
        foreach (sbyte[] row in IidPar) System.Array.Clear(row);
        foreach (sbyte[] row in IccPar) System.Array.Clear(row);
        foreach (sbyte[] row in IpdPar) System.Array.Clear(row);
        foreach (sbyte[] row in OpdPar) System.Array.Clear(row);
    }
}

/// <summary>
/// <c>PSContext</c>: the parsed parameters plus the filterbank, delay and smoothing state that
/// has to survive from one frame to the next.
/// </summary>
internal sealed class PsContext
{
    public readonly PsCommonContext Common = new();

    /// <summary><c>in_buf[5][44][2]</c>: the hybrid filterbank's input with its six-slot history.</summary>
    public readonly float[][][] InBuf = Complex2D(5, 44);

    /// <summary><c>delay[91][32+14][2]</c>.</summary>
    public readonly float[][][] Delay =
        Complex2D(PsLimits.MaxSsb, PsLimits.QmfTimeSlots + PsLimits.MaxDelay);

    /// <summary><c>ap_delay[50][3][32+5][2]</c>.</summary>
    public readonly float[][][][] ApDelay = Complex3D(
        PsLimits.MaxApBands, PsLimits.ApLinks, PsLimits.QmfTimeSlots + PsLimits.MaxApDelay);

    public readonly float[] PeakDecayNrg = new float[34];
    public readonly float[] PowerSmooth = new float[34];
    public readonly float[] PeakDecayDiffSmooth = new float[34];

    /// <summary>Mixing matrices, <c>[re/im][env+1][band]</c>.</summary>
    public readonly float[][][] H11 = Matrix();
    public readonly float[][][] H12 = Matrix();
    public readonly float[][][] H21 = Matrix();
    public readonly float[][][] H22 = Matrix();

    /// <summary><c>Lbuf[91][32][2]</c>, the hybrid-domain left/mid signal.</summary>
    public readonly float[][][] LBuf = Complex2D(PsLimits.MaxSsb, PsLimits.QmfTimeSlots);

    /// <summary><c>Rbuf[91][32][2]</c>, the decorrelated side signal.</summary>
    public readonly float[][][] RBuf = Complex2D(PsLimits.MaxSsb, PsLimits.QmfTimeSlots);

    public readonly sbyte[] OpdHist = new sbyte[PsLimits.MaxNrIidIcc];
    public readonly sbyte[] IpdHist = new sbyte[PsLimits.MaxNrIidIcc];

    /// <summary>Scratch for <see cref="Ps.Decorrelation"/>: per-band power and transient gain.</summary>
    public readonly float[][] Power = Plane(34, PsLimits.QmfTimeSlots);
    public readonly float[][] TransientGain = Plane(34, PsLimits.QmfTimeSlots);

    /// <summary>Scratch for the remapped parameters, one row per envelope.</summary>
    public readonly sbyte[][] IidMapped = Plane8(PsLimits.MaxNumEnv, PsLimits.MaxNrIidIcc);
    public readonly sbyte[][] IccMapped = Plane8(PsLimits.MaxNumEnv, PsLimits.MaxNrIidIcc);
    public readonly sbyte[][] IpdMapped = Plane8(PsLimits.MaxNumEnv, PsLimits.MaxNrIidIcc);
    public readonly sbyte[][] OpdMapped = Plane8(PsLimits.MaxNumEnv, PsLimits.MaxNrIidIcc);

    /// <summary><c>hybrid6_cx</c>'s eight-point scratch.</summary>
    public readonly float[][] HybridTemp = Complex(8);

    /// <summary><c>stereo_processing</c>'s interpolation coefficients, <c>h[2][4]</c>.</summary>
    public readonly float[][] H = Plane(2, 4);
    public readonly float[][] HStep = Plane(2, 4);

    public static float[][] Complex(int n)
    {
        var result = new float[n][];
        for (int i = 0; i < n; i++) result[i] = new float[2];
        return result;
    }

    private static float[][][] Complex2D(int outer, int inner)
    {
        var result = new float[outer][][];
        for (int i = 0; i < outer; i++) result[i] = Complex(inner);
        return result;
    }

    private static float[][][][] Complex3D(int a, int b, int c)
    {
        var result = new float[a][][][];
        for (int i = 0; i < a; i++) result[i] = Complex2D(b, c);
        return result;
    }

    private static float[][] Plane(int rows, int columns)
    {
        var result = new float[rows][];
        for (int i = 0; i < rows; i++) result[i] = new float[columns];
        return result;
    }

    private static sbyte[][] Plane8(int rows, int columns)
    {
        var result = new sbyte[rows][];
        for (int i = 0; i < rows; i++) result[i] = new sbyte[columns];
        return result;
    }

    private static float[][][] Matrix()
    {
        var result = new float[2][][];
        for (int i = 0; i < 2; i++)
        {
            result[i] = new float[PsLimits.MaxNumEnv + 1][];
            for (int e = 0; e <= PsLimits.MaxNumEnv; e++)
                result[i][e] = new float[PsLimits.MaxNrIidIcc];
        }
        return result;
    }

    public void Reset()
    {
        Common.Reset();
        foreach (float[][] row in InBuf) Clear(row);
        foreach (float[][] row in Delay) Clear(row);
        foreach (float[][][] band in ApDelay) foreach (float[][] link in band) Clear(link);
        System.Array.Clear(PeakDecayNrg);
        System.Array.Clear(PowerSmooth);
        System.Array.Clear(PeakDecayDiffSmooth);
        foreach (float[][][] matrix in new[] { H11, H12, H21, H22 })
            foreach (float[][] part in matrix)
                foreach (float[] row in part)
                    System.Array.Clear(row);
        foreach (float[][] row in LBuf) Clear(row);
        foreach (float[][] row in RBuf) Clear(row);
        System.Array.Clear(OpdHist);
        System.Array.Clear(IpdHist);
    }

    private static void Clear(float[][] rows)
    {
        foreach (float[] row in rows) System.Array.Clear(row);
    }
}

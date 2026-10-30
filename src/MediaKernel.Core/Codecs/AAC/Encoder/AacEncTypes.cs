// MediaKernel - the AAC encoder's data layout
//
// The structures of libavcodec/aac.h and aacenc.h that the encoder works through, with their
// fields in the C's declared order. The layout comes before the algorithms on purpose: these
// structs ARE the encoder, and a field quietly missing from one of them is the kind of fault
// that surfaces three stages later as a decoder reading a zero where ffmpeg reads a flag.
//
// Two-dimensional C arrays are flattened, with the stride spelled out at the accessor, because
// a jagged array would allocate a row object per window for data that is written every frame.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary><c>enum WindowSequence</c>.</summary>
internal enum AacWindowSequence
{
    OnlyLong = 0,
    LongStart = 1,
    EightShort = 2,
    LongStop = 3,
}

/// <summary><c>TemporalNoiseShaping</c>: the filters TNS applies, per window.</summary>
internal sealed class AacTemporalNoiseShaping
{
    /// <summary><c>TNS_MAX_ORDER</c>.</summary>
    public const int MaxOrder = 20;

    /// <summary>Windows a frame can have.</summary>
    public const int MaxWindows = 8;

    /// <summary>Filters a window can carry.</summary>
    public const int MaxFilters = 4;

    public int Present;

    /// <summary><c>n_filt[8]</c>.</summary>
    public readonly int[] NFilt = new int[MaxWindows];

    /// <summary><c>length[8][4]</c>, flattened: <c>[w * 4 + filt]</c>.</summary>
    public readonly int[] Length = new int[MaxWindows * MaxFilters];

    /// <summary><c>direction[8][4]</c>.</summary>
    public readonly int[] Direction = new int[MaxWindows * MaxFilters];

    /// <summary><c>order[8][4]</c>.</summary>
    public readonly int[] Order = new int[MaxWindows * MaxFilters];

    /// <summary><c>coef_idx[8][4][TNS_MAX_ORDER]</c>, flattened.</summary>
    public readonly int[] CoefIdx = new int[MaxWindows * MaxFilters * MaxOrder];

    /// <summary><c>coef[8][4][TNS_MAX_ORDER]</c>, flattened.</summary>
    public readonly float[] Coef = new float[MaxWindows * MaxFilters * MaxOrder];

    /// <summary>The <c>[w][filt]</c> slice of <see cref="CoefIdx"/>.</summary>
    public Span<int> CoefIdxOf(int window, int filter) =>
        CoefIdx.AsSpan((window * MaxFilters + filter) * MaxOrder, MaxOrder);

    /// <summary>The <c>[w][filt]</c> slice of <see cref="Coef"/>.</summary>
    public Span<float> CoefOf(int window, int filter) =>
        Coef.AsSpan((window * MaxFilters + filter) * MaxOrder, MaxOrder);

    public void Clear()
    {
        Present = 0;
        Array.Clear(NFilt);
        Array.Clear(Length);
        Array.Clear(Direction);
        Array.Clear(Order);
        Array.Clear(CoefIdx);
        Array.Clear(Coef);
    }
}

/// <summary><c>IndividualChannelStream</c>: how a channel's spectrum is divided this frame.</summary>
internal sealed class AacIndividualChannelStream
{
    /// <summary><c>max_sfb</c>: scalefactor bands per group.</summary>
    public byte MaxSfb;

    /// <summary><c>window_sequence[2]</c>: this frame's and the previous frame's.</summary>
    public readonly AacWindowSequence[] WindowSequence = new AacWindowSequence[2];

    /// <summary><c>use_kb_window[2]</c>: Kaiser-Bessel if set, otherwise sine.</summary>
    public readonly byte[] UseKbWindow = new byte[2];

    public int NumWindowGroups;
    public int PrevNumWindowGroups;

    /// <summary><c>group_len[8]</c>.</summary>
    public readonly byte[] GroupLen = new byte[8];

    /// <summary>
    /// <c>swb_offset</c>: the first coefficient of each scalefactor band. The encoder's band
    /// SIZES are in <see cref="AacEncTables"/>; this is the running sum of them, which is what
    /// the C carries.
    /// </summary>
    public readonly int[] SwbOffset = new int[128];

    public int NumSwb;
    public int NumWindows;
    public int TnsMaxBands;

    /// <summary><c>swb_sizes</c>: the band sizes themselves, which the encoder also reads.</summary>
    public readonly byte[] SwbSizes = new byte[128];

    /// <summary><c>window_clipping[8]</c>: set where a window is near clipping.</summary>
    public readonly byte[] WindowClipping = new byte[8];
}

/// <summary><c>SingleChannelElement</c>: one channel, as the encoder builds it.</summary>
/// <remarks>
/// Only the fields the translated stages use are here. Each one is added when the function that
/// reads it is translated, so the struct never carries a field nothing has been checked against.
/// </remarks>
internal sealed class AacSingleChannelElement
{
    public readonly AacIndividualChannelStream Ics = new();

    /// <summary><c>coeffs[1024]</c>: the spectrum, which TNS and the quantiser both rewrite.</summary>
    public readonly float[] Coeffs = new float[1024];

    /// <summary>
    /// <c>pcoeffs[1024]</c>: the spectrum BEFORE TNS. The AR filter reads its own history from
    /// here rather than from the values it is writing, which is what makes it invertible.
    /// </summary>
    public readonly float[] PCoeffs = new float[1024];

    /// <summary><c>sf_idx[128]</c>: the scalefactor of each band.</summary>
    public readonly int[] SfIdx = new int[128];

    /// <summary><c>zeroes[128]</c>: set where a band is coded as nothing.</summary>
    public readonly byte[] Zeroes = new byte[128];

    /// <summary><c>band_type[128]</c>: the codebook each band is coded with.</summary>
    public readonly byte[] BandType = new byte[128];

    /// <summary><c>can_pns[128]</c>: set where noise substitution is allowed.</summary>
    public readonly byte[] CanPns = new byte[128];

    /// <summary>
    /// <c>band_alt[128]</c>: the codebook each band had BEFORE the noise search. The search
    /// costs a band against the codebook the quantiser picked, which it is in the middle of
    /// overwriting, so the original has to be kept.
    /// </summary>
    public readonly byte[] BandAlt = new byte[128];

    /// <summary><c>is_ener[128]</c>: the intensity stereo scale of each band.</summary>
    public readonly float[] IsEner = new float[128];

    /// <summary><c>pns_ener[128]</c>: the level a noise-substituted band is reconstructed at.</summary>
    public readonly float[] PnsEner = new float[128];

    public readonly AacTemporalNoiseShaping Tns = new();
}

/// <summary><c>ChannelElement</c>: a pair of channels coded together.</summary>
internal sealed class AacChannelElement
{
    /// <summary><c>common_window</c>: set when both channels share a window sequence.</summary>
    public int CommonWindow;

    /// <summary><c>ms_mask[128]</c>: set where the band is coded mid/side.</summary>
    public readonly byte[] MsMask = new byte[128];

    /// <summary><c>is_mask[128]</c>: set where the band is coded as intensity stereo.</summary>
    public readonly byte[] IsMask = new byte[128];

    /// <summary><c>is_mode</c>: set when any band is.</summary>
    public int IsMode;

    /// <summary><c>ch[2]</c>.</summary>
    public readonly AacSingleChannelElement[] Ch = [new(), new()];
}

/// <summary>
/// The fields of <c>AVCodecContext</c> the coefficient searches read. A small struct rather
/// than a context object, because these five values are all of libavcodec's encoder
/// configuration that the translated stages touch, and they never change within a frame.
/// </summary>
internal readonly struct AacEncoderSettings
{
    public required int SampleRate { get; init; }

    /// <summary><c>bit_rate</c>, which is 64-bit in the C and is used in double arithmetic.</summary>
    public required long BitRate { get; init; }

    /// <summary><c>ch_layout.nb_channels</c>.</summary>
    public required int Channels { get; init; }

    /// <summary><c>cutoff</c>: an explicit bandwidth limit, or zero to derive one.</summary>
    public required int Cutoff { get; init; }

    /// <summary><c>flags &amp; AV_CODEC_FLAG_QSCALE</c>: constant quality rather than rate.</summary>
    public required bool QScale { get; init; }
}

/// <summary>
/// <c>FFPsyBand</c>: what the psychoacoustic model says about one band of one window.
/// </summary>
internal struct AacPsyBand
{
    public int Bits;
    public float Energy;
    public float Threshold;

    /// <summary><c>spread</c>: the energy spread over the band.</summary>
    public float Spread;
}

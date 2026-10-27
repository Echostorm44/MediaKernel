// DTS Coherent Acoustics - the declarations of dca.h, dca_core.h and dca_syncwords.h.
//
// Ported layout-first, before any algorithm, because the context structs and their caches ARE
// the algorithm here: dca_core.c addresses subband samples through a table of pointers per
// channel per subband, and the shape of that table is what makes its inner loops the shape they
// are.

using System;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>dca_syncwords.h</c>.</summary>
internal static class DcaSyncwords
{
    public const uint CoreBe = 0x7FFE8001u;
    public const uint CoreLe = 0xFE7F0180u;
    public const uint Core14BBe = 0x1FFFE800u;
    public const uint Core14BLe = 0xFF1F00E8u;
    public const uint Xch = 0x5A5A5A5Au;
    public const uint Xxch = 0x47004A03u;
    public const uint X96 = 0x1D95F262u;
    public const uint Xbr = 0x655E315Eu;
    public const uint Lbr = 0x0A801921u;
    public const uint Xll = 0x41A29547u;
    public const uint Substream = 0x64582025u;
    public const uint SubstreamCore = 0x02B09261u;
    public const uint Rev1Aux = 0x9A1105A0u;
    public const uint XllX = 0x02000850u;
    public const uint XllXImax = 0xF14000D0u;
}

/// <summary>The <c>#define</c>d limits of <c>dca.h</c>, <c>dca_core.h</c> and <c>dcahuff.h</c>.</summary>
internal static class DcaLimits
{
    public const int CoreFrameHeaderSize = 18;

    public const int Channels = 7;
    public const int Subbands = 32;
    public const int SubbandsX96 = 64;
    public const int Subframes = 16;
    public const int SubbandSamples = 8;
    public const int PcmBlockSamples = 32;
    public const int LfeHistory = 8;
    public const int AbitsMax = 26;

    public const int CoreChannelsMax = 6;
    public const int DmixChannelsMax = 4;
    public const int XxchChannelsMax = 2;
    public const int ExssChannelsMax = 8;
    public const int ExssChsetsMax = 4;

    public const int FilterModeX96 = 0x01;
    public const int FilterModeFixed = 0x02;

    public const int CodeBooks = 10;
    public const int Bitalloc12Count = 5;
}

/// <summary><c>enum DCAParseError</c>. Negative, as the C returns them.</summary>
internal enum DcaParseError
{
    None = 0,
    SyncWord = -1,
    DeficitSamples = -2,
    PcmBlocks = -3,
    FrameSize = -4,
    AudioMode = -5,
    SampleRate = -6,
    ReservedBit = -7,
    LfeFlag = -8,
    PcmRes = -9,
}

/// <summary><c>enum DCASpeaker</c> - the order is the bit order of the layout masks.</summary>
internal enum DcaSpeaker
{
    C, L, R, Ls,
    Rs, Lfe1, Cs, Lsr,
    Rsr, Lss, Rss, Lc,
    Rc, Lh, Ch, Rh,
    Lfe2, Lw, Rw, Oh,
    Lhs, Rhs, Chr, Lhr,
    Rhr, Cl, Ll, Rl,
    Rsv1, Rsv2, Rsv3, Rsv4,

    Count,
}

/// <summary><c>enum DCASpeakerMask</c> - one bit per <see cref="DcaSpeaker"/>.</summary>
internal static class DcaSpeakerMask
{
    public const int C = 0x00000001;
    public const int L = 0x00000002;
    public const int R = 0x00000004;
    public const int Ls = 0x00000008;
    public const int Rs = 0x00000010;
    public const int Lfe1 = 0x00000020;
    public const int Cs = 0x00000040;
    public const int Lsr = 0x00000080;
    public const int Rsr = 0x00000100;
    public const int Lss = 0x00000200;
    public const int Rss = 0x00000400;
    public const int Lc = 0x00000800;
    public const int Rc = 0x00001000;
    public const int Lh = 0x00002000;
    public const int Ch = 0x00004000;
    public const int Rh = 0x00008000;
    public const int Lfe2 = 0x00010000;
    public const int Lw = 0x00020000;
    public const int Rw = 0x00040000;
    public const int Oh = 0x00080000;
    public const int Lhs = 0x00100000;
    public const int Rhs = 0x00200000;
    public const int Chr = 0x00400000;
    public const int Lhr = 0x00800000;
    public const int Rhr = 0x01000000;
    public const int Cl = 0x02000000;
    public const int Ll = 0x04000000;
    public const int Rl = 0x08000000;
}

/// <summary>The <c>DCA_SPEAKER_LAYOUT_*</c> macros.</summary>
internal static class DcaSpeakerLayout
{
    public const int Mono = DcaSpeakerMask.C;
    public const int Stereo = DcaSpeakerMask.L | DcaSpeakerMask.R;
    public const int Point2Point1 = Stereo | DcaSpeakerMask.Lfe1;
    public const int Layout3_0 = Stereo | DcaSpeakerMask.C;
    public const int Layout2_1 = Stereo | DcaSpeakerMask.Cs;
    public const int Layout3_1 = Layout3_0 | DcaSpeakerMask.Cs;
    public const int Layout2_2 = Stereo | DcaSpeakerMask.Ls | DcaSpeakerMask.Rs;
    public const int Layout5Point0 = Layout3_0 | DcaSpeakerMask.Ls | DcaSpeakerMask.Rs;
    public const int Layout5Point1 = Layout5Point0 | DcaSpeakerMask.Lfe1;
    public const int Layout7Point0Wide = Layout5Point0 | DcaSpeakerMask.Lw | DcaSpeakerMask.Rw;
    public const int Layout7Point1Wide = Layout7Point0Wide | DcaSpeakerMask.Lfe1;

    /// <summary><c>DCA_HAS_STEREO</c>.</summary>
    public static bool HasStereo(int mask) => (mask & Stereo) == Stereo;
}

/// <summary><c>enum DCADownMixType</c> - the layout a dynamic downmix matrix mixes down to.</summary>
/// <remarks>
/// Only <see cref="LoRo"/> and <see cref="LtRt"/> are usable for a stereo request: the C checks
/// for exactly those two before it sets the request mask, because the others describe a downmix
/// to something that is not a stereo pair.
/// </remarks>
internal enum DcaDmixType
{
    Type1_0,
    LoRo,
    LtRt,
    Type3_0,
    Type2_1,
    Type2_2,
    Type3_1,

    Count = 7,
}

/// <summary><c>enum DCASpeakerPair</c>.</summary>
internal static class DcaSpeakerPair
{
    public const int C = 0x0001;
    public const int Lr = 0x0002;
    public const int LsRs = 0x0004;
    public const int Lfe1 = 0x0008;
    public const int Cs = 0x0010;
    public const int LhRh = 0x0020;
    public const int LsrRsr = 0x0040;
    public const int Ch = 0x0080;
    public const int Oh = 0x0100;
    public const int LcRc = 0x0200;
    public const int LwRw = 0x0400;
    public const int LssRss = 0x0800;
    public const int Lfe2 = 0x1000;
    public const int LhsRhs = 0x2000;
    public const int Chr = 0x4000;
    public const int LhrRhr = 0x8000;

    /// <summary>
    /// <c>ff_dca_count_chs_for_mask</c> - how many individual channels a pair mask names.
    /// </summary>
    /// <remarks>
    /// The trick is FFmpeg's and is worth not flattening into a loop: every bit in 0xae66 names a
    /// <i>pair</i> of speakers rather than one, so shifting those bits up by 16 and counting the
    /// whole 32-bit word counts them twice in a single popcount.
    /// </remarks>
    public static int CountChsForMask(uint mask)
        => System.Numerics.BitOperations.PopCount((mask & 0xffff) | ((mask & 0xae66) << 16));
}

/// <summary><c>enum DCARepresentationType</c>.</summary>
internal enum DcaRepresentationType
{
    LtRt = 2,
    LhRh = 3,
}

/// <summary><c>enum DCAExtensionMask</c>.</summary>
internal static class DcaExtensionMask
{
    public const int CssCore = 0x001;
    public const int CssXxch = 0x002;
    public const int CssX96 = 0x004;
    public const int CssXch = 0x008;
    public const int CssMask = 0x00f;
    public const int ExssCore = 0x010;
    public const int ExssXbr = 0x020;
    public const int ExssXxch = 0x040;
    public const int ExssX96 = 0x080;
    public const int ExssLbr = 0x100;
    public const int ExssXll = 0x200;
    public const int ExssRsv1 = 0x400;
    public const int ExssRsv2 = 0x800;
    public const int ExssMask = 0xff0;
}

/// <summary><c>enum DCADownMixType</c>.</summary>
internal enum DcaDownMixType
{
    Type1_0,
    TypeLoRo,
    TypeLtRt,
    Type3_0,
    Type2_1,
    Type2_2,
    Type3_1,

    Count,
}

/// <summary><c>enum DCACoreAudioMode</c>.</summary>
internal enum DcaCoreAudioMode
{
    /// <summary>Mode 0: A (mono).</summary>
    Mono,
    /// <summary>Mode 1: A + B (dual mono).</summary>
    MonoDual,
    /// <summary>Mode 2: L + R (stereo).</summary>
    Stereo,
    /// <summary>Mode 3: (L+R) + (L-R) (sum-diff).</summary>
    StereoSumDiff,
    /// <summary>Mode 4: LT + RT (left and right total).</summary>
    StereoTotal,
    /// <summary>Mode 5: C + L + R.</summary>
    Mode3F,
    /// <summary>Mode 6: L + R + S.</summary>
    Mode2F1R,
    /// <summary>Mode 7: C + L + R + S.</summary>
    Mode3F1R,
    /// <summary>Mode 8: L + R + SL + SR.</summary>
    Mode2F2R,
    /// <summary>Mode 9: C + L + R + SL + SR.</summary>
    Mode3F2R,

    Count,
}

/// <summary><c>enum DCACoreExtAudioType</c>.</summary>
internal static class DcaCoreExtAudioType
{
    public const int Xch = 0;
    public const int X96 = 2;
    public const int Xxch = 6;
}

/// <summary><c>enum DCACoreLFEFlag</c>.</summary>
internal static class DcaCoreLfeFlag
{
    public const int None = 0;
    public const int Lfe128 = 1;
    public const int Lfe64 = 2;
    public const int Invalid = 3;
}

/// <summary>
/// <c>DCACoreFrameHeader</c> - the 18 bytes at the head of every core substream frame.
/// </summary>
/// <remarks>
/// A struct, and the fields in the C's declared order. Several of them are flags the core
/// decoder copies straight into its own context, and keeping the order means the copy in
/// <c>ff_dca_core_parse</c> reads top to bottom in both languages.
/// </remarks>
internal struct DcaCoreFrameHeader
{
    /// <summary>Frame type.</summary>
    public byte NormalFrame;
    /// <summary>Deficit sample count.</summary>
    public byte DeficitSamples;
    /// <summary>CRC present flag.</summary>
    public byte CrcPresent;
    /// <summary>Number of PCM sample blocks.</summary>
    public byte Npcmblocks;
    /// <summary>Primary frame byte size.</summary>
    public ushort FrameSize;
    /// <summary>Audio channel arrangement.</summary>
    public byte AudioMode;
    /// <summary>Core audio sampling frequency.</summary>
    public byte SrCode;
    /// <summary>Transmission bit rate.</summary>
    public byte BrCode;
    /// <summary>Embedded dynamic range flag.</summary>
    public byte DrcPresent;
    /// <summary>Embedded time stamp flag.</summary>
    public byte TsPresent;
    /// <summary>Auxiliary data flag.</summary>
    public byte AuxPresent;
    /// <summary>HDCD mastering flag.</summary>
    public byte HdcdMaster;
    /// <summary>Extension audio descriptor flag.</summary>
    public byte ExtAudioType;
    /// <summary>Extended coding flag.</summary>
    public byte ExtAudioPresent;
    /// <summary>Audio sync word insertion flag.</summary>
    public byte SyncSsf;
    /// <summary>Low frequency effects flag.</summary>
    public byte LfePresent;
    /// <summary>Predictor history flag switch.</summary>
    public byte PredictorHistory;
    /// <summary>Multirate interpolator switch.</summary>
    public byte FilterPerfect;
    /// <summary>Encoder software revision.</summary>
    public byte EncoderRev;
    /// <summary>Copy history.</summary>
    public byte CopyHist;
    /// <summary>Source PCM resolution.</summary>
    public byte PcmrCode;
    /// <summary>Front sum/difference flag.</summary>
    public byte SumdiffFront;
    /// <summary>Surround sum/difference flag.</summary>
    public byte SumdiffSurround;
    /// <summary>Dialog normalization / unspecified.</summary>
    public byte DnCode;
}

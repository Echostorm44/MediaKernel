// Translated from lame-3.100/libmp3lame/encoder.h and machine.h.
//
// FFmpeg has no native MP3 encoder - libavcodec/mpegaudioenc.c is layer II only - so this
// encoder is a translation of LAME 3.100, the library ffmpeg's libmp3lame.c wraps. The gate is
// byte equality with `ffmpeg -c:a libmp3lame`; see the port checklist, section "mp3enc".
//
// PRECISION. LAME's FLOAT is a 32-bit float and its FLOAT8 is a 64-bit double
// (machine.h:131,141). Every variable here keeps the width the C gives it. Widening a FLOAT to
// double changes the output, and nothing but the gate would notice.

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary>
/// Layout and delay constants from <c>encoder.h</c>, under their C names.
/// </summary>
internal static class LameConstants
{
    /// <summary><c>ENCDELAY</c> - the encoder's look-ahead, in samples.</summary>
    public const int EncDelay = 576;

    /// <summary><c>POSTDELAY</c> - samples appended so the decoder can drain the filterbank.</summary>
    public const int PostDelay = 1152;

    /// <summary><c>MDCTDELAY</c>.</summary>
    public const int MdctDelay = 48;

    /// <summary><c>FFTOFFSET</c> - <c>224 + MDCTDELAY</c>.</summary>
    public const int FftOffset = 224 + MdctDelay;

    /// <summary><c>DECDELAY</c> - the decoder's own delay, which the LAME tag reports.</summary>
    public const int DecDelay = 528;

    /// <summary><c>CBANDS</c> - partition bands in the psychoacoustic model.</summary>
    public const int CBands = 64;

    /// <summary><c>SBPSY_l</c>.</summary>
    public const int SbPsyL = 21;

    /// <summary><c>SBPSY_s</c>.</summary>
    public const int SbPsyS = 12;

    /// <summary><c>SBMAX_l</c> - scalefactor bands, long blocks.</summary>
    public const int SbMaxL = 22;

    /// <summary><c>SBMAX_s</c> - scalefactor bands, short blocks.</summary>
    public const int SbMaxS = 13;

    /// <summary><c>PSFB21</c>.</summary>
    public const int Psfb21 = 6;

    /// <summary><c>PSFB12</c>.</summary>
    public const int Psfb12 = 6;

    /// <summary><c>SFBMAX</c> - <c>SBMAX_s * 3</c>, the widest scalefactor array either block type needs.</summary>
    public const int SfbMax = SbMaxS * 3;

    /// <summary><c>BLKSIZE</c> - long-block FFT length.</summary>
    public const int BlkSize = 1024;

    /// <summary><c>HBLKSIZE</c> - <c>BLKSIZE / 2 + 1</c>.</summary>
    public const int HBlkSize = BlkSize / 2 + 1;

    /// <summary><c>BLKSIZE_s</c> - short-block FFT length.</summary>
    public const int BlkSizeS = 256;

    /// <summary><c>HBLKSIZE_s</c> - <c>BLKSIZE_s / 2 + 1</c>.</summary>
    public const int HBlkSizeS = BlkSizeS / 2 + 1;

    /// <summary>Granules per frame, and MDCT coefficients per granule.</summary>
    public const int GranulesPerFrame = 2;

    /// <summary>MDCT coefficients in one granule - <c>xr[576]</c>.</summary>
    public const int CoefficientsPerGranule = 576;

    /// <summary><c>SBLIMIT</c> - subbands out of the polyphase filterbank.</summary>
    public const int SbLimit = 32;
}

/// <summary>
/// <c>block_type</c> values - <c>encoder.h</c> <c>NORM_TYPE</c> and friends.
/// </summary>
internal static class LameBlockType
{
    /// <summary><c>NORM_TYPE</c>.</summary>
    public const int Norm = 0;

    /// <summary><c>START_TYPE</c>.</summary>
    public const int Start = 1;

    /// <summary><c>SHORT_TYPE</c>.</summary>
    public const int Short = 2;

    /// <summary><c>STOP_TYPE</c>.</summary>
    public const int Stop = 3;
}

/// <summary>
/// <c>mode_ext</c> values - <c>encoder.h</c> <c>MPG_MD_*</c>.
/// </summary>
internal static class LameModeExt
{
    /// <summary><c>MPG_MD_LR_LR</c> - plain left/right.</summary>
    public const int LrLr = 0;

    /// <summary><c>MPG_MD_LR_I</c>.</summary>
    public const int LrI = 1;

    /// <summary><c>MPG_MD_MS_LR</c> - mid/side.</summary>
    public const int MsLr = 2;

    /// <summary><c>MPG_MD_MS_I</c>.</summary>
    public const int MsI = 3;
}

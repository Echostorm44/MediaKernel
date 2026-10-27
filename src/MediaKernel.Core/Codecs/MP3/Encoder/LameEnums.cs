// Translated from lame-3.100/include/lame.h and libmp3lame/lame_global_flags.h.
// The C names are kept as doc comments because libmp3lame.c names these values directly.

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>vbr_mode_e</c> - which rate control the encoder runs.</summary>
internal enum VbrMode
{
    /// <summary><c>vbr_off</c> - constant bitrate.</summary>
    Off = 0,

    /// <summary><c>vbr_mt</c> - obsolete, the same as <see cref="Mtrh"/>.</summary>
    Mt = 1,

    /// <summary><c>vbr_rh</c>.</summary>
    Rh = 2,

    /// <summary><c>vbr_abr</c> - average bitrate. ffmpeg selects this for -abr 1.</summary>
    Abr = 3,

    /// <summary><c>vbr_mtrh</c>. Also <c>vbr_default</c>, which is what ffmpeg passes for VBR.</summary>
    Mtrh = 4,

    /// <summary><c>vbr_max_indicator</c> - a bound for sanity checks, never a mode.</summary>
    MaxIndicator = 5,

    /// <summary><c>vbr_default</c> - an alias of <see cref="Mtrh"/>, not a distinct value.</summary>
    Default = Mtrh,
}

/// <summary><c>MPEG_mode_e</c> - the frame header's channel mode.</summary>
internal enum MpegMode
{
    /// <summary><c>STEREO</c>.</summary>
    Stereo = 0,

    /// <summary><c>JOINT_STEREO</c> - ffmpeg's default for more than one channel.</summary>
    JointStereo = 1,

    /// <summary><c>DUAL_CHANNEL</c> - LAME does not support this.</summary>
    DualChannel = 2,

    /// <summary><c>MONO</c>.</summary>
    Mono = 3,

    /// <summary><c>NOT_SET</c> - the initial value; lame_init_params resolves it.</summary>
    NotSet = 4,

    /// <summary><c>MAX_INDICATOR</c> - a bound for sanity checks, never a mode.</summary>
    MaxIndicator = 5,
}

/// <summary><c>short_block_e</c> - how freely the encoder may switch to short blocks.</summary>
internal enum ShortBlock
{
    /// <summary><c>short_block_not_set</c> - let LAME decide.</summary>
    NotSet = -1,

    /// <summary><c>short_block_allowed</c> - permitted, and L and R may differ.</summary>
    Allowed = 0,

    /// <summary><c>short_block_coupled</c> - permitted, but L and R must match.</summary>
    Coupled = 1,

    /// <summary><c>short_block_dispensed</c> - long blocks only.</summary>
    Dispensed = 2,

    /// <summary><c>short_block_forced</c> - short blocks only.</summary>
    Forced = 3,
}

/// <summary><c>Padding_type</c> (<c>lame.h</c>) - obsolete; LAME always adjusts.</summary>
internal enum PaddingType
{
    /// <summary><c>PAD_NO</c>.</summary>
    PadNo = 0,

    /// <summary><c>PAD_ALL</c>.</summary>
    PadAll = 1,

    /// <summary><c>PAD_ADJUST</c> - the only behaviour lame-3.100 has.</summary>
    PadAdjust = 2,

    /// <summary><c>PAD_MAX_INDICATOR</c> - a sanity bound, never a setting.</summary>
    MaxIndicator = 3,
}


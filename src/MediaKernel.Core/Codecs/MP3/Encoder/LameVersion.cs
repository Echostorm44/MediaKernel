// Translated from lame-3.100/libmp3lame/version.c and version.h.
//
// Every string here is the C's preprocessor expansion for the pinned release, 3.100, resolved
// once rather than reproduced with #if: LAME_TYPE_VERSION is 2 (release) and LAME_PATCH_VERSION
// is 0, so LAME_RELEASE_VERSION is true and LAME_PATCH_VERSION > 0 is false, and the versions
// are not conditional on anything this port can change. The expansions are spelled out in the
// remarks so that re-pinning LAME is a visible edit rather than a silent drift.
//
// get_lame_short_version is not here: the frame-level code needs it at compile time, so it is
// a const on LameBitstream, and this class returns the same string.

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>lame_version_t</c> (<c>lame.h</c>).</summary>
internal struct LameVersionT
{
    public int Major;
    public int Minor;
    public int Alpha;
    public int Beta;

    public int PsyMajor;
    public int PsyMinor;
    public int PsyAlpha;
    public int PsyBeta;

    /// <summary><c>features</c> - the C calls this obsolete and always sets it empty.</summary>
    public string Features;
}

/// <summary><c>version.c</c> and the macros of <c>version.h</c>.</summary>
internal static class LameVersion
{
    /// <summary><c>LAME_MAJOR_VERSION</c>.</summary>
    public const int MajorVersion = 3;

    /// <summary><c>LAME_MINOR_VERSION</c>.</summary>
    public const int MinorVersion = 100;

    /// <summary><c>LAME_TYPE_VERSION</c> - 0 alpha, 1 beta, 2 release.</summary>
    public const int TypeVersion = 2;

    /// <summary><c>LAME_PATCH_VERSION</c>.</summary>
    public const int PatchVersion = 0;

    /// <summary><c>LAME_URL</c>.</summary>
    public const string Url = "http://lame.sf.net";

    /// <summary><c>PSY_MAJOR_VERSION</c>.</summary>
    public const int PsyMajorVersion = 1;

    /// <summary><c>PSY_MINOR_VERSION</c>.</summary>
    public const int PsyMinorVersion = 0;

    /// <summary><c>PSY_ALPHA_VERSION</c>.</summary>
    public const int PsyAlphaVersion = 0;

    /// <summary><c>PSY_BETA_VERSION</c>.</summary>
    public const int PsyBetaVersion = 0;

    /// <summary><c>get_lame_version</c>.</summary>
    /// <remarks>
    /// The release branch with no patch level, so <c>"3" "." "100"</c>. The alpha and beta
    /// branches embed <c>__DATE__</c> and <c>__TIME__</c>, which is why the C comments that
    /// they make output validation harder - and why this port would not be able to reproduce
    /// them even if the pinned version were one.
    /// </remarks>
    public static string GetLameVersion() => "3.100";

    /// <summary><c>get_lame_short_version</c>.</summary>
    public static string GetLameShortVersion() => LameBitstream.ShortVersion;

    /// <summary><c>get_lame_very_short_version</c> - for the LAME VBR tag.</summary>
    /// <remarks>
    /// <c>"LAME" "3" "." "100" P</c> with <c>P</c> a single space on the release branch, so
    /// the trailing space is part of the string and not a typo.
    /// </remarks>
    public static string GetLameVeryShortVersion() => "LAME3.100 ";

    /// <summary><c>get_lame_tag_encoder_short_version</c> - at most nine characters.</summary>
    /// <remarks>
    /// The same expansion as <see cref="GetLameVeryShortVersion"/> on this branch; the C keeps
    /// them separate because the tag one must never grow past nine characters and must start
    /// with "LAME", which some hardware decoders rely on.
    /// </remarks>
    public static string GetLameTagEncoderShortVersion() => "LAME3.100 ";

    /// <summary><c>get_psy_version</c>.</summary>
    public static string GetPsyVersion() => "1.0";

    /// <summary><c>get_lame_url</c>.</summary>
    public static string GetLameUrl() => Url;

    /// <summary><c>get_lame_version_numerical</c>.</summary>
    public static void GetLameVersionNumerical(out LameVersionT lvp)
    {
        lvp = default;

        // generic version
        lvp.Major = MajorVersion;
        lvp.Minor = MinorVersion;
        lvp.Alpha = 0;
        lvp.Beta = 0;

        // psy version
        lvp.PsyMajor = PsyMajorVersion;
        lvp.PsyMinor = PsyMinorVersion;
        lvp.PsyAlpha = PsyAlphaVersion;
        lvp.PsyBeta = PsyBetaVersion;

        // compile time features; the C calls this obsolete and always sets it empty
        lvp.Features = "";
    }

    /// <summary><c>get_lame_os_bitness</c>.</summary>
    /// <remarks>
    /// The C switches on <c>sizeof(void *)</c>, which is the pointer width of the build. The
    /// managed equivalent is <see cref="System.IntPtr.Size"/>, and the default arm is kept
    /// even though nothing but 4 and 8 can reach it.
    /// </remarks>
    public static string GetLameOsBitness() => System.IntPtr.Size switch
    {
        4 => "32bits",
        8 => "64bits",
        _ => "",
    };
}

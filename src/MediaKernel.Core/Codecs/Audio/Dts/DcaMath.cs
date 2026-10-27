// DTS Coherent Acoustics - fixed point arithmetic.
//
// Port of ffmpeg-master/libavcodec/dcamath.h. These are the whole reason the DTS core can be
// decoded bit-exactly: every dequantisation, prediction and filter step in dca_core.c goes
// through one of them, and each one rounds. Getting the rounding wrong is not a small error
// that averages out - the ADPCM predictor feeds its own output back in, so a half-LSB
// difference compounds across a frame.

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary>
/// <c>dcamath.h</c> - rounding fixed point helpers shared by every part of the DTS decoder.
/// </summary>
internal static class DcaMath
{
    /// <summary>
    /// <c>norm__</c> - shift <paramref name="a"/> right by <paramref name="bits"/>, rounding to
    /// nearest, and narrow to 32 bits.
    /// </summary>
    /// <remarks>
    /// The rounding is "add half an LSB, then shift", which for a negative number rounds towards
    /// positive infinity rather than towards zero. An arithmetic shift of a negative value is
    /// floor division in C and in C#, which is what makes the plain shift correct here; a divide
    /// would truncate towards zero instead and be wrong for exactly half the inputs.
    /// <para>
    /// A non-positive <paramref name="bits"/> is not an error and does not shift left: the C
    /// returns the low 32 bits of <paramref name="a"/> unchanged, and callers rely on that for
    /// the shift == 0 case in <c>ff_dca_core_dequantize</c>.
    /// </para>
    /// </remarks>
    public static int Norm(long a, int bits)
        => bits > 0 ? (int)((a + (1L << (bits - 1))) >> bits) : (int)a;

    /// <summary><c>mul__</c> - multiply in 64 bits and round back down to 32.</summary>
    public static int Mul(int a, int b, int bits) => Norm((long)a * b, bits);

    public static int Norm13(long a) => Norm(a, 13);

    public static int Norm16(long a) => Norm(a, 16);

    public static int Norm20(long a) => Norm(a, 20);

    public static int Norm21(long a) => Norm(a, 21);

    public static int Norm23(long a) => Norm(a, 23);

    public static int Mul15(int a, int b) => Mul(a, b, 15);

    public static int Mul16(int a, int b) => Mul(a, b, 16);

    public static int Mul17(int a, int b) => Mul(a, b, 17);

    public static int Mul22(int a, int b) => Mul(a, b, 22);

    public static int Mul23(int a, int b) => Mul(a, b, 23);

    public static int Mul31(int a, int b) => Mul(a, b, 31);

    public static int Mul32(int a, int b) => Mul(a, b, 32);

    /// <summary>
    /// <c>clip23</c> - <c>av_clip_intp2(a, 23)</c>, the saturation the core's sample path uses.
    /// </summary>
    /// <remarks>
    /// Saturating, not wrapping: 24 bits signed, so [-8388608, 8388607]. The single-comparison
    /// form is FFmpeg's own - if the value needs no clipping its top nine bits are all copies of
    /// the sign bit, so <c>(a + 0x800000) &gt;&gt; 24</c> is zero.
    /// </remarks>
    public static int Clip23(int a)
        => ((a + 0x800000) >> 24) != 0 ? (a >> 31) ^ 0x7FFFFF : a;
}

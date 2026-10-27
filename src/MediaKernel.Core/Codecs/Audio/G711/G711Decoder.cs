// G.711 A-law and μ-law audio decoder
// Reference: ITU-T G.711, VLC modules/codec/g711.c
// G.711 is used in telephony and VoIP - simple 8-bit companded audio

namespace MediaKernel.Core.Codecs.Audio.G711;

/// <summary>
/// G.711 companding law type.
/// </summary>
public enum G711Law
{
    /// <summary>μ-law (North America, Japan) - ITU-T G.711 Annex 1.</summary>
    MuLaw,

    /// <summary>A-law (Europe, international) - ITU-T G.711 Annex 2.</summary>
    ALaw
}

/// <summary>
/// G.711 audio decoder supporting both A-law and μ-law companding.
/// Decodes 8-bit companded samples to 16-bit linear PCM.
/// </summary>
/// <remarks>
/// G.711 is a simple table-lookup codec. Each 8-bit input byte maps to a 16-bit output sample.
/// No state is maintained between blocks - each sample is independent.
/// Commonly used at 8kHz mono for telephony, but supports any sample rate and channel count.
/// </remarks>
public sealed class G711Decoder
{
    private readonly G711Law law;

    /// <summary>Gets the companding law used by this decoder.</summary>
    public G711Law Law => law;

    /// <summary>
    /// Creates a new G.711 decoder.
    /// </summary>
    /// <param name="law">The companding law to use for decoding.</param>
    public G711Decoder(G711Law law)
    {
        this.law = law;
    }

    /// <summary>
    /// Decodes G.711 compressed audio to 16-bit linear PCM.
    /// </summary>
    /// <param name="source">G.711 encoded bytes (one byte per sample).</param>
    /// <param name="destination">Output buffer for 16-bit PCM samples. Must be at least source.Length samples.</param>
    /// <returns>Number of samples decoded.</returns>
    public int Decode(ReadOnlySpan<byte> source, Span<short> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination buffer too small", nameof(destination));

        ReadOnlySpan<short> table = law == G711Law.MuLaw ? MuLawToLinear : ALawToLinear;

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = table[source[i]];
        }

        return source.Length;
    }

    /// <summary>
    /// Decodes G.711 compressed audio to 32-bit floating point PCM.
    /// </summary>
    /// <param name="source">G.711 encoded bytes (one byte per sample).</param>
    /// <param name="destination">Output buffer for float samples (-1.0 to 1.0). Must be at least source.Length samples.</param>
    /// <returns>Number of samples decoded.</returns>
    public int DecodeFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination buffer too small", nameof(destination));

        ReadOnlySpan<short> table = law == G711Law.MuLaw ? MuLawToLinear : ALawToLinear;
        const float scale = 1.0f / 32768.0f;

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = table[source[i]] * scale;
        }

        return source.Length;
    }

    /// <summary>
    /// Encodes 16-bit linear PCM to G.711 compressed audio.
    /// </summary>
    /// <param name="source">16-bit PCM samples to encode.</param>
    /// <param name="destination">Output buffer for G.711 bytes. Must be at least source.Length bytes.</param>
    /// <returns>Number of bytes encoded.</returns>
    public int Encode(ReadOnlySpan<short> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination buffer too small", nameof(destination));

        if (law == G711Law.MuLaw)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = EncodeMuLaw(source[i]);
            }
        }
        else
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = EncodeALaw(source[i]);
            }
        }

        return source.Length;
    }

    /// <summary>
    /// Calculates the required output buffer size for decoding.
    /// </summary>
    /// <param name="inputBytes">Number of G.711 input bytes.</param>
    /// <returns>Number of 16-bit samples that will be produced.</returns>
    public static int GetDecodedSampleCount(int inputBytes) => inputBytes;

    /// <summary>
    /// Calculates the required output buffer size for encoding.
    /// </summary>
    /// <param name="inputSamples">Number of 16-bit input samples.</param>
    /// <returns>Number of G.711 bytes that will be produced.</returns>
    public static int GetEncodedByteCount(int inputSamples) => inputSamples;

    #region μ-law Encoding

    private static byte EncodeMuLaw(short sample)
    {
        // BIAS for μ-law encoding
        const int Bias = 0x84;
        const int ClipValue = 32635;

        int sign = (sample >> 8) & 0x80;
        if (sign != 0)
            sample = (short)-sample;

        if (sample > ClipValue)
            sample = ClipValue;

        sample = (short)(sample + Bias);

        int exponent = MuLawExponentLookup[(sample >> 7) & 0xFF];
        int mantissa = (sample >> (exponent + 3)) & 0x0F;

        byte encoded = (byte)(~(sign | (exponent << 4) | mantissa));
        return encoded;
    }

    private static readonly byte[] MuLawExponentLookup =
    [
        0, 0, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7
    ];

    #endregion

    #region A-law Encoding

    private static byte EncodeALaw(short sample)
    {
        // A-law: sign bit is 1 for positive, 0 for negative (inverted from usual)
        int sign;
        if (sample >= 0)
        {
            sign = 0x80; // Positive: sign bit = 1 (after XOR becomes 0)
        }
        else
        {
            sign = 0x00; // Negative: sign bit = 0 (after XOR becomes 1)
            sample = (short)-sample;
        }

        if (sample > 32767)
            sample = 32767;

        int exponent;
        int mantissa;

        if (sample < 256)
        {
            exponent = 0;
            mantissa = sample >> 4;
        }
        else
        {
            exponent = ALawExponentLookup[(sample >> 8) & 0x7F];
            mantissa = (sample >> (exponent + 3)) & 0x0F;
        }

        byte encoded = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)(encoded ^ 0xD5); // Toggle even bits
    }

    private static readonly byte[] ALawExponentLookup =
    [
        1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7
    ];

    #endregion

    #region Decoding Lookup Tables

    /// <summary>
    /// μ-law to 16-bit linear PCM lookup table (256 entries).
    /// ITU-T G.711 Annex 1.
    /// </summary>
    private static readonly short[] MuLawToLinear =
    [
        -32124, -31100, -30076, -29052, -28028, -27004, -25980, -24956,
        -23932, -22908, -21884, -20860, -19836, -18812, -17788, -16764,
        -15996, -15484, -14972, -14460, -13948, -13436, -12924, -12412,
        -11900, -11388, -10876, -10364,  -9852,  -9340,  -8828,  -8316,
         -7932,  -7676,  -7420,  -7164,  -6908,  -6652,  -6396,  -6140,
         -5884,  -5628,  -5372,  -5116,  -4860,  -4604,  -4348,  -4092,
         -3900,  -3772,  -3644,  -3516,  -3388,  -3260,  -3132,  -3004,
         -2876,  -2748,  -2620,  -2492,  -2364,  -2236,  -2108,  -1980,
         -1884,  -1820,  -1756,  -1692,  -1628,  -1564,  -1500,  -1436,
         -1372,  -1308,  -1244,  -1180,  -1116,  -1052,   -988,   -924,
          -876,   -844,   -812,   -780,   -748,   -716,   -684,   -652,
          -620,   -588,   -556,   -524,   -492,   -460,   -428,   -396,
          -372,   -356,   -340,   -324,   -308,   -292,   -276,   -260,
          -244,   -228,   -212,   -196,   -180,   -164,   -148,   -132,
          -120,   -112,   -104,    -96,    -88,    -80,    -72,    -64,
           -56,    -48,    -40,    -32,    -24,    -16,     -8,      0,
         32124,  31100,  30076,  29052,  28028,  27004,  25980,  24956,
         23932,  22908,  21884,  20860,  19836,  18812,  17788,  16764,
         15996,  15484,  14972,  14460,  13948,  13436,  12924,  12412,
         11900,  11388,  10876,  10364,   9852,   9340,   8828,   8316,
          7932,   7676,   7420,   7164,   6908,   6652,   6396,   6140,
          5884,   5628,   5372,   5116,   4860,   4604,   4348,   4092,
          3900,   3772,   3644,   3516,   3388,   3260,   3132,   3004,
          2876,   2748,   2620,   2492,   2364,   2236,   2108,   1980,
          1884,   1820,   1756,   1692,   1628,   1564,   1500,   1436,
          1372,   1308,   1244,   1180,   1116,   1052,    988,    924,
           876,    844,    812,    780,    748,    716,    684,    652,
           620,    588,    556,    524,    492,    460,    428,    396,
           372,    356,    340,    324,    308,    292,    276,    260,
           244,    228,    212,    196,    180,    164,    148,    132,
           120,    112,    104,     96,     88,     80,     72,     64,
            56,     48,     40,     32,     24,     16,      8,      0
    ];

    /// <summary>
    /// A-law to 16-bit linear PCM lookup table (256 entries).
    /// ITU-T G.711 Annex 2.
    /// </summary>
    private static readonly short[] ALawToLinear =
    [
         -5504,  -5248,  -6016,  -5760,  -4480,  -4224,  -4992,  -4736,
         -7552,  -7296,  -8064,  -7808,  -6528,  -6272,  -7040,  -6784,
         -2752,  -2624,  -3008,  -2880,  -2240,  -2112,  -2496,  -2368,
         -3776,  -3648,  -4032,  -3904,  -3264,  -3136,  -3520,  -3392,
        -22016, -20992, -24064, -23040, -17920, -16896, -19968, -18944,
        -30208, -29184, -32256, -31232, -26112, -25088, -28160, -27136,
        -11008, -10496, -12032, -11520,  -8960,  -8448,  -9984,  -9472,
        -15104, -14592, -16128, -15616, -13056, -12544, -14080, -13568,
          -344,   -328,   -376,   -360,   -280,   -264,   -312,   -296,
          -472,   -456,   -504,   -488,   -408,   -392,   -440,   -424,
           -88,    -72,   -120,   -104,    -24,     -8,    -56,    -40,
          -216,   -200,   -248,   -232,   -152,   -136,   -184,   -168,
         -1376,  -1312,  -1504,  -1440,  -1120,  -1056,  -1248,  -1184,
         -1888,  -1824,  -2016,  -1952,  -1632,  -1568,  -1760,  -1696,
          -688,   -656,   -752,   -720,   -560,   -528,   -624,   -592,
          -944,   -912,  -1008,   -976,   -816,   -784,   -880,   -848,
          5504,   5248,   6016,   5760,   4480,   4224,   4992,   4736,
          7552,   7296,   8064,   7808,   6528,   6272,   7040,   6784,
          2752,   2624,   3008,   2880,   2240,   2112,   2496,   2368,
          3776,   3648,   4032,   3904,   3264,   3136,   3520,   3392,
         22016,  20992,  24064,  23040,  17920,  16896,  19968,  18944,
         30208,  29184,  32256,  31232,  26112,  25088,  28160,  27136,
         11008,  10496,  12032,  11520,   8960,   8448,   9984,   9472,
         15104,  14592,  16128,  15616,  13056,  12544,  14080,  13568,
           344,    328,    376,    360,    280,    264,    312,    296,
           472,    456,    504,    488,    408,    392,    440,    424,
            88,     72,    120,    104,     24,      8,     56,     40,
           216,    200,    248,    232,    152,    136,    184,    168,
          1376,   1312,   1504,   1440,   1120,   1056,   1248,   1184,
          1888,   1824,   2016,   1952,   1632,   1568,   1760,   1696,
           688,    656,    752,    720,    560,    528,    624,    592,
           944,    912,   1008,    976,    816,    784,    880,    848
    ];

    #endregion
}

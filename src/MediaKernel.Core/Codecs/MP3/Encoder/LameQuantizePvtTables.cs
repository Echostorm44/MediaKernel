// Translated from lame-3.100/libmp3lame/quantize_pvt.c - sfBandIndex, the scalefactor band
// boundaries from tables B.2 and B.8 of the standard, plus the excerpt of lame_init_params that
// copies the right row into gfc->scalefac_band.
//
// GENERATED, not typed, by F:/Code/mkprobe/lamesfband.cs. Several short-block rows are written
// in the C as divisions - `0 / 3, 12 / 3, ...` - and are emitted here as the same expressions so
// that C# folds them with the same integer division.

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary>One row of <c>sfBandIndex</c> - a <c>scalefac_struct</c> in the C.</summary>
internal readonly struct SfBandRow
{
    /// <summary><c>l[1 + SBMAX_l]</c>.</summary>
    public required int[] L { get; init; }

    /// <summary><c>s[1 + SBMAX_s]</c>.</summary>
    public required int[] S { get; init; }

    /// <summary><c>psfb21[1 + PSFB21]</c>.</summary>
    public required int[] Psfb21 { get; init; }

    /// <summary><c>psfb12[1 + PSFB12]</c>.</summary>
    public required int[] Psfb12 { get; init; }
}

/// <summary><c>quantize_pvt.c</c>'s tables.</summary>
internal static class LameQuantizePvtTables
{
    /// <summary>
    /// <c>sfBandIndex[9]</c> - indexed as
    /// <c>samplerate_index + 3 * version + 6 * (samplerate_out &lt; 16000)</c>.
    /// </summary>
    public static readonly SfBandRow[] SfBandIndex =
    [
        // Table B.2.b: 22.05 kHz
        new SfBandRow
        {
            L = [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
            S = [0, 4, 8, 12, 18, 24, 32, 42, 56, 74, 100, 132, 174, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // Table B.2.c: 24 kHz
        new SfBandRow
        {
            L = [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 114, 136, 162, 194, 232, 278, 332, 394, 464, 540, 576],
            S = [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 136, 180, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // Table B.2.a: 16 kHz
        new SfBandRow
        {
            L = [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
            S = [0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // Table B.8.b: 44.1 kHz
        new SfBandRow
        {
            L = [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 52, 62, 74, 90, 110, 134, 162, 196, 238, 288, 342, 418, 576],
            S = [0, 4, 8, 12, 16, 22, 30, 40, 52, 66, 84, 106, 136, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // Table B.8.c: 48 kHz
        new SfBandRow
        {
            L = [0, 4, 8, 12, 16, 20, 24, 30, 36, 42, 50, 60, 72, 88, 106, 128, 156, 190, 230, 276, 330, 384, 576],
            S = [0, 4, 8, 12, 16, 22, 28, 38, 50, 64, 80, 100, 126, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // Table B.8.a: 32 kHz
        new SfBandRow
        {
            L = [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 54, 66, 82, 102, 126, 156, 194, 240, 296, 364, 448, 550, 576],
            S = [0, 4, 8, 12, 16, 22, 30, 42, 58, 78, 104, 138, 180, 192],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // MPEG-2.5 11.025 kHz
        new SfBandRow
        {
            L = [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
            S = [0 / 3, 12 / 3, 24 / 3, 36 / 3, 54 / 3, 78 / 3, 108 / 3, 144 / 3, 186 / 3, 240 / 3, 312 / 3, 402 / 3, 522 / 3, 576 / 3],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // MPEG-2.5 12 kHz
        new SfBandRow
        {
            L = [0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576],
            S = [0 / 3, 12 / 3, 24 / 3, 36 / 3, 54 / 3, 78 / 3, 108 / 3, 144 / 3, 186 / 3, 240 / 3, 312 / 3, 402 / 3, 522 / 3, 576 / 3],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
        // MPEG-2.5 8 kHz
        new SfBandRow
        {
            L = [0, 12, 24, 36, 48, 60, 72, 88, 108, 132, 160, 192, 232, 280, 336, 400, 476, 566, 568, 570, 572, 574, 576],
            S = [0 / 3, 24 / 3, 48 / 3, 72 / 3, 108 / 3, 156 / 3, 216 / 3, 288 / 3, 372 / 3, 480 / 3, 486 / 3, 492 / 3, 498 / 3, 576 / 3],
            Psfb21 = [0, 0, 0, 0, 0, 0, 0],
            Psfb12 = [0, 0, 0, 0, 0, 0, 0],
        },
    ];

    /// <summary>
    /// The block of <c>lame_init_params</c> (<c>lame.c</c>) that fills
    /// <c>gfc-&gt;scalefac_band</c>, including the two pseudo sub-band tables it derives.
    /// </summary>
    /// <remarks>
    /// It lives here with the table it reads rather than waiting for the rest of
    /// <c>lame_init_params</c>, because <c>psymodel_init</c> cannot run without it.
    /// <c>LameInitParams</c> will call this when <c>lame.c</c> is translated.
    /// </remarks>
    public static void InitScalefacBand(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int i;
        int j = cfg.SamplerateIndex + 3 * cfg.Version + 6 * (cfg.SamplerateOut < 16000 ? 1 : 0);

        for (i = 0; i < LameConstants.SbMaxL + 1; i++)
            gfc.ScalefacBand.L[i] = SfBandIndex[j].L[i];

        for (i = 0; i < LameConstants.Psfb21 + 1; i++)
        {
            int size = (gfc.ScalefacBand.L[22] - gfc.ScalefacBand.L[21]) / LameConstants.Psfb21;
            int start = gfc.ScalefacBand.L[21] + i * size;
            gfc.ScalefacBand.Psfb21[i] = start;
        }
        gfc.ScalefacBand.Psfb21[LameConstants.Psfb21] = 576;

        for (i = 0; i < LameConstants.SbMaxS + 1; i++)
            gfc.ScalefacBand.S[i] = SfBandIndex[j].S[i];

        for (i = 0; i < LameConstants.Psfb12 + 1; i++)
        {
            int size = (gfc.ScalefacBand.S[13] - gfc.ScalefacBand.S[12]) / LameConstants.Psfb12;
            int start = gfc.ScalefacBand.S[12] + i * size;
            gfc.ScalefacBand.Psfb12[i] = start;
        }
        gfc.ScalefacBand.Psfb12[LameConstants.Psfb12] = 192;
    }
}

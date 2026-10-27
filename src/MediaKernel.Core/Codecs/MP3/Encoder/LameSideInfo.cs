// Translated from lame-3.100/libmp3lame/l3side.h - the Layer III side information.
//
// Fields are in the C's declared order, which the porting rules require: in this encoder the structs
// ARE the algorithm, and takehiro.c depends on their exact value semantics.
//
// WHY EVERY ARRAY HERE IS INLINE. takehiro.c copies a whole gr_info by value four times -
// `memcpy(gi, cod_info2, sizeof(gr_info))` at :871 and :955, and the reverse at :903 and :918 -
// and that copy includes both 576-element arrays. Holding xr or l3_enc as a C# array reference
// would turn those copies into aliases: the encoder would still run, still emit frames, and
// quietly produce different bytes. As plain inline arrays a C# struct assignment reproduces the
// memcpy exactly.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>scalefac_struct</c> - scalefactor band boundaries.</summary>
internal struct ScalefacStruct
{
    /// <summary><c>l[1 + SBMAX_l]</c>.</summary>
    public Int23 L;

    /// <summary><c>s[1 + SBMAX_s]</c>.</summary>
    public Int14 S;

    /// <summary><c>psfb21[1 + PSFB21]</c>.</summary>
    public Int7 Psfb21;

    /// <summary><c>psfb12[1 + PSFB12]</c>.</summary>
    public Int7 Psfb12;

    [InlineArray(1 + LameConstants.SbMaxL)] internal struct Int23 { private int element0; }

    [InlineArray(1 + LameConstants.SbMaxS)] internal struct Int14 { private int element0; }

    [InlineArray(1 + LameConstants.Psfb21)] internal struct Int7 { private int element0; }
}

/// <summary><c>III_psy_xmin</c> - a masking or energy threshold per scalefactor band.</summary>
internal struct IiiPsyXmin
{
    /// <summary><c>l[SBMAX_l]</c>.</summary>
    public Float22 L;

    /// <summary><c>s[SBMAX_s][3]</c> - flattened; index as <c>[sfb * 3 + window]</c>.</summary>
    public Float39 S;

    [InlineArray(LameConstants.SbMaxL)] internal struct Float22 { private float element0; }

    [InlineArray(LameConstants.SbMaxS * 3)] internal struct Float39 { private float element0; }
}

/// <summary><c>III_psy_ratio</c> - what the psychoacoustic model hands the quantiser.</summary>
internal struct IiiPsyRatio
{
    /// <summary><c>thm</c> - the masking threshold.</summary>
    public IiiPsyXmin Thm;

    /// <summary><c>en</c> - the signal energy.</summary>
    public IiiPsyXmin En;
}

/// <summary>
/// <c>gr_info</c> - one granule of one channel: its coefficients, its quantised output, and
/// every side-information field the bitstream will carry for it.
/// </summary>
/// <remarks>
/// A value type on purpose - see the file header. Roughly 5.3 KB, so it is passed by
/// <c>ref</c> everywhere except where the C itself takes a copy.
/// </remarks>
internal struct GrInfo
{
    /// <summary>
    /// What <see cref="SfbPartitionTableRow"/> holds when the C holds a NULL pointer. Zero is a
    /// real row, so it cannot double as the empty value.
    /// </summary>
    public const int NoSfbPartitionTable = -1;

    /// <summary><c>xr[576]</c> - the MDCT coefficients, FLOAT in the C.</summary>
    public Float576 Xr;

    /// <summary><c>l3_enc[576]</c> - the quantised coefficients.</summary>
    public Int576 L3Enc;

    /// <summary><c>scalefac[SFBMAX]</c>.</summary>
    public IntSfbMax Scalefac;

    /// <summary><c>xrpow_max</c>.</summary>
    public float XrpowMax;

    /// <summary><c>part2_3_length</c>.</summary>
    public int Part23Length;

    /// <summary><c>big_values</c>.</summary>
    public int BigValues;

    /// <summary><c>count1</c>.</summary>
    public int Count1;

    /// <summary><c>global_gain</c>.</summary>
    public int GlobalGain;

    /// <summary><c>scalefac_compress</c>.</summary>
    public int ScalefacCompress;

    /// <summary><c>block_type</c> - see <see cref="LameBlockType"/>.</summary>
    public int BlockType;

    /// <summary><c>mixed_block_flag</c>.</summary>
    public int MixedBlockFlag;

    /// <summary><c>table_select[3]</c>.</summary>
    public Int3 TableSelect;

    /// <summary><c>subblock_gain[3 + 1]</c>.</summary>
    public Int4 SubblockGain;

    /// <summary><c>region0_count</c>.</summary>
    public int Region0Count;

    /// <summary><c>region1_count</c>.</summary>
    public int Region1Count;

    /// <summary><c>preflag</c>.</summary>
    public int Preflag;

    /// <summary><c>scalefac_scale</c>.</summary>
    public int ScalefacScale;

    /// <summary><c>count1table_select</c>.</summary>
    public int Count1TableSelect;

    /// <summary><c>part2_length</c>.</summary>
    public int Part2Length;

    /// <summary><c>sfb_lmax</c>.</summary>
    public int SfbLmax;

    /// <summary><c>sfb_smin</c>.</summary>
    public int SfbSmin;

    /// <summary><c>psy_lmax</c>.</summary>
    public int PsyLmax;

    /// <summary><c>sfbmax</c>.</summary>
    public int Sfbmax;

    /// <summary><c>psymax</c>.</summary>
    public int Psymax;

    /// <summary><c>sfbdivide</c>.</summary>
    public int Sfbdivide;

    /// <summary><c>width[SFBMAX]</c>.</summary>
    public IntSfbMax Width;

    /// <summary><c>window[SFBMAX]</c>.</summary>
    public IntSfbMax Window;

    /// <summary><c>count1bits</c>.</summary>
    public int Count1Bits;

    /// <summary>
    /// <c>sfb_partition_table</c> - added for LSF. A <c>const int *</c> in the C, and it only
    /// ever points at a row of <c>nr_of_sfb_block[6][3][4]</c>, which is then indexed 0..3
    /// (<c>takehiro.c:1272</c>, <c>bitstream.c:749</c>). So the faithful translation is that
    /// row's identity: <c>table_number * 3 + row_in_table</c>, or
    /// <see cref="NoSfbPartitionTable"/> for the C's NULL, which two asserts check for.
    /// </summary>
    public int SfbPartitionTableRow;

    /// <summary><c>slen[4]</c>.</summary>
    public Int4 Slen;

    /// <summary><c>max_nonzero_coeff</c>.</summary>
    public int MaxNonzeroCoeff;

    /// <summary>
    /// <c>energy_above_cutoff[SFBMAX]</c> - a <c>char</c> in the C, used only as a 0/1 flag,
    /// so its signedness never shows.
    /// </summary>
    public ByteSfbMax EnergyAboveCutoff;

    [InlineArray(LameConstants.CoefficientsPerGranule)] internal struct Float576 { private float element0; }

    [InlineArray(LameConstants.CoefficientsPerGranule)] internal struct Int576 { private int element0; }

    [InlineArray(LameConstants.SfbMax)] internal struct IntSfbMax { private int element0; }

    [InlineArray(LameConstants.SfbMax)] internal struct ByteSfbMax { private byte element0; }

    [InlineArray(3)] internal struct Int3 { private int element0; }

    [InlineArray(4)] internal struct Int4 { private int element0; }
}

/// <summary><c>III_side_info_t</c> - the whole frame's side information.</summary>
internal struct IiiSideInfo
{
    /// <summary><c>tt[2][2]</c> - granule, then channel. Flattened; index as <c>[gr * 2 + ch]</c>.</summary>
    public GrInfoTt Tt;

    /// <summary><c>main_data_begin</c> - the bit reservoir's backward pointer.</summary>
    public int MainDataBegin;

    /// <summary><c>private_bits</c>.</summary>
    public int PrivateBits;

    /// <summary><c>resvDrain_pre</c>.</summary>
    public int ResvDrainPre;

    /// <summary><c>resvDrain_post</c>.</summary>
    public int ResvDrainPost;

    /// <summary><c>scfsi[2][4]</c> - per channel. Flattened; index as <c>[ch * 4 + band]</c>.</summary>
    public Int8 Scfsi;

    [InlineArray(4)] internal struct GrInfoTt { private GrInfo element0; }

    [InlineArray(8)] internal struct Int8 { private int element0; }
}

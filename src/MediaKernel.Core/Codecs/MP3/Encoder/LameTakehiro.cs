// Translated from lame-3.100/libmp3lame/takehiro.c - quantisation of xr^(3/4), choosing the
// Huffman table that codes a region in the fewest bits, and storing the scalefactors.
//
// TAKEHIRO_IEEE754_HACK, AND WHY THE ODD-LOOKING CODE IS THE POINT. quantize_lines_xrpow adds
// 2^23 * 2^7 to a double and stores it as a float. That forces the value's exponent, so the
// float's low mantissa bits are its integer part, and reading the bit pattern and subtracting
// MAGIC_INT gives the rounded integer without a conversion instruction. The C's own comment
// records that this replaced hand-written assembly in 2000. It is not an optimisation that can
// be swapped for Math.Round: the rounding mode, the tie behaviour and the interaction with
// adj43asm all come out of it, and adj43asm itself is built only when this path is compiled.
// Translating it as "convert to int" would change the encoder's output.
//
// The C's #else branch - XRPOW_FTOI, QUANTFAC, ROUNDFAC and the adj43 table - belongs to the
// machines where that trick does not work and is not the reference build; it is kept here
// behind the same flag, as LameQuantizePvt keeps the other adj43.
//
// choose_table is a function pointer in the C, assigned by huffman_init and pointing at an MMX
// kernel where one is built. It is called directly here, which is the project's standing
// decision on DSP indirection and is recorded in the port checklist.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>takehiro.c</c>.</summary>
internal static class LameTakehiro
{
    /// <summary><c>MAGIC_FLOAT</c> - <c>65536 * 128</c>, which is 2^23.</summary>
    private const double MagicFloat = 65536.0 * 128;

    /// <summary><c>MAGIC_INT</c> - the bit pattern of <see cref="MagicFloat"/>.</summary>
    private const int MagicInt = 0x4b000000;

    /// <summary><c>ROUNDFAC</c> - only used on the non-Takehiro path.</summary>
    private const float Roundfac = 0.4054f;

    /// <summary>
    /// <c>subdv_table[23]</c> - the region0 and region1 band counts for a given number of
    /// scalefactor bands, flattened; index as <c>[bands * 2 + which]</c>.
    /// </summary>
    private static readonly int[] subdvTable =
    [
        0, 0,  // 0 bands
        0, 0,  // 1 band
        0, 0,  // 2 bands
        0, 0,  // 3 bands
        0, 0,  // 4 bands
        0, 1,  // 5 bands
        1, 1,  // 6 bands
        1, 1,  // 7 bands
        1, 2,  // 8 bands
        2, 2,  // 9 bands
        2, 3,  // 10 bands
        2, 3,  // 11 bands
        3, 4,  // 12 bands
        3, 4,  // 13 bands
        3, 4,  // 14 bands
        4, 5,  // 15 bands
        4, 5,  // 16 bands
        4, 6,  // 17 bands
        5, 6,  // 18 bands
        5, 6,  // 19 bands
        5, 7,  // 20 bands
        6, 7,  // 21 bands
        6, 7,  // 22 bands
    ];

    /// <summary><c>huf_tbl_noESC[]</c> - the first table that can code a given maximum.</summary>
    private static readonly int[] hufTblNoEsc =
        [1, 2, 5, 7, 7, 10, 10, 13, 13, 13, 13, 13, 13, 13, 13];

    /// <summary><c>slen1_n[16]</c>.</summary>
    private static readonly int[] slen1N = [1, 1, 1, 1, 8, 2, 2, 2, 4, 4, 4, 8, 8, 8, 16, 16];

    /// <summary><c>slen2_n[16]</c>.</summary>
    private static readonly int[] slen2N = [1, 2, 4, 8, 1, 2, 4, 8, 2, 4, 8, 2, 4, 8, 4, 8];

    /// <summary><c>slen1_tab[16]</c>.</summary>
    public static readonly int[] Slen1Tab = [0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4];

    /// <summary><c>slen2_tab[16]</c>.</summary>
    public static readonly int[] Slen2Tab = [0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3];

    /// <summary><c>scale_short[16]</c> - <c>18 * slen1_tab[i] + 18 * slen2_tab[i]</c>.</summary>
    private static readonly int[] scaleShort =
        [0, 18, 36, 54, 54, 36, 54, 72, 54, 72, 90, 72, 90, 108, 108, 126];

    /// <summary><c>scale_mixed[16]</c> - <c>17 * slen1_tab[i] + 18 * slen2_tab[i]</c>.</summary>
    private static readonly int[] scaleMixed =
        [0, 18, 36, 54, 51, 35, 53, 71, 52, 70, 88, 69, 87, 105, 104, 122];

    /// <summary><c>scale_long[16]</c> - <c>11 * slen1_tab[i] + 10 * slen2_tab[i]</c>.</summary>
    private static readonly int[] scaleLong =
        [0, 10, 20, 30, 33, 21, 31, 41, 32, 42, 52, 43, 53, 63, 64, 74];

    /// <summary><c>max_range_sfac_tab[6][4]</c> - the largest scalefactor values for MPEG-2.</summary>
    private static readonly int[][] maxRangeSfacTab =
    [
        [15, 15, 7, 7],
        [15, 15, 7, 0],
        [7, 3, 0, 0],
        [15, 31, 31, 0],
        [7, 7, 7, 0],
        [3, 3, 0, 0],
    ];

    /// <summary><c>log2tab[]</c> of <c>mpeg2_scale_bitcount</c>.</summary>
    private static readonly int[] log2Tab = [0, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4];

    /// <summary>
    /// <c>quantize_lines_xrpow_01</c> - the lines known to quantise to 0 or 1.
    /// </summary>
    private static void QuantizeLinesXrpow01(
        int l, float istep, ReadOnlySpan<float> xr, Span<int> ix)
    {
        float compareval0 = (1.0f - 0.4054f) / istep;

        for (int i = 0; i < l; i += 2)
        {
            float xr0 = xr[i + 0];
            float xr1 = xr[i + 1];
            int ix0 = compareval0 > xr0 ? 0 : 1;
            int ix1 = compareval0 > xr1 ? 0 : 1;
            ix[i + 0] = ix0;
            ix[i + 1] = ix1;
        }
    }

    /// <summary>
    /// <c>quantize_lines_xrpow</c> - quantise a run of lines, rounding through the float's bits.
    /// </summary>
    /// <remarks>
    /// See the file header. Each value is scaled, offset by 2^23 so the exponent is pinned, and
    /// stored as a float; the mantissa's low bits are then the integer part, and
    /// <c>adj43asm</c> indexed by them gives the correction that puts the decision point at the
    /// midpoint of the reconstruction levels rather than of the inputs. The sums are double
    /// right up to the store, exactly as in the C, because <c>fi[k].f = x</c> is the only
    /// narrowing in the chain.
    /// </remarks>
    /// <remarks>
    /// Public rather than private only so that <c>mp3enc-takehiro</c> can drive it directly;
    /// it is <c>static</c> in the C and is called from nowhere else here.
    /// </remarks>
    public static void QuantizeLinesXrpow(
        int l, float istep, ReadOnlySpan<float> xp, Span<int> pi)
    {
        if (LameQuantizeConstants.TakehiroIeee754Hack)
        {
            int fi = 0;
            int xpi = 0;
            l >>= 1;
            int remaining = l % 2;
            l >>= 1;
            while (l-- != 0)
            {
                double x0 = istep * xp[xpi + 0];
                double x1 = istep * xp[xpi + 1];
                double x2 = istep * xp[xpi + 2];
                double x3 = istep * xp[xpi + 3];

                x0 += MagicFloat;
                int i0 = BitConverter.SingleToInt32Bits((float)x0);
                x1 += MagicFloat;
                int i1 = BitConverter.SingleToInt32Bits((float)x1);
                x2 += MagicFloat;
                int i2 = BitConverter.SingleToInt32Bits((float)x2);
                x3 += MagicFloat;
                int i3 = BitConverter.SingleToInt32Bits((float)x3);

                i0 = BitConverter.SingleToInt32Bits(
                    (float)(x0 + LameQuantizePvt.Adj43[i0 - MagicInt]));
                i1 = BitConverter.SingleToInt32Bits(
                    (float)(x1 + LameQuantizePvt.Adj43[i1 - MagicInt]));
                i2 = BitConverter.SingleToInt32Bits(
                    (float)(x2 + LameQuantizePvt.Adj43[i2 - MagicInt]));
                i3 = BitConverter.SingleToInt32Bits(
                    (float)(x3 + LameQuantizePvt.Adj43[i3 - MagicInt]));

                pi[fi + 0] = i0 - MagicInt;
                pi[fi + 1] = i1 - MagicInt;
                pi[fi + 2] = i2 - MagicInt;
                pi[fi + 3] = i3 - MagicInt;
                fi += 4;
                xpi += 4;
            }
            if (remaining != 0)
            {
                double x0 = istep * xp[xpi + 0];
                double x1 = istep * xp[xpi + 1];

                x0 += MagicFloat;
                int i0 = BitConverter.SingleToInt32Bits((float)x0);
                x1 += MagicFloat;
                int i1 = BitConverter.SingleToInt32Bits((float)x1);

                i0 = BitConverter.SingleToInt32Bits(
                    (float)(x0 + LameQuantizePvt.Adj43[i0 - MagicInt]));
                i1 = BitConverter.SingleToInt32Bits(
                    (float)(x1 + LameQuantizePvt.Adj43[i1 - MagicInt]));

                pi[fi + 0] = i0 - MagicInt;
                pi[fi + 1] = i1 - MagicInt;
            }
        }
        else
        {
            // The portable branch, for machines where the bit trick does not work. Kept for
            // completeness; the reference build does not take it.
            int xr = 0, ixi = 0;
            l >>= 1;
            int remaining = l % 2;
            l >>= 1;
            while (l-- != 0)
            {
                float x0, x1, x2, x3;
                int rx0, rx1, rx2, rx3;

                x0 = xp[xr++] * istep;
                x1 = xp[xr++] * istep;
                rx0 = (int)x0;
                x2 = xp[xr++] * istep;
                rx1 = (int)x1;
                x3 = xp[xr++] * istep;
                rx2 = (int)x2;
                x0 += LameQuantizePvt.Adj43[rx0];
                rx3 = (int)x3;
                x1 += LameQuantizePvt.Adj43[rx1];
                pi[ixi++] = (int)x0;
                x2 += LameQuantizePvt.Adj43[rx2];
                pi[ixi++] = (int)x1;
                x3 += LameQuantizePvt.Adj43[rx3];
                pi[ixi++] = (int)x2;
                pi[ixi++] = (int)x3;
            }
            if (remaining != 0)
            {
                float x0, x1;
                int rx0, rx1;

                x0 = xp[xr++] * istep;
                x1 = xp[xr++] * istep;
                rx0 = (int)x0;
                rx1 = (int)x1;
                x0 += LameQuantizePvt.Adj43[rx0];
                x1 += LameQuantizePvt.Adj43[rx1];
                pi[ixi++] = (int)x0;
                pi[ixi++] = (int)x1;
            }
            _ = Roundfac;
        }
    }

    /// <summary>
    /// <c>quantize_xrpow</c> - decide which lines to quantise and with which of the two kernels.
    /// </summary>
    /// <remarks>
    /// The C accumulates consecutive bands that take the same kernel and quantises them in one
    /// call, which is why <c>acc_xp</c> and <c>acc_iData</c> trail the loop pointers. The two
    /// trailing pointers are offsets here.
    /// </remarks>
    private static void QuantizeXrpow(
        ReadOnlySpan<float> xp, Span<int> pi, float istep,
        in GrInfo codInfo, CalcNoiseData? prevNoise)
    {
        int sfb;
        int sfbmax;
        int j = 0;
        bool prevDataUse;
        int iData = 0;
        int accumulate = 0;
        int accumulate01 = 0;
        int accIData = 0;
        int accXp = 0;
        int xpBase = 0;

        // Reusing previously computed data does not work when the global gain changes; the C
        // notes that finding out why would allow a cache and a noticeable speedup.
        prevDataUse = prevNoise is not null && codInfo.GlobalGain == prevNoise.GlobalGain;

        sfbmax = codInfo.BlockType == LameBlockType.Short ? 38 : 21;

        for (sfb = 0; sfb <= sfbmax; sfb++)
        {
            int step = -1;

            if (prevDataUse || codInfo.BlockType == LameBlockType.Norm)
            {
                step = codInfo.GlobalGain
                     - ((codInfo.Scalefac[sfb]
                         + (codInfo.Preflag != 0 ? LameQuantizePvt.Pretab[sfb] : 0))
                        << (codInfo.ScalefacScale + 1))
                     - codInfo.SubblockGain[codInfo.Window[sfb]] * 8;
            }
            if (prevDataUse && prevNoise!.Step[sfb] == step)
            {
                // do not recompute this part, but flush what has accumulated
                if (accumulate != 0)
                {
                    QuantizeLinesXrpow(accumulate, istep, xp[accXp..], pi[accIData..]);
                    accumulate = 0;
                }
                if (accumulate01 != 0)
                {
                    QuantizeLinesXrpow01(accumulate01, istep, xp[accXp..], pi[accIData..]);
                    accumulate01 = 0;
                }
            }
            else
            {
                int l = codInfo.Width[sfb];

                if (j + codInfo.Width[sfb] > codInfo.MaxNonzeroCoeff)
                {
                    // do not compute the upper zero part
                    int usefullsize = codInfo.MaxNonzeroCoeff - j + 1;
                    pi[codInfo.MaxNonzeroCoeff..576].Clear();
                    l = usefullsize;

                    if (l < 0) l = 0;

                    // no need to compute higher sfb values
                    sfb = sfbmax + 1;
                }

                // accumulate lines to quantise
                if (accumulate == 0 && accumulate01 == 0)
                {
                    accIData = iData;
                    accXp = xpBase;
                }
                if (prevNoise is not null
                    && prevNoise.SfbCount1 > 0
                    && sfb >= prevNoise.SfbCount1
                    && prevNoise.Step[sfb] > 0 && step >= prevNoise.Step[sfb])
                {
                    if (accumulate != 0)
                    {
                        QuantizeLinesXrpow(accumulate, istep, xp[accXp..], pi[accIData..]);
                        accumulate = 0;
                        accIData = iData;
                        accXp = xpBase;
                    }
                    accumulate01 += l;
                }
                else
                {
                    if (accumulate01 != 0)
                    {
                        QuantizeLinesXrpow01(accumulate01, istep, xp[accXp..], pi[accIData..]);
                        accumulate01 = 0;
                        accIData = iData;
                        accXp = xpBase;
                    }
                    accumulate += l;
                }

                if (l <= 0)
                {
                    // rh 20040215: may happen because of the prev_data_use optimisation
                    if (accumulate01 != 0)
                    {
                        QuantizeLinesXrpow01(accumulate01, istep, xp[accXp..], pi[accIData..]);
                        accumulate01 = 0;
                    }
                    if (accumulate != 0)
                    {
                        QuantizeLinesXrpow(accumulate, istep, xp[accXp..], pi[accIData..]);
                        accumulate = 0;
                    }

                    break;
                }
            }
            if (sfb <= sfbmax)
            {
                iData += codInfo.Width[sfb];
                xpBase += codInfo.Width[sfb];
                j += codInfo.Width[sfb];
            }
        }
        if (accumulate != 0)
        {
            QuantizeLinesXrpow(accumulate, istep, xp[accXp..], pi[accIData..]);
        }
        if (accumulate01 != 0)
        {
            QuantizeLinesXrpow01(accumulate01, istep, xp[accXp..], pi[accIData..]);
        }
    }

    /// <summary><c>ix_max</c>.</summary>
    private static int IxMax(ReadOnlySpan<int> ix)
    {
        int max1 = 0, max2 = 0;
        int i = 0;

        do
        {
            int x1 = ix[i++];
            int x2 = ix[i++];
            if (max1 < x1) max1 = x1;
            if (max2 < x2) max2 = x2;
        } while (i < ix.Length);

        if (max1 < max2) max1 = max2;
        return max1;
    }

    /// <summary><c>count_bit_ESC</c> - cost two ESC tables at once and keep the cheaper.</summary>
    private static int CountBitEsc(ReadOnlySpan<int> ix, int t1, int t2, ref uint s)
    {
        uint linbits = LameTables.ht[t1].Xlen * 65536u + LameTables.ht[t2].Xlen;
        uint sum = 0, sum2;
        int i = 0;

        do
        {
            uint x = (uint)ix[i++];
            uint y = (uint)ix[i++];

            if (x >= 15u) { x = 15u; sum += linbits; }
            if (y >= 15u) { y = 15u; sum += linbits; }
            x <<= 4;
            x += y;
            sum += LameTables.largetbl[x];
        } while (i < ix.Length);

        sum2 = sum & 0xffffu;
        sum >>= 16;

        if (sum > sum2) { sum = sum2; t1 = t2; }

        s += sum;
        return t1;
    }

    /// <summary><c>count_bit_noESC</c>.</summary>
    private static int CountBitNoEsc(ReadOnlySpan<int> ix, int mx, ref uint s)
    {
        uint sum1 = 0;
        byte[] hlen1 = LameTables.ht[1].Hlen!;
        int i = 0;
        _ = mx;

        do
        {
            uint x0 = (uint)ix[i++];
            uint x1 = (uint)ix[i++];
            sum1 += hlen1[x0 + x0 + x1];
        } while (i < ix.Length);

        s += sum1;
        return 1;
    }

    /// <summary><c>count_bit_noESC_from2</c>.</summary>
    private static int CountBitNoEscFrom2(ReadOnlySpan<int> ix, int max, ref uint s)
    {
        int t1 = hufTblNoEsc[max - 1];
        uint xlen = LameTables.ht[t1].Xlen;
        uint[] table = t1 == 2 ? LameTables.table23 : LameTables.table56;
        uint sum = 0, sum2;
        int i = 0;

        do
        {
            uint x0 = (uint)ix[i++];
            uint x1 = (uint)ix[i++];
            sum += table[x0 * xlen + x1];
        } while (i < ix.Length);

        sum2 = sum & 0xffffu;
        sum >>= 16;

        if (sum > sum2) { sum = sum2; t1++; }

        s += sum;
        return t1;
    }

    /// <summary><c>count_bit_noESC_from3</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBitNoEscFrom3(ReadOnlySpan<int> ix, int max, ref uint s)
    {
        int t1 = hufTblNoEsc[max - 1];
        uint sum1 = 0;
        uint sum2 = 0;
        uint sum3 = 0;
        uint xlen = LameTables.ht[t1].Xlen;
        byte[] hlen1 = LameTables.ht[t1].Hlen!;
        byte[] hlen2 = LameTables.ht[t1 + 1].Hlen!;
        byte[] hlen3 = LameTables.ht[t1 + 2].Hlen!;
        int t;
        int i = 0;

        do
        {
            uint x0 = (uint)ix[i++];
            uint x1 = (uint)ix[i++];
            uint x = x0 * xlen + x1;
            sum1 += hlen1[x];
            sum2 += hlen2[x];
            sum3 += hlen3[x];
        } while (i < ix.Length);

        t = t1;
        if (sum1 > sum2) { sum1 = sum2; t++; }
        if (sum1 > sum3) { sum1 = sum3; t = t1 + 2; }
        s += sum1;

        return t;
    }

    /// <summary>
    /// <c>count_fncs[]</c> - which counter handles a given maximum value, as a switch.
    /// </summary>
    /// <remarks>
    /// The C holds this as a table of function pointers indexed by <c>max</c>, with
    /// <c>count_bit_null</c> at index 0 for an all-zero region. A switch is the same dispatch
    /// without the indirect call, and the arms are in the C's order.
    /// </remarks>
    private static int CountFunction(int max, ReadOnlySpan<int> ix, ref uint s) => max switch
    {
        0 => 0,                                      // count_bit_null
        1 => CountBitNoEsc(ix, max, ref s),
        2 or 3 => CountBitNoEscFrom2(ix, max, ref s),
        _ => CountBitNoEscFrom3(ix, max, ref s),     // 4..15
    };

    /// <summary>
    /// <c>choose_table_nonMMX</c> - the Huffman table that codes <paramref name="ix"/> in the
    /// fewest bits.
    /// </summary>
    /// <remarks>
    /// The C warns that this knows the sizes and characteristics of the tables of Table B.7 and
    /// will not work with arbitrary ones.
    /// </remarks>
    public static int ChooseTableNonMmx(ReadOnlySpan<int> ix, ref int bits)
    {
        uint s = (uint)bits;
        uint max = (uint)IxMax(ix);
        int choice, choice2;

        if (max <= 15)
        {
            int t = CountFunction((int)max, ix, ref s);
            bits = (int)s;
            return t;
        }

        // try tables with linbits
        if (max > LameQuantizeConstants.IxmaxVal)
        {
            bits = LameQuantizeConstants.LargeBits;
            return -1;
        }
        max -= 15u;
        for (choice2 = 24; choice2 < 32; choice2++)
            if (LameTables.ht[choice2].Linmax >= max)
                break;

        for (choice = choice2 - 8; choice < 24; choice++)
            if (LameTables.ht[choice].Linmax >= max)
                break;

        int result = CountBitEsc(ix, choice, choice2, ref s);
        bits = (int)s;
        return result;
    }

    /// <summary>
    /// <c>noquant_count_bits</c> - the bits this granule's already-quantised lines will take.
    /// </summary>
    public static int NoquantCountBits(
        LameInternalFlags gfc, ref GrInfo gi, CalcNoiseData? prevNoise)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int bits = 0;
        int i, a1, a2;
        Span<int> ix = gi.L3Enc;

        i = Math.Min(576, ((gi.MaxNonzeroCoeff + 2) >> 1) << 1);

        if (prevNoise is not null) prevNoise.SfbCount1 = 0;

        // Determine the count1 region.
        for (; i > 1; i -= 2)
            if ((ix[i - 1] | ix[i - 2]) != 0)
                break;
        gi.Count1 = i;

        // The bits to encode the quadruples.
        a1 = a2 = 0;
        for (; i > 3; i -= 4)
        {
            int x4 = ix[i - 4];
            int x3 = ix[i - 3];
            int x2 = ix[i - 2];
            int x1 = ix[i - 1];
            int p;
            // a hack to check that all values are at most 1
            if ((uint)(x4 | x3 | x2 | x1) > 1) break;

            p = ((x4 * 2 + x3) * 2 + x2) * 2 + x1;
            a1 += LameTables.t32l[p];
            a2 += LameTables.t33l[p];
        }

        bits = a1;
        gi.Count1TableSelect = 0;
        if (a1 > a2)
        {
            bits = a2;
            gi.Count1TableSelect = 1;
        }

        gi.Count1Bits = bits;
        gi.BigValues = i;
        if (i == 0) return bits;

        if (gi.BlockType == LameBlockType.Short)
        {
            a1 = 3 * gfc.ScalefacBand.S[3];
            if (a1 > gi.BigValues) a1 = gi.BigValues;
            a2 = gi.BigValues;
        }
        else if (gi.BlockType == LameBlockType.Norm)
        {
            a1 = gi.Region0Count = gfc.SvQnt.BvScf![i - 2];
            a2 = gi.Region1Count = gfc.SvQnt.BvScf[i - 1];

            a2 = gfc.ScalefacBand.L[a1 + a2 + 2];
            a1 = gfc.ScalefacBand.L[a1 + 1];
            if (a2 < i)
                gi.TableSelect[2] = ChooseTableNonMmx(ix[a2..i], ref bits);
        }
        else
        {
            gi.Region0Count = 7;
            gi.Region1Count = LameConstants.SbMaxL - 1 - 7 - 1;
            a1 = gfc.ScalefacBand.L[7 + 1];
            a2 = i;
            if (a1 > a2) a1 = a2;
        }

        // Allow for the case where bigvalues < region0 < region1, with region0 and region1
        // then ignored.
        a1 = Math.Min(a1, i);
        a2 = Math.Min(a2, i);

        // Count the bits needed for the bigvalues region.
        if (0 < a1)
            gi.TableSelect[0] = ChooseTableNonMmx(ix[..a1], ref bits);
        if (a1 < a2)
            gi.TableSelect[1] = ChooseTableNonMmx(ix[a1..a2], ref bits);
        if (cfg.UseBestHuffman == 2)
        {
            gi.Part23Length = bits;
            BestHuffmanDivide(gfc, ref gi);
            bits = gi.Part23Length;
        }

        if (prevNoise is not null)
        {
            if (gi.BlockType == LameBlockType.Norm)
            {
                int sfb = 0;
                while (gfc.ScalefacBand.L[sfb] < gi.BigValues) sfb++;
                prevNoise.SfbCount1 = sfb;
            }
        }

        return bits;
    }

    /// <summary><c>count_bits</c> - quantise, then count.</summary>
    public static int CountBits(
        LameInternalFlags gfc, ReadOnlySpan<float> xr, ref GrInfo gi, CalcNoiseData? prevNoise)
    {
        Span<int> ix = gi.L3Enc;

        // quantize_xrpow uses a table lookup, so this has to be checked first
        float w = LameQuantizeConstants.IxmaxVal / LameQuantizePvt.Ipow20Of(gi.GlobalGain);

        if (gi.XrpowMax > w) return LameQuantizeConstants.LargeBits;

        QuantizeXrpow(xr, ix, LameQuantizePvt.Ipow20Of(gi.GlobalGain), gi, prevNoise);

        if ((gfc.SvQnt.SubstepShaping & 2) != 0)
        {
            int sfb, j = 0;
            // 0.634521682242439 = 0.5946 * 2^(0.5 * 0.1875)
            int gain = gi.GlobalGain + gi.ScalefacScale;
            float roundfac = (float)(0.634521682242439 / LameQuantizePvt.Ipow20Of(gain));
            for (sfb = 0; sfb < gi.Sfbmax; sfb++)
            {
                int width = gi.Width[sfb];
                if (gfc.SvQnt.Pseudohalf![sfb] == 0)
                {
                    j += width;
                }
                else
                {
                    int k;
                    for (k = j, j += width; k < j; ++k)
                        ix[k] = xr[k] >= roundfac ? ix[k] : 0;
                }
            }
        }
        return NoquantCountBits(gfc, ref gi, prevNoise);
    }

    /// <summary><c>recalc_divide_init</c>.</summary>
    private static void RecalcDivideInit(
        LameInternalFlags gfc, in GrInfo codInfo, ReadOnlySpan<int> ix,
        Span<int> r01Bits, Span<int> r01Div, Span<int> r0Tbl, Span<int> r1Tbl)
    {
        int r0, r1, bigv, r0t, r1t, bits;

        bigv = codInfo.BigValues;

        for (r0 = 0; r0 <= 7 + 15; r0++)
            r01Bits[r0] = LameQuantizeConstants.LargeBits;

        for (r0 = 0; r0 < 16; r0++)
        {
            int a1 = gfc.ScalefacBand.L[r0 + 1];
            int r0bits;
            if (a1 >= bigv) break;
            r0bits = 0;
            r0t = ChooseTableNonMmx(ix[..a1], ref r0bits);

            for (r1 = 0; r1 < 8; r1++)
            {
                int a2 = gfc.ScalefacBand.L[r0 + r1 + 2];
                if (a2 >= bigv) break;

                bits = r0bits;
                r1t = ChooseTableNonMmx(ix[a1..a2], ref bits);
                if (r01Bits[r0 + r1] > bits)
                {
                    r01Bits[r0 + r1] = bits;
                    r01Div[r0 + r1] = r0;
                    r0Tbl[r0 + r1] = r0t;
                    r1Tbl[r0 + r1] = r1t;
                }
            }
        }
    }

    /// <summary><c>recalc_divide_sub</c>.</summary>
    private static void RecalcDivideSub(
        LameInternalFlags gfc, in GrInfo codInfo2, ref GrInfo gi, ReadOnlySpan<int> ix,
        ReadOnlySpan<int> r01Bits, ReadOnlySpan<int> r01Div,
        ReadOnlySpan<int> r0Tbl, ReadOnlySpan<int> r1Tbl)
    {
        int bits, r2, a2, bigv, r2t;

        bigv = codInfo2.BigValues;

        for (r2 = 2; r2 < LameConstants.SbMaxL + 1; r2++)
        {
            a2 = gfc.ScalefacBand.L[r2];
            if (a2 >= bigv) break;

            bits = r01Bits[r2 - 2] + codInfo2.Count1Bits;
            if (gi.Part23Length <= bits) break;

            r2t = ChooseTableNonMmx(ix[a2..bigv], ref bits);
            if (gi.Part23Length <= bits) continue;

            gi = codInfo2;      // memcpy(gi, cod_info2, sizeof(gr_info))
            gi.Part23Length = bits;
            gi.Region0Count = r01Div[r2 - 2];
            gi.Region1Count = r2 - 2 - r01Div[r2 - 2];
            gi.TableSelect[0] = r0Tbl[r2 - 2];
            gi.TableSelect[1] = r1Tbl[r2 - 2];
            gi.TableSelect[2] = r2t;
        }
    }

    /// <summary>
    /// <c>best_huffman_divide</c> - recompute the best region split and count1 boundary.
    /// </summary>
    /// <remarks>
    /// The three <c>memcpy(&amp;cod_info2, gi, sizeof(gr_info))</c> here, and the one inside
    /// <c>recalc_divide_sub</c>, are why <see cref="GrInfo"/> is a value type with every array
    /// inline: they are a save and a restore, and a C# struct holding <c>float[]</c> would make
    /// them aliases instead.
    /// </remarks>
    public static void BestHuffmanDivide(LameInternalFlags gfc, ref GrInfo gi)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int i, a1, a2;
        GrInfo codInfo2;
        ReadOnlySpan<int> ix = gi.L3Enc;

        Int23 r01BitsStorage = default, r01DivStorage = default;
        Int23 r0TblStorage = default, r1TblStorage = default;
        Span<int> r01Bits = r01BitsStorage;
        Span<int> r01Div = r01DivStorage;
        Span<int> r0Tbl = r0TblStorage;
        Span<int> r1Tbl = r1TblStorage;

        // the short block path fails for MPEG-2
        if (gi.BlockType == LameBlockType.Short && cfg.ModeGr == 1) return;

        codInfo2 = gi;
        if (gi.BlockType == LameBlockType.Norm)
        {
            RecalcDivideInit(gfc, gi, ix, r01Bits, r01Div, r0Tbl, r1Tbl);
            RecalcDivideSub(gfc, codInfo2, ref gi, ix, r01Bits, r01Div, r0Tbl, r1Tbl);
        }

        i = codInfo2.BigValues;
        if (i == 0 || (uint)(ix[i - 2] | ix[i - 1]) > 1) return;

        i = gi.Count1 + 2;
        if (i > 576) return;

        // the bits to encode the quadruples
        codInfo2 = gi;
        codInfo2.Count1 = i;
        a1 = a2 = 0;

        for (; i > codInfo2.BigValues; i -= 4)
        {
            int p = ((ix[i - 4] * 2 + ix[i - 3]) * 2 + ix[i - 2]) * 2 + ix[i - 1];
            a1 += LameTables.t32l[p];
            a2 += LameTables.t33l[p];
        }
        codInfo2.BigValues = i;

        codInfo2.Count1TableSelect = 0;
        if (a1 > a2)
        {
            a1 = a2;
            codInfo2.Count1TableSelect = 1;
        }

        codInfo2.Count1Bits = a1;

        if (codInfo2.BlockType == LameBlockType.Norm)
        {
            RecalcDivideSub(gfc, codInfo2, ref gi, ix, r01Bits, r01Div, r0Tbl, r1Tbl);
        }
        else
        {
            // Count the bits needed for the bigvalues region.
            codInfo2.Part23Length = a1;
            a1 = gfc.ScalefacBand.L[7 + 1];
            if (a1 > i) a1 = i;
            int part23 = codInfo2.Part23Length;
            if (a1 > 0)
                codInfo2.TableSelect[0] = ChooseTableNonMmx(ix[..a1], ref part23);
            if (i > a1)
                codInfo2.TableSelect[1] = ChooseTableNonMmx(ix[a1..i], ref part23);
            codInfo2.Part23Length = part23;
            if (gi.Part23Length > codInfo2.Part23Length)
                gi = codInfo2;
        }
    }

    /// <summary><c>scfsi_calc</c> - which scalefactor bands granule 1 can inherit.</summary>
    private static void ScfsiCalc(int ch, ref IiiSideInfo l3Side)
    {
        int i;
        int s1, s2, c1, c2;
        int sfb;
        ref GrInfo gi = ref l3Side.Tt[1 * 2 + ch];
        ref GrInfo g0 = ref l3Side.Tt[0 * 2 + ch];

        for (i = 0; i < LameTables.scfsi_band.Length - 1; i++)
        {
            for (sfb = LameTables.scfsi_band[i]; sfb < LameTables.scfsi_band[i + 1]; sfb++)
            {
                if (g0.Scalefac[sfb] != gi.Scalefac[sfb] && gi.Scalefac[sfb] >= 0)
                    break;
            }
            if (sfb == LameTables.scfsi_band[i + 1])
            {
                for (sfb = LameTables.scfsi_band[i]; sfb < LameTables.scfsi_band[i + 1]; sfb++)
                    gi.Scalefac[sfb] = -1;
                l3Side.Scfsi[ch * 4 + i] = 1;
            }
        }

        s1 = c1 = 0;
        for (sfb = 0; sfb < 11; sfb++)
        {
            if (gi.Scalefac[sfb] == -1) continue;
            c1++;
            if (s1 < gi.Scalefac[sfb]) s1 = gi.Scalefac[sfb];
        }

        s2 = c2 = 0;
        for (; sfb < LameConstants.SbPsyL; sfb++)
        {
            if (gi.Scalefac[sfb] == -1) continue;
            c2++;
            if (s2 < gi.Scalefac[sfb]) s2 = gi.Scalefac[sfb];
        }

        for (i = 0; i < 16; i++)
        {
            if (s1 < slen1N[i] && s2 < slen2N[i])
            {
                int c = Slen1Tab[i] * c1 + Slen2Tab[i] * c2;
                if (gi.Part2Length > c)
                {
                    gi.Part2Length = c;
                    gi.ScalefacCompress = i;
                }
            }
        }
    }

    /// <summary>
    /// <c>best_scalefac_store</c> - the cheapest way to store the final scalefactors.
    /// </summary>
    /// <remarks>
    /// Only call this once the scalefactors are final and the channel and granule will not be
    /// re-encoded. The magic number -2 means "this band's lines are all zero, so anything
    /// goes"; the C's comment says only this function and <c>scfsi_calc</c> know about it, and
    /// that is still true here.
    /// </remarks>
    public static void BestScalefacStore(
        LameInternalFlags gfc, int gr, int ch, ref IiiSideInfo l3Side)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref GrInfo gi = ref l3Side.Tt[gr * 2 + ch];
        int sfb, i, j, l;
        int recalc = 0;

        // Remove scalefactors from bands whose lines are all zero; the idea comes from the AAC
        // ISO documents, added mt 3/00.
        j = 0;
        for (sfb = 0; sfb < gi.Sfbmax; sfb++)
        {
            int width = gi.Width[sfb];
            for (l = j, j += width; l < j; ++l)
                if (gi.L3Enc[l] != 0)
                    break;
            if (l == j)
                gi.Scalefac[sfb] = recalc = -2;
        }

        if (gi.ScalefacScale == 0 && gi.Preflag == 0)
        {
            int s = 0;
            for (sfb = 0; sfb < gi.Sfbmax; sfb++)
                if (gi.Scalefac[sfb] > 0)
                    s |= gi.Scalefac[sfb];

            if ((s & 1) == 0 && s != 0)
            {
                for (sfb = 0; sfb < gi.Sfbmax; sfb++)
                    if (gi.Scalefac[sfb] > 0)
                        gi.Scalefac[sfb] >>= 1;

                gi.ScalefacScale = recalc = 1;
            }
        }

        if (gi.Preflag == 0 && gi.BlockType != LameBlockType.Short && cfg.ModeGr == 2)
        {
            for (sfb = 11; sfb < LameConstants.SbPsyL; sfb++)
                if (gi.Scalefac[sfb] < LameQuantizePvt.Pretab[sfb] && gi.Scalefac[sfb] != -2)
                    break;
            if (sfb == LameConstants.SbPsyL)
            {
                for (sfb = 11; sfb < LameConstants.SbPsyL; sfb++)
                    if (gi.Scalefac[sfb] > 0)
                        gi.Scalefac[sfb] -= LameQuantizePvt.Pretab[sfb];

                gi.Preflag = recalc = 1;
            }
        }

        for (i = 0; i < 4; i++)
            l3Side.Scfsi[ch * 4 + i] = 0;

        if (cfg.ModeGr == 2 && gr == 1
            && l3Side.Tt[0 * 2 + ch].BlockType != LameBlockType.Short
            && l3Side.Tt[1 * 2 + ch].BlockType != LameBlockType.Short)
        {
            ScfsiCalc(ch, ref l3Side);
            recalc = 0;
        }
        for (sfb = 0; sfb < gi.Sfbmax; sfb++)
            if (gi.Scalefac[sfb] == -2)
                gi.Scalefac[sfb] = 0;   // if anything goes, 0 is a good choice

        if (recalc != 0)
            ScaleBitcount(gfc, ref gi);
    }

    /// <summary>
    /// <c>mpeg1_scale_bitcount</c> - the bits to code the scalefactors, MPEG-1.
    /// </summary>
    /// <remarks>
    /// Takehiro Tominaga 10/99: loop over every possible <c>scalefac_compress</c> to find the
    /// one that uses the fewest bits, where the IS would stop at the first valid index.
    /// </remarks>
    private static int Mpeg1ScaleBitcount(LameInternalFlags gfc, ref GrInfo codInfo)
    {
        int k, sfb, maxSlen1 = 0, maxSlen2 = 0;
        int[] tab;
        _ = gfc;

        if (codInfo.BlockType == LameBlockType.Short)
        {
            tab = scaleShort;
            if (codInfo.MixedBlockFlag != 0) tab = scaleMixed;
        }
        else
        {
            tab = scaleLong;
            if (codInfo.Preflag == 0)
            {
                for (sfb = 11; sfb < LameConstants.SbPsyL; sfb++)
                    if (codInfo.Scalefac[sfb] < LameQuantizePvt.Pretab[sfb])
                        break;

                if (sfb == LameConstants.SbPsyL)
                {
                    codInfo.Preflag = 1;
                    for (sfb = 11; sfb < LameConstants.SbPsyL; sfb++)
                        codInfo.Scalefac[sfb] -= LameQuantizePvt.Pretab[sfb];
                }
            }
        }

        for (sfb = 0; sfb < codInfo.Sfbdivide; sfb++)
            if (maxSlen1 < codInfo.Scalefac[sfb])
                maxSlen1 = codInfo.Scalefac[sfb];

        for (; sfb < codInfo.Sfbmax; sfb++)
            if (maxSlen2 < codInfo.Scalefac[sfb])
                maxSlen2 = codInfo.Scalefac[sfb];

        codInfo.Part2Length = LameQuantizeConstants.LargeBits;
        for (k = 0; k < 16; k++)
        {
            if (maxSlen1 < slen1N[k] && maxSlen2 < slen2N[k]
                && codInfo.Part2Length > tab[k])
            {
                codInfo.Part2Length = tab[k];
                codInfo.ScalefacCompress = k;
            }
        }
        return codInfo.Part2Length == LameQuantizeConstants.LargeBits ? 1 : 0;
    }

    /// <summary>
    /// <c>mpeg2_scale_bitcount</c> - the same for MPEG-2's lower sample rates.
    /// </summary>
    /// <remarks>
    /// The C's comment records that this is reverse engineered from section 2.4.3.2 of the
    /// MPEG-2 IS. <c>sfb_partition_table</c> is a <c>const int *</c> there, pointing at a row of
    /// <c>nr_of_sfb_block</c>; here it is that row's identity, which is what
    /// <see cref="GrInfo.SfbPartitionTableRow"/> holds.
    /// </remarks>
    private static int Mpeg2ScaleBitcount(LameInternalFlags gfc, ref GrInfo codInfo)
    {
        int tableNumber, rowInTable, partition, nrSfb, window, over;
        int i, sfb;
        Int4 maxSfacStorage = default;
        Span<int> maxSfac = maxSfacStorage;
        int[] partitionTable;
        _ = gfc;

        // The C notes it should try to use table one, but does not yet.
        tableNumber = codInfo.Preflag != 0 ? 2 : 0;

        for (i = 0; i < 4; i++) maxSfac[i] = 0;

        if (codInfo.BlockType == LameBlockType.Short)
        {
            rowInTable = 1;
            partitionTable = LameQuantizePvt.NrOfSfbBlock[tableNumber][rowInTable];
            for (sfb = 0, partition = 0; partition < 4; partition++)
            {
                nrSfb = partitionTable[partition] / 3;
                for (i = 0; i < nrSfb; i++, sfb++)
                    for (window = 0; window < 3; window++)
                        if (codInfo.Scalefac[sfb * 3 + window] > maxSfac[partition])
                            maxSfac[partition] = codInfo.Scalefac[sfb * 3 + window];
            }
        }
        else
        {
            rowInTable = 0;
            partitionTable = LameQuantizePvt.NrOfSfbBlock[tableNumber][rowInTable];
            for (sfb = 0, partition = 0; partition < 4; partition++)
            {
                nrSfb = partitionTable[partition];
                for (i = 0; i < nrSfb; i++, sfb++)
                    if (codInfo.Scalefac[sfb] > maxSfac[partition])
                        maxSfac[partition] = codInfo.Scalefac[sfb];
            }
        }

        for (over = 0, partition = 0; partition < 4; partition++)
            if (maxSfac[partition] > maxRangeSfacTab[tableNumber][partition])
                over++;

        if (over == 0)
        {
            // No band has been over-amplified, so scalefac_compress and slen[] can be set.
            int slen1, slen2, slen3, slen4;

            codInfo.SfbPartitionTableRow = tableNumber * 3 + rowInTable;
            for (partition = 0; partition < 4; partition++)
                codInfo.Slen[partition] = log2Tab[maxSfac[partition]];

            slen1 = codInfo.Slen[0];
            slen2 = codInfo.Slen[1];
            slen3 = codInfo.Slen[2];
            slen4 = codInfo.Slen[3];

            switch (tableNumber)
            {
                case 0:
                    codInfo.ScalefacCompress = (((slen1 * 5) + slen2) << 4)
                                             + (slen3 << 2)
                                             + slen4;
                    break;
                case 1:
                    codInfo.ScalefacCompress = 400 + (((slen1 * 5) + slen2) << 2) + slen3;
                    break;
                case 2:
                    codInfo.ScalefacCompress = 500 + (slen1 * 3) + slen2;
                    break;
                default:
                    throw new NotSupportedException(
                        "intensity stereo is not implemented yet, as in the C.");
            }

            codInfo.Part2Length = 0;
            for (partition = 0; partition < 4; partition++)
                codInfo.Part2Length += codInfo.Slen[partition] * partitionTable[partition];
        }
        return over;
    }

    /// <summary><c>scale_bitcount</c>.</summary>
    public static int ScaleBitcount(LameInternalFlags gfc, ref GrInfo codInfo)
        => gfc.Cfg.ModeGr == 2
            ? Mpeg1ScaleBitcount(gfc, ref codInfo)
            : Mpeg2ScaleBitcount(gfc, ref codInfo);

    /// <summary>
    /// <c>huffman_init</c> - precompute the big-values region split for every even count.
    /// </summary>
    /// <remarks>
    /// The C also assigns <c>gfc-&gt;choose_table</c>, picking an MMX kernel where one is built.
    /// There is no function pointer here; <see cref="ChooseTableNonMmx"/> is called directly,
    /// which is the project's standing decision on DSP indirection.
    /// </remarks>
    public static void HuffmanInit(LameInternalFlags gfc)
    {
        byte[] bvScf = gfc.SvQnt.BvScf!;

        for (int i = 2; i <= 576; i += 2)
        {
            int scfbAnz = 0, bvIndex;
            while (gfc.ScalefacBand.L[++scfbAnz] < i) { }

            bvIndex = subdvTable[scfbAnz * 2 + 0];
            while (gfc.ScalefacBand.L[bvIndex + 1] > i)
                bvIndex--;

            if (bvIndex < 0)
            {
                // Everything is going to be encoded as region0: bigvalues < region0 < region1,
                // so set region0 and region1 to something larger than bigvalues.
                bvIndex = subdvTable[scfbAnz * 2 + 0];
            }

            bvScf[i - 2] = (byte)bvIndex;

            bvIndex = subdvTable[scfbAnz * 2 + 1];
            while (gfc.ScalefacBand.L[bvIndex + bvScf[i - 2] + 2] > i)
                bvIndex--;

            if (bvIndex < 0)
                bvIndex = subdvTable[scfbAnz * 2 + 1];

            bvScf[i - 1] = (byte)bvIndex;
        }
    }

    // The C's small stack arrays. Inline array structs rather than stackalloc, because a method
    // containing a localloc never tiers up past Tier0 and both of these sit inside the
    // quantisation loop.

    /// <summary><c>int r01_bits[7 + 15 + 1]</c> and its three companions.</summary>
    [InlineArray(7 + 15 + 1)] private struct Int23 { private int element0; }

    /// <summary><c>int max_sfac[4]</c>.</summary>
    [InlineArray(4)] private struct Int4 { private int element0; }
}

// Licensed under the MIT License.
// Based on ISO/IEC 11172-3 (MPEG-1 Audio Layer III) specification.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// Decoder for MP3 scalefactors.
/// Scalefactors control the amplification of frequency bands during requantization.
/// </summary>
public static class Mp3ScalefactorDecoder
{
    /// <summary>
    /// Number of scalefactor bands for long blocks.
    /// </summary>
    public const int LongBlockScalefactorBands = 21;
    
    /// <summary>
    /// Number of scalefactor bands for short blocks.
    /// </summary>
    public const int ShortBlockScalefactorBands = 12;
    
    /// <summary>
    /// Number of windows for short blocks.
    /// </summary>
    public const int ShortBlockWindows = 3;
    
    /// <summary>
    /// Decodes scalefactors for MPEG-1 Layer III.
    /// </summary>
    /// <param name="reader">Bit reader positioned at scalefactor data.</param>
    /// <param name="granuleInfo">Granule side information.</param>
    /// <param name="granuleIndex">Granule index (0 or 1).</param>
    /// <param name="scfsi0">Scalefactor selection info for band 0-5.</param>
    /// <param name="scfsi1">Scalefactor selection info for band 6-10.</param>
    /// <param name="scfsi2">Scalefactor selection info for band 11-15.</param>
    /// <param name="scfsi3">Scalefactor selection info for band 16-20.</param>
    /// <param name="scalefactorsLong">Output buffer for long block scalefactors (21 bands).</param>
    /// <param name="scalefactorsShort">Output buffer for short block scalefactors (12 bands × 3 windows).</param>
    /// <param name="bitsRead">Number of bits consumed.</param>
    public static void DecodeMpeg1Scalefactors(
        ref Mp3BitReader reader,
        in Mp3GranuleInfo granuleInfo,
        int granuleIndex,
        int scfsi0, int scfsi1, int scfsi2, int scfsi3,
        Span<int> scalefactorsLong,
        Span<int> scalefactorsShort,
        out int bitsRead)
    {
        bitsRead = 0;
        int startBits = reader.BitsRead;
        
        // Get scalefactor lengths based on block type and scalefactor_compress
        int slen1 = Mp3HuffmanTables.ScalefactorLength1[granuleInfo.ScalefactorCompress];
        int slen2 = Mp3HuffmanTables.ScalefactorLength2[granuleInfo.ScalefactorCompress];
        
        if (granuleInfo.WindowSwitchingFlag && granuleInfo.BlockType == 2)
        {
            // Short blocks or mixed blocks
            if (granuleInfo.MixedBlockFlag)
            {
                // Mixed block: 8 long bands + 3 short bands × 3 windows for remaining
                
                // First 8 scalefactor bands are long block format
                for (int sfb = 0; sfb < 8; sfb++)
                {
                    scalefactorsLong[sfb] = reader.ReadBits(slen1);
                }
                
                // Remaining bands are short block format
                for (int sfb = 3; sfb < 6; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        int idx = sfb * 3 + window;
                        scalefactorsShort[idx] = reader.ReadBits(slen1);
                    }
                }
                
                for (int sfb = 6; sfb < 12; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        int idx = sfb * 3 + window;
                        scalefactorsShort[idx] = reader.ReadBits(slen2);
                    }
                }
            }
            else
            {
                // Pure short blocks
                // 12 short sfb × 3 windows, split into slen1 and slen2 regions
                for (int sfb = 0; sfb < 6; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        int idx = sfb * 3 + window;
                        scalefactorsShort[idx] = reader.ReadBits(slen1);
                    }
                }
                
                for (int sfb = 6; sfb < 12; sfb++)
                {
                    for (int window = 0; window < 3; window++)
                    {
                        int idx = sfb * 3 + window;
                        scalefactorsShort[idx] = reader.ReadBits(slen2);
                    }
                }
            }
        }
        else
        {
            // Long blocks
            // scfsi controls which bands are copied from granule 0
            // scfsi bands: 0 = sfb 0-5, 1 = sfb 6-10, 2 = sfb 11-15, 3 = sfb 16-20
            
            if (granuleIndex == 0 || scfsi0 == 0)
            {
                // Read bands 0-5
                for (int sfb = 0; sfb < 6; sfb++)
                {
                    scalefactorsLong[sfb] = reader.ReadBits(slen1);
                }
            }
            
            if (granuleIndex == 0 || scfsi1 == 0)
            {
                // Read bands 6-10
                for (int sfb = 6; sfb < 11; sfb++)
                {
                    scalefactorsLong[sfb] = reader.ReadBits(slen1);
                }
            }
            
            if (granuleIndex == 0 || scfsi2 == 0)
            {
                // Read bands 11-15
                for (int sfb = 11; sfb < 16; sfb++)
                {
                    scalefactorsLong[sfb] = reader.ReadBits(slen2);
                }
            }
            
            if (granuleIndex == 0 || scfsi3 == 0)
            {
                // Read bands 16-20
                for (int sfb = 16; sfb < 21; sfb++)
                {
                    scalefactorsLong[sfb] = reader.ReadBits(slen2);
                }
            }
        }
        
        bitsRead = reader.BitsRead - startBits;
    }
    
    /// <summary>
    /// Decodes scalefactors for MPEG-2 LSF and MPEG-2.5 Layer III.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A translation of the LSF branch of FFmpeg's <c>mpegaudiodec_template.c</c>, replacing a
    /// reconstruction that was wrong in three ways. LSF does not divide its scalefactors into
    /// the MPEG-1 groups of 6/5/5/5 bands: <c>scalefac_compress</c> selects one of six rows of
    /// <c>ff_lsf_nsf_table</c>, and that row says how many scalefactors each of the four
    /// partitions holds - {11, 10, 0, 0} and {7, 7, 7, 0} among them. The old code assumed
    /// 6/5/5/5 for long blocks and 6/3/3 for short ones, so every frame with
    /// <c>scalefac_compress</c> at or above 400, and every short block, read the wrong number
    /// of bits at the wrong widths. It also never set <c>preflag</c>, which LSF carries in
    /// <c>scalefac_compress</c> rather than in the side information.
    /// </para>
    /// <para>
    /// The partitions do not line up with the long/short split either: for a mixed block the
    /// first partition of row 2 holds 15 scalefactors, which is the six long bands and nine of
    /// the short ones. So the values are read into one flat list, exactly as the C does, and
    /// then handed out the way <c>exponents_from_scale_factors</c> consumes them - the first
    /// <c>long_end</c> to the long bands, the rest to the short bands window by window.
    /// Anything the row does not supply stays zero, which is the C's own
    /// <c>for (; j &lt; 40; j++) g-&gt;scale_factors[j] = 0;</c>.
    /// </para>
    /// </remarks>
    /// <param name="reader">Bit reader positioned at scalefactor data.</param>
    /// <param name="granuleInfo">Granule side information.</param>
    /// <param name="intensityStereoChannel1">
    /// <c>(s-&gt;mode_ext &amp; MODE_EXT_I_STEREO) &amp;&amp; ch == 1</c>, which halves
    /// <c>scalefac_compress</c> and selects the second set of three table rows.
    /// </param>
    /// <param name="sampleRateIndex">
    /// FFmpeg's <c>sample_rate_index</c>, 0-8. Only the <c>&lt;= 2</c> test is used, and no LSF
    /// frame can reach it, but it is the C's test and MPEG-2.5 at 8 kHz is row 8.
    /// </param>
    /// <param name="scalefactorsLong">Output buffer for long block scalefactors (22 bands).</param>
    /// <param name="scalefactorsShort">Output buffer for short block scalefactors (13 × 3).</param>
    /// <param name="preflag">
    /// Set when <c>scalefac_compress</c> is 500 or more. LSF has no preflag bit in the side
    /// information, so this is the only place it is decided.
    /// </param>
    /// <param name="bitsRead">Number of bits consumed.</param>
    public static void DecodeMpeg2Scalefactors(
        ref Mp3BitReader reader,
        in Mp3GranuleInfo granuleInfo,
        bool intensityStereoChannel1,
        int sampleRateIndex,
        Span<int> scalefactorsLong,
        Span<int> scalefactorsShort,
        out bool preflag,
        out int bitsRead)
    {
        int startBits = reader.BitsRead;
        preflag = false;

        // tindex: 0 long, 1 short, 2 short with a switch point (mixed).
        int tindex = granuleInfo.BlockType == 2
            ? granuleInfo.MixedBlockFlag ? 2 : 1
            : 0;

        Slen4 slenStore = default;
        Span<int> slen = slenStore;

        int sf = granuleInfo.ScalefactorCompress;
        int tindex2;
        if (intensityStereoChannel1)
        {
            sf >>= 1;
            if (sf < 180)
            {
                LsfSfExpand(slen, sf, 6, 6, 0);
                tindex2 = 3;
            }
            else if (sf < 244)
            {
                LsfSfExpand(slen, sf - 180, 4, 4, 0);
                tindex2 = 4;
            }
            else
            {
                LsfSfExpand(slen, sf - 244, 3, 0, 0);
                tindex2 = 5;
            }
        }
        else
        {
            if (sf < 400)
            {
                LsfSfExpand(slen, sf, 5, 4, 4);
                tindex2 = 0;
            }
            else if (sf < 500)
            {
                LsfSfExpand(slen, sf - 400, 5, 4, 0);
                tindex2 = 1;
            }
            else
            {
                LsfSfExpand(slen, sf - 500, 3, 0, 0);
                tindex2 = 2;
                preflag = true;
            }
        }

        // g->scale_factors[40], read in order and zero past what the row supplies.
        Scalefactors40 flatStore = default;
        Span<int> flat = flatStore;

        int j = 0;
        for (int k = 0; k < 4; k++)
        {
            int n = Mp3HuffmanTables.LsfNsfTable[(tindex2 * 3 + tindex) * 4 + k];
            int sl = slen[k];
            if (sl != 0)
                for (int i = 0; i < n; i++) flat[j++] = reader.ReadBits(sl);
            else
                for (int i = 0; i < n; i++) flat[j++] = 0;
        }

        // compute_band_indexes, for the two cases LSF can be in.
        int longEnd, shortStart;
        if (tindex == 0)
        {
            longEnd = 22;
            shortStart = 13;
        }
        else if (tindex == 2)
        {
            longEnd = sampleRateIndex <= 2 ? 8 : 6;
            shortStart = 3;
        }
        else
        {
            longEnd = 0;
            shortStart = 0;
        }

        // exponents_from_scale_factors' own walk over the flat list.
        for (int i = 0; i < longEnd; i++)
            scalefactorsLong[i] = flat[i];

        if (shortStart < 13)
        {
            int k = longEnd;
            for (int i = shortStart; i < 13; i++)
                for (int l = 0; l < 3; l++)
                    scalefactorsShort[i * 3 + l] = flat[k++];
        }

        bitsRead = reader.BitsRead - startBits;
    }

    /// <summary>
    /// <c>lsf_sf_expand</c> and its <c>SPLIT</c> macro - peels <c>scalefac_compress</c> apart
    /// from the last partition backwards.
    /// </summary>
    /// <remarks>
    /// The reciprocal multiplies for 5 and 6 are the C's, kept because they are what the C
    /// computes: <c>(sf * 205) &gt;&gt; 10</c> is <c>sf / 5</c> and <c>(sf * 171) &gt;&gt; 10</c>
    /// is <c>sf / 6</c> over the range <c>scalefac_compress</c> can reach, and writing the
    /// division instead would be a different expression to audit rather than the same one.
    /// An <c>n</c> of 0 contributes nothing and leaves <c>sf</c> alone, which is how the rows
    /// with fewer than four partitions work.
    /// </remarks>
    private static void LsfSfExpand(Span<int> slen, int sf, int n1, int n2, int n3)
    {
        Split(out slen[3], ref sf, n3);
        Split(out slen[2], ref sf, n2);
        Split(out slen[1], ref sf, n1);
        slen[0] = sf;
    }

    /// <summary>The <c>SPLIT</c> macro of <c>mpegaudiodec_template.c</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Split(out int dst, ref int sf, int n)
    {
        if (n == 3)
        {
            int m = sf * 171 >> 9;
            dst = sf - 3 * m;
            sf = m;
        }
        else if (n == 4)
        {
            dst = sf & 3;
            sf >>= 2;
        }
        else if (n == 5)
        {
            int m = sf * 205 >> 10;
            dst = sf - 5 * m;
            sf = m;
        }
        else if (n == 6)
        {
            int m = sf * 171 >> 10;
            dst = sf - 6 * m;
            sf = m;
        }
        else
        {
            dst = 0;
        }
    }

    /// <summary><c>int slen[4]</c>, as an inline array rather than a stackalloc.</summary>
    [InlineArray(4)]
    private struct Slen4 { private int element0; }

    /// <summary><c>g->scale_factors[40]</c>, as an inline array rather than a stackalloc.</summary>
    [InlineArray(40)]
    private struct Scalefactors40 { private int element0; }
}

// WMA v1/v2 setup and the two shared bitstream readers - a translation of
// ffmpeg-master/libavcodec/wma.c and wma_common.c.
//
// Everything here is initialisation except the last two methods, which wmadec.c calls per block.
// The initialisation is most of what distinguishes one WMA stream from another: the band layout,
// which coefficients are coded, where the high band starts and which of the six coefficient code
// sets to use are all derived here from the sample rate, the bit rate and the channel count, and a
// decoder that got any of them wrong would produce plausible-sounding rubbish rather than fail.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Wma;

internal static class Wma
{
    /// <summary>
    /// <c>ff_wma_get_frame_len_bits</c> (wma_common.c): log2 of the output samples per frame.
    /// </summary>
    public static int GetFrameLenBits(int sampleRate, int version, uint decodeFlags)
    {
        int frameLenBits;

        if (sampleRate <= 16000)
            frameLenBits = 9;
        else if (sampleRate <= 22050 || (sampleRate <= 32000 && version == 1))
            frameLenBits = 10;
        else if (sampleRate <= 48000 || version < 3)
            frameLenBits = 11;
        else if (sampleRate <= 96000)
            frameLenBits = 12;
        else
            frameLenBits = 13;

        if (version == 3)
        {
            uint tmp = decodeFlags & 0x6;
            if (tmp == 0x2)
                ++frameLenBits;
            else if (tmp == 0x4)
                --frameLenBits;
            else if (tmp == 0x6)
                frameLenBits -= 2;
        }

        return frameLenBits;
    }

    /// <summary>
    /// <c>init_coef_vlc</c>: build one coefficient VLC and the run, level and escape tables that
    /// go with it.
    /// </summary>
    /// <remarks>
    /// The run and level tables are not in the bitstream or in the C's data: they are *derived*
    /// from the levels table by walking it, and the walk is the only thing that says which code
    /// means which (run, level) pair. <c>int_table[k]</c> records where level <c>k + 1</c> starts,
    /// which the escape path needs.
    /// </remarks>
    private static void InitCoefVlc(int tableIndex, out VLCElem[] vlc, out ushort[] runTable,
                                    out float[] levelTable, out ushort[] intTable)
    {
        ReadOnlySpan<byte> tableBits = WmaCoefVlcTables.HuffBits(tableIndex);
        ReadOnlySpan<uint> tableCodes = WmaCoefVlcTables.HuffCodes(tableIndex);
        ReadOnlySpan<ushort> levelsTable = WmaCoefVlcTables.Levels(tableIndex);
        int n = tableBits.Length;

        vlc = BuildVlc(WmaLimits.VlcBits, n, tableBits, tableCodes);

        runTable = new ushort[n];
        levelTable = new float[n];
        intTable = new ushort[n];

        int i = 2;
        int level = 1;
        int k = 0;
        while (i < n)
        {
            intTable[k] = (ushort)i;
            int l = levelsTable[k++];
            for (int j = 0; j < l; j++)
            {
                runTable[i] = (ushort)j;
                levelTable[i] = level;
                i++;
            }
            level++;
        }
    }

    /// <summary>
    /// Build a VLC from a (length, code) pair of tables, the shape <c>vlc_init</c> takes.
    /// </summary>
    /// <remarks>
    /// The table is sized by measurement rather than by a declared constant. FFmpeg's WMA uses
    /// <c>vlc_init</c>, which grows its table as it builds, so unlike its static tables there is
    /// no size in the C to copy across; the builder here refuses to overflow and says how many
    /// entries it wanted, so the sizes below came from running it. The first level is
    /// 1 &lt;&lt; nbBits entries and the subtables follow it.
    /// </remarks>
    private static VLCElem[] BuildVlc(int nbBits, int nbCodes,
                                      ReadOnlySpan<byte> lengths, ReadOnlySpan<uint> codes)
    {
        // Copied out because a span cannot be captured by the lambdas the builder takes.
        byte[] lengthArray = lengths.ToArray();
        uint[] codeArray = codes.ToArray();

        // Grown rather than guessed: the builder throws when the table is too small, and doubling
        // from the first level's size settles in one or two steps for every table here.
        int size = Math.Max(1 << nbBits, 1 << nbBits);
        while (true)
        {
            var table = new VLCElem[size];
            try
            {
                Vlc.InitTableSparse(table, nbBits, nbCodes,
                                    i => lengthArray[i], i => codeArray[i]);
                return table;
            }
            catch (InvalidOperationException) when (size < 1 << 20)
            {
                size *= 2;
            }
        }
    }

    /// <summary>
    /// Build a VLC from a table of (symbol, length) pairs, the shape
    /// <c>ff_vlc_init_from_lengths</c> takes.
    /// </summary>
    /// <remarks>
    /// The assignment rule lives in <see cref="Vlc.InitFromLengths"/>, which is where the reason
    /// it cannot be guessed is written down. This is the strided-pair adaptor FFmpeg gets for
    /// free from passing <c>&amp;table[0][1]</c> and <c>&amp;table[0][0]</c> with a stride of two.
    /// </remarks>
    private static VLCElem[] BuildVlcFromLengths(int nbBits, ReadOnlySpan<byte> table, int stride,
                                                 int symbolOffset, int lengthOffset, int entries)
    {
        // Copied out because a span cannot be captured by the accessors the builder takes.
        byte[] pairs = table[..(entries * stride)].ToArray();
        return Vlc.InitFromLengths(nbBits, entries,
                                   i => pairs[i * stride + lengthOffset],
                                   i => pairs[i * stride + symbolOffset]);
    }

    /// <summary>
    /// The coefficient VLC for one code set, for the gate that round-trips every code.
    /// </summary>
    internal static VLCElem[] BuildCoefVlcForTest(int tableIndex)
        => BuildVlc(WmaLimits.VlcBits, WmaCoefVlcTables.HuffBits(tableIndex).Length,
                    WmaCoefVlcTables.HuffBits(tableIndex), WmaCoefVlcTables.HuffCodes(tableIndex));

    /// <summary>
    /// <c>ff_wma_init</c>: everything derived from the stream's rate, bit rate and flags.
    /// </summary>
    /// <returns>False where the C returns a negative error code.</returns>
    public static bool Init(WmaContext s, int flags2)
    {
        int channels = s.Channels;

        if (s.SampleRate > 50000 || channels > 2 || s.BitRate <= 0)
            return false;

        // Compute the MDCT block size.
        s.FrameLenBits = GetFrameLenBits(s.SampleRate, s.Version, 0);
        s.NextBlockLenBits = s.FrameLenBits;
        s.PrevBlockLenBits = s.FrameLenBits;
        s.BlockLenBits = s.FrameLenBits;

        s.FrameLen = 1 << s.FrameLenBits;
        if (s.UseVariableBlockLen)
        {
            int nb = ((flags2 >> 3) & 3) + 1;
            if (s.BitRate / channels >= 32000)
                nb += 2;
            int nbMax = s.FrameLenBits - WmaLimits.BlockMinBits;
            if (nb > nbMax)
                nb = nbMax;
            s.NbBlockSizes = nb + 1;
        }
        else
        {
            s.NbBlockSizes = 1;
        }

        // Rate dependent parameters.
        s.UseNoiseCoding = true;
        float highFreq = s.SampleRate * 0.5f;

        // For version 2 the rates are normalised.
        int sampleRate1 = s.SampleRate;
        if (s.Version == 2)
        {
            if (sampleRate1 >= 44100)
                sampleRate1 = 44100;
            else if (sampleRate1 >= 22050)
                sampleRate1 = 22050;
            else if (sampleRate1 >= 16000)
                sampleRate1 = 16000;
            else if (sampleRate1 >= 11025)
                sampleRate1 = 11025;
            else if (sampleRate1 >= 8000)
                sampleRate1 = 8000;
        }

        float bps = (float)s.BitRate / (channels * s.SampleRate);
        s.ByteOffsetBits = Log2((int)(bps * s.FrameLen / 8.0 + 0.5)) + 2;

        // MIN_CACHE_BITS is 25 in the C's bit reader. The C refuses the stream rather than
        // reading a field it cannot hold.
        if (s.ByteOffsetBits + 3 > 25)
            return false;

        // The high frequency value, and whether noise coding is on.
        float bps1 = bps;
        if (channels == 2)
            bps1 = bps * 1.6f;

        if (sampleRate1 == 44100)
        {
            if (bps1 >= 0.61f)
                s.UseNoiseCoding = false;
            else
                highFreq *= 0.4f;
        }
        else if (sampleRate1 == 22050)
        {
            if (bps1 >= 1.16f)
                s.UseNoiseCoding = false;
            else if (bps1 >= 0.72f)
                highFreq *= 0.7f;
            else
                highFreq *= 0.6f;
        }
        else if (sampleRate1 == 16000)
        {
            highFreq *= bps > 0.5f ? 0.5f : 0.3f;
        }
        else if (sampleRate1 == 11025)
        {
            highFreq *= 0.7f;
        }
        else if (sampleRate1 == 8000)
        {
            if (bps <= 0.625f)
                highFreq *= 0.5f;
            else if (bps > 0.75f)
                s.UseNoiseCoding = false;
            else
                highFreq *= 0.65f;
        }
        else
        {
            if (bps >= 0.8f)
                highFreq *= 0.75f;
            else if (bps >= 0.6f)
                highFreq *= 0.6f;
            else
                highFreq *= 0.5f;
        }

        // The scale factor band sizes, for each MDCT block size.
        s.CoefsStart = s.Version == 1 ? 3 : 0;

        for (int k = 0; k < s.NbBlockSizes; k++)
        {
            int blockLen = s.FrameLen >> k;
            int i, j, pos, lpos, n, a, b;

            if (s.Version == 1)
            {
                lpos = 0;
                for (i = 0; i < 25; i++)
                {
                    a = WmaTables.CriticalFreqs[i];
                    b = s.SampleRate;
                    pos = ((blockLen * 2 * a) + (b >> 1)) / b;
                    if (pos > blockLen)
                        pos = blockLen;
                    s.ExponentBands[0, i] = pos - lpos;
                    if (pos >= blockLen)
                    {
                        i++;
                        break;
                    }
                    lpos = pos;
                }
                s.ExponentSizes[0] = i;
            }
            else
            {
                // The hardcoded tables, where there is one for this rate and block size.
                ReadOnlySpan<byte> table = default;
                bool haveTable = false;
                a = s.FrameLenBits - WmaLimits.BlockMinBits - k;
                if (a < 3)
                {
                    const int stride = WmaTables.ExponentBand44100Stride;
                    if (s.SampleRate >= 44100)
                    {
                        table = WmaTables.ExponentBand44100.Slice(a * stride, stride);
                        haveTable = true;
                    }
                    else if (s.SampleRate >= 32000)
                    {
                        table = WmaTables.ExponentBand32000.Slice(a * stride, stride);
                        haveTable = true;
                    }
                    else if (s.SampleRate >= 22050)
                    {
                        table = WmaTables.ExponentBand22050.Slice(a * stride, stride);
                        haveTable = true;
                    }
                }

                if (haveTable)
                {
                    // The first value of a row is the band count; the bands follow it.
                    n = table[0];
                    for (i = 0; i < n; i++)
                        s.ExponentBands[k, i] = table[1 + i];
                    s.ExponentSizes[k] = n;
                }
                else
                {
                    j = 0;
                    lpos = 0;
                    for (i = 0; i < 25; i++)
                    {
                        a = WmaTables.CriticalFreqs[i];
                        b = s.SampleRate;
                        pos = ((blockLen * 2 * a) + (b << 1)) / (4 * b);
                        pos <<= 2;
                        if (pos > blockLen)
                            pos = blockLen;
                        if (pos > lpos)
                            s.ExponentBands[k, j++] = pos - lpos;
                        if (pos >= blockLen)
                            break;
                        lpos = pos;
                    }
                    s.ExponentSizes[k] = j;
                }
            }

            // Max number of coefs.
            s.CoefsEnd[k] = (s.FrameLen - ((s.FrameLen * 9) / 100)) >> k;

            // High frequency computation.
            s.HighBandStart[k] = (int)((blockLen * 2 * highFreq) / s.SampleRate + 0.5);

            n = s.ExponentSizes[k];
            j = 0;
            pos = 0;
            for (i = 0; i < n; i++)
            {
                int start = pos;
                pos += s.ExponentBands[k, i];
                int end = pos;
                if (start < s.HighBandStart[k])
                    start = s.HighBandStart[k];
                if (end > s.CoefsEnd[k])
                    end = s.CoefsEnd[k];
                if (end > start)
                    s.ExponentHighBands[k, j++] = end - start;
            }
            s.ExponentHighSizes[k] = j;
        }

        // One inverse MDCT and one window per block size.
        for (int i = 0; i < s.NbBlockSizes; i++)
        {
            s.Mdct[i] = Dsp.Tx.TxMdct.Create(
                1 << (s.FrameLenBits - i), inverse: true, full: true, 1.0f / 32768.0f);
            s.Windows[i] = Dsp.SineWindows.Get(1 << (s.FrameLenBits - i));
        }

        s.ResetBlockLengths = true;

        if (s.UseNoiseCoding)
        {
            // The noise generator. The constants are the C's, including the deliberate wrap of
            // the unsigned seed and the cast to a signed int before scaling.
            s.NoiseMult = s.UseExpVlc ? 0.02f : 0.04f;

            uint seed = 1;
            float norm = (float)((1.0 / (1L << 31)) * Math.Sqrt(3) * s.NoiseMult);
            for (int i = 0; i < WmaLimits.NoiseTabSize; i++)
            {
                seed = seed * 314159 + 1;
                s.NoiseTable[i] = (int)seed * norm;
            }
        }

        // Choose the VLC tables for the coefficients.
        int coefVlcTable = 2;
        if (s.SampleRate >= 32000)
        {
            if (bps1 < 0.72f)
                coefVlcTable = 0;
            else if (bps1 < 1.16f)
                coefVlcTable = 1;
        }

        for (int i = 0; i < 2; i++)
        {
            int index = coefVlcTable * 2 + i;
            s.CoefVlcIndex[i] = index;
            InitCoefVlc(index, out VLCElem[] vlc, out ushort[] run, out float[] level,
                        out ushort[] ints);
            s.CoefVlc[i] = vlc;
            s.RunTable[i] = run;
            s.LevelTable[i] = level;
            s.IntTable[i] = ints;
        }

        return true;
    }

    /// <summary>
    /// The exponent and high-band-gain VLCs, which wmadec.c builds after <see cref="Init"/>.
    /// </summary>
    public static void InitDecoderVlcs(WmaContext s)
    {
        if (s.UseNoiseCoding)
        {
            s.HGainVlc = BuildVlcFromLengths(
                WmaLimits.HGainVlcBits, WmaTables.HgainHufftab,
                WmaTables.HgainHufftabStride, symbolOffset: 0, lengthOffset: 1, entries: 37);
        }

        if (s.UseExpVlc)
        {
            s.ExpVlc = BuildVlc(WmaLimits.ExpVlcBits, WmaTables.AacScalefactorBits.Length,
                                WmaTables.AacScalefactorBits, WmaTables.AacScalefactorCode);
        }
    }

    /// <summary><c>ff_wma_total_gain_to_bits</c>.</summary>
    public static int TotalGainToBits(int totalGain) => totalGain switch
    {
        < 15 => 13,
        < 32 => 12,
        < 40 => 11,
        < 45 => 10,
        _ => 9,
    };

    /// <summary><c>ff_wma_get_large_val</c>: an uncompressed coefficient, up to 34 bits.</summary>
    public static uint GetLargeVal(ref GetBitContext gb)
    {
        int nBits = 8;
        if (gb.GetBits1() != 0)
        {
            nBits += 8;
            if (gb.GetBits1() != 0)
            {
                nBits += 8;
                if (gb.GetBits1() != 0)
                    nBits += 7;
            }
        }
        // get_bits_long in the C, which exists only because its reader caches 25 bits at a time;
        // this one assembles a bit at a time and handles up to 32, so GetBits covers it. The
        // maximum here is 31 bits (8 + 8 + 8 + 7).
        return gb.GetBits(nBits);
    }

    /// <summary>
    /// <c>ff_wma_run_level_decode</c>: the run-length coded spectral coefficients.
    /// </summary>
    /// <remarks>
    /// The C writes the level through a <c>uint32_t *</c> aliased over the float output so it can
    /// apply the sign by flipping the top bit - <c>iptr[offset] = ilvl[code] ^ (sign &amp;
    /// 0x80000000)</c>. That is reproduced with a bit cast rather than arithmetic, because the two
    /// are not the same for a zero level: negating 0.0f gives -0.0f, which carries the sign bit,
    /// and the IMDCT is sensitive to it.
    /// </remarks>
    public static bool RunLevelDecode(ref GetBitContext gb, ReadOnlySpan<VLCElem> vlc,
                                      ReadOnlySpan<float> levelTable, ReadOnlySpan<ushort> runTable,
                                      int version, Span<float> ptr, int offset, int numCoefs,
                                      int blockLen, int frameLenBits, int coefNbBits)
    {
        uint coefMask = (uint)blockLen - 1;

        for (; offset < numCoefs; offset++)
        {
            int code = gb.GetVlc2(vlc, WmaLimits.VlcBits, WmaLimits.VlcMax);
            if (code > 1)
            {
                // Normal code.
                offset += runTable[code];
                // get_bits1() - 1 is 0 or -1 in the C, where the result is a signed int. Taken
                // as unsigned it wraps to 0xFFFFFFFF, which is the same bit pattern but makes the
                // mask below read as a mistake, so the cast is explicit.
                int sign = (int)gb.GetBits1() - 1;
                uint bits = BitConverter.SingleToUInt32Bits(levelTable[code]);
                ptr[(int)(offset & coefMask)] =
                    BitConverter.UInt32BitsToSingle(bits ^ ((uint)sign & 0x80000000u));
            }
            else if (code == 1)
            {
                // End of block.
                break;
            }
            else
            {
                // Escape.
                int level;
                if (version == 0)
                {
                    level = (int)gb.GetBits(coefNbBits);

                    // The C's note: "this is rather suboptimal. reading block_len_bits would be
                    // better".
                    offset += (int)gb.GetBits(frameLenBits);
                }
                else
                {
                    level = (int)GetLargeVal(ref gb);

                    // Escape decode.
                    if (gb.GetBits1() != 0)
                    {
                        if (gb.GetBits1() != 0)
                        {
                            if (gb.GetBits1() != 0)
                                return false;   // broken escape sequence
                            offset += (int)gb.GetBits(frameLenBits) + 4;
                        }
                        else
                        {
                            offset += (int)gb.GetBits(2) + 1;
                        }
                    }
                }

                int sign = (int)gb.GetBits1() - 1;
                ptr[(int)(offset & coefMask)] = (level ^ sign) - sign;
            }
        }

        // The C's note: end of block can be omitted.
        return offset <= numCoefs;
    }

    /// <summary><c>av_log2</c>.</summary>
    private static int Log2(int value) => value <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
}

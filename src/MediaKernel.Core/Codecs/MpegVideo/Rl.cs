// FFmpeg's rl.c and rl.h - the run-length tables H.263, MPEG-4 Part 2 and the MSMPEG-4 family
// read their coefficients through.
//
// A port note that matters: the C's ff_rl_init takes a caller-supplied
// uint8_t static_store[2][2 * MAX_RUN + MAX_LEVEL + 3] and carves max_level, max_run and
// index_run out of it, reading max_level and max_run through int8_t* views of the same
// unsigned bytes. It is one buffer because C wants one static allocation per table, and the
// function only ever RAISES max_level and max_run, so it depends on that buffer starting at
// zero. Three arrays per "last" are used here instead, which a new C# array zeroes for the
// same reason; the signed view is immaterial because neither value can exceed MAX_LEVEL.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// One run-length table (<c>RLTable</c>), with the derived tables <see cref="Rl.Init"/> and
/// <see cref="Rl.InitVlc"/> fill in.
/// </summary>
/// <remarks>
/// A class rather than a struct: there are a dozen of these in the whole library, each built
/// once at startup and then read through, and the derived arrays are shared by reference.
/// </remarks>
public sealed class RlTable
{
    /// <summary><c>n</c> - number of entries of <see cref="TableVlc"/> minus one.</summary>
    public int N;

    /// <summary><c>last</c> - number of values for <c>last == 0</c>.</summary>
    public int Last;

    /// <summary>
    /// <c>table_vlc</c> - the C's <c>uint16_t[n + 1][2]</c>, flattened: entry <c>i</c>'s code is
    /// at <c>2 * i</c> and its length at <c>2 * i + 1</c>.
    /// </summary>
    public ushort[] TableVlc = [];

    /// <summary><c>table_run</c>.</summary>
    public sbyte[] TableRun = [];

    /// <summary><c>table_level</c>.</summary>
    public sbyte[] TableLevel = [];

    /// <summary><c>index_run[2]</c> - encoding only; first index of each run.</summary>
    public byte[]?[] IndexRun = new byte[]?[2];

    /// <summary><c>max_level[2]</c> - the largest level that occurs at each run.</summary>
    public sbyte[]?[] MaxLevel = new sbyte[]?[2];

    /// <summary><c>max_run[2]</c> - the largest run that occurs at each level.</summary>
    public sbyte[]?[] MaxRun = new sbyte[]?[2];

    /// <summary>
    /// <c>rl_vlc[32]</c> - one decoding table per quantiser, with the level already scaled by
    /// that quantiser. A null entry stops <see cref="Rl.InitVlc"/>, which is how the C's
    /// <c>INIT_FIRST_VLC_RL</c> builds only the first.
    /// </summary>
    public VLCElem[]?[] RlVlc = new VLCElem[]?[32];
}

/// <summary>Run-length table construction (<c>rl.c</c>).</summary>
public static class Rl
{
    /// <summary><c>MAX_RUN</c>.</summary>
    public const int MaxRunValue = 64;

    /// <summary><c>MAX_LEVEL</c>.</summary>
    public const int MaxLevelValue = 64;

    /// <summary>
    /// <c>ff_rl_init_level_run</c> - the same derivation as <see cref="Init"/> for
    /// <c>last == 0</c> alone, for callers that hold the two tables themselves.
    /// </summary>
    public static void InitLevelRun(Span<byte> maxLevel, Span<byte> indexRun,
                                    ReadOnlySpan<byte> tableRun, ReadOnlySpan<byte> tableLevel,
                                    int n)
    {
        // The sentinel is n itself: "no entry has this run yet".
        indexRun[..(MaxRunValue + 1)].Fill((byte)n);

        for (int i = 0; i < n; i++)
        {
            int run = tableRun[i];
            int level = tableLevel[i];
            if (indexRun[run] == n)
                indexRun[run] = (byte)i;
            if (level > maxLevel[run])
                maxLevel[run] = (byte)level;
        }
    }

    /// <summary>
    /// <c>ff_rl_init</c> - derives <c>index_run</c>, <c>max_level</c> and <c>max_run</c> from
    /// the table's codes, runs and levels, for both values of <c>last</c>.
    /// </summary>
    public static void Init(RlTable rl)
    {
        for (int last = 0; last < 2; last++)
        {
            // Zeroed by the allocation, which is what the C's static store relies on: the loop
            // below only ever raises these.
            var maxLevel = new sbyte[MaxRunValue + 1];
            var maxRun = new sbyte[MaxLevelValue + 1];
            var indexRun = new byte[MaxRunValue + 1];

            int start, end;
            if (last == 0)
            {
                start = 0;
                end = rl.Last;
            }
            else
            {
                start = rl.Last;
                end = rl.N;
            }

            indexRun.AsSpan().Fill((byte)rl.N);

            for (int i = start; i < end; i++)
            {
                int run = rl.TableRun[i];
                int level = rl.TableLevel[i];
                if (indexRun[run] == rl.N)
                    indexRun[run] = (byte)i;
                if (level > maxLevel[run])
                    maxLevel[run] = (sbyte)level;
                if (run > maxRun[level])
                    maxRun[level] = (sbyte)run;
            }

            rl.MaxLevel[last] = maxLevel;
            rl.MaxRun[last] = maxRun;
            rl.IndexRun[last] = indexRun;
        }
    }

    /// <summary>
    /// <c>ff_rl_init_vlc</c> - builds the decoding table for quantiser 0 and then derives the
    /// other 31 from it, each with the level pre-multiplied by its quantiser.
    /// </summary>
    /// <param name="rl">The table, whose <c>RlVlc</c> slots the caller has already sized.</param>
    /// <param name="staticSize">
    /// Entries per slot. The C's callers pass a number tuned per table (554, 1072, ...), and the
    /// subtables the builder emits make the real extent depend on it, so it is not derivable
    /// from <c>n</c>.
    /// </param>
    public static void InitVlc(RlTable rl, int staticSize)
    {
        VLCElem[] first = rl.RlVlc[0]
            ?? throw new InvalidOperationException("rl_vlc[0] must be allocated before InitVlc.");

        ushort[] tableVlc = rl.TableVlc;
        Vlc.InitTableSparse(
            first.AsSpan(0, staticSize),
            nbBits: 9,
            nbCodes: rl.N + 1,
            lengthOf: i => tableVlc[2 * i + 1],
            codeOf: i => tableVlc[2 * i]);

        // Counting down matters: quantiser 0's slot is both the source every other quantiser
        // reads and the last one overwritten, so going up would trash it on the first step.
        // The C's own comment says as much.
        for (int q = 31; q >= 0; q--)
        {
            int qmul = q * 2;
            int qadd = (q - 1) | 1;

            VLCElem[]? dst = rl.RlVlc[q];
            if (dst is null)
                continue;

            if (q == 0)
            {
                qmul = 1;
                qadd = 0;
            }

            for (int i = 0; i < staticSize; i++)
            {
                int idx = first[i].Sym;
                int len = first[i].Len;
                int level, run;

                if (len == 0)
                {
                    // Illegal code. Run 66 is past the block, which is how the reader detects it.
                    run = 66;
                    level = MaxLevelValue;
                }
                else if (len < 0)
                {
                    // More bits needed; the symbol is the subtable's base index.
                    run = 0;
                    level = idx;
                }
                else if (idx == rl.N)
                {
                    // The escape code.
                    run = 66;
                    level = 0;
                }
                else
                {
                    run = rl.TableRun[idx] + 1;
                    level = rl.TableLevel[idx] * qmul + qadd;
                    if (idx >= rl.Last)
                        run += 192;
                }

                dst[i].Len8 = (sbyte)len;
                dst[i].Level = (short)level;
                dst[i].Run = (byte)run;
            }
        }
    }

    /// <summary><c>get_rl_index</c> - encoding only.</summary>
    public static int GetRlIndex(RlTable rl, int last, int run, int level)
    {
        if (level > rl.MaxLevel[last]![run])
            return rl.N;
        return rl.IndexRun[last]![run] + level - 1;
    }
}

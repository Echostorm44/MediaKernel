// Variable-length code tables - a translation of the parts of ffmpeg-master/libavcodec/vlc.h and
// vlc.c that the MPEG-video decoders use.
//
// The subset is the static-table initialiser mpeg12.c reaches through VLC_INIT_STATIC_TABLE and
// VLC_INIT_STATIC_SPARSE_TABLE, the recursive builder behind it, and the run-length variant in
// mpeg12.c. The multi-symbol tables (VLC_MULTI) and the little-endian input and output modes are
// not in scope: nothing in the MPEG-video path uses them.
//
// The builder is ported rather than replaced. A simpler one that decodes the same codes would be
// easy to write and wrong, because get_vlc2's two-level lookup is coupled to the exact layout
// this builder produces - a negative length meaning "a subtable lives at sym" is the contract
// between them, so reinventing one half silently breaks the other.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// <c>VLCElem</c> (<c>vlc.h:32</c>) - one entry of a VLC lookup table.
/// </summary>
/// <remarks>
/// <para>
/// A union in C, and an explicit-layout struct here, which C# supports directly. The first
/// reading - <see cref="Sym"/> and <see cref="Len"/> - is what <c>get_vlc2</c> uses. The second -
/// <see cref="Level"/>, <see cref="Len8"/>, <see cref="Run"/> - is the run-length form used for
/// coefficient tables, where the same four bytes carry a decoded run and level instead.
/// </para>
/// <para>
/// <see cref="Len"/> carries three cases, and they are the whole protocol between the builder and
/// the reader: positive is the code's length in bits; zero means no code, with <see cref="Sym"/>
/// set to -1; negative means the code is longer than this table is wide, so <c>-Len</c> further
/// bits must be read and <see cref="Sym"/> is the base index of the subtable to read them in.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct VLCElem
{
    /// <summary>The decoded symbol, or a subtable base index when <see cref="Len"/> is negative.</summary>
    [FieldOffset(0)] public short Sym;

    /// <summary>Code length in bits; see the remarks for the zero and negative cases.</summary>
    [FieldOffset(2)] public short Len;

    /// <summary>Run-length reading: the coefficient level. Overlays <see cref="Sym"/>.</summary>
    [FieldOffset(0)] public short Level;

    /// <summary>Run-length reading: the code length as a byte. Overlays the low half of <see cref="Len"/>.</summary>
    [FieldOffset(2)] public sbyte Len8;

    /// <summary>Run-length reading: the zero run before the level. Overlays the high half of <see cref="Len"/>.</summary>
    [FieldOffset(3)] public byte Run;
}

/// <summary>VLC table construction (<c>vlc.c</c>).</summary>
public static class Vlc
{
    /// <summary>One code while a table is being built (<c>VLCcode</c>).</summary>
    private struct VlcCode
    {
        /// <summary>The code, left-aligned in 32 bits.</summary>
        public uint Code;

        /// <summary>The decoded symbol.</summary>
        public short Symbol;

        /// <summary>Length in bits.</summary>
        public byte Bits;
    }

    private sealed class VlcCodeComparer : IComparer<VlcCode>
    {
        public static readonly VlcCodeComparer Instance = new();
        public int Compare(VlcCode a, VlcCode b) => a.Code.CompareTo(b.Code);
    }

    /// <summary>
    /// <c>ff_vlc_init_table_sparse</c> - builds <paramref name="table"/> from a code length and a
    /// code per entry, and optionally an explicit symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg passes each column as a pointer with a <c>wrap</c> stride and
    /// a <c>size</c> width, because C cannot hand over a strided view. That is how a single
    /// <c>uint8_t[36][2]</c> supplies both the lengths and the codes, from <c>&amp;table[0][1]</c>
    /// and <c>&amp;table[0][0]</c> with a stride of two. Reproducing that arithmetic here would
    /// mean reading a two-byte column out of a byte span, which is correct only on a
    /// little-endian machine and is easy to get subtly wrong. Each column is taken as an accessor
    /// instead: it is called once per entry while a table is built, for seven tables at startup,
    /// so the indirection costs nothing measurable and cannot be endian-dependent.
    /// </para>
    /// <para>
    /// The two-pass copy with a sort between the passes is FFmpeg's and is not incidental. Codes
    /// longer than <paramref name="nbBits"/> go in first and sorted, the short ones after,
    /// because <see cref="BuildTable"/> walks the array once and needs every code that shares a
    /// subtable to be contiguous.
    /// </para>
    /// </remarks>
    /// <param name="table">Destination, sized by the caller as FFmpeg's static tables are.</param>
    /// <param name="nbBits">Bits looked up at the first level.</param>
    /// <param name="nbCodes">Number of entries to consider.</param>
    /// <param name="lengthOf">Code length in bits of entry <c>i</c>; zero means no code.</param>
    /// <param name="codeOf">The code of entry <c>i</c>, right-aligned within its length.</param>
    /// <param name="symbolOf">Symbol of entry <c>i</c>, or null to use <c>i</c> itself.</param>
    /// <returns>How many entries of <paramref name="table"/> the built table occupies.</returns>
    public static int InitTableSparse(
        Span<VLCElem> table,
        int nbBits,
        int nbCodes,
        Func<int, int> lengthOf,
        Func<int, uint> codeOf,
        Func<int, int>? symbolOf = null,
        bool littleEndian = false)
    {
        var buf = new VlcCode[nbCodes];
        int j = 0;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < nbCodes; i++)
            {
                int len = lengthOf(i);
                bool isLong = len > nbBits;

                // Pass 0 takes the long codes, pass 1 the short non-zero ones. A zero length
                // means "no code here" and belongs to neither pass.
                if (pass == 0 ? !isLong : !(len != 0 && !isLong))
                    continue;

                if (len > 3 * nbBits || len > 32)
                    throw new InvalidOperationException(
                        $"VLC code {i} is {len} bits, too long for a {nbBits}-bit table.");

                uint code = codeOf(i);
                if (len < 32 && code >= (1u << len))
                    throw new InvalidOperationException(
                        $"VLC code {i} (0x{code:X}) does not fit in {len} bits.");

                buf[j].Bits = (byte)len;
                buf[j].Code = len == 32 ? code : code << (32 - len);
                buf[j].Symbol = (short)(symbolOf is null ? i : symbolOf(i));
                j++;
            }

            if (pass == 0)
            {
                // compare_vlcspec sorts on code >> 1, so two codes differing only in their lowest
                // bit compare equal and C leaves their relative order unspecified. Sorting on the
                // whole code refines that: deterministic where the C is not, and still satisfying
                // the contiguity the builder needs. FFmpeg's own comment says sorting by code is
                // sufficient though not necessary.
                Array.Sort(buf, 0, j, VlcCodeComparer.Instance);
            }
        }

        table.Clear();
        int used = 0;
        BuildTable(table, ref used, nbBits, j, buf, 0, littleEndian);
        return used;
    }

    /// <summary>
    /// <c>ff_vlc_init_tables_sparse</c> - the same builder, for a caller whose table size is
    /// not in its source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FFmpeg's version hands out slices of one big static pool: <c>VLC_INIT_STATE</c> points a
    /// <c>VLCInitState</c> at the array, each call builds at the cursor and advances it, and the
    /// table's length is implicitly the distance to the next one. VC-1 builds twenty-five
    /// families of tables that way out of a single <c>VLCElem[32372]</c>, and not one of those
    /// lengths is written down anywhere in the C.
    /// </para>
    /// <para>
    /// DIVERGENCE, recorded: there is no pool here. The builder already reports how much of a
    /// destination it used, so each table is built into a scratch buffer and copied out at
    /// exactly that length - the same lengths FFmpeg's cursor would have produced, since they
    /// come from the same <see cref="BuildTable"/> walk, but each in its own array. A pool would
    /// buy one allocation at startup and cost every reader a slice, and <c>Span</c> cannot be
    /// stored in the static fields these tables live in.
    /// </para>
    /// </remarks>
    public static VLCElem[] InitTablesSparse(
        int nbBits,
        int nbCodes,
        Func<int, int> lengthOf,
        Func<int, uint> codeOf,
        Func<int, int>? symbolOf = null)
    {
        int size = 1 << nbBits;
        while (true)
        {
            var scratch = new VLCElem[size];
            try
            {
                int used = InitTableSparse(scratch, nbBits, nbCodes, lengthOf, codeOf, symbolOf);
                return scratch[..used];
            }
            catch (InvalidOperationException) when (size < 1 << 20)
            {
                size *= 2;
            }
        }
    }

    /// <summary>
    /// <c>ff_vlc_init_from_lengths</c> - builds a table from code <i>lengths</i> alone, assigning
    /// the codes itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assignment is ported rather than reinvented, because the obvious guess is wrong and
    /// was tried first here: it is <b>not</b> "sort by length, then count up". The C keeps a
    /// 32-bit accumulator of the code <i>left-aligned</i>, walks the table in its own order, and
    /// after each entry adds <c>1 &lt;&lt; (32 - len)</c> - one slot of that length. Entries
    /// therefore come out in table order, and a table whose lengths are not sorted - WMA's hgain
    /// table runs 10, 10, 10, 10, 9, 9, 8, ... then back up to 13 - gets completely different
    /// codes under the two rules. Guessing it cost 1379 of 1395 WMA packets.
    /// </para>
    /// <para>
    /// A negative length means "advance the code as if a code of that length were here, but emit
    /// nothing", which is how the C leaves holes in a tree; zero means "no code" and does not
    /// advance. <see cref="InitTableSparse"/> wants each code right-aligned within its length,
    /// which is the accumulator shifted down by <c>32 - len</c>.
    /// </para>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg's callers size the destination statically and pass a slice of
    /// one big array, so the table size is in their source and can be copied across. Nothing here
    /// has that array, so the table is grown until the builder stops objecting - it throws with
    /// the size it wanted - and returned. That is a startup cost of a few rebuilds per table,
    /// once per process.
    /// </para>
    /// </remarks>
    /// <param name="nbBits">Bits looked up at the first level.</param>
    /// <param name="nbCodes">Number of entries in the source table.</param>
    /// <param name="lengthOf">Length in bits of entry <c>i</c>; see the remarks for 0 and &lt;0.</param>
    /// <param name="symbolOf">Symbol of entry <c>i</c>, or null to use <c>i</c> itself.</param>
    /// <param name="symbolOffset">Added to every symbol, FFmpeg's <c>offset</c> argument.</param>
    /// <param name="littleEndian">
    /// FFmpeg's <c>VLC_INIT_LE</c>, for a codec whose file begins
    /// <c>#define BITSTREAM_READER_LE</c> - DTS Express is the one here. The table is built
    /// bit-reversed so that a little-endian reader's <c>ShowBits</c> indexes it directly; read
    /// it with <see cref="GetBitContextLe"/> and nothing else.
    /// </param>
    /// <remarks>
    /// <para>
    /// A note on the flag, because the obvious reading of the C is wrong here:
    /// <c>VLC_INIT_LE</c> is <c>VLC_INIT_INPUT_LE | VLC_INIT_OUTPUT_LE</c>, but
    /// <c>ff_vlc_init_from_lengths</c> <b>ignores the INPUT half entirely</b>. INPUT_LE only
    /// bit-swaps codes that were READ from a table, in <c>ff_vlc_init_sparse</c>; this function
    /// generates its codes from the lengths, left-aligned, and the only flag that reaches
    /// <c>build_table</c> is OUTPUT_LE. So the one difference this parameter makes is the
    /// table's index order.
    /// </para>
    /// </remarks>
    public static VLCElem[] InitFromLengths(
        int nbBits,
        int nbCodes,
        Func<int, int> lengthOf,
        Func<int, int>? symbolOf,
        int symbolOffset = 0,
        bool littleEndian = false)
    {
        // len_max in the C. A length past it is a corrupt table, not a deep one.
        int lengthMax = Math.Min(32, 3 * nbBits);

        var lengths = new int[nbCodes];
        var symbols = new int[nbCodes];
        var codes = new uint[nbCodes];

        ulong code = 0;
        for (int i = 0; i < nbCodes; i++)
        {
            int length = lengthOf(i);
            int advance;

            if (length > 0)
            {
                lengths[i] = length;
                symbols[i] = (symbolOf is null ? i : symbolOf(i)) + symbolOffset;
                // The accumulator is left-aligned; InitTableSparse wants it right-aligned.
                codes[i] = (uint)(code >> (32 - length));
                advance = length;
            }
            else if (length < 0)
            {
                advance = -length;
            }
            else
            {
                continue;
            }

            if (advance > lengthMax || (code & ((1u << (32 - advance)) - 1)) != 0)
                throw new InvalidOperationException(
                    $"VLC entry {i} has length {advance}, which does not fit a {nbBits}-bit "
                  + $"table (max {lengthMax}) or is misaligned for it.");

            code += 1UL << (32 - advance);

            if (code > (ulong)uint.MaxValue + 1)
                throw new InvalidOperationException(
                    $"Overdetermined VLC tree: the lengths overflow the code space at entry {i}.");
        }

        int size = 1 << nbBits;
        while (true)
        {
            var table = new VLCElem[size];
            try
            {
                InitTableSparse(table, nbBits, nbCodes,
                                i => lengths[i], i => codes[i], i => symbols[i],
                                littleEndian);
                return table;
            }
            catch (InvalidOperationException) when (size < 1 << 20)
            {
                size *= 2;
            }
        }
    }

    /// <summary>
    /// <c>build_table</c> - fills one table level and recurses for codes too long to fit in it.
    /// </summary>
    /// <param name="table">The whole destination; subtables are carved out of its tail.</param>
    /// <param name="used">How much of <paramref name="table"/> has been handed out so far.</param>
    /// <param name="tableNbBits">Width of the level being built.</param>
    /// <param name="nbCodes">Codes to place.</param>
    /// <param name="codes">The code array; entries are rewritten as prefixes are consumed.</param>
    /// <param name="codesStart">Where in <paramref name="codes"/> this level's codes start.</param>
    /// <returns>The index in <paramref name="table"/> where this level begins.</returns>
    private static int BuildTable(
        Span<VLCElem> table, ref int used, int tableNbBits, int nbCodes, VlcCode[] codes,
        int codesStart, bool littleEndian = false)
    {
        if (tableNbBits > 30)
            throw new InvalidOperationException("VLC table width over 30 bits.");

        int tableSize = 1 << tableNbBits;
        int tableIndex = used;
        used += tableSize;
        if (used > table.Length)
            throw new InvalidOperationException(
                $"VLC table needs {used} entries but was given {table.Length}. FFmpeg sizes these "
              + "statically, so a mismatch means a declared size did not come across.");

        for (int i = 0; i < nbCodes; i++)
        {
            int n = codes[codesStart + i].Bits;
            uint code = codes[codesStart + i].Code;
            short symbol = codes[codesStart + i].Symbol;

            if (n <= tableNbBits)
            {
                // Fits at this level: fill every slot whose prefix matches this code.
                //
                // The two orders are genuinely different table layouts, not two spellings of
                // one. Big endian puts the code in the HIGH bits of the index and the slots
                // that share it are contiguous, so the step is 1. Little endian reverses the
                // whole 32-bit left-aligned code, which leaves the reversed code in the LOW n
                // bits and zero above, and the slots that share it are then strided by
                // 1 << n - because the bits a reader has not consumed yet sit ABOVE the code
                // rather than below it.
                int slot = littleEndian
                    ? (int)BitSwap32(code)
                    : (int)(code >> (32 - tableNbBits));
                int count = 1 << (tableNbBits - n);
                int step = littleEndian ? 1 << n : 1;

                for (int k = 0; k < count; k++)
                {
                    int haveLen = table[tableIndex + slot].Len;
                    int haveSym = table[tableIndex + slot].Sym;
                    if ((haveLen != 0 || haveSym != 0) && (haveLen != n || haveSym != symbol))
                        throw new InvalidOperationException(
                            $"Incorrect VLC codes: slot {slot} already holds len {haveLen} sym {haveSym} "
                          + $"and cannot also hold len {n} sym {symbol}.");

                    table[tableIndex + slot].Len = (short)n;
                    table[tableIndex + slot].Sym = symbol;
                    slot += step;
                }
            }
            else
            {
                // Too long: consume this level's worth of prefix and recurse with every code that
                // shares it. The sort above made those entries contiguous.
                n -= tableNbBits;
                uint codePrefix = code >> (32 - tableNbBits);
                int subtableBits = n;

                codes[codesStart + i].Bits = (byte)n;
                codes[codesStart + i].Code = code << tableNbBits;

                int k = i + 1;
                for (; k < nbCodes; k++)
                {
                    n = codes[codesStart + k].Bits - tableNbBits;
                    if (n <= 0) break;
                    code = codes[codesStart + k].Code;
                    if (code >> (32 - tableNbBits) != codePrefix) break;
                    codes[codesStart + k].Bits = (byte)n;
                    codes[codesStart + k].Code = code << tableNbBits;
                    subtableBits = Math.Max(subtableBits, n);
                }
                subtableBits = Math.Min(subtableBits, tableNbBits);

                int slot = littleEndian
                    ? (int)(BitSwap32(codePrefix) >> (32 - tableNbBits))
                    : (int)codePrefix;

                // A negative length is the signal to get_vlc2 that Sym is a subtable base.
                table[tableIndex + slot].Len = (short)(-subtableBits);

                int index = BuildTable(table, ref used, subtableBits, k - i, codes,
                                       codesStart + i, littleEndian);

                table[tableIndex + slot].Sym = (short)index;
                if (table[tableIndex + slot].Sym != index)
                    throw new InvalidOperationException(
                        $"VLC subtable index {index} does not fit in a short.");

                i = k - 1;
            }
        }

        for (int i = 0; i < tableSize; i++)
            if (table[tableIndex + i].Len == 0)
                table[tableIndex + i].Sym = -1;

        return tableIndex;
    }

    /// <summary><c>bitswap_32</c> (libavcodec/mathops.h) - reverse all 32 bits.</summary>
    /// <remarks>
    /// FFmpeg does this with four lookups in <c>ff_reverse</c>, a 256-byte table. The shift and
    /// mask form below is the same value with no table, which matters because this runs only at
    /// table build time and a 256-byte table would be live for the process to save nothing.
    /// </remarks>
    private static uint BitSwap32(uint v)
    {
        v = (v >> 16) | (v << 16);
        v = ((v & 0xFF00FF00u) >> 8) | ((v & 0x00FF00FFu) << 8);
        v = ((v & 0xF0F0F0F0u) >> 4) | ((v & 0x0F0F0F0Fu) << 4);
        v = ((v & 0xCCCCCCCCu) >> 2) | ((v & 0x33333333u) << 2);
        v = ((v & 0xAAAAAAAAu) >> 1) | ((v & 0x55555555u) << 1);
        return v;
    }
}

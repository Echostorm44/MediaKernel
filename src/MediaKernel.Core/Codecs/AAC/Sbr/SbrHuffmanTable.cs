using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// A Huffman code given as a list of (symbol, code length) pairs, built the way FFmpeg's
/// <c>ff_vlc_init_from_lengths</c> builds one.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg stores the SBR and PS tables as lengths rather than codes, because the code follows
/// from the lengths alone. The rule is not "sort by length and number consecutively": it is a
/// running fractional accumulation in the order the list is written. Each symbol takes the top
/// <c>len</c> bits of a 32-bit accumulator, and the accumulator then advances by
/// <c>1 &lt;&lt; (32 - len)</c>. For a list whose lengths happen to ascend the two rules agree,
/// which is why a grouped implementation worked for every SBR table.
/// </para>
/// <para>
/// It does not work for PS. <c>huff_ipd_df</c> is written <c>3,4,4,4,4,4,4,1</c> and
/// <c>huff_iid_df1</c> starts <c>4,4,3,3,5,5</c> - the shortest code is written last, or in the
/// middle. A grouped implementation hands those tables the wrong symbols, and the visible effect
/// is not noise: the PS payload parse consumes the wrong number of bits, the whole payload is
/// rejected as an extension overflow, and the stream silently decodes as dual mono.
/// </para>
/// <para>
/// Decoding walks one bit at a time and, at each length that has codes, looks for the accumulated
/// code among them. Codes of one length are not necessarily contiguous under this rule, so the
/// lookup is a search rather than an index - over tables of at most 61 entries read a few hundred
/// times per frame.
/// </para>
/// </remarks>
internal sealed class SbrHuffmanTable
{
    private const int MaxLength = 20;

    private readonly int offset;
    private readonly byte[] lengths;

    /// <summary>Codes of each length, ascending, and the symbol each one decodes to.</summary>
    private readonly uint[][] codesByLength = new uint[MaxLength + 1][];
    private readonly int[][] symbolsByLength = new int[MaxLength + 1][];

    public SbrHuffmanTable(int offset, int[] symbols, byte[] lengths)
    {
        if (symbols.Length != lengths.Length)
            throw new ArgumentException("symbol and length tables must be the same size");

        this.offset = offset;
        this.lengths = lengths;

        var countPerLength = new int[MaxLength + 1];
        foreach (byte length in lengths)
        {
            if (length is 0 or > MaxLength)
                throw new ArgumentException($"unsupported code length {length}");
            countPerLength[length]++;
        }

        for (int length = 1; length <= MaxLength; length++)
        {
            if (countPerLength[length] == 0) continue;
            codesByLength[length] = new uint[countPerLength[length]];
            symbolsByLength[length] = new int[countPerLength[length]];
        }

        // ff_vlc_init_from_lengths, in the order the list is written.
        var filled = new int[MaxLength + 1];
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            uint aligned = (uint)(code >> (32 - length));

            if ((code & ((1u << (32 - length)) - 1)) != 0)
                throw new ArgumentException(
                    $"invalid code table: the code for symbol {symbols[i]} is not aligned to its "
                  + $"length of {length} bits. The lengths do not describe a prefix code.");

            int slot = filled[length]++;
            codesByLength[length]![slot] = aligned;
            symbolsByLength[length]![slot] = symbols[i];

            code += 1ul << (32 - length);
            if (code > 0x1_0000_0000ul)
                throw new ArgumentException("overdetermined code table: the lengths sum past 1.");
        }

        // Codes of equal length are not guaranteed to be written in ascending order, and the
        // lookup below assumes they are.
        for (int length = 1; length <= MaxLength; length++)
        {
            if (codesByLength[length] is null) continue;
            Array.Sort(codesByLength[length]!, symbolsByLength[length]!);
        }
    }

    /// <summary>Code lengths, exposed so the table's completeness can be checked.</summary>
    public ReadOnlySpan<byte> CodeLengths => lengths;

    /// <summary>
    /// Reads one code and returns its symbol, already biased by the table's offset.
    /// </summary>
    public int Decode(ref SbrBitReader reader)
    {
        uint code = 0;
        for (int length = 1; length <= MaxLength; length++)
        {
            code = (code << 1) | reader.ReadBit();

            uint[]? codes = codesByLength[length];
            if (codes is null) continue;

            int low = 0, high = codes.Length - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                if (codes[mid] == code) return symbolsByLength[length]![mid] + offset;
                if (codes[mid] < code) low = mid + 1;
                else high = mid - 1;
            }
        }

        // Only reachable on corrupt data. Returning the bias leaves the delta at zero, which keeps
        // the envelope flat rather than injecting a wild scalefactor.
        reader.MarkInvalid();
        return offset;
    }
}

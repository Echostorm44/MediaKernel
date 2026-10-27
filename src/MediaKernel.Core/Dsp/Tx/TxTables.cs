// libavutil/tx_template.c's power-of-two cosine tables.
//
// FFmpeg computes these at first use from the SR_TABLE macro: cos(i * 2pi/len) in double for
// i < len/4, then one zero entry. The zero is not padding - ff_tx_fft_sr_combine indexes the
// table from both ends and reads it.
//
// They are computed here rather than stored for the same reason FFmpeg computes them: the values
// are what cos() in double, rounded once to float, produces. A table typed out to eight decimal
// places would differ in the last bit, and the last bit is the whole point of this port.

using System;

namespace MediaKernel.Core.Dsp.Tx;

/// <summary>The split-radix twiddle tables, one per power-of-two length from 8 to 131072.</summary>
internal static class TxTables
{
    private const int SmallestTable = 8;
    private const int LargestTable = 131072;

    private static readonly float[]?[] tables = new float[18][];
    private static readonly object gate = new();

    /// <summary>
    /// <c>ff_tx_tab_&lt;len&gt;</c>: <c>len/4</c> cosines and a zero.
    /// </summary>
    public static float[] SrTable(int length)
    {
        if (length < SmallestTable || length > LargestTable || (length & (length - 1)) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"tx: no split-radix table for length {length}. The tables FFmpeg generates run "
              + "from 8 to 131072 in powers of two, and a request for anything else means the "
              + "codelet chain was resolved wrongly.");

        int slot = System.Numerics.BitOperations.TrailingZeroCount(length);
        float[]? table = System.Threading.Volatile.Read(ref tables[slot]);
        if (table != null) return table;

        lock (gate)
        {
            table = tables[slot];
            if (table != null) return table;

            table = new float[length / 4 + 1];
            double freq = 2 * Math.PI / length;
            for (int i = 0; i < length / 4; i++)
                table[i] = (float)Math.Cos(i * freq);
            table[length / 4] = 0;

            System.Threading.Volatile.Write(ref tables[slot], table);
            return table;
        }
    }
}

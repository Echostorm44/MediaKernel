// aacps_common.c - reading the Parametric Stereo payload out of an SBR extension.
//
// The payload is a few hundred bits: which tools are on, how the frame is split into envelopes,
// and then delta-coded IID, ICC and optionally IPD/OPD parameters per envelope. Everything is
// Huffman coded either across frequency or against the previous envelope.
//
// Two details that are easy to lose in translation and are kept here deliberately:
//
//   * the C reads from a COPY of the caller's bit context and only advances the caller by the
//     number of bits it actually consumed - and on any error it advances the caller by the whole
//     declared length instead. Doing it any other way desynchronises the SBR payload.
//   * the "fake envelope" fix-up writes par[num_env] where num_env can equal PS_MAX_NUM_ENV, so
//     the parameter arrays carry one more row than the constant suggests.

using System;
using MediaKernel.Core.Codecs.AAC.Sbr;

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary><c>ff_ps_read_data</c> and the parameter readers it is built from.</summary>
internal static class PsParser
{
    // vlc_ps order, from the anonymous enum in aacps_common.c.
    private const int HuffIidDf1 = 0;
    private const int HuffIidDt1 = 1;
    private const int HuffIidDf0 = 2;
    private const int HuffIidDt0 = 3;
    private const int HuffIccDf = 4;
    private const int HuffIccDt = 5;
    private const int HuffIpdDf = 6;
    private const int HuffIpdDt = 7;
    private const int HuffOpdDf = 8;
    private const int HuffOpdDt = 9;

    private static ReadOnlySpan<sbyte> NumEnvTab =>
    [
        0, 1, 2, 4,
        1, 2, 3, 4,
    ];

    private static ReadOnlySpan<sbyte> NrIidIccParTab => [10, 20, 34, 10, 20, 34];
    private static ReadOnlySpan<sbyte> NrIidOpdParTab => [5, 11, 17, 5, 11, 17];

    /// <summary><c>huff_iid</c>: indexed <c>2*dt + iid_quant</c>.</summary>
    private static ReadOnlySpan<int> HuffIid => [HuffIidDf0, HuffIidDf1, HuffIidDt0, HuffIidDt1];

    /// <summary><c>ff_log2_tab</c> for the four values num_env can take.</summary>
    private static int Log2Tab(int v) => v switch
    {
        1 => 0,
        2 => 1,
        3 => 1,
        4 => 2,
        _ => throw new InvalidOperationException(
            $"PS: num_env {v} is impossible; num_env_tab only yields 0, 1, 2, 3 or 4."),
    };

    /// <summary>
    /// <c>READ_PAR_DATA</c>, instantiated once rather than three times: the three parameters
    /// differ only in their mask and their validity test, which are passed in.
    /// </summary>
    /// <param name="mask">0 for IID and ICC, 7 for IPD and OPD.</param>
    /// <param name="check">
    /// Which validity test the C's ERR_CONDITION is for this parameter. A delegate would read
    /// closer to the macro but would allocate a closure on every envelope.
    /// </param>
    /// <returns>False when a decoded parameter is out of range, which aborts the payload.</returns>
    private static bool ReadParData(ref SbrBitReader reader, PsCommonContext ps,
                                    sbyte[][] par, int tableIndex, int e, bool dt,
                                    int num, int mask, ParCheck check, int limit = 0)
    {
        SbrHuffmanTable table = PsData.Tables[tableIndex];

        if (dt)
        {
            int ePrev = e != 0 ? e - 1 : ps.NumEnvOld - 1;
            if (ePrev < 0) ePrev = 0;
            for (int b = 0; b < num; b++)
            {
                int val = par[ePrev][b] + table.Decode(ref reader);
                if (mask != 0) val &= mask;
                par[e][b] = (sbyte)val;
                if (Illegal(check, val, limit)) return false;
            }
        }
        else
        {
            int val = 0;
            for (int b = 0; b < num; b++)
            {
                val += table.Decode(ref reader);
                if (mask != 0) val &= mask;
                par[e][b] = (sbyte)val;
                if (Illegal(check, val, limit)) return false;
            }
        }

        return true;
    }

    /// <summary>The three forms of the macro's ERR_CONDITION.</summary>
    private enum ParCheck { None, IidAbs, IccRange }

    private static bool Illegal(ParCheck check, int value, int limit) => check switch
    {
        ParCheck.None => false,
        ParCheck.IidAbs => Math.Abs(value) > limit,
        ParCheck.IccRange => (uint)value > 7u,
        _ => throw new InvalidOperationException($"PS: unknown parameter check {check}."),
    };

    /// <summary><c>ps_read_extension_data</c>. Returns the bits it consumed.</summary>
    private static int ReadExtensionData(ref SbrBitReader reader, PsCommonContext ps,
                                         int psExtensionId)
    {
        int count = reader.Position;

        if (psExtensionId != 0) return 0;

        ps.EnableIpdOpd = reader.ReadFlag();
        if (ps.EnableIpdOpd)
        {
            for (int e = 0; e < ps.NumEnv; e++)
            {
                bool dt = reader.ReadFlag();
                ReadParData(ref reader, ps, ps.IpdPar, dt ? HuffIpdDt : HuffIpdDf, e, dt,
                            ps.NrIpdOpdPar, 0x07, ParCheck.None);
                dt = reader.ReadFlag();
                ReadParData(ref reader, ps, ps.OpdPar, dt ? HuffOpdDt : HuffOpdDf, e, dt,
                            ps.NrIpdOpdPar, 0x07, ParCheck.None);
            }
        }

        reader.Skip(1);   // reserved_ps
        return reader.Position - count;
    }

    /// <summary>
    /// <c>ff_ps_read_data</c>: reads one PS payload, advancing <paramref name="host"/> by the bits
    /// consumed, or by <paramref name="bitsLeft"/> if the payload was rejected.
    /// </summary>
    /// <returns>Bits consumed, which the caller subtracts from the extension's remaining length.</returns>
    public static int ReadData(ref SbrBitReader host, PsCommonContext ps, int bitsLeft)
    {
        int bitCountStart = host.Position;
        SbrBitReader reader = host;      // the C reads from a copy; so does this

        bool header = reader.ReadFlag();
        if (header)
        {
            ps.EnableIid = reader.ReadFlag();
            if (ps.EnableIid)
            {
                int iidMode = (int)reader.ReadBits(3);
                if (iidMode > 5) return Fail(ref host, ps, bitsLeft, "iid_mode reserved", iidMode);
                ps.NrIidPar = NrIidIccParTab[iidMode];
                ps.IidQuant = iidMode > 2 ? 1 : 0;
                ps.NrIpdOpdPar = NrIidOpdParTab[iidMode];
            }

            ps.EnableIcc = reader.ReadFlag();
            if (ps.EnableIcc)
            {
                ps.IccMode = (int)reader.ReadBits(3);
                if (ps.IccMode > 5)
                    return Fail(ref host, ps, bitsLeft, "icc_mode reserved", ps.IccMode);
                ps.NrIccPar = NrIidIccParTab[ps.IccMode];
            }

            ps.EnableExt = reader.ReadFlag();
        }

        ps.FrameClass = (int)reader.ReadBit();
        ps.NumEnvOld = ps.NumEnv;
        ps.NumEnv = NumEnvTab[ps.FrameClass * 4 + (int)reader.ReadBits(2)];

        ps.BorderPosition[0] = -1;
        if (ps.FrameClass != 0)
        {
            for (int e = 1; e <= ps.NumEnv; e++)
            {
                ps.BorderPosition[e] = (int)reader.ReadBits(5);
                if (ps.BorderPosition[e] < ps.BorderPosition[e - 1])
                    return Fail(ref host, ps, bitsLeft, "border_position non monotone");
            }
        }
        else
        {
            for (int e = 1; e <= ps.NumEnv; e++)
                ps.BorderPosition[e] = (e * PsLimits.NumQmfSlots >> Log2Tab(ps.NumEnv)) - 1;
        }

        if (ps.EnableIid)
        {
            int limit = 7 + 8 * ps.IidQuant;
            for (int e = 0; e < ps.NumEnv; e++)
            {
                bool dt = reader.ReadFlag();
                if (!ReadParData(ref reader, ps, ps.IidPar, HuffIid[2 * (dt ? 1 : 0) + ps.IidQuant],
                                 e, dt, ps.NrIidPar, 0, ParCheck.IidAbs, limit))
                    return Fail(ref host, ps, bitsLeft, "illegal iid in envelope", e);
            }
        }
        else
        {
            Clear(ps.IidPar);
        }

        if (ps.EnableIcc)
        {
            for (int e = 0; e < ps.NumEnv; e++)
            {
                bool dt = reader.ReadFlag();
                if (!ReadParData(ref reader, ps, ps.IccPar, dt ? HuffIccDt : HuffIccDf,
                                 e, dt, ps.NrIccPar, 0, ParCheck.IccRange))
                    return Fail(ref host, ps, bitsLeft, "illegal icc in envelope", e);
            }
        }
        else
        {
            Clear(ps.IccPar);
        }

        if (ps.EnableExt)
        {
            int cnt = (int)reader.ReadBits(4);
            if (cnt == 15) cnt += (int)reader.ReadBits(8);
            cnt *= 8;
            while (cnt > 7)
            {
                int psExtensionId = (int)reader.ReadBits(2);
                cnt -= 2 + ReadExtensionData(ref reader, ps, psExtensionId);
            }
            if (cnt < 0) return Fail(ref host, ps, bitsLeft, "ps extension overflow", cnt);
            reader.Skip(cnt);
        }

        if (PsLimits.Baseline) ps.EnableIpdOpd = false;

        // Fix up envelopes: a frame whose last border falls short of the frame end gets a copy of
        // the previous envelope's parameters extended to the end.
        if (ps.NumEnv == 0 || ps.BorderPosition[ps.NumEnv] < PsLimits.NumQmfSlots - 1)
        {
            int source = ps.NumEnv != 0 ? ps.NumEnv - 1 : ps.NumEnvOld - 1;
            if (source >= 0 && source != ps.NumEnv)
            {
                if (ps.EnableIid) ps.IidPar[source].CopyTo(ps.IidPar[ps.NumEnv], 0);
                if (ps.EnableIcc) ps.IccPar[source].CopyTo(ps.IccPar[ps.NumEnv], 0);
                if (ps.EnableIpdOpd)
                {
                    ps.IpdPar[source].CopyTo(ps.IpdPar[ps.NumEnv], 0);
                    ps.OpdPar[source].CopyTo(ps.OpdPar[ps.NumEnv], 0);
                }
            }

            if (ps.EnableIid)
            {
                int limit = 7 + 8 * ps.IidQuant;
                for (int b = 0; b < ps.NrIidPar; b++)
                    if (Math.Abs(ps.IidPar[ps.NumEnv][b]) > limit)
                        return Fail(ref host, ps, bitsLeft, "iid_par invalid in the fake envelope");
            }

            if (ps.EnableIcc)
            {
                // The C tests nr_iid_par here, not nr_icc_par. Kept, because the two differ when
                // only one of the tools is enabled and copying the "obvious" bound would change
                // which payloads are rejected.
                for (int b = 0; b < ps.NrIidPar; b++)
                    if ((uint)ps.IccPar[ps.NumEnv][b] > 7u)
                        return Fail(ref host, ps, bitsLeft, "icc_par invalid in the fake envelope");
            }

            ps.NumEnv++;
            ps.BorderPosition[ps.NumEnv] = PsLimits.NumQmfSlots - 1;
        }

        ps.Is34BandsOld = ps.Is34Bands;
        if (!PsLimits.Baseline && (ps.EnableIid || ps.EnableIcc))
            ps.Is34Bands = (ps.EnableIid && ps.NrIidPar == 34) ||
                           (ps.EnableIcc && ps.NrIccPar == 34);

        if (!ps.EnableIpdOpd)
        {
            Clear(ps.IpdPar);
            Clear(ps.OpdPar);
        }

        if (header) ps.Start = true;

        int bitsConsumed = reader.Position - bitCountStart;
        if (bitsConsumed <= bitsLeft && !reader.IsInvalid)
        {
            host.Skip(bitsConsumed);
            ps.FailReason = null;
            ps.FailValue = PsCommonContext.NoFailValue;
            return bitsConsumed;
        }

        return Fail(ref host, ps, bitsLeft,
                    reader.IsInvalid ? "overran the payload, bits read"
                                     : "consumed the wrong number of bits",
                    bitsConsumed);
    }

    /// <summary>
    /// The C's <c>err:</c> label: turn PS off, drop every parameter, and consume the whole
    /// declared payload so the SBR parse stays aligned.
    /// </summary>
    /// <param name="reason">
    /// A constant string. Never an interpolated one: this runs per frame on a stream that trips
    /// it, and two of al_sbr_ps_06's frames do, which showed up as 72 bytes a frame in the
    /// no-allocation gate. The number goes in <paramref name="value"/> instead.
    /// </param>
    /// <param name="value">The offending value, or <c>PsCommonContext.NoFailValue</c>.</param>
    private static int Fail(ref SbrBitReader host, PsCommonContext ps, int bitsLeft,
                            string reason, int value = PsCommonContext.NoFailValue)
    {
        ps.Start = false;
        ps.FailReason = reason;
        ps.FailValue = value;
        host.Skip(bitsLeft);
        Clear(ps.IidPar);
        Clear(ps.IccPar);
        Clear(ps.IpdPar);
        Clear(ps.OpdPar);
        return bitsLeft;
    }

    private static void Clear(sbyte[][] rows)
    {
        foreach (sbyte[] row in rows) Array.Clear(row);
    }
}

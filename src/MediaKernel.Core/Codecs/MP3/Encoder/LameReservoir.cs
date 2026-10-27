// Translated from lame-3.100/libmp3lame/reservoir.c - the bit reservoir.
//
// The reservoir is what lets one granule borrow bits from the space earlier frames did not use.
// main_data_begin is the backward pointer that says how far back this frame's data starts, and
// it has 9 bits in MPEG-1 and 8 in MPEG-2, which is where resvLimit comes from.
//
// The gfc->pinfo block of ResvFrameBegin writes into the frame analyser's plotting_data and is
// omitted, as everywhere else; libmp3lame.c never enables it.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>reservoir.c</c>.</summary>
internal static class LameReservoir
{
    /// <summary>
    /// <c>ResvFrameBegin</c> - how many bits are available for this frame.
    /// </summary>
    /// <param name="meanBits">Out: the target number of bits per granule.</param>
    /// <returns><c>fullFrameBits</c>, the maximum available for encoding this frame.</returns>
    public static int ResvFrameBegin(LameInternalFlags gfc, out int meanBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int fullFrameBits;
        int resvLimit;
        int maxmp3buf;
        int frameLength;
        int meanBitsLocal;

        frameLength = LameBitstream.GetFrameBits(gfc);
        meanBitsLocal = (frameLength - cfg.SideinfoLen * 8) / cfg.ModeGr;

        // main_data_begin has 9 bits in MPEG-1 and 8 in MPEG-2
        resvLimit = 8 * 256 * cfg.ModeGr - 8;

        // The maximum allowed frame size: do not use more than this many bits even if the frame
        // has room for them.
        maxmp3buf = cfg.BufferConstraint;
        esv.ResvMax = maxmp3buf - frameLength;
        if (esv.ResvMax > resvLimit) esv.ResvMax = resvLimit;
        if (esv.ResvMax < 0 || cfg.DisableReservoir != 0) esv.ResvMax = 0;

        fullFrameBits = meanBitsLocal * cfg.ModeGr + Math.Min(esv.ResvSize, esv.ResvMax);

        if (fullFrameBits > maxmp3buf) fullFrameBits = maxmp3buf;

        l3Side.ResvDrainPre = 0;

        meanBits = meanBitsLocal;
        return fullFrameBits;
    }

    /// <summary>
    /// <c>ResvMaxBits</c> - the target bits for one granule and what the reservoir can add.
    /// </summary>
    public static void ResvMaxBits(
        LameInternalFlags gfc, int meanBits, out int targBits, out int extraBits, int cbr)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        int addBits, targBitsLocal, extraBitsLocal;
        int resvSize = esv.ResvSize, resvMax = esv.ResvMax;

        // compensate for the bits saved in the first granule
        if (cbr != 0) resvSize += meanBits;

        if ((gfc.SvQnt.SubstepShaping & 1) != 0) resvMax = (int)(resvMax * 0.9);

        targBitsLocal = meanBits;

        // extra bits if the reservoir is almost full
        if (resvSize * 10 > resvMax * 9)
        {
            addBits = resvSize - resvMax * 9 / 10;
            targBitsLocal += addBits;
            gfc.SvQnt.SubstepShaping |= 0x80;
        }
        else
        {
            addBits = 0;
            gfc.SvQnt.SubstepShaping &= 0x7f;
            // Build up the reservoir, a little more slowly than FhG. It could be mean_bits/15,
            // but this was rigged to always produce 100 - the old value - at 128 kbps.
            // The C is `targBits -= .1 * mean_bits;` on an int: the whole right-hand side is
            // formed in double and the *result* is truncated on assignment, not the
            // subtrahend. Taking (int)(.1 * mean_bits) first is off by one whenever
            // 0.1 * mean_bits has a fractional part, which at 128 kbps it always does.
            if (cfg.DisableReservoir == 0 && (gfc.SvQnt.SubstepShaping & 1) == 0)
                targBitsLocal = (int)(targBitsLocal - .1 * meanBits);
        }

        // what we are allowed to take from the reservoir; the IS says 6/10
        extraBitsLocal = resvSize < esv.ResvMax * 6 / 10 ? resvSize : esv.ResvMax * 6 / 10;
        extraBitsLocal -= addBits;

        if (extraBitsLocal < 0) extraBitsLocal = 0;

        targBits = targBitsLocal;
        extraBits = extraBitsLocal;
    }

    /// <summary>
    /// <c>ResvAdjust</c> - shrink the reservoir by what this granule used.
    /// </summary>
    public static void ResvAdjust(LameInternalFlags gfc, in GrInfo gi)
    {
        gfc.SvEnc.ResvSize -= gi.Part23Length + gi.Part2Length;
    }

    /// <summary>
    /// <c>ResvFrameEnd</c> - bring the reservoir back inside its limits, with stuffing if need be.
    /// </summary>
    /// <remarks>
    /// The drain into the previous frame's ancillary data is the C's NEW_DRAIN path, enabled
    /// since 2010: without it, a 320 kbps file could fail to play on the FhG decoder shipped
    /// with Windows.
    /// </remarks>
    public static void ResvFrameEnd(LameInternalFlags gfc, int meanBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int stuffingBits;
        int overBits;

        esv.ResvSize += meanBits * cfg.ModeGr;
        stuffingBits = 0;
        l3Side.ResvDrainPost = 0;
        l3Side.ResvDrainPre = 0;

        // we must be byte aligned
        if ((overBits = esv.ResvSize % 8) != 0) stuffingBits += overBits;

        overBits = esv.ResvSize - stuffingBits - esv.ResvMax;
        if (overBits > 0) stuffingBits += overBits;

        // Drain as many bits as possible into the previous frame's ancillary data. In VBR mode
        // ResvMax may have changed, so main_data_begin must not describe a reservoir bigger
        // than ResvMax.
        {
            int mdbBytes = Math.Min(l3Side.MainDataBegin * 8, stuffingBits) / 8;
            l3Side.ResvDrainPre += 8 * mdbBytes;
            stuffingBits -= 8 * mdbBytes;
            esv.ResvSize -= 8 * mdbBytes;
            l3Side.MainDataBegin -= mdbBytes;
        }
        // drain the rest into this frame's ancillary data
        l3Side.ResvDrainPost += stuffingBits;
        esv.ResvSize -= stuffingBits;
    }
}

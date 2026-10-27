// Block clearing and filling - a translation of ffmpeg-master/libavcodec/blockdsp.c.
//
// Per the 2026-10-16 decision the BlockDSPContext function-pointer table is gone and these are
// static calls.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Block clearing and filling (<c>blockdsp.c</c>).</summary>
public static class BlockDsp
{
    /// <summary><c>clear_block_c</c> - one 8x8 coefficient block.</summary>
    public static void ClearBlock(Span<short> block) => block[..64].Clear();

    /// <summary>
    /// <c>clear_blocks_c</c> - the six blocks of a 4:2:0 macroblock in one go.
    /// </summary>
    /// <remarks>
    /// Six and not twelve: the C clears <c>6 * 64</c> entries, which is a 4:2:0 macroblock's four
    /// luma and two chroma blocks. A 4:2:2 or 4:4:4 macroblock has more, and the decoder clears
    /// those separately.
    /// </remarks>
    public static void ClearBlocks(Span<short> blocks) => blocks[..(6 * 64)].Clear();

    /// <summary><c>fill_block16_c</c>.</summary>
    public static void FillBlock16(Span<byte> block, int offset, byte value, int lineSize, int h)
    {
        for (int i = 0; i < h; i++)
            block.Slice(offset + i * lineSize, 16).Fill(value);
    }

    /// <summary><c>fill_block8_c</c>.</summary>
    public static void FillBlock8(Span<byte> block, int offset, byte value, int lineSize, int h)
    {
        for (int i = 0; i < h; i++)
            block.Slice(offset + i * lineSize, 8).Fill(value);
    }
}

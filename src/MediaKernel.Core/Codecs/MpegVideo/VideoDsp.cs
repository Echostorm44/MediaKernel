// Edge emulation - a translation of ff_emulated_edge_mc from
// ffmpeg-master/libavcodec/videodsp_template.c at 8 bits.
//
// Per the 2026-10-16 decision the VideoDSPContext function-pointer table is gone and this is a
// static call. prefetch is not ported: it is a hint with no managed equivalent, and FFmpeg's own
// C fallback for it is an empty function.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Edge emulation for motion vectors that point outside the picture (<c>videodsp.c</c>).</summary>
public static class VideoDsp
{
    /// <summary>
    /// <c>ff_emulated_edge_mc</c> - copies a block out of the picture into a scratch buffer,
    /// replicating the edge pixels wherever the block falls outside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A motion vector near the frame border can address samples that do not exist. Rather than
    /// bounds-check the inner interpolation loops, the decoder builds a block here in which the
    /// missing rows and columns have been filled with the nearest real pixel, and then
    /// interpolates from that unconditionally. The filled block is what the standard requires:
    /// edge replication is the defined behaviour, not a fallback.
    /// </para>
    /// <para>
    /// The first two clamps move the source so that even a block entirely outside the picture
    /// still reads one real row or column to replicate from. Without them the copy below would
    /// have nothing to copy.
    /// </para>
    /// </remarks>
    /// <param name="buf">Destination scratch buffer.</param>
    /// <param name="bufOffset">Where in <paramref name="buf"/> the block starts.</param>
    /// <param name="src">The picture.</param>
    /// <param name="srcOffset">Where in <paramref name="src"/> the wanted block starts, which may
    /// be outside the picture and is adjusted here.</param>
    /// <param name="bufLinesize">Destination stride.</param>
    /// <param name="srcLinesize">Picture stride.</param>
    /// <param name="blockW">Block width.</param>
    /// <param name="blockH">Block height.</param>
    /// <param name="srcX">Block's x position in the picture, possibly negative.</param>
    /// <param name="srcY">Block's y position, possibly negative.</param>
    /// <param name="w">Picture width.</param>
    /// <param name="h">Picture height.</param>
    public static void EmulatedEdgeMc(
        Span<byte> buf, int bufOffset,
        ReadOnlySpan<byte> src, int srcOffset,
        int bufLinesize, int srcLinesize,
        int blockW, int blockH,
        int srcX, int srcY, int w, int h)
    {
        if (w == 0 || h == 0) return;

        if (srcY >= h)
        {
            srcOffset -= srcY * srcLinesize;
            srcOffset += (h - 1) * srcLinesize;
            srcY = h - 1;
        }
        else if (srcY <= -blockH)
        {
            srcOffset -= srcY * srcLinesize;
            srcOffset += (1 - blockH) * srcLinesize;
            srcY = 1 - blockH;
        }

        if (srcX >= w)
        {
            srcOffset -= 1 + srcX - w;
            srcX = w - 1;
        }
        else if (srcX <= -blockW)
        {
            srcOffset += 1 - blockW - srcX;
            srcX = 1 - blockW;
        }

        int startY = Math.Max(0, -srcY);
        int startX = Math.Max(0, -srcX);
        int endY = Math.Min(blockH, h - srcY);
        int endX = Math.Min(blockW, w - srcX);

        int width = endX - startX;
        srcOffset += startY * srcLinesize + startX;
        bufOffset += startX;

        int y = 0;

        // Top: repeat the first real row upwards.
        for (; y < startY; y++)
        {
            src.Slice(srcOffset, width).CopyTo(buf.Slice(bufOffset, width));
            bufOffset += bufLinesize;
        }

        // The part that exists.
        for (; y < endY; y++)
        {
            src.Slice(srcOffset, width).CopyTo(buf.Slice(bufOffset, width));
            srcOffset += srcLinesize;
            bufOffset += bufLinesize;
        }

        // Bottom: repeat the last real row downwards.
        srcOffset -= srcLinesize;
        for (; y < blockH; y++)
        {
            src.Slice(srcOffset, width).CopyTo(buf.Slice(bufOffset, width));
            bufOffset += bufLinesize;
        }

        // Left and right, now that every row holds its real pixels.
        bufOffset -= blockH * bufLinesize + startX;
        for (int row = 0; row < blockH; row++)
        {
            for (int x = 0; x < startX; x++)
                buf[bufOffset + x] = buf[bufOffset + startX];

            for (int x = endX; x < blockW; x++)
                buf[bufOffset + x] = buf[bufOffset + endX - 1];

            bufOffset += bufLinesize;
        }
    }
}

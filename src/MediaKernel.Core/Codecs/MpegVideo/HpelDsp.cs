// Half-pel motion compensation - a translation of the PIXOP2 family in
// ffmpeg-master/libavcodec/hpeldsp.c at 8 bits.
//
// MPEG-1/2 luma motion vectors are half-pel, so a block is fetched at one of four positions:
// integral, half a pixel right, half a pixel down, or both. That is the dxy index the motion
// compensation code passes, and these are the sixteen functions behind it - four positions, two
// widths, put and avg - plus the no-rounding variants.
//
// DIVERGENCE, recorded: FFmpeg's PIXOP2 works on four pixels at a time packed into a uint32 and
// averages them with SWAR arithmetic, masking to stop carries crossing byte lanes. The
// per-pixel form here computes the same values - that was checked term by term against the
// packed code and is covered by the tests - and is legible. Should this ever be worth
// vectorising, Vector128 is the way to do it in C#, not a hand-rolled 32-bit SWAR.
//
// Per the 2026-10-16 decision the HpelDSPContext function-pointer table is gone. The dxy index
// becomes a switch at the one call site that needs it.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Half-pel motion compensation (<c>hpeldsp.c</c>).</summary>
/// <remarks>
/// <para>
/// Rounding is the subtlety. The ordinary forms average with <c>(a + b + 1) &gt;&gt; 1</c>, and
/// the diagonal with <c>(a + b + c + d + 2) &gt;&gt; 2</c>. The no-rounding forms drop the bias
/// to <c>(a + b) &gt;&gt; 1</c> and <c>(a + b + c + d + 1) &gt;&gt; 2</c>, and they exist because
/// MPEG-4 and H.263 specify unbiased averaging for some pictures. MPEG-1/2 uses the rounded
/// forms, with <c>no_rounding</c> clear; the unrounded ones are translated because they are the
/// same family and the table has them.
/// </para>
/// <para>
/// Sixteen-wide is two eight-wide calls side by side, which is what FFmpeg's CALL_2X_PIXELS
/// macro does.
/// </para>
/// </remarks>
public static class HpelDsp
{
    /// <summary>Copies an <paramref name="width"/>-wide block, the integral position.</summary>
    private static void Pixels(
        Span<byte> block, int blockOffset, ReadOnlySpan<byte> pixels, int pixelsOffset,
        int lineSize, int h, int width, bool average)
    {
        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = pixels[pixelsOffset + x];
                block[blockOffset + x] = average
                    ? (byte)((block[blockOffset + x] + v + 1) >> 1)
                    : (byte)v;
            }
            blockOffset += lineSize;
            pixelsOffset += lineSize;
        }
    }

    /// <summary>Half a pixel horizontally: averages each sample with its right neighbour.</summary>
    private static void PixelsX2(
        Span<byte> block, int blockOffset, ReadOnlySpan<byte> pixels, int pixelsOffset,
        int lineSize, int h, int width, bool average, bool round)
    {
        int bias = round ? 1 : 0;
        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (pixels[pixelsOffset + x] + pixels[pixelsOffset + x + 1] + bias) >> 1;
                block[blockOffset + x] = average
                    ? (byte)((block[blockOffset + x] + v + 1) >> 1)
                    : (byte)v;
            }
            blockOffset += lineSize;
            pixelsOffset += lineSize;
        }
    }

    /// <summary>Half a pixel vertically: averages each sample with the one below it.</summary>
    private static void PixelsY2(
        Span<byte> block, int blockOffset, ReadOnlySpan<byte> pixels, int pixelsOffset,
        int lineSize, int h, int width, bool average, bool round)
    {
        int bias = round ? 1 : 0;
        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (pixels[pixelsOffset + x] + pixels[pixelsOffset + lineSize + x] + bias) >> 1;
                block[blockOffset + x] = average
                    ? (byte)((block[blockOffset + x] + v + 1) >> 1)
                    : (byte)v;
            }
            blockOffset += lineSize;
            pixelsOffset += lineSize;
        }
    }

    /// <summary>Half a pixel in both directions: the average of a two-by-two neighbourhood.</summary>
    private static void PixelsXy2(
        Span<byte> block, int blockOffset, ReadOnlySpan<byte> pixels, int pixelsOffset,
        int lineSize, int h, int width, bool average, bool round)
    {
        int bias = round ? 2 : 1;
        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (pixels[pixelsOffset + x]
                       + pixels[pixelsOffset + x + 1]
                       + pixels[pixelsOffset + lineSize + x]
                       + pixels[pixelsOffset + lineSize + x + 1]
                       + bias) >> 2;
                block[blockOffset + x] = average
                    ? (byte)((block[blockOffset + x] + v + 1) >> 1)
                    : (byte)v;
            }
            blockOffset += lineSize;
            pixelsOffset += lineSize;
        }
    }

    /// <summary>
    /// Dispatches on the half-pel position, which is how the motion compensation code indexes
    /// FFmpeg's <c>pixels_tab[size][dxy]</c>.
    /// </summary>
    /// <param name="dxy">0 integral, 1 half right, 2 half down, 3 both.</param>
    /// <param name="width">8 or 16.</param>
    /// <param name="average">False for the put family, true for avg.</param>
    /// <param name="round">False for the no_rnd family.</param>
    public static void PixelsTab(
        int dxy, int width, bool average, bool round,
        Span<byte> block, int blockOffset, ReadOnlySpan<byte> pixels, int pixelsOffset,
        int lineSize, int h)
    {
        if (width == 16)
        {
            // Two eight-wide halves, as CALL_2X_PIXELS does.
            PixelsTab(dxy, 8, average, round, block, blockOffset, pixels, pixelsOffset, lineSize, h);
            PixelsTab(dxy, 8, average, round, block, blockOffset + 8, pixels, pixelsOffset + 8, lineSize, h);
            return;
        }

        switch (dxy)
        {
            case 0: Pixels(block, blockOffset, pixels, pixelsOffset, lineSize, h, width, average); break;
            case 1: PixelsX2(block, blockOffset, pixels, pixelsOffset, lineSize, h, width, average, round); break;
            case 2: PixelsY2(block, blockOffset, pixels, pixelsOffset, lineSize, h, width, average, round); break;
            case 3: PixelsXy2(block, blockOffset, pixels, pixelsOffset, lineSize, h, width, average, round); break;
            default:
                // Unknown input fails loudly; a silent no-op here would show up as a frozen block.
                throw new ArgumentOutOfRangeException(
                    nameof(dxy), dxy, "Half-pel position must be 0, 1, 2 or 3.");
        }
    }
}

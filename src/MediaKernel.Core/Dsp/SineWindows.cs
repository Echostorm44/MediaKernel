// Sine windows - a translation of ffmpeg-master/libavcodec/sinewin.c and sinewin_tablegen.h.
//
// FFmpeg keeps one half-window per power-of-two length in ff_sine_windows[] and fills each on
// first use through ff_init_ff_sine_windows. The windows are symmetric, so half is all that is
// stored, and a codec that needs the other half reads this one backwards.

using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// <c>ff_sine_windows</c>: the half sine window for each power-of-two length.
/// </summary>
/// <remarks>
/// This project's AAC decoder has its own sine window tables, built for its two fixed transform
/// sizes before this existed. They are the same values - the same expression, in the same order,
/// so bit-identical - and they are deliberately left alone: AAC is conformant, and re-pointing a
/// conformant codec's window tables buys nothing.
/// </remarks>
internal static class SineWindows
{
    private static readonly Dictionary<int, float[]> cache = [];
    private static readonly object gate = new();

    /// <summary>
    /// <c>ff_sine_windows[log2(n)]</c>: <c>n</c> values of <c>sin((i + 0.5) * pi / 2n)</c>.
    /// </summary>
    /// <param name="n">Half the window length; a power of two.</param>
    public static float[] Get(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(n), $"sine window length {n} is not a power of two of at least 2.");

        lock (gate)
        {
            if (cache.TryGetValue(n, out float[]? cached))
                return cached;

            var window = new float[n];
            Init(window, n);
            cache[n] = window;
            return window;
        }
    }

    /// <summary><c>ff_sine_window_init</c>.</summary>
    private static void Init(Span<float> window, int n)
    {
        for (int i = 0; i < n; i++)
            window[i] = MathF.Sin((float)((i + 0.5) * (Math.PI / (2.0 * n))));
    }
}

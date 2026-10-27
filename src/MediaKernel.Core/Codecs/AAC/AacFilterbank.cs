using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// AAC IMDCT filterbank using half-IMDCT (M spectral → M time-domain samples),
/// matching FFmpeg's imdct_and_windowing() approach exactly.
/// 
/// Half-IMDCT formula (from FFmpeg's ff_tx_mdct_naive_inv in tx_template.c):
///   For i=0..M/2-1:  dst[i]     =  (1/M) Σ X[k]·cos((2k+1)·phase·(2M-2i-1))
///   For i=0..M/2-1:  dst[i+M/2] = -(1/M) Σ X[k]·cos((2k+1)·phase·(3M+2i+1))
/// where phase = π/(4M).
///
/// Windowing and overlap-add use vector_fmul_window (deferred windowing)
/// where the window is applied at overlap time, not during IMDCT.
/// </summary>
public sealed class AacFilterbank
{
    private readonly float[] longWindowSine;
    private readonly float[] longWindowKbd;
    private readonly float[] shortWindowSine;
    private readonly float[] shortWindowKbd;

    // The 960-sample framing a stream selects with the AudioSpecificConfig's frameLengthFlag.
    // Both transforms and both window pairs are built only when such a stream arrives, because
    // the 960-point MDCT's tables cost more to generate than a 1024-point decoder should pay.
    private bool frameLengthShort;
    private Dsp.Tx.TxMdct? mdct960;
    private Dsp.Tx.TxMdct? mdct120;
    private float[]? longWindowSine960;
    private float[]? longWindowKbd960;
    private float[]? shortWindowSine120;
    private float[]? shortWindowKbd120;

    // av_tx's inverse MDCT, bit-exact against FFmpeg's own (see TxTests). The scales are
    // FFmpeg's MDCT_INIT: 1/M for the length, divided by 32768 for the float build, which is
    // where the factor the dequantiser used to carry belongs.
    private readonly Dsp.Tx.TxMdct mdct1024 =
        Dsp.Tx.TxMdct.Create(1024, inverse: true, full: false, (float)(1.0 / 1024 / 32768.0));

    private readonly Dsp.Tx.TxMdct mdct128 =
        Dsp.Tx.TxMdct.Create(128, inverse: true, full: false, (float)(1.0 / 128 / 32768.0));

    /// <summary><c>mdct512</c>: AAC LD's only transform, and ELD's at 1024 frame length.</summary>
    private readonly Dsp.Tx.TxMdct mdct512 =
        Dsp.Tx.TxMdct.Create(512, inverse: true, full: false, (float)(1.0 / 512 / 32768.0));

    /// <summary><c>mdct480</c>: ELD's transform at 960 frame length.</summary>
    private readonly Dsp.Tx.TxMdct mdct480 =
        Dsp.Tx.TxMdct.Create(480, inverse: true, full: false, (float)(1.0 / 480 / 32768.0));
    
    /// <summary>
    /// The forward MDCT long term prediction runs over its windowed time signal: 2048 samples in,
    /// 1024 coefficients out.
    /// </summary>
    /// <remarks>
    /// The scale is FFmpeg's, written there as <c>-32786.0*2 + 36</c>, which is exactly -65536.
    /// It is not the reciprocal of the inverse transform's scale and it is not 1/32768 either:
    /// the forward transform has to undo the 1/32768 the inverse scales by *and* carry the
    /// MDCT's own factor of two, with the sign the reference implementation uses. Deriving a
    /// "cleaner" value here changes every predicted coefficient.
    /// </remarks>
    private readonly Dsp.Tx.TxMdct mdctLtp =
        Dsp.Tx.TxMdct.Create(1024, inverse: false, full: false, -32786.0f * 2 + 36);

    /// <summary>Debug: dump raw half-IMDCT output to file (set path to enable, null to disable).</summary>
    public string? DebugDumpImdctPath;
    

    /// <summary>
    /// Number of spectral coefficients for a long block (1024).
    /// </summary>
    public const int LongBlockLength = 1024;

    /// <summary>
    /// Number of spectral coefficients for a short block (128).
    /// </summary>
    public const int ShortBlockLength = 128;

    public AacFilterbank()
    {
        longWindowSine = AacWindowTables.SineWindowLong;
        longWindowKbd = AacWindowTables.KbdWindowLong;
        shortWindowSine = AacWindowTables.SineWindowShort;
        shortWindowKbd = AacWindowTables.KbdWindowShort;

    }

    /// <summary>
    /// Selects the framing this decoder's streams use, the way <c>init_dsp</c> and the
    /// <c>SET(imdct_and_windowing_960)</c> dispatch in <c>aacdec.c</c> do between them.
    /// </summary>
    /// <param name="useShortFrames">
    /// The AudioSpecificConfig's <c>frameLengthFlag</c>: 960 samples per frame rather than 1024.
    /// </param>
    public void Configure(bool useShortFrames)
    {
        frameLengthShort = useShortFrames;
        if (!useShortFrames || mdct960 != null) return;

        // MDCT_INIT(ac->mdct960, ..., 960, 1.0/960) with the float build's extra 1/32768.
        mdct960 = Dsp.Tx.TxMdct.Create(960, inverse: true, full: false,
                                       (float)(1.0 / 960 / 32768.0));
        mdct120 = Dsp.Tx.TxMdct.Create(120, inverse: true, full: false,
                                       (float)(1.0 / 120 / 32768.0));
        longWindowSine960 = AacWindowTables.SineWindow960;
        longWindowKbd960 = AacWindowTables.KbdWindow960;
        shortWindowSine120 = AacWindowTables.SineWindow120;
        shortWindowKbd120 = AacWindowTables.KbdWindow120;
    }

    /// <summary>
    /// <c>windowing_and_mdct_ltp</c>: window the predicted time signal with the window pair this
    /// frame's sequence calls for, then take its forward MDCT.
    /// </summary>
    /// <param name="output">1024 predicted spectral coefficients.</param>
    /// <param name="predicted">2048 predicted time samples, windowed in place.</param>
    public void WindowingAndMdctLtp(Span<float> output, Span<float> predicted,
                                    WindowSequence windowSequence, int windowShape,
                                    int prevWindowShape)
    {
        var lwindow = windowShape == 0 ? longWindowSine : longWindowKbd;
        var swindow = windowShape == 0 ? shortWindowSine : shortWindowKbd;
        var lwindowPrev = prevWindowShape == 0 ? longWindowSine : longWindowKbd;
        var swindowPrev = prevWindowShape == 0 ? shortWindowSine : shortWindowKbd;

        if (windowSequence != WindowSequence.LongStopSequence)
        {
            for (int i = 0; i < 1024; i++)
                predicted[i] *= lwindowPrev[i];
        }
        else
        {
            predicted.Slice(0, 448).Clear();
            for (int i = 0; i < 128; i++)
                predicted[448 + i] *= swindowPrev[i];
        }

        if (windowSequence != WindowSequence.LongStartSequence)
        {
            for (int i = 0; i < 1024; i++)
                predicted[1024 + i] *= lwindow[1023 - i];
        }
        else
        {
            for (int i = 0; i < 128; i++)
                predicted[1024 + 448 + i] *= swindow[127 - i];
            predicted.Slice(1024 + 576, 448).Clear();
        }

        mdctLtp.Transform(output, predicted);
    }

    /// <summary>
    /// <c>update_ltp</c>: build the half of the LTP history that this frame's IMDCT output has
    /// not been windowed into yet, then roll the history forward.
    /// </summary>
    /// <param name="ltpState">3072 samples of history, rolled in place.</param>
    /// <param name="savedLtp">1024-sample scratch; the C reuses the channel's coefficients.</param>
    /// <param name="saved">The overlap the IMDCT just stored.</param>
    /// <param name="output">This frame's 1024 output samples.</param>
    /// <param name="buf">The raw half-IMDCT output of this frame, FFmpeg's <c>buf_mdct</c>.</param>
    public void UpdateLtp(Span<float> ltpState, Span<float> savedLtp, ReadOnlySpan<float> saved,
                          ReadOnlySpan<float> output, ReadOnlySpan<float> buf,
                          WindowSequence windowSequence, int windowShape)
    {
        var lwindow = windowShape == 0 ? longWindowSine : longWindowKbd;
        var swindow = windowShape == 0 ? shortWindowSine : shortWindowKbd;

        if (windowSequence == WindowSequence.EightShortSequence)
        {
            saved.Slice(0, 512).CopyTo(savedLtp);
            savedLtp.Slice(576, 448).Clear();
            // vector_fmul_reverse(saved_ltp + 448, buf_mdct + 960, &swindow[64], 64):
            // the window runs backwards, the source forwards.
            for (int i = 0; i < 64; i++)
                savedLtp[448 + i] = buf[960 + i] * swindow[64 + 63 - i];
            for (int i = 0; i < 64; i++)
                savedLtp[i + 512] = buf[1023 - i] * swindow[63 - i];
        }
        else if (windowSequence == WindowSequence.LongStartSequence)
        {
            buf.Slice(512, 448).CopyTo(savedLtp);
            savedLtp.Slice(576, 448).Clear();
            // vector_fmul_reverse(saved_ltp + 448, buf_mdct + 960, &swindow[64], 64):
            // the window runs backwards, the source forwards.
            for (int i = 0; i < 64; i++)
                savedLtp[448 + i] = buf[960 + i] * swindow[64 + 63 - i];
            for (int i = 0; i < 64; i++)
                savedLtp[i + 512] = buf[1023 - i] * swindow[63 - i];
        }
        else
        {
            // LONG_STOP or ONLY_LONG.
            // vector_fmul_reverse(saved_ltp, buf_mdct + 512, &lwindow[512], 512).
            for (int i = 0; i < 512; i++)
                savedLtp[i] = buf[512 + i] * lwindow[512 + 511 - i];
            for (int i = 0; i < 512; i++)
                savedLtp[i + 512] = buf[1023 - i] * lwindow[511 - i];
        }

        ltpState.Slice(1024, 1024).CopyTo(ltpState);
        output.Slice(0, 1024).CopyTo(ltpState.Slice(1024));
        savedLtp.Slice(0, 1024).CopyTo(ltpState.Slice(2048));
    }

    /// <summary>
    /// Performs IMDCT, windowing, and overlap-add for one channel.
    /// Direct port of FFmpeg's imdct_and_windowing() from aacdec_dsp_template.c.
    /// 
    /// Uses half-IMDCT (M→M samples) with deferred windowing via VectorFmulWindow.
    /// The saved buffer stores unwindowed half-IMDCT output; windows are applied
    /// during the overlap step based on previous and current window sequences.
    /// </summary>
    /// <param name="spectral">Spectral coefficients (1024 for all sequences).</param>
    /// <param name="output">Output PCM samples (1024 samples).</param>
    /// <param name="saved">Saved overlap buffer (1024 samples, only 512 used), updated in place.</param>
    /// <param name="windowSequence">Current frame's window sequence.</param>
    /// <param name="prevWindowSequence">Previous frame's window sequence.</param>
    /// <param name="windowShape">Current frame's window shape (0=sine, 1=KBD).</param>
    /// <param name="prevWindowShape">Previous frame's window shape (0=sine, 1=KBD).</param>
    /// <param name="buf">Temporary buffer for half-IMDCT output (at least 1024 samples).</param>
    public void ImdctAndWindowing(
        ReadOnlySpan<float> spectral,
        Span<float> output,
        Span<float> saved,
        WindowSequence windowSequence,
        WindowSequence prevWindowSequence,
        int windowShape,
        int prevWindowShape,
        Span<float> buf)
    {
        if (frameLengthShort)
        {
            ImdctAndWindowing960(spectral, output, saved, windowSequence, prevWindowSequence,
                                 windowShape, prevWindowShape, buf);
            return;
        }

        // Select windows. Our window arrays are 2048/256 samples (full IMDCT size),
        // but VectorFmulWindow only accesses the first 1024/128 entries, which match
        // FFmpeg's sine_1024/sine_128 and aac_kbd_long_1024/aac_kbd_short_128.
        var lwindowPrev = prevWindowShape == 0 ? longWindowSine : longWindowKbd;
        var swindow = windowShape == 0 ? shortWindowSine : shortWindowKbd;
        var swindowPrev = prevWindowShape == 0 ? shortWindowSine : shortWindowKbd;

        // === Half-IMDCT ===
        if (windowSequence == WindowSequence.EightShortSequence)
        {
            // 8 × short half-IMDCT: 128 spectral → 128 time-domain per block
            for (int i = 0; i < 1024; i += ShortBlockLength)
                mdct128.Transform(buf.Slice(i, ShortBlockLength),
                                  spectral.Slice(i, ShortBlockLength));
        }
        else
        {
            // 1 × long half-IMDCT: 1024 spectral → 1024 time-domain
            mdct1024.Transform(buf, spectral);
            
            if (DebugDumpImdctPath != null)
            {
                var bytes = new byte[1024 * 4];
                for (int db = 0; db < 1024; db++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(db * 4), buf[db]);
                System.IO.File.WriteAllBytes(DebugDumpImdctPath, bytes);
                DebugDumpImdctPath = null; // one-shot
            }
        }

        // === Windowed overlap ===
        // FFmpeg comment: "all 'meaningless' short to long and long to short transitions
        // are considered to be short to short transitions"
        bool prevIsLongType = prevWindowSequence == WindowSequence.OnlyLongSequence ||
                              prevWindowSequence == WindowSequence.LongStopSequence;
        bool currIsLongType = windowSequence == WindowSequence.OnlyLongSequence ||
                              windowSequence == WindowSequence.LongStartSequence;

        // temp is needed for EightShort block 3-4 overlap that straddles output/saved boundary
        Span<float> temp = stackalloc float[128];

        if (prevIsLongType && currIsLongType)
        {
            // Long→Long or Long→Start: standard long windowed overlap
            VectorFmulWindow(output, saved, buf, lwindowPrev, 512);
        }
        else
        {
            // Short transition path: flat copy + short window overlaps
            saved.Slice(0, 448).CopyTo(output);

            if (windowSequence == WindowSequence.EightShortSequence)
            {
                // Saved→Block0 overlap
                VectorFmulWindow(output.Slice(448), saved.Slice(448), buf, swindowPrev, 64);
                // Block0→Block1 overlap
                VectorFmulWindow(output.Slice(448 + 128), buf.Slice(64), buf.Slice(128), swindow, 64);
                // Block1→Block2 overlap
                VectorFmulWindow(output.Slice(448 + 256), buf.Slice(192), buf.Slice(256), swindow, 64);
                // Block2→Block3 overlap
                VectorFmulWindow(output.Slice(448 + 384), buf.Slice(320), buf.Slice(384), swindow, 64);
                // Block3→Block4 overlap (straddles output[960..1023] and saved[0..63])
                VectorFmulWindow(temp, buf.Slice(448), buf.Slice(512), swindow, 64);
                temp.Slice(0, 64).CopyTo(output.Slice(960));
            }
            else
            {
                // LongStop: short window overlap then flat copy
                VectorFmulWindow(output.Slice(448), saved.Slice(448), buf, swindowPrev, 64);
                buf.Slice(64, 448).CopyTo(output.Slice(576));
            }
        }

        // === Buffer update (save second half for next frame's overlap) ===
        if (windowSequence == WindowSequence.EightShortSequence)
        {
            // Tail of block3→4 overlap
            temp.Slice(64, 64).CopyTo(saved);
            // Block4→5, 5→6, 6→7 overlaps stored in saved
            VectorFmulWindow(saved.Slice(64), buf.Slice(576), buf.Slice(640), swindow, 64);
            VectorFmulWindow(saved.Slice(192), buf.Slice(704), buf.Slice(768), swindow, 64);
            VectorFmulWindow(saved.Slice(320), buf.Slice(832), buf.Slice(896), swindow, 64);
            // Tail of block7 (unwindowed)
            buf.Slice(960, 64).CopyTo(saved.Slice(448));
            saved.Slice(512).Clear();
        }
        else
        {
            // OnlyLong, LongStart, LongStop: save buf[512..1023] (unwindowed)
            buf.Slice(512, 512).CopyTo(saved);
            saved.Slice(512).Clear();
        }
    }

    /// <summary>
    /// <c>imdct_and_windowing_960</c>: the same three stages at 960 samples a frame.
    /// </summary>
    /// <remarks>
    /// The spectral coefficients still sit 128 apart per window even here - the short-block loop
    /// reads <c>in + i * 128</c> and writes <c>buf + i * 120</c> - so only the time-domain side
    /// shortens. The overlap lengths are 480 long and 60 short, and the flat copy before the
    /// short overlaps is 420 rather than 448.
    /// </remarks>
    private void ImdctAndWindowing960(
        ReadOnlySpan<float> spectral,
        Span<float> output,
        Span<float> saved,
        WindowSequence windowSequence,
        WindowSequence prevWindowSequence,
        int windowShape,
        int prevWindowShape,
        Span<float> buf)
    {
        var lwindowPrev = prevWindowShape == 0 ? longWindowSine960! : longWindowKbd960!;
        var swindow = windowShape == 0 ? shortWindowSine120! : shortWindowKbd120!;
        var swindowPrev = prevWindowShape == 0 ? shortWindowSine120! : shortWindowKbd120!;

        // === Half-IMDCT ===
        if (windowSequence == WindowSequence.EightShortSequence)
        {
            for (int i = 0; i < 8; i++)
                mdct120!.Transform(buf.Slice(i * 120, 120), spectral.Slice(i * 128, 128));
        }
        else
        {
            mdct960!.Transform(buf.Slice(0, 960), spectral.Slice(0, 960));
        }

        // === Windowed overlap ===
        bool prevIsLongType = prevWindowSequence == WindowSequence.OnlyLongSequence ||
                              prevWindowSequence == WindowSequence.LongStopSequence;
        bool currIsLongType = windowSequence == WindowSequence.OnlyLongSequence ||
                              windowSequence == WindowSequence.LongStartSequence;

        // ac->temp, needed because the block 3 to 4 overlap straddles output and saved.
        Span<float> temp = stackalloc float[120];

        if (prevIsLongType && currIsLongType)
        {
            VectorFmulWindow(output, saved, buf, lwindowPrev, 480);
        }
        else
        {
            saved.Slice(0, 420).CopyTo(output);

            if (windowSequence == WindowSequence.EightShortSequence)
            {
                VectorFmulWindow(output.Slice(420), saved.Slice(420), buf, swindowPrev, 60);
                VectorFmulWindow(output.Slice(420 + 120), buf.Slice(60), buf.Slice(120), swindow, 60);
                VectorFmulWindow(output.Slice(420 + 240), buf.Slice(180), buf.Slice(240), swindow, 60);
                VectorFmulWindow(output.Slice(420 + 360), buf.Slice(300), buf.Slice(360), swindow, 60);
                VectorFmulWindow(temp, buf.Slice(420), buf.Slice(480), swindow, 60);
                temp.Slice(0, 60).CopyTo(output.Slice(420 + 480));
            }
            else
            {
                VectorFmulWindow(output.Slice(420), saved.Slice(420), buf, swindowPrev, 60);
                buf.Slice(60, 420).CopyTo(output.Slice(540));
            }
        }

        // === Buffer update ===
        if (windowSequence == WindowSequence.EightShortSequence)
        {
            temp.Slice(60, 60).CopyTo(saved);
            VectorFmulWindow(saved.Slice(60), buf.Slice(540), buf.Slice(600), swindow, 60);
            VectorFmulWindow(saved.Slice(180), buf.Slice(660), buf.Slice(720), swindow, 60);
            VectorFmulWindow(saved.Slice(300), buf.Slice(780), buf.Slice(840), swindow, 60);
            buf.Slice(900, 60).CopyTo(saved.Slice(420));
            saved.Slice(480).Clear();
        }
        else
        {
            // LONG_START is a separate branch in the C, copying buf[480..899] and then
            // buf[900..959]; the two copies are contiguous, so they are the one copy below.
            buf.Slice(480, 480).CopyTo(saved);
            saved.Slice(480).Clear();
        }
    }

    #region Private Implementation

    /// <summary>
    /// Windowed overlap-add. Direct port of FFmpeg's vector_fmul_window_c.
    /// Produces 2*halfLen output samples from halfLen of src0 and src1,
    /// using a window of 2*halfLen entries.
    /// 
    /// dst[k]              = src0[k] * win[2*halfLen-1-k] - src1[halfLen-1-k] * win[k]
    /// dst[2*halfLen-1-k]  = src0[k] * win[k]             + src1[halfLen-1-k] * win[2*halfLen-1-k]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// <c>imdct_and_windowing_ld</c>: IMDCT, windowing and overlap-add for one AAC LD channel.
    /// </summary>
    /// <remarks>
    /// LD is defined only for ONLY_LONG_SEQUENCE, so there is no window sequence to dispatch on
    /// and none of the eight-short machinery applies. What is left is a choice of overlap: the
    /// previous frame's <c>use_kb_window</c> selects between a full 512-tap sine overlap and a
    /// LOW OVERLAP one, 64 taps of <c>sine_128</c> with flat copies either side. The low-overlap
    /// form is what the shape bit means here - LD substitutes it for the Kaiser-Bessel window
    /// that the bit selects in every other profile, which is why this reads the bit and then
    /// uses a sine window in both branches.
    ///
    /// The lengths are FFmpeg's literal constants, not <c>n</c> scaled from the frame length:
    /// <c>imdct_and_windowing_ld</c> uses <c>mdct512</c> and 192/64/320/256 whatever
    /// <c>frame_length_short</c> says. An LD stream at 960 would therefore transform at 512 in
    /// FFmpeg too, and this matches it rather than guessing at a 480 form FFmpeg has never run.
    /// </remarks>
    /// <param name="spectral">The 512 spectral coefficients.</param>
    /// <param name="output">The 512 output samples.</param>
    /// <param name="saved">The overlap buffer; its first 256 entries are read and rewritten.</param>
    /// <param name="prevUseKbWindow">The PREVIOUS frame's window shape bit.</param>
    /// <param name="buf">Scratch for the half-IMDCT output, at least 512 samples.</param>
    public void ImdctAndWindowingLd(
        ReadOnlySpan<float> spectral,
        Span<float> output,
        Span<float> saved,
        bool prevUseKbWindow,
        Span<float> buf)
    {
        mdct512.Transform(buf.Slice(0, 512), spectral.Slice(0, 512));

        if (prevUseKbWindow)
        {
            saved.Slice(0, 192).CopyTo(output);
            VectorFmulWindow(output.Slice(192, 128), saved.Slice(192, 64), buf.Slice(0, 64),
                             AacWindowTables.SineWindowShort, 64);
            buf.Slice(64, 192).CopyTo(output.Slice(320));
        }
        else
        {
            VectorFmulWindow(output.Slice(0, 512), saved.Slice(0, 256), buf.Slice(0, 256),
                             AacWindowTables.SineWindow512, 256);
        }

        buf.Slice(256, 256).CopyTo(saved);
    }

    /// <summary>
    /// <c>imdct_and_windowing_eld</c>: IMDCT, windowing and overlap-add for one ELD channel.
    /// </summary>
    /// <remarks>
    /// ELD's inverse transform is not the AAC IMDCT. It is mapped onto one by the construction
    /// in Chivukula, Reznik and Devarajan, "Efficient algorithms for MPEG-4 AAC-ELD, AAC-LD and
    /// AAC-LC filterbanks" (ICALIP 2008): reverse the coefficients in place with a sign pattern,
    /// run the ordinary MDCT, then alternate the sign of the result. The pre-pass WRITES THROUGH
    /// <paramref name="spectral"/>, which is why that span is mutable - FFmpeg's <c>in</c> is
    /// <c>sce-&gt;coeffs</c> itself and the swap is destructive. A caller that needs the
    /// coefficients afterwards must copy them first.
    ///
    /// The overlap then runs four frames deep rather than two: <paramref name="saved"/> holds
    /// 3n samples and is shifted whole each frame. The window offsets are FFmpeg's, including
    /// the comment it carries - the specification says to window samples [0..511] and the
    /// reference decoder uses [128..639], so the loops start at n/4 and the window index is
    /// biased by -n/4 throughout.
    /// </remarks>
    /// <param name="spectral">The n spectral coefficients. OVERWRITTEN by the pre-pass.</param>
    /// <param name="output">The n output samples.</param>
    /// <param name="saved">The 3n-sample overlap buffer, shifted and refilled in place.</param>
    /// <param name="n">512, or 480 at 960 frame length.</param>
    /// <param name="buf">Scratch for the MDCT output, at least n samples.</param>
    public void ImdctAndWindowingEld(
        Span<float> spectral,
        Span<float> output,
        Span<float> saved,
        int n,
        Span<float> buf)
    {
        if (n != 512 && n != 480)
            throw new ArgumentOutOfRangeException(nameof(n), n,
                "ELD has exactly two frame lengths, 512 and 480.");

        int n2 = n >> 1;
        int n4 = n >> 2;
        float[] window = n == 480 ? AacEldWindowTables.EldWindow480
                                  : AacEldWindowTables.EldWindow512;

        for (int i = 0; i < n2; i += 2)
        {
            float temp = spectral[i];
            spectral[i] = -spectral[n - 1 - i];
            spectral[n - 1 - i] = temp;

            temp = -spectral[i + 1];
            spectral[i + 1] = spectral[n - 2 - i];
            spectral[n - 2 - i] = temp;
        }

        if (n == 480)
            mdct480.Transform(buf.Slice(0, 480), spectral.Slice(0, 480));
        else
            mdct512.Transform(buf.Slice(0, 512), spectral.Slice(0, 512));

        // The C scales both halves of the pair by (USE_FIXED + 1), negating the even one. In the
        // float build that factor is 1, so only the sign survives and the odd entry is left
        // alone; the fixed-point build doubles both, which is where its extra bit of headroom
        // comes from and is not something a float port can or should reproduce.
        for (int i = 0; i < n; i += 2)
            buf[i] = -buf[i];

        // Like the regular IMDCT, at this point we hold the middle half of a transform, with
        // even symmetry on the left and odd symmetry on the right.
        for (int i = n4; i < n2; i++)
        {
            output[i - n4] = buf[n2 - 1 - i] * window[i - n4]
                           + saved[i + n2] * window[i + n - n4]
                           - saved[n + n2 - 1 - i] * window[i + 2 * n - n4]
                           - saved[2 * n + n2 + i] * window[i + 3 * n - n4];
        }

        for (int i = 0; i < n2; i++)
        {
            output[n4 + i] = buf[i] * window[i + n2 - n4]
                           - saved[n - 1 - i] * window[i + n2 + n - n4]
                           - saved[n + i] * window[i + n2 + 2 * n - n4]
                           + saved[2 * n + n - 1 - i] * window[i + n2 + 3 * n - n4];
        }

        for (int i = 0; i < n4; i++)
        {
            output[n2 + n4 + i] = buf[i + n2] * window[i + n - n4]
                                - saved[n2 - 1 - i] * window[i + 2 * n - n4]
                                - saved[n + n2 + i] * window[i + 3 * n - n4];
        }

        // memmove, not memcpy: the ranges overlap, and the shift has to read the old contents.
        saved.Slice(0, 2 * n).CopyTo(saved.Slice(n, 2 * n));
        buf.Slice(0, n).CopyTo(saved);
    }

    private static void VectorFmulWindow(
        Span<float> dst,
        ReadOnlySpan<float> src0,
        ReadOnlySpan<float> src1,
        float[] win,
        int halfLen)
    {
        for (int k = 0; k < halfLen; k++)
        {
            float s0 = src0[k];
            float s1 = src1[halfLen - 1 - k];
            float wi = win[k];
            float wj = win[2 * halfLen - 1 - k];
            dst[k] = s0 * wj - s1 * wi;
            dst[2 * halfLen - 1 - k] = s0 * wi + s1 * wj;
        }
    }

    #endregion
}

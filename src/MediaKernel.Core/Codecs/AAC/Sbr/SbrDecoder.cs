using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// Spectral Band Replication: the HE-AAC tool that rebuilds the upper spectrum from the AAC core
/// plus a compact parametric description.
/// </summary>
/// <remarks>
/// This currently parses the payload and maintains the band tables; reconstruction is not wired in
/// yet, so <see cref="Ready"/> reports whether a frame's parameters were understood rather than
/// whether audio was produced. Parsing is worth having on its own: it is exactly verifiable (a
/// correct parse consumes the declared payload to the byte) and everything else builds on it.
/// </remarks>
internal sealed class SbrDecoder
{
    /// <summary>Extension payload type for SBR data (14496-3 Table 4.51).</summary>
    public const int ExtensionSbrData = 13;

    /// <summary>SBR data preceded by a CRC.</summary>
    public const int ExtensionSbrDataCrc = 14;

    private readonly SbrContext context = new();
    private readonly SbrTables tables = new();

    /// <summary>
    /// <c>ff_aac_sbr_init</c> calls <c>ff_ps_init</c>; so does this. The PS tables are computed
    /// rather than stored, and nothing else builds them.
    /// </summary>
    public SbrDecoder() => Ps.PsTables.Initialise();

    /// <summary>Payloads seen, including those skipped for want of a header.</summary>
    public int PayloadsSeen { get; private set; }

    /// <summary>
    /// Payloads that arrived before any SBR header. Joining a live stream mid-broadcast always
    /// produces a few of these: headers are sent periodically, and until one arrives the band
    /// tables are unknown, so the payload can only be skipped. FFmpeg behaves the same way.
    /// </summary>
    public int PayloadsBeforeHeader { get; private set; }

    /// <summary>Payloads that were parsed, as opposed to skipped.</summary>
    public int PayloadsParsed { get; private set; }

    /// <summary>
    /// Parsed payloads whose consumed length matched the declared one exactly. This should equal
    /// <see cref="PayloadsParsed"/>; anything less is a real divergence.
    /// </summary>
    public int PayloadsExactLength { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Payloads whose consumed length did not match the declared one, as (declared, consumed,
    /// extensionPresent). A correct parse should leave this empty; anything in it is a divergence
    /// worth explaining rather than tolerating.
    /// </summary>
    /// <summary>
    /// The first <see cref="MaxLengthMismatches"/> payloads whose consumed length did not match
    /// their declared one, for diagnostics.
    /// </summary>
    /// <remarks>
    /// Built to its full capacity up front. It is capped, so the growth was bounded either way,
    /// but a decoder that allocates at all while decoding fails the no-allocation gate - and a
    /// diagnostic is the last thing that should be spending allocations on a decode path.
    /// </remarks>
    public System.Collections.Generic.List<(int Index, int Declared, int Consumed, bool Started)>
        LengthMismatches { get; } = new(MaxLengthMismatches);

    /// <summary>How many mismatching payloads are recorded before the rest are counted only.</summary>
    private const int MaxLengthMismatches = 32;

    /// <summary>True when a header has been seen and the band tables are valid.</summary>
    public bool Ready => context.Started && !context.Disabled;

    /// <summary>Sample rate of the reconstructed signal: twice the AAC core's.</summary>
    public int SampleRate => context.SampleRate;

    /// <summary>
    /// <c>m4ac.ps</c>: -1 not yet known, 0 absent, 1 present. The AAC decoder sets it from the
    /// AudioSpecificConfig, and raises it from -1 to 1 the first time a PS payload appears in a
    /// mono stream, which is how implicitly signalled HE-AACv2 is detected.
    /// </summary>
    public int PsMode
    {
        get => context.PsMode;
        set => context.PsMode = value;
    }

    /// <summary>True once a PS payload has been parsed, not merely seen.</summary>
    public bool PsStarted => context.Ps.Common.Start;

    /// <summary>
    /// The Parametric Stereo parameters of the most recent payload, for diagnosis. Formats on
    /// demand and is called from tests, never from the decode path.
    /// </summary>
    public string DescribePs()
    {
        var ps = context.Ps.Common;
        return $"start={(ps.Start ? 1 : 0)} iid={(ps.EnableIid ? 1 : 0)}/{ps.NrIidPar} " +
               $"quant={ps.IidQuant} icc={(ps.EnableIcc ? 1 : 0)}/{ps.NrIccPar} " +
               $"iccMode={ps.IccMode} ext={(ps.EnableExt ? 1 : 0)} " +
               $"ipdopd={(ps.EnableIpdOpd ? 1 : 0)}/{ps.NrIpdOpdPar} " +
               $"is34={(ps.Is34Bands ? 1 : 0)} nenv={ps.NumEnv} class={ps.FrameClass}" +
               (ps.FailReason is null ? "" : $" FAILED: {ps.DescribeFailure()}") +
               $" maxiid={MaxAbs(ps.IidPar, ps.NumEnv, ps.NrIidPar)}" +
               $" iid0=[{string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(ps.IidPar[0], 6), v => (int)v))}]" +
               $" icc0=[{string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(ps.IccPar[0], 6), v => (int)v))}]";
    }

    /// <summary>
    /// Where to write a per-frame dump of every SBR stage, or null for no dump. This exists to be
    /// diffed against the same dump taken from a patched FFmpeg: that comparison is what found
    /// the <c>s_indexmapped</c> clear, after reading the C line by line had cleared the same code
    /// as faithful. See the shadow-decoder section in the project notes for how to build the
    /// other half and for the frame offset the comparison needs.
    /// </summary>
    /// <remarks>
    /// The body lives in <see cref="DumpStages"/> rather than inline in <see cref="Apply"/>:
    /// switched-off diagnostic bodies have measured 4-8% in this project twice by inflating the
    /// method around them, so the per-frame path keeps only the null check.
    /// </remarks>
    public static System.IO.TextWriter? StageDump;

    /// <summary>
    /// Which channel element this decoder belongs to, as <c>(type &lt;&lt; 8) | instance tag</c>.
    /// Only used to label the stage dump, so a multi-element stream's records can be told apart.
    /// </summary>
    public int ElementKey;
    private static int stageFrame;

    private static double StageEnergy(float[][] band, int from, int count)
    {
        double e = 0;
        for (int i = from; i < from + count && i < band.Length; i++)
            e += (double)band[i][0] * band[i][0] + (double)band[i][1] * band[i][1];
        return e;
    }

    /// <summary>
    /// One frame of every SBR intermediate, in the format the patched <c>aacsbr_template.c</c>
    /// writes: band layout and envelope borders, chirp factors, the low and high bands, the
    /// mapped envelope/noise/sinusoid values, the gains, and the assembled output.
    /// </summary>
    private void DumpStages(System.IO.TextWriter d, SbrChannelState state,
                            SbrChannelData channel, float[][][] adjusted, int ch,
                            bool active, int startSubband, int subbandCount)
    {
        var adjust = state.Adjustment;
        var borders = new System.Text.StringBuilder();
        for (int e = 0; e <= channel.EnvelopeCount; e++)
            borders.Append(' ').Append(channel.EnvelopeBorders[e]);

        d.WriteLine($"SBRF {stageFrame} ch {ch} elem {ElementKey} start {(active ? 1 : 0)} " +
                    $"kx0 {previousStartSubband} kx1 {startSubband} " +
                    $"m0 {previousSubbandCount} m1 {subbandCount} " +
                    $"nenv {channel.EnvelopeCount}{borders} " +
                    $"ea {channel.SineStartEnvelope[0]} {channel.SineStartEnvelope[1]}");

        for (int k = 0; k < 5; k++)
            d.WriteLine($"BW {stageFrame} {ch} {k} {state.Chirp[k]:e9}");

        for (int k = 0; k < 32; k++)
            d.WriteLine($"XLOW {stageFrame} {ch} {k} " +
                        $"{StageEnergy(highFrequency.LowBand[k], 8, 32):e9}");

        for (int k = 0; k < subbandCount; k++)
            d.WriteLine($"XHIGH {stageFrame} {ch} {k} " +
                        $"{StageEnergy(highFrequency.HighBand[k], 8, 32):e9}");

        for (int e = 0; e < channel.EnvelopeCount; e++)
            for (int k = 0; k < subbandCount; k++)
                d.WriteLine($"MAP {stageFrame} {ch} {e} {k} " +
                            $"{adjust.OriginalEnvelope[e][k]:e9} " +
                            $"{adjust.MappedNoise[e][k]:e9} " +
                            $"{adjust.MappedSine[e][k]} " +
                            $"{channel.SineIndexMapped[e + 1][k]}");

        for (int e = 0; e < channel.EnvelopeCount; e++)
            for (int k = 0; k < subbandCount; k++)
                d.WriteLine($"GAIN {stageFrame} {ch} {e} {k} {adjust.Gain[e][k]:e9} " +
                            $"{adjust.NoiseLevel[e][k]:e9} {adjust.CurrentEnvelope[e][k]:e9}");

        // The assembled spectrum is slot-major: slot, then band.
        for (int slot = 0; slot < 38 && slot < adjusted.Length; slot++)
            d.WriteLine($"Y {stageFrame} {ch} {slot} {StageEnergy(adjusted[slot], 0, 64):e9}");
    }

    /// <summary>Largest absolute parameter value over the envelopes actually present.</summary>
    private static int MaxAbs(sbyte[][] parameters, int envelopes, int bands)
    {
        int max = 0;
        for (int e = 0; e < envelopes && e < parameters.Length; e++)
            for (int b = 0; b < bands && b < parameters[e].Length; b++)
            {
                int v = Math.Abs(parameters[e][b]);
                if (v > max) max = v;
            }
        return max;
    }

    /// <summary>Number of QMF subbands SBR reconstructs, once a header has been read.</summary>
    public int SubbandCount => tables.SubbandCount;

    /// <summary>First QMF subband SBR reconstructs.</summary>
    public int StartSubband => tables.StartSubband;

    /// <summary>The derived band layout, for diagnosis.</summary>
    /// <summary>True once a Parametric Stereo payload has been seen in the bitstream.</summary>
    public bool SawParametricStereo => context.SawParametricStereo;

    /// <summary>Bit set of SBR extension ids seen, for diagnosis.</summary>
    public uint ExtensionsSeen => context.ExtensionsSeen;

    public string DescribeTables() =>
        $"k0={context.K[0]} k1={context.K[1]} k2={context.K[2]} kx={tables.StartSubband} " +
        $"M={tables.SubbandCount} nMaster={tables.MasterCount} nLow={context.BandCount[0]} " +
        $"nHigh={context.BandCount[1]} nQ={context.NoiseBandCount} nLim={tables.LimiterCount} " +
        $"patches={tables.PatchCount}";

    /// <summary>Envelopes in the most recent frame, per channel.</summary>
    public int EnvelopeCount(int channel) => context.Channels[channel].EnvelopeCount;

    /// <summary>
    /// Tell SBR the core rate and the rate the configuration says the output should run at.
    /// </summary>
    /// <remarks>
    /// <c>sbr->sample_rate</c> is always twice the core rate. The configuration's extension rate
    /// is a separate thing, and when it is lower - "single rate" SBR, where the extension rate
    /// equals the core rate - FFmpeg synthesises through the downsampled filterbank and produces
    /// 1024 samples at the core rate rather than 2048 at twice it. A decoder that ignores this
    /// returns a frame of the wrong length, which is how it shows up: not as distortion but as
    /// an output 1.5 times too long.
    /// </remarks>
    public void Configure(int coreSampleRate, int extensionSampleRate = 0)
    {
        int sbrRate = coreSampleRate * 2;
        if (context.SampleRate != sbrRate)
        {
            context.SampleRate = sbrRate;
            context.Reset();
            context.SampleRate = sbrRate;
        }

        context.ExtensionSampleRate = extensionSampleRate > 0 ? extensionSampleRate : sbrRate;
    }

    /// <summary><c>downsampled</c>: the extension rate is below twice the core rate.</summary>
    public bool Downsampled =>
        context.ExtensionSampleRate > 0 && context.ExtensionSampleRate < context.SampleRate;

    /// <summary>The rate the samples <see cref="Apply"/> writes actually run at.</summary>
    public int OutputSampleRate =>
        context.ExtensionSampleRate > 0 ? context.ExtensionSampleRate : context.SampleRate;

    public void Reset()
    {
        context.Reset();
        foreach (var state in channelStates)
            state.Reset();
        highFrequency.Reset();
        // kx' initialises to 32, not 0: sbr_turnoff sets "sbr->kx[1] = 32; //Typo in spec, kx'
        // inits to 32" and the push at the top of the next frame carries it into kx[0]. Zero
        // would make sbr_lf_gen copy no overlap from the previous frame on the frame after a
        // reset or a turnoff, where the reference copies all 32 low bands.
        previousStartSubband = SbrQmf.AnalysisBands;
        previousSubbandCount = 0;
        justReset = true;
    }

    /// <summary>
    /// <c>sbr_turnoff</c>: stop decoding SBR data but keep running the filterbanks, so the
    /// element still comes out upsampled with nothing above the crossover.
    /// </summary>
    /// <remarks>
    /// This is not <see cref="Reset"/>. A reset re-primes the smoothing history for a new header;
    /// a turnoff says the payload could not be trusted and the rest of the stream is to be treated
    /// as pure upsampling. The spectrum parameters go to -1 rather than zero so that the next
    /// header that does arrive compares unequal to them and triggers a rebuild - the C memsets the
    /// whole struct to -1 for exactly that reason.
    /// </remarks>
    public void Turnoff()
    {
        context.Started = false;
        context.Disabled = true;

        // kx' inits to 32, not 0 - the C's own comment calls it a typo in the spec.
        previousStartSubband = SbrQmf.AnalysisBands;
        previousSubbandCount = 0;

        foreach (var channel in context.Channels)
            channel.SineStartEnvelope[1] = -1;

        context.SpectrumParameters = SbrSpectrumParameters.Invalid;
    }

    private readonly SbrChannelState[] channelStates = [new SbrChannelState(), new SbrChannelState()];
    private readonly SbrHighFrequency highFrequency = new();

    /// <summary>kx and M from the previous frame, needed while the low band spans the boundary.</summary>
    private int previousStartSubband = SbrQmf.AnalysisBands;
    private int previousSubbandCount;

    /// <summary>True for the first frame after a header change, when the smoothing history is primed
    /// from this frame's own gains rather than carried over.</summary>
    private bool justReset = true;

    /// <summary>
    /// Runs the whole reconstruction for one AAC frame: analysis, high frequency generation,
    /// envelope adjustment and synthesis. Each channel's 1024 core samples become 2048 samples at
    /// twice the rate, written back over the input.
    /// </summary>
    /// <param name="channels">Per-channel buffers, each at least 2048 samples long.</param>
    /// <param name="channelCount">How many of them hold decoded audio.</param>
    /// <returns>Samples produced per channel, or 0 when SBR could not be applied.</returns>
    public int Apply(float[][] channels, int channelCount)
    {
        if (channelCount <= 0 || context.SampleRate <= 0)
            return 0;

        int usable = Math.Min(channelCount, channelStates.Length);
        bool downsampled = Downsampled;
        int outputSamples = downsampled ? 1024 : 2048;

        // Pure upsampling mode. FFmpeg calls ff_aac_sbr_apply whenever SBR is signalled, before
        // any header has arrived and after sbr_turnoff, and in that state it runs the
        // filterbanks with kx' = 32 and M = 0 - so the output is the core upsampled with nothing
        // above it, and still a full frame long. Returning the core frame instead, as this used
        // to, makes the output of a stream whose first payloads precede its first header come
        // out short: three of FFmpeg's own HE-AAC conformance streams are 25% to 50% the wrong
        // length that way, which no amount of per-sample comparison can repair.
        bool active = context.Started && !context.Disabled;

        // kx and M advance one frame behind, because the low band for this frame is partly built
        // from the previous frame's slots and has to be indexed with the layout they used.
        int startSubband = active ? tables.StartSubband : SbrQmf.AnalysisBands;
        int subbandCount = active ? tables.SubbandCount : 0;

        // Coupled stereo has to be dequantised for the pair before either channel is touched, so
        // this happens once for the element rather than inside the per-channel loop.
        if (active)
            SbrEnvelopeAdjustment.Dequantise(context, usable);

        for (int ch = 0; ch < usable; ch++)
        {
            var state = channelStates[ch];
            var channel = context.Channels[ch];

            if (channels[ch].Length < outputSamples)
                return 0;

            var current = state.Analysis[state.AnalysisBuffer];
            var previous = state.Analysis[1 - state.AnalysisBuffer];

            state.Filterbank.Analyse(channels[ch].AsSpan(0, 1024), current);
            highFrequency.GenerateLowBand(current, previous, startSubband, previousStartSubband);
            state.AnalysisBuffer ^= 1;

            if (FrameTrace is not null)
                FrameTrace.Add(DescribeAnalysis(current, ch));

            if (!active)
            {
                BuildSpectrum(state, channel, startSubband, subbandCount);

                // Dump the inactive frames too: the reference decoder runs every element through
                // the filterbanks whether or not SBR is on for it, so its dump has a record for
                // every element and channel of every frame. Skipping ours here would make the
                // two sequences align only by accident.
                if (StageDump is not null)
                    DumpStages(StageDump, state, channel, state.Adjusted[state.AnalysisBuffer],
                               ch, active, startSubband, subbandCount);
                continue;
            }

            highFrequency.ComputeInverseFilter(context.K[0]);
            SbrHighFrequency.UpdateChirpFactors(channel, state.Chirp, context.NoiseBandCount);

            // A failure here is sbr_turnoff in the C: SBR stops for the rest of the stream, but
            // this frame still comes out as a full upsampled frame rather than as nothing.
            if (!highFrequency.Generate(context, tables, state.Chirp,
                                        channel.EnvelopeBorders[0],
                                        channel.EnvelopeBorders[channel.EnvelopeCount])
                || !state.Adjustment.Map(context, tables, channel))
            {
                context.Disabled = true;
                active = false;
                startSubband = SbrQmf.AnalysisBands;
                subbandCount = 0;
                highFrequency.GenerateLowBand(current, previous, startSubband, previousStartSubband);
                BuildSpectrum(state, channel, startSubband, subbandCount);
                continue;
            }

            state.Adjustment.EstimateEnvelope(context, tables, channel, highFrequency.HighBand);
            state.Adjustment.GainTrace = FrameTrace;
            state.Adjustment.CalculateGains(context, tables, channel);

            if (FrameTrace is not null)
                FrameTrace.Add(DescribeGains(state, channel, ch, subbandCount));

            // Deliberately not cleared: the next frame reads slots 32..37 of this buffer, which a
            // frame whose envelope borders run past slot 16 writes and a shorter one does not.
            // Clearing here silently drops the overlap the following frame needs.
            var adjusted = state.Adjusted[state.AnalysisBuffer];

            state.Adjustment.Assemble(context, tables, channel, highFrequency.HighBand, adjusted,
                                      reset: justReset);

            BuildSpectrum(state, channel, startSubband, subbandCount);

            if (StageDump is not null)
                DumpStages(StageDump, state, channel, adjusted, ch, active,
                           startSubband, subbandCount);

            if (FrameTrace is not null)
                FrameTrace.Add(DescribeFrame(state, channel, ch, startSubband, subbandCount));
        }

        if (StageDump is not null) stageFrame++;


        // Parametric Stereo turns the one reconstructed spectrum into two before synthesis.
        // When the configuration says PS is present but no payload has arrived yet, FFmpeg still
        // produces two channels by copying - the output channel count is already fixed, and
        // emitting one channel would desynchronise the caller.
        int outputChannels = usable;
        if (context.PsMode == 1 && usable == 1)
        {
            float[][][] left = channelStates[0].Spectrum;
            float[][][] right = channelStates[1].Spectrum;

            if (context.Ps.Common.Start)
            {
                Ps.AacPs.Apply(context.Ps, left, right, startSubband + subbandCount);
            }
            else
            {
                for (int part = 0; part < 2; part++)
                    for (int slot = 0; slot < SbrChannelState.OutputSlots; slot++)
                        left[part][slot].CopyTo(right[part][slot], 0);
            }

            outputChannels = 2;
        }

        if (outputChannels > channels.Length) return 0;

        for (int ch = 0; ch < outputChannels; ch++)
        {
            if (channels[ch].Length < outputSamples) return 0;
            channelStates[ch].Filterbank.Synthesise(
                channelStates[ch].Spectrum, channels[ch].AsSpan(0, outputSamples), downsampled);
        }

        previousStartSubband = startSubband;
        previousSubbandCount = subbandCount;
        justReset = false;
        OutputChannels = outputChannels;

        return outputSamples;
    }

    /// <summary>
    /// Channels the last <see cref="Apply"/> wrote. This is the input count except under
    /// Parametric Stereo, where one coded channel becomes two.
    /// </summary>
    public int OutputChannels { get; private set; }

    /// <summary>
    /// Per-frame fingerprints of the reconstruction state, in the same form the instrumented
    /// FFmpeg build emits, so the two can be diffed frame by frame. Null unless set.
    /// </summary>
    public System.Collections.Generic.List<string>? FrameTrace;

    private int traceFrame;

    private int traceGainFrame;

    /// <summary>Fingerprints the envelope estimate and the gains derived from it.</summary>
    private string DescribeGains(SbrChannelState state, SbrChannelData channel, int ch, int subbandCount)
    {
        var adjust = state.Adjustment;
        double ec = 0, gn = 0, qm = 0, sm = 0;

        for (int e = 0; e < channel.EnvelopeCount; e++)
            for (int m = 0; m < subbandCount; m++)
            {
                ec += Math.Abs(adjust.CurrentEnvelope[e][m]);
                gn += Math.Abs(adjust.Gain[e][m]);
                qm += Math.Abs(adjust.NoiseLevel[e][m]);
                sm += Math.Abs(adjust.SineLevel[e][m]);
            }

        if (ch == 0) traceGainFrame++;

        double eo = 0, qmap = 0;
        for (int e = 0; e < channel.EnvelopeCount; e++)
            for (int m = 0; m < subbandCount; m++)
            {
                eo += Math.Abs(adjust.OriginalEnvelope[e][m]);
                qmap += Math.Abs(adjust.MappedNoise[e][m]);
            }

        return $"MKENV f={traceGainFrame - 1} ch={ch} nenv={channel.EnvelopeCount} " +
               $"ea0={channel.SineStartEnvelope[0]} ea1={channel.SineStartEnvelope[1]} " +
               $"ec={ec:F4} gn={gn:F4} qm={qm:F4} sm={sm:F4} " +
               $"eo={eo:F4} qmap={qmap:F4} nlim={tables.LimiterCount} " +
               $"lim0={tables.Limiter[0]} lim1={tables.Limiter[1]} " +
               $"lim2={(tables.LimiterCount > 1 ? tables.Limiter[2] : -1)}";
    }

    private int traceAnalysisFrame;

    /// <summary>Fingerprints the analysis bank output and the low band derived from it.</summary>
    private string DescribeAnalysis(float[][][] current, int ch)
    {
        double wr = 0, wi = 0, xl = 0;

        for (int i = 0; i < 32; i++)
            for (int k = 0; k < 32; k++)
            {
                wr += Math.Abs(current[i][k][0]);
                wi += Math.Abs(current[i][k][1]);
            }

        for (int k = 0; k < 32; k++)
            for (int i = 0; i < 40; i++)
                xl += Math.Abs(highFrequency.LowBand[k][i][0]) + Math.Abs(highFrequency.LowBand[k][i][1]);

        if (ch == 0) traceAnalysisFrame++;

        return $"MKW f={traceAnalysisFrame - 1} ch={ch} wr={wr:F4} wi={wi:F4} xl={xl:F4} " +
               $"w00={current[0][0][0]:F6} w01={current[0][0][1]:F6}";
    }

    private string DescribeFrame(SbrChannelState state, SbrChannelData channel, int ch,
                                 int startSubband, int subbandCount)
    {
        double xre = 0, xim = 0, xlo = 0, xhi = 0, low = 0, high = 0;

        for (int i = 0; i < 32; i++)
        {
            for (int k = 0; k < 64; k++)
            {
                xre += state.Spectrum[0][i][k] * (i + 1);
                xim += state.Spectrum[1][i][k] * (k + 1);
                if (k < startSubband) xlo += Math.Abs(state.Spectrum[0][i][k]);
                else xhi += Math.Abs(state.Spectrum[0][i][k]);
            }
        }

        for (int k = 0; k < 32; k++)
            for (int i = 0; i < 40; i++)
                low += Math.Abs(highFrequency.LowBand[k][i][0]) * (k + 1);

        for (int k = 0; k < 64; k++)
            for (int i = 0; i < 40; i++)
                high += Math.Abs(highFrequency.HighBand[k][i][0]) * (k + 1);

        if (ch == 0) traceFrame++;

        return $"MKSBR f={traceFrame - 1} ch={ch} kx0={previousStartSubband} kx1={startSubband} " +
               $"m0={previousSubbandCount} m1={subbandCount} nenv={channel.EnvelopeCount} " +
               $"Xre={xre:F3} Xim={xim:F3} Xlo={xlo:F3} Xhi={xhi:F3} low={low:F3} high={high:F3}";
    }

    /// <summary>
    /// Combines the low band and the adjusted high band into the synthesis input
    /// (14496-3 sp04, sbr_x_gen). The split point moves partway through the frame: slots before it
    /// use the previous frame's band layout and its adjusted high band, slots after use this one's.
    /// </summary>
    private void BuildSpectrum(SbrChannelState state, SbrChannelData channel,
                               int startSubband, int subbandCount)
    {
        var spectrum = state.Spectrum;
        foreach (var part in spectrum)
            foreach (var slot in part)
                Array.Clear(slot);

        var lowBand = highFrequency.LowBand;
        var previousAdjusted = state.Adjusted[1 - state.AnalysisBuffer];
        var currentAdjusted = state.Adjusted[state.AnalysisBuffer];

        int split = Math.Max(2 * channel.PreviousFinalBorder - SbrQmf.TimeSlots, 0);
        split = Math.Min(split, SbrQmf.TimeSlots);

        for (int k = 0; k < previousStartSubband && k < 32; k++)
        {
            for (int i = 0; i < split; i++)
            {
                spectrum[0][i][k] = lowBand[k][i + SbrHighFrequency.EnvelopeAdjustmentOffset][0];
                spectrum[1][i][k] = lowBand[k][i + SbrHighFrequency.EnvelopeAdjustmentOffset][1];
            }
        }

        for (int k = previousStartSubband; k < previousStartSubband + previousSubbandCount && k < 64; k++)
        {
            for (int i = 0; i < split; i++)
            {
                spectrum[0][i][k] = previousAdjusted[i + SbrQmf.TimeSlots][k][0];
                spectrum[1][i][k] = previousAdjusted[i + SbrQmf.TimeSlots][k][1];
            }
        }

        // The low band runs to slot 37, not 31: FFmpeg's sbr_x_gen fills X up to 38 here, and
        // Parametric Stereo's hybrid filterbank reads those slots. Synthesis ignores them.
        for (int k = 0; k < startSubband && k < 32; k++)
        {
            for (int i = split; i < SbrChannelState.OutputSlots; i++)
            {
                spectrum[0][i][k] = lowBand[k][i + SbrHighFrequency.EnvelopeAdjustmentOffset][0];
                spectrum[1][i][k] = lowBand[k][i + SbrHighFrequency.EnvelopeAdjustmentOffset][1];
            }
        }

        for (int k = startSubband; k < startSubband + subbandCount && k < 64; k++)
        {
            for (int i = split; i < SbrQmf.TimeSlots; i++)
            {
                spectrum[0][i][k] = currentAdjusted[i][k][0];
                spectrum[1][i][k] = currentAdjusted[i][k][1];
            }
        }
    }


    /// <summary>
    /// Decodes one SBR extension payload found inside an AAC fill element. Returns the number of
    /// bytes consumed, which the caller subtracts from the fill element's length.
    /// </summary>
    /// <param name="reader">Positioned just after the extension type field.</param>
    /// <param name="payloadBytes">Length the fill element declared.</param>
    /// <param name="hasCrc">True for EXT_SBR_DATA_CRC.</param>
    /// <param name="element">Which AAC element the payload belongs to.</param>
    public int DecodeExtension(ref SbrBitReader reader, int payloadBytes, bool hasCrc,
                               SbrElementType element)
    {
        int bits = 0;
        PayloadsSeen++;

        if (hasCrc)
        {
            reader.Skip(10); // bs_sbr_crc_bits - not checked
            bits += 10;
        }

        bits++;
        bool hasHeader = reader.ReadFlag(); // bs_header_flag
        if (hasHeader)
        {
            int before = reader.Position;
            SbrParser.ReadHeader(context, ref reader);
            bits += reader.Position - before;
        }

        // The band tables must be rebuilt before the data is read: the envelope loops are bounded
        // by the band counts those tables produce.
        if (context.NeedsReset)
        {
            context.NeedsReset = false;
            context.NeedsLimiterRebuild = false;   // the full rebuild covers it
            if (!SbrFrequencyTables.Build(context, tables))
                context.Disabled = true;
            else
                justReset = true;
        }
        else if (context.NeedsLimiterRebuild)
        {
            context.NeedsLimiterRebuild = false;
            if (!SbrFrequencyTables.MakeLimiterTable(context, tables))
                context.Disabled = true;
        }

        bool sawExtension = false;
        bool parsedData = context.Started && !context.Disabled;
        if (!parsedData)
            PayloadsBeforeHeader++;
        if (parsedData)
        {
            int before = reader.Position;
            SbrParser.ReadElementData(context, ref reader, element, out sawExtension);
            bits += reader.Position - before;
        }

        if (reader.IsInvalid)
        {
            context.Disabled = true;
            LastError = "SBR payload overran its declared length";
        }
        else if (context.Disabled)
        {
            LastError ??= "SBR payload rejected as invalid";
        }
        else
        {
            PayloadsParsed++;
        }

        // The payload is padded to a byte boundary; the 4 bits are the extension type the caller
        // already read.
        int alignBits = ((payloadBytes << 3) - 4 - bits) & 7;
        int bytesRead = (bits + alignBits + 4) >> 3;

        // Only a payload that was actually parsed can be expected to consume its declared length;
        // a skipped one consumes just the flag bits by design.
        if (parsedData)
        {
            if (bytesRead == payloadBytes)
                PayloadsExactLength++;
            else if (LengthMismatches.Count < MaxLengthMismatches)
                LengthMismatches.Add((PayloadsSeen, payloadBytes, bytesRead, parsedData));
        }

        // Reading past the declared length means the parse has gone wrong, and the C's answer is
        // not to carry on with a corrected count: it turns SBR off for the rest of the stream.
        // The caller's bitstream position is unaffected either way, because it advances by the
        // declared length and never by this figure.
        if (bytesRead > payloadBytes)
        {
            LastError = $"expected {payloadBytes} SBR bytes, read {bytesRead}";
            Turnoff();
        }

        return bytesRead;
    }
}

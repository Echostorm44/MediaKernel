using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>Which AAC element the SBR payload belongs to.</summary>
internal enum SbrElementType
{
    SingleChannel,
    ChannelPair,
}

/// <summary>
/// Parses the SBR extension payload (14496-3 4.5.2.8). Port of FFmpeg's read_sbr_* family in
/// aacsbr_template.c.
/// </summary>
/// <remarks>
/// Parsing is kept apart from reconstruction so it can be verified on its own: a correct parse
/// consumes exactly the payload the AAC element declared, which is a strong check that every field
/// was read in the right order and with the right width.
/// </remarks>
internal static class SbrParser
{
    /// <summary>Number of QMF time slots in a frame. 960-sample frames are not supported.</summary>
    private const int TimeSlots = 16;

    /// <summary>ceil(log2(n)) for the envelope counts a pointer field can span.</summary>
    private static ReadOnlySpan<byte> CeilLog2 => [0, 1, 2, 2, 3, 3];

    /// <summary>
    /// Reads the per-element SBR data that follows the optional header. Returns false when the
    /// payload describes something the spec forbids, in which case SBR is turned off.
    /// </summary>
    public static bool ReadElementData(SbrContext sbr, ref SbrBitReader reader, SbrElementType element,
                                       out bool sawExtension)
    {
        sawExtension = false;
        bool ok = element switch
        {
            SbrElementType.SingleChannel => ReadSingleChannelElement(sbr, ref reader),
            SbrElementType.ChannelPair => ReadChannelPairElement(sbr, ref reader),
            _ => false,
        };

        if (!ok || reader.IsInvalid)
        {
            sbr.Disabled = true;
            return false;
        }

        if (reader.ReadFlag()) // bs_extended_data
        {
            sawExtension = true;
            int bitsLeft = (int)reader.ReadBits(4); // bs_extension_size
            if (bitsLeft == 15)
                bitsLeft += (int)reader.ReadBits(8); // bs_esc_count

            bitsLeft <<= 3;
            while (bitsLeft > 7)
            {
                bitsLeft -= 2;
                ReadExtension(sbr, ref reader, (int)reader.ReadBits(2), ref bitsLeft);
            }

            if (bitsLeft > 0)
                reader.Skip(bitsLeft);
        }

        return true;
    }

    /// <summary>Reads sbr_header. A change to the band layout forces the derived tables to be rebuilt.</summary>
    public static void ReadHeader(SbrContext sbr, ref SbrBitReader reader)
    {
        var previous = sbr.SpectrumParameters;
        uint previousLimiterBands = sbr.LimiterBands;

        sbr.Started = true;
        sbr.AmplitudeResolutionHeader = reader.ReadFlag();

        sbr.SpectrumParameters.StartFrequency = (byte)reader.ReadBits(4);
        sbr.SpectrumParameters.StopFrequency = (byte)reader.ReadBits(4);
        sbr.SpectrumParameters.CrossoverBand = (byte)reader.ReadBits(3);
        reader.Skip(2); // bs_reserved

        bool headerExtra1 = reader.ReadFlag();
        bool headerExtra2 = reader.ReadFlag();

        if (headerExtra1)
        {
            sbr.SpectrumParameters.FrequencyScale = (byte)reader.ReadBits(2);
            sbr.SpectrumParameters.AlterScale = (byte)reader.ReadBit();
            sbr.SpectrumParameters.NoiseBands = (byte)reader.ReadBits(2);
        }
        else
        {
            sbr.SpectrumParameters.FrequencyScale = 2;
            sbr.SpectrumParameters.AlterScale = 1;
            sbr.SpectrumParameters.NoiseBands = 2;
        }

        if (!sbr.SpectrumParameters.Equals(previous))
            sbr.NeedsReset = true;

        if (headerExtra2)
        {
            sbr.LimiterBands = reader.ReadBits(2);
            sbr.LimiterGains = reader.ReadBits(2);
            sbr.InterpolationFrequency = reader.ReadBit();
            sbr.SmoothingMode = reader.ReadBit();
        }
        else
        {
            sbr.LimiterBands = 2;
            sbr.LimiterGains = 2;
            sbr.InterpolationFrequency = 1;
            sbr.SmoothingMode = 1;
        }

        // A changed limiter-band count rebuilds the limiter table ALONE. It used to set
        // NeedsReset, which is a different thing: a reset rebuilds every derived table and, more
        // to the point, makes sbr_hf_assemble prime its smoothing history from this frame's own
        // gains instead of carrying the previous frame's over. The C is explicit about the
        // distinction - "if (sbr->bs_limiter_bands != old_bs_limiter_bands && !sbr->reset)
        // sbr_make_f_tablelim(sbr);".
        //
        // This path is dead on the whole AAC conformance corpus, and that was measured rather
        // than assumed: across the 184 sbr_headers in the 28 FATE streams, every single
        // limiter-band change arrives together with a spectrum-parameter change, so the
        // !NeedsReset guard excludes it every time and the conformance numbers do not move by a
        // digit either way. It is here because it is what the C does, not because a gate covers
        // it - no stream in the corpus can tell the two apart.
        if (sbr.LimiterBands != previousLimiterBands && !sbr.NeedsReset)
            sbr.NeedsLimiterRebuild = true;
    }

    private static bool ReadSingleChannelElement(SbrContext sbr, ref SbrBitReader reader)
    {
        if (reader.ReadFlag()) // bs_data_extra
            reader.Skip(4); // bs_reserved

        var channel = sbr.Channels[0];
        if (!ReadGrid(sbr, ref reader, channel))
            return false;

        ReadDeltaTimeFlags(ref reader, channel);
        ReadInverseFilter(sbr, ref reader, channel);
        if (!ReadEnvelope(sbr, ref reader, channel, channelIndex: 0))
            return false;
        if (!ReadNoise(sbr, ref reader, channel, channelIndex: 0))
            return false;

        channel.AddHarmonicFlag = reader.ReadFlag();
        if (channel.AddHarmonicFlag)
            reader.ReadBitVector(channel.AddHarmonic, sbr.BandCount[1]);

        return true;
    }

    private static bool ReadChannelPairElement(SbrContext sbr, ref SbrBitReader reader)
    {
        if (reader.ReadFlag()) // bs_data_extra
            reader.Skip(8); // bs_reserved

        var left = sbr.Channels[0];
        var right = sbr.Channels[1];

        sbr.Coupling = reader.ReadFlag();
        if (sbr.Coupling)
        {
            // One grid drives both channels; the second channel's envelopes are a balance.
            if (!ReadGrid(sbr, ref reader, left))
                return false;
            CopyGrid(right, left);

            ReadDeltaTimeFlags(ref reader, left);
            ReadDeltaTimeFlags(ref reader, right);
            ReadInverseFilter(sbr, ref reader, left);

            // Coupling transmits one set of inverse-filter modes, for the left channel, and the
            // right channel adopts it - but it keeps its OWN previous frame's mode as history:
            //   memcpy(data[1].bs_invf_mode[1], data[1].bs_invf_mode[0], ...);
            //   memcpy(data[1].bs_invf_mode[0], data[0].bs_invf_mode[0], ...);
            // The order matters and the source of the first copy matters. Copying the left
            // channel's current mode into the right channel's history instead makes
            // mode[0] + mode[1] always even for the right channel, so sbr_chirp's
            // "exactly one of this frame and the last asked for inverse filtering" case - the one
            // that targets 0.6 rather than the table value - can never fire on it.
            right.InverseFilterMode[0].AsSpan().CopyTo(right.InverseFilterMode[1]);
            left.InverseFilterMode[0].AsSpan().CopyTo(right.InverseFilterMode[0]);

            if (!ReadEnvelope(sbr, ref reader, left, 0)) return false;
            if (!ReadNoise(sbr, ref reader, left, 0)) return false;
            if (!ReadEnvelope(sbr, ref reader, right, 1)) return false;
            if (!ReadNoise(sbr, ref reader, right, 1)) return false;
        }
        else
        {
            if (!ReadGrid(sbr, ref reader, left)) return false;
            if (!ReadGrid(sbr, ref reader, right)) return false;

            ReadDeltaTimeFlags(ref reader, left);
            ReadDeltaTimeFlags(ref reader, right);
            ReadInverseFilter(sbr, ref reader, left);
            ReadInverseFilter(sbr, ref reader, right);

            if (!ReadEnvelope(sbr, ref reader, left, 0)) return false;
            if (!ReadEnvelope(sbr, ref reader, right, 1)) return false;
            if (!ReadNoise(sbr, ref reader, left, 0)) return false;
            if (!ReadNoise(sbr, ref reader, right, 1)) return false;
        }

        left.AddHarmonicFlag = reader.ReadFlag();
        if (left.AddHarmonicFlag)
            reader.ReadBitVector(left.AddHarmonic, sbr.BandCount[1]);

        right.AddHarmonicFlag = reader.ReadFlag();
        if (right.AddHarmonicFlag)
            reader.ReadBitVector(right.AddHarmonic, sbr.BandCount[1]);

        return true;
    }

    /// <summary>
    /// Reads the time grid: how many envelopes the frame has and where their borders fall.
    /// </summary>
    private static bool ReadGrid(SbrContext sbr, ref SbrBitReader reader, SbrChannelData channel)
    {
        int pointer = 0;
        int trailingBorder = TimeSlots;
        int leadCount = 0, trailCount = 0;
        int previousEnvelopeCount = channel.EnvelopeCount;

        // These three carry over from the previous frame before anything is overwritten.
        channel.FrequencyResolution[0] = channel.FrequencyResolution[channel.EnvelopeCount];
        channel.AmplitudeResolution = sbr.AmplitudeResolutionHeader;
        channel.PreviousFinalBorder = channel.EnvelopeBorders[previousEnvelopeCount];

        var frameClass = (SbrFrameClass)reader.ReadBits(2);
        switch (frameClass)
        {
            case SbrFrameClass.FixFix:
            {
                int count = 1 << (int)reader.ReadBits(2);
                if (count > 5)
                    return false;

                channel.EnvelopeCount = count;
                leadCount = count - 1;
                if (count == 1)
                    channel.AmplitudeResolution = false;

                channel.EnvelopeBorders[0] = 0;
                channel.EnvelopeBorders[count] = (byte)trailingBorder;

                int step = (trailingBorder + (count >> 1)) / count;
                for (int i = 0; i < leadCount; i++)
                    channel.EnvelopeBorders[i + 1] = (byte)(channel.EnvelopeBorders[i] + step);

                channel.FrequencyResolution[1] = (byte)reader.ReadBit();
                for (int i = 1; i < count; i++)
                    channel.FrequencyResolution[i + 1] = channel.FrequencyResolution[1];
                break;
            }

            case SbrFrameClass.FixVar:
            {
                trailingBorder += (int)reader.ReadBits(2);
                trailCount = (int)reader.ReadBits(2);
                channel.EnvelopeCount = trailCount + 1;
                channel.EnvelopeBorders[0] = 0;
                channel.EnvelopeBorders[channel.EnvelopeCount] = (byte)trailingBorder;

                for (int i = 0; i < trailCount; i++)
                    channel.EnvelopeBorders[channel.EnvelopeCount - 1 - i] =
                        (byte)(channel.EnvelopeBorders[channel.EnvelopeCount - i] - 2 * (int)reader.ReadBits(2) - 2);

                pointer = (int)reader.ReadBits(CeilLog2[channel.EnvelopeCount]);

                for (int i = 0; i < channel.EnvelopeCount; i++)
                    channel.FrequencyResolution[channel.EnvelopeCount - i] = (byte)reader.ReadBit();
                break;
            }

            case SbrFrameClass.VarFix:
            {
                channel.EnvelopeBorders[0] = (byte)reader.ReadBits(2);
                leadCount = (int)reader.ReadBits(2);
                channel.EnvelopeCount = leadCount + 1;
                channel.EnvelopeBorders[channel.EnvelopeCount] = (byte)trailingBorder;

                for (int i = 0; i < leadCount; i++)
                    channel.EnvelopeBorders[i + 1] =
                        (byte)(channel.EnvelopeBorders[i] + 2 * (int)reader.ReadBits(2) + 2);

                pointer = (int)reader.ReadBits(CeilLog2[channel.EnvelopeCount]);
                reader.ReadBitVector(channel.FrequencyResolution.AsSpan(1), channel.EnvelopeCount);
                break;
            }

            case SbrFrameClass.VarVar:
            {
                channel.EnvelopeBorders[0] = (byte)reader.ReadBits(2);
                trailingBorder += (int)reader.ReadBits(2);
                leadCount = (int)reader.ReadBits(2);
                trailCount = (int)reader.ReadBits(2);

                int count = leadCount + trailCount + 1;
                if (count > 5)
                    return false;

                channel.EnvelopeCount = count;
                channel.EnvelopeBorders[count] = (byte)trailingBorder;

                for (int i = 0; i < leadCount; i++)
                    channel.EnvelopeBorders[i + 1] =
                        (byte)(channel.EnvelopeBorders[i] + 2 * (int)reader.ReadBits(2) + 2);
                for (int i = 0; i < trailCount; i++)
                    channel.EnvelopeBorders[count - 1 - i] =
                        (byte)(channel.EnvelopeBorders[count - i] - 2 * (int)reader.ReadBits(2) - 2);

                pointer = (int)reader.ReadBits(CeilLog2[count]);
                reader.ReadBitVector(channel.FrequencyResolution.AsSpan(1), count);
                break;
            }
        }

        channel.FrameClass = frameClass;

        if (pointer > channel.EnvelopeCount + 1)
            return false;

        // Borders must strictly increase; anything else would give an envelope zero or negative
        // length and index outside the time grid later.
        for (int i = 1; i <= channel.EnvelopeCount; i++)
        {
            if (channel.EnvelopeBorders[i - 1] >= channel.EnvelopeBorders[i])
                return false;
        }

        channel.NoiseFloorCount = (channel.EnvelopeCount > 1) ? 2 : 1;
        channel.NoiseBorders[0] = channel.EnvelopeBorders[0];
        channel.NoiseBorders[channel.NoiseFloorCount] = channel.EnvelopeBorders[channel.EnvelopeCount];

        if (channel.NoiseFloorCount > 1)
        {
            int index;
            if (frameClass == SbrFrameClass.FixFix)
            {
                index = channel.EnvelopeCount >> 1;
            }
            else if (((int)frameClass & 1) != 0) // FixVar or VarVar
            {
                index = channel.EnvelopeCount - Math.Max(pointer - 1, 1);
            }
            else // VarFix
            {
                index = pointer switch
                {
                    0 => 1,
                    1 => channel.EnvelopeCount - 1,
                    _ => pointer - 1,
                };
            }
            channel.NoiseBorders[1] = channel.EnvelopeBorders[index];
        }

        // l_APrev and l_A mark the envelope where a sine may start, which must not be smoothed.
        channel.SineStartEnvelope[0] = channel.SineStartEnvelope[1] != previousEnvelopeCount ? -1 : 0;
        channel.SineStartEnvelope[1] = -1;
        if (((int)frameClass & 1) != 0 && pointer != 0)
            channel.SineStartEnvelope[1] = channel.EnvelopeCount + 1 - pointer;
        else if (frameClass == SbrFrameClass.VarFix && pointer > 1)
            channel.SineStartEnvelope[1] = pointer - 1;

        return true;
    }

    /// <summary>Mirrors FFmpeg's copy_sbr_grid: the coupled channel shares the grid but keeps its own history.</summary>
    private static void CopyGrid(SbrChannelData destination, SbrChannelData source)
    {
        destination.FrequencyResolution[0] = destination.FrequencyResolution[destination.EnvelopeCount];
        destination.PreviousFinalBorder = destination.EnvelopeBorders[destination.EnvelopeCount];
        destination.SineStartEnvelope[0] =
            destination.SineStartEnvelope[1] != destination.EnvelopeCount ? -1 : 0;

        destination.FrameClass = source.FrameClass;
        destination.EnvelopeCount = source.EnvelopeCount;
        destination.NoiseFloorCount = source.NoiseFloorCount;
        destination.AmplitudeResolution = source.AmplitudeResolution;
        destination.SineStartEnvelope[1] = source.SineStartEnvelope[1];

        source.EnvelopeBorders.AsSpan().CopyTo(destination.EnvelopeBorders);
        source.NoiseBorders.AsSpan().CopyTo(destination.NoiseBorders);
        source.FrequencyResolution.AsSpan(1).CopyTo(destination.FrequencyResolution.AsSpan(1));
    }

    private static void ReadDeltaTimeFlags(ref SbrBitReader reader, SbrChannelData channel)
    {
        reader.ReadBitVector(channel.DeltaTimeEnvelope, channel.EnvelopeCount);
        reader.ReadBitVector(channel.DeltaTimeNoise, channel.NoiseFloorCount);
    }

    private static void ReadInverseFilter(SbrContext sbr, ref SbrBitReader reader, SbrChannelData channel)
    {
        channel.InverseFilterMode[0].AsSpan().CopyTo(channel.InverseFilterMode[1]);
        for (int i = 0; i < sbr.NoiseBandCount; i++)
            channel.InverseFilterMode[0][i] = (byte)reader.ReadBits(2);
    }

    /// <summary>
    /// Reads envelope scalefactors, delta-coded either across time (against the previous envelope)
    /// or across frequency (against the previous band).
    /// </summary>
    private static bool ReadEnvelope(SbrContext sbr, ref SbrBitReader reader,
                                     SbrChannelData channel, int channelIndex)
    {
        int delta = (channelIndex == 1 && sbr.Coupling) ? 2 : 1;
        int odd = sbr.BandCount[1] & 1;

        SbrHuffmanTable timeTable, frequencyTable;
        int startBits;

        if (sbr.Coupling && channelIndex == 1)
        {
            if (channel.AmplitudeResolution)
            {
                startBits = 5;
                timeTable = SbrHuffmanTables.TimeEnvBalance30dB;
                frequencyTable = SbrHuffmanTables.FreqEnvBalance30dB;
            }
            else
            {
                startBits = 6;
                timeTable = SbrHuffmanTables.TimeEnvBalance15dB;
                frequencyTable = SbrHuffmanTables.FreqEnvBalance15dB;
            }
        }
        else
        {
            if (channel.AmplitudeResolution)
            {
                startBits = 6;
                timeTable = SbrHuffmanTables.TimeEnv30dB;
                frequencyTable = SbrHuffmanTables.FreqEnv30dB;
            }
            else
            {
                startBits = 7;
                timeTable = SbrHuffmanTables.TimeEnv15dB;
                frequencyTable = SbrHuffmanTables.FreqEnv15dB;
            }
        }

        for (int i = 0; i < channel.EnvelopeCount; i++)
        {
            var current = channel.EnvelopeScalefactors[i + 1];
            var previous = channel.EnvelopeScalefactors[i];
            int bands = sbr.BandCount[channel.FrequencyResolution[i + 1]];

            if (channel.DeltaTimeEnvelope[i] != 0)
            {
                // Delta against the same band of the previous envelope. When the resolution
                // changes between envelopes the bands do not line up, so map the index across.
                if (channel.FrequencyResolution[i + 1] == channel.FrequencyResolution[i])
                {
                    for (int j = 0; j < bands; j++)
                    {
                        int value = previous[j] + delta * timeTable.Decode(ref reader);
                        if ((uint)value > 127) return false;
                        current[j] = (byte)value;
                    }
                }
                else if (channel.FrequencyResolution[i + 1] != 0)
                {
                    for (int j = 0; j < bands; j++)
                    {
                        int k = (j + odd) >> 1;
                        int value = previous[k] + delta * timeTable.Decode(ref reader);
                        if ((uint)value > 127) return false;
                        current[j] = (byte)value;
                    }
                }
                else
                {
                    for (int j = 0; j < bands; j++)
                    {
                        int k = j != 0 ? 2 * j - odd : 0;
                        int value = previous[k] + delta * timeTable.Decode(ref reader);
                        if ((uint)value > 127) return false;
                        current[j] = (byte)value;
                    }
                }
            }
            else
            {
                current[0] = (byte)(delta * (int)reader.ReadBits(startBits));
                for (int j = 1; j < bands; j++)
                {
                    int value = current[j - 1] + delta * frequencyTable.Decode(ref reader);
                    if ((uint)value > 127) return false;
                    current[j] = (byte)value;
                }
            }
        }

        // The last envelope becomes the reference for the next frame's delta-in-time coding.
        channel.EnvelopeScalefactors[channel.EnvelopeCount].AsSpan()
            .CopyTo(channel.EnvelopeScalefactors[0]);

        return !reader.IsInvalid;
    }

    private static bool ReadNoise(SbrContext sbr, ref SbrBitReader reader,
                                  SbrChannelData channel, int channelIndex)
    {
        int delta = (channelIndex == 1 && sbr.Coupling) ? 2 : 1;

        SbrHuffmanTable timeTable, frequencyTable;
        if (sbr.Coupling && channelIndex == 1)
        {
            timeTable = SbrHuffmanTables.TimeNoiseBalance30dB;
            frequencyTable = SbrHuffmanTables.FreqEnvBalance30dB;
        }
        else
        {
            timeTable = SbrHuffmanTables.TimeNoise30dB;
            frequencyTable = SbrHuffmanTables.FreqEnv30dB;
        }

        for (int i = 0; i < channel.NoiseFloorCount; i++)
        {
            var current = channel.NoiseScalefactors[i + 1];
            var previous = channel.NoiseScalefactors[i];

            if (channel.DeltaTimeNoise[i] != 0)
            {
                for (int j = 0; j < sbr.NoiseBandCount; j++)
                {
                    int value = previous[j] + delta * timeTable.Decode(ref reader);
                    if ((uint)value > 30) return false;
                    current[j] = (byte)value;
                }
            }
            else
            {
                current[0] = (byte)(delta * (int)reader.ReadBits(5));
                for (int j = 1; j < sbr.NoiseBandCount; j++)
                {
                    int value = current[j - 1] + delta * frequencyTable.Decode(ref reader);
                    if ((uint)value > 30) return false;
                    current[j] = (byte)value;
                }
            }
        }

        channel.NoiseScalefactors[channel.NoiseFloorCount].AsSpan()
            .CopyTo(channel.NoiseScalefactors[0]);

        return !reader.IsInvalid;
    }

    /// <summary>The extension id carrying Parametric Stereo, per 14496-3 Table 4.63.</summary>
    private const int ParametricStereoExtension = 2;

    /// <summary>
    /// <c>read_sbr_extension</c>. Parametric Stereo is the only extension defined; anything else
    /// is padding or reserved, and its bits are consumed so the payload length still adds up.
    /// </summary>
    private static void ReadExtension(SbrContext sbr, ref SbrBitReader reader, int extensionId,
                                      ref int bitsLeft)
    {
        sbr.ExtensionsSeen |= 1u << (extensionId & 3);

        if (extensionId == ParametricStereoExtension)
        {
            sbr.SawParametricStereo = true;

            // PsMode 0 means the configuration said PS is absent. FFmpeg logs and skips rather
            // than decoding a tool the configuration denied, because the output channel count was
            // already fixed from that configuration.
            if (sbr.PsMode == 0)
            {
                reader.Skip(bitsLeft);
                bitsLeft = 0;
                return;
            }

            bitsLeft -= Ps.PsParser.ReadData(ref reader, sbr.Ps.Common, bitsLeft);
            return;
        }

        reader.Skip(bitsLeft);
        bitsLeft = 0;
    }
}

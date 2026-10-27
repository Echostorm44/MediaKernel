// Licensed under the MIT License.
// Based on RFC 6716 (Opus Audio Codec).

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Complete Opus audio decoder.
/// Supports all three modes: SILK-only (speech), CELT-only (music), and Hybrid.
/// </summary>
public class OpusDecoder
{
    /// <summary>Opus output sample rate (always 48kHz).</summary>
    public const int OutputSampleRate = 48000;
    
    /// <summary>Maximum frame duration in samples (60ms at 48kHz).</summary>
    public const int MaxFrameSamples = 2880;
    
    /// <summary>Maximum channels.</summary>
    public const int MaxChannels = 2;
    
    private readonly CeltDecoder celtDecoder;
    private readonly SilkDecoder silkDecoder;
    private readonly SilkResampler[] silkResamplers;  // per-channel resamplers
    private readonly float[] outputBuffer;
    private readonly float[] silkBuffer;
    private readonly float[] celtBuffer;
    private readonly float[] resampleBuffer;
    
    // Per-channel output arrays for CeltDecoder interface
    private readonly float[][] celtOutputChannels;
    
    // Per-channel output arrays for SilkDecoder
    private readonly float[][] silkOutputChannels;
    
    // Per-channel temp buffers for SILK resampled output in hybrid mode
    private readonly float[][] silkResampledChannels;
    
    // CELT delay FIFO for hybrid mode — buffers excess CELT samples when
    // SILK resampler produces fewer than frame_duration samples (filter startup).
    // Matches FFmpeg's s->celt_delay (av_audio_fifo).
    private readonly float[][] celtDelayFifo;
    private int celtDelayCount;
    
    private readonly int channelCount;
    private OpusHeader header;
    private bool isInitialized;
    private int preskipRemaining;
    private int silkSampleRate;  // current SILK sample rate (for resampler init)
    private int delayedSamples;  // samples buffered in resampler but not yet output
    private OpusMode previousMode;  // for cross-mode flushing on mode switch
    
    // Redundancy CELT frame support (RFC 6716 Section 4.2 — transition smoothing)
    // When a SILK packet has remaining bits, FFmpeg unconditionally decodes a
    // redundancy CELT frame for crossfading during mode transitions.
    private readonly float[][] redundancyOutput;  // [2][240] per-channel redundancy CELT output
    private int redundancyIdx;  // >0 means pending crossfade from previous frame
    
    // silk_frame_duration_ms[config] — from FFmpeg dec.c
    private static ReadOnlySpan<int> SilkFrameDurationMs => [
        10, 20, 40, 60,  // SILK NB
        10, 20, 40, 60,  // SILK MB
        10, 20, 40, 60,  // SILK WB
        10, 20,           // Hybrid SWB
        10, 20            // Hybrid FB
    ];
    
    // silk_resample_delay[bandwidth] — initial silence for resampler priming
    private static ReadOnlySpan<int> SilkResampleDelay => [4, 8, 11, 11, 11];
    
    /// <summary>
    /// Gets the output sample rate (always 48kHz).
    /// </summary>
    public int SampleRate => OutputSampleRate;
    
    /// <summary>
    /// Gets the number of output channels.
    /// </summary>
    public int ChannelCount => channelCount;
    
    /// <summary>
    /// Gets the output gain as a linear multiplier.
    /// </summary>
    public float OutputGain => header.OutputGainLinear;
    
    /// <summary>
    /// Creates a new Opus decoder.
    /// </summary>
    /// <param name="channelCount">Number of output channels (1 or 2).</param>
    public OpusDecoder(int channelCount = 2)
    {
        if (channelCount < 1 || channelCount > MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        
        this.channelCount = channelCount;
        
        // Create decoder states
        celtDecoder = new CeltDecoder(channelCount, applyPhaseInv: true);
        silkDecoder = new SilkDecoder();
        silkResamplers = new SilkResampler[channelCount];
        for (int i = 0; i < channelCount; i++)
            silkResamplers[i] = new SilkResampler();
        
        // Allocate buffers
        outputBuffer = new float[MaxFrameSamples * channelCount];
        silkBuffer = new float[MaxFrameSamples * channelCount];
        celtBuffer = new float[MaxFrameSamples * channelCount];
        resampleBuffer = new float[MaxFrameSamples * channelCount];
        
        // Per-channel output arrays for CELT
        celtOutputChannels = new float[channelCount][];
        for (int i = 0; i < channelCount; i++)
            celtOutputChannels[i] = new float[MaxFrameSamples];
        
        // Per-channel output arrays for SILK (at SILK sample rate, max 960 per channel)
        silkOutputChannels = new float[2][];
        silkOutputChannels[0] = new float[960];
        silkOutputChannels[1] = new float[960];
        
        // Per-channel resampled SILK output (at 48kHz) for hybrid mixing
        silkResampledChannels = new float[channelCount][];
        for (int i = 0; i < channelCount; i++)
            silkResampledChannels[i] = new float[MaxFrameSamples];
        
        // CELT delay FIFO for hybrid mode
        celtDelayFifo = new float[channelCount][];
        for (int i = 0; i < channelCount; i++)
            celtDelayFifo[i] = new float[MaxFrameSamples];
        celtDelayCount = 0;
        
        // Redundancy CELT output buffers (always 2 channels, 240 samples)
        redundancyOutput = new float[2][];
        redundancyOutput[0] = new float[240];
        redundancyOutput[1] = new float[240];
    }
    
    /// <summary>
    /// Initializes the decoder with an Opus header.
    /// </summary>
    /// <param name="headerData">Raw OpusHead data.</param>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize(ReadOnlySpan<byte> headerData)
    {
        if (!OpusPacketParser.TryParseHeader(headerData, out header))
            return false;
        
        preskipRemaining = header.PreSkip;
        isInitialized = true;
        
        return true;
    }
    
    /// <summary>
    /// Decodes an Opus packet to PCM samples.
    /// </summary>
    /// <param name="packet">Opus packet data.</param>
    /// <param name="output">Output buffer for PCM samples.</param>
    /// <param name="samplesDecoded">Number of samples decoded per channel.</param>
    /// <returns>True if decoding succeeded.</returns>
    public bool DecodePacket(ReadOnlySpan<byte> packet, Span<float> output, out int samplesDecoded)
    {
        samplesDecoded = 0;
        
        if (packet.Length == 0)
        {
            // Packet loss: apply PLC (Packet Loss Concealment)
            return DecodePlc(output, out samplesDecoded);
        }
        
        // Parse packet structure
        if (!OpusPacketParser.TryParsePacket(packet, out OpusPacketInfo packetInfo))
            return false;
        
        int outputIndex = 0;
        int payloadOffset = packetInfo.PayloadOffset;
        
        // Flush resampler when switching to CELT or changing SILK sample rate.
        // Matching FFmpeg's opus_decode_subpacket (dec.c:406-446):
        // When the resampler is initialized (silkSampleRate > 0) and the new mode
        // is CELT-only, or the SILK sample rate changes, flush delayed samples and
        // close the resampler before decoding any frames.
        // FFmpeg checks this regardless of delayedSamples — even if 0, it still
        // closes the resampler so the next SILK frame re-initializes fresh.
        if (silkSampleRate > 0)
        {
            bool needFlush = false;
            
            if (packetInfo.Toc.Mode == OpusMode.CeltOnly)
            {
                needFlush = true;
            }
            else if (packetInfo.Toc.Mode == OpusMode.SilkOnly || packetInfo.Toc.Mode == OpusMode.Hybrid)
            {
                // Check if SILK sample rate is changing
                int newRate = packetInfo.Toc.Mode == OpusMode.Hybrid ? 16000 :
                    Math.Min((int)packetInfo.Toc.Bandwidth, 2) switch
                    {
                        0 => 8000,
                        1 => 12000,
                        _ => 16000
                    };
                if (newRate != silkSampleRate)
                    needFlush = true;
            }
            
            if (needFlush)
            {
                if (delayedSamples > 0)
                {
                    int flushed = FlushResamplerInternal(output.Slice(outputIndex));
                    if (flushed > 0)
                    {
                        outputIndex += flushed * channelCount;
                        samplesDecoded += flushed;
                    }
                }
                else
                {
                    // No delayed samples, but still close the resampler (matching swr_close)
                    foreach (var r in silkResamplers)
                        r.Reset();
                    silkSampleRate = 0;
                    celtDelayCount = 0;
                }
            }
        }
        
        // Decode each frame in the packet
        for (int frame = 0; frame < packetInfo.FrameCount; frame++)
        {
            int frameSize = packetInfo.FrameSizes[frame];
            ReadOnlySpan<byte> frameData = packet.Slice(payloadOffset, frameSize);
            payloadOffset += frameSize;
            
            int frameSamples = packetInfo.Toc.FrameSamples;
            
            if (!DecodeFrame(frameData, packetInfo.Toc, frameSamples, out int decodedSamples))
                return false;
            
            // Copy to output
            int samplesToOutput = decodedSamples * channelCount;
            if (outputIndex + samplesToOutput <= output.Length)
            {
                outputBuffer.AsSpan(0, samplesToOutput).CopyTo(output.Slice(outputIndex));
                outputIndex += samplesToOutput;
            }
            
            samplesDecoded += decodedSamples;
        }
        
        // Handle preskip (initial samples to discard)
        if (preskipRemaining > 0)
        {
            int samplesToSkip = Math.Min(preskipRemaining, samplesDecoded);
            int skipAmount = samplesToSkip * channelCount;
            
            if (skipAmount < outputIndex)
            {
                // Shift remaining samples to beginning
                output.Slice(skipAmount, outputIndex - skipAmount).CopyTo(output);
            }
            
            samplesDecoded -= samplesToSkip;
            preskipRemaining -= samplesToSkip;
        }
        
        // Apply output gain
        if (header.OutputGain != 0)
        {
            float gain = OutputGain;
            for (int i = 0; i < samplesDecoded * channelCount; i++)
            {
                output[i] *= gain;
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Decodes a single frame. Handles cross-mode flushing when the mode changes.
    /// FFmpeg flushes SILK when switching to CELT-only, and flushes CELT when
    /// switching to SILK-only, matching opus_decode_frame in dec.c.
    /// </summary>
    private bool DecodeFrame(
        ReadOnlySpan<byte> frameData,
        OpusToc toc,
        int frameSamples,
        out int samplesDecoded)
    {
        samplesDecoded = 0;
        
        if (frameData.Length == 0)
        {
            // Zero-length frame = DTX (discontinuous transmission)
            Array.Clear(outputBuffer, 0, frameSamples * channelCount);
            samplesDecoded = frameSamples;
            return true;
        }
        
        var rangeDecoder = new OpusRangeDecoder(frameData);
        
        // Cross-mode flushing (matching FFmpeg's dec.c behavior):
        // When not decoding SILK/Hybrid, flush SILK state.
        // When not decoding CELT/Hybrid, flush CELT state.
        OpusMode currentMode = toc.Mode;
        
        if (currentMode != OpusMode.SilkOnly && currentMode != OpusMode.Hybrid)
        {
            // Not using SILK this frame — flush SILK decoder state
            if (previousMode == OpusMode.SilkOnly || previousMode == OpusMode.Hybrid)
                silkDecoder.Flush();
        }
        
        if (currentMode != OpusMode.CeltOnly && currentMode != OpusMode.Hybrid)
        {
            // Not using CELT this frame — flush CELT decoder state
            if (previousMode == OpusMode.CeltOnly || previousMode == OpusMode.Hybrid)
                celtDecoder.Flush();
        }
        
        previousMode = currentMode;
        
        bool success;
        switch (currentMode)
        {
            case OpusMode.SilkOnly:
                success = DecodeSilkFrame(ref rangeDecoder, frameData, toc, frameSamples, out samplesDecoded);
                break;
                
            case OpusMode.CeltOnly:
                success = DecodeCeltFrame(ref rangeDecoder, toc, frameSamples, out samplesDecoded);
                break;
                
            case OpusMode.Hybrid:
                success = DecodeHybridFrame(ref rangeDecoder, frameData, toc, frameSamples, out samplesDecoded);
                break;
                
            default:
                return false;
        }
        
        if (!success)
            return false;
        
        return true;
    }
    
    /// <summary>
    /// Decodes a SILK-only frame, including optional redundancy CELT crossfade.
    /// Matches FFmpeg dec.c opus_decode_frame for SILK-only mode.
    /// </summary>
    private bool DecodeSilkFrame(
        ref OpusRangeDecoder rangeDecoder,
        ReadOnlySpan<byte> frameData,
        OpusToc toc,
        int frameSamples,
        out int samplesDecoded)
    {
        samplesDecoded = 0;
        int prevDelayedSamples = delayedSamples;
        
        int config = toc.Configuration;
        int durationMs = SilkFrameDurationMs[config];
        int silkBandwidth = Math.Min((int)toc.Bandwidth, 2);  // SILK max is WB=2
        int codedChannels = toc.IsStereo ? 2 : 1;
        
        // Initialize resampler if needed
        int sampleRate = silkBandwidth switch
        {
            0 => 8000,   // NB
            1 => 12000,  // MB
            _ => 16000   // WB
        };
        
        if (sampleRate != silkSampleRate)
        {
            silkSampleRate = sampleRate;
            for (int ch = 0; ch < channelCount; ch++)
            {
                silkResamplers[ch].Init(sampleRate);
                silkResamplers[ch].FeedSilence(SilkResampleDelay[(int)toc.Bandwidth]);
            }
            delayedSamples = 0;
            prevDelayedSamples = 0;
        }
        
        // Configure SILK output channels
        silkDecoder.OutputChannels = Math.Min(codedChannels, channelCount);
        
        // Decode SILK superframe
        int silkSamples = silkDecoder.DecodeSuperframe(
            ref rangeDecoder, silkOutputChannels,
            silkBandwidth, codedChannels, durationMs);
        
        if (silkSamples < 0)
            return false;
        
        // Check for redundancy CELT frame (FFmpeg dec.c:281-282)
        // For SILK-only mode, if ≥17 bits remain after SILK decode, a redundancy frame exists
        uint consumed = rangeDecoder.Tell();
        int totalBits = rangeDecoder.DataLength * 8;
        int redundancy = 0;
        int redundancyPos = 0;
        int redundancySize = 0;
        
        if (consumed + 17 <= (uint)totalBits)
        {
            redundancy = 1;
            redundancyPos = (int)rangeDecoder.DecLog(1);
            redundancySize = rangeDecoder.DataLength - ((int)consumed + 7) / 8;
        }
        
        // Pre-redundancy: decode CELT redundancy frame before main output (FFmpeg dec.c:297-302)
        if (redundancy != 0 && redundancyPos != 0)
        {
            DecodeRedundancyCelt(frameData, redundancySize, toc);
            celtDecoder.Flush();
        }
        
        // Resample from SILK rate to 48kHz
        int actualSamples = 0;
        for (int ch = 0; ch < channelCount; ch++)
        {
            int srcCh = Math.Min(ch, codedChannels - 1);
            int resampled = silkResamplers[ch].Resample(
                silkOutputChannels[srcCh].AsSpan(0, silkSamples),
                silkSamples,
                celtOutputChannels[ch].AsSpan(0, frameSamples),
                frameSamples);
            if (ch == 0)
                actualSamples = resampled;
        }
        
        // Track delayed samples (matching FFmpeg's s->delayed_samples)
        delayedSamples += frameSamples - actualSamples;
        
        // Apply leftover redundancy crossfade from PREVIOUS frame (FFmpeg dec.c:360-365)
        // Must happen AFTER resample output is ready but BEFORE any new post-redundancy
        // decode overwrites redundancyOutput. In FFmpeg this runs between the CELT flush
        // (line 358) and the post-redundancy decode (line 370).
        if (redundancyIdx > 0)
        {
            int fadeLen = 120 - redundancyIdx;
            var leftoverWindow = CeltTables.Window2;
            for (int ch = 0; ch < channelCount; ch++)
            {
                for (int i = 0; i < fadeLen; i++)
                {
                    float w = leftoverWindow[redundancyIdx + i];
                    celtOutputChannels[ch][i] =
                        redundancyOutput[ch][120 + redundancyIdx + i] * w
                      + celtOutputChannels[ch][i] * (1.0f - w);
                }
            }
            redundancyIdx = 0;
        }
        
        // Apply redundancy crossfade on per-channel buffers (before interleaving)
        if (redundancy != 0)
        {
            if (redundancyPos == 0)
            {
                // Post-redundancy: crossfade at end of frame (FFmpeg dec.c:367-381)
                celtDecoder.Flush();
                DecodeRedundancyCelt(frameData, redundancySize, toc);
                
                int fadeStart = actualSamples - 120 + prevDelayedSamples;
                int fadeLen = 120 - prevDelayedSamples;
                if (fadeStart >= 0 && fadeLen > 0)
                {
                    var window = CeltTables.Window2;
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        for (int i = 0; i < fadeLen; i++)
                        {
                            float w = window[i];
                            celtOutputChannels[ch][fadeStart + i] =
                                redundancyOutput[ch][120 + i] * w
                              + celtOutputChannels[ch][fadeStart + i] * (1.0f - w);
                        }
                    }
                }
                if (prevDelayedSamples > 0)
                    redundancyIdx = 120 - prevDelayedSamples;
            }
            else
            {
                // Pre-redundancy: copy first 120 + crossfade next 120 (FFmpeg dec.c:382-390)
                for (int ch = 0; ch < channelCount; ch++)
                {
                    Array.Copy(redundancyOutput[ch], 0,
                        celtOutputChannels[ch], prevDelayedSamples, 120);
                    
                    int fadeOff = 120 + prevDelayedSamples;
                    var window = CeltTables.Window2;
                    for (int i = 0; i < 120; i++)
                    {
                        float w = window[i];
                        celtOutputChannels[ch][fadeOff + i] =
                            celtOutputChannels[ch][fadeOff + i] * w
                          + redundancyOutput[ch][120 + i] * (1.0f - w);
                    }
                }
            }
        }
        
        // Interleave into outputBuffer
        for (int ch = 0; ch < channelCount; ch++)
        {
            for (int i = 0; i < actualSamples; i++)
                outputBuffer[i * channelCount + ch] = celtOutputChannels[ch][i];
        }
        
        samplesDecoded = actualSamples;
        return true;
    }
    
    /// <summary>
    /// Decodes a redundancy CELT frame from trailing bytes in a SILK packet.
    /// Matches FFmpeg dec.c opus_decode_redundancy().
    /// The CELT frame is always 240 samples (5ms at 48kHz), decoded from startBand=0.
    /// </summary>
    private void DecodeRedundancyCelt(ReadOnlySpan<byte> frameData, int redundancySize, OpusToc toc)
    {
        int codedChannels = toc.IsStereo ? 2 : 1;
        int endBand = toc.Bandwidth switch
        {
            OpusBandwidth.Narrowband => 13,
            OpusBandwidth.Mediumband => 17,
            OpusBandwidth.Wideband => 17,
            OpusBandwidth.SuperWideband => 19,
            OpusBandwidth.Fullband => 21,
            _ => 21
        };
        
        // Redundancy data is at the end of the frame
        var redundancyData = frameData.Slice(frameData.Length - redundancySize, redundancySize);
        var rc = new OpusRangeDecoder(redundancyData);
        rc.InitRaw((uint)redundancySize);
        
        // Decode 240-sample CELT frame (startBand=0)
        Span<float[]> outputs = new float[][] { redundancyOutput[0], redundancyOutput[1] };
        celtDecoder.DecodeFrame(ref rc, outputs, codedChannels, 240, 0, endBand);
    }
    
    /// <summary>
    /// Decodes a CELT-only frame using the full CELT decoder pipeline.
    /// </summary>
    private bool DecodeCeltFrame(
        ref OpusRangeDecoder rangeDecoder,
        OpusToc toc,
        int frameSamples,
        out int samplesDecoded)
    {
        samplesDecoded = 0;
        
        int codedChannels = toc.IsStereo ? 2 : 1;
        
        // Determine end band from bandwidth
        int endBand = toc.Bandwidth switch
        {
            OpusBandwidth.Narrowband => 13,
            OpusBandwidth.Mediumband => 17,
            OpusBandwidth.Wideband => 17,
            OpusBandwidth.SuperWideband => 19,
            OpusBandwidth.Fullband => 21,
            _ => 21
        };
        
        // Initialize raw bits reader for CELT (reads from end of frame)
        rangeDecoder.InitRaw((uint)rangeDecoder.DataLength);
        
        // Always pass all channelCount output buffers — CELT does mono→stereo expansion
        // when codedChannels < OutputChannels (matching FFmpeg which always passes 2 pointers)
        Span<float[]> outputs = celtOutputChannels.AsSpan(0, channelCount);
        
        if (!celtDecoder.DecodeFrame(ref rangeDecoder, outputs,
                                      codedChannels, frameSamples,
                                      0, endBand))
            return false;
        
        // Apply leftover redundancy crossfade from PREVIOUS frame (FFmpeg dec.c:360-365)
        if (redundancyIdx > 0)
        {
            int fadeLen = 120 - redundancyIdx;
            var leftoverWindow = CeltTables.Window2;
            for (int ch = 0; ch < channelCount; ch++)
            {
                for (int i = 0; i < fadeLen; i++)
                {
                    float w = leftoverWindow[redundancyIdx + i];
                    celtOutputChannels[ch][i] =
                        redundancyOutput[ch][120 + redundancyIdx + i] * w
                      + celtOutputChannels[ch][i] * (1.0f - w);
                }
            }
            redundancyIdx = 0;
        }
        
        // Interleave into outputBuffer
        // Use each channel's output directly — even for mono CELT, the decoder writes
        // to both channels with independent overlap/deemphasis state from prior stereo frames
        for (int ch = 0; ch < channelCount; ch++)
        {
            for (int i = 0; i < frameSamples; i++)
                outputBuffer[i * channelCount + ch] = celtOutputChannels[ch][i];
        }
        
        samplesDecoded = frameSamples;
        return true;
    }
    
    /// <summary>
    /// Decodes a Hybrid frame (SILK low-frequency + CELT high-frequency).
    /// SILK decodes the lower bands, CELT decodes the upper bands (startBand=17).
    /// 
    /// Includes redundancy CELT frame handling matching FFmpeg dec.c:277-390.
    /// Hybrid redundancy frames are explicitly signaled (12-bit log decode).
    /// When present, a 240-sample full-band CELT frame is decoded and crossfaded
    /// with the main Hybrid output to smooth CELT↔Hybrid mode transitions.
    /// 
    /// The SILK resampler (16kHz→48kHz) introduces filter startup delay, so the
    /// first frame produces fewer than frame_duration samples. To keep SILK+CELT
    /// aligned, excess CELT samples are buffered in a delay FIFO and prepended
    /// to the next frame's output (matching FFmpeg's s->celt_delay mechanism).
    /// </summary>
    private bool DecodeHybridFrame(
        ref OpusRangeDecoder rangeDecoder,
        ReadOnlySpan<byte> frameData,
        OpusToc toc,
        int frameSamples,
        out int samplesDecoded)
    {
        samplesDecoded = 0;
        int prevDelayedSamples = delayedSamples;
        
        int config = toc.Configuration;
        int durationMs = SilkFrameDurationMs[config];
        int silkBandwidth = 2; // Hybrid always uses WB for SILK component
        int codedChannels = toc.IsStereo ? 2 : 1;
        
        // Initialize resampler if needed (hybrid SILK is always 16kHz)
        int sampleRate = 16000;
        if (sampleRate != silkSampleRate)
        {
            silkSampleRate = sampleRate;
            for (int ch = 0; ch < channelCount; ch++)
            {
                silkResamplers[ch].Init(sampleRate);
                silkResamplers[ch].FeedSilence(SilkResampleDelay[(int)toc.Bandwidth]);
            }
            delayedSamples = 0;
            prevDelayedSamples = 0;
            celtDelayCount = 0;
        }
        
        // Decode SILK component
        silkDecoder.OutputChannels = Math.Min(codedChannels, channelCount);
        
        int silkSamples = silkDecoder.DecodeSuperframe(
            ref rangeDecoder, silkOutputChannels,
            silkBandwidth, codedChannels, durationMs);
        
        if (silkSamples < 0)
            return false;
        
        // Resample SILK from 16kHz to 48kHz
        int actualSamples = 0;
        for (int ch = 0; ch < channelCount; ch++)
        {
            int srcCh = Math.Min(ch, codedChannels - 1);
            int resampled = silkResamplers[ch].Resample(
                silkOutputChannels[srcCh].AsSpan(0, silkSamples),
                silkSamples,
                silkResampledChannels[ch].AsSpan(0, frameSamples),
                frameSamples);
            if (ch == 0)
                actualSamples = resampled;
        }
        delayedSamples += frameSamples - actualSamples;
        
        // Read redundancy flag from range coder (between SILK and CELT).
        // FFmpeg dec.c:279: if (consumed + 37 <= size * 8) redundancy = ff_opus_rc_dec_log(&s->rc, 12);
        int consumed = (int)rangeDecoder.Tell();
        int frameDataBits = rangeDecoder.DataLength * 8;
        int redundancy = 0;
        int redundancySize = 0;
        int redundancyPos = 0;
        if (consumed + 37 <= frameDataBits)
        {
            redundancy = (int)rangeDecoder.DecLog(12);
            if (redundancy != 0)
            {
                redundancyPos = (int)rangeDecoder.DecLog(1);
                redundancySize = (int)rangeDecoder.DecUint(256) + 2;
            }
        }
        int celtDataSize = rangeDecoder.DataLength - redundancySize;
        
        // Pre-redundancy: decode CELT redundancy frame before main CELT (FFmpeg dec.c:297-302)
        if (redundancy != 0 && redundancyPos != 0)
        {
            DecodeRedundancyCelt(frameData, redundancySize, toc);
            celtDecoder.Flush();
        }
        
        // --- CELT delay FIFO handling (matches FFmpeg's s->celt_delay) ---
        int celtOutputSamples = actualSamples;
        int priorDelay = celtDelayCount;
        
        if (priorDelay > 0)
        {
            // Hybrid mode with delay: mix previously buffered CELT samples into SILK output
            for (int ch = 0; ch < channelCount; ch++)
            {
                for (int i = 0; i < priorDelay; i++)
                    silkResampledChannels[ch][i] += celtDelayFifo[ch][i];
            }
            celtOutputSamples -= priorDelay;
        }
        else if (priorDelay == 0 && celtDelayCount > 0)
        {
            // CELT-only mode had delay samples — spurious, reset (FFmpeg dec.c:323-329)
            celtDelayCount = 0;
        }
        
        // Decode CELT component (starts at band 17 in hybrid mode)
        int endBand = toc.Bandwidth switch
        {
            OpusBandwidth.SuperWideband => 19,
            OpusBandwidth.Fullband => 21,
            _ => 21
        };
        
        rangeDecoder.InitRaw((uint)celtDataSize);
        
        Span<float[]> celtOutputs = celtOutputChannels.AsSpan(0, channelCount);
        if (!celtDecoder.DecodeFrame(ref rangeDecoder, celtOutputs,
                                      codedChannels, frameSamples,
                                      17, endBand))
            return false;
        
        // Mix new CELT samples into per-channel buffers (offset by prior delay)
        for (int ch = 0; ch < channelCount; ch++)
        {
            float[] celt = celtOutputChannels[ch];
            for (int i = 0; i < celtOutputSamples; i++)
                silkResampledChannels[ch][i + priorDelay] += celt[i];
            
            // Buffer remaining CELT samples for next frame
            int toBuffer = frameSamples - celtOutputSamples;
            for (int i = 0; i < toBuffer; i++)
                celtDelayFifo[ch][i] = celt[celtOutputSamples + i];
        }
        celtDelayCount = frameSamples - celtOutputSamples;
        
        // Apply leftover redundancy crossfade from PREVIOUS frame (FFmpeg dec.c:360-365)
        if (redundancyIdx > 0)
        {
            int fadeLen = 120 - redundancyIdx;
            var leftoverWindow = CeltTables.Window2;
            for (int ch = 0; ch < channelCount; ch++)
            {
                for (int i = 0; i < fadeLen; i++)
                {
                    float w = leftoverWindow[redundancyIdx + i];
                    silkResampledChannels[ch][i] =
                        redundancyOutput[ch][120 + redundancyIdx + i] * w
                      + silkResampledChannels[ch][i] * (1.0f - w);
                }
            }
            redundancyIdx = 0;
        }
        
        // Apply redundancy crossfade on per-channel buffers (before interleaving)
        if (redundancy != 0)
        {
            if (redundancyPos == 0)
            {
                // Post-redundancy: crossfade at end of frame (FFmpeg dec.c:367-381)
                celtDecoder.Flush();
                DecodeRedundancyCelt(frameData, redundancySize, toc);
                
                int fadeStart = actualSamples - 120 + prevDelayedSamples;
                int fadeLen = 120 - prevDelayedSamples;
                if (fadeStart >= 0 && fadeLen > 0)
                {
                    var window = CeltTables.Window2;
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        for (int i = 0; i < fadeLen; i++)
                        {
                            float w = window[i];
                            silkResampledChannels[ch][fadeStart + i] =
                                redundancyOutput[ch][120 + i] * w
                              + silkResampledChannels[ch][fadeStart + i] * (1.0f - w);
                        }
                    }
                }
                if (prevDelayedSamples > 0)
                    redundancyIdx = 120 - prevDelayedSamples;
            }
            else
            {
                // Pre-redundancy: copy first 120 + crossfade next 120 (FFmpeg dec.c:382-390)
                for (int ch = 0; ch < channelCount; ch++)
                {
                    Array.Copy(redundancyOutput[ch], 0,
                        silkResampledChannels[ch], prevDelayedSamples, 120);
                    
                    int fadeOff = 120 + prevDelayedSamples;
                    var window = CeltTables.Window2;
                    for (int i = 0; i < 120; i++)
                    {
                        float w = window[i];
                        silkResampledChannels[ch][fadeOff + i] =
                            silkResampledChannels[ch][fadeOff + i] * w
                          + redundancyOutput[ch][120 + i] * (1.0f - w);
                    }
                }
            }
        }
        
        // Interleave per-channel combined output into outputBuffer
        for (int ch = 0; ch < channelCount; ch++)
        {
            for (int i = 0; i < actualSamples; i++)
                outputBuffer[i * channelCount + ch] = silkResampledChannels[ch][i];
        }
        
        samplesDecoded = actualSamples;
        return true;
    }
    
    /// <summary>
    /// Applies Packet Loss Concealment.
    /// </summary>
    private bool DecodePlc(Span<float> output, out int samplesDecoded)
    {
        // Simple PLC: fade out previous frame
        int frameSamples = 960; // Default 20ms at 48kHz
        samplesDecoded = frameSamples;
        
        // Apply fade-out
        for (int i = 0; i < frameSamples * channelCount; i++)
        {
            float fadeGain = 1.0f - (i / (float)(frameSamples * channelCount));
            output[i] = outputBuffer[i] * fadeGain * 0.5f;
        }
        
        return true;
    }
    
    /// <summary>
    /// Internal resampler flush: outputs delayed samples and resets resampler state.
    /// Used both on mode switch (SILK→CELT) and at end-of-stream.
    /// Matching FFmpeg's opus_flush_resample + swr_close sequence.
    /// </summary>
    private int FlushResamplerInternal(Span<float> output)
    {
        if (delayedSamples <= 0 || silkSampleRate == 0)
            return 0;
        
        int flushed = 0;
        for (int ch = 0; ch < channelCount; ch++)
        {
            int n = silkResamplers[ch].Flush(
                silkResampledChannels[ch].AsSpan(0, delayedSamples),
                delayedSamples);
            if (ch == 0)
                flushed = n;
        }
        
        if (flushed <= 0)
        {
            // Reset even if no samples flushed
            foreach (var r in silkResamplers)
                r.Reset();
            silkSampleRate = 0;
            delayedSamples = 0;
            celtDelayCount = 0;
            return 0;
        }
        
        // Interleave flushed SILK into output, adding any remaining CELT delay
        int outputIdx = 0;
        for (int i = 0; i < flushed; i++)
        {
            for (int ch = 0; ch < channelCount; ch++)
            {
                float val = silkResampledChannels[ch][i];
                if (i < celtDelayCount)
                    val += celtDelayFifo[ch][i];
                if (outputIdx < output.Length)
                    output[outputIdx++] = val;
            }
        }
        
        // Reset resampler state (matching swr_close in FFmpeg)
        foreach (var r in silkResamplers)
            r.Reset();
        silkSampleRate = 0;
        delayedSamples = 0;
        celtDelayCount = 0;
        
        return flushed;
    }
    
    /// <summary>
    /// Flushes remaining delayed samples from the resampler.
    /// Call after all packets have been decoded to get the final output.
    /// Also mixes any remaining CELT delay FIFO samples into the flushed output.
    /// Returns the number of flushed samples per channel.
    /// </summary>
    public int FlushResampler(Span<float> output)
    {
        return FlushResamplerInternal(output);
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        celtDecoder.Flush();
        silkDecoder.Reset();
        foreach (var r in silkResamplers)
            r.Reset();
        silkSampleRate = 0;
        delayedSamples = 0;
        celtDelayCount = 0;
        Array.Clear(outputBuffer);
        Array.Clear(silkBuffer);
        Array.Clear(celtBuffer);
        preskipRemaining = header.PreSkip;
    }
}

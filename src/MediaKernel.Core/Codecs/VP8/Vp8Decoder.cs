// VP8 decoder — main decoder, frame header parsing, frame decode loop
// Ported from FFmpeg libavcodec/vp8.c

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.Vp9; // Reuse Vp9BoolDecoder (same VPX range coder)

namespace MediaKernel.Core.Codecs.VP8;

/// <summary>
/// VP8 video decoder — managed C# port of FFmpeg's VP8 decoder.
/// Uses macroblock-level processing (16x16 blocks), 4x4 DCT/WHT transforms,
/// and the VPX boolean range coder shared with VP9.
/// </summary>
[Codec("vp8", CodecType.Video, DisplayName = "VP8 Software Decoder", Priority = 5)]
public sealed partial class Vp8Decoder : IVideoDecoder
{
    private readonly Vp8DecoderContext ctx = new();



    /// <summary>Header range coder (for modes and motion vectors).</summary>
    private Vp9BoolDecoder headerCoder;

    /// <summary>Coefficient partition range coders (1, 2, 4, or 8).</summary>
    private readonly Vp9BoolDecoder[] coeffCoders = new Vp9BoolDecoder[8];

    private bool initialized;
    private bool isReady;
    // IVideoDecoder implementation
    public string CodecId => "vp8";
    public bool IsHardwareAccelerated => false;
    public int Width => ctx.Width;
    public int Height => ctx.Height;
    public PixelFormat OutputFormat => PixelFormat.Yuv420P;
    public bool IsReady => isReady;

    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // VP8 doesn't use codec private data — all config is in-band
        isReady = true;
        return true;
    }

    public void Flush()
    {
        // Reset reference frames on seek
        for (int i = 0; i < ctx.FrameRef.Length; i++)
            ctx.FrameRef[i] = null;
        initialized = false;
    }

    public void DrainPendingFrames()
    {
        // VP8 doesn't use B-frame reorder — no pending frames
    }

    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        if (data.Length == 0)
            return null;

        bool gotFrame = DecodeFrame(data,
            out var outputY, out var outputCb, out var outputCr,
            out int linesize, out int uvLinesize,
            out int width, out int height);

        if (!gotFrame || width == 0 || height == 0)
            return null;

        isReady = true;
        return ExtractOutputFrame(outputY, outputCb, outputCr,
            linesize, uvLinesize, width, height, presentationTimeTicks);
    }

    /// <summary>
    /// Extract the visible frame as a DecodedVideoFrame.
    /// Strips border padding and copies to a dense output buffer.
    /// </summary>
    private static DecodedVideoFrame ExtractOutputFrame(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcCb, ReadOnlySpan<byte> srcCr,
        int yStride, int uvStride, int width, int height, long pts)
    {
        int uvW = (width + 1) >> 1;
        int uvH = (height + 1) >> 1;
        int ySize = width * height;
        int uvSize = uvW * uvH;
        int totalSize = ySize + uvSize * 2;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int yOff = 0, uOff = ySize, vOff = ySize + uvSize;

        // Copy luma (strip stride padding)
        for (int y = 0; y < height; y++)
            srcY.Slice(y * yStride, width).CopyTo(outputBuffer.AsSpan(yOff + y * width));

        // Copy chroma
        for (int y = 0; y < uvH; y++)
            srcCb.Slice(y * uvStride, uvW).CopyTo(outputBuffer.AsSpan(uOff + y * uvW));

        for (int y = 0; y < uvH; y++)
            srcCr.Slice(y * uvStride, uvW).CopyTo(outputBuffer.AsSpan(vOff + y * uvW));

        return new DecodedVideoFrame(
            width, height, PixelFormat.Yuv420P, pts,
            outputBuffer,
            yOff, width,
            uOff, uvW,
            vOff, uvW);
    }

    // ==================== Range coder helper methods ====================
    // These mirror FFmpeg's vp8_rac_get_sint, vp8_rac_get_nn, vp8_rac_get_coeff

    /// <summary>
    /// Read a signed integer: flag bit, then |value| bits, then sign bit.
    /// Port of vp8_rac_get_sint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RacGetSint(ref Vp9BoolDecoder c, int bits)
    {
        if (c.Get() == 0)
            return 0;

        int v = c.GetUint(bits);
        return c.Get() != 0 ? -v : v;
    }

    /// <summary>
    /// Read a non-negative non-zero value: 7-bit unsigned, if zero → 1.
    /// Port of vp8_rac_get_nn.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RacGetNn(ref Vp9BoolDecoder c)
    {
        int v = c.GetUint(7) << 1;
        return v + (v == 0 ? 1 : 0);
    }

    /// <summary>
    /// Read DCT extra bits using zero-terminated probability table.
    /// Port of vp8_rac_get_coeff.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RacGetCoeff(ref Vp9BoolDecoder c, ReadOnlySpan<byte> prob)
    {
        int v = 0;
        int i = 0;
        do
        {
            v = (v << 1) + c.GetProb(prob[i++]);
        } while (prob[i] != 0);
        return v;
    }

    // ==================== Frame header parsing ====================

    /// <summary>
    /// Decode the VP8 frame header.
    /// Port of vp8_decode_frame_header.
    /// </summary>
    /// <param name="data">Raw frame data (including 3-byte tag).</param>
    /// <returns>
    /// Offset into data where coefficient partition data begins,
    /// or -1 on error.
    /// </returns>
    public int DecodeFrameHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return -1;

        // 3-byte frame tag
        int tag = data[0] | (data[1] << 8) | (data[2] << 16);
        ctx.Keyframe = (tag & 1) == 0;
        ctx.Profile = (byte)((tag >> 1) & 7);
        ctx.Invisible = (tag & 0x10) == 0;
        int headerSize = tag >> 5;

        int pos = 3;
        int remaining = data.Length - 3;

        ctx.HeaderPartitionSize = headerSize;

        if (ctx.Profile > 3)
        {
            // Profile > 3 not defined but we proceed
        }

        if (ctx.Keyframe)
        {
            if (remaining < 7)
                return -1;

            // Keyframe sync code: 0x9D 0x01 0x2A
            if (data[pos] != 0x9D || data[pos + 1] != 0x01 || data[pos + 2] != 0x2A)
                return -1;

            int width = (data[pos + 3] | (data[pos + 4] << 8)) & 0x3FFF;
            int height = (data[pos + 5] | (data[pos + 6] << 8)) & 0x3FFF;
            // int hscale = data[pos + 4] >> 6;
            // int vscale = data[pos + 6] >> 6;

            pos += 7;
            remaining -= 7;

            // Keyframe initializations
            ctx.UpdateGolden = (int)Vp8FrameType.Current;
            ctx.UpdateAltref = (int)Vp8FrameType.Current;

            ResetProbabilityTables();

            // Reset mode probs to inter defaults (yes, even on keyframe — matches FFmpeg)
            Vp8Data.Pred16x16ProbInter.CopyTo(ctx.Prob[0].Pred16x16);
            Vp8Data.Pred8x8cProbInter.CopyTo(ctx.Prob[0].Pred8x8c);
            Buffer.BlockCopy(Vp8Data.MvDefaultProb, 0, ctx.Prob[0].Mvc, 0, 2 * 19);

            // Clear segmentation and LF delta
            ctx.Segmentation = default;
            ctx.Segmentation.Init();
            ctx.LfDelta = default;
            ctx.LfDelta.Init();

            // Update dimensions
            if (width != ctx.Width || height != ctx.Height || !initialized)
            {
                ctx.Width = width;
                ctx.Height = height;
                ctx.MbWidth = (width + 15) / 16;
                ctx.MbHeight = (height + 15) / 16;
                ctx.AllocateFrameArrays();
                initialized = true;
            }
        }

        if (headerSize > remaining)
            return -1;

        // Initialize the header range coder
        if (!headerCoder.Init(data.Slice(pos, headerSize)))
            return -1;

        pos += headerSize;
        remaining -= headerSize;

        // Keyframe: colorspace + fullrange
        if (ctx.Keyframe)
        {
            ctx.Colorspace = (byte)headerCoder.Get();
            ctx.Fullrange = (byte)headerCoder.Get();
        }

        // Segmentation
        ctx.Segmentation.Enabled = headerCoder.Get() != 0;
        if (ctx.Segmentation.Enabled)
            ParseSegmentInfo();
        else
            ctx.Segmentation.UpdateMap = false;

        // Loop filter
        ctx.Filter.Simple = headerCoder.Get() != 0;
        ctx.Filter.Level = (byte)headerCoder.GetUint(6);
        ctx.Filter.Sharpness = (byte)headerCoder.GetUint(3);

        // LF deltas
        ctx.LfDelta.Enabled = headerCoder.Get() != 0;
        if (ctx.LfDelta.Enabled)
        {
            ctx.LfDelta.Update = headerCoder.Get() != 0;
            if (ctx.LfDelta.Update)
                UpdateLfDeltas();
        }

        // Coefficient partitions
        int coeffPartitionsResult = SetupPartitions(data.Slice(pos), remaining);
        if (coeffPartitionsResult < 0)
            return -1;

        // Update dimensions for inter frames if needed
        if (!ctx.Keyframe)
        {
            if (!initialized)
                return -1; // Need keyframe first

            // Dimensions don't change on inter frames in VP8
        }

        // Quantization
        GetQuants();

        // Inter frame: reference frame updates + sign bias
        if (!ctx.Keyframe)
        {
            UpdateRefs();
            ctx.SignBias[(int)Vp8FrameType.Golden] = (sbyte)headerCoder.Get();
            ctx.SignBias[(int)Vp8FrameType.Altref] = (sbyte)headerCoder.Get();
        }

        // Probability update flag
        ctx.UpdateProbabilities = headerCoder.Get() != 0;
        if (!ctx.UpdateProbabilities)
        {
            // Save current probs so we can restore after this frame
            ctx.Prob[1].CopyFrom(ctx.Prob[0]);
        }

        // Update last frame
        ctx.UpdateLast = ctx.Keyframe || headerCoder.Get() != 0;

        // Update coefficient probability tables
        UpdateProbabilityTables();

        // MB skip
        ctx.MbSkipEnabled = headerCoder.Get() != 0;
        if (ctx.MbSkipEnabled)
            ctx.Prob[0].MbSkip = (byte)headerCoder.GetUint(8);

        // Inter frame: intra/last/golden probs + mode/MV updates
        if (!ctx.Keyframe)
        {
            ctx.Prob[0].Intra = (byte)headerCoder.GetUint(8);
            ctx.Prob[0].Last = (byte)headerCoder.GetUint(8);
            ctx.Prob[0].Golden = (byte)headerCoder.GetUint(8);
            UpdatePred16x16Pred8x8cMvcProbabilities();
        }

        return 0;
    }

    // ==================== Header sub-parsers ====================

    /// <summary>Port of parse_segment_info.</summary>
    private void ParseSegmentInfo()
    {
        ctx.Segmentation.UpdateMap = headerCoder.Get() != 0;
        ctx.Segmentation.UpdateFeatureData = headerCoder.Get() != 0;

        if (ctx.Segmentation.UpdateFeatureData)
        {
            ctx.Segmentation.AbsoluteVals = headerCoder.Get() != 0;

            for (int i = 0; i < 4; i++)
                ctx.Segmentation.BaseQuant[i] = (sbyte)RacGetSint(ref headerCoder, 7);

            for (int i = 0; i < 4; i++)
                ctx.Segmentation.FilterLevel[i] = (sbyte)RacGetSint(ref headerCoder, 6);
        }

        if (ctx.Segmentation.UpdateMap)
        {
            for (int i = 0; i < 3; i++)
                ctx.Prob[0].SegmentId[i] = headerCoder.Get() != 0
                    ? (byte)headerCoder.GetUint(8)
                    : (byte)255;
        }
    }

    /// <summary>Port of update_lf_deltas.</summary>
    private void UpdateLfDeltas()
    {
        // Ref deltas: [0]=intra, [1]=prev, [2]=golden, [3]=altref
        for (int i = 0; i < 4; i++)
        {
            if (headerCoder.Get() != 0)
            {
                ctx.LfDelta.Ref[i] = (sbyte)headerCoder.GetUint(6);
                if (headerCoder.Get() != 0)
                    ctx.LfDelta.Ref[i] = (sbyte)(-ctx.LfDelta.Ref[i]);
            }
        }

        // Mode deltas: [4]=i4x4, [5]=zero mv, [6]=inter, [7]=split mv
        for (int i = 4; i <= 7; i++)
        {
            if (headerCoder.Get() != 0)
            {
                ctx.LfDelta.Mode[i] = (sbyte)headerCoder.GetUint(6);
                if (headerCoder.Get() != 0)
                    ctx.LfDelta.Mode[i] = (sbyte)(-ctx.LfDelta.Mode[i]);
            }
        }
    }

    /// <summary>
    /// Setup coefficient partitions from the data following the header.
    /// Port of setup_partitions.
    /// </summary>
    private int SetupPartitions(ReadOnlySpan<byte> buf, int bufSize)
    {
        ctx.NumCoeffPartitions = 1 << headerCoder.GetUint(2);

        int sizeBytes= 3 * (ctx.NumCoeffPartitions - 1);
        if (bufSize < sizeBytes)
            return -1;

        int dataOffset = sizeBytes;
        int dataRemaining = bufSize - sizeBytes;

        for (int i = 0; i < ctx.NumCoeffPartitions - 1; i++)
        {
            int size = buf[3 * i] | (buf[3 * i + 1] << 8) | (buf[3 * i + 2] << 16);
            if (dataRemaining < size)
                return -1;

            if (!coeffCoders[i].Init(buf.Slice(dataOffset, size)))
                return -1;

            dataOffset += size;
            dataRemaining -= size;
        }

        // Last partition uses all remaining data
        if (dataRemaining <= 0)
            return -1;

        if (!coeffCoders[ctx.NumCoeffPartitions - 1].Init(buf.Slice(dataOffset, dataRemaining)))
            return -1;

        return 0;
    }

    /// <summary>
    /// Parse quantization parameters from header.
    /// Port of vp8_get_quants.
    /// </summary>
    private void GetQuants()
    {
        ctx.RawQuant.YacQi = headerCoder.GetUint(7);
        ctx.RawQuant.YdcDelta = RacGetSint(ref headerCoder, 4);
        ctx.RawQuant.Y2dcDelta = RacGetSint(ref headerCoder, 4);
        ctx.RawQuant.Y2acDelta = RacGetSint(ref headerCoder, 4);
        ctx.RawQuant.UvdcDelta = RacGetSint(ref headerCoder, 4);
        ctx.RawQuant.UvacDelta = RacGetSint(ref headerCoder, 4);

        for (int i = 0; i < 4; i++)
        {
            int baseQi;
            if (ctx.Segmentation.Enabled)
            {
                baseQi = ctx.Segmentation.BaseQuant[i];
                if (!ctx.Segmentation.AbsoluteVals)
                    baseQi += ctx.RawQuant.YacQi;
            }
            else
            {
                baseQi = ctx.RawQuant.YacQi;
            }

            ref var qm = ref ctx.Qmat[i];

            qm.LumaDcQmul = (short)Vp8Data.DcQlookup[Clip7(baseQi + ctx.RawQuant.YdcDelta)];
            qm.LumaAcQmul = (short)Vp8Data.AcQlookup[Clip7(baseQi)];

            // Y2 (luma DC block) quant: DC * 2, AC * 155/100 (approximated as * 101581 >> 16)
            qm.LumaDcOnlyDcQmul = (short)(Vp8Data.DcQlookup[Clip7(baseQi + ctx.RawQuant.Y2dcDelta)] * 2);
            qm.LumaDcOnlyAcQmul = (short)Math.Max(
                Vp8Data.AcQlookup[Clip7(baseQi + ctx.RawQuant.Y2acDelta)] * 101581 >> 16,
                8);

            qm.ChromaDcQmul = (short)Math.Min(
                (int)Vp8Data.DcQlookup[Clip7(baseQi + ctx.RawQuant.UvdcDelta)],
                132);
            qm.ChromaAcQmul = (short)Vp8Data.AcQlookup[Clip7(baseQi + ctx.RawQuant.UvacDelta)];
        }
    }

    /// <summary>Clip value to [0, 127] (7-bit unsigned).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip7(int v) => Math.Clamp(v, 0, 127);

    /// <summary>
    /// Determine which buffer golden/altref should be updated with.
    /// Port of ref_to_update + update_refs.
    /// </summary>
    private void UpdateRefs()
    {
        bool updateGolden = headerCoder.Get() != 0;
        bool updateAltref = headerCoder.Get() != 0;

        ctx.UpdateGolden = RefToUpdate(updateGolden, Vp8FrameType.Golden);
        ctx.UpdateAltref = RefToUpdate(updateAltref, Vp8FrameType.Altref);
    }

    private int RefToUpdate(bool update, Vp8FrameType refType)
    {
        if (update)
            return (int)Vp8FrameType.Current;

        int v = headerCoder.GetUint(2);
        return v switch
        {
            1 => (int)Vp8FrameType.Previous,
            2 => refType == Vp8FrameType.Golden
                ? (int)Vp8FrameType.Altref
                : (int)Vp8FrameType.Golden,
            _ => (int)Vp8FrameType.None,
        };
    }

    /// <summary>
    /// Reset coefficient probability tables to defaults.
    /// Port of vp78_reset_probability_tables.
    /// </summary>
    private void ResetProbabilityTables()
    {
        var prob = ctx.Prob[0];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 16; j++)
            {
                int band = Vp8Data.CoeffBand[j];
                for (int k = 0; k < 3; k++)
                    for (int l = 0; l < Vp8Data.NumDctTokens - 1; l++)
                        prob.Token[i, j, k, l] = Vp8Data.TokenDefaultProbs[i, band, k, l];
            }
    }

    /// <summary>
    /// Update coefficient probability tables from the bitstream.
    /// Port of vp78_update_probability_tables.
    /// </summary>
    private void UpdateProbabilityTables()
    {
        var prob = ctx.Prob[0];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 8; j++)
                for (int k = 0; k < 3; k++)
                    for (int l = 0; l < Vp8Data.NumDctTokens - 1; l++)
                    {
                        if (headerCoder.GetProbBranchy(Vp8Data.TokenUpdateProbs[i, j, k, l]))
                        {
                            byte newProb = (byte)headerCoder.GetUint(8);
                            // Apply to all coefficient positions in this band
                            for (int m = 0; m < 10; m++)
                            {
                                int idx = Vp8Data.CoeffBandIndexes[j, m];
                                if (idx < 0) break;
                                prob.Token[i, idx, k, l] = newProb;
                            }
                        }
                    }
    }

    /// <summary>
    /// Update 16x16, chroma, and MV probabilities.
    /// Port of vp78_update_pred16x16_pred8x8_mvc_probabilities (VP8 variant, mvc_size=19).
    /// </summary>
    private void UpdatePred16x16Pred8x8cMvcProbabilities()
    {
        var prob = ctx.Prob[0];

        if (headerCoder.Get() != 0)
            for (int i = 0; i < 4; i++)
                prob.Pred16x16[i] = (byte)headerCoder.GetUint(8);

        if (headerCoder.Get() != 0)
            for (int i = 0; i < 3; i++)
                prob.Pred8x8c[i] = (byte)headerCoder.GetUint(8);

        // MV probability update (17.2)
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 19; j++)
                if (headerCoder.GetProbBranchy(Vp8Data.MvUpdateProb[i, j]))
                    prob.Mvc[i, j] = (byte)RacGetNn(ref headerCoder);
    }

    // ==================== Frame decode ====================

    /// <summary>
    /// Decode a complete VP8 frame from raw frame data.
    /// Port of vp78_decode_frame (VP8 path).
    /// </summary>
    /// <param name="data">Raw frame data including the 3-byte frame tag.</param>
    /// <param name="outputY">Output luma plane (set on success).</param>
    /// <param name="outputCb">Output Cb chroma plane (set on success).</param>
    /// <param name="outputCr">Output Cr chroma plane (set on success).</param>
    /// <param name="linesize">Output luma stride.</param>
    /// <param name="uvLinesize">Output chroma stride.</param>
    /// <param name="width">Output frame width.</param>
    /// <param name="height">Output frame height.</param>
    /// <returns>True if a visible frame was produced, false otherwise.</returns>
    public bool DecodeFrame(ReadOnlySpan<byte> data,
                            out ReadOnlySpan<byte> outputY, out ReadOnlySpan<byte> outputCb,
                            out ReadOnlySpan<byte> outputCr,
                            out int linesize, out int uvLinesize,
                            out int width, out int height)
    {
        outputY = outputCb = outputCr = default;
        linesize = uvLinesize = width = height = 0;

        // Parse frame header (sets ctx fields, initializes header/coeff coders)
        int ret = DecodeFrameHeader(data);
        if (ret < 0)
            return false;

        // Save previous current frame pointer
        var prevFrame = ctx.FrameRef[(int)Vp8FrameType.Current];

        // For inter frames, all reference frames must exist
        if (!ctx.Keyframe)
        {
            if (ctx.FrameRef[(int)Vp8FrameType.Previous] == null ||
                ctx.FrameRef[(int)Vp8FrameType.Golden] == null ||
                ctx.FrameRef[(int)Vp8FrameType.Altref] == null)
                return false;
        }

        // Set deblock_filter based on filter level
        ctx.DeblockFilter = ctx.Filter.Level > 0;

        // Find a free frame buffer
        var curFrame = FindFreeBuffer(prevFrame);
        if (!curFrame.IsAllocated || curFrame.Width != ctx.Width || curFrame.Height != ctx.Height)
            curFrame.Allocate(ctx.Width, ctx.Height);
        else
        {
            // Clear the existing buffer
            Array.Clear(curFrame.DataY!, 0, curFrame.DataY!.Length);
            Array.Clear(curFrame.DataCb!, 0, curFrame.DataCb!.Length);
            Array.Clear(curFrame.DataCr!, 0, curFrame.DataCr!.Length);
        }

        ctx.FrameRef[(int)Vp8FrameType.Current] = curFrame;

        // Prepare next frame pointers (will be committed after decode)
        var nextFrameP = new Vp8Frame?[4];

        // Update altref
        if (ctx.UpdateAltref != (int)Vp8FrameType.None)
            nextFrameP[(int)Vp8FrameType.Altref] = ctx.FrameRef[ctx.UpdateAltref];
        else
            nextFrameP[(int)Vp8FrameType.Altref] = ctx.FrameRef[(int)Vp8FrameType.Altref];

        // Update golden
        if (ctx.UpdateGolden != (int)Vp8FrameType.None)
            nextFrameP[(int)Vp8FrameType.Golden] = ctx.FrameRef[ctx.UpdateGolden];
        else
            nextFrameP[(int)Vp8FrameType.Golden] = ctx.FrameRef[(int)Vp8FrameType.Golden];

        // Update last/previous
        if (ctx.UpdateLast)
            nextFrameP[(int)Vp8FrameType.Previous] = curFrame;
        else
            nextFrameP[(int)Vp8FrameType.Previous] = ctx.FrameRef[(int)Vp8FrameType.Previous];

        nextFrameP[(int)Vp8FrameType.Current] = curFrame;

        // Clear top-edge state
        Array.Clear(ctx.TopNnz!, 0, ctx.TopNnz!.Length);
        Array.Fill(ctx.Intra4x4PredModeTop!, (byte)2); // DC_PRED
        Array.Clear(ctx.RefCount, 0, ctx.RefCount.Length);

        // Clear sentinel row (row 0 in layout 1) for top-edge prediction
        for (int i = 0; i < ctx.MbWidth + 1; i++)
            ctx.Macroblocks![i] = default;

        // Allocate per-row filter strengths
        filterStrength ??= new Vp8FilterStrength[ctx.MbWidth];
        if (filterStrength.Length < ctx.MbWidth)
            filterStrength = new Vp8FilterStrength[ctx.MbWidth];

        // Decode all MB rows
        for (int mbY = 0; mbY < ctx.MbHeight; mbY++)
        {
            int rowRet = DecodeMbRowNoFilter(curFrame, prevFrame, mbY);
            if (rowRet < 0)
            {
                // Commit frame pointers even on error
                Array.Copy(nextFrameP, ctx.FrameRef, 4);
                return false;
            }

            if (ctx.DeblockFilter)
                FilterMbRow(curFrame, mbY);
        }

        // Commit next frame pointers
        Array.Copy(nextFrameP, ctx.FrameRef, 4);

        // Probability persistence: if update_probabilities is false,
        // restore saved probabilities (changes were temporary)
        if (!ctx.UpdateProbabilities)
            ctx.Prob[0].CopyFrom(ctx.Prob[1]);

        // Output
        if (!ctx.Invisible)
        {
            outputY = new ReadOnlySpan<byte>(curFrame.DataY!, curFrame.YOffset,
                curFrame.Linesize * ctx.Height);
            outputCb = new ReadOnlySpan<byte>(curFrame.DataCb!, curFrame.UvOffset,
                curFrame.UvLinesize * ((ctx.Height + 1) / 2));
            outputCr = new ReadOnlySpan<byte>(curFrame.DataCr!, curFrame.UvOffset,
                curFrame.UvLinesize * ((ctx.Height + 1) / 2));
            linesize = curFrame.Linesize;
            uvLinesize = curFrame.UvLinesize;
            width = ctx.Width;
            height = ctx.Height;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Find a free frame buffer (not referenced by any framep slot).
    /// Port of vp8_find_free_buffer.
    /// </summary>
    private Vp8Frame FindFreeBuffer(Vp8Frame? prevFrame)
    {
        for (int i = 0; i < ctx.Frames.Length; i++)
        {
            var f = ctx.Frames[i];
            if (f != prevFrame &&
                f != ctx.FrameRef[(int)Vp8FrameType.Previous] &&
                f != ctx.FrameRef[(int)Vp8FrameType.Golden] &&
                f != ctx.FrameRef[(int)Vp8FrameType.Altref])
                return f;
        }
        // Shouldn't happen with 5 frame slots
        return ctx.Frames[0];
    }

    // ==================== Lifecycle ====================

    public void Dispose()
    {
        ctx.Dispose();
    }
}

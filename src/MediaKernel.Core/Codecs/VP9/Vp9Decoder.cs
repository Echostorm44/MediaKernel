// VP9 decoder — frame header parsing (uncompressed + compressed)
// Ported from FFmpeg libavcodec/vp9.c: decode_frame_header()

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>VP9 decoder — main entry point and frame header parsing.</summary>
[Codec("vp9", CodecType.Video, DisplayName = "VP9 Software Decoder", Priority = 5)]
public sealed partial class Vp9Decoder : IVideoDecoder
{
    private const uint Vp9SyncCode = 0x498342;

    /// <summary>Enable diagnostic coefficient logging (set before decode).</summary>
    public static bool EnableDiagCoefLog;
    /// <summary>Skip loop filter for diagnostic purposes.</summary>
    public static bool DiagSkipLoopFilter;
    public static bool DiagSkipInterResidual;
    public static bool DiagTraceInterBlock;
    public static bool DiagSkipAdaptProbs;
    /// <summary>Skip adaptation for a specific category: 0=none, 1=coef, 2=skip, 3=intra, 4=comp, 5=compRef, 6=singleRef, 7=partition, 8=tx, 9=filter, 10=mvMode, 11=mvJoint, 12=mvComp, 13=yMode, 14=uvMode.</summary>
    public static int DiagSkipAdaptCategory;
    /// <summary>Disable temporal MV prediction for diagnostic purposes.</summary>
    public static bool DiagDisableTemporalMvs;
    /// <summary>Enable per-coefficient tracing for first block.</summary>
    public static bool DiagTraceCoefs;
    /// <summary>Trace block positions during decode.</summary>
    public static bool DiagTraceBlocks;
    /// <summary>Trace bool decoder state at superblock boundaries.</summary>
    public static bool DiagTraceSbState;
    /// <summary>When true, log only bufPos at each SB start (minimal output).</summary>
    public static bool DiagTracePerSbPos;
    /// <summary>Trace frame header values.</summary>
    public static bool DiagTraceHeaders;
    public static bool DiagTraceSegQuant;
    /// <summary>When >= 0, trace reconstruction details for blocks at this 8x8 row.</summary>
    public static int DiagTraceReconRow = -1;
    /// <summary>When >= 0, force eob=0 for all sub-blocks at 8x8 rows >= this value.</summary>
    public static int DiagForceZeroEobMinRow = -1;
    /// <summary>When >= 0, trace block/coef info for all blocks at 8x8 rows >= this value.</summary>
    public static int DiagTraceBlocksMinRow = -1;

    /// <summary>Capture bool decoder state + probs for shadow comparison.</summary>
    public static bool DiagCaptureShadowState;
    /// <summary>Captured state for shadow comparison.</summary>
    public static (int High, uint CodeWord, int Bits, int BufPos)? DiagCapturedBoolState;
    public static byte[]? DiagCapturedBitstream;
    public static byte[]? DiagCapturedProbs; // 6*6*11 expanded probs
    public static int DiagCapturedNnz;
    public static short[]? DiagCapturedQmul;
    public static short[]? DiagCapturedCoefResult; // decoded coefficients

    /// <summary>When set, captures the invisible frame Y/U/V pixel data for diagnostics.</summary>
    public static bool DiagCaptureInvisibleFrame;
    /// <summary>Captured invisible frame pixel data (Y plane, tightly packed).</summary>
    public static byte[]? DiagInvisibleY;
    public static byte[]? DiagInvisibleU;
    public static byte[]? DiagInvisibleV;
    public static int DiagInvisibleYStride;
    public static int DiagInvisibleWidth;
    public static int DiagInvisibleHeight;

    private readonly Vp9DecoderContext ctx = new();
    private bool isReady;
    private bool wasShowExistingFrame; // set by DecodeFrame when show_existing_frame
    private long lastPts;

    // Bit reader for uncompressed header (MSB-first)
    private ReadOnlyMemory<byte> frameData;

    /// <summary>
    /// Backs <see cref="frameData"/>, reused between frames. The bit reader needs a field, and a
    /// span cannot be stored in one, so the frame has to live in memory this decoder owns - but it
    /// was being copied into a fresh array every frame, which at VP9's frame sizes was the largest
    /// single allocation on the decode path. Grows to the largest frame seen and then stops.
    /// </summary>
    private byte[]? frameDataBuffer;
    private int bitOffset;

    /// <summary>
    /// Range decoders for the tile columns, and for the compressed header, kept across frames.
    /// </summary>
    /// <remarks>
    /// Vp9BoolDecoder is a struct, so constructing one costs nothing on the heap - but it zeroes
    /// the byte[] the decoder copies its tile into, and Init then has to allocate a fresh one.
    /// Constructing a decoder per tile per frame therefore allocated a tile-sized array per tile
    /// per frame, a tile being most of the frame's compressed data. Init resets every field it
    /// needs, so the instances can simply be re-initialised in place instead.
    /// </remarks>
    /// <summary>Per-phase allocation counters, for the zero-allocation work only.</summary>

    private Vp9BoolDecoder[]? tileBoolDecoders;
    private Vp9BoolDecoder compressedHeaderDecoder;

    // IVideoDecoder implementation
    public string CodecId => "vp9";
    public bool IsHardwareAccelerated => false;
    public int Width => ctx.Width;
    public int Height => ctx.Height;
    /// <inheritdoc/>
    /// <remarks>
    /// Decided by the frame header, not fixed: VP9 profile 1 carries 4:2:2, 4:4:4 and RGB,
    /// and profiles 2 and 3 carry 10- and 12-bit. See <c>read_colorspace_details</c>.
    /// </remarks>
    public PixelFormat OutputFormat => ctx.OutputPixelFormat;
    public bool IsReady => isReady;

    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // VP9 doesn't use codec private data — all config is in-band
        // Force LUT rebuild on first frame (FFmpeg sets sharpness=-1 in vp9_decode_init)
        ctx.Header.Sharpness = 255;
        isReady = true;
        return true;
    }

    public void Flush()
    {
        // Reset decoder state for seek
        ctx.Reset();
    }

    public void DrainPendingFrames()
    {
        // VP9 doesn't use B-frame reorder — no pending frames
    }

    public void Dispose()
    {
        ctx.Dispose();
    }

    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        if (data.Length == 0)
            return null;

        lastPts = presentationTimeTicks;

        // VP9 superframe splitting: a single packet may contain multiple frames
        // (e.g., invisible altref + visible frame). FFmpeg handles this via the
        // vp9_superframe_split BSF; we do it inline here.
        if (EnableDiagCoefLog)
            Console.WriteLine($"  DECODE_ENTRY: dataLen={data.Length} lastByte=0x{data[^1]:X2} sfParse={Vp9HeaderParser.TryParseSuperframeIndex(data, out var sfInfoDbg)} sfCount={sfInfoDbg.FrameCount}");
        bool sfOk = Vp9HeaderParser.TryParseSuperframeIndex(data, out var sfInfo);
        if (sfOk && sfInfo.FrameCount > 1)
        {
            int offset = 0;
            var sizes = sfInfo.FrameSizes;
            DecodedVideoFrame? result = null;

            for (int i = 0; i < sfInfo.FrameCount; i++)
            {
                int frameSize = sizes[i];
                if (offset + frameSize > data.Length)
                    break;

                var subFrame = data.Slice(offset, frameSize);
                offset += frameSize;

                int consumed = DecodeFrame(subFrame);
                if (consumed < 0)
                    continue;

                // Invisible frames are decoded for reference but NOT output.
                // show_existing_frame is ALWAYS visible (FFmpeg sets *got_frame=1 directly).
                if (ctx.Header.Invisible != 0 && !wasShowExistingFrame)
                {
                    if (DiagCaptureInvisibleFrame)
                        CaptureInvisibleFrame();
                    continue;
                }

                // Extract the visible frame — if multiple visible frames exist,
                // keep the last one (matches FFmpeg behavior)
                result?.Dispose();
                result = ExtractOutputFrame(presentationTimeTicks);
            }

            isReady = true;
            return result;
        }

        // Single frame (no superframe index)
        int singleConsumed = DecodeFrame(data);
        if (singleConsumed < 0)
            return null;

        // Invisible frames (altref) are decoded for reference but NOT output.
        // show_existing_frame is ALWAYS visible (FFmpeg sets *got_frame=1 directly).
        if (ctx.Header.Invisible != 0 && !wasShowExistingFrame)
        {
            if (DiagCaptureInvisibleFrame)
                CaptureInvisibleFrame();
            return null;
        }

        isReady = true;
        var outFrame = ExtractOutputFrame(presentationTimeTicks);
        return outFrame;
    }

    /// <summary>
    /// Extract the current decoded frame as a DecodedVideoFrame.
    /// Copies pixel data from internal buffers to a pooled output buffer.
    /// </summary>
    private DecodedVideoFrame? ExtractOutputFrame(long presentationTimeTicks)
    {

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        if (curFrame == null || !curFrame.Valid)
            return null;

        if (ctx.Width == 0 || ctx.Height == 0)
            return null;

        int w = ctx.Width;
        int h = ctx.Height;

        // THE SUBSAMPLING IS THE STREAM'S, not an assumption. This read (w + 1) >> 1 and
        // (h + 1) >> 1 unconditionally and labelled the result Yuv420P, so a 4:4:4 stream -
        // which VP9 profile 1 carries, and which one file in this tree is - was handed out
        // as the top-left quarter of each chroma plane under the wrong geometry. The planes
        // decoded correctly; only the copy out was wrong, which is the kind of fault no
        // decoder-level test can see.
        int bpp = Math.Max(1, (int)ctx.BytesPerPixel);
        int uvW = (w + (1 << ctx.SubsamplingX) - 1) >> ctx.SubsamplingX;
        int uvH = (h + (1 << ctx.SubsamplingY) - 1) >> ctx.SubsamplingY;

        int yRowBytes = w * bpp;
        int uvRowBytes = uvW * bpp;
        int ySize = yRowBytes * h;
        int uvSize = uvRowBytes * uvH;
        int totalSize = ySize + uvSize * 2;

        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(totalSize);
        int yOff = 0;
        int uOff = ySize;
        int vOff = ySize + uvSize;

        for (int y = 0; y < h; y++)
        {
            curFrame.LumaData.AsSpan(y * curFrame.LumaStride, yRowBytes)
                .CopyTo(outputBuffer.AsSpan(yOff + y * yRowBytes));
        }

        for (int y = 0; y < uvH; y++)
        {
            curFrame.ChromaUData.AsSpan(y * curFrame.ChromaStride, uvRowBytes)
                .CopyTo(outputBuffer.AsSpan(uOff + y * uvRowBytes));
        }

        for (int y = 0; y < uvH; y++)
        {
            curFrame.ChromaVData.AsSpan(y * curFrame.ChromaStride, uvRowBytes)
                .CopyTo(outputBuffer.AsSpan(vOff + y * uvRowBytes));
        }

        return new DecodedVideoFrame(
            w, h, ctx.OutputPixelFormat, presentationTimeTicks,
            outputBuffer,
            yOff, yRowBytes,
            uOff, uvRowBytes,
            vOff, uvRowBytes);
    }

    /// <summary>
    /// Capture the current invisible frame's pixel data into diagnostic static fields.
    /// </summary>
    private void CaptureInvisibleFrame()
    {
        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        if (curFrame == null || !curFrame.Valid)
            return;

        int w = ctx.Width;
        int h = ctx.Height;
        int uvW = (w + 1) >> 1;
        int uvH = (h + 1) >> 1;

        DiagInvisibleWidth = w;
        DiagInvisibleHeight = h;
        DiagInvisibleYStride = curFrame.LumaStride;

        DiagInvisibleY = new byte[w * h];
        DiagInvisibleU = new byte[uvW * uvH];
        DiagInvisibleV = new byte[uvW * uvH];

        for (int y = 0; y < h; y++)
            curFrame.LumaData.AsSpan(y * curFrame.LumaStride, w)
                .CopyTo(DiagInvisibleY.AsSpan(y * w));

        for (int y = 0; y < uvH; y++)
            curFrame.ChromaUData.AsSpan(y * curFrame.ChromaStride, uvW)
                .CopyTo(DiagInvisibleU.AsSpan(y * uvW));

        for (int y = 0; y < uvH; y++)
            curFrame.ChromaVData.AsSpan(y * curFrame.ChromaStride, uvW)
                .CopyTo(DiagInvisibleV.AsSpan(y * uvW));
    }

    /// <summary>Decode a VP9 frame.</summary>
    /// <returns>Number of bytes consumed, or negative on error.</returns>
    public int DecodeFrame(ReadOnlySpan<byte> data)
    {
        StoreFrameData(data);
        bitOffset = 0;
        wasShowExistingFrame = false;

        // Save previous segmentation state before parsing new header
        // (matches FFmpeg vp9.c:1591-1592 which evaluates retain_segmap_ref before decode_frame_header)
        ref var h = ref ctx.Header;
        bool prevSegEnabled = h.Segmentation.Enabled != 0;
        bool prevSegUpdateMap = h.Segmentation.UpdateMap != 0;

        int showExistingFrame = -1;
        int consumed = DecodeFrameHeader(data, ref showExistingFrame);
        if (consumed < 0)
            return consumed;

        if (showExistingFrame >= 0)
        {
            // Show existing frame — copy the referenced frame into CurFrame for output.
            // FFmpeg: av_frame_ref(frame, s->s.refs[ref].f) + *got_frame = 1.
            // Reference slots are NOT updated for show_existing_frame.
            // h.Invisible is NOT modified — it retains the value from the last decoded frame
            // so that the next real frame's lastInvisible / UseLastFrameMvs is correct.
            var refFrame = ctx.Refs[showExistingFrame];
            if (refFrame == null || !refFrame.Valid)
                return -1; // requested reference not available

            var showFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
            if (showFrame == null)
            {
                showFrame = new Vp9ReferenceFrameBuffer();
                ctx.Frames[Vp9DecoderContext.CurFrame] = showFrame;
            }
            CopyFrameBuffer(refFrame, showFrame);
            wasShowExistingFrame = true;
            return data.Length;
        }

        // Save previous CUR_FRAME as temporal MV/segmap reference before allocating new frame
        // (matches FFmpeg vp9.c:1632-1636)
        // src = previous CUR_FRAME if current frame is inter + visible + no error resilience, else BLANK
        bool useCurrentAsSrc = h.Keyframe == 0 && h.IntraOnly == 0 && h.ErrorRes == 0;
        int srcIdx = useCurrentAsSrc ? Vp9DecoderContext.CurFrame : Vp9DecoderContext.BlankFrame;
        var src = ctx.Frames[srcIdx];

        var segMapRef = ctx.Frames[Vp9DecoderContext.RefFrameSegMap];
        bool retainSegmapRef = segMapRef.SegmentationMap != null &&
            (!prevSegEnabled || !prevSegUpdateMap);

        if (!retainSegmapRef || h.Keyframe != 0 || h.IntraOnly != 0)
            CopyFrameTemporalData(src, ctx.Frames[Vp9DecoderContext.RefFrameSegMap]);
        CopyFrameTemporalData(src, ctx.Frames[Vp9DecoderContext.RefFrameMvPair]);

        // Allocate current frame buffer
        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        if (curFrame == null)
        {
            curFrame = new Vp9ReferenceFrameBuffer();
            ctx.Frames[Vp9DecoderContext.CurFrame] = curFrame;
        }
        Vp9DecoderContext.AllocateFrameBuffer(curFrame, ctx.Width, ctx.Height,
                                              ctx.SubsamplingX, ctx.SubsamplingY);

        // If REF_FRAME_MVPAIR dimensions don't match new CUR_FRAME, invalidate segmap ref
        // (matches FFmpeg vp9.c:1655-1660)
        var mvPairRef = ctx.Frames[Vp9DecoderContext.RefFrameMvPair];
        if (!mvPairRef.Valid ||
            mvPairRef.Width != ctx.Width || mvPairRef.Height != ctx.Height)
        {
            var segMapFrame = ctx.Frames[Vp9DecoderContext.RefFrameSegMap];
            if (segMapFrame.SegmentationMap != null)
            {
                segMapFrame.SpareSegmentationMap = segMapFrame.SegmentationMap;
                segMapFrame.SegmentationMap = null;
            }
        }

        // Initialize above context arrays for the frame (matching FFmpeg memset calls)
        InitializeAboveContextArrays();

        if (DiagTraceHeaders)
        {
            Console.WriteLine($"  [HDR] {ctx.Width}x{ctx.Height} key={h.Keyframe} intra={h.IntraOnly} " +
                $"invisible={h.Invisible} " +
                $"lf={h.FilterLevel} sharp={h.Sharpness} lfDeltaEn={h.LfDeltaEnabled} " +
                $"lfDeltaUpd={h.LfDeltaUpdated} " +
                $"lfRef=[{h.LfDeltaRef0},{h.LfDeltaRef1},{h.LfDeltaRef2},{h.LfDeltaRef3}] " +
                $"lfMode=[{h.LfDeltaMode0},{h.LfDeltaMode1}] " +
                $"yacQi={h.YacQi} txfm={h.TxfmMode} " +
                $"refIdx=[{h.RefIdx0},{h.RefIdx1},{h.RefIdx2}] " +
                $"signBias=[{h.SignBias0},{h.SignBias1},{h.SignBias2}] " +
                $"compPred={h.CompPredMode} fixComp={h.FixCompRef} " +
                $"refreshMask={h.RefreshRefMask:X2} seg={h.Segmentation.Enabled} " +
                $"updMap={h.Segmentation.UpdateMap} segTemp={h.Segmentation.Temporal} segAbs={h.Segmentation.AbsoluteVals} " +
                $"tileR={h.TileRows} tileC={h.TileCols} " +
                $"frameCtx={h.FrameCtxId} parallel={h.ParallelMode} refreshCtx={h.RefreshCtx} " +
                $"compHdr={h.CompressedHeaderSize} consumed={consumed}");
        }

        // Decode tiles
        ctx.DiagBlockCount = 0;
        ctx.DiagCoefLog = EnableDiagCoefLog;
        if (EnableDiagCoefLog)
        {
            Console.WriteLine($"  HEADER: {ctx.Width}x{ctx.Height} key={h.Keyframe} txfmMode={h.TxfmMode} " +
                $"lossless={h.Lossless} yacQi={h.YacQi} ydcDelta={h.YdcQdelta} " +
                $"compHdrSize={h.CompressedHeaderSize} uncompHdrSize={h.UncompressedHeaderSize} " +
                $"tileRows={h.TileRows} tileCols={h.TileCols} consumed={consumed} frameLen={data.Length} " +
                $"invisible={h.Invisible} intraOnly={h.IntraOnly} refreshMask={h.RefreshRefMask:X2} " +
                $"refIdx=[{h.RefIdx0},{h.RefIdx1},{h.RefIdx2}] signBias=[{h.SignBias0},{h.SignBias1},{h.SignBias2}] " +
                $"compPredMode={h.CompPredMode} fixCompRef={h.FixCompRef} frameCtx={h.FrameCtxId} " +
                $"parallelMode={h.ParallelMode} refreshCtx={h.RefreshCtx}");
        }
        int tileDataStart = consumed;
        if (DiagTraceInterBlock || EnableDiagCoefLog)
        {
            Console.Write($"  TILE_DATA_START={tileDataStart} first16=");
            for (int i = 0; i < Math.Min(16, data.Length - tileDataStart); i++)
                Console.Write($"{data[tileDataStart + i]:X2} ");
            Console.WriteLine();

            // Dump reference frame data before decode
            if (h.Keyframe == 0)
            {
                for (int ri = 0; ri < 3; ri++)
                {
                    int slot = h.GetRefIdx(ri);
                    var rf = ctx.Refs[slot];
                    if (rf?.LumaData != null)
                    {
                        long sum = 0;
                        for (int pi = 0; pi < Math.Min(1000, rf.LumaData.Length); pi++)
                            sum += rf.LumaData[pi];
                        Console.WriteLine($"  REF_CHECK[{ri}→slot{slot}]: {rf.Width}x{rf.Height} stride={rf.LumaStride} " +
                            $"valid={rf.Valid} first10=[{string.Join(",", rf.LumaData.AsSpan(0, Math.Min(10, rf.LumaData.Length)).ToArray())}] sum1000={sum}");
                    }
                    else
                        Console.WriteLine($"  REF_CHECK[{ri}→slot{slot}]: NULL");
                }
            }
        }
        int tileRet = DecodeTiles(data.Slice(tileDataStart));
        if (tileRet < 0)
            return tileRet;

        // Update probabilities for next frame
        if (h.RefreshCtx != 0 && !DiagSkipAdaptProbs)
        {
            if (h.ParallelMode != 0)
            {
                // Parallel mode: copy current probs to stored context without adaptation
                // This matches FFmpeg's behavior at vp9.c:1709-1722
                ctx.ProbCtx[h.FrameCtxId].CopyFrom(ctx.ActiveProb);
            }
            else
            {
                // Normal mode: adapt probs using accumulated counts
                AdaptProbs();
            }
        }

        // Update last-keyframe state (matches FFmpeg: last_keyframe = keyframe || intraonly)
        ctx.LastKeyframe = (byte)(h.Keyframe != 0 || h.IntraOnly != 0 ? 1 : 0);

        // Diagnostic: check if current frame differs from ref slot 0 (keyframe)
        if (EnableDiagCoefLog && h.Keyframe == 0 && curFrame.LumaData != null && ctx.Refs[0]?.LumaData != null)
        {
            int diffCount = 0;
            int maxDiff = 0;
            int firstDiffIdx = -1;
            var curLuma = curFrame.LumaData;
            var refLuma = ctx.Refs[0].LumaData;
            int cmpLen = Math.Min(curLuma.Length, refLuma.Length);
            for (int j = 0; j < cmpLen; j++)
            {
                int d = Math.Abs(curLuma[j] - refLuma[j]);
                if (d > 0)
                {
                    diffCount++;
                    if (d > maxDiff) maxDiff = d;
                    if (firstDiffIdx < 0) firstDiffIdx = j;
                }
            }
            Console.WriteLine($"  FRAME_VS_REF0: frame invisible={h.Invisible} diffPixels={diffCount}/{cmpLen} maxDiff={maxDiff} firstDiffAt={firstDiffIdx}");
        }

        // Update reference frame slots
        for (int i = 0; i < 8; i++)
        {
            if ((h.RefreshRefMask & (1 << i)) != 0)
            {
                if (ctx.Refs[i] == null)
                    ctx.Refs[i] = new Vp9ReferenceFrameBuffer();
                CopyFrameBuffer(curFrame, ctx.Refs[i]);
            }
        }

        if (DiagTraceHeaders)
        {
            // Print MD5 of the actual pixel region for the current frame
            using var md5 = System.Security.Cryptography.MD5.Create();
            int fw = ctx.Width, fh = ctx.Height;
            int yBytes = fw * fh;
            int uvW = (fw + 1) >> 1, uvH = (fh + 1) >> 1;
            int uvBytes = uvW * uvH;
            byte[] packed = new byte[yBytes + uvBytes * 2];
            int pos = 0;
            for (int y = 0; y < fh; y++)
            {
                curFrame.LumaData.AsSpan(y * curFrame.LumaStride, fw).CopyTo(packed.AsSpan(pos));
                pos += fw;
            }
            for (int y = 0; y < uvH; y++)
            {
                curFrame.ChromaUData.AsSpan(y * curFrame.ChromaStride, uvW).CopyTo(packed.AsSpan(pos));
                pos += uvW;
            }
            for (int y = 0; y < uvH; y++)
            {
                curFrame.ChromaVData.AsSpan(y * curFrame.ChromaStride, uvW).CopyTo(packed.AsSpan(pos));
                pos += uvW;
            }
            byte[] hash = md5.ComputeHash(packed);
            string md5str = BitConverter.ToString(hash).Replace("-", "").ToLower();
            Console.WriteLine($"  [FRAME_MD5] invisible={h.Invisible} refreshMask={h.RefreshRefMask:X2} md5={md5str} size={packed.Length}");
        }

        curFrame.Valid = true;
        return data.Length; // consumed entire packet
    }

    /// <summary>Copy frame buffer contents from src to dst.</summary>
    private static void CopyFrameBuffer(Vp9ReferenceFrameBuffer src, Vp9ReferenceFrameBuffer dst)
    {
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.LumaStride = src.LumaStride;
        dst.ChromaStride = src.ChromaStride;
        dst.Valid = true;

        if (src.LumaData != null)
        {
            if (dst.LumaData == null || dst.LumaData.Length < src.LumaData.Length)
                dst.LumaData = new byte[src.LumaData.Length];
            src.LumaData.AsSpan().CopyTo(dst.LumaData);
        }
        if (src.ChromaUData != null)
        {
            if (dst.ChromaUData == null || dst.ChromaUData.Length < src.ChromaUData.Length)
                dst.ChromaUData = new byte[src.ChromaUData.Length];
            src.ChromaUData.AsSpan().CopyTo(dst.ChromaUData);
        }
        if (src.ChromaVData != null)
        {
            if (dst.ChromaVData == null || dst.ChromaVData.Length < src.ChromaVData.Length)
                dst.ChromaVData = new byte[src.ChromaVData.Length];
            src.ChromaVData.AsSpan().CopyTo(dst.ChromaVData);
        }
        if (src.SegmentationMap != null)
        {
            if (dst.SegmentationMap == null || dst.SegmentationMap.Length < src.SegmentationMap.Length)
                dst.SegmentationMap = new byte[src.SegmentationMap.Length];
            src.SegmentationMap.AsSpan().CopyTo(dst.SegmentationMap);
        }
        if (src.Mvs != null)
        {
            if (dst.Mvs == null || dst.Mvs.Length < src.Mvs.Length)
                dst.Mvs = new Vp9MvRefPair[src.Mvs.Length];
            src.Mvs.AsSpan().CopyTo(dst.Mvs);
        }
    }

    /// <summary>Copy only MV and segmentation data from src to dst (for temporal references).</summary>
    private static void CopyFrameTemporalData(Vp9ReferenceFrameBuffer src, Vp9ReferenceFrameBuffer dst)
    {
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.Valid = src.Valid;

        if (src.Mvs != null)
        {
            if (dst.Mvs == null || dst.Mvs.Length < src.Mvs.Length)
                dst.Mvs = TakeParkedOrAllocate(ref dst.SpareMvs, src.Mvs.Length);
            src.Mvs.AsSpan().CopyTo(dst.Mvs);
        }
        else if (dst.Mvs != null)
        {
            dst.SpareMvs = dst.Mvs;
            dst.Mvs = null;
        }

        if (src.SegmentationMap != null)
        {
            if (dst.SegmentationMap == null || dst.SegmentationMap.Length < src.SegmentationMap.Length)
                dst.SegmentationMap = TakeParkedOrAllocate(ref dst.SpareSegmentationMap, src.SegmentationMap.Length);
            src.SegmentationMap.AsSpan().CopyTo(dst.SegmentationMap);
        }
        else if (dst.SegmentationMap != null)
        {
            dst.SpareSegmentationMap = dst.SegmentationMap;
            dst.SegmentationMap = null;
        }
    }

    /// <summary>
    /// Takes back a parked buffer of exactly <paramref name="length"/> elements, or allocates one.
    /// </summary>
    /// <remarks>
    /// The length has to match exactly rather than merely suffice: callers read the array's Length,
    /// and handing back a longer buffer would leave a tail holding the previous frame's data where
    /// a fresh array would have zeroes.
    /// </remarks>
    private static T[] TakeParkedOrAllocate<T>(ref T[]? parked, int length)
    {
        if (parked is { } buffer && buffer.Length == length)
        {
            parked = null;
            return buffer;
        }

        return new T[length];
    }

    /// <summary>
    /// Decode all tiles in the frame. Port of FFmpeg decode_tiles().
    /// </summary>
    private int DecodeTiles(ReadOnlySpan<byte> data)
    {
        ref var h = ref ctx.Header;
        int size = data.Length;
        int dataOff = 0;

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        int lsY = curFrame.LumaStride;
        int lsUv = curFrame.ChromaStride;
        int ssH = ctx.SubsamplingX;
        int ssV = ctx.SubsamplingY;

        // Tile bool decoders, kept between frames so their buffers survive - see the field.
        int totalTileCols = h.TileCols;
        if (tileBoolDecoders is null || tileBoolDecoders.Length < totalTileCols)
            Array.Resize(ref tileBoolDecoders, totalTileCols);

        int yOff = 0, uvOff = 0;

        for (int tileRow = 0; tileRow < h.TileRows; tileRow++)
        {
            SetTileOffset(out int tileRowStart, out int tileRowEnd,
                          tileRow, h.Log2TileRows, ctx.SuperblockRows);

            // Initialize tile column bool decoders
            for (int tileCol = 0; tileCol < h.TileCols; tileCol++)
            {
                int tileSize;
                if (tileCol == h.TileCols - 1 && tileRow == h.TileRows - 1)
                {
                    tileSize = size;
                }
                else
                {
                    if (size < 4) return -1;
                    tileSize = BinaryPrimitives.ReadInt32BigEndian(data.Slice(dataOff));
                    dataOff += 4;
                    size -= 4;
                }
                if (tileSize > size) return -1;

                tileBoolDecoders[tileCol].Init(data.Slice(dataOff, tileSize));

                // Read and discard marker bit
                if (tileBoolDecoders[tileCol].GetProbBranchy(128))
                    return -1;

                dataOff += tileSize;
                size -= tileSize;
            }

            // Decode superblock rows in this tile row
            for (int row = tileRowStart; row < tileRowEnd;
                 row += 8, yOff += lsY * 64, uvOff += lsUv * (64 >> ssV))
            {
                int yOff2 = yOff, uvOff2 = uvOff;
                int lflvlIdx = 0;

                for (int tileCol = 0; tileCol < h.TileCols; tileCol++)
                {
                    SetTileOffset(out int tileColStart, out int tileColEnd,
                                  tileCol, h.Log2TileCols, ctx.SuperblockCols);
                    ctx.TileColStart = tileColStart;

                    // Reset left context arrays at tile column boundary
                    ResetLeftContext(ref h);

                    if (DiagTraceSbState)
                    {
                        var srcSt = tileBoolDecoders[tileCol].GetState();
                        Console.WriteLine($"TILE_ASSIGN row={row} tileCol={tileCol}: src pos={srcSt.BufPos} high={srcSt.High}");
                    }

                    ctx.ActiveBoolDecoder = tileBoolDecoders[tileCol];

                    // Trace SB row start for bottom-edge debugging
                    if (DiagTraceBlocksMinRow >= 0)
                    {
                        var sbRowSt = ctx.ActiveBoolDecoder.GetState();
                        var (_, _, sbRowEnd) = ctx.ActiveBoolDecoder.GetBufferState();
                        Console.WriteLine($"  [SB-ROW] row={row} tileCol={tileCol} bufPos={sbRowSt.BufPos} bufEnd={sbRowEnd} high={sbRowSt.High} cw=0x{sbRowSt.CodeWord:X8} bits={sbRowSt.Bits}");
                    }

                    if (DiagTraceSbState)
                    {
                        var dstSt = ctx.ActiveBoolDecoder.GetState();
                        Console.WriteLine($"TILE_ASSIGN row={row} tileCol={tileCol}: dst pos={dstSt.BufPos} high={dstSt.High}");
                    }

                    for (int col = tileColStart; col < tileColEnd;
                         col += 8, yOff2 += 64, uvOff2 += 64 >> ssH, lflvlIdx++)
                    {
                        // Ensure we have enough lflvl entries
                        if (lflvlIdx < ctx.LoopFilterLevels!.Length)
                            ctx.LoopFilterLevels[lflvlIdx].ClearMask();

                        // Decode this 64×64 superblock
                        ctx.CurrentRow = row;
                        ctx.CurrentCol = col;
                        ctx.CurrentLflvlIdx = lflvlIdx;
                        if (DiagTracePerSbPos)
                        {
                            var (h2, cw, b, bp) = ctx.ActiveBoolDecoder.GetState();
                            Console.WriteLine($"SB({row},{col}) pos={bp} high={h2} cw=0x{cw:X8} bits={b} calls={ctx.ActiveBoolDecoder.CallCount}");
                        }
                        if (DiagTraceSbState)
                        {
                            var (h2, cw, b, bp) = ctx.ActiveBoolDecoder.GetState();
                            Console.WriteLine($"SB({row},{col}) bool: high={h2} cw=0x{cw:X8} bits={b} pos={bp}");
                            // Enable block-level tracing for SB(0,8) and SB(8,8)
                            if ((row == 0 && col == 8) || (row == 8 && col == 8))
                            {
                                ctx.DiagCoefLog = true;
                                ctx.DiagBlockCount = 0;
                                Console.Write($"  AboveYNnz[{col*2}..{col*2+15}]: ");
                                for (int ci = col * 2; ci < col * 2 + 16 && ci < ctx.AboveYNnzCtx!.Length; ci++)
                                    Console.Write($"{ctx.AboveYNnzCtx[ci]} ");
                                Console.WriteLine();
                                Console.Write($"  LeftYNnz: ");
                                for (int ci = 0; ci < 16; ci++)
                                    Console.Write($"{ctx.LeftYNnzCtx[ci]} ");
                                Console.WriteLine();
                            }
                            else
                            {
                                ctx.DiagCoefLog = false;
                            }
                        }
                        DecodeSuperblock(ref ctx.ActiveBoolDecoder, row, col,
                            Vp9BlockLevel.Bl64x64, yOff2, uvOff2);
                    }

                    tileBoolDecoders[tileCol] = ctx.ActiveBoolDecoder;
                }

                // Backup pre-loopfilter reconstruction data for intra prediction of next row
                if (row + 8 < ctx.Rows)
                    BackupIntraPredData(yOff, uvOff);

                // Apply loop filter for this superblock row
                if (h.FilterLevel > 0)
                {
                    yOff2 = yOff;
                    uvOff2 = uvOff;
                    for (int col = 0, li = 0; col < ctx.Cols; col += 8, li++,
                         yOff2 += 64, uvOff2 += 64 >> ssH)
                    {
                        if (li < ctx.LoopFilterLevels!.Length && ctx.Header.FilterLevel > 0 && !DiagSkipLoopFilter)
                            LoopfilterSb(ref ctx.LoopFilterLevels[li], row, col, yOff2, uvOff2);
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>Compute tile start and end offsets (in 8-pel units). Port of FFmpeg set_tile_offset().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetTileOffset(out int start, out int end, int idx, int log2N, int n)
    {
        int sbStart = (idx * n) >> log2N;
        int sbEnd = ((idx + 1) * n) >> log2N;
        start = Math.Min(sbStart, n) << 3;
        end = Math.Min(sbEnd, n) << 3;
    }

    /// <summary>Reset left context arrays at tile column boundary.</summary>
    private void ResetLeftContext(ref Vp9BitstreamHeader h)
    {
        Array.Clear(ctx.LeftPartitionCtx);
        Array.Clear(ctx.LeftSkipCtx);
        Array.Clear(ctx.LeftYNnzCtx);
        Array.Clear(ctx.LeftUvNnzCtx0);
        Array.Clear(ctx.LeftUvNnzCtx1);
        Array.Clear(ctx.LeftSegpredCtx);

        byte fillVal = (h.Keyframe != 0 || h.IntraOnly != 0)
            ? (byte)Vp9IntraMode.Dc
            : (byte)10; // NEARESTMV
        Array.Fill(ctx.LeftModeCtx, fillVal);
    }

    /// <summary>Initialize above context arrays at start of frame decode.
    /// Port of FFmpeg memset calls in decode_tiles().</summary>
    private void InitializeAboveContextArrays()
    {
        ref var h = ref ctx.Header;
        int cols = ctx.Cols;

        Array.Clear(ctx.AbovePartitionCtx, 0, Math.Min(cols, ctx.AbovePartitionCtx.Length));
        Array.Clear(ctx.AboveSkipCtx, 0, Math.Min(cols, ctx.AboveSkipCtx.Length));
        Array.Clear(ctx.AboveSegpredCtx, 0, Math.Min(cols, ctx.AboveSegpredCtx.Length));

        // Mode context: cols * 2 for keyframe (4x4 units), cols for inter
        if (h.Keyframe != 0 || h.IntraOnly != 0)
            Array.Fill(ctx.AboveModeCtx, (byte)Vp9IntraMode.Dc, 0, Math.Min(cols * 2, ctx.AboveModeCtx.Length));
        else
            Array.Fill(ctx.AboveModeCtx, (byte)10 /*NEARESTMV*/, 0, Math.Min(cols, ctx.AboveModeCtx.Length));

        // NNZ contexts: sb_cols * 16 for Y, sb_cols * 16 >> ss_h for UV
        Array.Clear(ctx.AboveYNnzCtx);
        Array.Clear(ctx.AboveUvNnzCtx0);
        Array.Clear(ctx.AboveUvNnzCtx1);
    }

    /// <summary>Backup the bottom row of the current superblock row for intra prediction of the next row.</summary>
    private void BackupIntraPredData(int yOff, int uvOff)
    {
        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        int lsY = curFrame.LumaStride;
        int lsUv = curFrame.ChromaStride;
        int ssV = ctx.SubsamplingY;
        int pixCols = ctx.SuperblockCols * 64;

        if (curFrame.LumaData != null && ctx.IntraPredDataY != null)
        {
            int srcOff = yOff + 63 * lsY;
            int copyLen = Math.Min(pixCols, curFrame.LumaData.Length - srcOff);
            if (copyLen > 0 && copyLen <= ctx.IntraPredDataY.Length)
                curFrame.LumaData.AsSpan(srcOff, copyLen).CopyTo(ctx.IntraPredDataY);
        }

        int chromaRow = (64 >> ssV) - 1;
        int chromaCols = pixCols >> ctx.SubsamplingX;
        if (curFrame.ChromaUData != null && ctx.IntraPredDataU != null)
        {
            int srcOff = uvOff + chromaRow * lsUv;
            int copyLen = Math.Min(chromaCols, curFrame.ChromaUData.Length - srcOff);
            if (copyLen > 0 && copyLen <= ctx.IntraPredDataU.Length)
                curFrame.ChromaUData.AsSpan(srcOff, copyLen).CopyTo(ctx.IntraPredDataU);
        }
        if (curFrame.ChromaVData != null && ctx.IntraPredDataV != null)
        {
            int srcOff = uvOff + chromaRow * lsUv;
            int copyLen = Math.Min(chromaCols, curFrame.ChromaVData.Length - srcOff);
            if (copyLen > 0 && copyLen <= ctx.IntraPredDataV.Length)
                curFrame.ChromaVData.AsSpan(srcOff, copyLen).CopyTo(ctx.IntraPredDataV);
        }
    }

    /// <summary>Parse the uncompressed and compressed frame headers.</summary>
    /// <returns>Total bytes consumed (uncompressed + compressed header).</returns>
    private int DecodeFrameHeader(ReadOnlySpan<byte> data, ref int showExistingRef)
    {
        ref var h = ref ctx.Header;

        InitBitReader(data);

        // Frame marker — must be 0x2
        if (ReadBits(2) != 2)
            return -1; // Invalid frame marker

        // Profile
        byte profile = (byte)ReadBits(1);
        profile |= (byte)(ReadBits(1) << 1);
        if (profile == 3)
            profile += (byte)ReadBits(1);
        if (profile > 3)
            return -1; // Unsupported profile
        h.Profile = profile;

        // Show existing frame — re-show a previously decoded reference frame.
        // Do NOT modify h.Invisible here — the header state should reflect the
        // last decoded frame (not this show-existing), so that lastInvisible
        // is correct when the next real frame parses UseLastFrameMvs.
        if (ReadBit())
        {
            showExistingRef = ReadBits(3);
            return (bitOffset + 7) / 8;
        }

        ctx.LastKeyframe = h.Keyframe;
        h.Keyframe = (byte)(ReadBit() ? 0 : 1); // 0 = keyframe (inverted!)

        byte lastInvisible = h.Invisible;
        h.Invisible = (byte)(ReadBit() ? 0 : 1);
        h.ErrorRes = (byte)(ReadBit() ? 1 : 0);
        h.UseLastFrameMvs = (byte)(h.ErrorRes == 0 && lastInvisible == 0 ? 1 : 0);
        if (DiagDisableTemporalMvs) h.UseLastFrameMvs = 0;

        int w, ht;

        if (h.Keyframe != 0)
        {
            // Keyframe
            if (ReadBits(24) != Vp9SyncCode)
                return -1; // Invalid sync code

            ReadColorSpaceDetails(ref h);
            h.RefreshRefMask = 0xFF;
            w = ReadBits(16) + 1;
            ht = ReadBits(16) + 1;
            if (ReadBit()) // display size
                SkipBits(32);
        }
        else
        {
            h.IntraOnly = (byte)(h.Invisible != 0 ? (ReadBit() ? 1 : 0) : 0);
            h.ResetCtx = (byte)(h.ErrorRes != 0 ? 0 : ReadBits(2));

            if (h.IntraOnly != 0)
            {
                if (ReadBits(24) != Vp9SyncCode)
                    return -1;

                if (profile >= 1)
                {
                    ReadColorSpaceDetails(ref h);
                }
                else
                {
                    ctx.SubsamplingX = 1;
                    ctx.SubsamplingY = 1;
                    h.Bpp = 8;
                    ctx.BytesPerPixel = 1;
                    ctx.BitsPerComponent = 8;
                }

                h.RefreshRefMask = (byte)ReadBits(8);
                w = ReadBits(16) + 1;
                ht = ReadBits(16) + 1;
                if (ReadBit()) SkipBits(32);
            }
            else
            {
                // Inter frame
                h.RefreshRefMask = (byte)ReadBits(8);
                h.RefIdx0 = (byte)ReadBits(3);
                h.SignBias0 = (byte)(ReadBit() && h.ErrorRes == 0 ? 1 : 0);
                h.RefIdx1 = (byte)ReadBits(3);
                h.SignBias1 = (byte)(ReadBit() && h.ErrorRes == 0 ? 1 : 0);
                h.RefIdx2 = (byte)ReadBits(3);
                h.SignBias2 = (byte)(ReadBit() && h.ErrorRes == 0 ? 1 : 0);

                // Check references are valid
                if (!ctx.Refs[h.RefIdx0].Valid ||
                    !ctx.Refs[h.RefIdx1].Valid ||
                    !ctx.Refs[h.RefIdx2].Valid)
                    return -1; // Not all references available

                // Size from reference or explicit
                if (ReadBit())
                {
                    w = ctx.Refs[h.RefIdx0].Width;
                    ht = ctx.Refs[h.RefIdx0].Height;
                }
                else if (ReadBit())
                {
                    w = ctx.Refs[h.RefIdx1].Width;
                    ht = ctx.Refs[h.RefIdx1].Height;
                }
                else if (ReadBit())
                {
                    w = ctx.Refs[h.RefIdx2].Width;
                    ht = ctx.Refs[h.RefIdx2].Height;
                }
                else
                {
                    w = ReadBits(16) + 1;
                    ht = ReadBits(16) + 1;
                }

                // Check use_last_frame_mvs validity
                var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
                if (!curFrame.Valid || curFrame.Width != w || curFrame.Height != ht)
                    h.UseLastFrameMvs = 0;

                if (ReadBit()) SkipBits(32); // display size

                h.HighPrecisionMvs = (byte)(ReadBit() ? 1 : 0);
                h.FilterMode = (byte)(ReadBit() ? 4 /* SWITCHABLE */ : ReadBits(2));

                h.AllowCompInter = (byte)(
                    (h.SignBias0 != h.SignBias1 || h.SignBias0 != h.SignBias2) ? 1 : 0);

                if (h.AllowCompInter != 0)
                {
                    if (h.SignBias0 == h.SignBias1)
                    {
                        h.FixCompRef = 2;
                        h.VarCompRef0 = 0;
                        h.VarCompRef1 = 1;
                    }
                    else if (h.SignBias0 == h.SignBias2)
                    {
                        h.FixCompRef = 1;
                        h.VarCompRef0 = 0;
                        h.VarCompRef1 = 2;
                    }
                    else
                    {
                        h.FixCompRef = 0;
                        h.VarCompRef0 = 1;
                        h.VarCompRef1 = 2;
                    }
                }
            }
        }

        // Update dimensions
        ctx.Width = w;
        ctx.Height = ht;
        ctx.AllocateContextArrays();

        h.RefreshCtx = (byte)(h.ErrorRes != 0 ? 0 : (ReadBit() ? 1 : 0));
        h.ParallelMode = (byte)(h.ErrorRes != 0 ? 1 : (ReadBit() ? 1 : 0));
        byte frameCtxId = (byte)ReadBits(2);
        h.FrameCtxId = frameCtxId;
        if (h.Keyframe != 0 || h.IntraOnly != 0)
            h.FrameCtxId = 0; // libvpx ignores this for keyframes

        // Loop filter header
        if (h.Keyframe != 0 || h.ErrorRes != 0 || h.IntraOnly != 0)
        {
            // Reset loopfilter defaults
            h.LfDeltaRef0 = 1;
            h.LfDeltaRef1 = 0;
            h.LfDeltaRef2 = -1;
            h.LfDeltaRef3 = -1;
            h.LfDeltaMode0 = 0;
            h.LfDeltaMode1 = 0;
            h.Segmentation = default;
        }

        h.FilterLevel = (byte)ReadBits(6);
        if (DiagSkipLoopFilter) Console.WriteLine($"[DIAG] FilterLevel={h.FilterLevel}");
        byte sharp = (byte)ReadBits(3);
        if (h.Sharpness != sharp)
        {
            ctx.BuildFilterLookupTables(h.FilterLevel, sharp);
            h.Sharpness = sharp;
        }

        if (ReadBit()) // lf_delta.enabled
        {
            h.LfDeltaEnabled = 1;
            if (ReadBit()) // lf_delta.updated
            {
                h.LfDeltaUpdated = 1;
                for (int i = 0; i < 4; i++)
                    if (ReadBit())
                        SetLfDeltaRef(ref h, i, ReadSignedBitsInv(6));

                for (int i = 0; i < 2; i++)
                    if (ReadBit())
                        SetLfDeltaMode(ref h, i, ReadSignedBitsInv(6));
            }
        }
        else
        {
            h.LfDeltaEnabled = 0;
        }

        // Quantization header
        h.YacQi = (byte)ReadBits(8);
        h.YdcQdelta = (sbyte)(ReadBit() ? ReadSignedBitsInv(4) : 0);
        h.UvdcQdelta = (sbyte)(ReadBit() ? ReadSignedBitsInv(4) : 0);
        h.UvacQdelta = (sbyte)(ReadBit() ? ReadSignedBitsInv(4) : 0);
        h.Lossless = (byte)(h.YacQi == 0 && h.YdcQdelta == 0 &&
                            h.UvdcQdelta == 0 && h.UvacQdelta == 0 ? 1 : 0);

        // Segmentation header — FFmpeg uses assignment-in-condition: if ((var = get_bits1()))
        // so all fields are ALWAYS set from the bitstream. We must do the same to avoid
        // stale values from previous frames corrupting the bool decoder.
        h.Segmentation.Enabled = (byte)(ReadBit() ? 1 : 0);
        if (h.Segmentation.Enabled != 0)
        {
            h.Segmentation.UpdateMap = (byte)(ReadBit() ? 1 : 0);
            if (h.Segmentation.UpdateMap != 0)
            {
                for (int i = 0; i < 7; i++)
                    h.Segmentation.SetProb(i, ReadBit() ? (byte)ReadBits(8) : (byte)255);

                h.Segmentation.Temporal = (byte)(ReadBit() ? 1 : 0);
                if (h.Segmentation.Temporal != 0)
                {
                    for (int i = 0; i < 3; i++)
                        h.Segmentation.SetPredProb(i, ReadBit() ? (byte)ReadBits(8) : (byte)255);
                }
            }

            if (ReadBit()) // segment data update
            {
                h.Segmentation.AbsoluteVals = (byte)(ReadBit() ? 1 : 0);
                for (int i = 0; i < 8; i++)
                {
                    ref var feat = ref h.Segmentation.GetFeatMut(i);
                    feat.QEnabled = (byte)(ReadBit() ? 1 : 0);
                    if (feat.QEnabled != 0)
                        feat.QVal = (short)ReadSignedBitsInv(8);
                    feat.LfEnabled = (byte)(ReadBit() ? 1 : 0);
                    if (feat.LfEnabled != 0)
                        feat.LfVal = (sbyte)ReadSignedBitsInv(6);
                    feat.RefEnabled = (byte)(ReadBit() ? 1 : 0);
                    if (feat.RefEnabled != 0)
                        feat.RefVal = (byte)ReadBits(2);
                    feat.SkipEnabled = (byte)(ReadBit() ? 1 : 0);
                }
            }
        }
        else
        {
            h.Segmentation.Temporal = 0;
            h.Segmentation.UpdateMap = 0;
        }

        // Calculate qmul and lflvl for each segment
        ComputeSegmentQuantizers(ref h);

        // Tiling
        ComputeTiling(ref h);

        // Check inter-frame reference validity and compute MV scale
        if (h.Keyframe == 0 && h.IntraOnly == 0)
        {
            if (!ComputeMvScale(ref h))
                return -1;
        }

        // Reset probability contexts if needed
        ResetProbContextsIfNeeded(ref h, frameCtxId);

        // Compressed header size
        h.CompressedHeaderSize = (ushort)ReadBits(16);
        h.UncompressedHeaderSize = (ushort)((bitOffset + 7) / 8);

        // Parse compressed header
        int compHeaderStart = AlignBitOffset();
        if (h.CompressedHeaderSize > data.Length - compHeaderStart)
            return -1;

        ReadOnlySpan<byte> compData = data.Slice(compHeaderStart, h.CompressedHeaderSize);
        int ret = DecodeCompressedHeader(compData, ref h, frameCtxId);
        if (ret < 0)
            return ret;

        return compHeaderStart + h.CompressedHeaderSize;
    }

    // ==================== Compressed header parsing ====================

    /// <summary>Parse the compressed (arithmetic-coded) frame header.</summary>
    private int DecodeCompressedHeader(ReadOnlySpan<byte> data, ref Vp9BitstreamHeader h, int frameCtxId)
    {
        ref var c = ref compressedHeaderDecoder;
        c.Init(data);

        // Marker bit
        if (c.GetProbBranchy(128))
            return -1; // Marker bit was set

        // Reset counts
        ctx.Counts.Clear();

        // Copy base probabilities from selected context
        ctx.ActiveProb.CopyFrom(ctx.ProbCtx[frameCtxId]);

        // Transform mode
        if (h.Lossless != 0)
        {
            h.TxfmMode = 0; // TX_4X4
        }
        else
        {
            h.TxfmMode = (byte)c.GetUint(2);
            if (h.TxfmMode == 3)
                h.TxfmMode += (byte)c.Get();

            if (h.TxfmMode == 4) // TX_SWITCHABLE
            {
                var tx8p = ctx.ActiveProb.Tx8p;
                var tx16p = ctx.ActiveProb.Tx16p;
                var tx32p = ctx.ActiveProb.Tx32p;

                for (int i = 0; i < 2; i++)
                    if (c.GetProbBranchy(252))
                        tx8p[i] = UpdateProb(ref c, tx8p[i]);
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        if (c.GetProbBranchy(252))
                            tx16p[i * 2 + j] = UpdateProb(ref c, tx16p[i * 2 + j]);
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 3; j++)
                        if (c.GetProbBranchy(252))
                            tx32p[i * 3 + j] = UpdateProb(ref c, tx32p[i * 3 + j]);
            }
        }

        // Coefficient probability updates
        DecodeCoefProbUpdates(ref c, ref h, frameCtxId);

        // Skip probability updates
        var skip = ctx.ActiveProb.Skip;
        for (int i = 0; i < 3; i++)
            if (c.GetProbBranchy(252))
                skip[i] = UpdateProb(ref c, skip[i]);

        if (h.Keyframe == 0 && h.IntraOnly == 0)
        {
            DecodeInterProbUpdates(ref c, ref h);
        }

        return 0;
    }

    /// <summary>Decode coefficient probability updates from compressed header.</summary>
    private void DecodeCoefProbUpdates(ref Vp9BoolDecoder c, ref Vp9BitstreamHeader h, int frameCtxId)
    {
        var coef = ctx.ActiveProb.Coef;
        var refCoef = ctx.ProbCtx[frameCtxId].Coef;

        for (int i = 0; i < 4; i++) // tx sizes
        {
            int updateFlag = c.Get();
            int updateCount = 0;
            if (DiagTraceCoefs)
                System.Console.WriteLine($"  COEF_UPDATE tx={i}: flag={updateFlag} boolState={c.GetState()}");

            if (updateFlag != 0) // update flag
            {
                for (int j = 0; j < 2; j++) // block type
                    for (int k = 0; k < 2; k++) // ref type (intra/inter)
                        for (int l = 0; l < 6; l++) // band
                            for (int m = 0; m < 6; m++) // position
                            {
                                if (m >= 3 && l == 0) break; // dc only has 3 pt
                                int idx = (((i * 2 + j) * 2 + k) * 6 + l) * 6 + m;
                                int baseIdx = idx * 3;
                                int refBase = idx * 3;
                                for (int n = 0; n < 3; n++)
                                {
                                    if (c.GetProbBranchy(252))
                                    {
                                        byte oldVal = refCoef[refBase + n];
                                        coef[baseIdx + n] = UpdateProb(ref c, refCoef[refBase + n]);
                                        updateCount++;
                                        if (DiagTraceCoefs)
                                            System.Console.WriteLine($"    UPDATE tx={i} [{j},{k},{l},{m},{n}]: ref={oldVal} -> {coef[baseIdx + n]} boolState={c.GetState()}");
                                    }
                                    else
                                    {
                                        coef[baseIdx + n] = refCoef[refBase + n];
                                    }
                                }
                            }
            }
            else
            {
                for (int j = 0; j < 2; j++)
                    for (int k = 0; k < 2; k++)
                        for (int l = 0; l < 6; l++)
                            for (int m = 0; m < 6; m++)
                            {
                                if (m > 3 && l == 0) break;
                                int idx = (((i * 2 + j) * 2 + k) * 6 + l) * 6 + m;
                                int baseIdx = idx * 3;
                                int refBase = idx * 3;
                                coef[baseIdx] = refCoef[refBase];
                                coef[baseIdx + 1] = refCoef[refBase + 1];
                                coef[baseIdx + 2] = refCoef[refBase + 2];
                            }
            }

            if (DiagTraceCoefs)
            {
                int checkIdx = 144 * 3; // TX_8X8,Y,Intra,band0,nnz0
                System.Console.WriteLine($"  After tx={i}: updates={updateCount} coef[432..434]={coef[checkIdx]},{coef[checkIdx+1]},{coef[checkIdx+2]} (ref={refCoef[checkIdx]},{refCoef[checkIdx+1]},{refCoef[checkIdx+2]})");
            }

            if (h.TxfmMode == i)
                break;
        }

        // Expand 3-value coef probs to 11-value using pareto8 model
        ctx.ExpandCoefProbs();

    }

    /// <summary>Decode inter-frame probability updates from compressed header.</summary>
    private void DecodeInterProbUpdates(ref Vp9BoolDecoder c, ref Vp9BitstreamHeader h)
    {
        var prob = ctx.ActiveProb;

        // MV mode
        for (int i = 0; i < 7; i++)
            for (int j = 0; j < 3; j++)
                if (c.GetProbBranchy(252))
                    prob.MvMode[i * 3 + j] = UpdateProb(ref c, prob.MvMode[i * 3 + j]);

        // Filter
        if (h.FilterMode == 4) // FILTER_SWITCHABLE
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    if (c.GetProbBranchy(252))
                        prob.Filter[i * 2 + j] = UpdateProb(ref c, prob.Filter[i * 2 + j]);

        // Intra
        for (int i = 0; i < 4; i++)
            if (c.GetProbBranchy(252))
                prob.Intra[i] = UpdateProb(ref c, prob.Intra[i]);

        // Compound prediction mode
        if (h.AllowCompInter != 0)
        {
            h.CompPredMode = (byte)c.Get();
            if (h.CompPredMode != 0)
                h.CompPredMode += (byte)c.Get();

            if (h.CompPredMode == 2) // PRED_SWITCHABLE
                for (int i = 0; i < 5; i++)
                    if (c.GetProbBranchy(252))
                        prob.Comp[i] = UpdateProb(ref c, prob.Comp[i]);
        }
        else
        {
            h.CompPredMode = 0; // PRED_SINGLEREF
        }

        // Single ref
        if (h.CompPredMode != 1) // not PRED_COMPREF
            for (int i = 0; i < 5; i++)
            {
                if (c.GetProbBranchy(252))
                    prob.SingleRef[i * 2] = UpdateProb(ref c, prob.SingleRef[i * 2]);
                if (c.GetProbBranchy(252))
                    prob.SingleRef[i * 2 + 1] = UpdateProb(ref c, prob.SingleRef[i * 2 + 1]);
            }

        // Comp ref
        if (h.CompPredMode != 0) // not PRED_SINGLEREF
            for (int i = 0; i < 5; i++)
                if (c.GetProbBranchy(252))
                    prob.CompRef[i] = UpdateProb(ref c, prob.CompRef[i]);

        // Y mode
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 9; j++)
                if (c.GetProbBranchy(252))
                    prob.YMode[i * 9 + j] = UpdateProb(ref c, prob.YMode[i * 9 + j]);

        // Partition (note: index [3-i] for the level dimension)
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 3; k++)
                    if (c.GetProbBranchy(252))
                        prob.Partition[(3 - i) * 12 + j * 3 + k] =
                            UpdateProb(ref c, prob.Partition[(3 - i) * 12 + j * 3 + k]);

        // MV updates (these use direct value, not update_prob subexp model)
        for (int i = 0; i < 3; i++)
            if (c.GetProbBranchy(252))
                prob.MvJoint[i] = (byte)((c.GetUint(7) << 1) | 1);

        for (int i = 0; i < 2; i++)
        {
            int comp = i * 33; // offset into MvComp array
            if (c.GetProbBranchy(252))
                prob.MvComp[comp + 0] = (byte)((c.GetUint(7) << 1) | 1); // sign

            for (int j = 0; j < 10; j++)
                if (c.GetProbBranchy(252))
                    prob.MvComp[comp + 1 + j] = (byte)((c.GetUint(7) << 1) | 1); // classes

            if (c.GetProbBranchy(252))
                prob.MvComp[comp + 11] = (byte)((c.GetUint(7) << 1) | 1); // class0

            for (int j = 0; j < 10; j++)
                if (c.GetProbBranchy(252))
                    prob.MvComp[comp + 12 + j] = (byte)((c.GetUint(7) << 1) | 1); // bits
        }

        for (int i = 0; i < 2; i++)
        {
            int comp = i * 33;
            // class0_fp[2][3]
            for (int j = 0; j < 2; j++)
                for (int k = 0; k < 3; k++)
                    if (c.GetProbBranchy(252))
                        prob.MvComp[comp + 22 + j * 3 + k] = (byte)((c.GetUint(7) << 1) | 1);

            // fp[3]
            for (int j = 0; j < 3; j++)
                if (c.GetProbBranchy(252))
                    prob.MvComp[comp + 28 + j] = (byte)((c.GetUint(7) << 1) | 1);
        }

        if (h.HighPrecisionMvs != 0)
        {
            for (int i = 0; i < 2; i++)
            {
                int comp = i * 33;
                if (c.GetProbBranchy(252))
                    prob.MvComp[comp + 31] = (byte)((c.GetUint(7) << 1) | 1); // class0_hp
                if (c.GetProbBranchy(252))
                    prob.MvComp[comp + 32] = (byte)((c.GetUint(7) << 1) | 1); // hp
            }
        }
    }

    // ==================== Helper functions ====================

    /// <summary>Differential probability update (subexp model).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UpdateProb(ref Vp9BoolDecoder c, int p)
    {
        int d;
        if (c.Get() == 0)
        {
            d = c.GetUint(4);
        }
        else if (c.Get() == 0)
        {
            d = c.GetUint(4) + 16;
        }
        else if (c.Get() == 0)
        {
            d = c.GetUint(5) + 32;
        }
        else
        {
            d = c.GetUint(7);
            if (d >= 65)
                d = (d << 1) - 65 + c.Get();
            d += 64;
        }

        int mapped = Vp9Data.InvMapTable[d];
        int result = p <= 128
            ? 1 + InvRecenterNonneg(mapped, p - 1)
            : 255 - InvRecenterNonneg(mapped, 255 - p);
        return (byte)result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InvRecenterNonneg(int v, int m)
    {
        if (v > 2 * m)
            return v;
        if ((v & 1) != 0)
            return m - ((v + 1) >> 1);
        return m + (v >> 1);
    }

    private void ReadColorSpaceDetails(ref Vp9BitstreamHeader h)
    {
        int bits = h.Profile <= 1 ? 0 : 1 + ReadBits(1); // 0=8bit, 1=10bit, 2=12bit
        h.Bpp = (byte)(8 + bits * 2);
        ctx.BytesPerPixel = (byte)((7 + h.Bpp) >> 3);
        ctx.BitsPerComponent = h.Bpp;

        // REFUSED, not approximated. Everything upstream of the pixels is ported for 10 and
        // 12 bit - the header, the quantiser tables, the pixel formats, the buffer sizing and
        // the copy out - and ALL of it worked: the five profile 2 and 3 vectors came out at
        // the right geometry, the right pixel format and the right byte count. Not one of
        // their frames matched ffmpeg, including the first keyframe, because every DSP kernel
        // in this decoder takes Span<byte>: the inverse transforms, intra prediction, the
        // eight-tap filters and the loop filter all read a 16-bit sample as two 8-bit ones.
        // FFmpeg instantiates vp9dsp.c twice more, as vp9dsp_10bpp.c and vp9dsp_12bpp.c, and
        // that is the work this needs. Until then a profile 2 or 3 stream must fail here
        // rather than hand out confident noise - 86,400 bytes of it per frame, correctly
        // labelled.
        if (bits != 0)
            throw new NotSupportedException(
                $"VP9: profile {h.Profile} is {h.Bpp}-bit. This decoder's DSP kernels are "
              + "eight-bit only (FFmpeg's vp9dsp_10bpp.c and vp9dsp_12bpp.c have no "
              + "counterpart here), so the picture would be wrong in every sample.");

        int colorspace = ReadBits(3);
        // Colorspace 7 = RGB
        if (colorspace == 7)
        {
            // pix_fmt_rgb[bits] in the C. The three planes are G, B and R at full
            // resolution, which is what GBRP means, and the colour range is always full.
            ctx.SubsamplingX = 0;
            ctx.SubsamplingY = 0;
            ctx.OutputPixelFormat = bits switch
            {
                0 => PixelFormat.Gbrp,
                1 => PixelFormat.Gbrp10,
                _ => throw new NotSupportedException(
                         "VP9: 12-bit RGB (AV_PIX_FMT_GBRP12) is a pixel format this "
                       + "project does not have."),
            };

            if ((h.Profile & 1) != 0)
            {
                if (ReadBits(1) != 0)
                    throw new InvalidOperationException("VP9: reserved bit set in RGB.");
            }
            else
            {
                // The C refuses this rather than decoding it: RGB exists only in the odd
                // profiles, so an even-profile stream claiming it is malformed.
                throw new InvalidOperationException(
                    $"VP9: RGB not supported in profile {h.Profile}.");
            }
        }
        else
        {
            ReadBits(1); // color_range
            if ((h.Profile & 1) != 0)
            {
                ctx.SubsamplingX = (byte)ReadBits(1);
                ctx.SubsamplingY = (byte)ReadBits(1);
                ctx.OutputPixelFormat = PixFmtForSubsampling(
                    bits, ctx.SubsamplingY, ctx.SubsamplingX, h.Profile);

                // 4:2:0 in an odd profile is malformed - it is what the even profiles are
                // for - and the C returns AVERROR_INVALIDDATA for it.
                if (ctx.SubsamplingX == 1 && ctx.SubsamplingY == 1)
                    throw new InvalidOperationException(
                        $"VP9: YUV 4:2:0 not supported in profile {h.Profile}.");

                if (ReadBits(1) != 0)
                    throw new InvalidOperationException(
                        $"VP9: profile {h.Profile} color details reserved bit set.");
            }
            else
            {
                ctx.SubsamplingX = 1;
                ctx.SubsamplingY = 1;
                ctx.OutputPixelFormat = PixFmtForSubsampling(bits, 1, 1, h.Profile);
            }
        }
    }

    /// <summary>
    /// <c>pix_fmt_for_ss[bits][ss_v][ss_h]</c>, which is the C's table of twelve. The 4:4:0
    /// column is the one this project has no PixelFormat for; it throws rather than being
    /// read as something close.
    /// </summary>
    private static PixelFormat PixFmtForSubsampling(int bits, int ssV, int ssH, int profile)
    {
        if (ssV == 1 && ssH == 0)
            throw new NotSupportedException(
                $"VP9: profile {profile} 4:4:0 (AV_PIX_FMT_YUV440P) is a pixel format this "
              + "project does not have.");

        return (bits, ssV, ssH) switch
        {
            (0, 0, 0) => PixelFormat.Yuv444P,
            (0, 0, 1) => PixelFormat.Yuv422P,
            (0, 1, 1) => PixelFormat.Yuv420P,
            (1, 0, 0) => PixelFormat.Yuv444P10,
            (1, 0, 1) => PixelFormat.Yuv422P10,
            (1, 1, 1) => PixelFormat.Yuv420P10,
            (2, 0, 0) => PixelFormat.Yuv444P12,
            (2, 0, 1) => PixelFormat.Yuv422P12,
            (2, 1, 1) => PixelFormat.Yuv420P12,
            _ => throw new NotSupportedException(
                     $"VP9: {8 + bits * 2}-bit with ss_v {ssV} and ss_h {ssH} is in no row "
                   + "of pix_fmt_for_ss."),
        };
    }

    private void ComputeSegmentQuantizers(ref Vp9BitstreamHeader h)
    {
        int bppIndex = h.Bpp == 8 ? 0 : h.Bpp == 10 ? 1 : 2;
        ReadOnlySpan<short> dcQ = bppIndex switch
        {
            0 => Vp9Data.DcQLookup8Bit,
            1 => Vp9Data.DcQLookup10Bit,
            _ => Vp9Data.DcQLookup12Bit,
        };
        ReadOnlySpan<short> acQ = bppIndex switch
        {
            0 => Vp9Data.AcQLookup8Bit,
            1 => Vp9Data.AcQLookup10Bit,
            _ => Vp9Data.AcQLookup12Bit,
        };

        int segCount = h.Segmentation.Enabled != 0 ? 8 : 1;
        for (int i = 0; i < segCount; i++)
        {
            ref var feat = ref h.Segmentation.GetFeatMut(i);
            int qyac;

            if (h.Segmentation.Enabled != 0 && feat.QEnabled != 0)
            {
                qyac = h.Segmentation.AbsoluteVals != 0
                    ? Clip(feat.QVal, 0, 255)
                    : Clip(h.YacQi + feat.QVal, 0, 255);
            }
            else
            {
                qyac = h.YacQi;
            }

            int qydc = Clip(qyac + h.YdcQdelta, 0, 255);
            int quvdc = Clip(qyac + h.UvdcQdelta, 0, 255);
            int quvac = Clip(qyac + h.UvacQdelta, 0, 255);
            qyac = Clip(qyac, 0, 255);

            feat.QmulYDc = dcQ[qydc];
            feat.QmulYAc = acQ[qyac];
            feat.QmulUvDc = dcQ[quvdc];
            feat.QmulUvAc = acQ[quvac];

            if (DiagTraceSegQuant)
                Console.WriteLine($"[SEG-Q] seg={i} QEnabled={feat.QEnabled} QVal={feat.QVal} absVal={h.Segmentation.AbsoluteVals} " +
                    $"baseQi={h.YacQi} qyac={qyac} qydc={qydc} quvdc={quvdc} quvac={quvac} " +
                    $"QmulYDc={feat.QmulYDc} QmulYAc={feat.QmulYAc} QmulUvDc={feat.QmulUvDc} QmulUvAc={feat.QmulUvAc}");

            // Loop filter levels
            int sh = h.FilterLevel >= 32 ? 1 : 0;
            int lflvl;
            if (h.Segmentation.Enabled != 0 && feat.LfEnabled != 0)
            {
                lflvl = h.Segmentation.AbsoluteVals != 0
                    ? Clip(feat.LfVal, 0, 63)
                    : Clip(h.FilterLevel + feat.LfVal, 0, 63);
            }
            else
            {
                lflvl = h.FilterLevel;
            }

            if (DiagTraceSegQuant)
                Console.WriteLine($"[SEG-LF] seg={i} LfEnabled={feat.LfEnabled} LfVal={feat.LfVal} baseLf={h.FilterLevel} lflvl={lflvl} deltaEnabled={h.LfDeltaEnabled}");

            if (h.LfDeltaEnabled != 0)
            {
                feat.LflvlRef0Mode0 = (byte)Clip(lflvl + (h.LfDeltaRef0 << sh), 0, 63);
                feat.LflvlRef0Mode1 = feat.LflvlRef0Mode0;
                for (int j = 1; j < 4; j++)
                {
                    int refDelta = GetLfDeltaRef(ref h, j);
                    byte m0 = (byte)Clip(lflvl + ((refDelta + h.LfDeltaMode0) << sh), 0, 63);
                    byte m1 = (byte)Clip(lflvl + ((refDelta + h.LfDeltaMode1) << sh), 0, 63);
                    SetLflvl(ref feat, j, m0, m1);
                }
            }
            else
            {
                byte l = (byte)lflvl;
                feat.LflvlRef0Mode0 = l; feat.LflvlRef0Mode1 = l;
                feat.LflvlRef1Mode0 = l; feat.LflvlRef1Mode1 = l;
                feat.LflvlRef2Mode0 = l; feat.LflvlRef2Mode1 = l;
                feat.LflvlRef3Mode0 = l; feat.LflvlRef3Mode1 = l;
            }
        }
    }

    private void ComputeTiling(ref Vp9BitstreamHeader h)
    {
        int sbCols = ctx.SuperblockCols;

        int log2TileCols = 0;
        while (sbCols > (64 << log2TileCols))
            log2TileCols++;

        int max = 0;
        while ((sbCols >> max) >= 4)
            max++;
        max = Math.Max(0, max - 1);

        while (max > log2TileCols)
        {
            if (ReadBit())
                log2TileCols++;
            else
                break;
        }

        h.Log2TileCols = (byte)log2TileCols;
        h.TileCols = (ushort)(1 << log2TileCols);

        // decode012: 0, or 1 + bit
        int log2TileRows;
        if (!ReadBit())
            log2TileRows = 0;
        else
            log2TileRows = 1 + (ReadBit() ? 1 : 0);

        h.Log2TileRows = (byte)log2TileRows;
        h.TileRows = (ushort)(1 << log2TileRows);
    }

    private bool ComputeMvScale(ref Vp9BitstreamHeader h)
    {
        int validRefCount = 0;
        // A span, so the collection expression becomes an inline array on the stack. As a byte[] this
        // was a three-element array allocated for every inter frame, purely to index three fields in
        // a loop.
        ReadOnlySpan<byte> refIdxs = [h.RefIdx0, h.RefIdx1, h.RefIdx2];

        for (int i = 0; i < 3; i++)
        {
            var refFrame = ctx.Refs[refIdxs[i]];
            int refw = refFrame.Width;
            int refh = refFrame.Height;

            if (refw == ctx.Width && refh == ctx.Height)
            {
                ctx.MvScale[i * 2] = 0;
                ctx.MvScale[i * 2 + 1] = 0;
            }
            else
            {
                if (ctx.Width * 2 < refw || ctx.Height * 2 < refh ||
                    ctx.Width > 16 * refw || ctx.Height > 16 * refh)
                {
                    ctx.MvScale[i * 2] = 0xFFFF;
                    ctx.MvScale[i * 2 + 1] = 0xFFFF;
                    continue;
                }
                ctx.MvScale[i * 2] = (ushort)((refw << 14) / ctx.Width);
                ctx.MvScale[i * 2 + 1] = (ushort)((refh << 14) / ctx.Height);
                ctx.MvStep[i * 2] = (byte)(16 * ctx.MvScale[i * 2] >> 14);
                ctx.MvStep[i * 2 + 1] = (byte)(16 * ctx.MvScale[i * 2 + 1] >> 14);
            }
            validRefCount++;
        }

        return validRefCount > 0;
    }

    private void ResetProbContextsIfNeeded(ref Vp9BitstreamHeader h, int frameCtxId)
    {
        if (h.Keyframe != 0 || h.ErrorRes != 0 || (h.IntraOnly != 0 && h.ResetCtx == 3))
        {
            for (int i = 0; i < 4; i++)
                ctx.ProbCtx[i].ResetToDefaults();
        }
        else if (h.IntraOnly != 0 && h.ResetCtx == 2)
        {
            ctx.ProbCtx[frameCtxId].ResetToDefaults();
        }
    }

    // ==================== Bit reader (MSB-first, uncompressed header) ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitBitReader(ReadOnlySpan<byte> data)
    {
        StoreFrameData(data);
        bitOffset = 0;
    }

    /// <summary>Copies <paramref name="data"/> into the reusable frame buffer for the bit reader.</summary>
    private void StoreFrameData(ReadOnlySpan<byte> data)
    {
        if (frameDataBuffer is null || frameDataBuffer.Length < data.Length)
            frameDataBuffer = new byte[Math.Max(data.Length, Math.Max(65536, frameDataBuffer is null ? 0 : frameDataBuffer.Length * 2))];

        data.CopyTo(frameDataBuffer);
        frameData = frameDataBuffer.AsMemory(0, data.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadBits(int n)
    {
        int result = 0;
        var span = frameData.Span;
        for (int i = 0; i < n; i++)
        {
            int byteIdx = bitOffset >> 3;
            int bitIdx = 7 - (bitOffset & 7);
            result = (result << 1) | ((span[byteIdx] >> bitIdx) & 1);
            bitOffset++;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ReadBit() => ReadBits(1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipBits(int n) => bitOffset += n;

    /// <summary>Read signed value where the sign bit comes AFTER the magnitude (VP9 convention).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadSignedBitsInv(int n)
    {
        int v = ReadBits(n);
        return ReadBit() ? -v : v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AlignBitOffset()
    {
        int bytePos = (bitOffset + 7) / 8;
        bitOffset = bytePos * 8;
        return bytePos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip(int v, int min, int max) => v < min ? min : v > max ? max : v;

    // ==================== Header field helpers ====================

    private static void SetLfDeltaRef(ref Vp9BitstreamHeader h, int i, int val)
    {
        switch (i)
        {
            case 0: h.LfDeltaRef0 = (sbyte)val; break;
            case 1: h.LfDeltaRef1 = (sbyte)val; break;
            case 2: h.LfDeltaRef2 = (sbyte)val; break;
            case 3: h.LfDeltaRef3 = (sbyte)val; break;
        }
    }

    private static int GetLfDeltaRef(ref Vp9BitstreamHeader h, int i) =>
        i switch { 0 => h.LfDeltaRef0, 1 => h.LfDeltaRef1, 2 => h.LfDeltaRef2, 3 => h.LfDeltaRef3, _ => 0 };

    private static void SetLfDeltaMode(ref Vp9BitstreamHeader h, int i, int val)
    {
        if (i == 0) h.LfDeltaMode0 = (sbyte)val;
        else h.LfDeltaMode1 = (sbyte)val;
    }

    private static void SetLflvl(ref Vp9SegmentFeatures feat, int j, byte m0, byte m1)
    {
        switch (j)
        {
            case 1: feat.LflvlRef1Mode0 = m0; feat.LflvlRef1Mode1 = m1; break;
            case 2: feat.LflvlRef2Mode0 = m0; feat.LflvlRef2Mode1 = m1; break;
            case 3: feat.LflvlRef3Mode0 = m0; feat.LflvlRef3Mode1 = m1; break;
        }
    }
}


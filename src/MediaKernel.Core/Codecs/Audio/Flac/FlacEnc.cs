using System;
using System.Security.Cryptography;

namespace MediaKernel.Core.Codecs.Audio.Flac;

/// <summary>Sample format handed to the FLAC encoder — FFmpeg's two accepted formats.</summary>
public enum FlacSampleFormat
{
    /// <summary>AV_SAMPLE_FMT_S16</summary>
    S16,
    /// <summary>AV_SAMPLE_FMT_S32</summary>
    S32,
}

/// <summary>FLAC_CHMODE_*: the stereo decorrelation modes.</summary>
public enum FlacChannelMode
{
    /// <summary>Auto — let the encoder estimate. Not a bitstream value.</summary>
    Auto = -1,
    /// <summary>FLAC_CHMODE_INDEPENDENT</summary>
    Independent = 0,
    /// <summary>FLAC_CHMODE_LEFT_SIDE</summary>
    LeftSide = 1,
    /// <summary>FLAC_CHMODE_RIGHT_SIDE</summary>
    RightSide = 2,
    /// <summary>FLAC_CHMODE_MID_SIDE</summary>
    MidSide = 3,
}

/// <summary>CompressionOptions — the AVOptions of FFmpeg's FLAC encoder.</summary>
public sealed class FlacCompressionOptions
{
    /// <summary>compression_level; -1 means the encoder default of 5.</summary>
    public int CompressionLevel = -1;
    /// <summary>block_time_ms, filled in from the compression level.</summary>
    public int BlockTimeMs;
    /// <summary>lpc_type</summary>
    public LpcType LpcType = LpcType.Default;
    /// <summary>lpc_passes</summary>
    public int LpcPasses = 2;
    /// <summary>lpc_coeff_precision</summary>
    public int LpcCoeffPrecision = 15;
    /// <summary>min_prediction_order; -1 means from the compression level.</summary>
    public int MinPredictionOrder = -1;
    /// <summary>max_prediction_order; -1 means from the compression level.</summary>
    public int MaxPredictionOrder = -1;
    /// <summary>prediction_order_method; -1 means from the compression level.</summary>
    public int PredictionOrderMethod = -1;
    /// <summary>min_partition_order; -1 means from the compression level.</summary>
    public int MinPartitionOrder = -1;
    /// <summary>max_partition_order; -1 means from the compression level.</summary>
    public int MaxPartitionOrder = -1;
    /// <summary>ch_mode</summary>
    public FlacChannelMode ChMode = FlacChannelMode.Auto;
    /// <summary>exact_rice_parameters</summary>
    public bool ExactRiceParameters;
    /// <summary>multi_dim_quant</summary>
    public bool MultiDimQuant;
}

/// <summary>
/// FFmpeg's <c>flacenc.c</c>: the FLAC encoder, translated function for function.
/// </summary>
/// <remarks>
/// <para>
/// The target here is byte-for-byte identity with <c>ffmpeg -c:a flac</c>, which is reachable
/// because every decision this encoder makes is deterministic integer or double arithmetic.
/// The one place that is not portable between builds is <c>lpc_compute_autocorr</c>, which
/// FFmpeg has SSE2/AVX versions of that sum in a different order; the gate therefore compares
/// against <c>-cpuflags 0</c>, which is the C this file was translated from.
/// </para>
/// <para>
/// The caller drives it the way libavcodec does: <see cref="EncodeFrame"/> once per block of
/// <see cref="FrameSize"/> samples (the last block may be shorter), then <see cref="Finish"/>,
/// which seals the MD5 and rewrites <see cref="Extradata"/>.
/// </para>
/// </remarks>
public sealed class FlacEnc
{
    /// <summary>FLAC_SUBFRAME_CONSTANT</summary>
    public const int SubframeConstant = 0;
    /// <summary>FLAC_SUBFRAME_VERBATIM</summary>
    public const int SubframeVerbatim = 1;
    /// <summary>FLAC_SUBFRAME_FIXED</summary>
    public const int SubframeFixed = 8;
    /// <summary>FLAC_SUBFRAME_LPC</summary>
    public const int SubframeLpc = 32;

    private const int MaxFixedOrder = 4;
    private const int MaxPartitionOrderConst = 8;
    private const int MaxPartitions = 1 << MaxPartitionOrderConst;
    private const int MinLpcShift = 0;
    private const int MaxLpcShift = 15;

    private const int FlacStreaminfoSize = 34;
    private const int FlacMaxChannels = 8;
    private const int FlacMinBlocksize = 16;
    private const int FlacMaxBlocksize = 65535;

    private const int CodingModeRice = 4;
    private const int CodingModeRice2 = 5;

    /// <summary>ff_flac_sample_rate_table</summary>
    private static readonly int[] SampleRateTable =
    [
        0, 88200, 176400, 192000, 8000, 16000, 22050, 24000,
        32000, 44100, 48000, 96000, 0, 0, 0, 0,
    ];

    /// <summary>ff_flac_blocksize_table</summary>
    private static readonly int[] BlocksizeTable =
    [
        0, 192, 576 << 0, 576 << 1, 576 << 2, 576 << 3, 0, 0,
        256 << 0, 256 << 1, 256 << 2, 256 << 3, 256 << 4, 256 << 5, 256 << 6, 256 << 7,
    ];

    /// <summary>RiceContext</summary>
    private sealed class RiceContext
    {
        public int CodingMode;
        public int Porder;
        public readonly int[] Params = new int[MaxPartitions];

        public void CopyFrom(RiceContext other)
        {
            CodingMode = other.CodingMode;
            Porder = other.Porder;
            Array.Copy(other.Params, Params, MaxPartitions);
        }
    }

    /// <summary>FlacSubframe</summary>
    private sealed class FlacSubframe
    {
        public int Type;
        public int TypeCode;
        public int Obits;
        public int Wasted;
        public int Order;
        public readonly int[] Coefs = new int[Lpc.MaxLpcOrder];
        public int Shift;

        public readonly RiceContext Rc = new();
        public readonly uint[] RcUdata;
        // uint64_t rc_sums[32][MAX_PARTITIONS], flattened with stride MAX_PARTITIONS.
        public readonly ulong[] RcSums = new ulong[32 * MaxPartitions];

        public readonly int[] Samples;
        public readonly int[] Residual;

        public FlacSubframe(int maxBlocksize)
        {
            RcUdata = new uint[maxBlocksize];
            // The C's samples[FLAC_MAX_BLOCKSIZE] is always longer than the block in it, which
            // the unrolled kernels quietly rely on: both read smp[i+1] on the last iteration of
            // a block whose length and predictor order have opposite parity. Sized to the exact
            // block, that read is off the end, so the slack is reproduced deliberately.
            Samples = new int[maxBlocksize + 11];
            // The C over-allocates by 11, because the unrolled LPC kernel writes res[i+1] on
            // the last iteration of an odd-length block.
            Residual = new int[maxBlocksize + 11];
        }
    }

    /// <summary>FlacFrame</summary>
    private sealed class FlacFrame
    {
        public readonly FlacSubframe[] Subframes;
        public readonly long[] Samples33Bps;
        public int Blocksize;
        public readonly int[] BsCode = new int[2];
        public int ChMode;
        public bool VerbatimOnly;

        public FlacFrame(int channels, int maxBlocksize)
        {
            Subframes = new FlacSubframe[channels];
            for (int i = 0; i < channels; i++)
                Subframes[i] = new FlacSubframe(maxBlocksize);
            Samples33Bps = new long[maxBlocksize];
        }
    }

    private int channels;
    private int samplerate;
    private readonly int[] srCode = new int[2];
    private int bpsCode;
    private int maxBlocksize;
    private int minFramesize;
    private int maxFramesize;
    private int maxEncodedFramesize;
    private uint frameCount;
    private ulong sampleCount;
    private readonly byte[] md5Sum = new byte[16];
    private FlacFrame frame;
    private readonly FlacCompressionOptions options;
    private readonly Lpc lpcCtx = new();
    private IncrementalHash md5Ctx;
    private byte[] md5Buffer = [];

    private FlacSampleFormat sampleFmt;
    private int bitsPerRawSample;
    private readonly PutBitContext pb = new([], 0);

    // Scratch that the C keeps on the stack of encode_residual_ch. Fields here, because the
    // C's locals are 4 KB and 128 bytes and this tree does not allocate per frame.
    private readonly int[] coefs = new int[Lpc.MaxLpcOrder * Lpc.MaxLpcOrder];
    private readonly int[] shift = new int[Lpc.MaxLpcOrder];
    private readonly int[] lpcTry = new int[Lpc.MaxLpcOrder];

    /// <summary>The STREAMINFO metadata block — FFmpeg's <c>avctx->extradata</c>.</summary>
    public byte[] Extradata { get; } = new byte[FlacStreaminfoSize];

    /// <summary>avctx->frame_size: the block size this encoder wants per call.</summary>
    public int FrameSize { get; private set; }

    /// <summary>avctx->bits_per_raw_sample, settled during initialisation.</summary>
    public int BitsPerRawSample => bitsPerRawSample;

    /// <summary>The largest frame this encoder can emit, for sizing the output buffer.</summary>
    public int MaxFramesize => maxFramesize;

    /// <summary>Creates an encoder. Call <see cref="Init"/> before encoding.</summary>
    public FlacEnc(FlacCompressionOptions options = null)
    {
        this.options = options ?? new FlacCompressionOptions();
    }

    /// <summary>write_streaminfo</summary>
    private void WriteStreaminfo(byte[] header)
    {
        Array.Clear(header, 0, FlacStreaminfoSize);
        var p = new PutBitContext(header, FlacStreaminfoSize);

        p.PutBits(16, (uint)maxBlocksize);
        p.PutBits(16, (uint)maxBlocksize);
        p.PutBits(24, (uint)minFramesize);
        p.PutBits(24, (uint)maxFramesize);
        p.PutBits(20, (uint)samplerate);
        p.PutBits(3, (uint)(channels - 1));
        p.PutBits(5, (uint)(bitsPerRawSample - 1));
        // 36-bit sample count, in two calls as the C does.
        p.PutBits(24, (uint)((sampleCount & 0xFFFFFF000UL) >> 12));
        p.PutBits(12, (uint)(sampleCount & 0x000000FFFUL));
        p.Flush();
        Array.Copy(md5Sum, 0, header, 18, 16);
    }

    /// <summary>flac_get_max_frame_size</summary>
    private static int FlacGetMaxFrameSize(int blocksize, int ch, int bps)
    {
        int count = 16;                       // frame header
        count += ch * ((7 + bps + 7) / 8);    // subframe headers
        if (ch == 2)
            count += ((2 * bps + 1) * blocksize + 7) / 8;
        else
            count += (ch * bps * blocksize + 7) / 8;
        count += 2;                           // frame footer
        return count;
    }

    /// <summary>select_blocksize</summary>
    private static int SelectBlocksize(int samplerate, int blockTimeMs)
    {
        if (samplerate <= 0)
            throw new ArgumentOutOfRangeException(nameof(samplerate));

        int blocksize = BlocksizeTable[1];
        int target = (samplerate * blockTimeMs) / 1000;
        for (int i = 0; i < 16; i++)
        {
            if (target >= BlocksizeTable[i] && BlocksizeTable[i] > blocksize)
                blocksize = BlocksizeTable[i];
        }
        return blocksize;
    }

    /// <summary>flac_encode_init</summary>
    public void Init(int sampleRate, int channelCount, FlacSampleFormat sampleFormat,
                     int bitsPerRawSampleHint = 0, int frameSize = 0)
    {
        sampleFmt = sampleFormat;
        bitsPerRawSample = bitsPerRawSampleHint;

        switch (sampleFormat)
        {
            case FlacSampleFormat.S16:
                bitsPerRawSample = 16;
                bpsCode = 4;
                break;
            case FlacSampleFormat.S32:
                // The C warns below 24 and encodes as 24 anyway; above 24 it needs
                // -strict experimental, which this entry point expresses as an explicit 32.
                if (bitsPerRawSample <= 24)
                {
                    bitsPerRawSample = 24;
                    bpsCode = 6;
                }
                else
                {
                    bitsPerRawSample = 32;
                    bpsCode = 7;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sampleFormat));
        }

        if (channelCount < 1 || channelCount > FlacMaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channelCount),
                $"{channelCount} channels not supported (max {FlacMaxChannels})");
        channels = channelCount;

        if (sampleRate < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int i;
        for (i = 1; i < 12; i++)
        {
            if (sampleRate == SampleRateTable[i])
            {
                samplerate = SampleRateTable[i];
                srCode[0] = i;
                srCode[1] = 0;
                break;
            }
        }
        if (i == 12)
        {
            if (sampleRate % 1000 == 0 && sampleRate < 255000)
            {
                srCode[0] = 12;
                srCode[1] = sampleRate / 1000;
            }
            else if (sampleRate % 10 == 0 && sampleRate < 655350)
            {
                srCode[0] = 14;
                srCode[1] = sampleRate / 10;
            }
            else if (sampleRate < 65535)
            {
                srCode[0] = 13;
                srCode[1] = sampleRate;
            }
            else if (sampleRate < 1048576)
            {
                srCode[0] = 0;
                srCode[1] = 0;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate),
                    $"{sampleRate} Hz not supported");
            }
            samplerate = sampleRate;
        }

        if (options.CompressionLevel < 0)
            options.CompressionLevel = 5;

        int level = options.CompressionLevel;
        if (level > 12)
            throw new ArgumentOutOfRangeException(nameof(options), $"invalid compression level: {level}");

        options.BlockTimeMs =
            new[] { 27, 27, 27, 105, 105, 105, 105, 105, 105, 105, 105, 105, 105 }[level];

        if (options.LpcType == LpcType.Default)
        {
            options.LpcType = new[]
            {
                LpcType.Fixed, LpcType.Fixed, LpcType.Fixed,
                LpcType.Levinson, LpcType.Levinson, LpcType.Levinson,
                LpcType.Levinson, LpcType.Levinson, LpcType.Levinson,
                LpcType.Levinson, LpcType.Levinson, LpcType.Levinson,
                LpcType.Levinson,
            }[level];
        }

        if (options.MinPredictionOrder < 0)
            options.MinPredictionOrder =
                new[] { 2, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }[level];
        if (options.MaxPredictionOrder < 0)
            options.MaxPredictionOrder =
                new[] { 3, 4, 4, 6, 8, 8, 8, 8, 12, 12, 12, 32, 32 }[level];

        if (options.PredictionOrderMethod < 0)
        {
            options.PredictionOrderMethod = new[]
            {
                Lpc.OrderMethodEst, Lpc.OrderMethodEst, Lpc.OrderMethodEst,
                Lpc.OrderMethodEst, Lpc.OrderMethodEst, Lpc.OrderMethodEst,
                Lpc.OrderMethod4Level, Lpc.OrderMethodLog, Lpc.OrderMethod4Level,
                Lpc.OrderMethodLog, Lpc.OrderMethodSearch, Lpc.OrderMethodLog,
                Lpc.OrderMethodSearch,
            }[level];
        }

        if (options.MinPartitionOrder > options.MaxPartitionOrder)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"invalid partition orders: min={options.MinPartitionOrder} max={options.MaxPartitionOrder}");
        if (options.MinPartitionOrder < 0)
            options.MinPartitionOrder =
                new[] { 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }[level];
        if (options.MaxPartitionOrder < 0)
            options.MaxPartitionOrder =
                new[] { 2, 2, 3, 3, 3, 8, 8, 8, 8, 8, 8, 8, 8 }[level];

        if (options.LpcType == LpcType.None)
        {
            options.MinPredictionOrder = 0;
            options.MaxPredictionOrder = 0;
        }
        else if (options.LpcType == LpcType.Fixed)
        {
            if (options.MinPredictionOrder > MaxFixedOrder)
                options.MinPredictionOrder = MaxFixedOrder;
            if (options.MaxPredictionOrder > MaxFixedOrder)
                options.MaxPredictionOrder = MaxFixedOrder;
        }

        if (options.MaxPredictionOrder < options.MinPredictionOrder)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"invalid prediction orders: min={options.MinPredictionOrder} max={options.MaxPredictionOrder}");

        if (frameSize > 0)
        {
            if (frameSize < FlacMinBlocksize || frameSize > FlacMaxBlocksize)
                throw new ArgumentOutOfRangeException(nameof(frameSize), $"invalid block size: {frameSize}");
            FrameSize = frameSize;
        }
        else
        {
            FrameSize = SelectBlocksize(samplerate, options.BlockTimeMs);
        }
        maxBlocksize = FrameSize;

        maxFramesize = FlacGetMaxFrameSize(FrameSize, channels, bitsPerRawSample);

        md5Ctx = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        frame = new FlacFrame(channels, maxBlocksize);

        WriteStreaminfo(Extradata);

        frameCount = 0;
        minFramesize = maxFramesize;

        lpcCtx.Init(FrameSize, options.MaxPredictionOrder, LpcType.Levinson);
    }

    /// <summary>init_frame</summary>
    private void InitFrame(int nbSamples)
    {
        int i;
        for (i = 0; i < 16; i++)
        {
            if (nbSamples == BlocksizeTable[i])
            {
                frame.Blocksize = BlocksizeTable[i];
                frame.BsCode[0] = i;
                frame.BsCode[1] = 0;
                break;
            }
        }
        if (i == 16)
        {
            frame.Blocksize = nbSamples;
            frame.BsCode[0] = frame.Blocksize <= 256 ? 6 : 7;
            frame.BsCode[1] = frame.Blocksize - 1;
        }

        for (int ch = 0; ch < channels; ch++)
        {
            FlacSubframe sub = frame.Subframes[ch];
            sub.Wasted = 0;
            sub.Obits = bitsPerRawSample;
            sub.Rc.CodingMode = sub.Obits > 16 ? CodingModeRice2 : CodingModeRice;
        }

        frame.VerbatimOnly = false;
    }

    /// <summary>copy_samples, for AV_SAMPLE_FMT_S16.</summary>
    private void CopySamples(ReadOnlySpan<short> samples)
    {
        for (int i = 0, j = 0; i < frame.Blocksize; i++)
            for (int ch = 0; ch < channels; ch++, j++)
                frame.Subframes[ch].Samples[i] = samples[j];
    }

    /// <summary>copy_samples, for AV_SAMPLE_FMT_S32.</summary>
    private void CopySamples(ReadOnlySpan<int> samples)
    {
        int sh = 32 - bitsPerRawSample;
        for (int i = 0, j = 0; i < frame.Blocksize; i++)
            for (int ch = 0; ch < channels; ch++, j++)
                frame.Subframes[ch].Samples[i] = samples[j] >> sh;
    }

    /// <summary>rice_count_exact</summary>
    private static ulong RiceCountExact(ReadOnlySpan<int> res, int n, int k)
    {
        ulong count = 0;
        for (int i = 0; i < n; i++)
        {
            uint v = ((uint)res[i] << 1) ^ (uint)(res[i] >> 31);
            count += (ulong)((v >> k) + 1 + (uint)k);
        }
        return count;
    }

    /// <summary>subframe_count_exact</summary>
    private ulong SubframeCountExact(FlacSubframe sub, int predOrder)
    {
        ulong count = 8;                       // subframe header

        if (sub.Wasted != 0)
            count += (ulong)sub.Wasted;

        if (sub.Type == SubframeConstant)
        {
            count += (ulong)sub.Obits;
        }
        else if (sub.Type == SubframeVerbatim)
        {
            count += (ulong)(frame.Blocksize * sub.Obits);
        }
        else
        {
            count += (ulong)(predOrder * sub.Obits);          // warm-up samples

            if (sub.Type == SubframeLpc)
                count += (ulong)(4 + 5 + predOrder * options.LpcCoeffPrecision);

            count += 2;                                       // rice-encoded block

            int porder = sub.Rc.Porder;
            int psize = frame.Blocksize >> porder;
            count += 4;                                       // partition order

            int i = predOrder;
            int partEnd = psize;
            for (int p = 0; p < 1 << porder; p++)
            {
                int k = sub.Rc.Params[p];
                count += (ulong)sub.Rc.CodingMode;
                count += RiceCountExact(sub.Residual.AsSpan(i), partEnd - i, k);
                i = partEnd;
                partEnd = Math.Min(frame.Blocksize, partEnd + psize);
            }
        }

        return count;
    }

    /// <summary>rice_encode_count</summary>
    private static ulong RiceEncodeCount(ulong sum, int n, int k) =>
        (ulong)((long)n * (k + 1)) + ((sum - (ulong)(n >> 1)) >> k);

    /// <summary>av_log2</summary>
    private static int Log2(uint v) => v == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(v);

    /// <summary>FFABS. Math.Abs throws on int.MinValue; the C wraps, and the sums rely on it.</summary>
    private static int FfAbs(int a) => a < 0 ? -a : a;

    /// <summary>FFABS, 64-bit.</summary>
    private static long FfAbs(long a) => a < 0 ? -a : a;

    /// <summary>av_clipl_int32</summary>
    private static int ClipInt32(long a) =>
        (a + 0x80000000L) > uint.MaxValue ? (int)((a >> 63) ^ 0x7FFFFFFF) : (int)a;

    /// <summary>find_optimal_param</summary>
    private static int FindOptimalParam(ulong sum, int n, int maxParam)
    {
        if (sum <= (ulong)(n >> 1))
            return 0;
        ulong sum2 = sum - (ulong)(n >> 1);
        int k = Log2((uint)ClipInt32((long)(sum2 / (ulong)n)));
        return Math.Min(k, maxParam);
    }

    /// <summary>find_optimal_param_exact</summary>
    private static int FindOptimalParamExact(ReadOnlySpan<ulong> sums, int i, int maxParam)
    {
        int bestk = 0;
        long bestbits = long.MaxValue;

        for (int k = 0; k <= maxParam; k++)
        {
            long bits = (long)sums[k * MaxPartitions + i];
            if (bits < bestbits)
            {
                bestbits = bits;
                bestk = k;
            }
        }

        return bestk;
    }

    /// <summary>calc_optimal_rice_params</summary>
    private static ulong CalcOptimalRiceParams(RiceContext rc, int porder, ReadOnlySpan<ulong> sums,
                                               int n, int predOrder, int maxParam, bool exact)
    {
        int part = 1 << porder;
        ulong allBits = (ulong)(4 * part);

        int cnt = (n >> porder) - predOrder;
        for (int i = 0; i < part; i++)
        {
            int k;
            if (exact)
            {
                k = FindOptimalParamExact(sums, i, maxParam);
                allBits += sums[k * MaxPartitions + i];
            }
            else
            {
                k = FindOptimalParam(sums[i], cnt, maxParam);
                allBits += RiceEncodeCount(sums[i], cnt, k);
            }
            rc.Params[i] = k;
            cnt = n >> porder;
        }

        rc.Porder = porder;

        return allBits;
    }

    /// <summary>calc_sum_top</summary>
    private static void CalcSumTop(int pmax, int kmax, ReadOnlySpan<uint> data, int n,
                                   int predOrder, Span<ulong> sums)
    {
        int parts = 1 << pmax;

        for (int k = 0; k <= kmax; k++)
        {
            int res = predOrder;
            int resEnd = n >> pmax;
            for (int i = 0; i < parts; i++)
            {
                if (kmax != 0)
                {
                    ulong sum = (ulong)((1L + k) * (resEnd - res));
                    while (res < resEnd)
                        sum += data[res++] >> k;
                    sums[k * MaxPartitions + i] = sum;
                }
                else
                {
                    ulong sum = 0;
                    while (res < resEnd)
                        sum += data[res++];
                    sums[k * MaxPartitions + i] = sum;
                }
                resEnd += n >> pmax;
            }
        }
    }

    /// <summary>calc_sum_next</summary>
    private static void CalcSumNext(int level, Span<ulong> sums, int kmax)
    {
        int parts = 1 << level;
        for (int i = 0; i < parts; i++)
            for (int k = 0; k <= kmax; k++)
                sums[k * MaxPartitions + i] =
                    sums[k * MaxPartitions + 2 * i] + sums[k * MaxPartitions + 2 * i + 1];
    }

    private readonly RiceContext tmpRc = new();

    /// <summary>calc_rice_params</summary>
    private ulong CalcRiceParams(RiceContext rc, uint[] udata, ulong[] sums, int pmin, int pmax,
                                 ReadOnlySpan<int> data, int n, int predOrder, bool exact)
    {
        Span<ulong> bits = stackalloc ulong[MaxPartitionOrderConst + 1];
        int kmax = (1 << rc.CodingMode) - 2;

        tmpRc.CodingMode = rc.CodingMode;

        for (int i = predOrder; i < n; i++)
            udata[i] = ((uint)data[i] << 1) ^ (uint)(data[i] >> 31);

        CalcSumTop(pmax, exact ? kmax : 0, udata, n, predOrder, sums);

        int optPorder = pmin;
        bits[pmin] = uint.MaxValue;
        for (int i = pmax; ;)
        {
            bits[i] = CalcOptimalRiceParams(tmpRc, i, sums, n, predOrder, kmax, exact);
            if (bits[i] < bits[optPorder] || pmax == pmin)
            {
                optPorder = i;
                rc.CopyFrom(tmpRc);
            }
            if (i == pmin)
                break;
            CalcSumNext(--i, sums, exact ? kmax : 0);
        }

        return bits[optPorder];
    }

    /// <summary>get_max_p_order</summary>
    private static int GetMaxPOrder(int maxPorder, int n, int order)
    {
        int porder = Math.Min(maxPorder, Log2((uint)(n ^ (n - 1))));
        if (order > 0)
            porder = Math.Min(porder, Log2((uint)(n / order)));
        return porder;
    }

    /// <summary>find_subframe_rice_params</summary>
    private ulong FindSubframeRiceParams(FlacSubframe sub, int predOrder)
    {
        int pmin = GetMaxPOrder(options.MinPartitionOrder, frame.Blocksize, predOrder);
        int pmax = GetMaxPOrder(options.MaxPartitionOrder, frame.Blocksize, predOrder);

        ulong bits = (ulong)(8 + predOrder * sub.Obits + 2 + sub.Rc.CodingMode);
        if (sub.Type == SubframeLpc)
            bits += (ulong)(4 + 5 + predOrder * options.LpcCoeffPrecision);
        bits += CalcRiceParams(sub.Rc, sub.RcUdata, sub.RcSums, pmin, pmax, sub.Residual,
                               frame.Blocksize, predOrder, options.ExactRiceParameters);
        return bits;
    }

    /// <summary>encode_residual_fixed</summary>
    private static void EncodeResidualFixed(Span<int> res, ReadOnlySpan<int> smp, int n, int order)
    {
        for (int i = 0; i < order; i++)
            res[i] = smp[i];

        if (order == 0)
        {
            for (int i = order; i < n; i++)
                res[i] = smp[i];
        }
        else if (order == 1)
        {
            for (int i = order; i < n; i++)
                res[i] = smp[i] - smp[i - 1];
        }
        else if (order == 2)
        {
            int a = smp[order - 1] - smp[order - 2];
            for (int i = order; i < n; i += 2)
            {
                int b = smp[i] - smp[i - 1];
                res[i] = b - a;
                a = smp[i + 1] - smp[i];
                res[i + 1] = a - b;
            }
        }
        else if (order == 3)
        {
            int a = smp[order - 1] - smp[order - 2];
            int c = smp[order - 1] - 2 * smp[order - 2] + smp[order - 3];
            for (int i = order; i < n; i += 2)
            {
                int b = smp[i] - smp[i - 1];
                int d = b - a;
                res[i] = d - c;
                a = smp[i + 1] - smp[i];
                c = a - b;
                res[i + 1] = c - d;
            }
        }
        else
        {
            int a = smp[order - 1] - smp[order - 2];
            int c = smp[order - 1] - 2 * smp[order - 2] + smp[order - 3];
            int e = smp[order - 1] - 3 * smp[order - 2] + 3 * smp[order - 3] - smp[order - 4];
            for (int i = order; i < n; i += 2)
            {
                int b = smp[i] - smp[i - 1];
                int d = b - a;
                int f = d - c;
                res[i] = f - e;
                a = smp[i + 1] - smp[i];
                c = a - b;
                e = c - d;
                res[i + 1] = e - f;
            }
        }
    }

    /// <summary>
    /// encode_residual_fixed_with_residual_limit. Returns true where the C returns 1, meaning a
    /// residual fell outside (INT32_MIN, INT32_MAX] and the caller must try something else.
    /// </summary>
    private static bool EncodeResidualFixedWithResidualLimit(Span<int> res, ReadOnlySpan<int> smp,
                                                             int n, int order)
    {
        for (int i = 0; i < order; i++)
            res[i] = smp[i];

        if (order == 0)
        {
            for (int i = order; i < n; i++)
            {
                if (smp[i] == int.MinValue)
                    return true;
                res[i] = smp[i];
            }
        }
        else if (order == 1)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = (long)smp[i] - smp[i - 1];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else if (order == 2)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = (long)smp[i] - 2 * (long)smp[i - 1] + smp[i - 2];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else if (order == 3)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = (long)smp[i] - 3 * (long)smp[i - 1] + 3 * (long)smp[i - 2] - smp[i - 3];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else
        {
            for (int i = order; i < n; i++)
            {
                long res64 = (long)smp[i] - 4 * (long)smp[i - 1] + 6 * (long)smp[i - 2]
                           - 4 * (long)smp[i - 3] + smp[i - 4];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        return false;
    }

    /// <summary>encode_residual_fixed_with_residual_limit_33bps</summary>
    private static bool EncodeResidualFixedWithResidualLimit33Bps(Span<int> res,
                                                                  ReadOnlySpan<long> smp,
                                                                  int n, int order)
    {
        for (int i = 0; i < order; i++)
            res[i] = (int)smp[i];

        if (order == 0)
        {
            for (int i = order; i < n; i++)
            {
                if (smp[i] == int.MinValue)
                    return true;
                res[i] = (int)smp[i];
            }
        }
        else if (order == 1)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = smp[i] - smp[i - 1];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else if (order == 2)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = smp[i] - 2 * smp[i - 1] + smp[i - 2];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else if (order == 3)
        {
            for (int i = order; i < n; i++)
            {
                long res64 = smp[i] - 3 * smp[i - 1] + 3 * smp[i - 2] - smp[i - 3];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        else
        {
            for (int i = order; i < n; i++)
            {
                long res64 = smp[i] - 4 * smp[i - 1] + 6 * smp[i - 2] - 4 * smp[i - 3] + smp[i - 4];
                if (res64 <= int.MinValue || res64 > int.MaxValue)
                    return true;
                res[i] = (int)res64;
            }
        }
        return false;
    }

    /// <summary>lpc_encode_with_residual_limit</summary>
    private static bool LpcEncodeWithResidualLimit(Span<int> res, ReadOnlySpan<int> smp, int len,
                                                   int order, ReadOnlySpan<int> coefs, int shift)
    {
        for (int i = 0; i < order; i++)
            res[i] = smp[i];

        for (int i = order; i < len; i++)
        {
            long p = 0;
            for (int j = 0; j < order; j++)
                p += (long)coefs[j] * smp[i - 1 - j];
            p >>= shift;
            long tmp = smp[i] - p;
            if (tmp <= int.MinValue || tmp > int.MaxValue)
                return true;
            res[i] = (int)tmp;
        }
        return false;
    }

    /// <summary>lpc_encode_with_residual_limit_33bps</summary>
    private static bool LpcEncodeWithResidualLimit33Bps(Span<int> res, ReadOnlySpan<long> smp,
                                                        int len, int order,
                                                        ReadOnlySpan<int> coefs, int shift)
    {
        for (int i = 0; i < order; i++)
            res[i] = (int)smp[i];

        for (int i = order; i < len; i++)
        {
            long p = 0;
            for (int j = 0; j < order; j++)
                p += coefs[j] * smp[i - 1 - j];
            p >>= shift;
            long tmp = smp[i] - p;
            if (tmp <= int.MinValue || tmp > int.MaxValue)
                return true;
            res[i] = (int)tmp;
        }
        return false;
    }

    /// <summary>lpc_encode_choose_datapath</summary>
    private bool LpcEncodeChooseDatapath(int bps, Span<int> res, ReadOnlySpan<int> smp,
                                         ReadOnlySpan<long> smp33Bps, int len, int order,
                                         ReadOnlySpan<int> coefs, int shift)
    {
        ulong maxResidualValue = 0;
        long maxSampleValue = 1L << (bps - 1);
        for (int i = 0; i < order; i++)
            maxResidualValue += (ulong)FfAbs(maxSampleValue * coefs[i]);
        maxResidualValue >>= shift;
        maxResidualValue += (ulong)maxSampleValue;

        if (bps > 32)
        {
            if (LpcEncodeWithResidualLimit33Bps(res, smp33Bps, len, order, coefs, shift))
                return true;
        }
        else if (maxResidualValue > int.MaxValue)
        {
            if (LpcEncodeWithResidualLimit(res, smp, len, order, coefs, shift))
                return true;
        }
        else if (bps + options.LpcCoeffPrecision + Log2((uint)order) <= 32)
        {
            FlacEncDsp.FlacLpcEncodeC16(res, smp, len, order, coefs, shift);
        }
        else
        {
            FlacEncDsp.FlacLpcEncodeC32(res, smp, len, order, coefs, shift);
        }
        return false;
    }

    /// <summary>The DEFAULT_TO_VERBATIM macro.</summary>
    private ulong DefaultToVerbatim(FlacSubframe sub, Span<int> res, ReadOnlySpan<int> smp, int n)
    {
        sub.Type = sub.TypeCode = SubframeVerbatim;
        if (sub.Obits <= 32)
            smp[..n].CopyTo(res);
        return SubframeCountExact(sub, 0);
    }

    /// <summary>encode_residual_ch</summary>
    private ulong EncodeResidualCh(int ch)
    {
        FlacSubframe sub = frame.Subframes[ch];
        int[] res = sub.Residual;
        int[] smp = sub.Samples;
        long[] smp33Bps = frame.Samples33Bps;
        int n = frame.Blocksize;
        int i;

        /* CONSTANT */
        if (sub.Obits > 32)
        {
            for (i = 1; i < n; i++)
                if (smp33Bps[i] != smp33Bps[0])
                    break;
            if (i == n)
            {
                sub.Type = sub.TypeCode = SubframeConstant;
                return SubframeCountExact(sub, 0);
            }
        }
        else
        {
            for (i = 1; i < n; i++)
                if (smp[i] != smp[0])
                    break;
            if (i == n)
            {
                sub.Type = sub.TypeCode = SubframeConstant;
                res[0] = smp[0];
                return SubframeCountExact(sub, 0);
            }
        }

        /* VERBATIM */
        if (frame.VerbatimOnly || n < 5)
            return DefaultToVerbatim(sub, res, smp, n);

        int minOrder = options.MinPredictionOrder;
        int maxOrder = options.MaxPredictionOrder;
        int omethod = options.PredictionOrderMethod;
        int optOrder;

        /* FIXED */
        sub.Type = SubframeFixed;
        if (options.LpcType == LpcType.None || options.LpcType == LpcType.Fixed || n <= maxOrder)
        {
            Span<ulong> bits = stackalloc ulong[MaxFixedOrder + 1];
            if (maxOrder > MaxFixedOrder)
                maxOrder = MaxFixedOrder;
            optOrder = 0;
            bits[0] = uint.MaxValue;
            for (i = minOrder; i <= maxOrder; i++)
            {
                if (sub.Obits == 33)
                {
                    if (EncodeResidualFixedWithResidualLimit33Bps(res, smp33Bps, n, i))
                        continue;
                }
                else if (sub.Obits + i >= 32)
                {
                    if (EncodeResidualFixedWithResidualLimit(res, smp, n, i))
                        continue;
                }
                else
                {
                    EncodeResidualFixed(res, smp, n, i);
                }
                bits[i] = FindSubframeRiceParams(sub, i);
                if (bits[i] < bits[optOrder])
                    optOrder = i;
            }
            if (optOrder == 0 && bits[0] == uint.MaxValue)
                return DefaultToVerbatim(sub, res, smp, n);

            sub.Order = optOrder;
            sub.TypeCode = sub.Type | sub.Order;
            if (sub.Order != maxOrder)
            {
                // `i` is deliberately the loop's exit value here, as in the C.
                if (sub.Obits == 33)
                    EncodeResidualFixedWithResidualLimit33Bps(res, smp33Bps, n, sub.Order);
                else if (sub.Obits + i >= 32)
                    EncodeResidualFixedWithResidualLimit(res, smp, n, sub.Order);
                else
                    EncodeResidualFixed(res, smp, n, sub.Order);
                FindSubframeRiceParams(sub, sub.Order);
            }
            return SubframeCountExact(sub, sub.Order);
        }

        /* LPC */
        sub.Type = SubframeLpc;
        if (sub.Obits == 33)
        {
            // The LSB is thrown away so the shared analysis can work on int32.
            for (i = 0; i < n; i++)
                smp[i] = (int)(smp33Bps[i] >> 1);
        }

        optOrder = lpcCtx.CalcCoefs(smp, n, minOrder, maxOrder, options.LpcCoeffPrecision,
                                    coefs, shift, options.LpcType, options.LpcPasses, omethod,
                                    MinLpcShift, MaxLpcShift, 0);

        if (omethod == Lpc.OrderMethod2Level ||
            omethod == Lpc.OrderMethod4Level ||
            omethod == Lpc.OrderMethod8Level)
        {
            int levels = 1 << omethod;
            Span<ulong> bits = stackalloc ulong[1 << Lpc.OrderMethod8Level];
            int order = -1;
            int optIndex = levels - 1;
            optOrder = maxOrder - 1;
            bits[optIndex] = uint.MaxValue;
            for (i = levels - 1; i >= 0; i--)
            {
                int lastOrder = order;
                order = minOrder + (((maxOrder - minOrder + 1) * (i + 1)) / levels) - 1;
                order = Math.Clamp(order, minOrder - 1, maxOrder - 1);
                if (order == lastOrder)
                    continue;
                if (LpcEncodeChooseDatapath(sub.Obits, res, smp, smp33Bps, n, order + 1,
                                            CoefRow(order), shift[order]))
                    continue;
                bits[i] = FindSubframeRiceParams(sub, order + 1);
                if (bits[i] < bits[optIndex])
                {
                    optIndex = i;
                    optOrder = order;
                }
            }
            optOrder++;
        }
        else if (omethod == Lpc.OrderMethodSearch)
        {
            Span<ulong> bits = stackalloc ulong[Lpc.MaxLpcOrder];
            optOrder = 0;
            bits[0] = uint.MaxValue;
            for (i = minOrder - 1; i < maxOrder; i++)
            {
                if (LpcEncodeChooseDatapath(sub.Obits, res, smp, smp33Bps, n, i + 1,
                                            CoefRow(i), shift[i]))
                    continue;
                bits[i] = FindSubframeRiceParams(sub, i + 1);
                if (bits[i] < bits[optOrder])
                    optOrder = i;
            }
            optOrder++;
        }
        else if (omethod == Lpc.OrderMethodLog)
        {
            Span<ulong> bits = stackalloc ulong[Lpc.MaxLpcOrder];
            optOrder = minOrder - 1 + (maxOrder - minOrder) / 3;
            bits.Fill(ulong.MaxValue);          // memset(bits, -1, sizeof(bits))

            for (int step = 16; step != 0; step >>= 1)
            {
                int last = optOrder;
                for (i = last - step; i <= last + step; i += step)
                {
                    if (i < minOrder - 1 || i >= maxOrder || bits[i] < uint.MaxValue)
                        continue;
                    if (LpcEncodeChooseDatapath(sub.Obits, res, smp, smp33Bps, n, i + 1,
                                                CoefRow(i), shift[i]))
                        continue;
                    bits[i] = FindSubframeRiceParams(sub, i + 1);
                    if (bits[i] < bits[optOrder])
                        optOrder = i;
                }
            }
            optOrder++;
        }

        if (options.MultiDimQuant)
            MultiDimensionalQuantise(sub, res, smp, smp33Bps, n, optOrder);

        sub.Order = optOrder;
        sub.TypeCode = sub.Type | (sub.Order - 1);
        sub.Shift = shift[sub.Order - 1];
        for (i = 0; i < sub.Order; i++)
            sub.Coefs[i] = coefs[(sub.Order - 1) * Lpc.MaxLpcOrder + i];

        if (LpcEncodeChooseDatapath(sub.Obits, res, smp, smp33Bps, n, sub.Order, sub.Coefs,
                                    sub.Shift))
            return DefaultToVerbatim(sub, res, smp, n);

        FindSubframeRiceParams(sub, sub.Order);

        return SubframeCountExact(sub, sub.Order);
    }

    private Span<int> CoefRow(int order) => coefs.AsSpan(order * Lpc.MaxLpcOrder, Lpc.MaxLpcOrder);

    /// <summary>
    /// The multi_dim_quant block of encode_residual_ch, lifted out because it is a self-contained
    /// search and the C function is already 200 lines.
    /// </summary>
    private void MultiDimensionalQuantise(FlacSubframe sub, Span<int> res, ReadOnlySpan<int> smp,
                                          ReadOnlySpan<long> smp33Bps, int n, int optOrder)
    {
        int allsteps = 1;
        long bestScore = long.MaxValue;
        int qmax = (1 << (options.LpcCoeffPrecision - 1)) - 1;

        for (int i = 0; i < optOrder; i++)
            allsteps *= 3;

        bool improved;
        do
        {
            improved = false;
            for (int step = 0; step < allsteps; step++)
            {
                int tmp = step;
                int diffsum = 0;

                for (int i = 0; i < optOrder; i++)
                {
                    int diff = ((tmp + 1) % 3) - 1;
                    lpcTry[i] = Math.Clamp(coefs[(optOrder - 1) * Lpc.MaxLpcOrder + i] + diff,
                                           -qmax, qmax);
                    tmp /= 3;
                    diffsum += diff != 0 ? 1 : 0;
                }
                if (diffsum > 8)
                    continue;

                if (LpcEncodeChooseDatapath(sub.Obits, res, smp, smp33Bps, n, optOrder, lpcTry,
                                            shift[optOrder - 1]))
                    continue;
                long score = (long)FindSubframeRiceParams(sub, optOrder);
                if (score < bestScore)
                {
                    bestScore = score;
                    // The C's memcpy uses sizeof(*coefs), one whole MAX_LPC_ORDER row.
                    lpcTry.AsSpan(0, Lpc.MaxLpcOrder)
                          .CopyTo(coefs.AsSpan((optOrder - 1) * Lpc.MaxLpcOrder));
                    improved = true;
                }
            }
        } while (improved);
    }

    /// <summary>count_frame_header</summary>
    private int CountFrameHeader()
    {
        int count = 32;

        // PUT_UTF8(s->frame_count, tmp, count += 8;)
        Span<byte> utf8 = stackalloc byte[7];
        count += PutUtf8(frameCount, utf8) * 8;

        if (frame.BsCode[0] == 6)
            count += 8;
        else if (frame.BsCode[0] == 7)
            count += 16;

        count += ((srCode[0] == 12 ? 1 : 0) + (srCode[0] > 12 ? 1 : 0) * 2) * 8;

        count += 8;                              // frame header CRC-8

        return count;
    }

    /// <summary>
    /// The PUT_UTF8 macro from libavutil/common.h, writing the bytes it would emit into
    /// <paramref name="dst"/> and returning how many. Both callers need the same bytes - one
    /// counts them, one writes them - so the macro is expanded once rather than twice.
    /// </summary>
    private static int PutUtf8(uint val, Span<byte> dst)
    {
        int n = 0;
        if (val < 0x80)
        {
            dst[n++] = (byte)val;
        }
        else
        {
            int bytes = (Log2(val) + 4) / 5;
            int shift = (bytes - 1) * 6;
            dst[n++] = (byte)((256 - (256 >> bytes)) | (val >> shift));
            while (shift >= 6)
            {
                shift -= 6;
                dst[n++] = (byte)(0x80 | ((val >> shift) & 0x3f));
            }
        }
        return n;
    }

    /// <summary>encode_frame</summary>
    private int EncodeFrameInternal()
    {
        ulong count = (ulong)CountFrameHeader();

        for (int ch = 0; ch < channels; ch++)
            count += EncodeResidualCh(ch);

        count += (8 - (count & 7)) & 7;          // byte alignment
        count += 16;                             // CRC-16

        count >>= 3;
        if (count > int.MaxValue)
            return -1;
        return (int)count;
    }

    /// <summary>remove_wasted_bits</summary>
    private void RemoveWastedBits()
    {
        for (int ch = 0; ch < channels; ch++)
        {
            FlacSubframe sub = frame.Subframes[ch];
            int wastedBits;

            if (sub.Obits > 32)
            {
                long v = 0;
                for (int i = 0; i < frame.Blocksize; i++)
                {
                    v |= frame.Samples33Bps[i];
                    if ((v & 1) != 0)
                        break;
                }

                // The C returns rather than continues: one channel without wasted bits ends the
                // search for every channel after it. Kept as written.
                if (v == 0 || (v & 1) != 0)
                    return;

                int tz = System.Numerics.BitOperations.TrailingZeroCount((ulong)v);
                for (int i = 0; i < frame.Blocksize; i++)
                    sub.Samples[i] = (int)(frame.Samples33Bps[i] >> tz);
                wastedBits = tz;
            }
            else
            {
                int v = 0;
                for (int i = 0; i < frame.Blocksize; i++)
                {
                    v |= sub.Samples[i];
                    if ((v & 1) != 0)
                        break;
                }

                if (v == 0 || (v & 1) != 0)
                    return;

                int tz = System.Numerics.BitOperations.TrailingZeroCount((uint)v);
                for (int i = 0; i < frame.Blocksize; i++)
                    sub.Samples[i] >>= tz;
                wastedBits = tz;
            }

            sub.Wasted = wastedBits;
            sub.Obits -= wastedBits;

            // Removing wasted bits can bring a 24-bit subframe into RICE's range.
            if (sub.Obits <= 17)
                sub.Rc.CodingMode = CodingModeRice;
        }
    }

    /// <summary>estimate_stereo_mode</summary>
    private static int EstimateStereoMode(ReadOnlySpan<int> leftCh, ReadOnlySpan<int> rightCh,
                                          int n, int maxRiceParam, int bps)
    {
        Span<ulong> sum = stackalloc ulong[4];
        Span<ulong> score = stackalloc ulong[4];

        sum.Clear();
        if (bps < 30)
        {
            for (int i = 2; i < n; i++)
            {
                int lt = leftCh[i] - 2 * leftCh[i - 1] + leftCh[i - 2];
                int rt = rightCh[i] - 2 * rightCh[i - 1] + rightCh[i - 2];
                sum[2] += (ulong)FfAbs((lt + rt) >> 1);
                sum[3] += (ulong)FfAbs(lt - rt);
                sum[0] += (ulong)FfAbs(lt);
                sum[1] += (ulong)FfAbs(rt);
            }
        }
        else
        {
            for (int i = 2; i < n; i++)
            {
                long lt = (long)leftCh[i] - 2 * (long)leftCh[i - 1] + leftCh[i - 2];
                long rt = (long)rightCh[i] - 2 * (long)rightCh[i - 1] + rightCh[i - 2];
                sum[2] += (ulong)FfAbs((lt + rt) >> 1);
                sum[3] += (ulong)FfAbs(lt - rt);
                sum[0] += (ulong)FfAbs(lt);
                sum[1] += (ulong)FfAbs(rt);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            int k = FindOptimalParam(2 * sum[i], n, maxRiceParam);
            sum[i] = RiceEncodeCount(2 * sum[i], n, k);
        }

        score[0] = sum[0] + sum[1];
        score[1] = sum[0] + sum[3];
        score[2] = sum[1] + sum[3];
        score[3] = sum[2] + sum[3];

        int best = 0;
        for (int i = 1; i < 4; i++)
            if (score[i] < score[best])
                best = i;

        return best;
    }

    /// <summary>channel_decorrelation</summary>
    private void ChannelDecorrelation()
    {
        int n = frame.Blocksize;
        int[] left = frame.Subframes[0].Samples;
        int[] right = channels >= 2 ? frame.Subframes[1].Samples : null;
        long[] side33Bps = frame.Samples33Bps;

        if (channels != 2)
        {
            frame.ChMode = (int)FlacChannelMode.Independent;
            return;
        }

        if (options.ChMode == FlacChannelMode.Auto)
        {
            int maxRiceParam = (1 << frame.Subframes[0].Rc.CodingMode) - 2;
            frame.ChMode = EstimateStereoMode(left, right, n, maxRiceParam, bitsPerRawSample);
        }
        else
        {
            frame.ChMode = (int)options.ChMode;
        }

        if (frame.ChMode == (int)FlacChannelMode.Independent)
            return;

        if (bitsPerRawSample == 32)
        {
            if (frame.ChMode == (int)FlacChannelMode.MidSide)
            {
                for (int i = 0; i < n; i++)
                {
                    long tmp = left[i];
                    left[i] = (int)((tmp + right[i]) >> 1);
                    side33Bps[i] = tmp - right[i];
                }
                frame.Subframes[1].Obits++;
            }
            else if (frame.ChMode == (int)FlacChannelMode.LeftSide)
            {
                for (int i = 0; i < n; i++)
                    side33Bps[i] = (long)left[i] - right[i];
                frame.Subframes[1].Obits++;
            }
            else
            {
                for (int i = 0; i < n; i++)
                    side33Bps[i] = (long)left[i] - right[i];
                frame.Subframes[0].Obits++;
            }
        }
        else
        {
            if (frame.ChMode == (int)FlacChannelMode.MidSide)
            {
                for (int i = 0; i < n; i++)
                {
                    int tmp = left[i];
                    left[i] = (tmp + right[i]) >> 1;
                    right[i] = tmp - right[i];
                }
                frame.Subframes[1].Obits++;
            }
            else if (frame.ChMode == (int)FlacChannelMode.LeftSide)
            {
                for (int i = 0; i < n; i++)
                    right[i] = left[i] - right[i];
                frame.Subframes[1].Obits++;
            }
            else
            {
                for (int i = 0; i < n; i++)
                    left[i] -= right[i];
                frame.Subframes[0].Obits++;
            }
        }
    }

    /// <summary>write_utf8</summary>
    private void WriteUtf8(uint val)
    {
        Span<byte> utf8 = stackalloc byte[7];
        int n = PutUtf8(val, utf8);
        for (int i = 0; i < n; i++)
            pb.PutBits(8, utf8[i]);
    }

    /// <summary>write_frame_header</summary>
    private void WriteFrameHeader()
    {
        pb.PutBits(16, 0xFFF8);
        pb.PutBits(4, (uint)frame.BsCode[0]);
        pb.PutBits(4, (uint)srCode[0]);

        if (frame.ChMode == (int)FlacChannelMode.Independent)
            pb.PutBits(4, (uint)(channels - 1));
        else
            pb.PutBits(4, (uint)(frame.ChMode + FlacMaxChannels - 1));

        pb.PutBits(3, (uint)bpsCode);
        pb.PutBits(1, 0);
        WriteUtf8(frameCount);

        if (frame.BsCode[0] == 6)
            pb.PutBits(8, (uint)frame.BsCode[1]);
        else if (frame.BsCode[0] == 7)
            pb.PutBits(16, (uint)frame.BsCode[1]);

        if (srCode[0] == 12)
            pb.PutBits(8, (uint)srCode[1]);
        else if (srCode[0] > 12)
            pb.PutBits(16, (uint)srCode[1]);

        pb.Flush();
        byte crc = FlacDecoder.CalculateCrc8(pb.Buffer.AsSpan(0, pb.BytesOutput));
        pb.PutBits(8, crc);
    }

    /// <summary>set_sr_golomb_flac</summary>
    private void SetSrGolombFlac(int i, int k)
    {
        uint v = ((uint)i << 1) ^ (uint)(i >> 31);

        uint e = (v >> k) + 1;
        while (e > 31)
        {
            pb.PutBits(31, 0);
            e -= 31;
        }
        pb.PutBits((int)e, 1);
        if (k != 0)
        {
            uint mask = uint.MaxValue >> (32 - k);
            pb.PutBits(k, v & mask);
        }
    }

    /// <summary>write_subframes</summary>
    private void WriteSubframes()
    {
        for (int ch = 0; ch < channels; ch++)
        {
            FlacSubframe sub = frame.Subframes[ch];
            int[] residual = sub.Residual;
            int res = 0;
            int frameEnd = frame.Blocksize;

            /* subframe header */
            pb.PutBits(1, 0);
            pb.PutBits(6, (uint)sub.TypeCode);
            pb.PutBits(1, sub.Wasted != 0 ? 1u : 0u);
            if (sub.Wasted != 0)
                pb.PutBits(sub.Wasted, 1);

            if (sub.Type == SubframeConstant)
            {
                if (sub.Obits == 33)
                    pb.PutSBits63(33, frame.Samples33Bps[0]);
                else if (sub.Obits == 32)
                    pb.PutBits32((uint)residual[0]);
                else
                    pb.PutSBits(sub.Obits, residual[0]);
            }
            else if (sub.Type == SubframeVerbatim)
            {
                if (sub.Obits == 33)
                {
                    for (int i = 0; i < frame.Blocksize; i++)
                        pb.PutSBits63(33, frame.Samples33Bps[i]);
                }
                else if (sub.Obits == 32)
                {
                    while (res < frameEnd)
                        pb.PutBits32((uint)residual[res++]);
                }
                else
                {
                    while (res < frameEnd)
                        pb.PutSBits(sub.Obits, residual[res++]);
                }
            }
            else
            {
                /* warm-up samples */
                if (sub.Obits == 33)
                {
                    for (int i = 0; i < sub.Order; i++)
                        pb.PutSBits63(33, frame.Samples33Bps[i]);
                    res += sub.Order;
                }
                else if (sub.Obits == 32)
                {
                    for (int i = 0; i < sub.Order; i++)
                        pb.PutBits32((uint)residual[res++]);
                }
                else
                {
                    for (int i = 0; i < sub.Order; i++)
                        pb.PutSBits(sub.Obits, residual[res++]);
                }

                /* LPC coefficients */
                if (sub.Type == SubframeLpc)
                {
                    int cbits = options.LpcCoeffPrecision;
                    pb.PutBits(4, (uint)(cbits - 1));
                    pb.PutSBits(5, sub.Shift);
                    for (int i = 0; i < sub.Order; i++)
                        pb.PutSBits(cbits, sub.Coefs[i]);
                }

                /* rice-encoded block */
                pb.PutBits(2, (uint)(sub.Rc.CodingMode - 4));

                int porder = sub.Rc.Porder;
                int psize = frame.Blocksize >> porder;
                pb.PutBits(4, (uint)porder);

                int partEnd = psize;
                for (int p = 0; p < 1 << porder; p++)
                {
                    int k = sub.Rc.Params[p];
                    pb.PutBits(sub.Rc.CodingMode, (uint)k);
                    while (res < partEnd)
                        SetSrGolombFlac(residual[res++], k);
                    partEnd = Math.Min(frameEnd, partEnd + psize);
                }
            }
        }
    }

    /// <summary>write_frame_footer</summary>
    private void WriteFrameFooter()
    {
        pb.Flush();
        // The C computes AV_CRC_16_ANSI, which is FFmpeg's byte-swapped form of FLAC's CRC-16,
        // and then av_bswap16's it back. CalculateCrc16 is already the FLAC-order value, so the
        // two swaps cancel and it goes out big-endian with none.
        ushort crc = FlacDecoder.CalculateCrc16(pb.Buffer.AsSpan(0, pb.BytesOutput));
        pb.PutBits(16, crc);
        pb.Flush();
    }

    /// <summary>write_frame</summary>
    private int WriteFrame(byte[] output, int size)
    {
        pb.Reset(output, size);
        WriteFrameHeader();
        WriteSubframes();
        WriteFrameFooter();
        return pb.BytesOutput;
    }

    /// <summary>update_md5_sum</summary>
    private void UpdateMd5Sum(ReadOnlySpan<short> samples)
    {
        int bufSize = frame.Blocksize * channels * ((bitsPerRawSample + 7) / 8);
        EnsureMd5Buffer(bufSize);
        for (int i = 0; i < frame.Blocksize * channels; i++)
        {
            md5Buffer[2 * i] = (byte)samples[i];
            md5Buffer[2 * i + 1] = (byte)(samples[i] >> 8);
        }
        md5Ctx.AppendData(md5Buffer, 0, bufSize);
    }

    /// <summary>update_md5_sum</summary>
    private void UpdateMd5Sum(ReadOnlySpan<int> samples)
    {
        int bufSize = frame.Blocksize * channels * ((bitsPerRawSample + 7) / 8);
        EnsureMd5Buffer(bufSize);

        if (bitsPerRawSample <= 24)
        {
            for (int i = 0; i < frame.Blocksize * channels; i++)
            {
                int v = samples[i] >> 8;
                md5Buffer[3 * i] = (byte)v;
                md5Buffer[3 * i + 1] = (byte)(v >> 8);
                md5Buffer[3 * i + 2] = (byte)(v >> 16);
            }
        }
        else
        {
            for (int i = 0; i < frame.Blocksize * channels; i++)
            {
                int v = samples[i];
                md5Buffer[4 * i] = (byte)v;
                md5Buffer[4 * i + 1] = (byte)(v >> 8);
                md5Buffer[4 * i + 2] = (byte)(v >> 16);
                md5Buffer[4 * i + 3] = (byte)(v >> 24);
            }
        }
        md5Ctx.AppendData(md5Buffer, 0, bufSize);
    }

    private void EnsureMd5Buffer(int size)
    {
        if (md5Buffer.Length < size)
            md5Buffer = new byte[size];
    }

    /// <summary>
    /// flac_encode_frame, for 16-bit input. <paramref name="output"/> must hold
    /// <see cref="MaxFramesize"/> bytes; the return value is how many were written.
    /// </summary>
    public int EncodeFrame(ReadOnlySpan<short> samples, int nbSamples, byte[] output)
    {
        if (sampleFmt != FlacSampleFormat.S16)
            throw new InvalidOperationException("This encoder was initialised for 32-bit input.");
        PrepareFrame(nbSamples);
        CopySamples(samples);
        int bytes = EncodeAndWrite(output);
        UpdateMd5Sum(samples);
        FinishFrame(nbSamples, bytes);
        return bytes;
    }

    /// <summary>flac_encode_frame, for 32-bit input.</summary>
    public int EncodeFrame(ReadOnlySpan<int> samples, int nbSamples, byte[] output)
    {
        if (sampleFmt != FlacSampleFormat.S32)
            throw new InvalidOperationException("This encoder was initialised for 16-bit input.");
        PrepareFrame(nbSamples);
        CopySamples(samples);
        int bytes = EncodeAndWrite(output);
        UpdateMd5Sum(samples);
        FinishFrame(nbSamples, bytes);
        return bytes;
    }

    private void PrepareFrame(int nbSamples)
    {
        // The C tests against frame.blocksize, which is the PREVIOUS frame's size - zero on the
        // first call, so the first frame never takes this branch however short it is.
        if (nbSamples < frame.Blocksize)
            maxFramesize = FlacGetMaxFrameSize(nbSamples, channels, bitsPerRawSample);
        InitFrame(nbSamples);
    }

    private int EncodeAndWrite(byte[] output)
    {
        ChannelDecorrelation();
        RemoveWastedBits();

        int frameBytes = EncodeFrameInternal();

        // Fall back on verbatim if the compressed frame came out larger than an uncompressed one.
        if (frameBytes < 0 || frameBytes > maxFramesize)
        {
            frame.VerbatimOnly = true;
            frameBytes = EncodeFrameInternal();
            if (frameBytes < 0)
                throw new InvalidOperationException("Bad frame count");
        }

        if (output.Length < frameBytes)
            throw new ArgumentException($"output buffer holds {output.Length} bytes, frame needs {frameBytes}");

        return WriteFrame(output, frameBytes);
    }

    private void FinishFrame(int nbSamples, int outBytes)
    {
        frameCount++;
        sampleCount += (ulong)nbSamples;
        if (outBytes > maxEncodedFramesize)
            maxEncodedFramesize = outBytes;
        if (outBytes < minFramesize)
            minFramesize = outBytes;
    }

    /// <summary>
    /// The <c>!frame</c> branch of flac_encode_frame: seal the MD5 and rewrite
    /// <see cref="Extradata"/> with the real frame sizes and checksum.
    /// </summary>
    public void Finish()
    {
        maxFramesize = maxEncodedFramesize;
        byte[] digest = md5Ctx.GetHashAndReset();
        Array.Copy(digest, md5Sum, 16);
        WriteStreaminfo(Extradata);
    }

    /// <summary>flac_encode_close</summary>
    public void Close()
    {
        md5Ctx?.Dispose();
        md5Ctx = null;
        md5Buffer = [];
        lpcCtx.End();
    }
}

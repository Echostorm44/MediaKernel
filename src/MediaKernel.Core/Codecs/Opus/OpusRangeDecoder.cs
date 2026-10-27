// Licensed under the MIT License.
// Port of FFmpeg's libavcodec/opus/rc.c range decoder.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Opus range decoder — faithful port of FFmpeg's OpusRangeCoder (rc.c/rc.h).
/// Uses MSB-first bit reading with range normalization per RFC 6716 Section 4.1.
/// Also supports raw bits read from the end of the frame (used by CELT).
/// </summary>
public ref struct OpusRangeDecoder
{
    // FFmpeg constants from rc.c
    private const int RC_SYM = 8;
    private const uint RC_CEIL = (1u << RC_SYM) - 1;  // 255
    private const uint RC_TOP = 1u << 31;
    private const uint RC_BOT = RC_TOP >> RC_SYM;      // 1u << 23

    // Data buffer
    private readonly ReadOnlySpan<byte> data;

    // MSB-first bit reader state (replaces FFmpeg's GetBitContext)
    private int gbByteOffset;
    private ulong gbBuffer;
    private int gbBitsLeft;

    // Range coder state
    private uint range;
    private uint value;
    private uint totalBits;

    // Raw bits context — reads from end of frame, backwards (used by CELT)
    private int rawPosition;       // byte index counting from end (decrements toward start)
    private uint rawBytes;
    private uint rawCacheLen;
    private uint rawCacheVal;

    /// <summary>Current range value (for checkpointing).</summary>
    public uint Range => range;

    /// <summary>Current value (for checkpointing).</summary>
    public uint Value => value;

    /// <summary>Total bits consumed by the range coder.</summary>
    public uint TotalBits { get => totalBits; set => totalBits = value; }

    /// <summary>Number of raw bytes available for CELT end-of-frame reading.</summary>
    public uint RawBytes => rawBytes;

    /// <summary>Total length of the compressed data in bytes.</summary>
    public int DataLength => data.Length;

    /// <summary>
    /// Initializes the range decoder from compressed frame data.
    /// Port of ff_opus_rc_dec_init().
    /// </summary>
    public OpusRangeDecoder(ReadOnlySpan<byte> data)
    {
        this.data = data;

        // Initialize MSB-first bit reader
        gbByteOffset = 0;
        gbBuffer = 0;
        gbBitsLeft = 0;
        RefillBitBuffer();

        // FFmpeg: rc->range = 128; rc->value = 127 - get_bits(&rc->gb, 7);
        range = 128;
        value = 127 - GetBits(7);
        totalBits = 9;

        // Raw bits (not initialized until InitRaw is called)
        rawPosition = 0;
        rawBytes = 0;
        rawCacheLen = 0;
        rawCacheVal = 0;

        Normalize();
    }

    /// <summary>
    /// Initializes the raw bits reader for CELT end-of-frame bits.
    /// Port of ff_opus_rc_dec_raw_init().
    /// </summary>
    /// <param name="bytes">Number of bytes available for raw reading from end of frame.</param>
    public void InitRaw(uint bytes)
    {
        // rawPosition starts at the end of the data
        rawPosition = data.Length;
        rawBytes = bytes;
        rawCacheLen = 0;
        rawCacheVal = 0;
    }

    /// <summary>
    /// Decodes a symbol using a CDF table.
    /// Port of ff_opus_rc_dec_cdf().
    /// CDF format: [total, cumfreq[0], cumfreq[1], ..., cumfreq[N-1]]
    /// where cumfreq[N-1] == total.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecCdf(ReadOnlySpan<ushort> cdf)
    {
        uint total = cdf[0];
        ReadOnlySpan<ushort> cumFreqs = cdf.Slice(1);

        uint scale = range / total;
        uint symbol = value / scale + 1;
        symbol = total - Math.Min(symbol, total);

        // Linear scan for the symbol
        uint k = 0;
        while (cumFreqs[(int)k] <= symbol)
            k++;

        uint high = cumFreqs[(int)k];
        uint low = k != 0 ? (uint)cumFreqs[(int)(k - 1)] : 0u;

        Update(scale, low, high, total);
        return k;
    }

    /// <summary>
    /// Decodes a symbol with log-domain probability (prob = 1/(2^bits)).
    /// Port of ff_opus_rc_dec_log().
    /// Returns 1 if value &lt; scale (first half), 0 otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecLog(uint bits)
    {
        uint scale = range >> (int)bits;

        if (value >= scale)
        {
            value -= scale;
            range -= scale;
            Normalize();
            return 0;
        }
        else
        {
            range = scale;
            Normalize();
            return 1;
        }
    }

    /// <summary>
    /// Decodes a uniformly distributed integer in [0, size).
    /// Port of ff_opus_rc_dec_uint().
    /// </summary>
    public uint DecUint(uint size)
    {
        uint bits = OpusIlog(size - 1);
        uint total = (bits > 8) ? ((size - 1) >> (int)(bits - 8)) + 1 : size;

        uint scale = range / total;
        uint k = value / scale + 1;
        k = total - Math.Min(k, total);
        Update(scale, k, k + 1, total);

        if (bits > 8)
        {
            k = (k << (int)(bits - 8)) | GetRaw(bits - 8);
            return Math.Min(k, size - 1);
        }
        return k;
    }

    /// <summary>
    /// Decodes a uniform integer with step probability.
    /// Port of ff_opus_rc_dec_uint_step().
    /// Uses probability 3 up to k0, then 1 after.
    /// </summary>
    public uint DecUintStep(int k0)
    {
        uint total = (uint)((k0 + 1) * 3 + k0);
        uint scale = range / total;
        uint symbol = value / scale + 1;
        symbol = total - Math.Min(symbol, total);

        uint k = (symbol < (uint)(k0 + 1) * 3) ? symbol / 3 : symbol - (uint)(k0 + 1) * 2;

        uint low, high;
        if (k <= (uint)k0)
        {
            low = 3 * k;
            high = 3 * (k + 1);
        }
        else
        {
            low = (k - 1 - (uint)k0) + 3 * ((uint)k0 + 1);
            high = (k - (uint)k0) + 3 * ((uint)k0 + 1);
        }

        Update(scale, low, high, total);
        return k;
    }

    /// <summary>
    /// Decodes a triangularly distributed integer.
    /// Port of ff_opus_rc_dec_uint_tri().
    /// </summary>
    public uint DecUintTri(int qn)
    {
        uint total = (uint)(((qn >> 1) + 1) * ((qn >> 1) + 1));
        uint scale = range / total;
        uint center = value / scale + 1;
        center = total - Math.Min(center, total);

        uint k, low, symbol;
        if (center < total >> 1)
        {
            k = ((uint)Math.Sqrt(8.0 * center + 1.0) - 1) >> 1;
            low = k * (k + 1) >> 1;
            symbol = k + 1;
        }
        else
        {
            k = (uint)(2 * (qn + 1) - (uint)Math.Sqrt(8.0 * (total - center - 1) + 1.0)) >> 1;
            low = total - (((uint)qn + 1 - k) * ((uint)qn + 2 - k) >> 1);
            symbol = (uint)qn + 1 - k;
        }

        Update(scale, low, low + symbol, total);
        return k;
    }

    /// <summary>
    /// Decodes a Laplace-distributed value.
    /// Port of ff_opus_rc_dec_laplace().
    /// </summary>
    public int DecLaplace(uint symbol, int decay)
    {
        int val = 0;
        uint scale = range >> 15;
        uint center = value / scale + 1;
        center = (1u << 15) - Math.Min(center, 1u << 15);

        uint low = 0;

        if (center >= symbol)
        {
            val++;
            low = symbol;
            symbol = 1 + ((32768 - 32 - symbol) * (uint)(16384 - decay) >> 15);

            while (symbol > 1 && center >= low + 2 * symbol)
            {
                val++;
                symbol *= 2;
                low += symbol;
                symbol = ((symbol - 2) * (uint)decay >> 15) + 1;
            }

            if (symbol <= 1)
            {
                int distance = (int)((center - low) >> 1);
                val += distance;
                low += (uint)(2 * distance);
            }

            if (center < low + symbol)
                val *= -1;
            else
                low += symbol;
        }

        Update(scale, low, Math.Min(low + symbol, 32768), 32768);
        return val;
    }

    /// <summary>
    /// Reads 1-25 raw bits from the end of the frame (backwards byte-wise).
    /// Port of ff_opus_rc_get_raw(). Used by CELT.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetRaw(uint count)
    {
        while (rawBytes > 0 && rawCacheLen < count)
        {
            rawPosition--;
            rawCacheVal |= (uint)data[rawPosition] << (int)rawCacheLen;
            rawCacheLen += 8;
            rawBytes--;
        }

        uint val = rawCacheVal & ((1u << (int)count) - 1);
        rawCacheVal >>= (int)count;
        rawCacheLen -= count;
        totalBits += count;

        return val;
    }

    /// <summary>
    /// Estimates bits of entropy consumed so far (integer precision).
    /// Port of opus_rc_tell().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Tell()
    {
        return totalBits - (uint)BitOperations.Log2(range) - 1;
    }

    /// <summary>
    /// Estimates bits of entropy consumed so far (1/8th bit precision).
    /// Port of opus_rc_tell_frac().
    /// </summary>
    public uint TellFrac()
    {
        uint tb = totalBits << 3;
        uint rcbuffer = (uint)BitOperations.Log2(range) + 1;
        uint r = range >> (int)(rcbuffer - 16);

        for (int i = 0; i < 3; i++)
        {
            r = r * r >> 15;
            uint bit = r >> 16;
            rcbuffer = (rcbuffer << 1) | bit;
            r >>= (int)bit;
        }

        return tb - rcbuffer;
    }

    // --- Private helpers ---

    /// <summary>
    /// Updates range coder state after decoding a symbol.
    /// Port of opus_rc_dec_update().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Update(uint scale, uint low, uint high, uint total)
    {
        value -= scale * (total - high);
        range = low != 0 ? scale * (high - low)
                         : range - scale * (total - high);
        Normalize();
    }

    /// <summary>
    /// Normalizes range coder state by reading more bits when range gets small.
    /// Port of opus_rc_dec_normalize().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        while (range <= RC_BOT)
        {
            value = ((value << RC_SYM) | (GetBits(RC_SYM) ^ RC_CEIL)) & (RC_TOP - 1);
            range <<= RC_SYM;
            totalBits += RC_SYM;
        }
    }

    /// <summary>
    /// Refills the MSB-first bit buffer from the data stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefillBitBuffer()
    {
        while (gbBitsLeft <= 56 && gbByteOffset < data.Length)
        {
            gbBuffer |= (ulong)data[gbByteOffset++] << (56 - gbBitsLeft);
            gbBitsLeft += 8;
        }
    }

    /// <summary>
    /// Reads n bits MSB-first from the bit buffer.
    /// Equivalent to FFmpeg's get_bits() on GetBitContext.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetBits(int n)
    {
        RefillBitBuffer();
        uint result = (uint)(gbBuffer >> (64 - n));
        gbBuffer <<= n;
        gbBitsLeft -= n;
        return result;
    }

    /// <summary>
    /// Equivalent of FFmpeg's opus_ilog: number of bits needed to represent value.
    /// opus_ilog(i) = av_log2(i) + !!(i)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint OpusIlog(uint i)
    {
        return i != 0 ? (uint)BitOperations.Log2(i) + 1 : 0;
    }
}

/// <summary>
/// Snapshot of range coder state for checkpoint/rollback (used by CELT).
/// </summary>
public struct OpusRangeCoderCheckpoint
{
    public uint Range;
    public uint Value;
    public uint TotalBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OpusRangeCoderCheckpoint Save(ref OpusRangeDecoder rc)
    {
        return new OpusRangeCoderCheckpoint
        {
            Range = rc.Range,
            Value = rc.Value,
            TotalBits = rc.TotalBits,
        };
    }
}

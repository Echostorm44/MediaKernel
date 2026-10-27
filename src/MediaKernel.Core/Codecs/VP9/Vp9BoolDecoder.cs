// VP9 boolean entropy decoder (VPX range coder)
// Ported from FFmpeg libavcodec/vpx_rac.h/c and vp89_rac.h

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>
/// VP9/VPX boolean arithmetic decoder (range coder).
/// Port of FFmpeg's VPXRangeCoder.
/// </summary>
public struct Vp9BoolDecoder
{
    private int high;
    private int bits; // stored negated: negative bits = positive count of bits left
    private byte[] buffer;
    private int bufferPos;
    private int bufferEnd;
    private uint codeWord;
    private int endReached;

    /// <summary>Number of range coder operations performed (incremented by each Renorm).</summary>
    public int CallCount;

    /// <summary>Normalization shift lookup table (ff_vpx_norm_shift).</summary>
    private static ReadOnlySpan<byte> NormShift => new byte[]
    {
        8,7,6,6,5,5,5,5,4,4,4,4,4,4,4,4,
        3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
        2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,
        2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
    };

    public int BufferPosition => bufferPos;

    /// <summary>Get the internal decoder state for diagnostics.</summary>
    public (int High, uint CodeWord, int Bits, int BufPos) GetState() => (high, codeWord, bits, bufferPos);

    /// <summary>Get the raw buffer and remaining size for shadow comparison.</summary>
    public (byte[] Buffer, int Pos, int End) GetBufferState() => (buffer, bufferPos, bufferEnd);

    /// <summary>Initialize the range decoder from a buffer.</summary>
    public bool Init(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 1)
            return false;

        high = 255;
        bits = -16;

        // Callers re-initialise a decoder they already own rather than constructing a fresh one, so
        // Init has to reset every field that construction used to zero - this counter included.
        CallCount = 0;

        // Copied into a buffer this decoder keeps rather than a fresh array each time. Init runs
        // per tile per frame, and a tile is most of the frame's compressed data.
        // Grown geometrically, not to exactly what this tile needs. Tile sizes drift upwards over a
        // stream, so growing to fit meant a fresh buffer for every tile that happened to be a little
        // larger than the last; doubling reaches the stream's largest tile in a few steps and then
        // stops allocating altogether.
        if (buffer is null || buffer.Length < buf.Length)
            buffer = new byte[Math.Max(buf.Length, Math.Max(4096, buffer is null ? 0 : buffer.Length * 2))];
        buf.CopyTo(buffer);

        bufferPos = 0;
        bufferEnd = buf.Length;
        endReached = 0;

        // Read first 3 bytes as big-endian 24-bit value
        codeWord = 0;
        if (bufferPos < bufferEnd) codeWord = (uint)buf[bufferPos++] << 16;
        if (bufferPos < bufferEnd) codeWord |= (uint)buf[bufferPos++] << 8;
        if (bufferPos < bufferEnd) codeWord |= buf[bufferPos++];

        return true;
    }

    /// <summary>Check if we've reached the end of the stream.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnd()
    {
        if (bufferPos >= bufferEnd && bits >= 0)
            endReached++;
        return endReached > 10;
    }

    /// <summary>Renormalize the range decoder state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Renorm()
    {
        int shift = NormShift[high];
        high <<= shift;
        uint code = codeWord << shift;
        int b = bits + shift;

        if (b >= 0 && bufferPos < bufferEnd)
        {
            // Read 16 bits big-endian (matches FFmpeg bytestream_get_be16)
            uint word = 0;
            if (bufferPos < bufferEnd)
                word = (uint)buffer[bufferPos++] << 8;
            if (bufferPos < bufferEnd)
                word |= buffer[bufferPos++];

            code |= word << b;
            b -= 16;
        }

        bits = b;
        CallCount++;
        return code;
    }

    /// <summary>
    /// Decode one bit with the given probability (0-255).
    /// Port of vpx_rac_get_prob.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetProb(byte prob)
    {
        uint code = Renorm();
        uint low = (uint)(1 + ((high - 1) * prob >> 8));
        uint lowShift = low << 16;
        int bit = code >= lowShift ? 1 : 0;

        if (bit != 0)
        {
            high -= (int)low;
            codeWord = code - lowShift;
        }
        else
        {
            high = (int)low;
            codeWord = code;
        }

        return bit;
    }

    /// <summary>
    /// Branchy variant of GetProb — to be used where there's a branch on the result.
    /// Port of vpx_rac_get_prob_branchy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetProbBranchy(int prob)
    {
        uint code = Renorm();
        uint low = (uint)(1 + ((high - 1) * prob >> 8));
        uint lowShift = low << 16;

        if (code >= lowShift)
        {
            high -= (int)low;
            codeWord = code - lowShift;
            return true;
        }

        high = (int)low;
        codeWord = code;
        return false;
    }

    /// <summary>
    /// Decode one equiprobable bit (VP8/VP9 variant).
    /// Port of vp89_rac_get — uses vpx_rac_get_prob(128), NOT vpx_rac_get.
    /// Note: vpx_rac_get uses a slightly different rounding: (high+1)>>1.
    /// VP9 always uses vp89_rac_get which calls vpx_rac_get_prob(c, 128).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get()
    {
        return GetProb(128);
    }

    /// <summary>
    /// Read an unsigned integer of the given bit width (MSB first, equiprobable).
    /// Port of vp89_rac_get_uint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetUint(int numBits)
    {
        int value = 0;
        for (int i = 0; i < numBits; i++)
            value = (value << 1) | Get();
        return value;
    }

    /// <summary>
    /// Decode a value from a binary tree using provided probabilities.
    /// Port of vp89_rac_get_tree.
    /// tree[i][0] = left child, tree[i][1] = right child.
    /// Positive values are interior node indices, negative values are -(leaf_value).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probs)
    {
        // Tree is flat: tree[flatIdx] = left, tree[flatIdx+1] = right.
        // Positive values = next flat index, 0/negative = leaf (return -value).
        // probs[nodeIdx] where nodeIdx = flatIdx >> 1.
        int i = 0;
        do
        {
            int bit = GetProb(probs[i >> 1]);
            i = tree[i + bit];
        } while (i > 0);

        return -i;
    }
}

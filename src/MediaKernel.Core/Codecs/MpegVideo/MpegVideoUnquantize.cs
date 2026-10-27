// Coefficient dequantisation - a translation of ffmpeg-master/libavcodec/mpegvideo_unquantize.c.
//
// The MPEG-1 and MPEG-2 forms are here; the H.263 pair is not, since the MPEG-2 decoder never
// selects it. Per the 2026-10-16 decision the two function pointers on MpegEncContext are gone
// and the caller picks by codec, which for MPEG-1/2 it already knows statically.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Coefficient dequantisation (<c>mpegvideo_unquantize.c</c>).</summary>
/// <remarks>
/// <para>
/// Four things differ between the variants and all four matter for bit-exactness.
/// </para>
/// <para>
/// MPEG-1 forces every dequantised coefficient odd with <c>(level - 1) | 1</c>, the "oddification"
/// the standard requires to avoid an IDCT mismatch between encoder and decoder. MPEG-2 does not,
/// and instead corrects the parity of the whole block once at the end by flipping the lowest bit
/// of coefficient 63 when the sum is even. Confusing the two produces a picture that is nearly
/// right, which is the worst kind of wrong.
/// </para>
/// <para>
/// The shifts differ by one between intra and inter and between MPEG-1 and MPEG-2, because the
/// inter form folds in the <c>(2 * level + 1)</c> reconstruction the standard specifies.
/// </para>
/// <para>
/// MPEG-2 scales <c>qscale</c> before use: doubled normally, or looked up in the non-linear table
/// when <c>q_scale_type</c> is set by the picture coding extension.
/// </para>
/// <para>
/// The loops run to <c>block_last_index[n]</c> and read through the scan table's permutation,
/// not in raster order. The coefficients were scattered by the scan, so the last coded one is a
/// position in scan order and everything past it is zero.
/// </para>
/// </remarks>
public static class MpegVideoUnquantize
{
    /// <summary>
    /// <c>ff_init_scantable</c> - permutes a scan and records, for each position in scan
    /// order, the highest raster index reached so far.
    /// </summary>
    /// <remarks>
    /// <c>raster_end</c> is the half of this function the port did without until H.263: the
    /// unquantisers run to <c>raster_end[block_last_index[n]]</c>, which is the last raster
    /// position any coded coefficient can occupy, and is not the same as the last scan index.
    /// </remarks>
    public static void InitScantable(ref ScanTable st, ReadOnlySpan<byte> permutation,
                                     byte[] srcScantable)
    {
        st.Scantable = srcScantable;

        int end = -1;
        for (int i = 0; i < 64; i++)
        {
            int j = srcScantable[i];
            st.Permutated[i] = permutation[j];
            if (permutation[j] > end)
                end = permutation[j];
            st.RasterEnd[i] = (byte)end;
        }
    }

    /// <summary><c>dct_unquantize_h263_intra_c</c>.</summary>
    /// <remarks>
    /// Coefficient zero is scaled by the DC scale and left alone by the loop, which starts at
    /// one - except under Advanced Intra Coding, where <c>h263_pred_acdc</c> has already
    /// produced the final DC and the adder is zero.
    /// </remarks>
    public static void H263Intra(ref MpegEncContext s, Span<short> block, int n, int qscale)
    {
        int qmul = qscale << 1;
        int qadd;

        if (s.H263Aic == 0)
        {
            block[0] *= (short)(n < 4 ? s.YDcScale : s.CDcScale);
            qadd = (qscale - 1) | 1;
        }
        else
        {
            qadd = 0;
        }

        int nCoeffs = s.AcPred != 0 ? 63 : s.IntraScantable.RasterEnd[s.BlockLastIndex[n]];

        for (int i = 1; i <= nCoeffs; i++)
        {
            int level = block[i];
            if (level != 0)
            {
                level = level < 0 ? level * qmul - qadd : level * qmul + qadd;
                block[i] = (short)level;
            }
        }
    }

    /// <summary><c>dct_unquantize_h263_inter_c</c>.</summary>
    public static void H263Inter(ref MpegEncContext s, Span<short> block, int n, int qscale)
    {
        int qadd = (qscale - 1) | 1;
        int qmul = qscale << 1;

        int nCoeffs = s.InterScantable.RasterEnd[s.BlockLastIndex[n]];

        for (int i = 0; i <= nCoeffs; i++)
        {
            int level = block[i];
            if (level != 0)
            {
                level = level < 0 ? level * qmul - qadd : level * qmul + qadd;
                block[i] = (short)level;
            }
        }
    }

    /// <summary><c>dct_unquantize_mpeg1_intra_c</c>.</summary>
    public static void Mpeg1Intra(
        Span<short> block, int n, int qscale, int lastIndex,
        ReadOnlySpan<byte> permutatedScan, ReadOnlySpan<ushort> intraMatrix,
        int yDcScale, int cDcScale)
    {
        block[0] *= (short)(n < 4 ? yDcScale : cDcScale);

        for (int i = 1; i <= lastIndex; i++)
        {
            int j = permutatedScan[i];
            int level = block[j];
            if (level == 0) continue;

            if (level < 0)
            {
                level = -level;
                level = (level * qscale * intraMatrix[j]) >> 3;
                level = (level - 1) | 1;
                level = -level;
            }
            else
            {
                level = (level * qscale * intraMatrix[j]) >> 3;
                level = (level - 1) | 1;
            }
            block[j] = (short)level;
        }
    }

    /// <summary><c>dct_unquantize_mpeg1_inter_c</c>.</summary>
    public static void Mpeg1Inter(
        Span<short> block, int qscale, int lastIndex,
        ReadOnlySpan<byte> permutatedScan, ReadOnlySpan<ushort> interMatrix)
    {
        for (int i = 0; i <= lastIndex; i++)
        {
            int j = permutatedScan[i];
            int level = block[j];
            if (level == 0) continue;

            if (level < 0)
            {
                level = -level;
                level = (((level << 1) + 1) * qscale * interMatrix[j]) >> 4;
                level = (level - 1) | 1;
                level = -level;
            }
            else
            {
                level = (((level << 1) + 1) * qscale * interMatrix[j]) >> 4;
                level = (level - 1) | 1;
            }
            block[j] = (short)level;
        }
    }

    /// <summary>Scales qscale the way both MPEG-2 forms do, before anything else.</summary>
    private static int Mpeg2Qscale(int qscale, int qScaleType)
        => qScaleType != 0 ? MpegVideoData.Mpeg2NonLinearQscale[qscale] : qscale << 1;

    /// <summary>
    /// <c>dct_unquantize_mpeg2_intra_c</c>, and <c>dct_unquantize_mpeg2_intra_bitexact</c> when
    /// <paramref name="bitExact"/> is set.
    /// </summary>
    /// <remarks>
    /// The two differ only in the parity correction: the bit-exact form accumulates the sum,
    /// starting from the scaled DC, and flips coefficient 63's lowest bit when that sum is even.
    /// FFmpeg selects it under its bitexact flag, which is what FATE runs under, so this is the
    /// form conformance compares against.
    /// </remarks>
    public static void Mpeg2Intra(
        Span<short> block, int n, int qscale, int lastIndex, int qScaleType,
        ReadOnlySpan<byte> permutatedScan, ReadOnlySpan<ushort> intraMatrix,
        int yDcScale, int cDcScale, bool bitExact)
    {
        qscale = Mpeg2Qscale(qscale, qScaleType);

        block[0] *= (short)(n < 4 ? yDcScale : cDcScale);

        int sum = -1;
        if (bitExact) sum += block[0];

        for (int i = 1; i <= lastIndex; i++)
        {
            int j = permutatedScan[i];
            int level = block[j];
            if (level == 0) continue;

            if (level < 0)
            {
                level = -level;
                level = (level * qscale * intraMatrix[j]) >> 4;
                level = -level;
            }
            else
            {
                level = (level * qscale * intraMatrix[j]) >> 4;
            }
            block[j] = (short)level;
            if (bitExact) sum += level;
        }

        if (bitExact)
            block[63] ^= (short)(sum & 1);
    }

    /// <summary><c>dct_unquantize_mpeg2_inter_c</c>.</summary>
    /// <remarks>
    /// Always carries the parity correction, unlike the intra form where it is the bit-exact
    /// variant's distinguishing feature.
    /// </remarks>
    public static void Mpeg2Inter(
        Span<short> block, int qscale, int lastIndex, int qScaleType,
        ReadOnlySpan<byte> permutatedScan, ReadOnlySpan<ushort> interMatrix)
    {
        qscale = Mpeg2Qscale(qscale, qScaleType);

        int sum = -1;
        for (int i = 0; i <= lastIndex; i++)
        {
            int j = permutatedScan[i];
            int level = block[j];
            if (level == 0) continue;

            if (level < 0)
            {
                level = -level;
                level = (((level << 1) + 1) * qscale * interMatrix[j]) >> 5;
                level = -level;
            }
            else
            {
                level = (((level << 1) + 1) * qscale * interMatrix[j]) >> 5;
            }
            block[j] = (short)level;
            sum += level;
        }

        block[63] ^= (short)(sum & 1);
    }
}

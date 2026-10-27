// VC-1's motion compensation - a translation of ffmpeg-master/libavcodec/vc1_mc.c.
//
// All of vc1_mc.c is translated: the four source-block rescalers, the two four-vector
// reducers, and the five entry points.
//
// The rescalers take a plane as a span with an offset and a stride, because the C hands them
// an interior pointer into a picture or into the edge emulation buffer.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// FFmpeg's <c>vc1_mc.c</c>: motion compensation, and the rescalers that range reduction and
/// intensity compensation apply to a source block before interpolating it.
/// </summary>
public static class Vc1Mc
{
    /// <summary>
    /// <c>vc1_scale_luma</c> - range reduction on a <paramref name="k"/> by
    /// <paramref name="k"/> luma block, halving its distance from 128.
    /// </summary>
    public static void Vc1ScaleLuma(Span<byte> srcY, int off, int k, int linesize)
    {
        for (int j = 0; j < k; j++)
        {
            for (int i = 0; i < k; i++)
                srcY[off + i] = (byte)(((srcY[off + i] - 128) >> 1) + 128);
            off += linesize;
        }
    }

    /// <summary><c>vc1_scale_chroma</c> - the same over both chroma planes at once.</summary>
    public static void Vc1ScaleChroma(Span<byte> srcU, int uOff, Span<byte> srcV, int vOff,
                                      int k, int uvlinesize)
    {
        for (int j = 0; j < k; j++)
        {
            for (int i = 0; i < k; i++)
            {
                srcU[uOff + i] = (byte)(((srcU[uOff + i] - 128) >> 1) + 128);
                srcV[vOff + i] = (byte)(((srcV[vOff + i] - 128) >> 1) + 128);
            }
            uOff += uvlinesize;
            vOff += uvlinesize;
        }
    }

    /// <summary>
    /// <c>vc1_lut_scale_luma</c> - intensity compensation, with a different lookup table on
    /// even and odd rows because the two fields of an interlaced picture are compensated
    /// separately.
    /// </summary>
    /// <remarks>
    /// The loop steps two rows at a time and breaks out of the middle when <paramref name="k"/>
    /// is odd, so the last row of an odd-height block is filtered with <paramref name="lut1"/>
    /// and not with <paramref name="lut2"/>. Rewriting it as a single row loop with a parity
    /// test would be the same thing, and is not done because this is what the C does.
    /// </remarks>
    public static void Vc1LutScaleLuma(Span<byte> srcY, int off,
                                       ReadOnlySpan<byte> lut1, ReadOnlySpan<byte> lut2,
                                       int k, int linesize)
    {
        for (int j = 0; j < k; j += 2)
        {
            for (int i = 0; i < k; i++)
                srcY[off + i] = lut1[srcY[off + i]];
            off += linesize;

            if (j + 1 == k)
                break;

            for (int i = 0; i < k; i++)
                srcY[off + i] = lut2[srcY[off + i]];
            off += linesize;
        }
    }

    /// <summary><c>vc1_lut_scale_chroma</c> - the same over both chroma planes.</summary>
    public static void Vc1LutScaleChroma(Span<byte> srcU, int uOff, Span<byte> srcV, int vOff,
                                         ReadOnlySpan<byte> lut1, ReadOnlySpan<byte> lut2,
                                         int k, int uvlinesize)
    {
        for (int j = 0; j < k; j += 2)
        {
            for (int i = 0; i < k; i++)
            {
                srcU[uOff + i] = lut1[srcU[uOff + i]];
                srcV[vOff + i] = lut1[srcV[vOff + i]];
            }
            uOff += uvlinesize;
            vOff += uvlinesize;

            if (j + 1 == k)
                break;

            for (int i = 0; i < k; i++)
            {
                srcU[uOff + i] = lut2[srcU[uOff + i]];
                srcV[vOff + i] = lut2[srcV[vOff + i]];
            }
            uOff += uvlinesize;
            vOff += uvlinesize;
        }
    }

    /// <summary><c>popcount4</c>.</summary>
    private static ReadOnlySpan<byte> Popcount4 =>
        [0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4];

    /// <summary>
    /// <c>index2</c> of <c>get_luma_mv</c> - for each pattern of four field flags with exactly
    /// two set, which two of the four vectors to average, packed as two nibbles.
    /// </summary>
    private static ReadOnlySpan<byte> LumaIndex2 =>
        [0, 0, 0, 0x23, 0, 0x13, 0x03, 0, 0, 0x12, 0x02, 0, 0x01, 0, 0, 0];

    /// <summary><c>index2</c> of <c>get_chroma_mv</c>, which is a different table.</summary>
    private static ReadOnlySpan<byte> ChromaIndex2 =>
        [0, 0, 0, 0x01, 0, 0x02, 0x12, 0, 0, 0x03, 0x13, 0, 0x23, 0, 0, 0];

    /// <summary><c>s-&gt;mv[dir][n][component]</c>, flattened as <c>MpegEncContext.Mv</c> is.</summary>
    private static int Mv(ref MpegEncContext s, int dir, int n, int component) =>
        s.Mv[dir * 8 + n * 2 + component];

    /// <summary>
    /// <c>get_luma_mv</c> - reduce a four-vector macroblock's luma vectors to one, by how many
    /// of the four point at the opposite field.
    /// </summary>
    /// <returns>How many of the four point at the opposite field.</returns>
    public static int GetLumaMv(Vc1Context v, int dir, out short tx, out short ty)
    {
        ref MpegEncContext s = ref v.S;
        byte[] mvF = v.MvFBase!;
        int o = v.MvFOffset[dir] + v.BlocksOff;
        int idx = mvF[o + s.BlockIndex[0]]
                | (mvF[o + s.BlockIndex[1]] << 1)
                | (mvF[o + s.BlockIndex[2]] << 2)
                | (mvF[o + s.BlockIndex[3]] << 3);
        int oppCount = Popcount4[idx];

        switch (oppCount)
        {
            case 0:
            case 4:
                tx = (short)MathOps.Median4(Mv(ref s, dir, 0, 0), Mv(ref s, dir, 1, 0),
                                            Mv(ref s, dir, 2, 0), Mv(ref s, dir, 3, 0));
                ty = (short)MathOps.Median4(Mv(ref s, dir, 0, 1), Mv(ref s, dir, 1, 1),
                                            Mv(ref s, dir, 2, 1), Mv(ref s, dir, 3, 1));
                break;
            case 1:
                tx = (short)MathOps.MidPred(Mv(ref s, dir, idx < 2 ? 1 : 0, 0),
                                            Mv(ref s, dir, 1 + (idx < 4 ? 1 : 0), 0),
                                            Mv(ref s, dir, 2 + (idx < 8 ? 1 : 0), 0));
                ty = (short)MathOps.MidPred(Mv(ref s, dir, idx < 2 ? 1 : 0, 1),
                                            Mv(ref s, dir, 1 + (idx < 4 ? 1 : 0), 1),
                                            Mv(ref s, dir, 2 + (idx < 8 ? 1 : 0), 1));
                break;
            case 3:
                tx = (short)MathOps.MidPred(Mv(ref s, dir, idx > 0xd ? 1 : 0, 0),
                                            Mv(ref s, dir, 1 + (idx > 0xb ? 1 : 0), 0),
                                            Mv(ref s, dir, 2 + (idx > 0x7 ? 1 : 0), 0));
                ty = (short)MathOps.MidPred(Mv(ref s, dir, idx > 0xd ? 1 : 0, 1),
                                            Mv(ref s, dir, 1 + (idx > 0xb ? 1 : 0), 1),
                                            Mv(ref s, dir, 2 + (idx > 0x7 ? 1 : 0), 1));
                break;
            default:  // 2
                tx = (short)((Mv(ref s, dir, LumaIndex2[idx] >> 4, 0)
                              + Mv(ref s, dir, LumaIndex2[idx] & 0xf, 0)) / 2);
                ty = (short)((Mv(ref s, dir, LumaIndex2[idx] >> 4, 1)
                              + Mv(ref s, dir, LumaIndex2[idx] & 0xf, 1)) / 2);
                break;
        }
        return oppCount;
    }

    /// <summary>
    /// <c>get_chroma_mv</c> - reduce the same four vectors to one for chroma, by how many of
    /// the four blocks are inter-coded.
    /// </summary>
    /// <returns>
    /// How many of the four are valid, or 0 when fewer than two are - in which case
    /// <paramref name="tx"/> and <paramref name="ty"/> are left alone, as the C leaves them.
    /// </returns>
    public static int GetChromaMv(Vc1Context v, int dir, ref short tx, ref short ty)
    {
        ref MpegEncContext s = ref v.S;
        byte[] mbType = v.MbTypeBase!;
        int o = v.MbTypeOffset;
        int idx = (mbType[o + s.BlockIndex[0]] == 0 ? 1 : 0)
                | ((mbType[o + s.BlockIndex[1]] == 0 ? 1 : 0) << 1)
                | ((mbType[o + s.BlockIndex[2]] == 0 ? 1 : 0) << 2)
                | ((mbType[o + s.BlockIndex[3]] == 0 ? 1 : 0) << 3);
        int validCount = Popcount4[idx];

        switch (validCount)
        {
            case 4:
                tx = (short)MathOps.Median4(Mv(ref s, dir, 0, 0), Mv(ref s, dir, 1, 0),
                                            Mv(ref s, dir, 2, 0), Mv(ref s, dir, 3, 0));
                ty = (short)MathOps.Median4(Mv(ref s, dir, 0, 1), Mv(ref s, dir, 1, 1),
                                            Mv(ref s, dir, 2, 1), Mv(ref s, dir, 3, 1));
                break;
            case 3:
                tx = (short)MathOps.MidPred(Mv(ref s, dir, idx > 0xd ? 1 : 0, 0),
                                            Mv(ref s, dir, 1 + (idx > 0xb ? 1 : 0), 0),
                                            Mv(ref s, dir, 2 + (idx > 0x7 ? 1 : 0), 0));
                ty = (short)MathOps.MidPred(Mv(ref s, dir, idx > 0xd ? 1 : 0, 1),
                                            Mv(ref s, dir, 1 + (idx > 0xb ? 1 : 0), 1),
                                            Mv(ref s, dir, 2 + (idx > 0x7 ? 1 : 0), 1));
                break;
            case 2:
                tx = (short)((Mv(ref s, dir, ChromaIndex2[idx] >> 4, 0)
                              + Mv(ref s, dir, ChromaIndex2[idx] & 0xf, 0)) / 2);
                ty = (short)((Mv(ref s, dir, ChromaIndex2[idx] >> 4, 1)
                              + Mv(ref s, dir, ChromaIndex2[idx] & 0xf, 1)) / 2);
                break;
            default:
                return 0;
        }
        return validCount;
    }
    // --- The entry points ---------------------------------------------------------------------
    //
    // Each of these predicts one macroblock, or one block of one, into s->dest[] from a
    // reference picture. The shape is the same every time: work out the vector, work out where
    // in the reference it points, clip that to what the profile allows, and then either read the
    // reference directly or - if the block would run off the edge, or needs range reduction or
    // intensity compensation applied first - build a copy in the edge emulation buffer and read
    // that instead. The interpolation at the end is the only part that touches a kernel.
    //
    // In C the "read that instead" is a pointer reassignment. Here the source is a (buffer,
    // offset) pair, because the picture and the emulation buffer are different allocations, and
    // both halves are reassigned together.

    /// <summary><c>s-&gt;mv[i][j][k]</c>, flattened as <c>Int16Flat</c> holds it.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary>The frame's own line size, which a field view's doubled one is derived from.</summary>
    private static int FrameLinesize(ref MpegEncContext s, int plane) =>
        s.BufferPools.Pool!.Picture(s.CurPic.Ptr).Linesize[plane];

    /// <summary>
    /// <c>ff_vc1_mc_1mv</c> - predicts a whole macroblock from one motion vector, luma and
    /// both chroma planes.
    /// </summary>
    public static void Vc1Mc1mv(Vc1Context v, int dir)
    {
        ref MpegEncContext s = ref v.S;
        int vEdgePos = s.VEdgePos >> v.FieldMode;
        int linesizeY = (int)s.Linesize;
        int uvlinesizeY = (int)s.Uvlinesize;

        if ((v.FieldMode == 0 ||
             (v.RefFieldType[dir] == 1 && v.CurFieldType == 1)) &&
            s.LastPic.Frame is null)
            return;

        int linesize = FrameLinesize(ref s, 0);
        int uvlinesize = FrameLinesize(ref s, 1);

        int mx = s.Mv[MvIndex(dir, 0, 0)];
        int my = s.Mv[MvIndex(dir, 0, 1)];

        // store motion vectors for further use in B-frames
        if (s.PictType == AvPictureType.P)
        {
            short[] mvL1 = s.CurPic.MotionValL1!;
            int mo1 = s.CurPic.MotionValOffsetL1;
            for (int i = 0; i < 4; i++)
            {
                mvL1[mo1 + (s.BlockIndex[i] + v.BlocksOff) * 2] = (short)mx;
                mvL1[mo1 + (s.BlockIndex[i] + v.BlocksOff) * 2 + 1] = (short)my;
            }
        }

        int uvmx = (mx + (((mx & 3) == 3) ? 1 : 0)) >> 1;
        int uvmy = (my + (((my & 3) == 3) ? 1 : 0)) >> 1;
        v.LumaMvBase![v.LumaMvOffset + s.MbX * 2 + 0] = (short)uvmx;
        v.LumaMvBase[v.LumaMvOffset + s.MbX * 2 + 1] = (short)uvmy;

        if (v.FieldMode != 0 && v.CurFieldType != v.RefFieldType[dir])
        {
            my = my - 2 + 4 * v.CurFieldType;
            uvmy = uvmy - 2 + 4 * v.CurFieldType;
        }

        // fastuvmc shall be ignored for interlaced frame picture
        if (v.Fastuvmc != 0 && v.Fcm != FrameCodingMode.IlaceFrame)
        {
            uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
            uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
        }

        byte[]? srcBuf;
        int srcY, srcU, srcV;
        byte[] luty;
        byte[] lutuv;
        int useIc;
        int interlace;

        if (dir == 0)
        {
            if (v.FieldMode != 0 && v.CurFieldType != v.RefFieldType[dir] && v.SecondField != 0)
            {
                srcBuf = s.CurPic.Frame;
                srcY = s.CurPic.Data[0];
                srcU = s.CurPic.Data[1];
                srcV = s.CurPic.Data[2];
                luty = v.CurrLuty!;
                lutuv = v.CurrLutuv!;
                useIc = v.CurrUseIc;
                interlace = 1;
            }
            else
            {
                srcBuf = s.LastPic.Frame;
                srcY = s.LastPic.Data[0];
                srcU = s.LastPic.Data[1];
                srcV = s.LastPic.Data[2];
                luty = v.LastLuty;
                lutuv = v.LastLutuv;
                useIc = v.LastUseIc;
                interlace = v.LastInterlaced;
            }
        }
        else
        {
            srcBuf = s.NextPic.Frame;
            srcY = s.NextPic.Data[0];
            srcU = s.NextPic.Data[1];
            srcV = s.NextPic.Data[2];
            luty = v.NextLuty;
            lutuv = v.NextLutuv;
            useIc = v.NextUseIc;
            interlace = v.NextInterlaced;
        }

        if (srcBuf is null)
        {
            // FFmpeg logs "Referenced frame missing." and returns.
            return;
        }

        int srcX = s.MbX * 16 + (mx >> 2);
        int srcYPos = s.MbY * 16 + (my >> 2);
        int uvsrcX = s.MbX * 8 + (uvmx >> 2);
        int uvsrcY = s.MbY * 8 + (uvmy >> 2);

        if (v.Profile != (int)Vc1Profile.Advanced)
        {
            srcX = Math.Clamp(srcX, -16, s.MbWidth * 16);
            srcYPos = Math.Clamp(srcYPos, -16, s.MbHeight * 16);
            uvsrcX = Math.Clamp(uvsrcX, -8, s.MbWidth * 8);
            uvsrcY = Math.Clamp(uvsrcY, -8, s.MbHeight * 8);
        }
        else
        {
            srcX = Math.Clamp(srcX, -17, v.Avctx.CodedWidth);
            uvsrcX = Math.Clamp(uvsrcX, -8, v.Avctx.CodedWidth >> 1);
            if (v.Fcm == FrameCodingMode.IlaceFrame)
            {
                srcYPos = Math.Clamp(srcYPos, -18 + (srcYPos & 1),
                                     v.Avctx.CodedHeight + (srcYPos & 1));
                uvsrcY = Math.Clamp(uvsrcY, -8 + (uvsrcY & 1),
                                    (v.Avctx.CodedHeight >> 1) + (uvsrcY & 1));
            }
            else
            {
                srcYPos = Math.Clamp(srcYPos, -18, v.Avctx.CodedHeight + 1);
                uvsrcY = Math.Clamp(uvsrcY, -8, v.Avctx.CodedHeight >> 1);
            }
        }

        srcY += srcYPos * linesizeY + srcX;
        srcU += uvsrcY * uvlinesizeY + uvsrcX;
        srcV += uvsrcY * uvlinesizeY + uvsrcX;

        if (v.FieldMode != 0 && v.RefFieldType[dir] != 0)
        {
            srcY += linesize;
            srcU += uvlinesize;
            srcV += uvlinesize;
        }

        if (v.Rangeredfrm != 0 || useIc != 0
            || s.HEdgePos < 22 || vEdgePos < 22
            || (uint)(srcX - s.Mspel) > (uint)(s.HEdgePos - (mx & 3) - 16 - s.Mspel * 3)
            || (uint)(srcYPos - 1) > (uint)(vEdgePos - (my & 3) - 16 - 3))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int ybuf = s.Sc.EdgeEmuBufferOffset;
            int ubuf = ybuf + 19 * linesizeY;
            int vbuf = ubuf + 9 * uvlinesizeY;
            int k = 17 + s.Mspel * 2;

            srcY -= s.Mspel * (1 + linesizeY);
            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize << 1, linesize << 1,
                                        k, v.FieldMode != 0 ? k : (k + 1) >> 1,
                                        srcX - s.Mspel,
                                        (srcYPos - s.Mspel) >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos, s.VEdgePos >> 1);
                if (v.FieldMode == 0)
                    VideoDsp.EmulatedEdgeMc(emu, ybuf + linesize, srcBuf, srcY + linesize,
                                            linesize << 1, linesize << 1,
                                            k, k >> 1,
                                            srcX - s.Mspel,
                                            (srcYPos - s.Mspel + 1) >> 1,
                                            s.HEdgePos, s.VEdgePos >> 1);
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize, linesize,
                                        k, v.FieldMode != 0 ? (k << 1) - 1 : k,
                                        srcX - s.Mspel,
                                        v.FieldMode != 0
                                            ? 2 * (srcYPos - s.Mspel) + v.RefFieldType[dir]
                                            : srcYPos - s.Mspel,
                                        s.HEdgePos, s.VEdgePos);
            }

            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                if (v.FieldMode == 0)
                {
                    VideoDsp.EmulatedEdgeMc(emu, ubuf + uvlinesize, srcBuf, srcU + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                    VideoDsp.EmulatedEdgeMc(emu, vbuf + uvlinesize, srcBuf, srcV + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                }
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0
                                            ? 2 * uvsrcY + v.RefFieldType[dir] : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0
                                            ? 2 * uvsrcY + v.RefFieldType[dir] : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
            }

            srcBuf = emu;
            srcY = ybuf;
            srcU = ubuf;
            srcV = vbuf;

            // if we deal with range reduction we need to scale source blocks
            if (v.Rangeredfrm != 0)
            {
                Vc1ScaleLuma(srcBuf, srcY, k, linesizeY);
                Vc1ScaleChroma(srcBuf, srcU, srcBuf, srcV, 9, uvlinesizeY);
            }

            // if we deal with intensity compensation we need to scale source blocks
            if (useIc != 0)
            {
                int ly0 = v.FieldMode != 0 ? v.RefFieldType[dir] : ((0 + srcYPos - s.Mspel) & 1);
                int ly1 = v.FieldMode != 0 ? v.RefFieldType[dir] : ((1 + srcYPos - s.Mspel) & 1);
                int lc0 = v.FieldMode != 0 ? v.RefFieldType[dir] : ((0 + uvsrcY) & 1);
                int lc1 = v.FieldMode != 0 ? v.RefFieldType[dir] : ((1 + uvsrcY) & 1);
                Vc1LutScaleLuma(srcBuf, srcY,
                                luty.AsSpan(ly0 * 256, 256), luty.AsSpan(ly1 * 256, 256),
                                k, linesizeY);
                Vc1LutScaleChroma(srcBuf, srcU, srcBuf, srcV,
                                  lutuv.AsSpan(lc0 * 256, 256), lutuv.AsSpan(lc1 * 256, 256),
                                  9, uvlinesizeY);
            }

            srcY += s.Mspel * (1 + linesizeY);
        }

        byte[] dst = s.CurPic.Frame!;

        if (s.Mspel != 0)
        {
            Vc1Dsp.Vc1MspelMc16<Vc1Dsp.Put>(dst, s.Dest[0], srcBuf, srcY, linesizeY,
                                            mx & 3, my & 3, v.Rnd);
        }
        else
        {
            // hpel mc - always used for luma
            int dxy = (my & 2) | ((mx & 2) >> 1);
            HpelDsp.PixelsTab(dxy, 16, average: false, round: v.Rnd == 0,
                              dst, s.Dest[0], srcBuf, srcY, linesizeY, 16);
        }

        // Chroma MC always uses qpel bilinear
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        if (v.Rnd == 0)
        {
            H264ChromaDsp.PutChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8,
                                           uvmx, uvmy);
            H264ChromaDsp.PutChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8,
                                           uvmx, uvmy);
        }
        else
        {
            Vc1Dsp.PutNoRndVc1ChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8,
                                        uvmx, uvmy);
            Vc1Dsp.PutNoRndVc1ChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8,
                                        uvmx, uvmy);
        }

        if (v.FieldMode != 0)
        {
            byte mvf = (byte)(v.CurFieldType != v.RefFieldType[dir] ? 1 : 0);
            v.MvFBase![v.MvFOffset[dir] + s.BlockIndex[4] + v.MbOff] = mvf;
            v.MvFBase[v.MvFOffset[dir] + s.BlockIndex[5] + v.MbOff] = mvf;
        }
    }
    /// <summary>
    /// <c>ff_vc1_mc_4mv_luma</c> - predicts one 8x8 luma block of a four-vector macroblock.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="Vc1Mc1mv"/> over a quarter of the area, with two additions.
    /// <c>fieldmv</c>, which only an interlaced frame picture can set, makes the block's two
    /// field lines alternate rows of the frame, so every stride doubles and the source row is
    /// picked one line at a time. And an interlaced frame picture clamps by adjusting the
    /// <em>vector</em> rather than the source position, so the fractional part is preserved.
    /// </remarks>
    public static void Vc1Mc4mvLuma(Vc1Context v, int n, int dir, int avg)
    {
        ref MpegEncContext s = ref v.S;
        int fieldmv = v.Fcm == FrameCodingMode.IlaceFrame
            ? v.BlkMvTypeBase![v.BlkMvTypeOffset + s.BlockIndex[n]] : 0;
        int vEdgePos = s.VEdgePos >> v.FieldMode;
        int linesizeY = (int)s.Linesize;

        if ((v.FieldMode == 0 ||
             (v.RefFieldType[dir] == 1 && v.CurFieldType == 1)) &&
            s.LastPic.Frame is null)
            return;

        int linesize = FrameLinesize(ref s, 0);

        int mx = s.Mv[MvIndex(dir, n, 0)];
        int my = s.Mv[MvIndex(dir, n, 1)];

        byte[]? srcBuf;
        int srcY;
        byte[] luty;
        int useIc;
        int interlace;

        if (dir == 0)
        {
            if (v.FieldMode != 0 && v.CurFieldType != v.RefFieldType[dir] && v.SecondField != 0)
            {
                srcBuf = s.CurPic.Frame;
                srcY = s.CurPic.Data[0];
                luty = v.CurrLuty!;
                useIc = v.CurrUseIc;
                interlace = 1;
            }
            else
            {
                srcBuf = s.LastPic.Frame;
                srcY = s.LastPic.Data[0];
                luty = v.LastLuty;
                useIc = v.LastUseIc;
                interlace = v.LastInterlaced;
            }
        }
        else
        {
            srcBuf = s.NextPic.Frame;
            srcY = s.NextPic.Data[0];
            luty = v.NextLuty;
            useIc = v.NextUseIc;
            interlace = v.NextInterlaced;
        }

        if (srcBuf is null)
            return;

        if (v.FieldMode != 0)
        {
            if (v.CurFieldType != v.RefFieldType[dir])
                my = my - 2 + 4 * v.CurFieldType;
        }

        if (s.PictType == AvPictureType.P && n == 3 && v.FieldMode != 0)
        {
            short[] mvL1 = s.CurPic.MotionValL1!;
            int mo1 = s.CurPic.MotionValOffsetL1;
            int at = mo1 + (s.BlockIndex[0] + v.BlocksOff) * 2;
            int oppCount = GetLumaMv(v, 0, out short tx, out short ty);
            mvL1[at] = tx;
            mvL1[at + 1] = ty;
            byte f = (byte)(oppCount > 2 ? 1 : 0);
            for (int k = 0; k < 4; k++)
                v.MvFBase![v.MvFOffset[1] + s.BlockIndex[k] + v.BlocksOff] = f;
        }

        if (v.Fcm == FrameCodingMode.IlaceFrame)
        {
            // not sure if needed for other types of picture
            int width = v.Avctx.CodedWidth;
            int height = v.Avctx.CodedHeight >> 1;
            if (s.PictType == AvPictureType.P)
            {
                short[] mvL1 = s.CurPic.MotionValL1!;
                int mo1 = s.CurPic.MotionValOffsetL1;
                mvL1[mo1 + (s.BlockIndex[n] + v.BlocksOff) * 2] = (short)mx;
                mvL1[mo1 + (s.BlockIndex[n] + v.BlocksOff) * 2 + 1] = (short)my;
            }
            int qx = s.MbX * 16 + (mx >> 2);
            int qy = s.MbY * 8 + (my >> 3);

            if (qx < -17)
                mx -= 4 * (qx + 17);
            else if (qx > width)
                mx -= 4 * (qx - width);
            if (qy < -18)
                my -= 8 * (qy + 18);
            else if (qy > height + 1)
                my -= 8 * (qy - height - 1);
        }

        int off;
        if (v.Fcm == FrameCodingMode.IlaceFrame && fieldmv != 0)
            off = (n > 1 ? linesizeY : 0) + (n & 1) * 8;
        else
            off = linesizeY * 4 * (n & 2) + (n & 1) * 8;

        int srcX = s.MbX * 16 + (n & 1) * 8 + (mx >> 2);
        int srcYPos = fieldmv == 0
            ? s.MbY * 16 + (n & 2) * 4 + (my >> 2)
            : s.MbY * 16 + (n > 1 ? 1 : 0) + (my >> 2);

        if (v.Profile != (int)Vc1Profile.Advanced)
        {
            srcX = Math.Clamp(srcX, -16, s.MbWidth * 16);
            srcYPos = Math.Clamp(srcYPos, -16, s.MbHeight * 16);
        }
        else
        {
            srcX = Math.Clamp(srcX, -17, v.Avctx.CodedWidth);
            if (v.Fcm == FrameCodingMode.IlaceFrame)
                srcYPos = Math.Clamp(srcYPos, -18 + (srcYPos & 1),
                                     v.Avctx.CodedHeight + (srcYPos & 1));
            else
                srcYPos = Math.Clamp(srcYPos, -18, v.Avctx.CodedHeight + 1);
        }

        srcY += srcYPos * linesizeY + srcX;
        if (v.FieldMode != 0 && v.RefFieldType[dir] != 0)
            srcY += linesize;

        if (v.Rangeredfrm != 0 || useIc != 0
            || s.HEdgePos < 13 || vEdgePos < 23
            || (uint)(srcX - s.Mspel) > (uint)(s.HEdgePos - (mx & 3) - 8 - s.Mspel * 2)
            || (uint)(srcYPos - (s.Mspel << fieldmv))
                 > (uint)(vEdgePos - (my & 3) - ((8 + s.Mspel * 2) << fieldmv)))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int ybuf = s.Sc.EdgeEmuBufferOffset;
            int k = 9 + s.Mspel * 2;

            srcY -= s.Mspel * (1 + (linesizeY << fieldmv));
            // check emulate edge stride and offset
            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize << 1, linesize << 1,
                                        k, v.FieldMode != 0 ? k : ((k << fieldmv) + 1) >> 1,
                                        srcX - s.Mspel,
                                        (srcYPos - (s.Mspel << fieldmv))
                                            >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos, s.VEdgePos >> 1);
                if (v.FieldMode == 0 && fieldmv == 0)
                    VideoDsp.EmulatedEdgeMc(emu, ybuf + linesize, srcBuf, srcY + linesize,
                                            linesize << 1, linesize << 1,
                                            k, k >> 1,
                                            srcX - s.Mspel,
                                            (srcYPos - s.Mspel + 1) >> 1,
                                            s.HEdgePos, s.VEdgePos >> 1);
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize, linesize,
                                        k, v.FieldMode != 0 ? (k << 1) - 1 : k << fieldmv,
                                        srcX - s.Mspel,
                                        v.FieldMode != 0
                                            ? 2 * (srcYPos - s.Mspel) + v.RefFieldType[dir]
                                            : srcYPos - (s.Mspel << fieldmv),
                                        s.HEdgePos, s.VEdgePos);
            }

            srcBuf = emu;
            srcY = ybuf;

            // if we deal with range reduction we need to scale source blocks
            if (v.Rangeredfrm != 0)
                Vc1ScaleLuma(srcBuf, srcY, k, linesizeY << fieldmv);

            // if we deal with intensity compensation we need to scale source blocks
            if (useIc != 0)
            {
                int l0 = v.FieldMode != 0
                    ? v.RefFieldType[dir]
                    : (((0 << fieldmv) + srcYPos - (s.Mspel << fieldmv)) & 1);
                int l1 = v.FieldMode != 0
                    ? v.RefFieldType[dir]
                    : (((1 << fieldmv) + srcYPos - (s.Mspel << fieldmv)) & 1);
                Vc1LutScaleLuma(srcBuf, srcY,
                                luty.AsSpan(l0 * 256, 256), luty.AsSpan(l1 * 256, 256),
                                k, linesizeY << fieldmv);
            }

            srcY += s.Mspel * (1 + (linesizeY << fieldmv));
        }

        byte[] dst = s.CurPic.Frame!;

        if (s.Mspel != 0)
        {
            if (avg != 0)
                Vc1Dsp.Vc1MspelMc<Vc1Dsp.Avg>(dst, s.Dest[0] + off, srcBuf, srcY,
                                              linesizeY << fieldmv, mx & 3, my & 3, v.Rnd);
            else
                Vc1Dsp.Vc1MspelMc<Vc1Dsp.Put>(dst, s.Dest[0] + off, srcBuf, srcY,
                                              linesizeY << fieldmv, mx & 3, my & 3, v.Rnd);
        }
        else
        {
            // hpel mc - always used for luma
            int dxy = (my & 2) | ((mx & 2) >> 1);
            HpelDsp.PixelsTab(dxy, 8, average: false, round: v.Rnd == 0,
                              dst, s.Dest[0] + off, srcBuf, srcY, linesizeY, 8);
        }
    }
    /// <summary>
    /// <c>ff_vc1_mc_4mv_chroma</c> - predicts both chroma planes of a four-vector macroblock
    /// from the one vector the four luma vectors reduce to.
    /// </summary>
    /// <remarks>
    /// The reduction is the interesting part: <see cref="GetChromaMv"/> when the picture has
    /// one reference, <see cref="GetLumaMv"/> when a field picture has two, and in the first
    /// case a macroblock whose blocks are all intra has no vector at all, which zeroes the
    /// stored vector and returns without predicting anything.
    /// <para>
    /// The two emulated chroma blocks sit <c>16</c> bytes apart in the edge buffer rather than
    /// a plane apart, which is what the C does and is worth not tidying: the blocks are nine
    /// wide and the second starts at a fixed offset, not at a multiple of the line size.
    /// </para>
    /// </remarks>
    public static void Vc1Mc4mvChroma(Vc1Context v, int dir)
    {
        ref MpegEncContext s = ref v.S;
        int vEdgePos = s.VEdgePos >> v.FieldMode;
        int uvlinesizeY = (int)s.Uvlinesize;
        short tx = 0, ty = 0;
        int chromaRefType;

        if (v.FieldMode == 0 && s.LastPic.Frame is null)
            return;

        short[] mvL1 = s.CurPic.MotionValL1!;
        int at = s.CurPic.MotionValOffsetL1 + (s.BlockIndex[0] + v.BlocksOff) * 2;

        // calculate chroma MV vector from four luma MVs
        if (v.FieldMode == 0 || v.Numref == 0)
        {
            int validCount = GetChromaMv(v, dir, ref tx, ref ty);
            if (validCount == 0)
            {
                mvL1[at] = 0;
                mvL1[at + 1] = 0;
                v.LumaMvBase![v.LumaMvOffset + s.MbX * 2 + 0] = 0;
                v.LumaMvBase[v.LumaMvOffset + s.MbX * 2 + 1] = 0;
                return; // no need to do MC for intra blocks
            }
            chromaRefType = v.RefFieldType[dir];
        }
        else
        {
            int oppCount = GetLumaMv(v, dir, out tx, out ty);
            chromaRefType = v.CurFieldType ^ (oppCount > 2 ? 1 : 0);
        }

        if (v.FieldMode != 0 && chromaRefType == 1 && v.CurFieldType == 1 &&
            s.LastPic.Frame is null)
            return;

        mvL1[at] = tx;
        mvL1[at + 1] = ty;

        int uvlinesize = FrameLinesize(ref s, 1);

        int uvmx = (tx + (((tx & 3) == 3) ? 1 : 0)) >> 1;
        int uvmy = (ty + (((ty & 3) == 3) ? 1 : 0)) >> 1;

        v.LumaMvBase![v.LumaMvOffset + s.MbX * 2 + 0] = (short)uvmx;
        v.LumaMvBase[v.LumaMvOffset + s.MbX * 2 + 1] = (short)uvmy;

        if (v.Fastuvmc != 0)
        {
            uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
            uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
        }
        // Field conversion bias
        if (v.CurFieldType != chromaRefType)
            uvmy += 2 - 4 * chromaRefType;

        int uvsrcX = s.MbX * 8 + (uvmx >> 2);
        int uvsrcY = s.MbY * 8 + (uvmy >> 2);

        if (v.Profile != (int)Vc1Profile.Advanced)
        {
            uvsrcX = Math.Clamp(uvsrcX, -8, s.MbWidth * 8);
            uvsrcY = Math.Clamp(uvsrcY, -8, s.MbHeight * 8);
        }
        else
        {
            uvsrcX = Math.Clamp(uvsrcX, -8, v.Avctx.CodedWidth >> 1);
            uvsrcY = Math.Clamp(uvsrcY, -8, v.Avctx.CodedHeight >> 1);
        }

        byte[]? srcBuf;
        int srcU, srcV;
        byte[] lutuv;
        int useIc;
        int interlace;

        if (dir == 0)
        {
            if (v.FieldMode != 0 && v.CurFieldType != chromaRefType && v.SecondField != 0)
            {
                srcBuf = s.CurPic.Frame;
                srcU = s.CurPic.Data[1];
                srcV = s.CurPic.Data[2];
                lutuv = v.CurrLutuv!;
                useIc = v.CurrUseIc;
                interlace = 1;
            }
            else
            {
                srcBuf = s.LastPic.Frame;
                srcU = s.LastPic.Data[1];
                srcV = s.LastPic.Data[2];
                lutuv = v.LastLutuv;
                useIc = v.LastUseIc;
                interlace = v.LastInterlaced;
            }
        }
        else
        {
            srcBuf = s.NextPic.Frame;
            srcU = s.NextPic.Data[1];
            srcV = s.NextPic.Data[2];
            lutuv = v.NextLutuv;
            useIc = v.NextUseIc;
            interlace = v.NextInterlaced;
        }

        if (srcBuf is null)
            return;

        srcU += uvsrcY * uvlinesizeY + uvsrcX;
        srcV += uvsrcY * uvlinesizeY + uvsrcX;

        if (v.FieldMode != 0 && chromaRefType != 0)
        {
            srcU += uvlinesize;
            srcV += uvlinesize;
        }

        if (v.Rangeredfrm != 0 || useIc != 0
            || s.HEdgePos < 18 || vEdgePos < 18
            || (uint)uvsrcX > (uint)((s.HEdgePos >> 1) - 9)
            || (uint)uvsrcY > (uint)((vEdgePos >> 1) - 9))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int ubuf = s.Sc.EdgeEmuBufferOffset;
            int vbuf = ubuf + 16;

            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                if (v.FieldMode == 0)
                {
                    VideoDsp.EmulatedEdgeMc(emu, ubuf + uvlinesize, srcBuf, srcU + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                    VideoDsp.EmulatedEdgeMc(emu, vbuf + uvlinesize, srcBuf, srcV + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                }
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0 ? 2 * uvsrcY + chromaRefType : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0 ? 2 * uvsrcY + chromaRefType : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
            }

            srcBuf = emu;
            srcU = ubuf;
            srcV = vbuf;

            // if we deal with range reduction we need to scale source blocks
            if (v.Rangeredfrm != 0)
                Vc1ScaleChroma(srcBuf, srcU, srcBuf, srcV, 9, uvlinesizeY);

            // if we deal with intensity compensation we need to scale source blocks
            if (useIc != 0)
            {
                int l0 = v.FieldMode != 0 ? chromaRefType : ((0 + uvsrcY) & 1);
                int l1 = v.FieldMode != 0 ? chromaRefType : ((1 + uvsrcY) & 1);
                Vc1LutScaleChroma(srcBuf, srcU, srcBuf, srcV,
                                  lutuv.AsSpan(l0 * 256, 256), lutuv.AsSpan(l1 * 256, 256),
                                  9, uvlinesizeY);
            }
        }

        byte[] dst = s.CurPic.Frame!;

        // Chroma MC always uses qpel bilinear
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        if (v.Rnd == 0)
        {
            H264ChromaDsp.PutChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8, uvmx, uvmy);
            H264ChromaDsp.PutChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8, uvmx, uvmy);
        }
        else
        {
            Vc1Dsp.PutNoRndVc1ChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8,
                                        uvmx, uvmy);
            Vc1Dsp.PutNoRndVc1ChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8,
                                        uvmx, uvmy);
        }

        if (v.FieldMode != 0)
        {
            byte mvf = (byte)(v.CurFieldType != chromaRefType ? 1 : 0);
            v.MvFBase![v.MvFOffset[dir] + s.BlockIndex[4] + v.MbOff] = mvf;
            v.MvFBase[v.MvFOffset[dir] + s.BlockIndex[5] + v.MbOff] = mvf;
        }
    }
    /// <summary>
    /// <c>s_rndtblfield</c> - the vertical chroma vector a field-coded block's luma vector
    /// reduces to, indexed by the luma vector's low four bits.
    /// </summary>
    private static ReadOnlySpan<byte> SRndTblField =>
        [0, 0, 1, 2, 4, 4, 5, 6, 2, 2, 3, 8, 6, 6, 7, 12];

    /// <summary>
    /// <c>ff_vc1_mc_4mv_chroma4</c> - predicts both chroma planes of an interlaced frame
    /// macroblock as four 4x4 blocks, each with its own vector and its own reference.
    /// </summary>
    /// <remarks>
    /// The other chroma entry point reduces four luma vectors to one; this one keeps all four,
    /// so each quarter is derived, clipped, possibly emulated and interpolated on its own, and
    /// the two halves of the macroblock can even come from different reference pictures -
    /// <paramref name="dir"/> for blocks 0 and 1, <paramref name="dir2"/> for 2 and 3.
    /// <para>
    /// The vertical reduction is a table rather than arithmetic when the block is field-coded,
    /// because the two fields are half a line apart: <see cref="SRndTblField"/>.
    /// </para>
    /// </remarks>
    public static void Vc1Mc4mvChroma4(Vc1Context v, int dir, int dir2, int avg)
    {
        ref MpegEncContext s = ref v.S;
        int uvlinesizeY = (int)s.Uvlinesize;
        int fieldmv = v.BlkMvTypeBase![v.BlkMvTypeOffset + s.BlockIndex[0]];
        int vDist = fieldmv != 0 ? 1 : 4;   // vertical offset for lower sub-blocks
        int vEdgePos = s.VEdgePos >> 1;

        int uvlinesize = FrameLinesize(ref s, 1);

        Span<int> uvmxField = stackalloc int[4];
        Span<int> uvmyField = stackalloc int[4];

        for (int i = 0; i < 4; i++)
        {
            int d = i < 2 ? dir : dir2;
            int tx = s.Mv[MvIndex(d, i, 0)];
            uvmxField[i] = (tx + (((tx & 3) == 3) ? 1 : 0)) >> 1;
            int ty = s.Mv[MvIndex(d, i, 1)];
            if (fieldmv != 0)
                uvmyField[i] = (ty >> 4) * 8 + SRndTblField[ty & 0xF];
            else
                uvmyField[i] = (ty + (((ty & 3) == 3) ? 1 : 0)) >> 1;
        }

        byte[] dst = s.CurPic.Frame!;

        for (int i = 0; i < 4; i++)
        {
            int off = (i & 1) * 4 + ((i & 2) != 0 ? vDist * uvlinesizeY : 0);
            int uvsrcX = s.MbX * 8 + (i & 1) * 4 + (uvmxField[i] >> 2);
            int uvsrcY = s.MbY * 8 + ((i & 2) != 0 ? vDist : 0) + (uvmyField[i] >> 2);
            // FIXME: implement proper pull-back (see vc1cropmv.c, vc1CROPMV_ChromaPullBack())
            uvsrcX = Math.Clamp(uvsrcX, -8, v.Avctx.CodedWidth >> 1);
            if (v.Fcm == FrameCodingMode.IlaceFrame)
                uvsrcY = Math.Clamp(uvsrcY, -8 + (uvsrcY & 1),
                                    (v.Avctx.CodedHeight >> 1) + (uvsrcY & 1));
            else
                uvsrcY = Math.Clamp(uvsrcY, -8, v.Avctx.CodedHeight >> 1);

            byte[]? srcBuf;
            int srcU, srcV;
            byte[] lutuv;
            int useIc;
            int interlace;

            if ((i < 2 ? dir : dir2) != 0)
            {
                srcBuf = s.NextPic.Frame;
                srcU = s.NextPic.Data[1];
                srcV = s.NextPic.Data[2];
                lutuv = v.NextLutuv;
                useIc = v.NextUseIc;
                interlace = v.NextInterlaced;
            }
            else
            {
                srcBuf = s.LastPic.Frame;
                srcU = s.LastPic.Data[1];
                srcV = s.LastPic.Data[2];
                lutuv = v.LastLutuv;
                useIc = v.LastUseIc;
                interlace = v.LastInterlaced;
            }

            if (srcBuf is null)
                return;

            srcU += uvsrcY * uvlinesizeY + uvsrcX;
            srcV += uvsrcY * uvlinesizeY + uvsrcX;
            uvmxField[i] = (uvmxField[i] & 3) << 1;
            uvmyField[i] = (uvmyField[i] & 3) << 1;

            if (useIc != 0
                || s.HEdgePos < 10 || vEdgePos < (5 << fieldmv)
                || (uint)uvsrcX > (uint)((s.HEdgePos >> 1) - 5)
                || (uint)uvsrcY > (uint)(vEdgePos - (5 << fieldmv)))
            {
                byte[] emu = s.Sc.ScratchpadBuf!;
                int ubuf = s.Sc.EdgeEmuBufferOffset;
                int vbuf = ubuf + 16;

                if (interlace != 0)
                {
                    VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                            uvlinesize << 1, uvlinesize << 1,
                                            5, ((5 << fieldmv) + 1) >> 1,
                                            uvsrcX, uvsrcY >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                    VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                            uvlinesize << 1, uvlinesize << 1,
                                            5, ((5 << fieldmv) + 1) >> 1,
                                            uvsrcX, uvsrcY >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                    if (fieldmv == 0)
                    {
                        VideoDsp.EmulatedEdgeMc(emu, ubuf + uvlinesize,
                                                srcBuf, srcU + uvlinesize,
                                                uvlinesize << 1, uvlinesize << 1,
                                                5, 2, uvsrcX, (uvsrcY + 1) >> 1,
                                                s.HEdgePos >> 1, s.VEdgePos >> 2);
                        VideoDsp.EmulatedEdgeMc(emu, vbuf + uvlinesize,
                                                srcBuf, srcV + uvlinesize,
                                                uvlinesize << 1, uvlinesize << 1,
                                                5, 2, uvsrcX, (uvsrcY + 1) >> 1,
                                                s.HEdgePos >> 1, s.VEdgePos >> 2);
                    }
                }
                else
                {
                    VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                            uvlinesize, uvlinesize,
                                            5, 5 << fieldmv, uvsrcX, uvsrcY,
                                            s.HEdgePos >> 1, s.VEdgePos >> 1);
                    VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                            uvlinesize, uvlinesize,
                                            5, 5 << fieldmv, uvsrcX, uvsrcY,
                                            s.HEdgePos >> 1, s.VEdgePos >> 1);
                }

                srcBuf = emu;
                srcU = ubuf;
                srcV = vbuf;

                // if we deal with intensity compensation we need to scale source blocks
                if (useIc != 0)
                {
                    int l0 = (uvsrcY + (0 << fieldmv)) & 1;
                    int l1 = (uvsrcY + (1 << fieldmv)) & 1;
                    Vc1LutScaleChroma(srcBuf, srcU, srcBuf, srcV,
                                      lutuv.AsSpan(l0 * 256, 256), lutuv.AsSpan(l1 * 256, 256),
                                      5, uvlinesizeY << fieldmv);
                }
            }

            int stride = uvlinesizeY << fieldmv;
            if (avg != 0)
            {
                if (v.Rnd == 0)
                {
                    H264ChromaDsp.AvgChromaMc4(dst, s.Dest[1] + off, srcBuf, srcU, stride, 4,
                                               uvmxField[i], uvmyField[i]);
                    H264ChromaDsp.AvgChromaMc4(dst, s.Dest[2] + off, srcBuf, srcV, stride, 4,
                                               uvmxField[i], uvmyField[i]);
                }
                else
                {
                    Vc1Dsp.AvgNoRndVc1ChromaMc4(dst, s.Dest[1] + off, srcBuf, srcU, stride, 4,
                                                uvmxField[i], uvmyField[i]);
                    Vc1Dsp.AvgNoRndVc1ChromaMc4(dst, s.Dest[2] + off, srcBuf, srcV, stride, 4,
                                                uvmxField[i], uvmyField[i]);
                }
            }
            else
            {
                if (v.Rnd == 0)
                {
                    H264ChromaDsp.PutChromaMc4(dst, s.Dest[1] + off, srcBuf, srcU, stride, 4,
                                               uvmxField[i], uvmyField[i]);
                    H264ChromaDsp.PutChromaMc4(dst, s.Dest[2] + off, srcBuf, srcV, stride, 4,
                                               uvmxField[i], uvmyField[i]);
                }
                else
                {
                    Vc1Dsp.PutNoRndVc1ChromaMc4(dst, s.Dest[1] + off, srcBuf, srcU, stride, 4,
                                                uvmxField[i], uvmyField[i]);
                    Vc1Dsp.PutNoRndVc1ChromaMc4(dst, s.Dest[2] + off, srcBuf, srcV, stride, 4,
                                                uvmxField[i], uvmyField[i]);
                }
            }
        }
    }
    /// <summary>
    /// <c>ff_vc1_interp_mc</c> - the backward half of a bidirectional prediction, averaged
    /// into a destination the forward half has already written.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="Vc1Mc1mv"/> against the next picture rather than the last,
    /// with every kernel the averaging variant instead of the replacing one. Two differences
    /// from <see cref="Vc1Mc1mv"/> are FFmpeg's and are reproduced rather than tidied:
    /// <list type="bullet">
    /// <item>
    /// FASTUVMC rounds the <em>other way</em> here. <c>ff_vc1_mc_1mv</c> writes
    /// <c>uvmx + (uvmx &lt; 0 ? uvmx &amp; 1 : -(uvmx &amp; 1))</c>, which rounds towards zero;
    /// this writes the negation of that, which rounds away from it. The specification has one
    /// rule, so one of the two is wrong - but a port that "fixes" it stops matching FFmpeg,
    /// which is the thing being matched.
    /// </item>
    /// <item>
    /// The edge predicate tests <c>src_x - 1</c> where <c>ff_vc1_mc_1mv</c> tests
    /// <c>src_x - s-&gt;mspel</c>, so a half-pel picture takes the emulated path one column
    /// sooner here.
    /// </item>
    /// </list>
    /// </remarks>
    public static void Vc1InterpMc(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        int vEdgePos = s.VEdgePos >> v.FieldMode;
        int useIc = v.NextUseIc;
        int interlace = v.NextInterlaced;
        int linesizeY = (int)s.Linesize;
        int uvlinesizeY = (int)s.Uvlinesize;

        if (v.FieldMode == 0 && s.NextPic.Frame is null)
            return;

        int linesize = FrameLinesize(ref s, 0);
        int uvlinesize = FrameLinesize(ref s, 1);

        int mx = s.Mv[MvIndex(1, 0, 0)];
        int my = s.Mv[MvIndex(1, 0, 1)];
        int uvmx = (mx + (((mx & 3) == 3) ? 1 : 0)) >> 1;
        int uvmy = (my + (((my & 3) == 3) ? 1 : 0)) >> 1;

        if (v.FieldMode != 0 && v.CurFieldType != v.RefFieldType[1])
        {
            my = my - 2 + 4 * v.CurFieldType;
            uvmy = uvmy - 2 + 4 * v.CurFieldType;
        }
        if (v.Fastuvmc != 0)
        {
            // The opposite sign to ff_vc1_mc_1mv's; see the remarks.
            uvmx += uvmx < 0 ? -(uvmx & 1) : uvmx & 1;
            uvmy += uvmy < 0 ? -(uvmy & 1) : uvmy & 1;
        }

        byte[]? srcBuf = s.NextPic.Frame;
        if (srcBuf is null)
            return;
        int srcY = s.NextPic.Data[0];
        int srcU = s.NextPic.Data[1];
        int srcV = s.NextPic.Data[2];

        int srcX = s.MbX * 16 + (mx >> 2);
        int srcYPos = s.MbY * 16 + (my >> 2);
        int uvsrcX = s.MbX * 8 + (uvmx >> 2);
        int uvsrcY = s.MbY * 8 + (uvmy >> 2);

        if (v.Profile != (int)Vc1Profile.Advanced)
        {
            srcX = Math.Clamp(srcX, -16, s.MbWidth * 16);
            srcYPos = Math.Clamp(srcYPos, -16, s.MbHeight * 16);
            uvsrcX = Math.Clamp(uvsrcX, -8, s.MbWidth * 8);
            uvsrcY = Math.Clamp(uvsrcY, -8, s.MbHeight * 8);
        }
        else
        {
            srcX = Math.Clamp(srcX, -17, v.Avctx.CodedWidth);
            uvsrcX = Math.Clamp(uvsrcX, -8, v.Avctx.CodedWidth >> 1);
            if (v.Fcm == FrameCodingMode.IlaceFrame)
            {
                srcYPos = Math.Clamp(srcYPos, -18 + (srcYPos & 1),
                                     v.Avctx.CodedHeight + (srcYPos & 1));
                uvsrcY = Math.Clamp(uvsrcY, -8 + (uvsrcY & 1),
                                    (v.Avctx.CodedHeight >> 1) + (uvsrcY & 1));
            }
            else
            {
                srcYPos = Math.Clamp(srcYPos, -18, v.Avctx.CodedHeight + 1);
                uvsrcY = Math.Clamp(uvsrcY, -8, v.Avctx.CodedHeight >> 1);
            }
        }

        srcY += srcYPos * linesizeY + srcX;
        srcU += uvsrcY * uvlinesizeY + uvsrcX;
        srcV += uvsrcY * uvlinesizeY + uvsrcX;

        if (v.FieldMode != 0 && v.RefFieldType[1] != 0)
        {
            srcY += linesize;
            srcU += uvlinesize;
            srcV += uvlinesize;
        }

        if (v.Rangeredfrm != 0 || s.HEdgePos < 22 || vEdgePos < 22 || useIc != 0
            || (uint)(srcX - 1) > (uint)(s.HEdgePos - (mx & 3) - 16 - 3)
            || (uint)(srcYPos - 1) > (uint)(vEdgePos - (my & 3) - 16 - 3))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int ybuf = s.Sc.EdgeEmuBufferOffset;
            int ubuf = ybuf + 19 * linesizeY;
            int vbuf = ubuf + 9 * uvlinesizeY;
            int k = 17 + s.Mspel * 2;

            srcY -= s.Mspel * (1 + linesizeY);
            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize << 1, linesize << 1,
                                        k, v.FieldMode != 0 ? k : (k + 1) >> 1,
                                        srcX - s.Mspel,
                                        (srcYPos - s.Mspel) >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos, s.VEdgePos >> 1);
                if (v.FieldMode == 0)
                    VideoDsp.EmulatedEdgeMc(emu, ybuf + linesize, srcBuf, srcY + linesize,
                                            linesize << 1, linesize << 1,
                                            k, k >> 1,
                                            srcX - s.Mspel, (srcYPos - s.Mspel + 1) >> 1,
                                            s.HEdgePos, s.VEdgePos >> 1);
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ybuf, srcBuf, srcY,
                                        linesize, linesize,
                                        k, v.FieldMode != 0 ? (k << 1) - 1 : k,
                                        srcX - s.Mspel,
                                        v.FieldMode != 0
                                            ? 2 * (srcYPos - s.Mspel) + v.RefFieldType[1]
                                            : srcYPos - s.Mspel,
                                        s.HEdgePos, s.VEdgePos);
            }

            if (interlace != 0)
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize << 1, uvlinesize << 1,
                                        9, v.FieldMode != 0 ? 9 : 5,
                                        uvsrcX, uvsrcY >> (v.FieldMode != 0 ? 0 : 1),
                                        s.HEdgePos >> 1, s.VEdgePos >> 2);
                if (v.FieldMode == 0)
                {
                    VideoDsp.EmulatedEdgeMc(emu, ubuf + uvlinesize, srcBuf, srcU + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                    VideoDsp.EmulatedEdgeMc(emu, vbuf + uvlinesize, srcBuf, srcV + uvlinesize,
                                            uvlinesize << 1, uvlinesize << 1,
                                            9, 4, uvsrcX, (uvsrcY + 1) >> 1,
                                            s.HEdgePos >> 1, s.VEdgePos >> 2);
                }
            }
            else
            {
                VideoDsp.EmulatedEdgeMc(emu, ubuf, srcBuf, srcU,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0
                                            ? 2 * uvsrcY + v.RefFieldType[1] : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
                VideoDsp.EmulatedEdgeMc(emu, vbuf, srcBuf, srcV,
                                        uvlinesize, uvlinesize,
                                        9, v.FieldMode != 0 ? 17 : 9,
                                        uvsrcX,
                                        v.FieldMode != 0
                                            ? 2 * uvsrcY + v.RefFieldType[1] : uvsrcY,
                                        s.HEdgePos >> 1, s.VEdgePos >> 1);
            }

            srcBuf = emu;
            srcY = ybuf;
            srcU = ubuf;
            srcV = vbuf;

            // if we deal with range reduction we need to scale source blocks
            if (v.Rangeredfrm != 0)
            {
                Vc1ScaleLuma(srcBuf, srcY, k, linesizeY);
                Vc1ScaleChroma(srcBuf, srcU, srcBuf, srcV, 9, uvlinesizeY);
            }

            if (useIc != 0)
            {
                byte[] luty = v.NextLuty;
                byte[] lutuv = v.NextLutuv;
                int ly0 = v.FieldMode != 0 ? v.RefFieldType[1] : ((0 + srcYPos - s.Mspel) & 1);
                int ly1 = v.FieldMode != 0 ? v.RefFieldType[1] : ((1 + srcYPos - s.Mspel) & 1);
                int lc0 = v.FieldMode != 0 ? v.RefFieldType[1] : ((0 + uvsrcY) & 1);
                int lc1 = v.FieldMode != 0 ? v.RefFieldType[1] : ((1 + uvsrcY) & 1);
                Vc1LutScaleLuma(srcBuf, srcY,
                                luty.AsSpan(ly0 * 256, 256), luty.AsSpan(ly1 * 256, 256),
                                k, linesizeY);
                Vc1LutScaleChroma(srcBuf, srcU, srcBuf, srcV,
                                  lutuv.AsSpan(lc0 * 256, 256), lutuv.AsSpan(lc1 * 256, 256),
                                  9, uvlinesizeY);
            }

            srcY += s.Mspel * (1 + linesizeY);
        }

        byte[] dst = s.CurPic.Frame!;

        if (s.Mspel != 0)
        {
            Vc1Dsp.Vc1MspelMc16<Vc1Dsp.Avg>(dst, s.Dest[0], srcBuf, srcY, linesizeY,
                                            mx & 3, my & 3, v.Rnd);
        }
        else
        {
            // hpel mc
            int dxy = (my & 2) | ((mx & 2) >> 1);
            HpelDsp.PixelsTab(dxy, 16, average: true, round: v.Rnd == 0,
                              dst, s.Dest[0], srcBuf, srcY, linesizeY, 16);
        }

        // Chroma MC always uses qpel bilinear
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        if (v.Rnd == 0)
        {
            H264ChromaDsp.AvgChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8,
                                       uvmx, uvmy);
            H264ChromaDsp.AvgChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8,
                                       uvmx, uvmy);
        }
        else
        {
            Vc1Dsp.AvgNoRndVc1ChromaMc8(dst, s.Dest[1], srcBuf, srcU, uvlinesizeY, 8,
                                        uvmx, uvmy);
            Vc1Dsp.AvgNoRndVc1ChromaMc8(dst, s.Dest[2], srcBuf, srcV, uvlinesizeY, 8,
                                        uvmx, uvmy);
        }
    }
}

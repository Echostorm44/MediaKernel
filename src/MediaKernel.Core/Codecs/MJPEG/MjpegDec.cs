// Ported from FFmpeg's libavcodec/mjpegdec.c, with mjpegdec_common.c's VLC builder and the two
// restart helpers that live in mjpegdec.h.
//
// The data layout comes first, before any algorithm: MJpegDecodeContext is what the scan
// decoder reads, and its component arrays - h_count, v_count, comp_index, dc_index, ac_index,
// nb_blocks, h_scount, v_scount - ARE the macroblock loop. They are translated with their
// fields in the C's declared order.

using System;
using System.Buffers.Binary;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>
/// <c>MJpegDecodeContext</c> (<c>mjpegdec.h</c>) - everything one JPEG or Motion JPEG decoder
/// holds.
/// </summary>
/// <remarks>
/// <para>
/// A class rather than a struct: it is large, there is exactly one per decoder, and it owns
/// arrays. The fields are in the order the C declares them.
/// </para>
/// <para>
/// SCOPED, and the omissions are deliberate rather than pending. The C's struct serves four
/// codec ids and several formats this port does not promise - JPEG-LS (<c>ls</c>,
/// <c>jls_state</c>, <c>t1..t3</c>, <c>near</c>, <c>reset</c>, <c>maxval</c>), lossless JPEG
/// (<c>lossless</c>, <c>ljpeg_buffer</c>), Bayer DNG, SMV (<c>smv_*</c>), MxPEG
/// (<c>copy_block</c>, <c>mb_bitmask</c>), hardware acceleration and the EXIF, ICC and
/// stereo3d side data. Each is listed in the function checklist as <c>diverged</c> with its
/// reason rather than left as a field nothing sets.
/// </para>
/// </remarks>
public sealed class MJpegDecodeContext
{
    /// <summary><c>MAX_COMPONENTS</c>.</summary>
    public const int MaxComponents = 4;

    /// <summary><c>gb</c> - the entropy-coded data reader.</summary>
    public GetBitContext Gb;

    /// <summary><c>gB</c> - the byte reader the segment headers are parsed with.</summary>
    public GetByteContext GB;

    /// <summary><c>buf_size</c> - how much of the packet is left to read as segments.</summary>
    public int BufSize;

    /// <summary>
    /// <c>buffer</c> - where a scan is unescaped to, and <c>buffer_size</c> is its length. It
    /// grows to the largest scan seen and is then kept, rather than being reallocated per
    /// frame.
    /// </summary>
    public byte[]? Buffer;

    /// <summary><c>quant_matrixes[4][64]</c>, flattened with a stride of 64.</summary>
    public readonly ushort[] QuantMatrixes = new ushort[4 * 64];

    /// <summary>
    /// <c>vlcs[3][4]</c> - the Huffman books, flattened with a stride of 4: index 0 is the DC
    /// class, 1 the AC class, and 2 is JPEG-LS, which this port does not build.
    /// </summary>
    public readonly VLCElem[]?[] Vlcs = new VLCElem[3 * 4][];

    /// <summary><c>qscale[4]</c> - the quantiser scale derived from each matrix.</summary>
    public readonly int[] Qscale = new int[4];

    /// <summary><c>orig_height</c> - the height the container declared at init.</summary>
    public int OrigHeight;

    /// <summary><c>first_picture</c>.</summary>
    public int FirstPicture;

    /// <summary><c>interlaced</c>.</summary>
    public int Interlaced;

    /// <summary><c>bottom_field</c>.</summary>
    public int BottomField;

    /// <summary><c>progressive</c> - SOF2 rather than SOF0 or SOF1.</summary>
    public int Progressive;

    /// <summary><c>rgb</c> - the components are R, G and B rather than Y, Cb and Cr.</summary>
    public int Rgb;

    /// <summary><c>upscale_h[4]</c> and <c>upscale_v[4]</c> - per-component chroma upscaling
    /// for the subsamplings that have no pixel format of their own.</summary>
    public readonly byte[] UpscaleH = new byte[4];

    /// <inheritdoc cref="UpscaleH"/>
    public readonly byte[] UpscaleV = new byte[4];

    /// <summary><c>bits</c> - bits per component, 8 for everything this port decodes.</summary>
    public int Bits;

    /// <summary><c>adobe_transform</c> - the colour transform an APP14 Adobe segment declares.</summary>
    /// <remarks>
    /// <c>MjpegDecodeInit</c> sets it to -1, and the sentinel is -1 rather than 0 because a
    /// transform of ZERO has a meaning: it says the three components are R, G and B. It
    /// carried a field initialiser saying the same thing for a while; a perturbation sweep
    /// showed that one was dead, and two places to set a sentinel is one too many.
    /// </remarks>
    public int AdobeTransform;

    /// <summary>
    /// <c>cs_itu601</c> - a <c>CS=ITU601</c> comment, which says the stream is limited range.
    /// </summary>
    /// <remarks>
    /// FFmpeg says this by choosing <c>yuv420p</c> over <c>yuvj420p</c>; the SAMPLES are the
    /// same either way, so a framecrc gate cannot see it and <see cref="FullRange"/> is what
    /// carries it here.
    /// </remarks>
    public int CsItu601;

    /// <summary><c>multiscope</c> - 2 for a <c>MULTISCOPE II</c> comment.</summary>
    public int Multiscope;

    /// <summary>
    /// <c>mjpb_skiptosod</c> - padding between SOS and the image data, which only Apple's
    /// MJPEG-B has.
    /// </summary>
    /// <remarks>
    /// MJPEG-B is JPEG with the markers removed and their contents addressed by offsets from
    /// a small header instead, and the offsets to SOS and to the start of data are given
    /// separately. The gap between them is padding, and the number of bytes to skip is
    /// <c>sod_offs - sos_offs - (the SOS segment length)</c>.
    /// </remarks>
    public int MjpbSkiptosod;

    /// <summary>
    /// The entropy data is already unescaped, so <c>MjpegUnescapeSos</c> must hand it over
    /// verbatim rather than scanning it for markers.
    /// </summary>
    /// <remarks>
    /// This is a codec-id test in the C - MEDIA100, MJPEGB and THP - and a flag here,
    /// because this port has no AVCodecContext to read an id from. It is NOT an
    /// optimisation. These formats strip the marker structure, so a 0xFF in the entropy data
    /// is just a byte; unescaping it would stop at the first one that is not followed by
    /// zero and hand the scan a truncated buffer, which is exactly what happened before this
    /// existed: every frame of mjpegb_part.mov parsed its header, its tables and its SOF,
    /// and then failed in the scan.
    /// </remarks>
    public bool InputIsUnescaped;

    /// <summary>
    /// The scan unescapes ONCE at its start and then re-aligns at each restart, instead of
    /// unescaping again. THP only.
    /// </summary>
    /// <remarks>
    /// A codec-id test in the C, and a flag here for the same reason as
    /// <see cref="InputIsUnescaped"/>. THP's entropy data is already unescaped like
    /// MJPEG-B's, so there is nothing to unescape a second time at a restart marker; what is
    /// needed instead is a byte alignment, because the marker left the reader mid-byte.
    /// </remarks>
    public bool AlignedRestarts;

    /// <summary>
    /// <c>avctx-&gt;codec_tag</c> - the container's FourCC, or zero for a bare .jpg file.
    /// </summary>
    /// <remarks>
    /// Read in two places, and for unrelated reasons: an <c>MJPG</c> tag with no declared
    /// field order means bottom-field-first (ffmpeg ticket 3229), and a nonzero tag of ANY
    /// value is what makes the Intel encoder's comment trustworthy as a bottom-up flag.
    /// </remarks>
    public uint CodecTag;

    /// <summary>
    /// <c>avctx-&gt;sample_aspect_ratio</c>. 0/1 until something declares otherwise, which
    /// in this port is only a <c>MULTISCOPE II</c> comment - the JFIF arm's ratio is read by
    /// nothing here.
    /// </summary>
    public int SampleAspectRatioNum;

    /// <inheritdoc cref="SampleAspectRatioNum"/>
    public int SampleAspectRatioDen = 1;

    /// <summary><c>pegasus_rct</c> - Pegasus lossless JPEG's reversible colour transform.</summary>
    public int PegasusRct;

    /// <summary><c>bayer</c> - a Bayer-mosaic lossless JPEG. Nothing in this port sets it.</summary>
    public int Bayer;

    /// <summary><c>colr</c> - an APP segment's colour-space byte.</summary>
    public int Colr;

    /// <summary><c>xfrm</c> - an APP segment's transform byte.</summary>
    public int Xfrm;

    // --- SOS fields ---------------------------------------------------------------------------

    /// <summary><c>nb_components_sos</c> - components in the current scan.</summary>
    public int NbComponentsSos;

    /// <summary><c>Ss</c>, <c>Se</c> - the spectral band this scan codes (progressive).</summary>
    public int Ss;

    /// <inheritdoc cref="Ss"/>
    public int Se;

    /// <summary><c>Ah</c>, <c>Al</c> - the successive approximation bit positions.</summary>
    public int Ah;

    /// <inheritdoc cref="Ah"/>
    public int Al;

    /// <summary><c>width</c>, <c>height</c> - the coded picture size.</summary>
    public int Width;

    /// <inheritdoc cref="Width"/>
    public int Height;

    /// <summary><c>mb_width</c>, <c>mb_height</c> - the size in MCUs.</summary>
    public int MbWidth;

    /// <inheritdoc cref="MbWidth"/>
    public int MbHeight;

    /// <summary><c>nb_components</c> - components in the frame, from SOF.</summary>
    public int NbComponents;

    /// <summary><c>block_stride[MAX_COMPONENTS]</c>.</summary>
    public readonly int[] BlockStride = new int[MaxComponents];

    /// <summary><c>component_id[MAX_COMPONENTS]</c> - the identifier SOF gave each component.</summary>
    public readonly int[] ComponentId = new int[MaxComponents];

    /// <summary><c>h_count</c>, <c>v_count</c> - each component's sampling factors.</summary>
    public readonly int[] HCount = new int[MaxComponents];

    /// <inheritdoc cref="HCount"/>
    public readonly int[] VCount = new int[MaxComponents];

    /// <summary><c>comp_index[MAX_COMPONENTS]</c> - which frame component each scan component is.</summary>
    public readonly int[] CompIndex = new int[MaxComponents];

    /// <summary><c>dc_index</c>, <c>ac_index</c> - the Huffman books the scan selected.</summary>
    public readonly int[] DcIndex = new int[MaxComponents];

    /// <inheritdoc cref="DcIndex"/>
    public readonly int[] AcIndex = new int[MaxComponents];

    /// <summary><c>nb_blocks[MAX_COMPONENTS]</c> - blocks of each component per MCU.</summary>
    public readonly int[] NbBlocks = new int[MaxComponents];

    /// <summary><c>h_scount</c>, <c>v_scount</c> - the sampling factors in the current scan.</summary>
    public readonly int[] HScount = new int[MaxComponents];

    /// <inheritdoc cref="HScount"/>
    public readonly int[] VScount = new int[MaxComponents];

    /// <summary><c>quant_sindex[MAX_COMPONENTS]</c>.</summary>
    public readonly int[] QuantSindex = new int[MaxComponents];

    /// <summary><c>h_max</c>, <c>v_max</c> - the largest sampling factors, which set the MCU size.</summary>
    public int HMax;

    /// <inheritdoc cref="HMax"/>
    public int VMax;

    /// <summary><c>quant_index[4]</c> - the quantiser each component uses.</summary>
    public readonly int[] QuantIndex = new int[4];

    /// <summary><c>last_dc[MAX_COMPONENTS]</c> - the running DC predictor, dequantised.</summary>
    public readonly int[] LastDc = new int[MaxComponents];

    /// <summary><c>got_picture</c> - a SOF has been read and the picture is valid.</summary>
    public int GotPicture;

    /// <summary><c>linesize[MAX_COMPONENTS]</c>, already shifted for an interlaced field.</summary>
    public readonly int[] Linesize = new int[MaxComponents];

    /// <summary><c>block[64]</c> - the one coefficient block a baseline scan decodes into.</summary>
    public readonly short[] Block = new short[64];

    /// <summary>
    /// <c>blocks[MAX_COMPONENTS]</c> - the whole picture's coefficients, which a progressive
    /// scan accumulates across several passes. Null until a progressive SOF allocates them.
    /// </summary>
    public readonly short[]?[] Blocks = new short[MaxComponents][];

    /// <summary><c>last_nnz[MAX_COMPONENTS]</c> - per block, the highest coefficient written.</summary>
    public readonly byte[]?[] LastNnz = new byte[MaxComponents][];

    /// <summary>
    /// <c>coefs_finished[MAX_COMPONENTS]</c> - a bit per coefficient, set once a progressive
    /// scan has finished refining it.
    /// </summary>
    public readonly ulong[] CoefsFinished = new ulong[MaxComponents];

    /// <summary><c>permutated_scantable[64]</c> - the zigzag, permuted by the IDCT's order.</summary>
    public readonly byte[] PermutatedScantable = new byte[64];

    /// <summary><c>restart_interval</c> - MCUs between restart markers, or 0 for none.</summary>
    public int RestartInterval;

    /// <summary><c>restart_count</c>.</summary>
    public int RestartCount;

    /// <summary><c>interlace_polarity</c>.</summary>
    public int InterlacePolarity;

    /// <summary><c>cur_scan</c>.</summary>
    public int CurScan;

    /// <summary>
    /// <c>flipped</c> - the picture is stored bottom-up, which two encoders announce in a
    /// COMMENT and nowhere else.
    /// </summary>
    public int Flipped;

    /// <summary><c>extern_huff</c> - the Huffman tables came from extradata, not the stream.</summary>
    public int ExternHuff;

    /// <summary>
    /// <c>avctx-&gt;pix_fmt</c> - the format SOF chose. On the context rather than on a codec
    /// context stand-in, because it is the only AVCodecContext field the header path writes
    /// that anything downstream reads.
    /// </summary>
    public PixelFormat PixelFormat;

    /// <summary>
    /// <c>picture_ptr-&gt;data</c> and its planes, as one buffer with an offset each - the
    /// shape every decoder in this project hands a frame over in.
    /// </summary>
    public byte[]? Picture;

    /// <inheritdoc cref="Picture"/>
    public readonly int[] PlaneOffset = new int[MaxComponents];

    /// <summary>
    /// What takes the frame buffer once SOF has settled the size and format - FFmpeg's
    /// <c>ff_get_buffer</c>, which is a callback there too.
    /// </summary>
    public Action<MJpegDecodeContext>? PictureAllocator;

    /// <summary>
    /// <c>avctx-&gt;height</c>, which is TWICE <see cref="Height"/> for an interlaced Motion
    /// JPEG: each image in the stream is one field, and <c>Height</c> is the field's.
    /// </summary>
    public int FrameHeight;

    /// <summary>The allocated luma plane size, which is whole macroblocks.</summary>
    public int LumaWidth;

    /// <inheritdoc cref="LumaWidth"/>
    public int LumaHeight;

    /// <summary>The chroma plane size, which the scan's edge test needs.</summary>
    public int ChromaWidth;

    /// <inheritdoc cref="ChromaWidth"/>
    public int ChromaHeight;

    /// <summary>
    /// <c>avctx-&gt;color_range == AVCOL_RANGE_JPEG</c>. FFmpeg says it by choosing a YUVJ
    /// pixel format; this project's <see cref="MediaKernel.Core.PixelFormat"/> carries no
    /// range, so it is said here instead rather than lost.
    /// </summary>
    public bool FullRange;
}

/// <summary><c>mjpegdec.c</c> - the JPEG and Motion JPEG decoder.</summary>
public static class MjpegDec
{
    /// <summary>
    /// <c>build_huffman_codes</c> (<c>mjpegdec_common.c</c>) - expands a BITS array into one
    /// code length per symbol.
    /// </summary>
    /// <remarks>
    /// A JPEG Huffman table is given as sixteen counts - how many codes are two bits long,
    /// three bits long and so on - and a list of symbols in canonical order. This turns the
    /// counts back into the per-symbol lengths a generic VLC builder wants, and returns how
    /// many symbols there were.
    /// </remarks>
    /// <param name="huffSize">Receives one length per symbol; must hold 256.</param>
    /// <param name="bitsTable">The BITS array, seventeen entries with index 0 unused.</param>
    /// <returns>The number of codes, which is the sum of the counts.</returns>
    public static int BuildHuffmanCodes(Span<byte> huffSize, ReadOnlySpan<byte> bitsTable)
    {
        int nbCodes = 0;
        for (int i = 1, j = 0; i <= 16; i++)
        {
            nbCodes += bitsTable[i];
            if (nbCodes > 256)
                throw new InvalidOperationException(
                    $"JPEG: a Huffman table declares {nbCodes} codes, and 256 is the most a "
                  + "byte-symbol alphabet can have.");

            for (; j < nbCodes; j++)
                huffSize[j] = (byte)i;
        }
        return nbCodes;
    }

    /// <summary>
    /// <c>ff_mjpeg_build_vlc</c> (<c>mjpegdec_common.c</c>) - builds one Huffman book from a
    /// DHT segment's BITS and VALS arrays.
    /// </summary>
    /// <remarks>
    /// Two transformations the tables themselves do not show, and both matter to the decoder
    /// that reads the result:
    /// <list type="bullet">
    /// <item>every AC symbol has 16 added to it, which is what lets one switch distinguish a
    /// DC symbol from an AC one;</item>
    /// <item>an AC symbol of zero is the end-of-block code, and becomes <c>16 * 256</c> rather
    /// than 16 - a value no real symbol can take, so the scan decoder recognises EOB without a
    /// separate test.</item>
    /// </list>
    /// </remarks>
    /// <param name="bitsTable">The BITS array, seventeen entries with index 0 unused.</param>
    /// <param name="valTable">The symbols, in canonical order.</param>
    /// <param name="isAc">Non-zero for an AC table.</param>
    /// <returns>The built table.</returns>
    public static VLCElem[] MjpegBuildVlc(ReadOnlySpan<byte> bitsTable, ReadOnlySpan<byte> valTable,
                                          int isAc)
    {
        Span<byte> huffSize = stackalloc byte[256];
        int nbCodes = BuildHuffmanCodes(huffSize, bitsTable);

        var huffSym = new int[nbCodes];
        for (int i = 0; i < nbCodes; i++)
        {
            huffSym[i] = valTable[i] + 16 * isAc;

            if (isAc != 0 && valTable[i] == 0)
                huffSym[i] = 16 * 256;
        }

        // The C's two trailing zeroes are the symbol offset and the flags, both of which this
        // builder defaults; its 1 and 2 and 2 are the element widths of the three C arrays,
        // which a Func indexer has no equivalent of.
        byte[] lengths = huffSize[..nbCodes].ToArray();
        return Vlc.InitFromLengths(9, nbCodes, i => lengths[i], i => huffSym[i]);
    }


    /// <summary><c>AVERROR_INVALIDDATA</c>.</summary>
    public const int AverrorInvalidData = -1094995529;

    /// <summary>
    /// <c>mjpeg_parse_len</c> - reads a segment's declared length and checks it fits.
    /// </summary>
    /// <remarks>
    /// This is the only bounds check the segment parsers make, and it is what lets every read
    /// inside a segment be unchecked: the length covers itself, so a segment of <c>len</c>
    /// bytes has <c>len - 2</c> of payload, and the reader is known to hold that much.
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegParseLen(MJpegDecodeContext s, out int plen, string name)
    {
        ArgumentNullException.ThrowIfNull(s);

        int len = s.GB.GetBe16U();
        if (len < 2 || s.GB.BytesLeft < len - 2)
        {
            plen = 0;
            return AverrorInvalidData;      // "%s: invalid len %d", name, len
        }
        plen = len - 2;
        return 0;
    }

    /// <summary>
    /// <c>ff_mjpeg_decode_dqt</c> - the quantisation tables of one DQT segment.
    /// </summary>
    /// <remarks>
    /// One segment can carry several tables, which is why this loops. A precision byte of 1
    /// means sixteen-bit entries, so a table is 64 or 128 bytes after its one-byte header -
    /// the <c>1 + 64 * (1 + pr)</c> the length is decremented by.
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegDecodeDqt(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        int ret = MjpegParseLen(s, out int len, "dqt");
        if (ret < 0)
            return ret;

        while (len >= 65)
        {
            byte b = s.GB.GetByteU();
            int pr = b >> 4;
            if (pr > 1)
                return AverrorInvalidData;      // "dqt: invalid precision"
            if (len < 1 + 64 * (1 + pr))
                return AverrorInvalidData;

            int index = b & 0x0F;
            if (index >= 4)
                return AverrorInvalidData;

            // read quant table
            for (int i = 0; i < 64; i++)
            {
                s.QuantMatrixes[index * 64 + i] =
                    pr != 0 ? s.GB.GetBe16U() : s.GB.GetByteU();

                // The C logs "dqt: 0 quant value" and carries on, raising it to an error only
                // under AV_EF_EXPLODE. A zero divisor would be a division by zero in
                // dequantisation, but FFmpeg reaches it only for a coefficient that is itself
                // zero in practice, and refusing the picture would be stricter than ffmpeg.
            }

            // XXX FIXME fine-tune, and perhaps add dc too
            s.Qscale[index] = Math.Max(s.QuantMatrixes[index * 64 + 1],
                                       s.QuantMatrixes[index * 64 + 8]) >> 1;
            len -= 1 + 64 * (1 + pr);
        }
        return 0;
    }

    /// <summary>
    /// <c>ff_mjpeg_decode_dht</c> - the Huffman tables of one DHT segment, built into VLCs.
    /// </summary>
    /// <remarks>
    /// An AC table is built twice: once as AC, into <c>vlcs[1]</c>, and once as DC into
    /// <c>vlcs[2]</c>. The second copy is JPEG-LS's, which reads the same book without the
    /// AC symbol offset. This port does not decode JPEG-LS, but the build is kept because
    /// leaving it out would make the two tables disagree about what index 2 holds, and
    /// because it costs one build per table at header time.
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegDecodeDht(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        Span<byte> bitsTable = stackalloc byte[17];
        Span<byte> valTable = stackalloc byte[256];

        int ret = MjpegParseLen(s, out int len, "dht");
        if (ret < 0)
            return ret;

        while (len > 0)
        {
            if (len < 17)
                return AverrorInvalidData;

            byte b = s.GB.GetByteU();
            int cls = b >> 4;
            if (cls >= 2)
                return AverrorInvalidData;
            int index = b & 0x0F;
            if (index >= 4)
                return AverrorInvalidData;

            bitsTable[0] = 0;
            int n = 0;
            for (int i = 1; i <= 16; i++)
            {
                bitsTable[i] = s.GB.GetByteU();
                n += bitsTable[i];
            }
            len -= 17;
            if (len < n || n > 256)
                return AverrorInvalidData;

            valTable[..256].Clear();
            for (int i = 0; i < n; i++)
                valTable[i] = s.GB.GetByteU();
            len -= n;

            // build VLC and flush previous vlc if present
            s.Vlcs[cls * 4 + index] = MjpegBuildVlc(bitsTable, valTable, cls > 0 ? 1 : 0);

            if (cls > 0)
                s.Vlcs[2 * 4 + index] = MjpegBuildVlc(bitsTable, valTable, 0);

            // raw_huffman_lengths and raw_huffman_values are kept for hardware acceleration,
            // which this port has none of; they are recorded in the checklist, not stored.
        }
        return 0;
    }

    /// <summary><c>mjpeg_decode_dri</c> - the restart interval, in MCUs.</summary>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegDecodeDri(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (s.GB.GetBe16U() != 4)
            return AverrorInvalidData;
        s.RestartInterval = s.GB.GetBe16U();
        return 0;
    }

    /// <summary>
    /// <c>ff_mjpeg_find_marker</c> - the next marker at or after <paramref name="pos"/>.
    /// </summary>
    /// <remarks>
    /// A marker is 0xFF followed by a byte in [SOF0, COM]; a run of 0xFF bytes is fill and
    /// only the last one counts. Anything else after an 0xFF - including the 0x00 that escapes
    /// a literal 0xFF inside entropy-coded data - is not a marker and the scan continues.
    /// </remarks>
    /// <param name="buf">The packet.</param>
    /// <param name="pos">Where to start; receives the position just past the marker, or
    /// <paramref name="bufEnd"/> when there is none.</param>
    /// <param name="bufEnd">One past the last byte.</param>
    /// <returns>The marker's second byte, or -1.</returns>
    public static int MjpegFindMarker(ReadOnlySpan<byte> buf, ref int pos, int bufEnd)
    {
        int bufPtr = pos;
        while (true)
        {
            int ff = buf[bufPtr..bufEnd].IndexOf((byte)0xFF);
            if (ff < 0)
                break;

            bufPtr += ff + 1;
            while (bufPtr < bufEnd)
            {
                int val = buf[bufPtr++];
                if (val != 0xFF)
                {
                    if (val >= JpegMarker.Sof0 && val <= JpegMarker.Com)
                    {
                        pos = bufPtr;
                        return val;
                    }
                    break;
                }
            }
        }

        pos = bufEnd;
        return -1;
    }

    /// <summary>
    /// <c>ff_mjpeg_unescape_sos</c> - copies entropy-coded data out of the packet with its
    /// 0xFF escapes removed, stopping at the next marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inside a scan, a literal 0xFF byte is written as 0xFF 0x00, and a run of 0xFF fill
    /// bytes may precede any marker. This undoes the first and skips the second, and stops
    /// where a real marker begins - a restart marker is consumed, anything else is left for
    /// the caller to read.
    /// </para>
    /// <para>
    /// The destination grows to the largest scan seen and is then kept, rather than being
    /// reallocated per scan, which is what <c>av_fast_padded_malloc</c> does in the C. The
    /// padding past the end is zeroed because the bit reader is allowed to run into it.
    /// </para>
    /// <para>
    /// The JPEG-LS arm is not translated: it copies at bit granularity through a
    /// <c>PutBitContext</c> because JPEG-LS escapes differently, and JPEG-LS is out of scope.
    /// </para>
    /// </remarks>
    /// <returns>The length of the unescaped data.</returns>
    public static int MjpegUnescapeSos(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        byte[] src = s.GB.Buffer;
        int bufPtr = s.GB.Position;
        int bufEnd = bufPtr + s.GB.BytesLeft;

        if (s.InputIsUnescaped)
        {
            // "The image data buffer is already unescaped. The only way to find the size of
            // the buffer is by fully decoding it." - the C's own comment, and the reason
            // the whole remaining buffer is handed over.
            int whole = bufEnd - bufPtr;
            if (s.Buffer is null || s.Buffer.Length < whole + InputBufferPaddingSize)
                s.Buffer = new byte[whole + InputBufferPaddingSize];

            src.AsSpan(bufPtr, whole).CopyTo(s.Buffer);
            s.Buffer.AsSpan(whole, InputBufferPaddingSize).Clear();
            s.GB.SkipU(whole);
            return whole;
        }

        int needed = bufEnd - bufPtr + InputBufferPaddingSize;
        if (s.Buffer is null || s.Buffer.Length < needed)
            s.Buffer = new byte[needed];
        byte[] dst = s.Buffer;

        int from = bufPtr;      // src in the C: the start of the run not yet copied
        int ptr = bufPtr;
        int written = 0;

        while (true)
        {
            int ff = src.AsSpan(ptr, bufEnd - ptr).IndexOf((byte)0xFF);
            if (ff < 0)
            {
                // Copy remaining verbatim data.
                ptr = bufEnd;
                break;
            }

            ptr += ff + 1;
            if (ptr >= bufEnd)
            {
                ptr = bufEnd;
                break;
            }

            // Copy verbatim data.
            int length = ptr - 1 - from;
            if (length > 0)
            {
                src.AsSpan(from, length).CopyTo(dst.AsSpan(written));
                written += length;
            }

            byte x = src[ptr++];
            // Discard multiple optional 0xFF fill bytes.
            while (x == 0xFF && ptr < bufEnd)
                x = src[ptr++];

            from = ptr;
            if (x == 0)
            {
                // Stuffed zero byte.
                dst[written++] = 0xFF;
            }
            else if (x >= JpegMarker.Rst0 && x <= JpegMarker.Rst7)
            {
                // Restart marker: consumed, and the scan continues after it.
                goto found;
            }
            else
            {
                // Non-restart marker: left for the caller.
                ptr -= 2;
                goto found;
            }
        }

        {
            int length = ptr - from;
            if (length > 0)
            {
                src.AsSpan(from, length).CopyTo(dst.AsSpan(written));
                written += length;
            }
        }

    found:
        dst.AsSpan(written, InputBufferPaddingSize).Clear();
        s.GB.SkipU(ptr - bufPtr);
        return written;
    }

    /// <summary>
    /// <c>AV_INPUT_BUFFER_PADDING_SIZE</c> - the slack a bit reader may read into.
    /// </summary>
    public const int InputBufferPaddingSize = 64;


    /// <summary><c>AVERROR_PATCHWELCOME</c>.</summary>
    public const int AverrorPatchwelcome = -1463898360;

    /// <summary>
    /// <c>ff_mjpeg_decode_sof</c> - the frame header: size, precision, and one sampling
    /// factor and quantiser index per component.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SCOPED, and what is left out is listed rather than silently skipped. The C's arms for
    /// JPEG-LS, lossless, Bayer DNG, SMV, the AVRn and AVDJ height quirks, the odd_height.mov
    /// hack and hardware acceleration are not translated - each needs a codec id or a flag
    /// this port does not carry. The interlace detection IS translated: Motion JPEG in AVI
    /// uses it, and <c>mjpeg_field_order.avi</c> is one of the conformance samples.
    /// </para>
    /// <para>
    /// The pixel format is chosen from <c>pix_fmt_id</c>, which packs all eight sampling
    /// factors into one integer so that a switch can name each subsampling by the shape of
    /// its components rather than by a chain of comparisons. The two normalisation steps
    /// before the switch divide every factor by two when they are all even, which is how
    /// 2x2:1x1:1x1 and 4x4:2x2:2x2 land on the same case.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeSof(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        Span<int> hCount = stackalloc int[MJpegDecodeContext.MaxComponents];
        Span<int> vCount = stackalloc int[MJpegDecodeContext.MaxComponents];
        hCount.Clear();
        vCount.Clear();

        s.CurScan = 0;
        Array.Clear(s.UpscaleH);
        Array.Clear(s.UpscaleV);

        int ret = MjpegParseLen(s, out int len, "sof");
        if (ret < 0)
            return ret;
        if (len < 6)
            return AverrorInvalidData;

        int bits = s.GB.GetByteU();
        if (bits is > 16 or < 1)
            return AverrorInvalidData;      // "bits %d is invalid"

        int height = s.GB.GetBe16U();
        int width = s.GB.GetBe16U();

        if (width <= 0 || height <= 0)
            return AverrorInvalidData;      // av_image_check_size

        if (s.Progressive == 0)
        {
            // A valid frame requires at least 1 bit for DC + 1 bit for AC for each 8x8 block.
            if (s.BufSize != 0 &&
                (long)((width + 7) / 8) * ((height + 7) / 8) > s.BufSize * 4L)
            {
                return AverrorInvalidData;
            }
        }

        int nbComponents = s.GB.GetByteU();
        if (nbComponents <= 0 || nbComponents > MJpegDecodeContext.MaxComponents)
            return AverrorInvalidData;

        if (s.Interlaced != 0 && s.BottomField == (s.InterlacePolarity == 0 ? 1 : 0))
        {
            if (nbComponents != s.NbComponents)
                return AverrorInvalidData;  // "nb_components changing in interlaced picture"
        }

        len -= 6;
        if (len != 3 * nbComponents)
            return AverrorInvalidData;      // "decode_sof0: error, len mismatch components"

        s.NbComponents = nbComponents;
        s.HMax = 1;
        s.VMax = 1;
        for (int i = 0; i < nbComponents; i++)
        {
            // component id
            s.ComponentId[i] = s.GB.GetByteU();
            byte b = s.GB.GetByteU();
            hCount[i] = b >> 4;
            vCount[i] = b & 0x0F;

            // compute hmax and vmax (only used in interleaved case)
            if (hCount[i] > s.HMax) s.HMax = hCount[i];
            if (vCount[i] > s.VMax) s.VMax = vCount[i];

            s.QuantIndex[i] = s.GB.GetByteU();
            if (s.QuantIndex[i] >= 4)
                return AverrorInvalidData;  // "quant_index is invalid"
            if (hCount[i] == 0 || vCount[i] == 0)
                return AverrorInvalidData;  // "Invalid sampling factor in component"
        }

        if (nbComponents == 4 &&
            s.ComponentId[0] == 'C' && s.ComponentId[1] == 'M' &&
            s.ComponentId[2] == 'Y' && s.ComponentId[3] == 'K')
        {
            s.AdobeTransform = 0;
        }

        // if different size, realloc/alloc picture
        bool sizeChange = width != s.Width || height != s.Height || bits != s.Bits ||
                          !hCount.SequenceEqual(s.HCount) || !vCount.SequenceEqual(s.VCount);

        if (sizeChange)
        {
            s.Width = width;
            s.Height = height;
            s.Bits = bits;
            hCount.CopyTo(s.HCount);
            vCount.CopyTo(s.VCount);
            s.Interlaced = 0;
            s.GotPicture = 0;

            // test interlaced mode: a Motion JPEG field is half the height the container
            // declared, and nothing in the bitstream says so.
            // DIVERGENCE: the C's test is
            //   (s->multiscope != 2 || pkt_timebase.den >= 25 * pkt_timebase.num)
            // and this port has no packet time base to evaluate the right-hand side with, so
            // a MULTISCOPE II stream never takes the interlaced path here where the C would
            // take it at 25 fps or more. Recorded rather than quietly dropped: no sample in
            // this tree is MULTISCOPE II, and inventing a frame rate would be worse.
            if (s.FirstPicture != 0 && s.Multiscope != 2 && s.OrigHeight != 0 &&
                s.Height < s.OrigHeight * 3 / 4)
            {
                s.Interlaced = 1;
                s.BottomField = s.InterlacePolarity;
                height *= 2;
            }

            // avctx->height in the C, which is set by ff_set_dimensions here and then
            // PERSISTS. The local height is only doubled inside this block, so recomputing
            // the frame height outside it gave every frame after the first a picture half
            // the height - the second and third frames of mjpeg_field_order.avi came out at
            // 468x156 while the first was right.
            s.FrameHeight = height;
            s.FirstPicture = 0;
        }

        if (s.GotPicture != 0 && s.Interlaced != 0 &&
            s.BottomField == (s.InterlacePolarity == 0 ? 1 : 0))
        {
            if (s.Progressive != 0)
                return AverrorInvalidData;  // progressively coded interlaced picture
        }
        else
        {
            s.Rgb = 0;

            // XXX: not complete test !
            uint pixFmtId = ((uint)s.HCount[0] << 28) | ((uint)s.VCount[0] << 24) |
                            ((uint)s.HCount[1] << 20) | ((uint)s.VCount[1] << 16) |
                            ((uint)s.HCount[2] << 12) | ((uint)s.VCount[2] << 8) |
                            ((uint)s.HCount[3] << 4) | (uint)s.VCount[3];

            // NOTE we do not allocate pictures large enough for the possible padding of
            // h/v_count being 4
            if ((pixFmtId & 0xD0D0D0D0) == 0)
                pixFmtId -= (pixFmtId & 0xF0F0F0F0) >> 1;
            if ((pixFmtId & 0x0D0D0D0D) == 0)
                pixFmtId -= (pixFmtId & 0x0F0F0F0F) >> 1;

            for (int i = 0; i < 8; i++)
            {
                int j = 6 + (i & 1) - (i & 6);
                int isample = (int)(pixFmtId >> (4 * i)) & 0xF;
                int jsample = (int)(pixFmtId >> (4 * j)) & 0xF;

                if (isample == 1 && jsample != 2 && (i < 2 || i > 5))
                    jsample = (int)(pixFmtId >> (8 + 4 * (i & 1))) & 0xF;
                if (isample == 1 && jsample != 2 && (i < 2 || i > 5))
                    jsample = (int)(pixFmtId >> (16 + 4 * (i & 1))) & 0xF;

                if (isample == 1 && jsample == 2)
                {
                    if ((i & 1) != 0) s.UpscaleH[j / 2] = 1;
                    else s.UpscaleV[j / 2] = 1;
                }
            }

            s.PixelFormat = SelectPixelFormat(s, pixFmtId, bits);

            // ff_get_buffer: the picture can only be taken once the size and the format are
            // both known, which is here and not at init.
            s.PictureAllocator?.Invoke(s);
            s.GotPicture = 1;

            // s->linesize[i] = picture->linesize[i] << s->interlaced. A field picture walks
            // every other line of the frame, so the stride the scan steps by is doubled and
            // the frame's own stride is what the second field is offset by.
            for (int i = 0; i < MJpegDecodeContext.MaxComponents; i++)
                s.Linesize[i] <<= s.Interlaced;
        }

        // totally blank picture as progressive JPEG will only add details to it
        if (s.Progressive != 0)
        {
            int bw = (width + s.HMax * 8 - 1) / (s.HMax * 8);
            int bh = (height + s.VMax * 8 - 1) / (s.VMax * 8);
            for (int i = 0; i < s.NbComponents; i++)
            {
                int size = bw * bh * s.HCount[i] * s.VCount[i];
                s.Blocks[i] = new short[size * 64];
                s.LastNnz[i] = new byte[size];
                s.BlockStride[i] = bw * s.HCount[i];
            }
            Array.Clear(s.CoefsFinished);
        }

        return 0;
    }

    /// <summary>
    /// The pixel format half of <c>ff_mjpeg_decode_sof</c>'s <c>pix_fmt_id</c> switch, for the
    /// subsamplings this port has a plane layout for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SCOPED, and it throws rather than guessing. The C's switch names about forty
    /// <c>pix_fmt_id</c> values across YUV, YUVA, GBR, GBRA, grey, paletted and sixteen-bit
    /// formats. This port's <see cref="PixelFormat"/> has three of them - 4:2:0, 4:2:2 and
    /// 4:4:4 planar - so those are translated with the C's case labels intact and everything
    /// else fails loudly with the id in the message. Of the eleven FATE jpg samples, five
    /// land here and the other six are GBRP or 16-bit grey; both need a pixel format this
    /// project does not have yet, which is a plumbing job rather than a decoder one.
    /// </para>
    /// <para>
    /// FFmpeg's J variants (YUVJ420P and friends) differ from the plain ones only in declared
    /// colour range, and this project's PixelFormat carries no range, so they map to the same
    /// value. The range is recorded on the context for a caller that wants it.
    /// </para>
    /// </remarks>
    private static PixelFormat SelectPixelFormat(MJpegDecodeContext s, uint pixFmtId, int bits)
    {
        // A JPEG whose three components are identified 'R', 'G' and 'B' is planar RGB, not
        // YCbCr, whatever its sampling - all five GBRP samples in the FATE set carry ids 82,
        // 71, 66. An Adobe APP14 segment declaring transform 0 says the same thing about a
        // file whose components are numbered 1, 2, 3 in the ordinary way, and there is
        // nothing else in such a file to go on: without the APP14 it decodes as YCbCr and
        // looks like a colour bug.
        //
        // AdobeTransform starts at -1 and not 0, because 0 is a value with a meaning.
        bool rgbIds = s.NbComponents >= 3 &&
                      s.ComponentId[0] == 'R' && s.ComponentId[1] == 'G' &&
                      s.ComponentId[2] == 'B';
        bool rgb = rgbIds || s.AdobeTransform == 0;

        switch (pixFmtId)
        {
            case 0x11111100:
            case 0x12121100:
            case 0x22122100:
            case 0x21211100:
            case 0x21112100:
            case 0x22211200:
            case 0x22221100:
            case 0x22112200:
            case 0x11222200:
                // 4:4:4, or planar RGB at the same shape. This is the one group where the
                // C tests adobe_transform as well as the component ids; the others test ids
                // alone, and widening them would be a change to FFmpeg rather than a port.
                if (rgb)
                    return PixelFormat.Gbrp;
                if (bits > 8)
                    goto default;
                s.FullRange = s.CsItu601 == 0;
                return PixelFormat.Yuv444P;

            case 0x11311100:
                // Planar RGB with green coded three times as wide as red and blue.
                if (!rgbIds)
                    goto default;
                s.UpscaleH[0] = s.UpscaleH[2] = 2;
                return PixelFormat.Gbrp;

            case 0x11412100:
                // Planar RGB with green four times as wide as red and twice as wide as blue.
                if (!rgbIds)
                    goto default;
                s.UpscaleH[0] = 4;
                s.UpscaleH[1] = 0;
                s.UpscaleH[2] = 1;
                return PixelFormat.Gbrp;

            case 0x31111100:
                // 4:4:4 as well, but luma is coded three times as wide as the chroma, so the
                // two chroma planes are stretched by three afterwards. The nibble loop before
                // this switch only ever asks for a stretch by two; the factors of three and
                // five are per-case, here and in 0x11311100.
                s.FullRange = s.CsItu601 == 0;
                s.UpscaleH[1] = s.UpscaleH[2] = 2;
                return PixelFormat.Yuv444P;

            case 0x21111100:
                if (rgbIds)
                {
                    s.UpscaleH[1] = s.UpscaleH[2] = 1;
                    return PixelFormat.Gbrp;
                }
                s.FullRange = s.CsItu601 == 0;
                return PixelFormat.Yuv422P;

            case 0x22121100:
            case 0x22111200:
            case 0x41211100:
                s.FullRange = s.CsItu601 == 0;
                return PixelFormat.Yuv422P;

            case 0x22111100:
            case 0x23111100:
                s.FullRange = s.CsItu601 == 0;
                return PixelFormat.Yuv420P;

            case 0x42111100:
                s.FullRange = s.CsItu601 == 0;
                s.UpscaleH[1] = s.UpscaleH[2] = 1;
                return PixelFormat.Yuv420P;

            case 0x24111100:
                s.FullRange = s.CsItu601 == 0;
                s.UpscaleV[1] = s.UpscaleV[2] = 1;
                return PixelFormat.Yuv420P;

            case 0x11000000:
            case 0x13000000:
            case 0x14000000:
            case 0x31000000:
            case 0x33000000:
            case 0x34000000:
            case 0x41000000:
            case 0x43000000:
            case 0x44000000:
                // One component: greyscale. The C's force_pal8 arm is out of scope.
                return bits <= 8 ? PixelFormat.Gray8 : PixelFormat.Gray16;

            default:
                // unk_pixfmt in the C, which logs the id and returns AVERROR_PATCHWELCOME.
                throw new NotSupportedException(
                    $"JPEG: unhandled component shape 0x{pixFmtId:X8} "
                  + $"({s.NbComponents} components, {bits} bits). 4:2:0, 4:2:2, 4:4:4, GBRP "
                  + "and grey are handled; CMYK and the alpha formats need a pixel format "
                  + "this project does not have.");
        }
    }


    /// <summary>
    /// <c>ff_mjpeg_decode_sos</c> - the scan header, then the scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan header maps each scan component back to a frame component and picks its two
    /// Huffman books, which is what makes a non-interleaved scan possible: a progressive JPEG
    /// codes one component at a time and the MCU grid is different for each.
    /// </para>
    /// <para>
    /// SCOPED: the lossless, JPEG-LS, hwaccel and mjpeg-b arms are not translated. The
    /// Metasoft MTSJ quirk, which swaps Cb and Cr, needs a codec tag this port does not carry
    /// and is recorded rather than guessed at.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeSos(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        const int blockSize = 8;    // s->lossless ? 1 : 8, and lossless is out of scope

        if (s.GotPicture == 0)
            return AverrorInvalidData;  // "Can not process SOS before SOF, skipping"

        int ret = MjpegParseLen(s, out int len, "sos");
        if (ret < 0)
            return ret;
        if (len < 1)
            return AverrorInvalidData;

        s.NbComponentsSos = s.GB.GetByteU();
        if (s.NbComponentsSos == 0 || s.NbComponentsSos > MJpegDecodeContext.MaxComponents)
            return AverrorPatchwelcome;     // "decode_sos: nb_components (%d)"

        if (len != 4 + 2 * s.NbComponentsSos)
            return AverrorInvalidData;

        for (int i = 0; i < s.NbComponentsSos; i++)
        {
            int id = s.GB.GetByteU();

            // find component index
            int index;
            for (index = 0; index < s.NbComponents; index++)
            {
                if (id == s.ComponentId[index])
                    break;
            }
            if (index == s.NbComponents)
                return AverrorInvalidData;  // "decode_sos: index out of components"

            s.QuantSindex[i] = s.QuantIndex[index];
            s.NbBlocks[i] = s.HCount[index] * s.VCount[index];
            s.HScount[i] = s.HCount[index];
            s.VScount[i] = s.VCount[index];
            s.CompIndex[i] = index;

            byte b = s.GB.GetByteU();
            s.DcIndex[i] = b >> 4;
            s.AcIndex[i] = b & 0x0F;

            if (s.DcIndex[i] >= 4 || s.AcIndex[i] >= 4)
                return AverrorInvalidData;  // out_of_range

            // The AC book a progressive scan reads is vlcs[2], the copy decode_dht builds
            // without the AC symbol offset, and it is indexed by ac_index[0] and not [i] -
            // reproduced as written.
            if (s.Vlcs[0 * 4 + s.DcIndex[i]] is null ||
                (s.Progressive != 0 ? s.Vlcs[2 * 4 + s.AcIndex[0]]
                                    : s.Vlcs[1 * 4 + s.AcIndex[i]]) is null)
            {
                return AverrorInvalidData;  // out_of_range
            }
        }

        s.Ss = s.GB.GetByteU();     // JPEG Ss / lossless JPEG predictor / JPEG-LS NEAR
        s.Se = s.GB.GetByteU();     // JPEG Se / JPEG-LS ILV
        byte ahAl = s.GB.GetByteU();
        s.Ah = ahAl >> 4;
        s.Al = ahAl & 0x0F;

        if (s.NbComponentsSos > 1)
        {
            // interleaved stream
            s.MbWidth = (s.Width + s.HMax * blockSize - 1) / (s.HMax * blockSize);
            s.MbHeight = (s.Height + s.VMax * blockSize - 1) / (s.VMax * blockSize);
        }
        else
        {
            int h = s.HMax / s.HScount[0];
            int v = s.VMax / s.VScount[0];
            s.MbWidth = (s.Width + h * blockSize - 1) / (h * blockSize);
            s.MbHeight = (s.Height + v * blockSize - 1) / (v * blockSize);
            s.NbBlocks[0] = 1;
            s.HScount[0] = 1;
            s.VScount[0] = 1;
        }

        // "mjpeg-b can have padding bytes between sos and image data, skip them" - the C's
        // own comment. Zero for every other variant, so this costs a test on a path that
        // already has one.
        if (s.MjpbSkiptosod != 0)
            s.GB.SkipU(s.MjpbSkiptosod);

        int scanResult = s.Progressive != 0 && s.Ss != 0
            ? MjpegDecodeScanProgressiveAc(s)
            : MjpegDecodeScan(s);

        if (s.InputIsUnescaped)
        {
            // "Add the amount of bits read from the unescaped image data buffer into the
            // GetByteContext." For the escaped formats MjpegUnescapeSos advances s.GB as it
            // copies, so the byte reader is already past the scan; for these it never
            // touched it, and the marker loop would re-read the entropy data as segments.
            s.GB.SkipU((s.Gb.Index + 7) / 8);
        }

        return scanResult;
    }

    /// <summary>
    /// <c>ff_mjpeg_decode_frame_from_buf</c>'s segment loop - walks the markers of one packet
    /// and dispatches each.
    /// </summary>
    /// <remarks>
    /// SCOPED: the APP, COM, DNL, EXP and DHP segments are skipped by length rather than
    /// parsed, because what they carry - EXIF, ICC, Adobe's colour transform, Pegasus's RCT,
    /// stereo3d - reaches nothing this port decodes. The ones that DO change the decode are
    /// the Adobe transform and the AVI field order, and both are recorded as todo rather than
    /// silently absent. JPEG-LS and lossless SOF markers throw.
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeFrameFromBuf(MJpegDecodeContext s, byte[] buf, int bufSize)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(buf);

        s.BufSize = bufSize;
        int bufPtr = 0;

        while (bufPtr < bufSize)
        {
            // find start next marker
            int startCode = MjpegFindMarker(buf, ref bufPtr, bufSize);
            if (startCode < 0)
                break;

            int bytesLeft = bufSize - bufPtr;
            s.GB.Init(buf, bufPtr, bytesLeft);

            // process markers
            if (startCode >= JpegMarker.Rst0 && startCode <= JpegMarker.Rst7)
            {
                // nothing to do with RSTn: restart markers inside a scan are consumed by the
                // unescaper, and one out here is a stray.
                bufPtr = s.GB.Position;
                continue;
            }

            if (startCode >= JpegMarker.App0 && startCode <= JpegMarker.App15)
            {
                int r = MjpegDecodeApp(s, startCode);
                if (r < 0)
                    return r;
                bufPtr = s.GB.Position;
                continue;
            }

            if (startCode == JpegMarker.Com)
            {
                int r = MjpegDecodeCom(s);
                if (r < 0)
                    return r;
                bufPtr = s.GB.Position;
                continue;
            }

            switch (startCode)
            {
                case JpegMarker.Soi:
                    s.RestartInterval = 0;
                    break;      // nothing to do on SOI

                case JpegMarker.Dqt:
                {
                    int r = MjpegDecodeDqt(s);
                    if (r < 0)
                        return r;
                    break;
                }

                case JpegMarker.Dht:
                {
                    int r = MjpegDecodeDht(s);
                    if (r < 0)
                        return r;
                    break;
                }

                case JpegMarker.Sof0:
                case JpegMarker.Sof1:
                    s.Progressive = 0;
                    {
                        int r = MjpegDecodeSof(s);
                        if (r < 0)
                            return r;
                    }
                    break;

                case JpegMarker.Sof2:
                    s.Progressive = 1;
                    {
                        int r = MjpegDecodeSof(s);
                        if (r < 0)
                            return r;
                    }
                    break;

                case JpegMarker.Sof3:
                case JpegMarker.Sof55:
                    throw new NotSupportedException(
                        startCode == JpegMarker.Sof3
                            ? "JPEG: lossless (SOF3) is out of scope for this port."
                            : "JPEG: JPEG-LS (SOF55) is out of scope for this port.");

                case JpegMarker.Sof5:
                case JpegMarker.Sof6:
                case JpegMarker.Sof7:
                case JpegMarker.Sof9:
                case JpegMarker.Sof10:
                case JpegMarker.Sof11:
                case JpegMarker.Sof13:
                case JpegMarker.Sof14:
                case JpegMarker.Sof15:
                    // The C's "SOF %d is not supported" arm: differential and
                    // arithmetic-coded JPEG, which ffmpeg does not decode either.
                    return AverrorPatchwelcome;

                case JpegMarker.Eoi:
                    // eoi_parser: a progressive stream has been accumulating coefficients in
                    // blocks[] across every scan, and this is where the whole picture is
                    // transformed at once.
                    if (s.Progressive != 0 && s.CurScan != 0 && s.GotPicture != 0)
                        MjpegIdctScanProgressiveAc(s);
                    s.CurScan = 0;

                    if (s.Interlaced != 0)
                    {
                        s.BottomField ^= 1;

                        // if not bottom field, do not output image yet - the other field is
                        // the next image in this same packet, and it writes into the lines
                        // this one skipped.
                        if (s.BottomField == (s.InterlacePolarity == 0 ? 1 : 0))
                        {
                            bufPtr = s.GB.Position;
                            continue;
                        }
                    }
                    if (s.GotPicture != 0)
                    {
                        UpscalePlanes(s);
                        FlipPlanes(s);

                        if (s.PixelFormat == PixelFormat.Gbrp)
                        {
                            // The scan wrote the components in the order SOF listed them,
                            // which for an RGB JPEG is R, G, B. GBRP wants G, B, R, and the
                            // C gets there with two swaps rather than a rename.
                            (s.PlaneOffset[0], s.PlaneOffset[2]) =
                                (s.PlaneOffset[2], s.PlaneOffset[0]);
                            (s.PlaneOffset[0], s.PlaneOffset[1]) =
                                (s.PlaneOffset[1], s.PlaneOffset[0]);
                            (s.Linesize[0], s.Linesize[2]) = (s.Linesize[2], s.Linesize[0]);
                            (s.Linesize[0], s.Linesize[1]) = (s.Linesize[1], s.Linesize[0]);
                        }
                    }
                    return 0;

                case JpegMarker.Sos:
                {
                    s.CurScan++;
                    int r = MjpegDecodeSos(s);
                    if (r < 0)
                        return r;
                    break;
                }

                case JpegMarker.Dri:
                {
                    int r = MjpegDecodeDri(s);
                    if (r < 0)
                        return r;
                    break;
                }

                case JpegMarker.Dnl:
                case JpegMarker.Dhp:
                case JpegMarker.Exp:
                case JpegMarker.Dac:
                {
                    // Skipped by length, as the C does for everything it does not read.
                    int r = MjpegParseLen(s, out int skipLen, "unhandled");
                    if (r < 0)
                        return r;
                    s.GB.SkipU(skipLen);
                    break;
                }

                default:
                    // Unknown input fails loudly is the rule, but not here: an unrecognised
                    // marker in [SOF0, COM] is one this decoder does not read, and the C
                    // skips it too. find_marker already rejected anything outside that range.
                    break;
            }

            bufPtr = s.GB.Position;
        }

        return 0;
    }



    /// <summary>
    /// The <c>upscale_h</c> and <c>upscale_v</c> passes of
    /// <c>ff_mjpeg_decode_frame_from_buf</c> - stretches a plane that was coded smaller than
    /// the pixel format it lands in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A JPEG may give its components sampling factors that no pixel format matches - 2x1,
    /// 1x1, 2x1 is one of the FATE samples, where luma and Cr are full width and Cb is half.
    /// FFmpeg picks the format that fits the LARGEST component and stretches the others in
    /// place after the scan, which is what <c>ff_mjpeg_decode_sof</c>'s loop over
    /// <c>pix_fmt_id</c> nibbles decides.
    /// </para>
    /// <para>
    /// The stretch is done backwards, from the last column inwards, so that it can be done in
    /// place; the last column is a copy and every other is the average of the two source
    /// columns it falls between. Only the x2 case is translated - x3 and x5 need sampling
    /// factors of 3 and 4, which no sample here has, and they throw.
    /// </para>
    /// </remarks>
    private static void UpscalePlanes(MJpegDecodeContext s)
    {
        (int hShift, int vShift) = ChromaShift(s.PixelFormat);

        for (int p = 0; p < s.NbComponents; p++)
        {
            if (s.UpscaleH[p] == 0)
                continue;
            if (s.UpscaleH[p] is not (1 or 2 or 4))
            {
                throw new NotSupportedException(
                    $"JPEG: horizontal upscale code {s.UpscaleH[p]} on plane {p}.");
            }

            int w = s.Width, h = s.Height;
            if (p is 1 or 2)
            {
                w = (w + (1 << hShift) - 1) >> hShift;
                h = (h + (1 << vShift) - 1) >> vShift;
            }
            if (s.UpscaleV[p] == 1)
                h = (h + 1) >> 1;

            byte[] pic = s.Picture!;
            int linesize = s.Linesize[p];
            for (int i = 0; i < h; i++)
            {
                int line = s.PlaneOffset[p] + i * linesize;

                if (s.UpscaleH[p] == 1)
                {
                    // By two: the last column is a copy and every other is the average of
                    // the two source columns it falls between. Backwards, so it is in place.
                    pic[line + w - 1] = pic[line + (w - 1) / 2];
                    for (int index = w - 2; index > 0; index--)
                        pic[line + index] = (byte)((pic[line + index / 2] +
                                                    pic[line + (index + 1) / 2]) >> 1);
                }
                else if (s.UpscaleH[p] == 2)
                {
                    // By three. The two last columns are copies and the rest average three
                    // source columns - with a +1 before the divide, which the by-two arm's
                    // shift does not have.
                    pic[line + w - 1] = pic[line + (w - 1) / 3];
                    if (w > 1)
                        pic[line + w - 2] = pic[line + w - 1];
                    for (int index = w - 3; index > 0; index--)
                    {
                        pic[line + index] = (byte)((pic[line + index / 3] +
                                                    pic[line + (index + 1) / 3] +
                                                    pic[line + (index + 2) / 3] + 1) / 3);
                    }
                }
                else
                {
                    // By five, and not by four - upscale_h is one less than the factor only
                    // for the first two codes. The three last columns each have their own
                    // rule and the rest average four source columns.
                    pic[line + w - 1] = pic[line + ((w - 1) >> 2)];
                    if (w > 1)
                        pic[line + w - 2] = (byte)((pic[line + ((w - 1) >> 2)] * 3 +
                                                    pic[line + ((w - 2) >> 2)]) >> 2);
                    if (w > 2)
                        pic[line + w - 3] = (byte)((pic[line + ((w - 1) >> 2)] +
                                                    pic[line + ((w - 2) >> 2)]) >> 1);
                    for (int index = w - 4; index > 0; index--)
                    {
                        pic[line + index] = (byte)((pic[line + ((index + 3) >> 2)] +
                                                    pic[line + ((index + 2) >> 2)] +
                                                    pic[line + ((index + 1) >> 2)] +
                                                    pic[line + (index >> 2)]) >> 2);
                    }
                }
            }
        }

        for (int p = 0; p < s.NbComponents; p++)
        {
            if (s.UpscaleV[p] == 0)
                continue;
            if (s.UpscaleV[p] != 1)
            {
                throw new NotSupportedException(
                    $"JPEG: vertical upscale by {s.UpscaleV[p] + 1} on plane {p}, which needs "
                  + "a sampling factor of 3 or more.");
            }

            int w = s.Width, h = s.Height;
            if (p is 1 or 2)
            {
                w = (w + (1 << hShift) - 1) >> hShift;
                h = (h + (1 << vShift) - 1) >> vShift;
            }

            byte[] pic = s.Picture!;
            int linesize = s.Linesize[p];
            int dst = s.PlaneOffset[p] + (h - 1) * linesize;
            for (int i = h - 1; i != 0; i--)
            {
                int src1 = s.PlaneOffset[p] + i * s.UpscaleV[p] / (s.UpscaleV[p] + 1) * linesize;
                int src2 = s.PlaneOffset[p] + (i + 1) * s.UpscaleV[p] / (s.UpscaleV[p] + 1) * linesize;

                if (src1 == src2 || i == h - 1)
                {
                    pic.AsSpan(src1, w).CopyTo(pic.AsSpan(dst));
                }
                else
                {
                    for (int index = 0; index < w; index++)
                        pic[dst + index] = (byte)((pic[src1 + index] + pic[src2 + index]) >> 1);
                }
                dst -= linesize;
            }
        }
    }


    /// <summary><c>parse_avid</c> - the field polarity an AVID comment states.</summary>
    /// <remarks>
    /// Byte 12 of the comment: 1 is NTSC and bottom-field-first, 2 is PAL and
    /// top-field-first. Nothing else in such a file says which field comes first.
    /// </remarks>
    public static void ParseAvid(MJpegDecodeContext s, ReadOnlySpan<byte> buf, int len)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (len > 12 && buf[12] == 1)   // 1 - NTSC
            s.InterlacePolarity = 1;
        if (len > 12 && buf[12] == 2)   // 2 - PAL
            s.InterlacePolarity = 0;
    }

    /// <summary>
    /// <c>mjpeg_decode_com</c> - the COM segment, four of whose contents change a decode.
    /// </summary>
    /// <remarks>
    /// A comment is normally text nobody reads. Four specific strings are not: an
    /// <c>AVID</c> comment carries the field polarity, <c>CS=ITU601</c> says the samples are
    /// limited range, two encoders announce a bottom-up picture by naming themselves, and
    /// <c>MULTISCOPE II</c> sets the aspect ratio and suppresses the interlace test.
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeCom(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        int ret = MjpegParseLen(s, out int len, "com");
        if (ret < 0)
            return ret;
        if (len == 0)
            return 0;

        byte[] cbuf = new byte[len + 1];
        for (int i = 0; i < len; i++)
            cbuf[i] = s.GB.GetByteU();

        // The C trims a trailing newline, terminates after the last byte, and then compares
        // with strcmp and strncmp - so the string ends at the first NUL, which is NOT the
        // same as the segment length. A COM segment that stores its text with a terminator
        // is one byte longer than its text, and comparing against the segment length makes
        // every strcmp arm miss. That is exactly what happened: CS=ITU601 matched nothing.
        if (cbuf[len - 1] == (byte)'\n')
            cbuf[len - 1] = 0;

        int textLength = 0;
        while (textLength < len && cbuf[textLength] != 0)
            textLength++;

        if (TextStartsWith(cbuf, textLength, "AVID"))
        {
            // The C passes the SEGMENT length here, not the trimmed one, and ParseAvid
            // indexes byte 12 against it.
            ParseAvid(s, cbuf, len);
        }
        else if (TextEquals(cbuf, textLength, "CS=ITU601"))
        {
            s.CsItu601 = 1;
        }
        else if ((TextStartsWith(cbuf, textLength, "Intel(R) JPEG Library, version 1")
                  && s.CodecTag != 0)
                 || TextStartsWith(cbuf, textLength, "Metasoft MJPEG Codec"))
        {
            // Both encoders store the picture bottom-up. The Intel one is trusted only in a
            // container - the C's `&& s->avctx->codec_tag` - because the same library wrote
            // top-down still JPEGs.
            s.Flipped = 1;
        }
        else if (TextEquals(cbuf, textLength, "MULTISCOPE II"))
        {
            s.SampleAspectRatioNum = 1;
            s.SampleAspectRatioDen = 2;
            s.Multiscope = 2;
        }

        return 0;
    }

    private static bool TextStartsWith(ReadOnlySpan<byte> buf, int length, string text)
    {
        if (length < text.Length)
            return false;
        for (int i = 0; i < text.Length; i++)
            if (buf[i] != (byte)text[i])
                return false;
        return true;
    }

    private static bool TextEquals(ReadOnlySpan<byte> buf, int length, string text) =>
        length == text.Length && TextStartsWith(buf, length, text);

    /// <summary>
    /// <c>mjpeg_decode_app</c> - an APPn segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most APP segments carry metadata and are skipped. The ones that change a decode are
    /// translated: <c>Adobe</c> (APP14) sets the colour transform, which is what tells a
    /// three-component JPEG with ordinary component ids that it is RGB and not YCbCr;
    /// <c>LJIF</c> sets Pegasus lossless JPEG colour flags; and <c>colr</c>/<c>xfrm</c>
    /// carry a colour space and a transform.
    /// </para>
    /// <para>
    /// SCOPED OUT, and recorded in the checklist rather than silently dropped: the EXIF
    /// parse (APP1), the JPS stereo-3D side data (APP3), the JFIF aspect ratio and thumbnail
    /// skip, and the Apple MJPEG-A offsets, which the C only logs. None of them changes a
    /// sample, and each needs side-data plumbing this port does not have. The segment is
    /// still consumed by its DECLARED length, so leaving their contents unread cannot
    /// desynchronise the marker loop.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeApp(MJpegDecodeContext s, int startCode)
    {
        ArgumentNullException.ThrowIfNull(s);

        int ret = MjpegParseLen(s, out int len, "app");
        if (ret < 0)
            return AverrorInvalidData;

        // Where the segment ends, so every arm can leave from the same place however many
        // bytes it read. The C reaches `out:` by goto and skips the remainder there.
        int segmentEnd = s.GB.Tell + len;

        if (len < 4)
            return EndOfAppSegment(s, segmentEnd);      // "skipping APPx stub"

        uint id = s.GB.GetBe32U();
        len -= 4;

        if (id == Fourcc("AVI1"))
        {
            // AVI1, a polarity byte, a zero, then two field sizes. The C LOGS the polarity
            // and does not store it - the AVID COMMENT is what sets polarity - so this arm
            // reads nothing and exists to be distinguished from the default.
            return EndOfAppSegment(s, segmentEnd);
        }

        if (id == Fourcc("JFIF"))
        {
            // The aspect ratio and the thumbnail. Neither changes a sample.
            return EndOfAppSegment(s, segmentEnd);
        }

        if (id == Fourcc("Adob") && len >= 8
            && s.GB.PeekByteU() == (byte)'e'
            && s.GB.PeekBe32U() != Fourcc("e_CM"))
        {
            // The e_CM test keeps an "Adobe_CM" segment - a different, longer APP14 that
            // Photoshop also writes - out of this arm.
            s.GB.SkipU(1);                  // 'e'
            s.GB.SkipU(2);                  // version
            s.GB.SkipU(2);                  // flags0
            s.GB.SkipU(2);                  // flags1
            s.AdobeTransform = s.GB.GetByteU();
            return EndOfAppSegment(s, segmentEnd);
        }

        if (id == Fourcc("LJIF"))
        {
            int rgb = s.Rgb;
            int pegasusRct = s.PegasusRct;

            if (len < 9)
                return EndOfAppSegment(s, segmentEnd);

            s.GB.SkipU(8);                  // a version and three unknowns, two bytes each
            switch (s.GB.GetByteU())
            {
                case 1: rgb = 1; pegasusRct = 0; break;
                case 2: rgb = 1; pegasusRct = 1; break;
                default: break;             // "unknown colorspace", and nothing changes
            }

            if (s.Bayer != 0)
                return EndOfAppSegment(s, segmentEnd);
            if (s.GotPicture != 0 && (rgb != s.Rgb || pegasusRct != s.PegasusRct))
                return EndOfAppSegment(s, segmentEnd);  // "Mismatching LJIF tag"

            s.Rgb = rgb;
            s.PegasusRct = pegasusRct;
            return EndOfAppSegment(s, segmentEnd);
        }

        // These two are AV_RL32 in the C, compared against a big-endian read, so the bytes
        // in the file are 'r','l','o','c' and 'm','r','f','x'. Reading them big-endian
        // instead matches nothing and the arms become dead.
        if (id == FourccLe("colr") && len > 0)
        {
            s.Colr = s.GB.GetByteU();
            return EndOfAppSegment(s, segmentEnd);
        }

        if (id == FourccLe("xfrm") && len > 0)
        {
            s.Xfrm = s.GB.GetByteU();
            return EndOfAppSegment(s, segmentEnd);
        }

        return EndOfAppSegment(s, segmentEnd);
    }

    /// <summary><c>out:</c> - leave the segment at its declared end, whatever was read.</summary>
    private static int EndOfAppSegment(MJpegDecodeContext s, int segmentEnd)
    {
        s.GB.Position = segmentEnd;
        return 0;
    }

    /// <summary><c>AV_RB32("abcd")</c> as a big-endian read sees it.</summary>
    private static uint Fourcc(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    /// <summary><c>AV_RL32("abcd")</c> compared against a big-endian read.</summary>
    private static uint FourccLe(string tag) =>
        ((uint)tag[3] << 24) | ((uint)tag[2] << 16) | ((uint)tag[1] << 8) | tag[0];


    /// <summary>
    /// The <c>s-&gt;flipped</c> arm of <c>ff_mjpeg_decode_frame_from_buf</c> - a picture an
    /// encoder stored bottom-up, turned the right way up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIVERGENCE, and a deliberate one. The C does not move any pixels: it walks each
    /// plane pointer to its LAST line and negates the line size, so the frame it hands out
    /// is read upwards. This port's picture is one byte array with a plane offset and a
    /// positive stride apiece, and every reader of it - the scan, the upscalers, the
    /// adapter's extractor - assumes forward lines. Threading a negative stride through all
    /// of them to save one copy of a picture that two obsolete encoders produce is the wrong
    /// trade, so the rows are swapped instead. The result is identical; only the cost is
    /// different, and it is paid once per frame on streams nothing else in this tree uses.
    /// </para>
    /// <para>
    /// <c>&amp;&amp; !s-&gt;rgb</c> is part of the test, not an optimisation: a Pegasus RGB
    /// lossless JPEG sets <c>flipped</c> through the same comment and must not be flipped.
    /// </para>
    /// </remarks>
    private static void FlipPlanes(MJpegDecodeContext s)
    {
        if (s.Flipped == 0 || s.Rgb != 0)
            return;

        (int hShift, int vShift) = ChromaShift(s.PixelFormat);

        for (int plane = 0; plane < s.NbComponents; plane++)
        {
            int stride = s.Linesize[plane];
            if (stride == 0)
                continue;

            int height = s.FrameHeight;
            if (plane > 0 && plane < 3)
                height = (height + (1 << vShift) - 1) >> vShift;

            int width = plane > 0 && plane < 3
                ? (s.Width + (1 << hShift) - 1) >> hShift
                : s.Width;

            int at = s.PlaneOffset[plane];
            for (int y = 0; y < height / 2; y++)
            {
                Span<byte> top = s.Picture.AsSpan(at + y * stride, width);
                Span<byte> bottom = s.Picture.AsSpan(at + (height - 1 - y) * stride, width);
                for (int x = 0; x < width; x++)
                    (top[x], bottom[x]) = (bottom[x], top[x]);
            }
        }
    }

    /// <summary>
    /// <c>shift_output</c> - moves a block's samples up into the top of their lane.
    /// </summary>
    /// <remarks>
    /// A JPEG of a precision that is not a whole number of bytes - 12-bit is the one in the
    /// FATE set - decodes to 12-bit values that have to be presented as 16-bit ones, and
    /// FFmpeg does that by shifting left rather than by scaling. So 0xFFF becomes 0xFFF0 and
    /// not 0xFFFF, which is what <c>gray16le</c> output of a 12-bit JPEG looks like.
    /// </remarks>
    private static void ShiftOutput(MJpegDecodeContext s, int at, int linesize)
    {
        int shift = s.Bits > 8 ? 16 - s.Bits : 8 - s.Bits;

        for (int blockY = 0; blockY < 8; blockY++)
        {
            int row = at + blockY * linesize;
            for (int blockX = 0; blockX < 8; blockX++)
            {
                if (s.Bits > 8)
                {
                    int p = row + 2 * blockX;
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(s.Picture.AsSpan(p));
                    BinaryPrimitives.WriteUInt16LittleEndian(s.Picture.AsSpan(p),
                                                             (ushort)(v << shift));
                }
                else
                {
                    s.Picture![row + blockX] <<= shift;
                }
            }
        }
    }

    /// <summary>
    /// <c>mjpeg_decode_dc</c> - one DC difference: a Huffman-coded magnitude, then that many
    /// raw bits.
    /// </summary>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegDecodeDc(MJpegDecodeContext s, int dcIndex, out int val)
    {
        int code = s.Gb.GetVlc2(s.Vlcs[0 * 4 + dcIndex], 9, 2);
        if (code < 0 || code > 16)
        {
            val = 0;
            return AverrorInvalidData;  // "mjpeg_decode_dc: bad vlc"
        }

        val = code != 0 ? s.Gb.GetXBits(code) : 0;
        return 0;
    }

    /// <summary>
    /// <c>decode_block</c> - one 8x8 block of a baseline scan, dequantised as it is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DC is a difference from the previous block of the same component, and it is the
    /// DEQUANTISED value that is carried forward - which is why <c>last_dc</c> is multiplied
    /// by the quantiser here and not at the end.
    /// </para>
    /// <para>
    /// Each AC symbol packs a zero run in its top four bits and a magnitude in its bottom
    /// four. A magnitude of zero with a run of 15 is the sixteen-zero skip, and with a run of
    /// 0 it is end-of-block - which this loop reaches as "i stays below 63 and code is 0",
    /// the same way the C does, because <c>ff_mjpeg_build_vlc</c> gave EOB the symbol 16*256
    /// and <c>code &amp; 0xf</c> is then 0 while <c>code &gt;&gt; 4</c> is 256, taking i past
    /// 63 and ending the loop.
    /// </para>
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int DecodeBlock(MJpegDecodeContext s, Span<short> block, int component,
                                  int dcIndex, int acIndex, ReadOnlySpan<ushort> quantMatrix)
    {
        // DC coef
        int ret = MjpegDecodeDc(s, dcIndex, out int val);
        if (ret < 0)
            return ret;

        val = val * quantMatrix[0] + s.LastDc[component];
        s.LastDc[component] = val;
        block[0] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);

        // AC coefs
        VLCElem[] ac = s.Vlcs[1 * 4 + acIndex]!;
        int i = 0;
        do
        {
            int code = s.Gb.GetVlc2(ac, 9, 2);
            if (code < 0)
                return AverrorInvalidData;

            i += (int)((uint)code >> 4);
            code &= 0xF;
            if (code != 0)
            {
                int level = s.Gb.GetXBits(code);

                if (i > 63)
                    return AverrorInvalidData;      // "error count: %d"

                int j = s.PermutatedScantable[i];
                block[j] = (short)(level * quantMatrix[i]);
            }
        }
        while (i < 63);

        return 0;
    }

    /// <summary>
    /// <c>mjpeg_decode_scan</c> - the macroblock loop of a baseline scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One MCU holds <c>h_scount * v_scount</c> blocks of each scan component, and the blocks
    /// are laid out left to right then top to bottom within the MCU - which is what the
    /// <c>x</c>, <c>y</c> walk at the bottom of the inner loop does.
    /// </para>
    /// <para>
    /// A block whose top-left corner is outside the picture is still DECODED and then thrown
    /// away: the bitstream has it either way, and skipping the read would desynchronise
    /// everything after. That is what the null <c>ptr</c> is for.
    /// </para>
    /// <para>
    /// SCOPED: the MxPEG block-copy bitmask, the THP restart handling and the interlaced
    /// second-field pass are not translated. The progressive DC arm is, because a progressive
    /// JPEG's first scan comes through here with <c>Ss == 0</c>.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int MjpegDecodeScan(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        int nbComponents = s.NbComponentsSos;
        int ah = s.Ah, al = s.Al;
        int bytesPerPixel = s.Bits > 8 ? 2 : 1;

        for (int i = 0; i < nbComponents; i++)
            s.CoefsFinished[s.CompIndex[i]] |= 1;

        s.RestartCount = -1;

        Span<short> block = s.Block;

        for (int mbY = 0; mbY < s.MbHeight; mbY++)
        {
            for (int mbX = 0; mbX < s.MbWidth; mbX++)
            {
                bool restart;
                if (s.AlignedRestarts)
                {
                    // THP. The data is already unescaped, so it is handed over once - at
                    // the first MCU, where restart_count is still -1 - and a restart after
                    // that only needs the reader put back on a byte boundary.
                    if (s.RestartCount < 0)
                    {
                        int size = MjpegUnescapeSos(s);
                        if (size < 0)
                            return size;
                        s.Gb.InitGetBits8(s.Buffer!, size);
                    }

                    restart = ShouldRestart(s);
                    if (restart)
                        s.Gb.AlignGetBits();
                }
                else
                {
                    int r = HandleRestart(s, out restart);
                    if (r < 0)
                        return r;
                }

                if (restart)
                {
                    for (int i = 0; i < nbComponents; i++)
                        s.LastDc[i] = 4 << s.Bits;
                }

                if (s.Gb.GetBitsLeft() < 0)
                    return AverrorInvalidData;      // "overread"

                for (int i = 0; i < nbComponents; i++)
                {
                    int n = s.NbBlocks[i];
                    int c = s.CompIndex[i];
                    int h = s.HScount[i];
                    int v = s.VScount[i];
                    int x = 0, y = 0;

                    for (int j = 0; j < n; j++)
                    {
                        int linesize = s.Linesize[c];
                        int blockOffset = linesize * (v * mbY + y) * 8
                                        + (h * mbX + x) * 8 * bytesPerPixel;

                        // linesize is already the doubled one, so half of it is the frame's
                        // own stride: one line down, which is where the second field sits.
                        if (s.Interlaced != 0 && s.BottomField != 0)
                            blockOffset += linesize >> 1;

                        bool inside =
                            8 * (h * mbX + x) < (c is 1 or 2 ? s.ChromaWidth : s.Width) &&
                            8 * (v * mbY + y) < (c is 1 or 2 ? s.ChromaHeight : s.Height);

                        if (s.Progressive == 0)
                        {
                            block.Clear();
                            if (DecodeBlock(s, block, i, s.DcIndex[i], s.AcIndex[i],
                                    s.QuantMatrixes.AsSpan(s.QuantSindex[i] * 64, 64)) < 0)
                            {
                                return AverrorInvalidData;      // "error y=%d x=%d"
                            }

                            if (inside && linesize != 0)
                            {
                                int at = s.PlaneOffset[c] + blockOffset;
                                if (bytesPerPixel == 1)
                                    IdctDsp.SimpleIdctPut(s.Picture!, at, linesize, block);
                                else
                                    SimpleIdct12.Put(s.Picture!, at, linesize, block);

                                if ((s.Bits & 7) != 0)
                                    ShiftOutput(s, at, linesize);
                            }
                        }
                        else
                        {
                            // A progressive stream's DC scan comes through here, because it
                            // is the interleaved one; its AC scans go to
                            // MjpegDecodeScanProgressiveAc instead.
                            int blockIdx = s.BlockStride[c] * (v * mbY + y) + (h * mbX + x);
                            Span<short> dcBlock = s.Blocks[c].AsSpan(blockIdx * 64, 64);
                            ReadOnlySpan<ushort> q =
                                s.QuantMatrixes.AsSpan(s.QuantSindex[i] * 64, 64);

                            if (ah != 0)
                            {
                                dcBlock[0] = (short)(dcBlock[0] +
                                                     ((int)s.Gb.GetBits(1) * q[0] << al));
                            }
                            else if (DecodeDcProgressive(s, dcBlock, i, s.DcIndex[i],
                                                         q, al) < 0)
                            {
                                return AverrorInvalidData;      // "error y=%d x=%d"
                            }
                        }

                        if (++x == h)
                        {
                            x = 0;
                            y++;
                        }
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>ff_mjpeg_handle_restart</c> (<c>mjpegdec.h</c>) - unescapes the next run of
    /// entropy-coded data when a restart marker is due.
    /// </summary>
    /// <returns>0, or a negative error.</returns>
    public static int HandleRestart(MJpegDecodeContext s, out bool restart)
    {
        ArgumentNullException.ThrowIfNull(s);

        restart = ShouldRestart(s);
        if (restart)
        {
            int size = MjpegUnescapeSos(s);
            if (size < 0)
                return size;
            s.Gb.InitGetBits8(s.Buffer!, size);
        }
        return 0;
    }


    /// <summary>
    /// <c>decode_dc_progressive</c> - the DC coefficient of one block in a progressive
    /// stream's first scan.
    /// </summary>
    /// <remarks>
    /// The same DC difference as a baseline block, but shifted left by <c>Al</c>: a
    /// progressive stream may send the DC's high bits in one scan and refine them later, and
    /// <c>Al</c> says how many low bits are still missing.
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int DecodeDcProgressive(MJpegDecodeContext s, Span<short> block,
                                          int component, int dcIndex,
                                          ReadOnlySpan<ushort> quantMatrix, int al)
    {
        block.Clear();
        int ret = MjpegDecodeDc(s, dcIndex, out int val);
        if (ret < 0)
            return ret;

        val = val * (quantMatrix[0] << al) + s.LastDc[component];
        s.LastDc[component] = val;
        block[0] = (short)val;
        return 0;
    }

    /// <summary>
    /// <c>decode_block_progressive</c> - the AC coefficients of one block, first pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A progressive AC scan codes a band of coefficients, <c>Ss</c> to <c>Se</c>, at a
    /// precision of <c>Al</c> bits less than full. It also codes runs of entirely empty
    /// blocks: the symbol whose magnitude nibble is zero and whose run nibble is not 15 means
    /// "this block and the next <c>2^run + extra - 1</c> blocks have nothing in this band",
    /// which is what <c>EOBRUN</c> carries between calls.
    /// </para>
    /// <para>
    /// The AC book it reads is <c>vlcs[2]</c>, the copy <c>decode_dht</c> builds WITHOUT the
    /// AC symbol offset - so the symbol here is the raw run and size nibbles, not the
    /// offset-by-16 form a baseline scan reads.
    /// </para>
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int DecodeBlockProgressive(MJpegDecodeContext s, Span<short> block,
                                             ref byte lastNnz, int acIndex,
                                             ReadOnlySpan<ushort> quantMatrix,
                                             int ss, int se, int al, ref int eobRun)
    {
        if (eobRun != 0)
        {
            eobRun--;
            return 0;
        }

        VLCElem[] ac = s.Vlcs[2 * 4 + acIndex]!;
        int i = ss;
        for (; ; i++)
        {
            int code = s.Gb.GetVlc2(ac, 9, 2);
            if (code < 0)
                return AverrorInvalidData;

            int run = (int)((uint)code >> 4);
            code &= 0xF;
            if (code != 0)
            {
                i += run;

                int level = s.Gb.GetXBits(code);

                if (i >= se)
                {
                    if (i == se)
                    {
                        block[s.PermutatedScantable[se]] =
                            (short)(level * (quantMatrix[se] << al));
                        break;
                    }
                    return AverrorInvalidData;
                }
                block[s.PermutatedScantable[i]] = (short)(level * (quantMatrix[i] << al));
            }
            else
            {
                if (run == 0xF)
                {
                    // ZRL - skip 15 coefficients
                    i += 15;
                    if (i >= se)
                        return AverrorInvalidData;
                }
                else
                {
                    int val = 1 << run;
                    if (run != 0)
                        val += (int)s.Gb.GetBits(run);
                    eobRun = val - 1;
                    break;
                }
            }
        }

        if (i > lastNnz)
            lastNnz = (byte)i;

        return 0;
    }

    /// <summary>
    /// <c>REFINE_BIT</c> - one correction bit for a coefficient that is already non-zero.
    /// </summary>
    /// <remarks>
    /// The bit is added with the coefficient's own sign, which is what the
    /// <c>(q ^ sign) - sign</c> does: for a negative coefficient the magnitude grows
    /// downwards. <c>sign</c> is the arithmetic shift of a 16-bit value by 15, so it is 0 or
    /// -1 - and it is taken from the SHORT, which is why the block is read before the add.
    /// </remarks>
    private static void RefineBit(MJpegDecodeContext s, Span<short> block, int j,
                                  ReadOnlySpan<ushort> quantMatrix, int i, int al)
    {
        int sign = block[j] >> 15;
        block[j] = (short)(block[j] +
                           (((int)s.Gb.GetBits(1) * ((quantMatrix[i] ^ sign) - sign)) << al));
    }

    /// <summary>
    /// <c>decode_block_refinement</c> - the AC coefficients of one block, refinement pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refinement scan sends one bit per coefficient it has already placed, plus new
    /// coefficients at this precision. The zero-run walk is the C's <c>ZERO_RUN</c> macro,
    /// inlined here as the C inlines it: it steps over coefficients, refining the ones that
    /// are already non-zero - those cost a bit and do not count against the run - and
    /// stopping at the <c>run</c>-th zero.
    /// </para>
    /// <para>
    /// A new coefficient's magnitude is always 1 at this precision, so only its sign is
    /// coded: <c>val</c> is that bit, and <c>(q ^ val) - val</c> turns it into plus or minus
    /// the quantiser.
    /// </para>
    /// </remarks>
    /// <returns>0, or -1.</returns>
    public static int DecodeBlockRefinement(MJpegDecodeContext s, Span<short> block,
                                            ref byte lastNnz, int acIndex,
                                            ReadOnlySpan<ushort> quantMatrix,
                                            int ss, int se, int al, ref int eobRun)
    {
        int i = ss;
        int last = Math.Min(se, lastNnz);

        if (eobRun != 0)
        {
            eobRun--;
        }
        else
        {
            VLCElem[] ac = s.Vlcs[2 * 4 + acIndex]!;
            for (; ; i++)
            {
                int code = s.Gb.GetVlc2(ac, 9, 2);
                if (code < 0)
                    return -1;

                int run;
                if ((code & 0xF) != 0)
                {
                    run = (int)((uint)code >> 4);
                    int val = (int)s.Gb.GetBits(1);

                    // ZERO_RUN
                    for (; ; i++)
                    {
                        if (i > last)
                        {
                            i += run;
                            if (i > se)
                                return -1;
                            break;
                        }
                        int jz = s.PermutatedScantable[i];
                        if (block[jz] != 0)
                            RefineBit(s, block, jz, quantMatrix, i, al);
                        else if (run-- == 0)
                            break;
                    }

                    int j = s.PermutatedScantable[i];
                    val--;
                    block[j] = (short)(((quantMatrix[i] << al) ^ val) - val);
                    if (i == se)
                    {
                        if (i > lastNnz)
                            lastNnz = (byte)i;
                        return 0;
                    }
                }
                else
                {
                    run = (int)((uint)code >> 4);
                    if (run == 0xF)
                    {
                        // ZERO_RUN
                        for (; ; i++)
                        {
                            if (i > last)
                            {
                                i += run;
                                if (i > se)
                                    return -1;
                                break;
                            }
                            int jz = s.PermutatedScantable[i];
                            if (block[jz] != 0)
                                RefineBit(s, block, jz, quantMatrix, i, al);
                            else if (run-- == 0)
                                break;
                        }
                    }
                    else
                    {
                        int val = run;
                        run = 1 << run;
                        if (val != 0)
                            run += (int)s.Gb.GetBits(val);
                        eobRun = run - 1;
                        break;
                    }
                }
            }

            if (i > lastNnz)
                lastNnz = (byte)i;
        }

        for (; i <= last; i++)
        {
            int j = s.PermutatedScantable[i];
            if (block[j] != 0)
                RefineBit(s, block, j, quantMatrix, i, al);
        }

        return 0;
    }

    /// <summary>
    /// <c>mjpeg_decode_scan_progressive_ac</c> - one AC scan of a progressive stream.
    /// </summary>
    /// <remarks>
    /// A progressive AC scan is always single-component, so there is no MCU interleave here:
    /// the loop walks the component's own block grid. The coefficients go into
    /// <c>blocks[c]</c> and stay there until every scan has been read, which is why nothing
    /// is transformed in this function.
    /// </remarks>
    /// <returns>0, or <see cref="AverrorInvalidData"/>.</returns>
    public static int MjpegDecodeScanProgressiveAc(MJpegDecodeContext s)
    {
        int ss = s.Ss, se = s.Se, ah = s.Ah, al = s.Al;
        int eobRun = 0;
        int c = s.CompIndex[0];

        if (se < ss || se > 63)
            return AverrorInvalidData;

        // s->coefs_finished is a bitmask for coefficients coded; Ss and Se are the band.
        s.CoefsFinished[c] |= (2UL << se) - (1UL << ss);

        short[] blocks = s.Blocks[c]!;
        byte[] lastNnz = s.LastNnz[c]!;
        ReadOnlySpan<ushort> quantMatrix =
            s.QuantMatrixes.AsSpan(s.QuantSindex[0] * 64, 64);

        s.RestartCount = -1;

        for (int mbY = 0; mbY < s.MbHeight; mbY++)
        {
            int blockIdx = mbY * s.BlockStride[c];
            for (int mbX = 0; mbX < s.MbWidth; mbX++, blockIdx++)
            {
                int ret = HandleRestart(s, out bool restart);
                if (ret < 0)
                    return ret;
                if (restart)
                    eobRun = 0;

                Span<short> block = blocks.AsSpan(blockIdx * 64, 64);

                ret = ah != 0
                    ? DecodeBlockRefinement(s, block, ref lastNnz[blockIdx], s.AcIndex[0],
                                            quantMatrix, ss, se, al, ref eobRun)
                    : DecodeBlockProgressive(s, block, ref lastNnz[blockIdx], s.AcIndex[0],
                                             quantMatrix, ss, se, al, ref eobRun);

                if (ret >= 0 && s.Gb.GetBitsLeft() < 0)
                    ret = AverrorInvalidData;
                if (ret < 0)
                    return AverrorInvalidData;      // "error y=%d x=%d"
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>mjpeg_idct_scan_progressive_ac</c> - transforms every block once all the scans have
    /// been read.
    /// </summary>
    /// <remarks>
    /// Each component has its own block grid, because a progressive stream's non-interleaved
    /// scans code one component at a time: <c>h</c> and <c>v</c> here are the component's
    /// subsampling relative to the largest, not its sampling factors.
    /// </remarks>
    public static void MjpegIdctScanProgressiveAc(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        for (int c = 0; c < s.NbComponents; c++)
        {
            int linesize = s.Linesize[c];
            int h = s.HMax / s.HCount[c];
            int v = s.VMax / s.VCount[c];
            int mbWidth = (s.Width + h * 8 - 1) / (h * 8);
            int mbHeight = (s.Height + v * 8 - 1) / (v * 8);

            short[] blocks = s.Blocks[c]!;

            for (int mbY = 0; mbY < mbHeight; mbY++)
            {
                int ptr = s.PlaneOffset[c] + mbY * linesize * 8;
                int blockIdx = mbY * s.BlockStride[c];
                for (int mbX = 0; mbX < mbWidth; mbX++, blockIdx++)
                {
                    IdctDsp.SimpleIdctPut(s.Picture!, ptr, linesize,
                                          blocks.AsSpan(blockIdx * 64, 64));
                    ptr += 8;
                }
            }
        }
    }


    /// <summary>
    /// <c>init_default_huffman_tables</c> - the Annex K.3 books, for a stream that refers to
    /// them instead of carrying a DHT of its own.
    /// </summary>
    /// <remarks>
    /// Six builds, not four: the two AC books are built again into <c>vlcs[2]</c> without the
    /// AC symbol offset, which is the copy a progressive scan's DC pass reads.
    /// </remarks>
    public static void InitDefaultHuffmanTables(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        s.Vlcs[0 * 4 + 0] = MjpegBuildVlc(JpegTables.BitsDcLuminance, JpegTables.ValDc, 0);
        s.Vlcs[0 * 4 + 1] = MjpegBuildVlc(JpegTables.BitsDcChrominance, JpegTables.ValDc, 0);
        s.Vlcs[1 * 4 + 0] = MjpegBuildVlc(JpegTables.BitsAcLuminance,
                                          JpegTables.ValAcLuminance, 1);
        s.Vlcs[1 * 4 + 1] = MjpegBuildVlc(JpegTables.BitsAcChrominance,
                                          JpegTables.ValAcChrominance, 1);
        s.Vlcs[2 * 4 + 0] = MjpegBuildVlc(JpegTables.BitsAcLuminance,
                                          JpegTables.ValAcLuminance, 0);
        s.Vlcs[2 * 4 + 1] = MjpegBuildVlc(JpegTables.BitsAcChrominance,
                                          JpegTables.ValAcChrominance, 0);
    }

    /// <summary>
    /// <c>init_idct</c> - the inverse transform and the scan order permuted for it.
    /// </summary>
    /// <remarks>
    /// The simple IDCT, which is what FFmpeg's own FATE rules select with <c>-idct simple</c>
    /// for every jpg reference, and whose permutation is the identity. A JPEG's coefficients
    /// are in zigzag order, so the scantable is the zigzag and not a codec-specific one.
    /// </remarks>
    public static void InitIdct(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        IdctDsp.PermuteScantable(s.PermutatedScantable, IdctDsp.ZigzagDirect,
                                 IdctDsp.IdentityPermutation);
    }

    /// <summary>
    /// <c>ff_mjpeg_decode_init</c> - a decoder before it has seen any of the stream.
    /// </summary>
    /// <remarks>
    /// SCOPED: the <c>extern_huff</c> arm, which reads a DHT out of extradata for AVI's
    /// "MJPG with tables in the header" variant, is not translated yet and is recorded. The
    /// SMV, ICC, EXIF and hwaccel setup is out of scope.
    /// </remarks>
    /// <param name="codedHeight">
    /// The height the container declared, which is the only way to notice that a Motion JPEG
    /// frame is one field of an interlaced picture - the bitstream does not say.
    /// </param>
    /// <param name="fieldOrder">An <see cref="MjpegFieldOrder"/> value from the container.</param>
    /// <param name="codecTag">The container's FourCC, little-endian as the C reads it.</param>
    /// <param name="externHuff">
    /// <c>avctx-&gt;extradata</c> holding a DHT segment, for the <c>extern_huff</c> option.
    /// </param>
    /// <remarks>
    /// <c>extern_huff</c> is an AVOption in the C, defaulting to off, and it is a decision
    /// the CALLER makes rather than something detected from the stream: an AVI MJPG track
    /// can carry its Huffman tables in the stream header and omit DHT from every frame, and
    /// nothing in such a file distinguishes it from a stream that simply means the default
    /// tables. So this takes the extradata and uses it when given, which is the same
    /// decision at the same place.
    /// </remarks>
    public static void MjpegDecodeInit(MJpegDecodeContext s, int codedHeight,
                                       int fieldOrder = MjpegFieldOrder.Unknown,
                                       uint codecTag = 0,
                                       ReadOnlySpan<byte> externHuff = default)
    {
        ArgumentNullException.ThrowIfNull(s);

        InitIdct(s);
        s.Buffer = null;
        s.FirstPicture = 1;
        s.GotPicture = 0;
        s.OrigHeight = codedHeight;
        s.CodecTag = codecTag;
        s.AdobeTransform = -1;

        InitDefaultHuffmanTables(s);

        if (!externHuff.IsEmpty)
        {
            // The C reads the extradata as a DHT segment BODY - the length field and the
            // tables, without the 0xFFC4 marker - and falls back to the defaults if it does
            // not parse. "Switching back to internal" is not a nicety: a stream with no DHT
            // and no usable extradata has to decode with something.
            // No save and restore of s.GB around this. The C does not do one either - it
            // points gB at the extradata and leaves it there, because the next thing to
            // touch gB is a frame decode, which re-inits it over the packet. An earlier
            // version here restored it, and a perturbation showed the restore was dead.
            byte[] extradata = externHuff.ToArray();
            s.GB.Init(extradata, 0, extradata.Length);
            int ret;
            try
            {
                ret = MjpegDecodeDht(s);
            }
            catch (IndexOutOfRangeException)
            {
                // A truncated segment runs off the end of a buffer the C would have sized
                // with bytestream2; the C's own answer to a bad table is the same fallback.
                ret = AverrorInvalidData;
            }

            if (ret < 0)
                InitDefaultHuffmanTables(s);
        }

        // Which field comes first, which NOTHING in the bitstream says. A QuickTime track
        // that declares bottom-field-first says so; and a track that declares nothing but is
        // tagged MJPG is bottom-field-first too, which is ffmpeg ticket 3229 - the AVI
        // written by the hardware that prompted it codes the bottom field first and labels
        // the file not at all.
        if (fieldOrder == MjpegFieldOrder.BottomBottom)
        {
            s.InterlacePolarity = 1;
        }
        else if (fieldOrder == MjpegFieldOrder.Unknown)
        {
            // AV_RL32("MJPG")
            if (codecTag == 0x47504A4D)
                s.InterlacePolarity = 1;
        }
    }

    /// <summary>
    /// <c>av_pix_fmt_get_chroma_sub_sample</c>, for the formats this decoder produces.
    /// </summary>
    /// <remarks>
    /// GBRP is three full-resolution planes, so its shifts are zero - the same as 4:4:4, and
    /// every place that asks for them treats the two alike.
    /// </remarks>
    private static (int HShift, int VShift) ChromaShift(PixelFormat format) => format switch
    {
        PixelFormat.Yuv420P => (1, 1),
        PixelFormat.Yuv422P => (1, 0),
        PixelFormat.Yuv444P or PixelFormat.Gbrp or PixelFormat.Gray8
            or PixelFormat.Gray16 => (0, 0),
        // Unknown input fails loudly: a wrong guess here sizes the planes wrongly and looks
        // like a decode bug rather than a format one.
        _ => throw new NotSupportedException($"JPEG: no chroma shift for {format}."),
    };

    /// <summary>
    /// The frame buffer <c>ff_get_buffer</c> would have taken, as one array with a plane
    /// offset each.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: FFmpeg allocates through AVFrame and a buffer pool, with padded
    /// line sizes it chooses. Here the planes are one rented buffer with no padding, which is
    /// the shape every other decoder in this project hands a frame over in, and the line size
    /// is the plane width. Nothing in JPEG reads past a line, so the padding FFmpeg's
    /// allocator adds changes no pixel.
    /// </remarks>
    internal static void AllocPicture(MJpegDecodeContext s)
    {
        (int hShift, int vShift) = ChromaShift(s.PixelFormat);

        // The planes are whole macroblocks wide and tall, because the scan writes an 8x8
        // block whose top-left corner is inside the picture even when its bottom or right
        // edge is not. FFmpeg gets the same slack from its allocator's alignment.
        int lumaWidth = (s.Width + s.HMax * 8 - 1) / (s.HMax * 8) * (s.HMax * 8);

        // Round the FIELD height up to whole macroblocks and then double it, rather than
        // rounding the frame height: with the stride doubled, the last macroblock row of a
        // field writes eight lines two frame-lines apart, so a 156-line field needs 160
        // field-lines of slack and therefore 320 frame-lines. Rounding 312 up to 312 leaves
        // the last row writing past the plane, which is what IdctDsp.SimpleIdctPut said.
        int fieldLumaHeight = (s.Height + s.VMax * 8 - 1) / (s.VMax * 8) * (s.VMax * 8);
        int lumaHeight = fieldLumaHeight << s.Interlaced;

        // The chroma size the SCAN tests against is the FIELD's, because that is what one
        // image in the stream covers; the plane is the frame's.
        s.ChromaWidth = (s.Width + (1 << hShift) - 1) >> hShift;
        s.ChromaHeight = (s.Height + (1 << vShift) - 1) >> vShift;

        int chromaWidth = lumaWidth >> hShift;
        int chromaHeight = lumaHeight >> vShift;

        // bytes_per_pixel in the C. A sample over eight bits occupies a 16-bit lane, so
        // every offset and stride below is in BYTES and not in samples.
        int bps = s.Bits > 8 ? 2 : 1;

        int lumaPlane = lumaWidth * bps * lumaHeight;
        int chromaPlane = s.NbComponents > 1 ? chromaWidth * bps * chromaHeight : 0;
        int total = lumaPlane + 2 * chromaPlane;

        if (s.Picture is null || s.Picture.Length < total)
            s.Picture = new byte[total];

        s.PlaneOffset[0] = 0;
        s.PlaneOffset[1] = lumaPlane;
        s.PlaneOffset[2] = lumaPlane + chromaPlane;
        s.PlaneOffset[3] = 0;

        s.Linesize[0] = lumaWidth * bps;
        s.Linesize[1] = chromaWidth * bps;
        s.Linesize[2] = chromaWidth * bps;
        s.Linesize[3] = 0;

        s.LumaWidth = lumaWidth;
        s.LumaHeight = lumaHeight;
    }

    /// <summary>
    /// Decodes one whole JPEG image, headers and all, into <see cref="MJpegDecodeContext.Picture"/>.
    /// </summary>
    /// <remarks>
    /// This is <c>ff_mjpeg_decode_frame_from_buf</c> with its frame management, which FFmpeg
    /// does through AVFrame and this project does through a plane buffer. The segment loop is
    /// <see cref="MjpegDecodeFrameFromBuf"/>; the buffer is taken once SOF has settled the
    /// size and format, which is where <c>ff_get_buffer</c> sits in the C.
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int DecodeImage(MJpegDecodeContext s, byte[] buf, int bufSize)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(buf);

        s.PictureAllocator = AllocPicture;
        return MjpegDecodeFrameFromBuf(s, buf, bufSize);
    }

    /// <summary>
    /// <c>ff_mjpeg_should_restart</c> (<c>mjpegdec.h</c>) - whether a restart marker is due
    /// before the next MCU.
    /// </summary>
    /// <remarks>
    /// The second arm, for a stream with no declared restart interval, is not dead: a decoder
    /// that meets a restart marker anyway sets <c>restart_count</c> negative, and this is what
    /// notices.
    /// </remarks>
    public static bool ShouldRestart(MJpegDecodeContext s)
    {
        ArgumentNullException.ThrowIfNull(s);

        bool restart = false;
        if (s.RestartInterval != 0)
        {
            if (s.RestartCount <= 0)
            {
                s.RestartCount = s.RestartInterval;
                restart = true;
            }
            s.RestartCount--;
        }
        else
        {
            if (s.RestartCount < 0)
            {
                s.RestartCount = 0;
                restart = true;
            }
        }
        return restart;
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// H.264 in-loop deblocking filter.
    /// Removes blocking artifacts at macroblock and 4x4 block boundaries.
    /// Based on H.264 section 8.7.
    /// </summary>
    public static class DeblockingFilter
    {
        /// <summary>Temporary diagnostic: set to specific mbAddr to trace bS computation.</summary>
        public static int DiagMbAddr = -1;
        /// <summary>Temporary diagnostic: set of additional mbAddrs to trace (neighbors).</summary>
        public static HashSet<int>? DiagMbAddrSet;
        /// <summary>Temporary diagnostic: only trace when this POC is being deblocked (-1 = disabled).</summary>
        public static int DiagPoc = -1;
        /// <summary>Temporary diagnostic: only trace at this decode count (-1 = disabled, 0-indexed).</summary>
        public static int DiagDecodeCount = -1;
        /// <summary>Temporary diagnostic: tracks how many times ApplyDeblocking has been called.</summary>
        public static int DeblockCallCount;
        /// <summary>Temporary diagnostic: current POC being deblocked (set by caller).</summary>
        public static int CurrentDeblockPoc = -1;
        /// <summary>Temporary diagnostic: output writer for bS traces.</summary>
        public static System.IO.TextWriter? DiagOutput;
        /// <summary>Temporary diagnostic: pre-deblocking pixel snapshot for specific MB.</summary>
        public static byte[]? DiagPreDeblockLuma;
        public static int DiagPreDeblockStride;
        public static int DiagPreDeblockMbX, DiagPreDeblockMbY;
        /// <summary>Temporary diagnostic: FFmpeg reference luma pixels for comparison. Set to enable per-edge comparison.</summary>
        public static byte[]? RefLuma;
        public static int RefLumaStride;
        public static bool RefFirstMismatchFound;
        /// <summary>Temporary diagnostic: when true, dump all H-edge0 bS values for the entire picture.</summary>
        public static bool DumpAllHEdge0Bs;
        /// <summary>Temporary diagnostic: trace all filter ops affecting this luma offset (-1=disabled).</summary>
        public static int TracePixelOffset = -1;
        /// <summary>Temporary diagnostic: set of (x,y) pixel coords to trace writes for 10-bit deblocking.</summary>
        public static HashSet<(int x, int y)>? TracePixelCoords;
        /// <summary>Luma stride for TracePixelCoords (needed to convert offset → x,y).</summary>
        public static int TraceStride;
        // The three fifty-two entry threshold tables of H.264 Tables 8-16 and 8-17 are not held
        // separately: the padded tables below carry the same values in their middle, and keeping a
        // second copy only invites the two to drift. H264DeblockingTableTests asserts the padded
        // tables against the spec values written out independently.

        /// <summary>
        /// How far the padded threshold tables are shifted, so that an index derived from a
        /// quantiser and a slice filter offset cannot leave the table.
        /// </summary>
        /// <remarks>
        /// FFmpeg's alpha, beta and tc0 tables are <c>52*3</c> entries rather than 52, with the
        /// first and last values repeated across the padding, and are indexed by <c>qp + a</c>
        /// where <c>a = 52 + slice_alpha_c0_offset - qp_bd_offset</c> (h264_loopfilter.c). A
        /// quantiser of 0..51 and an offset of -12..12 then land inside 40..115, and ten-bit
        /// video's wider quantiser range inside 28..115 - so the clamp that every lookup here
        /// used to carry is not needed at all. That clamp ran twice per edge and once per
        /// segment for tc0, which is sixteen plus thirty-two times per macroblock.
        ///
        /// The tc0 table is also transposed relative to ours. Theirs is indexed
        /// <c>[index_a][bS]</c>, so the four segments of one edge share an index and their four
        /// limits are four adjacent bytes; ours was <c>[bS][index_a]</c>, which put them
        /// fifty-two bytes apart in three different rows. Eight columns rather than their four,
        /// because our callers may pass a boundary strength of four and theirs cannot - column
        /// four onwards is a defined zero instead of the next row's data.
        /// </remarks>
        private const int QpIndexBias = 52;

        private static ReadOnlySpan<byte> AlphaTablePadded => new byte[]
        {
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    4,    4,    5,    6,    7,    8,    9,   10,   12,   13,
              15,   17,   20,   22,   25,   28,   32,   36,   40,   45,   50,   56,   63,
              71,   80,   90,  101,  113,  127,  144,  162,  182,  203,  226,  255,  255,
             255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,
             255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,
             255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,
             255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,  255,
        };

        private static ReadOnlySpan<byte> BetaTablePadded => new byte[]
        {
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    2,    2,    2,    3,    3,    3,    3,    4,    4,    4,
               6,    6,    7,    7,    8,    8,    9,    9,   10,   10,   11,   11,   12,
              12,   13,   13,   14,   14,   15,   15,   16,   16,   17,   17,   18,   18,
              18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,
              18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,
              18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,
              18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,   18,
        };

        private static ReadOnlySpan<byte> Tc0TablePadded => new byte[]
        {
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    0,    0,    0,    0,    0,
               0,    0,    0,    1,    0,    0,    0,    0,
               0,    0,    0,    1,    0,    0,    0,    0,
               0,    0,    0,    1,    0,    0,    0,    0,
               0,    0,    0,    1,    0,    0,    0,    0,
               0,    0,    1,    1,    0,    0,    0,    0,
               0,    0,    1,    1,    0,    0,    0,    0,
               0,    1,    1,    1,    0,    0,    0,    0,
               0,    1,    1,    1,    0,    0,    0,    0,
               0,    1,    1,    1,    0,    0,    0,    0,
               0,    1,    1,    1,    0,    0,    0,    0,
               0,    1,    1,    2,    0,    0,    0,    0,
               0,    1,    1,    2,    0,    0,    0,    0,
               0,    1,    1,    2,    0,    0,    0,    0,
               0,    1,    1,    2,    0,    0,    0,    0,
               0,    1,    2,    3,    0,    0,    0,    0,
               0,    1,    2,    3,    0,    0,    0,    0,
               0,    2,    2,    3,    0,    0,    0,    0,
               0,    2,    2,    4,    0,    0,    0,    0,
               0,    2,    3,    4,    0,    0,    0,    0,
               0,    2,    3,    4,    0,    0,    0,    0,
               0,    3,    3,    5,    0,    0,    0,    0,
               0,    3,    4,    6,    0,    0,    0,    0,
               0,    3,    4,    6,    0,    0,    0,    0,
               0,    4,    5,    7,    0,    0,    0,    0,
               0,    4,    5,    8,    0,    0,    0,    0,
               0,    4,    6,    9,    0,    0,    0,    0,
               0,    5,    7,   10,    0,    0,    0,    0,
               0,    6,    8,   11,    0,    0,    0,    0,
               0,    6,    8,   13,    0,    0,    0,    0,
               0,    7,   10,   14,    0,    0,    0,    0,
               0,    8,   11,   16,    0,    0,    0,    0,
               0,    9,   12,   18,    0,    0,    0,    0,
               0,   10,   13,   20,    0,    0,    0,    0,
               0,   11,   15,   23,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
               0,   13,   17,   25,    0,    0,    0,    0,
        };

        /// <summary>The clipping limit for one segment, from an already-biased index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Tc0Biased(int boundaryStrength, int biasedIndexA)
            => Tc0TablePadded[(biasedIndexA << 3) + boundaryStrength];


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadPixel(Span<byte> buffer, int pixelIndex, int pixelShift)
        {
            if (pixelShift == 0) return buffer[pixelIndex];
            return MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WritePixel(Span<byte> buffer, int pixelIndex, int value, int pixelShift)
        {
            if (pixelShift == 0) buffer[pixelIndex] = (byte)value;
            else MemoryMarshal.Cast<byte, ushort>(buffer)[pixelIndex] = (ushort)value;
        }

        // A method containing stackalloc emits localloc, and the runtime excludes any such method
        // from tiered compilation: it is compiled once with full optimisation and never reaches
        // Tier1, so it never gets Dynamic PGO or the hot/cold block ordering that comes with it.
        // FilterMacroblock is the largest single method in the profile at 13.7% of the decode
        // thread, and it was forfeiting all of that for three scratch buffers. An inline array is
        // a struct local, so it gives the same stack storage without the localloc.
        [System.Runtime.CompilerServices.InlineArray(4)]
        private struct SegmentStrengths { private int _element0; }

        /// <summary>
        /// Everything the loop filter needs that does not change within a picture: the three
        /// planes, the motion and reference arrays the boundary strengths are derived from, and
        /// the chroma quantiser offsets and sample format.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>ff_h264_filter_mb</c> takes nine arguments and reads everything else off <c>h</c>
        /// and <c>sl</c>; this method used to take forty. Twenty-three of those slots held the
        /// same values for every macroblock in the picture, and five of them were spans, which
        /// cost two slots each.
        /// </para>
        /// <para>
        /// That is not free the way an unused argument sounds like it should be. The caller
        /// stores every slot before the call whether the callee reads it or not, and an empty
        /// method with the identical forty-argument signature, called once more per macroblock,
        /// measured <b>1.0% of decode</b> on its own - eleven of twelve interleaved rounds
        /// negative. Passing one address instead is what that measurement was pricing.
        /// </para>
        /// </remarks>
        internal readonly ref struct DeblockPicture
        {
            public readonly Span<byte> Luma;
            public readonly Span<byte> ChromaCb;
            public readonly Span<byte> ChromaCr;
            public readonly int LumaStride;
            public readonly int ChromaStride;
            public readonly ReadOnlySpan<short> MvL0;
            public readonly ReadOnlySpan<short> MvL1;
            public readonly ReadOnlySpan<int> RefPicL0;
            public readonly ReadOnlySpan<int> RefPicL1;
            public readonly ReadOnlySpan<bool> MotionUniform;
            public readonly int ChromaQpIndexOffsetCb;
            public readonly int ChromaQpIndexOffsetCr;
            public readonly bool IsFieldPicture;
            public readonly bool IsMbaffFrame;
            public readonly int PixelShift;
            public readonly int BitDepth;
            public readonly bool IsChroma422;

            public DeblockPicture(
                Span<byte> luma, Span<byte> chromaCb, Span<byte> chromaCr,
                int lumaStride, int chromaStride,
                ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
                ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1,
                ReadOnlySpan<bool> motionUniform,
                int chromaQpIndexOffsetCb, int chromaQpIndexOffsetCr,
                bool isFieldPicture, bool isMbaffFrame,
                int pixelShift, int bitDepth, bool isChroma422)
            {
                Luma = luma;
                ChromaCb = chromaCb;
                ChromaCr = chromaCr;
                LumaStride = lumaStride;
                ChromaStride = chromaStride;
                MvL0 = mvL0;
                MvL1 = mvL1;
                RefPicL0 = refPicL0;
                RefPicL1 = refPicL1;
                MotionUniform = motionUniform;
                ChromaQpIndexOffsetCb = chromaQpIndexOffsetCb;
                ChromaQpIndexOffsetCr = chromaQpIndexOffsetCr;
                IsFieldPicture = isFieldPicture;
                IsMbaffFrame = isMbaffFrame;
                PixelShift = pixelShift;
                BitDepth = bitDepth;
                IsChroma422 = isChroma422;
            }
        }

        /// <summary>
        /// What the loop filter needs that does change per macroblock. The caller holds one of
        /// these across the whole picture and writes only the fields that actually moved, which
        /// is the other half of what <see cref="DeblockPicture"/> buys: the MBAFF fields and the
        /// pass selectors are written once for a progressive picture and never again.
        /// </summary>
        /// <remarks>
        /// The strides here are the effective ones. An MBAFF field-coded macroblock filters each
        /// field independently and so sees a doubled stride and a base offset back into its own
        /// rows; a progressive macroblock sees the picture's own strides and zero offsets.
        /// </remarks>
        internal struct DeblockMacroblock
        {
            public int LumaStride;
            public int ChromaStride;
            public int MbX;
            public int MbY;
            public int Qp;
            public int FilterOffsetA;
            public int FilterOffsetB;
            public int CurrentMbAddr;
            public int LeftMbAddr;
            public int TopMbAddr;
            public int LumaBaseOffset;
            public int ChromaCbBaseOffset;
            public int ChromaCrBaseOffset;
            public bool HasLeftMb;
            public bool HasTopMb;
            public bool CurrentMbIsField;
            public bool LeftMbIsField;
            public bool TopMbIsField;
            public bool SkipHorizontalEdge0;
            public bool SkipVerticalEdge0;
            public bool VerticalOnly;
            public bool HorizontalOnly;
        }

        /// <summary>
        /// Applies deblocking filter to a macroblock per H.264 section 8.7.
        /// Processes vertical edges first, then horizontal edges.
        /// Each edge has 4 segments of 4 samples; bS is computed per segment.
        /// </summary>
        internal static void FilterMacroblock(
            in DeblockPicture picture,
            in DeblockMacroblock mb,
            in Macroblock leftMb,
            in Macroblock topMb,
            in Macroblock currentMb)
        {
            // The picture-constant half of what this method used to take as arguments, and the
            // per-macroblock half, read back into the names the body below has always used. The
            // body is unchanged; what changed is that the caller no longer stores forty-odd
            // argument slots for every macroblock in the picture.
            Span<byte> luma = picture.Luma;
            Span<byte> chromaCb = picture.ChromaCb;
            Span<byte> chromaCr = picture.ChromaCr;
            ReadOnlySpan<short> deblockMvL0 = picture.MvL0;
            ReadOnlySpan<short> deblockMvL1 = picture.MvL1;
            ReadOnlySpan<int> deblockRefPicL0 = picture.RefPicL0;
            ReadOnlySpan<int> deblockRefPicL1 = picture.RefPicL1;
            ReadOnlySpan<bool> deblockMotionUniform = picture.MotionUniform;
            int chromaQpIndexOffsetCb = picture.ChromaQpIndexOffsetCb;
            int chromaQpIndexOffsetCr = picture.ChromaQpIndexOffsetCr;
            bool isFieldPicture = picture.IsFieldPicture;
            bool isMbaffFrame = picture.IsMbaffFrame;
            int pixelShift = picture.PixelShift;
            int bitDepth = picture.BitDepth;
            bool isChroma422 = picture.IsChroma422;

            int lumaStride = mb.LumaStride;
            int chromaStride = mb.ChromaStride;
            int mbX = mb.MbX;
            int mbY = mb.MbY;
            int qp = mb.Qp;
            int filterOffsetA = mb.FilterOffsetA;
            int filterOffsetB = mb.FilterOffsetB;
            bool hasLeftMb = mb.HasLeftMb;
            bool hasTopMb = mb.HasTopMb;
            int currentMbAddr = mb.CurrentMbAddr;
            int leftMbAddr = mb.LeftMbAddr;
            int topMbAddr = mb.TopMbAddr;
            bool currentMbIsField = mb.CurrentMbIsField;
            bool leftMbIsField = mb.LeftMbIsField;
            bool topMbIsField = mb.TopMbIsField;
            int lumaBaseOffset = mb.LumaBaseOffset;
            int chromaCbBaseOffset = mb.ChromaCbBaseOffset;
            int chromaCrBaseOffset = mb.ChromaCrBaseOffset;
            bool skipHorizontalEdge0 = mb.SkipHorizontalEdge0;
            bool skipVerticalEdge0 = mb.SkipVerticalEdge0;
            bool verticalOnly = mb.VerticalOnly;
            bool horizontalOnly = mb.HorizontalOnly;
            int lumaX = mbX * 16;
            int lumaY = mbY * 16;
            int chromaX = mbX * 8;
            int chromaY = mbY * (isChroma422 ? 16 : 8);
            int maxPixelValue = (1 << bitDepth) - 1;
            int bitDepthScale = 1 << (bitDepth - 8); // 1 for 8-bit, 4 for 10-bit
            int qpBdOffset = 6 * (bitDepth - 8); // 0 for 8-bit, 12 for 10-bit
            
            // Base byte offsets for MBAFF field-coded MBs (shifts dest to correct field rows)
            int lumaOff = lumaBaseOffset;
            int cbOff = chromaCbBaseOffset;
            int crOff = chromaCrBaseOffset;

            bool diagPocMatch = (DiagPoc < 0 || CurrentDeblockPoc == DiagPoc) && (DiagDecodeCount < 0 || DeblockCallCount == DiagDecodeCount);
            bool pixelTraceActive = TracePixelCoords != null && TraceStride > 0 && diagPocMatch;
            bool diag = DiagOutput != null && diagPocMatch && (currentMbAddr == DiagMbAddr || (DiagMbAddrSet != null && DiagMbAddrSet.Contains(currentMbAddr)));
            if (diag)
            {
                DiagOutput!.WriteLine($"DEBLOCK MB({mbX},{mbY}) addr={currentMbAddr} POC={CurrentDeblockPoc} decode#{DeblockCallCount} QP={qp} isIntra={currentMb.IsIntra} offsetA={filterOffsetA} offsetB={filterOffsetB}");
                if (hasLeftMb) DiagOutput.WriteLine($"  Left: addr={leftMbAddr} QP={leftMb.QuantizationParameter} isIntra={leftMb.IsIntra}");
                if (hasTopMb) DiagOutput.WriteLine($"  Top: addr={topMbAddr} QP={topMb.QuantizationParameter} isIntra={topMb.IsIntra}");
                // Dump NZC for current MB
                var nzcStr = "";
                for (int y4 = 0; y4 < 4; y4++)
                    for (int x4 = 0; x4 < 4; x4++)
                        nzcStr += currentMb.HasNonZeroCoeffs4x4(x4, y4) ? "1" : "0";
                DiagOutput.WriteLine($"  NZC: {nzcStr}");
                // Dump refs and MVs
                for (int y4 = 0; y4 < 4; y4++)
                    for (int x4 = 0; x4 < 4; x4++)
                    {
                        int f = currentMbAddr * 16 + y4 * 4 + x4;
                        DiagOutput.WriteLine($"  [{x4},{y4}] refL0={deblockRefPicL0[f]} refL1={deblockRefPicL1[f]} mvL0=({deblockMvL0[2 * f]},{deblockMvL0[2 * f + 1]}) mvL1=({deblockMvL1[2 * f]},{deblockMvL1[2 * f + 1]})");
                    }
                // Save pre-deblocking pixels for this MB
                DiagPreDeblockLuma = new byte[16 * 16];
                DiagPreDeblockStride = lumaStride;
                DiagPreDeblockMbX = mbX; DiagPreDeblockMbY = mbY;
                for (int dy = 0; dy < 16; dy++)
                    for (int dx = 0; dx < 16; dx++)
                        DiagPreDeblockLuma[dy * 16 + dx] = luma[lumaOff + (lumaY + dy) * lumaStride + lumaX + dx];
            }

            // Boundary strength depends only on the two blocks either side of an edge, so it is
            // the same number for luma and for chroma, and chroma is now filtered on the same
            // edge that derived it - so the four strengths of the edge being worked on are all
            // that ever has to be held. This used to be a thirty-two entry cache filled with -1
            // on every macroblock, because the chroma passes came afterwards and had to know
            // which edges the luma passes had already done; a hundred and twenty-eight bytes
            // written for every macroblock in the picture, including the 85% that filter no
            // internal edge at all.
            SegmentStrengths edgeStrengthStorage = default;
            Span<int> edgeStrengths = edgeStrengthStorage;

            // An internal edge is only filtered where one of its sides carries residual or the
            // motion differs across it. A macroblock with no coded luma residual and one motion
            // vector throughout has neither, so all six of its internal edges - three in each
            // direction - are dead. Measured on a 1080p stream, 89.3% of edges have every
            // segment at strength zero, which is what skipped macroblocks being about three
            // quarters of the picture looks like from here.
            //
            // The uniformity half of that question is answered when the deblocking data is
            // written, where the values are already in registers. Re-deriving it here means
            // re-reading all ninety-six entries per macroblock, which is what made the first
            // attempt at using it a 3.2% loss - see docs/measured-dead-ends.md.
            bool currentMotionUniform = (uint)currentMbAddr < (uint)deblockMotionUniform.Length
                                        && deblockMotionUniform[currentMbAddr];
            bool internalEdgesQuiet = !currentMb.IsIntra && currentMb.CodedBlockPatternLuma == 0
                                      && currentMotionUniform;

            // The same question across a macroblock edge. An edge is dead when neither side
            // codes residual and the motion does not change across it - and both macroblocks
            // carrying one motion throughout is now a flag rather than a scan, so asking costs
            // six array reads instead of the ninety-six that made the first attempt at this a
            // loss (docs/measured-dead-ends.md).
            //
            // This skips the whole edge rather than supplying a strength for it: no quantiser
            // averaging, no threshold lookups, no four derivations, and no chroma pass over the
            // same edge. Measured on a 1080p stream, 46.4% of macroblock-edge derivations come
            // out zero.
            bool quietSide = !currentMb.IsIntra && currentMb.CodedBlockPatternLuma == 0
                             && currentMotionUniform;

            bool leftEdgeQuiet = quietSide && hasLeftMb && !leftMb.IsIntra
                && leftMb.CodedBlockPatternLuma == 0
                && (uint)leftMbAddr < (uint)deblockMotionUniform.Length
                && deblockMotionUniform[leftMbAddr]
                && MotionAgrees(deblockMvL0, deblockMvL1,
                    deblockRefPicL0, deblockRefPicL1, leftMbAddr * 16 + 3, currentMbAddr * 16,
                    leftMbAddr * 4 + 1, currentMbAddr * 4);

            bool topEdgeQuiet = quietSide && hasTopMb && !topMb.IsIntra
                && topMb.CodedBlockPatternLuma == 0
                && !(isMbaffFrame && currentMbIsField != topMbIsField)
                && (uint)topMbAddr < (uint)deblockMotionUniform.Length
                && deblockMotionUniform[topMbAddr]
                && MotionAgrees(deblockMvL0, deblockMvL1,
                    deblockRefPicL0, deblockRefPicL1, topMbAddr * 16 + 12, currentMbAddr * 16,
                    topMbAddr * 4 + 2, currentMbAddr * 4);

            // Filter vertical edges (left edge of macroblock, then 3 internal edges)
            // For 8x8 transform, skip internal edges 1 and 3 (inside 8x8 blocks)
            // per ffmpeg h264_loopfilter.c: step = IS_8x8DCT ? 2 : 1
            if (!horizontalOnly)
            for (int edge = 0; edge < 4; edge++)
            {
                if (edge == 0 && (leftEdgeQuiet || mbX == 0 || !hasLeftMb || skipVerticalEdge0))
                    continue;
                if (currentMb.Transform8x8 && (edge == 1 || edge == 3))
                    continue;
                if (edge > 0 && internalEdgesQuiet)
                    continue;

                // Chroma is filtered here, on this edge, off these strengths - which is what
                // ff_h264_filter_mb_fast's FILTER macro does and what this used to do in a
                // separate pass over the macroblock afterwards. There are two vertical chroma
                // edges for both 4:2:0 and 4:2:2, sitting on luma edges 0 and 2.
                bool chromaEdgeHere = (edge & 1) == 0;

                bool isMbEdge = (edge == 0);
                int edgeX = lumaX + edge * 4;

                // Average QP at MB boundaries per spec 8.7.2.1
                int edgeQp = qp;
                if (isMbEdge && hasLeftMb)
                    edgeQp = (qp + leftMb.QuantizationParameter + 1) >> 1;

                // Subtract qpBdOffset before table lookup (QP' → standard QP for 10-bit)
                // Biased by QpIndexBias and not clamped - see QpIndexBias. Both indices stay
                // inside the padded tables for every legal quantiser and slice offset.
                int indexA = edgeQp - qpBdOffset + filterOffsetA + QpIndexBias;
                int indexB = edgeQp - qpBdOffset + filterOffsetB + QpIndexBias;
                int alpha = AlphaTablePadded[indexA] * bitDepthScale;
                int beta = BetaTablePadded[indexB] * bitDepthScale;

                // Both tables are zero below index 16, and a zero threshold cannot be met:
                // |p0-q0| >= 0 always holds, so no line of this edge would be filtered. The
                // point of testing here rather than inside the filter is that it also skips
                // four boundary-strength derivations, which are the expensive part.
                // The luma thresholds no longer decide whether the edge is visited, only
                // whether luma is filtered: chroma derives its own from its own quantiser, and
                // a positive chroma_qp_index_offset can leave it live where luma is dead.
                bool lumaLive = alpha != 0 && beta != 0;
                if (!lumaLive && !chromaEdgeHere) continue;

                // 4 segments of 4 lines each
                if (lumaLive)
                for (int seg = 0; seg < 4; seg++)
                {
                    int bS = edgeStrengths[seg] =
                        CalculateBoundaryStrengthVertical(edge, seg, isMbEdge, leftMb, hasLeftMb, currentMb,
                        deblockMvL0, deblockMvL1,
                        deblockRefPicL0, deblockRefPicL1, currentMbAddr, leftMbAddr,
                        isFieldPicture, isMbaffFrame, currentMbIsField);
                    if (diag)
                        DiagOutput!.WriteLine($"  V-edge{edge} seg{seg}: bS={bS} QP={edgeQp} alpha={alpha} beta={beta} indexA={indexA - QpIndexBias}");
                    
                    // Trace bS=0 at known pixel positions before skipping
                    if (bS == 0 && TracePixelOffset >= 0 && edge == 2 && seg == 3)
                    {
                        // Check if this V-edge2 seg3 would reach our target pixel
                        int checkY = seg * 4 + 2; // line 2
                        int checkOffset = lumaOff + (lumaY + checkY) * lumaStride + edgeX;
                        int tgt = TracePixelOffset;
                        if (tgt >= checkOffset - 3 && tgt <= checkOffset + 2)
                            Console.WriteLine($"TRACE dc#{DeblockCallCount} POC={CurrentDeblockPoc} V-edge{edge} s{seg} MB({mbX},{mbY}): bS=0 SKIPPED (pixel would be at offset {checkOffset})");
                    }
                    
                    if (bS == 0) continue;

                    // All four lines of a segment share bS, alpha, beta and the tc0 lookup, so
                    // they differ only in their pixels - four lanes with no cross-lane traffic.
                    // Tracing keeps the scalar path so its per-line output stays intact.
                    if (pixelShift == 0 && !pixelTraceActive && TracePixelOffset < 0
                        && Dsp.H264DeblockingDsp.IsSupported)
                    {
                        int segOffset = lumaOff + (lumaY + seg * 4) * lumaStride + edgeX;
                        Dsp.H264DeblockingDsp.FilterLuma4(
                            luma, segOffset, 1, lumaStride, alpha, beta, bS,
                            Tc0Biased(bS, indexA) * bitDepthScale);
                        continue;
                    }

                    for (int line = 0; line < 4; line++)
                    {
                        int y = seg * 4 + line;
                        int offset = lumaOff + (lumaY + y) * lumaStride + edgeX;

                        // 10-bit pixel coord trace (V-edge: step=1, affects offset-3..offset+2)
                        if (pixelTraceActive)
                        {
                            int pixY = (lumaY + y);
                            // V-edge q0 is at edgeX, p0 at edgeX-1
                            bool needsTrace = false;
                            for (int dx = -3; dx <= 2; dx++)
                            {
                                if (TracePixelCoords.Contains((edgeX + dx, pixY)))
                                { needsTrace = true; break; }
                            }
                            if (needsTrace)
                            {
                                // Snapshot pre-filter values
                                int preP2 = ReadPixel(luma, offset - 3, pixelShift);
                                int preP1 = ReadPixel(luma, offset - 2, pixelShift);
                                int preP0 = ReadPixel(luma, offset - 1, pixelShift);
                                int preQ0 = ReadPixel(luma, offset, pixelShift);
                                int preQ1 = ReadPixel(luma, offset + 1, pixelShift);
                                int preQ2 = ReadPixel(luma, offset + 2, pixelShift);

                                FilterEdgeLuma(luma, offset, 1, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);

                                int postP2 = ReadPixel(luma, offset - 3, pixelShift);
                                int postP1 = ReadPixel(luma, offset - 2, pixelShift);
                                int postP0 = ReadPixel(luma, offset - 1, pixelShift);
                                int postQ0 = ReadPixel(luma, offset, pixelShift);
                                int postQ1 = ReadPixel(luma, offset + 1, pixelShift);
                                int postQ2 = ReadPixel(luma, offset + 2, pixelShift);

                                Console.WriteLine($"V-e{edge}s{seg}l{line} MB({mbX},{mbY}) y={pixY} bS={bS} a={alpha} b={beta}: p[{preP2},{preP1},{preP0}]q[{preQ0},{preQ1},{preQ2}] → p[{postP2},{postP1},{postP0}]q[{postQ0},{postQ1},{postQ2}]");
                                continue;
                            }
                        }

                        // Pixel trace for V-edge (legacy 8-bit trace)
                        if (TracePixelOffset >= 0)
                        {
                            int tgt = TracePixelOffset;
                            // V-edge affects p2(offset-3) to q2(offset+2)
                            if (tgt >= offset - 3 && tgt <= offset + 2)
                            {
                                string pos = tgt == offset - 2 ? "p1" : tgt == offset - 1 ? "p0" : tgt == offset ? "q0" : tgt == offset + 1 ? "q1" : tgt == offset - 3 ? "p2" : "q2";
                                int pre = luma[tgt];
                                FilterEdgeLuma(luma, offset, 1, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                                int post = luma[tgt];
                                Console.WriteLine($"TRACE dc#{DeblockCallCount} POC={CurrentDeblockPoc} V-edge{edge} s{seg} l{line} MB({mbX},{mbY}): [{pos}]={pre}→{post} bS={bS} a={alpha} b={beta} p=[{luma[offset-3]},{luma[offset-2]},{luma[offset-1]}] q=[{luma[offset]},{luma[offset+1]},{luma[offset+2]}]");
                                continue;
                            }
                        }

                        FilterEdgeLuma(luma, offset, 1, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                    }
                }

                if (chromaEdgeHere)
                {
                    int chromaEdgeX = chromaX + (edge >> 1) * 4;

                    int chromaQpCurCb = ChromaQpFromLumaQp(qp - qpBdOffset + chromaQpIndexOffsetCb);
                    int chromaQpCurCr = ChromaQpFromLumaQp(qp - qpBdOffset + chromaQpIndexOffsetCr);
                    int chromaQpEdgeCb = chromaQpCurCb;
                    int chromaQpEdgeCr = chromaQpCurCr;
                    if (isMbEdge && hasLeftMb)
                    {
                        int leftQp = leftMb.QuantizationParameter;
                        chromaQpEdgeCb = (chromaQpCurCb + ChromaQpFromLumaQp(leftQp - qpBdOffset + chromaQpIndexOffsetCb) + 1) >> 1;
                        chromaQpEdgeCr = (chromaQpCurCr + ChromaQpFromLumaQp(leftQp - qpBdOffset + chromaQpIndexOffsetCr) + 1) >> 1;
                    }

                    // Biased, not clamped - see QpIndexBias.
                    int chromaIndexACb = chromaQpEdgeCb + filterOffsetA + QpIndexBias;
                    int chromaIndexACr = chromaQpEdgeCr + filterOffsetA + QpIndexBias;
                    int chromaAlphaCb = AlphaTablePadded[chromaIndexACb] * bitDepthScale;
                    int chromaBetaCb = BetaTablePadded[chromaQpEdgeCb + filterOffsetB + QpIndexBias] * bitDepthScale;
                    int chromaAlphaCr = AlphaTablePadded[chromaIndexACr] * bitDepthScale;
                    int chromaBetaCr = BetaTablePadded[chromaQpEdgeCr + filterOffsetB + QpIndexBias] * bitDepthScale;

                    // 4 luma segments, each covering two chroma rows at 4:2:0 and four at 4:2:2.
                    // The strengths are the ones the luma pass just derived into edgeStrengths,
                    // except where luma's own thresholds were zero and it never ran - chroma's
                    // come from its own quantiser and can be live where luma's are not.
                    int chromaRowsPerSeg = isChroma422 ? 4 : 2;
                    if (!lumaLive)
                        for (int lumaSeg = 0; lumaSeg < 4; lumaSeg++)
                            edgeStrengths[lumaSeg] = CalculateBoundaryStrengthVertical(
                                edge, lumaSeg, isMbEdge, leftMb, hasLeftMb, currentMb,
                                deblockMvL0, deblockMvL1,
                                deblockRefPicL0, deblockRefPicL1, currentMbAddr, leftMbAddr,
                                isFieldPicture, isMbaffFrame, currentMbIsField);

                    // The eight lines of a 4:2:0 edge are exactly one vector of shorts.
                    int vEdgeOffset = chromaY * chromaStride + chromaEdgeX;
                    bool vDone = !isChroma422 && pixelShift == 0
                        && Dsp.H264DeblockingDsp.IsChromaEdgeSupported
                        && TryFilterChromaEdges(
                               chromaCb, chromaCr, cbOff + vEdgeOffset, crOff + vEdgeOffset,
                               1, chromaStride,
                               chromaAlphaCb, chromaBetaCb, chromaIndexACb,
                               chromaAlphaCr, chromaBetaCr, chromaIndexACr,
                               edgeStrengths, bitDepthScale);

                    if (!vDone)
                    for (int lumaSeg = 0; lumaSeg < 4; lumaSeg++)
                    {
                        int bS = edgeStrengths[lumaSeg];
                        if (bS == 0) continue;

                        for (int line = 0; line < chromaRowsPerSeg; line++)
                        {
                            int y = lumaSeg * chromaRowsPerSeg + line;
                            int offset = (chromaY + y) * chromaStride + chromaEdgeX;
                            FilterEdgeChroma(chromaCb, cbOff + offset, 1, chromaAlphaCb, chromaBetaCb, bS, chromaIndexACb - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                            FilterEdgeChroma(chromaCr, crOff + offset, 1, chromaAlphaCr, chromaBetaCr, bS, chromaIndexACr - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                        }
                    }
                }
            }

            // Filter horizontal edges (top edge of macroblock, then 3 internal edges)
            // For 8x8 transform, skip internal edges 1 and 3 (inside 8x8 blocks)
            if (!verticalOnly)
            for (int edge = 0; edge < 4; edge++)
            {
                if (edge == 0 && (topEdgeQuiet || mbY == 0 || !hasTopMb || skipHorizontalEdge0))
                    continue;
                if (edge > 0 && internalEdgesQuiet)
                    continue;

                // 4:2:0 has two horizontal chroma edges, on luma edges 0 and 2; 4:2:2 has four,
                // one per luma edge, because its chroma is full height. So at 4:2:2 the 8x8
                // transform's skip of the odd luma edges cannot skip the edge outright - there
                // is a chroma edge on it - and it has to kill luma alone.
                bool chromaEdgeHere = isChroma422 || (edge & 1) == 0;
                bool lumaSkippedBy8x8 = currentMb.Transform8x8 && (edge & 1) == 1;
                if (lumaSkippedBy8x8 && !chromaEdgeHere)
                    continue;

                bool isMbEdge = (edge == 0);
                int edgeY = lumaY + edge * 4;

                int edgeQp = qp;
                if (isMbEdge && hasTopMb)
                    edgeQp = (qp + topMb.QuantizationParameter + 1) >> 1;

                // Biased by QpIndexBias and not clamped - see QpIndexBias. Both indices stay
                // inside the padded tables for every legal quantiser and slice offset.
                int indexA = edgeQp - qpBdOffset + filterOffsetA + QpIndexBias;
                int indexB = edgeQp - qpBdOffset + filterOffsetB + QpIndexBias;
                int alpha = AlphaTablePadded[indexA] * bitDepthScale;
                int beta = BetaTablePadded[indexB] * bitDepthScale;

                // Both tables are zero below index 16, and a zero threshold cannot be met:
                // |p0-q0| >= 0 always holds, so no line of this edge would be filtered. The
                // point of testing here rather than inside the filter is that it also skips
                // four boundary-strength derivations, which are the expensive part.
                bool lumaLive = alpha != 0 && beta != 0 && !lumaSkippedBy8x8;
                if (!lumaLive && !chromaEdgeHere) continue;

                // 4 segments of 4 columns each
                if (lumaLive)
                for (int seg = 0; seg < 4; seg++)
                {
                    int bS = edgeStrengths[seg] =
                        CalculateBoundaryStrengthHorizontal(edge, seg, isMbEdge, topMb, hasTopMb, currentMb,
                        deblockMvL0, deblockMvL1,
                        deblockRefPicL0, deblockRefPicL1, currentMbAddr, topMbAddr,
                        isFieldPicture, isMbaffFrame, currentMbIsField, topMbIsField);
                    if (diag)
                        DiagOutput!.WriteLine($"  H-edge{edge} seg{seg}: bS={bS} QP={edgeQp} alpha={alpha} beta={beta} indexA={indexA - QpIndexBias}");

                    // Dump H-edge0 bS with full details for boundary analysis
                    if (DumpAllHEdge0Bs && edge == 0)
                    {
                        // Only dump for specific MB positions where we know errors occur
                        bool isTargetMb = (mbX == 9 && mbY == 2) || (mbX == 4 && mbY == 3) ||
                                          (mbX == 12 && mbY == 4) || (mbX == 11 && mbY == 4);
                        if (isTargetMb)
                        {
                            int pIdx = topMbAddr * 16 + 3 * 4 + seg;
                            int qIdx = currentMbAddr * 16 + 0 * 4 + seg;
                            Console.Write($"H0 MB({mbX},{mbY}) s{seg} dc#{DeblockCallCount}: bS={bS} ");
                            if (hasTopMb)
                            {
                                Console.Write($"topI={topMb.IsIntra} curI={currentMb.IsIntra} ");
                                Console.Write($"nzcP={topMb.HasNonZeroCoeffs4x4(seg, 3)} nzcQ={currentMb.HasNonZeroCoeffs4x4(seg, 0)} ");
                                if (!currentMb.IsIntra && !topMb.IsIntra)
                                {
                                    Console.Write($"rpL0=({deblockRefPicL0[pIdx]},{deblockRefPicL0[qIdx]}) ");
                                    Console.Write($"rpL1=({deblockRefPicL1[pIdx]},{deblockRefPicL1[qIdx]}) ");
                                    Console.Write($"mvL0=({deblockMvL0[2 * pIdx]},{deblockMvL0[2 * pIdx + 1]})/({deblockMvL0[2 * qIdx]},{deblockMvL0[2 * qIdx + 1]}) ");
                                    Console.Write($"mvL1=({deblockMvL1[2 * pIdx]},{deblockMvL1[2 * pIdx + 1]})/({deblockMvL1[2 * qIdx]},{deblockMvL1[2 * qIdx + 1]}) ");
                                }
                            }
                            Console.WriteLine($"QP={edgeQp} a={alpha} b={beta}");
                        }
                    }

                    // Trace bS=0 at known pixel positions before skipping (H-edge)
                    if (bS == 0 && TracePixelOffset >= 0 && (edge == 0 || edge == 3) && seg == 1)
                    {
                        int checkX = seg * 4 + 2; // col 2
                        int checkOffset = lumaOff + edgeY * lumaStride + lumaX + checkX;
                        int tgt = TracePixelOffset;
                        bool affects = false;
                        for (int d = -3; d <= 2; d++) { if (checkOffset + d * lumaStride == tgt) { affects = true; break; } }
                        if (affects)
                            Console.WriteLine($"TRACE dc#{DeblockCallCount} POC={CurrentDeblockPoc} H-edge{edge} s{seg} MB({mbX},{mbY}): bS=0 SKIPPED");
                    }

                    if (bS == 0) continue;

                    if (pixelShift == 0 && !pixelTraceActive && TracePixelOffset < 0
                        && Dsp.H264DeblockingDsp.IsSupported)
                    {
                        int segOffset = lumaOff + edgeY * lumaStride + lumaX + seg * 4;
                        Dsp.H264DeblockingDsp.FilterLuma4(
                            luma, segOffset, lumaStride, 1, alpha, beta, bS,
                            Tc0Biased(bS, indexA) * bitDepthScale);
                        continue;
                    }

                    for (int col = 0; col < 4; col++)
                    {
                        int x = seg * 4 + col;
                        int offset = lumaOff + edgeY * lumaStride + lumaX + x;
                        int pixX = lumaX + x;

                        // 10-bit pixel coord trace (H-edge: step=stride, affects rows edgeY-3..edgeY+2)
                        if (pixelTraceActive)
                        {
                            bool needsTrace = false;
                            for (int dy = -3; dy <= 2; dy++)
                            {
                                if (TracePixelCoords.Contains((pixX, edgeY + dy)))
                                { needsTrace = true; break; }
                            }
                            if (needsTrace)
                            {
                                int preP2 = ReadPixel(luma, offset - 3 * lumaStride, pixelShift);
                                int preP1 = ReadPixel(luma, offset - 2 * lumaStride, pixelShift);
                                int preP0 = ReadPixel(luma, offset - lumaStride, pixelShift);
                                int preQ0 = ReadPixel(luma, offset, pixelShift);
                                int preQ1 = ReadPixel(luma, offset + lumaStride, pixelShift);
                                int preQ2 = ReadPixel(luma, offset + 2 * lumaStride, pixelShift);

                                FilterEdgeLuma(luma, offset, lumaStride, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);

                                int postP2 = ReadPixel(luma, offset - 3 * lumaStride, pixelShift);
                                int postP1 = ReadPixel(luma, offset - 2 * lumaStride, pixelShift);
                                int postP0 = ReadPixel(luma, offset - lumaStride, pixelShift);
                                int postQ0 = ReadPixel(luma, offset, pixelShift);
                                int postQ1 = ReadPixel(luma, offset + lumaStride, pixelShift);
                                int postQ2 = ReadPixel(luma, offset + 2 * lumaStride, pixelShift);

                                Console.WriteLine($"H-e{edge}s{seg}c{col} MB({mbX},{mbY}) x={pixX} bS={bS} a={alpha} b={beta}: p[{preP2},{preP1},{preP0}]q[{preQ0},{preQ1},{preQ2}] → p[{postP2},{postP1},{postP0}]q[{postQ0},{postQ1},{postQ2}]");
                                continue;
                            }
                        }

                        // Pixel trace for H-edge (legacy 8-bit)
                        if (TracePixelOffset >= 0)
                        {
                            int tgt = TracePixelOffset;
                            // H-edge affects p2(offset-3*step) to q2(offset+2*step)
                            bool affects = false;
                            for (int d = -3; d <= 2; d++)
                            {
                                if (offset + d * lumaStride == tgt) { affects = true; break; }
                            }
                            if (affects)
                            {
                                int pre = luma[tgt];
                                FilterEdgeLuma(luma, offset, lumaStride, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                                int post = luma[tgt];
                                if (pre != post)
                                {
                                    string hpos = "";
                                    for (int d = -3; d <= 2; d++) { if (offset + d * lumaStride == tgt) { hpos = d == -2 ? "p1" : d == -1 ? "p0" : d == 0 ? "q0" : d == 1 ? "q1" : d == -3 ? "p2" : "q2"; break; } }
                                    Console.Write($"TRACE dc#{DeblockCallCount} POC={CurrentDeblockPoc} H-edge{edge} s{seg} c{col} MB({mbX},{mbY}): [{hpos}]={pre}→{post} bS={bS} a={alpha} b={beta}");
                                    Console.Write($" p=[{luma[offset-3*lumaStride]},{luma[offset-2*lumaStride]},{luma[offset-lumaStride]}]");
                                    Console.WriteLine($" q=[{luma[offset]},{luma[offset+lumaStride]},{luma[offset+2*lumaStride]}]");
                                }
                                else
                                {
                                    // Log even when unchanged, for debugging — print p/q values to understand why
                                    Console.Write($"TRACE dc#{DeblockCallCount} POC={CurrentDeblockPoc} H-edge{edge} s{seg} c{col} MB({mbX},{mbY}): UNCHANGED={pre} bS={bS} a={alpha} b={beta}");
                                    Console.Write($" p=[{luma[offset-3*lumaStride]},{luma[offset-2*lumaStride]},{luma[offset-lumaStride]}]");
                                    Console.WriteLine($" q=[{luma[offset]},{luma[offset+lumaStride]},{luma[offset+2*lumaStride]}]");
                                }
                                continue;
                            }
                        }

                        FilterEdgeLuma(luma, offset, lumaStride, alpha, beta, bS, indexA - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                    }
                }

                if (chromaEdgeHere)
                {
                    int chromaEdgeY = chromaY + (isChroma422 ? edge : edge >> 1) * 4;

                    int chromaQpCurCb = ChromaQpFromLumaQp(qp - qpBdOffset + chromaQpIndexOffsetCb);
                    int chromaQpCurCr = ChromaQpFromLumaQp(qp - qpBdOffset + chromaQpIndexOffsetCr);
                    int chromaQpEdgeCb = chromaQpCurCb;
                    int chromaQpEdgeCr = chromaQpCurCr;
                    if (isMbEdge && hasTopMb)
                    {
                        int topQp = topMb.QuantizationParameter;
                        chromaQpEdgeCb = (chromaQpCurCb + ChromaQpFromLumaQp(topQp - qpBdOffset + chromaQpIndexOffsetCb) + 1) >> 1;
                        chromaQpEdgeCr = (chromaQpCurCr + ChromaQpFromLumaQp(topQp - qpBdOffset + chromaQpIndexOffsetCr) + 1) >> 1;
                    }

                    // Biased, not clamped - see QpIndexBias.
                    int chromaIndexACb = chromaQpEdgeCb + filterOffsetA + QpIndexBias;
                    int chromaIndexACr = chromaQpEdgeCr + filterOffsetA + QpIndexBias;
                    int chromaAlphaCb = AlphaTablePadded[chromaIndexACb] * bitDepthScale;
                    int chromaBetaCb = BetaTablePadded[chromaQpEdgeCb + filterOffsetB + QpIndexBias] * bitDepthScale;
                    int chromaAlphaCr = AlphaTablePadded[chromaIndexACr] * bitDepthScale;
                    int chromaBetaCr = BetaTablePadded[chromaQpEdgeCr + filterOffsetB + QpIndexBias] * bitDepthScale;

                    // 4 luma segments, each covering two chroma columns. As above: the luma
                    // pass derived these unless its own thresholds kept it from running.
                    if (!lumaLive)
                        for (int lumaSeg = 0; lumaSeg < 4; lumaSeg++)
                            edgeStrengths[lumaSeg] = CalculateBoundaryStrengthHorizontal(
                                edge, lumaSeg, isMbEdge, topMb, hasTopMb, currentMb,
                                deblockMvL0, deblockMvL1,
                                deblockRefPicL0, deblockRefPicL1, currentMbAddr, topMbAddr,
                                isFieldPicture, isMbaffFrame, currentMbIsField, topMbIsField);

                    // Here the eight lanes are eight adjacent columns, so they load and store whole.
                    int hEdgeOffset = chromaEdgeY * chromaStride + chromaX;
                    bool hDone = !isChroma422 && pixelShift == 0
                        && Dsp.H264DeblockingDsp.IsChromaEdgeSupported
                        && TryFilterChromaEdges(
                               chromaCb, chromaCr, cbOff + hEdgeOffset, crOff + hEdgeOffset,
                               chromaStride, 1,
                               chromaAlphaCb, chromaBetaCb, chromaIndexACb,
                               chromaAlphaCr, chromaBetaCr, chromaIndexACr,
                               edgeStrengths, bitDepthScale);

                    if (!hDone)
                    for (int lumaSeg = 0; lumaSeg < 4; lumaSeg++)
                    {
                        int bS = edgeStrengths[lumaSeg];
                        if (bS == 0) continue;

                        for (int col = 0; col < 2; col++)
                        {
                            int x = lumaSeg * 2 + col;
                            int offset = chromaEdgeY * chromaStride + chromaX + x;
                            FilterEdgeChroma(chromaCb, cbOff + offset, chromaStride, chromaAlphaCb, chromaBetaCb, bS, chromaIndexACb - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                            FilterEdgeChroma(chromaCr, crOff + offset, chromaStride, chromaAlphaCr, chromaBetaCr, bS, chromaIndexACr - QpIndexBias, pixelShift, maxPixelValue, bitDepthScale);
                        }
                    }
                }
            }

            // Post-deblocking pixel dump for diagnostic MB (only on last pass)
            if (!verticalOnly && diag && DiagPreDeblockLuma != null)
            {
                DiagOutput!.WriteLine($"  Post-deblock pixel delta (deblocked - original):");
                for (int dy = 0; dy < 16; dy++)
                {
                    var line = new System.Text.StringBuilder($"    y={dy:D2}: ");
                    for (int dx = 0; dx < 16; dx++)
                    {
                        int pre = DiagPreDeblockLuma[dy * 16 + dx];
                        int post = luma[lumaOff + (lumaY + dy) * lumaStride + lumaX + dx];
                        int delta = post - pre;
                        line.Append(delta != 0 ? $"{delta,4}" : "   .");
                    }
                    DiagOutput.WriteLine(line.ToString());
                }
            }
        }

        /// <summary>
        /// Calculates boundary strength for a vertical edge segment per H.264 Table 8-18.
        /// edge: 0-3 (0 = left MB boundary), seg: 0-3 (4-pixel segment index).
        /// P block is on the left, Q block is on the right of the edge.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CalculateBoundaryStrengthVertical(
            int edge, int seg, bool isMbEdge,
            in Macroblock leftMb, bool hasLeftMb, in Macroblock currentMb,
            ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
            ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1,
            int currentMbAddr, int leftMbAddr,
            bool isFieldPicture,
            bool isMbaffFrame = false,
            bool currentMbIsField = false)
        {
            // MVy threshold: field pictures and MBAFF field MBs use ±2 qpel (threshold 1 → "> 1" = ">= 2")
            // Frame: ±4 qpel (threshold 3 → "> 3" = ">= 4")
            int mvyThreshold = (isFieldPicture || (isMbaffFrame && currentMbIsField)) ? 1 : 3;
            
            if (isMbEdge)
            {
                if (currentMb.IsIntra)
                    return 4;
                if (hasLeftMb && leftMb.IsIntra)
                    return 4;
                
                // P block: left MB at (x4=3, y4=seg), Q block: current MB at (x4=0, y4=seg)
                int pIdx = leftMbAddr * 16 + seg * 4 + 3;
                int qIdx = currentMbAddr * 16 + seg * 4 + 0;
                int pPart = leftMbAddr * 4 + (seg >> 1) * 2 + 1;
                int qPart = currentMbAddr * 4 + (seg >> 1) * 2;
                
                bool hasCoeffsP = hasLeftMb && leftMb.HasNonZeroCoeffs4x4(3, seg);
                bool hasCoeffsQ = currentMb.HasNonZeroCoeffs4x4(0, seg);
                if (hasCoeffsP || hasCoeffsQ)
                    return 2;
                
                return CompareMvForBoundaryStrength(mvL0, mvL1, refPicL0, refPicL1, pIdx, qIdx, pPart, qPart, mvyThreshold);
            }

            // Internal edge within the MB
            if (currentMb.IsIntra)
                return 3;

            // P block at (edge-1, seg), Q block at (edge, seg)
            bool hasCoeffsIntP = currentMb.HasNonZeroCoeffs4x4(edge - 1, seg);
            bool hasCoeffsIntQ = currentMb.HasNonZeroCoeffs4x4(edge, seg);
            if (hasCoeffsIntP || hasCoeffsIntQ)
                return 2;

            int pIntIdx = currentMbAddr * 16 + seg * 4 + (edge - 1);
            int qIntIdx = currentMbAddr * 16 + seg * 4 + edge;
            int pIntPart = currentMbAddr * 4 + (seg >> 1) * 2 + ((edge - 1) >> 1);
            int qIntPart = currentMbAddr * 4 + (seg >> 1) * 2 + (edge >> 1);
            return CompareMvForBoundaryStrength(mvL0, mvL1, refPicL0, refPicL1, pIntIdx, qIntIdx, pIntPart, qIntPart, mvyThreshold);
        }

        /// <summary>
        /// <summary>
        /// Whether every internal edge of this macroblock must have a boundary strength of zero.
        /// </summary>
        /// <remarks>
        /// An internal edge is only filtered if one of its sides carries residual or the motion
        /// differs across it. A macroblock with no coded luma residual and one motion vector
        /// throughout has neither, so all six of its internal edges - three in each direction -
        /// are dead. Measured on a 1080p stream, 89.3% of edges have every segment at strength
        /// zero, which is what skipped macroblocks being about three quarters of the picture
        /// looks like from here.
        ///
        /// Testing it once costs sixteen comparisons; deriving it per segment costs twenty-four
        /// derivations, each of which reads coefficient counts and then compares motion vectors
        /// and reference indices. FFmpeg reaches the same place from the other side, computing
        /// every strength for the macroblock in one vectorised pass and then skipping a dead edge
        /// with a single test of all four values.
        /// </remarks>
        private static bool InternalEdgesAreQuiet(
            in Macroblock currentMb, int currentMbAddr,
            ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
            ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1)
        {
            // Intra macroblocks filter their internal edges at strength three regardless.
            if (currentMb.IsIntra || currentMb.CodedBlockPatternLuma != 0)
                return false;

            int first = currentMbAddr * 16;
            if (2 * (first + 16) > mvL0.Length)
                return false;

            // One 32-bit value is a whole vector now, so a block agrees in one comparison.
            ref short m0 = ref MemoryMarshal.GetReference(mvL0);
            ref short m1 = ref MemoryMarshal.GetReference(mvL1);
            int v0 = ReadVector(ref m0, first), v1 = ReadVector(ref m1, first);
            int r0 = refPicL0[first], r1 = refPicL1[first];

            for (int i = 1; i < 16; i++)
            {
                int at = first + i;
                if (ReadVector(ref m0, at) != v0 || ReadVector(ref m1, at) != v1 ||
                    refPicL0[at] != r0 || refPicL1[at] != r1)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates boundary strength for a horizontal edge segment per H.264 Table 8-18.
        /// edge: 0-3 (0 = top MB boundary), seg: 0-3 (4-pixel segment index).
        /// P block is above, Q block is below the edge.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CalculateBoundaryStrengthHorizontal(
            int edge, int seg, bool isMbEdge,
            in Macroblock topMb, bool hasTopMb, in Macroblock currentMb,
            ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
            ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1,
            int currentMbAddr, int topMbAddr,
            bool isFieldPicture,
            bool isMbaffFrame = false,
            bool currentMbIsField = false,
            bool topMbIsField = false)
        {
            // MVy threshold: field and MBAFF field MBs use tighter threshold
            int mvyThreshold = (isFieldPicture || (isMbaffFrame && currentMbIsField)) ? 1 : 3;
            
            if (isMbEdge)
            {
                bool currentIsIntra = currentMb.IsIntra;
                bool topIsIntra = hasTopMb && topMb.IsIntra;
                
                int bS;
                
                if (currentIsIntra || topIsIntra)
                {
                    // Field pictures: always bS=3 for horizontal intra boundary edges.
                    // (FFmpeg fast path h264_loopfilter.c:271: FIELD_PICTURE ? bS3 : bS4)
                    if (isFieldPicture)
                    {
                        bS = 3;
                    }
                    else if (isMbaffFrame)
                    {
                        // MBAFF slow path h264_loopfilter.c:547-552:
                        // Start at bS=3. Upgrade to bS=4 only if NEITHER MB is interlaced
                        // (i.e., both are frame-coded). The upgrade criterion is interlacing
                        // status, NOT whether both are intra.
                        bS = (!currentMbIsField && !topMbIsField) ? 4 : 3;
                    }
                    else
                    {
                        // Progressive frame: bS=4 at intra boundary
                        bS = 4;
                    }
                }
                else
                {
                    // FFmpeg h264_loopfilter.c:557-559: MBAFF cross-type horizontal override.
                    // When current and top have different interlacing, set bS=1 as baseline
                    // (mv_done=1 skips MV check). However, NNZ still upgrades to bS=2
                    // (FFmpeg line 577-579 runs AFTER the cross-type override).
                    bool crossType = isMbaffFrame && currentMbIsField != topMbIsField;
                    
                    // P block: top MB at (x4=seg, y4=3), Q block: current MB at (x4=seg, y4=0)
                    int pIdx = topMbAddr * 16 + 3 * 4 + seg;
                    int qIdx = currentMbAddr * 16 + 0 * 4 + seg;
                    int pPart = topMbAddr * 4 + 2 + (seg >> 1);
                    int qPart = currentMbAddr * 4 + (seg >> 1);
                    
                    bool hasCoeffsP = hasTopMb && topMb.HasNonZeroCoeffs4x4(seg, 3);
                    bool hasCoeffsQ = currentMb.HasNonZeroCoeffs4x4(seg, 0);
                    if (hasCoeffsP || hasCoeffsQ)
                        bS = 2;
                    else if (crossType)
                        bS = 1;
                    else
                        bS = CompareMvForBoundaryStrength(mvL0, mvL1, refPicL0, refPicL1, pIdx, qIdx, pPart, qPart, mvyThreshold);
                }
                
                return bS;
            }

            if (currentMb.IsIntra)
                return 3;

            // P block at (seg, edge-1), Q block at (seg, edge)
            bool hasCoeffsIntP = currentMb.HasNonZeroCoeffs4x4(seg, edge - 1);
            bool hasCoeffsIntQ = currentMb.HasNonZeroCoeffs4x4(seg, edge);
            if (hasCoeffsIntP || hasCoeffsIntQ)
                return 2;

            int pIntIdx = currentMbAddr * 16 + (edge - 1) * 4 + seg;
            int qIntIdx = currentMbAddr * 16 + edge * 4 + seg;
            int pIntPart = currentMbAddr * 4 + ((edge - 1) >> 1) * 2 + (seg >> 1);
            int qIntPart = currentMbAddr * 4 + (edge >> 1) * 2 + (seg >> 1);
            return CompareMvForBoundaryStrength(mvL0, mvL1, refPicL0, refPicL1, pIntIdx, qIntIdx, pIntPart, qIntPart, mvyThreshold);
        }
        
        /// <summary>
        /// Compares reference pictures (by POC) and motion vectors between two adjacent 4x4 blocks
        /// to determine bS=1 (different) or bS=0 (matching) per H.264 Table 8-18.
        /// For B-slices with two reference lists, checks both straight and swapped configurations.
        /// Uses picture identity (POC) instead of raw ref indices for correct cross-list comparison.
        /// Returns 1 if refs differ or any MV component differs by >= 4 quarter-pel units.
        /// </summary>
        /// <summary>Both components of one motion vector, as a single 32-bit value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadVector(ref short motion, int block)
            => Unsafe.ReadUnaligned<int>(
                ref Unsafe.As<short, byte>(ref Unsafe.Add(ref motion, (nint)(2 * block))));

        /// <summary>
        /// Whether two blocks carry identical motion - the same references and the same vectors.
        /// </summary>
        /// <remarks>
        /// Stricter than the boundary strength test, which allows the vectors to differ by up to
        /// a whole sample. Being stricter is safe here: it can only decline to skip an edge that
        /// would have derived to zero anyway, never skip one that needed filtering.
        /// </remarks>
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MotionAgrees(
            ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
            ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1,
            int a, int b, int aPart, int bPart)
        {
            if ((uint)(2 * a + 1) >= (uint)mvL0.Length || (uint)(2 * b + 1) >= (uint)mvL0.Length)
                return false;

            // Motion vectors are per 4x4 block, reference identity per 8x8 partition. With
            // the components adjacent a whole vector is one 32-bit read and one comparison.
            ref short m0 = ref MemoryMarshal.GetReference(mvL0);
            ref short m1 = ref MemoryMarshal.GetReference(mvL1);

            return refPicL0[aPart] == refPicL0[bPart] && refPicL1[aPart] == refPicL1[bPart]
                && ReadVector(ref m0, a) == ReadVector(ref m0, b)
                && ReadVector(ref m1, a) == ReadVector(ref m1, b);
        }

        /// <remarks>
        /// These index through a ref rather than the span. Every index is
        /// <c>macroblockAddress * 16 + block</c> with block in 0..15, against arrays allocated
        /// as <c>macroblockCount * 16</c> - so the bound holds by construction of the caller's
        /// loop, not by anything the JIT can see. It therefore emits a check on each of the
        /// twelve reads made here, and this runs up to thirty-two times per macroblock.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CompareMvForBoundaryStrength(
            ReadOnlySpan<short> mvL0, ReadOnlySpan<short> mvL1,
            ReadOnlySpan<int> refPicL0, ReadOnlySpan<int> refPicL1,
            int pIdx, int qIdx, int pPart, int qPart,
            int mvyThreshold = 3)
        {
            ref int r0 = ref MemoryMarshal.GetReference(refPicL0);
            ref int r1 = ref MemoryMarshal.GetReference(refPicL1);

            int pRefPicL0 = Unsafe.Add(ref r0, (nint)pPart);
            int qRefPicL0 = Unsafe.Add(ref r0, (nint)qPart);
            int pRefPicL1 = Unsafe.Add(ref r1, (nint)pPart);
            int qRefPicL1 = Unsafe.Add(ref r1, (nint)qPart);

            // Straight comparison: P_L0 against Q_L0 and P_L1 against Q_L1.
            if (pRefPicL0 == qRefPicL0 && pRefPicL1 == qRefPicL1)
            {
                if (!MvDiffExceedsThreshold(mvL0, pIdx, qIdx, pRefPicL0, mvyThreshold) &&
                    !MvDiffExceedsThreshold(mvL1, pIdx, qIdx, pRefPicL1, mvyThreshold))
                    return 0;
            }

            // Swapped, for B slices predicting from both lists.
            if (pRefPicL0 == qRefPicL1 && pRefPicL1 == qRefPicL0)
            {
                if (!MvDiffExceedsThresholdCross(mvL0, pIdx, mvL1, qIdx, pRefPicL0, mvyThreshold) &&
                    !MvDiffExceedsThresholdCross(mvL1, pIdx, mvL0, qIdx, pRefPicL1, mvyThreshold))
                    return 0;
            }

            return 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MvDiffExceedsThreshold(
            ReadOnlySpan<short> mv,
            int pIdx, int qIdx, int refPic,
            int mvyThreshold)
        {
            if (refPic < 0) return false;

            ref short m = ref MemoryMarshal.GetReference(mv);
            int diffX = Unsafe.Add(ref m, (nint)(2 * pIdx)) - Unsafe.Add(ref m, (nint)(2 * qIdx));
            int diffY = Unsafe.Add(ref m, (nint)(2 * pIdx + 1)) - Unsafe.Add(ref m, (nint)(2 * qIdx + 1));
            return diffX > 3 || diffX < -3 || diffY > mvyThreshold || diffY < -mvyThreshold;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MvDiffExceedsThresholdCross(
            ReadOnlySpan<short> pMv, int pIdx,
            ReadOnlySpan<short> qMv, int qIdx,
            int refPic,
            int mvyThreshold)
        {
            if (refPic < 0) return false;

            ref short pm = ref MemoryMarshal.GetReference(pMv);
            ref short qm = ref MemoryMarshal.GetReference(qMv);
            int diffX = Unsafe.Add(ref pm, (nint)(2 * pIdx)) - Unsafe.Add(ref qm, (nint)(2 * qIdx));
            int diffY = Unsafe.Add(ref pm, (nint)(2 * pIdx + 1)) - Unsafe.Add(ref qm, (nint)(2 * qIdx + 1));
            return diffX > 3 || diffX < -3 || diffY > mvyThreshold || diffY < -mvyThreshold;
        }

        /// <summary>
        /// Filters a single luma edge (4 pixels on each side).
        /// </summary>
        /// <param name="buffer">Luma buffer.</param>
        /// <param name="offset">Offset to the edge position (q0).</param>
        /// <param name="step">Step between adjacent samples.</param>
        /// <param name="alpha">Alpha threshold.</param>
        /// <param name="beta">Beta threshold.</param>
        /// <param name="boundaryStrength">Boundary strength (1-4).</param>
        /// <param name="indexA">Index A = Clamp(edgeQp + filterOffsetA, 0, 51) for tc0 lookup.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <remarks>
        /// Internal rather than private so the vectorised replacement can be checked against this
        /// exact code rather than against a second copy of the formulas - a reference written from
        /// the same reading of the spec can share the same misunderstanding.
        /// </remarks>
        internal static void FilterEdgeLuma(
            Span<byte> buffer,
            int offset,
            int step,
            int alpha,
            int beta,
            int boundaryStrength,
            int indexA,
            int pixelShift = 0, int maxPixelValue = 255, int bitDepthScale = 1)
        {
            // Get samples: p3 p2 p1 p0 | q0 q1 q2 q3
            int p0 = ReadPixel(buffer, offset - step, pixelShift);
            int p1 = ReadPixel(buffer, offset - 2 * step, pixelShift);
            int p2 = ReadPixel(buffer, offset - 3 * step, pixelShift);
            int q0 = ReadPixel(buffer, offset, pixelShift);
            int q1 = ReadPixel(buffer, offset + step, pixelShift);
            int q2 = ReadPixel(buffer, offset + 2 * step, pixelShift);

            // Check filtering conditions
            if (Math.Abs(p0 - q0) >= alpha)
                return;
            if (Math.Abs(p1 - p0) >= beta)
                return;
            if (Math.Abs(q1 - q0) >= beta)
                return;

            if (boundaryStrength == 4)
            {
                // Strong filtering
                FilterStrongLuma(buffer, offset, step, p0, p1, p2, q0, q1, q2, alpha, beta,
                    pixelShift, maxPixelValue);
            }
            else
            {
                // Normal filtering
                FilterNormalLuma(buffer, offset, step, p0, p1, q0, q1, boundaryStrength, indexA, alpha, beta,
                    pixelShift, maxPixelValue, bitDepthScale);
            }
        }

        /// <summary>
        /// Strong luma filtering (bS = 4).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FilterStrongLuma(
            Span<byte> buffer,
            int offset,
            int step,
            int p0, int p1, int p2,
            int q0, int q1, int q2,
            int alpha, int beta,
            int pixelShift = 0, int maxPixelValue = 255)
        {
            int p3 = ReadPixel(buffer, offset - 4 * step, pixelShift);
            int q3 = ReadPixel(buffer, offset + 3 * step, pixelShift);

            bool smallGap = (Math.Abs(p0 - q0) < ((alpha >> 2) + 2));
            bool ap = Math.Abs(p2 - p0) < beta;
            bool aq = Math.Abs(q2 - q0) < beta;

            if (smallGap)
            {
                if (ap)
                {
                    WritePixel(buffer, offset - step, (p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3, pixelShift);
                    WritePixel(buffer, offset - 2 * step, (p2 + p1 + p0 + q0 + 2) >> 2, pixelShift);
                    WritePixel(buffer, offset - 3 * step, (2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3, pixelShift);
                }
                else
                {
                    WritePixel(buffer, offset - step, (2 * p1 + p0 + q1 + 2) >> 2, pixelShift);
                }

                if (aq)
                {
                    WritePixel(buffer, offset, (p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3, pixelShift);
                    WritePixel(buffer, offset + step, (p0 + q0 + q1 + q2 + 2) >> 2, pixelShift);
                    WritePixel(buffer, offset + 2 * step, (p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3, pixelShift);
                }
                else
                {
                    WritePixel(buffer, offset, (2 * q1 + q0 + p1 + 2) >> 2, pixelShift);
                }
            }
            else
            {
                // Weak strong filtering
                WritePixel(buffer, offset - step, (2 * p1 + p0 + q1 + 2) >> 2, pixelShift);
                WritePixel(buffer, offset, (2 * q1 + q0 + p1 + 2) >> 2, pixelShift);
            }
        }

        /// <summary>
        /// Normal luma filtering (bS = 1-3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FilterNormalLuma(
            Span<byte> buffer,
            int offset,
            int step,
            int p0, int p1,
            int q0, int q1,
            int boundaryStrength,
            int indexA,
            int alpha, int beta,
            int pixelShift = 0, int maxPixelValue = 255, int bitDepthScale = 1)
        {
            int tc0 = GetTc0(boundaryStrength, indexA) * bitDepthScale;
            int tc = tc0;

            int p2 = ReadPixel(buffer, offset - 3 * step, pixelShift);
            int q2 = ReadPixel(buffer, offset + 2 * step, pixelShift);

            bool ap = Math.Abs(p2 - p0) < beta;
            bool aq = Math.Abs(q2 - q0) < beta;

            if (ap)
                tc++;
            if (aq)
                tc++;

            // Calculate delta
            int delta = Clip3(-tc, tc, ((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3));

            // Apply delta
            WritePixel(buffer, offset - step, Math.Clamp(p0 + delta, 0, maxPixelValue), pixelShift);
            WritePixel(buffer, offset, Math.Clamp(q0 - delta, 0, maxPixelValue), pixelShift);

            // Optionally modify p1 and q1
            if (ap)
            {
                int deltaP1 = Clip3(-tc0, tc0, (p2 + ((p0 + q0 + 1) >> 1) - 2 * p1) >> 1);
                WritePixel(buffer, offset - 2 * step, Math.Clamp(p1 + deltaP1, 0, maxPixelValue), pixelShift);
            }

            if (aq)
            {
                int deltaQ1 = Clip3(-tc0, tc0, (q2 + ((p0 + q0 + 1) >> 1) - 2 * q1) >> 1);
                WritePixel(buffer, offset + step, Math.Clamp(q1 + deltaQ1, 0, maxPixelValue), pixelShift);
            }
        }

        /// <summary>Per-lane clipping limits for one edge, two lanes to a segment.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> ChromaLimits(
            ReadOnlySpan<int> segmentStrengths, int biasedIndexA, int bitDepthScale)
        {
            short t0 = (short)(Tc0Biased(segmentStrengths[0], biasedIndexA) * bitDepthScale + 1);
            short t1 = (short)(Tc0Biased(segmentStrengths[1], biasedIndexA) * bitDepthScale + 1);
            short t2 = (short)(Tc0Biased(segmentStrengths[2], biasedIndexA) * bitDepthScale + 1);
            short t3 = (short)(Tc0Biased(segmentStrengths[3], biasedIndexA) * bitDepthScale + 1);
            return Vector128.Create(t0, t0, t1, t1, t2, t2, t3, t3);
        }

        /// <summary>
        /// Filters both chroma planes of one edge with the vector kernel, where its strengths
        /// allow it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cb and Cr are two planes of one edge, so they share everything the edge decides:
        /// whether any segment is filtered at all, whether it takes the strength-four filter,
        /// which segments are active - and, when the two planes' quantisers agree, the clipping
        /// limits as well. All of that used to be worked out once per plane.
        /// </para>
        /// <para>
        /// The quantisers agree unless the picture parameter set gives Cb and Cr different
        /// offsets. That is the same case <c>ff_h264_filter_mb_fast</c> declines to handle at
        /// all - it hands the macroblock to the general path on <c>pps-&gt;chroma_qp_diff</c> -
        /// so one quantiser for both planes is the case worth having a fast path for.
        /// </para>
        /// <para>
        /// In practice an edge never carries both four and something else - four requires both
        /// sides intra across a macroblock edge, which is a property of the pair rather than of
        /// one segment - but nothing in the spec says so, and the scalar path handles it.
        /// </para>
        /// </remarks>
        /// <returns>False if the caller must fall back to filtering line by line.</returns>
        private static bool TryFilterChromaEdges(
            Span<byte> chromaCb, Span<byte> chromaCr, int cbOffset, int crOffset,
            int step, int lineStep,
            int alphaCb, int betaCb, int biasedIndexACb,
            int alphaCr, int betaCr, int biasedIndexACr,
            ReadOnlySpan<int> segmentStrengths, int bitDepthScale)
        {
            int fours = 0, zeroes = 0;
            for (int seg = 0; seg < 4; seg++)
            {
                if (segmentStrengths[seg] == 4) fours++;
                else if (segmentStrengths[seg] == 0) zeroes++;
            }

            // A strength of four selects a different filter, so an edge carrying both four and
            // not-four cannot be done in one pass. Both planes see the same strengths, so this
            // decision - and the all-zero one below - is the same for both.
            if (fours != 0 && fours != 4) return false;
            if (zeroes == 4) return true;

            // Two lines of the edge share a segment, so each strength fills two lanes.
            short a0 = segmentStrengths[0] != 0 ? (short)-1 : (short)0;
            short a1 = segmentStrengths[1] != 0 ? (short)-1 : (short)0;
            short a2 = segmentStrengths[2] != 0 ? (short)-1 : (short)0;
            short a3 = segmentStrengths[3] != 0 ? (short)-1 : (short)0;
            Vector128<short> active = Vector128.Create(a0, a0, a1, a1, a2, a2, a3, a3);

            Vector128<short> limitsCb = ChromaLimits(segmentStrengths, biasedIndexACb, bitDepthScale);
            Vector128<short> limitsCr = biasedIndexACr == biasedIndexACb
                ? limitsCb
                : ChromaLimits(segmentStrengths, biasedIndexACr, bitDepthScale);

            bool strong = fours == 4;
            Dsp.H264DeblockingDsp.FilterChromaEdge8(
                chromaCb, cbOffset, step, lineStep, alphaCb, betaCb, limitsCb, active, strong);
            Dsp.H264DeblockingDsp.FilterChromaEdge8(
                chromaCr, crOffset, step, lineStep, alphaCr, betaCr, limitsCr, active, strong);
            return true;
        }

        private static void FilterEdgeChroma(
            Span<byte> buffer,
            int offset,
            int step,
            int alpha,
            int beta,
            int boundaryStrength,
            int indexA,
            int pixelShift = 0, int maxPixelValue = 255, int bitDepthScale = 1)
        {
            int p0 = ReadPixel(buffer, offset - step, pixelShift);
            int p1 = ReadPixel(buffer, offset - 2 * step, pixelShift);
            int q0 = ReadPixel(buffer, offset, pixelShift);
            int q1 = ReadPixel(buffer, offset + step, pixelShift);

            if (Math.Abs(p0 - q0) >= alpha)
                return;
            if (Math.Abs(p1 - p0) >= beta)
                return;
            if (Math.Abs(q1 - q0) >= beta)
                return;

            if (boundaryStrength == 4)
            {
                // Strong chroma filter (H.264 section 8.7.2.3)
                WritePixel(buffer, offset - step, (2 * p1 + p0 + q1 + 2) >> 2, pixelShift);
                WritePixel(buffer, offset, (2 * q1 + q0 + p1 + 2) >> 2, pixelShift);
            }
            else
            {
                int tc = GetTc0(boundaryStrength, indexA) * bitDepthScale + 1;
                int delta = Clip3(-tc, tc, ((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3));
                WritePixel(buffer, offset - step, Math.Clamp(p0 + delta, 0, maxPixelValue), pixelShift);
                WritePixel(buffer, offset, Math.Clamp(q0 - delta, 0, maxPixelValue), pixelShift);
            }
        }

        /// <summary>
        /// Gets tc0 from boundary strength and indexA (= Clamp(edgeQp + filterOffsetA, 0, 51)).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <remarks>Takes an unbiased 0..51 index, unlike <see cref="Tc0Biased"/>.</remarks>
        internal static int GetTc0(int boundaryStrength, int indexA)
            => Tc0Biased(boundaryStrength, Math.Clamp(indexA, 0, 51) + QpIndexBias);

        /// <summary>
        /// Converts luma QP to chroma QP using H.264 table.
        /// </summary>
        // H.264 Table 8-15: chromaQp from lumaQp (indices 0-51)
        private static ReadOnlySpan<byte> ChromaQpTable => new byte[]
        {
             0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
            31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38,
            39, 39, 39, 39
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ChromaQpFromLumaQp(int qpLuma)
        {
            return ChromaQpTable[Math.Clamp(qpLuma, 0, 51)];
        }

        /// <summary>
        /// Clips value to range [min, max].
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clip3(int min, int max, int value)
        {
            return Math.Max(min, Math.Min(max, value));
        }
        
        // Public accessors for MBAFF special-case filtering in H264Decoder
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetAlpha(int indexA) => AlphaTablePadded[Math.Clamp(indexA, 0, 51) + QpIndexBias];
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetBeta(int indexB) => BetaTablePadded[Math.Clamp(indexB, 0, 51) + QpIndexBias];
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LumaToChromaQp(int lumaQp, int chromaQpOffset)
        {
            return ChromaQpFromLumaQp(Math.Clamp(lumaQp + chromaQpOffset, 0, 51));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FilterEdgeLumaPublic(Span<byte> buffer, int offset, int step,
            int alpha, int beta, int boundaryStrength, int indexA,
            int pixelShift = 0, int maxPixelValue = 255, int bitDepthScale = 1)
        {
            FilterEdgeLuma(buffer, offset, step, alpha, beta, boundaryStrength, indexA,
                pixelShift, maxPixelValue, bitDepthScale);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FilterEdgeChromaPublic(Span<byte> buffer, int offset, int step,
            int alpha, int beta, int boundaryStrength, int indexA,
            int pixelShift = 0, int maxPixelValue = 255, int bitDepthScale = 1)
        {
            FilterEdgeChroma(buffer, offset, step, alpha, beta, boundaryStrength, indexA,
                pixelShift, maxPixelValue, bitDepthScale);
        }
    }
}

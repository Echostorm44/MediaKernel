using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Manages H.264 decoder state including parameter sets and reference frames.
    /// </summary>
    public sealed class H264DecoderContext : IDisposable
    {
        private readonly Dictionary<int, SequenceParameterSet> spsMap = new();
        private readonly Dictionary<int, PictureParameterSet> ppsMap = new();
        private bool disposed;

        /// <summary>Currently active SPS.</summary>
        public SequenceParameterSet? ActiveSps { get; private set; }

        /// <summary>Currently active PPS.</summary>
        public PictureParameterSet? ActivePps { get; private set; }

        /// <summary>Picture width in pixels (after cropping).</summary>
        public int Width => ActiveSps?.DisplayWidth ?? 0;

        /// <summary>Picture height in pixels (after cropping).</summary>
        public int Height => ActiveSps?.DisplayHeight ?? 0;

        /// <summary>Coded picture width in pixels (before cropping).</summary>
        public int CodedWidth => ActiveSps?.CodedWidth ?? 0;

        /// <summary>Coded picture height in pixels (before cropping).</summary>
        public int CodedHeight => ActiveSps?.CodedHeight ?? 0;

        /// <summary>Width in macroblocks.</summary>
        public int WidthInMacroblocks => (ActiveSps?.PictureWidthInMacroblocksMinus1 ?? 0) + 1;

        /// <summary>Height in macroblocks.</summary>
        public int HeightInMacroblocks
        {
            get
            {
                if (ActiveSps == null) return 0;
                int mapUnits = ActiveSps.PictureHeightInMapUnitsMinus1 + 1;
                return ActiveSps.FrameMacroblockOnly ? mapUnits : mapUnits * 2;
            }
        }

        /// <summary>Total number of macroblocks per frame.</summary>
        public int TotalMacroblocks => WidthInMacroblocks * HeightInMacroblocks;

        /// <summary>Profile of the current stream.</summary>
        public H264Profile Profile => ActiveSps?.Profile ?? H264Profile.Baseline;

        /// <summary>Level of the current stream (e.g., 40 = level 4.0).</summary>
        public int Level => ActiveSps?.Level ?? 0;

        /// <summary>Chroma format of the current stream.</summary>
        public ChromaFormat ChromaFormat => ActiveSps?.ChromaFormat ?? ChromaFormat.Chroma420;

        /// <summary>Bit depth for luma samples.</summary>
        public int BitDepthLuma => ActiveSps?.BitDepthLuma ?? 8;

        /// <summary>Bit depth for chroma samples.</summary>
        public int BitDepthChroma => ActiveSps?.BitDepthChroma ?? 8;

        /// <summary>Frame rate from VUI timing info.</summary>
        public double FrameRate => ActiveSps?.FrameRate ?? 0;

        /// <summary>True if using CABAC entropy coding.</summary>
        public bool UseCabac => ActivePps?.EntropyCodingMode ?? false;

        /// <summary>Maximum number of reference frames.</summary>
        public int MaxRefFrames => ActiveSps?.MaxNumRefFrames ?? 1;

        /// <summary>Configuration version - incremented when SPS/PPS changes.</summary>
        public int ConfigurationVersion { get; private set; }

        /// <summary>
        /// Processes a NAL unit, updating context if it's an SPS or PPS.
        /// </summary>
        /// <param name="nalUnit">The NAL unit to process.</param>
        /// <returns>True if this NAL unit changed the decoder configuration.</returns>
        public bool ProcessNalUnit(NalUnit nalUnit)
        {
            switch (nalUnit.Type)
            {
                case NalUnitType.SequenceParameterSet:
                    return ProcessSps(nalUnit.Payload.Span);

                case NalUnitType.PictureParameterSet:
                    return ProcessPps(nalUnit.Payload.Span);

                case NalUnitType.CodedSliceIdr:
                case NalUnitType.CodedSliceNonIdr:
                    // For slice NALs, we may need to activate the correct PPS
                    return ActivatePpsFromSlice(nalUnit.Payload.Span);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Parses and stores an SPS.
        /// </summary>
        private bool ProcessSps(ReadOnlySpan<byte> payload)
        {
            var sps = ParameterSetParser.ParseSps(payload);
            if (sps == null)
                return false;

            int spsId = sps.SequenceParameterSetId;
            bool changed = !spsMap.ContainsKey(spsId) || !SpsEquals(spsMap[spsId], sps);

            spsMap[spsId] = sps;

            if (changed)
            {
                ConfigurationVersion++;
            }

            return changed;
        }

        /// <summary>
        /// Parses and stores a PPS.
        /// </summary>
        private bool ProcessPps(ReadOnlySpan<byte> payload)
        {
            // A stream carrying its parameter sets in band commonly repeats them before every
            // picture: of the Annex-B conformance streams in TestMedia, most that carry a picture
            // set at all carry exactly one per coded picture. Parsing an identical payload again
            // builds a whole new PictureParameterSet - a copy of the payload plus twelve scaling
            // matrix arrays - for every frame, which is per-frame allocation on a path that has
            // nothing new to say.
            //
            // The payload bytes and the sequence set they are read against decide the result
            // completely, so a repeat is recognised here and the set already registered is kept.
            // PpsEquals cannot answer this - it compares four of the thirty-odd fields - and it
            // stays as it is, because it answers a different question: whether the configuration
            // changed in a way a caller has to react to.
            if (MatchesRegisteredPps(payload))
                return false;

            // Get the referenced SPS for extended parsing
            var pps = ParameterSetParser.ParsePps(payload, null);
            if (pps == null)
                return false;

            // Re-parse with SPS if available
            if (spsMap.TryGetValue(pps.SequenceParameterSetId, out var sps))
            {
                pps = ParameterSetParser.ParsePps(payload, sps);
                if (pps == null)
                    return false;
            }
            pps.ParsedAgainstSps = sps;

            int ppsId = pps.PictureParameterSetId;
            bool changed = !ppsMap.ContainsKey(ppsId) || !PpsEquals(ppsMap[ppsId], pps);

            ppsMap[ppsId] = pps;

            if (changed)
            {
                ConfigurationVersion++;
            }

            return changed;
        }

        /// <summary>
        /// Whether this payload is byte-for-byte one already registered, read against the same
        /// sequence set - in which case parsing it again would produce an identical set.
        /// </summary>
        /// <remarks>
        /// Every registered set is checked rather than the one under this payload's id, because
        /// reading the id out would mean unescaping and parsing the payload, which is the work
        /// being avoided. A payload carries its own id, so a match is necessarily the set under
        /// that id, and there are only ever a handful registered.
        /// </remarks>
        private bool MatchesRegisteredPps(ReadOnlySpan<byte> payload)
        {
            foreach (var registered in ppsMap.Values)
            {
                if (registered.RawData is not byte[] prior || !payload.SequenceEqual(prior))
                    continue;

                spsMap.TryGetValue(registered.SequenceParameterSetId, out var spsNow);
                return ReferenceEquals(registered.ParsedAgainstSps, spsNow);
            }
            return false;
        }

        /// <summary>
        /// Activates the PPS referenced by a slice NAL unit.
        /// Called when decoding Annex-B streams where SPS/PPS arrive in-band.
        /// </summary>
        /// <summary>
        /// How much of a slice NAL to unescape when only the PPS id is wanted. Three Exp-Golomb
        /// values precede it - first_mb_in_slice, slice_type, pic_parameter_set_id - and their
        /// coded maxima come to well under sixteen RBSP bytes, so thirty-two input bytes are ample
        /// even if several are escape bytes.
        /// </summary>
        private const int PpsIdPeekBytes = 32;

        /// <summary>Activates the PPS named by a slice whose data is already unescaped.</summary>
        internal bool ActivatePpsFromSliceRbsp(ReadOnlySpan<byte> rbsp)
        {
            if (rbsp.Length < 2)
                return false;

            var reader = new BitstreamReader(rbsp);
            try
            {
                reader.ReadExpGolombUnsigned();  // first_mb_in_slice
                reader.ReadExpGolombUnsigned();  // slice_type
                return ActivatePps((int)reader.ReadExpGolombUnsigned());
            }
            catch
            {
                return false;
            }
        }

        internal bool ActivatePpsFromSlice(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 2)
                return false;

            // Peek at the slice header to get PPS ID. Only the start of the NAL needs unescaping:
            // unescaping the whole slice allocated a copy of it every frame to read a few bits.
            // Taking a prefix is safe - whether a byte is an escape depends only on the two before
            // it, so unescaping a prefix yields a prefix of the same RBSP.
            int peekLength = Math.Min(payload.Length, PpsIdPeekBytes);
            byte[] rbsp = NalUnitParser.RemoveEmulationPreventionBytes(payload[..peekLength]);
            var reader = new BitstreamReader(rbsp);

            try
            {
                reader.ReadExpGolombUnsigned();  // first_mb_in_slice
                reader.ReadExpGolombUnsigned();  // slice_type
                uint ppsId = reader.ReadExpGolombUnsigned();

                return ActivatePps((int)ppsId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Activates a PPS by ID, also activating its referenced SPS.
        /// </summary>
        /// <param name="ppsId">The PPS ID to activate.</param>
        /// <returns>True if activation succeeded and changed the configuration.</returns>
        public bool ActivatePps(int ppsId)
        {
            bool foundPps = ppsMap.TryGetValue(ppsId, out var pps);
            SequenceParameterSet? sps = null;
            bool foundSps = foundPps && spsMap.TryGetValue(pps!.SequenceParameterSetId, out sps);
            if (!foundPps || !foundSps)
                return false;

            bool changed = ActivePps != pps || ActiveSps != sps;

            ActivePps = pps;
            ActiveSps = sps;

            return changed;
        }

        /// <summary>
        /// Gets an SPS by ID.
        /// </summary>
        public SequenceParameterSet? GetSps(int spsId)
        {
            return spsMap.TryGetValue(spsId, out var sps) ? sps : null;
        }

        /// <summary>
        /// Gets a PPS by ID.
        /// </summary>
        public PictureParameterSet? GetPps(int ppsId)
        {
            return ppsMap.TryGetValue(ppsId, out var pps) ? pps : null;
        }

        /// <summary>
        /// <c>pps_ref_count</c> from <c>ff_h264_execute_ref_pic_marking</c>: the largest
        /// default active reference count over EVERY parameter set this stream has sent, not
        /// just the active one. The random-access heuristic uses it to decide whether a
        /// picture before an I picture could still be referenced.
        /// </summary>
        public (int L0, int L1) MaxPpsRefCounts()
        {
            int l0 = 0, l1 = 0;
            foreach (PictureParameterSet pps in ppsMap.Values)
            {
                l0 = Math.Max(l0, pps.NumRefIdxL0DefaultActiveMinus1 + 1);
                l1 = Math.Max(l1, pps.NumRefIdxL1DefaultActiveMinus1 + 1);
            }
            return (l0, l1);
        }

        /// <summary>
        /// Parses a slice header from a slice NAL unit.
        /// </summary>
        /// <param name="nalUnit">The slice NAL unit.</param>
        /// <returns>Parsed slice header, or null if parsing fails.</returns>
        /// <summary>Parses a slice header from data the caller has already unescaped.</summary>
        private readonly SliceHeader?[] sliceHeaderScratch = new SliceHeader?[2];
        private int sliceHeaderParity;

        public SliceHeader? ParseSliceHeaderRbsp(NalUnit nalUnit, ReadOnlySpan<byte> rbsp)
        {
            // Always activate PPS from the slice's pps_id — mid-stream PPS updates are stored in
            // ppsMap but not auto-activated. Leaving this out parses later slices against whichever
            // parameter set happened to be active, which is why the streams that switch PPS - the
            // field, MBAFF and weighted-prediction conformance cases - diverged after frame 0.
            ActivatePpsFromSliceRbsp(rbsp);

            if (ActiveSps == null || ActivePps == null)
                return null;

            // Alternated between two instances rather than reusing one. The decoder keeps the
            // header it just parsed - currentSliceHeader lives until the frame is deblocked - so a
            // single instance would be overwritten by the next slice while still in use. Two give
            // the previous slice's header a full slice of life beyond its parse.
            sliceHeaderParity ^= 1;
            var reuse = sliceHeaderScratch[sliceHeaderParity] ??= new SliceHeader();

            return SliceHeaderParser.ParseSliceHeaderRbsp(
                rbsp, nalUnit.Type, nalUnit.ReferenceIdc, ActiveSps, ActivePps, reuse);
        }

        public SliceHeader? ParseSliceHeader(NalUnit nalUnit)
        {
            // Always activate PPS from the slice's pps_id — mid-stream PPS
            // updates are stored in ppsMap but not auto-activated.
            ActivatePpsFromSlice(nalUnit.Payload.Span);

            if (ActiveSps == null || ActivePps == null)
                return null;

            return SliceHeaderParser.ParseSliceHeader(
                nalUnit.Payload.Span,
                nalUnit.Type,
                nalUnit.ReferenceIdc,
                ActiveSps,
                ActivePps);
        }

        /// <summary>
        /// Resets the decoder context, clearing all parameter sets.
        /// </summary>
        public void Reset()
        {
            spsMap.Clear();
            ppsMap.Clear();
            ActiveSps = null;
            ActivePps = null;
            ConfigurationVersion++;
        }

        /// <summary>
        /// Simple equality check for SPS (checks key properties).
        /// </summary>
        private static bool SpsEquals(SequenceParameterSet a, SequenceParameterSet b)
        {
            return a.SequenceParameterSetId == b.SequenceParameterSetId &&
                   a.Profile == b.Profile &&
                   a.Level == b.Level &&
                   a.CodedWidth == b.CodedWidth &&
                   a.CodedHeight == b.CodedHeight &&
                   a.ChromaFormat == b.ChromaFormat &&
                   a.BitDepthLuma == b.BitDepthLuma;
        }

        /// <summary>
        /// Simple equality check for PPS (checks key properties).
        /// </summary>
        private static bool PpsEquals(PictureParameterSet a, PictureParameterSet b)
        {
            return a.PictureParameterSetId == b.PictureParameterSetId &&
                   a.SequenceParameterSetId == b.SequenceParameterSetId &&
                   a.EntropyCodingMode == b.EntropyCodingMode &&
                   a.Transform8x8Mode == b.Transform8x8Mode;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            spsMap.Clear();
            ppsMap.Clear();
            disposed = true;
        }
    }
}

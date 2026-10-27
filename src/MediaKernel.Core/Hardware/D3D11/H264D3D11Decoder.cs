using System;
using System.Collections.Generic;
using MediaKernel.Core.Codecs.H264;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// Reference picture information for decoded picture buffer management.
    /// </summary>
    public struct H264ReferencePicture
    {
        public int SurfaceIndex;
        public int FrameNum;
        public int PicOrderCntTop;
        public int PicOrderCntBottom;
        public bool IsLongTermReference;
        public bool IsUsedForReference;
        public bool TopFieldUsed;
        public bool BottomFieldUsed;
    }

    /// <summary>
    /// Decoded Picture Buffer (DPB) for H.264 hardware decoding.
    /// Manages reference picture tracking and POC calculation.
    /// </summary>
    public sealed class H264DecodedPictureBuffer
    {
        private readonly H264ReferencePicture[] referencePictures;
        private readonly int maxReferencePictures;

        // POC calculation state
        private int previousPicOrderCntMsb;
        private int previousPicOrderCntLsb;
        private int previousFrameNumOffset;
        private int previousFrameNum;

        public H264DecodedPictureBuffer(int maxRefFrames)
        {
            maxReferencePictures = Math.Min(maxRefFrames, 16);
            referencePictures = new H264ReferencePicture[16];

            for (int i = 0; i < 16; i++)
            {
                referencePictures[i].SurfaceIndex = -1;
            }
        }

        /// <summary>
        /// Resets the DPB state (e.g., after an IDR).
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < 16; i++)
            {
                referencePictures[i].IsUsedForReference = false;
                referencePictures[i].SurfaceIndex = -1;
            }

            previousPicOrderCntMsb = 0;
            previousPicOrderCntLsb = 0;
            previousFrameNumOffset = 0;
            previousFrameNum = 0;
        }

        /// <summary>
        /// Calculates Picture Order Count for the current picture.
        /// </summary>
        public void CalculatePoc(
            SequenceParameterSet sps,
            SliceHeader slice,
            bool isIdr,
            out int pocTop,
            out int pocBottom)
        {
            pocTop = 0;
            pocBottom = 0;

            if (sps.PictureOrderCountType == 0)
            {
                int maxPocLsb = 1 << sps.Log2MaxPictureOrderCountLsb;
                int slicePocLsb = (int)slice.PictureOrderCountLsb;

                int picOrderCntMsb;
                if (isIdr)
                {
                    previousPicOrderCntMsb = 0;
                    previousPicOrderCntLsb = 0;
                }

                if (slicePocLsb < previousPicOrderCntLsb &&
                    (previousPicOrderCntLsb - slicePocLsb) >= maxPocLsb / 2)
                {
                    picOrderCntMsb = previousPicOrderCntMsb + maxPocLsb;
                }
                else if (slicePocLsb > previousPicOrderCntLsb &&
                         (slicePocLsb - previousPicOrderCntLsb) > maxPocLsb / 2)
                {
                    picOrderCntMsb = previousPicOrderCntMsb - maxPocLsb;
                }
                else
                {
                    picOrderCntMsb = previousPicOrderCntMsb;
                }

                pocTop = picOrderCntMsb + slicePocLsb;

                if (!slice.FieldPicture)
                {
                    pocBottom = pocTop + slice.DeltaPicOrderCountBottom;
                }
                else
                {
                    pocBottom = pocTop;
                }

                // Update state for next picture
                if (slice.NalReferenceIdc > 0)
                {
                    previousPicOrderCntMsb = picOrderCntMsb;
                    previousPicOrderCntLsb = slicePocLsb;
                }
            }
            else if (sps.PictureOrderCountType == 2)
            {
                // POC type 2: Uses frame_num
                int frameNumOffset;
                int frameNum = slice.FrameNumber;

                if (isIdr)
                {
                    frameNumOffset = 0;
                }
                else if (previousFrameNum > frameNum)
                {
                    frameNumOffset = previousFrameNumOffset + sps.MaxFrameNumber;
                }
                else
                {
                    frameNumOffset = previousFrameNumOffset;
                }

                int tempPoc;
                if (isIdr)
                {
                    tempPoc = 0;
                }
                else if (slice.NalReferenceIdc == 0)
                {
                    tempPoc = 2 * (frameNumOffset + frameNum) - 1;
                }
                else
                {
                    tempPoc = 2 * (frameNumOffset + frameNum);
                }

                pocTop = tempPoc;
                pocBottom = tempPoc;

                previousFrameNumOffset = frameNumOffset;
            }

            previousFrameNum = slice.FrameNumber;
        }

        /// <summary>
        /// Adds a decoded picture to the DPB as a reference.
        /// </summary>
        public void AddReferencePicture(
            int surfaceIndex,
            int frameNum,
            int pocTop,
            int pocBottom,
            bool isLongTerm)
        {
            int slotIndex = -1;

            for (int i = 0; i < maxReferencePictures; i++)
            {
                if (!referencePictures[i].IsUsedForReference)
                {
                    slotIndex = i;
                    break;
                }
            }

            if (slotIndex < 0)
            {
                slotIndex = 0;
            }

            referencePictures[slotIndex] = new H264ReferencePicture
            {
                SurfaceIndex = surfaceIndex,
                FrameNum = frameNum,
                PicOrderCntTop = pocTop,
                PicOrderCntBottom = pocBottom,
                IsLongTermReference = isLongTerm,
                IsUsedForReference = true,
                TopFieldUsed = true,
                BottomFieldUsed = true,
            };
        }

        /// <summary>
        /// Gets the current reference pictures for DXVA parameter filling.
        /// </summary>
        public IEnumerable<H264ReferencePicture> GetReferencePictures()
        {
            for (int i = 0; i < maxReferencePictures; i++)
            {
                if (referencePictures[i].IsUsedForReference)
                {
                    yield return referencePictures[i];
                }
            }
        }

        /// <summary>
        /// Gets the number of current reference pictures.
        /// </summary>
        public int ReferenceCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < maxReferencePictures; i++)
                {
                    if (referencePictures[i].IsUsedForReference)
                        count++;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// H.264 hardware decoder using D3D11VA.
    /// Bridges parsed NAL units to DXVA picture parameters.
    /// </summary>
    public sealed class H264D3D11Decoder : IDisposable
    {
        private readonly D3D11VideoDecoder decoder;
        private readonly H264DecoderContext h264Context;
        private readonly H264DecodedPictureBuffer dpb;

        private bool isInitialized;
        private bool isDisposed;

        public H264D3D11Decoder(D3D11VideoDecoder videoDecoder)
        {
            decoder = videoDecoder ?? throw new ArgumentNullException(nameof(videoDecoder));
            h264Context = new H264DecoderContext();
            dpb = new H264DecodedPictureBuffer(16);
        }

        public bool IsInitialized => isInitialized;

        /// <summary>
        /// Initializes the decoder with codec configuration data (avcC box or SPS/PPS NALs).
        /// </summary>
        public bool Initialize(ReadOnlySpan<byte> codecPrivateData)
        {
            if (isInitialized) return true;

            if (codecPrivateData.Length >= 7 && codecPrivateData[0] == 1)
            {
                if (!ParseAvcC(codecPrivateData))
                    return false;
            }
            else
            {
                if (!ParseAnnexBInit(codecPrivateData))
                    return false;
            }

            if (h264Context.ActiveSps is null)
            {
                return false;
            }

            isInitialized = true;
            return true;
        }

        private bool ParseAvcC(ReadOnlySpan<byte> data)
        {
            if (data.Length < 7) return false;

            int numSps = data[5] & 0x1F;
            int offset = 6;

            for (int i = 0; i < numSps && offset + 2 <= data.Length; i++)
            {
                int spsLength = (data[offset] << 8) | data[offset + 1];
                offset += 2;

                if (offset + spsLength > data.Length) return false;

                var spsData = data.Slice(offset, spsLength);
                var spsNal = new NalUnit(NalUnitType.SequenceParameterSet, 3, 
                    spsData.ToArray(), spsData.ToArray(), 0);
                h264Context.ProcessNalUnit(spsNal);
                offset += spsLength;
            }

            if (offset >= data.Length) return false;

            int numPps = data[offset] & 0xFF;
            offset++;

            for (int i = 0; i < numPps && offset + 2 <= data.Length; i++)
            {
                int ppsLength = (data[offset] << 8) | data[offset + 1];
                offset += 2;

                if (offset + ppsLength > data.Length) return false;

                var ppsData = data.Slice(offset, ppsLength);
                var ppsNal = new NalUnit(NalUnitType.PictureParameterSet, 3, 
                    ppsData.ToArray(), ppsData.ToArray(), 0);
                h264Context.ProcessNalUnit(ppsNal);
                offset += ppsLength;
            }

            return h264Context.ActiveSps is not null;
        }

        private bool ParseAnnexBInit(ReadOnlySpan<byte> data)
        {
            var nalUnits = NalUnitParser.ParseAnnexB(data.ToArray());
            foreach (var nal in nalUnits)
            {
                h264Context.ProcessNalUnit(nal);
            }

            return h264Context.ActiveSps is not null;
        }

        /// <summary>
        /// Decodes a single access unit (one or more NAL units forming a picture).
        /// </summary>
        public D3D11DecodeResult DecodeAccessUnit(ReadOnlySpan<byte> accessUnitData, long timestamp)
        {
            if (!isInitialized)
            {
                return D3D11DecodeResult.Failure(D3D11Interop.EFail);
            }

            var nalUnits = NalUnitParser.ParseAnnexB(accessUnitData.ToArray());

            SliceHeader? firstSlice = null;
            bool isIdr = false;

            foreach (var nal in nalUnits)
            {
                switch (nal.Type)
                {
                    case NalUnitType.SequenceParameterSet:
                        h264Context.ProcessNalUnit(nal);
                        break;

                    case NalUnitType.PictureParameterSet:
                        h264Context.ProcessNalUnit(nal);
                        break;

                    case NalUnitType.CodedSliceIdr:
                        isIdr = true;
                        firstSlice ??= h264Context.ParseSliceHeader(nal);
                        break;

                    case NalUnitType.CodedSliceNonIdr:
                        firstSlice ??= h264Context.ParseSliceHeader(nal);
                        break;
                }
            }

            if (firstSlice is null || h264Context.ActiveSps is null || h264Context.ActivePps is null)
            {
                return D3D11DecodeResult.Failure(D3D11Interop.EInvalidArg);
            }

            var sps = h264Context.ActiveSps;
            var pps = h264Context.ActivePps;
            var slice = firstSlice;

            if (isIdr)
            {
                dpb.Reset();
            }

            dpb.CalculatePoc(sps, slice, isIdr, out int pocTop, out int pocBottom);

            var pictureParams = new DxvaH264PictureParameters();
            DxvaH264ParameterBuilder.InitializePictureParameters(ref pictureParams);
            DxvaH264ParameterBuilder.SetFrameDimensions(
                ref pictureParams,
                h264Context.WidthInMacroblocks,
                h264Context.HeightInMacroblocks);

            pictureParams.NumRefFrames = (byte)sps.MaxNumRefFrames;
            pictureParams.Log2MaxFrameNumMinus4 = sps.Log2MaxFrameNumber - 4;
            pictureParams.PicOrderCntBitDepthMinus4 = sps.Log2MaxPictureOrderCountLsb - 4;
            pictureParams.Transform8x8ModeFlag = (byte)(pps.Transform8x8Mode ? 1 : 0);
            pictureParams.MbaffFrameFlag = (byte)(sps.MbAdaptiveFrameField && !slice.FieldPicture ? 1 : 0);
            pictureParams.FieldPicFlag = (byte)(slice.FieldPicture ? 1 : 0);
            pictureParams.ConstrainedIntraPredFlag = (byte)(pps.ConstrainedIntraPrediction ? 1 : 0);
            pictureParams.WeightedPredFlag = (byte)(pps.WeightedPrediction ? 1 : 0);
            pictureParams.WeightedBipredIdc = (byte)pps.WeightedBiprediction;
            pictureParams.IntraPicFlag = (byte)(slice.Type == SliceType.ISlice || slice.Type == SliceType.SiSlice || slice.Type == SliceType.ISliceAll || slice.Type == SliceType.SiSliceAll ? 1 : 0);

            pictureParams.CurrFieldOrderCnt0 = pocTop;
            pictureParams.CurrFieldOrderCnt1 = pocBottom;

            int refIndex = 0;
            foreach (var refPic in dpb.GetReferencePictures())
            {
                if (refIndex >= 16) break;

                DxvaH264ParameterBuilder.SetReferencePicture(
                    ref pictureParams,
                    refIndex,
                    (byte)refPic.SurfaceIndex,
                    refPic.PicOrderCntTop,
                    refPic.PicOrderCntBottom,
                    refPic.IsLongTermReference,
                    (short)refPic.FrameNum);

                refIndex++;
            }

            var qmatrix = new DxvaH264QuantizationMatrices();
            qmatrix.SetDefaultFlatMatrices();

            var sliceControl = new DxvaH264SliceShort
            {
                BitstreamDataOffset = 0,
                SliceDataSize = (uint)accessUnitData.Length,
                SliceIndex = 0,
            };

            ReadOnlySpan<byte> picParamsBytes;
            ReadOnlySpan<byte> qmatrixBytes;
            ReadOnlySpan<byte> sliceBytes;

            unsafe
            {
                picParamsBytes = new ReadOnlySpan<byte>(&pictureParams, sizeof(DxvaH264PictureParameters));
                qmatrixBytes = new ReadOnlySpan<byte>(&qmatrix, sizeof(DxvaH264QuantizationMatrices));
                sliceBytes = new ReadOnlySpan<byte>(&sliceControl, sizeof(DxvaH264SliceShort));
            }

            var result = decoder.DecodeFrame(accessUnitData, picParamsBytes, sliceBytes, qmatrixBytes);

            if (result.Success && result.Surface is not null && slice.NalReferenceIdc > 0)
            {
                dpb.AddReferencePicture(
                    result.Surface.Index,
                    slice.FrameNumber,
                    pocTop,
                    pocBottom,
                    isLongTerm: false);
            }

            return result;
        }

        /// <summary>
        /// Flushes the decoder.
        /// </summary>
        public void Flush()
        {
            dpb.Reset();
        }

        /// <summary>
        /// Resets decoder state (for seeking).
        /// </summary>
        public void Reset()
        {
            dpb.Reset();
            h264Context.Reset();
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            h264Context.Dispose();
            decoder.Dispose();
        }
    }
}
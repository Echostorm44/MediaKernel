using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// DXVA2 picture parameters for H.264/AVC decoding.
    /// Matches the DXVA_PicParams_H264 structure from dxva.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct DxvaH264PictureParameters
    {
        // Surface dimensions
        public ushort FrameWidthInMbs;
        public ushort FrameHeightInMbs;

        // Current picture
        public DxvaH264PicEntry CurrentPicture;

        // Reference pictures
        public DxvaH264PicEntry RefPic0;
        public DxvaH264PicEntry RefPic1;
        public DxvaH264PicEntry RefPic2;
        public DxvaH264PicEntry RefPic3;
        public DxvaH264PicEntry RefPic4;
        public DxvaH264PicEntry RefPic5;
        public DxvaH264PicEntry RefPic6;
        public DxvaH264PicEntry RefPic7;
        public DxvaH264PicEntry RefPic8;
        public DxvaH264PicEntry RefPic9;
        public DxvaH264PicEntry RefPic10;
        public DxvaH264PicEntry RefPic11;
        public DxvaH264PicEntry RefPic12;
        public DxvaH264PicEntry RefPic13;
        public DxvaH264PicEntry RefPic14;
        public DxvaH264PicEntry RefPic15;

        // Picture coding parameters (from PPS)
        public byte NumRefFrames;
        public byte Bitfields1;
        public byte Bitfields2;
        public byte Reserved8Bits;

        // Calculated POC values
        public int CurrFieldOrderCnt0;
        public int CurrFieldOrderCnt1;

        // POC for reference pictures (pairs per reference)
        public fixed int FieldOrderCntList[32]; // [16][2]

        // Picture order count
        public int PicOrderCntBitDepthMinus4;
        public int Log2MaxFrameNumMinus4;

        // Frame number
        public byte NumSliceGroupsMinus1;
        public byte SliceGroupMapType;
        public byte DeblockingFilterControlPresentFlag;
        public byte RedundantPicCntPresentFlag;
        public byte Reserved8Bits2;

        // Picture and slice parameters
        public ushort SliceGroupChangeRate;

        public fixed short FrameNumList[16];

        public uint UsedForReferenceFlags;
        public ushort NonExistingFrameFlags;

        public ushort FrameNumWrapAroundShift;

        // Weighted prediction
        public byte WeightedPredFlag;
        public byte WeightedBipredIdc;

        // Transform and quantization
        public byte Transform8x8ModeFlag;
        public byte MbaffFrameFlag;
        public byte FieldPicFlag;
        public byte ConstrainedIntraPredFlag;
        public byte IntraPicFlag;
        public byte CurrPicIndex;

        public fixed sbyte PicInitQpMinus26[2];
        public fixed sbyte ChromaQpIndexOffset[2];
        public fixed sbyte SecondChromaQpIndexOffset[2];

        public byte ContinuationFlag;

        // List 0 references
        public fixed byte RefPicList0[16];
        // List 1 references
        public fixed byte RefPicList1[16];

        public ushort Reserved16Bits;

        public short StatusReportFeedbackNumber;

        /// <summary>
        /// Gets a reference to the RefPicList array for indexed access.
        /// </summary>
        public Span<DxvaH264PicEntry> GetRefPicList()
        {
            fixed (DxvaH264PicEntry* ptr = &RefPic0)
            {
                return new Span<DxvaH264PicEntry>(ptr, 16);
            }
        }
    }

    /// <summary>
    /// Reference picture entry for H.264 DXVA decoding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DxvaH264PicEntry
    {
        private byte value;

        /// <summary>
        /// Surface index (0-127). Value of 255 means unused.
        /// </summary>
        public byte Index
        {
            readonly get => (byte)(value & 0x7F);
            set => this.value = (byte)((this.value & 0x80) | (value & 0x7F));
        }

        /// <summary>
        /// Associated flag (top field/bottom field/frame).
        /// </summary>
        public bool AssociatedFlag
        {
            readonly get => (value & 0x80) != 0;
            set => this.value = (byte)((this.value & 0x7F) | (value ? 0x80 : 0));
        }

        /// <summary>
        /// Marks the entry as unused (Index = 255).
        /// </summary>
        public static DxvaH264PicEntry Unused => new() { value = 255 };

        public DxvaH264PicEntry(byte index, bool associatedFlag = false)
        {
            value = (byte)((index & 0x7F) | (associatedFlag ? 0x80 : 0));
        }
    }

    /// <summary>
    /// DXVA2 quantization matrices for H.264 decoding.
    /// Matches the DXVA_Qmatrix_H264 structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct DxvaH264QuantizationMatrices
    {
        // 6 4x4 scaling lists, each with 16 elements
        public fixed byte ScalingList4x4_0[16];
        public fixed byte ScalingList4x4_1[16];
        public fixed byte ScalingList4x4_2[16];
        public fixed byte ScalingList4x4_3[16];
        public fixed byte ScalingList4x4_4[16];
        public fixed byte ScalingList4x4_5[16];

        // 6 8x8 scaling lists (only 2 are used for H.264), each with 64 elements
        public fixed byte ScalingList8x8_0[64];
        public fixed byte ScalingList8x8_1[64];
        public fixed byte ScalingList8x8_2[64];
        public fixed byte ScalingList8x8_3[64];
        public fixed byte ScalingList8x8_4[64];
        public fixed byte ScalingList8x8_5[64];

        /// <summary>
        /// Sets default flat scaling lists (all 16 for 4x4, all 16 for 8x8).
        /// </summary>
        public void SetDefaultFlatMatrices()
        {
            fixed (byte* list4x4 = ScalingList4x4_0)
            {
                for (int i = 0; i < 6 * 16; i++)
                    list4x4[i] = 16;
            }

            fixed (byte* list8x8 = ScalingList8x8_0)
            {
                for (int i = 0; i < 6 * 64; i++)
                    list8x8[i] = 16;
            }
        }
    }

    /// <summary>
    /// DXVA2 slice control data for H.264 long slice format.
    /// Matches DXVA_Slice_H264_Long structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DxvaH264SliceLong
    {
        /// <summary>
        /// Offset in the bitstream buffer where this slice begins.
        /// </summary>
        public uint BitstreamDataOffset;

        /// <summary>
        /// Size of the slice data in bytes.
        /// </summary>
        public uint SliceDataSize;

        /// <summary>
        /// MB address of the first MB in the slice.
        /// </summary>
        public ushort FirstMbAddress;

        /// <summary>
        /// Number of MBs in this slice.
        /// </summary>
        public ushort NumMbsInSlice;

        /// <summary>
        /// Offset to skip start code and slice header to get to slice data.
        /// </summary>
        public ushort SliceHeaderBitsInBuffer;

        /// <summary>
        /// Slice type (I, P, B).
        /// </summary>
        public ushort SliceType;

        /// <summary>
        /// Luma log2 weight denom.
        /// </summary>
        public byte LumaLog2WeightDenom;

        /// <summary>
        /// Chroma log2 weight denom.
        /// </summary>
        public byte ChromaLog2WeightDenom;

        /// <summary>
        /// Number of L0 references.
        /// </summary>
        public byte NumRefIdxL0ActiveMinus1;

        /// <summary>
        /// Number of L1 references.
        /// </summary>
        public byte NumRefIdxL1ActiveMinus1;

        /// <summary>
        /// Slice QP delta.
        /// </summary>
        public sbyte SliceQpDelta;

        /// <summary>
        /// Slice Qs delta for SP/SI.
        /// </summary>
        public sbyte SliceQsDelta;

        /// <summary>
        /// Disable deblocking filter IDC.
        /// </summary>
        public byte DisableDeblockingFilterIdc;

        /// <summary>
        /// Slice alpha C0 offset div 2.
        /// </summary>
        public sbyte SliceAlphaC0OffsetDiv2;

        /// <summary>
        /// Slice beta offset div 2.
        /// </summary>
        public sbyte SliceBetaOffsetDiv2;

        /// <summary>
        /// Reserved padding.
        /// </summary>
        public byte Reserved8Bits;

        // Reference picture lists follow in extended formats
    }

    /// <summary>
    /// DXVA2 slice control data for H.264 short slice format.
    /// Matches DXVA_Slice_H264_Short structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DxvaH264SliceShort
    {
        /// <summary>
        /// Offset in the bitstream buffer where this slice begins.
        /// </summary>
        public uint BitstreamDataOffset;

        /// <summary>
        /// Size of the slice data in bytes.
        /// </summary>
        public uint SliceDataSize;

        /// <summary>
        /// Reserved/slice-index for multi-slice frames.
        /// </summary>
        public ushort SliceIndex;

        /// <summary>
        /// Reserved padding.
        /// </summary>
        public ushort Reserved;
    }

    /// <summary>
    /// Builder for constructing H.264 DXVA picture parameters from parsed SPS/PPS and slice header.
    /// </summary>
    public static class DxvaH264ParameterBuilder
    {
        /// <summary>
        /// Initializes picture parameters with default unused reference list.
        /// </summary>
        public static void InitializePictureParameters(ref DxvaH264PictureParameters parameters)
        {
            // Initialize all reference pictures to unused
            var refList = parameters.GetRefPicList();
            for (int i = 0; i < 16; i++)
            {
                refList[i] = DxvaH264PicEntry.Unused;
            }

            // Clear field order count list
            unsafe
            {
                for (int i = 0; i < 32; i++)
                {
                    parameters.FieldOrderCntList[i] = 0;
                }
            }
        }

        /// <summary>
        /// Sets the frame dimensions from SPS.
        /// </summary>
        public static void SetFrameDimensions(
            ref DxvaH264PictureParameters parameters,
            int widthInMbs,
            int heightInMbs)
        {
            parameters.FrameWidthInMbs = (ushort)widthInMbs;
            parameters.FrameHeightInMbs = (ushort)heightInMbs;
        }

        /// <summary>
        /// Sets the current picture entry.
        /// </summary>
        public static void SetCurrentPicture(
            ref DxvaH264PictureParameters parameters,
            byte surfaceIndex,
            bool isBottomField = false)
        {
            parameters.CurrentPicture = new DxvaH264PicEntry(surfaceIndex, isBottomField);
            parameters.CurrPicIndex = surfaceIndex;
        }

        /// <summary>
        /// Adds a reference picture to the reference list.
        /// </summary>
        public static void SetReferencePicture(
            ref DxvaH264PictureParameters parameters,
            int refIndex,
            byte surfaceIndex,
            int poc0,
            int poc1,
            bool isLongTerm,
            short frameNum)
        {
            if (refIndex < 0 || refIndex >= 16) return;

            var refList = parameters.GetRefPicList();
            refList[refIndex] = new DxvaH264PicEntry(surfaceIndex, isLongTerm);

            unsafe
            {
                parameters.FieldOrderCntList[refIndex * 2] = poc0;
                parameters.FieldOrderCntList[refIndex * 2 + 1] = poc1;
                parameters.FrameNumList[refIndex] = frameNum;
            }
        }
    }
}

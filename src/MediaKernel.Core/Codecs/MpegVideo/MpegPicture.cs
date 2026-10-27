// MPEG-video picture structures - a translation of ffmpeg-master/libavcodec/mpegpicture.h.
//
// This is the first file in the port where the translation cannot be mechanical, because three C
// idioms here have no C# equivalent. Each is handled the same way and recorded in
// the port checklist, so none of it is a silent reinterpretation:
//
//  1. A POINTER INTO AN ALLOCATION. FFmpeg allocates qscale_table_base and then sets
//     qscale_table to point some way into it, so that indexing with a negative macroblock
//     coordinate - the row above the first - lands inside the allocation instead of before it.
//     C# cannot hold an interior pointer in a field, so each pair becomes the array plus an
//     offset, and callers index base[offset + i]. The offset is the whole point and must not be
//     optimised away.
//
//  2. A UNION. ScratchpadContext overlaps scratchpad_buf with rd_scratchpad; the second is the
//     encoder's name for the same memory. Only the decoder is in scope, so the field keeps the
//     decoder's meaning and the encoder alias is recorded as diverged rather than modelled.
//
//  3. REFCOUNTED POOLS. BufferPoolContext holds five AVRefStructPool pointers, which is
//     libavutil's object pool for the per-picture tables. This project already has
//     FrameBufferPool<T> and uses it for exactly this - H.264 rents its colocated motion vector
//     tables through FrameBufferPool<short>, which is the direct counterpart of motion_val_pool -
//     so the pools become rents from it. Translating libavutil's allocator instead would be
//     porting memory machinery rather than a codec, and would fight the project's rule that
//     nothing allocates per frame.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Picture-level constants from <c>mpegpicture.h</c>.</summary>
public static class MpegPicture
{
    /// <summary><c>MPV_MAX_PLANES</c>.</summary>
    public const int MaxPlanes = 3;

    /// <summary>
    /// <c>EDGE_WIDTH</c> - how far a picture's allocation extends past each edge, so that motion
    /// vectors pointing outside the frame read replicated samples instead of running off the
    /// buffer.
    /// </summary>
    public const int EdgeWidth = 16;
}

/// <summary>
/// <c>ScratchpadContext</c> (<c>mpegpicture.h:34</c>) - per-thread scratch buffers.
/// </summary>
/// <remarks>
/// FFmpeg allocates one <c>scratchpad_buf</c> and points the others into it, so the offsets here
/// replace those interior pointers. <c>rd_scratchpad</c>, the encoder's union alias of the same
/// buffer, is deliberately absent.
/// </remarks>
public struct ScratchpadContext
{
    /// <summary>The single allocation the offsets below index into (<c>scratchpad_buf</c>).</summary>
    public byte[]? ScratchpadBuf;

    /// <summary>
    /// <c>edge_emu_buffer</c> - where a motion vector pointing outside the frame has its
    /// replicated block built, as an offset into <see cref="ScratchpadBuf"/>.
    /// </summary>
    public int EdgeEmuBufferOffset;

    /// <summary><c>obmc_scratchpad</c>, as an offset into <see cref="ScratchpadBuf"/>.</summary>
    public int ObmcScratchpadOffset;

    /// <summary>
    /// <c>linesize</c> - the line size the buffers were sized for. A picture whose line size
    /// differs needs them reallocated, which is the only reason this is stored.
    /// </summary>
    public int Linesize;
}

/// <summary>
/// <c>BufferPoolContext</c> (<c>mpegpicture.h:44</c>) - the dimensions the per-picture tables were
/// allocated for.
/// </summary>
/// <remarks>
/// FFmpeg's five <c>AVRefStructPool</c> pointers are not mirrored; the tables are rented from
/// <c>FrameBufferPool&lt;T&gt;</c> instead, as H.264 and HEVC already do. What remains is the part
/// that carries meaning rather than mechanism: the dimensions the current tables were sized for,
/// which is what tells the decoder a resolution change has invalidated them.
/// </remarks>
public struct BufferPoolContext
{
    /// <summary><c>alloc_mb_width</c>.</summary>
    public int AllocMbWidth;

    /// <summary><c>alloc_mb_height</c>.</summary>
    public int AllocMbHeight;

    /// <summary><c>alloc_mb_stride</c>.</summary>
    public int AllocMbStride;

    /// <summary>
    /// The pictures in flight, standing in for the five <c>AVRefStructPool</c> pointers this
    /// structure holds in FFmpeg. Created on the first allocation.
    /// </summary>
    public MpvPicturePool? Pool;
}

/// <summary>
/// <c>MPVPicture</c> (<c>mpegpicture.h:58</c>) - a decoded picture and the per-macroblock tables
/// decoded alongside it.
/// </summary>
/// <remarks>
/// The tables outlive the picture's own decode because a later picture predicts from them: a
/// B-picture in spatial direct mode reads the colocated picture's motion vectors and reference
/// indices. That is why they hang off the picture and not off the decoder.
/// </remarks>
public struct MPVPicture
{
    /// <summary>
    /// The sample planes (<c>AVFrame *f</c>), as one allocation with a plane offset each, which is
    /// how this project's other decoders hold a picture.
    /// </summary>
    public byte[]? Frame;

    /// <summary>Offset of each plane within <see cref="Frame"/>.</summary>
    public PlaneOffsets Data;

    /// <summary>Line size of each plane.</summary>
    public PlaneStrides Linesize;

    /// <summary>
    /// <c>AV_FRAME_DATA_A53_CC</c> side data - the closed-caption triplets for this picture.
    /// </summary>
    /// <remarks>
    /// Captions belong to the picture and not to the decoder, because pictures come out in
    /// display order: the user data for a B picture is read after that of the reference that
    /// follows it on screen. Holding them anywhere else would deliver them against the wrong
    /// frame. Allocated once per pool slot at the maximum the format allows, so that carrying
    /// captions costs no allocation per picture.
    /// </remarks>
    public byte[]? ClosedCaptions;

    /// <summary>How much of <see cref="ClosedCaptions"/> is live; zero means this picture has none.</summary>
    public int ClosedCaptionsLength;

    /// <summary>
    /// <c>AVFrame.pts</c> - the presentation timestamp of the CODED picture held in this slot.
    /// </summary>
    /// <remarks>
    /// It lives on the picture for the same reason the closed captions do: MPEG codes pictures
    /// out of display order and holds every non-bidirectional one back by a frame, so the
    /// picture handed out by a given decode call is usually not the picture that call decoded.
    /// A timestamp kept anywhere else is attached to the wrong frame - which is what made the
    /// program stream's frames arrive at 760, 793, 926, 860, 893, 1027 ms, each I or P picture
    /// carrying a stamp three slots ahead of its place.
    /// </remarks>
    public long Pts;

    /// <summary><c>qscale_table_base</c> - the allocation.</summary>
    public sbyte[]? QscaleTableBase;

    /// <summary><c>qscale_table</c> - as an offset into <see cref="QscaleTableBase"/>.</summary>
    public int QscaleTableOffset;

    /// <summary><c>motion_val_base[2]</c> - the allocations, forward and backward.</summary>
    public short[]? MotionValBaseL0;

    /// <inheritdoc cref="MotionValBaseL0"/>
    public short[]? MotionValBaseL1;

    /// <summary>
    /// <c>motion_val[2]</c> - as offsets into the allocations above. Each entry is a pair, so an
    /// offset counts shorts and not vectors.
    /// </summary>
    public int MotionValOffsetL0;

    /// <inheritdoc cref="MotionValOffsetL0"/>
    public int MotionValOffsetL1;

    /// <summary><c>mb_type_base</c> - the allocation.</summary>
    public uint[]? MbTypeBase;

    /// <summary><c>mb_type</c> - as an offset into <see cref="MbTypeBase"/>.</summary>
    public int MbTypeOffset;

    /// <summary><c>mbskip_table</c>.</summary>
    public byte[]? MbskipTable;

    /// <summary><c>ref_index[2]</c>.</summary>
    public sbyte[]? RefIndexL0;

    /// <inheritdoc cref="RefIndexL0"/>
    public sbyte[]? RefIndexL1;

    /// <summary><c>mb_width</c> of the tables, which may lag the sequence after a size change.</summary>
    public int MbWidth;

    /// <summary><c>mb_height</c> of the tables.</summary>
    public int MbHeight;

    /// <summary><c>mb_stride</c> of the tables.</summary>
    public int MbStride;

    /// <summary>
    /// <c>dummy</c> - a placeholder picture, synthesised when a stream starts on a
    /// non-intra picture so that prediction has something to read. Must not be output.
    /// </summary>
    public int Dummy;

    /// <summary><c>field_picture</c> - whether the picture was coded as two separate fields.</summary>
    public int FieldPicture;

    /// <summary><c>b_frame_score</c>.</summary>
    public int BFrameScore;

    /// <summary><c>reference</c> - non-zero while a later picture may still predict from this one.</summary>
    public int Reference;

    /// <summary><c>shared</c>.</summary>
    public int Shared;

    /// <summary><c>display_picture_number</c>.</summary>
    public int DisplayPictureNumber;

    /// <summary><c>coded_picture_number</c>.</summary>
    public int CodedPictureNumber;

    // The five fields below live on AVFrame in FFmpeg (f->pict_type, f->flags and
    // f->repeat_pict). AVFrame is libavutil's container and is not mirrored by this project, so
    // the subset the MPEG decoder writes and reads back is kept on the picture instead. Recorded
    // in the port checklist against ff_mpv_frame_start, which is where they are set.

    /// <summary><c>f->pict_type</c>.</summary>
    public AvPictureType PictType;

    /// <summary><c>AV_FRAME_FLAG_KEY</c>.</summary>
    public int KeyFrame;

    /// <summary><c>AV_FRAME_FLAG_TOP_FIELD_FIRST</c>.</summary>
    public int TopFieldFirst;

    /// <summary><c>AV_FRAME_FLAG_INTERLACED</c>.</summary>
    public int Interlaced;

    /// <summary><c>f->repeat_pict</c> - extra field periods this picture is displayed for.</summary>
    public int RepeatPict;

    /// <summary>Three plane offsets into a single picture allocation.</summary>
    [System.Runtime.CompilerServices.InlineArray(MpegPicture.MaxPlanes)]
    public struct PlaneOffsets
    {
        private int element0;
    }

    /// <summary>Three plane line sizes.</summary>
    [System.Runtime.CompilerServices.InlineArray(MpegPicture.MaxPlanes)]
    public struct PlaneStrides
    {
        private int element0;
    }
}

/// <summary>
/// <c>MPVWorkPicture</c> (<c>mpegpicture.h:95</c>) - the view of a picture the macroblock layer
/// actually reads.
/// </summary>
/// <remarks>
/// It exists because a field picture's planes are not the frame's planes: the same samples are
/// addressed with doubled line sizes and an offset for the second field, so the decoder works
/// through a view with its own <c>data</c> and <c>linesize</c> rather than re-deriving them per
/// macroblock. The table references are borrowed from <see cref="Ptr"/> and are not owned here.
/// </remarks>
public struct MPVWorkPicture
{
    /// <summary>The picture allocation this view reads (<c>ptr</c>'s frame).</summary>
    public byte[]? Frame;

    /// <summary><c>data[MPV_MAX_PLANES]</c>, as offsets into <see cref="Frame"/>.</summary>
    public MPVPicture.PlaneOffsets Data;

    /// <summary><c>linesize[MPV_MAX_PLANES]</c> - doubled for a field picture.</summary>
    public MPVPicture.PlaneStrides Linesize;

    /// <summary>
    /// <c>ptr</c> - the picture this is a view of, or 0 for none. A slot in
    /// <see cref="MpvPicturePool"/> rather than a pointer, so the view stays copyable; slot zero
    /// is reserved so that a zeroed view refers to nothing, as a zeroed C structure does.
    /// </summary>
    public int Ptr;

    /// <summary><c>qscale_table</c> - borrowed.</summary>
    public sbyte[]? QscaleTable;

    /// <summary><c>qscale_table</c>'s offset within its allocation.</summary>
    public int QscaleTableOffset;

    /// <summary><c>motion_val[2]</c> - borrowed.</summary>
    public short[]? MotionValL0;

    /// <inheritdoc cref="MotionValL0"/>
    public short[]? MotionValL1;

    /// <summary>Offsets for the two motion vector tables.</summary>
    public int MotionValOffsetL0;

    /// <inheritdoc cref="MotionValOffsetL0"/>
    public int MotionValOffsetL1;

    /// <summary><c>mb_type</c> - borrowed.</summary>
    public uint[]? MbType;

    /// <summary><c>mb_type</c>'s offset within its allocation.</summary>
    public int MbTypeOffset;

    /// <summary><c>mbskip_table</c> - borrowed.</summary>
    public byte[]? MbskipTable;

    /// <summary><c>ref_index[2]</c> - borrowed.</summary>
    public sbyte[]? RefIndexL0;

    /// <inheritdoc cref="RefIndexL0"/>
    public sbyte[]? RefIndexL1;

    /// <summary><c>reference</c>.</summary>
    public int Reference;
}

/// <summary>
/// The pictures a decoder has in flight, with a reference count each - what
/// <c>AVRefStructPool</c> does for <c>MPVPicture</c> in FFmpeg.
/// </summary>
/// <remarks>
/// <para>
/// DIVERGENCE, recorded: FFmpeg refcounts each picture through libavutil, so a picture is a
/// pointer and "no picture" is a null one. Here a picture is a slot index, and a slot index of
/// zero would be indistinguishable from a default-initialised structure. Slot zero is therefore
/// reserved and never handed out, so a zeroed <see cref="MPVWorkPicture"/> refers to nothing,
/// exactly as a zeroed C structure does.
/// </para>
/// <para>
/// Three pictures are live at once at most - the one being decoded and the two it predicts from
/// - and a fourth may still be held by the caller of the frame just output, so the capacity is
/// deliberately small. Running out means a reference was leaked, which is a bug and is reported
/// as one rather than papered over by growing.
/// </para>
/// </remarks>
public sealed class MpvPicturePool
{
    /// <summary>Slot zero is the null picture; the rest are usable.</summary>
    public const int Capacity = 8;

    private readonly MPVPicture[] pictures = new MPVPicture[Capacity];
    private readonly int[] referenceCount = new int[Capacity];

    /// <summary>The picture in a slot. Slot zero is never a real picture.</summary>
    public ref MPVPicture Picture(int slot) => ref pictures[slot];

    /// <summary>How many work pictures hold the slot.</summary>
    public int ReferenceCount(int slot) => referenceCount[slot];

    /// <summary>
    /// Takes an unused slot and gives it one reference, owned by the caller until it is handed
    /// to a work picture with <see cref="AdoptSlot"/>. The slot keeps whatever buffers it had
    /// last time it was used, so a steady-state decode rents nothing.
    /// </summary>
    public int AllocSlot()
    {
        for (int slot = 1; slot < Capacity; slot++)
        {
            if (referenceCount[slot] != 0) continue;

            referenceCount[slot] = 1;

            // Everything except the buffers is per-picture state and must not survive reuse.
            ref MPVPicture p = ref pictures[slot];
            p.Dummy = 0;
            p.FieldPicture = 0;
            p.Reference = 0;
            p.PictType = AvPictureType.None;
            p.KeyFrame = 0;
            p.TopFieldFirst = 0;
            p.Interlaced = 0;
            p.RepeatPict = 0;

            return slot;
        }

        throw new InvalidOperationException(
            "No free MPEG picture slot. At most three pictures are live at once, so this means "
          + "a reference was taken and never released.");
    }

    /// <summary>Adds a reference to a slot.</summary>
    public void AddReference(int slot)
    {
        if (slot == 0) return;
        referenceCount[slot]++;
    }

    /// <summary>
    /// <c>ff_mpv_unref_picture</c> - drops the work picture's reference and empties it.
    /// </summary>
    public void UnrefPicture(ref MPVWorkPicture wpic)
    {
        if (wpic.Ptr != 0)
        {
            if (referenceCount[wpic.Ptr] <= 0)
                throw new InvalidOperationException(
                    $"MPEG picture slot {wpic.Ptr} was released more times than it was taken.");

            referenceCount[wpic.Ptr]--;
        }

        wpic = default;
    }

    /// <summary><c>set_workpic_from_pic</c> - the view a macroblock layer reads.</summary>
    private void SetWorkpicFromPic(ref MPVWorkPicture wpic, int slot)
    {
        ref MPVPicture pic = ref pictures[slot];

        wpic.Frame = pic.Frame;
        for (int i = 0; i < MpegPicture.MaxPlanes; i++)
        {
            wpic.Data[i] = pic.Data[i];
            wpic.Linesize[i] = pic.Linesize[i];
        }

        wpic.QscaleTable = pic.QscaleTableBase;
        wpic.QscaleTableOffset = pic.QscaleTableOffset;
        wpic.MbType = pic.MbTypeBase;
        wpic.MbTypeOffset = pic.MbTypeOffset;
        wpic.MbskipTable = pic.MbskipTable;

        wpic.MotionValL0 = pic.MotionValBaseL0;
        wpic.MotionValL1 = pic.MotionValBaseL1;
        wpic.MotionValOffsetL0 = pic.MotionValOffsetL0;
        wpic.MotionValOffsetL1 = pic.MotionValOffsetL1;
        wpic.RefIndexL0 = pic.RefIndexL0;
        wpic.RefIndexL1 = pic.RefIndexL1;

        wpic.Reference = pic.Reference;
    }

    /// <summary>
    /// Moves the reference <see cref="AllocSlot"/> returned into a work picture, releasing
    /// whatever that work picture pointed at before. This is the tail of <c>alloc_picture</c>,
    /// where FFmpeg simply assigns the pointer the pool handed it.
    /// </summary>
    public void AdoptSlot(ref MPVWorkPicture wpic, int slot)
    {
        int previous = wpic.Ptr;
        wpic = default;
        if (previous != 0) referenceCount[previous]--;

        wpic.Ptr = slot;
        SetWorkpicFromPic(ref wpic, slot);
    }

    /// <summary>
    /// <c>ff_mpv_workpic_from_pic</c> - points a work picture at a slot, releasing whatever it
    /// pointed at before.
    /// </summary>
    public void WorkpicFromPic(ref MPVWorkPicture wpic, int slot)
    {
        // The order matters when wpic already points at this slot: take the new reference before
        // dropping the old one, or the slot is briefly free and could be handed out.
        AddReference(slot);

        int previous = wpic.Ptr;
        wpic = default;
        if (previous != 0) referenceCount[previous]--;

        if (slot == 0) return;

        wpic.Ptr = slot;
        SetWorkpicFromPic(ref wpic, slot);
    }
}

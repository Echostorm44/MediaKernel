// MediaKernel - the RIFF codec tag tables, and the WAVEFORMAT reader every container that
// carries one needs.
//
// A translation of:
//   ffmpeg-master/libavformat/riff.c      - ff_codec_bmp_tags, ff_codec_bmp_tags_unofficial,
//                                           ff_codec_wav_tags, ff_codec_wav_guids
//   ffmpeg-master/libavformat/riffdec.c   - ff_get_wav_header, parse_waveformatex,
//                                           ff_wav_codec_get_id, ff_get_guid,
//                                           ff_codec_guid_get_id
//   ffmpeg-master/libavformat/utils.c     - ff_codec_get_id, ff_toupper4
//
// Nothing in this tree had any of it. AVI hand-listed five WAVE format tags in a switch and
// named everything else "0x<tag>"; its video side handed out the raw four-character code. WAV
// has its own hand-written table. The tables below are read out of riff.c mechanically rather
// than transcribed, with the codec names read out of libavcodec/codec_desc.c, because a
// 459-row table typed by hand is a table with a bug in it. Their order is the C's order and
// must stay that way: ff_codec_get_id returns the first match, and riff.c's own comments say
// so ("must come after s16le in this list").

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// One row of an <c>AVCodecTag</c> table: the codec FFmpeg names, and the tag a container
/// writes for it.
/// </summary>
/// <remarks>
/// The C's <c>enum AVCodecID</c> is a string here, because that is what this tree's codec
/// registry is keyed by and what ffprobe prints. A codec with no decoder still gets its real
/// name, so a track can be listed and skipped rather than decoded as something it is not.
/// </remarks>
public readonly struct CodecTag(string codec, uint tag)
{
    public readonly string Codec = codec;
    public readonly uint Tag = tag;
}

/// <summary>One row of an <c>AVCodecGuid</c> table.</summary>
public readonly struct CodecGuid(string codec, byte[] guid)
{
    public readonly string Codec = codec;
    public readonly byte[] Guid = guid;
}

/// <summary>The RIFF tag tables and the lookups over them.</summary>
public static class RiffTags
{
    /// <summary>
    /// <c>MKTAG</c>: four bytes packed little-endian, which is the order a RIFF file writes
    /// them in.
    /// </summary>
    public static uint MkTag(int a, int b, int c, int d)
        => (uint)(a | (b << 8) | (c << 16) | (d << 24));

    /// <summary>A translation of <c>ff_codec_bmp_tags</c>, in the C's own order.</summary>
    public static readonly CodecTag[] CodecBmpTags =
    [
        new("h264", MkTag('H', '2', '6', '4')),
        new("h264", MkTag('h', '2', '6', '4')),
        new("h264", MkTag('X', '2', '6', '4')),
        new("h264", MkTag('x', '2', '6', '4')),
        new("h264", MkTag('a', 'v', 'c', '1')),
        new("h264", MkTag('D', 'A', 'V', 'C')),
        new("h264", MkTag('S', 'M', 'V', '2')),
        new("h264", MkTag('V', 'S', 'S', 'H')),
        new("h264", MkTag('Q', '2', '6', '4')),  // QNAP surveillance system
        new("h264", MkTag('V', '2', '6', '4')),  // CCTV recordings
        new("h264", MkTag('G', 'A', 'V', 'C')),  // GeoVision camera
        new("h264", MkTag('U', 'M', 'S', 'V')),
        new("h264", MkTag('t', 's', 'h', 'd')),
        new("h264", MkTag('I', 'N', 'M', 'C')),
        new("h263", MkTag('H', '2', '6', '3')),
        new("h263", MkTag('X', '2', '6', '3')),
        new("h263", MkTag('T', '2', '6', '3')),
        new("h263", MkTag('L', '2', '6', '3')),
        new("h263", MkTag('V', 'X', '1', 'K')),
        new("h263", MkTag('Z', 'y', 'G', 'o')),
        new("h263", MkTag('M', '2', '6', '3')),
        new("h263", MkTag('l', 's', 'v', 'm')),
        new("h263p", MkTag('H', '2', '6', '3')),
        new("h263i", MkTag('I', '2', '6', '3')),  // Intel H.263
        new("h261", MkTag('H', '2', '6', '1')),
        new("h263", MkTag('U', '2', '6', '3')),
        new("h263", MkTag('V', 'S', 'M', '4')),  // needs -vf il=l=i:c=i
        new("mpeg4", MkTag('F', 'M', 'P', '4')),
        new("mpeg4", MkTag('D', 'I', 'V', 'X')),
        new("mpeg4", MkTag('D', 'X', '5', '0')),
        new("mpeg4", MkTag('X', 'V', 'I', 'D')),
        new("mpeg4", MkTag('M', 'P', '4', 'S')),
        new("mpeg4", MkTag('M', '4', 'S', '2')),
        new("mpeg4", MkTag(4, 0, 0, 0)),
        new("mpeg4", MkTag('Z', 'M', 'P', '4')),
        new("mpeg4", MkTag('D', 'I', 'V', '1')),
        new("mpeg4", MkTag('B', 'L', 'Z', '0')),
        new("mpeg4", MkTag('m', 'p', '4', 'v')),
        new("mpeg4", MkTag('U', 'M', 'P', '4')),
        new("mpeg4", MkTag('W', 'V', '1', 'F')),
        new("mpeg4", MkTag('S', 'E', 'D', 'G')),
        new("mpeg4", MkTag('R', 'M', 'P', '4')),
        new("mpeg4", MkTag('3', 'I', 'V', '2')),
        new("mpeg4", MkTag('W', 'A', 'W', 'V')),
        new("mpeg4", MkTag('F', 'F', 'D', 'S')),
        new("mpeg4", MkTag('F', 'V', 'F', 'W')),
        new("mpeg4", MkTag('D', 'C', 'O', 'D')),
        new("mpeg4", MkTag('M', 'V', 'X', 'M')),
        new("mpeg4", MkTag('P', 'M', '4', 'V')),
        new("mpeg4", MkTag('S', 'M', 'P', '4')),
        new("mpeg4", MkTag('D', 'X', 'G', 'M')),
        new("mpeg4", MkTag('V', 'I', 'D', 'M')),
        new("mpeg4", MkTag('M', '4', 'T', '3')),
        new("mpeg4", MkTag('G', 'E', 'O', 'X')),
        new("mpeg4", MkTag('G', '2', '6', '4')),
        new("mpeg4", MkTag('H', 'D', 'X', '4')),
        new("mpeg4", MkTag('D', 'M', '4', 'V')),
        new("mpeg4", MkTag('D', 'M', 'K', '2')),
        new("mpeg4", MkTag('D', 'Y', 'M', '4')),
        new("mpeg4", MkTag('D', 'I', 'G', 'I')),
        new("mpeg4", MkTag('E', 'P', 'H', 'V')),
        new("mpeg4", MkTag('E', 'M', '4', 'A')),
        new("mpeg4", MkTag('M', '4', 'C', 'C')),
        new("mpeg4", MkTag('S', 'N', '4', '0')),
        new("mpeg4", MkTag('V', 'S', 'P', 'X')),
        new("mpeg4", MkTag('U', 'L', 'D', 'X')),
        new("mpeg4", MkTag('G', 'E', 'O', 'V')),
        new("mpeg4", MkTag('S', 'I', 'P', 'P')),
        new("mpeg4", MkTag('S', 'M', '4', 'V')),
        new("mpeg4", MkTag('X', 'V', 'I', 'X')),
        new("mpeg4", MkTag('D', 'r', 'e', 'X')),
        new("mpeg4", MkTag('Q', 'M', 'P', '4')),  // QNAP Systems
        new("mpeg4", MkTag('P', 'L', 'V', '1')),  // Pelco DVR MPEG-4
        new("mpeg4", MkTag('G', 'L', 'V', '4')),
        new("mpeg4", MkTag('G', 'M', 'P', '4')),  // GeoVision camera
        new("mpeg4", MkTag('M', 'N', 'M', '4')),  // March Networks DVR
        new("mpeg4", MkTag('G', 'T', 'M', '4')),  // Telefactor
        new("msmpeg4v3", MkTag('M', 'P', '4', '3')),
        new("msmpeg4v3", MkTag('D', 'I', 'V', '3')),
        new("msmpeg4v3", MkTag('M', 'P', 'G', '3')),
        new("msmpeg4v3", MkTag('D', 'I', 'V', '5')),
        new("msmpeg4v3", MkTag('D', 'I', 'V', '6')),
        new("msmpeg4v3", MkTag('D', 'I', 'V', '4')),
        new("msmpeg4v3", MkTag('D', 'V', 'X', '3')),
        new("msmpeg4v3", MkTag('A', 'P', '4', '1')),
        new("msmpeg4v3", MkTag('C', 'O', 'L', '1')),
        new("msmpeg4v3", MkTag('C', 'O', 'L', '0')),
        new("msmpeg4v2", MkTag('M', 'P', '4', '2')),
        new("msmpeg4v2", MkTag('D', 'I', 'V', '2')),
        new("msmpeg4v1", MkTag('M', 'P', 'G', '4')),
        new("msmpeg4v1", MkTag('M', 'P', '4', '1')),
        new("wmv1", MkTag('W', 'M', 'V', '1')),
        new("wmv2", MkTag('W', 'M', 'V', '2')),
        new("wmv2", MkTag('G', 'X', 'V', 'E')),
        new("dvvideo", MkTag('d', 'v', 's', 'd')),
        new("dvvideo", MkTag('d', 'v', 'h', 'd')),
        new("dvvideo", MkTag('d', 'v', 'h', '1')),
        new("dvvideo", MkTag('d', 'v', 's', 'l')),
        new("dvvideo", MkTag('d', 'v', '2', '5')),
        new("dvvideo", MkTag('d', 'v', '5', '0')),
        new("dvvideo", MkTag('c', 'd', 'v', 'c')),
        new("dvvideo", MkTag('C', 'D', 'V', 'H')),
        new("dvvideo", MkTag('C', 'D', 'V', '5')),
        new("dvvideo", MkTag('d', 'v', 'c', ' ')),
        new("dvvideo", MkTag('d', 'v', 'c', 's')),
        new("dvvideo", MkTag('d', 'v', 'h', '1')),
        new("dvvideo", MkTag('d', 'v', 'i', 's')),
        new("dvvideo", MkTag('p', 'd', 'v', 'c')),
        new("dvvideo", MkTag('S', 'L', '2', '5')),
        new("dvvideo", MkTag('S', 'L', 'D', 'V')),
        new("dvvideo", MkTag('A', 'V', 'd', '1')),
        new("mpeg1video", MkTag('m', 'p', 'g', '1')),
        new("mpeg2video", MkTag('m', 'p', 'g', '2')),
        new("mpeg2video", MkTag('M', 'P', 'E', 'G')),
        new("mpeg1video", MkTag('P', 'I', 'M', '1')),
        new("mpeg2video", MkTag('P', 'I', 'M', '2')),
        new("mpeg2video", MkTag('V', 'C', 'R', '2')),
        new("mpeg1video", MkTag(1, 0, 0, 16)),
        new("mpeg2video", MkTag(2, 0, 0, 16)),
        new("mpeg4", MkTag(4, 0, 0, 16)),
        new("mpeg2video", MkTag('D', 'V', 'R', ' ')),
        new("mpeg2video", MkTag('M', 'M', 'E', 'S')),
        new("mpeg2video", MkTag('L', 'M', 'P', '2')),
        new("mpeg2video", MkTag('s', 'l', 'i', 'f')),
        new("mpeg2video", MkTag('E', 'M', '2', 'V')),
        new("mpeg2video", MkTag('M', '7', '0', '1')),
        new("mpeg2video", MkTag('M', '7', '0', '2')),
        new("mpeg2video", MkTag('M', '7', '0', '3')),
        new("mpeg2video", MkTag('M', '7', '0', '4')),
        new("mpeg2video", MkTag('M', '7', '0', '5')),
        new("mpeg2video", MkTag('m', 'p', 'g', 'v')),
        new("mpeg1video", MkTag('B', 'W', '1', '0')),
        new("mpeg1video", MkTag('X', 'M', 'P', 'G')),  // Xing MPEG intra only
        new("mjpeg", MkTag('M', 'J', 'P', 'G')),
        new("mjpeg", MkTag('M', 'S', 'C', '2')),  // Multiscope II
        new("mjpeg", MkTag('L', 'J', 'P', 'G')),
        new("mjpeg", MkTag('d', 'm', 'b', '1')),
        new("mjpeg", MkTag('m', 'j', 'p', 'a')),
        new("mjpeg", MkTag('J', 'R', '2', '4')),  // Quadrox Mjpeg
        new("ljpeg", MkTag('L', 'J', 'P', 'G')),
        new("mjpeg", MkTag('J', 'P', 'G', 'L')),
        new("jpegls", MkTag('M', 'J', 'L', 'S')),
        new("jpegls", MkTag('M', 'J', 'P', 'G')),
        new("mjpeg", MkTag('M', 'J', 'L', 'S')),
        new("mjpeg", MkTag('j', 'p', 'e', 'g')),
        new("mjpeg", MkTag('I', 'J', 'P', 'G')),
        new("avrn", MkTag('A', 'V', 'R', 'n')),
        new("mjpeg", MkTag('A', 'C', 'D', 'V')),
        new("mjpeg", MkTag('Q', 'I', 'V', 'G')),
        new("mjpeg", MkTag('S', 'L', 'M', 'J')),
        new("mjpeg", MkTag('C', 'J', 'P', 'G')),
        new("mjpeg", MkTag('I', 'J', 'L', 'V')),
        new("mjpeg", MkTag('M', 'V', 'J', 'P')),
        new("mjpeg", MkTag('A', 'V', 'I', '1')),
        new("mjpeg", MkTag('A', 'V', 'I', '2')),
        new("mjpeg", MkTag('M', 'T', 'S', 'J')),
        new("mjpeg", MkTag('Z', 'J', 'P', 'G')),
        new("mjpeg", MkTag('M', 'M', 'J', 'P')),
        new("huffyuv", MkTag('H', 'F', 'Y', 'U')),
        new("ffvhuff", MkTag('F', 'F', 'V', 'H')),
        new("cyuv", MkTag('C', 'Y', 'U', 'V')),
        new("rawvideo", MkTag(0, 0, 0, 0)),
        new("rawvideo", MkTag(3, 0, 0, 0)),
        new("rawvideo", MkTag('I', '4', '2', '0')),
        new("rawvideo", MkTag('Y', 'U', 'Y', '2')),
        new("rawvideo", MkTag('Y', '2', '1', '0')),
        new("rawvideo", MkTag('Y', '2', '1', '6')),
        new("rawvideo", MkTag('Y', '4', '1', '6')),
        new("rawvideo", MkTag('Y', '4', '2', '2')),
        new("rawvideo", MkTag('V', '4', '2', '2')),
        new("rawvideo", MkTag('Y', '4', '1', '0')),
        new("rawvideo", MkTag('Y', 'U', 'N', 'V')),
        new("rawvideo", MkTag('U', 'Y', 'N', 'V')),
        new("rawvideo", MkTag('U', 'Y', 'N', 'Y')),
        new("rawvideo", MkTag('u', 'y', 'v', '1')),
        new("rawvideo", MkTag('2', 'V', 'u', '1')),
        new("rawvideo", MkTag('2', 'v', 'u', 'y')),
        new("rawvideo", MkTag('y', 'u', 'v', 's')),
        new("rawvideo", MkTag('y', 'u', 'v', '2')),
        new("rawvideo", MkTag('P', '4', '2', '2')),
        new("rawvideo", MkTag('Y', 'V', '1', '2')),
        new("rawvideo", MkTag('Y', 'V', '1', '6')),
        new("rawvideo", MkTag('Y', 'V', '2', '4')),
        new("rawvideo", MkTag('U', 'Y', 'V', 'Y')),
        new("rawvideo", MkTag('V', 'Y', 'U', 'Y')),
        new("rawvideo", MkTag('I', 'Y', 'U', 'V')),
        new("rawvideo", MkTag('A', 'Y', 'U', 'V')),
        new("rawvideo", MkTag('Y', '8', '0', '0')),
        new("rawvideo", MkTag('Y', '8', ' ', ' ')),
        new("rawvideo", MkTag('H', 'D', 'Y', 'C')),
        new("rawvideo", MkTag('Y', 'V', 'U', '9')),
        new("rawvideo", MkTag('V', 'D', 'T', 'Z')),
        new("rawvideo", MkTag('Y', '4', '1', '1')),
        new("rawvideo", MkTag('N', 'V', '1', '2')),
        new("rawvideo", MkTag('N', 'V', '2', '1')),
        new("rawvideo", MkTag('Y', '4', '1', 'B')),
        new("rawvideo", MkTag('Y', '4', '2', 'B')),
        new("rawvideo", MkTag('Y', 'U', 'V', '9')),
        new("rawvideo", MkTag('Y', 'V', 'U', '9')),
        new("rawvideo", MkTag('a', 'u', 'v', '2')),
        new("rawvideo", MkTag('Y', 'V', 'Y', 'U')),
        new("rawvideo", MkTag('Y', 'U', 'Y', 'V')),
        new("rawvideo", MkTag('I', '4', '1', '0')),
        new("rawvideo", MkTag('I', '4', '1', '1')),
        new("rawvideo", MkTag('I', '4', '2', '2')),
        new("rawvideo", MkTag('I', '4', '4', '0')),
        new("rawvideo", MkTag('I', '4', '4', '4')),
        new("rawvideo", MkTag('J', '4', '2', '0')),
        new("rawvideo", MkTag('J', '4', '2', '2')),
        new("rawvideo", MkTag('J', '4', '4', '0')),
        new("rawvideo", MkTag('J', '4', '4', '4')),
        new("rawvideo", MkTag('Y', 'U', 'V', 'A')),
        new("rawvideo", MkTag('I', '4', '0', 'A')),
        new("rawvideo", MkTag('I', '4', '2', 'A')),
        new("rawvideo", MkTag('R', 'G', 'B', '2')),
        new("rawvideo", MkTag('R', 'V', '1', '5')),
        new("rawvideo", MkTag('R', 'V', '1', '6')),
        new("rawvideo", MkTag('R', 'V', '2', '4')),
        new("rawvideo", MkTag('R', 'V', '3', '2')),
        new("rawvideo", MkTag('R', 'G', 'B', 'A')),
        new("rawvideo", MkTag('A', 'V', '3', '2')),
        new("rawvideo", MkTag('G', 'R', 'E', 'Y')),
        new("rawvideo", MkTag('I', '0', '9', 'L')),
        new("rawvideo", MkTag('I', '0', '9', 'B')),
        new("rawvideo", MkTag('I', '2', '9', 'L')),
        new("rawvideo", MkTag('I', '2', '9', 'B')),
        new("rawvideo", MkTag('I', '4', '9', 'L')),
        new("rawvideo", MkTag('I', '4', '9', 'B')),
        new("rawvideo", MkTag('I', '0', 'A', 'L')),
        new("rawvideo", MkTag('I', '0', 'A', 'B')),
        new("rawvideo", MkTag('I', '2', 'A', 'L')),
        new("rawvideo", MkTag('I', '2', 'A', 'B')),
        new("rawvideo", MkTag('I', '4', 'A', 'L')),
        new("rawvideo", MkTag('I', '4', 'A', 'B')),
        new("rawvideo", MkTag('I', '4', 'F', 'L')),
        new("rawvideo", MkTag('I', '4', 'F', 'B')),
        new("rawvideo", MkTag('I', '0', 'C', 'L')),
        new("rawvideo", MkTag('I', '0', 'C', 'B')),
        new("rawvideo", MkTag('I', '2', 'C', 'L')),
        new("rawvideo", MkTag('I', '2', 'C', 'B')),
        new("rawvideo", MkTag('I', '4', 'C', 'L')),
        new("rawvideo", MkTag('I', '4', 'C', 'B')),
        new("rawvideo", MkTag('I', '0', 'F', 'L')),
        new("rawvideo", MkTag('I', '0', 'F', 'B')),
        new("rawvideo", MkTag('v', '3', '0', '8')),
        new("rawvideo", MkTag('v', '4', '0', '8')),
        new("rawvideo", MkTag('v', '4', '1', '0')),
        new("rawvideo", MkTag('y', '4', '0', '8')),
        new("frwu", MkTag('F', 'R', 'W', 'U')),
        new("r10k", MkTag('R', '1', '0', 'k')),
        new("r210", MkTag('r', '2', '1', '0')),
        new("v210", MkTag('v', '2', '1', '0')),
        new("v210", MkTag('C', '2', '1', '0')),
        new("v308", MkTag('v', '3', '0', '8')),
        new("v408", MkTag('v', '4', '0', '8')),
        new("v410", MkTag('v', '4', '1', '0')),
        new("yuv4", MkTag('y', 'u', 'v', '4')),
        new("indeo3", MkTag('I', 'V', '3', '1')),
        new("indeo3", MkTag('I', 'V', '3', '2')),
        new("indeo4", MkTag('I', 'V', '4', '1')),
        new("indeo5", MkTag('I', 'V', '5', '0')),
        new("vp3", MkTag('V', 'P', '3', '1')),
        new("vp3", MkTag('V', 'P', '3', '0')),
        new("vp4", MkTag('V', 'P', '4', '0')),
        new("vp5", MkTag('V', 'P', '5', '0')),
        new("vp6", MkTag('V', 'P', '6', '0')),
        new("vp6", MkTag('V', 'P', '6', '1')),
        new("vp6", MkTag('V', 'P', '6', '2')),
        new("vp6a", MkTag('V', 'P', '6', 'A')),
        new("vp6f", MkTag('V', 'P', '6', 'F')),
        new("vp6f", MkTag('F', 'L', 'V', '4')),
        new("vp7", MkTag('V', 'P', '7', '0')),
        new("vp7", MkTag('V', 'P', '7', '1')),
        new("vp8", MkTag('V', 'P', '8', '0')),
        new("vp9", MkTag('V', 'P', '9', '0')),
        new("asv1", MkTag('A', 'S', 'V', '1')),
        new("asv2", MkTag('A', 'S', 'V', '2')),
        new("vcr1", MkTag('V', 'C', 'R', '1')),
        new("ffv1", MkTag('F', 'F', 'V', '1')),
        new("xan_wc4", MkTag('X', 'x', 'a', 'n')),
        new("mimic", MkTag('L', 'M', '2', '0')),
        new("msrle", MkTag('m', 'r', 'l', 'e')),
        new("msrle", MkTag(1, 0, 0, 0)),
        new("msrle", MkTag(2, 0, 0, 0)),
        new("msvideo1", MkTag('M', 'S', 'V', 'C')),
        new("msvideo1", MkTag('m', 's', 'v', 'c')),
        new("msvideo1", MkTag('C', 'R', 'A', 'M')),
        new("msvideo1", MkTag('c', 'r', 'a', 'm')),
        new("msvideo1", MkTag('W', 'H', 'A', 'M')),
        new("msvideo1", MkTag('w', 'h', 'a', 'm')),
        new("cinepak", MkTag('c', 'v', 'i', 'd')),
        new("truemotion1", MkTag('D', 'U', 'C', 'K')),
        new("truemotion1", MkTag('P', 'V', 'E', 'Z')),
        new("mszh", MkTag('M', 'S', 'Z', 'H')),
        new("zlib", MkTag('Z', 'L', 'I', 'B')),
        new("snow", MkTag('S', 'N', 'O', 'W')),
        new("4xm", MkTag('4', 'X', 'M', 'V')),
        new("flv1", MkTag('F', 'L', 'V', '1')),
        new("flv1", MkTag('S', '2', '6', '3')),
        new("flashsv", MkTag('F', 'S', 'V', '1')),
        new("svq1", MkTag('s', 'v', 'q', '1')),
        new("tscc", MkTag('t', 's', 'c', 'c')),
        new("ulti", MkTag('U', 'L', 'T', 'I')),
        new("vixl", MkTag('V', 'I', 'X', 'L')),
        new("qpeg", MkTag('Q', 'P', 'E', 'G')),
        new("qpeg", MkTag('Q', '1', '.', '0')),
        new("qpeg", MkTag('Q', '1', '.', '1')),
        new("wmv3", MkTag('W', 'M', 'V', '3')),
        new("wmv3image", MkTag('W', 'M', 'V', 'P')),
        new("vc1", MkTag('W', 'V', 'C', '1')),
        new("vc1", MkTag('W', 'M', 'V', 'A')),
        new("vc1image", MkTag('W', 'V', 'P', '2')),
        new("loco", MkTag('L', 'O', 'C', 'O')),
        new("wnv1", MkTag('W', 'N', 'V', '1')),
        new("wnv1", MkTag('Y', 'U', 'V', '8')),
        new("aasc", MkTag('A', 'A', 'S', '4')),  // Autodesk 24 bit RLE compressor
        new("aasc", MkTag('A', 'A', 'S', 'C')),
        new("indeo2", MkTag('R', 'T', '2', '1')),
        new("fraps", MkTag('F', 'P', 'S', '1')),
        new("theora", MkTag('t', 'h', 'e', 'o')),
        new("truemotion2", MkTag('T', 'M', '2', '0')),
        new("truemotion2rt", MkTag('T', 'R', '2', '0')),
        new("cscd", MkTag('C', 'S', 'C', 'D')),
        new("zmbv", MkTag('Z', 'M', 'B', 'V')),
        new("kmvc", MkTag('K', 'M', 'V', 'C')),
        new("cavs", MkTag('C', 'A', 'V', 'S')),
        new("avs2", MkTag('A', 'V', 'S', '2')),
        new("jpeg2000", MkTag('m', 'j', 'p', '2')),
        new("jpeg2000", MkTag('M', 'J', '2', 'C')),
        new("jpeg2000", MkTag('L', 'J', '2', 'C')),
        new("jpeg2000", MkTag('L', 'J', '2', 'K')),
        new("jpeg2000", MkTag('I', 'P', 'J', '2')),
        new("jpeg2000", MkTag('A', 'V', 'j', '2')),  // Avid jpeg2000
        new("vmnc", MkTag('V', 'M', 'n', 'c')),
        new("targa", MkTag('t', 'g', 'a', ' ')),
        new("png", MkTag('M', 'P', 'N', 'G')),
        new("png", MkTag('P', 'N', 'G', '1')),
        new("png", MkTag('p', 'n', 'g', ' ')),  // ImageJ
        new("cljr", MkTag('C', 'L', 'J', 'R')),
        new("dirac", MkTag('d', 'r', 'a', 'c')),
        new("rpza", MkTag('a', 'z', 'p', 'r')),
        new("rpza", MkTag('R', 'P', 'Z', 'A')),
        new("rpza", MkTag('r', 'p', 'z', 'a')),
        new("sp5x", MkTag('S', 'P', '5', '4')),
        new("aura", MkTag('A', 'U', 'R', 'A')),
        new("aura2", MkTag('A', 'U', 'R', '2')),
        new("dpx", MkTag('d', 'p', 'x', ' ')),
        new("kgv1", MkTag('K', 'G', 'V', '1')),
        new("lagarith", MkTag('L', 'A', 'G', 'S')),
        new("amv", MkTag('A', 'M', 'V', 'F')),
        new("utvideo", MkTag('U', 'L', 'R', 'A')),
        new("utvideo", MkTag('U', 'L', 'R', 'G')),
        new("utvideo", MkTag('U', 'L', 'Y', '0')),
        new("utvideo", MkTag('U', 'L', 'Y', '2')),
        new("utvideo", MkTag('U', 'L', 'Y', '4')),
        new("utvideo", MkTag('U', 'L', 'H', '0')),
        new("utvideo", MkTag('U', 'L', 'H', '2')),
        new("utvideo", MkTag('U', 'L', 'H', '4')),
        new("utvideo", MkTag('U', 'Q', 'Y', '0')),
        new("utvideo", MkTag('U', 'Q', 'Y', '2')),
        new("utvideo", MkTag('U', 'Q', 'R', 'A')),
        new("utvideo", MkTag('U', 'Q', 'R', 'G')),
        new("utvideo", MkTag('U', 'M', 'Y', '2')),
        new("utvideo", MkTag('U', 'M', 'H', '2')),
        new("utvideo", MkTag('U', 'M', 'Y', '4')),
        new("utvideo", MkTag('U', 'M', 'H', '4')),
        new("utvideo", MkTag('U', 'M', 'R', 'A')),
        new("utvideo", MkTag('U', 'M', 'R', 'G')),
        new("vble", MkTag('V', 'B', 'L', 'E')),
        new("escape130", MkTag('E', '1', '3', '0')),
        new("dxtory", MkTag('x', 't', 'o', 'r')),
        new("zerocodec", MkTag('Z', 'E', 'C', 'O')),
        new("y41p", MkTag('Y', '4', '1', 'P')),
        new("flic", MkTag('A', 'F', 'L', 'C')),
        new("mss1", MkTag('M', 'S', 'S', '1')),
        new("msa1", MkTag('M', 'S', 'A', '1')),
        new("tscc2", MkTag('T', 'S', 'C', '2')),
        new("mts2", MkTag('M', 'T', 'S', '2')),
        new("cllc", MkTag('C', 'L', 'L', 'C')),
        new("mss2", MkTag('M', 'S', 'S', '2')),
        new("svq3", MkTag('S', 'V', 'Q', '3')),
        new("012v", MkTag('0', '1', '2', 'v')),
        new("012v", MkTag('a', '1', '2', 'v')),
        new("g2m", MkTag('G', '2', 'M', '2')),
        new("g2m", MkTag('G', '2', 'M', '3')),
        new("g2m", MkTag('G', '2', 'M', '4')),
        new("g2m", MkTag('G', '2', 'M', '5')),
        new("fic", MkTag('F', 'I', 'C', 'V')),
        new("hqx", MkTag('C', 'H', 'Q', 'X')),
        new("tdsc", MkTag('T', 'D', 'S', 'C')),
        new("hq_hqa", MkTag('C', 'U', 'V', 'C')),
        new("rv40", MkTag('R', 'V', '4', '0')),
        new("screenpresso", MkTag('S', 'P', 'V', '1')),
        new("rscc", MkTag('R', 'S', 'C', 'C')),
        new("rscc", MkTag('I', 'S', 'C', 'C')),
        new("cfhd", MkTag('C', 'F', 'H', 'D')),
        new("m101", MkTag('M', '1', '0', '1')),
        new("m101", MkTag('M', '1', '0', '2')),
        new("magicyuv", MkTag('M', 'A', 'G', 'Y')),
        new("magicyuv", MkTag('M', '8', 'R', 'G')),
        new("magicyuv", MkTag('M', '8', 'R', 'A')),
        new("magicyuv", MkTag('M', '8', 'G', '0')),
        new("magicyuv", MkTag('M', '8', 'Y', '0')),
        new("magicyuv", MkTag('M', '8', 'Y', '2')),
        new("magicyuv", MkTag('M', '8', 'Y', '4')),
        new("magicyuv", MkTag('M', '8', 'Y', 'A')),
        new("magicyuv", MkTag('M', '0', 'R', 'A')),
        new("magicyuv", MkTag('M', '0', 'R', 'G')),
        new("magicyuv", MkTag('M', '0', 'G', '0')),
        new("magicyuv", MkTag('M', '0', 'Y', '0')),
        new("magicyuv", MkTag('M', '0', 'Y', '2')),
        new("magicyuv", MkTag('M', '0', 'Y', '4')),
        new("magicyuv", MkTag('M', '2', 'R', 'A')),
        new("magicyuv", MkTag('M', '2', 'R', 'G')),
        new("magicyuv", MkTag('M', '4', 'R', 'A')),
        new("magicyuv", MkTag('M', '4', 'R', 'G')),
        new("ylc", MkTag('Y', 'L', 'C', '0')),
        new("speedhq", MkTag('S', 'H', 'Q', '0')),
        new("speedhq", MkTag('S', 'H', 'Q', '1')),
        new("speedhq", MkTag('S', 'H', 'Q', '2')),
        new("speedhq", MkTag('S', 'H', 'Q', '3')),
        new("speedhq", MkTag('S', 'H', 'Q', '4')),
        new("speedhq", MkTag('S', 'H', 'Q', '5')),
        new("speedhq", MkTag('S', 'H', 'Q', '7')),
        new("speedhq", MkTag('S', 'H', 'Q', '9')),
        new("fmvc", MkTag('F', 'M', 'V', 'C')),
        new("scpr", MkTag('S', 'C', 'P', 'R')),
        new("clearvideo", MkTag('U', 'C', 'O', 'D')),
        new("av1", MkTag('A', 'V', '0', '1')),
        new("mscc", MkTag('M', 'S', 'C', 'C')),
        new("srgc", MkTag('S', 'R', 'G', 'C')),
        new("imm4", MkTag('I', 'M', 'M', '4')),
        new("prosumer", MkTag('B', 'T', '2', '0')),
        new("mwsc", MkTag('M', 'W', 'S', 'C')),
        new("wcmv", MkTag('W', 'C', 'M', 'V')),
        new("rasc", MkTag('R', 'A', 'S', 'C')),
        new("hymt", MkTag('H', 'Y', 'M', 'T')),
        new("arbc", MkTag('A', 'R', 'B', 'C')),
        new("agm", MkTag('A', 'G', 'M', '0')),
        new("agm", MkTag('A', 'G', 'M', '1')),
        new("agm", MkTag('A', 'G', 'M', '2')),
        new("agm", MkTag('A', 'G', 'M', '3')),
        new("agm", MkTag('A', 'G', 'M', '4')),
        new("agm", MkTag('A', 'G', 'M', '5')),
        new("agm", MkTag('A', 'G', 'M', '6')),
        new("agm", MkTag('A', 'G', 'M', '7')),
        new("lscr", MkTag('L', 'S', 'C', 'R')),
        new("imm5", MkTag('I', 'M', 'M', '5')),
        new("mvdv", MkTag('M', 'V', 'D', 'V')),
        new("mvha", MkTag('M', 'V', 'H', 'A')),
        new("mv30", MkTag('M', 'V', '3', '0')),
        new("notchlc", MkTag('n', 'l', 'c', '1')),
        new("vqc", MkTag('V', 'Q', 'C', '1')),
        new("vqc", MkTag('V', 'Q', 'C', '2')),
        new("rtv1", MkTag('R', 'T', 'V', '1')),
        new("vmix", MkTag('V', 'M', 'X', '1')),
        new("lead", MkTag('L', 'E', 'A', 'D')),
        new("evc", MkTag('e', 'v', 'c', '1')),
        new("rv60", MkTag('R', 'V', '6', '0')),
    ];

    /// <summary>A translation of <c>ff_codec_bmp_tags_unofficial</c>, in the C's own order.</summary>
    public static readonly CodecTag[] CodecBmpTagsUnofficial =
    [
        new("hevc", MkTag('H', 'E', 'V', 'C')),
        new("hevc", MkTag('H', '2', '6', '5')),
    ];

    /// <summary>A translation of <c>ff_codec_wav_tags</c>, in the C's own order.</summary>
    public static readonly CodecTag[] CodecWavTags =
    [
        new("pcm_s16le", 0x0001),
        new("pcm_u8", 0x0001),
        new("pcm_s24le", 0x0001),
        new("pcm_s32le", 0x0001),
        new("pcm_s64le", 0x0001),
        new("adpcm_ms", 0x0002),
        new("pcm_f32le", 0x0003),
        new("pcm_f64le", 0x0003),
        new("pcm_alaw", 0x0006),
        new("pcm_mulaw", 0x0007),
        new("wmavoice", 0x000A),
        new("adpcm_ima_oki", 0x0010),
        new("adpcm_ima_wav", 0x0011),
        new("adpcm_zork", 0x0011),
        new("adpcm_ima_oki", 0x0017),
        new("adpcm_yamaha", 0x0020),
        new("truespeech", 0x0022),
        new("gsm_ms", 0x0031),
        new("gsm_ms", 0x0032),  // msn audio
        new("amr_nb", 0x0038),  // rogue format number
        new("g723_1", 0x0042),
        new("adpcm_g726", 0x0045),
        new("adpcm_g726", 0x0014),  // g723 Antex
        new("adpcm_g726", 0x0040),  // g721 Antex
        new("g728", 0x0041),
        new("mp2", 0x0050),
        new("mp3", 0x0055),
        new("amr_nb", 0x0057),
        new("amr_wb", 0x0058),
        new("adpcm_ima_dk4", 0x0061),
        new("adpcm_ima_dk3", 0x0062),
        new("adpcm_g726", 0x0064),
        new("adpcm_ima_xbox", 0x0069),
        new("metasound", 0x0075),
        new("g729", 0x0083),
        new("aac", 0x00FF),
        new("g723_1", 0x0111),
        new("adpcm_sanyo", 0x0125),
        new("sipr", 0x0130),
        new("acelp.kelvin", 0x0135),
        new("wmav1", 0x0160),
        new("wmav2", 0x0161),
        new("wmapro", 0x0162),
        new("wmalossless", 0x0163),
        new("xma1", 0x0165),
        new("xma2", 0x0166),
        new("ftr", 0x0180),
        new("adpcm_ct", 0x0200),
        new("dvaudio", 0x0215),
        new("dvaudio", 0x0216),
        new("atrac3", 0x0270),
        new("msnsiren", 0x028E),
        new("adpcm_g722", 0x028F),
        new("misc4", 0x0350),
        new("imc", 0x0401),
        new("iac", 0x0402),
        new("avc", 0x0500),
        new("avc", 0x0501),
        new("gsm_ms", 0x1500),
        new("truespeech", 0x1501),
        new("aac", 0x1600),
        new("aac_latm", 0x1602),
        new("aac", 0x1610),
        new("ac3", 0x2000),
        new("eac3", 0x2000),
        new("dts", 0x2001),
        new("sonic", 0x2048),
        new("sonicls", 0x2048),
        new("g729", 0x2222),
        new("pcm_mulaw", 0x6C75),
        new("aac", 0x706D),
        new("aac", 0x4143),
        new("ftr", 0x4180),
        new("xan_dpcm", 0x594A),
        new("g729", 0x729A),
        new("ftr", 0x8180),
        new("g723_1", 0xA100),  // Comverse Infosys Ltd. G723 1
        new("aac", 0xA106),
        new("speex", 0xA109),
        new("g728", 0xCD02),
        new("flac", 0xF1AC),
        new("dfpwm", 0xFFFE),
        new("adpcm_swf", ('S' << 8) + 'F'),
        new("vorbis", ('V' << 8) + 'o'),
    ];

    /// <summary>
    /// riffdec.c's <c>ff_codec_wav_guids</c>: the subformats a WAVEFORMATEXTENSIBLE names by
    /// GUID rather than by format tag.
    /// </summary>
    public static readonly CodecGuid[] CodecWavGuids =
    [
        new("ac3",        [0x2C, 0x80, 0x6D, 0xE0, 0x46, 0xDB, 0xCF, 0x11, 0xB4, 0xD1, 0x00, 0x80, 0x5F, 0x6C, 0xBB, 0xEA]),
        new("atrac3p",    [0xBF, 0xAA, 0x23, 0xE9, 0x58, 0xCB, 0x71, 0x44, 0xA1, 0x19, 0xFF, 0xFA, 0x01, 0xE4, 0xCE, 0x62]),
        new("atrac9",     [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C]),
        new("eac3",       [0xAF, 0x87, 0xFB, 0xA7, 0x02, 0x2D, 0xFB, 0x42, 0xA4, 0xD4, 0x05, 0xCD, 0x93, 0x84, 0x3B, 0xDD]),
        new("mp2",        [0x2B, 0x80, 0x6D, 0xE0, 0x46, 0xDB, 0xCF, 0x11, 0xB4, 0xD1, 0x00, 0x80, 0x5F, 0x6C, 0xBB, 0xEA]),
        new("adpcm_agm",  [0x82, 0xEC, 0x1F, 0x6A, 0xCA, 0xDB, 0x19, 0x45, 0xBD, 0xE7, 0x56, 0xD3, 0xB3, 0xEF, 0x98, 0x1D]),
        new("dfpwm",      [0x3A, 0xC1, 0xFA, 0x38, 0x81, 0x1D, 0x43, 0x61, 0xA4, 0x0D, 0xCE, 0x53, 0xCA, 0x60, 0x7C, 0xD1]),
    ];

    /// <summary>riff.h's <c>FF_MEDIASUBTYPE_BASE_GUID</c>.</summary>
    private static ReadOnlySpan<byte> MediaSubtypeBaseGuid
        => [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>riff.h's <c>FF_AMBISONIC_BASE_GUID</c>.</summary>
    private static ReadOnlySpan<byte> AmbisonicBaseGuid
        => [0x21, 0x07, 0xD3, 0x11, 0x86, 0x44, 0xC8, 0xC1, 0xCA, 0x00, 0x00, 0x00];

    /// <summary>riff.h's <c>FF_BROKEN_BASE_GUID</c>.</summary>
    private static ReadOnlySpan<byte> BrokenBaseGuid
        => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA];

    /// <summary>
    /// True when the GUID's last twelve bytes are one of the three bases that mean "the first
    /// four bytes are a WAVE format tag".
    /// </summary>
    internal static bool IsFormatTagGuid(ReadOnlySpan<byte> guid)
        => guid[4..].SequenceEqual(MediaSubtypeBaseGuid)
        || guid[4..].SequenceEqual(AmbisonicBaseGuid)
        || guid[4..].SequenceEqual(BrokenBaseGuid);

    /// <summary>utils.c's <c>ff_toupper4</c>.</summary>
    public static uint Toupper4(uint x)
        => (uint)(ToUpper((byte)x)
                | (ToUpper((byte)(x >> 8)) << 8)
                | (ToUpper((byte)(x >> 16)) << 16)
                | (ToUpper((byte)(x >> 24)) << 24));

    private static int ToUpper(byte c) => c >= 'a' && c <= 'z' ? c - 'a' + 'A' : c;

    /// <summary>
    /// utils.c's <c>ff_codec_get_id</c>: an exact match over the whole table first, then a
    /// case-insensitive one. Both passes run the table in order, so the first row for a tag
    /// wins.
    /// </summary>
    public static string? CodecGetId(CodecTag[] tags, uint tag)
    {
        for (int i = 0; i < tags.Length; i++)
            if (tag == tags[i].Tag)
                return tags[i].Codec;

        for (int i = 0; i < tags.Length; i++)
            if (Toupper4(tag) == Toupper4(tags[i].Tag))
                return tags[i].Codec;

        return null;
    }

    /// <summary>riffdec.c's <c>ff_codec_guid_get_id</c>.</summary>
    public static string? CodecGuidGetId(CodecGuid[] guids, ReadOnlySpan<byte> guid)
    {
        for (int i = 0; i < guids.Length; i++)
            if (guid.SequenceEqual(guids[i].Guid))
                return guids[i].Codec;

        return null;
    }

    /// <summary>
    /// riffdec.c's <c>ff_wav_codec_get_id</c>. Tag 0x0001 and tag 0x0003 each stand for a
    /// whole family, and the bits per coded sample is the only thing that says which member -
    /// which is why the table lists the families' first member and this resolves it.
    /// </summary>
    public static string? WavCodecGetId(uint tag, int bps)
    {
        string? id = CodecGetId(CodecWavTags, tag);
        if (id is null)
            return null;

        if (id == "pcm_s16le")
            id = CafDec.GetPcmCodecId(bps, false, false, ~1);
        else if (id == "pcm_f32le")
            id = CafDec.GetPcmCodecId(bps, true, false, 0);

        if (id == "adpcm_ima_wav" && bps == 8)
            id = "adpcm_zork";

        return id;
    }
}

/// <summary>
/// What <see cref="Riff.GetWavHeader"/> read out of a WAVEFORMAT, PCMWAVEFORMAT or
/// WAVEFORMATEX. The C fills an <c>AVCodecParameters</c> in place; the fields it touches are
/// these.
/// </summary>
public struct WavHeader
{
    public string? CodecName;
    public uint CodecTag;
    public int Channels;
    public int SampleRate;
    public long BitRate;
    public int BlockAlign;
    public int BitsPerCodedSample;
    public byte[]? ExtraData;

    /// <summary>
    /// A WAVEFORMATEXTENSIBLE's <c>dwChannelMask</c>, zero when the header did not carry one.
    /// </summary>
    public ulong ChannelMask;
}

/// <summary>The RIFF structure readers.</summary>
public static class Riff
{
    /// <summary>
    /// riffdec.c's <c>ff_get_wav_header</c>. The three structures this can be handed -
    /// WAVEFORMAT, PCMWAVEFORMAT and WAVEFORMATEX - are each the previous one with fields
    /// added at the bottom, so the size is what says which one arrived.
    /// </summary>
    /// <remarks>
    /// Exactly <paramref name="size"/> bytes are consumed from <paramref name="stream"/>,
    /// including the C's trailing <c>avio_skip</c> of whatever the chunk had left over.
    /// </remarks>
    public static WavHeader GetWavHeader(Stream stream, int size, bool bigEndian)
    {
        if (size < 14)
            throw new InvalidDataException($"RIFF: wav header size {size} < 14.");

        long end = stream.Position + size;
        var par = default(WavHeader);

        int id;
        long bitrate = 0;
        int channels = 0;
        if (!bigEndian)
        {
            id = ReadU16Le(stream);
            if (id != 0x0165)
            {
                channels = ReadU16Le(stream);
                par.SampleRate = (int)ReadU32Le(stream);
                bitrate = ReadU32Le(stream) * 8L;
                par.BlockAlign = ReadU16Le(stream);
            }
        }
        else
        {
            id = ReadU16Be(stream);
            channels = ReadU16Be(stream);
            par.SampleRate = (int)ReadU32Be(stream);
            bitrate = ReadU32Be(stream) * 8L;
            par.BlockAlign = ReadU16Be(stream);
        }

        if (size == 14)
        {
            // Plain vanilla WAVEFORMAT, which has no wBitsPerSample at all.
            par.BitsPerCodedSample = 8;
        }
        else
        {
            par.BitsPerCodedSample = bigEndian ? ReadU16Be(stream) : ReadU16Le(stream);
        }

        if (id == 0xFFFE)
        {
            par.CodecTag = 0;
        }
        else
        {
            par.CodecTag = (uint)id;
            par.CodecName = RiffTags.WavCodecGetId((uint)id, par.BitsPerCodedSample);
        }

        if (size >= 18 && id != 0x0165)
        {
            // Obviously dealing with WAVEFORMATEX.
            int cbSize = ReadU16Le(stream);
            if (bigEndian)
                throw new NotSupportedException(
                    "RIFF: WAVEFORMATEX inside a RIFX file is not translated; ffmpeg refuses it "
                  + "too (AVERROR_PATCHWELCOME).");

            size -= 18;
            cbSize = Math.Min(size, cbSize);

            if (cbSize >= 22 && id == 0xFFFE)
            {
                ParseWaveFormatEx(stream, ref par);
                cbSize -= 22;
                size -= 22;
            }
            else if (cbSize >= 12 && id == 0x1610)
            {
                // HEAACWAVEFORMAT.
                int payloadType = ReadU16Le(stream);
                if (payloadType == 3)
                    par.CodecName = "aac_latm";
                Skip(stream, 2);                     // wAudioProfileLevelIndication
                int structType = ReadU16Le(stream);
                if (structType != 0)
                    throw new NotSupportedException(
                        $"RIFF: HEAACWAVEINFO wStructType {structType} is not translated; "
                      + "ffmpeg refuses it too (AVERROR_PATCHWELCOME).");
                Skip(stream, 2);                     // wReserved1
                Skip(stream, 4);                     // dwReserved2
                cbSize -= 12;
                size -= 12;
            }

            if (cbSize > 0)
            {
                par.ExtraData = new byte[cbSize];
                stream.ReadExactly(par.ExtraData);
                size -= cbSize;
            }

            // It is possible for the chunk to contain garbage at the end.
            if (size > 0)
                Skip(stream, size);
        }
        else if (id == 0x0165 && size >= 32)
        {
            // XMA2, whose stream count, rate and channel count live in its extradata.
            size -= 4;
            par.ExtraData = new byte[size];
            stream.ReadExactly(par.ExtraData);
            int streams = BinaryPrimitives.ReadUInt16LittleEndian(par.ExtraData.AsSpan(4, 2));
            par.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                par.ExtraData.AsSpan(12, 4));
            channels = 0;
            bitrate = 0;
            if (size < 8 + streams * 20)
                throw new InvalidDataException(
                    $"RIFF: XMA2 header declares {streams} streams but has {size} bytes.");
            for (int i = 0; i < streams; i++)
                channels += par.ExtraData[8 + i * 20 + 17];
        }

        par.BitRate = bitrate;

        if (par.SampleRate <= 0)
            throw new InvalidDataException($"RIFF: invalid sample rate {par.SampleRate}.");

        if (par.CodecName == "aac_latm")
        {
            // The channel count and sample rate in the header are those before SBR and PS.
            channels = 0;
            par.SampleRate = 0;
        }

        if (par.CodecName == "adpcm_g726" && par.SampleRate != 0)
            par.BitsPerCodedSample = (int)(par.BitRate / par.SampleRate);

        par.Channels = channels;

        // The C discards a WAVEFORMATEXTENSIBLE layout that disagrees with the channel count;
        // the mask is kept here and the count always wins, which is the same outcome.
        if (par.ChannelMask != 0 && System.Numerics.BitOperations.PopCount(par.ChannelMask)
            != channels)
            par.ChannelMask = 0;

        // Whatever the C's arithmetic left, the chunk is consumed exactly.
        if (stream.Position < end)
            Skip(stream, (int)(end - stream.Position));

        return par;
    }

    /// <summary>
    /// riffdec.c's <c>parse_waveformatex</c>: the 22 bytes a WAVEFORMATEXTENSIBLE adds. The
    /// subformat GUID either wraps a WAVE format tag in its first four bytes - which is what
    /// the three base GUIDs mean - or names a codec outright.
    /// </summary>
    private static void ParseWaveFormatEx(Stream stream, ref WavHeader par)
    {
        int bps = ReadU16Le(stream);
        if (bps != 0)
            par.BitsPerCodedSample = bps;

        par.ChannelMask = ReadU32Le(stream);

        Span<byte> subformat = stackalloc byte[16];
        GetGuid(stream, subformat);

        if (RiffTags.IsFormatTagGuid(subformat))
        {
            par.CodecTag = BinaryPrimitives.ReadUInt32LittleEndian(subformat);
            par.CodecName = RiffTags.WavCodecGetId(par.CodecTag, par.BitsPerCodedSample);
        }
        else
        {
            par.CodecName = RiffTags.CodecGuidGetId(RiffTags.CodecWavGuids, subformat);
        }
    }

    /// <summary>riffdec.c's <c>ff_get_guid</c>: sixteen bytes, zeroed on a short read.</summary>
    internal static void GetGuid(Stream stream, Span<byte> guid)
    {
        if (guid.Length != 16)
            throw new ArgumentException("a GUID is 16 bytes", nameof(guid));

        int got = 0;
        while (got < 16)
        {
            int n = stream.Read(guid[got..]);
            if (n <= 0)
            {
                guid.Clear();
                return;
            }
            got += n;
        }
    }

    private static void Skip(Stream stream, int count)
    {
        if (count <= 0) return;
        if (stream.CanSeek) stream.Position += count;
        else
        {
            Span<byte> sink = stackalloc byte[256];
            while (count > 0)
            {
                int n = stream.Read(sink[..Math.Min(count, sink.Length)]);
                if (n <= 0) return;
                count -= n;
            }
        }
    }

    private static int ReadU16Le(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    private static int ReadU16Be(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static uint ReadU32Le(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static uint ReadU32Be(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }
}

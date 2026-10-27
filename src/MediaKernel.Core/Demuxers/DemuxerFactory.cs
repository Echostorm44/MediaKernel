// MediaKernel - Demuxer Factory
// Automatically selects and creates the appropriate demuxer for a media source

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaKernel.Core.Codecs.AAC;
using MediaKernel.Core.Codecs.AC3;
using MediaKernel.Core.Codecs.MP3;
using MediaKernel.Core.Input;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result of demuxer probing.
/// </summary>
public readonly struct DemuxerProbeResult
{
    /// <summary>Whether a format was detected.</summary>
    public bool Success { get; init; }
    
    /// <summary>Detected format name.</summary>
    public string? FormatName { get; init; }
    
    /// <summary>Confidence score (0-100).</summary>
    public int Confidence { get; init; }
    
    /// <summary>Type of demuxer to use.</summary>
    public DemuxerType Type { get; init; }
}

/// <summary>
/// Supported demuxer types.
/// </summary>
public enum DemuxerType
{
    Unknown,
    Mp4,
    Matroska,
    Avi,
    Flv,
    MpegTs,
    Ogg,
    Wav,
    Aiff,
    Au,
    Caf,
    Smf,
    Voc,
    Flac,
    Tta,
    Xa,
    Pva,
    Nsv,
    Cdg,
    Mjpeg,
    Smjpeg,
    Thp,
    RawAudio,
    RawVideo,
    Dv,
    Stl,
    Asf,
    MpegPs,
    VobSub,
    Hx,
    Mp3,
    Adts,
    Ac3,
    Dts,
    DtsHd,

    /// <summary>
    /// An IVF file - a 32-byte header and length-prefixed frames, which is how the VP8, VP9
    /// and AV1 test vectors are distributed and so the container most of this tree's video
    /// samples are in.
    /// </summary>
    Ivf,

    /// <summary>A raw VC-1 Advanced Profile elementary stream.</summary>
    Vc1Es,

    /// <summary>SMPTE's RCV wrapper around a VC-1 Simple or Main Profile stream.</summary>
    Vc1Rcv,

    /// <summary>A raw MPEG-4 Part 2 elementary stream, as a .m4v holds.</summary>
    Mpeg4Video
}

/// <summary>
/// Factory for creating demuxers based on media source.
/// </summary>
public static class DemuxerFactory
{
    // Magic bytes for format detection
    private static readonly byte[] Mp4Ftyp = { 0x66, 0x74, 0x79, 0x70 }; // "ftyp" at offset 4
    private static readonly byte[] MatroskaMagic = { 0x1A, 0x45, 0xDF, 0xA3 }; // EBML header
    private static readonly byte[] AviMagic = { 0x52, 0x49, 0x46, 0x46 }; // "RIFF"
    private static readonly byte[] FlvMagic = { 0x46, 0x4C, 0x56 }; // "FLV"
    private static readonly byte[] TsSyncByte = { 0x47 }; // MPEG-TS sync
    private static readonly byte[] OggMagic = { 0x4F, 0x67, 0x67, 0x53 }; // "OggS"
    private static readonly byte[] WavMagic = { 0x52, 0x49, 0x46, 0x46 }; // "RIFF" (then "WAVE")
    private static readonly byte[] AiffMagic = { 0x46, 0x4F, 0x52, 0x4D }; // "FORM" (then "AIFF")
    private static readonly byte[] AuMagic = { 0x2E, 0x73, 0x6E, 0x64 }; // ".snd"
    private static readonly byte[] SmfMagic = { 0x4D, 0x54, 0x68, 0x64 }; // "MThd"
    private static readonly byte[] RmidMagic = { 0x52, 0x49, 0x46, 0x46 }; // "RIFF" (then "RMID")
    private static readonly byte[] FlacMagic = { 0x66, 0x4C, 0x61, 0x43 }; // "fLaC"
    private static readonly byte[] PvaMagic = { 0x41, 0x56 }; // "AV" (PVA header start)
    private static readonly byte[] NsvMagic = { 0x4E, 0x53, 0x56 }; // "NSV" (followed by 'f' or 's')
    private static readonly byte[] JpegSoiMagic = { 0xFF, 0xD8 }; // JPEG Start of Image
    private static readonly byte[] Y4MMagic = { 0x59, 0x55, 0x56, 0x34, 0x4D, 0x50, 0x45, 0x47, 0x32 }; // "YUV4MPEG2"
    private static readonly byte[] AsfMagic = { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 
                                                 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C }; // ASF Header GUID
    private static readonly byte[] MpegPsMagic = { 0x00, 0x00, 0x01, 0xBA }; // MPEG PS Pack start code
    private static readonly byte[] HxvsMagic = { 0x48, 0x58, 0x56, 0x53 }; // "HXVS" (H.264 HX file)
    private static readonly byte[] HxvtMagic = { 0x48, 0x58, 0x56, 0x54 }; // "HXVT" (HEVC HX file)
    private static readonly byte[] IvfMagic = { 0x44, 0x4B, 0x49, 0x46 }; // "DKIF"

    /// <summary>
    /// <c>vc1t_probe</c>: an RCV file has 0xC5 as its fourth byte, a struct-C size of 4 and
    /// 0xC where the second size goes. Three fixed fields, which is what makes it a probe
    /// rather than a guess - the file starts with a 24-bit frame count and no magic.
    /// </summary>
    private static bool ProbeRcv(ReadOnlySpan<byte> header)
    {
        if (header.Length < 24 || header[3] != 0xC5)
            return false;

        uint structCSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (structCSize < 4 || structCSize > 64)
            return false;

        // DIVERGENCE: vc1t_probe hardcodes a struct-C size of 4 and reads the 0xC at a fixed
        // offset 20, and then REJECTS a file whose size is anything else - ff_vc1t_demuxer
        // opens those by their .rcv extension instead. TestMedia/fate-suite/vc1/SMM0005.rcv
        // has a size of 5, so the fixed-offset probe says no to a file the demuxer reads
        // perfectly. This follows the size, which agrees with vc1t_probe exactly wherever
        // vc1t_probe says yes.
        int afterExtradata = 8 + (int)structCSize;
        if (header.Length < afterExtradata + 12)
            return false;

        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(header[(afterExtradata + 8)..]) == 0xC;
    }
    
    /// <summary>
    /// Probe a stream to detect its format.
    /// </summary>
    public static async Task<DemuxerProbeResult> ProbeAsync(
        MediaInput input, 
        CancellationToken cancellationToken = default)
    {
        if (!input.IsOpen)
            await input.OpenAsync(cancellationToken);
        
        // Check if format was detected from extension/content-type
        if (!string.IsNullOrEmpty(input.Info.DetectedFormat))
        {
            var type = FormatNameToType(input.Info.DetectedFormat);
            if (type != DemuxerType.Unknown)
            {
                return new DemuxerProbeResult
                {
                    Success = true,
                    FormatName = input.Info.DetectedFormat,
                    Confidence = 80, // High confidence from extension/content-type
                    Type = type
                };
            }
        }
        
        // Read header bytes for magic detection
        byte[] header = new byte[12];
        long originalPosition = input.Position;
        
        int read = await input.ReadAsync(header, cancellationToken);
        if (read < 4)
        {
            return new DemuxerProbeResult { Success = false };
        }
        
        // Seek back to start
        if (input.Info.CanSeek)
        {
            await input.SeekAsync(originalPosition, cancellationToken);
        }
        
        // Probe by magic bytes
        return ProbeByMagic(header.AsSpan(0, read));
    }
    

    /// <summary>
    /// <c>mov_probe</c> (<c>mov.c</c>) - walk the top-level boxes looking for one that only
    /// an ISO-BMFF file would carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A box is a big-endian 32-bit size and a four-character type. Size 1 means the real
    /// size is a 64-bit field after the type, and size 0 means "to the end of the file" -
    /// both of which a classic QuickTime file uses. A size below the minimum is not fatal:
    /// the C steps forward FOUR bytes and tries again, because some writers put junk at the
    /// front.
    /// </para>
    /// <para>
    /// SCOPED: the C computes a SCORE and weighs common words like <c>wide</c> and
    /// <c>free</c> lower than <c>moov</c>, because it is choosing between demuxers that all
    /// get a vote. This returns a bool from the strong tags alone - <c>moov</c>, <c>mdat</c>,
    /// <c>ftyp</c>, <c>pnot</c>, <c>udta</c> - and leaves the weak ones out rather than
    /// inventing a scoring system for a probe that has no competitors here.
    /// </para>
    /// </remarks>
    private static bool ProbeIsoBoxes(ReadOnlySpan<byte> header)
    {
        int offset = 0;

        while (offset + 8 <= header.Length)
        {
            int minSize = 8;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[offset..]);

            if (size == 1 && offset + 16 <= header.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[(offset + 8)..]);
                minSize = 16;
            }
            else if (size == 0)
            {
                size = header.Length - offset;
            }

            if (size < minSize)
            {
                // "ignore invalid offset" - step by four and look again.
                offset += 4;
                continue;
            }

            uint tag = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset + 4, 4));
            switch (tag)
            {
                case 0x6D6F6F76:    // 'moov'
                case 0x6D646174:    // 'mdat'
                case 0x706E6F74:    // 'pnot' - "movs with preview pics like ew.mov"
                case 0x75647461:    // 'udta'
                case 0x66747970:    // 'ftyp'
                    return true;
            }

            if (size > header.Length - offset)
                break;
            offset += (int)size;
        }

        return false;
    }

    /// <summary>
    /// Probe raw bytes for format detection.
    /// </summary>
    public static DemuxerProbeResult ProbeByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return new DemuxerProbeResult { Success = false };
        
        // Check for DTS-HD (DTSHDHDR). dtshd_probe scores this AVPROBE_SCORE_MAX on the first
        // eight bytes alone, and it has to come before the DTS frame-chain heuristic further
        // down, which would otherwise find core frames in the file's BLACKOUT chunk.
        //
        // The test is DtsHdAudioSource.DtshdProbe's rather than a second copy of the magic
        // here. It was a second copy, and the registry recorded dtshd_probe as verified while
        // nothing called the method that is the translation of it.
        if (DtsHdAudioSource.DtshdProbe(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "dtshd",
                Confidence = 100,
                Type = DemuxerType.DtsHd
            };
        }

        // Check for EBML (Matroska/WebM)
        if (header.StartsWith(MatroskaMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "matroska",
                Confidence = 100,
                Type = DemuxerType.Matroska
            };
        }
        
        // MP4 and QuickTime. This used to test for 'ftyp' at offset 4 and nothing else,
        // which misses every classic QuickTime file: ftyp was added by MP4, and a .mov
        // written before that starts straight in at 'moov' or 'mdat'. ProbeIsoBoxes walks
        // the top-level boxes the way mov_probe does.
        if (ProbeIsoBoxes(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "mp4",
                Confidence = 100,
                Type = DemuxerType.Mp4
            };
        }
        
        // Check for FLV
        if (header.StartsWith(FlvMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "flv",
                Confidence = 100,
                Type = DemuxerType.Flv
            };
        }
        
        // Check for OGG
        if (header.StartsWith(OggMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "ogg",
                Confidence = 100,
                Type = DemuxerType.Ogg
            };
        }
        
        // Check for RIFF (AVI or WAV)
        if (header.StartsWith(AviMagic))
        {
            if (header.Length >= 12)
            {
                // Check for "AVI " or "WAVE" at offset 8
                if (header[8] == 'A' && header[9] == 'V' && header[10] == 'I' && header[11] == ' ')
                {
                    return new DemuxerProbeResult
                    {
                        Success = true,
                        FormatName = "avi",
                        Confidence = 100,
                        Type = DemuxerType.Avi
                    };
                }
                if (header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
                {
                    return new DemuxerProbeResult
                    {
                        Success = true,
                        FormatName = "wav",
                        Confidence = 100,
                        Type = DemuxerType.Wav
                    };
                }
            }
            // Unknown RIFF type, assume AVI
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "avi",
                Confidence = 50,
                Type = DemuxerType.Avi
            };
        }
        
        // Check for AIFF and AIFF-C, which are FORM with AIFF or AIFC at offset 8 - the same
        // shape as RIFF above, big-endian. AiffMagic was declared here and never tested, so
        // this probe could not return DemuxerType.Aiff at all and every .aiff reached the
        // pipeline as "Could not detect media format".
        if (header.StartsWith(AiffMagic) && header.Length >= 12)
        {
            bool aiff = header[8] == 'A' && header[9] == 'I' && header[10] == 'F'
                     && (header[11] == 'F' || header[11] == 'C');
            if (aiff)
            {
                return new DemuxerProbeResult
                {
                    Success = true,
                    FormatName = header[11] == 'C' ? "aiff-c" : "aiff",
                    Confidence = 100,
                    Type = DemuxerType.Aiff
                };
            }
        }
        
        // Check for AU (Sun/NeXT audio)
        if (header.StartsWith(AuMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "au",
                Confidence = 100,
                Type = DemuxerType.Au
            };
        }
        
        // Check for CAF (Core Audio Format). The port of cafdec.c owns the probe, because
        // a second copy of a magic number here is how dtshd_probe came to be translated and
        // never called.
        if (CafDec.Probe(header) > 0)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "caf",
                Confidence = 100,
                Type = DemuxerType.Caf
            };
        }
        
        // Check for SMF/MIDI (MThd header)
        if (header.StartsWith(SmfMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "midi",
                Confidence = 100,
                Type = DemuxerType.Smf
            };
        }
        
        // Check for RIFF MIDI (RMID)
        if (header.StartsWith(RmidMagic) && header.Length >= 12 &&
            header[8] == 'R' && header[9] == 'M' && header[10] == 'I' && header[11] == 'D')
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "midi",
                Confidence = 100,
                Type = DemuxerType.Smf
            };
        }
        
        // Check for VOC (Creative Voice File)
        // The port of vocdec.c owns the probe, including the checksum over the version
        // number that separates a certain match from a merely plausible one.
        if (VocDec.Probe(header) > 0)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "voc",
                Confidence = 100,
                Type = DemuxerType.Voc
            };
        }
        
        // Check for FLAC
        if (header.StartsWith(FlacMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "flac",
                Confidence = 100,
                Type = DemuxerType.Flac
            };
        }
        
        // Check for TTA (True Audio)
        // The port of tta.c owns the probe: the signature alone is four bytes that a
        // text file could carry, and the C also requires the format, channel count,
        // bit depth and sample rate to be non-zero before it claims the file.
        if (TtaDec.Probe(header) > 0)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "tta",
                Confidence = 100,
                Type = DemuxerType.Tta
            };
        }
        
        // Check for XA (EA ADPCM audio)
        // The port of xa.c owns the probe. Unlike the others it scores 50, not 100: the
        // tag is three bytes and the rest of the confidence comes from the header fields
        // being sane, which is exactly what AVPROBE_SCORE_EXTENSION means.
        if (XaDec.Probe(header) > 0)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "xa",
                Confidence = 100,
                Type = DemuxerType.Xa
            };
        }
        
        // Check for PVA (DVB recordings)
        if (PvaDemuxer.CanParse(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "pva",
                Confidence = 100,
                Type = DemuxerType.Pva
            };
        }
        
        // Check for NSV (Nullsoft Streaming Video)
        if (NsvDemuxer.CanParse(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "nsv",
                Confidence = 100,
                Type = DemuxerType.Nsv
            };
        }
        
        // THP. Its magic is only four bytes, so the C's probe also wants a plausible frame
        // rate at offset 16 and ThpDemuxer.Probe keeps both halves.
        if (ThpDemuxer.Probe(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "thp",
                Confidence = 100,
                Type = DemuxerType.Thp
            };
        }

        // SMJPEG before MJPEG, and not only because it is the stronger magic: both use the
        // .mjpg extension, so extension-based selection cannot tell them apart and the eight
        // magic bytes are the only thing that can.
        if (SmjpegDemuxer.Probe(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "smjpeg",
                Confidence = 100,
                Type = DemuxerType.Smjpeg
            };
        }

        // Check for MJPEG (JPEG SOI or MIME boundary)
        if (MjpegDemuxer.CanParse(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "mjpeg",
                Confidence = 80, // Lower confidence since it could be single image
                Type = DemuxerType.Mjpeg
            };
        }
        
        // Check for DV (Digital Video)
        if (DvDemuxer.CanParse(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "dv",
                Confidence = 90,
                Type = DemuxerType.Dv
            };
        }
        
        // IVF: "DKIF", a 32-byte header, then one length and timestamp per frame. The
        // codec is a fourcc inside that header, so one probe covers VP8, VP9 and AV1.
        if (header.Length >= 4 && header[..4].SequenceEqual(IvfMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "ivf",
                Confidence = 100,
                Type = DemuxerType.Ivf
            };
        }

        // Check for Y4M (YUV4MPEG2) raw video format
        if (header.Length >= 9 && header[..9].SequenceEqual(Y4MMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "y4m",
                Confidence = 100,
                Type = DemuxerType.RawVideo
            };
        }
        
        // Check for ASF/WMV/WMA (16-byte GUID header)
        if (header.Length >= 16 && header[..16].SequenceEqual(AsfMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "asf",
                Confidence = 100,
                Type = DemuxerType.Asf
            };
        }
        
        // Check for STL (EBU Subtitles)
        if (StlDemuxer.CanParse(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "stl",
                Confidence = 100,
                Type = DemuxerType.Stl
            };
        }
        
        // Check for MPEG-PS (Pack start code 0x000001BA)
        // Must be checked before MPEG-TS since both can have 0x00 bytes
        if (header.Length >= 4 && header[..4].SequenceEqual(MpegPsMagic))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "mpegps",
                Confidence = 100,
                Type = DemuxerType.MpegPs
            };
        }
        
        // Check for HX IP camera format (HXVS for H.264, HXVT for HEVC)
        if (header.Length >= 4 && (header[..4].SequenceEqual(HxvsMagic) || header[..4].SequenceEqual(HxvtMagic)))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "hx",
                Confidence = 100,
                Type = DemuxerType.Hx
            };
        }
        
        // Check for MPEG-TS. A lone 0x47 is far too weak on its own - it is just 'G' - so
        // require the sync byte to repeat at the 188-byte packet stride whenever the caller gave
        // us enough header to look.
        if (header[0] == 0x47 && IsMpegTsSyncPattern(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "mpegts",
                Confidence = 80,
                Type = DemuxerType.MpegTs
            };
        }
        
        // RAW ELEMENTARY VIDEO STREAMS, after every container probe, for the same reason
        // the audio ones below are last: the evidence is a start code three bytes long, and
        // a start code can occur inside anything. Ordering this before the transport stream
        // probe made fate-suite/mpeg2/mpeg2_field_encoding.ts detect as VC-1 - a 00 00 01 0F
        // somewhere in a TS payload is not a VC-1 sequence header - and the container-naming
        // gate said so immediately.
        //
        // The RCV probe is a fixed-field test rather than a start code, so it could sit
        // anywhere; it stays with its sibling.
        if (ProbeRcv(header))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "vc1test",
                Confidence = 100,
                Type = DemuxerType.Vc1Rcv
            };
        }

        if (Vc1EsDemuxer.ProbeData(header).HasSequenceHeader)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "vc1",
                Confidence = 60,
                Type = DemuxerType.Vc1Es
            };
        }

        // MPEG-4 Part 2, on the one start code that is unambiguous: a Video Object Layer
        // header, 0x000001 followed by 0x20..0x2F. DIVERGENCE from mpeg4video_probe, which
        // also counts VOPs and visual objects and requires no reserved start codes; the VOL
        // is the half that matters, because every other MPEG-4 start code it accepts -
        // 0x00..0x1F among them - is also a PICTURE start code in MPEG-1 and MPEG-2 video,
        // and an MPEG-2 elementary stream would be taken for an MPEG-4 one without it.
        if (Mpeg4VideoDemuxer.Probe(header).HasVideoObjectLayer)
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "m4v",
                Confidence = 60,
                Type = DemuxerType.Mpeg4Video
            };
        }

        // Raw elementary audio streams (Shoutcast/Icecast serve these directly, with no
        // container at all). Checked last because the sync word is only 11-12 bits and would
        // otherwise shadow real container magic.
        var elementary = ProbeElementaryAudio(header);
        if (elementary.Success)
            return elementary;

        return new DemuxerProbeResult { Success = false };
    }

    /// <summary>
    /// True when 0x47 recurs at the 188-byte transport packet stride. Falls back to accepting a
    /// single sync byte only when the caller supplied less than one packet of header, since
    /// nothing better can be said at that length.
    /// </summary>
    private static bool IsMpegTsSyncPattern(ReadOnlySpan<byte> header)
    {
        const int PacketSize = 188;
        if (header.Length < PacketSize * 2)
            return true;

        int checks = Math.Min(4, (header.Length - 1) / PacketSize);
        for (int i = 1; i <= checks; i++)
        {
            if (header[i * PacketSize] != 0x47)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Detects a raw MPEG audio (MP3) or ADTS AAC elementary stream, skipping an ID3v2 tag if one
    /// is present. Both start with an all-ones sync word, so a single header is not enough to tell
    /// them apart from arbitrary data - a chain of frames is followed, each one landing exactly on
    /// the next sync word, which is what makes the result trustworthy.
    /// </summary>
    private static DemuxerProbeResult ProbeElementaryAudio(ReadOnlySpan<byte> header)
    {
        int offset = 0;
        bool hasId3 = header.Length >= 10 && header[0] == 'I' && header[1] == 'D' && header[2] == '3';
        if (hasId3)
        {
            // ID3v2 size is a syncsafe integer: 7 significant bits per byte.
            int tagSize = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) |
                          ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            offset = 10 + tagSize;

            if (offset >= header.Length)
            {
                // The tag runs past the probe buffer, so no frame is reachable. An ID3v2 tag is
                // overwhelmingly MPEG audio, but say so at low confidence rather than pretending
                // to have verified it - and let the demuxer, which reads the whole tag, decide.
                return new DemuxerProbeResult
                {
                    Success = true,
                    FormatName = "mp3",
                    Confidence = 40,
                    Type = DemuxerType.Mp3
                };
            }
        }

        var body = header[offset..];

        // ADTS first: its layer field is 00, whereas MPEG audio layers are 01..11, so a valid
        // ADTS header is never a valid MP3 header and the order does not create ambiguity.
        if (IsFrameChainConvincing(CountAdtsFrames(body, out bool adtsPastEnd), adtsPastEnd))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "aac",
                Confidence = hasId3 ? 90 : 80,
                Type = DemuxerType.Adts
            };
        }

        if (IsFrameChainConvincing(CountAc3Frames(body, out bool ac3PastEnd), ac3PastEnd))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "ac3",
                Confidence = hasId3 ? 90 : 80,
                Type = DemuxerType.Ac3
            };
        }

        if (IsFrameChainConvincing(CountDtsFrames(body, out bool dtsPastEnd), dtsPastEnd))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "dts",
                Confidence = hasId3 ? 90 : 80,
                Type = DemuxerType.Dts
            };
        }

        if (IsFrameChainConvincing(CountMpegAudioFrames(body, out bool mpegPastEnd), mpegPastEnd))
        {
            return new DemuxerProbeResult
            {
                Success = true,
                FormatName = "mp3",
                Confidence = hasId3 ? 90 : 80,
                Type = DemuxerType.Mp3
            };
        }

        return new DemuxerProbeResult { Success = false };
    }

    private const int ElementaryProbeFrames = 4;

    /// <summary>
    /// A chain is convincing when several frames link up, or when a single frame parsed cleanly
    /// and its own length carried past the end of what we were given - there is nothing further to
    /// check in that case, and refusing would reject any stream whose frames exceed the buffer.
    /// </summary>
    private static bool IsFrameChainConvincing(int frames, bool ranPastEnd) =>
        frames >= 2 || (frames == 1 && ranPastEnd);

    private static int CountAdtsFrames(ReadOnlySpan<byte> data, out bool ranPastEnd)
    {
        ranPastEnd = false;
        int offset = 0, frames = 0;
        while (frames < ElementaryProbeFrames && offset + 7 <= data.Length)
        {
            if (!AdtsParser.TryParseHeader(data[offset..], out var adts) || adts.FrameLength < 7)
                break;
            frames++;
            offset += adts.FrameLength;
            if (offset > data.Length)
            {
                ranPastEnd = true;
                break;
            }
        }
        return frames;
    }

    /// <summary>
    /// Count linked DTS core frames at the head of an elementary stream.
    /// </summary>
    /// <remarks>
    /// Only the big-endian 16-bit layout is probed. The other three - little-endian and the two
    /// 14-bit forms - exist and Dca.ConvertBitstream handles them, but a raw file in one of them
    /// is rare enough that probing for it is not worth the false positives, and a file that
    /// reaches the pipeline undetected says so loudly rather than decoding to noise.
    /// </remarks>
    private static int CountDtsFrames(ReadOnlySpan<byte> data, out bool ranPastEnd)
    {
        ranPastEnd = false;
        int offset = 0, frames = 0;
        while (frames < ElementaryProbeFrames && offset + 18 <= data.Length)
        {
            var header = default(MediaKernel.Core.Codecs.Audio.Dts.DcaCoreFrameHeader);
            var reader = default(MediaKernel.Core.Codecs.MpegVideo.GetBitContext);
            byte[] window = data.Slice(offset, Math.Min(32, data.Length - offset)).ToArray();
            reader.InitGetBits8(window, window.Length);

            if (MediaKernel.Core.Codecs.Audio.Dts.Dca.ParseCoreFrameHeader(ref header, ref reader)
                != MediaKernel.Core.Codecs.Audio.Dts.DcaParseError.None)
                break;

            frames++;
            offset += header.FrameSize;
            if (offset > data.Length)
            {
                ranPastEnd = true;
                break;
            }
        }
        return frames;
    }

    private static int CountAc3Frames(ReadOnlySpan<byte> data, out bool ranPastEnd)
    {
        ranPastEnd = false;
        int offset = 0, frames = 0;
        while (frames < ElementaryProbeFrames && offset + 7 <= data.Length)
        {
            if (!Ac3FrameParser.TryParseHeader(data[offset..], out var ac3) || ac3.FrameSize < 7)
                break;
            frames++;
            offset += ac3.FrameSize;
            if (offset > data.Length)
            {
                ranPastEnd = true;
                break;
            }
        }
        return frames;
    }

    private static int CountMpegAudioFrames(ReadOnlySpan<byte> data, out bool ranPastEnd)
    {
        ranPastEnd = false;
        int offset = 0, frames = 0;
        while (frames < ElementaryProbeFrames && offset + 4 <= data.Length)
        {
            if (!Mp3FrameParser.TryParseHeader(data[offset..], out var mpeg) || mpeg.FrameSize < 4)
                break;
            frames++;
            offset += mpeg.FrameSize;
            if (offset > data.Length)
            {
                ranPastEnd = true;
                break;
            }
        }
        return frames;
    }
    
    /// <summary>
    /// Create a demuxer for the specified type.
    /// </summary>
    public static IDisposable Create(DemuxerType type, Stream stream)
    {
        return type switch
        {
            DemuxerType.Mp4 => new Mp4Demuxer(stream),
            DemuxerType.Matroska => new MatroskaDemuxer(stream),
            DemuxerType.Avi => new AviDemuxer(stream),
            DemuxerType.Flv => new FlvDemuxer(stream),
            DemuxerType.MpegTs => new TsDemuxer(stream),
            DemuxerType.Ogg => new OggDemuxer(stream),
            DemuxerType.Wav => new WavDemuxer(stream),
            DemuxerType.MpegPs => CreateMpegPsDemuxer(stream),
            DemuxerType.Hx => new HxDemuxer(stream),
            _ => throw new NotSupportedException($"No demuxer available for type: {type}")
        };
    }
    
    private static MpegPsDemuxer CreateMpegPsDemuxer(Stream stream)
    {
        var demuxer = new MpegPsDemuxer();
        demuxer.Initialize(stream);
        return demuxer;
    }
    
    /// <summary>
    /// Create an MP4 demuxer if the probe detects MP4 format.
    /// </summary>
    public static async Task<Mp4Demuxer?> CreateMp4DemuxerAsync(
        MediaInput input,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(input, cancellationToken);
        if (probe.Type != DemuxerType.Mp4)
            return null;
        return new Mp4Demuxer(input.AsStream());
    }
    
    /// <summary>
    /// Create a Matroska demuxer if the probe detects MKV/WebM format.
    /// </summary>
    public static async Task<MatroskaDemuxer?> CreateMatroskaDemuxerAsync(
        MediaInput input,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(input, cancellationToken);
        if (probe.Type != DemuxerType.Matroska)
            return null;
        return new MatroskaDemuxer(input.AsStream());
    }
    
    /// <summary>
    /// Create a demuxer by probing the input.
    /// </summary>
    public static async Task<IDisposable> CreateAsync(
        MediaInput input,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(input, cancellationToken);
        
        if (!probe.Success)
            throw new NotSupportedException("Could not detect media format");
        
        return Create(probe.Type, input.AsStream());
    }
    
    /// <summary>
    /// Create a demuxer for a URI (file path or URL).
    /// </summary>
    public static async Task<(IDisposable demuxer, MediaInput input)> CreateFromUriAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        var input = MediaInput.Create(uri);
        await input.OpenAsync(cancellationToken);
        
        var demuxer = await CreateAsync(input, cancellationToken);
        return (demuxer, input);
    }
    
    private static DemuxerType FormatNameToType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "mp4" or "mov" or "m4a" or "m4v" => DemuxerType.Mp4,
            "matroska" or "mkv" or "webm" => DemuxerType.Matroska,
            "avi" => DemuxerType.Avi,
            "flv" => DemuxerType.Flv,
            "mpegts" or "ts" or "mts" or "m2ts" => DemuxerType.MpegTs,
            "ogg" or "ogv" or "oga" => DemuxerType.Ogg,
            "wav" or "wave" => DemuxerType.Wav,
            "midi" or "mid" or "kar" or "rmi" => DemuxerType.Smf,
            "voc" => DemuxerType.Voc,
            "flac" => DemuxerType.Flac,
            "dtshd" => DemuxerType.DtsHd,
            "dts" or "cpt" => DemuxerType.Dts,
            "tta" => DemuxerType.Tta,
            "xa" => DemuxerType.Xa,
            "pva" => DemuxerType.Pva,
            "nsv" => DemuxerType.Nsv,
            "cdg" => DemuxerType.Cdg,
            "smjpeg" => DemuxerType.Smjpeg,
            "thp" => DemuxerType.Thp,
            "mjpeg" or "mjpg" => DemuxerType.Mjpeg,
            "rawaud" or "pcm" or "raw" => DemuxerType.RawAudio,
            "rawvid" or "yuv" or "y4m" => DemuxerType.RawVideo,
            "ivf" => DemuxerType.Ivf,
            "vc1" => DemuxerType.Vc1Es,
            "vc1test" or "rcv" => DemuxerType.Vc1Rcv,
            "m4v" or "mpeg4video" => DemuxerType.Mpeg4Video,
            "dv" or "dif" => DemuxerType.Dv,
            "stl" => DemuxerType.Stl,
            "asf" or "wmv" or "wma" => DemuxerType.Asf,
            "mpegps" or "ps" or "vob" or "mpeg" or "mpg" => DemuxerType.MpegPs,
            "vobsub" or "idx" => DemuxerType.VobSub,
            "hx" => DemuxerType.Hx,
            _ => DemuxerType.Unknown
        };
    }
}

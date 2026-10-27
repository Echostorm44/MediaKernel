// Ported from FFmpeg's libavformat/vc1test.c - the "VC-1 test bitstream" container, which is
// what the .rcv files in SMPTE's conformance set are.
//
// It exists because Simple and Main Profile VC-1 have no start codes: the bitstream alone
// cannot be split into pictures, so a conformance stream has to carry its framing with it.
// This container does the least that makes that possible - a short header holding the four
// extradata bytes and the picture size, then a length and a timestamp before each picture.

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>One picture of a VC-1 test bitstream, with the framing the container supplies.</summary>
public readonly struct Vc1TestPacket
{
    /// <summary>The coded picture.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The presentation timestamp, in the stream's own time base.</summary>
    public uint Pts { get; init; }

    /// <summary>Whether this picture is a key frame.</summary>
    public bool IsKeyframe { get; init; }
}

/// <summary>
/// <c>ff_vc1t_demuxer</c> (<c>vc1test.c</c>) - the VC-1 test bitstream container, <c>.rcv</c>.
/// </summary>
public sealed class Vc1TestDemuxer : IDisposable
{
    /// <summary><c>VC1_EXTRADATA_SIZE</c>.</summary>
    private const int Vc1ExtradataSize = 4;

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] header = new byte[8];
    private byte[] packetBuffer = [];
    private bool parsed;

    /// <summary>The four extradata bytes, which are a Main Profile sequence header.</summary>
    public byte[] Extradata { get; private set; } = [];

    /// <summary><c>width</c>, from the container and nowhere else.</summary>
    public int Width { get; private set; }

    /// <summary><c>height</c>.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// <c>frames</c> - how many pictures the header claims, which is the stream's duration in
    /// the frame-rate time base.
    /// </summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// <c>fps</c>. 0xFFFFFFFF means the pictures carry millisecond timestamps instead.
    /// </summary>
    public uint FrameRate { get; private set; }

    /// <summary>
    /// The codec these streams always hold. The container fixes it, which is what lets a
    /// four-byte sequence header be read as WMV3's layout rather than scanned for markers.
    /// </summary>
    public string Codec => "wmv3";

    /// <summary>Opens a demuxer over a VC-1 test bitstream.</summary>
    public Vc1TestDemuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>
    /// <c>vc1t_read_header</c>.
    /// </summary>
    /// <remarks>
    /// Every check the C makes is kept, because each one is the only thing standing between a
    /// file of the wrong kind and a decoder being handed four arbitrary bytes as a sequence
    /// header: the 0xC5 marker, a struct_c of at least four bytes, and the 0xC that says the
    /// following struct_a is the one this layout expects.
    /// </remarks>
    /// <returns>False when the file is not a VC-1 test bitstream.</returns>
    public bool Parse()
    {
        if (parsed) return Width > 0;
        parsed = true;

        Span<byte> buf = stackalloc byte[16];

        if (!ReadExactly(buf[..8])) return false;
        FrameCount = buf[0] | (buf[1] << 8) | (buf[2] << 16);       // avio_rl24
        if (buf[3] != 0xC5) return false;

        uint structCSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        if (structCSize < Vc1ExtradataSize) return false;

        Extradata = new byte[Vc1ExtradataSize];
        if (!ReadExactly(Extradata)) return false;

        if (!Skip((long)structCSize - Vc1ExtradataSize)) return false;

        if (!ReadExactly(buf[..12])) return false;
        Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf);
        Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]) != 0xC) return false;

        if (!Skip(8)) return false;

        if (!ReadExactly(buf[..4])) return false;
        FrameRate = BinaryPrimitives.ReadUInt32LittleEndian(buf);

        return Width > 0 && Height > 0;
    }

    /// <summary>
    /// <c>vc1t_read_packet</c> - one picture, behind its three-byte length, flag byte and
    /// timestamp.
    /// </summary>
    public bool ReadPacket(out Vc1TestPacket packet)
    {
        packet = default;
        if (!parsed && !Parse()) return false;

        if (!ReadExactly(header)) return false;      // avio_feof: a short read is the end

        int frameSize = header[0] | (header[1] << 8) | (header[2] << 16);
        bool keyframe = (header[3] & 0x80) != 0;
        uint pts = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

        if (frameSize <= 0) return false;
        if (packetBuffer.Length < frameSize)
            packetBuffer = new byte[frameSize];
        if (!ReadExactly(packetBuffer.AsSpan(0, frameSize))) return false;

        packet = new Vc1TestPacket
        {
            Data = packetBuffer.AsMemory(0, frameSize),
            Pts = pts,
            IsKeyframe = keyframe,
        };
        return true;
    }

    private bool ReadExactly(Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int n = stream.Read(destination[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private bool Skip(long count)
    {
        if (count < 0) return false;
        if (count == 0) return true;

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return true;
        }

        Span<byte> sink = stackalloc byte[256];
        while (count > 0)
        {
            int want = (int)Math.Min(count, sink.Length);
            if (!ReadExactly(sink[..want])) return false;
            count -= want;
        }
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!leaveOpen)
            stream.Dispose();
    }
}

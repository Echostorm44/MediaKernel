// Ported from FFmpeg's libavformat/thp.c - the Nintendo GameCube THP container.
//
// A THP file is a fixed header, a component list, and then a chain of frames: every frame
// states the size of the NEXT one, so the file is walked forward and has no index. Each frame
// holds the video and, when the file has audio, the audio for that frame immediately after it
// - which is why reading a packet here is a two-state affair: a video read remembers how much
// audio follows, and the next call hands that over before moving on.

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>One packet of a THP file.</summary>
public readonly struct ThpPacket
{
    /// <summary>The payload.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The stream this belongs to, as <see cref="ThpDemuxer"/> numbered them.</summary>
    public int StreamIndex { get; init; }

    /// <summary>Whether this is the video stream's packet.</summary>
    public bool IsVideo { get; init; }

    /// <summary>The frame this packet belongs to, counting from zero.</summary>
    public int FrameIndex { get; init; }
}

/// <summary>
/// <c>ff_thp_demuxer</c> (<c>thp.c</c>) - the GameCube THP container.
/// </summary>
public sealed class ThpDemuxer : IDisposable
{
    /// <summary><c>MKTAG('T','H','P','\0')</c>, read big-endian.</summary>
    private static ReadOnlySpan<byte> Magic => [(byte)'T', (byte)'H', (byte)'P', 0];

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private byte[] packetBuffer = [];

    private bool headerRead;
    private long nextFrame;
    private uint nextFrameSize;
    private uint audioSize;
    private int frame;

    /// <summary>Opens a demuxer over a THP file.</summary>
    public ThpDemuxer(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    /// <summary><c>thp-&gt;version</c>.</summary>
    public uint Version { get; private set; }

    /// <summary><c>thp-&gt;framecnt</c>.</summary>
    public uint FrameCount { get; private set; }

    /// <summary>The frame rate's numerator - <c>thp-&gt;fps.num</c>.</summary>
    public int FpsNum { get; private set; }

    /// <summary>The frame rate's denominator.</summary>
    public int FpsDen { get; private set; } = 1;

    /// <summary><c>codecpar-&gt;width</c> of the video component.</summary>
    public int Width { get; private set; }

    /// <summary><c>codecpar-&gt;height</c>.</summary>
    public int Height { get; private set; }

    /// <summary>The index this demuxer gave the video stream, or -1.</summary>
    public int VideoStreamIndex { get; private set; } = -1;

    /// <summary>The index of the audio stream, or -1 when the file has none.</summary>
    public int AudioStreamIndex { get; private set; } = -1;

    /// <summary>The audio component's channel count.</summary>
    public int Channels { get; private set; }

    /// <summary>The audio component's sample rate.</summary>
    public int SampleRate { get; private set; }

    /// <summary>The codec id the video component always has.</summary>
    public string VideoCodecId => "thp";

    /// <summary>The codec id the audio component always has.</summary>
    public string AudioCodecId => "adpcm_thp";

    /// <summary><c>thp_probe</c>.</summary>
    /// <remarks>
    /// The magic alone is only a QUARTER score in the C unless the frame rate at offset 16 -
    /// a big-endian IEEE float - is between 0.1 and 1000. Both halves are kept: four bytes
    /// of magic is weak enough that another format could carry them.
    /// </remarks>
    public static bool Probe(ReadOnlySpan<byte> header)
    {
        if (header.Length < 20 || !header[..4].SequenceEqual(Magic))
            return false;

        float fps = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32BigEndian(header[16..]));
        return !float.IsNaN(fps) && fps >= 0.1f && fps <= 1000f;
    }

    /// <summary>
    /// <c>thp_read_header</c> - the fixed header and the component list.
    /// </summary>
    /// <returns>False when the file is not a THP, or declares no video component.</returns>
    public bool ReadHeader()
    {
        if (headerRead)
            return VideoStreamIndex >= 0;
        headerRead = true;

        Span<byte> buf = stackalloc byte[44];
        if (!ReadExactly(buf))
            return false;

        if (!buf[..4].SequenceEqual(Magic))
            return false;

        Version = Be32(buf[4..]);

        // buf[8..12] is the maximum buffer size and buf[12..16] the maximum sample count.
        // The C ignores both and allocates a packet per frame; this project may not, so the
        // buffer is sized ONCE from the file's own figure. A THP's frames grow as the film
        // goes on, and without this the buffer doubles its way up through the stream - the
        // no-alloc gate measured the growth as 6,055 bytes a read, and geometric growth
        // alone still left a doubling inside its measured window. Clamped, because the
        // field is a hint and a corrupt one must not allocate the file.
        uint maxBufferSize = Be32(buf[8..]);
        if (maxBufferSize > 0 && maxBufferSize <= 64 * 1024 * 1024
            && maxBufferSize <= (ulong)stream.Length)
        {
            packetBuffer = new byte[maxBufferSize];
        }

        float fps = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(buf[16..]));
        if (!RationalFromDouble(fps, out int num, out int den) || den <= 0 || num < 0)
            return false;
        FpsNum = num;
        FpsDen = den;

        FrameCount = Be32(buf[20..]);
        uint firstFrameSize = Be32(buf[24..]);
        // buf[28..32] is the maximum frame size, used by the C only to bound its reads.
        uint compOff = Be32(buf[32..]);
        // buf[36..40] is offsetDataOffset, which nothing reads.
        uint firstFrame = Be32(buf[40..]);

        nextFrameSize = firstFrameSize;
        nextFrame = firstFrame;

        // The component structure, which is elsewhere in the file.
        if (compOff >= stream.Length)
            return false;
        stream.Position = compOff;

        Span<byte> compHeader = stackalloc byte[20];
        if (!ReadExactly(compHeader))
            return false;

        uint compCount = Be32(compHeader);
        if (compCount > 16)
            return false;

        // The 16 type bytes follow the count, and the per-component data follows those.
        ReadOnlySpan<byte> components = compHeader[4..20];

        Span<byte> field = stackalloc byte[12];
        int streamCount = 0;

        for (int i = 0; i < compCount; i++)
        {
            if (components[i] == 0)
            {
                if (VideoStreamIndex >= 0)
                    break;              // "if (thp->vst) break" - one video component only

                if (!ReadExactly(field[..8]))
                    return false;
                Width = (int)Be32(field);
                Height = (int)Be32(field[4..]);
                VideoStreamIndex = streamCount++;

                // Version 0x11000 carries one more field the C reads and discards.
                if (Version == 0x11000 && !ReadExactly(field[..4]))
                    return false;
            }
            else if (components[i] == 1)
            {
                if (AudioStreamIndex >= 0)
                    break;

                if (!ReadExactly(field[..12]))
                    return false;
                Channels = (int)Be32(field);
                SampleRate = (int)Be32(field[4..]);
                // field[8..12] is the audio stream's duration in samples.
                AudioStreamIndex = streamCount++;
            }
        }

        return VideoStreamIndex >= 0;
    }

    /// <summary>
    /// <c>thp_read_packet</c> - the next video packet, or the audio that followed the last one.
    /// </summary>
    /// <returns>False at the end of the last frame.</returns>
    public bool ReadPacket(out ThpPacket packet)
    {
        packet = default;
        if (!headerRead && !ReadHeader())
            return false;

        if (audioSize == 0)
        {
            if (frame >= FrameCount)
                return false;

            stream.Position = nextFrame;

            // Every frame states the size of the NEXT one, so the chain is walked forward.
            // FFMAX(size, 1) is what stops a zero size looping on the same frame forever.
            nextFrame += Math.Max(nextFrameSize, 1u);

            Span<byte> head = stackalloc byte[16];
            int want = AudioStreamIndex >= 0 ? 16 : 12;
            if (!ReadExactly(head[..want]))
                return false;

            nextFrameSize = Be32(head);
            // head[4..8] is the previous frame's total size, which nothing reads.
            uint size = Be32(head[8..]);

            if (AudioStreamIndex >= 0)
                audioSize = Be32(head[12..]);
            else
                frame++;

            if (!TryReadPayload(size, out ReadOnlyMemory<byte> data))
                return false;

            packet = new ThpPacket
            {
                Data = data,
                StreamIndex = VideoStreamIndex,
                IsVideo = true,
                FrameIndex = AudioStreamIndex >= 0 ? frame : frame - 1,
            };
            return true;
        }

        {
            if (!TryReadPayload(audioSize, out ReadOnlyMemory<byte> data))
                return false;

            packet = new ThpPacket
            {
                Data = data,
                StreamIndex = AudioStreamIndex,
                IsVideo = false,
                FrameIndex = frame,
            };

            audioSize = 0;
            frame++;
            return true;
        }
    }

    /// <summary>
    /// <c>av_d2q</c>, far enough for a frame rate. The C refuses the file when it fails.
    /// </summary>
    /// <remarks>
    /// A THP's frame rate is an IEEE float, and the C turns it into a rational with
    /// <c>av_d2q(x, INT_MAX)</c>. Only the DENOMINATOR is used - the time base is
    /// <c>1/fps</c> - so an exact continued-fraction expansion is more than this needs, but
    /// a rate like 29.97 has to come out as something better than 2997/100 or the timestamps
    /// drift. A simple power-of-ten scaling reduced by the GCD does that.
    /// </remarks>
    private static bool RationalFromDouble(double value, out int num, out int den)
    {
        num = 0;
        den = 1;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return false;

        const int Scale = 1000000;
        long n = (long)Math.Round(value * Scale);
        long d = Scale;
        if (n <= 0)
            return false;

        long g = Gcd(n, d);
        n /= g;
        d /= g;
        if (n > int.MaxValue || d > int.MaxValue)
            return false;

        num = (int)n;
        den = (int)d;
        return true;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private bool TryReadPayload(uint size, out ReadOnlyMemory<byte> data)
    {
        data = default;
        if (size > int.MaxValue)
            return false;

        // GEOMETRIC, not exact. A THP's frames get bigger as the film goes on, so an
        // exact-size buffer reallocates on most of them: the no-alloc gate measured 6,055
        // bytes a read over 38 reads of this file, which is the demuxer growing one frame at
        // a time for the length of the stream. Doubling settles it inside the warm-up.
        if (packetBuffer.Length < size)
            packetBuffer = new byte[Math.Max((int)size, packetBuffer.Length * 2)];

        // "if (ret != size) return AVERROR_INVALIDDATA" - a short read is the end of a
        // truncated file and not a short packet.
        if (ReadUpTo(packetBuffer.AsSpan(0, (int)size)) != (int)size)
            return false;

        data = packetBuffer.AsMemory(0, (int)size);
        return true;
    }

    private static uint Be32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32BigEndian(b);

    private bool ReadExactly(Span<byte> destination) =>
        ReadUpTo(destination) == destination.Length;

    private int ReadUpTo(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        packetBuffer = [];
        if (!leaveOpen)
            stream.Dispose();
    }
}

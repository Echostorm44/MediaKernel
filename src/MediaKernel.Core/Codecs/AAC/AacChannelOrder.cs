// libavcodec/aac/aacdec.c's count_paired_channels, assign_pair, assign_channels and
// sniff_channel_order: the order a program config element's channels come out in.
//
// A channelConfig of 0 means "the program config element is the configuration", and the PCE does
// not list channels - it lists elements in four position groups (front, side, back, LFE). Working
// out which output channel each element feeds is not a table lookup: FFmpeg walks each group,
// hands the group's odd leading single-channel element the centre position, hands each pair the
// next two positions from ff_aac_channel_map, and then stable-sorts the elements by the AV channel
// position they landed on.
//
// The sort is why this cannot be approximated. A 5.1 stream that declares three front elements and
// no back elements - which is how the ISO conformance streams al15_44 and al_sbr_cm_48_5.1 are
// written - gives its FIRST channel pair the front-left-of-centre and front-right-of-centre
// positions and its SECOND pair plain front left and right. Sorting then puts the second pair
// first, so the output order is the second pair, the single channel, the LFE, then the first pair.
// Assigning by element order or by instance tag instead produces a decode where every channel is
// bit-exact and two of them are in each other's places, which sounds like a broken decoder and
// measures like one.

using System;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// Which output channel each element of a program config element feeds, by FFmpeg's rules.
/// </summary>
internal static class AacChannelOrder
{
    /// <summary><c>AAC_CHANNEL_*</c>: the position group an element was declared in.</summary>
    public enum Position
    {
        Front = 1,
        Side = 2,
        Back = 3,
        Lfe = 4,
    }

    // The AV_CHAN_* ordinals ff_aac_channel_map names. The sort compares the bit each one sets,
    // not the ordinal, so these values and their order both matter.
    private const int None = -1;
    private const int Unused = 0x200;
    private const int FrontLeft = 0, FrontRight = 1, FrontCenter = 2, LowFrequency = 3;
    private const int BackLeft = 4, BackRight = 5;
    private const int FrontLeftOfCenter = 6, FrontRightOfCenter = 7, BackCenter = 8;
    private const int SideLeft = 9, SideRight = 10, TopCenter = 11;
    private const int TopFrontLeft = 12, TopFrontCenter = 13, TopFrontRight = 14;
    private const int TopBackLeft = 15, TopBackCenter = 16, TopBackRight = 17;
    private const int LowFrequency2 = 35, TopSideLeft = 36, TopSideRight = 37;
    private const int BottomFrontCenter = 38, BottomFrontLeft = 39, BottomFrontRight = 40;

    /// <summary><c>ff_aac_channel_map[3][4][6]</c>.</summary>
    private static readonly int[][][] ChannelMap =
    [
        [
            [FrontCenter, FrontLeftOfCenter, FrontRightOfCenter, FrontLeft, FrontRight, None],
            [Unused, None, None, None, None, None],
            [Unused, SideLeft, SideRight, BackLeft, BackRight, BackCenter],
            [LowFrequency, LowFrequency2, None, None, None, None],
        ],
        [
            [TopFrontCenter, None, None, TopFrontLeft, TopFrontRight, None],
            [Unused, TopSideLeft, TopSideRight, None, None, TopCenter],
            [Unused, None, None, TopBackLeft, TopBackRight, TopBackCenter],
            [None, None, None, None, None, None],
        ],
        [
            [BottomFrontCenter, None, None, BottomFrontLeft, BottomFrontRight, None],
            [None, None, None, None, None, None],
            [None, None, None, None, None, None],
            [None, None, None, None, None, None],
        ],
    ];

    /// <summary>One element of the PCE, with the position group it was declared in.</summary>
    public struct Entry
    {
        public SyntaxElementType Type;
        public int InstanceTag;
        public Position Group;

        /// <summary>The AV channel positions this element ended up with, as a bit set.</summary>
        public ulong AvPosition;
    }

    /// <summary>
    /// <c>count_paired_channels</c>: how many channels the run of elements at
    /// <paramref name="current"/> that share <paramref name="position"/> carries, or -1 if that
    /// run cannot be paired up.
    /// </summary>
    private static int CountPairedChannels(Entry[] map, int tags, Position position, int current)
    {
        int channels = 0;
        bool firstPair = false;
        bool singleParity = false;

        for (int i = current; i < tags; i++)
        {
            if (map[i].Group != position)
                break;

            if (map[i].Type == SyntaxElementType.ChannelPairElement)
            {
                if (singleParity)
                {
                    if (position == Position.Front && !firstPair)
                        singleParity = false;
                    else
                        return -1;
                }

                channels += 2;
                firstPair = true;
            }
            else
            {
                channels++;
                if (position != Position.Lfe)
                    singleParity = !singleParity;
            }
        }

        return channels;
    }

    /// <summary>
    /// <c>assign_pair</c>: give one pair of positions to a channel pair element, or to two
    /// consecutive single channel elements. Returns how many entries it consumed.
    /// </summary>
    private static int AssignPair(Entry[] order, Entry[] map, int offset,
                                  ulong left, ulong right, Position position)
    {
        if (map[offset].Type == SyntaxElementType.ChannelPairElement)
        {
            order[offset] = map[offset];
            order[offset].AvPosition = left | right;
            order[offset].Group = position;
            return 1;
        }

        order[offset] = map[offset];
        order[offset].AvPosition = left;
        order[offset].Group = position;
        order[offset + 1] = map[offset + 1];
        order[offset + 1].AvPosition = right;
        order[offset + 1].Group = position;
        return 2;
    }

    /// <summary><c>assign_channels</c>. False where the C returns -1.</summary>
    private static bool AssignChannels(Entry[] order, Entry[] map, int tags,
                                       int layer, Position position, ref int current)
    {
        int i = current;
        int channels = CountPairedChannels(map, tags, position, i);
        if (channels < 0 || channels > 5)
            return true;   // the C returns 0 here: nothing assigned, not a failure

        int[] row = ChannelMap[layer][(int)position - 1];

        if (position == Position.Lfe)
        {
            int k = 0;
            while (channels > 0)
            {
                if (row[k] == None) return false;
                order[i] = map[i];
                order[i].AvPosition = 1UL << row[k];
                order[i].Group = position;
                i++;
                k++;
                channels--;
            }

            current = i;
            return true;
        }

        // An odd leading single channel element takes the group's centre position.
        while ((channels & 1) != 0)
        {
            if (row[0] == None) return false;
            if (row[0] == Unused) break;
            order[i] = map[i];
            order[i].AvPosition = 1UL << row[0];
            order[i].Group = position;
            i++;
            channels--;
        }

        int j = position != Position.Side && channels <= 3 ? 3 : 1;
        while (channels >= 2)
        {
            if (row[j] == None || row[j + 1] == None) return false;
            i += AssignPair(order, map, i, 1UL << row[j], 1UL << row[j + 1], position);
            j += 2;
            channels -= 2;
        }

        while ((channels & 1) != 0)
        {
            if (row[5] == None) return false;
            order[i] = map[i];
            order[i].AvPosition = 1UL << row[5];
            order[i].Group = position;
            i++;
            channels--;
        }

        if (channels != 0)
            return false;

        current = i;
        return true;
    }

    /// <summary>
    /// <c>sniff_channel_order</c>: the elements of <paramref name="map"/> in output order, or null
    /// when the layout cannot be worked out and the caller should fall back.
    /// </summary>
    /// <remarks>
    /// The 22.2 reordering the C does after the sort is not translated: no stream in this project
    /// uses it, and a wrong guess at it would be worse than falling back. Everything else follows
    /// the C, including the insertion sort, which is stable - elements that landed on the same
    /// position keep their PCE order.
    /// </remarks>
    public static Entry[]? SniffChannelOrder(Entry[] map, int tags)
    {
        if (tags <= 0 || tags > map.Length)
            return null;

        var order = new Entry[tags];
        int i = 0;

        for (int layer = 0; layer < 3 && i < tags; layer++)
        {
            if (!AssignChannels(order, map, tags, layer, Position.Front, ref i)) return null;
            if (!AssignChannels(order, map, tags, layer, Position.Side, ref i)) return null;
            if (!AssignChannels(order, map, tags, layer, Position.Back, ref i)) return null;
            if (!AssignChannels(order, map, tags, layer, Position.Lfe, ref i)) return null;
        }

        if (i != tags)
            return null;

        // "utilize the AV channel position define as a stable sort". A pair's position is the two
        // bits it occupies, so the comparison is on the combined bit set, not on a channel index.
        int n = i;
        do
        {
            int next = 0;
            for (int k = 1; k < n; k++)
                if (order[k - 1].AvPosition > order[k].AvPosition)
                {
                    (order[k - 1], order[k]) = (order[k], order[k - 1]);
                    next = k;
                }
            n = next;
        }
        while (n > 0);

        return order;
    }
}

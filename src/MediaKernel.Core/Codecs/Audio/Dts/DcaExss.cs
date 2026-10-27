// DTS extension substream header - a port of ffmpeg-master/libavcodec/dca_exss.c.
//
// Every DTS-HD frame is a core frame followed by an extension substream, and the EXSS header is
// a directory: it says how long the substream is, how many assets it holds, and for each asset
// which extensions it carries and at what byte offset. Nothing in the EXSS is audio. That is
// what makes it cheap and what makes it a prerequisite - XBR, XLL, LBR and the EXSS forms of
// XXCH and X96 are all reached only through the offsets this produces.
//
// Most of the header is skipped rather than stored, and that is the C's doing, not a shortcut:
// the asset descriptor carries language tags, textual information, speaker remapping sets, DRC
// codes and whole mixing configurations that FFmpeg's decoder has no use for. They still have
// to be parsed bit for bit, because every field's position depends on the ones before it.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>DCAExssAsset</c> - one audio asset's descriptor, fields in declared order.</summary>
internal struct DcaExssAsset
{
    public int AssetOffset;
    public int AssetSize;
    public int AssetIndex;

    public int PcmBitRes;
    public int MaxSampleRate;
    public int NchannelsTotal;
    public int OneToOneMapChToSpkr;
    public int EmbeddedStereo;
    public int Embedded6Ch;
    public int SpkrMaskEnabled;
    public int SpkrMask;
    public int RepresentationType;

    public int CodingMode;
    public int ExtensionMask;

    public int CoreOffset;
    public int CoreSize;

    public int XbrOffset;
    public int XbrSize;

    public int XxchOffset;
    public int XxchSize;

    public int X96Offset;
    public int X96Size;

    public int LbrOffset;
    public int LbrSize;

    public int XllOffset;
    public int XllSize;
    public int XllSyncPresent;
    public int XllDelayNframes;
    public int XllSyncOffset;

    public int HdStreamId;
}

/// <summary><c>DCAExssParser</c>.</summary>
internal sealed class DcaExssParser
{
    private GetBitContext gb;

    public int ExssIndex;
    public int ExssSizeNbits;
    public int ExssSize;

    public int StaticFieldsPresent;
    public int Npresents;
    public int Nassets;

    public int MixMetadataEnabled;
    public int Nmixoutconfigs;
    private readonly int[] nmixoutchs = new int[4];

    /// <summary>
    /// <c>assets[1]</c>. FFmpeg declares one and refuses a stream with more, so a single asset
    /// is the C's limit and not a simplification here.
    /// </summary>
    public DcaExssAsset Asset;

    /// <summary>Why the last <see cref="Parse"/> returned false.</summary>
    public string LastError { get; private set; } = "";

    /// <summary><c>parse_xll_parameters</c>.</summary>
    private void ParseXllParameters(ref DcaExssAsset asset)
    {
        // Size of XLL data in extension substream
        asset.XllSize = (int)gb.GetBits(ExssSizeNbits) + 1;

        // XLL sync word present flag
        asset.XllSyncPresent = (int)gb.GetBits1();
        if (asset.XllSyncPresent != 0)
        {
            // Peak bit rate smoothing buffer size
            gb.SkipBits(4);

            // Number of bits for XLL decoding delay
            int xllDelayNbits = (int)gb.GetBits(5) + 1;

            // Initial XLL decoding delay in frames
            asset.XllDelayNframes = (int)GetBitsLong(xllDelayNbits);

            // Number of bytes offset to XLL sync
            asset.XllSyncOffset = (int)gb.GetBits(ExssSizeNbits);
        }
        else
        {
            asset.XllDelayNframes = 0;
            asset.XllSyncOffset = 0;
        }
    }

    /// <summary><c>parse_lbr_parameters</c>.</summary>
    private void ParseLbrParameters(ref DcaExssAsset asset)
    {
        // Size of LBR component in extension substream
        asset.LbrSize = (int)gb.GetBits(14) + 1;

        // LBR sync word present flag
        if (gb.GetBits1() != 0)
            // LBR sync distance
            gb.SkipBits(2);
    }

    /// <summary><c>parse_descriptor</c>.</summary>
    private bool ParseDescriptor(ref DcaExssAsset asset)
    {
        int descrPos = gb.GetBitsCount();

        // Size of audio asset descriptor in bytes
        int descrSize = (int)gb.GetBits(9) + 1;

        // Audio asset identifier
        asset.AssetIndex = (int)gb.GetBits(3);

        //
        // Per stream static metadata
        //

        if (StaticFieldsPresent != 0)
        {
            // Asset type descriptor presence
            if (gb.GetBits1() != 0)
                // Asset type descriptor
                gb.SkipBits(4);

            // Language descriptor presence
            if (gb.GetBits1() != 0)
                // Language descriptor
                gb.SkipBits(24);

            // Additional textual information presence
            if (gb.GetBits1() != 0)
            {
                // Byte size of additional text info
                int textSize = (int)gb.GetBits(10) + 1;

                if (gb.GetBitsLeft() < textSize * 8)
                {
                    LastError = "EXSS asset descriptor text runs past the end of the substream";
                    return false;
                }

                // Additional textual information string
                gb.SkipBits(textSize * 8);
            }

            // PCM bit resolution
            asset.PcmBitRes = (int)gb.GetBits(5) + 1;

            // Maximum sample rate
            asset.MaxSampleRate = (int)DcaData.SamplingFreqs[(int)gb.GetBits(4)];

            // Total number of channels
            asset.NchannelsTotal = (int)gb.GetBits(8) + 1;

            // One to one map channel to speakers
            asset.OneToOneMapChToSpkr = (int)gb.GetBits1();
            if (asset.OneToOneMapChToSpkr != 0)
            {
                int spkrMaskNbits = 0;
                Span<int> nspeakers = stackalloc int[8];

                // Embedded stereo flag
                asset.EmbeddedStereo =
                    asset.NchannelsTotal > 2 && gb.GetBits1() != 0 ? 1 : 0;

                // Embedded 6 channels flag
                asset.Embedded6Ch =
                    asset.NchannelsTotal > 6 && gb.GetBits1() != 0 ? 1 : 0;

                // Speaker mask enabled flag
                asset.SpkrMaskEnabled = (int)gb.GetBits1();
                if (asset.SpkrMaskEnabled != 0)
                {
                    // Number of bits for speaker activity mask
                    spkrMaskNbits = ((int)gb.GetBits(2) + 1) << 2;

                    // Loudspeaker activity mask
                    asset.SpkrMask = (int)gb.GetBits(spkrMaskNbits);
                }

                // Number of speaker remapping sets
                int spkrRemapNsets = (int)gb.GetBits(3);
                if (spkrRemapNsets != 0 && spkrMaskNbits == 0)
                {
                    LastError = "EXSS speaker mask disabled yet there are remapping sets";
                    return false;
                }

                // Standard loudspeaker layout mask
                for (int i = 0; i < spkrRemapNsets; i++)
                    nspeakers[i] = Dca.CountChsForMask((int)gb.GetBits(spkrMaskNbits));

                for (int i = 0; i < spkrRemapNsets; i++)
                {
                    // Number of channels to be decoded for speaker remapping
                    int nchForRemaps = (int)gb.GetBits(5) + 1;

                    for (int j = 0; j < nspeakers[i]; j++)
                    {
                        // Decoded channels to output speaker mapping mask
                        int remapChMask = (int)GetBitsLong(nchForRemaps);

                        // Loudspeaker remapping codes
                        gb.SkipBits(System.Numerics.BitOperations.PopCount((uint)remapChMask) * 5);
                    }
                }
            }
            else
            {
                asset.EmbeddedStereo = 0;
                asset.Embedded6Ch = 0;
                asset.SpkrMaskEnabled = 0;
                asset.SpkrMask = 0;

                // Representation type
                asset.RepresentationType = (int)gb.GetBits(3);
            }
        }

        //
        // DRC, DNC and mixing metadata
        //

        // Dynamic range coefficient presence flag
        int drcPresent = (int)gb.GetBits1();

        // Code for dynamic range coefficient
        if (drcPresent != 0)
            gb.SkipBits(8);

        // Dialog normalization presence flag
        if (gb.GetBits1() != 0)
            // Dialog normalization code
            gb.SkipBits(5);

        // DRC for stereo downmix
        if (drcPresent != 0 && asset.EmbeddedStereo != 0)
            gb.SkipBits(8);

        // Mixing metadata presence flag
        if (MixMetadataEnabled != 0 && gb.GetBits1() != 0)
        {
            // External mixing flag
            gb.SkipBits1();

            // Post mixing / replacement gain adjustment
            gb.SkipBits(6);

            if (gb.GetBits(2) == 3)
                // Custom code for mixing DRC
                gb.SkipBits(8);
            else
                // Limit for mixing DRC
                gb.SkipBits(3);

            // Scaling type for channels of main audio, then the scaling parameters, which are
            // per mixer output channel or per configuration depending on the flag.
            if (gb.GetBits1() != 0)
                for (int i = 0; i < Nmixoutconfigs; i++)
                    gb.SkipBits(6 * nmixoutchs[i]);
            else
                gb.SkipBits(6 * Nmixoutconfigs);

            int nchannelsDmix = asset.NchannelsTotal;
            if (asset.Embedded6Ch != 0)
                nchannelsDmix += 6;
            if (asset.EmbeddedStereo != 0)
                nchannelsDmix += 2;

            for (int i = 0; i < Nmixoutconfigs; i++)
            {
                if (nmixoutchs[i] == 0)
                {
                    LastError = "invalid speaker layout mask for an EXSS mixing configuration";
                    return false;
                }
                for (int j = 0; j < nchannelsDmix; j++)
                {
                    // Mix output mask
                    int mixMapMask = (int)gb.GetBits(nmixoutchs[i]);

                    // Mixing coefficients
                    gb.SkipBits(System.Numerics.BitOperations.PopCount((uint)mixMapMask) * 6);
                }
            }
        }

        //
        // Decoder navigation data
        //

        // Coding mode for the asset
        asset.CodingMode = (int)gb.GetBits(2);

        // Coding components used in asset
        switch (asset.CodingMode)
        {
            case 0: // Coding mode that may contain multiple coding components
                asset.ExtensionMask = (int)gb.GetBits(12);

                if ((asset.ExtensionMask & DcaExtensionMask.ExssCore) != 0)
                {
                    // Size of core component in extension substream
                    asset.CoreSize = (int)gb.GetBits(14) + 1;
                    // Core sync word present flag
                    if (gb.GetBits1() != 0)
                        // Core sync distance
                        gb.SkipBits(2);
                }

                if ((asset.ExtensionMask & DcaExtensionMask.ExssXbr) != 0)
                    // Size of XBR extension in extension substream
                    asset.XbrSize = (int)gb.GetBits(14) + 1;

                if ((asset.ExtensionMask & DcaExtensionMask.ExssXxch) != 0)
                    // Size of XXCH extension in extension substream
                    asset.XxchSize = (int)gb.GetBits(14) + 1;

                if ((asset.ExtensionMask & DcaExtensionMask.ExssX96) != 0)
                    // Size of X96 extension in extension substream
                    asset.X96Size = (int)gb.GetBits(12) + 1;

                if ((asset.ExtensionMask & DcaExtensionMask.ExssLbr) != 0)
                    ParseLbrParameters(ref asset);

                if ((asset.ExtensionMask & DcaExtensionMask.ExssXll) != 0)
                    ParseXllParameters(ref asset);

                if ((asset.ExtensionMask & DcaExtensionMask.ExssRsv1) != 0)
                    gb.SkipBits(16);

                if ((asset.ExtensionMask & DcaExtensionMask.ExssRsv2) != 0)
                    gb.SkipBits(16);
                break;

            case 1: // Loss-less coding mode without CBR component
                asset.ExtensionMask = DcaExtensionMask.ExssXll;
                ParseXllParameters(ref asset);
                break;

            case 2: // Low bit rate mode
                asset.ExtensionMask = DcaExtensionMask.ExssLbr;
                ParseLbrParameters(ref asset);
                break;

            case 3: // Auxiliary coding mode
                asset.ExtensionMask = 0;

                // Size of auxiliary coded data
                gb.SkipBits(14);

                // Auxiliary codec identification
                gb.SkipBits(8);

                // Aux sync word present flag
                if (gb.GetBits1() != 0)
                    // Aux sync distance
                    gb.SkipBits(3);
                break;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssXll) != 0)
            // DTS-HD stream ID
            asset.HdStreamId = (int)gb.GetBits(3);

        // One to one mixing flag, per channel main audio scaling flag, main audio scaling codes,
        // decode asset in secondary decoder flag, revision 2 DRC metadata, reserved, zero pad:
        // all skipped by seeking to the end of the descriptor the length field declared.
        if (!SeekBits(descrPos + descrSize * 8))
        {
            LastError = "read past end of EXSS asset descriptor";
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>set_exss_offsets</c> - turn the per-extension sizes into offsets from the start of the
    /// substream. The extensions sit in this fixed order, each immediately after the last.
    /// </summary>
    private static bool SetExssOffsets(ref DcaExssAsset asset)
    {
        int offs = asset.AssetOffset;
        int size = asset.AssetSize;

        if ((asset.ExtensionMask & DcaExtensionMask.ExssCore) != 0)
        {
            asset.CoreOffset = offs;
            if (asset.CoreSize > size)
                return false;
            offs += asset.CoreSize;
            size -= asset.CoreSize;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssXbr) != 0)
        {
            asset.XbrOffset = offs;
            if (asset.XbrSize > size)
                return false;
            offs += asset.XbrSize;
            size -= asset.XbrSize;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssXxch) != 0)
        {
            asset.XxchOffset = offs;
            if (asset.XxchSize > size)
                return false;
            offs += asset.XxchSize;
            size -= asset.XxchSize;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssX96) != 0)
        {
            asset.X96Offset = offs;
            if (asset.X96Size > size)
                return false;
            offs += asset.X96Size;
            size -= asset.X96Size;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssLbr) != 0)
        {
            asset.LbrOffset = offs;
            if (asset.LbrSize > size)
                return false;
            offs += asset.LbrSize;
            size -= asset.LbrSize;
        }

        if ((asset.ExtensionMask & DcaExtensionMask.ExssXll) != 0)
        {
            asset.XllOffset = offs;
            if (asset.XllSize > size)
                return false;
            offs += asset.XllSize;
            size -= asset.XllSize;
        }

        return true;
    }

    /// <summary>
    /// <c>ff_dca_exss_parse</c> - read the extension substream header at
    /// <paramref name="offset"/> in <paramref name="data"/>.
    /// </summary>
    /// <remarks>
    /// The C takes a pointer into the packet and treats it as the start of the substream; every
    /// offset it produces is relative to that. Here the whole packet is passed with the offset
    /// alongside, because there are no interior pointers, and the asset offsets stay relative
    /// to the substream exactly as the C's are.
    /// </remarks>
    public bool Parse(byte[] data, int offset, int size)
    {
        LastError = "";
        Asset = default;

        gb = default;
        gb.InitGetBits8(data, offset, size);

        // Extension substream sync word
        gb.SkipBits(32);

        // User defined bits
        gb.SkipBits(8);

        // Extension substream index
        ExssIndex = (int)gb.GetBits(2);

        // Flag indicating short or long header size
        int wideHdr = (int)gb.GetBits1();

        // Extension substream header length
        int headerSize = (int)gb.GetBits(8 + 4 * wideHdr) + 1;

        // The CRC check here is a no-op on FFmpeg's default error recognition; see
        // DcaCore.CheckCrc for why that is kept rather than tightened.

        ExssSizeNbits = 16 + 4 * wideHdr;

        // Number of bytes of extension substream
        ExssSize = (int)gb.GetBits(ExssSizeNbits) + 1;
        if (ExssSize > size)
        {
            LastError = $"packet too short for the EXSS frame: {ExssSize} declared, {size} left";
            return false;
        }

        // Per stream static fields presence flag
        StaticFieldsPresent = (int)gb.GetBits1();
        if (StaticFieldsPresent != 0)
        {
            Span<int> activeExssMask = stackalloc int[8];

            // Reference clock code
            gb.SkipBits(2);

            // Extension substream frame duration
            gb.SkipBits(3);

            // Timecode presence flag
            if (gb.GetBits1() != 0)
                // Timecode data
                gb.SkipBits(36);

            // Number of defined audio presentations
            Npresents = (int)gb.GetBits(3) + 1;
            if (Npresents > 1)
            {
                LastError = $"{Npresents} audio presentations is more than this decoder handles";
                return false;
            }

            // Number of audio assets in extension substream
            Nassets = (int)gb.GetBits(3) + 1;
            if (Nassets > 1)
            {
                LastError = $"{Nassets} audio assets is more than this decoder handles";
                return false;
            }

            // Active extension substream mask for audio presentation
            for (int i = 0; i < Npresents; i++)
                activeExssMask[i] = (int)gb.GetBits(ExssIndex + 1);

            // Active audio asset mask
            for (int i = 0; i < Npresents; i++)
                gb.SkipBits(System.Numerics.BitOperations.PopCount((uint)activeExssMask[i]) * 8);

            // Mixing metadata enable flag
            MixMetadataEnabled = (int)gb.GetBits1();
            if (MixMetadataEnabled != 0)
            {
                // Mixing metadata adjustment level
                gb.SkipBits(2);

                // Number of bits for mixer output speaker activity mask
                int spkrMaskNbits = ((int)gb.GetBits(2) + 1) << 2;

                // Number of mixing configurations
                Nmixoutconfigs = (int)gb.GetBits(2) + 1;

                // Speaker layout mask for mixer output channels
                for (int i = 0; i < Nmixoutconfigs; i++)
                    nmixoutchs[i] = Dca.CountChsForMask((int)gb.GetBits(spkrMaskNbits));
            }
        }
        else
        {
            Npresents = 1;
            Nassets = 1;
        }

        // Size of encoded asset data in bytes
        int assetOffset = headerSize;
        for (int i = 0; i < Nassets; i++)
        {
            Asset.AssetOffset = assetOffset;
            Asset.AssetSize = (int)gb.GetBits(ExssSizeNbits) + 1;
            assetOffset += Asset.AssetSize;
            if (assetOffset > ExssSize)
            {
                LastError = "EXSS asset out of bounds";
                return false;
            }
        }

        // Audio asset descriptor
        for (int i = 0; i < Nassets; i++)
        {
            if (!ParseDescriptor(ref Asset))
                return false;
            if (!SetExssOffsets(ref Asset))
            {
                LastError = "invalid extension size in the EXSS asset descriptor";
                return false;
            }
        }

        // Backward compatible core present and its indices, reserved, byte align, CRC16 of the
        // header: all skipped by seeking to the end of the header.
        if (!SeekBits(headerSize * 8))
        {
            LastError = "read past end of EXSS header";
            return false;
        }

        return true;
    }

    /// <summary><c>ff_dca_seek_bits</c>.</summary>
    private bool SeekBits(int p)
    {
        int at = gb.GetBitsCount();
        if (p < at || p > gb.SizeInBits)
            return false;
        gb.SkipBits(p - at);
        return true;
    }

    /// <summary>
    /// <c>get_bits_long</c> - the same as <c>get_bits</c> but valid for 0 and for 32 bits, which
    /// a field whose width comes from the bitstream can be.
    /// </summary>
    private uint GetBitsLong(int n)
    {
        if (n == 0)
            return 0;
        if (n <= 25)
            return gb.GetBits(n);
        uint high = gb.GetBits(16);
        return high << (n - 16) | gb.GetBits(n - 16);
    }
}

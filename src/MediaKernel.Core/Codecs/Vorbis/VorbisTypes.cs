// Licensed under the MIT License.
// Based on Vorbis I Specification (https://xiph.org/vorbis/doc/Vorbis_I_spec.html)

using System;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>
/// Vorbis stream identification header.
/// </summary>
public readonly struct VorbisIdentificationHeader
{
    /// <summary>Magic signature for Vorbis identification header.</summary>
    public static ReadOnlySpan<byte> Magic => "\x01vorbis"u8;
    
    /// <summary>Vorbis version (always 0 for Vorbis I).</summary>
    public readonly uint Version;
    
    /// <summary>Number of audio channels (1-255).</summary>
    public readonly byte ChannelCount;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly uint SampleRate;
    
    /// <summary>Maximum bitrate (0 = unset).</summary>
    public readonly int BitrateMaximum;
    
    /// <summary>Nominal bitrate (0 = unset).</summary>
    public readonly int BitrateNominal;
    
    /// <summary>Minimum bitrate (0 = unset).</summary>
    public readonly int BitrateMinimum;
    
    /// <summary>Size of short blocks (power of 2: 64-8192).</summary>
    public readonly int BlockSize0;
    
    /// <summary>Size of long blocks (power of 2: 64-8192).</summary>
    public readonly int BlockSize1;
    
    public VorbisIdentificationHeader(
        uint version, byte channelCount, uint sampleRate,
        int bitrateMax, int bitrateNominal, int bitrateMin,
        int blockSize0, int blockSize1)
    {
        Version = version;
        ChannelCount = channelCount;
        SampleRate = sampleRate;
        BitrateMaximum = bitrateMax;
        BitrateNominal = bitrateNominal;
        BitrateMinimum = bitrateMin;
        BlockSize0 = blockSize0;
        BlockSize1 = blockSize1;
    }
}

/// <summary>
/// Vorbis comment header with metadata.
/// </summary>
public class VorbisCommentHeader
{
    /// <summary>Magic signature for Vorbis comment header.</summary>
    public static ReadOnlySpan<byte> Magic => "\x03vorbis"u8;
    
    /// <summary>Vendor string (encoder identification).</summary>
    public string Vendor { get; set; } = string.Empty;
    
    /// <summary>User comments as key=value pairs.</summary>
    public string[] Comments { get; set; } = Array.Empty<string>();
    
    /// <summary>Gets a specific comment by key (case-insensitive).</summary>
    public string? GetComment(string key)
    {
        foreach (var comment in Comments)
        {
            int equalPos = comment.IndexOf('=');
            if (equalPos > 0)
            {
                string commentKey = comment[..equalPos];
                if (commentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return comment[(equalPos + 1)..];
            }
        }
        return null;
    }
    
    /// <summary>Gets the title tag.</summary>
    public string? Title => GetComment("TITLE");
    
    /// <summary>Gets the artist tag.</summary>
    public string? Artist => GetComment("ARTIST");
    
    /// <summary>Gets the album tag.</summary>
    public string? Album => GetComment("ALBUM");
    
    /// <summary>Gets the track number tag.</summary>
    public string? TrackNumber => GetComment("TRACKNUMBER");
}

/// <summary>
/// Floor type enumeration.
/// </summary>
public enum VorbisFloorType
{
    /// <summary>Floor type 0: LSP (legacy, rarely used).</summary>
    Type0 = 0,
    
    /// <summary>Floor type 1: Curve fitting (standard).</summary>
    Type1 = 1
}

/// <summary>
/// Residue type enumeration.
/// </summary>
public enum VorbisResidueType
{
    /// <summary>Interleaved vector residue.</summary>
    Type0 = 0,
    
    /// <summary>Fixed-partition residue.</summary>
    Type1 = 1,
    
    /// <summary>Variable-partition residue (most common).</summary>
    Type2 = 2
}

/// <summary>
/// Represents a Vorbis codebook for Huffman and vector quantization.
/// </summary>
public class VorbisCodebook
{
    /// <summary>Number of dimensions (vector size).</summary>
    public int Dimensions { get; set; }
    
    /// <summary>Number of entries in the codebook.</summary>
    public int EntryCount { get; set; }
    
    /// <summary>Whether codeword lengths are ordered.</summary>
    public bool Ordered { get; set; }
    
    /// <summary>Whether unused entries are sparse.</summary>
    public bool Sparse { get; set; }
    
    /// <summary>Codeword lengths for each entry (0 = unused).</summary>
    public byte[] CodewordLengths { get; set; } = Array.Empty<byte>();
    
    /// <summary>Lookup type (0 = no lookup, 1 = implicit, 2 = explicit).</summary>
    public int LookupType { get; set; }
    
    /// <summary>Minimum value for VQ.</summary>
    public float MinimumValue { get; set; }
    
    /// <summary>Delta value for VQ.</summary>
    public float DeltaValue { get; set; }
    
    /// <summary>Value bits for VQ.</summary>
    public int ValueBits { get; set; }
    
    /// <summary>Sequence flag for VQ.</summary>
    public bool SequenceP { get; set; }
    
    /// <summary>Multiplicand values for lookup.</summary>
    public uint[] Multiplicands { get; set; } = Array.Empty<uint>();
    
    /// <summary>Pre-computed VQ vectors (EntryCount × Dimensions).</summary>
    public float[]? VectorLookup { get; set; }
    
    /// <summary>Huffman decode tree for fast decoding.</summary>
    public int[] DecodeTree { get; set; } = Array.Empty<int>();
    
    /// <summary>Number of lookup values.</summary>
    public int LookupValues { get; set; }
}

/// <summary>
/// Base class for floor configurations.
/// </summary>
public abstract class VorbisFloorConfiguration
{
    /// <summary>Floor type (0 or 1).</summary>
    public VorbisFloorType FloorType { get; set; }
}

/// <summary>
/// Floor type 0 configuration (LSP-based).
/// </summary>
public class VorbisFloor0Configuration : VorbisFloorConfiguration
{
    /// <summary>LSP order.</summary>
    public int Order { get; set; }
    
    /// <summary>Sample rate for bark map.</summary>
    public int Rate { get; set; }
    
    /// <summary>Bark map size.</summary>
    public int BarkMapSize { get; set; }
    
    /// <summary>Amplitude bits.</summary>
    public int AmplitudeBits { get; set; }
    
    /// <summary>Amplitude offset.</summary>
    public int AmplitudeOffset { get; set; }
    
    /// <summary>Book list (codebook indices).</summary>
    public int[] BookList { get; set; } = Array.Empty<int>();
    
    /// <summary>Bark-scale frequency map per blocksize [0]=short, [1]=long.</summary>
    public int[]?[] Map { get; set; } = new int[2][];
    
    /// <summary>Map size per blocksize.</summary>
    public int[] MapSize { get; set; } = new int[2];
    
    /// <summary>Scratch buffer for LSP coefficients during decode.</summary>
    public float[] Lsp { get; set; } = Array.Empty<float>();
    
    /// <summary>Maximum codebook dimension across all books.</summary>
    public int MaxCodebookDim { get; set; }
}

/// <summary>
/// Floor type 1 configuration.
/// </summary>
public class VorbisFloor1Configuration : VorbisFloorConfiguration
{
    /// <summary>Number of partitions.</summary>
    public int Partitions { get; set; }
    
    /// <summary>Partition class assignments.</summary>
    public int[] PartitionClassList { get; set; } = Array.Empty<int>();
    
    /// <summary>Class dimensions.</summary>
    public int[] ClassDimensions { get; set; } = Array.Empty<int>();
    
    /// <summary>Class subclasses.</summary>
    public int[] ClassSubclasses { get; set; } = Array.Empty<int>();
    
    /// <summary>Class masterbooks.</summary>
    public int[] ClassMasterbooks { get; set; } = Array.Empty<int>();
    
    /// <summary>Subclass codebooks (flattened 2D array).</summary>
    public int[] SubclassBooks { get; set; } = Array.Empty<int>();
    
    /// <summary>Multiplier (1-4).</summary>
    public int Multiplier { get; set; }
    
    /// <summary>Range bits.</summary>
    public int RangeBits { get; set; }
    
    /// <summary>X-list values (sorted).</summary>
    public int[] XList { get; set; } = Array.Empty<int>();
    
    /// <summary>Sorted order of X values.</summary>
    public int[] XListSortOrder { get; set; } = Array.Empty<int>();
    
    /// <summary>Low neighbor indices.</summary>
    public int[] LowNeighbor { get; set; } = Array.Empty<int>();
    
    /// <summary>High neighbor indices.</summary>
    public int[] HighNeighbor { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Residue configuration.
/// </summary>
public class VorbisResidueConfiguration
{
    /// <summary>Residue type (0, 1, or 2).</summary>
    public VorbisResidueType Type { get; set; }
    
    /// <summary>Begin position in residue vector.</summary>
    public int Begin { get; set; }
    
    /// <summary>End position in residue vector.</summary>
    public int End { get; set; }
    
    /// <summary>Partition size.</summary>
    public int PartitionSize { get; set; }
    
    /// <summary>Number of classifications.</summary>
    public int Classifications { get; set; }
    
    /// <summary>Classbook index.</summary>
    public int Classbook { get; set; }
    
    /// <summary>Cascade bits per classification.</summary>
    public byte[] Cascade { get; set; } = Array.Empty<byte>();
    
    /// <summary>Codebook assignments (Classifications × 8).</summary>
    public int[] Books { get; set; } = Array.Empty<int>();
    
    /// <summary>Maximum pass number with valid codebooks (0-7).</summary>
    public int MaxPass { get; set; }
}

/// <summary>
/// Mapping configuration.
/// </summary>
public class VorbisMappingConfiguration
{
    /// <summary>Number of submaps.</summary>
    public int SubmapCount { get; set; }
    
    /// <summary>Coupling steps (0 for mono).</summary>
    public int CouplingSteps { get; set; }
    
    /// <summary>Coupling magnitude channel indices.</summary>
    public int[] CouplingMagnitude { get; set; } = Array.Empty<int>();
    
    /// <summary>Coupling angle channel indices.</summary>
    public int[] CouplingAngle { get; set; } = Array.Empty<int>();
    
    /// <summary>Channel multiplex indices.</summary>
    public int[] Mux { get; set; } = Array.Empty<int>();
    
    /// <summary>Floor indices per submap.</summary>
    public int[] SubmapFloor { get; set; } = Array.Empty<int>();
    
    /// <summary>Residue indices per submap.</summary>
    public int[] SubmapResidue { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Mode configuration.
/// </summary>
public readonly struct VorbisModeConfiguration
{
    /// <summary>Block flag (0 = short, 1 = long).</summary>
    public readonly bool BlockFlag;
    
    /// <summary>Window type (always 0).</summary>
    public readonly int WindowType;
    
    /// <summary>Transform type (always 0 = MDCT).</summary>
    public readonly int TransformType;
    
    /// <summary>Mapping index.</summary>
    public readonly int Mapping;
    
    public VorbisModeConfiguration(bool blockFlag, int windowType, int transformType, int mapping)
    {
        BlockFlag = blockFlag;
        WindowType = windowType;
        TransformType = transformType;
        Mapping = mapping;
    }
}

/// <summary>
/// Complete setup header configuration.
/// </summary>
public class VorbisSetupHeader
{
    /// <summary>Magic signature for Vorbis setup header.</summary>
    public static ReadOnlySpan<byte> Magic => "\x05vorbis"u8;
    
    /// <summary>Codebooks.</summary>
    public VorbisCodebook[] Codebooks { get; set; } = Array.Empty<VorbisCodebook>();
    
    /// <summary>Floor configurations (type 0 and type 1).</summary>
    public VorbisFloorConfiguration[] Floors { get; set; } = Array.Empty<VorbisFloorConfiguration>();
    
    /// <summary>Residue configurations.</summary>
    public VorbisResidueConfiguration[] Residues { get; set; } = Array.Empty<VorbisResidueConfiguration>();
    
    /// <summary>Mapping configurations.</summary>
    public VorbisMappingConfiguration[] Mappings { get; set; } = Array.Empty<VorbisMappingConfiguration>();
    
    /// <summary>Mode configurations.</summary>
    public VorbisModeConfiguration[] Modes { get; set; } = Array.Empty<VorbisModeConfiguration>();
}

/// <summary>
/// Vorbis static tables from the specification.
/// </summary>
public static class VorbisTables
{
    /// <summary>
    /// Floor 1 inverse dB lookup table (Vorbis I spec §9.2.6).
    /// Indexed by floor Y value × multiplier, clamped to [0,255].
    /// Returns linear amplitude multiplier.
    /// </summary>
    public static ReadOnlySpan<float> Floor1InverseDbTable => new float[256]
    {
        1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f,
        1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
        1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.128753e-07f,
        2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
        2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f,
        3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
        4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f,
        6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
        7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f,
        1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
        1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f,
        1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
        2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f,
        2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
        3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f,
        4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
        5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f,
        7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
        9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f,
        1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
        1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f,
        2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
        2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f,
        3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
        4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f,
        5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
        7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f,
        9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
        0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f,
        0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
        0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f,
        0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
        0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f,
        0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
        0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f,
        0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
        0.00092223983f, 0.00098217216f, 0.0010459992f,  0.0011139742f,
        0.0011863665f,  0.0012634633f,  0.0013455702f,  0.0014330129f,
        0.0015261382f,  0.0016253153f,  0.0017309374f,  0.0018434235f,
        0.0019632195f,  0.0020908006f,  0.0022266726f,  0.0023713743f,
        0.0025254795f,  0.0026895994f,  0.0028643847f,  0.0030505286f,
        0.0032487691f,  0.0034598925f,  0.0036847358f,  0.0039241906f,
        0.0041792066f,  0.0044507950f,  0.0047400328f,  0.0050480668f,
        0.0053761186f,  0.0057254891f,  0.0060975636f,  0.0064938176f,
        0.0069158225f,  0.0073652516f,  0.0078438871f,  0.0083536271f,
        0.0088964928f,  0.009474637f,   0.010090352f,   0.010746080f,
        0.011444421f,   0.012188144f,   0.012980198f,   0.013823725f,
        0.014722068f,   0.015678791f,   0.016697687f,   0.017782797f,
        0.018938423f,   0.020169149f,   0.021479854f,   0.022875735f,
        0.024362330f,   0.025945531f,   0.027631618f,   0.029427276f,
        0.031339626f,   0.033376252f,   0.035545228f,   0.037855157f,
        0.040315199f,   0.042935108f,   0.045725273f,   0.048696758f,
        0.051861348f,   0.055231591f,   0.058820850f,   0.062643361f,
        0.066714279f,   0.071049749f,   0.075666962f,   0.080584227f,
        0.085821044f,   0.091398179f,   0.097337747f,   0.10366330f,
        0.11039993f,    0.11757434f,    0.12521498f,    0.13335215f,
        0.14201813f,    0.15124727f,    0.16107617f,    0.17154380f,
        0.18269168f,    0.19456402f,    0.20720788f,    0.22067342f,
        0.23501402f,    0.25028656f,    0.26655159f,    0.28387361f,
        0.30232132f,    0.32196786f,    0.34289114f,    0.36517414f,
        0.38890521f,    0.41417847f,    0.44109412f,    0.46975890f,
        0.50028648f,    0.53279791f,    0.56742212f,    0.60429640f,
        0.64356699f,    0.68538959f,    0.72993007f,    0.77736504f,
        0.82788260f,    0.88168307f,    0.9389798f,     1.0f,
    };
}

/// <summary>
/// Per-channel decode state for a single audio packet.
/// </summary>
public class VorbisChannelState
{
    /// <summary>Floor amplitude curve (in dB).</summary>
    public float[] FloorOutput;
    
    /// <summary>Residue values.</summary>
    public float[] ResidueOutput;
    
    /// <summary>Combined spectrum (floor × residue).</summary>
    public float[] Spectrum;
    
    /// <summary>Whether this channel has energy (not zeroed floor).</summary>
    public bool HasEnergy;
    
    public VorbisChannelState(int maxBlockSize)
    {
        int halfBlock = maxBlockSize / 2;
        FloorOutput = new float[halfBlock];
        ResidueOutput = new float[halfBlock];
        Spectrum = new float[halfBlock];
    }
    
    public void Reset()
    {
        Array.Clear(FloorOutput);
        Array.Clear(ResidueOutput);
        Array.Clear(Spectrum);
        HasEnergy = false;
    }
}

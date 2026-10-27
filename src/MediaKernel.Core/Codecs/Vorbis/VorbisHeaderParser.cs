// Licensed under the MIT License.
// Vorbis header parsing.

using System;
using System.Text;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>
/// Parser for Vorbis header packets.
/// </summary>
public static class VorbisHeaderParser
{
    /// <summary>
    /// Parses the identification header.
    /// </summary>
    public static VorbisIdentificationHeader ParseIdentificationHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            throw new ArgumentException("Identification header too short");
        
        // Check magic
        if (!data[..7].SequenceEqual(VorbisIdentificationHeader.Magic))
            throw new FormatException("Invalid Vorbis identification header magic");
        
        var reader = new VorbisBitReader(data[7..]);
        
        uint version = reader.ReadBits(32);
        if (version != 0)
            throw new FormatException($"Unsupported Vorbis version: {version}");
        
        byte channels = (byte)reader.ReadBits(8);
        if (channels == 0)
            throw new FormatException("Invalid channel count: 0");
        
        uint sampleRate = reader.ReadBits(32);
        if (sampleRate == 0)
            throw new FormatException("Invalid sample rate: 0");
        
        int bitrateMax = (int)reader.ReadBits(32);
        int bitrateNominal = (int)reader.ReadBits(32);
        int bitrateMin = (int)reader.ReadBits(32);
        
        int blockSize0Bits = (int)reader.ReadBits(4);
        int blockSize1Bits = (int)reader.ReadBits(4);
        
        int blockSize0 = 1 << blockSize0Bits;
        int blockSize1 = 1 << blockSize1Bits;
        
        // Validate block sizes
        if (blockSize0 < 64 || blockSize0 > 8192)
            throw new FormatException($"Invalid blocksize_0: {blockSize0}");
        if (blockSize1 < 64 || blockSize1 > 8192)
            throw new FormatException($"Invalid blocksize_1: {blockSize1}");
        if (blockSize0 > blockSize1)
            throw new FormatException("blocksize_0 must be <= blocksize_1");
        
        // Framing bit should be 1
        if (!reader.ReadBool())
            throw new FormatException("Invalid framing bit in identification header");
        
        return new VorbisIdentificationHeader(
            version, channels, sampleRate,
            bitrateMax, bitrateNominal, bitrateMin,
            blockSize0, blockSize1);
    }
    
    /// <summary>
    /// Parses the comment header.
    /// </summary>
    public static VorbisCommentHeader ParseCommentHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            throw new ArgumentException("Comment header too short");
        
        // Check magic
        if (!data[..7].SequenceEqual(VorbisCommentHeader.Magic))
            throw new FormatException("Invalid Vorbis comment header magic");
        
        int offset = 7;
        
        // Vendor string (little-endian length + UTF-8 data)
        if (offset + 4 > data.Length)
            throw new FormatException("Comment header truncated");
        
        int vendorLength = data[offset] | (data[offset + 1] << 8) |
                           (data[offset + 2] << 16) | (data[offset + 3] << 24);
        offset += 4;
        
        if (vendorLength < 0 || offset + vendorLength > data.Length)
            throw new FormatException("Invalid vendor length");
        
        string vendor = vendorLength > 0 
            ? Encoding.UTF8.GetString(data.Slice(offset, vendorLength)) 
            : string.Empty;
        offset += vendorLength;
        
        // Comment count
        if (offset + 4 > data.Length)
            throw new FormatException("Comment header truncated");
        
        int commentCount = data[offset] | (data[offset + 1] << 8) |
                           (data[offset + 2] << 16) | (data[offset + 3] << 24);
        offset += 4;
        
        if (commentCount < 0)
            throw new FormatException("Invalid comment count");
        
        var comments = new string[commentCount];
        
        for (int i = 0; i < commentCount; i++)
        {
            if (offset + 4 > data.Length)
                throw new FormatException("Comment header truncated");
            
            int length = data[offset] | (data[offset + 1] << 8) |
                         (data[offset + 2] << 16) | (data[offset + 3] << 24);
            offset += 4;
            
            if (length < 0 || offset + length > data.Length)
                throw new FormatException("Invalid comment length");
            
            comments[i] = length > 0 
                ? Encoding.UTF8.GetString(data.Slice(offset, length)) 
                : string.Empty;
            offset += length;
        }
        
        return new VorbisCommentHeader
        {
            Vendor = vendor,
            Comments = comments
        };
    }
    
    /// <summary>
    /// Parses the setup header.
    /// </summary>
    public static VorbisSetupHeader ParseSetupHeader(ReadOnlySpan<byte> data, VorbisIdentificationHeader identHeader)
    {
        if (data.Length < 16)
            throw new ArgumentException("Setup header too short");
        
        // Check magic
        if (!data[..7].SequenceEqual(VorbisSetupHeader.Magic))
            throw new FormatException("Invalid Vorbis setup header magic");
        
        var reader = new VorbisBitReader(data[7..]);
        var setup = new VorbisSetupHeader();
        
        // Parse codebooks
        int codebookCount = (int)reader.ReadBits(8) + 1;
        setup.Codebooks = new VorbisCodebook[codebookCount];
        
        for (int i = 0; i < codebookCount; i++)
        {
            setup.Codebooks[i] = ParseCodebook(ref reader);
        }
        
        // Parse time domain transforms (placeholder - always 0)
        int timeCount = (int)reader.ReadBits(6) + 1;
        for (int i = 0; i < timeCount; i++)
        {
            int timeType = (int)reader.ReadBits(16);
            if (timeType != 0)
                throw new FormatException($"Unsupported time domain type: {timeType}");
        }
        
        // Parse floors
        int floorCount = (int)reader.ReadBits(6) + 1;
        setup.Floors = new VorbisFloorConfiguration[floorCount];
        
        for (int i = 0; i < floorCount; i++)
        {
            int floorType = (int)reader.ReadBits(16);
            if (floorType == 0)
                setup.Floors[i] = ParseFloor0(ref reader, setup.Codebooks, identHeader);
            else if (floorType == 1)
                setup.Floors[i] = ParseFloor1(ref reader);
            else
                throw new FormatException($"Floor type {floorType} not supported (only types 0 and 1)");
        }
        
        // Parse residues
        int residueCount = (int)reader.ReadBits(6) + 1;
        setup.Residues = new VorbisResidueConfiguration[residueCount];
        
        for (int i = 0; i < residueCount; i++)
        {
            setup.Residues[i] = ParseResidue(ref reader, setup.Codebooks);
        }
        
        // Parse mappings
        int mappingCount = (int)reader.ReadBits(6) + 1;
        setup.Mappings = new VorbisMappingConfiguration[mappingCount];
        
        for (int i = 0; i < mappingCount; i++)
        {
            setup.Mappings[i] = ParseMapping(ref reader, identHeader.ChannelCount);
        }
        
        // Parse modes
        int modeCount = (int)reader.ReadBits(6) + 1;
        setup.Modes = new VorbisModeConfiguration[modeCount];
        
        for (int i = 0; i < modeCount; i++)
        {
            setup.Modes[i] = ParseMode(ref reader);
        }
        
        // Framing bit
        if (!reader.ReadBool())
            throw new FormatException("Invalid framing bit in setup header");
        
        return setup;
    }
    
    /// <summary>
    /// Parses a codebook.
    /// </summary>
    private static VorbisCodebook ParseCodebook(ref VorbisBitReader reader)
    {
        // Sync pattern
        int sync = (int)reader.ReadBits(24);
        if (sync != 0x564342) // "BCV"
            throw new FormatException($"Invalid codebook sync pattern: 0x{sync:X6}");
        
        var codebook = new VorbisCodebook();
        
        codebook.Dimensions = (int)reader.ReadBits(16);
        codebook.EntryCount = (int)reader.ReadBits(24);
        
        // Read codeword lengths
        codebook.Ordered = reader.ReadBool();
        codebook.CodewordLengths = new byte[codebook.EntryCount];
        
        if (codebook.Ordered)
        {
            // Ordered codeword lengths
            int currentEntry = 0;
            int currentLength = (int)reader.ReadBits(5) + 1;
            
            while (currentEntry < codebook.EntryCount)
            {
                int bits = VorbisBitReader.ILog(codebook.EntryCount - currentEntry);
                int number = (int)reader.ReadBits(bits);
                
                for (int i = 0; i < number && currentEntry < codebook.EntryCount; i++)
                {
                    codebook.CodewordLengths[currentEntry++] = (byte)currentLength;
                }
                
                currentLength++;
            }
        }
        else
        {
            // Unordered codeword lengths
            codebook.Sparse = reader.ReadBool();
            
            for (int i = 0; i < codebook.EntryCount; i++)
            {
                if (codebook.Sparse)
                {
                    if (reader.ReadBool())
                    {
                        codebook.CodewordLengths[i] = (byte)(reader.ReadBits(5) + 1);
                    }
                    else
                    {
                        codebook.CodewordLengths[i] = 0; // Unused
                    }
                }
                else
                {
                    codebook.CodewordLengths[i] = (byte)(reader.ReadBits(5) + 1);
                }
            }
        }
        
        // Build Huffman decode tree
        BuildHuffmanTree(codebook);
        
        // Read VQ lookup
        codebook.LookupType = (int)reader.ReadBits(4);
        
        if (codebook.LookupType == 1 || codebook.LookupType == 2)
        {
            codebook.MinimumValue = reader.ReadFloat32();
            codebook.DeltaValue = reader.ReadFloat32();
            codebook.ValueBits = (int)reader.ReadBits(4) + 1;
            codebook.SequenceP = reader.ReadBool();
            
            // Calculate lookup values count
            if (codebook.LookupType == 1)
            {
                codebook.LookupValues = LookupValues(codebook.EntryCount, codebook.Dimensions);
            }
            else // LookupType == 2
            {
                codebook.LookupValues = codebook.EntryCount * codebook.Dimensions;
            }
            
            // Read multiplicands
            codebook.Multiplicands = new uint[codebook.LookupValues];
            for (int i = 0; i < codebook.LookupValues; i++)
            {
                codebook.Multiplicands[i] = reader.ReadBits(codebook.ValueBits);
            }
            
            // Build VQ lookup table
            BuildVqLookup(codebook);
        }
        
        return codebook;
    }
    
    /// <summary>
    /// Calculates lookup values for lookup type 1.
    /// </summary>
    private static int LookupValues(int entries, int dimensions)
    {
        // Find floor of entries^(1/dimensions)
        int value = 0;
        while (IntPow(value + 1, dimensions) <= entries)
        {
            value++;
        }
        return value;
    }
    
    /// <summary>
    /// Integer power function.
    /// </summary>
    private static int IntPow(int baseValue, int exp)
    {
        int result = 1;
        for (int i = 0; i < exp; i++)
        {
            result *= baseValue;
        }
        return result;
    }
    
    /// <summary>
    /// Builds Huffman decode tree using Vorbis entry-order code assignment.
    /// Ports FFmpeg's ff_vorbis_len2vlc (vorbis.c) which generates LSB-first
    /// codewords by processing entries sequentially and filling the binary tree
    /// left-to-right. The standard canonical Huffman algorithm (group by length)
    /// is WRONG for Vorbis — it produces different codewords when entry lengths
    /// are non-monotonic.
    /// </summary>
    private static void BuildHuffmanTree(VorbisCodebook codebook)
    {
        // Find first used entry
        int firstUsed = -1;
        for (int i = 0; i < codebook.EntryCount; i++)
        {
            if (codebook.CodewordLengths[i] > 0)
            {
                firstUsed = i;
                break;
            }
        }
        
        if (firstUsed < 0)
        {
            codebook.DecodeTree = Array.Empty<int>();
            return;
        }
        
        // Generate LSB-first codewords using FFmpeg's len2vlc algorithm.
        // exit_at_level[d] tracks the next available code at depth d in the LSB-first tree.
        // A non-zero value means there's an "exit point" (available branch) at that depth.
        Span<uint> exitAtLevel = stackalloc uint[33];
        exitAtLevel.Clear();
        
        int entryCount = codebook.EntryCount;
        Span<uint> codes = stackalloc uint[Math.Min(entryCount, 1024)];
        uint[]? codesHeap = entryCount > 1024 ? new uint[entryCount] : null;
        if (codesHeap != null)
            codes = codesHeap;
        else
            codes = codes[..entryCount];
        
        // First used entry gets code 0 (leftmost path)
        codes[firstUsed] = 0;
        int firstLen = codebook.CodewordLengths[firstUsed];
        for (int i = 0; i < firstLen; i++)
            exitAtLevel[i + 1] = 1u << i;
        
        // Process remaining entries in order (matching Vorbis spec's entry-order assignment)
        for (int p = firstUsed + 1; p < entryCount; p++)
        {
            int len = codebook.CodewordLengths[p];
            if (len == 0)
                continue;
            
            // Find the highest available exit point at or below this entry's depth
            int exitLevel = 0;
            for (int i = len; i > 0; i--)
            {
                if (exitAtLevel[i] != 0)
                {
                    exitLevel = i;
                    break;
                }
            }
            
            if (exitLevel == 0)
                break; // Overspecified tree
            
            uint code = exitAtLevel[exitLevel];
            exitAtLevel[exitLevel] = 0;
            
            // Extend the code down to the required depth, creating new exit points
            for (int j = exitLevel + 1; j <= len; j++)
                exitAtLevel[j] = code + (1u << (j - 1));
            
            codes[p] = code;
        }
        
        // Build binary tree from the LSB-first codes.
        // Node N has children at DecodeTree[N*2+0] (bit=0) and DecodeTree[N*2+1] (bit=1).
        // Positive values = internal node index. Negative values = ~entry_index (leaf).
        const int sentinel = int.MinValue;
        int nodeCount = 1;
        int treeCapacity = 256;
        var tree = new int[treeCapacity];
        Array.Fill(tree, sentinel);
        
        for (int i = 0; i < entryCount; i++)
        {
            int length = codebook.CodewordLengths[i];
            if (length == 0)
                continue;
            
            uint codeword = codes[i];
            
            // Walk tree using LSB-first bits: bit 0 at root, bit 1 at depth 1, etc.
            int node = 0;
            for (int bit = 0; bit < length; bit++)
            {
                int childBit = (int)((codeword >> bit) & 1);
                int index = node * 2 + childBit;
                
                if (bit == length - 1)
                {
                    tree[index] = ~i;
                }
                else
                {
                    if (tree[index] == sentinel)
                    {
                        int newNode = nodeCount++;
                        tree[index] = newNode;
                        
                        int needed = (newNode + 1) * 2;
                        if (needed > treeCapacity)
                        {
                            int newCapacity = Math.Max(treeCapacity * 2, needed);
                            var newTree = new int[newCapacity];
                            Array.Copy(tree, newTree, treeCapacity);
                            Array.Fill(newTree, sentinel, treeCapacity, newCapacity - treeCapacity);
                            tree = newTree;
                            treeCapacity = newCapacity;
                        }
                    }
                    node = tree[index];
                }
            }
        }
        
        int usedSize = nodeCount * 2;
        if (usedSize < treeCapacity)
        {
            codebook.DecodeTree = new int[usedSize];
            Array.Copy(tree, codebook.DecodeTree, usedSize);
        }
        else
        {
            codebook.DecodeTree = tree;
        }
    }
    
    /// <summary>
    /// Builds VQ vector lookup table.
    /// </summary>
    private static void BuildVqLookup(VorbisCodebook codebook)
    {
        codebook.VectorLookup = new float[codebook.EntryCount * codebook.Dimensions];
        
        if (codebook.LookupType == 1)
        {
            // Type 1: lattice VQ
            for (int entry = 0; entry < codebook.EntryCount; entry++)
            {
                float last = 0;
                int indexDivisor = 1;
                
                for (int dim = 0; dim < codebook.Dimensions; dim++)
                {
                    int multiplicandIndex = (entry / indexDivisor) % codebook.LookupValues;
                    float value = codebook.Multiplicands[multiplicandIndex] * codebook.DeltaValue + codebook.MinimumValue + last;
                    
                    codebook.VectorLookup[entry * codebook.Dimensions + dim] = value;
                    
                    if (codebook.SequenceP)
                        last = value;
                    
                    indexDivisor *= codebook.LookupValues;
                }
            }
        }
        else if (codebook.LookupType == 2)
        {
            // Type 2: packed VQ
            for (int entry = 0; entry < codebook.EntryCount; entry++)
            {
                float last = 0;
                int offset = entry * codebook.Dimensions;
                
                for (int dim = 0; dim < codebook.Dimensions; dim++)
                {
                    float value = codebook.Multiplicands[offset + dim] * codebook.DeltaValue + codebook.MinimumValue + last;
                    
                    codebook.VectorLookup[offset + dim] = value;
                    
                    if (codebook.SequenceP)
                        last = value;
                }
            }
        }
    }
    
    /// <summary>
    /// Parses floor type 0 configuration (FFmpeg: vorbis_parse_setup_hdr_floors type 0 block).
    /// </summary>
    private static VorbisFloor0Configuration ParseFloor0(ref VorbisBitReader reader,
                                                          VorbisCodebook[] codebooks,
                                                          VorbisIdentificationHeader identHeader)
    {
        var floor = new VorbisFloor0Configuration();
        floor.FloorType = VorbisFloorType.Type0;
        
        floor.Order = (int)reader.ReadBits(8);
        if (floor.Order == 0)
            throw new FormatException("Floor 0 order is 0");
        
        floor.Rate = (int)reader.ReadBits(16);
        if (floor.Rate == 0)
            throw new FormatException("Floor 0 rate is 0");
        
        floor.BarkMapSize = (int)reader.ReadBits(16);
        if (floor.BarkMapSize == 0)
            throw new FormatException("Floor 0 bark map size is 0");
        
        floor.AmplitudeBits = (int)reader.ReadBits(6);
        floor.AmplitudeOffset = (int)reader.ReadBits(8);
        int numBooks = (int)reader.ReadBits(4) + 1;
        
        floor.BookList = new int[numBooks];
        int maxCodebookDim = 0;
        for (int idx = 0; idx < numBooks; idx++)
        {
            int bookIdx = (int)reader.ReadBits(8);
            if (bookIdx >= codebooks.Length)
                throw new FormatException($"Floor 0 book index {bookIdx} out of range");
            floor.BookList[idx] = bookIdx;
            if (codebooks[bookIdx].Dimensions > maxCodebookDim)
                maxCodebookDim = codebooks[bookIdx].Dimensions;
        }
        floor.MaxCodebookDim = maxCodebookDim;
        
        // Create bark-scale frequency maps for both blocksizes
        CreateFloor0Map(floor, identHeader);
        
        // Allocate LSP scratch buffer (order + 1 + max_codebook_dim for padding)
        floor.Lsp = new float[floor.Order + 1 + maxCodebookDim];
        
        return floor;
    }
    
    /// <summary>
    /// Creates bark-scale frequency maps for floor type 0 (FFmpeg: create_map).
    /// </summary>
    private static void CreateFloor0Map(VorbisFloor0Configuration floor, VorbisIdentificationHeader identHeader)
    {
        int[] blockSizes = { identHeader.BlockSize0, identHeader.BlockSize1 };
        
        for (int blockflag = 0; blockflag < 2; blockflag++)
        {
            int n = blockSizes[blockflag] / 2;
            var map = new int[n + 1]; // n + sentinel
            
            for (int idx = 0; idx < n; idx++)
            {
                float barkVal = Bark(floor.Rate * idx / (2.0f * n)) *
                                (floor.BarkMapSize / Bark(floor.Rate / 2.0f));
                map[idx] = (int)MathF.Floor(barkVal);
                if (map[idx] > floor.BarkMapSize - 1)
                    map[idx] = floor.BarkMapSize - 1;
            }
            map[n] = -1; // sentinel
            floor.Map[blockflag] = map;
            floor.MapSize[blockflag] = n;
        }
    }
    
    /// <summary>
    /// BARK frequency scale conversion (FFmpeg: BARK macro).
    /// </summary>
    private static float Bark(float x)
    {
        return 13.1f * MathF.Atan(0.00074f * x) + 2.24f * MathF.Atan(1.85e-8f * x * x) + 1e-4f * x;
    }
    
    /// <summary>
    /// Parses floor type 1 configuration.
    /// </summary>
    private static VorbisFloor1Configuration ParseFloor1(ref VorbisBitReader reader)
    {
        var floor = new VorbisFloor1Configuration();
        floor.FloorType = VorbisFloorType.Type1;
        
        floor.Partitions = (int)reader.ReadBits(5);
        
        int maxClass = -1;
        floor.PartitionClassList = new int[floor.Partitions];
        
        for (int i = 0; i < floor.Partitions; i++)
        {
            floor.PartitionClassList[i] = (int)reader.ReadBits(4);
            if (floor.PartitionClassList[i] > maxClass)
                maxClass = floor.PartitionClassList[i];
        }
        
        int classCount = maxClass + 1;
        floor.ClassDimensions = new int[classCount];
        floor.ClassSubclasses = new int[classCount];
        floor.ClassMasterbooks = new int[classCount];
        floor.SubclassBooks = new int[classCount * 8];
        
        for (int i = 0; i < classCount; i++)
        {
            floor.ClassDimensions[i] = (int)reader.ReadBits(3) + 1;
            floor.ClassSubclasses[i] = (int)reader.ReadBits(2);
            
            if (floor.ClassSubclasses[i] != 0)
            {
                floor.ClassMasterbooks[i] = (int)reader.ReadBits(8);
            }
            
            int subclassCount = 1 << floor.ClassSubclasses[i];
            for (int j = 0; j < subclassCount; j++)
            {
                floor.SubclassBooks[i * 8 + j] = (int)reader.ReadBits(8) - 1;
            }
        }
        
        floor.Multiplier = (int)reader.ReadBits(2) + 1;
        floor.RangeBits = (int)reader.ReadBits(4);
        
        // Build X list
        int xListLength = 2;
        for (int i = 0; i < floor.Partitions; i++)
        {
            xListLength += floor.ClassDimensions[floor.PartitionClassList[i]];
        }
        
        floor.XList = new int[xListLength];
        floor.XList[0] = 0;
        floor.XList[1] = 1 << floor.RangeBits;
        
        int xIndex = 2;
        for (int i = 0; i < floor.Partitions; i++)
        {
            int classIndex = floor.PartitionClassList[i];
            for (int j = 0; j < floor.ClassDimensions[classIndex]; j++)
            {
                floor.XList[xIndex++] = (int)reader.ReadBits(floor.RangeBits);
            }
        }
        
        // Build sort order and neighbors
        BuildFloorNeighbors(floor);
        
        return floor;
    }
    
    /// <summary>
    /// Builds floor neighbor information.
    /// </summary>
    private static void BuildFloorNeighbors(VorbisFloor1Configuration floor)
    {
        int count = floor.XList.Length;
        
        // Build sorted order
        floor.XListSortOrder = new int[count];
        for (int i = 0; i < count; i++)
        {
            floor.XListSortOrder[i] = i;
        }
        
        // Sort by X value
        Array.Sort(floor.XListSortOrder, (a, b) => floor.XList[a].CompareTo(floor.XList[b]));
        
        // Build neighbor indices (low_neighbor and high_neighbor)
        floor.LowNeighbor = new int[count];
        floor.HighNeighbor = new int[count];
        
        for (int i = 0; i < count; i++)
        {
            floor.LowNeighbor[i] = LowNeighbor(floor.XList, i);
            floor.HighNeighbor[i] = HighNeighbor(floor.XList, i);
        }
    }
    
    /// <summary>
    /// Finds low neighbor index.
    /// </summary>
    private static int LowNeighbor(int[] xList, int n)
    {
        int candidate = -1;
        int candidateX = int.MinValue;
        
        for (int i = 0; i < n; i++)
        {
            if (xList[i] < xList[n] && xList[i] > candidateX)
            {
                candidateX = xList[i];
                candidate = i;
            }
        }
        
        return candidate == -1 ? 0 : candidate;
    }
    
    /// <summary>
    /// Finds high neighbor index.
    /// </summary>
    private static int HighNeighbor(int[] xList, int n)
    {
        int candidate = -1;
        int candidateX = int.MaxValue;
        
        for (int i = 0; i < n; i++)
        {
            if (xList[i] > xList[n] && xList[i] < candidateX)
            {
                candidateX = xList[i];
                candidate = i;
            }
        }
        
        return candidate == -1 ? 1 : candidate;
    }
    
    /// <summary>
    /// Parses residue configuration.
    /// </summary>
    private static VorbisResidueConfiguration ParseResidue(ref VorbisBitReader reader, VorbisCodebook[] codebooks)
    {
        var residue = new VorbisResidueConfiguration();
        
        int type = (int)reader.ReadBits(16);
        if (type > 2)
            throw new FormatException($"Unsupported residue type: {type}");
        
        residue.Type = (VorbisResidueType)type;
        residue.Begin = (int)reader.ReadBits(24);
        residue.End = (int)reader.ReadBits(24);
        residue.PartitionSize = (int)reader.ReadBits(24) + 1;
        residue.Classifications = (int)reader.ReadBits(6) + 1;
        residue.Classbook = (int)reader.ReadBits(8);
        
        residue.Cascade = new byte[residue.Classifications];
        for (int i = 0; i < residue.Classifications; i++)
        {
            int highBits = 0;
            int lowBits = (int)reader.ReadBits(3);
            
            if (reader.ReadBool())
            {
                highBits = (int)reader.ReadBits(5);
            }
            
            residue.Cascade[i] = (byte)(highBits * 8 + lowBits);
        }
        
        residue.Books = new int[residue.Classifications * 8];
        int maxPass = 0;
        for (int i = 0; i < residue.Classifications; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                if ((residue.Cascade[i] & (1 << j)) != 0)
                {
                    residue.Books[i * 8 + j] = (int)reader.ReadBits(8);
                    if (j > maxPass)
                        maxPass = j;
                }
                else
                {
                    residue.Books[i * 8 + j] = -1;
                }
            }
        }
        residue.MaxPass = maxPass;
        
        return residue;
    }
    
    /// <summary>
    /// Parses mapping configuration.
    /// </summary>
    private static VorbisMappingConfiguration ParseMapping(ref VorbisBitReader reader, int channelCount)
    {
        var mapping = new VorbisMappingConfiguration();
        
        int mappingType = (int)reader.ReadBits(16);
        if (mappingType != 0)
            throw new FormatException($"Unsupported mapping type: {mappingType}");
        
        // Submaps
        if (reader.ReadBool())
        {
            mapping.SubmapCount = (int)reader.ReadBits(4) + 1;
        }
        else
        {
            mapping.SubmapCount = 1;
        }
        
        // Coupling
        if (reader.ReadBool())
        {
            mapping.CouplingSteps = (int)reader.ReadBits(8) + 1;
            mapping.CouplingMagnitude = new int[mapping.CouplingSteps];
            mapping.CouplingAngle = new int[mapping.CouplingSteps];
            
            int channelBits = VorbisBitReader.ILog(channelCount - 1);
            
            for (int i = 0; i < mapping.CouplingSteps; i++)
            {
                mapping.CouplingMagnitude[i] = (int)reader.ReadBits(channelBits);
                mapping.CouplingAngle[i] = (int)reader.ReadBits(channelBits);
            }
        }
        else
        {
            mapping.CouplingSteps = 0;
            mapping.CouplingMagnitude = Array.Empty<int>();
            mapping.CouplingAngle = Array.Empty<int>();
        }
        
        // Reserved
        int reserved = (int)reader.ReadBits(2);
        if (reserved != 0)
            throw new FormatException("Invalid reserved bits in mapping");
        
        // Mux
        mapping.Mux = new int[channelCount];
        if (mapping.SubmapCount > 1)
        {
            for (int i = 0; i < channelCount; i++)
            {
                mapping.Mux[i] = (int)reader.ReadBits(4);
            }
        }
        
        // Submap floor/residue
        mapping.SubmapFloor = new int[mapping.SubmapCount];
        mapping.SubmapResidue = new int[mapping.SubmapCount];
        
        for (int i = 0; i < mapping.SubmapCount; i++)
        {
            reader.ReadBits(8); // Reserved
            mapping.SubmapFloor[i] = (int)reader.ReadBits(8);
            mapping.SubmapResidue[i] = (int)reader.ReadBits(8);
        }
        
        return mapping;
    }
    
    /// <summary>
    /// Parses mode configuration.
    /// </summary>
    private static VorbisModeConfiguration ParseMode(ref VorbisBitReader reader)
    {
        bool blockFlag = reader.ReadBool();
        int windowType = (int)reader.ReadBits(16);
        int transformType = (int)reader.ReadBits(16);
        int mapping = (int)reader.ReadBits(8);
        
        if (windowType != 0)
            throw new FormatException($"Unsupported window type: {windowType}");
        if (transformType != 0)
            throw new FormatException($"Unsupported transform type: {transformType}");
        
        return new VorbisModeConfiguration(blockFlag, windowType, transformType, mapping);
    }
}

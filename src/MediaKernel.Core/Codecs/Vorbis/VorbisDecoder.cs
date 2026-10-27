// Licensed under the MIT License.
// Vorbis audio decoder implementation.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>
/// Complete Vorbis audio decoder.
/// </summary>
public class VorbisDecoder
{
    private readonly float[] outputBuffer;
    private readonly float[][] channelBuffers;  // Per-channel output buffer (reused as ret in FFmpeg)
    private readonly float[][] savedBuffers;    // Per-channel saved overlap (N/4 values, matching FFmpeg's vc->saved)
    private readonly float[][] imdctBuffers;    // Per-channel IMDCT half output (N/2 values)
    private readonly VorbisChannelState[] channelStates;
    private readonly bool[] doNotDecodeBuffer;
    private readonly float[] interleavedResidueBuffer;
    private readonly int[] channelIndicesBuffer;
    // Per-channel classification values: classifs[ch * maxPartitions + partition]
    private readonly byte[] classifsBuffer;
    
    private VorbisIdentificationHeader identHeader;
    private VorbisCommentHeader? commentHeader;
    private VorbisSetupHeader? setupHeader;
    
    private VorbisMdct mdctShort;
    private VorbisMdct mdctLong;
    
    private bool isInitialized;
    private int previousWindow;  // -1 = first frame, 0 = short, 1 = long (matches FFmpeg)
    private bool firstFrame;     // True until first audio frame is returned
    
    /// <summary>Gets the sample rate.</summary>
    public int SampleRate => (int)identHeader.SampleRate;
    
    /// <summary>Gets the channel count.</summary>
    public int ChannelCount => identHeader.ChannelCount;
    
    /// <summary>Gets the nominal bitrate (0 if not specified).</summary>
    public int BitrateNominal => identHeader.BitrateNominal;
    
    /// <summary>Gets the vendor string from comments.</summary>
    public string? Vendor => commentHeader?.Vendor;
    
    /// <summary>Gets whether the decoder is initialized.</summary>
    public bool IsInitialized => isInitialized;
    
    /// <summary>Gets the maximum block size.</summary>
    public int MaxBlockSize => identHeader.BlockSize1;
    
    /// <summary>Gets the short block size.</summary>
    public int BlockSize0 => identHeader.BlockSize0;
    
    /// <summary>Gets the long block size.</summary>
    public int BlockSize1 => identHeader.BlockSize1;
    
    /// <summary>
    /// Creates a new Vorbis decoder.
    /// </summary>
    public VorbisDecoder()
    {
        // Pre-allocate for maximum possible configuration (8 channels, 8192 block size)
        int maxChannels = 8;
        int maxBlockSize = 8192;
        
        outputBuffer = new float[maxBlockSize * maxChannels];
        channelBuffers = new float[maxChannels][];
        savedBuffers = new float[maxChannels][];
        imdctBuffers = new float[maxChannels][];
        channelStates = new VorbisChannelState[maxChannels];
        
        for (int i = 0; i < maxChannels; i++)
        {
            channelBuffers[i] = new float[maxBlockSize];  // Output/ret buffer
            savedBuffers[i] = new float[maxBlockSize / 4]; // Saved overlap (N/4 for largest block)
            imdctBuffers[i] = new float[maxBlockSize / 2]; // IMDCT half output
            channelStates[i] = new VorbisChannelState(maxBlockSize);
        }
        
        doNotDecodeBuffer = new bool[maxChannels];
        interleavedResidueBuffer = new float[maxBlockSize * maxChannels];
        channelIndicesBuffer = new int[maxChannels];
        // Max partitions = maxBlockSize / min_partition_size(~4) ≈ 2048 per channel
        classifsBuffer = new byte[maxChannels * 2048];
        
        // Initialize with defaults
        identHeader = default;
        mdctShort = new VorbisMdct(256);
        mdctLong = new VorbisMdct(2048);
    }
    
    /// <summary>
    /// Initializes the decoder with header packets.
    /// </summary>
    /// <param name="identificationHeader">First Vorbis header packet.</param>
    /// <param name="commentHeader">Second Vorbis header packet.</param>
    /// <param name="setupHeader">Third Vorbis header packet.</param>
    public void Initialize(ReadOnlySpan<byte> identificationHeader, 
                          ReadOnlySpan<byte> commentHeader, 
                          ReadOnlySpan<byte> setupHeader)
    {
        // Parse identification header
        identHeader = VorbisHeaderParser.ParseIdentificationHeader(identificationHeader);
        
        // Parse comment header
        this.commentHeader = VorbisHeaderParser.ParseCommentHeader(commentHeader);
        
        // Parse setup header
        this.setupHeader = VorbisHeaderParser.ParseSetupHeader(setupHeader, identHeader);
        
        // Initialize MDCT for both block sizes
        mdctShort = new VorbisMdct(identHeader.BlockSize0);
        mdctLong = new VorbisMdct(identHeader.BlockSize1);
        
        // Clear saved buffers
        for (int ch = 0; ch < identHeader.ChannelCount; ch++)
        {
            Array.Clear(savedBuffers[ch]);
        }
        
        previousWindow = -1;
        firstFrame = true;
        isInitialized = true;
    }
    
    /// <summary>
    /// Decodes an audio packet.
    /// </summary>
    /// <param name="packet">Vorbis audio packet data.</param>
    /// <returns>Decoded PCM samples (interleaved).</returns>
    public Span<float> DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (!isInitialized || setupHeader == null)
            throw new InvalidOperationException("Decoder not initialized");
        
        if (packet.Length == 0)
            return Span<float>.Empty;
        
        var reader = new VorbisBitReader(packet);
        
        // Check packet type (0 = audio)
        if (reader.ReadBit() != 0)
            throw new FormatException("Not an audio packet");
        
        // Read mode number
        int modeBits = VorbisBitReader.ILog(setupHeader.Modes.Length - 1);
        int modeNumber = (int)reader.ReadBits(modeBits);
        
        if (modeNumber >= setupHeader.Modes.Length)
            throw new FormatException($"Invalid mode number: {modeNumber}");
        
        var mode = setupHeader.Modes[modeNumber];
        var mapping = setupHeader.Mappings[mode.Mapping];
        
        // Determine block size
        bool isLongBlock = mode.BlockFlag;
        int blockSize = isLongBlock ? identHeader.BlockSize1 : identHeader.BlockSize0;
        int halfBlock = blockSize / 2;
        
        // For long blocks, read window type hints
        bool previousWindowFlag = false;
        bool nextWindowFlag = false;
        if (isLongBlock)
        {
            previousWindowFlag = reader.ReadBool();
            nextWindowFlag = reader.ReadBool();
        }
        
        // Reset channel states
        for (int ch = 0; ch < identHeader.ChannelCount; ch++)
        {
            channelStates[ch].Reset();
        }
        
        // Decode floors for each channel
        int blockflag = isLongBlock ? 1 : 0;
        DecodeFloors(ref reader, mapping, halfBlock, blockflag);
        
        // Nonzero vector propagation (FFmpeg vorbisdec.c lines 1677-1682)
        // If either channel of a coupled pair has energy, both must be decoded
        for (int i = mapping.CouplingSteps - 1; i >= 0; i--)
        {
            int magCh = mapping.CouplingMagnitude[i];
            int angCh = mapping.CouplingAngle[i];
            if (channelStates[magCh].HasEnergy || channelStates[angCh].HasEnergy)
            {
                channelStates[magCh].HasEnergy = true;
                channelStates[angCh].HasEnergy = true;
            }
        }
        
        // Decode residues
        DecodeResidues(ref reader, mapping, halfBlock);
        
        // Apply coupling
        ApplyCoupling(mapping, halfBlock);
        
        // Combine floor and residue, then inverse MDCT and overlap-add
        int blockflagInt = isLongBlock ? 1 : 0;
        int outputSamples = ApplyMdctAndOverlap(blockflagInt, blockSize, previousWindowFlag, nextWindowFlag);
        
        // Update state for next frame
        previousWindow = blockflagInt;
        
        // Skip first frame (initial_padding, matching FFmpeg)
        if (firstFrame)
        {
            firstFrame = false;
            return Span<float>.Empty;
        }
        
        // Interleave channels
        return InterleaveOutput(outputSamples);
    }
    
    /// <summary>
    /// Decodes floor data for all channels.
    /// </summary>
    private void DecodeFloors(ref VorbisBitReader reader, VorbisMappingConfiguration mapping, int halfBlock, int blockflag)
    {
        for (int ch = 0; ch < identHeader.ChannelCount; ch++)
        {
            int submap = mapping.Mux[ch];
            int floorIndex = mapping.SubmapFloor[submap];
            var floor = setupHeader!.Floors[floorIndex];
            
            if (floor is VorbisFloor1Configuration floor1)
                channelStates[ch].HasEnergy = DecodeFloor1(ref reader, floor1, channelStates[ch].FloorOutput, halfBlock);
            else if (floor is VorbisFloor0Configuration floor0)
                channelStates[ch].HasEnergy = DecodeFloor0(ref reader, floor0, channelStates[ch].FloorOutput, halfBlock, blockflag);
            else
                channelStates[ch].HasEnergy = false;
        }
    }
    
    /// <summary>
    /// Decodes floor type 0 data (FFmpeg: vorbis_floor0_decode).
    /// LSP-based floor reconstruction using bark-scale mapping.
    /// </summary>
    private bool DecodeFloor0(ref VorbisBitReader reader, VorbisFloor0Configuration floor,
                              Span<float> output, int n, int blockflag)
    {
        if (floor.AmplitudeBits == 0)
        {
            output[..n].Clear();
            return false;
        }
        
        ulong amplitude = reader.ReadBits64(floor.AmplitudeBits);
        if (amplitude == 0)
        {
            output[..n].Clear();
            return false;
        }
        
        float last = 0;
        int lspLen = 0;
        var lsp = floor.Lsp;
        
        int bookIdx = (int)reader.ReadBits(VorbisBitReader.ILog(floor.BookList.Length));
        if (bookIdx >= floor.BookList.Length)
            bookIdx = 0;
        
        var codebook = setupHeader!.Codebooks[floor.BookList[bookIdx]];
        if (codebook.VectorLookup == null)
            return false;
        
        while (lspLen < floor.Order)
        {
            var vq = reader.ReadVq(codebook);
            if (vq.IsEmpty)
                return false;
            
            for (int idx = 0; idx < codebook.Dimensions && lspLen + idx < lsp.Length; idx++)
                lsp[lspLen + idx] = vq[idx] + last;
            
            if (lspLen + codebook.Dimensions - 1 < lsp.Length)
                last = lsp[lspLen + codebook.Dimensions - 1];
            
            lspLen += codebook.Dimensions;
        }
        
        // Synthesize floor output: LSP to floor curve
        int order = floor.Order;
        float wstep = MathF.PI / floor.BarkMapSize;
        
        // Convert LSP coefficients: lsp[i] = 2 * cos(lsp[i])
        for (int i = 0; i < order; i++)
            lsp[i] = 2.0f * MathF.Cos(lsp[i]);
        
        var map = floor.Map[blockflag];
        int mapSize = floor.MapSize[blockflag];
        if (map == null || mapSize <= 0)
            return false;
        
        int pos = 0;
        while (pos < mapSize)
        {
            int iterCond = map[pos];
            float p = 0.5f;
            float q = 0.5f;
            float twoCosW = 2.0f * MathF.Cos(wstep * iterCond);
            
            // Evaluate the polynomial products
            int j;
            for (j = 0; j + 1 < order; j += 2)
            {
                q *= lsp[j] - twoCosW;
                p *= lsp[j + 1] - twoCosW;
            }
            
            if (j == order)
            {
                // Even order
                p *= p * (2.0f - twoCosW);
                q *= q * (2.0f + twoCosW);
            }
            else
            {
                // Odd order
                q *= twoCosW - lsp[j];
                p *= p * (4.0f - twoCosW * twoCosW);
                q *= q;
            }
            
            if (p + q == 0.0f)
                return false;
            
            // Calculate linear floor value
            float val = MathF.Exp((float)(((amplitude * (ulong)floor.AmplitudeOffset) /
                        (double)(((1UL << floor.AmplitudeBits) - 1) * Math.Sqrt(p + q)))
                        - floor.AmplitudeOffset) * 0.11512925f);
            
            // Fill vector for all positions with same bark map value
            do
            {
                if (pos < output.Length)
                    output[pos] = val;
                pos++;
            } while (pos < mapSize && map[pos] == iterCond);
        }
        
        return true;
    }
    
    /// <summary>
    /// Decodes floor type 1 data.
    /// </summary>
    private bool DecodeFloor1(ref VorbisBitReader reader, VorbisFloor1Configuration floor, 
                              Span<float> output, int n)
    {
        // Read amplitude flag
        if (!reader.ReadBool())
        {
            // Zero floor (channel is silent)
            output[..n].Clear();
            return false;
        }
        
        // Decode Y values
        Span<int> yList = stackalloc int[floor.XList.Length];
        Span<bool> step2Flag = stackalloc bool[floor.XList.Length];
        Span<int> finalY = stackalloc int[floor.XList.Length];
        
        int range = GetFloorRange(floor.Multiplier);
        int rangeBits = VorbisBitReader.ILog(range - 1);
        
        // Read first two Y values
        yList[0] = (int)reader.ReadBits(rangeBits);
        yList[1] = (int)reader.ReadBits(rangeBits);
        
        
        // Decode remaining Y values by class
        int offset = 2;
        for (int partition = 0; partition < floor.Partitions; partition++)
        {
            int classIndex = floor.PartitionClassList[partition];
            int cdim = floor.ClassDimensions[classIndex];
            int cbits = floor.ClassSubclasses[classIndex];
            int csub = (1 << cbits) - 1;
            int cval = 0;
            
            if (cbits > 0)
            {
                int masterbook = floor.ClassMasterbooks[classIndex];
                cval = reader.ReadHuffman(setupHeader!.Codebooks[masterbook]);
            }
            
            for (int j = 0; j < cdim; j++)
            {
                int book = floor.SubclassBooks[classIndex * 8 + (cval & csub)];
                cval >>= cbits;
                
                if (book >= 0)
                {
                    yList[offset] = reader.ReadHuffman(setupHeader!.Codebooks[book]);
                }
                else
                {
                    yList[offset] = 0;
                }
                offset++;
            }
        }
        
        // Amplitude curve synthesis (step 2)
        step2Flag[0] = true;
        step2Flag[1] = true;
        finalY[0] = yList[0];
        finalY[1] = yList[1];
        
        for (int i = 2; i < floor.XList.Length; i++)
        {
            int lowNeighbor = floor.LowNeighbor[i];
            int highNeighbor = floor.HighNeighbor[i];
            int predicted = RenderPoint(floor.XList[lowNeighbor], finalY[lowNeighbor],
                                        floor.XList[highNeighbor], finalY[highNeighbor],
                                        floor.XList[i]);
            
            int val = yList[i];
            int highRoom = range - predicted;
            int lowRoom = predicted;
            int room = Math.Min(highRoom, lowRoom) * 2;
            
            if (val != 0)
            {
                step2Flag[lowNeighbor] = true;
                step2Flag[highNeighbor] = true;
                step2Flag[i] = true;
                
                if (val >= room)
                {
                    if (highRoom > lowRoom)
                        finalY[i] = val - lowRoom + predicted;
                    else
                        finalY[i] = predicted - val + highRoom - 1;
                }
                else
                {
                    if ((val & 1) != 0)
                        finalY[i] = predicted - ((val + 1) >> 1);
                    else
                        finalY[i] = predicted + (val >> 1);
                }
            }
            else
            {
                step2Flag[i] = false;
                finalY[i] = predicted;
            }
        }
        
        // Render curve to output (matching FFmpeg's ff_vorbis_floor1_render_list)
        // FFmpeg: lx=0, ly=y_list[0]*multiplier, render from lx,ly to each flagged point
        int mult = floor.Multiplier;
        int lx = 0;
        int ly = finalY[0] * mult;
        
        for (int i = 1; i < floor.XList.Length; i++)
        {
            int sortedIndex = floor.XListSortOrder[i];
            if (step2Flag[sortedIndex])
            {
                int hx = floor.XList[sortedIndex];
                if (hx >= n)
                {
                    // Render to end of output
                    RenderFloor1Line(lx, n, ly, finalY[sortedIndex] * mult, output);
                    lx = n;
                    break;
                }
                
                RenderFloor1Line(lx, hx, ly, finalY[sortedIndex] * mult, output);
                lx = hx;
                ly = finalY[sortedIndex] * mult;
            }
        }
        
        // Fill remaining with last value
        if (lx < n)
            RenderFloor1Line(lx, n, ly, ly, output);
        
        return true;
    }
    
    /// <summary>
    /// Gets the floor range for a multiplier.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetFloorRange(int multiplier)
    {
        return multiplier switch
        {
            1 => 256,
            2 => 128,
            3 => 86,
            4 => 64,
            _ => 256
        };
    }
    
    /// <summary>
    /// Renders a point on the floor curve.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int err = ady * (x - x0);
        int off = err / adx;
        
        return dy < 0 ? y0 - off : y0 + off;
    }
    
    /// <summary>
    /// Renders a floor1 line segment (FFmpeg: render_line).
    /// Y values are pre-multiplied by the floor multiplier for correct Bresenham resolution.
    /// </summary>
    private static void RenderFloor1Line(int x0, int x1, int y0, int y1, Span<float> output)
    {
        if (x0 >= output.Length)
            return;
        
        x1 = Math.Min(x1, output.Length);
        
        int adx = x1 - x0;
        if (adx <= 0)
        {
            if (x0 < output.Length)
                output[x0] = FloorDbToLinear(y0);
            return;
        }
        
        int dy = y1 - y0;
        int ady = Math.Abs(dy);
        int sy = dy < 0 ? -1 : 1;
        int base_ = dy / adx;
        int y = y0;
        int err = -adx;
        ady -= Math.Abs(base_) * adx;
        
        output[x0] = FloorDbToLinear(y0);
        
        int x = x0;
        while (++x < x1)
        {
            y += base_;
            err += ady;
            if (err >= 0)
            {
                err -= adx;
                y += sy;
            }
            if (x < output.Length)
                output[x] = FloorDbToLinear(y);
        }
    }
    
    /// <summary>
    /// Converts pre-multiplied floor Y value to linear amplitude using the Vorbis spec lookup table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FloorDbToLinear(int y)
    {
        // Clamp to [0, 255] per spec (av_clip_uint8 in FFmpeg)
        if (y < 0) y = 0;
        else if (y > 255) y = 255;
        return VorbisTables.Floor1InverseDbTable[y];
    }
    
    /// <summary>
    /// Decodes residue data.
    /// </summary>
    private void DecodeResidues(ref VorbisBitReader reader, VorbisMappingConfiguration mapping, int halfBlock)
    {
        for (int submap = 0; submap < mapping.SubmapCount; submap++)
        {
            int residueIndex = mapping.SubmapResidue[submap];
            var residue = setupHeader!.Residues[residueIndex];
            
            // Determine which channels use this submap
            int channelCount = 0;
            for (int ch = 0; ch < identHeader.ChannelCount; ch++)
            {
                if (mapping.Mux[ch] == submap)
                {
                    doNotDecodeBuffer[channelCount] = !channelStates[ch].HasEnergy;
                    channelCount++;
                }
            }
            
            // Decode residue
            DecodeResidue(ref reader, residue, mapping, submap, halfBlock, channelCount);
        }
    }
    
    /// <summary>
    /// Decodes a single residue (FFmpeg: vorbis_residue_decode_internal).
    /// </summary>
    private void DecodeResidue(ref VorbisBitReader reader, VorbisResidueConfiguration residue,
                               VorbisMappingConfiguration mapping, int submap, int vlen, 
                               int ch)
    {
        // Build channel indices list first (needed for ptnsToRead calculation)
        int chUsed = 0;
        for (int c = 0; c < identHeader.ChannelCount; c++)
        {
            if (mapping.Mux[c] == submap)
                channelIndicesBuffer[chUsed++] = c;
        }
        
        int vrType = (int)residue.Type;
        
        // For Type 2, the residue vector is interleaved: length = vlen * ch (Vorbis spec §8.6.2)
        int effectiveLen = (vrType == 2) ? vlen * chUsed : vlen;
        int limitEnd = Math.Max(0, Math.Min(residue.End, effectiveLen));
        int limitBegin = Math.Max(0, Math.Min(residue.Begin, effectiveLen));
        
        int classbookIdx = residue.Classbook;
        var classCodebook = setupHeader!.Codebooks[classbookIdx];
        int cpc = classCodebook.Dimensions; // classwords per codeword
        int ptnsToRead = (limitEnd - limitBegin) / residue.PartitionSize;
        
        if (ptnsToRead <= 0 || residue.PartitionSize <= 0)
            return;
        
        if (vrType == 2)
        {
            // Type 2: AND all do_not_decode flags together
            bool allZero = true;
            for (int j = 0; j < chUsed; j++)
            {
                if (!doNotDecodeBuffer[j]) { allZero = false; break; }
            }
            if (allZero) return;
        }
        
        DecodeResidueInternal(ref reader, residue, classCodebook, cpc, ptnsToRead, vlen, chUsed, vrType);
    }
    
    /// <summary>
    /// Core residue decode loop (ports FFmpeg's vorbis_residue_decode_internal).
    /// </summary>
    private void DecodeResidueInternal(ref VorbisBitReader reader, VorbisResidueConfiguration residue,
                                       VorbisCodebook classCodebook, int cpc, int ptnsToRead,
                                       int vlen, int chUsed, int vrType)
    {
        if (vrType == 2)
        {
            // Type 2: decode into a single interleaved buffer then de-interleave
            int interleavedLen = vlen * chUsed;
            int bufferSize = Math.Min(interleavedLen, interleavedResidueBuffer.Length);
            Array.Clear(interleavedResidueBuffer, 0, bufferSize);
            
            DecodeResidueChannels(ref reader, residue, classCodebook, cpc, ptnsToRead,
                                  interleavedResidueBuffer, bufferSize, 1, vrType);
            
            // De-interleave: FFmpeg uses voffs_div/voffs_mod pattern
            for (int i = 0; i < vlen; i++)
            {
                for (int c = 0; c < chUsed; c++)
                {
                    int idx = i * chUsed + c;
                    if (idx < bufferSize)
                        channelStates[channelIndicesBuffer[c]].ResidueOutput[i] = interleavedResidueBuffer[idx];
                }
            }
        }
        else
        {
            DecodeResidueChannels(ref reader, residue, classCodebook, cpc, ptnsToRead,
                                  null, vlen, chUsed, vrType);
        }
    }
    
    /// <summary>
    /// Classification + vector decode loop matching FFmpeg's structure.
    /// </summary>
    private void DecodeResidueChannels(ref VorbisBitReader reader, VorbisResidueConfiguration residue,
                                       VorbisCodebook classCodebook, int cpc, int ptnsToRead,
                                       float[]? interleavedOutput, int vlen, int chUsed, int vrType)
    {
        // Clear classifs for this decode
        int classifsNeeded = chUsed * ptnsToRead;
        int classifsSize = Math.Min(classifsNeeded, classifsBuffer.Length);
        Array.Clear(classifsBuffer, 0, classifsSize);
        
        for (int pass = 0; pass <= residue.MaxPass; pass++)
        {
            int voffset = residue.Begin;
            
            for (int partitionCount = 0; partitionCount < ptnsToRead; )
            {
                // Classification: one Huffman value per active channel → cpc classification values
                if (pass == 0)
                {
                    for (int j = 0; j < chUsed; j++)
                    {
                        if (vrType != 2 && doNotDecodeBuffer[j])
                            continue;
                        
                        int temp = reader.ReadHuffman(classCodebook);
                        if (temp < 0) return;
                        
                        // Decompose into cpc classification values (matching FFmpeg setup_classifs)
                        for (int i = partitionCount + cpc - 1; i >= partitionCount; i--)
                        {
                            if (i < ptnsToRead)
                            {
                                int idx = j * ptnsToRead + i;
                                if (idx < classifsBuffer.Length)
                                    classifsBuffer[idx] = (byte)(temp % residue.Classifications);
                            }
                            temp /= residue.Classifications;
                        }
                    }
                }
                
                // Decode vectors for cpc partitions
                for (int i = 0; i < cpc && partitionCount < ptnsToRead; i++, partitionCount++)
                {
                    for (int j = 0; j < chUsed; j++)
                    {
                        if (vrType != 2 && doNotDecodeBuffer[j])
                            continue;
                        
                        int classifIdx = j * ptnsToRead + partitionCount;
                        int vqclass = (classifIdx < classifsBuffer.Length) ? classifsBuffer[classifIdx] : 0;
                        int vqbook = residue.Books[vqclass * 8 + pass];
                        
                        if (vqbook < 0)
                            continue;
                        
                        var book = setupHeader!.Codebooks[vqbook];
                        if (book.Dimensions <= 0 || book.VectorLookup == null)
                            continue;
                        
                        if (reader.BitsRemaining < 0)
                            return;
                        
                        int dim = book.Dimensions;
                        int step = residue.PartitionSize / dim;
                        
                        if (vrType == 0)
                        {
                            // Type 0: interleaved vector placement (vec[k + l*step])
                            float[] output = channelStates[channelIndicesBuffer[j]].ResidueOutput;
                            int voffs = voffset;
                            
                            for (int k = 0; k < step; k++)
                            {
                                var vq = reader.ReadVq(book);
                                for (int l = 0; l < vq.Length; l++)
                                {
                                    int idx = voffs + k + l * step;
                                    if (idx < output.Length)
                                        output[idx] += vq[l];
                                }
                            }
                        }
                        else if (vrType == 1)
                        {
                            // Type 1: sequential vector placement
                            float[] output = channelStates[channelIndicesBuffer[j]].ResidueOutput;
                            int voffs = voffset;
                            
                            for (int k = 0; k < step; k++)
                            {
                                var vq = reader.ReadVq(book);
                                for (int l = 0; l < vq.Length && voffs < vlen; l++, voffs++)
                                    output[voffs] += vq[l];
                            }
                        }
                        else // vrType == 2
                        {
                            // Type 2: sequential into interleaved buffer
                            float[] output = interleavedOutput!;
                            int voffs = voffset;
                            
                            for (int k = 0; k < step; k++)
                            {
                                var vq = reader.ReadVq(book);
                                for (int l = 0; l < vq.Length && voffs < output.Length; l++, voffs++)
                                    output[voffs] += vq[l];
                            }
                        }
                    }
                    
                    voffset += residue.PartitionSize;
                }
            }
        }
    }
    /// <summary>
    /// Applies channel coupling (polar to rectangular).
    /// </summary>
    private void ApplyCoupling(VorbisMappingConfiguration mapping, int halfBlock)
    {
        for (int step = mapping.CouplingSteps - 1; step >= 0; step--)
        {
            int magnitudeChannel = mapping.CouplingMagnitude[step];
            int angleChannel = mapping.CouplingAngle[step];
            
            var magnitude = channelStates[magnitudeChannel].ResidueOutput;
            var angle = channelStates[angleChannel].ResidueOutput;
            
            for (int i = 0; i < halfBlock; i++)
            {
                float m = magnitude[i];
                float a = angle[i];
                
                if (m > 0)
                {
                    if (a > 0)
                    {
                        magnitude[i] = m;
                        angle[i] = m - a;
                    }
                    else
                    {
                        magnitude[i] = m + a;
                        angle[i] = m;
                    }
                }
                else
                {
                    if (a > 0)
                    {
                        magnitude[i] = m;
                        angle[i] = m + a;
                    }
                    else
                    {
                        magnitude[i] = m - a;
                        angle[i] = m;
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Applies inverse MDCT and overlap-add, matching FFmpeg's vorbis_parse_audio_packet approach.
    /// Uses imdct_half + vector_fmul_window for correct block size transitions.
    /// </summary>
    private int ApplyMdctAndOverlap(int blockflag, int blockSize, bool prevWindowFlag, bool nextWindowFlag)
    {
        int halfBlock = blockSize / 2;
        int quarterBlock = blockSize / 4;
        var mdct = blockflag != 0 ? mdctLong : mdctShort;
        
        int bs0 = identHeader.BlockSize0;
        int bs1 = identHeader.BlockSize1;
        
        // Determine previous_window for this frame (matching FFmpeg lines 1644-1649)
        int prevWin = previousWindow;
        if (blockflag != 0)
        {
            if (prevWin < 0)
                prevWin = prevWindowFlag ? 1 : 0;
        }
        else if (prevWin < 0)
        {
            prevWin = 0;
        }
        
        // Output length = (currentBlockSize + previousBlockSize) / 4
        int retlen = (blockSize + (prevWin != 0 ? bs1 : bs0)) / 4;
        
        // Select window: win = vc->win[blockflag & previous_window]
        int winIndex = blockflag & prevWin;
        var win = (winIndex != 0 ? mdctLong : mdctShort).Window;
        
        for (int ch = 0; ch < identHeader.ChannelCount; ch++)
        {
            var state = channelStates[ch];
            
            // Multiply floor and residue to get spectrum
            if (state.HasEnergy)
            {
                for (int i = 0; i < halfBlock; i++)
                    state.Spectrum[i] = state.FloorOutput[i] * state.ResidueOutput[i];
            }
            else
            {
                Array.Clear(state.Spectrum, 0, halfBlock);
            }
            
            // IMDCT half: N/2 spectral → N/2 time-domain (unwindowed)
            mdct.InverseMdctHalf(state.Spectrum.AsSpan(0, halfBlock), imdctBuffers[ch]);
            
            var saved = savedBuffers[ch];
            var buf = imdctBuffers[ch];
            var ret = channelBuffers[ch];
            
            // Overlap-add (matching FFmpeg vorbisdec.c lines 1754-1762)
            if (blockflag == prevWin)
            {
                // Same block size: standard windowed overlap
                VorbisMdct.VectorFmulWindow(ret, saved, buf, win, quarterBlock);
            }
            else if (blockflag > prevWin)
            {
                // Short→Long transition (current is long, previous was short)
                VorbisMdct.VectorFmulWindow(ret, saved, buf, win, bs0 / 4);
                // Copy remaining IMDCT output after the overlap region
                buf.AsSpan(bs0 / 4, (bs1 - bs0) / 4).CopyTo(ret.AsSpan(bs0 / 2));
            }
            else
            {
                // Long→Short transition (current is short, previous was long)
                // Copy non-overlapping saved portion
                saved.AsSpan(0, (bs1 - bs0) / 4).CopyTo(ret);
                // Windowed overlap for the center portion
                VorbisMdct.VectorFmulWindow(
                    ret.AsSpan((bs1 - bs0) / 4),
                    saved.AsSpan((bs1 - bs0) / 4),
                    buf,
                    win,
                    bs0 / 4);
            }
            
            // Save second quarter of IMDCT output for next frame
            buf.AsSpan(quarterBlock, quarterBlock).CopyTo(saved);
        }
        
        return retlen;
    }
    
    /// <summary>
    /// Interleaves channel outputs.
    /// </summary>
    private Span<float> InterleaveOutput(int samplesPerChannel)
    {
        int totalSamples = samplesPerChannel * identHeader.ChannelCount;
        
        if (identHeader.ChannelCount == 1)
        {
            // Mono - just copy
            channelBuffers[0].AsSpan(0, samplesPerChannel).CopyTo(outputBuffer);
        }
        else if (identHeader.ChannelCount == 2)
        {
            // Stereo - interleave
            var left = channelBuffers[0];
            var right = channelBuffers[1];
            
            for (int i = 0; i < samplesPerChannel; i++)
            {
                outputBuffer[i * 2] = left[i];
                outputBuffer[i * 2 + 1] = right[i];
            }
        }
        else
        {
            // Multi-channel
            for (int i = 0; i < samplesPerChannel; i++)
            {
                for (int ch = 0; ch < identHeader.ChannelCount; ch++)
                {
                    outputBuffer[i * identHeader.ChannelCount + ch] = channelBuffers[ch][i];
                }
            }
        }
        
        return outputBuffer.AsSpan(0, totalSamples);
    }
    
    /// <summary>
    /// Resets decoder state for seeking.
    /// </summary>
    public void Reset()
    {
        for (int ch = 0; ch < identHeader.ChannelCount; ch++)
        {
            Array.Clear(savedBuffers[ch]);
            channelStates[ch].Reset();
        }
        
        previousWindow = -1;
        firstFrame = true;
    }
}
using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// Temporal Noise Shaping (TNS) decoder for AAC.
/// TNS uses LPC filtering in the frequency domain to shape quantization noise.
/// ISO/IEC 14496-3 section 4.6.9
/// </summary>
public static class AacTnsDecoder
{
    /// <summary>
    /// Debug flag: when true, log TNS filter parameters.
    /// </summary>
    public static bool DebugLogTns;
    
    /// <summary>
    /// Debug frame range for TNS logging. Only log if DebugLogTns is true AND current frame is in range.
    /// Set to -1 to log all frames.
    /// </summary>
    public static int DebugLogTnsFrameStart = -1;
    public static int DebugLogTnsFrameEnd = -1;
    private static int tnsCallCounter;
    
    /// <summary>
    /// Call this at the start of each frame to track frame number for TNS debug.
    /// </summary>
    public static void DebugSetFrame(int frame)
    {
        tnsCallCounter = 0;
        _currentFrame = frame;
    }
    private static int _currentFrame = -1;
    
    private static bool ShouldLog => DebugLogTns && 
        (DebugLogTnsFrameStart < 0 || (_currentFrame >= DebugLogTnsFrameStart && _currentFrame <= DebugLogTnsFrameEnd));
    
    /// <summary>
    /// Maximum number of TNS filters per window.
    /// </summary>
    public const int MaxFiltersPerWindow = 3;
    
    /// <summary>
    /// Maximum TNS filter order for long blocks.
    /// </summary>
    public const int MaxOrderLong = 20;
    
    /// <summary>
    /// Maximum TNS filter order for short blocks.
    /// </summary>
    public const int MaxOrderShort = 7;
    
    /// <summary>
    /// Decodes TNS data from the bitstream.
    /// </summary>
    /// <param name="reader">Bit reader positioned at TNS data.</param>
    /// <param name="icsInfo">ICS info for window configuration.</param>
    /// <param name="tnsData">Output TNS data structure.</param>
    public static void DecodeTnsData(ref AacBitReader reader, in IcsInfo icsInfo, out TnsData tnsData)
    {
        tnsData = new TnsData();
        
        bool isShortBlock = icsInfo.WindowSequence == WindowSequence.EightShortSequence;
        int numWindows = isShortBlock ? 8 : 1;
        int filterCountBits = isShortBlock ? 1 : 2;
        int lengthBits = isShortBlock ? 4 : 6;
        int orderBits = isShortBlock ? 3 : 5;
        
        tnsData.WindowCount = numWindows;
        
        for (int window = 0; window < numWindows; window++)
        {
            int filterCount = reader.ReadBits(filterCountBits);
            tnsData.FilterCounts[window] = filterCount;
            
            if (filterCount > 0)
            {
                bool coefficientResolution = reader.ReadBit();
                tnsData.CoefficientResolutions[window] = coefficientResolution;
                int coefficientBits = coefficientResolution ? 4 : 3;
                
                for (int filter = 0; filter < filterCount; filter++)
                {
                    int filterIndex = TnsData.GetFilterIndex(window, filter);
                    ref var filterData = ref tnsData.Filters[filterIndex];
                    
                    filterData.Length = reader.ReadBits(lengthBits);
                    filterData.Order = reader.ReadBits(orderBits);
                    
                    if (filterData.Order > 0)
                    {
                        filterData.Direction = reader.ReadBit();
                        bool coefficientCompress = reader.ReadBit();
                        filterData.CoefficientCompress = coefficientCompress;
                        
                        int actualBits = coefficientCompress ? coefficientBits - 1 : coefficientBits;
                        
                        for (int i = 0; i < filterData.Order; i++)
                        {
                            int codedCoefficient = reader.ReadBits(actualBits);
                            filterData.CodedCoefficients[i] = codedCoefficient;
                        }
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Applies TNS filtering to spectral coefficients.
    /// Port of FFmpeg's apply_tns() from aacdec_dsp_template.c.
    /// </summary>
    /// <param name="spectralCoefficients">Spectral coefficients to filter in-place.</param>
    /// <param name="tnsData">Decoded TNS data.</param>
    /// <param name="icsInfo">ICS info for frequency band mapping.</param>
    /// <param name="swbOffsets">Scalefactor band offsets.</param>
    /// <param name="numSwb">Total number of SWBs for this sampling rate and block type.</param>
    /// <param name="tnsMaxBands">Maximum TNS bands for this sampling rate and block type.</param>
    /// <param name="decode">
    /// True for the normal use of the tool, which runs the AR filter. False when the tool is
    /// being used by long term prediction, which runs the MA filter instead - a different filter,
    /// not the same one with a sign flipped. FFmpeg's apply_tns takes the same flag and its own
    /// comment reads "1 if tool is used normally, 0 if tool is used in LTP".
    /// </param>
    public static void ApplyTnsFilter(
        Span<float> spectralCoefficients,
        in TnsData tnsData,
        in IcsInfo icsInfo,
        ReadOnlySpan<int> swbOffsets,
        int numSwb,
        int tnsMaxBands,
        bool decode = true)
    {
        // FFmpeg: mmm = min(tns_max_bands, max_sfb)
        int mmm = Math.Min(tnsMaxBands, icsInfo.MaxScalefactorBand);
        if (mmm == 0)
            return;
        
        if (ShouldLog)
            Console.WriteLine($"    [TNS] f{_currentFrame} tnsMaxBands={tnsMaxBands}, maxSfb={icsInfo.MaxScalefactorBand}, mmm={mmm}, numSwb={numSwb}");
        
        bool isShortBlock = icsInfo.WindowSequence == WindowSequence.EightShortSequence;
        int windowLength = isShortBlock ? 128 : 1024;
        
        Span<float> lpcCoefficients = stackalloc float[MaxOrderLong];
        
        for (int window = 0; window < tnsData.WindowCount; window++)
        {
            int filterCount = tnsData.FilterCounts[window];
            if (filterCount == 0)
                continue;
            
            bool coefficientResolution = tnsData.CoefficientResolutions[window];
            int windowOffset = window * windowLength;
            
            // FFmpeg: bottom = ics->num_swb (total SWBs, not max_sfb)
            int bottomBand = numSwb;
            
            for (int filterIdx = 0; filterIdx < filterCount; filterIdx++)
            {
                int arrayIndex = TnsData.GetFilterIndex(window, filterIdx);
                ref readonly var filter = ref tnsData.Filters[arrayIndex];
                
                int topBand = bottomBand;
                bottomBand = Math.Max(0, topBand - filter.Length);
                
                if (filter.Order == 0)
                    continue;
                
                // FFmpeg: start = swb_offset[min(bottom, mmm)], end = swb_offset[min(top, mmm)]
                int startPosition = GetSwbOffset(swbOffsets, Math.Min(bottomBand, mmm), windowLength);
                int endPosition = GetSwbOffset(swbOffsets, Math.Min(topBand, mmm), windowLength);
                
                int size = endPosition - startPosition;
                if (size <= 0)
                    continue;
                
                if (ShouldLog)
                {
                    Console.WriteLine($"    [TNS] filter {filterIdx}: length={filter.Length}, order={filter.Order}, " +
                        $"dir={filter.Direction}, compress={filter.CoefficientCompress}");
                    Console.WriteLine($"    [TNS]   top={topBand}, bottom={bottomBand}, " +
                        $"start=swb[min({bottomBand},{mmm})]={startPosition}, end=swb[min({topBand},{mmm})]={endPosition}, size={size}");
                    Console.Write("    [TNS]   coded coeffs: ");
                    for (int c = 0; c < filter.Order; c++) Console.Write($"{filter.CodedCoefficients[c]} ");
                    Console.WriteLine();
                }
                
                lpcCoefficients.Clear();
                ConvertToLpcCoefficients(
                    filter.CodedCoefficients,
                    filter.Order,
                    coefficientResolution,
                    filter.CoefficientCompress,
                    lpcCoefficients);
                
                if (ShouldLog)
                {
                    Console.Write("    [TNS]   LPC coeffs: ");
                    for (int c = 0; c < filter.Order; c++) Console.Write($"{lpcCoefficients[c]:G8} ");
                    Console.WriteLine();
                    
                    // Show pre-filter spectral at filter boundaries
                    Console.Write($"    [TNS]   pre-filter spectral[{windowOffset + startPosition}..+5]: ");
                    for (int c = 0; c < Math.Min(5, size); c++)
                        Console.Write($"{spectralCoefficients[windowOffset + startPosition + c]:G8} ");
                    Console.WriteLine();
                    
                    // Show pre-filter spectral at SFB boundaries 20-25
                    for (int sfb = 20; sfb <= 25 && sfb < swbOffsets.Length - 1; sfb++)
                    {
                        int s0 = swbOffsets[sfb], s1 = swbOffsets[sfb + 1];
                        if (s0 >= startPosition && s0 < endPosition)
                        {
                            Console.Write($"    [TNS]   pre-filter SFB{sfb}[{s0}-{s1}]: ");
                            for (int c = s0; c < s1 && c < endPosition; c++)
                                Console.Write($"{spectralCoefficients[windowOffset + c]:G8} ");
                            Console.WriteLine();
                        }
                    }
                }
                
                if (decode)
                {
                    // Apply AR filter (decode mode) matching FFmpeg exactly
                    ApplyArFilter(
                        spectralCoefficients,
                        windowOffset + startPosition,
                        size,
                        lpcCoefficients,
                        filter.Order,
                        filter.Direction);
                }
                else
                {
                    ApplyMaFilter(
                        spectralCoefficients,
                        windowOffset + startPosition,
                        size,
                        lpcCoefficients,
                        filter.Order,
                        filter.Direction);
                }
                
                if (ShouldLog)
                {
                    // Show post-filter spectral at SFB boundaries 20-25
                    for (int sfb = 20; sfb <= 25 && sfb < swbOffsets.Length - 1; sfb++)
                    {
                        int s0 = swbOffsets[sfb], s1 = swbOffsets[sfb + 1];
                        if (s0 >= startPosition && s0 < endPosition)
                        {
                            Console.Write($"    [TNS]   post-filter SFB{sfb}[{s0}-{s1}]: ");
                            for (int c = s0; c < s1 && c < endPosition; c++)
                                Console.Write($"{spectralCoefficients[windowOffset + c]:G8} ");
                            Console.WriteLine();
                        }
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Converts coded TNS coefficients to LPC filter coefficients.
    /// Direct port of FFmpeg's compute_lpc_coefs() with normalize=0, lpc_stride=0.
    /// Note: FFmpeg negates reflection coefficients (r = -autoc[i]) and stores 0-indexed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertToLpcCoefficients(
        ReadOnlySpan<int> codedCoefficients,
        int order,
        bool coefficientResolution,
        bool coefficientCompress,
        Span<float> lpcCoefficients)
    {
        // FFmpeg: tmp2_idx = 2 * coef_compress + coef_res
        int tableIdx = (coefficientCompress ? 2 : 0) + (coefficientResolution ? 1 : 0);
        ReadOnlySpan<float> invQuantTable = GetTnsTable(tableIdx);
        
        // Port of FFmpeg's compute_lpc_coefs (lpc_functions.h) with normalize=0, stride=0
        // Key: r = -autoc[i] (negation!)
        for (int i = 0; i < order; i++)
        {
            float r = -invQuantTable[codedCoefficients[i]];
            lpcCoefficients[i] = r;
            
            for (int j = 0; j < (i + 1) >> 1; j++)
            {
                float f = lpcCoefficients[j];
                float b = lpcCoefficients[i - 1 - j];
                lpcCoefficients[j] = f + r * b;
                lpcCoefficients[i - 1 - j] = b + r * f;
            }
        }
    }
    
    /// <summary>
    /// Applies AR (all-pole) filter for TNS decode.
    /// Direct port of FFmpeg's decode-mode TNS loop from aacdec_dsp_template.c:202-206.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// The moving-average filter long term prediction runs the tool with: the C's
    /// <c>apply_tns(..., decode = 0)</c> branch.
    /// </summary>
    /// <remarks>
    /// The C keeps its history in a <c>tmp[TNS_MAX_ORDER+1]</c> it never initialises, which is
    /// safe there because the tap count is bounded by <c>m</c> - no entry is read before it has
    /// been shifted in. This uses an inline array rather than a stackalloc: a method containing
    /// one never reaches Tier1 or Dynamic PGO, and this one runs per filter per window.
    /// </remarks>
    private static void ApplyMaFilter(
        Span<float> coef,
        int startIndex,
        int size,
        ReadOnlySpan<float> lpc,
        int order,
        bool direction)
    {
        int inc;
        int start;

        if (direction)
        {
            inc = -1;
            start = startIndex + size - 1;
        }
        else
        {
            inc = 1;
            start = startIndex;
        }

        TnsHistory history = default;
        Span<float> tmp = history;

        for (int m = 0; m < size; m++, start += inc)
        {
            tmp[0] = coef[start];

            int taps = Math.Min(m, order);
            for (int i = 1; i <= taps; i++)
                coef[start] += tmp[i] * lpc[i - 1];

            for (int i = order; i > 0; i--)
                tmp[i] = tmp[i - 1];
        }
    }

    private static void ApplyArFilter(
        Span<float> coef,
        int startIndex,
        int size,
        ReadOnlySpan<float> lpc,
        int order,
        bool direction)
    {
        int inc;
        int start;
        
        if (direction)
        {
            inc = -1;
            start = startIndex + size - 1;
        }
        else
        {
            inc = 1;
            start = startIndex;
        }
        
        // FFmpeg: coef[start] -= coef[start - i*inc] * lpc[i - 1];
        // lpc is 0-indexed (from compute_lpc_coefs with stride=0)
        for (int m = 0; m < size; m++, start += inc)
        {
            int taps = Math.Min(m, order);
            for (int i = 1; i <= taps; i++)
            {
                coef[start] -= coef[start - i * inc] * lpc[i - 1];
            }
        }
    }
    
    /// <summary>
    /// Gets the SWB offset for a band, clamped to window length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSwbOffset(ReadOnlySpan<int> swbOffsets, int band, int windowLength)
    {
        if (band < 0)
            return 0;
        if (band >= swbOffsets.Length)
            return windowLength;
        return Math.Min(swbOffsets[band], windowLength);
    }
    
    /// <summary>
    /// Gets the TNS coefficient dequantization table.
    /// Port of FFmpeg's ff_tns_tmp2_map[4] from aactab.c.
    /// Index = compress * 2 + res.
    /// </summary>
    private static ReadOnlySpan<float> GetTnsTable(int tableIdx)
    {
        return tableIdx switch
        {
            0 => TnsMap_0_3,  // compress=0, res=0: 8 entries (3-bit values)
            1 => TnsMap_0_4,  // compress=0, res=1: 16 entries (4-bit values)
            2 => TnsMap_1_3,  // compress=1, res=0: 4 entries (2-bit values)
            3 => TnsMap_1_4,  // compress=1, res=1: 8 entries (3-bit values)
            _ => TnsMap_0_3
        };
    }
    
    // TNS coefficient tables from FFmpeg's aactab.c (tns_tmp2_map)
    // Named as tns_tmp2_map_{compress}_{res}
    
    /// <summary>compress=0, res=0 → 3-bit, 8 entries</summary>
    private static ReadOnlySpan<float> TnsMap_0_3 =>
    [
        0.00000000f, -0.43388373f, -0.78183150f, -0.97492790f,
        0.98480773f,  0.86602539f,  0.64278758f,  0.34202015f
    ];
    
    /// <summary>compress=0, res=1 → 4-bit, 16 entries</summary>
    private static ReadOnlySpan<float> TnsMap_0_4 =>
    [
         0.00000000f, -0.20791170f, -0.40673664f, -0.58778524f,
        -0.74314481f, -0.86602539f, -0.95105654f, -0.99452192f,
         0.99573416f,  0.96182561f,  0.89516330f,  0.79801720f,
         0.67369562f,  0.52643216f,  0.36124167f,  0.18374951f
    ];
    
    /// <summary>compress=1, res=0 → 2-bit, 4 entries</summary>
    private static ReadOnlySpan<float> TnsMap_1_3 =>
    [
        0.00000000f, -0.43388373f,  0.64278758f,  0.34202015f
    ];
    
    /// <summary>compress=1, res=1 → 3-bit, 8 entries</summary>
    private static ReadOnlySpan<float> TnsMap_1_4 =>
    [
        0.00000000f, -0.20791170f, -0.40673664f, -0.58778524f,
        0.67369562f,  0.52643216f,  0.36124167f,  0.18374951f
    ];
}

/// <summary>
/// TNS data structure for one channel.
/// </summary>
/// <summary>The MA filter's tap history, <c>tmp[TNS_MAX_ORDER + 1]</c>.</summary>
[InlineArray(AacTnsDecoder.MaxOrderLong + 1)]
internal struct TnsHistory
{
    private float element;
}

public struct TnsData
{
    /// <summary>
    /// Number of windows (1 for long, 8 for short blocks).
    /// </summary>
    public int WindowCount;
    
    /// <summary>
    /// Number of filters per window.
    /// </summary>
    public TnsFilterCountsArray FilterCounts;
    
    /// <summary>
    /// Coefficient resolution per window.
    /// </summary>
    public TnsCoefficientResolutionArray CoefficientResolutions;
    
    /// <summary>
    /// Filter data (up to 8 windows × 3 filters).
    /// </summary>
    public TnsFilterDataArray Filters;
    
    /// <summary>
    /// Gets the index into the filters array for a specific window and filter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetFilterIndex(int window, int filterIndex)
    {
        return window * AacTnsDecoder.MaxFiltersPerWindow + filterIndex;
    }
}

/// <summary>
/// TNS filter data for a single filter region.
/// </summary>
public struct TnsFilterData
{
    /// <summary>
    /// Filter length in scalefactor bands.
    /// </summary>
    public int Length;
    
    /// <summary>
    /// Filter order (number of LPC coefficients).
    /// </summary>
    public int Order;
    
    /// <summary>
    /// Filter direction: false = upward (low to high), true = downward (high to low).
    /// </summary>
    public bool Direction;
    
    /// <summary>
    /// Whether coefficient compression is used.
    /// </summary>
    public bool CoefficientCompress;
    
    /// <summary>
    /// Coded TNS coefficients (PARCOR/reflection coefficients).
    /// </summary>
    public TnsCodedCoefficientArray CodedCoefficients;
}

/// <summary>
/// Fixed-size array for TNS filter counts (8 windows max).
/// </summary>
[System.Runtime.CompilerServices.InlineArray(8)]
public struct TnsFilterCountsArray
{
    private int firstElement;
}

/// <summary>
/// Fixed-size array for TNS coefficient resolutions (8 windows max).
/// </summary>
[System.Runtime.CompilerServices.InlineArray(8)]
public struct TnsCoefficientResolutionArray
{
    private bool firstElement;
}

/// <summary>
/// Fixed-size array for TNS filter data (8 windows × 3 filters = 24 max).
/// </summary>
[System.Runtime.CompilerServices.InlineArray(24)]
public struct TnsFilterDataArray
{
    private TnsFilterData firstElement;
}

/// <summary>
/// Fixed-size array for coded TNS coefficients (max order = 20).
/// </summary>
[System.Runtime.CompilerServices.InlineArray(20)]
public struct TnsCodedCoefficientArray
{
    private int firstElement;
}

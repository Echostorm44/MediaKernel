using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Audio
{
    /// <summary>
    /// Channel downmixer for converting multi-channel audio to stereo.
    /// 
    /// Implements ITU-R BS.775 compliant downmixing for 5.1 surround to stereo.
    /// Uses SIMD acceleration where available.
    /// </summary>
    public sealed class ChannelDownmixer
    {
        // ITU-R BS.775 standard coefficients
        // L = FL + 0.707 * FC + 0.707 * BL
        // R = FR + 0.707 * FC + 0.707 * BR
        private const float CenterCoefficient = 0.707107f;      // 1/sqrt(2)
        private const float SurroundCoefficient = 0.707107f;    // 1/sqrt(2)
        private const float LfeCoefficient = 0.0f;              // LFE off by default
        
        // Adjustable parameters
        private float centerBoost = 1.0f;
        private float surroundLevel = 1.0f;
        private float lfeLevel = 0.0f;
        private bool lfeEnabled = false;

        /// <summary>
        /// Gets or sets the center channel boost factor (dialogue enhancement).
        /// Default is 1.0 (no boost). Range: 0.5 to 2.0.
        /// </summary>
        public float CenterBoost
        {
            get => centerBoost;
            set => centerBoost = Math.Clamp(value, 0.5f, 2.0f);
        }

        /// <summary>
        /// Gets or sets the surround channel level.
        /// Default is 1.0. Range: 0.0 to 1.5.
        /// </summary>
        public float SurroundLevel
        {
            get => surroundLevel;
            set => surroundLevel = Math.Clamp(value, 0.0f, 1.5f);
        }

        /// <summary>
        /// Gets or sets whether LFE is mixed into output.
        /// Default is false (suitable for headphones).
        /// </summary>
        public bool LfeEnabled
        {
            get => lfeEnabled;
            set => lfeEnabled = value;
        }

        /// <summary>
        /// Gets or sets the LFE mix level when enabled.
        /// Default is 0.5. Range: 0.0 to 1.0.
        /// </summary>
        public float LfeLevel
        {
            get => lfeLevel;
            set => lfeLevel = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Downmixes 5.1 surround to stereo.
        /// 
        /// Input channel order: FL, FR, FC, LFE, BL, BR (standard 5.1)
        /// Output: L, R (interleaved stereo)
        /// </summary>
        /// <param name="input">5.1 input (6 channels interleaved).</param>
        /// <param name="output">Stereo output (2 channels interleaved).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Downmix51ToStereo(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            if (Avx.IsSupported && frameCount >= 8)
            {
                Downmix51ToStereoAvx(input, output, frameCount);
            }
            else if (Sse.IsSupported && frameCount >= 4)
            {
                Downmix51ToStereoSse(input, output, frameCount);
            }
            else
            {
                Downmix51ToStereoScalar(input, output, frameCount);
            }
        }

        /// <summary>
        /// Scalar implementation of 5.1 to stereo downmix.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Downmix51ToStereoScalar(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            float centerCoeff = CenterCoefficient * centerBoost;
            float surroundCoeff = SurroundCoefficient * surroundLevel;
            float lfeCoeff = lfeEnabled ? LfeCoefficient + lfeLevel * 0.5f : 0.0f;
            
            for (int frame = 0; frame < frameCount; frame++)
            {
                int inIdx = frame * 6;
                int outIdx = frame * 2;
                
                float frontLeft = input[inIdx];
                float frontRight = input[inIdx + 1];
                float center = input[inIdx + 2];
                float lfe = input[inIdx + 3];
                float backLeft = input[inIdx + 4];
                float backRight = input[inIdx + 5];
                
                // ITU-R BS.775 formula
                float left = frontLeft + centerCoeff * center + surroundCoeff * backLeft + lfeCoeff * lfe;
                float right = frontRight + centerCoeff * center + surroundCoeff * backRight + lfeCoeff * lfe;
                
                output[outIdx] = left;
                output[outIdx + 1] = right;
            }
        }

        /// <summary>
        /// SSE implementation of 5.1 to stereo downmix.
        /// Processes 4 frames at a time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void Downmix51ToStereoSse(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            float centerCoeff = CenterCoefficient * centerBoost;
            float surroundCoeff = SurroundCoefficient * surroundLevel;
            float lfeCoeff = lfeEnabled ? lfeLevel * 0.5f : 0.0f;
            
            var centerVec = Vector128.Create(centerCoeff);
            var surroundVec = Vector128.Create(surroundCoeff);
            var lfeVec = Vector128.Create(lfeCoeff);
            
            int frame = 0;
            
            fixed (float* inPtr = input)
            fixed (float* outPtr = output)
            {
                // Process 4 frames at a time
                for (; frame <= frameCount - 4; frame += 4)
                {
                    // Load 4 frames of 5.1 audio (24 floats)
                    // Deinterleave channels
                    float* baseIn = inPtr + frame * 6;
                    
                    // Manual gather for each channel across 4 frames
                    var fl = Vector128.Create(baseIn[0], baseIn[6], baseIn[12], baseIn[18]);
                    var fr = Vector128.Create(baseIn[1], baseIn[7], baseIn[13], baseIn[19]);
                    var fc = Vector128.Create(baseIn[2], baseIn[8], baseIn[14], baseIn[20]);
                    var lfe = Vector128.Create(baseIn[3], baseIn[9], baseIn[15], baseIn[21]);
                    var bl = Vector128.Create(baseIn[4], baseIn[10], baseIn[16], baseIn[22]);
                    var br = Vector128.Create(baseIn[5], baseIn[11], baseIn[17], baseIn[23]);
                    
                    // L = FL + center * FC + surround * BL + lfe * LFE
                    var left = fl;
                    left = Sse.Add(left, Sse.Multiply(fc, centerVec));
                    left = Sse.Add(left, Sse.Multiply(bl, surroundVec));
                    left = Sse.Add(left, Sse.Multiply(lfe, lfeVec));
                    
                    // R = FR + center * FC + surround * BR + lfe * LFE
                    var right = fr;
                    right = Sse.Add(right, Sse.Multiply(fc, centerVec));
                    right = Sse.Add(right, Sse.Multiply(br, surroundVec));
                    right = Sse.Add(right, Sse.Multiply(lfe, lfeVec));
                    
                    // Interleave L and R for output
                    var lo = Sse.UnpackLow(left, right);  // L0 R0 L1 R1
                    var hi = Sse.UnpackHigh(left, right); // L2 R2 L3 R3
                    
                    Sse.Store(outPtr + frame * 2, lo);
                    Sse.Store(outPtr + frame * 2 + 4, hi);
                }
            }
            
            // Handle remaining frames with scalar
            for (; frame < frameCount; frame++)
            {
                int inIdx = frame * 6;
                int outIdx = frame * 2;
                
                float left = input[inIdx] + centerCoeff * input[inIdx + 2] + 
                            surroundCoeff * input[inIdx + 4] + lfeCoeff * input[inIdx + 3];
                float right = input[inIdx + 1] + centerCoeff * input[inIdx + 2] + 
                             surroundCoeff * input[inIdx + 5] + lfeCoeff * input[inIdx + 3];
                
                output[outIdx] = left;
                output[outIdx + 1] = right;
            }
        }

        /// <summary>
        /// AVX implementation of 5.1 to stereo downmix.
        /// Processes 8 frames at a time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void Downmix51ToStereoAvx(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            float centerCoeff = CenterCoefficient * centerBoost;
            float surroundCoeff = SurroundCoefficient * surroundLevel;
            float lfeCoeff = lfeEnabled ? lfeLevel * 0.5f : 0.0f;
            
            var centerVec = Vector256.Create(centerCoeff);
            var surroundVec = Vector256.Create(surroundCoeff);
            var lfeVec = Vector256.Create(lfeCoeff);
            
            int frame = 0;
            
            fixed (float* inPtr = input)
            fixed (float* outPtr = output)
            {
                // Process 8 frames at a time
                for (; frame <= frameCount - 8; frame += 8)
                {
                    float* baseIn = inPtr + frame * 6;
                    
                    // Manual gather for each channel across 8 frames
                    var fl = Vector256.Create(
                        baseIn[0], baseIn[6], baseIn[12], baseIn[18],
                        baseIn[24], baseIn[30], baseIn[36], baseIn[42]);
                    var fr = Vector256.Create(
                        baseIn[1], baseIn[7], baseIn[13], baseIn[19],
                        baseIn[25], baseIn[31], baseIn[37], baseIn[43]);
                    var fc = Vector256.Create(
                        baseIn[2], baseIn[8], baseIn[14], baseIn[20],
                        baseIn[26], baseIn[32], baseIn[38], baseIn[44]);
                    var lfe = Vector256.Create(
                        baseIn[3], baseIn[9], baseIn[15], baseIn[21],
                        baseIn[27], baseIn[33], baseIn[39], baseIn[45]);
                    var bl = Vector256.Create(
                        baseIn[4], baseIn[10], baseIn[16], baseIn[22],
                        baseIn[28], baseIn[34], baseIn[40], baseIn[46]);
                    var br = Vector256.Create(
                        baseIn[5], baseIn[11], baseIn[17], baseIn[23],
                        baseIn[29], baseIn[35], baseIn[41], baseIn[47]);
                    
                    // L = FL + center * FC + surround * BL + lfe * LFE
                    var left = fl;
                    left = Avx.Add(left, Avx.Multiply(fc, centerVec));
                    left = Avx.Add(left, Avx.Multiply(bl, surroundVec));
                    left = Avx.Add(left, Avx.Multiply(lfe, lfeVec));
                    
                    // R = FR + center * FC + surround * BR + lfe * LFE
                    var right = fr;
                    right = Avx.Add(right, Avx.Multiply(fc, centerVec));
                    right = Avx.Add(right, Avx.Multiply(br, surroundVec));
                    right = Avx.Add(right, Avx.Multiply(lfe, lfeVec));
                    
                    // Interleave L and R
                    var lo = Avx.UnpackLow(left, right);
                    var hi = Avx.UnpackHigh(left, right);
                    
                    // AVX unpack works on 128-bit lanes, need to permute
                    var perm0 = Avx.Permute2x128(lo, hi, 0x20); // Low halves
                    var perm1 = Avx.Permute2x128(lo, hi, 0x31); // High halves
                    
                    Avx.Store(outPtr + frame * 2, perm0);
                    Avx.Store(outPtr + frame * 2 + 8, perm1);
                }
            }
            
            // Handle remaining with SSE path
            if (frame < frameCount)
            {
                Downmix51ToStereoSse(
                    input.Slice(frame * 6), 
                    output.Slice(frame * 2), 
                    frameCount - frame);
            }
        }

        /// <summary>
        /// Downmixes 7.1 surround to stereo.
        /// 
        /// Input channel order: FL, FR, FC, LFE, BL, BR, SL, SR (standard 7.1)
        /// Output: L, R (interleaved stereo)
        /// </summary>
        public void Downmix71ToStereo(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            float centerCoeff = CenterCoefficient * centerBoost;
            float surroundCoeff = SurroundCoefficient * surroundLevel * 0.5f; // Split surround energy
            float lfeCoeff = lfeEnabled ? lfeLevel * 0.5f : 0.0f;
            
            for (int frame = 0; frame < frameCount; frame++)
            {
                int inIdx = frame * 8;
                int outIdx = frame * 2;
                
                float frontLeft = input[inIdx];
                float frontRight = input[inIdx + 1];
                float center = input[inIdx + 2];
                float lfe = input[inIdx + 3];
                float backLeft = input[inIdx + 4];
                float backRight = input[inIdx + 5];
                float sideLeft = input[inIdx + 6];
                float sideRight = input[inIdx + 7];
                
                // Extended downmix with side channels
                float left = frontLeft + centerCoeff * center + 
                            surroundCoeff * (backLeft + sideLeft) + lfeCoeff * lfe;
                float right = frontRight + centerCoeff * center + 
                             surroundCoeff * (backRight + sideRight) + lfeCoeff * lfe;
                
                output[outIdx] = left;
                output[outIdx + 1] = right;
            }
        }

        /// <summary>
        /// Upmixes mono to stereo (simple duplication).
        /// </summary>
        public static void UpmixMonoToStereo(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                output[i * 2] = input[i];
                output[i * 2 + 1] = input[i];
            }
        }

        /// <summary>
        /// Downmixes stereo to mono (average).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void DownmixStereoToMono(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            if (Sse.IsSupported && frameCount >= 4)
            {
                DownmixStereoToMonoSse(input, output, frameCount);
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    output[i] = (input[i * 2] + input[i * 2 + 1]) * 0.5f;
                }
            }
        }

        private static unsafe void DownmixStereoToMonoSse(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            var half = Vector128.Create(0.5f);
            int i = 0;
            
            fixed (float* inPtr = input)
            fixed (float* outPtr = output)
            {
                for (; i <= frameCount - 4; i += 4)
                {
                    // Load 4 stereo frames (8 floats)
                    var v0 = Sse.LoadVector128(inPtr + i * 2);     // L0 R0 L1 R1
                    var v1 = Sse.LoadVector128(inPtr + i * 2 + 4); // L2 R2 L3 R3
                    
                    // Add adjacent pairs and average
                    var sum = Sse3.IsSupported 
                        ? Sse3.HorizontalAdd(v0, v1)  // L0+R0 L1+R1 L2+R2 L3+R3
                        : HorizontalAddFallback(v0, v1);
                    var mono = Sse.Multiply(sum, half);
                    
                    Sse.Store(outPtr + i, mono);
                }
            }
            
            // Scalar remainder
            for (; i < frameCount; i++)
            {
                output[i] = (input[i * 2] + input[i * 2 + 1]) * 0.5f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> HorizontalAddFallback(Vector128<float> a, Vector128<float> b)
        {
            // Manual horizontal add: a[0]+a[1], a[2]+a[3], b[0]+b[1], b[2]+b[3]
            var shuf1 = Sse.Shuffle(a, a, 0b10_11_00_01);
            var sum1 = Sse.Add(a, shuf1);
            var shuf2 = Sse.Shuffle(b, b, 0b10_11_00_01);
            var sum2 = Sse.Add(b, shuf2);
            return Sse.Shuffle(sum1, sum2, 0b10_00_10_00);
        }
    }
    
    /// <summary>
    /// Dolby Surround decoder - decodes stereo Dolby Surround encoded audio to surround.
    /// Based on VLC's dolby.c - simple decoder for Dolby Surround encoded streams.
    /// </summary>
    public sealed class DolbySurroundDecoder
    {
        /// <summary>
        /// Decodes Dolby Surround stereo to 5.1 surround.
        /// Uses the formula: Center = L + R, Rear = L - R
        /// </summary>
        /// <param name="stereoInput">Stereo input (L, R interleaved).</param>
        /// <param name="surroundOutput">5.1 output (FL, FR, FC, LFE, BL, BR interleaved).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        public void DecodeToSurround51(ReadOnlySpan<float> stereoInput, Span<float> surroundOutput, int frameCount)
        {
            // VLC's Dolby Surround decoding:
            // Center = (Left + Right)
            // Left = Left - Center/2
            // Right = Right - Center/2
            // Rear = (Left - Right) / numRearChannels
            
            for (int i = 0; i < frameCount; i++)
            {
                float left = stereoInput[i * 2];
                float right = stereoInput[i * 2 + 1];
                
                // Extract center (sum of L+R)
                float center = left + right;
                
                // Calculate front L/R with center removed
                float frontLeft = left - center * 0.5f;
                float frontRight = right - center * 0.5f;
                
                // Calculate rear (difference of L-R, divided by 2 rear channels)
                float rear = (left - right) * 0.5f;
                
                // Output 5.1: FL, FR, FC, LFE, BL, BR
                int outIdx = i * 6;
                surroundOutput[outIdx] = frontLeft;      // FL
                surroundOutput[outIdx + 1] = frontRight; // FR
                surroundOutput[outIdx + 2] = center;     // FC
                surroundOutput[outIdx + 3] = 0f;         // LFE (not encoded in Dolby Surround)
                surroundOutput[outIdx + 4] = rear;       // BL (rear left)
                surroundOutput[outIdx + 5] = rear;       // BR (rear right - same as BL for Dolby Surround)
            }
        }
        
        /// <summary>
        /// Decodes Dolby Surround stereo to quad (4.0) surround.
        /// </summary>
        /// <param name="stereoInput">Stereo input (L, R interleaved).</param>
        /// <param name="quadOutput">Quad output (FL, FR, BL, BR interleaved).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        public void DecodeToQuad(ReadOnlySpan<float> stereoInput, Span<float> quadOutput, int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                float left = stereoInput[i * 2];
                float right = stereoInput[i * 2 + 1];
                
                // Rear = difference / 2 rear channels
                float rear = (left - right) * 0.5f;
                
                // Output quad: FL, FR, BL, BR
                int outIdx = i * 4;
                quadOutput[outIdx] = left;       // FL
                quadOutput[outIdx + 1] = right;  // FR
                quadOutput[outIdx + 2] = rear;   // BL
                quadOutput[outIdx + 3] = rear;   // BR
            }
        }
        
        /// <summary>
        /// Checks if audio might be Dolby Surround encoded by analyzing L-R difference.
        /// High L-R correlation with phase differences suggests surround encoding.
        /// </summary>
        /// <param name="stereoInput">Stereo input samples.</param>
        /// <param name="frameCount">Number of frames to analyze.</param>
        /// <returns>Probability (0-1) that audio contains Dolby Surround encoding.</returns>
        public float AnalyzeDolbyProbability(ReadOnlySpan<float> stereoInput, int frameCount)
        {
            if (frameCount < 100) return 0f;
            
            float sumSquared = 0f;
            float diffSquared = 0f;
            
            for (int i = 0; i < frameCount; i++)
            {
                float left = stereoInput[i * 2];
                float right = stereoInput[i * 2 + 1];
                
                float sum = left + right;
                float diff = left - right;
                
                sumSquared += sum * sum;
                diffSquared += diff * diff;
            }
            
            // Dolby Surround typically has significant L-R difference content
            // compared to sum. If diff/sum ratio is high, likely surround.
            float total = sumSquared + diffSquared;
            if (total < 0.0001f) return 0f; // No signal
            
            // Ratio of difference energy to total energy
            // If mostly difference, very likely surround encoded
            float diffRatio = diffSquared / total;
            
            // Normalize: 0.3+ indicates surround, 0.5+ is very likely
            return Math.Clamp((diffRatio - 0.2f) * 1.67f, 0f, 1f);
        }
    }
    
    /// <summary>
    /// Channel upmixer - converts audio to higher channel counts.
    /// </summary>
    public sealed class ChannelUpmixer
    {
        /// <summary>
        /// Upmixes mono to stereo (duplicate to both channels).
        /// </summary>
        public void MonoToStereo(ReadOnlySpan<float> monoInput, Span<float> stereoOutput, int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                float sample = monoInput[i];
                stereoOutput[i * 2] = sample;
                stereoOutput[i * 2 + 1] = sample;
            }
        }
        
        /// <summary>
        /// Upmixes stereo to 5.1 with virtual surround (simple matrix).
        /// Creates phantom center and ambient rear channels.
        /// </summary>
        public void StereoToSurround51(ReadOnlySpan<float> stereoInput, Span<float> surroundOutput, int frameCount)
        {
            const float centerGain = 0.707107f; // 1/sqrt(2)
            const float rearGain = 0.5f;
            
            for (int i = 0; i < frameCount; i++)
            {
                float left = stereoInput[i * 2];
                float right = stereoInput[i * 2 + 1];
                
                // Center = average of L+R
                float center = (left + right) * centerGain * 0.5f;
                
                // Rear = difference (ambient)
                float rearLeft = (left - right) * rearGain;
                float rearRight = (right - left) * rearGain;
                
                // Output 5.1: FL, FR, FC, LFE, BL, BR
                int outIdx = i * 6;
                surroundOutput[outIdx] = left;
                surroundOutput[outIdx + 1] = right;
                surroundOutput[outIdx + 2] = center;
                surroundOutput[outIdx + 3] = 0f; // LFE
                surroundOutput[outIdx + 4] = rearLeft;
                surroundOutput[outIdx + 5] = rearRight;
            }
        }
        
        /// <summary>
        /// Upmixes 5.1 to 7.1 (adds side channels).
        /// </summary>
        public void Surround51To71(ReadOnlySpan<float> input51, Span<float> output71, int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                int inIdx = i * 6;
                int outIdx = i * 8;
                
                float frontLeft = input51[inIdx];
                float frontRight = input51[inIdx + 1];
                float center = input51[inIdx + 2];
                float lfe = input51[inIdx + 3];
                float backLeft = input51[inIdx + 4];
                float backRight = input51[inIdx + 5];
                
                // Side channels = average of front and back
                float sideLeft = (frontLeft + backLeft) * 0.5f;
                float sideRight = (frontRight + backRight) * 0.5f;
                
                // Output 7.1: FL, FR, FC, LFE, BL, BR, SL, SR
                output71[outIdx] = frontLeft;
                output71[outIdx + 1] = frontRight;
                output71[outIdx + 2] = center;
                output71[outIdx + 3] = lfe;
                output71[outIdx + 4] = backLeft;
                output71[outIdx + 5] = backRight;
                output71[outIdx + 6] = sideLeft;
                output71[outIdx + 7] = sideRight;
            }
        }
    }
    
    /// <summary>
    /// Channel remapper - rearranges audio channels.
    /// </summary>
    public sealed class ChannelRemapper
    {
        private int[] channelMap;
        private int sourceChannelCount;
        private int destinationChannelCount;
        
        /// <summary>
        /// Sets up the channel remapping.
        /// </summary>
        /// <param name="sourceChannels">Number of source channels.</param>
        /// <param name="destinationChannels">Number of destination channels.</param>
        /// <param name="mapping">Array where index is destination channel and value is source channel (-1 for silence).</param>
        public void SetMapping(int sourceChannels, int destinationChannels, int[] mapping)
        {
            if (mapping.Length != destinationChannels)
                throw new ArgumentException("Mapping length must match destination channel count");
            
            sourceChannelCount = sourceChannels;
            destinationChannelCount = destinationChannels;
            channelMap = (int[])mapping.Clone();
        }
        
        /// <summary>
        /// Creates a mapping for standard channel layouts.
        /// </summary>
        public static int[] CreateStandardMapping(ChannelLayout source, ChannelLayout destination)
        {
            // Standard mappings for common conversions
            return (source, destination) switch
            {
                // 5.1 channel order variations
                (ChannelLayout.FivePointOne_SMPTE, ChannelLayout.FivePointOne_Film) =>
                    new[] { 0, 1, 4, 5, 2, 3 }, // FL,FR,FC,LFE,BL,BR -> FL,FR,BL,BR,FC,LFE
                    
                (ChannelLayout.FivePointOne_Film, ChannelLayout.FivePointOne_SMPTE) =>
                    new[] { 0, 1, 4, 5, 2, 3 }, // FL,FR,BL,BR,FC,LFE -> FL,FR,FC,LFE,BL,BR
                
                _ => throw new NotSupportedException($"No standard mapping from {source} to {destination}")
            };
        }
        
        /// <summary>
        /// Remaps audio channels according to the configured mapping.
        /// </summary>
        public void Remap(ReadOnlySpan<float> input, Span<float> output, int frameCount)
        {
            if (channelMap == null)
                throw new InvalidOperationException("Mapping not configured");
            
            for (int frame = 0; frame < frameCount; frame++)
            {
                int sourceBase = frame * sourceChannelCount;
                int destBase = frame * destinationChannelCount;
                
                for (int destCh = 0; destCh < destinationChannelCount; destCh++)
                {
                    int sourceCh = channelMap[destCh];
                    output[destBase + destCh] = sourceCh >= 0 && sourceCh < sourceChannelCount
                        ? input[sourceBase + sourceCh]
                        : 0f;
                }
            }
        }
    }
    
    /// <summary>
    /// Standard channel layouts.
    /// </summary>
    public enum ChannelLayout
    {
        Mono,
        Stereo,
        Quad,
        FivePointOne_SMPTE,  // FL, FR, FC, LFE, BL, BR (standard SMPTE/ITU)
        FivePointOne_Film,    // FL, FR, BL, BR, FC, LFE (film order)
        SevenPointOne
    }
}
